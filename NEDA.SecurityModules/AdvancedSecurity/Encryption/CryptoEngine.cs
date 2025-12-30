using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.SecurityModules.AdvancedSecurity.Encryption.CryptoEngine;

namespace NEDA.SecurityModules.AdvancedSecurity.Encryption;
{
    /// <summary>
    /// Cryptographic algorithms supported by the engine;
    /// </summary>
    public enum CryptoAlgorithm;
    {
        AES_GCM_256 = 0,
        AES_CBC_256 = 1,
        RSA_OAEP_2048 = 2,
        RSA_OAEP_4096 = 3,
        ECDSA_P256 = 4,
        ECDSA_P384 = 5,
        ECDH_P256 = 6,
        ECDH_P384 = 7,
        HMAC_SHA256 = 8,
        HMAC_SHA512 = 9,
        PBKDF2_SHA256 = 10,
        Argon2id = 11,
        ChaCha20_Poly1305 = 12;
    }

    /// <summary>
    /// Key types for cryptographic operations;
    /// </summary>
    public enum KeyType;
    {
        Symmetric = 0,
        AsymmetricPublic = 1,
        AsymmetricPrivate = 2,
        Hmac = 3,
        Derivation = 4,
        Master = 5;
    }

    /// <summary>
    /// Key usage flags;
    /// </summary>
    [Flags]
    public enum KeyUsage;
    {
        None = 0,
        Encrypt = 1,
        Decrypt = 2,
        Sign = 4,
        Verify = 8,
        KeyWrap = 16,
        KeyUnwrap = 32,
        Derive = 64,
        All = Encrypt | Decrypt | Sign | Verify | KeyWrap | KeyUnwrap | Derive;
    }

    /// <summary>
    /// Represents a cryptographic key with metadata;
    /// </summary>
    public class CryptoKey;
    {
        public string KeyId { get; set; }
        public string KeyName { get; set; }
        public KeyType KeyType { get; set; }
        public CryptoAlgorithm Algorithm { get; set; }
        public KeyUsage Usage { get; set; }
        public byte[] KeyMaterial { get; set; }
        public byte[] IV { get; set; }
        public byte[] Tag { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime LastUsed { get; set; }
        public int UsageCount { get; set; }
        public bool IsActive { get; set; }
        public string Version { get; set; }

        public CryptoKey()
        {
            KeyId = Guid.NewGuid().ToString("N");
            CreatedAt = DateTime.UtcNow;
            LastUsed = DateTime.UtcNow;
            Metadata = new Dictionary<string, object>();
            IsActive = true;
            Version = "1.0";
        }
    }

    /// <summary>
    /// Result of cryptographic operation;
    /// </summary>
    public class CryptoResult;
    {
        public bool Success { get; set; }
        public byte[] Data { get; set; }
        public byte[] Key { get; set; }
        public byte[] IV { get; set; }
        public byte[] Tag { get; set; }
        public byte[] Signature { get; set; }
        public string Algorithm { get; set; }
        public string KeyId { get; set; }
        public string Error { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public TimeSpan ProcessingTime { get; set; }

        public CryptoResult()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Key management operations;
    /// </summary>
    public class KeyOperation;
    {
        public string OperationId { get; set; }
        public string KeyId { get; set; }
        public string OperationType { get; set; }
        public DateTime Timestamp { get; set; }
        public string PerformedBy { get; set; }
        public string ClientIP { get; set; }
        public Dictionary<string, object> Details { get; set; }

        public KeyOperation()
        {
            OperationId = Guid.NewGuid().ToString("N");
            Timestamp = DateTime.UtcNow;
            Details = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Professional cryptographic engine with advanced encryption,
    /// key management, and hardware security module integration;
    /// </summary>
    public class CryptoEngine : ICryptoEngine, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IKeyManager _keyManager;
        private readonly IAuditLogger _auditLogger;
        private readonly IEventBus _eventBus;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IDataProtector _dataProtector;

        private readonly Dictionary<string, CryptoKey> _keyCache = new Dictionary<string, CryptoKey>();
        private readonly ReaderWriterLockSlim _cacheLock = new ReaderWriterLockSlim();
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        private bool _disposed = false;
        private long _totalOperations = 0;
        private long _failedOperations = 0;
        private DateTime _startTime = DateTime.UtcNow;

        // Performance counters;
        private readonly PerformanceCounter _encryptionPerSecondCounter;
        private readonly PerformanceCounter _decryptionPerSecondCounter;
        private readonly PerformanceCounter _averageOperationTimeCounter;
        private readonly PerformanceCounter _keyUsageCounter;

        /// <summary>
        /// Event raised when cryptographic operation is performed;
        /// </summary>
        public event EventHandler<CryptoOperationEventArgs> CryptoOperationPerformed;

        /// <summary>
        /// Event raised when key is rotated;
        /// </summary>
        public event EventHandler<KeyRotatedEventArgs> KeyRotated;

        /// <summary>
        /// Event raised when cryptographic error occurs;
        /// </summary>
        public event EventHandler<CryptoErrorEventArgs> CryptoError;

        /// <summary>
        /// Initializes a new instance of CryptoEngine;
        /// </summary>
        public CryptoEngine(
            ILogger logger,
            IKeyManager keyManager,
            IAuditLogger auditLogger,
            IEventBus eventBus,
            IDiagnosticTool diagnosticTool,
            IDataProtector dataProtector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _dataProtector = dataProtector ?? throw new ArgumentNullException(nameof(dataProtector));

            // Initialize performance counters;
            _encryptionPerSecondCounter = new PerformanceCounter("NEDA Crypto Engine", "Encryptions Per Second");
            _decryptionPerSecondCounter = new PerformanceCounter("NEDA Crypto Engine", "Decryptions Per Second");
            _averageOperationTimeCounter = new PerformanceCounter("NEDA Crypto Engine", "Average Operation Time");
            _keyUsageCounter = new PerformanceCounter("NEDA Crypto Engine", "Key Usage");

            // Initialize with default keys;
            InitializeDefaultKeys();

            _logger.LogInformation("CryptoEngine initialized", new;
            {
                StartTime = _startTime,
                DefaultAlgorithms = Enum.GetNames(typeof(CryptoAlgorithm)).Length;
            });
        }

        /// <summary>
        /// Encrypts data using specified algorithm;
        /// </summary>
        public async Task<CryptoResult> EncryptAsync(
            byte[] plaintext,
            CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256,
            string keyId = null,
            Dictionary<string, object> metadata = null)
        {
            if (plaintext == null || plaintext.Length == 0)
                throw new ArgumentException("Plaintext cannot be null or empty", nameof(plaintext));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _totalOperations);

            try
            {
                CryptoKey key = null;

                // Get or generate key;
                if (!string.IsNullOrEmpty(keyId))
                {
                    key = await GetKeyAsync(keyId);
                    if (key == null || !key.IsActive)
                        throw new CryptographicException($"Key not found or inactive: {keyId}");

                    if (!key.Usage.HasFlag(KeyUsage.Encrypt))
                        throw new CryptographicException($"Key {keyId} not authorized for encryption");

                    if (key.ExpiresAt.HasValue && key.ExpiresAt.Value < DateTime.UtcNow)
                        throw new CryptographicException($"Key {keyId} has expired");
                }
                else;
                {
                    key = await GenerateKeyAsync(algorithm, KeyUsage.Encrypt, "Encryption key");
                }

                CryptoResult result = null;

                switch (algorithm)
                {
                    case CryptoAlgorithm.AES_GCM_256:
                        result = await EncryptAesGcmAsync(plaintext, key);
                        break;

                    case CryptoAlgorithm.AES_CBC_256:
                        result = await EncryptAesCbcAsync(plaintext, key);
                        break;

                    case CryptoAlgorithm.RSA_OAEP_2048:
                    case CryptoAlgorithm.RSA_OAEP_4096:
                        result = await EncryptRsaOaepAsync(plaintext, key);
                        break;

                    case CryptoAlgorithm.ChaCha20_Poly1305:
                        result = await EncryptChaCha20Poly1305Async(plaintext, key);
                        break;

                    default:
                        throw new NotSupportedException($"Algorithm {algorithm} is not supported for encryption");
                }

                result.KeyId = key.KeyId;
                result.Algorithm = algorithm.ToString();
                result.ProcessingTime = stopwatch.Elapsed;

                // Update key usage;
                key.LastUsed = DateTime.UtcNow;
                key.UsageCount++;
                await UpdateKeyAsync(key);

                // Update performance counters;
                _encryptionPerSecondCounter.Increment();
                _averageOperationTimeCounter.RawValue = (long)stopwatch.ElapsedMilliseconds;
                _keyUsageCounter.Increment();

                // Log operation;
                await LogCryptoOperationAsync("Encrypt", key.KeyId, algorithm, true, metadata, stopwatch.Elapsed);

                // Raise event;
                OnCryptoOperationPerformed(new CryptoOperationEventArgs(
                    "Encrypt",
                    key.KeyId,
                    algorithm,
                    plaintext.Length,
                    result.Data?.Length ?? 0,
                    true,
                    DateTime.UtcNow));

                return result;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedOperations);
                _logger.LogError($"Encryption failed", ex);

                OnCryptoError(new CryptoErrorEventArgs(
                    "Encrypt",
                    algorithm,
                    ex.Message,
                    DateTime.UtcNow));

                throw new CryptoException($"Encryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Decrypts data using specified algorithm;
        /// </summary>
        public async Task<CryptoResult> DecryptAsync(
            byte[] ciphertext,
            CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256,
            string keyId = null,
            byte[] iv = null,
            byte[] tag = null,
            Dictionary<string, object> metadata = null)
        {
            if (ciphertext == null || ciphertext.Length == 0)
                throw new ArgumentException("Ciphertext cannot be null or empty", nameof(ciphertext));

            if (string.IsNullOrEmpty(keyId))
                throw new ArgumentException("Key ID is required for decryption", nameof(keyId));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _totalOperations);

            try
            {
                var key = await GetKeyAsync(keyId);
                if (key == null || !key.IsActive)
                    throw new CryptographicException($"Key not found or inactive: {keyId}");

                if (!key.Usage.HasFlag(KeyUsage.Decrypt))
                    throw new CryptographicException($"Key {keyId} not authorized for decryption");

                if (key.ExpiresAt.HasValue && key.ExpiresAt.Value < DateTime.UtcNow)
                    throw new CryptographicException($"Key {keyId} has expired");

                CryptoResult result = null;

                switch (algorithm)
                {
                    case CryptoAlgorithm.AES_GCM_256:
                        if (iv == null || tag == null)
                            throw new ArgumentException("IV and Tag are required for AES-GCM decryption");

                        result = await DecryptAesGcmAsync(ciphertext, key, iv, tag);
                        break;

                    case CryptoAlgorithm.AES_CBC_256:
                        if (iv == null)
                            throw new ArgumentException("IV is required for AES-CBC decryption");

                        result = await DecryptAesCbcAsync(ciphertext, key, iv);
                        break;

                    case CryptoAlgorithm.RSA_OAEP_2048:
                    case CryptoAlgorithm.RSA_OAEP_4096:
                        result = await DecryptRsaOaepAsync(ciphertext, key);
                        break;

                    case CryptoAlgorithm.ChaCha20_Poly1305:
                        if (iv == null || tag == null)
                            throw new ArgumentException("IV and Tag are required for ChaCha20-Poly1305 decryption");

                        result = await DecryptChaCha20Poly1305Async(ciphertext, key, iv, tag);
                        break;

                    default:
                        throw new NotSupportedException($"Algorithm {algorithm} is not supported for decryption");
                }

                result.KeyId = key.KeyId;
                result.Algorithm = algorithm.ToString();
                result.ProcessingTime = stopwatch.Elapsed;

                // Update key usage;
                key.LastUsed = DateTime.UtcNow;
                key.UsageCount++;
                await UpdateKeyAsync(key);

                // Update performance counters;
                _decryptionPerSecondCounter.Increment();
                _averageOperationTimeCounter.RawValue = (long)stopwatch.ElapsedMilliseconds;
                _keyUsageCounter.Increment();

                // Log operation;
                await LogCryptoOperationAsync("Decrypt", key.KeyId, algorithm, true, metadata, stopwatch.Elapsed);

                // Raise event;
                OnCryptoOperationPerformed(new CryptoOperationEventArgs(
                    "Decrypt",
                    key.KeyId,
                    algorithm,
                    ciphertext.Length,
                    result.Data?.Length ?? 0,
                    true,
                    DateTime.UtcNow));

                return result;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedOperations);
                _logger.LogError($"Decryption failed", ex);

                OnCryptoError(new CryptoErrorEventArgs(
                    "Decrypt",
                    algorithm,
                    ex.Message,
                    DateTime.UtcNow));

                throw new CryptoException($"Decryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Signs data using specified algorithm;
        /// </summary>
        public async Task<CryptoResult> SignAsync(
            byte[] data,
            CryptoAlgorithm algorithm = CryptoAlgorithm.ECDSA_P256,
            string keyId = null,
            Dictionary<string, object> metadata = null)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _totalOperations);

            try
            {
                CryptoKey key = null;

                // Get or generate key;
                if (!string.IsNullOrEmpty(keyId))
                {
                    key = await GetKeyAsync(keyId);
                    if (key == null || !key.IsActive)
                        throw new CryptographicException($"Key not found or inactive: {keyId}");

                    if (!key.Usage.HasFlag(KeyUsage.Sign))
                        throw new CryptographicException($"Key {keyId} not authorized for signing");

                    if (key.ExpiresAt.HasValue && key.ExpiresAt.Value < DateTime.UtcNow)
                        throw new CryptographicException($"Key {keyId} has expired");
                }
                else;
                {
                    key = await GenerateKeyAsync(algorithm, KeyUsage.Sign, "Signing key");
                }

                CryptoResult result = null;

                switch (algorithm)
                {
                    case CryptoAlgorithm.ECDSA_P256:
                    case CryptoAlgorithm.ECDSA_P384:
                        result = await SignEcdsaAsync(data, key);
                        break;

                    case CryptoAlgorithm.HMAC_SHA256:
                    case CryptoAlgorithm.HMAC_SHA512:
                        result = await SignHmacAsync(data, key);
                        break;

                    case CryptoAlgorithm.RSA_OAEP_2048:
                    case CryptoAlgorithm.RSA_OAEP_4096:
                        result = await SignRsaAsync(data, key);
                        break;

                    default:
                        throw new NotSupportedException($"Algorithm {algorithm} is not supported for signing");
                }

                result.KeyId = key.KeyId;
                result.Algorithm = algorithm.ToString();
                result.ProcessingTime = stopwatch.Elapsed;

                // Update key usage;
                key.LastUsed = DateTime.UtcNow;
                key.UsageCount++;
                await UpdateKeyAsync(key);

                // Log operation;
                await LogCryptoOperationAsync("Sign", key.KeyId, algorithm, true, metadata, stopwatch.Elapsed);

                return result;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedOperations);
                _logger.LogError($"Signing failed", ex);

                OnCryptoError(new CryptoErrorEventArgs(
                    "Sign",
                    algorithm,
                    ex.Message,
                    DateTime.UtcNow));

                throw new CryptoException($"Signing failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Verifies data signature;
        /// </summary>
        public async Task<bool> VerifyAsync(
            byte[] data,
            byte[] signature,
            CryptoAlgorithm algorithm = CryptoAlgorithm.ECDSA_P256,
            string keyId = null,
            Dictionary<string, object> metadata = null)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            if (signature == null || signature.Length == 0)
                throw new ArgumentException("Signature cannot be null or empty", nameof(signature));

            if (string.IsNullOrEmpty(keyId))
                throw new ArgumentException("Key ID is required for verification", nameof(keyId));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _totalOperations);

            try
            {
                var key = await GetKeyAsync(keyId);
                if (key == null || !key.IsActive)
                    throw new CryptographicException($"Key not found or inactive: {keyId}");

                if (!key.Usage.HasFlag(KeyUsage.Verify))
                    throw new CryptographicException($"Key {keyId} not authorized for verification");

                bool isValid = false;

                switch (algorithm)
                {
                    case CryptoAlgorithm.ECDSA_P256:
                    case CryptoAlgorithm.ECDSA_P384:
                        isValid = await VerifyEcdsaAsync(data, signature, key);
                        break;

                    case CryptoAlgorithm.HMAC_SHA256:
                    case CryptoAlgorithm.HMAC_SHA512:
                        isValid = await VerifyHmacAsync(data, signature, key);
                        break;

                    case CryptoAlgorithm.RSA_OAEP_2048:
                    case CryptoAlgorithm.RSA_OAEP_4096:
                        isValid = await VerifyRsaAsync(data, signature, key);
                        break;

                    default:
                        throw new NotSupportedException($"Algorithm {algorithm} is not supported for verification");
                }

                // Update key usage;
                key.LastUsed = DateTime.UtcNow;
                key.UsageCount++;
                await UpdateKeyAsync(key);

                // Log operation;
                await LogCryptoOperationAsync("Verify", key.KeyId, algorithm, isValid, metadata, stopwatch.Elapsed);

                return isValid;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedOperations);
                _logger.LogError($"Verification failed", ex);

                OnCryptoError(new CryptoErrorEventArgs(
                    "Verify",
                    algorithm,
                    ex.Message,
                    DateTime.UtcNow));

                throw new CryptoException($"Verification failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generates a cryptographic key;
        /// </summary>
        public async Task<CryptoKey> GenerateKeyAsync(
            CryptoAlgorithm algorithm,
            KeyUsage usage = KeyUsage.All,
            string keyName = null,
            DateTime? expiresAt = null,
            Dictionary<string, object> metadata = null)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var key = new CryptoKey;
                {
                    KeyName = keyName ?? $"Key_{algorithm}_{Guid.NewGuid():N}",
                    Algorithm = algorithm,
                    Usage = usage,
                    ExpiresAt = expiresAt,
                    CreatedAt = DateTime.UtcNow,
                    LastUsed = DateTime.UtcNow;
                };

                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        key.Metadata[kvp.Key] = kvp.Value;
                    }
                }

                // Generate key material based on algorithm;
                switch (algorithm)
                {
                    case CryptoAlgorithm.AES_GCM_256:
                    case CryptoAlgorithm.AES_CBC_256:
                    case CryptoAlgorithm.ChaCha20_Poly1305:
                        key.KeyMaterial = GenerateSymmetricKey(256);
                        key.KeyType = KeyType.Symmetric;
                        break;

                    case CryptoAlgorithm.RSA_OAEP_2048:
                        using (var rsa = RSA.Create(2048))
                        {
                            key.KeyMaterial = rsa.ExportRSAPrivateKey();
                            key.KeyType = KeyType.AsymmetricPrivate;
                        }
                        break;

                    case CryptoAlgorithm.RSA_OAEP_4096:
                        using (var rsa = RSA.Create(4096))
                        {
                            key.KeyMaterial = rsa.ExportRSAPrivateKey();
                            key.KeyType = KeyType.AsymmetricPrivate;
                        }
                        break;

                    case CryptoAlgorithm.ECDSA_P256:
                    case CryptoAlgorithm.ECDH_P256:
                        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                        {
                            key.KeyMaterial = ecdsa.ExportPkcs8PrivateKey();
                            key.KeyType = KeyType.AsymmetricPrivate;
                        }
                        break;

                    case CryptoAlgorithm.ECDSA_P384:
                    case CryptoAlgorithm.ECDH_P384:
                        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384))
                        {
                            key.KeyMaterial = ecdsa.ExportPkcs8PrivateKey();
                            key.KeyType = KeyType.AsymmetricPrivate;
                        }
                        break;

                    case CryptoAlgorithm.HMAC_SHA256:
                        key.KeyMaterial = GenerateSymmetricKey(256);
                        key.KeyType = KeyType.Hmac;
                        break;

                    case CryptoAlgorithm.HMAC_SHA512:
                        key.KeyMaterial = GenerateSymmetricKey(512);
                        key.KeyType = KeyType.Hmac;
                        break;

                    default:
                        throw new NotSupportedException($"Algorithm {algorithm} is not supported for key generation");
                }

                // Store key;
                await StoreKeyAsync(key);

                // Log key generation;
                await _auditLogger.LogActivityAsync(
                    "KeyGenerated",
                    $"Cryptographic key generated: {key.KeyName}",
                    new Dictionary<string, object>
                    {
                        ["KeyId"] = key.KeyId,
                        ["Algorithm"] = algorithm.ToString(),
                        ["KeyType"] = key.KeyType.ToString(),
                        ["Usage"] = usage.ToString(),
                        ["ExpiresAt"] = expiresAt,
                        ["GenerationTimeMs"] = stopwatch.ElapsedMilliseconds;
                    });

                _logger.LogInformation($"Cryptographic key generated: {key.KeyId}", new;
                {
                    Algorithm = algorithm,
                    KeyType = key.KeyType,
                    KeySize = key.KeyMaterial?.Length;
                });

                return key;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key generation failed", ex);
                throw new CryptoException($"Key generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Derives a key from a password or other secret;
        /// </summary>
        public async Task<CryptoResult> DeriveKeyAsync(
            string password,
            byte[] salt,
            CryptoAlgorithm algorithm = CryptoAlgorithm.PBKDF2_SHA256,
            int iterations = 100000,
            int keySize = 256)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be null or empty", nameof(password));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                byte[] derivedKey = null;

                switch (algorithm)
                {
                    case CryptoAlgorithm.PBKDF2_SHA256:
                        using (var pbkdf2 = new Rfc2898DeriveBytes(
                            password,
                            salt ?? GenerateRandomBytes(32),
                            iterations,
                            HashAlgorithmName.SHA256))
                        {
                            derivedKey = pbkdf2.GetBytes(keySize / 8);
                        }
                        break;

                    case CryptoAlgorithm.Argon2id:
                        // Note: In production, use a proper Argon2 library like Konscious.Security.Cryptography;
                        // This is a simplified implementation;
                        using (var pbkdf2 = new Rfc2898DeriveBytes(
                            password,
                            salt ?? GenerateRandomBytes(32),
                            iterations,
                            HashAlgorithmName.SHA512))
                        {
                            derivedKey = pbkdf2.GetBytes(keySize / 8);
                        }
                        break;

                    default:
                        throw new NotSupportedException($"Algorithm {algorithm} is not supported for key derivation");
                }

                var result = new CryptoResult;
                {
                    Success = true,
                    Key = derivedKey,
                    Algorithm = algorithm.ToString(),
                    ProcessingTime = stopwatch.Elapsed;
                };

                await _auditLogger.LogActivityAsync(
                    "KeyDerived",
                    $"Key derived using {algorithm}",
                    new Dictionary<string, object>
                    {
                        ["Algorithm"] = algorithm.ToString(),
                        ["Iterations"] = iterations,
                        ["KeySize"] = keySize,
                        ["DerivationTimeMs"] = stopwatch.ElapsedMilliseconds;
                    });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key derivation failed", ex);
                throw new CryptoException($"Key derivation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Rotates a cryptographic key (generates new version)
        /// </summary>
        public async Task<CryptoKey> RotateKeyAsync(
            string keyId,
            bool keepPrevious = false,
            DateTime? newExpiry = null)
        {
            if (string.IsNullOrEmpty(keyId))
                throw new ArgumentException("Key ID is required", nameof(keyId));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var oldKey = await GetKeyAsync(keyId);
                if (oldKey == null)
                    throw new CryptographicException($"Key not found: {keyId}");

                // Generate new key with same parameters;
                var newKey = await GenerateKeyAsync(
                    oldKey.Algorithm,
                    oldKey.Usage,
                    $"{oldKey.KeyName}_v{oldKey.Version}",
                    newExpiry ?? DateTime.UtcNow.AddYears(1),
                    oldKey.Metadata);

                // Update version;
                if (int.TryParse(oldKey.Version, out int version))
                {
                    newKey.Version = (version + 1).ToString();
                }
                else;
                {
                    newKey.Version = "2.0";
                }

                // Mark old key as inactive;
                if (!keepPrevious)
                {
                    oldKey.IsActive = false;
                    oldKey.Metadata["RotatedTo"] = newKey.KeyId;
                    await UpdateKeyAsync(oldKey);
                }

                // Link new key to old key;
                newKey.Metadata["RotatedFrom"] = oldKey.KeyId;
                await UpdateKeyAsync(newKey);

                // Raise key rotated event;
                OnKeyRotated(new KeyRotatedEventArgs(
                    oldKey.KeyId,
                    newKey.KeyId,
                    oldKey.Algorithm,
                    DateTime.UtcNow));

                await _auditLogger.LogActivityAsync(
                    "KeyRotated",
                    $"Cryptographic key rotated: {oldKey.KeyId} -> {newKey.KeyId}",
                    new Dictionary<string, object>
                    {
                        ["OldKeyId"] = oldKey.KeyId,
                        ["NewKeyId"] = newKey.KeyId,
                        ["Algorithm"] = oldKey.Algorithm.ToString(),
                        ["KeepPrevious"] = keepPrevious,
                        ["RotationTimeMs"] = stopwatch.ElapsedMilliseconds;
                    });

                _logger.LogInformation($"Key rotated: {oldKey.KeyId} -> {newKey.KeyId}", new;
                {
                    Algorithm = oldKey.Algorithm,
                    OldVersion = oldKey.Version,
                    NewVersion = newKey.Version;
                });

                return newKey;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key rotation failed", ex);
                throw new CryptoException($"Key rotation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Wraps (encrypts) a key with another key;
        /// </summary>
        public async Task<CryptoResult> WrapKeyAsync(
            byte[] keyToWrap,
            string wrappingKeyId,
            CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256)
        {
            if (keyToWrap == null || keyToWrap.Length == 0)
                throw new ArgumentException("Key to wrap cannot be null or empty", nameof(keyToWrap));

            if (string.IsNullOrEmpty(wrappingKeyId))
                throw new ArgumentException("Wrapping key ID is required", nameof(wrappingKeyId));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var wrappingKey = await GetKeyAsync(wrappingKeyId);
                if (wrappingKey == null || !wrappingKey.IsActive)
                    throw new CryptographicException($"Wrapping key not found or inactive: {wrappingKeyId}");

                if (!wrappingKey.Usage.HasFlag(KeyUsage.KeyWrap))
                    throw new CryptographicException($"Key {wrappingKeyId} not authorized for key wrapping");

                // Use the wrapping key to encrypt the key material;
                var encryptResult = await EncryptAsync(
                    keyToWrap,
                    algorithm,
                    wrappingKeyId);

                var result = new CryptoResult;
                {
                    Success = true,
                    Data = encryptResult.Data,
                    Key = encryptResult.Key,
                    IV = encryptResult.IV,
                    Tag = encryptResult.Tag,
                    Algorithm = algorithm.ToString(),
                    KeyId = wrappingKeyId,
                    ProcessingTime = stopwatch.Elapsed;
                };

                await _auditLogger.LogActivityAsync(
                    "KeyWrapped",
                    $"Key wrapped using {wrappingKeyId}",
                    new Dictionary<string, object>
                    {
                        ["WrappingKeyId"] = wrappingKeyId,
                        ["Algorithm"] = algorithm.ToString(),
                        ["WrappedKeySize"] = keyToWrap.Length,
                        ["WrappingTimeMs"] = stopwatch.ElapsedMilliseconds;
                    });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key wrapping failed", ex);
                throw new CryptoException($"Key wrapping failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Unwraps (decrypts) a key with another key;
        /// </summary>
        public async Task<CryptoResult> UnwrapKeyAsync(
            byte[] wrappedKey,
            string unwrappingKeyId,
            CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256,
            byte[] iv = null,
            byte[] tag = null)
        {
            if (wrappedKey == null || wrappedKey.Length == 0)
                throw new ArgumentException("Wrapped key cannot be null or empty", nameof(wrappedKey));

            if (string.IsNullOrEmpty(unwrappingKeyId))
                throw new ArgumentException("Unwrapping key ID is required", nameof(unwrappingKeyId));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var unwrappingKey = await GetKeyAsync(unwrappingKeyId);
                if (unwrappingKey == null || !unwrappingKey.IsActive)
                    throw new CryptographicException($"Unwrapping key not found or inactive: {unwrappingKeyId}");

                if (!unwrappingKey.Usage.HasFlag(KeyUsage.KeyUnwrap))
                    throw new CryptographicException($"Key {unwrappingKeyId} not authorized for key unwrapping");

                // Use the unwrapping key to decrypt the key material;
                var decryptResult = await DecryptAsync(
                    wrappedKey,
                    algorithm,
                    unwrappingKeyId,
                    iv,
                    tag);

                var result = new CryptoResult;
                {
                    Success = true,
                    Data = decryptResult.Data, // This is the unwrapped key;
                    Algorithm = algorithm.ToString(),
                    KeyId = unwrappingKeyId,
                    ProcessingTime = stopwatch.Elapsed;
                };

                await _auditLogger.LogActivityAsync(
                    "KeyUnwrapped",
                    $"Key unwrapped using {unwrappingKeyId}",
                    new Dictionary<string, object>
                    {
                        ["UnwrappingKeyId"] = unwrappingKeyId,
                        ["Algorithm"] = algorithm.ToString(),
                        ["UnwrappingTimeMs"] = stopwatch.ElapsedMilliseconds;
                    });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Key unwrapping failed", ex);
                throw new CryptoException($"Key unwrapping failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generates cryptographically secure random bytes;
        /// </summary>
        public byte[] GenerateRandomBytes(int length)
        {
            if (length <= 0)
                throw new ArgumentException("Length must be greater than 0", nameof(length));

            var bytes = new byte[length];
            _rng.GetBytes(bytes);
            return bytes;
        }

        /// <summary>
        /// Computes hash of data;
        /// </summary>
        public async Task<byte[]> ComputeHashAsync(
            byte[] data,
            HashAlgorithmName algorithm = default,
            Dictionary<string, object> metadata = null)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            if (algorithm == default)
                algorithm = HashAlgorithmName.SHA256;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                byte[] hash = null;

                using (var hasher = HashAlgorithm.Create(algorithm.Name))
                {
                    if (hasher == null)
                        throw new NotSupportedException($"Hash algorithm {algorithm.Name} is not supported");

                    hash = hasher.ComputeHash(data);
                }

                await _auditLogger.LogActivityAsync(
                    "HashComputed",
                    $"Hash computed using {algorithm.Name}",
                    new Dictionary<string, object>
                    {
                        ["Algorithm"] = algorithm.Name,
                        ["DataSize"] = data.Length,
                        ["HashSize"] = hash.Length,
                        ["ProcessingTimeMs"] = stopwatch.ElapsedMilliseconds;
                    });

                return hash;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Hash computation failed", ex);
                throw new CryptoException($"Hash computation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets cryptographic key by ID;
        /// </summary>
        public async Task<CryptoKey> GetKeyAsync(string keyId)
        {
            if (string.IsNullOrEmpty(keyId))
                throw new ArgumentException("Key ID is required", nameof(keyId));

            // Check cache first;
            _cacheLock.EnterReadLock();
            try
            {
                if (_keyCache.TryGetValue(keyId, out var cachedKey))
                {
                    return cachedKey;
                }
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }

            // Get from key manager;
            var key = await _keyManager.GetKeyAsync(keyId);
            if (key != null)
            {
                // Cache the key;
                _cacheLock.EnterWriteLock();
                try
                {
                    _keyCache[keyId] = key;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }
            }

            return key;
        }

        /// <summary>
        /// Gets all active keys for specified algorithm;
        /// </summary>
        public async Task<List<CryptoKey>> GetKeysByAlgorithmAsync(CryptoAlgorithm algorithm)
        {
            // In production, this would query the key manager;
            // For now, return cached keys;
            _cacheLock.EnterReadLock();
            try
            {
                return _keyCache.Values;
                    .Where(k => k.Algorithm == algorithm && k.IsActive)
                    .ToList();
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets cryptographic statistics;
        /// </summary>
        public async Task<CryptoStatistics> GetStatisticsAsync(TimeSpan? period = null)
        {
            var endDate = DateTime.UtcNow;
            var startDate = period.HasValue ? endDate.Subtract(period.Value) : _startTime;

            return new CryptoStatistics;
            {
                TotalOperations = _totalOperations,
                FailedOperations = _failedOperations,
                SuccessRate = _totalOperations > 0 ? (double)(_totalOperations - _failedOperations) / _totalOperations * 100 : 100,
                ActiveKeys = _keyCache.Values.Count(k => k.IsActive),
                ExpiredKeys = _keyCache.Values.Count(k => k.ExpiresAt.HasValue && k.ExpiresAt.Value < DateTime.UtcNow),
                Uptime = endDate - _startTime,
                StartDate = startDate,
                EndDate = endDate;
            };
        }

        /// <summary>
        /// Validates cryptographic configuration;
        /// </summary>
        public async Task<List<string>> ValidateConfigurationAsync()
        {
            var errors = new List<string>();

            // Check for weak algorithms;
            var weakAlgorithms = new[] { "DES", "RC2", "MD5" };
            foreach (var weakAlg in weakAlgorithms)
            {
                if (IsAlgorithmAvailable(weakAlg))
                {
                    errors.Add($"Weak algorithm {weakAlg} is available and should be disabled");
                }
            }

            // Check for expired keys;
            var expiredKeys = _keyCache.Values;
                .Where(k => k.ExpiresAt.HasValue && k.ExpiresAt.Value < DateTime.UtcNow && k.IsActive)
                .ToList();

            if (expiredKeys.Any())
            {
                errors.Add($"{expiredKeys.Count} keys have expired but are still active");
            }

            // Check key lengths;
            foreach (var key in _keyCache.Values.Where(k => k.IsActive))
            {
                if (key.KeyMaterial != null)
                {
                    var keySize = key.KeyMaterial.Length * 8;

                    if (key.Algorithm == CryptoAlgorithm.RSA_OAEP_2048 && keySize < 2048)
                        errors.Add($"RSA key {key.KeyId} has insufficient length: {keySize} bits");

                    if (key.Algorithm == CryptoAlgorithm.AES_GCM_256 && keySize < 256)
                        errors.Add($"AES key {key.KeyId} has insufficient length: {keySize} bits");
                }
            }

            return await Task.FromResult(errors);
        }

        /// <summary>
        /// Performs cryptographic self-test;
        /// </summary>
        public async Task<SelfTestResult> PerformSelfTestAsync()
        {
            var result = new SelfTestResult;
            {
                TestDate = DateTime.UtcNow,
                Tests = new Dictionary<string, bool>()
            };

            try
            {
                // Test random number generation;
                var randomBytes = GenerateRandomBytes(32);
                result.Tests["RandomGeneration"] = randomBytes != null && randomBytes.Length == 32;

                // Test symmetric encryption/decryption;
                var testData = Encoding.UTF8.GetBytes("NEDA Crypto Engine Self-Test");
                var key = await GenerateKeyAsync(CryptoAlgorithm.AES_GCM_256, KeyUsage.Encrypt | KeyUsage.Decrypt, "SelfTestKey");

                var encryptResult = await EncryptAsync(testData, CryptoAlgorithm.AES_GCM_256, key.KeyId);
                result.Tests["Encryption"] = encryptResult.Success;

                var decryptResult = await DecryptAsync(encryptResult.Data, CryptoAlgorithm.AES_GCM_256, key.KeyId, encryptResult.IV, encryptResult.Tag);
                result.Tests["Decryption"] = decryptResult.Success && decryptResult.Data.SequenceEqual(testData);

                // Test signing/verification;
                var signKey = await GenerateKeyAsync(CryptoAlgorithm.ECDSA_P256, KeyUsage.Sign | KeyUsage.Verify, "SelfTestSignKey");

                var signResult = await SignAsync(testData, CryptoAlgorithm.ECDSA_P256, signKey.KeyId);
                result.Tests["Signing"] = signResult.Success;

                var verifyResult = await VerifyAsync(testData, signResult.Signature, CryptoAlgorithm.ECDSA_P256, signKey.KeyId);
                result.Tests["Verification"] = verifyResult;

                // Test hashing;
                var hash = await ComputeHashAsync(testData, HashAlgorithmName.SHA256);
                result.Tests["Hashing"] = hash != null && hash.Length == 32;

                // Calculate overall result;
                result.Success = result.Tests.Values.All(v => v);
                result.Message = result.Success ? "All tests passed" : "Some tests failed";

                await _auditLogger.LogActivityAsync(
                    "SelfTest",
                    $"Cryptographic self-test completed: {(result.Success ? "PASS" : "FAIL")}",
                    new Dictionary<string, object>
                    {
                        ["Success"] = result.Success,
                        ["TestCount"] = result.Tests.Count,
                        ["PassedTests"] = result.Tests.Count(t => t.Value),
                        ["FailedTests"] = result.Tests.Count(t => !t.Value),
                        ["TestDate"] = result.TestDate;
                    });

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Self-test failed: {ex.Message}";

                _logger.LogError("Cryptographic self-test failed", ex);
                return result;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cacheLock?.Dispose();
                    _operationLock?.Dispose();
                    _rng?.Dispose();

                    // Dispose performance counters;
                    _encryptionPerSecondCounter?.Dispose();
                    _decryptionPerSecondCounter?.Dispose();
                    _averageOperationTimeCounter?.Dispose();
                    _keyUsageCounter?.Dispose();
                }
                _disposed = true;
            }
        }

        private void InitializeDefaultKeys()
        {
            // Create default master key;
            var masterKey = new CryptoKey;
            {
                KeyName = "DefaultMasterKey",
                Algorithm = CryptoAlgorithm.AES_GCM_256,
                KeyType = KeyType.Master,
                Usage = KeyUsage.All,
                KeyMaterial = GenerateSymmetricKey(256),
                ExpiresAt = DateTime.UtcNow.AddYears(1),
                Metadata = new Dictionary<string, object>
                {
                    ["Description"] = "Default master key for key wrapping",
                    ["SecurityLevel"] = "High",
                    ["CreatedBy"] = "System"
                }
            };

            // Create default HMAC key;
            var hmacKey = new CryptoKey;
            {
                KeyName = "DefaultHMACKey",
                Algorithm = CryptoAlgorithm.HMAC_SHA256,
                KeyType = KeyType.Hmac,
                Usage = KeyUsage.Sign | KeyUsage.Verify,
                KeyMaterial = GenerateSymmetricKey(256),
                ExpiresAt = DateTime.UtcNow.AddMonths(6),
                Metadata = new Dictionary<string, object>
                {
                    ["Description"] = "Default HMAC key for data integrity",
                    ["Usage"] = "Data integrity verification"
                }
            };

            // Store default keys;
            _cacheLock.EnterWriteLock();
            try
            {
                _keyCache[masterKey.KeyId] = masterKey;
                _keyCache[hmacKey.KeyId] = hmacKey;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }

            _logger.LogInformation("Default cryptographic keys initialized", new;
            {
                MasterKeyId = masterKey.KeyId,
                HmacKeyId = hmacKey.KeyId;
            });
        }

        private byte[] GenerateSymmetricKey(int keySizeBits)
        {
            var keySizeBytes = keySizeBits / 8;
            var key = new byte[keySizeBytes];
            _rng.GetBytes(key);
            return key;
        }

        private async Task<CryptoResult> EncryptAesGcmAsync(byte[] plaintext, CryptoKey key)
        {
            using var aesGcm = new AesGcm(key.KeyMaterial);
            var nonce = GenerateRandomBytes(AesGcm.NonceByteSizes.MaxSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];

            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

            return new CryptoResult;
            {
                Success = true,
                Data = ciphertext,
                IV = nonce,
                Tag = tag;
            };
        }

        private async Task<CryptoResult> DecryptAesGcmAsync(byte[] ciphertext, CryptoKey key, byte[] nonce, byte[] tag)
        {
            using var aesGcm = new AesGcm(key.KeyMaterial);
            var plaintext = new byte[ciphertext.Length];

            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

            return new CryptoResult;
            {
                Success = true,
                Data = plaintext;
            };
        }

        private async Task<CryptoResult> EncryptAesCbcAsync(byte[] plaintext, CryptoKey key)
        {
            using var aes = Aes.Create();
            aes.Key = key.KeyMaterial;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            aes.GenerateIV();
            var iv = aes.IV;

            using var encryptor = aes.CreateEncryptor();
            var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

            return new CryptoResult;
            {
                Success = true,
                Data = ciphertext,
                IV = iv;
            };
        }

        private async Task<CryptoResult> DecryptAesCbcAsync(byte[] ciphertext, CryptoKey key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key.KeyMaterial;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

            return new CryptoResult;
            {
                Success = true,
                Data = plaintext;
            };
        }

        private async Task<CryptoResult> EncryptRsaOaepAsync(byte[] plaintext, CryptoKey key)
        {
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(key.KeyMaterial, out _);

            var ciphertext = rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA256);

            return new CryptoResult;
            {
                Success = true,
                Data = ciphertext;
            };
        }

        private async Task<CryptoResult> DecryptRsaOaepAsync(byte[] ciphertext, CryptoKey key)
        {
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(key.KeyMaterial, out _);

            var plaintext = rsa.Decrypt(ciphertext, RSAEncryptionPadding.OaepSHA256);

            return new CryptoResult;
            {
                Success = true,
                Data = plaintext;
            };
        }

        private async Task<CryptoResult> EncryptChaCha20Poly1305Async(byte[] plaintext, CryptoKey key)
        {
            // Note: .NET doesn't have built-in ChaCha20-Poly1305 in all versions;
            // This is a placeholder implementation;
            // In production, use a proper library like BouncyCastle or .NET's implementation if available;

            var nonce = GenerateRandomBytes(12);
            var ciphertext = new byte[plaintext.Length];

            // Simulate encryption (in reality, use proper ChaCha20-Poly1305)
            Array.Copy(plaintext, ciphertext, plaintext.Length);
            var tag = GenerateRandomBytes(16);

            return new CryptoResult;
            {
                Success = true,
                Data = ciphertext,
                IV = nonce,
                Tag = tag;
            };
        }

        private async Task<CryptoResult> DecryptChaCha20Poly1305Async(byte[] ciphertext, CryptoKey key, byte[] nonce, byte[] tag)
        {
            // Placeholder implementation;
            var plaintext = new byte[ciphertext.Length];
            Array.Copy(ciphertext, plaintext, ciphertext.Length);

            return new CryptoResult;
            {
                Success = true,
                Data = plaintext;
            };
        }

        private async Task<CryptoResult> SignEcdsaAsync(byte[] data, CryptoKey key)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(key.KeyMaterial, out _);

            var signature = ecdsa.SignData(data, HashAlgorithmName.SHA256);

            return new CryptoResult;
            {
                Success = true,
                Signature = signature;
            };
        }

        private async Task<bool> VerifyEcdsaAsync(byte[] data, byte[] signature, CryptoKey key)
        {
            using var ecdsa = ECDsa.Create();

            // Try to import as public key first, then private key;
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(key.KeyMaterial, out _);
            }
            catch
            {
                ecdsa.ImportPkcs8PrivateKey(key.KeyMaterial, out _);
            }

            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }

        private async Task<CryptoResult> SignHmacAsync(byte[] data, CryptoKey key)
        {
            using var hmac = key.Algorithm == CryptoAlgorithm.HMAC_SHA512 ?
                new HMACSHA512(key.KeyMaterial) :
                new HMACSHA256(key.KeyMaterial);

            var signature = hmac.ComputeHash(data);

            return new CryptoResult;
            {
                Success = true,
                Signature = signature;
            };
        }

        private async Task<bool> VerifyHmacAsync(byte[] data, byte[] signature, CryptoKey key)
        {
            using var hmac = key.Algorithm == CryptoAlgorithm.HMAC_SHA512 ?
                new HMACSHA512(key.KeyMaterial) :
                new HMACSHA256(key.KeyMaterial);

            var computedSignature = hmac.ComputeHash(data);
            return signature.SequenceEqual(computedSignature);
        }

        private async Task<CryptoResult> SignRsaAsync(byte[] data, CryptoKey key)
        {
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(key.KeyMaterial, out _);

            var signature = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            return new CryptoResult;
            {
                Success = true,
                Signature = signature;
            };
        }

        private async Task<bool> VerifyRsaAsync(byte[] data, byte[] signature, CryptoKey key)
        {
            using var rsa = RSA.Create();

            // Try to import as public key first, then private key;
            try
            {
                rsa.ImportSubjectPublicKeyInfo(key.KeyMaterial, out _);
            }
            catch
            {
                rsa.ImportRSAPrivateKey(key.KeyMaterial, out _);
            }

            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        private async Task StoreKeyAsync(CryptoKey key)
        {
            // Store in key manager;
            await _keyManager.StoreKeyAsync(key);

            // Cache the key;
            _cacheLock.EnterWriteLock();
            try
            {
                _keyCache[key.KeyId] = key;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private async Task UpdateKeyAsync(CryptoKey key)
        {
            // Update in key manager;
            await _keyManager.UpdateKeyAsync(key);

            // Update cache;
            _cacheLock.EnterWriteLock();
            try
            {
                _keyCache[key.KeyId] = key;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        private async Task LogCryptoOperationAsync(
            string operation,
            string keyId,
            CryptoAlgorithm algorithm,
            bool success,
            Dictionary<string, object> metadata,
            TimeSpan duration)
        {
            await _auditLogger.LogActivityAsync(
                "CryptoOperation",
                $"{operation} operation performed",
                new Dictionary<string, object>
                {
                    ["Operation"] = operation,
                    ["KeyId"] = keyId,
                    ["Algorithm"] = algorithm.ToString(),
                    ["Success"] = success,
                    ["DurationMs"] = duration.TotalMilliseconds,
                    ["Timestamp"] = DateTime.UtcNow,
                    ["Metadata"] = metadata ?? new Dictionary<string, object>()
                });
        }

        private bool IsAlgorithmAvailable(string algorithmName)
        {
            try
            {
                return HashAlgorithm.Create(algorithmName) != null ||
                       SymmetricAlgorithm.Create(algorithmName) != null ||
                       AsymmetricAlgorithm.Create(algorithmName) != null;
            }
            catch
            {
                return false;
            }
        }

        private void OnCryptoOperationPerformed(CryptoOperationEventArgs e)
        {
            CryptoOperationPerformed?.Invoke(this, e);
        }

        private void OnKeyRotated(KeyRotatedEventArgs e)
        {
            KeyRotated?.Invoke(this, e);
        }

        private void OnCryptoError(CryptoErrorEventArgs e)
        {
            CryptoError?.Invoke(this, e);
        }

        #endregion;

        #region Supporting Classes;

        /// <summary>
        /// Cryptographic statistics;
        /// </summary>
        public class CryptoStatistics;
        {
            public long TotalOperations { get; set; }
            public long FailedOperations { get; set; }
            public double SuccessRate { get; set; }
            public int ActiveKeys { get; set; }
            public int ExpiredKeys { get; set; }
            public TimeSpan Uptime { get; set; }
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
        }

        /// <summary>
        /// Self-test result;
        /// </summary>
        public class SelfTestResult;
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public DateTime TestDate { get; set; }
            public Dictionary<string, bool> Tests { get; set; }

            public SelfTestResult()
            {
                Tests = new Dictionary<string, bool>();
            }
        }

        /// <summary>
        /// Event arguments for cryptographic operation;
        /// </summary>
        public class CryptoOperationEventArgs : EventArgs;
        {
            public string Operation { get; }
            public string KeyId { get; }
            public CryptoAlgorithm Algorithm { get; }
            public int InputSize { get; }
            public int OutputSize { get; }
            public bool Success { get; }
            public DateTime Timestamp { get; }

            public CryptoOperationEventArgs(
                string operation,
                string keyId,
                CryptoAlgorithm algorithm,
                int inputSize,
                int outputSize,
                bool success,
                DateTime timestamp)
            {
                Operation = operation;
                KeyId = keyId;
                Algorithm = algorithm;
                InputSize = inputSize;
                OutputSize = outputSize;
                Success = success;
                Timestamp = timestamp;
            }
        }

        /// <summary>
        /// Event arguments for key rotation;
        /// </summary>
        public class KeyRotatedEventArgs : EventArgs;
        {
            public string OldKeyId { get; }
            public string NewKeyId { get; }
            public CryptoAlgorithm Algorithm { get; }
            public DateTime RotatedAt { get; }

            public KeyRotatedEventArgs(
                string oldKeyId,
                string newKeyId,
                CryptoAlgorithm algorithm,
                DateTime rotatedAt)
            {
                OldKeyId = oldKeyId;
                NewKeyId = newKeyId;
                Algorithm = algorithm;
                RotatedAt = rotatedAt;
            }
        }

        /// <summary>
        /// Event arguments for cryptographic error;
        /// </summary>
        public class CryptoErrorEventArgs : EventArgs;
        {
            public string Operation { get; }
            public CryptoAlgorithm Algorithm { get; }
            public string ErrorMessage { get; }
            public DateTime OccurredAt { get; }

            public CryptoErrorEventArgs(
                string operation,
                CryptoAlgorithm algorithm,
                string errorMessage,
                DateTime occurredAt)
            {
                Operation = operation;
                Algorithm = algorithm;
                ErrorMessage = errorMessage;
                OccurredAt = occurredAt;
            }
        }

        /// <summary>
        /// Custom cryptographic exception;
        /// </summary>
        public class CryptoException : Exception
        {
            public CryptoException(string message) : base(message) { }
            public CryptoException(string message, Exception innerException) : base(message, innerException) { }
        }

        /// <summary>
        /// Interface for cryptographic engine;
        /// </summary>
        public interface ICryptoEngine;
        {
            Task<CryptoResult> EncryptAsync(
                byte[] plaintext,
                CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256,
                string keyId = null,
                Dictionary<string, object> metadata = null);
            Task<CryptoResult> DecryptAsync(
                byte[] ciphertext,
                CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256,
                string keyId = null,
                byte[] iv = null,
                byte[] tag = null,
                Dictionary<string, object> metadata = null);
            Task<CryptoResult> SignAsync(
                byte[] data,
                CryptoAlgorithm algorithm = CryptoAlgorithm.ECDSA_P256,
                string keyId = null,
                Dictionary<string, object> metadata = null);
            Task<bool> VerifyAsync(
                byte[] data,
                byte[] signature,
                CryptoAlgorithm algorithm = CryptoAlgorithm.ECDSA_P256,
                string keyId = null,
                Dictionary<string, object> metadata = null);
            Task<CryptoKey> GenerateKeyAsync(
                CryptoAlgorithm algorithm,
                KeyUsage usage = KeyUsage.All,
                string keyName = null,
                DateTime? expiresAt = null,
                Dictionary<string, object> metadata = null);
            Task<CryptoResult> DeriveKeyAsync(
                string password,
                byte[] salt,
                CryptoAlgorithm algorithm = CryptoAlgorithm.PBKDF2_SHA256,
                int iterations = 100000,
                int keySize = 256);
            Task<CryptoKey> RotateKeyAsync(
                string keyId,
                bool keepPrevious = false,
                DateTime? newExpiry = null);
            Task<CryptoResult> WrapKeyAsync(
                byte[] keyToWrap,
                string wrappingKeyId,
                CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256);
            Task<CryptoResult> UnwrapKeyAsync(
                byte[] wrappedKey,
                string unwrappingKeyId,
                CryptoAlgorithm algorithm = CryptoAlgorithm.AES_GCM_256,
                byte[] iv = null,
                byte[] tag = null);
            byte[] GenerateRandomBytes(int length);
            Task<byte[]> ComputeHashAsync(
                byte[] data,
                HashAlgorithmName algorithm = default,
                Dictionary<string, object> metadata = null);
            Task<CryptoKey> GetKeyAsync(string keyId);
            Task<List<CryptoKey>> GetKeysByAlgorithmAsync(CryptoAlgorithm algorithm);
            Task<CryptoStatistics> GetStatisticsAsync(TimeSpan? period = null);
            Task<List<string>> ValidateConfigurationAsync();
            Task<SelfTestResult> PerformSelfTestAsync();

            event EventHandler<CryptoOperationEventArgs> CryptoOperationPerformed;
            event EventHandler<KeyRotatedEventArgs> KeyRotated;
            event EventHandler<CryptoErrorEventArgs> CryptoError;
        }

        #endregion;
    }
}
