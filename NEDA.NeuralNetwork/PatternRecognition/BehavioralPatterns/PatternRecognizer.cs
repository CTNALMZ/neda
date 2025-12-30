using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.NeuralNetwork.Common;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.Monitoring.MetricsCollector;

namespace NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
{
    /// <summary>
    /// Davranışsal desen tanıma motoru - Kullanıcı davranışları, alışkanlıklar ve aktivite desenlerini analiz eder;
    /// Gerçek zamanlı desen tespiti, öngörü ve anomali tespiti sağlar;
    /// </summary>
    public class PatternRecognizer : IPatternRecognizer, IDisposable;
    {
        private readonly ILogger<PatternRecognizer> _logger;
        private readonly PatternRecognitionConfig _config;
        private readonly IBehaviorModel _behaviorModel;
        private readonly ISequenceAnalyzer _sequenceAnalyzer;
        private readonly ITemporalPatternDetector _temporalDetector;
        private readonly IContextProcessor _contextProcessor;
        private readonly ConcurrentDictionary<string, BehavioralPattern> _activePatterns;
        private readonly ConcurrentDictionary<string, UserBehaviorProfile> _userProfiles;
        private readonly ConcurrentQueue<BehaviorEvent> _eventQueue;
        private readonly PatternRegistry _patternRegistry
        private readonly CancellationTokenSource _processingCancellation;
        private readonly SemaphoreSlim _processingLock;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly MetricsCollector _metricsCollector;
        private Task _processingTask;
        private bool _isInitialized;
        private bool _isDisposed;
        private DateTime _lastPatternUpdate;
        private readonly object _modelUpdateLock = new object();

        /// <summary>
        /// Desen tanıyıcıyı başlatır;
        /// </summary>
        public PatternRecognizer(
            ILogger<PatternRecognizer> logger,
            IOptions<PatternRecognitionConfig> config,
            IBehaviorModel behaviorModel = null,
            ISequenceAnalyzer sequenceAnalyzer = null,
            ITemporalPatternDetector temporalDetector = null,
            IContextProcessor contextProcessor = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? PatternRecognitionConfig.Default;

            _behaviorModel = behaviorModel ?? new NeuralBehaviorModel();
            _sequenceAnalyzer = sequenceAnalyzer ?? new MarkovSequenceAnalyzer();
            _temporalDetector = temporalDetector ?? new TemporalPatternDetector();
            _contextProcessor = contextProcessor ?? new ContextProcessor();

            _activePatterns = new ConcurrentDictionary<string, BehavioralPattern>();
            _userProfiles = new ConcurrentDictionary<string, UserBehaviorProfile>();
            _eventQueue = new ConcurrentQueue<BehaviorEvent>();
            _patternRegistry = new PatternRegistry();

            _processingCancellation = new CancellationTokenSource();
            _processingLock = new SemaphoreSlim(1, 1);
            _metricsCollector = new MetricsCollector("PatternRecognizer");

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() },
                WriteIndented = true;
            };

            _logger.LogInformation("PatternRecognizer initialized. Detection Sensitivity: {Sensitivity}",
                _config.DetectionSensitivity);
        }

        /// <summary>
        /// Asenkron başlatma işlemi;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                await ValidateConfigurationAsync();
                await LoadPatternDatabaseAsync();
                await InitializeModelsAsync();
                await StartProcessingLoopAsync();

                _isInitialized = true;
                _logger.LogInformation("PatternRecognizer initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PatternRecognizer");
                throw new PatternRecognizerInitializationException(
                    "PatternRecognizer initialization failed", ex);
            }
        }

        /// <summary>
        /// Yeni davranış olayı ekler;
        /// </summary>
        public async Task<EventProcessingResult> ProcessBehaviorEventAsync(
            BehaviorEvent behaviorEvent,
            ProcessingOptions options = null)
        {
            ValidateBehaviorEvent(behaviorEvent);
            options ??= ProcessingOptions.Default;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Olayı kuyruğa ekle;
                _eventQueue.Enqueue(behaviorEvent);
                _metricsCollector.IncrementCounter("events_received");

                // Gerçek zamanlı işleme;
                var processingResult = await ProcessEventImmediatelyAsync(behaviorEvent, options);

                // Desen güncellemesini tetikle;
                if (processingResult.ShouldUpdatePatterns)
                {
                    await UpdatePatternsForUserAsync(behaviorEvent.UserId);
                }

                // Olay işlendi olarak işaretle;
                behaviorEvent.ProcessedAt = DateTime.UtcNow;
                behaviorEvent.ProcessingStatus = EventStatus.Processed;

                stopwatch.Stop();
                _metricsCollector.RecordLatency("event_processing", stopwatch.Elapsed);

                return processingResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process behavior event for user {UserId}", behaviorEvent.UserId);
                behaviorEvent.ProcessingStatus = EventStatus.Failed;
                throw;
            }
        }

        /// <summary>
        /// Davranış desenlerini tespit eder;
        /// </summary>
        public async Task<PatternDetectionResult> DetectPatternsAsync(
            string userId,
            DetectionRange range = DetectionRange.All,
            PatternDetectionOptions options = null)
        {
            ValidateUserId(userId);
            options ??= PatternDetectionOptions.Default;

            var profile = await GetOrCreateUserProfileAsync(userId);
            var events = await GetEventsForRangeAsync(userId, range);

            if (events.Count < _config.MinimumEventsForDetection)
            {
                return new PatternDetectionResult;
                {
                    Status = DetectionStatus.InsufficientData,
                    Message = $"Insufficient data. Minimum {_config.MinimumEventsForDetection} events required"
                };
            }

            var detectionTasks = new List<Task<PatternDetectionResult>>();

            // Farklı desen türleri için paralel tespit;
            if (options.DetectTemporalPatterns)
            {
                detectionTasks.Add(DetectTemporalPatternsAsync(events, options));
            }

            if (options.DetectSequentialPatterns)
            {
                detectionTasks.Add(DetectSequentialPatternsAsync(events, options));
            }

            if (options.DetectContextualPatterns)
            {
                detectionTasks.Add(DetectContextualPatternsAsync(events, options));
            }

            if (options.DetectAnomalousPatterns)
            {
                detectionTasks.Add(DetectAnomalousPatternsAsync(events, options));
            }

            var results = await Task.WhenAll(detectionTasks);
            var combinedResult = CombineDetectionResults(results, profile, options);

            // Desenleri kaydet;
            if (combinedResult.DetectedPatterns.Any())
            {
                await StoreDetectedPatternsAsync(userId, combinedResult.DetectedPatterns);
            }

            _logger.LogInformation("Pattern detection completed for user {UserId}: {PatternCount} patterns found",
                userId, combinedResult.DetectedPatterns.Count);

            return combinedResult;
        }

        /// <summary>
        /// Davranış öngörüsü yapar;
        /// </summary>
        public async Task<BehaviorPrediction> PredictBehaviorAsync(
            string userId,
            PredictionContext context,
            PredictionOptions options = null)
        {
            ValidatePredictionContext(context);
            options ??= PredictionOptions.Default;

            var profile = await GetUserProfileAsync(userId);
            if (profile == null)
            {
                return new BehaviorPrediction;
                {
                    Confidence = 0,
                    Status = PredictionStatus.InsufficientData;
                };
            }

            var predictionContext = await BuildPredictionContextAsync(userId, context);
            var prediction = await _behaviorModel.PredictAsync(predictionContext, options);

            // Güven skorunu hesapla;
            prediction.Confidence = CalculatePredictionConfidence(prediction, profile, context);

            // Tahmin kalitesini değerlendir;
            prediction.QualityMetrics = await CalculatePredictionQualityAsync(userId, prediction);

            // Eğer güven yeterliyse, öneriler oluştur;
            if (prediction.Confidence >= _config.PredictionConfidenceThreshold)
            {
                prediction.Recommendations = await GenerateRecommendationsAsync(userId, prediction);
            }

            _logger.LogDebug("Behavior prediction for user {UserId}: {Confidence:F2} confidence",
                userId, prediction.Confidence);

            return prediction;
        }

        /// <summary>
        /// Desen tabanlı öneriler oluşturur;
        /// </summary>
        public async Task<List<Recommendation>> GeneratePatternBasedRecommendationsAsync(
            string userId,
            RecommendationContext context,
            RecommendationOptions options = null)
        {
            ValidateRecommendationContext(context);
            options ??= RecommendationOptions.Default;

            var profile = await GetUserProfileAsync(userId);
            if (profile == null || !profile.HasSufficientData)
            {
                return new List<Recommendation>();
            }

            var detectedPatterns = await GetRelevantPatternsAsync(userId, context);
            var recommendations = new List<Recommendation>();

            foreach (var pattern in detectedPatterns)
            {
                var patternRecommendations = await GenerateRecommendationsFromPatternAsync(
                    pattern, profile, context, options);

                recommendations.AddRange(patternRecommendations);
            }

            // Önerileri sırala ve filtrele;
            recommendations = recommendations;
                .OrderByDescending(r => r.RelevanceScore * r.Confidence)
                .Take(options.MaxRecommendations)
                .ToList();

            // Kişiselleştir;
            if (options.PersonalizeRecommendations)
            {
                recommendations = await PersonalizeRecommendationsAsync(userId, recommendations, profile);
            }

            return recommendations;
        }

        /// <summary>
        /// Davranış anomalisi tespit eder;
        /// </summary>
        public async Task<AnomalyDetectionResult> DetectBehavioralAnomalyAsync(
            BehaviorEvent behaviorEvent,
            AnomalyDetectionOptions options = null)
        {
            ValidateBehaviorEvent(behaviorEvent);
            options ??= AnomalyDetectionOptions.Default;

            var profile = await GetUserProfileAsync(behaviorEvent.UserId);
            var baseline = profile?.BehaviorBaseline;

            if (baseline == null)
            {
                return new AnomalyDetectionResult;
                {
                    IsAnomaly = false,
                    Confidence = 0,
                    Status = AnomalyStatus.InsufficientBaseline;
                };
            }

            var anomalyScore = await CalculateAnomalyScoreAsync(behaviorEvent, baseline, options);
            var isAnomaly = anomalyScore > options.AnomalyThreshold;

            var result = new AnomalyDetectionResult;
            {
                IsAnomaly = isAnomaly,
                AnomalyScore = anomalyScore,
                Confidence = CalculateAnomalyConfidence(anomalyScore, baseline),
                DetectedAt = DateTime.UtcNow,
                EventDetails = behaviorEvent,
                AnomalyType = isAnomaly ? DetermineAnomalyType(behaviorEvent, anomalyScore) : AnomalyType.Normal;
            };

            if (isAnomaly)
            {
                result.RecommendedActions = await GenerateAnomalyResponseAsync(result, profile);

                if (options.AlertOnAnomaly)
                {
                    await TriggerAnomalyAlertAsync(result);
                }

                _logger.LogWarning("Behavioral anomaly detected for user {UserId}: {AnomalyType} (Score: {Score})",
                    behaviorEvent.UserId, result.AnomalyType, result.AnomalyScore);
            }

            return result;
        }

        /// <summary>
        /// Desen öğrenme ve model güncellemesi yapar;
        /// </summary>
        public async Task<LearningResult> LearnFromPatternsAsync(
            IEnumerable<LearningSample> samples,
            LearningOptions options = null)
        {
            ValidateLearningSamples(samples);
            options ??= LearningOptions.Default;

            var sampleList = samples.ToList();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await _processingLock.WaitAsync();

                var learningContext = new LearningContext;
                {
                    Samples = sampleList,
                    Options = options,
                    CurrentPatterns = _activePatterns.Values.ToList()
                };

                var result = await PerformLearningAsync(learningContext);

                // Modelleri güncelle;
                if (result.Success)
                {
                    await UpdateModelsAsync(result);
                    await UpdatePatternRegistryAsync(result.DiscoveredPatterns);
                }

                stopwatch.Stop();
                result.LearningDuration = stopwatch.Elapsed;

                _logger.LogInformation("Learning completed: {NewPatterns} new patterns, {UpdatedPatterns} updated",
                    result.NewPatternsCount, result.UpdatedPatternsCount);

                return result;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Desenler için trend analizi yapar;
        /// </summary>
        public async Task<TrendAnalysisResult> AnalyzeTrendsAsync(
            string userId,
            TrendAnalysisOptions options = null)
        {
            ValidateUserId(userId);
            options ??= TrendAnalysisOptions.Default;

            var events = await GetHistoricalEventsAsync(userId, options.TimeRange);
            if (events.Count < options.MinimumDataPoints)
            {
                return new TrendAnalysisResult;
                {
                    Status = AnalysisStatus.InsufficientData;
                };
            }

            var analysisTasks = new[]
            {
                AnalyzeFrequencyTrendsAsync(events, options),
                AnalyzeIntensityTrendsAsync(events, options),
                AnalyzeTemporalTrendsAsync(events, options),
                AnalyzeCorrelationTrendsAsync(events, options)
            };

            var results = await Task.WhenAll(analysisTasks);
            var combinedResult = CombineTrendResults(results, events, options);

            // Trend tahmini;
            if (options.PredictFutureTrends)
            {
                combinedResult.FuturePredictions = await PredictFutureTrendsAsync(combinedResult, options);
            }

            // Önemli değişiklikleri işaretle;
            combinedResult.SignificantChanges = await DetectSignificantChangesAsync(combinedResult, options);

            return combinedResult;
        }

        /// <summary>
        /// Kullanıcı davranış profili oluşturur;
        /// </summary>
        public async Task<UserBehaviorProfile> CreateUserProfileAsync(
            string userId,
            ProfileCreationOptions options = null)
        {
            ValidateUserId(userId);
            options ??= ProfileCreationOptions.Default;

            var historicalEvents = await GetHistoricalEventsAsync(userId, options.HistoricalRange);
            var profile = await BuildUserProfileAsync(userId, historicalEvents, options);

            // Profili kaydet;
            _userProfiles[userId] = profile;
            await SaveUserProfileAsync(profile);

            // İlk desen analizini yap;
            if (historicalEvents.Count >= _config.MinimumEventsForProfile)
            {
                await PerformInitialPatternAnalysisAsync(userId, profile, historicalEvents);
            }

            _logger.LogInformation("User profile created for {UserId} with {EventCount} historical events",
                userId, historicalEvents.Count);

            return profile;
        }

        /// <summary>
        /// Desen doğrulama ve iyileştirme yapar;
        /// </summary>
        public async Task<ValidationResult> ValidatePatternsAsync(
            IEnumerable<BehavioralPattern> patterns,
            ValidationOptions options = null)
        {
            ValidatePatterns(patterns);
            options ??= ValidationOptions.Default;

            var patternList = patterns.ToList();
            var validationTasks = patternList.Select(pattern =>
                ValidateSinglePatternAsync(pattern, options));

            var results = await Task.WhenAll(validationTasks);
            var aggregatedResult = AggregateValidationResults(results, patternList);

            // Geçersiz desenleri kaldır;
            if (aggregatedResult.InvalidPatterns.Any())
            {
                await RemoveInvalidPatternsAsync(aggregatedResult.InvalidPatterns);
            }

            // İyileştirme önerileri;
            if (aggregatedResult.NeedsImprovement.Any())
            {
                await SuggestImprovementsAsync(aggregatedResult.NeedsImprovement);
            }

            return aggregatedResult;
        }

        /// <summary>
        /// Gerçek zamanlı desen izleme başlatır;
        /// </summary>
        public async Task StartRealTimeMonitoringAsync(
            string userId,
            RealTimeMonitoringOptions options)
        {
            ValidateUserId(userId);
            ValidateMonitoringOptions(options);

            if (_userProfiles.TryGetValue(userId, out var profile))
            {
                if (profile.RealTimeMonitor?.IsActive == true)
                {
                    throw new InvalidOperationException($"Real-time monitoring is already active for user {userId}");
                }

                var monitor = new RealTimeBehaviorMonitor(userId, options, this, _logger);
                profile.RealTimeMonitor = monitor;

                await monitor.StartAsync();

                _logger.LogInformation("Real-time monitoring started for user {UserId}", userId);
            }
            else;
            {
                throw new UserProfileNotFoundException($"User profile not found for {userId}");
            }
        }

        /// <summary>
        /// Gerçek zamanlı izlemeyi durdurur;
        /// </summary>
        public async Task StopRealTimeMonitoringAsync(string userId)
        {
            if (_userProfiles.TryGetValue(userId, out var profile))
            {
                if (profile.RealTimeMonitor != null)
                {
                    await profile.RealTimeMonitor.StopAsync();
                    profile.RealTimeMonitor = null;

                    _logger.LogInformation("Real-time monitoring stopped for user {UserId}", userId);
                }
            }
        }

        #region Private Implementation;

        private async Task StartProcessingLoopAsync()
        {
            _processingTask = Task.Run(async () =>
            {
                while (!_processingCancellation.Token.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessQueuedEventsAsync();
                        await UpdatePatternModelsAsync();
                        await CleanupOldDataAsync();

                        await Task.Delay(_config.ProcessingInterval, _processingCancellation.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in processing loop");
                        await Task.Delay(TimeSpan.FromSeconds(5), _processingCancellation.Token);
                    }
                }
            }, _processingCancellation.Token);
        }

        private async Task ProcessQueuedEventsAsync()
        {
            var batchSize = _config.BatchProcessingSize;
            var eventsToProcess = new List<BehaviorEvent>();

            // Kuyruktan olayları topla;
            for (int i = 0; i < batchSize && _eventQueue.TryDequeue(out var behaviorEvent); i++)
            {
                eventsToProcess.Add(behaviorEvent);
            }

            if (!eventsToProcess.Any())
                return;

            // Gruplara ayır;
            var groupedEvents = eventsToProcess.GroupBy(e => e.UserId);

            var processingTasks = groupedEvents.Select(group =>
                ProcessEventBatchAsync(group.Key, group.ToList()));

            await Task.WhenAll(processingTasks);

            _metricsCollector.IncrementCounter("batches_processed", eventsToProcess.Count);
        }

        private async Task ProcessEventBatchAsync(string userId, List<BehaviorEvent> events)
        {
            try
            {
                var profile = await GetOrCreateUserProfileAsync(userId);

                // Olayları işle;
                foreach (var behaviorEvent in events)
                {
                    await ProcessSingleEventAsync(behaviorEvent, profile);
                }

                // Toplu desen güncellemesi;
                if (events.Count >= _config.MinimumBatchSizeForUpdate)
                {
                    await UpdatePatternsForUserAsync(userId);
                }

                _logger.LogDebug("Processed {EventCount} events for user {UserId}", events.Count, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process event batch for user {UserId}", userId);
                // Başarısız olayları geri kuyruğa al;
                foreach (var behaviorEvent in events)
                {
                    _eventQueue.Enqueue(behaviorEvent);
                }
            }
        }

        private async Task ProcessSingleEventAsync(BehaviorEvent behaviorEvent, UserBehaviorProfile profile)
        {
            // İçeriği işle;
            var processedEvent = await _contextProcessor.ProcessAsync(behaviorEvent);

            // Profili güncelle;
            profile.UpdateWithEvent(processedEvent);

            // Anomali kontrolü;
            if (_config.RealTimeAnomalyDetection)
            {
                var anomalyResult = await DetectBehavioralAnomalyAsync(processedEvent);
                if (anomalyResult.IsAnomaly)
                {
                    await HandleDetectedAnomalyAsync(anomalyResult, profile);
                }
            }

            // Desen eşleştirme;
            var matchedPatterns = await FindMatchingPatternsAsync(processedEvent, profile);
            if (matchedPatterns.Any())
            {
                await UpdatePatternConfidenceAsync(matchedPatterns, processedEvent);
            }

            // Olayı arşivle;
            await ArchiveEventAsync(processedEvent);
        }

        private async Task UpdatePatternModelsAsync()
        {
            if (DateTime.UtcNow - _lastPatternUpdate < _config.PatternUpdateInterval)
                return;

            try
            {
                lock (_modelUpdateLock)
                {
                    var updateTasks = _activePatterns.Values.Select(pattern =>
                        UpdatePatternModelAsync(pattern));

                    Task.WhenAll(updateTasks).Wait();

                    _lastPatternUpdate = DateTime.UtcNow;
                    _logger.LogDebug("Pattern models updated");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update pattern models");
            }
        }

        private async Task<PatternDetectionResult> DetectTemporalPatternsAsync(
            List<BehaviorEvent> events,
            PatternDetectionOptions options)
        {
            var temporalEvents = events;
                .Where(e => e.Timestamp.HasValue)
                .OrderBy(e => e.Timestamp.Value)
                .ToList();

            return await _temporalDetector.DetectPatternsAsync(temporalEvents, options);
        }

        private async Task<PatternDetectionResult> DetectSequentialPatternsAsync(
            List<BehaviorEvent> events,
            PatternDetectionOptions options)
        {
            var sequences = events;
                .GroupBy(e => e.SessionId)
                .Where(g => g.Count() >= 2)
                .Select(g => g.OrderBy(e => e.Timestamp).ToList())
                .ToList();

            return await _sequenceAnalyzer.AnalyzeAsync(sequences, options);
        }

        private async Task<UserBehaviorProfile> GetOrCreateUserProfileAsync(string userId)
        {
            if (_userProfiles.TryGetValue(userId, out var existingProfile))
                return existingProfile;

            // Yeni profil oluştur;
            var profile = await CreateUserProfileAsync(userId, new ProfileCreationOptions;
            {
                HistoricalRange = TimeSpan.FromDays(30),
                IncludeAggregatedData = true;
            });

            return profile;
        }

        private double CalculatePredictionConfidence(
            BehaviorPrediction prediction,
            UserBehaviorProfile profile,
            PredictionContext context)
        {
            var baseConfidence = prediction.Confidence;

            // Profil kalitesi faktörü;
            var profileQuality = profile.DataQualityScore;
            baseConfidence *= profileQuality;

            // Bağlam uyum faktörü;
            var contextMatch = CalculateContextMatch(profile, context);
            baseConfidence *= contextMatch;

            // Zaman faktörü (yeni veriler daha güvenilir)
            var recencyFactor = CalculateRecencyFactor(profile.LastActivity);
            baseConfidence *= recencyFactor;

            return Math.Clamp(baseConfidence, 0, 1);
        }

        private async Task<List<BehavioralPattern>> GetRelevantPatternsAsync(
            string userId,
            RecommendationContext context)
        {
            var allPatterns = await GetPatternsForUserAsync(userId);

            return allPatterns;
                .Where(p => IsPatternRelevant(p, context))
                .OrderByDescending(p => p.Confidence * p.Frequency)
                .Take(_config.MaxPatternsForRecommendation)
                .ToList();
        }

        private bool IsPatternRelevant(BehavioralPattern pattern, RecommendationContext context)
        {
            // Zaman uyumu;
            if (!IsTimeRelevant(pattern, context.Time))
                return false;

            // Bağlam uyumu;
            if (!IsContextRelevant(pattern, context))
                return false;

            // Güven eşiği;
            if (pattern.Confidence < _config.MinimumPatternConfidence)
                return false;

            return true;
        }

        private async Task<double> CalculateAnomalyScoreAsync(
            BehaviorEvent behaviorEvent,
            BehaviorBaseline baseline,
            AnomalyDetectionOptions options)
        {
            var deviationScores = new List<double>();

            // Davranış tipi sapması;
            var typeDeviation = CalculateTypeDeviation(behaviorEvent, baseline);
            deviationScores.Add(typeDeviation);

            // Sıklık sapması;
            if (behaviorEvent.Timestamp.HasValue)
            {
                var frequencyDeviation = CalculateFrequencyDeviation(behaviorEvent, baseline);
                deviationScores.Add(frequencyDeviation);
            }

            // Yoğunluk sapması;
            var intensityDeviation = CalculateIntensityDeviation(behaviorEvent, baseline);
            deviationScores.Add(intensityDeviation);

            // Bağlam sapması;
            var contextDeviation = CalculateContextDeviation(behaviorEvent, baseline);
            deviationScores.Add(contextDeviation);

            // Ağırlıklı ortalama;
            var weightedScore = deviationScores;
                .Select((score, index) => score * options.WeightFactors[index])
                .Sum();

            return weightedScore;
        }

        #endregion;

        #region Validation Methods;

        private async Task ValidateConfigurationAsync()
        {
            if (_config.MinimumEventsForDetection < 3)
                throw new InvalidConfigurationException("Minimum events for detection must be at least 3");

            if (_config.PredictionConfidenceThreshold < 0.1 || _config.PredictionConfidenceThreshold > 0.9)
                throw new InvalidConfigurationException("Prediction confidence threshold must be between 0.1 and 0.9");

            if (_config.ProcessingInterval < TimeSpan.FromMilliseconds(100))
                throw new InvalidConfigurationException("Processing interval must be at least 100ms");

            await Task.CompletedTask;
        }

        private void ValidateBehaviorEvent(BehaviorEvent behaviorEvent)
        {
            if (behaviorEvent == null)
                throw new ArgumentNullException(nameof(behaviorEvent));

            if (string.IsNullOrWhiteSpace(behaviorEvent.UserId))
                throw new ArgumentException("User ID cannot be empty", nameof(behaviorEvent.UserId));

            if (string.IsNullOrWhiteSpace(behaviorEvent.EventType))
                throw new ArgumentException("Event type cannot be empty", nameof(behaviorEvent.EventType));
        }

        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));
        }

        private void ValidatePredictionContext(PredictionContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (context.TimeHorizon < TimeSpan.Zero)
                throw new ArgumentException("Time horizon cannot be negative", nameof(context.TimeHorizon));
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _processingCancellation.Cancel();

                    try
                    {
                        _processingTask?.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (AggregateException)
                    {
                        // Task iptal istisnalarını yoksay;
                    }

                    _processingCancellation.Dispose();
                    _processingLock.Dispose();

                    // Aktif izlemeleri durdur;
                    var stopTasks = _userProfiles.Values;
                        .Where(p => p.RealTimeMonitor?.IsActive == true)
                        .Select(p => p.RealTimeMonitor.StopAsync());

                    Task.WhenAll(stopTasks).Wait(TimeSpan.FromSeconds(10));
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Types;

    public class PatternRecognitionConfig;
    {
        public double DetectionSensitivity { get; set; } = 0.7;
        public int MinimumEventsForDetection { get; set; } = 10;
        public int MinimumEventsForProfile { get; set; } = 50;
        public int BatchProcessingSize { get; set; } = 100;
        public int MinimumBatchSizeForUpdate { get; set; } = 20;
        public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan PatternUpdateInterval { get; set; } = TimeSpan.FromMinutes(5);
        public double PredictionConfidenceThreshold { get; set; } = 0.6;
        public double MinimumPatternConfidence { get; set; } = 0.4;
        public int MaxPatternsForRecommendation { get; set; } = 10;
        public bool RealTimeAnomalyDetection { get; set; } = true;
        public TimeSpan DataRetentionPeriod { get; set; } = TimeSpan.FromDays(90);
        public int MaxActivePatternsPerUser { get; set; } = 100;
        public int MaxUserProfiles { get; set; } = 10000;

        public static PatternRecognitionConfig Default => new PatternRecognitionConfig();
    }

    public class PatternDetectionResult;
    {
        public List<BehavioralPattern> DetectedPatterns { get; set; } = new List<BehavioralPattern>();
        public DetectionStatus Status { get; set; }
        public double OverallConfidence { get; set; }
        public Dictionary<string, double> PatternConfidences { get; set; } = new Dictionary<string, double>();
        public DateTime DetectionTime { get; set; }
        public string Message { get; set; }
        public List<PatternRelationship> Relationships { get; set; } = new List<PatternRelationship>();
    }

    public class BehavioralPattern;
    {
        public string PatternId { get; set; }
        public string PatternType { get; set; }
        public string Description { get; set; }
        public double Confidence { get; set; }
        public double Frequency { get; set; }
        public TimeSpan AverageInterval { get; set; }
        public DateTime FirstDetected { get; set; }
        public DateTime LastDetected { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public List<PatternCondition> Conditions { get; set; } = new List<PatternCondition>();
        public List<PatternOutcome> ExpectedOutcomes { get; set; } = new List<PatternOutcome>();
        public PatternTrend Trend { get; set; }
    }

    public class BehaviorPrediction;
    {
        public string PredictedEvent { get; set; }
        public DateTime PredictedTime { get; set; }
        public double Confidence { get; set; }
        public PredictionStatus Status { get; set; }
        public Dictionary<string, double> AlternativePredictions { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, object> ContextFactors { get; set; } = new Dictionary<string, object>();
        public List<Recommendation> Recommendations { get; set; } = new List<Recommendation>();
        public QualityMetrics QualityMetrics { get; set; }
        public TimeSpan TimeHorizon { get; set; }
    }

    public class AnomalyDetectionResult;
    {
        public bool IsAnomaly { get; set; }
        public double AnomalyScore { get; set; }
        public double Confidence { get; set; }
        public AnomalyType AnomalyType { get; set; }
        public DateTime DetectedAt { get; set; }
        public BehaviorEvent EventDetails { get; set; }
        public List<string> RecommendedActions { get; set; } = new List<string>();
        public AnomalyStatus Status { get; set; }
        public Dictionary<string, double> ContributingFactors { get; set; } = new Dictionary<string, double>();
    }

    public class LearningResult;
    {
        public bool Success { get; set; }
        public int NewPatternsCount { get; set; }
        public int UpdatedPatternsCount { get; set; }
        public List<BehavioralPattern> DiscoveredPatterns { get; set; } = new List<BehavioralPattern>();
        public double LearningEfficiency { get; set; }
        public TimeSpan LearningDuration { get; set; }
        public Dictionary<string, double> ModelImprovements { get; set; } = new Dictionary<string, double>();
        public List<LearningInsight> Insights { get; set; } = new List<LearningInsight>();
    }

    public class TrendAnalysisResult;
    {
        public List<BehaviorTrend> DetectedTrends { get; set; } = new List<BehaviorTrend>();
        public TrendDirection OverallDirection { get; set; }
        public double TrendStrength { get; set; }
        public AnalysisStatus Status { get; set; }
        public List<FuturePrediction> FuturePredictions { get; set; } = new List<FuturePrediction>();
        public List<SignificantChange> SignificantChanges { get; set; } = new List<SignificantChange>();
        public Dictionary<string, double> CorrelationMetrics { get; set; } = new Dictionary<string, double>();
    }

    public class UserBehaviorProfile;
    {
        public string UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime LastActivity { get; set; }
        public BehaviorBaseline BehaviorBaseline { get; set; }
        public List<BehavioralPattern> KnownPatterns { get; set; } = new List<BehavioralPattern>();
        public Dictionary<string, BehaviorStatistics> EventStatistics { get; set; } = new Dictionary<string, BehaviorStatistics>();
        public double DataQualityScore { get; set; }
        public bool HasSufficientData => EventStatistics.Count >= 5 && DataQualityScore > 0.6;
        public RealTimeBehaviorMonitor RealTimeMonitor { get; set; }
        public Dictionary<string, object> Preferences { get; set; } = new Dictionary<string, object>();
    }

    public enum DetectionStatus;
    {
        Success,
        InsufficientData,
        Processing,
        Failed;
    }

    public enum PredictionStatus;
    {
        HighConfidence,
        MediumConfidence,
        LowConfidence,
        InsufficientData,
        Uncertain;
    }

    public enum AnomalyType;
    {
        Normal,
        FrequencyAnomaly,
        IntensityAnomaly,
        TimingAnomaly,
        SequenceAnomaly,
        ContextAnomaly,
        MultiDimensionalAnomaly;
    }

    public enum TrendDirection;
    {
        Increasing,
        Decreasing,
        Stable,
        Cyclical,
        Volatile;
    }

    #endregion;
}
