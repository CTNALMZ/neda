using NEDA.ContentCreation.AssetPipeline.ImportManagers;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.MediaProcessing.ImageProcessing.ImageFilters;
using NEDA.Services.Messaging.EventBus;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;
using ImageFormat = SixLabors.ImageSharp.Formats.IImageFormat;

namespace NEDA.ContentCreation.AssetPipeline.FormatConverters;
{
    /// <summary>
    /// High-performance image format converter with advanced processing capabilities;
    /// Supports batch conversion, quality optimization, and metadata preservation;
    /// </summary>
    public class ImageConverter : IImageConverter, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ISettingsManager _settingsManager;
        private readonly IFilterEngine _filterEngine;
        private readonly SemaphoreSlim _processingLock;
        private readonly Dictionary<string, IImageFormat> _supportedFormats;
        private readonly Dictionary<ConversionPreset, ConversionSettings> _presets;
        private bool _disposed;

        public string ConverterId { get; }
        public ConverterStatus Status { get; private set; }
        public int TotalConversions { get; private set; }
        public long TotalBytesProcessed { get; private set; }
        public DateTime LastConversionTime { get; private set; }

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of ImageConverter;
        /// </summary>
        public ImageConverter(
            ILogger logger,
            IEventBus eventBus,
            ISettingsManager settingsManager,
            IFilterEngine filterEngine = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _filterEngine = filterEngine;

            ConverterId = Guid.NewGuid().ToString("N");
            _processingLock = new SemaphoreSlim(1, 1);

            InitializeSupportedFormats();
            InitializePresets();
            LoadConfiguration();

            Status = ConverterStatus.Ready;

            _logger.LogInformation($"ImageConverter initialized with ID: {ConverterId}");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Converts an image from one format to another;
        /// </summary>
        public async Task<ConversionResult> ConvertAsync(
            Stream inputStream,
            string sourceFormat,
            string targetFormat,
            ConversionSettings settings = null,
            CancellationToken cancellationToken = default)
        {
            ValidateParameters(inputStream, sourceFormat, targetFormat);

            var startTime = DateTime.UtcNow;
            var result = new ConversionResult;
            {
                ConversionId = Guid.NewGuid(),
                SourceFormat = sourceFormat,
                TargetFormat = targetFormat,
                StartTime = startTime;
            };

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                Status = ConverterStatus.Processing;

                await _eventBus.PublishAsync(new ConversionStartedEvent;
                {
                    ConversionId = result.ConversionId,
                    SourceFormat = sourceFormat,
                    TargetFormat = targetFormat,
                    Timestamp = startTime,
                    ConverterId = ConverterId;
                });

                _logger.LogDebug($"Starting conversion {result.ConversionId}: {sourceFormat} -> {targetFormat}");

                using (var outputStream = new MemoryStream())
                {
                    var conversionSettings = settings ?? GetDefaultSettings();

                    await ProcessConversionAsync(
                        inputStream,
                        outputStream,
                        sourceFormat,
                        targetFormat,
                        conversionSettings,
                        cancellationToken);

                    result.Success = true;
                    result.OutputData = outputStream.ToArray();
                    result.OriginalSize = inputStream.Length;
                    result.ConvertedSize = outputStream.Length;
                    result.CompressionRatio = CalculateCompressionRatio(inputStream.Length, outputStream.Length);

                    if (conversionSettings.PreserveMetadata)
                    {
                        await PreserveMetadataAsync(inputStream, outputStream, sourceFormat, targetFormat, cancellationToken);
                    }
                }

                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;

                UpdateStatistics(result);

                await _eventBus.PublishAsync(new ConversionCompletedEvent;
                {
                    ConversionId = result.ConversionId,
                    Success = true,
                    ProcessingTime = result.ProcessingTime,
                    OriginalSize = result.OriginalSize,
                    ConvertedSize = result.ConvertedSize,
                    CompressionRatio = result.CompressionRatio,
                    Timestamp = result.EndTime,
                    ConverterId = ConverterId;
                });

                _logger.LogInformation($"Conversion {result.ConversionId} completed successfully in {result.ProcessingTime.TotalMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;

                await HandleConversionErrorAsync(result, ex, cancellationToken);
                throw new ImageConversionException($"Failed to convert image: {ex.Message}", ex);
            }
            finally
            {
                Status = ConverterStatus.Ready;
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Converts an image file from disk;
        /// </summary>
        public async Task<FileConversionResult> ConvertFileAsync(
            string sourcePath,
            string targetPath,
            ConversionSettings settings = null,
            CancellationToken cancellationToken = default)
        {
            ValidateFilePaths(sourcePath, targetPath);

            var startTime = DateTime.UtcNow;
            var sourceFormat = GetFormatFromExtension(sourcePath);
            var targetFormat = GetFormatFromExtension(targetPath);

            var result = new FileConversionResult;
            {
                ConversionId = Guid.NewGuid(),
                SourcePath = sourcePath,
                TargetPath = targetPath,
                SourceFormat = sourceFormat,
                TargetFormat = targetFormat,
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

                await _eventBus.PublishAsync(new FileConversionStartedEvent;
                {
                    ConversionId = result.ConversionId,
                    SourcePath = sourcePath,
                    TargetPath = targetPath,
                    SourceFormat = sourceFormat,
                    TargetFormat = targetFormat,
                    FileSize = fileInfo.Length,
                    Timestamp = startTime,
                    ConverterId = ConverterId;
                });

                _logger.LogDebug($"Starting file conversion {result.ConversionId}: {sourcePath} -> {targetPath}");

                using (var inputStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var outputStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var conversionSettings = settings ?? GetDefaultSettings();

                    await ProcessConversionAsync(
                        inputStream,
                        outputStream,
                        sourceFormat,
                        targetFormat,
                        conversionSettings,
                        cancellationToken);
                }

                var targetFileInfo = new FileInfo(targetPath);
                result.ConvertedSize = targetFileInfo.Length;
                result.CompressionRatio = CalculateCompressionRatio(result.OriginalSize, result.ConvertedSize);
                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;

                UpdateStatistics(result);

                await _eventBus.PublishAsync(new FileConversionCompletedEvent;
                {
                    ConversionId = result.ConversionId,
                    Success = true,
                    ProcessingTime = result.ProcessingTime,
                    OriginalSize = result.OriginalSize,
                    ConvertedSize = result.ConvertedSize,
                    CompressionRatio = result.CompressionRatio,
                    Timestamp = result.EndTime,
                    ConverterId = ConverterId;
                });

                _logger.LogInformation($"File conversion {result.ConversionId} completed: {sourcePath} -> {targetPath}");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.UtcNow;
                result.ProcessingTime = result.EndTime - startTime;

                await HandleFileConversionErrorAsync(result, ex, cancellationToken);

                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { }
                }

                throw new ImageConversionException($"Failed to convert file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Batch converts multiple images;
        /// </summary>
        public async Task<BatchConversionResult> ConvertBatchAsync(
            IEnumerable<BatchConversionItem> items,
            BatchConversionSettings batchSettings = null,
            CancellationToken cancellationToken = default)
        {
            if (items == null || !items.Any())
            {
                throw new ArgumentException("No conversion items provided", nameof(items));
            }

            var startTime = DateTime.UtcNow;
            var result = new BatchConversionResult;
            {
                BatchId = Guid.NewGuid(),
                StartTime = startTime,
                TotalItems = items.Count()
            };

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                Status = ConverterStatus.ProcessingBatch;

                var settings = batchSettings ?? new BatchConversionSettings();
                var parallelOptions = new ParallelOptions;
                {
                    MaxDegreeOfParallelism = settings.MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken;
                };

                await _eventBus.PublishAsync(new BatchConversionStartedEvent;
                {
                    BatchId = result.BatchId,
                    ItemCount = result.TotalItems,
                    Timestamp = startTime,
                    ConverterId = ConverterId;
                });

                _logger.LogInformation($"Starting batch conversion {result.BatchId} with {result.TotalItems} items");

                var successfulConversions = 0;
                var failedConversions = 0;
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
                        if (item.SourceStream != null && item.TargetStream != null)
                        {
                            await ConvertStreamItemAsync(item, itemResult, ct);
                        }
                        else if (!string.IsNullOrEmpty(item.SourcePath) && !string.IsNullOrEmpty(item.TargetPath))
                        {
                            await ConvertFileItemAsync(item, itemResult, ct);
                        }
                        else;
                        {
                            throw new ImageConversionException("Invalid conversion item configuration");
                        }

                        Interlocked.Increment(ref successfulConversions);
                        itemResult.Success = true;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failedConversions);
                        itemResult.Success = false;
                        itemResult.ErrorMessage = ex.Message;
                        _logger.LogError(ex, $"Failed to convert batch item {item.ItemId}");
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
                result.SuccessfulItems = successfulConversions;
                result.FailedItems = failedConversions;
                result.ItemResults = results;
                result.Success = failedConversions == 0;

                UpdateBatchStatistics(result);

                await _eventBus.PublishAsync(new BatchConversionCompletedEvent;
                {
                    BatchId = result.BatchId,
                    Success = result.Success,
                    TotalItems = result.TotalItems,
                    SuccessfulItems = result.SuccessfulItems,
                    FailedItems = result.FailedItems,
                    ProcessingTime = result.ProcessingTime,
                    Timestamp = result.EndTime,
                    ConverterId = ConverterId;
                });

                _logger.LogInformation($"Batch conversion {result.BatchId} completed: {successfulConversions} successful, {failedConversions} failed");

                return result;
            }
            finally
            {
                Status = ConverterStatus.Ready;
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Gets supported image formats;
        /// </summary>
        public IEnumerable<ImageFormatInfo> GetSupportedFormats()
        {
            return _supportedFormats.Keys.Select(format => new ImageFormatInfo;
            {
                Format = format,
                Extensions = GetExtensionsForFormat(format),
                CanRead = true,
                CanWrite = true,
                SupportsTransparency = SupportsTransparency(format),
                SupportsAnimation = SupportsAnimation(format)
            });
        }

        /// <summary>
        /// Validates if conversion is supported;
        /// </summary>
        public bool IsConversionSupported(string sourceFormat, string targetFormat)
        {
            if (string.IsNullOrWhiteSpace(sourceFormat) || string.IsNullOrWhiteSpace(targetFormat))
            {
                return false;
            }

            var normalizedSource = NormalizeFormat(sourceFormat);
            var normalizedTarget = NormalizeFormat(targetFormat);

            return _supportedFormats.ContainsKey(normalizedSource) &&
                   _supportedFormats.ContainsKey(normalizedTarget) &&
                   !string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets optimal conversion settings for a specific use case;
        /// </summary>
        public ConversionSettings GetOptimalSettings(
            string sourceFormat,
            string targetFormat,
            ConversionPurpose purpose)
        {
            var settings = GetDefaultSettings();

            switch (purpose)
            {
                case ConversionPurpose.Web:
                    settings.Quality = 85;
                    settings.MaxWidth = 1920;
                    settings.MaxHeight = 1080;
                    settings.ResizeMode = ResizeMode.Max;
                    settings.OptimizeForWeb = true;
                    settings.ProgressiveEncoding = true;
                    break;

                case ConversionPurpose.Print:
                    settings.Quality = 100;
                    settings.Dpi = 300;
                    settings.ColorSpace = ColorSpace.Cmyk;
                    settings.PreserveMetadata = true;
                    break;

                case ConversionPurpose.Archive:
                    settings.Quality = 100;
                    settings.LosslessCompression = true;
                    settings.PreserveMetadata = true;
                    break;

                case ConversionPurpose.Thumbnail:
                    settings.Quality = 75;
                    settings.MaxWidth = 320;
                    settings.MaxHeight = 240;
                    settings.ResizeMode = ResizeMode.Crop;
                    settings.CreateThumbnail = true;
                    break;

                case ConversionPurpose.Mobile:
                    settings.Quality = 80;
                    settings.MaxWidth = 800;
                    settings.MaxHeight = 600;
                    settings.ResizeMode = ResizeMode.Stretch;
                    settings.OptimizeForMobile = true;
                    break;
            }

            return settings;
        }

        /// <summary>
        /// Gets converter statistics;
        /// </summary>
        public ConverterStatistics GetStatistics()
        {
            return new ConverterStatistics;
            {
                ConverterId = ConverterId,
                Status = Status,
                TotalConversions = TotalConversions,
                TotalBytesProcessed = TotalBytesProcessed,
                LastConversionTime = LastConversionTime,
                SupportedFormats = _supportedFormats.Count,
                PresetsAvailable = _presets.Count;
            };
        }

        #endregion;

        #region Private Methods;

        private void InitializeSupportedFormats()
        {
            _supportedFormats = new Dictionary<string, IImageFormat>(StringComparer.OrdinalIgnoreCase)
            {
                { "JPEG", SixLabors.ImageSharp.Formats.Jpeg.JpegFormat.Instance },
                { "JPG", SixLabors.ImageSharp.Formats.Jpeg.JpegFormat.Instance },
                { "PNG", SixLabors.ImageSharp.Formats.Png.PngFormat.Instance },
                { "GIF", SixLabors.ImageSharp.Formats.Gif.GifFormat.Instance },
                { "BMP", SixLabors.ImageSharp.Formats.Bmp.BmpFormat.Instance },
                { "TIFF", SixLabors.ImageSharp.Formats.Tiff.TiffFormat.Instance },
                { "TIF", SixLabors.ImageSharp.Formats.Tiff.TiffFormat.Instance },
                { "WEBP", SixLabors.ImageSharp.Formats.Webp.WebpFormat.Instance },
                { "ICO", SixLabors.ImageSharp.Formats.Ico.IcoFormat.Instance }
            };
        }

        private void InitializePresets()
        {
            _presets = new Dictionary<ConversionPreset, ConversionSettings>
            {
                [ConversionPreset.HighQuality] = new ConversionSettings;
                {
                    Quality = 95,
                    LosslessCompression = true,
                    PreserveMetadata = true,
                    Dpi = 300;
                },

                [ConversionPreset.WebOptimized] = new ConversionSettings;
                {
                    Quality = 85,
                    MaxWidth = 1920,
                    MaxHeight = 1080,
                    OptimizeForWeb = true,
                    ProgressiveEncoding = true;
                },

                [ConversionPreset.MobileOptimized] = new ConversionSettings;
                {
                    Quality = 80,
                    MaxWidth = 800,
                    MaxHeight = 600,
                    OptimizeForMobile = true;
                },

                [ConversionPreset.Thumbnail] = new ConversionSettings;
                {
                    Quality = 75,
                    MaxWidth = 320,
                    MaxHeight = 240,
                    ResizeMode = ResizeMode.Crop,
                    CreateThumbnail = true;
                },

                [ConversionPreset.Archive] = new ConversionSettings;
                {
                    Quality = 100,
                    LosslessCompression = true,
                    PreserveMetadata = true;
                }
            };
        }

        private void LoadConfiguration()
        {
            try
            {
                var config = _settingsManager.GetSection<ImageConverterConfig>("ImageConverter");
                if (config != null)
                {
                    // Apply configuration;
                    _logger.LogDebug("ImageConverter configuration loaded successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load ImageConverter configuration, using defaults");
            }
        }

        private async Task ProcessConversionAsync(
            Stream inputStream,
            Stream outputStream,
            string sourceFormat,
            string targetFormat,
            ConversionSettings settings,
            CancellationToken cancellationToken)
        {
            using (var image = await Image.LoadAsync(inputStream, cancellationToken))
            {
                // Apply preprocessing if filter engine is available;
                if (_filterEngine != null && settings.PreprocessFilters != null)
                {
                    await ApplyFiltersAsync(image, settings.PreprocessFilters, cancellationToken);
                }

                // Apply resizing if needed;
                if (settings.MaxWidth > 0 || settings.MaxHeight > 0)
                {
                    ApplyResizing(image, settings);
                }

                // Apply color space conversion if needed;
                if (settings.ColorSpace != ColorSpace.Default)
                {
                    ApplyColorSpaceConversion(image, settings.ColorSpace);
                }

                // Configure encoder based on target format and settings;
                var encoder = GetEncoder(targetFormat, settings);

                // Save the image;
                await image.SaveAsync(outputStream, encoder, cancellationToken);
            }
        }

        private void ApplyResizing(Image image, ConversionSettings settings)
        {
            var options = new ResizeOptions;
            {
                Size = new Size(settings.MaxWidth, settings.MaxHeight),
                Mode = ConvertResizeMode(settings.ResizeMode),
                Compand = settings.Compand;
            };

            if (settings.MaintainAspectRatio)
            {
                options.Size = CalculateAspectRatioSize(
                    image.Width,
                    image.Height,
                    settings.MaxWidth,
                    settings.MaxHeight);
            }

            image.Mutate(x => x.Resize(options));
        }

        private async Task ApplyFiltersAsync(Image image, IEnumerable<FilterSettings> filters, CancellationToken cancellationToken)
        {
            foreach (var filter in filters)
            {
                await _filterEngine.ApplyFilterAsync(image, filter, cancellationToken);
            }
        }

        private void ApplyColorSpaceConversion(Image image, ColorSpace targetColorSpace)
        {
            // Implement color space conversion logic;
            // This would typically involve color profile transformations;
        }

        private IImageEncoder GetEncoder(string format, ConversionSettings settings)
        {
            var normalizedFormat = NormalizeFormat(format);

            switch (normalizedFormat)
            {
                case "JPEG":
                case "JPG":
                    return new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder;
                    {
                        Quality = settings.Quality,
                        ColorType = settings.ProgressiveEncoding ?
                            SixLabors.ImageSharp.Formats.Jpeg.JpegColorType.YCbCr :
                            SixLabors.ImageSharp.Formats.Jpeg.JpegColorType.Rgb,
                        Interleaved = settings.ProgressiveEncoding;
                    };

                case "PNG":
                    return new SixLabors.ImageSharp.Formats.Png.PngEncoder;
                    {
                        CompressionLevel = GetPngCompressionLevel(settings.Quality),
                        ColorType = SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha,
                        BitDepth = SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8,
                        TransparentColorMode = SixLabors.ImageSharp.Formats.Png.PngTransparentColorMode.Preserve;
                    };

                case "WEBP":
                    return new SixLabors.ImageSharp.Formats.Webp.WebpEncoder;
                    {
                        Quality = settings.Quality,
                        Method = settings.LosslessCompression ?
                            SixLabors.ImageSharp.Formats.Webp.WebpEncodingMethod.Lossless :
                            SixLabors.ImageSharp.Formats.Webp.WebpEncodingMethod.Qualitative,
                        FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossy;
                    };

                default:
                    // Return default encoder for other formats;
                    return SixLabors.ImageSharp.Configuration.Default.ImageFormatsManager;
                        .GetEncoder(_supportedFormats[normalizedFormat]);
            }
        }

        private async Task PreserveMetadataAsync(
            Stream sourceStream,
            Stream targetStream,
            string sourceFormat,
            string targetFormat,
            CancellationToken cancellationToken)
        {
            // Implement metadata preservation logic;
            // This would extract EXIF, IPTC, XMP metadata from source and inject into target;
            await Task.CompletedTask;
        }

        private async Task ConvertStreamItemAsync(BatchConversionItem item, BatchItemResult result, CancellationToken cancellationToken)
        {
            var settings = item.Settings ?? GetDefaultSettings();

            item.SourceStream.Position = 0;
            result.OriginalSize = item.SourceStream.Length;

            using (var outputStream = new MemoryStream())
            {
                await ProcessConversionAsync(
                    item.SourceStream,
                    outputStream,
                    item.SourceFormat,
                    item.TargetFormat,
                    settings,
                    cancellationToken);

                outputStream.Position = 0;
                await outputStream.CopyToAsync(item.TargetStream, cancellationToken);

                result.ConvertedSize = outputStream.Length;
                result.CompressionRatio = CalculateCompressionRatio(result.OriginalSize, result.ConvertedSize);
            }
        }

        private async Task ConvertFileItemAsync(BatchConversionItem item, BatchItemResult result, CancellationToken cancellationToken)
        {
            var settings = item.Settings ?? GetDefaultSettings();

            using (var inputStream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var outputStream = new FileStream(item.TargetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                result.OriginalSize = inputStream.Length;

                await ProcessConversionAsync(
                    inputStream,
                    outputStream,
                    item.SourceFormat,
                    item.TargetFormat,
                    settings,
                    cancellationToken);

                result.ConvertedSize = outputStream.Length;
                result.CompressionRatio = CalculateCompressionRatio(result.OriginalSize, result.ConvertedSize);
            }
        }

        private async Task HandleConversionErrorAsync(ConversionResult result, Exception ex, CancellationToken cancellationToken)
        {
            _logger.LogError(ex, $"Conversion {result.ConversionId} failed: {ex.Message}");

            await _eventBus.PublishAsync(new ConversionFailedEvent;
            {
                ConversionId = result.ConversionId,
                ErrorMessage = ex.Message,
                ProcessingTime = result.ProcessingTime,
                Timestamp = result.EndTime,
                ConverterId = ConverterId;
            });
        }

        private async Task HandleFileConversionErrorAsync(FileConversionResult result, Exception ex, CancellationToken cancellationToken)
        {
            _logger.LogError(ex, $"File conversion {result.ConversionId} failed: {ex.Message}");

            await _eventBus.PublishAsync(new FileConversionFailedEvent;
            {
                ConversionId = result.ConversionId,
                SourcePath = result.SourcePath,
                TargetPath = result.TargetPath,
                ErrorMessage = ex.Message,
                ProcessingTime = result.ProcessingTime,
                Timestamp = result.EndTime,
                ConverterId = ConverterId;
            });
        }

        private void UpdateStatistics(ConversionResult result)
        {
            TotalConversions++;
            TotalBytesProcessed += result.ConvertedSize;
            LastConversionTime = DateTime.UtcNow;
        }

        private void UpdateStatistics(FileConversionResult result)
        {
            TotalConversions++;
            TotalBytesProcessed += result.ConvertedSize;
            LastConversionTime = DateTime.UtcNow;
        }

        private void UpdateBatchStatistics(BatchConversionResult result)
        {
            TotalConversions += result.SuccessfulItems;
            TotalBytesProcessed += result.ItemResults;
                .Where(r => r.Success)
                .Sum(r => r.ConvertedSize);
            LastConversionTime = DateTime.UtcNow;
        }

        private void ValidateParameters(Stream inputStream, string sourceFormat, string targetFormat)
        {
            if (inputStream == null)
            {
                throw new ArgumentNullException(nameof(inputStream));
            }

            if (string.IsNullOrWhiteSpace(sourceFormat))
            {
                throw new ArgumentException("Source format cannot be empty", nameof(sourceFormat));
            }

            if (string.IsNullOrWhiteSpace(targetFormat))
            {
                throw new ArgumentException("Target format cannot be empty", nameof(targetFormat));
            }

            if (!IsConversionSupported(sourceFormat, targetFormat))
            {
                throw new ImageConversionException($"Conversion from {sourceFormat} to {targetFormat} is not supported");
            }
        }

        private void ValidateFilePaths(string sourcePath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path cannot be empty", nameof(sourcePath));
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("Target path cannot be empty", nameof(targetPath));
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
                "ICO" => "ICO",
                _ => throw new ImageConversionException($"Unsupported file extension: {extension}")
            };
        }

        private Size CalculateAspectRatioSize(int originalWidth, int originalHeight, int maxWidth, int maxHeight)
        {
            double ratioX = (double)maxWidth / originalWidth;
            double ratioY = (double)maxHeight / originalHeight;
            double ratio = Math.Min(ratioX, ratioY);

            return new Size((int)(originalWidth * ratio), (int)(originalHeight * ratio));
        }

        private double CalculateCompressionRatio(long originalSize, long convertedSize)
        {
            if (originalSize == 0) return 0;
            return (double)convertedSize / originalSize;
        }

        private SixLabors.ImageSharp.Formats.Png.PngCompressionLevel GetPngCompressionLevel(int quality)
        {
            return quality switch;
            {
                >= 90 => SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestCompression,
                >= 70 => SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.DefaultCompression,
                _ => SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed;
            };
        }

        private SixLabors.ImageSharp.Processing.ResizeMode ConvertResizeMode(ResizeMode mode)
        {
            return mode switch;
            {
                ResizeMode.Crop => SixLabors.ImageSharp.Processing.ResizeMode.Crop,
                ResizeMode.Pad => SixLabors.ImageSharp.Processing.ResizeMode.Pad,
                ResizeMode.BoxPad => SixLabors.ImageSharp.Processing.ResizeMode.BoxPad,
                ResizeMode.Max => SixLabors.ImageSharp.Processing.ResizeMode.Max,
                ResizeMode.Min => SixLabors.ImageSharp.Processing.ResizeMode.Min,
                ResizeMode.Stretch => SixLabors.ImageSharp.Processing.ResizeMode.Stretch,
                _ => SixLabors.ImageSharp.Processing.ResizeMode.Crop;
            };
        }

        private string NormalizeFormat(string format)
        {
            return format?.Trim().ToUpperInvariant() switch;
            {
                "JPG" => "JPEG",
                "TIF" => "TIFF",
                var f when _supportedFormats.ContainsKey(f) => f,
                _ => throw new ImageConversionException($"Unsupported format: {format}")
            };
        }

        private IEnumerable<string> GetExtensionsForFormat(string format)
        {
            return format switch;
            {
                "JPEG" => new[] { ".jpg", ".jpeg" },
                "PNG" => new[] { ".png" },
                "GIF" => new[] { ".gif" },
                "BMP" => new[] { ".bmp" },
                "TIFF" => new[] { ".tif", ".tiff" },
                "WEBP" => new[] { ".webp" },
                "ICO" => new[] { ".ico" },
                _ => new[] { $".{format.ToLower()}" }
            };
        }

        private bool SupportsTransparency(string format)
        {
            return format switch;
            {
                "PNG" => true,
                "GIF" => true,
                "WEBP" => true,
                _ => false;
            };
        }

        private bool SupportsAnimation(string format)
        {
            return format switch;
            {
                "GIF" => true,
                "WEBP" => true,
                _ => false;
            };
        }

        private ConversionSettings GetDefaultSettings()
        {
            return new ConversionSettings;
            {
                Quality = 90,
                MaxWidth = 0,
                MaxHeight = 0,
                ResizeMode = ResizeMode.Crop,
                MaintainAspectRatio = true,
                Dpi = 96,
                ColorSpace = ColorSpace.Rgb,
                PreserveMetadata = false,
                OptimizeForWeb = false,
                OptimizeForMobile = false,
                ProgressiveEncoding = false,
                LosslessCompression = false,
                CreateThumbnail = false;
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
    /// Image conversion settings;
    /// </summary>
    public class ConversionSettings;
    {
        public int Quality { get; set; }
        public int MaxWidth { get; set; }
        public int MaxHeight { get; set; }
        public ResizeMode ResizeMode { get; set; }
        public bool MaintainAspectRatio { get; set; }
        public int Dpi { get; set; }
        public ColorSpace ColorSpace { get; set; }
        public bool PreserveMetadata { get; set; }
        public bool OptimizeForWeb { get; set; }
        public bool OptimizeForMobile { get; set; }
        public bool ProgressiveEncoding { get; set; }
        public bool LosslessCompression { get; set; }
        public bool CreateThumbnail { get; set; }
        public bool Compand { get; set; }
        public IEnumerable<FilterSettings> PreprocessFilters { get; set; }

        public ConversionSettings()
        {
            PreprocessFilters = new List<FilterSettings>();
        }
    }

    /// <summary>
    /// Batch conversion settings;
    /// </summary>
    public class BatchConversionSettings;
    {
        public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
        public bool StopOnFirstFailure { get; set; }
        public TimeSpan TimeoutPerItem { get; set; } = TimeSpan.FromMinutes(5);
        public bool CreateBackup { get; set; }
        public string BackupDirectory { get; set; }
    }

    /// <summary>
    /// Batch conversion item;
    /// </summary>
    public class BatchConversionItem;
    {
        public Guid ItemId { get; set; } = Guid.NewGuid();
        public string SourcePath { get; set; }
        public Stream SourceStream { get; set; }
        public string SourceFormat { get; set; }
        public string TargetPath { get; set; }
        public Stream TargetStream { get; set; }
        public string TargetFormat { get; set; }
        public ConversionSettings Settings { get; set; }
    }

    /// <summary>
    /// Conversion result;
    /// </summary>
    public class ConversionResult;
    {
        public Guid ConversionId { get; set; }
        public bool Success { get; set; }
        public string SourceFormat { get; set; }
        public string TargetFormat { get; set; }
        public byte[] OutputData { get; set; }
        public long OriginalSize { get; set; }
        public long ConvertedSize { get; set; }
        public double CompressionRatio { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// File conversion result;
    /// </summary>
    public class FileConversionResult : ConversionResult;
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
    }

    /// <summary>
    /// Batch conversion result;
    /// </summary>
    public class BatchConversionResult;
    {
        public Guid BatchId { get; set; }
        public bool Success { get; set; }
        public int TotalItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
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
        public long ConvertedSize { get; set; }
        public double CompressionRatio { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Image format information;
    /// </summary>
    public class ImageFormatInfo;
    {
        public string Format { get; set; }
        public IEnumerable<string> Extensions { get; set; }
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }
        public bool SupportsTransparency { get; set; }
        public bool SupportsAnimation { get; set; }
        public string MimeType { get; set; }
    }

    /// <summary>
    /// Converter statistics;
    /// </summary>
    public class ConverterStatistics;
    {
        public string ConverterId { get; set; }
        public ConverterStatus Status { get; set; }
        public int TotalConversions { get; set; }
        public long TotalBytesProcessed { get; set; }
        public DateTime LastConversionTime { get; set; }
        public int SupportedFormats { get; set; }
        public int PresetsAvailable { get; set; }
        public double AverageCompressionRatio { get; set; }
    }

    /// <summary>
    /// Image converter configuration;
    /// </summary>
    public class ImageConverterConfig;
    {
        public int DefaultQuality { get; set; } = 90;
        public int MaxConcurrentOperations { get; set; } = Environment.ProcessorCount;
        public bool EnableCaching { get; set; } = true;
        public int CacheSizeMB { get; set; } = 100;
        public bool EnableLogging { get; set; } = true;
        public string DefaultOutputDirectory { get; set; }
        public bool OverwriteExistingFiles { get; set; } = true;
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Converter status;
    /// </summary>
    public enum ConverterStatus;
    {
        Ready = 0,
        Processing = 1,
        ProcessingBatch = 2,
        Error = 3;
    }

    /// <summary>
    /// Conversion purpose;
    /// </summary>
    public enum ConversionPurpose;
    {
        Web = 0,
        Print = 1,
        Archive = 2,
        Thumbnail = 3,
        Mobile = 4,
        Default = 5;
    }

    /// <summary>
    /// Color space;
    /// </summary>
    public enum ColorSpace;
    {
        Default = 0,
        Rgb = 1,
        Cmyk = 2,
        Grayscale = 3,
        Srgb = 4,
        AdobeRgb = 5;
    }

    /// <summary>
    /// Resize mode;
    /// </summary>
    public enum ResizeMode;
    {
        Crop = 0,
        Pad = 1,
        BoxPad = 2,
        Max = 3,
        Min = 4,
        Stretch = 5;
    }

    /// <summary>
    /// Conversion preset;
    /// </summary>
    public enum ConversionPreset;
    {
        HighQuality = 0,
        WebOptimized = 1,
        MobileOptimized = 2,
        Thumbnail = 3,
        Archive = 4;
    }

    #endregion;

    #region Events;

    public abstract class ConverterEvent : IEvent;
    {
        public string ConverterId { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    public class ConversionStartedEvent : ConverterEvent;
    {
        public Guid ConversionId { get; set; }
        public string SourceFormat { get; set; }
        public string TargetFormat { get; set; }
    }

    public class ConversionCompletedEvent : ConverterEvent;
    {
        public Guid ConversionId { get; set; }
        public bool Success { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public long OriginalSize { get; set; }
        public long ConvertedSize { get; set; }
        public double CompressionRatio { get; set; }
    }

    public class ConversionFailedEvent : ConverterEvent;
    {
        public Guid ConversionId { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class FileConversionStartedEvent : ConverterEvent;
    {
        public Guid ConversionId { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string SourceFormat { get; set; }
        public string TargetFormat { get; set; }
        public long FileSize { get; set; }
    }

    public class FileConversionCompletedEvent : ConverterEvent;
    {
        public Guid ConversionId { get; set; }
        public bool Success { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public long OriginalSize { get; set; }
        public long ConvertedSize { get; set; }
        public double CompressionRatio { get; set; }
    }

    public class FileConversionFailedEvent : ConverterEvent;
    {
        public Guid ConversionId { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class BatchConversionStartedEvent : ConverterEvent;
    {
        public Guid BatchId { get; set; }
        public int ItemCount { get; set; }
    }

    public class BatchConversionCompletedEvent : ConverterEvent;
    {
        public Guid BatchId { get; set; }
        public bool Success { get; set; }
        public int TotalItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Image conversion exception;
    /// </summary>
    public class ImageConversionException : Exception
    {
        public ImageConversionException(string message) : base(message) { }
        public ImageConversionException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// Image converter interface;
    /// </summary>
    public interface IImageConverter : IDisposable
    {
        Task<ConversionResult> ConvertAsync(
            Stream inputStream,
            string sourceFormat,
            string targetFormat,
            ConversionSettings settings = null,
            CancellationToken cancellationToken = default);

        Task<FileConversionResult> ConvertFileAsync(
            string sourcePath,
            string targetPath,
            ConversionSettings settings = null,
            CancellationToken cancellationToken = default);

        Task<BatchConversionResult> ConvertBatchAsync(
            IEnumerable<BatchConversionItem> items,
            BatchConversionSettings batchSettings = null,
            CancellationToken cancellationToken = default);

        IEnumerable<ImageFormatInfo> GetSupportedFormats();
        bool IsConversionSupported(string sourceFormat, string targetFormat);
        ConversionSettings GetOptimalSettings(string sourceFormat, string targetFormat, ConversionPurpose purpose);
        ConverterStatistics GetStatistics();
    }

    #endregion;
}
