using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Services.Messaging.EventBus;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Brain.MemorySystem;

namespace NEDA.CharacterSystems.AI_Behaviors.NPC_Routines;
{
    /// <summary>
    /// Represents a scheduled activity for NPC routines;
    /// </summary>
    public class ScheduledActivity;
    {
        public string ActivityId { get; set; }
        public string ActivityName { get; set; }
        public ActivityType Type { get; set; }
        public ActivityPriority Priority { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public DayOfWeek[] ValidDays { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public ActivityCondition Condition { get; set; }
        public bool IsRecurring { get; set; }
        public TimeSpan? RecurrenceInterval { get; set; }
        public DateTime LastExecuted { get; set; }
        public ActivityStatus Status { get; set; }
        public string AssociatedLocation { get; set; }
        public string RequiredItem { get; set; }
        public int EnergyCost { get; set; }

        public ScheduledActivity()
        {
            Parameters = new Dictionary<string, object>();
            ValidDays = Enum.GetValues(typeof(DayOfWeek)).Cast<DayOfWeek>().ToArray();
            Status = ActivityStatus.Pending;
            Priority = ActivityPriority.Medium;
        }
    }

    /// <summary>
    /// Activity execution context with runtime information;
    /// </summary>
    public class ActivityExecutionContext;
    {
        public string ExecutionId { get; set; }
        public ScheduledActivity Activity { get; set; }
        public DateTime ScheduledTime { get; set; }
        public DateTime ActualStartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ExecutionResult Result { get; set; }
        public Dictionary<string, object> ExecutionData { get; set; }
        public NPCState NpcState { get; set; }
        public WorldState WorldState { get; set; }
        public CancellationTokenSource CancellationSource { get; set; }

        public ActivityExecutionContext()
        {
            ExecutionData = new Dictionary<string, object>();
            CancellationSource = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Activity condition evaluation;
    /// </summary>
    public class ActivityCondition;
    {
        public List<Condition> Preconditions { get; set; }
        public List<Condition> Postconditions { get; set; }
        public Func<NPCState, WorldState, bool> CustomEvaluator { get; set; }

        public bool Evaluate(NPCState npcState, WorldState worldState)
        {
            if (CustomEvaluator != null)
                return CustomEvaluator(npcState, worldState);

            return Preconditions.All(c => c.Evaluate(npcState, worldState));
        }
    }

    /// <summary>
    /// Main Activity Scheduler for managing NPC routines;
    /// </summary>
    public interface IActivityScheduler;
    {
        Task InitializeAsync();
        Task<string> ScheduleActivityAsync(ScheduledActivity activity);
        Task<bool> UpdateActivityAsync(string activityId, ScheduledActivity updatedActivity);
        Task<bool> CancelActivityAsync(string activityId);
        Task<List<ScheduledActivity>> GetScheduledActivitiesAsync(string npcId, DateTime date);
        Task<List<ActivityExecutionContext>> GetActivityHistoryAsync(string npcId, TimeSpan period);
        Task<bool> ForceExecuteActivityAsync(string activityId);
        Task<PauseResumeResult> PauseSchedulerAsync();
        Task<PauseResumeResult> ResumeSchedulerAsync();
        Task<ActivityStatistics> GetStatisticsAsync();
        void Dispose();
    }

    /// <summary>
    /// Implementation of Activity Scheduler with advanced features;
    /// </summary>
    public class ActivityScheduler : IActivityScheduler, IDisposable;
    {
        private readonly ILogger<ActivityScheduler> _logger;
        private readonly IEventBus _eventBus;
        private readonly IMetricsCollector _metrics;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ActivitySchedulerConfig _config;

        private readonly Dictionary<string, ScheduledActivity> _activities;
        private readonly Dictionary<string, ActivityExecutionContext> _executions;
        private readonly PriorityQueue<ScheduledActivity, ActivityPriority> _activityQueue;
        private readonly List<IActivityExecutionStrategy> _executionStrategies;

        private Timer _schedulerTimer;
        private Timer _maintenanceTimer;
        private CancellationTokenSource _globalCancellationToken;
        private bool _isRunning;
        private bool _isPaused;
        private readonly object _lockObject = new object();

        private ActivityStatistics _statistics;
        private INPCStateProvider _npcStateProvider;
        private IWorldStateProvider _worldStateProvider;

        /// <summary>
        /// Activity Scheduler constructor with dependency injection;
        /// </summary>
        public ActivityScheduler(
            ILogger<ActivityScheduler> logger,
            IEventBus eventBus,
            IMetricsCollector metrics,
            IShortTermMemory shortTermMemory,
            ActivitySchedulerConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _config = config ?? new ActivitySchedulerConfig();

            _activities = new Dictionary<string, ScheduledActivity>();
            _executions = new Dictionary<string, ActivityExecutionContext>();
            _activityQueue = new PriorityQueue<ScheduledActivity, ActivityPriority>();
            _executionStrategies = new List<IActivityExecutionStrategy>();
            _globalCancellationToken = new CancellationTokenSource();
            _statistics = new ActivityStatistics();

            RegisterDefaultStrategies();
            SubscribeToEvents();

            _logger.LogInformation("ActivityScheduler initialized with {StrategyCount} execution strategies",
                _executionStrategies.Count);
        }

        /// <summary>
        /// Initialize the scheduler with providers;
        /// </summary>
        public void Initialize(INPCStateProvider npcStateProvider, IWorldStateProvider worldStateProvider)
        {
            _npcStateProvider = npcStateProvider ?? throw new ArgumentNullException(nameof(npcStateProvider));
            _worldStateProvider = worldStateProvider ?? throw new ArgumentNullException(nameof(worldStateProvider));

            StartScheduler();
        }

        /// <summary>
        /// Async initialization;
        /// </summary>
        public async Task InitializeAsync()
        {
            _logger.LogInformation("Starting ActivityScheduler initialization...");

            // Load persisted activities from storage if needed;
            await LoadPersistedActivitiesAsync();

            // Start timers;
            StartScheduler();

            _logger.LogInformation("ActivityScheduler initialized successfully");
            await _eventBus.PublishAsync(new SchedulerInitializedEvent;
            {
                Timestamp = DateTime.UtcNow,
                ScheduledActivityCount = _activities.Count;
            });
        }

        /// <summary>
        /// Schedule a new activity;
        /// </summary>
        public async Task<string> ScheduleActivityAsync(ScheduledActivity activity)
        {
            if (activity == null)
                throw new ArgumentNullException(nameof(activity));

            lock (_lockObject)
            {
                // Generate unique ID if not provided;
                if (string.IsNullOrEmpty(activity.ActivityId))
                {
                    activity.ActivityId = GenerateActivityId(activity.ActivityName);
                }

                // Validate activity;
                if (!ValidateActivity(activity))
                {
                    throw new InvalidActivityException($"Activity validation failed for {activity.ActivityName}");
                }

                // Check for conflicts;
                if (HasScheduleConflict(activity))
                {
                    throw new ScheduleConflictException($"Schedule conflict detected for activity {activity.ActivityName}");
                }

                _activities[activity.ActivityId] = activity;
                _activityQueue.Enqueue(activity, activity.Priority);

                _logger.LogDebug("Activity scheduled: {ActivityId} - {ActivityName}",
                    activity.ActivityId, activity.ActivityName);
            }

            // Persist if configured;
            if (_config.PersistActivities)
            {
                await PersistActivityAsync(activity);
            }

            // Publish event;
            await _eventBus.PublishAsync(new ActivityScheduledEvent;
            {
                ActivityId = activity.ActivityId,
                ActivityName = activity.ActivityName,
                ScheduledTime = DateTime.UtcNow,
                Priority = activity.Priority;
            });

            // Update metrics;
            _metrics.IncrementCounter("activities.scheduled", new Dictionary<string, string>
            {
                ["type"] = activity.Type.ToString(),
                ["priority"] = activity.Priority.ToString()
            });

            return activity.ActivityId;
        }

        /// <summary>
        /// Update an existing activity;
        /// </summary>
        public async Task<bool> UpdateActivityAsync(string activityId, ScheduledActivity updatedActivity)
        {
            if (string.IsNullOrEmpty(activityId))
                throw new ArgumentNullException(nameof(activityId));

            if (updatedActivity == null)
                throw new ArgumentNullException(nameof(updatedActivity));

            lock (_lockObject)
            {
                if (!_activities.ContainsKey(activityId))
                {
                    _logger.LogWarning("Activity not found for update: {ActivityId}", activityId);
                    return false;
                }

                // Preserve original ID;
                updatedActivity.ActivityId = activityId;

                // Validate updated activity;
                if (!ValidateActivity(updatedActivity))
                {
                    throw new InvalidActivityException($"Activity validation failed for {updatedActivity.ActivityName}");
                }

                // Remove old activity from queue;
                var oldActivity = _activities[activityId];
                var tempQueue = new PriorityQueue<ScheduledActivity, ActivityPriority>();

                while (_activityQueue.Count > 0)
                {
                    var item = _activityQueue.Dequeue();
                    if (item.ActivityId != activityId)
                    {
                        tempQueue.Enqueue(item, item.Priority);
                    }
                }

                // Restore queue;
                while (tempQueue.Count > 0)
                {
                    var item = tempQueue.Dequeue();
                    _activityQueue.Enqueue(item, item.Priority);
                }

                // Add updated activity;
                _activities[activityId] = updatedActivity;
                _activityQueue.Enqueue(updatedActivity, updatedActivity.Priority);

                _logger.LogInformation("Activity updated: {ActivityId}", activityId);
            }

            // Persist if configured;
            if (_config.PersistActivities)
            {
                await PersistActivityAsync(updatedActivity);
            }

            await _eventBus.PublishAsync(new ActivityUpdatedEvent;
            {
                ActivityId = activityId,
                UpdateTime = DateTime.UtcNow;
            });

            return true;
        }

        /// <summary>
        /// Cancel a scheduled activity;
        /// </summary>
        public async Task<bool> CancelActivityAsync(string activityId)
        {
            if (string.IsNullOrEmpty(activityId))
                throw new ArgumentNullException(nameof(activityId));

            lock (_lockObject)
            {
                if (!_activities.ContainsKey(activityId))
                {
                    _logger.LogWarning("Activity not found for cancellation: {ActivityId}", activityId);
                    return false;
                }

                var activity = _activities[activityId];

                // Remove from activities dictionary;
                _activities.Remove(activityId);

                // Remove from queue;
                var tempQueue = new PriorityQueue<ScheduledActivity, ActivityPriority>();

                while (_activityQueue.Count > 0)
                {
                    var item = _activityQueue.Dequeue();
                    if (item.ActivityId != activityId)
                    {
                        tempQueue.Enqueue(item, item.Priority);
                    }
                }

                // Restore queue;
                while (tempQueue.Count > 0)
                {
                    var item = tempQueue.Dequeue();
                    _activityQueue.Enqueue(item, item.Priority);
                }

                // Cancel any ongoing execution;
                if (_executions.ContainsKey(activityId))
                {
                    var execution = _executions[activityId];
                    execution.CancellationSource?.Cancel();
                    execution.Result = ExecutionResult.Cancelled;
                    execution.EndTime = DateTime.UtcNow;
                }

                activity.Status = ActivityStatus.Cancelled;

                _logger.LogInformation("Activity cancelled: {ActivityId} - {ActivityName}",
                    activityId, activity.ActivityName);

                _statistics.CancelledCount++;
            }

            await _eventBus.PublishAsync(new ActivityCancelledEvent;
            {
                ActivityId = activityId,
                CancellationTime = DateTime.UtcNow;
            });

            _metrics.IncrementCounter("activities.cancelled");

            return true;
        }

        /// <summary>
        /// Get scheduled activities for a specific NPC and date;
        /// </summary>
        public Task<List<ScheduledActivity>> GetScheduledActivitiesAsync(string npcId, DateTime date)
        {
            if (string.IsNullOrEmpty(npcId))
                throw new ArgumentNullException(nameof(npcId));

            lock (_lockObject)
            {
                var activities = _activities.Values;
                    .Where(a => IsActivityForDate(a, date))
                    .OrderBy(a => a.StartTime)
                    .ThenBy(a => a.Priority)
                    .ToList();

                return Task.FromResult(activities);
            }
        }

        /// <summary>
        /// Get activity execution history;
        /// </summary>
        public Task<List<ActivityExecutionContext>> GetActivityHistoryAsync(string npcId, TimeSpan period)
        {
            var cutoffTime = DateTime.UtcNow - period;

            lock (_lockObject)
            {
                var history = _executions.Values;
                    .Where(e => e.ActualStartTime >= cutoffTime)
                    .OrderByDescending(e => e.ActualStartTime)
                    .ToList();

                return Task.FromResult(history);
            }
        }

        /// <summary>
        /// Force execute an activity immediately;
        /// </summary>
        public async Task<bool> ForceExecuteActivityAsync(string activityId)
        {
            if (string.IsNullOrEmpty(activityId))
                throw new ArgumentNullException(nameof(activityId));

            lock (_lockObject)
            {
                if (!_activities.ContainsKey(activityId))
                {
                    _logger.LogWarning("Activity not found for forced execution: {ActivityId}", activityId);
                    return false;
                }

                var activity = _activities[activityId];

                // Check if already executing;
                if (_executions.ContainsKey(activityId) &&
                    _executions[activityId].EndTime == null)
                {
                    _logger.LogWarning("Activity already executing: {ActivityId}", activityId);
                    return false;
                }

                // Execute immediately;
                _ = ExecuteActivityAsync(activity, true);

                _logger.LogInformation("Forced execution started for activity: {ActivityId}", activityId);
            }

            return true;
        }

        /// <summary>
        /// Pause the scheduler;
        /// </summary>
        public Task<PauseResumeResult> PauseSchedulerAsync()
        {
            lock (_lockObject)
            {
                if (_isPaused)
                {
                    return Task.FromResult(new PauseResumeResult;
                    {
                        Success = false,
                        Message = "Scheduler already paused",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                _isPaused = true;
                _schedulerTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                _logger.LogInformation("Activity scheduler paused");

                return Task.FromResult(new PauseResumeResult;
                {
                    Success = true,
                    Message = "Scheduler paused successfully",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Resume the scheduler;
        /// </summary>
        public Task<PauseResumeResult> ResumeSchedulerAsync()
        {
            lock (_lockObject)
            {
                if (!_isPaused)
                {
                    return Task.FromResult(new PauseResumeResult;
                    {
                        Success = false,
                        Message = "Scheduler already running",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                _isPaused = false;
                _schedulerTimer?.Change(_config.SchedulerInterval, _config.SchedulerInterval);

                _logger.LogInformation("Activity scheduler resumed");

                return Task.FromResult(new PauseResumeResult;
                {
                    Success = true,
                    Message = "Scheduler resumed successfully",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Get scheduler statistics;
        /// </summary>
        public Task<ActivityStatistics> GetStatisticsAsync()
        {
            lock (_lockObject)
            {
                _statistics.TotalActivities = _activities.Count;
                _statistics.QueuedActivities = _activityQueue.Count;
                _statistics.ActiveExecutions = _executions.Values.Count(e => e.EndTime == null);
                _statistics.UpcomingActivities = _activities.Values;
                    .Count(a => a.Status == ActivityStatus.Pending &&
                               IsActivityDueSoon(a, TimeSpan.FromMinutes(30)));

                return Task.FromResult(_statistics.Clone());
            }
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _globalCancellationToken?.Cancel();
                _globalCancellationToken?.Dispose();
                _schedulerTimer?.Dispose();
                _maintenanceTimer?.Dispose();

                // Complete all executions;
                foreach (var execution in _executions.Values)
                {
                    execution.CancellationSource?.Dispose();
                }

                _executions.Clear();
                _activities.Clear();

                _logger.LogInformation("ActivityScheduler disposed");
            }
        }

        #region Private Methods;

        private void StartScheduler()
        {
            if (_isRunning) return;

            _schedulerTimer = new Timer(
                SchedulerTick,
                null,
                TimeSpan.Zero,
                _config.SchedulerInterval);

            _maintenanceTimer = new Timer(
                MaintenanceTick,
                null,
                _config.MaintenanceInterval,
                _config.MaintenanceInterval);

            _isRunning = true;
            _isPaused = false;

            _logger.LogInformation("Activity scheduler started with {Interval} interval",
                _config.SchedulerInterval);
        }

        private async void SchedulerTick(object state)
        {
            if (_isPaused || _activityQueue.Count == 0)
                return;

            try
            {
                var now = DateTime.UtcNow;
                var dueActivities = new List<ScheduledActivity>();

                lock (_lockObject)
                {
                    // Check for due activities;
                    while (_activityQueue.Count > 0)
                    {
                        var activity = _activityQueue.Peek();

                        if (IsActivityDue(activity, now))
                        {
                            dueActivities.Add(activity);
                            _activityQueue.Dequeue();
                        }
                        else;
                        {
                            break;
                        }
                    }
                }

                // Execute due activities;
                foreach (var activity in dueActivities)
                {
                    try
                    {
                        await ExecuteActivityAsync(activity, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing activity {ActivityId}", activity.ActivityId);
                        await HandleExecutionErrorAsync(activity, ex);
                    }
                }

                // Update metrics;
                _metrics.Gauge("scheduler.queue_size", _activityQueue.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduler tick");
            }
        }

        private async void MaintenanceTick(object state)
        {
            try
            {
                await PerformMaintenanceAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in maintenance tick");
            }
        }

        private async Task PerformMaintenanceAsync()
        {
            lock (_lockObject)
            {
                // Clean up old executions;
                var cutoffTime = DateTime.UtcNow - _config.ExecutionHistoryRetention;
                var oldExecutions = _executions.Where(e => e.Value.EndTime < cutoffTime).ToList();

                foreach (var oldExecution in oldExecutions)
                {
                    _executions.Remove(oldExecution.Key);
                    _statistics.HistoryCleaned++;
                }

                // Check for stuck executions;
                var stuckCutoff = DateTime.UtcNow - _config.MaxExecutionTime;
                var stuckExecutions = _executions;
                    .Where(e => e.Value.EndTime == null && e.Value.ActualStartTime < stuckCutoff)
                    .ToList();

                foreach (var stuckExecution in stuckExecutions)
                {
                    _logger.LogWarning("Stuck execution detected: {ExecutionId}", stuckExecution.Key);
                    stuckExecution.Value.CancellationSource?.Cancel();
                    stuckExecution.Value.Result = ExecutionResult.Timeout;
                    stuckExecution.Value.EndTime = DateTime.UtcNow;
                    _statistics.TimeoutCount++;
                }

                _logger.LogDebug("Maintenance completed: cleaned {CleanedCount} old executions, found {StuckCount} stuck executions",
                    oldExecutions.Count, stuckExecutions.Count);
            }

            // Persist state if configured;
            if (_config.PersistActivities)
            {
                await PersistSchedulerStateAsync();
            }
        }

        private async Task ExecuteActivityAsync(ScheduledActivity activity, bool isForced = false)
        {
            if (activity == null)
                return;

            var executionId = $"{activity.ActivityId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            var executionContext = new ActivityExecutionContext;
            {
                ExecutionId = executionId,
                Activity = activity,
                ScheduledTime = DateTime.UtcNow.Date.Add(activity.StartTime),
                ActualStartTime = DateTime.UtcNow,
                NpcState = await _npcStateProvider.GetStateAsync(activity.ActivityId),
                WorldState = await _worldStateProvider.GetStateAsync()
            };

            lock (_lockObject)
            {
                _executions[executionId] = executionContext;
                activity.Status = ActivityStatus.Executing;
                activity.LastExecuted = DateTime.UtcNow;

                if (isForced)
                {
                    _statistics.ForcedExecutions++;
                }
                else;
                {
                    _statistics.ScheduledExecutions++;
                }
            }

            // Publish execution started event;
            await _eventBus.PublishAsync(new ActivityExecutionStartedEvent;
            {
                ExecutionId = executionId,
                ActivityId = activity.ActivityId,
                StartTime = DateTime.UtcNow,
                IsForced = isForced;
            });

            _logger.LogInformation("Activity execution started: {ActivityName} ({ExecutionId})",
                activity.ActivityName, executionId);

            try
            {
                // Check preconditions;
                if (activity.Condition != null &&
                    !activity.Condition.Evaluate(executionContext.NpcState, executionContext.WorldState))
                {
                    throw new PreconditionFailedException($"Preconditions not met for activity {activity.ActivityName}");
                }

                // Find appropriate execution strategy;
                var strategy = _executionStrategies;
                    .FirstOrDefault(s => s.CanExecute(activity.Type))
                    ?? _executionStrategies.First(s => s is DefaultExecutionStrategy);

                // Execute activity;
                var result = await strategy.ExecuteAsync(activity, executionContext,
                    executionContext.CancellationSource.Token);

                executionContext.Result = result.Result;
                executionContext.ExecutionData = result.ExecutionData;

                // Update postconditions;
                if (activity.Condition?.Postconditions != null)
                {
                    await UpdatePostconditionsAsync(activity, executionContext);
                }

                // Handle recurring activities;
                if (activity.IsRecurring && activity.RecurrenceInterval.HasValue)
                {
                    await RescheduleRecurringActivityAsync(activity);
                }

                _logger.LogInformation("Activity execution completed: {ActivityName} - Result: {Result}",
                    activity.ActivityName, result.Result);

                _statistics.SuccessCount++;
            }
            catch (OperationCanceledException)
            {
                executionContext.Result = ExecutionResult.Cancelled;
                _logger.LogInformation("Activity execution cancelled: {ActivityName}", activity.ActivityName);
                _statistics.CancelledCount++;
            }
            catch (Exception ex)
            {
                executionContext.Result = ExecutionResult.Failed;
                executionContext.ExecutionData["Error"] = ex.Message;
                _logger.LogError(ex, "Activity execution failed: {ActivityName}", activity.ActivityName);
                _statistics.FailedCount++;

                // Store in short-term memory for learning;
                await _shortTermMemory.StoreAsync($"activity_failure_{activity.ActivityId}", new;
                {
                    ActivityId = activity.ActivityId,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow,
                    ActivityType = activity.Type;
                });
            }
            finally
            {
                executionContext.EndTime = DateTime.UtcNow;
                activity.Status = activity.IsRecurring ? ActivityStatus.Pending : ActivityStatus.Completed;

                // Calculate execution duration;
                var duration = executionContext.EndTime.Value - executionContext.ActualStartTime;
                _metrics.Histogram("activity.execution_duration", duration.TotalMilliseconds, new Dictionary<string, string>
                {
                    ["type"] = activity.Type.ToString(),
                    ["result"] = executionContext.Result.ToString()
                });

                // Publish execution completed event;
                await _eventBus.PublishAsync(new ActivityExecutionCompletedEvent;
                {
                    ExecutionId = executionId,
                    ActivityId = activity.ActivityId,
                    EndTime = DateTime.UtcNow,
                    Result = executionContext.Result,
                    Duration = duration;
                });
            }
        }

        private async Task RescheduleRecurringActivityAsync(ScheduledActivity activity)
        {
            var nextActivity = activity.DeepClone();
            nextActivity.StartTime = nextActivity.StartTime.Add(nextActivity.RecurrenceInterval.Value);

            // Check if next occurrence is within valid time range;
            if (nextActivity.StartTime.TotalDays >= 1)
            {
                nextActivity.StartTime = TimeSpan.FromHours(0); // Reset to start of day;
            }

            await ScheduleActivityAsync(nextActivity);
        }

        private bool ValidateActivity(ScheduledActivity activity)
        {
            if (string.IsNullOrEmpty(activity.ActivityName))
                return false;

            if (activity.Duration <= TimeSpan.Zero)
                return false;

            if (activity.StartTime.TotalDays >= 1)
                return false;

            if (activity.IsRecurring && (!activity.RecurrenceInterval.HasValue || activity.RecurrenceInterval <= TimeSpan.Zero))
                return false;

            return true;
        }

        private bool HasScheduleConflict(ScheduledActivity newActivity)
        {
            if (!_config.CheckScheduleConflicts)
                return false;

            var newStart = newActivity.StartTime;
            var newEnd = newActivity.StartTime + newActivity.Duration;

            foreach (var existingActivity in _activities.Values)
            {
                if (existingActivity.Status != ActivityStatus.Pending)
                    continue;

                var existingStart = existingActivity.StartTime;
                var existingEnd = existingActivity.StartTime + existingActivity.Duration;

                // Check for time overlap;
                if (newStart < existingEnd && newEnd > existingStart)
                {
                    // Check if same days;
                    if (newActivity.ValidDays.Intersect(existingActivity.ValidDays).Any())
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsActivityDue(ScheduledActivity activity, DateTime currentTime)
        {
            var currentTimeOfDay = currentTime.TimeOfDay;

            // Check if today is a valid day;
            if (!activity.ValidDays.Contains(currentTime.DayOfWeek))
                return false;

            // Check if activity is within execution window;
            var executionWindowEnd = activity.StartTime + _config.ExecutionWindow;

            return currentTimeOfDay >= activity.StartTime &&
                   currentTimeOfDay <= executionWindowEnd &&
                   activity.Status == ActivityStatus.Pending;
        }

        private bool IsActivityForDate(ScheduledActivity activity, DateTime date)
        {
            return activity.ValidDays.Contains(date.DayOfWeek) &&
                   activity.Status == ActivityStatus.Pending;
        }

        private bool IsActivityDueSoon(ScheduledActivity activity, TimeSpan soonThreshold)
        {
            var now = DateTime.UtcNow;
            var activityTimeToday = now.Date.Add(activity.StartTime);

            if (activityTimeToday < now)
                activityTimeToday = activityTimeToday.AddDays(1);

            return (activityTimeToday - now) <= soonThreshold &&
                   activity.ValidDays.Contains(activityTimeToday.DayOfWeek);
        }

        private string GenerateActivityId(string activityName)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            return $"{activityName.ToLower().Replace(" ", "_")}_{timestamp}_{random}";
        }

        private void RegisterDefaultStrategies()
        {
            _executionStrategies.Add(new DefaultExecutionStrategy(_logger, _eventBus));
            _executionStrategies.Add(new CombatActivityStrategy(_logger, _eventBus));
            _executionStrategies.Add(new SocialActivityStrategy(_logger, _eventBus));
            _executionStrategies.Add(new MaintenanceActivityStrategy(_logger, _eventBus));
            _executionStrategies.Add(new ExplorationActivityStrategy(_logger, _eventBus));
        }

        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<WorldStateChangedEvent>(async e =>
            {
                await OnWorldStateChangedAsync(e);
            });

            _eventBus.Subscribe<NPCStateChangedEvent>(async e =>
            {
                await OnNPCStateChangedAsync(e);
            });
        }

        private async Task OnWorldStateChangedAsync(WorldStateChangedEvent e)
        {
            // Reevaluate activities based on world state changes;
            lock (_lockObject)
            {
                var affectedActivities = _activities.Values;
                    .Where(a => a.Condition != null &&
                               a.Status == ActivityStatus.Pending)
                    .ToList();

                foreach (var activity in affectedActivities)
                {
                    // Could trigger immediate execution or cancellation;
                    _logger.LogDebug("World state change may affect activity: {ActivityId}", activity.ActivityId);
                }
            }
        }

        private async Task OnNPCStateChangedAsync(NPCStateChangedEvent e)
        {
            // Adjust priorities or cancel activities based on NPC state;
            lock (_lockObject)
            {
                var npcActivities = _activities.Values;
                    .Where(a => a.ActivityId.Contains(e.NpcId) &&
                               a.Status == ActivityStatus.Pending)
                    .ToList();

                foreach (var activity in npcActivities)
                {
                    // Example: Lower priority if NPC is tired;
                    if (e.NewState.EnergyLevel < 20 && activity.EnergyCost > 10)
                    {
                        activity.Priority = ActivityPriority.Low;
                        _logger.LogDebug("Lowered priority for activity {ActivityId} due to low energy",
                            activity.ActivityId);
                    }
                }
            }
        }

        private async Task HandleExecutionErrorAsync(ScheduledActivity activity, Exception exception)
        {
            await _eventBus.PublishAsync(new ActivityErrorEvent;
            {
                ActivityId = activity.ActivityId,
                ErrorMessage = exception.Message,
                ErrorTime = DateTime.UtcNow,
                StackTrace = exception.StackTrace;
            });

            // Implement retry logic if configured;
            if (_config.MaxRetryCount > 0)
            {
                var retryCount = activity.Parameters.TryGetValue("RetryCount", out var count) ? (int)count : 0;

                if (retryCount < _config.MaxRetryCount)
                {
                    retryCount++;
                    activity.Parameters["RetryCount"] = retryCount;

                    // Reschedule with exponential backoff;
                    var backoffTime = TimeSpan.FromMinutes(Math.Pow(2, retryCount));
                    activity.StartTime = activity.StartTime.Add(backoffTime);

                    await ScheduleActivityAsync(activity);
                    _logger.LogInformation("Activity {ActivityId} rescheduled for retry #{RetryCount}",
                        activity.ActivityId, retryCount);
                }
            }
        }

        private async Task UpdatePostconditionsAsync(ScheduledActivity activity, ActivityExecutionContext context)
        {
            // Update NPC state based on activity completion;
            foreach (var condition in activity.Condition.Postconditions)
            {
                await condition.ApplyAsync(context.NpcState, context.WorldState);
            }
        }

        private async Task LoadPersistedActivitiesAsync()
        {
            // Implementation would load from database or file storage;
            if (_config.PersistActivities)
            {
                _logger.LogInformation("Loading persisted activities...");
                // TODO: Implement persistence layer integration;
            }
        }

        private async Task PersistActivityAsync(ScheduledActivity activity)
        {
            // Implementation would save to database or file storage;
            // TODO: Implement persistence layer integration;
            await Task.CompletedTask;
        }

        private async Task PersistSchedulerStateAsync()
        {
            // Implementation would save scheduler state;
            // TODO: Implement persistence layer integration;
            await Task.CompletedTask;
        }

        #endregion;

        #region Supporting Classes and Enums;

        public enum ActivityType;
        {
            Combat,
            Social,
            Exploration,
            Maintenance,
            Training,
            Crafting,
            Trading,
            Resting,
            Custom;
        }

        public enum ActivityPriority;
        {
            Critical = 0,
            High = 1,
            Medium = 2,
            Low = 3,
            Background = 4;
        }

        public enum ActivityStatus;
        {
            Pending,
            Executing,
            Completed,
            Cancelled,
            Failed,
            Skipped;
        }

        public enum ExecutionResult;
        {
            Success,
            Failed,
            Cancelled,
            Timeout,
            Skipped;
        }

        public class ActivityStatistics;
        {
            public int TotalActivities { get; set; }
            public int QueuedActivities { get; set; }
            public int ActiveExecutions { get; set; }
            public int UpcomingActivities { get; set; }
            public int ScheduledExecutions { get; set; }
            public int ForcedExecutions { get; set; }
            public int SuccessCount { get; set; }
            public int FailedCount { get; set; }
            public int CancelledCount { get; set; }
            public int TimeoutCount { get; set; }
            public int HistoryCleaned { get; set; }
            public DateTime LastMaintenance { get; set; }

            public ActivityStatistics Clone()
            {
                return (ActivityStatistics)MemberwiseClone();
            }
        }

        public class PauseResumeResult;
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class ActivitySchedulerConfig;
        {
            public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(30);
            public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromMinutes(5);
            public TimeSpan ExecutionWindow { get; set; } = TimeSpan.FromMinutes(15);
            public TimeSpan MaxExecutionTime { get; set; } = TimeSpan.FromMinutes(60);
            public TimeSpan ExecutionHistoryRetention { get; set; } = TimeSpan.FromDays(7);
            public int MaxRetryCount { get; set; } = 3;
            public bool PersistActivities { get; set; } = true;
            public bool CheckScheduleConflicts { get; set; } = true;
            public bool EnableAdaptiveScheduling { get; set; } = true;
            public int MaxQueueSize { get; set; } = 1000;
        }

        #endregion;
    }

    #region Interfaces for Dependency Injection;

    public interface INPCStateProvider;
    {
        Task<NPCState> GetStateAsync(string npcId);
    }

    public interface IWorldStateProvider;
    {
        Task<WorldState> GetStateAsync();
    }

    public interface IActivityExecutionStrategy;
    {
        bool CanExecute(ActivityType type);
        Task<ActivityExecutionResult> ExecuteAsync(
            ScheduledActivity activity,
            ActivityExecutionContext context,
            CancellationToken cancellationToken);
    }

    #endregion;

    #region Event Definitions;

    public class SchedulerInitializedEvent : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public int ScheduledActivityCount { get; set; }
    }

    public class ActivityScheduledEvent : IEvent;
    {
        public string ActivityId { get; set; }
        public string ActivityName { get; set; }
        public DateTime ScheduledTime { get; set; }
        public ActivityPriority Priority { get; set; }
    }

    public class ActivityUpdatedEvent : IEvent;
    {
        public string ActivityId { get; set; }
        public DateTime UpdateTime { get; set; }
    }

    public class ActivityCancelledEvent : IEvent;
    {
        public string ActivityId { get; set; }
        public DateTime CancellationTime { get; set; }
    }

    public class ActivityExecutionStartedEvent : IEvent;
    {
        public string ExecutionId { get; set; }
        public string ActivityId { get; set; }
        public DateTime StartTime { get; set; }
        public bool IsForced { get; set; }
    }

    public class ActivityExecutionCompletedEvent : IEvent;
    {
        public string ExecutionId { get; set; }
        public string ActivityId { get; set; }
        public DateTime EndTime { get; set; }
        public ExecutionResult Result { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public class ActivityErrorEvent : IEvent;
    {
        public string ActivityId { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime ErrorTime { get; set; }
        public string StackTrace { get; set; }
    }

    public class WorldStateChangedEvent : IEvent;
    {
        public WorldState NewState { get; set; }
        public WorldState OldState { get; set; }
        public DateTime ChangeTime { get; set; }
    }

    public class NPCStateChangedEvent : IEvent;
    {
        public string NpcId { get; set; }
        public NPCState NewState { get; set; }
        public NPCState OldState { get; set; }
        public DateTime ChangeTime { get; set; }
    }

    #endregion;

    #region Execution Strategy Implementations;

    public class DefaultExecutionStrategy : IActivityExecutionStrategy;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;

        public DefaultExecutionStrategy(ILogger logger, IEventBus eventBus)
        {
            _logger = logger;
            _eventBus = eventBus;
        }

        public bool CanExecute(ActivityType type)
        {
            return type == ActivityType.Custom;
        }

        public async Task<ActivityExecutionResult> ExecuteAsync(
            ScheduledActivity activity,
            ActivityExecutionContext context,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Executing custom activity: {ActivityName}", activity.ActivityName);

            // Default implementation - can be overridden by specific strategies;
            await Task.Delay(100, cancellationToken); // Simulate work;

            return new ActivityExecutionResult;
            {
                Result = ExecutionResult.Success,
                ExecutionData = new Dictionary<string, object>
                {
                    ["DefaultExecution"] = true,
                    ["CompletionTime"] = DateTime.UtcNow;
                }
            };
        }
    }

    public class CombatActivityStrategy : IActivityExecutionStrategy;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;

        public CombatActivityStrategy(ILogger logger, IEventBus eventBus)
        {
            _logger = logger;
            _eventBus = eventBus;
        }

        public bool CanExecute(ActivityType type)
        {
            return type == ActivityType.Combat;
        }

        public async Task<ActivityExecutionResult> ExecuteAsync(
            ScheduledActivity activity,
            ActivityExecutionContext context,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Executing combat activity: {ActivityName}", activity.ActivityName);

            // Implement combat logic;
            var combatResult = await SimulateCombatAsync(activity, context, cancellationToken);

            return new ActivityExecutionResult;
            {
                Result = combatResult.Success ? ExecutionResult.Success : ExecutionResult.Failed,
                ExecutionData = new Dictionary<string, object>
                {
                    ["CombatResult"] = combatResult,
                    ["DamageDealt"] = combatResult.DamageDealt,
                    ["ExperienceGained"] = combatResult.ExperienceGained;
                }
            };
        }

        private async Task<CombatResult> SimulateCombatAsync(
            ScheduledActivity activity,
            ActivityExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Simulate combat;
            await Task.Delay(500, cancellationToken);

            return new CombatResult;
            {
                Success = true,
                DamageDealt = 100,
                ExperienceGained = 50,
                Loot = new List<string> { "Sword", "Shield" }
            };
        }
    }

    // Additional strategy implementations (SocialActivityStrategy, etc.)
    // would follow similar patterns...

    #endregion;

    #region Supporting Data Classes;

    public class NPCState;
    {
        public string NpcId { get; set; }
        public int EnergyLevel { get; set; }
        public int Health { get; set; }
        public int Mood { get; set; }
        public List<string> Inventory { get; set; }
        public Dictionary<string, object> Attributes { get; set; }
        public Location CurrentLocation { get; set; }

        public NPCState()
        {
            Inventory = new List<string>();
            Attributes = new Dictionary<string, object>();
        }
    }

    public class WorldState;
    {
        public DateTime GameTime { get; set; }
        public WeatherCondition Weather { get; set; }
        public int DayNumber { get; set; }
        public Dictionary<string, LocationState> Locations { get; set; }
        public List<WorldEvent> ActiveEvents { get; set; }

        public WorldState()
        {
            Locations = new Dictionary<string, LocationState>();
            ActiveEvents = new List<WorldEvent>();
        }
    }

    public class Location;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public Coordinates Position { get; set; }
    }

    public class Coordinates;
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    public class LocationState;
    {
        public string LocationId { get; set; }
        public int NpcCount { get; set; }
        public bool IsSafe { get; set; }
        public List<string> AvailableActivities { get; set; }
    }

    public class WorldEvent;
    {
        public string EventId { get; set; }
        public string Type { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    public class Condition;
    {
        public string Type { get; set; }
        public string Property { get; set; }
        public object ExpectedValue { get; set; }
        public ComparisonOperator Operator { get; set; }

        public bool Evaluate(NPCState npcState, WorldState worldState)
        {
            // Implementation would evaluate the condition;
            return true;
        }

        public Task ApplyAsync(NPCState npcState, WorldState worldState)
        {
            // Implementation would apply the condition;
            return Task.CompletedTask;
        }
    }

    public enum ComparisonOperator;
    {
        Equals,
        NotEquals,
        GreaterThan,
        LessThan,
        Contains,
        NotContains;
    }

    public enum WeatherCondition;
    {
        Sunny,
        Rainy,
        Stormy,
        Snowy,
        Foggy;
    }

    public class ActivityExecutionResult;
    {
        public ExecutionResult Result { get; set; }
        public Dictionary<string, object> ExecutionData { get; set; }

        public ActivityExecutionResult()
        {
            ExecutionData = new Dictionary<string, object>();
        }
    }

    public class CombatResult;
    {
        public bool Success { get; set; }
        public int DamageDealt { get; set; }
        public int ExperienceGained { get; set; }
        public List<string> Loot { get; set; }

        public CombatResult()
        {
            Loot = new List<string>();
        }
    }

    #endregion;

    #region Custom Exceptions;

    public class InvalidActivityException : Exception
    {
        public InvalidActivityException(string message) : base(message) { }
    }

    public class ScheduleConflictException : Exception
    {
        public ScheduleConflictException(string message) : base(message) { }
    }

    public class PreconditionFailedException : Exception
    {
        public PreconditionFailedException(string message) : base(message) { }
    }

    #endregion;
}
