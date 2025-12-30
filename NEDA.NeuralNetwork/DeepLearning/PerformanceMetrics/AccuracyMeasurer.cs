using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Common.Utilities;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.NeuralNetwork.PatternRecognition;

namespace NEDA.NeuralNetwork.DeepLearning.PerformanceMetrics;
{
    /// <summary>
    /// Sinir ağı modellerinin doğruluk ölçümleri için gelişmiş ölçüm motoru;
    /// </summary>
    public class AccuracyMeasurer : IAccuracyMeasurer, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly INeuralNetwork _neuralNetwork;
        private readonly IStatisticalAnalyzer _statisticalAnalyzer;
        private readonly IConfidenceCalculator _confidenceCalculator;

        private readonly ConcurrentDictionary<string, AccuracyMeasurementSession> _activeSessions;
        private readonly ConcurrentDictionary<string, ModelAccuracyProfile> _modelProfiles;
        private readonly AccuracyMetricsRepository _metricsRepository;
        private readonly MeasurementValidator _validator;
        private readonly StatisticalEngine _statisticalEngine;

        private readonly SemaphoreSlim _measurementLock;
        private readonly Timer _metricsAggregationTimer;
        private readonly object _disposeLock = new object();
        private bool _disposed = false;
        private int _measurementCounter = 0;

        /// <summary>
        /// Doğruluk ölçümü seçenekleri;
        /// </summary>
        public class AccuracyMeasurementOptions;
        {
            public MeasurementType Type { get; set; } = MeasurementType.Comprehensive;
            public bool EnableCrossValidation { get; set; } = true;
            public int CrossValidationFolds { get; set; } = 5;
            public bool EnableStatisticalAnalysis { get; set; } = true;
            public bool EnableConfidenceIntervals { get; set; } = true;
            public double ConfidenceLevel { get; set; } = 0.95;
            public bool EnableErrorAnalysis { get; set; } = true;
            public bool EnableBiasDetection { get; set; } = true;
            public TimeSpan MeasurementTimeout { get; set; } = TimeSpan.FromMinutes(10);
            public ResourceConstraints ResourceConstraints { get; set; } = new ResourceConstraints();
            public bool EnableRealTimeUpdates { get; set; } = true;
            public DataSamplingStrategy SamplingStrategy { get; set; } = DataSamplingStrategy.Stratified;
        }

        /// <summary>
        /// Ölçüm tipi;
        /// </summary>
        public enum MeasurementType;
        {
            Basic,
            Standard,
            Comprehensive,
            Diagnostic,
            Research;
        }

        /// <summary>
        /// Problem tipi;
        /// </summary>
        public enum ProblemType;
        {
            BinaryClassification,
            MulticlassClassification,
            MultilabelClassification,
            Regression,
            Clustering,
            Ranking,
            Detection,
            Segmentation,
            Generation;
        }

        /// <summary>
        /// Veri örnekleme stratejisi;
        /// </summary>
        public enum DataSamplingStrategy;
        {
            Random,
            Stratified,
            Systematic,
            Clustered,
            Weighted,
            Adaptive;
        }

        /// <summary>
        /// Doğruluk ölçüm motorunu başlatır;
        /// </summary>
        public AccuracyMeasurer(
            ILogger logger,
            INeuralNetwork neuralNetwork,
            IStatisticalAnalyzer statisticalAnalyzer,
            IConfidenceCalculator confidenceCalculator)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _neuralNetwork = neuralNetwork ?? throw new ArgumentNullException(nameof(neuralNetwork));
            _statisticalAnalyzer = statisticalAnalyzer ?? throw new ArgumentNullException(nameof(statisticalAnalyzer));
            _confidenceCalculator = confidenceCalculator ?? throw new ArgumentNullException(nameof(confidenceCalculator));

            _activeSessions = new ConcurrentDictionary<string, AccuracyMeasurementSession>();
            _modelProfiles = new ConcurrentDictionary<string, ModelAccuracyProfile>();
            _metricsRepository = new AccuracyMetricsRepository(_logger);
            _validator = new MeasurementValidator(_logger);
            _statisticalEngine = new StatisticalEngine(_logger, _statisticalAnalyzer);

            _measurementLock = new SemaphoreSlim(1, 1);
            _metricsAggregationTimer = new Timer(AggregateMetrics, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            _logger.LogInformation("AccuracyMeasurer initialized", GetType().Name);
        }

        /// <summary>
        /// Doğruluk ölçüm motorunu başlatır;
        /// </summary>
        public async Task InitializeAsync(IEnumerable<ModelAccuracyProfile> initialProfiles = null)
        {
            try
            {
                _logger.LogInformation("Initializing AccuracyMeasurer", GetType().Name);

                // Metrik deposunu başlat;
                await _metricsRepository.InitializeAsync();

                // Başlangıç profillerini yükle;
                if (initialProfiles != null)
                {
                    foreach (var profile in initialProfiles)
                    {
                        _modelProfiles[profile.ModelId] = profile;
                    }
                }

                // İstatistiksel motoru başlat;
                await _statisticalEngine.InitializeAsync();

                // Validasyon kurallarını yükle;
                await LoadValidationRulesAsync();

                _logger.LogInformation("AccuracyMeasurer initialized successfully", GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError($"AccuracyMeasurer initialization failed: {ex.Message}",
                    GetType().Name, ex);
                throw new AccuracyMeasurementException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Modelin doğruluğunu ölçer;
        /// </summary>
        public async Task<AccuracyMeasurementResult> MeasureAccuracyAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            AccuracyMeasurementOptions options = null)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));
            if (dataset == null)
                throw new ArgumentNullException(nameof(dataset));

            options = options ?? new AccuracyMeasurementOptions();
            string sessionId = GenerateSessionId(model);
            CancellationTokenSource cts = null;

            try
            {
                cts = new CancellationTokenSource(options.MeasurementTimeout);

                _logger.LogInformation($"Measuring accuracy for model: {model.Name}", GetType().Name);

                // Ölçüm oturumu oluştur;
                var session = new AccuracyMeasurementSession;
                {
                    Id = sessionId,
                    ModelId = model.Id,
                    ModelName = model.Name,
                    Dataset = dataset,
                    Options = options,
                    StartTime = DateTime.UtcNow,
                    Status = MeasurementStatus.Running,
                    CancellationTokenSource = cts;
                };

                if (!_activeSessions.TryAdd(sessionId, session))
                    throw new AccuracyMeasurementException($"Failed to create measurement session {sessionId}");

                // Ölçümü gerçekleştir;
                AccuracyMeasurementResult result;
                switch (dataset.ProblemType)
                {
                    case ProblemType.BinaryClassification:
                        result = await MeasureBinaryClassificationAsync(model, dataset, options, cts.Token);
                        break;

                    case ProblemType.MulticlassClassification:
                        result = await MeasureMulticlassClassificationAsync(model, dataset, options, cts.Token);
                        break;

                    case ProblemType.MultilabelClassification:
                        result = await MeasureMultilabelClassificationAsync(model, dataset, options, cts.Token);
                        break;

                    case ProblemType.Regression:
                        result = await MeasureRegressionAsync(model, dataset, options, cts.Token);
                        break;

                    case ProblemType.Detection:
                        result = await MeasureDetectionAsync(model, dataset, options, cts.Token);
                        break;

                    case ProblemType.Segmentation:
                        result = await MeasureSegmentationAsync(model, dataset, options, cts.Token);
                        break;

                    default:
                        result = await MeasureCustomProblemAsync(model, dataset, options, cts.Token);
                        break;
                }

                // Sonuçları işle;
                result.SessionId = sessionId;
                result.MeasurementTime = DateTime.UtcNow - session.StartTime;
                result.Timestamp = DateTime.UtcNow;

                // İstatistiksel analiz;
                if (options.EnableStatisticalAnalysis)
                {
                    result.StatisticalAnalysis = await PerformStatisticalAnalysisAsync(result, dataset, options);
                }

                // Güven aralıkları;
                if (options.EnableConfidenceIntervals)
                {
                    result.ConfidenceIntervals = await CalculateConfidenceIntervalsAsync(result, options);
                }

                // Hata analizi;
                if (options.EnableErrorAnalysis)
                {
                    result.ErrorAnalysis = await AnalyzeErrorsAsync(model, dataset, result, options);
                }

                // Yanlılık tespiti;
                if (options.EnableBiasDetection)
                {
                    result.BiasDetection = await DetectBiasAsync(model, dataset, result, options);
                }

                // Model profilini güncelle;
                await UpdateModelProfileAsync(model, result, options);

                // Metrikleri kaydet;
                await _metricsRepository.RecordMeasurementAsync(result);

                _logger.LogInformation($"Accuracy measurement completed: {result.OverallAccuracy:P2}",
                    GetType().Name);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Accuracy measurement timed out for model: {model.Name}", GetType().Name);
                throw new AccuracyMeasurementTimeoutException($"Measurement timed out after {options.MeasurementTimeout}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Accuracy measurement failed for model {model.Name}: {ex.Message}",
                    GetType().Name, ex);
                throw new AccuracyMeasurementException($"Accuracy measurement failed: {ex.Message}", ex);
            }
            finally
            {
                if (sessionId != null)
                {
                    _activeSessions.TryRemove(sessionId, out _);
                }
                cts?.Dispose();
            }
        }

        /// <summary>
        /// Çapraz doğrulama ile doğruluğu ölçer;
        /// </summary>
        public async Task<CrossValidationResult> MeasureWithCrossValidationAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            CrossValidationOptions cvOptions,
            AccuracyMeasurementOptions options = null)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));
            if (dataset == null)
                throw new ArgumentNullException(nameof(dataset));

            options = options ?? new AccuracyMeasurementOptions();

            try
            {
                _logger.LogInformation($"Cross-validation measurement for model: {model.Name}", GetType().Name);

                var cvResult = new CrossValidationResult;
                {
                    ModelId = model.Id,
                    ModelName = model.Name,
                    DatasetId = dataset.Id,
                    CrossValidationType = cvOptions.Type,
                    NumberOfFolds = cvOptions.NumberOfFolds,
                    StartTime = DateTime.UtcNow;
                };

                // Veriyi fold'lara böl;
                var folds = await SplitDatasetIntoFoldsAsync(dataset, cvOptions);

                var foldResults = new List<AccuracyMeasurementResult>();
                var foldErrors = new List<Exception>();

                // Her fold için ölçüm yap;
                for (int fold = 0; fold < folds.Count; fold++)
                {
                    try
                    {
                        _logger.LogDebug($"Processing fold {fold + 1}/{folds.Count}", GetType().Name);

                        var trainData = folds.Where((_, i) => i != fold).SelectMany(f => f.Samples).ToList();
                        var testData = folds[fold].Samples;

                        var foldDataset = new MeasurementDataset;
                        {
                            Id = $"{dataset.Id}_fold_{fold}",
                            ProblemType = dataset.ProblemType,
                            Samples = testData,
                            TrainSamples = trainData,
                            Metadata = dataset.Metadata;
                        };

                        // Modeli eğit (eğer gerekliyse)
                        if (cvOptions.RetrainForEachFold)
                        {
                            await TrainModelForFoldAsync(model, trainData, cvOptions);
                        }

                        // Doğruluğu ölç;
                        var foldResult = await MeasureAccuracyAsync(model, foldDataset, options);
                        foldResult.FoldNumber = fold + 1;
                        foldResults.Add(foldResult);

                        cvResult.FoldMetrics.Add(foldResult.OverallAccuracy);
                    }
                    catch (Exception ex)
                    {
                        foldErrors.Add(ex);
                        _logger.LogWarning($"Fold {fold + 1} failed: {ex.Message}", GetType().Name);
                    }
                }

                // Sonuçları birleştir;
                cvResult.FoldResults = foldResults;
                cvResult.Errors = foldErrors;
                cvResult.EndTime = DateTime.UtcNow;
                cvResult.Duration = cvResult.EndTime - cvResult.StartTime;

                // İstatistikleri hesapla;
                cvResult.MeanAccuracy = cvResult.FoldMetrics.Any() ? cvResult.FoldMetrics.Average() : 0;
                cvResult.StdDevAccuracy = cvResult.FoldMetrics.Any() ?
                    CalculateStandardDeviation(cvResult.FoldMetrics) : 0;
                cvResult.MinAccuracy = cvResult.FoldMetrics.Any() ? cvResult.FoldMetrics.Min() : 0;
                cvResult.MaxAccuracy = cvResult.FoldMetrics.Any() ? cvResult.FoldMetrics.Max() : 0;
                cvResult.ConfidenceInterval = await CalculateCrossValidationCIAsync(cvResult, options);

                // Varyans analizi;
                cvResult.VarianceAnalysis = await AnalyzeVarianceAsync(cvResult);

                // Model kararlılığını değerlendir;
                cvResult.ModelStability = EvaluateModelStability(cvResult);

                _logger.LogInformation($"Cross-validation completed. Mean accuracy: {cvResult.MeanAccuracy:P2}",
                    GetType().Name);

                return cvResult;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Cross-validation failed for model {model.Name}: {ex.Message}",
                    GetType().Name, ex);
                throw new AccuracyMeasurementException($"Cross-validation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// İncremental doğruluk ölçümü yapar;
        /// </summary>
        public async Task<IncrementalAccuracyResult> MeasureAccuracyIncrementallyAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            IncrementalMeasurementOptions incrementalOptions,
            AccuracyMeasurementOptions options = null)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));
            if (dataset == null)
                throw new ArgumentNullException(nameof(dataset));

            options = options ?? new AccuracyMeasurementOptions();

            try
            {
                _logger.LogInformation($"Incremental accuracy measurement for model: {model.Name}", GetType().Name);

                var incrementalResult = new IncrementalAccuracyResult;
                {
                    ModelId = model.Id,
                    ModelName = model.Name,
                    DatasetId = dataset.Id,
                    IncrementSize = incrementalOptions.IncrementSize,
                    StartTime = DateTime.UtcNow;
                };

                // Veriyi artımlara böl;
                var increments = await CreateIncrementsAsync(dataset, incrementalOptions);

                var incrementalMetrics = new List<AccuracyMeasurementResult>();

                // Her artım için ölçüm yap;
                for (int i = 0; i < increments.Count; i++)
                {
                    try
                    {
                        _logger.LogDebug($"Processing increment {i + 1}/{increments.Count}", GetType().Name);

                        var incrementDataset = new MeasurementDataset;
                        {
                            Id = $"{dataset.Id}_increment_{i}",
                            ProblemType = dataset.ProblemType,
                            Samples = increments[i],
                            Metadata = dataset.Metadata;
                        };

                        // Doğruluğu ölç;
                        var incrementResult = await MeasureAccuracyAsync(model, incrementDataset, options);
                        incrementResult.IncrementNumber = i + 1;
                        incrementalMetrics.Add(incrementResult);

                        // Gerçek zamanlı güncelleme;
                        if (options.EnableRealTimeUpdates)
                        {
                            await UpdateRealTimeMetricsAsync(incrementResult, incrementalResult);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Increment {i + 1} failed: {ex.Message}", GetType().Name);
                    }
                }

                // Sonuçları analiz et;
                incrementalResult.IncrementResults = incrementalMetrics;
                incrementalResult.EndTime = DateTime.UtcNow;
                incrementalResult.Duration = incrementalResult.EndTime - incrementalResult.StartTime;

                // Eğilim analizi;
                incrementalResult.TrendAnalysis = await AnalyzeAccuracyTrendAsync(incrementalResult);

                // Yakınsama analizi;
                incrementalResult.ConvergenceAnalysis = await AnalyzeConvergenceAsync(incrementalResult);

                // Öğrenme eğrisi;
                incrementalResult.LearningCurve = await GenerateLearningCurveAsync(incrementalResult);

                _logger.LogInformation($"Incremental measurement completed. Final accuracy: {incrementalResult.FinalAccuracy:P2}",
                    GetType().Name);

                return incrementalResult;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Incremental measurement failed for model {model.Name}: {ex.Message}",
                    GetType().Name, ex);
                throw new AccuracyMeasurementException($"Incremental measurement failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Model karşılaştırması yapar;
        /// </summary>
        public async Task<ModelComparisonResult> CompareModelsAsync(
            IEnumerable<INeuralModel> models,
            MeasurementDataset dataset,
            ComparisonOptions comparisonOptions,
            AccuracyMeasurementOptions options = null)
        {
            if (models == null || !models.Any())
                throw new ArgumentException("At least one model is required", nameof(models));
            if (dataset == null)
                throw new ArgumentNullException(nameof(dataset));

            options = options ?? new AccuracyMeasurementOptions();

            try
            {
                _logger.LogInformation($"Comparing {models.Count()} models", GetType().Name);

                var comparisonResult = new ModelComparisonResult;
                {
                    DatasetId = dataset.Id,
                    ComparisonType = comparisonOptions.Type,
                    StartTime = DateTime.UtcNow,
                    Models = models.ToList()
                };

                var modelResults = new ConcurrentDictionary<string, AccuracyMeasurementResult>();
                var comparisonTasks = new List<Task>();

                // Her model için ölçüm yap;
                foreach (var model in models)
                {
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await MeasureAccuracyAsync(model, dataset, options);
                            modelResults[model.Id] = result;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Model {model.Name} comparison failed: {ex.Message}",
                                GetType().Name);
                        }
                    });

                    comparisonTasks.Add(task);
                }

                await Task.WhenAll(comparisonTasks);

                // Sonuçları birleştir;
                comparisonResult.ModelResults = modelResults;
                comparisonResult.EndTime = DateTime.UtcNow;
                comparisonResult.Duration = comparisonResult.EndTime - comparisonResult.StartTime;

                // İstatistiksel karşılaştırma;
                comparisonResult.StatisticalComparison = await PerformStatisticalComparisonAsync(
                    modelResults.Values.ToList(), comparisonOptions);

                // Sıralama;
                comparisonResult.Ranking = await RankModelsAsync(modelResults.Values.ToList(), comparisonOptions);

                // Öneriler;
                comparisonResult.Recommendations = await GenerateComparisonRecommendationsAsync(comparisonResult);

                _logger.LogInformation($"Model comparison completed. Best model: {comparisonResult.Ranking.FirstOrDefault()?.ModelName}",
                    GetType().Name);

                return comparisonResult;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Model comparison failed: {ex.Message}", GetType().Name, ex);
                throw new AccuracyMeasurementException($"Model comparison failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Doğruluk kalibrasyonu yapar;
        /// </summary>
        public async Task<CalibrationResult> CalibrateAccuracyAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            CalibrationOptions calibrationOptions)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));
            if (dataset == null)
                throw new ArgumentNullException(nameof(dataset));

            try
            {
                _logger.LogInformation($"Calibrating model: {model.Name}", GetType().Name);

                // Mevcut doğruluğu ölç;
                var baselineResult = await MeasureAccuracyAsync(model, dataset,
                    new AccuracyMeasurementOptions { Type = MeasurementType.Diagnostic });

                var calibrationResult = new CalibrationResult;
                {
                    ModelId = model.Id,
                    ModelName = model.Name,
                    BaselineAccuracy = baselineResult.OverallAccuracy,
                    StartTime = DateTime.UtcNow,
                    CalibrationMethod = calibrationOptions.Method;
                };

                // Kalibrasyon yöntemini uygula;
                switch (calibrationOptions.Method)
                {
                    case CalibrationMethod.TemperatureScaling:
                        calibrationResult = await ApplyTemperatureScalingAsync(
                            model, dataset, calibrationOptions, calibrationResult);
                        break;

                    case CalibrationMethod.PlattScaling:
                        calibrationResult = await ApplyPlattScalingAsync(
                            model, dataset, calibrationOptions, calibrationResult);
                        break;

                    case CalibrationMethod.IsotonicRegression:
                        calibrationResult = await ApplyIsotonicRegressionAsync(
                            model, dataset, calibrationOptions, calibrationResult);
                        break;

                    case CalibrationMethod.BayesianBinning:
                        calibrationResult = await ApplyBayesianBinningAsync(
                            model, dataset, calibrationOptions, calibrationResult);
                        break;

                    case CalibrationMethod.Ensemble:
                        calibrationResult = await ApplyEnsembleCalibrationAsync(
                            model, dataset, calibrationOptions, calibrationResult);
                        break;
                }

                // Kalibrasyon sonrası doğruluğu ölç;
                var calibratedResult = await MeasureAccuracyAsync(model, dataset,
                    new AccuracyMeasurementOptions { Type = MeasurementType.Diagnostic });

                calibrationResult.CalibratedAccuracy = calibratedResult.OverallAccuracy;
                calibrationResult.Improvement = calibratedResult.OverallAccuracy - baselineResult.OverallAccuracy;
                calibrationResult.EndTime = DateTime.UtcNow;
                calibrationResult.Duration = calibrationResult.EndTime - calibrationResult.StartTime;

                // Kalibrasyon kalitesini değerlendir;
                calibrationResult.CalibrationQuality = await EvaluateCalibrationQualityAsync(
                    calibrationResult, calibratedResult);

                _logger.LogInformation($"Calibration completed. Improvement: {calibrationResult.Improvement:P2}",
                    GetType().Name);

                return calibrationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Model calibration failed for {model.Name}: {ex.Message}",
                    GetType().Name, ex);
                throw new AccuracyCalibrationException($"Calibration failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Doğruluk metriklerini getirir;
        /// </summary>
        public async Task<AccuracyMetricsReport> GetAccuracyMetricsAsync(
            string modelId,
            TimeSpan? timeRange = null,
            MetricsAggregationLevel aggregationLevel = MetricsAggregationLevel.Detailed)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Model ID is required", nameof(modelId));

            try
            {
                _logger.LogDebug($"Getting accuracy metrics for model: {modelId}", GetType().Name);

                var report = new AccuracyMetricsReport;
                {
                    ModelId = modelId,
                    TimeRange = timeRange ?? TimeSpan.FromDays(30),
                    AggregationLevel = aggregationLevel,
                    GenerationTime = DateTime.UtcNow;
                };

                // Metrikleri veritabanından getir;
                var measurements = await _metricsRepository.GetMeasurementsForModelAsync(modelId, timeRange);

                if (!measurements.Any())
                {
                    _logger.LogWarning($"No metrics found for model {modelId}", GetType().Name);
                    return report;
                }

                // Temel istatistikler;
                report.BasicStatistics = await CalculateBasicStatisticsAsync(measurements);

                // Zaman serisi analizi;
                if (aggregationLevel >= MetricsAggregationLevel.Standard)
                {
                    report.TimeSeriesAnalysis = await AnalyzeTimeSeriesAsync(measurements, timeRange);
                }

                // Performans eğilimleri;
                if (aggregationLevel >= MetricsAggregationLevel.Comprehensive)
                {
                    report.PerformanceTrends = await AnalyzePerformanceTrendsAsync(measurements);
                }

                // Anomali tespiti;
                if (aggregationLevel >= MetricsAggregationLevel.Diagnostic)
                {
                    report.Anomalies = await DetectAnomaliesAsync(measurements);
                }

                // Öneriler;
                report.Recommendations = await GenerateMetricsRecommendationsAsync(report);

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get accuracy metrics for model {modelId}: {ex.Message}",
                    GetType().Name, ex);
                throw new AccuracyMeasurementException($"Failed to get metrics: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Aktif ölçüm oturumlarını getirir;
        /// </summary>
        public IEnumerable<MeasurementSessionInfo> GetActiveSessions()
        {
            return _activeSessions.Values.Select(session => new MeasurementSessionInfo;
            {
                SessionId = session.Id,
                ModelName = session.ModelName,
                DatasetId = session.Dataset.Id,
                StartTime = session.StartTime,
                Status = session.Status,
                ElapsedTime = DateTime.UtcNow - session.StartTime;
            }).ToList();
        }

        /// <summary>
        /// Model doğruluk profillerini getirir;
        /// </summary>
        public IEnumerable<ModelAccuracyProfile> GetModelProfiles()
        {
            return _modelProfiles.Values.ToList();
        }

        /// <summary>
        /// Metrik veritabanını temizler;
        /// </summary>
        public async Task ClearMetricsDatabaseAsync(DateTime? olderThan = null)
        {
            try
            {
                await _metricsRepository.ClearOldMetricsAsync(olderThan);
                _logger.LogInformation("Metrics database cleared", GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to clear metrics database: {ex.Message}", GetType().Name, ex);
                throw new AccuracyMeasurementException($"Failed to clear metrics: {ex.Message}", ex);
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
                        _metricsAggregationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        _metricsAggregationTimer?.Dispose();

                        // Aktif oturumları durdur;
                        foreach (var session in _activeSessions.Values)
                        {
                            session.CancellationTokenSource?.Cancel();
                            session.CancellationTokenSource?.Dispose();
                        }
                        _activeSessions.Clear();

                        // Kilitleri serbest bırak;
                        _measurementLock?.Dispose();

                        // Alt bileşenleri dispose et;
                        _metricsRepository?.Dispose();
                        _statisticalEngine?.Dispose();

                        _logger.LogInformation("AccuracyMeasurer disposed", GetType().Name);
                    }

                    _disposed = true;
                }
            }
        }

        #region Private Implementation Methods;

        private async Task<AccuracyMeasurementResult> MeasureBinaryClassificationAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            AccuracyMeasurementOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Measuring binary classification accuracy for model: {model.Name}", GetType().Name);

            var predictions = new List<PredictionResult>();
            var trueLabels = new List<bool>();

            // Tahminler topla;
            foreach (var sample in dataset.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var prediction = await _neuralNetwork.PredictAsync(model, sample.Features);
                var predictedClass = prediction.Confidence >= 0.5;

                predictions.Add(new PredictionResult;
                {
                    SampleId = sample.Id,
                    PredictedClass = predictedClass,
                    Confidence = prediction.Confidence,
                    TrueClass = sample.Label;
                });

                trueLabels.Add(sample.Label);
            }

            // Temel metrikleri hesapla;
            var metrics = CalculateBinaryClassificationMetrics(predictions, trueLabels);

            var result = new AccuracyMeasurementResult;
            {
                ModelId = model.Id,
                ModelName = model.Name,
                ProblemType = ProblemType.BinaryClassification,
                OverallAccuracy = metrics.Accuracy,
                Predictions = predictions,
                BasicMetrics = metrics,
                DatasetSize = dataset.Samples.Count;
            };

            // Detaylı metrikler;
            if (options.Type >= MeasurementType.Standard)
            {
                result.DetailedMetrics = CalculateDetailedBinaryMetrics(metrics);
            }

            // Karmaşıklık matrisi;
            if (options.Type >= MeasurementType.Comprehensive)
            {
                result.ConfusionMatrix = GenerateConfusionMatrix(predictions);
            }

            return result;
        }

        private async Task<AccuracyMeasurementResult> MeasureMulticlassClassificationAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            AccuracyMeasurementOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Measuring multiclass classification accuracy for model: {model.Name}", GetType().Name);

            var predictions = new List<PredictionResult>();
            var classMetrics = new Dictionary<string, ClassificationMetrics>();

            // Tahminler topla;
            foreach (var sample in dataset.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var prediction = await _neuralNetwork.PredictAsync(model, sample.Features);

                predictions.Add(new PredictionResult;
                {
                    SampleId = sample.Id,
                    PredictedClass = prediction.PredictedClass,
                    Confidence = prediction.Confidence,
                    TrueClass = sample.Label,
                    ClassProbabilities = prediction.ClassProbabilities;
                });
            }

            // Genel doğruluk;
            var correctCount = predictions.Count(p => p.PredictedClass == p.TrueClass);
            var overallAccuracy = (double)correctCount / predictions.Count;

            // Sınıf bazlı metrikler;
            var uniqueClasses = predictions.Select(p => p.TrueClass).Distinct().ToList();
            foreach (var className in uniqueClasses)
            {
                var classPredictions = predictions.Where(p => p.TrueClass == className).ToList();
                var classMetricsResult = CalculateClassMetrics(classPredictions, className);
                classMetrics[className] = classMetricsResult;
            }

            var result = new AccuracyMeasurementResult;
            {
                ModelId = model.Id,
                ModelName = model.Name,
                ProblemType = ProblemType.MulticlassClassification,
                OverallAccuracy = overallAccuracy,
                Predictions = predictions,
                ClassMetrics = classMetrics,
                DatasetSize = dataset.Samples.Count;
            };

            // Makro ve mikro ortalamalar;
            if (options.Type >= MeasurementType.Standard)
            {
                result.MacroAveragedMetrics = CalculateMacroAveragedMetrics(classMetrics.Values.ToList());
                result.MicroAveragedMetrics = CalculateMicroAveragedMetrics(predictions);
            }

            // Çok sınıflı karmaşıklık matrisi;
            if (options.Type >= MeasurementType.Comprehensive)
            {
                result.MulticlassConfusionMatrix = GenerateMulticlassConfusionMatrix(predictions, uniqueClasses);
            }

            return result;
        }

        private async Task<AccuracyMeasurementResult> MeasureRegressionAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            AccuracyMeasurementOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Measuring regression accuracy for model: {model.Name}", GetType().Name);

            var predictions = new List<PredictionResult>();
            var errors = new List<double>();

            // Tahminler topla;
            foreach (var sample in dataset.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var prediction = await _neuralNetwork.PredictAsync(model, sample.Features);
                var error = Math.Abs(prediction.PredictedValue - sample.NumericLabel);

                predictions.Add(new PredictionResult;
                {
                    SampleId = sample.Id,
                    PredictedValue = prediction.PredictedValue,
                    TrueValue = sample.NumericLabel,
                    Error = error;
                });

                errors.Add(error);
            }

            // Regresyon metrikleri;
            var regressionMetrics = CalculateRegressionMetrics(predictions);

            var result = new AccuracyMeasurementResult;
            {
                ModelId = model.Id,
                ModelName = model.Name,
                ProblemType = ProblemType.Regression,
                OverallAccuracy = 1.0 - (regressionMetrics.RMSE / regressionMetrics.Range), // Normalize edilmiş doğruluk;
                Predictions = predictions,
                RegressionMetrics = regressionMetrics,
                DatasetSize = dataset.Samples.Count;
            };

            // Artık analizi;
            if (options.Type >= MeasurementType.Diagnostic)
            {
                result.ResidualAnalysis = await AnalyzeResidualsAsync(predictions);
            }

            return result;
        }

        private async Task<StatisticalAnalysis> PerformStatisticalAnalysisAsync(
            AccuracyMeasurementResult result,
            MeasurementDataset dataset,
            AccuracyMeasurementOptions options)
        {
            var analysis = new StatisticalAnalysis;
            {
                BasicStatistics = CalculateBasicStatistics(result.Predictions),
                DistributionAnalysis = await AnalyzeDistributionAsync(result.Predictions),
                HypothesisTests = await PerformHypothesisTestsAsync(result, dataset),
                EffectSizes = await CalculateEffectSizesAsync(result),
                SignificanceLevel = options.ConfidenceLevel;
            };

            return analysis;
        }

        private async Task<ConfidenceInterval> CalculateConfidenceIntervalsAsync(
            AccuracyMeasurementResult result,
            AccuracyMeasurementOptions options)
        {
            var ci = new ConfidenceInterval;
            {
                ConfidenceLevel = options.ConfidenceLevel,
                AccuracyCI = await CalculateAccuracyCIAsync(result.OverallAccuracy, result.DatasetSize, options),
                PrecisionCI = result.BasicMetrics != null ?
                    await CalculateMetricCIAsync(result.BasicMetrics.Precision, result.DatasetSize, options) : null,
                RecallCI = result.BasicMetrics != null ?
                    await CalculateMetricCIAsync(result.BasicMetrics.Recall, result.DatasetSize, options) : null,
                F1CI = result.BasicMetrics != null ?
                    await CalculateMetricCIAsync(result.BasicMetrics.F1Score, result.DatasetSize, options) : null;
            };

            return ci;
        }

        private async Task<ErrorAnalysis> AnalyzeErrorsAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            AccuracyMeasurementResult result,
            AccuracyMeasurementOptions options)
        {
            var analysis = new ErrorAnalysis;
            {
                ErrorDistribution = await AnalyzeErrorDistributionAsync(result.Predictions),
                ErrorPatterns = await DetectErrorPatternsAsync(result.Predictions, dataset),
                HardSamples = await IdentifyHardSamplesAsync(result.Predictions, dataset),
                ErrorClusters = await ClusterErrorsAsync(result.Predictions),
                SystematicErrors = await DetectSystematicErrorsAsync(result.Predictions)
            };

            return analysis;
        }

        private async Task<BiasDetectionResult> DetectBiasAsync(
            INeuralModel model,
            MeasurementDataset dataset,
            AccuracyMeasurementResult result,
            AccuracyMeasurementOptions options)
        {
            var biasResult = new BiasDetectionResult;
            {
                DemographicBias = await AnalyzeDemographicBiasAsync(result.Predictions, dataset),
                FeatureBias = await AnalyzeFeatureBiasAsync(result.Predictions, dataset),
                ClassBias = await AnalyzeClassBiasAsync(result.Predictions, dataset),
                FairnessMetrics = await CalculateFairnessMetricsAsync(result.Predictions, dataset),
                BiasMitigationSuggestions = await GenerateBiasMitigationSuggestionsAsync(biasResult)
            };

            return biasResult;
        }

        private async Task UpdateModelProfileAsync(
            INeuralModel model,
            AccuracyMeasurementResult result,
            AccuracyMeasurementOptions options)
        {
            if (!_modelProfiles.TryGetValue(model.Id, out var profile))
            {
                profile = new ModelAccuracyProfile;
                {
                    ModelId = model.Id,
                    ModelName = model.Name,
                    CreatedAt = DateTime.UtcNow;
                };
            }

            profile.LastMeasurement = result.Timestamp;
            profile.HistoricalAccuracy.Add(new AccuracyRecord;
            {
                Accuracy = result.OverallAccuracy,
                Timestamp = result.Timestamp,
                DatasetId = result.DatasetId;
            });

            profile.BestAccuracy = Math.Max(profile.BestAccuracy, result.OverallAccuracy);
            profile.AverageAccuracy = profile.HistoricalAccuracy.Average(r => r.Accuracy);

            _modelProfiles[model.Id] = profile;
        }

        private string GenerateSessionId(INeuralModel model)
        {
            return $"ACC_{model.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Interlocked.Increment(ref _measurementCounter)}";
        }

        private void AggregateMetrics(object state)
        {
            try
            {
                // Metrikleri toplu halde işle;
                AggregateModelMetrics();
                CleanupOldSessions();

                _logger.LogDebug("Metrics aggregation completed", GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Metrics aggregation failed: {ex.Message}", GetType().Name);
            }
        }

        private void AggregateModelMetrics()
        {
            foreach (var profile in _modelProfiles.Values)
            {
                try
                {
                    // Profil metriklerini güncelle;
                    UpdateProfileAggregates(profile);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Profile aggregation failed for {profile.ModelName}: {ex.Message}",
                        GetType().Name);
                }
            }
        }

        private void CleanupOldSessions()
        {
            var oldSessions = _activeSessions;
                .Where(s => DateTime.UtcNow - s.Value.StartTime > TimeSpan.FromHours(1))
                .ToList();

            foreach (var session in oldSessions)
            {
                if (_activeSessions.TryRemove(session.Key, out var s))
                {
                    s.CancellationTokenSource?.Cancel();
                    _logger.LogDebug($"Cleaned up old session: {session.Key}", GetType().Name);
                }
            }
        }

        #endregion;

        #region Helper Classes;

        private class AccuracyMetricsRepository : IDisposable
        {
            private readonly ILogger _logger;
            private readonly ConcurrentDictionary<string, List<AccuracyMeasurementResult>> _measurements;

            public AccuracyMetricsRepository(ILogger logger)
            {
                _logger = logger;
                _measurements = new ConcurrentDictionary<string, List<AccuracyMeasurementResult>>();
            }

            public async Task InitializeAsync()
            {
                // Repository başlatma;
                // Implementasyon detayları;
            }

            public async Task RecordMeasurementAsync(AccuracyMeasurementResult result)
            {
                var modelMeasurements = _measurements.GetOrAdd(result.ModelId, _ => new List<AccuracyMeasurementResult>());
                modelMeasurements.Add(result);
            }

            public async Task<List<AccuracyMeasurementResult>> GetMeasurementsForModelAsync(
                string modelId, TimeSpan? timeRange)
            {
                if (_measurements.TryGetValue(modelId, out var measurements))
                {
                    if (timeRange.HasValue)
                    {
                        var cutoff = DateTime.UtcNow - timeRange.Value;
                        return measurements.Where(m => m.Timestamp >= cutoff).ToList();
                    }
                    return measurements;
                }

                return new List<AccuracyMeasurementResult>();
            }

            public async Task ClearOldMetricsAsync(DateTime? olderThan)
            {
                var cutoff = olderThan ?? DateTime.UtcNow.AddDays(-30);

                foreach (var kvp in _measurements)
                {
                    kvp.Value.RemoveAll(m => m.Timestamp < cutoff);
                }
            }

            public void Dispose()
            {
                // Kaynakları serbest bırak;
            }
        }

        private class StatisticalEngine : IDisposable
        {
            private readonly ILogger _logger;
            private readonly IStatisticalAnalyzer _statisticalAnalyzer;

            public StatisticalEngine(ILogger logger, IStatisticalAnalyzer statisticalAnalyzer)
            {
                _logger = logger;
                _statisticalAnalyzer = statisticalAnalyzer;
            }

            public async Task InitializeAsync()
            {
                // İstatistiksel motoru başlat;
            }

            public void Dispose()
            {
                // Kaynakları serbest bırak;
            }
        }

        private class MeasurementValidator;
        {
            private readonly ILogger _logger;

            public MeasurementValidator(ILogger logger)
            {
                _logger = logger;
            }
        }

        #endregion;

        #region Public Classes and Interfaces;

        /// <summary>
        /// Doğruluk ölçümü oturumu;
        /// </summary>
        public class AccuracyMeasurementSession;
        {
            public string Id { get; set; }
            public string ModelId { get; set; }
            public string ModelName { get; set; }
            public MeasurementDataset Dataset { get; set; }
            public AccuracyMeasurementOptions Options { get; set; }
            public DateTime StartTime { get; set; }
            public MeasurementStatus Status { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; set; }
        }

        /// <summary>
        /// Doğruluk ölçümü sonucu;
        /// </summary>
        public class AccuracyMeasurementResult;
        {
            public string SessionId { get; set; }
            public string ModelId { get; set; }
            public string ModelName { get; set; }
            public string DatasetId { get; set; }
            public ProblemType ProblemType { get; set; }
            public double OverallAccuracy { get; set; }
            public TimeSpan MeasurementTime { get; set; }
            public DateTime Timestamp { get; set; }
            public int DatasetSize { get; set; }
            public List<PredictionResult> Predictions { get; set; } = new List<PredictionResult>();
            public BasicClassificationMetrics BasicMetrics { get; set; }
            public DetailedClassificationMetrics DetailedMetrics { get; set; }
            public RegressionMetrics RegressionMetrics { get; set; }
            public Dictionary<string, ClassificationMetrics> ClassMetrics { get; set; } = new Dictionary<string, ClassificationMetrics>();
            public ClassificationMetrics MacroAveragedMetrics { get; set; }
            public ClassificationMetrics MicroAveragedMetrics { get; set; }
            public ConfusionMatrix ConfusionMatrix { get; set; }
            public MulticlassConfusionMatrix MulticlassConfusionMatrix { get; set; }
            public StatisticalAnalysis StatisticalAnalysis { get; set; }
            public ConfidenceInterval ConfidenceIntervals { get; set; }
            public ErrorAnalysis ErrorAnalysis { get; set; }
            public BiasDetectionResult BiasDetection { get; set; }
            public ResidualAnalysis ResidualAnalysis { get; set; }
            public int FoldNumber { get; set; }
            public int IncrementNumber { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Çapraz doğrulama sonucu;
        /// </summary>
        public class CrossValidationResult;
        {
            public string ModelId { get; set; }
            public string ModelName { get; set; }
            public string DatasetId { get; set; }
            public CrossValidationType CrossValidationType { get; set; }
            public int NumberOfFolds { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration { get; set; }
            public List<AccuracyMeasurementResult> FoldResults { get; set; } = new List<AccuracyMeasurementResult>();
            public List<Exception> Errors { get; set; } = new List<Exception>();
            public List<double> FoldMetrics { get; set; } = new List<double>();
            public double MeanAccuracy { get; set; }
            public double StdDevAccuracy { get; set; }
            public double MinAccuracy { get; set; }
            public double MaxAccuracy { get; set; }
            public ConfidenceInterval ConfidenceInterval { get; set; }
            public VarianceAnalysis VarianceAnalysis { get; set; }
            public ModelStability ModelStability { get; set; }
        }

        /// <summary>
        /// İncremental doğruluk sonucu;
        /// </summary>
        public class IncrementalAccuracyResult;
        {
            public string ModelId { get; set; }
            public string ModelName { get; set; }
            public string DatasetId { get; set; }
            public int IncrementSize { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration { get; set; }
            public List<AccuracyMeasurementResult> IncrementResults { get; set; } = new List<AccuracyMeasurementResult>();
            public double FinalAccuracy => IncrementResults.LastOrDefault()?.OverallAccuracy ?? 0;
            public TrendAnalysis TrendAnalysis { get; set; }
            public ConvergenceAnalysis ConvergenceAnalysis { get; set; }
            public LearningCurve LearningCurve { get; set; }
            public Dictionary<string, object> RealTimeMetrics { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Model karşılaştırma sonucu;
        /// </summary>
        public class ModelComparisonResult;
        {
            public string DatasetId { get; set; }
            public ComparisonType ComparisonType { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration { get; set; }
            public List<INeuralModel> Models { get; set; } = new List<INeuralModel>();
            public Dictionary<string, AccuracyMeasurementResult> ModelResults { get; set; } = new Dictionary<string, AccuracyMeasurementResult>();
            public StatisticalComparison StatisticalComparison { get; set; }
            public List<ModelRanking> Ranking { get; set; } = new List<ModelRanking>();
            public List<ComparisonRecommendation> Recommendations { get; set; } = new List<ComparisonRecommendation>();
        }

        /// <summary>
        /// Kalibrasyon sonucu;
        /// </summary>
        public class CalibrationResult;
        {
            public string ModelId { get; set; }
            public string ModelName { get; set; }
            public CalibrationMethod CalibrationMethod { get; set; }
            public double BaselineAccuracy { get; set; }
            public double CalibratedAccuracy { get; set; }
            public double Improvement { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public TimeSpan Duration { get; set; }
            public CalibrationQuality CalibrationQuality { get; set; }
            public Dictionary<string, object> CalibrationParameters { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Doğruluk metrikleri raporu;
        /// </summary>
        public class AccuracyMetricsReport;
        {
            public string ModelId { get; set; }
            public TimeSpan TimeRange { get; set; }
            public MetricsAggregationLevel AggregationLevel { get; set; }
            public DateTime GenerationTime { get; set; }
            public BasicStatistics BasicStatistics { get; set; }
            public TimeSeriesAnalysis TimeSeriesAnalysis { get; set; }
            public PerformanceTrends PerformanceTrends { get; set; }
            public List<AnomalyDetection> Anomalies { get; set; } = new List<AnomalyDetection>();
            public List<MetricsRecommendation> Recommendations { get; set; } = new List<MetricsRecommendation>();
        }

        /// <summary>
        /// Model doğruluk profili;
        /// </summary>
        public class ModelAccuracyProfile;
        {
            public string ModelId { get; set; }
            public string ModelName { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastMeasurement { get; set; }
            public double BestAccuracy { get; set; }
            public double AverageAccuracy { get; set; }
            public List<AccuracyRecord> HistoricalAccuracy { get; set; } = new List<AccuracyRecord>();
            public Dictionary<string, object> PerformanceCharacteristics { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Ölçüm veri seti;
        /// </summary>
        public class MeasurementDataset;
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public ProblemType ProblemType { get; set; }
            public List<DataSample> Samples { get; set; } = new List<DataSample>();
            public List<DataSample> TrainSamples { get; set; } = new List<DataSample>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Veri örneği;
        /// </summary>
        public class DataSample;
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public float[] Features { get; set; }
            public string Label { get; set; }
            public bool BooleanLabel { get; set; }
            public double NumericLabel { get; set; }
            public string[] MultiLabels { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Tahmin sonucu;
        /// </summary>
        public class PredictionResult;
        {
            public string SampleId { get; set; }
            public string PredictedClass { get; set; }
            public bool PredictedBoolean { get; set; }
            public double PredictedValue { get; set; }
            public double Confidence { get; set; }
            public string TrueClass { get; set; }
            public bool TrueBoolean { get; set; }
            public double TrueValue { get; set; }
            public double Error { get; set; }
            public Dictionary<string, double> ClassProbabilities { get; set; } = new Dictionary<string, double>();
        }

        /// <summary>
        /// Temel sınıflandırma metrikleri;
        /// </summary>
        public class BasicClassificationMetrics;
        {
            public double Accuracy { get; set; }
            public double Precision { get; set; }
            public double Recall { get; set; }
            public double F1Score { get; set; }
            public double Specificity { get; set; }
            public double NPV { get; set; } // Negative Predictive Value;
        }

        /// <summary>
        /// Detaylı sınıflandırma metrikleri;
        /// </summary>
        public class DetailedClassificationMetrics;
        {
            public double AUC { get; set; }
            public double MCC { get; set; } // Matthews Correlation Coefficient;
            public double CohenKappa { get; set; }
            public double Informedness { get; set; }
            public double Markedness { get; set; }
            public Dictionary<double, double> ROC { get; set; } = new Dictionary<double, double>();
            public Dictionary<double, double> PR { get; set; } = new Dictionary<double, double>(); // Precision-Recall;
        }

        /// <summary>
        /// Regresyon metrikleri;
        /// </summary>
        public class RegressionMetrics;
        {
            public double MAE { get; set; } // Mean Absolute Error;
            public double MSE { get; set; } // Mean Squared Error;
            public double RMSE { get; set; } // Root Mean Squared Error;
            public double R2 { get; set; } // R-squared;
            public double AdjustedR2 { get; set; }
            public double MAPE { get; set; } // Mean Absolute Percentage Error;
            public double Range { get; set; }
            public double ExplainedVariance { get; set; }
        }

        /// <summary>
        /// Kaynak kısıtlamaları;
        /// </summary>
        public class ResourceConstraints;
        {
            public long MaxMemoryBytes { get; set; } = 1024 * 1024 * 1024; // 1 GB;
            public int MaxConcurrentMeasurements { get; set; } = 5;
            public double MaxCpuUsage { get; set; } = 80.0;
        }

        /// <summary>
        /// Ölçüm oturumu bilgisi;
        /// </summary>
        public class MeasurementSessionInfo;
        {
            public string SessionId { get; set; }
            public string ModelName { get; set; }
            public string DatasetId { get; set; }
            public DateTime StartTime { get; set; }
            public MeasurementStatus Status { get; set; }
            public TimeSpan ElapsedTime { get; set; }
        }

        /// <summary>
        /// Doğruluk kaydı;
        /// </summary>
        public class AccuracyRecord;
        {
            public double Accuracy { get; set; }
            public DateTime Timestamp { get; set; }
            public string DatasetId { get; set; }
        }

        /// <summary>
        /// Ölçüm durumu;
        /// </summary>
        public enum MeasurementStatus;
        {
            Running,
            Completed,
            Failed,
            Cancelled,
            Timeout;
        }

        /// <summary>
        /// Çapraz doğrulama tipi;
        /// </summary>
        public enum CrossValidationType;
        {
            KFold,
            StratifiedKFold,
            LeaveOneOut,
            LeavePOut,
            TimeSeriesSplit,
            GroupKFold;
        }

        /// <summary>
        /// Karşılaştırma tipi;
        /// </summary>
        public enum ComparisonType;
        {
            Accuracy,
            Performance,
            Efficiency,
            Robustness,
            Comprehensive;
        }

        /// <summary>
        /// Kalibrasyon yöntemi;
        /// </summary>
        public enum CalibrationMethod;
        {
            TemperatureScaling,
            PlattScaling,
            IsotonicRegression,
            BayesianBinning,
            Ensemble,
            None;
        }

        /// <summary>
        /// Metrik toplama seviyesi;
        /// </summary>
        public enum MetricsAggregationLevel;
        {
            Basic,
            Standard,
            Comprehensive,
            Diagnostic,
            Research;
        }

        #endregion;

        #region Exceptions;

        public class AccuracyMeasurementException : Exception
        {
            public AccuracyMeasurementException(string message) : base(message) { }
            public AccuracyMeasurementException(string message, Exception inner) : base(message, inner) { }
        }

        public class AccuracyMeasurementTimeoutException : AccuracyMeasurementException;
        {
            public AccuracyMeasurementTimeoutException(string message) : base(message) { }
        }

        public class AccuracyCalibrationException : AccuracyMeasurementException;
        {
            public AccuracyCalibrationException(string message) : base(message) { }
            public AccuracyCalibrationException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }

    /// <summary>
    /// Doğruluk ölçüm arayüzü;
    /// </summary>
    public interface IAccuracyMeasurer : IDisposable
    {
        Task InitializeAsync(IEnumerable<AccuracyMeasurer.ModelAccuracyProfile> initialProfiles = null);
        Task<AccuracyMeasurer.AccuracyMeasurementResult> MeasureAccuracyAsync(
            INeuralModel model,
            AccuracyMeasurer.MeasurementDataset dataset,
            AccuracyMeasurer.AccuracyMeasurementOptions options = null);

        Task<AccuracyMeasurer.CrossValidationResult> MeasureWithCrossValidationAsync(
            INeuralModel model,
            AccuracyMeasurer.MeasurementDataset dataset,
            CrossValidationOptions cvOptions,
            AccuracyMeasurer.AccuracyMeasurementOptions options = null);

        Task<AccuracyMeasurer.IncrementalAccuracyResult> MeasureAccuracyIncrementallyAsync(
            INeuralModel model,
            AccuracyMeasurer.MeasurementDataset dataset,
            IncrementalMeasurementOptions incrementalOptions,
            AccuracyMeasurer.AccuracyMeasurementOptions options = null);

        Task<AccuracyMeasurer.ModelComparisonResult> CompareModelsAsync(
            IEnumerable<INeuralModel> models,
            AccuracyMeasurer.MeasurementDataset dataset,
            ComparisonOptions comparisonOptions,
            AccuracyMeasurer.AccuracyMeasurementOptions options = null);

        Task<AccuracyMeasurer.CalibrationResult> CalibrateAccuracyAsync(
            INeuralModel model,
            AccuracyMeasurer.MeasurementDataset dataset,
            CalibrationOptions calibrationOptions);

        Task<AccuracyMeasurer.AccuracyMetricsReport> GetAccuracyMetricsAsync(
            string modelId,
            TimeSpan? timeRange = null,
            MetricsAggregationLevel aggregationLevel = MetricsAggregationLevel.Detailed);

        IEnumerable<AccuracyMeasurer.MeasurementSessionInfo> GetActiveSessions();
        IEnumerable<AccuracyMeasurer.ModelAccuracyProfile> GetModelProfiles();
        Task ClearMetricsDatabaseAsync(DateTime? olderThan = null);
    }

    // Destekleyici sınıflar ve arayüzler;
    public interface INeuralModel;
    {
        string Id { get; }
        string Name { get; }
        ModelType Type { get; }
        // Diğer özellikler...
    }

    public class CrossValidationOptions;
    {
        public CrossValidationType Type { get; set; } = CrossValidationType.KFold;
        public int NumberOfFolds { get; set; } = 5;
        public bool RetrainForEachFold { get; set; } = false;
        public bool ShuffleData { get; set; } = true;
        public int RandomSeed { get; set; } = 42;
        public Dictionary<string, object> FoldParameters { get; set; } = new Dictionary<string, object>();
    }

    public class IncrementalMeasurementOptions;
    {
        public int IncrementSize { get; set; } = 100;
        public IncrementType Type { get; set; } = IncrementType.Sequential;
        public bool ShuffleBetweenIncrements { get; set; } = false;
        public TimeSpan IncrementInterval { get; set; } = TimeSpan.FromSeconds(1);
        public ProgressCallback ProgressCallback { get; set; }
    }

    public enum IncrementType;
    {
        Sequential,
        Random,
        Stratified,
        Adaptive;
    }

    public class ComparisonOptions;
    {
        public ComparisonType Type { get; set; } = ComparisonType.Comprehensive;
        public List<string> MetricsToCompare { get; set; } = new List<string>();
        public bool StatisticalSignificanceTesting { get; set; } = true;
        public double SignificanceLevel { get; set; } = 0.05;
        public Dictionary<string, object> ComparisonParameters { get; set; } = new Dictionary<string, object>();
    }

    public class CalibrationOptions;
    {
        public CalibrationMethod Method { get; set; } = CalibrationMethod.TemperatureScaling;
        public double ValidationSplit { get; set; } = 0.2;
        public int MaxIterations { get; set; } = 1000;
        public double Tolerance { get; set; } = 1e-6;
        public Dictionary<string, object> MethodParameters { get; set; } = new Dictionary<string, object>();
    }

    // Diğer destekleyici sınıflar...
    public class ConfusionMatrix { }
    public class MulticlassConfusionMatrix { }
    public class StatisticalAnalysis { }
    public class ConfidenceInterval { }
    public class ErrorAnalysis { }
    public class BiasDetectionResult { }
    public class ResidualAnalysis { }
    public class VarianceAnalysis { }
    public class ModelStability { }
    public class TrendAnalysis { }
    public class ConvergenceAnalysis { }
    public class LearningCurve { }
    public class StatisticalComparison { }
    public class ModelRanking { }
    public class ComparisonRecommendation { }
    public class CalibrationQuality { }
    public class BasicStatistics { }
    public class TimeSeriesAnalysis { }
    public class PerformanceTrends { }
    public class AnomalyDetection { }
    public class MetricsRecommendation { }
    public class ClassificationMetrics { }

    public interface IStatisticalAnalyzer { }
    public interface IConfidenceCalculator { }
}
