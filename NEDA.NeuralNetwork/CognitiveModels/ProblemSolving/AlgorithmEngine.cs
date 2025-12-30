using NEDA.Brain.KnowledgeBase;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.NeuralNetwork.DeepLearning;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.NeuralNetwork.CognitiveModels.ProblemSolving.AlgorithmEngine;

namespace NEDA.NeuralNetwork.CognitiveModels.ProblemSolving;
{
    /// <summary>
    /// Algoritma seçimi, optimizasyonu ve yürütülmesi için gelişmiş algoritma motoru;
    /// </summary>
    public class AlgorithmEngine : IAlgorithmEngine, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly INeuralNetwork _neuralNetwork;
        private readonly IPerformanceMetrics _performanceMetrics;

        private readonly ConcurrentDictionary<string, AlgorithmInstance> _runningAlgorithms;
        private readonly ConcurrentDictionary<string, AlgorithmPerformance> _algorithmPerformanceCache;
        private readonly List<AlgorithmRegistryEntry> _registeredAlgorithms;

        private readonly SemaphoreSlim _algorithmSelectionLock;
        private readonly Timer _monitoringTimer;
        private readonly object _disposeLock = new object();
        private bool _disposed = false;
        private int _nextAlgorithmId = 1;

        /// <summary>
        /// Algoritma yürütme seçenekleri;
        /// </summary>
        public class AlgorithmExecutionOptions;
        {
            public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
            public int MaxConcurrentAlgorithms { get; set; } = 10;
            public bool EnablePerformanceMonitoring { get; set; } = true;
            public bool EnableAutoOptimization { get; set; } = true;
            public double MinimumConfidenceThreshold { get; set; } = 0.7;
            public AlgorithmSelectionStrategy SelectionStrategy { get; set; } = AlgorithmSelectionStrategy.Hybrid;
            public ResourceConstraints ResourceConstraints { get; set; } = new ResourceConstraints();
            public bool EnableFallbackStrategies { get; set; } = true;
            public bool CollectDetailedMetrics { get; set; } = true;
        }

        /// <summary>
        /// Kaynak kısıtlamaları;
        /// </summary>
        public class ResourceConstraints;
        {
            public long MaxMemoryBytes { get; set; } = 1024 * 1024 * 1024; // 1 GB;
            public TimeSpan MaxExecutionTime { get; set; } = TimeSpan.FromHours(1);
            public int MaxThreads { get; set; } = Environment.ProcessorCount;
            public double MaxCpuPercentage { get; set; } = 80.0;
        }

        /// <summary>
        /// Algoritma seçim stratejisi;
        /// </summary>
        public enum AlgorithmSelectionStrategy;
        {
            PerformanceBased,
            SimilarityBased,
            Hybrid,
            NeuralNetwork,
            RuleBased,
            Experimental;
        }

        /// <summary>
        /// Algoritma karmaşıklığı;
        /// </summary>
        public enum AlgorithmComplexity;
        {
            Constant,
            Logarithmic,
            Linear,
            Linearithmic,
            Quadratic,
            Cubic,
            Exponential,
            Factorial;
        }

        /// <summary>
        /// Algoritma motorunu başlatır;
        /// </summary>
        public AlgorithmEngine(
            ILogger logger,
            IKnowledgeGraph knowledgeGraph,
            INeuralNetwork neuralNetwork,
            IPerformanceMetrics performanceMetrics)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _neuralNetwork = neuralNetwork ?? throw new ArgumentNullException(nameof(neuralNetwork));
            _performanceMetrics = performanceMetrics ?? throw new ArgumentNullException(nameof(performanceMetrics));

            _runningAlgorithms = new ConcurrentDictionary<string, AlgorithmInstance>();
            _algorithmPerformanceCache = new ConcurrentDictionary<string, AlgorithmPerformance>();
            _registeredAlgorithms = new List<AlgorithmRegistryEntry>();
            _algorithmSelectionLock = new SemaphoreSlim(1, 1);

            // İzleme timer'ını başlat;
            _monitoringTimer = new Timer(MonitorRunningAlgorithms, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _logger.LogInformation("AlgorithmEngine initialized", GetType().Name);
        }

        /// <summary>
        /// Algoritma motorunu kayıtlı algoritmalarla başlatır;
        /// </summary>
        public async Task InitializeAsync(IEnumerable<IAlgorithm> algorithms = null)
        {
            try
            {
                _logger.LogInformation("Initializing AlgorithmEngine", GetType().Name);

                // Dahili algoritmaları kaydet;
                RegisterBuiltInAlgorithms();

                // Harici algoritmaları kaydet;
                if (algorithms != null)
                {
                    foreach (var algorithm in algorithms)
                    {
                        await RegisterAlgorithmAsync(algorithm);
                    }
                }

                // Performans verilerini yükle;
                await LoadPerformanceHistoryAsync();

                // Sinir ağını eğit;
                await TrainAlgorithmSelectionModelAsync();

                _logger.LogInformation($"AlgorithmEngine initialized with {_registeredAlgorithms.Count} algorithms",
                    GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError($"AlgorithmEngine initialization failed: {ex.Message}",
                    GetType().Name, ex);
                throw new AlgorithmEngineException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Problem için en uygun algoritmayı seçer ve yürütür;
        /// </summary>
        public async Task<AlgorithmResult> SolveProblemAsync(
            ProblemDefinition problem,
            AlgorithmExecutionOptions options = null)
        {
            if (problem == null)
                throw new ArgumentNullException(nameof(problem));

            options = options ?? new AlgorithmExecutionOptions();
            string algorithmId = null;
            IAlgorithm selectedAlgorithm = null;
            CancellationTokenSource cts = null;

            try
            {
                _logger.LogInformation($"Solving problem: {problem.Id} - {problem.Description}",
                    GetType().Name);

                // Eşzamanlı algoritma sınırını kontrol et;
                await WaitForAvailableSlotAsync(options);

                // En uygun algoritmayı seç;
                selectedAlgorithm = await SelectOptimalAlgorithmAsync(problem, options);
                if (selectedAlgorithm == null)
                    throw new AlgorithmNotFoundException("No suitable algorithm found for the problem");

                algorithmId = GenerateAlgorithmId(selectedAlgorithm);
                cts = new CancellationTokenSource(options.Timeout);

                // Algoritma örneği oluştur;
                var instance = new AlgorithmInstance;
                {
                    Id = algorithmId,
                    Algorithm = selectedAlgorithm,
                    Problem = problem,
                    StartTime = DateTime.UtcNow,
                    Status = AlgorithmStatus.Running,
                    CancellationTokenSource = cts;
                };

                if (!_runningAlgorithms.TryAdd(algorithmId, instance))
                    throw new AlgorithmEngineException($"Failed to start algorithm {algorithmId}");

                // Performans izlemeyi başlat;
                var performanceMonitor = options.EnablePerformanceMonitoring ?
                    StartPerformanceMonitoring(algorithmId) : null;

                _logger.LogDebug($"Starting algorithm {selectedAlgorithm.Name} for problem {problem.Id}",
                    GetType().Name);

                // Algoritmayı yürüt;
                AlgorithmResult result;
                try
                {
                    result = await ExecuteAlgorithmWithRetryAsync(
                        selectedAlgorithm, problem, cts.Token, options);
                }
                catch (OperationCanceledException)
                {
                    throw new AlgorithmTimeoutException($"Algorithm timed out after {options.Timeout}");
                }

                // Sonuçları işle;
                result.AlgorithmId = algorithmId;
                result.AlgorithmName = selectedAlgorithm.Name;
                result.ExecutionTime = DateTime.UtcNow - instance.StartTime;

                // Performans verilerini güncelle;
                if (options.CollectDetailedMetrics && performanceMonitor != null)
                {
                    result.PerformanceMetrics = await performanceMonitor.GetMetricsAsync();
                    await UpdateAlgorithmPerformanceAsync(selectedAlgorithm, problem, result);
                }

                // Fallback stratejilerini kontrol et;
                if (!result.IsSuccessful && options.EnableFallbackStrategies)
                {
                    result = await TryFallbackStrategiesAsync(problem, result, options);
                }

                // Otomatik optimizasyon;
                if (options.EnableAutoOptimization && result.IsSuccessful)
                {
                    await OptimizeAlgorithmParametersAsync(selectedAlgorithm, problem, result);
                }

                return result;
            }
            catch (Exception ex) when (!(ex is AlgorithmEngineException))
            {
                _logger.LogError($"Problem solving failed: {ex.Message}", GetType().Name, ex);
                throw new AlgorithmEngineException($"Problem solving failed: {ex.Message}", ex);
            }
            finally
            {
                if (algorithmId != null)
                {
                    _runningAlgorithms.TryRemove(algorithmId, out _);
                }
                cts?.Dispose();
            }
        }

        /// <summary>
        /// Paralel problem çözümü için algoritma havuzu oluşturur;
        /// </summary>
        public async Task<ParallelAlgorithmResult> SolveProblemsInParallelAsync(
            IEnumerable<ProblemDefinition> problems,
            ParallelAlgorithmOptions parallelOptions,
            AlgorithmExecutionOptions algorithmOptions = null)
        {
            if (problems == null)
                throw new ArgumentNullException(nameof(problems));

            algorithmOptions = algorithmOptions ?? new AlgorithmExecutionOptions();

            try
            {
                _logger.LogInformation($"Solving {problems.Count()} problems in parallel", GetType().Name);

                var results = new ConcurrentBag<AlgorithmResult>();
                var exceptions = new ConcurrentQueue<Exception>();

                var parallelOptionsInternal = new ParallelOptions;
                {
                    MaxDegreeOfParallelism = Math.Min(
                        algorithmOptions.ResourceConstraints.MaxThreads,
                        parallelOptions.MaxDegreeOfParallelism),
                    CancellationToken = parallelOptions.CancellationToken;
                };

                await Parallel.ForEachAsync(
                    problems,
                    parallelOptionsInternal,
                    async (problem, cancellationToken) =>
                    {
                        try
                        {
                            var result = await SolveProblemAsync(problem, algorithmOptions);
                            results.Add(result);
                        }
                        catch (Exception ex)
                        {
                            exceptions.Enqueue(ex);
                            _logger.LogWarning($"Parallel problem solving failed: {ex.Message}",
                                GetType().Name);
                        }
                    });

                var combinedResult = new ParallelAlgorithmResult;
                {
                    IndividualResults = results.ToList(),
                    Exceptions = exceptions.ToList(),
                    TotalProblems = problems.Count(),
                    SuccessfulSolutions = results.Count(r => r.IsSuccessful),
                    FailedSolutions = results.Count(r => !r.IsSuccessful),
                    TotalExecutionTime = results.Sum(r => r.ExecutionTime.TotalSeconds)
                };

                // Toplu optimizasyon;
                if (algorithmOptions.EnableAutoOptimization)
                {
                    await OptimizeBasedOnParallelResultsAsync(combinedResult);
                }

                return combinedResult;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Parallel problem solving failed: {ex.Message}", GetType().Name, ex);
                throw new AlgorithmEngineException("Parallel solving failed", ex);
            }
        }

        /// <summary>
        /// Yeni algoritma kaydeder;
        /// </summary>
        public async Task RegisterAlgorithmAsync(IAlgorithm algorithm)
        {
            if (algorithm == null)
                throw new ArgumentNullException(nameof(algorithm));

            try
            {
                await _algorithmSelectionLock.WaitAsync();

                // Algoritma zaten kayıtlı mı kontrol et;
                if (_registeredAlgorithms.Any(a => a.Algorithm.Id == algorithm.Id))
                {
                    _logger.LogWarning($"Algorithm {algorithm.Name} already registered", GetType().Name);
                    return;
                }

                var entry = new AlgorithmRegistryEntry
                {
                    Algorithm = algorithm,
                    RegistrationTime = DateTime.UtcNow,
                    UsageCount = 0,
                    AveragePerformance = new AlgorithmPerformance()
                };

                _registeredAlgorithms.Add(entry);

                // Algoritma yeteneklerini analiz et;
                await AnalyzeAlgorithmCapabilitiesAsync(algorithm);

                // Performans modelini güncelle;
                await UpdateSelectionModelWithNewAlgorithmAsync(algorithm);

                _logger.LogInformation($"Algorithm registered: {algorithm.Name} ({algorithm.Id})",
                    GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Algorithm registration failed: {ex.Message}", GetType().Name, ex);
                throw new AlgorithmRegistrationException($"Failed to register algorithm: {ex.Message}", ex);
            }
            finally
            {
                _algorithmSelectionLock.Release();
            }
        }

        /// <summary>
        /// Algoritma parametrelerini optimize eder;
        /// </summary>
        public async Task<AlgorithmOptimizationResult> OptimizeAlgorithmAsync(
            string algorithmId,
            OptimizationConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(algorithmId))
                throw new ArgumentException("Algorithm ID is required", nameof(algorithmId));

            try
            {
                _logger.LogInformation($"Optimizing algorithm {algorithmId}", GetType().Name);

                var algorithm = GetAlgorithmById(algorithmId);
                if (algorithm == null)
                    throw new AlgorithmNotFoundException($"Algorithm {algorithmId} not found");

                var optimizer = CreateOptimizerForAlgorithm(algorithm, config);
                var optimizationResult = await optimizer.OptimizeAsync();

                // Optimize edilmiş parametreleri uygula;
                if (optimizationResult.Success)
                {
                    await ApplyOptimizedParametersAsync(algorithm, optimizationResult.OptimalParameters);

                    // Performans önbelleğini güncelle;
                    await ClearAlgorithmPerformanceCacheAsync(algorithmId);

                    _logger.LogInformation($"Algorithm {algorithmId} optimized successfully. " +
                                          $"Improvement: {optimizationResult.ImprovementPercentage:P2}",
                                          GetType().Name);
                }

                return optimizationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Algorithm optimization failed: {ex.Message}", GetType().Name, ex);
                throw new AlgorithmOptimizationException($"Optimization failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Algoritma performans analizi yapar;
        /// </summary>
        public async Task<AlgorithmAnalysis> AnalyzeAlgorithmPerformanceAsync(
            string algorithmId,
            TimeSpan? timeRange = null)
        {
            if (string.IsNullOrWhiteSpace(algorithmId))
                throw new ArgumentException("Algorithm ID is required", nameof(algorithmId));

            try
            {
                _logger.LogDebug($"Analyzing performance for algorithm {algorithmId}", GetType().Name);

                var algorithm = GetAlgorithmById(algorithmId);
                if (algorithm == null)
                    throw new AlgorithmNotFoundException($"Algorithm {algorithmId} not found");

                // Performans verilerini topla;
                var performanceData = await CollectPerformanceDataAsync(algorithmId, timeRange);

                // İstatistiksel analiz yap;
                var statisticalAnalysis = PerformStatisticalAnalysis(performanceData);

                // Zaman serisi analizi;
                var timeSeriesAnalysis = AnalyzeTimeSeries(performanceData);

                // Öneriler oluştur;
                var recommendations = GenerateOptimizationRecommendations(
                    statisticalAnalysis, timeSeriesAnalysis);

                var analysis = new AlgorithmAnalysis;
                {
                    AlgorithmId = algorithmId,
                    AlgorithmName = algorithm.Name,
                    TimeRange = timeRange ?? TimeSpan.FromDays(30),
                    StatisticalAnalysis = statisticalAnalysis,
                    TimeSeriesAnalysis = timeSeriesAnalysis,
                    Recommendations = recommendations,
                    OverallScore = CalculateOverallPerformanceScore(statisticalAnalysis),
                    GeneratedAt = DateTime.UtcNow;
                };

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Performance analysis failed: {ex.Message}", GetType().Name, ex);
                throw new AlgorithmAnalysisException($"Performance analysis failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Özel algoritma sınıfı oluşturur;
        /// </summary>
        public async Task<IAlgorithm> CreateCustomAlgorithmAsync(
            AlgorithmSpecification specification,
            TrainingData trainingData = null)
        {
            if (specification == null)
                throw new ArgumentNullException(nameof(specification));

            try
            {
                _logger.LogInformation($"Creating custom algorithm: {specification.Name}", GetType().Name);

                // Spesifikasyona göre algoritma oluştur;
                IAlgorithm algorithm;

                switch (specification.Type)
                {
                    case AlgorithmType.MachineLearning:
                        algorithm = await CreateMachineLearningAlgorithmAsync(specification, trainingData);
                        break;

                    case AlgorithmType.Heuristic:
                        algorithm = await CreateHeuristicAlgorithmAsync(specification);
                        break;

                    case AlgorithmType.Metaheuristic:
                        algorithm = await CreateMetaheuristicAlgorithmAsync(specification);
                        break;

                    case AlgorithmType.Hybrid:
                        algorithm = await CreateHybridAlgorithmAsync(specification, trainingData);
                        break;

                    default:
                        throw new ArgumentException($"Unsupported algorithm type: {specification.Type}");
                }

                // Algoritmayı kaydet;
                await RegisterAlgorithmAsync(algorithm);

                // Başlangıç eğitimi yap;
                if (trainingData != null)
                {
                    await TrainAlgorithmAsync(algorithm, trainingData);
                }

                _logger.LogInformation($"Custom algorithm created: {algorithm.Name}", GetType().Name);

                return algorithm;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Custom algorithm creation failed: {ex.Message}", GetType().Name, ex);
                throw new AlgorithmCreationException($"Failed to create custom algorithm: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Çalışan algoritmaları izler;
        /// </summary>
        public IEnumerable<AlgorithmInstanceInfo> GetRunningAlgorithms()
        {
            return _runningAlgorithms.Values.Select(instance => new AlgorithmInstanceInfo;
            {
                Id = instance.Id,
                AlgorithmName = instance.Algorithm?.Name,
                ProblemId = instance.Problem?.Id,
                StartTime = instance.StartTime,
                Status = instance.Status,
                ExecutionTime = DateTime.UtcNow - instance.StartTime;
            }).ToList();
        }

        /// <summary>
        /// Kayıtlı algoritmaları listeler;
        /// </summary>
        public IEnumerable<AlgorithmInfo> GetRegisteredAlgorithms()
        {
            return _registeredAlgorithms.Select(entry => new AlgorithmInfo;
            {
                Id = entry.Algorithm.Id,
                Name = entry.Algorithm.Name,
                Type = entry.Algorithm.Type,
                Complexity = entry.Algorithm.Complexity,
                UsageCount = entry.UsageCount,
                AveragePerformance = entry.AveragePerformance,
                RegistrationTime = entry.RegistrationTime;
            }).ToList();
        }

        /// <summary>
        /// Algoritmayı durdurur;
        /// </summary>
        public bool StopAlgorithm(string algorithmInstanceId)
        {
            if (string.IsNullOrWhiteSpace(algorithmInstanceId))
                throw new ArgumentException("Algorithm instance ID is required", nameof(algorithmInstanceId));

            if (_runningAlgorithms.TryGetValue(algorithmInstanceId, out var instance))
            {
                try
                {
                    instance.CancellationTokenSource?.Cancel();
                    instance.Status = AlgorithmStatus.Cancelled;

                    _logger.LogInformation($"Algorithm stopped: {algorithmInstanceId}", GetType().Name);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to stop algorithm {algorithmInstanceId}: {ex.Message}",
                        GetType().Name);
                }
            }

            return false;
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
                        _monitoringTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        _monitoringTimer?.Dispose();

                        // Çalışan tüm algoritmaları durdur;
                        foreach (var instanceId in _runningAlgorithms.Keys.ToList())
                        {
                            StopAlgorithm(instanceId);
                        }

                        _algorithmSelectionLock?.Dispose();

                        // Önbelleği temizle;
                        _algorithmPerformanceCache.Clear();
                        _registeredAlgorithms.Clear();

                        _logger.LogInformation("AlgorithmEngine disposed", GetType().Name);
                    }

                    _disposed = true;
                }
            }
        }

        #region Private Implementation Methods;

        private void RegisterBuiltInAlgorithms()
        {
            // Temel arama algoritmaları;
            RegisterBuiltInAlgorithm(new LinearSearchAlgorithm());
            RegisterBuiltInAlgorithm(new BinarySearchAlgorithm());
            RegisterBuiltInAlgorithm(new DepthFirstSearchAlgorithm());
            RegisterBuiltInAlgorithm(new BreadthFirstSearchAlgorithm());

            // Sıralama algoritmaları;
            RegisterBuiltInAlgorithm(new QuickSortAlgorithm());
            RegisterBuiltInAlgorithm(new MergeSortAlgorithm());
            RegisterBuiltInAlgorithm(new HeapSortAlgorithm());

            // Optimizasyon algoritmaları;
            RegisterBuiltInAlgorithm(new GradientDescentAlgorithm());
            RegisterBuiltInAlgorithm(new GeneticAlgorithm());
            RegisterBuiltInAlgorithm(new SimulatedAnnealingAlgorithm());

            // Makine öğrenmesi algoritmaları;
            RegisterBuiltInAlgorithm(new DecisionTreeAlgorithm());
            RegisterBuiltInAlgorithm(new RandomForestAlgorithm());
            RegisterBuiltInAlgorithm(new NeuralNetworkAlgorithm(_neuralNetwork));

            _logger.LogDebug($"Registered {_registeredAlgorithms.Count} built-in algorithms", GetType().Name);
        }

        private void RegisterBuiltInAlgorithm(IAlgorithm algorithm)
        {
            var entry = new AlgorithmRegistryEntry
            {
                Algorithm = algorithm,
                RegistrationTime = DateTime.UtcNow,
                UsageCount = 0,
                AveragePerformance = new AlgorithmPerformance()
            };

            _registeredAlgorithms.Add(entry);
        }

        private async Task<IAlgorithm> SelectOptimalAlgorithmAsync(
            ProblemDefinition problem,
            AlgorithmExecutionOptions options)
        {
            await _algorithmSelectionLock.WaitAsync();

            try
            {
                // Uygun algoritmaları filtrele;
                var suitableAlgorithms = _registeredAlgorithms;
                    .Where(entry => entry.Algorithm.CanSolve(problem))
                    .ToList();

                if (!suitableAlgorithms.Any())
                {
                    _logger.LogWarning($"No algorithms can solve problem: {problem.Id}", GetType().Name);
                    return null;
                }

                // Seçim stratejisine göre algoritma seç;
                IAlgorithm selectedAlgorithm;

                switch (options.SelectionStrategy)
                {
                    case AlgorithmSelectionStrategy.PerformanceBased:
                        selectedAlgorithm = SelectByPerformance(suitableAlgorithms, problem);
                        break;

                    case AlgorithmSelectionStrategy.SimilarityBased:
                        selectedAlgorithm = await SelectBySimilarityAsync(suitableAlgorithms, problem);
                        break;

                    case AlgorithmSelectionStrategy.NeuralNetwork:
                        selectedAlgorithm = await SelectByNeuralNetworkAsync(suitableAlgorithms, problem);
                        break;

                    case AlgorithmSelectionStrategy.RuleBased:
                        selectedAlgorithm = SelectByRules(suitableAlgorithms, problem);
                        break;

                    case AlgorithmSelectionStrategy.Hybrid:
                    default:
                        selectedAlgorithm = await SelectHybridAsync(suitableAlgorithms, problem, options);
                        break;
                }

                if (selectedAlgorithm == null && suitableAlgorithms.Any())
                {
                    // Varsayılan olarak en çok kullanılan algoritmayı seç;
                    selectedAlgorithm = suitableAlgorithms;
                        .OrderByDescending(a => a.UsageCount)
                        .First()
                        .Algorithm;
                }

                // Kullanım sayısını güncelle;
                var entry = _registeredAlgorithms.FirstOrDefault(e => e.Algorithm == selectedAlgorithm);
                if (entry != null)
                {
                    entry.UsageCount++;
                }

                _logger.LogDebug($"Selected algorithm: {selectedAlgorithm?.Name} for problem {problem.Id}",
                    GetType().Name);

                return selectedAlgorithm;
            }
            finally
            {
                _algorithmSelectionLock.Release();
            }
        }

        private IAlgorithm SelectByPerformance(List<AlgorithmRegistryEntry> algorithms, ProblemDefinition problem)
        {
            // Performans önbelleğinden en iyi algoritmayı bul;
            var scoredAlgorithms = new List<(IAlgorithm Algorithm, double Score)>();

            foreach (var entry in algorithms)
            {
                var cacheKey = $"{entry.Algorithm.Id}_{problem.Type}";
                if (_algorithmPerformanceCache.TryGetValue(cacheKey, out var performance))
                {
                    double score = CalculatePerformanceScore(performance, problem);
                    scoredAlgorithms.Add((entry.Algorithm, score));
                }
            }

            return scoredAlgorithms;
                .OrderByDescending(x => x.Score)
                .FirstOrDefault()
                .Algorithm;
        }

        private async Task<IAlgorithm> SelectBySimilarityAsync(
            List<AlgorithmRegistryEntry> algorithms,
            ProblemDefinition problem)
        {
            // Benzer problemlerde başarılı olan algoritmaları bul;
            var similarProblems = await _knowledgeGraph.FindSimilarProblemsAsync(problem, 10);

            var algorithmScores = new Dictionary<IAlgorithm, double>();

            foreach (var similarProblem in similarProblems)
            {
                var successfulAlgorithms = await _knowledgeGraph.GetSuccessfulAlgorithmsAsync(similarProblem.Id);

                foreach (var algorithm in algorithms.Select(a => a.Algorithm))
                {
                    if (successfulAlgorithms.Contains(algorithm.Id))
                    {
                        if (!algorithmScores.ContainsKey(algorithm))
                            algorithmScores[algorithm] = 0;

                        algorithmScores[algorithm] += similarProblem.SimilarityScore;
                    }
                }
            }

            return algorithmScores;
                .OrderByDescending(kvp => kvp.Value)
                .FirstOrDefault()
                .Key;
        }

        private async Task<IAlgorithm> SelectByNeuralNetworkAsync(
            List<AlgorithmRegistryEntry> algorithms,
            ProblemDefinition problem)
        {
            // Problem özelliklerini vektöre dönüştür;
            var problemFeatures = await ExtractProblemFeaturesAsync(problem);

            // Sinir ağından tahmin al;
            var predictions = await _neuralNetwork.PredictAsync(problemFeatures);

            // En yüksek skorlu algoritmayı seç;
            var algorithmScores = new Dictionary<IAlgorithm, double>();

            foreach (var entry in algorithms)
            {
                var algorithmFeatures = ExtractAlgorithmFeatures(entry.Algorithm);
                var compatibilityScore = CalculateCompatibilityScore(problemFeatures, algorithmFeatures);
                algorithmScores[entry.Algorithm] = compatibilityScore * 0.7 + predictions[entry.Algorithm.Id] * 0.3;
            }

            return algorithmScores;
                .OrderByDescending(kvp => kvp.Value)
                .FirstOrDefault()
                .Key;
        }

        private async Task<AlgorithmResult> ExecuteAlgorithmWithRetryAsync(
            IAlgorithm algorithm,
            ProblemDefinition problem,
            CancellationToken cancellationToken,
            AlgorithmExecutionOptions options)
        {
            int retryCount = 0;
            const int maxRetries = 3;

            while (retryCount < maxRetries)
            {
                try
                {
                    return await algorithm.ExecuteAsync(problem, cancellationToken);
                }
                catch (Exception ex) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    _logger.LogWarning($"Algorithm execution failed, retry {retryCount}/{maxRetries}: {ex.Message}",
                        GetType().Name);

                    // Bekle ve yeniden dene;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)), cancellationToken);

                    // Parametreleri ayarla;
                    AdjustAlgorithmParameters(algorithm, retryCount);
                }
            }

            throw new AlgorithmExecutionException($"Algorithm failed after {maxRetries} retries");
        }

        private async Task<AlgorithmResult> TryFallbackStrategiesAsync(
            ProblemDefinition problem,
            AlgorithmResult originalResult,
            AlgorithmExecutionOptions options)
        {
            _logger.LogInformation($"Trying fallback strategies for problem {problem.Id}", GetType().Name);

            // Alternatif algoritmaları dene;
            var alternativeAlgorithms = _registeredAlgorithms;
                .Where(a => a.Algorithm.CanSolve(problem) &&
                           a.Algorithm.Id != originalResult.AlgorithmId)
                .OrderByDescending(a => a.AveragePerformance.SuccessRate)
                .Take(3)
                .ToList();

            foreach (var entry in alternativeAlgorithms)
            {
                try
                {
                    var result = await entry.Algorithm.ExecuteAsync(problem, CancellationToken.None);
                    if (result.IsSuccessful)
                    {
                        _logger.LogInformation($"Fallback algorithm {entry.Algorithm.Name} succeeded", GetType().Name);

                        // Orijinal sonucu fallback sonucuyla güncelle;
                        originalResult = result;
                        originalResult.IsFallbackResult = true;
                        originalResult.FallbackAlgorithmId = entry.Algorithm.Id;
                        originalResult.FallbackAlgorithmName = entry.Algorithm.Name;

                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Fallback algorithm {entry.Algorithm.Name} failed: {ex.Message}",
                        GetType().Name);
                }
            }

            return originalResult;
        }

        private async Task UpdateAlgorithmPerformanceAsync(
            IAlgorithm algorithm,
            ProblemDefinition problem,
            AlgorithmResult result)
        {
            var cacheKey = $"{algorithm.Id}_{problem.Type}";
            var performance = new AlgorithmPerformance;
            {
                AlgorithmId = algorithm.Id,
                ProblemType = problem.Type,
                SuccessCount = result.IsSuccessful ? 1 : 0,
                FailureCount = result.IsSuccessful ? 0 : 1,
                TotalExecutionTime = result.ExecutionTime,
                AverageExecutionTime = result.ExecutionTime,
                MemoryUsage = result.PerformanceMetrics?.MemoryUsage ?? 0,
                LastExecutionTime = DateTime.UtcNow;
            };

            if (_algorithmPerformanceCache.TryGetValue(cacheKey, out var existingPerformance))
            {
                // Mevcut performansı güncelle;
                performance.SuccessCount += existingPerformance.SuccessCount;
                performance.FailureCount += existingPerformance.FailureCount;
                performance.TotalExecutionTime += existingPerformance.TotalExecutionTime;
                performance.AverageExecutionTime = TimeSpan.FromTicks(
                    performance.TotalExecutionTime.Ticks / (performance.SuccessCount + performance.FailureCount));
            }

            _algorithmPerformanceCache.AddOrUpdate(cacheKey, performance, (key, old) => performance);

            // Bilgi grafiğini güncelle;
            await _knowledgeGraph.RecordAlgorithmPerformanceAsync(algorithm, problem, result);
        }

        private async Task OptimizeAlgorithmParametersAsync(
            IAlgorithm algorithm,
            ProblemDefinition problem,
            AlgorithmResult result)
        {
            if (algorithm is IOptimizableAlgorithm optimizable)
            {
                try
                {
                    var optimizationResult = await optimizable.OptimizeParametersAsync(problem, result);
                    if (optimizationResult.Improved)
                    {
                        _logger.LogDebug($"Algorithm {algorithm.Name} parameters optimized", GetType().Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Parameter optimization failed for {algorithm.Name}: {ex.Message}",
                        GetType().Name);
                }
            }
        }

        private async Task TrainAlgorithmSelectionModelAsync()
        {
            try
            {
                var trainingData = await CollectTrainingDataAsync();
                if (trainingData.Any())
                {
                    await _neuralNetwork.TrainAsync(trainingData);
                    _logger.LogDebug("Algorithm selection model trained", GetType().Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Selection model training failed: {ex.Message}", GetType().Name);
            }
        }

        private async Task WaitForAvailableSlotAsync(AlgorithmExecutionOptions options)
        {
            while (_runningAlgorithms.Count >= options.MaxConcurrentAlgorithms)
            {
                _logger.LogDebug($"Waiting for algorithm slot. Current: {_runningAlgorithms.Count}/" +
                               $"{options.MaxConcurrentAlgorithms}", GetType().Name);

                await Task.Delay(TimeSpan.FromSeconds(1));

                // Süresi dolmuş algoritmaları temizle;
                CleanupExpiredAlgorithms();
            }
        }

        private void MonitorRunningAlgorithms(object state)
        {
            try
            {
                CleanupExpiredAlgorithms();
                CheckResourceConstraints();
                LogRunningAlgorithmsStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Algorithm monitoring failed: {ex.Message}", GetType().Name, ex);
            }
        }

        private void CleanupExpiredAlgorithms()
        {
            var expiredAlgorithms = _runningAlgorithms.Values;
                .Where(instance => DateTime.UtcNow - instance.StartTime > TimeSpan.FromHours(1))
                .ToList();

            foreach (var instance in expiredAlgorithms)
            {
                if (_runningAlgorithms.TryRemove(instance.Id, out _))
                {
                    instance.CancellationTokenSource?.Cancel();
                    _logger.LogWarning($"Removed expired algorithm: {instance.Id}", GetType().Name);
                }
            }
        }

        private string GenerateAlgorithmId(IAlgorithm algorithm)
        {
            return $"{algorithm.Id}_{Interlocked.Increment(ref _nextAlgorithmId)}";
        }

        #endregion;

        #region Helper Classes and Interfaces;

        /// <summary>
        /// Algoritma arayüzü;
        /// </summary>
        public interface IAlgorithm;
        {
            string Id { get; }
            string Name { get; }
            string Description { get; }
            AlgorithmType Type { get; }
            AlgorithmComplexity Complexity { get; }
            Task<AlgorithmResult> ExecuteAsync(ProblemDefinition problem, CancellationToken cancellationToken);
            bool CanSolve(ProblemDefinition problem);
            IEnumerable<AlgorithmParameter> GetParameters();
        }

        /// <summary>
        /// Optimize edilebilir algoritma arayüzü;
        /// </summary>
        public interface IOptimizableAlgorithm : IAlgorithm;
        {
            Task<ParameterOptimizationResult> OptimizeParametersAsync(
                ProblemDefinition problem,
                AlgorithmResult previousResult);
        }

        /// <summary>
        /// Problem tanımı;
        /// </summary>
        public class ProblemDefinition;
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Description { get; set; }
            public string Type { get; set; }
            public ProblemCategory Category { get; set; }
            public Dictionary<string, object> InputData { get; set; } = new Dictionary<string, object>();
            public Dictionary<string, object> Constraints { get; set; } = new Dictionary<string, object>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Algoritma sonucu;
        /// </summary>
        public class AlgorithmResult;
        {
            public string AlgorithmId { get; set; }
            public string AlgorithmName { get; set; }
            public string ProblemId { get; set; }
            public bool IsSuccessful { get; set; }
            public object Solution { get; set; }
            public TimeSpan ExecutionTime { get; set; }
            public string ErrorMessage { get; set; }
            public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
            public AlgorithmPerformanceMetrics PerformanceMetrics { get; set; }
            public bool IsFallbackResult { get; set; }
            public string FallbackAlgorithmId { get; set; }
            public string FallbackAlgorithmName { get; set; }
            public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Paralel algoritma sonucu;
        /// </summary>
        public class ParallelAlgorithmResult;
        {
            public List<AlgorithmResult> IndividualResults { get; set; } = new List<AlgorithmResult>();
            public List<Exception> Exceptions { get; set; } = new List<Exception>();
            public int TotalProblems { get; set; }
            public int SuccessfulSolutions { get; set; }
            public int FailedSolutions { get; set; }
            public double TotalExecutionTime { get; set; }
            public Dictionary<string, object> AggregateMetrics { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Algoritma performansı;
        /// </summary>
        public class AlgorithmPerformance;
        {
            public string AlgorithmId { get; set; }
            public string ProblemType { get; set; }
            public int SuccessCount { get; set; }
            public int FailureCount { get; set; }
            public TimeSpan TotalExecutionTime { get; set; }
            public TimeSpan AverageExecutionTime { get; set; }
            public double SuccessRate => SuccessCount + FailureCount > 0 ?
                (double)SuccessCount / (SuccessCount + FailureCount) : 0;
            public long MemoryUsage { get; set; }
            public DateTime LastExecutionTime { get; set; }
        }

        /// <summary>
        /// Algoritma örneği;
        /// </summary>
        private class AlgorithmInstance;
        {
            public string Id { get; set; }
            public IAlgorithm Algorithm { get; set; }
            public ProblemDefinition Problem { get; set; }
            public DateTime StartTime { get; set; }
            public AlgorithmStatus Status { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; set; }
        }

        /// <summary>
        /// Algoritma kayıt girdisi;
        /// </summary>
        private class AlgorithmRegistryEntry
        {
            public IAlgorithm Algorithm { get; set; }
            public DateTime RegistrationTime { get; set; }
            public int UsageCount { get; set; }
            public AlgorithmPerformance AveragePerformance { get; set; }
        }

        /// <summary>
        /// Algoritma durumu;
        /// </summary>
        public enum AlgorithmStatus;
        {
            Running,
            Completed,
            Failed,
            Cancelled,
            Timeout;
        }

        /// <summary>
        /// Algoritma tipi;
        /// </summary>
        public enum AlgorithmType;
        {
            Search,
            Sort,
            Optimization,
            MachineLearning,
            Heuristic,
            Metaheuristic,
            Hybrid,
            Custom;
        }

        /// <summary>
        /// Problem kategorisi;
        /// </summary>
        public enum ProblemCategory;
        {
            Classification,
            Regression,
            Clustering,
            Optimization,
            Search,
            Sorting,
            Planning,
            Prediction,
            Generation,
            Analysis;
        }

        /// <summary>
        /// Paralel algoritma seçenekleri;
        /// </summary>
        public class ParallelAlgorithmOptions;
        {
            public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
            public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
            public bool ContinueOnError { get; set; } = true;
            public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Algoritma spesifikasyonu;
        /// </summary>
        public class AlgorithmSpecification;
        {
            public string Name { get; set; }
            public AlgorithmType Type { get; set; }
            public string Description { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public List<string> Capabilities { get; set; } = new List<string>();
            public OptimizationGoals OptimizationGoals { get; set; } = new OptimizationGoals();
        }

        /// <summary>
        /// Optimizasyon konfigürasyonu;
        /// </summary>
        public class OptimizationConfiguration;
        {
            public OptimizationMethod Method { get; set; }
            public int MaxIterations { get; set; } = 100;
            public double TargetImprovement { get; set; } = 0.1;
            public ResourceConstraints Constraints { get; set; } = new ResourceConstraints();
            public Dictionary<string, ParameterRange> ParameterRanges { get; set; } = new Dictionary<string, ParameterRange>();
        }

        /// <summary>
        /// Algoritma analizi;
        /// </summary>
        public class AlgorithmAnalysis;
        {
            public string AlgorithmId { get; set; }
            public string AlgorithmName { get; set; }
            public TimeSpan TimeRange { get; set; }
            public StatisticalAnalysis StatisticalAnalysis { get; set; }
            public TimeSeriesAnalysis TimeSeriesAnalysis { get; set; }
            public List<OptimizationRecommendation> Recommendations { get; set; }
            public double OverallScore { get; set; }
            public DateTime GeneratedAt { get; set; }
        }

        /// <summary>
        /// Algoritma optimizasyon sonucu;
        /// </summary>
        public class AlgorithmOptimizationResult;
        {
            public bool Success { get; set; }
            public double ImprovementPercentage { get; set; }
            public Dictionary<string, object> OptimalParameters { get; set; }
            public AlgorithmPerformance BeforeOptimization { get; set; }
            public AlgorithmPerformance AfterOptimization { get; set; }
            public TimeSpan OptimizationTime { get; set; }
        }

        #endregion;

        #region Built-in Algorithm Implementations (Örnek)

        private class LinearSearchAlgorithm : IAlgorithm;
        {
            public string Id => "linear_search";
            public string Name => "Linear Search";
            public string Description => "Sequentially checks each element until a match is found";
            public AlgorithmType Type => AlgorithmType.Search;
            public AlgorithmComplexity Complexity => AlgorithmComplexity.Linear;

            public Task<AlgorithmResult> ExecuteAsync(ProblemDefinition problem, CancellationToken cancellationToken)
            {
                // Implementasyon;
                return Task.FromResult(new AlgorithmResult { IsSuccessful = true });
            }

            public bool CanSolve(ProblemDefinition problem)
            {
                return problem.Type == "search" || problem.Type == "find";
            }

            public IEnumerable<AlgorithmParameter> GetParameters()
            {
                yield return new AlgorithmParameter("sorted", typeof(bool), false);
            }
        }

        // Diğer built-in algoritma sınıfları...
        private class BinarySearchAlgorithm : IAlgorithm { /* Implementasyon */ }
        private class QuickSortAlgorithm : IAlgorithm { /* Implementasyon */ }
        private class GeneticAlgorithm : IAlgorithm, IOptimizableAlgorithm { /* Implementasyon */ }
        private class NeuralNetworkAlgorithm : IAlgorithm { /* Implementasyon */ }

        #endregion;

        #region Exceptions;

        public class AlgorithmEngineException : Exception
        {
            public AlgorithmEngineException(string message) : base(message) { }
            public AlgorithmEngineException(string message, Exception inner) : base(message, inner) { }
        }

        public class AlgorithmNotFoundException : AlgorithmEngineException;
        {
            public AlgorithmNotFoundException(string message) : base(message) { }
        }

        public class AlgorithmTimeoutException : AlgorithmEngineException;
        {
            public AlgorithmTimeoutException(string message) : base(message) { }
        }

        public class AlgorithmExecutionException : AlgorithmEngineException;
        {
            public AlgorithmExecutionException(string message) : base(message) { }
        }

        public class AlgorithmRegistrationException : AlgorithmEngineException;
        {
            public AlgorithmRegistrationException(string message) : base(message) { }
            public AlgorithmRegistrationException(string message, Exception inner) : base(message, inner) { }
        }

        public class AlgorithmOptimizationException : AlgorithmEngineException;
        {
            public AlgorithmOptimizationException(string message) : base(message) { }
            public AlgorithmOptimizationException(string message, Exception inner) : base(message, inner) { }
        }

        public class AlgorithmAnalysisException : AlgorithmEngineException;
        {
            public AlgorithmAnalysisException(string message) : base(message) { }
            public AlgorithmAnalysisException(string message, Exception inner) : base(message, inner) { }
        }

        public class AlgorithmCreationException : AlgorithmEngineException;
        {
            public AlgorithmCreationException(string message) : base(message) { }
            public AlgorithmCreationException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }

    /// <summary>
    /// Algoritma motoru arayüzü;
    /// </summary>
    public interface IAlgorithmEngine : IDisposable
    {
        Task InitializeAsync(IEnumerable<IAlgorithm> algorithms = null);
        Task<AlgorithmEngine.AlgorithmResult> SolveProblemAsync(
            AlgorithmEngine.ProblemDefinition problem,
            AlgorithmEngine.AlgorithmExecutionOptions options = null);

        Task<AlgorithmEngine.ParallelAlgorithmResult> SolveProblemsInParallelAsync(
            IEnumerable<AlgorithmEngine.ProblemDefinition> problems,
            AlgorithmEngine.ParallelAlgorithmOptions parallelOptions,
            AlgorithmEngine.AlgorithmExecutionOptions algorithmOptions = null);

        Task RegisterAlgorithmAsync(IAlgorithm algorithm);
        Task<AlgorithmEngine.AlgorithmOptimizationResult> OptimizeAlgorithmAsync(
            string algorithmId,
            AlgorithmEngine.OptimizationConfiguration config);

        Task<AlgorithmEngine.AlgorithmAnalysis> AnalyzeAlgorithmPerformanceAsync(
            string algorithmId,
            TimeSpan? timeRange = null);

        Task<IAlgorithm> CreateCustomAlgorithmAsync(
            AlgorithmEngine.AlgorithmSpecification specification,
            AlgorithmEngine.TrainingData trainingData = null);

        IEnumerable<AlgorithmEngine.AlgorithmInstanceInfo> GetRunningAlgorithms();
        IEnumerable<AlgorithmEngine.AlgorithmInfo> GetRegisteredAlgorithms();
        bool StopAlgorithm(string algorithmInstanceId);
    }

    // Destekleyici sınıflar ve enum'lar;
    public class AlgorithmParameter;
    {
        public string Name { get; }
        public Type Type { get; }
        public object DefaultValue { get; }

        public AlgorithmParameter(string name, Type type, object defaultValue)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
        }
    }

    public class AlgorithmPerformanceMetrics;
    {
        public long MemoryUsage { get; set; }
        public double CpuUsage { get; set; }
        public int ThreadCount { get; set; }
        public TimeSpan GcTime { get; set; }
        public Dictionary<string, double> CustomMetrics { get; set; } = new Dictionary<string, double>();
    }

    public class ParameterOptimizationResult;
    {
        public bool Improved { get; set; }
        public double ImprovementPercentage { get; set; }
        public Dictionary<string, object> NewParameters { get; set; }
        public TimeSpan OptimizationTime { get; set; }
    }

    public enum OptimizationMethod;
    {
        GridSearch,
        RandomSearch,
        BayesianOptimization,
        GradientBased,
        Evolutionary,
        Hybrid;
    }

    public class ParameterRange;
    {
        public object Min { get; set; }
        public object Max { get; set; }
        public object Step { get; set; }
    }

    public class OptimizationGoals;
    {
        public bool MinimizeTime { get; set; } = true;
        public bool MinimizeMemory { get; set; } = true;
        public bool MaximizeAccuracy { get; set; } = true;
        public double TimeWeight { get; set; } = 0.4;
        public double MemoryWeight { get; set; } = 0.3;
        public double AccuracyWeight { get; set; } = 0.3;
    }

    public class StatisticalAnalysis;
    {
        public double MeanExecutionTime { get; set; }
        public double StandardDeviation { get; set; }
        public double SuccessRate { get; set; }
        public double MemoryUsageMean { get; set; }
        public Dictionary<string, double> Percentiles { get; set; } = new Dictionary<string, double>();
    }

    public class TimeSeriesAnalysis;
    {
        public List<DataPoint> ExecutionTimes { get; set; } = new List<DataPoint>();
        public List<DataPoint> SuccessRates { get; set; } = new List<DataPoint>();
        public Trend AnalysisTrend { get; set; }
        public Seasonality SeasonalityPattern { get; set; }
    }

    public class DataPoint;
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
    }

    public enum Trend;
    {
        Improving,
        Declining,
        Stable,
        Volatile;
    }

    public class Seasonality;
    {
        public bool HasSeasonality { get; set; }
        public TimeSpan Period { get; set; }
        public double Strength { get; set; }
    }

    public class OptimizationRecommendation;
    {
        public string Category { get; set; }
        public string Description { get; set; }
        public double ExpectedImprovement { get; set; }
        public string Action { get; set; }
        public PriorityLevel Priority { get; set; }
    }

    public enum PriorityLevel;
    {
        Critical,
        High,
        Medium,
        Low;
    }

    public class AlgorithmInstanceInfo;
    {
        public string Id { get; set; }
        public string AlgorithmName { get; set; }
        public string ProblemId { get; set; }
        public DateTime StartTime { get; set; }
        public AlgorithmEngine.AlgorithmStatus Status { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    public class AlgorithmInfo;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public AlgorithmEngine.AlgorithmType Type { get; set; }
        public AlgorithmEngine.AlgorithmComplexity Complexity { get; set; }
        public int UsageCount { get; set; }
        public AlgorithmEngine.AlgorithmPerformance AveragePerformance { get; set; }
        public DateTime RegistrationTime { get; set; }
    }
}
