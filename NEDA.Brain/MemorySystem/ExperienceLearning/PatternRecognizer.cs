using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Transforms.TimeSeries;
using NEDA.AI.NaturalLanguage;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.MemorySystem;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.NeuralNetwork.PatternRecognition.AudioPatterns;
using NEDA.NeuralNetwork.PatternRecognition.ImagePatterns;
using NEDA.NeuralNetwork.PatternRecognition.TextPatterns;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
{
    /// <summary>
    /// Davranış kalıbı türlerini tanımlayan enum;
    /// </summary>
    public enum PatternType;
    {
        Routine = 1,           // Rutin davranışlar;
        Habit = 2,             // Alışkanlıklar;
        Anomaly = 3,           // Anormal davranışlar;
        Trend = 4,             // Eğilimler;
        Cycle = 5,             // Döngüsel davranışlar;
        Correlation = 6,       // Korelasyonlar;
        Sequence = 7,          // Sıralı davranışlar;
        Cluster = 8,           // Gruplanmış davranışlar;
        Prediction = 9,        // Tahmin kalıpları;
        Association = 10       // İlişkisel kalıplar;
    }

    /// <summary>
    /// Kalıp güven seviyelerini tanımlayan enum;
    /// </summary>
    public enum ConfidenceLevel;
    {
        Low = 1,               // Düşük güven (%0-30)
        Medium = 2,            // Orta güven (%31-70)
        High = 3,              // Yüksek güven (%71-90)
        VeryHigh = 4           // Çok yüksek güven (%91-100)
    }

    /// <summary>
    /// Davranış özelliklerini tanımlayan sınıf;
    /// </summary>
    public class BehaviorFeature;
    {
        public string FeatureName { get; set; } = string.Empty;
        public FeatureType Type { get; set; }
        public double Value { get; set; }
        public double NormalizedValue { get; set; }
        public double Weight { get; set; } = 1.0;
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public enum FeatureType;
        {
            Numerical,
            Categorical,
            Temporal,
            Spatial,
            Frequency,
            Intensity;
        }
    }

    /// <summary>
    /// Tanınan bir davranış kalıbını temsil eden sınıf;
    /// </summary>
    public class RecognizedPattern;
    {
        public string PatternId { get; set; } = Guid.NewGuid().ToString();
        public PatternType Type { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ConfidenceLevel Confidence { get; set; }
        public double ConfidenceScore { get; set; } // 0-1 arası;
        public DateTime FirstDetected { get; set; } = DateTime.UtcNow;
        public DateTime LastDetected { get; set; } = DateTime.UtcNow;
        public int DetectionCount { get; set; } = 1;
        public string UserId { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;

        // Kalıp özellikleri;
        public List<BehaviorFeature> Features { get; set; } = new List<BehaviorFeature>();
        public Dictionary<string, double> FeatureWeights { get; set; } = new Dictionary<string, double>();
        public List<string> Keywords { get; set; } = new List<string>();

        // İstatistikler;
        public double Frequency { get; set; } // Dakikada/saatte/günde;
        public double Duration { get; set; }  // Ortalama süre;
        public double Consistency { get; set; } // Tutarlılık skoru;
        public double Predictability { get; set; } // Tahmin edilebilirlik;

        // Zaman serisi verileri;
        public List<DateTime> OccurrenceTimes { get; set; } = new List<DateTime>();
        public List<double> IntensityLevels { get; set; } = new List<double>();

        // İlişkiler;
        public List<string> RelatedPatternIds { get; set; } = new List<string>();
        public Dictionary<string, double> CorrelationScores { get; set; } = new Dictionary<string, double>();

        // Tahminler;
        public DateTime? NextExpectedOccurrence { get; set; }
        public double ExpectedConfidence { get; set; }

        public void UpdateDetection(DateTime detectionTime, double intensity = 1.0)
        {
            LastDetected = detectionTime;
            DetectionCount++;
            OccurrenceTimes.Add(detectionTime);
            IntensityLevels.Add(intensity);

            // Güven skorunu güncelle;
            UpdateConfidenceScore();

            // Tutarlılık skorunu güncelle;
            UpdateConsistencyScore();

            // Sıklığı hesapla;
            CalculateFrequency();

            // Tahmini güncelle;
            UpdatePrediction();
        }

        private void UpdateConfidenceScore()
        {
            var detectionFactor = Math.Min(DetectionCount / 10.0, 1.0); // Max 10 tespit;
            var timeFactor = (DateTime.UtcNow - FirstDetected).TotalDays > 7 ? 1.0 :
                           (DateTime.UtcNow - FirstDetected).TotalDays / 7.0;
            var consistencyFactor = Consistency;

            ConfidenceScore = (detectionFactor * 0.4) + (timeFactor * 0.3) + (consistencyFactor * 0.3);

            // ConfidenceLevel'i güncelle;
            if (ConfidenceScore >= 0.9) Confidence = ConfidenceLevel.VeryHigh;
            else if (ConfidenceScore >= 0.7) Confidence = ConfidenceLevel.High;
            else if (ConfidenceScore >= 0.3) Confidence = ConfidenceLevel.Medium;
            else Confidence = ConfidenceLevel.Low;
        }

        private void UpdateConsistencyScore()
        {
            if (OccurrenceTimes.Count < 2) return;

            var intervals = new List<double>();
            for (int i = 1; i < OccurrenceTimes.Count; i++)
            {
                var interval = (OccurrenceTimes[i] - OccurrenceTimes[i - 1]).TotalMinutes;
                intervals.Add(interval);
            }

            if (intervals.Any())
            {
                var mean = intervals.Average();
                var variance = intervals.Select(x => Math.Pow(x - mean, 2)).Average();
                var stdDev = Math.Sqrt(variance);

                // Standart sapma ne kadar küçükse tutarlılık o kadar yüksek;
                Consistency = Math.Max(0, 1 - (stdDev / mean));
            }
        }

        private void CalculateFrequency()
        {
            if (OccurrenceTimes.Count < 2) return;

            var totalDuration = (LastDetected - FirstDetected).TotalDays;
            if (totalDuration > 0)
            {
                Frequency = DetectionCount / totalDuration; // Günlük frekans;
            }
        }

        private void UpdatePrediction()
        {
            if (OccurrenceTimes.Count < 3) return;

            var intervals = new List<double>();
            for (int i = 1; i < OccurrenceTimes.Count; i++)
            {
                var interval = (OccurrenceTimes[i] - OccurrenceTimes[i - 1]).TotalMinutes;
                intervals.Add(interval);
            }

            if (intervals.Any())
            {
                var avgInterval = intervals.Average();
                var lastInterval = intervals.Last();

                // ARIMA benzeri basit tahmin;
                var predictedInterval = (avgInterval * 0.7) + (lastInterval * 0.3);
                NextExpectedOccurrence = LastDetected.AddMinutes(predictedInterval);

                // Tahmin güveni;
                var intervalVariance = intervals.Select(x => Math.Pow(x - avgInterval, 2)).Average();
                ExpectedConfidence = Math.Max(0, 1 - (Math.Sqrt(intervalVariance) / avgInterval));
            }
        }

        public bool IsAnomaly()
        {
            return Type == PatternType.Anomaly ||
                   (Consistency < 0.3 && DetectionCount > 5) ||
                   IntensityLevels.Any(i => i > 2.0); // Yoğunluk eşiği;
        }

        public bool IsHabit()
        {
            return Type == PatternType.Habit ||
                   (Consistency > 0.7 && Frequency > 0.5 && DetectionCount > 10);
        }

        public bool IsRoutine()
        {
            return Type == PatternType.Routine ||
                   (Consistency > 0.8 && Frequency > 1.0 && DetectionCount > 20);
        }
    }

    /// <summary>
    /// Kalıp tanıma sonucunu temsil eden sınıf;
    /// </summary>
    public class PatternRecognitionResult;
    {
        public string RecognitionId { get; set; } = Guid.NewGuid().ToString();
        public DateTime RecognitionTime { get; set; } = DateTime.UtcNow;
        public string UserId { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;

        // Tanınan kalıplar;
        public List<RecognizedPattern> RecognizedPatterns { get; set; } = new List<RecognizedPattern>();
        public List<RecognizedPattern> NewPatterns { get; set; } = new List<RecognizedPattern>();
        public List<RecognizedPattern> UpdatedPatterns { get; set; } = new List<RecognizedPattern>();

        // İstatistikler;
        public int TotalPatterns { get; set; }
        public int AnomalyCount { get; set; }
        public int HabitCount { get; set; }
        public int RoutineCount { get; set; }

        // Analiz metrikleri;
        public double ProcessingTimeMs { get; set; }
        public double AverageConfidence { get; set; }
        public Dictionary<PatternType, int> PatternTypeDistribution { get; set; } = new Dictionary<PatternType, int>();

        // Öneriler;
        public List<PatternRecommendation> Recommendations { get; set; } = new List<PatternRecommendation>();

        public void CalculateMetrics()
        {
            TotalPatterns = RecognizedPatterns.Count;
            AnomalyCount = RecognizedPatterns.Count(p => p.IsAnomaly());
            HabitCount = RecognizedPatterns.Count(p => p.IsHabit());
            RoutineCount = RecognizedPatterns.Count(p => p.IsRoutine());

            if (RecognizedPatterns.Any())
            {
                AverageConfidence = RecognizedPatterns.Average(p => p.ConfidenceScore);
            }

            PatternTypeDistribution = RecognizedPatterns;
                .GroupBy(p => p.Type)
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>
    /// Kalıp tabanlı öneri sınıfı;
    /// </summary>
    public class PatternRecommendation;
    {
        public string RecommendationId { get; set; } = Guid.NewGuid().ToString();
        public RecommendationType Type { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string TargetPatternId { get; set; } = string.Empty;
        public double RelevanceScore { get; set; }
        public double ImpactScore { get; set; }
        public double Priority { get; set; }
        public List<string> ActionItems { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public enum RecommendationType;
        {
            OptimizeHabit,
            BreakHabit,
            EstablishRoutine,
            AddressAnomaly,
            LeverageTrend,
            ImproveConsistency,
            EnhanceEfficiency,
            ReduceRisk;
        }
    }

    /// <summary>
    /// Kalıp tanıma algoritma konfigürasyonu;
    /// </summary>
    public class PatternRecognitionConfig;
    {
        // Temel konfigürasyon;
        public int MinimumSamples { get; set; } = 10;
        public double ConfidenceThreshold { get; set; } = 0.7;
        public double AnomalyThreshold { get; set; } = 0.3;
        public int MaxPatternsPerUser { get; set; } = 100;
        public TimeSpan PatternExpiration { get; set; } = TimeSpan.FromDays(90);

        // Algoritma parametreleri;
        public double ClusterTolerance { get; set; } = 0.5;
        public int WindowSize { get; set; } = 10;
        public int PredictionHorizon { get; set; } = 5;
        public double SimilarityThreshold { get; set; } = 0.8;

        // Özellik ağırlıkları;
        public Dictionary<string, double> FeatureWeights { get; set; } = new Dictionary<string, double>
        {
            { "Temporal", 0.3 },
            { "Frequency", 0.25 },
            { "Duration", 0.2 },
            { "Intensity", 0.15 },
            { "Context", 0.1 }
        };

        // Performans ayarları;
        public int BatchSize { get; set; } = 100;
        public int MaxProcessingThreads { get; set; } = Environment.ProcessorCount;
        public bool EnableRealTimeProcessing { get; set; } = true;
        public bool EnableHistoricalAnalysis { get; set; } = true;
        public bool EnableCrossUserPatterns { get; set; } = false;

        // ML.NET ayarları;
        public string MLModelPath { get; set; } = "Models/PatternRecognition";
        public int TrainingEpochs { get; set; } = 100;
        public double LearningRate { get; set; } = 0.01;
    }

    /// <summary>
    /// Davranış kalıplarını tanıyan ve analiz eden gelişmiş sistem;
    /// </summary>
    public interface IPatternRecognizer : IDisposable
    {
        // Kalıp tanıma operasyonları;
        Task<PatternRecognitionResult> RecognizePatternsAsync(string userId, List<BehaviorFeature> features, string context = "");
        Task<PatternRecognitionResult> RecognizePatternsBatchAsync(string userId, List<List<BehaviorFeature>> featureBatches, string context = "");

        // Kalıp yönetimi;
        Task<RecognizedPattern> GetPatternAsync(string patternId, string userId);
        Task<List<RecognizedPattern>> GetUserPatternsAsync(string userId, PatternType? type = null);
        Task<List<RecognizedPattern>> GetPatternsByContextAsync(string context, string userId = null);
        Task<bool> DeletePatternAsync(string patternId, string userId);

        // Analiz ve raporlama;
        Task<Dictionary<string, double>> GetPatternStatisticsAsync(string userId);
        Task<List<RecognizedPattern>> DetectAnomaliesAsync(string userId, TimeSpan? timeWindow = null);
        Task<List<RecognizedPattern>> PredictNextPatternsAsync(string userId, int horizon = 5);
        Task<Dictionary<string, double>> GetPatternCorrelationsAsync(string userId);

        // Öğrenme ve optimizasyon;
        Task TrainModelAsync(string userId, List<RecognizedPattern> trainingData);
        Task OptimizeWeightsAsync(string userId);
        Task RecalibrateModelsAsync(string userId);

        // Sistem operasyonları;
        Task InitializeForUserAsync(string userId);
        Task CleanupOldPatternsAsync(string userId, TimeSpan? olderThan = null);
        Task ExportPatternsAsync(string userId, string exportPath);
        Task ImportPatternsAsync(string userId, string importPath);
    }

    /// <summary>
    /// Davranış kalıp tanıma sisteminin implementasyonu;
    /// </summary>
    public class PatternRecognizer : IPatternRecognizer;
    {
        private readonly ILogger<PatternRecognizer> _logger;
        private readonly IImageRecognizer _imageRecognizer;
        private readonly ITextAnalyzer _textAnalyzer;
        private readonly IAudioAnalyzer _audioAnalyzer;
        private readonly IEventBus _eventBus;
        private readonly IKnowledgeStore _knowledgeStore;
        private readonly MLContext _mlContext;

        private readonly ConcurrentDictionary<string, List<RecognizedPattern>> _userPatterns;
        private readonly ConcurrentDictionary<string, PatternRecognitionConfig> _userConfigs;
        private readonly ConcurrentDictionary<string, ITransformer> _userModels;

        private readonly PatternRecognitionConfig _defaultConfig;
        private readonly object _modelLock = new object();
        private bool _disposed;

        public PatternRecognizer(
            ILogger<PatternRecognizer> logger,
            IImageRecognizer imageRecognizer,
            ITextAnalyzer textAnalyzer,
            IAudioAnalyzer audioAnalyzer,
            IEventBus eventBus,
            IKnowledgeStore knowledgeStore)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _imageRecognizer = imageRecognizer ?? throw new ArgumentNullException(nameof(imageRecognizer));
            _textAnalyzer = textAnalyzer ?? throw new ArgumentNullException(nameof(textAnalyzer));
            _audioAnalyzer = audioAnalyzer ?? throw new ArgumentNullException(nameof(audioAnalyzer));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _knowledgeStore = knowledgeStore ?? throw new ArgumentNullException(nameof(knowledgeStore));

            _mlContext = new MLContext(seed: 42);

            _userPatterns = new ConcurrentDictionary<string, List<RecognizedPattern>>();
            _userConfigs = new ConcurrentDictionary<string, PatternRecognitionConfig>();
            _userModels = new ConcurrentDictionary<string, ITransformer>();

            _defaultConfig = new PatternRecognitionConfig();

            _logger.LogInformation("PatternRecognizer initialized with ML.NET");
        }

        /// <summary>
        /// Davranış özelliklerinden kalıpları tanır;
        /// </summary>
        public async Task<PatternRecognitionResult> RecognizePatternsAsync(
            string userId,
            List<BehaviorFeature> features,
            string context = "")
        {
            ValidateUserId(userId);

            if (features == null || !features.Any())
            {
                throw new ArgumentException("Features cannot be null or empty", nameof(features));
            }

            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogDebug("Recognizing patterns for user {UserId} with {FeatureCount} features",
                    userId, features.Count);

                var config = GetUserConfig(userId);
                var result = new PatternRecognitionResult;
                {
                    UserId = userId,
                    Context = context;
                };

                // Özellikleri normalize et;
                var normalizedFeatures = NormalizeFeatures(features, config);

                // Mevcut kalıpları al;
                var existingPatterns = GetUserPatternsInternal(userId);

                // Yeni kalıpları tanı;
                var recognizedPatterns = await RecognizeNewPatternsAsync(
                    userId, normalizedFeatures, existingPatterns, config, context);

                // Mevcut kalıpları güncelle;
                var updatedPatterns = await UpdateExistingPatternsAsync(
                    userId, normalizedFeatures, existingPatterns, config);

                // Sonuçları birleştir;
                result.RecognizedPatterns.AddRange(recognizedPatterns);
                result.RecognizedPatterns.AddRange(updatedPatterns);
                result.NewPatterns.AddRange(recognizedPatterns.Where(p => !existingPatterns.Any(ep => ep.PatternId == p.PatternId)));
                result.UpdatedPatterns.AddRange(updatedPatterns);

                // Metrikleri hesapla;
                result.CalculateMetrics();
                result.ProcessingTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

                // Öneriler oluştur;
                result.Recommendations = await GenerateRecommendationsAsync(userId, result.RecognizedPatterns);

                // Kalıpları kaydet;
                await SaveUserPatternsAsync(userId, result.RecognizedPatterns);

                // Olay yayınla;
                await _eventBus.PublishAsync(new PatternsRecognizedEvent;
                {
                    UserId = userId,
                    RecognitionId = result.RecognitionId,
                    PatternCount = result.TotalPatterns,
                    NewPatternCount = result.NewPatterns.Count,
                    Context = context,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation(
                    "Recognized {PatternCount} patterns for user {UserId} in {ProcessingTime}ms",
                    result.TotalPatterns, userId, result.ProcessingTimeMs);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recognizing patterns for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to recognize patterns", ex);
            }
        }

        /// <summary>
        /// Toplu kalıp tanıma işlemi;
        /// </summary>
        public async Task<PatternRecognitionResult> RecognizePatternsBatchAsync(
            string userId,
            List<List<BehaviorFeature>> featureBatches,
            string context = "")
        {
            ValidateUserId(userId);

            if (featureBatches == null || !featureBatches.Any())
            {
                throw new ArgumentException("Feature batches cannot be null or empty", nameof(featureBatches));
            }

            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogDebug("Recognizing patterns in batch for user {UserId} with {BatchCount} batches",
                    userId, featureBatches.Count);

                var config = GetUserConfig(userId);
                var result = new PatternRecognitionResult;
                {
                    UserId = userId,
                    Context = context;
                };

                var allRecognizedPatterns = new List<RecognizedPattern>();

                // Paralel işleme;
                var parallelOptions = new ParallelOptions;
                {
                    MaxDegreeOfParallelism = Math.Min(config.MaxProcessingThreads, featureBatches.Count)
                };

                await Parallel.ForEachAsync(featureBatches, parallelOptions, async (batch, cancellationToken) =>
                {
                    var batchResult = await RecognizePatternsAsync(userId, batch, context);
                    lock (allRecognizedPatterns)
                    {
                        allRecognizedPatterns.AddRange(batchResult.RecognizedPatterns);
                    }
                });

                // Benzersiz kalıpları birleştir;
                result.RecognizedPatterns = MergeSimilarPatterns(allRecognizedPatterns, config);
                result.CalculateMetrics();
                result.ProcessingTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

                // Öneriler oluştur;
                result.Recommendations = await GenerateRecommendationsAsync(userId, result.RecognizedPatterns);

                _logger.LogInformation(
                    "Recognized {PatternCount} patterns in batch for user {UserId} in {ProcessingTime}ms",
                    result.TotalPatterns, userId, result.ProcessingTimeMs);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recognizing patterns in batch for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to recognize patterns in batch", ex);
            }
        }

        /// <summary>
        /// Belirli bir kalıbı getirir;
        /// </summary>
        public async Task<RecognizedPattern> GetPatternAsync(string patternId, string userId)
        {
            ValidateUserId(userId);
            ValidatePatternId(patternId);

            try
            {
                _logger.LogDebug("Getting pattern {PatternId} for user {UserId}", patternId, userId);

                var patterns = GetUserPatternsInternal(userId);
                var pattern = patterns.FirstOrDefault(p => p.PatternId == patternId);

                if (pattern == null)
                {
                    throw new KeyNotFoundException($"Pattern {patternId} not found for user {userId}");
                }

                return pattern;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pattern {PatternId} for user {UserId}", patternId, userId);
                throw;
            }
        }

        /// <summary>
        /// Kullanıcının tüm kalıplarını getirir;
        /// </summary>
        public async Task<List<RecognizedPattern>> GetUserPatternsAsync(string userId, PatternType? type = null)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Getting patterns for user {UserId}, type: {PatternType}",
                    userId, type?.ToString() ?? "All");

                var patterns = GetUserPatternsInternal(userId);

                if (type.HasValue)
                {
                    patterns = patterns.Where(p => p.Type == type.Value).ToList();
                }

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting patterns for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Bağlama göre kalıpları getirir;
        /// </summary>
        public async Task<List<RecognizedPattern>> GetPatternsByContextAsync(string context, string userId = null)
        {
            if (string.IsNullOrWhiteSpace(context))
            {
                throw new ArgumentException("Context cannot be null or empty", nameof(context));
            }

            try
            {
                _logger.LogDebug("Getting patterns for context: {Context}, user: {UserId}",
                    context, userId ?? "All");

                var allPatterns = new List<RecognizedPattern>();

                if (!string.IsNullOrEmpty(userId))
                {
                    allPatterns.AddRange(GetUserPatternsInternal(userId));
                }
                else;
                {
                    foreach (var userPatterns in _userPatterns.Values)
                    {
                        allPatterns.AddRange(userPatterns);
                    }
                }

                return allPatterns;
                    .Where(p => p.Context.Equals(context, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.ConfidenceScore)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting patterns for context {Context}", context);
                throw;
            }
        }

        /// <summary>
        /// Kalıbı siler;
        /// </summary>
        public async Task<bool> DeletePatternAsync(string patternId, string userId)
        {
            ValidateUserId(userId);
            ValidatePatternId(patternId);

            try
            {
                _logger.LogDebug("Deleting pattern {PatternId} for user {UserId}", patternId, userId);

                if (!_userPatterns.TryGetValue(userId, out var patterns))
                {
                    return false;
                }

                var removed = patterns.RemoveAll(p => p.PatternId == patternId) > 0;

                if (removed)
                {
                    _logger.LogInformation("Pattern {PatternId} deleted for user {UserId}", patternId, userId);

                    // Olay yayınla;
                    await _eventBus.PublishAsync(new PatternDeletedEvent;
                    {
                        UserId = userId,
                        PatternId = patternId,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting pattern {PatternId} for user {UserId}", patternId, userId);
                throw new PatternRecognitionException("Failed to delete pattern", ex);
            }
        }

        /// <summary>
        /// Kalıp istatistiklerini getirir;
        /// </summary>
        public async Task<Dictionary<string, double>> GetPatternStatisticsAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Getting pattern statistics for user {UserId}", userId);

                var patterns = GetUserPatternsInternal(userId);
                var statistics = new Dictionary<string, double>();

                if (!patterns.Any())
                {
                    return statistics;
                }

                // Temel istatistikler;
                statistics["TotalPatterns"] = patterns.Count;
                statistics["AnomalyCount"] = patterns.Count(p => p.IsAnomaly());
                statistics["HabitCount"] = patterns.Count(p => p.IsHabit());
                statistics["RoutineCount"] = patterns.Count(p => p.IsRoutine());
                statistics["AverageConfidence"] = patterns.Average(p => p.ConfidenceScore);
                statistics["AverageConsistency"] = patterns.Average(p => p.Consistency);
                statistics["AverageFrequency"] = patterns.Average(p => p.Frequency);

                // Zaman bazlı istatistikler;
                var recentPatterns = patterns.Where(p =>
                    (DateTime.UtcNow - p.LastDetected).TotalDays < 7).ToList();
                statistics["RecentPatterns"] = recentPatterns.Count;

                var activePatterns = patterns.Where(p =>
                    p.DetectionCount > 1 &&
                    (DateTime.UtcNow - p.LastDetected).TotalDays < 30).ToList();
                statistics["ActivePatterns"] = activePatterns.Count;

                // Kategori dağılımı;
                var typeDistribution = patterns;
                    .GroupBy(p => p.Type)
                    .ToDictionary(g => $"Type_{g.Key}", g => (double)g.Count());

                foreach (var kvp in typeDistribution)
                {
                    statistics[kvp.Key] = kvp.Value;
                }

                return statistics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting pattern statistics for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Anormal kalıpları tespit eder;
        /// </summary>
        public async Task<List<RecognizedPattern>> DetectAnomaliesAsync(string userId, TimeSpan? timeWindow = null)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Detecting anomalies for user {UserId}", userId);

                var patterns = GetUserPatternsInternal(userId);
                var config = GetUserConfig(userId);

                var anomalies = new List<RecognizedPattern>();

                // Zaman penceresi filtresi;
                if (timeWindow.HasValue)
                {
                    var cutoffTime = DateTime.UtcNow - timeWindow.Value;
                    patterns = patterns.Where(p => p.LastDetected >= cutoffTime).ToList();
                }

                // Anomali tespit algoritmaları;

                // 1. Tutarlılık tabanlı anomali;
                var lowConsistencyPatterns = patterns;
                    .Where(p => p.Consistency < config.AnomalyThreshold && p.DetectionCount > 3)
                    .ToList();
                anomalies.AddRange(lowConsistencyPatterns);

                // 2. Yoğunluk tabanlı anomali;
                var highIntensityPatterns = patterns;
                    .Where(p => p.IntensityLevels.Any(i => i > 2.0))
                    .ToList();
                anomalies.AddRange(highIntensityPatterns);

                // 3. Frekans tabanlı anomali;
                var frequencyStats = patterns;
                    .Where(p => p.Frequency > 0)
                    .Select(p => p.Frequency)
                    .ToList();

                if (frequencyStats.Any())
                {
                    var avgFrequency = frequencyStats.Average();
                    var stdDev = Math.Sqrt(frequencyStats.Select(f => Math.Pow(f - avgFrequency, 2)).Average());

                    var frequencyAnomalies = patterns;
                        .Where(p => Math.Abs(p.Frequency - avgFrequency) > 2 * stdDev)
                        .ToList();
                    anomalies.AddRange(frequencyAnomalies);
                }

                // 4. Zaman serisi anomali tespiti;
                var timeSeriesAnomalies = await DetectTimeSeriesAnomaliesAsync(userId, patterns);
                anomalies.AddRange(timeSeriesAnomalies);

                // Benzersiz anormallikler;
                anomalies = anomalies;
                    .DistinctBy(p => p.PatternId)
                    .Where(p => !anomalies.Any(a => a != p && IsSubsetPattern(p, a)))
                    .ToList();

                // Anomali etiketi ekle;
                foreach (var anomaly in anomalies)
                {
                    anomaly.Type = PatternType.Anomaly;
                }

                _logger.LogInformation("Detected {AnomalyCount} anomalies for user {UserId}", anomalies.Count, userId);

                return anomalies;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting anomalies for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to detect anomalies", ex);
            }
        }

        /// <summary>
        /// Gelecekteki kalıpları tahmin eder;
        /// </summary>
        public async Task<List<RecognizedPattern>> PredictNextPatternsAsync(string userId, int horizon = 5)
        {
            ValidateUserId(userId);

            if (horizon <= 0 || horizon > 50)
            {
                throw new ArgumentException("Horizon must be between 1 and 50", nameof(horizon));
            }

            try
            {
                _logger.LogDebug("Predicting next patterns for user {UserId} with horizon {Horizon}",
                    userId, horizon);

                var patterns = GetUserPatternsInternal(userId);
                var predictions = new List<RecognizedPattern>();

                // Zaman serisi tahmini yap;
                foreach (var pattern in patterns.Where(p => p.OccurrenceTimes.Count >= 3))
                {
                    var predictedPattern = await PredictPatternOccurrenceAsync(pattern, horizon);
                    if (predictedPattern != null)
                    {
                        predictions.Add(predictedPattern);
                    }
                }

                // Tahmin kalıpları oluştur;
                var predictionPatterns = predictions;
                    .OrderByDescending(p => p.ExpectedConfidence)
                    .Take(horizon)
                    .ToList();

                // Tahmin türü ekle;
                foreach (var pattern in predictionPatterns)
                {
                    pattern.Type = PatternType.Prediction;
                }

                _logger.LogInformation("Predicted {PredictionCount} patterns for user {UserId}",
                    predictionPatterns.Count, userId);

                return predictionPatterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error predicting patterns for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to predict patterns", ex);
            }
        }

        /// <summary>
        /// Kalıp korelasyonlarını hesaplar;
        /// </summary>
        public async Task<Dictionary<string, double>> GetPatternCorrelationsAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Calculating pattern correlations for user {UserId}", userId);

                var patterns = GetUserPatternsInternal(userId);
                var correlations = new Dictionary<string, double>();

                if (patterns.Count < 2)
                {
                    return correlations;
                }

                // Çift yönlü korelasyon hesapla;
                for (int i = 0; i < patterns.Count; i++)
                {
                    for (int j = i + 1; j < patterns.Count; j++)
                    {
                        var pattern1 = patterns[i];
                        var pattern2 = patterns[j];

                        var correlation = CalculatePatternCorrelation(pattern1, pattern2);
                        var key = $"{pattern1.Name}_{pattern2.Name}";

                        correlations[key] = correlation;

                        // Kalıplara korelasyon skorlarını kaydet;
                        pattern1.CorrelationScores[pattern2.PatternId] = correlation;
                        pattern2.CorrelationScores[pattern1.PatternId] = correlation;
                    }
                }

                // İlişkili kalıp ID'lerini güncelle;
                foreach (var pattern in patterns)
                {
                    var relatedPatterns = pattern.CorrelationScores;
                        .Where(kv => kv.Value > 0.7)
                        .Select(kv => kv.Key)
                        .ToList();

                    pattern.RelatedPatternIds = relatedPatterns;
                }

                return correlations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating pattern correlations for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Modeli eğitir;
        /// </summary>
        public async Task TrainModelAsync(string userId, List<RecognizedPattern> trainingData)
        {
            ValidateUserId(userId);

            if (trainingData == null || !trainingData.Any())
            {
                throw new ArgumentException("Training data cannot be null or empty", nameof(trainingData));
            }

            try
            {
                _logger.LogInformation("Training model for user {UserId} with {DataCount} samples",
                    userId, trainingData.Count);

                // Eğitim verilerini hazırla;
                var trainingFeatures = ConvertPatternsToFeatures(trainingData);

                // ML.NET pipeline oluştur;
                var pipeline = _mlContext.Transforms.Concatenate("Features",
                        trainingFeatures.Select(f => f.FeatureName).ToArray())
                    .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                    .Append(_mlContext.Clustering.Trainers.KMeans(
                        numberOfClusters: Math.Min(10, trainingData.Count / 10),
                        featureColumnName: "Features"));

                // Modeli eğit;
                var model = pipeline.Fit(_mlContext.Data.LoadFromEnumerable(trainingFeatures));

                // Modeli kaydet;
                lock (_modelLock)
                {
                    _userModels[userId] = model;
                }

                // Model dosyasına kaydet;
                var modelPath = $"{_defaultConfig.MLModelPath}/{userId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
                _mlContext.Model.Save(model, null, modelPath);

                _logger.LogInformation("Model trained and saved for user {UserId}", userId);

                // Olay yayınla;
                await _eventBus.PublishAsync(new ModelTrainedEvent;
                {
                    UserId = userId,
                    ModelPath = modelPath,
                    TrainingSamples = trainingData.Count,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error training model for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to train model", ex);
            }
        }

        /// <summary>
        /// Ağırlıkları optimize eder;
        /// </summary>
        public async Task OptimizeWeightsAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogInformation("Optimizing feature weights for user {UserId}", userId);

                var patterns = GetUserPatternsInternal(userId);
                var config = GetUserConfig(userId);

                if (patterns.Count < _defaultConfig.MinimumSamples)
                {
                    _logger.LogWarning("Not enough patterns for weight optimization for user {UserId}", userId);
                    return;
                }

                // Her özellik için etki analizi yap;
                var featureImportances = new Dictionary<string, double>();

                foreach (var featureName in config.FeatureWeights.Keys)
                {
                    var importance = CalculateFeatureImportance(patterns, featureName);
                    featureImportances[featureName] = importance;
                }

                // Ağırlıkları normalize et ve güncelle;
                var totalImportance = featureImportances.Values.Sum();

                foreach (var kvp in featureImportances)
                {
                    config.FeatureWeights[kvp.Key] = kvp.Value / totalImportance;
                }

                // Güncellenmiş konfigürasyonu kaydet;
                _userConfigs[userId] = config;

                _logger.LogInformation("Feature weights optimized for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing weights for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to optimize weights", ex);
            }
        }

        /// <summary>
        /// Modelleri yeniden kalibre eder;
        /// </summary>
        public async Task RecalibrateModelsAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogInformation("Recalibrating models for user {UserId}", userId);

                // Eski modelleri temizle;
                lock (_modelLock)
                {
                    _userModels.TryRemove(userId, out _);
                }

                // Yeni verilerle modeli yeniden eğit;
                var patterns = GetUserPatternsInternal(userId);

                if (patterns.Count >= _defaultConfig.MinimumSamples)
                {
                    await TrainModelAsync(userId, patterns);
                }

                // Ağırlıkları yeniden optimize et;
                await OptimizeWeightsAsync(userId);

                // Eski kalıpları temizle;
                await CleanupOldPatternsAsync(userId, _defaultConfig.PatternExpiration);

                _logger.LogInformation("Models recalibrated for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recalibrating models for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to recalibrate models", ex);
            }
        }

        /// <summary>
        /// Kullanıcı için sistemi başlatır;
        /// </summary>
        public async Task InitializeForUserAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogInformation("Initializing pattern recognition for user {UserId}", userId);

                // Varsayılan konfigürasyonu ayarla;
                _userConfigs.TryAdd(userId, _defaultConfig);

                // Boş kalıp listesi oluştur;
                _userPatterns.TryAdd(userId, new List<RecognizedPattern>());

                // Bilgi tabanından mevcut kalıpları yükle;
                await LoadPatternsFromKnowledgeStoreAsync(userId);

                _logger.LogInformation("Pattern recognition initialized for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to initialize for user", ex);
            }
        }

        /// <summary>
        /// Eski kalıpları temizler;
        /// </summary>
        public async Task CleanupOldPatternsAsync(string userId, TimeSpan? olderThan = null)
        {
            ValidateUserId(userId);

            try
            {
                var cutoffTime = DateTime.UtcNow - (olderThan ?? _defaultConfig.PatternExpiration);

                _logger.LogInformation("Cleaning up patterns older than {CutoffTime} for user {UserId}",
                    cutoffTime, userId);

                if (!_userPatterns.TryGetValue(userId, out var patterns))
                {
                    return;
                }

                var oldPatterns = patterns;
                    .Where(p => p.LastDetected < cutoffTime && p.ConfidenceScore < 0.5)
                    .ToList();

                var removedCount = patterns.RemoveAll(p => oldPatterns.Any(op => op.PatternId == p.PatternId));

                _logger.LogInformation("Removed {RemovedCount} old patterns for user {UserId}",
                    removedCount, userId);

                // Olay yayınla;
                if (removedCount > 0)
                {
                    await _eventBus.PublishAsync(new PatternsCleanedUpEvent;
                    {
                        UserId = userId,
                        RemovedCount = removedCount,
                        CutoffTime = cutoffTime,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up patterns for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to clean up patterns", ex);
            }
        }

        /// <summary>
        /// Kalıpları dışa aktarır;
        /// </summary>
        public async Task ExportPatternsAsync(string userId, string exportPath)
        {
            ValidateUserId(userId);

            if (string.IsNullOrWhiteSpace(exportPath))
            {
                throw new ArgumentException("Export path cannot be null or empty", nameof(exportPath));
            }

            try
            {
                _logger.LogInformation("Exporting patterns for user {UserId} to {ExportPath}",
                    userId, exportPath);

                var patterns = GetUserPatternsInternal(userId);

                // JSON formatında dışa aktar;
                var exportData = new;
                {
                    UserId = userId,
                    ExportTime = DateTime.UtcNow,
                    PatternCount = patterns.Count,
                    Patterns = patterns;
                };

                // Gerçek uygulamada dosyaya yazma işlemi;
                // await File.WriteAllTextAsync(exportPath, JsonSerializer.Serialize(exportData));

                _logger.LogInformation("Patterns exported for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting patterns for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to export patterns", ex);
            }
        }

        /// <summary>
        /// Kalıpları içe aktarır;
        /// </summary>
        public async Task ImportPatternsAsync(string userId, string importPath)
        {
            ValidateUserId(userId);

            if (string.IsNullOrWhiteSpace(importPath))
            {
                throw new ArgumentException("Import path cannot be null or empty", nameof(importPath));
            }

            try
            {
                _logger.LogInformation("Importing patterns for user {UserId} from {ImportPath}",
                    userId, importPath);

                // Gerçek uygulamada dosyadan okuma işlemi;
                // var importData = JsonSerializer.Deserialize<dynamic>(await File.ReadAllTextAsync(importPath));

                // Kalıpları yükleme işlemi;
                // GetUserPatternsInternal(userId).AddRange(importData.Patterns);

                _logger.LogInformation("Patterns imported for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing patterns for user {UserId}", userId);
                throw new PatternRecognitionException("Failed to import patterns", ex);
            }
        }

        // Yardımcı metodlar;

        private List<RecognizedPattern> GetUserPatternsInternal(string userId)
        {
            return _userPatterns.GetOrAdd(userId, new List<RecognizedPattern>());
        }

        private PatternRecognitionConfig GetUserConfig(string userId)
        {
            return _userConfigs.GetOrAdd(userId, _defaultConfig);
        }

        private List<BehaviorFeature> NormalizeFeatures(List<BehaviorFeature> features, PatternRecognitionConfig config)
        {
            var normalized = new List<BehaviorFeature>();

            foreach (var feature in features)
            {
                var normalizedFeature = new BehaviorFeature;
                {
                    FeatureName = feature.FeatureName,
                    Type = feature.Type,
                    Value = feature.Value,
                    Timestamp = feature.Timestamp,
                    Metadata = feature.Metadata;
                };

                // Min-max normalizasyonu;
                switch (feature.Type)
                {
                    case BehaviorFeature.FeatureType.Numerical:
                        // Örnek normalizasyon - gerçek uygulamada daha gelişmiş;
                        normalizedFeature.NormalizedValue = Math.Min(feature.Value / 100.0, 1.0);
                        break;
                    case BehaviorFeature.FeatureType.Intensity:
                        normalizedFeature.NormalizedValue = Math.Min(feature.Value / 10.0, 1.0);
                        break;
                    case BehaviorFeature.FeatureType.Frequency:
                        normalizedFeature.NormalizedValue = Math.Min(feature.Value / 1000.0, 1.0);
                        break;
                    default:
                        normalizedFeature.NormalizedValue = feature.Value;
                        break;
                }

                // Ağırlık uygula;
                if (config.FeatureWeights.TryGetValue(feature.FeatureName, out var weight))
                {
                    normalizedFeature.Weight = weight;
                    normalizedFeature.NormalizedValue *= weight;
                }

                normalized.Add(normalizedFeature);
            }

            return normalized;
        }

        private async Task<List<RecognizedPattern>> RecognizeNewPatternsAsync(
            string userId,
            List<BehaviorFeature> features,
            List<RecognizedPattern> existingPatterns,
            PatternRecognitionConfig config,
            string context)
        {
            var newPatterns = new List<RecognizedPattern>();

            // Kümeleme algoritması ile yeni kalıpları tespit et;
            var clusters = ClusterFeatures(features, config);

            foreach (var cluster in clusters)
            {
                // Mevcut kalıplarla karşılaştır;
                var isNewPattern = !existingPatterns.Any(ep =>
                    IsSimilarPattern(cluster, ep, config.SimilarityThreshold));

                if (isNewPattern)
                {
                    var newPattern = CreatePatternFromCluster(cluster, userId, context);

                    if (newPattern.ConfidenceScore >= config.ConfidenceThreshold)
                    {
                        newPatterns.Add(newPattern);
                    }
                }
            }

            return newPatterns;
        }

        private async Task<List<RecognizedPattern>> UpdateExistingPatternsAsync(
            string userId,
            List<BehaviorFeature> features,
            List<RecognizedPattern> existingPatterns,
            PatternRecognitionConfig config)
        {
            var updatedPatterns = new List<RecognizedPattern>();

            foreach (var pattern in existingPatterns)
            {
                var matchingFeatures = features;
                    .Where(f => pattern.Features.Any(pf =>
                        IsSimilarFeature(f, pf, config.SimilarityThreshold)))
                    .ToList();

                if (matchingFeatures.Any())
                {
                    // Kalıbı güncelle;
                    var intensity = matchingFeatures.Average(f => f.NormalizedValue);
                    pattern.UpdateDetection(DateTime.UtcNow, intensity);

                    // Yeni özellikler ekle;
                    pattern.Features.AddRange(matchingFeatures);

                    updatedPatterns.Add(pattern);
                }
            }

            return updatedPatterns;
        }

        private List<List<BehaviorFeature>> ClusterFeatures(
            List<BehaviorFeature> features,
            PatternRecognitionConfig config)
        {
            var clusters = new List<List<BehaviorFeature>>();

            if (!features.Any())
            {
                return clusters;
            }

            // Basit mesafe tabanlı kümeleme;
            var unclustered = new List<BehaviorFeature>(features);

            while (unclustered.Any())
            {
                var seed = unclustered.First();
                var cluster = new List<BehaviorFeature> { seed };
                unclustered.Remove(seed);

                for (int i = unclustered.Count - 1; i >= 0; i--)
                {
                    var feature = unclustered[i];
                    var distance = CalculateFeatureDistance(seed, feature);

                    if (distance <= config.ClusterTolerance)
                    {
                        cluster.Add(feature);
                        unclustered.RemoveAt(i);
                    }
                }

                if (cluster.Count >= config.MinimumSamples)
                {
                    clusters.Add(cluster);
                }
            }

            return clusters;
        }

        private RecognizedPattern CreatePatternFromCluster(
            List<BehaviorFeature> cluster,
            string userId,
            string context)
        {
            var pattern = new RecognizedPattern;
            {
                UserId = userId,
                Context = context,
                Features = cluster,
                FirstDetected = cluster.Min(f => f.Timestamp),
                LastDetected = cluster.Max(f => f.Timestamp)
            };

            // Kalıp türünü belirle;
            pattern.Type = DeterminePatternType(cluster);

            // Kalıp adı ve açıklama oluştur;
            pattern.Name = GeneratePatternName(cluster, pattern.Type);
            pattern.Description = GeneratePatternDescription(cluster, pattern.Type);

            // Özellik ağırlıklarını hesapla;
            pattern.FeatureWeights = CalculateFeatureWeights(cluster);

            // Anahtar kelimeler çıkar;
            pattern.Keywords = ExtractKeywords(cluster);

            // Başlangıç istatistiklerini hesapla;
            pattern.UpdateDetection(pattern.LastDetected,
                cluster.Average(f => f.NormalizedValue));

            return pattern;
        }

        private PatternType DeterminePatternType(List<BehaviorFeature> cluster)
        {
            var featureTypes = cluster.Select(f => f.Type).Distinct().ToList();
            var avgValue = cluster.Average(f => f.NormalizedValue);
            var valueVariance = cluster.Select(f => Math.Pow(f.NormalizedValue - avgValue, 2)).Average();

            if (valueVariance > 0.5)
            {
                return PatternType.Anomaly;
            }
            else if (featureTypes.Contains(BehaviorFeature.FeatureType.Temporal) &&
                     cluster.Count > 5)
            {
                return PatternType.Routine;
            }
            else if (cluster.Count > 3 && avgValue > 0.7)
            {
                return PatternType.Habit;
            }
            else;
            {
                return PatternType.Trend;
            }
        }

        private string GeneratePatternName(List<BehaviorFeature> cluster, PatternType type)
        {
            var mainFeature = cluster;
                .OrderByDescending(f => f.Weight * f.NormalizedValue)
                .FirstOrDefault();

            var timeOfDay = cluster;
                .Where(f => f.Type == BehaviorFeature.FeatureType.Temporal)
                .Select(f => f.Timestamp.Hour)
                .GroupBy(h => h)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            var timeLabel = timeOfDay.HasValue ? GetTimeOfDayLabel(timeOfDay.Value) : "Various";

            return $"{type} Pattern: {mainFeature?.FeatureName ?? "Unknown"} at {timeLabel}";
        }

        private string GeneratePatternDescription(List<BehaviorFeature> cluster, PatternType type)
        {
            var featureSummary = cluster;
                .GroupBy(f => f.FeatureName)
                .Select(g => $"{g.Key} ({g.Count()} occurrences)")
                .Take(3)
                .ToList();

            return $"{type} involving {string.Join(", ", featureSummary)}. " +
                   $"Detected {cluster.Count} times with average intensity {cluster.Average(f => f.NormalizedValue):F2}.";
        }

        private Dictionary<string, double> CalculateFeatureWeights(List<BehaviorFeature> cluster)
        {
            var weights = new Dictionary<string, double>();

            var grouped = cluster.GroupBy(f => f.FeatureName);
            foreach (var group in grouped)
            {
                var frequency = (double)group.Count() / cluster.Count;
                var avgIntensity = group.Average(f => f.NormalizedValue);
                weights[group.Key] = frequency * avgIntensity;
            }

            // Normalize;
            var total = weights.Values.Sum();
            if (total > 0)
            {
                foreach (var key in weights.Keys.ToList())
                {
                    weights[key] /= total;
                }
            }

            return weights;
        }

        private List<string> ExtractKeywords(List<BehaviorFeature> cluster)
        {
            var keywords = new List<string>();

            // Özellik adları;
            keywords.AddRange(cluster.Select(f => f.FeatureName).Distinct());

            // Metadatadan anahtar kelimeler;
            foreach (var feature in cluster)
            {
                if (feature.Metadata.TryGetValue("keywords", out var value) &&
                    value is List<string> featureKeywords)
                {
                    keywords.AddRange(featureKeywords);
                }
            }

            return keywords.Distinct().ToList();
        }

        private async Task<List<PatternRecommendation>> GenerateRecommendationsAsync(
            string userId,
            List<RecognizedPattern> patterns)
        {
            var recommendations = new List<PatternRecommendation>();

            // Alışkanlık optimizasyon önerileri;
            var habits = patterns.Where(p => p.IsHabit()).ToList();
            foreach (var habit in habits)
            {
                if (habit.Consistency < 0.6)
                {
                    recommendations.Add(new PatternRecommendation;
                    {
                        Type = PatternRecommendation.RecommendationType.ImproveConsistency,
                        Title = $"Improve Consistency of {habit.Name}",
                        Description = $"This habit shows inconsistent patterns. Try to practice at consistent times.",
                        TargetPatternId = habit.PatternId,
                        RelevanceScore = 1 - habit.Consistency,
                        ImpactScore = 0.7,
                        Priority = (1 - habit.Consistency) * 0.7,
                        ActionItems = new List<string>
                        {
                            "Set specific time for this activity",
                            "Use reminders",
                            "Track daily progress"
                        }
                    });
                }
            }

            // Anomali adresleme önerileri;
            var anomalies = patterns.Where(p => p.IsAnomaly()).ToList();
            foreach (var anomaly in anomalies)
            {
                recommendations.Add(new PatternRecommendation;
                {
                    Type = PatternRecommendation.RecommendationType.AddressAnomaly,
                    Title = $"Address Anomaly: {anomaly.Name}",
                    Description = $"Unusual pattern detected. Review this activity for potential issues.",
                    TargetPatternId = anomaly.PatternId,
                    RelevanceScore = anomaly.ConfidenceScore,
                    ImpactScore = 0.9,
                    Priority = anomaly.ConfidenceScore * 0.9,
                    ActionItems = new List<string>
                    {
                        "Review recent activity logs",
                        "Check for system issues",
                        "Verify if this is expected behavior"
                    }
                });
            }

            // Rutin oluşturma önerileri;
            var frequentPatterns = patterns;
                .Where(p => p.Frequency > 1.0 && !p.IsRoutine() && !p.IsHabit())
                .OrderByDescending(p => p.Frequency)
                .Take(3)
                .ToList();

            foreach (var pattern in frequentPatterns)
            {
                recommendations.Add(new PatternRecommendation;
                {
                    Type = PatternRecommendation.RecommendationType.EstablishRoutine,
                    Title = $"Establish Routine for {pattern.Name}",
                    Description = $"This activity occurs frequently. Consider making it a formal routine.",
                    TargetPatternId = pattern.PatternId,
                    RelevanceScore = pattern.Frequency / 10.0,
                    ImpactScore = 0.6,
                    Priority = (pattern.Frequency / 10.0) * 0.6,
                    ActionItems = new List<string>
                    {
                        "Define clear routine steps",
                        "Schedule specific time slots",
                        "Set achievement milestones"
                    }
                });
            }

            return recommendations;
                .OrderByDescending(r => r.Priority)
                .Take(5)
                .ToList();
        }

        private async Task SaveUserPatternsAsync(string userId, List<RecognizedPattern> patterns)
        {
            // Bellekte sakla;
            _userPatterns[userId] = patterns;
                .OrderByDescending(p => p.ConfidenceScore)
                .Take(_defaultConfig.MaxPatternsPerUser)
                .ToList();

            // Bilgi tabanına kaydet;
            foreach (var pattern in patterns)
            {
                await SavePatternToKnowledgeStoreAsync(pattern);
            }
        }

        private async Task SavePatternToKnowledgeStoreAsync(RecognizedPattern pattern)
        {
            try
            {
                var knowledgeFact = new;
                {
                    Type = "BehaviorPattern",
                    PatternId = pattern.PatternId,
                    UserId = pattern.UserId,
                    PatternType = pattern.Type.ToString(),
                    ConfidenceScore = pattern.ConfidenceScore,
                    DetectionCount = pattern.DetectionCount,
                    LastDetected = pattern.LastDetected,
                    Keywords = pattern.Keywords;
                };

                // Gerçek uygulamada: await _knowledgeStore.StoreFactAsync(knowledgeFact);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save pattern {PatternId} to knowledge store", pattern.PatternId);
            }
        }

        private async Task LoadPatternsFromKnowledgeStoreAsync(string userId)
        {
            try
            {
                // Gerçek uygulamada: var patterns = await _knowledgeStore.GetFactsAsync<RecognizedPattern>(...);
                // _userPatterns[userId] = patterns;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load patterns from knowledge store for user {UserId}", userId);
            }
        }

        private async Task<List<RecognizedPattern>> DetectTimeSeriesAnomaliesAsync(
            string userId,
            List<RecognizedPattern> patterns)
        {
            var anomalies = new List<RecognizedPattern>();

            foreach (var pattern in patterns.Where(p => p.OccurrenceTimes.Count >= 5))
            {
                var intervals = new List<double>();
                for (int i = 1; i < pattern.OccurrenceTimes.Count; i++)
                {
                    intervals.Add((pattern.OccurrenceTimes[i] - pattern.OccurrenceTimes[i - 1]).TotalHours);
                }

                var mean = intervals.Average();
                var stdDev = Math.Sqrt(intervals.Select(x => Math.Pow(x - mean, 2)).Average());

                // Son aralık anomali mi?
                var lastInterval = intervals.Last();
                if (Math.Abs(lastInterval - mean) > 2 * stdDev)
                {
                    anomalies.Add(pattern);
                }
            }

            return anomalies;
        }

        private async Task<RecognizedPattern> PredictPatternOccurrenceAsync(
            RecognizedPattern pattern,
            int horizon)
        {
            if (pattern.OccurrenceTimes.Count < 3)
            {
                return null;
            }

            var intervals = new List<double>();
            for (int i = 1; i < pattern.OccurrenceTimes.Count; i++)
            {
                intervals.Add((pattern.OccurrenceTimes[i] - pattern.OccurrenceTimes[i - 1]).TotalHours);
            }

            var avgInterval = intervals.Average();
            var predictedTime = pattern.LastDetected.AddHours(avgInterval * horizon);

            var predictedPattern = new RecognizedPattern;
            {
                PatternId = $"PRED_{pattern.PatternId}_{horizon}",
                Name = $"Prediction: {pattern.Name}",
                Description = $"Predicted occurrence of {pattern.Name}",
                Type = PatternType.Prediction,
                UserId = pattern.UserId,
                Context = pattern.Context,
                NextExpectedOccurrence = predictedTime,
                ExpectedConfidence = pattern.Consistency * 0.8, // Tahmin güveni;
                Features = new List<BehaviorFeature>(pattern.Features)
            };

            return predictedPattern;
        }

        private double CalculatePatternCorrelation(RecognizedPattern pattern1, RecognizedPattern pattern2)
        {
            // Zaman örtüşmesi;
            var timeOverlap = CalculateTimeOverlap(pattern1.OccurrenceTimes, pattern2.OccurrenceTimes);

            // Özellik benzerliği;
            var featureSimilarity = CalculateFeatureSimilarity(pattern1.Features, pattern2.Features);

            // Bağlam benzerliği;
            var contextSimilarity = pattern1.Context == pattern2.Context ? 1.0 : 0.0;

            // Ağırlıklı korelasyon;
            return (timeOverlap * 0.4) + (featureSimilarity * 0.4) + (contextSimilarity * 0.2);
        }

        private double CalculateTimeOverlap(List<DateTime> times1, List<DateTime> times2)
        {
            if (!times1.Any() || !times2.Any())
            {
                return 0.0;
            }

            var timeSlots1 = times1.Select(t => t.Hour).Distinct().ToList();
            var timeSlots2 = times2.Select(t => t.Hour).Distinct().ToList();

            var overlap = timeSlots1.Intersect(timeSlots2).Count();
            var total = timeSlots1.Union(timeSlots2).Count();

            return total > 0 ? (double)overlap / total : 0.0;
        }

        private double CalculateFeatureSimilarity(List<BehaviorFeature> features1, List<BehaviorFeature> features2)
        {
            if (!features1.Any() || !features2.Any())
            {
                return 0.0;
            }

            var commonFeatures = features1;
                .Where(f1 => features2.Any(f2 => IsSimilarFeature(f1, f2, 0.8)))
                .Count();

            var totalFeatures = features1.Union(features2).Count();

            return totalFeatures > 0 ? (double)commonFeatures / totalFeatures : 0.0;
        }

        private double CalculateFeatureImportance(List<RecognizedPattern> patterns, string featureName)
        {
            // Özelliğin kalıp ayırt ediciliğini hesapla;
            var patternsWithFeature = patterns;
                .Where(p => p.Features.Any(f => f.FeatureName == featureName))
                .ToList();

            var patternsWithoutFeature = patterns;
                .Where(p => !p.Features.Any(f => f.FeatureName == featureName))
                .ToList();

            if (!patternsWithFeature.Any() || !patternsWithoutFeature.Any())
            {
                return 0.5; // Nötr ağırlık;
            }

            // Ortalama güven farkı;
            var avgConfidenceWith = patternsWithFeature.Average(p => p.ConfidenceScore);
            var avgConfidenceWithout = patternsWithoutFeature.Average(p => p.ConfidenceScore);

            var confidenceDiff = Math.Abs(avgConfidenceWith - avgConfidenceWithout);

            // Özellik frekansı;
            var featureFrequency = (double)patternsWithFeature.Count / patterns.Count;

            // Kombine önem skoru;
            return (confidenceDiff * 0.6) + (featureFrequency * 0.4);
        }

        private List<RecognizedPattern> MergeSimilarPatterns(
            List<RecognizedPattern> patterns,
            PatternRecognitionConfig config)
        {
            var merged = new List<RecognizedPattern>();
            var processed = new HashSet<string>();

            for (int i = 0; i < patterns.Count; i++)
            {
                if (processed.Contains(patterns[i].PatternId))
                {
                    continue;
                }

                var current = patterns[i];
                var similarPatterns = new List<RecognizedPattern> { current };

                for (int j = i + 1; j < patterns.Count; j++)
                {
                    if (!processed.Contains(patterns[j].PatternId) &&
                        IsSimilarPattern(current, patterns[j], config.SimilarityThreshold))
                    {
                        similarPatterns.Add(patterns[j]);
                        processed.Add(patterns[j].PatternId);
                    }
                }

                if (similarPatterns.Count > 1)
                {
                    // Benzer kalıpları birleştir;
                    var mergedPattern = MergePatterns(similarPatterns);
                    merged.Add(mergedPattern);
                }
                else;
                {
                    merged.Add(current);
                }

                processed.Add(current.PatternId);
            }

            return merged;
        }

        private RecognizedPattern MergePatterns(List<RecognizedPattern> patterns)
        {
            var merged = new RecognizedPattern;
            {
                PatternId = Guid.NewGuid().ToString(),
                Name = $"Merged: {string.Join(", ", patterns.Select(p => p.Name).Take(3))}",
                Description = $"Merged pattern from {patterns.Count} similar patterns",
                Type = patterns.GroupBy(p => p.Type)
                    .OrderByDescending(g => g.Count())
                    .First().Key,
                UserId = patterns.First().UserId,
                Context = patterns.First().Context,
                FirstDetected = patterns.Min(p => p.FirstDetected),
                LastDetected = patterns.Max(p => p.LastDetected),
                DetectionCount = patterns.Sum(p => p.DetectionCount),
                Features = patterns.SelectMany(p => p.Features).DistinctBy(f => f.FeatureName).ToList(),
                OccurrenceTimes = patterns.SelectMany(p => p.OccurrenceTimes).OrderBy(t => t).ToList(),
                IntensityLevels = patterns.SelectMany(p => p.IntensityLevels).ToList()
            };

            // İstatistikleri güncelle;
            merged.UpdateDetection(merged.LastDetected,
                merged.IntensityLevels.Any() ? merged.IntensityLevels.Average() : 1.0);

            return merged;
        }

        private double CalculateFeatureDistance(BehaviorFeature f1, BehaviorFeature f2)
        {
            if (f1.FeatureName != f2.FeatureName || f1.Type != f2.Type)
            {
                return 1.0; // Farklı özellikler;
            }

            var valueDiff = Math.Abs(f1.NormalizedValue - f2.NormalizedValue);
            var timeDiff = Math.Abs((f1.Timestamp - f2.Timestamp).TotalHours) / 24.0; // Gün cinsinden;

            return (valueDiff * 0.7) + (timeDiff * 0.3);
        }

        private bool IsSimilarFeature(BehaviorFeature f1, BehaviorFeature f2, double threshold)
        {
            var distance = CalculateFeatureDistance(f1, f2);
            return distance <= (1 - threshold);
        }

        private bool IsSimilarPattern(RecognizedPattern p1, RecognizedPattern p2, double threshold)
        {
            // Özellik benzerliği;
            var featureSimilarity = CalculateFeatureSimilarity(p1.Features, p2.Features);

            // Zaman örtüşmesi;
            var timeOverlap = CalculateTimeOverlap(p1.OccurrenceTimes, p2.OccurrenceTimes);

            // Bağlam eşleşmesi;
            var contextMatch = p1.Context == p2.Context ? 1.0 : 0.0;

            var similarity = (featureSimilarity * 0.5) + (timeOverlap * 0.3) + (contextMatch * 0.2);
            return similarity >= threshold;
        }

        private bool IsSubsetPattern(RecognizedPattern subset, RecognizedPattern superset)
        {
            // Alt küme kontrolü;
            var subsetFeatures = subset.Features.Select(f => f.FeatureName).ToHashSet();
            var supersetFeatures = superset.Features.Select(f => f.FeatureName).ToHashSet();

            return subsetFeatures.IsSubsetOf(supersetFeatures) &&
                   subset.OccurrenceTimes.All(t =>
                       superset.OccurrenceTimes.Any(st =>
                           Math.Abs((t - st).TotalHours) < 2));
        }

        private List<BehaviorFeature> ConvertPatternsToFeatures(List<RecognizedPattern> patterns)
        {
            var features = new List<BehaviorFeature>();

            foreach (var pattern in patterns)
            {
                foreach (var feature in pattern.Features)
                {
                    features.Add(feature);
                }
            }

            return features;
        }

        private string GetTimeOfDayLabel(int hour)
        {
            if (hour < 6) return "Night";
            if (hour < 12) return "Morning";
            if (hour < 18) return "Afternoon";
            return "Evening";
        }

        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            }
        }

        private void ValidatePatternId(string patternId)
        {
            if (string.IsNullOrWhiteSpace(patternId))
            {
                throw new ArgumentException("Pattern ID cannot be null or empty", nameof(patternId));
            }
        }

        // ML.NET için veri modelleri;
        private class PatternFeatureData;
        {
            [LoadColumn(0)]
            public string PatternId { get; set; } = string.Empty;

            [LoadColumn(1)]
            public float Feature1 { get; set; }

            [LoadColumn(2)]
            public float Feature2 { get; set; }

            [LoadColumn(3)]
            public float Feature3 { get; set; }

            [LoadColumn(4)]
            public float Feature4 { get; set; }

            [LoadColumn(5)]
            public string Cluster { get; set; } = string.Empty;
        }

        private class PatternPrediction;
        {
            [ColumnName("PredictedLabel")]
            public string PredictedCluster { get; set; } = string.Empty;

            [ColumnName("Score")]
            public float[] Scores { get; set; } = Array.Empty<float>();
        }

        // Olay sınıfları;
        public class PatternsRecognizedEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public string RecognitionId { get; set; } = string.Empty;
            public int PatternCount { get; set; }
            public int NewPatternCount { get; set; }
            public string Context { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class PatternDeletedEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public string PatternId { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class ModelTrainedEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public string ModelPath { get; set; } = string.Empty;
            public int TrainingSamples { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class PatternsCleanedUpEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public int RemovedCount { get; set; }
            public DateTime CutoffTime { get; set; }
            public DateTime Timestamp { get; set; }
        }

        // Özel exception sınıfı;
        public class PatternRecognitionException : Exception
        {
            public PatternRecognitionException() { }
            public PatternRecognitionException(string message) : base(message) { }
            public PatternRecognitionException(string message, Exception innerException)
                : base(message, innerException) { }
        }

        // IDisposable implementasyonu;
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
                    // ML.NET context'ini temizle;
                    _mlContext?.Dispose();

                    // Bellek temizliği;
                    _userPatterns.Clear();
                    _userConfigs.Clear();
                    _userModels.Clear();

                    _logger.LogInformation("PatternRecognizer disposed");
                }

                _disposed = true;
            }
        }

        ~PatternRecognizer()
        {
            Dispose(false);
        }
    }
}
