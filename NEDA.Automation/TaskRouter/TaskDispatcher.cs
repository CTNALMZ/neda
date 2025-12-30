using NEDA.Core.Logging;
using NEDA.Core.Common.Utilities;
using NEDA.Automation.TaskRouter;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace NEDA.Automation.TaskRouter;
{
    /// <summary>
    /// Task dispatch status;
    /// </summary>
    public enum DispatchStatus;
    {
        /// <summary>
        /// Task is pending dispatch;
        /// </summary>
        Pending = 0,

        /// <summary>
        /// Task is being dispatched;
        /// </summary>
        Dispatching = 1,

        /// <summary>
        /// Task has been dispatched successfully;
        /// </summary>
        Dispatched = 2,

        /// <summary>
        /// Task dispatch failed;
        /// </summary>
        Failed = 3,

        /// <summary>
        /// Task was cancelled before dispatch;
        /// </summary>
        Cancelled = 4,

        /// <summary>
        /// Task is being retried;
        /// </summary>
        Retrying = 5;
    }

    /// <summary>
    /// Task dispatch result;
    /// </summary>
    public class DispatchResult;
    {
        public Guid TaskId { get; set; }
        public string TaskName { get; set; }
        public DispatchStatus Status { get; set; }
        public List<string> DestinationIds { get; } = new();
        public DateTime DispatchTime { get; set; }
        public TimeSpan DispatchDuration { get; set; }
        public Exception Error { get; set; }
        public int RetryCount { get; set; }
        public Dictionary<string, object> DispatchMetrics { get; } = new();
        public bool Success => Status == DispatchStatus.Dispatched;

        public override string ToString()
        {
            return $"{TaskName} -> {Status} ({DispatchDuration.TotalMilliseconds}ms)";
        }
    }

    /// <summary>
    /// Task dispatch context;
    /// </summary>
    public class DispatchContext;
    {
        public Guid ContextId { get; } = Guid.NewGuid();
        public PrioritizedTask Task { get; set; }
        public RoutingDecision RoutingDecision { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public Dictionary<string, object> ContextData { get; } = new();
        public int CurrentRetry { get; set; }
        public TimeSpan Timeout { get; set; }

        public bool IsTimedOut => DateTime.UtcNow > CreatedTime + Timeout;
    }

    /// <summary>
    /// Dispatch strategy interface;
    /// </summary>
    public interface IDispatchStrategy;
    {
        Task<DispatchResult> DispatchAsync(
            DispatchContext context,
            IReadOnlyList<RouteDestination> destinations,
            CancellationToken cancellationToken = default);

        Task<DispatchResult> RedispatchAsync(
            DispatchContext context,
            DispatchResult previousResult,
            CancellationToken cancellationToken = default);

        void UpdateStrategy(DispatchMetrics metrics);
    }

    /// <summary>
    /// Dispatch metrics for strategy optimization;
    /// </summary>
    public class DispatchMetrics;
    {
        public int TotalDispatches { get; set; }
        public int SuccessfulDispatches { get; set; }
        public int FailedDispatches { get; set; }
        public int RetryCount { get; set; }
        public double AverageDispatchTimeMs { get; set; }
        public double AverageQueueTimeMs { get; set; }
        public Dictionary<string, int> FailuresByReason { get; } = new();
        public Dictionary<string, int> DispatchesByDestination { get; } = new();
        public DateTime CollectionStartTime { get; } = DateTime.UtcNow;

        public double SuccessRate => TotalDispatches > 0 ?
            (double)SuccessfulDispatches / TotalDispatches * 100 : 0;

        public double AverageRetryRate => TotalDispatches > 0 ?
            (double)RetryCount / TotalDispatches : 0;
    }

    /// <summary>
    /// Intelligent dispatch strategy implementation;
    /// </summary>
    public class IntelligentDispatchStrategy : IDispatchStrategy;
    {
        private readonly ILogger _logger;
        private readonly DispatchMetrics _metrics = new();
        private readonly object _metricsLock = new();
        private readonly Dictionary<string, DestinationDispatchStats> _destinationStats = new();
        private readonly AdaptiveRetryPolicy _retryPolicy;
        private readonly CircuitBreakerManager _circuitBreaker;

        public IntelligentDispatchStrategy(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryPolicy = new AdaptiveRetryPolicy(logger);
            _circuitBreaker = new CircuitBreakerManager(logger);
        }

        public async Task<DispatchResult> DispatchAsync(
            DispatchContext context,
            IReadOnlyList<RouteDestination> destinations,
            CancellationToken cancellationToken = default)
        {
            var result = new DispatchResult;
            {
                TaskId = context.Task.TaskId,
                TaskName = context.Task.TaskName,
                DispatchTime = DateTime.UtcNow;
            };

            try
            {
                ValidateDispatch(context, destinations);

                var startTime = DateTime.UtcNow;
                var successfulDestinations = new List<string>();

                // Check circuit breakers before dispatching;
                foreach (var destination in destinations)
                {
                    if (_circuitBreaker.IsOpen(destination.ExecutorId))
                    {
                        _logger.LogWarning($"Circuit breaker open for {destination.ExecutorId}, skipping dispatch");
                        continue;
                    }
                }

                // Determine dispatch mode;
                var dispatchMode = DetermineDispatchMode(context, destinations);

                // Execute dispatch based on mode;
                switch (dispatchMode)
                {
                    case DispatchMode.Sequential:
                        await DispatchSequential(context, destinations, successfulDestinations, cancellationToken);
                        break;

                    case DispatchMode.Parallel:
                        await DispatchParallel(context, destinations, successfulDestinations, cancellationToken);
                        break;

                    case DispatchMode.Broadcast:
                        await DispatchBroadcast(context, destinations, successfulDestinations, cancellationToken);
                        break;

                    case DispatchMode.FanOut:
                        await DispatchFanOut(context, destinations, successfulDestinations, cancellationToken);
                        break;
                }

                // Update result;
                result.DispatchDuration = DateTime.UtcNow - startTime;
                result.DestinationIds.AddRange(successfulDestinations);

                if (successfulDestinations.Count > 0)
                {
                    result.Status = DispatchStatus.Dispatched;
                    UpdateMetrics(result, true);

                    _logger.LogInformation($"Dispatch successful: {context.Task.TaskName} -> " +
                                          $"{successfulDestinations.Count} destinations, " +
                                          $"Time: {result.DispatchDuration.TotalMilliseconds}ms");
                }
                else;
                {
                    result.Status = DispatchStatus.Failed;
                    result.Error = new Exception("No destinations accepted the task");
                    UpdateMetrics(result, false);

                    _logger.LogWarning($"Dispatch failed: {context.Task.TaskName}, No destinations available");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Status = DispatchStatus.Cancelled;
                result.DispatchDuration = DateTime.UtcNow - result.DispatchTime;
                UpdateMetrics(result, false);

                _logger.LogInformation($"Dispatch cancelled: {context.Task.TaskName}");
                return result;
            }
            catch (Exception ex)
            {
                result.Status = DispatchStatus.Failed;
                result.Error = ex;
                result.DispatchDuration = DateTime.UtcNow - result.DispatchTime;
                UpdateMetrics(result, false);

                _logger.LogError($"Dispatch failed for {context.Task.TaskName}: {ex.Message}");
                return result;
            }
        }

        public async Task<DispatchResult> RedispatchAsync(
            DispatchContext context,
            DispatchResult previousResult,
            CancellationToken cancellationToken = default)
        {
            var result = new DispatchResult;
            {
                TaskId = context.Task.TaskId,
                TaskName = context.Task.TaskName,
                RetryCount = previousResult.RetryCount + 1,
                DispatchTime = DateTime.UtcNow;
            };

            try
            {
                // Check if we should retry
                if (!_retryPolicy.ShouldRetry(context, previousResult))
                {
                    result.Status = DispatchStatus.Failed;
                    result.Error = new Exception("Max retries exceeded or retry not allowed");
                    return result;
                }

                context.CurrentRetry = result.RetryCount;

                // Apply retry delay;
                var delay = _retryPolicy.GetRetryDelay(context, previousResult);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                // Update task metadata for retry
                context.Task.RetryCount = result.RetryCount;
                context.Task.Metadata["LastRetryTime"] = DateTime.UtcNow;
                context.Task.Metadata["RetryAttempt"] = result.RetryCount;

                _logger.LogInformation($"Retrying dispatch: {context.Task.TaskName}, " +
                                      $"Attempt: {result.RetryCount}, Delay: {delay.TotalMilliseconds}ms");

                // Get destinations from previous routing decision;
                var destinations = context.RoutingDecision?.SelectedDestinations ?? new List<RouteDestination>();

                // Redispatch;
                var redispatchResult = await DispatchAsync(context, destinations, cancellationToken);
                redispatchResult.RetryCount = result.RetryCount;

                return redispatchResult;
            }
            catch (Exception ex)
            {
                result.Status = DispatchStatus.Failed;
                result.Error = ex;
                result.DispatchDuration = DateTime.UtcNow - result.DispatchTime;

                _logger.LogError($"Redispatch failed for {context.Task.TaskName}: {ex.Message}");
                return result;
            }
        }

        public void UpdateStrategy(DispatchMetrics metrics)
        {
            lock (_metricsLock)
            {
                // Update internal metrics;
                _metrics.TotalDispatches = metrics.TotalDispatches;
                _metrics.SuccessfulDispatches = metrics.SuccessfulDispatches;
                _metrics.FailedDispatches = metrics.FailedDispatches;
                _metrics.RetryCount = metrics.RetryCount;
                _metrics.AverageDispatchTimeMs = metrics.AverageDispatchTimeMs;
                _metrics.AverageQueueTimeMs = metrics.AverageQueueTimeMs;

                // Update retry policy;
                _retryPolicy.UpdatePolicy(metrics);

                // Update circuit breakers;
                UpdateCircuitBreakers(metrics);
            }
        }

        #region Private Methods;

        private enum DispatchMode;
        {
            Sequential,
            Parallel,
            Broadcast,
            FanOut;
        }

        private void ValidateDispatch(DispatchContext context, IReadOnlyList<RouteDestination> destinations)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (context.Task == null)
                throw new ArgumentException("Task is required", nameof(context));

            if (destinations == null || destinations.Count == 0)
                throw new ArgumentException("At least one destination is required", nameof(destinations));

            if (context.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException();
        }

        private DispatchMode DetermineDispatchMode(DispatchContext context, IReadOnlyList<RouteDestination> destinations)
        {
            // Check task metadata for explicit mode;
            if (context.Task.Metadata.TryGetValue("DispatchMode", out var modeObj))
            {
                if (Enum.TryParse<DispatchMode>(modeObj.ToString(), out var explicitMode))
                    return explicitMode;
            }

            // Determine based on task and routing;
            if (context.RoutingDecision?.RouteType == RouteType.Broadcast)
                return DispatchMode.Broadcast;

            if (context.RoutingDecision?.RouteType == RouteType.FanOut)
                return DispatchMode.FanOut;

            if (destinations.Count == 1)
                return DispatchMode.Sequential;

            // For multiple destinations, check if they can handle parallel execution;
            if (destinations.All(d => d.Capabilities.ContainsKey("ParallelExecution")))
                return DispatchMode.Parallel;

            // Default to sequential for safety;
            return DispatchMode.Sequential;
        }

        private async Task DispatchSequential(
            DispatchContext context,
            IReadOnlyList<RouteDestination> destinations,
            List<string> successfulDestinations,
            CancellationToken cancellationToken)
        {
            foreach (var destination in destinations)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await DispatchToDestination(context, destination, cancellationToken);
                    successfulDestinations.Add(destination.ExecutorId);

                    // For sequential mode, stop after first successful dispatch;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to dispatch to {destination.ExecutorId}: {ex.Message}");
                    RecordDestinationFailure(destination.ExecutorId, ex);
                    continue;
                }
            }
        }

        private async Task DispatchParallel(
            DispatchContext context,
            IReadOnlyList<RouteDestination> destinations,
            List<string> successfulDestinations,
            CancellationToken cancellationToken)
        {
            var dispatchTasks = new List<Task<string>>();

            foreach (var destination in destinations)
            {
                var task = DispatchToDestinationWithResult(context, destination, cancellationToken);
                dispatchTasks.Add(task);
            }

            // Wait for all dispatch tasks with timeout;
            var timeoutTask = Task.Delay(context.Timeout, cancellationToken);
            var completedTask = await Task.WhenAny(
                Task.WhenAll(dispatchTasks),
                timeoutTask;
            );

            if (completedTask == timeoutTask)
                throw new TimeoutException($"Parallel dispatch timeout after {context.Timeout}");

            // Collect successful destinations;
            foreach (var dispatchTask in dispatchTasks)
            {
                if (dispatchTask.IsCompletedSuccessfully)
                {
                    var executorId = await dispatchTask;
                    if (!string.IsNullOrEmpty(executorId))
                        successfulDestinations.Add(executorId);
                }
            }
        }

        private async Task DispatchBroadcast(
            DispatchContext context,
            IReadOnlyList<RouteDestination> destinations,
            List<string> successfulDestinations,
            CancellationToken cancellationToken)
        {
            var dispatchTasks = new List<Task<string>>();

            foreach (var destination in destinations)
            {
                var task = DispatchToDestinationWithResult(context, destination, cancellationToken);
                dispatchTasks.Add(task);
            }

            // Wait for all broadcast tasks;
            await Task.WhenAll(dispatchTasks);

            // Collect all successful destinations;
            foreach (var dispatchTask in dispatchTasks)
            {
                if (dispatchTask.IsCompletedSuccessfully)
                {
                    var executorId = await dispatchTask;
                    if (!string.IsNullOrEmpty(executorId))
                        successfulDestinations.Add(executorId);
                }
            }
        }

        private async Task DispatchFanOut(
            DispatchContext context,
            IReadOnlyList<RouteDestination> destinations,
            List<string> successfulDestinations,
            CancellationToken cancellationToken)
        {
            // Fan-out requires specific partitioning;
            if (context.Task.Metadata.TryGetValue("FanOutPartitions", out var partitionsObj) &&
                partitionsObj is Dictionary<string, object> partitions)
            {
                foreach (var partition in partitions)
                {
                    var partitionDestinations = destinations;
                        .Where(d => d.Capabilities.ContainsKey(partition.Key))
                        .ToList();

                    if (partitionDestinations.Count > 0)
                    {
                        // Dispatch to first suitable destination for this partition;
                        foreach (var destination in partitionDestinations)
                        {
                            try
                            {
                                await DispatchToDestination(context, destination, cancellationToken);
                                successfulDestinations.Add(destination.ExecutorId);
                                break; // Move to next partition;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"Failed to dispatch partition {partition.Key} to {destination.ExecutorId}: {ex.Message}");
                                continue;
                            }
                        }
                    }
                }
            }
            else;
            {
                // Fallback to parallel dispatch;
                await DispatchParallel(context, destinations, successfulDestinations, cancellationToken);
            }
        }

        private async Task DispatchToDestination(
            DispatchContext context,
            RouteDestination destination,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Check destination availability;
                if (!destination.IsActive)
                    throw new InvalidOperationException($"Destination {destination.ExecutorId} is not active");

                if (destination.CurrentLoad >= 100)
                    throw new InvalidOperationException($"Destination {destination.ExecutorId} is at full capacity");

                // Prepare dispatch payload;
                var payload = CreateDispatchPayload(context, destination);

                // Execute dispatch (actual implementation would use network communication)
                await ExecuteDispatchAsync(destination, payload, cancellationToken);

                // Record success;
                var dispatchTime = DateTime.UtcNow - startTime;
                RecordDestinationSuccess(destination.ExecutorId, dispatchTime);

                _logger.LogDebug($"Dispatched to {destination.ExecutorId} in {dispatchTime.TotalMilliseconds}ms");
            }
            catch (Exception ex)
            {
                // Record failure;
                RecordDestinationFailure(destination.ExecutorId, ex);

                // Update circuit breaker;
                _circuitBreaker.RecordFailure(destination.ExecutorId);

                throw;
            }
        }

        private async Task<string> DispatchToDestinationWithResult(
            DispatchContext context,
            RouteDestination destination,
            CancellationToken cancellationToken)
        {
            try
            {
                await DispatchToDestination(context, destination, cancellationToken);
                return destination.ExecutorId;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, object> CreateDispatchPayload(DispatchContext context, RouteDestination destination)
        {
            var payload = new Dictionary<string, object>
            {
                ["TaskId"] = context.Task.TaskId,
                ["TaskName"] = context.Task.TaskName,
                ["TaskAction"] = context.Task.TaskAction,
                ["Priority"] = context.Task.Priority,
                ["Metadata"] = context.Task.Metadata,
                ["DispatchTime"] = DateTime.UtcNow,
                ["Source"] = Environment.MachineName,
                ["RequiresAck"] = true,
                ["Timeout"] = context.Timeout,
                ["RetryCount"] = context.CurrentRetry
            };

            // Add destination-specific instructions;
            if (destination.Capabilities.ContainsKey("GPU"))
                payload["UseGPU"] = true;

            if (destination.Capabilities.ContainsKey("HighMemory"))
                payload["MemoryAllocation"] = "High";

            return payload;
        }

        private async Task ExecuteDispatchAsync(
            RouteDestination destination,
            Dictionary<string, object> payload,
            CancellationToken cancellationToken)
        {
            // Simulate network dispatch;
            // In production, this would use gRPC, HTTP, or messaging system;

            await Task.Delay(10, cancellationToken); // Simulate network latency;

            // Simulate random failures for testing;
            if (new Random().NextDouble() < 0.05) // 5% failure rate for testing;
                throw new Exception("Simulated network failure");
        }

        private void RecordDestinationSuccess(string executorId, TimeSpan dispatchTime)
        {
            lock (_metricsLock)
            {
                if (!_destinationStats.ContainsKey(executorId))
                    _destinationStats[executorId] = new DestinationDispatchStats();

                var stats = _destinationStats[executorId];
                stats.TotalDispatches++;
                stats.SuccessfulDispatches++;
                stats.TotalDispatchTime += dispatchTime;
                stats.AverageDispatchTime = stats.TotalDispatchTime / stats.TotalDispatches;
                stats.LastSuccessTime = DateTime.UtcNow;

                // Update circuit breaker;
                _circuitBreaker.RecordSuccess(executorId);
            }
        }

        private void RecordDestinationFailure(string executorId, Exception error)
        {
            lock (_metricsLock)
            {
                if (!_destinationStats.ContainsKey(executorId))
                    _destinationStats[executorId] = new DestinationDispatchStats();

                var stats = _destinationStats[executorId];
                stats.TotalDispatches++;
                stats.FailedDispatches++;
                stats.LastError = error;
                stats.LastFailureTime = DateTime.UtcNow;

                var errorType = error.GetType().Name;
                if (!_metrics.FailuresByReason.ContainsKey(errorType))
                    _metrics.FailuresByReason[errorType] = 0;

                _metrics.FailuresByReason[errorType]++;
            }
        }

        private void UpdateMetrics(DispatchResult result, bool success)
        {
            lock (_metricsLock)
            {
                _metrics.TotalDispatches++;

                if (success)
                    _metrics.SuccessfulDispatches++;
                else;
                    _metrics.FailedDispatches++;

                // Update average dispatch time;
                var totalTime = _metrics.AverageDispatchTimeMs * (_metrics.TotalDispatches - 1) +
                               result.DispatchDuration.TotalMilliseconds;
                _metrics.AverageDispatchTimeMs = totalTime / _metrics.TotalDispatches;

                // Update destination statistics;
                foreach (var destId in result.DestinationIds)
                {
                    if (!_metrics.DispatchesByDestination.ContainsKey(destId))
                        _metrics.DispatchesByDestination[destId] = 0;

                    _metrics.DispatchesByDestination[destId]++;
                }
            }
        }

        private void UpdateCircuitBreakers(DispatchMetrics metrics)
        {
            foreach (var kvp in metrics.DispatchesByDestination)
            {
                var executorId = kvp.Key;
                var failureRate = CalculateFailureRate(executorId);

                if (failureRate > 0.5) // 50% failure rate;
                    _circuitBreaker.Trip(executorId);
                else if (failureRate < 0.1) // 10% failure rate;
                    _circuitBreaker.Reset(executorId);
            }
        }

        private double CalculateFailureRate(string executorId)
        {
            if (_destinationStats.TryGetValue(executorId, out var stats))
            {
                if (stats.TotalDispatches == 0) return 0;
                return (double)stats.FailedDispatches / stats.TotalDispatches;
            }

            return 0;
        }

        private class DestinationDispatchStats;
        {
            public int TotalDispatches { get; set; }
            public int SuccessfulDispatches { get; set; }
            public int FailedDispatches { get; set; }
            public TimeSpan TotalDispatchTime { get; set; }
            public TimeSpan AverageDispatchTime { get; set; }
            public Exception LastError { get; set; }
            public DateTime LastSuccessTime { get; set; }
            public DateTime LastFailureTime { get; set; }

            public double SuccessRate => TotalDispatches > 0 ?
                (double)SuccessfulDispatches / TotalDispatches : 0;
        }

        #endregion;
    }

    /// <summary>
    /// Adaptive retry policy;
    /// </summary>
    public class AdaptiveRetryPolicy;
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, RetryPolicyConfig> _policyConfigs = new();
        private readonly object _configLock = new();

        public AdaptiveRetryPolicy(ILogger logger)
        {
            _logger = logger;

            // Default policies;
            _policyConfigs["Default"] = new RetryPolicyConfig;
            {
                MaxRetries = 3,
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromMinutes(5),
                BackoffMultiplier = 2.0,
                RetryableErrors = new List<Type> { typeof(TimeoutException), typeof(IOException) }
            };

            _policyConfigs["Critical"] = new RetryPolicyConfig;
            {
                MaxRetries = 10,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(30),
                BackoffMultiplier = 1.5,
                RetryableErrors = new List<Type> { typeof(Exception) } // Retry all errors;
            };

            _policyConfigs["NonRetryable"] = new RetryPolicyConfig;
            {
                MaxRetries = 0,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                BackoffMultiplier = 1.0,
                RetryableErrors = new List<Type>()
            };
        }

        public bool ShouldRetry(DispatchContext context, DispatchResult previousResult)
        {
            var policy = GetPolicyForTask(context.Task);

            if (previousResult.RetryCount >= policy.MaxRetries)
                return false;

            if (previousResult.Error == null)
                return false;

            // Check if error is retryable;
            var errorType = previousResult.Error.GetType();
            if (!policy.RetryableErrors.Any(t => t.IsAssignableFrom(errorType)))
                return false;

            // Check timeout;
            if (context.IsTimedOut)
                return false;

            return true;
        }

        public TimeSpan GetRetryDelay(DispatchContext context, DispatchResult previousResult)
        {
            var policy = GetPolicyForTask(context.Task);

            // Exponential backoff with jitter;
            var baseDelay = policy.BaseDelay.TotalMilliseconds;
            var multiplier = Math.Pow(policy.BackoffMultiplier, previousResult.RetryCount);
            var delayMs = baseDelay * multiplier;

            // Add jitter (±20%)
            var jitter = (new Random().NextDouble() * 0.4) - 0.2; // -0.2 to +0.2;
            delayMs *= (1 + jitter);

            // Cap at max delay;
            delayMs = Math.Min(delayMs, policy.MaxDelay.TotalMilliseconds);

            return TimeSpan.FromMilliseconds(delayMs);
        }

        public void UpdatePolicy(DispatchMetrics metrics)
        {
            lock (_configLock)
            {
                // Adjust policies based on system performance;
                var successRate = metrics.SuccessRate;

                if (successRate < 80) // Low success rate;
                {
                    // Reduce retries to avoid system overload;
                    foreach (var config in _policyConfigs.Values)
                    {
                        config.MaxRetries = Math.Max(1, config.MaxRetries - 1);
                    }

                    _logger.LogInformation($"Retry policies adjusted due to low success rate ({successRate:F1}%)");
                }
                else if (successRate > 95) // High success rate;
                {
                    // Increase retries for reliability;
                    foreach (var config in _policyConfigs.Values)
                    {
                        if (config.MaxRetries < 10)
                            config.MaxRetries++;
                    }
                }
            }
        }

        private RetryPolicyConfig GetPolicyForTask(PrioritizedTask task)
        {
            // Determine policy based on task properties;
            if (task.Priority == TaskPriority.Emergency)
                return _policyConfigs["Critical"];

            if (task.Metadata.TryGetValue("NonRetryable", out var nonRetryable) &&
                (bool)nonRetryable)
                return _policyConfigs["NonRetryable"];

            return _policyConfigs["Default"];
        }

        private class RetryPolicyConfig;
        {
            public int MaxRetries { get; set; }
            public TimeSpan BaseDelay { get; set; }
            public TimeSpan MaxDelay { get; set; }
            public double BackoffMultiplier { get; set; }
            public List<Type> RetryableErrors { get; set; }
        }
    }

    /// <summary>
    /// Circuit breaker manager;
    /// </summary>
    public class CircuitBreakerManager;
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new();
        private readonly TimeSpan _resetTimeout = TimeSpan.FromMinutes(5);

        public CircuitBreakerManager(ILogger logger)
        {
            _logger = logger;
        }

        public bool IsOpen(string executorId)
        {
            if (_breakers.TryGetValue(executorId, out var breaker))
                return breaker.State == CircuitBreakerState.Open;

            return false;
        }

        public void RecordSuccess(string executorId)
        {
            var breaker = _breakers.GetOrAdd(executorId,
                id => new CircuitBreaker(_resetTimeout, _logger));

            breaker.RecordSuccess();
        }

        public void RecordFailure(string executorId)
        {
            var breaker = _breakers.GetOrAdd(executorId,
                id => new CircuitBreaker(_resetTimeout, _logger));

            breaker.RecordFailure();
        }

        public void Trip(string executorId)
        {
            var breaker = _breakers.GetOrAdd(executorId,
                id => new CircuitBreaker(_resetTimeout, _logger));

            breaker.Trip();
        }

        public void Reset(string executorId)
        {
            if (_breakers.TryGetValue(executorId, out var breaker))
                breaker.Reset();
        }

        private enum CircuitBreakerState;
        {
            Closed,
            Open,
            HalfOpen;
        }

        private class CircuitBreaker;
        {
            private readonly ILogger _logger;
            private readonly TimeSpan _resetTimeout;
            private CircuitBreakerState _state = CircuitBreakerState.Closed;
            private int _failureCount = 0;
            private int _successCount = 0;
            private DateTime _lastStateChange = DateTime.UtcNow;
            private const int FAILURE_THRESHOLD = 5;
            private const int SUCCESS_THRESHOLD = 3;

            public CircuitBreaker(TimeSpan resetTimeout, ILogger logger)
            {
                _resetTimeout = resetTimeout;
                _logger = logger;
            }

            public CircuitBreakerState State => _state;

            public void RecordSuccess()
            {
                lock (this)
                {
                    if (_state == CircuitBreakerState.HalfOpen)
                    {
                        _successCount++;

                        if (_successCount >= SUCCESS_THRESHOLD)
                        {
                            _state = CircuitBreakerState.Closed;
                            _failureCount = 0;
                            _successCount = 0;
                            _lastStateChange = DateTime.UtcNow;

                            _logger.LogInformation($"Circuit breaker closed after {SUCCESS_THRESHOLD} successes");
                        }
                    }
                    else if (_state == CircuitBreakerState.Closed)
                    {
                        _failureCount = Math.Max(0, _failureCount - 1);
                    }
                }
            }

            public void RecordFailure()
            {
                lock (this)
                {
                    if (_state == CircuitBreakerState.Closed)
                    {
                        _failureCount++;

                        if (_failureCount >= FAILURE_THRESHOLD)
                        {
                            _state = CircuitBreakerState.Open;
                            _lastStateChange = DateTime.UtcNow;

                            _logger.LogWarning($"Circuit breaker opened after {FAILURE_THRESHOLD} failures");
                        }
                    }
                    else if (_state == CircuitBreakerState.HalfOpen)
                    {
                        _state = CircuitBreakerState.Open;
                        _lastStateChange = DateTime.UtcNow;

                        _logger.LogWarning($"Circuit breaker re-opened after failure in half-open state");
                    }
                }
            }

            public void Trip()
            {
                lock (this)
                {
                    _state = CircuitBreakerState.Open;
                    _lastStateChange = DateTime.UtcNow;
                    _failureCount = FAILURE_THRESHOLD;

                    _logger.LogWarning($"Circuit breaker manually tripped");
                }
            }

            public void Reset()
            {
                lock (this)
                {
                    if (_state == CircuitBreakerState.Open &&
                        DateTime.UtcNow - _lastStateChange > _resetTimeout)
                    {
                        _state = CircuitBreakerState.HalfOpen;
                        _successCount = 0;
                        _lastStateChange = DateTime.UtcNow;

                        _logger.LogInformation($"Circuit breaker reset to half-open state");
                    }
                    else if (_state == CircuitBreakerState.Closed)
                    {
                        _failureCount = 0;
                        _successCount = 0;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Main Task Dispatcher for intelligent task distribution;
    /// </summary>
    public class TaskDispatcher : IDisposable
    {
        private readonly ILogger _logger;
        private readonly PriorityManager _priorityManager;
        private readonly RouterEngine _routerEngine;
        private readonly IDispatchStrategy _dispatchStrategy;
        private readonly Channel<DispatchContext> _dispatchQueue;
        private readonly ConcurrentDictionary<Guid, DispatchContext> _activeDispatches = new();
        private readonly List<Task> _workerTasks = new();
        private readonly Timer _metricsTimer;
        private readonly Timer _cleanupTimer;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _dispatchLock = new(1, 1);
        private bool _isDisposed;
        private int _maxConcurrentDispatches = 10;

        // Dispatch statistics;
        private readonly DispatchMetrics _dispatchMetrics = new();
        private readonly object _metricsLock = new();
        private long _totalDispatchesProcessed;
        private long _totalDispatchFailures;

        /// <summary>
        /// Event fired when task is dispatched;
        /// </summary>
        public event EventHandler<TaskDispatchedEventArgs> TaskDispatched;

        /// <summary>
        /// Event fired when dispatch completes;
        /// </summary>
        public event EventHandler<DispatchCompletedEventArgs> DispatchCompleted;

        /// <summary>
        /// Event fired when dispatch metrics are updated;
        /// </summary>
        public event EventHandler<DispatchMetricsUpdatedEventArgs> DispatchMetricsUpdated;

        public TaskDispatcher(
            ILogger logger,
            PriorityManager priorityManager,
            RouterEngine routerEngine,
            IDispatchStrategy dispatchStrategy = null,
            int maxQueueSize = 10000)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _priorityManager = priorityManager ?? throw new ArgumentNullException(nameof(priorityManager));
            _routerEngine = routerEngine ?? throw new ArgumentNullException(nameof(routerEngine));
            _dispatchStrategy = dispatchStrategy ?? new IntelligentDispatchStrategy(logger);

            // Create bounded dispatch queue;
            _dispatchQueue = Channel.CreateBounded<DispatchContext>(new BoundedChannelOptions(maxQueueSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false;
            });

            // Start dispatch workers;
            StartDispatchWorkers();

            // Start metrics collection every 30 seconds;
            _metricsTimer = new Timer(CollectAndUpdateMetrics, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            // Start cleanup timer every 5 minutes;
            _cleanupTimer = new Timer(CleanupOldDispatches, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            _logger.LogInformation($"TaskDispatcher initialized with {_maxConcurrentDispatches} workers");
        }

        /// <summary>
        /// Dispatch a single task;
        /// </summary>
        public async Task<DispatchResult> DispatchTaskAsync(PrioritizedTask task,
            CancellationToken cancellationToken = default)
        {
            ValidateTask(task);

            var context = new DispatchContext;
            {
                Task = task,
                CancellationToken = cancellationToken,
                Timeout = task.Timeout;
            };

            try
            {
                // Get routing decision;
                var routingDecision = await _routerEngine.RouteTaskAsync(task, cancellationToken);
                context.RoutingDecision = routingDecision;

                if (!routingDecision.IsValid)
                {
                    return new DispatchResult;
                    {
                        TaskId = task.TaskId,
                        TaskName = task.TaskName,
                        Status = DispatchStatus.Failed,
                        Error = new Exception($"Invalid routing decision: {routingDecision.RoutingReason}")
                    };
                }

                // Perform dispatch;
                var result = await _dispatchStrategy.DispatchAsync(
                    context,
                    routingDecision.SelectedDestinations,
                    cancellationToken);

                // Handle retry if needed;
                if (!result.Success && result.RetryCount < task.MaxRetries)
                {
                    result = await _dispatchStrategy.RedispatchAsync(context, result, cancellationToken);
                }

                // Update statistics;
                UpdateDispatchMetrics(result);

                // Fire events;
                OnDispatchCompleted(context, result);

                return result;
            }
            catch (Exception ex)
            {
                var result = new DispatchResult;
                {
                    TaskId = task.TaskId,
                    TaskName = task.TaskName,
                    Status = DispatchStatus.Failed,
                    Error = ex,
                    DispatchDuration = DateTime.UtcNow - context.CreatedTime;
                };

                UpdateDispatchMetrics(result);
                OnDispatchCompleted(context, result);

                _logger.LogError($"Dispatch failed for {task.TaskName}: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Queue task for async dispatch;
        /// </summary>
        public async Task<bool> QueueTaskAsync(PrioritizedTask task,
            CancellationToken cancellationToken = default)
        {
            ValidateTask(task);

            var context = new DispatchContext;
            {
                Task = task,
                CancellationToken = cancellationToken,
                Timeout = task.Timeout;
            };

            _activeDispatches[context.ContextId] = context;

            try
            {
                await _dispatchQueue.Writer.WriteAsync(context, cancellationToken);

                _logger.LogDebug($"Task queued for dispatch: {task.TaskName}, Queue size: {_dispatchQueue.Reader.Count}");
                return true;
            }
            catch (Exception ex)
            {
                _activeDispatches.TryRemove(context.ContextId, out _);
                _logger.LogError($"Failed to queue task {task.TaskName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Batch dispatch multiple tasks;
        /// </summary>
        public async Task<List<DispatchResult>> BatchDispatchAsync(List<PrioritizedTask> tasks,
            CancellationToken cancellationToken = default)
        {
            var results = new List<DispatchResult>();
            var batchStartTime = DateTime.UtcNow;

            // Group tasks by priority for optimal dispatch order;
            var groupedTasks = tasks;
                .GroupBy(t => t.Priority)
                .OrderBy(g => g.Key) // Emergency first;
                .ToList();

            foreach (var group in groupedTasks)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var groupTasks = group.ToList();
                var dispatchTasks = new List<Task<DispatchResult>>();

                // Dispatch tasks in parallel within priority group;
                foreach (var task in groupTasks)
                {
                    var dispatchTask = DispatchTaskAsync(task, cancellationToken);
                    dispatchTasks.Add(dispatchTask);
                }

                // Wait for all dispatches in this group;
                var groupResults = await Task.WhenAll(dispatchTasks);
                results.AddRange(groupResults);
            }

            var batchTime = DateTime.UtcNow - batchStartTime;
            _logger.LogInformation($"Batch dispatch completed: {results.Count} tasks, " +
                                  $"Time: {batchTime.TotalMilliseconds}ms, " +
                                  $"Success rate: {results.Count(r => r.Success) / (double)results.Count * 100:F1}%");

            return results;
        }

        /// <summary>
        /// Get dispatch queue statistics;
        /// </summary>
        public DispatchQueueStats GetQueueStats()
        {
            return new DispatchQueueStats;
            {
                QueueSize = _dispatchQueue.Reader.Count,
                ActiveDispatches = _activeDispatches.Count,
                WorkerCount = _workerTasks.Count,
                MaxConcurrentDispatches = _maxConcurrentDispatches,
                IsQueueFull = _dispatchQueue.Reader.Count >= _dispatchQueue.Writer.GetMemory().Length;
            };
        }

        /// <summary>
        /// Get dispatch statistics;
        /// </summary>
        public TaskDispatcherStats GetStatistics()
        {
            lock (_metricsLock)
            {
                return new TaskDispatcherStats;
                {
                    TotalDispatchesProcessed = _totalDispatchesProcessed,
                    TotalDispatchFailures = _totalDispatchFailures,
                    SuccessRate = _totalDispatchesProcessed > 0 ?
                        (double)(_totalDispatchesProcessed - _totalDispatchFailures) / _totalDispatchesProcessed * 100 : 100,
                    DispatchMetrics = _dispatchMetrics,
                    QueueStats = GetQueueStats(),
                    AverageDispatchTimeMs = _dispatchMetrics.AverageDispatchTimeMs,
                    AverageQueueTimeMs = _dispatchMetrics.AverageQueueTimeMs;
                };
            }
        }

        /// <summary>
        /// Set maximum concurrent dispatches;
        /// </summary>
        public async Task SetMaxConcurrentDispatchesAsync(int maxConcurrent,
            CancellationToken cancellationToken = default)
        {
            await _dispatchLock.WaitAsync(cancellationToken);
            try
            {
                if (maxConcurrent <= 0)
                    throw new ArgumentException("Max concurrent dispatches must be greater than 0", nameof(maxConcurrent));

                if (maxConcurrent != _maxConcurrentDispatches)
                {
                    _maxConcurrentDispatches = maxConcurrent;

                    // Restart workers with new count;
                    StopDispatchWorkers();
                    StartDispatchWorkers();

                    _logger.LogInformation($"Max concurrent dispatches changed to {maxConcurrent}");
                }
            }
            finally
            {
                _dispatchLock.Release();
            }
        }

        /// <summary>
        /// Cancel a pending dispatch;
        /// </summary>
        public async Task<bool> CancelDispatchAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            // Find context by task ID;
            var context = _activeDispatches.Values.FirstOrDefault(c => c.Task.TaskId == taskId);
            if (context == null)
                return false;

            // Cancel the context;
            if (context.CancellationToken.CanBeCanceled)
            {
                // This would signal cancellation through the cancellation token source;
                // Implementation depends on how cancellation is handled;
            }

            _activeDispatches.TryRemove(context.ContextId, out _);

            _logger.LogInformation($"Dispatch cancelled for task: {context.Task.TaskName}");
            return true;
        }

        /// <summary>
        /// Clear dispatch queue;
        /// </summary>
        public async Task<int> ClearQueueAsync(CancellationToken cancellationToken = default)
        {
            int clearedCount = 0;

            // Drain the queue;
            while (_dispatchQueue.Reader.TryRead(out var context))
            {
                _activeDispatches.TryRemove(context.ContextId, out _);
                clearedCount++;

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            if (clearedCount > 0)
            {
                _logger.LogInformation($"Cleared {clearedCount} tasks from dispatch queue");
            }

            return clearedCount;
        }

        /// <summary>
        /// Emergency stop all dispatches;
        /// </summary>
        public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
        {
            await _dispatchLock.WaitAsync(cancellationToken);
            try
            {
                // Stop all workers;
                StopDispatchWorkers();

                // Clear queue;
                await ClearQueueAsync(cancellationToken);

                // Cancel all active dispatches;
                foreach (var context in _activeDispatches.Values)
                {
                    if (context.CancellationToken.CanBeCanceled)
                    {
                        // Signal cancellation;
                    }
                }

                _activeDispatches.Clear();

                _logger.LogWarning($"Emergency stop executed. All dispatches stopped.");
            }
            finally
            {
                _dispatchLock.Release();
            }
        }

        /// <summary>
        /// Resume dispatches after emergency stop;
        /// </summary>
        public async Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            await _dispatchLock.WaitAsync(cancellationToken);
            try
            {
                StartDispatchWorkers();
                _logger.LogInformation($"Dispatches resumed with {_maxConcurrentDispatches} workers");
            }
            finally
            {
                _dispatchLock.Release();
            }
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _cts.Cancel();

            // Stop workers;
            StopDispatchWorkers();

            // Clear queue;
            _ = ClearQueueAsync(CancellationToken.None);

            _metricsTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _dispatchLock?.Dispose();
            _cts.Dispose();

            _isDisposed = true;
            _logger.LogInformation("TaskDispatcher disposed");
        }

        #region Private Methods;

        private void ValidateTask(PrioritizedTask task)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (task.TaskAction == null)
                throw new ArgumentException("Task action cannot be null", nameof(task));
        }

        private void StartDispatchWorkers()
        {
            for (int i = 0; i < _maxConcurrentDispatches; i++)
            {
                var workerTask = Task.Run(() => DispatchWorkerAsync(i, _cts.Token), _cts.Token);
                _workerTasks.Add(workerTask);
            }
        }

        private void StopDispatchWorkers()
        {
            // Workers will stop when cancellation token is triggered;
            _cts.Cancel();

            // Wait for workers to complete;
            Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(30));
            _workerTasks.Clear();
        }

        private async Task DispatchWorkerAsync(int workerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Dispatch worker {workerId} started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Read from queue with timeout;
                    var context = await _dispatchQueue.Reader.ReadAsync(cancellationToken);

                    if (context != null)
                    {
                        await ProcessQueuedDispatchAsync(context, workerId, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Dispatch worker {workerId} error: {ex.Message}");
                    await Task.Delay(1000, cancellationToken); // Wait before retry
                }
            }

            _logger.LogDebug($"Dispatch worker {workerId} stopped");
        }

        private async Task ProcessQueuedDispatchAsync(DispatchContext context, int workerId,
            CancellationToken cancellationToken)
        {
            var queueTime = DateTime.UtcNow - context.CreatedTime;

            try
            {
                // Get routing decision;
                var routingDecision = await _routerEngine.RouteTaskAsync(context.Task, cancellationToken);
                context.RoutingDecision = routingDecision;

                if (!routingDecision.IsValid)
                {
                    var result = new DispatchResult;
                    {
                        TaskId = context.Task.TaskId,
                        TaskName = context.Task.TaskName,
                        Status = DispatchStatus.Failed,
                        Error = new Exception($"Invalid routing: {routingDecision.RoutingReason}"),
                        DispatchDuration = TimeSpan.Zero;
                    };

                    UpdateDispatchMetrics(result);
                    OnDispatchCompleted(context, result);
                    return;
                }

                // Perform dispatch;
                var result = await _dispatchStrategy.DispatchAsync(
                    context,
                    routingDecision.SelectedDestinations,
                    cancellationToken);

                result.DispatchMetrics["QueueTimeMs"] = queueTime.TotalMilliseconds;

                // Update queue time metric;
                lock (_metricsLock)
                {
                    var totalQueueTime = _dispatchMetrics.AverageQueueTimeMs * (_dispatchMetrics.TotalDispatches - 1) +
                                        queueTime.TotalMilliseconds;
                    _dispatchMetrics.AverageQueueTimeMs = totalQueueTime / _dispatchMetrics.TotalDispatches;
                }

                // Handle retry
                if (!result.Success && result.RetryCount < context.Task.MaxRetries)
                {
                    result = await _dispatchStrategy.RedispatchAsync(context, result, cancellationToken);
                }

                // Update statistics;
                UpdateDispatchMetrics(result);

                // Fire event;
                OnDispatchCompleted(context, result);

                _logger.LogDebug($"Worker {workerId} dispatched: {context.Task.TaskName}, " +
                               $"Queue time: {queueTime.TotalMilliseconds}ms, " +
                               $"Result: {result.Status}");
            }
            catch (Exception ex)
            {
                var result = new DispatchResult;
                {
                    TaskId = context.Task.TaskId,
                    TaskName = context.Task.TaskName,
                    Status = DispatchStatus.Failed,
                    Error = ex,
                    DispatchDuration = DateTime.UtcNow - context.CreatedTime - queueTime,
                    DispatchMetrics = { ["QueueTimeMs"] = queueTime.TotalMilliseconds }
                };

                UpdateDispatchMetrics(result);
                OnDispatchCompleted(context, result);

                _logger.LogError($"Worker {workerId} failed to dispatch {context.Task.TaskName}: {ex.Message}");
            }
            finally
            {
                _activeDispatches.TryRemove(context.ContextId, out _);
            }
        }

        private void CollectAndUpdateMetrics(object state)
        {
            try
            {
                lock (_metricsLock)
                {
                    _dispatchStrategy.UpdateStrategy(_dispatchMetrics);
                    OnDispatchMetricsUpdated(_dispatchMetrics);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error collecting dispatch metrics: {ex.Message}");
            }
        }

        private void CleanupOldDispatches(object state)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddMinutes(-30); // 30 minutes old;
                var oldContexts = _activeDispatches.Values;
                    .Where(c => c.CreatedTime < cutoffTime)
                    .ToList();

                int removedCount = 0;

                foreach (var context in oldContexts)
                {
                    if (_activeDispatches.TryRemove(context.ContextId, out _))
                        removedCount++;
                }

                if (removedCount > 0)
                {
                    _logger.LogInformation($"Cleaned up {removedCount} old dispatch contexts");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during dispatch cleanup: {ex.Message}");
            }
        }

        private void UpdateDispatchMetrics(DispatchResult result)
        {
            lock (_metricsLock)
            {
                _totalDispatchesProcessed++;

                if (!result.Success)
                    _totalDispatchFailures++;

                // Update strategy metrics;
                _dispatchMetrics.TotalDispatches++;

                if (result.Success)
                    _dispatchMetrics.SuccessfulDispatches++;
                else;
                    _dispatchMetrics.FailedDispatches++;

                if (result.RetryCount > 0)
                    _dispatchMetrics.RetryCount++;

                // Update average dispatch time;
                var totalTime = _dispatchMetrics.AverageDispatchTimeMs * (_dispatchMetrics.TotalDispatches - 1) +
                               result.DispatchDuration.TotalMilliseconds;
                _dispatchMetrics.AverageDispatchTimeMs = totalTime / _dispatchMetrics.TotalDispatches;
            }
        }

        private void OnTaskDispatched(DispatchContext context, List<string> destinationIds)
        {
            TaskDispatched?.Invoke(this, new TaskDispatchedEventArgs;
            {
                TaskId = context.Task.TaskId,
                TaskName = context.Task.TaskName,
                DestinationIds = destinationIds,
                DispatchTime = DateTime.UtcNow;
            });
        }

        private void OnDispatchCompleted(DispatchContext context, DispatchResult result)
        {
            DispatchCompleted?.Invoke(this, new DispatchCompletedEventArgs;
            {
                TaskId = context.Task.TaskId,
                TaskName = context.Task.TaskName,
                DispatchResult = result,
                CompletedTime = DateTime.UtcNow;
            });
        }

        private void OnDispatchMetricsUpdated(DispatchMetrics metrics)
        {
            DispatchMetricsUpdated?.Invoke(this, new DispatchMetricsUpdatedEventArgs;
            {
                Metrics = metrics,
                Timestamp = DateTime.UtcNow;
            });
        }

        #endregion;
    }

    #region Event Args Classes;

    /// <summary>
    /// Event arguments for task dispatched events;
    /// </summary>
    public class TaskDispatchedEventArgs : EventArgs;
    {
        public Guid TaskId { get; set; }
        public string TaskName { get; set; }
        public List<string> DestinationIds { get; set; }
        public DateTime DispatchTime { get; set; }
    }

    /// <summary>
    /// Event arguments for dispatch completed events;
    /// </summary>
    public class DispatchCompletedEventArgs : EventArgs;
    {
        public Guid TaskId { get; set; }
        public string TaskName { get; set; }
        public DispatchResult DispatchResult { get; set; }
        public DateTime CompletedTime { get; set; }
    }

    /// <summary>
    /// Event arguments for dispatch metrics update events;
    /// </summary>
    public class DispatchMetricsUpdatedEventArgs : EventArgs;
    {
        public DispatchMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Dispatch queue statistics;
    /// </summary>
    public class DispatchQueueStats;
    {
        public int QueueSize { get; set; }
        public int ActiveDispatches { get; set; }
        public int WorkerCount { get; set; }
        public int MaxConcurrentDispatches { get; set; }
        public bool IsQueueFull { get; set; }

        public override string ToString()
        {
            return $"Queue: {QueueSize}, Active: {ActiveDispatches}, Workers: {WorkerCount}/{MaxConcurrentDispatches}";
        }
    }

    /// <summary>
    /// Task dispatcher statistics;
    /// </summary>
    public class TaskDispatcherStats;
    {
        public long TotalDispatchesProcessed { get; set; }
        public long TotalDispatchFailures { get; set; }
        public double SuccessRate { get; set; }
        public DispatchMetrics DispatchMetrics { get; set; }
        public DispatchQueueStats QueueStats { get; set; }
        public double AverageDispatchTimeMs { get; set; }
        public double AverageQueueTimeMs { get; set; }

        public override string ToString()
        {
            return $"Dispatches: {TotalDispatchesProcessed}, Success: {SuccessRate:F1}%, " +
                   $"Avg Dispatch: {AverageDispatchTimeMs:F1}ms, Avg Queue: {AverageQueueTimeMs:F1}ms, " +
                   $"Queue: {QueueStats?.QueueSize}";
        }
    }

    #endregion;
}
