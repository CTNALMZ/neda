using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Core.Common;
using NEDA.NeuralNetwork.PatternRecognition;
using NEDA.NeuralNetwork.AdaptiveLearning;
using NEDA.NeuralNetwork.CognitiveModels;

namespace NEDA.NeuralNetwork.PatternRecognition.AnomalyDetection;
{
    /// <summary>
    /// Sapma ve anomali analiz motoru;
    /// Normal davranış modellerinden sapmaları tespit eder ve analiz eder;
    /// </summary>
    public class DeviationAnalyzer : IDeviationAnalyzer, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IPatternRecognizer _patternRecognizer;
        private readonly IAnomalyDetector _anomalyDetector;
        private readonly List<DeviationPattern> _deviationPatterns;
        private readonly Dictionary<string, StatisticalModel> _statisticalModels;
        private readonly object _syncLock = new object();
        private readonly AdaptiveThresholdManager _thresholdManager;

        /// <summary>
        /// Sapma analiz konfigürasyonu;
        /// </summary>
        public DeviationAnalysisConfiguration Configuration { get; private set; }

        /// <summary>
        /// Analiz durumu;
        /// </summary>
        public AnalysisStatus Status { get; private set; }

        /// <summary>
        /// Toplam analiz edilen veri noktası sayısı;
        /// </summary>
        public long TotalDataPointsAnalyzed { get; private set; }

        /// <summary>
        /// Tespit edilen anomali sayısı;
        /// </summary>
        public long DetectedAnomalies { get; private set; }

        /// <summary>
        /// Son analiz metrikleri;
        /// </summary>
        public AnalysisMetrics LatestMetrics { get; private set; }

        /// <summary>
        /// Öğrenilmiş normal davranış modelleri;
        /// </summary>
        public IReadOnlyDictionary<string, BehavioralModel> NormalBehaviorModels { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Sapma tespit edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<DeviationDetectedEventArgs> DeviationDetected;

        /// <summary>
        /// Anomali tespit edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AnomalyIdentifiedEventArgs> AnomalyIdentified;

        /// <summary>
        /// Eşik değeri ayarlandığında tetiklenir;
        /// </summary>
        public event EventHandler<ThresholdAdjustedEventArgs> ThresholdAdjusted;

        /// <summary>
        /// Patern öğrenildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<PatternLearnedEventArgs> PatternLearned;

        /// <summary>
        /// Analiz tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<AnalysisCompletedEventArgs> AnalysisCompleted;

        #endregion;

        #region Constructors;

        /// <summary>
        /// DeviationAnalyzer sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="patternRecognizer">Patern tanıma servisi</param>
        /// <param name="anomalyDetector">Anomali tespit servisi</param>
        public DeviationAnalyzer(
            ILogger logger,
            IPatternRecognizer patternRecognizer,
            IAnomalyDetector anomalyDetector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _patternRecognizer = patternRecognizer ?? throw new ArgumentNullException(nameof(patternRecognizer));
            _anomalyDetector = anomalyDetector ?? throw new ArgumentNullException(nameof(anomalyDetector));

            _deviationPatterns = new List<DeviationPattern>();
            _statisticalModels = new Dictionary<string, StatisticalModel>();
            _thresholdManager = new AdaptiveThresholdManager(logger);

            Configuration = new DeviationAnalysisConfiguration;
            {
                AnalysisMode = AnalysisMode.RealTime,
                SensitivityLevel = SensitivityLevel.Medium,
                ConfidenceThreshold = 0.85,
                WindowSize = 100,
                LearningRate = 0.01,
                AdaptiveThresholding = true,
                MultiDimensionalAnalysis = true,
                TimeSeriesAnalysis = true,
                StatisticalMethod = StatisticalMethod.ZScore,
                MaximumDeviations = 3,
                MinimumDataPoints = 50,
                AutoCalibration = true;
            };

            Status = AnalysisStatus.Idle;
            LatestMetrics = new AnalysisMetrics();
            NormalBehaviorModels = new Dictionary<string, BehavioralModel>();

            _logger.Info("DeviationAnalyzer initialized successfully.");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Normal davranış modellerini öğrenir;
        /// </summary>
        /// <param name="trainingData">Eğitim verisi</param>
        /// <param name="context">Öğrenme bağlamı</param>
        /// <returns>Öğrenme sonucu</returns>
        public async Task<LearningResult> LearnNormalBehaviorAsync(
            IEnumerable<DataPoint> trainingData,
            LearningContext context = null)
        {
            ValidateTrainingData(trainingData);

            try
            {
                _logger.Info("Learning normal behavior patterns...");

                Status = AnalysisStatus.Learning;
                var startTime = DateTime.UtcNow;

                // Veriyi ön işleme;
                var processedData = await PreprocessDataAsync(trainingData, context);

                // İstatistiksel modeller oluştur;
                var statisticalResult = await BuildStatisticalModelsAsync(processedData);

                // Paternleri öğren;
                var patternResult = await LearnPatternsAsync(processedData, context);

                // Normal davranış modellerini oluştur;
                var behaviorModels = await CreateBehavioralModelsAsync(
                    statisticalResult,
                    patternResult);

                // Eşik değerlerini hesapla;
                await CalculateThresholdsAsync(behaviorModels);

                var learningTime = DateTime.UtcNow - startTime;

                _logger.Info($"Normal behavior learning completed. " +
                           $"Learned {behaviorModels.Count} models in {learningTime.TotalSeconds:F2}s");

                OnPatternLearned(new PatternLearnedEventArgs(
                    behaviorModels.Count,
                    learningTime,
                    patternResult.Confidence));

                Status = AnalysisStatus.Ready;

                return new LearningResult;
                {
                    Success = true,
                    ModelsLearned = behaviorModels.Count,
                    LearningTime = learningTime,
                    Confidence = patternResult.Confidence,
                    BehavioralModels = behaviorModels;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Normal behavior learning failed: {ex.Message}", ex);
                Status = AnalysisStatus.Failed;
                throw new DeviationAnalysisException("Normal behavior learning failed", ex);
            }
        }

        /// <summary>
        /// Tekil veri noktası için sapma analizi yapar;
        /// </summary>
        /// <param name="dataPoint">Analiz edilecek veri noktası</param>
        /// <param name="context">Analiz bağlamı</param>
        /// <returns>Sapma analiz sonucu</returns>
        public async Task<DeviationAnalysisResult> AnalyzeDeviationAsync(
            DataPoint dataPoint,
            AnalysisContext context = null)
        {
            ValidateDataPoint(dataPoint);

            try
            {
                _logger.Debug($"Analyzing data point: {dataPoint.Id}");

                Status = AnalysisStatus.Analyzing;
                TotalDataPointsAnalyzed++;

                // Veriyi ön işleme;
                var processedPoint = await PreprocessDataPointAsync(dataPoint, context);

                // İstatistiksel sapma hesapla;
                var statisticalDeviation = await CalculateStatisticalDeviationAsync(processedPoint);

                // Patern sapması hesapla;
                var patternDeviation = await CalculatePatternDeviationAsync(processedPoint, context);

                // Toplam sapma skoru hesapla;
                var totalDeviation = CalculateTotalDeviation(
                    statisticalDeviation,
                    patternDeviation);

                // Anomali olup olmadığını belirle;
                var isAnomaly = await DetermineIfAnomalyAsync(
                    totalDeviation,
                    processedPoint,
                    context);

                // Sapma detaylarını oluştur;
                var deviationDetails = await CreateDeviationDetailsAsync(
                    processedPoint,
                    statisticalDeviation,
                    patternDeviation,
                    totalDeviation,
                    isAnomaly);

                // Sonucu güncelle;
                UpdateAnalysisMetrics(deviationDetails);

                var result = new DeviationAnalysisResult;
                {
                    DataPoint = dataPoint,
                    DeviationScore = totalDeviation.Score,
                    IsAnomaly = isAnomaly,
                    Confidence = totalDeviation.Confidence,
                    DeviationType = deviationDetails.DeviationType,
                    ContributingFactors = deviationDetails.ContributingFactors,
                    Recommendations = deviationDetails.Recommendations,
                    Timestamp = DateTime.UtcNow;
                };

                // Olayları tetikle;
                if (totalDeviation.Score > Configuration.ConfidenceThreshold)
                {
                    OnDeviationDetected(new DeviationDetectedEventArgs(result));

                    if (isAnomaly)
                    {
                        DetectedAnomalies++;
                        OnAnomalyIdentified(new AnomalyIdentifiedEventArgs(result));
                    }
                }

                Status = AnalysisStatus.Ready;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Deviation analysis failed for data point {dataPoint.Id}: {ex.Message}", ex);
                Status = AnalysisStatus.Failed;
                throw new DeviationAnalysisException($"Analysis failed for data point {dataPoint.Id}", ex);
            }
        }

        /// <summary>
        /// Veri akışı için sürekli sapma analizi yapar;
        /// </summary>
        /// <param name="dataStream">Veri akışı</param>
        /// <param name="context">Analiz bağlamı</param>
        /// <returns>Analiz gözlemcisi</returns>
        public IDeviationObserver AnalyzeStream(
            IObservable<DataPoint> dataStream,
            AnalysisContext context = null)
        {
            if (dataStream == null)
                throw new ArgumentNullException(nameof(dataStream));

            _logger.Info("Starting stream analysis...");

            var observer = new StreamDeviationObserver(this, context, _logger);
            dataStream.Subscribe(observer);

            return observer;
        }

        /// <summary>
        /// Toplu veri için sapma analizi yapar;
        /// </summary>
        /// <param name="dataPoints">Veri noktaları koleksiyonu</param>
        /// <param name="context">Analiz bağlamı</param>
        /// <returns>Toplu analiz sonucu</returns>
        public async Task<BatchAnalysisResult> AnalyzeBatchAsync(
            IEnumerable<DataPoint> dataPoints,
            AnalysisContext context = null)
        {
            ValidateDataPoints(dataPoints);

            try
            {
                _logger.Info($"Starting batch analysis for {dataPoints.Count()} data points");

                Status = AnalysisStatus.Analyzing;
                var startTime = DateTime.UtcNow;

                var results = new List<DeviationAnalysisResult>();
                var anomalies = new List<DeviationAnalysisResult>();
                var deviationScores = new List<double>();

                // Paralel analiz için batch'leri oluştur;
                var batches = CreateBatches(dataPoints, Configuration.WindowSize);

                foreach (var batch in batches)
                {
                    var batchResults = await AnalyzeBatchInternalAsync(batch, context);
                    results.AddRange(batchResults.Results);
                    anomalies.AddRange(batchResults.Anomalies);
                    deviationScores.AddRange(batchResults.DeviationScores);

                    // İlerleme raporu;
                    OnAnalysisProgress(new AnalysisProgressEventArgs(
                        results.Count,
                        dataPoints.Count(),
                        DateTime.UtcNow - startTime));
                }

                // Toplu istatistikleri hesapla;
                var statistics = CalculateBatchStatistics(results, anomalies, deviationScores);

                // Eşik değerlerini güncelle (adaptif)
                if (Configuration.AutoCalibration)
                {
                    await UpdateThresholdsAsync(results, statistics);
                }

                var analysisTime = DateTime.UtcNow - startTime;

                _logger.Info($"Batch analysis completed. " +
                           $"Analyzed {results.Count} points, " +
                           $"found {anomalies.Count} anomalies in {analysisTime.TotalSeconds:F2}s");

                var result = new BatchAnalysisResult;
                {
                    TotalPointsAnalyzed = results.Count,
                    AnomaliesDetected = anomalies.Count,
                    AnalysisDuration = analysisTime,
                    AverageDeviationScore = statistics.AverageDeviation,
                    StandardDeviation = statistics.StandardDeviation,
                    AnomalyRate = (double)anomalies.Count / results.Count,
                    DetailedResults = results,
                    AnomalyList = anomalies,
                    Statistics = statistics;
                };

                OnAnalysisCompleted(new AnalysisCompletedEventArgs(result));

                Status = AnalysisStatus.Ready;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Batch analysis failed: {ex.Message}", ex);
                Status = AnalysisStatus.Failed;
                throw new DeviationAnalysisException("Batch analysis failed", ex);
            }
        }

        /// <summary>
        /// Çok boyutlu sapma analizi yapar;
        /// </summary>
        /// <param name="multiDimensionalData">Çok boyutlu veri</param>
        /// <param name="context">Analiz bağlamı</param>
        /// <returns>Çok boyutlu analiz sonucu</returns>
        public async Task<MultiDimensionalAnalysisResult> AnalyzeMultiDimensionalAsync(
            MultiDimensionalData multiDimensionalData,
            AnalysisContext context = null)
        {
            ValidateMultiDimensionalData(multiDimensionalData);

            try
            {
                _logger.Info($"Starting multi-dimensional analysis with {multiDimensionalData.Dimensions} dimensions");

                Status = AnalysisStatus.Analyzing;

                // Her boyut için ayrı analiz yap;
                var dimensionResults = new Dictionary<int, DimensionAnalysisResult>();

                for (int i = 0; i < multiDimensionalData.Dimensions; i++)
                {
                    var dimensionData = multiDimensionalData.GetDimension(i);
                    var dimensionResult = await AnalyzeDimensionAsync(dimensionData, i, context);
                    dimensionResults.Add(i, dimensionResult);
                }

                // Boyutlar arası korelasyon analizi;
                var correlationAnalysis = await AnalyzeCorrelationsAsync(dimensionResults);

                // Toplam sapma skoru hesapla;
                var overallDeviation = CalculateOverallDeviation(dimensionResults, correlationAnalysis);

                // Anomali tespiti;
                var anomalies = await DetectMultiDimensionalAnomaliesAsync(
                    dimensionResults,
                    correlationAnalysis,
                    overallDeviation);

                var result = new MultiDimensionalAnalysisResult;
                {
                    DimensionResults = dimensionResults,
                    CorrelationAnalysis = correlationAnalysis,
                    OverallDeviation = overallDeviation,
                    DetectedAnomalies = anomalies,
                    Dimensions = multiDimensionalData.Dimensions,
                    Confidence = CalculateMultiDimensionalConfidence(dimensionResults)
                };

                Status = AnalysisStatus.Ready;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Multi-dimensional analysis failed: {ex.Message}", ex);
                Status = AnalysisStatus.Failed;
                throw new DeviationAnalysisException("Multi-dimensional analysis failed", ex);
            }
        }

        /// <summary>
        /// Zaman serisi verisi için sapma analizi yapar;
        /// </summary>
        /// <param name="timeSeriesData">Zaman serisi verisi</param>
        /// <param name="context">Analiz bağlamı</param>
        /// <returns>Zaman serisi analiz sonucu</returns>
        public async Task<TimeSeriesAnalysisResult> AnalyzeTimeSeriesAsync(
            TimeSeriesData timeSeriesData,
            AnalysisContext context = null)
        {
            ValidateTimeSeriesData(timeSeriesData);

            try
            {
                _logger.Info($"Starting time series analysis with {timeSeriesData.Points.Count} points");

                Status = AnalysisStatus.Analyzing;

                // Zaman serisi özelliklerini çıkar;
                var features = await ExtractTimeSeriesFeaturesAsync(timeSeriesData);

                // Trend analizi yap;
                var trendAnalysis = await AnalyzeTrendAsync(timeSeriesData);

                // Mevsimsellik analizi yap;
                var seasonalityAnalysis = await AnalyzeSeasonalityAsync(timeSeriesData);

                // Sapmaları tespit et;
                var deviations = await DetectTimeSeriesDeviationsAsync(
                    timeSeriesData,
                    trendAnalysis,
                    seasonalityAnalysis);

                // Anomalileri belirle;
                var anomalies = await IdentifyTimeSeriesAnomaliesAsync(deviations, context);

                var result = new TimeSeriesAnalysisResult;
                {
                    TimeSeriesData = timeSeriesData,
                    ExtractedFeatures = features,
                    TrendAnalysis = trendAnalysis,
                    SeasonalityAnalysis = seasonalityAnalysis,
                    DetectedDeviations = deviations,
                    IdentifiedAnomalies = anomalies,
                    Forecast = await GenerateForecastAsync(timeSeriesData, deviations),
                    Confidence = CalculateTimeSeriesConfidence(deviations, anomalies)
                };

                Status = AnalysisStatus.Ready;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Time series analysis failed: {ex.Message}", ex);
                Status = AnalysisStatus.Failed;
                throw new DeviationAnalysisException("Time series analysis failed", ex);
            }
        }

        /// <summary>
        /// Sapma paternlerini getirir;
        /// </summary>
        /// <param name="patternType">Patern tipi (opsiyonel)</param>
        /// <returns>Sapma paternleri listesi</returns>
        public IReadOnlyList<DeviationPattern> GetDeviationPatterns(PatternType? patternType = null)
        {
            lock (_syncLock)
            {
                if (patternType.HasValue)
                {
                    return _deviationPatterns;
                        .Where(p => p.PatternType == patternType.Value)
                        .ToList();
                }

                return _deviationPatterns.ToList();
            }
        }

        /// <summary>
        /// İstatistiksel modelleri getirir;
        /// </summary>
        /// <param name="modelName">Model adı (opsiyonel)</param>
        /// <returns>İstatistiksel modeller</returns>
        public IReadOnlyDictionary<string, StatisticalModel> GetStatisticalModels(string modelName = null)
        {
            lock (_syncLock)
            {
                if (!string.IsNullOrEmpty(modelName))
                {
                    return _statisticalModels;
                        .Where(m => m.Key == modelName)
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }

                return _statisticalModels.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
        }

        /// <summary>
        /// Analiz konfigürasyonunu günceller;
        /// </summary>
        /// <param name="configuration">Yeni konfigürasyon</param>
        public void UpdateConfiguration(DeviationAnalysisConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            Configuration = configuration;
            _thresholdManager.UpdateConfiguration(configuration);

            _logger.Info("Deviation analysis configuration updated.");
        }

        /// <summary>
        /// Analiz metriklerini sıfırlar;
        /// </summary>
        public void ResetMetrics()
        {
            lock (_syncLock)
            {
                TotalDataPointsAnalyzed = 0;
                DetectedAnomalies = 0;
                LatestMetrics = new AnalysisMetrics();

                _logger.Info("Analysis metrics reset.");
            }
        }

        /// <summary>
        /// Analiz modellerini temizler;
        /// </summary>
        public void ClearModels()
        {
            lock (_syncLock)
            {
                _deviationPatterns.Clear();
                _statisticalModels.Clear();
                Status = AnalysisStatus.Idle;

                _logger.Info("Analysis models cleared.");
            }
        }

        #endregion;

        #region Private Methods;

        private async Task<IEnumerable<ProcessedDataPoint>> PreprocessDataAsync(
            IEnumerable<DataPoint> data,
            LearningContext context)
        {
            var processedData = new List<ProcessedDataPoint>();

            foreach (var point in data)
            {
                var processed = await PreprocessDataPointAsync(point, context);
                processedData.Add(processed);
            }

            return processedData;
        }

        private async Task<ProcessedDataPoint> PreprocessDataPointAsync(
            DataPoint dataPoint,
            AnalysisContext context)
        {
            // Normalizasyon;
            var normalized = await NormalizeDataAsync(dataPoint.Values);

            // Eksik veri tamamlama;
            var completed = await HandleMissingValuesAsync(normalized);

            // Gürültü azaltma;
            var denoised = await ReduceNoiseAsync(completed);

            // Özellik çıkarımı;
            var features = await ExtractFeaturesAsync(denoised, context);

            return new ProcessedDataPoint;
            {
                OriginalData = dataPoint,
                ProcessedValues = denoised,
                ExtractedFeatures = features,
                Timestamp = DateTime.UtcNow;
            };
        }

        private async Task<StatisticalModelsResult> BuildStatisticalModelsAsync(
            IEnumerable<ProcessedDataPoint> data)
        {
            var models = new Dictionary<string, StatisticalModel>();

            // Temel istatistikler;
            var basicStats = CalculateBasicStatistics(data);

            // Dağılım modelleri;
            var distributionModels = await BuildDistributionModelsAsync(data);

            // Korelasyon modelleri;
            var correlationModels = await BuildCorrelationModelsAsync(data);

            // Zaman serisi istatistikleri (eğer uygunsa)
            var timeSeriesStats = await CalculateTimeSeriesStatisticsAsync(data);

            models.Add("BasicStatistics", basicStats);
            models.Add("DistributionModels", distributionModels);
            models.Add("CorrelationModels", correlationModels);
            models.Add("TimeSeriesStatistics", timeSeriesStats);

            lock (_syncLock)
            {
                foreach (var model in models)
                {
                    _statisticalModels[model.Key] = model.Value;
                }
            }

            return new StatisticalModelsResult;
            {
                Models = models,
                Confidence = CalculateStatisticalConfidence(models)
            };
        }

        private async Task<PatternLearningResult> LearnPatternsAsync(
            IEnumerable<ProcessedDataPoint> data,
            LearningContext context)
        {
            var patterns = new List<BehavioralPattern>();

            // Normallik paternlerini öğren;
            var normalPatterns = await _patternRecognizer.LearnPatternsAsync(
                data.Select(d => d.ProcessedValues),
                context);

            // Davranış paternlerini çıkar;
            var behaviorPatterns = await ExtractBehaviorPatternsAsync(data, context);

            // Anomali paternlerini tanımla;
            var anomalyPatterns = await DefineAnomalyPatternsAsync(data, normalPatterns);

            patterns.AddRange(normalPatterns);
            patterns.AddRange(behaviorPatterns);
            patterns.AddRange(anomalyPatterns);

            lock (_syncLock)
            {
                foreach (var pattern in patterns)
                {
                    var deviationPattern = new DeviationPattern;
                    {
                        Pattern = pattern,
                        PatternType = DeterminePatternType(pattern),
                        Confidence = pattern.Confidence,
                        FirstSeen = DateTime.UtcNow,
                        LastSeen = DateTime.UtcNow,
                        OccurrenceCount = 1;
                    };

                    _deviationPatterns.Add(deviationPattern);
                }
            }

            return new PatternLearningResult;
            {
                PatternsLearned = patterns.Count,
                NormalPatterns = normalPatterns.Count,
                BehavioralPatterns = behaviorPatterns.Count,
                AnomalyPatterns = anomalyPatterns.Count,
                Confidence = patterns.Average(p => p.Confidence)
            };
        }

        private async Task<Dictionary<string, BehavioralModel>> CreateBehavioralModelsAsync(
            StatisticalModelsResult statisticalResult,
            PatternLearningResult patternResult)
        {
            var models = new Dictionary<string, BehavioralModel>();

            // İstatistiksel model tabanlı davranış modelleri;
            foreach (var statModel in statisticalResult.Models)
            {
                var behaviorModel = new BehavioralModel;
                {
                    ModelName = $"Behavioral_{statModel.Key}",
                    BaseModel = statModel.Value,
                    BehavioralType = BehavioralType.Statistical,
                    Confidence = statModel.Value.Confidence,
                    Created = DateTime.UtcNow;
                };

                models.Add(behaviorModel.ModelName, behaviorModel);
            }

            // Patern tabanlı davranış modelleri;
            var patternModels = await CreatePatternBasedModelsAsync(patternResult);
            foreach (var patternModel in patternModels)
            {
                models.Add(patternModel.Key, patternModel.Value);
            }

            NormalBehaviorModels = models;

            return models;
        }

        private async Task CalculateThresholdsAsync(Dictionary<string, BehavioralModel> models)
        {
            foreach (var model in models)
            {
                var threshold = await _thresholdManager.CalculateThresholdAsync(
                    model.Value,
                    Configuration);

                model.Value.Threshold = threshold;

                OnThresholdAdjusted(new ThresholdAdjustedEventArgs(
                    model.Key,
                    threshold,
                    ThresholdAdjustmentReason.InitialCalculation));
            }
        }

        private async Task<StatisticalDeviation> CalculateStatisticalDeviationAsync(
            ProcessedDataPoint dataPoint)
        {
            var deviations = new List<DimensionDeviation>();

            foreach (var model in _statisticalModels)
            {
                var deviation = await model.Value.CalculateDeviationAsync(dataPoint.ProcessedValues);
                deviations.Add(new DimensionDeviation;
                {
                    ModelName = model.Key,
                    DeviationScore = deviation.Score,
                    Confidence = deviation.Confidence,
                    Dimension = deviation.Dimension;
                });
            }

            return new StatisticalDeviation;
            {
                DimensionDeviations = deviations,
                OverallScore = deviations.Average(d => d.DeviationScore),
                Confidence = deviations.Average(d => d.Confidence),
                Method = Configuration.StatisticalMethod;
            };
        }

        private async Task<PatternDeviation> CalculatePatternDeviationAsync(
            ProcessedDataPoint dataPoint,
            AnalysisContext context)
        {
            var patternMatches = new List<PatternMatch>();
            var mismatches = new List<PatternMismatch>();

            foreach (var pattern in _deviationPatterns)
            {
                var matchResult = await _patternRecognizer.MatchPatternAsync(
                    dataPoint.ProcessedValues,
                    pattern.Pattern,
                    context);

                if (matchResult.IsMatch)
                {
                    patternMatches.Add(new PatternMatch;
                    {
                        PatternId = pattern.Pattern.Id,
                        MatchScore = matchResult.Score,
                        Confidence = matchResult.Confidence;
                    });
                }
                else;
                {
                    mismatches.Add(new PatternMismatch;
                    {
                        PatternId = pattern.Pattern.Id,
                        MismatchScore = 1 - matchResult.Score,
                        Reason = matchResult.MismatchReason;
                    });
                }
            }

            return new PatternDeviation;
            {
                PatternMatches = patternMatches,
                PatternMismatches = mismatches,
                MatchScore = patternMatches.Any() ? patternMatches.Average(m => m.MatchScore) : 0,
                MismatchScore = mismatches.Any() ? mismatches.Average(m => m.MismatchScore) : 1,
                OverallDeviation = patternMatches.Any()
                    ? 1 - patternMatches.Average(m => m.MatchScore)
                    : 1;
            };
        }

        private TotalDeviation CalculateTotalDeviation(
            StatisticalDeviation statisticalDeviation,
            PatternDeviation patternDeviation)
        {
            // Ağırlıklı ortalama hesapla;
            var statisticalWeight = GetStatisticalWeight();
            var patternWeight = 1 - statisticalWeight;

            var totalScore = (statisticalDeviation.OverallScore * statisticalWeight) +
                            (patternDeviation.OverallDeviation * patternWeight);

            var totalConfidence = (statisticalDeviation.Confidence * statisticalWeight) +
                                 (patternDeviation.OverallDeviation * patternWeight);

            return new TotalDeviation;
            {
                Score = totalScore,
                Confidence = totalConfidence,
                StatisticalDeviation = statisticalDeviation,
                PatternDeviation = patternDeviation,
                Weights = new DeviationWeights;
                {
                    StatisticalWeight = statisticalWeight,
                    PatternWeight = patternWeight;
                }
            };
        }

        private async Task<bool> DetermineIfAnomalyAsync(
            TotalDeviation totalDeviation,
            ProcessedDataPoint dataPoint,
            AnalysisContext context)
        {
            // Eşik değeri kontrolü;
            var threshold = await _thresholdManager.GetCurrentThresholdAsync(dataPoint, context);

            // Hassasiyet seviyesine göre ayarlama;
            var adjustedThreshold = AdjustThresholdForSensitivity(threshold);

            // Anomali kararı;
            var isAnomaly = totalDeviation.Score > adjustedThreshold;

            // Güven seviyesi kontrolü;
            if (isAnomaly && totalDeviation.Confidence < Configuration.ConfidenceThreshold)
            {
                isAnomaly = false; // Güven yeterli değilse anomali sayma;
            }

            return isAnomaly;
        }

        private async Task<DeviationDetails> CreateDeviationDetailsAsync(
            ProcessedDataPoint dataPoint,
            StatisticalDeviation statisticalDeviation,
            PatternDeviation patternDeviation,
            TotalDeviation totalDeviation,
            bool isAnomaly)
        {
            var details = new DeviationDetails;
            {
                DataPointId = dataPoint.OriginalData.Id,
                Timestamp = dataPoint.OriginalData.Timestamp,
                TotalDeviation = totalDeviation,
                IsAnomaly = isAnomaly,
                DeviationType = DetermineDeviationType(statisticalDeviation, patternDeviation),
                Severity = CalculateSeverity(totalDeviation.Score),
                ContributingFactors = await IdentifyContributingFactorsAsync(
                    statisticalDeviation,
                    patternDeviation),
                Recommendations = GenerateRecommendations(
                    totalDeviation,
                    isAnomaly,
                    dataPoint.OriginalData.Context),
                Context = dataPoint.OriginalData.Context;
            };

            return details;
        }

        private void UpdateAnalysisMetrics(DeviationDetails details)
        {
            lock (_syncLock)
            {
                LatestMetrics.TotalPoints++;
                LatestMetrics.TotalDeviationSum += details.TotalDeviation.Score;
                LatestMetrics.MaxDeviation = Math.Max(LatestMetrics.MaxDeviation, details.TotalDeviation.Score);
                LatestMetrics.MinDeviation = Math.Min(LatestMetrics.MinDeviation, details.TotalDeviation.Score);

                if (details.IsAnomaly)
                {
                    LatestMetrics.AnomalyCount++;
                }

                // Ortalama ve standart sapma güncelle;
                LatestMetrics.AverageDeviation = LatestMetrics.TotalDeviationSum / LatestMetrics.TotalPoints;

                // Varyans hesaplama (basitleştirilmiş)
                if (LatestMetrics.TotalPoints > 1)
                {
                    var varianceSum = LatestMetrics.TotalDeviationSumSquared +
                                    Math.Pow(details.TotalDeviation.Score, 2);
                    LatestMetrics.StandardDeviation = Math.Sqrt(
                        varianceSum / LatestMetrics.TotalPoints -
                        Math.Pow(LatestMetrics.AverageDeviation, 2));
                }

                LatestMetrics.LastUpdate = DateTime.UtcNow;
            }
        }

        private async Task<BatchAnalysisResult> AnalyzeBatchInternalAsync(
            IEnumerable<DataPoint> batch,
            AnalysisContext context)
        {
            var results = new List<DeviationAnalysisResult>();
            var anomalies = new List<DeviationAnalysisResult>();
            var deviationScores = new List<double>();

            foreach (var point in batch)
            {
                var result = await AnalyzeDeviationAsync(point, context);
                results.Add(result);
                deviationScores.Add(result.DeviationScore);

                if (result.IsAnomaly)
                {
                    anomalies.Add(result);
                }
            }

            return new BatchAnalysisResult;
            {
                Results = results,
                Anomalies = anomalies,
                DeviationScores = deviationScores;
            };
        }

        private IEnumerable<IEnumerable<DataPoint>> CreateBatches(
            IEnumerable<DataPoint> dataPoints,
            int batchSize)
        {
            var batch = new List<DataPoint>();

            foreach (var point in dataPoints)
            {
                batch.Add(point);

                if (batch.Count >= batchSize)
                {
                    yield return batch;
                    batch = new List<DataPoint>();
                }
            }

            if (batch.Any())
            {
                yield return batch;
            }
        }

        private BatchStatistics CalculateBatchStatistics(
            List<DeviationAnalysisResult> results,
            List<DeviationAnalysisResult> anomalies,
            List<double> deviationScores)
        {
            return new BatchStatistics;
            {
                TotalPoints = results.Count,
                AnomalyCount = anomalies.Count,
                AverageDeviation = deviationScores.Average(),
                MedianDeviation = CalculateMedian(deviationScores),
                StandardDeviation = CalculateStandardDeviation(deviationScores),
                MinDeviation = deviationScores.Min(),
                MaxDeviation = deviationScores.Max(),
                Percentile95 = CalculatePercentile(deviationScores, 0.95),
                Percentile99 = CalculatePercentile(deviationScores, 0.99),
                AnomalyRate = (double)anomalies.Count / results.Count;
            };
        }

        private async Task UpdateThresholdsAsync(
            List<DeviationAnalysisResult> results,
            BatchStatistics statistics)
        {
            var newThreshold = await _thresholdManager.AdaptThresholdAsync(
                results,
                statistics,
                Configuration);

            OnThresholdAdjusted(new ThresholdAdjustedEventArgs(
                "AdaptiveThreshold",
                newThreshold,
                ThresholdAdjustmentReason.AdaptiveUpdate));
        }

        private double GetStatisticalWeight()
        {
            // İstatistiksel metod'a göre ağırlık belirle;
            switch (Configuration.StatisticalMethod)
            {
                case StatisticalMethod.ZScore:
                    return 0.6;
                case StatisticalMethod.MAD:
                    return 0.5;
                case StatisticalMethod.IQR:
                    return 0.55;
                case StatisticalMethod.Mahalanobis:
                    return 0.7;
                default:
                    return 0.5;
            }
        }

        private double AdjustThresholdForSensitivity(double threshold)
        {
            switch (Configuration.SensitivityLevel)
            {
                case SensitivityLevel.VeryLow:
                    return threshold * 1.5;
                case SensitivityLevel.Low:
                    return threshold * 1.25;
                case SensitivityLevel.Medium:
                    return threshold;
                case SensitivityLevel.High:
                    return threshold * 0.75;
                case SensitivityLevel.VeryHigh:
                    return threshold * 0.5;
                default:
                    return threshold;
            }
        }

        private DeviationType DetermineDeviationType(
            StatisticalDeviation statisticalDeviation,
            PatternDeviation patternDeviation)
        {
            if (statisticalDeviation.OverallScore > patternDeviation.OverallDeviation * 1.5)
                return DeviationType.Statistical;

            if (patternDeviation.OverallDeviation > statisticalDeviation.OverallScore * 1.5)
                return DeviationType.Pattern;

            return DeviationType.Combined;
        }

        private double CalculateSeverity(double deviationScore)
        {
            if (deviationScore < 0.3) return 1; // Düşük;
            if (deviationScore < 0.6) return 2; // Orta;
            if (deviationScore < 0.8) return 3; // Yüksek;
            return 4; // Kritik;
        }

        #endregion;

        #region Event Triggers;

        protected virtual void OnDeviationDetected(DeviationDetectedEventArgs e)
        {
            DeviationDetected?.Invoke(this, e);
        }

        protected virtual void OnAnomalyIdentified(AnomalyIdentifiedEventArgs e)
        {
            AnomalyIdentified?.Invoke(this, e);
        }

        protected virtual void OnThresholdAdjusted(ThresholdAdjustedEventArgs e)
        {
            ThresholdAdjusted?.Invoke(this, e);
        }

        protected virtual void OnPatternLearned(PatternLearnedEventArgs e)
        {
            PatternLearned?.Invoke(this, e);
        }

        protected virtual void OnAnalysisProgress(AnalysisProgressEventArgs e)
        {
            // Progress event handler'ı eklenebilir;
        }

        protected virtual void OnAnalysisCompleted(AnalysisCompletedEventArgs e)
        {
            AnalysisCompleted?.Invoke(this, e);
        }

        #endregion;

        #region Validation Methods;

        private void ValidateTrainingData(IEnumerable<DataPoint> trainingData)
        {
            if (trainingData == null)
                throw new ArgumentNullException(nameof(trainingData));

            var dataList = trainingData.ToList();
            if (dataList.Count < Configuration.MinimumDataPoints)
                throw new ArgumentException(
                    $"Minimum {Configuration.MinimumDataPoints} data points required for training.",
                    nameof(trainingData));

            if (dataList.Any(d => d.Values == null || d.Values.Length == 0))
                throw new ArgumentException("Data points must contain values.", nameof(trainingData));
        }

        private void ValidateDataPoint(DataPoint dataPoint)
        {
            if (dataPoint == null)
                throw new ArgumentNullException(nameof(dataPoint));

            if (dataPoint.Values == null || dataPoint.Values.Length == 0)
                throw new ArgumentException("Data point must contain values.", nameof(dataPoint));
        }

        private void ValidateDataPoints(IEnumerable<DataPoint> dataPoints)
        {
            if (dataPoints == null)
                throw new ArgumentNullException(nameof(dataPoints));

            if (!dataPoints.Any())
                throw new ArgumentException("At least one data point is required.", nameof(dataPoints));
        }

        private void ValidateMultiDimensionalData(MultiDimensionalData data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Dimensions <= 0)
                throw new ArgumentException("Dimensions must be greater than zero.", nameof(data));

            if (data.DataPoints == null || !data.DataPoints.Any())
                throw new ArgumentException("Data points are required.", nameof(data));
        }

        private void ValidateTimeSeriesData(TimeSeriesData data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Points == null || data.Points.Count < 2)
                throw new ArgumentException("Time series must contain at least two points.", nameof(data));

            if (data.Points.Any(p => p.Timestamp == default))
                throw new ArgumentException("All time series points must have valid timestamps.", nameof(data));
        }

        #endregion;

        #region Helper Methods;

        private double CalculateMedian(List<double> values)
        {
            if (!values.Any()) return 0;

            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;

            if (sorted.Count % 2 == 0)
                return (sorted[mid - 1] + sorted[mid]) / 2.0;
            else;
                return sorted[mid];
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            if (values.Count < 2) return 0;

            var mean = values.Average();
            var sumSquares = values.Sum(v => Math.Pow(v - mean, 2));
            return Math.Sqrt(sumSquares / (values.Count - 1));
        }

        private double CalculatePercentile(List<double> values, double percentile)
        {
            if (!values.Any()) return 0;

            var sorted = values.OrderBy(v => v).ToList();
            double index = percentile * (sorted.Count - 1);
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);

            if (lower == upper) return sorted[lower];

            double weight = index - lower;
            return sorted[lower] * (1 - weight) + sorted[upper] * weight;
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    ClearModels();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~DeviationAnalyzer()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    public enum AnalysisStatus;
    {
        Idle,
        Learning,
        Analyzing,
        Ready,
        Failed;
    }

    public enum AnalysisMode;
    {
        RealTime,
        Batch,
        Streaming,
        Hybrid;
    }

    public enum SensitivityLevel;
    {
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh;
    }

    public enum StatisticalMethod;
    {
        ZScore,
        MAD,
        IQR,
        Mahalanobis,
        Percentile,
        DensityBased;
    }

    public enum DeviationType;
    {
        Statistical,
        Pattern,
        Temporal,
        Contextual,
        Combined;
    }

    public enum BehavioralType;
    {
        Statistical,
        PatternBased,
        Temporal,
        Contextual;
    }

    public enum PatternType;
    {
        Normal,
        Anomaly,
        Seasonal,
        Trend,
        Cyclical,
        Random;
    }

    public enum ThresholdAdjustmentReason;
    {
        InitialCalculation,
        AdaptiveUpdate,
        ManualOverride,
        ContextChange,
        PerformanceOptimization;
    }

    public class DeviationAnalysisConfiguration;
    {
        public AnalysisMode AnalysisMode { get; set; }
        public SensitivityLevel SensitivityLevel { get; set; }
        public double ConfidenceThreshold { get; set; }
        public int WindowSize { get; set; }
        public double LearningRate { get; set; }
        public bool AdaptiveThresholding { get; set; }
        public bool MultiDimensionalAnalysis { get; set; }
        public bool TimeSeriesAnalysis { get; set; }
        public StatisticalMethod StatisticalMethod { get; set; }
        public int MaximumDeviations { get; set; }
        public int MinimumDataPoints { get; set; }
        public bool AutoCalibration { get; set; }
    }

    public class AnalysisMetrics;
    {
        public long TotalPoints { get; set; }
        public long AnomalyCount { get; set; }
        public double TotalDeviationSum { get; set; }
        public double TotalDeviationSumSquared { get; set; }
        public double AverageDeviation { get; set; }
        public double StandardDeviation { get; set; }
        public double MinDeviation { get; set; }
        public double MaxDeviation { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    public class DeviationPattern;
    {
        public BehavioralPattern Pattern { get; set; }
        public PatternType PatternType { get; set; }
        public double Confidence { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public int OccurrenceCount { get; set; }
    }

    #endregion;

    #region Event Arguments;

    public class DeviationDetectedEventArgs : EventArgs;
    {
        public DeviationAnalysisResult Result { get; }

        public DeviationDetectedEventArgs(DeviationAnalysisResult result)
        {
            Result = result;
        }
    }

    public class AnomalyIdentifiedEventArgs : EventArgs;
    {
        public DeviationAnalysisResult Anomaly { get; }

        public AnomalyIdentifiedEventArgs(DeviationAnalysisResult anomaly)
        {
            Anomaly = anomaly;
        }
    }

    public class ThresholdAdjustedEventArgs : EventArgs;
    {
        public string ThresholdName { get; }
        public double NewValue { get; }
        public ThresholdAdjustmentReason Reason { get; }

        public ThresholdAdjustedEventArgs(string thresholdName, double newValue, ThresholdAdjustmentReason reason)
        {
            ThresholdName = thresholdName;
            NewValue = newValue;
            Reason = reason;
        }
    }

    public class PatternLearnedEventArgs : EventArgs;
    {
        public int PatternsLearned { get; }
        public TimeSpan LearningTime { get; }
        public double Confidence { get; }

        public PatternLearnedEventArgs(int patternsLearned, TimeSpan learningTime, double confidence)
        {
            PatternsLearned = patternsLearned;
            LearningTime = learningTime;
            Confidence = confidence;
        }
    }

    public class AnalysisProgressEventArgs : EventArgs;
    {
        public int ProcessedPoints { get; }
        public int TotalPoints { get; }
        public TimeSpan ElapsedTime { get; }

        public AnalysisProgressEventArgs(int processedPoints, int totalPoints, TimeSpan elapsedTime)
        {
            ProcessedPoints = processedPoints;
            TotalPoints = totalPoints;
            ElapsedTime = elapsedTime;
        }
    }

    public class AnalysisCompletedEventArgs : EventArgs;
    {
        public BatchAnalysisResult Result { get; }

        public AnalysisCompletedEventArgs(BatchAnalysisResult result)
        {
            Result = result;
        }
    }

    #endregion;

    #region Exceptions;

    public class DeviationAnalysisException : Exception
    {
        public AnalysisStage FailedStage { get; }

        public DeviationAnalysisException(string message) : base(message) { }

        public DeviationAnalysisException(string message, Exception innerException)
            : base(message, innerException) { }

        public DeviationAnalysisException(string message, AnalysisStage failedStage, Exception innerException = null)
            : base(message, innerException)
        {
            FailedStage = failedStage;
        }
    }

    public enum AnalysisStage;
    {
        DataPreprocessing,
        ModelLearning,
        StatisticalAnalysis,
        PatternRecognition,
        AnomalyDetection,
        ThresholdCalculation,
        ResultAggregation;
    }

    #endregion;
}
