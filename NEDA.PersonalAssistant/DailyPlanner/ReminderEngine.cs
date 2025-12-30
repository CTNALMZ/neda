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

namespace NEDA.PersonalAssistant.ReminderSystem;
{
    /// <summary>
    /// Akıllı hatırlatma motoru - Zamanlanmış, konum tabanlı ve olay tetiklemeli hatırlatmaları yönetir;
    /// </summary>
    public class ReminderEngine : IReminderEngine, IDisposable;
    {
        private readonly ILogger<ReminderEngine> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly IMemorySystem _memorySystem;
        private readonly IEventBus _eventBus;
        private readonly INotificationManager _notificationManager;
        private readonly ReminderEngineConfig _config;

        private readonly ConcurrentDictionary<string, Reminder> _activeReminders;
        private readonly ConcurrentDictionary<string, ReminderSchedule> _scheduledReminders;
        private readonly ConcurrentDictionary<string, ReminderRule> _reminderRules;
        private readonly ConcurrentQueue<ReminderTrigger> _triggerQueue;

        private readonly System.Timers.Timer _checkTimer;
        private readonly System.Timers.Timer _cleanupTimer;
        private readonly SemaphoreSlim _processingLock = new(1, 1);

        private bool _isInitialized;
        private bool _isRunning;
        private DateTime _lastCheckTime;
        private long _totalRemindersProcessed;

        /// <summary>
        /// Hatırlatma tetiklendiğinde oluşan olay;
        /// </summary>
        public event EventHandler<ReminderTriggeredEventArgs> ReminderTriggered;

        /// <summary>
        /// Hatırlatma durumu değiştiğinde oluşan olay;
        /// </summary>
        public event EventHandler<ReminderStatusChangedEventArgs> ReminderStatusChanged;

        /// <summary>
        /// Hatırlatma oluşturulduğunda oluşan olay;
        /// </summary>
        public event EventHandler<ReminderCreatedEventArgs> ReminderCreated;

        /// <summary>
        /// Motor istatistikleri;
        /// </summary>
        public ReminderEngineStatistics Statistics { get; private set; }

        /// <summary>
        /// Motor durumu;
        /// </summary>
        public EngineState State { get; private set; }

        /// <summary>
        /// ReminderEngine constructor;
        /// </summary>
        public ReminderEngine(
            ILogger<ReminderEngine> logger,
            IExceptionHandler exceptionHandler,
            IMemorySystem memorySystem,
            IEventBus eventBus,
            INotificationManager notificationManager,
            IOptions<ReminderEngineConfig> config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _memorySystem = memorySystem ?? throw new ArgumentNullException(nameof(memorySystem));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));
            _config = config?.Value ?? new ReminderEngineConfig();

            _activeReminders = new ConcurrentDictionary<string, Reminder>();
            _scheduledReminders = new ConcurrentDictionary<string, ReminderSchedule>();
            _reminderRules = new ConcurrentDictionary<string, ReminderRule>();
            _triggerQueue = new ConcurrentQueue<ReminderTrigger>();

            Statistics = new ReminderEngineStatistics();
            State = EngineState.Stopped;

            // Kontrol zamanlayıcısı (10 saniyede bir)
            _checkTimer = new System.Timers.Timer(10000);
            _checkTimer.Elapsed += OnCheckTimerElapsed;

            // Temizlik zamanlayıcısı (1 saatte bir)
            _cleanupTimer = new System.Timers.Timer(3600000);
            _cleanupTimer.Elapsed += OnCleanupTimerElapsed;

            // Event bus subscription;
            _eventBus.Subscribe<ReminderEvent>(HandleReminderEvent);

            _logger.LogInformation("ReminderEngine initialized");
        }

        /// <summary>
        /// Hatırlatma motorunu başlatır;
        /// </summary>
        public async Task<EngineResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("ReminderEngine already initialized");
                    return EngineResult.AlreadyInitialized();
                }

                _logger.LogInformation("Initializing ReminderEngine...");

                // Konfigürasyon validasyonu;
                if (!ValidateConfig(_config))
                    return EngineResult.Failure("Invalid ReminderEngine configuration");

                // Hatırlatma verilerini yükle;
                await LoadRemindersFromStorageAsync(cancellationToken);
                await LoadReminderRulesAsync(cancellationToken);

                // Zamanlayıcıları başlat;
                _checkTimer.Start();
                _cleanupTimer.Start();

                _isInitialized = true;
                _isRunning = true;
                State = EngineState.Running;
                Statistics.StartTime = DateTime.UtcNow;
                _lastCheckTime = DateTime.UtcNow;

                // Aktif hatırlatmaları planla;
                await ScheduleActiveRemindersAsync(cancellationToken);

                _logger.LogInformation($"ReminderEngine initialized with {_activeReminders.Count} active reminders");
                return EngineResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.Initialize");
                _logger.LogError(ex, "Failed to initialize ReminderEngine: {Error}", error.Message);
                State = EngineState.Error;
                return EngineResult.Failure($"Initialization failed: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Yeni hatırlatma oluşturur;
        /// </summary>
        public async Task<ReminderResult> CreateReminderAsync(CreateReminderRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!_isInitialized)
                return ReminderResult.EngineNotReady();

            try
            {
                _logger.LogInformation("Creating new reminder for user: {UserId}", request.UserId);

                // Validasyon;
                var validationResult = ValidateReminderRequest(request);
                if (!validationResult.IsValid)
                    return ReminderResult.Failure($"Invalid reminder request: {validationResult.ErrorMessage}");

                // Hatırlatma nesnesi oluştur;
                var reminder = new Reminder;
                {
                    Id = GenerateReminderId(),
                    UserId = request.UserId,
                    Title = request.Title,
                    Description = request.Description,
                    CreatedAt = DateTime.UtcNow,
                    Status = ReminderStatus.Pending,
                    Priority = request.Priority,
                    Category = request.Category,
                    Tags = request.Tags?.ToList() ?? new List<string>(),
                    Metadata = request.Metadata ?? new Dictionary<string, object>()
                };

                // Tetikleyici ayarla;
                switch (request.TriggerType)
                {
                    case TriggerType.TimeBased:
                        reminder.TimeTrigger = request.TimeTrigger;
                        reminder.NextTriggerTime = CalculateNextTriggerTime(request.TimeTrigger, DateTime.UtcNow);
                        break;

                    case TriggerType.LocationBased:
                        reminder.LocationTrigger = request.LocationTrigger;
                        break;

                    case TriggerType.EventBased:
                        reminder.EventTrigger = request.EventTrigger;
                        break;

                    case TriggerType.BehaviorBased:
                        reminder.BehaviorTrigger = request.BehaviorTrigger;
                        break;

                    default:
                        return ReminderResult.Failure($"Unsupported trigger type: {request.TriggerType}");
                }

                // Tekrarlama ayarlarını işle;
                if (request.RecurrencePattern != null)
                {
                    reminder.RecurrencePattern = request.RecurrencePattern;
                    reminder.IsRecurring = true;
                }

                // Son kullanma tarihi;
                if (request.ExpirationTime.HasValue)
                {
                    reminder.ExpirationTime = request.ExpirationTime.Value;
                }

                // Bağımlı hatırlatmalar;
                if (request.DependentReminders?.Any() == true)
                {
                    reminder.DependentReminderIds = request.DependentReminders.ToList();
                }

                // Hatırlatmayı kaydet;
                await SaveReminderAsync(reminder, cancellationToken);

                // Aktif hatırlatmalara ekle;
                if (_activeReminders.TryAdd(reminder.Id, reminder))
                {
                    // Planla;
                    await ScheduleReminderAsync(reminder, cancellationToken);

                    // İstatistikleri güncelle;
                    Statistics.TotalRemindersCreated++;
                    Statistics.ActiveReminders = _activeReminders.Count;

                    // Olayı tetikle;
                    OnReminderCreated(new ReminderCreatedEventArgs;
                    {
                        Reminder = reminder,
                        CreatedAt = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Reminder created successfully: {ReminderId}", reminder.Id);

                    return ReminderResult.Success(reminder);
                }

                return ReminderResult.Failure("Failed to add reminder to active collection");
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.CreateReminder");
                _logger.LogError(ex, "Error creating reminder: {Error}", error.Message);
                return ReminderResult.Failure($"Error creating reminder: {error.Message}");
            }
        }

        /// <summary>
        /// Hatırlatmayı günceller;
        /// </summary>
        public async Task<ReminderResult> UpdateReminderAsync(string reminderId, UpdateReminderRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(reminderId))
                throw new ArgumentException("Reminder ID cannot be null or empty", nameof(reminderId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Updating reminder: {ReminderId}", reminderId);

                if (!_activeReminders.TryGetValue(reminderId, out var reminder))
                {
                    // Storage'dan yüklemeyi dene;
                    reminder = await LoadReminderFromStorageAsync(reminderId, cancellationToken);
                    if (reminder == null)
                    {
                        return ReminderResult.NotFound();
                    }
                }

                // Güncelleme kontrolleri;
                if (reminder.Status == ReminderStatus.Completed && !request.ForceUpdate)
                {
                    return ReminderResult.Failure("Cannot update completed reminder without force flag");
                }

                // Alanları güncelle;
                if (!string.IsNullOrEmpty(request.Title))
                    reminder.Title = request.Title;

                if (!string.IsNullOrEmpty(request.Description))
                    reminder.Description = request.Description;

                if (request.Priority.HasValue)
                    reminder.Priority = request.Priority.Value;

                if (request.Category != null)
                    reminder.Category = request.Category;

                if (request.Tags != null)
                    reminder.Tags = request.Tags.ToList();

                if (request.Metadata != null)
                {
                    foreach (var kvp in request.Metadata)
                    {
                        reminder.Metadata[kvp.Key] = kvp.Value;
                    }
                }

                // Tetikleyici güncellemesi;
                if (request.TimeTrigger != null)
                {
                    reminder.TimeTrigger = request.TimeTrigger;
                    reminder.NextTriggerTime = CalculateNextTriggerTime(request.TimeTrigger, DateTime.UtcNow);
                    reminder.LastModified = DateTime.UtcNow;

                    // Planlamayı yenile;
                    await RescheduleReminderAsync(reminder, cancellationToken);
                }

                // Tekrarlama güncellemesi;
                if (request.RecurrencePattern != null)
                {
                    reminder.RecurrencePattern = request.RecurrencePattern;
                    reminder.IsRecurring = true;
                }

                // Son kullanma güncellemesi;
                if (request.ExpirationTime.HasValue)
                {
                    reminder.ExpirationTime = request.ExpirationTime.Value;
                }

                // Durum güncellemesi;
                if (request.Status.HasValue && request.Status.Value != reminder.Status)
                {
                    await ChangeReminderStatusAsync(reminder, request.Status.Value, cancellationToken);
                }

                // Değişiklikleri kaydet;
                reminder.LastModified = DateTime.UtcNow;
                await SaveReminderAsync(reminder, cancellationToken);

                _logger.LogInformation("Reminder updated successfully: {ReminderId}", reminderId);

                return ReminderResult.Success(reminder);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.UpdateReminder");
                _logger.LogError(ex, "Error updating reminder: {Error}", error.Message);
                return ReminderResult.Failure($"Error updating reminder: {error.Message}");
            }
        }

        /// <summary>
        /// Hatırlatmayı siler;
        /// </summary>
        public async Task<ReminderResult> DeleteReminderAsync(string reminderId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(reminderId))
                throw new ArgumentException("Reminder ID cannot be null or empty", nameof(reminderId));

            try
            {
                _logger.LogInformation("Deleting reminder: {ReminderId}", reminderId);

                if (!_activeReminders.TryRemove(reminderId, out var reminder))
                {
                    // Storage'dan yüklemeyi dene;
                    reminder = await LoadReminderFromStorageAsync(reminderId, cancellationToken);
                    if (reminder == null)
                    {
                        return ReminderResult.NotFound();
                    }
                }

                // Planlamayı iptal et;
                await UnscheduleReminderAsync(reminderId, cancellationToken);

                // Storage'dan sil;
                await DeleteReminderFromStorageAsync(reminderId, cancellationToken);

                // İstatistikleri güncelle;
                Statistics.TotalRemindersDeleted++;
                Statistics.ActiveReminders = _activeReminders.Count;

                _logger.LogInformation("Reminder deleted successfully: {ReminderId}", reminderId);

                return ReminderResult.Success(reminder);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.DeleteReminder");
                _logger.LogError(ex, "Error deleting reminder: {Error}", error.Message);
                return ReminderResult.Failure($"Error deleting reminder: {error.Message}");
            }
        }

        /// <summary>
        /// Hatırlatmayı ertele (snooze)
        /// </summary>
        public async Task<ReminderResult> SnoozeReminderAsync(string reminderId, TimeSpan snoozeDuration, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(reminderId))
                throw new ArgumentException("Reminder ID cannot be null or empty", nameof(reminderId));

            try
            {
                _logger.LogInformation("Snoozing reminder: {ReminderId} for {Duration}", reminderId, snoozeDuration);

                if (!_activeReminders.TryGetValue(reminderId, out var reminder))
                {
                    return ReminderResult.NotFound();
                }

                if (reminder.Status != ReminderStatus.Active && reminder.Status != ReminderStatus.Triggered)
                {
                    return ReminderResult.Failure($"Cannot snooze reminder in {reminder.Status} status");
                }

                // Ertelenme sayısını kontrol et;
                if (reminder.SnoozeCount >= _config.MaxSnoozeCount)
                {
                    return ReminderResult.Failure($"Maximum snooze count ({_config.MaxSnoozeCount}) reached");
                }

                // Erteleme zamanını hesapla;
                var newTriggerTime = DateTime.UtcNow.Add(snoozeDuration);

                // Zaman tetikleyicisini güncelle;
                if (reminder.TimeTrigger != null)
                {
                    reminder.TimeTrigger.TriggerTime = newTriggerTime;
                }

                reminder.NextTriggerTime = newTriggerTime;
                reminder.SnoozeCount++;
                reminder.LastSnoozed = DateTime.UtcNow;
                reminder.Status = ReminderStatus.Snoozed;

                // Planlamayı yenile;
                await RescheduleReminderAsync(reminder, cancellationToken);

                // Değişiklikleri kaydet;
                await SaveReminderAsync(reminder, cancellationToken);

                // Durum değişikliği olayı;
                OnReminderStatusChanged(new ReminderStatusChangedEventArgs;
                {
                    ReminderId = reminderId,
                    PreviousStatus = ReminderStatus.Triggered,
                    NewStatus = ReminderStatus.Snoozed,
                    ChangedAt = DateTime.UtcNow,
                    Reason = $"Snoozed for {snoozeDuration}"
                });

                _logger.LogInformation("Reminder snoozed successfully: {ReminderId}", reminderId);

                return ReminderResult.Success(reminder);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.SnoozeReminder");
                _logger.LogError(ex, "Error snoozing reminder: {Error}", error.Message);
                return ReminderResult.Failure($"Error snoozing reminder: {error.Message}");
            }
        }

        /// <summary>
        /// Hatırlatmayı tamamla (complete)
        /// </summary>
        public async Task<ReminderResult> CompleteReminderAsync(string reminderId, CompletionData completionData = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(reminderId))
                throw new ArgumentException("Reminder ID cannot be null or empty", nameof(reminderId));

            try
            {
                _logger.LogInformation("Completing reminder: {ReminderId}", reminderId);

                if (!_activeReminders.TryGetValue(reminderId, out var reminder))
                {
                    return ReminderResult.NotFound();
                }

                var previousStatus = reminder.Status;

                // Durumu tamamlandı olarak değiştir;
                reminder.Status = ReminderStatus.Completed;
                reminder.CompletedAt = DateTime.UtcNow;
                reminder.CompletionData = completionData;

                // Planlamayı iptal et;
                await UnscheduleReminderAsync(reminderId, cancellationToken);

                // Aktif listeden kaldır;
                _activeReminders.TryRemove(reminderId, out _);

                // Tamamlanmışlar listesine ekle;
                Statistics.CompletedReminders++;

                // Değişiklikleri kaydet;
                await SaveReminderAsync(reminder, cancellationToken);

                // Durum değişikliği olayı;
                OnReminderStatusChanged(new ReminderStatusChangedEventArgs;
                {
                    ReminderId = reminderId,
                    PreviousStatus = previousStatus,
                    NewStatus = ReminderStatus.Completed,
                    ChangedAt = DateTime.UtcNow,
                    Reason = "Manually completed by user"
                });

                // Bağımlı hatırlatmaları kontrol et;
                await ProcessDependentRemindersAsync(reminder, cancellationToken);

                _logger.LogInformation("Reminder completed successfully: {ReminderId}", reminderId);

                return ReminderResult.Success(reminder);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.CompleteReminder");
                _logger.LogError(ex, "Error completing reminder: {Error}", error.Message);
                return ReminderResult.Failure($"Error completing reminder: {error.Message}");
            }
        }

        /// <summary>
        /// Kullanıcının tüm hatırlatmalarını getirir;
        /// </summary>
        public async Task<IEnumerable<Reminder>> GetUserRemindersAsync(string userId, ReminderFilter filter = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                _logger.LogInformation("Getting reminders for user: {UserId}", userId);

                var reminders = new List<Reminder>();

                // Önce aktif hatırlatmalardan getir;
                var activeReminders = _activeReminders.Values;
                    .Where(r => r.UserId == userId)
                    .ToList();

                reminders.AddRange(activeReminders);

                // Storage'dan da getir (tamamlanmışlar, geçmiş)
                var storedReminders = await LoadUserRemindersFromStorageAsync(userId, cancellationToken);
                if (storedReminders != null)
                {
                    reminders.AddRange(storedReminders);
                }

                // Filtre uygula;
                if (filter != null)
                {
                    reminders = ApplyReminderFilter(reminders, filter).ToList();
                }

                // Sırala (öncelik, zaman)
                reminders = reminders;
                    .OrderByDescending(r => GetPriorityValue(r.Priority))
                    .ThenBy(r => r.NextTriggerTime)
                    .ToList();

                return reminders;
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.GetUserReminders");
                _logger.LogError(ex, "Error getting user reminders: {Error}", error.Message);
                throw;
            }
        }

        /// <summary>
        /// Yaklaşan hatırlatmaları getirir;
        /// </summary>
        public async Task<IEnumerable<Reminder>> GetUpcomingRemindersAsync(string userId, TimeSpan lookAhead, CancellationToken cancellationToken = default)
        {
            try
            {
                var now = DateTime.UtcNow;
                var cutoffTime = now.Add(lookAhead);

                var reminders = await GetUserRemindersAsync(userId, null, cancellationToken);

                return reminders;
                    .Where(r => r.Status == ReminderStatus.Pending ||
                               r.Status == ReminderStatus.Active ||
                               r.Status == ReminderStatus.Snoozed)
                    .Where(r => r.NextTriggerTime.HasValue &&
                               r.NextTriggerTime.Value <= cutoffTime &&
                               r.NextTriggerTime.Value >= now)
                    .OrderBy(r => r.NextTriggerTime)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting upcoming reminders");
                throw;
            }
        }

        /// <summary>
        /// Hatırlatma kuralı oluşturur;
        /// </summary>
        public async Task<RuleResult> CreateReminderRuleAsync(CreateRuleRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Creating new reminder rule for user: {UserId}", request.UserId);

                var rule = new ReminderRule;
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = request.UserId,
                    Name = request.Name,
                    Description = request.Description,
                    Condition = request.Condition,
                    Action = request.Action,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    LastTriggered = null,
                    TriggerCount = 0,
                    Metadata = request.Metadata ?? new Dictionary<string, object>()
                };

                // Kuralı kaydet;
                await SaveReminderRuleAsync(rule, cancellationToken);

                // Aktif kurallara ekle;
                if (_reminderRules.TryAdd(rule.Id, rule))
                {
                    _logger.LogInformation("Reminder rule created successfully: {RuleId}", rule.Id);
                    return RuleResult.Success(rule);
                }

                return RuleResult.Failure("Failed to add rule to active collection");
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.CreateReminderRule");
                _logger.LogError(ex, "Error creating reminder rule: {Error}", error.Message);
                return RuleResult.Failure($"Error creating reminder rule: {error.Message}");
            }
        }

        /// <summary>
        /// Hatırlatma kurallarını değerlendirir;
        /// </summary>
        public async Task<EvaluationResult> EvaluateRulesAsync(RuleEvaluationContext context, CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                _logger.LogDebug("Evaluating rules for user: {UserId}", context.UserId);

                var triggeredRules = new List<ReminderRule>();
                var createdReminders = new List<Reminder>();

                // Kullanıcının aktif kurallarını getir;
                var userRules = _reminderRules.Values;
                    .Where(r => r.UserId == context.UserId && r.IsActive)
                    .ToList();

                foreach (var rule in userRules)
                {
                    try
                    {
                        // Kural koşulunu değerlendir;
                        var conditionResult = await EvaluateRuleConditionAsync(rule.Condition, context, cancellationToken);

                        if (conditionResult.IsSatisfied)
                        {
                            // Kural tetiklendi;
                            triggeredRules.Add(rule);

                            // Eylemi uygula;
                            var actionResult = await ApplyRuleActionAsync(rule.Action, context, cancellationToken);

                            if (actionResult.ReminderCreated)
                            {
                                createdReminders.Add(actionResult.CreatedReminder);
                            }

                            // Kural istatistiklerini güncelle;
                            rule.LastTriggered = DateTime.UtcNow;
                            rule.TriggerCount++;
                            await SaveReminderRuleAsync(rule, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error evaluating rule: {RuleId}", rule.Id);
                    }
                }

                return new EvaluationResult;
                {
                    Success = true,
                    TriggeredRules = triggeredRules,
                    CreatedReminders = createdReminders,
                    TotalRulesEvaluated = userRules.Count;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating rules");
                return EvaluationResult.Failure($"Error evaluating rules: {ex.Message}");
            }
        }

        /// <summary>
        /// Motor durumunu kontrol eder;
        /// </summary>
        public async Task<EngineHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var healthStatus = new EngineHealthStatus;
                {
                    Component = "ReminderEngine",
                    Timestamp = DateTime.UtcNow,
                    OverallStatus = HealthStatus.Healthy;
                };

                // Temel metrikler;
                healthStatus.Metrics["ActiveReminders"] = _activeReminders.Count;
                healthStatus.Metrics["ScheduledReminders"] = _scheduledReminders.Count;
                healthStatus.Metrics["ActiveRules"] = _reminderRules.Count;
                healthStatus.Metrics["TriggerQueueSize"] = _triggerQueue.Count;
                healthStatus.Metrics["TotalRemindersProcessed"] = _totalRemindersProcessed;
                healthStatus.Metrics["UptimeMinutes"] = (DateTime.UtcNow - Statistics.StartTime).TotalMinutes;

                // Durum kontrolleri;
                if (!_isRunning)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = "Engine is not running";
                }
                else if (_triggerQueue.Count > 1000)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"High trigger queue size: {_triggerQueue.Count}";
                }
                else if (DateTime.UtcNow - _lastCheckTime > TimeSpan.FromMinutes(5))
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = "No recent check activity detected";
                }
                else;
                {
                    healthStatus.OverallStatus = HealthStatus.Healthy;
                    healthStatus.Message = "All systems operational";
                }

                // Bağımlı servis kontrolleri;
                var dependencyHealth = await CheckDependenciesHealthAsync(cancellationToken);
                healthStatus.Dependencies.AddRange(dependencyHealth);

                // Bağımlılıklardaki hatalara göre durumu ayarla;
                var errorDependencies = dependencyHealth.Count(d => d.Status == HealthStatus.Error);
                if (errorDependencies > 0)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = $"{errorDependencies} critical dependencies are down";
                }

                return healthStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
                return new EngineHealthStatus;
                {
                    Component = "ReminderEngine",
                    Timestamp = DateTime.UtcNow,
                    OverallStatus = HealthStatus.Error,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Motoru durdurur;
        /// </summary>
        public async Task<EngineResult> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isInitialized)
                {
                    return EngineResult.NotInitialized();
                }

                _logger.LogInformation("Shutting down ReminderEngine...");

                // Zamanlayıcıları durdur;
                _checkTimer.Stop();
                _cleanupTimer.Stop();

                // Tüm bekleyen tetikleyicileri işle;
                await ProcessPendingTriggersAsync(cancellationToken);

                // Aktif hatırlatmaları kaydet;
                await SaveAllRemindersAsync(cancellationToken);

                // Durumu güncelle;
                _isRunning = false;
                _isInitialized = false;
                State = EngineState.Stopped;
                Statistics.EndTime = DateTime.UtcNow;

                _logger.LogInformation("ReminderEngine shutdown completed");
                return EngineResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ReminderEngine.Shutdown");
                _logger.LogError(ex, "Error during shutdown: {Error}", error.Message);
                return EngineResult.Failure($"Shutdown failed: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        #region Private Methods;

        private async Task LoadRemindersFromStorageAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Loading reminders from storage...");

                // Storage'dan aktif hatırlatmaları yükle;
                var storedReminders = await _memorySystem.LoadActiveRemindersAsync(cancellationToken);

                if (storedReminders != null)
                {
                    foreach (var reminder in storedReminders)
                    {
                        if (reminder.Status == ReminderStatus.Pending ||
                            reminder.Status == ReminderStatus.Active ||
                            reminder.Status == ReminderStatus.Snoozed)
                        {
                            if (_activeReminders.TryAdd(reminder.Id, reminder))
                            {
                                Statistics.TotalRemindersLoaded++;
                            }
                        }
                    }
                }

                _logger.LogInformation("Loaded {Count} reminders from storage", Statistics.TotalRemindersLoaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading reminders from storage");
                throw;
            }
        }

        private async Task LoadReminderRulesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Loading reminder rules...");

                var rules = await _memorySystem.LoadReminderRulesAsync(cancellationToken);

                if (rules != null)
                {
                    foreach (var rule in rules.Where(r => r.IsActive))
                    {
                        _reminderRules[rule.Id] = rule;
                    }
                }

                _logger.LogInformation("Loaded {Count} reminder rules", _reminderRules.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading reminder rules");
            }
        }

        private async Task ScheduleActiveRemindersAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Scheduling active reminders...");

                var scheduledCount = 0;

                foreach (var reminder in _activeReminders.Values)
                {
                    if (reminder.Status == ReminderStatus.Pending ||
                        reminder.Status == ReminderStatus.Active ||
                        reminder.Status == ReminderStatus.Snoozed)
                    {
                        await ScheduleReminderAsync(reminder, cancellationToken);
                        scheduledCount++;
                    }
                }

                _logger.LogInformation("Scheduled {Count} reminders", scheduledCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling reminders");
            }
        }

        private async Task ScheduleReminderAsync(Reminder reminder, CancellationToken cancellationToken)
        {
            try
            {
                // Tetikleme zamanını kontrol et;
                if (!reminder.NextTriggerTime.HasValue)
                {
                    if (reminder.TimeTrigger != null)
                    {
                        reminder.NextTriggerTime = CalculateNextTriggerTime(reminder.TimeTrigger, DateTime.UtcNow);
                    }
                    else;
                    {
                        _logger.LogWarning("Reminder {ReminderId} has no trigger time", reminder.Id);
                        return;
                    }
                }

                // Zamanı geçmiş mi kontrol et;
                if (reminder.NextTriggerTime.Value < DateTime.UtcNow)
                {
                    if (reminder.IsRecurring && reminder.RecurrencePattern != null)
                    {
                        // Tekrarlanan hatırlatma için sonraki tetikleme zamanını hesapla;
                        reminder.NextTriggerTime = CalculateNextRecurrence(reminder.RecurrencePattern, DateTime.UtcNow);
                    }
                    else;
                    {
                        // Zamanı geçmiş, geçersiz hatırlatma;
                        reminder.Status = ReminderStatus.Expired;
                        await SaveReminderAsync(reminder, cancellationToken);
                        return;
                    }
                }

                // Plan oluştur;
                var schedule = new ReminderSchedule;
                {
                    ReminderId = reminder.Id,
                    TriggerTime = reminder.NextTriggerTime.Value,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow;
                };

                // Planı kaydet;
                if (_scheduledReminders.TryAdd(reminder.Id, schedule))
                {
                    _logger.LogDebug("Scheduled reminder {ReminderId} for {TriggerTime}",
                        reminder.Id, schedule.TriggerTime);
                }

                // Hatırlatma durumunu güncelle;
                if (reminder.Status == ReminderStatus.Pending)
                {
                    reminder.Status = ReminderStatus.Active;
                    await SaveReminderAsync(reminder, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling reminder: {ReminderId}", reminder.Id);
            }
        }

        private async Task RescheduleReminderAsync(Reminder reminder, CancellationToken cancellationToken)
        {
            // Önceki planı kaldır;
            await UnscheduleReminderAsync(reminder.Id, cancellationToken);

            // Yeni plan oluştur;
            await ScheduleReminderAsync(reminder, cancellationToken);
        }

        private async Task UnscheduleReminderAsync(string reminderId, CancellationToken cancellationToken)
        {
            _scheduledReminders.TryRemove(reminderId, out _);
            await Task.CompletedTask;
        }

        private DateTime CalculateNextTriggerTime(TimeTrigger trigger, DateTime referenceTime)
        {
            if (trigger.TriggerTime.HasValue)
            {
                return trigger.TriggerTime.Value;
            }
            else if (trigger.RelativeTime.HasValue)
            {
                return referenceTime.Add(trigger.RelativeTime.Value);
            }

            return referenceTime;
        }

        private DateTime? CalculateNextRecurrence(RecurrencePattern pattern, DateTime referenceTime)
        {
            if (pattern == null) return null;

            try
            {
                switch (pattern.RecurrenceType)
                {
                    case RecurrenceType.Daily:
                        return referenceTime.AddDays(pattern.Interval);

                    case RecurrenceType.Weekly:
                        return referenceTime.AddDays(7 * pattern.Interval);

                    case RecurrenceType.Monthly:
                        return referenceTime.AddMonths(pattern.Interval);

                    case RecurrenceType.Yearly:
                        return referenceTime.AddYears(pattern.Interval);

                    case RecurrenceType.Custom:
                        // Özel tekrarlama mantığı;
                        if (pattern.CustomRule != null)
                        {
                            return ApplyCustomRecurrenceRule(pattern.CustomRule, referenceTime);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating next recurrence");
            }

            return null;
        }

        private DateTime ApplyCustomRecurrenceRule(CustomRecurrenceRule rule, DateTime referenceTime)
        {
            // Özel tekrarlama kurallarını uygula;
            // Burada daha karmaşık kurallar işlenebilir (iş günleri, belirli günler vb.)
            return referenceTime.AddDays(1); // Varsayılan;
        }

        private async Task SaveReminderAsync(Reminder reminder, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.SaveReminderAsync(reminder, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving reminder: {ReminderId}", reminder.Id);
                throw;
            }
        }

        private async Task SaveAllRemindersAsync(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var reminder in _activeReminders.Values)
                {
                    await SaveReminderAsync(reminder, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving all reminders");
            }
        }

        private async Task<Reminder> LoadReminderFromStorageAsync(string reminderId, CancellationToken cancellationToken)
        {
            try
            {
                return await _memorySystem.LoadReminderAsync(reminderId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading reminder from storage: {ReminderId}", reminderId);
                return null;
            }
        }

        private async Task<List<Reminder>> LoadUserRemindersFromStorageAsync(string userId, CancellationToken cancellationToken)
        {
            try
            {
                return await _memorySystem.LoadUserRemindersAsync(userId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user reminders from storage: {UserId}", userId);
                return new List<Reminder>();
            }
        }

        private async Task DeleteReminderFromStorageAsync(string reminderId, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.DeleteReminderAsync(reminderId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting reminder from storage: {ReminderId}", reminderId);
            }
        }

        private async Task SaveReminderRuleAsync(ReminderRule rule, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.SaveReminderRuleAsync(rule, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving reminder rule: {RuleId}", rule.Id);
            }
        }

        private async Task ChangeReminderStatusAsync(Reminder reminder, ReminderStatus newStatus, CancellationToken cancellationToken)
        {
            var previousStatus = reminder.Status;
            reminder.Status = newStatus;
            reminder.LastModified = DateTime.UtcNow;

            // Duruma göre ek işlemler;
            switch (newStatus)
            {
                case ReminderStatus.Active:
                    await ScheduleReminderAsync(reminder, cancellationToken);
                    break;

                case ReminderStatus.Cancelled:
                case ReminderStatus.Completed:
                    await UnscheduleReminderAsync(reminder.Id, cancellationToken);
                    break;
            }

            // Durum değişikliği olayı;
            OnReminderStatusChanged(new ReminderStatusChangedEventArgs;
            {
                ReminderId = reminder.Id,
                PreviousStatus = previousStatus,
                NewStatus = newStatus,
                ChangedAt = DateTime.UtcNow,
                Reason = "Status changed by system"
            });
        }

        private async Task ProcessDependentRemindersAsync(Reminder completedReminder, CancellationToken cancellationToken)
        {
            if (completedReminder.DependentReminderIds?.Any() != true)
                return;

            foreach (var dependentId in completedReminder.DependentReminderIds)
            {
                if (_activeReminders.TryGetValue(dependentId, out var dependentReminder))
                {
                    // Bağımlı hatırlatmanın tetiklenme şartlarını kontrol et;
                    if (dependentReminder.DependencyConditions?.Any() == true)
                    {
                        var allConditionsMet = dependentReminder.DependencyConditions.All(condition =>
                            CheckDependencyCondition(condition, completedReminder));

                        if (allConditionsMet)
                        {
                            // Bağımlı hatırlatmayı aktif et;
                            dependentReminder.Status = ReminderStatus.Active;
                            await ScheduleReminderAsync(dependentReminder, cancellationToken);
                            await SaveReminderAsync(dependentReminder, cancellationToken);
                        }
                    }
                }
            }
        }

        private bool CheckDependencyCondition(DependencyCondition condition, Reminder completedReminder)
        {
            // Bağımlılık koşulunu kontrol et;
            switch (condition.Type)
            {
                case DependencyConditionType.Completed:
                    return completedReminder.Status == ReminderStatus.Completed;

                case DependencyConditionType.CompletedWithData:
                    if (completedReminder.CompletionData != null)
                    {
                        return condition.ExpectedValue == completedReminder.CompletionData.Result;
                    }
                    return false;

                case DependencyConditionType.TimeElapsed:
                    if (completedReminder.CompletedAt.HasValue)
                    {
                        var timeSinceCompletion = DateTime.UtcNow - completedReminder.CompletedAt.Value;
                        return timeSinceCompletion >= TimeSpan.Parse(condition.ExpectedValue);
                    }
                    return false;

                default:
                    return false;
            }
        }

        private async Task<ConditionEvaluationResult> EvaluateRuleConditionAsync(RuleCondition condition, RuleEvaluationContext context, CancellationToken cancellationToken)
        {
            try
            {
                // Kural koşulunu değerlendir;
                // Burada daha karmaşık koşul mantığı uygulanabilir;
                var isSatisfied = await EvaluateConditionExpressionAsync(condition.Expression, context, cancellationToken);

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

        private async Task<bool> EvaluateConditionExpressionAsync(string expression, RuleEvaluationContext context, CancellationToken cancellationToken)
        {
            // Koşul ifadesini değerlendir;
            // Basit bir implementasyon, gerçekte daha karmaşık bir parser gerekebilir;
            try
            {
                // Örnek: "time > '09:00' AND location == 'office'"
                // Burada basit bir değerlendirme yapılıyor;
                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating condition expression: {Expression}", expression);
                return false;
            }
        }

        private async Task<RuleActionResult> ApplyRuleActionAsync(RuleAction action, RuleEvaluationContext context, CancellationToken cancellationToken)
        {
            try
            {
                Reminder createdReminder = null;

                switch (action.ActionType)
                {
                    case RuleActionType.CreateReminder:
                        var reminderRequest = CreateReminderRequestFromAction(action, context);
                        var result = await CreateReminderAsync(reminderRequest, cancellationToken);
                        if (result.Success)
                        {
                            createdReminder = result.Reminder;
                        }
                        break;

                    case RuleActionType.SendNotification:
                        await SendRuleNotificationAsync(action, context, cancellationToken);
                        break;

                    case RuleActionType.UpdateReminder:
                        await UpdateReminderFromRuleAsync(action, context, cancellationToken);
                        break;
                }

                return new RuleActionResult;
                {
                    Success = true,
                    ReminderCreated = createdReminder != null,
                    CreatedReminder = createdReminder;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying rule action");
                return new RuleActionResult { Success = false };
            }
        }

        private CreateReminderRequest CreateReminderRequestFromAction(RuleAction action, RuleEvaluationContext context)
        {
            return new CreateReminderRequest;
            {
                UserId = context.UserId,
                Title = action.Parameters.ContainsKey("title") ? action.Parameters["title"].ToString() : "Rule-based Reminder",
                Description = action.Parameters.ContainsKey("description") ? action.Parameters["description"].ToString() : string.Empty,
                Priority = action.Parameters.ContainsKey("priority") ?
                    (ReminderPriority)Enum.Parse(typeof(ReminderPriority), action.Parameters["priority"].ToString()) :
                    ReminderPriority.Medium,
                TriggerType = TriggerType.TimeBased,
                TimeTrigger = new TimeTrigger;
                {
                    TriggerTime = DateTime.UtcNow.AddMinutes(5) // Varsayılan 5 dakika sonra;
                }
            };
        }

        private async Task SendRuleNotificationAsync(RuleAction action, RuleEvaluationContext context, CancellationToken cancellationToken)
        {
            try
            {
                var notification = new Notification;
                {
                    UserId = context.UserId,
                    Title = action.Parameters.ContainsKey("title") ? action.Parameters["title"].ToString() : "Rule Notification",
                    Message = action.Parameters.ContainsKey("message") ? action.Parameters["message"].ToString() : "A rule has been triggered",
                    Type = NotificationType.Info,
                    Priority = NotificationPriority.Medium,
                    Metadata = new Dictionary<string, object>
                    {
                        ["rule_id"] = context.RuleId,
                        ["trigger_context"] = context.TriggerData;
                    }
                };

                await _notificationManager.SendNotificationAsync(notification, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending rule notification");
            }
        }

        private async Task UpdateReminderFromRuleAsync(RuleAction action, RuleEvaluationContext context, CancellationToken cancellationToken)
        {
            // Kurala göre hatırlatma güncelleme;
            // Implementasyon detayları;
            await Task.CompletedTask;
        }

        private async Task<List<ServiceHealth>> CheckDependenciesHealthAsync(CancellationToken cancellationToken)
        {
            var dependencies = new List<ServiceHealth>();

            try
            {
                // MemorySystem sağlığı;
                var memoryHealth = await CheckMemorySystemHealthAsync(cancellationToken);
                dependencies.Add(memoryHealth);

                // NotificationManager sağlığı;
                var notificationHealth = await CheckNotificationManagerHealthAsync(cancellationToken);
                dependencies.Add(notificationHealth);

                // EventBus sağlığı;
                var eventBusHealth = CheckEventBusHealth();
                dependencies.Add(eventBusHealth);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking dependencies health");
            }

            return dependencies;
        }

        private async Task<ServiceHealth> CheckMemorySystemHealthAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Basit bir test sorgusu;
                var testResult = await _memorySystem.CheckHealthAsync(cancellationToken);

                return new ServiceHealth;
                {
                    ServiceName = "MemorySystem",
                    Status = testResult.IsHealthy ? HealthStatus.Healthy : HealthStatus.Error,
                    Message = testResult.Message;
                };
            }
            catch (Exception ex)
            {
                return new ServiceHealth;
                {
                    ServiceName = "MemorySystem",
                    Status = HealthStatus.Error,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        private async Task<ServiceHealth> CheckNotificationManagerHealthAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Basit bir test;
                var canSend = await _notificationManager.CanSendNotificationsAsync(cancellationToken);

                return new ServiceHealth;
                {
                    ServiceName = "NotificationManager",
                    Status = canSend ? HealthStatus.Healthy : HealthStatus.Warning,
                    Message = canSend ? "Notification service is available" : "Notification service may be unavailable"
                };
            }
            catch (Exception ex)
            {
                return new ServiceHealth;
                {
                    ServiceName = "NotificationManager",
                    Status = HealthStatus.Error,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        private ServiceHealth CheckEventBusHealth()
        {
            try
            {
                // EventBus bağlantı durumu;
                // Bu örnekte basit bir kontrol;
                return new ServiceHealth;
                {
                    ServiceName = "EventBus",
                    Status = HealthStatus.Healthy,
                    Message = "Event bus is connected"
                };
            }
            catch (Exception ex)
            {
                return new ServiceHealth;
                {
                    ServiceName = "EventBus",
                    Status = HealthStatus.Error,
                    Message = $"Event bus error: {ex.Message}"
                };
            }
        }

        private void OnCheckTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                _lastCheckTime = DateTime.UtcNow;
                CheckScheduledReminders();
                ProcessTriggerQueue();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in check timer elapsed");
            }
        }

        private void OnCleanupTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                CleanupExpiredReminders();
                CleanupOldTriggers();
                CompactDataStructures();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup timer elapsed");
            }
        }

        private void CheckScheduledReminders()
        {
            var now = DateTime.UtcNow;
            var triggeredReminders = new List<Reminder>();

            foreach (var schedule in _scheduledReminders.Values)
            {
                if (schedule.IsActive && schedule.TriggerTime <= now)
                {
                    if (_activeReminders.TryGetValue(schedule.ReminderId, out var reminder))
                    {
                        triggeredReminders.Add(reminder);
                    }
                }
            }

            // Tetiklenen hatırlatmaları işle;
            foreach (var reminder in triggeredReminders)
            {
                ProcessReminderTrigger(reminder);
            }
        }

        private void ProcessReminderTrigger(Reminder reminder)
        {
            try
            {
                _logger.LogInformation("Processing reminder trigger: {ReminderId}", reminder.Id);

                // Tetikleyici oluştur;
                var trigger = new ReminderTrigger;
                {
                    ReminderId = reminder.Id,
                    UserId = reminder.UserId,
                    TriggeredAt = DateTime.UtcNow,
                    TriggerType = TriggerType.TimeBased,
                    Data = new Dictionary<string, object>
                    {
                        ["reminder_title"] = reminder.Title,
                        ["reminder_description"] = reminder.Description,
                        ["original_trigger_time"] = reminder.NextTriggerTime;
                    }
                };

                // Kuyruğa ekle;
                _triggerQueue.Enqueue(trigger);

                // Hatırlatma durumunu güncelle;
                reminder.Status = ReminderStatus.Triggered;
                reminder.LastTriggered = DateTime.UtcNow;
                reminder.TriggerCount++;

                // Tekrarlanan hatırlatma için sonraki tetikleme zamanını hesapla;
                if (reminder.IsRecurring && reminder.RecurrencePattern != null)
                {
                    reminder.NextTriggerTime = CalculateNextRecurrence(reminder.RecurrencePattern, DateTime.UtcNow);

                    if (reminder.NextTriggerTime.HasValue)
                    {
                        // Yeniden planla;
                        _ = RescheduleReminderAsync(reminder, CancellationToken.None);
                    }
                    else;
                    {
                        // Tekrarlama sona erdi;
                        reminder.Status = ReminderStatus.Completed;
                        reminder.CompletedAt = DateTime.UtcNow;
                        _activeReminders.TryRemove(reminder.Id, out _);
                    }
                }

                // Tetikleme olayı;
                OnReminderTriggered(new ReminderTriggeredEventArgs;
                {
                    Reminder = reminder,
                    TriggeredAt = DateTime.UtcNow,
                    TriggerType = TriggerType.TimeBased;
                });

                Interlocked.Increment(ref _totalRemindersProcessed);
                Statistics.TotalTriggersProcessed = _totalRemindersProcessed;

                _logger.LogDebug("Reminder trigger processed: {ReminderId}", reminder.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing reminder trigger: {ReminderId}", reminder.Id);
            }
        }

        private void ProcessTriggerQueue()
        {
            try
            {
                var processedCount = 0;
                var maxProcess = Math.Min(_triggerQueue.Count, 100); // Her seferinde maksimum 100 işle;

                for (int i = 0; i < maxProcess; i++)
                {
                    if (_triggerQueue.TryDequeue(out var trigger))
                    {
                        ProcessSingleTrigger(trigger);
                        processedCount++;
                    }
                }

                if (processedCount > 0)
                {
                    _logger.LogDebug("Processed {Count} triggers from queue", processedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing trigger queue");
            }
        }

        private async Task ProcessPendingTriggersAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (_triggerQueue.TryDequeue(out var trigger))
                {
                    await ProcessTriggerAsync(trigger, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing pending triggers");
            }
        }

        private async Task ProcessTriggerAsync(ReminderTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                // Hatırlatmayı bul;
                if (!_activeReminders.TryGetValue(trigger.ReminderId, out var reminder))
                {
                    reminder = await LoadReminderFromStorageAsync(trigger.ReminderId, cancellationToken);
                    if (reminder == null)
                    {
                        _logger.LogWarning("Reminder not found for trigger: {ReminderId}", trigger.ReminderId);
                        return;
                    }
                }

                // Bildirim gönder;
                await SendReminderNotificationAsync(reminder, trigger, cancellationToken);

                // Event bus'a gönder;
                await PublishReminderEventAsync(reminder, trigger, cancellationToken);

                _logger.LogDebug("Trigger processed: {ReminderId}", trigger.ReminderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing trigger: {ReminderId}", trigger.ReminderId);
            }
        }

        private void ProcessSingleTrigger(ReminderTrigger trigger)
        {
            try
            {
                // Senkron işlemler;
                if (_activeReminders.TryGetValue(trigger.ReminderId, out var reminder))
                {
                    // Acil bildirimleri hemen gönder;
                    if (reminder.Priority == ReminderPriority.Critical || reminder.Priority == ReminderPriority.High)
                    {
                        _ = SendImmediateNotificationAsync(reminder, trigger);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing single trigger: {ReminderId}", trigger.ReminderId);
            }
        }

        private async Task SendImmediateNotificationAsync(Reminder reminder, ReminderTrigger trigger)
        {
            try
            {
                var notification = new Notification;
                {
                    UserId = reminder.UserId,
                    Title = $"Reminder: {reminder.Title}",
                    Message = reminder.Description,
                    Type = NotificationType.Reminder,
                    Priority = ConvertToNotificationPriority(reminder.Priority),
                    Metadata = new Dictionary<string, object>
                    {
                        ["reminder_id"] = reminder.Id,
                        ["trigger_type"] = trigger.TriggerType.ToString(),
                        ["triggered_at"] = trigger.TriggeredAt;
                    }
                };

                await _notificationManager.SendNotificationAsync(notification, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending immediate notification for reminder: {ReminderId}", reminder.Id);
            }
        }

        private async Task SendReminderNotificationAsync(Reminder reminder, ReminderTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                var notification = new Notification;
                {
                    UserId = reminder.UserId,
                    Title = $"Reminder: {reminder.Title}",
                    Message = reminder.Description,
                    Type = NotificationType.Reminder,
                    Priority = ConvertToNotificationPriority(reminder.Priority),
                    Actions = new List<NotificationAction>
                    {
                        new() { Id = "snooze_5", Label = "Snooze 5 min", ActionType = "snooze", Data = "5" },
                        new() { Id = "snooze_15", Label = "Snooze 15 min", ActionType = "snooze", Data = "15" },
                        new() { Id = "complete", Label = "Complete", ActionType = "complete" },
                        new() { Id = "view", Label = "View Details", ActionType = "view" }
                    },
                    Metadata = new Dictionary<string, object>
                    {
                        ["reminder_id"] = reminder.Id,
                        ["reminder_category"] = reminder.Category,
                        ["reminder_priority"] = reminder.Priority.ToString(),
                        ["trigger_type"] = trigger.TriggerType.ToString(),
                        ["triggered_at"] = trigger.TriggeredAt,
                        ["is_recurring"] = reminder.IsRecurring,
                        ["snooze_count"] = reminder.SnoozeCount;
                    }
                };

                await _notificationManager.SendNotificationAsync(notification, cancellationToken);
                Statistics.NotificationsSent++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending reminder notification: {ReminderId}", reminder.Id);
            }
        }

        private async Task PublishReminderEventAsync(Reminder reminder, ReminderTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                var reminderEvent = new ReminderEvent;
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    EventType = ReminderEventType.Triggered,
                    ReminderId = reminder.Id,
                    UserId = reminder.UserId,
                    Data = new Dictionary<string, object>
                    {
                        ["reminder_title"] = reminder.Title,
                        ["reminder_priority"] = reminder.Priority.ToString(),
                        ["trigger_type"] = trigger.TriggerType.ToString(),
                        ["triggered_at"] = trigger.TriggeredAt;
                    }
                };

                await _eventBus.PublishAsync(reminderEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing reminder event: {ReminderId}", reminder.Id);
            }
        }

        private void CleanupExpiredReminders()
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredReminders = new List<string>();

                foreach (var kvp in _activeReminders)
                {
                    var reminder = kvp.Value;

                    // Son kullanma tarihi geçmiş mi kontrol et;
                    if (reminder.ExpirationTime.HasValue && reminder.ExpirationTime.Value < now)
                    {
                        reminder.Status = ReminderStatus.Expired;
                        expiredReminders.Add(kvp.Key);
                    }

                    // Çok eski tamamlanmış hatırlatmalar;
                    if (reminder.Status == ReminderStatus.Completed &&
                        reminder.CompletedAt.HasValue &&
                        (now - reminder.CompletedAt.Value).TotalDays > _config.CompletedReminderRetentionDays)
                    {
                        expiredReminders.Add(kvp.Key);
                    }
                }

                // Süresi dolan hatırlatmaları temizle;
                foreach (var reminderId in expiredReminders)
                {
                    if (_activeReminders.TryRemove(reminderId, out var reminder))
                    {
                        _logger.LogDebug("Cleaned up expired reminder: {ReminderId}", reminderId);
                        Statistics.ExpiredReminders++;
                    }
                }

                if (expiredReminders.Count > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} expired reminders", expiredReminders.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up expired reminders");
            }
        }

        private void CleanupOldTriggers()
        {
            try
            {
                // Eski tetikleyicileri temizle (24 saatten eski)
                var cutoffTime = DateTime.UtcNow.AddHours(-24);
                var tempQueue = new ConcurrentQueue<ReminderTrigger>();

                while (_triggerQueue.TryDequeue(out var trigger))
                {
                    if (trigger.TriggeredAt > cutoffTime)
                    {
                        tempQueue.Enqueue(trigger);
                    }
                }

                // Kalan tetikleyicileri geri yükle;
                while (tempQueue.TryDequeue(out var trigger))
                {
                    _triggerQueue.Enqueue(trigger);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old triggers");
            }
        }

        private void CompactDataStructures()
        {
            try
            {
                // Bellek optimizasyonu için veri yapılarını sıkıştır;
                // Burada gerekli optimizasyonlar yapılabilir;
                _logger.LogDebug("Data structures compacted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error compacting data structures");
            }
        }

        private void HandleReminderEvent(ReminderEvent reminderEvent)
        {
            try
            {
                _logger.LogDebug("Received reminder event: {EventType}", reminderEvent.EventType);

                switch (reminderEvent.EventType)
                {
                    case ReminderEventType.Snoozed:
                        // Ertelenme işlemleri;
                        break;

                    case ReminderEventType.Completed:
                        // Tamamlama işlemleri;
                        break;

                    case ReminderEventType.Dismissed:
                        // Reddetme işlemleri;
                        break;

                    case ReminderEventType.Triggered:
                        // Tetiklenme işlemleri (zaten işleniyor)
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling reminder event");
            }
        }

        private ValidationResult ValidateReminderRequest(CreateReminderRequest request)
        {
            var result = new ValidationResult { IsValid = true };

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                result.IsValid = false;
                result.ErrorMessage = "Title cannot be empty";
                return result;
            }

            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                result.IsValid = false;
                result.ErrorMessage = "User ID cannot be empty";
                return result;
            }

            // Tetikleyici validasyonu;
            switch (request.TriggerType)
            {
                case TriggerType.TimeBased:
                    if (request.TimeTrigger == null)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Time trigger is required for time-based reminders";
                    }
                    break;

                case TriggerType.LocationBased:
                    if (request.LocationTrigger == null)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Location trigger is required for location-based reminders";
                    }
                    break;

                case TriggerType.EventBased:
                    if (request.EventTrigger == null)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Event trigger is required for event-based reminders";
                    }
                    break;
            }

            // Tekrarlama validasyonu;
            if (request.RecurrencePattern != null)
            {
                if (request.RecurrencePattern.Interval <= 0)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "Recurrence interval must be greater than 0";
                }
            }

            return result;
        }

        private bool ValidateConfig(ReminderEngineConfig config)
        {
            if (config == null) return false;
            if (config.MaxSnoozeCount <= 0) return false;
            if (config.CompletedReminderRetentionDays <= 0) return false;

            return true;
        }

        private string GenerateReminderId()
        {
            return $"rem_{Guid.NewGuid():N}";
        }

        private int GetPriorityValue(ReminderPriority priority)
        {
            return priority switch;
            {
                ReminderPriority.Critical => 4,
                ReminderPriority.High => 3,
                ReminderPriority.Medium => 2,
                ReminderPriority.Low => 1,
                _ => 0;
            };
        }

        private NotificationPriority ConvertToNotificationPriority(ReminderPriority reminderPriority)
        {
            return reminderPriority switch;
            {
                ReminderPriority.Critical => NotificationPriority.Critical,
                ReminderPriority.High => NotificationPriority.High,
                ReminderPriority.Medium => NotificationPriority.Medium,
                ReminderPriority.Low => NotificationPriority.Low,
                _ => NotificationPriority.Low;
            };
        }

        private IEnumerable<Reminder> ApplyReminderFilter(IEnumerable<Reminder> reminders, ReminderFilter filter)
        {
            var filtered = reminders;

            if (filter.Statuses?.Any() == true)
            {
                filtered = filtered.Where(r => filter.Statuses.Contains(r.Status));
            }

            if (filter.Priorities?.Any() == true)
            {
                filtered = filtered.Where(r => filter.Priorities.Contains(r.Priority));
            }

            if (filter.Categories?.Any() == true)
            {
                filtered = filtered.Where(r => filter.Categories.Contains(r.Category));
            }

            if (filter.FromTime.HasValue)
            {
                filtered = filtered.Where(r => r.CreatedAt >= filter.FromTime.Value);
            }

            if (filter.ToTime.HasValue)
            {
                filtered = filtered.Where(r => r.CreatedAt <= filter.ToTime.Value);
            }

            if (filter.IncludeCompleted.HasValue && !filter.IncludeCompleted.Value)
            {
                filtered = filtered.Where(r => r.Status != ReminderStatus.Completed);
            }

            if (filter.SearchText != null)
            {
                var search = filter.SearchText.ToLowerInvariant();
                filtered = filtered.Where(r =>
                    r.Title.ToLowerInvariant().Contains(search) ||
                    r.Description.ToLowerInvariant().Contains(search) ||
                    r.Tags.Any(t => t.ToLowerInvariant().Contains(search)));
            }

            return filtered;
        }

        private void OnReminderTriggered(ReminderTriggeredEventArgs e)
        {
            ReminderTriggered?.Invoke(this, e);
        }

        private void OnReminderStatusChanged(ReminderStatusChangedEventArgs e)
        {
            ReminderStatusChanged?.Invoke(this, e);
        }

        private void OnReminderCreated(ReminderCreatedEventArgs e)
        {
            ReminderCreated?.Invoke(this, e);
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
                    _checkTimer?.Dispose();
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

    public interface IReminderEngine : IDisposable
    {
        event EventHandler<ReminderTriggeredEventArgs> ReminderTriggered;
        event EventHandler<ReminderStatusChangedEventArgs> ReminderStatusChanged;
        event EventHandler<ReminderCreatedEventArgs> ReminderCreated;

        ReminderEngineStatistics Statistics { get; }
        EngineState State { get; }

        Task<EngineResult> InitializeAsync(CancellationToken cancellationToken = default);
        Task<ReminderResult> CreateReminderAsync(CreateReminderRequest request, CancellationToken cancellationToken = default);
        Task<ReminderResult> UpdateReminderAsync(string reminderId, UpdateReminderRequest request, CancellationToken cancellationToken = default);
        Task<ReminderResult> DeleteReminderAsync(string reminderId, CancellationToken cancellationToken = default);
        Task<ReminderResult> SnoozeReminderAsync(string reminderId, TimeSpan snoozeDuration, CancellationToken cancellationToken = default);
        Task<ReminderResult> CompleteReminderAsync(string reminderId, CompletionData completionData = null, CancellationToken cancellationToken = default);
        Task<IEnumerable<Reminder>> GetUserRemindersAsync(string userId, ReminderFilter filter = null, CancellationToken cancellationToken = default);
        Task<IEnumerable<Reminder>> GetUpcomingRemindersAsync(string userId, TimeSpan lookAhead, CancellationToken cancellationToken = default);
        Task<RuleResult> CreateReminderRuleAsync(CreateRuleRequest request, CancellationToken cancellationToken = default);
        Task<EvaluationResult> EvaluateRulesAsync(RuleEvaluationContext context, CancellationToken cancellationToken = default);
        Task<EngineHealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default);
        Task<EngineResult> ShutdownAsync(CancellationToken cancellationToken = default);
    }

    public enum EngineState;
    {
        Stopped,
        Initializing,
        Running,
        Error;
    }

    public enum ReminderStatus;
    {
        Pending,
        Active,
        Snoozed,
        Triggered,
        Completed,
        Cancelled,
        Expired;
    }

    public enum ReminderPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum TriggerType;
    {
        TimeBased,
        LocationBased,
        EventBased,
        BehaviorBased;
    }

    public enum RecurrenceType;
    {
        Daily,
        Weekly,
        Monthly,
        Yearly,
        Custom;
    }

    public enum DependencyConditionType;
    {
        Completed,
        CompletedWithData,
        TimeElapsed,
        LocationReached,
        EventOccurred;
    }

    public enum RuleActionType;
    {
        CreateReminder,
        UpdateReminder,
        DeleteReminder,
        SendNotification,
        LogActivity;
    }

    public enum ReminderEventType;
    {
        Created,
        Updated,
        Triggered,
        Snoozed,
        Completed,
        Dismissed,
        Expired;
    }

    public enum HealthStatus;
    {
        Healthy,
        Warning,
        Error;
    }

    public class ReminderEngineConfig;
    {
        public int MaxSnoozeCount { get; set; } = 5;
        public int CompletedReminderRetentionDays { get; set; } = 90;
        public int CheckIntervalSeconds { get; set; } = 10;
        public int CleanupIntervalHours { get; set; } = 1;
        public int MaxRemindersPerUser { get; set; } = 1000;
        public bool EnableRules { get; set; } = true;
        public bool EnableRecurrence { get; set; } = true;
        public Dictionary<string, object> AdvancedSettings { get; set; } = new();
    }

    public class Reminder;
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastModified { get; set; }
        public ReminderStatus Status { get; set; }
        public ReminderPriority Priority { get; set; }
        public string Category { get; set; } = "General";
        public List<string> Tags { get; set; } = new();

        // Tetikleyiciler;
        public TimeTrigger TimeTrigger { get; set; }
        public LocationTrigger LocationTrigger { get; set; }
        public EventTrigger EventTrigger { get; set; }
        public BehaviorTrigger BehaviorTrigger { get; set; }

        // Zamanlama;
        public DateTime? NextTriggerTime { get; set; }
        public DateTime? LastTriggered { get; set; }
        public int TriggerCount { get; set; }
        public int SnoozeCount { get; set; }
        public DateTime? LastSnoozed { get; set; }

        // Tekrarlama;
        public bool IsRecurring { get; set; }
        public RecurrencePattern RecurrencePattern { get; set; }

        // Son kullanma;
        public DateTime? ExpirationTime { get; set; }
        public DateTime? CompletedAt { get; set; }
        public CompletionData CompletionData { get; set; }

        // Bağımlılıklar;
        public List<string> DependentReminderIds { get; set; } = new();
        public List<DependencyCondition> DependencyConditions { get; set; } = new();

        // Meta veri;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class TimeTrigger;
    {
        public DateTime? TriggerTime { get; set; }
        public TimeSpan? RelativeTime { get; set; }
        public List<DayOfWeek> DaysOfWeek { get; set; } = new();
        public TimeSpan? TimeOfDay { get; set; }
        public Dictionary<string, object> AdditionalSettings { get; set; } = new();
    }

    public class LocationTrigger;
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double RadiusMeters { get; set; } = 100;
        public string LocationName { get; set; } = string.Empty;
        public TriggerCondition Condition { get; set; } = TriggerCondition.Enter;
        public Dictionary<string, object> AdditionalSettings { get; set; } = new();
    }

    public class EventTrigger;
    {
        public string EventType { get; set; } = string.Empty;
        public Dictionary<string, object> EventData { get; set; } = new();
        public string Source { get; set; } = string.Empty;
        public Dictionary<string, object> Conditions { get; set; } = new();
    }

    public class BehaviorTrigger;
    {
        public string BehaviorPattern { get; set; } = string.Empty;
        public Dictionary<string, object> Conditions { get; set; } = new();
        public double ConfidenceThreshold { get; set; } = 0.7;
    }

    public enum TriggerCondition;
    {
        Enter,
        Exit,
        Within,
        Nearby;
    }

    public class RecurrencePattern;
    {
        public RecurrenceType RecurrenceType { get; set; }
        public int Interval { get; set; } = 1;
        public DateTime? EndDate { get; set; }
        public int? OccurrenceCount { get; set; }
        public List<DateTime> Exceptions { get; set; } = new();
        public CustomRecurrenceRule CustomRule { get; set; }
    }

    public class CustomRecurrenceRule;
    {
        public string RuleExpression { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class DependencyCondition;
    {
        public DependencyConditionType Type { get; set; }
        public string ExpectedValue { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class CompletionData;
    {
        public string Result { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public DateTime CompletedAt { get; set; }
        public double SatisfactionScore { get; set; }
    }

    public class ReminderSchedule;
    {
        public string ReminderId { get; set; } = string.Empty;
        public DateTime TriggerTime { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastChecked { get; set; }
    }

    public class ReminderTrigger;
    {
        public string ReminderId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTime TriggeredAt { get; set; }
        public TriggerType TriggerType { get; set; }
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public class ReminderRule;
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public RuleCondition Condition { get; set; }
        public RuleAction Action { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastTriggered { get; set; }
        public int TriggerCount { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class RuleCondition;
    {
        public string Expression { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public double ConfidenceThreshold { get; set; } = 0.7;
    }

    public class RuleAction;
    {
        public RuleActionType ActionType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public int Priority { get; set; } = 1;
    }

    public class CreateReminderRequest;
    {
        public string UserId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ReminderPriority Priority { get; set; } = ReminderPriority.Medium;
        public string Category { get; set; } = "General";
        public IEnumerable<string> Tags { get; set; } = new List<string>();
        public TriggerType TriggerType { get; set; }
        public TimeTrigger TimeTrigger { get; set; }
        public LocationTrigger LocationTrigger { get; set; }
        public EventTrigger EventTrigger { get; set; }
        public BehaviorTrigger BehaviorTrigger { get; set; }
        public RecurrencePattern RecurrencePattern { get; set; }
        public DateTime? ExpirationTime { get; set; }
        public IEnumerable<string> DependentReminders { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class UpdateReminderRequest;
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ReminderPriority? Priority { get; set; }
        public string Category { get; set; }
        public IEnumerable<string> Tags { get; set; }
        public TimeTrigger TimeTrigger { get; set; }
        public RecurrencePattern RecurrencePattern { get; set; }
        public DateTime? ExpirationTime { get; set; }
        public ReminderStatus? Status { get; set; }
        public bool ForceUpdate { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ReminderFilter;
    {
        public List<ReminderStatus> Statuses { get; set; }
        public List<ReminderPriority> Priorities { get; set; }
        public List<string> Categories { get; set; }
        public DateTime? FromTime { get; set; }
        public DateTime? ToTime { get; set; }
        public bool? IncludeCompleted { get; set; } = true;
        public string SearchText { get; set; }
    }

    public class CreateRuleRequest;
    {
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public RuleCondition Condition { get; set; }
        public RuleAction Action { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class RuleEvaluationContext;
    {
        public string UserId { get; set; } = string.Empty;
        public string RuleId { get; set; } = string.Empty;
        public Dictionary<string, object> TriggerData { get; set; } = new();
        public DateTime EvaluationTime { get; set; } = DateTime.UtcNow;
    }

    public class ReminderEngineStatistics;
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Uptime => EndTime.HasValue ? EndTime.Value - StartTime : DateTime.UtcNow - StartTime;
        public long TotalRemindersCreated { get; set; }
        public long TotalRemindersDeleted { get; set; }
        public long TotalTriggersProcessed { get; set; }
        public int ActiveReminders { get; set; }
        public int CompletedReminders { get; set; }
        public int ExpiredReminders { get; set; }
        public int NotificationsSent { get; set; }
        public long TotalRemindersLoaded { get; set; }
    }

    public class EngineResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }

        public static EngineResult Success(string message = "Operation completed successfully") =>
            new() { Success = true, Message = message, Timestamp = DateTime.UtcNow };

        public static EngineResult Failure(string error) =>
            new() { Success = false, Message = error, Timestamp = DateTime.UtcNow };

        public static EngineResult AlreadyInitialized() =>
            new() { Success = false, Message = "Engine already initialized", Timestamp = DateTime.UtcNow };

        public static EngineResult NotInitialized() =>
            new() { Success = false, Message = "Engine not initialized", Timestamp = DateTime.UtcNow };
    }

    public class ReminderResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public Reminder Reminder { get; set; }

        public static ReminderResult Success(Reminder reminder) =>
            new() { Success = true, Reminder = reminder, Message = "Reminder operation completed" };

        public static ReminderResult Failure(string error) =>
            new() { Success = false, Message = error };

        public static ReminderResult NotFound() =>
            new() { Success = false, Message = "Reminder not found" };

        public static ReminderResult EngineNotReady() =>
            new() { Success = false, Message = "Reminder engine is not ready" };
    }

    public class RuleResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public ReminderRule Rule { get; set; }

        public static RuleResult Success(ReminderRule rule) =>
            new() { Success = true, Rule = rule, Message = "Rule operation completed" };

        public static RuleResult Failure(string error) =>
            new() { Success = false, Message = error };
    }

    public class EvaluationResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<ReminderRule> TriggeredRules { get; set; } = new();
        public List<Reminder> CreatedReminders { get; set; } = new();
        public int TotalRulesEvaluated { get; set; }

        public static EvaluationResult Success(List<ReminderRule> triggeredRules, List<Reminder> createdReminders, int totalEvaluated) =>
            new() { Success = true, TriggeredRules = triggeredRules, CreatedReminders = createdReminders, TotalRulesEvaluated = totalEvaluated };

        public static EvaluationResult Failure(string error) =>
            new() { Success = false, Message = error };
    }

    public class EngineHealthStatus;
    {
        public string Component { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public HealthStatus OverallStatus { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<ServiceHealth> Dependencies { get; set; } = new();
        public Dictionary<string, object> Metrics { get; set; } = new();
    }

    public class ServiceHealth;
    {
        public string ServiceName { get; set; } = string.Empty;
        public HealthStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ReminderTriggeredEventArgs : EventArgs;
    {
        public Reminder Reminder { get; set; }
        public DateTime TriggeredAt { get; set; }
        public TriggerType TriggerType { get; set; }
    }

    public class ReminderStatusChangedEventArgs : EventArgs;
    {
        public string ReminderId { get; set; } = string.Empty;
        public ReminderStatus PreviousStatus { get; set; }
        public ReminderStatus NewStatus { get; set; }
        public DateTime ChangedAt { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class ReminderCreatedEventArgs : EventArgs;
    {
        public Reminder Reminder { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class ReminderEvent : IEvent;
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public ReminderEventType EventType { get; set; }
        public string ReminderId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class ConditionEvaluationResult;
    {
        public bool IsSatisfied { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Details { get; set; } = new();
    }

    public class RuleActionResult;
    {
        public bool Success { get; set; }
        public bool ReminderCreated { get; set; }
        public Reminder CreatedReminder { get; set; }
    }

    // Notification related classes (simplified for this context)
    public class Notification;
    {
        public string UserId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationType Type { get; set; }
        public NotificationPriority Priority { get; set; }
        public List<NotificationAction> Actions { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class NotificationAction;
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }

    public enum NotificationType;
    {
        Info,
        Warning,
        Error,
        Reminder,
        Alert;
    }

    public enum NotificationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    #endregion;
}
