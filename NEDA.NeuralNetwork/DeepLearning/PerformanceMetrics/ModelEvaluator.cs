using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using NEDA.Core.Engine;
using NEDA.Core.Logging;
using NEDA.ExceptionHandling;
using NEDA.NeuralNetwork.DeepLearning.NeuralModels;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;

namespace NEDA.NeuralNetwork.DeepLearning.PerformanceMetrics;
{
    /// <summary>
    /// Gelişmiş model değerlendirme ve performans analizi motoru;
    /// Çoklu metrik, karşılaştırmalı analiz ve öneri sistemi;
    /// </summary>
    public class ModelEvaluator : IModelEvaluator, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly INeuralNetwork _neuralNetwork;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IKnowledgeBase _knowledgeBase;
        private readonly IMemorySystem _memorySystem;

        private readonly MetricsCalculator _metricsCalculator;
        private readonly ValidationEngine _validationEngine;
        private readonly BiasDetector _biasDetector;
        private readonly RobustnessTester _robustnessTester;
        private readonly ExplainabilityAnalyzer _explainabilityAnalyzer;
        private readonly ComparativeAnalyzer _comparativeAnalyzer;
        private readonly ReportGenerator _reportGenerator;

        private readonly EvaluationCache _evaluationCache;
        private readonly BenchmarkRepository _benchmarkRepository;
        private readonly DriftDetector _driftDetector;
        private readonly PerformanceTracker _performanceTracker;

        private readonly ConcurrentDictionary<string, EvaluationSession> _activeSessions;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly object _lockObject = new object();
        private bool _disposed = false;

        /// <summary>
        /// ModelEvaluator constructor - Dependency Injection ile bağımlılıklar;
        /// </summary>
        public ModelEvaluator(
            ILogger logger,
            INeuralNetwork neuralNetwork,
            IPerformanceMonitor performanceMonitor,
            IKnowledgeBase knowledgeBase,
            IMemorySystem memorySystem)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _neuralNetwork = neuralNetwork ?? throw new ArgumentNullException(nameof(neuralNetwork));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _knowledgeBase = knowledgeBase ?? throw new ArgumentNullException(nameof(knowledgeBase));
            _memorySystem = memorySystem ?? throw new ArgumentNullException(nameof(memorySystem));

            _metricsCalculator = new MetricsCalculator(logger);
            _validationEngine = new ValidationEngine(logger);
            _biasDetector = new BiasDetector(logger, knowledgeBase);
            _robustnessTester = new RobustnessTester(logger);
            _explainabilityAnalyzer = new ExplainabilityAnalyzer(logger);
            _comparativeAnalyzer = new ComparativeAnalyzer(logger);
            _reportGenerator = new ReportGenerator(logger);

            _evaluationCache = new EvaluationCache();
            _benchmarkRepository = new BenchmarkRepository(logger);
            _driftDetector = new DriftDetector(logger);
            _performanceTracker = new PerformanceTracker(logger, performanceMonitor);

            _activeSessions = new ConcurrentDictionary<string, EvaluationSession>();
            _cancellationTokenSource = new CancellationTokenSource();

            InitializeModelEvaluator();
        }

        /// <summary>
        /// Modeli kapsamlı bir şekilde değerlendir;
        /// </summary>
        public async Task<ComprehensiveEvaluationResult> EvaluateComprehensivelyAsync(
            ModelEvaluationRequest request)
        {
            var session = CreateEvaluationSession(request);
            _activeSessions[session.Id] = session;

            try
            {
                _logger.LogInformation($"Starting comprehensive evaluation for model: {request.ModelId}");
                session.Status = EvaluationStatus.Running;

                // 1. Temel performans değerlendirmesi;
                var basicEvaluation = await EvaluateBasicPerformanceAsync(
                    request.ModelId,
                    request.TestData,
                    request.Metrics);

                // 2. Doğrulama ve cross-validation;
                var validationResults = await PerformValidationAsync(
                    request.ModelId,
                    request.ValidationData,
                    request.ValidationConfig);

                // 3. Bias ve fairness analizi;
                var biasAnalysis = await AnalyzeBiasAndFairnessAsync(
                    request.ModelId,
                    request.TestData,
                    request.FairnessCriteria);

                // 4. Robustness testi;
                var robustnessResults = await TestRobustnessAsync(
                    request.ModelId,
                    request.TestData,
                    request.RobustnessTests);

                // 5. Explainability analizi;
                var explainabilityResults = await AnalyzeExplainabilityAsync(
                    request.ModelId,
                    request.TestData,
                    request.ExplainabilityMetrics);

                // 6. Drift tespiti;
                var driftAnalysis = await DetectDriftAsync(
                    request.ModelId,
                    request.TrainingData,
                    request.TestData);

                // 7. Karşılaştırmalı analiz (benchmark)
                var comparativeAnalysis = await PerformComparativeAnalysisAsync(
                    request.ModelId,
                    request.BenchmarkModels);

                // 8. Performans özeti;
                var performanceSummary = await GeneratePerformanceSummaryAsync(
                    basicEvaluation,
                    validationResults,
                    robustnessResults);

                // 9. Tavsiyeler ve iyileştirme önerileri;
                var recommendations = await GenerateRecommendationsAsync(
                    basicEvaluation,
                    biasAnalysis,
                    robustnessResults,
                    comparativeAnalysis);

                var result = new ComprehensiveEvaluationResult;
                {
                    SessionId = session.Id,
                    ModelId = request.ModelId,
                    BasicEvaluation = basicEvaluation,
                    ValidationResults = validationResults,
                    BiasAnalysis = biasAnalysis,
                    RobustnessResults = robustnessResults,
                    ExplainabilityResults = explainabilityResults,
                    DriftAnalysis = driftAnalysis,
                    ComparativeAnalysis = comparativeAnalysis,
                    PerformanceSummary = performanceSummary,
                    Recommendations = recommendations,
                    OverallScore = CalculateOverallScore(
                        basicEvaluation,
                        validationResults,
                        robustnessResults,
                        biasAnalysis),
                    EvaluationTime = DateTime.UtcNow - session.StartTime,
                    IsProductionReady = IsModelProductionReady(
                        basicEvaluation,
                        biasAnalysis,
                        robustnessResults)
                };

                session.Status = EvaluationStatus.Completed;
                session.Result = result;
                session.EndTime = DateTime.UtcNow;

                // Sonuçları önbelleğe al;
                _evaluationCache.Add(request.ModelId, result);

                // Benchmark veritabanına kaydet;
                await _benchmarkRepository.SaveEvaluationAsync(result);

                // Performans izleme;
                _performanceTracker.TrackEvaluation(result);

                return result;
            }
            catch (Exception ex)
            {
                session.Status = EvaluationStatus.Failed;
                session.Error = ex;
                _logger.LogError(ex, $"Comprehensive evaluation failed for model: {request.ModelId}");
                throw new ModelEvaluationException(
                    ErrorCodes.ModelEvaluation.EvaluationFailed,
                    $"Comprehensive evaluation failed for model: {request.ModelId}",
                    ex);
            }
            finally
            {
                _activeSessions.TryRemove(session.Id, out _);
            }
        }

        /// <summary>
        /// Modeli gerçek zamanlı olarak değerlendir (streaming data)
        /// </summary>
        public async Task<RealtimeEvaluationResult> EvaluateRealtimeAsync(
            RealtimeEvaluationRequest request)
        {
            var session = CreateRealtimeSession(request);
            _activeSessions[session.Id] = session;

            try
            {
                _logger.LogInformation($"Starting realtime evaluation for model: {request.ModelId}");

                var results = new List<RealtimeMetric>();
                var driftAlerts = new List<DriftAlert>();
                var performanceAlerts = new List<PerformanceAlert>();

                // Gerçek zamanlı veri akışını işle;
                await foreach (var dataPoint in request.DataStream.WithCancellation(_cancellationTokenSource.Token))
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        break;

                    // Model tahmini;
                    var prediction = await _neuralNetwork.ForwardAsync(
                        new NetworkInput { Data = dataPoint.Features });

                    // Gerçek değerle karşılaştır;
                    var metric = CalculateRealtimeMetric(
                        prediction,
                        dataPoint.ActualValue,
                        request.Metrics);

                    results.Add(metric);

                    // Drift tespiti;
                    if (results.Count >= request.DriftDetectionWindow)
                    {
                        var recentResults = results.TakeLast(request.DriftDetectionWindow).ToList();
                        var driftResult = await _driftDetector.DetectRealtimeDriftAsync(recentResults);

                        if (driftResult.IsDriftDetected)
                        {
                            var alert = new DriftAlert;
                            {
                                Timestamp = DateTime.UtcNow,
                                DriftScore = driftResult.DriftScore,
                                AffectedFeatures = driftResult.AffectedFeatures,
                                Severity = driftResult.Severity;
                            };
                            driftAlerts.Add(alert);

                            // Alert callback'i çağır;
                            if (request.OnDriftDetected != null)
                            {
                                await request.OnDriftDetected(alert);
                            }
                        }
                    }

                    // Performans düşüşü tespiti;
                    if (results.Count >= request.PerformanceWindow)
                    {
                        var recentPerformance = results;
                            .TakeLast(request.PerformanceWindow)
                            .Average(r => r.Value);

                        if (recentPerformance < request.PerformanceThreshold)
                        {
                            var alert = new PerformanceAlert;
                            {
                                Timestamp = DateTime.UtcNow,
                                CurrentPerformance = recentPerformance,
                                Threshold = request.PerformanceThreshold,
                                WindowSize = request.PerformanceWindow;
                            };
                            performanceAlerts.Add(alert);

                            // Alert callback'i çağır;
                            if (request.OnPerformanceAlert != null)
                            {
                                await request.OnPerformanceAlert(alert);
                            }
                        }
                    }

                    // Belirli aralıklarla ara sonuçları kaydet;
                    if (results.Count % request.CheckpointInterval == 0)
                    {
                        await SaveRealtimeCheckpointAsync(session, results, driftAlerts, performanceAlerts);
                    }
                }

                var result = new RealtimeEvaluationResult;
                {
                    SessionId = session.Id,
                    ModelId = request.ModelId,
                    TotalDataPoints = results.Count,
                    AverageMetrics = CalculateAverageMetrics(results),
                    DriftAlerts = driftAlerts,
                    PerformanceAlerts = performanceAlerts,
                    StartTime = session.StartTime,
                    EndTime = DateTime.UtcNow,
                    StreamingDuration = DateTime.UtcNow - session.StartTime,
                    DataThroughput = CalculateDataThroughput(results.Count, session.StartTime)
                };

                session.Status = EvaluationStatus.Completed;
                session.Result = result;

                return result;
            }
            catch (Exception ex)
            {
                session.Status = EvaluationStatus.Failed;
                session.Error = ex;
                _logger.LogError(ex, $"Realtime evaluation failed for model: {request.ModelId}");
                throw;
            }
            finally
            {
                _activeSessions.TryRemove(session.Id, out _);
            }
        }

        /// <summary>
        /// Model A/B testi yap;
        /// </summary>
        public async Task<ABTestResult> PerformABTestAsync(ABTestRequest request)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation($"Starting A/B test for models: {request.ModelAId} vs {request.ModelBId}");

                var tasks = new List<Task<ModelEvaluationResult>>
                {
                    EvaluateModelAsync(request.ModelAId, request.TestData, request.Metrics),
                    EvaluateModelAsync(request.ModelBId, request.TestData, request.Metrics)
                };

                var results = await Task.WhenAll(tasks);
                var modelAResult = results[0];
                var modelBResult = results[1];

                // İstatistiksel anlamlılık testi;
                var statisticalTest = await PerformStatisticalTestAsync(
                    modelAResult,
                    modelBResult,
                    request.SignificanceLevel);

                // Karar verme;
                var decision = MakeABTestDecision(
                    modelAResult,
                    modelBResult,
                    statisticalTest,
                    request.DecisionCriteria);

                // Güven aralıkları;
                var confidenceIntervals = CalculateConfidenceIntervals(
                    modelAResult,
                    modelBResult,
                    request.ConfidenceLevel);

                var result = new ABTestResult;
                {
                    TestId = Guid.NewGuid().ToString(),
                    ModelAResult = modelAResult,
                    ModelBResult = modelBResult,
                    StatisticalTest = statisticalTest,
                    Decision = decision,
                    ConfidenceIntervals = confidenceIntervals,
                    TestDuration = stopwatch.Elapsed,
                    IsStatisticallySignificant = statisticalTest.PValue < request.SignificanceLevel,
                    EffectSize = CalculateEffectSize(modelAResult, modelBResult),
                    Recommendations = GenerateABTestRecommendations(decision, statisticalTest)
                };

                // Test sonuçlarını kaydet;
                await SaveABTestResultAsync(result);

                return result;
            }
            finally
            {
                stopwatch.Stop();
            }
        }

        /// <summary>
        /// Modeli belirli bir metrik üzerinden değerlendir;
        /// </summary>
        public async Task<MetricEvaluationResult> EvaluateMetricAsync(
            string modelId,
            EvaluationData data,
            MetricDefinition metric)
        {
            try
            {
                _logger.LogDebug($"Evaluating metric {metric.Name} for model: {modelId}");

                // Cache kontrolü;
                var cacheKey = $"metric_{modelId}_{metric.Name}_{data.GetHashCode()}";
                if (_evaluationCache.TryGet(cacheKey, out MetricEvaluationResult cachedResult))
                {
                    return cachedResult;
                }

                // Model tahminlerini al;
                var predictions = await GetModelPredictionsAsync(modelId, data);

                // Metriği hesapla;
                var metricValue = await _metricsCalculator.CalculateMetricAsync(
                    metric,
                    predictions,
                    data);

                // Referans değerlerle karşılaştır;
                var benchmarkValue = await GetBenchmarkValueAsync(metric.Name, data.ProblemType);
                var percentile = await CalculatePercentileAsync(metricValue, metric.Name, data.ProblemType);

                var result = new MetricEvaluationResult;
                {
                    ModelId = modelId,
                    Metric = metric,
                    Value = metricValue,
                    BenchmarkValue = benchmarkValue,
                    Percentile = percentile,
                    IsAboveBenchmark = metricValue > benchmarkValue,
                    ConfidenceInterval = CalculateMetricConfidenceInterval(metricValue, predictions.Count),
                    Interpretation = InterpretMetricValue(metric, metricValue)
                };

                // Cache'e ekle;
                _evaluationCache.Add(cacheKey, result);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Metric evaluation failed for model {modelId}, metric {metric.Name}");
                throw;
            }
        }

        /// <summary>
        /// Model explainability raporu oluştur;
        /// </summary>
        public async Task<ExplainabilityReport> GenerateExplainabilityReportAsync(
            ExplainabilityRequest request)
        {
            try
            {
                _logger.LogInformation($"Generating explainability report for model: {request.ModelId}");

                // Feature importance;
                var featureImportance = await CalculateFeatureImportanceAsync(
                    request.ModelId,
                    request.SampleData);

                // SHAP values;
                var shapValues = await CalculateSHAPValuesAsync(
                    request.ModelId,
                    request.SampleData);

                // LIME explanations;
                var limeExplanations = await GenerateLIMEExplanationsAsync(
                    request.ModelId,
                    request.SampleData);

                // Decision boundary analysis;
                var decisionBoundary = await AnalyzeDecisionBoundaryAsync(
                    request.ModelId,
                    request.SampleData);

                // Counterfactual explanations;
                var counterfactuals = await GenerateCounterfactualsAsync(
                    request.ModelId,
                    request.SampleData,
                    request.TargetClass);

                var report = new ExplainabilityReport;
                {
                    ModelId = request.ModelId,
                    FeatureImportance = featureImportance,
                    SHAPValues = shapValues,
                    LIMEExplanations = limeExplanations,
                    DecisionBoundaryAnalysis = decisionBoundary,
                    CounterfactualExplanations = counterfactuals,
                    OverallExplainabilityScore = CalculateExplainabilityScore(
                        featureImportance,
                        shapValues,
                        limeExplanations),
                    InterpretabilityLevel = DetermineInterpretabilityLevel(
                        featureImportance,
                        shapValues),
                    Recommendations = GenerateExplainabilityRecommendations(
                        featureImportance,
                        shapValues)
                };

                // Raporu kaydet;
                await SaveExplainabilityReportAsync(report, request.OutputPath);

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Explainability report generation failed for model: {request.ModelId}");
                throw;
            }
        }

        /// <summary>
        /// Model benchmark karşılaştırması yap;
        /// </summary>
        public async Task<BenchmarkComparisonResult> CompareWithBenchmarkAsync(
            BenchmarkComparisonRequest request)
        {
            try
            {
                _logger.LogInformation($"Comparing model {request.ModelId} with benchmarks");

                // Model performansını al;
                var modelPerformance = await EvaluateModelAsync(
                    request.ModelId,
                    request.TestData,
                    request.Metrics);

                // Benchmark performanslarını al;
                var benchmarkPerformances = await GetBenchmarkPerformancesAsync(
                    request.BenchmarkNames,
                    request.TestData,
                    request.Metrics);

                // Karşılaştırmalı analiz;
                var comparison = await _comparativeAnalyzer.CompareAsync(
                    modelPerformance,
                    benchmarkPerformances);

                // Sıralama;
                var ranking = await RankModelsAsync(
                    modelPerformance,
                    benchmarkPerformances,
                    request.RankingCriteria);

                // İyileştirme alanları;
                var improvementAreas = IdentifyImprovementAreas(
                    modelPerformance,
                    benchmarkPerformances);

                var result = new BenchmarkComparisonResult;
                {
                    ModelId = request.ModelId,
                    ModelPerformance = modelPerformance,
                    BenchmarkPerformances = benchmarkPerformances,
                    Comparison = comparison,
                    Ranking = ranking,
                    ImprovementAreas = improvementAreas,
                    IsStateOfTheArt = ranking.Position == 1,
                    GapToBest = CalculateGapToBest(modelPerformance, benchmarkPerformances),
                    Recommendations = GenerateBenchmarkRecommendations(
                        modelPerformance,
                        benchmarkPerformances,
                        improvementAreas)
                };

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Benchmark comparison failed for model: {request.ModelId}");
                throw;
            }
        }

        /// <summary>
        /// Model drift raporu oluştur;
        /// </summary>
        public async Task<DriftReport> GenerateDriftReportAsync(DriftDetectionRequest request)
        {
            try
            {
                _logger.LogInformation($"Generating drift report for model: {request.ModelId}");

                // Data drift tespiti;
                var dataDriftResult = await _driftDetector.DetectDataDriftAsync(
                    request.TrainingData,
                    request.ProductionData,
                    request.DriftConfig);

                // Concept drift tespiti;
                var conceptDriftResult = await _driftDetector.DetectConceptDriftAsync(
                    request.ModelId,
                    request.TrainingData,
                    request.ProductionData,
                    request.DriftConfig);

                // Feature drift tespiti;
                var featureDriftResult = await _driftDetector.DetectFeatureDriftAsync(
                    request.TrainingData,
                    request.ProductionData);

                // Drift etkisi analizi;
                var impactAnalysis = await AnalyzeDriftImpactAsync(
                    request.ModelId,
                    dataDriftResult,
                    conceptDriftResult,
                    featureDriftResult);

                // Mitigation önerileri;
                var mitigationStrategies = await GenerateDriftMitigationStrategiesAsync(
                    dataDriftResult,
                    conceptDriftResult,
                    featureDriftResult);

                var report = new DriftReport;
                {
                    ModelId = request.ModelId,
                    DataDriftResult = dataDriftResult,
                    ConceptDriftResult = conceptDriftResult,
                    FeatureDriftResult = featureDriftResult,
                    ImpactAnalysis = impactAnalysis,
                    MitigationStrategies = mitigationStrategies,
                    OverallDriftScore = CalculateOverallDriftScore(
                        dataDriftResult,
                        conceptDriftResult,
                        featureDriftResult),
                    RequiresRetraining = ShouldRetrain(
                        dataDriftResult,
                        conceptDriftResult,
                        featureDriftResult,
                        request.DriftThreshold),
                    AlertLevel = DetermineDriftAlertLevel(
                        dataDriftResult,
                        conceptDriftResult,
                        featureDriftResult)
                };

                // Drift uyarısı gönder;
                if (report.AlertLevel >= DriftAlertLevel.Warning)
                {
                    await SendDriftAlertAsync(report, request.AlertRecipients);
                }

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Drift report generation failed for model: {request.ModelId}");
                throw;
            }
        }

        /// <summary>
        /// Model değerlendirme oturumu oluştur;
        /// </summary>
        private EvaluationSession CreateEvaluationSession(ModelEvaluationRequest request)
        {
            return new EvaluationSession;
            {
                Id = Guid.NewGuid().ToString(),
                Request = request,
                StartTime = DateTime.UtcNow,
                Status = EvaluationStatus.Created,
                SessionData = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Gerçek zamanlı değerlendirme oturumu oluştur;
        /// </summary>
        private RealtimeEvaluationSession CreateRealtimeSession(RealtimeEvaluationRequest request)
        {
            return new RealtimeEvaluationSession;
            {
                Id = Guid.NewGuid().ToString(),
                Request = request,
                StartTime = DateTime.UtcNow,
                Status = EvaluationStatus.Created,
                DataPointsProcessed = 0,
                SessionData = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// ModelEvaluator'ı başlat;
        /// </summary>
        private void InitializeModelEvaluator()
        {
            try
            {
                lock (_lockObject)
                {
                    _logger.LogInformation("Initializing ModelEvaluator...");

                    // Benchmark veritabanını yükle;
                    _benchmarkRepository.Initialize();

                    // Cache'i temizle;
                    _evaluationCache.ClearExpired();

                    // Metrik hesaplayıcıyı başlat;
                    _metricsCalculator.Initialize();

                    // Drift dedektörü başlat;
                    _driftDetector.Initialize();

                    _logger.LogInformation("ModelEvaluator initialized successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ModelEvaluator");
                throw new ModelEvaluationException(
                    ErrorCodes.ModelEvaluation.InitializationFailed,
                    "Failed to initialize ModelEvaluator",
                    ex);
            }
        }

        /// <summary>
        /// Dispose pattern implementation;
        /// </summary>
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
                    // İptal token'ını tetikle;
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();

                    // Yönetilen kaynakları serbest bırak;
                    _evaluationCache.Dispose();
                    _benchmarkRepository.Dispose();
                    _driftDetector.Dispose();
                    _performanceTracker.Dispose();

                    // Aktif oturumları temizle;
                    foreach (var session in _activeSessions.Values)
                    {
                        if (session.Status == EvaluationStatus.Running)
                        {
                            session.Status = EvaluationStatus.Cancelled;
                        }
                    }
                    _activeSessions.Clear();
                }

                _disposed = true;
            }
        }

        ~ModelEvaluator()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// IModelEvaluator arayüzü;
    /// </summary>
    public interface IModelEvaluator : IDisposable
    {
        Task<ComprehensiveEvaluationResult> EvaluateComprehensivelyAsync(ModelEvaluationRequest request);
        Task<RealtimeEvaluationResult> EvaluateRealtimeAsync(RealtimeEvaluationRequest request);
        Task<ABTestResult> PerformABTestAsync(ABTestRequest request);
        Task<MetricEvaluationResult> EvaluateMetricAsync(string modelId, EvaluationData data, MetricDefinition metric);
        Task<ExplainabilityReport> GenerateExplainabilityReportAsync(ExplainabilityRequest request);
        Task<BenchmarkComparisonResult> CompareWithBenchmarkAsync(BenchmarkComparisonRequest request);
        Task<DriftReport> GenerateDriftReportAsync(DriftDetectionRequest request);
    }

    /// <summary>
    /// Model değerlendirme isteği;
    /// </summary>
    public class ModelEvaluationRequest;
    {
        public string ModelId { get; set; }
        public EvaluationData TestData { get; set; }
        public EvaluationData ValidationData { get; set; }
        public EvaluationData TrainingData { get; set; }
        public List<MetricDefinition> Metrics { get; set; }
        public ValidationConfiguration ValidationConfig { get; set; }
        public FairnessCriteria FairnessCriteria { get; set; }
        public RobustnessTestConfig RobustnessTests { get; set; }
        public ExplainabilityMetrics ExplainabilityMetrics { get; set; }
        public List<string> BenchmarkModels { get; set; }
        public Dictionary<string, object> Parameters { get; set; }

        public ModelEvaluationRequest()
        {
            Metrics = new List<MetricDefinition>();
            BenchmarkModels = new List<string>();
            Parameters = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Kapsamlı değerlendirme sonucu;
    /// </summary>
    public class ComprehensiveEvaluationResult;
    {
        public string SessionId { get; set; }
        public string ModelId { get; set; }
        public ModelEvaluationResult BasicEvaluation { get; set; }
        public ValidationResult ValidationResults { get; set; }
        public BiasAnalysisResult BiasAnalysis { get; set; }
        public RobustnessTestResult RobustnessResults { get; set; }
        public ExplainabilityResult ExplainabilityResults { get; set; }
        public DriftAnalysisResult DriftAnalysis { get; set; }
        public ComparativeAnalysisResult ComparativeAnalysis { get; set; }
        public PerformanceSummary PerformanceSummary { get; set; }
        public List<Recommendation> Recommendations { get; set; }
        public double OverallScore { get; set; }
        public TimeSpan EvaluationTime { get; set; }
        public bool IsProductionReady { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public ComprehensiveEvaluationResult()
        {
            Recommendations = new List<Recommendation>();
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Model değerlendirme oturumu;
    /// </summary>
    public class EvaluationSession;
    {
        public string Id { get; set; }
        public ModelEvaluationRequest Request { get; set; }
        public object Result { get; set; }
        public EvaluationStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public Exception Error { get; set; }
        public Dictionary<string, object> SessionData { get; set; }

        public EvaluationSession()
        {
            SessionData = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Gerçek zamanlı değerlendirme oturumu;
    /// </summary>
    public class RealtimeEvaluationSession : EvaluationSession;
    {
        public int DataPointsProcessed { get; set; }
        public List<RealtimeMetric> CurrentMetrics { get; set; }
        public List<DriftAlert> CurrentDriftAlerts { get; set; }

        public RealtimeEvaluationSession()
        {
            CurrentMetrics = new List<RealtimeMetric>();
            CurrentDriftAlerts = new List<DriftAlert>();
        }
    }

    /// <summary>
    /// Model değerlendirme sonucu;
    /// </summary>
    public class ModelEvaluationResult;
    {
        public string ModelId { get; set; }
        public Dictionary<string, double> Metrics { get; set; }
        public ConfusionMatrix ConfusionMatrix { get; set; }
        public ROCCurve ROCCurve { get; set; }
        public PrecisionRecallCurve PrecisionRecallCurve { get; set; }
        public List<PredictionError> PredictionErrors { get; set; }
        public DateTime EvaluationTime { get; set; }
        public EvaluationData EvaluationData { get; set; }
        public Dictionary<string, object> AdditionalResults { get; set; }

        public ModelEvaluationResult()
        {
            Metrics = new Dictionary<string, double>();
            PredictionErrors = new List<PredictionError>();
            AdditionalResults = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Metrik tanımı;
    /// </summary>
    public class MetricDefinition;
    {
        public string Name { get; set; }
        public MetricType Type { get; set; }
        public double? Threshold { get; set; }
        public double? TargetValue { get; set; }
        public bool HigherIsBetter { get; set; }
        public Dictionary<string, object> Parameters { get; set; }

        public MetricDefinition()
        {
            Parameters = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Enum'lar ve tipler;
    /// </summary>
    public enum EvaluationStatus;
    {
        Created,
        Running,
        Completed,
        Failed,
        Cancelled;
    }

    public enum MetricType;
    {
        Accuracy,
        Precision,
        Recall,
        F1Score,
        AUC,
        MAE,
        MSE,
        RMSE,
        R2,
        MAPE,
        LogLoss,
        Custom;
    }

    public enum DriftAlertLevel;
    {
        Info,
        Warning,
        Critical,
        Emergency;
    }

    public enum ModelInterpretabilityLevel;
    {
        Low,
        Medium,
        High,
        VeryHigh;
    }

    /// <summary>
    /// Özel exception sınıfları;
    /// </summary>
    public class ModelEvaluationException : Exception
    {
        public string ErrorCode { get; }

        public ModelEvaluationException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public ModelEvaluationException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}

// Not: Yardımcı sınıflar (MetricsCalculator, ValidationEngine, BiasDetector, RobustnessTester, 
// ExplainabilityAnalyzer, ComparativeAnalyzer, ReportGenerator, EvaluationCache, BenchmarkRepository,
// DriftDetector, PerformanceTracker) ayrı dosyalarda implemente edilmelidir.
// Bu dosya ana ModelEvaluator sınıfını içerir.
