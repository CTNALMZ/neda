using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.ContentCreation.AssetPipeline.Common;

namespace NEDA.ContentCreation.AssetPipeline.QualityOptimizers;
{
    /// <summary>
    /// Advanced compression engine with multiple algorithms, adaptive compression,
    /// progress tracking, and optimization strategies;
    /// </summary>
    public class CompressionEngine : IDisposable
    {
        #region Nested Types;

        /// <summary>
        /// Compression request with source, target, and compression parameters;
        /// </summary>
        public class CompressionRequest;
        {
            /// <summary>
            /// Unique request identifier;
            /// </summary>
            public Guid Id { get; }

            /// <summary>
            /// Source to compress;
            /// </summary>
            public CompressionSource Source { get; set; }

            /// <summary>
            /// Target for compressed output;
            /// </summary>
            public CompressionTarget Target { get; set; }

            /// <summary>
            /// Compression options;
            /// </summary>
            public CompressionOptions Options { get; set; }

            /// <summary>
            /// Request priority;
            /// </summary>
            public CompressionPriority Priority { get; set; }

            /// <summary>
            /// Request status;
            /// </summary>
            public CompressionStatus Status { get; internal set; }

            /// <summary>
            /// Compression progress (0-100)
            /// </summary>
            public float Progress { get; internal set; }

            /// <summary>
            /// Compression result;
            /// </summary>
            public CompressionResult Result { get; internal set; }

            /// <summary>
            /// Error information if compression failed;
            /// </summary>
            public Exception Error { get; internal set; }

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

            public CompressionRequest()
            {
                Id = Guid.NewGuid();
                Options = new CompressionOptions();
                Priority = CompressionPriority.Normal;
                Status = CompressionStatus.Pending;
                Progress = 0f;
                CreatedAt = DateTime.UtcNow;
            }

            public CompressionRequest(CompressionSource source, CompressionTarget target) : this()
            {
                Source = source ?? throw new ArgumentNullException(nameof(source));
                Target = target ?? throw new ArgumentNullException(nameof(target));
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
        /// Compression source specification;
        /// </summary>
        public class CompressionSource;
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
            /// Source format;
            /// </summary>
            public string Format { get; set; }

            /// <summary>
            /// Source size in bytes;
            /// </summary>
            public long Size { get; internal set; }

            /// <summary>
            /// Source hash for integrity checking;
            /// </summary>
            public string Hash { get; internal set; }

            /// <summary>
            /// Source metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; }

            public CompressionSource()
            {
                Metadata = new Dictionary<string, object>();
            }

            public CompressionSource(string filePath) : this()
            {
                Type = SourceType.File;
                FilePath = filePath;
                UpdateFromFile();
            }

            public CompressionSource(byte[] data) : this()
            {
                Type = SourceType.Memory;
                Data = data;
                Size = data?.Length ?? 0;
            }

            public CompressionSource(Stream stream) : this()
            {
                Type = SourceType.Stream;
                Stream = stream;
                Size = stream?.Length ?? 0;
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

                if (Size <= 0)
                    errors.Add("Source size must be positive");

                return errors.Count == 0;
            }

            /// <summary>
            /// Update source information from file;
            /// </summary>
            public void UpdateFromFile()
            {
                if (Type != SourceType.File || !File.Exists(FilePath))
                    return;

                var fileInfo = new FileInfo(FilePath);
                Size = fileInfo.Length;

                // Calculate hash;
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(FilePath))
                {
                    Hash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
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
        }

        /// <summary>
        /// Compression target specification;
        /// </summary>
        public class CompressionTarget;
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
            /// Target stream;
            /// </summary>
            public Stream Stream { get; set; }

            /// <summary>
            /// Target compression format;
            /// </summary>
            public CompressionFormat Format { get; set; }

            /// <summary>
            /// Target metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; }

            public CompressionTarget()
            {
                Type = TargetType.File;
                Format = CompressionFormat.GZip;
                Metadata = new Dictionary<string, object>();
            }

            public CompressionTarget(string filePath, CompressionFormat format) : this()
            {
                FilePath = filePath;
                Format = format;
            }

            public CompressionTarget(Stream stream, CompressionFormat format) : this()
            {
                Type = TargetType.Stream;
                Stream = stream;
                Format = format;
            }

            /// <summary>
            /// Validate target;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (Type == TargetType.File && string.IsNullOrWhiteSpace(FilePath))
                    errors.Add("FilePath is required for file target");

                if (Type == TargetType.Stream && Stream == null)
                    errors.Add("Stream is required for stream target");

                if (!Enum.IsDefined(typeof(CompressionFormat), Format))
                    errors.Add($"Invalid compression format: {Format}");

                return errors.Count == 0;
            }

            /// <summary>
            /// Get target stream for writing;
            /// </summary>
            public Stream GetStream()
            {
                if (Type == TargetType.File)
                {
                    var directory = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    return File.Create(FilePath);
                }
                else if (Type == TargetType.Stream)
                {
                    return Stream;
                }

                throw new InvalidOperationException($"Unsupported target type: {Type}");
            }
        }

        /// <summary>
        /// Compression options;
        /// </summary>
        public class CompressionOptions;
        {
            /// <summary>
            /// Compression algorithm to use;
            /// </summary>
            public CompressionAlgorithm Algorithm { get; set; }

            /// <summary>
            /// Compression level;
            /// </summary>
            public CompressionLevel Level { get; set; }

            /// <summary>
            /// Enable parallel compression;
            /// </summary>
            public bool EnableParallelCompression { get; set; }

            /// <summary>
            /// Maximum degree of parallelism;
            /// </summary>
            public int MaxDegreeOfParallelism { get; set; }

            /// <summary>
            /// Compression block size in bytes;
            /// </summary>
            public int BlockSize { get; set; }

            /// <summary>
            /// Enable dictionary compression;
            /// </summary>
            public bool EnableDictionary { get; set; }

            /// <summary>
            /// Dictionary size in bytes;
            /// </summary>
            public int DictionarySize { get; set; }

            /// <summary>
            /// Enable checksum verification;
            /// </summary>
            public bool EnableChecksum { get; set; }

            /// <summary>
            /// Checksum algorithm;
            /// </summary>
            public ChecksumAlgorithm ChecksumAlgorithm { get; set; }

            /// <summary>
            /// Enable encryption;
            /// </summary>
            public bool EnableEncryption { get; set; }

            /// <summary>
            /// Encryption algorithm;
            /// </summary>
            public EncryptionAlgorithm EncryptionAlgorithm { get; set; }

            /// <summary>
            /// Encryption key (for AES)
            /// </summary>
            public byte[] EncryptionKey { get; set; }

            /// <summary>
            /// Encryption IV (for AES)
            /// </summary>
            public byte[] EncryptionIV { get; set; }

            /// <summary>
            /// Enable adaptive compression;
            /// </summary>
            public bool EnableAdaptiveCompression { get; set; }

            /// <summary>
            /// Adaptive compression threshold in bytes;
            /// </summary>
            public long AdaptiveThreshold { get; set; }

            /// <summary>
            /// Compression timeout;
            /// </summary>
            public TimeSpan Timeout { get; set; }

            /// <summary>
            /// Maximum memory usage in bytes;
            /// </summary>
            public long MaxMemoryUsage { get; set; }

            /// <summary>
            /// Preserve file attributes;
            /// </summary>
            public bool PreserveAttributes { get; set; }

            /// <summary>
            /// Preserve directory structure;
            /// </summary>
            public bool PreserveDirectoryStructure { get; set; }

            /// <summary>
            /// Overwrite existing files;
            /// </summary>
            public bool OverwriteExisting { get; set; }

            /// <summary>
            /// Create backup of original file;
            /// </summary>
            public bool CreateBackup { get; set; }

            /// <summary>
            /// Custom compression parameters;
            /// </summary>
            public Dictionary<string, object> Parameters { get; }

            public CompressionOptions()
            {
                Algorithm = CompressionAlgorithm.Deflate;
                Level = CompressionLevel.Optimal;
                EnableParallelCompression = true;
                MaxDegreeOfParallelism = Environment.ProcessorCount;
                BlockSize = 1024 * 1024; // 1MB;
                EnableDictionary = true;
                DictionarySize = 32 * 1024; // 32KB;
                EnableChecksum = true;
                ChecksumAlgorithm = ChecksumAlgorithm.CRC32;
                EnableEncryption = false;
                EncryptionAlgorithm = EncryptionAlgorithm.AES256;
                EnableAdaptiveCompression = true;
                AdaptiveThreshold = 1024 * 1024 * 10; // 10MB;
                Timeout = TimeSpan.FromMinutes(5);
                MaxMemoryUsage = 1024 * 1024 * 100; // 100MB;
                PreserveAttributes = true;
                PreserveDirectoryStructure = true;
                OverwriteExisting = true;
                CreateBackup = false;
                Parameters = new Dictionary<string, object>();
            }

            /// <summary>
            /// Validate options;
            /// </summary>
            public bool Validate(out List<string> errors)
            {
                errors = new List<string>();

                if (!Enum.IsDefined(typeof(CompressionAlgorithm), Algorithm))
                    errors.Add($"Invalid compression algorithm: {Algorithm}");

                if (!Enum.IsDefined(typeof(CompressionLevel), Level))
                    errors.Add($"Invalid compression level: {Level}");

                if (MaxDegreeOfParallelism < 1)
                    errors.Add("MaxDegreeOfParallelism must be at least 1");

                if (BlockSize < 1024)
                    errors.Add("BlockSize must be at least 1KB");

                if (DictionarySize < 0)
                    errors.Add("DictionarySize cannot be negative");

                if (!Enum.IsDefined(typeof(ChecksumAlgorithm), ChecksumAlgorithm))
                    errors.Add($"Invalid checksum algorithm: {ChecksumAlgorithm}");

                if (EnableEncryption)
                {
                    if (!Enum.IsDefined(typeof(EncryptionAlgorithm), EncryptionAlgorithm))
                        errors.Add($"Invalid encryption algorithm: {EncryptionAlgorithm}");

                    if (EncryptionAlgorithm == EncryptionAlgorithm.AES256 &&
                        (EncryptionKey == null || EncryptionKey.Length != 32))
                        errors.Add("AES256 requires a 32-byte (256-bit) key");
                }

                if (AdaptiveThreshold < 0)
                    errors.Add("AdaptiveThreshold cannot be negative");

                if (Timeout <= TimeSpan.Zero)
                    errors.Add("Timeout must be positive");

                if (MaxMemoryUsage < 1024 * 1024) // 1MB;
                    errors.Add("MaxMemoryUsage must be at least 1MB");

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
        /// Compression result;
        /// </summary>
        public class CompressionResult;
        {
            /// <summary>
            /// Success status;
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// Compressed output;
            /// </summary>
            public object Output { get; set; }

            /// <summary>
            /// Original size in bytes;
            /// </summary>
            public long OriginalSize { get; set; }

            /// <summary>
            /// Compressed size in bytes;
            /// </summary>
            public long CompressedSize { get; set; }

            /// <summary>
            /// Compression ratio (0-1)
            /// </summary>
            public float CompressionRatio { get; set; }

            /// <summary>
            /// Space saved in bytes;
            /// </summary>
            public long SpaceSaved { get; set; }

            /// <summary>
            /// Space saved percentage;
            /// </summary>
            public float SpaceSavedPercentage { get; set; }

            /// <summary>
            /// Processing statistics;
            /// </summary>
            public CompressionStatistics Statistics { get; set; }

            /// <summary>
            /// Compression metadata;
            /// </summary>
            public Dictionary<string, object> Metadata { get; set; }

            /// <summary>
            /// Error message if failed;
            /// </summary>
            public string ErrorMessage { get; set; }

            /// <summary>
            /// Warnings during compression;
            /// </summary>
            public List<string> Warnings { get; set; }

            public CompressionResult()
            {
                Success = false;
                Statistics = new CompressionStatistics();
                Metadata = new Dictionary<string, object>();
                Warnings = new List<string>();
            }

            public static CompressionResult SuccessResult(object output, long originalSize, long compressedSize)
            {
                var ratio = originalSize > 0 ? (float)compressedSize / originalSize : 0;
                var saved = originalSize - compressedSize;
                var savedPercentage = originalSize > 0 ? (float)saved / originalSize * 100 : 0;

                return new CompressionResult;
                {
                    Success = true,
                    Output = output,
                    OriginalSize = originalSize,
                    CompressedSize = compressedSize,
                    CompressionRatio = ratio,
                    SpaceSaved = saved,
                    SpaceSavedPercentage = savedPercentage;
                };
            }

            public static CompressionResult FailureResult(string error)
            {
                return new CompressionResult;
                {
                    Success = false,
                    ErrorMessage = error;
                };
            }

            /// <summary>
            /// Add warning;
            /// </summary>
            public void AddWarning(string warning)
            {
                if (!string.IsNullOrWhiteSpace(warning))
                    Warnings.Add(warning);
            }

            /// <summary>
            /// Add metadata;
            /// </summary>
            public void AddMetadata(string key, object value)
            {
                Metadata[key] = value;
            }
        }

        /// <summary>
        /// Compression statistics;
        /// </summary>
        public class CompressionStatistics;
        {
            public TimeSpan CompressionTime { get; set; }
            public TimeSpan DecompressionTime { get; set; }
            public long PeakMemoryUsage { get; set; }
            public float AverageCompressionSpeed { get; set; } // MB/s;
            public float AverageDecompressionSpeed { get; set; } // MB/s;
            public int BlocksCompressed { get; set; }
            public int BlocksDecompressed { get; set; }
            public long TotalBytesProcessed { get; set; }
            public int RetryCount { get; set; }
            public Dictionary<string, object> AlgorithmStatistics { get; set; }

            public CompressionStatistics()
            {
                AlgorithmStatistics = new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Batch compression request;
        /// </summary>
        public class BatchCompressionRequest;
        {
            public List<CompressionRequest> Requests { get; }
            public BatchOptions Options { get; }
            public string Name { get; set; }
            public string Description { get; set; }
            public Guid Id { get; }

            public BatchCompressionRequest()
            {
                Id = Guid.NewGuid();
                Requests = new List<CompressionRequest>();
                Options = new BatchOptions();
            }

            public BatchCompressionRequest(string name) : this()
            {
                Name = name;
            }

            public void AddRequest(CompressionRequest request)
            {
                Requests.Add(request);
            }

            public void AddRequests(IEnumerable<CompressionRequest> requests)
            {
                Requests.AddRange(requests);
            }
        }

        /// <summary>
        /// Batch compression options;
        /// </summary>
        public class BatchOptions;
        {
            public bool StopOnError { get; set; } = false;
            public int MaxConcurrentCompressions { get; set; } = Environment.ProcessorCount;
            public bool GenerateReport { get; set; } = true;
            public string ReportFormat { get; set; } = "JSON";
            public bool PreserveDirectoryStructure { get; set; } = true;
            public string OutputDirectory { get; set; }
            public bool OverwriteExisting { get; set; } = true;
            public CompressionFormat DefaultFormat { get; set; } = CompressionFormat.GZip;
            public CompressionLevel DefaultLevel { get; set; } = CompressionLevel.Optimal;
        }

        /// <summary>
        /// Batch compression result;
        /// </summary>
        public class BatchCompressionResult;
        {
            public Guid BatchId { get; set; }
            public string BatchName { get; set; }
            public BatchStatus Status { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime? CompletionTime { get; set; }
            public TimeSpan? ExecutionTime { get; set; }
            public int TotalRequests { get; set; }
            public int SuccessfulCompressions { get; set; }
            public int FailedCompressions { get; set; }
            public int SkippedCompressions { get; set; }
            public List<CompressionResult> Results { get; set; }
            public BatchStatistics Statistics { get; set; }
            public string ReportPath { get; set; }
            public Exception Error { get; set; }

            public BatchCompressionResult()
            {
                Results = new List<CompressionResult>();
                Statistics = new BatchStatistics();
            }
        }

        /// <summary>
        /// Batch statistics;
        /// </summary>
        public class BatchStatistics;
        {
            public long TotalOriginalSize { get; set; }
            public long TotalCompressedSize { get; set; }
            public float TotalCompressionRatio { get; set; }
            public long TotalSpaceSaved { get; set; }
            public TimeSpan TotalCompressionTime { get; set; }
            public long PeakMemoryUsage { get; set; }
            public Dictionary<CompressionFormat, int> FormatCounts { get; set; }
            public Dictionary<CompressionAlgorithm, int> AlgorithmCounts { get; set; }

            public BatchStatistics()
            {
                FormatCounts = new Dictionary<CompressionFormat, int>();
                AlgorithmCounts = new Dictionary<CompressionAlgorithm, int>();
            }
        }

        /// <summary>
        /// Compression algorithm interface;
        /// </summary>
        public interface ICompressionAlgorithm;
        {
            /// <summary>
            /// Algorithm name;
            /// </summary>
            string Name { get; }

            /// <summary>
            /// Algorithm version;
            /// </summary>
            string Version { get; }

            /// <summary>
            /// Supported compression formats;
            /// </summary>
            CompressionFormat[] SupportedFormats { get; }

            /// <summary>
            /// Algorithm capabilities;
            /// </summary>
            AlgorithmCapabilities Capabilities { get; }

            /// <summary>
            /// Compress data;
            /// </summary>
            Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken);

            /// <summary>
            /// Decompress data;
            /// </summary>
            Task<CompressionResult> DecompressAsync(CompressionRequest request, CancellationToken cancellationToken);

            /// <summary>
            /// Estimate compression ratio;
            /// </summary>
            float EstimateCompressionRatio(CompressionRequest request);

            /// <summary>
            /// Get algorithm information;
            /// </summary>
            AlgorithmInfo GetAlgorithmInfo();
        }

        /// <summary>
        /// Algorithm capabilities;
        /// </summary>
        public class AlgorithmCapabilities;
        {
            public bool SupportsStreaming { get; set; }
            public bool SupportsParallelCompression { get; set; }
            public bool SupportsEncryption { get; set; }
            public bool SupportsChecksum { get; set; }
            public bool SupportsDictionary { get; set; }
            public bool SupportsAdaptiveCompression { get; set; }
            public long MaxInputSize { get; set; } = long.MaxValue;
            public long MaxOutputSize { get; set; } = long.MaxValue;
            public int MaxBlockSize { get; set; } = 1024 * 1024 * 1024; // 1GB;
            public int MinBlockSize { get; set; } = 1024; // 1KB;
        }

        /// <summary>
        /// Algorithm information;
        /// </summary>
        public class AlgorithmInfo;
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string Developer { get; set; }
            public string License { get; set; }
            public bool IsLossless { get; set; }
            public bool IsOpenSource { get; set; }
            public float TypicalCompressionRatio { get; set; }
            public float TypicalCompressionSpeed { get; set; } // MB/s;
            public float TypicalDecompressionSpeed { get; set; } // MB/s;
            public string[] CommonUseCases { get; set; }
        }

        /// <summary>
        /// Compression context for algorithm execution;
        /// </summary>
        private class CompressionContext;
        {
            public CompressionRequest Request { get; }
            public ICompressionAlgorithm Algorithm { get; }
            public CancellationTokenSource CancellationTokenSource { get; }
            public Task<CompressionResult> Task { get; set; }
            public DateTime StartTime { get; }
            public volatile bool IsCompleted;
            public volatile bool IsCancelled;

            public CompressionContext(CompressionRequest request, ICompressionAlgorithm algorithm)
            {
                Request = request;
                Algorithm = algorithm;
                CancellationTokenSource = new CancellationTokenSource();
                StartTime = DateTime.UtcNow;
                IsCompleted = false;
                IsCancelled = false;
            }
        }

        /// <summary>
        /// Algorithm registry entry
        /// </summary>
        private class AlgorithmRegistryEntry
        {
            public ICompressionAlgorithm Algorithm { get; }
            public DateTime RegisteredAt { get; }
            public int UsageCount { get; set; }
            public DateTime LastUsed { get; set; }
            public bool IsEnabled { get; set; }

            public AlgorithmRegistryEntry(ICompressionAlgorithm algorithm)
            {
                Algorithm = algorithm;
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

        public enum CompressionStatus;
        {
            Pending,
            Compressing,
            Decompressing,
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

        public enum CompressionPriority;
        {
            Low,
            Normal,
            High,
            Critical;
        }

        public enum CompressionFormat;
        {
            GZip,
            Deflate,
            Brotli,
            Zstd,
            LZ4,
            LZMA,
            BZip2,
            LZF,
            Snappy,
            Zlib,
            Tar,
            Zip,
            SevenZip,
            Rar,
            Custom;
        }

        public enum CompressionAlgorithm;
        {
            Deflate,
            GZip,
            Brotli,
            Zstd,
            LZ4,
            LZMA,
            BZip2,
            LZF,
            Snappy,
            Zlib,
            Adaptive,
            Auto;
        }

        public enum CompressionLevel;
        {
            NoCompression,
            Fastest,
            Optimal,
            SmallestSize,
            Custom;
        }

        public enum ChecksumAlgorithm;
        {
            None,
            CRC32,
            Adler32,
            MD5,
            SHA1,
            SHA256,
            SHA512;
        }

        public enum EncryptionAlgorithm;
        {
            None,
            AES128,
            AES256,
            Blowfish,
            Twofish,
            ChaCha20,
            Custom;
        }

        #endregion;

        #region Fields;

        private readonly Dictionary<string, AlgorithmRegistryEntry> _algorithms;
        private readonly Dictionary<Guid, CompressionContext> _activeCompressions;
        private readonly Dictionary<Guid, BatchCompressionResult> _batchResults;
        private readonly ILogger _logger;
        private readonly object _algorithmLock = new object();
        private readonly object _compressionLock = new object();
        private bool _isDisposed;

        // Services;
        private MemoryPool _memoryPool;
        private ProgressTracker _progressTracker;
        private StatisticsCollector _statisticsCollector;

        // Configuration;
        private EngineConfiguration _configuration;
        private EngineStatistics _statistics;

        // Adaptive compression;
        private AdaptiveCompressionEngine _adaptiveEngine;
        private CompressionAnalyzer _compressionAnalyzer;

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
        /// Registered algorithm count;
        /// </summary>
        public int AlgorithmCount => _algorithms.Count;

        /// <summary>
        /// Active compression count;
        /// </summary>
        public int ActiveCompressionCount => _activeCompressions.Count;

        /// <summary>
        /// Memory pool utilization;
        /// </summary>
        public float MemoryPoolUtilization => _memoryPool?.Utilization ?? 0f;

        /// <summary>
        /// Is engine initialized;
        /// </summary>
        public bool IsInitialized { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Compression started event;
        /// </summary>
        public event EventHandler<CompressionRequest> OnCompressionStarted;

        /// <summary>
        /// Compression completed event;
        /// </summary>
        public event EventHandler<CompressionResultEventArgs> OnCompressionCompleted;

        /// <summary>
        /// Compression progress changed event;
        /// </summary>
        public event EventHandler<CompressionProgressEventArgs> OnCompressionProgressChanged;

        /// <summary>
        /// Batch compression started event;
        /// </summary>
        public event EventHandler<BatchCompressionRequest> OnBatchCompressionStarted;

        /// <summary>
        /// Batch compression completed event;
        /// </summary>
        public event EventHandler<BatchCompressionResult> OnBatchCompressionCompleted;

        /// <summary>
        /// Algorithm registered event;
        /// </summary>
        public event EventHandler<ICompressionAlgorithm> OnAlgorithmRegistered;

        /// <summary>
        /// Algorithm unregistered event;
        /// </summary>
        public event EventHandler<ICompressionAlgorithm> OnAlgorithmUnregistered;

        /// <summary>
        /// Engine error event;
        /// </summary>
        public event EventHandler<EngineErrorEventArgs> OnEngineError;

        /// <summary>
        /// Memory pool warning event;
        /// </summary>
        public event EventHandler<MemoryPoolWarningEventArgs> OnMemoryPoolWarning;

        /// <summary>
        /// Event args classes;
        /// </summary>
        public class CompressionResultEventArgs : EventArgs;
        {
            public CompressionRequest Request { get; set; }
            public CompressionResult Result { get; set; }
        }

        public class CompressionProgressEventArgs : EventArgs;
        {
            public Guid RequestId { get; set; }
            public float Progress { get; set; }
            public CompressionStatus Status { get; set; }
            public string CurrentOperation { get; set; }
            public long BytesProcessed { get; set; }
            public long TotalBytes { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class EngineErrorEventArgs : EventArgs;
        {
            public Exception Error { get; set; }
            public string Context { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class MemoryPoolWarningEventArgs : EventArgs;
        {
            public float Utilization { get; set; }
            public float Threshold { get; set; }
            public string WarningMessage { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion;

        #region Constructor;

        /// <summary>
        /// Create compression engine with default configuration;
        /// </summary>
        public CompressionEngine() : this(new EngineConfiguration())
        {
        }

        /// <summary>
        /// Create compression engine with custom configuration;
        /// </summary>
        public CompressionEngine(EngineConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _algorithms = new Dictionary<string, AlgorithmRegistryEntry>(StringComparer.OrdinalIgnoreCase);
            _activeCompressions = new Dictionary<Guid, CompressionContext>();
            _batchResults = new Dictionary<Guid, BatchCompressionResult>();

            _logger = LogManager.GetLogger("CompressionEngine");
            _memoryPool = new MemoryPool(_configuration.MemoryPoolSize);
            _progressTracker = new ProgressTracker();
            _statisticsCollector = new StatisticsCollector();

            _adaptiveEngine = new AdaptiveCompressionEngine();
            _compressionAnalyzer = new CompressionAnalyzer();

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
                // Load built-in algorithms;
                LoadBuiltInAlgorithms();

                // Initialize services;
                _memoryPool.Initialize();
                _adaptiveEngine.Initialize();
                _compressionAnalyzer.Initialize();

                // Start monitoring tasks;
                StartMonitoringTasks();

                IsInitialized = true;
                _logger.Info($"CompressionEngine initialized with {_algorithms.Count} algorithms");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize CompressionEngine: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Load built-in compression algorithms;
        /// </summary>
        private void LoadBuiltInAlgorithms()
        {
            // Register standard algorithms;
            RegisterAlgorithm(new GZipAlgorithm());
            RegisterAlgorithm(new DeflateAlgorithm());
            RegisterAlgorithm(new BrotliAlgorithm());
            RegisterAlgorithm(new ZstdAlgorithm());
            RegisterAlgorithm(new LZ4Algorithm());
            RegisterAlgorithm(new BZip2Algorithm());
            RegisterAlgorithm(new LZMAAlgorithm());
            RegisterAlgorithm(new ZipArchiveAlgorithm());

            _logger.Info($"Loaded {_algorithms.Count} built-in compression algorithms");
        }

        /// <summary>
        /// Start monitoring tasks;
        /// </summary>
        private void StartMonitoringTasks()
        {
            // Start memory pool monitoring;
            Task.Run(async () => await MonitorMemoryPoolAsync());

            // Start statistics collection;
            Task.Run(async () => await CollectStatisticsAsync());
        }

        #endregion;

        #region Algorithm Management;

        /// <summary>
        /// Register compression algorithm;
        /// </summary>
        public bool RegisterAlgorithm(ICompressionAlgorithm algorithm)
        {
            if (algorithm == null)
                throw new ArgumentNullException(nameof(algorithm));

            lock (_algorithmLock)
            {
                var key = $"{algorithm.Name}_{algorithm.Version}";

                if (_algorithms.ContainsKey(key))
                {
                    _logger.Warning($"Compression algorithm '{key}' is already registered");
                    return false;
                }

                var entry = new AlgorithmRegistryEntry(algorithm);
                _algorithms[key] = entry

                _logger.Info($"Registered compression algorithm: {algorithm.Name} v{algorithm.Version}");
                OnAlgorithmRegistered?.Invoke(this, algorithm);

                return true;
            }
        }

        /// <summary>
        /// Unregister compression algorithm;
        /// </summary>
        public bool UnregisterAlgorithm(string algorithmName, string version = null)
        {
            lock (_algorithmLock)
            {
                var key = string.IsNullOrEmpty(version)
                    ? _algorithms.Keys.FirstOrDefault(k => k.StartsWith(algorithmName, StringComparison.OrdinalIgnoreCase))
                    : $"{algorithmName}_{version}";

                if (key != null && _algorithms.TryGetValue(key, out var entry))
                {
                    _algorithms.Remove(key);
                    _logger.Info($"Unregistered compression algorithm: {algorithmName}");
                    OnAlgorithmUnregistered?.Invoke(this, entry.Algorithm);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Get algorithm by name;
        /// </summary>
        public ICompressionAlgorithm GetAlgorithm(string algorithmName, string version = null)
        {
            lock (_algorithmLock)
            {
                var key = string.IsNullOrEmpty(version)
                    ? _algorithms.Keys.FirstOrDefault(k => k.StartsWith(algorithmName, StringComparison.OrdinalIgnoreCase))
                    : $"{algorithmName}_{version}";

                if (key != null && _algorithms.TryGetValue(key, out var entry))
                {
                    return entry.Algorithm;
                }

                return null;
            }
        }

        /// <summary>
        /// Get all registered algorithms;
        /// </summary>
        public List<ICompressionAlgorithm> GetAlgorithms()
        {
            lock (_algorithmLock)
            {
                return _algorithms.Values.Select(e => e.Algorithm).ToList();
            }
        }

        /// <summary>
        /// Get algorithms for specific format;
        /// </summary>
        public List<ICompressionAlgorithm> GetAlgorithmsForFormat(CompressionFormat format)
        {
            lock (_algorithmLock)
            {
                return _algorithms.Values;
                    .Where(e => e.IsEnabled)
                    .Select(e => e.Algorithm)
                    .Where(a => a.SupportedFormats.Contains(format))
                    .ToList();
            }
        }

        /// <summary>
        /// Enable/disable algorithm;
        /// </summary>
        public bool SetAlgorithmEnabled(string algorithmName, string version, bool enabled)
        {
            lock (_algorithmLock)
            {
                var key = $"{algorithmName}_{version}";
                if (_algorithms.TryGetValue(key, out var entry))
                {
                    entry.IsEnabled = enabled;
                    _logger.Info($"Algorithm '{algorithmName}' {(enabled ? "enabled" : "disabled")}");
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Find best algorithm for request;
        /// </summary>
        private ICompressionAlgorithm FindBestAlgorithm(CompressionRequest request)
        {
            lock (_algorithmLock)
            {
                // If algorithm is specified, use it;
                if (request.Options.Algorithm != CompressionAlgorithm.Auto &&
                    request.Options.Algorithm != CompressionAlgorithm.Adaptive)
                {
                    var algorithm = _algorithms.Values;
                        .Where(e => e.IsEnabled)
                        .Select(e => e.Algorithm)
                        .FirstOrDefault(a => a.Name.Equals(request.Options.Algorithm.ToString(), StringComparison.OrdinalIgnoreCase));

                    if (algorithm != null)
                    {
                        UpdateAlgorithmUsage(algorithm);
                        return algorithm;
                    }
                }

                // Use adaptive compression if enabled;
                if (request.Options.EnableAdaptiveCompression &&
                    request.Source.Size >= request.Options.AdaptiveThreshold)
                {
                    var adaptiveAlgorithm = _adaptiveEngine.SelectAlgorithm(request, _algorithms.Values;
                        .Where(e => e.IsEnabled)
                        .Select(e => e.Algorithm)
                        .ToList());

                    if (adaptiveAlgorithm != null)
                    {
                        UpdateAlgorithmUsage(adaptiveAlgorithm);
                        return adaptiveAlgorithm;
                    }
                }

                // Default to GZip;
                var defaultAlgorithm = _algorithms.Values;
                    .Where(e => e.IsEnabled)
                    .Select(e => e.Algorithm)
                    .FirstOrDefault(a => a.Name.Equals("GZip", StringComparison.OrdinalIgnoreCase));

                if (defaultAlgorithm != null)
                {
                    UpdateAlgorithmUsage(defaultAlgorithm);
                    return defaultAlgorithm;
                }

                // Fallback to first available algorithm;
                var fallbackAlgorithm = _algorithms.Values;
                    .Where(e => e.IsEnabled)
                    .Select(e => e.Algorithm)
                    .FirstOrDefault();

                return fallbackAlgorithm;
            }
        }

        /// <summary>
        /// Update algorithm usage statistics;
        /// </summary>
        private void UpdateAlgorithmUsage(ICompressionAlgorithm algorithm)
        {
            lock (_algorithmLock)
            {
                var entry = _algorithms.Values.FirstOrDefault(e => e.Algorithm == algorithm);
                if (entry != null)
                {
                    entry.UsageCount++;
                    entry.LastUsed = DateTime.UtcNow;
                }
            }
        }

        #endregion;

        #region Compression Methods;

        /// <summary>
        /// Compress data;
        /// </summary>
        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!request.Validate(out var errors))
                throw new ArgumentException($"Invalid compression request: {string.Join(", ", errors)}");

            // Check if source is already compressed;
            if (IsAlreadyCompressed(request.Source) && !request.Options.GetParameter<bool>("ForceCompression", false))
            {
                var result = CompressionResult.SuccessResult(request.Source.GetBytes(), request.Source.Size, request.Source.Size);
                result.AddWarning("Source appears to already be compressed");
                return result;
            }

            // Find appropriate algorithm;
            var algorithm = FindBestAlgorithm(request);
            if (algorithm == null)
            {
                return CompressionResult.FailureResult(
                    $"No compression algorithm found for format: {request.Target.Format}");
            }

            // Validate request with algorithm;
            if (!ValidateRequestWithAlgorithm(request, algorithm))
            {
                return CompressionResult.FailureResult(
                    $"Request validation failed for algorithm: {algorithm.Name}");
            }

            // Check memory requirements;
            var estimatedMemory = EstimateMemoryUsage(request, algorithm);
            if (estimatedMemory > request.Options.MaxMemoryUsage)
            {
                return CompressionResult.FailureResult(
                    $"Estimated memory usage ({estimatedMemory} bytes) exceeds maximum ({request.Options.MaxMemoryUsage} bytes)");
            }

            // Create compression context;
            var context = new CompressionContext(request, algorithm);

            lock (_compressionLock)
            {
                _activeCompressions[request.Id] = context;
            }

            request.Status = CompressionStatus.Compressing;
            request.StartTime = DateTime.UtcNow;

            _logger.Info($"Starting compression: {request.Source.Size} bytes -> {algorithm.Name}");
            OnCompressionStarted?.Invoke(this, request);

            var result = new CompressionResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Execute compression with timeout;
                var timeoutTokenSource = new CancellationTokenSource(request.Options.Timeout);
                var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutTokenSource.Token, context.CancellationTokenSource.Token);

                // Monitor progress;
                var progressTask = MonitorCompressionProgressAsync(request, linkedTokenSource.Token);

                // Execute compression;
                var compressionTask = algorithm.CompressAsync(request, linkedTokenSource.Token);
                result = await compressionTask;

                // Update statistics;
                stopwatch.Stop();
                result.Statistics.CompressionTime = stopwatch.Elapsed;
                result.Statistics.AverageCompressionSpeed = request.Source.Size > 0;
                    ? (float)(request.Source.Size / 1024.0 / 1024.0) / (float)stopwatch.Elapsed.TotalSeconds;
                    : 0;

                // Update request with result;
                request.CompletionTime = DateTime.UtcNow;
                request.Result = result;
                request.Status = result.Success ? CompressionStatus.Completed : CompressionStatus.Failed;
                request.Error = result.Success ? null : new Exception(result.ErrorMessage);

                // Update engine statistics;
                UpdateStatistics(result, true);

                // Fire completion event;
                OnCompressionCompleted?.Invoke(this, new CompressionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });

                if (result.Success)
                {
                    _logger.Info($"Compression completed: {request.Source.Size} -> {result.CompressedSize} bytes " +
                                $"({result.SpaceSavedPercentage:F1}% saved) in {stopwatch.Elapsed.TotalSeconds:F2}s");
                }
                else;
                {
                    _logger.Error($"Compression failed: {result.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                request.Status = CompressionStatus.Cancelled;
                request.Error = new OperationCanceledException("Compression cancelled");

                result = CompressionResult.FailureResult("Compression cancelled");
                request.Result = result;

                _logger.Info("Compression cancelled");
                OnCompressionCompleted?.Invoke(this, new CompressionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });
            }
            catch (Exception ex)
            {
                request.Status = CompressionStatus.Failed;
                request.Error = ex;

                result = CompressionResult.FailureResult($"Compression error: {ex.Message}");
                request.Result = result;

                _logger.Error($"Compression error: {ex.Message}", ex);
                OnCompressionCompleted?.Invoke(this, new CompressionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });

                OnEngineError?.Invoke(this, new EngineErrorEventArgs;
                {
                    Error = ex,
                    Context = "Compression",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                context.IsCompleted = true;
                lock (_compressionLock)
                {
                    _activeCompressions.Remove(request.Id);
                }

                // Return memory to pool;
                _memoryPool.Return(estimatedMemory);
            }

            return result;
        }

        /// <summary>
        /// Decompress data;
        /// </summary>
        public async Task<CompressionResult> DecompressAsync(CompressionRequest request, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            // Find appropriate algorithm;
            var algorithm = FindDecompressionAlgorithm(request);
            if (algorithm == null)
            {
                return CompressionResult.FailureResult(
                    $"No decompression algorithm found for format: {request.Target.Format}");
            }

            // Create compression context;
            var context = new CompressionContext(request, algorithm);

            lock (_compressionLock)
            {
                _activeCompressions[request.Id] = context;
            }

            request.Status = CompressionStatus.Decompressing;
            request.StartTime = DateTime.UtcNow;

            _logger.Info($"Starting decompression with {algorithm.Name}");
            OnCompressionStarted?.Invoke(this, request);

            var result = new CompressionResult();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Execute decompression with timeout;
                var timeoutTokenSource = new CancellationTokenSource(request.Options.Timeout);
                var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutTokenSource.Token, context.CancellationTokenSource.Token);

                // Monitor progress;
                var progressTask = MonitorCompressionProgressAsync(request, linkedTokenSource.Token);

                // Execute decompression;
                var decompressionTask = algorithm.DecompressAsync(request, linkedTokenSource.Token);
                result = await decompressionTask;

                // Update statistics;
                stopwatch.Stop();
                result.Statistics.DecompressionTime = stopwatch.Elapsed;
                result.Statistics.AverageDecompressionSpeed = request.Source.Size > 0;
                    ? (float)(request.Source.Size / 1024.0 / 1024.0) / (float)stopwatch.Elapsed.TotalSeconds;
                    : 0;

                // Update request with result;
                request.CompletionTime = DateTime.UtcNow;
                request.Result = result;
                request.Status = result.Success ? CompressionStatus.Completed : CompressionStatus.Failed;
                request.Error = result.Success ? null : new Exception(result.ErrorMessage);

                // Update engine statistics;
                UpdateStatistics(result, false);

                // Fire completion event;
                OnCompressionCompleted?.Invoke(this, new CompressionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });

                if (result.Success)
                {
                    _logger.Info($"Decompression completed: {request.Source.Size} -> {result.CompressedSize} bytes " +
                                $"in {stopwatch.Elapsed.TotalSeconds:F2}s");
                }
                else;
                {
                    _logger.Error($"Decompression failed: {result.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                request.Status = CompressionStatus.Cancelled;
                request.Error = new OperationCanceledException("Decompression cancelled");

                result = CompressionResult.FailureResult("Decompression cancelled");
                request.Result = result;

                _logger.Info("Decompression cancelled");
                OnCompressionCompleted?.Invoke(this, new CompressionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });
            }
            catch (Exception ex)
            {
                request.Status = CompressionStatus.Failed;
                request.Error = ex;

                result = CompressionResult.FailureResult($"Decompression error: {ex.Message}");
                request.Result = result;

                _logger.Error($"Decompression error: {ex.Message}", ex);
                OnCompressionCompleted?.Invoke(this, new CompressionResultEventArgs;
                {
                    Request = request,
                    Result = result;
                });

                OnEngineError?.Invoke(this, new EngineErrorEventArgs;
                {
                    Error = ex,
                    Context = "Decompression",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                context.IsCompleted = true;
                lock (_compressionLock)
                {
                    _activeCompressions.Remove(request.Id);
                }
            }

            return result;
        }

        /// <summary>
        /// Compress file with simple parameters;
        /// </summary>
        public async Task<CompressionResult> CompressFileAsync(
            string sourcePath,
            string targetPath,
            CompressionFormat format = CompressionFormat.GZip,
            CompressionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be empty", nameof(sourcePath));

            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Target path cannot be empty", nameof(targetPath));

            var request = new CompressionRequest(
                new CompressionSource(sourcePath),
                new CompressionTarget(targetPath, format))
            {
                Options = options ?? new CompressionOptions()
            };

            return await CompressAsync(request);
        }

        /// <summary>
        /// Decompress file with simple parameters;
        /// </summary>
        public async Task<CompressionResult> DecompressFileAsync(
            string sourcePath,
            string targetPath,
            CompressionOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be empty", nameof(sourcePath));

            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentException("Target path cannot be empty", nameof(targetPath));

            // Detect compression format from file extension;
            var format = DetectCompressionFormat(sourcePath);

            var request = new CompressionRequest(
                new CompressionSource(sourcePath),
                new CompressionTarget(targetPath, format))
            {
                Options = options ?? new CompressionOptions()
            };

            return await DecompressAsync(request);
        }

        /// <summary>
        /// Compress bytes with simple parameters;
        /// </summary>
        public async Task<CompressionResult> CompressBytesAsync(
            byte[] sourceData,
            CompressionFormat format = CompressionFormat.GZip,
            CompressionOptions options = null)
        {
            if (sourceData == null || sourceData.Length == 0)
                throw new ArgumentException("Source data cannot be null or empty", nameof(sourceData));

            var request = new CompressionRequest(
                new CompressionSource(sourceData),
                new CompressionTarget;
                {
                    Type = TargetType.Memory,
                    Format = format;
                })
            {
                Options = options ?? new CompressionOptions()
            };

            return await CompressAsync(request);
        }

        /// <summary>
        /// Decompress bytes with simple parameters;
        /// </summary>
        public async Task<CompressionResult> DecompressBytesAsync(
            byte[] sourceData,
            CompressionFormat format,
            CompressionOptions options = null)
        {
            if (sourceData == null || sourceData.Length == 0)
                throw new ArgumentException("Source data cannot be null or empty", nameof(sourceData));

            var request = new CompressionRequest(
                new CompressionSource(sourceData),
                new CompressionTarget;
                {
                    Type = TargetType.Memory,
                    Format = format;
                })
            {
                Options = options ?? new CompressionOptions()
            };

            return await DecompressAsync(request);
        }

        #endregion;

        #region Batch Compression;

        /// <summary>
        /// Compress multiple files in batch;
        /// </summary>
        public async Task<BatchCompressionResult> CompressBatchAsync(
            BatchCompressionRequest batchRequest,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (batchRequest == null)
                throw new ArgumentNullException(nameof(batchRequest));

            if (batchRequest.Requests.Count == 0)
                throw new ArgumentException("Batch request must contain at least one compression request", nameof(batchRequest));

            var batchResult = new BatchCompressionResult;
            {
                BatchId = batchRequest.Id,
                BatchName = batchRequest.Name,
                Status = BatchStatus.Processing,
                StartTime = DateTime.UtcNow,
                TotalRequests = batchRequest.Requests.Count;
            };

            _batchResults[batchRequest.Id] = batchResult;

            _logger.Info($"Starting batch compression '{batchRequest.Name}' with {batchRequest.Requests.Count} requests");
            OnBatchCompressionStarted?.Invoke(this, batchRequest);

            var successfulCompressions = 0;
            var failedCompressions = 0;
            var skippedCompressions = 0;
            var results = new List<CompressionResult>();
            var exceptions = new List<Exception>();

            try
            {
                // Process compressions based on parallelism setting;
                if (batchRequest.Options.MaxConcurrentCompressions > 1)
                {
                    var parallelOptions = new ParallelOptions;
                    {
                        MaxDegreeOfParallelism = batchRequest.Options.MaxConcurrentCompressions,
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

                                // Set default format if not specified;
                                if (request.Target.Format == default)
                                {
                                    request.Target.Format = batchRequest.Options.DefaultFormat;
                                }

                                var result = await CompressAsync(request, ct);

                                lock (resultLock)
                                {
                                    results.Add(result);
                                    if (result.Success)
                                        successfulCompressions++;
                                    else;
                                        failedCompressions++;

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
                                    failedCompressions++;
                                    _logger.Error($"Batch compression failed for request {request.Id}: {ex.Message}", ex);
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

                            // Set default format if not specified;
                            if (request.Target.Format == default)
                            {
                                request.Target.Format = batchRequest.Options.DefaultFormat;
                            }

                            var result = await CompressAsync(request, cancellationToken);
                            results.Add(result);

                            if (result.Success)
                                successfulCompressions++;
                            else;
                                failedCompressions++;

                            UpdateBatchStatistics(batchResult.Statistics, request, result);
                        }
                        catch (Exception ex) when (batchRequest.Options.StopOnError)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                            failedCompressions++;
                            _logger.Error($"Batch compression failed for request {request.Id}: {ex.Message}", ex);
                        }
                    }
                }

                // Determine batch status;
                batchResult.Status = DetermineBatchStatus(
                    successfulCompressions,
                    failedCompressions,
                    skippedCompressions,
                    batchRequest.Requests.Count,
                    cancellationToken.IsCancellationRequested);

                // Set results;
                batchResult.SuccessfulCompressions = successfulCompressions;
                batchResult.FailedCompressions = failedCompressions;
                batchResult.SkippedCompressions = skippedCompressions;
                batchResult.Results = results;
                batchResult.CompletionTime = DateTime.UtcNow;
                batchResult.ExecutionTime = batchResult.CompletionTime - batchResult.StartTime;

                if (exceptions.Count > 0)
                {
                    batchResult.Error = new AggregateException("One or more compressions failed", exceptions);
                }

                // Generate report if requested;
                if (batchRequest.Options.GenerateReport)
                {
                    batchResult.ReportPath = GenerateBatchReport(batchRequest, batchResult);
                }

                _logger.Info($"Batch compression completed: {successfulCompressions} successful, {failedCompressions} failed");
            }
            catch (Exception ex)
            {
                batchResult.Status = BatchStatus.Failed;
                batchResult.Error = ex;
                batchResult.CompletionTime = DateTime.UtcNow;

                _logger.Error($"Batch compression failed: {ex.Message}", ex);
                OnEngineError?.Invoke(this, new EngineErrorEventArgs;
                {
                    Error = ex,
                    Context = "Batch compression",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                OnBatchCompressionCompleted?.Invoke(this, batchResult);
                _batchResults.Remove(batchRequest.Id);
            }

            return batchResult;
        }

        /// <summary>
        /// Decompress multiple files in batch;
        /// </summary>
        public async Task<BatchCompressionResult> DecompressBatchAsync(
            BatchCompressionRequest batchRequest,
            CancellationToken cancellationToken = default)
        {
            // Similar implementation to CompressBatchAsync but for decompression;
            // Would include format detection and algorithm selection for each file;
            throw new NotImplementedException();
        }

        #endregion;

        #region Helper Methods;

        /// <summary>
        /// Find decompression algorithm;
        /// </summary>
        private ICompressionAlgorithm FindDecompressionAlgorithm(CompressionRequest request)
        {
            lock (_algorithmLock)
            {
                // Try to find algorithm by format;
                var algorithms = _algorithms.Values;
                    .Where(e => e.IsEnabled)
                    .Select(e => e.Algorithm)
                    .Where(a => a.SupportedFormats.Contains(request.Target.Format))
                    .ToList();

                if (algorithms.Count > 0)
                {
                    var algorithm = algorithms.First();
                    UpdateAlgorithmUsage(algorithm);
                    return algorithm;
                }

                // Try to detect format from file;
                if (request.Source.Type == SourceType.File)
                {
                    var detectedFormat = DetectCompressionFormat(request.Source.FilePath);
                    if (detectedFormat != default)
                    {
                        request.Target.Format = detectedFormat;
                        return FindDecompressionAlgorithm(request);
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Detect compression format from file;
        /// </summary>
        private CompressionFormat DetectCompressionFormat(string filePath)
        {
            if (!File.Exists(filePath))
                return default;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            switch (extension)
            {
                case ".gz":
                case ".gzip":
                    return CompressionFormat.GZip;
                case ".zip":
                    return CompressionFormat.Zip;
                case ".7z":
                    return CompressionFormat.SevenZip;
                case ".rar":
                    return CompressionFormat.Rar;
                case ".bz2":
                    return CompressionFormat.BZip2;
                case ".xz":
                    return CompressionFormat.LZMA;
                case ".lz4":
                    return CompressionFormat.LZ4;
                case ".zst":
                    return CompressionFormat.Zstd;
                case ".br":
                    return CompressionFormat.Brotli;
                case ".tar":
                    return CompressionFormat.Tar;
                case ".deflate":
                    return CompressionFormat.Deflate;
                default:
                    // Try to detect by magic numbers;
                    return DetectFormatByMagicNumbers(filePath);
            }
        }

        /// <summary>
        /// Detect format by magic numbers;
        /// </summary>
        private CompressionFormat DetectFormatByMagicNumbers(string filePath)
        {
            try
            {
                var buffer = new byte[8];
                using (var stream = File.OpenRead(filePath))
                {
                    if (stream.Read(buffer, 0, buffer.Length) < buffer.Length)
                        return default;
                }

                // GZip: 1F 8B;
                if (buffer[0] == 0x1F && buffer[1] == 0x8B)
                    return CompressionFormat.GZip;

                // ZIP: 50 4B 03 04 or 50 4B 05 06 or 50 4B 07 08;
                if (buffer[0] == 0x50 && buffer[1] == 0x4B)
                    return CompressionFormat.Zip;

                // BZip2: 42 5A 68;
                if (buffer[0] == 0x42 && buffer[1] == 0x5A && buffer[2] == 0x68)
                    return CompressionFormat.BZip2;

                // 7-Zip: 37 7A BC AF 27 1C;
                if (buffer[0] == 0x37 && buffer[1] == 0x7A && buffer[2] == 0xBC &&
                    buffer[3] == 0xAF && buffer[4] == 0x27 && buffer[5] == 0x1C)
                    return CompressionFormat.SevenZip;

                // Zstd: 28 B5 2F FD;
                if (buffer[0] == 0x28 && buffer[1] == 0xB5 && buffer[2] == 0x2F && buffer[3] == 0xFD)
                    return CompressionFormat.Zstd;
            }
            catch
            {
                // Ignore detection errors;
            }

            return default;
        }

        /// <summary>
        /// Check if source is already compressed;
        /// </summary>
        private bool IsAlreadyCompressed(CompressionSource source)
        {
            if (source.Type != SourceType.File)
                return false;

            var format = DetectCompressionFormat(source.FilePath);
            return format != default;
        }

        /// <summary>
        /// Validate request with algorithm;
        /// </summary>
        private bool ValidateRequestWithAlgorithm(CompressionRequest request, ICompressionAlgorithm algorithm)
        {
            // Check size limits;
            if (request.Source.Size > algorithm.Capabilities.MaxInputSize)
                return false;

            // Check block size;
            if (request.Options.BlockSize > algorithm.Capabilities.MaxBlockSize ||
                request.Options.BlockSize < algorithm.Capabilities.MinBlockSize)
                return false;

            // Check if algorithm supports required features;
            if (request.Options.EnableEncryption && !algorithm.Capabilities.SupportsEncryption)
                return false;

            if (request.Options.EnableParallelCompression && !algorithm.Capabilities.SupportsParallelCompression)
                return false;

            if (request.Options.EnableDictionary && !algorithm.Capabilities.SupportsDictionary)
                return false;

            return true;
        }

        /// <summary>
        /// Estimate memory usage;
        /// </summary>
        private long EstimateMemoryUsage(CompressionRequest request, ICompressionAlgorithm algorithm)
        {
            // Base memory for algorithm;
            long memory = 1024 * 1024 * 10; // 10MB base;

            // Memory for source data;
            memory += request.Source.Size;

            // Memory for compression dictionary;
            if (request.Options.EnableDictionary)
                memory += request.Options.DictionarySize;

            // Memory for parallel processing;
            if (request.Options.EnableParallelCompression)
                memory *= request.Options.MaxDegreeOfParallelism;

            // Add 20% buffer;
            memory = (long)(memory * 1.2);

            return memory;
        }

        /// <summary>
        /// Monitor compression progress;
        /// </summary>
        private async Task MonitorCompressionProgressAsync(CompressionRequest request, CancellationToken cancellationToken)
        {
            var lastProgress = 0f;
            var lastBytesProcessed = 0L;

            while (!cancellationToken.IsCancellationRequested &&
                   (request.Status == CompressionStatus.Compressing ||
                    request.Status == CompressionStatus.Decompressing))
            {
                await Task.Delay(100, cancellationToken);

                // Simulate progress for now - in real implementation, this would come from the algorithm;
                if (request.Progress < 100f)
                {
                    // Simulate progress based on time elapsed;
                    var elapsed = DateTime.UtcNow - (request.StartTime ?? DateTime.UtcNow);
                    var estimatedTotalTime = EstimateCompressionTime(request);

                    if (estimatedTotalTime > TimeSpan.Zero)
                    {
                        request.Progress = Math.Min((float)(elapsed.TotalSeconds / estimatedTotalTime.TotalSeconds) * 100f, 100f);
                    }
                    else;
                    {
                        request.Progress = Math.Min(request.Progress + 1f, 100f);
                    }

                    // Simulate bytes processed;
                    var bytesProcessed = (long)(request.Source.Size * (request.Progress / 100f));

                    if (Math.Abs(request.Progress - lastProgress) >= 1f ||
                        Math.Abs(bytesProcessed - lastBytesProcessed) >= 1024 * 1024) // 1MB;
                    {
                        lastProgress = request.Progress;
                        lastBytesProcessed = bytesProcessed;

                        OnCompressionProgressChanged?.Invoke(this, new CompressionProgressEventArgs;
                        {
                            RequestId = request.Id,
                            Progress = request.Progress,
                            Status = request.Status,
                            CurrentOperation = request.Status == CompressionStatus.Compressing ? "Compressing" : "Decompressing",
                            BytesProcessed = bytesProcessed,
                            TotalBytes = request.Source.Size,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Estimate compression time;
        /// </summary>
        private TimeSpan EstimateCompressionTime(CompressionRequest request)
        {
            // Very rough estimation based on size and algorithm;
            var compressionSpeed = 50.0 * 1024 * 1024; // 50 MB/s default;

            switch (request.Options.Algorithm)
            {
                case CompressionAlgorithm.LZ4:
                    compressionSpeed = 400 * 1024 * 1024; // 400 MB/s;
                    break;
                case CompressionAlgorithm.Snappy:
                    compressionSpeed = 300 * 1024 * 1024; // 300 MB/s;
                    break;
                case CompressionAlgorithm.Zstd:
                    compressionSpeed = 200 * 1024 * 1024; // 200 MB/s;
                    break;
                case CompressionAlgorithm.GZip:
                    compressionSpeed = 100 * 1024 * 1024; // 100 MB/s;
                    break;
                case CompressionAlgorithm.LZMA:
                    compressionSpeed = 10 * 1024 * 1024; // 10 MB/s;
                    break;
                case CompressionAlgorithm.BZip2:
                    compressionSpeed = 5 * 1024 * 1024; // 5 MB/s;
                    break;
            }

            // Adjust for compression level;
            switch (request.Options.Level)
            {
                case CompressionLevel.Fastest:
                    compressionSpeed *= 2;
                    break;
                case CompressionLevel.SmallestSize:
                    compressionSpeed /= 2;
                    break;
                case CompressionLevel.Optimal:
                    // No adjustment;
                    break;
            }

            var seconds = request.Source.Size / compressionSpeed;
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Get output path for batch compression;
        /// </summary>
        private string GetOutputPath(string sourcePath, CompressionFormat format, string outputDirectory, bool preserveStructure)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var extension = GetFormatExtension(format);
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
        /// Get file extension for compression format;
        /// </summary>
        private string GetFormatExtension(CompressionFormat format)
        {
            return format switch;
            {
                CompressionFormat.GZip => ".gz",
                CompressionFormat.Zip => ".zip",
                CompressionFormat.SevenZip => ".7z",
                CompressionFormat.Rar => ".rar",
                CompressionFormat.BZip2 => ".bz2",
                CompressionFormat.LZMA => ".xz",
                CompressionFormat.LZ4 => ".lz4",
                CompressionFormat.Zstd => ".zst",
                CompressionFormat.Brotli => ".br",
                CompressionFormat.Tar => ".tar",
                CompressionFormat.Deflate => ".deflate",
                _ => ".compressed"
            };
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
        private void UpdateBatchStatistics(BatchStatistics statistics, CompressionRequest request, CompressionResult result)
        {
            statistics.TotalOriginalSize += result.OriginalSize;
            statistics.TotalCompressedSize += result.CompressedSize;

            if (result.OriginalSize > 0)
            {
                statistics.TotalCompressionRatio = (float)statistics.TotalCompressedSize / statistics.TotalOriginalSize;
                statistics.TotalSpaceSaved += result.SpaceSaved;
            }

            statistics.TotalCompressionTime += result.Statistics.CompressionTime;
            statistics.PeakMemoryUsage = Math.Max(statistics.PeakMemoryUsage, result.Statistics.PeakMemoryUsage);

            // Update format counts;
            if (statistics.FormatCounts.ContainsKey(request.Target.Format))
                statistics.FormatCounts[request.Target.Format]++;
            else;
                statistics.FormatCounts[request.Target.Format] = 1;

            // Update algorithm counts;
            if (statistics.AlgorithmCounts.ContainsKey(request.Options.Algorithm))
                statistics.AlgorithmCounts[request.Options.Algorithm]++;
            else;
                statistics.AlgorithmCounts[request.Options.Algorithm] = 1;
        }

        /// <summary>
        /// Generate batch report;
        /// </summary>
        private string GenerateBatchReport(BatchCompressionRequest batchRequest, BatchCompressionResult batchResult)
        {
            try
            {
                var reportDir = Path.Combine(_configuration.ReportDirectory, "CompressionReports");
                Directory.CreateDirectory(reportDir);

                var reportPath = Path.Combine(reportDir, $"CompressionReport_{batchRequest.Id}_{DateTime.Now:yyyyMMdd_HHmmss}.json");

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
                        Successful = batchResult.SuccessfulCompressions,
                        Failed = batchResult.FailedCompressions,
                        Skipped = batchResult.SkippedCompressions,
                        SuccessRate = (float)batchResult.SuccessfulCompressions / batchResult.TotalRequests * 100,
                        TotalOriginalSize = batchResult.Statistics.TotalOriginalSize,
                        TotalCompressedSize = batchResult.Statistics.TotalCompressedSize,
                        CompressionRatio = batchResult.Statistics.TotalCompressionRatio,
                        TotalSpaceSaved = batchResult.Statistics.TotalSpaceSaved,
                        SpaceSavedPercentage = batchResult.Statistics.TotalOriginalSize > 0;
                            ? (float)batchResult.Statistics.TotalSpaceSaved / batchResult.Statistics.TotalOriginalSize * 100;
                            : 0,
                        TotalCompressionTime = batchResult.Statistics.TotalCompressionTime.TotalSeconds,
                        PeakMemoryUsage = batchResult.Statistics.PeakMemoryUsage,
                        FormatCounts = batchResult.Statistics.FormatCounts,
                        AlgorithmCounts = batchResult.Statistics.AlgorithmCounts;
                    },
                    Results = batchResult.Results.Select(r => new;
                    {
                        Success = r.Success,
                        OriginalSize = r.OriginalSize,
                        CompressedSize = r.CompressedSize,
                        CompressionRatio = r.CompressionRatio,
                        SpaceSaved = r.SpaceSaved,
                        SpaceSavedPercentage = r.SpaceSavedPercentage,
                        CompressionTime = r.Statistics.CompressionTime.TotalSeconds,
                        CompressionSpeed = r.Statistics.AverageCompressionSpeed,
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

        /// <summary>
        /// Update engine statistics;
        /// </summary>
        private void UpdateStatistics(CompressionResult result, bool isCompression)
        {
            _statistics.TotalOperations++;

            if (isCompression)
                _statistics.TotalCompressions++;
            else;
                _statistics.TotalDecompressions++;

            if (result.Success)
                _statistics.SuccessfulOperations++;
            else;
                _statistics.FailedOperations++;

            _statistics.TotalOriginalSize += result.OriginalSize;
            _statistics.TotalCompressedSize += result.CompressedSize;
            _statistics.TotalSpaceSaved += result.SpaceSaved;

            if (isCompression)
                _statistics.TotalCompressionTime += result.Statistics.CompressionTime;
            else;
                _statistics.TotalDecompressionTime += result.Statistics.DecompressionTime;

            _statistics.PeakMemoryUsage = Math.Max(_statistics.PeakMemoryUsage, result.Statistics.PeakMemoryUsage);

            // Update algorithm statistics;
            var algorithmName = result.Metadata.ContainsKey("Algorithm")
                ? result.Metadata["Algorithm"].ToString()
                : "Unknown";

            if (_statistics.AlgorithmStats.ContainsKey(algorithmName))
            {
                var stats = _statistics.AlgorithmStats[algorithmName];
                stats.UsageCount++;
                stats.TotalOriginalSize += result.OriginalSize;
                stats.TotalCompressedSize += result.CompressedSize;
                stats.TotalTime += isCompression ? result.Statistics.CompressionTime : result.Statistics.DecompressionTime;
            }
            else;
            {
                _statistics.AlgorithmStats[algorithmName] = new AlgorithmStatistics;
                {
                    UsageCount = 1,
                    TotalOriginalSize = result.OriginalSize,
                    TotalCompressedSize = result.CompressedSize,
                    TotalTime = isCompression ? result.Statistics.CompressionTime : result.Statistics.DecompressionTime;
                };
            }
        }

        /// <summary>
        /// Monitor memory pool;
        /// </summary>
        private async Task MonitorMemoryPoolAsync()
        {
            while (!_isDisposed)
            {
                try
                {
                    await Task.Delay(5000); // Check every 5 seconds;

                    var utilization = _memoryPool.Utilization;
                    if (utilization > _configuration.MemoryPoolWarningThreshold)
                    {
                        OnMemoryPoolWarning?.Invoke(this, new MemoryPoolWarningEventArgs;
                        {
                            Utilization = utilization,
                            Threshold = _configuration.MemoryPoolWarningThreshold,
                            WarningMessage = $"Memory pool utilization is high: {utilization:P0}",
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }
                catch
                {
                    // Ignore monitoring errors;
                }
            }
        }

        /// <summary>
        /// Collect statistics;
        /// </summary>
        private async Task CollectStatisticsAsync()
        {
            while (!_isDisposed)
            {
                try
                {
                    await Task.Delay(60000); // Collect every minute;

                    // Update performance statistics;
                    _statistics.AverageCompressionSpeed = CalculateAverageSpeed(true);
                    _statistics.AverageDecompressionSpeed = CalculateAverageSpeed(false);

                    // Update compression ratio statistics;
                    if (_statistics.TotalOriginalSize > 0)
                    {
                        _statistics.AverageCompressionRatio = (float)_statistics.TotalCompressedSize / _statistics.TotalOriginalSize;
                    }
                }
                catch
                {
                    // Ignore statistics collection errors;
                }
            }
        }

        /// <summary>
        /// Calculate average compression/decompression speed;
        /// </summary>
        private float CalculateAverageSpeed(bool isCompression)
        {
            var totalTime = isCompression ? _statistics.TotalCompressionTime : _statistics.TotalDecompressionTime;
            var totalSize = isCompression ? _statistics.TotalOriginalSize : _statistics.TotalCompressedSize;

            if (totalTime.TotalSeconds > 0 && totalSize > 0)
            {
                return (float)(totalSize / 1024.0 / 1024.0) / (float)totalTime.TotalSeconds; // MB/s;
            }

            return 0f;
        }

        /// <summary>
        /// Cancel active compression;
        /// </summary>
        public bool CancelCompression(Guid requestId)
        {
            lock (_compressionLock)
            {
                if (_activeCompressions.TryGetValue(requestId, out var context))
                {
                    context.IsCancelled = true;
                    context.CancellationTokenSource.Cancel();
                    _logger.Info($"Cancelled compression: {requestId}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get active compressions;
        /// </summary>
        public List<CompressionRequest> GetActiveCompressions()
        {
            lock (_compressionLock)
            {
                return _activeCompressions.Values.Select(c => c.Request).ToList();
            }
        }

        /// <summary>
        /// Get batch result;
        /// </summary>
        public BatchCompressionResult GetBatchResult(Guid batchId)
        {
            _batchResults.TryGetValue(batchId, out var result);
            return result;
        }

        /// <summary>
        /// Estimate compression ratio for request;
        /// </summary>
        public float EstimateCompressionRatio(CompressionRequest request)
        {
            var algorithm = FindBestAlgorithm(request);
            if (algorithm != null)
            {
                return algorithm.EstimateCompressionRatio(request);
            }

            return 1.0f; // No compression by default;
        }

        /// <summary>
        /// Get algorithm information;
        /// </summary>
        public AlgorithmInfo GetAlgorithmInfo(string algorithmName, string version = null)
        {
            var algorithm = GetAlgorithm(algorithmName, version);
            return algorithm?.GetAlgorithmInfo();
        }

        /// <summary>
        /// Get all algorithm information;
        /// </summary>
        public List<AlgorithmInfo> GetAllAlgorithmInfo()
        {
            lock (_algorithmLock)
            {
                return _algorithms.Values;
                    .Select(e => e.Algorithm.GetAlgorithmInfo())
                    .ToList();
            }
        }

        /// <summary>
        /// Validate engine is not disposed;
        /// </summary>
        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(CompressionEngine));
        }

        #endregion;

        #region Engine Configuration;

        /// <summary>
        /// Engine configuration;
        /// </summary>
        public class EngineConfiguration;
        {
            public string TempDirectory { get; set; }
            public string ReportDirectory { get; set; }
            public int MaxConcurrentCompressions { get; set; }
            public long MemoryPoolSize { get; set; }
            public float MemoryPoolWarningThreshold { get; set; }
            public bool EnableStatistics { get; set; }
            public bool EnableProgressTracking { get; set; }
            public bool EnableAdaptiveCompression { get; set; }
            public TimeSpan StatisticsCollectionInterval { get; set; }
            public TimeSpan ProgressUpdateInterval { get; set; }

            public EngineConfiguration()
            {
                TempDirectory = Path.Combine(Path.GetTempPath(), "NEDA_CompressionEngine");
                ReportDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NEDA", "Reports");
                MaxConcurrentCompressions = Environment.ProcessorCount * 2;
                MemoryPoolSize = 1024L * 1024 * 1024 * 2; // 2GB;
                MemoryPoolWarningThreshold = 0.8f; // 80%
                EnableStatistics = true;
                EnableProgressTracking = true;
                EnableAdaptiveCompression = true;
                StatisticsCollectionInterval = TimeSpan.FromMinutes(1);
                ProgressUpdateInterval = TimeSpan.FromMilliseconds(100);
            }
        }

        /// <summary>
        /// Engine statistics;
        /// </summary>
        public class EngineStatistics;
        {
            public int TotalOperations { get; set; }
            public int TotalCompressions { get; set; }
            public int TotalDecompressions { get; set; }
            public int SuccessfulOperations { get; set; }
            public int FailedOperations { get; set; }
            public long TotalOriginalSize { get; set; }
            public long TotalCompressedSize { get; set; }
            public long TotalSpaceSaved { get; set; }
            public float AverageCompressionRatio { get; set; }
            public float AverageCompressionSpeed { get; set; } // MB/s;
            public float AverageDecompressionSpeed { get; set; } // MB/s;
            public TimeSpan TotalCompressionTime { get; set; }
            public TimeSpan TotalDecompressionTime { get; set; }
            public long PeakMemoryUsage { get; set; }
            public Dictionary<string, AlgorithmStatistics> AlgorithmStats { get; set; }
            public DateTime StartTime { get; }
            public TimeSpan Uptime => DateTime.UtcNow - StartTime;

            public EngineStatistics()
            {
                AlgorithmStats = new Dictionary<string, AlgorithmStatistics>();
                StartTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Algorithm statistics;
        /// </summary>
        public class AlgorithmStatistics;
        {
            public int UsageCount { get; set; }
            public long TotalOriginalSize { get; set; }
            public long TotalCompressedSize { get; set; }
            public TimeSpan TotalTime { get; set; }
            public float AverageRatio => TotalOriginalSize > 0 ? (float)TotalCompressedSize / TotalOriginalSize : 0;
            public float AverageSpeed => TotalTime.TotalSeconds > 0 ? (float)(TotalOriginalSize / 1024.0 / 1024.0) / (float)TotalTime.TotalSeconds : 0;
        }

        #endregion;

        #region Built-in Algorithms;

        /// <summary>
        /// Base algorithm implementation;
        /// </summary>
        public abstract class BaseCompressionAlgorithm : ICompressionAlgorithm;
        {
            public abstract string Name { get; }
            public abstract string Version { get; }
            public abstract CompressionFormat[] SupportedFormats { get; }
            public abstract AlgorithmCapabilities Capabilities { get; }

            public virtual async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                var result = new CompressionResult();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    // Validate request;
                    if (!ValidateRequest(request, out var errors))
                    {
                        return CompressionResult.FailureResult($"Validation failed: {string.Join(", ", errors)}");
                    }

                    // Perform compression;
                    var output = await PerformCompressionAsync(request, cancellationToken);

                    stopwatch.Stop();

                    result.Success = true;
                    result.Output = output;
                    result.OriginalSize = request.Source.Size;
                    result.CompressedSize = GetOutputSize(output);
                    result.CompressionRatio = request.Source.Size > 0 ? (float)result.CompressedSize / request.Source.Size : 0;
                    result.SpaceSaved = request.Source.Size - result.CompressedSize;
                    result.SpaceSavedPercentage = request.Source.Size > 0 ? (float)result.SpaceSaved / request.Source.Size * 100 : 0;
                    result.Statistics.CompressionTime = stopwatch.Stopwatch.Elapsed;
                    result.Statistics.AverageCompressionSpeed = request.Source.Size > 0;
                        ? (float)(request.Source.Size / 1024.0 / 1024.0) / (float)stopwatch.Elapsed.TotalSeconds;
                        : 0;
                    result.AddMetadata("Algorithm", Name);
                    result.AddMetadata("CompressionLevel", request.Options.Level.ToString());

                    return result;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    result.Statistics.CompressionTime = stopwatch.Elapsed;
                    return result;
                }
            }

            public virtual async Task<CompressionResult> DecompressAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                var result = new CompressionResult();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    // Validate request;
                    if (!ValidateRequest(request, out var errors))
                    {
                        return CompressionResult.FailureResult($"Validation failed: {string.Join(", ", errors)}");
                    }

                    // Perform decompression;
                    var output = await PerformDecompressionAsync(request, cancellationToken);

                    stopwatch.Stop();

                    result.Success = true;
                    result.Output = output;
                    result.CompressedSize = request.Source.Size;
                    result.OriginalSize = GetOutputSize(output);
                    result.CompressionRatio = result.OriginalSize > 0 ? (float)result.CompressedSize / result.OriginalSize : 0;
                    result.SpaceSaved = result.OriginalSize - result.CompressedSize;
                    result.SpaceSavedPercentage = result.OriginalSize > 0 ? (float)result.SpaceSaved / result.OriginalSize * 100 : 0;
                    result.Statistics.DecompressionTime = stopwatch.Elapsed;
                    result.Statistics.AverageDecompressionSpeed = request.Source.Size > 0;
                        ? (float)(request.Source.Size / 1024.0 / 1024.0) / (float)stopwatch.Elapsed.TotalSeconds;
                        : 0;
                    result.AddMetadata("Algorithm", Name);

                    return result;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    result.Statistics.DecompressionTime = stopwatch.Elapsed;
                    return result;
                }
            }

            public virtual float EstimateCompressionRatio(CompressionRequest request)
            {
                // Default estimation based on algorithm and level;
                var baseRatio = GetBaseCompressionRatio();

                // Adjust based on compression level;
                var levelFactor = request.Options.Level switch;
                {
                    CompressionLevel.Fastest => 1.2f,
                    CompressionLevel.Optimal => 1.0f,
                    CompressionLevel.SmallestSize => 0.8f,
                    _ => 1.0f;
                };

                // Adjust based on data type if known;
                var dataTypeFactor = EstimateDataTypeFactor(request);

                return baseRatio * levelFactor * dataTypeFactor;
            }

            public virtual AlgorithmInfo GetAlgorithmInfo()
            {
                return new AlgorithmInfo;
                {
                    Name = Name,
                    Description = $"Compression algorithm: {Name}",
                    IsLossless = true,
                    TypicalCompressionRatio = GetBaseCompressionRatio(),
                    TypicalCompressionSpeed = 100.0f, // 100 MB/s default;
                    TypicalDecompressionSpeed = 200.0f // 200 MB/s default;
                };
            }

            protected abstract Task<object> PerformCompressionAsync(CompressionRequest request, CancellationToken cancellationToken);
            protected abstract Task<object> PerformDecompressionAsync(CompressionRequest request, CancellationToken cancellationToken);

            protected virtual bool ValidateRequest(CompressionRequest request, out List<string> errors)
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

                // Check if format is supported;
                if (!SupportedFormats.Contains(request.Target.Format))
                {
                    errors.Add($"Format '{request.Target.Format}' is not supported");
                }

                // Check size limits;
                if (request.Source.Size > Capabilities.MaxInputSize)
                {
                    errors.Add($"Source size ({request.Source.Size} bytes) exceeds maximum ({Capabilities.MaxInputSize} bytes)");
                }

                return errors.Count == 0;
            }

            protected virtual long GetOutputSize(object output)
            {
                if (output is byte[] bytes)
                    return bytes.Length;
                else if (output is string filePath && File.Exists(filePath))
                    return new FileInfo(filePath).Length;
                else if (output is Stream stream)
                    return stream.Length;
                else;
                    return 0;
            }

            protected virtual float GetBaseCompressionRatio()
            {
                return 0.5f; // 50% compression by default;
            }

            protected virtual float EstimateDataTypeFactor(CompressionRequest request)
            {
                // Default factor;
                return 1.0f;
            }
        }

        /// <summary>
        /// GZip compression algorithm;
        /// </summary>
        public class GZipAlgorithm : BaseCompressionAlgorithm;
        {
            public override string Name => "GZip";
            public override string Version => "1.0.0";

            public override CompressionFormat[] SupportedFormats => new[]
            {
                CompressionFormat.GZip;
            };

            public override AlgorithmCapabilities Capabilities => new AlgorithmCapabilities;
            {
                SupportsStreaming = true,
                SupportsParallelCompression = false,
                SupportsEncryption = false,
                SupportsChecksum = true,
                SupportsDictionary = false,
                SupportsAdaptiveCompression = false,
                MaxInputSize = 1024L * 1024 * 1024 * 4, // 4GB;
                MaxOutputSize = 1024L * 1024 * 1024 * 4, // 4GB;
                MaxBlockSize = 1024 * 1024 * 32, // 32MB;
                MinBlockSize = 1024 // 1KB;
            };

            protected override async Task<object> PerformCompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                using (var sourceStream = request.Source.GetStream())
                using (var targetStream = request.Target.GetStream())
                {
                    var compressionLevel = ToSystemCompressionLevel(request.Options.Level);

                    using (var gzipStream = new GZipStream(targetStream, compressionLevel, true))
                    {
                        await sourceStream.CopyToAsync(gzipStream, 81920, cancellationToken);
                    }

                    if (request.Target.Type == TargetType.File)
                        return request.Target.FilePath;
                    else if (request.Target.Type == TargetType.Stream)
                        return targetStream;
                    else;
                        return GetStreamBytes(targetStream);
                }
            }

            protected override async Task<object> PerformDecompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                using (var sourceStream = request.Source.GetStream())
                using (var targetStream = request.Target.GetStream())
                {
                    using (var gzipStream = new GZipStream(sourceStream, CompressionMode.Decompress, true))
                    {
                        await gzipStream.CopyToAsync(targetStream, 81920, cancellationToken);
                    }

                    if (request.Target.Type == TargetType.File)
                        return request.Target.FilePath;
                    else if (request.Target.Type == TargetType.Stream)
                        return targetStream;
                    else;
                        return GetStreamBytes(targetStream);
                }
            }

            protected override float GetBaseCompressionRatio()
            {
                return 0.4f; // GZip typically achieves 40% compression;
            }

            private System.IO.Compression.CompressionLevel ToSystemCompressionLevel(CompressionLevel level)
            {
                return level switch;
                {
                    CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
                    CompressionLevel.Optimal => System.IO.Compression.CompressionLevel.Optimal,
                    CompressionLevel.SmallestSize => System.IO.Compression.CompressionLevel.SmallestSize,
                    _ => System.IO.Compression.CompressionLevel.Optimal;
                };
            }

            private byte[] GetStreamBytes(Stream stream)
            {
                if (stream is MemoryStream memoryStream)
                    return memoryStream.ToArray();

                stream.Position = 0;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Deflate compression algorithm;
        /// </summary>
        public class DeflateAlgorithm : BaseCompressionAlgorithm;
        {
            public override string Name => "Deflate";
            public override string Version => "1.0.0";

            public override CompressionFormat[] SupportedFormats => new[]
            {
                CompressionFormat.Deflate,
                CompressionFormat.Zlib;
            };

            public override AlgorithmCapabilities Capabilities => new AlgorithmCapabilities;
            {
                SupportsStreaming = true,
                SupportsParallelCompression = false,
                SupportsEncryption = false,
                SupportsChecksum = true,
                SupportsDictionary = false,
                SupportsAdaptiveCompression = false,
                MaxInputSize = 1024L * 1024 * 1024 * 2, // 2GB;
                MaxOutputSize = 1024L * 1024 * 1024 * 2, // 2GB;
                MaxBlockSize = 1024 * 1024 * 16, // 16MB;
                MinBlockSize = 1024 // 1KB;
            };

            protected override async Task<object> PerformCompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                using (var sourceStream = request.Source.GetStream())
                using (var targetStream = request.Target.GetStream())
                {
                    var compressionLevel = ToSystemCompressionLevel(request.Options.Level);

                    using (var deflateStream = new DeflateStream(targetStream, compressionLevel, true))
                    {
                        await sourceStream.CopyToAsync(deflateStream, 81920, cancellationToken);
                    }

                    if (request.Target.Type == TargetType.File)
                        return request.Target.FilePath;
                    else if (request.Target.Type == TargetType.Stream)
                        return targetStream;
                    else;
                        return GetStreamBytes(targetStream);
                }
            }

            protected override async Task<object> PerformDecompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                using (var sourceStream = request.Source.GetStream())
                using (var targetStream = request.Target.GetStream())
                {
                    using (var deflateStream = new DeflateStream(sourceStream, CompressionMode.Decompress, true))
                    {
                        await deflateStream.CopyToAsync(targetStream, 81920, cancellationToken);
                    }

                    if (request.Target.Type == TargetType.File)
                        return request.Target.FilePath;
                    else if (request.Target.Type == TargetType.Stream)
                        return targetStream;
                    else;
                        return GetStreamBytes(targetStream);
                }
            }

            protected override float GetBaseCompressionRatio()
            {
                return 0.45f; // Deflate typically achieves 45% compression;
            }

            private System.IO.Compression.CompressionLevel ToSystemCompressionLevel(CompressionLevel level)
            {
                return level switch;
                {
                    CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
                    CompressionLevel.Optimal => System.IO.Compression.CompressionLevel.Optimal,
                    CompressionLevel.SmallestSize => System.IO.Compression.CompressionLevel.SmallestSize,
                    _ => System.IO.Compression.CompressionLevel.Optimal;
                };
            }

            private byte[] GetStreamBytes(Stream stream)
            {
                if (stream is MemoryStream memoryStream)
                    return memoryStream.ToArray();

                stream.Position = 0;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Brotli compression algorithm;
        /// </summary>
        public class BrotliAlgorithm : BaseCompressionAlgorithm;
        {
            public override string Name => "Brotli";
            public override string Version => "1.0.0";

            public override CompressionFormat[] SupportedFormats => new[]
            {
                CompressionFormat.Brotli;
            };

            public override AlgorithmCapabilities Capabilities => new AlgorithmCapabilities;
            {
                SupportsStreaming = true,
                SupportsParallelCompression = false,
                SupportsEncryption = false,
                SupportsChecksum = true,
                SupportsDictionary = true,
                SupportsAdaptiveCompression = true,
                MaxInputSize = 1024L * 1024 * 1024 * 4, // 4GB;
                MaxOutputSize = 1024L * 1024 * 1024 * 4, // 4GB;
                MaxBlockSize = 1024 * 1024 * 64, // 64MB;
                MinBlockSize = 1024 // 1KB;
            };

            protected override async Task<object> PerformCompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                using (var sourceStream = request.Source.GetStream())
                using (var targetStream = request.Target.GetStream())
                {
                    var compressionLevel = ToSystemCompressionLevel(request.Options.Level);

                    using (var brotliStream = new BrotliStream(targetStream, compressionLevel, true))
                    {
                        await sourceStream.CopyToAsync(brotliStream, 81920, cancellationToken);
                    }

                    if (request.Target.Type == TargetType.File)
                        return request.Target.FilePath;
                    else if (request.Target.Type == TargetType.Stream)
                        return targetStream;
                    else;
                        return GetStreamBytes(targetStream);
                }
            }

            protected override async Task<object> PerformDecompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                using (var sourceStream = request.Source.GetStream())
                using (var targetStream = request.Target.GetStream())
                {
                    using (var brotliStream = new BrotliStream(sourceStream, CompressionMode.Decompress, true))
                    {
                        await brotliStream.CopyToAsync(targetStream, 81920, cancellationToken);
                    }

                    if (request.Target.Type == TargetType.File)
                        return request.Target.FilePath;
                    else if (request.Target.Type == TargetType.Stream)
                        return targetStream;
                    else;
                        return GetStreamBytes(targetStream);
                }
            }

            protected override float GetBaseCompressionRatio()
            {
                return 0.3f; // Brotli typically achieves 30% compression (better than GZip)
            }

            private System.IO.Compression.CompressionLevel ToSystemCompressionLevel(CompressionLevel level)
            {
                // Brotli has 0-11 levels, map our levels appropriately;
                return level switch;
                {
                    CompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
                    CompressionLevel.Optimal => System.IO.Compression.CompressionLevel.Optimal,
                    CompressionLevel.SmallestSize => System.IO.Compression.CompressionLevel.SmallestSize,
                    _ => System.IO.Compression.CompressionLevel.Optimal;
                };
            }

            private byte[] GetStreamBytes(Stream stream)
            {
                if (stream is MemoryStream memoryStream)
                    return memoryStream.ToArray();

                stream.Position = 0;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Zstd compression algorithm;
        /// </summary>
        public class ZstdAlgorithm : BaseCompressionAlgorithm;
        {
            public override string Name => "Zstd";
            public override string Version => "1.0.0";

            public override CompressionFormat[] SupportedFormats => new[]
            {
                CompressionFormat.Zstd;
            };

            public override AlgorithmCapabilities Capabilities => new AlgorithmCapabilities;
            {
                SupportsStreaming = true,
                SupportsParallelCompression = true,
                SupportsEncryption = false,
                SupportsChecksum = true,
                SupportsDictionary = true,
                SupportsAdaptiveCompression = true,
                MaxInputSize = 1024L * 1024 * 1024 * 8, // 8GB;
                MaxOutputSize = 1024L * 1024 * 1024 * 8, // 8GB;
                MaxBlockSize = 1024 * 1024 * 128, // 128MB;
                MinBlockSize = 1024 // 1KB;
            };

            protected override async Task<object> PerformCompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                // In a real implementation, this would use a Zstd library;
                // For now, simulate with GZip;
                return await Task.Run(() =>
                {
                    // Simulate compression;
                    Thread.Sleep(100);

                    if (request.Target.Type == TargetType.File)
                    {
                        File.WriteAllText(request.Target.FilePath, "Simulated Zstd compression");
                        return request.Target.FilePath;
                    }
                    else;
                    {
                        return new byte[] { 0x28, 0xB5, 0x2F, 0xFD }; // Zstd magic number;
                    }
                }, cancellationToken);
            }

            protected override async Task<object> PerformDecompressionAsync(CompressionRequest request, CancellationToken cancellationToken)
            {
                // In a real implementation, this would use a Zstd library;
                // For now, simulate;
                return await Task.Run(() =>
                {
                    // Simulate decompression;
                    Thread.Sleep(100);

                    if (request.Target.Type == TargetType.File)
                    {
                        File.WriteAllText(request.Target.FilePath, "Simulated Zstd decompression");
                        return request.Target.FilePath;
                    }
                    else;
                    {
                        return new byte[1024]; // 1KB of zeros;
                    }
                }, cancellationToken);
            }

            protected override float GetBaseCompressionRatio()
            {
                return 0.25f; // Zstd typically achieves 25% compression (very good)
            }
        }

        // Additional algorithms would be implemented similarly;
        public class LZ4Algorithm : BaseCompressionAlgorithm { /* Implementation */ }
        public class BZip2Algorithm : BaseCompressionAlgorithm { /* Implementation */ }
        public class LZMAAlgorithm : BaseCompressionAlgorithm { /* Implementation */ }
        public class ZipArchiveAlgorithm : BaseCompressionAlgorithm { /* Implementation */ }

        #endregion;

        #region Helper Classes;

        /// <summary>
        /// Memory pool for efficient memory management;
        /// </summary>
        private class MemoryPool;
        {
            private readonly long _totalSize;
            private long _usedSize;
            private readonly object _lock = new object();
            private readonly Queue<byte[]> _availableBuffers;
            private readonly Dictionary<int, List<byte[]>> _bufferPools;

            public float Utilization => _totalSize > 0 ? (float)_usedSize / _totalSize : 0f;

            public MemoryPool(long totalSize)
            {
                _totalSize = totalSize;
                _usedSize = 0;
                _availableBuffers = new Queue<byte[]>();
                _bufferPools = new Dictionary<int, List<byte[]>>();
            }

            public void Initialize()
            {
                // Pre-allocate some buffers;
                for (int i = 0; i < 10; i++)
                {
                    var buffer = new byte[1024 * 1024]; // 1MB buffers;
                    _availableBuffers.Enqueue(buffer);
                    _usedSize += buffer.Length;
                }
            }

            public byte[] Rent(int size)
            {
                lock (_lock)
                {
                    // Try to find buffer of exact size;
                    if (_bufferPools.TryGetValue(size, out var pool) && pool.Count > 0)
                    {
                        var buffer = pool[pool.Count - 1];
                        pool.RemoveAt(pool.Count - 1);
                        return buffer;
                    }

                    // Try to find larger buffer;
                    foreach (var kvp in _bufferPools.Where(kvp => kvp.Key >= size && kvp.Value.Count > 0))
                    {
                        var buffer = kvp.Value[kvp.Value.Count - 1];
                        kvp.Value.RemoveAt(kvp.Value.Count - 1);
                        return buffer;
                    }

                    // Allocate new buffer;
                    var newBuffer = new byte[size];
                    _usedSize += size;
                    return newBuffer;
                }
            }

            public void Return(byte[] buffer)
            {
                if (buffer == null)
                    return;

                lock (_lock)
                {
                    var size = buffer.Length;

                    if (!_bufferPools.ContainsKey(size))
                        _bufferPools[size] = new List<byte[]>();

                    _bufferPools[size].Add(buffer);
                }
            }

            public void Return(long sizeEstimate)
            {
                // For simplicity, just track used size;
                lock (_lock)
                {
                    _usedSize = Math.Max(0, _usedSize - sizeEstimate);
                }
            }
        }

        /// <summary>
        /// Progress tracker;
        /// </summary>
        private class ProgressTracker;
        {
            private readonly Dictionary<Guid, ProgressInfo> _progressInfo;
            private readonly object _lock = new object();

            public ProgressTracker()
            {
                _progressInfo = new Dictionary<Guid, ProgressInfo>();
            }

            public void UpdateProgress(Guid requestId, float progress, long bytesProcessed)
            {
                lock (_lock)
                {
                    if (!_progressInfo.TryGetValue(requestId, out var info))
                    {
                        info = new ProgressInfo();
                        _progressInfo[requestId] = info;
                    }

                    info.Progress = progress;
                    info.BytesProcessed = bytesProcessed;
                    info.LastUpdate = DateTime.UtcNow;
                }
            }

            public ProgressInfo GetProgress(Guid requestId)
            {
                lock (_lock)
                {
                    _progressInfo.TryGetValue(requestId, out var info);
                    return info;
                }
            }

            public void RemoveProgress(Guid requestId)
            {
                lock (_lock)
                {
                    _progressInfo.Remove(requestId);
                }
            }

            public class ProgressInfo;
            {
                public float Progress { get; set; }
                public long BytesProcessed { get; set; }
                public DateTime LastUpdate { get; set; }
            }
        }

        /// <summary>
        /// Statistics collector;
        /// </summary>
        private class StatisticsCollector;
        {
            private readonly List<CompressionResult> _recentResults;
            private readonly object _lock = new object();
            private DateTime _lastCollection;

            public StatisticsCollector()
            {
                _recentResults = new List<CompressionResult>();
                _lastCollection = DateTime.UtcNow;
            }

            public void AddResult(CompressionResult result)
            {
                lock (_lock)
                {
                    _recentResults.Add(result);

                    // Keep only last 1000 results;
                    if (_recentResults.Count > 1000)
                    {
                        _recentResults.RemoveRange(0, _recentResults.Count - 1000);
                    }
                }
            }

            public PerformanceStatistics GetPerformanceStatistics()
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    var lastHour = _recentResults.Where(r =>
                        r.Metadata.ContainsKey("CompletionTime") &&
                        (DateTime)r.Metadata["CompletionTime"] > now.AddHours(-1)).ToList();

                    var stats = new PerformanceStatistics;
                    {
                        TotalOperations = _recentResults.Count,
                        LastHourOperations = lastHour.Count,
                        AverageCompressionRatio = lastHour.Count > 0 ? lastHour.Average(r => r.CompressionRatio) : 0,
                        AverageCompressionSpeed = lastHour.Count > 0 ? lastHour.Average(r => r.Statistics.AverageCompressionSpeed) : 0,
                        AverageDecompressionSpeed = lastHour.Count > 0 ? lastHour.Average(r => r.Statistics.AverageDecompressionSpeed) : 0;
                    };

                    return stats;
                }
            }

            public class PerformanceStatistics;
            {
                public int TotalOperations { get; set; }
                public int LastHourOperations { get; set; }
                public float AverageCompressionRatio { get; set; }
                public float AverageCompressionSpeed { get; set; }
                public float AverageDecompressionSpeed { get; set; }
            }
        }

        /// <summary>
        /// Adaptive compression engine;
        /// </summary>
        private class AdaptiveCompressionEngine;
        {
            private readonly Dictionary<string, AlgorithmPerformance> _algorithmPerformance;
            private readonly object _lock = new object();

            public AdaptiveCompressionEngine()
            {
                _algorithmPerformance = new Dictionary<string, AlgorithmPerformance>();
            }

            public void Initialize()
            {
                // Load performance data from disk or initialize with defaults;
            }

            public ICompressionAlgorithm SelectAlgorithm(CompressionRequest request, List<ICompressionAlgorithm> algorithms)
            {
                if (algorithms.Count == 0)
                    return null;

                lock (_lock)
                {
                    // Simple selection: choose algorithm with best estimated ratio for this data size;
                    var bestAlgorithm = algorithms[0];
                    var bestScore = 0f;

                    foreach (var algorithm in algorithms)
                    {
                        var score = CalculateAlgorithmScore(request, algorithm);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestAlgorithm = algorithm;
                        }
                    }

                    return bestAlgorithm;
                }
            }

            private float CalculateAlgorithmScore(CompressionRequest request, ICompressionAlgorithm algorithm)
            {
                // Base score on estimated compression ratio;
                var ratioScore = algorithm.EstimateCompressionRatio(request) * 100;

                // Adjust based on algorithm performance history;
                var performance = GetAlgorithmPerformance(algorithm.Name);
                var performanceScore = performance.SuccessRate * 50;

                // Adjust based on speed (faster algorithms get higher score)
                var speedScore = (1.0f / Math.Max(performance.AverageTime.TotalSeconds, 0.1f)) * 10;

                return ratioScore + performanceScore + speedScore;
            }

            private AlgorithmPerformance GetAlgorithmPerformance(string algorithmName)
            {
                if (!_algorithmPerformance.TryGetValue(algorithmName, out var performance))
                {
                    performance = new AlgorithmPerformance();
                    _algorithmPerformance[algorithmName] = performance;
                }
                return performance;
            }

            public void UpdatePerformance(string algorithmName, CompressionResult result)
            {
                lock (_lock)
                {
                    var performance = GetAlgorithmPerformance(algorithmName);
                    performance.TotalOperations++;

                    if (result.Success)
                        performance.SuccessfulOperations++;

                    performance.TotalTime += result.Statistics.CompressionTime;
                    performance.TotalOriginalSize += result.OriginalSize;
                    performance.TotalCompressedSize += result.CompressedSize;
                }
            }

            private class AlgorithmPerformance;
            {
                public int TotalOperations { get; set; }
                public int SuccessfulOperations { get; set; }
                public float SuccessRate => TotalOperations > 0 ? (float)SuccessfulOperations / TotalOperations : 0;
                public TimeSpan TotalTime { get; set; }
                public TimeSpan AverageTime => TotalOperations > 0 ? TimeSpan.FromTicks(TotalTime.Ticks / TotalOperations) : TimeSpan.Zero;
                public long TotalOriginalSize { get; set; }
                public long TotalCompressedSize { get; set; }
                public float AverageRatio => TotalOriginalSize > 0 ? (float)TotalCompressedSize / TotalOriginalSize : 0;
            }
        }

        /// <summary>
        /// Compression analyzer;
        /// </summary>
        private class CompressionAnalyzer;
        {
            public void Initialize()
            {
                // Initialize analyzer;
            }

            public DataType AnalyzeData(byte[] data)
            {
                // Simple data type analysis;
                if (data == null || data.Length == 0)
                    return DataType.Unknown;

                // Check for text data;
                if (IsTextData(data))
                    return DataType.Text;

                // Check for image data;
                if (IsImageData(data))
                    return DataType.Image;

                // Check for binary data patterns;
                if (IsExecutableData(data))
                    return DataType.Executable;

                return DataType.Binary;
            }

            private bool IsTextData(byte[] data)
            {
                // Check if data is mostly ASCII/UTF8;
                int textChars = 0;
                foreach (var b in data.Take(1000))
                {
                    if (b >= 32 && b <= 126 || b == 9 || b == 10 || b == 13)
                        textChars++;
                }
                return textChars > 900; // 90% text characters;
            }

            private bool IsImageData(byte[] data)
            {
                // Check for common image signatures;
                if (data.Length >= 2)
                {
                    // JPEG: FF D8;
                    if (data[0] == 0xFF && data[1] == 0xD8)
                        return true;

                    // PNG: 89 50 4E 47;
                    if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 &&
                        data[2] == 0x4E && data[3] == 0x47)
                        return true;
                }
                return false;
            }

            private bool IsExecutableData(byte[] data)
            {
                // Check for PE header (Windows executable)
                if (data.Length >= 2 && data[0] == 0x4D && data[1] == 0x5A) // MZ;
                    return true;

                // Check for ELF header (Linux executable)
                if (data.Length >= 4 && data[0] == 0x7F && data[1] == 0x45 &&
                    data[2] == 0x4C && data[3] == 0x46) // ELF;
                    return true;

                return false;
            }

            public enum DataType;
            {
                Unknown,
                Text,
                Image,
                Audio,
                Video,
                Executable,
                Archive,
                Database,
                Binary;
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
                    // Cancel all active compressions;
                    lock (_compressionLock)
                    {
                        foreach (var context in _activeCompressions.Values)
                        {
                            context.CancellationTokenSource.Cancel();
                        }
                        _activeCompressions.Clear();
                    }

                    // Clear collections;
                    _algorithms.Clear();
                    _batchResults.Clear();

                    // Dispose services;
                    _memoryPool = null;
                    _progressTracker = null;
                    _statisticsCollector = null;
                    _adaptiveEngine = null;
                    _compressionAnalyzer = null;

                    // Clear events;
                    OnCompressionStarted = null;
                    OnCompressionCompleted = null;
                    OnCompressionProgressChanged = null;
                    OnBatchCompressionStarted = null;
                    OnBatchCompressionCompleted = null;
                    OnAlgorithmRegistered = null;
                    OnAlgorithmUnregistered = null;
                    OnEngineError = null;
                    OnMemoryPoolWarning = null;
                }

                _isDisposed = true;
            }
        }

        ~CompressionEngine()
        {
            Dispose(false);
        }

        #endregion;
    }
}
