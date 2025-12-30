using NEDA.AI.MachineLearning;
using NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
using NEDA.Core.Common;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Engine;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.EngineIntegration.AssetManager;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.EngineIntegration.Unreal.BlenderIntegration;
{
    /// <summary>
    /// 3D model generation engine with support for procedural generation, optimization,
    /// and integration with Blender and other 3D modeling software;
    /// </summary>
    public class ModelGenerator : IModelGenerator, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IAssetImporter _assetImporter;
        private readonly ModelGeneratorConfiguration _configuration;
        private readonly Random _random;
        private bool _disposed;

        private static readonly string[] SupportedFormats =
        {
            ".fbx", ".obj", ".gltf", ".glb", ".blend", ".dae", ".stl", ".ply"
        };

        /// <summary>
        /// Model generation events;
        /// </summary>
        public event EventHandler<ModelGenerationEventArgs> ModelGenerationStarted;
        public event EventHandler<ModelGenerationEventArgs> ModelGenerationCompleted;
        public event EventHandler<ModelGenerationErrorEventArgs> ModelGenerationError;

        /// <summary>
        /// Initialize model generator with dependencies;
        /// </summary>
        /// <param name="logger">Logging service</param>
        /// <param name="securityManager">Security manager</param>
        /// <param name="assetImporter">Asset import service</param>
        /// <param name="configuration">Generator configuration</param>
        public ModelGenerator(
            ILogger logger,
            ISecurityManager securityManager,
            IAssetImporter assetImporter,
            ModelGeneratorConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _assetImporter = assetImporter ?? throw new ArgumentNullException(nameof(assetImporter));
            _configuration = configuration ?? ModelGeneratorConfiguration.Default;
            _random = new Random(Guid.NewGuid().GetHashCode());

            _logger.LogInformation($"ModelGenerator initialized with configuration: {_configuration}");
        }

        /// <summary>
        /// Generate a 3D model based on parameters and templates;
        /// </summary>
        /// <param name="parameters">Model generation parameters</param>
        /// <param name="templatePath">Optional template file path</param>
        /// <returns>Generated model metadata</returns>
        public async Task<ModelGenerationResult> GenerateModelAsync(
            ModelGenerationParameters parameters,
            string templatePath = null)
        {
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            if (!_securityManager.ValidateOperation("ModelGeneration", parameters.UserContext))
                throw new UnauthorizedAccessException("User not authorized for model generation");

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                OnModelGenerationStarted(new ModelGenerationEventArgs;
                {
                    OperationId = operationId,
                    Parameters = parameters,
                    StartTime = startTime;
                });

                _logger.LogInformation($"Starting model generation {operationId} for type: {parameters.ModelType}");

                // Validate parameters;
                ValidateParameters(parameters);

                // Check template if provided;
                ModelTemplate template = null;
                if (!string.IsNullOrWhiteSpace(templatePath))
                {
                    template = await LoadTemplateAsync(templatePath);
                }

                // Generate based on model type;
                GeneratedModel model = parameters.ModelType switch;
                {
                    ModelType.Procedural => await GenerateProceduralModelAsync(parameters, template),
                    ModelType.Parametric => await GenerateParametricModelAsync(parameters, template),
                    ModelType.TemplateBased => await GenerateFromTemplateAsync(parameters, template),
                    ModelType.AI_Generated => await GenerateAIModelAsync(parameters),
                    _ => throw new NotSupportedException($"Model type {parameters.ModelType} is not supported")
                };

                // Optimize if requested;
                if (parameters.Optimize)
                {
                    model = await OptimizeModelAsync(model, parameters.OptimizationLevel);
                }

                // Apply materials and textures;
                if (parameters.ApplyMaterials)
                {
                    model = await ApplyMaterialsAsync(model, parameters.MaterialSettings);
                }

                // Export to desired format;
                var exportResult = await ExportModelAsync(model, parameters.ExportFormat, parameters.OutputPath);

                var result = new ModelGenerationResult;
                {
                    Success = true,
                    OperationId = operationId,
                    GeneratedModel = model,
                    ExportPath = exportResult.FilePath,
                    FileSize = exportResult.FileSize,
                    GenerationTime = DateTime.UtcNow - startTime,
                    Statistics = new ModelStatistics;
                    {
                        VertexCount = model.VertexCount,
                        TriangleCount = model.TriangleCount,
                        MeshCount = model.MeshCount,
                        MaterialCount = model.Materials?.Count ?? 0,
                        TextureCount = model.Textures?.Count ?? 0;
                    }
                };

                _logger.LogInformation($"Model generation {operationId} completed successfully. " +
                    $"Vertex count: {result.Statistics.VertexCount}, " +
                    $"Generation time: {result.GenerationTime.TotalSeconds:F2}s");

                OnModelGenerationCompleted(new ModelGenerationEventArgs;
                {
                    OperationId = operationId,
                    Parameters = parameters,
                    StartTime = startTime,
                    EndTime = DateTime.UtcNow,
                    Result = result;
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Model generation {operationId} failed");

                OnModelGenerationError(new ModelGenerationErrorEventArgs;
                {
                    OperationId = operationId,
                    Parameters = parameters,
                    Error = ex,
                    Timestamp = DateTime.UtcNow;
                });

                throw new ModelGenerationException($"Failed to generate model: {ex.Message}", ex, operationId);
            }
        }

        /// <summary>
        /// Generate multiple models in batch;
        /// </summary>
        public async Task<BatchGenerationResult> GenerateBatchAsync(
            IEnumerable<ModelGenerationParameters> parametersList,
            BatchGenerationOptions options)
        {
            if (parametersList == null)
                throw new ArgumentNullException(nameof(parametersList));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var batchId = Guid.NewGuid().ToString();
            var results = new List<ModelGenerationResult>();
            var errors = new List<BatchGenerationError>();

            _logger.LogInformation($"Starting batch model generation {batchId} with {parametersList.Count()} models");

            int processed = 0;
            int total = parametersList.Count();

            // Process in parallel if allowed;
            if (options.MaxDegreeOfParallelism > 1)
            {
                var parallelOptions = new ParallelOptions;
                {
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                    CancellationToken = options.CancellationToken;
                };

                await Parallel.ForEachAsync(parametersList, parallelOptions, async (parameters, cancellationToken) =>
                {
                    try
                    {
                        var result = await GenerateModelAsync(parameters);
                        lock (results)
                        {
                            results.Add(result);
                        }
                        Interlocked.Increment(ref processed);

                        options.ProgressCallback?.Invoke(new BatchProgress;
                        {
                            BatchId = batchId,
                            Processed = processed,
                            Total = total,
                            SuccessCount = results.Count,
                            ErrorCount = errors.Count;
                        });
                    }
                    catch (Exception ex)
                    {
                        var error = new BatchGenerationError;
                        {
                            Parameters = parameters,
                            Error = ex,
                            Timestamp = DateTime.UtcNow;
                        };
                        lock (errors)
                        {
                            errors.Add(error);
                        }
                        Interlocked.Increment(ref processed);

                        options.ProgressCallback?.Invoke(new BatchProgress;
                        {
                            BatchId = batchId,
                            Processed = processed,
                            Total = total,
                            SuccessCount = results.Count,
                            ErrorCount = errors.Count;
                        });
                    }
                });
            }
            else;
            {
                // Sequential processing;
                foreach (var parameters in parametersList)
                {
                    if (options.CancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var result = await GenerateModelAsync(parameters);
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new BatchGenerationError;
                        {
                            Parameters = parameters,
                            Error = ex,
                            Timestamp = DateTime.UtcNow;
                        });
                    }

                    processed++;
                    options.ProgressCallback?.Invoke(new BatchProgress;
                    {
                        BatchId = batchId,
                        Processed = processed,
                        Total = total,
                        SuccessCount = results.Count,
                        ErrorCount = errors.Count;
                    });
                }
            }

            return new BatchGenerationResult;
            {
                BatchId = batchId,
                SuccessCount = results.Count,
                ErrorCount = errors.Count,
                Results = results,
                Errors = errors,
                TotalProcessingTime = DateTime.UtcNow - options.StartTime;
            };
        }

        /// <summary>
        /// Generate LOD (Level of Detail) models from source model;
        /// </summary>
        public async Task<LODGenerationResult> GenerateLODsAsync(
            string sourceModelPath,
            LODGenerationParameters lodParameters)
        {
            if (string.IsNullOrWhiteSpace(sourceModelPath))
                throw new ArgumentException("Source model path cannot be empty", nameof(sourceModelPath));
            if (lodParameters == null)
                throw new ArgumentNullException(nameof(lodParameters));

            if (!File.Exists(sourceModelPath))
                throw new FileNotFoundException($"Source model not found: {sourceModelPath}");

            var operationId = Guid.NewGuid().ToString();
            _logger.LogInformation($"Starting LOD generation {operationId} for: {sourceModelPath}");

            try
            {
                // Import source model;
                var sourceModel = await _assetImporter.ImportModelAsync(sourceModelPath, new ImportOptions;
                {
                    ImportMaterials = true,
                    ImportTextures = true,
                    ImportAnimations = lodParameters.PreserveAnimations;
                });

                var lodModels = new List<GeneratedModel>();
                var lodLevels = lodParameters.LODLevels.OrderByDescending(l => l.ScreenSize).ToList();

                foreach (var lodLevel in lodLevels)
                {
                    var simplifiedModel = await SimplifyMeshAsync(sourceModel, lodLevel.ReductionFactor);

                    // Apply additional optimizations;
                    if (lodLevel.OptimizeGeometry)
                    {
                        simplifiedModel = await OptimizeGeometryAsync(simplifiedModel);
                    }

                    if (lodLevel.ReduceMaterials)
                    {
                        simplifiedModel = await ReduceMaterialsAsync(simplifiedModel, lodLevel.MaxMaterials);
                    }

                    lodModels.Add(simplifiedModel);
                }

                // Export LODs;
                var exportResults = new List<LODExportResult>();
                string basePath = Path.Combine(
                    Path.GetDirectoryName(sourceModelPath) ?? ".",
                    Path.GetFileNameWithoutExtension(sourceModelPath));

                for (int i = 0; i < lodModels.Count; i++)
                {
                    var lodModel = lodModels[i];
                    var lodLevel = lodLevels[i];

                    string lodPath = $"{basePath}_LOD{i}{Path.GetExtension(sourceModelPath)}";
                    var exportResult = await ExportModelAsync(lodModel,
                        Path.GetExtension(sourceModelPath).TrimStart('.'),
                        lodPath);

                    exportResults.Add(new LODExportResult;
                    {
                        LODLevel = i,
                        ScreenSize = lodLevel.ScreenSize,
                        FilePath = exportResult.FilePath,
                        FileSize = exportResult.FileSize,
                        VertexCount = lodModel.VertexCount,
                        TriangleCount = lodModel.TriangleCount;
                    });
                }

                return new LODGenerationResult;
                {
                    Success = true,
                    OperationId = operationId,
                    SourceModel = sourceModelPath,
                    LODs = exportResults,
                    TotalReduction = CalculateReductionPercentage(
                        sourceModel.VertexCount,
                        lodModels.Last().VertexCount)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"LOD generation {operationId} failed");
                throw new ModelGenerationException($"Failed to generate LODs: {ex.Message}", ex, operationId);
            }
        }

        /// <summary>
        /// Generate terrain model from heightmap or parameters;
        /// </summary>
        public async Task<TerrainGenerationResult> GenerateTerrainAsync(
            TerrainGenerationParameters parameters)
        {
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            var operationId = Guid.NewGuid().ToString();
            _logger.LogInformation($"Starting terrain generation {operationId}");

            try
            {
                float[,] heightmap;

                if (!string.IsNullOrWhiteSpace(parameters.HeightmapPath))
                {
                    heightmap = await LoadHeightmapAsync(parameters.HeightmapPath);
                }
                else if (parameters.UseProceduralHeightmap)
                {
                    heightmap = GenerateProceduralHeightmap(
                        parameters.Width,
                        parameters.Height,
                        parameters.NoiseParameters);
                }
                else;
                {
                    heightmap = new float[parameters.Width, parameters.Height];
                    // Initialize with base height;
                    for (int x = 0; x < parameters.Width; x++)
                    {
                        for (int y = 0; y < parameters.Height; y++)
                        {
                            heightmap[x, y] = parameters.BaseHeight;
                        }
                    }
                }

                // Generate terrain mesh from heightmap;
                var terrainMesh = await GenerateTerrainMeshAsync(heightmap, parameters);

                // Apply materials;
                if (parameters.MaterialLayers?.Any() == true)
                {
                    terrainMesh = await ApplyTerrainMaterialsAsync(terrainMesh, parameters.MaterialLayers);
                }

                // Add foliage if requested;
                if (parameters.GenerateFoliage)
                {
                    terrainMesh = await AddFoliageAsync(terrainMesh, parameters.FoliageParameters);
                }

                // Export terrain;
                string outputPath = parameters.OutputPath ??
                    $"terrain_{DateTime.Now:yyyyMMdd_HHmmss}.{parameters.ExportFormat}";

                var exportResult = await ExportModelAsync(terrainMesh, parameters.ExportFormat, outputPath);

                return new TerrainGenerationResult;
                {
                    Success = true,
                    OperationId = operationId,
                    TerrainMesh = terrainMesh,
                    Heightmap = heightmap,
                    ExportPath = exportResult.FilePath,
                    Dimensions = new TerrainDimensions;
                    {
                        Width = parameters.Width,
                        Height = parameters.Height,
                        MaxElevation = heightmap.Cast<float>().Max(),
                        MinElevation = heightmap.Cast<float>().Min()
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Terrain generation {operationId} failed");
                throw new ModelGenerationException($"Failed to generate terrain: {ex.Message}", ex, operationId);
            }
        }

        /// <summary>
        /// Convert 2D image to 3D model (extrusion/lathe)
        /// </summary>
        public async Task<ImageToModelResult> ConvertImageToModelAsync(
            string imagePath,
            ImageConversionParameters parameters)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                throw new ArgumentException("Image path cannot be empty", nameof(imagePath));
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            if (!File.Exists(imagePath))
                throw new FileNotFoundException($"Image not found: {imagePath}");

            var operationId = Guid.NewGuid().ToString();
            _logger.LogInformation($"Starting image to model conversion {operationId} for: {imagePath}");

            try
            {
                // Load and process image;
                var imageData = await ProcessImageForConversionAsync(imagePath, parameters);

                GeneratedModel model;

                switch (parameters.ConversionMethod)
                {
                    case ImageConversionMethod.Extrusion:
                        model = await ExtrudeImageAsync(imageData, parameters.ExtrusionParameters);
                        break;
                    case ImageConversionMethod.Lathe:
                        model = await LatheImageAsync(imageData, parameters.LatheParameters);
                        break;
                    case ImageConversionMethod.Displacement:
                        model = await CreateDisplacementModelAsync(imageData, parameters.DisplacementParameters);
                        break;
                    default:
                        throw new NotSupportedException($"Conversion method {parameters.ConversionMethod} not supported");
                }

                // Export model;
                string outputPath = parameters.OutputPath ??
                    $"{Path.GetFileNameWithoutExtension(imagePath)}_{parameters.ConversionMethod}.{parameters.ExportFormat}";

                var exportResult = await ExportModelAsync(model, parameters.ExportFormat, outputPath);

                return new ImageToModelResult;
                {
                    Success = true,
                    OperationId = operationId,
                    SourceImage = imagePath,
                    GeneratedModel = model,
                    ExportPath = exportResult.FilePath,
                    ConversionMethod = parameters.ConversionMethod;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Image to model conversion {operationId} failed");
                throw new ModelGenerationException($"Failed to convert image to model: {ex.Message}", ex, operationId);
            }
        }

        #region Private Methods;

        private async Task<GeneratedModel> GenerateProceduralModelAsync(
            ModelGenerationParameters parameters,
            ModelTemplate template)
        {
            _logger.LogDebug($"Generating procedural model with complexity: {parameters.Complexity}");

            // Generate base shape;
            var baseShape = await GenerateBaseShapeAsync(parameters.ShapeParameters);

            // Apply modifications;
            if (parameters.Modifications?.Any() == true)
            {
                foreach (var modification in parameters.Modifications)
                {
                    baseShape = await ApplyModificationAsync(baseShape, modification);
                }
            }

            // Add details;
            if (parameters.AddDetails)
            {
                baseShape = await AddSurfaceDetailsAsync(baseShape, parameters.DetailParameters);
            }

            return baseShape;
        }

        private async Task<GeneratedModel> GenerateParametricModelAsync(
            ModelGenerationParameters parameters,
            ModelTemplate template)
        {
            _logger.LogDebug($"Generating parametric model with {parameters.Params?.Count ?? 0} parameters");

            // Parse and validate parameters;
            var validatedParams = ValidateAndParseParameters(parameters.Params);

            // Generate model based on parametric equations;
            var vertices = new List<Vector3>();
            var triangles = new List<Triangle>();

            // This is a simplified example - actual implementation would use parametric equations;
            switch (parameters.ParametricType)
            {
                case ParametricType.Surface:
                    vertices = await GenerateParametricSurfaceAsync(validatedParams);
                    triangles = TriangulateSurface(vertices);
                    break;
                case ParametricType.Volume:
                    vertices = await GenerateParametricVolumeAsync(validatedParams);
                    triangles = TriangulateVolume(vertices);
                    break;
                default:
                    throw new NotSupportedException($"Parametric type {parameters.ParametricType} not supported");
            }

            return new GeneratedModel;
            {
                Vertices = vertices,
                Triangles = triangles,
                ModelType = ModelType.Parametric,
                GenerationParameters = parameters;
            };
        }

        private async Task<GeneratedModel> GenerateFromTemplateAsync(
            ModelGenerationParameters parameters,
            ModelTemplate template)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));

            _logger.LogDebug($"Generating model from template: {template.Name}");

            // Clone template geometry
            var model = template.BaseModel.Clone();

            // Apply parameter overrides;
            if (parameters.TemplateParameters?.Any() == true)
            {
                model = await ApplyTemplateParametersAsync(model, template, parameters.TemplateParameters);
            }

            return model;
        }

        private async Task<GeneratedModel> GenerateAIModelAsync(
            ModelGenerationParameters parameters)
        {
            _logger.LogDebug("Generating AI-based model");

            // This would integrate with AI/ML models for model generation;
            // For now, placeholder implementation;

            // Load AI model if not already loaded;
            if (_aiModel == null)
            {
                _aiModel = await LoadAIModelAsync(_configuration.AIModelPath);
            }

            // Generate using AI model;
            var aiResult = await _aiModel.GenerateAsync(parameters.AIParameters);

            return ConvertAIRepresentationToModel(aiResult);
        }

        private async Task<GeneratedModel> SimplifyMeshAsync(
            GeneratedModel model,
            float reductionFactor)
        {
            _logger.LogDebug($"Simplifying mesh by {reductionFactor:P0}");

            if (reductionFactor <= 0 || reductionFactor >= 1)
                throw new ArgumentException("Reduction factor must be between 0 and 1", nameof(reductionFactor));

            // Implementation of mesh simplification algorithm (e.g., Quadric Error Metrics)
            return await Task.Run(() =>
            {
                var simplified = MeshSimplifier.Simplify(model, reductionFactor);
                _logger.LogDebug($"Mesh simplified: {model.VertexCount} -> {simplified.VertexCount} vertices");
                return simplified;
            });
        }

        private async Task<ModelTemplate> LoadTemplateAsync(string templatePath)
        {
            if (!File.Exists(templatePath))
                throw new FileNotFoundException($"Template file not found: {templatePath}");

            string extension = Path.GetExtension(templatePath).ToLowerInvariant();

            return extension switch;
            {
                ".json" => await LoadJsonTemplateAsync(templatePath),
                ".xml" => await LoadXmlTemplateAsync(templatePath),
                ".blend" => await LoadBlenderTemplateAsync(templatePath),
                _ => throw new NotSupportedException($"Template format {extension} not supported")
            };
        }

        private void ValidateParameters(ModelGenerationParameters parameters)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(parameters.OutputPath))
            {
                errors.Add("Output path is required");
            }

            if (parameters.ModelType == ModelType.Parametric && parameters.Params?.Any() != true)
            {
                errors.Add("Parameters are required for parametric models");
            }

            if (parameters.Complexity < 1 || parameters.Complexity > 10)
            {
                errors.Add("Complexity must be between 1 and 10");
            }

            if (errors.Any())
            {
                throw new ModelGenerationValidationException(
                    $"Parameter validation failed: {string.Join(", ", errors)}");
            }
        }

        private float CalculateReductionPercentage(int original, int reduced)
        {
            if (original <= 0) return 0;
            return 1f - ((float)reduced / original);
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnModelGenerationStarted(ModelGenerationEventArgs e)
        {
            ModelGenerationStarted?.Invoke(this, e);
        }

        protected virtual void OnModelGenerationCompleted(ModelGenerationEventArgs e)
        {
            ModelGenerationCompleted?.Invoke(this, e);
        }

        protected virtual void OnModelGenerationError(ModelGenerationErrorEventArgs e)
        {
            ModelGenerationError?.Invoke(this, e);
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
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    _aiModel?.Dispose();
                }

                _disposed = true;
            }
        }

        ~ModelGenerator()
        {
            Dispose(false);
        }

        #endregion;

        #region Private Fields;

        private IAIModel _aiModel;

        #endregion;
    }

    #region Supporting Classes;

    public enum ModelType;
    {
        Procedural,
        Parametric,
        TemplateBased,
        AI_Generated;
    }

    public enum ParametricType;
    {
        Surface,
        Volume,
        Curve;
    }

    public enum ImageConversionMethod;
    {
        Extrusion,
        Lathe,
        Displacement;
    }

    public class ModelGenerationParameters;
    {
        public string ModelName { get; set; }
        public ModelType ModelType { get; set; }
        public ParametricType ParametricType { get; set; }
        public int Complexity { get; set; } = 5;
        public bool Optimize { get; set; } = true;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public bool ApplyMaterials { get; set; } = true;
        public MaterialSettings MaterialSettings { get; set; }
        public Dictionary<string, object> Params { get; set; }
        public Dictionary<string, object> TemplateParameters { get; set; }
        public Dictionary<string, object> AIParameters { get; set; }
        public List<ModelModification> Modifications { get; set; }
        public bool AddDetails { get; set; }
        public DetailParameters DetailParameters { get; set; }
        public ShapeParameters ShapeParameters { get; set; }
        public string ExportFormat { get; set; } = "fbx";
        public string OutputPath { get; set; }
        public object UserContext { get; set; }
    }

    public class ModelGenerationResult;
    {
        public bool Success { get; set; }
        public string OperationId { get; set; }
        public GeneratedModel GeneratedModel { get; set; }
        public string ExportPath { get; set; }
        public long FileSize { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public ModelStatistics Statistics { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class GeneratedModel;
    {
        public List<Vector3> Vertices { get; set; }
        public List<Triangle> Triangles { get; set; }
        public List<Vector3> Normals { get; set; }
        public List<Vector2> UVs { get; set; }
        public List<Material> Materials { get; set; }
        public List<Texture> Textures { get; set; }
        public BoundingBox Bounds { get; set; }
        public ModelType ModelType { get; set; }
        public object GenerationParameters { get; set; }
        public int VertexCount => Vertices?.Count ?? 0;
        public int TriangleCount => Triangles?.Count ?? 0;
        public int MeshCount { get; set; } = 1;

        public GeneratedModel Clone()
        {
            return new GeneratedModel;
            {
                Vertices = new List<Vector3>(Vertices),
                Triangles = new List<Triangle>(Triangles),
                Normals = new List<Vector3>(Normals),
                UVs = new List<Vector2>(UVs),
                Materials = new List<Material>(Materials),
                Textures = new List<Texture>(Textures),
                Bounds = Bounds,
                ModelType = ModelType,
                GenerationParameters = GenerationParameters,
                MeshCount = MeshCount;
            };
        }
    }

    public class ModelGenerationEventArgs : EventArgs;
    {
        public string OperationId { get; set; }
        public ModelGenerationParameters Parameters { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ModelGenerationResult Result { get; set; }
    }

    public class ModelGenerationErrorEventArgs : EventArgs;
    {
        public string OperationId { get; set; }
        public ModelGenerationParameters Parameters { get; set; }
        public Exception Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    [Serializable]
    public class ModelGenerationException : Exception
    {
        public string OperationId { get; }

        public ModelGenerationException(string message, Exception innerException, string operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }

        protected ModelGenerationException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context)
        {
        }
    }

    public class ModelGenerationValidationException : Exception
    {
        public ModelGenerationValidationException(string message) : base(message)
        {
        }
    }

    #endregion;
}
