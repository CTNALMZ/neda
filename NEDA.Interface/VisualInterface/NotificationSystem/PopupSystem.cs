using NEDA.Core.Common;
using NEDA.Core.Configuration;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.HardwareSecurity.USBCryptoToken;
using NEDA.Interface.VisualInterface.Controls;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace NEDA.Interface.VisualInterface.NotificationSystem;
{
    /// <summary>
    /// Popup bildirim sistemi - Kullanıcıya popup mesajları gösterir ve yönetir;
    /// </summary>
    public class PopupSystem : IPopupSystem, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly INotificationManager _notificationManager;

        private readonly ConcurrentDictionary<string, PopupNotification> _activePopups;
        private readonly ConcurrentQueue<PopupNotification> _notificationQueue;
        private readonly ConcurrentDictionary<PopupPriority, List<PopupNotification>> _priorityQueues;

        private readonly Subject<PopupEvent> _popupEvents;
        private readonly Subject<PopupNotification> _notificationEvents;

        private Timer _cleanupTimer;
        private Timer _displayTimer;
        private Timer _animationTimer;

        private readonly object _displayLock = new object();
        private bool _isInitialized;
        private bool _isDisplaying;
        private PopupSystemConfiguration _configuration;
        private PopupStatistics _statistics;
        private PopupDisplayManager _displayManager;

        /// <summary>
        /// Popup sistemi durumu;
        /// </summary>
        public PopupSystemState State { get; private set; }

        /// <summary>
        /// Popup olayları (reaktif)
        /// </summary>
        public IObservable<PopupEvent> PopupEvents => _popupEvents.AsObservable();
        public IObservable<PopupNotification> NotificationEvents => _notificationEvents.AsObservable();

        /// <summary>
        /// Aktif popup sayısı;
        /// </summary>
        public int ActivePopupCount => _activePopups.Count;

        /// <summary>
        /// Kuyruktaki bildirim sayısı;
        /// </summary>
        public int QueuedNotificationCount => _notificationQueue.Count;

        /// <summary>
        /// Yeni bir PopupSystem örneği oluşturur;
        /// </summary>
        public PopupSystem(
            ILogger logger,
            IEventBus eventBus,
            INotificationManager notificationManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));

            _activePopups = new ConcurrentDictionary<string, PopupNotification>();
            _notificationQueue = new ConcurrentQueue<PopupNotification>();
            _priorityQueues = new ConcurrentDictionary<PopupPriority, List<PopupNotification>>();

            _popupEvents = new Subject<PopupEvent>();
            _notificationEvents = new Subject<PopupNotification>();

            _configuration = new PopupSystemConfiguration();
            _statistics = new PopupStatistics();
            State = PopupSystemState.Stopped;

            InitializePriorityQueues();
        }

        /// <summary>
        /// Popup sistemini başlatır;
        /// </summary>
        public async Task InitializeAsync(PopupSystemConfiguration configuration = null)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Popup sistemi başlatılıyor...");

                // Yapılandırmayı güncelle;
                if (configuration != null)
                    _configuration = configuration;
                else;
                    await LoadConfigurationAsync();

                State = PopupSystemState.Initializing;

                // Display manager'ı başlat;
                _displayManager = new PopupDisplayManager(_configuration);
                await _displayManager.InitializeAsync();

                // Event bus kaydı;
                await RegisterEventHandlersAsync();

                // Timer'ları başlat;
                InitializeTimers();

                // Stilleri yükle;
                await LoadPopupStylesAsync();

                // İstatistikleri sıfırla;
                ResetStatistics();

                _isInitialized = true;
                State = PopupSystemState.Ready;

                _logger.LogInformation("Popup sistemi başarıyla başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Popup sistemi başlatma hatası: {ex.Message}", ex);
                State = PopupSystemState.Error;
                throw new PopupSystemException("Popup sistemi başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Popup bildirimi gösterir;
        /// </summary>
        public async Task<PopupResult> ShowPopupAsync(PopupNotification notification, ShowPopupOptions options = null)
        {
            ValidateSystemState();

            try
            {
                if (notification == null)
                    throw new ArgumentNullException(nameof(notification));

                // Bildirimi hazırla;
                PrepareNotification(notification);

                var showOptions = options ?? new ShowPopupOptions();
                var startTime = DateTime.UtcNow;

                _logger.LogDebug($"Popup bildirimi oluşturuluyor: {notification.Title}");

                // Önceliğe göre işlem;
                if (showOptions.Immediate || notification.Priority == PopupPriority.Critical)
                {
                    // Hemen göster;
                    return await DisplayPopupImmediatelyAsync(notification, showOptions);
                }
                else;
                {
                    // Kuyruğa al;
                    return await QueueNotificationAsync(notification, showOptions);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Popup gösterim hatası: {ex.Message}", ex);
                throw new PopupShowException($"Popup gösterilemedi: {notification?.Title}", ex);
            }
        }

        /// <summary>
        /// Kullanıcı tanımlı popup gösterir;
        /// </summary>
        public async Task<PopupResult> ShowCustomPopupAsync(
            CustomPopupRequest request,
            ShowPopupOptions options = null)
        {
            ValidateSystemState();

            try
            {
                if (request == null)
                    throw new ArgumentNullException(nameof(request));

                var customOptions = options ?? new ShowPopupOptions();

                // Özel popup oluştur;
                var popup = await CreateCustomPopupAsync(request);

                // Popup'ı göster;
                var result = await ShowPopupAsync(popup, customOptions);

                return new PopupResult;
                {
                    Success = result.Success,
                    PopupId = result.PopupId,
                    CustomData = popup.CustomData,
                    DisplayTime = result.DisplayTime;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Özel popup gösterim hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Onay popup'ı gösterir;
        /// </summary>
        public async Task<ConfirmationResult> ShowConfirmationAsync(
            ConfirmationRequest request,
            ShowPopupOptions options = null)
        {
            ValidateSystemState();

            try
            {
                if (request == null)
                    throw new ArgumentNullException(nameof(request));

                var confirmationOptions = options ?? new ShowPopupOptions();

                // Onay popup'ı oluştur;
                var confirmationPopup = await CreateConfirmationPopupAsync(request);

                // Popup'ı göster ve sonucu bekle;
                var result = await ShowPopupAndWaitForResponseAsync(confirmationPopup, confirmationOptions);

                return new ConfirmationResult;
                {
                    Confirmed = result.Confirmed,
                    SelectedOption = result.SelectedOption,
                    PopupId = result.PopupId,
                    UserInput = result.UserInput;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Onay popup'ı hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Giriş popup'ı gösterir;
        /// </summary>
        public async Task<InputResult> ShowInputPopupAsync(
            InputRequest request,
            ShowPopupOptions options = null)
        {
            ValidateSystemState();

            try
            {
                if (request == null)
                    throw new ArgumentNullException(nameof(request));

                var inputOptions = options ?? new ShowPopupOptions();

                // Giriş popup'ı oluştur;
                var inputPopup = await CreateInputPopupAsync(request);

                // Popup'ı göster ve sonucu bekle;
                var result = await ShowPopupAndWaitForResponseAsync(inputPopup, inputOptions);

                return new InputResult;
                {
                    Success = result.Success,
                    InputValue = result.InputValue,
                    PopupId = result.PopupId,
                    Cancelled = result.Cancelled;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Giriş popup'ı hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// İlerleme çubuğu popup'ı gösterir;
        /// </summary>
        public async Task<ProgressPopupResult> ShowProgressPopupAsync(
            ProgressRequest request,
            ShowPopupOptions options = null)
        {
            ValidateSystemState();

            try
            {
                if (request == null)
                    throw new ArgumentNullException(nameof(request));

                var progressOptions = options ?? new ShowPopupOptions();

                // İlerleme popup'ı oluştur;
                var progressPopup = await CreateProgressPopupAsync(request);

                // Popup'ı göster;
                var result = await ShowPopupAsync(progressPopup, progressOptions);

                return new ProgressPopupResult;
                {
                    Success = result.Success,
                    PopupId = result.PopupId,
                    ProgressPopup = progressPopup,
                    CanCancel = request.CanCancel;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"İlerleme popup'ı hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Popup'ı kapatır;
        /// </summary>
        public async Task<PopupResult> ClosePopupAsync(
            string popupId,
            ClosePopupReason reason = ClosePopupReason.UserAction,
            object resultData = null)
        {
            ValidateSystemState();

            try
            {
                if (string.IsNullOrWhiteSpace(popupId))
                    throw new ArgumentException("Popup ID'si belirtilmelidir", nameof(popupId));

                if (!_activePopups.TryRemove(popupId, out var popup))
                {
                    // Kuyruktaki popup'ı kontrol et;
                    if (TryRemoveFromQueue(popupId, out popup))
                    {
                        return new PopupResult;
                        {
                            Success = true,
                            PopupId = popupId,
                            State = PopupState.Cancelled,
                            Message = "Popup kuyruktan kaldırıldı"
                        };
                    }

                    return new PopupResult;
                    {
                        Success = false,
                        PopupId = popupId,
                        Error = "Popup bulunamadı"
                    };
                }

                // Popup durumunu güncelle;
                popup.State = PopupState.Closed;
                popup.ClosedAt = DateTime.UtcNow;
                popup.CloseReason = reason;
                popup.ResultData = resultData;

                // Display manager'dan kaldır;
                await _displayManager.RemovePopupAsync(popupId);

                // Popup olayı;
                var popupEvent = new PopupEvent;
                {
                    EventType = PopupEventType.Closed,
                    PopupId = popupId,
                    Popup = popup,
                    Timestamp = DateTime.UtcNow,
                    Reason = reason,
                    ResultData = resultData;
                };

                _popupEvents.OnNext(popupEvent);
                _notificationEvents.OnNext(popup);

                // Event bus'a gönder;
                await _eventBus.PublishAsync(new PopupClosedEvent;
                {
                    PopupId = popupId,
                    Popup = popup,
                    Reason = reason,
                    ResultData = resultData,
                    Timestamp = DateTime.UtcNow;
                });

                // İstatistikleri güncelle;
                UpdateStatisticsForClose(popup, reason);

                // Bekleyen popup'ları göster;
                await ShowNextPopupAsync();

                _logger.LogDebug($"Popup kapatıldı: {popupId} - {reason}");

                return new PopupResult;
                {
                    Success = true,
                    PopupId = popupId,
                    State = PopupState.Closed,
                    DisplayTime = popup.ClosedAt - popup.CreatedAt,
                    Message = "Popup başarıyla kapatıldı",
                    ResultData = resultData;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Popup kapatma hatası: {ex.Message}", ex);
                throw new PopupCloseException($"Popup kapatılamadı: {popupId}", ex);
            }
        }

        /// <summary>
        /// Tüm popup'ları kapatır;
        /// </summary>
        public async Task<BatchPopupResult> CloseAllPopupsAsync(
            ClosePopupReason reason = ClosePopupReason.SystemAction,
            string category = null)
        {
            ValidateSystemState();

            try
            {
                _logger.LogInformation($"Tüm popup'lar kapatılıyor: {_activePopups.Count} aktif, {_notificationQueue.Count} kuyrukta");

                var results = new List<PopupResult>();
                var popupsToClose = _activePopups.Values.ToList();

                // Kategori filtresi;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    popupsToClose = popupsToClose.Where(p => p.Category == category).ToList();
                }

                // Aktif popup'ları kapat;
                foreach (var popup in popupsToClose)
                {
                    try
                    {
                        var result = await ClosePopupAsync(popup.Id, reason);
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Popup kapatma hatası (ID: {popup.Id}): {ex.Message}");
                        results.Add(new PopupResult;
                        {
                            Success = false,
                            PopupId = popup.Id,
                            Error = ex.Message;
                        });
                    }
                }

                // Kuyruğu temizle;
                ClearQueue(category);

                return new BatchPopupResult;
                {
                    TotalProcessed = results.Count,
                    Successful = results.Count(r => r.Success),
                    Failed = results.Count(r => !r.Success),
                    Results = results;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Tüm popup'ları kapatma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Popup'ı günceller;
        /// </summary>
        public async Task<PopupResult> UpdatePopupAsync(string popupId, PopupUpdate update)
        {
            ValidateSystemState();

            try
            {
                if (string.IsNullOrWhiteSpace(popupId))
                    throw new ArgumentException("Popup ID'si belirtilmelidir", nameof(popupId));

                if (update == null)
                    throw new ArgumentNullException(nameof(update));

                if (!_activePopups.TryGetValue(popupId, out var popup))
                {
                    return new PopupResult;
                    {
                        Success = false,
                        PopupId = popupId,
                        Error = "Aktif popup bulunamadı"
                    };
                }

                // Güncellemeleri uygula;
                ApplyPopupUpdate(popup, update);

                // Display manager'ı güncelle;
                await _displayManager.UpdatePopupAsync(popupId, update);

                // Popup olayı;
                _popupEvents.OnNext(new PopupEvent;
                {
                    EventType = PopupEventType.Updated,
                    PopupId = popupId,
                    Popup = popup,
                    Timestamp = DateTime.UtcNow,
                    UpdateData = update;
                });

                _logger.LogDebug($"Popup güncellendi: {popupId}");

                return new PopupResult;
                {
                    Success = true,
                    PopupId = popupId,
                    State = popup.State,
                    Message = "Popup başarıyla güncellendi"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Popup güncelleme hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Popup istatistiklerini alır;
        /// </summary>
        public PopupStatistics GetStatistics()
        {
            ValidateSystemState();

            lock (_statistics)
            {
                return new PopupStatistics;
                {
                    StartTime = _statistics.StartTime,
                    TotalShown = _statistics.TotalShown,
                    TotalClosed = _statistics.TotalClosed,
                    TotalTimedOut = _statistics.TotalTimedOut,
                    TotalClicked = _statistics.TotalClicked,
                    AverageDisplayTime = _statistics.AverageDisplayTime,
                    MaxConcurrent = _statistics.MaxConcurrent,
                    CurrentActive = _activePopups.Count,
                    CurrentQueued = _notificationQueue.Count,
                    ByType = new Dictionary<PopupType, int>(_statistics.ByType),
                    ByPriority = new Dictionary<PopupPriority, int>(_statistics.ByPriority),
                    ByCategory = new Dictionary<string, int>(_statistics.ByCategory)
                };
            }
        }

        /// <summary>
        /// Popup geçmişini alır;
        /// </summary>
        public IEnumerable<PopupNotification> GetPopupHistory(
            DateTime? startDate = null,
            DateTime? endDate = null,
            string category = null,
            PopupType? type = null)
        {
            ValidateSystemState();

            try
            {
                // Burada kalıcı depolamadan geçmiş alınacak;
                // Şimdilik boş liste döndür;
                return new List<PopupNotification>();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Popup geçmişi alma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Popup sistemini durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Popup sistemi durduruluyor...");

                State = PopupSystemState.Stopping;

                // Tüm popup'ları kapat;
                await CloseAllPopupsAsync(ClosePopupReason.SystemShutdown);

                // Timer'ları durdur;
                StopTimers();

                // Display manager'ı durdur;
                await _displayManager.ShutdownAsync();

                // Event bus kaydını kaldır;
                await UnregisterEventHandlersAsync();

                // Kaynakları serbest bırak;
                await ReleaseResourcesAsync();

                // İstatistikleri kaydet;
                await SaveStatisticsAsync();

                _isInitialized = false;
                State = PopupSystemState.Stopped;

                _logger.LogInformation("Popup sistemi başarıyla durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Popup sistemi durdurma hatası: {ex.Message}", ex);
                throw;
            }
        }

        #region Private Methods;

        private void ValidateSystemState()
        {
            if (!_isInitialized || State != PopupSystemState.Ready)
                throw new InvalidOperationException("Popup sistemi hazır durumda değil");
        }

        private void InitializePriorityQueues()
        {
            foreach (PopupPriority priority in Enum.GetValues(typeof(PopupPriority)))
            {
                _priorityQueues[priority] = new List<PopupNotification>();
            }
        }

        private async Task<PopupResult> DisplayPopupImmediatelyAsync(PopupNotification notification, ShowPopupOptions options)
        {
            lock (_displayLock)
            {
                if (_activePopups.Count >= _configuration.MaxConcurrentPopups)
                {
                    // Maksimum sayıya ulaşıldı, kuyruğa al;
                    return QueueNotificationAsync(notification, options).Result;
                }

                notification.State = PopupState.Displaying;
                notification.DisplayedAt = DateTime.UtcNow;

                if (!_activePopups.TryAdd(notification.Id, notification))
                {
                    throw new InvalidOperationException($"Popup eklenemedi: {notification.Id}");
                }
            }

            // Display manager ile göster;
            await _displayManager.ShowPopupAsync(notification, options);

            // Popup olayı;
            var popupEvent = new PopupEvent;
            {
                EventType = PopupEventType.Displaying,
                PopupId = notification.Id,
                Popup = notification,
                Timestamp = DateTime.UtcNow;
            };

            _popupEvents.OnNext(popupEvent);
            _notificationEvents.OnNext(notification);

            // Event bus'a gönder;
            await _eventBus.PublishAsync(new PopupDisplayedEvent;
            {
                PopupId = notification.Id,
                Popup = notification,
                Timestamp = DateTime.UtcNow;
            });

            // Zaman aşımı timer'ını başlat;
            if (notification.Timeout > TimeSpan.Zero)
            {
                StartTimeoutTimer(notification);
            }

            // İstatistikleri güncelle;
            UpdateStatisticsForDisplay(notification);

            _logger.LogInformation($"Popup gösteriliyor: {notification.Id} - {notification.Title}");

            return new PopupResult;
            {
                Success = true,
                PopupId = notification.Id,
                State = PopupState.Displaying,
                DisplayTime = TimeSpan.Zero,
                Message = "Popup gösteriliyor"
            };
        }

        private async Task<PopupResult> QueueNotificationAsync(PopupNotification notification, ShowPopupOptions options)
        {
            notification.State = PopupState.Queued;

            // Öncelik kuyruğuna ekle;
            _priorityQueues[notification.Priority].Add(notification);
            _notificationQueue.Enqueue(notification);

            // Kuyruk olayı;
            _popupEvents.OnNext(new PopupEvent;
            {
                EventType = PopupEventType.Queued,
                PopupId = notification.Id,
                Popup = notification,
                Timestamp = DateTime.UtcNow;
            });

            // İstatistikleri güncelle;
            UpdateStatisticsForQueue(notification);

            return new PopupResult;
            {
                Success = true,
                PopupId = notification.Id,
                State = PopupState.Queued,
                QueuePosition = _notificationQueue.Count,
                Message = "Popup kuyruğa alındı"
            };
        }

        private async Task ShowNextPopupAsync()
        {
            if (_notificationQueue.IsEmpty || _activePopups.Count >= _configuration.MaxConcurrentPopups)
                return;

            lock (_displayLock)
            {
                if (_isDisplaying || _activePopups.Count >= _configuration.MaxConcurrentPopups)
                    return;

                _isDisplaying = true;
            }

            try
            {
                // Öncelik sırasına göre popup al;
                PopupNotification nextPopup = null;

                foreach (var priority in Enum.GetValues(typeof(PopupPriority)).Cast<PopupPriority>().OrderByDescending(p => (int)p))
                {
                    var queue = _priorityQueues[priority];
                    if (queue.Count > 0)
                    {
                        nextPopup = queue[0];
                        queue.RemoveAt(0);

                        // Ana kuyruktan da kaldır;
                        RemoveFromMainQueue(nextPopup.Id);
                        break;
                    }
                }

                if (nextPopup != null)
                {
                    await DisplayPopupImmediatelyAsync(nextPopup, new ShowPopupOptions());
                }
            }
            finally
            {
                lock (_displayLock)
                {
                    _isDisplaying = false;
                }
            }
        }

        private bool RemoveFromMainQueue(string popupId)
        {
            // Kuyruktan çıkarmak için geçici liste kullan;
            var tempList = _notificationQueue.ToList();
            _notificationQueue.Clear();
            bool removed = false;

            foreach (var item in tempList)
            {
                if (item.Id != popupId)
                {
                    _notificationQueue.Enqueue(item);
                }
                else;
                {
                    removed = true;
                }
            }

            return removed;
        }

        private bool TryRemoveFromQueue(string popupId, out PopupNotification popup)
        {
            popup = null;

            // Öncelik kuyruklarından kaldır;
            foreach (var queue in _priorityQueues.Values)
            {
                var item = queue.FirstOrDefault(p => p.Id == popupId);
                if (item != null)
                {
                    popup = item;
                    queue.Remove(item);
                    break;
                }
            }

            // Ana kuyruktan kaldır;
            if (popup != null)
            {
                RemoveFromMainQueue(popupId);
            }

            return popup != null;
        }

        private void ClearQueue(string category = null)
        {
            // Öncelik kuyruklarını temizle;
            foreach (var queue in _priorityQueues.Values)
            {
                var itemsToRemove = string.IsNullOrWhiteSpace(category)
                    ? queue.ToList()
                    : queue.Where(p => p.Category == category).ToList();

                foreach (var item in itemsToRemove)
                {
                    item.State = PopupState.Cancelled;
                    item.ClosedAt = DateTime.UtcNow;
                    item.CloseReason = ClosePopupReason.SystemAction;

                    queue.Remove(item);

                    _popupEvents.OnNext(new PopupEvent;
                    {
                        EventType = PopupEventType.Cancelled,
                        PopupId = item.Id,
                        Popup = item,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }

            // Ana kuyruğu temizle;
            var tempList = _notificationQueue.ToList();
            _notificationQueue.Clear();

            foreach (var item in tempList)
            {
                if (string.IsNullOrWhiteSpace(category) || item.Category == category)
                {
                    item.State = PopupState.Cancelled;
                    item.ClosedAt = DateTime.UtcNow;
                    item.CloseReason = ClosePopupReason.SystemAction;

                    _popupEvents.OnNext(new PopupEvent;
                    {
                        EventType = PopupEventType.Cancelled,
                        PopupId = item.Id,
                        Popup = item,
                        Timestamp = DateTime.UtcNow;
                    });
                }
                else;
                {
                    _notificationQueue.Enqueue(item);
                }
            }
        }

        private void PrepareNotification(PopupNotification notification)
        {
            if (string.IsNullOrWhiteSpace(notification.Id))
                notification.Id = GeneratePopupId();

            if (string.IsNullOrWhiteSpace(notification.Category))
                notification.Category = "General";

            notification.CreatedAt = DateTime.UtcNow;
            notification.State = PopupState.Pending;

            // Varsayılan değerleri ayarla;
            if (notification.Timeout == TimeSpan.Zero)
                notification.Timeout = _configuration.DefaultTimeout;

            if (notification.Priority == PopupPriority.Normal)
                notification.Priority = DeterminePriority(notification.Type, notification.Severity);
        }

        private PopupPriority DeterminePriority(PopupType type, PopupSeverity severity)
        {
            return (type, severity) switch;
            {
                (PopupType.Error, _) => PopupPriority.High,
                (_, PopupSeverity.Critical) => PopupPriority.Critical,
                (_, PopupSeverity.High) => PopupPriority.High,
                (PopupType.Warning, _) => PopupPriority.Medium,
                _ => PopupPriority.Normal;
            };
        }

        private void StartTimeoutTimer(PopupNotification popup)
        {
            var timer = new Timer(
                async _ => await HandlePopupTimeoutAsync(popup.Id),
                null,
                popup.Timeout,
                Timeout.InfiniteTimeSpan);

            popup.TimeoutTimer = timer;
        }

        private async Task HandlePopupTimeoutAsync(string popupId)
        {
            if (_activePopups.ContainsKey(popupId))
            {
                await ClosePopupAsync(popupId, ClosePopupReason.Timeout);
            }
        }

        private void InitializeTimers()
        {
            // Temizlik timer'ı (eski popup'ları temizler)
            _cleanupTimer = new Timer(
                async _ => await CleanupOldPopupsAsync(),
                null,
                TimeSpan.FromMinutes(_configuration.CleanupIntervalMinutes),
                TimeSpan.FromMinutes(_configuration.CleanupIntervalMinutes));

            // Gösterim timer'ı (kuyruktaki popup'ları gösterir)
            _displayTimer = new Timer(
                async _ => await ProcessQueueAsync(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));

            // Animasyon timer'ı (popup animasyonlarını yönetir)
            _animationTimer = new Timer(
                async _ => await ProcessAnimationsAsync(),
                null,
                TimeSpan.FromMilliseconds(16), // ~60 FPS;
                TimeSpan.FromMilliseconds(16));
        }

        private void StopTimers()
        {
            _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _displayTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _animationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private async Task ProcessQueueAsync()
        {
            await ShowNextPopupAsync();
        }

        private async Task ProcessAnimationsAsync()
        {
            await _displayManager?.ProcessAnimationsAsync();
        }

        private async Task CleanupOldPopupsAsync()
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddHours(-_configuration.HistoryRetentionHours);
                var oldPopups = _activePopups.Values;
                    .Where(p => p.CreatedAt < cutoffTime)
                    .ToList();

                foreach (var popup in oldPopups)
                {
                    await ClosePopupAsync(popup.Id, ClosePopupReason.Cleanup);
                }

                _logger.LogDebug($"Eski popup temizliği tamamlandı: {oldPopups.Count} popup kapatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Popup temizleme hatası: {ex.Message}", ex);
            }
        }

        private string GeneratePopupId()
        {
            return $"popup_{Guid.NewGuid():N}";
        }

        private void UpdateStatisticsForDisplay(PopupNotification popup)
        {
            lock (_statistics)
            {
                _statistics.TotalShown++;

                if (!_statistics.ByType.ContainsKey(popup.Type))
                    _statistics.ByType[popup.Type] = 0;
                _statistics.ByType[popup.Type]++;

                if (!_statistics.ByPriority.ContainsKey(popup.Priority))
                    _statistics.ByPriority[popup.Priority] = 0;
                _statistics.ByPriority[popup.Priority]++;

                if (!_statistics.ByCategory.ContainsKey(popup.Category))
                    _statistics.ByCategory[popup.Category] = 0;
                _statistics.ByCategory[popup.Category]++;

                _statistics.CurrentActive = _activePopups.Count;
                _statistics.MaxConcurrent = Math.Max(_statistics.MaxConcurrent, _activePopups.Count);
            }
        }

        private void UpdateStatisticsForClose(PopupNotification popup, ClosePopupReason reason)
        {
            lock (_statistics)
            {
                _statistics.TotalClosed++;

                if (reason == ClosePopupReason.Timeout)
                    _statistics.TotalTimedOut++;
                else if (reason == ClosePopupReason.UserAction)
                    _statistics.TotalClicked++;

                if (popup.DisplayedAt.HasValue)
                {
                    var displayTime = DateTime.UtcNow - popup.DisplayedAt.Value;
                    _statistics.AverageDisplayTime = (_statistics.AverageDisplayTime * (_statistics.TotalClosed - 1) + displayTime.TotalMilliseconds) / _statistics.TotalClosed;
                }
            }
        }

        private void UpdateStatisticsForQueue(PopupNotification popup)
        {
            lock (_statistics)
            {
                _statistics.CurrentQueued = _notificationQueue.Count;
            }
        }

        private void ResetStatistics()
        {
            lock (_statistics)
            {
                _statistics = new PopupStatistics;
                {
                    StartTime = DateTime.UtcNow,
                    TotalShown = 0,
                    TotalClosed = 0,
                    TotalTimedOut = 0,
                    TotalClicked = 0,
                    AverageDisplayTime = 0,
                    MaxConcurrent = 0,
                    CurrentActive = 0,
                    CurrentQueued = 0,
                    ByType = new Dictionary<PopupType, int>(),
                    ByPriority = new Dictionary<PopupPriority, int>(),
                    ByCategory = new Dictionary<string, int>()
                };
            }
        }

        private void ApplyPopupUpdate(PopupNotification popup, PopupUpdate update)
        {
            if (update.Title != null)
                popup.Title = update.Title;

            if (update.Message != null)
                popup.Message = update.Message;

            if (update.Progress.HasValue)
                popup.Progress = update.Progress.Value;

            if (update.ProgressText != null)
                popup.ProgressText = update.ProgressText;

            if (update.Buttons != null)
                popup.Buttons = update.Buttons;

            popup.LastUpdated = DateTime.UtcNow;
        }

        private async Task ReleaseResourcesAsync()
        {
            // Timer'ları dispose et;
            _cleanupTimer?.Dispose();
            _displayTimer?.Dispose();
            _animationTimer?.Dispose();

            // Subject'leri tamamla ve dispose et;
            _popupEvents.OnCompleted();
            _popupEvents.Dispose();
            _notificationEvents.OnCompleted();
            _notificationEvents.Dispose();

            // Koleksiyonları temizle;
            _activePopups.Clear();
            _notificationQueue.Clear();
            _priorityQueues.Clear();
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
                    StopTimers();
                    ReleaseResourcesAsync().Wait();
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

    #region Supporting Types;

    /// <summary>
    /// Popup bildirimi;
    /// </summary>
    public class PopupNotification;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public PopupType Type { get; set; }
        public PopupSeverity Severity { get; set; }
        public PopupPriority Priority { get; set; }
        public string Category { get; set; }
        public PopupState State { get; set; }
        public List<PopupButton> Buttons { get; set; } = new List<PopupButton>();
        public TimeSpan Timeout { get; set; }
        public double? Progress { get; set; }
        public string ProgressText { get; set; }
        public object CustomData { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime? DisplayedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public ClosePopupReason? CloseReason { get; set; }
        public object ResultData { get; set; }
        public Timer TimeoutTimer { get; set; }
        public PopupPosition Position { get; set; }
        public string StyleName { get; set; }
    }

    /// <summary>
    /// Popup tipi;
    /// </summary>
    public enum PopupType;
    {
        Information,
        Warning,
        Error,
        Success,
        Confirmation,
        Input,
        Progress,
        Custom;
    }

    /// <summary>
    /// Popup önem seviyesi;
    /// </summary>
    public enum PopupSeverity;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Popup önceliği;
    /// </summary>
    public enum PopupPriority;
    {
        Low = 0,
        Normal = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    /// <summary>
    /// Popup durumu;
    /// </summary>
    public enum PopupState;
    {
        Pending,
        Queued,
        Displaying,
        Closed,
        Cancelled,
        TimedOut;
    }

    /// <summary>
    /// Popup kapatma nedeni;
    /// </summary>
    public enum ClosePopupReason;
    {
        UserAction,
        ButtonClick,
        Timeout,
        SystemAction,
        SystemShutdown,
        Cleanup;
    }

    /// <summary>
    /// Popup pozisyonu;
    /// </summary>
    public enum PopupPosition;
    {
        TopLeft,
        TopCenter,
        TopRight,
        MiddleLeft,
        MiddleCenter,
        MiddleRight,
        BottomLeft,
        BottomCenter,
        BottomRight,
        Custom;
    }

    /// <summary>
    /// Popup butonu;
    /// </summary>
    public class PopupButton;
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public ButtonType Type { get; set; }
        public object Data { get; set; }
        public bool IsDefault { get; set; }
        public bool IsCancel { get; set; }
        public ButtonAction Action { get; set; }
    }

    /// <summary>
    /// Buton tipi;
    /// </summary>
    public enum ButtonType;
    {
        Primary,
        Secondary,
        Success,
        Danger,
        Warning,
        Info,
        Link;
    }

    /// <summary>
    /// Buton aksiyonu;
    /// </summary>
    public enum ButtonAction;
    {
        Close,
        Confirm,
        Cancel,
        Custom,
        Navigate,
        Execute;
    }

    /// <summary>
    /// Popup sistem yapılandırması;
    /// </summary>
    public class PopupSystemConfiguration;
    {
        public int MaxConcurrentPopups { get; set; } = 3;
        public int CleanupIntervalMinutes { get; set; } = 60;
        public int HistoryRetentionHours { get; set; } = 24;
        public bool EnableSound { get; set; } = true;
        public bool EnableAnimation { get; set; } = true;
        public bool EnableDrag { get; set; } = true;
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public PopupPosition DefaultPosition { get; set; } = PopupPosition.BottomRight;
        public Dictionary<PopupType, PopupStyle> Styles { get; set; } = new Dictionary<PopupType, PopupStyle>();
    }

    /// <summary>
    /// Popup istatistikleri;
    /// </summary>
    public class PopupStatistics;
    {
        public DateTime StartTime { get; set; }
        public int TotalShown { get; set; }
        public int TotalClosed { get; set; }
        public int TotalTimedOut { get; set; }
        public int TotalClicked { get; set; }
        public double AverageDisplayTime { get; set; } // milisaniye;
        public int MaxConcurrent { get; set; }
        public int CurrentActive { get; set; }
        public int CurrentQueued { get; set; }
        public Dictionary<PopupType, int> ByType { get; set; } = new Dictionary<PopupType, int>();
        public Dictionary<PopupPriority, int> ByPriority { get; set; } = new Dictionary<PopupPriority, int>();
        public Dictionary<string, int> ByCategory { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Popup olayı;
    /// </summary>
    public class PopupEvent;
    {
        public PopupEventType EventType { get; set; }
        public string PopupId { get; set; }
        public PopupNotification Popup { get; set; }
        public DateTime Timestamp { get; set; }
        public ClosePopupReason? Reason { get; set; }
        public object ResultData { get; set; }
        public object UpdateData { get; set; }
        public object AdditionalData { get; set; }
    }

    /// <summary>
    /// Popup olay tipi;
    /// </summary>
    public enum PopupEventType;
    {
        Created,
        Queued,
        Displaying,
        Updated,
        Closed,
        Cancelled,
        ButtonClicked,
        TimedOut;
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// Popup sistemi arayüzü;
    /// </summary>
    public interface IPopupSystem : IDisposable
    {
        Task InitializeAsync(PopupSystemConfiguration configuration = null);
        Task<PopupResult> ShowPopupAsync(PopupNotification notification, ShowPopupOptions options = null);
        Task<PopupResult> ShowCustomPopupAsync(CustomPopupRequest request, ShowPopupOptions options = null);
        Task<ConfirmationResult> ShowConfirmationAsync(ConfirmationRequest request, ShowPopupOptions options = null);
        Task<InputResult> ShowInputPopupAsync(InputRequest request, ShowPopupOptions options = null);
        Task<ProgressPopupResult> ShowProgressPopupAsync(ProgressRequest request, ShowPopupOptions options = null);
        Task<PopupResult> ClosePopupAsync(string popupId, ClosePopupReason reason = ClosePopupReason.UserAction, object resultData = null);
        Task<BatchPopupResult> CloseAllPopupsAsync(ClosePopupReason reason = ClosePopupReason.SystemAction, string category = null);
        Task<PopupResult> UpdatePopupAsync(string popupId, PopupUpdate update);
        PopupStatistics GetStatistics();
        IEnumerable<PopupNotification> GetPopupHistory(DateTime? startDate = null, DateTime? endDate = null, string category = null, PopupType? type = null);
        Task ShutdownAsync();
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Popup sistemi istisnası;
    /// </summary>
    public class PopupSystemException : Exception
    {
        public PopupSystemException() { }
        public PopupSystemException(string message) : base(message) { }
        public PopupSystemException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Popup gösterim istisnası;
    /// </summary>
    public class PopupShowException : PopupSystemException;
    {
        public PopupShowException() { }
        public PopupShowException(string message) : base(message) { }
        public PopupShowException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Popup kapatma istisnası;
    /// </summary>
    public class PopupCloseException : PopupSystemException;
    {
        public PopupCloseException() { }
        public PopupCloseException(string message) : base(message) { }
        public PopupCloseException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
