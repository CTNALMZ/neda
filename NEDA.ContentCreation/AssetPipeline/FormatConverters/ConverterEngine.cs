using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Reflection;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.ContentCreation.AssetPipeline.Common;

namespace NEDA.ContentCreation.AssetPipeline.FormatConverters;
{
    /// <summary>
    /// Advanced format conversion engine supporting pluggable converters,
    /// batch processing, format detection, and quality optimization;
    /// </summary>
    public class ConverterEngine : IDisposable
    {
        #region Nested Types;

        /// <summary>
        /// Conversion request with source, target, and conversion options;
        /// </summary>
        public class ConversionRequest;
        {
            /// <summary>
            /// Unique request identifier;
            /// </summary>
            public Guid Id { get; }

            /// <summary>
            /// Source file path or data;
            /// </summary>
            public ConversionSource Source { get; set; }

            /// <summary>
            /// Target format specification;
            /// </summary>
            public ConversionTarget Target { get; set; }

            /// <summary>
            /// Conversion options and parameters;
            /// </summary>
            public ConversionOptions Options { get; set; }

            /// <summary>
            /// Conversion priority;
            /// </summary>
            public int Priority { get; set; }

            /// <summary>
            /// Request status;
            /// </summary>
            public ConversionStatus Status { get; internal set; }

            /// <summary>
            /// Conversion progress (0-100)
            /// </summary>
            public float Progress { get; internal set; }

            /// <summary>
            /// Error information if conversion failed;
            /// </summary>
            public Exception Error { get; internal set; }

            /// <summary>
            /// Conversion result;
            /// </summary>
            public ConversionResult Result { get; internal set; }

            /// <summary>
            /// Creation timestamp;
            /// </summary>
            public DateTime CreatedAt { get; }

            /// <summary>
            /// Start time;
            /// </summary>
            public DateTime? StartTime { get; internal set; }

            /// <summary>
            /// Completion time;
            /// </summary>
            public DateTime? CompletionTime { get; internal set; }

            /// <summary>
            /// Execution time;
            /// </summary>
            public TimeSpan? ExecutionTime => StartTime.HasValue && CompletionTime.HasValue;
                ? CompletionTime.Value - StartTime.Value;
                : null;

            public ConversionRequest()
            {
                Id = Guid.NewGuid();
                Options = new ConversionOptions();
                Status = ConversionStatus.Pending;
                Progress = 0f;
                CreatedAt = DateTime.UtcNow;
                Priority = 1;
            }

            /// <summary>
            /// Validate request parameters;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (Source == null)
                    errors.Add("Source is required");
                else if (!Source.Validate(out var sourceErrors))
                    errors.AddRange(sourceErrors.Select(e => $"Source: {e}"));

                if (Target == null)
                    errors.Add("Target is required");
                else if (!Target.Validate(out var targetErrors))
                    errors.AddRange(targetErrors.Select(e => $"Target: {e}"));

                if (Options == null)
                    errors.Add("Options is required");
                else if (!Options.Validate(out var optionErrors))
                    errors.AddRange(optionErrors.Select(e => $"Options: {e}"));

                return errors.Count == 0;
            }
        }

        /// <summary>
        /// Conversion source specification;
        /// </summary>
        public class ConversionSource;
        {
            /// <summary>
            /// Source type;
            /// </summary>
            public SourceType Type { get; set; }

            /// <summary>
            /// File path for file-based sources;
            /// </summary>
            public string FilePath { get; set; }

            /// <summary>
            /// Raw data for memory-based sources;
            /// </summary>
            public byte[] Data { get; set; }

            /// <summary>
            /// Stream for stream-based sources;
            /// </summary>
            public Stream Stream { get; set; }

            /// <summary>
            /// Source format (auto-detected if null)
            /// </summary>
            public string Format { get; set; }

            /// <summary>
            /// Source metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; }

            /// <summary>
            /// Source file size in bytes;
            /// </summary>
            public long? FileSize { get; internal set; }

            /// <summary>
            /// Source hash for validation;
            /// </summary>
            public string Hash { get; internal set; }

            public ConversionSource()
            {
                Metadata = new Dictionary<string, object>();
            }

            public ConversionSource(string filePath) : this()
            {
                Type = SourceType.File;
                FilePath = filePath;
            }

            public ConversionSource(byte[] data) : this()
            {
                Type = SourceType.Memory;
                Data = data;
            }

            public ConversionSource(Stream stream) : this()
            {
                Type = SourceType.Stream;
                Stream = stream;
            }

            /// <summary>
            /// Validate source;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                switch (Type)
                {
                    case SourceType.File:
                        if (string.IsNullOrWhiteSpace(FilePath))
                            errors.Add("FilePath is required for file source");
                        else if (!File.Exists(FilePath))
                            errors.Add($"File not found: {FilePath}");
                        break;

                    case SourceType.Memory:
                        if (Data == null || Data.Length == 0)
                            errors.Add("Data is required for memory source");
                        break;

                    case SourceType.Stream:
                        if (Stream == null)
                            errors.Add("Stream is required for stream source");
                        else if (!Stream.CanRead)
                            errors.Add("Stream must be readable");
                        break;
                }

                return errors.Count == 0;
            }

            /// <summary>
            /// Get source as stream;
            /// </summary>
            public Stream GetStream()
            {
                switch (Type)
                {
                    case SourceType.File:
                        return File.OpenRead(FilePath);
                    case SourceType.Memory:
                        return new MemoryStream(Data);
                    case SourceType.Stream:
                        return Stream;
                    default:
                        throw new InvalidOperationException($"Unsupported source type: {Type}");
                }
            }

            /// <summary>
            /// Get source bytes;
            /// </summary>
            public byte[] GetBytes()
            {
                switch (Type)
                {
                    case SourceType.File:
                        return File.ReadAllBytes(FilePath);
                    case SourceType.Memory:
                        return Data;
                    case SourceType.Stream:
                        using (var memoryStream = new MemoryStream())
                        {
                            Stream.CopyTo(memoryStream);
                            return memoryStream.ToArray();
                        }
                    default:
                        throw new InvalidOperationException($"Unsupported source type: {Type}");
                }
            }

            /// <summary>
            /// Update metadata from actual source;
            /// </summary>
            public void UpdateMetadata()
            {
                try
                {
                    switch (Type)
                    {
                        case SourceType.File:
                            if (File.Exists(FilePath))
                            {
                                var fileInfo = new FileInfo(FilePath);
                                FileSize = fileInfo.Length;
                                Metadata["CreationTime"] = fileInfo.CreationTime;
                                Metadata["LastWriteTime"] = fileInfo.LastWriteTime;
                                Metadata["Extension"] = fileInfo.Extension;
                            }
                            break;

                        case SourceType.Memory:
                            FileSize = Data?.Length;
                            break;

                        case SourceType.Stream:
                            FileSize = Stream?.Length;
                            break;
                    }

                    // Calculate hash;
                    if (FileSize.HasValue && FileSize > 0)
                    {
                        using (var stream = GetStream())
                        {
                            Hash = CalculateHash(stream);
                        }
                    }
                }
                catch
                {
                    // Metadata update is optional, don't throw;
                }
            }

            private string CalculateHash(Stream stream)
            {
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        /// <summary>
        /// Conversion target specification;
        /// </summary>
        public class ConversionTarget;
        {
            /// <summary>
            /// Target type;
            /// </summary>
            public TargetType Type { get; set; }

            /// <summary>
            /// Target file path;
            /// </summary>
            public string FilePath { get; set; }

            /// <summary>
            /// Target format (e.g., "PNG", "JPEG", "FBX", "GLB")
            /// </summary>
            public string Format { get; set; }

            /// <summary>
            /// Target quality profile;
            /// </summary>
            public QualityProfile Quality { get; set; }

            /// <summary>
            /// Target resolution/size;
            /// </summary>
            public TargetResolution Resolution { get; set; }

            /// <summary>
            /// Compression settings;
            /// </summary>
            public CompressionSettings Compression { get; set; }

            /// <summary>
            /// Output metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; }

            public ConversionTarget()
            {
                Type = TargetType.File;
                Quality = QualityProfile.Medium;
                Resolution = new TargetResolution();
                Compression = new CompressionSettings();
                Metadata = new Dictionary<string, object>();
            }

            public ConversionTarget(string filePath, string format) : this()
            {
                FilePath = filePath;
                Format = format;
            }

            /// <summary>
            /// Validate target;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (string.IsNullOrWhiteSpace(Format))
                    errors.Add("Format is required");

                if (Type == TargetType.File && string.IsNullOrWhiteSpace(FilePath))
                    errors.Add("FilePath is required for file target");

                return errors.Count == 0;
            }

            /// <summary>
            /// Get target stream for writing;
            /// </summary>
            public Stream GetStream()
            {
                if (Type != TargetType.File)
                    throw new InvalidOperationException("Stream output is only supported for file targets");

                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return File.Create(FilePath);
            }
        }

        /// <summary>
        /// Conversion options and parameters;
        /// </summary>
        public class ConversionOptions;
        {
            /// <summary>
            /// Enable format auto-detection;
            /// </summary>
            public bool AutoDetectFormat { get; set; } = true;

            /// <summary>
            /// Enable quality optimization;
            /// </summary>
            public bool OptimizeQuality { get; set; } = true;

            /// <summary>
            /// Enable size optimization;
            /// </summary>
            public bool OptimizeSize { get; set; } = true;

            /// <summary>
            /// Preserve metadata;
            /// </summary>
            public bool PreserveMetadata { get; set; } = true;

            /// <summary>
            /// Preserve transparency;
            /// </summary>
            public bool PreserveTransparency { get; set; } = true;

            /// <summary>
            /// Overwrite existing files;
            /// </summary>
            public bool OverwriteExisting { get; set; } = true;

            /// <summary>
            /// Create backup of original file;
            /// </summary>
            public bool CreateBackup { get; set; } = false;

            /// <summary>
            /// Timeout for conversion;
            /// </summary>
            public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

            /// <summary>
            /// Maximum retry attempts;
            /// </summary>
            public int MaxRetryAttempts { get; set; } = 3;

            /// <summary>
            /// Retry delay;
            /// </summary>
            public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

            /// <summary>
            /// Enable parallel processing for multi-file conversions;
            /// </summary>
            public bool EnableParallelProcessing { get; set; } = true;

            /// <summary>
            /// Maximum degree of parallelism;
            /// </summary>
            public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

            /// <summary>
            /// Custom conversion parameters;
            /// </summary>
            public Dictionary<string, object> Parameters { get; }

            public ConversionOptions()
            {
                Parameters = new Dictionary<string, object>();
            }

            /// <summary>
            /// Validate options;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (Timeout <= TimeSpan.Zero)
                    errors.Add("Timeout must be positive");

                if (MaxRetryAttempts < 0)
                    errors.Add("MaxRetryAttempts cannot be negative");

                if (RetryDelay < TimeSpan.Zero)
                    errors.Add("RetryDelay cannot be negative");

                if (MaxDegreeOfParallelism < 1)
                    errors.Add("MaxDegreeOfParallelism must be at least 1");

                return errors.Count == 0;
            }

            /// <summary>
            /// Get parameter with type safety;
            /// </summary>
            public T GetParameter<T>(string key, T defaultValue = default)
            {
                if (Parameters.TryGetValue(key, out var value) && value is T typedValue)
                    return typedValue;

                return defaultValue;
            }

            /// <summary>
            /// Set parameter;
            /// </summary>
            public void SetParameter(string key, object value)
            {
                Parameters[key] = value;
            }
        }

        /// <summary>
        /// Conversion result;
        /// </summary>
        public class ConversionResult;
        {
            /// <summary>
            /// Success status;
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// Output file path or data;
            /// </summary>
            public object Output { get; set; }

            /// <summary>
            /// Output size in bytes;
            /// </summary>
            public long OutputSize { get; set; }

            /// <summary>
            /// Compression ratio (0-1)
            /// </summary>
            public float CompressionRatio { get; set; }

            /// <summary>
            /// Quality score (0-100)
            /// </summary>
            public float QualityScore { get; set; }

            /// <summary>
            /// Processing statistics;
            /// </summary>
            public ConversionStatistics Statistics { get; set; }

            /// <summary>
            /// Output metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; set; }

            /// <summary>
            /// Warnings during conversion;
            /// </summary>
            public List<string> Warnings { get; set; }

            /// <summary>
            /// Error message if failed;
            /// </summary>
            public string ErrorMessage { get; set; }

            public ConversionResult()
            {
                Statistics = new ConversionStatistics();
                Metadata = new Dictionary<string, object>();
                Warnings = new List<string>();
            }

            public static ConversionResult SuccessResult(object output = null)
            {
                return new ConversionResult;
                {
                    Success = true,
                    Output = output;
                };
            }

            public static ConversionResult FailureResult(string error)
            {
                return new ConversionResult;
                {
                    Success = false,
                    ErrorMessage = error;
                };
            }
        }

        /// <summary>
        /// Conversion statistics;
        /// </summary>
        public class ConversionStatistics;
        {
            public TimeSpan ProcessingTime { get; set; }
            public long PeakMemoryUsage { get; set; }
            public int Iterations { get; set; }
            public float QualityLoss { get; set; }
            public long InputSize { get; set; }
            public long OutputSize { get; set; }
            public TimeSpan CompressionTime { get; set; }
            public TimeSpan DecompressionTime { get; set; }
        }

        /// <summary>
        /// Target resolution specification;
        /// </summary>
        public class TargetResolution;
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public ResolutionUnit Unit { get; set; }
            public bool MaintainAspectRatio { get; set; } = true;
            public ScalingMode ScalingMode { get; set; } = ScalingMode.HighQuality;

            public TargetResolution()
            {
                Width = 0; // Auto;
                Height = 0; // Auto;
                Unit = ResolutionUnit.Pixels;
            }

            public TargetResolution(int width, int height) : this()
            {
                Width = width;
                Height = height;
            }

            /// <summary>
            /// Calculate actual dimensions preserving aspect ratio;
            /// </summary>
            public (int width, int height) CalculateDimensions(int originalWidth, int originalHeight)
            {
                if (Width <= 0 && Height <= 0)
                    return (originalWidth, originalHeight);

                if (!MaintainAspectRatio)
                    return (Width > 0 ? Width : originalWidth, Height > 0 ? Height : originalHeight);

                var ratioX = Width > 0 ? (double)Width / originalWidth : double.MaxValue;
                var ratioY = Height > 0 ? (double)Height / originalHeight : double.MaxValue;
                var ratio = Math.Min(ratioX, ratioY);

                return (
                    (int)Math.Round(originalWidth * ratio),
                    (int)Math.Round(originalHeight * ratio)
                );
            }
        }

        /// <summary>
        /// Compression settings;
        /// </summary>
        public class CompressionSettings;
        {
            public CompressionAlgorithm Algorithm { get; set; }
            public int Level { get; set; } = 6;
            public bool Lossless { get; set; } = true;
            public float Quality { get; set; } = 85f;
            public bool Progressive { get; set; } = false;
            public bool Optimize { get; set; } = true;

            public CompressionSettings()
            {
                Algorithm = CompressionAlgorithm.Auto;
            }
        }

        /// <summary>
        /// Format converter interface;
        /// </summary>
        public interface IFormatConverter;
        {
            /// <summary>
            /// Converter name;
            /// </summary>
            string Name { get; }

            /// <summary>
            /// Converter version;
            /// </summary>
            string Version { get; }

            /// <summary>
            /// Supported input formats;
            /// </summary>
            string[] SupportedInputFormats { get; }

            /// <summary>
            /// Supported output formats;
            /// </summary>
            string[] SupportedOutputFormats { get; }

            /// <summary>
            /// Converter capabilities;
            /// </summary>
            ConverterCapabilities Capabilities { get; }

            /// <summary>
            /// Convert data;
            /// </summary>
            Task<ConversionResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken);

            /// <summary>
            /// Validate conversion request;
            /// </summary>
            bool ValidateRequest(ConversionRequest request, out List<string> errors);

            /// <summary>
            /// Get format information;
            /// </summary>
            FormatInfo GetFormatInfo(string format);

            /// <summary>
            /// Estimate output size;
            /// </summary>
            long EstimateOutputSize(ConversionRequest request);
        }

        /// <summary>
        /// Converter capabilities;
        /// </summary>
        public class ConverterCapabilities;
        {
            public bool SupportsStreaming { get; set; }
            public bool SupportsParallelProcessing { get; set; }
            public bool SupportsProgressiveConversion { get; set; }
            public bool SupportsMetadata { get; set; }
            public bool SupportsTransparency { get; set; }
            public bool SupportsAnimation { get; set; }
            public bool Supports3DModels { get; set; }
            public bool SupportsVectorGraphics { get; set; }
            public long MaxInputSize { get; set; } = 1024 * 1024 * 1024; // 1GB;
            public long MaxOutputSize { get; set; } = 1024 * 1024 * 1024; // 1GB;
            public int MaxDimensions { get; set; } = 16384;
        }

        /// <summary>
        /// Format information;
        /// </summary>
        public class FormatInfo;
        {
            public string Format { get; set; }
            public string Description { get; set; }
            public string MimeType { get; set; }
            public string[] Extensions { get; set; }
            public bool IsLossy { get; set; }
            public bool SupportsTransparency { get; set; }
            public bool SupportsAnimation { get; set; }
            public bool SupportsLayers { get; set; }
            public CompressionAlgorithm DefaultCompression { get; set; }
            public float DefaultQuality { get; set; } = 85f;
        }

        /// <summary>
        /// Batch conversion request;
        /// </summary>
        public class BatchConversionRequest;
        {
            public List<ConversionRequest> Requests { get; }
            public BatchOptions Options { get; }
            public string Name { get; set; }
            public string Description { get; set; }
            public Guid Id { get; }

            public BatchConversionRequest()
            {
                Id = Guid.NewGuid();
                Requests = new List<ConversionRequest>();
                Options = new BatchOptions();
            }

            public BatchConversionRequest(string name) : this()
            {
                Name = name;
            }

            public void AddRequest(ConversionRequest request)
            {
                Requests.Add(request);
            }

            public void AddRequests(IEnumerable<ConversionRequest> requests)
            {
                Requests.AddRange(requests);
            }
        }

        /// <summary>
        /// Batch conversion options;
        /// </summary>
        public class BatchOptions;
        {
            public bool StopOnError { get; set; } = false;
            public int MaxConcurrentConversions { get; set; } = Environment.ProcessorCount;
            public bool GenerateReport { get; set; } = true;
            public string ReportFormat { get; set; } = "JSON";
            public bool PreserveDirectoryStructure { get; set; } = true;
            public string OutputDirectory { get; set; }
            public bool OverwriteExisting { get; set; } = true;
        }

        /// <summary>
        /// Batch conversion result;
        /// </summary>
        public class BatchConversionResult;
        {
            public Guid BatchId { get; set; }
            public string BatchName { get; set; }
            public BatchStatus Status { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? CompletionTime { get; set; }
            public TimeSpan? ExecutionTime { get; set; }
            public int TotalRequests { get; set; }
            public int SuccessfulConversions { get; set; }
            public int FailedConversions { get; set; }
            public int SkippedConversions { get; set; }
            public List<ConversionResult> Results { get; set; }
            public BatchStatistics Statistics { get; set; }
            public string ReportPath { get; set; }
            public Exception Error { get; set; }

            public BatchConversionResult()
            {
                Results = new List<ConversionResult>();
                Statistics = new BatchStatistics();
            }
        }

        /// <summary>
        /// Batch statistics;
        /// </summary>
        public class BatchStatistics;
        {
            public long TotalInputSize { get; set; }
            public long TotalOutputSize { get; set; }
            public float TotalCompressionRatio { get; set; }
            public TimeSpan TotalProcessingTime { get; set; }
            public long PeakMemoryUsage { get; set; }
            public Dictionary<string, int> FormatConversions { get; set; }

            public BatchStatistics()
            {
                FormatConversions = new Dictionary<string, int>();
            }
        }

        /// <summary>
        /// Conversion context for converter execution;
        /// </summary>
        private class ConversionContext;
        {
            public ConversionRequest Request { get; }
            public IFormatConverter Converter { get; }
            public CancellationTokenSource CancellationTokenSource { get; }
            public Task<ConversionResult> Task { get; set; }
            public DateTime StartTime { get; }
            public volatile bool IsCompleted;
            public volatile bool IsCancelled;

            public ConversionContext(ConversionRequest request, IFormatConverter converter)
            {
                Request = request;
                Converter = converter;
                CancellationTokenSource = new CancellationTokenSource();
                StartTime = DateTime.UtcNow;
                IsCompleted = false;
                IsCancelled = false;
            }
        }

        /// <summary>
        /// Converter registry entry
        /// </summary>
        private class ConverterRegistryEntry
        {
            public IFormatConverter Converter { get; }
            public DateTime RegisteredAt { get; }
            public int UsageCount { get; set; }
            public DateTime LastUsed { get; set; }
            public bool IsEnabled { get; set; }

            public ConverterRegistryEntry(IFormatConverter converter)
            {
                Converter = converter;
                RegisteredAt = DateTime.UtcNow;
                UsageCount = 0;
                LastUsed = DateTime.UtcNow;
                IsEnabled = true;
            }
        }

        #endregion;

        #region Enums;

        public enum SourceType;
        {
            File,
            Memory,
            Stream;
        }

        public enum TargetType;
        {
            File,
            Memory,
            Stream;
        }

        public enum ConversionStatus;
        {
            Pending,
            Processing,
            Completed,
            Failed,
            Cancelled,
            Skipped;
        }

        public enum BatchStatus;
        {
            Pending,
            Processing,
            Completed,
            Failed,
            PartiallyCompleted,
            Cancelled;
        }

        public enum QualityProfile;
        {
            Low,
            Medium,
            High,
            Ultra,
            Lossless;
        }

        public enum ResolutionUnit;
        {
            Pixels,
            Inches,
            Centimeters,
            Millimeters,
            Points;
        }

        public enum ScalingMode;
        {
            NearestNeighbor,
            Bilinear,
            Bicubic,
            HighQuality,
            Lanczos;
        }

        public enum CompressionAlgorithm;
        {
            None,
            Auto,
            Deflate,
            GZip,
            Brotli,
            LZ4,
            Zstd,
            JPEG,
            PNG,
            WebP,
            AVIF;
        }

        #endregion;

        #region Fields;

        private readonly Dictionary<string, ConverterRegistryEntry> _converters;
        private readonly Dictionary<Guid, ConversionContext> _activeConversions;
        private readonly Dictionary<Guid, BatchConversionResult> _batchResults;
        private readonly List<FormatInfo> _registeredFormats;
        private readonly ILogger _logger;
        private readonly object _converterLock = new object();
        private readonly object _conversionLock = new object();
        private bool _isDisposed;

        // Configuration;
        private EngineConfiguration _configuration;
        private FormatDetector _formatDetector;
        private QualityOptimizer _qualityOptimizer;

        // Statistics;
        private EngineStatistics _statistics;
        private PerformanceMonitor _performanceMonitor;

        #endregion;

        #region Properties;

        /// <summary>
        /// Engine configuration;
        /// </summary>
        public EngineConfiguration Configuration => _configuration;

        /// <summary>
        /// Engine statistics;
        /// </summary>
        public EngineStatistics Statistics => _statistics;

        /// <summary>
        /// Registered converter count;
        /// </summary>
        public int ConverterCount => _converters.Count;

        /// <summary>
        /// Registered format count;
        /// </summary>
        public int FormatCount => _registeredFormats.Count;

        /// <summary>
        /// Active conversion count;
        /// </summary>
        public int ActiveConversionCount => _activeConversions.Count;

        /// <summary>
        /// Supported input formats;
        /// </summary>
        public List<string> SupportedInputFormats;
        {
            get;
            {
                lock (_converterLock)
                {
                    return _converters.Values;
                        .Where(c => c.IsEnabled)
                        .SelectMany(c => c.Converter.SupportedInputFormats)
                        .Distinct()
                        .ToList();
                }
            }
        }

        /// <summary>
        /// Supported output formats;
        /// </summary>
        public List<string> SupportedOutputFormats;
        {
            get;
            {
                lock (_converterLock)
                {
                    return _converters.Values;
                        .Where(c => c.IsEnabled)
                        .SelectMany(c => c.Converter.SupportedOutputFormats)
                        .Distinct()
                        .ToList();
                }
            }
        }

        /// <summary>
        /// Is engine initialized;
        /// </summary>
        public bool IsInitialized { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Conversion started event;
        /// </summary>
        public event EventHandler<ConversionRequest> OnConversionStarted;

        /// <summary>
        /// Conversion completed event;
        /// </summary>
        public event EventHandler<ConversionResultEventArgs> OnConversionCompleted;

        /// <summary>
        /// Conversion progress changed event;
        /// </summary>
        public event EventHandler<ConversionProgressEventArgs> OnConversionProgressChanged;

        /// <summary>
        /// Batch conversion started event;
        /// </summary>
        public event EventHandler<BatchConversionRequest> OnBatchConversionStarted;

        /// <summary>
        /// Batch conversion completed event;
        /// </summary>
        public event EventHandler<BatchConversionResult> OnBatchConversionCompleted;

        /// <summary>
        /// Converter registered event;
        /// </summary>
        public event EventHandler<IFormatConverter> OnConverterRegistered;

        /// <summary>
        /// Converter unregistered event;
        /// </summary>
        public event EventHandler<IFormatConverter> OnConverterUnregistered;

        /// <summary>
        /// Engine error event;
        /// </summary>
        public event EventHandler<EngineErrorEventArgs> OnEngineError;

        /// <summary>
        /// Event args classes;
        /// </summary>
        public class ConversionResultEventArgs : EventArgs;
        {
            public ConversionRequest Request { get; set; }
            public ConversionResult Result { get; set; }
        }

        public class ConversionProgressEventArgs : EventArgs;
        {
            public Guid RequestId { get; set; }
            public float Progress { get; set; }
            public ConversionStatus Status { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class EngineErrorEventArgs : EventArgs;
        {
            public Exception Error { get; set; }
            public string Context { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion;

        #region Constructor;

        /// <summary>
        /// Create converter engine with default configuration;
        /// </summary>
        public ConverterEngine() : this(new EngineConfiguration())
        {
        }

        /// <summary>
        /// Create converter engine with custom configuration;
        /// </summary>
        public ConverterEngine(EngineConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _converters = new Dictionary<string, ConverterRegistryEntry>(StringComparer.OrdinalIgnoreCase);
            _activeConversions = new Dictionary<Guid, ConversionContext>();
            _batchResults = new Dictionary<Guid, BatchConversionResult>();
            _registeredFormats = new List<FormatInfo>();

            _logger = LogManager.GetLogger("ConverterEngine");
            _formatDetector = new FormatDetector();
            _qualityOptimizer = new QualityOptimizer();
            _performanceMonitor = new PerformanceMonitor();

            _statistics = new EngineStatistics();
            _isDisposed = false;
            IsInitialized = false;

            InitializeEngine();
        }

        /// <summary>
        /// Initialize engine;
        /// </summary>
        private void InitializeEngine()
        {
            try
            {
                // Load built-in converters;
                LoadBuiltInConverters();

                // Initialize format detector;
                _formatDetector.Initialize();

                // Initialize quality optimizer;
                _qualityOptimizer.Initialize();

                IsInitialized = true;
                _logger.Info($"ConverterEngine initialized with {_converters.Count} converters");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize ConverterEngine: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Load built-in converters;
        /// </summary>
        private void LoadBuiltInConverters()
        {
            // Register image converters;
            RegisterConverter(new ImageConverter());
            RegisterConverter(new PdfConverter());
            RegisterConverter(new DocumentConverter());
            RegisterConverter(new AudioConverter());
            RegisterConverter(new VideoConverter());
            RegisterConverter(new ModelConverter());

            // Register archive converters;
            RegisterConverter(new ArchiveConverter());

            _logger.Info($"Loaded {_converters.Count} built-in converters");
        }

        #endregion;

        #region Converter Registration;

        /// <summary>
        /// Register format converter;
        /// </summary>
        public bool RegisterConverter(IFormatConverter converter)
        {
            if (converter == null)
                throw new ArgumentNullException(nameof(converter));

            lock (_converterLock)
            {
                var key = $"{converter.Name}_{converter.Version}";

                if (_converters.ContainsKey(key))
                {
                    _logger.Warning($"Converter '{key}' is already registered");
                    return false;
                }

                var entry = new ConverterRegistryEntry(converter);
                _converters[key] = entry

                // Register formats;
                foreach (var format in converter.SupportedInputFormats.Concat(converter.SupportedOutputFormats))
                {
                    var formatInfo = converter.GetFormatInfo(format);
                    if (formatInfo != null && !_registeredFormats.Any(f => f.Format.Equals(format, StringComparison.OrdinalIgnoreCase)))
                    {
                        _registeredFormats.Add(formatInfo);
                    }
                }

                _logger.Info($"Registered converter: {converter.Name} v{converter.Version}");
                OnConverterRegistered?.Invoke(this, converter);

                return true;
            }
        }

        /// <summary>
        /// Unregister format converter;
        /// </summary>
        public bool UnregisterConverter(string converterName, string version = null)
        {
            lock (_converterLock)
            {
                var key = string.IsNullOrEmpty(version)
                    ? _converters.Keys.FirstOrDefault(k => k.StartsWith(converterName, StringComparison.OrdinalIgnoreCase))
                    : $"{converterName}_{version}";

                if (key != null && _converters.TryGetValue(key, out var entry))
                {
                    _converters.Remove(key);

                    // Clean up formats that are no longer supported;
                    CleanupFormats();

                    _logger.Info($"Unregistered converter: {converterName}");
                    OnConverterUnregistered?.Invoke(this, entry.Converter);

                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Clean up unsupported formats;
        /// </summary>
        private void CleanupFormats()
        {
            var supportedFormats = _converters.Values;
                .SelectMany(c => c.Converter.SupportedInputFormats.Concat(c.Converter.SupportedOutputFormats))
                .Distinct()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _registeredFormats.RemoveAll(f => !supportedFormats.Contains(f.Format));
        }

        /// <summary>
        /// Get converter by name;
        /// </summary>
        public IFormatConverter GetConverter(string converterName, string version = null)
        {
            lock (_converterLock)
            {
                var key = string.IsNullOrEmpty(version)
                    ? _converters.Keys.FirstOrDefault(k => k.StartsWith(converterName, StringComparison.OrdinalIgnoreCase))
                    : $"{converterName}_{version}";

                if (key != null && _converters.TryGetValue(key, out var entry))
                {
                    return entry.Converter;
                }

                return null;
            }
        }

        /// <summary>
        /// Get all registered converters;
        /// </summary>
        public List<IFormatConverter> GetConverters()
        {
            lock (_converterLock)
            {
                return _converters.Values.Select(e => e.Converter).ToList();
            }
        }

        /// <summary>
        /// Get converters for specific format conversion;
        /// </summary>
        public List<IFormatConverter> GetConvertersForConversion(string inputFormat, string outputFormat)
        {
            lock (_converterLock)
            {
                return _converters.Values;
                    .Where(e => e.IsEnabled)
                    .Select(e => e.Converter)
                    .Where(c => c.SupportedInputFormats.Contains(inputFormat, StringComparer.OrdinalIgnoreCase) &&
                               c.SupportedOutputFormats.Contains(outputFormat, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        /// <summary>
        /// Enable/disable converter;
        /// </summary>
        public bool SetConverterEnabled(string converterName, string version, bool enabled)
        {
            lock (_converterLock)
            {
                var key = $"{converterName}_{version}";
                if (_converters.TryGetValue(key, out var entry))
                {
                    entry.IsEnabled = enabled;
                    _logger.Info($"Converter '{converterName}' {(enabled ? "enabled" : "disabled")}");
                    return true;
                }

                return false;
            }
        }

        #endregion;

        #region Format Detection;

        /// <summary>
        /// Detect file format;
        /// </summary>
        public string DetectFormat(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}", filePath);

            return _formatDetector.DetectFromFile(filePath);
        }

        /// <summary>
        /// Detect format from bytes;
        /// </summary>
        public string DetectFormat(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            return _formatDetector.DetectFromBytes(data);
        }

        /// <summary>
        /// Detect format from stream;
        /// </summary>
        public string DetectFormat(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!stream.CanRead)
                throw new ArgumentException("Stream must be readable", nameof(stream));

            return _formatDetector.DetectFromStream(stream);
        }

        /// <summary>
        /// Get format information;
        /// </summary>
        public FormatInfo GetFormatInfo(string format)
        {
            return _registeredFormats.FirstOrDefault(f =>
                f.Format.Equals(format, StringComparison.OrdinalIgnoreCase) ||
                f.Extensions.Any(e => e.Equals(format, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Get all format information;
        /// </summary>
        public List<FormatInfo> GetAllFormatInfo()
        {
            return new List<FormatInfo>(_registeredFormats);
        }

        #endregion;

        #region Single Conversion;

        /// <summary>
        /// Convert single file;
        /// </summary>
        public async Task<ConversionResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!request.Validate(out var errors))
                throw new ArgumentException($"Invalid conversion request: {string.Join(", ", errors)}");

            // Auto-detect source format if needed;
            if (string.IsNullOrEmpty(request.Source.Format) && request.Options.AutoDetectFormat)
            {
                request.Source.Format = DetectFormat(request.Source);
                if (string.IsNullOrEmpty(request.Source.Format))
                {
                    return ConversionResult.FailureResult("Could not detect source format");
                }
            }

            // Update source metadata;
            request.Source.UpdateMetadata();

            // Find appropriate converter;
            var converter = FindConverter(request.Source.Format, request.Target.Format);
            if (converter == null)
            {
                return ConversionResult.FailureResult(
                    $"No converter found for {request.Source.Format} -> {request.Target.Format}");
            }

            // Validate request with converter;
            if (!converter.ValidateRequest(request, out var converterErrors))
            {
                return ConversionResult.FailureResult(
                    $"Invalid conversion request: {string.Join(", ", converterErrors)}");
            }

            // Check file size limits;
            var maxInputSize = converter.Capabilities.MaxInputSize;
            if (request.Source.FileSize.HasValue && request.Source.FileSize > maxInputSize)
            {
                return ConversionResult.FailureResult(
                    $"Input file size ({request.Source.FileSize} bytes) exceeds maximum ({maxInputSize} bytes)");
            }

            // Estimate output size;
            var estimatedSize = converter.EstimateOutputSize(request);
            if (estimatedSize > converter.Capabilities.MaxOutputSize)
            {
                return ConversionResult.FailureResult(
                    $"Estimated output size ({estimatedSize} bytes) exceeds maximum ({converter.Capabilities.MaxOutputSize} bytes)");
            }

            // Create conversion context;
            var context = new ConversionContext(request, converter);

            lock (_conversionLock)
            {
                _activeConversions[request.Id] = context;
            }

            request.Status = ConversionStatus.Processing;
            request.StartTime = DateTime.UtcNow;

            _logger.Info($"Starting conversion: {request.Source.Format} -> {request.Target.Format}");
            OnConversionStarted?.Invoke(this, request);

            try
            {
                // Execute conversion with timeout;
                var timeoutTokenSource = new CancellationTokenSource(request.Options.Timeout);
                var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutTokenSource.Token, context.CancellationTokenSource.Token);

                var conversionTask = converter.ConvertAsync(request, linkedTokenSource.Token);

                // Monitor progress;
                var progressTask = MonitorConversionProgressAsync(request, linkedTokenSource.Token);

                // Wait for completion;
                var result = await conversionTask;

                // Update request with result;
                request.CompletionTime = DateTime.UtcNow;
                request.Result = result;
                request.Status = result.Success ? ConversionStatus.Completed : ConversionStatus.Failed;
                request.Error = result.Success ? null : new Exception(result.ErrorMessage);

                // Update statistics;
                UpdateStatistics(request, result);

                // Fire completion event;
                OnConversionCompleted?.Invoke(this, new ConversionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });

                if (result.Success)
                {
                    _logger.Info($"Conversion completed successfully in {request.ExecutionTime}");
                }
                else;
                {
                    _logger.Error($"Conversion failed: {result.ErrorMessage}");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                request.Status = ConversionStatus.Cancelled;
                request.Error = new OperationCanceledException("Conversion cancelled");

                var result = ConversionResult.FailureResult("Conversion cancelled");
                request.Result = result;

                _logger.Info("Conversion cancelled");
                OnConversionCompleted?.Invoke(this, new ConversionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });

                return result;
            }
            catch (Exception ex)
            {
                request.Status = ConversionStatus.Failed;
                request.Error = ex;

                var result = ConversionResult.FailureResult($"Conversion error: {ex.Message}");
                request.Result = result;

                _logger.Error($"Conversion error: {ex.Message}", ex);
                OnConversionCompleted?.Invoke(this, new ConversionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });

                OnEngineError?.Invoke(this, new EngineErrorEventArgs;
                {
                    Error = ex,
                    Context = "Single conversion",
                    Timestamp = DateTime.UtcNow;
                });

                return result;
            }
            finally
            {
                context.IsCompleted = true;
                lock (_conversionLock)
                {
                    _activeConversions.Remove(request.Id);
                }
            }
        }

        /// <summary>
        /// Convert file with simple parameters;
        /// </summary>
        public async Task<ConversionResult> ConvertFileAsync(
            string sourcePath,
            string targetPath,
            string targetFormat = null,
            ConversionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be empty", nameof(sourcePath));

            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Target path cannot be empty", nameof(targetPath));

            // Auto-detect target format from extension if not specified;
            if (string.IsNullOrEmpty(targetFormat))
            {
                targetFormat = Path.GetExtension(targetPath).TrimStart('.');
                if (string.IsNullOrEmpty(targetFormat))
                {
                    throw new ArgumentException("Target format could not be determined from file extension", nameof(targetPath));
                }
            }

            var request = new ConversionRequest;
            {
                Source = new ConversionSource(sourcePath),
                Target = new ConversionTarget(targetPath, targetFormat),
                Options = options ?? new ConversionOptions()
            };

            return await ConvertAsync(request);
        }

        /// <summary>
        /// Convert bytes with simple parameters;
        /// </summary>
        public async Task<ConversionResult> ConvertBytesAsync(
            byte[] sourceData,
            string sourceFormat,
            string targetFormat,
            ConversionOptions options = null)
        {
            if (sourceData == null || sourceData.Length == 0)
                throw new ArgumentException("Source data cannot be null or empty", nameof(sourceData));

            if (string.IsNullOrWhiteSpace(targetFormat))
                throw new ArgumentException("Target format cannot be empty", nameof(targetFormat));

            var request = new ConversionRequest;
            {
                Source = new ConversionSource(sourceData) { Format = sourceFormat },
                Target = new ConversionTarget { Format = targetFormat },
                Options = options ?? new ConversionOptions()
            };

            return await ConvertAsync(request);
        }

        #endregion;

        #region Batch Conversion;

        /// <summary>
        /// Convert multiple files in batch;
        /// </summary>
        public async Task<BatchConversionResult> ConvertBatchAsync(
            BatchConversionRequest batchRequest,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (batchRequest == null)
                throw new ArgumentNullException(nameof(batchRequest));

            if (batchRequest.Requests.Count == 0)
                throw new ArgumentException("Batch request must contain at least one conversion request", nameof(batchRequest));

            var batchResult = new BatchConversionResult;
            {
                BatchId = batchRequest.Id,
                BatchName = batchRequest.Name,
                Status = BatchStatus.Processing,
                StartTime = DateTime.UtcNow,
                TotalRequests = batchRequest.Requests.Count;
            };

            _batchResults[batchRequest.Id] = batchResult;

            _logger.Info($"Starting batch conversion '{batchRequest.Name}' with {batchRequest.Requests.Count} requests");
            OnBatchConversionStarted?.Invoke(this, batchRequest);

            var successfulConversions = 0;
            var failedConversions = 0;
            var skippedConversions = 0;
            var results = new List<ConversionResult>();
            var exceptions = new List<Exception>();

            try
            {
                // Process conversions based on parallelism setting;
                if (batchRequest.Options.EnableParallelProcessing && batchRequest.Options.MaxConcurrentConversions > 1)
                {
                    var parallelOptions = new ParallelOptions;
                    {
                        MaxDegreeOfParallelism = batchRequest.Options.MaxConcurrentConversions,
                        CancellationToken = cancellationToken;
                    };

                    var resultLock = new object();

                    await Parallel.ForEachAsync(
                        batchRequest.Requests,
                        parallelOptions,
                        async (request, ct) =>
                        {
                            try
                            {
                                // Set output path if not specified;
                                if (request.Target.Type == TargetType.File &&
                                    string.IsNullOrEmpty(request.Target.FilePath) &&
                                    !string.IsNullOrEmpty(batchRequest.Options.OutputDirectory))
                                {
                                    request.Target.FilePath = GetOutputPath(
                                        request.Source.FilePath,
                                        request.Target.Format,
                                        batchRequest.Options.OutputDirectory,
                                        batchRequest.Options.PreserveDirectoryStructure);
                                }

                                var result = await ConvertAsync(request, ct);

                                lock (resultLock)
                                {
                                    results.Add(result);
                                    if (result.Success)
                                        successfulConversions++;
                                    else;
                                        failedConversions++;

                                    UpdateBatchStatistics(batchResult.Statistics, request, result);
                                }
                            }
                            catch (Exception ex) when (batchRequest.Options.StopOnError)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                lock (resultLock)
                                {
                                    exceptions.Add(ex);
                                    failedConversions++;
                                    _logger.Error($"Batch conversion failed for request {request.Id}: {ex.Message}", ex);
                                }
                            }
                        });
                }
                else;
                {
                    // Sequential processing;
                    foreach (var request in batchRequest.Requests)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        try
                        {
                            // Set output path if not specified;
                            if (request.Target.Type == TargetType.File &&
                                string.IsNullOrEmpty(request.Target.FilePath) &&
                                !string.IsNullOrEmpty(batchRequest.Options.OutputDirectory))
                            {
                                request.Target.FilePath = GetOutputPath(
                                    request.Source.FilePath,
                                    request.Target.Format,
                                    batchRequest.Options.OutputDirectory,
                                    batchRequest.Options.PreserveDirectoryStructure);
                            }

                            var result = await ConvertAsync(request, cancellationToken);
                            results.Add(result);

                            if (result.Success)
                                successfulConversions++;
                            else;
                                failedConversions++;

                            UpdateBatchStatistics(batchResult.Statistics, request, result);
                        }
                        catch (Exception ex) when (batchRequest.Options.StopOnError)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                            failedConversions++;
                            _logger.Error($"Batch conversion failed for request {request.Id}: {ex.Message}", ex);
                        }
                    }
                }

                // Determine batch status;
                batchResult.Status = DetermineBatchStatus(
                    successfulConversions,
                    failedConversions,
                    skippedConversions,
                    batchRequest.Requests.Count,
                    cancellationToken.IsCancellationRequested);

                // Set results;
                batchResult.SuccessfulConversions = successfulConversions;
                batchResult.FailedConversions = failedConversions;
                batchResult.SkippedConversions = skippedConversions;
                batchResult.Results = results;
                batchResult.CompletionTime = DateTime.UtcNow;
                batchResult.ExecutionTime = batchResult.CompletionTime - batchResult.StartTime;

                if (exceptions.Count > 0)
                {
                    batchResult.Error = new AggregateException("One or more conversions failed", exceptions);
                }

                // Generate report if requested;
                if (batchRequest.Options.GenerateReport)
                {
                    batchResult.ReportPath = GenerateBatchReport(batchRequest, batchResult);
                }

                _logger.Info($"Batch conversion completed: {successfulConversions} successful, {failedConversions} failed");
            }
            catch (Exception ex)
            {
                batchResult.Status = BatchStatus.Failed;
                batchResult.Error = ex;
                batchResult.CompletionTime = DateTime.UtcNow;

                _logger.Error($"Batch conversion failed: {ex.Message}", ex);
                OnEngineError?.Invoke(this, new EngineErrorEventArgs;
                {
                    Error = ex,
                    Context = "Batch conversion",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                OnBatchConversionCompleted?.Invoke(this, batchResult);
                _batchResults.Remove(batchRequest.Id);
            }

            return batchResult;
        }

        /// <summary>
        /// Convert multiple files with simple parameters;
        /// </summary>
        public async Task<BatchConversionResult> ConvertFilesAsync(
            IEnumerable<string> sourceFiles,
            string outputDirectory,
            string targetFormat,
            BatchOptions options = null)
        {
            if (sourceFiles == null || !sourceFiles.Any())
                throw new ArgumentException("Source files cannot be null or empty", nameof(sourceFiles));

            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory cannot be empty", nameof(outputDirectory));

            if (string.IsNullOrWhiteSpace(targetFormat))
                throw new ArgumentException("Target format cannot be empty", nameof(targetFormat));

            var batchRequest = new BatchConversionRequest($"BatchConvert_{DateTime.Now:yyyyMMdd_HHmmss}")
            {
                Options = options ?? new BatchOptions;
                {
                    OutputDirectory = outputDirectory,
                    PreserveDirectoryStructure = true;
                }
            };

            foreach (var sourceFile in sourceFiles)
            {
                if (!File.Exists(sourceFile))
                {
                    _logger.Warning($"Source file not found, skipping: {sourceFile}");
                    continue;
                }

                var request = new ConversionRequest;
                {
                    Source = new ConversionSource(sourceFile),
                    Target = new ConversionTarget;
                    {
                        Format = targetFormat,
                        Type = TargetType.File;
                        // FilePath will be set automatically during batch processing;
                    },
                    Options = new ConversionOptions;
                    {
                        OverwriteExisting = batchRequest.Options.OverwriteExisting;
                    }
                };

                batchRequest.AddRequest(request);
            }

            return await ConvertBatchAsync(batchRequest);
        }

        /// <summary>
        /// Get output path for batch conversion;
        /// </summary>
        private string GetOutputPath(string sourcePath, string targetFormat, string outputDirectory, bool preserveStructure)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var extension = $".{targetFormat.ToLowerInvariant()}";
            var outputFileName = $"{fileName}{extension}";

            if (preserveStructure)
            {
                // Get relative path from source directory root;
                var sourceDir = Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrEmpty(sourceDir))
                    return Path.Combine(outputDirectory, outputFileName);

                // Create relative directory structure in output directory;
                var relativeDir = Path.GetRelativePath(Path.GetPathRoot(sourceDir) ?? string.Empty, sourceDir);
                var outputPath = Path.Combine(outputDirectory, relativeDir, outputFileName);

                // Create directory if it doesn't exist;
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                return outputPath;
            }
            else;
            {
                return Path.Combine(outputDirectory, outputFileName);
            }
        }

        /// <summary>
        /// Determine batch status;
        /// </summary>
        private BatchStatus DetermineBatchStatus(int successful, int failed, int skipped, int total, bool cancelled)
        {
            if (cancelled)
                return BatchStatus.Cancelled;

            if (successful == total)
                return BatchStatus.Completed;

            if (failed == total)
                return BatchStatus.Failed;

            if (successful > 0 && failed > 0)
                return BatchStatus.PartiallyCompleted;

            return BatchStatus.Completed;
        }

        /// <summary>
        /// Update batch statistics;
        /// </summary>
        private void UpdateBatchStatistics(BatchStatistics statistics, ConversionRequest request, ConversionResult result)
        {
            statistics.TotalInputSize += request.Source.FileSize ?? 0;
            statistics.TotalOutputSize += result.OutputSize;

            if (request.Source.FileSize.HasValue && request.Source.FileSize > 0)
            {
                statistics.TotalCompressionRatio = (float)statistics.TotalOutputSize / statistics.TotalInputSize;
            }

            statistics.TotalProcessingTime += result.Statistics.ProcessingTime;
            statistics.PeakMemoryUsage = Math.Max(statistics.PeakMemoryUsage, result.Statistics.PeakMemoryUsage);

            var conversionKey = $"{request.Source.Format}_to_{request.Target.Format}";
            if (statistics.FormatConversions.ContainsKey(conversionKey))
                statistics.FormatConversions[conversionKey]++;
            else;
                statistics.FormatConversions[conversionKey] = 1;
        }

        /// <summary>
        /// Generate batch report;
        /// </summary>
        private string GenerateBatchReport(BatchConversionRequest batchRequest, BatchConversionResult batchResult)
        {
            try
            {
                var reportDir = Path.Combine(_configuration.TempDirectory, "Reports");
                Directory.CreateDirectory(reportDir);

                var reportPath = Path.Combine(reportDir, $"BatchReport_{batchRequest.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.json");

                var report = new;
                {
                    BatchId = batchRequest.Id,
                    BatchName = batchRequest.Name,
                    StartTime = batchResult.StartTime,
                    CompletionTime = batchResult.CompletionTime,
                    ExecutionTime = batchResult.ExecutionTime?.TotalSeconds,
                    Status = batchResult.Status.ToString(),
                    Statistics = new;
                    {
                        TotalRequests = batchResult.TotalRequests,
                        Successful = batchResult.SuccessfulConversions,
                        Failed = batchResult.FailedConversions,
                        Skipped = batchResult.SkippedConversions,
                        SuccessRate = (float)batchResult.SuccessfulConversions / batchResult.TotalRequests * 100,
                        TotalInputSize = batchResult.Statistics.TotalInputSize,
                        TotalOutputSize = batchResult.Statistics.TotalOutputSize,
                        CompressionRatio = batchResult.Statistics.TotalCompressionRatio,
                        TotalProcessingTime = batchResult.Statistics.TotalProcessingTime.TotalSeconds,
                        PeakMemoryUsage = batchResult.Statistics.PeakMemoryUsage,
                        FormatConversions = batchResult.Statistics.FormatConversions;
                    },
                    Results = batchResult.Results.Select(r => new;
                    {
                        Success = r.Success,
                        OutputSize = r.OutputSize,
                        CompressionRatio = r.CompressionRatio,
                        QualityScore = r.QualityScore,
                        ProcessingTime = r.Statistics.ProcessingTime.TotalSeconds,
                        ErrorMessage = r.ErrorMessage,
                        Warnings = r.Warnings;
                    })
                };

                var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = true;
                });

                File.WriteAllText(reportPath, json);
                return reportPath;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate batch report: {ex.Message}", ex);
                return null;
            }
        }

        #endregion;

        #region Helper Methods;

        /// <summary>
        /// Find converter for format conversion;
        /// </summary>
        private IFormatConverter FindConverter(string sourceFormat, string targetFormat)
        {
            lock (_converterLock)
            {
                // First, try to find exact match;
                var exactMatch = _converters.Values;
                    .Where(e => e.IsEnabled)
                    .Select(e => e.Converter)
                    .FirstOrDefault(c =>
                        c.SupportedInputFormats.Contains(sourceFormat, StringComparer.OrdinalIgnoreCase) &&
                        c.SupportedOutputFormats.Contains(targetFormat, StringComparer.OrdinalIgnoreCase));

                if (exactMatch != null)
                {
                    UpdateConverterUsage(exactMatch);
                    return exactMatch;
                }

                // Try to find converter with format aliases;
                foreach (var entry in _converters.Values.Where(e => e.IsEnabled))
                {
                    var converter = entry.Converter;

                    // Check if converter supports source format (with aliases)
                    var supportedInput = converter.SupportedInputFormats;
                        .FirstOrDefault(f => FormatMatches(f, sourceFormat));

                    if (supportedInput == null)
                        continue;

                    // Check if converter supports target format (with aliases)
                    var supportedOutput = converter.SupportedOutputFormats;
                        .FirstOrDefault(f => FormatMatches(f, targetFormat));

                    if (supportedOutput != null)
                    {
                        UpdateConverterUsage(converter);
                        return converter;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Check if formats match (with aliases)
        /// </summary>
        private bool FormatMatches(string format1, string format2)
        {
            if (string.Equals(format1, format2, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check format aliases;
            var aliases = GetFormatAliases(format1);
            return aliases.Any(a => string.Equals(a, format2, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get format aliases;
        /// </summary>
        private List<string> GetFormatAliases(string format)
        {
            var aliases = new List<string> { format };

            // Common format aliases;
            switch (format.ToUpperInvariant())
            {
                case "JPG":
                    aliases.Add("JPEG");
                    break;
                case "JPEG":
                    aliases.Add("JPG");
                    break;
                case "TIF":
                    aliases.Add("TIFF");
                    break;
                case "TIFF":
                    aliases.Add("TIF");
                    break;
            }

            return aliases;
        }

        /// <summary>
        /// Update converter usage statistics;
        /// </summary>
        private void UpdateConverterUsage(IFormatConverter converter)
        {
            lock (_converterLock)
            {
                var entry = _converters.Values.FirstOrDefault(e => e.Converter == converter);
                if (entry != null)
                {
                    entry.UsageCount++;
                    entry.LastUsed = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// Monitor conversion progress;
        /// </summary>
        private async Task MonitorConversionProgressAsync(ConversionRequest request, CancellationToken cancellationToken)
        {
            var lastProgress = 0f;

            while (!cancellationToken.IsCancellationRequested &&
                   (request.Status == ConversionStatus.Processing || request.Status == ConversionStatus.Pending))
            {
                await Task.Delay(100, cancellationToken);

                // Simulate progress for now - in real implementation, this would come from the converter;
                if (request.Progress < 100f)
                {
                    request.Progress = Math.Min(request.Progress + 1f, 100f);

                    if (Math.Abs(request.Progress - lastProgress) >= 1f)
                    {
                        lastProgress = request.Progress;
                        OnConversionProgressChanged?.Invoke(this, new ConversionProgressEventArgs;
                        {
                            RequestId = request.Id,
                            Progress = request.Progress,
                            Status = request.Status,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Update engine statistics;
        /// </summary>
        private void UpdateStatistics(ConversionRequest request, ConversionResult result)
        {
            _statistics.TotalConversions++;

            if (result.Success)
                _statistics.SuccessfulConversions++;
            else;
                _statistics.FailedConversions++;

            _statistics.TotalProcessingTime += result.Statistics.ProcessingTime;
            _statistics.TotalInputSize += request.Source.FileSize ?? 0;
            _statistics.TotalOutputSize += result.OutputSize;
            _statistics.PeakMemoryUsage = Math.Max(_statistics.PeakMemoryUsage, result.Statistics.PeakMemoryUsage);

            var conversionKey = $"{request.Source.Format}_to_{request.Target.Format}";
            if (_statistics.FormatConversionCounts.ContainsKey(conversionKey))
                _statistics.FormatConversionCounts[conversionKey]++;
            else;
                _statistics.FormatConversionCounts[conversionKey] = 1;
        }

        /// <summary>
        /// Detect format from conversion source;
        /// </summary>
        private string DetectFormat(ConversionSource source)
        {
            try
            {
                switch (source.Type)
                {
                    case SourceType.File:
                        return DetectFormat(source.FilePath);
                    case SourceType.Memory:
                        return DetectFormat(source.Data);
                    case SourceType.Stream:
                        return DetectFormat(source.Stream);
                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Cancel active conversion;
        /// </summary>
        public bool CancelConversion(Guid requestId)
        {
            lock (_conversionLock)
            {
                if (_activeConversions.TryGetValue(requestId, out var context))
                {
                    context.IsCancelled = true;
                    context.CancellationTokenSource.Cancel();
                    _logger.Info($"Cancelled conversion: {requestId}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get active conversions;
        /// </summary>
        public List<ConversionRequest> GetActiveConversions()
        {
            lock (_conversionLock)
            {
                return _activeConversions.Values.Select(c => c.Request).ToList();
            }
        }

        /// <summary>
        /// Get batch result;
        /// </summary>
        public BatchConversionResult GetBatchResult(Guid batchId)
        {
            _batchResults.TryGetValue(batchId, out var result);
            return result;
        }

        /// <summary>
        /// Validate engine is not disposed;
        /// </summary>
        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ConverterEngine));
        }

        #endregion;

        #region Engine Configuration;

        /// <summary>
        /// Engine configuration;
        /// </summary>
        public class EngineConfiguration;
        {
            public string TempDirectory { get; set; }
            public int MaxConcurrentConversions { get; set; }
            public long MaxMemoryUsage { get; set; }
            public bool EnableCaching { get; set; }
            public int CacheSize { get; set; }
            public TimeSpan CacheDuration { get; set; }
            public bool EnableLogging { get; set; }
            public string LogDirectory { get; set; }
            public bool EnablePerformanceMonitoring { get; set; }
            public bool EnableFormatDetection { get; set; }
            public bool EnableQualityOptimization { get; set; }

            public EngineConfiguration()
            {
                TempDirectory = Path.Combine(Path.GetTempPath(), "NEDA_ConverterEngine");
                MaxConcurrentConversions = Environment.ProcessorCount * 2;
                MaxMemoryUsage = 1024 * 1024 * 1024; // 1GB;
                EnableCaching = true;
                CacheSize = 100;
                CacheDuration = TimeSpan.FromHours(1);
                EnableLogging = true;
                LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NEDA", "Logs");
                EnablePerformanceMonitoring = true;
                EnableFormatDetection = true;
                EnableQualityOptimization = true;
            }
        }

        /// <summary>
        /// Engine statistics;
        /// </summary>
        public class EngineStatistics;
        {
            public int TotalConversions { get; set; }
            public int SuccessfulConversions { get; set; }
            public int FailedConversions { get; set; }
            public TimeSpan TotalProcessingTime { get; set; }
            public long TotalInputSize { get; set; }
            public long TotalOutputSize { get; set; }
            public float AverageCompressionRatio { get; set; }
            public long PeakMemoryUsage { get; set; }
            public Dictionary<string, int> FormatConversionCounts { get; set; }
            public DateTime StartTime { get; }
            public TimeSpan Uptime => DateTime.UtcNow - StartTime;

            public EngineStatistics()
            {
                FormatConversionCounts = new Dictionary<string, int>();
                StartTime = DateTime.UtcNow;
            }
        }

        #endregion;

        #region Built-in Converters;

        /// <summary>
        /// Base converter implementation;
        /// </summary>
        public abstract class BaseConverter : IFormatConverter;
        {
            public abstract string Name { get; }
            public abstract string Version { get; }
            public abstract string[] SupportedInputFormats { get; }
            public abstract string[] SupportedOutputFormats { get; }
            public abstract ConverterCapabilities Capabilities { get; }

            public virtual async Task<ConversionResult> ConvertAsync(ConversionRequest request, CancellationToken cancellationToken)
            {
                var result = new ConversionResult();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    // Validate request;
                    if (!ValidateRequest(request, out var errors))
                    {
                        return ConversionResult.FailureResult($"Validation failed: {string.Join(", ", errors)}");
                    }

                    // Perform conversion;
                    var output = await PerformConversionAsync(request, cancellationToken);

                    stopwatch.Stop();

                    result.Success = true;
                    result.Output = output;
                    result.Statistics.ProcessingTime = stopwatch.Elapsed;

                    // Calculate output size;
                    if (output is byte[] bytes)
                        result.OutputSize = bytes.Length;
                    else if (output is string filePath && File.Exists(filePath))
                        result.OutputSize = new FileInfo(filePath).Length;

                    // Calculate compression ratio;
                    if (request.Source.FileSize.HasValue && request.Source.FileSize > 0)
                    {
                        result.CompressionRatio = (float)result.OutputSize / request.Source.FileSize.Value;
                    }

                    // Calculate quality score;
                    result.QualityScore = CalculateQualityScore(request, output);

                    return result;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    result.Statistics.ProcessingTime = stopwatch.Elapsed;
                    return result;
                }
            }

            public virtual bool ValidateRequest(ConversionRequest request, out List<string> errors)
            {
                errors = new List<string>();

                if (request == null)
                {
                    errors.Add("Request is null");
                    return false;
                }

                if (request.Source == null)
                {
                    errors.Add("Source is null");
                    return false;
                }

                if (request.Target == null)
                {
                    errors.Add("Target is null");
                    return false;
                }

                // Check if input format is supported;
                if (!SupportedInputFormats.Contains(request.Source.Format, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Input format '{request.Source.Format}' is not supported");
                }

                // Check if output format is supported;
                if (!SupportedOutputFormats.Contains(request.Target.Format, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Output format '{request.Target.Format}' is not supported");
                }

                return errors.Count == 0;
            }

            public virtual FormatInfo GetFormatInfo(string format)
            {
                return new FormatInfo;
                {
                    Format = format,
                    Extensions = new[] { format.ToLowerInvariant() }
                };
            }

            public virtual long EstimateOutputSize(ConversionRequest request)
            {
                // Default estimation: assume same size as input;
                return request.Source.FileSize ?? 1024 * 1024; // 1MB default;
            }

            protected abstract Task<object> PerformConversionAsync(ConversionRequest request, CancellationToken cancellationToken);

            protected virtual float CalculateQualityScore(ConversionRequest request, object output)
            {
                // Default quality score based on target quality profile;
                return request.Target.Quality switch;
                {
                    QualityProfile.Low => 60f,
                    QualityProfile.Medium => 75f,
                    QualityProfile.High => 85f,
                    QualityProfile.Ultra => 95f,
                    QualityProfile.Lossless => 100f,
                    _ => 75f;
                };
            }
        }

        /// <summary>
        /// Image converter implementation;
        /// </summary>
        public class ImageConverter : BaseConverter;
        {
            public override string Name => "ImageConverter";
            public override string Version => "1.0.0";

            public override string[] SupportedInputFormats => new[]
            {
                "JPEG", "JPG", "PNG", "GIF", "BMP", "TIFF", "TIF",
                "WEBP", "AVIF", "HEIC", "HEIF", "ICO", "SVG"
            };

            public override string[] SupportedOutputFormats => new[]
            {
                "JPEG", "JPG", "PNG", "GIF", "BMP", "TIFF", "TIF",
                "WEBP", "AVIF", "ICO", "SVG"
            };

            public override ConverterCapabilities Capabilities => new ConverterCapabilities;
            {
                SupportsStreaming = true,
                SupportsParallelProcessing = true,
                SupportsProgressiveConversion = true,
                SupportsMetadata = true,
                SupportsTransparency = true,
                SupportsAnimation = true,
                SupportsVectorGraphics = true,
                MaxInputSize = 1024 * 1024 * 100, // 100MB;
                MaxOutputSize = 1024 * 1024 * 200, // 200MB;
                MaxDimensions = 32768;
            };

            protected override async Task<object> PerformConversionAsync(ConversionRequest request, CancellationToken cancellationToken)
            {
                // In a real implementation, this would use System.Drawing, ImageSharp, or similar;
                // For now, simulate conversion;
                await Task.Delay(1000, cancellationToken);

                if (request.Target.Type == TargetType.File)
                {
                    // Create dummy output file;
                    var outputPath = request.Target.FilePath;
                    File.WriteAllText(outputPath, "Simulated image conversion");
                    return outputPath;
                }
                else;
                {
                    // Return dummy bytes;
                    return new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG signature;
                }
            }
        }

        /// <summary>
        /// PDF converter implementation;
        /// </summary>
        public class PdfConverter : BaseConverter;
        {
            public override string Name => "PdfConverter";
            public override string Version => "1.0.0";

            public override string[] SupportedInputFormats => new[]
            {
                "PDF", "XPS", "EPUB", "MOBI"
            };

            public override string[] SupportedOutputFormats => new[]
            {
                "PDF", "DOCX", "DOC", "RTF", "TXT", "HTML",
                "JPEG", "PNG", "TIFF", "SVG"
            };

            public override ConverterCapabilities Capabilities => new ConverterCapabilities;
            {
                SupportsStreaming = false,
                SupportsParallelProcessing = false,
                SupportsProgressiveConversion = false,
                SupportsMetadata = true,
                SupportsTransparency = true,
                SupportsAnimation = false,
                SupportsVectorGraphics = true,
                MaxInputSize = 1024 * 1024 * 500, // 500MB;
                MaxOutputSize = 1024 * 1024 * 1000, // 1GB;
                MaxDimensions = 16384;
            };

            protected override async Task<object> PerformConversionAsync(ConversionRequest request, CancellationToken cancellationToken)
            {
                await Task.Delay(2000, cancellationToken);

                if (request.Target.Type == TargetType.File)
                {
                    var outputPath = request.Target.FilePath;
                    File.WriteAllText(outputPath, "Simulated PDF conversion");
                    return outputPath;
                }
                else;
                {
                    return new byte[] { 0x25, 0x50, 0x44, 0x46 }; // PDF signature;
                }
            }
        }

        // Additional built-in converters would be implemented similarly;
        public class DocumentConverter : BaseConverter { /* Implementation */ }
        public class AudioConverter : BaseConverter { /* Implementation */ }
        public class VideoConverter : BaseConverter { /* Implementation */ }
        public class ModelConverter : BaseConverter { /* Implementation */ }
        public class ArchiveConverter : BaseConverter { /* Implementation */ }

        #endregion;

        #region Helper Classes;

        /// <summary>
        /// Format detector;
        /// </summary>
        private class FormatDetector;
        {
            private readonly Dictionary<string, byte[]> _signatures;

            public FormatDetector()
            {
                _signatures = new Dictionary<string, byte[]>
                {
                    ["PNG"] = new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                    ["JPEG"] = new byte[] { 0xFF, 0xD8, 0xFF },
                    ["GIF"] = new byte[] { 0x47, 0x49, 0x46 },
                    ["BMP"] = new byte[] { 0x42, 0x4D },
                    ["PDF"] = new byte[] { 0x25, 0x50, 0x44, 0x46 },
                    ["ZIP"] = new byte[] { 0x50, 0x4B, 0x03, 0x04 },
                    ["GZIP"] = new byte[] { 0x1F, 0x8B },
                    ["MP3"] = new byte[] { 0x49, 0x44, 0x33 },
                    ["MP4"] = new byte[] { 0x66, 0x74, 0x79, 0x70 },
                    ["AVI"] = new byte[] { 0x52, 0x49, 0x46, 0x46 }
                };
            }

            public void Initialize()
            {
                // Additional initialization if needed;
            }

            public string DetectFromFile(string filePath)
            {
                using (var stream = File.OpenRead(filePath))
                {
                    return DetectFromStream(stream) ?? DetectFromExtension(filePath);
                }
            }

            public string DetectFromBytes(byte[] data)
            {
                using (var stream = new MemoryStream(data))
                {
                    return DetectFromStream(stream);
                }
            }

            public string DetectFromStream(Stream stream)
            {
                var originalPosition = stream.Position;
                stream.Position = 0;

                try
                {
                    // Read first 16 bytes for signature detection;
                    var buffer = new byte[16];
                    var bytesRead = stream.Read(buffer, 0, buffer.Length);

                    foreach (var signature in _signatures)
                    {
                        if (bytesRead >= signature.Value.Length)
                        {
                            bool match = true;
                            for (int i = 0; i < signature.Value.Length; i++)
                            {
                                if (buffer[i] != signature.Value[i])
                                {
                                    match = false;
                                    break;
                                }
                            }

                            if (match)
                                return signature.Key;
                        }
                    }

                    return null;
                }
                finally
                {
                    stream.Position = originalPosition;
                }
            }

            private string DetectFromExtension(string filePath)
            {
                var extension = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

                // Map extensions to formats;
                var extensionMap = new Dictionary<string, string>
                {
                    ["JPG"] = "JPEG",
                    ["JPEG"] = "JPEG",
                    ["PNG"] = "PNG",
                    ["GIF"] = "GIF",
                    ["BMP"] = "BMP",
                    ["TIF"] = "TIFF",
                    ["TIFF"] = "TIFF",
                    ["PDF"] = "PDF",
                    ["DOC"] = "DOC",
                    ["DOCX"] = "DOCX",
                    ["XLS"] = "XLS",
                    ["XLSX"] = "XLSX",
                    ["PPT"] = "PPT",
                    ["PPTX"] = "PPTX",
                    ["MP3"] = "MP3",
                    ["MP4"] = "MP4",
                    ["AVI"] = "AVI",
                    ["MKV"] = "MKV",
                    ["ZIP"] = "ZIP",
                    ["RAR"] = "RAR",
                    ["7Z"] = "7Z",
                    ["FBX"] = "FBX",
                    ["OBJ"] = "OBJ",
                    ["GLTF"] = "GLTF",
                    ["GLB"] = "GLB"
                };

                if (extensionMap.TryGetValue(extension, out var format))
                    return format;

                return extension; // Return extension as format name;
            }
        }

        /// <summary>
        /// Quality optimizer;
        /// </summary>
        private class QualityOptimizer;
        {
            public void Initialize()
            {
                // Initialization if needed;
            }

            public float Optimize(ConversionRequest request, float initialQuality)
            {
                // Apply quality optimization based on request options;
                if (!request.Options.OptimizeQuality)
                    return initialQuality;

                // Adjust quality based on target profile;
                return request.Target.Quality switch;
                {
                    QualityProfile.Low => Math.Min(initialQuality, 60f),
                    QualityProfile.Medium => Math.Min(initialQuality, 80f),
                    QualityProfile.High => Math.Min(initialQuality, 90f),
                    QualityProfile.Ultra => Math.Min(initialQuality, 95f),
                    QualityProfile.Lossless => 100f,
                    _ => initialQuality;
                };
            }
        }

        /// <summary>
        /// Performance monitor;
        /// </summary>
        private class PerformanceMonitor;
        {
            private DateTime _startTime;
            private long _totalConversions;
            private TimeSpan _totalProcessingTime;

            public PerformanceMonitor()
            {
                _startTime = DateTime.UtcNow;
                _totalConversions = 0;
                _totalProcessingTime = TimeSpan.Zero;
            }

            public void RecordConversion(TimeSpan processingTime)
            {
                _totalConversions++;
                _totalProcessingTime += processingTime;
            }

            public float GetAverageProcessingTime()
            {
                return _totalConversions > 0;
                    ? (float)_totalProcessingTime.TotalMilliseconds / _totalConversions;
                    : 0f;
            }

            public float GetConversionsPerSecond()
            {
                var uptime = DateTime.UtcNow - _startTime;
                return uptime.TotalSeconds > 0;
                    ? (float)_totalConversions / (float)uptime.TotalSeconds;
                    : 0f;
            }
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
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Cancel all active conversions;
                    lock (_conversionLock)
                    {
                        foreach (var context in _activeConversions.Values)
                        {
                            context.CancellationTokenSource.Cancel();
                        }
                        _activeConversions.Clear();
                    }

                    // Clear collections;
                    _converters.Clear();
                    _batchResults.Clear();
                    _registeredFormats.Clear();

                    // Clear events;
                    OnConversionStarted = null;
                    OnConversionCompleted = null;
                    OnConversionProgressChanged = null;
                    OnBatchConversionStarted = null;
                    OnBatchConversionCompleted = null;
                    OnConverterRegistered = null;
                    OnConverterUnregistered = null;
                    OnEngineError = null;
                }

                _isDisposed = true;
            }
        }

        ~ConverterEngine()
        {
            Dispose(false);
        }

        #endregion;
    }
}
