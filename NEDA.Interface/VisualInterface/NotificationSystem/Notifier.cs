using NEDA.Core.Engine;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Security;
using NEDA.Interface.VisualInterface.NotificationSystem.Contracts;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Media;
using System.Windows.Threading;

namespace NEDA.Interface.VisualInterface.NotificationSystem;
{
    /// <summary>
    /// Bildirim yönetim sistemi - Tüm bildirimleri merkezi olarak yönetir;
    /// </summary>
    public class Notifier : INotifyPropertyChanged, INotifier;
    {
        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IEventBus _eventBus;
        private readonly NotifierConfiguration _configuration;

        private readonly ConcurrentDictionary<string, Notification> _activeNotifications;
        private readonly ConcurrentQueue<Notification> _notificationQueue;
        private readonly ConcurrentDictionary<string, NotificationTemplate> _templates;
        private readonly ConcurrentDictionary<string, NotificationChannel> _channels;
        private readonly ConcurrentDictionary<string, NotificationPreference> _userPreferences;

        private readonly SemaphoreSlim _notificationLock = new SemaphoreSlim(1, 1);
        private readonly System.Timers.Timer _cleanupTimer;
        private readonly System.Timers.Timer _deliveryTimer;
        private readonly DispatcherTimer _uiTimer;

        private bool _isInitialized;
        private NotificationSoundPlayer _soundPlayer;
        private NotificationHistoryManager _historyManager;
        private CancellationTokenSource _notifierCts;

        /// <summary>
        /// Notifier sınıfı için constructor;
        /// </summary>
        public Notifier(
            ILogger logger,
            IErrorHandler errorHandler,
            IEventBus eventBus,
            NotifierConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _configuration = configuration ?? NotifierConfiguration.Default;

            _activeNotifications = new ConcurrentDictionary<string, Notification>();
            _notificationQueue = new ConcurrentQueue<Notification>();
            _templates = new ConcurrentDictionary<string, NotificationTemplate>();
            _channels = new ConcurrentDictionary<string, NotificationChannel>();
            _userPreferences = new ConcurrentDictionary<string, NotificationPreference>();

            // Bildirim temizleme timer'ı;
            _cleanupTimer = new System.Timers.Timer(_configuration.CleanupInterval.TotalMilliseconds);
            _cleanupTimer.Elapsed += async (s, e) => await CleanupExpiredNotificationsAsync();
            _cleanupTimer.AutoReset = true;

            // Bildirim dağıtım timer'ı;
            _deliveryTimer = new System.Timers.Timer(_configuration.DeliveryCheckInterval.TotalMilliseconds);
            _deliveryTimer.Elapsed += async (s, e) => await ProcessNotificationQueueAsync();
            _deliveryTimer.AutoReset = true;

            // UI güncelleme timer'ı (WPF için)
            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(_configuration.UiUpdateIntervalMs);
            _uiTimer.Tick += OnUiTimerTick;

            // Ses oynatıcı;
            _soundPlayer = new NotificationSoundPlayer(_configuration);

            // Geçmiş yöneticisi;
            _historyManager = new NotificationHistoryManager(_configuration);

            // Olay abonelikleri;
            SubscribeToEvents();

            // Varsayılan şablonları yükle;
            LoadDefaultTemplates();

            // Varsayılan kanalları yükle;
            LoadDefaultChannels();

            PropertyChanged += OnNotifierPropertyChanged;
        }

        #region Public Properties;

        /// <summary>
        /// Aktif bildirimler;
        /// </summary>
        public ObservableCollection<Notification> ActiveNotifications { get; } = new ObservableCollection<Notification>();

        /// <summary>
        /// Bekleyen bildirim sayısı;
        /// </summary>
        public int PendingNotificationCount;
        {
            get => _pendingNotificationCount;
            private set => SetField(ref _pendingNotificationCount, value);
        }
        private int _pendingNotificationCount;

        /// <summary>
        /// Bugünkü bildirim sayısı;
        /// </summary>
        public int TodayNotificationCount;
        {
            get => _todayNotificationCount;
            private set => SetField(ref _todayNotificationCount, value);
        }
        private int _todayNotificationCount;

        /// <summary>
        /// Okunmamış bildirim sayısı;
        /// </summary>
        public int UnreadNotificationCount;
        {
            get => _unreadNotificationCount;
            private set => SetField(ref _unreadNotificationCount, value);
        }
        private int _unreadNotificationCount;

        /// <summary>
        /// Bildirim sesi etkin mi;
        /// </summary>
        public bool IsSoundEnabled;
        {
            get => _isSoundEnabled;
            set => SetField(ref _isSoundEnabled, value);
        }
        private bool _isSoundEnabled = true;

        /// <summary>
        /// Bildirimler etkin mi;
        /// </summary>
        public bool IsEnabled;
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }
        private bool _isEnabled = true;

        /// <summary>
        /// Öncelikli bildirim modu etkin mi;
        /// </summary>
        public bool IsPriorityModeEnabled;
        {
            get => _isPriorityModeEnabled;
            set => SetField(ref _isPriorityModeEnabled, value);
        }
        private bool _isPriorityModeEnabled;

        /// <summary>
        /// Notifier durumu;
        /// </summary>
        public NotifierState State;
        {
            get => _state;
            private set => SetField(ref _state, value);
        }
        private NotifierState _state = NotifierState.Stopped;

        /// <summary>
        /// Son bildirim zamanı;
        /// </summary>
        public DateTime LastNotificationTime;
        {
            get => _lastNotificationTime;
            private set => SetField(ref _lastNotificationTime, value);
        }
        private DateTime _lastNotificationTime;

        /// <summary>
        /// Aktif kanallar;
        /// </summary>
        public ObservableCollection<NotificationChannel> ActiveChannels { get; } = new ObservableCollection<NotificationChannel>();

        /// <summary>
        /// Kullanıcı ayarları;
        /// </summary>
        public NotifierSettings Settings;
        {
            get => _settings;
            set => SetField(ref _settings, value);
        }
        private NotifierSettings _settings = NotifierSettings.Default;

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Notifier'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(string userId = null, CancellationToken cancellationToken = default)
        {
            if (State != NotifierState.Stopped)
                throw new InvalidOperationException($"Notifier is already in {State} state");

            try
            {
                State = NotifierState.Initializing;
                _notifierCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                _logger.LogInformation("Initializing notifier...");

                // Kullanıcı tercihlerini yükle;
                if (!string.IsNullOrEmpty(userId))
                {
                    await LoadUserPreferencesAsync(userId, _notifierCts.Token);
                }

                // Geçmiş bildirimleri yükle;
                await _historyManager.LoadHistoryAsync(_notifierCts.Token);

                // Timer'ları başlat;
                StartTimers();

                // İstatistikleri güncelle;
                await UpdateStatisticsAsync(_notifierCts.Token);

                State = NotifierState.Running;
                _isInitialized = true;

                await _eventBus.PublishAsync(new NotifierInitializedEvent;
                {
                    NotifierId = GetNotifierId(),
                    UserId = userId,
                    Timestamp = DateTime.UtcNow;
                }, _notifierCts.Token);

                _logger.LogInformation("Notifier initialized successfully");
            }
            catch (Exception ex)
            {
                State = NotifierState.Error;
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "Initialize",
                    UserId = userId,
                    AdditionalData = new { State = State }
                });
                throw;
            }
        }

        /// <summary>
        /// Notifier'ı durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (State == NotifierState.Stopped)
                return;

            try
            {
                State = NotifierState.ShuttingDown;

                _logger.LogInformation("Shutting down notifier...");

                // Timer'ları durdur;
                StopTimers();

                // Aktif bildirimleri temizle;
                await ClearAllNotificationsAsync();

                // İptal token'ını iptal et;
                _notifierCts?.Cancel();
                _notifierCts?.Dispose();

                // Olay aboneliklerini temizle;
                UnsubscribeFromEvents();

                // Geçmişi kaydet;
                await _historyManager.SaveHistoryAsync();

                State = NotifierState.Stopped;
                _isInitialized = false;

                await _eventBus.PublishAsync(new NotifierShutdownEvent;
                {
                    NotifierId = GetNotifierId(),
                    Timestamp = DateTime.UtcNow;
                }, CancellationToken.None);

                _logger.LogInformation("Notifier shutdown completed");
            }
            catch (Exception ex)
            {
                State = NotifierState.Error;
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "Shutdown",
                    AdditionalData = new { State = State }
                });
                throw;
            }
        }

        /// <summary>
        /// Bildirim gönderir;
        /// </summary>
        public async Task<NotificationResult> SendNotificationAsync(NotificationRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (State != NotifierState.Running || !IsEnabled)
            {
                return NotificationResult.Failed("Notifier is not running or disabled");
            }

            var notificationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                // İstek doğrulama;
                var validation = ValidateNotificationRequest(request);
                if (!validation.IsValid)
                {
                    return NotificationResult.Failed($"Invalid notification request: {string.Join(", ", validation.Errors)}");
                }

                // Kullanıcı tercihlerini kontrol et;
                if (!ShouldSendNotification(request))
                {
                    _logger.LogDebug($"Notification suppressed by user preferences for: {request.Title}");
                    return NotificationResult.Suppressed(notificationId, "Notification suppressed by user preferences");
                }

                // Bildirim oluştur;
                var notification = await CreateNotificationAsync(request, notificationId, cancellationToken);

                // Bildirimi kuyruğa ekle;
                _notificationQueue.Enqueue(notification);
                PendingNotificationCount = _notificationQueue.Count;

                // Öncelikli bildirim ise hemen işle;
                if (notification.Priority == NotificationPriority.Critical || request.RequireImmediate)
                {
                    await ProcessNotificationImmediatelyAsync(notification, cancellationToken);
                }

                // İstatistikleri güncelle;
                await UpdateStatisticsAsync(cancellationToken);

                var result = NotificationResult.Success(notificationId, notification);

                await _eventBus.PublishAsync(new NotificationQueuedEvent;
                {
                    NotificationId = notificationId,
                    Title = notification.Title,
                    Type = notification.Type,
                    Priority = notification.Priority,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Notification queued: {notification.Title} (ID: {notificationId}, Type: {notification.Type})");

                return result;
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "SendNotification",
                    AdditionalData = new { RequestTitle = request.Title, NotificationId = notificationId }
                });

                return NotificationResult.Failed($"Failed to send notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Toplu bildirim gönderir;
        /// </summary>
        public async Task<BatchNotificationResult> SendBatchNotificationsAsync(
            IEnumerable<NotificationRequest> requests,
            BatchNotificationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            var batchId = Guid.NewGuid().ToString();
            var results = new List<NotificationResult>();
            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogInformation($"Starting batch notification (Batch ID: {batchId}, Count: {requests.Count()})");

                options ??= BatchNotificationOptions.Default;

                // Paralel işleme;
                var tasks = new List<Task<NotificationResult>>();

                foreach (var request in requests)
                {
                    if (options.MaxConcurrent > 0 && tasks.Count >= options.MaxConcurrent)
                    {
                        // Tamamlanan görevleri bekle;
                        var completed = await Task.WhenAny(tasks);
                        tasks.Remove(completed);
                        results.Add(await completed);
                    }

                    tasks.Add(SendNotificationAsync(request, cancellationToken));
                }

                // Kalan görevleri bekle;
                var remainingResults = await Task.WhenAll(tasks);
                results.AddRange(remainingResults);

                var totalTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

                var batchResult = new BatchNotificationResult;
                {
                    BatchId = batchId,
                    TotalCount = results.Count,
                    SuccessCount = results.Count(r => r.IsSuccess),
                    FailedCount = results.Count(r => !r.IsSuccess),
                    SuppressedCount = results.Count(r => r.Status == NotificationStatus.Suppressed),
                    TotalTime = totalTime,
                    Results = results,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(new BatchNotificationCompletedEvent;
                {
                    BatchId = batchId,
                    SuccessCount = batchResult.SuccessCount,
                    FailedCount = batchResult.FailedCount,
                    TotalTime = totalTime,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Batch notification completed: {batchResult.SuccessCount} success, {batchResult.FailedCount} failed");

                return batchResult;
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "SendBatchNotifications",
                    AdditionalData = new { BatchId = batchId, RequestCount = requests.Count() }
                });

                return BatchNotificationResult.Failed(batchId, $"Batch notification failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Bildirimi kapatır;
        /// </summary>
        public async Task<NotificationOperationResult> DismissNotificationAsync(string notificationId, DismissReason reason = DismissReason.UserAction, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(notificationId))
                throw new ArgumentException("Notification ID cannot be empty", nameof(notificationId));

            try
            {
                if (!_activeNotifications.TryGetValue(notificationId, out var notification))
                {
                    return NotificationOperationResult.Failed($"Notification with id '{notificationId}' not found");
                }

                // Bildirimi kapat;
                notification.Status = NotificationStatus.Dismissed;
                notification.DismissedTime = DateTime.UtcNow;
                notification.DismissReason = reason;

                // Aktif bildirimlerden kaldır;
                if (_activeNotifications.TryRemove(notificationId, out _))
                {
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        ActiveNotifications.Remove(notification);
                    });

                    // Geçmişe ekle;
                    await _historyManager.AddToHistoryAsync(notification);

                    await _eventBus.PublishAsync(new NotificationDismissedEvent;
                    {
                        NotificationId = notificationId,
                        Title = notification.Title,
                        Reason = reason,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Notification dismissed: {notification.Title} (ID: {notificationId}, Reason: {reason})");

                    return NotificationOperationResult.Success(notificationId);
                }

                return NotificationOperationResult.Failed("Failed to dismiss notification");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "DismissNotification",
                    AdditionalData = new { NotificationId = notificationId }
                });

                return NotificationOperationResult.Failed($"Failed to dismiss notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Tüm bildirimleri kapatır;
        /// </summary>
        public async Task<NotificationOperationResult> DismissAllNotificationsAsync(DismissReason reason = DismissReason.UserAction, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation($"Dismissing all notifications (Reason: {reason})");

                var notificationIds = _activeNotifications.Keys.ToList();
                var results = new List<NotificationOperationResult>();
                var dismissedCount = 0;

                foreach (var notificationId in notificationIds)
                {
                    var result = await DismissNotificationAsync(notificationId, reason, cancellationToken);
                    results.Add(result);

                    if (result.IsSuccess)
                        dismissedCount++;
                }

                // Kuyruktaki bildirimleri de temizle;
                while (_notificationQueue.TryDequeue(out _))
                {
                    // Kuyruktan kaldır;
                }
                PendingNotificationCount = 0;

                await _eventBus.PublishAsync(new AllNotificationsDismissedEvent;
                {
                    DismissedCount = dismissedCount,
                    Reason = reason,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"All notifications dismissed: {dismissedCount} notifications");

                return NotificationOperationResult.Success($"dismissed_{dismissedCount}", $"{dismissedCount} notifications dismissed");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "DismissAllNotifications"
                });

                return NotificationOperationResult.Failed($"Failed to dismiss all notifications: {ex.Message}");
            }
        }

        /// <summary>
        /// Bildirimi okundu olarak işaretler;
        /// </summary>
        public async Task<NotificationOperationResult> MarkAsReadAsync(string notificationId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(notificationId))
                throw new ArgumentException("Notification ID cannot be empty", nameof(notificationId));

            try
            {
                if (!_activeNotifications.TryGetValue(notificationId, out var notification))
                {
                    // Geçmişteki bildirimleri kontrol et;
                    var historical = await _historyManager.GetNotificationAsync(notificationId);
                    if (historical != null)
                    {
                        historical.IsRead = true;
                        await _historyManager.UpdateNotificationAsync(historical);

                        _logger.LogDebug($"Historical notification marked as read: {notificationId}");
                        return NotificationOperationResult.Success(notificationId);
                    }

                    return NotificationOperationResult.Failed($"Notification with id '{notificationId}' not found");
                }

                notification.IsRead = true;
                notification.ReadTime = DateTime.UtcNow;

                await ExecuteOnUiThreadAsync(() =>
                {
                    // UI güncellemesi;
                    var index = ActiveNotifications.IndexOf(notification);
                    if (index >= 0)
                    {
                        ActiveNotifications[index] = notification;
                    }
                });

                // Okunmamış bildirim sayısını güncelle;
                await UpdateUnreadCountAsync();

                await _eventBus.PublishAsync(new NotificationReadEvent;
                {
                    NotificationId = notificationId,
                    Title = notification.Title,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Notification marked as read: {notification.Title} (ID: {notificationId})");

                return NotificationOperationResult.Success(notificationId);
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "MarkAsRead",
                    AdditionalData = new { NotificationId = notificationId }
                });

                return NotificationOperationResult.Failed($"Failed to mark notification as read: {ex.Message}");
            }
        }

        /// <summary>
        /// Tüm bildirimleri okundu olarak işaretler;
        /// </summary>
        public async Task<NotificationOperationResult> MarkAllAsReadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Marking all notifications as read");

                var readCount = 0;

                foreach (var notification in _activeNotifications.Values)
                {
                    if (!notification.IsRead)
                    {
                        notification.IsRead = true;
                        notification.ReadTime = DateTime.UtcNow;
                        readCount++;
                    }
                }

                // UI güncellemesi;
                await ExecuteOnUiThreadAsync(() =>
                {
                    foreach (var notification in ActiveNotifications)
                    {
                        if (!notification.IsRead)
                        {
                            notification.IsRead = true;
                            notification.ReadTime = DateTime.UtcNow;
                        }
                    }
                });

                // Geçmişteki okunmamış bildirimleri de işaretle;
                var historicalReadCount = await _historyManager.MarkAllAsReadAsync();

                // Okunmamış bildirim sayısını güncelle;
                UnreadNotificationCount = 0;

                await _eventBus.PublishAsync(new AllNotificationsReadEvent;
                {
                    ReadCount = readCount + historicalReadCount,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"All notifications marked as read: {readCount} active, {historicalReadCount} historical");

                return NotificationOperationResult.Success($"read_{readCount}", $"{readCount} notifications marked as read");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "MarkAllAsRead"
                });

                return NotificationOperationResult.Failed($"Failed to mark all notifications as read: {ex.Message}");
            }
        }

        /// <summary>
        /// Bildirim şablonu kaydeder;
        /// </summary>
        public async Task<TemplateOperationResult> RegisterTemplateAsync(NotificationTemplate template, CancellationToken cancellationToken = default)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));

            if (string.IsNullOrWhiteSpace(template.Id))
                template.Id = Guid.NewGuid().ToString();

            try
            {
                // Şablon doğrulama;
                var validation = ValidateTemplate(template);
                if (!validation.IsValid)
                {
                    return TemplateOperationResult.Failed($"Template validation failed: {string.Join(", ", validation.Errors)}");
                }

                // Çakışma kontrolü;
                if (_templates.ContainsKey(template.Id))
                {
                    return TemplateOperationResult.Failed($"Template with id '{template.Id}' already exists");
                }

                if (_templates.TryAdd(template.Id, template))
                {
                    await _eventBus.PublishAsync(new TemplateRegisteredEvent;
                    {
                        TemplateId = template.Id,
                        TemplateName = template.Name,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Notification template registered: {template.Name} (ID: {template.Id})");

                    return TemplateOperationResult.Success(template.Id);
                }

                return TemplateOperationResult.Failed("Failed to register template");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "RegisterTemplate",
                    AdditionalData = new { TemplateId = template.Id, TemplateName = template.Name }
                });

                return TemplateOperationResult.Failed($"Failed to register template: {ex.Message}");
            }
        }

        /// <summary>
        /// Bildirim kanalı kaydeder;
        /// </summary>
        public async Task<ChannelOperationResult> RegisterChannelAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));

            if (string.IsNullOrWhiteSpace(channel.Id))
                channel.Id = Guid.NewGuid().ToString();

            try
            {
                // Kanal doğrulama;
                var validation = ValidateChannel(channel);
                if (!validation.IsValid)
                {
                    return ChannelOperationResult.Failed($"Channel validation failed: {string.Join(", ", validation.Errors)}");
                }

                // Çakışma kontrolü;
                if (_channels.ContainsKey(channel.Id))
                {
                    return ChannelOperationResult.Failed($"Channel with id '{channel.Id}' already exists");
                }

                // Kanalı başlat;
                await channel.InitializeAsync(cancellationToken);

                if (_channels.TryAdd(channel.Id, channel))
                {
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        ActiveChannels.Add(channel);
                    });

                    await _eventBus.PublishAsync(new ChannelRegisteredEvent;
                    {
                        ChannelId = channel.Id,
                        ChannelName = channel.Name,
                        ChannelType = channel.Type,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Notification channel registered: {channel.Name} (ID: {channel.Id}, Type: {channel.Type})");

                    return ChannelOperationResult.Success(channel.Id);
                }

                return ChannelOperationResult.Failed("Failed to register channel");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "RegisterChannel",
                    AdditionalData = new { ChannelId = channel.Id, ChannelName = channel.Name }
                });

                return ChannelOperationResult.Failed($"Failed to register channel: {ex.Message}");
            }
        }

        /// <summary>
        /// Bildirim geçmişini getirir;
        /// </summary>
        public async Task<NotificationHistory> GetNotificationHistoryAsync(
            DateTime? startDate = null,
            DateTime? endDate = null,
            NotificationType? type = null,
            int maxCount = 100,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var history = await _historyManager.GetHistoryAsync(startDate, endDate, type, maxCount, cancellationToken);

                // Aktif bildirimleri de ekle;
                var activeNotifications = _activeNotifications.Values;
                    .Where(n => !n.IsExpired)
                    .Select(n => new HistoricalNotification(n))
                    .ToList();

                history.Notifications.InsertRange(0, activeNotifications);
                history.TotalCount = history.Notifications.Count;

                return history;
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "GetNotificationHistory"
                });
                throw;
            }
        }

        /// <summary>
        /// Bildirim istatistiklerini getirir;
        /// </summary>
        public async Task<NotificationStatistics> GetStatisticsAsync(
            TimeSpan? period = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = new NotificationStatistics;
                {
                    PeriodStart = period.HasValue ? DateTime.UtcNow - period.Value : DateTime.MinValue,
                    PeriodEnd = DateTime.UtcNow,
                    GeneratedAt = DateTime.UtcNow;
                };

                // Aktif bildirim istatistikleri;
                stats.ActiveNotifications = _activeNotifications.Count;
                stats.PendingNotifications = PendingNotificationCount;
                stats.UnreadNotifications = UnreadNotificationCount;

                // Tip bazlı istatistikler;
                stats.ByType = _activeNotifications.Values;
                    .Concat(await _historyManager.GetRecentNotificationsAsync(100, cancellationToken))
                    .GroupBy(n => n.Type)
                    .Select(g => new NotificationTypeStatistics;
                    {
                        Type = g.Key,
                        Count = g.Count(),
                        UnreadCount = g.Count(n => !n.IsRead)
                    })
                    .ToList();

                // Öncelik bazlı istatistikler;
                stats.ByPriority = _activeNotifications.Values;
                    .Concat(await _historyManager.GetRecentNotificationsAsync(100, cancellationToken))
                    .GroupBy(n => n.Priority)
                    .Select(g => new NotificationPriorityStatistics;
                    {
                        Priority = g.Key,
                        Count = g.Count(),
                        AverageDisplayTime = g.Average(n => n.DisplayTime?.TotalSeconds ?? 0)
                    })
                    .ToList();

                // Kanal bazlı istatistikler;
                stats.ByChannel = _channels.Values;
                    .Select(c => new NotificationChannelStatistics;
                    {
                        ChannelId = c.Id,
                        ChannelName = c.Name,
                        ChannelType = c.Type,
                        SuccessRate = c.GetSuccessRate(),
                        AverageDeliveryTime = c.GetAverageDeliveryTime()
                    })
                    .ToList();

                // Günlük istatistikler;
                var today = DateTime.Today;
                stats.TodayCount = _activeNotifications.Values.Count(n => n.CreatedTime.Date == today) +
                                  (await _historyManager.GetNotificationsByDateAsync(today, cancellationToken)).Count;

                return stats;
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "GetStatistics"
                });
                throw;
            }
        }

        /// <summary>
        /// Öncelik modunu değiştirir;
        /// </summary>
        public async Task EnablePriorityModeAsync(bool enable, CancellationToken cancellationToken = default)
        {
            if (IsPriorityModeEnabled == enable)
                return;

            try
            {
                _logger.LogInformation($"{(enable ? "Enabling" : "Disabling")} priority mode");

                IsPriorityModeEnabled = enable;

                if (enable)
                {
                    // Öncelik modunda sadece kritik bildirimler gösterilir;
                    await FilterPriorityNotificationsAsync(cancellationToken);
                }
                else;
                {
                    // Tüm bildirimleri geri yükle;
                    await RestoreAllNotificationsAsync(cancellationToken);
                }

                await _eventBus.PublishAsync(new PriorityModeChangedEvent;
                {
                    IsEnabled = enable,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Priority mode {(enable ? "enabled" : "disabled")}");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Notifier",
                    Operation = "EnablePriorityMode"
                });
                throw;
            }
        }

        /// <summary>
        /// Test bildirimi gönderir;
        /// </summary>
        public async Task<NotificationResult> SendTestNotificationAsync(
            NotificationType type = NotificationType.Info,
            NotificationPriority priority = NotificationPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            var request = new NotificationRequest;
            {
                Title = "Test Notification",
                Message = $"This is a test notification of type {type} with priority {priority}",
                Type = type,
                Priority = priority,
                Category = "Test",
                RequireAcknowledgment = false,
                ExpirationTime = TimeSpan.FromMinutes(5)
            };

            return await SendNotificationAsync(request, cancellationToken);
        }

        #endregion;

        #region Private Methods;

        private void LoadDefaultTemplates()
        {
            var defaultTemplates = new[]
            {
                new NotificationTemplate;
                {
                    Id = "info_default",
                    Name = "Default Info Template",
                    Type = NotificationType.Info,
                    TitleTemplate = "{Title}",
                    MessageTemplate = "{Message}",
                    Icon = "ℹ️",
                    Color = "#2196F3",
                    Sound = "info.wav",
                    DefaultDuration = TimeSpan.FromSeconds(10)
                },
                new NotificationTemplate;
                {
                    Id = "warning_default",
                    Name = "Default Warning Template",
                    Type = NotificationType.Warning,
                    TitleTemplate = "⚠️ {Title}",
                    MessageTemplate = "{Message}",
                    Icon = "⚠️",
                    Color = "#FF9800",
                    Sound = "warning.wav",
                    DefaultDuration = TimeSpan.FromSeconds(15)
                },
                new NotificationTemplate;
                {
                    Id = "error_default",
                    Name = "Default Error Template",
                    Type = NotificationType.Error,
                    TitleTemplate = "❌ {Title}",
                    MessageTemplate = "{Message}",
                    Icon = "❌",
                    Color = "#F44336",
                    Sound = "error.wav",
                    DefaultDuration = TimeSpan.FromSeconds(20)
                },
                new NotificationTemplate;
                {
                    Id = "success_default",
                    Name = "Default Success Template",
                    Type = NotificationType.Success,
                    TitleTemplate = "✅ {Title}",
                    MessageTemplate = "{Message}",
                    Icon = "✅",
                    Color = "#4CAF50",
                    Sound = "success.wav",
                    DefaultDuration = TimeSpan.FromSeconds(8)
                }
            };

            foreach (var template in defaultTemplates)
            {
                _templates.TryAdd(template.Id, template);
            }
        }

        private void LoadDefaultChannels()
        {
            var defaultChannels = new[]
            {
                new NotificationChannel;
                {
                    Id = "toast_channel",
                    Name = "Toast Notifications",
                    Type = NotificationChannelType.Toast,
                    IsEnabled = true,
                    Priority = ChannelPriority.High,
                    Settings = new ChannelSettings;
                    {
                        Position = ToastPosition.BottomRight,
                        Animation = ToastAnimation.Slide,
                        MaxToasts = 3;
                    }
                },
                new NotificationChannel;
                {
                    Id = "banner_channel",
                    Name = "Banner Notifications",
                    Type = NotificationChannelType.Banner,
                    IsEnabled = true,
                    Priority = ChannelPriority.Normal,
                    Settings = new ChannelSettings;
                    {
                        Position = BannerPosition.Top,
                        AutoDismiss = true,
                        DismissTime = TimeSpan.FromSeconds(5)
                    }
                }
            };

            foreach (var channel in defaultChannels)
            {
                _channels.TryAdd(channel.Id, channel);
                ActiveChannels.Add(channel);
            }
        }

        private async Task LoadUserPreferencesAsync(string userId, CancellationToken cancellationToken)
        {
            try
            {
                // Burada kullanıcı tercihleri veritabanından yüklenir;
                // Şimdilik varsayılan tercihler kullanılıyor;

                var preference = new NotificationPreference;
                {
                    UserId = userId,
                    IsEnabled = true,
                    SoundEnabled = true,
                    DoNotDisturb = false,
                    DoNotDisturbStart = TimeSpan.FromHours(22), // 22:00;
                    DoNotDisturbEnd = TimeSpan.FromHours(8),    // 08:00;
                    MutedCategories = new HashSet<string>(),
                    PriorityCategories = new HashSet<string> { "System", "Security", "Critical" },
                    ChannelPreferences = new Dictionary<string, bool>
                    {
                        ["toast_channel"] = true,
                        ["banner_channel"] = true;
                    }
                };

                _userPreferences[userId] = preference;

                // Ayarları uygula;
                IsEnabled = preference.IsEnabled;
                IsSoundEnabled = preference.SoundEnabled;
                Settings = NotifierSettings.FromPreference(preference);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load user preferences: {ex.Message}");
            }
        }

        private async Task<Notification> CreateNotificationAsync(NotificationRequest request, string notificationId, CancellationToken cancellationToken)
        {
            // Şablonu bul veya varsayılan şablonu kullan;
            var template = FindTemplate(request) ?? GetDefaultTemplate(request.Type);

            // Bildirimi oluştur;
            var notification = new Notification;
            {
                Id = notificationId,
                Title = ApplyTemplate(template.TitleTemplate, request),
                Message = ApplyTemplate(template.MessageTemplate, request),
                Type = request.Type,
                Priority = request.Priority,
                Category = request.Category,
                Source = request.Source,
                Icon = template.Icon,
                Color = template.Color,
                CreatedTime = DateTime.UtcNow,
                ExpirationTime = request.ExpirationTime ?? template.DefaultDuration,
                RequireAcknowledgment = request.RequireAcknowledgment,
                Actions = request.Actions?.Select(a => a.Clone()).ToList() ?? new List<NotificationAction>(),
                Data = request.Data != null ? new Dictionary<string, object>(request.Data) : new Dictionary<string, object>(),
                TemplateId = template.Id,
                Sound = template.Sound,
                Status = NotificationStatus.Pending;
            };

            // Özel alanları ekle;
            if (request.CustomFields != null)
            {
                foreach (var field in request.CustomFields)
                {
                    notification.CustomFields[field.Key] = field.Value;
                }
            }

            await Task.CompletedTask;
            return notification;
        }

        private async Task ProcessNotificationImmediatelyAsync(Notification notification, CancellationToken cancellationToken)
        {
            try
            {
                // Bildirimi aktif listeye ekle;
                if (_activeNotifications.TryAdd(notification.Id, notification))
                {
                    // UI thread'inde çalıştır;
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        ActiveNotifications.Insert(0, notification); // En üste ekle;
                    });

                    // Ses çal (eğer etkinse)
                    if (IsSoundEnabled && !string.IsNullOrEmpty(notification.Sound))
                    {
                        await _soundPlayer.PlayAsync(notification.Sound, notification.Type, cancellationToken);
                    }

                    // Kanallara dağıt;
                    await DeliverToChannelsAsync(notification, cancellationToken);

                    notification.Status = NotificationStatus.Active;
                    notification.DisplayTime = DateTime.UtcNow;
                    LastNotificationTime = DateTime.UtcNow;

                    // Süre dolunca otomatik kapatma;
                    if (notification.ExpirationTime.HasValue && notification.ExpirationTime.Value > TimeSpan.Zero)
                    {
                        _ = Task.Delay(notification.ExpirationTime.Value).ContinueWith(async _ =>
                        {
                            if (_activeNotifications.ContainsKey(notification.Id))
                            {
                                await DismissNotificationAsync(notification.Id, DismissReason.Expired, cancellationToken);
                            }
                        }, cancellationToken);
                    }

                    await _eventBus.PublishAsync(new NotificationDisplayedEvent;
                    {
                        NotificationId = notification.Id,
                        Title = notification.Title,
                        Type = notification.Type,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogDebug($"Notification displayed immediately: {notification.Title}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to process notification immediately: {ex.Message}");
                notification.Status = NotificationStatus.Failed;
            }
        }

        private async Task ProcessNotificationQueueAsync()
        {
            if (_notificationQueue.IsEmpty || !IsEnabled)
                return;

            try
            {
                // Öncelikli bildirim modunda sadece kritik bildirimleri işle;
                if (IsPriorityModeEnabled)
                {
                    // Kritik bildirimleri bul;
                    var criticalNotifications = _notificationQueue;
                        .Where(n => n.Priority == NotificationPriority.Critical)
                        .ToList();

                    foreach (var notification in criticalNotifications)
                    {
                        if (_notificationQueue.TryDequeue(out var dequeued) && dequeued.Id == notification.Id)
                        {
                            await ProcessNotificationImmediatelyAsync(notification, _notifierCts?.Token ?? CancellationToken.None);
                        }
                    }
                }
                else;
                {
                    // Sıradaki bildirimi işle;
                    if (_notificationQueue.TryDequeue(out var notification))
                    {
                        await ProcessNotificationImmediatelyAsync(notification, _notifierCts?.Token ?? CancellationToken.None);
                    }
                }

                PendingNotificationCount = _notificationQueue.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to process notification queue: {ex.Message}");
            }
        }

        private async Task DeliverToChannelsAsync(Notification notification, CancellationToken cancellationToken)
        {
            var deliveryTasks = new List<Task<ChannelDeliveryResult>>();

            foreach (var channel in _channels.Values.Where(c => c.IsEnabled))
            {
                // Kanal tercihlerini kontrol et;
                if (!ShouldDeliverToChannel(notification, channel))
                    continue;

                // Dağıtım görevi oluştur;
                deliveryTasks.Add(channel.DeliverAsync(notification, cancellationToken));
            }

            if (deliveryTasks.Count > 0)
            {
                var results = await Task.WhenAll(deliveryTasks);

                // Sonuçları işle;
                foreach (var result in results)
                {
                    if (result.IsSuccess)
                    {
                        notification.DeliveredChannels.Add(result.ChannelId);
                        _logger.LogDebug($"Notification delivered to channel: {result.ChannelId}");
                    }
                    else;
                    {
                        _logger.LogWarning($"Failed to deliver to channel {result.ChannelId}: {result.Error}");
                    }
                }
            }
        }

        private async Task CleanupExpiredNotificationsAsync()
        {
            try
            {
                var expiredIds = new List<string>();
                var now = DateTime.UtcNow;

                foreach (var kvp in _activeNotifications)
                {
                    var notification = kvp.Value;

                    // Süresi dolmuş bildirimleri bul;
                    if (notification.IsExpired ||
                        (notification.ExpirationTime.HasValue &&
                         notification.CreatedTime + notification.ExpirationTime.Value < now))
                    {
                        expiredIds.Add(kvp.Key);
                    }
                }

                // Süresi dolmuş bildirimleri kapat;
                foreach (var id in expiredIds)
                {
                    await DismissNotificationAsync(id, DismissReason.Expired, _notifierCts?.Token ?? CancellationToken.None);
                }

                // Eski geçmiş kayıtlarını temizle;
                await _historyManager.CleanupOldRecordsAsync(_configuration.HistoryRetentionDays);

                _logger.LogDebug($"Cleanup completed: {expiredIds.Count} notifications expired");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Cleanup failed: {ex.Message}");
            }
        }

        private async Task ClearAllNotificationsAsync()
        {
            // Aktif bildirimleri temizle;
            var notificationIds = _activeNotifications.Keys.ToList();
            foreach (var id in notificationIds)
            {
                await DismissNotificationAsync(id, DismissReason.SystemShutdown, CancellationToken.None);
            }

            // Kuyruğu temizle;
            while (_notificationQueue.TryDequeue(out _))
            {
                // Kuyruktan kaldır;
            }
            PendingNotificationCount = 0;

            await ExecuteOnUiThreadAsync(() =>
            {
                ActiveNotifications.Clear();
            });

            _logger.LogInformation("All notifications cleared");
        }

        private async Task UpdateStatisticsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Bekleyen bildirim sayısı;
                PendingNotificationCount = _notificationQueue.Count;

                // Bugünkü bildirim sayısı;
                var today = DateTime.Today;
                TodayNotificationCount = _activeNotifications.Values.Count(n => n.CreatedTime.Date == today) +
                                        (await _historyManager.GetNotificationsByDateAsync(today, cancellationToken)).Count;

                // Okunmamış bildirim sayısı;
                await UpdateUnreadCountAsync();

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to update statistics: {ex.Message}");
            }
        }

        private async Task UpdateUnreadCountAsync()
        {
            var unreadCount = _activeNotifications.Values.Count(n => !n.IsRead);
            var historicalUnread = await _historyManager.GetUnreadCountAsync();

            UnreadNotificationCount = unreadCount + historicalUnread;
        }

        private async Task FilterPriorityNotificationsAsync(CancellationToken cancellationToken)
        {
            // Öncelik modunda sadece yüksek öncelikli bildirimleri göster;
            var nonPriorityIds = _activeNotifications;
                .Where(kvp => kvp.Value.Priority < NotificationPriority.High)
                .Select(kvp => kvp.Key)
                .ToList();

            // Düşük öncelikli bildirimleri geçici olarak sakla;
            foreach (var id in nonPriorityIds)
            {
                if (_activeNotifications.TryRemove(id, out var notification))
                {
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        ActiveNotifications.Remove(notification);
                    });

                    // Geçici olarak kuyruğa geri ekle;
                    _notificationQueue.Enqueue(notification);
                }
            }

            PendingNotificationCount = _notificationQueue.Count;
        }

        private async Task RestoreAllNotificationsAsync(CancellationToken cancellationToken)
        {
            // Tüm bildirimleri geri yükle;
            while (_notificationQueue.TryDequeue(out var notification))
            {
                if (_activeNotifications.TryAdd(notification.Id, notification))
                {
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        ActiveNotifications.Add(notification);
                    });
                }
            }

            PendingNotificationCount = 0;
        }

        private NotificationTemplate FindTemplate(NotificationRequest request)
        {
            // Özel şablon ID'si varsa onu kullan;
            if (!string.IsNullOrEmpty(request.TemplateId) && _templates.TryGetValue(request.TemplateId, out var template))
                return template;

            // Tip ve kategoriye göre şablon ara;
            return _templates.Values.FirstOrDefault(t =>
                t.Type == request.Type &&
                (string.IsNullOrEmpty(t.Category) || t.Category == request.Category));
        }

        private NotificationTemplate GetDefaultTemplate(NotificationType type)
        {
            return _templates.Values.FirstOrDefault(t => t.Type == type) ?? _templates["info_default"];
        }

        private string ApplyTemplate(string template, NotificationRequest request)
        {
            if (string.IsNullOrEmpty(template))
                return string.Empty;

            var result = template;
                .Replace("{Title}", request.Title ?? string.Empty)
                .Replace("{Message}", request.Message ?? string.Empty)
                .Replace("{Category}", request.Category ?? string.Empty)
                .Replace("{Source}", request.Source ?? string.Empty)
                .Replace("{Priority}", request.Priority.ToString())
                .Replace("{Type}", request.Type.ToString());

            // Özel alanları yerine koy;
            if (request.CustomFields != null)
            {
                foreach (var field in request.CustomFields)
                {
                    result = result.Replace($"{{{field.Key}}}", field.Value?.ToString() ?? string.Empty);
                }
            }

            return result;
        }

        private bool ShouldSendNotification(NotificationRequest request)
        {
            // Rahatsız etmeyin modu kontrolü;
            if (Settings.DoNotDisturb && IsDoNotDisturbTime())
            {
                return request.Priority >= NotificationPriority.High; // Sadece yüksek öncelikliler;
            }

            // Kategori kontrolü;
            if (!string.IsNullOrEmpty(request.Category) && Settings.MutedCategories.Contains(request.Category))
            {
                return request.Priority >= NotificationPriority.High; // Sessize alınmış kategoriler;
            }

            return true;
        }

        private bool ShouldDeliverToChannel(Notification notification, NotificationChannel channel)
        {
            // Kanal etkin değilse;
            if (!channel.IsEnabled)
                return false;

            // Öncelik eşleşmesi;
            if (notification.Priority < GetMinimumPriorityForChannel(channel))
                return false;

            // Kategori filtrelemesi;
            if (channel.FilteredCategories != null && channel.FilteredCategories.Contains(notification.Category))
                return false;

            // Kullanıcı tercihleri;
            if (Settings.ChannelPreferences != null &&
                Settings.ChannelPreferences.TryGetValue(channel.Id, out var isEnabled))
            {
                return isEnabled;
            }

            return true;
        }

        private NotificationPriority GetMinimumPriorityForChannel(NotificationChannel channel)
        {
            return channel.Priority switch;
            {
                ChannelPriority.Low => NotificationPriority.Low,
                ChannelPriority.Normal => NotificationPriority.Normal,
                ChannelPriority.High => NotificationPriority.High,
                ChannelPriority.Critical => NotificationPriority.Critical,
                _ => NotificationPriority.Normal;
            };
        }

        private bool IsDoNotDisturbTime()
        {
            if (!Settings.DoNotDisturb)
                return false;

            var now = DateTime.Now.TimeOfDay;
            return now >= Settings.DoNotDisturbStart && now <= Settings.DoNotDisturbEnd;
        }

        private ValidationResult ValidateNotificationRequest(NotificationRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.Title))
                errors.Add("Title cannot be empty");

            if (string.IsNullOrWhiteSpace(request.Message))
                errors.Add("Message cannot be empty");

            if (request.ExpirationTime.HasValue && request.ExpirationTime.Value <= TimeSpan.Zero)
                errors.Add("Expiration time must be positive");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        private ValidationResult ValidateTemplate(NotificationTemplate template)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(template.Name))
                errors.Add("Template name cannot be empty");

            if (string.IsNullOrWhiteSpace(template.TitleTemplate))
                errors.Add("Title template cannot be empty");

            if (template.DefaultDuration <= TimeSpan.Zero)
                errors.Add("Default duration must be positive");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        private ValidationResult ValidateChannel(NotificationChannel channel)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(channel.Name))
                errors.Add("Channel name cannot be empty");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        private void StartTimers()
        {
            _cleanupTimer.Start();
            _deliveryTimer.Start();
            _uiTimer.Start();
        }

        private void StopTimers()
        {
            _cleanupTimer.Stop();
            _deliveryTimer.Stop();
            _uiTimer.Stop();
        }

        private void OnUiTimerTick(object sender, EventArgs e)
        {
            // UI güncellemeleri burada yapılır;
            // Örneğin: bildirim sürelerini güncelle, animasyonları yönet, vs.
            UpdateNotificationDisplayTimes();
        }

        private void UpdateNotificationDisplayTimes()
        {
            var now = DateTime.UtcNow;

            foreach (var notification in ActiveNotifications)
            {
                if (notification.DisplayTime.HasValue)
                {
                    notification.DisplayDuration = now - notification.DisplayTime.Value;

                    // Süre dolmak üzereyse görsel uyarı;
                    if (notification.ExpirationTime.HasValue)
                    {
                        var remaining = notification.ExpirationTime.Value - notification.DisplayDuration;
                        notification.IsExpiringSoon = remaining < TimeSpan.FromSeconds(10);
                    }
                }
            }
        }

        private async Task ExecuteOnUiThreadAsync(Action action)
        {
            try
            {
                // WPF için Dispatcher kullanımı;
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(action);
                }
                else;
                {
                    // Alternatif: Task.Run ile thread havuzu;
                    await Task.Run(action);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"UI thread execution failed: {ex.Message}");
            }
        }

        private string GetNotifierId()
        {
            return $"notifier_{Environment.MachineName}_{GetHashCode()}";
        }

        private void SubscribeToEvents()
        {
            // Sistem olaylarına abone ol;
            _eventBus.Subscribe<SystemAlertEvent>(HandleSystemAlert);
            _eventBus.Subscribe<UserActivityEvent>(HandleUserActivity);
        }

        private void UnsubscribeFromEvents()
        {
            _eventBus.Unsubscribe<SystemAlertEvent>(HandleSystemAlert);
            _eventBus.Unsubscribe<UserActivityEvent>(HandleUserActivity);
        }

        private async Task HandleSystemAlert(SystemAlertEvent alert)
        {
            // Sistem uyarılarını bildirim olarak göster;
            var request = new NotificationRequest;
            {
                Title = alert.AlertType,
                Message = alert.Message,
                Type = GetNotificationTypeFromAlert(alert.Severity),
                Priority = GetNotificationPriorityFromAlert(alert.Severity),
                Category = "System",
                Source = alert.Source,
                RequireAcknowledgment = alert.RequireAcknowledgment,
                Data = new Dictionary<string, object>
                {
                    ["AlertId"] = alert.AlertId,
                    ["Severity"] = alert.Severity;
                }
            };

            await SendNotificationAsync(request, _notifierCts?.Token ?? CancellationToken.None);
        }

        private NotificationType GetNotificationTypeFromAlert(string severity)
        {
            return severity.ToLower() switch;
            {
                "critical" or "error" => NotificationType.Error,
                "warning" => NotificationType.Warning,
                "info" => NotificationType.Info,
                _ => NotificationType.Info;
            };
        }

        private NotificationPriority GetNotificationPriorityFromAlert(string severity)
        {
            return severity.ToLower() switch;
            {
                "critical" => NotificationPriority.Critical,
                "error" => NotificationPriority.High,
                "warning" => NotificationPriority.Normal,
                "info" => NotificationPriority.Low,
                _ => NotificationPriority.Normal;
            };
        }

        private async Task HandleUserActivity(UserActivityEvent activity)
        {
            // Kullanıcı aktifse rahatsız etmeyin modunu kontrol et;
            if (activity.ActivityType == "UserActive")
            {
                // Kullanıcı aktif, bildirimleri normal moda al;
                if (Settings.DoNotDisturb)
                {
                    Settings.DoNotDisturb = false;
                    _logger.LogDebug("Do not disturb disabled due to user activity");
                }
            }
            else if (activity.ActivityType == "UserInactive")
            {
                // Kullanıcı inaktif, rahatsız etmeyin modunu etkinleştir;
                if (!Settings.DoNotDisturb && IsDoNotDisturbTime())
                {
                    Settings.DoNotDisturb = true;
                    _logger.LogDebug("Do not disturb enabled due to user inactivity");
                }
            }

            await Task.CompletedTask;
        }

        private void OnNotifierPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Property değişikliklerini işle;
            switch (e.PropertyName)
            {
                case nameof(Settings):
                    OnSettingsChanged();
                    break;
                case nameof(IsEnabled):
                    OnEnabledChanged();
                    break;
                case nameof(IsSoundEnabled):
                    OnSoundEnabledChanged();
                    break;
            }
        }

        private void OnSettingsChanged()
        {
            // Ayarlar değiştiğinde timer'ları güncelle;
            _logger.LogDebug("Notifier settings changed");
        }

        private void OnEnabledChanged()
        {
            if (!IsEnabled)
            {
                // Notifier devre dışı bırakıldı, tüm bildirimleri kapat;
                _ = ClearAllNotificationsAsync();
            }

            _logger.LogInformation($"Notifier {(IsEnabled ? "enabled" : "disabled")}");
        }

        private void OnSoundEnabledChanged()
        {
            _logger.LogDebug($"Notification sound {(IsSoundEnabled ? "enabled" : "disabled")}");
        }

        #endregion;

        #region INotifyPropertyChanged Implementation;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
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
                    // Notifier'ı durdur;
                    _ = ShutdownAsync().ConfigureAwait(false);

                    // Timer'ları dispose et;
                    _cleanupTimer?.Dispose();
                    _deliveryTimer?.Dispose();
                    _uiTimer?.Stop();

                    // CancellationTokenSource'ı dispose et;
                    _notifierCts?.Dispose();

                    // Ses oynatıcıyı dispose et;
                    _soundPlayer?.Dispose();

                    // Olay aboneliklerini temizle;
                    PropertyChanged -= OnNotifierPropertyChanged;
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

    #region Supporting Classes;

    /// <summary>
    /// Bildirim tipi;
    /// </summary>
    public enum NotificationType;
    {
        Info,
        Warning,
        Error,
        Success,
        Custom;
    }

    /// <summary>
    /// Bildirim önceliği;
    /// </summary>
    public enum NotificationPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    /// <summary>
    /// Bildirim durumu;
    /// </summary>
    public enum NotificationStatus;
    {
        Pending,
        Active,
        Read,
        Dismissed,
        Expired,
        Failed,
        Suppressed;
    }

    /// <summary>
    /// Bildirim kanal tipi;
    /// </summary>
    public enum NotificationChannelType;
    {
        Toast,
        Banner,
        Popup,
        InApp,
        Tray,
        Sound,
        Email,
        Push;
    }

    /// <summary>
    /// Kanal önceliği;
    /// </summary>
    public enum ChannelPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    /// <summary>
    /// Toast pozisyonu;
    /// </summary>
    public enum ToastPosition;
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        TopCenter,
        BottomCenter;
    }

    /// <summary>
    /// Banner pozisyonu;
    /// </summary>
    public enum BannerPosition;
    {
        Top,
        Bottom;
    }

    /// <summary>
    /// Toast animasyonu;
    /// </summary>
    public enum ToastAnimation;
    {
        None,
        Slide,
        Fade,
        Scale,
        Custom;
    }

    /// <summary>
    /// Kapatma sebebi;
    /// </summary>
    public enum DismissReason;
    {
        UserAction,
        Expired,
        SystemAction,
        Replaced,
        SystemShutdown;
    }

    /// <summary>
    /// Notifier durumu;
    /// </summary>
    public enum NotifierState;
    {
        Stopped,
        Initializing,
        Running,
        ShuttingDown,
        Error;
    }

    /// <summary>
    /// Bildirim isteği;
    /// </summary>
    public class NotificationRequest;
    {
        public string Title { get; set; }
        public string Message { get; set; }
        public NotificationType Type { get; set; } = NotificationType.Info;
        public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
        public string Category { get; set; }
        public string Source { get; set; }
        public string TemplateId { get; set; }
        public bool RequireAcknowledgment { get; set; }
        public TimeSpan? ExpirationTime { get; set; }
        public bool RequireImmediate { get; set; }
        public List<NotificationAction> Actions { get; set; } = new List<NotificationAction>();
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> CustomFields { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Bildirim;
    /// </summary>
    public class Notification : INotifyPropertyChanged;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public NotificationType Type { get; set; }
        public NotificationPriority Priority { get; set; }
        public string Category { get; set; }
        public string Source { get; set; }
        public string Icon { get; set; }
        public string Color { get; set; }
        public string Sound { get; set; }
        public string TemplateId { get; set; }

        private NotificationStatus _status;
        public NotificationStatus Status;
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        private bool _isRead;
        public bool IsRead;
        {
            get => _isRead;
            set => SetField(ref _isRead, value);
        }

        public DateTime CreatedTime { get; set; }
        public DateTime? DisplayTime { get; set; }
        public DateTime? ReadTime { get; set; }
        public DateTime? DismissedTime { get; set; }
        public TimeSpan? DisplayDuration { get; set; }
        public TimeSpan? ExpirationTime { get; set; }
        public DismissReason? DismissReason { get; set; }
        public bool RequireAcknowledgment { get; set; }

        private bool _isExpiringSoon;
        public bool IsExpiringSoon;
        {
            get => _isExpiringSoon;
            set => SetField(ref _isExpiringSoon, value);
        }

        public List<NotificationAction> Actions { get; set; } = new List<NotificationAction>();
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> CustomFields { get; set; } = new Dictionary<string, string>();
        public HashSet<string> DeliveredChannels { get; set; } = new HashSet<string>();

        public bool IsExpired;
        {
            get;
            {
                if (!ExpirationTime.HasValue || !DisplayTime.HasValue)
                    return false;

                return DateTime.UtcNow > DisplayTime.Value + ExpirationTime.Value;
            }
        }

        #region INotifyPropertyChanged;
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        #endregion;
    }

    /// <summary>
    /// Bildirim eylemi;
    /// </summary>
    public class NotificationAction;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Label { get; set; }
        public string ActionType { get; set; }
        public object ActionData { get; set; }
        public string Icon { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsDestructive { get; set; }

        public NotificationAction Clone()
        {
            return new NotificationAction;
            {
                Id = Guid.NewGuid().ToString(),
                Label = Label,
                ActionType = ActionType,
                ActionData = ActionData,
                Icon = Icon,
                IsPrimary = IsPrimary,
                IsDestructive = IsDestructive;
            };
        }
    }

    /// <summary>
    /// Bildirim şablonu;
    /// </summary>
    public class NotificationTemplate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public NotificationType Type { get; set; }
        public string Category { get; set; }
        public string TitleTemplate { get; set; }
        public string MessageTemplate { get; set; }
        public string Icon { get; set; }
        public string Color { get; set; }
        public string Sound { get; set; }
        public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(10);
        public Dictionary<string, object> DefaultData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Bildirim kanalı;
    /// </summary>
    public class NotificationChannel : INotifyPropertyChanged;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public NotificationChannelType Type { get; set; }
        public ChannelPriority Priority { get; set; } = ChannelPriority.Normal;

        private bool _isEnabled = true;
        public bool IsEnabled;
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }

        public HashSet<string> FilteredCategories { get; set; } = new HashSet<string>();
        public ChannelSettings Settings { get; set; } = new ChannelSettings();

        private int _deliveryCount;
        public int DeliveryCount;
        {
            get => _deliveryCount;
            private set => SetField(ref _deliveryCount, value);
        }

        private int _successCount;
        public int SuccessCount;
        {
            get => _successCount;
            private set => SetField(ref _successCount, value);
        }

        private double _averageDeliveryTime;
        public double AverageDeliveryTime;
        {
            get => _averageDeliveryTime;
            private set => SetField(ref _averageDeliveryTime, value);
        }

        private readonly List<double> _deliveryTimes = new List<double>();

        public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public virtual async Task<ChannelDeliveryResult> DeliverAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            DeliveryCount++;
            var startTime = DateTime.UtcNow;

            try
            {
                // Kanal tipine göre dağıtım;
                bool success = await DeliverByTypeAsync(notification, cancellationToken);

                var deliveryTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _deliveryTimes.Add(deliveryTime);
                AverageDeliveryTime = _deliveryTimes.Average();

                if (success)
                {
                    SuccessCount++;
                    return ChannelDeliveryResult.Success(Id, deliveryTime);
                }
                else;
                {
                    return ChannelDeliveryResult.Failed(Id, "Delivery failed", deliveryTime);
                }
            }
            catch (Exception ex)
            {
                var deliveryTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                return ChannelDeliveryResult.Failed(Id, ex.Message, deliveryTime);
            }
        }

        protected virtual Task<bool> DeliverByTypeAsync(Notification notification, CancellationToken cancellationToken)
        {
            // Türetilen sınıflar bu metodu override eder;
            return Task.FromResult(true);
        }

        public virtual Task CleanupAsync()
        {
            return Task.CompletedTask;
        }

        public double GetSuccessRate()
        {
            return DeliveryCount > 0 ? (double)SuccessCount / DeliveryCount : 0;
        }

        public double GetAverageDeliveryTime()
        {
            return AverageDeliveryTime;
        }

        #region INotifyPropertyChanged;
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        #endregion;
    }

    /// <summary>
    /// Kanal ayarları;
    /// </summary>
    public class ChannelSettings;
    {
        public ToastPosition Position { get; set; } = ToastPosition.BottomRight;
        public ToastAnimation Animation { get; set; } = ToastAnimation.Slide;
        public BannerPosition BannerPosition { get; set; } = BannerPosition.Top;
        public bool AutoDismiss { get; set; } = true;
        public TimeSpan DismissTime { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxToasts { get; set; } = 3;
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Bildirim tercihi;
    /// </summary>
    public class NotificationPreference;
    {
        public string UserId { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool SoundEnabled { get; set; } = true;
        public bool DoNotDisturb { get; set; }
        public TimeSpan DoNotDisturbStart { get; set; }
        public TimeSpan DoNotDisturbEnd { get; set; }
        public HashSet<string> MutedCategories { get; set; } = new HashSet<string>();
        public HashSet<string> PriorityCategories { get; set; } = new HashSet<string>();
        public Dictionary<string, bool> ChannelPreferences { get; set; } = new Dictionary<string, bool>();
    }

    /// <summary>
    /// Notifier ayarları;
    /// </summary>
    public class NotifierSettings;
    {
        public static NotifierSettings Default => new NotifierSettings();

        public bool SoundEnabled { get; set; } = true;
        public bool DoNotDisturb { get; set; }
        public TimeSpan DoNotDisturbStart { get; set; } = TimeSpan.FromHours(22);
        public TimeSpan DoNotDisturbEnd { get; set; } = TimeSpan.FromHours(8);
        public HashSet<string> MutedCategories { get; set; } = new HashSet<string>();
        public Dictionary<string, bool> ChannelPreferences { get; set; } = new Dictionary<string, bool>();

        public static NotifierSettings FromPreference(NotificationPreference preference)
        {
            return new NotifierSettings;
            {
                SoundEnabled = preference.SoundEnabled,
                DoNotDisturb = preference.DoNotDisturb,
                DoNotDisturbStart = preference.DoNotDisturbStart,
                DoNotDisturbEnd = preference.DoNotDisturbEnd,
                MutedCategories = new HashSet<string>(preference.MutedCategories),
                ChannelPreferences = new Dictionary<string, bool>(preference.ChannelPreferences)
            };
        }
    }

    /// <summary>
    /// Notifier konfigürasyonu;
    /// </summary>
    public class NotifierConfiguration;
    {
        public static NotifierConfiguration Default => new NotifierConfiguration();

        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan DeliveryCheckInterval { get; set; } = TimeSpan.FromSeconds(1);
        public int UiUpdateIntervalMs { get; set; } = 100;
        public int MaxActiveNotifications { get; set; } = 10;
        public int MaxQueueSize { get; set; } = 100;
        public int HistoryRetentionDays { get; set; } = 30;
        public bool EnableSound { get; set; } = true;
        public string SoundDirectory { get; set; } = "Sounds";
        public Dictionary<NotificationType, string> DefaultSounds { get; set; } = new Dictionary<NotificationType, string>
        {
            [NotificationType.Info] = "info.wav",
            [NotificationType.Warning] = "warning.wav",
            [NotificationType.Error] = "error.wav",
            [NotificationType.Success] = "success.wav"
        };
    }

    /// <summary>
    /// Toplu bildirim seçenekleri;
    /// </summary>
    public class BatchNotificationOptions;
    {
        public static BatchNotificationOptions Default => new BatchNotificationOptions();

        public int MaxConcurrent { get; set; } = 5;
        public bool StopOnFirstFailure { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Tarihi bildirim;
    /// </summary>
    public class HistoricalNotification;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public NotificationType Type { get; set; }
        public NotificationPriority Priority { get; set; }
        public string Category { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime? ReadTime { get; set; }
        public DateTime? DismissedTime { get; set; }
        public DismissReason? DismissReason { get; set; }

        public HistoricalNotification(Notification notification)
        {
            Id = notification.Id;
            Title = notification.Title;
            Message = notification.Message;
            Type = notification.Type;
            Priority = notification.Priority;
            Category = notification.Category;
            IsRead = notification.IsRead;
            CreatedTime = notification.CreatedTime;
            ReadTime = notification.ReadTime;
            DismissedTime = notification.DismissedTime;
            DismissReason = notification.DismissReason;
        }
    }

    /// <summary>
    /// Bildirim geçmişi;
    /// </summary>
    public class NotificationHistory;
    {
        public int TotalCount { get; set; }
        public int UnreadCount { get; set; }
        public List<HistoricalNotification> Notifications { get; set; } = new List<HistoricalNotification>();
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Bildirim istatistikleri;
    /// </summary>
    public class NotificationStatistics;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public DateTime GeneratedAt { get; set; }
        public int ActiveNotifications { get; set; }
        public int PendingNotifications { get; set; }
        public int UnreadNotifications { get; set; }
        public int TodayCount { get; set; }
        public List<NotificationTypeStatistics> ByType { get; set; } = new List<NotificationTypeStatistics>();
        public List<NotificationPriorityStatistics> ByPriority { get; set; } = new List<NotificationPriorityStatistics>();
        public List<NotificationChannelStatistics> ByChannel { get; set; } = new List<NotificationChannelStatistics>();
    }

    /// <summary>
    /// Bildirim tipi istatistikleri;
    /// </summary>
    public class NotificationTypeStatistics;
    {
        public NotificationType Type { get; set; }
        public int Count { get; set; }
        public int UnreadCount { get; set; }
    }

    /// <summary>
    /// Bildirim önceliği istatistikleri;
    /// </summary>
    public class NotificationPriorityStatistics;
    {
        public NotificationPriority Priority { get; set; }
        public int Count { get; set; }
        public double AverageDisplayTime { get; set; }
    }

    /// <summary>
    /// Bildirim kanalı istatistikleri;
    /// </summary>
    public class NotificationChannelStatistics;
    {
        public string ChannelId { get; set; }
        public string ChannelName { get; set; }
        public NotificationChannelType ChannelType { get; set; }
        public double SuccessRate { get; set; }
        public double AverageDeliveryTime { get; set; }
    }

    /// <summary>
    /// Doğrulama sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Bildirim sonucu;
    /// </summary>
    public class NotificationResult;
    {
        public bool IsSuccess { get; set; }
        public string NotificationId { get; set; }
        public Notification Notification { get; set; }
        public NotificationStatus Status { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static NotificationResult Success(string notificationId, Notification notification, string message = null)
        {
            return new NotificationResult;
            {
                IsSuccess = true,
                NotificationId = notificationId,
                Notification = notification,
                Status = NotificationStatus.Pending,
                Message = message ?? "Notification sent successfully"
            };
        }

        public static NotificationResult Failed(string error)
        {
            return new NotificationResult;
            {
                IsSuccess = false,
                Status = NotificationStatus.Failed,
                Error = error;
            };
        }

        public static NotificationResult Suppressed(string notificationId, string reason)
        {
            return new NotificationResult;
            {
                IsSuccess = true,
                NotificationId = notificationId,
                Status = NotificationStatus.Suppressed,
                Message = $"Notification suppressed: {reason}"
            };
        }
    }

    /// <summary>
    /// Toplu bildirim sonucu;
    /// </summary>
    public class BatchNotificationResult;
    {
        public string BatchId { get; set; }
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int SuppressedCount { get; set; }
        public double TotalTime { get; set; }
        public List<NotificationResult> Results { get; set; } = new List<NotificationResult>();
        public DateTime Timestamp { get; set; }

        public static BatchNotificationResult Failed(string batchId, string error)
        {
            return new BatchNotificationResult;
            {
                BatchId = batchId,
                FailedCount = 1,
                TotalTime = 0,
                Timestamp = DateTime.UtcNow;
            };
        }
    }

    /// <summary>
    /// Bildirim operasyon sonucu;
    /// </summary>
    public class NotificationOperationResult;
    {
        public bool IsSuccess { get; set; }
        public string NotificationId { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static NotificationOperationResult Success(string notificationId, string message = null)
        {
            return new NotificationOperationResult;
            {
                IsSuccess = true,
                NotificationId = notificationId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static NotificationOperationResult Failed(string error)
        {
            return new NotificationOperationResult;
            {
                IsSuccess = false,
                Error = error;
            };
        }
    }

    /// <summary>
    /// Şablon operasyon sonucu;
    /// </summary>
    public class TemplateOperationResult;
    {
        public bool IsSuccess { get; set; }
        public string TemplateId { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static TemplateOperationResult Success(string templateId, string message = null)
        {
            return new TemplateOperationResult;
            {
                IsSuccess = true,
                TemplateId = templateId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static TemplateOperationResult Failed(string error)
        {
            return new TemplateOperationResult;
            {
                IsSuccess = false,
                Error = error;
            };
        }
    }

    /// <summary>
    /// Kanal operasyon sonucu;
    /// </summary>
    public class ChannelOperationResult;
    {
        public bool IsSuccess { get; set; }
        public string ChannelId { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static ChannelOperationResult Success(string channelId, string message = null)
        {
            return new ChannelOperationResult;
            {
                IsSuccess = true,
                ChannelId = channelId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static ChannelOperationResult Failed(string error)
        {
            return new ChannelOperationResult;
            {
                IsSuccess = false,
                Error = error;
            };
        }
    }

    /// <summary>
    /// Kanal dağıtım sonucu;
    /// </summary>
    public class ChannelDeliveryResult;
    {
        public bool IsSuccess { get; set; }
        public string ChannelId { get; set; }
        public double DeliveryTime { get; set; }
        public string Error { get; set; }

        public static ChannelDeliveryResult Success(string channelId, double deliveryTime)
        {
            return new ChannelDeliveryResult;
            {
                IsSuccess = true,
                ChannelId = channelId,
                DeliveryTime = deliveryTime;
            };
        }

        public static ChannelDeliveryResult Failed(string channelId, string error, double deliveryTime = 0)
        {
            return new ChannelDeliveryResult;
            {
                IsSuccess = false,
                ChannelId = channelId,
                DeliveryTime = deliveryTime,
                Error = error;
            };
        }
    }

    #endregion;

    #region Internal Helper Classes;

    /// <summary>
    /// Bildirim ses oynatıcı;
    /// </summary>
    internal class NotificationSoundPlayer : IDisposable
    {
        private readonly NotifierConfiguration _configuration;
        private readonly Dictionary<string, SoundPlayer> _soundCache;
        private readonly object _lock = new object();
        private bool _isDisposed;

        public NotificationSoundPlayer(NotifierConfiguration configuration)
        {
            _configuration = configuration;
            _soundCache = new Dictionary<string, SoundPlayer>();
        }

        public async Task PlayAsync(string soundFile, NotificationType type, CancellationToken cancellationToken = default)
        {
            if (!_configuration.EnableSound || string.IsNullOrEmpty(soundFile))
                return;

            try
            {
                // Ses dosyası yolunu oluştur;
                var soundPath = GetSoundPath(soundFile, type);
                if (string.IsNullOrEmpty(soundPath) || !System.IO.File.Exists(soundPath))
                    return;

                await Task.Run(() =>
                {
                    lock (_lock)
                    {
                        if (_isDisposed)
                            return;

                        try
                        {
                            // Ses önbelleğinde varsa onu kullan;
                            if (!_soundCache.TryGetValue(soundPath, out var player))
                            {
                                player = new SoundPlayer(soundPath);
                                _soundCache[soundPath] = player;
                            }

                            // Ses çal;
                            player.Play();
                        }
                        catch
                        {
                            // Ses çalma hatasını sessizce yut;
                        }
                    }
                }, cancellationToken);
            }
            catch
            {
                // Hata durumunda sessiz kal;
            }
        }

        private string GetSoundPath(string soundFile, NotificationType type)
        {
            // Önce belirtilen dosya;
            if (System.IO.File.Exists(soundFile))
                return soundFile;

            // Ses dizinindeki dosya;
            var pathInDirectory = System.IO.Path.Combine(_configuration.SoundDirectory, soundFile);
            if (System.IO.File.Exists(pathInDirectory))
                return pathInDirectory;

            // Varsayılan ses dosyası;
            if (_configuration.DefaultSounds.TryGetValue(type, out var defaultSound))
            {
                var defaultPath = System.IO.Path.Combine(_configuration.SoundDirectory, defaultSound);
                if (System.IO.File.Exists(defaultPath))
                    return defaultPath;
            }

            return null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (!_isDisposed)
                {
                    _isDisposed = true;

                    foreach (var player in _soundCache.Values)
                    {
                        player.Dispose();
                    }
                    _soundCache.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Bildirim geçmiş yöneticisi;
    /// </summary>
    internal class NotificationHistoryManager;
    {
        private readonly NotifierConfiguration _configuration;
        private readonly List<HistoricalNotification> _history;
        private readonly string _historyFilePath;
        private readonly object _lock = new object();

        public NotificationHistoryManager(NotifierConfiguration configuration)
        {
            _configuration = configuration;
            _history = new List<HistoricalNotification>();
            _historyFilePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEDA",
                "NotificationHistory.json");
        }

        public async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!System.IO.File.Exists(_historyFilePath))
                    return;

                var json = await System.IO.File.ReadAllTextAsync(_historyFilePath, cancellationToken);
                var history = JsonSerializer.Deserialize<List<HistoricalNotification>>(json);

                lock (_lock)
                {
                    _history.Clear();
                    if (history != null)
                    {
                        _history.AddRange(history);
                    }
                }
            }
            catch
            {
                // Geçmiş yüklenemezse boş liste kullan;
                lock (_lock) _history.Clear();
            }
        }

        public async Task SaveHistoryAsync()
        {
            try
            {
                // Dizini oluştur;
                var directory = System.IO.Path.GetDirectoryName(_historyFilePath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                List<HistoricalNotification> copy;
                lock (_lock)
                {
                    copy = new List<HistoricalNotification>(_history);
                }

                var json = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(_historyFilePath, json);
            }
            catch
            {
                // Kaydetme hatasını yut;
            }
        }

        public async Task AddToHistoryAsync(Notification notification)
        {
            var historical = new HistoricalNotification(notification);

            lock (_lock)
            {
                _history.Add(historical);

                // Tarih limitini kontrol et;
                if (_history.Count > 1000) // Maksimum kayıt sayısı;
                {
                    _history.RemoveRange(0, _history.Count - 1000);
                }
            }

            await SaveHistoryAsync();
        }

        public async Task UpdateNotificationAsync(HistoricalNotification notification)
        {
            lock (_lock)
            {
                var existing = _history.FirstOrDefault(n => n.Id == notification.Id);
                if (existing != null)
                {
                    _history.Remove(existing);
                    _history.Add(notification);
                }
            }

            await SaveHistoryAsync();
        }

        public async Task<NotificationHistory> GetHistoryAsync(
            DateTime? startDate,
            DateTime? endDate,
            NotificationType? type,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            List<HistoricalNotification> filtered;

            lock (_lock)
            {
                filtered = _history;
                    .Where(n => (!startDate.HasValue || n.CreatedTime >= startDate.Value) &&
                               (!endDate.HasValue || n.CreatedTime <= endDate.Value) &&
                               (!type.HasValue || n.Type == type.Value))
                    .OrderByDescending(n => n.CreatedTime)
                    .Take(maxCount)
                    .ToList();
            }

            var history = new NotificationHistory;
            {
                Notifications = filtered,
                UnreadCount = filtered.Count(n => !n.IsRead),
                GeneratedAt = DateTime.UtcNow;
            };

            await Task.CompletedTask;
            return history;
        }

        public async Task<List<HistoricalNotification>> GetRecentNotificationsAsync(int count, CancellationToken cancellationToken = default)
        {
            List<HistoricalNotification> recent;

            lock (_lock)
            {
                recent = _history;
                    .OrderByDescending(n => n.CreatedTime)
                    .Take(count)
                    .ToList();
            }

            await Task.CompletedTask;
            return recent;
        }

        public async Task<List<HistoricalNotification>> GetNotificationsByDateAsync(DateTime date, CancellationToken cancellationToken = default)
        {
            List<HistoricalNotification> byDate;

            lock (_lock)
            {
                byDate = _history;
                    .Where(n => n.CreatedTime.Date == date)
                    .ToList();
            }

            await Task.CompletedTask;
            return byDate;
        }

        public async Task<int> GetUnreadCountAsync()
        {
            int count;

            lock (_lock)
            {
                count = _history.Count(n => !n.IsRead);
            }

            await Task.CompletedTask;
            return count;
        }

        public async Task<int> MarkAllAsReadAsync()
        {
            int count = 0;

            lock (_lock)
            {
                foreach (var notification in _history.Where(n => !n.IsRead))
                {
                    notification.IsRead = true;
                    notification.ReadTime = DateTime.UtcNow;
                    count++;
                }
            }

            await SaveHistoryAsync();
            return count;
        }

        public async Task CleanupOldRecordsAsync(int retentionDays)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            lock (_lock)
            {
                _history.RemoveAll(n => n.CreatedTime < cutoffDate);
            }

            await SaveHistoryAsync();
        }

        public async Task<HistoricalNotification> GetNotificationAsync(string notificationId)
        {
            HistoricalNotification notification;

            lock (_lock)
            {
                notification = _history.FirstOrDefault(n => n.Id == notificationId);
            }

            await Task.CompletedTask;
            return notification;
        }
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Notifier başlatıldı olayı;
    /// </summary>
    public class NotifierInitializedEvent : IEvent;
    {
        public string NotifierId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Notifier durduruldu olayı;
    /// </summary>
    public class NotifierShutdownEvent : IEvent;
    {
        public string NotifierId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Bildirim kuyruğa eklendi olayı;
    /// </summary>
    public class NotificationQueuedEvent : IEvent;
    {
        public string NotificationId { get; set; }
        public string Title { get; set; }
        public NotificationType Type { get; set; }
        public NotificationPriority Priority { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Bildirim gösterildi olayı;
    /// </summary>
    public class NotificationDisplayedEvent : IEvent;
    {
        public string NotificationId { get; set; }
        public string Title { get; set; }
        public NotificationType Type { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Bildirim kapatıldı olayı;
    /// </summary>
    public class NotificationDismissedEvent : IEvent;
    {
        public string NotificationId { get; set; }
        public string Title { get; set; }
        public DismissReason Reason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tüm bildirimler kapatıldı olayı;
    /// </summary>
    public class AllNotificationsDismissedEvent : IEvent;
    {
        public int DismissedCount { get; set; }
        public DismissReason Reason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Bildirim okundu olayı;
    /// </summary>
    public class NotificationReadEvent : IEvent;
    {
        public string NotificationId { get; set; }
        public string Title { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tüm bildirimler okundu olayı;
    /// </summary>
    public class AllNotificationsReadEvent : IEvent;
    {
        public int ReadCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Şablon kaydedildi olayı;
    /// </summary>
    public class TemplateRegisteredEvent : IEvent;
    {
        public string TemplateId { get; set; }
        public string TemplateName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kanal kaydedildi olayı;
    /// </summary>
    public class ChannelRegisteredEvent : IEvent;
    {
        public string ChannelId { get; set; }
        public string ChannelName { get; set; }
        public NotificationChannelType ChannelType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Toplu bildirim tamamlandı olayı;
    /// </summary>
    public class BatchNotificationCompletedEvent : IEvent;
    {
        public string BatchId { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public double TotalTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Öncelik modu değişti olayı;
    /// </summary>
    public class PriorityModeChangedEvent : IEvent;
    {
        public bool IsEnabled { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sistem uyarı olayı;
    /// </summary>
    public class SystemAlertEvent : IEvent;
    {
        public string AlertId { get; set; }
        public string AlertType { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
        public string Source { get; set; }
        public bool RequireAcknowledgment { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kullanıcı aktivite olayı;
    /// </summary>
    public class UserActivityEvent : IEvent;
    {
        public string UserId { get; set; }
        public string ActivityType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}

namespace NEDA.Interface.VisualInterface.NotificationSystem.Contracts;
{
    /// <summary>
    /// Notifier servisi interface'i;
    /// </summary>
    public interface INotifier : INotifyPropertyChanged, IDisposable;
    {
        // Properties;
        ObservableCollection<Notification> ActiveNotifications { get; }
        int PendingNotificationCount { get; }
        int TodayNotificationCount { get; }
        int UnreadNotificationCount { get; }
        bool IsSoundEnabled { get; set; }
        bool IsEnabled { get; set; }
        bool IsPriorityModeEnabled { get; set; }
        NotifierState State { get; }
        DateTime LastNotificationTime { get; }
        ObservableCollection<NotificationChannel> ActiveChannels { get; }
        NotifierSettings Settings { get; set; }

        // Methods;
        Task InitializeAsync(string userId = null, CancellationToken cancellationToken = default);
        Task ShutdownAsync();

        Task<NotificationResult> SendNotificationAsync(
            NotificationRequest request,
            CancellationToken cancellationToken = default);

        Task<BatchNotificationResult> SendBatchNotificationsAsync(
            IEnumerable<NotificationRequest> requests,
            BatchNotificationOptions options = null,
            CancellationToken cancellationToken = default);

        Task<NotificationOperationResult> DismissNotificationAsync(
            string notificationId,
            DismissReason reason = DismissReason.UserAction,
            CancellationToken cancellationToken = default);

        Task<NotificationOperationResult> DismissAllNotificationsAsync(
            DismissReason reason = DismissReason.UserAction,
            CancellationToken cancellationToken = default);

        Task<NotificationOperationResult> MarkAsReadAsync(
            string notificationId,
            CancellationToken cancellationToken = default);

        Task<NotificationOperationResult> MarkAllAsReadAsync(
            CancellationToken cancellationToken = default);

        Task<TemplateOperationResult> RegisterTemplateAsync(
            NotificationTemplate template,
            CancellationToken cancellationToken = default);

        Task<ChannelOperationResult> RegisterChannelAsync(
            NotificationChannel channel,
            CancellationToken cancellationToken = default);

        Task<NotificationHistory> GetNotificationHistoryAsync(
            DateTime? startDate = null,
            DateTime? endDate = null,
            NotificationType? type = null,
            int maxCount = 100,
            CancellationToken cancellationToken = default);

        Task<NotificationStatistics> GetStatisticsAsync(
            TimeSpan? period = null,
            CancellationToken cancellationToken = default);

        Task EnablePriorityModeAsync(
            bool enable,
            CancellationToken cancellationToken = default);

        Task<NotificationResult> SendTestNotificationAsync(
            NotificationType type = NotificationType.Info,
            NotificationPriority priority = NotificationPriority.Normal,
            CancellationToken cancellationToken = default);
    }
}
