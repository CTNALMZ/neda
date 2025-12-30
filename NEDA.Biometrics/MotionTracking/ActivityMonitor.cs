using NEDA.Animation.SequenceEditor.CinematicDirector;
using NEDA.API.Middleware;
using NEDA.API.Versioning;
using NEDA.Automation.WorkflowEngine.WorkflowDesigner;
using NEDA.Biometrics.FaceRecognition;
using NEDA.Common;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.NeuralNetwork.PatternRecognition.AnomalyDetection;
using NEDA.Services.Messaging.EventBus;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;
using static System.Collections.Specialized.BitVector32;

namespace NEDA.MotionTracking;
{
    /// <summary>
    /// Aktivite İzleme Sistemi - Kullanıcı aktivitelerini izler ve analiz eder;
    /// Özellikler: Gerçek zamanlı izleme, pattern tanıma, anomali tespiti, performans optimizasyonu;
    /// Tasarım desenleri: Observer, Strategy, Decorator, Factory Method, Command;
    /// </summary>
    public class ActivityMonitor : IActivityMonitor, IDisposable;
    {
        #region Fields;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IErrorReporter _errorReporter;
        private readonly IPerformanceMonitor _performanceMonitor;

        private readonly ConcurrentDictionary<string, ActivitySession> _activeSessions;
        private readonly ConcurrentDictionary<string, ActivityPattern> _activityPatterns;
        private readonly ConcurrentQueue<ActivityEvent> _eventQueue;
        private readonly ConcurrentBag<ActivityLog> _activityLogs;
        private readonly ConcurrentDictionary<string, UserActivityProfile> _userProfiles;

        private readonly List<IActivityDetector> _detectors;
        private readonly ActivityAnalyzer _activityAnalyzer;
        private readonly PatternRecognizer _patternRecognizer;
        private readonly AnomalyDetector _anomalyDetector;
        private readonly ActivityClassifier _activityClassifier;
        private readonly PerformanceOptimizer _performanceOptimizer;

        private readonly Subject<ActivityEvent> _activitySubject;
        private readonly Subject<AlertEvent> _alertSubject;
        private readonly Subject<PerformanceMetric> _performanceSubject;

        private readonly Timer _monitoringTimer;
        private readonly Timer _cleanupTimer;
        private readonly Timer _reportingTimer;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ManualResetEventSlim _processingEvent;
        private readonly object _syncLock = new object();

        private ActivityMonitorConfiguration _configuration;
        private ActivityMonitorState _state;
        private bool _isInitialized;
        private bool _isProcessing;
        private DateTime _startTime;
        private long _totalEventsProcessed;
        private long _totalAlertsGenerated;
        private long _totalAnomaliesDetected;

        private readonly Stopwatch _performanceStopwatch;
        private readonly ConcurrentBag<PerformanceMetric> _performanceMetrics;
        private readonly ActivityCache _activityCache;
        private readonly AlertManager _alertManager;

        #endregion;

        #region Properties;

        /// <summary>
        Aktivite izleyici durumu;
        /// </summary>
        public ActivityMonitorState State;
        {
            get => _state;
            private set;
            {
                if (_state != value)
                {
                    _state = value;
                    OnStateChanged();
                }
            }
        }

        /// <summary>
        Konfigürasyon;
        /// </summary>
        public ActivityMonitorConfiguration Configuration;
        {
            get => _configuration;
            set;
            {
                _configuration = value ?? throw new ArgumentNullException(nameof(value));
                UpdateConfiguration();
            }
        }

        /// <summary>
        Başlatıldı mı?
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        İşlemde mi?
        /// </summary>
        public bool IsProcessing => _isProcessing;

        /// <summary>
        Aktif session sayısı;
        /// </summary>
        public int ActiveSessionCount => _activeSessions.Count;

        /// <summary>
        Kayıtlı pattern sayısı;
        /// </summary>
        public int PatternCount => _activityPatterns.Count;

        /// <summary>
        İşlenen toplam event sayısı;
        /// </summary>
        public long TotalEventsProcessed => _totalEventsProcessed;

        /// <summary>
        Oluşturulan toplam alert sayısı;
        /// </summary>
        public long TotalAlertsGenerated => _totalAlertsGenerated;

        /// <summary>
        Tespit edilen toplam anomali sayısı;
        /// </summary>
        public long TotalAnomaliesDetected => _totalAnomaliesDetected;

        /// <summary>
        Çalışma süresi;
        /// </summary>
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;

        /// <summary>
        Event kuyruğu boyutu;
        /// </summary>
        public int QueueSize => _eventQueue.Count;

        /// <summary>
        Aktif kullanıcı profili sayısı;
        /// </summary>
        public int UserProfileCount => _userProfiles.Count;

        /// <summary>
        Performans metrikleri;
        /// </summary>
        public IReadOnlyList<PerformanceMetric> PerformanceMetrics => _performanceMetrics.ToList();

        /// <summary>
        Aktif session'lar;
        /// </summary>
        public IReadOnlyDictionary<string, ActivitySession> ActiveSessions => _activeSessions;

        /// <summary>
        Son aktivite zamanı;
        /// </summary>
        public DateTime LastActivityTime { get; private set; }

        /// <summary>
        Event işleme hızı(events/sn)
        /// </summary>
        public double EventsPerSecond { get; private set; }

        /// <summary>
        Ortalama işleme süresi(ms)
        /// </summary>
        public double AverageProcessingTimeMs { get; private set; }

        /// <summary>
        Bellek kullanımı(MB)
        /// </summary>
        public double MemoryUsageMB => GetMemoryUsageMB();

        #endregion;

        #region Events;

        /// <summary>
        Aktivite tespit edildi event'i;
        /// </summary>
        public event EventHandler<ActivityDetectedEventArgs> ActivityDetected;

        /// <summary>
        Pattern tanındı event'i;
        /// </summary>
        public event EventHandler<PatternRecognizedEventArgs> PatternRecognized;

        /// <summary>
        Anomali tespit edildi event'i;
        /// </summary>
        public event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;

        /// <summary>
        Alert oluşturuldu event'i;
        /// </summary>
        public event EventHandler<AlertGeneratedEventArgs> AlertGenerated;

        /// <summary>
        Session başladı event'i;
        /// </summary>
        public event EventHandler<SessionStartedEventArgs> SessionStarted;

        /// <summary>
        Session sonlandı event'i;
        /// </summary>
        public event EventHandler<SessionEndedEventArgs> SessionEnded;

        /// <summary>
        Performans metrikleri güncellendi event'i;
        /// </summary>
        public event EventHandler<PerformanceMetricsUpdatedEventArgs> PerformanceMetricsUpdated;

        /// <summary>
        Durum değişti event'i;
        /// </summary>
        public event EventHandler<ActivityMonitorStateChangedEventArgs> StateChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// ActivityMonitor constructor;
        /// </summary>
        public ActivityMonitor(
            ILogger logger,
            IEventBus eventBus,
            IErrorReporter errorReporter,
            IPerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            // Concurrent koleksiyonlar;
            _activeSessions = new ConcurrentDictionary<string, ActivitySession>();
            _activityPatterns = new ConcurrentDictionary<string, ActivityPattern>();
            _eventQueue = new ConcurrentQueue<ActivityEvent>();
            _activityLogs = new ConcurrentBag<ActivityLog>();
            _userProfiles = new ConcurrentDictionary<string, UserActivityProfile>();

            // Reactive subjects;
            _activitySubject = new Subject<ActivityEvent>();
            _alertSubject = new Subject<AlertEvent>();
            _performanceSubject = new Subject<PerformanceMetric>();

            // Bileşenleri başlat;
            _activityAnalyzer = new ActivityAnalyzer(logger);
            _patternRecognizer = new PatternRecognizer(logger);
            _anomalyDetector = new AnomalyDetector(logger);
            _activityClassifier = new ActivityClassifier(logger);
            _performanceOptimizer = new PerformanceOptimizer(logger);
            _activityCache = new ActivityCache(logger);
            _alertManager = new AlertManager(logger);

            // Detector'ları başlat;
            _detectors = InitializeDetectors();

            // Timer'lar;
            _monitoringTimer = new Timer(MonitoringCallback, null, Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer = new Timer(CleanupCallback, null, Timeout.Infinite, Timeout.Infinite);
            _reportingTimer = new Timer(ReportingCallback, null, Timeout.Infinite, Timeout.Infinite);

            // İptal token'ı;
            _cancellationTokenSource = new CancellationTokenSource();
            _processingEvent = new ManualResetEventSlim(false);

            // Performans araçları;
            _performanceStopwatch = new Stopwatch();
            _performanceMetrics = new ConcurrentBag<PerformanceMetric>();

            // Varsayılan konfigürasyon;
            _configuration = ActivityMonitorConfiguration.Default;

            State = ActivityMonitorState.Stopped;

            _logger.LogInformation("ActivityMonitor initialized successfully", GetType());
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Aktivite izleyiciyi başlat;
        /// </summary>
        public async Task InitializeAsync(ActivityMonitorConfiguration configuration = null)
        {
            try
            {
                _performanceMonitor.StartOperation("InitializeActivityMonitor");
                _logger.LogDebug("Initializing activity monitor");

                if (_isInitialized)
                {
                    _logger.LogWarning("Activity monitor is already initialized");
                    return;
                }

                // Konfigürasyonu ayarla;
                if (configuration != null)
                {
                    _configuration = configuration;
                }

                // Bileşenleri yapılandır;
                _activityAnalyzer.Configure(_configuration.AnalysisConfig);
                _patternRecognizer.Configure(_configuration.PatternConfig);
                _anomalyDetector.Configure(_configuration.AnomalyConfig);
                _activityClassifier.Configure(_configuration.ClassificationConfig);
                _performanceOptimizer.Configure(_configuration.PerformanceConfig);
                _activityCache.Configure(_configuration.CacheConfig);
                _alertManager.Configure(_configuration.AlertConfig);

                // Detector'ları yapılandır;
                foreach (var detector in _detectors)
                {
                    detector.Configure(_configuration);
                }

                // Pattern'leri yükle;
                await LoadPatternsAsync();

                // Kullanıcı profillerini yükle;
                await LoadUserProfilesAsync();

                // Event işleme thread'ini başlat;
                StartEventProcessing();

                // Timer'ları başlat;
                StartTimers();

                // Reactive stream'leri başlat;
                StartReactiveStreams();

                _isInitialized = true;
                _startTime = DateTime.UtcNow;
                State = ActivityMonitorState.Running;

                _logger.LogInformation($"Activity monitor initialized successfully with {_detectors.Count} detectors");
                _eventBus.Publish(new ActivityMonitorInitializedEvent(this));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize activity monitor");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.InitializationFailed);
                throw new ActivityMonitorException("Failed to initialize activity monitor", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("InitializeActivityMonitor");
            }
        }

        /// <summary>
        /// Aktivite event'i gönder;
        /// </summary>
        public void SendActivityEvent(ActivityEvent activityEvent)
        {
            ValidateInitialized();

            try
            {
                if (activityEvent == null)
                {
                    throw new ArgumentNullException(nameof(activityEvent));
                }

                // Event'i kuyruğa ekle;
                _eventQueue.Enqueue(activityEvent);

                // İşleme event'ini tetikle;
                _processingEvent.Set();

                // Son aktivite zamanını güncelle;
                LastActivityTime = DateTime.UtcNow;

                _logger.LogTrace($"Activity event queued: {activityEvent.ActivityType}, Session: {activityEvent.SessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send activity event");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.SendEventFailed);
                throw new ActivityMonitorException("Failed to send activity event", ex);
            }
        }

        /// <summary>
        /// Batch aktivite event'leri gönder;
        /// </summary>
        public void SendActivityEvents(IEnumerable<ActivityEvent> activityEvents)
        {
            ValidateInitialized();

            try
            {
                if (activityEvents == null)
                {
                    throw new ArgumentNullException(nameof(activityEvents));
                }

                int count = 0;
                foreach (var activityEvent in activityEvents)
                {
                    _eventQueue.Enqueue(activityEvent);
                    count++;
                }

                if (count > 0)
                {
                    _processingEvent.Set();
                    LastActivityTime = DateTime.UtcNow;
                }

                _logger.LogDebug($"Batch activity events queued: {count} events");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send batch activity events");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.SendBatchFailed);
                throw new ActivityMonitorException("Failed to send batch activity events", ex);
            }
        }

        /// <summary>
        /// Yeni session başlat;
        /// </summary>
        public async Task<ActivitySession> StartSessionAsync(
            string sessionId,
            string userId = null,
            SessionContext context = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("StartActivitySession");
                _logger.LogDebug($"Starting activity session: {sessionId}, User: {userId ?? "Anonymous"}");

                // Session var mı kontrol et;
                if (_activeSessions.ContainsKey(sessionId))
                {
                    _logger.LogWarning($"Session already exists: {sessionId}");
                    return _activeSessions[sessionId];
                }

                // Yeni session oluştur;
                var session = new ActivitySession(sessionId, userId)
                {
                    StartTime = DateTime.UtcNow,
                    Status = SessionStatus.Active,
                    Context = context ?? SessionContext.Default,
                    Configuration = _configuration.SessionConfig;
                };

                // Kullanıcı profili yükle;
                if (!string.IsNullOrEmpty(userId))
                {
                    await LoadOrCreateUserProfileAsync(userId, session);
                }

                // Session'a ekle;
                if (!_activeSessions.TryAdd(sessionId, session))
                {
                    throw new InvalidOperationException($"Failed to add session: {sessionId}");
                }

                // Session event'i gönder;
                var sessionEvent = new ActivityEvent;
                {
                    SessionId = sessionId,
                    UserId = userId,
                    ActivityType = ActivityType.SessionStarted,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["SessionId"] = sessionId,
                        ["StartTime"] = session.StartTime,
                        ["Context"] = context?.ToDictionary()
                    }
                };

                SendActivityEvent(sessionEvent);

                // Event tetikle;
                OnSessionStarted(session);

                _logger.LogInformation($"Activity session started: {sessionId}, User: {userId ?? "Anonymous"}");

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start activity session");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.StartSessionFailed);
                throw new ActivityMonitorException("Failed to start activity session", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StartActivitySession");
            }
        }

        /// <summary>
        /// Session'ı sonlandır;
        /// </summary>
        public async Task<SessionResult> EndSessionAsync(
            string sessionId,
            SessionEndReason reason = SessionEndReason.Completed)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("EndActivitySession");
                _logger.LogDebug($"Ending activity session: {sessionId}, Reason: {reason}");

                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    throw new SessionNotFoundException($"Session not found: {sessionId}");
                }

                session.EndTime = DateTime.UtcNow;
                session.Status = SessionStatus.Ended;
                session.EndReason = reason;

                // Session event'i gönder;
                var sessionEvent = new ActivityEvent;
                {
                    SessionId = sessionId,
                    UserId = session.UserId,
                    ActivityType = ActivityType.SessionEnded,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["SessionId"] = sessionId,
                        ["EndTime"] = session.EndTime,
                        ["Duration"] = (session.EndTime - session.StartTime).TotalSeconds,
                        ["Reason"] = reason.ToString()
                    }
                };

                SendActivityEvent(sessionEvent);

                // Session metriklerini kaydet;
                await SaveSessionMetricsAsync(session);

                // Session'ı kaldır;
                _activeSessions.TryRemove(sessionId, out _);

                var result = new SessionResult;
                {
                    SessionId = sessionId,
                    StartTime = session.StartTime,
                    EndTime = session.EndTime,
                    Duration = session.EndTime - session.StartTime,
                    TotalEvents = session.TotalEvents,
                    Status = session.Status,
                    EndReason = reason,
                    Metrics = session.Metrics;
                };

                // Event tetikle;
                OnSessionEnded(result);

                _logger.LogInformation($"Activity session ended: {sessionId}, Duration: {result.Duration.TotalSeconds:F1}s, Events: {session.TotalEvents}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to end activity session");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.EndSessionFailed);
                throw new ActivityMonitorException("Failed to end activity session", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("EndActivitySession");
            }
        }

        /// <summary>
        /// Aktivite pattern'i kaydet;
        /// </summary>
        public async Task<PatternResult> RegisterPatternAsync(ActivityPattern pattern)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("RegisterActivityPattern");
                _logger.LogDebug($"Registering activity pattern: {pattern.Name}");

                if (pattern == null)
                {
                    throw new ArgumentNullException(nameof(pattern));
                }

                // Pattern'i kaydet;
                if (!_activityPatterns.TryAdd(pattern.Id, pattern))
                {
                    throw new InvalidOperationException($"Pattern already exists: {pattern.Id}");
                }

                // Pattern recognizer'a ekle;
                await _patternRecognizer.AddPatternAsync(pattern);

                var result = new PatternResult;
                {
                    PatternId = pattern.Id,
                    PatternName = pattern.Name,
                    IsRegistered = true,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation($"Activity pattern registered: {pattern.Name}, Type: {pattern.PatternType}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register activity pattern");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.RegisterPatternFailed);
                throw new ActivityMonitorException("Failed to register activity pattern", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("RegisterActivityPattern");
            }
        }

        /// <summary>
        /// Kullanıcı aktivite profilini getir;
        /// </summary>
        public async Task<UserActivityProfile> GetUserProfileAsync(string userId)
        {
            ValidateInitialized();

            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
                }

                if (_userProfiles.TryGetValue(userId, out var profile))
                {
                    return profile;
                }

                // Profili yükle;
                profile = await LoadUserProfileAsync(userId);
                if (profile != null)
                {
                    _userProfiles[userId] = profile;
                }

                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user profile");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.GetProfileFailed);
                throw new ActivityMonitorException("Failed to get user profile", ex);
            }
        }

        /// <summary>
        /// Aktivite analizi yap;
        /// </summary>
        public async Task<ActivityAnalysisResult> AnalyzeActivitiesAsync(
            IEnumerable<ActivityEvent> activities,
            AnalysisOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("AnalyzeActivities");
                _logger.LogDebug($"Analyzing {activities.Count()} activities");

                var analysisOptions = options ?? AnalysisOptions.Default;
                var result = await _activityAnalyzer.AnalyzeAsync(activities, analysisOptions);

                // Pattern tanıma;
                if (analysisOptions.DetectPatterns)
                {
                    var patterns = await _patternRecognizer.RecognizePatternsAsync(activities);
                    result.Patterns = patterns;
                }

                // Anomali tespiti;
                if (analysisOptions.DetectAnomalies)
                {
                    var anomalies = await _anomalyDetector.DetectAsync(activities);
                    result.Anomalies = anomalies;
                }

                // Sınıflandırma;
                if (analysisOptions.ClassifyActivities)
                {
                    var classifications = await _activityClassifier.ClassifyAsync(activities);
                    result.Classifications = classifications;
                }

                _logger.LogDebug($"Activity analysis completed: {result.TotalActivities} activities analyzed");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze activities");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.AnalysisFailed);
                throw new ActivityMonitorException("Failed to analyze activities", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("AnalyzeActivities");
            }
        }

        /// <summary>
        /// Real-time aktivite izlemesi başlat;
        /// </summary>
        public IObservable<ActivityEvent> StartRealTimeMonitoring(string sessionId = null)
        {
            ValidateInitialized();

            try
            {
                _logger.LogDebug($"Starting real-time monitoring for session: {sessionId ?? "All"}");

                if (string.IsNullOrEmpty(sessionId))
                {
                    return _activitySubject.AsObservable();
                }
                else;
                {
                    return _activitySubject;
                        .Where(e => e.SessionId == sessionId)
                        .AsObservable();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start real-time monitoring");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.RealTimeMonitoringFailed);
                throw new ActivityMonitorException("Failed to start real-time monitoring", ex);
            }
        }

        /// <summary>
        /// Alert stream'i başlat;
        /// </summary>
        public IObservable<AlertEvent> StartAlertStream()
        {
            ValidateInitialized();

            try
            {
                _logger.LogDebug("Starting alert stream");
                return _alertSubject.AsObservable();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start alert stream");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.AlertStreamFailed);
                throw new ActivityMonitorException("Failed to start alert stream", ex);
            }
        }

        /// <summary>
        /// Performans metriklerini getir;
        /// </summary>
        public async Task<ActivityMonitorMetrics> GetMetricsAsync()
        {
            try
            {
                var metrics = new ActivityMonitorMetrics;
                {
                    Timestamp = DateTime.UtcNow,
                    Uptime = Uptime,
                    State = State,
                    IsInitialized = _isInitialized,
                    IsProcessing = _isProcessing,
                    ActiveSessionCount = ActiveSessionCount,
                    PatternCount = PatternCount,
                    UserProfileCount = UserProfileCount,
                    TotalEventsProcessed = _totalEventsProcessed,
                    TotalAlertsGenerated = _totalAlertsGenerated,
                    TotalAnomaliesDetected = _totalAnomaliesDetected,
                    QueueSize = QueueSize,
                    EventsPerSecond = EventsPerSecond,
                    AverageProcessingTimeMs = AverageProcessingTimeMs,
                    MemoryUsageMB = MemoryUsageMB,
                    LastActivityTime = LastActivityTime,
                    Configuration = _configuration;
                };

                // Detector metriklerini ekle;
                metrics.DetectorMetrics = _detectors;
                    .Select(d => d.GetMetrics())
                    .ToList();

                // Session metriklerini ekle;
                metrics.SessionMetrics = _activeSessions.Values;
                    .Select(s => s.Metrics)
                    .ToList();

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get activity monitor metrics");
                return new ActivityMonitorMetrics;
                {
                    Timestamp = DateTime.UtcNow,
                    Error = ex.Message;
                };
            }
        }

        /// <summary>
        /// Aktivite izlemeyi durdur;
        /// </summary>
        public async Task StopAsync()
        {
            try
            {
                _performanceMonitor.StartOperation("StopActivityMonitor");
                _logger.LogDebug("Stopping activity monitor");

                if (!_isInitialized)
                {
                    return;
                }

                State = ActivityMonitorState.Stopping;

                // İptal token'ını tetikle;
                _cancellationTokenSource.Cancel();

                // Timer'ları durdur;
                StopTimers();

                // Aktif session'ları sonlandır;
                foreach (var sessionId in _activeSessions.Keys.ToList())
                {
                    try
                    {
                        await EndSessionAsync(sessionId, SessionEndReason.MonitorStopped);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to end session during stop: {sessionId}");
                    }
                }

                // Stream'leri durdur;
                StopReactiveStreams();

                // Verileri kaydet;
                await SaveDataAsync();

                // Kaynakları temizle;
                await CleanupResourcesAsync();

                _isInitialized = false;
                State = ActivityMonitorState.Stopped;

                _logger.LogInformation("Activity monitor stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop activity monitor");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.StopFailed);
                throw new ActivityMonitorException("Failed to stop activity monitor", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StopActivityMonitor");
            }
        }

        /// <summary>
        /// Aktivite izlemeyi duraklat;
        /// </summary>
        public void Pause()
        {
            ValidateInitialized();

            try
            {
                _logger.LogDebug("Pausing activity monitor");
                State = ActivityMonitorState.Paused;
                _isProcessing = false;
                _logger.LogInformation("Activity monitor paused");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause activity monitor");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.PauseFailed);
                throw new ActivityMonitorException("Failed to pause activity monitor", ex);
            }
        }

        /// <summary>
        /// Aktivite izlemeyi devam ettir;
        /// </summary>
        public void Resume()
        {
            ValidateInitialized();

            try
            {
                _logger.LogDebug("Resuming activity monitor");
                State = ActivityMonitorState.Running;
                _isProcessing = true;
                _processingEvent.Set();
                _logger.LogInformation("Activity monitor resumed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume activity monitor");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.ResumeFailed);
                throw new ActivityMonitorException("Failed to resume activity monitor", ex);
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// İnitilize kontrolü yap;
        /// </summary>
        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new ActivityMonitorException("Activity monitor is not initialized. Call InitializeAsync first.");
            }

            if (State == ActivityMonitorState.Error)
            {
                throw new ActivityMonitorException("Activity monitor is in error state. Check logs for details.");
            }
        }

        /// <summary>
        /// Detector'ları başlat;
        /// </summary>
        private List<IActivityDetector> InitializeDetectors()
        {
            var detectors = new List<IActivityDetector>
            {
                new MotionDetector(_logger),
                new GestureDetector(_logger),
                new VoiceActivityDetector(_logger),
                new EyeTrackingDetector(_logger),
                new KeyboardActivityDetector(_logger),
                new MouseActivityDetector(_logger),
                new ApplicationActivityDetector(_logger),
                new NetworkActivityDetector(_logger),
                new SystemResourceDetector(_logger)
            };

            _logger.LogDebug($"Initialized {detectors.Count} activity detectors");
            return detectors;
        }

        /// <summary>
        /// Event işlemeyi başlat;
        /// </summary>
        private void StartEventProcessing()
        {
            Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        // Event bekleyin;
                        _processingEvent.Wait(_cancellationTokenSource.Token);
                        _processingEvent.Reset();

                        // Event'leri işle;
                        await ProcessEventsAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        // İptal edildi, normal çıkış;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in event processing loop");
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Event'leri işle;
        /// </summary>
        private async Task ProcessEventsAsync()
        {
            try
            {
                _isProcessing = true;
                _performanceStopwatch.Restart();

                int processedCount = 0;
                var batchStartTime = DateTime.UtcNow;

                while (!_eventQueue.IsEmpty && processedCount < _configuration.MaxBatchSize)
                {
                    if (_eventQueue.TryDequeue(out var activityEvent))
                    {
                        try
                        {
                            await ProcessSingleEventAsync(activityEvent);
                            processedCount++;
                            Interlocked.Increment(ref _totalEventsProcessed);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to process event: {activityEvent.ActivityType}");
                            _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.EventProcessingFailed);
                        }
                    }
                }

                _performanceStopwatch.Stop();

                // Performans metriklerini güncelle;
                if (processedCount > 0)
                {
                    var batchDuration = _performanceStopwatch.ElapsedMilliseconds;
                    var processingTimeMs = batchDuration / processedCount;

                    UpdatePerformanceMetrics(processedCount, batchDuration, processingTimeMs);

                    _logger.LogTrace($"Processed {processedCount} events in {batchDuration}ms ({processingTimeMs:F1}ms/event)");
                }

                _isProcessing = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch event processing");
                _errorReporter.ReportError(ex, ErrorCodes.ActivityMonitor.BatchProcessingFailed);
                _isProcessing = false;
            }
        }

        /// <summary>
        /// Tek event'i işle;
        /// </summary>
        private async Task ProcessSingleEventAsync(ActivityEvent activityEvent)
        {
            // Session'ı güncelle;
            if (_activeSessions.TryGetValue(activityEvent.SessionId, out var session))
            {
                session.TotalEvents++;
                session.LastActivityTime = DateTime.UtcNow;

                // Session metriklerini güncelle;
                UpdateSessionMetrics(session, activityEvent);
            }

            // Cache'e ekle;
            await _activityCache.AddAsync(activityEvent);

            // Aktiviteyi analiz et;
            var analysisResult = await _activityAnalyzer.AnalyzeAsync(activityEvent);

            // Pattern tanıma;
            var patternResult = await _patternRecognizer.RecognizeAsync(activityEvent);
            if (patternResult.IsRecognized)
            {
                OnPatternRecognized(patternResult);
            }

            // Anomali tespiti;
            var anomalyResult = await _anomalyDetector.DetectAsync(activityEvent);
            if (anomalyResult.IsAnomaly)
            {
                Interlocked.Increment(ref _totalAnomaliesDetected);
                OnAnomalyDetected(anomalyResult);

                // Alert oluştur;
                var alert = await _alertManager.CreateAlertAsync(anomalyResult);
                if (alert != null)
                {
                    Interlocked.Increment(ref _totalAlertsGenerated);
                    OnAlertGenerated(alert);
                }
            }

            // Sınıflandırma;
            var classificationResult = await _activityClassifier.ClassifyAsync(activityEvent);

            // Aktivite log'u oluştur;
            var activityLog = new ActivityLog;
            {
                EventId = activityEvent.Id,
                SessionId = activityEvent.SessionId,
                UserId = activityEvent.UserId,
                ActivityType = activityEvent.ActivityType,
                Timestamp = activityEvent.Timestamp,
                AnalysisResult = analysisResult,
                PatternResult = patternResult,
                AnomalyResult = anomalyResult,
                ClassificationResult = classificationResult,
                Metadata = activityEvent.Data;
            };

            _activityLogs.Add(activityLog);

            // Reactive subject'e gönder;
            _activitySubject.OnNext(activityEvent);

            // Event tetikle;
            OnActivityDetected(activityEvent, analysisResult);
        }

        /// <summary>
        /// Session metriklerini güncelle;
        /// </summary>
        private void UpdateSessionMetrics(ActivitySession session, ActivityEvent activityEvent)
        {
            lock (session.Metrics)
            {
                session.Metrics.TotalEvents++;
                session.Metrics.LastEventTime = DateTime.UtcNow;

                // Aktivite tipine göre metrikleri güncelle;
                switch (activityEvent.ActivityType)
                {
                    case ActivityType.Motion:
                        session.Metrics.MotionEvents++;
                        break;
                    case ActivityType.Gesture:
                        session.Metrics.GestureEvents++;
                        break;
                    case ActivityType.Voice:
                        session.Metrics.VoiceEvents++;
                        break;
                    case ActivityType.Keyboard:
                        session.Metrics.KeyboardEvents++;
                        break;
                    case ActivityType.Mouse:
                        session.Metrics.MouseEvents++;
                        break;
                    case ActivityType.Application:
                        session.Metrics.ApplicationEvents++;
                        break;
                    case ActivityType.Network:
                        session.Metrics.NetworkEvents++;
                        break;
                    case ActivityType.System:
                        session.Metrics.SystemEvents++;
                        break;
                }

                // Zaman aralığı metrikleri;
                var timeSinceLastEvent = (DateTime.UtcNow - session.Metrics.LastEventTime).TotalSeconds;
                session.Metrics.AverageEventInterval = (session.Metrics.AverageEventInterval * (session.Metrics.TotalEvents - 1) + timeSinceLastEvent) / session.Metrics.TotalEvents;
            }
        }

        /// <summary>
        /// Performans metriklerini güncelle;
        /// </summary>
        private void UpdatePerformanceMetrics(int eventsProcessed, long batchDurationMs, double avgProcessingTimeMs)
        {
            lock (_syncLock)
            {
                // Event işleme hızı;
                var batchDurationSeconds = batchDurationMs / 1000.0;
                var currentEventsPerSecond = eventsProcessed / batchDurationSeconds;

                // Hareketli ortalama hesapla;
                EventsPerSecond = (EventsPerSecond * 0.7) + (currentEventsPerSecond * 0.3);
                AverageProcessingTimeMs = (AverageProcessingTimeMs * 0.7) + (avgProcessingTimeMs * 0.3);

                // Performans metriği kaydet;
                var metric = new PerformanceMetric;
                {
                    Timestamp = DateTime.UtcNow,
                    OperationName = "ProcessEvents",
                    DurationMs = batchDurationMs,
                    EventsProcessed = eventsProcessed,
                    EventsPerSecond = currentEventsPerSecond,
                    AverageProcessingTimeMs = avgProcessingTimeMs,
                    MemoryUsageMB = MemoryUsageMB,
                    QueueSize = QueueSize;
                };

                _performanceMetrics.Add(metric);
                _performanceSubject.OnNext(metric);

                // Eski metrikleri temizle;
                if (_performanceMetrics.Count > _configuration.MaxPerformanceMetrics)
                {
                    var oldMetrics = _performanceMetrics;
                        .OrderBy(m => m.Timestamp)
                        .Take(_performanceMetrics.Count - _configuration.MaxPerformanceMetrics)
                        .ToList();

                    foreach (var oldMetric in oldMetrics)
                    {
                        _performanceMetrics.TryTake(out _);
                    }
                }

                // Event tetikle;
                OnPerformanceMetricsUpdated();
            }
        }

        /// <summary>
        /// Timer callback'leri;
        /// </summary>
        private void MonitoringCallback(object state)
        {
            try
            {
                // Sistem durumunu kontrol et;
                CheckSystemHealth();

                // Detector'ları güncelle;
                UpdateDetectors();

                // Cache'i temizle;
                _activityCache.Cleanup();

                // Session timeout kontrolü;
                CheckSessionTimeouts();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring callback");
            }
        }

        private void CleanupCallback(object state)
        {
            try
            {
                // Eski log'ları temizle;
                CleanupOldLogs();

                // Bellek optimizasyonu;
                OptimizeMemoryUsage();

                // Geçici dosyaları temizle;
                CleanupTempFiles();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup callback");
            }
        }

        private void ReportingCallback(object state)
        {
            try
            {
                // Periyodik rapor oluştur;
                GeneratePeriodicReport();

                // Metrikleri dışarı aktar;
                ExportMetrics();

                // Sistem durumunu log'la;
                LogSystemStatus();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in reporting callback");
            }
        }

        /// <summary>
        /// Timer'ları başlat;
        /// </summary>
        private void StartTimers()
        {
            _monitoringTimer.Change(
                _configuration.MonitoringInterval,
                _configuration.MonitoringInterval);

            _cleanupTimer.Change(
                _configuration.CleanupInterval,
                _configuration.CleanupInterval);

            _reportingTimer.Change(
                _configuration.ReportingInterval,
                _configuration.ReportingInterval);

            _logger.LogDebug("Timers started successfully");
        }

        /// <summary>
        /// Timer'ları durdur;
        /// </summary>
        private void StopTimers()
        {
            _monitoringTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _reportingTimer.Change(Timeout.Infinite, Timeout.Infinite);

            _logger.LogDebug("Timers stopped successfully");
        }

        /// <summary>
        /// Reactive stream'leri başlat;
        /// </summary>
        private void StartReactiveStreams()
        {
            // Aktivite stream'i;
            _activitySubject;
                .Buffer(TimeSpan.FromSeconds(_configuration.StreamBufferSeconds))
                .Where(buffer => buffer.Any())
                .Subscribe(async buffer =>
                {
                    try
                    {
                        await ProcessEventBufferAsync(buffer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing event buffer");
                    }
                }, _cancellationTokenSource.Token);

            // Alert stream'i;
            _alertSubject;
                .Subscribe(alert =>
                {
                    _eventBus.Publish(new AlertGeneratedEvent(alert));
                }, _cancellationTokenSource.Token);

            // Performans stream'i;
            _performanceSubject;
                .Buffer(TimeSpan.FromSeconds(30))
                .Where(buffer => buffer.Any())
                .Subscribe(buffer =>
                {
                    var avgEventsPerSecond = buffer.Average(m => m.EventsPerSecond);
                    var avgProcessingTime = buffer.Average(m => m.AverageProcessingTimeMs);

                    _logger.LogDebug($"Performance stats: {avgEventsPerSecond:F1} events/s, {avgProcessingTime:F1} ms/event");
                }, _cancellationTokenSource.Token);

            _logger.LogDebug("Reactive streams started successfully");
        }

        /// <summary>
        /// Reactive stream'leri durdur;
        /// </summary>
        private void StopReactiveStreams()
        {
            _activitySubject.OnCompleted();
            _alertSubject.OnCompleted();
            _performanceSubject.OnCompleted();

            _logger.LogDebug("Reactive streams stopped successfully");
        }

        /// <summary>
        /// Event buffer'ını işle;
        /// </summary>
        private async Task ProcessEventBufferAsync(IList<ActivityEvent> events)
        {
            if (!events.Any())
                return;

            try
            {
                // Batch analizi;
                var analysisResult = await _activityAnalyzer.AnalyzeBatchAsync(events);

                // Batch pattern tanıma;
                var patternResults = await _patternRecognizer.RecognizeBatchAsync(events);

                // Batch anomali tespiti;
                var anomalyResults = await _anomalyDetector.DetectBatchAsync(events);

                // EventBus'a gönder;
                _eventBus.Publish(new ActivityBatchProcessedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    EventCount = events.Count,
                    AnalysisResult = analysisResult,
                    PatternResults = patternResults,
                    AnomalyResults = anomalyResults;
                });

                _logger.LogTrace($"Processed event buffer: {events.Count} events");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event buffer");
            }
        }

        /// <summary>
        /// Pattern'leri yükle;
        /// </summary>
        private async Task LoadPatternsAsync()
        {
            try
            {
                // Varsayılan pattern'leri yükle;
                var defaultPatterns = ActivityPattern.GetDefaultPatterns();
                foreach (var pattern in defaultPatterns)
                {
                    _activityPatterns[pattern.Id] = pattern;
                    await _patternRecognizer.AddPatternAsync(pattern);
                }

                // Özel pattern'leri yükle (dosya/db'den)
                var customPatterns = await LoadCustomPatternsAsync();
                foreach (var pattern in customPatterns)
                {
                    _activityPatterns[pattern.Id] = pattern;
                    await _patternRecognizer.AddPatternAsync(pattern);
                }

                _logger.LogInformation($"Loaded {_activityPatterns.Count} activity patterns");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load patterns");
                throw;
            }
        }

        /// <summary>
        /// Özel pattern'leri yükle;
        /// </summary>
        private async Task<List<ActivityPattern>> LoadCustomPatternsAsync()
        {
            // Pattern'leri dosya sisteminden veya veritabanından yükle;
            // Bu kısım proje yapısına göre implemente edilecek;
            await Task.CompletedTask;
            return new List<ActivityPattern>();
        }

        /// <summary>
        /// Kullanıcı profillerini yükle;
        /// </summary>
        private async Task LoadUserProfilesAsync()
        {
            try
            {
                // Kullanıcı profillerini yükle;
                // Bu kısım proje yapısına göre implemente edilecek;
                await Task.CompletedTask;

                _logger.LogDebug("User profiles loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user profiles");
                throw;
            }
        }

        /// <summary>
        /// Kullanıcı profilini yükle veya oluştur;
        /// </summary>
        private async Task LoadOrCreateUserProfileAsync(string userId, ActivitySession session)
        {
            try
            {
                if (!_userProfiles.TryGetValue(userId, out var profile))
                {
                    profile = await LoadUserProfileAsync(userId);
                    if (profile == null)
                    {
                        // Yeni profil oluştur;
                        profile = new UserActivityProfile(userId)
                        {
                            CreatedAt = DateTime.UtcNow,
                            LastUpdated = DateTime.UtcNow,
                            Configuration = _configuration.UserProfileConfig;
                        };
                    }

                    _userProfiles[userId] = profile;
                }

                // Session'a profil ekle;
                session.UserProfile = profile;
                profile.LastActivityTime = DateTime.UtcNow;
                profile.TotalSessions++;

                _logger.LogDebug($"User profile loaded/created for: {userId}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to load/create user profile for: {userId}");
            }
        }

        /// <summary>
        /// Kullanıcı profilini yükle;
        /// </summary>
        private async Task<UserActivityProfile> LoadUserProfileAsync(string userId)
        {
            // Kullanıcı profilini dosya sisteminden veya veritabanından yükle;
            // Bu kısım proje yapısına göre implemente edilecek;
            await Task.CompletedTask;
            return null;
        }

        /// <summary>
        /// Session metriklerini kaydet;
        /// </summary>
        private async Task SaveSessionMetricsAsync(ActivitySession session)
        {
            try
            {
                // Session metriklerini kaydet;
                // Bu kısım proje yapısına göre implemente edilecek;
                await Task.CompletedTask;

                _logger.LogTrace($"Session metrics saved: {session.SessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to save session metrics: {session.SessionId}");
            }
        }

        /// <summary>
        /// Verileri kaydet;
        /// </summary>
        private async Task SaveDataAsync()
        {
            try
            {
                // Aktivite log'larını kaydet;
                await SaveActivityLogsAsync();

                // Kullanıcı profillerini kaydet;
                await SaveUserProfilesAsync();

                // Pattern'leri kaydet;
                await SavePatternsAsync();

                _logger.LogDebug("Activity data saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save activity data");
            }
        }

        /// <summary>
        /// Aktivite log'larını kaydet;
        /// </summary>
        private async Task SaveActivityLogsAsync()
        {
            // Aktivite log'larını kaydet;
            // Bu kısım proje yapısına göre implemente edilecek;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Kullanıcı profillerini kaydet;
        /// </summary>
        private async Task SaveUserProfilesAsync()
        {
            // Kullanıcı profillerini kaydet;
            // Bu kısım proje yapısına göre implemente edilecek;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Pattern'leri kaydet;
        /// </summary>
        private async Task SavePatternsAsync()
        {
            // Pattern'leri kaydet;
            // Bu kısım proje yapısına göre implemente edilecek;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Sistem sağlığını kontrol et;
        /// </summary>
        private void CheckSystemHealth()
        {
            try
            {
                var metrics = GetMetricsAsync().GetAwaiter().GetResult();

                // Queue boyutu kontrolü;
                if (metrics.QueueSize > _configuration.MaxQueueSizeWarning)
                {
                    _logger.LogWarning($"Queue size is high: {metrics.QueueSize}");
                    _eventBus.Publish(new SystemHealthWarningEvent;
                    {
                        Component = "ActivityMonitor",
                        Metric = "QueueSize",
                        Value = metrics.QueueSize,
                        Threshold = _configuration.MaxQueueSizeWarning,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Bellek kullanımı kontrolü;
                if (metrics.MemoryUsageMB > _configuration.MaxMemoryUsageMB)
                {
                    _logger.LogWarning($"Memory usage is high: {metrics.MemoryUsageMB:F1} MB");
                    _eventBus.Publish(new SystemHealthWarningEvent;
                    {
                        Component = "ActivityMonitor",
                        Metric = "MemoryUsage",
                        Value = metrics.MemoryUsageMB,
                        Threshold = _configuration.MaxMemoryUsageMB,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Event işleme hızı kontrolü;
                if (metrics.EventsPerSecond < _configuration.MinEventsPerSecond)
                {
                    _logger.LogWarning($"Event processing rate is low: {metrics.EventsPerSecond:F1} events/s");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking system health");
            }
        }

        /// <summary>
        /// Detector'ları güncelle;
        /// </summary>
        private void UpdateDetectors()
        {
            foreach (var detector in _detectors)
            {
                try
                {
                    detector.Update();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to update detector: {detector.GetType().Name}");
                }
            }
        }

        /// <summary>
        /// Session timeout kontrolü;
        /// </summary>
        private void CheckSessionTimeouts()
        {
            var timeoutThreshold = DateTime.UtcNow.AddSeconds(-_configuration.SessionTimeoutSeconds);

            foreach (var session in _activeSessions.Values)
            {
                if (session.LastActivityTime < timeoutThreshold)
                {
                    _logger.LogWarning($"Session timeout: {session.SessionId}, Last activity: {session.LastActivityTime}");

                    // Session'ı sonlandır;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await EndSessionAsync(session.SessionId, SessionEndReason.Timeout);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to end timed out session: {session.SessionId}");
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Eski log'ları temizle;
        /// </summary>
        private void CleanupOldLogs()
        {
            try
            {
                var retentionTime = DateTime.UtcNow.AddDays(-_configuration.LogRetentionDays);
                var oldLogs = _activityLogs;
                    .Where(log => log.Timestamp < retentionTime)
                    .ToList();

                int removedCount = 0;
                foreach (var log in oldLogs)
                {
                    if (_activityLogs.TryTake(out _))
                    {
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    _logger.LogDebug($"Cleaned up {removedCount} old activity logs");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old logs");
            }
        }

        /// <summary>
        /// Bellek optimizasyonu;
        /// </summary>
        private void OptimizeMemoryUsage()
        {
            try
            {
                // GC çağır;
                if (MemoryUsageMB > _configuration.MemoryOptimizationThresholdMB)
                {
                    GC.Collect(2, GCCollectionMode.Optimized, false, true);
                    GC.WaitForPendingFinalizers();

                    _logger.LogDebug($"Memory optimized, current usage: {MemoryUsageMB:F1} MB");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing memory");
            }
        }

        /// <summary>
        /// Geçici dosyaları temizle;
        /// </summary>
        private void CleanupTempFiles()
        {
            try
            {
                // Geçici dosyaları temizle;
                // Bu kısım proje yapısına göre implemente edilecek;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up temp files");
            }
        }

        /// <summary>
        /// Periyodik rapor oluştur;
        /// </summary>
        private void GeneratePeriodicReport()
        {
            try
            {
                var report = new ActivityMonitorReport;
                {
                    Timestamp = DateTime.UtcNow,
                    Uptime = Uptime,
                    ActiveSessions = ActiveSessionCount,
                    TotalEventsProcessed = _totalEventsProcessed,
                    TotalAlertsGenerated = _totalAlertsGenerated,
                    TotalAnomaliesDetected = _totalAnomaliesDetected,
                    EventsPerSecond = EventsPerSecond,
                    AverageProcessingTimeMs = AverageProcessingTimeMs,
                    MemoryUsageMB = MemoryUsageMB,
                    QueueSize = QueueSize,
                    DetectorStatus = _detectors.Select(d => d.GetStatus()).ToList(),
                    SessionStatistics = _activeSessions.Values.Select(s => new SessionStatistics;
                    {
                        SessionId = s.SessionId,
                        UserId = s.UserId,
                        Duration = DateTime.UtcNow - s.StartTime,
                        TotalEvents = s.TotalEvents,
                        LastActivity = s.LastActivityTime;
                    }).ToList()
                };

                // Raporu kaydet veya gönder;
                SaveReport(report);

                _logger.LogDebug($"Periodic report generated: {report.Timestamp}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating periodic report");
            }
        }

        /// <summary>
        /// Raporu kaydet;
        /// </summary>
        private void SaveReport(ActivityMonitorReport report)
        {
            // Raporu kaydet;
            // Bu kısım proje yapısına göre implemente edilecek;
        }

        /// <summary>
        /// Metrikleri dışarı aktar;
        /// </summary>
        private void ExportMetrics()
        {
            try
            {
                // Metrikleri dışarı aktar;
                // Bu kısım proje yapısına göre implemente edilecek;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting metrics");
            }
        }

        /// <summary>
        /// Sistem durumunu log'la;
        /// </summary>
        private void LogSystemStatus()
        {
            try
            {
                _logger.LogInformation($"Activity Monitor Status - " +
                    $"Sessions: {ActiveSessionCount}, " +
                    $"Queue: {QueueSize}, " +
                    $"Events/s: {EventsPerSecond:F1}, " +
                    $"Memory: {MemoryUsageMB:F1} MB, " +
                    $"Uptime: {Uptime:hh\\:mm\\:ss}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging system status");
            }
        }

        /// <summary>
        /// Bellek kullanımını getir;
        /// </summary>
        private double GetMemoryUsageMB()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                return process.WorkingSet64 / (1024.0 * 1024.0);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Konfigürasyonu güncelle;
        /// </summary>
        private void UpdateConfiguration()
        {
            try
            {
                // Bileşenleri yeniden yapılandır;
                _activityAnalyzer.Configure(_configuration.AnalysisConfig);
                _patternRecognizer.Configure(_configuration.PatternConfig);
                _anomalyDetector.Configure(_configuration.AnomalyConfig);
                _activityClassifier.Configure(_configuration.ClassificationConfig);
                _performanceOptimizer.Configure(_configuration.PerformanceConfig);
                _activityCache.Configure(_configuration.CacheConfig);
                _alertManager.Configure(_configuration.AlertConfig);

                // Detector'ları güncelle;
                foreach (var detector in _detectors)
                {
                    detector.Configure(_configuration);
                }

                // Timer'ları yeniden başlat;
                if (_isInitialized)
                {
                    StopTimers();
                    StartTimers();
                }

                _logger.LogInformation("Activity monitor configuration updated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update configuration");
                throw new ActivityMonitorException("Failed to update configuration", ex);
            }
        }

        /// <summary>
        /// Kaynakları temizle;
        /// </summary>
        private async Task CleanupResourcesAsync()
        {
            try
            {
                // Detector'ları temizle;
                foreach (var detector in _detectors)
                {
                    if (detector is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }

                // Bileşenleri temizle;
                _activityAnalyzer.Dispose();
                _patternRecognizer.Dispose();
                _anomalyDetector.Dispose();
                _activityClassifier.Dispose();
                _performanceOptimizer.Dispose();
                _activityCache.Dispose();
                _alertManager.Dispose();

                // Concurrent koleksiyonları temizle;
                _activeSessions.Clear();
                _activityPatterns.Clear();
                while (_eventQueue.TryDequeue(out _)) { }
                _activityLogs.Clear();
                _userProfiles.Clear();
                _performanceMetrics.Clear();

                // Timer'ları temizle;
                _monitoringTimer.Dispose();
                _cleanupTimer.Dispose();
                _reportingTimer.Dispose();

                // İptal token'ını temizle;
                _cancellationTokenSource.Dispose();
                _processingEvent.Dispose();

                _logger.LogDebug("Activity monitor resources cleaned up");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup resources");
            }
        }

        #endregion;

        #region Event Triggers;

        /// <summary>
        Aktivite tespit edildi event'ini tetikle;
        /// </summary>
        protected virtual void OnActivityDetected(ActivityEvent activityEvent, ActivityAnalysisResult analysisResult)
        {
            ActivityDetected?.Invoke(this, new ActivityDetectedEventArgs(activityEvent, analysisResult));
            _eventBus.Publish(new ActivityDetectedEvent(activityEvent, analysisResult));
        }

        /// <summary>
        Pattern tanındı event'ini tetikle;
        /// </summary>
        protected virtual void OnPatternRecognized(PatternRecognitionResult result)
        {
            PatternRecognized?.Invoke(this, new PatternRecognizedEventArgs(result));
            _eventBus.Publish(new PatternRecognizedEvent(result));
        }

        /// <summary>
        Anomali tespit edildi event'ini tetikle;
        /// </summary>
        protected virtual void OnAnomalyDetected(AnomalyDetectionResult result)
        {
            AnomalyDetected?.Invoke(this, new AnomalyDetectedEventArgs(result));
            _eventBus.Publish(new AnomalyDetectedEvent(result));
        }

        /// <summary>
        Alert oluşturuldu event'ini tetikle;
        /// </summary>
        protected virtual void OnAlertGenerated(AlertEvent alert)
        {
            AlertGenerated?.Invoke(this, new AlertGeneratedEventArgs(alert));
            _alertSubject.OnNext(alert);
        }

        /// <summary>
        Session başladı event'ini tetikle;
        /// </summary>
        protected virtual void OnSessionStarted(ActivitySession session)
        {
            SessionStarted?.Invoke(this, new SessionStartedEventArgs(session));
            _eventBus.Publish(new SessionStartedEvent(session));
        }

        /// <summary>
        Session sonlandı event'ini tetikle;
        /// </summary>
        protected virtual void OnSessionEnded(SessionResult result)
        {
            SessionEnded?.Invoke(this, new SessionEndedEventArgs(result));
            _eventBus.Publish(new SessionEndedEvent(result));
        }

        /// <summary>
        Performans metrikleri güncellendi event'ini tetikle;
        /// </summary>
        protected virtual void OnPerformanceMetricsUpdated()
        {
            PerformanceMetricsUpdated?.Invoke(this, new PerformanceMetricsUpdatedEventArgs(_performanceMetrics.ToList()));
        }

        /// <summary>
        Durum değişti event'ini tetikle;
        /// </summary>
        protected virtual void OnStateChanged()
        {
            StateChanged?.Invoke(this, new ActivityMonitorStateChangedEventArgs(_state));
            _eventBus.Publish(new ActivityMonitorStateChangedEvent(_state));
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        /// <summary>
        /// Dispose pattern implementation;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Managed kaynakları temizle;
                    if (_isInitialized)
                    {
                        StopAsync().GetAwaiter().GetResult();
                    }

                    _cancellationTokenSource?.Dispose();
                    _processingEvent?.Dispose();

                    _monitoringTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _reportingTimer?.Dispose();

                    _activitySubject?.Dispose();
                    _alertSubject?.Dispose();
                    _performanceSubject?.Dispose();

                    foreach (var detector in _detectors)
                    {
                        if (detector is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    _activityAnalyzer?.Dispose();
                    _patternRecognizer?.Dispose();
                    _anomalyDetector?.Dispose();
                    _activityClassifier?.Dispose();
                    _performanceOptimizer?.Dispose();
                    _activityCache?.Dispose();
                    _alertManager?.Dispose();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Dispose;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~ActivityMonitor()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Aktivite izleyici durumu;
    /// </summary>
    public enum ActivityMonitorState;
    {
        Stopped,
        Initializing,
        Running,
        Paused,
        Stopping,
        Error;
    }

    /// <summary>
    /// Aktivite tipi;
    /// </summary>
    public enum ActivityType;
    {
        Unknown,
        Motion,
        Gesture,
        Voice,
        EyeTracking,
        Keyboard,
        Mouse,
        Application,
        Network,
        System,
        SessionStarted,
        SessionEnded,
        UserInteraction,
        SystemResource,
        Custom;
    }

    /// <summary>
    /// Session durumu;
    /// </summary>
    public enum SessionStatus;
    {
        Active,
        Ended,
        Timeout,
        Error;
    }

    /// <summary>
    /// Session sonlandırma nedeni;
    /// </summary>
    public enum SessionEndReason;
    {
        Completed,
        Timeout,
        UserRequest,
        Error,
        MonitorStopped,
        SystemShutdown;
    }

    /// <summary>
    /// Aktivite event'i;
    /// </summary>
    public class ActivityEvent;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public ActivityType ActivityType { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        public double Confidence { get; set; } = 1.0;
        public string Source { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Aktivite session'ı;
    /// </summary>
    public class ActivitySession;
    {
        public string SessionId { get; }
        public string UserId { get; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastActivityTime { get; set; }
        public SessionStatus Status { get; set; }
        public SessionEndReason? EndReason { get; set; }
        public SessionContext Context { get; set; }
        public UserActivityProfile UserProfile { get; set; }
        public SessionConfiguration Configuration { get; set; }
        public SessionMetrics Metrics { get; } = new SessionMetrics();
        public int TotalEvents { get; set; }

        public ActivitySession(string sessionId, string userId = null)
        {
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            UserId = userId;
        }
    }

    /// <summary>
    /// Session metrikleri;
    /// </summary>
    public class SessionMetrics;
    {
        public int TotalEvents { get; set; }
        public int MotionEvents { get; set; }
        public int GestureEvents { get; set; }
        public int VoiceEvents { get; set; }
        public int KeyboardEvents { get; set; }
        public int MouseEvents { get; set; }
        public int ApplicationEvents { get; set; }
        public int NetworkEvents { get; set; }
        public int SystemEvents { get; set; }
        public DateTime LastEventTime { get; set; }
        public double AverageEventInterval { get; set; }
        public double PeakEventsPerSecond { get; set; }
        public Dictionary<string, double> CustomMetrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Kullanıcı aktivite profili;
    /// </summary>
    public class UserActivityProfile;
    {
        public string UserId { get; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime LastActivityTime { get; set; }
        public int TotalSessions { get; set; }
        public long TotalEvents { get; set; }
        public Dictionary<ActivityType, int> ActivityCounts { get; set; } = new Dictionary<ActivityType, int>();
        public Dictionary<string, ActivityPattern> CommonPatterns { get; set; } = new Dictionary<string, ActivityPattern>();
        public UserProfileConfiguration Configuration { get; set; }
        public Dictionary<string, object> Preferences { get; set; } = new Dictionary<string, object>();

        public UserActivityProfile(string userId)
        {
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        }
    }

    /// <summary>
    /// Aktivite pattern'i;
    /// </summary>
    public class ActivityPattern;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Description { get; set; }
        public PatternType PatternType { get; set; }
        public List<ActivityEvent> Sequence { get; set; } = new List<ActivityEvent>();
        public Dictionary<string, object> Conditions { get; set; } = new Dictionary<string, object>();
        public double Threshold { get; set; } = 0.8;
        public int Priority { get; set; } = 1;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public static List<ActivityPattern> GetDefaultPatterns()
        {
            return new List<ActivityPattern>
            {
                new ActivityPattern;
                {
                    Name = "RapidClicking",
                    Description = "Hızlı tıklama aktivitesi",
                    PatternType = PatternType.MouseActivity,
                    Conditions = new Dictionary<string, object>
                    {
                        ["MinClicksPerSecond"] = 5,
                        ["DurationSeconds"] = 2;
                    },
                    Priority = 2;
                },
                new ActivityPattern;
                {
                    Name = "Inactivity",
                    Description = "Uzun süreli hareketsizlik",
                    PatternType = PatternType.Inactivity,
                    Conditions = new Dictionary<string, object>
                    {
                        ["MaxEventsPerMinute"] = 1,
                        ["DurationMinutes"] = 5;
                    },
                    Priority = 1;
                }
            };
        }
    }

    /// <summary>
    /// Pattern tipi;
    /// </summary>
    public enum PatternType;
    {
        Unknown,
        MotionPattern,
        GesturePattern,
        VoicePattern,
        KeyboardPattern,
        MousePattern,
        ApplicationPattern,
        NetworkPattern,
        SystemPattern,
        Inactivity,
        Anomaly,
        Custom;
    }

    /// <summary>
    /// Aktivite analizi sonucu;
    /// </summary>
    public class ActivityAnalysisResult;
    {
        public bool IsAnalyzed { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> Features { get; set; } = new Dictionary<string, double>();
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Pattern tanıma sonucu;
    /// </summary>
    public class PatternRecognitionResult;
    {
        public bool IsRecognized { get; set; }
        public string PatternId { get; set; }
        public string PatternName { get; set; }
        public double Confidence { get; set; }
        public List<ActivityEvent> MatchedEvents { get; set; } = new List<ActivityEvent>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Anomali tespit sonucu;
    /// </summary>
    public class AnomalyDetectionResult;
    {
        public bool IsAnomaly { get; set; }
        public double AnomalyScore { get; set; }
        public string AnomalyType { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
        public DateTime Timestamp { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Alert event'i;
    /// </summary>
    public class AlertEvent;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public AlertLevel Level { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public string Source { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        public bool IsAcknowledged { get; set; }
    }

    /// <summary>
    /// Alert seviyesi;
    /// </summary>
    public enum AlertLevel;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    /// <summary>
    /// Performans metriği;
    /// </summary>
    public class PerformanceMetric;
    {
        public DateTime Timestamp { get; set; }
        public string OperationName { get; set; }
        public long DurationMs { get; set; }
        public int EventsProcessed { get; set; }
        public double EventsPerSecond { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double MemoryUsageMB { get; set; }
        public int QueueSize { get; set; }
        public Dictionary<string, object> CustomMetrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Aktivite izleyici metrikleri;
    /// </summary>
    public class ActivityMonitorMetrics;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Uptime { get; set; }
        public ActivityMonitorState State { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsProcessing { get; set; }
        public int ActiveSessionCount { get; set; }
        public int PatternCount { get; set; }
        public int UserProfileCount { get; set; }
        public long TotalEventsProcessed { get; set; }
        public long TotalAlertsGenerated { get; set; }
        public long TotalAnomaliesDetected { get; set; }
        public int QueueSize { get; set; }
        public double EventsPerSecond { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double MemoryUsageMB { get; set; }
        public DateTime LastActivityTime { get; set; }
        public ActivityMonitorConfiguration Configuration { get; set; }
        public List<DetectorMetrics> DetectorMetrics { get; set; } = new List<DetectorMetrics>();
        public List<SessionMetrics> SessionMetrics { get; set; } = new List<SessionMetrics>();
        public string Error { get; set; }
    }

    /// <summary>
    /// Aktivite izleyici raporu;
    /// </summary>
    public class ActivityMonitorReport;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActiveSessions { get; set; }
        public long TotalEventsProcessed { get; set; }
        public long TotalAlertsGenerated { get; set; }
        public long TotalAnomaliesDetected { get; set; }
        public double EventsPerSecond { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double MemoryUsageMB { get; set; }
        public int QueueSize { get; set; }
        public List<DetectorStatus> DetectorStatus { get; set; } = new List<DetectorStatus>();
        public List<SessionStatistics> SessionStatistics { get; set; } = new List<SessionStatistics>();
    }

    /// <summary>
    /// Session istatistikleri;
    /// </summary>
    public class SessionStatistics;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public TimeSpan Duration { get; set; }
        public int TotalEvents { get; set; }
        public DateTime LastActivity { get; set; }
    }

    #endregion;
}
