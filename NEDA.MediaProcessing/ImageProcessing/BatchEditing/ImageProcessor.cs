using Microsoft.Extensions.DependencyInjection;
using NEDA.Common.Utilities;
using NEDA.Interface.TextInput.NaturalLanguageInput;
using NEDA.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NEDA.MediaProcessing.ImageProcessing.ImageFilters;
{
    /// <summary>
    /// High-performance image processing engine with support for filters, effects, and batch operations;
    /// Optimized for both CPU and GPU processing using parallel algorithms;
    /// </summary>
    public class ImageProcessor : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly FilterEngine _filterEngine;
        private readonly ColorCorrectionEngine _colorCorrector;
        private readonly ImageOptimizationEngine _optimizationEngine;
        private readonly ParallelProcessingManager _parallelManager;

        private ProcessingMode _currentMode;
        private ImageFormat _outputFormat;
        private QualitySettings _qualitySettings;
        private bool _useGpuAcceleration;
        private bool _isDisposed;

        private readonly object _processingLock = new object();

        /// <summary>
        /// Image processing modes;
        /// </summary>
        public enum ProcessingMode;
        {
            SingleImage,
            Batch,
            Stream,
            RealTime;
        }

        /// <summary>
        /// Image processing quality settings;
        /// </summary>
        public class QualitySettings;
        {
            public int JPEGQuality { get; set; } = 85;
            public CompressionLevel PNGCompression { get; set; } = CompressionLevel.Optimal;
            public bool PreserveMetadata { get; set; } = true;
            public ColorProfile ColorProfile { get; set; } = ColorProfile.SRGB;
            public bool MaintainAspectRatio { get; set; } = true;
            public InterpolationMode Interpolation { get; set; } = InterpolationMode.HighQualityBicubic;
            public bool UseLosslessCompression { get; set; } = false;
        }

        /// <summary>
        /// Image processing configuration;
        /// </summary>
        public class ProcessorConfiguration;
        {
            public ProcessingMode DefaultMode { get; set; } = ProcessingMode.SingleImage;
            public bool EnableGpuAcceleration { get; set; } = false;
            public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
            public ImageFormat DefaultOutputFormat { get; set; } = ImageFormat.Jpeg;
            public QualitySettings Quality { get; set; } = new QualitySettings();
            public MemoryCacheSettings CacheSettings { get; set; } = new MemoryCacheSettings();
            public int MaxImageDimension { get; set; } = 8192;
        }

        /// <summary>
        /// Initialize ImageProcessor with configuration;
        /// </summary>
        public ImageProcessor(IServiceProvider serviceProvider, ILogger logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Initialize components;
            _filterEngine = new FilterEngine(logger);
            _colorCorrector = new ColorCorrectionEngine(logger);
            _optimizationEngine = new ImageOptimizationEngine(logger);
            _parallelManager = new ParallelProcessingManager(Environment.ProcessorCount);

            // Default configuration;
            _currentMode = ProcessingMode.SingleImage;
            _outputFormat = ImageFormat.Jpeg;
            _qualitySettings = new QualitySettings();

            _logger.LogInformation("ImageProcessor initialized");
        }

        /// <summary>
        /// Configure the processor with custom settings;
        /// </summary>
        public void Configure(ProcessorConfiguration configuration)
        {
            lock (_processingLock)
            {
                _currentMode = configuration.DefaultMode;
                _useGpuAcceleration = configuration.EnableGpuAcceleration && IsGpuAvailable();
                _outputFormat = configuration.DefaultOutputFormat;
                _qualitySettings = configuration.Quality ?? new QualitySettings();

                _parallelManager.MaxDegreeOfParallelism = configuration.MaxDegreeOfParallelism;

                if (_useGpuAcceleration)
                {
                    InitializeGpuAcceleration();
                }

                _logger.LogInformation($"ImageProcessor configured: Mode={_currentMode}, GPU={_useGpuAcceleration}");
            }
        }

        /// <summary>
        /// Apply a single filter to an image;
        /// </summary>
        public async Task<ProcessedImage> ApplyFilterAsync(Image input, ImageFilter filter, FilterParameters parameters = null)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug($"Applying filter: {filter.Name} to image");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                ProcessedImage result;

                if (_useGpuAcceleration && filter.SupportsGpu)
                {
                    result = await ApplyFilterGpuAsync(input, filter, parameters);
                }
                else;
                {
                    result = await ApplyFilterCpuAsync(input, filter, parameters);
                }

                stopwatch.Stop();
                _logger.LogDebug($"Filter applied in {stopwatch.ElapsedMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply filter: {filter.Name}");
                throw new ImageProcessingException($"Filter application failed: {filter.Name}", ex);
            }
        }

        /// <summary>
        /// Apply multiple filters sequentially;
        /// </summary>
        public async Task<ProcessedImage> ApplyFilterChainAsync(Image input, IEnumerable<ImageFilter> filters,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug($"Applying filter chain with {filters.Count()} filters");

                var currentImage = input;
                var processingStats = new ProcessingStatistics();

                foreach (var filter in filters)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Filter chain processing cancelled");
                        break;
                    }

                    var parameters = GetDefaultParameters(filter);
                    var result = await ApplyFilterAsync(currentImage, filter, parameters);

                    currentImage = result.Image;
                    processingStats.AddFilterResult(filter.Name, result.ProcessingTime);

                    // Clean up previous image if it's not the original;
                    if (currentImage != input && currentImage != result.Image)
                    {
                        currentImage?.Dispose();
                    }
                }

                var finalResult = new ProcessedImage(currentImage, processingStats);
                _logger.LogInformation($"Filter chain completed: {processingStats}");

                return finalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Filter chain processing failed");
                throw new ImageProcessingException("Filter chain processing failed", ex);
            }
        }

        /// <summary>
        /// Batch process multiple images with the same filter;
        /// </summary>
        public async Task<BatchProcessingResult> BatchProcessAsync(IEnumerable<Image> images, ImageFilter filter,
            BatchProcessingOptions options = null)
        {
            var imagesList = images.ToList();
            if (!imagesList.Any())
            {
                return new BatchProcessingResult();
            }

            try
            {
                _logger.LogInformation($"Starting batch processing of {imagesList.Count} images");

                var result = new BatchProcessingResult();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                if (_currentMode == ProcessingMode.Batch && imagesList.Count > 1)
                {
                    // Parallel processing for batch mode;
                    var parallelOptions = new ParallelOptions;
                    {
                        MaxDegreeOfParallelism = _parallelManager.MaxDegreeOfParallelism,
                        CancellationToken = options?.CancellationToken ?? CancellationToken.None;
                    };

                    await Parallel.ForEachAsync(imagesList, parallelOptions, async (image, ct) =>
                    {
                        try
                        {
                            var processed = await ApplyFilterAsync(image, filter, options?.Parameters);
                            lock (result)
                            {
                                result.SuccessfulProcesses++;
                                result.ProcessedImages.Add(processed);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to process image in batch");
                            lock (result)
                            {
                                result.FailedProcesses++;
                                result.Errors.Add(new ProcessingError(image, ex.Message));
                            }
                        }
                    });
                }
                else;
                {
                    // Sequential processing;
                    foreach (var image in imagesList)
                    {
                        try
                        {
                            var processed = await ApplyFilterAsync(image, filter, options?.Parameters);
                            result.SuccessfulProcesses++;
                            result.ProcessedImages.Add(processed);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to process image in batch");
                            result.FailedProcesses++;
                            result.Errors.Add(new ProcessingError(image, ex.Message));
                        }
                    }
                }

                stopwatch.Stop();
                result.TotalProcessingTime = stopwatch.Elapsed;

                _logger.LogInformation($"Batch processing completed: {result.SuccessfulProcesses} successful, {result.FailedProcesses} failed");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch processing failed");
                throw new ImageProcessingException("Batch processing failed", ex);
            }
        }

        /// <summary>
        /// Apply color correction to an image;
        /// </summary>
        public async Task<ProcessedImage> ApplyColorCorrectionAsync(Image input, ColorCorrectionSettings settings)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug("Applying color correction");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                Bitmap processedBitmap;

                lock (_processingLock)
                {
                    using var sourceBitmap = new Bitmap(input);
                    processedBitmap = _colorCorrector.CorrectColors(sourceBitmap, settings);
                }

                stopwatch.Stop();

                var result = new ProcessedImage(processedBitmap, new ProcessingStatistics;
                {
                    ProcessingTime = stopwatch.Elapsed,
                    Operation = "ColorCorrection"
                });

                _logger.LogDebug($"Color correction applied in {stopwatch.ElapsedMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Color correction failed");
                throw new ImageProcessingException("Color correction failed", ex);
            }
        }

        /// <summary>
        /// Resize image with high-quality interpolation;
        /// </summary>
        public async Task<ProcessedImage> ResizeAsync(Image input, Size newSize, bool maintainAspectRatio = true)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug($"Resizing image from {input.Size} to {newSize}");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                Size finalSize = maintainAspectRatio ?
                    CalculateAspectRatioSize(input.Size, newSize) : newSize;

                Bitmap resizedImage;

                if (_useGpuAcceleration)
                {
                    resizedImage = await ResizeGpuAsync(input, finalSize);
                }
                else;
                {
                    resizedImage = await ResizeCpuAsync(input, finalSize);
                }

                stopwatch.Stop();

                var result = new ProcessedImage(resizedImage, new ProcessingStatistics;
                {
                    ProcessingTime = stopwatch.Elapsed,
                    Operation = "Resize",
                    OriginalSize = input.Size,
                    NewSize = finalSize;
                });

                _logger.LogDebug($"Image resized in {stopwatch.ElapsedMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Image resize failed");
                throw new ImageProcessingException("Image resize failed", ex);
            }
        }

        /// <summary>
        /// Optimize image for web (compression, format conversion, metadata stripping)
        /// </summary>
        public async Task<ProcessedImage> OptimizeForWebAsync(Image input, WebOptimizationSettings settings)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug("Optimizing image for web");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var optimizationTasks = new List<Task>();
                Image currentImage = input;

                // Apply optimizations based on settings;
                if (settings.ResizeToMaxWidth > 0)
                {
                    var maxSize = new Size(settings.ResizeToMaxWidth,
                        (int)(input.Height * ((double)settings.ResizeToMaxWidth / input.Width)));
                    currentImage = (await ResizeAsync(currentImage, maxSize)).Image;
                }

                if (settings.ConvertToFormat.HasValue)
                {
                    currentImage = await ConvertFormatAsync(currentImage, settings.ConvertToFormat.Value);
                }

                if (settings.StripMetadata)
                {
                    currentImage = StripMetadata(currentImage);
                }

                if (settings.Compress)
                {
                    currentImage = await CompressImageAsync(currentImage, settings.CompressionLevel);
                }

                stopwatch.Stop();

                var result = new ProcessedImage(currentImage, new ProcessingStatistics;
                {
                    ProcessingTime = stopwatch.Elapsed,
                    Operation = "WebOptimization",
                    CompressionRatio = CalculateCompressionRatio(input, currentImage)
                });

                _logger.LogInformation($"Web optimization completed: {result.Statistics.CompressionRatio:P0} compression");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Web optimization failed");
                throw new ImageProcessingException("Web optimization failed", ex);
            }
        }

        /// <summary>
        /// Apply convolutional filter (blur, sharpen, edge detection, etc.)
        /// </summary>
        public async Task<ProcessedImage> ApplyConvolutionAsync(Image input, ConvolutionKernel kernel)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug($"Applying convolution kernel: {kernel.Name}");

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                Bitmap resultBitmap;

                if (_useGpuAcceleration && kernel.SupportsGpu)
                {
                    resultBitmap = await ApplyConvolutionGpuAsync(input, kernel);
                }
                else;
                {
                    resultBitmap = await ApplyConvolutionCpuAsync(input, kernel);
                }

                stopwatch.Stop();

                var result = new ProcessedImage(resultBitmap, new ProcessingStatistics;
                {
                    ProcessingTime = stopwatch.Elapsed,
                    Operation = $"Convolution-{kernel.Name}"
                });

                _logger.LogDebug($"Convolution applied in {stopwatch.ElapsedMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Convolution failed: {kernel.Name}");
                throw new ImageProcessingException($"Convolution failed: {kernel.Name}", ex);
            }
        }

        /// <summary>
        /// Extract image metadata;
        /// </summary>
        public async Task<ImageMetadata> ExtractMetadataAsync(Image image)
        {
            try
            {
                _logger.LogDebug("Extracting image metadata");

                return await Task.Run(() =>
                {
                    var metadata = new ImageMetadata;
                    {
                        Format = image.RawFormat.ToString(),
                        Size = image.Size,
                        HorizontalResolution = image.HorizontalResolution,
                        VerticalResolution = image.VerticalResolution,
                        PixelFormat = image.PixelFormat.ToString()
                    };

                    // Extract EXIF data if available;
                    if (image.PropertyIdList != null && image.PropertyIdList.Length > 0)
                    {
                        metadata.ExifData = ExtractExifData(image);
                    }

                    return metadata;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metadata extraction failed");
                throw new ImageProcessingException("Metadata extraction failed", ex);
            }
        }

        /// <summary>
        /// Convert image to different format;
        /// </summary>
        public async Task<Image> ConvertFormatAsync(Image input, ImageFormat targetFormat,
            ConversionQuality quality = ConversionQuality.High)
        {
            ValidateImage(input);

            try
            {
                _logger.LogDebug($"Converting image format to {targetFormat}");

                return await Task.Run(() =>
                {
                    using var memoryStream = new MemoryStream();

                    // Configure encoder parameters based on format and quality;
                    var encoderParameters = GetEncoderParameters(targetFormat, quality);

                    // Get encoder;
                    var encoder = GetEncoder(targetFormat);

                    // Save to stream;
                    input.Save(memoryStream, encoder, encoderParameters);

                    // Load from stream;
                    memoryStream.Position = 0;
                    return Image.FromStream(memoryStream);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Format conversion failed to {targetFormat}");
                throw new ImageProcessingException($"Format conversion failed to {targetFormat}", ex);
            }
        }

        /// <summary>
        /// Get processor statistics and performance metrics;
        /// </summary>
        public ProcessorStatistics GetStatistics()
        {
            return new ProcessorStatistics;
            {
                ProcessingMode = _currentMode,
                UseGpuAcceleration = _useGpuAcceleration,
                MaxParallelism = _parallelManager.MaxDegreeOfParallelism,
                OutputFormat = _outputFormat,
                FilterEngineStats = _filterEngine.GetStatistics(),
                ColorCorrectorStats = _colorCorrector.GetStatistics()
            };
        }

        /// <summary>
        /// Clean up resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            lock (_processingLock)
            {
                try
                {
                    _filterEngine?.Dispose();
                    _colorCorrector?.Dispose();
                    _optimizationEngine?.Dispose();
                    _parallelManager?.Dispose();

                    _isDisposed = true;
                    _logger.LogInformation("ImageProcessor disposed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing ImageProcessor");
                }
            }
        }

        #region Private Methods;

        private void ValidateImage(Image image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            if (image.Width <= 0 || image.Height <= 0)
                throw new ArgumentException("Invalid image dimensions");

            // Check if image dimensions are within limits;
            if (image.Width > 16384 || image.Height > 16384)
                throw new ImageProcessingException($"Image dimensions too large: {image.Width}x{image.Height}");
        }

        private bool IsGpuAvailable()
        {
            try
            {
                // Check for GPU availability;
                // This is a simplified check - in production, use proper GPU detection;
                return false; // Temporarily disabled;
            }
            catch
            {
                return false;
            }
        }

        private void InitializeGpuAcceleration()
        {
            try
            {
                // Initialize GPU processing if available;
                // This would involve initializing CUDA/OpenCL contexts;
                _logger.LogInformation("GPU acceleration initialized");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GPU acceleration initialization failed, falling back to CPU");
                _useGpuAcceleration = false;
            }
        }

        private async Task<ProcessedImage> ApplyFilterCpuAsync(Image input, ImageFilter filter, FilterParameters parameters)
        {
            return await Task.Run(() =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                using var bitmap = new Bitmap(input);
                var resultBitmap = _filterEngine.ApplyFilter(bitmap, filter, parameters);

                stopwatch.Stop();

                return new ProcessedImage(resultBitmap, new ProcessingStatistics;
                {
                    ProcessingTime = stopwatch.Elapsed,
                    Operation = filter.Name,
                    HardwareAccelerated = false;
                });
            });
        }

        private async Task<ProcessedImage> ApplyFilterGpuAsync(Image input, ImageFilter filter, FilterParameters parameters)
        {
            // GPU implementation would go here;
            // For now, fall back to CPU;
            _logger.LogWarning("GPU filter not implemented, falling back to CPU");
            return await ApplyFilterCpuAsync(input, filter, parameters);
        }

        private async Task<Bitmap> ResizeCpuAsync(Image input, Size newSize)
        {
            return await Task.Run(() =>
            {
                var bitmap = new Bitmap(newSize.Width, newSize.Height, PixelFormat.Format32bppArgb);

                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.InterpolationMode = _qualitySettings.Interpolation;
                    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                    graphics.DrawImage(input, 0, 0, newSize.Width, newSize.Height);
                }

                return bitmap;
            });
        }

        private async Task<Bitmap> ResizeGpuAsync(Image input, Size newSize)
        {
            // GPU resize implementation;
            // For now, fall back to CPU;
            return await ResizeCpuAsync(input, newSize);
        }

        private async Task<Bitmap> ApplyConvolutionCpuAsync(Image input, ConvolutionKernel kernel)
        {
            return await Task.Run(() =>
            {
                using var sourceBitmap = new Bitmap(input);
                return ConvolutionEngine.ApplyKernel(sourceBitmap, kernel);
            });
        }

        private async Task<Bitmap> ApplyConvolutionGpuAsync(Image input, ConvolutionKernel kernel)
        {
            // GPU convolution implementation;
            // For now, fall back to CPU;
            return await ApplyConvolutionCpuAsync(input, kernel);
        }

        private Size CalculateAspectRatioSize(Size original, Size target)
        {
            double ratio = Math.Min((double)target.Width / original.Width,
                                   (double)target.Height / original.Height);

            return new Size(
                (int)(original.Width * ratio),
                (int)(original.Height * ratio)
            );
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            return codecs.FirstOrDefault(codec => codec.FormatID == format.Guid);
        }

        private EncoderParameters GetEncoderParameters(ImageFormat format, ConversionQuality quality)
        {
            var parameters = new EncoderParameters(1);

            if (format == ImageFormat.Jpeg)
            {
                parameters.Param[0] = new EncoderParameter(
                    Encoder.Quality,
                    (long)(quality == ConversionQuality.High ? 90L :
                           quality == ConversionQuality.Medium ? 75L : 50L)
                );
            }

            return parameters;
        }

        private Dictionary<string, string> ExtractExifData(Image image)
        {
            var exifData = new Dictionary<string, string>();

            foreach (var propId in image.PropertyIdList)
            {
                var property = image.GetPropertyItem(propId);
                if (property != null)
                {
                    // Convert property value based on type;
                    string value = ConvertExifValue(property);
                    exifData.Add($"0x{propId:X4}", value);
                }
            }

            return exifData;
        }

        private string ConvertExifValue(PropertyItem property)
        {
            // Simplified conversion - real implementation would handle all EXIF types;
            if (property.Type == 2) // ASCII;
            {
                return System.Text.Encoding.ASCII.GetString(property.Value).Trim('\0');
            }

            return $"Binary data, length: {property.Len}";
        }

        private async Task<Image> CompressImageAsync(Image input, CompressionLevel level)
        {
            return await Task.Run(() =>
            {
                using var memoryStream = new MemoryStream();

                var encoder = GetEncoder(ImageFormat.Jpeg);
                var parameters = new EncoderParameters(1);

                long quality = level switch;
                {
                    CompressionLevel.Maximum => 100L,
                    CompressionLevel.High => 85L,
                    CompressionLevel.Medium => 70L,
                    CompressionLevel.Low => 55L,
                    _ => 75L;
                };

                parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                input.Save(memoryStream, encoder, parameters);

                memoryStream.Position = 0;
                return Image.FromStream(memoryStream);
            });
        }

        private Image StripMetadata(Image input)
        {
            // Create a new image without metadata;
            var cleanBitmap = new Bitmap(input.Width, input.Height);

            using (var graphics = Graphics.FromImage(cleanBitmap))
            {
                graphics.DrawImage(input, 0, 0, input.Width, input.Height);
            }

            return cleanBitmap;
        }

        private double CalculateCompressionRatio(Image original, Image compressed)
        {
            using var originalStream = new MemoryStream();
            using var compressedStream = new MemoryStream();

            original.Save(originalStream, ImageFormat.Png);
            compressed.Save(compressedStream, ImageFormat.Png);

            return 1.0 - ((double)compressedStream.Length / originalStream.Length);
        }

        private FilterParameters GetDefaultParameters(ImageFilter filter)
        {
            return filter.DefaultParameters ?? new FilterParameters();
        }

        #endregion;

        #region Nested Types;

        public class ProcessedImage : IDisposable
        {
            public Image Image { get; }
            public ProcessingStatistics Statistics { get; }

            public ProcessedImage(Image image, ProcessingStatistics statistics)
            {
                Image = image ?? throw new ArgumentNullException(nameof(image));
                Statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
            }

            public void Dispose()
            {
                Image?.Dispose();
            }
        }

        public class ProcessingStatistics;
        {
            public TimeSpan ProcessingTime { get; set; }
            public string Operation { get; set; }
            public bool HardwareAccelerated { get; set; }
            public Size? OriginalSize { get; set; }
            public Size? NewSize { get; set; }
            public double? CompressionRatio { get; set; }
            public Dictionary<string, TimeSpan> FilterTimes { get; } = new Dictionary<string, TimeSpan>();

            public void AddFilterResult(string filterName, TimeSpan time)
            {
                FilterTimes[filterName] = time;
            }

            public override string ToString()
            {
                return $"Operation: {Operation}, Time: {ProcessingTime.TotalMilliseconds}ms, GPU: {HardwareAccelerated}";
            }
        }

        public class BatchProcessingResult;
        {
            public int SuccessfulProcesses { get; set; }
            public int FailedProcesses { get; set; }
            public List<ProcessedImage> ProcessedImages { get; } = new List<ProcessedImage>();
            public List<ProcessingError> Errors { get; } = new List<ProcessingError>();
            public TimeSpan TotalProcessingTime { get; set; }

            public double SuccessRate => TotalProcesses > 0 ? (double)SuccessfulProcesses / TotalProcesses : 0;
            public int TotalProcesses => SuccessfulProcesses + FailedProcesses;
        }

        public class ProcessingError;
        {
            public Image Image { get; }
            public string ErrorMessage { get; }

            public ProcessingError(Image image, string errorMessage)
            {
                Image = image;
                ErrorMessage = errorMessage;
            }
        }

        public class ProcessorStatistics;
        {
            public ProcessingMode ProcessingMode { get; set; }
            public bool UseGpuAcceleration { get; set; }
            public int MaxParallelism { get; set; }
            public ImageFormat OutputFormat { get; set; }
            public FilterEngineStatistics FilterEngineStats { get; set; }
            public ColorCorrectorStatistics ColorCorrectorStats { get; set; }
        }

        public class ImageProcessingException : Exception
        {
            public ImageProcessingException(string message) : base(message) { }
            public ImageProcessingException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }

    /// <summary>
    /// Base class for image filters;
    /// </summary>
    public abstract class ImageFilter;
    {
        public string Name { get; protected set; }
        public string Description { get; protected set; }
        public bool SupportsGpu { get; protected set; }
        public FilterParameters DefaultParameters { get; protected set; }

        public abstract Bitmap Apply(Bitmap input, FilterParameters parameters);
    }

    /// <summary>
    /// Filter parameters container;
    /// </summary>
    public class FilterParameters;
    {
        private readonly Dictionary<string, object> _parameters = new Dictionary<string, object>();

        public T GetParameter<T>(string name, T defaultValue = default)
        {
            return _parameters.TryGetValue(name, out var value) ? (T)value : defaultValue;
        }

        public void SetParameter(string name, object value)
        {
            _parameters[name] = value;
        }
    }

    /// <summary>
    /// Color correction settings;
    /// </summary>
    public class ColorCorrectionSettings;
    {
        public float Brightness { get; set; } = 1.0f;
        public float Contrast { get; set; } = 1.0f;
        public float Saturation { get; set; } = 1.0f;
        public float Gamma { get; set; } = 1.0f;
        public ColorBalance Balance { get; set; } = new ColorBalance();
        public bool AutoWhiteBalance { get; set; } = true;
        public bool AutoExposure { get; set; } = false;
    }

    /// <summary>
    /// Web optimization settings;
    /// </summary>
    public class WebOptimizationSettings;
    {
        public int ResizeToMaxWidth { get; set; } = 0;
        public ImageFormat? ConvertToFormat { get; set; } = ImageFormat.Jpeg;
        public bool StripMetadata { get; set; } = true;
        public bool Compress { get; set; } = true;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.High;
    }

    /// <summary>
    /// Convolution kernel for image processing;
    /// </summary>
    public class ConvolutionKernel;
    {
        public string Name { get; set; }
        public float[,] Matrix { get; set; }
        public float Divisor { get; set; } = 1.0f;
        public float Offset { get; set; } = 0.0f;
        public bool SupportsGpu { get; set; } = true;
    }

    /// <summary>
    /// Image metadata container;
    /// </summary>
    public class ImageMetadata;
    {
        public string Format { get; set; }
        public Size Size { get; set; }
        public float HorizontalResolution { get; set; }
        public float VerticalResolution { get; set; }
        public string PixelFormat { get; set; }
        public Dictionary<string, string> ExifData { get; set; }
    }

    /// <summary>
    /// Compression levels;
    /// </summary>
    public enum CompressionLevel;
    {
        Maximum,
        High,
        Medium,
        Low,
        Optimal;
    }

    /// <summary>
    /// Conversion quality levels;
    /// </summary>
    public enum ConversionQuality;
    {
        High,
        Medium,
        Low;
    }

    /// <summary>
    /// Color profiles;
    /// </summary>
    public enum ColorProfile;
    {
        SRGB,
        AdobeRGB,
        ProPhotoRGB,
        CMYK;
    }

    /// <summary>
    /// Interpolation modes;
    /// </summary>
    public enum InterpolationMode;
    {
        NearestNeighbor,
        Low,
        High,
        Bilinear,
        Bicubic,
        HighQualityBilinear,
        HighQualityBicubic;
    }
}
