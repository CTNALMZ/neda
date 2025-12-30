using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Logging;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.SecurityModules.Encryption;
using NEDA.SecurityModules.Firewall;
using NEDA.SecurityModules.Monitoring;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Schema;

namespace NEDA.SecurityModules.Manifest;
{
    /// <summary>
    /// Manifest integrity verification result;
    /// </summary>
    public enum ManifestIntegrityStatus;
    {
        Valid,
        InvalidSignature,
        InvalidHash,
        Tampered,
        Expired,
        Revoked,
        Unknown;
    }

    /// <summary>
    /// Manifest load options;
    /// </summary>
    [Flags]
    public enum ManifestLoadOptions;
    {
        None = 0,
        ValidateSchema = 1,
        VerifySignature = 2,
        CheckRevocation = 4,
        ValidatePermissions = 8,
        CacheInMemory = 16,
        AutoReload = 32,
        StrictMode = ValidateSchema | VerifySignature | CheckRevocation;
    }

    /// <summary>
    /// Manifest metadata;
    /// </summary>
    public class ManifestMetadata;
    {
        [JsonPropertyName("manifestId")]
        public string ManifestId { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("issuedDate")]
        public DateTime IssuedDate { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("validFrom")]
        public DateTime ValidFrom { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("validUntil")]
        public DateTime ValidUntil { get; set; } = DateTime.UtcNow.AddYears(1);

        [JsonPropertyName("signatureAlgorithm")]
        public string SignatureAlgorithm { get; set; } = "SHA256";

        [JsonPropertyName("hashAlgorithm")]
        public string HashAlgorithm { get; set; } = "SHA256";

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = new List<string>();

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new List<string>();

        [JsonPropertyName("compatibility")]
        public Dictionary<string, string> Compatibility { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("revision")]
        public int Revision { get; set; } = 1;

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; } = true;

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Security manifest with metadata and content;
    /// </summary>
    public class SecurityManifest;
    {
        [JsonPropertyName("metadata")]
        public ManifestMetadata Metadata { get; set; } = new ManifestMetadata();

        [JsonPropertyName("firewallRules")]
        public List<FirewallRule> FirewallRules { get; set; } = new List<FirewallRule>();

        [JsonPropertyName("accessPolicies")]
        public List<AccessPolicy> AccessPolicies { get; set; } = new List<AccessPolicy>();

        [JsonPropertyName("encryptionKeys")]
        public List<KeyDefinition> EncryptionKeys { get; set; } = new List<KeyDefinition>();

        [JsonPropertyName("auditSettings")]
        public AuditSettings AuditSettings { get; set; } = new AuditSettings();

        [JsonPropertyName("complianceRules")]
        public List<ComplianceRule> ComplianceRules { get; set; } = new List<ComplianceRule>();

        [JsonPropertyName("monitoringRules")]
        public List<MonitoringRule> MonitoringRules { get; set; } = new List<MonitoringRule>();

        [JsonPropertyName("customSections")]
        public Dictionary<string, object> CustomSections { get; set; } = new Dictionary<string, object>();

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("certificateThumbprint")]
        public string CertificateThumbprint { get; set; } = string.Empty;
    }

    /// <summary>
    /// Access policy definition;
    /// </summary>
    public class AccessPolicy;
    {
        public string PolicyId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> AllowedUsers { get; set; } = new List<string>();
        public List<string> AllowedGroups { get; set; } = new List<string>();
        public List<string> AllowedRoles { get; set; } = new List<string>();
        public List<string> DeniedUsers { get; set; } = new List<string>();
        public List<string> AllowedIPs { get; set; } = new List<string>();
        public List<string> DeniedIPs { get; set; } = new List<string>();
        public Dictionary<string, string> Conditions { get; set; } = new Dictionary<string, string>();
        public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
        public DateTime EffectiveUntil { get; set; } = DateTime.UtcNow.AddYears(1);
        public int Priority { get; set; } = 100;
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// Encryption key definition;
    /// </summary>
    public class KeyDefinition;
    {
        public string KeyId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Algorithm { get; set; } = "AES-256";
        public string Purpose { get; set; } = string.Empty;
        public string KeyMaterial { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; } = true;
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Audit settings;
    /// </summary>
    public class AuditSettings;
    {
        public bool EnableAuditLogging { get; set; } = true;
        public string LogLevel { get; set; } = "Information";
        public List<string> AuditedEvents { get; set; } = new List<string>();
        public int RetentionDays { get; set; } = 365;
        public bool EncryptLogs { get; set; } = true;
        public bool EnableRealTimeMonitoring { get; set; } = true;
        public List<string> AlertRecipients { get; set; } = new List<string>();
    }

    /// <summary>
    /// Compliance rule;
    /// </summary>
    public class ComplianceRule;
    {
        public string RuleId { get; set; } = Guid.NewGuid().ToString();
        public string Standard { get; set; } = string.Empty;
        public string Requirement { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Controls { get; set; } = new List<string>();
        public bool IsMandatory { get; set; } = true;
        public DateTime? DueDate { get; set; }
        public string Status { get; set; } = "NotStarted";
    }

    /// <summary>
    /// Monitoring rule;
    /// </summary>
    public class MonitoringRule;
    {
        public string RuleId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Metric { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public double Threshold { get; set; }
        public string Severity { get; set; } = "Medium";
        public List<string> Actions { get; set; } = new List<string>();
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// Manifest validation result;
    /// </summary>
    public class ManifestValidationResult;
    {
        public bool IsValid { get; set; }
        public ManifestIntegrityStatus IntegrityStatus { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public DateTime ValidationTime { get; set; } = DateTime.UtcNow;
        public string ManifestId { get; set; } = string.Empty;
        public string ManifestName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Manifest change event arguments;
    /// </summary>
    public class ManifestChangedEventArgs : EventArgs;
    {
        public string ManifestId { get; }
        public string ManifestPath { get; }
        public ChangeType ChangeType { get; }
        public DateTime ChangeTime { get; } = DateTime.UtcNow;
        public SecurityManifest OldManifest { get; }
        public SecurityManifest NewManifest { get; }

        public ManifestChangedEventArgs(
            string manifestId,
            string manifestPath,
            ChangeType changeType,
            SecurityManifest oldManifest,
            SecurityManifest newManifest)
        {
            ManifestId = manifestId;
            ManifestPath = manifestPath;
            ChangeType = changeType;
            OldManifest = oldManifest;
            NewManifest = newManifest;
        }
    }

    /// <summary>
    /// Change types for manifest monitoring;
    /// </summary>
    public enum ChangeType;
    {
        Created,
        Modified,
        Deleted,
        Renamed,
        Invalidated;
    }

    /// <summary>
    /// Manifest configuration;
    /// </summary>
    public class ManifestConfiguration;
    {
        public string ManifestDirectory { get; set; } = "Manifests";
        public string SchemaDirectory { get; set; } = "Schemas";
        public string BackupDirectory { get; set; } = "Backups";
        public string CertificateStore { get; set; } = "Certificates";
        public TimeSpan FileWatcherInterval { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxBackupCount { get; set; } = 10;
        public bool EnableAutoBackup { get; set; } = true;
        public bool EnableFileWatcher { get; set; } = true;
        public ManifestLoadOptions DefaultLoadOptions { get; set; } = ManifestLoadOptions.StrictMode;
        public List<string> TrustedPublishers { get; set; } = new List<string>();
        public List<string> RequiredPermissions { get; set; } = new List<string>();
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// Advanced manifest management system for security configuration;
    /// Handles loading, validation, verification, and lifecycle management of security manifests;
    /// </summary>
    public class ManifestManager : IDisposable
    {
        private readonly ILogger<ManifestManager> _logger;
        private readonly ManifestValidator _validator;
        private readonly CryptoEngine _cryptoEngine;
        private readonly SecurityMonitor _securityMonitor;
        private readonly IAuditLogger _auditLogger;
        private readonly IOptions<ManifestConfiguration> _configuration;

        private readonly ConcurrentDictionary<string, SecurityManifest> _manifestCache;
        private readonly ConcurrentDictionary<string, DateTime> _manifestTimestamps;
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileWatchers;
        private readonly ConcurrentDictionary<string, Timer> _refreshTimers;

        private readonly ReaderWriterLockSlim _cacheLock;
        private readonly SemaphoreSlim _loadSemaphore;
        private readonly CancellationTokenSource _cancellationTokenSource;

        private bool _isDisposed;
        private bool _isInitialized;
        private DateTime _startTime = DateTime.UtcNow;
        private int _totalManifestsLoaded;
        private int _failedLoads;
        private int _validationErrors;

        /// <summary>
        /// Event raised when manifest is loaded;
        /// </summary>
        public event EventHandler<SecurityManifest> ManifestLoaded;

        /// <summary>
        /// Event raised when manifest is validated;
        /// </summary>
        public event EventHandler<ManifestValidationResult> ManifestValidated;

        /// <summary>
        /// Event raised when manifest is changed;
        /// </summary>
        public event EventHandler<ManifestChangedEventArgs> ManifestChanged;

        /// <summary>
        /// Event raised when manifest error occurs;
        /// </summary>
        public event EventHandler<Exception> ManifestError;

        /// <summary>
        /// Initializes a new instance of ManifestManager;
        /// </summary>
        public ManifestManager(
            ILogger<ManifestManager> logger,
            ManifestValidator validator,
            CryptoEngine cryptoEngine,
            SecurityMonitor securityMonitor,
            IAuditLogger auditLogger,
            IOptions<ManifestConfiguration> configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _manifestCache = new ConcurrentDictionary<string, SecurityManifest>();
            _manifestTimestamps = new ConcurrentDictionary<string, DateTime>();
            _fileWatchers = new ConcurrentDictionary<string, FileSystemWatcher>();
            _refreshTimers = new ConcurrentDictionary<string, Timer>();

            _cacheLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _loadSemaphore = new SemaphoreSlim(5, 5); // Limit concurrent loads;
            _cancellationTokenSource = new CancellationTokenSource();

            EnsureDirectoriesExist();

            _logger.LogInformation("ManifestManager initialized with configuration: {@Config}",
                _configuration.Value);
        }

        /// <summary>
        /// Initializes the manifest manager and loads all manifests;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Starting ManifestManager initialization...");

                await LoadAllManifestsAsync(cancellationToken);

                if (_configuration.Value.EnableFileWatcher)
                {
                    SetupFileWatchers();
                }

                _isInitialized = true;
                _logger.LogInformation("ManifestManager initialized successfully. Loaded {Count} manifests.",
                    _manifestCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ManifestManager");
                throw new SecurityException("ManifestManager initialization failed", ex);
            }
        }

        /// <summary>
        /// Loads a security manifest from file;
        /// </summary>
        public async Task<SecurityManifest> LoadManifestAsync(
            string filePath,
            ManifestLoadOptions options = ManifestLoadOptions.None,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Manifest file not found: {filePath}", filePath);

            await _loadSemaphore.WaitAsync(cancellationToken);
            try
            {
                var normalizedPath = Path.GetFullPath(filePath);
                var manifestId = GetManifestIdFromPath(normalizedPath);

                // Check cache first;
                if (options.HasFlag(ManifestLoadOptions.CacheInMemory) &&
                    _manifestCache.TryGetValue(manifestId, out var cachedManifest))
                {
                    if (IsManifestFresh(cachedManifest, normalizedPath))
                    {
                        _logger.LogDebug("Returning cached manifest: {ManifestId}", manifestId);
                        return cachedManifest;
                    }
                }

                _logger.LogInformation("Loading manifest from: {FilePath}", normalizedPath);

                // Read and parse manifest;
                var manifestContent = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
                var manifest = await ParseManifestAsync(manifestContent, normalizedPath, cancellationToken);

                // Validate if required;
                if (options != ManifestLoadOptions.None)
                {
                    var validationResult = await ValidateManifestAsync(manifest, options, cancellationToken);

                    if (!validationResult.IsValid && options.HasFlag(ManifestLoadOptions.StrictMode))
                    {
                        throw new SecurityException($"Manifest validation failed: {string.Join(", ", validationResult.Errors)}");
                    }

                    OnManifestValidated(validationResult);
                }

                // Cache if requested;
                if (options.HasFlag(ManifestLoadOptions.CacheInMemory))
                {
                    _manifestCache[manifestId] = manifest;
                    _manifestTimestamps[manifestId] = File.GetLastWriteTimeUtc(normalizedPath);
                }

                // Backup if enabled;
                if (_configuration.Value.EnableAutoBackup)
                {
                    await BackupManifestAsync(manifest, normalizedPath, cancellationToken);
                }

                Interlocked.Increment(ref _totalManifestsLoaded);
                OnManifestLoaded(manifest);

                await LogManifestLoadAsync(manifest, normalizedPath, options);

                return manifest;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedLoads);
                _logger.LogError(ex, "Failed to load manifest from {FilePath}", filePath);
                OnManifestError(ex);
                throw;
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        /// <summary>
        /// Loads all manifests from the configured directory;
        /// </summary>
        public async Task LoadAllManifestsAsync(CancellationToken cancellationToken = default)
        {
            var manifestDir = _configuration.Value.ManifestDirectory;
            if (!Directory.Exists(manifestDir))
            {
                _logger.LogWarning("Manifest directory does not exist: {ManifestDir}", manifestDir);
                return;
            }

            var manifestFiles = Directory.GetFiles(manifestDir, "*.json", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(manifestDir, "*.xml", SearchOption.AllDirectories))
                .ToArray();

            _logger.LogInformation("Found {Count} manifest files to load", manifestFiles.Length);

            var loadTasks = manifestFiles.Select(file =>
                LoadManifestAsync(file, _configuration.Value.DefaultLoadOptions, cancellationToken)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            _logger.LogError(t.Exception, "Failed to load manifest: {File}", file);
                        }
                        return t;
                    }));

            await Task.WhenAll(loadTasks);
        }

        /// <summary>
        /// Saves a manifest to file;
        /// </summary>
        public async Task SaveManifestAsync(
            SecurityManifest manifest,
            string filePath,
            bool signManifest = true,
            CancellationToken cancellationToken = default)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            try
            {
                var normalizedPath = Path.GetFullPath(filePath);
                var directory = Path.GetDirectoryName(normalizedPath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Update manifest metadata;
                manifest.Metadata.Revision++;
                manifest.Metadata.IssuedDate = DateTime.UtcNow;

                // Sign manifest if requested;
                if (signManifest)
                {
                    await SignManifestAsync(manifest, cancellationToken);
                }

                // Serialize to JSON;
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                };

                var manifestJson = JsonSerializer.Serialize(manifest, jsonOptions);

                // Write to file with atomic operation;
                var tempFile = normalizedPath + ".tmp";
                await File.WriteAllTextAsync(tempFile, manifestJson, cancellationToken);

                // Replace original file;
                File.Move(tempFile, normalizedPath, true);

                var manifestId = GetManifestIdFromPath(normalizedPath);

                // Update cache;
                if (_manifestCache.ContainsKey(manifestId))
                {
                    _manifestCache[manifestId] = manifest;
                    _manifestTimestamps[manifestId] = DateTime.UtcNow;
                }

                _logger.LogInformation("Saved manifest to {FilePath}, Revision: {Revision}",
                    normalizedPath, manifest.Metadata.Revision);

                await _auditLogger.LogSecurityEventAsync("ManifestSaved", new;
                {
                    ManifestId = manifest.Metadata.ManifestId,
                    FilePath = normalizedPath,
                    Revision = manifest.Metadata.Revision,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save manifest to {FilePath}", filePath);
                throw;
            }
        }

        /// <summary>
        /// Validates a manifest against schema and security requirements;
        /// </summary>
        public async Task<ManifestValidationResult> ValidateManifestAsync(
            SecurityManifest manifest,
            ManifestLoadOptions options = ManifestLoadOptions.StrictMode,
            CancellationToken cancellationToken = default)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            var result = new ManifestValidationResult;
            {
                ManifestId = manifest.Metadata.ManifestId,
                ManifestName = manifest.Metadata.Name;
            };

            try
            {
                // 1. Validate schema if requested;
                if (options.HasFlag(ManifestLoadOptions.ValidateSchema))
                {
                    var schemaResult = await _validator.ValidateSchemaAsync(manifest, cancellationToken);
                    if (!schemaResult.IsValid)
                    {
                        result.Errors.AddRange(schemaResult.Errors);
                        result.Warnings.AddRange(schemaResult.Warnings);
                    }
                }

                // 2. Verify signature if requested;
                if (options.HasFlag(ManifestLoadOptions.VerifySignature))
                {
                    var integrityResult = await VerifyManifestIntegrityAsync(manifest, cancellationToken);
                    result.IntegrityStatus = integrityResult.Status;

                    if (integrityResult.Status != ManifestIntegrityStatus.Valid)
                    {
                        result.Errors.Add($"Manifest integrity check failed: {integrityResult.Status}");
                    }
                }

                // 3. Check revocation if requested;
                if (options.HasFlag(ManifestLoadOptions.CheckRevocation))
                {
                    if (await IsManifestRevokedAsync(manifest, cancellationToken))
                    {
                        result.IntegrityStatus = ManifestIntegrityStatus.Revoked;
                        result.Errors.Add("Manifest has been revoked");
                    }
                }

                // 4. Check validity dates;
                var now = DateTime.UtcNow;
                if (now < manifest.Metadata.ValidFrom)
                {
                    result.Errors.Add($"Manifest is not yet valid. Valid from: {manifest.Metadata.ValidFrom}");
                }
                if (now > manifest.Metadata.ValidUntil)
                {
                    result.IntegrityStatus = ManifestIntegrityStatus.Expired;
                    result.Errors.Add($"Manifest has expired. Valid until: {manifest.Metadata.ValidUntil}");
                }

                // 5. Validate permissions if requested;
                if (options.HasFlag(ManifestLoadOptions.ValidatePermissions))
                {
                    var permissionErrors = ValidatePermissions(manifest);
                    result.Errors.AddRange(permissionErrors);
                }

                // 6. Check dependencies;
                foreach (var dependency in manifest.Metadata.Dependencies)
                {
                    if (!_manifestCache.ContainsKey(dependency))
                    {
                        result.Warnings.Add($"Missing dependency: {dependency}");
                    }
                }

                result.IsValid = result.Errors.Count == 0 &&
                                result.IntegrityStatus == ManifestIntegrityStatus.Valid;

                if (!result.IsValid)
                {
                    Interlocked.Increment(ref _validationErrors);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate manifest {ManifestId}", manifest.Metadata.ManifestId);
                result.Errors.Add($"Validation error: {ex.Message}");
                result.IsValid = false;
                return result;
            }
        }

        /// <summary>
        /// Gets a manifest by ID;
        /// </summary>
        public SecurityManifest GetManifest(string manifestId)
        {
            if (string.IsNullOrEmpty(manifestId))
                throw new ArgumentException("Manifest ID cannot be null or empty", nameof(manifestId));

            _cacheLock.EnterReadLock();
            try
            {
                return _manifestCache.TryGetValue(manifestId, out var manifest) ? manifest : null;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets all loaded manifests;
        /// </summary>
        public IReadOnlyCollection<SecurityManifest> GetAllManifests()
        {
            _cacheLock.EnterReadLock();
            try
            {
                return _manifestCache.Values.ToList().AsReadOnly();
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets manifests by tag;
        /// </summary>
        public IEnumerable<SecurityManifest> GetManifestsByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                throw new ArgumentException("Tag cannot be null or empty", nameof(tag));

            _cacheLock.EnterReadLock();
            try
            {
                return _manifestCache.Values;
                    .Where(m => m.Metadata.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Checks if manifest is active and valid;
        /// </summary>
        public async Task<bool> IsManifestActiveAsync(string manifestId, CancellationToken cancellationToken = default)
        {
            var manifest = GetManifest(manifestId);
            if (manifest == null)
                return false;

            var validationResult = await ValidateManifestAsync(manifest, ManifestLoadOptions.StrictMode, cancellationToken);
            return validationResult.IsValid && manifest.Metadata.IsActive;
        }

        /// <summary>
        /// Updates a manifest in cache and optionally persists to disk;
        /// </summary>
        public async Task UpdateManifestAsync(
            string manifestId,
            Action<SecurityManifest> updateAction,
            bool persistToDisk = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(manifestId))
                throw new ArgumentException("Manifest ID cannot be null or empty", nameof(manifestId));

            if (updateAction == null)
                throw new ArgumentNullException(nameof(updateAction));

            _cacheLock.EnterWriteLock();
            try
            {
                if (!_manifestCache.TryGetValue(manifestId, out var oldManifest))
                {
                    throw new KeyNotFoundException($"Manifest not found: {manifestId}");
                }

                // Create a deep copy for the old version;
                var oldManifestCopy = CloneManifest(oldManifest);

                // Apply updates;
                updateAction(oldManifest);
                oldManifest.Metadata.Revision++;
                oldManifest.Metadata.IssuedDate = DateTime.UtcNow;

                // Validate updated manifest;
                var validationResult = await ValidateManifestAsync(oldManifest, cancellationToken: cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new InvalidOperationException($"Updated manifest is invalid: {string.Join(", ", validationResult.Errors)}");
                }

                // Find the original file path (this is simplified - in reality you'd track paths)
                var manifestPath = FindManifestPath(manifestId);

                if (persistToDisk && !string.IsNullOrEmpty(manifestPath))
                {
                    await SaveManifestAsync(oldManifest, manifestPath, true, cancellationToken);
                }

                // Update cache;
                _manifestCache[manifestId] = oldManifest;
                _manifestTimestamps[manifestId] = DateTime.UtcNow;

                // Raise change event;
                OnManifestChanged(new ManifestChangedEventArgs(
                    manifestId,
                    manifestPath,
                    ChangeType.Modified,
                    oldManifestCopy,
                    oldManifest;
                ));

                _logger.LogInformation("Updated manifest {ManifestId}, Revision: {Revision}",
                    manifestId, oldManifest.Metadata.Revision);
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Removes a manifest from cache and optionally from disk;
        /// </summary>
        public async Task RemoveManifestAsync(
            string manifestId,
            bool removeFromDisk = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(manifestId))
                throw new ArgumentException("Manifest ID cannot be null or empty", nameof(manifestId));

            _cacheLock.EnterWriteLock();
            try
            {
                if (!_manifestCache.TryRemove(manifestId, out var removedManifest))
                {
                    return;
                }

                _manifestTimestamps.TryRemove(manifestId, out _);

                if (removeFromDisk)
                {
                    var manifestPath = FindManifestPath(manifestId);
                    if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
                    {
                        // Create backup before deletion;
                        await BackupManifestAsync(removedManifest, manifestPath, cancellationToken);
                        File.Delete(manifestPath);
                    }
                }

                // Remove file watcher if exists;
                if (_fileWatchers.TryRemove(manifestId, out var watcher))
                {
                    watcher.Dispose();
                }

                // Remove refresh timer if exists;
                if (_refreshTimers.TryRemove(manifestId, out var timer))
                {
                    timer.Dispose();
                }

                // Raise change event;
                OnManifestChanged(new ManifestChangedEventArgs(
                    manifestId,
                    FindManifestPath(manifestId),
                    ChangeType.Deleted,
                    removedManifest,
                    null;
                ));

                _logger.LogInformation("Removed manifest {ManifestId}", manifestId);
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Clears all manifests from cache;
        /// </summary>
        public void ClearCache()
        {
            _cacheLock.EnterWriteLock();
            try
            {
                _manifestCache.Clear();
                _manifestTimestamps.Clear();

                foreach (var watcher in _fileWatchers.Values)
                {
                    watcher.Dispose();
                }
                _fileWatchers.Clear();

                foreach (var timer in _refreshTimers.Values)
                {
                    timer.Dispose();
                }
                _refreshTimers.Clear();

                _logger.LogInformation("Cleared manifest cache");
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Gets statistics about manifest management;
        /// </summary>
        public Dictionary<string, object> GetStatistics()
        {
            var stats = new Dictionary<string, object>
            {
                ["TotalManifestsLoaded"] = _totalManifestsLoaded,
                ["ManifestsInCache"] = _manifestCache.Count,
                ["FailedLoads"] = _failedLoads,
                ["ValidationErrors"] = _validationErrors,
                ["IsInitialized"] = _isInitialized,
                ["Uptime"] = DateTime.UtcNow - _startTime,
                ["FileWatchersActive"] = _fileWatchers.Count,
                ["RefreshTimersActive"] = _refreshTimers.Count;
            };

            // Add cache hit/miss stats;
            var cacheStats = _manifestCache.Values;
                .GroupBy(m => m.Metadata.Version)
                .ToDictionary(g => $"Version_{g.Key}", g => g.Count());

            foreach (var cacheStat in cacheStats)
            {
                stats[cacheStat.Key] = cacheStat.Value;
            }

            return stats;
        }

        /// <summary>
        /// Exports manifest to different formats;
        /// </summary>
        public async Task<string> ExportManifestAsync(
            string manifestId,
            string format = "json",
            bool includeSignature = true,
            CancellationToken cancellationToken = default)
        {
            var manifest = GetManifest(manifestId);
            if (manifest == null)
                throw new KeyNotFoundException($"Manifest not found: {manifestId}");

            return format.ToLowerInvariant() switch;
            {
                "json" => await ExportToJsonAsync(manifest, includeSignature, cancellationToken),
                "xml" => await ExportToXmlAsync(manifest, includeSignature, cancellationToken),
                "yaml" => await ExportToYamlAsync(manifest, includeSignature, cancellationToken),
                _ => throw new NotSupportedException($"Export format not supported: {format}")
            };
        }

        /// <summary>
        /// Imports manifest from string;
        /// </summary>
        public async Task<SecurityManifest> ImportManifestAsync(
            string content,
            string format = "json",
            ManifestLoadOptions options = ManifestLoadOptions.StrictMode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(content))
                throw new ArgumentException("Content cannot be null or empty", nameof(content));

            try
            {
                SecurityManifest manifest = format.ToLowerInvariant() switch;
                {
                    "json" => JsonSerializer.Deserialize<SecurityManifest>(content),
                    "xml" => await ParseXmlManifestAsync(content, cancellationToken),
                    _ => throw new NotSupportedException($"Import format not supported: {format}")
                };

                if (manifest == null)
                    throw new InvalidDataException("Failed to parse manifest content");

                // Validate if required;
                if (options != ManifestLoadOptions.None)
                {
                    var validationResult = await ValidateManifestAsync(manifest, options, cancellationToken);
                    if (!validationResult.IsValid && options.HasFlag(ManifestLoadOptions.StrictMode))
                    {
                        throw new SecurityException($"Imported manifest validation failed: {string.Join(", ", validationResult.Errors)}");
                    }
                }

                // Add to cache;
                var manifestId = manifest.Metadata.ManifestId;
                _manifestCache[manifestId] = manifest;
                _manifestTimestamps[manifestId] = DateTime.UtcNow;

                return manifest;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import manifest in format {Format}", format);
                throw;
            }
        }

        #region Private Helper Methods;

        /// <summary>
        /// Ensures required directories exist;
        /// </summary>
        private void EnsureDirectoriesExist()
        {
            var config = _configuration.Value;

            if (!Directory.Exists(config.ManifestDirectory))
                Directory.CreateDirectory(config.ManifestDirectory);

            if (!Directory.Exists(config.SchemaDirectory))
                Directory.CreateDirectory(config.SchemaDirectory);

            if (!Directory.Exists(config.BackupDirectory))
                Directory.CreateDirectory(config.BackupDirectory);

            if (!Directory.Exists(config.CertificateStore))
                Directory.CreateDirectory(config.CertificateStore);
        }

        /// <summary>
        /// Sets up file system watchers for manifest directories;
        /// </summary>
        private void SetupFileWatchers()
        {
            var config = _configuration.Value;

            var watcher = new FileSystemWatcher(config.ManifestDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = "*.*",
                EnableRaisingEvents = true,
                IncludeSubdirectories = true;
            };

            watcher.Changed += OnManifestFileChanged;
            watcher.Created += OnManifestFileCreated;
            watcher.Deleted += OnManifestFileDeleted;
            watcher.Renamed += OnManifestFileRenamed;

            _fileWatchers["root"] = watcher;
            _logger.LogInformation("File system watcher started for {Directory}", config.ManifestDirectory);
        }

        /// <summary>
        /// Parses manifest from content;
        /// </summary>
        private async Task<SecurityManifest> ParseManifestAsync(
            string content,
            string filePath,
            CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch;
            {
                ".json" => JsonSerializer.Deserialize<SecurityManifest>(content),
                ".xml" => await ParseXmlManifestAsync(content, cancellationToken),
                _ => throw new NotSupportedException($"Manifest format not supported: {extension}")
            };
        }

        /// <summary>
        /// Parses XML manifest;
        /// </summary>
        private async Task<SecurityManifest> ParseXmlManifestAsync(string xmlContent, CancellationToken cancellationToken)
        {
            // XML parsing implementation;
            // This is a simplified version - actual implementation would be more complex;
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xmlContent);

            // Convert XML to SecurityManifest;
            // In a real implementation, you would map XML nodes to manifest properties;
            var manifest = new SecurityManifest();

            // Parse metadata;
            var metadataNode = xmlDoc.SelectSingleNode("/SecurityManifest/Metadata");
            if (metadataNode != null)
            {
                manifest.Metadata.ManifestId = metadataNode.SelectSingleNode("ManifestId")?.InnerText ?? Guid.NewGuid().ToString();
                manifest.Metadata.Name = metadataNode.SelectSingleNode("Name")?.InnerText ?? string.Empty;
                manifest.Metadata.Version = metadataNode.SelectSingleNode("Version")?.InnerText ?? "1.0.0";
            }

            // TODO: Parse other sections;

            return manifest;
        }

        /// <summary>
        /// Signs a manifest with digital signature;
        /// </summary>
        private async Task SignManifestAsync(SecurityManifest manifest, CancellationToken cancellationToken)
        {
            try
            {
                // Remove existing signature for new calculation;
                manifest.Signature = string.Empty;
                manifest.Hash = string.Empty;

                // Calculate hash;
                var manifestJson = JsonSerializer.Serialize(manifest);
                manifest.Hash = await _cryptoEngine.ComputeHashAsync(manifestJson, manifest.Metadata.HashAlgorithm, cancellationToken);

                // Sign the hash;
                manifest.Signature = await _cryptoEngine.SignDataAsync(
                    manifest.Hash,
                    manifest.Metadata.SignatureAlgorithm,
                    cancellationToken);

                _logger.LogDebug("Manifest {ManifestId} signed with {Algorithm}",
                    manifest.Metadata.ManifestId, manifest.Metadata.SignatureAlgorithm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sign manifest {ManifestId}", manifest.Metadata.ManifestId);
                throw;
            }
        }

        /// <summary>
        /// Verifies manifest integrity;
        /// </summary>
        private async Task<(ManifestIntegrityStatus Status, string Details)> VerifyManifestIntegrityAsync(
            SecurityManifest manifest,
            CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(manifest.Signature) || string.IsNullOrEmpty(manifest.Hash))
                {
                    return (ManifestIntegrityStatus.InvalidSignature, "Missing signature or hash");
                }

                // Calculate current hash;
                var tempSignature = manifest.Signature;
                var tempHash = manifest.Hash;

                manifest.Signature = string.Empty;
                manifest.Hash = string.Empty;

                var manifestJson = JsonSerializer.Serialize(manifest);
                var calculatedHash = await _cryptoEngine.ComputeHashAsync(manifestJson, manifest.Metadata.HashAlgorithm, cancellationToken);

                // Restore values;
                manifest.Signature = tempSignature;
                manifest.Hash = tempHash;

                // Verify hash matches;
                if (!string.Equals(calculatedHash, manifest.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    return (ManifestIntegrityStatus.InvalidHash, "Hash verification failed");
                }

                // Verify signature;
                var isSignatureValid = await _cryptoEngine.VerifySignatureAsync(
                    manifest.Hash,
                    manifest.Signature,
                    manifest.Metadata.SignatureAlgorithm,
                    cancellationToken);

                if (!isSignatureValid)
                {
                    return (ManifestIntegrityStatus.InvalidSignature, "Signature verification failed");
                }

                // Check certificate validity if thumbprint is provided;
                if (!string.IsNullOrEmpty(manifest.CertificateThumbprint))
                {
                    var isCertificateValid = await _cryptoEngine.ValidateCertificateAsync(
                        manifest.CertificateThumbprint,
                        cancellationToken);

                    if (!isCertificateValid)
                    {
                        return (ManifestIntegrityStatus.InvalidSignature, "Certificate validation failed");
                    }
                }

                return (ManifestIntegrityStatus.Valid, "Manifest integrity verified");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify manifest integrity");
                return (ManifestIntegrityStatus.Unknown, $"Verification error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if manifest is revoked;
        /// </summary>
        private async Task<bool> IsManifestRevokedAsync(SecurityManifest manifest, CancellationToken cancellationToken)
        {
            // Check against revocation list;
            // This would integrate with a certificate revocation list (CRL) or OCSP;
            // For now, return false (not revoked)
            return await Task.FromResult(false);
        }

        /// <summary>
        /// Validates manifest permissions;
        /// </summary>
        private List<string> ValidatePermissions(SecurityManifest manifest)
        {
            var errors = new List<string>();
            var requiredPermissions = _configuration.Value.RequiredPermissions;

            foreach (var requiredPermission in requiredPermissions)
            {
                if (!manifest.Metadata.Permissions.Contains(requiredPermission, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Missing required permission: {requiredPermission}");
                }
            }

            return errors;
        }

        /// <summary>
        /// Creates backup of manifest;
        /// </summary>
        private async Task BackupManifestAsync(
            SecurityManifest manifest,
            string originalPath,
            CancellationToken cancellationToken)
        {
            try
            {
                var backupDir = _configuration.Value.BackupDirectory;
                var fileName = Path.GetFileName(originalPath);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var backupFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{timestamp}_{manifest.Metadata.Revision}{Path.GetExtension(fileName)}";
                var backupPath = Path.Combine(backupDir, backupFileName);

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var manifestJson = JsonSerializer.Serialize(manifest, jsonOptions);

                await File.WriteAllTextAsync(backupPath, manifestJson, cancellationToken);

                // Clean up old backups;
                await CleanupOldBackupsAsync(fileName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create backup for manifest {ManifestId}", manifest.Metadata.ManifestId);
            }
        }

        /// <summary>
        /// Cleans up old backups;
        /// </summary>
        private async Task CleanupOldBackupsAsync(string fileName, CancellationToken cancellationToken)
        {
            try
            {
                var backupDir = _configuration.Value.BackupDirectory;
                var maxBackups = _configuration.Value.MaxBackupCount;

                var backupFiles = Directory.GetFiles(backupDir, $"{Path.GetFileNameWithoutExtension(fileName)}_*")
                    .OrderByDescending(f => File.GetCreationTimeUtc(f))
                    .ToList();

                if (backupFiles.Count > maxBackups)
                {
                    foreach (var oldBackup in backupFiles.Skip(maxBackups))
                    {
                        File.Delete(oldBackup);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old backups");
            }
        }

        /// <summary>
        /// Checks if cached manifest is fresh;
        /// </summary>
        private bool IsManifestFresh(SecurityManifest manifest, string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var lastWriteTime = File.GetLastWriteTimeUtc(filePath);
            return _manifestTimestamps.TryGetValue(manifest.Metadata.ManifestId, out var cachedTime) &&
                   cachedTime >= lastWriteTime;
        }

        /// <summary>
        /// Gets manifest ID from file path;
        /// </summary>
        private string GetManifestIdFromPath(string filePath)
        {
            return Path.GetFileNameWithoutExtension(filePath);
        }

        /// <summary>
        /// Finds manifest path by ID;
        /// </summary>
        private string FindManifestPath(string manifestId)
        {
            // In a real implementation, you would track the file path for each manifest;
            // This is a simplified version;
            var manifestDir = _configuration.Value.ManifestDirectory;
            var possibleFiles = Directory.GetFiles(manifestDir, "*.json", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(manifestDir, "*.xml", SearchOption.AllDirectories));

            foreach (var file in possibleFiles)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var manifest = JsonSerializer.Deserialize<SecurityManifest>(content);
                    if (manifest?.Metadata.ManifestId == manifestId)
                    {
                        return file;
                    }
                }
                catch
                {
                    // Skip files that can't be parsed;
                }
            }

            return null;
        }

        /// <summary>
        /// Clones a manifest (deep copy)
        /// </summary>
        private SecurityManifest CloneManifest(SecurityManifest manifest)
        {
            var json = JsonSerializer.Serialize(manifest);
            return JsonSerializer.Deserialize<SecurityManifest>(json);
        }

        /// <summary>
        /// Exports manifest to JSON;
        /// </summary>
        private async Task<string> ExportToJsonAsync(
            SecurityManifest manifest,
            bool includeSignature,
            CancellationToken cancellationToken)
        {
            var exportManifest = CloneManifest(manifest);

            if (!includeSignature)
            {
                exportManifest.Signature = string.Empty;
                exportManifest.Hash = string.Empty;
                exportManifest.CertificateThumbprint = string.Empty;
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            };

            return JsonSerializer.Serialize(exportManifest, options);
        }

        /// <summary>
        /// Exports manifest to XML;
        /// </summary>
        private async Task<string> ExportToXmlAsync(
            SecurityManifest manifest,
            bool includeSignature,
            CancellationToken cancellationToken)
        {
            // XML export implementation;
            // This would convert the manifest to XML format;
            var xmlDoc = new XmlDocument();
            var root = xmlDoc.CreateElement("SecurityManifest");
            xmlDoc.AppendChild(root);

            // Add metadata;
            var metadata = xmlDoc.CreateElement("Metadata");
            metadata.AppendChild(CreateXmlElement(xmlDoc, "ManifestId", manifest.Metadata.ManifestId));
            metadata.AppendChild(CreateXmlElement(xmlDoc, "Name", manifest.Metadata.Name));
            metadata.AppendChild(CreateXmlElement(xmlDoc, "Version", manifest.Metadata.Version));
            root.AppendChild(metadata);

            // Add signature if included;
            if (includeSignature && !string.IsNullOrEmpty(manifest.Signature))
            {
                var signature = xmlDoc.CreateElement("Signature");
                signature.InnerText = manifest.Signature;
                root.AppendChild(signature);
            }

            return xmlDoc.OuterXml;
        }

        /// <summary>
        /// Exports manifest to YAML;
        /// </summary>
        private async Task<string> ExportToYamlAsync(
            SecurityManifest manifest,
            bool includeSignature,
            CancellationToken cancellationToken)
        {
            // YAML export implementation;
            // This is a simplified version;
            var yaml = new StringBuilder();

            yaml.AppendLine("---");
            yaml.AppendLine($"manifestId: {manifest.Metadata.ManifestId}");
            yaml.AppendLine($"name: {manifest.Metadata.Name}");
            yaml.AppendLine($"version: {manifest.Metadata.Version}");

            if (includeSignature && !string.IsNullOrEmpty(manifest.Signature))
            {
                yaml.AppendLine($"signature: {manifest.Signature}");
            }

            return yaml.ToString();
        }

        /// <summary>
        /// Creates XML element;
        /// </summary>
        private XmlElement CreateXmlElement(XmlDocument doc, string name, string value)
        {
            var element = doc.CreateElement(name);
            element.InnerText = value;
            return element;
        }

        /// <summary>
        /// Logs manifest load event;
        /// </summary>
        private async Task LogManifestLoadAsync(
            SecurityManifest manifest,
            string filePath,
            ManifestLoadOptions options)
        {
            try
            {
                await _auditLogger.LogSecurityEventAsync("ManifestLoaded", new;
                {
                    ManifestId = manifest.Metadata.ManifestId,
                    FilePath = filePath,
                    Options = options.ToString(),
                    Timestamp = DateTime.UtcNow,
                    Metadata = manifest.Metadata;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log manifest load event");
            }
        }

        #endregion;

        #region File System Watcher Events;

        private async void OnManifestFileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType != WatcherChangeTypes.Changed)
                return;

            try
            {
                // Debounce rapid changes;
                await Task.Delay(1000);

                var manifestId = GetManifestIdFromPath(e.FullPath);
                if (_manifestCache.ContainsKey(manifestId))
                {
                    _logger.LogInformation("Manifest file changed: {FilePath}", e.FullPath);

                    // Reload the manifest;
                    var manifest = await LoadManifestAsync(
                        e.FullPath,
                        _configuration.Value.DefaultLoadOptions | ManifestLoadOptions.AutoReload,
                        _cancellationTokenSource.Token);

                    if (manifest != null)
                    {
                        var oldManifest = _manifestCache[manifestId];
                        OnManifestChanged(new ManifestChangedEventArgs(
                            manifestId,
                            e.FullPath,
                            ChangeType.Modified,
                            oldManifest,
                            manifest;
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling file change for {FilePath}", e.FullPath);
            }
        }

        private async void OnManifestFileCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                _logger.LogInformation("New manifest file created: {FilePath}", e.FullPath);

                var manifest = await LoadManifestAsync(
                    e.FullPath,
                    _configuration.Value.DefaultLoadOptions,
                    _cancellationTokenSource.Token);

                if (manifest != null)
                {
                    OnManifestChanged(new ManifestChangedEventArgs(
                        manifest.Metadata.ManifestId,
                        e.FullPath,
                        ChangeType.Created,
                        null,
                        manifest;
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling file creation for {FilePath}", e.FullPath);
            }
        }

        private void OnManifestFileDeleted(object sender, FileSystemEventArgs e)
        {
            try
            {
                var manifestId = GetManifestIdFromPath(e.FullPath);
                _logger.LogInformation("Manifest file deleted: {FilePath}", e.FullPath);

                if (_manifestCache.TryRemove(manifestId, out var removedManifest))
                {
                    OnManifestChanged(new ManifestChangedEventArgs(
                        manifestId,
                        e.FullPath,
                        ChangeType.Deleted,
                        removedManifest,
                        null;
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling file deletion for {FilePath}", e.FullPath);
            }
        }

        private void OnManifestFileRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                _logger.LogInformation("Manifest file renamed: {OldPath} -> {NewPath}", e.OldFullPath, e.FullPath);

                var oldManifestId = GetManifestIdFromPath(e.OldFullPath);
                var newManifestId = GetManifestIdFromPath(e.FullPath);

                if (_manifestCache.TryRemove(oldManifestId, out var manifest))
                {
                    _manifestCache[newManifestId] = manifest;

                    OnManifestChanged(new ManifestChangedEventArgs(
                        oldManifestId,
                        e.FullPath,
                        ChangeType.Renamed,
                        manifest,
                        manifest;
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling file rename from {OldPath} to {NewPath}",
                    e.OldFullPath, e.FullPath);
            }
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnManifestLoaded(SecurityManifest manifest)
        {
            ManifestLoaded?.Invoke(this, manifest);
        }

        protected virtual void OnManifestValidated(ManifestValidationResult result)
        {
            ManifestValidated?.Invoke(this, result);
        }

        protected virtual void OnManifestChanged(ManifestChangedEventArgs e)
        {
            ManifestChanged?.Invoke(this, e);
        }

        protected virtual void OnManifestError(Exception ex)
        {
            ManifestError?.Invoke(this, ex);
        }

        #endregion;

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();

            _cacheLock?.Dispose();
            _loadSemaphore?.Dispose();

            foreach (var watcher in _fileWatchers.Values)
            {
                watcher.Dispose();
            }

            foreach (var timer in _refreshTimers.Values)
            {
                timer.Dispose();
            }

            _manifestCache.Clear();
            _manifestTimestamps.Clear();
            _fileWatchers.Clear();
            _refreshTimers.Clear();

            _logger.LogInformation("ManifestManager disposed");

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~ManifestManager()
        {
            Dispose();
        }
    }
}
