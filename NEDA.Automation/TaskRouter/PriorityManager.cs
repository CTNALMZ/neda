using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Automation.TaskRouter;
{
    /// <summary>
    /// Task priority levels with detailed descriptions;
    /// </summary>
    public enum TaskPriority;
    {
        /// <summary>
        /// Emergency - Requires immediate attention, system-critical;
        /// </summary>
        Emergency = 0,

        /// <summary>
        /// High - Important tasks that affect user experience;
        /// </summary>
        High = 1,

        /// <summary>
        /// Medium - Standard priority tasks;
        /// </summary>
        Medium = 2,

        /// <summary>
        /// Low - Background or non-urgent tasks;
        /// </summary>
        Low = 3,

        /// <summary>
        /// Idle - Tasks that should only run when system is idle;
        /// </summary>
        Idle = 4;
    }

    /// <summary>
    /// Represents a task with priority metadata;
    /// </summary>
    public class PrioritizedTask;
    {
        public Guid TaskId { get; } = Guid.NewGuid();
        public string TaskName { get; set; }
        public Func<CancellationToken, Task> TaskAction { get; set; }
        public TaskPriority Priority { get; set; }
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public DateTime? ScheduledTime { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; } = 3;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
        public Dictionary<string, object> Metadata { get; } = new Dictionary<string, object>();
        public string Category { get; set; }
        public string Owner { get; set; }
        public double UrgencyScore { get; set; }
        public double ImportanceScore { get; set; }

        public double TotalPriorityScore => CalculatePriorityScore();

        private double CalculatePriorityScore()
        {
            const double PRIORITY_WEIGHT = 0.4;
            const double URGENCY_WEIGHT = 0.3;
            const double IMPORTANCE_WEIGHT = 0.2;
            const double AGE_WEIGHT = 0.1;

            var priorityScore = (int)(TaskPriority.Idle - Priority) / (double)(int)TaskPriority.Idle;
            var ageInHours = (DateTime.UtcNow - CreatedTime).TotalHours;
            var ageScore = Math.Min(ageInHours / 24.0, 1.0);

            return (priorityScore * PRIORITY_WEIGHT) +
                   (UrgencyScore * URGENCY_WEIGHT) +
                   (ImportanceScore * IMPORTANCE_WEIGHT) +
                   (ageScore * AGE_WEIGHT);
        }
    }

    /// <summary>
    /// Priority calculation strategy interface;
    /// </summary>
    public interface IPriorityStrategy;
    {
        double CalculatePriority(PrioritizedTask task);
        void UpdateStrategyBasedOnSystemMetrics(SystemMetrics metrics);
    }

    /// <summary>
    /// System metrics for adaptive priority management;
    /// </summary>
    public class SystemMetrics;
    {
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public int ActiveTasks { get; set; }
        public int PendingTasks { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Adaptive priority strategy implementation;
    /// </summary>
    public class AdaptivePriorityStrategy : IPriorityStrategy;
    {
        private readonly ILogger _logger;
        private double _systemLoadFactor = 1.0;
        private readonly object _lock = new object();

        public AdaptivePriorityStrategy(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public double CalculatePriority(PrioritizedTask task)
        {
            lock (_lock)
            {
                var baseScore = task.TotalPriorityScore;
                var adjustedScore = baseScore * _systemLoadFactor;

                // Apply category-specific adjustments;
                if (task.Category == "SystemCritical")
                    adjustedScore *= 1.5;
                else if (task.Category == "UserInteractive")
                    adjustedScore *= 1.2;

                return Math.Max(0.1, Math.Min(1.0, adjustedScore));
            }
        }

        public void UpdateStrategyBasedOnSystemMetrics(SystemMetrics metrics)
        {
            lock (_lock)
            {
                // Adjust based on system load;
                var load = (metrics.CpuUsage + metrics.MemoryUsage) / 2.0;

                if (load > 80.0)
                    _systemLoadFactor = 0.7; // Reduce priority under high load;
                else if (load > 60.0)
                    _systemLoadFactor = 0.85;
                else if (load < 30.0)
                    _systemLoadFactor = 1.3; // Increase priority when system is idle;
                else;
                    _systemLoadFactor = 1.0;

                _logger.LogInformation($"Priority strategy updated. System load: {load:F1}%, Factor: {_systemLoadFactor:F2}");
            }
        }
    }

    /// <summary>
    /// Main Priority Manager for intelligent task prioritization;
    /// </summary>
    public class PriorityManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Guid, PrioritizedTask> _pendingTasks = new();
        private readonly ConcurrentDictionary<string, List<PrioritizedTask>> _categorizedTasks = new();
        private readonly PriorityQueue<PrioritizedTask, double> _priorityQueue;
        private readonly IPriorityStrategy _priorityStrategy;
        private readonly SystemMetricsCollector _metricsCollector;
        private readonly Timer _metricsTimer;
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _queueLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private bool _isDisposed;

        // Statistics;
        private long _totalTasksProcessed;
        private long _highPriorityTasksProcessed;
        private DateTime _startTime = DateTime.UtcNow;

        /// <summary>
        /// Event fired when task priority changes;
        /// </summary>
        public event EventHandler<PriorityChangedEventArgs> PriorityChanged;

        /// <summary>
        /// Event fired when task is completed;
        /// </summary>
        public event EventHandler<TaskCompletedEventArgs> TaskCompleted;

        public PriorityManager(ILogger logger, IPriorityStrategy priorityStrategy = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _priorityStrategy = priorityStrategy ?? new AdaptivePriorityStrategy(logger);
            _priorityQueue = new PriorityQueue<PrioritizedTask, double>(Comparer<double>.Create((x, y) => y.CompareTo(x)));
            _metricsCollector = new SystemMetricsCollector(logger);

            // Start metrics collection every 30 seconds;
            _metricsTimer = new Timer(CollectSystemMetrics, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            // Start cleanup timer every hour;
            _cleanupTimer = new Timer(CleanupOldTasks, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            _logger.LogInformation("PriorityManager initialized successfully");
        }

        /// <summary>
        /// Add a task with priority;
        /// </summary>
        public async Task<Guid> AddTaskAsync(PrioritizedTask task, CancellationToken cancellationToken = default)
        {
            ValidateTask(task);

            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                // Calculate final priority score;
                task.UrgencyScore = CalculateUrgencyScore(task);
                task.ImportanceScore = CalculateImportanceScore(task);

                var priorityScore = _priorityStrategy.CalculatePriority(task);

                _pendingTasks[task.TaskId] = task;
                AddToCategory(task);
                _priorityQueue.Enqueue(task, priorityScore);

                _logger.LogInformation($"Task added: {task.TaskName}, Priority: {task.Priority}, " +
                                      $"Score: {priorityScore:F2}, Category: {task.Category}");

                OnPriorityChanged(task, priorityScore);

                return task.TaskId;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Get next highest priority task;
        /// </summary>
        public async Task<PrioritizedTask> GetNextTaskAsync(CancellationToken cancellationToken = default)
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                if (_priorityQueue.Count == 0)
                    return null;

                if (_priorityQueue.TryDequeue(out var task, out var priorityScore))
                {
                    _pendingTasks.TryRemove(task.TaskId, out _);
                    RemoveFromCategory(task);

                    _logger.LogDebug($"Task dequeued: {task.TaskName}, Priority Score: {priorityScore:F2}");
                    return task;
                }

                return null;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Update task priority dynamically;
        /// </summary>
        public async Task<bool> UpdateTaskPriorityAsync(Guid taskId, TaskPriority newPriority,
            CancellationToken cancellationToken = default)
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                if (!_pendingTasks.TryGetValue(taskId, out var task))
                {
                    _logger.LogWarning($"Task not found for priority update: {taskId}");
                    return false;
                }

                var oldPriority = task.Priority;
                task.Priority = newPriority;

                // Recalculate and re-enqueue;
                var priorityScore = _priorityStrategy.CalculatePriority(task);
                _priorityQueue.Enqueue(task, priorityScore);

                _logger.LogInformation($"Task priority updated: {task.TaskName}, " +
                                      $"Old: {oldPriority}, New: {newPriority}, Score: {priorityScore:F2}");

                OnPriorityChanged(task, priorityScore);
                return true;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Promote task to higher priority;
        /// </summary>
        public async Task<bool> PromoteTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                if (!_pendingTasks.TryGetValue(taskId, out var task))
                    return false;

                var currentPriority = (int)task.Priority;
                if (currentPriority > 0)
                {
                    task.Priority = (TaskPriority)(currentPriority - 1);

                    // Recalculate and re-enqueue;
                    var priorityScore = _priorityStrategy.CalculatePriority(task);
                    _priorityQueue.Enqueue(task, priorityScore);

                    _logger.LogInformation($"Task promoted: {task.TaskName}, New Priority: {task.Priority}");
                    return true;
                }

                return false;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Demote task to lower priority;
        /// </summary>
        public async Task<bool> DemoteTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                if (!_pendingTasks.TryGetValue(taskId, out var task))
                    return false;

                var currentPriority = (int)task.Priority;
                if (currentPriority < (int)TaskPriority.Idle)
                {
                    task.Priority = (TaskPriority)(currentPriority + 1);

                    // Recalculate and re-enqueue;
                    var priorityScore = _priorityStrategy.CalculatePriority(task);
                    _priorityQueue.Enqueue(task, priorityScore);

                    _logger.LogInformation($"Task demoted: {task.TaskName}, New Priority: {task.Priority}");
                    return true;
                }

                return false;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Remove a task from the queue;
        /// </summary>
        public async Task<bool> RemoveTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                if (_pendingTasks.TryRemove(taskId, out var task))
                {
                    RemoveFromCategory(task);
                    _logger.LogInformation($"Task removed: {task.TaskName}");
                    return true;
                }

                return false;
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Get task statistics;
        /// </summary>
        public PriorityManagerStats GetStatistics()
        {
            var now = DateTime.UtcNow;
            var uptime = now - _startTime;

            return new PriorityManagerStats;
            {
                TotalTasksProcessed = _totalTasksProcessed,
                HighPriorityTasksProcessed = _highPriorityTasksProcessed,
                PendingTasksCount = _pendingTasks.Count,
                QueueSize = _priorityQueue.Count,
                Uptime = uptime,
                AverageTasksPerHour = uptime.TotalHours > 0 ?
                    _totalTasksProcessed / uptime.TotalHours : 0,
                TasksByCategory = GetTasksByCategory(),
                TasksByPriority = GetTasksByPriority()
            };
        }

        /// <summary>
        /// Get all pending tasks (for monitoring/debugging)
        /// </summary>
        public IReadOnlyList<PrioritizedTask> GetPendingTasks()
        {
            return _pendingTasks.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Get tasks by category;
        /// </summary>
        public Dictionary<string, int> GetTasksByCategory()
        {
            return _categorizedTasks.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Count;
            );
        }

        /// <summary>
        /// Get tasks by priority level;
        /// </summary>
        public Dictionary<TaskPriority, int> GetTasksByPriority()
        {
            return _pendingTasks.Values;
                .GroupBy(t => t.Priority)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Emergency priority boost for critical tasks;
        /// </summary>
        public async Task EmergencyPriorityBoostAsync(string category, CancellationToken cancellationToken = default)
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                var tasksToBoost = _pendingTasks.Values;
                    .Where(t => t.Category == category && t.Priority > TaskPriority.Emergency)
                    .ToList();

                foreach (var task in tasksToBoost)
                {
                    task.Priority = TaskPriority.Emergency;
                    task.UrgencyScore = 1.0;

                    // Re-enqueue with highest priority;
                    _priorityQueue.Enqueue(task, 1.0);
                }

                if (tasksToBoost.Count > 0)
                {
                    _logger.LogWarning($"Emergency priority boost applied to {tasksToBoost.Count} tasks in category: {category}");
                }
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Cleanup old/completed tasks;
        /// </summary>
        public async Task<int> CleanupStaleTasksAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
        {
            var cutoffTime = DateTime.UtcNow - olderThan;
            var staleTasks = _pendingTasks.Values;
                .Where(t => t.CreatedTime < cutoffTime)
                .ToList();

            int removedCount = 0;

            foreach (var task in staleTasks)
            {
                if (await RemoveTaskAsync(task.TaskId, cancellationToken))
                    removedCount++;
            }

            if (removedCount > 0)
            {
                _logger.LogInformation($"Cleaned up {removedCount} stale tasks older than {olderThan}");
            }

            return removedCount;
        }

        /// <summary>
        /// Reset all priorities to default;
        /// </summary>
        public async Task ResetPrioritiesAsync(CancellationToken cancellationToken = default)
        {
            await _queueLock.WaitAsync(cancellationToken);
            try
            {
                var tasks = _pendingTasks.Values.ToList();
                _pendingTasks.Clear();
                _categorizedTasks.Clear();
                _priorityQueue.Clear();

                foreach (var task in tasks)
                {
                    task.Priority = TaskPriority.Medium;
                    task.UrgencyScore = 0.5;
                    task.ImportanceScore = 0.5;

                    var priorityScore = _priorityStrategy.CalculatePriority(task);
                    _pendingTasks[task.TaskId] = task;
                    AddToCategory(task);
                    _priorityQueue.Enqueue(task, priorityScore);
                }

                _logger.LogInformation($"Reset priorities for {tasks.Count} tasks");
            }
            finally
            {
                _queueLock.Release();
            }
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _cts.Cancel();
            _metricsTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _queueLock?.Dispose();
            _cts.Dispose();

            _isDisposed = true;
            _logger.LogInformation("PriorityManager disposed");
        }

        #region Private Methods;

        private void ValidateTask(PrioritizedTask task)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (string.IsNullOrWhiteSpace(task.TaskName))
                throw new ArgumentException("Task name cannot be empty", nameof(task));

            if (task.TaskAction == null)
                throw new ArgumentException("Task action cannot be null", nameof(task));
        }

        private double CalculateUrgencyScore(PrioritizedTask task)
        {
            double score = 0.0;

            // Time-based urgency;
            if (task.ScheduledTime.HasValue)
            {
                var timeUntilScheduled = (task.ScheduledTime.Value - DateTime.UtcNow).TotalHours;
                score += Math.Max(0, 1.0 - (timeUntilScheduled / 24.0));
            }

            // Retry-based urgency;
            if (task.RetryCount > 0)
                score += 0.1 * Math.Min(task.RetryCount, 5);

            // Priority-based urgency;
            score += ((int)TaskPriority.Idle - (int)task.Priority) * 0.1;

            return Math.Min(score, 1.0);
        }

        private double CalculateImportanceScore(PrioritizedTask task)
        {
            double score = 0.0;

            // Category importance;
            if (task.Category == "SystemCritical")
                score += 0.8;
            else if (task.Category == "UserInteractive")
                score += 0.6;
            else if (task.Category == "Background")
                score += 0.3;
            else;
                score += 0.5;

            // Owner importance (system tasks are more important)
            if (task.Owner == "System")
                score += 0.2;

            return Math.Min(score, 1.0);
        }

        private void AddToCategory(PrioritizedTask task)
        {
            if (string.IsNullOrEmpty(task.Category))
                task.Category = "Default";

            _categorizedTasks.AddOrUpdate(
                task.Category,
                new List<PrioritizedTask> { task },
                (key, existingList) =>
                {
                    existingList.Add(task);
                    return existingList;
                });
        }

        private void RemoveFromCategory(PrioritizedTask task)
        {
            if (!string.IsNullOrEmpty(task.Category) &&
                _categorizedTasks.TryGetValue(task.Category, out var taskList))
            {
                taskList.Remove(task);
                if (taskList.Count == 0)
                    _categorizedTasks.TryRemove(task.Category, out _);
            }
        }

        private void CollectSystemMetrics(object state)
        {
            try
            {
                var metrics = _metricsCollector.CollectMetrics();
                _priorityStrategy.UpdateStrategyBasedOnSystemMetrics(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error collecting system metrics: {ex.Message}");
            }
        }

        private void CleanupOldTasks(object state)
        {
            try
            {
                _ = CleanupStaleTasksAsync(TimeSpan.FromHours(24), _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during task cleanup: {ex.Message}");
            }
        }

        private void OnPriorityChanged(PrioritizedTask task, double newScore)
        {
            PriorityChanged?.Invoke(this, new PriorityChangedEventArgs;
            {
                TaskId = task.TaskId,
                TaskName = task.TaskName,
                NewPriority = task.Priority,
                PriorityScore = newScore,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnTaskCompleted(PrioritizedTask task, bool success, TimeSpan executionTime)
        {
            Interlocked.Increment(ref _totalTasksProcessed);
            if (task.Priority <= TaskPriority.High)
                Interlocked.Increment(ref _highPriorityTasksProcessed);

            TaskCompleted?.Invoke(this, new TaskCompletedEventArgs;
            {
                TaskId = task.TaskId,
                TaskName = task.TaskName,
                Priority = task.Priority,
                Success = success,
                ExecutionTime = executionTime,
                CompletedTime = DateTime.UtcNow;
            });
        }

        #endregion;

        #region Nested Classes;

        /// <summary>
        /// System metrics collector;
        /// </summary>
        private class SystemMetricsCollector;
        {
            private readonly ILogger _logger;

            public SystemMetricsCollector(ILogger logger)
            {
                _logger = logger;
            }

            public SystemMetrics CollectMetrics()
            {
                try
                {
                    return new SystemMetrics;
                    {
                        CpuUsage = GetCpuUsage(),
                        MemoryUsage = GetMemoryUsage(),
                        ActiveTasks = Process.GetCurrentProcess().Threads.Count,
                        PendingTasks = 0 // Will be set by caller;
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error collecting system metrics: {ex.Message}");
                    return new SystemMetrics(); // Return empty metrics on error;
                }
            }

            private double GetCpuUsage()
            {
                // Simplified - in production use PerformanceCounter or system APIs;
                return 0.0;
            }

            private double GetMemoryUsage()
            {
                var process = Process.GetCurrentProcess();
                var memoryMB = process.WorkingSet64 / (1024.0 * 1024.0);
                var totalMemory = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                return (memoryMB / (memoryMB + totalMemory)) * 100.0;
            }
        }

        #endregion;
    }

    #region Event Args Classes;

    /// <summary>
    /// Event arguments for priority change events;
    /// </summary>
    public class PriorityChangedEventArgs : EventArgs;
    {
        public Guid TaskId { get; set; }
        public string TaskName { get; set; }
        public TaskPriority NewPriority { get; set; }
        public double PriorityScore { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for task completion events;
    /// </summary>
    public class TaskCompletedEventArgs : EventArgs;
    {
        public Guid TaskId { get; set; }
        public string TaskName { get; set; }
        public TaskPriority Priority { get; set; }
        public bool Success { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime CompletedTime { get; set; }
    }

    /// <summary>
    /// Priority manager statistics;
    /// </summary>
    public class PriorityManagerStats;
    {
        public long TotalTasksProcessed { get; set; }
        public long HighPriorityTasksProcessed { get; set; }
        public int PendingTasksCount { get; set; }
        public int QueueSize { get; set; }
        public TimeSpan Uptime { get; set; }
        public double AverageTasksPerHour { get; set; }
        public Dictionary<string, int> TasksByCategory { get; set; }
        public Dictionary<TaskPriority, int> TasksByPriority { get; set; }

        public override string ToString()
        {
            return $"Tasks: {TotalTasksProcessed} total, {PendingTasksCount} pending, " +
                   $"Queue: {QueueSize}, Uptime: {Uptime:hh\\:mm\\:ss}, " +
                   $"Avg/Hour: {AverageTasksPerHour:F1}";
        }
    }

    #endregion;
}
