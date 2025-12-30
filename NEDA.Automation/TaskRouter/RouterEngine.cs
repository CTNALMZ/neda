using NEDA.Core.Logging;
using NEDA.Core.Common.Utilities;
using NEDA.Automation.TaskRouter;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Automation.TaskRouter;
{
    /// <summary>
    /// Route types for different task categories;
    /// </summary>
    public enum RouteType;
    {
        /// <summary>
        /// Direct route to specific executor;
        /// </summary>
        Direct = 0,

        /// <summary>
        /// Load balanced across multiple executors;
        /// </summary>
        LoadBalanced = 1,

        /// <summary>
        /// Broadcast to all available executors;
        /// </summary>
        Broadcast = 2,

        /// <summary>
        /// Fan-out to specialized executors;
        /// </summary>
        FanOut = 3,

        /// <summary>
        /// Chain of executors in sequence;
        /// </summary>
        Chain = 4,

        /// <summary>
        /// Route based on content inspection;
        /// </summary>
        ContentBased = 5,

        /// <summary>
        /// Route to first available executor;
        /// </summary>
        FirstAvailable = 6,

        /// <summary>
        /// Route based on geographical location;
        /// </summary>
        GeoLocation = 7;
    }

    /// <summary>
    /// Route destination information;
    /// </summary>
    public class RouteDestination;
    {
        public string ExecutorId { get; set; }
        public string ExecutorName { get; set; }
        public string ExecutorType { get; set; }
        public string MachineName { get; set; }
        public string IPAddress { get; set; }
        public int Port { get; set; }
        public Dictionary<string, string> Capabilities { get; } = new();
        public DateTime LastHeartbeat { get; set; }
        public bool IsActive { get; set; }
        public int CurrentLoad { get; set; }
        public double PerformanceScore { get; set; }
        public GeoLocation Location { get; set; }
        public List<string> SupportedTaskTypes { get; } = new();

        public bool CanHandleTask(PrioritizedTask task)
        {
            if (!IsActive) return false;

            // Check task type support;
            if (SupportedTaskTypes.Count > 0 &&
                task.Category != null &&
                !SupportedTaskTypes.Contains(task.Category))
                return false;

            // Check capability requirements;
            if (task.Metadata.TryGetValue("RequiredCapabilities", out var requiredCaps) &&
                requiredCaps is List<string> capabilities)
            {
                foreach (var cap in capabilities)
                {
                    if (!Capabilities.ContainsKey(cap))
                        return false;
                }
            }

            // Check load capacity;
            if (CurrentLoad >= 100) // 100% load;
                return false;

            return true;
        }
    }

    /// <summary>
    /// Geographical location for routing;
    /// </summary>
    public class GeoLocation;
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Region { get; set; }
        public string DataCenter { get; set; }

        public double DistanceTo(GeoLocation other)
        {
            if (other == null) return double.MaxValue;

            // Haversine formula for distance calculation;
            var R = 6371; // Earth's radius in kilometers;
            var dLat = ToRadians(other.Latitude - Latitude);
            var dLon = ToRadians(other.Longitude - Longitude);

            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRadians(Latitude)) * Math.Cos(ToRadians(other.Latitude)) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private static double ToRadians(double degrees) => degrees * Math.PI / 180;
    }

    /// <summary>
    /// Routing decision with metrics;
    /// </summary>
    public class RoutingDecision;
    {
        public Guid DecisionId { get; } = Guid.NewGuid();
        public PrioritizedTask Task { get; set; }
        public RouteType RouteType { get; set; }
        public List<RouteDestination> SelectedDestinations { get; } = new();
        public DateTime DecisionTime { get; } = DateTime.UtcNow;
        public double RoutingScore { get; set; }
        public Dictionary<string, object> RoutingMetrics { get; } = new();
        public string RoutingReason { get; set; }

        public bool IsValid => SelectedDestinations.Count > 0 &&
                              SelectedDestinations.All(d => d.IsActive);
    }

    /// <summary>
    /// Routing strategy interface;
    /// </summary>
    public interface IRoutingStrategy;
    {
        Task<RoutingDecision> MakeRoutingDecisionAsync(
            PrioritizedTask task,
            IReadOnlyList<RouteDestination> availableDestinations,
            CancellationToken cancellationToken = default);

        void UpdateStrategyMetrics(RoutingMetrics metrics);
    }

    /// <summary>
    /// Routing metrics for strategy optimization;
    /// </summary>
    public class RoutingMetrics;
    {
        public int TotalRoutes { get; set; }
        public int SuccessfulRoutes { get; set; }
        public int FailedRoutes { get; set; }
        public double AverageRoutingTimeMs { get; set; }
        public double AverageRouteScore { get; set; }
        public Dictionary<RouteType, int> RoutesByType { get; } = new();
        public DateTime CollectionStartTime { get; } = DateTime.UtcNow;

        public double SuccessRate => TotalRoutes > 0 ?
            (double)SuccessfulRoutes / TotalRoutes * 100 : 0;
    }

    /// <summary>
    /// Intelligent routing strategy implementation;
    /// </summary>
    public class IntelligentRoutingStrategy : IRoutingStrategy;
    {
        private readonly ILogger _logger;
        private readonly RoutingMetrics _metrics = new();
        private readonly object _metricsLock = new();
        private readonly Dictionary<string, double> _executorScores = new();
        private readonly AdaptiveLoadBalancer _loadBalancer;

        public IntelligentRoutingStrategy(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _loadBalancer = new AdaptiveLoadBalancer(logger);
        }

        public async Task<RoutingDecision> MakeRoutingDecisionAsync(
            PrioritizedTask task,
            IReadOnlyList<RouteDestination> availableDestinations,
            CancellationToken cancellationToken = default)
        {
            var decision = new RoutingDecision { Task = task };

            try
            {
                var startTime = DateTime.UtcNow;

                // Filter available destinations;
                var suitableDestinations = availableDestinations;
                    .Where(d => d.CanHandleTask(task))
                    .ToList();

                if (suitableDestinations.Count == 0)
                {
                    decision.RoutingReason = "No suitable destinations available";
                    return decision;
                }

                // Determine route type based on task characteristics;
                decision.RouteType = DetermineRouteType(task, suitableDestinations);

                // Select destinations based on route type;
                switch (decision.RouteType)
                {
                    case RouteType.Direct:
                        await HandleDirectRouting(task, suitableDestinations, decision, cancellationToken);
                        break;

                    case RouteType.LoadBalanced:
                        await HandleLoadBalancedRouting(task, suitableDestinations, decision, cancellationToken);
                        break;

                    case RouteType.Broadcast:
                        await HandleBroadcastRouting(task, suitableDestinations, decision, cancellationToken);
                        break;

                    case RouteType.FanOut:
                        await HandleFanOutRouting(task, suitableDestinations, decision, cancellationToken);
                        break;

                    case RouteType.Chain:
                        await HandleChainRouting(task, suitableDestinations, decision, cancellationToken);
                        break;

                    case RouteType.ContentBased:
                        await HandleContentBasedRouting(task, suitableDestinations, decision, cancellationToken);
                        break;

                    case RouteType.FirstAvailable:
                        await HandleFirstAvailableRouting(task, suitableDestinations, decision, cancellationToken);
                        break;

                    case RouteType.GeoLocation:
                        await HandleGeoLocationRouting(task, suitableDestinations, decision, cancellationToken);
                        break;
                }

                // Calculate routing score;
                decision.RoutingScore = CalculateRoutingScore(decision);

                var routingTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                decision.RoutingMetrics["RoutingTimeMs"] = routingTime;
                decision.RoutingMetrics["DestinationsConsidered"] = suitableDestinations.Count;
                decision.RoutingMetrics["DestinationsSelected"] = decision.SelectedDestinations.Count;

                UpdateMetrics(decision, routingTime, true);

                _logger.LogInformation($"Routing decision made: {task.TaskName} -> " +
                                      $"{decision.RouteType}, Score: {decision.RoutingScore:F2}, " +
                                      $"Destinations: {decision.SelectedDestinations.Count}");

                return decision;
            }
            catch (Exception ex)
            {
                decision.RoutingReason = $"Routing failed: {ex.Message}";
                UpdateMetrics(decision, 0, false);
                _logger.LogError($"Routing decision failed for task {task.TaskName}: {ex.Message}");
                return decision;
            }
        }

        public void UpdateStrategyMetrics(RoutingMetrics metrics)
        {
            lock (_metricsLock)
            {
                _metrics.TotalRoutes = metrics.TotalRoutes;
                _metrics.SuccessfulRoutes = metrics.SuccessfulRoutes;
                _metrics.FailedRoutes = metrics.FailedRoutes;
                _metrics.AverageRoutingTimeMs = metrics.AverageRoutingTimeMs;
                _metrics.AverageRouteScore = metrics.AverageRouteScore;

                foreach (var kvp in metrics.RoutesByType)
                {
                    _metrics.RoutesByType[kvp.Key] = kvp.Value;
                }

                // Update executor scores based on metrics;
                UpdateExecutorScores();
            }
        }

        #region Private Methods;

        private RouteType DetermineRouteType(PrioritizedTask task, List<RouteDestination> destinations)
        {
            // Check task metadata for explicit route type;
            if (task.Metadata.TryGetValue("RouteType", out var routeTypeObj) &&
                routeTypeObj is RouteType explicitRouteType)
                return explicitRouteType;

            // Determine based on task characteristics;
            if (task.Metadata.TryGetValue("BroadcastRequired", out var broadcast) &&
                (bool)broadcast)
                return RouteType.Broadcast;

            if (task.Metadata.TryGetValue("ChainRequired", out var chain) &&
                (bool)chain)
                return RouteType.Chain;

            if (task.Metadata.TryGetValue("FanOutRequired", out var fanOut) &&
                (bool)fanOut)
                return RouteType.FanOut;

            if (destinations.Count == 1)
                return RouteType.Direct;

            if (task.Metadata.TryGetValue("GeoLocation", out var geoLocation))
                return RouteType.GeoLocation;

            // Default to load balanced for multiple destinations;
            return RouteType.LoadBalanced;
        }

        private async Task HandleDirectRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            // For direct routing, select the best single destination;
            var bestDestination = destinations;
                .OrderByDescending(d => d.PerformanceScore)
                .ThenBy(d => d.CurrentLoad)
                .FirstOrDefault();

            if (bestDestination != null)
            {
                decision.SelectedDestinations.Add(bestDestination);
                decision.RoutingReason = "Direct route to best performing executor";
            }
        }

        private async Task HandleLoadBalancedRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            var selected = await _loadBalancer.SelectDestinationsAsync(
                task, destinations, 1, cancellationToken);

            decision.SelectedDestinations.AddRange(selected);
            decision.RoutingReason = "Load balanced routing";
        }

        private async Task HandleBroadcastRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            // Broadcast to all suitable destinations;
            decision.SelectedDestinations.AddRange(destinations);
            decision.RoutingReason = "Broadcast to all available executors";
        }

        private async Task HandleFanOutRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            // Fan out to specialized executors based on capabilities;
            if (task.Metadata.TryGetValue("FanOutPattern", out var patternObj) &&
                patternObj is Dictionary<string, List<string>> pattern)
            {
                foreach (var kvp in pattern)
                {
                    var specializedDestinations = destinations;
                        .Where(d => kvp.Value.All(cap => d.Capabilities.ContainsKey(cap)))
                        .ToList();

                    if (specializedDestinations.Count > 0)
                    {
                        var selected = await _loadBalancer.SelectDestinationsAsync(
                            task, specializedDestinations, 1, cancellationToken);

                        decision.SelectedDestinations.AddRange(selected);
                    }
                }
            }

            if (decision.SelectedDestinations.Count == 0)
            {
                // Fallback to load balanced;
                await HandleLoadBalancedRouting(task, destinations, decision, cancellationToken);
            }

            decision.RoutingReason = "Fan-out to specialized executors";
        }

        private async Task HandleChainRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            // Chain routing requires specific sequence;
            if (task.Metadata.TryGetValue("ChainSequence", out var sequenceObj) &&
                sequenceObj is List<string> sequence)
            {
                foreach (var executorType in sequence)
                {
                    var nextDestination = destinations;
                        .FirstOrDefault(d => d.ExecutorType == executorType &&
                                           d.CanHandleTask(task));

                    if (nextDestination != null)
                    {
                        decision.SelectedDestinations.Add(nextDestination);
                    }
                    else;
                    {
                        decision.RoutingReason = $"Chain broken: No {executorType} executor available";
                        decision.SelectedDestinations.Clear();
                        return;
                    }
                }
            }

            decision.RoutingReason = "Chained routing sequence";
        }

        private async Task HandleContentBasedRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            // Route based on task content analysis;
            if (task.Metadata.TryGetValue("ContentType", out var contentType))
            {
                var contentSpecificDestinations = destinations;
                    .Where(d => d.Capabilities.ContainsKey($"Process_{contentType}"))
                    .ToList();

                if (contentSpecificDestinations.Count > 0)
                {
                    var selected = await _loadBalancer.SelectDestinationsAsync(
                        task, contentSpecificDestinations, 1, cancellationToken);

                    decision.SelectedDestinations.AddRange(selected);
                    decision.RoutingReason = $"Content-based routing for {contentType}";
                    return;
                }
            }

            // Fallback to load balanced;
            await HandleLoadBalancedRouting(task, destinations, decision, cancellationToken);
            decision.RoutingReason = "Content-based routing (fallback to load balanced)";
        }

        private async Task HandleFirstAvailableRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            // Select first available destination;
            var firstAvailable = destinations;
                .OrderBy(d => d.CurrentLoad)
                .ThenByDescending(d => d.PerformanceScore)
                .FirstOrDefault();

            if (firstAvailable != null)
            {
                decision.SelectedDestinations.Add(firstAvailable);
                decision.RoutingReason = "First available executor routing";
            }
        }

        private async Task HandleGeoLocationRouting(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            RoutingDecision decision,
            CancellationToken cancellationToken)
        {
            if (task.Metadata.TryGetValue("TargetLocation", out var targetLocationObj) &&
                targetLocationObj is GeoLocation targetLocation)
            {
                // Route to nearest geographical location;
                var nearestDestination = destinations;
                    .Where(d => d.Location != null)
                    .OrderBy(d => d.Location.DistanceTo(targetLocation))
                    .FirstOrDefault();

                if (nearestDestination != null)
                {
                    decision.SelectedDestinations.Add(nearestDestination);
                    decision.RoutingReason = $"Geo-location routing to {nearestDestination.Location.Region}";
                    return;
                }
            }

            // Fallback to load balanced;
            await HandleLoadBalancedRouting(task, destinations, decision, cancellationToken);
            decision.RoutingReason = "Geo-location routing (fallback to load balanced)";
        }

        private double CalculateRoutingScore(RoutingDecision decision)
        {
            if (decision.SelectedDestinations.Count == 0)
                return 0.0;

            double score = 0.0;

            // Base score from destinations;
            foreach (var dest in decision.SelectedDestinations)
            {
                score += dest.PerformanceScore * (1.0 - dest.CurrentLoad / 100.0);
            }

            score /= decision.SelectedDestinations.Count;

            // Adjust based on route type;
            switch (decision.RouteType)
            {
                case RouteType.Direct:
                    score *= 1.1;
                    break;
                case RouteType.LoadBalanced:
                    score *= 1.05;
                    break;
                case RouteType.GeoLocation:
                    score *= 1.2; // Geo routing is premium;
                    break;
            }

            return Math.Min(score, 1.0);
        }

        private void UpdateMetrics(RoutingDecision decision, double routingTimeMs, bool success)
        {
            lock (_metricsLock)
            {
                _metrics.TotalRoutes++;

                if (success)
                    _metrics.SuccessfulRoutes++;
                else;
                    _metrics.FailedRoutes++;

                // Update average routing time;
                var totalTime = _metrics.AverageRoutingTimeMs * (_metrics.TotalRoutes - 1) + routingTimeMs;
                _metrics.AverageRoutingTimeMs = totalTime / _metrics.TotalRoutes;

                // Update average route score;
                var totalScore = _metrics.AverageRouteScore * (_metrics.TotalRoutes - 1) + decision.RoutingScore;
                _metrics.AverageRouteScore = totalScore / _metrics.TotalRoutes;

                // Update route type statistics;
                if (!_metrics.RoutesByType.ContainsKey(decision.RouteType))
                    _metrics.RoutesByType[decision.RouteType] = 0;

                _metrics.RoutesByType[decision.RouteType]++;
            }
        }

        private void UpdateExecutorScores()
        {
            // Update executor performance scores based on routing success;
            lock (_metricsLock)
            {
                // This would be implemented with actual performance data;
                // For now, it's a placeholder for the scoring logic;
            }
        }

        #endregion;
    }

    /// <summary>
    /// Adaptive load balancer for routing decisions;
    /// </summary>
    public class AdaptiveLoadBalancer;
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, LoadStatistics> _executorStats = new();
        private readonly object _statsLock = new();

        public AdaptiveLoadBalancer(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<List<RouteDestination>> SelectDestinationsAsync(
            PrioritizedTask task,
            List<RouteDestination> destinations,
            int count,
            CancellationToken cancellationToken)
        {
            if (destinations.Count == 0 || count <= 0)
                return new List<RouteDestination>();

            // Calculate selection scores for each destination;
            var scoredDestinations = destinations;
                .Select(d => new;
                {
                    Destination = d,
                    Score = CalculateSelectionScore(d, task)
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            // Select top N destinations;
            var selected = scoredDestinations;
                .Take(count)
                .Select(x => x.Destination)
                .ToList();

            // Update statistics;
            UpdateSelectionStatistics(selected);

            return selected;
        }

        private double CalculateSelectionScore(RouteDestination destination, PrioritizedTask task)
        {
            double score = 0.0;

            // Performance score (40%)
            score += destination.PerformanceScore * 0.4;

            // Load factor (30%)
            score += (1.0 - destination.CurrentLoad / 100.0) * 0.3;

            // Capability match (20%)
            double capabilityScore = 0.0;
            if (task.Metadata.TryGetValue("RequiredCapabilities", out var requiredCaps) &&
                requiredCaps is List<string> capabilities)
            {
                var matched = capabilities.Count(c => destination.Capabilities.ContainsKey(c));
                capabilityScore = (double)matched / capabilities.Count;
            }
            score += capabilityScore * 0.2;

            // Historical performance (10%)
            lock (_statsLock)
            {
                if (_executorStats.TryGetValue(destination.ExecutorId, out var stats))
                {
                    score += stats.SuccessRate * 0.1;
                }
            }

            return score;
        }

        private void UpdateSelectionStatistics(List<RouteDestination> selected)
        {
            lock (_statsLock)
            {
                foreach (var dest in selected)
                {
                    if (!_executorStats.ContainsKey(dest.ExecutorId))
                    {
                        _executorStats[dest.ExecutorId] = new LoadStatistics();
                    }

                    _executorStats[dest.ExecutorId].TotalSelections++;
                    _executorStats[dest.ExecutorId].LastSelectedTime = DateTime.UtcNow;
                }
            }
        }

        public void UpdateExecutionResult(string executorId, bool success, TimeSpan executionTime)
        {
            lock (_statsLock)
            {
                if (_executorStats.TryGetValue(executorId, out var stats))
                {
                    stats.TotalExecutions++;

                    if (success)
                        stats.SuccessfulExecutions++;
                    else;
                        stats.FailedExecutions++;

                    stats.TotalExecutionTime += executionTime;
                    stats.AverageExecutionTime = stats.TotalExecutionTime / stats.TotalExecutions;
                }
            }
        }

        private class LoadStatistics;
        {
            public int TotalSelections { get; set; }
            public int TotalExecutions { get; set; }
            public int SuccessfulExecutions { get; set; }
            public int FailedExecutions { get; set; }
            public TimeSpan TotalExecutionTime { get; set; }
            public TimeSpan AverageExecutionTime { get; set; }
            public DateTime LastSelectedTime { get; set; }

            public double SuccessRate => TotalExecutions > 0 ?
                (double)SuccessfulExecutions / TotalExecutions : 1.0;
        }
    }

    /// <summary>
    /// Main Router Engine for intelligent task routing;
    /// </summary>
    public class RouterEngine : IDisposable
    {
        private readonly ILogger _logger;
        private readonly PriorityManager _priorityManager;
        private readonly IRoutingStrategy _routingStrategy;
        private readonly ConcurrentDictionary<string, RouteDestination> _destinations = new();
        private readonly Timer _healthCheckTimer;
        private readonly Timer _metricsTimer;
        private readonly SemaphoreSlim _routingLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private bool _isDisposed;

        // Routing statistics;
        private readonly RoutingMetrics _routingMetrics = new();
        private readonly object _metricsLock = new();
        private long _totalTasksRouted;
        private long _totalRoutingFailures;

        /// <summary>
        /// Event fired when task is routed;
        /// </summary>
        public event EventHandler<TaskRoutedEventArgs> TaskRouted;

        /// <summary>
        /// Event fired when destination status changes;
        /// </summary>
        public event EventHandler<DestinationStatusChangedEventArgs> DestinationStatusChanged;

        /// <summary>
        /// Event fired when routing metrics are updated;
        /// </summary>
        public event EventHandler<RoutingMetricsUpdatedEventArgs> RoutingMetricsUpdated;

        public RouterEngine(
            ILogger logger,
            PriorityManager priorityManager,
            IRoutingStrategy routingStrategy = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _priorityManager = priorityManager ?? throw new ArgumentNullException(nameof(priorityManager));
            _routingStrategy = routingStrategy ?? new IntelligentRoutingStrategy(logger);

            // Start health checks every 60 seconds;
            _healthCheckTimer = new Timer(PerformHealthChecks, null,
                TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            // Start metrics collection every 30 seconds;
            _metricsTimer = new Timer(CollectAndUpdateMetrics, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            _logger.LogInformation("RouterEngine initialized successfully");
        }

        /// <summary>
        /// Register a new route destination;
        /// </summary>
        public async Task<bool> RegisterDestinationAsync(RouteDestination destination,
            CancellationToken cancellationToken = default)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            if (string.IsNullOrWhiteSpace(destination.ExecutorId))
                throw new ArgumentException("ExecutorId is required", nameof(destination));

            var existing = _destinations.GetOrAdd(destination.ExecutorId, destination);

            if (existing != destination)
            {
                // Update existing destination;
                _destinations[destination.ExecutorId] = destination;
            }

            destination.LastHeartbeat = DateTime.UtcNow;
            destination.IsActive = true;

            _logger.LogInformation($"Destination registered: {destination.ExecutorName} ({destination.ExecutorId}) " +
                                  $"on {destination.MachineName}:{destination.Port}");

            OnDestinationStatusChanged(destination, true, "Registered");

            return true;
        }

        /// <summary>
        /// Unregister a route destination;
        /// </summary>
        public async Task<bool> UnregisterDestinationAsync(string executorId,
            CancellationToken cancellationToken = default)
        {
            if (_destinations.TryRemove(executorId, out var destination))
            {
                destination.IsActive = false;

                _logger.LogInformation($"Destination unregistered: {executorId}");
                OnDestinationStatusChanged(destination, false, "Unregistered");

                return true;
            }

            return false;
        }

        /// <summary>
        /// Route a task to appropriate destination(s)
        /// </summary>
        public async Task<RoutingDecision> RouteTaskAsync(PrioritizedTask task,
            CancellationToken cancellationToken = default)
        {
            ValidateTask(task);

            await _routingLock.WaitAsync(cancellationToken);
            try
            {
                var startTime = DateTime.UtcNow;

                // Get available destinations;
                var availableDestinations = _destinations.Values;
                    .Where(d => d.IsActive)
                    .ToList()
                    .AsReadOnly();

                if (availableDestinations.Count == 0)
                {
                    _logger.LogWarning($"No active destinations available for routing task: {task.TaskName}");
                    return new RoutingDecision;
                    {
                        Task = task,
                        RoutingReason = "No active destinations available",
                        RoutingScore = 0;
                    };
                }

                // Make routing decision;
                var decision = await _routingStrategy.MakeRoutingDecisionAsync(
                    task, availableDestinations, cancellationToken);

                if (decision.IsValid)
                {
                    // Update task metadata with routing information;
                    task.Metadata["RoutingDecisionId"] = decision.DecisionId;
                    task.Metadata["RouteType"] = decision.RouteType;
                    task.Metadata["SelectedDestinations"] = decision.SelectedDestinations;
                        .Select(d => d.ExecutorId)
                        .ToList();

                    Interlocked.Increment(ref _totalTasksRouted);

                    var routingTime = DateTime.UtcNow - startTime;
                    UpdateRoutingMetrics(decision, routingTime, true);

                    _logger.LogInformation($"Task routed: {task.TaskName} -> " +
                                          $"{decision.SelectedDestinations.Count} destinations, " +
                                          $"Score: {decision.RoutingScore:F2}");

                    OnTaskRouted(task, decision);

                    return decision;
                }
                else;
                {
                    Interlocked.Increment(ref _totalRoutingFailures);
                    UpdateRoutingMetrics(decision, DateTime.UtcNow - startTime, false);

                    _logger.LogWarning($"Routing failed for task: {task.TaskName}, Reason: {decision.RoutingReason}");

                    return decision;
                }
            }
            finally
            {
                _routingLock.Release();
            }
        }

        /// <summary>
        /// Batch route multiple tasks;
        /// </summary>
        public async Task<List<RoutingDecision>> BatchRouteTasksAsync(
            List<PrioritizedTask> tasks,
            CancellationToken cancellationToken = default)
        {
            var decisions = new List<RoutingDecision>();
            var batchStartTime = DateTime.UtcNow;

            foreach (var task in tasks)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var decision = await RouteTaskAsync(task, cancellationToken);
                decisions.Add(decision);
            }

            var batchTime = DateTime.UtcNow - batchStartTime;
            _logger.LogInformation($"Batch routing completed: {decisions.Count} tasks, " +
                                  $"Time: {batchTime.TotalMilliseconds}ms");

            return decisions;
        }

        /// <summary>
        /// Get all registered destinations;
        /// </summary>
        public IReadOnlyList<RouteDestination> GetDestinations(bool activeOnly = true)
        {
            var destinations = activeOnly ?
                _destinations.Values.Where(d => d.IsActive).ToList() :
                _destinations.Values.ToList();

            return destinations.AsReadOnly();
        }

        /// <summary>
        /// Get destination by ID;
        /// </summary>
        public RouteDestination GetDestination(string executorId)
        {
            return _destinations.TryGetValue(executorId, out var destination) ? destination : null;
        }

        /// <summary>
        /// Update destination status;
        /// </summary>
        public async Task<bool> UpdateDestinationStatusAsync(string executorId, bool isActive,
            string reason = null, CancellationToken cancellationToken = default)
        {
            if (_destinations.TryGetValue(executorId, out var destination))
            {
                var previousStatus = destination.IsActive;
                destination.IsActive = isActive;
                destination.LastHeartbeat = DateTime.UtcNow;

                if (previousStatus != isActive)
                {
                    _logger.LogInformation($"Destination status changed: {executorId} -> " +
                                          $"{(isActive ? "Active" : "Inactive")}, Reason: {reason}");

                    OnDestinationStatusChanged(destination, isActive, reason);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Update destination load;
        /// </summary>
        public async Task<bool> UpdateDestinationLoadAsync(string executorId, int currentLoad,
            CancellationToken cancellationToken = default)
        {
            if (_destinations.TryGetValue(executorId, out var destination))
            {
                destination.CurrentLoad = Math.Max(0, Math.Min(100, currentLoad));
                destination.LastHeartbeat = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get routing statistics;
        /// </summary>
        public RouterEngineStats GetStatistics()
        {
            lock (_metricsLock)
            {
                var activeDestinations = _destinations.Values.Count(d => d.IsActive);
                var totalDestinations = _destinations.Count;

                return new RouterEngineStats;
                {
                    TotalDestinations = totalDestinations,
                    ActiveDestinations = activeDestinations,
                    TotalTasksRouted = _totalTasksRouted,
                    TotalRoutingFailures = _totalRoutingFailures,
                    SuccessRate = _totalTasksRouted > 0 ?
                        (double)(_totalTasksRouted - _totalRoutingFailures) / _totalTasksRouted * 100 : 100,
                    RoutingMetrics = _routingMetrics,
                    DestinationLoadDistribution = GetDestinationLoadDistribution(),
                    RouteTypeDistribution = _routingMetrics.RoutesByType;
                };
            }
        }

        /// <summary>
        /// Perform emergency rerouting for failed destinations;
        /// </summary>
        public async Task<int> EmergencyRerouteAsync(string failedExecutorId,
            CancellationToken cancellationToken = default)
        {
            // Find tasks routed to failed executor;
            var tasksToReroute = new List<PrioritizedTask>();
            // This would query tasks from the priority manager;

            int reroutedCount = 0;

            foreach (var task in tasksToReroute)
            {
                var newDecision = await RouteTaskAsync(task, cancellationToken);
                if (newDecision.IsValid)
                    reroutedCount++;
            }

            if (reroutedCount > 0)
            {
                _logger.LogWarning($"Emergency reroute completed: {reroutedCount} tasks rerouted from failed executor {failedExecutorId}");
            }

            return reroutedCount;
        }

        /// <summary>
        /// Clear all destinations;
        /// </summary>
        public async Task ClearDestinationsAsync(CancellationToken cancellationToken = default)
        {
            await _routingLock.WaitAsync(cancellationToken);
            try
            {
                var count = _destinations.Count;
                _destinations.Clear();

                _logger.LogInformation($"Cleared all {count} destinations");
            }
            finally
            {
                _routingLock.Release();
            }
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _cts.Cancel();
            _healthCheckTimer?.Dispose();
            _metricsTimer?.Dispose();
            _routingLock?.Dispose();
            _cts.Dispose();

            _isDisposed = true;
            _logger.LogInformation("RouterEngine disposed");
        }

        #region Private Methods;

        private void ValidateTask(PrioritizedTask task)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));
        }

        private void PerformHealthChecks(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var inactiveThreshold = TimeSpan.FromMinutes(5);

                foreach (var destination in _destinations.Values.ToList())
                {
                    var timeSinceHeartbeat = now - destination.LastHeartbeat;

                    if (destination.IsActive && timeSinceHeartbeat > inactiveThreshold)
                    {
                        // Mark as inactive;
                        destination.IsActive = false;
                        _logger.LogWarning($"Destination marked inactive due to no heartbeat: {destination.ExecutorId}, " +
                                          $"Last: {destination.LastHeartbeat}");

                        OnDestinationStatusChanged(destination, false, "No heartbeat");
                    }

                    // Perform network connectivity check;
                    if (!string.IsNullOrEmpty(destination.IPAddress))
                    {
                        var isReachable = CheckNetworkConnectivity(destination.IPAddress, destination.Port);

                        if (destination.IsActive && !isReachable)
                        {
                            destination.IsActive = false;
                            _logger.LogWarning($"Destination marked inactive due to network unreachable: {destination.ExecutorId}");
                            OnDestinationStatusChanged(destination, false, "Network unreachable");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during health checks: {ex.Message}");
            }
        }

        private bool CheckNetworkConnectivity(string ipAddress, int port)
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(ipAddress, 1000); // 1 second timeout;
                return reply?.Status == IPStatus.Success;
            }
            catch
            {
                return false;
            }
        }

        private void CollectAndUpdateMetrics(object state)
        {
            try
            {
                lock (_metricsLock)
                {
                    _routingStrategy.UpdateStrategyMetrics(_routingMetrics);
                    OnRoutingMetricsUpdated(_routingMetrics);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error collecting routing metrics: {ex.Message}");
            }
        }

        private void UpdateRoutingMetrics(RoutingDecision decision, TimeSpan routingTime, bool success)
        {
            lock (_metricsLock)
            {
                _routingMetrics.TotalRoutes++;

                if (success)
                    _routingMetrics.SuccessfulRoutes++;
                else;
                    _routingMetrics.FailedRoutes++;

                // Update average routing time;
                var totalTime = _routingMetrics.AverageRoutingTimeMs * (_routingMetrics.TotalRoutes - 1) +
                               routingTime.TotalMilliseconds;
                _routingMetrics.AverageRoutingTimeMs = totalTime / _routingMetrics.TotalRoutes;

                // Update average route score;
                var totalScore = _routingMetrics.AverageRouteScore * (_routingMetrics.TotalRoutes - 1) +
                                decision.RoutingScore;
                _routingMetrics.AverageRouteScore = totalScore / _routingMetrics.TotalRoutes;

                // Update route type statistics;
                if (!_routingMetrics.RoutesByType.ContainsKey(decision.RouteType))
                    _routingMetrics.RoutesByType[decision.RouteType] = 0;

                _routingMetrics.RoutesByType[decision.RouteType]++;
            }
        }

        private Dictionary<string, int> GetDestinationLoadDistribution()
        {
            return _destinations.Values;
                .GroupBy(d => GetLoadCategory(d.CurrentLoad))
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private string GetLoadCategory(int load)
        {
            if (load < 30) return "Low";
            if (load < 70) return "Medium";
            if (load < 90) return "High";
            return "Critical";
        }

        private void OnTaskRouted(PrioritizedTask task, RoutingDecision decision)
        {
            TaskRouted?.Invoke(this, new TaskRoutedEventArgs;
            {
                TaskId = task.TaskId,
                TaskName = task.TaskName,
                RoutingDecision = decision,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnDestinationStatusChanged(RouteDestination destination, bool isActive, string reason)
        {
            DestinationStatusChanged?.Invoke(this, new DestinationStatusChangedEventArgs;
            {
                ExecutorId = destination.ExecutorId,
                ExecutorName = destination.ExecutorName,
                IsActive = isActive,
                Reason = reason,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnRoutingMetricsUpdated(RoutingMetrics metrics)
        {
            RoutingMetricsUpdated?.Invoke(this, new RoutingMetricsUpdatedEventArgs;
            {
                Metrics = metrics,
                Timestamp = DateTime.UtcNow;
            });
        }

        #endregion;
    }

    #region Event Args Classes;

    /// <summary>
    /// Event arguments for task routed events;
    /// </summary>
    public class TaskRoutedEventArgs : EventArgs;
    {
        public Guid TaskId { get; set; }
        public string TaskName { get; set; }
        public RoutingDecision RoutingDecision { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for destination status change events;
    /// </summary>
    public class DestinationStatusChangedEventArgs : EventArgs;
    {
        public string ExecutorId { get; set; }
        public string ExecutorName { get; set; }
        public bool IsActive { get; set; }
        public string Reason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for routing metrics update events;
    /// </summary>
    public class RoutingMetricsUpdatedEventArgs : EventArgs;
    {
        public RoutingMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Router engine statistics;
    /// </summary>
    public class RouterEngineStats;
    {
        public int TotalDestinations { get; set; }
        public int ActiveDestinations { get; set; }
        public long TotalTasksRouted { get; set; }
        public long TotalRoutingFailures { get; set; }
        public double SuccessRate { get; set; }
        public RoutingMetrics RoutingMetrics { get; set; }
        public Dictionary<string, int> DestinationLoadDistribution { get; set; }
        public Dictionary<RouteType, int> RouteTypeDistribution { get; set; }

        public override string ToString()
        {
            return $"Destinations: {ActiveDestinations}/{TotalDestinations} active, " +
                   $"Tasks Routed: {TotalTasksRouted}, Success Rate: {SuccessRate:F1}%, " +
                   $"Avg Routing Time: {RoutingMetrics?.AverageRoutingTimeMs:F1}ms";
        }
    }

    #endregion;
}
