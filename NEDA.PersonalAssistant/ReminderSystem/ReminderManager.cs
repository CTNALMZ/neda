using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Services.EventBus;
using NEDA.Services.NotificationService;
using NEDA.Monitoring.MetricsCollector;
using NEDA.PersonalAssistant.DailyPlanner;
using NEDA.Interface.ResponseGenerator;

namespace NEDA.PersonalAssistant.ReminderSystem;
{
    /// <summary>
    /// Hatırlatıcı yönetimi arayüzü.
    /// </summary>
    public interface IReminderManager : IDisposable
    {
        /// <summary>
        /// Yeni bir hatırlatıcı oluşturur.
        /// </summary>
        /// <param name="reminder">Hatırlatıcı bilgileri.</param>
        /// <returns>Oluşturma sonucu.</returns>
        Task<ReminderCreationResult> CreateReminderAsync(Reminder reminder);

        /// <summary>
        /// Varolan bir hatırlatıcıyı günceller.
        /// </summary>
        /// <param name="reminderId">Hatırlatıcı ID.</param>
        /// <param name="updates">Güncelleme bilgileri.</param>
        /// <returns>Güncelleme sonucu.</returns>
        Task<UpdateResult> UpdateReminderAsync(Guid reminderId, ReminderUpdate updates);

        /// <summary>
        /// Hatırlatıcıyı iptal eder.
        /// </summary>
        /// <param name="reminderId">Hatırlatıcı ID.</param>
        /// <param name="reason">İptal nedeni.</param>
        /// <returns>İptal sonucu.</returns>
        Task<CancellationResult> CancelReminderAsync(Guid reminderId, string reason = null);

        /// <summary>
        /// Hatırlatıcıyı erteleme yapar.
        /// </summary>
        /// <param name="reminderId">Hatırlatıcı ID.</param>
        /// <param name="snoozeDuration">Erteleme süresi.</param>
        /// <returns>Erteleme sonucu.</returns>
        Task<SnoozeResult> SnoozeReminderAsync(Guid reminderId, TimeSpan snoozeDuration);

        /// <summary>
        /// Hatırlatıcıyı tamamlanmış olarak işaretler.
        /// </summary>
        /// <param name="reminderId">Hatırlatıcı ID.</param>
        /// <param name="completionInfo">Tamamlama bilgileri.</param>
        /// <returns>Tamamlama sonucu.</returns>
        Task<CompletionResult> CompleteReminderAsync(Guid reminderId, CompletionInfo completionInfo = null);

        /// <summary>
        /// Kullanıcının aktif hatırlatıcılarını getirir.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        /// <returns>Aktif hatırlatıcılar listesi.</returns>
        IReadOnlyList<Reminder> GetActiveReminders(Guid userId);

        /// <summary>
        /// Yaklaşan hatırlatıcıları getirir.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        /// <param name="timeWindow">Zaman penceresi.</param>
        /// <returns>Yaklaşan hatırlatıcılar.</returns>
        IReadOnlyList<Reminder> GetUpcomingReminders(Guid userId, TimeSpan timeWindow);

        /// <summary>
        /// Günlük hatırlatıcı özetini getirir.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        /// <param name="date">Tarih.</param>
        /// <returns>Günlük özet.</returns>
        Task<DailyReminderSummary> GetDailySummaryAsync(Guid userId, DateTime date);

        /// <summary>
        /// Tekrarlayan hatırlatıcılar için sonraki oluşumu hesaplar.
        /// </summary>
        /// <param name="reminder">Hatırlatıcı.</param>
        /// <returns>Sonraki oluşum zamanı.</returns>
        DateTime? CalculateNextOccurrence(Reminder reminder);

        /// <summary>
        /// Hatırlatıcı önceliklerini yeniden hesaplar.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        Task RecalculatePrioritiesAsync(Guid userId);

        /// <summary>
        /// Hatırlatıcı istatistiklerini getirir.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        /// <param name="timeRange">Zaman aralığı.</param>
        /// <returns>İstatistikler.</returns>
        Task<ReminderStatistics> GetStatisticsAsync(Guid userId, TimeRange timeRange);

        /// <summary>
        /// Hatırlatıcı motorunu başlatır.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Hatırlatıcı motorunu durdurur.
        /// </summary>
        Task StopAsync();
    }

    /// <summary>
    /// Hatırlatıcı yöneticisi ana sınıfı.
    /// </summary>
    public class ReminderManager : IReminderManager;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly INotificationManager _notificationManager;
        private readonly ITaskScheduler _taskScheduler;
        private readonly IResponseGenerator _responseGenerator;

        private readonly ReminderStore _reminderStore;
        private readonly ReminderEngine _reminderEngine;
        private readonly PriorityCalculator _priorityCalculator;
        private readonly SnoozeManager _snoozeManager;
        private readonly RecurrenceCalculator _recurrenceCalculator;
        private readonly SmartAlertManager _smartAlertManager;

        private readonly ConcurrentDictionary<Guid, ReminderTimer> _reminderTimers;
        private readonly ConcurrentDictionary<Guid, Reminder> _activeReminders;
        private readonly CancellationTokenSource _shutdownTokenSource;
        private readonly SemaphoreSlim _processingLock;
        private readonly Timer _maintenanceTimer;
        private readonly Timer _alertTimer;

        private bool _disposed;
        private bool _isRunning;
        private DateTime _startTime;

        /// <summary>
        /// Hatırlatıcı yöneticisi oluşturur.
        /// </summary>
        public ReminderManager(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IEventBus eventBus,
            INotificationManager notificationManager,
            ITaskScheduler taskScheduler,
            IResponseGenerator responseGenerator)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));
            _taskScheduler = taskScheduler ?? throw new ArgumentNullException(nameof(taskScheduler));
            _responseGenerator = responseGenerator ?? throw new ArgumentNullException(nameof(responseGenerator));

            _reminderStore = new ReminderStore();
            _reminderEngine = new ReminderEngine();
            _priorityCalculator = new PriorityCalculator();
            _snoozeManager = new SnoozeManager();
            _recurrenceCalculator = new RecurrenceCalculator();
            _smartAlertManager = new SmartAlertManager();

            _reminderTimers = new ConcurrentDictionary<Guid, ReminderTimer>();
            _activeReminders = new ConcurrentDictionary<Guid, Reminder>();
            _shutdownTokenSource = new CancellationTokenSource();
            _processingLock = new SemaphoreSlim(1, 1);

            // Bakım timer'ı: her saat başı;
            _maintenanceTimer = new Timer(PerformMaintenance, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            // Alarm timer'ı: her dakika kontrol;
            _alertTimer = new Timer(CheckDueReminders, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            _eventBus.Subscribe<ReminderTriggeredEvent>(HandleReminderTriggered);
            _eventBus.Subscribe<ReminderCompletedEvent>(HandleReminderCompleted);
            _eventBus.Subscribe<UserActivityEvent>(HandleUserActivity);

            _logger.LogInformation("ReminderManager initialized");
        }

        /// <summary>
        /// Yeni bir hatırlatıcı oluşturur.
        /// </summary>
        public async Task<ReminderCreationResult> CreateReminderAsync(Reminder reminder)
        {
            ValidateReminder(reminder);

            await _processingLock.WaitAsync();
            try
            {
                // ID atama;
                if (reminder.Id == Guid.Empty)
                {
                    reminder.Id = Guid.NewGuid();
                }

                // Öncelik hesaplama;
                reminder.Priority = await _priorityCalculator.CalculatePriorityAsync(reminder);

                // Başlangıç durumu;
                reminder.Status = ReminderStatus.Scheduled;
                reminder.CreatedAt = DateTime.UtcNow;
                reminder.UpdatedAt = DateTime.UtcNow;

                // Tekrarlayan hatırlatıcı için sonraki tetikleme zamanı;
                if (reminder.RecurrencePattern != null)
                {
                    reminder.NextOccurrence = CalculateNextOccurrence(reminder);
                    if (!reminder.NextOccurrence.HasValue)
                    {
                        return new ReminderCreationResult;
                        {
                            Success = false,
                            ReminderId = reminder.Id,
                            Message = "Invalid recurrence pattern"
                        };
                    }
                }
                else;
                {
                    reminder.NextOccurrence = reminder.TriggerTime;
                }

                // Hatırlatıcıyı saklama;
                _reminderStore.AddReminder(reminder);
                _activeReminders[reminder.Id] = reminder;

                // Zamanlayıcı oluştur;
                SetupReminderTimer(reminder);

                // Event yayınla;
                var @event = new ReminderCreatedEvent;
                {
                    ReminderId = reminder.Id,
                    UserId = reminder.UserId,
                    Title = reminder.Title,
                    TriggerTime = reminder.TriggerTime,
                    Priority = reminder.Priority,
                    ReminderType = reminder.Type,
                    IsRecurring = reminder.RecurrencePattern != null,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("reminders.created", 1);
                _logger.LogInformation($"Reminder created: {reminder.Id} for user {reminder.UserId}");

                // Akıllı önerileri kontrol et;
                await CheckForSmartSuggestionsAsync(reminder);

                return new ReminderCreationResult;
                {
                    Success = true,
                    ReminderId = reminder.Id,
                    TriggerTime = reminder.TriggerTime,
                    NextOccurrence = reminder.NextOccurrence,
                    Priority = reminder.Priority,
                    Message = "Reminder successfully created"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create reminder {reminder.Id}");
                _metricsCollector.RecordMetric("reminders.creation.error", 1);

                return new ReminderCreationResult;
                {
                    Success = false,
                    ReminderId = reminder.Id,
                    Message = $"Creation failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Varolan bir hatırlatıcıyı günceller.
        /// </summary>
        public async Task<UpdateResult> UpdateReminderAsync(Guid reminderId, ReminderUpdate updates)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeReminders.TryGetValue(reminderId, out var reminder))
                {
                    return new UpdateResult;
                    {
                        Success = false,
                        ReminderId = reminderId,
                        Message = "Reminder not found"
                    };
                }

                var originalReminder = reminder.Clone();

                // Güncellemeleri uygula;
                ApplyUpdates(reminder, updates);
                reminder.UpdatedAt = DateTime.UtcNow;

                // Öncelik yeniden hesapla;
                reminder.Priority = await _priorityCalculator.CalculatePriorityAsync(reminder);

                // Tetikleme zamanı güncellendi ise zamanlayıcıyı güncelle;
                if (updates.TriggerTime.HasValue || updates.RecurrencePattern != null)
                {
                    if (reminder.RecurrencePattern != null)
                    {
                        reminder.NextOccurrence = CalculateNextOccurrence(reminder);
                    }
                    else;
                    {
                        reminder.NextOccurrence = reminder.TriggerTime;
                    }

                    UpdateReminderTimer(reminderId, reminder);
                }

                // Hatırlatıcıyı güncelle;
                _reminderStore.UpdateReminder(reminder);
                _activeReminders[reminderId] = reminder;

                // Event yayınla;
                var @event = new ReminderUpdatedEvent;
                {
                    ReminderId = reminderId,
                    UserId = reminder.UserId,
                    OriginalReminder = originalReminder,
                    UpdatedReminder = reminder,
                    UpdateType = updates.UpdateType,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("reminders.updated", 1);
                _logger.LogInformation($"Reminder updated: {reminderId}");

                return new UpdateResult;
                {
                    Success = true,
                    ReminderId = reminderId,
                    Message = "Reminder successfully updated"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update reminder {reminderId}");
                _metricsCollector.RecordMetric("reminders.update.error", 1);

                return new UpdateResult;
                {
                    Success = false,
                    ReminderId = reminderId,
                    Message = $"Update failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Hatırlatıcıyı iptal eder.
        /// </summary>
        public async Task<CancellationResult> CancelReminderAsync(Guid reminderId, string reason = null)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeReminders.TryRemove(reminderId, out var reminder))
                {
                    return new CancellationResult;
                    {
                        Success = false,
                        ReminderId = reminderId,
                        Message = "Reminder not found"
                    };
                }

                // Zamanlayıcıyı temizle;
                if (_reminderTimers.TryRemove(reminderId, out var timer))
                {
                    timer.Dispose();
                }

                // Durumu güncelle;
                reminder.Status = ReminderStatus.Cancelled;
                reminder.UpdatedAt = DateTime.UtcNow;

                // Hatırlatıcıyı saklama güncelle;
                _reminderStore.UpdateReminder(reminder);

                // Event yayınla;
                var @event = new ReminderCancelledEvent;
                {
                    ReminderId = reminderId,
                    UserId = reminder.UserId,
                    Title = reminder.Title,
                    Reason = reason,
                    WasRecurring = reminder.RecurrencePattern != null,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("reminders.cancelled", 1);
                _logger.LogInformation($"Reminder cancelled: {reminderId}, reason: {reason ?? "No reason provided"}");

                return new CancellationResult;
                {
                    Success = true,
                    ReminderId = reminderId,
                    Message = "Reminder successfully cancelled"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to cancel reminder {reminderId}");
                _metricsCollector.RecordMetric("reminders.cancellation.error", 1);

                return new CancellationResult;
                {
                    Success = false,
                    ReminderId = reminderId,
                    Message = $"Cancellation failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Hatırlatıcıyı erteleme yapar.
        /// </summary>
        public async Task<SnoozeResult> SnoozeReminderAsync(Guid reminderId, TimeSpan snoozeDuration)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeReminders.TryGetValue(reminderId, out var reminder))
                {
                    return new SnoozeResult;
                    {
                        Success = false,
                        ReminderId = reminderId,
                        Message = "Reminder not found"
                    };
                }

                // Ertelenmiş hatırlatıcıyı işle;
                var snoozeResult = await _snoozeManager.SnoozeReminderAsync(reminder, snoozeDuration);

                if (!snoozeResult.Success)
                {
                    return new SnoozeResult;
                    {
                        Success = false,
                        ReminderId = reminderId,
                        Message = snoozeResult.Message;
                    };
                }

                // Zamanlayıcıyı güncelle;
                UpdateReminderTimer(reminderId, reminder);

                // Event yayınla;
                var @event = new ReminderSnoozedEvent;
                {
                    ReminderId = reminderId,
                    UserId = reminder.UserId,
                    OriginalTriggerTime = reminder.TriggerTime,
                    NewTriggerTime = reminder.NextOccurrence ?? reminder.TriggerTime,
                    SnoozeDuration = snoozeDuration,
                    SnoozeCount = reminder.SnoozeCount,
                    MaxSnoozeCount = reminder.MaxSnoozeCount,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("reminders.snoozed", 1);
                _logger.LogInformation($"Reminder snoozed: {reminderId} for {snoozeDuration}");

                return new SnoozeResult;
                {
                    Success = true,
                    ReminderId = reminderId,
                    NewTriggerTime = reminder.NextOccurrence ?? reminder.TriggerTime,
                    SnoozeCount = reminder.SnoozeCount,
                    Message = "Reminder successfully snoozed"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to snooze reminder {reminderId}");
                _metricsCollector.RecordMetric("reminders.snooze.error", 1);

                return new SnoozeResult;
                {
                    Success = false,
                    ReminderId = reminderId,
                    Message = $"Snooze failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Hatırlatıcıyı tamamlanmış olarak işaretler.
        /// </summary>
        public async Task<CompletionResult> CompleteReminderAsync(Guid reminderId, CompletionInfo completionInfo = null)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeReminders.TryGetValue(reminderId, out var reminder))
                {
                    return new CompletionResult;
                    {
                        Success = false,
                        ReminderId = reminderId,
                        Message = "Reminder not found"
                    };
                }

                // Tamamlama bilgilerini uygula;
                if (completionInfo != null)
                {
                    reminder.CompletionTime = completionInfo.CompletionTime ?? DateTime.UtcNow;
                    reminder.CompletionNotes = completionInfo.Notes;
                    reminder.CompletionStatus = completionInfo.Status;
                }
                else;
                {
                    reminder.CompletionTime = DateTime.UtcNow;
                    reminder.CompletionStatus = CompletionStatus.Completed;
                }

                reminder.Status = ReminderStatus.Completed;
                reminder.UpdatedAt = DateTime.UtcNow;

                // Zamanlayıcıyı temizle;
                if (_reminderTimers.TryRemove(reminderId, out var timer))
                {
                    timer.Dispose();
                }

                // Hatırlatıcıyı saklama güncelle;
                _reminderStore.UpdateReminder(reminder);

                // Tekrarlayan hatırlatıcı için sonraki oluşumu planla;
                if (reminder.RecurrencePattern != null)
                {
                    await ScheduleNextRecurrenceAsync(reminder);
                }
                else;
                {
                    _activeReminders.TryRemove(reminderId, out _);
                }

                // Event yayınla;
                var @event = new ReminderCompletedEvent;
                {
                    ReminderId = reminderId,
                    UserId = reminder.UserId,
                    Title = reminder.Title,
                    TriggerTime = reminder.TriggerTime,
                    CompletionTime = reminder.CompletionTime.Value,
                    CompletionStatus = reminder.CompletionStatus.Value,
                    IsRecurring = reminder.RecurrencePattern != null,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("reminders.completed", 1);
                _logger.LogInformation($"Reminder completed: {reminderId}");

                return new CompletionResult;
                {
                    Success = true,
                    ReminderId = reminderId,
                    CompletionTime = reminder.CompletionTime.Value,
                    IsRecurring = reminder.RecurrencePattern != null,
                    Message = "Reminder successfully completed"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to complete reminder {reminderId}");
                _metricsCollector.RecordMetric("reminders.completion.error", 1);

                return new CompletionResult;
                {
                    Success = false,
                    ReminderId = reminderId,
                    Message = $"Completion failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Kullanıcının aktif hatırlatıcılarını getirir.
        /// </summary>
        public IReadOnlyList<Reminder> GetActiveReminders(Guid userId)
        {
            return _activeReminders.Values;
                .Where(r => r.UserId == userId &&
                           (r.Status == ReminderStatus.Scheduled ||
                            r.Status == ReminderStatus.Snoozed))
                .OrderBy(r => r.NextOccurrence ?? r.TriggerTime)
                .ThenByDescending(r => r.Priority)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Yaklaşan hatırlatıcıları getirir.
        /// </summary>
        public IReadOnlyList<Reminder> GetUpcomingReminders(Guid userId, TimeSpan timeWindow)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.Add(timeWindow);

            return _activeReminders.Values;
                .Where(r => r.UserId == userId &&
                           r.Status == ReminderStatus.Scheduled &&
                           (r.NextOccurrence ?? r.TriggerTime) >= now &&
                           (r.NextOccurrence ?? r.TriggerTime) <= cutoff)
                .OrderBy(r => r.NextOccurrence ?? r.TriggerTime)
                .ThenByDescending(r => r.Priority)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Günlük hatırlatıcı özetini getirir.
        /// </summary>
        public async Task<DailyReminderSummary> GetDailySummaryAsync(Guid userId, DateTime date)
        {
            var startOfDay = date.Date;
            var endOfDay = startOfDay.AddDays(1).AddTicks(-1);

            var reminders = _reminderStore.GetRemindersInRange(userId, startOfDay, endOfDay);

            return new DailyReminderSummary;
            {
                Date = date,
                UserId = userId,
                TotalReminders = reminders.Count,
                CompletedCount = reminders.Count(r => r.Status == ReminderStatus.Completed),
                PendingCount = reminders.Count(r => r.Status == ReminderStatus.Scheduled),
                OverdueCount = reminders.Count(r => r.Status == ReminderStatus.Scheduled &&
                                                   (r.NextOccurrence ?? r.TriggerTime) < DateTime.UtcNow),
                SnoozedCount = reminders.Count(r => r.Status == ReminderStatus.Snoozed),
                HighPriorityCount = reminders.Count(r => r.Priority >= 0.8),
                RemindersByCategory = reminders;
                    .GroupBy(r => r.Category ?? "Uncategorized")
                    .ToDictionary(g => g.Key, g => g.Count()),
                UpcomingReminders = reminders;
                    .Where(r => r.Status == ReminderStatus.Scheduled)
                    .OrderBy(r => r.NextOccurrence ?? r.TriggerTime)
                    .Take(10)
                    .ToList()
            };
        }

        /// <summary>
        /// Tekrarlayan hatırlatıcılar için sonraki oluşumu hesaplar.
        /// </summary>
        public DateTime? CalculateNextOccurrence(Reminder reminder)
        {
            if (reminder.RecurrencePattern == null)
                return null;

            var baseTime = reminder.LastTriggered ?? reminder.TriggerTime;
            return _recurrenceCalculator.CalculateNextOccurrence(baseTime, reminder.RecurrencePattern);
        }

        /// <summary>
        /// Hatırlatıcı önceliklerini yeniden hesaplar.
        /// </summary>
        public async Task RecalculatePrioritiesAsync(Guid userId)
        {
            await _processingLock.WaitAsync();
            try
            {
                var userReminders = _activeReminders.Values;
                    .Where(r => r.UserId == userId && r.Status == ReminderStatus.Scheduled)
                    .ToList();

                foreach (var reminder in userReminders)
                {
                    var newPriority = await _priorityCalculator.CalculatePriorityAsync(reminder);
                    if (Math.Abs(reminder.Priority - newPriority) > 0.01)
                    {
                        reminder.Priority = newPriority;
                        _reminderStore.UpdateReminder(reminder);
                        _logger.LogDebug($"Reminder {reminder.Id} priority updated to {newPriority}");
                    }
                }

                _logger.LogInformation($"Priorities recalculated for user {userId}, {userReminders.Count} reminders updated");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Hatırlatıcı istatistiklerini getirir.
        /// </summary>
        public async Task<ReminderStatistics> GetStatisticsAsync(Guid userId, TimeRange timeRange)
        {
            var statistics = _reminderStore.GetStatistics(userId, timeRange.Start, timeRange.End);

            // Ek analizler;
            statistics.AverageCompletionTime = await CalculateAverageCompletionTimeAsync(userId, timeRange);
            statistics.SuccessRate = CalculateSuccessRate(statistics);
            statistics.Trends = await AnalyzeTrendsAsync(userId, timeRange);

            return statistics;
        }

        /// <summary>
        /// Hatırlatıcı motorunu başlatır.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning) return;

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                // Aktif hatırlatıcıları yükle;
                await LoadActiveRemindersAsync(cancellationToken);

                // Motoru başlat;
                _reminderEngine.Start();

                _isRunning = true;
                _startTime = DateTime.UtcNow;

                _logger.LogInformation("ReminderManager started");

                var @event = new ReminderManagerStartedEvent;
                {
                    StartTime = _startTime,
                    LoadedReminderCount = _activeReminders.Count,
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
        /// Hatırlatıcı motorunu durdurur.
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            await _processingLock.WaitAsync();
            try
            {
                // Tüm zamanlayıcıları durdur;
                foreach (var timer in _reminderTimers.Values)
                {
                    timer.Dispose();
                }
                _reminderTimers.Clear();

                // Motoru durdur;
                _reminderEngine.Stop();

                _isRunning = false;

                _logger.LogInformation("ReminderManager stopped");

                var @event = new ReminderManagerStoppedEvent;
                {
                    StopTime = DateTime.UtcNow,
                    Uptime = DateTime.UtcNow - _startTime,
                    ActiveReminderCount = _activeReminders.Count,
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
                    StopAsync().Wait(TimeSpan.FromSeconds(5));

                    _maintenanceTimer?.Dispose();
                    _alertTimer?.Dispose();
                    _processingLock?.Dispose();
                    _shutdownTokenSource?.Dispose();

                    foreach (var timer in _reminderTimers.Values)
                    {
                        timer?.Dispose();
                    }

                    _reminderTimers.Clear();

                    if (_eventBus != null)
                    {
                        _eventBus.Unsubscribe<ReminderTriggeredEvent>(HandleReminderTriggered);
                        _eventBus.Unsubscribe<ReminderCompletedEvent>(HandleReminderCompleted);
                        _eventBus.Unsubscribe<UserActivityEvent>(HandleUserActivity);
                    }

                    _reminderStore.Dispose();
                }

                _disposed = true;
            }
        }

        #region Private Methods;

        private void ValidateReminder(Reminder reminder)
        {
            if (reminder == null)
                throw new ArgumentNullException(nameof(reminder));

            if (reminder.UserId == Guid.Empty)
                throw new ArgumentException("UserId cannot be empty", nameof(reminder.UserId));

            if (string.IsNullOrWhiteSpace(reminder.Title))
                throw new ArgumentException("Title cannot be null or empty", nameof(reminder.Title));

            if (reminder.TriggerTime < DateTime.UtcNow && !reminder.AllowPastTrigger)
                throw new ArgumentException("TriggerTime cannot be in the past", nameof(reminder.TriggerTime));
        }

        private void SetupReminderTimer(Reminder reminder)
        {
            var triggerTime = reminder.NextOccurrence ?? reminder.TriggerTime;
            var now = DateTime.UtcNow;

            if (triggerTime <= now)
            {
                // Tetikleme zamanı geçmiş, hemen tetikle;
                Task.Run(() => TriggerReminderAsync(reminder.Id));
                return;
            }

            var delay = triggerTime - now;

            var timer = new Timer(
                _ => TriggerReminderAsync(reminder.Id).Wait(),
                null,
                delay,
                Timeout.InfiniteTimeSpan);

            _reminderTimers[reminder.Id] = new ReminderTimer(timer, triggerTime);
        }

        private void UpdateReminderTimer(Guid reminderId, Reminder reminder)
        {
            if (_reminderTimers.TryRemove(reminderId, out var oldTimer))
            {
                oldTimer.Timer.Dispose();
            }

            SetupReminderTimer(reminder);
        }

        private async Task TriggerReminderAsync(Guid reminderId)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeReminders.TryGetValue(reminderId, out var reminder))
                {
                    _logger.LogWarning($"Reminder {reminderId} not found for triggering");
                    return;
                }

                // Tetikleme zamanını güncelle;
                reminder.LastTriggered = DateTime.UtcNow;
                reminder.TriggerCount++;
                reminder.Status = ReminderStatus.Triggered;

                // Event yayınla;
                var triggeredEvent = new ReminderTriggeredEvent;
                {
                    ReminderId = reminderId,
                    UserId = reminder.UserId,
                    Title = reminder.Title,
                    TriggerTime = reminder.LastTriggered.Value,
                    Priority = reminder.Priority,
                    ReminderType = reminder.Type,
                    IsRecurring = reminder.RecurrencePattern != null,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(triggeredEvent);

                // Bildirim gönder;
                await SendReminderNotificationAsync(reminder);

                _metricsCollector.RecordMetric("reminders.triggered", 1);
                _logger.LogInformation($"Reminder triggered: {reminderId} for user {reminder.UserId}");

                // Tekrarlayan hatırlatıcı için sonraki tetiklemeyi planla;
                if (reminder.RecurrencePattern != null)
                {
                    var nextOccurrence = CalculateNextOccurrence(reminder);
                    if (nextOccurrence.HasValue)
                    {
                        reminder.NextOccurrence = nextOccurrence;
                        reminder.Status = ReminderStatus.Scheduled;
                        SetupReminderTimer(reminder);
                        _logger.LogInformation($"Next occurrence scheduled for reminder {reminderId} at {nextOccurrence.Value}");
                    }
                    else;
                    {
                        reminder.Status = ReminderStatus.Completed;
                        _activeReminders.TryRemove(reminderId, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error triggering reminder {reminderId}");
                _metricsCollector.RecordMetric("reminders.trigger.error", 1);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        private async Task SendReminderNotificationAsync(Reminder reminder)
        {
            try
            {
                // Akıllı bildirim stratejisi;
                var strategy = await _smartAlertManager.DetermineNotificationStrategyAsync(reminder);

                // Bildirim oluştur;
                var notification = new Notification;
                {
                    Id = Guid.NewGuid(),
                    UserId = reminder.UserId,
                    Title = reminder.Title,
                    Message = reminder.Description ?? $"Reminder: {reminder.Title}",
                    Type = NotificationType.Reminder,
                    Priority = (NotificationPriority)((int)(reminder.Priority * 4)), // 0-1 -> 0-4;
                    Data = new Dictionary<string, object>
                    {
                        ["reminderId"] = reminder.Id,
                        ["reminderType"] = reminder.Type.ToString(),
                        ["category"] = reminder.Category,
                        ["priority"] = reminder.Priority;
                    },
                    ExpiresAt = DateTime.UtcNow.Add(reminder.ExpirationDuration),
                    CreatedAt = DateTime.UtcNow;
                };

                // Bildirim gönder;
                await _notificationManager.SendNotificationAsync(notification, strategy);

                _logger.LogDebug($"Notification sent for reminder {reminder.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send notification for reminder {reminder.Id}");
            }
        }

        private async Task ScheduleNextRecurrenceAsync(Reminder originalReminder)
        {
            var nextOccurrence = CalculateNextOccurrence(originalReminder);
            if (!nextOccurrence.HasValue)
            {
                _logger.LogInformation($"No more occurrences for recurring reminder {originalReminder.Id}");
                return;
            }

            var nextReminder = originalReminder.Clone();
            nextReminder.Id = Guid.NewGuid();
            nextReminder.TriggerTime = nextOccurrence.Value;
            nextReminder.NextOccurrence = nextOccurrence;
            nextReminder.Status = ReminderStatus.Scheduled;
            nextReminder.TriggerCount = 0;
            nextReminder.LastTriggered = null;
            nextReminder.SnoozeCount = 0;
            nextReminder.CompletionTime = null;
            nextReminder.CompletionStatus = null;
            nextReminder.CompletionNotes = null;

            await CreateReminderAsync(nextReminder);
            _logger.LogInformation($"Next occurrence scheduled for recurring reminder {originalReminder.Id} at {nextOccurrence.Value}");
        }

        private async Task CheckDueReminders(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var dueReminders = _activeReminders.Values;
                    .Where(r => r.Status == ReminderStatus.Scheduled &&
                               (r.NextOccurrence ?? r.TriggerTime) <= now)
                    .ToList();

                foreach (var reminder in dueReminders)
                {
                    await TriggerReminderAsync(reminder.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking due reminders");
            }
        }

        private async Task LoadActiveRemindersAsync(CancellationToken cancellationToken)
        {
            var activeReminders = _reminderStore.GetActiveReminders();

            foreach (var reminder in activeReminders)
            {
                _activeReminders[reminder.Id] = reminder;
                SetupReminderTimer(reminder);
            }

            _logger.LogInformation($"Loaded {activeReminders.Count} active reminders");
            await Task.CompletedTask;
        }

        private void PerformMaintenance(object state)
        {
            try
            {
                // Süresi dolmuş hatırlatıcıları temizle;
                var cutoffTime = DateTime.UtcNow.AddDays(-30);
                var expiredReminders = _activeReminders.Values;
                    .Where(r => r.Status == ReminderStatus.Completed ||
                               r.Status == ReminderStatus.Cancelled)
                    .Where(r => r.UpdatedAt < cutoffTime)
                    .ToList();

                foreach (var reminder in expiredReminders)
                {
                    _activeReminders.TryRemove(reminder.Id, out _);
                    _reminderStore.ArchiveReminder(reminder.Id);
                }

                if (expiredReminders.Any())
                {
                    _logger.LogInformation($"Cleaned up {expiredReminders.Count} expired reminders");
                }

                // Metrikleri güncelle;
                UpdateMetrics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during maintenance");
            }
        }

        private void UpdateMetrics()
        {
            var activeCount = _activeReminders.Count;
            var scheduledCount = _activeReminders.Values.Count(r => r.Status == ReminderStatus.Scheduled);
            var triggeredCount = _activeReminders.Values.Count(r => r.Status == ReminderStatus.Triggered);

            _metricsCollector.RecordMetric("reminders.active", activeCount);
            _metricsCollector.RecordMetric("reminders.scheduled", scheduledCount);
            _metricsCollector.RecordMetric("reminders.triggered", triggeredCount);
        }

        private void ApplyUpdates(Reminder reminder, ReminderUpdate updates)
        {
            if (updates.Title != null) reminder.Title = updates.Title;
            if (updates.Description != null) reminder.Description = updates.Description;
            if (updates.TriggerTime.HasValue) reminder.TriggerTime = updates.TriggerTime.Value;
            if (updates.RecurrencePattern != null) reminder.RecurrencePattern = updates.RecurrencePattern;
            if (updates.Category != null) reminder.Category = updates.Category;
            if (updates.Tags != null) reminder.Tags = updates.Tags;
            if (updates.Priority.HasValue) reminder.Priority = updates.Priority.Value;
            if (updates.NotificationSettings != null) reminder.NotificationSettings = updates.NotificationSettings;
            if (updates.MaxSnoozeCount.HasValue) reminder.MaxSnoozeCount = updates.MaxSnoozeCount.Value;
            if (updates.ExpirationDuration.HasValue) reminder.ExpirationDuration = updates.ExpirationDuration.Value;
            if (updates.Metadata != null) reminder.Metadata = updates.Metadata;
        }

        private async Task CheckForSmartSuggestionsAsync(Reminder reminder)
        {
            // Akıllı öneriler kontrolü;
            var suggestions = await _smartAlertManager.GetSuggestionsAsync(reminder);

            if (suggestions.Any())
            {
                var @event = new SmartSuggestionsEvent;
                {
                    ReminderId = reminder.Id,
                    UserId = reminder.UserId,
                    Suggestions = suggestions,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _logger.LogDebug($"Found {suggestions.Count} smart suggestions for reminder {reminder.Id}");
            }
        }

        private async Task<TimeSpan> CalculateAverageCompletionTimeAsync(Guid userId, TimeRange timeRange)
        {
            var completedReminders = _reminderStore.GetCompletedReminders(userId, timeRange.Start, timeRange.End);

            if (!completedReminders.Any()) return TimeSpan.Zero;

            var totalTicks = completedReminders;
                .Where(r => r.CompletionTime.HasValue && r.TriggerTime <= r.CompletionTime)
                .Select(r => (r.CompletionTime.Value - r.TriggerTime).Ticks)
                .Average();

            return TimeSpan.FromTicks((long)totalTicks);
        }

        private double CalculateSuccessRate(ReminderStatistics statistics)
        {
            var total = statistics.CompletedCount + statistics.CancelledCount + statistics.ExpiredCount;
            if (total == 0) return 1.0;

            return (double)statistics.CompletedCount / total;
        }

        private async Task<List<ReminderTrend>> AnalyzeTrendsAsync(Guid userId, TimeRange timeRange)
        {
            // Trend analizi;
            var trends = new List<ReminderTrend>();

            // Tamamlama trendi;
            var dailyStats = _reminderStore.GetDailyStatistics(userId, timeRange.Start, timeRange.End);
            if (dailyStats.Count >= 7)
            {
                trends.Add(new ReminderTrend;
                {
                    Type = TrendType.CompletionRate,
                    Value = dailyStats.Average(s => s.CompletionRate),
                    Change = CalculateTrendChange(dailyStats.Select(s => s.CompletionRate).ToList()),
                    Confidence = 0.8;
                });
            }

            return trends;
        }

        private double CalculateTrendChange(List<double> values)
        {
            if (values.Count < 2) return 0;

            var first = values.First();
            var last = values.Last();

            if (Math.Abs(first) < 0.001) return last > 0 ? 100 : 0;

            return ((last - first) / first) * 100;
        }

        private void HandleReminderTriggered(ReminderTriggeredEvent @event)
        {
            _logger.LogDebug($"Reminder {@event.ReminderId} triggered event handled");
        }

        private void HandleReminderCompleted(ReminderCompletedEvent @event)
        {
            _logger.LogDebug($"Reminder {@event.ReminderId} completed event handled");
        }

        private void HandleUserActivity(UserActivityEvent @event)
        {
            // Kullanıcı aktivitesine göre hatırlatıcı ayarlamaları;
            if (@event.ActivityType == ActivityType.WorkStart)
            {
                // İş başlangıcında günlük hatırlatıcıları kontrol et;
                Task.Run(async () =>
                {
                    var dailySummary = await GetDailySummaryAsync(@event.UserId, DateTime.UtcNow.Date);
                    if (dailySummary.HighPriorityCount > 0)
                    {
                        _logger.LogInformation($"User {@event.UserId} has {dailySummary.HighPriorityCount} high priority reminders today");
                    }
                });
            }
        }

        #endregion;

        #region Internal Supporting Classes;

        /// <summary>
        /// Hatırlatıcı saklama motoru.
        /// </summary>
        internal class ReminderStore : IDisposable
        {
            private readonly ConcurrentDictionary<Guid, Reminder> _reminders;
            private readonly ConcurrentDictionary<Guid, List<Guid>> _userReminders;
            private readonly System.Data.Common.DbConnection _databaseConnection;

            public ReminderStore()
            {
                _reminders = new ConcurrentDictionary<Guid, Reminder>();
                _userReminders = new ConcurrentDictionary<Guid, List<Guid>>();

                // Veritabanı bağlantısı;
                var connectionString = "Data Source=reminders.db";
                _databaseConnection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                _databaseConnection.Open();
                InitializeDatabase();
            }

            public void AddReminder(Reminder reminder)
            {
                _reminders[reminder.Id] = reminder;

                var userReminderList = _userReminders.GetOrAdd(reminder.UserId, _ => new List<Guid>());
                lock (userReminderList)
                {
                    if (!userReminderList.Contains(reminder.Id))
                    {
                        userReminderList.Add(reminder.Id);
                    }
                }

                SaveToDatabase(reminder);
            }

            public void UpdateReminder(Reminder reminder)
            {
                _reminders[reminder.Id] = reminder;
                UpdateInDatabase(reminder);
            }

            public Reminder GetReminder(Guid reminderId)
            {
                return _reminders.TryGetValue(reminderId, out var reminder) ? reminder : null;
            }

            public List<Reminder> GetActiveReminders()
            {
                return _reminders.Values;
                    .Where(r => r.Status == ReminderStatus.Scheduled ||
                               r.Status == ReminderStatus.Snoozed)
                    .ToList();
            }

            public List<Reminder> GetRemindersInRange(Guid userId, DateTime start, DateTime end)
            {
                if (!_userReminders.TryGetValue(userId, out var reminderIds))
                    return new List<Reminder>();

                return reminderIds;
                    .Select(id => GetReminder(id))
                    .Where(r => r != null &&
                               (r.TriggerTime >= start && r.TriggerTime <= end) ||
                               (r.NextOccurrence.HasValue && r.NextOccurrence >= start && r.NextOccurrence <= end))
                    .ToList();
            }

            public List<Reminder> GetCompletedReminders(Guid userId, DateTime start, DateTime end)
            {
                if (!_userReminders.TryGetValue(userId, out var reminderIds))
                    return new List<Reminder>();

                return reminderIds;
                    .Select(id => GetReminder(id))
                    .Where(r => r != null &&
                               r.Status == ReminderStatus.Completed &&
                               r.CompletionTime.HasValue &&
                               r.CompletionTime >= start &&
                               r.CompletionTime <= end)
                    .ToList();
            }

            public ReminderStatistics GetStatistics(Guid userId, DateTime start, DateTime end)
            {
                var reminders = GetRemindersInRange(userId, start, end);

                return new ReminderStatistics;
                {
                    UserId = userId,
                    TimeRange = new TimeRange(start, end),
                    TotalCount = reminders.Count,
                    CompletedCount = reminders.Count(r => r.Status == ReminderStatus.Completed),
                    CancelledCount = reminders.Count(r => r.Status == ReminderStatus.Cancelled),
                    ExpiredCount = reminders.Count(r => r.Status == ReminderStatus.Expired),
                    SnoozedCount = reminders.Count(r => r.Status == ReminderStatus.Snoozed),
                    AveragePriority = reminders.Any() ? reminders.Average(r => r.Priority) : 0,
                    Categories = reminders;
                        .GroupBy(r => r.Category ?? "Uncategorized")
                        .ToDictionary(g => g.Key, g => g.Count()),
                    Types = reminders;
                        .GroupBy(r => r.Type)
                        .ToDictionary(g => g.Key, g => g.Count())
                };
            }

            public List<DailyStatistics> GetDailyStatistics(Guid userId, DateTime start, DateTime end)
            {
                var days = Enumerable.Range(0, (end - start).Days + 1)
                    .Select(offset => start.AddDays(offset))
                    .ToList();

                var statistics = new List<DailyStatistics>();

                foreach (var day in days)
                {
                    var dayReminders = GetRemindersInRange(userId, day.Date, day.Date.AddDays(1).AddTicks(-1));

                    statistics.Add(new DailyStatistics;
                    {
                        Date = day,
                        TotalCount = dayReminders.Count,
                        CompletedCount = dayReminders.Count(r => r.Status == ReminderStatus.Completed),
                        CompletionRate = dayReminders.Count > 0 ?
                            (double)dayReminders.Count(r => r.Status == ReminderStatus.Completed) / dayReminders.Count : 0;
                    });
                }

                return statistics;
            }

            public void ArchiveReminder(Guid reminderId)
            {
                if (_reminders.TryRemove(reminderId, out var reminder))
                {
                    // Kullanıcı listesinden kaldır;
                    if (_userReminders.TryGetValue(reminder.UserId, out var userReminderList))
                    {
                        lock (userReminderList)
                        {
                            userReminderList.Remove(reminderId);
                        }
                    }

                    ArchiveToDatabase(reminder);
                }
            }

            private void InitializeDatabase()
            {
                using var command = _databaseConnection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Reminders (
                        Id TEXT PRIMARY KEY,
                        UserId TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Description TEXT,
                        TriggerTime DATETIME NOT NULL,
                        NextOccurrence DATETIME,
                        Priority REAL NOT NULL,
                        Status INTEGER NOT NULL,
                        Type INTEGER NOT NULL,
                        Category TEXT,
                        Tags TEXT,
                        IsRecurring INTEGER NOT NULL,
                        RecurrencePattern TEXT,
                        NotificationSettings TEXT,
                        MaxSnoozeCount INTEGER NOT NULL,
                        SnoozeCount INTEGER NOT NULL,
                        ExpirationDuration TEXT NOT NULL,
                        Metadata TEXT,
                        CreatedAt DATETIME NOT NULL,
                        UpdatedAt DATETIME NOT NULL,
                        LastTriggered DATETIME,
                        TriggerCount INTEGER NOT NULL,
                        CompletionTime DATETIME,
                        CompletionStatus INTEGER,
                        CompletionNotes TEXT;
                    );
                    
                    CREATE INDEX IF NOT EXISTS idx_reminders_user ON Reminders(UserId);
                    CREATE INDEX IF NOT EXISTS idx_reminders_status ON Reminders(Status);
                    CREATE INDEX IF NOT EXISTS idx_reminders_trigger ON Reminders(TriggerTime);
                    CREATE INDEX IF NOT EXISTS idx_reminders_priority ON Reminders(Priority);
                ";
                command.ExecuteNonQuery();
            }

            private void SaveToDatabase(Reminder reminder)
            {
                // Veritabanı kaydetme işlemi;
            }

            private void UpdateInDatabase(Reminder reminder)
            {
                // Veritabanı güncelleme işlemi;
            }

            private void ArchiveToDatabase(Reminder reminder)
            {
                // Arşivleme işlemi;
            }

            public void Dispose()
            {
                _databaseConnection?.Close();
                _databaseConnection?.Dispose();
            }
        }

        /// <summary>
        /// Hatırlatıcı motoru.
        /// </summary>
        internal class ReminderEngine;
        {
            public void Start()
            {
                // Motor başlatma;
            }

            public void Stop()
            {
                // Motor durdurma;
            }
        }

        /// <summary>
        /// Öncelik hesaplayıcı.
        /// </summary>
        internal class PriorityCalculator;
        {
            public async Task<double> CalculatePriorityAsync(Reminder reminder)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var basePriority = 0.5;

                // Zaman faktörü;
                var timeFactor = CalculateTimeFactor(reminder);

                // Kategori faktörü;
                var categoryFactor = GetCategoryFactor(reminder.Category);

                // Tekrarlama faktörü;
                var recurrenceFactor = reminder.RecurrencePattern != null ? 0.1 : 0.3;

                // Son önceliği hesapla;
                var priority = basePriority + timeFactor + categoryFactor + recurrenceFactor;

                return Math.Min(Math.Max(priority, 0.0), 1.0);
            }

            private double CalculateTimeFactor(Reminder reminder)
            {
                var now = DateTime.UtcNow;
                var timeUntilTrigger = (reminder.NextOccurrence ?? reminder.TriggerTime) - now;

                if (timeUntilTrigger <= TimeSpan.Zero) return 0.8; // Geçmiş hatırlatıcı;
                if (timeUntilTrigger <= TimeSpan.FromHours(1)) return 0.7; // 1 saat içinde;
                if (timeUntilTrigger <= TimeSpan.FromHours(24)) return 0.5; // 24 saat içinde;
                if (timeUntilTrigger <= TimeSpan.FromDays(7)) return 0.3; // 1 hafta içinde;

                return 0.1; // Daha uzun süre;
            }

            private double GetCategoryFactor(string category)
            {
                return category?.ToLower() switch;
                {
                    "health" => 0.3,
                    "work" => 0.2,
                    "finance" => 0.25,
                    "personal" => 0.1,
                    "urgent" => 0.4,
                    _ => 0.0;
                };
            }
        }

        /// <summary>
        /// Ertelenme yöneticisi.
        /// </summary>
        internal class SnoozeManager;
        {
            public async Task<SnoozeResult> SnoozeReminderAsync(Reminder reminder, TimeSpan snoozeDuration)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                if (reminder.SnoozeCount >= reminder.MaxSnoozeCount)
                {
                    return new SnoozeResult;
                    {
                        Success = false,
                        Message = $"Maximum snooze count ({reminder.MaxSnoozeCount}) reached"
                    };
                }

                // Ertelenmiş zamanı hesapla;
                var newTriggerTime = DateTime.UtcNow.Add(snoozeDuration);

                // Hatırlatıcıyı güncelle;
                reminder.NextOccurrence = newTriggerTime;
                reminder.Status = ReminderStatus.Snoozed;
                reminder.SnoozeCount++;
                reminder.UpdatedAt = DateTime.UtcNow;

                return new SnoozeResult;
                {
                    Success = true,
                    NewTriggerTime = newTriggerTime,
                    SnoozeCount = reminder.SnoozeCount;
                };
            }
        }

        /// <summary>
        /// Tekrarlama hesaplayıcı.
        /// </summary>
        internal class RecurrenceCalculator;
        {
            public DateTime? CalculateNextOccurrence(DateTime baseTime, RecurrencePattern pattern)
            {
                if (pattern == null) return null;

                switch (pattern.Type)
                {
                    case RecurrenceType.Daily:
                        return baseTime.AddDays(pattern.Interval);

                    case RecurrenceType.Weekly:
                        return baseTime.AddDays(7 * pattern.Interval);

                    case RecurrenceType.Monthly:
                        return baseTime.AddMonths(pattern.Interval);

                    case RecurrenceType.Yearly:
                        return baseTime.AddYears(pattern.Interval);

                    case RecurrenceType.Custom:
                        if (pattern.CustomRule != null)
                        {
                            return pattern.CustomRule.CalculateNext(baseTime);
                        }
                        break;
                }

                return null;
            }
        }

        /// <summary>
        /// Akıllı alarm yöneticisi.
        /// </summary>
        internal class SmartAlertManager;
        {
            public async Task<NotificationStrategy> DetermineNotificationStrategyAsync(Reminder reminder)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                return new NotificationStrategy;
                {
                    Channels = DetermineChannels(reminder),
                    Intensity = DetermineIntensity(reminder.Priority),
                    Timing = DetermineTiming(reminder),
                    Escalation = DetermineEscalation(reminder)
                };
            }

            public async Task<List<SmartSuggestion>> GetSuggestionsAsync(Reminder reminder)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var suggestions = new List<SmartSuggestion>();

                // Zaman önerileri;
                if (reminder.TriggerTime.Hour < 6 || reminder.TriggerTime.Hour > 22)
                {
                    suggestions.Add(new SmartSuggestion;
                    {
                        Type = SuggestionType.TimeAdjustment,
                        Message = "Consider scheduling during waking hours",
                        Confidence = 0.8,
                        Action = new SuggestionAction;
                        {
                            Type = ActionType.Reschedule,
                            Parameters = new Dictionary<string, object>
                            {
                                ["newTime"] = reminder.TriggerTime.Date.AddHours(9) // Sabah 9;
                            }
                        }
                    });
                }

                return suggestions;
            }

            private List<NotificationChannel> DetermineChannels(Reminder reminder)
            {
                var channels = new List<NotificationChannel> { NotificationChannel.InApp };

                if (reminder.Priority > 0.7) channels.Add(NotificationChannel.Push);
                if (reminder.Priority > 0.9) channels.Add(NotificationChannel.Email);
                if (reminder.Type == ReminderType.Meeting) channels.Add(NotificationChannel.Calendar);

                return channels;
            }

            private NotificationIntensity DetermineIntensity(double priority)
            {
                return priority switch;
                {
                    > 0.8 => NotificationIntensity.High,
                    > 0.5 => NotificationIntensity.Medium,
                    _ => NotificationIntensity.Low;
                };
            }

            private NotificationTiming DetermineTiming(Reminder reminder)
            {
                return new NotificationTiming;
                {
                    InitialAlert = reminder.TriggerTime.AddMinutes(-15), // 15 dakika önce;
                    FollowUpInterval = reminder.Priority > 0.7 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15),
                    MaxFollowUps = reminder.Priority > 0.7 ? 3 : 1;
                };
            }

            private EscalationPlan DetermineEscalation(Reminder reminder)
            {
                return new EscalationPlan;
                {
                    Levels = new List<EscalationLevel>
                    {
                        new EscalationLevel { After = TimeSpan.FromMinutes(10), Intensity = NotificationIntensity.Medium },
                        new EscalationLevel { After = TimeSpan.FromMinutes(30), Intensity = NotificationIntensity.High },
                        new EscalationLevel { After = TimeSpan.FromHours(1), Channels = new List<NotificationChannel> { NotificationChannel.Email, NotificationChannel.Sms } }
                    }
                };
            }
        }

        /// <summary>
        /// Hatırlatıcı zamanlayıcı.
        /// </summary>
        internal class ReminderTimer;
        {
            public Timer Timer { get; }
            public DateTime TriggerTime { get; }

            public ReminderTimer(Timer timer, DateTime triggerTime)
            {
                Timer = timer ?? throw new ArgumentNullException(nameof(timer));
                TriggerTime = triggerTime;
            }
        }

        #endregion;
    }

    #region Public Models and Enums;

    /// <summary>
    /// Hatırlatıcı.
    /// </summary>
    public class Reminder;
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime TriggerTime { get; set; }
        public DateTime? NextOccurrence { get; set; }
        public double Priority { get; set; } = 0.5;
        public ReminderStatus Status { get; set; } = ReminderStatus.Scheduled;
        public ReminderType Type { get; set; } = ReminderType.General;
        public string Category { get; set; }
        public List<string> Tags { get; set; }
        public RecurrencePattern RecurrencePattern { get; set; }
        public NotificationSettings NotificationSettings { get; set; }
        public int MaxSnoozeCount { get; set; } = 3;
        public int SnoozeCount { get; set; }
        public TimeSpan ExpirationDuration { get; set; } = TimeSpan.FromDays(7);
        public Dictionary<string, object> Metadata { get; set; }
        public bool AllowPastTrigger { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastTriggered { get; set; }
        public int TriggerCount { get; set; }
        public DateTime? CompletionTime { get; set; }
        public CompletionStatus? CompletionStatus { get; set; }
        public string CompletionNotes { get; set; }

        public Reminder Clone()
        {
            return new Reminder;
            {
                Id = Id,
                UserId = UserId,
                Title = Title,
                Description = Description,
                TriggerTime = TriggerTime,
                NextOccurrence = NextOccurrence,
                Priority = Priority,
                Status = Status,
                Type = Type,
                Category = Category,
                Tags = Tags?.ToList(),
                RecurrencePattern = RecurrencePattern?.Clone(),
                NotificationSettings = NotificationSettings?.Clone(),
                MaxSnoozeCount = MaxSnoozeCount,
                SnoozeCount = SnoozeCount,
                ExpirationDuration = ExpirationDuration,
                Metadata = Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AllowPastTrigger = AllowPastTrigger,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                LastTriggered = LastTriggered,
                TriggerCount = TriggerCount,
                CompletionTime = CompletionTime,
                CompletionStatus = CompletionStatus,
                CompletionNotes = CompletionNotes;
            };
        }
    }

    /// <summary>
    /// Hatırlatıcı oluşturma sonucu.
    /// </summary>
    public class ReminderCreationResult;
    {
        public bool Success { get; set; }
        public Guid ReminderId { get; set; }
        public string Message { get; set; }
        public DateTime? TriggerTime { get; set; }
        public DateTime? NextOccurrence { get; set; }
        public double? Priority { get; set; }
    }

    /// <summary>
    /// Güncelleme sonucu.
    /// </summary>
    public class UpdateResult;
    {
        public bool Success { get; set; }
        public Guid ReminderId { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// İptal sonucu.
    /// </summary>
    public class CancellationResult;
    {
        public bool Success { get; set; }
        public Guid ReminderId { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Erteleme sonucu.
    /// </summary>
    public class SnoozeResult;
    {
        public bool Success { get; set; }
        public Guid ReminderId { get; set; }
        public string Message { get; set; }
        public DateTime? NewTriggerTime { get; set; }
        public int SnoozeCount { get; set; }
    }

    /// <summary>
    /// Tamamlama sonucu.
    /// </summary>
    public class CompletionResult;
    {
        public bool Success { get; set; }
        public Guid ReminderId { get; set; }
        public string Message { get; set; }
        public DateTime? CompletionTime { get; set; }
        public bool IsRecurring { get; set; }
    }

    /// <summary>
    /// Günlük hatırlatıcı özeti.
    /// </summary>
    public class DailyReminderSummary;
    {
        public DateTime Date { get; set; }
        public Guid UserId { get; set; }
        public int TotalReminders { get; set; }
        public int CompletedCount { get; set; }
        public int PendingCount { get; set; }
        public int OverdueCount { get; set; }
        public int SnoozedCount { get; set; }
        public int HighPriorityCount { get; set; }
        public Dictionary<string, int> RemindersByCategory { get; set; }
        public List<Reminder> UpcomingReminders { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı istatistikleri.
    /// </summary>
    public class ReminderStatistics;
    {
        public Guid UserId { get; set; }
        public TimeRange TimeRange { get; set; }
        public int TotalCount { get; set; }
        public int CompletedCount { get; set; }
        public int CancelledCount { get; set; }
        public int ExpiredCount { get; set; }
        public int SnoozedCount { get; set; }
        public double AveragePriority { get; set; }
        public TimeSpan AverageCompletionTime { get; set; }
        public double SuccessRate { get; set; }
        public Dictionary<string, int> Categories { get; set; }
        public Dictionary<ReminderType, int> Types { get; set; }
        public List<ReminderTrend> Trends { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı güncellemesi.
    /// </summary>
    public class ReminderUpdate;
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? TriggerTime { get; set; }
        public RecurrencePattern RecurrencePattern { get; set; }
        public string Category { get; set; }
        public List<string> Tags { get; set; }
        public double? Priority { get; set; }
        public NotificationSettings NotificationSettings { get; set; }
        public int? MaxSnoozeCount { get; set; }
        public TimeSpan? ExpirationDuration { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public UpdateType UpdateType { get; set; } = UpdateType.Modification;
    }

    /// <summary>
    /// Tekrarlama deseni.
    /// </summary>
    public class RecurrencePattern;
    {
        public RecurrenceType Type { get; set; }
        public int Interval { get; set; } = 1;
        public DateTime? EndDate { get; set; }
        public int? OccurrenceCount { get; set; }
        public List<DayOfWeek> DaysOfWeek { get; set; }
        public CustomRecurrenceRule CustomRule { get; set; }

        public RecurrencePattern Clone()
        {
            return new RecurrencePattern;
            {
                Type = Type,
                Interval = Interval,
                EndDate = EndDate,
                OccurrenceCount = OccurrenceCount,
                DaysOfWeek = DaysOfWeek?.ToList(),
                CustomRule = CustomRule?.Clone()
            };
        }
    }

    /// <summary>
    /// Bildirim ayarları.
    /// </summary>
    public class NotificationSettings;
    {
        public List<NotificationChannel> Channels { get; set; } = new List<NotificationChannel> { NotificationChannel.InApp };
        public TimeSpan PreReminderTime { get; set; } = TimeSpan.FromMinutes(15);
        public int MaxReminders { get; set; } = 3;
        public bool EscalateIfNotAcknowledged { get; set; } = true;
        public TimeSpan EscalationTime { get; set; } = TimeSpan.FromMinutes(30);

        public NotificationSettings Clone()
        {
            return new NotificationSettings;
            {
                Channels = Channels?.ToList(),
                PreReminderTime = PreReminderTime,
                MaxReminders = MaxReminders,
                EscalateIfNotAcknowledged = EscalateIfNotAcknowledged,
                EscalationTime = EscalationTime;
            };
        }
    }

    /// <summary>
    /// Tamamlama bilgileri.
    /// </summary>
    public class CompletionInfo;
    {
        public DateTime? CompletionTime { get; set; }
        public CompletionStatus Status { get; set; } = CompletionStatus.Completed;
        public string Notes { get; set; }
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

    /// <summary>
    /// Hatırlatıcı trendi.
    /// </summary>
    public class ReminderTrend;
    {
        public TrendType Type { get; set; }
        public double Value { get; set; }
        public double Change { get; set; }
        public double Confidence { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Günlük istatistikler.
    /// </summary>
    public class DailyStatistics;
    {
        public DateTime Date { get; set; }
        public int TotalCount { get; set; }
        public int CompletedCount { get; set; }
        public double CompletionRate { get; set; }
    }

    /// <summary>
    /// Bildirim stratejisi.
    /// </summary>
    public class NotificationStrategy;
    {
        public List<NotificationChannel> Channels { get; set; }
        public NotificationIntensity Intensity { get; set; }
        public NotificationTiming Timing { get; set; }
        public EscalationPlan Escalation { get; set; }
    }

    /// <summary>
    /// Akıllı öneri.
    /// </summary>
    public class SmartSuggestion;
    {
        public SuggestionType Type { get; set; }
        public string Message { get; set; }
        public double Confidence { get; set; }
        public SuggestionAction Action { get; set; }
    }

    /// <summary>
    /// Özel tekrarlama kuralı.
    /// </summary>
    public class CustomRecurrenceRule;
    {
        public string RuleDefinition { get; set; }
        public Dictionary<string, object> Parameters { get; set; }

        public DateTime? CalculateNext(DateTime baseTime)
        {
            // Özel kural hesaplaması;
            return baseTime.AddDays(1); // Basit örnek;
        }

        public CustomRecurrenceRule Clone()
        {
            return new CustomRecurrenceRule;
            {
                RuleDefinition = RuleDefinition,
                Parameters = Parameters?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };
        }
    }

    /// <summary>
    /// Öneri aksiyonu.
    /// </summary>
    public class SuggestionAction;
    {
        public ActionType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    /// <summary>
    /// Bildirim zamanlaması.
    /// </summary>
    public class NotificationTiming;
    {
        public DateTime? InitialAlert { get; set; }
        public TimeSpan FollowUpInterval { get; set; }
        public int MaxFollowUps { get; set; }
    }

    /// <summary>
    /// Eskalasyon planı.
    /// </summary>
    public class EscalationPlan;
    {
        public List<EscalationLevel> Levels { get; set; }
    }

    /// <summary>
    /// Eskalasyon seviyesi.
    /// </summary>
    public class EscalationLevel;
    {
        public TimeSpan After { get; set; }
        public NotificationIntensity Intensity { get; set; }
        public List<NotificationChannel> Channels { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Hatırlatıcı durumu.
    /// </summary>
    public enum ReminderStatus;
    {
        Scheduled,
        Triggered,
        Snoozed,
        Completed,
        Cancelled,
        Expired;
    }

    /// <summary>
    /// Hatırlatıcı türü.
    /// </summary>
    public enum ReminderType;
    {
        General,
        Meeting,
        Appointment,
        Medication,
        BillPayment,
        Birthday,
        Anniversary,
        TaskDeadline,
        Maintenance,
        Custom;
    }

    /// <summary>
    /// Tamamlama durumu.
    /// </summary>
    public enum CompletionStatus;
    {
        Completed,
        Skipped,
        PartiallyCompleted,
        Failed;
    }

    /// <summary>
    /// Tekrarlama türü.
    /// </summary>
    public enum RecurrenceType;
    {
        Daily,
        Weekly,
        Monthly,
        Yearly,
        Custom;
    }

    /// <summary>
    /// Bildirim kanalı.
    /// </summary>
    public enum NotificationChannel;
    {
        InApp,
        Push,
        Email,
        Sms,
        Calendar,
        Voice;
    }

    /// <summary>
    /// Güncelleme türü.
    /// </summary>
    public enum UpdateType;
    {
        Modification,
        Reschedule,
        PriorityChange,
        NotificationChange;
    }

    /// <summary>
    /// Trend türü.
    /// </summary>
    public enum TrendType;
    {
        CompletionRate,
        CreationRate,
        SnoozeRate,
        PriorityTrend;
    }

    /// <summary>
    /// Bildirim yoğunluğu.
    /// </summary>
    public enum NotificationIntensity;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Öneri türü.
    /// </summary>
    public enum SuggestionType;
    {
        TimeAdjustment,
        CategoryChange,
        PriorityAdjustment,
        RecurrenceSuggestion,
        NotificationOptimization;
    }

    /// <summary>
    /// Aksiyon türü.
    /// </summary>
    public enum ActionType;
    {
        Reschedule,
        ChangeCategory,
        AdjustPriority,
        AddRecurrence,
        ModifyNotifications;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Hatırlatıcı oluşturuldu event'i.
    /// </summary>
    public class ReminderCreatedEvent : IEvent;
    {
        public Guid ReminderId { get; set; }
        public Guid UserId { get; set; }
        public string Title { get; set; }
        public DateTime TriggerTime { get; set; }
        public double Priority { get; set; }
        public ReminderType ReminderType { get; set; }
        public bool IsRecurring { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı güncellendi event'i.
    /// </summary>
    public class ReminderUpdatedEvent : IEvent;
    {
        public Guid ReminderId { get; set; }
        public Guid UserId { get; set; }
        public Reminder OriginalReminder { get; set; }
        public Reminder UpdatedReminder { get; set; }
        public UpdateType UpdateType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı iptal edildi event'i.
    /// </summary>
    public class ReminderCancelledEvent : IEvent;
    {
        public Guid ReminderId { get; set; }
        public Guid UserId { get; set; }
        public string Title { get; set; }
        public string Reason { get; set; }
        public bool WasRecurring { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı tetiklendi event'i.
    /// </summary>
    public class ReminderTriggeredEvent : IEvent;
    {
        public Guid ReminderId { get; set; }
        public Guid UserId { get; set; }
        public string Title { get; set; }
        public DateTime TriggerTime { get; set; }
        public double Priority { get; set; }
        public ReminderType ReminderType { get; set; }
        public bool IsRecurring { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı ertelendi event'i.
    /// </summary>
    public class ReminderSnoozedEvent : IEvent;
    {
        public Guid ReminderId { get; set; }
        public Guid UserId { get; set; }
        public DateTime OriginalTriggerTime { get; set; }
        public DateTime NewTriggerTime { get; set; }
        public TimeSpan SnoozeDuration { get; set; }
        public int SnoozeCount { get; set; }
        public int MaxSnoozeCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı tamamlandı event'i.
    /// </summary>
    public class ReminderCompletedEvent : IEvent;
    {
        public Guid ReminderId { get; set; }
        public Guid UserId { get; set; }
        public string Title { get; set; }
        public DateTime TriggerTime { get; set; }
        public DateTime CompletionTime { get; set; }
        public CompletionStatus CompletionStatus { get; set; }
        public bool IsRecurring { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı yöneticisi başlatıldı event'i.
    /// </summary>
    public class ReminderManagerStartedEvent : IEvent;
    {
        public DateTime StartTime { get; set; }
        public int LoadedReminderCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hatırlatıcı yöneticisi durduruldu event'i.
    /// </summary>
    public class ReminderManagerStoppedEvent : IEvent;
    {
        public DateTime StopTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActiveReminderCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Akıllı öneriler event'i.
    /// </summary>
    public class SmartSuggestionsEvent : IEvent;
    {
        public Guid ReminderId { get; set; }
        public Guid UserId { get; set; }
        public List<SmartSuggestion> Suggestions { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kullanıcı aktivite event'i.
    /// </summary>
    public class UserActivityEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public ActivityType ActivityType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Aktivite türü.
    /// </summary>
    public enum ActivityType;
    {
        WorkStart,
        WorkEnd,
        BreakStart,
        BreakEnd,
        MeetingStart,
        MeetingEnd,
        FocusModeStart,
        FocusModeEnd;
    }

    #endregion;
}
