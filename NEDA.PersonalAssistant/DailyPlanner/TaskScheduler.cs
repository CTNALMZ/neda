using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Services.EventBus;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Automation.TaskRouter;

namespace NEDA.PersonalAssistant.DailyPlanner;
{
    /// <summary>
    /// Görev zamanlayıcı motoru arayüzü.
    /// </summary>
    public interface ITaskScheduler : IDisposable
    {
        /// <summary>
        /// Yeni bir görev planlar.
        /// </summary>
        /// <param name="task">Planlanacak görev.</param>
        /// <returns>Planlama sonucu.</returns>
        Task<SchedulingResult> ScheduleTaskAsync(ScheduledTask task);

        /// <summary>
        /// Varolan bir görevi günceller.
        /// </summary>
        /// <param name="taskId">Görev ID.</param>
        /// <param name="updates">Güncellemeler.</param>
        /// <returns>Güncelleme sonucu.</returns>
        Task<UpdateResult> UpdateTaskAsync(Guid taskId, TaskUpdate updates);

        /// <summary>
        /// Planlanmış bir görevi iptal eder.
        /// </summary>
        /// <param name="taskId">Görev ID.</param>
        /// <param name="reason">İptal nedeni.</param>
        /// <returns>İptal sonucu.</returns>
        Task<CancellationResult> CancelTaskAsync(Guid taskId, string reason = null);

        /// <summary>
        /// Tüm aktif görevleri getirir.
        /// </summary>
        /// <returns>Aktif görevler listesi.</returns>
        IReadOnlyList<ScheduledTask> GetActiveTasks();

        /// <summary>
        /// Kullanıcının görevlerini getirir.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        /// <param name="status">Görev durumu filtresi.</param>
        /// <returns>Kullanıcı görevleri.</returns>
        IReadOnlyList<ScheduledTask> GetUserTasks(Guid userId, TaskStatus? status = null);

        /// <summary>
        /// Yaklaşan görevleri getirir.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        /// <param name="timeWindow">Zaman penceresi.</param>
        /// <returns>Yaklaşan görevler.</returns>
        IReadOnlyList<ScheduledTask> GetUpcomingTasks(Guid userId, TimeSpan timeWindow);

        /// <summary>
        /// Görev bağımlılıklarını kontrol eder.
        /// </summary>
        /// <param name="taskId">Görev ID.</param>
        /// <returns>Bağımlılık durumu.</returns>
        Task<DependencyStatus> CheckDependenciesAsync(Guid taskId);

        /// <summary>
        /// Zaman çizelgesini optimize eder.
        /// </summary>
        /// <param name="userId">Kullanıcı ID.</param>
        /// <param name="optimizationStrategy">Optimizasyon stratejisi.</param>
        /// <returns>Optimizasyon sonucu.</returns>
        Task<OptimizationResult> OptimizeScheduleAsync(Guid userId, OptimizationStrategy optimizationStrategy);

        /// <summary>
        /// Görev çakışmalarını kontrol eder.
        /// </summary>
        /// <param name="task">Kontrol edilecek görev.</param>
        /// <returns>Çakışma listesi.</returns>
        IReadOnlyList<TaskConflict> CheckForConflicts(ScheduledTask task);

        /// <summary>
        /// Zamanlayıcı durumunu getirir.
        /// </summary>
        SchedulerStatus GetStatus();

        /// <summary>
        /// Zamanlayıcıyı başlatır.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Zamanlayıcıyı durdurur.
        /// </summary>
        Task StopAsync();
    }

    /// <summary>
    /// Görev zamanlayıcı ana sınıfı.
    /// </summary>
    public class TaskScheduler : ITaskScheduler;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly ITaskDispatcher _taskDispatcher;
        private readonly IPriorityManager _priorityManager;

        private readonly TaskStore _taskStore;
        private readonly SchedulerEngine _schedulerEngine;
        private readonly ConflictDetector _conflictDetector;
        private readonly OptimizationEngine _optimizationEngine;
        private readonly NotificationManager _notificationManager;

        private readonly ConcurrentDictionary<Guid, ScheduledTask> _activeTasks;
        private readonly ConcurrentDictionary<Guid, Timer> _taskTimers;
        private readonly CancellationTokenSource _shutdownTokenSource;
        private readonly SemaphoreSlim _processingLock;
        private readonly Timer _maintenanceTimer;

        private bool _disposed;
        private bool _isRunning;
        private DateTime _startTime;

        /// <summary>
        /// Görev zamanlayıcı oluşturur.
        /// </summary>
        public TaskScheduler(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IEventBus eventBus,
            ITaskDispatcher taskDispatcher,
            IPriorityManager priorityManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _taskDispatcher = taskDispatcher ?? throw new ArgumentNullException(nameof(taskDispatcher));
            _priorityManager = priorityManager ?? throw new ArgumentNullException(nameof(priorityManager));

            _taskStore = new TaskStore();
            _schedulerEngine = new SchedulerEngine();
            _conflictDetector = new ConflictDetector();
            _optimizationEngine = new OptimizationEngine();
            _notificationManager = new NotificationManager();

            _activeTasks = new ConcurrentDictionary<Guid, ScheduledTask>();
            _taskTimers = new ConcurrentDictionary<Guid, Timer>();
            _shutdownTokenSource = new CancellationTokenSource();
            _processingLock = new SemaphoreSlim(1, 1);

            // Bakım timer'ı: her 30 dakikada bir;
            _maintenanceTimer = new Timer(PerformMaintenance, null,
                TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

            _eventBus.Subscribe<TaskCompletedEvent>(HandleTaskCompleted);
            _eventBus.Subscribe<TaskFailedEvent>(HandleTaskFailed);
            _eventBus.Subscribe<PriorityChangedEvent>(HandlePriorityChanged);

            _logger.LogInformation("TaskScheduler initialized");
        }

        /// <summary>
        /// Yeni bir görev planlar.
        /// </summary>
        public async Task<SchedulingResult> ScheduleTaskAsync(ScheduledTask task)
        {
            ValidateTask(task);

            await _processingLock.WaitAsync();
            try
            {
                // Çakışma kontrolü;
                var conflicts = CheckForConflicts(task);
                if (conflicts.Any())
                {
                    return new SchedulingResult;
                    {
                        Success = false,
                        TaskId = task.Id,
                        Message = $"Task conflicts with {conflicts.Count} existing tasks",
                        Conflicts = conflicts;
                    };
                }

                // Bağımlılık kontrolü;
                var dependencyStatus = await CheckDependenciesAsync(task.Id);
                if (dependencyStatus.Status != DependencyStatusType.Ready)
                {
                    return new SchedulingResult;
                    {
                        Success = false,
                        TaskId = task.Id,
                        Message = $"Task dependencies not ready: {dependencyStatus.Status}",
                        DependencyStatus = dependencyStatus;
                    };
                }

                // Öncelik hesapla;
                var priority = await _priorityManager.CalculatePriorityAsync(task);
                task.Priority = priority;

                // Zamanlama yap;
                var scheduleResult = _schedulerEngine.ScheduleTask(task);
                if (!scheduleResult.Success)
                {
                    return new SchedulingResult;
                    {
                        Success = false,
                        TaskId = task.Id,
                        Message = scheduleResult.Message;
                    };
                }

                // Görevi sakla;
                _taskStore.AddTask(task);
                _activeTasks[task.Id] = task;

                // Zamanlayıcı oluştur;
                SetupTaskTimer(task);

                // Event yayınla;
                var @event = new TaskScheduledEvent;
                {
                    TaskId = task.Id,
                    UserId = task.UserId,
                    ScheduledTime = task.ScheduledTime,
                    Priority = task.Priority,
                    TaskType = task.Type,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("tasks.scheduled", 1);
                _logger.LogInformation($"Task scheduled: {task.Id} for user {task.UserId}");

                return new SchedulingResult;
                {
                    Success = true,
                    TaskId = task.Id,
                    ScheduledTime = task.ScheduledTime,
                    Priority = task.Priority,
                    Message = "Task successfully scheduled"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to schedule task {task.Id}");
                _metricsCollector.RecordMetric("tasks.scheduling.error", 1);

                return new SchedulingResult;
                {
                    Success = false,
                    TaskId = task.Id,
                    Message = $"Scheduling failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Varolan bir görevi günceller.
        /// </summary>
        public async Task<UpdateResult> UpdateTaskAsync(Guid taskId, TaskUpdate updates)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeTasks.TryGetValue(taskId, out var task))
                {
                    return new UpdateResult;
                    {
                        Success = false,
                        TaskId = taskId,
                        Message = "Task not found"
                    };
                }

                var originalTask = task.Clone();

                // Güncellemeleri uygula;
                ApplyUpdates(task, updates);

                // Çakışma kontrolü (yeniden)
                var conflicts = CheckForConflicts(task);
                if (conflicts.Any())
                {
                    return new UpdateResult;
                    {
                        Success = false,
                        TaskId = taskId,
                        Message = $"Update creates conflicts with {conflicts.Count} tasks",
                        Conflicts = conflicts;
                    };
                }

                // Zamanlayıcıyı güncelle;
                UpdateTaskTimer(taskId, task);

                // Görevi saklama güncelle;
                _taskStore.UpdateTask(task);
                _activeTasks[taskId] = task;

                // Event yayınla;
                var @event = new TaskUpdatedEvent;
                {
                    TaskId = taskId,
                    UserId = task.UserId,
                    OriginalTask = originalTask,
                    UpdatedTask = task,
                    UpdateType = updates.UpdateType,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("tasks.updated", 1);
                _logger.LogInformation($"Task updated: {taskId}");

                return new UpdateResult;
                {
                    Success = true,
                    TaskId = taskId,
                    Message = "Task successfully updated"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update task {taskId}");
                _metricsCollector.RecordMetric("tasks.update.error", 1);

                return new UpdateResult;
                {
                    Success = false,
                    TaskId = taskId,
                    Message = $"Update failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Planlanmış bir görevi iptal eder.
        /// </summary>
        public async Task<CancellationResult> CancelTaskAsync(Guid taskId, string reason = null)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeTasks.TryRemove(taskId, out var task))
                {
                    return new CancellationResult;
                    {
                        Success = false,
                        TaskId = taskId,
                        Message = "Task not found"
                    };
                }

                // Zamanlayıcıyı temizle;
                if (_taskTimers.TryRemove(taskId, out var timer))
                {
                    timer.Dispose();
                }

                // Görevi saklamadan kaldır;
                _taskStore.RemoveTask(taskId);

                // Bağımlı görevleri kontrol et;
                var dependentTasks = _taskStore.GetDependentTasks(taskId);
                foreach (var dependentTask in dependentTasks)
                {
                    dependentTask.Status = TaskStatus.Cancelled;
                    _logger.LogWarning($"Dependent task {dependentTask.Id} cancelled due to parent task {taskId} cancellation");
                }

                // Event yayınla;
                var @event = new TaskCancelledEvent;
                {
                    TaskId = taskId,
                    UserId = task.UserId,
                    TaskType = task.Type,
                    Reason = reason,
                    DependentTasksAffected = dependentTasks.Count,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("tasks.cancelled", 1);
                _logger.LogInformation($"Task cancelled: {taskId}, reason: {reason ?? "No reason provided"}");

                return new CancellationResult;
                {
                    Success = true,
                    TaskId = taskId,
                    Message = "Task successfully cancelled",
                    DependentTasksAffected = dependentTasks.Count;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to cancel task {taskId}");
                _metricsCollector.RecordMetric("tasks.cancellation.error", 1);

                return new CancellationResult;
                {
                    Success = false,
                    TaskId = taskId,
                    Message = $"Cancellation failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Tüm aktif görevleri getirir.
        /// </summary>
        public IReadOnlyList<ScheduledTask> GetActiveTasks()
        {
            return _activeTasks.Values;
                .Where(t => t.Status == TaskStatus.Scheduled || t.Status == TaskStatus.Pending)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Kullanıcının görevlerini getirir.
        /// </summary>
        public IReadOnlyList<ScheduledTask> GetUserTasks(Guid userId, TaskStatus? status = null)
        {
            var query = _activeTasks.Values.Where(t => t.UserId == userId);

            if (status.HasValue)
            {
                query = query.Where(t => t.Status == status.Value);
            }

            return query;
                .OrderBy(t => t.ScheduledTime)
                .ThenByDescending(t => t.Priority)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Yaklaşan görevleri getirir.
        /// </summary>
        public IReadOnlyList<ScheduledTask> GetUpcomingTasks(Guid userId, TimeSpan timeWindow)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.Add(timeWindow);

            return _activeTasks.Values;
                .Where(t => t.UserId == userId &&
                           t.Status == TaskStatus.Scheduled &&
                           t.ScheduledTime >= now &&
                           t.ScheduledTime <= cutoff)
                .OrderBy(t => t.ScheduledTime)
                .ThenByDescending(t => t.Priority)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Görev bağımlılıklarını kontrol eder.
        /// </summary>
        public async Task<DependencyStatus> CheckDependenciesAsync(Guid taskId)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task))
            {
                return new DependencyStatus;
                {
                    TaskId = taskId,
                    Status = DependencyStatusType.NotFound,
                    Message = "Task not found"
                };
            }

            var dependencies = task.Dependencies ?? new List<Guid>();
            if (!dependencies.Any())
            {
                return new DependencyStatus;
                {
                    TaskId = taskId,
                    Status = DependencyStatusType.Ready,
                    Message = "No dependencies"
                };
            }

            var results = new List<DependencyCheckResult>();
            var allReady = true;

            foreach (var dependencyId in dependencies)
            {
                if (!_activeTasks.TryGetValue(dependencyId, out var dependency))
                {
                    results.Add(new DependencyCheckResult;
                    {
                        DependencyId = dependencyId,
                        Status = TaskStatus.NotFound,
                        Message = "Dependency task not found"
                    });
                    allReady = false;
                    continue;
                }

                var dependencyStatus = dependency.Status;
                var isReady = dependencyStatus == TaskStatus.Completed ||
                             dependencyStatus == TaskStatus.Skipped;

                results.Add(new DependencyCheckResult;
                {
                    DependencyId = dependencyId,
                    Status = dependencyStatus,
                    IsReady = isReady,
                    Message = $"Dependency status: {dependencyStatus}"
                });

                if (!isReady)
                {
                    allReady = false;
                }
            }

            return new DependencyStatus;
            {
                TaskId = taskId,
                Status = allReady ? DependencyStatusType.Ready : DependencyStatusType.Waiting,
                Dependencies = results,
                ReadyCount = results.Count(r => r.IsReady),
                TotalCount = results.Count,
                Message = allReady ? "All dependencies ready" : "Some dependencies not ready"
            };
        }

        /// <summary>
        /// Zaman çizelgesini optimize eder.
        /// </summary>
        public async Task<OptimizationResult> OptimizeScheduleAsync(Guid userId, OptimizationStrategy optimizationStrategy)
        {
            await _processingLock.WaitAsync();
            try
            {
                var userTasks = GetUserTasks(userId, TaskStatus.Scheduled).ToList();
                if (!userTasks.Any())
                {
                    return new OptimizationResult;
                    {
                        Success = false,
                        UserId = userId,
                        Message = "No scheduled tasks to optimize"
                    };
                }

                var optimizationResult = await _optimizationEngine.OptimizeAsync(userTasks, optimizationStrategy);

                if (!optimizationResult.Success)
                {
                    return optimizationResult;
                }

                // Optimize edilmiş görevleri uygula;
                foreach (var optimizedTask in optimizationResult.OptimizedTasks)
                {
                    if (_activeTasks.TryGetValue(optimizedTask.Id, out var existingTask))
                    {
                        existingTask.ScheduledTime = optimizedTask.ScheduledTime;
                        existingTask.Priority = optimizedTask.Priority;
                        UpdateTaskTimer(existingTask.Id, existingTask);
                    }
                }

                // Event yayınla;
                var @event = new ScheduleOptimizedEvent;
                {
                    UserId = userId,
                    Strategy = optimizationStrategy,
                    OriginalTaskCount = userTasks.Count,
                    OptimizedTaskCount = optimizationResult.OptimizedTasks.Count,
                    TotalTimeSaved = optimizationResult.TotalTimeSaved,
                    EfficiencyImprovement = optimizationResult.EfficiencyImprovement,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _metricsCollector.RecordMetric("schedule.optimized", 1);
                _logger.LogInformation($"Schedule optimized for user {userId}, {optimizationResult.OptimizedTasks.Count} tasks optimized");

                return optimizationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to optimize schedule for user {userId}");
                _metricsCollector.RecordMetric("schedule.optimization.error", 1);

                return new OptimizationResult;
                {
                    Success = false,
                    UserId = userId,
                    Message = $"Optimization failed: {ex.Message}"
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Görev çakışmalarını kontrol eder.
        /// </summary>
        public IReadOnlyList<TaskConflict> CheckForConflicts(ScheduledTask task)
        {
            return _conflictDetector.DetectConflicts(task, _activeTasks.Values);
        }

        /// <summary>
        /// Zamanlayıcı durumunu getirir.
        /// </summary>
        public SchedulerStatus GetStatus()
        {
            var activeTaskCount = _activeTasks.Count;
            var completedToday = _taskStore.GetCompletedTasksCount(DateTime.UtcNow.Date);
            var failedToday = _taskStore.GetFailedTasksCount(DateTime.UtcNow.Date);

            var upcomingTasks = _activeTasks.Values;
                .Where(t => t.Status == TaskStatus.Scheduled &&
                           t.ScheduledTime > DateTime.UtcNow)
                .OrderBy(t => t.ScheduledTime)
                .Take(10)
                .ToList();

            return new SchedulerStatus;
            {
                IsRunning = _isRunning,
                StartTime = _startTime,
                Uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                ActiveTaskCount = activeTaskCount,
                CompletedTasksToday = completedToday,
                FailedTasksToday = failedToday,
                UpcomingTasks = upcomingTasks,
                AverageProcessingTime = _taskStore.GetAverageProcessingTime(),
                SuccessRate = _taskStore.GetSuccessRate()
            };
        }

        /// <summary>
        /// Zamanlayıcıyı başlatır.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning) return;

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                // Bekleyen görevleri yükle;
                await LoadPendingTasksAsync(cancellationToken);

                // Zamanlayıcı motorunu başlat;
                _schedulerEngine.Start();

                _isRunning = true;
                _startTime = DateTime.UtcNow;

                _logger.LogInformation("TaskScheduler started");

                var @event = new SchedulerStartedEvent;
                {
                    StartTime = _startTime,
                    LoadedTaskCount = _activeTasks.Count,
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
        /// Zamanlayıcıyı durdurur.
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            await _processingLock.WaitAsync();
            try
            {
                // Tüm zamanlayıcıları durdur;
                foreach (var timer in _taskTimers.Values)
                {
                    timer.Dispose();
                }
                _taskTimers.Clear();

                // Zamanlayıcı motorunu durdur;
                _schedulerEngine.Stop();

                _isRunning = false;

                _logger.LogInformation("TaskScheduler stopped");

                var @event = new SchedulerStoppedEvent;
                {
                    StopTime = DateTime.UtcNow,
                    Uptime = DateTime.UtcNow - _startTime,
                    ActiveTaskCount = _activeTasks.Count,
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
                    _processingLock?.Dispose();
                    _shutdownTokenSource?.Dispose();

                    foreach (var timer in _taskTimers.Values)
                    {
                        timer?.Dispose();
                    }

                    _taskTimers.Clear();

                    if (_eventBus != null)
                    {
                        _eventBus.Unsubscribe<TaskCompletedEvent>(HandleTaskCompleted);
                        _eventBus.Unsubscribe<TaskFailedEvent>(HandleTaskFailed);
                        _eventBus.Unsubscribe<PriorityChangedEvent>(HandlePriorityChanged);
                    }

                    _taskStore.Dispose();
                }

                _disposed = true;
            }
        }

        #region Private Methods;

        private void ValidateTask(ScheduledTask task)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (task.Id == Guid.Empty)
                task.Id = Guid.NewGuid();

            if (task.UserId == Guid.Empty)
                throw new ArgumentException("UserId cannot be empty", nameof(task.UserId));

            if (string.IsNullOrWhiteSpace(task.Title))
                throw new ArgumentException("Title cannot be null or empty", nameof(task.Title));

            if (task.ScheduledTime < DateTime.UtcNow && !task.AllowPastScheduling)
                throw new ArgumentException("ScheduledTime cannot be in the past", nameof(task.ScheduledTime));

            if (task.Duration <= TimeSpan.Zero)
                throw new ArgumentException("Duration must be positive", nameof(task.Duration));
        }

        private void SetupTaskTimer(ScheduledTask task)
        {
            var now = DateTime.UtcNow;
            var delay = task.ScheduledTime - now;

            if (delay <= TimeSpan.Zero)
            {
                // Görev zamanı geçmiş, hemen çalıştır;
                Task.Run(() => ExecuteTaskAsync(task.Id));
                return;
            }

            var timer = new Timer(
                _ => ExecuteTaskAsync(task.Id).Wait(),
                null,
                delay,
                Timeout.InfiniteTimeSpan);

            _taskTimers[task.Id] = timer;
        }

        private void UpdateTaskTimer(Guid taskId, ScheduledTask task)
        {
            if (_taskTimers.TryRemove(taskId, out var oldTimer))
            {
                oldTimer.Dispose();
            }

            SetupTaskTimer(task);
        }

        private async Task ExecuteTaskAsync(Guid taskId)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeTasks.TryGetValue(taskId, out var task))
                {
                    _logger.LogWarning($"Task {taskId} not found for execution");
                    return;
                }

                // Bağımlılık kontrolü;
                var dependencyStatus = await CheckDependenciesAsync(taskId);
                if (dependencyStatus.Status != DependencyStatusType.Ready)
                {
                    task.Status = TaskStatus.WaitingForDependencies;
                    _logger.LogInformation($"Task {taskId} waiting for dependencies");
                    return;
                }

                task.Status = TaskStatus.Running;
                task.ActualStartTime = DateTime.UtcNow;

                // Event yayınla;
                var startedEvent = new TaskStartedEvent;
                {
                    TaskId = taskId,
                    UserId = task.UserId,
                    StartTime = task.ActualStartTime.Value,
                    Priority = task.Priority,
                    TaskType = task.Type,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(startedEvent);

                _logger.LogInformation($"Task {taskId} execution started");

                // Görevi dağıtıcıya gönder;
                var dispatchResult = await _taskDispatcher.DispatchTaskAsync(task);

                // Sonuçları işle;
                await ProcessTaskResultAsync(taskId, dispatchResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing task {taskId}");
                await MarkTaskAsFailedAsync(taskId, $"Execution error: {ex.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        private async Task ProcessTaskResultAsync(Guid taskId, DispatchResult dispatchResult)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task))
                return;

            task.ActualEndTime = DateTime.UtcNow;
            task.ProcessingTime = task.ActualEndTime - task.ActualStartTime;

            if (dispatchResult.Success)
            {
                task.Status = TaskStatus.Completed;
                task.Result = dispatchResult.Result;

                var completedEvent = new TaskCompletedEvent;
                {
                    TaskId = taskId,
                    UserId = task.UserId,
                    StartTime = task.ActualStartTime.Value,
                    EndTime = task.ActualEndTime.Value,
                    ProcessingTime = task.ProcessingTime.Value,
                    Result = task.Result,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(completedEvent);

                _metricsCollector.RecordMetric("tasks.completed", 1);
                _metricsCollector.RecordMetric("tasks.processing.time", task.ProcessingTime.Value.TotalMilliseconds);
                _logger.LogInformation($"Task {taskId} completed in {task.ProcessingTime.Value.TotalMilliseconds}ms");
            }
            else;
            {
                await MarkTaskAsFailedAsync(taskId, dispatchResult.ErrorMessage);
            }

            // Zamanlayıcıyı temizle;
            if (_taskTimers.TryRemove(taskId, out var timer))
            {
                timer.Dispose();
            }

            // Tekrarlayan görev kontrolü;
            if (task.IsRecurring && task.RecurrencePattern != null && dispatchResult.Success)
            {
                await ScheduleNextRecurrenceAsync(task);
            }

            // Bağımlı görevleri kontrol et;
            await CheckDependentTasksAsync(taskId);
        }

        private async Task MarkTaskAsFailedAsync(Guid taskId, string errorMessage)
        {
            if (!_activeTasks.TryGetValue(taskId, out var task))
                return;

            task.Status = TaskStatus.Failed;
            task.ErrorMessage = errorMessage;
            task.ActualEndTime = DateTime.UtcNow;

            if (task.ActualStartTime.HasValue)
            {
                task.ProcessingTime = task.ActualEndTime - task.ActualStartTime;
            }

            var failedEvent = new TaskFailedEvent;
            {
                TaskId = taskId,
                UserId = task.UserId,
                ErrorMessage = errorMessage,
                AttemptCount = task.AttemptCount,
                MaxAttempts = task.MaxAttempts,
                Timestamp = DateTime.UtcNow;
            };

            await _eventBus.PublishAsync(failedEvent);

            _metricsCollector.RecordMetric("tasks.failed", 1);
            _logger.LogError($"Task {taskId} failed: {errorMessage}");

            // Yeniden deneme kontrolü;
            if (task.AttemptCount < task.MaxAttempts)
            {
                task.AttemptCount++;
                task.Status = TaskStatus.Scheduled;
                task.ScheduledTime = DateTime.UtcNow.Add(task.RetryDelay);

                SetupTaskTimer(task);
                _logger.LogInformation($"Task {taskId} rescheduled for retry #{task.AttemptCount}");
            }
        }

        private async Task ScheduleNextRecurrenceAsync(ScheduledTask task)
        {
            var nextOccurrence = CalculateNextOccurrence(task);
            if (!nextOccurrence.HasValue)
            {
                _logger.LogInformation($"No more occurrences for recurring task {task.Id}");
                return;
            }

            var nextTask = task.Clone();
            nextTask.Id = Guid.NewGuid();
            nextTask.ScheduledTime = nextOccurrence.Value;
            nextTask.Status = TaskStatus.Scheduled;
            nextTask.AttemptCount = 0;
            nextTask.ActualStartTime = null;
            nextTask.ActualEndTime = null;
            nextTask.ProcessingTime = null;
            nextTask.Result = null;
            nextTask.ErrorMessage = null;

            await ScheduleTaskAsync(nextTask);
            _logger.LogInformation($"Next occurrence scheduled for recurring task {task.Id} at {nextOccurrence.Value}");
        }

        private DateTime? CalculateNextOccurrence(ScheduledTask task)
        {
            if (task.RecurrencePattern == null)
                return null;

            var pattern = task.RecurrencePattern;
            var lastScheduled = task.ScheduledTime;

            switch (pattern.Type)
            {
                case RecurrenceType.Daily:
                    return lastScheduled.AddDays(pattern.Interval);

                case RecurrenceType.Weekly:
                    return lastScheduled.AddDays(7 * pattern.Interval);

                case RecurrenceType.Monthly:
                    return lastScheduled.AddMonths(pattern.Interval);

                case RecurrenceType.Yearly:
                    return lastScheduled.AddYears(pattern.Interval);

                case RecurrenceType.Custom:
                    if (pattern.CustomRule != null)
                    {
                        return pattern.CustomRule.CalculateNext(lastScheduled);
                    }
                    break;
            }

            return null;
        }

        private async Task CheckDependentTasksAsync(Guid taskId)
        {
            var dependentTasks = _taskStore.GetDependentTasks(taskId);

            foreach (var dependentTask in dependentTasks)
            {
                var dependencyStatus = await CheckDependenciesAsync(dependentTask.Id);
                if (dependencyStatus.Status == DependencyStatusType.Ready &&
                    dependentTask.Status == TaskStatus.WaitingForDependencies)
                {
                    dependentTask.Status = TaskStatus.Scheduled;
                    SetupTaskTimer(dependentTask);
                    _logger.LogInformation($"Dependent task {dependentTask.Id} now ready for execution");
                }
            }
        }

        private async Task LoadPendingTasksAsync(CancellationToken cancellationToken)
        {
            var pendingTasks = _taskStore.GetPendingTasks();

            foreach (var task in pendingTasks)
            {
                if (task.Status == TaskStatus.Scheduled && task.ScheduledTime > DateTime.UtcNow)
                {
                    _activeTasks[task.Id] = task;
                    SetupTaskTimer(task);
                }
            }

            _logger.LogInformation($"Loaded {pendingTasks.Count} pending tasks");
            await Task.CompletedTask;
        }

        private void PerformMaintenance(object state)
        {
            try
            {
                // Süresi dolmuş görevleri temizle;
                var cutoffTime = DateTime.UtcNow.AddDays(-7);
                var expiredTasks = _activeTasks.Values;
                    .Where(t => t.Status == TaskStatus.Completed ||
                               t.Status == TaskStatus.Failed ||
                               t.Status == TaskStatus.Cancelled)
                    .Where(t => t.ActualEndTime.HasValue &&
                               t.ActualEndTime.Value < cutoffTime)
                    .ToList();

                foreach (var task in expiredTasks)
                {
                    _activeTasks.TryRemove(task.Id, out _);
                    _taskStore.ArchiveTask(task.Id);
                }

                if (expiredTasks.Any())
                {
                    _logger.LogInformation($"Cleaned up {expiredTasks.Count} expired tasks");
                }

                // Performans metriklerini güncelle;
                UpdatePerformanceMetrics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during maintenance");
            }
        }

        private void UpdatePerformanceMetrics()
        {
            var status = GetStatus();

            _metricsCollector.RecordMetric("scheduler.active_tasks", status.ActiveTaskCount);
            _metricsCollector.RecordMetric("scheduler.completed_today", status.CompletedTasksToday);
            _metricsCollector.RecordMetric("scheduler.failed_today", status.FailedTasksToday);
            _metricsCollector.RecordMetric("scheduler.success_rate", status.SuccessRate);
            _metricsCollector.RecordMetric("scheduler.avg_processing_time",
                status.AverageProcessingTime.TotalMilliseconds);
        }

        private void ApplyUpdates(ScheduledTask task, TaskUpdate updates)
        {
            if (updates.Title != null) task.Title = updates.Title;
            if (updates.Description != null) task.Description = updates.Description;
            if (updates.ScheduledTime.HasValue) task.ScheduledTime = updates.ScheduledTime.Value;
            if (updates.Duration.HasValue) task.Duration = updates.Duration.Value;
            if (updates.Priority.HasValue) task.Priority = updates.Priority.Value;
            if (updates.Category != null) task.Category = updates.Category;
            if (updates.Tags != null) task.Tags = updates.Tags;
            if (updates.RecurrencePattern != null) task.RecurrencePattern = updates.RecurrencePattern;
            if (updates.MaxAttempts.HasValue) task.MaxAttempts = updates.MaxAttempts.Value;
            if (updates.RetryDelay.HasValue) task.RetryDelay = updates.RetryDelay.Value;
            if (updates.Dependencies != null) task.Dependencies = updates.Dependencies;
            if (updates.Metadata != null) task.Metadata = updates.Metadata;
        }

        private void HandleTaskCompleted(TaskCompletedEvent @event)
        {
            // Tamamlanan görevler için ek işlemler;
            _logger.LogDebug($"Task {@event.TaskId} completion handled");
        }

        private void HandleTaskFailed(TaskFailedEvent @event)
        {
            // Başarısız görevler için ek işlemler;
            _logger.LogDebug($"Task {@event.TaskId} failure handled: {@event.ErrorMessage}");
        }

        private void HandlePriorityChanged(PriorityChangedEvent @event)
        {
            // Öncelik değişikliklerini işle;
            foreach (var task in _activeTasks.Values.Where(t => t.UserId == @event.UserId))
            {
                var newPriority = _priorityManager.CalculatePriorityAsync(task).Result;
                if (Math.Abs(task.Priority - newPriority) > 0.01)
                {
                    task.Priority = newPriority;
                    _logger.LogDebug($"Task {task.Id} priority updated to {newPriority}");
                }
            }
        }

        #endregion;

        #region Internal Supporting Classes;

        /// <summary>
        /// Görev saklama motoru.
        /// </summary>
        internal class TaskStore : IDisposable
        {
            private readonly ConcurrentDictionary<Guid, ScheduledTask> _tasks;
            private readonly ConcurrentDictionary<Guid, List<Guid>> _taskDependencies;
            private readonly System.Data.Common.DbConnection _databaseConnection;

            public TaskStore()
            {
                _tasks = new ConcurrentDictionary<Guid, ScheduledTask>();
                _taskDependencies = new ConcurrentDictionary<Guid, List<Guid>>();

                // Veritabanı bağlantısı;
                var connectionString = "Data Source=tasks.db";
                _databaseConnection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
                _databaseConnection.Open();
                InitializeDatabase();
            }

            public void AddTask(ScheduledTask task)
            {
                _tasks[task.Id] = task;

                if (task.Dependencies != null && task.Dependencies.Any())
                {
                    _taskDependencies[task.Id] = new List<Guid>(task.Dependencies);
                }

                SaveToDatabase(task);
            }

            public void UpdateTask(ScheduledTask task)
            {
                _tasks[task.Id] = task;

                if (task.Dependencies != null)
                {
                    _taskDependencies[task.Id] = new List<Guid>(task.Dependencies);
                }

                UpdateInDatabase(task);
            }

            public void RemoveTask(Guid taskId)
            {
                _tasks.TryRemove(taskId, out _);
                _taskDependencies.TryRemove(taskId, out _);
                DeleteFromDatabase(taskId);
            }

            public ScheduledTask GetTask(Guid taskId)
            {
                return _tasks.TryGetValue(taskId, out var task) ? task : null;
            }

            public List<ScheduledTask> GetPendingTasks()
            {
                return _tasks.Values;
                    .Where(t => t.Status == TaskStatus.Scheduled ||
                               t.Status == TaskStatus.WaitingForDependencies)
                    .ToList();
            }

            public List<ScheduledTask> GetDependentTasks(Guid parentTaskId)
            {
                return _taskDependencies;
                    .Where(kvp => kvp.Value.Contains(parentTaskId))
                    .Select(kvp => GetTask(kvp.Key))
                    .Where(t => t != null)
                    .ToList();
            }

            public int GetCompletedTasksCount(DateTime date)
            {
                return _tasks.Values;
                    .Count(t => t.Status == TaskStatus.Completed &&
                               t.ActualEndTime.HasValue &&
                               t.ActualEndTime.Value.Date == date.Date);
            }

            public int GetFailedTasksCount(DateTime date)
            {
                return _tasks.Values;
                    .Count(t => t.Status == TaskStatus.Failed &&
                               t.ActualEndTime.HasValue &&
                               t.ActualEndTime.Value.Date == date.Date);
            }

            public TimeSpan GetAverageProcessingTime()
            {
                var completedTasks = _tasks.Values;
                    .Where(t => t.Status == TaskStatus.Completed &&
                               t.ProcessingTime.HasValue)
                    .ToList();

                if (!completedTasks.Any()) return TimeSpan.Zero;

                var averageTicks = completedTasks.Average(t => t.ProcessingTime.Value.Ticks);
                return TimeSpan.FromTicks((long)averageTicks);
            }

            public double GetSuccessRate()
            {
                var completedTasks = _tasks.Values;
                    .Count(t => t.Status == TaskStatus.Completed);
                var failedTasks = _tasks.Values;
                    .Count(t => t.Status == TaskStatus.Failed);

                var total = completedTasks + failedTasks;
                if (total == 0) return 1.0;

                return (double)completedTasks / total;
            }

            public void ArchiveTask(Guid taskId)
            {
                if (_tasks.TryRemove(taskId, out var task))
                {
                    ArchiveToDatabase(task);
                }
            }

            private void InitializeDatabase()
            {
                using var command = _databaseConnection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Tasks (
                        Id TEXT PRIMARY KEY,
                        UserId TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Description TEXT,
                        ScheduledTime DATETIME NOT NULL,
                        Duration TEXT NOT NULL,
                        Priority REAL NOT NULL,
                        Status INTEGER NOT NULL,
                        Type INTEGER NOT NULL,
                        Category TEXT,
                        Tags TEXT,
                        IsRecurring INTEGER NOT NULL,
                        RecurrencePattern TEXT,
                        Dependencies TEXT,
                        MaxAttempts INTEGER NOT NULL,
                        AttemptCount INTEGER NOT NULL,
                        RetryDelay TEXT,
                        Metadata TEXT,
                        CreatedAt DATETIME NOT NULL,
                        UpdatedAt DATETIME NOT NULL,
                        ActualStartTime DATETIME,
                        ActualEndTime DATETIME,
                        ProcessingTime TEXT,
                        Result TEXT,
                        ErrorMessage TEXT;
                    );
                    
                    CREATE INDEX IF NOT EXISTS idx_tasks_user ON Tasks(UserId);
                    CREATE INDEX IF NOT EXISTS idx_tasks_status ON Tasks(Status);
                    CREATE INDEX IF NOT EXISTS idx_tasks_scheduled ON Tasks(ScheduledTime);
                    CREATE INDEX IF NOT EXISTS idx_tasks_priority ON Tasks(Priority);
                ";
                command.ExecuteNonQuery();
            }

            private void SaveToDatabase(ScheduledTask task)
            {
                // Veritabanı kaydetme işlemi;
            }

            private void UpdateInDatabase(ScheduledTask task)
            {
                // Veritabanı güncelleme işlemi;
            }

            private void DeleteFromDatabase(Guid taskId)
            {
                // Veritabanı silme işlemi;
            }

            private void ArchiveToDatabase(ScheduledTask task)
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
        /// Zamanlayıcı motoru.
        /// </summary>
        internal class SchedulerEngine;
        {
            public SchedulingResult ScheduleTask(ScheduledTask task)
            {
                // Zamanlama algoritması;
                // Gerçek uygulamada daha karmaşık algoritmalar kullanılır;

                return new SchedulingResult;
                {
                    Success = true,
                    TaskId = task.Id,
                    ScheduledTime = task.ScheduledTime,
                    Priority = task.Priority;
                };
            }

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
        /// Çakışma dedektörü.
        /// </summary>
        internal class ConflictDetector;
        {
            public List<TaskConflict> DetectConflicts(ScheduledTask newTask, IEnumerable<ScheduledTask> existingTasks)
            {
                var conflicts = new List<TaskConflict>();

                foreach (var existingTask in existingTasks)
                {
                    if (existingTask.Id == newTask.Id) continue;
                    if (existingTask.UserId != newTask.UserId) continue;
                    if (existingTask.Status != TaskStatus.Scheduled) continue;

                    var newStart = newTask.ScheduledTime;
                    var newEnd = newTask.ScheduledTime + newTask.Duration;
                    var existingStart = existingTask.ScheduledTime;
                    var existingEnd = existingTask.ScheduledTime + existingTask.Duration;

                    // Zaman çakışması kontrolü;
                    if (newStart < existingEnd && newEnd > existingStart)
                    {
                        conflicts.Add(new TaskConflict;
                        {
                            Task1Id = newTask.Id,
                            Task2Id = existingTask.Id,
                            Type = ConflictType.TimeOverlap,
                            Severity = CalculateConflictSeverity(newTask, existingTask),
                            OverlapDuration = CalculateOverlap(newStart, newEnd, existingStart, existingEnd),
                            Message = $"Time conflict with task '{existingTask.Title}'"
                        });
                    }

                    // Kaynak çakışması kontrolü;
                    if (HasResourceConflict(newTask, existingTask))
                    {
                        conflicts.Add(new TaskConflict;
                        {
                            Task1Id = newTask.Id,
                            Task2Id = existingTask.Id,
                            Type = ConflictType.ResourceConflict,
                            Severity = ConflictSeverity.Medium,
                            Message = $"Resource conflict with task '{existingTask.Title}'"
                        });
                    }
                }

                return conflicts;
            }

            private bool HasResourceConflict(ScheduledTask task1, ScheduledTask task2)
            {
                if (task1.RequiredResources == null || task2.RequiredResources == null)
                    return false;

                return task1.RequiredResources.Intersect(task2.RequiredResources).Any();
            }

            private ConflictSeverity CalculateConflictSeverity(ScheduledTask task1, ScheduledTask task2)
            {
                var priorityDiff = Math.Abs(task1.Priority - task2.Priority);

                if (priorityDiff > 0.7) return ConflictSeverity.Low;
                if (priorityDiff > 0.3) return ConflictSeverity.Medium;
                return ConflictSeverity.High;
            }

            private TimeSpan CalculateOverlap(DateTime start1, DateTime end1, DateTime start2, DateTime end2)
            {
                var latestStart = start1 > start2 ? start1 : start2;
                var earliestEnd = end1 < end2 ? end1 : end2;

                if (latestStart < earliestEnd)
                {
                    return earliestEnd - latestStart;
                }

                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Optimizasyon motoru.
        /// </summary>
        internal class OptimizationEngine;
        {
            public async Task<OptimizationResult> OptimizeAsync(List<ScheduledTask> tasks, OptimizationStrategy strategy)
            {
                await Task.Delay(100); // Simüle edilmiş işlem;

                var optimizedTasks = new List<ScheduledTask>();
                var totalTimeSaved = TimeSpan.Zero;

                switch (strategy)
                {
                    case OptimizationStrategy.Efficiency:
                        optimizedTasks = OptimizeForEfficiency(tasks);
                        break;

                    case OptimizationStrategy.Priority:
                        optimizedTasks = OptimizeForPriority(tasks);
                        break;

                    case OptimizationStrategy.ResourceUsage:
                        optimizedTasks = OptimizeForResourceUsage(tasks);
                        break;

                    case OptimizationStrategy.Deadline:
                        optimizedTasks = OptimizeForDeadline(tasks);
                        break;
                }

                // Zaman tasarrufunu hesapla;
                totalTimeSaved = CalculateTimeSaved(tasks, optimizedTasks);

                return new OptimizationResult;
                {
                    Success = true,
                    UserId = tasks.FirstOrDefault()?.UserId ?? Guid.Empty,
                    Strategy = strategy,
                    OriginalTasks = tasks,
                    OptimizedTasks = optimizedTasks,
                    TotalTimeSaved = totalTimeSaved,
                    EfficiencyImprovement = CalculateEfficiencyImprovement(tasks, optimizedTasks),
                    Message = $"Schedule optimized using {strategy} strategy"
                };
            }

            private List<ScheduledTask> OptimizeForEfficiency(List<ScheduledTask> tasks)
            {
                // Verimlilik için optimizasyon;
                return tasks;
                    .OrderBy(t => t.Duration)
                    .ThenByDescending(t => t.Priority)
                    .Select((t, i) =>
                    {
                        var optimized = t.Clone();
                        optimized.ScheduledTime = DateTime.UtcNow.Date.AddHours(9).AddMinutes(i * 30);
                        return optimized;
                    })
                    .ToList();
            }

            private List<ScheduledTask> OptimizeForPriority(List<ScheduledTask> tasks)
            {
                // Öncelik için optimizasyon;
                return tasks;
                    .OrderByDescending(t => t.Priority)
                    .Select((t, i) =>
                    {
                        var optimized = t.Clone();
                        optimized.ScheduledTime = DateTime.UtcNow.Date.AddHours(9).AddMinutes(i * 30);
                        return optimized;
                    })
                    .ToList();
            }

            private List<ScheduledTask> OptimizeForResourceUsage(List<ScheduledTask> tasks)
            {
                // Kaynak kullanımı için optimizasyon;
                return tasks;
                    .OrderBy(t => t.RequiredResources?.Count ?? 0)
                    .ThenByDescending(t => t.Priority)
                    .Select((t, i) =>
                    {
                        var optimized = t.Clone();
                        optimized.ScheduledTime = DateTime.UtcNow.Date.AddHours(9).AddMinutes(i * 30);
                        return optimized;
                    })
                    .ToList();
            }

            private List<ScheduledTask> OptimizeForDeadline(List<ScheduledTask> tasks)
            {
                // Son teslim tarihi için optimizasyon;
                return tasks;
                    .Where(t => t.Deadline.HasValue)
                    .OrderBy(t => t.Deadline)
                    .ThenByDescending(t => t.Priority)
                    .Select((t, i) =>
                    {
                        var optimized = t.Clone();
                        optimized.ScheduledTime = DateTime.UtcNow.Date.AddHours(9).AddMinutes(i * 30);
                        return optimized;
                    })
                    .ToList();
            }

            private TimeSpan CalculateTimeSaved(List<ScheduledTask> original, List<ScheduledTask> optimized)
            {
                // Basit zaman tasarrufu hesaplama;
                return TimeSpan.FromHours(1); // Simüle edilmiş;
            }

            private double CalculateEfficiencyImprovement(List<ScheduledTask> original, List<ScheduledTask> optimized)
            {
                // Verimlilik iyileştirmesi hesaplama;
                return 0.15; // %15 iyileşme;
            }
        }

        /// <summary>
        /// Bildirim yöneticisi.
        /// </summary>
        internal class NotificationManager;
        {
            public async Task SendTaskReminderAsync(ScheduledTask task, TimeSpan reminderOffset)
            {
                // Görev hatırlatıcısı gönder;
                await Task.Delay(10);
            }

            public async Task SendTaskStartedNotificationAsync(ScheduledTask task)
            {
                // Görev başladı bildirimi;
                await Task.Delay(10);
            }

            public async Task SendTaskCompletedNotificationAsync(ScheduledTask task)
            {
                // Görev tamamlandı bildirimi;
                await Task.Delay(10);
            }
        }

        #endregion;
    }

    #region Public Models and Enums;

    /// <summary>
    /// Planlanmış görev.
    /// </summary>
    public class ScheduledTask;
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime ScheduledTime { get; set; }
        public TimeSpan Duration { get; set; }
        public double Priority { get; set; } = 0.5;
        public TaskStatus Status { get; set; } = TaskStatus.Scheduled;
        public TaskType Type { get; set; } = TaskType.General;
        public string Category { get; set; }
        public List<string> Tags { get; set; }
        public bool IsRecurring { get; set; }
        public RecurrencePattern RecurrencePattern { get; set; }
        public List<Guid> Dependencies { get; set; }
        public int MaxAttempts { get; set; } = 3;
        public int AttemptCount { get; set; }
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);
        public Dictionary<string, object> Metadata { get; set; }
        public List<string> RequiredResources { get; set; }
        public DateTime? Deadline { get; set; }
        public bool AllowPastScheduling { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ActualStartTime { get; set; }
        public DateTime? ActualEndTime { get; set; }
        public TimeSpan? ProcessingTime { get; set; }
        public object Result { get; set; }
        public string ErrorMessage { get; set; }

        public ScheduledTask Clone()
        {
            return new ScheduledTask;
            {
                Id = Id,
                UserId = UserId,
                Title = Title,
                Description = Description,
                ScheduledTime = ScheduledTime,
                Duration = Duration,
                Priority = Priority,
                Status = Status,
                Type = Type,
                Category = Category,
                Tags = Tags?.ToList(),
                IsRecurring = IsRecurring,
                RecurrencePattern = RecurrencePattern?.Clone(),
                Dependencies = Dependencies?.ToList(),
                MaxAttempts = MaxAttempts,
                AttemptCount = AttemptCount,
                RetryDelay = RetryDelay,
                Metadata = Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                RequiredResources = RequiredResources?.ToList(),
                Deadline = Deadline,
                AllowPastScheduling = AllowPastScheduling,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                ActualStartTime = ActualStartTime,
                ActualEndTime = ActualEndTime,
                ProcessingTime = ProcessingTime,
                Result = Result,
                ErrorMessage = ErrorMessage;
            };
        }
    }

    /// <summary>
    /// Planlama sonucu.
    /// </summary>
    public class SchedulingResult;
    {
        public bool Success { get; set; }
        public Guid TaskId { get; set; }
        public string Message { get; set; }
        public DateTime? ScheduledTime { get; set; }
        public double? Priority { get; set; }
        public List<TaskConflict> Conflicts { get; set; }
        public DependencyStatus DependencyStatus { get; set; }
    }

    /// <summary>
    /// Güncelleme sonucu.
    /// </summary>
    public class UpdateResult;
    {
        public bool Success { get; set; }
        public Guid TaskId { get; set; }
        public string Message { get; set; }
        public List<TaskConflict> Conflicts { get; set; }
    }

    /// <summary>
    /// İptal sonucu.
    /// </summary>
    public class CancellationResult;
    {
        public bool Success { get; set; }
        public Guid TaskId { get; set; }
        public string Message { get; set; }
        public int DependentTasksAffected { get; set; }
    }

    /// <summary>
    /// Bağımlılık durumu.
    /// </summary>
    public class DependencyStatus;
    {
        public Guid TaskId { get; set; }
        public DependencyStatusType Status { get; set; }
        public string Message { get; set; }
        public List<DependencyCheckResult> Dependencies { get; set; }
        public int ReadyCount { get; set; }
        public int TotalCount { get; set; }
    }

    /// <summary>
    /// Optimizasyon sonucu.
    /// </summary>
    public class OptimizationResult;
    {
        public bool Success { get; set; }
        public Guid UserId { get; set; }
        public OptimizationStrategy Strategy { get; set; }
        public string Message { get; set; }
        public List<ScheduledTask> OriginalTasks { get; set; }
        public List<ScheduledTask> OptimizedTasks { get; set; }
        public TimeSpan TotalTimeSaved { get; set; }
        public double EfficiencyImprovement { get; set; }
    }

    /// <summary>
    /// Görev çakışması.
    /// </summary>
    public class TaskConflict;
    {
        public Guid Task1Id { get; set; }
        public Guid Task2Id { get; set; }
        public ConflictType Type { get; set; }
        public ConflictSeverity Severity { get; set; }
        public string Message { get; set; }
        public TimeSpan OverlapDuration { get; set; }
    }

    /// <summary>
    /// Zamanlayıcı durumu.
    /// </summary>
    public class SchedulerStatus;
    {
        public bool IsRunning { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActiveTaskCount { get; set; }
        public int CompletedTasksToday { get; set; }
        public int FailedTasksToday { get; set; }
        public List<ScheduledTask> UpcomingTasks { get; set; }
        public TimeSpan AverageProcessingTime { get; set; }
        public double SuccessRate { get; set; }
    }

    /// <summary>
    /// Görev güncellemesi.
    /// </summary>
    public class TaskUpdate;
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime? ScheduledTime { get; set; }
        public TimeSpan? Duration { get; set; }
        public double? Priority { get; set; }
        public string Category { get; set; }
        public List<string> Tags { get; set; }
        public RecurrencePattern RecurrencePattern { get; set; }
        public int? MaxAttempts { get; set; }
        public TimeSpan? RetryDelay { get; set; }
        public List<Guid> Dependencies { get; set; }
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
    /// Bağımlılık kontrol sonucu.
    /// </summary>
    public class DependencyCheckResult;
    {
        public Guid DependencyId { get; set; }
        public TaskStatus Status { get; set; }
        public bool IsReady { get; set; }
        public string Message { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Görev durumu.
    /// </summary>
    public enum TaskStatus;
    {
        Scheduled,
        Running,
        Completed,
        Failed,
        Cancelled,
        WaitingForDependencies,
        Pending,
        NotFound;
    }

    /// <summary>
    /// Görev türü.
    /// </summary>
    public enum TaskType;
    {
        General,
        Work,
        Personal,
        Health,
        Learning,
        Social,
        Financial,
        Maintenance,
        Automated,
        Manual;
    }

    /// <summary>
    /// Bağımlılık durumu türü.
    /// </summary>
    public enum DependencyStatusType;
    {
        Ready,
        Waiting,
        Failed,
        NotFound;
    }

    /// <summary>
    /// Optimizasyon stratejisi.
    /// </summary>
    public enum OptimizationStrategy;
    {
        Efficiency,
        Priority,
        ResourceUsage,
        Deadline,
        Balanced;
    }

    /// <summary>
    /// Çakışma türü.
    /// </summary>
    public enum ConflictType;
    {
        TimeOverlap,
        ResourceConflict,
        DependencyConflict,
        PriorityConflict;
    }

    /// <summary>
    /// Çakışma şiddeti.
    /// </summary>
    public enum ConflictSeverity;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Güncelleme türü.
    /// </summary>
    public enum UpdateType;
    {
        Modification,
        Reschedule,
        PriorityChange,
        DependencyUpdate;
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

    #endregion;

    #region Events;

    /// <summary>
    /// Görev planlandı event'i.
    /// </summary>
    public class TaskScheduledEvent : IEvent;
    {
        public Guid TaskId { get; set; }
        public Guid UserId { get; set; }
        public DateTime ScheduledTime { get; set; }
        public double Priority { get; set; }
        public TaskType TaskType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Görev güncellendi event'i.
    /// </summary>
    public class TaskUpdatedEvent : IEvent;
    {
        public Guid TaskId { get; set; }
        public Guid UserId { get; set; }
        public ScheduledTask OriginalTask { get; set; }
        public ScheduledTask UpdatedTask { get; set; }
        public UpdateType UpdateType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Görev iptal edildi event'i.
    /// </summary>
    public class TaskCancelledEvent : IEvent;
    {
        public Guid TaskId { get; set; }
        public Guid UserId { get; set; }
        public TaskType TaskType { get; set; }
        public string Reason { get; set; }
        public int DependentTasksAffected { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Görev başladı event'i.
    /// </summary>
    public class TaskStartedEvent : IEvent;
    {
        public Guid TaskId { get; set; }
        public Guid UserId { get; set; }
        public DateTime StartTime { get; set; }
        public double Priority { get; set; }
        public TaskType TaskType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Görev tamamlandı event'i.
    /// </summary>
    public class TaskCompletedEvent : IEvent;
    {
        public Guid TaskId { get; set; }
        public Guid UserId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public object Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Görev başarısız oldu event'i.
    /// </summary>
    public class TaskFailedEvent : IEvent;
    {
        public Guid TaskId { get; set; }
        public Guid UserId { get; set; }
        public string ErrorMessage { get; set; }
        public int AttemptCount { get; set; }
        public int MaxAttempts { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Çizelge optimize edildi event'i.
    /// </summary>
    public class ScheduleOptimizedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public OptimizationStrategy Strategy { get; set; }
        public int OriginalTaskCount { get; set; }
        public int OptimizedTaskCount { get; set; }
        public TimeSpan TotalTimeSaved { get; set; }
        public double EfficiencyImprovement { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Zamanlayıcı başlatıldı event'i.
    /// </summary>
    public class SchedulerStartedEvent : IEvent;
    {
        public DateTime StartTime { get; set; }
        public int LoadedTaskCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Zamanlayıcı durduruldu event'i.
    /// </summary>
    public class SchedulerStoppedEvent : IEvent;
    {
        public DateTime StopTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActiveTaskCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Öncelik değişti event'i.
    /// </summary>
    public class PriorityChangedEvent : IEvent;
    {
        public Guid UserId { get; set; }
        public double OldPriority { get; set; }
        public double NewPriority { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}
