using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.TimeSeries;
using MathNet.Numerics;
using MathNet.Numerics.Statistics;
using NEDA.NeuralNetwork.PatternRecognition;
using NEDA.NeuralNetwork.DeepLearning.NeuralModels;
using NEDA.NeuralNetwork.DeepLearning.TrainingPipelines;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Logging;
using NEDA.Monitoring.MetricsCollector;
using Newtonsoft.Json;

namespace NEDA.NeuralNetwork.PatternRecognition.AnomalyDetection;
{
    /// <summary>
    /// Anomaly Detector - Çoklu yöntemlerle anomali tespiti yapar;
    /// </summary>
    public interface IAnomalyDetector;
    {
        Task<AnomalyDetectionResult> DetectAsync(DetectionRequest request);
        Task<ModelPerformance> TrainAsync(TrainingRequest request);
        Task<DetectionModel> LoadModelAsync(string modelId);
        Task SaveModelAsync(DetectionModel model);
        Task<ThresholdOptimizationResult> OptimizeThresholdsAsync(ThresholdOptimizationRequest request);
        Task<RealTimeDetectionStream> CreateStreamingDetectorAsync(StreamingConfig config);
        Task<List<AnomalyPattern>> AnalyzePatternsAsync(PatternAnalysisRequest request);
        Task<AdaptiveThresholds> CalculateAdaptiveThresholdsAsync(AdaptiveThresholdRequest request);
    }

    /// <summary>
    /// Anomali tespit isteği;
    /// </summary>
    public class DetectionRequest;
    {
        public string DetectionId { get; set; } = Guid.NewGuid().ToString();
        public DetectionMethod Method { get; set; }
        public List<double> Data { get; set; } = new List<double>();
        public List<double[]> MultivariateData { get; set; } = new List<double[]>();
        public DateTimeOffset? StartTime { get; set; }
        public DateTimeOffset? EndTime { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
        public DetectionSensitivity Sensitivity { get; set; } = DetectionSensitivity.Medium;
        public bool UseAdaptiveThreshold { get; set; } = true;
        public int WindowSize { get; set; } = 100;
        public int PredictionHorizon { get; set; } = 10;
        public Dictionary<AnomalyType, double> CustomThresholds { get; set; } = new Dictionary<AnomalyType, double>();
        public string ModelId { get; set; }
        public bool ReturnExplanation { get; set; } = true;
        public bool CalculateConfidence { get; set; } = true;
    }

    /// <summary>
    /// Anomali tespit yöntemleri;
    /// </summary>
    public enum DetectionMethod;
    {
        Statistical,
        MachineLearning,
        DeepLearning,
        TimeSeries,
        Ensemble,
        AutoEncoder,
        IsolationForest,
        OneClassSVM,
        LSTM,
        Prophet,
        SARIMA,
        Hybrid,
        Adaptive;
    }

    /// <summary>
    /// Anomali tespit hassasiyeti;
    /// </summary>
    public enum DetectionSensitivity;
    {
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh,
        Critical;
    }

    /// <summary>
    /// Anomali türleri;
    /// </summary>
    public enum AnomalyType;
    {
        PointAnomaly,
        ContextualAnomaly,
        CollectiveAnomaly,
        TrendAnomaly,
        SeasonalAnomaly,
        PatternChange,
        DistributionShift,
        ConceptDrift,
        Outlier,
        Novelty;
    }

    /// <summary>
    /// Anomali tespit sonucu;
    /// </summary>
    public class AnomalyDetectionResult;
    {
        public string DetectionId { get; set; }
        public DateTimeOffset DetectionTime { get; set; }
        public bool Success { get; set; }
        public List<DetectedAnomaly> Anomalies { get; set; } = new List<DetectedAnomaly>();
        public List<double> AnomalyScores { get; set; } = new List<double>();
        public List<double> Thresholds { get; set; } = new List<double>();
        public DetectionMetrics Metrics { get; set; }
        public Dictionary<string, object> ModelInfo { get; set; } = new Dictionary<string, object>();
        public List<string> Warnings { get; set; } = new List<string>();
        public AnomalyReport Report { get; set; }
        public Dictionary<string, double> FeatureImportance { get; set; } = new Dictionary<string, double>();
        public double OverallAnomalyScore { get; set; }
        public ConfidenceMetrics Confidence { get; set; }
    }

    /// <summary>
    /// Tespit edilen anomali;
    /// </summary>
    public class DetectedAnomaly;
    {
        public int Index { get; set; }
        public double Value { get; set; }
        public double ExpectedValue { get; set; }
        public double AnomalyScore { get; set; }
        public double Confidence { get; set; }
        public AnomalyType Type { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public List<string> Features { get; set; } = new List<string>();
        public string Explanation { get; set; }
        public SeverityLevel Severity { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
        public List<CorrelatedAnomaly> CorrelatedAnomalies { get; set; } = new List<CorrelatedAnomaly>();
        public string RootCause { get; set; }
        public RecommendedAction RecommendedAction { get; set; }
    }

    /// <summary>
    /// Şiddet seviyesi;
    /// </summary>
    public enum SeverityLevel;
    {
        Info,
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Tespit metrikleri;
    /// </summary>
    public class DetectionMetrics;
    {
        public double Precision { get; set; }
        public double Recall { get; set; }
        public double F1Score { get; set; }
        public double AUC { get; set; }
        public double FalsePositiveRate { get; set; }
        public double FalseNegativeRate { get; set; }
        public double DetectionRate { get; set; }
        public TimeSpan AverageDetectionTime { get; set; }
        public double ComputationalCost { get; set; }
        public Dictionary<AnomalyType, double> TypeSpecificMetrics { get; set; } = new Dictionary<AnomalyType, double>();
        public ConfusionMatrix ConfusionMatrix { get; set; }
        public List<LearningCurve> LearningCurves { get; set; } = new List<LearningCurve>();
    }

    /// <summary>
    /// Güven metrikleri;
    /// </summary>
    public class ConfidenceMetrics;
    {
        public double ModelConfidence { get; set; }
        public double DataQuality { get; set; }
        public double PredictionStability { get; set; }
        public double HistoricalAccuracy { get; set; }
        public Dictionary<string, double> FeatureConfidence { get; set; } = new Dictionary<string, double>();
        public double OverallConfidence { get; set; }
    }

    /// <summary>
    /// AnomalyDetector ana implementasyonu;
    /// </summary>
    public class AnomalyDetector : IAnomalyDetector;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly Dictionary<string, DetectionModel> _loadedModels;
        private readonly MLContext _mlContext;
        private readonly IAnomalyModelFactory _modelFactory;
        private readonly IThresholdOptimizer _thresholdOptimizer;
        private readonly IPatternAnalyzer _patternAnalyzer;
        private readonly AdaptiveThresholdManager _thresholdManager;

        public AnomalyDetector(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IAnomalyModelFactory modelFactory,
            IThresholdOptimizer thresholdOptimizer,
            IPatternAnalyzer patternAnalyzer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _modelFactory = modelFactory ?? throw new ArgumentNullException(nameof(modelFactory));
            _thresholdOptimizer = thresholdOptimizer ?? throw new ArgumentNullException(nameof(thresholdOptimizer));
            _patternAnalyzer = patternAnalyzer ?? throw new ArgumentNullException(nameof(patternAnalyzer));

            _mlContext = new MLContext(seed: 42);
            _loadedModels = new Dictionary<string, DetectionModel>();
            _thresholdManager = new AdaptiveThresholdManager(logger);
        }

        public async Task<AnomalyDetectionResult> DetectAsync(DetectionRequest request)
        {
            var stopwatch = Stopwatch.StartNew();
            var detectionId = request.DetectionId;

            _logger.LogInformation($"Starting anomaly detection: {detectionId}");
            await _metricsCollector.RecordMetricAsync("anomaly_detection_started", 1);

            try
            {
                // 1. Veri doğrulama ve ön işleme;
                var processedData = await PreprocessDataAsync(request);

                // 2. Model yükleme veya seçimi;
                var detectionModel = await GetDetectionModelAsync(request);

                // 3. Tespit yöntemine göre analiz;
                var rawResult = await ExecuteDetectionAsync(processedData, detectionModel, request);

                // 4. Eşik optimizasyonu ve filtreleme;
                var filteredAnomalies = await ApplyThresholdsAsync(rawResult, request);

                // 5. Sonuçları zenginleştirme;
                var enrichedResult = await EnrichResultsAsync(filteredAnomalies, processedData, detectionModel, request);

                // 6. Metrik hesaplama;
                var metrics = await CalculateMetricsAsync(enrichedResult, processedData, detectionModel);

                // 7. Rapor oluşturma;
                var report = await GenerateReportAsync(enrichedResult, metrics, request);

                stopwatch.Stop();

                var result = new AnomalyDetectionResult;
                {
                    DetectionId = detectionId,
                    DetectionTime = DateTimeOffset.UtcNow,
                    Success = true,
                    Anomalies = enrichedResult,
                    Metrics = metrics,
                    Report = report,
                    OverallAnomalyScore = enrichedResult.Count > 0 ?
                        enrichedResult.Average(a => a.AnomalyScore) : 0,
                    Confidence = await CalculateConfidenceAsync(enrichedResult, detectionModel)
                };

                await _metricsCollector.RecordMetricAsync("anomaly_detection_completed", 1);
                await _metricsCollector.RecordMetricAsync("anomaly_detection_duration", stopwatch.ElapsedMilliseconds);
                await _metricsCollector.RecordMetricAsync("anomalies_detected", enrichedResult.Count);

                _logger.LogInformation($"Anomaly detection completed: {detectionId}. Found {enrichedResult.Count} anomalies.");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Anomaly detection failed: {ex.Message}", ex);
                await _metricsCollector.RecordMetricAsync("anomaly_detection_failed", 1);

                return new AnomalyDetectionResult;
                {
                    DetectionId = detectionId,
                    DetectionTime = DateTimeOffset.UtcNow,
                    Success = false,
                    Warnings = new List<string> { $"Detection failed: {ex.Message}" }
                };
            }
        }

        public async Task<ModelPerformance> TrainAsync(TrainingRequest request)
        {
            _logger.LogInformation($"Starting model training for anomaly detection");

            try
            {
                var model = await _modelFactory.CreateModelAsync(request.ModelType);

                // 1. Veri hazırlığı;
                var preparedData = await PrepareTrainingDataAsync(request);

                // 2. Model eğitimi;
                var trainedModel = await model.TrainAsync(preparedData, request.TrainingConfig);

                // 3. Model değerlendirme;
                var performance = await EvaluateModelAsync(trainedModel, request.ValidationData);

                // 4. Model kaydetme;
                await SaveModelAsync(new DetectionModel;
                {
                    ModelId = Guid.NewGuid().ToString(),
                    Model = trainedModel,
                    Performance = performance,
                    TrainingDate = DateTime.UtcNow,
                    ModelType = request.ModelType;
                });

                _logger.LogInformation($"Model training completed successfully");

                return performance;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Model training failed: {ex.Message}", ex);
                throw new AnomalyDetectionException($"Training failed: {ex.Message}", ex);
            }
        }

        public async Task<DetectionModel> LoadModelAsync(string modelId)
        {
            if (_loadedModels.TryGetValue(modelId, out var cachedModel))
            {
                return cachedModel;
            }

            // Modeli diskten veya veritabanından yükle;
            var model = await LoadModelFromStorageAsync(modelId);

            if (model != null)
            {
                _loadedModels[modelId] = model;
                _logger.LogDebug($"Model loaded: {modelId}");
            }

            return model;
        }

        public async Task SaveModelAsync(DetectionModel model)
        {
            // Modeli diske veya veritabanına kaydet;
            await SaveModelToStorageAsync(model);

            // Cache'e ekle;
            _loadedModels[model.ModelId] = model;

            _logger.LogDebug($"Model saved: {model.ModelId}");
        }

        public async Task<ThresholdOptimizationResult> OptimizeThresholdsAsync(ThresholdOptimizationRequest request)
        {
            return await _thresholdOptimizer.OptimizeAsync(request);
        }

        public async Task<RealTimeDetectionStream> CreateStreamingDetectorAsync(StreamingConfig config)
        {
            return new RealTimeDetectionStream(this, config, _logger);
        }

        public async Task<List<AnomalyPattern>> AnalyzePatternsAsync(PatternAnalysisRequest request)
        {
            return await _patternAnalyzer.AnalyzeAsync(request);
        }

        public async Task<AdaptiveThresholds> CalculateAdaptiveThresholdsAsync(AdaptiveThresholdRequest request)
        {
            return await _thresholdManager.CalculateAdaptiveThresholdsAsync(request);
        }

        #region Private Implementation Methods;

        private async Task<ProcessedData> PreprocessDataAsync(DetectionRequest request)
        {
            var stopwatch = Stopwatch.StartNew();

            _logger.LogDebug($"Preprocessing data for detection: {request.DetectionId}");

            try
            {
                var processedData = new ProcessedData;
                {
                    OriginalRequest = request,
                    PreprocessingStartTime = DateTimeOffset.UtcNow;
                };

                // 1. Veri temizleme;
                var cleanedData = await CleanDataAsync(request.Data);
                processedData.CleanedData = cleanedData;

                // 2. Normalizasyon;
                var normalizedData = await NormalizeDataAsync(cleanedData, request.Method);
                processedData.NormalizedData = normalizedData;

                // 3. Özellik çıkarımı;
                var features = await ExtractFeaturesAsync(normalizedData, request);
                processedData.Features = features;

                // 4. Pencereleme (time series için)
                if (IsTimeSeriesMethod(request.Method))
                {
                    var windows = await CreateWindowsAsync(normalizedData, request.WindowSize);
                    processedData.Windows = windows;
                }

                // 5. İstatistiksel özellikler;
                var statistics = await CalculateStatisticsAsync(cleanedData);
                processedData.Statistics = statistics;

                processedData.PreprocessingEndTime = DateTimeOffset.UtcNow;
                processedData.PreprocessingDuration = stopwatch.Elapsed;

                _logger.LogDebug($"Data preprocessing completed in {stopwatch.ElapsedMilliseconds}ms");

                return processedData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Data preprocessing failed: {ex.Message}", ex);
                throw new AnomalyDetectionException($"Data preprocessing failed: {ex.Message}", ex);
            }
        }

        private async Task<List<double>> CleanDataAsync(List<double> data)
        {
            // NaN ve Infinity değerleri temizle;
            var cleaned = data.Where(d => !double.IsNaN(d) && !double.IsInfinity(d)).ToList();

            // Aykırı değerleri filtrele (IQR yöntemi ile)
            if (cleaned.Count > 10)
            {
                var q1 = cleaned.Quantile(0.25);
                var q3 = cleaned.Quantile(0.75);
                var iqr = q3 - q1;
                var lowerBound = q1 - 1.5 * iqr;
                var upperBound = q3 + 1.5 * iqr;

                cleaned = cleaned.Where(d => d >= lowerBound && d <= upperBound).ToList();
            }

            return await Task.FromResult(cleaned);
        }

        private async Task<List<double>> NormalizeDataAsync(List<double> data, DetectionMethod method)
        {
            if (data.Count == 0) return data;

            switch (method)
            {
                case DetectionMethod.Statistical:
                case DetectionMethod.MachineLearning:
                    // Z-score normalizasyonu;
                    var mean = data.Average();
                    var stdDev = data.StandardDeviation();

                    if (stdDev == 0) return data;

                    return data.Select(d => (d - mean) / stdDev).ToList();

                case DetectionMethod.DeepLearning:
                case DetectionMethod.LSTM:
                    // Min-Max normalizasyonu;
                    var min = data.Min();
                    var max = data.Max();
                    var range = max - min;

                    if (range == 0) return data.Select(_ => 0.5).ToList();

                    return data.Select(d => (d - min) / range).ToList();

                default:
                    return data;
            }
        }

        private async Task<Dictionary<string, double>> ExtractFeaturesAsync(List<double> data, DetectionRequest request)
        {
            var features = new Dictionary<string, double>();

            if (data.Count < 2) return features;

            // Temel istatistiksel özellikler;
            features["mean"] = data.Average();
            features["std"] = data.StandardDeviation();
            features["variance"] = data.Variance();
            features["skewness"] = data.Skewness();
            features["kurtosis"] = data.Kurtosis();
            features["median"] = data.Median();
            features["q1"] = data.Quantile(0.25);
            features["q3"] = data.Quantile(0.75);
            features["min"] = data.Min();
            features["max"] = data.Max();
            features["range"] = features["max"] - features["min"];
            features["iqr"] = features["q3"] - features["q1"];

            // Zaman serisi özellikleri;
            if (IsTimeSeriesMethod(request.Method))
            {
                features["autocorr_lag1"] = CalculateAutocorrelation(data, 1);
                features["trend_strength"] = CalculateTrendStrength(data);
                features["seasonality_strength"] = CalculateSeasonalityStrength(data);
                features["stationarity"] = CalculateStationarity(data);
            }

            // Spektral özellikler;
            features["entropy"] = CalculateEntropy(data);
            features["energy"] = CalculateEnergy(data);

            return await Task.FromResult(features);
        }

        private async Task<DetectionModel> GetDetectionModelAsync(DetectionRequest request)
        {
            // 1. Özel model ID belirtilmişse onu yükle;
            if (!string.IsNullOrEmpty(request.ModelId))
            {
                var model = await LoadModelAsync(request.ModelId);
                if (model != null) return model;
            }

            // 2. Yönteme göre varsayılan model oluştur;
            var modelType = DetermineModelType(request.Method, request.Data.Count);
            return await _modelFactory.CreateModelAsync(modelType);
        }

        private async Task<List<DetectedAnomaly>> ExecuteDetectionAsync(
            ProcessedData data,
            DetectionModel model,
            DetectionRequest request)
        {
            switch (request.Method)
            {
                case DetectionMethod.Statistical:
                    return await DetectStatisticalAnomaliesAsync(data, request);

                case DetectionMethod.MachineLearning:
                    return await DetectWithMachineLearningAsync(data, model, request);

                case DetectionMethod.DeepLearning:
                case DetectionMethod.LSTM:
                    return await DetectWithDeepLearningAsync(data, model, request);

                case DetectionMethod.TimeSeries:
                    return await DetectTimeSeriesAnomaliesAsync(data, model, request);

                case DetectionMethod.Ensemble:
                    return await DetectWithEnsembleAsync(data, request);

                case DetectionMethod.AutoEncoder:
                    return await DetectWithAutoEncoderAsync(data, model, request);

                case DetectionMethod.IsolationForest:
                    return await DetectWithIsolationForestAsync(data, model, request);

                case DetectionMethod.Hybrid:
                    return await DetectWithHybridApproachAsync(data, model, request);

                default:
                    return await DetectStatisticalAnomaliesAsync(data, request);
            }
        }

        private async Task<List<DetectedAnomaly>> DetectStatisticalAnomaliesAsync(
            ProcessedData data,
            DetectionRequest request)
        {
            var anomalies = new List<DetectedAnomaly>();
            var scores = new List<double>();

            if (data.CleanedData.Count < 3)
            {
                _logger.LogWarning("Insufficient data for statistical anomaly detection");
                return anomalies;
            }

            var mean = data.Statistics["mean"];
            var stdDev = data.Statistics["std"];

            // Z-skor tabanlı anomali tespiti;
            for (int i = 0; i < data.CleanedData.Count; i++)
            {
                var value = data.CleanedData[i];
                var zScore = Math.Abs((value - mean) / stdDev);

                // Hassasiyete göre eşik belirle;
                var threshold = GetSensitivityThreshold(request.Sensitivity, StatisticalMethod.ZScore);

                if (zScore > threshold)
                {
                    var anomaly = new DetectedAnomaly;
                    {
                        Index = i,
                        Value = value,
                        ExpectedValue = mean,
                        AnomalyScore = zScore,
                        Confidence = CalculateStatisticalConfidence(zScore, threshold),
                        Type = AnomalyType.PointAnomaly,
                        Severity = DetermineSeverity(zScore, threshold)
                    };

                    anomalies.Add(anomaly);
                }

                scores.Add(zScore);
            }

            return await Task.FromResult(anomalies);
        }

        private async Task<List<DetectedAnomaly>> DetectWithMachineLearningAsync(
            ProcessedData data,
            DetectionModel model,
            DetectionRequest request)
        {
            var anomalies = new List<DetectedAnomaly>();

            try
            {
                // ML.NET ile anomali tespiti;
                var dataView = _mlContext.Data.LoadFromEnumerable(
                    data.CleanedData.Select((value, index) => new InputData { Value = value, Index = index }));

                // Anomali tespiti için pipeline;
                var pipeline = _mlContext.Transforms.DetectIidSpike(
                    outputColumnName: nameof(SpikePrediction.Prediction),
                    inputColumnName: nameof(InputData.Value),
                    confidence: 95,
                    pvalueHistoryLength: request.WindowSize / 2);

                var transformedData = pipeline.Fit(dataView).Transform(dataView);

                var predictions = _mlContext.Data.CreateEnumerable<SpikePrediction>(
                    transformedData, reuseRowObject: false).ToList();

                for (int i = 0; i < predictions.Count; i++)
                {
                    var prediction = predictions[i];

                    if (prediction.Prediction[0] == 1) // Spike detected;
                    {
                        var anomaly = new DetectedAnomaly;
                        {
                            Index = i,
                            Value = data.CleanedData[i],
                            ExpectedValue = prediction.Prediction[1], // Expected value;
                            AnomalyScore = prediction.Prediction[2], // P-Value;
                            Confidence = 1 - prediction.Prediction[2], // Confidence from P-Value;
                            Type = AnomalyType.PointAnomaly,
                            Severity = DetermineSeverityFromPValue(prediction.Prediction[2])
                        };

                        anomalies.Add(anomaly);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Machine learning detection failed: {ex.Message}", ex);
            }

            return await Task.FromResult(anomalies);
        }

        private async Task<List<DetectedAnomaly>> DetectWithDeepLearningAsync(
            ProcessedData data,
            DetectionModel model,
            DetectionRequest request)
        {
            var anomalies = new List<DetectedAnomaly>();

            try
            {
                // Autoencoder tabanlı anomali tespiti;
                if (model.Model is NeuralNetwork neuralNetwork)
                {
                    var reconstructionErrors = new List<double>();

                    // Her data point için reconstruction error hesapla;
                    foreach (var value in data.NormalizedData)
                    {
                        var input = new[] { value };
                        var output = await neuralNetwork.PredictAsync(input);
                        var error = CalculateReconstructionError(input, output);
                        reconstructionErrors.Add(error);
                    }

                    // Reconstruction error'ların dağılımına göre anomali tespiti;
                    var meanError = reconstructionErrors.Average();
                    var stdError = reconstructionErrors.StandardDeviation();

                    for (int i = 0; i < reconstructionErrors.Count; i++)
                    {
                        var error = reconstructionErrors[i];
                        var zScore = (error - meanError) / stdError;

                        var threshold = GetSensitivityThreshold(
                            request.Sensitivity,
                            StatisticalMethod.AutoEncoder);

                        if (zScore > threshold)
                        {
                            var anomaly = new DetectedAnomaly;
                            {
                                Index = i,
                                Value = data.CleanedData[i],
                                ExpectedValue = meanError,
                                AnomalyScore = zScore,
                                Confidence = 1 - (2 * Statistics.CDF.Normal(0, 1, -zScore)),
                                Type = AnomalyType.PointAnomaly,
                                Severity = DetermineSeverity(zScore, threshold)
                            };

                            anomalies.Add(anomaly);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Deep learning detection failed: {ex.Message}", ex);
            }

            return await Task.FromResult(anomalies);
        }

        private async Task<List<DetectedAnomaly>> DetectTimeSeriesAnomaliesAsync(
            ProcessedData data,
            DetectionModel model,
            DetectionRequest request)
        {
            var anomalies = new List<DetectedAnomaly>();

            try
            {
                // SRCNN (Spectral Residual CNN) ile anomali tespiti;
                var pipeline = _mlContext.Transforms.DetectAnomalyBySrCnn(
                    outputColumnName: nameof(SrCnnPrediction.Prediction),
                    inputColumnName: nameof(InputData.Value),
                    windowSize: request.WindowSize,
                    backAddWindowSize: request.WindowSize / 2,
                    lookaheadWindowSize: request.PredictionHorizon,
                    threshold: 0.3,
                    sensitivity: (int)request.Sensitivity * 20);

                var dataView = _mlContext.Data.LoadFromEnumerable(
                    data.CleanedData.Select((value, index) => new InputData { Value = value, Index = index }));

                var transformedData = pipeline.Fit(dataView).Transform(dataView);

                var predictions = _mlContext.Data.CreateEnumerable<SrCnnPrediction>(
                    transformedData, reuseRowObject: false).ToList();

                for (int i = 0; i < predictions.Count; i++)
                {
                    var prediction = predictions[i];

                    if (prediction.Prediction[0] == 1) // Anomaly detected;
                    {
                        var anomaly = new DetectedAnomaly;
                        {
                            Index = i,
                            Value = data.CleanedData[i],
                            ExpectedValue = prediction.Prediction[1],
                            AnomalyScore = prediction.Prediction[2],
                            Confidence = prediction.Prediction[3],
                            Type = DetermineTimeSeriesAnomalyType(prediction.Prediction, data.CleanedData, i),
                            Severity = DetermineSeverityFromScore(prediction.Prediction[2])
                        };

                        anomalies.Add(anomaly);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Time series detection failed: {ex.Message}", ex);
            }

            return await Task.FromResult(anomalies);
        }

        private async Task<List<DetectedAnomaly>> ApplyThresholdsAsync(
            List<DetectedAnomaly> rawAnomalies,
            DetectionRequest request)
        {
            if (!rawAnomalies.Any()) return rawAnomalies;

            var filteredAnomalies = new List<DetectedAnomaly>();

            // Uyarlamalı eşik kullan;
            if (request.UseAdaptiveThreshold)
            {
                var adaptiveThresholds = await _thresholdManager.GetAdaptiveThresholdsAsync(
                    rawAnomalies.Select(a => a.AnomalyScore).ToList(),
                    request.Sensitivity);

                foreach (var anomaly in rawAnomalies)
                {
                    var threshold = adaptiveThresholds.GetThresholdForType(anomaly.Type);

                    if (anomaly.AnomalyScore >= threshold)
                    {
                        filteredAnomalies.Add(anomaly);
                    }
                }
            }
            else;
            {
                // Sabit eşik kullan;
                var threshold = GetStaticThreshold(request.Sensitivity, request.Method);

                filteredAnomalies = rawAnomalies;
                    .Where(a => a.AnomalyScore >= threshold)
                    .ToList();
            }

            // Özel eşikler uygula;
            if (request.CustomThresholds.Any())
            {
                foreach (var kvp in request.CustomThresholds)
                {
                    var typeAnomalies = filteredAnomalies.Where(a => a.Type == kvp.Key);
                    foreach (var anomaly in typeAnomalies)
                    {
                        if (anomaly.AnomalyScore < kvp.Value)
                        {
                            filteredAnomalies.Remove(anomaly);
                        }
                    }
                }
            }

            return filteredAnomalies;
        }

        private async Task<List<DetectedAnomaly>> EnrichResultsAsync(
            List<DetectedAnomaly> anomalies,
            ProcessedData data,
            DetectionModel model,
            DetectionRequest request)
        {
            var enrichedAnomalies = new List<DetectedAnomaly>();

            foreach (var anomaly in anomalies)
            {
                var enrichedAnomaly = await EnrichAnomalyAsync(anomaly, data, model, request);
                enrichedAnomalies.Add(enrichedAnomaly);
            }

            return enrichedAnomalies;
        }

        private async Task<DetectedAnomaly> EnrichAnomalyAsync(
            DetectedAnomaly anomaly,
            ProcessedData data,
            DetectionModel model,
            DetectionRequest request)
        {
            // 1. Context ekle;
            if (request.Context != null)
            {
                anomaly.Context = new Dictionary<string, object>(request.Context);
            }

            // 2. Açıklama oluştur;
            if (request.ReturnExplanation)
            {
                anomaly.Explanation = await GenerateExplanationAsync(anomaly, data, request.Method);
            }

            // 3. İlişkili anomalileri bul;
            anomaly.CorrelatedAnomalies = await FindCorrelatedAnomaliesAsync(anomaly, anomalies, data);

            // 4. Kök neden analizi;
            anomaly.RootCause = await AnalyzeRootCauseAsync(anomaly, data);

            // 5. Önerilen aksiyon;
            anomaly.RecommendedAction = await DetermineRecommendedActionAsync(anomaly, request);

            return anomaly;
        }

        private async Task<DetectionMetrics> CalculateMetricsAsync(
            List<DetectedAnomaly> anomalies,
            ProcessedData data,
            DetectionModel model)
        {
            var metrics = new DetectionMetrics();

            if (!anomalies.Any()) return metrics;

            // Precision, Recall, F1 Score hesaplama;
            // Burada ground truth verisi olmadığı için simüle ediyoruz;
            // Gerçek kullanımda ground truth ile karşılaştırma yapılmalı;

            var scores = anomalies.Select(a => a.AnomalyScore).ToList();
            var confidences = anomalies.Select(a => a.Confidence).ToList();

            metrics.Precision = confidences.Average();
            metrics.Recall = anomalies.Count / (double)data.CleanedData.Count * 100;
            metrics.F1Score = 2 * (metrics.Precision * metrics.Recall) / (metrics.Precision + metrics.Recall);
            metrics.AverageDetectionTime = TimeSpan.FromMilliseconds(anomalies.Average(a => a.AnomalyScore) * 100);

            // Anomali tipine göre metrikler;
            var groupedByType = anomalies.GroupBy(a => a.Type);
            foreach (var group in groupedByType)
            {
                metrics.TypeSpecificMetrics[group.Key] = group.Average(a => a.Confidence);
            }

            return await Task.FromResult(metrics);
        }

        private async Task<AnomalyReport> GenerateReportAsync(
            List<DetectedAnomaly> anomalies,
            DetectionMetrics metrics,
            DetectionRequest request)
        {
            var report = new AnomalyReport;
            {
                ReportId = Guid.NewGuid().ToString(),
                GenerationTime = DateTimeOffset.UtcNow,
                DetectionRequest = request,
                Summary = new DetectionSummary;
                {
                    TotalAnomalies = anomalies.Count,
                    CriticalAnomalies = anomalies.Count(a => a.Severity == SeverityLevel.Critical),
                    HighSeverityAnomalies = anomalies.Count(a => a.Severity == SeverityLevel.High),
                    AverageConfidence = anomalies.Any() ? anomalies.Average(a => a.Confidence) : 0,
                    DetectionRate = metrics.DetectionRate;
                },
                AnomaliesByType = anomalies.GroupBy(a => a.Type)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AnomaliesBySeverity = anomalies.GroupBy(a => a.Severity)
                    .ToDictionary(g => g.Key, g => g.Count()),
                Recommendations = anomalies.Select(a => a.RecommendedAction)
                    .Distinct()
                    .ToList(),
                Metrics = metrics;
            };

            return await Task.FromResult(report);
        }

        private async Task<ConfidenceMetrics> CalculateConfidenceAsync(
            List<DetectedAnomaly> anomalies,
            DetectionModel model)
        {
            var confidence = new ConfidenceMetrics();

            if (!anomalies.Any())
            {
                confidence.OverallConfidence = 1.0; // No anomalies = high confidence;
                return confidence;
            }

            confidence.ModelConfidence = model?.Performance?.Accuracy ?? 0.8;
            confidence.DataQuality = CalculateDataQuality(anomalies);
            confidence.PredictionStability = CalculatePredictionStability(anomalies);
            confidence.HistoricalAccuracy = await GetHistoricalAccuracyAsync(model);
            confidence.OverallConfidence = (
                confidence.ModelConfidence * 0.4 +
                confidence.DataQuality * 0.3 +
                confidence.PredictionStability * 0.2 +
                confidence.HistoricalAccuracy * 0.1;
            );

            return confidence;
        }

        #endregion;

        #region Helper Methods;

        private bool IsTimeSeriesMethod(DetectionMethod method)
        {
            return method == DetectionMethod.TimeSeries ||
                   method == DetectionMethod.LSTM ||
                   method == DetectionMethod.Prophet ||
                   method == DetectionMethod.SARIMA;
        }

        private double CalculateReconstructionError(double[] input, double[] output)
        {
            if (input.Length != output.Length) return double.MaxValue;

            var squaredErrors = input.Zip(output, (i, o) => Math.Pow(i - o, 2));
            return Math.Sqrt(squaredErrors.Sum() / input.Length);
        }

        private double CalculateAutocorrelation(List<double> data, int lag)
        {
            if (data.Count <= lag) return 0;

            var mean = data.Average();
            var variance = data.Variance();

            if (variance == 0) return 0;

            double autocovariance = 0;
            for (int i = 0; i < data.Count - lag; i++)
            {
                autocovariance += (data[i] - mean) * (data[i + lag] - mean);
            }

            autocovariance /= (data.Count - lag);

            return autocovariance / variance;
        }

        private double CalculateTrendStrength(List<double> data)
        {
            if (data.Count < 2) return 0;

            var x = Enumerable.Range(0, data.Count).Select(i => (double)i).ToArray();
            var y = data.ToArray();

            var slope = Fit.Line(x, y).Item2;
            var yMean = y.Average();
            var ssTotal = y.Sum(yi => Math.Pow(yi - yMean, 2));
            var ssResidual = y.Zip(x, (yi, xi) => Math.Pow(yi - (slope * xi + y[0]), 2)).Sum();

            return 1 - (ssResidual / ssTotal);
        }

        private double CalculateSeasonalityStrength(List<double> data)
        {
            if (data.Count < 12) return 0; // En az 12 nokta gerekiyor;

            // Basit mevsimsellik gücü hesaplama;
            var seasonalIndices = new List<double>();
            int seasonLength = Math.Min(12, data.Count / 2);

            for (int i = 0; i < seasonLength; i++)
            {
                var seasonalValues = new List<double>();
                for (int j = i; j < data.Count; j += seasonLength)
                {
                    seasonalValues.Add(data[j]);
                }
                if (seasonalValues.Any())
                {
                    seasonalIndices.Add(seasonalValues.Average() / data.Average());
                }
            }

            var variance = seasonalIndices.Variance();
            return Math.Min(1, variance * 10); // Normalize edilmiş değer;
        }

        private double CalculateStationarity(List<double> data)
        {
            // Augmented Dickey-Fuller test benzeri basit istatistik;
            var differences = new List<double>();
            for (int i = 1; i < data.Count; i++)
            {
                differences.Add(data[i] - data[i - 1]);
            }

            var diffVariance = differences.Variance();
            var dataVariance = data.Variance();

            if (dataVariance == 0) return 1;

            return 1 - (diffVariance / dataVariance);
        }

        private double CalculateEntropy(List<double> data)
        {
            if (data.Count == 0) return 0;

            // Shannon entropisi (basitleştirilmiş)
            var normalized = data.Select(d => Math.Abs(d)).ToArray();
            var sum = normalized.Sum();
            if (sum == 0) return 0;

            var probabilities = normalized.Select(d => d / sum).ToArray();
            var entropy = 0.0;

            foreach (var p in probabilities)
            {
                if (p > 0)
                {
                    entropy -= p * Math.Log(p, 2);
                }
            }

            return entropy / Math.Log(data.Count, 2); // Normalize edilmiş entropi;
        }

        private double CalculateEnergy(List<double> data)
        {
            return data.Sum(d => d * d) / data.Count;
        }

        private double GetSensitivityThreshold(DetectionSensitivity sensitivity, StatisticalMethod method)
        {
            // Hassasiyete göre eşik değerleri;
            var baseThresholds = new Dictionary<DetectionSensitivity, double>
            {
                [DetectionSensitivity.VeryLow] = 3.5,
                [DetectionSensitivity.Low] = 3.0,
                [DetectionSensitivity.Medium] = 2.5,
                [DetectionSensitivity.High] = 2.0,
                [DetectionSensitivity.VeryHigh] = 1.5,
                [DetectionSensitivity.Critical] = 1.0;
            };

            var threshold = baseThresholds[sensitivity];

            // Yönteme göre ayarlama;
            switch (method)
            {
                case StatisticalMethod.AutoEncoder:
                    return threshold * 1.2;
                case StatisticalMethod.IsolationForest:
                    return threshold * 0.8;
                default:
                    return threshold;
            }
        }

        private double GetStaticThreshold(DetectionSensitivity sensitivity, DetectionMethod method)
        {
            var baseThreshold = GetSensitivityThreshold(sensitivity, StatisticalMethod.ZScore);

            return method switch;
            {
                DetectionMethod.DeepLearning => baseThreshold * 1.1,
                DetectionMethod.TimeSeries => baseThreshold * 0.9,
                DetectionMethod.Ensemble => baseThreshold * 0.85,
                _ => baseThreshold;
            };
        }

        private double CalculateStatisticalConfidence(double zScore, double threshold)
        {
            // Z-skor'dan güven aralığı hesaplama;
            var pValue = 2 * (1 - Statistics.CDF.Normal(0, 1, Math.Abs(zScore)));
            var confidence = 1 - pValue;

            // Threshold'a göre normalize et;
            return Math.Max(0, Math.Min(1, confidence * (threshold / 2.5)));
        }

        private SeverityLevel DetermineSeverity(double zScore, double threshold)
        {
            var ratio = zScore / threshold;

            return ratio switch;
            {
                >= 3.0 => SeverityLevel.Critical,
                >= 2.0 => SeverityLevel.High,
                >= 1.5 => SeverityLevel.Medium,
                >= 1.0 => SeverityLevel.Low,
                _ => SeverityLevel.Info;
            };
        }

        private SeverityLevel DetermineSeverityFromPValue(double pValue)
        {
            return pValue switch;
            {
                < 0.001 => SeverityLevel.Critical,
                < 0.01 => SeverityLevel.High,
                < 0.05 => SeverityLevel.Medium,
                < 0.1 => SeverityLevel.Low,
                _ => SeverityLevel.Info;
            };
        }

        private SeverityLevel DetermineSeverityFromScore(double score)
        {
            return score switch;
            {
                >= 0.9 => SeverityLevel.Critical,
                >= 0.7 => SeverityLevel.High,
                >= 0.5 => SeverityLevel.Medium,
                >= 0.3 => SeverityLevel.Low,
                _ => SeverityLevel.Info;
            };
        }

        private AnomalyType DetermineTimeSeriesAnomalyType(double[] prediction, List<double> data, int index)
        {
            var score = prediction[2];
            var mag = prediction[1];

            if (index < 2 || index >= data.Count - 2) return AnomalyType.PointAnomaly;

            // Çevresindeki değerlere bakarak anomali tipini belirle;
            var prev1 = data[index - 1];
            var prev2 = data[index - 2];
            var next1 = data[index + 1];
            var next2 = data[index + 2];
            var current = data[index];

            var localMean = (prev1 + prev2 + next1 + next2) / 4;
            var localStd = Math.Sqrt(
                (Math.Pow(prev1 - localMean, 2) + Math.Pow(prev2 - localMean, 2) +
                 Math.Pow(next1 - localMean, 2) + Math.Pow(next2 - localMean, 2)) / 4);

            if (localStd == 0) return AnomalyType.PointAnomaly;

            var localZ = Math.Abs(current - localMean) / localStd;

            if (localZ > 3) return AnomalyType.PointAnomaly;

            // Trend anomalisi kontrolü;
            var trend = (next1 - prev1) / 2.0;
            if (Math.Abs(trend) > localStd * 2) return AnomalyType.TrendAnomaly;

            return AnomalyType.ContextualAnomaly;
        }

        private async Task<string> GenerateExplanationAsync(
            DetectedAnomaly anomaly,
            ProcessedData data,
            DetectionMethod method)
        {
            var explanation = new List<string>
            {
                $"Detected {anomaly.Type.ToString().ToLower()} anomaly with severity: {anomaly.Severity}"
            };

            if (data.Statistics != null)
            {
                var zScore = Math.Abs((anomaly.Value - data.Statistics["mean"]) / data.Statistics["std"]);
                explanation.Add($"Z-score: {zScore:F2}");
                explanation.Add($"Value {anomaly.Value:F2} is {zScore:F2} standard deviations from mean {data.Statistics["mean"]:F2}");
            }

            explanation.Add($"Confidence: {anomaly.Confidence:P0}");
            explanation.Add($"Detection method: {method}");

            return string.Join(". ", explanation);
        }

        private async Task<List<CorrelatedAnomaly>> FindCorrelatedAnomaliesAsync(
            DetectedAnomaly anomaly,
            List<DetectedAnomaly> allAnomalies,
            ProcessedData data)
        {
            var correlated = new List<CorrelatedAnomaly>();

            // Belirli bir zaman penceresindeki diğer anomalileri bul;
            var windowStart = Math.Max(0, anomaly.Index - 10);
            var windowEnd = Math.Min(data.CleanedData.Count - 1, anomaly.Index + 10);

            var windowAnomalies = allAnomalies;
                .Where(a => a.Index >= windowStart && a.Index <= windowEnd && a.Index != anomaly.Index)
                .ToList();

            foreach (var other in windowAnomalies)
            {
                var distance = Math.Abs(other.Index - anomaly.Index);
                var scoreCorrelation = Math.Abs(other.AnomalyScore - anomaly.AnomalyScore) /
                                      Math.Max(other.AnomalyScore, anomaly.AnomalyScore);

                if (distance <= 5 && scoreCorrelation < 0.5)
                {
                    correlated.Add(new CorrelatedAnomaly;
                    {
                        Index = other.Index,
                        AnomalyScore = other.AnomalyScore,
                        Type = other.Type,
                        Distance = distance,
                        CorrelationStrength = 1 - scoreCorrelation;
                    });
                }
            }

            return correlated;
        }

        private async Task<string> AnalyzeRootCauseAsync(DetectedAnomaly anomaly, ProcessedData data)
        {
            var rootCauses = new List<string>();

            // İstatistiksel analiz;
            if (anomaly.Value > data.Statistics["q3"] + 3 * data.Statistics["iqr"])
            {
                rootCauses.Add("Extreme outlier beyond 3*IQR");
            }
            else if (anomaly.Value > data.Statistics["max"] * 0.9)
            {
                rootCauses.Add("Near maximum value");
            }
            else if (anomaly.Value < data.Statistics["min"] * 1.1)
            {
                rootCauses.Add("Near minimum value");
            }

            // Pattern analizi;
            if (anomaly.Type == AnomalyType.TrendAnomaly)
            {
                rootCauses.Add("Sudden trend change detected");
            }
            else if (anomaly.Type == AnomalyType.SeasonalAnomaly)
            {
                rootCauses.Add("Seasonal pattern violation");
            }

            return rootCauses.Any() ?
                string.Join("; ", rootCauses) :
                "Unknown root cause - requires further investigation";
        }

        private async Task<RecommendedAction> DetermineRecommendedActionAsync(
            DetectedAnomaly anomaly,
            DetectionRequest request)
        {
            var action = new RecommendedAction;
            {
                AnomalyId = $"{request.DetectionId}_{anomaly.Index}",
                Timestamp = DateTimeOffset.UtcNow;
            };

            switch (anomaly.Severity)
            {
                case SeverityLevel.Critical:
                    action.Priority = ActionPriority.Immediate;
                    action.Actions = new List<string>
                    {
                        "Immediate system shutdown if safety critical",
                        "Notify on-call engineer",
                        "Begin automated recovery procedures",
                        "Log detailed diagnostics"
                    };
                    action.Timeframe = "Immediately";
                    break;

                case SeverityLevel.High:
                    action.Priority = ActionPriority.High;
                    action.Actions = new List<string>
                    {
                        "Schedule maintenance window",
                        "Increase monitoring frequency",
                        "Prepare rollback plan",
                        "Collect additional metrics"
                    };
                    action.Timeframe = "Within 1 hour";
                    break;

                case SeverityLevel.Medium:
                    action.Priority = ActionPriority.Medium;
                    action.Actions = new List<string>
                    {
                        "Add to next review meeting",
                        "Monitor for recurrence",
                        "Update anomaly detection thresholds",
                        "Document findings"
                    };
                    action.Timeframe = "Within 24 hours";
                    break;

                case SeverityLevel.Low:
                case SeverityLevel.Info:
                    action.Priority = ActionPriority.Low;
                    action.Actions = new List<string>
                    {
                        "Add to weekly report",
                        "Continue normal monitoring",
                        "Update historical patterns"
                    };
                    action.Timeframe = "Within 1 week";
                    break;
            }

            return action;
        }

        private double CalculateDataQuality(List<DetectedAnomaly> anomalies)
        {
            if (!anomalies.Any()) return 1.0;

            // Daha az anomali = daha yüksek veri kalitesi;
            var anomalyRatio = anomalies.Count / 100.0; // 100 data point varsayımı;
            return Math.Max(0, 1 - anomalyRatio);
        }

        private double CalculatePredictionStability(List<DetectedAnomaly> anomalies)
        {
            if (anomalies.Count < 2) return 1.0;

            var scores = anomalies.Select(a => a.AnomalyScore).ToList();
            var mean = scores.Average();
            var variance = scores.Variance();

            if (mean == 0) return 1.0;

            var coefficientOfVariation = Math.Sqrt(variance) / mean;
            return Math.Max(0, 1 - coefficientOfVariation);
        }

        private async Task<double> GetHistoricalAccuracyAsync(DetectionModel model)
        {
            if (model?.Performance == null) return 0.8;

            return model.Performance.HistoricalAccuracy;
        }

        #endregion;

        #region Nested Classes;

        private class ProcessedData;
        {
            public DetectionRequest OriginalRequest { get; set; }
            public List<double> CleanedData { get; set; }
            public List<double> NormalizedData { get; set; }
            public Dictionary<string, double> Features { get; set; }
            public Dictionary<string, double> Statistics { get; set; }
            public List<List<double>> Windows { get; set; }
            public DateTimeOffset PreprocessingStartTime { get; set; }
            public DateTimeOffset PreprocessingEndTime { get; set; }
            public TimeSpan PreprocessingDuration { get; set; }
        }

        private class InputData;
        {
            [LoadColumn(0)]
            public float Value { get; set; }

            [LoadColumn(1)]
            public int Index { get; set; }
        }

        private class SpikePrediction;
        {
            [VectorType(3)]
            public double[] Prediction { get; set; }
        }

        private class SrCnnPrediction;
        {
            [VectorType(4)]
            public double[] Prediction { get; set; }
        }

        private enum StatisticalMethod;
        {
            ZScore,
            IQR,
            AutoEncoder,
            IsolationForest;
        }

        #endregion;
    }

    /// <summary>
    /// Anomali tespit modeli;
    /// </summary>
    public class DetectionModel;
    {
        public string ModelId { get; set; }
        public object Model { get; set; } // ML.NET model veya NeuralNetwork;
        public ModelPerformance Performance { get; set; }
        public DateTime TrainingDate { get; set; }
        public DetectionMethod ModelType { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Model performansı;
    /// </summary>
    public class ModelPerformance;
    {
        public double Accuracy { get; set; }
        public double Precision { get; set; }
        public double Recall { get; set; }
        public double F1Score { get; set; }
        public double AUC { get; set; }
        public double FalsePositiveRate { get; set; }
        public double TrainingTime { get; set; }
        public double InferenceTime { get; set; }
        public Dictionary<string, double> FeatureImportance { get; set; } = new Dictionary<string, double>();
        public double HistoricalAccuracy { get; set; }
        public List<ValidationResult> ValidationResults { get; set; } = new List<ValidationResult>();
    }

    /// <summary>
    /// Anomali raporu;
    /// </summary>
    public class AnomalyReport;
    {
        public string ReportId { get; set; }
        public DateTimeOffset GenerationTime { get; set; }
        public DetectionRequest DetectionRequest { get; set; }
        public DetectionSummary Summary { get; set; }
        public Dictionary<AnomalyType, int> AnomaliesByType { get; set; }
        public Dictionary<SeverityLevel, int> AnomaliesBySeverity { get; set; }
        public List<RecommendedAction> Recommendations { get; set; }
        public DetectionMetrics Metrics { get; set; }
    }

    /// <summary>
    /// Tespit özeti;
    /// </summary>
    public class DetectionSummary;
    {
        public int TotalAnomalies { get; set; }
        public int CriticalAnomalies { get; set; }
        public int HighSeverityAnomalies { get; set; }
        public double AverageConfidence { get; set; }
        public double DetectionRate { get; set; }
    }

    /// <summary>
    /// Önerilen aksiyon;
    /// </summary>
    public class RecommendedAction;
    {
        public string AnomalyId { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public ActionPriority Priority { get; set; }
        public List<string> Actions { get; set; }
        public string Timeframe { get; set; }
    }

    /// <summary>
    /// Aksiyon önceliği;
    /// </summary>
    public enum ActionPriority;
    {
        Immediate,
        High,
        Medium,
        Low;
    }

    /// <summary>
    /// İlişkili anomali;
    /// </summary>
    public class CorrelatedAnomaly;
    {
        public int Index { get; set; }
        public double AnomalyScore { get; set; }
        public AnomalyType Type { get; set; }
        public int Distance { get; set; }
        public double CorrelationStrength { get; set; }
    }

    /// <summary>
    /// Anomali tespit istisnası;
    /// </summary>
    public class AnomalyDetectionException : Exception
    {
        public AnomalyDetectionException(string message) : base(message) { }
        public AnomalyDetectionException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Gerçek zamanlı tespit akışı;
    /// </summary>
    public class RealTimeDetectionStream : IDisposable
    {
        private readonly IAnomalyDetector _detector;
        private readonly StreamingConfig _config;
        private readonly ILogger _logger;
        private readonly Queue<double> _buffer;
        private Timer _detectionTimer;
        private bool _isRunning;

        public event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;

        public RealTimeDetectionStream(IAnomalyDetector detector, StreamingConfig config, ILogger logger)
        {
            _detector = detector;
            _config = config;
            _logger = logger;
            _buffer = new Queue<double>(config.BufferSize);
        }

        public void Start()
        {
            if (_isRunning) return;

            _detectionTimer = new Timer(PerformDetection, null, 0, _config.DetectionIntervalMs);
            _isRunning = true;

            _logger.LogInformation($"Real-time anomaly detection stream started with interval: {_config.DetectionIntervalMs}ms");
        }

        public void Stop()
        {
            _detectionTimer?.Dispose();
            _isRunning = false;

            _logger.LogInformation("Real-time anomaly detection stream stopped");
        }

        public void AddDataPoint(double value)
        {
            lock (_buffer)
            {
                if (_buffer.Count >= _config.BufferSize)
                {
                    _buffer.Dequeue();
                }
                _buffer.Enqueue(value);
            }
        }

        private async void PerformDetection(object state)
        {
            if (_buffer.Count < _config.MinDataPoints) return;

            List<double> data;
            lock (_buffer)
            {
                data = _buffer.ToList();
            }

            try
            {
                var request = new DetectionRequest;
                {
                    Method = _config.DetectionMethod,
                    Data = data,
                    Sensitivity = _config.Sensitivity,
                    WindowSize = _config.WindowSize;
                };

                var result = await _detector.DetectAsync(request);

                foreach (var anomaly in result.Anomalies)
                {
                    OnAnomalyDetected(new AnomalyDetectedEventArgs;
                    {
                        Anomaly = anomaly,
                        DetectionResult = result,
                        Timestamp = DateTimeOffset.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Real-time detection failed: {ex.Message}", ex);
            }
        }

        protected virtual void OnAnomalyDetected(AnomalyDetectedEventArgs e)
        {
            AnomalyDetected?.Invoke(this, e);
        }

        public void Dispose()
        {
            Stop();
            _detectionTimer?.Dispose();
        }
    }

    /// <summary>
    /// Akış yapılandırması;
    /// </summary>
    public class StreamingConfig;
    {
        public int DetectionIntervalMs { get; set; } = 1000;
        public int BufferSize { get; set; } = 1000;
        public int MinDataPoints { get; set; } = 100;
        public DetectionMethod DetectionMethod { get; set; } = DetectionMethod.Statistical;
        public DetectionSensitivity Sensitivity { get; set; } = DetectionSensitivity.Medium;
        public int WindowSize { get; set; } = 100;
    }

    /// <summary>
    /// Anomali tespit edildi olayı argümanları;
    /// </summary>
    public class AnomalyDetectedEventArgs : EventArgs;
    {
        public DetectedAnomaly Anomaly { get; set; }
        public AnomalyDetectionResult DetectionResult { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }
}
