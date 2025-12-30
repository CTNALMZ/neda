using Microsoft.Extensions.Logging;
using NEDA.Animation.CharacterAnimation.AnimationBlueprints.StateMachines;
using NEDA.API.Middleware;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace NEDA.Animation.CharacterAnimation.MotionImport;
{
    /// <summary>
    /// Motion capture ve animasyon dosyalarını içe aktarma sistemini yöneten ana sınıf;
    /// FBX, BVH, Mocap, Motion Capture verilerini import eder;
    /// </summary>
    public interface IMotionImporter : IDisposable
    {
        /// <summary>
        /// Importer ID;
        /// </summary>
        Guid ImporterId { get; }

        /// <summary>
        /// Importer adı;
        /// </summary>
        string ImporterName { get; set; }

        /// <summary>
        /// Desteklenen dosya formatları;
        /// </summary>
        IReadOnlyList<MotionFileFormat> SupportedFormats { get; }

        /// <summary>
        /// Import işlemi devam ediyor mu?
        /// </summary>
        bool IsImporting { get; }

        /// <summary>
        /// Import edilen motion'lar;
        /// </summary>
        IReadOnlyDictionary<string, ImportedMotion> ImportedMotions { get; }

        /// <summary>
        /// Import işlemini başlat;
        /// </summary>
        Task<bool> InitializeAsync(MotionImporterConfiguration configuration);

        /// <summary>
        /// Motion dosyasını import et;
        /// </summary>
        Task<ImportedMotion> ImportMotionAsync(string filePath, ImportOptions options = null);

        /// <summary>
        /// Batch import işlemi;
        /// </summary>
        Task<List<ImportedMotion>> ImportBatchAsync(List<string> filePaths, ImportOptions options = null);

        /// <summary>
        /// Motion'ı belirtilen formatta export et;
        /// </summary>
        Task<bool> ExportMotionAsync(ImportedMotion motion, string outputPath, ExportFormat format);

        /// <summary>
        /// Motion'ı optimize et;
        /// </summary>
        Task<ImportedMotion> OptimizeMotionAsync(ImportedMotion motion, OptimizationSettings settings);

        /// <summary>
        /// Motion'ı retarget et (farklı skeleton'a uygula)
        /// </summary>
        Task<ImportedMotion> RetargetMotionAsync(ImportedMotion motion, SkeletonDefinition targetSkeleton, RetargetSettings settings);

        /// <summary>
        /// Motion'ı blend et (birden fazla motion'ı birleştir)
        /// </summary>
        Task<ImportedMotion> BlendMotionsAsync(List<ImportedMotion> motions, BlendSettings settings);

        /// <summary>
        /// Motion'ı loop'a uyarla;
        /// </summary>
        Task<ImportedMotion> MakeMotionLoopableAsync(ImportedMotion motion, LoopSettings settings);

        /// <summary>
        /// Motion'dan animation clip oluştur;
        /// </summary>
        Task<AnimationClip> CreateAnimationClipAsync(ImportedMotion motion, ClipCreationSettings settings);

        /// <summary>
        /// Motion'dan state machine oluştur;
        /// </summary>
        Task<IStateMachine> CreateStateMachineFromMotionAsync(ImportedMotion motion, StateMachineSettings settings);

        /// <summary>
        /// Import edilen motion'ı kaydet;
        /// </summary>
        Task<bool> SaveImportedMotionAsync(ImportedMotion motion, string filePath);

        /// <summary>
        /// Import edilen motion'ı yükle;
        /// </summary>
        Task<ImportedMotion> LoadImportedMotionAsync(string filePath);

        /// <summary>
        /// Motion'ı preview et (ön izleme)
        /// </summary>
        Task<MotionPreview> PreviewMotionAsync(ImportedMotion motion, PreviewSettings settings);

        /// <summary>
        /// Motion'ı analiz et;
        /// </summary>
        Task<MotionAnalysis> AnalyzeMotionAsync(ImportedMotion motion);

        /// <summary>
        /// Motion'ı cleanup et (temizle)
        /// </summary>
        Task<ImportedMotion> CleanupMotionAsync(ImportedMotion motion, CleanupSettings settings);

        /// <summary>
        /// Motion importer event'ine abone ol;
        /// </summary>
        void SubscribeToImporterEvent(MotionImporterEventType eventType, Action<MotionImporterEventArgs> handler);

        /// <summary>
        /// Motion importer event'inden abonelikten çık;
        /// </summary>
        void UnsubscribeFromImporterEvent(MotionImporterEventType eventType, Action<MotionImporterEventArgs> handler);

        /// <summary>
        /// İlerleme durumunu al;
        /// </summary>
        ImportProgress GetImportProgress();

        /// <summary>
        /// Performans metriklerini al;
        /// </summary>
        MotionImporterMetrics GetPerformanceMetrics();
    }
    
    /// <summary>
    Motion Importer ana implementasyonu;
    /// </summary>
    public class MotionImporter : IMotionImporter;
    {
        private readonly ILogger<MotionImporter> _logger;
        private readonly IEventBus _eventBus;
        private readonly IErrorReporter _errorReporter;
        private readonly object _syncLock = new object();

        private Guid _importerId;
        private string _importerName;
        private bool _isInitialized;
        private bool _isImporting;
        private MotionImporterConfiguration _configuration;

        private readonly Dictionary<string, ImportedMotion> _importedMotions = new Dictionary<string, ImportedMotion>();
        private readonly Dictionary<MotionFileFormat, IMotionFormatHandler> _formatHandlers =
            new Dictionary<MotionFileFormat, IMotionFormatHandler>();
        private readonly Dictionary<MotionImporterEventType, List<Action<MotionImporterEventArgs>>> _eventHandlers =
            new Dictionary<MotionImporterEventType, List<Action<MotionImporterEventArgs>>>();

        // Performance tracking;
        private readonly PerformanceTracker _performanceTracker = new PerformanceTracker();
        private ImportProgress _currentProgress;
        private CancellationTokenSource _importCancellationTokenSource;

        // Cache system;
        private readonly MotionCache _motionCache = new MotionCache();
        private bool _isDisposed;

        /// <summary>
        /// MotionImporter constructor;
        /// </summary>
        public MotionImporter(
            ILogger<MotionImporter> logger,
            IEventBus eventBus,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            _importerId = Guid.NewGuid();
            _importerName = $"MotionImporter_{_importerId.ToString("N").Substring(0, 8)}";
            _isInitialized = false;
            _isImporting = false;

            // Initialize progress;
            _currentProgress = new ImportProgress;
            {
                TotalFiles = 0,
                ProcessedFiles = 0,
                CurrentFile = null,
                Percentage = 0,
                Status = ImportStatus.Idle;
            };

            _logger.LogInformation("MotionImporter initialized with ID: {ImporterId}", _importerId);
        }

        public Guid ImporterId => _importerId;

        public string ImporterName;
        {
            get => _importerName;
            set;
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Importer name cannot be null or empty", nameof(value));

                lock (_syncLock)
                {
                    var oldName = _importerName;
                    _importerName = value;
                    _logger.LogDebug("MotionImporter name changed from '{OldName}' to '{NewName}'", oldName, value);

                    RaiseImporterEvent(MotionImporterEventType.ImporterRenamed,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            OldName = oldName,
                            NewName = value;
                        });
                }
            }
        }

        public IReadOnlyList<MotionFileFormat> SupportedFormats;
        {
            get;
            {
                lock (_syncLock)
                {
                    return _formatHandlers.Keys.ToList();
                }
            }
        }

        public bool IsImporting => _isImporting;

        public IReadOnlyDictionary<string, ImportedMotion> ImportedMotions;
        {
            get;
            {
                lock (_syncLock)
                {
                    return new Dictionary<string, ImportedMotion>(_importedMotions);
                }
            }
        }

        public async Task<bool> InitializeAsync(MotionImporterConfiguration configuration)
        {
            try
            {
                using (_performanceTracker.TrackOperation("Initialize"))
                {
                    _logger.LogInformation("Initializing MotionImporter with configuration");

                    if (configuration == null)
                        throw new ArgumentNullException(nameof(configuration));

                    lock (_syncLock)
                    {
                        if (_isInitialized)
                        {
                            _logger.LogWarning("MotionImporter is already initialized");
                            return true;
                        }

                        _configuration = configuration;
                        _importerName = configuration.ImporterName;

                        // Format handler'ları yükle;
                        InitializeFormatHandlers();

                        // Cache'i başlat;
                        _motionCache.Initialize(configuration.CacheSettings);

                        _isInitialized = true;
                    }

                    // Event bus'a kayıt ol;
                    await _eventBus.SubscribeAsync<MotionImporterEvent>(HandleImporterEvent);

                    _logger.LogInformation("MotionImporter initialized successfully. Supported formats: {FormatCount}",
                        _formatHandlers.Count);

                    RaiseImporterEvent(MotionImporterEventType.ImporterInitialized,
                        new MotionImporterEventArgs { ImporterId = _importerId });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MotionImporter");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionImporterInitializationFailed);
                throw new MotionImporterException("Failed to initialize MotionImporter", ex,
                    ErrorCodes.MotionImporterInitializationFailed);
            }
        }

        public async Task<ImportedMotion> ImportMotionAsync(string filePath, ImportOptions options = null)
        {
            try
            {
                using (_performanceTracker.TrackOperation("ImportMotion"))
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                        throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

                    if (!File.Exists(filePath))
                        throw new FileNotFoundException($"Motion file not found: {filePath}");

                    // Cache kontrolü;
                    var cacheKey = GenerateCacheKey(filePath, options);
                    if (_configuration.UseCache && _motionCache.TryGetMotion(cacheKey, out var cachedMotion))
                    {
                        _logger.LogInformation("Loading motion from cache: {FilePath}", filePath);

                        lock (_syncLock)
                        {
                            _importedMotions[cachedMotion.MotionId] = cachedMotion;
                        }

                        RaiseImporterEvent(MotionImporterEventType.MotionImported,
                            new MotionImporterEventArgs;
                            {
                                ImporterId = _importerId,
                                MotionId = cachedMotion.MotionId,
                                FilePath = filePath,
                                FromCache = true;
                            });

                        return cachedMotion;
                    }

                    // Import işlemini başlat;
                    lock (_syncLock)
                    {
                        _isImporting = true;
                        UpdateProgress(ImportStatus.Importing, 0, 1, filePath);
                    }

                    _logger.LogInformation("Importing motion file: {FilePath}", filePath);

                    // Dosya formatını belirle;
                    var format = DetectFileFormat(filePath);
                    if (!_formatHandlers.ContainsKey(format))
                    {
                        throw new MotionImporterException($"Unsupported file format: {format}",
                            ErrorCodes.UnsupportedFileFormat);
                    }

                    var handler = _formatHandlers[format];

                    // Import options;
                    options ??= new ImportOptions();

                    // Import işlemi;
                    var motionData = await handler.ImportAsync(filePath, options);

                    // Motion'ı oluştur;
                    var motion = CreateImportedMotion(motionData, filePath, options);

                    // Skeleton'ı process et;
                    if (options.ProcessSkeleton)
                    {
                        motion = await ProcessSkeletonAsync(motion, options.SkeletonSettings);
                    }

                    // Animation data'yı process et;
                    if (options.ProcessAnimation)
                    {
                        motion = await ProcessAnimationDataAsync(motion, options.AnimationSettings);
                    }

                    // Cache'e ekle;
                    if (_configuration.UseCache)
                    {
                        _motionCache.AddMotion(cacheKey, motion);
                    }

                    // Imported motions'a ekle;
                    lock (_syncLock)
                    {
                        _importedMotions[motion.MotionId] = motion;
                        UpdateProgress(ImportStatus.Completed, 1, 1, filePath);
                        _isImporting = false;
                    }

                    _logger.LogInformation("Motion imported successfully: {MotionName} ({FrameCount} frames, {Duration}s)",
                        motion.MotionName, motion.FrameCount, motion.Duration);

                    RaiseImporterEvent(MotionImporterEventType.MotionImported,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            MotionName = motion.MotionName,
                            FilePath = filePath,
                            FrameCount = motion.FrameCount,
                            Duration = motion.Duration,
                            Format = format;
                        });

                    await _eventBus.PublishAsync(new MotionImporterEvent;
                    {
                        EventType = MotionImporterEventType.MotionImported.ToString(),
                        ImporterId = _importerId,
                        Timestamp = DateTime.UtcNow,
                        Data = new;
                        {
                            MotionId = motion.MotionId,
                            MotionName = motion.MotionName,
                            FilePath = filePath,
                            Format = format.ToString()
                        }
                    });

                    return motion;
                }
            }
            catch (Exception ex)
            {
                lock (_syncLock)
                {
                    UpdateProgress(ImportStatus.Failed, 0, 1, filePath);
                    _isImporting = false;
                }

                _logger.LogError(ex, "Failed to import motion file: {FilePath}", filePath);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionImportFailed);
                throw new MotionImporterException("Failed to import motion", ex, ErrorCodes.MotionImportFailed);
            }
        }

        public async Task<List<ImportedMotion>> ImportBatchAsync(List<string> filePaths, ImportOptions options = null)
        {
            try
            {
                using (_performanceTracker.TrackOperation("ImportBatch"))
                {
                    if (filePaths == null || filePaths.Count == 0)
                        throw new ArgumentException("File paths list cannot be null or empty", nameof(filePaths));

                    _logger.LogInformation("Starting batch import of {FileCount} files", filePaths.Count);

                    lock (_syncLock)
                    {
                        _isImporting = true;
                        UpdateProgress(ImportStatus.Importing, 0, filePaths.Count, "Batch import");
                    }

                    var importedMotions = new List<ImportedMotion>();
                    var failedImports = new List<string>();

                    options ??= new ImportOptions();

                    for (int i = 0; i < filePaths.Count; i++)
                    {
                        var filePath = filePaths[i];

                        try
                        {
                            lock (_syncLock)
                            {
                                UpdateProgress(ImportStatus.Importing, i, filePaths.Count, filePath);
                            }

                            var motion = await ImportMotionAsync(filePath, options);
                            importedMotions.Add(motion);

                            _logger.LogDebug("Successfully imported: {FilePath}", filePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to import file in batch: {FilePath}", filePath);
                            failedImports.Add(filePath);

                            RaiseImporterEvent(MotionImporterEventType.ImportFailed,
                                new MotionImporterEventArgs;
                                {
                                    ImporterId = _importerId,
                                    FilePath = filePath,
                                    ErrorMessage = ex.Message;
                                });
                        }
                    }

                    lock (_syncLock)
                    {
                        UpdateProgress(ImportStatus.Completed, filePaths.Count, filePaths.Count, "Batch import completed");
                        _isImporting = false;
                    }

                    _logger.LogInformation("Batch import completed. Success: {SuccessCount}, Failed: {FailedCount}",
                        importedMotions.Count, failedImports.Count);

                    if (failedImports.Count > 0)
                    {
                        RaiseImporterEvent(MotionImporterEventType.BatchImportCompletedWithErrors,
                            new MotionImporterEventArgs;
                            {
                                ImporterId = _importerId,
                                SuccessCount = importedMotions.Count,
                                FailedCount = failedImports.Count,
                                FailedFiles = failedImports;
                            });
                    }
                    else;
                    {
                        RaiseImporterEvent(MotionImporterEventType.BatchImportCompleted,
                            new MotionImporterEventArgs;
                            {
                                ImporterId = _importerId,
                                SuccessCount = importedMotions.Count;
                            });
                    }

                    return importedMotions;
                }
            }
            catch (Exception ex)
            {
                lock (_syncLock)
                {
                    _isImporting = false;
                }

                _logger.LogError(ex, "Failed batch import");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.BatchImportFailed);
                throw new MotionImporterException("Failed batch import", ex, ErrorCodes.BatchImportFailed);
            }
        }

        public async Task<bool> ExportMotionAsync(ImportedMotion motion, string outputPath, ExportFormat format)
        {
            try
            {
                using (_performanceTracker.TrackOperation("ExportMotion"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    if (string.IsNullOrWhiteSpace(outputPath))
                        throw new ArgumentException("Output path cannot be null or empty", nameof(outputPath));

                    _logger.LogInformation("Exporting motion {MotionName} to {Format} format",
                        motion.MotionName, format);

                    // Format handler'ı bul;
                    var formatEnum = GetFormatFromExportFormat(format);
                    if (!_formatHandlers.ContainsKey(formatEnum))
                    {
                        throw new MotionImporterException($"Unsupported export format: {format}",
                            ErrorCodes.UnsupportedExportFormat);
                    }

                    var handler = _formatHandlers[formatEnum];

                    // Export işlemi;
                    var exportData = PrepareMotionForExport(motion);
                    await handler.ExportAsync(exportData, outputPath);

                    _logger.LogInformation("Motion exported successfully to: {OutputPath}", outputPath);

                    RaiseImporterEvent(MotionImporterEventType.MotionExported,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            MotionName = motion.MotionName,
                            OutputPath = outputPath,
                            ExportFormat = format;
                        });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionExportFailed);
                throw new MotionImporterException("Failed to export motion", ex, ErrorCodes.MotionExportFailed);
            }
        }

        public async Task<ImportedMotion> OptimizeMotionAsync(ImportedMotion motion, OptimizationSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("OptimizeMotion"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    if (settings == null)
                        settings = new OptimizationSettings();

                    _logger.LogInformation("Optimizing motion: {MotionName}", motion.MotionName);

                    var optimizedMotion = motion.Clone();

                    // Keyframe reduction;
                    if (settings.ReduceKeyframes)
                    {
                        optimizedMotion = await ReduceKeyframesAsync(optimizedMotion, settings);
                    }

                    // Curve optimization;
                    if (settings.OptimizeCurves)
                    {
                        optimizedMotion = await OptimizeCurvesAsync(optimizedMotion, settings);
                    }

                    // Compression;
                    if (settings.CompressData)
                    {
                        optimizedMotion = await CompressMotionDataAsync(optimizedMotion, settings);
                    }

                    // Normalize;
                    if (settings.Normalize)
                    {
                        optimizedMotion = await NormalizeMotionAsync(optimizedMotion, settings);
                    }

                    _logger.LogInformation("Motion optimized: {MotionName} (Original: {OriginalSize}KB, Optimized: {OptimizedSize}KB)",
                        motion.MotionName, motion.DataSize / 1024, optimizedMotion.DataSize / 1024);

                    RaiseImporterEvent(MotionImporterEventType.MotionOptimized,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            OptimizedMotionId = optimizedMotion.MotionId,
                            OriginalSize = motion.DataSize,
                            OptimizedSize = optimizedMotion.DataSize;
                        });

                    return optimizedMotion;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to optimize motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionOptimizationFailed);
                throw new MotionImporterException("Failed to optimize motion", ex, ErrorCodes.MotionOptimizationFailed);
            }
        }

        public async Task<ImportedMotion> RetargetMotionAsync(ImportedMotion motion, SkeletonDefinition targetSkeleton, RetargetSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("RetargetMotion"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    if (targetSkeleton == null)
                        throw new ArgumentNullException(nameof(targetSkeleton));

                    settings ??= new RetargetSettings();

                    _logger.LogInformation("Retargeting motion {MotionName} to target skeleton", motion.MotionName);

                    // Source skeleton;
                    var sourceSkeleton = motion.Skeleton;

                    // Bone mapping oluştur;
                    var boneMapping = await CreateBoneMappingAsync(sourceSkeleton, targetSkeleton, settings);

                    // Retarget motion;
                    var retargetedMotion = await PerformRetargetingAsync(motion, sourceSkeleton, targetSkeleton, boneMapping, settings);

                    // Post-process;
                    retargetedMotion = await PostProcessRetargetedMotionAsync(retargetedMotion, settings);

                    _logger.LogInformation("Motion retargeted successfully. Source bones: {SourceBones}, Target bones: {TargetBones}",
                        sourceSkeleton.Bones.Count, targetSkeleton.Bones.Count);

                    RaiseImporterEvent(MotionImporterEventType.MotionRetargeted,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            RetargetedMotionId = retargetedMotion.MotionId,
                            SourceBoneCount = sourceSkeleton.Bones.Count,
                            TargetBoneCount = targetSkeleton.Bones.Count;
                        });

                    return retargetedMotion;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retarget motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionRetargetingFailed);
                throw new MotionImporterException("Failed to retarget motion", ex, ErrorCodes.MotionRetargetingFailed);
            }
        }

        public async Task<ImportedMotion> BlendMotionsAsync(List<ImportedMotion> motions, BlendSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("BlendMotions"))
                {
                    if (motions == null || motions.Count < 2)
                        throw new ArgumentException("At least two motions are required for blending", nameof(motions));

                    if (settings == null)
                        settings = new BlendSettings();

                    _logger.LogInformation("Blending {MotionCount} motions", motions.Count);

                    // Validate motions;
                    if (!await ValidateMotionsForBlendingAsync(motions, settings))
                    {
                        throw new MotionImporterException("Motions are not compatible for blending",
                            ErrorCodes.IncompatibleMotionsForBlending);
                    }

                    // Align motions;
                    var alignedMotions = await AlignMotionsAsync(motions, settings);

                    // Perform blending;
                    var blendedMotion = await PerformBlendingAsync(alignedMotions, settings);

                    // Post-process;
                    blendedMotion = await PostProcessBlendedMotionAsync(blendedMotion, settings);

                    _logger.LogInformation("Motions blended successfully. Result duration: {Duration}s", blendedMotion.Duration);

                    RaiseImporterEvent(MotionImporterEventType.MotionsBlended,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionCount = motions.Count,
                            BlendedMotionId = blendedMotion.MotionId,
                            Duration = blendedMotion.Duration;
                        });

                    return blendedMotion;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to blend motions");
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionBlendingFailed);
                throw new MotionImporterException("Failed to blend motions", ex, ErrorCodes.MotionBlendingFailed);
            }
        }

        public async Task<ImportedMotion> MakeMotionLoopableAsync(ImportedMotion motion, LoopSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("MakeMotionLoopable"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    settings ??= new LoopSettings();

                    _logger.LogInformation("Making motion loopable: {MotionName}", motion.MotionName);

                    var loopableMotion = motion.Clone();

                    // Analyze motion for loop points;
                    var loopAnalysis = await AnalyzeMotionForLoopAsync(loopableMotion, settings);

                    // Create seamless loop;
                    loopableMotion = await CreateSeamlessLoopAsync(loopableMotion, loopAnalysis, settings);

                    // Adjust timing;
                    loopableMotion = await AdjustTimingForLoopAsync(loopableMotion, settings);

                    // Set loop flag;
                    loopableMotion.IsLoopable = true;
                    loopableMotion.LoopSettings = settings;

                    _logger.LogInformation("Motion made loopable. Loop points: {LoopPoints}",
                        string.Join(", ", loopAnalysis.LoopPoints.Select(p => p.ToString("F2"))));

                    RaiseImporterEvent(MotionImporterEventType.MotionMadeLoopable,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            LoopableMotionId = loopableMotion.MotionId,
                            LoopPoints = loopAnalysis.LoopPoints;
                        });

                    return loopableMotion;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to make motion loopable: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.LoopCreationFailed);
                throw new MotionImporterException("Failed to make motion loopable", ex, ErrorCodes.LoopCreationFailed);
            }
        }

        public async Task<AnimationClip> CreateAnimationClipAsync(ImportedMotion motion, ClipCreationSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("CreateAnimationClip"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    settings ??= new ClipCreationSettings();

                    _logger.LogInformation("Creating animation clip from motion: {MotionName}", motion.MotionName);

                    // Create animation clip;
                    var animationClip = new AnimationClip;
                    {
                        ClipId = Guid.NewGuid().ToString(),
                        ClipName = $"{motion.MotionName}_Clip",
                        Duration = motion.Duration,
                        FrameRate = motion.FrameRate,
                        IsLooping = motion.IsLoopable,
                        Skeleton = motion.Skeleton;
                    };

                    // Convert motion data to animation curves;
                    animationClip.Curves = await ConvertMotionToCurvesAsync(motion, settings);

                    // Add events;
                    if (settings.AddEvents)
                    {
                        animationClip.Events = await ExtractEventsFromMotionAsync(motion, settings);
                    }

                    // Add metadata;
                    animationClip.Metadata = new Dictionary<string, object>
                    {
                        ["SourceMotionId"] = motion.MotionId,
                        ["CreationTime"] = DateTime.UtcNow,
                        ["ImporterVersion"] = "1.0.0"
                    };

                    _logger.LogInformation("Animation clip created: {ClipName} ({CurveCount} curves)",
                        animationClip.ClipName, animationClip.Curves.Count);

                    RaiseImporterEvent(MotionImporterEventType.AnimationClipCreated,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            ClipId = animationClip.ClipId,
                            ClipName = animationClip.ClipName,
                            CurveCount = animationClip.Curves.Count;
                        });

                    return animationClip;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create animation clip from motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.AnimationClipCreationFailed);
                throw new MotionImporterException("Failed to create animation clip", ex, ErrorCodes.AnimationClipCreationFailed);
            }
        }

        public async Task<IStateMachine> CreateStateMachineFromMotionAsync(ImportedMotion motion, StateMachineSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("CreateStateMachine"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    settings ??= new StateMachineSettings();

                    _logger.LogInformation("Creating state machine from motion: {MotionName}", motion.MotionName);

                    // Analyze motion for states;
                    var motionAnalysis = await AnalyzeMotionAsync(motion);

                    // Extract states from motion;
                    var states = await ExtractStatesFromMotionAsync(motion, motionAnalysis, settings);

                    // Create transitions;
                    var transitions = await CreateTransitionsFromMotionAsync(motion, states, settings);

                    // Create state machine;
                    var stateMachine = new StateMachine(_logger, _eventBus, _errorReporter);

                    var config = new StateMachineConfiguration;
                    {
                        MachineName = $"{motion.MotionName}_StateMachine",
                        EnableDebugMode = settings.EnableDebugMode;
                    };

                    await stateMachine.InitializeAsync(config);

                    // Add states;
                    foreach (var state in states)
                    {
                        await stateMachine.AddStateAsync(state.StateId, new StateConfiguration;
                        {
                            StateName = state.StateName,
                            StateType = state.StateType;
                        });
                    }

                    // Add transitions;
                    foreach (var transition in transitions)
                    {
                        await stateMachine.AddTransitionAsync(transition.TransitionId, new TransitionConfiguration;
                        {
                            TransitionName = transition.TransitionName,
                            SourceStateId = transition.SourceStateId,
                            TargetStateId = transition.TargetStateId,
                            TransitionType = transition.TransitionType;
                        });
                    }

                    _logger.LogInformation("State machine created: {StateCount} states, {TransitionCount} transitions",
                        states.Count, transitions.Count);

                    RaiseImporterEvent(MotionImporterEventType.StateMachineCreated,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            StateMachineId = stateMachine.MachineId,
                            StateCount = states.Count,
                            TransitionCount = transitions.Count;
                        });

                    return stateMachine;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create state machine from motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.StateMachineCreationFailed);
                throw new MotionImporterException("Failed to create state machine", ex, ErrorCodes.StateMachineCreationFailed);
            }
        }

        public async Task<bool> SaveImportedMotionAsync(ImportedMotion motion, string filePath)
        {
            try
            {
                using (_performanceTracker.TrackOperation("SaveImportedMotion"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    if (string.IsNullOrWhiteSpace(filePath))
                        throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

                    _logger.LogInformation("Saving imported motion: {MotionName}", motion.MotionName);

                    // Serialize motion data;
                    var motionData = SerializeMotionData(motion);

                    // Save to file;
                    await File.WriteAllTextAsync(filePath, motionData);

                    _logger.LogInformation("Motion saved successfully to: {FilePath}", filePath);

                    RaiseImporterEvent(MotionImporterEventType.MotionSaved,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            FilePath = filePath;
                        });

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save imported motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionSaveFailed);
                throw new MotionImporterException("Failed to save motion", ex, ErrorCodes.MotionSaveFailed);
            }
        }

        public async Task<ImportedMotion> LoadImportedMotionAsync(string filePath)
        {
            try
            {
                using (_performanceTracker.TrackOperation("LoadImportedMotion"))
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                        throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

                    if (!File.Exists(filePath))
                        throw new FileNotFoundException($"Motion file not found: {filePath}");

                    _logger.LogInformation("Loading imported motion from: {FilePath}", filePath);

                    // Read file;
                    var motionData = await File.ReadAllTextAsync(filePath);

                    // Deserialize;
                    var motion = DeserializeMotionData(motionData);

                    // Add to imported motions;
                    lock (_syncLock)
                    {
                        _importedMotions[motion.MotionId] = motion;
                    }

                    _logger.LogInformation("Motion loaded successfully: {MotionName}", motion.MotionName);

                    RaiseImporterEvent(MotionImporterEventType.MotionLoaded,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            FilePath = filePath;
                        });

                    return motion;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load imported motion from: {FilePath}", filePath);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionLoadFailed);
                throw new MotionImporterException("Failed to load motion", ex, ErrorCodes.MotionLoadFailed);
            }
        }

        public async Task<MotionPreview> PreviewMotionAsync(ImportedMotion motion, PreviewSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("PreviewMotion"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    settings ??= new PreviewSettings();

                    _logger.LogDebug("Generating preview for motion: {MotionName}", motion.MotionName);

                    var preview = new MotionPreview;
                    {
                        PreviewId = Guid.NewGuid().ToString(),
                        MotionId = motion.MotionId,
                        GeneratedTime = DateTime.UtcNow,
                        Settings = settings;
                    };

                    // Generate preview frames;
                    preview.Frames = await GeneratePreviewFramesAsync(motion, settings);

                    // Generate thumbnail;
                    preview.Thumbnail = await GenerateThumbnailAsync(motion, settings);

                    // Generate statistics;
                    preview.Statistics = await GeneratePreviewStatisticsAsync(motion);

                    _logger.LogDebug("Preview generated: {FrameCount} frames", preview.Frames.Count);

                    return preview;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate preview for motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.PreviewGenerationFailed);
                throw new MotionImporterException("Failed to generate preview", ex, ErrorCodes.PreviewGenerationFailed);
            }
        }

        public async Task<MotionAnalysis> AnalyzeMotionAsync(ImportedMotion motion)
        {
            try
            {
                using (_performanceTracker.TrackOperation("AnalyzeMotion"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    _logger.LogInformation("Analyzing motion: {MotionName}", motion.MotionName);

                    var analysis = new MotionAnalysis;
                    {
                        MotionId = motion.MotionId,
                        AnalysisTime = DateTime.UtcNow;
                    };

                    // Basic statistics;
                    analysis.BasicStats = await CalculateBasicStatisticsAsync(motion);

                    // Quality metrics;
                    analysis.QualityMetrics = await CalculateQualityMetricsAsync(motion);

                    // Motion characteristics;
                    analysis.Characteristics = await ExtractMotionCharacteristicsAsync(motion);

                    // Issues and warnings;
                    analysis.Issues = await DetectIssuesAsync(motion);

                    // Recommendations;
                    analysis.Recommendations = await GenerateRecommendationsAsync(motion, analysis);

                    _logger.LogInformation("Motion analysis completed. Issues found: {IssueCount}", analysis.Issues.Count);

                    RaiseImporterEvent(MotionImporterEventType.MotionAnalyzed,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            IssueCount = analysis.Issues.Count,
                            QualityScore = analysis.QualityMetrics.OverallScore;
                        });

                    return analysis;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionAnalysisFailed);
                throw new MotionImporterException("Failed to analyze motion", ex, ErrorCodes.MotionAnalysisFailed);
            }
        }

        public async Task<ImportedMotion> CleanupMotionAsync(ImportedMotion motion, CleanupSettings settings)
        {
            try
            {
                using (_performanceTracker.TrackOperation("CleanupMotion"))
                {
                    if (motion == null)
                        throw new ArgumentNullException(nameof(motion));

                    settings ??= new CleanupSettings();

                    _logger.LogInformation("Cleaning up motion: {MotionName}", motion.MotionName);

                    var cleanedMotion = motion.Clone();

                    // Remove noise;
                    if (settings.RemoveNoise)
                    {
                        cleanedMotion = await RemoveNoiseAsync(cleanedMotion, settings);
                    }

                    // Fix broken curves;
                    if (settings.FixBrokenCurves)
                    {
                        cleanedMotion = await FixBrokenCurvesAsync(cleanedMotion, settings);
                    }

                    // Remove unnecessary bones;
                    if (settings.RemoveUnnecessaryBones)
                    {
                        cleanedMotion = await RemoveUnnecessaryBonesAsync(cleanedMotion, settings);
                    }

                    // Fix foot sliding;
                    if (settings.FixFootSliding)
                    {
                        cleanedMotion = await FixFootSlidingAsync(cleanedMotion, settings);
                    }

                    // Normalize rotations;
                    if (settings.NormalizeRotations)
                    {
                        cleanedMotion = await NormalizeRotationsAsync(cleanedMotion, settings);
                    }

                    _logger.LogInformation("Motion cleaned up. Issues fixed: {FixedIssues}",
                        string.Join(", ", cleanedMotion.Metadata.GetValueOrDefault("FixedIssues", new List<string>()) as List<string>));

                    RaiseImporterEvent(MotionImporterEventType.MotionCleanedUp,
                        new MotionImporterEventArgs;
                        {
                            ImporterId = _importerId,
                            MotionId = motion.MotionId,
                            CleanedMotionId = cleanedMotion.MotionId;
                        });

                    return cleanedMotion;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup motion: {MotionName}", motion?.MotionName);
                await _errorReporter.ReportErrorAsync(ex, ErrorCodes.MotionCleanupFailed);
                throw new MotionImporterException("Failed to cleanup motion", ex, ErrorCodes.MotionCleanupFailed);
            }
        }

        public void SubscribeToImporterEvent(MotionImporterEventType eventType, Action<MotionImporterEventArgs> handler)
        {
            lock (_syncLock)
            {
                if (!_eventHandlers.ContainsKey(eventType))
                {
                    _eventHandlers[eventType] = new List<Action<MotionImporterEventArgs>>();
                }

                _eventHandlers[eventType].Add(handler);
                _logger.LogDebug("Subscribed to importer event: {EventType}", eventType);
            }
        }

        public void UnsubscribeFromImporterEvent(MotionImporterEventType eventType, Action<MotionImporterEventArgs> handler)
        {
            lock (_syncLock)
            {
                if (_eventHandlers.ContainsKey(eventType))
                {
                    _eventHandlers[eventType].Remove(handler);
                    _logger.LogDebug("Unsubscribed from importer event: {EventType}", eventType);
                }
            }
        }

        public ImportProgress GetImportProgress()
        {
            lock (_syncLock)
            {
                return _currentProgress.Clone();
            }
        }

        public MotionImporterMetrics GetPerformanceMetrics()
        {
            return _performanceTracker.GetMetrics();
        }

        private void InitializeFormatHandlers()
        {
            // FBX format handler;
            _formatHandlers[MotionFileFormat.FBX] = new FBXFormatHandler(_logger);

            // BVH format handler;
            _formatHandlers[MotionFileFormat.BVH] = new BVHFormatHandler(_logger);

            // Collada format handler;
            _formatHandlers[MotionFileFormat.Collada] = new ColladaFormatHandler(_logger);

            // Mocap format handler;
            _formatHandlers[MotionFileFormat.Mocap] = new MocapFormatHandler(_logger);

            // Custom NEDA format;
            _formatHandlers[MotionFileFormat.NEDA] = new NEDAFormatHandler(_logger);

            _logger.LogInformation("Initialized {HandlerCount} format handlers", _formatHandlers.Count);
        }

        private MotionFileFormat DetectFileFormat(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch;
            {
                ".fbx" => MotionFileFormat.FBX,
                ".bvh" => MotionFileFormat.BVH,
                ".dae" => MotionFileFormat.Collada,
                ".c3d" => MotionFileFormat.Mocap,
                ".trc" => MotionFileFormat.Mocap,
                ".neda" => MotionFileFormat.NEDA,
                ".json" => MotionFileFormat.NEDA,
                _ => throw new MotionImporterException($"Unsupported file extension: {extension}",
                    ErrorCodes.UnsupportedFileExtension)
            };
        }

        private ImportedMotion CreateImportedMotion(MotionData motionData, string filePath, ImportOptions options)
        {
            return new ImportedMotion;
            {
                MotionId = Guid.NewGuid().ToString(),
                MotionName = Path.GetFileNameWithoutExtension(filePath),
                SourceFilePath = filePath,
                ImportTime = DateTime.UtcNow,
                FrameRate = motionData.FrameRate,
                Duration = motionData.Duration,
                FrameCount = motionData.Frames.Count,
                Skeleton = motionData.Skeleton,
                AnimationData = motionData.AnimationData,
                Metadata = new Dictionary<string, object>
                {
                    ["ImportOptions"] = options,
                    ["OriginalFormat"] = DetectFileFormat(filePath),
                    ["DataSize"] = CalculateDataSize(motionData)
                }
            };
        }

        private async Task<ImportedMotion> ProcessSkeletonAsync(ImportedMotion motion, SkeletonSettings settings)
        {
            // Skeleton processing logic;
            await Task.Delay(10); // Simulate processing;

            // Example: Fix bone orientations, scale, etc.
            var processedSkeleton = motion.Skeleton.Clone();

            // Apply settings;
            if (settings.FixBoneOrientations)
            {
                processedSkeleton = await FixBoneOrientationsAsync(processedSkeleton);
            }

            if (settings.NormalizeScale)
            {
                processedSkeleton = await NormalizeSkeletonScaleAsync(processedSkeleton, settings.TargetScale);
            }

            var processedMotion = motion.Clone();
            processedMotion.Skeleton = processedSkeleton;

            return processedMotion;
        }

        private async Task<ImportedMotion> ProcessAnimationDataAsync(ImportedMotion motion, AnimationSettings settings)
        {
            // Animation data processing logic;
            await Task.Delay(10); // Simulate processing;

            var processedData = motion.AnimationData.Clone();

            // Apply settings;
            if (settings.ConvertToLocalSpace)
            {
                processedData = await ConvertToLocalSpaceAsync(processedData, motion.Skeleton);
            }

            if (settings.FilterNoise)
            {
                processedData = await ApplyNoiseFilterAsync(processedData, settings.FilterStrength);
            }

            if (settings.Resample)
            {
                processedData = await ResampleAnimationAsync(processedData, settings.TargetFrameRate);
            }

            var processedMotion = motion.Clone();
            processedMotion.AnimationData = processedData;
            processedMotion.FrameRate = settings.TargetFrameRate;
            processedMotion.Duration = processedData.Frames.Count / (float)settings.TargetFrameRate;

            return processedMotion;
        }

        private string GenerateCacheKey(string filePath, ImportOptions options)
        {
            var fileInfo = new FileInfo(filePath);
            var optionsHash = options?.GetHashCode() ?? 0;

            return $"{filePath}_{fileInfo.LastWriteTime.Ticks}_{optionsHash}";
        }

        private MotionFileFormat GetFormatFromExportFormat(ExportFormat format)
        {
            return format switch;
            {
                ExportFormat.FBX => MotionFileFormat.FBX,
                ExportFormat.BVH => MotionFileFormat.BVH,
                ExportFormat.Collada => MotionFileFormat.Collada,
                ExportFormat.NEDA => MotionFileFormat.NEDA,
                _ => MotionFileFormat.NEDA;
            };
        }

        private MotionData PrepareMotionForExport(ImportedMotion motion)
        {
            return new MotionData;
            {
                Skeleton = motion.Skeleton,
                AnimationData = motion.AnimationData,
                FrameRate = motion.FrameRate,
                Duration = motion.Duration,
                Frames = motion.AnimationData.Frames,
                Metadata = motion.Metadata;
            };
        }

        private void UpdateProgress(ImportStatus status, int processed, int total, string currentFile)
        {
            lock (_syncLock)
            {
                _currentProgress.Status = status;
                _currentProgress.ProcessedFiles = processed;
                _currentProgress.TotalFiles = total;
                _currentProgress.CurrentFile = currentFile;
                _currentProgress.Percentage = total > 0 ? (int)((float)processed / total * 100) : 0;

                // Raise progress event;
                RaiseImporterEvent(MotionImporterEventType.ImportProgress,
                    new MotionImporterEventArgs;
                    {
                        ImporterId = _importerId,
                        Progress = _currentProgress.Clone()
                    });
            }
        }

        private void RaiseImporterEvent(MotionImporterEventType eventType, MotionImporterEventArgs args)
        {
            lock (_syncLock)
            {
                if (_eventHandlers.ContainsKey(eventType))
                {
                    foreach (var handler in _eventHandlers[eventType].ToList())
                    {
                        try
                        {
                            handler?.Invoke(args);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in importer event handler for {EventType}", eventType);
                        }
                    }
                }
            }
        }

        private async Task HandleImporterEvent(MotionImporterEvent @event)
        {
            // External event'leri işle;
            await Task.CompletedTask;
        }

        private string SerializeMotionData(ImportedMotion motion)
        {
            // JSON serialization;
            var options = new System.Text.Json.JsonSerializerOptions;
            {
                WriteIndented = true,
                Converters = { new MotionDataJsonConverter() }
            };

            return System.Text.Json.JsonSerializer.Serialize(motion, options);
        }

        private ImportedMotion DeserializeMotionData(string json)
        {
            // JSON deserialization;
            var options = new System.Text.Json.JsonSerializerOptions;
            {
                Converters = { new MotionDataJsonConverter() }
            };

            return System.Text.Json.JsonSerializer.Deserialize<ImportedMotion>(json, options);
        }

        private long CalculateDataSize(MotionData motionData)
        {
            // Approximate data size calculation;
            var frameSize = motionData.Skeleton.Bones.Count * (3 * 4 + 4 * 4); // position + rotation;
            return frameSize * motionData.Frames.Count;
        }

        // Helper methods for various operations;
        private async Task<ImportedMotion> ReduceKeyframesAsync(ImportedMotion motion, OptimizationSettings settings)
        {
            // Keyframe reduction logic;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> OptimizeCurvesAsync(ImportedMotion motion, OptimizationSettings settings)
        {
            // Curve optimization logic;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> CompressMotionDataAsync(ImportedMotion motion, OptimizationSettings settings)
        {
            // Data compression logic;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> NormalizeMotionAsync(ImportedMotion motion, OptimizationSettings settings)
        {
            // Normalization logic;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<Dictionary<string, BoneMapping>> CreateBoneMappingAsync(
            SkeletonDefinition source,
            SkeletonDefinition target,
            RetargetSettings settings)
        {
            // Bone mapping logic;
            await Task.Delay(10);
            return new Dictionary<string, BoneMapping>();
        }

        private async Task<ImportedMotion> PerformRetargetingAsync(
            ImportedMotion motion,
            SkeletonDefinition source,
            SkeletonDefinition target,
            Dictionary<string, BoneMapping> mapping,
            RetargetSettings settings)
        {
            // Retargeting logic;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<bool> ValidateMotionsForBlendingAsync(List<ImportedMotion> motions, BlendSettings settings)
        {
            // Validation logic;
            await Task.Delay(10);
            return true;
        }

        private async Task<List<ImportedMotion>> AlignMotionsAsync(List<ImportedMotion> motions, BlendSettings settings)
        {
            // Alignment logic;
            await Task.Delay(10);
            return motions.Select(m => m.Clone()).ToList();
        }

        private async Task<ImportedMotion> PerformBlendingAsync(List<ImportedMotion> motions, BlendSettings settings)
        {
            // Blending logic;
            await Task.Delay(10);
            return motions.First().Clone();
        }

        private async Task<LoopAnalysis> AnalyzeMotionForLoopAsync(ImportedMotion motion, LoopSettings settings)
        {
            // Loop analysis logic;
            await Task.Delay(10);
            return new LoopAnalysis();
        }

        private async Task<Dictionary<string, AnimationCurve>> ConvertMotionToCurvesAsync(
            ImportedMotion motion,
            ClipCreationSettings settings)
        {
            // Curve conversion logic;
            await Task.Delay(10);
            return new Dictionary<string, AnimationCurve>();
        }

        private async Task<List<AnimationEvent>> ExtractEventsFromMotionAsync(
            ImportedMotion motion,
            ClipCreationSettings settings)
        {
            // Event extraction logic;
            await Task.Delay(10);
            return new List<AnimationEvent>();
        }

        private async Task<List<MotionState>> ExtractStatesFromMotionAsync(
            ImportedMotion motion,
            MotionAnalysis analysis,
            StateMachineSettings settings)
        {
            // State extraction logic;
            await Task.Delay(10);
            return new List<MotionState>();
        }

        private async Task<List<MotionTransition>> CreateTransitionsFromMotionAsync(
            ImportedMotion motion,
            List<MotionState> states,
            StateMachineSettings settings)
        {
            // Transition creation logic;
            await Task.Delay(10);
            return new List<MotionTransition>();
        }

        private async Task<List<PreviewFrame>> GeneratePreviewFramesAsync(ImportedMotion motion, PreviewSettings settings)
        {
            // Preview frame generation logic;
            await Task.Delay(10);
            return new List<PreviewFrame>();
        }

        private async Task<byte[]> GenerateThumbnailAsync(ImportedMotion motion, PreviewSettings settings)
        {
            // Thumbnail generation logic;
            await Task.Delay(10);
            return new byte[0];
        }

        private async Task<MotionStatistics> GeneratePreviewStatisticsAsync(ImportedMotion motion)
        {
            // Statistics generation logic;
            await Task.Delay(10);
            return new MotionStatistics();
        }

        private async Task<BasicStatistics> CalculateBasicStatisticsAsync(ImportedMotion motion)
        {
            // Basic statistics calculation;
            await Task.Delay(10);
            return new BasicStatistics();
        }

        private async Task<QualityMetrics> CalculateQualityMetricsAsync(ImportedMotion motion)
        {
            // Quality metrics calculation;
            await Task.Delay(10);
            return new QualityMetrics();
        }

        private async Task<MotionCharacteristics> ExtractMotionCharacteristicsAsync(ImportedMotion motion)
        {
            // Characteristics extraction;
            await Task.Delay(10);
            return new MotionCharacteristics();
        }

        private async Task<List<MotionIssue>> DetectIssuesAsync(ImportedMotion motion)
        {
            // Issue detection;
            await Task.Delay(10);
            return new List<MotionIssue>();
        }

        private async Task<List<Recommendation>> GenerateRecommendationsAsync(ImportedMotion motion, MotionAnalysis analysis)
        {
            // Recommendation generation;
            await Task.Delay(10);
            return new List<Recommendation>();
        }

        private async Task<ImportedMotion> RemoveNoiseAsync(ImportedMotion motion, CleanupSettings settings)
        {
            // Noise removal;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> FixBrokenCurvesAsync(ImportedMotion motion, CleanupSettings settings)
        {
            // Curve fixing;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> RemoveUnnecessaryBonesAsync(ImportedMotion motion, CleanupSettings settings)
        {
            // Bone removal;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> FixFootSlidingAsync(ImportedMotion motion, CleanupSettings settings)
        {
            // Foot sliding fix;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> NormalizeRotationsAsync(ImportedMotion motion, CleanupSettings settings)
        {
            // Rotation normalization;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<SkeletonDefinition> FixBoneOrientationsAsync(SkeletonDefinition skeleton)
        {
            // Bone orientation fix;
            await Task.Delay(10);
            return skeleton.Clone();
        }

        private async Task<SkeletonDefinition> NormalizeSkeletonScaleAsync(SkeletonDefinition skeleton, float targetScale)
        {
            // Scale normalization;
            await Task.Delay(10);
            return skeleton.Clone();
        }

        private async Task<AnimationData> ConvertToLocalSpaceAsync(AnimationData data, SkeletonDefinition skeleton)
        {
            // Space conversion;
            await Task.Delay(10);
            return data.Clone();
        }

        private async Task<AnimationData> ApplyNoiseFilterAsync(AnimationData data, float strength)
        {
            // Noise filtering;
            await Task.Delay(10);
            return data.Clone();
        }

        private async Task<AnimationData> ResampleAnimationAsync(AnimationData data, float targetFrameRate)
        {
            // Resampling;
            await Task.Delay(10);
            return data.Clone();
        }

        private async Task<ImportedMotion> PostProcessRetargetedMotionAsync(ImportedMotion motion, RetargetSettings settings)
        {
            // Post-processing;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> PostProcessBlendedMotionAsync(ImportedMotion motion, BlendSettings settings)
        {
            // Post-processing;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> CreateSeamlessLoopAsync(ImportedMotion motion, LoopAnalysis analysis, LoopSettings settings)
        {
            // Loop creation;
            await Task.Delay(10);
            return motion.Clone();
        }

        private async Task<ImportedMotion> AdjustTimingForLoopAsync(ImportedMotion motion, LoopSettings settings)
        {
            // Timing adjustment;
            await Task.Delay(10);
            return motion.Clone();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                _logger.LogInformation("Disposing MotionImporter: {ImporterName}", _importerName);

                // Cancel any ongoing import;
                _importCancellationTokenSource?.Cancel();

                // Clear cache;
                _motionCache.Clear();

                // Clear format handlers;
                _formatHandlers.Clear();

                // Clear imported motions;
                _importedMotions.Clear();

                // Clear event handlers;
                _eventHandlers.Clear();

                _isDisposed = true;

                _logger.LogInformation("MotionImporter disposed: {ImporterName}", _importerName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing MotionImporter");
            }
        }
    }

    // Format Handler Interfaces and Implementations;

    public interface IMotionFormatHandler;
    {
        MotionFileFormat Format { get; }
        Task<MotionData> ImportAsync(string filePath, ImportOptions options);
        Task<bool> ExportAsync(MotionData motionData, string outputPath);
        IReadOnlyList<string> SupportedExtensions { get; }
    }

    internal class FBXFormatHandler : IMotionFormatHandler;
    {
        private readonly ILogger _logger;

        public MotionFileFormat Format => MotionFileFormat.FBX;
        public IReadOnlyList<string> SupportedExtensions => new[] { ".fbx" };

        public FBXFormatHandler(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<MotionData> ImportAsync(string filePath, ImportOptions options)
        {
            _logger.LogInformation("Importing FBX file: {FilePath}", filePath);

            // FBX import logic here;
            // This would integrate with FBX SDK or Assimp;
            await Task.Delay(100); // Simulate import;

            return new MotionData;
            {
                Skeleton = new SkeletonDefinition(),
                AnimationData = new AnimationData(),
                FrameRate = 30.0f,
                Duration = 10.0f,
                Frames = new List<AnimationFrame>(),
                Metadata = new Dictionary<string, object>
                {
                    ["Format"] = "FBX",
                    ["ImportedBy"] = "FBXFormatHandler"
                }
            };
        }

        public async Task<bool> ExportAsync(MotionData motionData, string outputPath)
        {
            _logger.LogInformation("Exporting to FBX: {OutputPath}", outputPath);

            // FBX export logic here;
            await Task.Delay(100); // Simulate export;

            return true;
        }
    }

    internal class BVHFormatHandler : IMotionFormatHandler;
    {
        private readonly ILogger _logger;

        public MotionFileFormat Format => MotionFileFormat.BVH;
        public IReadOnlyList<string> SupportedExtensions => new[] { ".bvh" };

        public BVHFormatHandler(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<MotionData> ImportAsync(string filePath, ImportOptions options)
        {
            _logger.LogInformation("Importing BVH file: {FilePath}", filePath);

            // BVH import logic here;
            await Task.Delay(100); // Simulate import;

            return new MotionData;
            {
                Skeleton = new SkeletonDefinition(),
                AnimationData = new AnimationData(),
                FrameRate = 30.0f,
                Duration = 10.0f,
                Frames = new List<AnimationFrame>(),
                Metadata = new Dictionary<string, object>
                {
                    ["Format"] = "BVH",
                    ["ImportedBy"] = "BVHFormatHandler"
                }
            };
        }

        public async Task<bool> ExportAsync(MotionData motionData, string outputPath)
        {
            _logger.LogInformation("Exporting to BVH: {OutputPath}", outputPath);

            // BVH export logic here;
            await Task.Delay(100); // Simulate export;

            return true;
        }
    }

    internal class ColladaFormatHandler : IMotionFormatHandler;
    {
        private readonly ILogger _logger;

        public MotionFileFormat Format => MotionFileFormat.Collada;
        public IReadOnlyList<string> SupportedExtensions => new[] { ".dae" };

        public ColladaFormatHandler(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<MotionData> ImportAsync(string filePath, ImportOptions options)
        {
            _logger.LogInformation("Importing Collada file: {FilePath}", filePath);

            // Collada import logic here;
            await Task.Delay(100); // Simulate import;

            return new MotionData;
            {
                Skeleton = new SkeletonDefinition(),
                AnimationData = new AnimationData(),
                FrameRate = 30.0f,
                Duration = 10.0f,
                Frames = new List<AnimationFrame>(),
                Metadata = new Dictionary<string, object>
                {
                    ["Format"] = "Collada",
                    ["ImportedBy"] = "ColladaFormatHandler"
                }
            };
        }

        public async Task<bool> ExportAsync(MotionData motionData, string outputPath)
        {
            _logger.LogInformation("Exporting to Collada: {OutputPath}", outputPath);

            // Collada export logic here;
            await Task.Delay(100); // Simulate export;

            return true;
        }
    }

    internal class MocapFormatHandler : IMotionFormatHandler;
    {
        private readonly ILogger _logger;

        public MotionFileFormat Format => MotionFileFormat.Mocap;
        public IReadOnlyList<string> SupportedExtensions => new[] { ".c3d", ".trc" };

        public MocapFormatHandler(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<MotionData> ImportAsync(string filePath, ImportOptions options)
        {
            _logger.LogInformation("Importing Mocap file: {FilePath}", filePath);

            // Mocap import logic here;
            await Task.Delay(100); // Simulate import;

            return new MotionData;
            {
                Skeleton = new SkeletonDefinition(),
                AnimationData = new AnimationData(),
                FrameRate = 120.0f, // High frame rate for mocap;
                Duration = 10.0f,
                Frames = new List<AnimationFrame>(),
                Metadata = new Dictionary<string, object>
                {
                    ["Format"] = "Mocap",
                    ["ImportedBy"] = "MocapFormatHandler"
                }
            };
        }

        public async Task<bool> ExportAsync(MotionData motionData, string outputPath)
        {
            _logger.LogInformation("Exporting to Mocap format: {OutputPath}", outputPath);

            // Mocap export logic here;
            await Task.Delay(100); // Simulate export;

            return true;
        }
    }

    internal class NEDAFormatHandler : IMotionFormatHandler;
    {
        private readonly ILogger _logger;

        public MotionFileFormat Format => MotionFileFormat.NEDA;
        public IReadOnlyList<string> SupportedExtensions => new[] { ".neda", ".json" };

        public NEDAFormatHandler(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<MotionData> ImportAsync(string filePath, ImportOptions options)
        {
            _logger.LogInformation("Importing NEDA format file: {FilePath}", filePath);

            // NEDA format import logic here;
            var json = await File.ReadAllTextAsync(filePath);
            var motionData = System.Text.Json.JsonSerializer.Deserialize<MotionData>(json);

            return motionData;
        }

        public async Task<bool> ExportAsync(MotionData motionData, string outputPath)
        {
            _logger.LogInformation("Exporting to NEDA format: {OutputPath}", outputPath);

            // NEDA format export logic here;
            var json = System.Text.Json.JsonSerializer.Serialize(motionData, new System.Text.Json.JsonSerializerOptions;
            {
                WriteIndented = true;
            });

            await File.WriteAllTextAsync(outputPath, json);

            return true;
        }
    }

    // Cache System;

    internal class MotionCache;
    {
        private readonly Dictionary<string, ImportedMotion> _cache = new Dictionary<string, ImportedMotion>();
        private readonly object _syncLock = new object();
        private CacheSettings _settings;

        public void Initialize(CacheSettings settings)
        {
            _settings = settings;
        }

        public bool TryGetMotion(string key, out ImportedMotion motion)
        {
            lock (_syncLock)
            {
                if (_cache.TryGetValue(key, out motion))
                {
                    // Check if cache entry is still valid;
                    if (IsCacheEntryValid(key, motion))
                    {
                        return true;
                    }

                    // Remove expired entry
                    _cache.Remove(key);
                }

                motion = null;
                return false;
            }
        }

        public void AddMotion(string key, ImportedMotion motion)
        {
            lock (_syncLock)
            {
                // Check cache size limit;
                if (_cache.Count >= _settings.MaxCacheSize)
                {
                    RemoveOldestEntries();
                }

                _cache[key] = motion.Clone();

                // Add metadata;
                motion.Metadata["CachedTime"] = DateTime.UtcNow;
                motion.Metadata["CacheKey"] = key;
            }
        }

        public void Clear()
        {
            lock (_syncLock)
            {
                _cache.Clear();
            }
        }

        private bool IsCacheEntryValid(string key, ImportedMotion motion)
        {
            if (!motion.Metadata.TryGetValue("CachedTime", out var cachedTimeObj) ||
                !(cachedTimeObj is DateTime cachedTime))
            {
                return false;
            }

            var age = DateTime.UtcNow - cachedTime;
            return age.TotalMinutes < _settings.CacheDurationMinutes;
        }

        private void RemoveOldestEntries()
        {
            var entriesToRemove = _cache.OrderBy(kvp =>
            {
                if (kvp.Value.Metadata.TryGetValue("CachedTime", out var time) && time is DateTime cachedTime)
                    return cachedTime;
                return DateTime.MinValue;
            })
                .Take(_cache.Count - _settings.MaxCacheSize + 1)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in entriesToRemove)
            {
                _cache.Remove(key);
            }
        }
    }

    // Performance Tracker;

    internal class PerformanceTracker;
    {
        private readonly object _syncLock = new object();
        private readonly Dictionary<string, List<float>> _operationTimes = new Dictionary<string, List<float>>();
        private DateTime _startTime;

        public PerformanceTracker()
        {
            _startTime = DateTime.UtcNow;
        }

        public IDisposable TrackOperation(string operationName)
        {
            return new OperationTracker(this, operationName);
        }

        public void RecordOperationTime(string operationName, float time)
        {
            lock (_syncLock)
            {
                if (!_operationTimes.ContainsKey(operationName))
                {
                    _operationTimes[operationName] = new List<float>();
                }

                _operationTimes[operationName].Add(time);

                // Keep only last 100 samples;
                if (_operationTimes[operationName].Count > 100)
                {
                    _operationTimes[operationName].RemoveAt(0);
                }
            }
        }

        public MotionImporterMetrics GetMetrics()
        {
            lock (_syncLock)
            {
                var metrics = new MotionImporterMetrics;
                {
                    TotalRuntime = (float)(DateTime.UtcNow - _startTime).TotalSeconds,
                    OperationMetrics = new Dictionary<string, OperationMetrics>()
                };

                foreach (var kvp in _operationTimes)
                {
                    if (kvp.Value.Count > 0)
                    {
                        metrics.OperationMetrics[kvp.Key] = new OperationMetrics;
                        {
                            CallCount = kvp.Value.Count,
                            AverageTime = kvp.Value.Average(),
                            MinTime = kvp.Value.Min(),
                            MaxTime = kvp.Value.Max(),
                            TotalTime = kvp.Value.Sum()
                        };
                    }
                }

                return metrics;
            }
        }

        public void Reset()
        {
            lock (_syncLock)
            {
                _operationTimes.Clear();
                _startTime = DateTime.UtcNow;
            }
        }

        private class OperationTracker : IDisposable
        {
            private readonly PerformanceTracker _tracker;
            private readonly string _operationName;
            private readonly DateTime _startTime;
            private bool _isDisposed;

            public OperationTracker(PerformanceTracker tracker, string operationName)
            {
                _tracker = tracker;
                _operationName = operationName;
                _startTime = DateTime.UtcNow;
            }

            public void Dispose()
            {
                if (_isDisposed)
                    return;

                var elapsed = (float)(DateTime.UtcNow - _startTime).TotalSeconds;
                _tracker.RecordOperationTime(_operationName, elapsed);
                _isDisposed = true;
            }
        }
    }

    // JSON Converter;

    internal class MotionDataJsonConverter : System.Text.Json.Serialization.JsonConverter<ImportedMotion>
    {
        public override ImportedMotion Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            return System.Text.Json.JsonSerializer.Deserialize<ImportedMotion>(ref reader, options);
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, ImportedMotion value, System.Text.Json.JsonSerializerOptions options)
        {
            System.Text.Json.JsonSerializer.Serialize(writer, value, options);
        }
    }

    // Enum and Data Class Definitions;

    public enum MotionFileFormat;
    {
        FBX,
        BVH,
        Collada,
        Mocap,
        NEDA,
        Unknown;
    }

    public enum ExportFormat;
    {
        FBX,
        BVH,
        Collada,
        NEDA,
        Unity,
        Unreal;
    }

    public enum ImportStatus;
    {
        Idle,
        Importing,
        Processing,
        Completed,
        Failed,
        Cancelled;
    }

    public enum MotionImporterEventType;
    {
        ImporterInitialized,
        ImporterStarted,
        ImporterStopped,
        ImporterRenamed,
        ImportStarted,
        ImportProgress,
        ImportCompleted,
        ImportFailed,
        BatchImportStarted,
        BatchImportCompleted,
        BatchImportCompletedWithErrors,
        MotionImported,
        MotionExported,
        MotionOptimized,
        MotionRetargeted,
        MotionsBlended,
        MotionMadeLoopable,
        AnimationClipCreated,
        StateMachineCreated,
        MotionSaved,
        MotionLoaded,
        MotionAnalyzed,
        MotionCleanedUp,
        PreviewGenerated,
        ErrorOccurred;
    }

    // Configuration Classes;

    public class MotionImporterConfiguration;
    {
        public string ImporterName { get; set; } = "Motion Importer";
        public bool UseCache { get; set; } = true;
        public CacheSettings CacheSettings { get; set; } = new CacheSettings();
        public bool EnableLogging { get; set; } = true;
        public bool EnablePerformanceTracking { get; set; } = true;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class ImportOptions;
    {
        public bool ProcessSkeleton { get; set; } = true;
        public bool ProcessAnimation { get; set; } = true;
        public SkeletonSettings SkeletonSettings { get; set; } = new SkeletonSettings();
        public AnimationSettings AnimationSettings { get; set; } = new AnimationSettings();
        public bool ImportMetadata { get; set; } = true;
        public bool CreatePreview { get; set; } = true;
        public float ScaleFactor { get; set; } = 1.0f;
        public CoordinateSystem TargetCoordinateSystem { get; set; } = CoordinateSystem.RightHanded;
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    public class SkeletonSettings;
    {
        public bool FixBoneOrientations { get; set; } = true;
        public bool NormalizeScale { get; set; } = true;
        public float TargetScale { get; set; } = 1.0f;
        public bool RemoveEndEffectors { get; set; } = false;
        public bool CreateVirtualRoot { get; set; } = true;
    }

    public class AnimationSettings;
    {
        public bool ConvertToLocalSpace { get; set; } = true;
        public bool FilterNoise { get; set; } = true;
        public float FilterStrength { get; set; } = 0.1f;
        public bool Resample { get; set; } = false;
        public float TargetFrameRate { get; set; } = 30.0f;
        public bool RemoveTranslation { get; set; } = false;
        public bool ExtractRootMotion { get; set; } = true;
    }

    public class OptimizationSettings;
    {
        public bool ReduceKeyframes { get; set; } = true;
        public float KeyframeThreshold { get; set; } = 0.01f;
        public bool OptimizeCurves { get; set; } = true;
        public float CurveTolerance { get; set; } = 0.001f;
        public bool CompressData { get; set; } = false;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Medium;
        public bool Normalize { get; set; } = true;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class RetargetSettings;
    {
        public RetargetMethod Method { get; set; } = RetargetMethod.Auto;
        public bool MaintainProportions { get; set; } = true;
        public bool PreserveMotion { get; set; } = true;
        public float TranslationScale { get; set; } = 1.0f;
        public bool FixFeet { get; set; } = true;
        public bool AlignHips { get; set; } = true;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class BlendSettings;
    {
        public BlendMethod Method { get; set; } = BlendMethod.Linear;
        public List<float> Weights { get; set; } = new List<float>();
        public bool AlignRoot { get; set; } = true;
        public bool MatchDuration { get; set; } = true;
        public float BlendDuration { get; set; } = 0.3f;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class LoopSettings;
    {
        public bool AutoDetect { get; set; } = true;
        public List<float> ManualLoopPoints { get; set; } = new List<float>();
        public float SearchWindow { get; set; } = 0.5f;
        public float Tolerance { get; set; } = 0.01f;
        public bool CreatePingPong { get; set; } = false;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class ClipCreationSettings;
    {
        public string ClipName { get; set; }
        public bool AddEvents { get; set; } = true;
        public float EventThreshold { get; set; } = 0.1f;
        public bool GenerateCurves { get; set; } = true;
        public CurveGenerationMethod CurveMethod { get; set; } = CurveGenerationMethod.Auto;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class StateMachineSettings;
    {
        public bool EnableDebugMode { get; set; } = false;
        public float StateDetectionThreshold { get; set; } = 0.2f;
        public bool CreateTransitions { get; set; } = true;
        public float TransitionThreshold { get; set; } = 0.1f;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class PreviewSettings;
    {
        public int FrameCount { get; set; } = 10;
        public int ThumbnailWidth { get; set; } = 256;
        public int ThumbnailHeight { get; set; } = 256;
        public bool GenerateStatistics { get; set; } = true;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class CleanupSettings;
    {
        public bool RemoveNoise { get; set; } = true;
        public float NoiseThreshold { get; set; } = 0.01f;
        public bool FixBrokenCurves { get; set; } = true;
        public bool RemoveUnnecessaryBones { get; set; } = false;
        public bool FixFootSliding { get; set; } = true;
        public bool NormalizeRotations { get; set; } = true;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new Dictionary<string, object>();
    }

    public class CacheSettings;
    {
        public int MaxCacheSize { get; set; } = 100;
        public int CacheDurationMinutes { get; set; } = 60;
        public bool EnableDiskCache { get; set; } = false;
        public string CacheDirectory { get; set; } = "Cache/Motions";
    }

    // Data Classes;

    public class ImportedMotion;
    {
        public string MotionId { get; set; }
        public string MotionName { get; set; }
        public string SourceFilePath { get; set; }
        public DateTime ImportTime { get; set; }
        public float FrameRate { get; set; }
        public float Duration { get; set; }
        public int FrameCount { get; set; }
        public SkeletonDefinition Skeleton { get; set; }
        public AnimationData AnimationData { get; set; }
        public bool IsLoopable { get; set; }
        public LoopSettings LoopSettings { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public long DataSize => Metadata.TryGetValue("DataSize", out var size) ? (long)size : 0;

        public ImportedMotion Clone()
        {
            return new ImportedMotion;
            {
                MotionId = Guid.NewGuid().ToString(),
                MotionName = $"{MotionName}_Clone",
                SourceFilePath = SourceFilePath,
                ImportTime = DateTime.UtcNow,
                FrameRate = FrameRate,
                Duration = Duration,
                FrameCount = FrameCount,
                Skeleton = Skeleton?.Clone(),
                AnimationData = AnimationData?.Clone(),
                IsLoopable = IsLoopable,
                LoopSettings = LoopSettings,
                Metadata = new Dictionary<string, object>(Metadata)
            };
        }
    }

    public class MotionData;
    {
        public SkeletonDefinition Skeleton { get; set; }
        public AnimationData AnimationData { get; set; }
        public float FrameRate { get; set; }
        public float Duration { get; set; }
        public List<AnimationFrame> Frames { get; set; } = new List<AnimationFrame>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class SkeletonDefinition;
    {
        public string SkeletonId { get; set; }
        public string SkeletonName { get; set; }
        public List<BoneDefinition> Bones { get; set; } = new List<BoneDefinition>();
        public Dictionary<string, string> BoneMapping { get; set; } = new Dictionary<string, string>();
        public Vector3 RootOffset { get; set; }
        public float Scale { get; set; } = 1.0f;
        public CoordinateSystem CoordinateSystem { get; set; }

        public SkeletonDefinition Clone()
        {
            return new SkeletonDefinition;
            {
                SkeletonId = Guid.NewGuid().ToString(),
                SkeletonName = $"{SkeletonName}_Clone",
                Bones = Bones.Select(b => b.Clone()).ToList(),
                BoneMapping = new Dictionary<string, string>(BoneMapping),
                RootOffset = RootOffset,
                Scale = Scale,
                CoordinateSystem = CoordinateSystem;
            };
        }
    }

    public class BoneDefinition;
    {
        public string BoneId { get; set; }
        public string BoneName { get; set; }
        public string ParentId { get; set; }
        public Vector3 LocalPosition { get; set; }
        public Quaternion LocalRotation { get; set; }
        public Vector3 BindPosePosition { get; set; }
        public Quaternion BindPoseRotation { get; set; }
        public BoneType Type { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public BoneDefinition Clone()
        {
            return new BoneDefinition;
            {
                BoneId = BoneId,
                BoneName = BoneName,
                ParentId = ParentId,
                LocalPosition = LocalPosition,
                LocalRotation = LocalRotation,
                BindPosePosition = BindPosePosition,
                BindPoseRotation = BindPoseRotation,
                Type = Type,
                Properties = new Dictionary<string, object>(Properties)
            };
        }
    }

    public class AnimationData;
    {
        public List<AnimationFrame> Frames { get; set; } = new List<AnimationFrame>();
        public Dictionary<string, AnimationCurve> Curves { get; set; } = new Dictionary<string, AnimationCurve>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public AnimationData Clone()
        {
            return new AnimationData;
            {
                Frames = Frames.Select(f => f.Clone()).ToList(),
                Curves = Curves.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone()),
                Metadata = new Dictionary<string, object>(Metadata)
            };
        }
    }

    public class AnimationFrame;
    {
        public float Time { get; set; }
        public Dictionary<string, BonePose> BonePoses { get; set; } = new Dictionary<string, BonePose>();
        public RootMotionData RootMotion { get; set; }

        public AnimationFrame Clone()
        {
            return new AnimationFrame;
            {
                Time = Time,
                BonePoses = BonePoses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone()),
                RootMotion = RootMotion?.Clone()
            };
        }
    }

    public class BonePose;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; } = Vector3.One;

        public BonePose Clone()
        {
            return new BonePose;
            {
                Position = Position,
                Rotation = Rotation,
                Scale = Scale;
            };
        }
    }

    public class RootMotionData;
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Velocity { get; set; }
        public Vector3 AngularVelocity { get; set; }

        public RootMotionData Clone()
        {
            return new RootMotionData;
            {
                Position = Position,
                Rotation = Rotation,
                Velocity = Velocity,
                AngularVelocity = AngularVelocity;
            };
        }
    }

    public class AnimationCurve;
    {
        public string CurveId { get; set; }
        public string BoneId { get; set; }
        public CurveType Type { get; set; }
        public List<Keyframe> Keyframes { get; set; } = new List<Keyframe>();
        public WrapMode PreWrapMode { get; set; } = WrapMode.Clamp;
        public WrapMode PostWrapMode { get; set; } = WrapMode.Clamp;

        public AnimationCurve Clone()
        {
            return new AnimationCurve;
            {
                CurveId = Guid.NewGuid().ToString(),
                BoneId = BoneId,
                Type = Type,
                Keyframes = Keyframes.Select(k => k.Clone()).ToList(),
                PreWrapMode = PreWrapMode,
                PostWrapMode = PostWrapMode;
            };
        }
    }

    public class Keyframe;
    {
        public float Time { get; set; }
        public float Value { get; set; }
        public float InTangent { get; set; }
        public float OutTangent { get; set; }
        public TangentMode TangentMode { get; set; }

        public Keyframe Clone()
        {
            return new Keyframe;
            {
                Time = Time,
                Value = Value,
                InTangent = InTangent,
                OutTangent = OutTangent,
                TangentMode = TangentMode;
            };
        }
    }

    // Event and Analysis Classes;

    public class MotionImporterEventArgs : EventArgs;
    {
        public Guid ImporterId { get; set; }
        public string ImporterName { get; set; }
        public string MotionId { get; set; }
        public string MotionName { get; set; }
        public string FilePath { get; set; }
        public string OutputPath { get; set; }
        public MotionFileFormat Format { get; set; }
        public ExportFormat ExportFormat { get; set; }
        public ImportProgress Progress { get; set; }
        public bool FromCache { get; set; }
        public int FrameCount { get; set; }
        public float Duration { get; set; }
        public string OldName { get; set; }
        public string NewName { get; set; }
        public string ErrorMessage { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public List<string> FailedFiles { get; set; }
        public string OptimizedMotionId { get; set; }
        public string RetargetedMotionId { get; set; }
        public string BlendedMotionId { get; set; }
        public string LoopableMotionId { get; set; }
        public string CleanedMotionId { get; set; }
        public string ClipId { get; set; }
        public string ClipName { get; set; }
        public Guid StateMachineId { get; set; }
        public long OriginalSize { get; set; }
        public long OptimizedSize { get; set; }
        public int SourceBoneCount { get; set; }
        public int TargetBoneCount { get; set; }
        public int MotionCount { get; set; }
        public int CurveCount { get; set; }
        public int StateCount { get; set; }
        public int TransitionCount { get; set; }
        public int IssueCount { get; set; }
        public float QualityScore { get; set; }
        public List<float> LoopPoints { get; set; }
    }

    public class ImportProgress;
    {
        public ImportStatus Status { get; set; }
        public int ProcessedFiles { get; set; }
        public int TotalFiles { get; set; }
        public string CurrentFile { get; set; }
        public int Percentage { get; set; }

        public ImportProgress Clone()
        {
            return new ImportProgress;
            {
                Status = Status,
                ProcessedFiles = ProcessedFiles,
                TotalFiles = TotalFiles,
                CurrentFile = CurrentFile,
                Percentage = Percentage;
            };
        }
    }

    public class MotionPreview;
    {
        public string PreviewId { get; set; }
        public string MotionId { get; set; }
        public List<PreviewFrame> Frames { get; set; } = new List<PreviewFrame>();
        public byte[] Thumbnail { get; set; }
        public MotionStatistics Statistics { get; set; }
        public DateTime GeneratedTime { get; set; }
        public PreviewSettings Settings { get; set; }
    }

    public class PreviewFrame;
    {
        public float Time { get; set; }
        public byte[] ImageData { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }

    public class MotionAnalysis;
    {
        public string MotionId { get; set; }
        public BasicStatistics BasicStats { get; set; }
        public QualityMetrics QualityMetrics { get; set; }
        public MotionCharacteristics Characteristics { get; set; }
        public List<MotionIssue> Issues { get; set; } = new List<MotionIssue>();
        public List<Recommendation> Recommendations { get; set; } = new List<Recommendation>();
        public DateTime AnalysisTime { get; set; }
    }

    public class BasicStatistics;
    {
        public int BoneCount { get; set; }
        public int FrameCount { get; set; }
        public float Duration { get; set; }
        public float FrameRate { get; set; }
        public long DataSize { get; set; }
        public float AverageSpeed { get; set; }
        public float MaxSpeed { get; set; }
        public float TravelDistance { get; set; }
    }

    public class QualityMetrics;
    {
        public float OverallScore { get; set; }
        public float Smoothness { get; set; }
        public float Consistency { get; set; }
        public float Completeness { get; set; }
        public float NoiseLevel { get; set; }
        public float FootSliding { get; set; }
        public float PopDetection { get; set; }
    }

    public class MotionCharacteristics;
    {
        public MotionType Type { get; set; }
        public float EnergyLevel { get; set; }
        public float Symmetry { get; set; }
        public float Rhythm { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, float> FeatureVector { get; set; } = new Dictionary<string, float>();
    }

    public class MotionIssue;
    {
        public string IssueId { get; set; }
        public IssueType Type { get; set; }
        public Severity Severity { get; set; }
        public string Description { get; set; }
        public float Time { get; set; }
        public List<string> AffectedBones { get; set; } = new List<string>();
        public string Recommendation { get; set; }
    }

    public class Recommendation;
    {
        public string RecommendationId { get; set; }
        public RecommendationType Type { get; set; }
        public string Description { get; set; }
        public Priority Priority { get; set; }
        public List<string> Steps { get; set; } = new List<string>();
    }

    public class MotionStatistics;
    {
        public Dictionary<string, float> BoneMovements { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, float> RotationRanges { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, float> VelocityStats { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, object> AdditionalStats { get; set; } = new Dictionary<string, object>();
    }

    // Supporting Classes;

    public class BoneMapping;
    {
        public string SourceBoneId { get; set; }
        public string TargetBoneId { get; set; }
        public float Weight { get; set; } = 1.0f;
        public Vector3 Offset { get; set; }
        public Quaternion RotationOffset { get; set; }
    }

    public class LoopAnalysis;
    {
        public List<float> LoopPoints { get; set; } = new List<float>();
        public float QualityScore { get; set; }
        public bool IsLoopable { get; set; }
        public Dictionary<string, object> AnalysisData { get; set; } = new Dictionary<string, object>();
    }

    public class AnimationClip;
    {
        public string ClipId { get; set; }
        public string ClipName { get; set; }
        public float Duration { get; set; }
        public float FrameRate { get; set; }
        public bool IsLooping { get; set; }
        public SkeletonDefinition Skeleton { get; set; }
        public Dictionary<string, AnimationCurve> Curves { get; set; } = new Dictionary<string, AnimationCurve>();
        public List<AnimationEvent> Events { get; set; } = new List<AnimationEvent>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class AnimationEvent;
    {
        public string EventId { get; set; }
        public string EventName { get; set; }
        public float Time { get; set; }
        public EventType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class MotionState;
    {
        public string StateId { get; set; }
        public string StateName { get; set; }
        public StateType StateType { get; set; }
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class MotionTransition;
    {
        public string TransitionId { get; set; }
        public string TransitionName { get; set; }
        public string SourceStateId { get; set; }
        public string TargetStateId { get; set; }
        public TransitionType TransitionType { get; set; }
        public float StartTime { get; set; }
        public float Duration { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class MotionImporterMetrics;
    {
        public float TotalRuntime { get; set; }
        public Dictionary<string, OperationMetrics> OperationMetrics { get; set; } = new Dictionary<string, OperationMetrics>();
    }

    public class OperationMetrics;
    {
        public int CallCount { get; set; }
        public float AverageTime { get; set; }
        public float MinTime { get; set; }
        public float MaxTime { get; set; }
        public float TotalTime { get; set; }
    }

    // Enums;

    public enum CoordinateSystem;
    {
        RightHanded,
        LeftHanded,
        Unreal,
        Unity;
    }

    public enum BoneType;
    {
        Root,
        Hips,
        Spine,
        Chest,
        Neck,
        Head,
        Shoulder,
        Arm,
        Forearm,
        Hand,
        Finger,
        Thigh,
        Leg,
        Foot,
        Toe,
        Prop,
        Custom;
    }

    public enum CurveType;
    {
        PositionX,
        PositionY,
        PositionZ,
        RotationX,
        RotationY,
        RotationZ,
        RotationW,
        ScaleX,
        ScaleY,
        ScaleZ,
        Custom;
    }

    public enum WrapMode;
    {
        Clamp,
        Loop,
        PingPong,
        ClampForever;
    }

    public enum TangentMode;
    {
        Auto,
        Linear,
        Constant,
        Custom;
    }

    public enum CompressionLevel;
    {
        None,
        Low,
        Medium,
        High,
        Maximum;
    }

    public enum RetargetMethod;
    {
        Auto,
        Position,
        Rotation,
        Full,
        Simple,
        Advanced;
    }

    public enum BlendMethod;
    {
        Linear,
        Additive,
        Override,
        Crossfade,
        Layered;
    }

    public enum CurveGenerationMethod;
    {
        Auto,
        Manual,
        Optimized,
        Preset;
    }

    public enum MotionType;
    {
        Idle,
        Walk,
        Run,
        Jump,
        Attack,
        Reaction,
        Emotional,
        Cinematic,
        Custom;
    }

    public enum IssueType;
    {
        Noise,
        Pop,
        FootSliding,
        BrokenCurve,
        MissingData,
        ScaleIssue,
        RotationIssue,
        TimingIssue,
        QualityIssue;
    }

    public enum Severity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    public enum RecommendationType;
    {
        Optimization,
        Cleanup,
        Retargeting,
        Blending,
        Looping,
        QualityImprovement;
    }

    public enum Priority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum EventType;
    {
        Footstep,
        Impact,
        Sound,
        Effect,
        Custom;
    }

    // Event Class;

    public class MotionImporterEvent : IEvent;
    {
        public string EventType { get; set; }
        public Guid ImporterId { get; set; }
        public DateTime Timestamp { get; set; }
        public object Data { get; set; }
    }

    // Exception Class;

    public class MotionImporterException : Exception
    {
        public string ErrorCode { get; }

        public MotionImporterException(string message) : base(message)
        {
            ErrorCode = ErrorCodes.MotionImporterGenericError;
        }

        public MotionImporterException(string message, string errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public MotionImporterException(string message, Exception innerException, string errorCode)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Error Codes;

    public static class ErrorCodes;
    {
        public const string MotionImporterGenericError = "MOTIONIMPORTER_001";
        public const string MotionImporterInitializationFailed = "MOTIONIMPORTER_002";
        public const string UnsupportedFileFormat = "MOTIONIMPORTER_003";
        public const string UnsupportedFileExtension = "MOTIONIMPORTER_004";
        public const string MotionImportFailed = "MOTIONIMPORTER_005";
        public const string BatchImportFailed = "MOTIONIMPORTER_006";
        public const string UnsupportedExportFormat = "MOTIONIMPORTER_007";
        public const string MotionExportFailed = "MOTIONIMPORTER_008";
        public const string MotionOptimizationFailed = "MOTIONIMPORTER_009";
        public const string MotionRetargetingFailed = "MOTIONIMPORTER_010";
        public const string IncompatibleMotionsForBlending = "MOTIONIMPORTER_011";
        public const string MotionBlendingFailed = "MOTIONIMPORTER_012";
        public const string LoopCreationFailed = "MOTIONIMPORTER_013";
        public const string AnimationClipCreationFailed = "MOTIONIMPORTER_014";
        public const string StateMachineCreationFailed = "MOTIONIMPORTER_015";
        public const string MotionSaveFailed = "MOTIONIMPORTER_016";
        public const string MotionLoadFailed = "MOTIONIMPORTER_017";
        public const string MotionAnalysisFailed = "MOTIONIMPORTER_018";
        public const string MotionCleanupFailed = "MOTIONIMPORTER_019";
        public const string PreviewGenerationFailed = "MOTIONIMPORTER_020";
    }
}
