using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using Newtonsoft.Json;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace NEDA.SecurityModules.AdvancedSecurity.Encryption;
{
    /// <summary>
    /// Key types managed by the KeyManager;
    /// </summary>
    public enum KeyType;
    {
        Unknown = 0,
        Symmetric = 1,      // AES, DES, etc.
        Asymmetric = 2,     // RSA, ECDSA, etc.
        Hashing = 3,        // HMAC, PBKDF2, etc.
        Certificate = 4,    // X.509 certificates;
        Derived = 5,        // Key derivation;
        Ephemeral = 6       // Temporary keys;
    }

    /// <summary>
    /// Key usage purposes;
    /// </summary>
    public enum KeyUsage;
    {
        Encryption = 0,
        Decryption = 1,
        Signing = 2,
        Verification = 3,
        KeyExchange = 4,
        KeyWrapping = 5,
        DataEncryption = 6,
        KeyEncryption = 7,
        All = 99;
    }

    /// <summary>
    /// Key storage locations;
    /// </summary>
    public enum KeyStorage;
    {
        InMemory = 0,
        Database = 1,
        HSM = 2,           // Hardware Security Module;
        AzureKeyVault = 3,
        AWSKMS = 4,
        GoogleKMS = 5,
        FileSystem = 6,
        Registry = 7;
    }

    /// <summary>
    /// Key rotation states;
    /// </summary>
    public enum KeyRotationState;
    {
        Active = 0,
        Previous = 1,
        Pending = 2,
        Expired = 3,
        Revoked = 4,
        Compromised = 5;
    }

    /// <summary>
    /// Main KeyManager interface;
    /// </summary>
    public interface IKeyManager : IDisposable
    {
        /// <summary>
        /// Generate new cryptographic key;
        /// </summary>
        Task<KeyGenerationResult> GenerateKeyAsync(KeyGenerationRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Import external key;
        /// </summary>
        Task<KeyImportResult> ImportKeyAsync(KeyImportRequest request);

        /// <summary>
        /// Export key in specified format;
        /// </summary>
        Task<KeyExportResult> ExportKeyAsync(Guid keyId, KeyExportOptions options);

        /// <summary>
        /// Get key by ID with optional metadata;
        /// </summary>
        Task<CryptographicKey> GetKeyAsync(Guid keyId, bool includePrivateMaterial = false);

        /// <summary>
        /// Rotate key (create new version and mark old as previous)
        /// </summary>
        Task<KeyRotationResult> RotateKeyAsync(Guid keyId, KeyRotationOptions options = null);

        /// <summary>
        /// Revoke key immediately;
        /// </summary>
        Task<bool> RevokeKeyAsync(Guid keyId, RevocationReason reason = RevocationReason.SecurityBreach);

        /// <summary>
        /// Get active key for specific algorithm and purpose;
        /// </summary>
        Task<CryptographicKey> GetActiveKeyAsync(string algorithm, KeyUsage usage = KeyUsage.All);

        /// <summary>
        /// Get key history with all versions;
        /// </summary>
        Task<KeyHistory> GetKeyHistoryAsync(Guid keyId);

        /// <summary>
        /// Perform key health check;
        /// </summary>
        Task<KeyHealthStatus> GetKeyHealthAsync(Guid keyId);

        /// <summary>
        /// Backup key to secure storage;
        /// </summary>
        Task<KeyBackupResult> BackupKeyAsync(Guid keyId, BackupOptions options);

        /// <summary>
        /// Restore key from backup;
        /// </summary>
        Task<KeyRestoreResult> RestoreKeyAsync(Guid keyId, RestoreOptions options);

        /// <summary>
        /// Encrypt data using specified key;
        /// </summary>
        Task<EncryptionResult> EncryptAsync(Guid keyId, byte[] plaintext, EncryptionOptions options = null);

        /// <summary>
        /// Decrypt data using specified key;
        /// </summary>
        Task<DecryptionResult> DecryptAsync(Guid keyId, byte[] ciphertext, DecryptionOptions options = null);

        /// <summary>
        /// Sign data using specified key;
        /// </summary>
        Task<SigningResult> SignAsync(Guid keyId, byte[] data, SigningOptions options = null);

        /// <summary>
        /// Verify signature using specified key;
        /// </summary>
        Task<VerificationResult> VerifyAsync(Guid keyId, byte[] data, byte[] signature, VerificationOptions options = null);

        /// <summary>
        /// Wrap (encrypt) another key;
        /// </summary>
        Task<KeyWrapResult> WrapKeyAsync(Guid wrappingKeyId, byte[] keyToWrap, KeyWrapOptions options = null);

        /// <summary>
        /// Unwrap (decrypt) a wrapped key;
        /// </summary>
        Task<KeyUnwrapResult> UnwrapKeyAsync(Guid wrappingKeyId, byte[] wrappedKey, KeyUnwrapOptions options = null);

        /// <summary>
        /// Get key statistics and metrics;
        /// </summary>
        Task<KeyStatistics> GetKeyStatisticsAsync(TimeSpan? timeframe = null);

        /// <summary>
        /// Perform key audit and compliance check;
        /// </summary>
        Task<KeyAuditResult> AuditKeysAsync(KeyAuditOptions options = null);

        /// <summary>
        /// Clean up expired and revoked keys;
        /// </summary>
        Task<KeyCleanupResult> CleanupKeysAsync(KeyCleanupOptions options = null);

        /// <summary>
        /// Get overall key management health status;
        /// </summary>
        Task<KeyManagerHealthStatus> GetHealthStatusAsync();
    }

    /// <summary>
    /// Main KeyManager implementation with comprehensive key lifecycle management;
    /// </summary>
    public class KeyManager : IKeyManager;
    {
        private readonly ILogger<KeyManager> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly IMemoryCache _memoryCache;
        private readonly KeyManagerConfig _config;
        private readonly IKeyRepository _repository;
        private readonly IKeyStorageProvider _storageProvider;
        private readonly IKeyProtector _keyProtector;
        private readonly Timer _rotationTimer;
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _keyOperationLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // Algorithm providers;
        private readonly Dictionary<string, IAlgorithmProvider> _algorithmProviders;

        // Key cache;
        private readonly MemoryCache<Guid, CryptographicKey> _keyCache;

        // Active keys cache (algorithm -> key)
        private readonly MemoryCache<string, Guid> _activeKeysCache;

        public KeyManager(
            ILogger<KeyManager> logger,
            IAuditLogger auditLogger,
            IMemoryCache memoryCache,
            IOptions<KeyManagerConfig> configOptions,
            IKeyRepository repository,
            IKeyStorageProvider storageProvider,
            IKeyProtector keyProtector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _config = configOptions?.Value ?? throw new ArgumentNullException(nameof(configOptions));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _keyProtector = keyProtector ?? throw new ArgumentNullException(nameof(keyProtector));

            _algorithmProviders = new Dictionary<string, IAlgorithmProvider>();
            _keyCache = new MemoryCache<Guid, CryptographicKey>(TimeSpan.FromMinutes(_config.KeyCacheTTLMinutes));
            _activeKeysCache = new MemoryCache<string, Guid>(TimeSpan.FromMinutes(5));

            InitializeAlgorithmProviders();
            InitializeKeyStorage();

            // Setup automatic key rotation;
            if (_config.EnableAutomaticRotation)
            {
                _rotationTimer = new Timer(
                    ExecuteScheduledRotationsAsync,
                    null,
                    TimeSpan.FromHours(_config.RotationCheckIntervalHours),
                    TimeSpan.FromHours(_config.RotationCheckIntervalHours));
            }

            // Setup key cleanup;
            if (_config.EnableAutomaticCleanup)
            {
                _cleanupTimer = new Timer(
                    ExecuteScheduledCleanupAsync,
                    null,
                    TimeSpan.FromHours(_config.CleanupIntervalHours),
                    TimeSpan.FromHours(_config.CleanupIntervalHours));
            }

            _logger.LogInformation("KeyManager initialized with {ProviderCount} algorithm providers",
                _algorithmProviders.Count);
        }

        private void InitializeAlgorithmProviders()
        {
            // Symmetric algorithm providers;
            _algorithmProviders["AES"] = new AesAlgorithmProvider(_logger, _config);
            _algorithmProviders["DES"] = new DesAlgorithmProvider(_logger, _config);
            _algorithmProviders["3DES"] = new TripleDesAlgorithmProvider(_logger, _config);
            _algorithmProviders["RC2"] = new Rc2AlgorithmProvider(_logger, _config);

            // Asymmetric algorithm providers;
            _algorithmProviders["RSA"] = new RsaAlgorithmProvider(_logger, _config);
            _algorithmProviders["ECDSA"] = new EcdsaAlgorithmProvider(_logger, _config);
            _algorithmProviders["DSA"] = new DsaAlgorithmProvider(_logger, _config);

            // Hashing algorithm providers;
            _algorithmProviders["HMACSHA256"] = new HmacSha256AlgorithmProvider(_logger, _config);
            _algorithmProviders["HMACSHA512"] = new HmacSha512AlgorithmProvider(_logger, _config);
            _algorithmProviders["PBKDF2"] = new Pbkdf2AlgorithmProvider(_logger, _config);

            // Load custom providers from configuration;
            foreach (var providerConfig in _config.CustomAlgorithmProviders)
            {
                try
                {
                    var providerType = Type.GetType(providerConfig.ProviderType);
                    if (providerType != null && typeof(IAlgorithmProvider).IsAssignableFrom(providerType))
                    {
                        var provider = Activator.CreateInstance(providerType,
                            _logger, _config, providerConfig.Parameters) as IAlgorithmProvider;

                        if (provider != null)
                        {
                            _algorithmProviders[providerConfig.AlgorithmName] = provider;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize algorithm provider: {ProviderType}",
                        providerConfig.ProviderType);
                }
            }
        }

        private void InitializeKeyStorage()
        {
            try
            {
                // Initialize storage provider based on configuration;
                _storageProvider.Initialize(_config.StorageSettings);

                // Test storage connection;
                var testResult = _storageProvider.TestConnectionAsync().GetAwaiter().GetResult();
                if (!testResult.Success)
                {
                    throw new KeyException($"Key storage initialization failed: {testResult.ErrorMessage}");
                }

                _logger.LogInformation("Key storage initialized: {StorageType}", _config.StorageSettings.StorageType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize key storage");
                throw new KeyException("Key storage initialization failed", ex);
            }
        }

        public async Task<KeyGenerationResult> GenerateKeyAsync(
            KeyGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _keyOperationLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Generating key: {Algorithm}, Size: {KeySize}, Type: {KeyType}",
                    request.Algorithm, request.KeySize, request.KeyType);

                // Validate request;
                var validationResult = ValidateKeyGenerationRequest(request);
                if (!validationResult.IsValid)
                {
                    throw new KeyValidationException($"Invalid key generation request: {string.Join(", ", validationResult.Errors)}");
                }

                // Get algorithm provider;
                if (!_algorithmProviders.TryGetValue(request.Algorithm.ToUpperInvariant(), out var provider))
                {
                    throw new KeyException($"Algorithm not supported: {request.Algorithm}");
                }

                // Generate key;
                var keyMaterial = await provider.GenerateKeyAsync(request, cancellationToken);

                // Create key metadata;
                var key = new CryptographicKey;
                {
                    Id = Guid.NewGuid(),
                    KeyId = GenerateKeyId(request),
                    Algorithm = request.Algorithm,
                    KeyType = request.KeyType,
                    KeySize = request.KeySize,
                    Usage = request.Usage,
                    Storage = request.Storage,
                    CreatedAt = DateTime.UtcNow,
                    ActivatedAt = DateTime.UtcNow,
                    ExpiresAt = request.ExpiresAt,
                    RotationState = KeyRotationState.Active,
                    Version = 1,
                    IsActive = true,
                    IsCompromised = false,
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    Tags = request.Tags ?? new List<string>()
                };

                // Protect key material;
                var protectedKey = await _keyProtector.ProtectAsync(keyMaterial, key.Id, request.ProtectionOptions);
                key.ProtectedKeyMaterial = protectedKey;

                // Store key;
                await _repository.StoreKeyAsync(key);

                // Store in secure storage;
                await _storageProvider.StoreKeyAsync(key.Id, keyMaterial, new StorageOptions;
                {
                    OverrideExisting = false,
                    Metadata = key.Metadata;
                });

                // Update active key cache;
                await UpdateActiveKeyCacheAsync(key);

                // Clear relevant caches;
                ClearKeyCache(key.Id);

                // Log key generation;
                await _auditLogger.LogKeyGeneratedAsync(new KeyAuditRecord;
                {
                    KeyId = key.Id,
                    Algorithm = key.Algorithm,
                    KeyType = key.KeyType,
                    KeySize = key.KeySize,
                    GeneratedAt = key.CreatedAt,
                    ExpiresAt = key.ExpiresAt;
                });

                _logger.LogInformation("Key generated successfully: {KeyId} ({Algorithm}-{KeySize})",
                    key.Id, key.Algorithm, key.KeySize);

                return new KeyGenerationResult;
                {
                    Success = true,
                    KeyId = key.Id,
                    Key = key,
                    GeneratedAt = key.CreatedAt;
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Key generation cancelled for algorithm: {Algorithm}", request.Algorithm);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate key: {Algorithm}", request.Algorithm);
                throw new KeyException("Key generation failed", ex);
            }
            finally
            {
                _keyOperationLock.Release();
            }
        }

        public async Task<KeyImportResult> ImportKeyAsync(KeyImportRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _keyOperationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Importing key: {Algorithm}, Format: {Format}",
                    request.Algorithm, request.Format);

                // Validate request;
                var validationResult = ValidateKeyImportRequest(request);
                if (!validationResult.IsValid)
                {
                    throw new KeyValidationException($"Invalid key import request: {string.Join(", ", validationResult.Errors)}");
                }

                // Parse key material based on format;
                byte[] keyMaterial;
                try
                {
                    keyMaterial = ParseKeyMaterial(request.KeyData, request.Format, request.Password);
                }
                catch (Exception ex)
                {
                    throw new KeyValidationException($"Failed to parse key material: {ex.Message}", ex);
                }

                // Verify key material;
                if (!await VerifyKeyMaterialAsync(keyMaterial, request.Algorithm, request.KeySize))
                {
                    throw new KeyValidationException("Key material verification failed");
                }

                // Create key metadata;
                var key = new CryptographicKey;
                {
                    Id = Guid.NewGuid(),
                    KeyId = GenerateKeyId(new KeyGenerationRequest;
                    {
                        Algorithm = request.Algorithm,
                        KeySize = request.KeySize,
                        KeyType = request.KeyType;
                    }),
                    Algorithm = request.Algorithm,
                    KeyType = request.KeyType,
                    KeySize = request.KeySize,
                    Usage = request.Usage,
                    Storage = request.Storage,
                    CreatedAt = DateTime.UtcNow,
                    ActivatedAt = DateTime.UtcNow,
                    ExpiresAt = request.ExpiresAt,
                    RotationState = KeyRotationState.Active,
                    Version = 1,
                    IsActive = true,
                    IsCompromised = false,
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    Tags = request.Tags ?? new List<string>(),
                    IsImported = true,
                    ImportedAt = DateTime.UtcNow,
                    ImportSource = request.Source;
                };

                // Protect key material;
                var protectedKey = await _keyProtector.ProtectAsync(keyMaterial, key.Id, request.ProtectionOptions);
                key.ProtectedKeyMaterial = protectedKey;

                // Store key;
                await _repository.StoreKeyAsync(key);

                // Store in secure storage;
                await _storageProvider.StoreKeyAsync(key.Id, keyMaterial, new StorageOptions;
                {
                    OverrideExisting = false,
                    Metadata = key.Metadata;
                });

                // Update active key cache;
                await UpdateActiveKeyCacheAsync(key);

                // Clear relevant caches;
                ClearKeyCache(key.Id);

                // Log key import;
                await _auditLogger.LogKeyImportedAsync(new KeyImportAuditRecord;
                {
                    KeyId = key.Id,
                    Algorithm = key.Algorithm,
                    KeyType = key.KeyType,
                    KeySize = key.KeySize,
                    ImportedAt = key.ImportedAt.Value,
                    Source = request.Source;
                });

                _logger.LogInformation("Key imported successfully: {KeyId} ({Algorithm}-{KeySize})",
                    key.Id, key.Algorithm, key.KeySize);

                return new KeyImportResult;
                {
                    Success = true,
                    KeyId = key.Id,
                    Key = key,
                    ImportedAt = key.ImportedAt.Value;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import key: {Algorithm}", request.Algorithm);
                throw new KeyException("Key import failed", ex);
            }
            finally
            {
                _keyOperationLock.Release();
            }
        }

        public async Task<KeyExportResult> ExportKeyAsync(Guid keyId, KeyExportOptions options)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            try
            {
                _logger.LogInformation("Exporting key: {KeyId}, Format: {Format}", keyId, options.Format);

                // Get key;
                var key = await GetKeyAsync(keyId, options.IncludePrivateMaterial);
                if (key == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                // Check export permissions;
                if (!CanExportKey(key, options))
                {
                    throw new KeyAccessException("Key export not permitted");
                }

                // Retrieve key material;
                var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                if (keyMaterial == null)
                {
                    throw new KeyException($"Key material not found for key: {keyId}");
                }

                // Format key material based on requested format;
                byte[] exportedData;
                string contentType;

                switch (options.Format)
                {
                    case KeyExportFormat.PEM:
                        exportedData = FormatAsPem(keyMaterial, key.Algorithm, options);
                        contentType = "application/x-pem-file";
                        break;

                    case KeyExportFormat.DER:
                        exportedData = FormatAsDer(keyMaterial, key.Algorithm, options);
                        contentType = "application/x-x509-ca-cert";
                        break;

                    case KeyExportFormat.XML:
                        exportedData = FormatAsXml(keyMaterial, key.Algorithm, options);
                        contentType = "application/xml";
                        break;

                    case KeyExportFormat.JSON:
                        exportedData = FormatAsJson(keyMaterial, key.Algorithm, options);
                        contentType = "application/json";
                        break;

                    case KeyExportFormat.Raw:
                        exportedData = keyMaterial;
                        contentType = "application/octet-stream";
                        break;

                    default:
                        throw new NotSupportedException($"Export format not supported: {options.Format}");
                }

                // Encrypt exported data if requested;
                if (options.EncryptExport)
                {
                    exportedData = await EncryptExportDataAsync(exportedData, options);
                }

                // Sign exported data if requested;
                if (options.SignExport)
                {
                    var signature = await SignExportDataAsync(exportedData, options);
                    // Append signature or include in metadata;
                }

                // Log key export;
                await _auditLogger.LogKeyExportedAsync(new KeyExportAuditRecord;
                {
                    KeyId = keyId,
                    Format = options.Format,
                    ExportedBy = options.ExportedBy,
                    ExportedAt = DateTime.UtcNow,
                    IncludePrivateMaterial = options.IncludePrivateMaterial;
                });

                _logger.LogInformation("Key exported successfully: {KeyId}, Size: {Size} bytes",
                    keyId, exportedData.Length);

                return new KeyExportResult;
                {
                    Success = true,
                    KeyId = keyId,
                    Format = options.Format,
                    Data = exportedData,
                    ContentType = contentType,
                    FileName = GenerateExportFileName(key, options.Format),
                    ExportedAt = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export key: {KeyId}", keyId);
                throw new KeyException("Key export failed", ex);
            }
        }

        public async Task<CryptographicKey> GetKeyAsync(Guid keyId, bool includePrivateMaterial = false)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            try
            {
                // Try cache first;
                var cacheKey = $"key_{keyId}_private{includePrivateMaterial}";
                if (_keyCache.TryGetValue(keyId, out var cachedKey) && cachedKey != null)
                {
                    if (!includePrivateMaterial || (includePrivateMaterial && cachedKey.HasPrivateMaterial))
                    {
                        _logger.LogDebug("Cache hit for key: {KeyId}", keyId);
                        return cachedKey;
                    }
                }

                _logger.LogDebug("Cache miss for key: {KeyId}, fetching from repository", keyId);

                // Get key metadata from repository;
                var key = await _repository.GetKeyAsync(keyId);
                if (key == null)
                {
                    return null;
                }

                // Retrieve key material if needed;
                if (includePrivateMaterial && !key.HasPrivateMaterial)
                {
                    try
                    {
                        var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                        if (keyMaterial != null)
                        {
                            // Note: In production, you would decrypt/unprotect the key material here;
                            key.RawKeyMaterial = keyMaterial;
                            key.HasPrivateMaterial = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to retrieve key material for key: {KeyId}", keyId);
                    }
                }

                // Cache the key (without private material for security)
                var keyToCache = key.Clone();
                if (!includePrivateMaterial)
                {
                    keyToCache.RawKeyMaterial = null;
                    keyToCache.HasPrivateMaterial = false;
                }

                _keyCache.Set(keyId, keyToCache, TimeSpan.FromMinutes(_config.KeyCacheTTLMinutes));

                return key;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get key: {KeyId}", keyId);
                throw new KeyException("Failed to retrieve key", ex);
            }
        }

        public async Task<KeyRotationResult> RotateKeyAsync(Guid keyId, KeyRotationOptions options = null)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            options ??= new KeyRotationOptions();

            await using var transaction = await _repository.BeginTransactionAsync();
            try
            {
                _logger.LogInformation("Rotating key: {KeyId}", keyId);

                // Get current key;
                var currentKey = await _repository.GetKeyAsync(keyId);
                if (currentKey == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                // Check if key can be rotated;
                if (!CanRotateKey(currentKey, options))
                {
                    throw new KeyOperationException($"Key cannot be rotated: {keyId}");
                }

                var result = new KeyRotationResult;
                {
                    RotationId = Guid.NewGuid(),
                    KeyId = keyId,
                    StartedAt = DateTime.UtcNow;
                };

                // Generate new key version;
                var newKeyRequest = new KeyGenerationRequest;
                {
                    Algorithm = currentKey.Algorithm,
                    KeySize = currentKey.KeySize,
                    KeyType = currentKey.KeyType,
                    Usage = currentKey.Usage,
                    Storage = currentKey.Storage,
                    ExpiresAt = options.NewKeyExpiration ?? DateTime.UtcNow.Add(_config.DefaultKeyLifetime),
                    Metadata = options.Metadata ?? new Dictionary<string, object>(),
                    Tags = currentKey.Tags,
                    ProtectionOptions = options.ProtectionOptions;
                };

                var newKeyResult = await GenerateKeyAsync(newKeyRequest);
                result.NewKeyId = newKeyResult.KeyId;
                result.NewKeyVersion = newKeyResult.Key.Version;

                // Update current key state;
                currentKey.RotationState = options.KeepPreviousActive ? KeyRotationState.Previous : KeyRotationState.Expired;
                currentKey.DeactivatedAt = DateTime.UtcNow;
                currentKey.DeactivationReason = "Key rotation";
                currentKey.SucceededByKeyId = newKeyResult.KeyId;

                await _repository.UpdateKeyAsync(currentKey);

                // Update new key metadata;
                var newKey = newKeyResult.Key;
                newKey.PrecededByKeyId = currentKey.Id;
                newKey.RotationState = KeyRotationState.Active;
                newKey.ActivatedAt = DateTime.UtcNow;

                await _repository.UpdateKeyAsync(newKey);

                // Perform key re-encryption if requested;
                if (options.ReEncryptData)
                {
                    result.ReEncryptionResult = await ReEncryptDataWithNewKeyAsync(currentKey, newKey, options);
                }

                // Update key dependencies;
                await UpdateKeyDependenciesAsync(currentKey.Id, newKey.Id);

                await transaction.CommitAsync();

                // Clear caches;
                ClearKeyCache(currentKey.Id);
                ClearKeyCache(newKey.Id);
                _activeKeysCache.Clear();

                // Log key rotation;
                await _auditLogger.LogKeyRotatedAsync(new KeyRotationAuditRecord;
                {
                    RotationId = result.RotationId,
                    OldKeyId = currentKey.Id,
                    NewKeyId = newKey.Id,
                    Algorithm = currentKey.Algorithm,
                    RotatedAt = DateTime.UtcNow,
                    Reason = options.Reason;
                });

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;
                result.Success = true;

                _logger.LogInformation("Key rotated successfully: {OldKeyId} -> {NewKeyId}",
                    currentKey.Id, newKey.Id);

                return result;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to rotate key: {KeyId}", keyId);
                throw new KeyException("Key rotation failed", ex);
            }
        }

        public async Task<bool> RevokeKeyAsync(Guid keyId, RevocationReason reason = RevocationReason.SecurityBreach)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            await _keyOperationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Revoking key: {KeyId}, Reason: {Reason}", keyId, reason);

                // Get key;
                var key = await _repository.GetKeyAsync(keyId);
                if (key == null)
                {
                    _logger.LogWarning("Key not found for revocation: {KeyId}", keyId);
                    return false;
                }

                // Update key state;
                key.IsActive = false;
                key.IsCompromised = reason == RevocationReason.SecurityBreach || reason == RevocationReason.Compromised;
                key.RevokedAt = DateTime.UtcNow;
                key.RevocationReason = reason;
                key.RotationState = KeyRotationState.Revoked;

                await _repository.UpdateKeyAsync(key);

                // Destroy key material if configured;
                if (_config.DestroyRevokedKeys)
                {
                    await _storageProvider.DeleteKeyAsync(keyId);
                    _logger.LogDebug("Key material destroyed for revoked key: {KeyId}", keyId);
                }

                // Clear caches;
                ClearKeyCache(keyId);
                _activeKeysCache.Clear();

                // Log key revocation;
                await _auditLogger.LogKeyRevokedAsync(new KeyRevocationAuditRecord;
                {
                    KeyId = keyId,
                    Algorithm = key.Algorithm,
                    RevokedAt = key.RevokedAt.Value,
                    Reason = reason,
                    Destroyed = _config.DestroyRevokedKeys;
                });

                _logger.LogInformation("Key revoked: {KeyId} ({Algorithm})", keyId, key.Algorithm);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to revoke key: {KeyId}", keyId);
                throw new KeyException("Key revocation failed", ex);
            }
            finally
            {
                _keyOperationLock.Release();
            }
        }

        public async Task<CryptographicKey> GetActiveKeyAsync(string algorithm, KeyUsage usage = KeyUsage.All)
        {
            if (string.IsNullOrWhiteSpace(algorithm))
                throw new ArgumentException("Algorithm cannot be empty", nameof(algorithm));

            try
            {
                // Try cache first;
                var cacheKey = $"active_key_{algorithm}_{usage}";
                if (_activeKeysCache.TryGetValue(cacheKey, out var cachedKeyId) && cachedKeyId != Guid.Empty)
                {
                    var cachedKey = await GetKeyAsync(cachedKeyId);
                    if (cachedKey != null && cachedKey.IsActive && !cachedKey.IsCompromised)
                    {
                        _logger.LogDebug("Cache hit for active key: {Algorithm} -> {KeyId}", algorithm, cachedKeyId);
                        return cachedKey;
                    }
                }

                _logger.LogDebug("Cache miss for active key: {Algorithm}, querying repository", algorithm);

                // Query repository for active key;
                var activeKey = await _repository.GetActiveKeyAsync(algorithm, usage);
                if (activeKey == null)
                {
                    // No active key found, check if we should auto-generate;
                    if (_config.AutoGenerateMissingKeys)
                    {
                        _logger.LogInformation("No active key found for {Algorithm}, auto-generating new key", algorithm);

                        var request = new KeyGenerationRequest;
                        {
                            Algorithm = algorithm,
                            KeySize = GetDefaultKeySize(algorithm),
                            KeyType = GetKeyTypeForAlgorithm(algorithm),
                            Usage = usage,
                            Storage = _config.DefaultStorage,
                            ExpiresAt = DateTime.UtcNow.Add(_config.DefaultKeyLifetime)
                        };

                        var result = await GenerateKeyAsync(request);
                        activeKey = result.Key;
                    }
                    else;
                    {
                        throw new KeyNotFoundException($"No active key found for algorithm: {algorithm}");
                    }
                }

                // Cache the active key;
                _activeKeysCache.Set(cacheKey, activeKey.Id, TimeSpan.FromMinutes(5));

                return activeKey;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get active key for algorithm: {Algorithm}", algorithm);
                throw new KeyException("Failed to get active key", ex);
            }
        }

        public async Task<KeyHistory> GetKeyHistoryAsync(Guid keyId)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            try
            {
                _logger.LogDebug("Getting key history for: {KeyId}", keyId);

                var history = new KeyHistory;
                {
                    KeyId = keyId,
                    GeneratedAt = DateTime.UtcNow;
                };

                // Get current key;
                var currentKey = await _repository.GetKeyAsync(keyId);
                if (currentKey == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                history.CurrentKey = currentKey;

                // Get previous versions;
                var previousKeys = await _repository.GetKeyVersionsAsync(keyId);
                history.PreviousVersions = previousKeys.ToList();

                // Get rotation history;
                history.Rotations = await _repository.GetKeyRotationsAsync(keyId);

                // Get usage history;
                history.UsageHistory = await _repository.GetKeyUsageHistoryAsync(keyId, TimeSpan.FromDays(90));

                // Calculate statistics;
                history.TotalRotations = history.Rotations.Count;
                history.FirstRotation = history.Rotations.OrderBy(r => r.RotatedAt).FirstOrDefault();
                history.LastRotation = history.Rotations.OrderByDescending(r => r.RotatedAt).FirstOrDefault();
                history.AverageRotationInterval = CalculateAverageRotationInterval(history.Rotations);

                _logger.LogDebug("Retrieved key history with {VersionCount} versions for key: {KeyId}",
                    history.PreviousVersions.Count, keyId);

                return history;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get key history for key: {KeyId}", keyId);
                throw new KeyException("Failed to get key history", ex);
            }
        }

        public async Task<KeyHealthStatus> GetKeyHealthAsync(Guid keyId)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            try
            {
                _logger.LogDebug("Checking key health for: {KeyId}", keyId);

                var status = new KeyHealthStatus;
                {
                    KeyId = keyId,
                    CheckedAt = DateTime.UtcNow;
                };

                // Get key;
                var key = await GetKeyAsync(keyId);
                if (key == null)
                {
                    status.Exists = false;
                    status.OverallHealth = KeyHealth.Missing;
                    status.Message = "Key not found";
                    return status;
                }

                status.Exists = true;
                status.Key = key;

                // Check expiration;
                if (key.ExpiresAt.HasValue)
                {
                    status.ExpiresAt = key.ExpiresAt;
                    status.DaysToExpiration = (key.ExpiresAt.Value - DateTime.UtcNow).Days;

                    if (DateTime.UtcNow > key.ExpiresAt.Value)
                    {
                        status.IsExpired = true;
                        status.ExpirationHealth = KeyHealth.Critical;
                    }
                    else if (status.DaysToExpiration <= 7)
                    {
                        status.ExpirationHealth = KeyHealth.Warning;
                    }
                    else;
                    {
                        status.ExpirationHealth = KeyHealth.Healthy;
                    }
                }

                // Check activation;
                if (key.ActivatedAt.HasValue)
                {
                    var daysActive = (DateTime.UtcNow - key.ActivatedAt.Value).Days;
                    status.DaysActive = daysActive;

                    if (daysActive > _config.MaxKeyAgeDays)
                    {
                        status.AgeHealth = KeyHealth.Warning;
                        status.Message = "Key has been active for too long";
                    }
                    else;
                    {
                        status.AgeHealth = KeyHealth.Healthy;
                    }
                }

                // Check if key is active;
                status.IsActive = key.IsActive;
                status.IsCompromised = key.IsCompromised;

                if (!key.IsActive)
                {
                    status.ActivationHealth = KeyHealth.Critical;
                    status.Message = "Key is not active";
                }
                else if (key.IsCompromised)
                {
                    status.ActivationHealth = KeyHealth.Critical;
                    status.Message = "Key is compromised";
                }
                else;
                {
                    status.ActivationHealth = KeyHealth.Healthy;
                }

                // Check key material;
                try
                {
                    var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                    status.KeyMaterialExists = keyMaterial != null;
                    status.KeyMaterialSize = keyMaterial?.Length ?? 0;

                    if (keyMaterial == null)
                    {
                        status.MaterialHealth = KeyHealth.Critical;
                        status.Message = "Key material not found in storage";
                    }
                    else;
                    {
                        // Validate key material format;
                        var isValid = await ValidateKeyMaterialFormatAsync(keyMaterial, key.Algorithm);
                        status.KeyMaterialValid = isValid;
                        status.MaterialHealth = isValid ? KeyHealth.Healthy : KeyHealth.Critical;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check key material for key: {KeyId}", keyId);
                    status.MaterialHealth = KeyHealth.Error;
                    status.KeyMaterialError = ex.Message;
                }

                // Check usage statistics;
                var usageStats = await _repository.GetKeyUsageStatisticsAsync(keyId, TimeSpan.FromDays(30));
                status.UsageCount = usageStats.TotalUsage;
                status.LastUsedAt = usageStats.LastUsed;

                if (usageStats.TotalUsage == 0 && status.DaysActive > 7)
                {
                    status.UsageHealth = KeyHealth.Warning;
                    status.Message = "Key has not been used since creation";
                }
                else;
                {
                    status.UsageHealth = KeyHealth.Healthy;
                }

                // Calculate overall health;
                status.OverallHealth = CalculateOverallKeyHealth(status);

                // Generate recommendations;
                status.Recommendations = GenerateKeyHealthRecommendations(status);

                _logger.LogDebug("Key health check completed: {KeyId} -> {Health}", keyId, status.OverallHealth);

                return status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check key health for key: {KeyId}", keyId);
                throw new KeyException("Key health check failed", ex);
            }
        }

        public async Task<KeyBackupResult> BackupKeyAsync(Guid keyId, BackupOptions options)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            try
            {
                _logger.LogInformation("Backing up key: {KeyId} to {BackupLocation}", keyId, options.BackupLocation);

                // Get key;
                var key = await GetKeyAsync(keyId, includePrivateMaterial: true);
                if (key == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                var result = new KeyBackupResult;
                {
                    BackupId = Guid.NewGuid(),
                    KeyId = keyId,
                    StartedAt = DateTime.UtcNow;
                };

                // Prepare backup data;
                var backupData = new KeyBackupData;
                {
                    KeyId = keyId,
                    KeyMetadata = key,
                    BackupTimestamp = DateTime.UtcNow,
                    BackupVersion = 1,
                    BackupPurpose = options.Purpose;
                };

                // Retrieve key material;
                var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                if (keyMaterial == null)
                {
                    throw new KeyException($"Key material not found for key: {keyId}");
                }

                backupData.KeyMaterial = keyMaterial;

                // Encrypt backup if requested;
                if (options.EncryptBackup)
                {
                    backupData = await EncryptBackupDataAsync(backupData, options);
                    result.IsEncrypted = true;
                }

                // Sign backup if requested;
                if (options.SignBackup)
                {
                    var signature = await SignBackupDataAsync(backupData, options);
                    backupData.Signature = signature;
                    result.IsSigned = true;
                }

                // Store backup;
                var backupPath = await _storageProvider.StoreBackupAsync(backupData, options);
                result.BackupPath = backupPath;
                result.BackupSize = backupData.GetTotalSize();

                // Update key metadata;
                key.LastBackupAt = DateTime.UtcNow;
                key.BackupCount = (key.BackupCount ?? 0) + 1;
                await _repository.UpdateKeyAsync(key);

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;
                result.Success = true;

                // Log backup;
                await _auditLogger.LogKeyBackupAsync(new KeyBackupAuditRecord;
                {
                    BackupId = result.BackupId,
                    KeyId = keyId,
                    BackupLocation = options.BackupLocation,
                    BackupSize = result.BackupSize,
                    IsEncrypted = result.IsEncrypted,
                    IsSigned = result.IsSigned,
                    BackedUpAt = DateTime.UtcNow;
                });

                _logger.LogInformation("Key backup completed: {KeyId} -> {BackupPath}, Size: {Size} bytes",
                    keyId, backupPath, result.BackupSize);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backup key: {KeyId}", keyId);
                throw new KeyException("Key backup failed", ex);
            }
        }

        public async Task<KeyRestoreResult> RestoreKeyAsync(Guid keyId, RestoreOptions options)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            await _keyOperationLock.WaitAsync();
            try
            {
                _logger.LogInformation("Restoring key: {KeyId} from {BackupPath}", keyId, options.BackupPath);

                var result = new KeyRestoreResult;
                {
                    RestoreId = Guid.NewGuid(),
                    KeyId = keyId,
                    StartedAt = DateTime.UtcNow;
                };

                // Load backup data;
                var backupData = await _storageProvider.RetrieveBackupAsync(options.BackupPath, options);
                if (backupData == null)
                {
                    throw new KeyException($"Backup not found: {options.BackupPath}");
                }

                // Verify backup integrity;
                if (backupData.Signature != null)
                {
                    var isValid = await VerifyBackupSignatureAsync(backupData, options);
                    if (!isValid)
                    {
                        throw new KeyValidationException("Backup signature verification failed");
                    }
                    result.SignatureVerified = true;
                }

                // Decrypt backup if encrypted;
                if (backupData.IsEncrypted)
                {
                    backupData = await DecryptBackupDataAsync(backupData, options);
                    result.WasEncrypted = true;
                }

                // Verify backup data;
                if (backupData.KeyId != keyId)
                {
                    throw new KeyValidationException($"Backup key ID mismatch: expected {keyId}, got {backupData.KeyId}");
                }

                // Check if key already exists;
                var existingKey = await _repository.GetKeyAsync(keyId);
                if (existingKey != null && !options.ForceOverwrite)
                {
                    throw new KeyConflictException($"Key already exists: {keyId}. Use ForceOverwrite to replace.");
                }

                // Restore key metadata;
                var restoredKey = backupData.KeyMetadata;
                restoredKey.RestoredAt = DateTime.UtcNow;
                restoredKey.RestoredFromBackup = options.BackupPath;
                restoredKey.RestoreReason = options.Reason;

                if (existingKey != null)
                {
                    // Update existing key;
                    restoredKey.Id = existingKey.Id;
                    restoredKey.Version = existingKey.Version + 1;
                    await _repository.UpdateKeyAsync(restoredKey);
                    result.ExistingKeyReplaced = true;
                }
                else;
                {
                    // Create new key;
                    await _repository.StoreKeyAsync(restoredKey);
                    result.NewKeyCreated = true;
                }

                // Restore key material;
                await _storageProvider.StoreKeyAsync(keyId, backupData.KeyMaterial, new StorageOptions;
                {
                    OverrideExisting = true,
                    Metadata = restoredKey.Metadata;
                });

                // Clear caches;
                ClearKeyCache(keyId);
                _activeKeysCache.Clear();

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;
                result.Success = true;
                result.RestoredKey = restoredKey;

                // Log restore;
                await _auditLogger.LogKeyRestoreAsync(new KeyRestoreAuditRecord;
                {
                    RestoreId = result.RestoreId,
                    KeyId = keyId,
                    BackupPath = options.BackupPath,
                    RestoredAt = DateTime.UtcNow,
                    WasEncrypted = result.WasEncrypted,
                    SignatureVerified = result.SignatureVerified;
                });

                _logger.LogInformation("Key restore completed: {KeyId} from {BackupPath}", keyId, options.BackupPath);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore key: {KeyId}", keyId);
                throw new KeyException("Key restore failed", ex);
            }
            finally
            {
                _keyOperationLock.Release();
            }
        }

        public async Task<EncryptionResult> EncryptAsync(Guid keyId, byte[] plaintext, EncryptionOptions options = null)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            if (plaintext == null || plaintext.Length == 0)
                throw new ArgumentException("Plaintext cannot be empty", nameof(plaintext));

            options ??= new EncryptionOptions();

            try
            {
                _logger.LogDebug("Encrypting data with key: {KeyId}, Size: {Size} bytes", keyId, plaintext.Length);

                // Get key;
                var key = await GetKeyAsync(keyId);
                if (key == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                // Check key usage;
                if (!CanUseKeyForOperation(key, KeyOperation.Encryption))
                {
                    throw new KeyUsageException($"Key cannot be used for encryption: {keyId}");
                }

                // Get algorithm provider;
                if (!_algorithmProviders.TryGetValue(key.Algorithm.ToUpperInvariant(), out var provider))
                {
                    throw new KeyException($"Algorithm not supported: {key.Algorithm}");
                }

                // Retrieve key material;
                var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                if (keyMaterial == null)
                {
                    throw new KeyException($"Key material not found for key: {keyId}");
                }

                // Perform encryption;
                var startTime = DateTime.UtcNow;
                var ciphertext = await provider.EncryptAsync(keyMaterial, plaintext, options);
                var duration = DateTime.UtcNow - startTime;

                // Log encryption operation;
                await _auditLogger.LogEncryptionOperationAsync(new EncryptionAuditRecord;
                {
                    KeyId = keyId,
                    Operation = "Encryption",
                    DataSize = plaintext.Length,
                    Timestamp = startTime,
                    Duration = duration;
                });

                // Update key usage statistics;
                await _repository.RecordKeyUsageAsync(keyId, KeyOperation.Encryption, plaintext.Length);

                _logger.LogDebug("Encryption completed: {KeyId}, Input: {InputSize}, Output: {OutputSize}, Duration: {Duration}ms",
                    keyId, plaintext.Length, ciphertext.Length, duration.TotalMilliseconds);

                return new EncryptionResult;
                {
                    Success = true,
                    KeyId = keyId,
                    Ciphertext = ciphertext,
                    Algorithm = key.Algorithm,
                    EncryptedAt = startTime,
                    Duration = duration;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Encryption failed for key: {KeyId}", keyId);
                throw new KeyException("Encryption failed", ex);
            }
        }

        public async Task<DecryptionResult> DecryptAsync(Guid keyId, byte[] ciphertext, DecryptionOptions options = null)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            if (ciphertext == null || ciphertext.Length == 0)
                throw new ArgumentException("Ciphertext cannot be empty", nameof(ciphertext));

            options ??= new DecryptionOptions();

            try
            {
                _logger.LogDebug("Decrypting data with key: {KeyId}, Size: {Size} bytes", keyId, ciphertext.Length);

                // Get key;
                var key = await GetKeyAsync(keyId, includePrivateMaterial: true);
                if (key == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                // Check key usage;
                if (!CanUseKeyForOperation(key, KeyOperation.Decryption))
                {
                    throw new KeyUsageException($"Key cannot be used for decryption: {keyId}");
                }

                // Get algorithm provider;
                if (!_algorithmProviders.TryGetValue(key.Algorithm.ToUpperInvariant(), out var provider))
                {
                    throw new KeyException($"Algorithm not supported: {key.Algorithm}");
                }

                // Retrieve key material;
                var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                if (keyMaterial == null)
                {
                    throw new KeyException($"Key material not found for key: {keyId}");
                }

                // Perform decryption;
                var startTime = DateTime.UtcNow;
                var plaintext = await provider.DecryptAsync(keyMaterial, ciphertext, options);
                var duration = DateTime.UtcNow - startTime;

                // Log decryption operation;
                await _auditLogger.LogDecryptionOperationAsync(new DecryptionAuditRecord;
                {
                    KeyId = keyId,
                    Operation = "Decryption",
                    DataSize = ciphertext.Length,
                    Timestamp = startTime,
                    Duration = duration;
                });

                // Update key usage statistics;
                await _repository.RecordKeyUsageAsync(keyId, KeyOperation.Decryption, ciphertext.Length);

                _logger.LogDebug("Decryption completed: {KeyId}, Input: {InputSize}, Output: {OutputSize}, Duration: {Duration}ms",
                    keyId, ciphertext.Length, plaintext.Length, duration.TotalMilliseconds);

                return new DecryptionResult;
                {
                    Success = true,
                    KeyId = keyId,
                    Plaintext = plaintext,
                    Algorithm = key.Algorithm,
                    DecryptedAt = startTime,
                    Duration = duration;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Decryption failed for key: {KeyId}", keyId);
                throw new KeyException("Decryption failed", ex);
            }
        }

        public async Task<SigningResult> SignAsync(Guid keyId, byte[] data, SigningOptions options = null)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be empty", nameof(data));

            options ??= new SigningOptions();

            try
            {
                _logger.LogDebug("Signing data with key: {KeyId}, Size: {Size} bytes", keyId, data.Length);

                // Get key;
                var key = await GetKeyAsync(keyId, includePrivateMaterial: true);
                if (key == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                // Check key usage;
                if (!CanUseKeyForOperation(key, KeyOperation.Signing))
                {
                    throw new KeyUsageException($"Key cannot be used for signing: {keyId}");
                }

                // Get algorithm provider;
                if (!_algorithmProviders.TryGetValue(key.Algorithm.ToUpperInvariant(), out var provider))
                {
                    throw new KeyException($"Algorithm not supported: {key.Algorithm}");
                }

                // Retrieve key material;
                var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                if (keyMaterial == null)
                {
                    throw new KeyException($"Key material not found for key: {keyId}");
                }

                // Perform signing;
                var startTime = DateTime.UtcNow;
                var signature = await provider.SignAsync(keyMaterial, data, options);
                var duration = DateTime.UtcNow - startTime;

                // Log signing operation;
                await _auditLogger.LogSigningOperationAsync(new SigningAuditRecord;
                {
                    KeyId = keyId,
                    Operation = "Signing",
                    DataSize = data.Length,
                    Timestamp = startTime,
                    Duration = duration;
                });

                // Update key usage statistics;
                await _repository.RecordKeyUsageAsync(keyId, KeyOperation.Signing, data.Length);

                _logger.LogDebug("Signing completed: {KeyId}, Data: {DataSize}, Signature: {SignatureSize}, Duration: {Duration}ms",
                    keyId, data.Length, signature.Length, duration.TotalMilliseconds);

                return new SigningResult;
                {
                    Success = true,
                    KeyId = keyId,
                    Data = data,
                    Signature = signature,
                    Algorithm = key.Algorithm,
                    SignedAt = startTime,
                    Duration = duration;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Signing failed for key: {KeyId}", keyId);
                throw new KeyException("Signing failed", ex);
            }
        }

        public async Task<VerificationResult> VerifyAsync(Guid keyId, byte[] data, byte[] signature, VerificationOptions options = null)
        {
            if (keyId == Guid.Empty)
                throw new ArgumentException("Key ID cannot be empty", nameof(keyId));

            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be empty", nameof(data));

            if (signature == null || signature.Length == 0)
                throw new ArgumentException("Signature cannot be empty", nameof(signature));

            options ??= new VerificationOptions();

            try
            {
                _logger.LogDebug("Verifying signature with key: {KeyId}, Data: {DataSize}, Signature: {SignatureSize}",
                    keyId, data.Length, signature.Length);

                // Get key;
                var key = await GetKeyAsync(keyId);
                if (key == null)
                {
                    throw new KeyNotFoundException($"Key not found: {keyId}");
                }

                // Check key usage;
                if (!CanUseKeyForOperation(key, KeyOperation.Verification))
                {
                    throw new KeyUsageException($"Key cannot be used for verification: {keyId}");
                }

                // Get algorithm provider;
                if (!_algorithmProviders.TryGetValue(key.Algorithm.ToUpperInvariant(), out var provider))
                {
                    throw new KeyException($"Algorithm not supported: {key.Algorithm}");
                }

                // Retrieve key material;
                var keyMaterial = await _storageProvider.RetrieveKeyAsync(keyId);
                if (keyMaterial == null)
                {
                    throw new KeyException($"Key material not found for key: {keyId}");
                }

                // Perform verification;
                var startTime = DateTime.UtcNow;
                var isValid = await provider.VerifyAsync(keyMaterial, data, signature, options);
                var duration = DateTime.UtcNow - startTime;

                // Log verification operation;
                await _auditLogger.LogVerificationOperationAsync(new VerificationAuditRecord;
                {
                    KeyId = keyId,
                    Operation = "Verification",
                    DataSize = data.Length,
                    IsValid = isValid,
                    Timestamp = startTime,
                    Duration = duration;
                });

                // Update key usage statistics;
                await _repository.RecordKeyUsageAsync(keyId, KeyOperation.Verification, data.Length);

                _logger.LogDebug("Verification completed: {KeyId}, Valid: {IsValid}, Duration: {Duration}ms",
                    keyId, isValid, duration.TotalMilliseconds);

                return new VerificationResult;
                {
                    Success = true,
                    KeyId = keyId,
                    Data = data,
                    Signature = signature,
                    IsValid = isValid,
                    Algorithm = key.Algorithm,
                    VerifiedAt = startTime,
                    Duration = duration;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Verification failed for key: {KeyId}", keyId);
                throw new KeyException("Verification failed", ex);
            }
        }

        public async Task<KeyWrapResult> WrapKeyAsync(Guid wrappingKeyId, byte[] keyToWrap, KeyWrapOptions options = null)
        {
            if (wrappingKeyId == Guid.Empty)
                throw new ArgumentException("Wrapping key ID cannot be empty", nameof(wrappingKeyId));

            if (keyToWrap == null || keyToWrap.Length == 0)
                throw new ArgumentException("Key to wrap cannot be empty", nameof(keyToWrap));

            options ??= new KeyWrapOptions();

            try
            {
                _logger.LogDebug("Wrapping key with wrapping key: {WrappingKeyId}, Key size: {KeySize}",
                    wrappingKeyId, keyToWrap.Length);

                // Get wrapping key;
                var wrappingKey = await GetKeyAsync(wrappingKeyId);
                if (wrappingKey == null)
                {
                    throw new KeyNotFoundException($"Wrapping key not found: {wrappingKeyId}");
                }

                // Check key usage;
                if (!CanUseKeyForOperation(wrappingKey, KeyOperation.KeyWrapping))
                {
                    throw new KeyUsageException($"Key cannot be used for key wrapping: {wrappingKeyId}");
                }

                // Get algorithm provider;
                if (!_algorithmProviders.TryGetValue(wrappingKey.Algorithm.ToUpperInvariant(), out var provider))
                {
                    throw new KeyException($"Algorithm not supported: {wrappingKey.Algorithm}");
                }

                // Retrieve wrapping key material;
                var wrappingKeyMaterial = await _storageProvider.RetrieveKeyAsync(wrappingKeyId);
                if (wrappingKeyMaterial == null)
                {
                    throw new KeyException($"Key material not found for wrapping key: {wrappingKeyId}");
                }

                // Perform key wrapping;
                var startTime = DateTime.UtcNow;
                var wrappedKey = await provider.WrapKeyAsync(wrappingKeyMaterial, keyToWrap, options);
                var duration = DateTime.UtcNow - startTime;

                // Log key wrapping operation;
                await _auditLogger.LogKeyWrapOperationAsync(new KeyWrapAuditRecord;
                {
                    WrappingKeyId = wrappingKeyId,
                    Operation = "KeyWrapping",
                    KeySize = keyToWrap.Length,
                    WrappedKeySize = wrappedKey.Length,
                    Timestamp = startTime,
                    Duration = duration;
                });

                // Update key usage statistics;
                await _repository.RecordKeyUsageAsync(wrappingKeyId, KeyOperation.KeyWrapping, keyToWrap.Length);

                _logger.LogDebug("Key wrapping completed: {WrappingKeyId}, Input: {InputSize}, Output: {OutputSize}, Duration: {Duration}ms",
                    wrappingKeyId, keyToWrap.Length, wrappedKey.Length, duration.TotalMilliseconds);

                return new KeyWrapResult;
                {
                    Success = true,
                    WrappingKeyId = wrappingKeyId,
                    WrappedKey = wrappedKey,
                    Algorithm = wrappingKey.Algorithm,
                    WrappedAt = startTime,
                    Duration = duration;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key wrapping failed for wrapping key: {WrappingKeyId}", wrappingKeyId);
                throw new KeyException("Key wrapping failed", ex);
            }
        }

        public async Task<KeyUnwrapResult> UnwrapKeyAsync(Guid wrappingKeyId, byte[] wrappedKey, KeyUnwrapOptions options = null)
        {
            if (wrappingKeyId == Guid.Empty)
                throw new ArgumentException("Wrapping key ID cannot be empty", nameof(wrappingKeyId));

            if (wrappedKey == null || wrappedKey.Length == 0)
                throw new ArgumentException("Wrapped key cannot be empty", nameof(wrappedKey));

            options ??= new KeyUnwrapOptions();

            try
            {
                _logger.LogDebug("Unwrapping key with wrapping key: {WrappingKeyId}, Wrapped key size: {KeySize}",
                    wrappingKeyId, wrappedKey.Length);

                // Get wrapping key;
                var wrappingKey = await GetKeyAsync(wrappingKeyId, includePrivateMaterial: true);
                if (wrappingKey == null)
                {
                    throw new KeyNotFoundException($"Wrapping key not found: {wrappingKeyId}");
                }

                // Check key usage;
                if (!CanUseKeyForOperation(wrappingKey, KeyOperation.KeyUnwrapping))
                {
                    throw new KeyUsageException($"Key cannot be used for key unwrapping: {wrappingKeyId}");
                }

                // Get algorithm provider;
                if (!_algorithmProviders.TryGetValue(wrappingKey.Algorithm.ToUpperInvariant(), out var provider))
                {
                    throw new KeyException($"Algorithm not supported: {wrappingKey.Algorithm}");
                }

                // Retrieve wrapping key material;
                var wrappingKeyMaterial = await _storageProvider.RetrieveKeyAsync(wrappingKeyId);
                if (wrappingKeyMaterial == null)
                {
                    throw new KeyException($"Key material not found for wrapping key: {wrappingKeyId}");
                }

                // Perform key unwrapping;
                var startTime = DateTime.UtcNow;
                var unwrappedKey = await provider.UnwrapKeyAsync(wrappingKeyMaterial, wrappedKey, options);
                var duration = DateTime.UtcNow - startTime;

                // Log key unwrapping operation;
                await _auditLogger.LogKeyUnwrapOperationAsync(new KeyUnwrapAuditRecord;
                {
                    WrappingKeyId = wrappingKeyId,
                    Operation = "KeyUnwrapping",
                    WrappedKeySize = wrappedKey.Length,
                    UnwrappedKeySize = unwrappedKey.Length,
                    Timestamp = startTime,
                    Duration = duration;
                });

                // Update key usage statistics;
                await _repository.RecordKeyUsageAsync(wrappingKeyId, KeyOperation.KeyUnwrapping, wrappedKey.Length);

                _logger.LogDebug("Key unwrapping completed: {WrappingKeyId}, Input: {InputSize}, Output: {OutputSize}, Duration: {Duration}ms",
                    wrappingKeyId, wrappedKey.Length, unwrappedKey.Length, duration.TotalMilliseconds);

                return new KeyUnwrapResult;
                {
                    Success = true,
                    WrappingKeyId = wrappingKeyId,
                    UnwrappedKey = unwrappedKey,
                    Algorithm = wrappingKey.Algorithm,
                    UnwrappedAt = startTime,
                    Duration = duration;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key unwrapping failed for wrapping key: {WrappingKeyId}", wrappingKeyId);
                throw new KeyException("Key unwrapping failed", ex);
            }
        }

        public async Task<KeyStatistics> GetKeyStatisticsAsync(TimeSpan? timeframe = null)
        {
            try
            {
                var startTime = timeframe.HasValue;
                    ? DateTime.UtcNow.Subtract(timeframe.Value)
                    : DateTime.UtcNow.AddDays(-30); // Default 30 days;

                _logger.LogDebug("Getting key statistics from {StartTime}", startTime);

                var stats = new KeyStatistics;
                {
                    GeneratedAt = DateTime.UtcNow,
                    TimeframeStart = startTime,
                    TimeframeEnd = DateTime.UtcNow;
                };

                // Get key counts;
                stats.TotalKeys = await _repository.GetTotalKeyCountAsync();
                stats.ActiveKeys = await _repository.GetActiveKeyCountAsync();
                stats.ExpiredKeys = await _repository.GetExpiredKeyCountAsync();
                stats.RevokedKeys = await _repository.GetRevokedKeyCountAsync();
                stats.CompromisedKeys = await _repository.GetCompromisedKeyCountAsync();

                // Get key type distribution;
                stats.KeyTypeDistribution = await _repository.GetKeyTypeDistributionAsync();

                // Get algorithm distribution;
                stats.AlgorithmDistribution = await _repository.GetAlgorithmDistributionAsync();

                // Get storage distribution;
                stats.StorageDistribution = await _repository.GetStorageDistributionAsync();

                // Get key generation statistics;
                stats.KeysGenerated = await _repository.GetKeysGeneratedCountAsync(startTime);
                stats.KeysRotated = await _repository.GetKeysRotatedCountAsync(startTime);
                stats.KeysRevoked = await _repository.GetKeysRevokedCountAsync(startTime);

                // Get usage statistics;
                stats.EncryptionOperations = await _repository.GetEncryptionOperationCountAsync(startTime);
                stats.DecryptionOperations = await _repository.GetDecryptionOperationCountAsync(startTime);
                stats.SigningOperations = await _repository.GetSigningOperationCountAsync(startTime);
                stats.VerificationOperations = await _repository.GetVerificationOperationCountAsync(startTime);

                // Calculate average key age;
                stats.AverageKeyAgeDays = await _repository.GetAverageKeyAgeAsync();

                // Calculate key rotation frequency;
                stats.AverageRotationFrequencyDays = await _repository.GetAverageRotationFrequencyAsync();

                // Get top used keys;
                stats.TopUsedKeys = await _repository.GetTopUsedKeysAsync(startTime, 10);

                // Get key health summary;
                stats.HealthyKeys = await _repository.GetHealthyKeyCountAsync();
                stats.WarningKeys = await _repository.GetWarningKeyCountAsync();
                stats.CriticalKeys = await _repository.GetCriticalKeyCountAsync();

                _logger.LogDebug("Generated key statistics: {TotalKeys} total keys, {ActiveKeys} active",
                    stats.TotalKeys, stats.ActiveKeys);

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get key statistics");
                throw new KeyException("Failed to get key statistics", ex);
            }
        }

        public async Task<KeyAuditResult> AuditKeysAsync(KeyAuditOptions options = null)
        {
            options ??= new KeyAuditOptions();

            try
            {
                _logger.LogInformation("Starting key audit with options: {Options}", JsonConvert.SerializeObject(options));

                var result = new KeyAuditResult;
                {
                    AuditId = Guid.NewGuid(),
                    StartedAt = DateTime.UtcNow;
                };

                // Get all keys for audit;
                var keys = await _repository.GetAllKeysAsync(options.IncludeInactive);

                foreach (var key in keys)
                {
                    var keyAudit = await AuditSingleKeyAsync(key, options);
                    result.KeyAudits.Add(keyAudit);

                    // Categorize findings;
                    if (keyAudit.CriticalFindings.Any())
                        result.CriticalKeys.Add(key.Id);
                    else if (keyAudit.WarningFindings.Any())
                        result.WarningKeys.Add(key.Id);
                    else;
                        result.HealthyKeys.Add(key.Id);
                }

                // Check for policy violations;
                result.PolicyViolations = await CheckPolicyViolationsAsync(keys, options);

                // Check for compliance issues;
                if (options.CheckCompliance)
                {
                    result.ComplianceIssues = await CheckComplianceIssuesAsync(keys, options);
                }

                // Generate audit report;
                result.Summary = GenerateAuditSummary(result);
                result.Recommendations = GenerateAuditRecommendations(result);

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                // Store audit result;
                await _repository.StoreAuditResultAsync(result);

                // Log audit;
                await _auditLogger.LogKeyAuditAsync(new KeyAuditAuditRecord;
                {
                    AuditId = result.AuditId,
                    TotalKeysAudited = keys.Count,
                    CriticalKeysFound = result.CriticalKeys.Count,
                    WarningKeysFound = result.WarningKeys.Count,
                    StartedAt = result.StartedAt,
                    CompletedAt = result.CompletedAt;
                });

                _logger.LogInformation("Key audit completed: {AuditId}, Keys: {KeyCount}, Critical: {CriticalCount}",
                    result.AuditId, keys.Count, result.CriticalKeys.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key audit failed");
                throw new KeyException("Key audit failed", ex);
            }
        }

        public async Task<KeyCleanupResult> CleanupKeysAsync(KeyCleanupOptions options = null)
        {
            options ??= new KeyCleanupOptions();

            try
            {
                _logger.LogInformation("Starting key cleanup with options: {Options}", JsonConvert.SerializeObject(options));

                var result = new KeyCleanupResult;
                {
                    CleanupId = Guid.NewGuid(),
                    StartedAt = DateTime.UtcNow;
                };

                // Cleanup expired keys;
                if (options.CleanupExpiredKeys)
                {
                    var expiredKeys = await _repository.GetExpiredKeysAsync(options.ExpiredForAtLeast);
                    foreach (var key in expiredKeys)
                    {
                        if (await CanCleanupKeyAsync(key, options))
                        {
                            await CleanupSingleKeyAsync(key, KeyCleanupReason.Expired, options);
                            result.ExpiredKeysRemoved++;
                        }
                    }
                }

                // Cleanup revoked keys;
                if (options.CleanupRevokedKeys)
                {
                    var revokedKeys = await _repository.GetRevokedKeysAsync(options.RevokedForAtLeast);
                    foreach (var key in revokedKeys)
                    {
                        if (await CanCleanupKeyAsync(key, options))
                        {
                            await CleanupSingleKeyAsync(key, KeyCleanupReason.Revoked, options);
                            result.RevokedKeysRemoved++;
                        }
                    }
                }

                // Cleanup compromised keys;
                if (options.CleanupCompromisedKeys)
                {
                    var compromisedKeys = await _repository.GetCompromisedKeysAsync(options.CompromisedForAtLeast);
                    foreach (var key in compromisedKeys)
                    {
                        if (await CanCleanupKeyAsync(key, options))
                        {
                            await CleanupSingleKeyAsync(key, KeyCleanupReason.Compromised, options);
                            result.CompromisedKeysRemoved++;
                        }
                    }
                }

                // Cleanup old backups;
                if (options.CleanupOldBackups)
                {
                    result.BackupsRemoved = await _storageProvider.CleanupOldBackupsAsync(options.BackupRetentionPeriod);
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                // Clear caches;
                ClearAllCaches();

                // Log cleanup;
                await _auditLogger.LogKeyCleanupAsync(new KeyCleanupAuditRecord;
                {
                    CleanupId = result.CleanupId,
                    ExpiredKeysRemoved = result.ExpiredKeysRemoved,
                    RevokedKeysRemoved = result.RevokedKeysRemoved,
                    CompromisedKeysRemoved = result.CompromisedKeysRemoved,
                    BackupsRemoved = result.BackupsRemoved,
                    StartedAt = result.StartedAt,
                    CompletedAt = result.CompletedAt;
                });

                _logger.LogInformation("Key cleanup completed: {Expired} expired, {Revoked} revoked, {Compromised} compromised keys removed",
                    result.ExpiredKeysRemoved, result.RevokedKeysRemoved, result.CompromisedKeysRemoved);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key cleanup failed");
                throw new KeyException("Key cleanup failed", ex);
            }
        }

        public async Task<KeyManagerHealthStatus> GetHealthStatusAsync()
        {
            try
            {
                var status = new KeyManagerHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ServiceStatus = ServiceStatus.Healthy;
                };

                // Check repository connection;
                status.RepositoryHealthy = await _repository.CheckHealthAsync();

                // Check storage provider;
                status.StorageProviderHealthy = await _storageProvider.CheckHealthAsync();

                // Check key protector;
                status.KeyProtectorHealthy = await _keyProtector.CheckHealthAsync();

                // Check algorithm providers;
                status.AlgorithmProvidersHealthy = _algorithmProviders.All(p => p.Value.IsHealthy());

                // Get key statistics;
                var stats = await GetKeyStatisticsAsync(TimeSpan.FromHours(1));

                status.KeysGeneratedLastHour = stats.KeysGenerated;
                status.KeysRotatedLastHour = stats.KeysRotated;
                status.KeysRevokedLastHour = stats.KeysRevoked;
                status.EncryptionOperationsLastHour = stats.EncryptionOperations;

                // Check for critical keys;
                status.CriticalKeys = await _repository.GetCriticalKeyCountAsync();

                // Check for expired keys;
                status.ExpiredKeys = await _repository.GetExpiredKeyCountAsync();

                // Check for keys nearing expiration;
                status.KeysNearingExpiration = await _repository.GetKeysNearingExpirationCountAsync(TimeSpan.FromDays(7));

                // Check cache health;
                status.CacheHealthy = _keyCache.IsHealthy() && _activeKeysCache.IsHealthy();

                // Determine overall status;
                if (!status.RepositoryHealthy || !status.StorageProviderHealthy || !status.KeyProtectorHealthy)
                {
                    status.ServiceStatus = ServiceStatus.Critical;
                }
                else if (!status.AlgorithmProvidersHealthy || status.CriticalKeys > 0)
                {
                    status.ServiceStatus = ServiceStatus.Degraded;
                }
                else if (status.ExpiredKeys > 5 || status.KeysNearingExpiration > 10)
                {
                    status.ServiceStatus = ServiceStatus.Warning;
                }

                return status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get key manager health status");
                return new KeyManagerHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ServiceStatus = ServiceStatus.Unhealthy,
                    HealthCheckError = ex.Message;
                };
            }
        }

        #region Private Helper Methods;

        private ValidationResult ValidateKeyGenerationRequest(KeyGenerationRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.Algorithm))
                errors.Add("Algorithm is required");

            if (request.KeySize <= 0)
                errors.Add("Key size must be positive");

            if (request.KeyType == KeyType.Unknown)
                errors.Add("Valid key type is required");

            if (request.Storage == KeyStorage.Unknown)
                errors.Add("Valid storage location is required");

            // Validate algorithm-specific key sizes;
            if (!IsValidKeySizeForAlgorithm(request.Algorithm, request.KeySize))
            {
                errors.Add($"Key size {request.KeySize} is not valid for algorithm {request.Algorithm}");
            }

            // Validate expiration;
            if (request.ExpiresAt.HasValue && request.ExpiresAt <= DateTime.UtcNow)
            {
                errors.Add("Expiration date must be in the future");
            }

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private ValidationResult ValidateKeyImportRequest(KeyImportRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.Algorithm))
                errors.Add("Algorithm is required");

            if (request.KeyData == null || request.KeyData.Length == 0)
                errors.Add("Key data is required");

            if (request.KeyType == KeyType.Unknown)
                errors.Add("Valid key type is required");

            if (request.Format == KeyImportFormat.Unknown)
                errors.Add("Valid import format is required");

            if (request.Storage == KeyStorage.Unknown)
                errors.Add("Valid storage location is required");

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private string GenerateKeyId(KeyGenerationRequest request)
        {
            using var sha256 = SHA256.Create();
            var input = $"{request.Algorithm}_{request.KeySize}_{Guid.NewGuid()}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

        private async Task<bool> VerifyKeyMaterialAsync(byte[] keyMaterial, string algorithm, int keySize)
        {
            try
            {
                // Basic validation;
                if (keyMaterial == null || keyMaterial.Length == 0)
                    return false;

                // Algorithm-specific validation;
                switch (algorithm.ToUpperInvariant())
                {
                    case "AES":
                        return keyMaterial.Length == keySize / 8; // AES key size in bytes;

                    case "RSA":
                        // RSA key validation is more complex;
                        using (var rsa = RSA.Create())
                        {
                            rsa.ImportRSAPrivateKey(keyMaterial, out _);
                            return rsa.KeySize == keySize;
                        }

                    default:
                        // For unknown algorithms, just check minimum size;
                        return keyMaterial.Length >= 16; // Minimum 128-bit key;
                }
            }
            catch
            {
                return false;
            }
        }

        private byte[] ParseKeyMaterial(string keyData, KeyImportFormat format, string password)
        {
            return format switch;
            {
                KeyImportFormat.PEM => ParsePemKey(keyData, password),
                KeyImportFormat.DER => Convert.FromBase64String(keyData),
                KeyImportFormat.XML => Encoding.UTF8.GetBytes(keyData),
                KeyImportFormat.JSON => Encoding.UTF8.GetBytes(keyData),
                KeyImportFormat.Raw => Convert.FromBase64String(keyData),
                _ => throw new NotSupportedException($"Import format not supported: {format}")
            };
        }

        private byte[] ParsePemKey(string pemData, string password)
        {
            // Remove PEM headers/footers and decode base64;
            var lines = pemData.Split('\n');
            var base64Data = new StringBuilder();

            foreach (var line in lines)
            {
                if (!line.StartsWith("---") && !string.IsNullOrWhiteSpace(line))
                {
                    base64Data.Append(line.Trim());
                }
            }

            var keyBytes = Convert.FromBase64String(base64Data.ToString());

            // Handle encrypted PEM;
            if (!string.IsNullOrEmpty(password) && IsEncryptedPem(pemData))
            {
                // Decrypt using password;
                keyBytes = DecryptPemKey(keyBytes, password);
            }

            return keyBytes;
        }

        private bool IsEncryptedPem(string pemData)
        {
            return pemData.Contains("ENCRYPTED") || pemData.Contains("Proc-Type: 4,ENCRYPTED");
        }

        private byte[] DecryptPemKey(byte[] encryptedKey, string password)
        {
            // Implement PEM decryption using password-based encryption;
            // This is a simplified version - actual implementation depends on encryption algorithm;
            using var deriveBytes = new Rfc2898DeriveBytes(password, new byte[16], 10000, HashAlgorithmName.SHA256);
            var key = deriveBytes.GetBytes(32); // 256-bit key;
            var iv = deriveBytes.GetBytes(16);  // 128-bit IV;

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(encryptedKey, 0, encryptedKey.Length);
        }

        private bool CanUseKeyForOperation(CryptographicKey key, KeyOperation operation)
        {
            if (!key.IsActive || key.IsCompromised)
                return false;

            // Check if key is expired;
            if (key.ExpiresAt.HasValue && key.ExpiresAt < DateTime.UtcNow)
                return false;

            // Check key usage permissions;
            return operation switch;
            {
                KeyOperation.Encryption => key.Usage.HasFlag(KeyUsage.Encryption) || key.Usage.HasFlag(KeyUsage.All),
                KeyOperation.Decryption => key.Usage.HasFlag(KeyUsage.Decryption) || key.Usage.HasFlag(KeyUsage.All),
                KeyOperation.Signing => key.Usage.HasFlag(KeyUsage.Signing) || key.Usage.HasFlag(KeyUsage.All),
                KeyOperation.Verification => key.Usage.HasFlag(KeyUsage.Verification) || key.Usage.HasFlag(KeyUsage.All),
                KeyOperation.KeyWrapping => key.Usage.HasFlag(KeyUsage.KeyWrapping) || key.Usage.HasFlag(KeyUsage.All),
                KeyOperation.KeyUnwrapping => key.Usage.HasFlag(KeyUsage.KeyWrapping) || key.Usage.HasFlag(KeyUsage.All),
                _ => false;
            };
        }

        private bool CanRotateKey(CryptographicKey key, KeyRotationOptions options)
        {
            if (!key.IsActive || key.IsCompromised)
                return false;

            // Check if key is already being rotated;
            if (key.RotationState == KeyRotationState.Pending)
                return false;

            // Check minimum rotation interval;
            if (key.ActivatedAt.HasValue)
            {
                var timeActive = DateTime.UtcNow - key.ActivatedAt.Value;
                if (timeActive < _config.MinimumRotationInterval)
                    return false;
            }

            return true;
        }

        private async Task UpdateActiveKeyCacheAsync(CryptographicKey key)
        {
            if (key.IsActive && !key.IsCompromised)
            {
                var cacheKey = $"active_key_{key.Algorithm}_{key.Usage}";
                _activeKeysCache.Set(cacheKey, key.Id, TimeSpan.FromMinutes(5));
            }
        }

        private void ClearKeyCache(Guid keyId)
        {
            _keyCache.Remove(keyId);
        }

        private void ClearAllCaches()
        {
            _keyCache.Clear();
            _activeKeysCache.Clear();
        }

        private bool IsValidKeySizeForAlgorithm(string algorithm, int keySize)
        {
            return algorithm.ToUpperInvariant() switch;
            {
                "AES" => keySize == 128 || keySize == 192 || keySize == 256,
                "DES" => keySize == 64,
                "3DES" => keySize == 192,
                "RSA" => keySize >= 2048 && keySize <= 4096 && keySize % 8 == 0,
                "ECDSA" => keySize == 256 || keySize == 384 || keySize == 521,
                "HMACSHA256" => keySize >= 256,
                "HMACSHA512" => keySize >= 512,
                _ => keySize >= 128 // Default minimum;
            };
        }

        private int GetDefaultKeySize(string algorithm)
        {
            return algorithm.ToUpperInvariant() switch;
            {
                "AES" => 256,
                "RSA" => 2048,
                "ECDSA" => 256,
                "HMACSHA256" => 256,
                "HMACSHA512" => 512,
                _ => 256;
            };
        }

        private KeyType GetKeyTypeForAlgorithm(string algorithm)
        {
            return algorithm.ToUpperInvariant() switch;
            {
                "AES" or "DES" or "3DES" or "RC2" => KeyType.Symmetric,
                "RSA" or "ECDSA" or "DSA" => KeyType.Asymmetric,
                "HMACSHA256" or "HMACSHA512" or "PBKDF2" => KeyType.Hashing,
                _ => KeyType.Symmetric;
            };
        }

        private async void ExecuteScheduledRotationsAsync(object state)
        {
            try
            {
                _logger.LogDebug("Executing scheduled key rotations");

                var keysToRotate = await _repository.GetKeysDueForRotationAsync();

                foreach (var key in keysToRotate)
                {
                    try
                    {
                        await RotateKeyAsync(key.Id, new KeyRotationOptions;
                        {
                            Reason = "Scheduled rotation",
                            KeepPreviousActive = true,
                            ReEncryptData = _config.ReEncryptOnRotation;
                        });

                        _logger.LogInformation("Scheduled rotation completed for key: {KeyId}", key.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scheduled rotation failed for key: {KeyId}", key.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled key rotation execution failed");
            }
        }

        private async void ExecuteScheduledCleanupAsync(object state)
        {
            try
            {
                _logger.LogDebug("Executing scheduled key cleanup");

                await CleanupKeysAsync(new KeyCleanupOptions;
                {
                    CleanupExpiredKeys = true,
                    ExpiredForAtLeast = TimeSpan.FromDays(30),
                    CleanupRevokedKeys = true,
                    RevokedForAtLeast = TimeSpan.FromDays(90),
                    CleanupCompromisedKeys = true,
                    CompromisedForAtLeast = TimeSpan.FromDays(1),
                    CleanupOldBackups = true,
                    BackupRetentionPeriod = TimeSpan.FromDays(365)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled key cleanup failed");
            }
        }

        private KeyHealth CalculateOverallKeyHealth(KeyHealthStatus status)
        {
            // Take the worst health status;
            var healthStatuses = new[]
            {
                status.ExpirationHealth,
                status.ActivationHealth,
                status.MaterialHealth,
                status.AgeHealth,
                status.UsageHealth;
            };

            return healthStatuses.Max();
        }

        private async Task<bool> CanCleanupKeyAsync(CryptographicKey key, KeyCleanupOptions options)
        {
            // Check if key is still in use;
            if (options.CheckUsageBeforeCleanup)
            {
                var lastUsed = await _repository.GetKeyLastUsedAsync(key.Id);
                if (lastUsed.HasValue && (DateTime.UtcNow - lastUsed.Value) < options.MinimumIdleTime)
                {
                    return false;
                }
            }

            // Check if key has dependencies;
            if (options.CheckDependencies)
            {
                var hasDependencies = await _repository.KeyHasDependenciesAsync(key.Id);
                if (hasDependencies)
                {
                    return false;
                }
            }

            return true;
        }

        private async Task CleanupSingleKeyAsync(CryptographicKey key, KeyCleanupReason reason, KeyCleanupOptions options)
        {
            // Backup key if configured;
            if (options.BackupBeforeCleanup)
            {
                try
                {
                    await BackupKeyAsync(key.Id, new BackupOptions;
                    {
                        BackupLocation = $"cleanup_backup_{DateTime.UtcNow:yyyyMMdd}",
                        Purpose = $"Pre-cleanup backup for {reason}",
                        EncryptBackup = true,
                        SignBackup = true;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to backup key before cleanup: {KeyId}", key.Id);
                }
            }

            // Delete key material from storage;
            await _storageProvider.DeleteKeyAsync(key.Id);

            // Update key metadata;
            key.CleanedUpAt = DateTime.UtcNow;
            key.CleanupReason = reason;
            await _repository.UpdateKeyAsync(key);

            // Log cleanup;
            await _auditLogger.LogKeyCleanupOperationAsync(new KeyCleanupOperationAuditRecord;
            {
                KeyId = key.Id,
                CleanupReason = reason,
                CleanedUpAt = DateTime.UtcNow;
            });
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _rotationTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _keyOperationLock?.Dispose();
                    _keyCache?.Dispose();
                    _activeKeysCache?.Dispose();

                    foreach (var provider in _algorithmProviders.Values)
                    {
                        if (provider is IDisposable disposableProvider)
                        {
                            disposableProvider.Dispose();
                        }
                    }

                    _logger.LogInformation("KeyManager disposed");
                }

                _disposed = true;
            }
        }

        ~KeyManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Enums;

    public enum KeyOperation;
    {
        Encryption = 0,
        Decryption = 1,
        Signing = 2,
        Verification = 3,
        KeyWrapping = 4,
        KeyUnwrapping = 5;
    }

    public enum KeyExportFormat;
    {
        PEM = 0,
        DER = 1,
        XML = 2,
        JSON = 3,
        Raw = 4;
    }

    public enum KeyImportFormat;
    {
        Unknown = 0,
        PEM = 1,
        DER = 2,
        XML = 3,
        JSON = 4,
        Raw = 5;
    }

    public enum RevocationReason;
    {
        SecurityBreach = 0,
        Compromised = 1,
        KeyRotation = 2,
        Administrative = 3,
        LegalRequirement = 4,
        SystemMaintenance = 5;
    }

    public enum KeyCleanupReason;
    {
        Expired = 0,
        Revoked = 1,
        Compromised = 2,
        Orphaned = 3,
        SystemCleanup = 4;
    }

    public enum KeyHealth;
    {
        Healthy = 0,
        Warning = 1,
        Critical = 2,
        Error = 3,
        Missing = 4;
    }

    public enum ServiceStatus;
    {
        Healthy = 0,
        Warning = 1,
        Degraded = 2,
        Unhealthy = 3,
        Critical = 4;
    }

    // Additional supporting classes would be defined here...
    // (CryptographicKey, KeyGenerationRequest, KeyImportRequest, KeyRotationResult, etc.)

    #endregion;
}
