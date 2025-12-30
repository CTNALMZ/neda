using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.NeuralNetwork.CognitiveModels.CreativeThinking;
using NEDA.NeuralNetwork.DeepLearning.NeuralModels;
using NEDA.NeuralNetwork.PatternRecognition;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.DeepLearning.NeuralModels;
{
    /// <summary>
    /// Sinir ağı katmanları oluşturmak, yapılandırmak ve optimize etmek için gelişmiş katman inşaatçısı;
    /// </summary>
    public class LayerBuilder : ILayerBuilder, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IPerformanceMetrics _performanceMetrics;
        private readonly IPatternRecognizer _patternRecognizer;

        private readonly ConcurrentDictionary<string, LayerTemplate> _layerTemplates;
        private readonly ConcurrentDictionary<string, LayerConfiguration> _layerConfigurations;
        private readonly ConcurrentDictionary<string, BuiltLayer> _builtLayers;
        private readonly LayerFactory _layerFactory;
        private readonly ArchitectureValidator _architectureValidator;
        private readonly LayerOptimizer _layerOptimizer;

        private readonly SemaphoreSlim _buildLock;
        private readonly Timer _cleanupTimer;
        private readonly object _disposeLock = new object();
        private bool _disposed = false;
        private int _layerCounter = 0;

        /// <summary>
        /// Katman inşa seçenekleri;
        /// </summary>
        public class LayerBuildOptions;
        {
            public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Balanced;
            public bool EnableAutoConfiguration { get; set; } = true;
            public bool EnableParallelBuilding { get; set; } = true;
            public bool EnableCaching { get; set; } = true;
            public bool EnableValidation { get; set; } = true;
            public bool EnableProfiling { get; set; } = true;
            public TimeSpan BuildTimeout { get; set; } = TimeSpan.FromMinutes(5);
            public ResourceConstraints ResourceConstraints { get; set; } = new ResourceConstraints();
            public MemoryAllocationStrategy MemoryStrategy { get; set; } = MemoryAllocationStrategy.Optimized;
        }

        /// <summary>
        /// Katman tipi;
        /// </summary>
        public enum LayerType;
        {
            Dense,
            Convolutional,
            Recurrent,
            LSTM,
            GRU,
            Attention,
            Transformer,
            Pooling,
            Normalization,
            Dropout,
            Embedding,
            Flatten,
            Concatenate,
            Reshape,
            Custom;
        }

        /// <summary>
        /// Aktivasyon fonksiyonu;
        /// </summary>
        public enum ActivationFunction;
        {
            ReLU,
            Sigmoid,
            Tanh,
            Softmax,
            LeakyReLU,
            ELU,
            SELU,
            GELU,
            Swish,
            Mish,
            Linear;
        }

        /// <summary>
        /// Optimizasyon seviyesi;
        /// </summary>
        public enum OptimizationLevel;
        {
            Minimal,
            Balanced,
            Aggressive,
            Extreme;
        }

        /// <summary>
        /// Bellek tahsis stratejisi;
        /// </summary>
        public enum MemoryAllocationStrategy;
        {
            Conservative,
            Balanced,
            Optimized,
            Aggressive;
        }

        /// <summary>
        /// Katman inşaatçısını başlatır;
        /// </summary>
        public LayerBuilder(
            ILogger logger,
            IPerformanceMetrics performanceMetrics,
            IPatternRecognizer patternRecognizer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _performanceMetrics = performanceMetrics ?? throw new ArgumentNullException(nameof(performanceMetrics));
            _patternRecognizer = patternRecognizer ?? throw new ArgumentNullException(nameof(patternRecognizer));

            _layerTemplates = new ConcurrentDictionary<string, LayerTemplate>();
            _layerConfigurations = new ConcurrentDictionary<string, LayerConfiguration>();
            _builtLayers = new ConcurrentDictionary<string, BuiltLayer>();

            _layerFactory = new LayerFactory(_logger);
            _architectureValidator = new ArchitectureValidator(_logger);
            _layerOptimizer = new LayerOptimizer(_logger, _performanceMetrics);

            _buildLock = new SemaphoreSlim(1, 1);
            _cleanupTimer = new Timer(CleanupUnusedLayers, null,
                TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

            _logger.LogInformation("LayerBuilder initialized", GetType().Name);
        }

        /// <summary>
        /// Katman inşaatçısını şablonlar ve konfigürasyonlarla başlatır;
        /// </summary>
        public async Task InitializeAsync(IEnumerable<LayerTemplate> templates = null)
        {
            try
            {
                _logger.LogInformation("Initializing LayerBuilder", GetType().Name);

                // Yerleşik şablonları yükle;
                await LoadBuiltInTemplatesAsync();

                // Özel şablonları yükle;
                if (templates != null)
                {
                    foreach (var template in templates)
                    {
                        await RegisterLayerTemplateAsync(template);
                    }
                }

                // Katman fabrikasını başlat;
                await _layerFactory.InitializeAsync();

                // Optimizasyon modellerini eğit;
                await TrainOptimizationModelsAsync();

                _logger.LogInformation($"LayerBuilder initialized with {_layerTemplates.Count} templates",
                    GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError($"LayerBuilder initialization failed: {ex.Message}",
                    GetType().Name, ex);
                throw new LayerBuilderException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Yeni bir sinir ağı katmanı oluşturur;
        /// </summary>
        public async Task<INeuralLayer> BuildLayerAsync(
            LayerSpecification specification,
            LayerBuildOptions options = null)
        {
            if (specification == null)
                throw new ArgumentNullException(nameof(specification));

            options = options ?? new LayerBuildOptions();
            string layerId = GenerateLayerId(specification);
            CancellationTokenSource cts = null;

            try
            {
                cts = new CancellationTokenSource(options.BuildTimeout);

                _logger.LogInformation($"Building layer: {specification.Name} ({specification.Type})",
                    GetType().Name);

                // Önbelleği kontrol et;
                if (options.EnableCaching)
                {
                    var cachedLayer = await GetCachedLayerAsync(layerId, specification);
                    if (cachedLayer != null)
                    {
                        _logger.LogDebug($"Using cached layer: {layerId}", GetType().Name);
                        return cachedLayer;
                    }
                }

                // Otomatik konfigürasyon;
                if (options.EnableAutoConfiguration)
                {
                    specification = await AutoConfigureSpecificationAsync(specification, options);
                }

                // Katman yapısını doğrula;
                var validationResult = await ValidateLayerSpecificationAsync(specification, options);
                if (!validationResult.IsValid)
                {
                    throw new LayerValidationException($"Layer validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // Katmanı oluştur;
                INeuralLayer layer;
                switch (specification.Type)
                {
                    case LayerType.Dense:
                        layer = await BuildDenseLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Convolutional:
                        layer = await BuildConvolutionalLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Recurrent:
                        layer = await BuildRecurrentLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.LSTM:
                        layer = await BuildLSTMLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.GRU:
                        layer = await BuildGRULayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Attention:
                        layer = await BuildAttentionLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Transformer:
                        layer = await BuildTransformerLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Pooling:
                        layer = await BuildPoolingLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Normalization:
                        layer = await BuildNormalizationLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Dropout:
                        layer = await BuildDropoutLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Embedding:
                        layer = await BuildEmbeddingLayerAsync(specification, options, cts.Token);
                        break;

                    case LayerType.Custom:
                        layer = await BuildCustomLayerAsync(specification, options, cts.Token);
                        break;

                    default:
                        throw new ArgumentException($"Unsupported layer type: {specification.Type}");
                }

                // Katmanı optimize et;
                if (options.OptimizationLevel != OptimizationLevel.Minimal)
                {
                    layer = await OptimizeLayerAsync(layer, specification, options, cts.Token);
                }

                // Profil oluştur;
                if (options.EnableProfiling)
                {
                    await ProfileLayerAsync(layer, specification, options);
                }

                // Önbelleğe ekle;
                if (options.EnableCaching)
                {
                    await CacheBuiltLayerAsync(layerId, layer, specification, options);
                }

                // Konfigürasyonu kaydet;
                var configuration = new LayerConfiguration;
                {
                    LayerId = layerId,
                    Specification = specification,
                    BuildOptions = options,
                    BuildTime = DateTime.UtcNow;
                };
                _layerConfigurations[layerId] = configuration;

                _logger.LogInformation($"Layer built successfully: {layer.Name} (ID: {layer.Id})",
                    GetType().Name);

                return layer;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Layer build timed out: {specification.Name}", GetType().Name);
                throw new LayerBuildTimeoutException($"Layer build timed out after {options.BuildTimeout}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Layer build failed for {specification.Name}: {ex.Message}",
                    GetType().Name, ex);
                throw new LayerBuilderException($"Layer build failed: {ex.Message}", ex);
            }
            finally
            {
                cts?.Dispose();
            }
        }

        /// <summary>
        /// Birden fazla katmanı paralel olarak oluşturur;
        /// </summary>
        public async Task<List<INeuralLayer>> BuildLayersInParallelAsync(
            IEnumerable<LayerSpecification> specifications,
            ParallelBuildOptions parallelOptions,
            LayerBuildOptions buildOptions = null)
        {
            if (specifications == null || !specifications.Any())
                throw new ArgumentException("At least one layer specification is required", nameof(specifications));

            buildOptions = buildOptions ?? new LayerBuildOptions();

            try
            {
                _logger.LogInformation($"Building {specifications.Count()} layers in parallel", GetType().Name);

                var layers = new ConcurrentBag<INeuralLayer>();
                var failedSpecs = new ConcurrentBag<(LayerSpecification Spec, Exception Error)>();

                var parallelOptionsInternal = new ParallelOptions;
                {
                    MaxDegreeOfParallelism = Math.Min(
                        buildOptions.ResourceConstraints.MaxConcurrentBuilds,
                        Environment.ProcessorCount),
                    CancellationToken = parallelOptions.CancellationToken;
                };

                await Parallel.ForEachAsync(
                    specifications,
                    parallelOptionsInternal,
                    async (spec, cancellationToken) =>
                    {
                        try
                        {
                            var layer = await BuildLayerAsync(spec, buildOptions);
                            layers.Add(layer);
                        }
                        catch (Exception ex)
                        {
                            failedSpecs.Add((spec, ex));
                            _logger.LogWarning($"Parallel layer build failed for {spec.Name}: {ex.Message}",
                                GetType().Name);
                        }
                    });

                // Bağlantıları kur (eğer belirtilmişse)
                if (parallelOptions.ConnectLayers)
                {
                    await ConnectLayersAsync(layers.ToList(), parallelOptions.ConnectionStrategy);
                }

                _logger.LogInformation($"Parallel build completed: {layers.Count} successful, {failedSpecs.Count} failed",
                    GetType().Name);

                return layers.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Parallel layer build failed: {ex.Message}", GetType().Name, ex);
                throw new LayerBuilderException($"Parallel build failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Önceden tanımlanmış şablondan katman oluşturur;
        /// </summary>
        public async Task<INeuralLayer> BuildLayerFromTemplateAsync(
            string templateName,
            Dictionary<string, object> parameters = null,
            LayerBuildOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(templateName))
                throw new ArgumentException("Template name is required", nameof(templateName));

            options = options ?? new LayerBuildOptions();

            try
            {
                _logger.LogInformation($"Building layer from template: {templateName}", GetType().Name);

                // Şablonu al;
                if (!_layerTemplates.TryGetValue(templateName, out var template))
                {
                    throw new LayerTemplateNotFoundException($"Template not found: {templateName}");
                }

                // Şablonu parametrelerle özelleştir;
                var specification = await CustomizeTemplateAsync(template, parameters);

                // Katmanı oluştur;
                return await BuildLayerAsync(specification, options);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Template layer build failed: {ex.Message}", GetType().Name, ex);
                throw new LayerBuilderException($"Template build failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Otomatik katman mimarisi önerir;
        /// </summary>
        public async Task<LayerArchitectureRecommendation> RecommendArchitectureAsync(
            ProblemDefinition problem,
            ArchitectureConstraints constraints)
        {
            if (problem == null)
                throw new ArgumentNullException(nameof(problem));

            if (constraints == null)
                throw new ArgumentNullException(nameof(constraints));

            try
            {
                _logger.LogInformation("Generating architecture recommendation", GetType().Name);

                // Problem analizi;
                var problemAnalysis = await AnalyzeProblemAsync(problem);

                // Benzer çözümleri bul;
                var similarSolutions = await FindSimilarSolutionsAsync(problemAnalysis, constraints);

                // Katman kombinasyonlarını oluştur;
                var layerCombinations = await GenerateLayerCombinationsAsync(
                    problemAnalysis, constraints, similarSolutions);

                // Kombinasyonları değerlendir;
                var evaluatedCombinations = await EvaluateCombinationsAsync(
                    layerCombinations, problem, constraints);

                // En iyi mimariyi seç;
                var bestArchitecture = SelectBestArchitecture(evaluatedCombinations, constraints);

                // Detaylı öneriler oluştur;
                var recommendation = new LayerArchitectureRecommendation;
                {
                    ProblemId = problem.Id,
                    ProblemType = problem.Type,
                    RecommendedArchitecture = bestArchitecture,
                    AlternativeArchitectures = evaluatedCombinations;
                        .Take(5)
                        .Select(c => c.Architecture)
                        .ToList(),
                    ConfidenceScore = bestArchitecture.Score,
                    ExpectedPerformance = await EstimatePerformanceAsync(bestArchitecture, problem),
                    ImplementationSteps = await GenerateImplementationStepsAsync(bestArchitecture),
                    Constraints = constraints;
                };

                return recommendation;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Architecture recommendation failed: {ex.Message}", GetType().Name, ex);
                throw new LayerBuilderException($"Architecture recommendation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Katmanı verilen girdiye göre optimize eder;
        /// </summary>
        public async Task<INeuralLayer> OptimizeLayerForInputAsync(
            INeuralLayer layer,
            InputCharacteristics input,
            OptimizationOptions optimizationOptions)
        {
            if (layer == null)
                throw new ArgumentNullException(nameof(layer));

            if (input == null)
                throw new ArgumentNullException(nameof(input));

            try
            {
                _logger.LogInformation($"Optimizing layer {layer.Name} for input", GetType().Name);

                // Girdi analizi;
                var inputAnalysis = await AnalyzeInputCharacteristicsAsync(input);

                // Katman uyumluluğunu kontrol et;
                var compatibility = await CheckLayerCompatibilityAsync(layer, inputAnalysis);

                // Optimizasyon stratejilerini seç;
                var strategies = await SelectOptimizationStrategiesAsync(layer, inputAnalysis, optimizationOptions);

                // Katmanı optimize et;
                var optimizedLayer = await ApplyOptimizationStrategiesAsync(
                    layer, strategies, inputAnalysis, optimizationOptions);

                // Optimizasyon sonuçlarını doğrula;
                await ValidateOptimizationAsync(optimizedLayer, inputAnalysis, optimizationOptions);

                return optimizedLayer;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Layer optimization failed: {ex.Message}", GetType().Name, ex);
                throw new LayerOptimizationException($"Layer optimization failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Katman şablonu kaydeder;
        /// </summary>
        public async Task RegisterLayerTemplateAsync(LayerTemplate template)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));

            try
            {
                await _buildLock.WaitAsync();

                // Şablonu doğrula;
                var validationResult = await ValidateTemplateAsync(template);
                if (!validationResult.IsValid)
                {
                    throw new LayerTemplateException($"Template validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // Şablonu kaydet;
                if (_layerTemplates.TryAdd(template.Name, template))
                {
                    // Şablon özelliklerini analiz et;
                    await AnalyzeTemplateAsync(template);

                    // İlgili katmanları güncelle;
                    await UpdateRelatedLayersAsync(template);

                    _logger.LogInformation($"Layer template registered: {template.Name}", GetType().Name);
                }
                else;
                {
                    _logger.LogWarning($"Template already exists: {template.Name}", GetType().Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Template registration failed: {ex.Message}", GetType().Name, ex);
                throw new LayerTemplateException($"Template registration failed: {ex.Message}", ex);
            }
            finally
            {
                _buildLock.Release();
            }
        }

        /// <summary>
        /// Katman birleştirme işlemi yapar;
        /// </summary>
        public async Task<INeuralLayer> MergeLayersAsync(
            IEnumerable<INeuralLayer> layers,
            MergeStrategy strategy,
            MergeOptions options = null)
        {
            if (layers == null || !layers.Any())
                throw new ArgumentException("At least one layer is required", nameof(layers));

            if (layers.Count() < 2)
                throw new ArgumentException("At least two layers are required for merging", nameof(layers));

            options = options ?? new MergeOptions();

            try
            {
                _logger.LogInformation($"Merging {layers.Count()} layers using {strategy} strategy", GetType().Name);

                // Katman uyumluluğunu kontrol et;
                var compatibility = await CheckMergeCompatibilityAsync(layers, strategy);
                if (!compatibility.IsCompatible)
                {
                    throw new LayerMergeException($"Layers are not compatible for merging: {compatibility.Reason}");
                }

                INeuralLayer mergedLayer;

                switch (strategy)
                {
                    case MergeStrategy.Concatenate:
                        mergedLayer = await ConcatenateLayersAsync(layers, options);
                        break;

                    case MergeStrategy.Add:
                        mergedLayer = await AddLayersAsync(layers, options);
                        break;

                    case MergeStrategy.Average:
                        mergedLayer = await AverageLayersAsync(layers, options);
                        break;

                    case MergeStrategy.Max:
                        mergedLayer = await MaxLayersAsync(layers, options);
                        break;

                    case MergeStrategy.Attention:
                        mergedLayer = await MergeWithAttentionAsync(layers, options);
                        break;

                    case MergeStrategy.Transformer:
                        mergedLayer = await MergeWithTransformerAsync(layers, options);
                        break;

                    default:
                        throw new ArgumentException($"Unsupported merge strategy: {strategy}");
                }

                // Birleştirme optimizasyonu;
                if (options.EnableOptimization)
                {
                    mergedLayer = await OptimizeMergedLayerAsync(mergedLayer, layers, options);
                }

                // Birleştirme doğrulaması;
                await ValidateMergeAsync(mergedLayer, layers, options);

                _logger.LogInformation($"Layers merged successfully into: {mergedLayer.Name}", GetType().Name);

                return mergedLayer;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Layer merge failed: {ex.Message}", GetType().Name, ex);
                throw new LayerMergeException($"Layer merge failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Katman klonlama işlemi yapar;
        /// </summary>
        public async Task<INeuralLayer> CloneLayerAsync(
            INeuralLayer sourceLayer,
            CloneOptions options = null)
        {
            if (sourceLayer == null)
                throw new ArgumentNullException(nameof(sourceLayer));

            options = options ?? new CloneOptions();

            try
            {
                _logger.LogInformation($"Cloning layer: {sourceLayer.Name}", GetType().Name);

                // Derin kopya oluştur;
                var clonedLayer = await CreateDeepCopyAsync(sourceLayer, options);

                // Klon optimizasyonu;
                if (options.OptimizeClone)
                {
                    clonedLayer = await OptimizeCloneAsync(clonedLayer, sourceLayer, options);
                }

                // Klon doğrulaması;
                await ValidateCloneAsync(clonedLayer, sourceLayer, options);

                return clonedLayer;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Layer clone failed: {ex.Message}", GetType().Name, ex);
                throw new LayerCloneException($"Layer clone failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Kayıtlı katman şablonlarını getirir;
        /// </summary>
        public IEnumerable<LayerTemplate> GetRegisteredTemplates()
        {
            return _layerTemplates.Values.ToList();
        }

        /// <summary>
        /// Katman inşa istatistiklerini getirir;
        /// </summary>
        public LayerBuildStatistics GetBuildStatistics(TimeSpan? timeRange = null)
        {
            var configurations = timeRange.HasValue;
                ? _layerConfigurations.Values.Where(c => DateTime.UtcNow - c.BuildTime <= timeRange.Value)
                : _layerConfigurations.Values;

            var stats = new LayerBuildStatistics;
            {
                TotalLayersBuilt = configurations.Count(),
                SuccessfulBuilds = configurations.Count(c => c.BuildResult?.Success == true),
                FailedBuilds = configurations.Count(c => c.BuildResult?.Success == false),
                AverageBuildTime = configurations.Any()
                    ? TimeSpan.FromTicks((long)configurations.Average(c => c.BuildResult?.BuildTime.Ticks ?? 0))
                    : TimeSpan.Zero,
                LayerTypeDistribution = configurations;
                    .GroupBy(c => c.Specification.Type)
                    .ToDictionary(g => g.Key, g => g.Count()),
                OptimizationLevelDistribution = configurations;
                    .GroupBy(c => c.BuildOptions.OptimizationLevel)
                    .ToDictionary(g => g.Key, g => g.Count()),
                RecentBuilds = configurations;
                    .OrderByDescending(c => c.BuildTime)
                    .Take(100)
                    .Select(c => new BuildRecord;
                    {
                        LayerName = c.Specification.Name,
                        LayerType = c.Specification.Type,
                        BuildTime = c.BuildResult?.BuildTime ?? TimeSpan.Zero,
                        Success = c.BuildResult?.Success ?? false,
                        Timestamp = c.BuildTime;
                    })
                    .ToList()
            };

            return stats;
        }

        /// <summary>
        /// Kullanılmayan katmanları temizler;
        /// </summary>
        public void CleanupUnusedResources(TimeSpan? ageThreshold = null)
        {
            var threshold = ageThreshold ?? TimeSpan.FromHours(1);
            var oldLayers = _builtLayers;
                .Where(kvp => DateTime.UtcNow - kvp.Value.LastAccessTime > threshold)
                .ToList();

            foreach (var kvp in oldLayers)
            {
                if (_builtLayers.TryRemove(kvp.Key, out var layer))
                {
                    try
                    {
                        layer.Layer?.Dispose();
                        _logger.LogDebug($"Cleaned up unused layer: {kvp.Key}", GetType().Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to cleanup layer {kvp.Key}: {ex.Message}", GetType().Name);
                    }
                }
            }
        }

        /// <summary>
        /// Kaynakları serbest bırakır;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (_disposeLock)
            {
                if (!_disposed)
                {
                    if (disposing)
                    {
                        // Timer'ı durdur;
                        _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        _cleanupTimer?.Dispose();

                        // Kilitleri serbest bırak;
                        _buildLock?.Dispose();

                        // Katmanları temizle;
                        foreach (var layer in _builtLayers.Values)
                        {
                            try
                            {
                                layer.Layer?.Dispose();
                            }
                            catch
                            {
                                // Ignore disposal errors;
                            }
                        }
                        _builtLayers.Clear();

                        // Alt bileşenleri dispose et;
                        _layerFactory?.Dispose();
                        _layerOptimizer?.Dispose();

                        _logger.LogInformation("LayerBuilder disposed", GetType().Name);
                    }

                    _disposed = true;
                }
            }
        }

        #region Private Implementation Methods;

        private async Task<INeuralLayer> BuildDenseLayerAsync(
            LayerSpecification spec,
            LayerBuildOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Building dense layer: {spec.Name}", GetType().Name);

            // Parametreleri çıkar;
            var units = (int)spec.Parameters["units"];
            var activation = (ActivationFunction)spec.Parameters["activation"];
            var useBias = spec.Parameters.ContainsKey("use_bias") ? (bool)spec.Parameters["use_bias"] : true;

            // Katmanı oluştur;
            var layer = await _layerFactory.CreateDenseLayerAsync(
                spec.Name,
                units,
                activation,
                useBias,
                cancellationToken);

            // İlkleme stratejisi;
            if (spec.Parameters.ContainsKey("initializer"))
            {
                var initializer = (InitializationStrategy)spec.Parameters["initializer"];
                await InitializeLayerWeightsAsync(layer, initializer, cancellationToken);
            }

            // Regularization;
            if (spec.Parameters.ContainsKey("regularizer"))
            {
                var regularizer = (RegularizationType)spec.Parameters["regularizer"];
                await ApplyRegularizationAsync(layer, regularizer, spec.Parameters, cancellationToken);
            }

            return layer;
        }

        private async Task<INeuralLayer> BuildConvolutionalLayerAsync(
            LayerSpecification spec,
            LayerBuildOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Building convolutional layer: {spec.Name}", GetType().Name);

            // Parametreleri çıkar;
            var filters = (int)spec.Parameters["filters"];
            var kernelSize = (int[])spec.Parameters["kernel_size"];
            var strides = spec.Parameters.ContainsKey("strides") ? (int[])spec.Parameters["strides"] : new[] { 1, 1 };
            var padding = spec.Parameters.ContainsKey("padding") ? (string)spec.Parameters["padding"] : "valid";
            var activation = (ActivationFunction)spec.Parameters["activation"];

            // Katmanı oluştur;
            var layer = await _layerFactory.CreateConvolutionalLayerAsync(
                spec.Name,
                filters,
                kernelSize,
                strides,
                padding,
                activation,
                cancellationToken);

            // İlkleme;
            if (spec.Parameters.ContainsKey("initializer"))
            {
                var initializer = (InitializationStrategy)spec.Parameters["initializer"];
                await InitializeLayerWeightsAsync(layer, initializer, cancellationToken);
            }

            return layer;
        }

        private async Task<INeuralLayer> BuildLSTMLayerAsync(
            LayerSpecification spec,
            LayerBuildOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Building LSTM layer: {spec.Name}", GetType().Name);

            // Parametreleri çıkar;
            var units = (int)spec.Parameters["units"];
            var returnSequences = spec.Parameters.ContainsKey("return_sequences") ?
                (bool)spec.Parameters["return_sequences"] : false;
            var activation = (ActivationFunction)spec.Parameters["activation"];
            var recurrentActivation = spec.Parameters.ContainsKey("recurrent_activation") ?
                (ActivationFunction)spec.Parameters["recurrent_activation"] : ActivationFunction.Sigmoid;

            // Katmanı oluştur;
            var layer = await _layerFactory.CreateLSTMLayerAsync(
                spec.Name,
                units,
                returnSequences,
                activation,
                recurrentActivation,
                cancellationToken);

            return layer;
        }

        private async Task<INeuralLayer> BuildAttentionLayerAsync(
            LayerSpecification spec,
            LayerBuildOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Building attention layer: {spec.Name}", GetType().Name);

            // Parametreleri çıkar;
            var heads = (int)spec.Parameters["heads"];
            var keyDim = (int)spec.Parameters["key_dim"];
            var valueDim = spec.Parameters.ContainsKey("value_dim") ? (int)spec.Parameters["value_dim"] : keyDim;
            var dropout = spec.Parameters.ContainsKey("dropout") ? (float)spec.Parameters["dropout"] : 0.0f;

            // Katmanı oluştur;
            var layer = await _layerFactory.CreateAttentionLayerAsync(
                spec.Name,
                heads,
                keyDim,
                valueDim,
                dropout,
                cancellationToken);

            return layer;
        }

        private async Task<INeuralLayer> OptimizeLayerAsync(
            INeuralLayer layer,
            LayerSpecification spec,
            LayerBuildOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Optimizing layer: {layer.Name}", GetType().Name);

            var optimizationTasks = new List<Task>();

            // Bellek optimizasyonu;
            if (options.MemoryStrategy != MemoryAllocationStrategy.Conservative)
            {
                optimizationTasks.Add(OptimizeLayerMemoryAsync(layer, spec, options, cancellationToken));
            }

            // Performans optimizasyonu;
            optimizationTasks.Add(OptimizeLayerPerformanceAsync(layer, spec, options, cancellationToken));

            // Sayısal stabilite optimizasyonu;
            optimizationTasks.Add(OptimizeNumericalStabilityAsync(layer, spec, options, cancellationToken));

            await Task.WhenAll(optimizationTasks);

            return layer;
        }

        private async Task OptimizeLayerMemoryAsync(
            INeuralLayer layer,
            LayerSpecification spec,
            LayerBuildOptions options,
            CancellationToken cancellationToken)
        {
            switch (options.MemoryStrategy)
            {
                case MemoryAllocationStrategy.Balanced:
                    await ApplyBalancedMemoryOptimizationAsync(layer, spec, cancellationToken);
                    break;

                case MemoryAllocationStrategy.Optimized:
                    await ApplyOptimizedMemoryStrategyAsync(layer, spec, cancellationToken);
                    break;

                case MemoryAllocationStrategy.Aggressive:
                    await ApplyAggressiveMemoryOptimizationAsync(layer, spec, cancellationToken);
                    break;
            }
        }

        private async Task ProfileLayerAsync(
            INeuralLayer layer,
            LayerSpecification spec,
            LayerBuildOptions options)
        {
            var profile = new LayerProfile;
            {
                LayerId = layer.Id,
                LayerName = layer.Name,
                LayerType = spec.Type,
                ParameterCount = await layer.GetParameterCountAsync(),
                MemoryUsage = await layer.EstimateMemoryUsageAsync(),
                ComputeComplexity = await layer.EstimateComputeComplexityAsync(),
                ProfilingTime = DateTime.UtcNow;
            };

            // Profili kaydet;
            // Implementasyon detayları;
        }

        private async Task<LayerSpecification> AutoConfigureSpecificationAsync(
            LayerSpecification spec,
            LayerBuildOptions options)
        {
            // Eksik parametreleri otomatik doldur;
            var defaultParams = await GetDefaultParametersAsync(spec.Type, options.OptimizationLevel);

            foreach (var param in defaultParams)
            {
                if (!spec.Parameters.ContainsKey(param.Key))
                {
                    spec.Parameters[param.Key] = param.Value;
                }
            }

            // Aktivasyon fonksiyonunu optimize et;
            if (!spec.Parameters.ContainsKey("activation"))
            {
                spec.Parameters["activation"] = await RecommendActivationAsync(spec.Type);
            }

            // İlkleme stratejisini optimize et;
            if (!spec.Parameters.ContainsKey("initializer"))
            {
                spec.Parameters["initializer"] = await RecommendInitializerAsync(spec.Type);
            }

            return spec;
        }

        private async Task<ValidationResult> ValidateLayerSpecificationAsync(
            LayerSpecification spec,
            LayerBuildOptions options)
        {
            var result = new ValidationResult();

            // Temel doğrulamalar;
            if (string.IsNullOrWhiteSpace(spec.Name))
                result.Errors.Add("Layer name is required");

            if (spec.Parameters == null)
                spec.Parameters = new Dictionary<string, object>();

            // Tip bazlı doğrulama;
            switch (spec.Type)
            {
                case LayerType.Dense:
                    if (!spec.Parameters.ContainsKey("units"))
                        result.Errors.Add("Dense layer requires 'units' parameter");
                    break;

                case LayerType.Convolutional:
                    if (!spec.Parameters.ContainsKey("filters"))
                        result.Errors.Add("Convolutional layer requires 'filters' parameter");
                    if (!spec.Parameters.ContainsKey("kernel_size"))
                        result.Errors.Add("Convolutional layer requires 'kernel_size' parameter");
                    break;

                case LayerType.LSTM:
                    if (!spec.Parameters.ContainsKey("units"))
                        result.Errors.Add("LSTM layer requires 'units' parameter");
                    break;
            }

            // Değer doğrulamaları;
            foreach (var param in spec.Parameters)
            {
                var validation = await ValidateParameterAsync(spec.Type, param.Key, param.Value);
                if (!validation.IsValid)
                {
                    result.Errors.Add($"Parameter '{param.Key}': {validation.Error}");
                }
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }

        private string GenerateLayerId(LayerSpecification spec)
        {
            var paramHash = spec.Parameters?
                .OrderBy(p => p.Key)
                .Select(p => $"{p.Key}={p.Value}")
                .Aggregate((a, b) => $"{a}|{b}") ?? "";

            return $"LAYER_{spec.Type}_{paramHash.GetHashCode():X8}_{Interlocked.Increment(ref _layerCounter)}";
        }

        private void CleanupUnusedLayers(object state)
        {
            try
            {
                CleanupUnusedResources(TimeSpan.FromMinutes(30));
                _logger.LogDebug("Unused layers cleanup completed", GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Layers cleanup failed: {ex.Message}", GetType().Name);
            }
        }

        private async Task LoadBuiltInTemplatesAsync()
        {
            var builtInTemplates = new List<LayerTemplate>
            {
                CreateDenseTemplate(),
                CreateConvolutionalTemplate(),
                CreateLSTMTemplate(),
                CreateAttentionTemplate(),
                CreateDropoutTemplate(),
                CreateBatchNormTemplate()
            };

            foreach (var template in builtInTemplates)
            {
                await RegisterLayerTemplateAsync(template);
            }
        }

        private LayerTemplate CreateDenseTemplate()
        {
            return new LayerTemplate;
            {
                Name = "StandardDense",
                Type = LayerType.Dense,
                Description = "Standard fully connected layer",
                DefaultParameters = new Dictionary<string, object>
                {
                    ["units"] = 128,
                    ["activation"] = ActivationFunction.ReLU,
                    ["use_bias"] = true,
                    ["initializer"] = InitializationStrategy.GlorotUniform,
                    ["regularizer"] = RegularizationType.None;
                },
                RecommendedUse = "General purpose hidden layers",
                PerformanceCharacteristics = new PerformanceProfile;
                {
                    MemoryUsage = MemoryUsage.Moderate,
                    ComputeComplexity = Complexity.Moderate,
                    TrainingSpeed = Speed.Fast,
                    InferenceSpeed = Speed.Fast;
                }
            };
        }

        #endregion;

        #region Public Classes and Interfaces;

        /// <summary>
        /// Katman özellikleri;
        /// </summary>
        public class LayerSpecification;
        {
            public string Name { get; set; }
            public LayerType Type { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public string Description { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Katman şablonu;
        /// </summary>
        public class LayerTemplate;
        {
            public string Name { get; set; }
            public LayerType Type { get; set; }
            public string Description { get; set; }
            public Dictionary<string, object> DefaultParameters { get; set; } = new Dictionary<string, object>();
            public Dictionary<string, ParameterConstraint> ParameterConstraints { get; set; } = new Dictionary<string, ParameterConstraint>();
            public string RecommendedUse { get; set; }
            public PerformanceProfile PerformanceCharacteristics { get; set; }
            public List<string> CompatibleLayers { get; set; } = new List<string>();
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public int UsageCount { get; set; }
        }

        /// <summary>
        /// Katman konfigürasyonu;
        /// </summary>
        private class LayerConfiguration;
        {
            public string LayerId { get; set; }
            public LayerSpecification Specification { get; set; }
            public LayerBuildOptions BuildOptions { get; set; }
            public BuildResult BuildResult { get; set; }
            public DateTime BuildTime { get; set; }
        }

        /// <summary>
        /// İnşa edilmiş katman;
        /// </summary>
        private class BuiltLayer;
        {
            public string Id { get; set; }
            public INeuralLayer Layer { get; set; }
            public LayerSpecification Specification { get; set; }
            public DateTime BuildTime { get; set; }
            public DateTime LastAccessTime { get; set; }
            public int AccessCount { get; set; }
        }

        /// <summary>
        /// Katman mimarisi önerisi;
        /// </summary>
        public class LayerArchitectureRecommendation;
        {
            public string ProblemId { get; set; }
            public string ProblemType { get; set; }
            public RecommendedArchitecture RecommendedArchitecture { get; set; }
            public List<RecommendedArchitecture> AlternativeArchitectures { get; set; } = new List<RecommendedArchitecture>();
            public double ConfidenceScore { get; set; }
            public ExpectedPerformance ExpectedPerformance { get; set; }
            public ImplementationSteps ImplementationSteps { get; set; }
            public ArchitectureConstraints Constraints { get; set; }
            public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Katman inşa istatistikleri;
        /// </summary>
        public class LayerBuildStatistics;
        {
            public int TotalLayersBuilt { get; set; }
            public int SuccessfulBuilds { get; set; }
            public int FailedBuilds { get; set; }
            public double SuccessRate => TotalLayersBuilt > 0 ? (double)SuccessfulBuilds / TotalLayersBuilt : 0;
            public TimeSpan AverageBuildTime { get; set; }
            public Dictionary<LayerType, int> LayerTypeDistribution { get; set; } = new Dictionary<LayerType, int>();
            public Dictionary<OptimizationLevel, int> OptimizationLevelDistribution { get; set; } = new Dictionary<OptimizationLevel, int>();
            public List<BuildRecord> RecentBuilds { get; set; } = new List<BuildRecord>();
        }

        /// <summary>
        /// Paralel inşa seçenekleri;
        /// </summary>
        public class ParallelBuildOptions;
        {
            public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
            public bool ContinueOnError { get; set; } = true;
            public bool ConnectLayers { get; set; } = false;
            public ConnectionStrategy ConnectionStrategy { get; set; } = ConnectionStrategy.Sequential;
            public ProgressReportingOptions ProgressReporting { get; set; } = new ProgressReportingOptions();
        }

        /// <summary>
        /// Problem tanımı;
        /// </summary>
        public class ProblemDefinition;
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Type { get; set; }
            public InputCharacteristics Input { get; set; }
            public OutputRequirements Output { get; set; }
            public Dictionary<string, object> Constraints { get; set; } = new Dictionary<string, object>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Mimari kısıtlamalar;
        /// </summary>
        public class ArchitectureConstraints;
        {
            public int MaxLayers { get; set; } = 10;
            public long MaxParameters { get; set; } = 1000000;
            public long MaxMemoryBytes { get; set; } = 100 * 1024 * 1024; // 100 MB;
            public TimeSpan MaxInferenceTime { get; set; } = TimeSpan.FromSeconds(1);
            public List<LayerType> AllowedLayerTypes { get; set; } = new List<LayerType>();
            public List<LayerType> ProhibitedLayerTypes { get; set; } = new List<LayerType>();
            public OptimizationTarget Target { get; set; } = OptimizationTarget.Accuracy;
        }

        /// <summary>
        /// Birleştirme seçenekleri;
        /// </summary>
        public class MergeOptions;
        {
            public bool EnableOptimization { get; set; } = true;
            public bool PreserveInputOrder { get; set; } = true;
            public double MergeTolerance { get; set; } = 0.001;
            public Dictionary<string, object> MergeParameters { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Klonlama seçenekleri;
        /// </summary>
        public class CloneOptions;
        {
            public bool DeepCopy { get; set; } = true;
            public bool CopyWeights { get; set; } = true;
            public bool OptimizeClone { get; set; } = true;
            public string NewName { get; set; }
            public Dictionary<string, object> Modifications { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// İlkleme stratejisi;
        /// </summary>
        public enum InitializationStrategy;
        {
            Zeros,
            Ones,
            RandomNormal,
            RandomUniform,
            GlorotNormal,
            GlorotUniform,
            HeNormal,
            HeUniform,
            LeCunNormal,
            LeCunUniform;
        }

        /// <summary>
        /// Regularizasyon tipi;
        /// </summary>
        public enum RegularizationType;
        {
            None,
            L1,
            L2,
            L1L2,
            Dropout,
            BatchNorm,
            LayerNorm;
        }

        /// <summary>
        /// Bellek kullanımı;
        /// </summary>
        public enum MemoryUsage;
        {
            Low,
            Moderate,
            High,
            VeryHigh;
        }

        /// <summary>
        /// Hesaplama karmaşıklığı;
        /// </summary>
        public enum Complexity;
        {
            Low,
            Moderate,
            High,
            VeryHigh;
        }

        /// <summary>
        /// Hız;
        /// </summary>
        public enum Speed;
        {
            Slow,
            Moderate,
            Fast,
            VeryFast;
        }

        /// <summary>
        /// Performans profili;
        /// </summary>
        public class PerformanceProfile;
        {
            public MemoryUsage MemoryUsage { get; set; }
            public Complexity ComputeComplexity { get; set; }
            public Speed TrainingSpeed { get; set; }
            public Speed InferenceSpeed { get; set; }
            public double NumericalStability { get; set; }
            public double GradientFlow { get; set; }
        }

        /// <summary>
        /// Parametre kısıtlaması;
        /// </summary>
        public class ParameterConstraint;
        {
            public Type Type { get; set; }
            public object MinValue { get; set; }
            public object MaxValue { get; set; }
            public object DefaultValue { get; set; }
            public List<object> AllowedValues { get; set; } = new List<object>();
            public string Description { get; set; }
        }

        /// <summary>
        /// Önerilen mimari;
        /// </summary>
        public class RecommendedArchitecture;
        {
            public List<LayerSpecification> Layers { get; set; } = new List<LayerSpecification>();
            public ConnectionPattern Connections { get; set; }
            public double Score { get; set; }
            public Dictionary<string, double> ScoreBreakdown { get; set; } = new Dictionary<string, double>();
            public ArchitectureExplanation Explanation { get; set; }
        }

        /// <summary>
        /// Beklenen performans;
        /// </summary>
        public class ExpectedPerformance;
        {
            public double Accuracy { get; set; }
            public TimeSpan TrainingTime { get; set; }
            public TimeSpan InferenceTime { get; set; }
            public long MemoryUsage { get; set; }
            public double ConvergenceRate { get; set; }
            public double Generalization { get; set; }
        }

        /// <summary>
        /// İnşa kaydı;
        /// </summary>
        public class BuildRecord;
        {
            public string LayerName { get; set; }
            public LayerType LayerType { get; set; }
            public TimeSpan BuildTime { get; set; }
            public bool Success { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Birleştirme stratejisi;
        /// </summary>
        public enum MergeStrategy;
        {
            Concatenate,
            Add,
            Average,
            Max,
            Attention,
            Transformer;
        }

        /// <summary>
        /// Bağlantı stratejisi;
        /// </summary>
        public enum ConnectionStrategy;
        {
            Sequential,
            Parallel,
            Residual,
            Dense,
            Attention,
            Custom;
        }

        /// <summary>
        /// Optimizasyon hedefi;
        /// </summary>
        public enum OptimizationTarget;
        {
            Accuracy,
            Speed,
            Memory,
            Power,
            Balanced;
        }

        #endregion;

        #region Exceptions;

        public class LayerBuilderException : Exception
        {
            public LayerBuilderException(string message) : base(message) { }
            public LayerBuilderException(string message, Exception inner) : base(message, inner) { }
        }

        public class LayerBuildTimeoutException : LayerBuilderException;
        {
            public LayerBuildTimeoutException(string message) : base(message) { }
        }

        public class LayerValidationException : LayerBuilderException;
        {
            public LayerValidationException(string message) : base(message) { }
        }

        public class LayerTemplateNotFoundException : LayerBuilderException;
        {
            public LayerTemplateNotFoundException(string message) : base(message) { }
        }

        public class LayerTemplateException : LayerBuilderException;
        {
            public LayerTemplateException(string message) : base(message) { }
            public LayerTemplateException(string message, Exception inner) : base(message, inner) { }
        }

        public class LayerOptimizationException : LayerBuilderException;
        {
            public LayerOptimizationException(string message) : base(message) { }
            public LayerOptimizationException(string message, Exception inner) : base(message, inner) { }
        }

        public class LayerMergeException : LayerBuilderException;
        {
            public LayerMergeException(string message) : base(message) { }
            public LayerMergeException(string message, Exception inner) : base(message, inner) { }
        }

        public class LayerCloneException : LayerBuilderException;
        {
            public LayerCloneException(string message) : base(message) { }
            public LayerCloneException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }

    /// <summary>
    /// Katman inşaatçısı arayüzü;
    /// </summary>
    public interface ILayerBuilder : IDisposable
    {
        Task InitializeAsync(IEnumerable<LayerBuilder.LayerTemplate> templates = null);
        Task<INeuralLayer> BuildLayerAsync(
            LayerBuilder.LayerSpecification specification,
            LayerBuilder.LayerBuildOptions options = null);

        Task<List<INeuralLayer>> BuildLayersInParallelAsync(
            IEnumerable<LayerBuilder.LayerSpecification> specifications,
            LayerBuilder.ParallelBuildOptions parallelOptions,
            LayerBuilder.LayerBuildOptions buildOptions = null);

        Task<INeuralLayer> BuildLayerFromTemplateAsync(
            string templateName,
            Dictionary<string, object> parameters = null,
            LayerBuilder.LayerBuildOptions options = null);

        Task<LayerBuilder.LayerArchitectureRecommendation> RecommendArchitectureAsync(
            LayerBuilder.ProblemDefinition problem,
            LayerBuilder.ArchitectureConstraints constraints);

        Task<INeuralLayer> OptimizeLayerForInputAsync(
            INeuralLayer layer,
            InputCharacteristics input,
            OptimizationOptions optimizationOptions);

        Task RegisterLayerTemplateAsync(LayerBuilder.LayerTemplate template);
        Task<INeuralLayer> MergeLayersAsync(
            IEnumerable<INeuralLayer> layers,
            LayerBuilder.MergeStrategy strategy,
            LayerBuilder.MergeOptions options = null);

        Task<INeuralLayer> CloneLayerAsync(
            INeuralLayer sourceLayer,
            LayerBuilder.CloneOptions options = null);

        IEnumerable<LayerBuilder.LayerTemplate> GetRegisteredTemplates();
        LayerBuilder.LayerBuildStatistics GetBuildStatistics(TimeSpan? timeRange = null);
        void CleanupUnusedResources(TimeSpan? ageThreshold = null);
    }

    // Sinir ağı katmanı arayüzü;
    public interface INeuralLayer : IDisposable
    {
        string Id { get; }
        string Name { get; }
        LayerBuilder.LayerType Type { get; }
        LayerState State { get; }

        Task InitializeAsync(Dictionary<string, object> parameters);
        Task<Tensor> ForwardAsync(Tensor input);
        Task<Tensor> BackwardAsync(Tensor gradient);
        Task UpdateWeightsAsync(Tensor gradients, Optimizer optimizer);
        Task<long> GetParameterCountAsync();
        Task<long> EstimateMemoryUsageAsync();
        Task<double> EstimateComputeComplexityAsync();
        Task<LayerStatistics> GetStatisticsAsync();
    }

    // Destekleyici sınıflar;
    public class Tensor;
    {
        public float[] Data { get; set; }
        public int[] Shape { get; set; }
        public TensorType Type { get; set; }
    }

    public enum TensorType;
    {
        Dense,
        Sparse,
        Compressed;
    }

    public enum LayerState;
    {
        Uninitialized,
        Initialized,
        Trained,
        Frozen,
        Optimized;
    }

    public class LayerStatistics;
    {
        public long ParameterCount { get; set; }
        public long MemoryUsage { get; set; }
        public double ComputeComplexity { get; set; }
        public GradientStatistics GradientStats { get; set; }
        public WeightStatistics WeightStats { get; set; }
        public ActivationStatistics ActivationStats { get; set; }
    }

    public class InputCharacteristics;
    {
        public int[] Shape { get; set; }
        public DataType DataType { get; set; }
        public Range ValueRange { get; set; }
        public Distribution Distribution { get; set; }
        public NoiseLevel Noise { get; set; }
    }

    public enum DataType;
    {
        Float32,
        Float16,
        Int8,
        Int16,
        Int32;
    }

    public class Range;
    {
        public double Min { get; set; }
        public double Max { get; set; }
    }

    public enum Distribution;
    {
        Uniform,
        Normal,
        LogNormal,
        Exponential,
        Custom;
    }

    public enum NoiseLevel;
    {
        None,
        Low,
        Medium,
        High;
    }

    public class OptimizationOptions;
    {
        public LayerBuilder.OptimizationLevel Level { get; set; }
        public bool EnableMemoryOptimization { get; set; } = true;
        public bool EnableComputeOptimization { get; set; } = true;
        public bool EnableNumericalOptimization { get; set; } = true;
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class BuildResult;
    {
        public bool Success { get; set; }
        public TimeSpan BuildTime { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    public class LayerProfile;
    {
        public string LayerId { get; set; }
        public string LayerName { get; set; }
        public LayerBuilder.LayerType LayerType { get; set; }
        public long ParameterCount { get; set; }
        public long MemoryUsage { get; set; }
        public double ComputeComplexity { get; set; }
        public DateTime ProfilingTime { get; set; }
    }

    public class ResourceConstraints;
    {
        public int MaxConcurrentBuilds { get; set; } = Environment.ProcessorCount;
        public long MaxMemoryPerBuild { get; set; } = 100 * 1024 * 1024; // 100 MB;
        public TimeSpan MaxBuildTime { get; set; } = TimeSpan.FromMinutes(5);
    }

    public class ProgressReportingOptions;
    {
        public bool EnableProgress { get; set; } = true;
        public TimeSpan ReportInterval { get; set; } = TimeSpan.FromSeconds(1);
        public Action<BuildProgress> ProgressCallback { get; set; }
    }

    public class BuildProgress;
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string CurrentOperation { get; set; }
        public double Percentage => Total > 0 ? (double)Current / Total * 100 : 0;
    }
}
