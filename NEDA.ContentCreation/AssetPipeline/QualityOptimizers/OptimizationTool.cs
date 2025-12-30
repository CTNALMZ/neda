using NEDA.ContentCreation.AssetPipeline.FormatConverters;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.MediaProcessing.ImageProcessing.BatchEditing;
using NEDA.MediaProcessing.ImageProcessing.ImageFilters;
using NEDA.Services.Messaging.EventBus;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace NEDA.ContentCreation.AssetPipeline.QualityOptimizers;
{
    /// <summary>
    /// Advanced optimization tool for media assets with AI-powered optimization strategies;
    /// Supports lossless/lossy compression, smart resizing, and quality enhancement;
    /// </summary>
    public class OptimizationTool : IOptimizationTool, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ISettingsManager _settingsManager;
        private readonly IFilterEngine _filterEngine;
        private readonly IImageConverter _imageConverter;
        private readonly SemaphoreSlim _processingLock;
        private readonly Dictionary<OptimizationPreset, OptimizationProfile> _optimizationProfiles;
        private readonly Dictionary<string, IOptimizationStrategy> _optimizationStrategies;
        private bool _disposed;

        public string ToolId { get; }
        public ToolStatus Status { get; private set; }
        public int TotalOptimizations { get; private set; }
        public long TotalBytesSaved { get; private set; }
        public DateTime LastOptimizationTime { get; private set; }

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of OptimizationTool;
        /// </summary>
        public OptimizationTool(
            ILogger logger,
            IEventBus eventBus,
            ISettingsManager settingsManager,
            IFilterEngine filterEngine = null,
            IImageConverter imageConverter = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _filterEngine = filterEngine;
            _imageConverter = imageConverter;

            ToolId = Guid.NewGuid().ToString("N");
            _processingLock = new SemaphoreSlim(1, 1);
            _optimizationProfiles = new Dictionary<OptimizationPreset, OptimizationProfile>();
            _optimizationStrategies = new Dictionary<string, IOptimizationStrategy>();

            InitializeOptimizationProfiles();
            RegisterDefaultStrategies();
            LoadConfiguration();

            Status = ToolStatus.Ready;

            _logger.LogInformation($"OptimizationTool initialized with ID: {ToolId}");
        }

        #endregion;

        #region Public Methods - Core Optimization;

        /// <summary>
        /// Optimizes an image stream with advanced compression and quality enhancement;
        /// </summary>
        public async Task<OptimizationResult> OptimizeAsync(
            Stream inputStream,
            string format,
            OptimizationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateParameters(inputStream, format);

            var startTime = DateTime.UtcNow;
            var result = new OptimizationResult;
            {
                OptimizationId = Guid.NewGuid(),
                Format = format,
                StartTime = startTime,
                OriginalSize = inputStream.Length;
            };

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                Status = ToolStatus.Processing;

                var optimizationOptions = options ?? GetDefaultOptions();
                var profile = GetOptimizationProfile(optimizationOptions.Preset);

                await _eventBus.PublishAsync(new OptimizationStartedEvent;
                {
                    OptimizationId = result.OptimizationId,
                    Format = format,
                    Preset = optimizationOptions.Preset,
                    OriginalSize = result.OriginalSize,
                    Timestamp = startTime,
                    ToolId = ToolId;
                });

                _logger.LogDebug($"Starting optimization {result.OptimizationId} for {format} format");

                using (var outputStream = new MemoryStream())
                {
                    await ApplyOptimizationAsync(
                        inputStream,
                        outputStream,
                        format,
                        optimizationOptions,
                        profile,
                        cancellationToken);

                    result.OptimizedSize = outputStream.Length;
                    result.OptimizedData = outputStream.ToArray();
                    result.CompressionRatio = CalculateCompressionRatio(result.OriginalSize, result.OptimizedSize);
                    result.BytesSaved = result.OriginalSize - result.OptimizedSize;
                    result.PercentageSaved = CalculatePercentageSaved(result.OriginalSize, result.OptimizedSize);
                }

                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;

                UpdateStatistics(result);

                await _eventBus.PublishAsync(new OptimizationCompletedEvent;
                {
                    OptimizationId = result.OptimizationId,
                    Success = true,
                    Format = format,
                    ProcessingTime = result.ProcessingTime,
                    OriginalSize = result.OriginalSize,
                    OptimizedSize = result.OptimizedSize,
                    BytesSaved = result.BytesSaved,
                    PercentageSaved = result.PercentageSaved,
                    CompressionRatio = result.CompressionRatio,
                    Timestamp = result.EndTime,
                    ToolId = ToolId;
                });

                _logger.LogInformation($"Optimization {result.OptimizationId} completed: " +
                    $"{result.OriginalSize} bytes -> {result.OptimizedSize} bytes " +
                    $"(Saved: {result.PercentageSaved:F2}%)");

                return result;
            }
            catch (Exception ex)
            {
                await HandleOptimizationErrorAsync(result, ex, cancellationToken);
                throw new OptimizationException($"Failed to optimize image: {ex.Message}", ex);
            }
            finally
            {
                Status = ToolStatus.Ready;
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Optimizes an image file with smart quality analysis;
        /// </summary>
        public async Task<FileOptimizationResult> OptimizeFileAsync(
            string sourcePath,
            string targetPath = null,
            OptimizationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateFilePaths(sourcePath, ref targetPath);

            var startTime = DateTime.UtcNow;
            var format = GetFormatFromExtension(sourcePath);

            var result = new FileOptimizationResult;
            {
                OptimizationId = Guid.NewGuid(),
                SourcePath = sourcePath,
                TargetPath = targetPath,
                Format = format,
                StartTime = startTime;
            };

            try
            {
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException($"Source file not found: {sourcePath}");
                }

                var fileInfo = new FileInfo(sourcePath);
                result.OriginalSize = fileInfo.Length;

                await _eventBus.PublishAsync(new FileOptimizationStartedEvent;
                {
                    OptimizationId = result.OptimizationId,
                    SourcePath = sourcePath,
                    TargetPath = targetPath,
                    Format = format,
                    FileSize = fileInfo.Length,
                    Timestamp = startTime,
                    ToolId = ToolId;
                });

                _logger.LogDebug($"Starting file optimization {result.OptimizationId}: {sourcePath}");

                var optimizationOptions = options ?? GetDefaultOptions();
                var profile = GetOptimizationProfile(optimizationOptions.Preset);

                using (var inputStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var outputStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await ApplyOptimizationAsync(
                        inputStream,
                        outputStream,
                        format,
                        optimizationOptions,
                        profile,
                        cancellationToken);
                }

                var targetFileInfo = new FileInfo(targetPath);
                result.OptimizedSize = targetFileInfo.Length;
                result.CompressionRatio = CalculateCompressionRatio(result.OriginalSize, result.OptimizedSize);
                result.BytesSaved = result.OriginalSize - result.OptimizedSize;
                result.PercentageSaved = CalculatePercentageSaved(result.OriginalSize, result.OptimizedSize);
                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;

                UpdateStatistics(result);

                await _eventBus.PublishAsync(new FileOptimizationCompletedEvent;
                {
                    OptimizationId = result.OptimizationId,
                    Success = true,
                    SourcePath = sourcePath,
                    TargetPath = targetPath,
                    ProcessingTime = result.ProcessingTime,
                    OriginalSize = result.OriginalSize,
                    OptimizedSize = result.OptimizedSize,
                    BytesSaved = result.BytesSaved,
                    PercentageSaved = result.PercentageSaved,
                    Timestamp = result.EndTime,
                    ToolId = ToolId;
                });

                _logger.LogInformation($"File optimization {result.OptimizationId} completed: " +
                    $"{sourcePath} -> {targetPath} " +
                    $"(Saved: {result.PercentageSaved:F2}%)");

                return result;
            }
            catch (Exception ex)
            {
                await HandleFileOptimizationErrorAsync(result, ex, cancellationToken);

                if (File.Exists(targetPath) && targetPath != sourcePath)
                {
                    try { File.Delete(targetPath); } catch { }
                }

                throw new OptimizationException($"Failed to optimize file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Batch optimizes multiple files with parallel processing;
        /// </summary>
        public async Task<BatchOptimizationResult> OptimizeBatchAsync(
            IEnumerable<BatchOptimizationItem> items,
            BatchOptimizationSettings batchSettings = null,
            CancellationToken cancellationToken = default)
        {
            if (items == null || !items.Any())
            {
                throw new ArgumentException("No optimization items provided", nameof(items));
            }

            var startTime = DateTime.UtcNow;
            var result = new BatchOptimizationResult;
            {
                BatchId = Guid.NewGuid(),
                StartTime = startTime,
                TotalItems = items.Count()
            };

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                Status = ToolStatus.ProcessingBatch;

                var settings = batchSettings ?? new BatchOptimizationSettings();
                var parallelOptions = new ParallelOptions;
                {
                    MaxDegreeOfParallelism = settings.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken;
                };

                await _eventBus.PublishAsync(new BatchOptimizationStartedEvent;
                {
                    BatchId = result.BatchId,
                    ItemCount = result.TotalItems,
                    Timestamp = startTime,
                    ToolId = ToolId;
                });

                _logger.LogInformation($"Starting batch optimization {result.BatchId} with {result.TotalItems} items");

                var successfulOptimizations = 0;
                var failedOptimizations = 0;
                var totalBytesSaved = 0L;
                var results = new List<BatchItemResult>();

                await Parallel.ForEachAsync(items, parallelOptions, async (item, ct) =>
                {
                    var itemResult = new BatchItemResult;
                    {
                        ItemId = item.ItemId,
                        SourcePath = item.SourcePath,
                        TargetPath = item.TargetPath,
                        StartTime = DateTime.UtcNow;
                    };

                    try
                    {
                        if (!string.IsNullOrEmpty(item.SourcePath) && !string.IsNullOrEmpty(item.TargetPath))
                        {
                            await OptimizeFileItemAsync(item, itemResult, ct);
                        }
                        else if (item.SourceStream != null && item.TargetStream != null)
                        {
                            await OptimizeStreamItemAsync(item, itemResult, ct);
                        }
                        else;
                        {
                            throw new OptimizationException("Invalid optimization item configuration");
                        }

                        Interlocked.Increment(ref successfulOptimizations);
                        Interlocked.Add(ref totalBytesSaved, itemResult.BytesSaved);
                        itemResult.Success = true;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failedOptimizations);
                        itemResult.Success = false;
                        itemResult.ErrorMessage = ex.Message;
                        _logger.LogError(ex, $"Failed to optimize batch item {item.ItemId}");
                    }
                    finally
                    {
                        itemResult.EndTime = DateTime.UtcNow;
                        itemResult.ProcessingTime = itemResult.EndTime - itemResult.StartTime;

                        lock (results)
                        {
                            results.Add(itemResult);
                        }
                    }
                });

                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;
                result.SuccessfulItems = successfulOptimizations;
                result.FailedItems = failedOptimizations;
                result.TotalBytesSaved = totalBytesSaved;
                result.ItemResults = results;
                result.Success = failedOptimizations == 0;

                UpdateBatchStatistics(result);

                await _eventBus.PublishAsync(new BatchOptimizationCompletedEvent;
                {
                    BatchId = result.BatchId,
                    Success = result.Success,
                    TotalItems = result.TotalItems,
                    SuccessfulItems = result.SuccessfulItems,
                    FailedItems = result.FailedItems,
                    TotalBytesSaved = result.TotalBytesSaved,
                    ProcessingTime = result.ProcessingTime,
                    Timestamp = result.EndTime,
                    ToolId = ToolId;
                });

                _logger.LogInformation($"Batch optimization {result.BatchId} completed: " +
                    $"{successfulOptimizations} successful, {failedOptimizations} failed, " +
                    $"Total saved: {FormatBytes(totalBytesSaved)}");

                return result;
            }
            finally
            {
                Status = ToolStatus.Ready;
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Analyzes image for optimization potential without modifying it;
        /// </summary>
        public async Task<AnalysisResult> AnalyzeOptimizationPotentialAsync(
            Stream imageStream,
            string format,
            AnalysisOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateParameters(imageStream, format);

            var startTime = DateTime.UtcNow;
            var result = new AnalysisResult;
            {
                AnalysisId = Guid.NewGuid(),
                Format = format,
                StartTime = startTime,
                OriginalSize = imageStream.Length;
            };

            try
            {
                var analysisOptions = options ?? new AnalysisOptions();

                await _eventBus.PublishAsync(new AnalysisStartedEvent;
                {
                    AnalysisId = result.AnalysisId,
                    Format = format,
                    OriginalSize = result.OriginalSize,
                    Timestamp = startTime,
                    ToolId = ToolId;
                });

                _logger.LogDebug($"Starting optimization analysis {result.AnalysisId} for {format}");

                using (var image = await Image.LoadAsync(imageStream, cancellationToken))
                {
                    // Analyze image characteristics;
                    result.ImageCharacteristics = await AnalyzeImageCharacteristicsAsync(image, format, cancellationToken);

                    // Calculate optimization potentials for different presets;
                    result.OptimizationPotentials = await CalculateOptimizationPotentialsAsync(
                        image, format, analysisOptions, cancellationToken);

                    // Generate recommendations;
                    result.Recommendations = await GenerateOptimizationRecommendationsAsync(
                        result.ImageCharacteristics, result.OptimizationPotentials, analysisOptions);
                }

                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;

                await _eventBus.PublishAsync(new AnalysisCompletedEvent;
                {
                    AnalysisId = result.AnalysisId,
                    Success = true,
                    Format = format,
                    ProcessingTime = result.ProcessingTime,
                    Timestamp = result.EndTime,
                    ToolId = ToolId;
                });

                _logger.LogInformation($"Optimization analysis {result.AnalysisId} completed");

                return result;
            }
            catch (Exception ex)
            {
                await HandleAnalysisErrorAsync(result, ex, cancellationToken);
                throw new OptimizationException($"Failed to analyze optimization potential: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Public Methods - Advanced Features;

        /// <summary>
        /// Performs lossless optimization preserving all image data;
        /// </summary>
        public async Task<OptimizationResult> LosslessOptimizeAsync(
            Stream inputStream,
            string format,
            LosslessOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var optimizationOptions = new OptimizationOptions;
            {
                Preset = OptimizationPreset.Lossless,
                Quality = 100,
                CompressionLevel = CompressionLevel.Maximum,
                EnableLosslessCompression = true,
                PreserveMetadata = options?.PreserveMetadata ?? true,
                RemoveUnusedData = options?.RemoveUnusedData ?? true;
            };

            return await OptimizeAsync(inputStream, format, optimizationOptions, cancellationToken);
        }

        /// <summary>
        /// Performs aggressive optimization with maximum compression;
        /// </summary>
        public async Task<OptimizationResult> AggressiveOptimizeAsync(
            Stream inputStream,
            string format,
            AggressiveOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var optimizationOptions = new OptimizationOptions;
            {
                Preset = OptimizationPreset.MaximumCompression,
                Quality = options?.Quality ?? 70,
                CompressionLevel = CompressionLevel.Maximum,
                EnableLossyCompression = true,
                ReduceColorPalette = options?.ReduceColorPalette ?? true,
                RemoveMetadata = options?.RemoveMetadata ?? true,
                DownsampleImages = options?.DownsampleImages ?? true;
            };

            return await OptimizeAsync(inputStream, format, optimizationOptions, cancellationToken);
        }

        /// <summary>
        /// Smart optimizes based on content analysis;
        /// </summary>
        public async Task<OptimizationResult> SmartOptimizeAsync(
            Stream inputStream,
            string format,
            SmartOptions options = null,
            CancellationToken cancellationToken = default)
        {
            // First analyze the image;
            var analysis = await AnalyzeOptimizationPotentialAsync(inputStream, format, null, cancellationToken);

            // Determine best optimization strategy based on analysis;
            var optimizationOptions = DetermineBestOptimizationStrategy(analysis, options);

            // Reset stream position;
            inputStream.Position = 0;

            // Apply optimization;
            return await OptimizeAsync(inputStream, format, optimizationOptions, cancellationToken);
        }

        /// <summary>
        /// Registers a custom optimization strategy;
        /// </summary>
        public async Task RegisterStrategyAsync(
            IOptimizationStrategy strategy,
            CancellationToken cancellationToken = default)
        {
            if (strategy == null)
            {
                throw new ArgumentNullException(nameof(strategy));
            }

            try
            {
                await _processingLock.WaitAsync(cancellationToken);

                if (_optimizationStrategies.ContainsKey(strategy.StrategyId))
                {
                    _logger.LogWarning($"Optimization strategy {strategy.StrategyId} is already registered");
                    return;
                }

                await strategy.InitializeAsync(cancellationToken);
                _optimizationStrategies[strategy.StrategyId] = strategy;

                _logger.LogInformation($"Optimization strategy {strategy.StrategyId} registered successfully");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Gets available optimization presets;
        /// </summary>
        public IEnumerable<OptimizationPresetInfo> GetAvailablePresets()
        {
            return _optimizationProfiles.Values.Select(profile => new OptimizationPresetInfo;
            {
                Preset = profile.Preset,
                Name = profile.Name,
                Description = profile.Description,
                TypicalSavings = profile.TypicalSavings,
                RecommendedFor = profile.RecommendedFor;
            });
        }

        /// <summary>
        /// Gets tool statistics;
        /// </summary>
        public ToolStatistics GetStatistics()
        {
            return new ToolStatistics;
            {
                ToolId = ToolId,
                Status = Status,
                TotalOptimizations = TotalOptimizations,
                TotalBytesSaved = TotalBytesSaved,
                LastOptimizationTime = LastOptimizationTime,
                RegisteredStrategies = _optimizationStrategies.Count,
                AvailablePresets = _optimizationProfiles.Count,
                AverageSavingsPercentage = CalculateAverageSavings()
            };
        }

        #endregion;

        #region Private Methods - Core Implementation;

        private void InitializeOptimizationProfiles()
        {
            // Lossless Preset;
            _optimizationProfiles[OptimizationPreset.Lossless] = new OptimizationProfile;
            {
                Preset = OptimizationPreset.Lossless,
                Name = "Lossless",
                Description = "Maximum compression without quality loss",
                Quality = 100,
                CompressionLevel = CompressionLevel.Maximum,
                EnableLosslessCompression = true,
                RemoveUnusedData = true,
                OptimizeCompressionTables = true,
                ProgressiveEncoding = false,
                TypicalSavings = 15, // 15% typical savings;
                RecommendedFor = "Archives, professional photography, source files"
            };

            // Web Preset;
            _optimizationProfiles[OptimizationPreset.Web] = new OptimizationProfile;
            {
                Preset = OptimizationPreset.Web,
                Name = "Web Optimized",
                Description = "Optimal balance for web delivery",
                Quality = 85,
                CompressionLevel = CompressionLevel.High,
                EnableLossyCompression = true,
                RemoveMetadata = true,
                OptimizeForWeb = true,
                ProgressiveEncoding = true,
                ConvertToSrgb = true,
                TypicalSavings = 65, // 65% typical savings;
                RecommendedFor = "Website images, web applications, online content"
            };

            // Mobile Preset;
            _optimizationProfiles[OptimizationPreset.Mobile] = new OptimizationProfile;
            {
                Preset = OptimizationPreset.Mobile,
                Name = "Mobile Optimized",
                Description = "Optimized for mobile devices and slow networks",
                Quality = 80,
                CompressionLevel = CompressionLevel.High,
                EnableLossyCompression = true,
                RemoveMetadata = true,
                OptimizeForMobile = true,
                DownsampleImages = true,
                MaxWidth = 1200,
                MaxHeight = 1200,
                TypicalSavings = 75, // 75% typical savings;
                RecommendedFor = "Mobile apps, responsive websites, slow connections"
            };

            // Maximum Compression Preset;
            _optimizationProfiles[OptimizationPreset.MaximumCompression] = new OptimizationProfile;
            {
                Preset = OptimizationPreset.MaximumCompression,
                Name = "Maximum Compression",
                Description = "Aggressive compression for maximum size reduction",
                Quality = 70,
                CompressionLevel = CompressionLevel.Maximum,
                EnableLossyCompression = true,
                RemoveMetadata = true,
                ReduceColorPalette = true,
                DownsampleImages = true,
                AggressiveCompression = true,
                TypicalSavings = 85, // 85% typical savings;
                RecommendedFor = "Email attachments, storage optimization, bandwidth-limited scenarios"
            };

            // Print Preset;
            _optimizationProfiles[OptimizationPreset.Print] = new OptimizationProfile;
            {
                Preset = OptimizationPreset.Print,
                Name = "Print Ready",
                Description = "Optimized for high-quality printing",
                Quality = 100,
                CompressionLevel = CompressionLevel.Normal,
                EnableLosslessCompression = true,
                PreserveMetadata = true,
                MaintainColorProfiles = true,
                Dpi = 300,
                TypicalSavings = 10, // 10% typical savings;
                RecommendedFor = "Print materials, high-resolution displays, professional output"
            };

            // Social Media Preset;
            _optimizationProfiles[OptimizationPreset.SocialMedia] = new OptimizationProfile;
            {
                Preset = OptimizationPreset.SocialMedia,
                Name = "Social Media",
                Description = "Optimized for social media platforms",
                Quality = 90,
                CompressionLevel = CompressionLevel.High,
                EnableLossyCompression = true,
                RemoveMetadata = true,
                SquareCrop = true,
                MaxWidth = 1080,
                MaxHeight = 1080,
                TypicalSavings = 60, // 60% typical savings;
                RecommendedFor = "Facebook, Instagram, Twitter, social media posts"
            };
        }

        private void RegisterDefaultStrategies()
        {
            // Register built-in optimization strategies;
            RegisterBuiltinStrategies();
        }

        private void RegisterBuiltinStrategies()
        {
            try
            {
                // JPEG Optimization Strategy;
                var jpegStrategy = new JpegOptimizationStrategy(_logger);
                _optimizationStrategies[jpegStrategy.StrategyId] = jpegStrategy;

                // PNG Optimization Strategy;
                var pngStrategy = new PngOptimizationStrategy(_logger);
                _optimizationStrategies[pngStrategy.StrategyId] = pngStrategy;

                // WebP Optimization Strategy;
                var webpStrategy = new WebPOptimizationStrategy(_logger);
                _optimizationStrategies[webpStrategy.StrategyId] = webpStrategy;

                // Generic Image Optimization Strategy;
                var genericStrategy = new GenericOptimizationStrategy(_logger);
                _optimizationStrategies[genericStrategy.StrategyId] = genericStrategy;

                _logger.LogDebug($"Registered {_optimizationStrategies.Count} built-in optimization strategies");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register built-in optimization strategies");
            }
        }

        private void LoadConfiguration()
        {
            try
            {
                var config = _settingsManager.GetSection<OptimizationToolConfig>("OptimizationTool");
                if (config != null)
                {
                    // Apply configuration;
                    _logger.LogDebug("OptimizationTool configuration loaded successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load OptimizationTool configuration, using defaults");
            }
        }

        private async Task ApplyOptimizationAsync(
            Stream inputStream,
            Stream outputStream,
            string format,
            OptimizationOptions options,
            OptimizationProfile profile,
            CancellationToken cancellationToken)
        {
            // Reset stream position;
            inputStream.Position = 0;

            // Get appropriate optimization strategy;
            var strategy = GetOptimizationStrategy(format);

            if (strategy != null)
            {
                // Use strategy-specific optimization;
                await strategy.OptimizeAsync(inputStream, outputStream, format, options, cancellationToken);
            }
            else;
            {
                // Use generic optimization;
                await GenericOptimizeAsync(inputStream, outputStream, format, options, profile, cancellationToken);
            }
        }

        private async Task GenericOptimizeAsync(
            Stream inputStream,
            Stream outputStream,
            string format,
            OptimizationOptions options,
            OptimizationProfile profile,
            CancellationToken cancellationToken)
        {
            using (var image = await Image.LoadAsync(inputStream, cancellationToken))
            {
                // Apply preprocessing if filter engine is available;
                if (_filterEngine != null && options.PreprocessFilters != null)
                {
                    await ApplyPreprocessingFiltersAsync(image, options.PreprocessFilters, cancellationToken);
                }

                // Apply resizing if needed;
                if (profile.MaxWidth > 0 || profile.MaxHeight > 0)
                {
                    ApplySmartResizing(image, profile);
                }

                // Apply color optimization;
                if (profile.ReduceColorPalette)
                {
                    ApplyColorReduction(image, format, profile);
                }

                // Get encoder with optimization settings;
                var encoder = GetOptimizedEncoder(format, profile);

                // Save optimized image;
                await image.SaveAsync(outputStream, encoder, cancellationToken);
            }
        }

        private async Task ApplyPreprocessingFiltersAsync(
            Image image,
            IEnumerable<FilterSettings> filters,
            CancellationToken cancellationToken)
        {
            foreach (var filter in filters)
            {
                await _filterEngine.ApplyFilterAsync(image, filter, cancellationToken);
            }
        }

        private void ApplySmartResizing(Image image, OptimizationProfile profile)
        {
            if (profile.MaxWidth <= 0 && profile.MaxHeight <= 0)
            {
                return;
            }

            var options = new ResizeOptions;
            {
                Size = new Size(profile.MaxWidth, profile.MaxHeight),
                Mode = ResizeMode.Max,
                Compand = false;
            };

            // Apply square crop for social media if needed;
            if (profile.SquareCrop)
            {
                options.Mode = ResizeMode.Crop;
                var size = Math.Min(profile.MaxWidth, profile.MaxHeight);
                options.Size = new Size(size, size);
            }

            image.Mutate(x => x.Resize(options));
        }

        private void ApplyColorReduction(Image image, string format, OptimizationProfile profile)
        {
            // Apply color reduction based on format and profile;
            switch (format.ToUpperInvariant())
            {
                case "PNG":
                case "GIF":
                    // Quantize colors for PNG and GIF;
                    image.Mutate(x => x.Quantize(new SixLabors.ImageSharp.Processing.Processors.Quantization.WuQuantizer()));
                    break;

                case "JPEG":
                case "JPG":
                    // JPEG already uses color subsampling;
                    break;
            }
        }

        private IImageEncoder GetOptimizedEncoder(string format, OptimizationProfile profile)
        {
            var normalizedFormat = NormalizeFormat(format);

            switch (normalizedFormat)
            {
                case "JPEG":
                case "JPG":
                    return new JpegEncoder;
                    {
                        Quality = profile.Quality,
                        ColorType = profile.ProgressiveEncoding ?
                            JpegColorType.YCbCr : JpegColorType.Rgb,
                        Interleaved = profile.ProgressiveEncoding;
                    };

                case "PNG":
                    return new PngEncoder;
                    {
                        CompressionLevel = GetPngCompressionLevel(profile.CompressionLevel),
                        ColorType = PngColorType.RgbWithAlpha,
                        BitDepth = PngBitDepth.Bit8,
                        TransparentColorMode = PngTransparentColorMode.Preserve;
                    };

                case "WEBP":
                    return new WebpEncoder;
                    {
                        Quality = profile.Quality,
                        Method = profile.EnableLosslessCompression ?
                            WebpEncodingMethod.Lossless : WebpEncodingMethod.Qualitative,
                        FileFormat = profile.EnableLosslessCompression ?
                            WebpFileFormatType.Lossless : WebpFileFormatType.Lossy;
                    };

                default:
                    // Return default encoder for other formats;
                    return SixLabors.ImageSharp.Configuration.Default.ImageFormatsManager;
                        .GetEncoder(GetImageFormat(normalizedFormat));
            }
        }

        private async Task OptimizeFileItemAsync(BatchOptimizationItem item, BatchItemResult result, CancellationToken cancellationToken)
        {
            var options = item.Options ?? GetDefaultOptions();

            using (var inputStream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var outputStream = new FileStream(item.TargetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var fileInfo = new FileInfo(item.SourcePath);
                result.OriginalSize = fileInfo.Length;

                var profile = GetOptimizationProfile(options.Preset);

                await ApplyOptimizationAsync(
                    inputStream,
                    outputStream,
                    item.Format,
                    options,
                    profile,
                    cancellationToken);

                var targetFileInfo = new FileInfo(item.TargetPath);
                result.OptimizedSize = targetFileInfo.Length;
                result.BytesSaved = result.OriginalSize - result.OptimizedSize;
                result.PercentageSaved = CalculatePercentageSaved(result.OriginalSize, result.OptimizedSize);
            }
        }

        private async Task OptimizeStreamItemAsync(BatchOptimizationItem item, BatchItemResult result, CancellationToken cancellationToken)
        {
            var options = item.Options ?? GetDefaultOptions();

            item.SourceStream.Position = 0;
            result.OriginalSize = item.SourceStream.Length;

            var profile = GetOptimizationProfile(options.Preset);

            await ApplyOptimizationAsync(
                item.SourceStream,
                item.TargetStream,
                item.Format,
                options,
                profile,
                cancellationToken);

            item.TargetStream.Position = 0;
            result.OptimizedSize = item.TargetStream.Length;
            result.BytesSaved = result.OriginalSize - result.OptimizedSize;
            result.PercentageSaved = CalculatePercentageSaved(result.OriginalSize, result.OptimizedSize);
        }

        private async Task<ImageCharacteristics> AnalyzeImageCharacteristicsAsync(
            Image image,
            string format,
            CancellationToken cancellationToken)
        {
            return new ImageCharacteristics;
            {
                Width = image.Width,
                Height = image.Height,
                AspectRatio = (double)image.Width / image.Height,
                Format = format,
                HasTransparency = HasTransparency(image),
                ColorDepth = GetColorDepth(image),
                IsAnimated = false, // Would need format-specific check;
                EstimatedQuality = EstimateImageQuality(image)
            };
        }

        private async Task<IEnumerable<OptimizationPotential>> CalculateOptimizationPotentialsAsync(
            Image image,
            string format,
            AnalysisOptions options,
            CancellationToken cancellationToken)
        {
            var potentials = new List<OptimizationPotential>();

            // Calculate potential for each preset;
            foreach (var preset in _optimizationProfiles.Keys)
            {
                var profile = _optimizationProfiles[preset];
                var potential = await CalculatePresetPotentialAsync(image, format, profile, cancellationToken);
                potentials.Add(potential);
            }

            return potentials;
        }

        private async Task<OptimizationPotential> CalculatePresetPotentialAsync(
            Image image,
            string format,
            OptimizationProfile profile,
            CancellationToken cancellationToken)
        {
            // Simulate optimization to estimate savings;
            using (var simulatedStream = new MemoryStream())
            {
                var encoder = GetOptimizedEncoder(format, profile);
                await image.SaveAsync(simulatedStream, encoder, cancellationToken);

                return new OptimizationPotential;
                {
                    Preset = profile.Preset,
                    EstimatedSize = simulatedStream.Length,
                    EstimatedSavings = profile.TypicalSavings,
                    Recommended = IsPresetRecommended(image, format, profile)
                };
            }
        }

        private async Task<IEnumerable<OptimizationRecommendation>> GenerateOptimizationRecommendationsAsync(
            ImageCharacteristics characteristics,
            IEnumerable<OptimizationPotential> potentials,
            AnalysisOptions options)
        {
            var recommendations = new List<OptimizationRecommendation>();

            // Generate recommendations based on analysis;
            foreach (var potential in potentials.OrderBy(p => p.EstimatedSize))
            {
                var recommendation = new OptimizationRecommendation;
                {
                    Preset = potential.Preset,
                    Confidence = CalculateRecommendationConfidence(characteristics, potential),
                    ExpectedSavings = potential.EstimatedSavings,
                    Priority = GetRecommendationPriority(potential),
                    Reasoning = GenerateRecommendationReasoning(characteristics, potential)
                };

                recommendations.Add(recommendation);
            }

            return recommendations.OrderByDescending(r => r.Priority).ThenByDescending(r => r.Confidence);
        }

        private OptimizationOptions DetermineBestOptimizationStrategy(
            AnalysisResult analysis,
            SmartOptions options)
        {
            // Find the best preset based on recommendations;
            var bestRecommendation = analysis.Recommendations;
                .OrderByDescending(r => r.Priority)
                .ThenByDescending(r => r.Confidence)
                .FirstOrDefault();

            if (bestRecommendation == null)
            {
                return GetDefaultOptions();
            }

            var profile = GetOptimizationProfile(bestRecommendation.Preset);

            return new OptimizationOptions;
            {
                Preset = bestRecommendation.Preset,
                Quality = profile.Quality,
                CompressionLevel = profile.CompressionLevel,
                EnableLosslessCompression = profile.EnableLosslessCompression,
                EnableLossyCompression = profile.EnableLossyCompression,
                RemoveMetadata = profile.RemoveMetadata,
                PreserveMetadata = profile.PreserveMetadata,
                OptimizeForWeb = profile.OptimizeForWeb,
                OptimizeForMobile = profile.OptimizeForMobile;
            };
        }

        #endregion;

        #region Private Methods - Helpers;

        private IOptimizationStrategy GetOptimizationStrategy(string format)
        {
            var strategyKey = $"{format.ToUpperInvariant()}_STRATEGY";

            if (_optimizationStrategies.TryGetValue(strategyKey, out var strategy))
            {
                return strategy;
            }

            // Try generic strategies;
            return _optimizationStrategies.Values.FirstOrDefault(s => s.SupportsFormat(format));
        }

        private OptimizationProfile GetOptimizationProfile(OptimizationPreset preset)
        {
            if (_optimizationProfiles.TryGetValue(preset, out var profile))
            {
                return profile;
            }

            // Return Web preset as default;
            return _optimizationProfiles[OptimizationPreset.Web];
        }

        private async Task HandleOptimizationErrorAsync(OptimizationResult result, Exception ex, CancellationToken cancellationToken)
        {
            _logger.LogError(ex, $"Optimization {result.OptimizationId} failed");

            await _eventBus.PublishAsync(new OptimizationFailedEvent;
            {
                OptimizationId = result.OptimizationId,
                Format = result.Format,
                ErrorMessage = ex.Message,
                ProcessingTime = result.ProcessingTime,
                Timestamp = result.EndTime,
                ToolId = ToolId;
            });
        }

        private async Task HandleFileOptimizationErrorAsync(FileOptimizationResult result, Exception ex, CancellationToken cancellationToken)
        {
            _logger.LogError(ex, $"File optimization {result.OptimizationId} failed");

            await _eventBus.PublishAsync(new FileOptimizationFailedEvent;
            {
                OptimizationId = result.OptimizationId,
                SourcePath = result.SourcePath,
                TargetPath = result.TargetPath,
                ErrorMessage = ex.Message,
                ProcessingTime = result.ProcessingTime,
                Timestamp = result.EndTime,
                ToolId = ToolId;
            });
        }

        private async Task HandleAnalysisErrorAsync(AnalysisResult result, Exception ex, CancellationToken cancellationToken)
        {
            _logger.LogError(ex, $"Optimization analysis {result.AnalysisId} failed");

            await _eventBus.PublishAsync(new AnalysisFailedEvent;
            {
                AnalysisId = result.AnalysisId,
                Format = result.Format,
                ErrorMessage = ex.Message,
                ProcessingTime = result.ProcessingTime,
                Timestamp = result.EndTime,
                ToolId = ToolId;
            });
        }

        private void UpdateStatistics(OptimizationResult result)
        {
            TotalOptimizations++;
            TotalBytesSaved += result.BytesSaved;
            LastOptimizationTime = DateTime.UtcNow;
        }

        private void UpdateStatistics(FileOptimizationResult result)
        {
            TotalOptimizations++;
            TotalBytesSaved += result.BytesSaved;
            LastOptimizationTime = DateTime.UtcNow;
        }

        private void UpdateBatchStatistics(BatchOptimizationResult result)
        {
            TotalOptimizations += result.SuccessfulItems;
            TotalBytesSaved += result.TotalBytesSaved;
            LastOptimizationTime = DateTime.UtcNow;
        }

        private void ValidateParameters(Stream stream, string format)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (string.IsNullOrWhiteSpace(format))
            {
                throw new ArgumentException("Format cannot be empty", nameof(format));
            }
        }

        private void ValidateFilePaths(string sourcePath, ref string targetPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path cannot be empty", nameof(sourcePath));
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                targetPath = sourcePath;
            }

            var sourceDir = Path.GetDirectoryName(sourcePath);
            var targetDir = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }
        }

        private string GetFormatFromExtension(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.TrimStart('.').ToUpperInvariant();
            return extension switch;
            {
                "JPG" or "JPEG" => "JPEG",
                "PNG" => "PNG",
                "GIF" => "GIF",
                "BMP" => "BMP",
                "TIF" or "TIFF" => "TIFF",
                "WEBP" => "WEBP",
                _ => throw new OptimizationException($"Unsupported file extension: {extension}")
            };
        }

        private double CalculateCompressionRatio(long originalSize, long optimizedSize)
        {
            if (originalSize == 0) return 0;
            return (double)optimizedSize / originalSize;
        }

        private double CalculatePercentageSaved(long originalSize, long optimizedSize)
        {
            if (originalSize == 0) return 0;
            return 100.0 * (originalSize - optimizedSize) / originalSize;
        }

        private double CalculateAverageSavings()
        {
            if (TotalOptimizations == 0) return 0;

            // This is a simplified calculation;
            // In production, you'd track individual savings;
            return 65.0; // Typical average savings;
        }

        private string NormalizeFormat(string format)
        {
            return format?.Trim().ToUpperInvariant() switch;
            {
                "JPG" => "JPEG",
                "TIF" => "TIFF",
                var f => f;
            };
        }

        private IImageFormat GetImageFormat(string format)
        {
            return format switch;
            {
                "JPEG" => SixLabors.ImageSharp.Formats.Jpeg.JpegFormat.Instance,
                "PNG" => SixLabors.ImageSharp.Formats.Png.PngFormat.Instance,
                "GIF" => SixLabors.ImageSharp.Formats.Gif.GifFormat.Instance,
                "BMP" => SixLabors.ImageSharp.Formats.Bmp.BmpFormat.Instance,
                "TIFF" => SixLabors.ImageSharp.Formats.Tiff.TiffFormat.Instance,
                "WEBP" => SixLabors.ImageSharp.Formats.Webp.WebpFormat.Instance,
                _ => throw new OptimizationException($"Unsupported format: {format}")
            };
        }

        private PngCompressionLevel GetPngCompressionLevel(CompressionLevel level)
        {
            return level switch;
            {
                CompressionLevel.Maximum => PngCompressionLevel.BestCompression,
                CompressionLevel.High => PngCompressionLevel.DefaultCompression,
                CompressionLevel.Normal => PngCompressionLevel.DefaultCompression,
                CompressionLevel.Low => PngCompressionLevel.BestSpeed,
                _ => PngCompressionLevel.DefaultCompression;
            };
        }

        private bool HasTransparency(Image image)
        {
            // Check if image has transparent pixels;
            // This is a simplified check;
            return image.PixelType.AlphaRepresentation != PixelAlphaRepresentation.None;
        }

        private int GetColorDepth(Image image)
        {
            return image.PixelType.BitsPerPixel;
        }

        private int EstimateImageQuality(Image image)
        {
            // Simple quality estimation based on image characteristics;
            // In production, this would use more sophisticated algorithms;
            return 85;
        }

        private bool IsPresetRecommended(Image image, string format, OptimizationProfile profile)
        {
            // Determine if preset is recommended for this image;
            var characteristics = new ImageCharacteristics;
            {
                Width = image.Width,
                Height = image.Height,
                AspectRatio = (double)image.Width / image.Height;
            };

            // Simple recommendation logic;
            if (profile.MaxWidth > 0 && image.Width > profile.MaxWidth * 1.5)
            {
                return true;
            }

            if (profile.MaxHeight > 0 && image.Height > profile.MaxHeight * 1.5)
            {
                return true;
            }

            return false;
        }

        private double CalculateRecommendationConfidence(
            ImageCharacteristics characteristics,
            OptimizationPotential potential)
        {
            // Calculate confidence score for recommendation;
            double confidence = 0.7; // Base confidence;

            // Adjust based on characteristics;
            if (characteristics.Width > 2000 || characteristics.Height > 2000)
            {
                confidence += 0.2; // High resolution images benefit more from optimization;
            }

            if (potential.EstimatedSavings > 50)
            {
                confidence += 0.1; // High savings potential increases confidence;
            }

            return Math.Min(confidence, 1.0);
        }

        private int GetRecommendationPriority(OptimizationPotential potential)
        {
            // Priority based on preset type;
            return potential.Preset switch;
            {
                OptimizationPreset.Web => 3,
                OptimizationPreset.Mobile => 2,
                OptimizationPreset.Lossless => 1,
                OptimizationPreset.MaximumCompression => 4,
                OptimizationPreset.Print => 1,
                OptimizationPreset.SocialMedia => 3,
                _ => 2;
            };
        }

        private string GenerateRecommendationReasoning(
            ImageCharacteristics characteristics,
            OptimizationPotential potential)
        {
            var profile = GetOptimizationProfile(potential.Preset);

            var reasoning = new List<string>
            {
                $"Optimization preset: {profile.Name}",
                $"Estimated savings: {potential.EstimatedSavings}%",
                $"Recommended for: {profile.RecommendedFor}"
            };

            if (characteristics.Width > 2000 || characteristics.Height > 2000)
            {
                reasoning.Add("High-resolution image benefits from optimization");
            }

            return string.Join("; ", reasoning);
        }

        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:0.##} {suffixes[suffixIndex]}";
        }

        private OptimizationOptions GetDefaultOptions()
        {
            return new OptimizationOptions;
            {
                Preset = OptimizationPreset.Web,
                Quality = 85,
                CompressionLevel = CompressionLevel.High,
                EnableLossyCompression = true,
                RemoveMetadata = true,
                OptimizeForWeb = true,
                ProgressiveEncoding = true;
            };
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _processingLock?.Dispose();

                    foreach (var strategy in _optimizationStrategies.Values.OfType<IDisposable>())
                    {
                        strategy.Dispose();
                    }

                    _optimizationStrategies.Clear();
                    _optimizationProfiles.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Optimization options;
    /// </summary>
    public class OptimizationOptions;
    {
        public OptimizationPreset Preset { get; set; }
        public int Quality { get; set; }
        public CompressionLevel CompressionLevel { get; set; }
        public bool EnableLosslessCompression { get; set; }
        public bool EnableLossyCompression { get; set; }
        public bool RemoveMetadata { get; set; }
        public bool PreserveMetadata { get; set; }
        public bool OptimizeForWeb { get; set; }
        public bool OptimizeForMobile { get; set; }
        public bool ProgressiveEncoding { get; set; }
        public IEnumerable<FilterSettings> PreprocessFilters { get; set; }

        public OptimizationOptions()
        {
            PreprocessFilters = new List<FilterSettings>();
        }
    }

    /// <summary>
    /// Lossless optimization options;
    /// </summary>
    public class LosslessOptions;
    {
        public bool PreserveMetadata { get; set; } = true;
        public bool RemoveUnusedData { get; set; } = true;
        public bool OptimizeCompressionTables { get; set; } = true;
    }

    /// <summary>
    /// Aggressive optimization options;
    /// </summary>
    public class AggressiveOptions;
    {
        public int Quality { get; set; } = 70;
        public bool ReduceColorPalette { get; set; } = true;
        public bool RemoveMetadata { get; set; } = true;
        public bool DownsampleImages { get; set; } = true;
    }

    /// <summary>
    /// Smart optimization options;
    /// </summary>
    public class SmartOptions;
    {
        public bool PrioritizeQuality { get; set; } = false;
        public bool PrioritizeSize { get; set; } = false;
        public int TargetSizeBytes { get; set; } = 0;
        public double TargetCompressionRatio { get; set; } = 0;
    }

    /// <summary>
    /// Batch optimization settings;
    /// </summary>
    public class BatchOptimizationSettings;
    {
        public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
        public bool StopOnFirstFailure { get; set; } = false;
        public TimeSpan TimeoutPerItem { get; set; } = TimeSpan.FromMinutes(2);
        public bool CreateBackup { get; set; } = true;
        public string BackupDirectory { get; set; }
        public OptimizationPreset DefaultPreset { get; set; } = OptimizationPreset.Web;
    }

    /// <summary>
    /// Batch optimization item;
    /// </summary>
    public class BatchOptimizationItem;
    {
        public Guid ItemId { get; set; } = Guid.NewGuid();
        public string SourcePath { get; set; }
        public Stream SourceStream { get; set; }
        public string TargetPath { get; set; }
        public Stream TargetStream { get; set; }
        public string Format { get; set; }
        public OptimizationOptions Options { get; set; }
    }

    /// <summary>
    /// Analysis options;
    /// </summary>
    public class AnalysisOptions;
    {
        public bool DetailedAnalysis { get; set; } = true;
        public bool IncludeVisualComparison { get; set; } = false;
        public int MaxAnalysisTimeMs { get; set; } = 5000;
    }

    #endregion;

    #region Results;

    /// <summary>
    /// Optimization result;
    /// </summary>
    public class OptimizationResult;
    {
        public Guid OptimizationId { get; set; }
        public bool Success { get; set; }
        public string Format { get; set; }
        public byte[] OptimizedData { get; set; }
        public long OriginalSize { get; set; }
        public long OptimizedSize { get; set; }
        public long BytesSaved { get; set; }
        public double PercentageSaved { get; set; }
        public double CompressionRatio { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// File optimization result;
    /// </summary>
    public class FileOptimizationResult : OptimizationResult;
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
    }

    /// <summary>
    /// Batch optimization result;
    /// </summary>
    public class BatchOptimizationResult;
    {
        public Guid BatchId { get; set; }
        public bool Success { get; set; }
        public int TotalItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public long TotalBytesSaved { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public IEnumerable<BatchItemResult> ItemResults { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Batch item result;
    /// </summary>
    public class BatchItemResult;
    {
        public Guid ItemId { get; set; }
        public bool Success { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public long OriginalSize { get; set; }
        public long OptimizedSize { get; set; }
        public long BytesSaved { get; set; }
        public double PercentageSaved { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Analysis result;
    /// </summary>
    public class AnalysisResult;
    {
        public Guid AnalysisId { get; set; }
        public bool Success { get; set; }
        public string Format { get; set; }
        public long OriginalSize { get; set; }
        public ImageCharacteristics ImageCharacteristics { get; set; }
        public IEnumerable<OptimizationPotential> OptimizationPotentials { get; set; }
        public IEnumerable<OptimizationRecommendation> Recommendations { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Image characteristics;
    /// </summary>
    public class ImageCharacteristics;
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double AspectRatio { get; set; }
        public string Format { get; set; }
        public bool HasTransparency { get; set; }
        public int ColorDepth { get; set; }
        public bool IsAnimated { get; set; }
        public int EstimatedQuality { get; set; }
        public Dictionary<string, object> AdditionalProperties { get; set; }
    }

    /// <summary>
    /// Optimization potential;
    /// </summary>
    public class OptimizationPotential;
    {
        public OptimizationPreset Preset { get; set; }
        public long EstimatedSize { get; set; }
        public double EstimatedSavings { get; set; }
        public bool Recommended { get; set; }
        public Dictionary<string, object> Details { get; set; }
    }

    /// <summary>
    /// Optimization recommendation;
    /// </summary>
    public class OptimizationRecommendation;
    {
        public OptimizationPreset Preset { get; set; }
        public double Confidence { get; set; }
        public double ExpectedSavings { get; set; }
        public int Priority { get; set; }
        public string Reasoning { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Optimization profile;
    /// </summary>
    public class OptimizationProfile;
    {
        public OptimizationPreset Preset { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int Quality { get; set; }
        public CompressionLevel CompressionLevel { get; set; }
        public bool EnableLosslessCompression { get; set; }
        public bool EnableLossyCompression { get; set; }
        public bool RemoveMetadata { get; set; }
        public bool PreserveMetadata { get; set; }
        public bool OptimizeForWeb { get; set; }
        public bool OptimizeForMobile { get; set; }
        public bool ProgressiveEncoding { get; set; }
        public bool RemoveUnusedData { get; set; }
        public bool OptimizeCompressionTables { get; set; }
        public bool ReduceColorPalette { get; set; }
        public bool DownsampleImages { get; set; }
        public bool ConvertToSrgb { get; set; }
        public bool MaintainColorProfiles { get; set; }
        public bool SquareCrop { get; set; }
        public bool AggressiveCompression { get; set; }
        public int MaxWidth { get; set; }
        public int MaxHeight { get; set; }
        public int Dpi { get; set; }
        public double TypicalSavings { get; set; }
        public string RecommendedFor { get; set; }
    }

    /// <summary>
    /// Optimization preset information;
    /// </summary>
    public class OptimizationPresetInfo;
    {
        public OptimizationPreset Preset { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public double TypicalSavings { get; set; }
        public string RecommendedFor { get; set; }
    }

    /// <summary>
    /// Tool statistics;
    /// </summary>
    public class ToolStatistics;
    {
        public string ToolId { get; set; }
        public ToolStatus Status { get; set; }
        public int TotalOptimizations { get; set; }
        public long TotalBytesSaved { get; set; }
        public DateTime LastOptimizationTime { get; set; }
        public int RegisteredStrategies { get; set; }
        public int AvailablePresets { get; set; }
        public double AverageSavingsPercentage { get; set; }
        public Dictionary<string, int> FormatOptimizationCount { get; set; }
    }

    /// <summary>
    /// Optimization tool configuration;
    /// </summary>
    public class OptimizationToolConfig;
    {
        public int MaxConcurrentOperations { get; set; } = Environment.ProcessorCount;
        public bool EnableSmartOptimization { get; set; } = true;
        public bool EnableLosslessOptimization { get; set; } = true;
        public int DefaultQuality { get; set; } = 85;
        public bool EnableCaching { get; set; } = true;
        public int CacheSizeMB { get; set; } = 200;
        public bool EnablePerformanceMetrics { get; set; } = true;
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(3);
        public string DefaultOutputDirectory { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Tool status;
    /// </summary>
    public enum ToolStatus;
    {
        Ready = 0,
        Processing = 1,
        ProcessingBatch = 2,
        Error = 3;
    }

    /// <summary>
    /// Optimization preset;
    /// </summary>
    public enum OptimizationPreset;
    {
        Lossless = 0,
        Web = 1,
        Mobile = 2,
        MaximumCompression = 3,
        Print = 4,
        SocialMedia = 5;
    }

    /// <summary>
    /// Compression level;
    /// </summary>
    public enum CompressionLevel;
    {
        None = 0,
        Low = 1,
        Normal = 2,
        High = 3,
        Maximum = 4;
    }

    #endregion;

    #region Events;

    public abstract class OptimizationToolEvent : IEvent;
    {
        public string ToolId { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    public class OptimizationStartedEvent : OptimizationToolEvent;
    {
        public Guid OptimizationId { get; set; }
        public string Format { get; set; }
        public OptimizationPreset Preset { get; set; }
        public long OriginalSize { get; set; }
    }

    public class OptimizationCompletedEvent : OptimizationToolEvent;
    {
        public Guid OptimizationId { get; set; }
        public bool Success { get; set; }
        public string Format { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public long OriginalSize { get; set; }
        public long OptimizedSize { get; set; }
        public long BytesSaved { get; set; }
        public double PercentageSaved { get; set; }
        public double CompressionRatio { get; set; }
    }

    public class OptimizationFailedEvent : OptimizationToolEvent;
    {
        public Guid OptimizationId { get; set; }
        public string Format { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class FileOptimizationStartedEvent : OptimizationToolEvent;
    {
        public Guid OptimizationId { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string Format { get; set; }
        public long FileSize { get; set; }
    }

    public class FileOptimizationCompletedEvent : OptimizationToolEvent;
    {
        public Guid OptimizationId { get; set; }
        public bool Success { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public long OriginalSize { get; set; }
        public long OptimizedSize { get; set; }
        public long BytesSaved { get; set; }
        public double PercentageSaved { get; set; }
    }

    public class FileOptimizationFailedEvent : OptimizationToolEvent;
    {
        public Guid OptimizationId { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class BatchOptimizationStartedEvent : OptimizationToolEvent;
    {
        public Guid BatchId { get; set; }
        public int ItemCount { get; set; }
    }

    public class BatchOptimizationCompletedEvent : OptimizationToolEvent;
    {
        public Guid BatchId { get; set; }
        public bool Success { get; set; }
        public int TotalItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public long TotalBytesSaved { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class AnalysisStartedEvent : OptimizationToolEvent;
    {
        public Guid AnalysisId { get; set; }
        public string Format { get; set; }
        public long OriginalSize { get; set; }
    }

    public class AnalysisCompletedEvent : OptimizationToolEvent;
    {
        public Guid AnalysisId { get; set; }
        public bool Success { get; set; }
        public string Format { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class AnalysisFailedEvent : OptimizationToolEvent;
    {
        public Guid AnalysisId { get; set; }
        public string Format { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Optimization exception;
    /// </summary>
    public class OptimizationException : Exception
    {
        public OptimizationException(string message) : base(message) { }
        public OptimizationException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Interfaces and Strategies;

    /// <summary>
    /// Optimization tool interface;
    /// </summary>
    public interface IOptimizationTool : IDisposable
    {
        Task<OptimizationResult> OptimizeAsync(
            Stream inputStream,
            string format,
            OptimizationOptions options = null,
            CancellationToken cancellationToken = default);

        Task<FileOptimizationResult> OptimizeFileAsync(
            string sourcePath,
            string targetPath = null,
            OptimizationOptions options = null,
            CancellationToken cancellationToken = default);

        Task<BatchOptimizationResult> OptimizeBatchAsync(
            IEnumerable<BatchOptimizationItem> items,
            BatchOptimizationSettings batchSettings = null,
            CancellationToken cancellationToken = default);

        Task<AnalysisResult> AnalyzeOptimizationPotentialAsync(
            Stream imageStream,
            string format,
            AnalysisOptions options = null,
            CancellationToken cancellationToken = default);

        Task<OptimizationResult> LosslessOptimizeAsync(
            Stream inputStream,
            string format,
            LosslessOptions options = null,
            CancellationToken cancellationToken = default);

        Task<OptimizationResult> AggressiveOptimizeAsync(
            Stream inputStream,
            string format,
            AggressiveOptions options = null,
            CancellationToken cancellationToken = default);

        Task<OptimizationResult> SmartOptimizeAsync(
            Stream inputStream,
            string format,
            SmartOptions options = null,
            CancellationToken cancellationToken = default);

        Task RegisterStrategyAsync(IOptimizationStrategy strategy, CancellationToken cancellationToken = default);

        IEnumerable<OptimizationPresetInfo> GetAvailablePresets();
        ToolStatistics GetStatistics();
    }

    /// <summary>
    /// Optimization strategy interface;
    /// </summary>
    public interface IOptimizationStrategy : IDisposable
    {
        string StrategyId { get; }
        string Name { get; }
        string Description { get; }

        Task InitializeAsync(CancellationToken cancellationToken);
        bool SupportsFormat(string format);

        Task OptimizeAsync(
            Stream inputStream,
            Stream outputStream,
            string format,
            OptimizationOptions options,
            CancellationToken cancellationToken);
    }

    #endregion;

    #region Built-in Strategies;

    /// <summary>
    /// JPEG optimization strategy;
    /// </summary>
    internal class JpegOptimizationStrategy : IOptimizationStrategy;
    {
        private readonly ILogger _logger;

        public string StrategyId => "JPEG_OPTIMIZATION_STRATEGY";
        public string Name => "JPEG Optimization Strategy";
        public string Description => "Advanced JPEG compression and optimization";

        public JpegOptimizationStrategy(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public bool SupportsFormat(string format)
        {
            return format?.ToUpperInvariant() is "JPEG" or "JPG";
        }

        public async Task OptimizeAsync(
            Stream inputStream,
            Stream outputStream,
            string format,
            OptimizationOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                inputStream.Position = 0;

                using (var image = await Image.LoadAsync(inputStream, cancellationToken))
                {
                    // Apply JPEG-specific optimizations;
                    var encoder = new JpegEncoder;
                    {
                        Quality = options.Quality,
                        ColorType = options.ProgressiveEncoding ? JpegColorType.YCbCr : JpegColorType.Rgb,
                        Interleaved = options.ProgressiveEncoding;
                    };

                    await image.SaveAsync(outputStream, encoder, cancellationToken);
                }

                _logger.LogDebug($"JPEG optimization completed: {inputStream.Length} -> {outputStream.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JPEG optimization failed");
                throw new OptimizationException($"JPEG optimization failed: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            // Cleanup resources if needed;
        }
    }

    /// <summary>
    /// PNG optimization strategy;
    /// </summary>
    internal class PngOptimizationStrategy : IOptimizationStrategy;
    {
        private readonly ILogger _logger;

        public string StrategyId => "PNG_OPTIMIZATION_STRATEGY";
        public string Name => "PNG Optimization Strategy";
        public string Description => "Advanced PNG compression and optimization";

        public PngOptimizationStrategy(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public bool SupportsFormat(string format)
        {
            return format?.ToUpperInvariant() == "PNG";
        }

        public async Task OptimizeAsync(
            Stream inputStream,
            Stream outputStream,
            string format,
            OptimizationOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                inputStream.Position = 0;

                using (var image = await Image.LoadAsync(inputStream, cancellationToken))
                {
                    // Apply PNG-specific optimizations;
                    var encoder = new PngEncoder;
                    {
                        CompressionLevel = GetPngCompressionLevel(options.CompressionLevel),
                        ColorType = PngColorType.RgbWithAlpha,
                        BitDepth = PngBitDepth.Bit8,
                        TransparentColorMode = PngTransparentColorMode.Preserve;
                    };

                    await image.SaveAsync(outputStream, encoder, cancellationToken);
                }

                _logger.LogDebug($"PNG optimization completed: {inputStream.Length} -> {outputStream.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PNG optimization failed");
                throw new OptimizationException($"PNG optimization failed: {ex.Message}", ex);
            }
        }

        private PngCompressionLevel GetPngCompressionLevel(CompressionLevel level)
        {
            return level switch;
            {
                CompressionLevel.Maximum => PngCompressionLevel.BestCompression,
                CompressionLevel.High => PngCompressionLevel.DefaultCompression,
                CompressionLevel.Normal => PngCompressionLevel.DefaultCompression,
                CompressionLevel.Low => PngCompressionLevel.BestSpeed,
                _ => PngCompressionLevel.DefaultCompression;
            };
        }

        public void Dispose()
        {
            // Cleanup resources if needed;
        }
    }

    /// <summary>
    /// WebP optimization strategy;
    /// </summary>
    internal class WebPOptimizationStrategy : IOptimizationStrategy;
    {
        private readonly ILogger _logger;

        public string StrategyId => "WEBP_OPTIMIZATION_STRATEGY";
        public string Name => "WebP Optimization Strategy";
        public string Description => "Advanced WebP compression and optimization";

        public WebPOptimizationStrategy(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public bool SupportsFormat(string format)
        {
            return format?.ToUpperInvariant() == "WEBP";
        }

        public async Task OptimizeAsync(
            Stream inputStream,
            Stream outputStream,
            string format,
            OptimizationOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                inputStream.Position = 0;

                using (var image = await Image.LoadAsync(inputStream, cancellationToken))
                {
                    // Apply WebP-specific optimizations;
                    var encoder = new WebpEncoder;
                    {
                        Quality = options.Quality,
                        Method = options.EnableLosslessCompression ?
                            WebpEncodingMethod.Lossless : WebpEncodingMethod.Qualitative,
                        FileFormat = options.EnableLosslessCompression ?
                            WebpFileFormatType.Lossless : WebpFileFormatType.Lossy;
                    };

                    await image.SaveAsync(outputStream, encoder, cancellationToken);
                }

                _logger.LogDebug($"WebP optimization completed: {inputStream.Length} -> {outputStream.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebP optimization failed");
                throw new OptimizationException($"WebP optimization failed: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            // Cleanup resources if needed;
        }
    }

    /// <summary>
    /// Generic image optimization strategy;
    /// </summary>
    internal class GenericOptimizationStrategy : IOptimizationStrategy;
    {
        private readonly ILogger _logger;

        public string StrategyId => "GENERIC_OPTIMIZATION_STRATEGY";
        public string Name => "Generic Image Optimization Strategy";
        public string Description => "Generic optimization for various image formats";

        public GenericOptimizationStrategy(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public bool SupportsFormat(string format)
        {
            // Supports common image formats;
            var supportedFormats = new[] { "BMP", "TIFF", "TIF", "GIF", "ICO" };
            return supportedFormats.Contains(format?.ToUpperInvariant());
        }

        public async Task OptimizeAsync(
            Stream inputStream,
            Stream outputStream,
            string format,
            OptimizationOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                inputStream.Position = 0;

                using (var image = await Image.LoadAsync(inputStream, cancellationToken))
                {
                    // Apply resizing if needed;
                    if (options is ExtendedOptimizationOptions extendedOptions)
                    {
                        if (extendedOptions.MaxWidth > 0 || extendedOptions.MaxHeight > 0)
                        {
                            ApplyResizing(image, extendedOptions);
                        }
                    }

                    // Save with appropriate encoder;
                    var imageFormat = GetImageFormat(format);
                    var encoder = SixLabors.ImageSharp.Configuration.Default.ImageFormatsManager;
                        .GetEncoder(imageFormat);

                    await image.SaveAsync(outputStream, encoder, cancellationToken);
                }

                _logger.LogDebug($"Generic optimization completed for {format}: {inputStream.Length} -> {outputStream.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Generic optimization failed for {format}");
                throw new OptimizationException($"Generic optimization failed: {ex.Message}", ex);
            }
        }

        private void ApplyResizing(Image image, ExtendedOptimizationOptions options)
        {
            var resizeOptions = new ResizeOptions;
            {
                Size = new Size(options.MaxWidth, options.MaxHeight),
                Mode = ResizeMode.Max,
                Compand = false;
            };

            image.Mutate(x => x.Resize(resizeOptions));
        }

        private IImageFormat GetImageFormat(string format)
        {
            return format?.ToUpperInvariant() switch;
            {
                "BMP" => SixLabors.ImageSharp.Formats.Bmp.BmpFormat.Instance,
                "TIFF" or "TIF" => SixLabors.ImageSharp.Formats.Tiff.TiffFormat.Instance,
                "GIF" => SixLabors.ImageSharp.Formats.Gif.GifFormat.Instance,
                "ICO" => SixLabors.ImageSharp.Formats.Ico.IcoFormat.Instance,
                _ => throw new OptimizationException($"Unsupported format for generic strategy: {format}")
            };
        }

        public void Dispose()
        {
            // Cleanup resources if needed;
        }
    }

    /// <summary>
    /// Extended optimization options with additional properties;
    /// </summary>
    internal class ExtendedOptimizationOptions : OptimizationOptions;
    {
        public int MaxWidth { get; set; }
        public int MaxHeight { get; set; }
    }

    #endregion;
}
