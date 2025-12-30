using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using NEDA.KnowledgeBase.DataManagement;
using NEDA.Services.FileService;
using NEDA.Services.ProjectService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.EngineIntegration.AssetManager;
{
    /// <summary>
    /// Asset export engine for multiple game engines and formats;
    /// Supports batch exporting, format conversion, and validation;
    /// </summary>
    public interface IAssetExporter;
    {
        /// <summary>
        /// Exports an asset to specified format;
        /// </summary>
        Task<ExportResult> ExportAssetAsync(AssetExportRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Exports multiple assets in batch mode;
        /// </summary>
        Task<BatchExportResult> ExportBatchAsync(BatchExportRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Converts asset from one format to another;
        /// </summary>
        Task<ConversionResult> ConvertAssetAsync(AssetConversionRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates asset for export;
        /// </summary>
        Task<ValidationResult> ValidateForExportAsync(AssetValidationRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets supported export formats for asset type;
        /// </summary>
        IEnumerable<ExportFormat> GetSupportedFormats(AssetType assetType);
    }

    /// <summary>
    /// Main implementation of asset exporter;
    /// </summary>
    public class AssetExporter : IAssetExporter, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IFileManager _fileManager;
        private readonly IProjectManager _projectManager;
        private readonly IExportFormatRegistry _formatRegistry
        private readonly IExportPipelineFactory _pipelineFactory;
        private readonly ExportConfiguration _configuration;
        private readonly List<IExportValidator> _validators;
        private readonly Dictionary<string, IExportProcessor> _processors;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of AssetExporter;
        /// </summary>
        public AssetExporter(
            ILogger logger,
            IFileManager fileManager,
            IProjectManager projectManager,
            IExportFormatRegistry formatRegistry,
            IExportPipelineFactory pipelineFactory,
            ExportConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
            _formatRegistry = formatRegistry ?? throw new ArgumentNullException(nameof(formatRegistry));
            _pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _validators = new List<IExportValidator>();
            _processors = new Dictionary<string, IExportProcessor>();

            InitializeProcessors();
            RegisterValidators();

            _logger.LogInformation("AssetExporter initialized with {ProcessorCount} processors and {ValidatorCount} validators",
                _processors.Count, _validators.Count);
        }

        private void InitializeProcessors()
        {
            // Register built-in processors;
            RegisterProcessor(new FbxExportProcessor(_logger, _configuration));
            RegisterProcessor(new ObjExportProcessor(_logger, _configuration));
            RegisterProcessor(new GltfExportProcessor(_logger, _configuration));
            RegisterProcessor(new UnityPackageExportProcessor(_logger, _configuration));
            RegisterProcessor(new UnrealExportProcessor(_logger, _configuration));
            RegisterProcessor(new ImageExportProcessor(_logger, _configuration));
            RegisterProcessor(new AudioExportProcessor(_logger, _configuration));
            RegisterProcessor(new AnimationExportProcessor(_logger, _configuration));
        }

        private void RegisterValidators()
        {
            _validators.Add(new MeshValidator(_logger));
            _validators.Add(new TextureValidator(_logger));
            _validators.Add(new MaterialValidator(_logger));
            _validators.Add(new AnimationValidator(_logger));
            _validators.Add(new PhysicsValidator(_logger));
            _validators.Add(new PerformanceValidator(_logger));
        }

        private void RegisterProcessor(IExportProcessor processor)
        {
            if (processor == null) return;

            foreach (var format in processor.SupportedFormats)
            {
                _processors[format.Key.ToLowerInvariant()] = processor;
                _logger.LogDebug("Registered processor {ProcessorName} for format {Format}",
                    processor.GetType().Name, format.Key);
            }
        }

        /// <inheritdoc/>
        public async Task<ExportResult> ExportAssetAsync(AssetExportRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Starting asset export operation {OperationId} for asset: {AssetPath}",
                    operationId, request.SourcePath);

                // Validate request;
                var validationResult = await ValidateExportRequestAsync(request, cancellationToken);
                if (!validationResult.IsValid)
                {
                    return ExportResult.Failure(validationResult.Errors, $"Validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // Create export pipeline;
                var pipeline = _pipelineFactory.CreatePipeline(request.Format, request.Options);

                // Execute export pipeline;
                var context = new ExportContext;
                {
                    Request = request,
                    OperationId = operationId,
                    CancellationToken = cancellationToken;
                };

                var exportData = await pipeline.ExecuteAsync(context);

                stopwatch.Stop();

                _logger.LogInformation("Asset export completed successfully. Operation: {OperationId}, Duration: {Duration}ms, Output: {OutputPath}",
                    operationId, stopwatch.ElapsedMilliseconds, request.OutputPath);

                return ExportResult.Success(
                    exportData.OutputPath,
                    exportData.Stats,
                    exportData.Metadata);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Asset export operation {OperationId} was cancelled", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export asset. Operation: {OperationId}, Source: {SourcePath}, Format: {Format}",
                    operationId, request.SourcePath, request.Format);

                return ExportResult.Failure(new[] { ex.Message }, $"Export failed: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public async Task<BatchExportResult> ExportBatchAsync(BatchExportRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation("Starting batch export operation {OperationId} with {AssetCount} assets",
                operationId, request.Assets.Count);

            var results = new List<ExportResult>();
            var completedCount = 0;
            var failedCount = 0;

            // Configure parallel options based on system capabilities;
            var parallelOptions = new ParallelOptions;
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _configuration.MaxParallelExports;
            };

            try
            {
                await Parallel.ForEachAsync(
                    request.Assets,
                    parallelOptions,
                    async (assetRequest, ct) =>
                    {
                        try
                        {
                            var result = await ExportAssetAsync(assetRequest, ct);
                            lock (results)
                            {
                                results.Add(result);
                                if (result.Success)
                                    completedCount++;
                                else;
                                    failedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to export asset in batch: {AssetPath}", assetRequest.SourcePath);
                            lock (results)
                            {
                                results.Add(ExportResult.Failure(new[] { ex.Message }, $"Batch export failed: {ex.Message}"));
                                failedCount++;
                            }
                        }
                    });

                stopwatch.Stop();

                _logger.LogInformation("Batch export operation {OperationId} completed. Total: {Total}, Success: {Success}, Failed: {Failed}, Duration: {Duration}ms",
                    operationId, request.Assets.Count, completedCount, failedCount, stopwatch.ElapsedMilliseconds);

                return new BatchExportResult;
                {
                    OperationId = operationId,
                    TotalAssets = request.Assets.Count,
                    SuccessfulExports = completedCount,
                    FailedExports = failedCount,
                    Results = results,
                    Duration = stopwatch.Elapsed,
                    Success = failedCount == 0;
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Batch export operation {OperationId} was cancelled", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch export operation {OperationId} failed", operationId);
                throw new AssetExportException($"Batch export failed: {ex.Message}", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<ConversionResult> ConvertAssetAsync(AssetConversionRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var operationId = Guid.NewGuid();

            _logger.LogInformation("Starting asset conversion operation {OperationId}: {SourceFormat} -> {TargetFormat}",
                operationId, request.SourceFormat, request.TargetFormat);

            try
            {
                // Check if conversion is supported;
                if (!IsConversionSupported(request.SourceFormat, request.TargetFormat))
                {
                    throw new NotSupportedException($"Conversion from {request.SourceFormat} to {request.TargetFormat} is not supported");
                }

                // Create intermediate export request;
                var exportRequest = new AssetExportRequest;
                {
                    SourcePath = request.SourcePath,
                    OutputPath = GetTempOutputPath(request.TargetFormat),
                    Format = request.TargetFormat,
                    Options = request.Options;
                };

                // Perform export;
                var exportResult = await ExportAssetAsync(exportRequest, cancellationToken);

                if (!exportResult.Success)
                {
                    return ConversionResult.Failure(exportResult.Errors, $"Conversion failed: {string.Join(", ", exportResult.Errors)}");
                }

                // Post-process converted asset;
                await PostProcessConversionAsync(exportRequest.OutputPath, request, cancellationToken);

                _logger.LogInformation("Asset conversion completed successfully. Operation: {OperationId}, Output: {OutputPath}",
                    operationId, exportRequest.OutputPath);

                return ConversionResult.Success(
                    exportRequest.OutputPath,
                    exportResult.Stats,
                    exportResult.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Asset conversion failed. Operation: {OperationId}, Source: {SourcePath}",
                    operationId, request.SourcePath);

                throw new AssetConversionException($"Asset conversion failed: {ex.Message}", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<ValidationResult> ValidateForExportAsync(AssetValidationRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var validationErrors = new List<string>();
            var warnings = new List<string>();
            var validationStopwatch = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogDebug("Starting validation for asset: {AssetPath}", request.AssetPath);

            try
            {
                // Run all validators in parallel;
                var validationTasks = _validators;
                    .Where(v => v.CanValidate(request.AssetType))
                    .Select(v => v.ValidateAsync(request, cancellationToken))
                    .ToList();

                await Task.WhenAll(validationTasks);

                // Collect results;
                foreach (var task in validationTasks)
                {
                    var result = await task;
                    validationErrors.AddRange(result.Errors);
                    warnings.AddRange(result.Warnings);
                }

                validationStopwatch.Stop();

                var isValid = !validationErrors.Any();
                var status = isValid ? "VALID" : "INVALID";

                _logger.LogInformation("Asset validation completed. Path: {AssetPath}, Status: {Status}, Errors: {ErrorCount}, Warnings: {WarningCount}, Duration: {Duration}ms",
                    request.AssetPath, status, validationErrors.Count, warnings.Count, validationStopwatch.ElapsedMilliseconds);

                return new ValidationResult;
                {
                    IsValid = isValid,
                    Errors = validationErrors,
                    Warnings = warnings,
                    ValidationTime = validationStopwatch.Elapsed;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Asset validation failed for: {AssetPath}", request.AssetPath);

                return new ValidationResult;
                {
                    IsValid = false,
                    Errors = new[] { $"Validation process failed: {ex.Message}" },
                    Warnings = warnings;
                };
            }
        }

        /// <inheritdoc/>
        public IEnumerable<ExportFormat> GetSupportedFormats(AssetType assetType)
        {
            return _formatRegistry.GetFormatsForAssetType(assetType)
                .OrderBy(f => f.Name)
                .ToList();
        }

        private async Task<ValidationResult> ValidateExportRequestAsync(AssetExportRequest request, CancellationToken cancellationToken)
        {
            var errors = new List<string>();

            // Check if source file exists;
            if (!await _fileManager.ExistsAsync(request.SourcePath))
            {
                errors.Add($"Source file does not exist: {request.SourcePath}");
            }

            // Check if format is supported;
            var supportedFormats = GetSupportedFormats(request.AssetType);
            if (!supportedFormats.Any(f => f.Key.Equals(request.Format, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Format '{request.Format}' is not supported for asset type '{request.AssetType}'");
            }

            // Check output directory permissions;
            var outputDir = Path.GetDirectoryName(request.OutputPath);
            if (!string.IsNullOrEmpty(outputDir) && !await _fileManager.HasWriteAccessAsync(outputDir))
            {
                errors.Add($"No write access to output directory: {outputDir}");
            }

            // Validate file size limits;
            var fileSize = await _fileManager.GetFileSizeAsync(request.SourcePath);
            if (fileSize > _configuration.MaxFileSizeBytes)
            {
                errors.Add($"File size ({fileSize} bytes) exceeds maximum limit ({_configuration.MaxFileSizeBytes} bytes)");
            }

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private bool IsConversionSupported(string sourceFormat, string targetFormat)
        {
            // Get processors for both formats;
            var sourceProcessor = GetProcessorForFormat(sourceFormat);
            var targetProcessor = GetProcessorForFormat(targetFormat);

            if (sourceProcessor == null || targetProcessor == null)
                return false;

            // Check if conversion is in supported conversion matrix;
            return _formatRegistry.IsConversionSupported(sourceFormat, targetFormat);
        }

        private IExportProcessor GetProcessorForFormat(string format)
        {
            var key = format.ToLowerInvariant();
            return _processors.TryGetValue(key, out var processor) ? processor : null;
        }

        private string GetTempOutputPath(string format)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "NEDA_Exports");
            Directory.CreateDirectory(tempDir);

            var fileName = $"export_{Guid.NewGuid():N}.{GetDefaultExtension(format)}";
            return Path.Combine(tempDir, fileName);
        }

        private string GetDefaultExtension(string format)
        {
            return _formatRegistry.GetDefaultExtension(format) ?? format.ToLowerInvariant();
        }

        private async Task PostProcessConversionAsync(string outputPath, AssetConversionRequest request, CancellationToken cancellationToken)
        {
            if (request.PostProcessingOptions?.Any() != true)
                return;

            _logger.LogDebug("Applying post-processing to converted asset: {OutputPath}", outputPath);

            foreach (var option in request.PostProcessingOptions)
            {
                switch (option)
                {
                    case PostProcessOption.OptimizeMesh:
                        await OptimizeMeshAsync(outputPath, cancellationToken);
                        break;
                    case PostProcessOption.GenerateMipMaps:
                        await GenerateMipMapsAsync(outputPath, cancellationToken);
                        break;
                    case PostProcessOption.CompressTextures:
                        await CompressTexturesAsync(outputPath, cancellationToken);
                        break;
                    case PostProcessOption.GenerateLODs:
                        await GenerateLODsAsync(outputPath, request.LodSettings, cancellationToken);
                        break;
                    case PostProcessOption.ValidateOutput:
                        await ValidateOutputAssetAsync(outputPath, request.TargetFormat, cancellationToken);
                        break;
                }
            }
        }

        private async Task OptimizeMeshAsync(string filePath, CancellationToken cancellationToken)
        {
            // Implementation for mesh optimization;
            _logger.LogDebug("Optimizing mesh: {FilePath}", filePath);
            await Task.Delay(100, cancellationToken); // Placeholder;
        }

        private async Task GenerateMipMapsAsync(string filePath, CancellationToken cancellationToken)
        {
            // Implementation for mipmap generation;
            _logger.LogDebug("Generating mipmaps for: {FilePath}", filePath);
            await Task.Delay(100, cancellationToken); // Placeholder;
        }

        private async Task CompressTexturesAsync(string filePath, CancellationToken cancellationToken)
        {
            // Implementation for texture compression;
            _logger.LogDebug("Compressing textures in: {FilePath}", filePath);
            await Task.Delay(100, cancellationToken); // Placeholder;
        }

        private async Task GenerateLODsAsync(string filePath, LodSettings settings, CancellationToken cancellationToken)
        {
            // Implementation for LOD generation;
            _logger.LogDebug("Generating LODs for: {FilePath}", filePath);
            await Task.Delay(100, cancellationToken); // Placeholder;
        }

        private async Task ValidateOutputAssetAsync(string filePath, string format, CancellationToken cancellationToken)
        {
            var validationRequest = new AssetValidationRequest;
            {
                AssetPath = filePath,
                AssetType = AssetTypeHelper.DetectAssetType(format),
                ValidationLevel = ValidationLevel.Strict;
            };

            var result = await ValidateForExportAsync(validationRequest, cancellationToken);

            if (!result.IsValid)
            {
                throw new AssetValidationException($"Output asset validation failed: {string.Join(", ", result.Errors)}");
            }
        }

        /// <summary>
        /// Clean up resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                foreach (var processor in _processors.Values.OfType<IDisposable>())
                {
                    processor.Dispose();
                }
                _processors.Clear();
                _validators.Clear();
            }

            _disposed = true;
        }

        ~AssetExporter()
        {
            Dispose(false);
        }
    }

    #region Supporting Types and Interfaces;

    public class AssetExportRequest;
    {
        public required string SourcePath { get; set; }
        public required string OutputPath { get; set; }
        public required string Format { get; set; }
        public AssetType AssetType { get; set; } = AssetType.AutoDetect;
        public ExportOptions Options { get; set; } = new ExportOptions();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class BatchExportRequest;
    {
        public required List<AssetExportRequest> Assets { get; set; }
        public BatchExportOptions Options { get; set; } = new BatchExportOptions();
    }

    public class AssetConversionRequest;
    {
        public required string SourcePath { get; set; }
        public required string SourceFormat { get; set; }
        public required string TargetFormat { get; set; }
        public ExportOptions Options { get; set; } = new ExportOptions();
        public List<PostProcessOption> PostProcessingOptions { get; set; } = new List<PostProcessOption>();
        public LodSettings LodSettings { get; set; }
    }

    public class AssetValidationRequest;
    {
        public required string AssetPath { get; set; }
        public AssetType AssetType { get; set; }
        public ValidationLevel ValidationLevel { get; set; } = ValidationLevel.Normal;
    }

    public class ExportResult;
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public ExportStatistics Stats { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime ExportTime { get; set; } = DateTime.UtcNow;

        public static ExportResult Success(string outputPath, ExportStatistics stats, Dictionary<string, object> metadata)
        {
            return new ExportResult;
            {
                Success = true,
                OutputPath = outputPath,
                Stats = stats,
                Metadata = metadata;
            };
        }

        public static ExportResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new ExportResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class BatchExportResult;
    {
        public Guid OperationId { get; set; }
        public bool Success { get; set; }
        public int TotalAssets { get; set; }
        public int SuccessfulExports { get; set; }
        public int FailedExports { get; set; }
        public List<ExportResult> Results { get; set; } = new List<ExportResult>();
        public TimeSpan Duration { get; set; }
        public ExportStatistics TotalStats { get; set; } = new ExportStatistics();
    }

    public class ConversionResult;
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public ExportStatistics Stats { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public static ConversionResult Success(string outputPath, ExportStatistics stats, Dictionary<string, object> metadata)
        {
            return new ConversionResult;
            {
                Success = true,
                OutputPath = outputPath,
                Stats = stats,
                Metadata = metadata;
            };
        }

        public static ConversionResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new ConversionResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public TimeSpan ValidationTime { get; set; }
    }

    public class ExportOptions;
    {
        public bool PreserveHierarchy { get; set; } = true;
        public bool GenerateColliders { get; set; } = false;
        public bool ExportAnimations { get; set; } = true;
        public bool ExportMaterials { get; set; } = true;
        public bool ExportTextures { get; set; } = true;
        public TextureCompression TextureCompression { get; set; } = TextureCompression.None;
        public MeshOptimization MeshOptimization { get; set; } = MeshOptimization.None;
        public float ScaleFactor { get; set; } = 1.0f;
        public CoordinateSystem CoordinateSystem { get; set; } = CoordinateSystem.RightHanded;
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    public class BatchExportOptions;
    {
        public bool StopOnFirstError { get; set; } = false;
        public int MaxParallelExports { get; set; } = Environment.ProcessorCount;
        public bool GenerateReport { get; set; } = true;
        public string ReportOutputPath { get; set; }
    }

    public class ExportStatistics;
    {
        public long InputSizeBytes { get; set; }
        public long OutputSizeBytes { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public int MaterialCount { get; set; }
        public int TextureCount { get; set; }
        public int AnimationCount { get; set; }
        public double CompressionRatio => InputSizeBytes > 0 ? (double)OutputSizeBytes / InputSizeBytes : 1.0;
    }

    public class LodSettings;
    {
        public List<float> ScreenSizes { get; set; } = new List<float> { 1.0f, 0.5f, 0.2f };
        public float ReductionFactor { get; set; } = 0.5f;
        public bool AutoGenerate { get; set; } = true;
        public int MaxLODLevel { get; set; } = 3;
    }

    public enum AssetType;
    {
        AutoDetect,
        Mesh,
        Texture,
        Material,
        Animation,
        Audio,
        Video,
        Scene,
        Prefab,
        Script,
        Shader,
        Font,
        Data;
    }

    public enum TextureCompression;
    {
        None,
        DXT1,
        DXT5,
        BC7,
        ETC2,
        ASTC,
        PVRTC;
    }

    public enum MeshOptimization;
    {
        None,
        Simplify,
        Reorder,
        Merge,
        Split,
        Cleanup;
    }

    public enum CoordinateSystem;
    {
        RightHanded,
        LeftHanded,
        YUp,
        ZUp;
    }

    public enum PostProcessOption;
    {
        OptimizeMesh,
        GenerateMipMaps,
        CompressTextures,
        GenerateLODs,
        ValidateOutput,
        CreateThumbnail,
        GenerateMetadata;
    }

    public enum ValidationLevel;
    {
        Basic,
        Normal,
        Strict,
        Exhaustive;
    }

    public class ExportFormat;
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string[] Extensions { get; set; }
        public AssetType[] SupportedAssetTypes { get; set; }
        public bool SupportsAnimation { get; set; }
        public bool SupportsMaterials { get; set; }
        public bool SupportsTextures { get; set; }
        public int MaxFileSize { get; set; } = 100 * 1024 * 1024; // 100MB default;
    }

    public interface IExportProcessor;
    {
        string Name { get; }
        string Version { get; }
        Dictionary<string, ExportFormat> SupportedFormats { get; }
        Task<ExportData> ProcessAsync(ExportContext context);
        bool CanProcess(string format);
    }

    public interface IExportValidator;
    {
        string Name { get; }
        bool CanValidate(AssetType assetType);
        Task<ValidationResult> ValidateAsync(AssetValidationRequest request, CancellationToken cancellationToken);
    }

    public interface IExportFormatRegistry
    {
        IEnumerable<ExportFormat> GetFormatsForAssetType(AssetType assetType);
        ExportFormat GetFormat(string key);
        bool IsConversionSupported(string sourceFormat, string targetFormat);
        string GetDefaultExtension(string format);
        void RegisterFormat(ExportFormat format);
        void UnregisterFormat(string key);
    }

    public interface IExportPipelineFactory;
    {
        IExportPipeline CreatePipeline(string format, ExportOptions options);
    }

    public interface IExportPipeline;
    {
        Task<ExportData> ExecuteAsync(ExportContext context);
    }

    public class ExportContext;
    {
        public AssetExportRequest Request { get; set; }
        public Guid OperationId { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Dictionary<string, object> State { get; set; } = new Dictionary<string, object>();
    }

    public class ExportData;
    {
        public string OutputPath { get; set; }
        public ExportStatistics Stats { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class ExportConfiguration;
    {
        public string DefaultExportDirectory { get; set; } = "Exports";
        public int MaxParallelExports { get; set; } = Environment.ProcessorCount;
        public long MaxFileSizeBytes { get; set; } = 1024L * 1024 * 1024; // 1GB;
        public bool EnableLogging { get; set; } = true;
        public bool EnableValidation { get; set; } = true;
        public bool CreateBackup { get; set; } = true;
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public Dictionary<string, object> FormatSettings { get; set; } = new Dictionary<string, object>();
    }

    public static class AssetTypeHelper;
    {
        public static AssetType DetectAssetType(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();

            return extension switch;
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".stl" or ".ply" => AssetType.Mesh,
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".tiff" or ".exr" or ".psd" => AssetType.Texture,
                ".mat" or ".material" or ".mtl" => AssetType.Material,
                ".anim" or ".animation" or ".fbx" => AssetType.Animation,
                ".wav" or ".mp3" or ".ogg" or ".flac" => AssetType.Audio,
                ".mp4" or ".avi" or ".mov" or ".wmv" => AssetType.Video,
                ".unity" or ".uasset" or ".umap" => AssetType.Scene,
                ".prefab" => AssetType.Prefab,
                ".cs" or ".cpp" or ".h" => AssetType.Script,
                ".shader" or ".hlsl" or ".glsl" => AssetType.Shader,
                ".ttf" or ".otf" or ".fon" => AssetType.Font,
                _ => AssetType.Data;
            };
        }

        public static AssetType DetectAssetType(string format)
        {
            return format.ToLowerInvariant() switch;
            {
                "fbx" or "obj" or "gltf" or "glb" => AssetType.Mesh,
                "png" or "jpg" or "tga" or "exr" => AssetType.Texture,
                "material" or "mtl" => AssetType.Material,
                "anim" or "animation" => AssetType.Animation,
                "wav" or "mp3" => AssetType.Audio,
                "mp4" or "avi" => AssetType.Video,
                _ => AssetType.Data;
            };
        }
    }

    public class AssetExportException : Exception
    {
        public AssetExportException(string message) : base(message) { }
        public AssetExportException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AssetConversionException : Exception
    {
        public AssetConversionException(string message) : base(message) { }
        public AssetConversionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AssetValidationException : Exception
    {
        public AssetValidationException(string message) : base(message) { }
        public AssetValidationException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Processor Implementations (Partial Examples)

    internal class FbxExportProcessor : IExportProcessor, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ExportConfiguration _configuration;
        private bool _disposed;

        public string Name => "FBX Export Processor";
        public string Version => "1.0.0";

        public Dictionary<string, ExportFormat> SupportedFormats { get; }

        public FbxExportProcessor(ILogger logger, ExportConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            SupportedFormats = new Dictionary<string, ExportFormat>
            {
                ["fbx"] = new ExportFormat;
                {
                    Key = "fbx",
                    Name = "Autodesk FBX",
                    Description = "FBX format for 3D models and animations",
                    Extensions = new[] { ".fbx" },
                    SupportedAssetTypes = new[] { AssetType.Mesh, AssetType.Animation, AssetType.Scene },
                    SupportsAnimation = true,
                    SupportsMaterials = true,
                    SupportsTextures = true,
                    MaxFileSize = 500 * 1024 * 1024 // 500MB;
                }
            };
        }

        public bool CanProcess(string format)
        {
            return SupportedFormats.ContainsKey(format.ToLowerInvariant());
        }

        public async Task<ExportData> ProcessAsync(ExportContext context)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogDebug("FBX processor starting export: {SourcePath} -> {OutputPath}",
                    context.Request.SourcePath, context.Request.OutputPath);

                // Implementation would use FBX SDK or similar library;
                // This is a simplified example;

                await Task.Delay(100, context.CancellationToken); // Simulate processing;

                var stats = new ExportStatistics;
                {
                    InputSizeBytes = new FileInfo(context.Request.SourcePath).Length,
                    OutputSizeBytes = new FileInfo(context.Request.OutputPath).Length,
                    ProcessingTime = stopwatch.Elapsed,
                    VertexCount = 1000,
                    TriangleCount = 2000,
                    MaterialCount = 5,
                    TextureCount = 3,
                    AnimationCount = context.Request.Options.ExportAnimations ? 2 : 0;
                };

                var metadata = new Dictionary<string, object>
                {
                    ["formatVersion"] = "FBX 2020",
                    ["processor"] = Name,
                    ["processingTime"] = stopwatch.ElapsedMilliseconds;
                };

                stopwatch.Stop();

                return new ExportData;
                {
                    OutputPath = context.Request.OutputPath,
                    Stats = stats,
                    Metadata = metadata;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FBX processor failed for operation: {OperationId}", context.OperationId);
                throw new AssetExportException($"FBX export failed: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            // Cleanup FBX SDK resources;
            _disposed = true;
        }
    }

    // Additional processor implementations would follow similar pattern;
    internal class ObjExportProcessor : IExportProcessor { /* Implementation */ }
    internal class GltfExportProcessor : IExportProcessor { /* Implementation */ }
    internal class UnityPackageExportProcessor : IExportProcessor { /* Implementation */ }
    internal class UnrealExportProcessor : IExportProcessor { /* Implementation */ }
    internal class ImageExportProcessor : IExportProcessor { /* Implementation */ }
    internal class AudioExportProcessor : IExportProcessor { /* Implementation */ }
    internal class AnimationExportProcessor : IExportProcessor { /* Implementation */ }

    #endregion;

    #region Validator Implementations;

    internal class MeshValidator : IExportValidator;
    {
        private readonly ILogger _logger;

        public string Name => "Mesh Validator";

        public MeshValidator(ILogger logger)
        {
            _logger = logger;
        }

        public bool CanValidate(AssetType assetType)
        {
            return assetType == AssetType.Mesh || assetType == AssetType.AutoDetect;
        }

        public async Task<ValidationResult> ValidateAsync(AssetValidationRequest request, CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            try
            {
                // Validate mesh file;
                if (!File.Exists(request.AssetPath))
                {
                    errors.Add($"Mesh file not found: {request.AssetPath}");
                    return CreateResult(false, errors, warnings);
                }

                // Check file size;
                var fileInfo = new FileInfo(request.AssetPath);
                if (fileInfo.Length > 500 * 1024 * 1024) // 500MB limit;
                {
                    warnings.Add($"Mesh file is large: {fileInfo.Length / (1024 * 1024)}MB");
                }

                // Validate mesh structure (simplified example)
                await ValidateMeshStructureAsync(request.AssetPath, errors, warnings, cancellationToken);

                // Check for required components;
                await CheckMeshComponentsAsync(request.AssetPath, errors, warnings, cancellationToken);

                return CreateResult(!errors.Any(), errors, warnings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mesh validation failed for: {AssetPath}", request.AssetPath);
                errors.Add($"Validation error: {ex.Message}");
                return CreateResult(false, errors, warnings);
            }
        }

        private async Task ValidateMeshStructureAsync(string filePath, List<string> errors, List<string> warnings, CancellationToken cancellationToken)
        {
            // Implementation would parse mesh file and validate structure;
            await Task.Delay(10, cancellationToken); // Simulate validation;
        }

        private async Task CheckMeshComponentsAsync(string filePath, List<string> errors, List<string> warnings, CancellationToken cancellationToken)
        {
            // Implementation would check for vertices, triangles, normals, UVs, etc.
            await Task.Delay(10, cancellationToken); // Simulate check;
        }

        private ValidationResult CreateResult(bool isValid, List<string> errors, List<string> warnings)
        {
            return new ValidationResult;
            {
                IsValid = isValid,
                Errors = errors,
                Warnings = warnings;
            };
        }
    }

    internal class TextureValidator : IExportValidator { /* Implementation */ }
    internal class MaterialValidator : IExportValidator { /* Implementation */ }
    internal class AnimationValidator : IExportValidator { /* Implementation */ }
    internal class PhysicsValidator : IExportValidator { /* Implementation */ }
    internal class PerformanceValidator : IExportValidator { /* Implementation */ }

    #endregion;
}
