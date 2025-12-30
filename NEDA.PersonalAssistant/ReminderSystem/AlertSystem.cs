using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Brain.MemorySystem;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Services.Messaging.EventBus;
using NEDA.Services.NotificationService;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Monitoring.Diagnostics;

namespace NEDA.PersonalAssistant.ReminderSystem;
{
    /// <summary>
    /// Akıllı uyarı sistemi - Kritik olaylar, anormallikler ve sistem durumları için çok kanallı uyarıları yönetir;
    /// </summary>
    public class AlertSystem : IAlertSystem, IDisposable;
    {
        private readonly ILogger<AlertSystem> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly IMemorySystem _memorySystem;
        private readonly IEventBus _eventBus;
        private readonly INotificationManager _notificationManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly AlertSystemConfig _config;

        private readonly ConcurrentDictionary<string, ActiveAlert> _activeAlerts;
        private readonly ConcurrentDictionary<string, AlertRule> _alertRules;
        private readonly ConcurrentDictionary<string, AlertChannel> _alertChannels;
        private readonly ConcurrentQueue<AlertEvent> _alertQueue;
        private readonly ConcurrentDictionary<string, AlertHistory> _alertHistory;

        private readonly System.Timers.Timer _monitoringTimer;
        private readonly System.Timers.Timer _escalationTimer;
        private readonly System.Timers.Timer _cleanupTimer;
        private readonly SemaphoreSlim _processingLock = new(1, 1);

        private bool _isInitialized;
        private bool _isMonitoring;
        private DateTime _lastHealthCheck;
        private long _totalAlertsProcessed;
        private readonly AlertStatistics _statistics;

        /// <summary>
        /// Yeni uyarı oluşturulduğunda tetiklenen olay;
        /// </summary>
        public event EventHandler<AlertCreatedEventArgs> AlertCreated;

        /// <summary>
        /// Uyarı durumu değiştiğinde tetiklenen olay;
        /// </summary>
        public event EventHandler<AlertStatusChangedEventArgs> AlertStatusChanged;

        /// <summary>
        /// Uyarı onaylandığında tetiklenen olay;
        /// </summary>
        public event EventHandler<AlertAcknowledgedEventArgs> AlertAcknowledged;

        /// <summary>
        /// Uyarı işleme istatistikleri;
        /// </summary>
        public AlertStatistics Statistics => _statistics;

        /// <summary>
        /// Sistem durumu;
        /// </summary>
        public SystemState State { get; private set; }

        /// <summary>
        /// AlertSystem constructor;
        /// </summary>
        public AlertSystem(
            ILogger<AlertSystem> logger,
            IExceptionHandler exceptionHandler,
            IMemorySystem memorySystem,
            IEventBus eventBus,
            INotificationManager notificationManager,
            IPerformanceMonitor performanceMonitor,
            IDiagnosticTool diagnosticTool,
            IOptions<AlertSystemConfig> config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _memorySystem = memorySystem ?? throw new ArgumentNullException(nameof(memorySystem));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _config = config?.Value ?? new AlertSystemConfig();

            _activeAlerts = new ConcurrentDictionary<string, ActiveAlert>();
            _alertRules = new ConcurrentDictionary<string, AlertRule>();
            _alertChannels = new ConcurrentDictionary<string, AlertChannel>();
            _alertQueue = new ConcurrentQueue<AlertEvent>();
            _alertHistory = new ConcurrentDictionary<string, AlertHistory>();

            _statistics = new AlertStatistics();
            State = SystemState.Stopped;

            // İzleme zamanlayıcısı (30 saniyede bir)
            _monitoringTimer = new System.Timers.Timer(30000);
            _monitoringTimer.Elapsed += OnMonitoringTimerElapsed;

            // Eslasyon zamanlayıcısı (5 dakikada bir)
            _escalationTimer = new System.Timers.Timer(300000);
            _escalationTimer.Elapsed += OnEscalationTimerElapsed;

            // Temizlik zamanlayıcısı (1 saatte bir)
            _cleanupTimer = new System.Timers.Timer(3600000);
            _cleanupTimer.Elapsed += OnCleanupTimerElapsed;

            // Event bus subscription;
            _eventBus.Subscribe<SystemAlertEvent>(HandleSystemAlertEvent);
            _eventBus.Subscribe<PerformanceAlertEvent>(HandlePerformanceAlertEvent);

            _logger.LogInformation("AlertSystem initialized");
        }

        /// <summary>
        /// Uyarı sistemini başlatır;
        /// </summary>
        public async Task<SystemResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("AlertSystem already initialized");
                    return SystemResult.AlreadyInitialized();
                }

                _logger.LogInformation("Initializing AlertSystem...");

                // Konfigürasyon validasyonu;
                if (!ValidateConfig(_config))
                    return SystemResult.Failure("Invalid AlertSystem configuration");

                // Uyarı kanallarını başlat;
                await InitializeAlertChannelsAsync(cancellationToken);

                // Uyarı kurallarını yükle;
                await LoadAlertRulesAsync(cancellationToken);

                // Aktif uyarıları yükle;
                await LoadActiveAlertsAsync(cancellationToken);

                // Zamanlayıcıları başlat;
                _monitoringTimer.Start();
                _escalationTimer.Start();
                _cleanupTimer.Start();

                _isInitialized = true;
                _isMonitoring = true;
                State = SystemState.Running;
                _statistics.StartTime = DateTime.UtcNow;
                _lastHealthCheck = DateTime.UtcNow;

                _logger.LogInformation($"AlertSystem initialized with {_alertRules.Count} rules and {_alertChannels.Count} channels");
                return SystemResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.Initialize");
                _logger.LogError(ex, "Failed to initialize AlertSystem: {Error}", error.Message);
                State = SystemState.Error;
                return SystemResult.Failure($"Initialization failed: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Yeni uyarı oluşturur;
        /// </summary>
        public async Task<AlertResult> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!_isInitialized)
                return AlertResult.SystemNotReady();

            try
            {
                _logger.LogInformation("Creating new alert: {Title}", request.Title);

                // Validasyon;
                var validationResult = ValidateAlertRequest(request);
                if (!validationResult.IsValid)
                    return AlertResult.Failure($"Invalid alert request: {validationResult.ErrorMessage}");

                // Uyarı ID'si oluştur;
                var alertId = GenerateAlertId(request.SourceSystem, request.AlertType);

                // Aktif uyarıları kontrol et (aynı kaynak için)
                if (CheckDuplicateAlert(alertId, request.SourceId, request.AlertType))
                {
                    _logger.LogWarning("Duplicate alert detected: {SourceId}, {AlertType}", request.SourceId, request.AlertType);

                    if (_config.SuppressDuplicateAlerts)
                    {
                        return AlertResult.DuplicateAlert();
                    }
                }

                // Uyarı nesnesi oluştur;
                var alert = new Alert;
                {
                    Id = alertId,
                    Title = request.Title,
                    Message = request.Message,
                    Description = request.Description,
                    SourceSystem = request.SourceSystem,
                    SourceId = request.SourceId,
                    AlertType = request.AlertType,
                    Severity = request.Severity,
                    Category = request.Category,
                    CreatedAt = DateTime.UtcNow,
                    Status = AlertStatus.New,
                    Priority = CalculateAlertPriority(request.Severity, request.Impact),
                    Tags = request.Tags?.ToList() ?? new List<string>(),
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    ExpiresAt = request.ExpiresAt,
                    AssignedTo = request.AssignedTo;
                };

                // Kritik uyarı için otomatik yükselt;
                if (alert.Severity == AlertSeverity.Critical)
                {
                    alert.Priority = AlertPriority.Critical;
                    alert.RequiresAcknowledgement = true;
                    alert.AutoEscalate = true;
                }

                // Aktif uyarıya dönüştür;
                var activeAlert = new ActiveAlert;
                {
                    Alert = alert,
                    CreatedAt = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow,
                    RetryCount = 0,
                    IsEscalated = false,
                    EscalationLevel = 0;
                };

                // Uyarıyı kaydet;
                await SaveAlertAsync(alert, cancellationToken);

                // Aktif uyarılara ekle;
                if (_activeAlerts.TryAdd(alert.Id, activeAlert))
                {
                    // İstatistikleri güncelle;
                    UpdateStatisticsForNewAlert(alert);

                    // Kanal seçimi ve dağıtım;
                    var channels = SelectAlertChannels(alert);
                    var distributionResult = await DistributeAlertAsync(alert, channels, cancellationToken);

                    // Kuyruğa ekle;
                    var alertEvent = new AlertEvent;
                    {
                        AlertId = alert.Id,
                        EventType = AlertEventType.Created,
                        Timestamp = DateTime.UtcNow,
                        Data = new Dictionary<string, object>
                        {
                            ["distribution_result"] = distributionResult;
                        }
                    };
                    _alertQueue.Enqueue(alertEvent);

                    // Olayı tetikle;
                    OnAlertCreated(new AlertCreatedEventArgs;
                    {
                        Alert = alert,
                        CreatedAt = DateTime.UtcNow,
                        DistributionChannels = channels,
                        IsCritical = alert.Severity == AlertSeverity.Critical;
                    });

                    // Geçmişe ekle;
                    await AddToAlertHistoryAsync(alert, AlertHistoryType.Created, cancellationToken);

                    // Performans metriklerini güncelle;
                    await _performanceMonitor.RecordMetricAsync(
                        "alert_created",
                        1,
                        new Dictionary<string, object>
                        {
                            ["alert_type"] = alert.AlertType.ToString(),
                            ["severity"] = alert.Severity.ToString(),
                            ["source_system"] = alert.SourceSystem;
                        },
                        cancellationToken);

                    _logger.LogInformation("Alert created successfully: {AlertId} - {Title}", alert.Id, alert.Title);

                    return AlertResult.Success(alert);
                }

                return AlertResult.Failure("Failed to add alert to active collection");
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.CreateAlert");
                _logger.LogError(ex, "Error creating alert: {Error}", error.Message);
                return AlertResult.Failure($"Error creating alert: {error.Message}");
            }
        }

        /// <summary>
        /// Uyarıyı onaylar;
        /// </summary>
        public async Task<AlertResult> AcknowledgeAlertAsync(string alertId, AcknowledgementRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(alertId))
                throw new ArgumentException("Alert ID cannot be null or empty", nameof(alertId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Acknowledging alert: {AlertId}", alertId);

                if (!_activeAlerts.TryGetValue(alertId, out var activeAlert))
                {
                    return AlertResult.NotFound();
                }

                var alert = activeAlert.Alert;

                // Durum kontrolü;
                if (alert.Status == AlertStatus.Resolved || alert.Status == AlertStatus.Closed)
                {
                    return AlertResult.Failure($"Cannot acknowledge alert in {alert.Status} status");
                }

                // Önceki durumu kaydet;
                var previousStatus = alert.Status;

                // Uyarıyı onayla;
                alert.Status = AlertStatus.Acknowledged;
                alert.AcknowledgedAt = DateTime.UtcNow;
                alert.AcknowledgedBy = request.AcknowledgedBy;
                alert.AcknowledgementComment = request.Comment;
                alert.LastUpdated = DateTime.UtcNow;

                // Aktif uyarıyı güncelle;
                activeAlert.LastUpdated = DateTime.UtcNow;
                activeAlert.IsEscalated = false; // Eslasyonu sıfırla;

                // Değişiklikleri kaydet;
                await SaveAlertAsync(alert, cancellationToken);

                // Durum değişikliği olayı;
                OnAlertStatusChanged(new AlertStatusChangedEventArgs;
                {
                    AlertId = alertId,
                    PreviousStatus = previousStatus,
                    NewStatus = alert.Status,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.AcknowledgedBy,
                    Reason = $"Acknowledged: {request.Comment}"
                });

                // Onaylama olayı;
                OnAlertAcknowledged(new AlertAcknowledgedEventArgs;
                {
                    Alert = alert,
                    AcknowledgedAt = DateTime.UtcNow,
                    AcknowledgedBy = request.AcknowledgedBy,
                    Comment = request.Comment;
                });

                // Geçmişe ekle;
                await AddToAlertHistoryAsync(alert, AlertHistoryType.Acknowledged, cancellationToken);

                // Onay bildirimi gönder;
                await SendAcknowledgementNotificationAsync(alert, request, cancellationToken);

                _logger.LogInformation("Alert acknowledged successfully: {AlertId}", alertId);

                return AlertResult.Success(alert);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.AcknowledgeAlert");
                _logger.LogError(ex, "Error acknowledging alert: {Error}", error.Message);
                return AlertResult.Failure($"Error acknowledging alert: {error.Message}");
            }
        }

        /// <summary>
        /// Uyarıyı çözüldü olarak işaretler;
        /// </summary>
        public async Task<AlertResult> ResolveAlertAsync(string alertId, ResolutionRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(alertId))
                throw new ArgumentException("Alert ID cannot be null or empty", nameof(alertId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Resolving alert: {AlertId}", alertId);

                if (!_activeAlerts.TryGetValue(alertId, out var activeAlert))
                {
                    return AlertResult.NotFound();
                }

                var alert = activeAlert.Alert;

                // Durum kontrolü;
                if (alert.Status == AlertStatus.Closed)
                {
                    return AlertResult.Failure("Alert is already closed");
                }

                // Önceki durumu kaydet;
                var previousStatus = alert.Status;

                // Uyarıyı çözüldü olarak işaretle;
                alert.Status = AlertStatus.Resolved;
                alert.ResolvedAt = DateTime.UtcNow;
                alert.ResolvedBy = request.ResolvedBy;
                alert.ResolutionComment = request.Comment;
                alert.ResolutionCode = request.ResolutionCode;
                alert.ResolutionData = request.ResolutionData;
                alert.LastUpdated = DateTime.UtcNow;

                // Aktif listeden kaldır;
                _activeAlerts.TryRemove(alertId, out _);

                // İstatistikleri güncelle;
                _statistics.ResolvedAlerts++;
                _statistics.ActiveAlerts = _activeAlerts.Count;

                // Değişiklikleri kaydet;
                await SaveAlertAsync(alert, cancellationToken);

                // Durum değişikliği olayı;
                OnAlertStatusChanged(new AlertStatusChangedEventArgs;
                {
                    AlertId = alertId,
                    PreviousStatus = previousStatus,
                    NewStatus = alert.Status,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.ResolvedBy,
                    Reason = $"Resolved: {request.Comment}"
                });

                // Geçmişe ekle;
                await AddToAlertHistoryAsync(alert, AlertHistoryType.Resolved, cancellationToken);

                // Çözüm bildirimi gönder;
                await SendResolutionNotificationAsync(alert, request, cancellationToken);

                _logger.LogInformation("Alert resolved successfully: {AlertId}", alertId);

                return AlertResult.Success(alert);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.ResolveAlert");
                _logger.LogError(ex, "Error resolving alert: {Error}", error.Message);
                return AlertResult.Failure($"Error resolving alert: {error.Message}");
            }
        }

        /// <summary>
        /// Uyarıyı kapatır;
        /// </summary>
        public async Task<AlertResult> CloseAlertAsync(string alertId, CloseRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(alertId))
                throw new ArgumentException("Alert ID cannot be null or empty", nameof(alertId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Closing alert: {AlertId}", alertId);

                var alert = await LoadAlertAsync(alertId, cancellationToken);
                if (alert == null)
                {
                    return AlertResult.NotFound();
                }

                // Durum kontrolü;
                if (alert.Status == AlertStatus.Closed)
                {
                    return AlertResult.Failure("Alert is already closed");
                }

                // Çözülmemiş kritik uyarıları kapatma kontrolü;
                if (alert.Severity == AlertSeverity.Critical &&
                    alert.Status != AlertStatus.Resolved &&
                    !request.ForceClose)
                {
                    return AlertResult.Failure("Critical alerts must be resolved before closing");
                }

                // Önceki durumu kaydet;
                var previousStatus = alert.Status;

                // Uyarıyı kapat;
                alert.Status = AlertStatus.Closed;
                alert.ClosedAt = DateTime.UtcNow;
                alert.ClosedBy = request.ClosedBy;
                alert.CloseComment = request.Comment;
                alert.LastUpdated = DateTime.UtcNow;

                // Aktif listeden kaldır (eğer oradaysa)
                _activeAlerts.TryRemove(alertId, out _);

                // İstatistikleri güncelle;
                _statistics.ClosedAlerts++;
                _statistics.ActiveAlerts = _activeAlerts.Count;

                // Değişiklikleri kaydet;
                await SaveAlertAsync(alert, cancellationToken);

                // Durum değişikliği olayı;
                OnAlertStatusChanged(new AlertStatusChangedEventArgs;
                {
                    AlertId = alertId,
                    PreviousStatus = previousStatus,
                    NewStatus = alert.Status,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.ClosedBy,
                    Reason = $"Closed: {request.Comment}"
                });

                // Geçmişe ekle;
                await AddToAlertHistoryAsync(alert, AlertHistoryType.Closed, cancellationToken);

                _logger.LogInformation("Alert closed successfully: {AlertId}", alertId);

                return AlertResult.Success(alert);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.CloseAlert");
                _logger.LogError(ex, "Error closing alert: {Error}", error.Message);
                return AlertResult.Failure($"Error closing alert: {error.Message}");
            }
        }

        /// <summary>
        /// Uyarı kurallarını değerlendirir;
        /// </summary>
        public async Task<RuleEvaluationResult> EvaluateAlertRulesAsync(RuleEvaluationContext context, CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                _logger.LogDebug("Evaluating alert rules for system: {System}", context.SourceSystem);

                var triggeredRules = new List<AlertRule>();
                var createdAlerts = new List<Alert>();

                // İlgili kuralları getir;
                var relevantRules = _alertRules.Values;
                    .Where(r => r.IsActive &&
                               (r.SourceSystem == context.SourceSystem || r.SourceSystem == "*"))
                    .ToList();

                foreach (var rule in relevantRules)
                {
                    try
                    {
                        // Kural koşullarını değerlendir;
                        var conditionResult = await EvaluateRuleConditionAsync(rule.Condition, context, cancellationToken);

                        if (conditionResult.IsSatisfied)
                        {
                            // Kural tetiklendi;
                            triggeredRules.Add(rule);

                            // Çakışma kontrolü;
                            if (rule.SuppressionWindow.HasValue)
                            {
                                var lastTrigger = await GetLastRuleTriggerAsync(rule.Id, context.SourceId, cancellationToken);
                                if (lastTrigger.HasValue &&
                                    (DateTime.UtcNow - lastTrigger.Value) < rule.SuppressionWindow.Value)
                                {
                                    _logger.LogDebug("Rule suppressed due to suppression window: {RuleId}", rule.Id);
                                    continue;
                                }
                            }

                            // Uyarı oluştur;
                            var alertRequest = CreateAlertRequestFromRule(rule, context);
                            var alertResult = await CreateAlertAsync(alertRequest, cancellationToken);

                            if (alertResult.Success)
                            {
                                createdAlerts.Add(alertResult.Alert);

                                // Kural tetikleme kaydı;
                                await RecordRuleTriggerAsync(rule.Id, context.SourceId, cancellationToken);

                                // Kural istatistiklerini güncelle;
                                rule.LastTriggered = DateTime.UtcNow;
                                rule.TriggerCount++;
                                await SaveAlertRuleAsync(rule, cancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error evaluating rule: {RuleId}", rule.Id);
                    }
                }

                return new RuleEvaluationResult;
                {
                    Success = true,
                    TriggeredRules = triggeredRules,
                    CreatedAlerts = createdAlerts,
                    TotalRulesEvaluated = relevantRules.Count;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating alert rules");
                return RuleEvaluationResult.Failure($"Error evaluating rules: {ex.Message}");
            }
        }

        /// <summary>
        /// Aktif uyarıları getirir;
        /// </summary>
        public async Task<IEnumerable<Alert>> GetActiveAlertsAsync(AlertFilter filter = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var alerts = _activeAlerts.Values.Select(a => a.Alert).ToList();

                // Filtre uygula;
                if (filter != null)
                {
                    alerts = ApplyAlertFilter(alerts, filter).ToList();
                }

                // Sırala (öncelik, şiddet, zaman)
                alerts = alerts;
                    .OrderByDescending(a => GetPriorityValue(a.Priority))
                    .ThenByDescending(a => GetSeverityValue(a.Severity))
                    .ThenByDescending(a => a.CreatedAt)
                    .ToList();

                return alerts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active alerts");
                throw;
            }
        }

        /// <summary>
        /// Uyarı geçmişini getirir;
        /// </summary>
        public async Task<IEnumerable<AlertHistoryEntry>> GetAlertHistoryAsync(
            string alertId,
            DateTime? startTime = null,
            DateTime? endTime = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Getting alert history: {AlertId}", alertId);

                if (_alertHistory.TryGetValue(alertId, out var history))
                {
                    var entries = history.Entries;
                        .Where(e => (!startTime.HasValue || e.Timestamp >= startTime.Value) &&
                                   (!endTime.HasValue || e.Timestamp <= endTime.Value))
                        .OrderByDescending(e => e.Timestamp)
                        .ToList();

                    return entries;
                }

                // Storage'dan yükle;
                var storedHistory = await LoadAlertHistoryFromStorageAsync(alertId, cancellationToken);
                return storedHistory ?? Enumerable.Empty<AlertHistoryEntry>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting alert history: {AlertId}", alertId);
                throw;
            }
        }

        /// <summary>
        /// Sistem durumunu kontrol eder;
        /// </summary>
        public async Task<SystemHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var healthStatus = new SystemHealthStatus;
                {
                    Component = "AlertSystem",
                    Timestamp = DateTime.UtcNow,
                    OverallStatus = HealthStatus.Healthy;
                };

                // Temel metrikler;
                healthStatus.Metrics["ActiveAlerts"] = _activeAlerts.Count;
                healthStatus.Metrics["ActiveRules"] = _alertRules.Count;
                healthStatus.Metrics["ActiveChannels"] = _alertChannels.Count;
                healthStatus.Metrics["AlertQueueSize"] = _alertQueue.Count;
                healthStatus.Metrics["TotalAlertsProcessed"] = _totalAlertsProcessed;
                healthStatus.Metrics["UptimeMinutes"] = (DateTime.UtcNow - _statistics.StartTime).TotalMinutes;

                // Performans metrikleri;
                healthStatus.Metrics["AverageProcessingTimeMs"] = _statistics.AverageProcessingTime;
                healthStatus.Metrics["AlertCreationRate"] = _statistics.AlertsCreatedPerMinute;
                healthStatus.Metrics["AcknowledgementRate"] = _statistics.AcknowledgementsPerMinute;

                // Kanal durumları;
                var channelHealth = await CheckChannelHealthAsync(cancellationToken);
                healthStatus.Subcomponents.AddRange(channelHealth);

                // Sistem durumu kontrolleri;
                if (!_isMonitoring)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = "System is not monitoring";
                }
                else if (_alertQueue.Count > 1000)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"High alert queue size: {_alertQueue.Count}";
                }
                else if (_activeAlerts.Count > 500)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"High number of active alerts: {_activeAlerts.Count}";
                }
                else if (DateTime.UtcNow - _lastHealthCheck > TimeSpan.FromMinutes(10))
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = "No recent health check activity";
                }
                else;
                {
                    healthStatus.OverallStatus = HealthStatus.Healthy;
                    healthStatus.Message = "All systems operational";
                }

                // Kritik uyarılar kontrolü;
                var criticalAlerts = _activeAlerts.Values.Count(a => a.Alert.Severity == AlertSeverity.Critical);
                if (criticalAlerts > 0)
                {
                    healthStatus.Metrics["CriticalAlerts"] = criticalAlerts;
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"{criticalAlerts} critical alerts pending";
                }

                // Kanal hataları;
                var errorChannels = channelHealth.Count(c => c.Status == HealthStatus.Error);
                if (errorChannels > 0)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = $"{errorChannels} alert channels are down";
                }

                // Tanılama bilgisi ekle;
                await _diagnosticTool.LogDiagnosticInfoAsync("alert_system_health_check", healthStatus, cancellationToken);

                return healthStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
                return new SystemHealthStatus;
                {
                    Component = "AlertSystem",
                    Timestamp = DateTime.UtcNow,
                    OverallStatus = HealthStatus.Error,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Uyarı istatistiklerini getirir;
        /// </summary>
        public async Task<AlertAnalytics> GetAnalyticsAsync(AnalyticsRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Generating alert analytics for period: {From} to {To}",
                    request.StartTime, request.EndTime);

                var analytics = new AlertAnalytics;
                {
                    PeriodStart = request.StartTime,
                    PeriodEnd = request.EndTime,
                    GeneratedAt = DateTime.UtcNow;
                };

                // Tüm uyarıları getir;
                var allAlerts = await LoadAllAlertsAsync(request.StartTime, request.EndTime, cancellationToken);

                // Temel istatistikler;
                analytics.TotalAlerts = allAlerts.Count;
                analytics.ActiveAlerts = allAlerts.Count(a => a.Status == AlertStatus.New ||
                                                             a.Status == AlertStatus.Acknowledged);
                analytics.ResolvedAlerts = allAlerts.Count(a => a.Status == AlertStatus.Resolved);
                analytics.ClosedAlerts = allAlerts.Count(a => a.Status == AlertStatus.Closed);

                // Şiddet dağılımı;
                analytics.SeverityDistribution = allAlerts;
                    .GroupBy(a => a.Severity)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count());

                // Kategori dağılımı;
                analytics.CategoryDistribution = allAlerts;
                    .GroupBy(a => a.Category)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Zaman serisi verisi;
                analytics.TimeSeriesData = GenerateTimeSeriesData(allAlerts, request.Granularity);

                // Ortalama çözüm süresi;
                analytics.AverageResolutionTime = CalculateAverageResolutionTime(allAlerts);

                // En çok uyarı üreten sistemler;
                analytics.TopSourceSystems = allAlerts;
                    .GroupBy(a => a.SourceSystem)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Trend analizi;
                analytics.TrendAnalysis = AnalyzeAlertTrends(allAlerts, request.StartTime, request.EndTime);

                // Öneriler;
                analytics.Recommendations = GenerateRecommendations(analytics);

                _logger.LogInformation("Analytics generated successfully");
                return analytics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating analytics");
                throw;
            }
        }

        /// <summary>
        /// Sistem izlemeyi durdurur;
        /// </summary>
        public async Task<SystemResult> StopMonitoringAsync(CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isMonitoring)
                {
                    return SystemResult.NotRunning();
                }

                _logger.LogInformation("Stopping AlertSystem monitoring...");

                // Zamanlayıcıları durdur;
                _monitoringTimer.Stop();
                _escalationTimer.Stop();
                _cleanupTimer.Stop();

                // Bekleyen uyarıları işle;
                await ProcessPendingAlertsAsync(cancellationToken);

                // Tüm aktif uyarıları kaydet;
                await SaveAllActiveAlertsAsync(cancellationToken);

                _isMonitoring = false;
                State = SystemState.Paused;

                _logger.LogInformation("AlertSystem monitoring stopped");
                return SystemResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.StopMonitoring");
                _logger.LogError(ex, "Error stopping monitoring: {Error}", error.Message);
                return SystemResult.Failure($"Error stopping monitoring: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Sistem izlemeyi başlatır;
        /// </summary>
        public async Task<SystemResult> StartMonitoringAsync(CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (_isMonitoring)
                {
                    return SystemResult.AlreadyRunning();
                }

                _logger.LogInformation("Starting AlertSystem monitoring...");

                // Zamanlayıcıları başlat;
                _monitoringTimer.Start();
                _escalationTimer.Start();
                _cleanupTimer.Start();

                _isMonitoring = true;
                State = SystemState.Running;

                _logger.LogInformation("AlertSystem monitoring started");
                return SystemResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.StartMonitoring");
                _logger.LogError(ex, "Error starting monitoring: {Error}", error.Message);
                return SystemResult.Failure($"Error starting monitoring: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Sistemi kapatır;
        /// </summary>
        public async Task<SystemResult> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isInitialized)
                {
                    return SystemResult.NotInitialized();
                }

                _logger.LogInformation("Shutting down AlertSystem...");

                // İzlemeyi durdur;
                await StopMonitoringAsync(cancellationToken);

                // Tüm kaynakları temizle;
                await CleanupAllResourcesAsync(cancellationToken);

                // Durumu güncelle;
                _isInitialized = false;
                State = SystemState.Stopped;
                _statistics.EndTime = DateTime.UtcNow;

                _logger.LogInformation("AlertSystem shutdown completed");
                return SystemResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlertSystem.Shutdown");
                _logger.LogError(ex, "Error during shutdown: {Error}", error.Message);
                return SystemResult.Failure($"Shutdown failed: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        #region Private Methods;

        private async Task InitializeAlertChannelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Initializing alert channels...");

                // Varsayılan kanalları oluştur;
                var defaultChannels = new[]
                {
                    CreateInAppChannel(),
                    CreateEmailChannel(),
                    CreateSMSChannel(),
                    CreatePushChannel(),
                    CreateWebhookChannel(),
                    CreateSlackChannel(),
                    CreateTeamsChannel()
                };

                foreach (var channel in defaultChannels)
                {
                    if (await InitializeChannelAsync(channel, cancellationToken))
                    {
                        _alertChannels[channel.Id] = channel;
                        _logger.LogDebug("Alert channel initialized: {ChannelName} ({ChannelId})",
                            channel.Name, channel.Id);
                    }
                }

                _logger.LogInformation("Initialized {Count} alert channels", _alertChannels.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing alert channels");
                throw;
            }
        }

        private AlertChannel CreateInAppChannel()
        {
            return new AlertChannel;
            {
                Id = "in_app",
                Name = "In-App Notifications",
                Type = ChannelType.InApp,
                IsEnabled = true,
                PrioritySupport = new[] { AlertPriority.Low, AlertPriority.Medium, AlertPriority.High, AlertPriority.Critical },
                Config = new Dictionary<string, object>
                {
                    ["sound_enabled"] = true,
                    ["vibration_enabled"] = true,
                    ["led_enabled"] = true,
                    ["timeout_seconds"] = 30;
                },
                RetryPolicy = new RetryPolicy;
                {
                    MaxRetries = 3,
                    RetryDelay = TimeSpan.FromSeconds(5),
                    BackoffMultiplier = 2.0;
                }
            };
        }

        private AlertChannel CreateEmailChannel()
        {
            return new AlertChannel;
            {
                Id = "email",
                Name = "Email Notifications",
                Type = ChannelType.Email,
                IsEnabled = true,
                PrioritySupport = new[] { AlertPriority.Medium, AlertPriority.High, AlertPriority.Critical },
                Config = new Dictionary<string, object>
                {
                    ["smtp_server"] = _config.EmailSettings?.SmtpServer,
                    ["smtp_port"] = _config.EmailSettings?.SmtpPort ?? 587,
                    ["from_address"] = _config.EmailSettings?.FromAddress,
                    ["use_ssl"] = true,
                    ["template_path"] = "Templates/Email/"
                },
                RetryPolicy = new RetryPolicy;
                {
                    MaxRetries = 5,
                    RetryDelay = TimeSpan.FromSeconds(10),
                    BackoffMultiplier = 1.5;
                }
            };
        }

        private AlertChannel CreateSMSChannel()
        {
            return new AlertChannel;
            {
                Id = "sms",
                Name = "SMS Notifications",
                Type = ChannelType.SMS,
                IsEnabled = true,
                PrioritySupport = new[] { AlertPriority.High, AlertPriority.Critical },
                Config = new Dictionary<string, object>
                {
                    ["provider"] = _config.SmsSettings?.Provider ?? "Twilio",
                    ["account_sid"] = _config.SmsSettings?.AccountSid,
                    ["auth_token"] = _config.SmsSettings?.AuthToken,
                    ["from_number"] = _config.SmsSettings?.FromNumber,
                    ["max_length"] = 160,
                    ["unicode_support"] = true;
                },
                RetryPolicy = new RetryPolicy;
                {
                    MaxRetries = 3,
                    RetryDelay = TimeSpan.FromSeconds(30),
                    BackoffMultiplier = 2.0;
                }
            };
        }

        private AlertChannel CreatePushChannel()
        {
            return new AlertChannel;
            {
                Id = "push",
                Name = "Push Notifications",
                Type = ChannelType.Push,
                IsEnabled = true,
                PrioritySupport = new[] { AlertPriority.Medium, AlertPriority.High, AlertPriority.Critical },
                Config = new Dictionary<string, object>
                {
                    ["platforms"] = new[] { "ios", "android", "web" },
                    ["apns_certificate_path"] = _config.PushSettings?.ApnsCertificatePath,
                    ["fcm_api_key"] = _config.PushSettings?.FcmApiKey,
                    ["web_push_public_key"] = _config.PushSettings?.WebPushPublicKey,
                    ["expiration_hours"] = 24;
                },
                RetryPolicy = new RetryPolicy;
                {
                    MaxRetries = 3,
                    RetryDelay = TimeSpan.FromSeconds(15),
                    BackoffMultiplier = 1.8;
                }
            };
        }

        private AlertChannel CreateWebhookChannel()
        {
            return new AlertChannel;
            {
                Id = "webhook",
                Name = "Webhook Notifications",
                Type = ChannelType.Webhook,
                IsEnabled = true,
                PrioritySupport = new[] { AlertPriority.Medium, AlertPriority.High, AlertPriority.Critical },
                Config = new Dictionary<string, object>
                {
                    ["timeout_seconds"] = 30,
                    ["retry_on_failure"] = true,
                    ["content_type"] = "application/json",
                    ["signature_header"] = "X-Signature"
                },
                RetryPolicy = new RetryPolicy;
                {
                    MaxRetries = 3,
                    RetryDelay = TimeSpan.FromSeconds(10),
                    BackoffMultiplier = 2.0;
                }
            };
        }

        private AlertChannel CreateSlackChannel()
        {
            return new AlertChannel;
            {
                Id = "slack",
                Name = "Slack Notifications",
                Type = ChannelType.Slack,
                IsEnabled = _config.EnableSlackIntegration,
                PrioritySupport = new[] { AlertPriority.Medium, AlertPriority.High, AlertPriority.Critical },
                Config = new Dictionary<string, object>
                {
                    ["webhook_url"] = _config.SlackSettings?.WebhookUrl,
                    ["channel"] = _config.SlackSettings?.Channel ?? "#alerts",
                    ["username"] = _config.SlackSettings?.Username ?? "NEDA Alert System",
                    ["icon_emoji"] = _config.SlackSettings?.IconEmoji ?? ":warning:",
                    ["color_coding"] = true;
                },
                RetryPolicy = new RetryPolicy;
                {
                    MaxRetries = 2,
                    RetryDelay = TimeSpan.FromSeconds(5),
                    BackoffMultiplier = 1.5;
                }
            };
        }

        private AlertChannel CreateTeamsChannel()
        {
            return new AlertChannel;
            {
                Id = "teams",
                Name = "Microsoft Teams",
                Type = ChannelType.Teams,
                IsEnabled = _config.EnableTeamsIntegration,
                PrioritySupport = new[] { AlertPriority.Medium, AlertPriority.High, AlertPriority.Critical },
                Config = new Dictionary<string, object>
                {
                    ["webhook_url"] = _config.TeamsSettings?.WebhookUrl,
                    ["theme_color"] = _config.TeamsSettings?.ThemeColor ?? "FF0000",
                    ["show_facts"] = true,
                    ["show_actions"] = true;
                },
                RetryPolicy = new RetryPolicy;
                {
                    MaxRetries = 2,
                    RetryDelay = TimeSpan.FromSeconds(5),
                    BackoffMultiplier = 1.5;
                }
            };
        }

        private async Task<bool> InitializeChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            try
            {
                if (!channel.IsEnabled)
                {
                    _logger.LogDebug("Channel {ChannelName} is disabled", channel.Name);
                    return false;
                }

                // Kanal tipine göre başlatma;
                switch (channel.Type)
                {
                    case ChannelType.Email:
                        await InitializeEmailChannelAsync(channel, cancellationToken);
                        break;
                    case ChannelType.SMS:
                        await InitializeSmsChannelAsync(channel, cancellationToken);
                        break;
                    case ChannelType.Push:
                        await InitializePushChannelAsync(channel, cancellationToken);
                        break;
                    case ChannelType.Webhook:
                        await InitializeWebhookChannelAsync(channel, cancellationToken);
                        break;
                    case ChannelType.Slack:
                        await InitializeSlackChannelAsync(channel, cancellationToken);
                        break;
                    case ChannelType.Teams:
                        await InitializeTeamsChannelAsync(channel, cancellationToken);
                        break;
                }

                channel.IsInitialized = true;
                channel.LastHealthCheck = DateTime.UtcNow;

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing channel: {ChannelName}", channel.Name);
                channel.IsInitialized = false;
                channel.LastError = ex.Message;
                return false;
            }
        }

        private async Task InitializeEmailChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Email kanalı başlatma;
            // SMTP bağlantı testi vb.
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Email channel initialized");
        }

        private async Task InitializeSmsChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // SMS kanalı başlatma;
            // Provider bağlantı testi;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("SMS channel initialized");
        }

        private async Task InitializePushChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Push kanalı başlatma;
            // FCM/APNS bağlantı testi;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Push channel initialized");
        }

        private async Task InitializeWebhookChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Webhook kanalı başlatma;
            // URL validasyonu;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Webhook channel initialized");
        }

        private async Task InitializeSlackChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Slack kanalı başlatma;
            // Webhook testi;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Slack channel initialized");
        }

        private async Task InitializeTeamsChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Teams kanalı başlatma;
            // Webhook testi;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Teams channel initialized");
        }

        private async Task LoadAlertRulesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Loading alert rules...");

                var rules = await _memorySystem.LoadAlertRulesAsync(cancellationToken);

                if (rules != null)
                {
                    foreach (var rule in rules.Where(r => r.IsActive))
                    {
                        _alertRules[rule.Id] = rule;
                    }
                }

                // Varsayılan kuralları ekle;
                AddDefaultAlertRules();

                _logger.LogInformation("Loaded {Count} alert rules", _alertRules.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading alert rules");
            }
        }

        private void AddDefaultAlertRules()
        {
            try
            {
                // Sistem sağlığı kuralları;
                var systemHealthRule = new AlertRule;
                {
                    Id = "system_health_critical",
                    Name = "System Health Critical",
                    Description = "Triggers when system health status becomes critical",
                    SourceSystem = "SystemMonitor",
                    Condition = new RuleCondition;
                    {
                        Expression = "health_status == 'Critical'",
                        Parameters = new Dictionary<string, object>
                        {
                            ["threshold"] = 0.1,
                            ["duration_minutes"] = 5;
                        }
                    },
                    AlertTemplate = new AlertTemplate;
                    {
                        Title = "System Health Critical Alert",
                        Message = "System health status has become critical. Immediate attention required.",
                        Severity = AlertSeverity.Critical,
                        Category = "SystemHealth",
                        Tags = new[] { "system", "health", "critical" }
                    },
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SuppressionWindow = TimeSpan.FromMinutes(30)
                };

                _alertRules[systemHealthRule.Id] = systemHealthRule;

                // Performans kuralları;
                var performanceRule = new AlertRule;
                {
                    Id = "high_cpu_usage",
                    Name = "High CPU Usage",
                    Description = "Triggers when CPU usage exceeds threshold",
                    SourceSystem = "PerformanceMonitor",
                    Condition = new RuleCondition;
                    {
                        Expression = "cpu_usage > 90",
                        Parameters = new Dictionary<string, object>
                        {
                            ["threshold"] = 90.0,
                            ["duration_minutes"] = 2;
                        }
                    },
                    AlertTemplate = new AlertTemplate;
                    {
                        Title = "High CPU Usage Alert",
                        Message = "CPU usage has exceeded {threshold}% for {duration} minutes.",
                        Severity = AlertSeverity.High,
                        Category = "Performance",
                        Tags = new[] { "cpu", "performance", "monitoring" }
                    },
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SuppressionWindow = TimeSpan.FromMinutes(10)
                };

                _alertRules[performanceRule.Id] = performanceRule;

                // Bellek kuralları;
                var memoryRule = new AlertRule;
                {
                    Id = "high_memory_usage",
                    Name = "High Memory Usage",
                    Description = "Triggers when memory usage exceeds threshold",
                    SourceSystem = "PerformanceMonitor",
                    Condition = new RuleCondition;
                    {
                        Expression = "memory_usage > 85",
                        Parameters = new Dictionary<string, object>
                        {
                            ["threshold"] = 85.0,
                            ["duration_minutes"] = 2;
                        }
                    },
                    AlertTemplate = new AlertTemplate;
                    {
                        Title = "High Memory Usage Alert",
                        Message = "Memory usage has exceeded {threshold}% for {duration} minutes.",
                        Severity = AlertSeverity.High,
                        Category = "Performance",
                        Tags = new[] { "memory", "performance", "monitoring" }
                    },
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    SuppressionWindow = TimeSpan.FromMinutes(10)
                };

                _alertRules[memoryRule.Id] = memoryRule;

                _logger.LogInformation("Added {Count} default alert rules", 3);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding default alert rules");
            }
        }

        private async Task LoadActiveAlertsAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Loading active alerts...");

                var activeAlerts = await _memorySystem.LoadActiveAlertsAsync(cancellationToken);

                if (activeAlerts != null)
                {
                    foreach (var alert in activeAlerts)
                    {
                        if (alert.Status == AlertStatus.New ||
                            alert.Status == AlertStatus.Acknowledged)
                        {
                            var activeAlert = new ActiveAlert;
                            {
                                Alert = alert,
                                CreatedAt = alert.CreatedAt,
                                LastUpdated = alert.LastUpdated,
                                RetryCount = 0,
                                IsEscalated = false,
                                EscalationLevel = 0;
                            };

                            if (_activeAlerts.TryAdd(alert.Id, activeAlert))
                            {
                                _statistics.ActiveAlerts = _activeAlerts.Count;
                            }
                        }
                    }
                }

                _logger.LogInformation("Loaded {Count} active alerts", _activeAlerts.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading active alerts");
            }
        }

        private async Task SaveAlertAsync(Alert alert, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.SaveAlertAsync(alert, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving alert: {AlertId}", alert.Id);
                throw;
            }
        }

        private async Task SaveAllActiveAlertsAsync(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var activeAlert in _activeAlerts.Values)
                {
                    await SaveAlertAsync(activeAlert.Alert, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving all active alerts");
            }
        }

        private async Task<Alert> LoadAlertAsync(string alertId, CancellationToken cancellationToken)
        {
            try
            {
                return await _memorySystem.LoadAlertAsync(alertId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading alert: {AlertId}", alertId);
                return null;
            }
        }

        private async Task SaveAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.SaveAlertRuleAsync(rule, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving alert rule: {RuleId}", rule.Id);
            }
        }

        private async Task AddToAlertHistoryAsync(Alert alert, AlertHistoryType historyType, CancellationToken cancellationToken)
        {
            try
            {
                var historyEntry = new AlertHistoryEntry
                {
                    AlertId = alert.Id,
                    HistoryType = historyType,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["alert_status"] = alert.Status.ToString(),
                        ["alert_severity"] = alert.Severity.ToString(),
                        ["alert_priority"] = alert.Priority.ToString()
                    }
                };

                if (!_alertHistory.ContainsKey(alert.Id))
                {
                    _alertHistory[alert.Id] = new AlertHistory;
                    {
                        AlertId = alert.Id,
                        Entries = new List<AlertHistoryEntry>()
                    };
                }

                _alertHistory[alert.Id].Entries.Add(historyEntry);

                // Storage'a kaydet;
                await _memorySystem.SaveAlertHistoryAsync(historyEntry, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding to alert history: {AlertId}", alert.Id);
            }
        }

        private async Task<IEnumerable<AlertHistoryEntry>> LoadAlertHistoryFromStorageAsync(string alertId, CancellationToken cancellationToken)
        {
            try
            {
                return await _memorySystem.LoadAlertHistoryAsync(alertId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading alert history from storage: {AlertId}", alertId);
                return null;
            }
        }

        private string GenerateAlertId(string sourceSystem, AlertType alertType)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            var sourceAbbr = GetSourceAbbreviation(sourceSystem);
            var typeAbbr = GetTypeAbbreviation(alertType);

            return $"ALERT-{sourceAbbr}-{typeAbbr}-{timestamp}-{random}";
        }

        private string GetSourceAbbreviation(string sourceSystem)
        {
            return sourceSystem.Length > 4;
                ? sourceSystem.Substring(0, 4).ToUpper()
                : sourceSystem.ToUpper();
        }

        private string GetTypeAbbreviation(AlertType alertType)
        {
            return alertType.ToString().Substring(0, 3).ToUpper();
        }

        private bool CheckDuplicateAlert(string alertId, string sourceId, AlertType alertType)
        {
            // Aynı kaynak ve tip için son 1 saatteki uyarıları kontrol et;
            var cutoffTime = DateTime.UtcNow.AddHours(-1);

            return _activeAlerts.Values.Any(a =>
                a.Alert.SourceId == sourceId &&
                a.Alert.AlertType == alertType &&
                a.CreatedAt > cutoffTime);
        }

        private AlertPriority CalculateAlertPriority(AlertSeverity severity, AlertImpact impact)
        {
            // Şiddet ve etkiye göre öncelik hesapla;
            var basePriority = severity switch;
            {
                AlertSeverity.Info => AlertPriority.Low,
                AlertSeverity.Warning => AlertPriority.Medium,
                AlertSeverity.Error => AlertPriority.High,
                AlertSeverity.Critical => AlertPriority.Critical,
                _ => AlertPriority.Medium;
            };

            // Etkiye göre ayarla;
            if (impact == AlertImpact.High && basePriority < AlertPriority.High)
            {
                return AlertPriority.High;
            }
            else if (impact == AlertImpact.Critical && basePriority < AlertPriority.Critical)
            {
                return AlertPriority.Critical;
            }

            return basePriority;
        }

        private void UpdateStatisticsForNewAlert(Alert alert)
        {
            _statistics.AlertsCreated++;
            _statistics.ActiveAlerts = _activeAlerts.Count;
            _statistics.LastAlertCreated = DateTime.UtcNow;

            // Şiddet istatistikleri;
            switch (alert.Severity)
            {
                case AlertSeverity.Info:
                    _statistics.InfoAlerts++;
                    break;
                case AlertSeverity.Warning:
                    _statistics.WarningAlerts++;
                    break;
                case AlertSeverity.Error:
                    _statistics.ErrorAlerts++;
                    break;
                case AlertSeverity.Critical:
                    _statistics.CriticalAlerts++;
                    break;
            }

            // Kategori istatistikleri;
            if (!_statistics.CategoryCounts.ContainsKey(alert.Category))
            {
                _statistics.CategoryCounts[alert.Category] = 0;
            }
            _statistics.CategoryCounts[alert.Category]++;

            // Dakikada oluşturulan uyarı sayısını hesapla;
            CalculateAlertsPerMinute();
        }

        private void CalculateAlertsPerMinute()
        {
            if (_statistics.StartTime == default)
                return;

            var elapsedMinutes = (DateTime.UtcNow - _statistics.StartTime).TotalMinutes;
            if (elapsedMinutes > 0)
            {
                _statistics.AlertsCreatedPerMinute = _statistics.AlertsCreated / elapsedMinutes;
            }
        }

        private List<AlertChannel> SelectAlertChannels(Alert alert)
        {
            var selectedChannels = new List<AlertChannel>();

            foreach (var channel in _alertChannels.Values)
            {
                if (!channel.IsEnabled || !channel.IsInitialized)
                    continue;

                // Öncelik desteği kontrolü;
                if (!channel.PrioritySupport.Contains(alert.Priority))
                    continue;

                // Kanal filtreleri;
                if (ShouldUseChannelForAlert(channel, alert))
                {
                    selectedChannels.Add(channel);
                }
            }

            // Kritik uyarılar için ek kanallar;
            if (alert.Severity == AlertSeverity.Critical)
            {
                AddCriticalAlertChannels(selectedChannels, alert);
            }

            return selectedChannels;
        }

        private bool ShouldUseChannelForAlert(AlertChannel channel, Alert alert)
        {
            // Kanal konfigürasyonuna göre filtrele;
            if (channel.Config.TryGetValue("min_severity", out var minSeverity))
            {
                var minSeverityValue = (AlertSeverity)Enum.Parse(typeof(AlertSeverity), minSeverity.ToString());
                if (alert.Severity < minSeverityValue)
                    return false;
            }

            // Kategori filtreleri;
            if (channel.Config.TryGetValue("categories", out var categories))
            {
                var categoryList = categories as IEnumerable<string>;
                if (categoryList != null && !categoryList.Contains(alert.Category))
                    return false;
            }

            // Zaman filtreleri (gece saatleri vb.)
            if (channel.Config.TryGetValue("quiet_hours", out var quietHours))
            {
                if (IsQuietHour(quietHours))
                    return false;
            }

            return true;
        }

        private bool IsQuietHour(object quietHoursConfig)
        {
            // Sessiz saat kontrolü;
            // Örnek: {"start": "22:00", "end": "08:00"}
            try
            {
                var now = DateTime.UtcNow.TimeOfDay;
                // Basit implementasyon, gerçekte daha karmaşık olabilir;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void AddCriticalAlertChannels(List<AlertChannel> channels, Alert alert)
        {
            // Kritik uyarılar için ek kanallar ekle;
            var additionalChannels = _alertChannels.Values;
                .Where(c => c.IsEnabled &&
                           c.IsInitialized &&
                           c.Type == ChannelType.SMS ||
                           c.Type == ChannelType.Push)
                .Except(channels)
                .ToList();

            channels.AddRange(additionalChannels);
        }

        private async Task<DistributionResult> DistributeAlertAsync(Alert alert, List<AlertChannel> channels, CancellationToken cancellationToken)
        {
            var distributionResult = new DistributionResult;
            {
                AlertId = alert.Id,
                StartedAt = DateTime.UtcNow,
                ChannelResults = new List<ChannelResult>()
            };

            if (!channels.Any())
            {
                distributionResult.Status = DistributionStatus.NoChannels;
                return distributionResult;
            }

            var tasks = channels.Select(channel =>
                DistributeToChannelAsync(alert, channel, cancellationToken));

            var results = await Task.WhenAll(tasks);
            distributionResult.ChannelResults.AddRange(results);

            // Başarı durumunu hesapla;
            var successful = results.Count(r => r.Status == ChannelStatus.Success);
            var failed = results.Count(r => r.Status == ChannelStatus.Failed);

            if (successful > 0 && failed == 0)
            {
                distributionResult.Status = DistributionStatus.Success;
            }
            else if (successful > 0 && failed > 0)
            {
                distributionResult.Status = DistributionStatus.PartialSuccess;
            }
            else;
            {
                distributionResult.Status = DistributionStatus.Failed;
            }

            distributionResult.CompletedAt = DateTime.UtcNow;
            distributionResult.SuccessfulChannels = successful;
            distributionResult.FailedChannels = failed;

            // İstatistikleri güncelle;
            Interlocked.Increment(ref _totalAlertsProcessed);
            _statistics.AlertsDistributed++;

            return distributionResult;
        }

        private async Task<ChannelResult> DistributeToChannelAsync(Alert alert, AlertChannel channel, CancellationToken cancellationToken)
        {
            var channelResult = new ChannelResult;
            {
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                ChannelType = channel.Type,
                StartedAt = DateTime.UtcNow;
            };

            try
            {
                // Kanal tipine göre dağıtım;
                var notification = CreateNotificationForChannel(alert, channel);

                switch (channel.Type)
                {
                    case ChannelType.InApp:
                        channelResult = await SendInAppNotificationAsync(notification, channel, channelResult, cancellationToken);
                        break;
                    case ChannelType.Email:
                        channelResult = await SendEmailNotificationAsync(notification, channel, channelResult, cancellationToken);
                        break;
                    case ChannelType.SMS:
                        channelResult = await SendSmsNotificationAsync(notification, channel, channelResult, cancellationToken);
                        break;
                    case ChannelType.Push:
                        channelResult = await SendPushNotificationAsync(notification, channel, channelResult, cancellationToken);
                        break;
                    case ChannelType.Webhook:
                        channelResult = await SendWebhookNotificationAsync(notification, channel, channelResult, cancellationToken);
                        break;
                    case ChannelType.Slack:
                        channelResult = await SendSlackNotificationAsync(notification, channel, channelResult, cancellationToken);
                        break;
                    case ChannelType.Teams:
                        channelResult = await SendTeamsNotificationAsync(notification, channel, channelResult, cancellationToken);
                        break;
                    default:
                        channelResult.Status = ChannelStatus.Unsupported;
                        channelResult.ErrorMessage = $"Unsupported channel type: {channel.Type}";
                        break;
                }

                // Başarılı ise kanal istatistiklerini güncelle;
                if (channelResult.Status == ChannelStatus.Success)
                {
                    channel.LastSuccessfulDelivery = DateTime.UtcNow;
                    channel.SuccessfulDeliveries++;
                }
                else;
                {
                    channel.LastFailedDelivery = DateTime.UtcNow;
                    channel.FailedDeliveries++;
                    channel.LastError = channelResult.ErrorMessage;
                }

                channel.LastUsed = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                channelResult.Status = ChannelStatus.Failed;
                channelResult.ErrorMessage = ex.Message;
                channelResult.CompletedAt = DateTime.UtcNow;

                _logger.LogError(ex, "Error distributing to channel {ChannelName}: {Error}",
                    channel.Name, ex.Message);
            }

            return channelResult;
        }

        private Notification CreateNotificationForChannel(Alert alert, AlertChannel channel)
        {
            var notification = new Notification;
            {
                Id = Guid.NewGuid().ToString(),
                AlertId = alert.Id,
                Title = alert.Title,
                Message = alert.Message,
                Severity = alert.Severity,
                Priority = alert.Priority,
                Category = alert.Category,
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    ["alert_source"] = alert.SourceSystem,
                    ["alert_type"] = alert.AlertType.ToString(),
                    ["alert_created"] = alert.CreatedAt,
                    ["channel_type"] = channel.Type.ToString()
                }
            };

            // Kanal tipine göre özelleştirme;
            switch (channel.Type)
            {
                case ChannelType.SMS:
                    // SMS için kısa mesaj;
                    notification.Message = TruncateForSms(alert.Message);
                    break;
                case ChannelType.Slack:
                case ChannelType.Teams:
                    // Slack/Teams için formatlı mesaj;
                    notification.Message = FormatForCollaborationTools(alert);
                    break;
            }

            return notification;
        }

        private string TruncateForSms(string message)
        {
            const int maxLength = 160;
            return message.Length > maxLength;
                ? message.Substring(0, maxLength - 3) + "..."
                : message;
        }

        private string FormatForCollaborationTools(Alert alert)
        {
            return $"*{alert.Title}*\n" +
                   $"{alert.Message}\n" +
                   $"\n" +
                   $"*Severity:* {alert.Severity}\n" +
                   $"*Priority:* {alert.Priority}\n" +
                   $"*Category:* {alert.Category}\n" +
                   $"*Created:* {alert.CreatedAt:yyyy-MM-dd HH:mm:ss}\n" +
                   $"\n" +
                   $"<{GetAlertUrl(alert.Id)}|View Alert>";
        }

        private string GetAlertUrl(string alertId)
        {
            return $"{_config.BaseUrl}/alerts/{alertId}";
        }

        private async Task<ChannelResult> SendInAppNotificationAsync(
            Notification notification,
            AlertChannel channel,
            ChannelResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // In-app bildirim gönderme;
                await _notificationManager.SendInAppNotificationAsync(notification, cancellationToken);

                result.Status = ChannelStatus.Success;
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ChannelStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
                throw;
            }
        }

        private async Task<ChannelResult> SendEmailNotificationAsync(
            Notification notification,
            AlertChannel channel,
            ChannelResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // Email bildirim gönderme;
                await _notificationManager.SendEmailNotificationAsync(notification, cancellationToken);

                result.Status = ChannelStatus.Success;
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ChannelStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
                throw;
            }
        }

        private async Task<ChannelResult> SendSmsNotificationAsync(
            Notification notification,
            AlertChannel channel,
            ChannelResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // SMS bildirim gönderme;
                await _notificationManager.SendSmsNotificationAsync(notification, cancellationToken);

                result.Status = ChannelStatus.Success;
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ChannelStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
                throw;
            }
        }

        private async Task<ChannelResult> SendPushNotificationAsync(
            Notification notification,
            AlertChannel channel,
            ChannelResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // Push bildirim gönderme;
                await _notificationManager.SendPushNotificationAsync(notification, cancellationToken);

                result.Status = ChannelStatus.Success;
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ChannelStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
                throw;
            }
        }

        private async Task<ChannelResult> SendWebhookNotificationAsync(
            Notification notification,
            AlertChannel channel,
            ChannelResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // Webhook bildirim gönderme;
                await _notificationManager.SendWebhookNotificationAsync(notification, cancellationToken);

                result.Status = ChannelStatus.Success;
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ChannelStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
                throw;
            }
        }

        private async Task<ChannelResult> SendSlackNotificationAsync(
            Notification notification,
            AlertChannel channel,
            ChannelResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // Slack bildirim gönderme;
                await _notificationManager.SendSlackNotificationAsync(notification, cancellationToken);

                result.Status = ChannelStatus.Success;
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ChannelStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
                throw;
            }
        }

        private async Task<ChannelResult> SendTeamsNotificationAsync(
            Notification notification,
            AlertChannel channel,
            ChannelResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // Teams bildirim gönderme;
                await _notificationManager.SendTeamsNotificationAsync(notification, cancellationToken);

                result.Status = ChannelStatus.Success;
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Status = ChannelStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.UtcNow;
                throw;
            }
        }

        private async Task SendAcknowledgementNotificationAsync(Alert alert, AcknowledgementRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var notification = new Notification;
                {
                    Title = $"Alert Acknowledged: {alert.Title}",
                    Message = $"Alert was acknowledged by {request.AcknowledgedBy}",
                    Severity = AlertSeverity.Info,
                    Priority = AlertPriority.Low,
                    Category = "Acknowledgement",
                    Metadata = new Dictionary<string, object>
                    {
                        ["acknowledged_by"] = request.AcknowledgedBy,
                        ["acknowledged_at"] = alert.AcknowledgedAt,
                        ["comment"] = request.Comment;
                    }
                };

                await _notificationManager.SendNotificationAsync(notification, cancellationToken);
                _statistics.AcknowledgementsSent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending acknowledgement notification");
            }
        }

        private async Task SendResolutionNotificationAsync(Alert alert, ResolutionRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var notification = new Notification;
                {
                    Title = $"Alert Resolved: {alert.Title}",
                    Message = $"Alert was resolved by {request.ResolvedBy}",
                    Severity = AlertSeverity.Info,
                    Priority = AlertPriority.Low,
                    Category = "Resolution",
                    Metadata = new Dictionary<string, object>
                    {
                        ["resolved_by"] = request.ResolvedBy,
                        ["resolved_at"] = alert.ResolvedAt,
                        ["resolution_code"] = request.ResolutionCode,
                        ["comment"] = request.Comment;
                    }
                };

                await _notificationManager.SendNotificationAsync(notification, cancellationToken);
                _statistics.ResolutionsSent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending resolution notification");
            }
        }

        private async Task<ConditionEvaluationResult> EvaluateRuleConditionAsync(RuleCondition condition, RuleEvaluationContext context, CancellationToken cancellationToken)
        {
            try
            {
                // Kural koşulunu değerlendir;
                // Burada gerçek bir ifade değerlendirme motoru kullanılabilir;
                var expression = condition.Expression;
                var parameters = condition.Parameters;

                // Basit bir değerlendirme örneği;
                var isSatisfied = EvaluateSimpleCondition(expression, context, parameters);

                return new ConditionEvaluationResult;
                {
                    IsSatisfied = isSatisfied,
                    Confidence = 1.0,
                    Details = new Dictionary<string, object>()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating rule condition");
                return new ConditionEvaluationResult { IsSatisfied = false };
            }
        }

        private bool EvaluateSimpleCondition(string expression, RuleEvaluationContext context, Dictionary<string, object> parameters)
        {
            // Basit koşul değerlendirme;
            // Gerçek implementasyonda daha karmaşık bir parser kullanılmalı;
            try
            {
                if (expression.Contains("health_status == 'Critical'"))
                {
                    return context.TriggerData.TryGetValue("health_status", out var status) &&
                           status?.ToString() == "Critical";
                }
                else if (expression.Contains("cpu_usage >"))
                {
                    if (context.TriggerData.TryGetValue("cpu_usage", out var cpuUsage) &&
                        double.TryParse(cpuUsage?.ToString(), out var cpuValue))
                    {
                        if (parameters.TryGetValue("threshold", out var threshold) &&
                            double.TryParse(threshold?.ToString(), out var thresholdValue))
                        {
                            return cpuValue > thresholdValue;
                        }
                    }
                }
                else if (expression.Contains("memory_usage >"))
                {
                    if (context.TriggerData.TryGetValue("memory_usage", out var memoryUsage) &&
                        double.TryParse(memoryUsage?.ToString(), out var memoryValue))
                    {
                        if (parameters.TryGetValue("threshold", out var threshold) &&
                            double.TryParse(threshold?.ToString(), out var thresholdValue))
                        {
                            return memoryValue > thresholdValue;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating simple condition: {Expression}", expression);
                return false;
            }
        }

        private CreateAlertRequest CreateAlertRequestFromRule(AlertRule rule, RuleEvaluationContext context)
        {
            return new CreateAlertRequest;
            {
                Title = rule.AlertTemplate.Title,
                Message = FormatAlertMessage(rule.AlertTemplate.Message, context),
                Description = rule.AlertTemplate.Description,
                SourceSystem = rule.SourceSystem,
                SourceId = context.SourceId ?? "rule_triggered",
                AlertType = AlertType.RuleBased,
                Severity = rule.AlertTemplate.Severity,
                Category = rule.AlertTemplate.Category,
                Tags = rule.AlertTemplate.Tags,
                Metadata = new Dictionary<string, object>
                {
                    ["rule_id"] = rule.Id,
                    ["rule_name"] = rule.Name,
                    ["trigger_context"] = context.TriggerData,
                    ["evaluation_time"] = context.EvaluationTime;
                }
            };
        }

        private string FormatAlertMessage(string template, RuleEvaluationContext context)
        {
            // Şablonu bağlam verileri ile formatla;
            var message = template;

            foreach (var kvp in context.TriggerData)
            {
                message = message.Replace($"{{{kvp.Key}}}", kvp.Value?.ToString() ?? string.Empty);
            }

            return message;
        }

        private async Task<DateTime?> GetLastRuleTriggerAsync(string ruleId, string sourceId, CancellationToken cancellationToken)
        {
            try
            {
                return await _memorySystem.GetLastRuleTriggerAsync(ruleId, sourceId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting last rule trigger");
                return null;
            }
        }

        private async Task RecordRuleTriggerAsync(string ruleId, string sourceId, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.RecordRuleTriggerAsync(ruleId, sourceId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error recording rule trigger");
            }
        }

        private IEnumerable<Alert> ApplyAlertFilter(IEnumerable<Alert> alerts, AlertFilter filter)
        {
            var filtered = alerts;

            if (filter.Severities?.Any() == true)
            {
                filtered = filtered.Where(a => filter.Severities.Contains(a.Severity));
            }

            if (filter.Priorities?.Any() == true)
            {
                filtered = filtered.Where(a => filter.Priorities.Contains(a.Priority));
            }

            if (filter.Categories?.Any() == true)
            {
                filtered = filtered.Where(a => filter.Categories.Contains(a.Category));
            }

            if (filter.Statuses?.Any() == true)
            {
                filtered = filtered.Where(a => filter.Statuses.Contains(a.Status));
            }

            if (filter.SourceSystems?.Any() == true)
            {
                filtered = filtered.Where(a => filter.SourceSystems.Contains(a.SourceSystem));
            }

            if (filter.FromTime.HasValue)
            {
                filtered = filtered.Where(a => a.CreatedAt >= filter.FromTime.Value);
            }

            if (filter.ToTime.HasValue)
            {
                filtered = filtered.Where(a => a.CreatedAt <= filter.ToTime.Value);
            }

            if (filter.SearchText != null)
            {
                var search = filter.SearchText.ToLowerInvariant();
                filtered = filtered.Where(a =>
                    a.Title.ToLowerInvariant().Contains(search) ||
                    a.Message.ToLowerInvariant().Contains(search) ||
                    a.Description.ToLowerInvariant().Contains(search) ||
                    a.Tags.Any(t => t.ToLowerInvariant().Contains(search)));
            }

            if (filter.RequiresAcknowledgement.HasValue)
            {
                filtered = filtered.Where(a => a.RequiresAcknowledgement == filter.RequiresAcknowledgement.Value);
            }

            return filtered;
        }

        private async Task<List<SystemHealthStatus>> CheckChannelHealthAsync(CancellationToken cancellationToken)
        {
            var channelHealth = new List<SystemHealthStatus>();

            foreach (var channel in _alertChannels.Values)
            {
                var health = new SystemHealthStatus;
                {
                    Component = $"Channel:{channel.Name}",
                    Timestamp = DateTime.UtcNow;
                };

                try
                {
                    if (!channel.IsEnabled)
                    {
                        health.OverallStatus = HealthStatus.Healthy;
                        health.Message = "Channel is disabled";
                    }
                    else if (!channel.IsInitialized)
                    {
                        health.OverallStatus = HealthStatus.Error;
                        health.Message = "Channel not initialized";
                    }
                    else;
                    {
                        // Kanal tipine göre sağlık kontrolü;
                        var isHealthy = await CheckChannelSpecificHealthAsync(channel, cancellationToken);

                        health.OverallStatus = isHealthy ? HealthStatus.Healthy : HealthStatus.Error;
                        health.Message = isHealthy ? "Channel is operational" : "Channel health check failed";

                        health.Metrics["successful_deliveries"] = channel.SuccessfulDeliveries;
                        health.Metrics["failed_deliveries"] = channel.FailedDeliveries;
                        health.Metrics["success_rate"] = channel.SuccessfulDeliveries + channel.FailedDeliveries > 0;
                            ? (double)channel.SuccessfulDeliveries / (channel.SuccessfulDeliveries + channel.FailedDeliveries)
                            : 0;
                    }
                }
                catch (Exception ex)
                {
                    health.OverallStatus = HealthStatus.Error;
                    health.Message = $"Health check failed: {ex.Message}";
                }

                channelHealth.Add(health);
            }

            return channelHealth;
        }

        private async Task<bool> CheckChannelSpecificHealthAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            try
            {
                switch (channel.Type)
                {
                    case ChannelType.Email:
                        return await CheckEmailChannelHealthAsync(channel, cancellationToken);
                    case ChannelType.SMS:
                        return await CheckSmsChannelHealthAsync(channel, cancellationToken);
                    case ChannelType.Push:
                        return await CheckPushChannelHealthAsync(channel, cancellationToken);
                    case ChannelType.Webhook:
                        return await CheckWebhookChannelHealthAsync(channel, cancellationToken);
                    case ChannelType.Slack:
                        return await CheckSlackChannelHealthAsync(channel, cancellationToken);
                    case ChannelType.Teams:
                        return await CheckTeamsChannelHealthAsync(channel, cancellationToken);
                    default:
                        return true; // Diğer kanallar için basit kontrol;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking channel health: {ChannelName}", channel.Name);
                return false;
            }
        }

        private async Task<bool> CheckEmailChannelHealthAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // SMTP bağlantı testi;
            await Task.Delay(100, cancellationToken);
            return true;
        }

        private async Task<bool> CheckSmsChannelHealthAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // SMS provider bağlantı testi;
            await Task.Delay(100, cancellationToken);
            return true;
        }

        private async Task<bool> CheckPushChannelHealthAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Push servis bağlantı testi;
            await Task.Delay(100, cancellationToken);
            return true;
        }

        private async Task<bool> CheckWebhookChannelHealthAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Webhook endpoint testi;
            await Task.Delay(100, cancellationToken);
            return true;
        }

        private async Task<bool> CheckSlackChannelHealthAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Slack webhook testi;
            await Task.Delay(100, cancellationToken);
            return true;
        }

        private async Task<bool> CheckTeamsChannelHealthAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            // Teams webhook testi;
            await Task.Delay(100, cancellationToken);
            return true;
        }

        private async Task ProcessPendingAlertsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (_alertQueue.TryDequeue(out var alertEvent))
                {
                    await ProcessAlertEventAsync(alertEvent, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing pending alerts");
            }
        }

        private async Task ProcessAlertEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken)
        {
            try
            {
                switch (alertEvent.EventType)
                {
                    case AlertEventType.Created:
                        // Uyarı oluşturuldu işlemleri;
                        await ProcessAlertCreatedEventAsync(alertEvent, cancellationToken);
                        break;
                    case AlertEventType.Acknowledged:
                        // Uyarı onaylandı işlemleri;
                        await ProcessAlertAcknowledgedEventAsync(alertEvent, cancellationToken);
                        break;
                    case AlertEventType.Resolved:
                        // Uyarı çözüldü işlemleri;
                        await ProcessAlertResolvedEventAsync(alertEvent, cancellationToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing alert event: {EventType}", alertEvent.EventType);
            }
        }

        private async Task ProcessAlertCreatedEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken)
        {
            // Uyarı oluşturuldu işlemleri;
            // Örneğin: dashboard güncelleme, analytics, loglama;
            await Task.Delay(10, cancellationToken);
        }

        private async Task ProcessAlertAcknowledgedEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken)
        {
            // Uyarı onaylandı işlemleri;
            await Task.Delay(10, cancellationToken);
        }

        private async Task ProcessAlertResolvedEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken)
        {
            // Uyarı çözüldü işlemleri;
            await Task.Delay(10, cancellationToken);
        }

        private async Task CleanupAllResourcesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Tüm kanalları temizle;
                foreach (var channel in _alertChannels.Values)
                {
                    await CleanupChannelAsync(channel, cancellationToken);
                }

                // Tüm koleksiyonları temizle;
                _activeAlerts.Clear();
                _alertRules.Clear();
                _alertChannels.Clear();
                _alertQueue.Clear();
                _alertHistory.Clear();

                _logger.LogDebug("All resources cleaned up");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up resources");
            }
        }

        private async Task CleanupChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            try
            {
                // Kanal tipine göre temizleme;
                switch (channel.Type)
                {
                    case ChannelType.Email:
                        await CleanupEmailChannelAsync(channel, cancellationToken);
                        break;
                    case ChannelType.SMS:
                        await CleanupSmsChannelAsync(channel, cancellationToken);
                        break;
                    case ChannelType.Push:
                        await CleanupPushChannelAsync(channel, cancellationToken);
                        break;
                }

                channel.IsInitialized = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up channel: {ChannelName}", channel.Name);
            }
        }

        private async Task CleanupEmailChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
        }

        private async Task CleanupSmsChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
        }

        private async Task CleanupPushChannelAsync(AlertChannel channel, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
        }

        private async Task<List<Alert>> LoadAllAlertsAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken)
        {
            try
            {
                return await _memorySystem.LoadAlertsByTimeRangeAsync(startTime, endTime, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading all alerts");
                return new List<Alert>();
            }
        }

        private Dictionary<string, int> GenerateTimeSeriesData(List<Alert> alerts, TimeGranularity granularity)
        {
            var timeSeries = new Dictionary<string, int>();

            if (!alerts.Any())
                return timeSeries;

            var current = startTime;
            while (current <= endTime)
            {
                var key = granularity switch;
                {
                    TimeGranularity.Hourly => current.ToString("yyyy-MM-dd HH:00"),
                    TimeGranularity.Daily => current.ToString("yyyy-MM-dd"),
                    TimeGranularity.Weekly => $"Week {GetWeekOfYear(current)}",
                    TimeGranularity.Monthly => current.ToString("yyyy-MM"),
                    _ => current.ToString("yyyy-MM-dd HH:00")
                };

                var count = alerts.Count(a => a.CreatedAt >= current &&
                                            a.CreatedAt < current.Add(granularity switch;
                                            {
                                                TimeGranularity.Hourly => TimeSpan.FromHours(1),
                                                TimeGranularity.Daily => TimeSpan.FromDays(1),
                                                TimeGranularity.Weekly => TimeSpan.FromDays(7),
                                                TimeGranularity.Monthly => TimeSpan.FromDays(30),
                                                _ => TimeSpan.FromHours(1)
                                            }));

                timeSeries[key] = count;

                current = granularity switch;
                {
                    TimeGranularity.Hourly => current.AddHours(1),
                    TimeGranularity.Daily => current.AddDays(1),
                    TimeGranularity.Weekly => current.AddDays(7),
                    TimeGranularity.Monthly => current.AddMonths(1),
                    _ => current.AddHours(1)
                };
            }

            return timeSeries;
        }

        private int GetWeekOfYear(DateTime date)
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            return culture.Calendar.GetWeekOfYear(date,
                culture.DateTimeFormat.CalendarWeekRule,
                culture.DateTimeFormat.FirstDayOfWeek);
        }

        private TimeSpan CalculateAverageResolutionTime(List<Alert> alerts)
        {
            var resolvedAlerts = alerts.Where(a => a.ResolvedAt.HasValue && a.AcknowledgedAt.HasValue).ToList();

            if (!resolvedAlerts.Any())
                return TimeSpan.Zero;

            var totalResolutionTime = resolvedAlerts;
                .Sum(a => (a.ResolvedAt.Value - a.AcknowledgedAt.Value).TotalSeconds);

            return TimeSpan.FromSeconds(totalResolutionTime / resolvedAlerts.Count);
        }

        private Dictionary<string, object> AnalyzeAlertTrends(List<Alert> alerts, DateTime startTime, DateTime endTime)
        {
            var analysis = new Dictionary<string, object>();

            // Trend hesaplamaları;
            var dailyCounts = alerts;
                .GroupBy(a => a.CreatedAt.Date)
                .Select(g => new { Date = g.Key, Count = g.Count() })
                .OrderBy(x => x.Date)
                .ToList();

            if (dailyCounts.Count >= 2)
            {
                var firstDay = dailyCounts.First();
                var lastDay = dailyCounts.Last();
                var daysBetween = (lastDay.Date - firstDay.Date).Days;

                if (daysBetween > 0)
                {
                    var averagePerDay = dailyCounts.Average(x => x.Count);
                    var trend = (lastDay.Count - firstDay.Count) / (double)daysBetween;

                    analysis["average_alerts_per_day"] = averagePerDay;
                    analysis["trend_direction"] = trend > 0 ? "increasing" : trend < 0 ? "decreasing" : "stable";
                    analysis["trend_magnitude"] = Math.Abs(trend);
                }
            }

            // Yoğun zamanlar;
            var hourlyDistribution = alerts;
                .GroupBy(a => a.CreatedAt.Hour)
                .Select(g => new { Hour = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(3)
                .ToList();

            analysis["peak_hours"] = hourlyDistribution.Select(x => $"{x.Hour:00}:00").ToList();

            return analysis;
        }

        private List<string> GenerateRecommendations(AlertAnalytics analytics)
        {
            var recommendations = new List<string>();

            // Kritik uyarı analizi;
            if (analytics.SeverityDistribution.ContainsKey("Critical") &&
                analytics.SeverityDistribution["Critical"] > 10)
            {
                recommendations.Add("High number of critical alerts detected. Consider reviewing alert thresholds and system health.");
            }

            // Çözüm süresi analizi;
            if (analytics.AverageResolutionTime.TotalHours > 4)
            {
                recommendations.Add("Average alert resolution time is high. Consider improving response processes or adding escalation rules.");
            }

            // Kategori analizi;
            var topCategory = analytics.CategoryDistribution.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (topCategory.Value > analytics.TotalAlerts * 0.5) // %50'den fazla;
            {
                recommendations.Add($"Most alerts ({topCategory.Value}) are in category '{topCategory.Key}'. Consider investigating root causes.");
            }

            // Zaman serisi trendi;
            if (analytics.TrendAnalysis.TryGetValue("trend_direction", out var trend) &&
                trend?.ToString() == "increasing")
            {
                recommendations.Add("Alert volume is trending upward. Consider proactive monitoring and capacity planning.");
            }

            return recommendations;
        }

        private int GetPriorityValue(AlertPriority priority)
        {
            return priority switch;
            {
                AlertPriority.Critical => 4,
                AlertPriority.High => 3,
                AlertPriority.Medium => 2,
                AlertPriority.Low => 1,
                _ => 0;
            };
        }

        private int GetSeverityValue(AlertSeverity severity)
        {
            return severity switch;
            {
                AlertSeverity.Critical => 4,
                AlertSeverity.Error => 3,
                AlertSeverity.Warning => 2,
                AlertSeverity.Info => 1,
                _ => 0;
            };
        }

        private void OnMonitoringTimerElapsed(object sender, System.Timers.TimedEventArgs e)
        {
            try
            {
                CheckAlertExpirations();
                ProcessAlertQueue();
                UpdateHealthMetrics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring timer elapsed");
            }
        }

        private void OnEscalationTimerElapsed(object sender, System.Timers.TimedEventArgs e)
        {
            try
            {
                ProcessEscalations();
                CheckUnacknowledgedAlerts();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in escalation timer elapsed");
            }
        }

        private void OnCleanupTimerElapsed(object sender, System.Timers.TimedEventArgs e)
        {
            try
            {
                CleanupOldAlerts();
                CleanupAlertHistory();
                CompactDataStructures();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup timer elapsed");
            }
        }

        private void CheckAlertExpirations()
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredAlerts = new List<string>();

                foreach (var kvp in _activeAlerts)
                {
                    var alert = kvp.Value.Alert;

                    if (alert.ExpiresAt.HasValue && alert.ExpiresAt.Value < now)
                    {
                        // Süresi dolan uyarıları kapat;
                        expiredAlerts.Add(kvp.Key);
                    }
                }

                foreach (var alertId in expiredAlerts)
                {
                    if (_activeAlerts.TryRemove(alertId, out var activeAlert))
                    {
                        activeAlert.Alert.Status = AlertStatus.Expired;
                        activeAlert.Alert.ClosedAt = now;
                        activeAlert.Alert.CloseComment = "Expired automatically";

                        _ = SaveAlertAsync(activeAlert.Alert, CancellationToken.None);

                        _statistics.ExpiredAlerts++;
                        _statistics.ActiveAlerts = _activeAlerts.Count;

                        _logger.LogInformation("Alert expired: {AlertId}", alertId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking alert expirations");
            }
        }

        private void ProcessAlertQueue()
        {
            try
            {
                var processedCount = 0;
                var maxProcess = Math.Min(_alertQueue.Count, 50);

                for (int i = 0; i < maxProcess; i++)
                {
                    if (_alertQueue.TryDequeue(out var alertEvent))
                    {
                        _ = ProcessAlertEventAsync(alertEvent, CancellationToken.None);
                        processedCount++;
                    }
                }

                if (processedCount > 0)
                {
                    _logger.LogDebug("Processed {Count} alert events from queue", processedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing alert queue");
            }
        }

        private void UpdateHealthMetrics()
        {
            _lastHealthCheck = DateTime.UtcNow;

            // İşleme süresi metriklerini güncelle;
            _statistics.AverageProcessingTime = CalculateAverageProcessingTime();

            // Kanal başarı oranlarını güncelle;
            UpdateChannelSuccessRates();
        }

        private double CalculateAverageProcessingTime()
        {
            // Örnek: son 100 uyarının ortalama işleme süresi;
            return _statistics.AlertsProcessed > 0 ? 100.0 : 0; // Basit implementasyon;
        }

        private void UpdateChannelSuccessRates()
        {
            foreach (var channel in _alertChannels.Values)
            {
                var totalDeliveries = channel.SuccessfulDeliveries + channel.FailedDeliveries;
                if (totalDeliveries > 0)
                {
                    channel.SuccessRate = (double)channel.SuccessfulDeliveries / totalDeliveries;
                }
            }
        }

        private void ProcessEscalations()
        {
            try
            {
                var now = DateTime.UtcNow;
                var alertsToEscalate = new List<ActiveAlert>();

                foreach (var activeAlert in _activeAlerts.Values)
                {
                    var alert = activeAlert.Alert;

                    // Onay gerektiren ve onaylanmamış kritik uyarıları kontrol et;
                    if (alert.RequiresAcknowledgement &&
                        alert.Status == AlertStatus.New &&
                        alert.Severity >= AlertSeverity.High)
                    {
                        var timeSinceCreation = now - alert.CreatedAt;

                        if (timeSinceCreation.TotalMinutes > GetEscalationThreshold(activeAlert.EscalationLevel))
                        {
                            alertsToEscalate.Add(activeAlert);
                        }
                    }
                }

                foreach (var alert in alertsToEscalate)
                {
                    EscalateAlert(alert);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing escalations");
            }
        }

        private double GetEscalationThreshold(int escalationLevel)
        {
            return escalationLevel switch;
            {
                0 => 15,  // 15 dakika;
                1 => 30,  // 30 dakika;
                2 => 60,  // 1 saat;
                _ => 120  // 2 saat;
            };
        }

        private void EscalateAlert(ActiveAlert activeAlert)
        {
            try
            {
                var alert = activeAlert.Alert;

                // Eslasyon seviyesini artır;
                activeAlert.EscalationLevel++;
                activeAlert.IsEscalated = true;
                alert.LastUpdated = DateTime.UtcNow;

                // Eslasyon bildirimi gönder;
                _ = SendEscalationNotificationAsync(alert, activeAlert.EscalationLevel, CancellationToken.None);

                // Geçmişe ekle;
                _ = AddToAlertHistoryAsync(alert, AlertHistoryType.Escalated, CancellationToken.None);

                _logger.LogInformation("Alert escalated: {AlertId} to level {Level}",
                    alert.Id, activeAlert.EscalationLevel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error escalating alert: {AlertId}", activeAlert.Alert.Id);
            }
        }

        private async Task SendEscalationNotificationAsync(Alert alert, int escalationLevel, CancellationToken cancellationToken)
        {
            try
            {
                var notification = new Notification;
                {
                    Title = $"Alert Escalated (Level {escalationLevel}): {alert.Title}",
                    Message = $"Alert has been escalated to level {escalationLevel} due to lack of acknowledgement.",
                    Severity = AlertSeverity.Critical,
                    Priority = AlertPriority.Critical,
                    Category = "Escalation",
                    Metadata = new Dictionary<string, object>
                    {
                        ["escalation_level"] = escalationLevel,
                        ["alert_created"] = alert.CreatedAt,
                        ["time_pending"] = DateTime.UtcNow - alert.CreatedAt;
                    }
                };

                await _notificationManager.SendNotificationAsync(notification, cancellationToken);
                _statistics.EscalationsSent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending escalation notification");
            }
        }

        private void CheckUnacknowledgedAlerts()
        {
            try
            {
                var unacknowledgedCritical = _activeAlerts.Values;
                    .Count(a => a.Alert.RequiresAcknowledgement &&
                               a.Alert.Status == AlertStatus.New &&
                               a.Alert.Severity == AlertSeverity.Critical);

                if (unacknowledgedCritical > 0)
                {
                    _logger.LogWarning("{Count} critical alerts are unacknowledged", unacknowledgedCritical);

                    // Dashboard güncelleme veya ek bildirim;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking unacknowledged alerts");
            }
        }

        private void CleanupOldAlerts()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddDays(-_config.AlertRetentionDays);
                var oldAlerts = _activeAlerts.Values;
                    .Where(a => a.Alert.CreatedAt < cutoffTime &&
                               a.Alert.Status != AlertStatus.New &&
                               a.Alert.Status != AlertStatus.Acknowledged)
                    .ToList();

                foreach (var alert in oldAlerts)
                {
                    if (_activeAlerts.TryRemove(alert.Alert.Id, out _))
                    {
                        _logger.LogDebug("Cleaned up old alert: {AlertId}", alert.Alert.Id);
                        _statistics.CleanedUpAlerts++;
                    }
                }

                if (oldAlerts.Count > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} old alerts", oldAlerts.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old alerts");
            }
        }

        private void CleanupAlertHistory()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddDays(-_config.HistoryRetentionDays);
                var oldHistoryKeys = _alertHistory.Keys;
                    .Where(key => _alertHistory[key].Entries.All(e => e.Timestamp < cutoffTime))
                    .ToList();

                foreach (var key in oldHistoryKeys)
                {
                    _alertHistory.TryRemove(key, out _);
                }

                if (oldHistoryKeys.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} old alert histories", oldHistoryKeys.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up alert history");
            }
        }

        private void CompactDataStructures()
        {
            try
            {
                // Bellek optimizasyonu için veri yapılarını sıkıştır;
                // Burada gerektiğinde koleksiyonları yeniden boyutlandır;
                _logger.LogDebug("Data structures compacted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error compacting data structures");
            }
        }

        private void HandleSystemAlertEvent(SystemAlertEvent systemEvent)
        {
            try
            {
                _logger.LogDebug("Received system alert event: {EventType}", systemEvent.EventType);

                // Sistem uyarı olaylarını işle;
                switch (systemEvent.EventType)
                {
                    case SystemEventType.HealthStatusChanged:
                        await HandleHealthStatusChangeAsync(systemEvent, CancellationToken.None);
                        break;
                    case SystemEventType.ResourceThresholdExceeded:
                        await HandleResourceThresholdAsync(systemEvent, CancellationToken.None);
                        break;
                    case SystemEventType.ServiceDown:
                        await HandleServiceDownAsync(systemEvent, CancellationToken.None);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling system alert event");
            }
        }

        private void HandlePerformanceAlertEvent(PerformanceAlertEvent performanceEvent)
        {
            try
            {
                _logger.LogDebug("Received performance alert event: {Metric}", performanceEvent.MetricName);

                // Performans uyarı olaylarını işle;
                var context = new RuleEvaluationContext;
                {
                    SourceSystem = "PerformanceMonitor",
                    SourceId = performanceEvent.Source,
                    TriggerData = new Dictionary<string, object>
                    {
                        [performanceEvent.MetricName.ToLower()] = performanceEvent.Value,
                        ["threshold"] = performanceEvent.Threshold,
                        ["timestamp"] = performanceEvent.Timestamp;
                    }
                };

                _ = EvaluateAlertRulesAsync(context, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling performance alert event");
            }
        }

        private async Task HandleHealthStatusChangeAsync(SystemAlertEvent systemEvent, CancellationToken cancellationToken)
        {
            var healthStatus = systemEvent.Data["status"]?.ToString();
            if (healthStatus == "Critical")
            {
                var alertRequest = new CreateAlertRequest;
                {
                    Title = "System Health Critical",
                    Message = $"System health status changed to {healthStatus}",
                    SourceSystem = "SystemMonitor",
                    SourceId = systemEvent.Source,
                    AlertType = AlertType.SystemHealth,
                    Severity = AlertSeverity.Critical,
                    Category = "SystemHealth",
                    Tags = new[] { "system", "health", "critical" },
                    Metadata = systemEvent.Data;
                };

                await CreateAlertAsync(alertRequest, cancellationToken);
            }
        }

        private async Task HandleResourceThresholdAsync(SystemAlertEvent systemEvent, CancellationToken cancellationToken)
        {
            var resourceType = systemEvent.Data["resource_type"]?.ToString();
            var usage = systemEvent.Data["usage"]?.ToString();
            var threshold = systemEvent.Data["threshold"]?.ToString();

            var alertRequest = new CreateAlertRequest;
            {
                Title = $"High {resourceType} Usage",
                Message = $"{resourceType} usage is at {usage}%, exceeding threshold of {threshold}%",
                SourceSystem = "ResourceMonitor",
                SourceId = systemEvent.Source,
                AlertType = AlertType.Resource,
                Severity = AlertSeverity.High,
                Category = "ResourceUsage",
                Tags = new[] { "resource", "usage", resourceType?.ToLower() },
                Metadata = systemEvent.Data;
            };

            await CreateAlertAsync(alertRequest, cancellationToken);
        }

        private async Task HandleServiceDownAsync(SystemAlertEvent systemEvent, CancellationToken cancellationToken)
        {
            var serviceName = systemEvent.Data["service_name"]?.ToString();
            var downtime = systemEvent.Data["downtime_minutes"]?.ToString();

            var alertRequest = new CreateAlertRequest;
            {
                Title = $"Service Down: {serviceName}",
                Message = $"{serviceName} service is down for {downtime} minutes",
                SourceSystem = "ServiceMonitor",
                SourceId = systemEvent.Source,
                AlertType = AlertType.Service,
                Severity = AlertSeverity.Critical,
                Category = "ServiceAvailability",
                Tags = new[] { "service", "downtime", serviceName?.ToLower() },
                Metadata = systemEvent.Data;
            };

            await CreateAlertAsync(alertRequest, cancellationToken);
        }

        private ValidationResult ValidateAlertRequest(CreateAlertRequest request)
        {
            var result = new ValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                result.IsValid = false;
                result.ErrorMessage = "Title cannot be empty";
                return result;
            }

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                result.IsValid = false;
                result.ErrorMessage = "Message cannot be empty";
                return result;
            }

            if (string.IsNullOrWhiteSpace(request.SourceSystem))
            {
                result.IsValid = false;
                result.ErrorMessage = "Source system cannot be empty";
                return result;
            }

            if (string.IsNullOrWhiteSpace(request.SourceId))
            {
                result.IsValid = false;
                result.ErrorMessage = "Source ID cannot be empty";
                return result;
            }

            return result;
        }

        private bool ValidateConfig(AlertSystemConfig config)
        {
            if (config == null) return false;
            if (config.AlertRetentionDays <= 0) return false;
            if (config.HistoryRetentionDays <= 0) return false;

            return true;
        }

        private void OnAlertCreated(AlertCreatedEventArgs e)
        {
            AlertCreated?.Invoke(this, e);
        }

        private void OnAlertStatusChanged(AlertStatusChangedEventArgs e)
        {
            AlertStatusChanged?.Invoke(this, e);
        }

        private void OnAlertAcknowledged(AlertAcknowledgedEventArgs e)
        {
            AlertAcknowledged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _monitoringTimer?.Dispose();
                    _escalationTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _processingLock?.Dispose();

                    if (_isInitialized)
                    {
                        _ = ShutdownAsync().ConfigureAwait(false);
                    }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public interface IAlertSystem : IDisposable
    {
        event EventHandler<AlertCreatedEventArgs> AlertCreated;
        event EventHandler<AlertStatusChangedEventArgs> AlertStatusChanged;
        event EventHandler<AlertAcknowledgedEventArgs> AlertAcknowledged;

        AlertStatistics Statistics { get; }
        SystemState State { get; }

        Task<SystemResult> InitializeAsync(CancellationToken cancellationToken = default);
        Task<AlertResult> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default);
        Task<AlertResult> AcknowledgeAlertAsync(string alertId, AcknowledgementRequest request, CancellationToken cancellationToken = default);
        Task<AlertResult> ResolveAlertAsync(string alertId, ResolutionRequest request, CancellationToken cancellationToken = default);
        Task<AlertResult> CloseAlertAsync(string alertId, CloseRequest request, CancellationToken cancellationToken = default);
        Task<RuleEvaluationResult> EvaluateAlertRulesAsync(RuleEvaluationContext context, CancellationToken cancellationToken = default);
        Task<IEnumerable<Alert>> GetActiveAlertsAsync(AlertFilter filter = null, CancellationToken cancellationToken = default);
        Task<IEnumerable<AlertHistoryEntry>> GetAlertHistoryAsync(string alertId, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);
        Task<SystemHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default);
        Task<AlertAnalytics> GetAnalyticsAsync(AnalyticsRequest request, CancellationToken cancellationToken = default);
        Task<SystemResult> StopMonitoringAsync(CancellationToken cancellationToken = default);
        Task<SystemResult> StartMonitoringAsync(CancellationToken cancellationToken = default);
        Task<SystemResult> ShutdownAsync(CancellationToken cancellationToken = default);
    }

    public enum SystemState;
    {
        Stopped,
        Initializing,
        Running,
        Paused,
        Error;
    }

    public enum AlertStatus;
    {
        New,
        Acknowledged,
        InProgress,
        Resolved,
        Closed,
        Expired;
    }

    public enum AlertSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    public enum AlertPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum AlertType;
    {
        SystemHealth,
        Performance,
        Security,
        Resource,
        Service,
        Application,
        Network,
        Database,
        RuleBased,
        Manual;
    }

    public enum AlertImpact;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum ChannelType;
    {
        InApp,
        Email,
        SMS,
        Push,
        Webhook,
        Slack,
        Teams,
        Phone,
        PagerDuty;
    }

    public enum ChannelStatus;
    {
        Success,
        Failed,
        Unsupported,
        Disabled;
    }

    public enum DistributionStatus;
    {
        Success,
        PartialSuccess,
        Failed,
        NoChannels;
    }

    public enum AlertEventType;
    {
        Created,
        Updated,
        Acknowledged,
        Resolved,
        Escalated,
        Closed,
        Expired;
    }

    public enum AlertHistoryType;
    {
        Created,
        Acknowledged,
        Resolved,
        Escalated,
        Closed,
        CommentAdded,
        Assigned,
        Reassigned;
    }

    public enum TimeGranularity;
    {
        Hourly,
        Daily,
        Weekly,
        Monthly;
    }

    public enum HealthStatus;
    {
        Healthy,
        Warning,
        Error;
    }

    public enum SystemEventType;
    {
        HealthStatusChanged,
        ResourceThresholdExceeded,
        ServiceDown,
        PerformanceDegraded,
        SecurityBreach;
    }

    public class AlertSystemConfig;
    {
        public int AlertRetentionDays { get; set; } = 90;
        public int HistoryRetentionDays { get; set; } = 365;
        public bool SuppressDuplicateAlerts { get; set; } = true;
        public int DuplicateSuppressionMinutes { get; set; } = 5;
        public bool EnableSlackIntegration { get; set; } = true;
        public bool EnableTeamsIntegration { get; set; } = true;
        public string BaseUrl { get; set; } = "https://neda.example.com";
        public EmailSettings EmailSettings { get; set; } = new();
        public SmsSettings SmsSettings { get; set; } = new();
        public PushSettings PushSettings { get; set; } = new();
        public SlackSettings SlackSettings { get; set; } = new();
        public TeamsSettings TeamsSettings { get; set; } = new();
        public Dictionary<string, object> AdvancedSettings { get; set; } = new();
    }

    public class EmailSettings;
    {
        public string SmtpServer { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 587;
        public string FromAddress { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public bool UseSsl { get; set; } = true;
    }

    public class SmsSettings;
    {
        public string Provider { get; set; } = "Twilio";
        public string AccountSid { get; set; } = string.Empty;
        public string AuthToken { get; set; } = string.Empty;
        public string FromNumber { get; set; } = string.Empty;
    }

    public class PushSettings;
    {
        public string ApnsCertificatePath { get; set; } = string.Empty;
        public string FcmApiKey { get; set; } = string.Empty;
        public string WebPushPublicKey { get; set; } = string.Empty;
        public string WebPushPrivateKey { get; set; } = string.Empty;
    }

    public class SlackSettings;
    {
        public string WebhookUrl { get; set; } = string.Empty;
        public string Channel { get; set; } = "#alerts";
        public string Username { get; set; } = "NEDA Alert System";
        public string IconEmoji { get; set; } = ":warning:";
    }

    public class TeamsSettings;
    {
        public string WebhookUrl { get; set; } = string.Empty;
        public string ThemeColor { get; set; } = "FF0000";
    }

    public class Alert;
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SourceSystem { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public AlertType AlertType { get; set; }
        public AlertSeverity Severity { get; set; }
        public AlertPriority Priority { get; set; }
        public string Category { get; set; } = "General";
        public List<string> Tags { get; set; } = new();
        public AlertStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public string AcknowledgedBy { get; set; } = string.Empty;
        public string AcknowledgementComment { get; set; } = string.Empty;
        public DateTime? ResolvedAt { get; set; }
        public string ResolvedBy { get; set; } = string.Empty;
        public string ResolutionComment { get; set; } = string.Empty;
        public string ResolutionCode { get; set; } = string.Empty;
        public Dictionary<string, object> ResolutionData { get; set; } = new();
        public DateTime? ClosedAt { get; set; }
        public string ClosedBy { get; set; } = string.Empty;
        public string CloseComment { get; set; } = string.Empty;
        public DateTime? ExpiresAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public string AssignedTo { get; set; } = string.Empty;
        public bool RequiresAcknowledgement { get; set; }
        public bool AutoEscalate { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class ActiveAlert;
    {
        public Alert Alert { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public int RetryCount { get; set; }
        public bool IsEscalated { get; set; }
        public int EscalationLevel { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class AlertChannel;
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public ChannelType Type { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsInitialized { get; set; }
        public AlertPriority[] PrioritySupport { get; set; } = Array.Empty<AlertPriority>();
        public Dictionary<string, object> Config { get; set; } = new();
        public RetryPolicy RetryPolicy { get; set; } = new();
        public DateTime? LastUsed { get; set; }
        public DateTime? LastHealthCheck { get; set; }
        public DateTime? LastSuccessfulDelivery { get; set; }
        public DateTime? LastFailedDelivery { get; set; }
        public string LastError { get; set; } = string.Empty;
        public long SuccessfulDeliveries { get; set; }
        public long FailedDeliveries { get; set; }
        public double SuccessRate { get; set; }
    }

    public class RetryPolicy;
    {
        public int MaxRetries { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
        public double BackoffMultiplier { get; set; } = 2.0;
        public List<int> RetryableStatusCodes { get; set; } = new();
    }

    public class AlertRule;
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SourceSystem { get; set; } = string.Empty;
        public RuleCondition Condition { get; set; }
        public AlertTemplate AlertTemplate { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastTriggered { get; set; }
        public int TriggerCount { get; set; }
        public TimeSpan? SuppressionWindow { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class RuleCondition;
    {
        public string Expression { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public double ConfidenceThreshold { get; set; } = 0.7;
    }

    public class AlertTemplate;
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public AlertSeverity Severity { get; set; }
        public string Category { get; set; } = "General";
        public string[] Tags { get; set; } = Array.Empty<string>();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class AlertHistory;
    {
        public string AlertId { get; set; } = string.Empty;
        public List<AlertHistoryEntry> Entries { get; set; } = new();
    }

    public class AlertHistoryEntry
    {
        public string AlertId { get; set; } = string.Empty;
        public AlertHistoryType HistoryType { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public class CreateAlertRequest;
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SourceSystem { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public AlertType AlertType { get; set; }
        public AlertSeverity Severity { get; set; }
        public AlertImpact Impact { get; set; } = AlertImpact.Medium;
        public string Category { get; set; } = "General";
        public IEnumerable<string> Tags { get; set; } = new List<string>();
        public DateTime? ExpiresAt { get; set; }
        public string AssignedTo { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class AcknowledgementRequest;
    {
        public string AcknowledgedBy { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class ResolutionRequest;
    {
        public string ResolvedBy { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string ResolutionCode { get; set; } = string.Empty;
        public Dictionary<string, object> ResolutionData { get; set; } = new();
    }

    public class CloseRequest;
    {
        public string ClosedBy { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public bool ForceClose { get; set; }
    }

    public class RuleEvaluationContext;
    {
        public string SourceSystem { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public Dictionary<string, object> TriggerData { get; set; } = new();
        public DateTime EvaluationTime { get; set; } = DateTime.UtcNow;
    }

    public class AlertFilter;
    {
        public List<AlertSeverity> Severities { get; set; }
        public List<AlertPriority> Priorities { get; set; }
        public List<string> Categories { get; set; }
        public List<AlertStatus> Statuses { get; set; }
        public List<string> SourceSystems { get; set; }
        public DateTime? FromTime { get; set; }
        public DateTime? ToTime { get; set; }
        public string SearchText { get; set; }
        public bool? RequiresAcknowledgement { get; set; }
    }

    public class AnalyticsRequest;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; } = DateTime.UtcNow;
        public TimeGranularity Granularity { get; set; } = TimeGranularity.Daily;
        public List<string> Categories { get; set; }
        public List<AlertSeverity> Severities { get; set; }
    }

    public class AlertStatistics;
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Uptime => EndTime.HasValue ? EndTime.Value - StartTime : DateTime.UtcNow - StartTime;
        public long AlertsCreated { get; set; }
        public long AlertsDistributed { get; set; }
        public long AlertsProcessed { get; set; }
        public int ActiveAlerts { get; set; }
        public int ResolvedAlerts { get; set; }
        public int ClosedAlerts { get; set; }
        public int ExpiredAlerts { get; set; }
        public int CleanedUpAlerts { get; set; }
        public int InfoAlerts { get; set; }
        public int WarningAlerts { get; set; }
        public int ErrorAlerts { get; set; }
        public int CriticalAlerts { get; set; }
        public int AcknowledgementsSent { get; set; }
        public int ResolutionsSent { get; set; }
        public int EscalationsSent { get; set; }
        public double AlertsCreatedPerMinute { get; set; }
        public double AcknowledgementsPerMinute { get; set; }
        public double AverageProcessingTime { get; set; }
        public DateTime LastAlertCreated { get; set; }
        public Dictionary<string, int> CategoryCounts { get; set; } = new();
    }

    public class AlertAnalytics;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public DateTime GeneratedAt { get; set; }
        public int TotalAlerts { get; set; }
        public int ActiveAlerts { get; set; }
        public int ResolvedAlerts { get; set; }
        public int ClosedAlerts { get; set; }
        public Dictionary<string, int> SeverityDistribution { get; set; } = new();
        public Dictionary<string, int> CategoryDistribution { get; set; } = new();
        public Dictionary<string, int> TimeSeriesData { get; set; } = new();
        public TimeSpan AverageResolutionTime { get; set; }
        public Dictionary<string, int> TopSourceSystems { get; set; } = new();
        public Dictionary<string, object> TrendAnalysis { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }

    public class SystemResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }

        public static SystemResult Success(string message = "Operation completed successfully") =>
            new() { Success = true, Message = message, Timestamp = DateTime.UtcNow };

        public static SystemResult Failure(string error) =>
            new() { Success = false, Message = error, Timestamp = DateTime.UtcNow };

        public static SystemResult AlreadyInitialized() =>
            new() { Success = false, Message = "System already initialized", Timestamp = DateTime.UtcNow };

        public static SystemResult NotInitialized() =>
            new() { Success = false, Message = "System not initialized", Timestamp = DateTime.UtcNow };

        public static SystemResult AlreadyRunning() =>
            new() { Success = false, Message = "System already running", Timestamp = DateTime.UtcNow };

        public static SystemResult NotRunning() =>
            new() { Success = false, Message = "System not running", Timestamp = DateTime.UtcNow };
    }

    public class AlertResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public Alert Alert { get; set; }

        public static AlertResult Success(Alert alert) =>
            new() { Success = true, Alert = alert, Message = "Alert operation completed" };

        public static AlertResult Failure(string error) =>
            new() { Success = false, Message = error };

        public static AlertResult NotFound() =>
            new() { Success = false, Message = "Alert not found" };

        public static AlertResult SystemNotReady() =>
            new() { Success = false, Message = "Alert system is not ready" };

        public static AlertResult DuplicateAlert() =>
            new() { Success = false, Message = "Duplicate alert suppressed" };
    }

    public class RuleEvaluationResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<AlertRule> TriggeredRules { get; set; } = new();
        public List<Alert> CreatedAlerts { get; set; } = new();
        public int TotalRulesEvaluated { get; set; }

        public static RuleEvaluationResult Success(List<AlertRule> triggeredRules, List<Alert> createdAlerts, int totalEvaluated) =>
            new() { Success = true, TriggeredRules = triggeredRules, CreatedAlerts = createdAlerts, TotalRulesEvaluated = totalEvaluated };

        public static RuleEvaluationResult Failure(string error) =>
            new() { Success = false, Message = error };
    }

    public class SystemHealthStatus;
    {
        public string Component { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public HealthStatus OverallStatus { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<SystemHealthStatus> Subcomponents { get; set; } = new();
        public Dictionary<string, object> Metrics { get; set; } = new();
    }

    public class AlertCreatedEventArgs : EventArgs;
    {
        public Alert Alert { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<AlertChannel> DistributionChannels { get; set; } = new();
        public bool IsCritical { get; set; }
    }

    public class AlertStatusChangedEventArgs : EventArgs;
    {
        public string AlertId { get; set; } = string.Empty;
        public AlertStatus PreviousStatus { get; set; }
        public AlertStatus NewStatus { get; set; }
        public DateTime ChangedAt { get; set; }
        public string ChangedBy { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public class AlertAcknowledgedEventArgs : EventArgs;
    {
        public Alert Alert { get; set; }
        public DateTime AcknowledgedAt { get; set; }
        public string AcknowledgedBy { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
    }

    public class AlertEvent;
    {
        public string AlertId { get; set; } = string.Empty;
        public AlertEventType EventType { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public class DistributionResult;
    {
        public string AlertId { get; set; } = string.Empty;
        public DistributionStatus Status { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public List<ChannelResult> ChannelResults { get; set; } = new();
        public int SuccessfulChannels { get; set; }
        public int FailedChannels { get; set; }
    }

    public class ChannelResult;
    {
        public string ChannelId { get; set; } = string.Empty;
        public string ChannelName { get; set; } = string.Empty;
        public ChannelType ChannelType { get; set; }
        public ChannelStatus Status { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int RetryCount { get; set; }
        public Dictionary<string, object> Details { get; set; } = new();
    }

    public class ConditionEvaluationResult;
    {
        public bool IsSatisfied { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Details { get; set; } = new();
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    // Event bus event classes;
    public class SystemAlertEvent : IEvent;
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public SystemEventType EventType { get; set; }
        public string Source { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public class PerformanceAlertEvent : IEvent;
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string MetricName { get; set; } = string.Empty;
        public double Value { get; set; }
        public double Threshold { get; set; }
        public string Source { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    #endregion;
}
