using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Numerics;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring;
using NEDA.ContentCreation.AssetPipeline.FormatConverters.Models;
using NEDA.ContentCreation.AssetPipeline.FormatConverters.Enums;
using NEDA.ContentCreation.AssetPipeline.ImportManagers;
using NEDA.ContentCreation.AssetPipeline.QualityOptimizers;

namespace NEDA.ContentCreation.AssetPipeline.FormatConverters;
{
    /// <summary>
    /// Advanced 3D model format conversion system with optimization, validation, and batch processing support;
    /// Supports FBX, GLTF, OBJ, STL, USD, and custom formats with real-time progress tracking;
    /// </summary>
    public class ModelConverter : IModelConverter, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IModelValidator _modelValidator;
        private readonly IOptimizationEngine _optimizationEngine;
        private readonly ITextureConverter _textureConverter;
        private readonly IAnimationConverter _animationConverter;

        private readonly Dictionary<ModelFormat, IFormatHandler> _formatHandlers;
        private readonly Dictionary<string, ConversionPreset> _conversionPresets;
        private readonly ConversionConfiguration _defaultConfiguration;

        private readonly SemaphoreSlim _conversionSemaphore;
        private readonly object _conversionLock = new object();
        private bool _isDisposed;

        public ConverterStatus Status { get; private set; }
        public ConverterStatistics Statistics { get; private set; }
        public int ActiveConversions { get; private set; }
        public int SupportedFormats => _formatHandlers.Count;

        #endregion;

        #region Events;

        public event EventHandler<ConversionStartedEventArgs> ConversionStarted;
        public event EventHandler<ConversionProgressEventArgs> ConversionProgress;
        public event EventHandler<ConversionCompletedEventArgs> ConversionCompleted;
        public event EventHandler<ConversionFailedEventArgs> ConversionFailed;
        public event EventHandler<FormatRegisteredEventArgs> FormatRegistered;
        public event EventHandler<PresetCreatedEventArgs> PresetCreated;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of ModelConverter with dependencies;
        /// </summary>
        public ModelConverter(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IModelValidator modelValidator = null,
            IOptimizationEngine optimizationEngine = null,
            ITextureConverter textureConverter = null,
            IAnimationConverter animationConverter = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _modelValidator = modelValidator ?? new DefaultModelValidator();
            _optimizationEngine = optimizationEngine ?? new DefaultOptimizationEngine();
            _textureConverter = textureConverter ?? new DefaultTextureConverter();
            _animationConverter = animationConverter ?? new DefaultAnimationConverter();

            _formatHandlers = new Dictionary<ModelFormat, IFormatHandler>();
            _conversionPresets = new Dictionary<string, ConversionPreset>();
            _defaultConfiguration = ConversionConfiguration.Default;

            _conversionSemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);

            Status = ConverterStatus.Ready;
            Statistics = new ConverterStatistics();

            RegisterBuiltInFormatHandlers();
            RegisterBuiltInPresets();

            _logger.LogInformation("ModelConverter initialized successfully");
        }

        private void RegisterBuiltInFormatHandlers()
        {
            // Register built-in format handlers;
            RegisterFormatHandler(ModelFormat.FBX, new FBXFormatHandler());
            RegisterFormatHandler(ModelFormat.GLTF, new GLTFFormatHandler());
            RegisterFormatHandler(ModelFormat.OBJ, new OBJFormatHandler());
            RegisterFormatHandler(ModelFormat.STL, new STLFormatHandler());
            RegisterFormatHandler(ModelFormat.USD, new USDFormatHandler());
            RegisterFormatHandler(ModelFormat.Blend, new BlendFormatHandler());
            RegisterFormatHandler(ModelFormat.Maya, new MayaFormatHandler());
            RegisterFormatHandler(ModelFormat.Max, new MaxFormatHandler());
            RegisterFormatHandler(ModelFormat.Unity, new UnityFormatHandler());
            RegisterFormatHandler(ModelFormat.Unreal, new UnrealFormatHandler());

            _logger.LogInformation($"Registered {_formatHandlers.Count} built-in format handlers");
        }

        private void RegisterBuiltInPresets()
        {
            // Game Engine Presets;
            CreatePreset("Unity_Mobile", new ConversionConfiguration;
            {
                TargetFormat = ModelFormat.Unity,
                OptimizationLevel = OptimizationLevel.High,
                MaxTextureSize = 2048,
                GenerateLODs = true,
                LODCount = 3,
                MergeMeshes = true,
                RemoveUnusedVertices = true,
                CompressTextures = true,
                CompressGeometry = true,
                GenerateLightmapUVs = true,
                MaxPolyCount = 50000;
            });

            CreatePreset("Unreal_HighQuality", new ConversionConfiguration;
            {
                TargetFormat = ModelFormat.Unreal,
                OptimizationLevel = OptimizationLevel.Medium,
                MaxTextureSize = 4096,
                GenerateLODs = true,
                LODCount = 5,
                PreserveNormals = true,
                PreserveUVs = true,
                GenerateCollision = true,
                GenerateNavMesh = false,
                ExportMaterials = true,
                ExportSkeleton = true,
                ExportAnimations = true;
            });

            CreatePreset("WebGL_Optimized", new ConversionConfiguration;
            {
                TargetFormat = ModelFormat.GLTF,
                OptimizationLevel = OptimizationLevel.Ultra,
                MaxTextureSize = 1024,
                GenerateLODs = false,
                MergeMeshes = true,
                CompressTextures = true,
                CompressGeometry = true,
                DracoCompression = true,
                QuantizeAttributes = true,
                MaxPolyCount = 10000,
                RemoveUnusedVertices = true;
            });

            // Industry Standard Presets;
            CreatePreset("Film_Production", new ConversionConfiguration;
            {
                TargetFormat = ModelFormat.FBX,
                OptimizationLevel = OptimizationLevel.Low,
                MaxTextureSize = 8192,
                GenerateLODs = false,
                PreserveNormals = true,
                PreserveUVs = true,
                PreserveVertexColors = true,
                PreserveCustomAttributes = true,
                ExportMaterials = true,
                ExportSkeleton = true,
                ExportAnimations = true,
                ExportBlendShapes = true;
            });

            CreatePreset("3D_Printing", new ConversionConfiguration;
            {
                TargetFormat = ModelFormat.STL,
                OptimizationLevel = OptimizationLevel.None,
                GenerateLODs = false,
                RepairGeometry = true,
                CloseHoles = true,
                EnsureManifold = true,
                UnitScale = 1.0f,
                ExportBinary = true;
            });

            _logger.LogInformation($"Registered {_conversionPresets.Count} built-in conversion presets");
        }

        #endregion;

        #region Format Handler Management;

        /// <summary>
        /// Registers a custom format handler;
        /// </summary>
        public void RegisterFormatHandler(ModelFormat format, IFormatHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_conversionLock)
            {
                _formatHandlers[format] = handler;

                _logger.LogInformation($"Registered format handler for {format}");
                OnFormatRegistered(new FormatRegisteredEventArgs(format, handler.GetType().Name));
            }
        }

        /// <summary>
        /// Unregisters a format handler;
        /// </summary>
        public bool UnregisterFormatHandler(ModelFormat format)
        {
            lock (_conversionLock)
            {
                var result = _formatHandlers.Remove(format);

                if (result)
                {
                    _logger.LogInformation($"Unregistered format handler for {format}");
                }

                return result;
            }
        }

        /// <summary>
        /// Gets the handler for a specific format;
        /// </summary>
        public IFormatHandler GetFormatHandler(ModelFormat format)
        {
            lock (_conversionLock)
            {
                return _formatHandlers.GetValueOrDefault(format);
            }
        }

        /// <summary>
        /// Checks if a format is supported;
        /// </summary>
        public bool IsFormatSupported(ModelFormat format)
        {
            lock (_conversionLock)
            {
                return _formatHandlers.ContainsKey(format);
            }
        }

        /// <summary>
        /// Gets all supported input formats;
        /// </summary>
        public IEnumerable<ModelFormat> GetSupportedInputFormats()
        {
            lock (_conversionLock)
            {
                return _formatHandlers.Keys.ToList();
            }
        }

        /// <summary>
        /// Gets all supported output formats for a given input format;
        /// </summary>
        public IEnumerable<ModelFormat> GetSupportedOutputFormats(ModelFormat inputFormat)
        {
            var handler = GetFormatHandler(inputFormat);
            if (handler == null)
                return Enumerable.Empty<ModelFormat>();

            return handler.GetSupportedOutputFormats();
        }

        #endregion;

        #region Preset Management;

        /// <summary>
        /// Creates a new conversion preset;
        /// </summary>
        public ConversionPreset CreatePreset(string name, ConversionConfiguration configuration)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Preset name cannot be empty", nameof(name));

            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            lock (_conversionLock)
            {
                if (_conversionPresets.ContainsKey(name))
                    throw new ConversionException($"Preset '{name}' already exists");

                var preset = new ConversionPreset;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    Configuration = configuration,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    UsageCount = 0;
                };

                _conversionPresets[name] = preset;

                _logger.LogInformation($"Created conversion preset '{name}'");
                OnPresetCreated(new PresetCreatedEventArgs(preset));

                return preset;
            }
        }

        /// <summary>
        /// Gets a conversion preset by name;
        /// </summary>
        public ConversionPreset GetPreset(string name)
        {
            lock (_conversionLock)
            {
                return _conversionPresets.GetValueOrDefault(name);
            }
        }

        /// <summary>
        /// Updates an existing preset;
        /// </summary>
        public bool UpdatePreset(string name, ConversionConfiguration configuration)
        {
            lock (_conversionLock)
            {
                if (!_conversionPresets.TryGetValue(name, out var preset))
                    return false;

                preset.Configuration = configuration;
                preset.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation($"Updated conversion preset '{name}'");
                return true;
            }
        }

        /// <summary>
        /// Deletes a preset;
        /// </summary>
        public bool DeletePreset(string name)
        {
            lock (_conversionLock)
            {
                var result = _conversionPresets.Remove(name);

                if (result)
                {
                    _logger.LogInformation($"Deleted conversion preset '{name}'");
                }

                return result;
            }
        }

        /// <summary>
        /// Lists all available presets;
        /// </summary>
        public IEnumerable<ConversionPreset> ListPresets()
        {
            lock (_conversionLock)
            {
                return _conversionPresets.Values.ToList();
            }
        }

        /// <summary>
        /// Gets presets suitable for a specific target format;
        /// </summary>
        public IEnumerable<ConversionPreset> GetPresetsForFormat(ModelFormat targetFormat)
        {
            lock (_conversionLock)
            {
                return _conversionPresets.Values;
                    .Where(p => p.Configuration.TargetFormat == targetFormat)
                    .OrderByDescending(p => p.UsageCount)
                    .ToList();
            }
        }

        #endregion;

        #region Single Model Conversion;

        /// <summary>
        /// Converts a single 3D model between formats with optional optimization;
        /// </summary>
        public async Task<ConversionResult> ConvertModelAsync(
            string sourcePath,
            string destinationPath,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            CancellationToken cancellationToken = default)
        {
            ValidateConversionParameters(sourcePath, destinationPath, targetFormat);

            configuration ??= _defaultConfiguration;
            configuration.TargetFormat = targetFormat;

            var conversionId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                await _conversionSemaphore.WaitAsync(cancellationToken);
                Interlocked.Increment(ref ActiveConversions);
                Status = ConverterStatus.Processing;

                // Start conversion;
                OnConversionStarted(new ConversionStartedEventArgs(conversionId, sourcePath, destinationPath, targetFormat));
                _logger.LogInformation($"Starting conversion {conversionId}: {Path.GetFileName(sourcePath)} -> {targetFormat}");

                // Determine source format;
                var sourceFormat = DetectFileFormat(sourcePath);
                if (sourceFormat == ModelFormat.Unknown)
                    throw new ConversionException($"Unable to detect format of source file: {sourcePath}");

                // Check format support;
                if (!IsFormatSupported(sourceFormat))
                    throw new ConversionException($"Source format {sourceFormat} is not supported");

                if (!IsFormatSupported(targetFormat))
                    throw new ConversionException($"Target format {targetFormat} is not supported");

                // Validate source file;
                var validationResult = await ValidateSourceFileAsync(sourcePath, sourceFormat, cancellationToken);
                if (!validationResult.IsValid)
                    throw new ConversionException($"Source file validation failed: {string.Join(", ", validationResult.Errors)}");

                // Load source model;
                OnConversionProgress(new ConversionProgressEventArgs(conversionId, 10, "Loading source model"));
                var sourceModel = await LoadModelAsync(sourcePath, sourceFormat, cancellationToken);

                // Apply configuration;
                await ApplyConfigurationAsync(sourceModel, configuration, cancellationToken);

                // Optimize model if requested;
                if (configuration.OptimizationLevel != OptimizationLevel.None)
                {
                    OnConversionProgress(new ConversionProgressEventArgs(conversionId, 30, "Optimizing model"));
                    sourceModel = await OptimizeModelAsync(sourceModel, configuration, cancellationToken);
                }

                // Process textures;
                if (configuration.ProcessTextures && sourceModel.HasTextures)
                {
                    OnConversionProgress(new ConversionProgressEventArgs(conversionId, 50, "Processing textures"));
                    await ProcessTexturesAsync(sourceModel, configuration, cancellationToken);
                }

                // Process animations;
                if (configuration.ProcessAnimations && sourceModel.HasAnimations)
                {
                    OnConversionProgress(new ConversionProgressEventArgs(conversionId, 60, "Processing animations"));
                    await ProcessAnimationsAsync(sourceModel, configuration, cancellationToken);
                }

                // Generate LODs if requested;
                if (configuration.GenerateLODs && configuration.LODCount > 1)
                {
                    OnConversionProgress(new ConversionProgressEventArgs(conversionId, 70, "Generating LODs"));
                    await GenerateLODsAsync(sourceModel, configuration, cancellationToken);
                }

                // Convert to target format;
                OnConversionProgress(new ConversionProgressEventArgs(conversionId, 80, $"Converting to {targetFormat}"));
                var convertedData = await ConvertFormatAsync(sourceModel, sourceFormat, targetFormat, configuration, cancellationToken);

                // Save converted model;
                OnConversionProgress(new ConversionProgressEventArgs(conversionId, 90, "Saving converted model"));
                await SaveModelAsync(destinationPath, convertedData, targetFormat, configuration, cancellationToken);

                // Validate output;
                OnConversionProgress(new ConversionProgressEventArgs(conversionId, 95, "Validating output"));
                var outputValidation = await ValidateOutputFileAsync(destinationPath, targetFormat, cancellationToken);

                // Calculate statistics;
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                var result = new ConversionResult;
                {
                    ConversionId = conversionId,
                    SourcePath = sourcePath,
                    DestinationPath = destinationPath,
                    SourceFormat = sourceFormat,
                    TargetFormat = targetFormat,
                    Success = true,
                    StartTime = startTime,
                    EndTime = endTime,
                    Duration = duration,
                    SourceStats = sourceModel.Statistics,
                    OutputStats = await GetModelStatisticsAsync(destinationPath, targetFormat, cancellationToken),
                    Warnings = validationResult.Warnings.Concat(outputValidation.Warnings).ToList(),
                    Configuration = configuration,
                    Metadata = new ConversionMetadata;
                    {
                        FileSize = new FileInfo(destinationPath).Length,
                        CompressionRatio = CalculateCompressionRatio(sourcePath, destinationPath),
                        OptimizationApplied = configuration.OptimizationLevel != OptimizationLevel.None,
                        LODsGenerated = configuration.GenerateLODs;
                    }
                };

                // Update statistics;
                UpdateStatistics(result, true);

                // Complete conversion;
                OnConversionProgress(new ConversionProgressEventArgs(conversionId, 100, "Conversion complete"));
                OnConversionCompleted(new ConversionCompletedEventArgs(conversionId, result));

                _logger.LogInformation($"Conversion {conversionId} completed successfully in {duration.TotalSeconds:F2}s");

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Conversion {conversionId} was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                var errorResult = new ConversionResult;
                {
                    ConversionId = conversionId,
                    SourcePath = sourcePath,
                    DestinationPath = destinationPath,
                    TargetFormat = targetFormat,
                    Success = false,
                    StartTime = startTime,
                    EndTime = DateTime.UtcNow,
                    ErrorMessage = ex.Message,
                    Exception = ex;
                };

                UpdateStatistics(errorResult, false);

                OnConversionFailed(new ConversionFailedEventArgs(conversionId, sourcePath, targetFormat, ex.Message, ex));
                _logger.LogError(ex, $"Conversion {conversionId} failed: {ex.Message}");

                throw new ConversionException($"Model conversion failed: {ex.Message}", ex);
            }
            finally
            {
                Interlocked.Decrement(ref ActiveConversions);
                _conversionSemaphore.Release();

                if (ActiveConversions == 0)
                {
                    Status = ConverterStatus.Ready;
                }
            }
        }

        /// <summary>
        /// Converts a model using a named preset;
        /// </summary>
        public async Task<ConversionResult> ConvertModelWithPresetAsync(
            string sourcePath,
            string destinationPath,
            string presetName,
            CancellationToken cancellationToken = default)
        {
            var preset = GetPreset(presetName);
            if (preset == null)
                throw new ConversionException($"Preset '{presetName}' not found");

            preset.UsageCount++;

            return await ConvertModelAsync(
                sourcePath,
                destinationPath,
                preset.Configuration.TargetFormat,
                preset.Configuration,
                cancellationToken);
        }

        /// <summary>
        /// Converts a model from a stream (useful for web uploads)
        /// </summary>
        public async Task<ConversionResult> ConvertModelFromStreamAsync(
            Stream sourceStream,
            string originalFileName,
            string destinationPath,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            CancellationToken cancellationToken = default)
        {
            if (sourceStream == null)
                throw new ArgumentNullException(nameof(sourceStream));

            if (!sourceStream.CanRead)
                throw new ArgumentException("Source stream must be readable", nameof(sourceStream));

            // Create temporary file;
            var tempPath = Path.Combine(Path.GetTempPath(), $"conversion_{Guid.NewGuid()}_{originalFileName}");

            try
            {
                using (var fileStream = File.Create(tempPath))
                {
                    await sourceStream.CopyToAsync(fileStream, cancellationToken);
                }

                // Convert from temporary file;
                return await ConvertModelAsync(
                    tempPath,
                    destinationPath,
                    targetFormat,
                    configuration,
                    cancellationToken);
            }
            finally
            {
                // Clean up temporary file;
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to delete temporary file: {tempPath}");
                }
            }
        }

        #endregion;

        #region Batch Conversion;

        /// <summary>
        /// Converts multiple models in batch with parallel processing;
        /// </summary>
        public async Task<BatchConversionResult> ConvertBatchAsync(
            IEnumerable<string> sourcePaths,
            string outputDirectory,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            int maxParallel = 0,
            CancellationToken cancellationToken = default)
        {
            var sourceList = sourcePaths.ToList();
            if (sourceList.Count == 0)
                throw new ArgumentException("No source files provided", nameof(sourcePaths));

            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory cannot be empty", nameof(outputDirectory));

            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            maxParallel = maxParallel > 0 ? maxParallel : Environment.ProcessorCount;

            var batchId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;
            var results = new List<ConversionResult>();
            var errors = new List<BatchConversionError>();

            _logger.LogInformation($"Starting batch conversion {batchId} with {sourceList.Count} files, max parallel: {maxParallel}");

            using (var semaphore = new SemaphoreSlim(maxParallel))
            {
                var tasks = sourceList.Select(sourcePath => ProcessBatchItemAsync(
                    sourcePath,
                    outputDirectory,
                    targetFormat,
                    configuration,
                    semaphore,
                    batchId,
                    cancellationToken));

                var taskResults = await Task.WhenAll(tasks);

                foreach (var taskResult in taskResults)
                {
                    if (taskResult.Success)
                    {
                        results.Add(taskResult.Result);
                    }
                    else;
                    {
                        errors.Add(new BatchConversionError;
                        {
                            SourcePath = taskResult.SourcePath,
                            ErrorMessage = taskResult.ErrorMessage,
                            Exception = taskResult.Exception;
                        });
                    }
                }
            }

            var endTime = DateTime.UtcNow;
            var duration = endTime - startTime;

            var batchResult = new BatchConversionResult;
            {
                BatchId = batchId,
                TotalFiles = sourceList.Count,
                SuccessfulConversions = results.Count,
                FailedConversions = errors.Count,
                StartTime = startTime,
                EndTime = endTime,
                Duration = duration,
                Results = results,
                Errors = errors,
                AverageDuration = results.Any() ? results.Average(r => r.Duration.TotalSeconds) : 0,
                SuccessRate = sourceList.Count > 0 ? (double)results.Count / sourceList.Count * 100 : 0;
            };

            _logger.LogInformation($"Batch conversion {batchId} completed: {batchResult.SuccessfulConversions} successful, {batchResult.FailedConversions} failed, duration: {duration.TotalSeconds:F2}s");

            return batchResult;
        }

        private async Task<BatchItemResult> ProcessBatchItemAsync(
            string sourcePath,
            string outputDirectory,
            ModelFormat targetFormat,
            ConversionConfiguration configuration,
            SemaphoreSlim semaphore,
            string batchId,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                var fileName = Path.GetFileNameWithoutExtension(sourcePath);
                var extension = GetFileExtension(targetFormat);
                var destinationPath = Path.Combine(outputDirectory, $"{fileName}{extension}");

                var result = await ConvertModelAsync(
                    sourcePath,
                    destinationPath,
                    targetFormat,
                    configuration,
                    cancellationToken);

                return new BatchItemResult;
                {
                    Success = true,
                    SourcePath = sourcePath,
                    Result = result;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Batch item failed: {sourcePath}");

                return new BatchItemResult;
                {
                    Success = false,
                    SourcePath = sourcePath,
                    ErrorMessage = ex.Message,
                    Exception = ex;
                };
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Converts all models in a directory recursively;
        /// </summary>
        public async Task<BatchConversionResult> ConvertDirectoryAsync(
            string sourceDirectory,
            string outputDirectory,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            string searchPattern = "*.*",
            SearchOption searchOption = SearchOption.AllDirectories,
            int maxParallel = 0,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");

            var files = Directory.EnumerateFiles(sourceDirectory, searchPattern, searchOption)
                .Where(f => IsSupportedFileFormat(f))
                .ToList();

            if (files.Count == 0)
            {
                _logger.LogWarning($"No supported model files found in directory: {sourceDirectory}");
                return new BatchConversionResult;
                {
                    BatchId = Guid.NewGuid().ToString(),
                    TotalFiles = 0,
                    SuccessfulConversions = 0,
                    FailedConversions = 0,
                    SuccessRate = 100;
                };
            }

            _logger.LogInformation($"Found {files.Count} model files in directory: {sourceDirectory}");

            return await ConvertBatchAsync(
                files,
                outputDirectory,
                targetFormat,
                configuration,
                maxParallel,
                cancellationToken);
        }

        #endregion;

        #region Core Conversion Logic;

        /// <summary>
        /// Detects the format of a file based on extension and magic bytes;
        /// </summary>
        private ModelFormat DetectFileFormat(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // Check by extension first;
            var formatByExtension = GetFormatFromExtension(extension);
            if (formatByExtension != ModelFormat.Unknown)
                return formatByExtension;

            // Check magic bytes for more accurate detection;
            try
            {
                using (var stream = File.OpenRead(filePath))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length >= 4)
                    {
                        var magicBytes = reader.ReadBytes(4);

                        // Check common 3D format magic bytes;
                        if (magicBytes[0] == 0x46 && magicBytes[1] == 0x42 && magicBytes[2] == 0x58) // 'FBX'
                            return ModelFormat.FBX;

                        if (magicBytes[0] == 0x67 && magicBytes[1] == 0x6C && magicBytes[2] == 0x54 && magicBytes[3] == 0x46) // 'glTF'
                            return ModelFormat.GLTF;

                        if (magicBytes[0] == 0x50 && magicBytes[1] == 0x4B && magicBytes[2] == 0x03 && magicBytes[3] == 0x04) // ZIP (GLB)
                            return ModelFormat.GLTF;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to detect file format by magic bytes: {filePath}");
            }

            return ModelFormat.Unknown;
        }

        private ModelFormat GetFormatFromExtension(string extension)
        {
            return extension switch;
            {
                ".fbx" => ModelFormat.FBX,
                ".gltf" => ModelFormat.GLTF,
                ".glb" => ModelFormat.GLTF,
                ".obj" => ModelFormat.OBJ,
                ".stl" => ModelFormat.STL,
                ".usd" => ModelFormat.USD,
                ".usda" => ModelFormat.USD,
                ".usdc" => ModelFormat.USD,
                ".blend" => ModelFormat.Blend,
                ".ma" => ModelFormat.Maya,
                ".mb" => ModelFormat.Maya,
                ".max" => ModelFormat.Max,
                ".unity" => ModelFormat.Unity,
                ".uasset" => ModelFormat.Unreal,
                ".3ds" => ModelFormat.ThreeDS,
                ".dae" => ModelFormat.Collada,
                ".ply" => ModelFormat.PLY,
                ".x" => ModelFormat.DirectX,
                ".md5mesh" => ModelFormat.MD5,
                _ => ModelFormat.Unknown;
            };
        }

        private string GetFileExtension(ModelFormat format)
        {
            return format switch;
            {
                ModelFormat.FBX => ".fbx",
                ModelFormat.GLTF => ".gltf",
                ModelFormat.OBJ => ".obj",
                ModelFormat.STL => ".stl",
                ModelFormat.USD => ".usd",
                ModelFormat.Blend => ".blend",
                ModelFormat.Maya => ".ma",
                ModelFormat.Max => ".max",
                ModelFormat.Unity => ".unity",
                ModelFormat.Unreal => ".uasset",
                ModelFormat.ThreeDS => ".3ds",
                ModelFormat.Collada => ".dae",
                ModelFormat.PLY => ".ply",
                ModelFormat.DirectX => ".x",
                ModelFormat.MD5 => ".md5mesh",
                _ => ".bin"
            };
        }

        private bool IsSupportedFileFormat(string filePath)
        {
            var format = DetectFileFormat(filePath);
            return format != ModelFormat.Unknown && IsFormatSupported(format);
        }

        /// <summary>
        /// Loads a model from disk using the appropriate format handler;
        /// </summary>
        private async Task<ModelData> LoadModelAsync(
            string filePath,
            ModelFormat format,
            CancellationToken cancellationToken)
        {
            var handler = GetFormatHandler(format);
            if (handler == null)
                throw new ConversionException($"No handler registered for format: {format}");

            try
            {
                _logger.LogDebug($"Loading model from {filePath} as {format}");
                return await handler.LoadAsync(filePath, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new ConversionException($"Failed to load model from {filePath}", ex);
            }
        }

        /// <summary>
        /// Converts model data between formats;
        /// </summary>
        private async Task<ModelData> ConvertFormatAsync(
            ModelData sourceModel,
            ModelFormat sourceFormat,
            ModelFormat targetFormat,
            ConversionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            // If same format, just return the source;
            if (sourceFormat == targetFormat && configuration.OptimizationLevel == OptimizationLevel.None)
                return sourceModel;

            var sourceHandler = GetFormatHandler(sourceFormat);
            var targetHandler = GetFormatHandler(targetFormat);

            if (sourceHandler == null || targetHandler == null)
                throw new ConversionException($"No handler registered for source or target format");

            try
            {
                // Convert through intermediate representation if needed;
                if (sourceHandler.CanConvertDirectlyTo(targetFormat))
                {
                    return await sourceHandler.ConvertToAsync(sourceModel, targetFormat, configuration, cancellationToken);
                }
                else;
                {
                    // Convert to common intermediate format first;
                    var intermediate = await sourceHandler.ConvertToIntermediateAsync(sourceModel, configuration, cancellationToken);
                    return await targetHandler.ConvertFromIntermediateAsync(intermediate, configuration, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                throw new ConversionException($"Failed to convert from {sourceFormat} to {targetFormat}", ex);
            }
        }

        /// <summary>
        /// Saves converted model to disk;
        /// </summary>
        private async Task SaveModelAsync(
            string filePath,
            ModelData modelData,
            ModelFormat format,
            ConversionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var handler = GetFormatHandler(format);
            if (handler == null)
                throw new ConversionException($"No handler registered for format: {format}");

            try
            {
                // Ensure directory exists;
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                await handler.SaveAsync(filePath, modelData, configuration, cancellationToken);

                _logger.LogDebug($"Saved model to {filePath} as {format}");
            }
            catch (Exception ex)
            {
                throw new ConversionException($"Failed to save model to {filePath}", ex);
            }
        }

        /// <summary>
        /// Applies conversion configuration to model;
        /// </summary>
        private async Task ApplyConfigurationAsync(
            ModelData model,
            ConversionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (configuration == null)
                return;

            // Apply scale;
            if (Math.Abs(configuration.Scale - 1.0f) > 0.001f)
            {
                await ApplyScaleAsync(model, configuration.Scale, cancellationToken);
            }

            // Apply rotation;
            if (configuration.Rotation != Quaternion.Identity)
            {
                await ApplyRotationAsync(model, configuration.Rotation, cancellationToken);
            }

            // Apply translation;
            if (configuration.Translation != Vector3.Zero)
            {
                await ApplyTranslationAsync(model, configuration.Translation, cancellationToken);
            }

            // Center pivot if requested;
            if (configuration.CenterPivot)
            {
                await CenterPivotAsync(model, cancellationToken);
            }

            // Freeze transformations if requested;
            if (configuration.FreezeTransformations)
            {
                await FreezeTransformationsAsync(model, cancellationToken);
            }

            await Task.CompletedTask;
        }

        private async Task ApplyScaleAsync(ModelData model, float scale, CancellationToken cancellationToken)
        {
            // Scale vertices;
            foreach (var mesh in model.Meshes)
            {
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    mesh.Vertices[i] *= scale;
                }
            }

            // Scale animation data if present;
            if (model.Animations != null)
            {
                foreach (var animation in model.Animations)
                {
                    foreach (var track in animation.Tracks)
                    {
                        if (track.ScaleKeys != null)
                        {
                            for (int i = 0; i < track.ScaleKeys.Length; i++)
                            {
                                track.ScaleKeys[i].Value *= scale;
                            }
                        }
                    }
                }
            }

            await Task.CompletedTask;
        }

        private async Task ApplyRotationAsync(ModelData model, Quaternion rotation, CancellationToken cancellationToken)
        {
            // Rotate vertices and normals;
            foreach (var mesh in model.Meshes)
            {
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    mesh.Vertices[i] = Vector3.Transform(mesh.Vertices[i], rotation);
                }

                if (mesh.Normals != null)
                {
                    for (int i = 0; i < mesh.Normals.Length; i++)
                    {
                        mesh.Normals[i] = Vector3.Transform(mesh.Normals[i], rotation);
                    }
                }
            }

            await Task.CompletedTask;
        }

        private async Task ApplyTranslationAsync(ModelData model, Vector3 translation, CancellationToken cancellationToken)
        {
            // Translate vertices;
            foreach (var mesh in model.Meshes)
            {
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    mesh.Vertices[i] += translation;
                }
            }

            await Task.CompletedTask;
        }

        private async Task CenterPivotAsync(ModelData model, CancellationToken cancellationToken)
        {
            if (model.Meshes.Count == 0)
                return;

            // Calculate bounding box center;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            foreach (var mesh in model.Meshes)
            {
                foreach (var vertex in mesh.Vertices)
                {
                    min = Vector3.Min(min, vertex);
                    max = Vector3.Max(max, vertex);
                }
            }

            var center = (min + max) / 2;

            // Translate to center;
            await ApplyTranslationAsync(model, -center, cancellationToken);
        }

        private async Task FreezeTransformationsAsync(ModelData model, CancellationToken cancellationToken)
        {
            // Apply transformations to vertices and reset transformation matrices;
            // This is a simplified implementation;
            foreach (var mesh in model.Meshes)
            {
                if (mesh.TransformMatrix != Matrix4x4.Identity)
                {
                    // Apply transformation to vertices;
                    for (int i = 0; i < mesh.Vertices.Length; i++)
                    {
                        mesh.Vertices[i] = Vector3.Transform(mesh.Vertices[i], mesh.TransformMatrix);
                    }

                    // Apply to normals;
                    if (mesh.Normals != null)
                    {
                        var normalMatrix = Matrix4x4.Transpose(Matrix4x4.Invert(mesh.TransformMatrix));
                        for (int i = 0; i < mesh.Normals.Length; i++)
                        {
                            mesh.Normals[i] = Vector3.TransformNormal(mesh.Normals[i], normalMatrix);
                            mesh.Normals[i] = Vector3.Normalize(mesh.Normals[i]);
                        }
                    }

                    // Reset transformation matrix;
                    mesh.TransformMatrix = Matrix4x4.Identity;
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Optimizes model based on configuration;
        /// </summary>
        private async Task<ModelData> OptimizeModelAsync(
            ModelData model,
            ConversionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (_optimizationEngine == null)
                return model;

            try
            {
                var optimizationOptions = new OptimizationOptions;
                {
                    Level = configuration.OptimizationLevel,
                    TargetPolyCount = configuration.MaxPolyCount,
                    MergeMeshes = configuration.MergeMeshes,
                    RemoveUnusedVertices = configuration.RemoveUnusedVertices,
                    RemoveDuplicateVertices = true,
                    GenerateNormals = !configuration.PreserveNormals && model.Meshes.Any(m => m.Normals == null),
                    GenerateUVs = !configuration.PreserveUVs && model.Meshes.Any(m => m.UVs == null),
                    GenerateTangents = configuration.GenerateTangents,
                    OptimizeVertexCache = true,
                    OptimizeOverdraw = true,
                    QuantizePositions = configuration.QuantizeAttributes,
                    QuantizeNormals = configuration.QuantizeAttributes,
                    QuantizeUVs = configuration.QuantizeAttributes;
                };

                return await _optimizationEngine.OptimizeAsync(model, optimizationOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Model optimization failed, continuing without optimization");
                return model;
            }
        }

        /// <summary>
        /// Processes and converts textures;
        /// </summary>
        private async Task ProcessTexturesAsync(
            ModelData model,
            ConversionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (_textureConverter == null || model.Textures == null)
                return;

            foreach (var texture in model.Textures)
            {
                try
                {
                    var textureOptions = new TextureConversionOptions;
                    {
                        MaxSize = configuration.MaxTextureSize,
                        Format = configuration.TextureFormat,
                        Compression = configuration.CompressTextures ? TextureCompression.DXT5 : TextureCompression.None,
                        GenerateMipmaps = configuration.GenerateMipmaps,
                        Quality = configuration.TextureQuality;
                    };

                    await _textureConverter.ConvertAsync(texture, textureOptions, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to process texture: {texture.Name}");
                }
            }
        }

        /// <summary>
        /// Processes animations;
        /// </summary>
        private async Task ProcessAnimationsAsync(
            ModelData model,
            ConversionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (_animationConverter == null || model.Animations == null)
                return;

            foreach (var animation in model.Animations)
            {
                try
                {
                    var animationOptions = new AnimationConversionOptions;
                    {
                        SampleRate = configuration.AnimationSampleRate,
                        RemoveRedundantKeys = configuration.OptimizeAnimations,
                        Compress = configuration.CompressAnimations,
                        MaxBoneCount = configuration.MaxBoneCount;
                    };

                    await _animationConverter.ConvertAsync(animation, animationOptions, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to process animation: {animation.Name}");
                }
            }
        }

        /// <summary>
        /// Generates LODs for the model;
        /// </summary>
        private async Task GenerateLODsAsync(
            ModelData model,
            ConversionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            if (_optimizationEngine == null || configuration.LODCount <= 1)
                return;

            try
            {
                var lodOptions = new LODGenerationOptions;
                {
                    LODCount = configuration.LODCount,
                    ReductionFactors = Enumerable.Range(1, configuration.LODCount - 1)
                        .Select(i => 1.0f - (i * 0.3f)) // 70%, 40%, 10% reduction for LODs;
                        .ToArray(),
                    PreserveUVs = configuration.PreserveUVs,
                    PreserveNormals = configuration.PreserveNormals,
                    PreserveBorders = true,
                    UseQuadricSimplification = true;
                };

                model.LODs = await _optimizationEngine.GenerateLODsAsync(model, lodOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LOD generation failed, continuing without LODs");
            }
        }

        #endregion;

        #region Validation and Statistics;

        private async Task<ValidationResult> ValidateSourceFileAsync(
            string filePath,
            ModelFormat format,
            CancellationToken cancellationToken)
        {
            return await _modelValidator.ValidateFileAsync(filePath, format, cancellationToken);
        }

        private async Task<ValidationResult> ValidateOutputFileAsync(
            string filePath,
            ModelFormat format,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath))
                return new ValidationResult { IsValid = false, Errors = new[] { "Output file was not created" } };

            return await _modelValidator.ValidateFileAsync(filePath, format, cancellationToken);
        }

        private async Task<ModelStatistics> GetModelStatisticsAsync(
            string filePath,
            ModelFormat format,
            CancellationToken cancellationToken)
        {
            try
            {
                var model = await LoadModelAsync(filePath, format, cancellationToken);
                return model.Statistics;
            }
            catch
            {
                return new ModelStatistics();
            }
        }

        private double CalculateCompressionRatio(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath) || !File.Exists(destinationPath))
                return 1.0;

            var sourceSize = new FileInfo(sourcePath).Length;
            var destSize = new FileInfo(destinationPath).Length;

            if (sourceSize == 0)
                return 1.0;

            return (double)destSize / sourceSize;
        }

        private void UpdateStatistics(ConversionResult result, bool success)
        {
            lock (_conversionLock)
            {
                Statistics.TotalConversions++;

                if (success)
                {
                    Statistics.SuccessfulConversions++;
                    Statistics.TotalConversionTime += result.Duration.TotalMilliseconds;

                    if (result.SourceStats != null)
                    {
                        Statistics.TotalVerticesProcessed += result.SourceStats.VertexCount;
                        Statistics.TotalTrianglesProcessed += result.SourceStats.TriangleCount;
                        Statistics.TotalMeshesProcessed += result.SourceStats.MeshCount;
                    }
                }
                else;
                {
                    Statistics.FailedConversions++;
                }

                Statistics.AverageConversionTime = Statistics.SuccessfulConversions > 0;
                    ? Statistics.TotalConversionTime / Statistics.SuccessfulConversions;
                    : 0;

                Statistics.LastConversionTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Gets detailed converter statistics;
        /// </summary>
        public ConverterStatistics GetDetailedStatistics()
        {
            lock (_conversionLock)
            {
                var stats = Statistics.Clone();
                stats.ActiveConversions = ActiveConversions;
                stats.Status = Status;
                stats.SupportedFormats = SupportedFormats;
                stats.RegisteredPresets = _conversionPresets.Count;

                return stats;
            }
        }

        #endregion;

        #region Utility Methods;

        private void ValidateConversionParameters(string sourcePath, string destinationPath, ModelFormat targetFormat)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be empty", nameof(sourcePath));

            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("Destination path cannot be empty", nameof(destinationPath));

            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Source file not found: {sourcePath}");

            if (targetFormat == ModelFormat.Unknown)
                throw new ArgumentException("Target format cannot be unknown", nameof(targetFormat));

            var sourceExtension = Path.GetExtension(sourcePath);
            var destExtension = Path.GetExtension(destinationPath);

            if (string.IsNullOrEmpty(destExtension))
                throw new ArgumentException("Destination path must have a file extension", nameof(destinationPath));
        }

        /// <summary>
        /// Gets information about a model file without loading it completely;
        /// </summary>
        public async Task<ModelInfo> GetModelInfoAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            var format = DetectFileFormat(filePath);
            if (format == ModelFormat.Unknown)
                throw new ConversionException($"Unable to detect format of file: {filePath}");

            var handler = GetFormatHandler(format);
            if (handler == null)
                throw new ConversionException($"No handler registered for format: {format}");

            try
            {
                return await handler.GetInfoAsync(filePath, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new ConversionException($"Failed to get model info for: {filePath}", ex);
            }
        }

        /// <summary>
        /// Estimates conversion time and size for a model;
        /// </summary>
        public async Task<ConversionEstimate> EstimateConversionAsync(
            string sourcePath,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            CancellationToken cancellationToken = default)
        {
            var info = await GetModelInfoAsync(sourcePath, cancellationToken);

            configuration ??= _defaultConfiguration;
            configuration.TargetFormat = targetFormat;

            var estimate = new ConversionEstimate;
            {
                SourceInfo = info,
                TargetFormat = targetFormat,
                EstimatedTime = TimeSpan.FromSeconds(info.Statistics.TriangleCount / 10000.0), // Rough estimate;
                EstimatedSize = CalculateEstimatedSize(info.Statistics, configuration),
                Complexity = CalculateConversionComplexity(info.Statistics, configuration),
                Recommendations = GenerateRecommendations(info.Statistics, configuration)
            };

            return estimate;
        }

        private long CalculateEstimatedSize(ModelStatistics stats, ConversionConfiguration config)
        {
            // Rough size estimation formula;
            var baseSize = stats.VertexCount * 32L + stats.TriangleCount * 12L; // Basic geometry

            if (config.CompressGeometry)
                baseSize = (long)(baseSize * 0.5); // 50% compression;

            if (config.GenerateLODs)
                baseSize += (long)(baseSize * 0.3 * (config.LODCount - 1)); // Additional LODs;

            return baseSize;
        }

        private ConversionComplexity CalculateConversionComplexity(ModelStatistics stats, ConversionConfiguration config)
        {
            var complexity = ConversionComplexity.Low;

            if (stats.TriangleCount > 1000000)
                complexity = ConversionComplexity.VeryHigh;
            else if (stats.TriangleCount > 100000)
                complexity = ConversionComplexity.High;
            else if (stats.TriangleCount > 10000)
                complexity = ConversionComplexity.Medium;

            if (config.OptimizationLevel >= OptimizationLevel.High)
                complexity = (ConversionComplexity)Math.Min((int)complexity + 1, (int)ConversionComplexity.VeryHigh);

            if (config.GenerateLODs && config.LODCount > 3)
                complexity = (ConversionComplexity)Math.Min((int)complexity + 1, (int)ConversionComplexity.VeryHigh);

            return complexity;
        }

        private List<string> GenerateRecommendations(ModelStatistics stats, ConversionConfiguration config)
        {
            var recommendations = new List<string>();

            if (stats.TriangleCount > 100000 && config.OptimizationLevel == OptimizationLevel.None)
                recommendations.Add("Consider enabling optimization for high-poly models");

            if (stats.TextureCount > 10 && config.MaxTextureSize > 2048)
                recommendations.Add("Consider reducing texture size for better performance");

            if (stats.AnimationCount > 5 && !config.CompressAnimations)
                recommendations.Add("Consider enabling animation compression");

            if (stats.VertexCount > 65535 && !config.ProcessAnimations)
                recommendations.Add("Model exceeds 16-bit index limit, consider splitting or optimization");

            return recommendations;
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnConversionStarted(ConversionStartedEventArgs e)
        {
            ConversionStarted?.Invoke(this, e);
        }

        protected virtual void OnConversionProgress(ConversionProgressEventArgs e)
        {
            ConversionProgress?.Invoke(this, e);
        }

        protected virtual void OnConversionCompleted(ConversionCompletedEventArgs e)
        {
            ConversionCompleted?.Invoke(this, e);
        }

        protected virtual void OnConversionFailed(ConversionFailedEventArgs e)
        {
            ConversionFailed?.Invoke(this, e);
        }

        protected virtual void OnFormatRegistered(FormatRegisteredEventArgs e)
        {
            FormatRegistered?.Invoke(this, e);
        }

        protected virtual void OnPresetCreated(PresetCreatedEventArgs e)
        {
            PresetCreated?.Invoke(this, e);
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
                    _conversionSemaphore?.Dispose();

                    // Dispose format handlers;
                    foreach (var handler in _formatHandlers.Values.OfType<IDisposable>())
                    {
                        handler.Dispose();
                    }
                }

                _isDisposed = true;
            }
        }

        ~ModelConverter()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IModelConverter;
    {
        // Format Handler Management;
        void RegisterFormatHandler(ModelFormat format, IFormatHandler handler);
        bool UnregisterFormatHandler(ModelFormat format);
        IFormatHandler GetFormatHandler(ModelFormat format);
        bool IsFormatSupported(ModelFormat format);
        IEnumerable<ModelFormat> GetSupportedInputFormats();
        IEnumerable<ModelFormat> GetSupportedOutputFormats(ModelFormat inputFormat);

        // Preset Management;
        ConversionPreset CreatePreset(string name, ConversionConfiguration configuration);
        ConversionPreset GetPreset(string name);
        bool UpdatePreset(string name, ConversionConfiguration configuration);
        bool DeletePreset(string name);
        IEnumerable<ConversionPreset> ListPresets();
        IEnumerable<ConversionPreset> GetPresetsForFormat(ModelFormat targetFormat);

        // Single Model Conversion;
        Task<ConversionResult> ConvertModelAsync(
            string sourcePath,
            string destinationPath,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            CancellationToken cancellationToken = default);

        Task<ConversionResult> ConvertModelWithPresetAsync(
            string sourcePath,
            string destinationPath,
            string presetName,
            CancellationToken cancellationToken = default);

        Task<ConversionResult> ConvertModelFromStreamAsync(
            Stream sourceStream,
            string originalFileName,
            string destinationPath,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            CancellationToken cancellationToken = default);

        // Batch Conversion;
        Task<BatchConversionResult> ConvertBatchAsync(
            IEnumerable<string> sourcePaths,
            string outputDirectory,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            int maxParallel = 0,
            CancellationToken cancellationToken = default);

        Task<BatchConversionResult> ConvertDirectoryAsync(
            string sourceDirectory,
            string outputDirectory,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            string searchPattern = "*.*",
            SearchOption searchOption = SearchOption.AllDirectories,
            int maxParallel = 0,
            CancellationToken cancellationToken = default);

        // Utility Methods;
        Task<ModelInfo> GetModelInfoAsync(string filePath, CancellationToken cancellationToken = default);
        Task<ConversionEstimate> EstimateConversionAsync(
            string sourcePath,
            ModelFormat targetFormat,
            ConversionConfiguration configuration = null,
            CancellationToken cancellationToken = default);

        ConverterStatistics GetDetailedStatistics();

        // Properties;
        ConverterStatus Status { get; }
        ConverterStatistics Statistics { get; }
        int ActiveConversions { get; }
        int SupportedFormats { get; }

        // Events;
        event EventHandler<ConversionStartedEventArgs> ConversionStarted;
        event EventHandler<ConversionProgressEventArgs> ConversionProgress;
        event EventHandler<ConversionCompletedEventArgs> ConversionCompleted;
        event EventHandler<ConversionFailedEventArgs> ConversionFailed;
        event EventHandler<FormatRegisteredEventArgs> FormatRegistered;
        event EventHandler<PresetCreatedEventArgs> PresetCreated;
    }

    public interface IFormatHandler;
    {
        Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken);
        Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken);
        Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken);

        bool CanConvertDirectlyTo(ModelFormat targetFormat);
        Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken);
        Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken);
        Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken);

        IEnumerable<ModelFormat> GetSupportedOutputFormats();
    }

    public interface IModelValidator;
    {
        Task<ValidationResult> ValidateFileAsync(string filePath, ModelFormat format, CancellationToken cancellationToken);
        Task<ValidationResult> ValidateModelAsync(ModelData model, CancellationToken cancellationToken);
    }

    public interface IOptimizationEngine;
    {
        Task<ModelData> OptimizeAsync(ModelData model, OptimizationOptions options, CancellationToken cancellationToken);
        Task<List<LODModel>> GenerateLODsAsync(ModelData model, LODGenerationOptions options, CancellationToken cancellationToken);
        Task<ModelData> SimplifyAsync(ModelData model, float reductionFactor, CancellationToken cancellationToken);
        Task<ModelData> RepairAsync(ModelData model, RepairOptions options, CancellationToken cancellationToken);
    }

    public interface ITextureConverter;
    {
        Task ConvertAsync(TextureData texture, TextureConversionOptions options, CancellationToken cancellationToken);
        Task ResizeAsync(TextureData texture, int maxSize, CancellationToken cancellationToken);
        Task CompressAsync(TextureData texture, TextureCompression compression, CancellationToken cancellationToken);
    }

    public interface IAnimationConverter;
    {
        Task ConvertAsync(AnimationData animation, AnimationConversionOptions options, CancellationToken cancellationToken);
        Task ResampleAsync(AnimationData animation, float sampleRate, CancellationToken cancellationToken);
        Task CompressAsync(AnimationData animation, CompressionOptions options, CancellationToken cancellationToken);
    }

    public enum ConverterStatus;
    {
        Ready,
        Processing,
        Paused,
        Error;
    }

    public enum ModelFormat;
    {
        Unknown,
        FBX,
        GLTF,
        OBJ,
        STL,
        USD,
        Blend,
        Maya,
        Max,
        Unity,
        Unreal,
        ThreeDS,
        Collada,
        PLY,
        DirectX,
        MD5,
        Custom;
    }

    public enum OptimizationLevel;
    {
        None,
        Low,
        Medium,
        High,
        Ultra;
    }

    public enum ConversionComplexity;
    {
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh;
    }

    public enum TextureFormat;
    {
        PNG,
        JPEG,
        DDS,
        TGA,
        BMP,
        HDR,
        EXR,
        KTX;
    }

    public enum TextureCompression;
    {
        None,
        DXT1,
        DXT3,
        DXT5,
        BC1,
        BC3,
        BC5,
        BC7,
        ETC1,
        ETC2,
        ASTC;
    }

    public class ConversionConfiguration;
    {
        public static ConversionConfiguration Default => new ConversionConfiguration();

        public ModelFormat TargetFormat { get; set; } = ModelFormat.GLTF;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public int MaxTextureSize { get; set; } = 2048;
        public bool GenerateLODs { get; set; } = false;
        public int LODCount { get; set; } = 3;
        public bool MergeMeshes { get; set; } = false;
        public bool RemoveUnusedVertices { get; set; } = true;
        public bool PreserveNormals { get; set; } = true;
        public bool PreserveUVs { get; set; } = true;
        public bool PreserveVertexColors { get; set; } = false;
        public bool PreserveCustomAttributes { get; set; } = false;
        public bool GenerateTangents { get; set; } = false;
        public bool GenerateLightmapUVs { get; set; } = false;
        public bool GenerateCollision { get; set; } = false;
        public bool GenerateNavMesh { get; set; } = false;
        public bool ExportMaterials { get; set; } = true;
        public bool ExportSkeleton { get; set; } = true;
        public bool ExportAnimations { get; set; } = true;
        public bool ExportBlendShapes { get; set; } = true;
        public bool ProcessTextures { get; set; } = true;
        public bool ProcessAnimations { get; set; } = true;
        public bool CompressTextures { get; set; } = false;
        public bool CompressGeometry { get; set; } = false;
        public bool CompressAnimations { get; set; } = false;
        public bool DracoCompression { get; set; } = false;
        public bool QuantizeAttributes { get; set; } = false;
        public bool OptimizeAnimations { get; set; } = true;
        public TextureFormat TextureFormat { get; set; } = TextureFormat.PNG;
        public TextureCompression TextureCompression { get; set; } = TextureCompression.None;
        public float TextureQuality { get; set; } = 0.9f;
        public bool GenerateMipmaps { get; set; } = true;
        public int MaxPolyCount { get; set; } = 0; // 0 = no limit;
        public int MaxBoneCount { get; set; } = 0; // 0 = no limit;
        public float AnimationSampleRate { get; set; } = 30.0f;
        public float Scale { get; set; } = 1.0f;
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Translation { get; set; } = Vector3.Zero;
        public bool CenterPivot { get; set; } = false;
        public bool FreezeTransformations { get; set; } = false;
        public bool RepairGeometry { get; set; } = false;
        public bool CloseHoles { get; set; } = false;
        public bool EnsureManifold { get; set; } = false;
        public float UnitScale { get; set; } = 1.0f;
        public bool ExportBinary { get; set; } = true;
    }

    public class ConversionPreset;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ConversionConfiguration Configuration { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int UsageCount { get; set; }
        public string Description { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
    }

    public class ModelData;
    {
        public string Name { get; set; }
        public List<MeshData> Meshes { get; set; } = new List<MeshData>();
        public List<MaterialData> Materials { get; set; } = new List<MaterialData>();
        public List<TextureData> Textures { get; set; } = new List<TextureData>();
        public List<AnimationData> Animations { get; set; } = new List<AnimationData>();
        public SkeletonData Skeleton { get; set; }
        public List<LODModel> LODs { get; set; } = new List<LODModel>();
        public ModelStatistics Statistics { get; set; } = new ModelStatistics();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public bool HasTextures => Textures != null && Textures.Count > 0;
        public bool HasAnimations => Animations != null && Animations.Count > 0;
        public bool HasSkeleton => Skeleton != null && Skeleton.Bones.Count > 0;
    }

    public class MeshData;
    {
        public string Name { get; set; }
        public Vector3[] Vertices { get; set; }
        public Vector3[] Normals { get; set; }
        public Vector2[] UVs { get; set; }
        public Vector4[] Tangents { get; set; }
        public Color[] Colors { get; set; }
        public int[] Indices { get; set; }
        public int MaterialIndex { get; set; }
        public Matrix4x4 TransformMatrix { get; set; } = Matrix4x4.Identity;
        public BoundingBox Bounds { get; set; }
        public Dictionary<string, object> CustomAttributes { get; set; } = new Dictionary<string, object>();
    }

    public class MaterialData;
    {
        public string Name { get; set; }
        public Vector3 DiffuseColor { get; set; } = Vector3.One;
        public Vector3 SpecularColor { get; set; } = Vector3.Zero;
        public float Shininess { get; set; } = 0.0f;
        public float Opacity { get; set; } = 1.0f;
        public string DiffuseTexture { get; set; }
        public string NormalTexture { get; set; }
        public string SpecularTexture { get; set; }
        public string EmissionTexture { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    public class TextureData;
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Channels { get; set; }
        public byte[] Data { get; set; }
        public TextureFormat Format { get; set; }
        public bool IsCompressed { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class AnimationData;
    {
        public string Name { get; set; }
        public float Duration { get; set; }
        public float FPS { get; set; } = 30.0f;
        public List<AnimationTrack> Tracks { get; set; } = new List<AnimationTrack>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class AnimationTrack;
    {
        public string BoneName { get; set; }
        public AnimationKeyframe<Vector3>[] PositionKeys { get; set; }
        public AnimationKeyframe<Quaternion>[] RotationKeys { get; set; }
        public AnimationKeyframe<Vector3>[] ScaleKeys { get; set; }
    }

    public class AnimationKeyframe<T>
    {
        public float Time { get; set; }
        public T Value { get; set; }
        public T InTangent { get; set; }
        public T OutTangent { get; set; }
    }

    public class SkeletonData;
    {
        public List<BoneData> Bones { get; set; } = new List<BoneData>();
        public List<int[]> Hierarchy { get; set; } = new List<int[]>();
        public Dictionary<string, Matrix4x4> InverseBindPoses { get; set; } = new Dictionary<string, Matrix4x4>();
    }

    public class BoneData;
    {
        public string Name { get; set; }
        public int ParentIndex { get; set; } = -1;
        public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 OffsetMatrix { get; set; } = Matrix4x4.Identity;
    }

    public class LODModel;
    {
        public float ScreenCoverage { get; set; }
        public ModelData Model { get; set; }
        public float ReductionFactor { get; set; }
    }

    public class IntermediateModel;
    {
        public List<IntermediateMesh> Meshes { get; set; } = new List<IntermediateMesh>();
        public List<IntermediateMaterial> Materials { get; set; } = new List<IntermediateMaterial>();
        public List<IntermediateTexture> Textures { get; set; } = new List<IntermediateTexture>();
        public IntermediateSkeleton Skeleton { get; set; }
        public List<IntermediateAnimation> Animations { get; set; } = new List<IntermediateAnimation>();
    }

    public class ModelInfo;
    {
        public string FilePath { get; set; }
        public ModelFormat Format { get; set; }
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }
        public ModelStatistics Statistics { get; set; }
        public bool IsValid { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
        public List<string> ValidationWarnings { get; set; } = new List<string>();
    }

    public class ModelStatistics;
    {
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public int MeshCount { get; set; }
        public int MaterialCount { get; set; }
        public int TextureCount { get; set; }
        public int AnimationCount { get; set; }
        public int BoneCount { get; set; }
        public int LODCount { get; set; }
        public BoundingBox BoundingBox { get; set; }
        public float AverageTriangleArea { get; set; }
        public float AverageEdgeLength { get; set; }
        public bool HasNormals { get; set; }
        public bool HasUVs { get; set; }
        public bool HasTangents { get; set; }
        public bool HasVertexColors { get; set; }
        public bool IsManifold { get; set; }
        public bool HasHoles { get; set; }
        public int NonManifoldEdges { get; set; }
        public int DuplicateVertices { get; set; }
        public int DegenerateTriangles { get; set; }
    }

    public class BoundingBox;
    {
        public Vector3 Min { get; set; }
        public Vector3 Max { get; set; }
        public Vector3 Center => (Min + Max) / 2;
        public Vector3 Size => Max - Min;
        public float Volume => Size.X * Size.Y * Size.Z;
    }

    public class ConversionResult;
    {
        public string ConversionId { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public ModelFormat SourceFormat { get; set; }
        public ModelFormat TargetFormat { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public ModelStatistics SourceStats { get; set; }
        public ModelStatistics OutputStats { get; set; }
        public ConversionMetadata Metadata { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public ConversionConfiguration Configuration { get; set; }
    }

    public class ConversionMetadata;
    {
        public long FileSize { get; set; }
        public double CompressionRatio { get; set; }
        public bool OptimizationApplied { get; set; }
        public bool LODsGenerated { get; set; }
        public bool TexturesProcessed { get; set; }
        public bool AnimationsProcessed { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public class BatchConversionResult;
    {
        public string BatchId { get; set; }
        public int TotalFiles { get; set; }
        public int SuccessfulConversions { get; set; }
        public int FailedConversions { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public List<ConversionResult> Results { get; set; } = new List<ConversionResult>();
        public List<BatchConversionError> Errors { get; set; } = new List<BatchConversionError>();
        public double AverageDuration { get; set; }
        public double SuccessRate { get; set; }
    }

    public class BatchConversionError;
    {
        public string SourcePath { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
    }

    public class BatchItemResult;
    {
        public bool Success { get; set; }
        public string SourcePath { get; set; }
        public ConversionResult Result { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
    }

    public class ConversionEstimate;
    {
        public ModelInfo SourceInfo { get; set; }
        public ModelFormat TargetFormat { get; set; }
        public TimeSpan EstimatedTime { get; set; }
        public long EstimatedSize { get; set; }
        public ConversionComplexity Complexity { get; set; }
        public List<string> Recommendations { get; set; } = new List<string>();
        public Dictionary<string, object> AdditionalInfo { get; set; } = new Dictionary<string, object>();
    }

    public class ConverterStatistics;
    {
        public int TotalConversions { get; set; }
        public int SuccessfulConversions { get; set; }
        public int FailedConversions { get; set; }
        public double TotalConversionTime { get; set; } // milliseconds;
        public double AverageConversionTime { get; set; } // milliseconds;
        public long TotalVerticesProcessed { get; set; }
        public long TotalTrianglesProcessed { get; set; }
        public long TotalMeshesProcessed { get; set; }
        public DateTime LastConversionTime { get; set; }
        public int ActiveConversions { get; set; }
        public ConverterStatus Status { get; set; }
        public int SupportedFormats { get; set; }
        public int RegisteredPresets { get; set; }

        public ConverterStatistics Clone()
        {
            return (ConverterStatistics)MemberwiseClone();
        }
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    public class OptimizationOptions;
    {
        public OptimizationLevel Level { get; set; }
        public int TargetPolyCount { get; set; }
        public bool MergeMeshes { get; set; }
        public bool RemoveUnusedVertices { get; set; }
        public bool RemoveDuplicateVertices { get; set; }
        public bool GenerateNormals { get; set; }
        public bool GenerateUVs { get; set; }
        public bool GenerateTangents { get; set; }
        public bool OptimizeVertexCache { get; set; }
        public bool OptimizeOverdraw { get; set; }
        public bool QuantizePositions { get; set; }
        public bool QuantizeNormals { get; set; }
        public bool QuantizeUVs { get; set; }
        public bool QuantizeColors { get; set; }
    }

    public class LODGenerationOptions;
    {
        public int LODCount { get; set; }
        public float[] ReductionFactors { get; set; }
        public bool PreserveUVs { get; set; }
        public bool PreserveNormals { get; set; }
        public bool PreserveBorders { get; set; }
        public bool UseQuadricSimplification { get; set; }
        public float Aggressiveness { get; set; } = 7.0f;
    }

    public class TextureConversionOptions;
    {
        public int MaxSize { get; set; }
        public TextureFormat Format { get; set; }
        public TextureCompression Compression { get; set; }
        public bool GenerateMipmaps { get; set; }
        public float Quality { get; set; }
        public bool FlipVertical { get; set; }
        public bool PremultiplyAlpha { get; set; }
    }

    public class AnimationConversionOptions;
    {
        public float SampleRate { get; set; }
        public bool RemoveRedundantKeys { get; set; }
        public bool Compress { get; set; }
        public int MaxBoneCount { get; set; }
        public float PositionError { get; set; } = 0.1f;
        public float RotationError { get; set; } = 0.01f;
        public float ScaleError { get; set; } = 0.01f;
    }

    public class RepairOptions;
    {
        public bool CloseHoles { get; set; }
        public bool RemoveDegenerateTriangles { get; set; }
        public bool RemoveDuplicateVertices { get; set; }
        public bool FixNormals { get; set; }
        public bool MakeManifold { get; set; }
        public float WeldDistance { get; set; } = 0.001f;
    }

    public class CompressionOptions;
    {
        public float PositionTolerance { get; set; } = 0.01f;
        public float RotationTolerance { get; set; } = 0.001f;
        public float ScaleTolerance { get; set; } = 0.001f;
        public bool RemoveConstantTracks { get; set; } = true;
        public bool Quantize { get; set; } = true;
    }

    // Default implementations;
    public class DefaultModelValidator : IModelValidator;
    {
        public Task<ValidationResult> ValidateFileAsync(string filePath, ModelFormat format, CancellationToken cancellationToken)
        {
            var result = new ValidationResult { IsValid = true };

            if (!File.Exists(filePath))
            {
                result.IsValid = false;
                result.Errors.Add("File does not exist");
                return Task.FromResult(result);
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
            {
                result.IsValid = false;
                result.Errors.Add("File is empty");
            }

            if (fileInfo.Length > 1024 * 1024 * 1024) // 1GB;
            {
                result.Warnings.Add("File size exceeds 1GB, may cause memory issues");
            }

            return Task.FromResult(result);
        }

        public Task<ValidationResult> ValidateModelAsync(ModelData model, CancellationToken cancellationToken)
        {
            var result = new ValidationResult { IsValid = true };

            if (model == null)
            {
                result.IsValid = false;
                result.Errors.Add("Model is null");
                return Task.FromResult(result);
            }

            if (model.Meshes.Count == 0)
            {
                result.Errors.Add("Model has no meshes");
                result.IsValid = false;
            }

            foreach (var mesh in model.Meshes)
            {
                if (mesh.Vertices == null || mesh.Vertices.Length == 0)
                {
                    result.Errors.Add($"Mesh '{mesh.Name}' has no vertices");
                    result.IsValid = false;
                }

                if (mesh.Indices == null || mesh.Indices.Length == 0)
                {
                    result.Errors.Add($"Mesh '{mesh.Name}' has no indices");
                    result.IsValid = false;
                }
            }

            return Task.FromResult(result);
        }
    }

    public class DefaultOptimizationEngine : IOptimizationEngine;
    {
        public Task<ModelData> OptimizeAsync(ModelData model, OptimizationOptions options, CancellationToken cancellationToken)
        {
            // Simplified optimization - real implementation would use mesh optimization algorithms;
            return Task.FromResult(model);
        }

        public Task<List<LODModel>> GenerateLODsAsync(ModelData model, LODGenerationOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<LODModel>());
        }

        public Task<ModelData> SimplifyAsync(ModelData model, float reductionFactor, CancellationToken cancellationToken)
        {
            return Task.FromResult(model);
        }

        public Task<ModelData> RepairAsync(ModelData model, RepairOptions options, CancellationToken cancellationToken)
        {
            return Task.FromResult(model);
        }
    }

    public class DefaultTextureConverter : ITextureConverter;
    {
        public Task ConvertAsync(TextureData texture, TextureConversionOptions options, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ResizeAsync(TextureData texture, int maxSize, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task CompressAsync(TextureData texture, TextureCompression compression, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    public class DefaultAnimationConverter : IAnimationConverter;
    {
        public Task ConvertAsync(AnimationData animation, AnimationConversionOptions options, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ResampleAsync(AnimationData animation, float sampleRate, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task CompressAsync(AnimationData animation, CompressionOptions options, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    // Built-in format handlers (simplified implementations)
    public class FBXFormatHandler : IFormatHandler, IDisposable;
    {
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModelData { Name = Path.GetFileNameWithoutExtension(filePath) });
        }

        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModelInfo { FilePath = filePath, Format = ModelFormat.FBX });
        }

        public bool CanConvertDirectlyTo(ModelFormat targetFormat)
        {
            return targetFormat == ModelFormat.FBX || targetFormat == ModelFormat.OBJ;
        }

        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.FromResult(sourceModel);
        }

        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.FromResult(new IntermediateModel());
        }

        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModelData());
        }

        public IEnumerable<ModelFormat> GetSupportedOutputFormats()
        {
            return new[] { ModelFormat.FBX, ModelFormat.OBJ, ModelFormat.GLTF };
        }

        public void Dispose() { }
    }

    public class GLTFFormatHandler : IFormatHandler, IDisposable;
    {
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModelData { Name = Path.GetFileNameWithoutExtension(filePath) });
        }

        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModelInfo { FilePath = filePath, Format = ModelFormat.GLTF });
        }

        public bool CanConvertDirectlyTo(ModelFormat targetFormat)
        {
            return targetFormat == ModelFormat.GLTF || targetFormat == ModelFormat.FBX;
        }

        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.FromResult(sourceModel);
        }

        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.FromResult(new IntermediateModel());
        }

        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ModelData());
        }

        public IEnumerable<ModelFormat> GetSupportedOutputFormats()
        {
            return new[] { ModelFormat.GLTF, ModelFormat.FBX, ModelFormat.OBJ, ModelFormat.USD };
        }

        public void Dispose() { }
    }

    // Other format handlers would be implemented similarly;
    public class OBJFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.OBJ };
    }

    public class STLFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.STL };
    }

    public class USDFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.USD };
    }

    public class BlendFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.Blend };
    }

    public class MayaFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.Maya };
    }

    public class MaxFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.Max };
    }

    public class UnityFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.Unity };
    }

    public class UnrealFormatHandler : IFormatHandler, IDisposable;
    {
        public void Dispose() { }
        public Task<ModelData> LoadAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task SaveAsync(string filePath, ModelData modelData, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ModelInfo> GetInfoAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult(new ModelInfo());
        public bool CanConvertDirectlyTo(ModelFormat targetFormat) => false;
        public Task<ModelData> ConvertToAsync(ModelData sourceModel, ModelFormat targetFormat, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public Task<IntermediateModel> ConvertToIntermediateAsync(ModelData sourceModel, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new IntermediateModel());
        public Task<ModelData> ConvertFromIntermediateAsync(IntermediateModel intermediate, ConversionConfiguration configuration, CancellationToken cancellationToken) => Task.FromResult(new ModelData());
        public IEnumerable<ModelFormat> GetSupportedOutputFormats() => new[] { ModelFormat.Unreal };
    }

    // Intermediate model classes;
    public class IntermediateMesh;
    {
        public Vector3[] Positions { get; set; }
        public Vector3[] Normals { get; set; }
        public Vector2[] UVs { get; set; }
        public int[] Indices { get; set; }
        public int MaterialIndex { get; set; }
    }

    public class IntermediateMaterial;
    {
        public string Name { get; set; }
        public Vector3 Color { get; set; }
        public float Metallic { get; set; }
        public float Roughness { get; set; }
        public string[] TexturePaths { get; set; }
    }

    public class IntermediateTexture;
    {
        public string Name { get; set; }
        public byte[] Data { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Channels { get; set; }
    }

    public class IntermediateSkeleton;
    {
        public List<IntermediateBone> Bones { get; set; }
    }

    public class IntermediateBone;
    {
        public string Name { get; set; }
        public int ParentIndex { get; set; }
        public Matrix4x4 Transform { get; set; }
    }

    public class IntermediateAnimation;
    {
        public string Name { get; set; }
        public float Duration { get; set; }
        public List<IntermediateAnimationTrack> Tracks { get; set; }
    }

    public class IntermediateAnimationTrack;
    {
        public string BoneName { get; set; }
        public float[] Times { get; set; }
        public Vector3[] Positions { get; set; }
        public Quaternion[] Rotations { get; set; }
        public Vector3[] Scales { get; set; }
    }

    #endregion;

    #region Event Args Classes;

    public class ConversionStartedEventArgs : EventArgs;
    {
        public string ConversionId { get; }
        public string SourcePath { get; }
        public string DestinationPath { get; }
        public ModelFormat TargetFormat { get; }

        public ConversionStartedEventArgs(string conversionId, string sourcePath, string destinationPath, ModelFormat targetFormat)
        {
            ConversionId = conversionId;
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
            TargetFormat = targetFormat;
        }
    }

    public class ConversionProgressEventArgs : EventArgs;
    {
        public string ConversionId { get; }
        public int ProgressPercentage { get; }
        public string CurrentStep { get; }
        public DateTime Timestamp { get; }

        public ConversionProgressEventArgs(string conversionId, int progressPercentage, string currentStep)
        {
            ConversionId = conversionId;
            ProgressPercentage = progressPercentage;
            CurrentStep = currentStep;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class ConversionCompletedEventArgs : EventArgs;
    {
        public string ConversionId { get; }
        public ConversionResult Result { get; }

        public ConversionCompletedEventArgs(string conversionId, ConversionResult result)
        {
            ConversionId = conversionId;
            Result = result;
        }
    }

    public class ConversionFailedEventArgs : EventArgs;
    {
        public string ConversionId { get; }
        public string SourcePath { get; }
        public ModelFormat TargetFormat { get; }
        public string ErrorMessage { get; }
        public Exception Exception { get; }

        public ConversionFailedEventArgs(string conversionId, string sourcePath, ModelFormat targetFormat, string errorMessage, Exception exception)
        {
            ConversionId = conversionId;
            SourcePath = sourcePath;
            TargetFormat = targetFormat;
            ErrorMessage = errorMessage;
            Exception = exception;
        }
    }

    public class FormatRegisteredEventArgs : EventArgs;
    {
        public ModelFormat Format { get; }
        public string HandlerName { get; }

        public FormatRegisteredEventArgs(ModelFormat format, string handlerName)
        {
            Format = format;
            HandlerName = handlerName;
        }
    }

    public class PresetCreatedEventArgs : EventArgs;
    {
        public ConversionPreset Preset { get; }

        public PresetCreatedEventArgs(ConversionPreset preset)
        {
            Preset = preset;
        }
    }

    #endregion;

    #region Custom Exceptions;

    public class ConversionException : Exception
    {
        public ConversionException(string message) : base(message) { }
        public ConversionException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
