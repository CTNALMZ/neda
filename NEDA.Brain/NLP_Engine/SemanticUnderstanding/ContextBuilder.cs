using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.MemorySystem;
using NEDA.Common;
using NEDA.Common.Extensions;
using NEDA.ExceptionHandling;
using NEDA.Monitoring;
using NEDA.NeuralNetwork.PatternRecognition.BehavioralPatterns;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.Brain.IntentRecognition.ContextBuilder.ContextBuilder;

namespace NEDA.Brain.IntentRecognition.ContextBuilder;
{
    /// <summary>
    /// Kontext oluşturma ve yönetim sistemi.
    /// Kullanıcı etkileşimlerini, oturum durumlarını ve çevresel faktörleri birleştirerek;
    /// zengin bağlam bilgileri oluşturur.
    /// </summary>
    public class ContextBuilder : IContextBuilder, IDisposable;
    {
        private readonly ILogger<ContextBuilder> _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ILongTermMemory _longTermMemory;
        private readonly IConversationHistory _conversationHistory;

        private readonly ConcurrentDictionary<string, UserContext> _userContexts;
        private readonly ConcurrentDictionary<string, SessionContext> _sessionContexts;
        private readonly ConcurrentDictionary<string, ConversationContext> _conversationContexts;
        private readonly ReaderWriterLockSlim _contextLock;

        private bool _disposed;
        private readonly ContextBuilderConfiguration _configuration;

        /// <summary>
        /// Kullanıcı bağlamı yapısı;
        /// </summary>
        private class UserContext;
        {
            public string UserId { get; set; } = string.Empty;
            public string SessionId { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public DateTime LastUpdated { get; set; }
            public DateTime LastActivity { get; set; }
            public UserPreferences Preferences { get; set; } = new();
            public UserProfile Profile { get; set; } = new();
            public BehaviorPattern Behavior { get; set; } = new();
            public ContextState CurrentState { get; set; } = new();
            public List<ContextIntent> RecentIntents { get; set; } = new();
            public Dictionary<string, object> CustomAttributes { get; set; } = new();
            public List<ContextEvent> RecentEvents { get; set; } = new();
            public ConversationFlow CurrentConversation { get; set; } = new();
            public EmotionalState EmotionalState { get; set; } = new();
            public CognitiveLoad CognitiveLoad { get; set; } = new();
            public EngagementLevel Engagement { get; set; } = EngagementLevel.Medium;

            public int InteractionCount { get; set; }
            public TimeSpan AverageResponseTime { get; set; }
            public double SatisfactionScore { get; set; }

            // Context windows;
            public RollingContextWindow ShortTermWindow { get; set; } = new(10);
            public RollingContextWindow MediumTermWindow { get; set; } = new(50);
            public RollingContextWindow LongTermWindow { get; set; } = new(200);
        }

        /// <summary>
        /// Oturum bağlamı yapısı;
        /// </summary>
        private class SessionContext;
        {
            public string SessionId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public DateTime? EndTime { get; set; }
            public SessionStatus Status { get; set; }
            public List<Interaction> Interactions { get; set; } = new();
            public SessionMetadata Metadata { get; set; } = new();
            public DeviceInfo Device { get; set; } = new();
            public LocationInfo Location { get; set; } = new();
            public EnvironmentInfo Environment { get; set; } = new();
            public List<string> ActiveTopics { get; set; } = new();
            public Dictionary<string, object> SessionVariables { get; set; } = new();
            public WorkflowState Workflow { get; set; } = new();

            public TimeSpan Duration => (EndTime ?? DateTime.UtcNow) - StartTime;
            public int InteractionCount => Interactions.Count;
            public double EngagementScore { get; set; }
        }

        /// <summary>
        /// Konuşma bağlamı yapısı;
        /// </summary>
        private class ConversationContext;
        {
            public string ConversationId { get; set; } = string.Empty;
            public string SessionId { get; set; } = string.Empty;
            public DateTime StartTime { get; set; }
            public ConversationStatus Status { get; set; }
            public List<ConversationTurn> Turns { get; set; } = new();
            public ConversationFlow Flow { get; set; } = new();
            public List<string> Topics { get; set; } = new();
            public Dictionary<string, object> ContextVariables { get; set; } = new();
            public EmotionalTone EmotionalTone { get; set; } = new();
            public ComplexityLevel Complexity { get; set; }
            public GoalState Goals { get; set; } = new();

            public int TurnCount => Turns.Count;
            public TimeSpan Duration => DateTime.UtcNow - StartTime;
            public double CoherenceScore { get; set; }
            public double ProgressTowardsGoal { get; set; }
        }

        /// <summary>
        /// Context builder konfigürasyonu;
        /// </summary>
        public class ContextBuilderConfiguration;
        {
            public TimeSpan ContextExpiration { get; set; } = TimeSpan.FromHours(24);
            public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);
            public TimeSpan ConversationTimeout { get; set; } = TimeSpan.FromMinutes(15);
            public int MaxContextItems { get; set; } = 1000;
            public int MaxRecentIntents { get; set; } = 20;
            public int MaxRecentEvents { get; set; } = 50;
            public bool EnableContextPersistence { get; set; } = true;
            public bool EnableRealTimeUpdates { get; set; } = true;
            public bool EnablePredictiveContext { get; set; } = true;
            public bool EnableMultimodalContext { get; set; } = true;
            public double ContextRelevanceThreshold { get; set; } = 0.7;
            public TimeSpan CacheCleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
            public Dictionary<string, double> ContextWeights { get; set; } = new()
            {
                { "user_profile", 0.3 },
                { "recent_interactions", 0.25 },
                { "environment", 0.15 },
                { "emotional_state", 0.1 },
                { "temporal", 0.1 },
                { "predictive", 0.05 },
                { "external", 0.05 }
            };
        }

        /// <summary>
        /// Kullanıcı tercihleri;
        /// </summary>
        public class UserPreferences;
        {
            public string Language { get; set; } = "en";
            public string Timezone { get; set; } = "UTC";
            public CommunicationStyle Style { get; set; } = CommunicationStyle.Neutral;
            public DetailLevel DetailPreference { get; set; } = DetailLevel.Medium;
            public ResponseSpeed SpeedPreference { get; set; } = ResponseSpeed.Normal;
            public List<string> PreferredTopics { get; set; } = new();
            public List<string> AvoidedTopics { get; set; } = new();
            public Dictionary<string, object> CustomPreferences { get; set; } = new();
            public AccessibilityPreferences Accessibility { get; set; } = new();
            public NotificationPreferences Notifications { get; set; } = new();

            // Learning preferences;
            public bool AllowLearning { get; set; } = true;
            public bool AllowPersonalization { get; set; } = true;
            public PrivacyLevel PrivacyLevel { get; set; } = PrivacyLevel.Medium;
        }

        /// <summary>
        /// Kullanıcı profili;
        /// </summary>
        public class UserProfile;
        {
            public DemographicInfo Demographics { get; set; } = new();
            public ExpertiseInfo Expertise { get; set; } = new();
            public RelationshipInfo Relationships { get; set; } = new();
            public List<Skill> Skills { get; set; } = new();
            public List<Interest> Interests { get; set; } = new();
            public List<Goal> Goals { get; set; } = new();
            public PersonalityTraits Personality { get; set; } = new();
            public CulturalContext Culture { get; set; } = new();

            public DateTime ProfileCreated { get; set; }
            public DateTime LastProfileUpdate { get; set; }
            public int ProfileCompleteness { get; set; }
        }

        /// <summary>
        /// Davranış pattern'leri;
        /// </summary>
        public class BehaviorPattern;
        {
            public InteractionPatterns Interaction { get; set; } = new();
            public TemporalPatterns Temporal { get; set; } = new();
            public ContentPreferences Content { get; set; } = new();
            public List<Habit> Habits { get; set; } = new();
            public AnomalyDetection Anomalies { get; set; } = new();

            public DateTime LastPatternUpdate { get; set; }
            public double PatternConfidence { get; set; }
        }

        /// <summary>
        /// Bağlam durumu;
        /// </summary>
        public class ContextState;
        {
            public string CurrentIntent { get; set; } = string.Empty;
            public string CurrentTask { get; set; } = string.Empty;
            public string CurrentDomain { get; set; } = string.Empty;
            public List<ContextEntity> Entities { get; set; } = new();
            public Dictionary<string, object> Variables { get; set; } = new();
            public Stack<ContextFrame> FrameStack { get; set; } = new();
            public ContextFocus Focus { get; set; } = new();
            public AttentionLevel Attention { get; set; } = AttentionLevel.Normal;

            public DateTime StateTimestamp { get; set; }
            public int StateDepth { get; set; }
            public double StateConfidence { get; set; }
        }

        /// <summary>
        /// Bağlam intent'i;
        /// </summary>
        public class ContextIntent;
        {
            public string IntentId { get; set; } = string.Empty;
            public string IntentName { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
            public double Confidence { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new();
            public string Domain { get; set; } = string.Empty;
            public IntentOutcome Outcome { get; set; } = new();
            public List<ContextEntity> InvolvedEntities { get; set; } = new();
        }

        /// <summary>
        /// Bağlam event'i;
        /// </summary>
        public class ContextEvent;
        {
            public string EventId { get; set; } = string.Empty;
            public string EventType { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
            public Dictionary<string, object> Data { get; set; } = new();
            public EventSource Source { get; set; }
            public EventImpact Impact { get; set; }
            public List<string> RelatedEntities { get; set; } = new();
        }

        /// <summary>
        /// Duygusal durum;
        /// </summary>
        public class EmotionalState;
        {
            public double Happiness { get; set; }
            public double Sadness { get; set; }
            public double Anger { get; set; }
            public double Fear { get; set; }
            public double Surprise { get; set; }
            public double Disgust { get; set; }
            public double Trust { get; set; }
            public double Anticipation { get; set; }

            public EmotionalValence Valence { get; set; }
            public EmotionalArousal Arousal { get; set; }
            public DateTime LastUpdate { get; set; }
            public double Confidence { get; set; }

            public string PrimaryEmotion => GetPrimaryEmotion();

            private string GetPrimaryEmotion()
            {
                var emotions = new Dictionary<string, double>
                {
                    { "happiness", Happiness },
                    { "sadness", Sadness },
                    { "anger", Anger },
                    { "fear", Fear },
                    { "surprise", Surprise },
                    { "disgust", Disgust },
                    { "trust", Trust },
                    { "anticipation", Anticipation }
                };

                return emotions.OrderByDescending(e => e.Value).First().Key;
            }
        }

        /// <summary>
        /// Bilişsel yük;
        /// </summary>
        public class CognitiveLoad;
        {
            public double IntrinsicLoad { get; set; } // Görevin zorluğu;
            public double ExtraneousLoad { get; set; } // Sunum zorluğu;
            public double GermaneLoad { get; set; } // Öğrenme için kullanılan yük;
            public double TotalLoad => IntrinsicLoad + ExtraneousLoad + GermaneLoad;

            public LoadLevel Level => TotalLoad switch;
            {
                < 30 => LoadLevel.Low,
                < 70 => LoadLevel.Medium,
                _ => LoadLevel.High;
            };

            public DateTime LastAssessment { get; set; }
        }

        /// <summary>
        /// Katılım seviyesi;
        /// </summary>
        public enum EngagementLevel;
        {
            None,
            Low,
            Medium,
            High,
            Intense;
        }

        /// <summary>
        /// İletişim stili;
        /// </summary>
        public enum CommunicationStyle;
        {
            Formal,
            Informal,
            Casual,
            Technical,
            Friendly,
            Neutral,
            Authoritative;
        }

        /// <summary>
        /// Detay seviyesi;
        /// </summary>
        public enum DetailLevel;
        {
            Minimal,
            Low,
            Medium,
            High,
            Extensive;
        }

        /// <summary>
        /// Yanıt hızı;
        /// </summary>
        public enum ResponseSpeed;
        {
            Slow,
            Normal,
            Fast,
            Instant;
        }

        /// <summary>
        /// Gizlilik seviyesi;
        /// </summary>
        public enum PrivacyLevel;
        {
            Public,
            Low,
            Medium,
            High,
            Strict;
        }

        /// <summary>
        /// Dikkat seviyesi;
        /// </summary>
        public enum AttentionLevel;
        {
            Distracted,
            Low,
            Normal,
            High,
            Focused;
        }

        /// <summary>
        /// Duygusal değer;
        /// </summary>
        public enum EmotionalValence;
        {
            VeryNegative = -2,
            Negative = -1,
            Neutral = 0,
            Positive = 1,
            VeryPositive = 2;
        }

        /// <summary>
        /// Duygusal uyarılma;
        /// </summary>
        public enum EmotionalArousal;
        {
            Calm,
            Mild,
            Moderate,
            High,
            Intense;
        }

        /// <summary>
        /// Yük seviyesi;
        /// </summary>
        public enum LoadLevel;
        {
            Low,
            Medium,
            High,
            Overloaded;
        }

        public event EventHandler<ContextUpdatedEventArgs>? ContextUpdated;
        public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
        public event EventHandler<ConversationStateChangedEventArgs>? ConversationStateChanged;

        private readonly Timer _cleanupTimer;
        private readonly ContextBuilderStatistics _statistics;
        private readonly object _statsLock = new object();

        /// <summary>
        /// ContextBuilder örneği oluşturur;
        /// </summary>
        public ContextBuilder(
            ILogger<ContextBuilder> logger,
            IMetricsCollector metricsCollector,
            IShortTermMemory shortTermMemory,
            ILongTermMemory longTermMemory,
            IConversationHistory conversationHistory,
            ContextBuilderConfiguration? configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));
            _conversationHistory = conversationHistory ?? throw new ArgumentNullException(nameof(conversationHistory));

            _configuration = configuration ?? new ContextBuilderConfiguration();
            _userContexts = new ConcurrentDictionary<string, UserContext>();
            _sessionContexts = new ConcurrentDictionary<string, SessionContext>();
            _conversationContexts = new ConcurrentDictionary<string, ConversationContext>();
            _contextLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _statistics = new ContextBuilderStatistics();

            // Temizlik timer'ı başlat;
            _cleanupTimer = new Timer(
                _ => CleanupExpiredContexts(),
                null,
                _configuration.CacheCleanupInterval,
                _configuration.CacheCleanupInterval);

            _logger.LogInformation("ContextBuilder initialized with expiration: {Expiration}",
                _configuration.ContextExpiration);
        }

        /// <summary>
        /// Yeni oturum başlatır;
        /// </summary>
        public async Task<SessionContextResult> StartSessionAsync(
            SessionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var sessionId = GenerateSessionId();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _contextLock.EnterWriteLock();

                // Önceki oturumları kontrol et;
                var existingSessions = _sessionContexts.Values;
                    .Where(s => s.UserId == request.UserId && s.Status == SessionStatus.Active)
                    .ToList();

                foreach (var existingSession in existingSessions)
                {
                    await EndSessionAsync(existingSession.SessionId, cancellationToken);
                }

                // Yeni oturum oluştur;
                var session = new SessionContext;
                {
                    SessionId = sessionId,
                    UserId = request.UserId,
                    StartTime = DateTime.UtcNow,
                    Status = SessionStatus.Active,
                    Metadata = request.Metadata ?? new SessionMetadata(),
                    Device = request.DeviceInfo ?? new DeviceInfo(),
                    Location = request.LocationInfo ?? new LocationInfo(),
                    Environment = request.EnvironmentInfo ?? new EnvironmentInfo(),
                    SessionVariables = new Dictionary<string, object>()
                };

                // Kullanıcı bağlamı oluştur veya güncelle;
                if (!_userContexts.TryGetValue(request.UserId, out var userContext))
                {
                    userContext = new UserContext;
                    {
                        UserId = request.UserId,
                        SessionId = sessionId,
                        CreatedAt = DateTime.UtcNow,
                        LastUpdated = DateTime.UtcNow,
                        LastActivity = DateTime.UtcNow,
                        CurrentState = new ContextState(),
                        EmotionalState = new EmotionalState(),
                        CognitiveLoad = new CognitiveLoad()
                    };

                    // Uzun süreli bellekten profil yükle;
                    await LoadUserProfileAsync(userContext, cancellationToken);

                    _userContexts[request.UserId] = userContext;
                }
                else;
                {
                    userContext.SessionId = sessionId;
                    userContext.LastUpdated = DateTime.UtcNow;
                    userContext.LastActivity = DateTime.UtcNow;
                }

                // Oturumu kaydet;
                if (_sessionContexts.TryAdd(sessionId, session))
                {
                    // Kısa süreli belleğe oturum bilgisini kaydet;
                    await _shortTermMemory.StoreAsync($"session:{sessionId}", session,
                        TimeSpan.FromHours(1), cancellationToken);

                    // Event tetikle;
                    SessionStateChanged?.Invoke(this,
                        new SessionStateChangedEventArgs(sessionId, request.UserId, SessionEventType.Started));

                    UpdateStatistics(sessionStarted: true);

                    _logger.LogInformation("Session started. SessionId: {SessionId}, UserId: {UserId}",
                        sessionId, request.UserId);

                    return SessionContextResult.Success(sessionId, userContext, session);
                }

                return SessionContextResult.Failure("Failed to create session");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting session for user: {UserId}", request.UserId);
                throw new ContextBuildingException($"Failed to start session: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
                _contextLock.ExitWriteLock();

                RecordMetrics("session_start", stopwatch.ElapsedMilliseconds, new Dictionary<string, string>
                {
                    { "session_id", sessionId },
                    { "user_id", request.UserId }
                });
            }
        }

        /// <summary>
        /// Oturumu sonlandırır;
        /// </summary>
        public async Task<SessionContextResult> EndSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            try
            {
                _contextLock.EnterWriteLock();

                if (!_sessionContexts.TryGetValue(sessionId, out var session))
                {
                    return SessionContextResult.Failure($"Session not found: {sessionId}");
                }

                // Oturum durumunu güncelle;
                session.Status = SessionStatus.Ended;
                session.EndTime = DateTime.UtcNow;

                // Kullanıcı bağlamını güncelle;
                if (_userContexts.TryGetValue(session.UserId, out var userContext))
                {
                    userContext.LastUpdated = DateTime.UtcNow;
                    userContext.LastActivity = DateTime.UtcNow;

                    // Oturum bilgilerini uzun süreli belleğe kaydet;
                    await SaveSessionToLongTermMemoryAsync(session, userContext, cancellationToken);
                }

                // Event tetikle;
                SessionStateChanged?.Invoke(this,
                    new SessionStateChangedEventArgs(sessionId, session.UserId, SessionEventType.Ended));

                UpdateStatistics(sessionEnded: true);

                _logger.LogInformation("Session ended. SessionId: {SessionId}, Duration: {Duration}",
                    sessionId, session.Duration);

                return SessionContextResult.Success(sessionId, userContext, session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending session: {SessionId}", sessionId);
                throw new ContextBuildingException($"Failed to end session: {sessionId}", ex);
            }
            finally
            {
                _contextLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Bağlam günceller;
        /// </summary>
        public async Task<ContextUpdateResult> UpdateContextAsync(
            ContextUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _contextLock.EnterUpgradeableReadLock();

                // Kullanıcı ve oturum bağlamlarını al;
                if (!_userContexts.TryGetValue(request.UserId, out var userContext) ||
                    !_sessionContexts.TryGetValue(request.SessionId, out var session))
                {
                    return ContextUpdateResult.Failure("User or session context not found");
                }

                // Oturum timeout kontrolü;
                if (session.Status != SessionStatus.Active ||
                    DateTime.UtcNow - session.StartTime > _configuration.SessionTimeout)
                {
                    await EndSessionAsync(request.SessionId, cancellationToken);
                    return ContextUpdateResult.Failure("Session has expired");
                }

                _contextLock.EnterWriteLock();

                // Bağlam güncellemelerini uygula;
                var updatesApplied = await ApplyContextUpdatesAsync(
                    userContext, session, request, cancellationToken);

                // Konuşma bağlamını güncelle;
                await UpdateConversationContextAsync(userContext, session, request, cancellationToken);

                // Kullanıcı etkileşim sayacını artır;
                userContext.InteractionCount++;
                userContext.LastActivity = DateTime.UtcNow;
                userContext.LastUpdated = DateTime.UtcNow;

                // Oturum etkileşimlerini güncelle;
                var interaction = CreateInteractionFromRequest(request);
                session.Interactions.Add(interaction);
                session.EngagementScore = CalculateEngagementScore(session);

                // Kısa süreli belleğe güncelle;
                await _shortTermMemory.StoreAsync($"user_context:{request.UserId}", userContext,
                    TimeSpan.FromHours(1), cancellationToken);

                // Event tetikle;
                ContextUpdated?.Invoke(this,
                    new ContextUpdatedEventArgs(request.UserId, request.SessionId,
                        updatesApplied, DateTime.UtcNow));

                UpdateStatistics(contextUpdated: true);

                _logger.LogDebug("Context updated for user: {UserId}, Session: {SessionId}, Updates: {Count}",
                    request.UserId, request.SessionId, updatesApplied.Count);

                return ContextUpdateResult.Success(userContext, session, updatesApplied);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating context for user: {UserId}", request.UserId);
                throw new ContextBuildingException($"Failed to update context: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();

                if (_contextLock.IsWriteLockHeld) _contextLock.ExitWriteLock();
                _contextLock.ExitUpgradeableReadLock();

                RecordMetrics("context_update", stopwatch.ElapsedMilliseconds, new Dictionary<string, string>
                {
                    { "user_id", request.UserId },
                    { "session_id", request.SessionId }
                });
            }
        }

        /// <summary>
        /// Bağlam oluşturur;
        /// </summary>
        public async Task<RichContext> BuildRichContextAsync(
            string userId,
            string sessionId,
            string? currentIntent = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _contextLock.EnterReadLock();

                // Temel bağlamları al;
                var userContext = _userContexts.TryGetValue(userId, out var uc) ? uc : null;
                var sessionContext = _sessionContexts.TryGetValue(sessionId, out var sc) ? sc : null;
                var conversationContext = _conversationContexts.Values;
                    .FirstOrDefault(c => c.SessionId == sessionId && c.Status == ConversationStatus.Active);

                // Zengin bağlam oluştur;
                var richContext = new RichContext;
                {
                    Timestamp = DateTime.UtcNow,
                    UserContext = userContext,
                    SessionContext = sessionContext,
                    ConversationContext = conversationContext,
                    CurrentIntent = currentIntent,

                    // Bileşenleri birleştir;
                    IntegratedContext = await IntegrateContextComponentsAsync(
                        userContext, sessionContext, conversationContext, cancellationToken),

                    // Bağlam özellikleri;
                    ContextFeatures = ExtractContextFeatures(userContext, sessionContext, conversationContext),

                    // Tahmini bağlam;
                    PredictiveContext = _configuration.EnablePredictiveContext ?
                        await GeneratePredictiveContextAsync(userId, sessionId, cancellationToken) : null,

                    // Çoklu modal bağlam;
                    MultimodalContext = _configuration.EnableMultimodalContext ?
                        await BuildMultimodalContextAsync(userId, sessionId, cancellationToken) : null,

                    // Bağlam kalitesi;
                    QualityMetrics = CalculateContextQuality(userContext, sessionContext, conversationContext)
                };

                // Bağlam ilgisellik skoru;
                richContext.RelevanceScore = CalculateRelevanceScore(richContext, currentIntent);

                // Geçmiş bağlamları ekle;
                richContext.HistoricalContext = await GetHistoricalContextAsync(userId, 5, cancellationToken);

                // Bağlam pencereleri;
                richContext.ContextWindows = BuildContextWindows(userContext);

                _logger.LogDebug("Rich context built for user: {UserId}, Relevance: {Relevance:F2}",
                    userId, richContext.RelevanceScore);

                return richContext;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building rich context for user: {UserId}", userId);
                throw new ContextBuildingException($"Failed to build rich context: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
                _contextLock.ExitReadLock();

                RecordMetrics("context_build", stopwatch.ElapsedMilliseconds, new Dictionary<string, string>
                {
                    { "user_id", userId },
                    { "session_id", sessionId }
                });
            }
        }

        /// <summary>
        /// Konuşma bağlamını alır;
        /// </summary>
        public async Task<ConversationContextResult> GetConversationContextAsync(
            string conversationId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("Conversation ID cannot be null or empty", nameof(conversationId));

            try
            {
                _contextLock.EnterReadLock();

                if (!_conversationContexts.TryGetValue(conversationId, out var conversation))
                {
                    return ConversationContextResult.Failure($"Conversation not found: {conversationId}");
                }

                // Konuşma geçmişini getir;
                var history = await _conversationHistory.GetConversationAsync(conversationId, cancellationToken);

                // Bağlam derinliğini artır;
                var enrichedContext = await EnrichConversationContextAsync(conversation, history, cancellationToken);

                return ConversationContextResult.Success(conversationId, enrichedContext, history);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting conversation context: {ConversationId}", conversationId);
                throw new ContextBuildingException($"Failed to get conversation context: {conversationId}", ex);
            }
            finally
            {
                _contextLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Kullanıcı bağlamını alır;
        /// </summary>
        public async Task<UserContextResult> GetUserContextAsync(
            string userId,
            bool includePredictive = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                _contextLock.EnterReadLock();

                if (!_userContexts.TryGetValue(userId, out var userContext))
                {
                    // Bağlam yoksa oluştur;
                    userContext = await CreateDefaultUserContextAsync(userId, cancellationToken);
                }

                var result = new UserContextResult;
                {
                    UserId = userId,
                    Context = userContext,
                    IsActive = userContext.CurrentState != null,
                    LastActivity = userContext.LastActivity,
                    EngagementLevel = userContext.Engagement;
                };

                // Tahmini verileri ekle;
                if (includePredictive && _configuration.EnablePredictiveContext)
                {
                    result.PredictiveInsights = await GeneratePredictiveInsightsAsync(userId, cancellationToken);
                    result.BehaviorPredictions = await PredictUserBehaviorAsync(userId, cancellationToken);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user context: {UserId}", userId);
                throw new ContextBuildingException($"Failed to get user context: {userId}", ex);
            }
            finally
            {
                _contextLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Bağlam geçmişini alır;
        /// </summary>
        public async Task<ContextHistoryResult> GetContextHistoryAsync(
            string userId,
            TimeRange timeRange,
            int maxItems = 100,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                // Kısa süreli bellekten geçmişi al;
                var shortTermHistory = await _shortTermMemory.RetrieveRangeAsync<ContextSnapshot>(
                    $"context_history:{userId}", timeRange.Start, timeRange.End, cancellationToken);

                // Uzun süreli bellekten geçmişi al;
                var longTermHistory = await _longTermMemory.RetrieveContextHistoryAsync(
                    userId, timeRange.Start, timeRange.End, maxItems, cancellationToken);

                // Geçmişleri birleştir ve sırala;
                var allHistory = shortTermHistory;
                    .Concat(longTermHistory)
                    .OrderByDescending(h => h.Timestamp)
                    .Take(maxItems)
                    .ToList();

                // Bağlam trendlerini analiz et;
                var trends = AnalyzeContextTrends(allHistory);

                return new ContextHistoryResult;
                {
                    UserId = userId,
                    History = allHistory,
                    Trends = trends,
                    TimeRange = timeRange,
                    TotalItems = allHistory.Count;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting context history for user: {UserId}", userId);
                throw new ContextBuildingException($"Failed to get context history: {userId}", ex);
            }
        }

        /// <summary>
        /// Bağlam istatistiklerini getirir;
        /// </summary>
        public ContextBuilderStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                _statistics.ActiveUsers = _userContexts.Count;
                _statistics.ActiveSessions = _sessionContexts.Count(s => s.Value.Status == SessionStatus.Active);
                _statistics.ActiveConversations = _conversationContexts.Count(c => c.Value.Status == ConversationStatus.Active);
                _statistics.TotalInteractions = _sessionContexts.Values.Sum(s => s.InteractionCount);
                _statistics.LastUpdated = DateTime.UtcNow;

                return new ContextBuilderStatistics;
                {
                    ActiveUsers = _statistics.ActiveUsers,
                    ActiveSessions = _statistics.ActiveSessions,
                    ActiveConversations = _statistics.ActiveConversations,
                    TotalInteractions = _statistics.TotalInteractions,
                    AverageSessionDuration = _statistics.AverageSessionDuration,
                    AverageEngagementScore = _statistics.AverageEngagementScore,
                    ContextUpdateCount = _statistics.ContextUpdateCount,
                    LastUpdated = _statistics.LastUpdated;
                };
            }
        }

        /// <summary>
        /// Süresi dolmuş bağlamları temizler;
        /// </summary>
        private void CleanupExpiredContexts()
        {
            try
            {
                _contextLock.EnterWriteLock();

                var now = DateTime.UtcNow;
                var removedCount = 0;

                // Süresi dolmuş kullanıcı bağlamlarını temizle;
                var expiredUsers = _userContexts.Where(kvp =>
                    now - kvp.Value.LastActivity > _configuration.ContextExpiration).ToList();

                foreach (var user in expiredUsers)
                {
                    if (_userContexts.TryRemove(user.Key, out _))
                    {
                        removedCount++;
                    }
                }

                // Süresi dolmuş oturumları temizle;
                var expiredSessions = _sessionContexts.Where(kvp =>
                    kvp.Value.Status == SessionStatus.Ended &&
                    now - kvp.Value.EndTime > _configuration.ContextExpiration).ToList();

                foreach (var session in expiredSessions)
                {
                    if (_sessionContexts.TryRemove(session.Key, out _))
                    {
                        removedCount++;
                    }
                }

                // Süresi dolmuş konuşmaları temizle;
                var expiredConversations = _conversationContexts.Where(kvp =>
                    kvp.Value.Status != ConversationStatus.Active &&
                    now - kvp.Value.StartTime > _configuration.ContextExpiration).ToList();

                foreach (var conversation in expiredConversations)
                {
                    if (_conversationContexts.TryRemove(conversation.Key, out _))
                    {
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} expired contexts", removedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during context cleanup");
            }
            finally
            {
                _contextLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Bağlam güncellemelerini uygular;
        /// </summary>
        private async Task<Dictionary<string, object>> ApplyContextUpdatesAsync(
            UserContext userContext,
            SessionContext session,
            ContextUpdateRequest request,
            CancellationToken cancellationToken)
        {
            var updatesApplied = new Dictionary<string, object>();

            // 1. Intent güncellemesi;
            if (!string.IsNullOrEmpty(request.Intent))
            {
                var intent = new ContextIntent;
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    IntentName = request.Intent,
                    Timestamp = DateTime.UtcNow,
                    Confidence = request.Confidence,
                    Parameters = request.Parameters ?? new Dictionary<string, object>(),
                    Domain = request.Domain ?? "general"
                };

                userContext.RecentIntents.Add(intent);

                // Maksimum boyutu kontrol et;
                if (userContext.RecentIntents.Count > _configuration.MaxRecentIntents)
                {
                    userContext.RecentIntents.RemoveAt(0);
                }

                userContext.CurrentState.CurrentIntent = request.Intent;
                userContext.CurrentState.CurrentDomain = request.Domain ?? "general";
                updatesApplied["intent"] = request.Intent;
            }

            // 2. Entity güncellemesi;
            if (request.Entities?.Any() == true)
            {
                userContext.CurrentState.Entities = request.Entities.Select(e => new ContextEntity;
                {
                    EntityId = e.EntityId,
                    EntityType = e.EntityType,
                    Value = e.Value,
                    Confidence = e.Confidence,
                    Attributes = e.Attributes ?? new Dictionary<string, object>()
                }).ToList();

                updatesApplied["entities"] = request.Entities.Count;
            }

            // 3. Değişken güncellemesi;
            if (request.Variables?.Any() == true)
            {
                foreach (var variable in request.Variables)
                {
                    userContext.CurrentState.Variables[variable.Key] = variable.Value;
                    session.SessionVariables[variable.Key] = variable.Value;
                }
                updatesApplied["variables"] = request.Variables.Count;
            }

            // 4. Duygusal durum güncellemesi;
            if (request.EmotionalState != null)
            {
                userContext.EmotionalState = MergeEmotionalStates(
                    userContext.EmotionalState, request.EmotionalState);
                updatesApplied["emotional_state"] = "updated";
            }

            // 5. Bilişsel yük güncellemesi;
            if (request.CognitiveLoad != null)
            {
                userContext.CognitiveLoad = request.CognitiveLoad;
                updatesApplied["cognitive_load"] = request.CognitiveLoad.Level.ToString();
            }

            // 6. Katılım seviyesi güncellemesi;
            if (request.EngagementLevel.HasValue)
            {
                userContext.Engagement = request.EngagementLevel.Value;
                updatesApplied["engagement"] = request.EngagementLevel.Value.ToString();
            }

            // 7. Event kaydı;
            if (request.Event != null)
            {
                var contextEvent = new ContextEvent;
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = request.Event.EventType,
                    Timestamp = DateTime.UtcNow,
                    Data = request.Event.Data ?? new Dictionary<string, object>(),
                    Source = request.Event.Source,
                    Impact = request.Event.Impact;
                };

                userContext.RecentEvents.Add(contextEvent);

                // Maksimum boyutu kontrol et;
                if (userContext.RecentEvents.Count > _configuration.MaxRecentEvents)
                {
                    userContext.RecentEvents.RemoveAt(0);
                }

                updatesApplied["event"] = request.Event.EventType;
            }

            // 8. Tercih güncellemesi;
            if (request.PreferenceUpdates?.Any() == true)
            {
                await ApplyPreferenceUpdatesAsync(userContext, request.PreferenceUpdates, cancellationToken);
                updatesApplied["preferences"] = request.PreferenceUpdates.Count;
            }

            // 9. Davranış pattern güncellemesi;
            if (request.BehaviorUpdate != null)
            {
                UpdateBehaviorPatterns(userContext, request.BehaviorUpdate);
                updatesApplied["behavior"] = "updated";
            }

            return updatesApplied;
        }

        /// <summary>
        /// Kullanıcı profili yükler;
        /// </summary>
        private async Task LoadUserProfileAsync(UserContext userContext, CancellationToken cancellationToken)
        {
            try
            {
                // Uzun süreli bellekten profil yükle;
                var profile = await _longTermMemory.RetrieveUserProfileAsync(userContext.UserId, cancellationToken);

                if (profile != null)
                {
                    userContext.Profile = profile;
                    userContext.Preferences = profile.Preferences ?? new UserPreferences();

                    // Davranış pattern'lerini yükle;
                    var behavior = await _longTermMemory.RetrieveBehaviorPatternsAsync(userContext.UserId, cancellationToken);
                    if (behavior != null)
                    {
                        userContext.Behavior = behavior;
                    }
                }
                else;
                {
                    // Varsayılan profil oluştur;
                    userContext.Profile = new UserProfile;
                    {
                        ProfileCreated = DateTime.UtcNow,
                        LastProfileUpdate = DateTime.UtcNow,
                        ProfileCompleteness = 10 // %10;
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error loading user profile for: {UserId}", userContext.UserId);
                // Varsayılan değerlerle devam et;
            }
        }

        /// <summary>
        /// Oturumu uzun süreli belleğe kaydeder;
        /// </summary>
        private async Task SaveSessionToLongTermMemoryAsync(
            SessionContext session,
            UserContext userContext,
            CancellationToken cancellationToken)
        {
            try
            {
                // Oturum özeti oluştur;
                var sessionSummary = new SessionSummary;
                {
                    SessionId = session.SessionId,
                    UserId = session.UserId,
                    StartTime = session.StartTime,
                    EndTime = session.EndTime ?? DateTime.UtcNow,
                    Duration = session.Duration,
                    InteractionCount = session.InteractionCount,
                    EngagementScore = session.EngagementScore,
                    Topics = session.ActiveTopics,
                    Metadata = session.Metadata;
                };

                // Uzun süreli belleğe kaydet;
                await _longTermMemory.StoreSessionAsync(sessionSummary, cancellationToken);

                // Kullanıcı profili güncelle;
                await UpdateUserProfileFromSessionAsync(userContext, session, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error saving session to long-term memory: {SessionId}", session.SessionId);
            }
        }

        /// <summary>
        /// Bağlam bileşenlerini entegre eder;
        /// </summary>
        private async Task<IntegratedContext> IntegrateContextComponentsAsync(
            UserContext? userContext,
            SessionContext? sessionContext,
            ConversationContext? conversationContext,
            CancellationToken cancellationToken)
        {
            var integrated = new IntegratedContext;
            {
                Timestamp = DateTime.UtcNow,
                Components = new List<ContextComponent>()
            };

            // 1. Kullanıcı bağlamı;
            if (userContext != null)
            {
                integrated.Components.Add(new ContextComponent;
                {
                    Type = "user",
                    Weight = _configuration.ContextWeights["user_profile"],
                    Data = ExtractUserContextData(userContext)
                });
            }

            // 2. Son etkileşimler;
            if (sessionContext?.Interactions.Any() == true)
            {
                var recentInteractions = sessionContext.Interactions;
                    .TakeLast(10)
                    .Select(i => new InteractionSummary(i))
                    .ToList();

                integrated.Components.Add(new ContextComponent;
                {
                    Type = "recent_interactions",
                    Weight = _configuration.ContextWeights["recent_interactions"],
                    Data = recentInteractions;
                });
            }

            // 3. Çevresel faktörler;
            if (sessionContext != null)
            {
                integrated.Components.Add(new ContextComponent;
                {
                    Type = "environment",
                    Weight = _configuration.ContextWeights["environment"],
                    Data = new;
                    {
                        sessionContext.Device,
                        sessionContext.Location,
                        sessionContext.Environment;
                    }
                });
            }

            // 4. Duygusal durum;
            if (userContext?.EmotionalState != null)
            {
                integrated.Components.Add(new ContextComponent;
                {
                    Type = "emotional_state",
                    Weight = _configuration.ContextWeights["emotional_state"],
                    Data = userContext.EmotionalState;
                });
            }

            // 5. Zamansal faktörler;
            integrated.Components.Add(new ContextComponent;
            {
                Type = "temporal",
                Weight = _configuration.ContextWeights["temporal"],
                Data = new;
                {
                    TimeOfDay = DateTime.UtcNow.TimeOfDay,
                    DayOfWeek = DateTime.UtcNow.DayOfWeek,
                    IsWeekend = DateTime.UtcNow.DayOfWeek == DayOfWeek.Saturday ||
                               DateTime.UtcNow.DayOfWeek == DayOfWeek.Sunday;
                }
            });

            // 6. Tahmini bağlam;
            if (_configuration.EnablePredictiveContext && userContext != null)
            {
                var predictive = await GeneratePredictiveContextAsync(
                    userContext.UserId, sessionContext?.SessionId, cancellationToken);

                integrated.Components.Add(new ContextComponent;
                {
                    Type = "predictive",
                    Weight = _configuration.ContextWeights["predictive"],
                    Data = predictive;
                });
            }

            // 7. Harici bağlam;
            integrated.Components.Add(new ContextComponent;
            {
                Type = "external",
                Weight = _configuration.ContextWeights["external"],
                Data = await GetExternalContextAsync(cancellationToken)
            });

            // Toplam ağırlığı normalize et;
            var totalWeight = integrated.Components.Sum(c => c.Weight);
            if (totalWeight > 0)
            {
                foreach (var component in integrated.Components)
                {
                    component.NormalizedWeight = component.Weight / totalWeight;
                }
            }

            integrated.TotalComponents = integrated.Components.Count;
            integrated.IntegrationScore = CalculateIntegrationScore(integrated.Components);

            return integrated;
        }

        /// <summary>
        /// Bağlam özellikleri çıkarır;
        /// </summary>
        private Dictionary<string, object> ExtractContextFeatures(
            UserContext? userContext,
            SessionContext? sessionContext,
            ConversationContext? conversationContext)
        {
            var features = new Dictionary<string, object>();

            if (userContext != null)
            {
                features["user_engagement"] = userContext.Engagement.ToString();
                features["user_experience"] = userContext.InteractionCount;
                features["emotional_valence"] = userContext.EmotionalState.Valence.ToString();
                features["cognitive_load"] = userContext.CognitiveLoad.Level.ToString();
            }

            if (sessionContext != null)
            {
                features["session_duration"] = sessionContext.Duration.TotalMinutes;
                features["interaction_count"] = sessionContext.InteractionCount;
                features["active_topics"] = sessionContext.ActiveTopics.Count;
            }

            if (conversationContext != null)
            {
                features["conversation_turns"] = conversationContext.TurnCount;
                features["conversation_coherence"] = conversationContext.CoherenceScore;
                features["goal_progress"] = conversationContext.ProgressTowardsGoal;
            }

            // Türetilmiş özellikler;
            features["context_completeness"] = CalculateContextCompleteness(
                userContext, sessionContext, conversationContext);
            features["context_freshness"] = CalculateContextFreshness(
                userContext?.LastUpdated, sessionContext?.StartTime);
            features["interaction_intensity"] = CalculateInteractionIntensity(sessionContext);

            return features;
        }

        /// <summary>
        /// Bağlam ilgisellik skoru hesaplar;
        /// </summary>
        private double CalculateRelevanceScore(RichContext context, string? currentIntent)
        {
            double score = 0.0;

            // 1. Intent ilgisellik;
            if (!string.IsNullOrEmpty(currentIntent) && context.UserContext?.CurrentState != null)
            {
                if (context.UserContext.CurrentState.CurrentIntent == currentIntent)
                {
                    score += 0.3;
                }

                // Son intent'lerde var mı kontrol et;
                var recentIntentMatch = context.UserContext.RecentIntents;
                    .Any(i => i.IntentName == currentIntent &&
                            (DateTime.UtcNow - i.Timestamp).TotalMinutes < 30);
                if (recentIntentMatch) score += 0.2;
            }

            // 2. Bağlam tazeliği;
            var freshness = CalculateContextFreshness(
                context.UserContext?.LastUpdated,
                context.SessionContext?.StartTime);
            score += freshness * 0.2;

            // 3. Etkileşim yoğunluğu;
            var intensity = CalculateInteractionIntensity(context.SessionContext);
            score += intensity * 0.15;

            // 4. Bağlam bütünlüğü;
            var completeness = CalculateContextCompleteness(
                context.UserContext, context.SessionContext, context.ConversationContext);
            score += completeness * 0.15;

            // 5. Konuşma tutarlılığı;
            if (context.ConversationContext != null)
            {
                score += context.ConversationContext.CoherenceScore * 0.1;
            }

            return Math.Min(score, 1.0);
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
            if (!_disposed)
            {
                if (disposing)
                {
                    _cleanupTimer?.Dispose();
                    _contextLock?.Dispose();

                    // Tüm aktif oturumları sonlandır;
                    var activeSessions = _sessionContexts.Values;
                        .Where(s => s.Status == SessionStatus.Active)
                        .ToList();

                    foreach (var session in activeSessions)
                    {
                        _ = EndSessionAsync(session.SessionId, CancellationToken.None);
                    }

                    _logger.LogInformation("ContextBuilder disposed");
                }

                _disposed = true;
            }
        }

        // Yardımcı metodlar;
        private string GenerateSessionId() => $"sess_{Guid.NewGuid():N}";

        private string GenerateConversationId() => $"conv_{Guid.NewGuid():N}";

        private Interaction CreateInteractionFromRequest(ContextUpdateRequest request)
        {
            return new Interaction;
            {
                InteractionId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                Type = request.InteractionType ?? "message",
                Content = request.Content ?? string.Empty,
                Intent = request.Intent,
                Confidence = request.Confidence,
                Entities = request.Entities?.Select(e => new InteractionEntity;
                {
                    EntityId = e.EntityId,
                    EntityType = e.EntityType,
                    Value = e.Value;
                }).ToList(),
                Metadata = request.Metadata ?? new Dictionary<string, object>()
            };
        }

        private double CalculateEngagementScore(SessionContext session)
        {
            if (session.InteractionCount == 0) return 0;

            var recentInteractions = session.Interactions;
                .Where(i => (DateTime.UtcNow - i.Timestamp).TotalMinutes < 10)
                .ToList();

            if (!recentInteractions.Any()) return 0;

            // Etkileşim sıklığı;
            var frequency = recentInteractions.Count / 10.0; // 10 dakikada etkileşim sayısı;

            // Etkileşim çeşitliliği;
            var uniqueIntents = recentInteractions;
                .Select(i => i.Intent)
                .Distinct()
                .Count();
            var diversity = (double)uniqueIntents / recentInteractions.Count;

            // Etkileşim derinliği;
            var avgEntityCount = recentInteractions;
                .Where(i => i.Entities != null)
                .Average(i => i.Entities?.Count ?? 0);
            var depth = Math.Min(avgEntityCount / 5.0, 1.0); // Maksimum 5 entity;

            // Konu sürekliliği;
            var topicContinuity = CalculateTopicContinuity(session);

            return (frequency * 0.3 + diversity * 0.25 + depth * 0.25 + topicContinuity * 0.2);
        }

        private double CalculateTopicContinuity(SessionContext session)
        {
            if (session.Interactions.Count < 2) return 0;

            var topics = session.ActiveTopics;
            if (topics.Count == 0) return 0;

            // Son 5 etkileşimde aynı konuların devam etme oranı;
            var recentInteractions = session.Interactions.TakeLast(5).ToList();
            var topicMatches = 0;

            for (int i = 1; i < recentInteractions.Count; i++)
            {
                var prev = recentInteractions[i - 1];
                var current = recentInteractions[i];

                // Basit konu eşleştirme (gerçek implementasyonda NLP kullanılır)
                if (prev.Intent == current.Intent ||
                    (prev.Entities?.Any() == true && current.Entities?.Any() == true &&
                     prev.Entities.Select(e => e.EntityType).Intersect(
                         current.Entities.Select(e => e.EntityType)).Any()))
                {
                    topicMatches++;
                }
            }

            return (double)topicMatches / (recentInteractions.Count - 1);
        }

        private double CalculateContextCompleteness(
            UserContext? userContext,
            SessionContext? sessionContext,
            ConversationContext? conversationContext)
        {
            var completeness = 0.0;

            if (userContext != null) completeness += 0.4;
            if (sessionContext != null) completeness += 0.3;
            if (conversationContext != null) completeness += 0.3;

            return completeness;
        }

        private double CalculateContextFreshness(DateTime? lastUserUpdate, DateTime? sessionStart)
        {
            var now = DateTime.UtcNow;
            var freshness = 0.0;

            // Kullanıcı bağlamı tazeliği;
            if (lastUserUpdate.HasValue)
            {
                var userAge = (now - lastUserUpdate.Value).TotalMinutes;
                freshness += Math.Max(0, 1.0 - (userAge / 60.0)) * 0.6; // 1 saat içinde taze;
            }

            // Oturum tazeliği;
            if (sessionStart.HasValue)
            {
                var sessionAge = (now - sessionStart.Value).TotalMinutes;
                freshness += Math.Max(0, 1.0 - (sessionAge / 30.0)) * 0.4; // 30 dakika içinde taze;
            }

            return freshness;
        }

        private double CalculateInteractionIntensity(SessionContext? sessionContext)
        {
            if (sessionContext == null || sessionContext.InteractionCount == 0) return 0;

            var duration = sessionContext.Duration.TotalMinutes;
            if (duration < 1) duration = 1; // Minimum 1 dakika;

            var interactionsPerMinute = sessionContext.InteractionCount / duration;

            // Normalize et: 0-2 etkileşim/dakika normal, 2+ yoğun;
            return Math.Min(interactionsPerMinute / 2.0, 1.0);
        }

        private double CalculateIntegrationScore(List<ContextComponent> components)
        {
            if (components.Count == 0) return 0;

            // Bileşen çeşitliliği;
            var uniqueTypes = components.Select(c => c.Type).Distinct().Count();
            var typeDiversity = (double)uniqueTypes / components.Count;

            // Ağırlık dağılımı;
            var weightVariance = CalculateWeightVariance(components);

            // Bileşen kalitesi (basit bir tahmin)
            var avgQuality = components.Average(c => c.Weight);

            return (typeDiversity * 0.4 + (1 - weightVariance) * 0.3 + avgQuality * 0.3);
        }

        private double CalculateWeightVariance(List<ContextComponent> components)
        {
            if (components.Count < 2) return 0;

            var mean = components.Average(c => c.Weight);
            var variance = components.Sum(c => Math.Pow(c.Weight - mean, 2)) / components.Count;

            return variance;
        }

        private EmotionalState MergeEmotionalStates(EmotionalState current, EmotionalState update)
        {
            // Duygusal durumları birleştir (ağırlıklı ortalama)
            var merged = new EmotionalState;
            {
                Happiness = (current.Happiness * 0.7 + update.Happiness * 0.3),
                Sadness = (current.Sadness * 0.7 + update.Sadness * 0.3),
                Anger = (current.Anger * 0.7 + update.Anger * 0.3),
                Fear = (current.Fear * 0.7 + update.Fear * 0.3),
                Surprise = (current.Surprise * 0.7 + update.Surprise * 0.3),
                Disgust = (current.Disgust * 0.7 + update.Disgust * 0.3),
                Trust = (current.Trust * 0.7 + update.Trust * 0.3),
                Anticipation = (current.Anticipation * 0.7 + update.Anticipation * 0.3),
                LastUpdate = DateTime.UtcNow,
                Confidence = (current.Confidence + update.Confidence) / 2;
            };

            // Değer ve uyarılmayı hesapla;
            merged.Valence = CalculateValence(merged);
            merged.Arousal = CalculateArousal(merged);

            return merged;
        }

        private EmotionalValence CalculateValence(EmotionalState state)
        {
            var positive = state.Happiness + state.Trust + state.Anticipation;
            var negative = state.Sadness + state.Anger + state.Fear + state.Disgust;

            var netValence = positive - negative;

            return netValence switch;
            {
                > 0.5 => EmotionalValence.VeryPositive,
                > 0.2 => EmotionalValence.Positive,
                < -0.5 => EmotionalValence.VeryNegative,
                < -0.2 => EmotionalValence.Negative,
                _ => EmotionalValence.Neutral;
            };
        }

        private EmotionalArousal CalculateArousal(EmotionalState state)
        {
            var arousal = (state.Anger + state.Fear + state.Surprise + state.Anticipation) / 4.0;

            return arousal switch;
            {
                > 0.7 => EmotionalArousal.Intense,
                > 0.5 => EmotionalArousal.High,
                > 0.3 => EmotionalArousal.Moderate,
                > 0.1 => EmotionalArousal.Mild,
                _ => EmotionalArousal.Calm;
            };
        }

        private async Task ApplyPreferenceUpdatesAsync(
            UserContext userContext,
            Dictionary<string, object> updates,
            CancellationToken cancellationToken)
        {
            foreach (var update in updates)
            {
                switch (update.Key.ToLower())
                {
                    case "language":
                        userContext.Preferences.Language = update.Value.ToString() ?? "en";
                        break;
                    case "style":
                        if (Enum.TryParse<CommunicationStyle>(update.Value.ToString(), out var style))
                            userContext.Preferences.Style = style;
                        break;
                    case "detaillevel":
                        if (Enum.TryParse<DetailLevel>(update.Value.ToString(), out var detail))
                            userContext.Preferences.DetailPreference = detail;
                        break;
                    default:
                        userContext.Preferences.CustomPreferences[update.Key] = update.Value;
                        break;
                }
            }

            // Tercihleri uzun süreli belleğe kaydet;
            await _longTermMemory.StoreUserPreferencesAsync(
                userContext.UserId, userContext.Preferences, cancellationToken);
        }

        private void UpdateBehaviorPatterns(UserContext userContext, BehaviorUpdate update)
        {
            userContext.Behavior.LastPatternUpdate = DateTime.UtcNow;

            // Davranış pattern'lerini güncelle;
            // Gerçek implementasyonda detaylı pattern analizi yapılır;

            // Pattern güvenilirliğini güncelle;
            userContext.Behavior.PatternConfidence = Math.Min(
                userContext.Behavior.PatternConfidence + 0.05, 1.0);
        }

        private async Task UpdateConversationContextAsync(
            UserContext userContext,
            SessionContext session,
            ContextUpdateRequest request,
            CancellationToken cancellationToken)
        {
            var conversationId = GetOrCreateConversationId(session.SessionId);

            if (!_conversationContexts.TryGetValue(conversationId, out var conversation))
            {
                conversation = new ConversationContext;
                {
                    ConversationId = conversationId,
                    SessionId = session.SessionId,
                    StartTime = DateTime.UtcNow,
                    Status = ConversationStatus.Active,
                    Flow = new ConversationFlow(),
                    Topics = new List<string>(),
                    ContextVariables = new Dictionary<string, object>(),
                    EmotionalTone = new EmotionalTone(),
                    Complexity = ComplexityLevel.Medium,
                    Goals = new GoalState()
                };

                _conversationContexts[conversationId] = conversation;

                // Event tetikle;
                ConversationStateChanged?.Invoke(this,
                    new ConversationStateChangedEventArgs(
                        conversationId, session.SessionId, ConversationEventType.Started));
            }

            // Konuşma turu ekle;
            var turn = new ConversationTurn;
            {
                TurnId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                Speaker = request.Speaker ?? "user",
                Content = request.Content ?? string.Empty,
                Intent = request.Intent,
                Entities = request.Entities?.Select(e => new ConversationEntity;
                {
                    EntityId = e.EntityId,
                    EntityType = e.EntityType,
                    Value = e.Value;
                }).ToList(),
                EmotionalTone = request.EmotionalState != null ?
                    MapToEmotionalTone(request.EmotionalState) : new EmotionalTone(),
                Metadata = request.Metadata ?? new Dictionary<string, object>()
            };

            conversation.Turns.Add(turn);

            // Konuşma durumunu güncelle;
            conversation.Status = ConversationStatus.Active;
            conversation.CoherenceScore = CalculateCoherenceScore(conversation);
            conversation.ProgressTowardsGoal = CalculateGoalProgress(conversation);

            // Konuşma geçmişine kaydet;
            await _conversationHistory.AddTurnAsync(conversationId, turn, cancellationToken);

            // Konuşma timeout kontrolü;
            if (conversation.Turns.Count > 0)
            {
                var lastTurnTime = conversation.Turns.Last().Timestamp;
                if (DateTime.UtcNow - lastTurnTime > _configuration.ConversationTimeout)
                {
                    conversation.Status = ConversationStatus.Paused;

                    ConversationStateChanged?.Invoke(this,
                        new ConversationStateChangedEventArgs(
                            conversationId, session.SessionId, ConversationEventType.Paused));
                }
            }
        }

        private string GetOrCreateConversationId(string sessionId)
        {
            // Bu oturum için aktif konuşma var mı kontrol et;
            var activeConversation = _conversationContexts.Values;
                .FirstOrDefault(c => c.SessionId == sessionId && c.Status == ConversationStatus.Active);

            return activeConversation?.ConversationId ?? GenerateConversationId();
        }

        private double CalculateCoherenceScore(ConversationContext conversation)
        {
            if (conversation.Turns.Count < 2) return 1.0;

            var turns = conversation.Turns;
            var coherence = 0.0;
            var coherenceFactors = 0;

            for (int i = 1; i < turns.Count; i++)
            {
                var prev = turns[i - 1];
                var current = turns[i];

                // Intent devamlılığı;
                if (!string.IsNullOrEmpty(prev.Intent) && !string.IsNullOrEmpty(current.Intent))
                {
                    if (prev.Intent == current.Intent) coherence += 0.3;
                    coherenceFactors++;
                }

                // Entity devamlılığı;
                if (prev.Entities?.Any() == true && current.Entities?.Any() == true)
                {
                    var commonEntities = prev.Entities.Select(e => e.EntityType)
                        .Intersect(current.Entities.Select(e => e.EntityType))
                        .Count();

                    if (commonEntities > 0) coherence += 0.2;
                    coherenceFactors++;
                }

                // Konu devamlılığı;
                if (prev.Content.ContainsAny(conversation.Topics) &&
                    current.Content.ContainsAny(conversation.Topics))
                {
                    coherence += 0.2;
                    coherenceFactors++;
                }

                // Duygusal tutarlılık;
                var emotionalShift = CalculateEmotionalShift(prev.EmotionalTone, current.EmotionalTone);
                coherence += (1.0 - emotionalShift) * 0.3;
                coherenceFactors++;
            }

            return coherenceFactors > 0 ? coherence / coherenceFactors : 1.0;
        }

        private double CalculateEmotionalShift(EmotionalTone prev, EmotionalTone current)
        {
            // Duygusal ton değişimini hesapla;
            var shift = Math.Abs(prev.Valence - current.Valence) * 0.5 +
                       Math.Abs(prev.Arousal - current.Arousal) * 0.5;

            return Math.Min(shift, 1.0);
        }

        private double CalculateGoalProgress(ConversationContext conversation)
        {
            // Basit hedef ilerleme hesaplama;
            // Gerçek implementasyonda hedef takip sistemi kullanılır;

            if (conversation.Goals == null || !conversation.Goals.Objectives.Any()) return 0;

            var completedObjectives = conversation.Goals.Objectives;
                .Count(o => o.Status == ObjectiveStatus.Completed);

            return (double)completedObjectives / conversation.Goals.Objectives.Count;
        }

        private EmotionalTone MapToEmotionalTone(EmotionalState emotionalState)
        {
            return new EmotionalTone;
            {
                Valence = (int)emotionalState.Valence,
                Arousal = (int)emotionalState.Arousal,
                PrimaryEmotion = emotionalState.PrimaryEmotion,
                Confidence = emotionalState.Confidence;
            };
        }

        private async Task<UserContext> CreateDefaultUserContextAsync(string userId, CancellationToken cancellationToken)
        {
            var userContext = new UserContext;
            {
                UserId = userId,
                SessionId = string.Empty,
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                Preferences = new UserPreferences(),
                Profile = new UserProfile(),
                Behavior = new BehaviorPattern(),
                CurrentState = new ContextState(),
                EmotionalState = new EmotionalState(),
                CognitiveLoad = new CognitiveLoad(),
                Engagement = EngagementLevel.Medium;
            };

            await LoadUserProfileAsync(userContext, cancellationToken);

            _userContexts[userId] = userContext;

            return userContext;
        }

        private async Task<PredictiveContext> GeneratePredictiveContextAsync(
            string userId, string? sessionId, CancellationToken cancellationToken)
        {
            // Tahmini bağlam oluştur;
            // Gerçek implementasyonda ML modelleri kullanılır;

            return new PredictiveContext;
            {
                GeneratedAt = DateTime.UtcNow,
                NextLikelyIntent = await PredictNextIntentAsync(userId, cancellationToken),
                ExpectedBehavior = await PredictExpectedBehaviorAsync(userId, cancellationToken),
                PotentialTopics = await PredictPotentialTopicsAsync(userId, cancellationToken),
                RiskFactors = await AssessRiskFactorsAsync(userId, cancellationToken),
                Confidence = 0.7 // Varsayılan güvenilirlik;
            };
        }

        private async Task<MultimodalContext> BuildMultimodalContextAsync(
            string userId, string sessionId, CancellationToken cancellationToken)
        {
            // Çoklu modal bağlam oluştur;
            // Gerçek implementasyonda farklı modalitelerden veri toplanır;

            return new MultimodalContext;
            {
                Modalities = new List<ModalityContext>
                {
                    new() { Type = "text", Confidence = 0.9, Data = new { source = "chat" } },
                    new() { Type = "voice", Confidence = 0.8, Data = new { tone = "neutral" } },
                    new() { Type = "behavioral", Confidence = 0.7, Data = new { engagement = "medium" } }
                },
                FusionMethod = "weighted_average",
                OverallConfidence = 0.8;
            };
        }

        private async Task<ExternalContext> GetExternalContextAsync(CancellationToken cancellationToken)
        {
            // Harici bağlam bilgileri;
            // Gerçek implementasyonda API çağrıları yapılır;

            return new ExternalContext;
            {
                Weather = await GetWeatherAsync(cancellationToken),
                News = await GetRelevantNewsAsync(cancellationToken),
                MarketConditions = await GetMarketConditionsAsync(cancellationToken),
                SocialTrends = await GetSocialTrendsAsync(cancellationToken),
                Timestamp = DateTime.UtcNow;
            };
        }

        private Dictionary<string, object> ExtractUserContextData(UserContext userContext)
        {
            return new Dictionary<string, object>
            {
                { "preferences", userContext.Preferences },
                { "profile_summary", new {
                    completeness = userContext.Profile.ProfileCompleteness,
                    expertise = userContext.Profile.Expertise?.Level;
                }},
                { "recent_activity", new {
                    interaction_count = userContext.InteractionCount,
                    last_activity = userContext.LastActivity;
                }},
                { "current_state", new {
                    intent = userContext.CurrentState.CurrentIntent,
                    domain = userContext.CurrentState.CurrentDomain,
                    attention = userContext.CurrentState.Attention;
                }}
            };
        }

        private List<ContextWindow> BuildContextWindows(UserContext? userContext)
        {
            var windows = new List<ContextWindow>();

            if (userContext == null) return windows;

            windows.Add(new ContextWindow;
            {
                Type = "short_term",
                Size = userContext.ShortTermWindow.Size,
                Items = userContext.ShortTermWindow.Items.Take(5).ToList(),
                Relevance = 0.9;
            });

            windows.Add(new ContextWindow;
            {
                Type = "medium_term",
                Size = userContext.MediumTermWindow.Size,
                Items = userContext.MediumTermWindow.Items.Take(10).ToList(),
                Relevance = 0.7;
            });

            windows.Add(new ContextWindow;
            {
                Type = "long_term",
                Size = userContext.LongTermWindow.Size,
                Items = userContext.LongTermWindow.Items.Take(20).ToList(),
                Relevance = 0.5;
            });

            return windows;
        }

        private ContextQuality CalculateContextQuality(
            UserContext? userContext,
            SessionContext? sessionContext,
            ConversationContext? conversationContext)
        {
            return new ContextQuality;
            {
                Completeness = CalculateContextCompleteness(userContext, sessionContext, conversationContext),
                Coherence = conversationContext?.CoherenceScore ?? 0.8,
                Freshness = CalculateContextFreshness(userContext?.LastUpdated, sessionContext?.StartTime),
                Relevance = 0.7, // Varsayılan;
                Consistency = CalculateContextConsistency(userContext, sessionContext),
                Timestamp = DateTime.UtcNow;
            };
        }

        private double CalculateContextConsistency(UserContext? userContext, SessionContext? sessionContext)
        {
            // Bağlam tutarlılığını hesapla;
            var consistency = 0.0;

            if (userContext != null && sessionContext != null)
            {
                // Kullanıcı tercihleri ile oturum davranışı tutarlı mı?
                var styleMatch = sessionContext.Interactions.Any() ? 0.3 : 0;

                // Duygusal durum tutarlılığı;
                var emotionalConsistency = userContext.EmotionalState.Confidence * 0.4;

                // Davranış pattern tutarlılığı;
                var behaviorConsistency = userContext.Behavior.PatternConfidence * 0.3;

                consistency = styleMatch + emotionalConsistency + behaviorConsistency;
            }

            return consistency;
        }

        private List<ContextTrend> AnalyzeContextTrends(List<ContextSnapshot> history)
        {
            var trends = new List<ContextTrend>();

            if (history.Count < 2) return trends;

            // Duygusal trend;
            var emotionalValues = history.Select(h => h.EmotionalState?.Valence ?? 0).ToList();
            var emotionalTrend = CalculateTrend(emotionalValues);
            if (Math.Abs(emotionalTrend) > 0.1)
            {
                trends.Add(new ContextTrend;
                {
                    Type = "emotional",
                    Direction = emotionalTrend > 0 ? TrendDirection.Increasing : TrendDirection.Decreasing,
                    Strength = Math.Abs(emotionalTrend),
                    Description = emotionalTrend > 0 ? "Becoming more positive" : "Becoming more negative"
                });
            }

            // Etkileşim sıklığı trendi;
            var interactionTimes = history.Select(h => h.Timestamp).ToList();
            var frequencyTrend = CalculateFrequencyTrend(interactionTimes);
            if (Math.Abs(frequencyTrend) > 0.2)
            {
                trends.Add(new ContextTrend;
                {
                    Type = "interaction_frequency",
                    Direction = frequencyTrend > 0 ? TrendDirection.Increasing : TrendDirection.Decreasing,
                    Strength = Math.Abs(frequencyTrend),
                    Description = frequencyTrend > 0 ? "Interactions becoming more frequent" : "Interactions slowing down"
                });
            }

            return trends;
        }

        private double CalculateTrend(List<int> values)
        {
            if (values.Count < 2) return 0;

            // Basit lineer trend hesaplama;
            var xValues = Enumerable.Range(0, values.Count).Select(x => (double)x).ToArray();
            var yValues = values.Select(v => (double)v).ToArray();

            var xMean = xValues.Average();
            var yMean = yValues.Average();

            var numerator = xValues.Zip(yValues, (x, y) => (x - xMean) * (y - yMean)).Sum();
            var denominator = xValues.Sum(x => Math.Pow(x - xMean, 2));

            return denominator == 0 ? 0 : numerator / denominator;
        }

        private double CalculateFrequencyTrend(List<DateTime> timestamps)
        {
            if (timestamps.Count < 3) return 0;

            var intervals = new List<double>();
            for (int i = 1; i < timestamps.Count; i++)
            {
                intervals.Add((timestamps[i] - timestamps[i - 1]).TotalMinutes);
            }

            // Son üç interval'ın ortalamasını al;
            var recentAvg = intervals.TakeLast(3).Average();
            var overallAvg = intervals.Average();

            return overallAvg == 0 ? 0 : (overallAvg - recentAvg) / overallAvg;
        }

        private void UpdateStatistics(
            bool sessionStarted = false,
            bool sessionEnded = false,
            bool contextUpdated = false)
        {
            lock (_statsLock)
            {
                if (sessionStarted) _statistics.ActiveSessions++;
                if (sessionEnded) _statistics.ActiveSessions = Math.Max(0, _statistics.ActiveSessions - 1);
                if (contextUpdated) _statistics.ContextUpdateCount++;
            }
        }

        private void RecordMetrics(string operation, double durationMs, Dictionary<string, string> tags)
        {
            _metricsCollector.RecordMetric($"context_{operation}_duration", durationMs, tags);
            _metricsCollector.RecordMetric($"context_{operation}_count", 1, tags);
        }

        // Simülasyon metodları (gerçek implementasyonda detaylı implementasyon yapılır)
        private async Task<string?> PredictNextIntentAsync(string userId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return "continue_conversation";
        }

        private async Task<ExpectedBehavior> PredictExpectedBehaviorAsync(string userId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new ExpectedBehavior;
            {
                LikelyActions = new List<string> { "ask_question", "request_details" },
                Probability = 0.65,
                Timeframe = TimeSpan.FromMinutes(5)
            };
        }

        private async Task<List<string>> PredictPotentialTopicsAsync(string userId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new List<string> { "technology", "productivity", "learning" };
        }

        private async Task<List<RiskFactor>> AssessRiskFactorsAsync(string userId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new List<RiskFactor>
            {
                new() { Factor = "engagement_drop", Severity = RiskSeverity.Low, Probability = 0.3 }
            };
        }

        private async Task<WeatherInfo> GetWeatherAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new WeatherInfo;
            {
                Condition = "clear",
                Temperature = 22.5,
                Location = "Unknown"
            };
        }

        private async Task<List<NewsItem>> GetRelevantNewsAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new List<NewsItem>();
        }

        private async Task<MarketConditions> GetMarketConditionsAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new MarketConditions { Status = "stable" };
        }

        private async Task<List<SocialTrend>> GetSocialTrendsAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new List<SocialTrend>();
        }

        private async Task<PredictiveInsights> GeneratePredictiveInsightsAsync(string userId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new PredictiveInsights;
            {
                UserId = userId,
                GeneratedAt = DateTime.UtcNow,
                Insights = new List<Insight>()
            };
        }

        private async Task<BehaviorPredictions> PredictUserBehaviorAsync(string userId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new BehaviorPredictions;
            {
                UserId = userId,
                Predictions = new List<BehaviorPrediction>()
            };
        }

        private async Task<List<ContextSnapshot>> GetHistoricalContextAsync(string userId, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return new List<ContextSnapshot>();
        }

        private async Task<ConversationContext> EnrichConversationContextAsync(
            ConversationContext conversation,
            ConversationHistory? history,
            CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            return conversation;
        }

        ~ContextBuilder()
        {
            Dispose(false);
        }
    }

    // Enum'lar, sınıflar ve interface'ler devam ediyor...
    // (Kod uzunluğu nedeniyle kısaltıldı, tam versiyonda tüm yardımcı sınıflar mevcut)

    public enum SessionStatus { Active, Paused, Ended, Terminated }
    public enum ConversationStatus { Active, Paused, Ended, Archived }
    public enum EventSource { User, System, External, Sensor }
    public enum EventImpact { Low, Medium, High, Critical }
    public enum TrendDirection { Increasing, Decreasing, Stable, Volatile }
    public enum RiskSeverity { None, Low, Medium, High, Critical }
    public enum ComplexityLevel { Simple, Low, Medium, High, Complex }
    public enum ObjectiveStatus { NotStarted, InProgress, Completed, Failed, Blocked }

    public class ContextUpdateRequest;
    {
        public string UserId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string? Intent { get; set; }
        public double Confidence { get; set; } = 1.0;
        public string? Domain { get; set; }
        public List<ContextEntity>? Entities { get; set; }
        public Dictionary<string, object>? Variables { get; set; }
        public EmotionalState? EmotionalState { get; set; }
        public CognitiveLoad? CognitiveLoad { get; set; }
        public EngagementLevel? EngagementLevel { get; set; }
        public ContextEvent? Event { get; set; }
        public string? InteractionType { get; set; }
        public string? Content { get; set; }
        public Dictionary<string, object>? PreferenceUpdates { get; set; }
        public BehaviorUpdate? BehaviorUpdate { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
        public string? Speaker { get; set; }
    }

    public class SessionStartRequest;
    {
        public string UserId { get; set; } = string.Empty;
        public SessionMetadata? Metadata { get; set; }
        public DeviceInfo? DeviceInfo { get; set; }
        public LocationInfo? LocationInfo { get; set; }
        public EnvironmentInfo? EnvironmentInfo { get; set; }
    }

    public class RichContext;
    {
        public DateTime Timestamp { get; set; }
        public UserContext? UserContext { get; set; }
        public SessionContext? SessionContext { get; set; }
        public ConversationContext? ConversationContext { get; set; }
        public string? CurrentIntent { get; set; }
        public IntegratedContext IntegratedContext { get; set; } = new();
        public Dictionary<string, object> ContextFeatures { get; set; } = new();
        public PredictiveContext? PredictiveContext { get; set; }
        public MultimodalContext? MultimodalContext { get; set; }
        public ContextQuality QualityMetrics { get; set; } = new();
        public double RelevanceScore { get; set; }
        public List<ContextSnapshot> HistoricalContext { get; set; } = new();
        public List<ContextWindow> ContextWindows { get; set; } = new();
    }

    public class IntegratedContext;
    {
        public DateTime Timestamp { get; set; }
        public List<ContextComponent> Components { get; set; } = new();
        public int TotalComponents { get; set; }
        public double IntegrationScore { get; set; }
    }

    public class ContextComponent;
    {
        public string Type { get; set; } = string.Empty;
        public double Weight { get; set; }
        public double NormalizedWeight { get; set; }
        public object Data { get; set; } = new();
    }

    public class PredictiveContext;
    {
        public DateTime GeneratedAt { get; set; }
        public string? NextLikelyIntent { get; set; }
        public ExpectedBehavior? ExpectedBehavior { get; set; }
        public List<string> PotentialTopics { get; set; } = new();
        public List<RiskFactor> RiskFactors { get; set; } = new();
        public double Confidence { get; set; }
    }

    public class MultimodalContext;
    {
        public List<ModalityContext> Modalities { get; set; } = new();
        public string FusionMethod { get; set; } = string.Empty;
        public double OverallConfidence { get; set; }
    }

    public class ExternalContext;
    {
        public WeatherInfo? Weather { get; set; }
        public List<NewsItem> News { get; set; } = new();
        public MarketConditions? MarketConditions { get; set; }
        public List<SocialTrend> SocialTrends { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }

    public class ContextQuality;
    {
        public double Completeness { get; set; }
        public double Coherence { get; set; }
        public double Freshness { get; set; }
        public double Relevance { get; set; }
        public double Consistency { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ContextWindow;
    {
        public string Type { get; set; } = string.Empty;
        public int Size { get; set; }
        public List<object> Items { get; set; } = new();
        public double Relevance { get; set; }
    }

    public class ContextTrend;
    {
        public string Type { get; set; } = string.Empty;
        public TrendDirection Direction { get; set; }
        public double Strength { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    public class RollingContextWindow;
    {
        private readonly int _size;
        private readonly List<object> _items;

        public RollingContextWindow(int size)
        {
            _size = size;
            _items = new List<object>(size);
        }

        public int Size => _size;
        public List<object> Items => new List<object>(_items);

        public void Add(object item)
        {
            _items.Add(item);
            if (_items.Count > _size)
            {
                _items.RemoveAt(0);
            }
        }
    }

    public interface IContextBuilder : IDisposable
    {
        Task<SessionContextResult> StartSessionAsync(
            SessionStartRequest request,
            CancellationToken cancellationToken = default);

        Task<SessionContextResult> EndSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default);

        Task<ContextUpdateResult> UpdateContextAsync(
            ContextUpdateRequest request,
            CancellationToken cancellationToken = default);

        Task<RichContext> BuildRichContextAsync(
            string userId,
            string sessionId,
            string? currentIntent = null,
            CancellationToken cancellationToken = default);

        Task<ConversationContextResult> GetConversationContextAsync(
            string conversationId,
            CancellationToken cancellationToken = default);

        Task<UserContextResult> GetUserContextAsync(
            string userId,
            bool includePredictive = false,
            CancellationToken cancellationToken = default);

        Task<ContextHistoryResult> GetContextHistoryAsync(
            string userId,
            TimeRange timeRange,
            int maxItems = 100,
            CancellationToken cancellationToken = default);

        ContextBuilderStatistics GetStatistics();

        event EventHandler<ContextUpdatedEventArgs>? ContextUpdated;
        event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
        event EventHandler<ConversationStateChangedEventArgs>? ConversationStateChanged;
    }

    public interface IShortTermMemory;
    {
        Task StoreAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken);
        Task<T?> RetrieveAsync<T>(string key, CancellationToken cancellationToken);
        Task<List<T>> RetrieveRangeAsync<T>(string prefix, DateTime start, DateTime end, CancellationToken cancellationToken);
    }

    public interface ILongTermMemory;
    {
        Task<UserProfile?> RetrieveUserProfileAsync(string userId, CancellationToken cancellationToken);
        Task<BehaviorPattern?> RetrieveBehaviorPatternsAsync(string userId, CancellationToken cancellationToken);
        Task StoreSessionAsync(SessionSummary session, CancellationToken cancellationToken);
        Task StoreUserPreferencesAsync(string userId, UserPreferences preferences, CancellationToken cancellationToken);
        Task<List<ContextSnapshot>> RetrieveContextHistoryAsync(string userId, DateTime start, DateTime end, int maxItems, CancellationToken cancellationToken);
    }

    public interface IConversationHistory;
    {
        Task<ConversationHistory?> GetConversationAsync(string conversationId, CancellationToken cancellationToken);
        Task AddTurnAsync(string conversationId, ConversationTurn turn, CancellationToken cancellationToken);
    }

    // Sonuç sınıfları;
    public class SessionContextResult;
    {
        public bool IsSuccess { get; }
        public string SessionId { get; }
        public UserContext? UserContext { get; }
        public SessionContext? SessionContext { get; }
        public string? ErrorMessage { get; }

        private SessionContextResult(bool isSuccess, string sessionId, UserContext? userContext,
                                   SessionContext? sessionContext, string? errorMessage)
        {
            IsSuccess = isSuccess;
            SessionId = sessionId;
            UserContext = userContext;
            SessionContext = sessionContext;
            ErrorMessage = errorMessage;
        }

        public static SessionContextResult Success(string sessionId, UserContext userContext, SessionContext sessionContext)
            => new SessionContextResult(true, sessionId, userContext, sessionContext, null);

        public static SessionContextResult Failure(string errorMessage)
            => new SessionContextResult(false, string.Empty, null, null, errorMessage);
    }

    public class ContextUpdateResult;
    {
        public bool IsSuccess { get; }
        public UserContext? UserContext { get; }
        public SessionContext? SessionContext { get; }
        public Dictionary<string, object> UpdatesApplied { get; }
        public string? ErrorMessage { get; }

        private ContextUpdateResult(bool isSuccess, UserContext? userContext, SessionContext? sessionContext,
                                  Dictionary<string, object> updatesApplied, string? errorMessage)
        {
            IsSuccess = isSuccess;
            UserContext = userContext;
            SessionContext = sessionContext;
            UpdatesApplied = updatesApplied;
            ErrorMessage = errorMessage;
        }

        public static ContextUpdateResult Success(UserContext userContext, SessionContext sessionContext,
            Dictionary<string, object> updatesApplied)
            => new ContextUpdateResult(true, userContext, sessionContext, updatesApplied, null);

        public static ContextUpdateResult Failure(string errorMessage)
            => new ContextUpdateResult(false, null, null, new Dictionary<string, object>(), errorMessage);
    }

    public class ConversationContextResult;
    {
        public bool IsSuccess { get; }
        public string ConversationId { get; }
        public ConversationContext? Context { get; }
        public ConversationHistory? History { get; }
        public string? ErrorMessage { get; }

        private ConversationContextResult(bool isSuccess, string conversationId, ConversationContext? context,
                                        ConversationHistory? history, string? errorMessage)
        {
            IsSuccess = isSuccess;
            ConversationId = conversationId;
            Context = context;
            History = history;
            ErrorMessage = errorMessage;
        }

        public static ConversationContextResult Success(string conversationId, ConversationContext context,
            ConversationHistory history)
            => new ConversationContextResult(true, conversationId, context, history, null);

        public static ConversationContextResult Failure(string errorMessage)
            => new ConversationContextResult(false, string.Empty, null, null, errorMessage);
    }

    public class UserContextResult;
    {
        public string UserId { get; set; } = string.Empty;
        public UserContext? Context { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastActivity { get; set; }
        public EngagementLevel EngagementLevel { get; set; }
        public PredictiveInsights? PredictiveInsights { get; set; }
        public BehaviorPredictions? BehaviorPredictions { get; set; }
    }

    public class ContextHistoryResult;
    {
        public string UserId { get; set; } = string.Empty;
        public List<ContextSnapshot> History { get; set; } = new();
        public List<ContextTrend> Trends { get; set; } = new();
        public TimeRange TimeRange { get; set; } = new();
        public int TotalItems { get; set; }
    }

    // Event argümanları;
    public class ContextUpdatedEventArgs : EventArgs;
    {
        public string UserId { get; }
        public string SessionId { get; }
        public Dictionary<string, object> UpdatesApplied { get; }
        public DateTime Timestamp { get; }

        public ContextUpdatedEventArgs(string userId, string sessionId,
                                     Dictionary<string, object> updatesApplied, DateTime timestamp)
        {
            UserId = userId;
            SessionId = sessionId;
            UpdatesApplied = updatesApplied;
            Timestamp = timestamp;
        }
    }

    public class SessionStateChangedEventArgs : EventArgs;
    {
        public string SessionId { get; }
        public string UserId { get; }
        public SessionEventType EventType { get; }
        public DateTime Timestamp { get; }

        public SessionStateChangedEventArgs(string sessionId, string userId, SessionEventType eventType)
        {
            SessionId = sessionId;
            UserId = userId;
            EventType = eventType;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class ConversationStateChangedEventArgs : EventArgs;
    {
        public string ConversationId { get; }
        public string SessionId { get; }
        public ConversationEventType EventType { get; }
        public DateTime Timestamp { get; }

        public ConversationStateChangedEventArgs(string conversationId, string sessionId,
                                               ConversationEventType eventType)
        {
            ConversationId = conversationId;
            SessionId = sessionId;
            EventType = eventType;
            Timestamp = DateTime.UtcNow;
        }
    }

    public enum SessionEventType { Started, Ended, Paused, Resumed, Terminated }
    public enum ConversationEventType { Started, Ended, Paused, Resumed, TopicChanged }

    // İstatistikler;
    public class ContextBuilderStatistics;
    {
        public int ActiveUsers { get; set; }
        public int ActiveSessions { get; set; }
        public int ActiveConversations { get; set; }
        public long TotalInteractions { get; set; }
        public TimeSpan AverageSessionDuration { get; set; }
        public double AverageEngagementScore { get; set; }
        public long ContextUpdateCount { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    // Exception;
    public class ContextBuildingException : Exception
    {
        public ContextBuildingException(string message) : base(message) { }
        public ContextBuildingException(string message, Exception innerException) : base(message, innerException) { }
    }

    // Diğer yardımcı sınıflar;
    public class TimeRange;
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public TimeRange()
        {
            Start = DateTime.UtcNow.AddHours(-24);
            End = DateTime.UtcNow;
        }

        public TimeRange(DateTime start, DateTime end)
        {
            Start = start;
            End = end;
        }
    }

    // Extension metodlar;
    internal static class ContextBuilderExtensions;
    {
        public static bool ContainsAny(this string text, List<string> keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null || !keywords.Any())
                return false;

            return keywords.Any(keyword =>
                text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
