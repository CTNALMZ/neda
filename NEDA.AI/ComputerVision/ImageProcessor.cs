using NEDA.AI.MachineLearning;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Advanced image processing engine with AI-powered enhancements
    /// </summary>
    public class ImageProcessor : IDisposable
    {
        private readonly ILogger _logger;
        private readonly RecoveryEngine _recoveryEngine;
        private bool _disposed = false;

        // Image processing configurations
        public ImageProcessorConfig Config { get; private set; }

        // AI model references for advanced processing
        private readonly MLModel _enhancementModel;
        private readonly MLModel _objectDetectionModel;

        public event EventHandler<ImageProcessingEventArgs> ProcessingCompleted;
        public event EventHandler<ImageProcessingErrorEventArgs> ProcessingError;

        public ImageProcessor(ILogger logger = null)
        {
            _logger = logger ?? LogManager.GetLogger("ImageProcessor");
            _recoveryEngine = new RecoveryEngine(_logger);
            Config = new ImageProcessorConfig();

            InitializeModels();
            SetupRecoveryStrategies();
        }

        public ImageProcessor(ImageProcessorConfig config, ILogger logger = null) : this(logger)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        private void InitializeModels()
        {
            try
            {
                // Initialize AI models for image processing
                _enhancementModel = ModelManager.LoadModel("image_enhancement_v1");
                _objectDetectionModel = ModelManager.LoadModel("object_detection_v2");

                _logger.Info("AI models initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize AI models: {ex.Message}");
                throw new ImageProcessingException("AI model initialization failed", ex);
            }
        }

        private void SetupRecoveryStrategies()
        {
            _recoveryEngine.AddStrategy<OutOfMemoryException>(new RetryStrategy(3, TimeSpan.FromSeconds(1)));
            _recoveryEngine.AddStrategy<FileNotFoundException>(new FallbackStrategy(UseDefaultImage));
        }

        /// <summary>
        /// Enhanced image processing with AI-powered optimizations
        /// </summary>
        public async Task<Bitmap> ProcessImageAsync(string imagePath, ImageOperation operation)
        {
            return await _recoveryEngine.ExecuteWithRecoveryAsync(async () =>
            {
                ValidateImagePath(imagePath);

                using (var originalImage = await LoadImageAsync(imagePath))
                {
                    var processedImage = await ProcessImageInternalAsync(originalImage, operation);

                    ProcessingCompleted?.Invoke(this,
                        new ImageProcessingEventArgs(operation, imagePath, processedImage));

                    return processedImage;
                }
            });
        }

        /// <summary>
        /// Process image from stream with advanced AI enhancements
        /// </summary>
        public async Task<Bitmap> ProcessImageAsync(Stream imageStream, ImageOperation operation)
        {
            return await _recoveryEngine.ExecuteWithRecoveryAsync(async () =>
            {
                using (var originalImage = await LoadImageFromStreamAsync(imageStream))
                {
                    return await ProcessImageInternalAsync(originalImage, operation);
                }
            });
        }

        /// <summary>
        /// Batch process multiple images with parallel execution
        /// </summary>
        public async Task<BatchProcessingResult> ProcessImagesBatchAsync(
            string[] imagePaths,
            ImageOperation operation,
            ParallelOptions parallelOptions = null)
        {
            var result = new BatchProcessingResult();
            parallelOptions ??= new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

            await Task.Run(() =>
            {
                Parallel.ForEach(imagePaths, parallelOptions, (imagePath, state, index) =>
                {
                    try
                    {
                        var processedImage = ProcessImageAsync(imagePath, operation).GetAwaiter().GetResult();
                        result.SuccessfulProcesses++;
                        result.ProcessedImages[imagePath] = processedImage;
                    }
                    catch (Exception ex)
                    {
                        result.FailedProcesses++;
                        result.Errors[imagePath] = ex.Message;
                        _logger.Warning($"Failed to process image {imagePath}: {ex.Message}");
                    }
                });
            });

            return result;
        }

        /// <summary>
        /// AI-powered image enhancement with neural networks
        /// </summary>
        public async Task<Bitmap> EnhanceImageAsync(Bitmap image, EnhancementType enhancementType)
        {
            return await Task.Run(() =>
            {
                try
                {
                    _logger.Info($"Applying {enhancementType} enhancement to image");

                    switch (enhancementType)
                    {
                        case EnhancementType.SuperResolution:
                            return ApplySuperResolution(image);
                        case EnhancementType.NoiseReduction:
                            return ApplyNoiseReduction(image);
                        case EnhancementType.ColorCorrection:
                            return ApplyColorCorrection(image);
                        case EnhancementType.Sharpness:
                            return ApplySharpnessEnhancement(image);
                        case EnhancementType.Contrast:
                            return ApplyContrastEnhancement(image);
                        default:
                            return image;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Image enhancement failed: {ex.Message}");
                    throw new ImageProcessingException($"Enhancement failed: {enhancementType}", ex);
                }
            });
        }

        /// <summary>
        /// Advanced object detection with AI
        /// </summary>
        public async Task<ObjectDetectionResult> DetectObjectsAsync(Bitmap image, float confidenceThreshold = 0.7f)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var inputTensor = ConvertImageToTensor(image);
                    var predictions = _objectDetectionModel.Predict(inputTensor);

                    return ProcessDetections(predictions, confidenceThreshold);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Object detection failed: {ex.Message}");
                    throw new ImageProcessingException("Object detection failed", ex);
                }
            });
        }

        /// <summary>
        /// Apply multiple filters and effects in sequence
        /// </summary>
        public async Task<Bitmap> ApplyFilterPipelineAsync(Bitmap image, params ImageFilter[] filters)
        {
            return await Task.Run(() =>
            {
                Bitmap currentImage = (Bitmap)image.Clone();

                try
                {
                    foreach (var filter in filters)
                    {
                        currentImage = ApplyFilter(currentImage, filter);
                    }

                    return currentImage;
                }
                catch (Exception ex)
                {
                    currentImage?.Dispose();
                    _logger.Error($"Filter pipeline failed: {ex.Message}");
                    throw new ImageProcessingException("Filter pipeline execution failed", ex);
                }
            });
        }

        // Core image processing implementation
        private async Task<Bitmap> ProcessImageInternalAsync(Bitmap image, ImageOperation operation)
        {
            return await Task.Run(() =>
            {
                try
                {
                    _logger.Debug($"Processing image with operation: {operation}");

                    switch (operation)
                    {
                        case ImageOperation.Resize:
                            return ResizeImage(image, Config.TargetWidth, Config.TargetHeight);
                        case ImageOperation.Crop:
                            return CropImage(image, Config.CropRegion);
                        case ImageOperation.Rotate:
                            return RotateImage(image, Config.RotationAngle);
                        case ImageOperation.Flip:
                            return FlipImage(image, Config.FlipMode);
                        case ImageOperation.Grayscale:
                            return ConvertToGrayscale(image);
                        case ImageOperation.Sepia:
                            return ApplySepiaTone(image);
                        case ImageOperation.Blur:
                            return ApplyGaussianBlur(image, Config.BlurRadius);
                        case ImageOperation.Sharpen:
                            return ApplySharpen(image);
                        case ImageOperation.EdgeDetection:
                            return DetectEdges(image);
                        case ImageOperation.Enhance:
                            return EnhanceImageAsync(image, Config.EnhancementType).GetAwaiter().GetResult();
                        default:
                            return image;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Image processing failed for operation {operation}: {ex.Message}");
                    throw new ImageProcessingException($"Operation failed: {operation}", ex);
                }
            });
        }

        #region Core Image Processing Methods

        private Bitmap ResizeImage(Bitmap image, int width, int height)
        {
            var resizedImage = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(resizedImage))
            {
                graphics.InterpolationMode = Config.InterpolationMode;
                graphics.SmoothingMode = Config.SmoothingMode;
                graphics.PixelOffsetMode = Config.PixelOffsetMode;
                graphics.CompositingQuality = Config.CompositingQuality;

                graphics.DrawImage(image, 0, 0, width, height);
            }
            return resizedImage;
        }

        private Bitmap CropImage(Bitmap image, Rectangle cropRegion)
        {
            ValidateCropRegion(image, cropRegion);

            var croppedImage = new Bitmap(cropRegion.Width, cropRegion.Height);
            using (var graphics = Graphics.FromImage(croppedImage))
            {
                graphics.DrawImage(image, new Rectangle(0, 0, cropRegion.Width, cropRegion.Height),
                                 cropRegion, GraphicsUnit.Pixel);
            }
            return croppedImage;
        }

        private Bitmap RotateImage(Bitmap image, float angle)
        {
            var rotatedImage = new Bitmap(image.Width, image.Height);
            using (var graphics = Graphics.FromImage(rotatedImage))
            {
                graphics.TranslateTransform((float)image.Width / 2, (float)image.Height / 2);
                graphics.RotateTransform(angle);
                graphics.TranslateTransform(-(float)image.Width / 2, -(float)image.Height / 2);
                graphics.DrawImage(image, new Point(0, 0));
            }
            return rotatedImage;
        }

        private unsafe Bitmap ConvertToGrayscale(Bitmap image)
        {
            var grayscaleImage = new Bitmap(image.Width, image.Height);

            var rect = new Rectangle(0, 0, image.Width, image.Height);
            var sourceData = image.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var destData = grayscaleImage.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            unsafe
            {
                var sourcePtr = (byte*)sourceData.Scan0;
                var destPtr = (byte*)destData.Scan0;

                for (int i = 0; i < sourceData.Height; i++)
                {
                    for (int j = 0; j < sourceData.Width; j++)
                    {
                        int index = i * sourceData.Stride + j * 4;

                        byte blue = sourcePtr[index];
                        byte green = sourcePtr[index + 1];
                        byte red = sourcePtr[index + 2];
                        byte alpha = sourcePtr[index + 3];

                        byte gray = (byte)((red * 0.3) + (green * 0.59) + (blue * 0.11));

                        destPtr[index] = gray;     // Blue
                        destPtr[index + 1] = gray; // Green
                        destPtr[index + 2] = gray; // Red
                        destPtr[index + 3] = alpha; // Alpha
                    }
                }
            }

            image.UnlockBits(sourceData);
            grayscaleImage.UnlockBits(destData);

            return grayscaleImage;
        }

        private Bitmap ApplyGaussianBlur(Bitmap image, int radius)
        {
            // Gaussian blur implementation using convolution
            var kernel = CreateGaussianKernel(radius);
            return ApplyConvolution(image, kernel);
        }

        private Bitmap ApplySepiaTone(Bitmap image)
        {
            var sepiaImage = new Bitmap(image.Width, image.Height);

            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    var pixel = image.GetPixel(x, y);

                    int r = pixel.R;
                    int g = pixel.G;
                    int b = pixel.B;

                    int tr = (int)(0.393 * r + 0.769 * g + 0.189 * b);
                    int tg = (int)(0.349 * r + 0.686 * g + 0.168 * b);
                    int tb = (int)(0.272 * r + 0.534 * g + 0.131 * b);

                    tr = Math.Min(255, tr);
                    tg = Math.Min(255, tg);
                    tb = Math.Min(255, tb);

                    sepiaImage.SetPixel(x, y, Color.FromArgb(pixel.A, tr, tg, tb));
                }
            }

            return sepiaImage;
        }

        #endregion

        #region AI-Powered Enhancement Methods

        private Bitmap ApplySuperResolution(Bitmap image)
        {
            // Use AI model to enhance image resolution
            var inputTensor = ConvertImageToTensor(image);
            var enhancedTensor = _enhancementModel.Predict(inputTensor);
            return ConvertTensorToImage(enhancedTensor);
        }

        private Bitmap ApplyNoiseReduction(Bitmap image)
        {
            // AI-based noise reduction
            var inputTensor = ConvertImageToTensor(image);
            var denoisedTensor = _enhancementModel.Predict(inputTensor);
            return ConvertTensorToImage(denoisedTensor);
        }

        private Bitmap ApplyColorCorrection(Bitmap image)
        {
            // AI-powered color correction
            var inputTensor = ConvertImageToTensor(image);
            var correctedTensor = _enhancementModel.Predict(inputTensor);
            return ConvertTensorToImage(correctedTensor);
        }

        #endregion

        #region Utility Methods

        private async Task<Bitmap> LoadImageAsync(string imagePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                    {
                        return new Bitmap(stream);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to load image from {imagePath}: {ex.Message}");
                    throw new ImageProcessingException($"Image loading failed: {imagePath}", ex);
                }
            });
        }

        private async Task<Bitmap> LoadImageFromStreamAsync(Stream stream)
        {
            return await Task.Run(() =>
            {
                try
                {
                    return new Bitmap(stream);
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to load image from stream");
                    throw new ImageProcessingException("Stream image loading failed", ex);
                }
            });
        }

        private void ValidateImagePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                throw new ArgumentException("Image path cannot be null or empty", nameof(imagePath));

            if (!File.Exists(imagePath))
                throw new FileNotFoundException($"Image file not found: {imagePath}");

            var extension = Path.GetExtension(imagePath).ToLower();
            var supportedFormats = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff" };

            if (!supportedFormats.Contains(extension))
                throw new NotSupportedException($"Image format not supported: {extension}");
        }

        private void ValidateCropRegion(Bitmap image, Rectangle cropRegion)
        {
            if (cropRegion.X < 0 || cropRegion.Y < 0 ||
                cropRegion.Right > image.Width || cropRegion.Bottom > image.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(cropRegion),
                    "Crop region is outside image boundaries");
            }
        }

        private float[,] CreateGaussianKernel(int radius)
        {
            int size = radius * 2 + 1;
            var kernel = new float[size, size];
            float sigma = radius / 3.0f;
            float sum = 0.0f;

            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -radius; y <= radius; y++)
                {
                    float value = (float)(1.0 / (2.0 * Math.PI * sigma * sigma) *
                                         Math.Exp(-(x * x + y * y) / (2 * sigma * sigma)));
                    kernel[x + radius, y + radius] = value;
                    sum += value;
                }
            }

            // Normalize kernel
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    kernel[i, j] /= sum;
                }
            }

            return kernel;
        }

        private Bitmap ApplyConvolution(Bitmap image, float[,] kernel)
        {
            // Convolution implementation for image filters
            var result = new Bitmap(image.Width, image.Height);
            int kernelSize = kernel.GetLength(0);
            int radius = kernelSize / 2;

            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    float r = 0, g = 0, b = 0;

                    for (int i = -radius; i <= radius; i++)
                    {
                        for (int j = -radius; j <= radius; j++)
                        {
                            int px = Math.Clamp(x + i, 0, image.Width - 1);
                            int py = Math.Clamp(y + j, 0, image.Height - 1);

                            var pixel = image.GetPixel(px, py);
                            float weight = kernel[i + radius, j + radius];

                            r += pixel.R * weight;
                            g += pixel.G * weight;
                            b += pixel.B * weight;
                        }
                    }

                    int red = Math.Clamp((int)r, 0, 255);
                    int green = Math.Clamp((int)g, 0, 255);
                    int blue = Math.Clamp((int)b, 0, 255);

                    result.SetPixel(x, y, Color.FromArgb(red, green, blue));
                }
            }

            return result;
        }

        private Bitmap UseDefaultImage()
        {
            // Fallback default image
            var defaultImage = new Bitmap(100, 100);
            using (var g = Graphics.FromImage(defaultImage))
            {
                g.Clear(Color.LightGray);
                g.DrawString("Default", new Font("Arial", 12), Brushes.Black, 10, 40);
            }
            return defaultImage;
        }

        #endregion

        #region Tensor Conversion Methods (AI Integration)

        private float[,,,] ConvertImageToTensor(Bitmap image)
        {
            // Convert bitmap to tensor format for AI model input
            var tensor = new float[1, image.Height, image.Width, 3]; // Batch, Height, Width, Channels

            for (int y = 0; y < image.Height; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    var pixel = image.GetPixel(x, y);
                    tensor[0, y, x, 0] = pixel.R / 255.0f; // Red channel
                    tensor[0, y, x, 1] = pixel.G / 255.0f; // Green channel
                    tensor[0, y, x, 2] = pixel.B / 255.0f; // Blue channel
                }
            }

            return tensor;
        }

        private Bitmap ConvertTensorToImage(float[,,,] tensor)
        {
            // Convert tensor back to bitmap
            int height = tensor.GetLength(1);
            int width = tensor.GetLength(2);
            var image = new Bitmap(width, height);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int r = (int)(tensor[0, y, x, 0] * 255);
                    int g = (int)(tensor[0, y, x, 1] * 255);
                    int b = (int)(tensor[0, y, x, 2] * 255);

                    r = Math.Clamp(r, 0, 255);
                    g = Math.Clamp(g, 0, 255);
                    b = Math.Clamp(b, 0, 255);

                    image.SetPixel(x, y, Color.FromArgb(r, g, b));
                }
            }

            return image;
        }

        private ObjectDetectionResult ProcessDetections(float[,,] predictions, float confidenceThreshold)
        {
            var result = new ObjectDetectionResult();
            int numDetections = predictions.GetLength(0);

            for (int i = 0; i < numDetections; i++)
            {
                float confidence = predictions[i, 4, 0];

                if (confidence > confidenceThreshold)
                {
                    var detection = new ObjectDetection
                    {
                        Confidence = confidence,
                        ClassId = (int)predictions[i, 5, 0],
                        BoundingBox = new Rectangle(
                            (int)predictions[i, 0, 0],
                            (int)predictions[i, 1, 0],
                            (int)(predictions[i, 2, 0] - predictions[i, 0, 0]),
                            (int)(predictions[i, 3, 0] - predictions[i, 1, 0])
                        )
                    };

                    result.Detections.Add(detection);
                }
            }

            return result;
        }

        #endregion

        #region Additional Image Processing Methods

        private Bitmap ApplySharpen(Bitmap image)
        {
            float[,] sharpenKernel = {
                { 0, -1, 0 },
                { -1, 5, -1 },
                { 0, -1, 0 }
            };

            return ApplyConvolution(image, sharpenKernel);
        }

        private Bitmap DetectEdges(Bitmap image)
        {
            float[,] edgeKernel = {
                { -1, -1, -1 },
                { -1,  8, -1 },
                { -1, -1, -1 }
            };

            return ApplyConvolution(image, edgeKernel);
        }

        private Bitmap ApplySharpnessEnhancement(Bitmap image)
        {
            // AI-powered sharpness enhancement
            return EnhanceImageAsync(image, EnhancementType.Sharpness).GetAwaiter().GetResult();
        }

        private Bitmap ApplyContrastEnhancement(Bitmap image)
        {
            // AI-powered contrast enhancement
            return EnhanceImageAsync(image, EnhancementType.Contrast).GetAwaiter().GetResult();
        }

        private Bitmap FlipImage(Bitmap image, FlipMode flipMode)
        {
            switch (flipMode)
            {
                case FlipMode.Horizontal:
                    image.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    break;
                case FlipMode.Vertical:
                    image.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    break;
                case FlipMode.Both:
                    image.RotateFlip(RotateFlipType.RotateNoneFlipXY);
                    break;
                default:
                    break;
            }
            return image;
        }

        private Bitmap ApplyFilter(Bitmap image, ImageFilter filter)
        {
            switch (filter.Type)
            {
                case FilterType.Brightness:
                    return AdjustBrightness(image, filter.Value);
                case FilterType.Contrast:
                    return AdjustContrast(image, filter.Value);
                case FilterType.Saturation:
                    return AdjustSaturation(image, filter.Value);
                case FilterType.Hue:
                    return AdjustHue(image, filter.Value);
                default:
                    return image;
            }
        }

        private Bitmap AdjustBrightness(Bitmap image, float value)
        {
            // Brightness adjustment implementation
            var adjustedImage = new Bitmap(image.Width, image.Height);

            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    var pixel = image.GetPixel(x, y);

                    int r = Math.Clamp((int)(pixel.R * value), 0, 255);
                    int g = Math.Clamp((int)(pixel.G * value), 0, 255);
                    int b = Math.Clamp((int)(pixel.B * value), 0, 255);

                    adjustedImage.SetPixel(x, y, Color.FromArgb(pixel.A, r, g, b));
                }
            }

            return adjustedImage;
        }

        private Bitmap AdjustContrast(Bitmap image, float value)
        {
            // Contrast adjustment implementation
            var adjustedImage = new Bitmap(image.Width, image.Height);
            float factor = (259 * (value + 255)) / (255 * (259 - value));

            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    var pixel = image.GetPixel(x, y);

                    int r = Math.Clamp((int)(factor * (pixel.R - 128) + 128), 0, 255);
                    int g = Math.Clamp((int)(factor * (pixel.G - 128) + 128), 0, 255);
                    int b = Math.Clamp((int)(factor * (pixel.B - 128) + 128), 0, 255);

                    adjustedImage.SetPixel(x, y, Color.FromArgb(pixel.A, r, g, b));
                }
            }

            return adjustedImage;
        }

        private Bitmap AdjustSaturation(Bitmap image, float value)
        {
            // Saturation adjustment implementation
            var adjustedImage = new Bitmap(image.Width, image.Height);

            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    var pixel = image.GetPixel(x, y);
                    var hsl = ColorConverter.RgbToHsl(pixel);
                    hsl.Saturation = Math.Clamp(hsl.Saturation * value, 0, 1);
                    var newColor = ColorConverter.HslToRgb(hsl);

                    adjustedImage.SetPixel(x, y, Color.FromArgb(pixel.A, newColor.R, newColor.G, newColor.B));
                }
            }

            return adjustedImage;
        }

        private Bitmap AdjustHue(Bitmap image, float value)
        {
            // Hue adjustment implementation
            var adjustedImage = new Bitmap(image.Width, image.Height);

            for (int x = 0; x < image.Width; x++)
            {
                for (int y = 0; y < image.Height; y++)
                {
                    var pixel = image.GetPixel(x, y);
                    var hsl = ColorConverter.RgbToHsl(pixel);
                    hsl.Hue = (hsl.Hue + value) % 360;
                    var newColor = ColorConverter.HslToRgb(hsl);

                    adjustedImage.SetPixel(x, y, Color.FromArgb(pixel.A, newColor.R, newColor.G, newColor.B));
                }
            }

            return adjustedImage;
        }

        #endregion

        #region IDisposable Implementation

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
                    _enhancementModel?.Dispose();
                    _objectDetectionModel?.Dispose();
                    _recoveryEngine?.Dispose();
                }
                _disposed = true;
            }
        }

        ~ImageProcessor()
        {
            Dispose(false);
        }

        #endregion
    }

    #region Supporting Classes and Enums

    /// <summary>
    /// Configuration for image processing operations
    /// </summary>
    public class ImageProcessorConfig
    {
        public int TargetWidth { get; set; } = 800;
        public int TargetHeight { get; set; } = 600;
        public Rectangle CropRegion { get; set; } = new Rectangle(0, 0, 100, 100);
        public float RotationAngle { get; set; } = 0f;
        public FlipMode FlipMode { get; set; } = FlipMode.None;
        public int BlurRadius { get; set; } = 3;
        public EnhancementType EnhancementType { get; set; } = EnhancementType.SuperResolution;

        // Graphics quality settings
        public System.Drawing.Drawing2D.InterpolationMode InterpolationMode { get; set; }
            = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        public System.Drawing.Drawing2D.SmoothingMode SmoothingMode { get; set; }
            = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        public System.Drawing.Drawing2D.PixelOffsetMode PixelOffsetMode { get; set; }
            = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        public System.Drawing.Drawing2D.CompositingQuality CompositingQuality { get; set; }
            = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
    }

    /// <summary>
    /// Image processing operations
    /// </summary>
    public enum ImageOperation
    {
        Resize,
        Crop,
        Rotate,
        Flip,
        Grayscale,
        Sepia,
        Blur,
        Sharpen,
        EdgeDetection,
        Enhance
    }

    /// <summary>
    /// Image enhancement types
    /// </summary>
    public enum EnhancementType
    {
        SuperResolution,
        NoiseReduction,
        ColorCorrection,
        Sharpness,
        Contrast
    }

    /// <summary>
    /// Flip modes for image transformation
    /// </summary>
    public enum FlipMode
    {
        None,
        Horizontal,
        Vertical,
        Both
    }

    /// <summary>
    /// Image filter types
    /// </summary>
    public enum FilterType
    {
        Brightness,
        Contrast,
        Saturation,
        Hue
    }

    /// <summary>
    /// Represents an image filter
    /// </summary>
    public class ImageFilter
    {
        public FilterType Type { get; set; }
        public float Value { get; set; }
    }

    /// <summary>
    /// Event arguments for image processing completion
    /// </summary>
    public class ImageProcessingEventArgs : EventArgs
    {
        public ImageOperation Operation { get; }
        public string ImagePath { get; }
        public Bitmap ProcessedImage { get; }

        public ImageProcessingEventArgs(ImageOperation operation, string imagePath, Bitmap processedImage)
        {
            Operation = operation;
            ImagePath = imagePath;
            ProcessedImage = processedImage;
        }
    }

    /// <summary>
    /// Event arguments for image processing errors
    /// </summary>
    public class ImageProcessingErrorEventArgs : EventArgs
    {
        public Exception Exception { get; }
        public string ImagePath { get; }
        public ImageOperation Operation { get; }

        public ImageProcessingErrorEventArgs(Exception exception, string imagePath, ImageOperation operation)
        {
            Exception = exception;
            ImagePath = imagePath;
            Operation = operation;
        }
    }

    /// <summary>
    /// Result of batch image processing
    /// </summary>
    public class BatchProcessingResult
    {
        public int SuccessfulProcesses { get; set; }
        public int FailedProcesses { get; set; }
        public Dictionary<string, Bitmap> ProcessedImages { get; set; } = new Dictionary<string, Bitmap>();
        public Dictionary<string, string> Errors { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Object detection result
    /// </summary>
    public class ObjectDetectionResult
    {
        public List<ObjectDetection> Detections { get; set; } = new List<ObjectDetection>();
        public TimeSpan ProcessingTime { get; set; }
    }

    /// <summary>
    /// Single object detection
    /// </summary>
    public class ObjectDetection
    {
        public Rectangle BoundingBox { get; set; }
        public float Confidence { get; set; }
        public int ClassId { get; set; }
        public string ClassName => GetClassName(ClassId);

        private string GetClassName(int classId)
        {
            return classId switch
            {
                0 => "Person",
                1 => "Vehicle",
                2 => "Animal",
                3 => "Object",
                _ => "Unknown"
            };
        }
    }

    /// <summary>
    /// Custom exception for image processing errors
    /// </summary>
    public class ImageProcessingException : Exception
    {
        public ImageProcessingException(string message) : base(message) { }
        public ImageProcessingException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion
}