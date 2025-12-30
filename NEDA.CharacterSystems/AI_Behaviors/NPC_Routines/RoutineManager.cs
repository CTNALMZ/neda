using NEDA.Brain.MemorySystem;
using NEDA.Brain.MemorySystem.RecallMechanism;
using NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees;
using NEDA.CharacterSystems.AI_Behaviors.BlackboardSystems;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Logging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.CharacterSystems.AI_Behaviors.NPC_Routines;
{
    /// <summary>
    /// NPC rutinlerini yöneten ana sınıf;
    /// Gerçek zamanlı rutin planlama, yürütme ve izleme sağlar;
    /// </summary>
    public interface IRoutineManager : IDisposable
    {
        /// <summary>
        /// NPC için yeni bir rutin ekler;
        /// </summary>
        Task<RoutineResult> AddRoutineAsync(string npcId, Routine routine);

        /// <summary>
        /// NPC'nin tüm rutinlerini getirir;
        /// </summary>
        Task<IEnumerable<Routine>> GetRoutinesAsync(string npcId);

        /// <summary>
        /// Rutini günceller;
        /// </summary>
        Task<RoutineResult> UpdateRoutineAsync(string npcId, string routineId, Routine updatedRoutine);

        /// <summary>
        /// Rutini siler;
        /// </summary>
        Task<RoutineResult> RemoveRoutineAsync(string npcId, string routineId);

        /// <summary>
        /// NPC için rutin planı oluşturur;
        /// </summary>
        Task<SchedulePlan> GenerateScheduleAsync(string npcId, ScheduleContext context);

        /// <summary>
        /// Aktif rutinleri başlatır;
        /// </summary>
        Task StartRoutinesAsync(string npcId);

        /// <summary>
        /// Rutinleri durdurur;
        /// </summary>
        Task StopRoutinesAsync(string npcId);

        /// <summary>
        /// Rutin durumunu alır;
        /// </summary>
        Task<RoutineStatus> GetRoutineStatusAsync(string npcId, string routineId);

        /// <summary>
        /// Rutin olayına abone olur;
        /// </summary>
        void SubscribeToRoutineEvents(string npcId, Action<RoutineEvent> handler);

        /// <summary>
        /// NPC'nin mevcut aktivitesini alır;
        /// </summary>
        Task<CurrentActivity> GetCurrentActivityAsync(string npcId);

        /// <summary>
        /// Rutin geçmişini alır;
        /// </summary>
        Task<RoutineHistory> GetRoutineHistoryAsync(string npcId, DateTime from, DateTime to);
    }

    /// <summary>
    /// RoutineManager implementasyonu;
    /// </summary>
    public class RoutineManager : IRoutineManager;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IBlackboard _blackboard;
        private readonly ILongTermMemory _longTermMemory;
        private readonly IActivityScheduler _activityScheduler;
        private readonly ISchedulePlanner _schedulePlanner;

        private readonly Dictionary<string, NPCContext> _npcContexts;
        private readonly Dictionary<string, List<RoutineEventSubscription>> _eventSubscriptions;
        private readonly Dictionary<string, CancellationTokenSource> _routineCancellationTokens;
        private readonly RoutineRepository _routineRepository;
        private readonly RoutineExecutor _routineExecutor;
        private readonly RoutineValidator _routineValidator;

        private bool _disposed = false;
        private readonly object _lock = new object();

        /// <summary>
        /// RoutineManager constructor;
        /// </summary>
        public RoutineManager(
            ILogger logger,
            IEventBus eventBus,
            IBlackboard blackboard,
            ILongTermMemory longTermMemory,
            IActivityScheduler activityScheduler,
            ISchedulePlanner schedulePlanner)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));
            _activityScheduler = activityScheduler ?? throw new ArgumentNullException(nameof(activityScheduler));
            _schedulePlanner = schedulePlanner ?? throw new ArgumentNullException(nameof(schedulePlanner));

            _npcContexts = new Dictionary<string, NPCContext>();
            _eventSubscriptions = new Dictionary<string, List<RoutineEventSubscription>>();
            _routineCancellationTokens = new Dictionary<string, CancellationTokenSource>();

            _routineRepository = new RoutineRepository();
            _routineExecutor = new RoutineExecutor(_logger, _eventBus);
            _routineValidator = new RoutineValidator();

            InitializeEventHandlers();

            _logger.LogInformation("RoutineManager initialized successfully");
        }

        /// <summary>
        /// NPC için yeni rutin ekler;
        /// </summary>
        public async Task<RoutineResult> AddRoutineAsync(string npcId, Routine routine)
        {
            ValidateNPCId(npcId);
            ValidateRoutine(routine);

            try
            {
                // Rutin validasyonu;
                var validationResult = await _routineValidator.ValidateAsync(routine);
                if (!validationResult.IsValid)
                {
                    return RoutineResult.Failure($"Routine validation failed: {validationResult.ErrorMessage}");
                }

                // NPC context'ini kontrol et;
                var npcContext = GetOrCreateNPCContext(npcId);

                // Rutini repository'e ekle;
                await _routineRepository.AddRoutineAsync(npcId, routine);

                // Blackboard'a rutin bilgisini kaydet;
                await _blackboard.SetValueAsync($"{npcId}:routines:{routine.Id}", routine);

                // Long term memory'e kaydet;
                await _longTermMemory.StoreExperienceAsync(
                    new RoutineExperience(routine, RoutineActionType.Added, DateTime.UtcNow));

                // Event yayınla;
                await _eventBus.PublishAsync(new RoutineAddedEvent;
                {
                    NPCId = npcId,
                    RoutineId = routine.Id,
                    RoutineName = routine.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Routine '{routine.Name}' added for NPC '{npcId}'");

                return RoutineResult.Success(routine.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add routine for NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to add routine for NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineAddFailed);
            }
        }

        /// <summary>
        /// NPC'nin tüm rutinlerini getirir;
        /// </summary>
        public async Task<IEnumerable<Routine>> GetRoutinesAsync(string npcId)
        {
            ValidateNPCId(npcId);

            try
            {
                // Repository'den rutinleri getir;
                var routines = await _routineRepository.GetRoutinesAsync(npcId);

                // Filtrele: sadece aktif rutinler;
                var activeRoutines = routines.Where(r => r.IsActive).ToList();

                // Her rutin için güncel durumu kontrol et;
                foreach (var routine in activeRoutines)
                {
                    routine.CurrentState = await GetRoutineCurrentStateAsync(npcId, routine);
                }

                return activeRoutines;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get routines for NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to get routines for NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineRetrieveFailed);
            }
        }

        /// <summary>
        /// Rutini günceller;
        /// </summary>
        public async Task<RoutineResult> UpdateRoutineAsync(string npcId, string routineId, Routine updatedRoutine)
        {
            ValidateNPCId(npcId);
            ValidateRoutineId(routineId);
            ValidateRoutine(updatedRoutine);

            try
            {
                // Mevcut rutini kontrol et;
                var existingRoutine = await _routineRepository.GetRoutineAsync(npcId, routineId);
                if (existingRoutine == null)
                {
                    return RoutineResult.Failure($"Routine '{routineId}' not found for NPC '{npcId}'");
                }

                // Validasyon;
                var validationResult = await _routineValidator.ValidateAsync(updatedRoutine);
                if (!validationResult.IsValid)
                {
                    return RoutineResult.Failure($"Routine validation failed: {validationResult.ErrorMessage}");
                }

                // Rutin durumunu kontrol et (çalışan rutin güncellenemez)
                var currentState = await GetRoutineCurrentStateAsync(npcId, existingRoutine);
                if (currentState == RoutineExecutionState.Running)
                {
                    return RoutineResult.Failure($"Cannot update running routine '{routineId}'");
                }

                // Güncelle;
                updatedRoutine.Id = routineId; // ID koru;
                updatedRoutine.LastModified = DateTime.UtcNow;

                await _routineRepository.UpdateRoutineAsync(npcId, routineId, updatedRoutine);

                // Blackboard'ı güncelle;
                await _blackboard.SetValueAsync($"{npcId}:routines:{routineId}", updatedRoutine);

                // Long term memory'e kaydet;
                await _longTermMemory.StoreExperienceAsync(
                    new RoutineExperience(updatedRoutine, RoutineActionType.Updated, DateTime.UtcNow));

                // Event yayınla;
                await _eventBus.PublishAsync(new RoutineUpdatedEvent;
                {
                    NPCId = npcId,
                    RoutineId = routineId,
                    RoutineName = updatedRoutine.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Routine '{routineId}' updated for NPC '{npcId}'");

                return RoutineResult.Success(routineId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update routine '{routineId}' for NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to update routine '{routineId}' for NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineUpdateFailed);
            }
        }

        /// <summary>
        /// Rutini siler;
        /// </summary>
        public async Task<RoutineResult> RemoveRoutineAsync(string npcId, string routineId)
        {
            ValidateNPCId(npcId);
            ValidateRoutineId(routineId);

            try
            {
                // Mevcut rutini kontrol et;
                var existingRoutine = await _routineRepository.GetRoutineAsync(npcId, routineId);
                if (existingRoutine == null)
                {
                    return RoutineResult.Failure($"Routine '{routineId}' not found for NPC '{npcId}'");
                }

                // Rutin durumunu kontrol et (çalışan rutin silinemez)
                var currentState = await GetRoutineCurrentStateAsync(npcId, existingRoutine);
                if (currentState == RoutineExecutionState.Running)
                {
                    return RoutineResult.Failure($"Cannot delete running routine '{routineId}'");
                }

                // Sil;
                await _routineRepository.RemoveRoutineAsync(npcId, routineId);

                // Blackboard'dan kaldır;
                await _blackboard.RemoveValueAsync($"{npcId}:routines:{routineId}");

                // Long term memory'e kaydet;
                await _longTermMemory.StoreExperienceAsync(
                    new RoutineExperience(existingRoutine, RoutineActionType.Deleted, DateTime.UtcNow));

                // Event yayınla;
                await _eventBus.PublishAsync(new RoutineRemovedEvent;
                {
                    NPCId = npcId,
                    RoutineId = routineId,
                    RoutineName = existingRoutine.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Routine '{routineId}' removed from NPC '{npcId}'");

                return RoutineResult.Success(routineId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove routine '{routineId}' from NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to remove routine '{routineId}' from NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineDeleteFailed);
            }
        }

        /// <summary>
        /// NPC için rutin planı oluşturur;
        /// </summary>
        public async Task<SchedulePlan> GenerateScheduleAsync(string npcId, ScheduleContext context)
        {
            ValidateNPCId(npcId);
            if (context == null) throw new ArgumentNullException(nameof(context));

            try
            {
                // NPC context'ini al;
                var npcContext = GetOrCreateNPCContext(npcId);

                // Mevcut rutinleri getir;
                var routines = await _routineRepository.GetRoutinesAsync(npcId);
                var activeRoutines = routines.Where(r => r.IsActive).ToList();

                if (!activeRoutines.Any())
                {
                    _logger.LogWarning($"No active routines found for NPC '{npcId}'");
                    return new SchedulePlan;
                    {
                        NPCId = npcId,
                        Date = context.StartDate.Date,
                        Activities = new List<ScheduledActivity>(),
                        Status = ScheduleStatus.Empty;
                    };
                }

                // SchedulePlanner ile plan oluştur;
                var schedulePlan = await _schedulePlanner.GeneratePlanAsync(
                    npcId,
                    activeRoutines,
                    context);

                // Planı NPC context'ine kaydet;
                npcContext.CurrentSchedule = schedulePlan;

                // Blackboard'a kaydet;
                await _blackboard.SetValueAsync($"{npcId}:current_schedule", schedulePlan);

                // Event yayınla;
                await _eventBus.PublishAsync(new ScheduleGeneratedEvent;
                {
                    NPCId = npcId,
                    ScheduleDate = schedulePlan.Date,
                    ActivityCount = schedulePlan.Activities.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Schedule generated for NPC '{npcId}' with {schedulePlan.Activities.Count} activities");

                return schedulePlan;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to generate schedule for NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to generate schedule for NPC '{npcId}'",
                    ex,
                    ErrorCodes.ScheduleGenerationFailed);
            }
        }

        /// <summary>
        /// Aktif rutinleri başlatır;
        /// </summary>
        public async Task StartRoutinesAsync(string npcId)
        {
            ValidateNPCId(npcId);

            try
            {
                lock (_lock)
                {
                    // Eğer zaten çalışıyorsa iptal et;
                    if (_routineCancellationTokens.ContainsKey(npcId))
                    {
                        _routineCancellationTokens[npcId].Cancel();
                        _routineCancellationTokens.Remove(npcId);
                    }

                    // Yeni cancellation token oluştur;
                    var cts = new CancellationTokenSource();
                    _routineCancellationTokens[npcId] = cts;
                }

                // NPC context'ini al;
                var npcContext = GetOrCreateNPCContext(npcId);

                // Aktif rutinleri getir;
                var activeRoutines = (await _routineRepository.GetRoutinesAsync(npcId))
                    .Where(r => r.IsActive &&
                           r.ExecutionTime.StartTime <= DateTime.Now.TimeOfDay &&
                           r.ExecutionTime.EndTime >= DateTime.Now.TimeOfDay)
                    .ToList();

                if (!activeRoutines.Any())
                {
                    _logger.LogWarning($"No active routines to start for NPC '{npcId}'");
                    return;
                }

                // Her rutin için execution task'ı başlat;
                foreach (var routine in activeRoutines)
                {
                    var cancellationToken = _routineCancellationTokens[npcId].Token;

                    // RoutineExecutor ile rutini başlat;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await _routineExecutor.ExecuteRoutineAsync(
                                npcId,
                                routine,
                                npcContext,
                                cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation($"Routine '{routine.Name}' execution cancelled for NPC '{npcId}'");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error executing routine '{routine.Name}' for NPC '{npcId}'");
                        }
                    }, cancellationToken);
                }

                // NPC context'ini güncelle;
                npcContext.IsRunning = true;
                npcContext.StartedAt = DateTime.UtcNow;

                // Event yayınla;
                await _eventBus.PublishAsync(new RoutinesStartedEvent;
                {
                    NPCId = npcId,
                    RoutineCount = activeRoutines.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Started {activeRoutines.Count} routines for NPC '{npcId}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start routines for NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to start routines for NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineStartFailed);
            }
        }

        /// <summary>
        /// Rutinleri durdurur;
        /// </summary>
        public async Task StopRoutinesAsync(string npcId)
        {
            ValidateNPCId(npcId);

            try
            {
                lock (_lock)
                {
                    if (_routineCancellationTokens.ContainsKey(npcId))
                    {
                        // Cancellation token'ı tetikle;
                        _routineCancellationTokens[npcId].Cancel();
                        _routineCancellationTokens.Remove(npcId);
                    }
                }

                // NPC context'ini güncelle;
                if (_npcContexts.ContainsKey(npcId))
                {
                    _npcContexts[npcId].IsRunning = false;
                    _npcContexts[npcId].StoppedAt = DateTime.UtcNow;
                }

                // Event yayınla;
                await _eventBus.PublishAsync(new RoutinesStoppedEvent;
                {
                    NPCId = npcId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Stopped all routines for NPC '{npcId}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to stop routines for NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to stop routines for NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineStopFailed);
            }
        }

        /// <summary>
        /// Rutin durumunu alır;
        /// </summary>
        public async Task<RoutineStatus> GetRoutineStatusAsync(string npcId, string routineId)
        {
            ValidateNPCId(npcId);
            ValidateRoutineId(routineId);

            try
            {
                // Rutini getir;
                var routine = await _routineRepository.GetRoutineAsync(npcId, routineId);
                if (routine == null)
                {
                    throw new RoutineNotFoundException($"Routine '{routineId}' not found for NPC '{npcId}'");
                }

                // Mevcut durumu hesapla;
                var currentState = await GetRoutineCurrentStateAsync(npcId, routine);

                // Progress durumunu hesapla;
                var progress = await CalculateRoutineProgressAsync(npcId, routine);

                return new RoutineStatus;
                {
                    RoutineId = routineId,
                    RoutineName = routine.Name,
                    CurrentState = currentState,
                    Progress = progress,
                    LastExecution = routine.LastExecuted,
                    NextExecution = CalculateNextExecution(routine),
                    IsActive = routine.IsActive,
                    HealthStatus = await GetRoutineHealthStatusAsync(npcId, routine)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get status for routine '{routineId}' of NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to get status for routine '{routineId}' of NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineStatusFailed);
            }
        }

        /// <summary>
        /// Rutin olayına abone olur;
        /// </summary>
        public void SubscribeToRoutineEvents(string npcId, Action<RoutineEvent> handler)
        {
            ValidateNPCId(npcId);
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (_lock)
            {
                if (!_eventSubscriptions.ContainsKey(npcId))
                {
                    _eventSubscriptions[npcId] = new List<RoutineEventSubscription>();
                }

                var subscription = new RoutineEventSubscription;
                {
                    Id = Guid.NewGuid().ToString(),
                    Handler = handler,
                    CreatedAt = DateTime.UtcNow;
                };

                _eventSubscriptions[npcId].Add(subscription);

                _logger.LogDebug($"Event subscription added for NPC '{npcId}'");
            }
        }

        /// <summary>
        /// NPC'nin mevcut aktivitesini alır;
        /// </summary>
        public async Task<CurrentActivity> GetCurrentActivityAsync(string npcId)
        {
            ValidateNPCId(npcId);

            try
            {
                // NPC context'ini kontrol et;
                if (!_npcContexts.ContainsKey(npcId))
                {
                    return new CurrentActivity;
                    {
                        NPCId = npcId,
                        IsActive = false,
                        ActivityName = "Idle",
                        Status = "No routines running"
                    };
                }

                var npcContext = _npcContexts[npcId];

                if (!npcContext.IsRunning || npcContext.CurrentSchedule == null)
                {
                    return new CurrentActivity;
                    {
                        NPCId = npcId,
                        IsActive = false,
                        ActivityName = "Idle",
                        Status = "No active schedule"
                    };
                }

                // Şu anki zamanı kontrol et;
                var currentTime = DateTime.Now.TimeOfDay;

                // Aktif aktiviteyi bul;
                var currentActivity = npcContext.CurrentSchedule.Activities;
                    .Where(a => a.StartTime <= currentTime && a.EndTime >= currentTime)
                    .OrderByDescending(a => a.Priority)
                    .FirstOrDefault();

                if (currentActivity == null)
                {
                    return new CurrentActivity;
                    {
                        NPCId = npcId,
                        IsActive = false,
                        ActivityName = "Break",
                        Status = "Between activities",
                        NextActivity = GetNextActivity(npcContext.CurrentSchedule)
                    };
                }

                // Aktivite progress'ini hesapla;
                var totalDuration = (currentActivity.EndTime - currentActivity.StartTime).TotalMinutes;
                var elapsedDuration = (currentTime - currentActivity.StartTime).TotalMinutes;
                var progress = totalDuration > 0 ? (elapsedDuration / totalDuration) * 100 : 0;

                return new CurrentActivity;
                {
                    NPCId = npcId,
                    IsActive = true,
                    ActivityName = currentActivity.Name,
                    RoutineId = currentActivity.RoutineId,
                    StartTime = currentActivity.StartTime,
                    EndTime = currentActivity.EndTime,
                    Progress = Math.Min(100, Math.Max(0, progress)),
                    Status = "In progress",
                    Location = currentActivity.Location,
                    NextActivity = GetNextActivity(npcContext.CurrentSchedule)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get current activity for NPC '{npcId}'");
                throw new RoutineManagementException(
                    $"Failed to get current activity for NPC '{npcId}'",
                    ex,
                    ErrorCodes.CurrentActivityFailed);
            }
        }

        /// <summary>
        /// Rutin geçmişini alır;
        /// </summary>
        public async Task<RoutineHistory> GetRoutineHistoryAsync(string npcId, DateTime from, DateTime to)
        {
            ValidateNPCId(npcId);

            if (from > to)
                throw new ArgumentException("Start date cannot be after end date");

            try
            {
                // Repository'den geçmişi getir;
                var historyEntries = await _routineRepository.GetRoutineHistoryAsync(npcId, from, to);

                // İstatistikleri hesapla;
                var statistics = CalculateHistoryStatistics(historyEntries);

                return new RoutineHistory;
                {
                    NPCId = npcId,
                    Period = new DateRange(from, to),
                    Entries = historyEntries,
                    Statistics = statistics,
                    TotalExecutions = historyEntries.Count,
                    SuccessRate = statistics.SuccessRate,
                    AverageDuration = statistics.AverageDuration;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get routine history for NPC '{npcId}' from {from} to {to}");
                throw new RoutineManagementException(
                    $"Failed to get routine history for NPC '{npcId}'",
                    ex,
                    ErrorCodes.RoutineHistoryFailed);
            }
        }

        /// <summary>
        /// Dispose pattern implementasyonu;
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
                    // Tüm cancellation token'ları iptal et;
                    lock (_lock)
                    {
                        foreach (var cts in _routineCancellationTokens.Values)
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                        _routineCancellationTokens.Clear();
                    }

                    // Event aboneliklerini temizle;
                    _eventSubscriptions.Clear();

                    // NPC context'lerini temizle;
                    _npcContexts.Clear();

                    _logger.LogInformation("RoutineManager disposed successfully");
                }

                _disposed = true;
            }
        }

        #region Private Helper Methods;

        private void InitializeEventHandlers()
        {
            _eventBus.Subscribe<RoutineCompletedEvent>(async @event =>
            {
                await HandleRoutineCompletedAsync(@event);
            });

            _eventBus.Subscribe<RoutineFailedEvent>(async @event =>
            {
                await HandleRoutineFailedAsync(@event);
            });

            _eventBus.Subscribe<ScheduleUpdatedEvent>(async @event =>
            {
                await HandleScheduleUpdatedAsync(@event);
            });

            _logger.LogDebug("Event handlers initialized for RoutineManager");
        }

        private async Task HandleRoutineCompletedAsync(RoutineCompletedEvent @event)
        {
            try
            {
                // NPC context'ini güncelle;
                if (_npcContexts.ContainsKey(@event.NPCId))
                {
                    _npcContexts[@event.NPCId].LastActivityCompleted = DateTime.UtcNow;
                }

                // Abonelere event'i yayınla;
                if (_eventSubscriptions.ContainsKey(@event.NPCId))
                {
                    foreach (var subscription in _eventSubscriptions[@event.NPCId])
                    {
                        try
                        {
                            subscription.Handler(@event);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error in event handler for NPC '{@event.NPCId}'");
                        }
                    }
                }

                // Long term memory'e kaydet;
                await _longTermMemory.StoreExperienceAsync(
                    new RoutineCompletionExperience(@event.RoutineId, @event.Success, @event.CompletionTime));

                _logger.LogDebug($"Routine '{@event.RoutineId}' completed for NPC '{@event.NPCId}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling routine completion for NPC '{@event.NPCId}'");
            }
        }

        private async Task HandleRoutineFailedAsync(RoutineFailedEvent @event)
        {
            try
            {
                // Failure handling logic;
                _logger.LogWarning($"Routine '{@event.RoutineId}' failed for NPC '{@event.NPCId}': {@event.ErrorMessage}");

                // Abonelere event'i yayınla;
                if (_eventSubscriptions.ContainsKey(@event.NPCId))
                {
                    foreach (var subscription in _eventSubscriptions[@event.NPCId])
                    {
                        try
                        {
                            subscription.Handler(@event);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error in event handler for NPC '{@event.NPCId}'");
                        }
                    }
                }

                // Recovery strategy uygula;
                await ApplyRecoveryStrategyAsync(@event.NPCId, @event.RoutineId, @event.ErrorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling routine failure for NPC '{@event.NPCId}'");
            }
        }

        private async Task HandleScheduleUpdatedAsync(ScheduleUpdatedEvent @event)
        {
            try
            {
                _logger.LogInformation($"Schedule updated for NPC '{@event.NPCId}'");

                // Eğer rutinler çalışıyorsa yeniden başlat;
                if (_npcContexts.ContainsKey(@event.NPCId) &&
                    _npcContexts[@event.NPCId].IsRunning)
                {
                    await StopRoutinesAsync(@event.NPCId);
                    await Task.Delay(1000); // Kısa bekle;
                    await StartRoutinesAsync(@event.NPCId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling schedule update for NPC '{@event.NPCId}'");
            }
        }

        private NPCContext GetOrCreateNPCContext(string npcId)
        {
            lock (_lock)
            {
                if (!_npcContexts.ContainsKey(npcId))
                {
                    _npcContexts[npcId] = new NPCContext;
                    {
                        NPCId = npcId,
                        CreatedAt = DateTime.UtcNow,
                        IsRunning = false;
                    };
                }

                return _npcContexts[npcId];
            }
        }

        private async Task<RoutineExecutionState> GetRoutineCurrentStateAsync(string npcId, Routine routine)
        {
            // Blackboard'dan mevcut durumu kontrol et;
            var stateKey = $"{npcId}:routine_state:{routine.Id}";
            var storedState = await _blackboard.GetValueAsync<RoutineExecutionState>(stateKey);

            if (storedState != null)
            {
                return storedState;
            }

            // Durumu hesapla;
            var currentTime = DateTime.Now.TimeOfDay;

            if (routine.ExecutionTime.StartTime > currentTime)
                return RoutineExecutionState.Scheduled;

            if (routine.ExecutionTime.StartTime <= currentTime && routine.ExecutionTime.EndTime >= currentTime)
                return RoutineExecutionState.Running;

            return RoutineExecutionState.Completed;
        }

        private async Task<double> CalculateRoutineProgressAsync(string npcId, Routine routine)
        {
            var currentState = await GetRoutineCurrentStateAsync(npcId, routine);

            if (currentState != RoutineExecutionState.Running)
                return currentState == RoutineExecutionState.Completed ? 100 : 0;

            var currentTime = DateTime.Now.TimeOfDay;
            var totalDuration = (routine.ExecutionTime.EndTime - routine.ExecutionTime.StartTime).TotalMinutes;
            var elapsedDuration = (currentTime - routine.ExecutionTime.StartTime).TotalMinutes;

            if (totalDuration <= 0)
                return 0;

            return Math.Min(100, (elapsedDuration / totalDuration) * 100);
        }

        private DateTime? CalculateNextExecution(Routine routine)
        {
            if (!routine.IsActive || !routine.Schedule.IsRecurring)
                return null;

            var now = DateTime.Now;
            var today = now.Date;

            // Bugün için başlangıç zamanı;
            var startDateTime = today.Add(routine.ExecutionTime.StartTime);

            if (startDateTime > now)
                return startDateTime;

            // Tekrarlanan rutinler için sonraki gün;
            if (routine.Schedule.RecurrenceType == RecurrenceType.Daily)
                return today.AddDays(1).Add(routine.ExecutionTime.StartTime);

            // Haftalık için sonraki hafta;
            if (routine.Schedule.RecurrenceType == RecurrenceType.Weekly)
                return today.AddDays(7).Add(routine.ExecutionTime.StartTime);

            return null;
        }

        private async Task<RoutineHealthStatus> GetRoutineHealthStatusAsync(string npcId, Routine routine)
        {
            var history = await _routineRepository.GetRoutineExecutionHistoryAsync(npcId, routine.Id, 10);

            if (!history.Any())
                return RoutineHealthStatus.Unknown;

            var successfulExecutions = history.Count(h => h.Success);
            var successRate = (double)successfulExecutions / history.Count * 100;

            if (successRate >= 90)
                return RoutineHealthStatus.Healthy;

            if (successRate >= 70)
                return RoutineHealthStatus.Warning;

            return RoutineHealthStatus.Critical;
        }

        private NextActivity GetNextActivity(SchedulePlan schedule)
        {
            var currentTime = DateTime.Now.TimeOfDay;

            var nextActivity = schedule.Activities;
                .Where(a => a.StartTime > currentTime)
                .OrderBy(a => a.StartTime)
                .FirstOrDefault();

            if (nextActivity == null)
                return null;

            return new NextActivity;
            {
                Name = nextActivity.Name,
                StartTime = nextActivity.StartTime,
                TimeUntilStart = nextActivity.StartTime - currentTime;
            };
        }

        private HistoryStatistics CalculateHistoryStatistics(List<RoutineHistoryEntry> entries)
        {
            if (!entries.Any())
                return new HistoryStatistics();

            var successfulEntries = entries.Where(e => e.Success).ToList();
            var failedEntries = entries.Where(e => !e.Success).ToList();

            return new HistoryStatistics;
            {
                TotalExecutions = entries.Count,
                SuccessfulExecutions = successfulEntries.Count,
                FailedExecutions = failedEntries.Count,
                SuccessRate = (double)successfulEntries.Count / entries.Count * 100,
                AverageDuration = entries.Average(e => e.Duration?.TotalMinutes ?? 0),
                MostFrequentRoutine = entries;
                    .GroupBy(e => e.RoutineId)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?
                    .Key ?? string.Empty;
            };
        }

        private async Task ApplyRecoveryStrategyAsync(string npcId, string routineId, string errorMessage)
        {
            try
            {
                // Rutini getir;
                var routine = await _routineRepository.GetRoutineAsync(npcId, routineId);
                if (routine == null)
                    return;

                // Recovery strategy kontrol et;
                if (routine.RecoveryStrategy != null)
                {
                    switch (routine.RecoveryStrategy.Type)
                    {
                        case RecoveryStrategyType.Retry:
                            await RetryRoutineAsync(npcId, routine);
                            break;

                        case RecoveryStrategyType.Fallback:
                            await ExecuteFallbackActivityAsync(npcId, routine);
                            break;

                        case RecoveryStrategyType.Skip:
                            _logger.LogInformation($"Skipping failed routine '{routineId}' for NPC '{npcId}'");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error applying recovery strategy for routine '{routineId}'");
            }
        }

        private async Task RetryRoutineAsync(string npcId, Routine routine)
        {
            var maxRetries = routine.RecoveryStrategy?.MaxRetries ?? 3;
            var retryDelay = routine.RecoveryStrategy?.RetryDelay ?? TimeSpan.FromSeconds(30);

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    _logger.LogInformation($"Retrying routine '{routine.Name}' for NPC '{npcId}' (attempt {i + 1}/{maxRetries})");

                    await Task.Delay(retryDelay);

                    // Retry logic;
                    // Burada rutin tekrar çalıştırılacak;

                    break; // Başarılı olursa çık;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Retry attempt {i + 1} failed for routine '{routine.Name}'");

                    if (i == maxRetries - 1)
                    {
                        _logger.LogError($"All retry attempts failed for routine '{routine.Name}'");
                    }
                }
            }
        }

        private async Task ExecuteFallbackActivityAsync(string npcId, Routine routine)
        {
            if (routine.RecoveryStrategy?.FallbackActivity == null)
                return;

            try
            {
                _logger.LogInformation($"Executing fallback activity for routine '{routine.Name}'");

                // Fallback activity çalıştır;
                var fallbackActivity = routine.RecoveryStrategy.FallbackActivity;

                // ActivityScheduler ile fallback çalıştır;
                await _activityScheduler.ExecuteActivityAsync(
                    npcId,
                    fallbackActivity,
                    ActivityPriority.High);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing fallback activity for routine '{routine.Name}'");
            }
        }

        private void ValidateNPCId(string npcId)
        {
            if (string.IsNullOrWhiteSpace(npcId))
                throw new ArgumentException("NPC ID cannot be null or empty", nameof(npcId));
        }

        private void ValidateRoutineId(string routineId)
        {
            if (string.IsNullOrWhiteSpace(routineId))
                throw new ArgumentException("Routine ID cannot be null or empty", nameof(routineId));
        }

        private void ValidateRoutine(Routine routine)
        {
            if (routine == null)
                throw new ArgumentNullException(nameof(routine));

            if (string.IsNullOrWhiteSpace(routine.Name))
                throw new ArgumentException("Routine name cannot be null or empty");

            if (routine.ExecutionTime == null)
                throw new ArgumentException("Execution time cannot be null");

            if (routine.ExecutionTime.StartTime >= routine.ExecutionTime.EndTime)
                throw new ArgumentException("Start time must be before end time");
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// NPC rutini;
    /// </summary>
    public class Routine;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Description { get; set; }
        public RoutineType Type { get; set; }
        public bool IsActive { get; set; } = true;
        public int Priority { get; set; } = 1;
        public RoutineExecutionTime ExecutionTime { get; set; }
        public RoutineSchedule Schedule { get; set; }
        public List<RoutineStep> Steps { get; set; } = new List<RoutineStep>();
        public RoutineConditions Conditions { get; set; }
        public RecoveryStrategy RecoveryStrategy { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
        public DateTime? LastExecuted { get; set; }
        public RoutineExecutionState CurrentState { get; set; }
    }

    /// <summary>
    /// Rutin yürütme zamanı;
    /// </summary>
    public class RoutineExecutionTime;
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public TimeSpan EstimatedDuration => EndTime - StartTime;
    }

    /// <summary>
    /// Rutin zamanlaması;
    /// </summary>
    public class RoutineSchedule;
    {
        public bool IsRecurring { get; set; }
        public RecurrenceType RecurrenceType { get; set; }
        public List<DayOfWeek> ActiveDays { get; set; } = new List<DayOfWeek>();
        public List<int> ActiveDates { get; set; } = new List<int>();
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidUntil { get; set; }
    }

    /// <summary>
    /// Rutin adımı;
    /// </summary>
    public class RoutineStep;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Action { get; set; }
        public string Target { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public TimeSpan EstimatedDuration { get; set; }
        public int Order { get; set; }
        public bool IsRequired { get; set; } = true;
        public List<RoutineStep> SubSteps { get; set; } = new List<RoutineStep>();
    }

    /// <summary>
    /// Rutin koşulları;
    /// </summary>
    public class RoutineConditions;
    {
        public List<Condition> Prerequisites { get; set; } = new List<Condition>();
        public List<Condition> SuccessConditions { get; set; } = new List<Condition>();
        public List<Condition> FailureConditions { get; set; } = new List<Condition>();
        public WeatherConditions WeatherRestrictions { get; set; }
        public TimeOfDayConditions TimeRestrictions { get; set; }
        public LocationConditions LocationRestrictions { get; set; }
    }

    /// <summary>
    /// İyileşme stratejisi;
    /// </summary>
    public class RecoveryStrategy;
    {
        public RecoveryStrategyType Type { get; set; }
        public int MaxRetries { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);
        public RoutineStep FallbackActivity { get; set; }
        public string NotificationChannel { get; set; }
    }

    /// <summary>
    /// NPC context'i;
    /// </summary>
    public class NPCContext;
    {
        public string NPCId { get; set; }
        public bool IsRunning { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? StoppedAt { get; set; }
        public DateTime? LastActivityCompleted { get; set; }
        public SchedulePlan CurrentSchedule { get; set; }
        public Dictionary<string, object> State { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Rutin sonucu;
    /// </summary>
    public class RoutineResult;
    {
        public bool Success { get; set; }
        public string RoutineId { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public static RoutineResult Success(string routineId, string message = null)
        {
            return new RoutineResult;
            {
                Success = true,
                RoutineId = routineId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static RoutineResult Failure(string errorMessage)
        {
            return new RoutineResult;
            {
                Success = false,
                Message = errorMessage;
            };
        }
    }

    /// <summary>
    /// Rutin durumu;
    /// </summary>
    public class RoutineStatus;
    {
        public string RoutineId { get; set; }
        public string RoutineName { get; set; }
        public RoutineExecutionState CurrentState { get; set; }
        public double Progress { get; set; }
        public DateTime? LastExecution { get; set; }
        public DateTime? NextExecution { get; set; }
        public bool IsActive { get; set; }
        public RoutineHealthStatus HealthStatus { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Mevcut aktivite;
    /// </summary>
    public class CurrentActivity;
    {
        public string NPCId { get; set; }
        public bool IsActive { get; set; }
        public string ActivityName { get; set; }
        public string RoutineId { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public double Progress { get; set; }
        public string Status { get; set; }
        public string Location { get; set; }
        public NextActivity NextActivity { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Sonraki aktivite;
    /// </summary>
    public class NextActivity;
    {
        public string Name { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan TimeUntilStart { get; set; }
        public string Location { get; set; }
    }

    /// <summary>
    /// Rutin geçmişi;
    /// </summary>
    public class RoutineHistory;
    {
        public string NPCId { get; set; }
        public DateRange Period { get; set; }
        public List<RoutineHistoryEntry> Entries { get; set; } = new List<RoutineHistoryEntry>();
        public HistoryStatistics Statistics { get; set; }
        public int TotalExecutions { get; set; }
        public double SuccessRate { get; set; }
        public double AverageDuration { get; set; }
    }

    /// <summary>
    /// Rutin geçmiş girişi;
    /// </summary>
    public class RoutineHistoryEntry
    {
        public string RoutineId { get; set; }
        public string RoutineName { get; set; }
        public DateTime ExecutionTime { get; set; }
        public TimeSpan? Duration { get; set; }
        public bool Success { get; set; }
        public string Result { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Tarih aralığı;
    /// </summary>
    public class DateRange;
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public DateRange(DateTime start, DateTime end)
        {
            Start = start;
            End = end;
        }
    }

    /// <summary>
    /// Geçmiş istatistikleri;
    /// </summary>
    public class HistoryStatistics;
    {
        public int TotalExecutions { get; set; }
        public int SuccessfulExecutions { get; set; }
        public int FailedExecutions { get; set; }
        public double SuccessRate { get; set; }
        public double AverageDuration { get; set; }
        public string MostFrequentRoutine { get; set; }
    }

    /// <summary>
    /// Rutin deneyimi (Long Term Memory için)
    /// </summary>
    public class RoutineExperience;
    {
        public string RoutineId { get; set; }
        public string RoutineName { get; set; }
        public RoutineActionType ActionType { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();

        public RoutineExperience(Routine routine, RoutineActionType actionType, DateTime timestamp)
        {
            RoutineId = routine.Id;
            RoutineName = routine.Name;
            ActionType = actionType;
            Timestamp = timestamp;
            Context = new Dictionary<string, object>
            {
                ["type"] = routine.Type,
                ["priority"] = routine.Priority;
            };
        }
    }

    /// <summary>
    /// Rutin tamamlama deneyimi;
    /// </summary>
    public class RoutineCompletionExperience;
    {
        public string RoutineId { get; set; }
        public bool Success { get; set; }
        public DateTime CompletionTime { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();

        public RoutineCompletionExperience(string routineId, bool success, DateTime completionTime)
        {
            RoutineId = routineId;
            Success = success;
            CompletionTime = completionTime;
        }
    }

    /// <summary>
    /// Rutin olay aboneliği;
    /// </summary>
    public class RoutineEventSubscription;
    {
        public string Id { get; set; }
        public Action<RoutineEvent> Handler { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Rutin tipi;
    /// </summary>
    public enum RoutineType;
    {
        Daily,
        Work,
        Leisure,
        Social,
        Training,
        Maintenance,
        Emergency,
        Custom;
    }

    /// <summary>
    /// Rutin yürütme durumu;
    /// </summary>
    public enum RoutineExecutionState;
    {
        Scheduled,
        Running,
        Paused,
        Completed,
        Failed,
        Cancelled;
    }

    /// <summary>
    /// Tekrarlama tipi;
    /// </summary>
    public enum RecurrenceType;
    {
        None,
        Daily,
        Weekly,
        Monthly,
        Yearly,
        Custom;
    }

    /// <summary>
    /// Rutin sağlık durumu;
    /// </summary>
    public enum RoutineHealthStatus;
    {
        Unknown,
        Healthy,
        Warning,
        Critical;
    }

    /// <summary>
    /// İyileşme stratejisi tipi;
    /// </summary>
    public enum RecoveryStrategyType;
    {
        Retry,
        Fallback,
        Skip,
        Notify;
    }

    /// <summary>
    /// Rutin aksiyon tipi;
    /// </summary>
    public enum RoutineActionType;
    {
        Added,
        Updated,
        Deleted,
        Executed,
        Completed,
        Failed;
    }

    /// <summary>
    /// Aktivite önceliği;
    /// </summary>
    public enum ActivityPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Rutin olayı base sınıfı;
    /// </summary>
    public abstract class RoutineEvent : IEvent;
    {
        public string NPCId { get; set; }
        public string RoutineId { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Rutin eklendi olayı;
    /// </summary>
    public class RoutineAddedEvent : RoutineEvent;
    {
        public string RoutineName { get; set; }
        public RoutineType RoutineType { get; set; }
    }

    /// <summary>
    /// Rutin güncellendi olayı;
    /// </summary>
    public class RoutineUpdatedEvent : RoutineEvent;
    {
        public string RoutineName { get; set; }
    }

    /// <summary>
    /// Rutin silindi olayı;
    /// </summary>
    public class RoutineRemovedEvent : RoutineEvent;
    {
        public string RoutineName { get; set; }
    }

    /// <summary>
    /// Rutin tamamlandı olayı;
    /// </summary>
    public class RoutineCompletedEvent : RoutineEvent;
    {
        public bool Success { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime CompletionTime { get; set; }
        public Dictionary<string, object> Results { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Rutin başarısız oldu olayı;
    /// </summary>
    public class RoutineFailedEvent : RoutineEvent;
    {
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Rutinler başladı olayı;
    /// </summary>
    public class RoutinesStartedEvent : RoutineEvent;
    {
        public int RoutineCount { get; set; }
    }

    /// <summary>
    /// Rutinler durduruldu olayı;
    /// </summary>
    public class RoutinesStoppedEvent : RoutineEvent;
    {
    }

    /// <summary>
    /// Zamanlama oluşturuldu olayı;
    /// </summary>
    public class ScheduleGeneratedEvent : RoutineEvent;
    {
        public DateTime ScheduleDate { get; set; }
        public int ActivityCount { get; set; }
    }

    /// <summary>
    /// Zamanlama güncellendi olayı;
    /// </summary>
    public class ScheduleUpdatedEvent : RoutineEvent;
    {
        public DateTime ScheduleDate { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Rutin yönetim istisnası;
    /// </summary>
    public class RoutineManagementException : Exception
    {
        public string ErrorCode { get; }
        public DateTime Timestamp { get; }

        public RoutineManagementException(string message, Exception innerException, string errorCode)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Timestamp = DateTime.UtcNow;
        }

        public RoutineManagementException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Rutin bulunamadı istisnası;
    /// </summary>
    public class RoutineNotFoundException : RoutineManagementException;
    {
        public RoutineNotFoundException(string message)
            : base(message, ErrorCodes.RoutineNotFound)
        {
        }
    }

    #endregion;

    #region Internal Classes;

    /// <summary>
    /// Rutin repository;
    /// </summary>
    internal class RoutineRepository;
    {
        private readonly Dictionary<string, Dictionary<string, Routine>> _npcRoutines;
        private readonly Dictionary<string, List<RoutineHistoryEntry>> _routineHistory;

        public RoutineRepository()
        {
            _npcRoutines = new Dictionary<string, Dictionary<string, Routine>>();
            _routineHistory = new Dictionary<string, List<RoutineHistoryEntry>>();
        }

        public Task AddRoutineAsync(string npcId, Routine routine)
        {
            if (!_npcRoutines.ContainsKey(npcId))
                _npcRoutines[npcId] = new Dictionary<string, Routine>();

            _npcRoutines[npcId][routine.Id] = routine;
            return Task.CompletedTask;
        }

        public Task<Routine> GetRoutineAsync(string npcId, string routineId)
        {
            if (_npcRoutines.ContainsKey(npcId) && _npcRoutines[npcId].ContainsKey(routineId))
                return Task.FromResult(_npcRoutines[npcId][routineId]);

            return Task.FromResult<Routine>(null);
        }

        public Task<IEnumerable<Routine>> GetRoutinesAsync(string npcId)
        {
            if (_npcRoutines.ContainsKey(npcId))
                return Task.FromResult(_npcRoutines[npcId].Values.AsEnumerable());

            return Task.FromResult(Enumerable.Empty<Routine>());
        }

        public Task UpdateRoutineAsync(string npcId, string routineId, Routine routine)
        {
            if (_npcRoutines.ContainsKey(npcId) && _npcRoutines[npcId].ContainsKey(routineId))
                _npcRoutines[npcId][routineId] = routine;

            return Task.CompletedTask;
        }

        public Task RemoveRoutineAsync(string npcId, string routineId)
        {
            if (_npcRoutines.ContainsKey(npcId) && _npcRoutines[npcId].ContainsKey(routineId))
                _npcRoutines[npcId].Remove(routineId);

            return Task.CompletedTask;
        }

        public Task<List<RoutineHistoryEntry>> GetRoutineHistoryAsync(string npcId, DateTime from, DateTime to)
        {
            var key = GetHistoryKey(npcId);
            if (_routineHistory.ContainsKey(key))
            {
                var entries = _routineHistory[key]
                    .Where(e => e.ExecutionTime >= from && e.ExecutionTime <= to)
                    .ToList();

                return Task.FromResult(entries);
            }

            return Task.FromResult(new List<RoutineHistoryEntry>());
        }

        public Task<List<RoutineHistoryEntry>> GetRoutineExecutionHistoryAsync(string npcId, string routineId, int count)
        {
            var key = GetHistoryKey(npcId);
            if (_routineHistory.ContainsKey(key))
            {
                var entries = _routineHistory[key]
                    .Where(e => e.RoutineId == routineId)
                    .OrderByDescending(e => e.ExecutionTime)
                    .Take(count)
                    .ToList();

                return Task.FromResult(entries);
            }

            return Task.FromResult(new List<RoutineHistoryEntry>());
        }

        private string GetHistoryKey(string npcId) => $"history_{npcId}";
    }

    /// <summary>
    /// Rutin executor;
    /// </summary>
    internal class RoutineExecutor;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;

        public RoutineExecutor(ILogger logger, IEventBus eventBus)
        {
            _logger = logger;
            _eventBus = eventBus;
        }

        public async Task ExecuteRoutineAsync(
            string npcId,
            Routine routine,
            NPCContext context,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogInformation($"Starting routine '{routine.Name}' for NPC '{npcId}'");

                // Ön koşulları kontrol et;
                if (!await CheckPrerequisitesAsync(routine, context))
                {
                    _logger.LogWarning($"Prerequisites not met for routine '{routine.Name}'");
                    return;
                }

                // Her adımı sırayla çalıştır;
                foreach (var step in routine.Steps.OrderBy(s => s.Order))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await ExecuteStepAsync(npcId, routine, step, context, cancellationToken);
                }

                var duration = DateTime.UtcNow - startTime;

                // Başarılı tamamlanma;
                await _eventBus.PublishAsync(new RoutineCompletedEvent;
                {
                    NPCId = npcId,
                    RoutineId = routine.Id,
                    RoutineName = routine.Name,
                    Success = true,
                    Duration = duration,
                    CompletionTime = DateTime.UtcNow;
                });

                _logger.LogInformation($"Routine '{routine.Name}' completed successfully in {duration.TotalSeconds:F2} seconds");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"Routine '{routine.Name}' was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;

                _logger.LogError(ex, $"Routine '{routine.Name}' failed after {duration.TotalSeconds:F2} seconds");

                // Hata olayı yayınla;
                await _eventBus.PublishAsync(new RoutineFailedEvent;
                {
                    NPCId = npcId,
                    RoutineId = routine.Id,
                    RoutineName = routine.Name,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                throw;
            }
        }

        private async Task<bool> CheckPrerequisitesAsync(Routine routine, NPCContext context)
        {
            if (routine.Conditions?.Prerequisites == null ||
                !routine.Conditions.Prerequisites.Any())
                return true;

            // Prerequisite kontrolü implementasyonu;
            // Burada koşullar kontrol edilecek;
            return true;
        }

        private async Task ExecuteStepAsync(
            string npcId,
            Routine routine,
            RoutineStep step,
            NPCContext context,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Executing step '{step.Name}' for routine '{routine.Name}'");

            try
            {
                // Step execution logic;
                // Burada gerçek adım yürütme işlemi yapılacak;

                await Task.Delay(step.EstimatedDuration, cancellationToken);

                _logger.LogDebug($"Step '{step.Name}' completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Step '{step.Name}' failed");
                throw;
            }
        }
    }

    /// <summary>
    /// Rutin validator;
    /// </summary>
    internal class RoutineValidator;
    {
        public async Task<ValidationResult> ValidateAsync(Routine routine)
        {
            var errors = new List<string>();

            // Name validation;
            if (string.IsNullOrWhiteSpace(routine.Name))
                errors.Add("Routine name cannot be empty");

            if (routine.Name.Length > 100)
                errors.Add("Routine name cannot exceed 100 characters");

            // Time validation;
            if (routine.ExecutionTime == null)
                errors.Add("Execution time is required");
            else;
            {
                if (routine.ExecutionTime.StartTime >= routine.ExecutionTime.EndTime)
                    errors.Add("Start time must be before end time");

                if (routine.ExecutionTime.EstimatedDuration.TotalHours > 24)
                    errors.Add("Routine duration cannot exceed 24 hours");
            }

            // Priority validation;
            if (routine.Priority < 1 || routine.Priority > 10)
                errors.Add("Priority must be between 1 and 10");

            // Steps validation;
            if (routine.Steps == null || !routine.Steps.Any())
                errors.Add("Routine must have at least one step");
            else;
            {
                foreach (var step in routine.Steps)
                {
                    if (string.IsNullOrWhiteSpace(step.Name))
                        errors.Add("Step name cannot be empty");

                    if (string.IsNullOrWhiteSpace(step.Action))
                        errors.Add("Step action cannot be empty");
                }
            }

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                ErrorMessage = errors.Any() ? string.Join("; ", errors) : null;
            };
        }
    }

    /// <summary>
    /// Validasyon sonucu;
    /// </summary>
    internal class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    #endregion;

    #region Error Codes;

    /// <summary>
    /// Hata kodları;
    /// </summary>
    internal static class ErrorCodes;
    {
        public const string RoutineAddFailed = "ROUTINE_ADD_FAILED";
        public const string RoutineRetrieveFailed = "ROUTINE_RETRIEVE_FAILED";
        public const string RoutineUpdateFailed = "ROUTINE_UPDATE_FAILED";
        public const string RoutineDeleteFailed = "ROUTINE_DELETE_FAILED";
        public const string ScheduleGenerationFailed = "SCHEDULE_GENERATION_FAILED";
        public const string RoutineStartFailed = "ROUTINE_START_FAILED";
        public const string RoutineStopFailed = "ROUTINE_STOP_FAILED";
        public const string RoutineStatusFailed = "ROUTINE_STATUS_FAILED";
        public const string CurrentActivityFailed = "CURRENT_ACTIVITY_FAILED";
        public const string RoutineHistoryFailed = "ROUTINE_HISTORY_FAILED";
        public const string RoutineNotFound = "ROUTINE_NOT_FOUND";
        public const string InvalidRoutine = "INVALID_ROUTINE";
        public const string RoutineExecutionFailed = "ROUTINE_EXECUTION_FAILED";
    }

    #endregion;
}
