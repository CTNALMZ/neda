using NEDA.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.ContentCreation.AssetPipeline.FormatConverters.ConverterEngine;

namespace NEDA.ContentCreation.AssetPipeline.QualityOptimizers;
{
    /// <summary>
    /// Quality optimization manager for asset pipeline;
    /// Handles compression, optimization, and quality assurance for game assets;
    /// </summary>
    public class QualityManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ISettingsManager _settingsManager;
        private readonly IEventBus _eventBus;
        private readonly CompressionEngine _compressionEngine;
        private readonly OptimizationTool _optimizationTool;
        private readonly Dictionary<string, QualityProfile> _qualityProfiles;
        private readonly object _syncLock = new object();
        private bool _disposed;

        /// <summary>
        /// Quality optimization events;
        /// </summary>
        public event EventHandler<QualityOptimizationEventArgs> OptimizationStarted;
        public event EventHandler<QualityOptimizationEventArgs> OptimizationCompleted;
        public event EventHandler<QualityOptimizationErrorEventArgs> OptimizationError;

        /// <summary>
        /// Initialize QualityManager with dependencies;
        /// </summary>
        public QualityManager(
            ILogger logger,
            ISettingsManager settingsManager,
            IEventBus eventBus,
            CompressionEngine compressionEngine,
            OptimizationTool optimizationTool)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _compressionEngine = compressionEngine ?? throw new ArgumentNullException(nameof(compressionEngine));
            _optimizationTool = optimizationTool ?? throw new ArgumentNullException(nameof(optimizationTool));

            _qualityProfiles = new Dictionary<string, QualityProfile>();
            InitializeDefaultProfiles();
            SubscribeToEvents();

            _logger.LogInformation("QualityManager initialized successfully");
        }

        /// <summary>
        /// Register a quality profile for specific asset types;
        /// </summary>
        public void RegisterQualityProfile(string profileName, QualityProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentException("Profile name cannot be null or empty", nameof(profileName));

            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            lock (_syncLock)
            {
                if (_qualityProfiles.ContainsKey(profileName))
                {
                    _logger.LogWarning($"Quality profile '{profileName}' already exists, overwriting");
                    _qualityProfiles[profileName] = profile;
                }
                else;
                {
                    _qualityProfiles.Add(profileName, profile);
                }

                _logger.LogInformation($"Quality profile '{profileName}' registered successfully");
            }
        }

        /// <summary>
        /// Get quality profile by name;
        /// </summary>
        public QualityProfile GetQualityProfile(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentException("Profile name cannot be null or empty", nameof(profileName));

            lock (_syncLock)
            {
                if (_qualityProfiles.TryGetValue(profileName, out var profile))
                {
                    return profile;
                }

                _logger.LogWarning($"Quality profile '{profileName}' not found, returning default");
                return GetDefaultProfile();
            }
        }

        /// <summary>
        /// Optimize asset with specific quality settings;
        /// </summary>
        public async Task<OptimizationResult> OptimizeAssetAsync(
            AssetInfo assetInfo,
            QualitySettings qualitySettings,
            CancellationToken cancellationToken = default)
        {
            if (assetInfo == null)
                throw new ArgumentNullException(nameof(assetInfo));

            if (qualitySettings == null)
                throw new ArgumentNullException(nameof(qualitySettings));

            var optimizationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogInformation($"Starting optimization for asset: {assetInfo.AssetPath}");

                OnOptimizationStarted(new QualityOptimizationEventArgs;
                {
                    OptimizationId = optimizationId,
                    AssetInfo = assetInfo,
                    QualitySettings = qualitySettings,
                    StartTime = startTime;
                });

                // Validate asset;
                await ValidateAssetAsync(assetInfo, cancellationToken);

                // Apply compression based on asset type;
                CompressionResult compressionResult = null;
                if (qualitySettings.EnableCompression)
                {
                    compressionResult = await _compressionEngine.CompressAssetAsync(
                        assetInfo,
                        qualitySettings.CompressionSettings,
                        cancellationToken);

                    if (compressionResult.Success)
                    {
                        assetInfo = compressionResult.OptimizedAsset;
                    }
                }

                // Apply optimization;
                var optimizationResult = await _optimizationTool.OptimizeAsync(
                    assetInfo,
                    qualitySettings,
                    cancellationToken);

                // Calculate quality metrics;
                var qualityMetrics = CalculateQualityMetrics(assetInfo, optimizationResult);

                var result = new OptimizationResult;
                {
                    OptimizationId = optimizationId,
                    Success = optimizationResult.Success && (compressionResult?.Success ?? true),
                    OriginalAsset = assetInfo,
                    OptimizedAsset = optimizationResult.OptimizedAsset,
                    CompressionResult = compressionResult,
                    OptimizationDetails = optimizationResult,
                    QualityMetrics = qualityMetrics,
                    ProcessingTime = DateTime.UtcNow - startTime,
                    ErrorMessages = optimizationResult.Errors;
                };

                _logger.LogInformation($"Optimization completed for asset: {assetInfo.AssetPath}. " +
                    $"Success: {result.Success}, Size Reduction: {qualityMetrics.SizeReductionPercentage}%");

                OnOptimizationCompleted(new QualityOptimizationEventArgs;
                {
                    OptimizationId = optimizationId,
                    AssetInfo = assetInfo,
                    QualitySettings = qualitySettings,
                    Result = result,
                    EndTime = DateTime.UtcNow;
                });

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Optimization cancelled for asset: {assetInfo.AssetPath}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during optimization for asset: {assetInfo.AssetPath}");

                OnOptimizationError(new QualityOptimizationErrorEventArgs;
                {
                    OptimizationId = optimizationId,
                    AssetInfo = assetInfo,
                    QualitySettings = qualitySettings,
                    Error = ex,
                    Timestamp = DateTime.UtcNow;
                });

                throw new QualityOptimizationException(
                    $"Failed to optimize asset: {assetInfo.AssetPath}", ex);
            }
        }

        /// <summary>
        /// Batch optimize multiple assets;
        /// </summary>
        public async Task<BatchOptimizationResult> OptimizeAssetsBatchAsync(
            IEnumerable<AssetInfo> assets,
            QualitySettings qualitySettings,
            IProgress<BatchOptimizationProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (assets == null)
                throw new ArgumentNullException(nameof(assets));

            if (qualitySettings == null)
                throw new ArgumentNullException(nameof(qualitySettings));

            var batchId = Guid.NewGuid().ToString();
            var assetsList = assets.ToList();
            var totalAssets = assetsList.Count;
            var completedAssets = 0;
            var failedAssets = 0;
            var results = new List<AssetOptimizationResult>();
            var startTime = DateTime.UtcNow;

            _logger.LogInformation($"Starting batch optimization for {totalAssets} assets. Batch ID: {batchId}");

            foreach (var asset in assetsList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"Batch optimization cancelled. Batch ID: {batchId}");
                    break;
                }

                try
                {
                    var result = await OptimizeAssetAsync(asset, qualitySettings, cancellationToken);

                    results.Add(new AssetOptimizationResult;
                    {
                        AssetInfo = asset,
                        OptimizationResult = result,
                        Success = result.Success;
                    });

                    completedAssets++;

                    progress?.Report(new BatchOptimizationProgress;
                    {
                        BatchId = batchId,
                        TotalAssets = totalAssets,
                        CompletedAssets = completedAssets,
                        FailedAssets = failedAssets,
                        CurrentAsset = asset.AssetPath,
                        PercentageComplete = (completedAssets * 100) / totalAssets;
                    });
                }
                catch (Exception ex)
                {
                    failedAssets++;
                    _logger.LogError(ex, $"Failed to optimize asset in batch: {asset.AssetPath}");

                    results.Add(new AssetOptimizationResult;
                    {
                        AssetInfo = asset,
                        Error = ex,
                        Success = false;
                    });
                }
            }

            var batchResult = new BatchOptimizationResult;
            {
                BatchId = batchId,
                TotalAssets = totalAssets,
                SuccessfulOptimizations = completedAssets - failedAssets,
                FailedOptimizations = failedAssets,
                Cancelled = cancellationToken.IsCancellationRequested,
                AssetResults = results,
                TotalProcessingTime = DateTime.UtcNow - startTime,
                AverageSizeReduction = results.Where(r => r.Success).Average(r =>
                    r.OptimizationResult?.QualityMetrics?.SizeReductionPercentage ?? 0)
            };

            _logger.LogInformation($"Batch optimization completed. Batch ID: {batchId}. " +
                $"Success: {batchResult.SuccessfulOptimizations}, Failed: {batchResult.FailedOptimizations}");

            return batchResult;
        }

        /// <summary>
        /// Validate asset before optimization;
        /// </summary>
        private async Task ValidateAssetAsync(AssetInfo assetInfo, CancellationToken cancellationToken)
        {
            if (!File.Exists(assetInfo.AssetPath))
                throw new FileNotFoundException($"Asset file not found: {assetInfo.AssetPath}");

            // Check file size limits;
            var fileInfo = new FileInfo(assetInfo.AssetPath);
            var maxSize = _settingsManager.GetSetting<long>("QualitySettings:MaxInputFileSize", 500 * 1024 * 1024); // 500MB default;

            if (fileInfo.Length > maxSize)
                throw new QualityValidationException(
                    $"Asset file too large: {fileInfo.Length} bytes. Maximum allowed: {maxSize} bytes");

            // Validate file format;
            var allowedFormats = _settingsManager.GetSetting<List<string>>("QualitySettings:AllowedFormats",
                new List<string> { ".png", ".jpg", ".jpeg", ".tga", ".fbx", ".obj", ".wav", ".mp3" });

            var extension = Path.GetExtension(assetInfo.AssetPath).ToLowerInvariant();
            if (!allowedFormats.Contains(extension))
                throw new QualityValidationException(
                    $"Unsupported file format: {extension}. Allowed formats: {string.Join(", ", allowedFormats)}");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Calculate quality metrics after optimization;
        /// </summary>
        private QualityMetrics CalculateQualityMetrics(AssetInfo originalAsset, OptimizationDetails optimizationDetails)
        {
            var originalFile = new FileInfo(originalAsset.AssetPath);
            var optimizedFile = new FileInfo(optimizationDetails.OptimizedAsset?.AssetPath ?? originalAsset.AssetPath);

            return new QualityMetrics;
            {
                OriginalSizeBytes = originalFile.Length,
                OptimizedSizeBytes = optimizedFile.Length,
                SizeReductionPercentage = originalFile.Length > 0 ?
                    (1 - (double)optimizedFile.Length / originalFile.Length) * 100 : 0,
                CompressionRatio = originalFile.Length > 0 ?
                    (double)originalFile.Length / optimizedFile.Length : 1,
                ProcessingTimeMs = optimizationDetails.ProcessingTime.TotalMilliseconds,
                QualityScore = CalculateQualityScore(optimizationDetails),
                MemoryUsageReduction = optimizationDetails.MemoryUsageReduction,
                PerformanceImprovement = optimizationDetails.PerformanceImprovement;
            };
        }

        /// <summary>
        /// Calculate overall quality score (0-100)
        /// </summary>
        private double CalculateQualityScore(OptimizationDetails details)
        {
            var score = 100.0;

            // Deduct points for quality loss;
            if (details.QualityLossDetected)
                score -= 20;

            // Add points for size reduction;
            score += Math.Min(details.SizeReductionPercentage, 30);

            // Add points for performance improvement;
            score += Math.Min(details.PerformanceImprovement * 10, 20);

            // Deduct points for long processing time;
            if (details.ProcessingTime > TimeSpan.FromSeconds(30))
                score -= 10;

            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// Initialize default quality profiles;
        /// </summary>
        private void InitializeDefaultProfiles()
        {
            // High Quality Profile;
            RegisterQualityProfile("HighQuality", new QualityProfile;
            {
                Name = "HighQuality",
                Description = "Maximum quality with minimal compression",
                Settings = new QualitySettings;
                {
                    EnableCompression = true,
                    CompressionLevel = CompressionLevel.Low,
                    MaxTextureSize = 4096,
                    MaxPolygonCount = 500000,
                    TextureFormat = TextureFormat.BC7,
                    AudioQuality = AudioQuality.High,
                    ModelOptimizationLevel = OptimizationLevel.Minimal,
                    PreserveMetadata = true;
                }
            });

            // Balanced Profile;
            RegisterQualityProfile("Balanced", new QualityProfile;
            {
                Name = "Balanced",
                Description = "Balanced quality and performance",
                Settings = new QualitySettings;
                {
                    EnableCompression = true,
                    CompressionLevel = CompressionLevel.Medium,
                    MaxTextureSize = 2048,
                    MaxPolygonCount = 200000,
                    TextureFormat = TextureFormat.BC3,
                    AudioQuality = AudioQuality.Medium,
                    ModelOptimizationLevel = OptimizationLevel.Moderate,
                    PreserveMetadata = true;
                }
            });

            // Performance Profile;
            RegisterQualityProfile("Performance", new QualityProfile;
            {
                Name = "Performance",
                Description = "Optimized for maximum performance",
                Settings = new QualitySettings;
                {
                    EnableCompression = true,
                    CompressionLevel = CompressionLevel.High,
                    MaxTextureSize = 1024,
                    MaxPolygonCount = 50000,
                    TextureFormat = TextureFormat.BC1,
                    AudioQuality = AudioQuality.Low,
                    ModelOptimizationLevel = OptimizationLevel.Aggressive,
                    PreserveMetadata = false;
                }
            });

            _logger.LogInformation("Default quality profiles initialized");
        }

        /// <summary>
        /// Get default quality profile;
        /// </summary>
        private QualityProfile GetDefaultProfile()
        {
            return _qualityProfiles.TryGetValue("Balanced", out var profile)
                ? profile;
                : _qualityProfiles.Values.First();
        }

        /// <summary>
        /// Subscribe to system events;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<SystemConfigurationChangedEvent>(OnSystemConfigurationChanged);
            _eventBus.Subscribe<AssetImportCompletedEvent>(OnAssetImportCompleted);
        }

        /// <summary>
        /// Handle system configuration changes;
        /// </summary>
        private void OnSystemConfigurationChanged(SystemConfigurationChangedEvent @event)
        {
            _logger.LogInformation("System configuration changed, reloading quality settings");
            // Reload settings if needed;
        }

        /// <summary>
        /// Handle asset import completion;
        /// </summary>
        private void OnAssetImportCompleted(AssetImportCompletedEvent @event)
        {
            if (_settingsManager.GetSetting<bool>("QualitySettings:AutoOptimizeOnImport", false))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var profile = GetQualityProfile(
                            _settingsManager.GetSetting<string>("QualitySettings:DefaultProfile", "Balanced"));

                        await OptimizeAssetAsync(@event.AssetInfo, profile.Settings);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-optimization failed after asset import");
                    }
                });
            }
        }

        /// <summary>
        /// Raise optimization started event;
        /// </summary>
        protected virtual void OnOptimizationStarted(QualityOptimizationEventArgs e)
        {
            OptimizationStarted?.Invoke(this, e);
            _eventBus.Publish(new QualityOptimizationStartedEvent(e));
        }

        /// <summary>
        /// Raise optimization completed event;
        /// </summary>
        protected virtual void OnOptimizationCompleted(QualityOptimizationEventArgs e)
        {
            OptimizationCompleted?.Invoke(this, e);
            _eventBus.Publish(new QualityOptimizationCompletedEvent(e));
        }

        /// <summary>
        /// Raise optimization error event;
        /// </summary>
        protected virtual void OnOptimizationError(QualityOptimizationErrorEventArgs e)
        {
            OptimizationError?.Invoke(this, e);
            _eventBus.Publish(new QualityOptimizationErrorEvent(e));
        }

        /// <summary>
        /// Get all registered quality profiles;
        /// </summary>
        public IReadOnlyDictionary<string, QualityProfile> GetAllProfiles()
        {
            lock (_syncLock)
            {
                return new Dictionary<string, QualityProfile>(_qualityProfiles);
            }
        }

        /// <summary>
        /// Cleanup resources;
        /// </summary>
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
                    _eventBus.Unsubscribe<SystemConfigurationChangedEvent>(OnSystemConfigurationChanged);
                    _eventBus.Unsubscribe<AssetImportCompletedEvent>(OnAssetImportCompleted);

                    if (_compressionEngine is IDisposable compressionDisposable)
                        compressionDisposable.Dispose();

                    if (_optimizationTool is IDisposable optimizationDisposable)
                        optimizationDisposable.Dispose();
                }

                _disposed = true;
            }
        }

        ~QualityManager()
        {
            Dispose(false);
        }
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Quality settings for asset optimization;
    /// </summary>
    public class QualitySettings;
    {
        public bool EnableCompression { get; set; } = true;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Medium;
        public CompressionSettings CompressionSettings { get; set; } = new CompressionSettings();
        public int MaxTextureSize { get; set; } = 2048;
        public int MaxPolygonCount { get; set; } = 100000;
        public TextureFormat TextureFormat { get; set; } = TextureFormat.BC3;
        public AudioQuality AudioQuality { get; set; } = AudioQuality.Medium;
        public OptimizationLevel ModelOptimizationLevel { get; set; } = OptimizationLevel.Moderate;
        public bool PreserveMetadata { get; set; } = true;
        public bool GenerateMipmaps { get; set; } = true;
        public int MipmapLevels { get; set; } = 10;
        public bool RemoveUnusedData { get; set; } = true;
    }

    /// <summary>
    /// Quality profile with named settings;
    /// </summary>
    public class QualityProfile;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public QualitySettings Settings { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Asset information;
    /// </summary>
    public class AssetInfo;
    {
        public string AssetPath { get; set; }
        public AssetType AssetType { get; set; }
        public string OriginalName { get; set; }
        public DateTime ImportDate { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Optimization result;
    /// </summary>
    public class OptimizationResult;
    {
        public string OptimizationId { get; set; }
        public bool Success { get; set; }
        public AssetInfo OriginalAsset { get; set; }
        public AssetInfo OptimizedAsset { get; set; }
        public CompressionResult CompressionResult { get; set; }
        public OptimizationDetails OptimizationDetails { get; set; }
        public QualityMetrics QualityMetrics { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public List<string> ErrorMessages { get; set; } = new List<string>();
    }

    /// <summary>
    /// Quality metrics for optimization;
    /// </summary>
    public class QualityMetrics;
    {
        public long OriginalSizeBytes { get; set; }
        public long OptimizedSizeBytes { get; set; }
        public double SizeReductionPercentage { get; set; }
        public double CompressionRatio { get; set; }
        public double ProcessingTimeMs { get; set; }
        public double QualityScore { get; set; }
        public double MemoryUsageReduction { get; set; }
        public double PerformanceImprovement { get; set; }
    }

    /// <summary>
    /// Batch optimization result;
    /// </summary>
    public class BatchOptimizationResult;
    {
        public string BatchId { get; set; }
        public int TotalAssets { get; set; }
        public int SuccessfulOptimizations { get; set; }
        public int FailedOptimizations { get; set; }
        public bool Cancelled { get; set; }
        public List<AssetOptimizationResult> AssetResults { get; set; } = new List<AssetOptimizationResult>();
        public TimeSpan TotalProcessingTime { get; set; }
        public double AverageSizeReduction { get; set; }
    }

    /// <summary>
    /// Individual asset optimization result in batch;
    /// </summary>
    public class AssetOptimizationResult;
    {
        public AssetInfo AssetInfo { get; set; }
        public OptimizationResult OptimizationResult { get; set; }
        public Exception Error { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Batch optimization progress;
    /// </summary>
    public class BatchOptimizationProgress;
    {
        public string BatchId { get; set; }
        public int TotalAssets { get; set; }
        public int CompletedAssets { get; set; }
        public int FailedAssets { get; set; }
        public string CurrentAsset { get; set; }
        public int PercentageComplete { get; set; }
    }

    /// <summary>
    /// Event arguments for quality optimization;
    /// </summary>
    public class QualityOptimizationEventArgs : EventArgs;
    {
        public string OptimizationId { get; set; }
        public AssetInfo AssetInfo { get; set; }
        public QualitySettings QualitySettings { get; set; }
        public OptimizationResult Result { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    /// <summary>
    /// Event arguments for quality optimization error;
    /// </summary>
    public class QualityOptimizationErrorEventArgs : EventArgs;
    {
        public string OptimizationId { get; set; }
        public AssetInfo AssetInfo { get; set; }
        public QualitySettings QualitySettings { get; set; }
        public Exception Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Compression level enum;
    /// </summary>
    public enum CompressionLevel;
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Ultra = 4;
    }

    /// <summary>
    /// Texture format enum;
    /// </summary>
    public enum TextureFormat;
    {
        RGBA32,
        RGBA16,
        BC1,    // DXT1;
        BC3,    // DXT5;
        BC4,    // ATI1;
        BC5,    // ATI2;
        BC7     // DX10+
    }

    /// <summary>
    /// Audio quality enum;
    /// </summary>
    public enum AudioQuality;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3;
    }

    /// <summary>
    /// Optimization level enum;
    /// </summary>
    public enum OptimizationLevel;
    {
        None = 0,
        Minimal = 1,
        Moderate = 2,
        Aggressive = 3;
    }

    /// <summary>
    /// Asset type enum;
    /// </summary>
    public enum AssetType;
    {
        Texture = 0,
        Model = 1,
        Audio = 2,
        Animation = 3,
        Material = 4,
        Shader = 5,
        Other = 99;
    }

    /// <summary>
    /// Custom exceptions;
    /// </summary>
    public class QualityOptimizationException : Exception
    {
        public QualityOptimizationException(string message) : base(message) { }
        public QualityOptimizationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class QualityValidationException : Exception
    {
        public QualityValidationException(string message) : base(message) { }
        public QualityValidationException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Event Classes;

    /// <summary>
    /// Quality optimization started event;
    /// </summary>
    public class QualityOptimizationStartedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public QualityOptimizationEventArgs Args { get; }

        public QualityOptimizationStartedEvent(QualityOptimizationEventArgs args)
        {
            Args = args ?? throw new ArgumentNullException(nameof(args));
        }
    }

    /// <summary>
    /// Quality optimization completed event;
    /// </summary>
    public class QualityOptimizationCompletedEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public QualityOptimizationEventArgs Args { get; }

        public QualityOptimizationCompletedEvent(QualityOptimizationEventArgs args)
        {
            Args = args ?? throw new ArgumentNullException(nameof(args));
        }
    }

    /// <summary>
    /// Quality optimization error event;
    /// </summary>
    public class QualityOptimizationErrorEvent : IEvent;
    {
        public string EventId { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public QualityOptimizationErrorEventArgs Args { get; }

        public QualityOptimizationErrorEvent(QualityOptimizationErrorEventArgs args)
        {
            Args = args ?? throw new ArgumentNullException(nameof(args));
        }
    }

    #endregion;
}
