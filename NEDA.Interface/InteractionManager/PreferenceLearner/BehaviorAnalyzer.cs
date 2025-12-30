using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using NEDA.NeuralNetwork.DeepLearning;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
{
    /// <summary>
    /// Davranış özellikleri;
    /// </summary>
    public class BehaviorFeature;
    {
        public string FeatureId { get; set; }
        public string FeatureName { get; set; }
        public FeatureType Type { get; set; }
        public double[] Values { get; set; }
        public double Weight { get; set; }
        public double[] NormalizedValues { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public BehaviorFeature()
        {
            Metadata = new Dictionary<string, object>();
            FeatureId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Davranış kalıbı;
    /// </summary>
    public class BehaviorPattern;
    {
        public string PatternId { get; set; }
        public string PatternName { get; set; }
        public PatternType Type { get; set; }
        public List<BehaviorFeature> Features { get; set; }
        public double[] PatternVector { get; set; }
        public double Confidence { get; set; }
        public double Support { get; set; }
        public List<string> Sequence { get; set; }
        public Dictionary<string, double> TransitionProbabilities { get; set; }
        public DateTime FirstDetected { get; set; }
        public DateTime LastDetected { get; set; }
        public int DetectionCount { get; set; }
        public Dictionary<string, object> PatternRules { get; set; }
        public bool IsAnomaly { get; set; }

        public BehaviorPattern()
        {
            Features = new List<BehaviorFeature>();
            Sequence = new List<string>();
            TransitionProbabilities = new Dictionary<string, double>();
            PatternRules = new Dictionary<string, object>();
            PatternId = Guid.NewGuid().ToString();
            FirstDetected = DateTime.UtcNow;
            LastDetected = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Davranış olayı;
    /// </summary>
    public class BehavioralEvent;
    {
        public string EventId { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public string EventType { get; set; }
        public Dictionary<string, object> EventData { get; set; }
        public DateTime Timestamp { get; set; }
        public string Context { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public double Significance { get; set; }

        public BehavioralEvent()
        {
            EventData = new Dictionary<string, object>();
            Metadata = new Dictionary<string, object>();
            EventId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Davranış analiz sonucu;
    /// </summary>
    public class BehaviorAnalysisResult;
    {
        public string AnalysisId { get; set; }
        public string UserId { get; set; }
        public List<string> DetectedPatterns { get; set; }
        public Dictionary<string, double> PatternConfidences { get; set; }
        public List<string> Anomalies { get; set; }
        public Dictionary<string, object> BehavioralMetrics { get; set; }
        public Dictionary<string, double> Predictions { get; set; }
        public List<string> Recommendations { get; set; }
        public DateTime AnalysisTime { get; set; }
        public double OverallConfidence { get; set; }

        public BehaviorAnalysisResult()
        {
            DetectedPatterns = new List<string>();
            PatternConfidences = new Dictionary<string, double>();
            Anomalies = new List<string>();
            BehavioralMetrics = new Dictionary<string, object>();
            Predictions = new Dictionary<string, double>();
            Recommendations = new List<string>();
            AnalysisId = Guid.NewGuid().ToString();
            AnalysisTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Özellik türleri;
    /// </summary>
    public enum FeatureType;
    {
        Temporal,
        Frequency,
        Sequential,
        Categorical,
        Numerical,
        Textual,
        Composite;
    }

    /// <summary>
    /// Kalıp türleri;
    /// </summary>
    public enum PatternType;
    {
        SequentialPattern,
        FrequentPattern,
        AnomalyPattern,
        TrendPattern,
        CyclicalPattern,
        AssociationPattern,
        PredictivePattern,
        HabitPattern;
    }

    /// <summary>
    /// Davranış analizörü arayüzü;
    /// </summary>
    public interface IBehaviorAnalyzer : IDisposable
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<BehaviorAnalysisResult> AnalyzeBehaviorAsync(string userId, TimeSpan timeWindow, CancellationToken cancellationToken = default);
        Task<List<BehaviorPattern>> ExtractPatternsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken = default);
        Task<List<string>> DetectAnomaliesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken = default);
        Task<Dictionary<string, double>> PredictBehaviorAsync(string userId, TimeSpan futureWindow, CancellationToken cancellationToken = default);
        Task<bool> TrainModelAsync(List<BehavioralEvent> trainingData, CancellationToken cancellationToken = default);
        Task<double> CalculateBehaviorSimilarityAsync(string userId1, string userId2, CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> GenerateBehavioralProfileAsync(string userId, CancellationToken cancellationToken = default);
        Task<List<string>> GenerateRecommendationsAsync(string userId, BehaviorAnalysisResult analysis, CancellationToken cancellationToken = default);
        Task<BehaviorAnalysisResult> RealTimeAnalysisAsync(BehavioralEvent currentEvent, CancellationToken cancellationToken = default);
        Task<bool> SavePatternAsync(BehaviorPattern pattern, CancellationToken cancellationToken = default);
        Task<List<BehaviorPattern>> GetPatternsByUserAsync(string userId, CancellationToken cancellationToken = default);
        Task<List<BehaviorPattern>> GetCommonPatternsAsync(PatternType? patternType = null, CancellationToken cancellationToken = default);
        Task<double> GetPatternConfidenceAsync(string patternId, CancellationToken cancellationToken = default);
        Task<bool> UpdatePatternAsync(BehaviorPattern pattern, CancellationToken cancellationToken = default);
        Task<bool> DeletePatternAsync(string patternId, CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> GetAnalyticsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default);
        event EventHandler<PatternDetectedEventArgs> PatternDetected;
        event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;
    }

    /// <summary>
    /// Kalıp tespit edildi event argümanları;
    /// </summary>
    public class PatternDetectedEventArgs : EventArgs;
    {
        public string PatternId { get; set; }
        public string UserId { get; set; }
        public string PatternName { get; set; }
        public double Confidence { get; set; }
        public DateTime DetectionTime { get; set; }
        public Dictionary<string, object> Context { get; set; }

        public PatternDetectedEventArgs(string patternId, string userId, string patternName, double confidence)
        {
            PatternId = patternId;
            UserId = userId;
            PatternName = patternName;
            Confidence = confidence;
            DetectionTime = DateTime.UtcNow;
            Context = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Anomali tespit edildi event argümanları;
    /// </summary>
    public class AnomalyDetectedEventArgs : EventArgs;
    {
        public string AnomalyId { get; set; }
        public string UserId { get; set; }
        public string EventId { get; set; }
        public string Description { get; set; }
        public double Severity { get; set; }
        public DateTime DetectionTime { get; set; }
        public Dictionary<string, object> EventData { get; set; }

        public AnomalyDetectedEventArgs(string anomalyId, string userId, string eventId, string description, double severity)
        {
            AnomalyId = anomalyId;
            UserId = userId;
            EventId = eventId;
            Description = description;
            Severity = severity;
            DetectionTime = DateTime.UtcNow;
            EventData = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Davranış analizörü implementasyonu;
    /// </summary>
    public class BehaviorAnalyzer : IBehaviorAnalyzer;
    {
        private readonly ILogger<BehaviorAnalyzer> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly AppConfig _appConfig;
        private readonly NeuralNetwork _neuralNetwork;

        private readonly ConcurrentDictionary<string, List<BehavioralEvent>> _userEvents;
        private readonly ConcurrentDictionary<string, List<BehaviorPattern>> _userPatterns;
        private readonly ConcurrentDictionary<string, BehaviorPattern> _patternCache;
        private readonly ConcurrentDictionary<string, double[]> _userBehaviorVectors;

        private readonly SemaphoreSlim _processingLock;
        private readonly Timer _patternMiningTimer;
        private readonly Timer _modelRetrainingTimer;
        private bool _isInitialized;
        private bool _isDisposed;
        private readonly object _syncLock = new object();

        // Pattern mining algoritmaları için sabitler;
        private const double MIN_SUPPORT = 0.1;
        private const double MIN_CONFIDENCE = 0.7;
        private const int MIN_SEQUENCE_LENGTH = 3;
        private const int MAX_SEQUENCE_LENGTH = 20;
        private const double ANOMALY_THRESHOLD = 0.95;

        /// <summary>
        /// Pattern tespit edildi event'i;
        /// </summary>
        public event EventHandler<PatternDetectedEventArgs> PatternDetected;

        /// <summary>
        /// Anomali tespit edildi event'i;
        /// </summary>
        public event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;

        /// <summary>
        /// BehaviorAnalyzer constructor;
        /// </summary>
        public BehaviorAnalyzer(
            ILogger<BehaviorAnalyzer> logger,
            IOptions<AppConfig> appConfig,
            IExceptionHandler exceptionHandler,
            NeuralNetwork neuralNetwork)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appConfig = appConfig?.Value ?? throw new ArgumentNullException(nameof(appConfig));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _neuralNetwork = neuralNetwork ?? throw new ArgumentNullException(nameof(neuralNetwork));

            _userEvents = new ConcurrentDictionary<string, List<BehavioralEvent>>();
            _userPatterns = new ConcurrentDictionary<string, List<BehaviorPattern>>();
            _patternCache = new ConcurrentDictionary<string, BehaviorPattern>();
            _userBehaviorVectors = new ConcurrentDictionary<string, double[]>();

            _processingLock = new SemaphoreSlim(1, 1);

            // Her 30 dakikada bir pattern mining;
            _patternMiningTimer = new Timer(MinePatternsCallback, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

            // Her 24 saatte bir modeli yeniden eğit;
            _modelRetrainingTimer = new Timer(RetrainModelCallback, null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
        }

        /// <summary>
        /// BehaviorAnalyzer'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("BehaviorAnalyzer başlatılıyor...");

                if (_isInitialized)
                {
                    _logger.LogWarning("BehaviorAnalyzer zaten başlatılmış");
                    return;
                }

                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    // Sinir ağını başlat;
                    await _neuralNetwork.InitializeAsync(cancellationToken);

                    // Önceden kaydedilmiş pattern'leri yükle;
                    await LoadPatternsFromStorageAsync(cancellationToken);

                    _isInitialized = true;
                    _logger.LogInformation("BehaviorAnalyzer başarıyla başlatıldı");
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BehaviorAnalyzer başlatılırken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.InitializeAsync");
                throw;
            }
        }

        /// <summary>
        /// Davranış analizi yapar;
        /// </summary>
        public async Task<BehaviorAnalysisResult> AnalyzeBehaviorAsync(string userId, TimeSpan timeWindow, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogDebug("Davranış analizi başlatılıyor. UserId: {UserId}, TimeWindow: {TimeWindow}",
                        userId, timeWindow);

                    var cutoffTime = DateTime.UtcNow - timeWindow;

                    // Kullanıcı olaylarını getir;
                    var userEvents = await GetUserEventsAsync(userId, cutoffTime, cancellationToken);

                    if (!userEvents.Any())
                    {
                        _logger.LogWarning("Analiz için yeterli olay bulunamadı. UserId: {UserId}", userId);
                        return CreateEmptyAnalysisResult(userId);
                    }

                    // Kalıpları çıkar;
                    var patterns = await ExtractPatternsAsync(userEvents, cancellationToken);

                    // Anomalileri tespit et;
                    var anomalies = await DetectAnomaliesAsync(userEvents, cancellationToken);

                    // Davranış vektörünü hesapla;
                    var behaviorVector = await CalculateBehaviorVectorAsync(userEvents, patterns, cancellationToken);
                    _userBehaviorVectors[userId] = behaviorVector;

                    // Tahminler yap;
                    var predictions = await PredictBehaviorAsync(userId, TimeSpan.FromHours(1), cancellationToken);

                    // Sonuç oluştur;
                    var result = await CreateAnalysisResultAsync(userId, patterns, anomalies, behaviorVector, predictions, cancellationToken);

                    _logger.LogInformation("Davranış analizi tamamlandı. UserId: {UserId}, Pattern: {PatternCount}, Anomaly: {AnomalyCount}",
                        userId, patterns.Count, anomalies.Count);

                    return result;
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Davranış analizi yapılırken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.AnalyzeBehaviorAsync");
                throw;
            }
        }

        /// <summary>
        /// Olaylardan kalıpları çıkarır;
        /// </summary>
        public async Task<List<BehaviorPattern>> ExtractPatternsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken = default)
        {
            if (events == null || !events.Any())
                return new List<BehaviorPattern>();

            try
            {
                var patterns = new List<BehaviorPattern>();

                // 1. Sıralı kalıpları çıkar;
                var sequentialPatterns = await ExtractSequentialPatternsAsync(events, cancellationToken);
                patterns.AddRange(sequentialPatterns);

                // 2. Sıkça tekrarlanan kalıpları çıkar;
                var frequentPatterns = await ExtractFrequentPatternsAsync(events, cancellationToken);
                patterns.AddRange(frequentPatterns);

                // 3. Zaman serisi kalıplarını çıkar;
                var timeSeriesPatterns = await ExtractTimeSeriesPatternsAsync(events, cancellationToken);
                patterns.AddRange(timeSeriesPatterns);

                // 4. İlişkisel kalıpları çıkar;
                var associationPatterns = await ExtractAssociationPatternsAsync(events, cancellationToken);
                patterns.AddRange(associationPatterns);

                // 5. Sinir ağı ile gelişmiş kalıpları çıkar;
                var neuralPatterns = await ExtractNeuralPatternsAsync(events, cancellationToken);
                patterns.AddRange(neuralPatterns);

                // Kalıpları konsolide et ve filtrele;
                var consolidatedPatterns = await ConsolidatePatternsAsync(patterns, cancellationToken);

                _logger.LogDebug("{PatternCount} kalıp çıkarıldı. Event: {EventCount}",
                    consolidatedPatterns.Count, events.Count);

                return consolidatedPatterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kalıplar çıkarılırken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.ExtractPatternsAsync");
                throw;
            }
        }

        /// <summary>
        /// Anomalileri tespit eder;
        /// </summary>
        public async Task<List<string>> DetectAnomaliesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken = default)
        {
            if (events == null || !events.Any())
                return new List<string>();

            try
            {
                var anomalies = new List<string>();

                // 1. İstatistiksel anomaliler;
                var statisticalAnomalies = await DetectStatisticalAnomaliesAsync(events, cancellationToken);
                anomalies.AddRange(statisticalAnomalies);

                // 2. Sıralı anomaliler;
                var sequentialAnomalies = await DetectSequentialAnomaliesAsync(events, cancellationToken);
                anomalies.AddRange(sequentialAnomalies);

                // 3. Zaman anomalileri;
                var temporalAnomalies = await DetectTemporalAnomaliesAsync(events, cancellationToken);
                anomalies.AddRange(temporalAnomalies);

                // 4. Sinir ağı ile anomali tespiti;
                var neuralAnomalies = await DetectNeuralAnomaliesAsync(events, cancellationToken);
                anomalies.AddRange(neuralAnomalies);

                // 5. Kural tabanlı anomali tespiti;
                var ruleBasedAnomalies = await DetectRuleBasedAnomaliesAsync(events, cancellationToken);
                anomalies.AddRange(ruleBasedAnomalies);

                // Anomalileri filtrele (threshold üzerinde olanlar)
                var filteredAnomalies = anomalies;
                    .Distinct()
                    .Where(anomaly => !string.IsNullOrEmpty(anomaly))
                    .ToList();

                _logger.LogDebug("{AnomalyCount} anomali tespit edildi", filteredAnomalies.Count);

                return filteredAnomalies;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anomaliler tespit edilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.DetectAnomaliesAsync");
                throw;
            }
        }

        /// <summary>
        /// Davranış tahmini yapar;
        /// </summary>
        public async Task<Dictionary<string, double>> PredictBehaviorAsync(string userId, TimeSpan futureWindow, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    // Kullanıcı davranış vektörünü getir;
                    if (!_userBehaviorVectors.TryGetValue(userId, out var behaviorVector))
                    {
                        // Vektör yoksa analiz yap ve oluştur;
                        var analysis = await AnalyzeBehaviorAsync(userId, TimeSpan.FromDays(7), cancellationToken);
                        if (!_userBehaviorVectors.TryGetValue(userId, out behaviorVector))
                        {
                            return new Dictionary<string, double>();
                        }
                    }

                    // Kullanıcı pattern'lerini getir;
                    var userPatterns = await GetPatternsByUserAsync(userId, cancellationToken);

                    // Geçmiş olayları getir;
                    var recentEvents = await GetUserEventsAsync(userId, DateTime.UtcNow - TimeSpan.FromDays(30), cancellationToken);

                    // Tahminler yap;
                    var predictions = new Dictionary<string, double>();

                    // 1. Event tipi tahminleri;
                    var eventTypePredictions = await PredictEventTypesAsync(userId, behaviorVector, recentEvents, futureWindow, cancellationToken);
                    foreach (var prediction in eventTypePredictions)
                    {
                        predictions[$"EventType_{prediction.Key}"] = prediction.Value;
                    }

                    // 2. Pattern tekrarlama tahminleri;
                    var patternPredictions = await PredictPatternOccurrencesAsync(userPatterns, behaviorVector, futureWindow, cancellationToken);
                    foreach (var prediction in patternPredictions)
                    {
                        predictions[$"Pattern_{prediction.Key}"] = prediction.Value;
                    }

                    // 3. Anomali tahminleri;
                    var anomalyPrediction = await PredictAnomalyProbabilityAsync(userId, behaviorVector, recentEvents, futureWindow, cancellationToken);
                    predictions["AnomalyProbability"] = anomalyPrediction;

                    // 4. Aktivite seviyesi tahmini;
                    var activityPrediction = await PredictActivityLevelAsync(userId, behaviorVector, futureWindow, cancellationToken);
                    predictions["ActivityLevel"] = activityPrediction;

                    _logger.LogDebug("Davranış tahmini tamamlandı. UserId: {UserId}, Prediction: {PredictionCount}",
                        userId, predictions.Count);

                    return predictions;
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Davranış tahmini yapılırken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.PredictBehaviorAsync");
                throw;
            }
        }

        /// <summary>
        /// Modeli eğitir;
        /// </summary>
        public async Task<bool> TrainModelAsync(List<BehavioralEvent> trainingData, CancellationToken cancellationToken = default)
        {
            if (trainingData == null || !trainingData.Any())
                throw new ArgumentException("Eğitim verisi boş olamaz", nameof(trainingData));

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogInformation("Model eğitimi başlatılıyor. TrainingData: {EventCount}", trainingData.Count);

                    // 1. Veriyi ön işleme;
                    var processedData = await PreprocessTrainingDataAsync(trainingData, cancellationToken);

                    // 2. Özellikleri çıkar;
                    var features = await ExtractFeaturesAsync(processedData, cancellationToken);

                    // 3. Sinir ağını eğit;
                    var trainingResult = await _neuralNetwork.TrainAsync(features, processedData.Select(e => ConvertEventToVector(e)).ToArray(), cancellationToken);

                    // 4. Pattern'leri eğit;
                    await TrainPatternModelsAsync(processedData, cancellationToken);

                    // 5. Anomali modellerini eğit;
                    await TrainAnomalyModelsAsync(processedData, cancellationToken);

                    _logger.LogInformation("Model eğitimi tamamlandı. Success: {Success}", trainingResult);
                    return trainingResult;
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Model eğitilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.TrainModelAsync");
                throw;
            }
        }

        /// <summary>
        /// Davranış benzerliğini hesaplar;
        /// </summary>
        public async Task<double> CalculateBehaviorSimilarityAsync(string userId1, string userId2, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId1) || string.IsNullOrEmpty(userId2))
                throw new ArgumentException("User ID'ler boş olamaz");

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    // Her iki kullanıcının da davranış vektörlerini getir;
                    if (!_userBehaviorVectors.TryGetValue(userId1, out var vector1))
                    {
                        var analysis1 = await AnalyzeBehaviorAsync(userId1, TimeSpan.FromDays(7), cancellationToken);
                        if (!_userBehaviorVectors.TryGetValue(userId1, out vector1))
                            return 0.0;
                    }

                    if (!_userBehaviorVectors.TryGetValue(userId2, out var vector2))
                    {
                        var analysis2 = await AnalyzeBehaviorAsync(userId2, TimeSpan.FromDays(7), cancellationToken);
                        if (!_userBehaviorVectors.TryGetValue(userId2, out vector2))
                            return 0.0;
                    }

                    // Cosine similarity hesapla;
                    var similarity = CalculateCosineSimilarity(vector1, vector2);

                    // Pattern benzerliğini hesapla;
                    var patterns1 = await GetPatternsByUserAsync(userId1, cancellationToken);
                    var patterns2 = await GetPatternsByUserAsync(userId2, cancellationToken);
                    var patternSimilarity = CalculatePatternSimilarity(patterns1, patterns2);

                    // Toplam benzerlik;
                    var totalSimilarity = (similarity * 0.6) + (patternSimilarity * 0.4);

                    _logger.LogDebug("Davranış benzerliği hesaplandı. User1: {UserId1}, User2: {UserId2}, Similarity: {Similarity:F2}",
                        userId1, userId2, totalSimilarity);

                    return totalSimilarity;
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Davranış benzerliği hesaplanırken hata oluştu. User1: {UserId1}, User2: {UserId2}",
                    userId1, userId2);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.CalculateBehaviorSimilarityAsync");
                throw;
            }
        }

        /// <summary>
        /// Davranış profili oluşturur;
        /// </summary>
        public async Task<Dictionary<string, object>> GenerateBehavioralProfileAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                // Davranış analizi yap;
                var analysis = await AnalyzeBehaviorAsync(userId, TimeSpan.FromDays(30), cancellationToken);

                // Pattern'leri getir;
                var patterns = await GetPatternsByUserAsync(userId, cancellationToken);

                // Tahminler yap;
                var predictions = await PredictBehaviorAsync(userId, TimeSpan.FromDays(7), cancellationToken);

                // Profil oluştur;
                var profile = new Dictionary<string, object>
                {
                    ["UserId"] = userId,
                    ["AnalysisTime"] = DateTime.UtcNow,
                    ["PatternCount"] = patterns.Count,
                    ["CommonPatterns"] = patterns.Where(p => p.Support > 0.5).Select(p => p.PatternName).ToList(),
                    ["AnomalyCount"] = analysis.Anomalies.Count,
                    ["BehaviorMetrics"] = analysis.BehavioralMetrics,
                    ["Predictions"] = predictions,
                    ["ActivityPatterns"] = ExtractActivityPatterns(patterns),
                    ["TemporalPatterns"] = ExtractTemporalPatterns(patterns),
                    ["BehavioralConsistency"] = CalculateBehavioralConsistency(patterns),
                    ["LearningAdaptability"] = CalculateLearningAdaptability(patterns)
                };

                // Pattern detayları;
                var patternDetails = patterns.Select(p => new;
                {
                    p.PatternId,
                    p.PatternName,
                    p.Type,
                    p.Confidence,
                    p.Support,
                    LastDetected = p.LastDetected.ToString("yyyy-MM-dd HH:mm:ss"),
                    DetectionCount = p.DetectionCount;
                }).ToList();

                profile["PatternDetails"] = patternDetails;

                _logger.LogDebug("Davranış profili oluşturuldu. UserId: {UserId}, Pattern: {PatternCount}",
                    userId, patterns.Count);

                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Davranış profili oluşturulurken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.GenerateBehavioralProfileAsync");
                throw;
            }
        }

        /// <summary>
        /// Öneriler oluşturur;
        /// </summary>
        public async Task<List<string>> GenerateRecommendationsAsync(string userId, BehaviorAnalysisResult analysis, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            try
            {
                var recommendations = new List<string>();

                // Anomalilere göre öneriler;
                if (analysis.Anomalies.Any())
                {
                    recommendations.Add("Düzensiz davranış kalıpları tespit edildi. Rutinlerinizi gözden geçirmeniz önerilir.");
                    recommendations.Add("Anomali oranı yüksek. Sistem kullanım alışkanlıklarınızı değerlendirin.");
                }

                // Pattern'lere göre öneriler;
                if (analysis.DetectedPatterns.Any())
                {
                    var strongPatterns = analysis.PatternConfidences.Where(p => p.Value > 0.8).ToList();
                    if (strongPatterns.Any())
                    {
                        recommendations.Add($"Güçlü davranış kalıpları tespit edildi ({strongPatterns.Count} adet). Bu kalıpları optimize edebilirsiniz.");
                    }
                }

                // Tahminlere göre öneriler;
                if (analysis.Predictions.Any())
                {
                    if (analysis.Predictions.TryGetValue("AnomalyProbability", out var anomalyProb) && anomalyProb > 0.7)
                    {
                        recommendations.Add("Yakın gelecekte anomali olasılığı yüksek. Önleyici tedbirler almanız önerilir.");
                    }

                    if (analysis.Predictions.TryGetValue("ActivityLevel", out var activityLevel))
                    {
                        if (activityLevel > 0.8)
                            recommendations.Add("Yüksek aktivite seviyesi bekleniyor. Dinlenme zamanlarını planlamayı unutmayın.");
                        else if (activityLevel < 0.3)
                            recommendations.Add("Düşük aktivite seviyesi bekleniyor. Üretkenlik için planlı aktiviteler ekleyin.");
                    }
                }

                // Metrik bazlı öneriler;
                if (analysis.BehavioralMetrics.TryGetValue("ConsistencyScore", out var consistencyObj) &&
                    consistencyObj is double consistency && consistency < 0.6)
                {
                    recommendations.Add("Davranış tutarlılığınız düşük. Rutin oluşturmak için aynı saatlerde benzer işlemler yapmayı deneyin.");
                }

                if (analysis.BehavioralMetrics.TryGetValue("EfficiencyScore", out var efficiencyObj) &&
                    efficiencyObj is double efficiency && efficiency < 0.5)
                {
                    recommendations.Add("Verimlilik skorunuz düşük. İş akışlarınızı optimize etmek için otomasyonları kullanmayı düşünün.");
                }

                // Pattern bazlı özel öneriler;
                var userPatterns = await GetPatternsByUserAsync(userId, cancellationToken);
                foreach (var pattern in userPatterns.Where(p => p.Support > 0.7 && p.Type == PatternType.HabitPattern))
                {
                    recommendations.Add($"'{pattern.PatternName}' alışkanlığınızı geliştirmek için hedefler belirleyebilirsiniz.");
                }

                // Limit öneri sayısı;
                recommendations = recommendations.Take(10).ToList();

                _logger.LogDebug("Öneriler oluşturuldu. UserId: {UserId}, Recommendation: {RecommendationCount}",
                    userId, recommendations.Count);

                return recommendations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Öneriler oluşturulurken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.GenerateRecommendationsAsync");
                throw;
            }
        }

        /// <summary>
        /// Gerçek zamanlı analiz yapar;
        /// </summary>
        public async Task<BehaviorAnalysisResult> RealTimeAnalysisAsync(BehavioralEvent currentEvent, CancellationToken cancellationToken = default)
        {
            if (currentEvent == null)
                throw new ArgumentNullException(nameof(currentEvent));

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogDebug("Gerçek zamanlı analiz başlatılıyor. EventId: {EventId}, UserId: {UserId}",
                        currentEvent.EventId, currentEvent.UserId);

                    // Event'i kaydet;
                    await RecordEventAsync(currentEvent, cancellationToken);

                    // Son 24 saatlik olayları getir;
                    var recentEvents = await GetUserEventsAsync(currentEvent.UserId, DateTime.UtcNow - TimeSpan.FromHours(24), cancellationToken);

                    // Anomali kontrolü;
                    var isAnomaly = await CheckForAnomalyAsync(currentEvent, recentEvents, cancellationToken);

                    if (isAnomaly)
                    {
                        var anomalyId = $"Anomaly_{currentEvent.EventId}";
                        OnAnomalyDetected(new AnomalyDetectedEventArgs(
                            anomalyId, currentEvent.UserId, currentEvent.EventId,
                            "Gerçek zamanlı anomali tespit edildi", 0.8));
                    }

                    // Pattern eşleştirme;
                    var matchedPatterns = await MatchPatternsAsync(currentEvent, recentEvents, cancellationToken);

                    foreach (var pattern in matchedPatterns)
                    {
                        OnPatternDetected(new PatternDetectedEventArgs(
                            pattern.PatternId, currentEvent.UserId, pattern.PatternName, pattern.Confidence));
                    }

                    // Hızlı analiz sonucu oluştur;
                    var result = new BehaviorAnalysisResult;
                    {
                        UserId = currentEvent.UserId,
                        DetectedPatterns = matchedPatterns.Select(p => p.PatternName).ToList(),
                        Anomalies = isAnomaly ? new List<string> { "RealTimeAnomaly" } : new List<string>(),
                        OverallConfidence = matchedPatterns.Any() ? matchedPatterns.Average(p => p.Confidence) : 0.0;
                    };

                    _logger.LogDebug("Gerçek zamanlı analiz tamamlandı. EventId: {EventId}, Pattern: {PatternCount}, Anomaly: {IsAnomaly}",
                        currentEvent.EventId, matchedPatterns.Count, isAnomaly);

                    return result;
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gerçek zamanlı analiz yapılırken hata oluştu. EventId: {EventId}", currentEvent.EventId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.RealTimeAnalysisAsync");
                throw;
            }
        }

        /// <summary>
        /// Pattern'i kaydeder;
        /// </summary>
        public async Task<bool> SavePatternAsync(BehaviorPattern pattern, CancellationToken cancellationToken = default)
        {
            if (pattern == null)
                throw new ArgumentNullException(nameof(pattern));

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    // Cache'e ekle;
                    _patternCache[pattern.PatternId] = pattern;

                    // Kullanıcı pattern listesine ekle;
                    if (!string.IsNullOrEmpty(pattern.PatternName))
                    {
                        // User pattern'lerini güncelle;
                        var users = ExtractUsersFromPattern(pattern);
                        foreach (var userId in users)
                        {
                            _userPatterns.AddOrUpdate(userId,
                                new List<BehaviorPattern> { pattern },
                                (key, existingList) =>
                                {
                                    var existingPattern = existingList.FirstOrDefault(p => p.PatternId == pattern.PatternId);
                                    if (existingPattern != null)
                                    {
                                        existingList.Remove(existingPattern);
                                    }
                                    existingList.Add(pattern);
                                    return existingList;
                                });
                        }
                    }

                    // Dosyaya kaydet;
                    await SavePatternToFileAsync(pattern, cancellationToken);

                    _logger.LogDebug("Pattern kaydedildi. PatternId: {PatternId}, Name: {PatternName}",
                        pattern.PatternId, pattern.PatternName);

                    return true;
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pattern kaydedilirken hata oluştu. PatternId: {PatternId}", pattern.PatternId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.SavePatternAsync");
                throw;
            }
        }

        /// <summary>
        /// Kullanıcıya ait pattern'leri getirir;
        /// </summary>
        public async Task<List<BehaviorPattern>> GetPatternsByUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("UserId boş olamaz", nameof(userId));

            try
            {
                if (_userPatterns.TryGetValue(userId, out var patterns))
                {
                    return patterns.ToList();
                }

                // Dosyadan yükle;
                var loadedPatterns = await LoadPatternsForUserAsync(userId, cancellationToken);
                if (loadedPatterns.Any())
                {
                    _userPatterns[userId] = loadedPatterns;
                }

                return loadedPatterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kullanıcı pattern'leri getirilirken hata oluştu. UserId: {UserId}", userId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.GetPatternsByUserAsync");
                throw;
            }
        }

        /// <summary>
        /// Ortak pattern'leri getirir;
        /// </summary>
        public async Task<List<BehaviorPattern>> GetCommonPatternsAsync(PatternType? patternType = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var allPatterns = _patternCache.Values.ToList();

                if (patternType.HasValue)
                {
                    allPatterns = allPatterns.Where(p => p.Type == patternType.Value).ToList();
                }

                // Support değerine göre sırala;
                var commonPatterns = allPatterns;
                    .Where(p => p.Support >= MIN_SUPPORT && p.Confidence >= MIN_CONFIDENCE)
                    .OrderByDescending(p => p.Support * p.Confidence)
                    .Take(100)
                    .ToList();

                return commonPatterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ortak pattern'ler getirilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.GetCommonPatternsAsync");
                throw;
            }
        }

        /// <summary>
        /// Pattern güven skorunu getirir;
        /// </summary>
        public async Task<double> GetPatternConfidenceAsync(string patternId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(patternId))
                throw new ArgumentException("PatternId boş olamaz", nameof(patternId));

            try
            {
                if (_patternCache.TryGetValue(patternId, out var pattern))
                {
                    return pattern.Confidence;
                }

                // Dosyadan yükle;
                var loadedPattern = await LoadPatternFromFileAsync(patternId, cancellationToken);
                if (loadedPattern != null)
                {
                    _patternCache[patternId] = loadedPattern;
                    return loadedPattern.Confidence;
                }

                return 0.0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pattern güven skoru getirilirken hata oluştu. PatternId: {PatternId}", patternId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.GetPatternConfidenceAsync");
                throw;
            }
        }

        /// <summary>
        /// Pattern'i günceller;
        /// </summary>
        public async Task<bool> UpdatePatternAsync(BehaviorPattern pattern, CancellationToken cancellationToken = default)
        {
            if (pattern == null)
                throw new ArgumentNullException(nameof(pattern));

            try
            {
                return await SavePatternAsync(pattern, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pattern güncellenirken hata oluştu. PatternId: {PatternId}", pattern.PatternId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.UpdatePatternAsync");
                throw;
            }
        }

        /// <summary>
        /// Pattern'i siler;
        /// </summary>
        public async Task<bool> DeletePatternAsync(string patternId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(patternId))
                throw new ArgumentException("PatternId boş olamaz", nameof(patternId));

            try
            {
                await _processingLock.WaitAsync(cancellationToken);
                try
                {
                    // Cache'ten sil;
                    _patternCache.TryRemove(patternId, out _);

                    // User pattern listelerinden sil;
                    foreach (var userPatterns in _userPatterns.Values)
                    {
                        var patternToRemove = userPatterns.FirstOrDefault(p => p.PatternId == patternId);
                        if (patternToRemove != null)
                        {
                            userPatterns.Remove(patternToRemove);
                        }
                    }

                    // Dosyadan sil;
                    var filePath = GetPatternFilePath(patternId);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }

                    _logger.LogDebug("Pattern silindi. PatternId: {PatternId}", patternId);
                    return true;
                }
                finally
                {
                    _processingLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pattern silinirken hata oluştu. PatternId: {PatternId}", patternId);
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.DeletePatternAsync");
                throw;
            }
        }

        /// <summary>
        /// Analitik verileri getirir;
        /// </summary>
        public async Task<Dictionary<string, object>> GetAnalyticsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            try
            {
                var analytics = new Dictionary<string, object>();

                // Toplam pattern sayısı;
                var totalPatterns = _patternCache.Count;
                analytics["TotalPatterns"] = totalPatterns;

                // Pattern türleri dağılımı;
                var patternTypeDistribution = _patternCache.Values;
                    .GroupBy(p => p.Type)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());
                analytics["PatternTypeDistribution"] = patternTypeDistribution;

                // Aktif kullanıcı sayısı;
                var activeUsers = _userPatterns.Count;
                analytics["ActiveUsers"] = activeUsers;

                // Pattern güven istatistikleri;
                var confidenceStats = _patternCache.Values;
                    .Select(p => p.Confidence)
                    .ToArray();

                if (confidenceStats.Any())
                {
                    analytics["AverageConfidence"] = confidenceStats.Average();
                    analytics["MinConfidence"] = confidenceStats.Min();
                    analytics["MaxConfidence"] = confidenceStats.Max();
                    analytics["ConfidenceStdDev"] = CalculateStandardDeviation(confidenceStats);
                }

                // Anomali istatistikleri;
                var anomalyPatterns = _patternCache.Values.Where(p => p.IsAnomaly).ToList();
                analytics["AnomalyPatterns"] = anomalyPatterns.Count;
                analytics["AnomalyRate"] = totalPatterns > 0 ? (double)anomalyPatterns.Count / totalPatterns : 0.0;

                // Zaman serisi verileri;
                var patternsByDate = _patternCache.Values;
                    .Where(p => p.FirstDetected >= startDate && p.FirstDetected <= endDate)
                    .GroupBy(p => p.FirstDetected.Date)
                    .OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key.ToString("yyyy-MM-dd"), g => g.Count());
                analytics["PatternsByDate"] = patternsByDate;

                // Kullanıcı başına pattern sayısı;
                var patternsPerUser = _userPatterns;
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
                analytics["PatternsPerUser"] = patternsPerUser;

                // En yaygın 10 pattern;
                var topPatterns = _patternCache.Values;
                    .OrderByDescending(p => p.Support * p.DetectionCount)
                    .Take(10)
                    .Select(p => new;
                    {
                        p.PatternId,
                        p.PatternName,
                        p.Type,
                        p.Support,
                        p.Confidence,
                        DetectionCount = p.DetectionCount;
                    })
                    .ToList();
                analytics["TopPatterns"] = topPatterns;

                _logger.LogDebug("Analitik veriler getirildi. Pattern: {PatternCount}, User: {UserCount}",
                    totalPatterns, activeUsers);

                return analytics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analitik veriler getirilirken hata oluştu");
                await _exceptionHandler.HandleExceptionAsync(ex, "BehaviorAnalyzer.GetAnalyticsAsync");
                throw;
            }
        }

        #region Yardımcı Metodlar;

        /// <summary>
        /// Pattern'leri depolamadan yükler;
        /// </summary>
        private async Task LoadPatternsFromStorageAsync(CancellationToken cancellationToken)
        {
            try
            {
                var patternsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "BehaviorPatterns");

                if (!Directory.Exists(patternsPath))
                    return;

                var files = Directory.GetFiles(patternsPath, "*.json");
                int loadedCount = 0;

                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken);
                        var pattern = JsonConvert.DeserializeObject<BehaviorPattern>(json);

                        if (pattern != null)
                        {
                            _patternCache[pattern.PatternId] = pattern;
                            loadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Pattern dosyası yüklenirken hata oluştu: {FileName}", Path.GetFileName(file));
                    }
                }

                _logger.LogDebug("{LoadedCount} pattern depolamadan yüklendi", loadedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pattern'ler depolamadan yüklenirken hata oluştu");
                throw;
            }
        }

        /// <summary>
        /// Pattern'i dosyaya kaydeder;
        /// </summary>
        private async Task SavePatternToFileAsync(BehaviorPattern pattern, CancellationToken cancellationToken)
        {
            var patternsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NEDA",
                "BehaviorPatterns");

            Directory.CreateDirectory(patternsPath);

            var filePath = Path.Combine(patternsPath, $"{pattern.PatternId}.json");
            var json = JsonConvert.SerializeObject(pattern, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
        }

        /// <summary>
        /// Pattern dosya yolunu getirir;
        /// </summary>
        private string GetPatternFilePath(string patternId)
        {
            var patternsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NEDA",
                "BehaviorPatterns");

            return Path.Combine(patternsPath, $"{patternId}.json");
        }

        /// <summary>
        /// Pattern'i dosyadan yükler;
        /// </summary>
        private async Task<BehaviorPattern> LoadPatternFromFileAsync(string patternId, CancellationToken cancellationToken)
        {
            try
            {
                var filePath = GetPatternFilePath(patternId);
                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                    return JsonConvert.DeserializeObject<BehaviorPattern>(json);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pattern dosyası okunurken hata oluştu: {PatternId}", patternId);
                return null;
            }
        }

        /// <summary>
        /// Kullanıcı pattern'lerini dosyadan yükler;
        /// </summary>
        private async Task<List<BehaviorPattern>> LoadPatternsForUserAsync(string userId, CancellationToken cancellationToken)
        {
            var patterns = new List<BehaviorPattern>();

            try
            {
                // Tüm pattern'leri tarayarak kullanıcıya ait olanları bul;
                foreach (var pattern in _patternCache.Values)
                {
                    if (PatternContainsUser(pattern, userId))
                    {
                        patterns.Add(pattern);
                    }
                }

                // Dosyalardan da yükle;
                var patternsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "BehaviorPatterns");

                if (Directory.Exists(patternsPath))
                {
                    var files = Directory.GetFiles(patternsPath, "*.json");
                    foreach (var file in files)
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(file, cancellationToken);
                            var pattern = JsonConvert.DeserializeObject<BehaviorPattern>(json);

                            if (pattern != null && PatternContainsUser(pattern, userId))
                            {
                                patterns.Add(pattern);
                                _patternCache[pattern.PatternId] = pattern;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Pattern dosyası okunurken hata oluştu: {FileName}", Path.GetFileName(file));
                        }
                    }
                }

                return patterns.DistinctBy(p => p.PatternId).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kullanıcı pattern'leri yüklenirken hata oluştu. UserId: {UserId}", userId);
                return patterns;
            }
        }

        /// <summary>
        /// Pattern'de kullanıcı olup olmadığını kontrol eder;
        /// </summary>
        private bool PatternContainsUser(BehaviorPattern pattern, string userId)
        {
            // Pattern metadata'sında kullanıcı bilgisi olabilir;
            if (pattern.PatternRules.TryGetValue("Users", out var usersObj) && usersObj is List<string> users)
            {
                return users.Contains(userId);
            }

            // Features'ta kullanıcı bilgisi olabilir;
            foreach (var feature in pattern.Features)
            {
                if (feature.Metadata.TryGetValue("UserId", out var featureUserId) && featureUserId?.ToString() == userId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pattern'den kullanıcıları çıkarır;
        /// </summary>
        private List<string> ExtractUsersFromPattern(BehaviorPattern pattern)
        {
            var users = new HashSet<string>();

            // Features'tan kullanıcıları çıkar;
            foreach (var feature in pattern.Features)
            {
                if (feature.Metadata.TryGetValue("UserId", out var userId) && userId != null)
                {
                    users.Add(userId.ToString());
                }
            }

            return users.ToList();
        }

        /// <summary>
        /// Kullanıcı olaylarını getirir;
        /// </summary>
        private async Task<List<BehavioralEvent>> GetUserEventsAsync(string userId, DateTime cutoffTime, CancellationToken cancellationToken)
        {
            if (_userEvents.TryGetValue(userId, out var events))
            {
                return events.Where(e => e.Timestamp >= cutoffTime).ToList();
            }

            // Dosyadan yükle;
            return await LoadEventsForUserAsync(userId, cutoffTime, cancellationToken);
        }

        /// <summary>
        /// Kullanıcı olaylarını dosyadan yükler;
        /// </summary>
        private async Task<List<BehavioralEvent>> LoadEventsForUserAsync(string userId, DateTime cutoffTime, CancellationToken cancellationToken)
        {
            var events = new List<BehavioralEvent>();

            try
            {
                var eventsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "BehaviorEvents",
                    userId);

                if (!Directory.Exists(eventsPath))
                    return events;

                var files = Directory.GetFiles(eventsPath, "*.json")
                    .Where(f => new FileInfo(f).LastWriteTime >= cutoffTime);

                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken);
                        var eventList = JsonConvert.DeserializeObject<List<BehavioralEvent>>(json);

                        if (eventList != null)
                        {
                            events.AddRange(eventList.Where(e => e.Timestamp >= cutoffTime));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Event dosyası okunurken hata oluştu: {FileName}", Path.GetFileName(file));
                    }
                }

                return events;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kullanıcı olayları yüklenirken hata oluştu. UserId: {UserId}", userId);
                return events;
            }
        }

        /// <summary>
        /// Event'i kaydeder;
        /// </summary>
        private async Task RecordEventAsync(BehavioralEvent @event, CancellationToken cancellationToken)
        {
            try
            {
                // Belleğe kaydet;
                _userEvents.AddOrUpdate(@event.UserId,
                    new List<BehavioralEvent> { @event },
                    (key, existingList) =>
                    {
                        existingList.Add(@event);
                        return existingList;
                    });

                // Dosyaya kaydet;
                await SaveEventToFileAsync(@event, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event kaydedilirken hata oluştu. EventId: {EventId}", @event.EventId);
            }
        }

        /// <summary>
        /// Event'i dosyaya kaydeder;
        /// </summary>
        private async Task SaveEventToFileAsync(BehavioralEvent @event, CancellationToken cancellationToken)
        {
            try
            {
                var eventsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "NEDA",
                    "BehaviorEvents",
                    @event.UserId);

                Directory.CreateDirectory(eventsPath);

                var date = @event.Timestamp.ToString("yyyy-MM-dd");
                var filePath = Path.Combine(eventsPath, $"{date}.json");

                List<BehavioralEvent> existingEvents = new List<BehavioralEvent>();

                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                    existingEvents = JsonConvert.DeserializeObject<List<BehavioralEvent>>(json) ?? new List<BehavioralEvent>();
                }

                existingEvents.Add(@event);

                // Günlük başına maksimum event sayısı;
                if (existingEvents.Count > 1000)
                {
                    existingEvents = existingEvents;
                        .OrderByDescending(e => e.Timestamp)
                        .Take(1000)
                        .ToList();
                }

                var newJson = JsonConvert.SerializeObject(existingEvents, Formatting.Indented);
                await File.WriteAllTextAsync(filePath, newJson, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event dosyaya kaydedilirken hata oluştu. EventId: {EventId}", @event.EventId);
            }
        }

        /// <summary>
        /// Boş analiz sonucu oluşturur;
        /// </summary>
        private BehaviorAnalysisResult CreateEmptyAnalysisResult(string userId)
        {
            return new BehaviorAnalysisResult;
            {
                UserId = userId,
                OverallConfidence = 0.0,
                BehavioralMetrics = new Dictionary<string, object>
                {
                    ["DataSufficiency"] = 0.0,
                    ["AnalysisQuality"] = 0.0;
                }
            };
        }

        /// <summary>
        /// Analiz sonucu oluşturur;
        /// </summary>
        private async Task<BehaviorAnalysisResult> CreateAnalysisResultAsync(string userId, List<BehaviorPattern> patterns,
            List<string> anomalies, double[] behaviorVector, Dictionary<string, double> predictions, CancellationToken cancellationToken)
        {
            var result = new BehaviorAnalysisResult;
            {
                UserId = userId,
                DetectedPatterns = patterns.Select(p => p.PatternName).ToList(),
                PatternConfidences = patterns.ToDictionary(p => p.PatternId, p => p.Confidence),
                Anomalies = anomalies,
                Predictions = predictions,
                OverallConfidence = patterns.Any() ? patterns.Average(p => p.Confidence) : 0.0;
            };

            // Davranış metriklerini hesapla;
            result.BehavioralMetrics = await CalculateBehavioralMetricsAsync(patterns, behaviorVector, cancellationToken);

            // Öneriler oluştur;
            result.Recommendations = await GenerateRecommendationsAsync(userId, result, cancellationToken);

            return result;
        }

        /// <summary>
        /// Davranış vektörünü hesaplar;
        /// </summary>
        private async Task<double[]> CalculateBehaviorVectorAsync(List<BehavioralEvent> events, List<BehaviorPattern> patterns, CancellationToken cancellationToken)
        {
            try
            {
                // 64 boyutlu davranış vektörü;
                var vector = new double[64];

                // Event frekansları;
                var eventTypes = events.GroupBy(e => e.EventType)
                    .ToDictionary(g => g.Key, g => g.Count());

                int index = 0;
                foreach (var eventType in eventTypes.Take(20))
                {
                    vector[index++] = Math.Log(eventType.Value + 1);
                }

                // Zaman özellikleri;
                var hours = events.Select(e => e.Timestamp.Hour).ToArray();
                vector[index++] = hours.Average();
                vector[index++] = hours.StdDev();

                var days = events.Select(e => (int)e.Timestamp.DayOfWeek).ToArray();
                vector[index++] = days.Average();
                vector[index++] = days.StdDev();

                // Pattern özellikleri;
                if (patterns.Any())
                {
                    vector[index++] = patterns.Count;
                    vector[index++] = patterns.Average(p => p.Confidence);
                    vector[index++] = patterns.Average(p => p.Support);
                    vector[index++] = patterns.Count(p => p.IsAnomaly);
                }

                // Event önem seviyeleri;
                var significanceSum = events.Sum(e => e.Significance);
                vector[index++] = significanceSum / events.Count;

                // Boş kalan yerleri 0 ile doldur;
                for (int i = index; i < vector.Length; i++)
                {
                    vector[i] = 0.0;
                }

                // Normalize et;
                var max = vector.Max();
                if (max > 0)
                {
                    for (int i = 0; i < vector.Length; i++)
                    {
                        vector[i] /= max;
                    }
                }

                return vector;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Davranış vektörü hesaplanırken hata oluştu");
                return new double[64];
            }
        }

        /// <summary>
        /// Davranış metriklerini hesaplar;
        /// </summary>
        private async Task<Dictionary<string, object>> CalculateBehavioralMetricsAsync(List<BehaviorPattern> patterns, double[] behaviorVector, CancellationToken cancellationToken)
        {
            var metrics = new Dictionary<string, object>();

            try
            {
                // Tutarlılık skoru;
                metrics["ConsistencyScore"] = CalculateConsistencyScore(patterns);

                // Çeşitlilik skoru;
                metrics["DiversityScore"] = CalculateDiversityScore(patterns);

                // Öngörülebilirlik skoru;
                metrics["PredictabilityScore"] = CalculatePredictabilityScore(patterns);

                // Aktivite seviyesi;
                metrics["ActivityLevel"] = CalculateActivityLevel(patterns);

                // Anomali oranı;
                metrics["AnomalyRate"] = patterns.Any() ? (double)patterns.Count(p => p.IsAnomaly) / patterns.Count : 0.0;

                // Pattern istikrarı;
                metrics["PatternStability"] = CalculatePatternStability(patterns);

                // Öğrenme oranı;
                metrics["LearningRate"] = CalculateLearningRate(patterns);

                // Verimlilik skoru;
                metrics["EfficiencyScore"] = CalculateEfficiencyScore(patterns, behaviorVector);

                // Adaptasyon yeteneği;
                metrics["AdaptabilityScore"] = CalculateAdaptabilityScore(patterns);

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Davranış metrikleri hesaplanırken hata oluştu");
                return metrics;
            }
        }

        #region Pattern Extraction Algorithms;

        /// <summary>
        /// Sıralı kalıpları çıkarır;
        /// </summary>
        private async Task<List<BehaviorPattern>> ExtractSequentialPatternsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var patterns = new List<BehaviorPattern>();

            try
            {
                // Olayları zamana göre sırala;
                var sortedEvents = events.OrderBy(e => e.Timestamp).ToList();

                // Pencere boyutu ile sıralı kalıpları ara;
                for (int windowSize = MIN_SEQUENCE_LENGTH; windowSize <= Math.Min(MAX_SEQUENCE_LENGTH, sortedEvents.Count); windowSize++)
                {
                    for (int i = 0; i <= sortedEvents.Count - windowSize; i++)
                    {
                        var window = sortedEvents.Skip(i).Take(windowSize).ToList();

                        // Sequence'i oluştur;
                        var sequence = window.Select(e => e.EventType).ToList();
                        var sequenceKey = string.Join("->", sequence);

                        // Pattern oluştur;
                        var pattern = new BehaviorPattern;
                        {
                            PatternName = $"SequentialPattern_{sequenceKey}",
                            Type = PatternType.SequentialPattern,
                            Sequence = sequence,
                            Confidence = CalculateSequenceConfidence(window),
                            Support = CalculateSequenceSupport(sequence, sortedEvents),
                            Features = ExtractFeaturesFromEvents(window)
                        };

                        // Threshold kontrolü;
                        if (pattern.Confidence >= MIN_CONFIDENCE && pattern.Support >= MIN_SUPPORT)
                        {
                            patterns.Add(pattern);
                        }
                    }
                }

                return patterns.DistinctBy(p => string.Join("->", p.Sequence)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sıralı kalıplar çıkarılırken hata oluştu");
                return patterns;
            }
        }

        /// <summary>
        /// Sıkça tekrarlanan kalıpları çıkarır;
        /// </summary>
        private async Task<List<BehaviorPattern>> ExtractFrequentPatternsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var patterns = new List<BehaviorPattern>();

            try
            {
                // Event tipi frekanslarını hesapla;
                var eventFrequencies = events;
                    .GroupBy(e => e.EventType)
                    .ToDictionary(g => g.Key, g => g.Count());

                var totalEvents = events.Count;

                // Frekans eşiği üzerindeki event'leri bul;
                var frequentEvents = eventFrequencies;
                    .Where(kvp => (double)kvp.Value / totalEvents >= MIN_SUPPORT)
                    .ToList();

                // Frequent pattern'ler oluştur;
                foreach (var frequentEvent in frequentEvents)
                {
                    var pattern = new BehaviorPattern;
                    {
                        PatternName = $"FrequentPattern_{frequentEvent.Key}",
                        Type = PatternType.FrequentPattern,
                        Support = (double)frequentEvent.Value / totalEvents,
                        Confidence = 1.0, // Frequent pattern'ler için yüksek güven;
                        Features = new List<BehaviorFeature>
                        {
                            new BehaviorFeature;
                            {
                                FeatureName = "EventFrequency",
                                Type = FeatureType.Frequency,
                                Values = new double[] { frequentEvent.Value },
                                Weight = 1.0;
                            }
                        }
                    };

                    patterns.Add(pattern);
                }

                // Event çiftleri için frequent pattern'ler;
                if (events.Count >= 2)
                {
                    var eventPairs = new Dictionary<string, int>();

                    for (int i = 0; i < events.Count - 1; i++)
                    {
                        var pair = $"{events[i].EventType}+{events[i + 1].EventType}";
                        if (eventPairs.ContainsKey(pair))
                            eventPairs[pair]++;
                        else;
                            eventPairs[pair] = 1;
                    }

                    var frequentPairs = eventPairs;
                        .Where(kvp => (double)kvp.Value / (events.Count - 1) >= MIN_SUPPORT)
                        .ToList();

                    foreach (var frequentPair in frequentPairs)
                    {
                        var pattern = new BehaviorPattern;
                        {
                            PatternName = $"FrequentPair_{frequentPair.Key}",
                            Type = PatternType.FrequentPattern,
                            Support = (double)frequentPair.Value / (events.Count - 1),
                            Confidence = 0.9,
                            Sequence = frequentPair.Key.Split('+').ToList()
                        };

                        patterns.Add(pattern);
                    }
                }

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sıkça tekrarlanan kalıplar çıkarılırken hata oluştu");
                return patterns;
            }
        }

        /// <summary>
        /// Zaman serisi kalıplarını çıkarır;
        /// </summary>
        private async Task<List<BehaviorPattern>> ExtractTimeSeriesPatternsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var patterns = new List<BehaviorPattern>();

            try
            {
                // Saat bazlı aktivite;
                var hourlyActivity = events;
                    .GroupBy(e => e.Timestamp.Hour)
                    .OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Günlük pattern'ler;
                var dailyPatterns = ExtractDailyPatterns(hourlyActivity);
                patterns.AddRange(dailyPatterns);

                // Haftalık pattern'ler;
                var weeklyActivity = events;
                    .GroupBy(e => e.Timestamp.DayOfWeek)
                    .ToDictionary(g => g.Key, g => g.Count());

                var weeklyPatterns = ExtractWeeklyPatterns(weeklyActivity);
                patterns.AddRange(weeklyPatterns);

                // Trend pattern'leri;
                var trendPatterns = ExtractTrendPatterns(events);
                patterns.AddRange(trendPatterns);

                // Döngüsel pattern'ler;
                var cyclicalPatterns = ExtractCyclicalPatterns(events);
                patterns.AddRange(cyclicalPatterns);

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Zaman serisi kalıpları çıkarılırken hata oluştu");
                return patterns;
            }
        }

        /// <summary>
        /// İlişkisel kalıpları çıkarır;
        /// </summary>
        private async Task<List<BehaviorPattern>> ExtractAssociationPatternsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var patterns = new List<BehaviorPattern>();

            try
            {
                // Apriori algoritması benzeri ilişki kuralları;
                var eventTypes = events.Select(e => e.EventType).Distinct().ToList();

                // Tekli öğeler;
                var singleItems = new Dictionary<string, int>();
                foreach (var eventType in eventTypes)
                {
                    var count = events.Count(e => e.EventType == eventType);
                    if ((double)count / events.Count >= MIN_SUPPORT)
                    {
                        singleItems[eventType] = count;
                    }
                }

                // İkili öğe setleri;
                var doubleItems = new Dictionary<string, int>();
                foreach (var item1 in singleItems.Keys)
                {
                    foreach (var item2 in singleItems.Keys.Where(k => k != item1))
                    {
                        var count = events.Count(e =>
                            events.Any(e2 => e2.EventType == item1) &&
                            events.Any(e2 => e2.EventType == item2));

                        if ((double)count / events.Count >= MIN_SUPPORT)
                        {
                            doubleItems[$"{item1}+{item2}"] = count;
                        }
                    }
                }

                // İlişki kuralları oluştur;
                foreach (var doubleItem in doubleItems)
                {
                    var items = doubleItem.Key.Split('+');
                    var item1 = items[0];
                    var item2 = items[1];

                    var item1Count = singleItems[item1];
                    var confidence = (double)doubleItem.Value / item1Count;

                    if (confidence >= MIN_CONFIDENCE)
                    {
                        var pattern = new BehaviorPattern;
                        {
                            PatternName = $"AssociationRule_{item1}=>{item2}",
                            Type = PatternType.AssociationPattern,
                            Support = (double)doubleItem.Value / events.Count,
                            Confidence = confidence,
                            PatternRules = new Dictionary<string, object>
                            {
                                ["Antecedent"] = item1,
                                ["Consequent"] = item2,
                                ["Lift"] = confidence / ((double)singleItems[item2] / events.Count)
                            }
                        };

                        patterns.Add(pattern);
                    }
                }

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "İlişkisel kalıplar çıkarılırken hata oluştu");
                return patterns;
            }
        }

        /// <summary>
        /// Sinir ağı ile kalıpları çıkarır;
        /// </summary>
        private async Task<List<BehaviorPattern>> ExtractNeuralPatternsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var patterns = new List<BehaviorPattern>();

            try
            {
                // Event'leri vektörlere dönüştür;
                var eventVectors = events.Select(e => ConvertEventToVector(e)).ToArray();

                if (eventVectors.Length < 10) // Minimum örnek sayısı;
                    return patterns;

                // Sinir ağı ile kümeleme;
                var clusters = await _neuralNetwork.ClusterAsync(eventVectors, cancellationToken);

                // Her küme için pattern oluştur;
                foreach (var cluster in clusters)
                {
                    var clusterEvents = events.Where((e, i) => cluster.Contains(i)).ToList();

                    if (clusterEvents.Any())
                    {
                        var pattern = new BehaviorPattern;
                        {
                            PatternName = $"NeuralCluster_{cluster.GetHashCode():X}",
                            Type = PatternType.PredictivePattern,
                            Confidence = CalculateClusterConfidence(clusterEvents),
                            Support = (double)clusterEvents.Count / events.Count,
                            Features = ExtractFeaturesFromEvents(clusterEvents)
                        };

                        patterns.Add(pattern);
                    }
                }

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sinir ağı kalıpları çıkarılırken hata oluştu");
                return patterns;
            }
        }

        #endregion;

        #region Anomaly Detection Algorithms;

        /// <summary>
        /// İstatistiksel anomalileri tespit eder;
        /// </summary>
        private async Task<List<string>> DetectStatisticalAnomaliesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var anomalies = new List<string>();

            try
            {
                if (events.Count < 10) // Minimum örnek sayısı;
                    return anomalies;

                // Zaman aralıkları;
                var timeIntervals = new List<double>();
                for (int i = 1; i < events.Count; i++)
                {
                    var interval = (events[i].Timestamp - events[i - 1].Timestamp).TotalSeconds;
                    timeIntervals.Add(interval);
                }

                if (timeIntervals.Any())
                {
                    var mean = timeIntervals.Average();
                    var stdDev = timeIntervals.StdDev();

                    // Z-skor hesapla;
                    for (int i = 0; i < timeIntervals.Count; i++)
                    {
                        var zScore = Math.Abs((timeIntervals[i] - mean) / stdDev);
                        if (zScore > 3.0) // 3 sigma kuralı;
                        {
                            anomalies.Add($"StatisticalAnomaly_TimeInterval_{events[i].EventId}");
                        }
                    }
                }

                // Event frekans anomalileri;
                var eventCounts = events.GroupBy(e => e.EventType)
                    .ToDictionary(g => g.Key, g => g.Count());

                var totalEvents = events.Count;
                var expectedFrequency = 1.0 / eventCounts.Count; // Uniform dağılım varsayımı;

                foreach (var kvp in eventCounts)
                {
                    var observedFrequency = (double)kvp.Value / totalEvents;
                    var deviation = Math.Abs(observedFrequency - expectedFrequency) / expectedFrequency;

                    if (deviation > 2.0) // %200 sapma;
                    {
                        anomalies.Add($"StatisticalAnomaly_Frequency_{kvp.Key}");
                    }
                }

                return anomalies;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "İstatistiksel anomaliler tespit edilirken hata oluştu");
                return anomalies;
            }
        }

        /// <summary>
        /// Sıralı anomalileri tespit eder;
        /// </summary>
        private async Task<List<string>> DetectSequentialAnomaliesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var anomalies = new List<string>();

            try
            {
                // Markov model ile sıralı anomaliler;
                var transitionMatrix = BuildTransitionMatrix(events);

                for (int i = 1; i < events.Count; i++)
                {
                    var fromEvent = events[i - 1].EventType;
                    var toEvent = events[i].EventType;

                    if (transitionMatrix.TryGetValue(fromEvent, out var transitions))
                    {
                        if (transitions.TryGetValue(toEvent, out var probability))
                        {
                            if (probability < 0.1) // Düşük geçiş olasılığı;
                            {
                                anomalies.Add($"SequentialAnomaly_{fromEvent}->{toEvent}_{events[i].EventId}");
                            }
                        }
                        else;
                        {
                            // Hiç görülmemiş geçiş;
                            anomalies.Add($"SequentialAnomaly_NewTransition_{fromEvent}->{toEvent}_{events[i].EventId}");
                        }
                    }
                }

                return anomalies;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sıralı anomaliler tespit edilirken hata oluştu");
                return anomalies;
            }
        }

        /// <summary>
        /// Zaman anomalilerini tespit eder;
        /// </summary>
        private async Task<List<string>> DetectTemporalAnomaliesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var anomalies = new List<string>();

            try
            {
                // Alışılmadık saatlerde aktivite;
                var normalHours = new HashSet<int> { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 }; // Çalışma saatleri;
                var unusualEvents = events.Where(e => !normalHours.Contains(e.Timestamp.Hour)).ToList();

                foreach (var unusualEvent in unusualEvents)
                {
                    anomalies.Add($"TemporalAnomaly_UnusualHour_{unusualEvent.Timestamp.Hour}_{unusualEvent.EventId}");
                }

                // Hafta sonu aktivitesi (eğer normalde yoksa)
                var weekendEvents = events.Where(e => e.Timestamp.DayOfWeek == DayOfWeek.Saturday || e.Timestamp.DayOfWeek == DayOfWeek.Sunday).ToList();
                var weekdayEvents = events.Where(e => e.Timestamp.DayOfWeek >= DayOfWeek.Monday && e.Timestamp.DayOfWeek <= DayOfWeek.Friday).ToList();

                if (weekdayEvents.Count > 0 && weekendEvents.Count > 0)
                {
                    var weekendRatio = (double)weekendEvents.Count / weekdayEvents.Count;
                    if (weekendRatio > 0.5) // Normalden fazla hafta sonu aktivitesi;
                    {
                        anomalies.Add($"TemporalAnomaly_HighWeekendActivity");
                    }
                }

                // Gece aktivitesi;
                var nightEvents = events.Where(e => e.Timestamp.Hour >= 22 || e.Timestamp.Hour <= 6).ToList();
                if (nightEvents.Count > events.Count * 0.3) // %30'dan fazla gece aktivitesi;
                {
                    anomalies.Add($"TemporalAnomaly_HighNightActivity");
                }

                return anomalies;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Zaman anomalileri tespit edilirken hata oluştu");
                return anomalies;
            }
        }

        /// <summary>
        /// Sinir ağı ile anomali tespiti;
        /// </summary>
        private async Task<List<string>> DetectNeuralAnomaliesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var anomalies = new List<string>();

            try
            {
                if (events.Count < 5)
                    return anomalies;

                // Event'leri vektörlere dönüştür;
                var eventVectors = events.Select(e => ConvertEventToVector(e)).ToArray();

                // Anomali skorlarını hesapla;
                var anomalyScores = await _neuralNetwork.PredictAnomaliesAsync(eventVectors, cancellationToken);

                for (int i = 0; i < anomalyScores.Length; i++)
                {
                    if (anomalyScores[i] > ANOMALY_THRESHOLD)
                    {
                        anomalies.Add($"NeuralAnomaly_{events[i].EventId}_Score{anomalyScores[i]:F2}");
                    }
                }

                return anomalies;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sinir ağı ile anomali tespiti yapılırken hata oluştu");
                return anomalies;
            }
        }

        /// <summary>
        /// Kural tabanlı anomali tespiti;
        /// </summary>
        private async Task<List<string>> DetectRuleBasedAnomaliesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var anomalies = new List<string>();

            try
            {
                // Önceden tanımlanmış kurallar;
                var rules = new List<Func<BehavioralEvent, bool>>
                {
                    // Çok hızlı ardışık event'ler;
                    e => events.Any(e2 =>
                        e2.EventId != e.EventId &&
                        e2.EventType == e.EventType &&
                        Math.Abs((e2.Timestamp - e.Timestamp).TotalSeconds) < 1.0),
                    
                    // Çok uzun event zincirleri;
                    e => events.Count(ev => ev.EventType == e.EventType) > 100,
                    
                    // Alışılmadık yüksek önem seviyesi;
                    e => e.Significance > 0.9 && events.Average(ev => ev.Significance) < 0.3,
                    
                    // Boş veya geçersiz event data;
                    e => e.EventData == null || e.EventData.Count == 0,
                    
                    // Gelecek zamanlı event (saat hatası)
                    e => e.Timestamp > DateTime.UtcNow.AddMinutes(5)
                };

                foreach (var rule in rules)
                {
                    var violatingEvents = events.Where(rule).ToList();
                    foreach (var violatingEvent in violatingEvents)
                    {
                        anomalies.Add($"RuleBasedAnomaly_{violatingEvent.EventId}");
                    }
                }

                return anomalies.Distinct().ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kural tabanlı anomali tespiti yapılırken hata oluştu");
                return anomalies;
            }
        }

        #endregion;

        #region Prediction Algorithms;

        /// <summary>
        /// Event tipi tahminleri yapar;
        /// </summary>
        private async Task<Dictionary<string, double>> PredictEventTypesAsync(string userId, double[] behaviorVector,
            List<BehavioralEvent> recentEvents, TimeSpan futureWindow, CancellationToken cancellationToken)
        {
            var predictions = new Dictionary<string, double>();

            try
            {
                // Son 24 saatteki event tiplerini analiz et;
                var recentEventTypes = recentEvents;
                    .Where(e => e.Timestamp >= DateTime.UtcNow - TimeSpan.FromHours(24))
                    .GroupBy(e => e.EventType)
                    .ToDictionary(g => g.Key, g => g.Count());

                var totalRecentEvents = recentEventTypes.Values.Sum();

                if (totalRecentEvents > 0)
                {
                    foreach (var eventType in recentEventTypes.Keys)
                    {
                        var probability = (double)recentEventTypes[eventType] / totalRecentEvents;

                        // Davranış vektörünü kullanarak probability'i ayarla;
                        var adjustedProbability = probability * (1.0 + behaviorVector[0] * 0.1); // İlk özellik aktivite seviyesi;

                        predictions[eventType] = Math.Min(adjustedProbability, 1.0);
                    }
                }

                // Pattern'lere göre tahmin;
                var userPatterns = await GetPatternsByUserAsync(userId, cancellationToken);
                foreach (var pattern in userPatterns.Where(p => p.Type == PatternType.PredictivePattern))
                {
                    if (pattern.PatternRules.TryGetValue("PredictedEvent", out var predictedEventObj))
                    {
                        var predictedEvent = predictedEventObj.ToString();
                        if (!predictions.ContainsKey(predictedEvent))
                        {
                            predictions[predictedEvent] = pattern.Confidence * 0.5;
                        }
                    }
                }

                return predictions;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event tipi tahmini yapılırken hata oluştu");
                return predictions;
            }
        }

        /// <summary>
        /// Pattern tekrarlama tahminleri yapar;
        /// </summary>
        private async Task<Dictionary<string, double>> PredictPatternOccurrencesAsync(List<BehaviorPattern> patterns,
            double[] behaviorVector, TimeSpan futureWindow, CancellationToken cancellationToken)
        {
            var predictions = new Dictionary<string, double>();

            try
            {
                foreach (var pattern in patterns)
                {
                    // Pattern'in son ne zaman tespit edildiğine bak;
                    var timeSinceLastDetection = DateTime.UtcNow - pattern.LastDetected;

                    // Pattern'in ortalama tekrarlama aralığını hesapla;
                    var avgInterval = pattern.DetectionCount > 1 ?
                        (pattern.LastDetected - pattern.FirstDetected).TotalHours / pattern.DetectionCount :
                        24.0; // Varsayılan 24 saat;

                    // Gelecek pencerede tekrarlama olasılığı;
                    var probability = Math.Exp(-timeSinceLastDetection.TotalHours / avgInterval);

                    // Davranış vektörü ile ayarla;
                    probability *= (1.0 + behaviorVector[1] * 0.2); // İkinci özellik tutarlılık;

                    predictions[pattern.PatternId] = Math.Min(probability, 1.0);
                }

                return predictions;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pattern tekrarlama tahmini yapılırken hata oluştu");
                return predictions;
            }
        }

        /// <summary>
        /// Anomali olasılığını tahmin eder;
        /// </summary>
        private async Task<double> PredictAnomalyProbabilityAsync(string userId, double[] behaviorVector,
            List<BehavioralEvent> recentEvents, TimeSpan futureWindow, CancellationToken cancellationToken)
        {
            try
            {
                double probability = 0.0;

                // Son anomalilere bak;
                var recentAnomalies = recentEvents;
                    .Where(e => e.Significance > 0.8) // Yüksek önem seviyesi = potansiyel anomali;
                    .ToList();

                if (recentAnomalies.Any())
                {
                    var anomalyRate = (double)recentAnomalies.Count / recentEvents.Count;
                    probability += anomalyRate * 0.5;
                }

                // Davranış tutarsızlığı;
                var consistency = behaviorVector[1]; // İkinci özellik tutarlılık;
                probability += (1.0 - consistency) * 0.3;

                // Zaman faktörü (gece saatleri daha riskli)
                var currentHour = DateTime.UtcNow.Hour;
                if (currentHour >= 22 || currentHour <= 6)
                {
                    probability += 0.2;
                }

                // Pattern istikrarsızlığı;
                var userPatterns = await GetPatternsByUserAsync(userId, cancellationToken);
                var unstablePatterns = userPatterns.Count(p => p.Confidence < 0.6);
                if (unstablePatterns > 0)
                {
                    probability += unstablePatterns * 0.05;
                }

                return Math.Min(probability, 1.0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anomali olasılığı tahmini yapılırken hata oluştu");
                return 0.5; // Varsayılan orta risk;
            }
        }

        /// <summary>
        /// Aktivite seviyesini tahmin eder;
        /// </summary>
        private async Task<double> PredictActivityLevelAsync(string userId, double[] behaviorVector, TimeSpan futureWindow, CancellationToken cancellationToken)
        {
            try
            {
                // Tarihsel aktivite pattern'leri;
                var userPatterns = await GetPatternsByUserAsync(userId, cancellationToken);
                var activityPatterns = userPatterns.Where(p => p.Type == PatternType.CyclicalPattern).ToList();

                double prediction = 0.5; // Varsayılan orta seviye;

                if (activityPatterns.Any())
                {
                    // Günün saatine göre aktivite;
                    var currentHour = DateTime.UtcNow.Hour;

                    foreach (var pattern in activityPatterns)
                    {
                        if (pattern.PatternRules.TryGetValue("PeakHours", out var peakHoursObj) &&
                            peakHoursObj is List<int> peakHours)
                        {
                            if (peakHours.Contains(currentHour))
                            {
                                prediction = 0.8; // Yoğun saat;
                            }
                        }
                    }
                }

                // Haftanın günü faktörü;
                var dayOfWeek = (int)DateTime.UtcNow.DayOfWeek;
                if (dayOfWeek >= 1 && dayOfWeek <= 5) // Hafta içi;
                {
                    prediction *= 1.2;
                }
                else // Hafta sonu;
                {
                    prediction *= 0.8;
                }

                // Davranış vektörü ile ayarla;
                prediction *= (1.0 + behaviorVector[0] * 0.3); // İlk özellik aktivite seviyesi;

                return Math.Min(prediction, 1.0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Aktivite seviyesi tahmini yapılırken hata oluştu");
                return 0.5;
            }
        }

        #endregion;

        #region Utility Methods;

        /// <summary>
        /// Event'i vektöre dönüştürür;
        /// </summary>
        private double[] ConvertEventToVector(BehavioralEvent @event)
        {
            var vector = new double[10];

            // Event tipi hash'i;
            vector[0] = @event.EventType?.GetHashCode() % 1000 / 1000.0 ?? 0.0;

            // Zaman özellikleri;
            vector[1] = @event.Timestamp.Hour / 24.0;
            vector[2] = (int)@event.Timestamp.DayOfWeek / 7.0;

            // Önem seviyesi;
            vector[3] = @event.Significance;

            // Event data boyutu;
            vector[4] = @event.EventData?.Count / 100.0 ?? 0.0;

            // Metadata boyutu;
            vector[5] = @event.Metadata?.Count / 50.0 ?? 0.0;

            // Context uzunluğu;
            vector[6] = @event.Context?.Length / 500.0 ?? 0.0;

            // Kalanlar 0.0;

            return vector;
        }

        /// <summary>
        /// Cosine similarity hesaplar;
        /// </summary>
        private double CalculateCosineSimilarity(double[] vector1, double[] vector2)
        {
            if (vector1.Length != vector2.Length)
                return 0.0;

            double dotProduct = 0.0;
            double magnitude1 = 0.0;
            double magnitude2 = 0.0;

            for (int i = 0; i < vector1.Length; i++)
            {
                dotProduct += vector1[i] * vector2[i];
                magnitude1 += vector1[i] * vector1[i];
                magnitude2 += vector2[i] * vector2[i];
            }

            magnitude1 = Math.Sqrt(magnitude1);
            magnitude2 = Math.Sqrt(magnitude2);

            if (magnitude1 == 0.0 || magnitude2 == 0.0)
                return 0.0;

            return dotProduct / (magnitude1 * magnitude2);
        }

        /// <summary>
        /// Pattern benzerliğini hesaplar;
        /// </summary>
        private double CalculatePatternSimilarity(List<BehaviorPattern> patterns1, List<BehaviorPattern> patterns2)
        {
            if (!patterns1.Any() || !patterns2.Any())
                return 0.0;

            var patternNames1 = patterns1.Select(p => p.PatternName).ToHashSet();
            var patternNames2 = patterns2.Select(p => p.PatternName).ToHashSet();

            var intersection = patternNames1.Intersect(patternNames2).Count();
            var union = patternNames1.Union(patternNames2).Count();

            return union > 0 ? (double)intersection / union : 0.0;
        }

        /// <summary>
        /// Standart sapma hesaplar;
        /// </summary>
        private double CalculateStandardDeviation(double[] values)
        {
            if (values.Length < 2)
                return 0.0;

            var mean = values.Average();
            var sumOfSquares = values.Sum(x => (x - mean) * (x - mean));
            return Math.Sqrt(sumOfSquares / (values.Length - 1));
        }

        /// <summary>
        /// Sequence güvenini hesaplar;
        /// </summary>
        private double CalculateSequenceConfidence(List<BehavioralEvent> sequence)
        {
            if (sequence.Count < 2)
                return 0.0;

            // Zaman aralıklarının tutarlılığı;
            var intervals = new List<double>();
            for (int i = 1; i < sequence.Count; i++)
            {
                intervals.Add((sequence[i].Timestamp - sequence[i - 1].Timestamp).TotalSeconds);
            }

            var meanInterval = intervals.Average();
            var stdDevInterval = intervals.StdDev();

            // Düşük standart sapma = yüksek güven;
            var intervalConsistency = stdDevInterval > 0 ? Math.Exp(-stdDevInterval / meanInterval) : 1.0;

            // Event tiplerinin tekrarlanabilirliği;
            var eventTypeConsistency = sequence.GroupBy(e => e.EventType)
                .All(g => g.Count() == 1) ? 1.0 : 0.7; // Benzersiz event'ler daha yüksek güven;

            return (intervalConsistency * 0.6 + eventTypeConsistency * 0.4);
        }

        /// <summary>
        /// Sequence support'unu hesaplar;
        /// </summary>
        private double CalculateSequenceSupport(List<string> sequence, List<BehavioralEvent> allEvents)
        {
            if (sequence.Count < 2 || allEvents.Count < sequence.Count)
                return 0.0;

            int matchCount = 0;
            var eventTypes = allEvents.Select(e => e.EventType).ToList();

            for (int i = 0; i <= eventTypes.Count - sequence.Count; i++)
            {
                bool matches = true;
                for (int j = 0; j < sequence.Count; j++)
                {
                    if (eventTypes[i + j] != sequence[j])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    matchCount++;
            }

            return (double)matchCount / (eventTypes.Count - sequence.Count + 1);
        }

        /// <summary>
        /// Özellikleri event'lerden çıkarır;
        /// </summary>
        private List<BehaviorFeature> ExtractFeaturesFromEvents(List<BehavioralEvent> events)
        {
            var features = new List<BehaviorFeature>();

            if (!events.Any())
                return features;

            // Zaman özellikleri;
            var times = events.Select(e => e.Timestamp.TimeOfDay.TotalSeconds).ToArray();
            features.Add(new BehaviorFeature;
            {
                FeatureName = "TimeOfDay",
                Type = FeatureType.Temporal,
                Values = times,
                Weight = 1.0;
            });

            // Event tipi frekansları;
            var eventTypeCounts = events.GroupBy(e => e.EventType)
                .ToDictionary(g => g.Key, g => g.Count());

            features.Add(new BehaviorFeature;
            {
                FeatureName = "EventFrequencies",
                Type = FeatureType.Frequency,
                Values = eventTypeCounts.Values.Select(v => (double)v).ToArray(),
                Weight = 0.8;
            });

            // Önem seviyeleri;
            var significances = events.Select(e => e.Significance).ToArray();
            features.Add(new BehaviorFeature;
            {
                FeatureName = "SignificanceLevels",
                Type = FeatureType.Numerical,
                Values = significances,
                Weight = 0.6;
            });

            return features;
        }

        /// <summary>
        /// Küme güvenini hesaplar;
        /// </summary>
        private double CalculateClusterConfidence(List<BehavioralEvent> clusterEvents)
        {
            if (clusterEvents.Count < 2)
                return 0.0;

            // Event'lerin birbirine benzerliği;
            var vectors = clusterEvents.Select(e => ConvertEventToVector(e)).ToArray();
            var similarities = new List<double>();

            for (int i = 0; i < vectors.Length; i++)
            {
                for (int j = i + 1; j < vectors.Length; j++)
                {
                    similarities.Add(CalculateCosineSimilarity(vectors[i], vectors[j]));
                }
            }

            return similarities.Any() ? similarities.Average() : 0.0;
        }

        /// <summary>
        /// Geçiş matrisi oluşturur;
        /// </summary>
        private Dictionary<string, Dictionary<string, double>> BuildTransitionMatrix(List<BehavioralEvent> events)
        {
            var matrix = new Dictionary<string, Dictionary<string, double>>();

            if (events.Count < 2)
                return matrix;

            for (int i = 1; i < events.Count; i++)
            {
                var fromEvent = events[i - 1].EventType;
                var toEvent = events[i].EventType;

                if (!matrix.ContainsKey(fromEvent))
                {
                    matrix[fromEvent] = new Dictionary<string, double>();
                }

                if (!matrix[fromEvent].ContainsKey(toEvent))
                {
                    matrix[fromEvent][toEvent] = 0.0;
                }

                matrix[fromEvent][toEvent]++;
            }

            // Normalize et (olasılıklara dönüştür)
            foreach (var fromEvent in matrix.Keys.ToList())
            {
                var total = matrix[fromEvent].Values.Sum();
                foreach (var toEvent in matrix[fromEvent].Keys.ToList())
                {
                    matrix[fromEvent][toEvent] /= total;
                }
            }

            return matrix;
        }

        /// <summary>
        /// Günlük pattern'leri çıkarır;
        /// </summary>
        private List<BehaviorPattern> ExtractDailyPatterns(Dictionary<int, int> hourlyActivity)
        {
            var patterns = new List<BehaviorPattern>();

            if (!hourlyActivity.Any())
                return patterns;

            // Yoğun saatleri bul;
            var peakHours = hourlyActivity;
                .Where(kvp => kvp.Value > hourlyActivity.Values.Average() * 1.5)
                .Select(kvp => kvp.Key)
                .ToList();

            if (peakHours.Any())
            {
                var pattern = new BehaviorPattern;
                {
                    PatternName = "DailyPeakHours",
                    Type = PatternType.CyclicalPattern,
                    Confidence = 0.8,
                    Support = (double)peakHours.Count / 24,
                    PatternRules = new Dictionary<string, object>
                    {
                        ["PeakHours"] = peakHours,
                        ["AverageActivity"] = hourlyActivity.Values.Average()
                    }
                };

                patterns.Add(pattern);
            }

            // Aktivite pattern'leri;
            var activityValues = hourlyActivity.OrderBy(kvp => kvp.Key).Select(kvp => (double)kvp.Value).ToArray();
            var trend = CalculateTrend(activityValues);

            if (Math.Abs(trend) > 0.1) // Belirgin trend;
            {
                var pattern = new BehaviorPattern;
                {
                    PatternName = trend > 0 ? "DailyIncreasingActivity" : "DailyDecreasingActivity",
                    Type = PatternType.TrendPattern,
                    Confidence = Math.Abs(trend),
                    Support = 1.0,
                    PatternRules = new Dictionary<string, object>
                    {
                        ["Trend"] = trend,
                        ["PeakHour"] = hourlyActivity.OrderByDescending(kvp => kvp.Value).First().Key;
                    }
                };

                patterns.Add(pattern);
            }

            return patterns;
        }

        /// <summary>
        /// Haftalık pattern'leri çıkarır;
        /// </summary>
        private List<BehaviorPattern> ExtractWeeklyPatterns(Dictionary<DayOfWeek, int> weeklyActivity)
        {
            var patterns = new List<BehaviorPattern>();

            if (!weeklyActivity.Any())
                return patterns;

            // Hafta içi vs hafta sonu;
            var weekdayActivity = weeklyActivity;
                .Where(kvp => kvp.Key >= DayOfWeek.Monday && kvp.Key <= DayOfWeek.Friday)
                .Sum(kvp => kvp.Value);

            var weekendActivity = weeklyActivity;
                .Where(kvp => kvp.Key == DayOfWeek.Saturday || kvp.Key == DayOfWeek.Sunday)
                .Sum(kvp => kvp.Value);

            var totalActivity = weekdayActivity + weekendActivity;

            if (totalActivity > 0)
            {
                var weekdayRatio = (double)weekdayActivity / totalActivity;
                var weekendRatio = (double)weekendActivity / totalActivity;

                var pattern = new BehaviorPattern;
                {
                    PatternName = weekdayRatio > weekendRatio ? "WeekdayFocused" : "WeekendFocused",
                    Type = PatternType.CyclicalPattern,
                    Confidence = Math.Abs(weekdayRatio - weekendRatio),
                    Support = 1.0,
                    PatternRules = new Dictionary<string, object>
                    {
                        ["WeekdayRatio"] = weekdayRatio,
                        ["WeekendRatio"] = weekendRatio,
                        ["MostActiveDay"] = weeklyActivity.OrderByDescending(kvp => kvp.Value).First().Key.ToString()
                    }
                };

                patterns.Add(pattern);
            }

            return patterns;
        }

        /// <summary>
        /// Trend pattern'lerini çıkarır;
        /// </summary>
        private List<BehaviorPattern> ExtractTrendPatterns(List<BehavioralEvent> events)
        {
            var patterns = new List<BehaviorPattern>();

            if (events.Count < 7) // Minimum 7 gün;
                return patterns;

            // Günlük aktivite trend'i;
            var dailyActivity = events;
                .GroupBy(e => e.Timestamp.Date)
                .OrderBy(g => g.Key)
                .Select(g => (double)g.Count())
                .ToArray();

            if (dailyActivity.Length >= 7)
            {
                var trend = CalculateTrend(dailyActivity);
                var volatility = CalculateVolatility(dailyActivity);

                var pattern = new BehaviorPattern;
                {
                    PatternName = trend > 0 ? "IncreasingActivityTrend" : "DecreasingActivityTrend",
                    Type = PatternType.TrendPattern,
                    Confidence = Math.Abs(trend) * (1.0 - volatility),
                    Support = 1.0,
                    PatternRules = new Dictionary<string, object>
                    {
                        ["Trend"] = trend,
                        ["Volatility"] = volatility,
                        ["AverageDailyActivity"] = dailyActivity.Average()
                    }
                };

                patterns.Add(pattern);
            }

            return patterns;
        }

        /// <summary>
        /// Döngüsel pattern'leri çıkarır;
        /// </summary>
        private List<BehaviorPattern> ExtractCyclicalPatterns(List<BehavioralEvent> events)
        {
            var patterns = new List<BehaviorPattern>();

            if (events.Count < 30) // Minimum 30 event;
                return patterns;

            // Haftalık döngüler;
            var dayOfWeekActivity = events;
                .GroupBy(e => (int)e.Timestamp.DayOfWeek)
                .ToDictionary(g => g.Key, g => g.Count());

            if (dayOfWeekActivity.Count == 7)
            {
                var values = Enumerable.Range(0, 7)
                    .Select(day => dayOfWeekActivity.ContainsKey(day) ? (double)dayOfWeekActivity[day] : 0.0)
                    .ToArray();

                var periodicity = CalculatePeriodicity(values, 7);

                if (periodicity > 0.7)
                {
                    var pattern = new BehaviorPattern;
                    {
                        PatternName = "WeeklyCycle",
                        Type = PatternType.CyclicalPattern,
                        Confidence = periodicity,
                        Support = 1.0,
                        PatternRules = new Dictionary<string, object>
                        {
                            ["CycleLength"] = 7,
                            ["PeakDay"] = dayOfWeekActivity.OrderByDescending(kvp => kvp.Value).First().Key,
                            ["Periodicity"] = periodicity;
                        }
                    };

                    patterns.Add(pattern);
                }
            }

            return patterns;
        }

        /// <summary>
        /// Trend hesaplar;
        /// </summary>
        private double CalculateTrend(double[] values)
        {
            if (values.Length < 2)
                return 0.0;

            // Basit doğrusal regresyon;
            var xValues = Enumerable.Range(0, values.Length).Select(x => (double)x).ToArray();
            var yValues = values;

            var xMean = xValues.Average();
            var yMean = yValues.Average();

            double numerator = 0.0;
            double denominator = 0.0;

            for (int i = 0; i < values.Length; i++)
            {
                numerator += (xValues[i] - xMean) * (yValues[i] - yMean);
                denominator += (xValues[i] - xMean) * (xValues[i] - xMean);
            }

            var slope = denominator != 0 ? numerator / denominator : 0.0;

            // Normalize et;
            var yRange = yValues.Max() - yValues.Min();
            return yRange != 0 ? slope / yRange * values.Length : 0.0;
        }

        /// <summary>
        /// Oynaklık (volatility) hesaplar;
        /// </summary>
        private double CalculateVolatility(double[] values)
        {
            if (values.Length < 2)
                return 0.0;

            var returns = new List<double>();
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i - 1] != 0)
                {
                    returns.Add((values[i] - values[i - 1]) / values[i - 1]);
                }
            }

            if (!returns.Any())
                return 0.0;

            var meanReturn = returns.Average();
            var variance = returns.Sum(r => Math.Pow(r - meanReturn, 2)) / returns.Count;
            return Math.Sqrt(variance);
        }

        /// <summary>
        /// Periyodiklik hesaplar;
        /// </summary>
        private double CalculatePeriodicity(double[] values, int expectedPeriod)
        {
            if (values.Length < expectedPeriod * 2)
                return 0.0;

            // Autocorrelation;
            double maxCorrelation = 0.0;

            for (int lag = 1; lag <= Math.Min(expectedPeriod * 2, values.Length / 2); lag++)
            {
                double correlation = 0.0;
                int count = 0;

                for (int i = 0; i < values.Length - lag; i++)
                {
                    correlation += values[i] * values[i + lag];
                    count++;
                }

                if (count > 0)
                {
                    correlation /= count;
                    if (correlation > maxCorrelation)
                    {
                        maxCorrelation = correlation;
                    }
                }
            }

            // Lag = expectedPeriod'deki correlation;
            double periodCorrelation = 0.0;
            int periodCount = 0;

            for (int i = 0; i < values.Length - expectedPeriod; i++)
            {
                periodCorrelation += values[i] * values[i + expectedPeriod];
                periodCount++;
            }

            if (periodCount > 0)
            {
                periodCorrelation /= periodCount;
            }

            // Periyodiklik skoru;
            var normalizedValues = values.Select(v => v / values.Max()).ToArray();
            var score = periodCorrelation * (1.0 - Math.Abs(expectedPeriod - values.Length / 2) / (values.Length / 2));

            return Math.Max(0.0, Math.Min(1.0, score));
        }

        /// <summary>
        /// Pattern'leri konsolide eder;
        /// </summary>
        private async Task<List<BehaviorPattern>> ConsolidatePatternsAsync(List<BehaviorPattern> patterns, CancellationToken cancellationToken)
        {
            var consolidated = new List<BehaviorPattern>();
            var patternGroups = new Dictionary<string, List<BehaviorPattern>>();

            // Pattern'leri isimlerine göre grupla;
            foreach (var pattern in patterns)
            {
                var key = pattern.PatternName ?? pattern.Type.ToString();

                if (!patternGroups.ContainsKey(key))
                {
                    patternGroups[key] = new List<BehaviorPattern>();
                }

                patternGroups[key].Add(pattern);
            }

            // Her grup için birleştirilmiş pattern oluştur;
            foreach (var group in patternGroups.Values)
            {
                if (group.Count == 1)
                {
                    consolidated.Add(group[0]);
                }
                else;
                {
                    var consolidatedPattern = new BehaviorPattern;
                    {
                        PatternName = group[0].PatternName,
                        Type = group[0].Type,
                        Confidence = group.Average(p => p.Confidence),
                        Support = group.Average(p => p.Support),
                        DetectionCount = group.Sum(p => p.DetectionCount),
                        FirstDetected = group.Min(p => p.FirstDetected),
                        LastDetected = group.Max(p => p.LastDetected),
                        IsAnomaly = group.Any(p => p.IsAnomaly)
                    };

                    // Features'ları birleştir;
                    var allFeatures = group.SelectMany(p => p.Features).ToList();
                    consolidatedPattern.Features = MergeFeatures(allFeatures);

                    consolidated.Add(consolidatedPattern);
                }
            }

            // Threshold'lara göre filtrele;
            consolidated = consolidated;
                .Where(p => p.Confidence >= MIN_CONFIDENCE && p.Support >= MIN_SUPPORT)
                .ToList();

            return consolidated;
        }

        /// <summary>
        /// Özellikleri birleştirir;
        /// </summary>
        private List<BehaviorFeature> MergeFeatures(List<BehaviorFeature> features)
        {
            var mergedFeatures = new Dictionary<string, BehaviorFeature>();

            foreach (var feature in features)
            {
                var key = $"{feature.FeatureName}_{feature.Type}";

                if (!mergedFeatures.ContainsKey(key))
                {
                    mergedFeatures[key] = new BehaviorFeature;
                    {
                        FeatureName = feature.FeatureName,
                        Type = feature.Type,
                        Values = feature.Values.ToArray(),
                        Weight = feature.Weight;
                    };
                }
                else;
                {
                    // Değerleri birleştir;
                    var existing = mergedFeatures[key];
                    var newValues = existing.Values.Concat(feature.Values).ToArray();
                    existing.Values = newValues;
                    existing.Weight = (existing.Weight + feature.Weight) / 2;
                }
            }

            return mergedFeatures.Values.ToList();
        }

        /// <summary>
        /// Eğitim verisini ön işler;
        /// </summary>
        private async Task<List<BehavioralEvent>> PreprocessTrainingDataAsync(List<BehavioralEvent> trainingData, CancellationToken cancellationToken)
        {
            // 1. Boş event'leri temizle;
            var cleanedData = trainingData;
                .Where(e => !string.IsNullOrEmpty(e.EventType))
                .Where(e => e.Timestamp <= DateTime.UtcNow.AddMinutes(5)) // Gelecek zamanlı olanları temizle;
                .ToList();

            // 2. Aykırı değerleri temizle;
            var eventCounts = cleanedData.GroupBy(e => e.EventType).ToDictionary(g => g.Key, g => g.Count());
            var totalEvents = cleanedData.Count;
            var avgEventsPerType = totalEvents / (double)eventCounts.Count;

            var filteredData = cleanedData;
                .Where(e => eventCounts[e.EventType] <= avgEventsPerType * 10) // Ortalamanın 10 katından fazla olanları temizle;
                .ToList();

            // 3. Zaman sıralaması yap;
            filteredData = filteredData;
                .OrderBy(e => e.Timestamp)
                .ToList();

            return filteredData;
        }

        /// <summary>
        /// Özellikleri çıkarır;
        /// </summary>
        private async Task<double[][]> ExtractFeaturesAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            var features = new List<double[]>();

            foreach (var @event in events)
            {
                features.Add(ConvertEventToVector(@event));
            }

            return features.ToArray();
        }

        /// <summary>
        /// Pattern modellerini eğitir;
        /// </summary>
        private async Task TrainPatternModelsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            // Pattern'leri çıkar;
            var patterns = await ExtractPatternsAsync(events, cancellationToken);

            // Pattern'leri kaydet;
            foreach (var pattern in patterns)
            {
                await SavePatternAsync(pattern, cancellationToken);
            }
        }

        /// <summary>
        /// Anomali modellerini eğitir;
        /// </summary>
        private async Task TrainAnomalyModelsAsync(List<BehavioralEvent> events, CancellationToken cancellationToken)
        {
            // Anomalileri tespit et;
            var anomalies = await DetectAnomaliesAsync(events, cancellationToken);

            // Anomalileri pattern olarak kaydet;
            foreach (var anomaly in anomalies)
            {
                var pattern = new BehaviorPattern;
                {
                    PatternName = anomaly,
                    Type = PatternType.AnomalyPattern,
                    Confidence = 0.9,
                    Support = 0.1, // Anomaliler nadirdir;
                    IsAnomaly = true;
                };

                await SavePatternAsync(pattern, cancellationToken);
            }
        }

        /// <summary>
        /// Anomali kontrolü yapar;
        /// </summary>
        private async Task<bool> CheckForAnomalyAsync(BehavioralEvent currentEvent, List<BehavioralEvent> recentEvents, CancellationToken cancellationToken)
        {
            try
            {
                // Hızlı anomali kontrolleri;

                // 1. Çok hızlı ardışık event;
                var veryRecentEvent = recentEvents.LastOrDefault();
                if (veryRecentEvent != null &&
                    (currentEvent.Timestamp - veryRecentEvent.Timestamp).TotalSeconds < 0.1)
                {
                    return true;
                }

                // 2. Alışılmadık event tipi;
                var eventTypeFrequency = recentEvents.Count(e => e.EventType == currentEvent.EventType);
                if (recentEvents.Count > 10 && eventTypeFrequency == 0)
                {
                    return true;
                }

                // 3. Yüksek önem seviyesi;
                if (currentEvent.Significance > 0.9 && recentEvents.Average(e => e.Significance) < 0.3)
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anomali kontrolü yapılırken hata oluştu");
                return false;
            }
        }

        /// <summary>
        /// Pattern eşleştirme yapar;
        /// </summary>
        private async Task<List<BehaviorPattern>> MatchPatternsAsync(BehavioralEvent currentEvent, List<BehavioralEvent> recentEvents, CancellationToken cancellationToken)
        {
            var matchedPatterns = new List<BehaviorPattern>();

            try
            {
                // Kullanıcı pattern'lerini getir;
                var userPatterns = await GetPatternsByUserAsync(currentEvent.UserId, cancellationToken);

                // Event sequence'ini oluştur;
                var eventSequence = recentEvents;
                    .TakeLast(10)
                    .Select(e => e.EventType)
                    .Concat(new[] { currentEvent.EventType })
                    .ToList();

                // Pattern'leri eşleştir;
                foreach (var pattern in userPatterns)
                {
                    if (pattern.Sequence != null && pattern.Sequence.Any())
                    {
                        // Sequence eşleştirme;
                        var matchScore = CalculateSequenceMatchScore(eventSequence, pattern.Sequence);
                        if (matchScore > 0.8)
                        {
                            matchedPatterns.Add(pattern);
                        }
                    }
                    else;
                    {
                        // Event tipi eşleştirme;
                        if (pattern.Features.Any(f => f.FeatureName.Contains("Event")) &&
                            pattern.PatternName.Contains(currentEvent.EventType))
                        {
                            matchedPatterns.Add(pattern);
                        }
                    }
                }

                return matchedPatterns;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pattern eşleştirme yapılırken hata oluştu");
                return matchedPatterns;
            }
        }

        /// <summary>
        /// Sequence eşleşme skorunu hesaplar;
        /// </summary>
        private double CalculateSequenceMatchScore(List<string> eventSequence, List<string> patternSequence)
        {
            if (!patternSequence.Any() || !eventSequence.Any())
                return 0.0;

            // Son kısım eşleşmesi;
            var eventSuffix = eventSequence.TakeLast(patternSequence.Count).ToList();
            if (eventSuffix.Count != patternSequence.Count)
                return 0.0;

            int matchCount = 0;
            for (int i = 0; i < eventSuffix.Count; i++)
            {
                if (eventSuffix[i] == patternSequence[i])
                {
                    matchCount++;
                }
            }

            return (double)matchCount / patternSequence.Count;
        }

        /// <summary>
        /// Tutarlılık skorunu hesaplar;
        /// </summary>
        private double CalculateConsistencyScore(List<BehaviorPattern> patterns)
        {
            if (!patterns.Any())
                return 0.0;

            // Pattern güvenlerinin standart sapması;
            var confidences = patterns.Select(p => p.Confidence).ToArray();
            var meanConfidence = confidences.Average();
            var stdDev = confidences.StdDev();

            // Düşük standart sapma = yüksek tutarlılık;
            return stdDev > 0 ? Math.Exp(-stdDev / meanConfidence) : 1.0;
        }

        /// <summary>
        /// Çeşitlilik skorunu hesaplar;
        /// </summary>
        private double CalculateDiversityScore(List<BehaviorPattern> patterns)
        {
            if (!patterns.Any())
                return 0.0;

            // Pattern türleri çeşitliliği;
            var typeCount = patterns.Select(p => p.Type).Distinct().Count();
            var maxTypes = Enum.GetValues(typeof(PatternType)).Length;

            return (double)typeCount / maxTypes;
        }

        /// <summary>
        /// Öngörülebilirlik skorunu hesaplar;
        /// </summary>
        private double CalculatePredictabilityScore(List<BehaviorPattern> patterns)
        {
            if (!patterns.Any())
                return 0.0;

            // Yüksek support ve güven = yüksek öngörülebilirlik;
            var predictability = patterns;
                .Where(p => !p.IsAnomaly)
                .Average(p => p.Support * p.Confidence);

            return predictability;
        }

        /// <summary>
        /// Aktivite seviyesini hesaplar;
        /// </summary>
        private double CalculateActivityLevel(List<BehaviorPattern> patterns)
        {
            if (!patterns.Any())
                return 0.0;

            // Detection count toplamı;
            var totalDetections = patterns.Sum(p => p.DetectionCount);
            var maxPossible = patterns.Count * 100; // Maksimum varsayılan;

            return Math.Min((double)totalDetections / maxPossible, 1.0);
        }

        /// <summary>
        /// Pattern istikrarını hesaplar;
        /// </summary>
        private double CalculatePatternStability(List<BehaviorPattern> patterns)
        {
            if (!patterns.Any())
                return 0.0;

            // Pattern'lerin zaman içindeki değişimi;
            var stabilities = new List<double>();

            foreach (var pattern in patterns)
            {
                var age = (DateTime.UtcNow - pattern.FirstDetected).TotalDays;
                var detectionRate = pattern.DetectionCount / Math.Max(age, 1.0);

                // Sabit detection rate = yüksek istikrar;
                var stability = Math.Exp(-Math.Abs(detectionRate - pattern.DetectionCount / 30.0) / detectionRate);
                stabilities.Add(stability);
            }

            return stabilities.Any() ? stabilities.Average() : 0.0;
        }

        /// <summary>
        /// Öğrenme oranını hesaplar;
        /// </summary>
        private double CalculateLearningRate(List<BehaviorPattern> patterns)
        {
            if (patterns.Count < 2)
                return 0.0;

            // Yeni pattern'lerin oranı;
            var recentPatterns = patterns.Count(p =>
                (DateTime.UtcNow - p.FirstDetected).TotalDays < 7);

            return (double)recentPatterns / patterns.Count;
        }

        /// <summary>
        /// Verimlilik skorunu hesaplar;
        /// </summary>
        private double CalculateEfficiencyScore(List<BehaviorPattern> patterns, double[] behaviorVector)
        {
            if (!patterns.Any())
                return 0.0;

            // Yüksek güven + düşük anomali + yüksek tutarlılık = yüksek verimlilik;
            var avgConfidence = patterns.Average(p => p.Confidence);
            var anomalyRate = (double)patterns.Count(p => p.IsAnomaly) / patterns.Count;
            var consistency = behaviorVector.Length > 1 ? behaviorVector[1] : 0.5;

            return avgConfidence * (1.0 - anomalyRate) * consistency;
        }

        /// <summary>
        /// Adaptasyon yeteneğini hesaplar;
        /// </summary>
        private double CalculateAdaptabilityScore(List<BehaviorPattern> patterns)
        {
            if (!patterns.Any())
                return 0.0;

            // Çeşitli pattern türleri + yeni pattern'ler = yüksek adaptasyon;
            var diversity = CalculateDiversityScore(patterns);
            var learningRate = CalculateLearningRate(patterns);

            return (diversity + learningRate) / 2;
        }

        /// <summary>
        /// Aktivite pattern'lerini çıkarır;
        /// </summary>
        private List<string> ExtractActivityPatterns(List<BehaviorPattern> patterns)
        {
            return patterns;
                .Where(p => p.Type == PatternType.CyclicalPattern || p.Type == PatternType.TrendPattern)
                .Select(p => p.PatternName)
                .ToList();
        }

        /// <summary>
        /// Zaman pattern'lerini çıkarır;
        /// </summary>
        private List<string> ExtractTemporalPatterns(List<BehaviorPattern> patterns)
        {
            return patterns;
                .Where(p => p.PatternName.Contains("Daily") || p.PatternName.Contains("Weekly") ||
                           p.PatternName.Contains("Hour") || p.PatternName.Contains("Time"))
                .Select(p => p.PatternName)
                .ToList();
        }

        /// <summary>
        /// Davranış tutarlılığını hesaplar;
        /// </summary>
        private double CalculateBehavioralConsistency(List<BehaviorPattern> patterns)
        {
            return CalculateConsistencyScore(patterns);
        }

        /// <summary>
        /// Öğrenme adaptasyonunu hesaplar;
        /// </summary>
        private double CalculateLearningAdaptability(List<BehaviorPattern> patterns)
        {
            return CalculateAdaptabilityScore(patterns);
        }

        /// <summary>
        /// PatternDetected event'ini tetikler;
        /// </summary>
        protected virtual void OnPatternDetected(PatternDetectedEventArgs e)
        {
            PatternDetected?.Invoke(this, e);
        }

        /// <summary>
        /// AnomalyDetected event'ini tetikler;
        /// </summary>
        protected virtual void OnAnomalyDetected(AnomalyDetectedEventArgs e)
        {
            AnomalyDetected?.Invoke(this, e);
        }

        /// <summary>
        /// Pattern mining callback'i;
        /// </summary>
        private async void MinePatternsCallback(object state)
        {
            try
            {
                if (_isDisposed)
                    return;

                _logger.LogInformation("Periyodik pattern mining başlatılıyor");

                // Tüm kullanıcılar için pattern mining yap;
                foreach (var userId in _userEvents.Keys)
                {
                    try
                    {
                        var analysis = await AnalyzeBehaviorAsync(userId, TimeSpan.FromDays(7), CancellationToken.None);
                        _logger.LogDebug("Periyodik pattern mining tamamlandı. UserId: {UserId}, Pattern: {PatternCount}",
                            userId, analysis.DetectedPatterns.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Kullanıcı için pattern mining yapılırken hata oluştu. UserId: {UserId}", userId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periyodik pattern mining yapılırken hata oluştu");
            }
        }

        /// <summary>
        /// Model yeniden eğitme callback'i;
        /// </summary>
        private async void RetrainModelCallback(object state)
        {
            try
            {
                if (_isDisposed)
                    return;

                _logger.LogInformation("Model yeniden eğitimi başlatılıyor");

                // Tüm event'leri topla;
                var allEvents = _userEvents.Values.SelectMany(events => events).ToList();

                if (allEvents.Count >= 100) // Minimum eğitim verisi;
                {
                    await TrainModelAsync(allEvents, CancellationToken.None);
                    _logger.LogInformation("Model yeniden eğitimi tamamlandı. Event: {EventCount}", allEvents.Count);
                }
                else;
                {
                    _logger.LogInformation("Yeterli eğitim verisi yok. Event: {EventCount}", allEvents.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Model yeniden eğitimi yapılırken hata oluştu");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _patternMiningTimer?.Dispose();
                    _modelRetrainingTimer?.Dispose();
                    _processingLock?.Dispose();

                    // Pattern'leri kaydet;
                    try
                    {
                        Task.Run(async () =>
                        {
                            foreach (var pattern in _patternCache.Values)
                            {
                                await SavePatternToFileAsync(pattern, CancellationToken.None);
                            }
                        }).Wait(TimeSpan.FromSeconds(10));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "BehaviorAnalyzer dispose edilirken hata oluştu");
                    }
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~BehaviorAnalyzer()
        {
            Dispose(false);
        }

        #endregion;
    }

    /// <summary>
    /// Extension metodlar;
    /// </summary>
    public static class StatisticsExtensions;
    {
        public static double StdDev(this IEnumerable<double> values)
        {
            var valueList = values.ToList();
            if (valueList.Count < 2)
                return 0.0;

            var mean = valueList.Average();
            var sumOfSquares = valueList.Sum(x => (x - mean) * (x - mean));
            return Math.Sqrt(sumOfSquares / (valueList.Count - 1));
        }
    }
}
