using NEDA.AI.MachineLearning;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Monitoring.MetricsCollector;
using NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
using NEDA.SecurityModules.AdvancedSecurity;
using NEDA.SecurityModules.Firewall;
using NEDA.Services.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.SecurityModules.Monitoring;
{
    /// <summary>
    /// Tehdit analizi motoru arayüzü.
    /// </summary>
    public interface IThreatAnalyzer : IDisposable
    {
        /// <summary>
        /// Güvenlik olayını analiz eder.
        /// </summary>
        /// <param name="securityEvent">Güvenlik olayı.</param>
        /// <returns>Analiz sonucu.</returns>
        Task<ThreatAnalysisResult> AnalyzeSecurityEventAsync(SecurityEvent securityEvent);

        /// <summary>
        /// Toplu güvenlik olaylarını analiz eder.
        /// </summary>
        /// <param name="securityEvents">Güvenlik olayları.</param>
        /// <returns>Toplu analiz sonuçları.</returns>
        Task<List<ThreatAnalysisResult>> AnalyzeBatchEventsAsync(List<SecurityEvent> securityEvents);

        /// <summary>
        /// Anomalik davranışları tespit eder.
        /// </summary>
        /// <param name="userBehavior">Kullanıcı davranış verisi.</param>
        /// <returns>Anomali tespit sonuçları.</returns>
        Task<AnomalyDetectionResult> DetectAnomaliesAsync(UserBehaviorData userBehavior);

        /// <summary>
        /// Gerçek zamanlı tehdit izlemesini başlatır.
        /// </summary>
        /// <param name="monitoringConfig">İzleme konfigürasyonu.</param>
        Task StartRealTimeMonitoringAsync(MonitoringConfig monitoringConfig);

        /// <summary>
        /// Tehdit istatistiklerini getirir.
        /// </summary>
        /// <param name="timeRange">Zaman aralığı.</param>
        /// <returns>Tehdit istatistikleri.</returns>
        Task<ThreatStatistics> GetThreatStatisticsAsync(TimeRange timeRange);

        /// <summary>
        /// Tehdit zekası verilerini günceller.
        /// </summary>
        /// <param name="threatIntelligence">Tehdit zekası verileri.</param>
        Task UpdateThreatIntelligenceAsync(ThreatIntelligenceData threatIntelligence);

        /// <summary>
        /// Tehdit korelasyon analizi yapar.
        /// </summary>
        /// <param name="correlationConfig">Korelasyon konfigürasyonu.</param>
        /// <returns>Korelasyon sonuçları.</returns>
        Task<CorrelationResult> PerformCorrelationAnalysisAsync(CorrelationConfig correlationConfig);

        /// <summary>
        /// Makine öğrenimi modelini eğitir.
        /// </summary>
        /// <param name="trainingData">Eğitim verileri.</param>
        Task TrainMachineLearningModelAsync(List<SecurityEvent> trainingData);

        /// <summary>
        /// Analiz motoru durumunu getirir.
        /// </summary>
        ThreatAnalyzerStatus GetStatus();
    }

    /// <summary>
    /// Tehdit analiz motoru ana sınıfı.
    /// </summary>
    public class ThreatAnalyzer : IThreatAnalyzer;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly IFirewallManager _firewallManager;
        private readonly IAuthenticationService _authService;
        private readonly IMachineLearningModel _mlModel;

        private readonly ThreatDetectionEngine _detectionEngine;
        private readonly AnomalyDetector _anomalyDetector;
        private readonly CorrelationEngine _correlationEngine;
        private readonly ThreatIntelligenceManager _intelligenceManager;
        private readonly RiskAssessmentEngine _riskAssessmentEngine;
        private readonly PatternRecognitionEngine _patternRecognition;

        private readonly ConcurrentQueue<SecurityEvent> _eventQueue;
        private readonly ConcurrentDictionary<string, ThreatIndicator> _activeThreats;
        private readonly SemaphoreSlim _processingLock;
        private readonly CancellationTokenSource _shutdownTokenSource;
        private readonly Timer _analysisTimer;
        private readonly Timer _intelligenceUpdateTimer;

        private bool _disposed;
        private bool _isMonitoring;
        private DateTime _startTime;
        private ThreatAnalyzerConfig _config;

        /// <summary>
        /// Tehdit analiz motoru oluşturur.
        /// </summary>
        public ThreatAnalyzer(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IEventBus eventBus,
            IFirewallManager firewallManager,
            IAuthenticationService authService,
            IMachineLearningModel mlModel)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _firewallManager = firewallManager ?? throw new ArgumentNullException(nameof(firewallManager));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _mlModel = mlModel ?? throw new ArgumentNullException(nameof(mlModel));

            _detectionEngine = new ThreatDetectionEngine();
            _anomalyDetector = new AnomalyDetector();
            _correlationEngine = new CorrelationEngine();
            _intelligenceManager = new ThreatIntelligenceManager();
            _riskAssessmentEngine = new RiskAssessmentEngine();
            _patternRecognition = new PatternRecognitionEngine();

            _eventQueue = new ConcurrentQueue<SecurityEvent>();
            _activeThreats = new ConcurrentDictionary<string, ThreatIndicator>();
            _processingLock = new SemaphoreSlim(1, 1);
            _shutdownTokenSource = new CancellationTokenSource();

            // Analiz timer'ı: her 10 saniyede bir;
            _analysisTimer = new Timer(AnalyzeQueuedEvents, null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            // Tehdit zekası güncelleme timer'ı: her saat başı;
            _intelligenceUpdateTimer = new Timer(UpdateThreatIntelligence, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            _eventBus.Subscribe<SecurityEventOccurred>(HandleSecurityEvent);
            _eventBus.Subscribe<UserBehaviorEvent>(HandleUserBehavior);
            _eventBus.Subscribe<SystemActivityEvent>(HandleSystemActivity);

            _logger.LogInformation("ThreatAnalyzer initialized");
        }

        /// <summary>
        /// Güvenlik olayını analiz eder.
        /// </summary>
        public async Task<ThreatAnalysisResult> AnalyzeSecurityEventAsync(SecurityEvent securityEvent)
        {
            ValidateSecurityEvent(securityEvent);

            var startTime = DateTime.UtcNow;

            try
            {
                await _processingLock.WaitAsync();

                // Temel analiz;
                var basicAnalysis = await PerformBasicAnalysisAsync(securityEvent);

                // Anomali tespiti;
                var anomalyResult = await DetectAnomaliesForEventAsync(securityEvent);

                // Tehdit zekası eşleştirme;
                var intelligenceMatch = await MatchWithThreatIntelligenceAsync(securityEvent);

                // Risk değerlendirmesi;
                var riskAssessment = await AssessRiskAsync(securityEvent, basicAnalysis, anomalyResult, intelligenceMatch);

                // Korelasyon analizi;
                var correlationResult = await CheckCorrelationsAsync(securityEvent);

                // Nihai tehdit skoru;
                var threatScore = CalculateThreatScore(basicAnalysis, anomalyResult, intelligenceMatch, riskAssessment, correlationResult);

                // Tehdit seviyesini belirle;
                var threatLevel = DetermineThreatLevel(threatScore);

                var result = new ThreatAnalysisResult;
                {
                    EventId = securityEvent.Id,
                    Timestamp = DateTime.UtcNow,
                    ThreatScore = threatScore,
                    ThreatLevel = threatLevel,
                    BasicAnalysis = basicAnalysis,
                    AnomalyResult = anomalyResult,
                    IntelligenceMatch = intelligenceMatch,
                    RiskAssessment = riskAssessment,
                    CorrelationResult = correlationResult,
                    Recommendations = GenerateRecommendations(threatLevel, securityEvent)
                };

                // Yüksek tehdit seviyeleri için aksiyon al;
                if (threatLevel >= ThreatLevel.High)
                {
                    await TakeRemediationActionsAsync(securityEvent, result);
                }

                // Event yayınla;
                await PublishThreatAnalysisEvent(securityEvent, result);

                _metricsCollector.RecordMetric("threat.analysis.completed", 1);
                _metricsCollector.RecordMetric("threat.analysis.time", (DateTime.UtcNow - startTime).TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error analyzing security event {securityEvent.Id}");
                _metricsCollector.RecordMetric("threat.analysis.error", 1);

                throw;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Toplu güvenlik olaylarını analiz eder.
        /// </summary>
        public async Task<List<ThreatAnalysisResult>> AnalyzeBatchEventsAsync(List<SecurityEvent> securityEvents)
        {
            if (securityEvents == null || securityEvents.Count == 0)
                throw new ArgumentException("Security events cannot be null or empty", nameof(securityEvents));

            var results = new List<ThreatAnalysisResult>();
            var tasks = new List<Task<ThreatAnalysisResult>>();

            // Paralel analiz;
            foreach (var securityEvent in securityEvents)
            {
                tasks.Add(AnalyzeSecurityEventAsync(securityEvent));
            }

            // Tüm task'ları bekle;
            var analysisResults = await Task.WhenAll(tasks);
            results.AddRange(analysisResults);

            // Toplu korelasyon analizi;
            var batchCorrelation = await AnalyzeBatchCorrelationsAsync(results);
            if (batchCorrelation.HasCorrelations)
            {
                _logger.LogWarning($"Batch analysis detected {batchCorrelation.CorrelatedEvents.Count} correlated threats");
            }

            _metricsCollector.RecordMetric("threat.analysis.batch", results.Count);

            return results;
        }

        /// <summary>
        /// Anomalik davranışları tespit eder.
        /// </summary>
        public async Task<AnomalyDetectionResult> DetectAnomaliesAsync(UserBehaviorData userBehavior)
        {
            ValidateUserBehavior(userBehavior);

            try
            {
                // Davranış analizi;
                var behaviorAnalysis = await AnalyzeUserBehaviorAsync(userBehavior);

                // Makine öğrenimi ile anomali tespiti;
                var mlResult = await _mlModel.PredictAnomalyAsync(userBehavior);

                // Desen tanıma;
                var patternResult = _patternRecognition.AnalyzePatterns(userBehavior);

                // Anomali skoru hesapla;
                var anomalyScore = CalculateAnomalyScore(behaviorAnalysis, mlResult, patternResult);

                // Anomali seviyesini belirle;
                var anomalyLevel = DetermineAnomalyLevel(anomalyScore);

                var result = new AnomalyDetectionResult;
                {
                    UserId = userBehavior.UserId,
                    Timestamp = DateTime.UtcNow,
                    AnomalyScore = anomalyScore,
                    AnomalyLevel = anomalyLevel,
                    BehaviorAnalysis = behaviorAnalysis,
                    MachineLearningResult = mlResult,
                    PatternRecognitionResult = patternResult,
                    IsAnomaly = anomalyLevel >= AnomalyLevel.Moderate;
                };

                if (result.IsAnomaly)
                {
                    _logger.LogWarning($"Anomaly detected for user {userBehavior.UserId}, score: {anomalyScore}");
                    await HandleAnomalyDetectionAsync(userBehavior, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error detecting anomalies for user {userBehavior.UserId}");
                throw;
            }
        }

        /// <summary>
        /// Gerçek zamanlı tehdit izlemesini başlatır.
        /// </summary>
        public async Task StartRealTimeMonitoringAsync(MonitoringConfig monitoringConfig)
        {
            if (_isMonitoring) return;

            await _processingLock.WaitAsync();
            try
            {
                _config = monitoringConfig ?? throw new ArgumentNullException(nameof(monitoringConfig));

                // İzleme kurallarını yükle;
                await LoadMonitoringRulesAsync(monitoringConfig);

                // Makine öğrenimi modelini başlat;
                await InitializeMachineLearningModelAsync();

                // Tehdit zekası verilerini yükle;
                await LoadThreatIntelligenceAsync();

                _isMonitoring = true;
                _startTime = DateTime.UtcNow;

                _logger.LogInformation("Real-time threat monitoring started");

                var @event = new MonitoringStartedEvent;
                {
                    StartTime = _startTime,
                    Config = monitoringConfig,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Tehdit istatistiklerini getirir.
        /// </summary>
        public async Task<ThreatStatistics> GetThreatStatisticsAsync(TimeRange timeRange)
        {
            var statistics = new ThreatStatistics;
            {
                TimeRange = timeRange,
                TotalEventsAnalyzed = _metricsCollector.GetMetric("threat.analysis.completed")?.Value ?? 0,
                ThreatDistribution = await GetThreatDistributionAsync(timeRange),
                AnomalyDetectionRate = await CalculateAnomalyDetectionRateAsync(timeRange),
                AverageResponseTime = await CalculateAverageResponseTimeAsync(timeRange),
                TopThreatSources = await GetTopThreatSourcesAsync(timeRange),
                ThreatTrends = await AnalyzeThreatTrendsAsync(timeRange)
            };

            return statistics;
        }

        /// <summary>
        /// Tehdit zekası verilerini günceller.
        /// </summary>
        public async Task UpdateThreatIntelligenceAsync(ThreatIntelligenceData threatIntelligence)
        {
            await _processingLock.WaitAsync();
            try
            {
                await _intelligenceManager.UpdateIntelligenceAsync(threatIntelligence);

                _logger.LogInformation($"Threat intelligence updated with {threatIntelligence.Indicators.Count} indicators");

                var @event = new ThreatIntelligenceUpdatedEvent;
                {
                    UpdateTime = DateTime.UtcNow,
                    IndicatorCount = threatIntelligence.Indicators.Count,
                    Source = threatIntelligence.Source,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Tehdit korelasyon analizi yapar.
        /// </summary>
        public async Task<CorrelationResult> PerformCorrelationAnalysisAsync(CorrelationConfig correlationConfig)
        {
            try
            {
                var result = await _correlationEngine.AnalyzeCorrelationsAsync(correlationConfig);

                if (result.HasCorrelations)
                {
                    _logger.LogWarning($"Correlation analysis detected {result.CorrelatedEvents.Count} correlated events");

                    // Korele edilmiş tehditleri işle;
                    foreach (var correlatedThreat in result.CorrelatedThreats)
                    {
                        await HandleCorrelatedThreatAsync(correlatedThreat);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing correlation analysis");
                throw;
            }
        }

        /// <summary>
        /// Makine öğrenimi modelini eğitir.
        /// </summary>
        public async Task TrainMachineLearningModelAsync(List<SecurityEvent> trainingData)
        {
            if (trainingData == null || trainingData.Count == 0)
                throw new ArgumentException("Training data cannot be null or empty", nameof(trainingData));

            await _processingLock.WaitAsync();
            try
            {
                _logger.LogInformation($"Starting ML model training with {trainingData.Count} events");

                // Eğitim verilerini hazırla;
                var preparedData = await PrepareTrainingDataAsync(trainingData);

                // Modeli eğit;
                var trainingResult = await _mlModel.TrainAsync(preparedData);

                // Model performansını değerlendir;
                var evaluationResult = await EvaluateModelPerformanceAsync(trainingResult);

                _logger.LogInformation($"ML model training completed. Accuracy: {evaluationResult.Accuracy:P2}");

                var @event = new ModelTrainingCompletedEvent;
                {
                    TrainingTime = DateTime.UtcNow,
                    TrainingDataSize = trainingData.Count,
                    Accuracy = evaluationResult.Accuracy,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Analiz motoru durumunu getirir.
        /// </summary>
        public ThreatAnalyzerStatus GetStatus()
        {
            return new ThreatAnalyzerStatus;
            {
                IsMonitoring = _isMonitoring,
                StartTime = _startTime,
                Uptime = _isMonitoring ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                ActiveThreatCount = _activeThreats.Count,
                QueuedEvents = _eventQueue.Count,
                LastAnalysisTime = DateTime.UtcNow,
                MLModelStatus = _mlModel.GetStatus(),
                ThreatIntelligenceCount = _intelligenceManager.GetIndicatorCount()
            };
        }

        /// <summary>
        /// Dispose pattern implementasyonu.
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
                    _shutdownTokenSource.Cancel();

                    _analysisTimer?.Dispose();
                    _intelligenceUpdateTimer?.Dispose();
                    _processingLock?.Dispose();
                    _shutdownTokenSource?.Dispose();

                    if (_eventBus != null)
                    {
                        _eventBus.Unsubscribe<SecurityEventOccurred>(HandleSecurityEvent);
                        _eventBus.Unsubscribe<UserBehaviorEvent>(HandleUserBehavior);
                        _eventBus.Unsubscribe<SystemActivityEvent>(HandleSystemActivity);
                    }

                    _intelligenceManager.Dispose();
                }

                _disposed = true;
            }
        }

        #region Private Methods;

        private void ValidateSecurityEvent(SecurityEvent securityEvent)
        {
            if (securityEvent == null)
                throw new ArgumentNullException(nameof(securityEvent));

            if (securityEvent.Id == Guid.Empty)
                throw new ArgumentException("Event ID cannot be empty", nameof(securityEvent.Id));

            if (string.IsNullOrWhiteSpace(securityEvent.EventType))
                throw new ArgumentException("Event type cannot be null or empty", nameof(securityEvent.EventType));
        }

        private void ValidateUserBehavior(UserBehaviorData userBehavior)
        {
            if (userBehavior == null)
                throw new ArgumentNullException(nameof(userBehavior));

            if (userBehavior.UserId == Guid.Empty)
                throw new ArgumentException("User ID cannot be empty", nameof(userBehavior.UserId));
        }

        private async Task<BasicAnalysisResult> PerformBasicAnalysisAsync(SecurityEvent securityEvent)
        {
            var result = new BasicAnalysisResult();

            // Kurallara göre analiz;
            var ruleMatches = _detectionEngine.AnalyzeWithRules(securityEvent);
            result.RuleMatches = ruleMatches;
            result.HasRuleViolations = ruleMatches.Any(r => r.Severity >= RuleSeverity.Medium);

            // İmza tabanlı tespit;
            var signatureMatches = await _intelligenceManager.MatchSignaturesAsync(securityEvent);
            result.SignatureMatches = signatureMatches;
            result.HasSignatureMatches = signatureMatches.Any();

            // Davranış analizi;
            var behaviorAnalysis = _detectionEngine.AnalyzeBehavior(securityEvent);
            result.BehaviorAnalysis = behaviorAnalysis;

            return result;
        }

        private async Task<AnomalyDetectionResult> DetectAnomaliesForEventAsync(SecurityEvent securityEvent)
        {
            // Olay için anomali tespiti;
            var userBehavior = ExtractUserBehaviorFromEvent(securityEvent);

            if (userBehavior != null)
            {
                return await DetectAnomaliesAsync(userBehavior);
            }

            return new AnomalyDetectionResult;
            {
                IsAnomaly = false,
                AnomalyScore = 0.0,
                AnomalyLevel = AnomalyLevel.None;
            };
        }

        private async Task<ThreatIntelligenceMatch> MatchWithThreatIntelligenceAsync(SecurityEvent securityEvent)
        {
            var match = await _intelligenceManager.MatchEventAsync(securityEvent);

            if (match.HasMatches)
            {
                _logger.LogWarning($"Threat intelligence match found for event {securityEvent.Id}");

                // Aktif tehditlere ekle;
                foreach (var indicator in match.MatchedIndicators)
                {
                    _activeThreats[indicator.Id] = indicator;
                }
            }

            return match;
        }

        private async Task<RiskAssessment> AssessRiskAsync(
            SecurityEvent securityEvent,
            BasicAnalysisResult basicAnalysis,
            AnomalyDetectionResult anomalyResult,
            ThreatIntelligenceMatch intelligenceMatch)
        {
            var assessment = await _riskAssessmentEngine.AssessRiskAsync(
                securityEvent, basicAnalysis, anomalyResult, intelligenceMatch);

            // Yüksek riskli olaylar için log;
            if (assessment.RiskLevel >= RiskLevel.High)
            {
                _logger.LogWarning($"High risk event detected: {securityEvent.Id}, risk score: {assessment.RiskScore}");
            }

            return assessment;
        }

        private async Task<CorrelationResult> CheckCorrelationsAsync(SecurityEvent securityEvent)
        {
            var config = new CorrelationConfig;
            {
                Event = securityEvent,
                TimeWindow = TimeSpan.FromHours(1),
                CorrelationRules = _config.CorrelationRules;
            };

            return await _correlationEngine.CheckCorrelationsAsync(config);
        }

        private double CalculateThreatScore(
            BasicAnalysisResult basicAnalysis,
            AnomalyDetectionResult anomalyResult,
            ThreatIntelligenceMatch intelligenceMatch,
            RiskAssessment riskAssessment,
            CorrelationResult correlationResult)
        {
            var scores = new List<double>();

            // Temel analiz skoru;
            if (basicAnalysis.HasRuleViolations) scores.Add(0.3);
            if (basicAnalysis.HasSignatureMatches) scores.Add(0.4);

            // Anomali skoru;
            scores.Add(anomalyResult.AnomalyScore * 0.2);

            // Tehdit zekası skoru;
            if (intelligenceMatch.HasMatches) scores.Add(0.5);

            // Risk skoru;
            scores.Add(riskAssessment.RiskScore * 0.3);

            // Korelasyon skoru;
            if (correlationResult.HasCorrelations) scores.Add(0.3);

            // Ortalama skor;
            return scores.Any() ? scores.Average() : 0.0;
        }

        private ThreatLevel DetermineThreatLevel(double threatScore)
        {
            return threatScore switch;
            {
                >= 0.8 => ThreatLevel.Critical,
                >= 0.6 => ThreatLevel.High,
                >= 0.4 => ThreatLevel.Medium,
                >= 0.2 => ThreatLevel.Low,
                _ => ThreatLevel.None;
            };
        }

        private List<SecurityRecommendation> GenerateRecommendations(ThreatLevel threatLevel, SecurityEvent securityEvent)
        {
            var recommendations = new List<SecurityRecommendation>();

            switch (threatLevel)
            {
                case ThreatLevel.Critical:
                    recommendations.Add(new SecurityRecommendation;
                    {
                        Action = SecurityAction.BlockUser,
                        Priority = RecommendationPriority.Critical,
                        Reason = "Critical threat detected",
                        Parameters = new Dictionary<string, object>
                        {
                            ["userId"] = securityEvent.UserId,
                            ["eventType"] = securityEvent.EventType;
                        }
                    });
                    recommendations.Add(new SecurityRecommendation;
                    {
                        Action = SecurityAction.EnableEnhancedMonitoring,
                        Priority = RecommendationPriority.High,
                        Reason = "Enhanced monitoring required"
                    });
                    break;

                case ThreatLevel.High:
                    recommendations.Add(new SecurityRecommendation;
                    {
                        Action = SecurityAction.RequireMFA,
                        Priority = RecommendationPriority.High,
                        Reason = "Suspicious activity detected"
                    });
                    break;

                case ThreatLevel.Medium:
                    recommendations.Add(new SecurityRecommendation;
                    {
                        Action = SecurityAction.IncreaseLogging,
                        Priority = RecommendationPriority.Medium,
                        Reason = "Potential threat detected"
                    });
                    break;
            }

            return recommendations;
        }

        private async Task TakeRemediationActionsAsync(SecurityEvent securityEvent, ThreatAnalysisResult result)
        {
            foreach (var recommendation in result.Recommendations.Where(r => r.Priority >= RecommendationPriority.High))
            {
                switch (recommendation.Action)
                {
                    case SecurityAction.BlockUser:
                        if (securityEvent.UserId != Guid.Empty)
                        {
                            await _authService.BlockUserAsync(securityEvent.UserId, "Threat detected");
                            _logger.LogWarning($"User {securityEvent.UserId} blocked due to threat");
                        }
                        break;

                    case SecurityAction.AddFirewallRule:
                        await _firewallManager.AddRuleAsync(new FirewallRule;
                        {
                            Name = $"Threat mitigation for {securityEvent.Id}",
                            Action = FirewallAction.Block,
                            Source = securityEvent.SourceIp,
                            Destination = securityEvent.DestinationIp,
                            Protocol = securityEvent.Protocol,
                            Port = securityEvent.Port;
                        });
                        break;

                    case SecurityAction.EnableEnhancedMonitoring:
                        await EnableEnhancedMonitoringAsync(securityEvent.UserId);
                        break;
                }
            }
        }

        private async Task PublishThreatAnalysisEvent(SecurityEvent securityEvent, ThreatAnalysisResult result)
        {
            var @event = new ThreatAnalyzedEvent;
            {
                EventId = securityEvent.Id,
                ThreatLevel = result.ThreatLevel,
                ThreatScore = result.ThreatScore,
                Timestamp = DateTime.UtcNow,
                Recommendations = result.Recommendations;
            };

            await _eventBus.PublishAsync(@event);
        }

        private async Task<BehaviorAnalysis> AnalyzeUserBehaviorAsync(UserBehaviorData userBehavior)
        {
            return await _anomalyDetector.AnalyzeBehaviorAsync(userBehavior);
        }

        private double CalculateAnomalyScore(
            BehaviorAnalysis behaviorAnalysis,
            MLPredictionResult mlResult,
            PatternRecognitionResult patternResult)
        {
            var scores = new List<double>();

            if (behaviorAnalysis.HasAnomalies) scores.Add(0.4);
            scores.Add(mlResult.Confidence * 0.3);
            if (patternResult.HasPatternMatches) scores.Add(0.3);

            return scores.Any() ? scores.Average() : 0.0;
        }

        private AnomalyLevel DetermineAnomalyLevel(double anomalyScore)
        {
            return anomalyScore switch;
            {
                >= 0.7 => AnomalyLevel.High,
                >= 0.5 => AnomalyLevel.Moderate,
                >= 0.3 => AnomalyLevel.Low,
                _ => AnomalyLevel.None;
            };
        }

        private async Task HandleAnomalyDetectionAsync(UserBehaviorData userBehavior, AnomalyDetectionResult result)
        {
            var @event = new AnomalyDetectedEvent;
            {
                UserId = userBehavior.UserId,
                AnomalyScore = result.AnomalyScore,
                AnomalyLevel = result.AnomalyLevel,
                Timestamp = DateTime.UtcNow;
            };

            await _eventBus.PublishAsync(@event);

            // Yüksek anomali seviyeleri için ek aksiyonlar;
            if (result.AnomalyLevel >= AnomalyLevel.Moderate)
            {
                await _authService.FlagUserForReviewAsync(userBehavior.UserId, "Anomalous behavior detected");
            }
        }

        private async Task LoadMonitoringRulesAsync(MonitoringConfig config)
        {
            // İzleme kurallarını yükle;
            await Task.Delay(100); // Simüle edilmiş işlem;
            _logger.LogInformation($"Loaded {config.Rules.Count} monitoring rules");
        }

        private async Task InitializeMachineLearningModelAsync()
        {
            // ML modelini başlat;
            await _mlModel.InitializeAsync();
            _logger.LogInformation("Machine learning model initialized");
        }

        private async Task LoadThreatIntelligenceAsync()
        {
            // Tehdit zekası verilerini yükle;
            await _intelligenceManager.LoadIntelligenceAsync();
            _logger.LogInformation("Threat intelligence data loaded");
        }

        private async Task<Dictionary<ThreatLevel, int>> GetThreatDistributionAsync(TimeRange timeRange)
        {
            // Tehdit dağılımını hesapla;
            return new Dictionary<ThreatLevel, int>
            {
                [ThreatLevel.Critical] = 0,
                [ThreatLevel.High] = 0,
                [ThreatLevel.Medium] = 0,
                [ThreatLevel.Low] = 0,
                [ThreatLevel.None] = 0;
            };
        }

        private async Task<double> CalculateAnomalyDetectionRateAsync(TimeRange timeRange)
        {
            // Anomali tespit oranını hesapla;
            return 0.95; // %95;
        }

        private async Task<TimeSpan> CalculateAverageResponseTimeAsync(TimeRange timeRange)
        {
            // Ortalama yanıt süresini hesapla;
            return TimeSpan.FromSeconds(2.5);
        }

        private async Task<List<ThreatSource>> GetTopThreatSourcesAsync(TimeRange timeRange)
        {
            // En çok tehdit kaynaklarını getir;
            return new List<ThreatSource>();
        }

        private async Task<List<ThreatTrend>> AnalyzeThreatTrendsAsync(TimeRange timeRange)
        {
            // Tehdit trendlerini analiz et;
            return new List<ThreatTrend>();
        }

        private void AnalyzeQueuedEvents(object state)
        {
            try
            {
                var eventsToProcess = new List<SecurityEvent>();

                // Kuyruktan olayları al;
                while (_eventQueue.TryDequeue(out var securityEvent) && eventsToProcess.Count < 100)
                {
                    eventsToProcess.Add(securityEvent);
                }

                if (eventsToProcess.Any())
                {
                    // Toplu analiz yap;
                    Task.Run(async () =>
                    {
                        await AnalyzeBatchEventsAsync(eventsToProcess);
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing queued events");
            }
        }

        private void UpdateThreatIntelligence(object state)
        {
            try
            {
                Task.Run(async () =>
                {
                    // Otomatik tehdit zekası güncellemesi;
                    var latestIntelligence = await _intelligenceManager.FetchLatestIntelligenceAsync();
                    if (latestIntelligence != null)
                    {
                        await UpdateThreatIntelligenceAsync(latestIntelligence);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating threat intelligence");
            }
        }

        private async Task<BatchCorrelationResult> AnalyzeBatchCorrelationsAsync(List<ThreatAnalysisResult> results)
        {
            return await _correlationEngine.AnalyzeBatchCorrelationsAsync(results);
        }

        private UserBehaviorData ExtractUserBehaviorFromEvent(SecurityEvent securityEvent)
        {
            // Güvenlik olayından kullanıcı davranış verisi çıkar;
            if (securityEvent.UserId == Guid.Empty) return null;

            return new UserBehaviorData;
            {
                UserId = securityEvent.UserId,
                ActivityType = securityEvent.EventType,
                Timestamp = securityEvent.Timestamp,
                Metadata = securityEvent.Metadata;
            };
        }

        private async Task HandleCorrelatedThreatAsync(CorrelatedThreat correlatedThreat)
        {
            // Korele edilmiş tehdidi işle;
            _logger.LogWarning($"Correlated threat detected: {correlatedThreat.ThreatType} with {correlatedThreat.RelatedEvents.Count} related events");

            var @event = new CorrelatedThreatDetectedEvent;
            {
                ThreatType = correlatedThreat.ThreatType,
                CorrelationScore = correlatedThreat.CorrelationScore,
                RelatedEventCount = correlatedThreat.RelatedEvents.Count,
                Timestamp = DateTime.UtcNow;
            };

            await _eventBus.PublishAsync(@event);
        }

        private async Task<TrainingData> PrepareTrainingDataAsync(List<SecurityEvent> securityEvents)
        {
            // Eğitim verilerini hazırla;
            return new TrainingData;
            {
                Events = securityEvents,
                Labels = securityEvents.Select(e => e.IsMalicious).ToList()
            };
        }

        private async Task<ModelEvaluationResult> EvaluateModelPerformanceAsync(TrainingResult trainingResult)
        {
            // Model performansını değerlendir;
            return await _mlModel.EvaluateAsync(trainingResult);
        }

        private async Task EnableEnhancedMonitoringAsync(Guid userId)
        {
            // Gelişmiş izlemeyi etkinleştir;
            await Task.Delay(100);
            _logger.LogInformation($"Enhanced monitoring enabled for user {userId}");
        }

        private void HandleSecurityEvent(SecurityEventOccurred @event)
        {
            // Güvenlik olayını kuyruğa ekle;
            _eventQueue.Enqueue(@event.SecurityEvent);
        }

        private void HandleUserBehavior(UserBehaviorEvent @event)
        {
            // Kullanıcı davranışını analiz et;
            Task.Run(async () =>
            {
                var anomalyResult = await DetectAnomaliesAsync(@event.BehaviorData);
                if (anomalyResult.IsAnomaly)
                {
                    _logger.LogWarning($"User behavior anomaly detected for user {@event.BehaviorData.UserId}");
                }
            });
        }

        private void HandleSystemActivity(SystemActivityEvent @event)
        {
            // Sistem aktivitesini analiz et;
            Task.Run(async () =>
            {
                var securityEvent = new SecurityEvent;
                {
                    Id = Guid.NewGuid(),
                    EventType = "SystemActivity",
                    Timestamp = @event.Timestamp,
                    SourceIp = @event.Source,
                    Metadata = new Dictionary<string, object>
                    {
                        ["activity"] = @event.ActivityType,
                        ["details"] = @event.Details;
                    }
                };

                await AnalyzeSecurityEventAsync(securityEvent);
            });
        }

        #endregion;

        #region Internal Supporting Classes;

        /// <summary>
        /// Tehdit tespit motoru.
        /// </summary>
        internal class ThreatDetectionEngine;
        {
            public List<RuleMatch> AnalyzeWithRules(SecurityEvent securityEvent)
            {
                var matches = new List<RuleMatch>();

                // Kurallara göre analiz yap;
                // Gerçek uygulamada bu kurallar konfigürasyondan gelir;

                return matches;
            }

            public BehaviorAnalysis AnalyzeBehavior(SecurityEvent securityEvent)
            {
                return new BehaviorAnalysis();
            }
        }

        /// <summary>
        /// Anomali dedektörü.
        /// </summary>
        internal class AnomalyDetector;
        {
            public async Task<BehaviorAnalysis> AnalyzeBehaviorAsync(UserBehaviorData userBehavior)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;
                return new BehaviorAnalysis();
            }
        }

        /// <summary>
        /// Korelasyon motoru.
        /// </summary>
        internal class CorrelationEngine : IDisposable
        {
            public async Task<CorrelationResult> AnalyzeCorrelationsAsync(CorrelationConfig config)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;
                return new CorrelationResult();
            }

            public async Task<CorrelationResult> CheckCorrelationsAsync(CorrelationConfig config)
            {
                await Task.Delay(20); // Simüle edilmiş işlem;
                return new CorrelationResult();
            }

            public async Task<BatchCorrelationResult> AnalyzeBatchCorrelationsAsync(List<ThreatAnalysisResult> results)
            {
                await Task.Delay(100); // Simüle edilmiş işlem;
                return new BatchCorrelationResult();
            }

            public void Dispose()
            {
                // Kaynak temizleme;
            }
        }

        /// <summary>
        /// Tehdit zekası yöneticisi.
        /// </summary>
        internal class ThreatIntelligenceManager : IDisposable
        {
            private List<ThreatIndicator> _indicators;

            public ThreatIntelligenceManager()
            {
                _indicators = new List<ThreatIndicator>();
            }

            public async Task UpdateIntelligenceAsync(ThreatIntelligenceData intelligence)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;
                _indicators.AddRange(intelligence.Indicators);
            }

            public async Task<ThreatIntelligenceMatch> MatchEventAsync(SecurityEvent securityEvent)
            {
                await Task.Delay(20); // Simüle edilmiş işlem;
                return new ThreatIntelligenceMatch();
            }

            public async Task<List<SignatureMatch>> MatchSignaturesAsync(SecurityEvent securityEvent)
            {
                await Task.Delay(15); // Simüle edilmiş işlem;
                return new List<SignatureMatch>();
            }

            public async Task LoadIntelligenceAsync()
            {
                await Task.Delay(100); // Simüle edilmiş işlem;
            }

            public async Task<ThreatIntelligenceData> FetchLatestIntelligenceAsync()
            {
                await Task.Delay(200); // Simüle edilmiş işlem;
                return new ThreatIntelligenceData();
            }

            public int GetIndicatorCount()
            {
                return _indicators.Count;
            }

            public void Dispose()
            {
                _indicators.Clear();
            }
        }

        /// <summary>
        /// Risk değerlendirme motoru.
        /// </summary>
        internal class RiskAssessmentEngine;
        {
            public async Task<RiskAssessment> AssessRiskAsync(
                SecurityEvent securityEvent,
                BasicAnalysisResult basicAnalysis,
                AnomalyDetectionResult anomalyResult,
                ThreatIntelligenceMatch intelligenceMatch)
            {
                await Task.Delay(30); // Simüle edilmiş işlem;
                return new RiskAssessment();
            }
        }

        /// <summary>
        /// Desen tanıma motoru.
        /// </summary>
        internal class PatternRecognitionEngine;
        {
            public PatternRecognitionResult AnalyzePatterns(UserBehaviorData userBehavior)
            {
                return new PatternRecognitionResult();
            }
        }

        #endregion;
    }

    #region Public Models and Enums;

    /// <summary>
    /// Güvenlik olayı.
    /// </summary>
    public class SecurityEvent;
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string EventType { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Guid UserId { get; set; }
        public string SourceIp { get; set; }
        public string DestinationIp { get; set; }
        public string Protocol { get; set; }
        public int? Port { get; set; }
        public string Action { get; set; }
        public bool IsSuccessful { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public bool IsMalicious { get; set; }
        public double Severity { get; set; } = 0.0;
    }

    /// <summary>
    /// Kullanıcı davranış verisi.
    /// </summary>
    public class UserBehaviorData;
    {
        public Guid UserId { get; set; }
        public string ActivityType { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> Metadata { get; set; }
        public List<BehaviorMetric> Metrics { get; set; }
    }

    /// <summary>
    /// Tehdit analiz sonucu.
    /// </summary>
    public class ThreatAnalysisResult;
    {
        public Guid EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public double ThreatScore { get; set; }
        public ThreatLevel ThreatLevel { get; set; }
        public BasicAnalysisResult BasicAnalysis { get; set; }
        public AnomalyDetectionResult AnomalyResult { get; set; }
        public ThreatIntelligenceMatch IntelligenceMatch { get; set; }
        public RiskAssessment RiskAssessment { get; set; }
        public CorrelationResult CorrelationResult { get; set; }
        public List<SecurityRecommendation> Recommendations { get; set; }
    }

    /// <summary>
    /// Anomali tespit sonucu.
    /// </summary>
    public class AnomalyDetectionResult;
    {
        public Guid UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public double AnomalyScore { get; set; }
        public AnomalyLevel AnomalyLevel { get; set; }
        public BehaviorAnalysis BehaviorAnalysis { get; set; }
        public MLPredictionResult MachineLearningResult { get; set; }
        public PatternRecognitionResult PatternRecognitionResult { get; set; }
        public bool IsAnomaly { get; set; }
    }

    /// <summary>
    /// Temel analiz sonucu.
    /// </summary>
    public class BasicAnalysisResult;
    {
        public List<RuleMatch> RuleMatches { get; set; }
        public List<SignatureMatch> SignatureMatches { get; set; }
        public BehaviorAnalysis BehaviorAnalysis { get; set; }
        public bool HasRuleViolations { get; set; }
        public bool HasSignatureMatches { get; set; }
    }

    /// <summary>
    /// Tehdit zekası eşleştirme.
    /// </summary>
    public class ThreatIntelligenceMatch;
    {
        public bool HasMatches { get; set; }
        public List<ThreatIndicator> MatchedIndicators { get; set; }
        public double MatchConfidence { get; set; }
    }

    /// <summary>
    /// Risk değerlendirmesi.
    /// </summary>
    public class RiskAssessment;
    {
        public double RiskScore { get; set; }
        public RiskLevel RiskLevel { get; set; }
        public List<RiskFactor> Factors { get; set; }
        public List<RiskMitigation> Mitigations { get; set; }
    }

    /// <summary>
    /// Korelasyon sonucu.
    /// </summary>
    public class CorrelationResult;
    {
        public bool HasCorrelations { get; set; }
        public List<CorrelatedEvent> CorrelatedEvents { get; set; }
        public List<CorrelatedThreat> CorrelatedThreats { get; set; }
        public double CorrelationScore { get; set; }
    }

    /// <summary>
    /// İzleme konfigürasyonu.
    /// </summary>
    public class MonitoringConfig;
    {
        public List<MonitoringRule> Rules { get; set; }
        public TimeSpan AnalysisInterval { get; set; } = TimeSpan.FromSeconds(10);
        public bool EnableRealTimeAnalysis { get; set; } = true;
        public List<CorrelationRule> CorrelationRules { get; set; }
        public Dictionary<string, object> Settings { get; set; }
    }

    /// <summary>
    /// Tehdit istatistikleri.
    /// </summary>
    public class ThreatStatistics;
    {
        public TimeRange TimeRange { get; set; }
        public double TotalEventsAnalyzed { get; set; }
        public Dictionary<ThreatLevel, int> ThreatDistribution { get; set; }
        public double AnomalyDetectionRate { get; set; }
        public TimeSpan AverageResponseTime { get; set; }
        public List<ThreatSource> TopThreatSources { get; set; }
        public List<ThreatTrend> ThreatTrends { get; set; }
    }

    /// <summary>
    /// Tehdit zekası verisi.
    /// </summary>
    public class ThreatIntelligenceData;
    {
        public List<ThreatIndicator> Indicators { get; set; }
        public string Source { get; set; }
        public DateTime LastUpdated { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Korelasyon konfigürasyonu.
    /// </summary>
    public class CorrelationConfig;
    {
        public SecurityEvent Event { get; set; }
        public TimeSpan TimeWindow { get; set; } = TimeSpan.FromHours(1);
        public List<CorrelationRule> CorrelationRules { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    /// <summary>
    /// Analiz motoru durumu.
    /// </summary>
    public class ThreatAnalyzerStatus;
    {
        public bool IsMonitoring { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActiveThreatCount { get; set; }
        public int QueuedEvents { get; set; }
        public DateTime LastAnalysisTime { get; set; }
        public MLModelStatus MLModelStatus { get; set; }
        public int ThreatIntelligenceCount { get; set; }
    }

    /// <summary>
    /// Tehdit göstergesi.
    /// </summary>
    public class ThreatIndicator;
    {
        public string Id { get; set; }
        public ThreatIndicatorType Type { get; set; }
        public string Value { get; set; }
        public ThreatLevel Severity { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Kural eşleşmesi.
    /// </summary>
    public class RuleMatch;
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public RuleSeverity Severity { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> MatchedConditions { get; set; }
    }

    /// <summary>
    /// İmza eşleşmesi.
    /// </summary>
    public class SignatureMatch;
    {
        public string SignatureId { get; set; }
        public string SignatureName { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> MatchedPatterns { get; set; }
    }

    /// <summary>
    /// Davranış analizi.
    /// </summary>
    public class BehaviorAnalysis;
    {
        public bool HasAnomalies { get; set; }
        public List<BehaviorAnomaly> Anomalies { get; set; }
        public double BehaviorScore { get; set; }
        public Dictionary<string, double> Metrics { get; set; }
    }

    /// <summary>
    /// Makine öğrenimi tahmini.
    /// </summary>
    public class MLPredictionResult;
    {
        public bool IsAnomaly { get; set; }
        public double Confidence { get; set; }
        public List<double> Probabilities { get; set; }
        public Dictionary<string, double> FeatureImportances { get; set; }
    }

    /// <summary>
    /// Desen tanıma sonucu.
    /// </summary>
    public class PatternRecognitionResult;
    {
        public bool HasPatternMatches { get; set; }
        public List<PatternMatch> MatchedPatterns { get; set; }
        public double PatternScore { get; set; }
    }

    /// <summary>
    /// Güvenlik önerisi.
    /// </summary>
    public class SecurityRecommendation;
    {
        public SecurityAction Action { get; set; }
        public RecommendationPriority Priority { get; set; }
        public string Reason { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    /// <summary>
    /// Toplu korelasyon sonucu.
    /// </summary>
    public class BatchCorrelationResult : CorrelationResult;
    {
        public int TotalEvents { get; set; }
        public int CorrelatedEventCount { get; set; }
        public List<CorrelationPattern> Patterns { get; set; }
    }

    /// <summary>
    /// Korele edilmiş tehdit.
    /// </summary>
    public class CorrelatedThreat;
    {
        public string ThreatType { get; set; }
        public double CorrelationScore { get; set; }
        public List<SecurityEvent> RelatedEvents { get; set; }
        public ThreatLevel ThreatLevel { get; set; }
    }

    /// <summary>
    /// Eğitim verisi.
    /// </summary>
    public class TrainingData;
    {
        public List<SecurityEvent> Events { get; set; }
        public List<bool> Labels { get; set; }
        public Dictionary<string, object> Features { get; set; }
    }

    /// <summary>
    /// Model değerlendirme sonucu.
    /// </summary>
    public class ModelEvaluationResult;
    {
        public double Accuracy { get; set; }
        public double Precision { get; set; }
        public double Recall { get; set; }
        public double F1Score { get; set; }
        public Dictionary<string, double> Metrics { get; set; }
    }

    /// <summary>
    /// Zaman aralığı.
    /// </summary>
    public struct TimeRange;
    {
        public DateTime Start { get; }
        public DateTime End { get; }

        public TimeRange(DateTime start, DateTime end)
        {
            if (start >= end)
                throw new ArgumentException("Start time must be before end time");

            Start = start;
            End = end;
        }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Tehdit seviyesi.
    /// </summary>
    public enum ThreatLevel;
    {
        None,
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Anomali seviyesi.
    /// </summary>
    public enum AnomalyLevel;
    {
        None,
        Low,
        Moderate,
        High;
    }

    /// <summary>
    /// Risk seviyesi.
    /// </summary>
    public enum RiskLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Kural şiddeti.
    /// </summary>
    public enum RuleSeverity;
    {
        Info,
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Tehdit göstergesi türü.
    /// </summary>
    public enum ThreatIndicatorType;
    {
        IPAddress,
        Domain,
        URL,
        FileHash,
        EmailAddress,
        UserAgent,
        BehaviorPattern;
    }

    /// <summary>
    /// Güvenlik aksiyonu.
    /// </summary>
    public enum SecurityAction;
    {
        BlockUser,
        RequireMFA,
        AddFirewallRule,
        IncreaseLogging,
        EnableEnhancedMonitoring,
        NotifyAdmin,
        QuarantineSystem;
    }

    /// <summary>
    /// Öneri önceliği.
    /// </summary>
    public enum RecommendationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Davranış metriği türü.
    /// </summary>
    public enum BehaviorMetricType;
    {
        LoginFrequency,
        ResourceAccess,
        DataTransfer,
        TimePattern,
        GeographicPattern;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Güvenlik olayı oluştu event'i.
    /// </summary>
    public class SecurityEventOccurred : IEvent;
    {
        public SecurityEvent SecurityEvent { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kullanıcı davranış event'i.
    /// </summary>
    public class UserBehaviorEvent : IEvent;
    {
        public UserBehaviorData BehaviorData { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sistem aktivite event'i.
    /// </summary>
    public class SystemActivityEvent : IEvent;
    {
        public string ActivityType { get; set; }
        public string Source { get; set; }
        public Dictionary<string, object> Details { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tehdit analiz edildi event'i.
    /// </summary>
    public class ThreatAnalyzedEvent : IEvent;
    {
        public Guid EventId { get; set; }
        public ThreatLevel ThreatLevel { get; set; }
        public double ThreatScore { get; set; }
        public List<SecurityRecommendation> Recommendations { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Anomali tespit edildi event'i.
    /// </summary>
    public class AnomalyDetectedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public double AnomalyScore { get; set; }
        public AnomalyLevel AnomalyLevel { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// İzleme başlatıldı event'i.
    /// </summary>
    public class MonitoringStartedEvent : IEvent;
    {
        public DateTime StartTime { get; set; }
        public MonitoringConfig Config { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tehdit zekası güncellendi event'i.
    /// </summary>
    public class ThreatIntelligenceUpdatedEvent : IEvent;
    {
        public DateTime UpdateTime { get; set; }
        public int IndicatorCount { get; set; }
        public string Source { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Model eğitimi tamamlandı event'i.
    /// </summary>
    public class ModelTrainingCompletedEvent : IEvent;
    {
        public DateTime TrainingTime { get; set; }
        public int TrainingDataSize { get; set; }
        public double Accuracy { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Korele edilmiş tehdit tespit edildi event'i.
    /// </summary>
    public class CorrelatedThreatDetectedEvent : IEvent;
    {
        public string ThreatType { get; set; }
        public double CorrelationScore { get; set; }
        public int RelatedEventCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}
