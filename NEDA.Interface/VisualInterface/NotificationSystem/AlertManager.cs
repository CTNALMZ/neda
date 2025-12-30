using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Services.Messaging.EventBus;
using NEDA.Services.NotificationService;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.VisualInterface.NotificationSystem;
{
    /// <summary>
    /// Kullanıcıya görsel ve sesli uyarılar, bildirimler sunan gelişmiş yönetici;
    /// Çoklu kanal, öncelik ve hedefleme desteği ile tam özellikli bildirim sistemi;
    /// </summary>
    public class AlertManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly INotificationService _notificationService;
        private readonly IEventBus _eventBus;

        private readonly ConcurrentDictionary<string, Alert> _activeAlerts;
        private readonly ConcurrentDictionary<string, AlertRule> _alertRules;
        private readonly ConcurrentQueue<Alert> _alertQueue;
        private readonly ConcurrentDictionary<string, List<Alert>> _alertHistory;

        private readonly AlertConfiguration _configuration;
        private readonly AlertRenderer _renderer;
        private readonly AlertScheduler _scheduler;
        private readonly AlertFilter _filter;

        private readonly SemaphoreSlim _processingLock;
        private readonly Timer _cleanupTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;

        private bool _isInitialized;
        private bool _isProcessing;
        private long _totalAlertsProcessed;

        /// <summary>
        /// AlertManager'ı başlatır;
        /// </summary>
        public AlertManager(
            ILogger logger,
            INotificationService notificationService,
            IEventBus eventBus = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _eventBus = eventBus;

            _activeAlerts = new ConcurrentDictionary<string, Alert>();
            _alertRules = new ConcurrentDictionary<string, AlertRule>();
            _alertQueue = new ConcurrentQueue<Alert>();
            _alertHistory = new ConcurrentDictionary<string, List<Alert>>();

            _configuration = new AlertConfiguration();
            _renderer = new AlertRenderer(logger);
            _scheduler = new AlertScheduler(logger);
            _filter = new AlertFilter(logger);

            _processingLock = new SemaphoreSlim(1, 1);
            _cancellationTokenSource = new CancellationTokenSource();

            // Temizleme timer'ı (5 dakikada bir)
            _cleanupTimer = new Timer(CleanupExpiredAlerts, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            _logger.LogInformation("AlertManager initialized.");
        }

        /// <summary>
        /// AlertManager'ı yapılandırır ve başlatır;
        /// </summary>
        public async Task InitializeAsync(AlertConfiguration configuration = null)
        {
            try
            {
                if (configuration != null)
                {
                    _configuration.Merge(configuration);
                }

                // Alert renderer'ı başlat;
                await _renderer.InitializeAsync();

                // Varsayılan kuralları yükle;
                await LoadDefaultRulesAsync();

                // Event bus'a abone ol;
                if (_eventBus != null)
                {
                    await SubscribeToEventsAsync();
                }

                // Bildirim servisini başlat;
                await _notificationService.InitializeAsync();

                _isInitialized = true;

                // Queue processing'ı başlat;
                _ = Task.Run(() => ProcessAlertQueueAsync(_cancellationTokenSource.Token));

                _logger.LogInformation("AlertManager initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize AlertManager.");
                throw new AlertManagerException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Yeni bir uyarı oluşturur ve kuyruğa ekler;
        /// </summary>
        /// <param name="alert">Uyarı bilgileri</param>
        /// <returns>Oluşturulan uyarının ID'si</returns>
        public async Task<string> CreateAlertAsync(Alert alert)
        {
            ValidateInitialization();

            if (alert == null)
            {
                throw new ArgumentNullException(nameof(alert));
            }

            // Alert ID ata;
            alert.Id = Guid.NewGuid().ToString();
            alert.CreatedAt = DateTime.UtcNow;

            // Doğrulama;
            ValidateAlert(alert);

            // Kurallara göre filtrele;
            if (!await _filter.ShouldDisplayAlertAsync(alert, _alertRules.Values.ToList()))
            {
                _logger.LogDebug($"Alert filtered out: {alert.Title}");
                return alert.Id;
            }

            // Önceliklendirme ve zamanlama;
            var scheduledAlert = await _scheduler.ScheduleAlertAsync(alert, _configuration);

            // Kuyruğa ekle;
            _alertQueue.Enqueue(scheduledAlert);

            _logger.LogInformation($"Alert created: {alert.Id} - {alert.Title}");

            return alert.Id;
        }

        /// <summary>
        /// Hızlı uyarı oluşturma metodu;
        /// </summary>
        public async Task<string> ShowAlertAsync(
            string title,
            string message,
            AlertType type = AlertType.Information,
            AlertPriority priority = AlertPriority.Medium,
            string category = null,
            Dictionary<string, object> metadata = null)
        {
            var alert = new Alert;
            {
                Title = title,
                Message = message,
                Type = type,
                Priority = priority,
                Category = category ?? "General",
                Metadata = metadata ?? new Dictionary<string, object>(),
                DisplayDuration = GetDefaultDuration(type, priority),
                RequiresAcknowledgment = priority >= AlertPriority.High;
            };

            return await CreateAlertAsync(alert);
        }

        /// <summary>
        /// Belirli bir uyarıyı getirir;
        /// </summary>
        public Alert GetAlert(string alertId)
        {
            if (string.IsNullOrEmpty(alertId))
            {
                throw new ArgumentException("Alert ID cannot be null or empty.", nameof(alertId));
            }

            if (_activeAlerts.TryGetValue(alertId, out var alert))
            {
                return alert;
            }

            // Tarihçede ara;
            foreach (var history in _alertHistory.Values)
            {
                var historicalAlert = history.FirstOrDefault(a => a.Id == alertId);
                if (historicalAlert != null)
                {
                    return historicalAlert;
                }
            }

            throw new AlertNotFoundException($"Alert not found: {alertId}");
        }

        /// <summary>
        /// Aktif uyarıları listeler;
        /// </summary>
        public List<Alert> GetActiveAlerts(string category = null, AlertPriority? priority = null)
        {
            var alerts = _activeAlerts.Values.ToList();

            if (!string.IsNullOrEmpty(category))
            {
                alerts = alerts.Where(a => a.Category == category).ToList();
            }

            if (priority.HasValue)
            {
                alerts = alerts.Where(a => a.Priority == priority.Value).ToList();
            }

            return alerts.OrderByDescending(a => a.Priority)
                         .ThenByDescending(a => a.CreatedAt)
                         .ToList();
        }

        /// <summary>
        /// Uyarı geçmişini getirir;
        /// </summary>
        public List<Alert> GetAlertHistory(
            DateTime? fromDate = null,
            DateTime? toDate = null,
            string category = null,
            AlertType? type = null)
        {
            var allHistory = _alertHistory.Values.SelectMany(x => x).ToList();

            if (fromDate.HasValue)
            {
                allHistory = allHistory.Where(a => a.CreatedAt >= fromDate.Value).ToList();
            }

            if (toDate.HasValue)
            {
                allHistory = allHistory.Where(a => a.CreatedAt <= toDate.Value).ToList();
            }

            if (!string.IsNullOrEmpty(category))
            {
                allHistory = allHistory.Where(a => a.Category == category).ToList();
            }

            if (type.HasValue)
            {
                allHistory = allHistory.Where(a => a.Type == type.Value).ToList();
            }

            return allHistory.OrderByDescending(a => a.CreatedAt).ToList();
        }

        /// <summary>
        /// Uyarıyı kullanıcı tarafından kabul edildi olarak işaretler;
        /// </summary>
        public async Task AcknowledgeAlertAsync(string alertId, string userId = null)
        {
            if (!_activeAlerts.TryGetValue(alertId, out var alert))
            {
                throw new AlertNotFoundException($"Active alert not found: {alertId}");
            }

            alert.AcknowledgedAt = DateTime.UtcNow;
            alert.AcknowledgedBy = userId ?? "System";
            alert.Status = AlertStatus.Acknowledged;

            // Event fırlat;
            await OnAlertAcknowledgedAsync(alert);

            _logger.LogInformation($"Alert acknowledged: {alertId} by {alert.AcknowledgedBy}");
        }

        /// <summary>
        /// Uyarıyı kapatır/dismiss eder;
        /// </summary>
        public async Task DismissAlertAsync(string alertId, string reason = null)
        {
            if (!_activeAlerts.TryRemove(alertId, out var alert))
            {
                throw new AlertNotFoundException($"Active alert not found: {alertId}");
            }

            alert.DismissedAt = DateTime.UtcNow;
            alert.DismissReason = reason;
            alert.Status = AlertStatus.Dismissed;

            // Geçmişe ekle;
            AddToHistory(alert);

            // Event fırlat;
            await OnAlertDismissedAsync(alert);

            _logger.LogInformation($"Alert dismissed: {alertId} - Reason: {reason ?? "Not specified"}");
        }

        /// <summary>
        /// Tüm uyarıları kapatır;
        /// </summary>
        public async Task DismissAllAlertsAsync(string reason = null)
        {
            var alertIds = _activeAlerts.Keys.ToList();
            var tasks = new List<Task>();

            foreach (var alertId in alertIds)
            {
                tasks.Add(DismissAlertAsync(alertId, reason));
            }

            await Task.WhenAll(tasks);
            _logger.LogInformation($"Dismissed all {alertIds.Count} alerts.");
        }

        /// <summary>
        /// Uyarı kuralı ekler;
        /// </summary>
        public void AddAlertRule(AlertRule rule)
        {
            if (rule == null)
            {
                throw new ArgumentNullException(nameof(rule));
            }

            rule.Id = Guid.NewGuid().ToString();
            _alertRules[rule.Id] = rule;

            _logger.LogDebug($"Added alert rule: {rule.Name}");
        }

        /// <summary>
        /// Uyarı kuralını kaldırır;
        /// </summary>
        public bool RemoveAlertRule(string ruleId)
        {
            return _alertRules.TryRemove(ruleId, out _);
        }

        /// <summary>
        /// Tüm uyarı kurallarını getirir;
        /// </summary>
        public List<AlertRule> GetAlertRules() => _alertRules.Values.ToList();

        /// <summary>
        /// Uyarı istatistiklerini getirir;
        /// </summary>
        public AlertStatistics GetStatistics()
        {
            var history = GetAlertHistory();

            return new AlertStatistics;
            {
                TotalAlertsProcessed = _totalAlertsProcessed,
                ActiveAlertsCount = _activeAlerts.Count,
                TotalAlertsInHistory = history.Count,
                AlertsByType = history.GroupBy(a => a.Type)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                AlertsByCategory = history.GroupBy(a => a.Category)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AverageResponseTime = history;
                    .Where(a => a.AcknowledgedAt.HasValue)
                    .Select(a => (a.AcknowledgedAt.Value - a.CreatedAt).TotalSeconds)
                    .DefaultIfEmpty(0)
                    .Average(),
                MostFrequentCategory = history;
                    .GroupBy(a => a.Category)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key ?? "N/A"
            };
        }

        /// <summary>
        /// Sistem durumunu kontrol eder;
        /// </summary>
        public AlertSystemHealth GetSystemHealth()
        {
            return new AlertSystemHealth;
            {
                IsInitialized = _isInitialized,
                IsProcessing = _isProcessing,
                ActiveAlerts = _activeAlerts.Count,
                QueuedAlerts = _alertQueue.Count,
                AlertRules = _alertRules.Count,
                LastCleanupTime = DateTime.UtcNow,
                MemoryUsage = GC.GetTotalMemory(false),
                ProcessingRate = CalculateProcessingRate()
            };
        }

        /// <summary>
        /// Uyarıları filtreler;
        /// </summary>
        public async Task<List<Alert>> FilterAlertsAsync(AlertFilterCriteria criteria)
        {
            var allAlerts = GetActiveAlerts();
            return await _filter.FilterAlertsAsync(allAlerts, criteria);
        }

        /// <summary>
        /// Uyarıları gruplandırır;
        /// </summary>
        public Dictionary<string, List<Alert>> GroupAlertsByCategory()
        {
            return _activeAlerts.Values;
                .GroupBy(a => a.Category)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        /// <summary>
        /// Özet bildirim oluşturur;
        /// </summary>
        public async Task<AlertSummary> CreateAlertSummaryAsync(TimeSpan period)
        {
            var fromDate = DateTime.UtcNow - period;
            var history = GetAlertHistory(fromDate);

            var summary = new AlertSummary;
            {
                PeriodStart = fromDate,
                PeriodEnd = DateTime.UtcNow,
                TotalAlerts = history.Count,
                CriticalAlerts = history.Count(a => a.Priority == AlertPriority.Critical),
                UnacknowledgedAlerts = history.Count(a => !a.AcknowledgedAt.HasValue),
                MostCommonType = history.GroupBy(a => a.Type)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key.ToString() ?? "N/A",
                Categories = history.GroupBy(a => a.Category)
                    .ToDictionary(g => g.Key, g => g.Count())
            };

            // Trend analizi;
            var hourlyGroups = history;
                .GroupBy(a => new DateTime(a.CreatedAt.Year, a.CreatedAt.Month, a.CreatedAt.Day, a.CreatedAt.Hour, 0, 0))
                .OrderBy(g => g.Key);

            summary.AlertTrend = hourlyGroups;
                .Select(g => new AlertTrendPoint;
                {
                    Timestamp = g.Key,
                    Count = g.Count(),
                    AveragePriority = g.Average(a => (int)a.Priority)
                })
                .ToList();

            return summary;
        }

        /// <summary>
        /// Acil durum uyarısı gönderir;
        /// </summary>
        public async Task<string> SendEmergencyAlertAsync(
            string title,
            string message,
            EmergencyLevel level,
            Dictionary<string, object> emergencyData = null)
        {
            var alert = new Alert;
            {
                Title = title,
                Message = message,
                Type = AlertType.Emergency,
                Priority = AlertPriority.Critical,
                Category = "Emergency",
                Metadata = emergencyData ?? new Dictionary<string, object>(),
                DisplayDuration = null, // Manuel kapatılana kadar açık kalır;
                RequiresAcknowledgment = true,
                RequiresImmediateAction = true,
                EmergencyLevel = level,
                SoundAlert = true,
                VisualAlert = true,
                VibrationAlert = true,
                RepeatCount = 3,
                RepeatInterval = TimeSpan.FromSeconds(10)
            };

            // Tüm kanallara gönder;
            await _notificationService.SendEmergencyNotificationAsync(alert);

            return await CreateAlertAsync(alert);
        }

        #region Private Methods;

        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("AlertManager must be initialized before use.");
            }
        }

        private void ValidateAlert(Alert alert)
        {
            if (string.IsNullOrWhiteSpace(alert.Title))
            {
                throw new ArgumentException("Alert title cannot be null or empty.", nameof(alert.Title));
            }

            if (string.IsNullOrWhiteSpace(alert.Message))
            {
                throw new ArgumentException("Alert message cannot be null or empty.", nameof(alert.Message));
            }

            if (alert.DisplayDuration.HasValue && alert.DisplayDuration.Value <= TimeSpan.Zero)
            {
                throw new ArgumentException("Display duration must be positive.", nameof(alert.DisplayDuration));
            }
        }

        private TimeSpan GetDefaultDuration(AlertType type, AlertPriority priority)
        {
            return priority switch;
            {
                AlertPriority.Low => TimeSpan.FromSeconds(5),
                AlertPriority.Medium => TimeSpan.FromSeconds(8),
                AlertPriority.High => TimeSpan.FromSeconds(12),
                AlertPriority.Critical => TimeSpan.FromSeconds(15),
                _ => TimeSpan.FromSeconds(8)
            };
        }

        private async Task ProcessAlertQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _processingLock.WaitAsync(cancellationToken);
                    _isProcessing = true;

                    if (_alertQueue.TryDequeue(out var alert))
                    {
                        await ProcessAlertAsync(alert, cancellationToken);
                        Interlocked.Increment(ref _totalAlertsProcessed);
                    }
                    else;
                    {
                        // Kuyruk boşsa bekle;
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // İptal edildi, normal çıkış;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing alert queue.");
                }
                finally
                {
                    _isProcessing = false;
                    _processingLock.Release();
                }
            }
        }

        private async Task ProcessAlertAsync(Alert alert, CancellationToken cancellationToken)
        {
            try
            {
                // Render et;
                var renderResult = await _renderer.RenderAlertAsync(alert, _configuration);

                if (!renderResult.Success)
                {
                    _logger.LogWarning($"Failed to render alert: {alert.Id}");
                    return;
                }

                // Aktif alert'lere ekle;
                if (!_activeAlerts.TryAdd(alert.Id, alert))
                {
                    _logger.LogWarning($"Alert already exists: {alert.Id}");
                    return;
                }

                // Geçmişe ekle;
                AddToHistory(alert);

                // Event fırlat;
                await OnAlertDisplayedAsync(alert);

                // Otomatik kapatma zamanlayıcı;
                if (alert.DisplayDuration.HasValue && !alert.RequiresAcknowledgment)
                {
                    _ = ScheduleAutoDismissalAsync(alert.Id, alert.DisplayDuration.Value, cancellationToken);
                }

                _logger.LogDebug($"Alert processed successfully: {alert.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process alert: {alert.Id}");
            }
        }

        private async Task ScheduleAutoDismissalAsync(string alertId, TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);

                if (_activeAlerts.ContainsKey(alertId))
                {
                    await DismissAlertAsync(alertId, "Auto-dismissed after timeout");
                }
            }
            catch (OperationCanceledException)
            {
                // İptal edildi, normal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in auto-dismissal for alert: {alertId}");
            }
        }

        private void AddToHistory(Alert alert)
        {
            var category = alert.Category ?? "Uncategorized";

            if (!_alertHistory.TryGetValue(category, out var history))
            {
                history = new List<Alert>();
                _alertHistory[category] = history;
            }

            history.Add(alert);

            // Tarihçeyi sınırla;
            if (history.Count > 1000)
            {
                history.RemoveRange(0, history.Count - 500);
            }
        }

        private async Task LoadDefaultRulesAsync()
        {
            // Spam koruma kuralı;
            var spamRule = new AlertRule;
            {
                Name = "Spam Protection",
                Description = "Blocks duplicate alerts within short time frame",
                Condition = (alert, context) =>
                {
                    var recentAlerts = GetAlertHistory(DateTime.UtcNow.AddMinutes(-1));
                    return recentAlerts.Count(a => a.Title == alert.Title &&
                                                  a.Message == alert.Message) < 3;
                },
                Action = AlertRuleAction.Filter;
            };

            AddAlertRule(spamRule);

            // Kritik uyarı yükseltme kuralı;
            var criticalRule = new AlertRule;
            {
                Name = "Critical Alert Escalation",
                Description = "Escalates alerts containing critical keywords",
                Condition = (alert, context) =>
                {
                    var criticalKeywords = new[] { "error", "failed", "critical", "emergency", "crash" };
                    return criticalKeywords.Any(keyword =>
                        alert.Title.ToLowerInvariant().Contains(keyword) ||
                        alert.Message.ToLowerInvariant().Contains(keyword));
                },
                Action = AlertRuleAction.Escalate,
                Metadata = new Dictionary<string, object>
                {
                    ["NewPriority"] = AlertPriority.Critical,
                    ["SoundAlert"] = true;
                }
            };

            AddAlertRule(criticalRule);

            await Task.CompletedTask;
        }

        private async Task SubscribeToEventsAsync()
        {
            // Sistem olaylarına abone ol;
            // Gerçek implementasyonda event bus'tan gelen event'ları dinle;
            await Task.CompletedTask;
        }

        private void CleanupExpiredAlerts(object state)
        {
            try
            {
                var expiredThreshold = DateTime.UtcNow.AddHours(-_configuration.HistoryRetentionHours);
                var expiredAlerts = _activeAlerts.Values;
                    .Where(a => a.CreatedAt < expiredThreshold &&
                               !a.RequiresAcknowledgment)
                    .ToList();

                foreach (var alert in expiredAlerts)
                {
                    _activeAlerts.TryRemove(alert.Id, out _);
                    _logger.LogDebug($"Cleaned up expired alert: {alert.Id}");
                }

                // Tarihçeyi temizle;
                foreach (var category in _alertHistory.Keys.ToList())
                {
                    var history = _alertHistory[category];
                    history.RemoveAll(a => a.CreatedAt < expiredThreshold);

                    if (history.Count == 0)
                    {
                        _alertHistory.TryRemove(category, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during alert cleanup.");
            }
        }

        private double CalculateProcessingRate()
        {
            // Son 1 dakikadaki işleme hızını hesapla;
            var recentAlerts = GetAlertHistory(DateTime.UtcNow.AddMinutes(-1));
            return recentAlerts.Count / 60.0; // alerts per second;
        }

        private async Task OnAlertDisplayedAsync(Alert alert)
        {
            try
            {
                // Event bus'a bildir;
                if (_eventBus != null)
                {
                    await _eventBus.PublishAsync(new AlertDisplayedEvent;
                    {
                        AlertId = alert.Id,
                        Timestamp = DateTime.UtcNow,
                        AlertType = alert.Type.ToString(),
                        Priority = alert.Priority.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish alert displayed event.");
            }
        }

        private async Task OnAlertAcknowledgedAsync(Alert alert)
        {
            try
            {
                if (_eventBus != null)
                {
                    await _eventBus.PublishAsync(new AlertAcknowledgedEvent;
                    {
                        AlertId = alert.Id,
                        Timestamp = DateTime.UtcNow,
                        AcknowledgedBy = alert.AcknowledgedBy;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish alert acknowledged event.");
            }
        }

        private async Task OnAlertDismissedAsync(Alert alert)
        {
            try
            {
                if (_eventBus != null)
                {
                    await _eventBus.PublishAsync(new AlertDismissedEvent;
                    {
                        AlertId = alert.Id,
                        Timestamp = DateTime.UtcNow,
                        DismissReason = alert.DismissReason;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish alert dismissed event.");
            }
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
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();

                    _cleanupTimer?.Dispose();
                    _processingLock?.Dispose();

                    _activeAlerts.Clear();
                    _alertQueue.Clear();
                    _alertHistory.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~AlertManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Uyarı yapılandırması;
    /// </summary>
    public class AlertConfiguration;
    {
        public int MaxActiveAlerts { get; set; } = 50;
        public int HistoryRetentionHours { get; set; } = 24;
        public int MaxQueueSize { get; set; } = 1000;
        public bool EnableSound { get; set; } = true;
        public bool EnableVisual { get; set; } = true;
        public bool EnableVibration { get; set; } = false;
        public bool EnableDesktopNotifications { get; set; } = true;
        public bool EnableMobileNotifications { get; set; } = false;
        public bool EnableEmailNotifications { get; set; } = false;
        public string DefaultTheme { get; set; } = "Modern";
        public TimeSpan DefaultDisplayDuration { get; set; } = TimeSpan.FromSeconds(8);
        public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();

        public void Merge(AlertConfiguration other)
        {
            if (other == null) return;

            MaxActiveAlerts = other.MaxActiveAlerts;
            HistoryRetentionHours = other.HistoryRetentionHours;
            MaxQueueSize = other.MaxQueueSize;
            EnableSound = other.EnableSound;
            EnableVisual = other.EnableVisual;
            EnableVibration = other.EnableVibration;
            EnableDesktopNotifications = other.EnableDesktopNotifications;
            EnableMobileNotifications = other.EnableMobileNotifications;
            EnableEmailNotifications = other.EnableEmailNotifications;
            DefaultTheme = other.DefaultTheme;
            DefaultDisplayDuration = other.DefaultDisplayDuration;

            foreach (var setting in other.AdvancedSettings)
            {
                AdvancedSettings[setting.Key] = setting.Value;
            }
        }
    }

    /// <summary>
    /// Uyarı bilgisi;
    /// </summary>
    public class Alert;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public AlertType Type { get; set; }
        public AlertPriority Priority { get; set; }
        public string Category { get; set; } = "General";
        public AlertStatus Status { get; set; } = AlertStatus.New;
        public DateTime CreatedAt { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public string AcknowledgedBy { get; set; }
        public DateTime? DismissedAt { get; set; }
        public string DismissReason { get; set; }
        public TimeSpan? DisplayDuration { get; set; }
        public bool RequiresAcknowledgment { get; set; }
        public bool RequiresImmediateAction { get; set; }
        public EmergencyLevel? EmergencyLevel { get; set; }
        public bool SoundAlert { get; set; }
        public bool VisualAlert { get; set; }
        public bool VibrationAlert { get; set; }
        public int RepeatCount { get; set; } = 1;
        public TimeSpan? RepeatInterval { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public List<string> Tags { get; set; } = new List<string>();
        public string Source { get; set; } = "System";
        public string Icon { get; set; }
        public string Color { get; set; }
        public Dictionary<string, object> Actions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Uyarı türleri;
    /// </summary>
    public enum AlertType;
    {
        Information,
        Warning,
        Error,
        Success,
        Emergency,
        System,
        Security,
        Performance;
    }

    /// <summary>
    /// Uyarı öncelik seviyeleri;
    /// </summary>
    public enum AlertPriority;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3;
    }

    /// <summary>
    /// Uyarı durumları;
    /// </summary>
    public enum AlertStatus;
    {
        New,
        Displayed,
        Acknowledged,
        Dismissed,
        Expired;
    }

    /// <summary>
    /// Acil durum seviyeleri;
    /// </summary>
    public enum EmergencyLevel;
    {
        Level1, // Informational;
        Level2, // Warning;
        Level3, // Critical;
        Level4  // Catastrophic;
    }

    /// <summary>
    /// Uyarı kuralı;
    /// </summary>
    public class AlertRule;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public Func<Alert, AlertRuleContext, bool> Condition { get; set; }
        public AlertRuleAction Action { get; set; }
        public bool IsEnabled { get; set; } = true;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public int Order { get; set; }
    }

    /// <summary>
    /// Uyarı kuralı bağlamı;
    /// </summary>
    public class AlertRuleContext;
    {
        public AlertManager AlertManager { get; set; }
        public AlertConfiguration Configuration { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Uyarı kuralı aksiyonları;
    /// </summary>
    public enum AlertRuleAction;
    {
        Allow,
        Filter,
        Modify,
        Escalate,
        Delay,
        Group;
    }

    /// <summary>
    /// Uyarı istatistikleri;
    /// </summary>
    public class AlertStatistics;
    {
        public long TotalAlertsProcessed { get; set; }
        public int ActiveAlertsCount { get; set; }
        public int TotalAlertsInHistory { get; set; }
        public Dictionary<string, int> AlertsByType { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> AlertsByCategory { get; set; } = new Dictionary<string, int>();
        public double AverageResponseTime { get; set; }
        public string MostFrequentCategory { get; set; }
    }

    /// <summary>
    /// Sistem sağlık durumu;
    /// </summary>
    public class AlertSystemHealth;
    {
        public bool IsInitialized { get; set; }
        public bool IsProcessing { get; set; }
        public int ActiveAlerts { get; set; }
        public int QueuedAlerts { get; set; }
        public int AlertRules { get; set; }
        public DateTime LastCleanupTime { get; set; }
        public long MemoryUsage { get; set; }
        public double ProcessingRate { get; set; }
    }

    /// <summary>
    /// Uyarı filtre kriterleri;
    /// </summary>
    public class AlertFilterCriteria;
    {
        public List<AlertType> Types { get; set; } = new List<AlertType>();
        public List<AlertPriority> Priorities { get; set; } = new List<AlertPriority>();
        public List<string> Categories { get; set; } = new List<string>();
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public bool? RequiresAcknowledgment { get; set; }
        public string SearchText { get; set; }
        public int MaxResults { get; set; } = 100;
    }

    /// <summary>
    /// Uyarı özeti;
    /// </summary>
    public class AlertSummary;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int TotalAlerts { get; set; }
        public int CriticalAlerts { get; set; }
        public int UnacknowledgedAlerts { get; set; }
        public string MostCommonType { get; set; }
        public Dictionary<string, int> Categories { get; set; } = new Dictionary<string, int>();
        public List<AlertTrendPoint> AlertTrend { get; set; } = new List<AlertTrendPoint>();
    }

    /// <summary>
    /// Uyarı trend noktası;
    /// </summary>
    public class AlertTrendPoint;
    {
        public DateTime Timestamp { get; set; }
        public int Count { get; set; }
        public double AveragePriority { get; set; }
    }

    #endregion;

    #region Internal Components;

    /// <summary>
    /// Uyarı render edici;
    /// </summary>
    internal class AlertRenderer;
    {
        private readonly ILogger _logger;

        public AlertRenderer(ILogger logger)
        {
            _logger = logger;
        }

        public async Task InitializeAsync()
        {
            await Task.CompletedTask;
            _logger.LogDebug("AlertRenderer initialized.");
        }

        public async Task<RenderResult> RenderAlertAsync(Alert alert, AlertConfiguration configuration)
        {
            try
            {
                // UI rendering logic;
                // Gerçek implementasyonda WPF/UI framework ile entegrasyon;

                await Task.Delay(10); // Simüle render süresi;

                return new RenderResult;
                {
                    Success = true,
                    RenderTime = DateTime.UtcNow,
                    RenderMethod = "Visual",
                    Details = $"Alert rendered: {alert.Title}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to render alert: {alert.Id}");
                return new RenderResult;
                {
                    Success = false,
                    Error = ex.Message;
                };
            }
        }
    }

    /// <summary>
    /// Render sonucu;
    /// </summary>
    internal class RenderResult;
    {
        public bool Success { get; set; }
        public DateTime RenderTime { get; set; }
        public string RenderMethod { get; set; }
        public string Details { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Uyarı zamanlayıcı;
    /// </summary>
    internal class AlertScheduler;
    {
        private readonly ILogger _logger;

        public AlertScheduler(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<Alert> ScheduleAlertAsync(Alert alert, AlertConfiguration configuration)
        {
            // Zamanlama ve önceliklendirme mantığı;
            // Gerçek implementasyonda daha karmaşık scheduling algoritmaları;

            await Task.CompletedTask;
            return alert;
        }
    }

    /// <summary>
    /// Uyarı filtresi;
    /// </summary>
    internal class AlertFilter;
    {
        private readonly ILogger _logger;

        public AlertFilter(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<bool> ShouldDisplayAlertAsync(Alert alert, List<AlertRule> rules)
        {
            try
            {
                foreach (var rule in rules.Where(r => r.IsEnabled).OrderBy(r => r.Order))
                {
                    var context = new AlertRuleContext();
                    if (rule.Condition(alert, context))
                    {
                        switch (rule.Action)
                        {
                            case AlertRuleAction.Filter:
                                return false;
                            case AlertRuleAction.Modify:
                                ApplyRuleModifications(alert, rule.Metadata);
                                break;
                            case AlertRuleAction.Escalate:
                                EscalateAlert(alert, rule.Metadata);
                                break;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in alert filtering.");
                return true; // Hata durumunda göster;
            }
        }

        public async Task<List<Alert>> FilterAlertsAsync(List<Alert> alerts, AlertFilterCriteria criteria)
        {
            var filtered = alerts.AsEnumerable();

            if (criteria.Types.Any())
            {
                filtered = filtered.Where(a => criteria.Types.Contains(a.Type));
            }

            if (criteria.Priorities.Any())
            {
                filtered = filtered.Where(a => criteria.Priorities.Contains(a.Priority));
            }

            if (criteria.Categories.Any())
            {
                filtered = filtered.Where(a => criteria.Categories.Contains(a.Category));
            }

            if (criteria.FromDate.HasValue)
            {
                filtered = filtered.Where(a => a.CreatedAt >= criteria.FromDate.Value);
            }

            if (criteria.ToDate.HasValue)
            {
                filtered = filtered.Where(a => a.CreatedAt <= criteria.ToDate.Value);
            }

            if (criteria.RequiresAcknowledgment.HasValue)
            {
                filtered = filtered.Where(a => a.RequiresAcknowledgment == criteria.RequiresAcknowledgment.Value);
            }

            if (!string.IsNullOrEmpty(criteria.SearchText))
            {
                var search = criteria.SearchText.ToLowerInvariant();
                filtered = filtered.Where(a =>
                    a.Title.ToLowerInvariant().Contains(search) ||
                    a.Message.ToLowerInvariant().Contains(search));
            }

            return filtered.Take(criteria.MaxResults).ToList();
        }

        private void ApplyRuleModifications(Alert alert, Dictionary<string, object> metadata)
        {
            if (metadata.TryGetValue("NewPriority", out var priority) &&
                Enum.TryParse(priority.ToString(), out AlertPriority newPriority))
            {
                alert.Priority = newPriority;
            }
        }

        private void EscalateAlert(Alert alert, Dictionary<string, object> metadata)
        {
            alert.Priority = AlertPriority.Critical;

            if (metadata.TryGetValue("SoundAlert", out var soundAlert) &&
                bool.TryParse(soundAlert.ToString(), out var enableSound))
            {
                alert.SoundAlert = enableSound;
            }
        }
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Uyarı görüntülendi event'ı;
    /// </summary>
    public class AlertDisplayedEvent : IEvent;
    {
        public string AlertId { get; set; }
        public DateTime Timestamp { get; set; }
        public string AlertType { get; set; }
        public string Priority { get; set; }
        public string EventType => "AlertDisplayed";
    }

    /// <summary>
    /// Uyarı kabul edildi event'ı;
    /// </summary>
    public class AlertAcknowledgedEvent : IEvent;
    {
        public string AlertId { get; set; }
        public DateTime Timestamp { get; set; }
        public string AcknowledgedBy { get; set; }
        public string EventType => "AlertAcknowledged";
    }

    /// <summary>
    /// Uyarı kapatıldı event'ı;
    /// </summary>
    public class AlertDismissedEvent : IEvent;
    {
        public string AlertId { get; set; }
        public DateTime Timestamp { get; set; }
        public string DismissReason { get; set; }
        public string EventType => "AlertDismissed";
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// AlertManager istisnası;
    /// </summary>
    public class AlertManagerException : Exception
    {
        public string AlertId { get; }
        public DateTime Timestamp { get; }

        public AlertManagerException(string message) : base(message)
        {
            Timestamp = DateTime.UtcNow;
        }

        public AlertManagerException(string message, Exception innerException)
            : base(message, innerException)
        {
            Timestamp = DateTime.UtcNow;
        }

        public AlertManagerException(string alertId, string message)
            : base(message)
        {
            AlertId = alertId;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Uyarı bulunamadı istisnası;
    /// </summary>
    public class AlertNotFoundException : AlertManagerException;
    {
        public AlertNotFoundException(string message) : base(message)
        {
        }
    }

    #endregion;
}
