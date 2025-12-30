using Microsoft.Extensions.Logging;
using NEDA.Automation.Executors;
using NEDA.Automation.ScenarioPlanner;
using NEDA.Brain.DecisionMaking;
using NEDA.Brain.NeuralNetwork.PatternRecognition;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Automation.TaskRouter;
{
    /// <summary>
    /// Intelligent Router - AI-powered task routing and distribution system;
    /// with adaptive learning and dynamic resource allocation;
    /// </summary>
    public class IntelligentRouter : IIntelligentRouter, IDisposable;
    {
        private readonly ILogger<IntelligentRouter> _logger;
        private readonly IRoutingStrategyEngine _strategyEngine;
        private readonly IResourceOptimizer _resourceOptimizer;
        private readonly IPatternRecognizer _patternRecognizer;
        private readonly IRoutingConfig _config;

        // Active task queues by priority;
        private readonly ConcurrentDictionary<RoutingPriority, PriorityTaskQueue> _priorityQueues;

        // Worker pool management;
        private readonly WorkerPoolManager _workerPool;

        // Routing history and analytics;
        private readonly RoutingHistoryStore _historyStore;

        // Real-time monitoring;
        private readonly RouterMetricsCollector _metricsCollector;

        // Load balancing engine;
        private readonly LoadBalancer _loadBalancer;

        // Adaptive learning system;
        private readonly RoutingLearningEngine _learningEngine;

        // Fault tolerance manager;
        private readonly FaultToleranceManager _faultManager;

        // Synchronization;
        private readonly SemaphoreSlim _routingLock;
        private readonly CancellationTokenSource _shutdownTokenSource;
        private bool _disposed;
        private bool _isRunning;

        /// <summary>
        /// Gets the current router status;
        /// </summary>
        public RouterStatus Status { get; private set; }

        /// <summary>
        /// Gets the total number of tasks in all queues;
        /// </summary>
        public int TotalQueuedTasks => _priorityQueues.Values.Sum(q => q.Count);

        /// <summary>
        /// Gets the number of active workers;
        /// </summary>
        public int ActiveWorkers => _workerPool.ActiveWorkerCount;

        /// <summary>
        /// Gets router utilization percentage;
        /// </summary>
        public double UtilizationPercentage => _metricsCollector.CurrentUtilization;

        /// <summary>
        /// Initializes a new instance of IntelligentRouter;
        /// </summary>
        public IntelligentRouter(
            ILogger<IntelligentRouter> logger,
            IRoutingStrategyEngine strategyEngine,
            IResourceOptimizer resourceOptimizer,
            IPatternRecognizer patternRecognizer,
            IRoutingConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _strategyEngine = strategyEngine ?? throw new ArgumentNullException(nameof(strategyEngine));
            _resourceOptimizer = resourceOptimizer ?? throw new ArgumentNullException(nameof(resourceOptimizer));
            _patternRecognizer = patternRecognizer ?? throw new ArgumentNullException(nameof(patternRecognizer));
            _config = config ?? RoutingConfig.Default;

            // Initialize components;
            _priorityQueues = new ConcurrentDictionary<RoutingPriority, PriorityTaskQueue>();
            InitializePriorityQueues();

            _workerPool = new WorkerPoolManager(_config.WorkerPoolSize, _config.MaxWorkers, logger);
            _historyStore = new RoutingHistoryStore(_config.HistoryRetention);
            _metricsCollector = new RouterMetricsCollector();
            _loadBalancer = new LoadBalancer(_config.LoadBalancingStrategy);
            _learningEngine = new RoutingLearningEngine(patternRecognizer);
            _faultManager = new FaultToleranceManager(_config.FaultToleranceConfig);

            _routingLock = new SemaphoreSlim(1, 1);
            _shutdownTokenSource = new CancellationTokenSource();

            Status = RouterStatus.Initialized;

            _logger.LogInformation("IntelligentRouter initialized with {Workers} workers, Strategy: {Strategy}",
                _config.InitialWorkers, _config.DefaultRoutingStrategy);
        }

        /// <summary>
        /// Starts the router and begins processing tasks;
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Router is already running");
                return;
            }

            await _routingLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Starting IntelligentRouter...");

                // Start worker pool;
                await _workerPool.StartAsync(cancellationToken);

                // Start background processors;
                StartBackgroundProcessors(cancellationToken);

                Status = RouterStatus.Running;
                _isRunning = true;

                _logger.LogInformation("IntelligentRouter started successfully");
            }
            finally
            {
                _routingLock.Release();
            }
        }

        /// <summary>
        /// Stops the router and completes all pending tasks;
        /// </summary>
        public async Task StopAsync(bool graceful = true, CancellationToken cancellationToken = default)
        {
            if (!_isRunning)
            {
                return;
            }

            await _routingLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Stopping IntelligentRouter (Graceful: {Graceful})...", graceful);

                Status = graceful ? RouterStatus.StoppingGracefully : RouterStatus.Stopping;

                // Signal shutdown;
                _shutdownTokenSource.Cancel();

                if (graceful)
                {
                    // Complete pending tasks;
                    await CompletePendingTasksAsync(cancellationToken);
                }

                // Stop worker pool;
                await _workerPool.StopAsync(graceful, cancellationToken);

                Status = RouterStatus.Stopped;
                _isRunning = false;

                _logger.LogInformation("IntelligentRouter stopped");
            }
            finally
            {
                _routingLock.Release();
            }
        }

        /// <summary>
        /// Routes a task to the appropriate handler based on intelligent analysis;
        /// </summary>
        public async Task<RoutingResult> RouteTaskAsync(RoutableTask task, RoutingContext context = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("IntelligentRouter.RouteTask"))
            {
                ValidateTask(task);

                try
                {
                    _metricsCollector.RecordTaskArrival();

                    // Analyze task for intelligent routing;
                    var analysis = await AnalyzeTaskAsync(task, context);

                    // Determine optimal routing strategy;
                    var routingDecision = await MakeRoutingDecisionAsync(task, analysis, context);

                    // Apply load balancing;
                    var balancedDecision = await ApplyLoadBalancingAsync(routingDecision);

                    // Execute routing;
                    var result = await ExecuteRoutingAsync(task, balancedDecision, context);

                    // Record history for learning;
                    await RecordRoutingHistoryAsync(task, routingDecision, result);

                    // Update adaptive learning;
                    await UpdateLearningModelAsync(task, routingDecision, result);

                    _metricsCollector.RecordTaskRouted(result.Success);

                    return result;
                }
                catch (Exception ex)
                {
                    _metricsCollector.RecordRoutingError();
                    _logger.LogError(ex, "Error routing task {TaskId}", task.Id);

                    // Attempt fault recovery;
                    var recoveryResult = await _faultManager.HandleRoutingFailureAsync(task, ex);

                    return RoutingResult.Failure(task.Id, $"Routing failed: {ex.Message}", recoveryResult);
                }
            }
        }

        /// <summary>
        /// Routes multiple tasks in batch with optimization;
        /// </summary>
        public async Task<BatchRoutingResult> RouteBatchAsync(
            IEnumerable<RoutableTask> tasks,
            BatchRoutingOptions options = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("IntelligentRouter.RouteBatch"))
            {
                options ??= BatchRoutingOptions.Default;

                try
                {
                    _metricsCollector.RecordBatchArrival(tasks.Count());

                    var taskList = tasks.ToList();
                    var batchId = Guid.NewGuid();

                    _logger.LogDebug("Starting batch routing for {Count} tasks, BatchId: {BatchId}",
                        taskList.Count, batchId);

                    // Analyze batch for optimization opportunities;
                    var batchAnalysis = await AnalyzeBatchAsync(taskList, options);

                    // Group tasks for optimized routing;
                    var taskGroups = await GroupTasksForOptimizedRoutingAsync(taskList, batchAnalysis);

                    // Route each group in parallel;
                    var routingTasks = taskGroups.Select(group =>
                        RouteTaskGroupAsync(group, options)).ToList();

                    var results = await Task.WhenAll(routingTasks);

                    // Aggregate results;
                    var aggregatedResult = AggregateBatchResults(results, batchId);

                    // Optimize resource allocation based on batch results;
                    await OptimizeResourceAllocationAsync(aggregatedResult);

                    _metricsCollector.RecordBatchCompleted(aggregatedResult);

                    return aggregatedResult;
                }
                catch (Exception ex)
                {
                    _metricsCollector.RecordBatchError();
                    _logger.LogError(ex, "Error in batch routing");
                    throw new RoutingException("Batch routing failed", ex);
                }
            }
        }

        /// <summary>
        /// Gets the optimal route for a task without executing it;
        /// </summary>
        public async Task<RoutingPlan> GetRoutingPlanAsync(RoutableTask task, RoutingContext context = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("IntelligentRouter.GetRoutingPlan"))
            {
                ValidateTask(task);

                try
                {
                    // Analyze task;
                    var analysis = await AnalyzeTaskAsync(task, context);

                    // Generate routing options;
                    var routingOptions = await GenerateRoutingOptionsAsync(task, analysis, context);

                    // Evaluate each option;
                    var evaluatedOptions = await EvaluateRoutingOptionsAsync(routingOptions, context);

                    // Select optimal plan;
                    var optimalPlan = SelectOptimalPlan(evaluatedOptions);

                    // Add optimization recommendations;
                    optimalPlan.OptimizationSuggestions = await GenerateOptimizationSuggestionsAsync(task, optimalPlan);

                    return optimalPlan;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generating routing plan for task {TaskId}", task.Id);
                    throw new RoutingPlanningException($"Failed to generate routing plan for task {task.Id}", ex);
                }
            }
        }

        /// <summary>
        /// Dynamically re-routes tasks based on current system conditions;
        /// </summary>
        public async Task<ReRoutingResult> ReRouteAsync(
            Guid taskId,
            ReRoutingReason reason,
            ReRoutingOptions options = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("IntelligentRouter.ReRoute"))
            {
                try
                {
                    _logger.LogInformation("Re-routing task {TaskId}, Reason: {Reason}", taskId, reason);

                    // Get current task state;
                    var taskState = await GetTaskStateAsync(taskId);
                    if (taskState == null)
                    {
                        return ReRoutingResult.Failure(taskId, $"Task {taskId} not found");
                    }

                    // Analyze re-routing needs;
                    var rerouteAnalysis = await AnalyzeReRoutingNeedsAsync(taskState, reason, options);

                    // Generate alternative routes;
                    var alternativeRoutes = await GenerateAlternativeRoutesAsync(taskState, rerouteAnalysis);

                    // Select best alternative;
                    var selectedRoute = await SelectBestAlternativeRouteAsync(alternativeRoutes, rerouteAnalysis);

                    // Execute re-routing;
                    var result = await ExecuteReRoutingAsync(taskState, selectedRoute, reason);

                    // Update routing history;
                    await UpdateRoutingHistoryForReRouteAsync(taskId, reason, result);

                    _metricsCollector.RecordReRouting(result.Success);

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error re-routing task {TaskId}", taskId);
                    return ReRoutingResult.Failure(taskId, $"Re-routing failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets current router statistics and metrics;
        /// </summary>
        public RouterStatistics GetStatistics()
        {
            return new RouterStatistics;
            {
                Timestamp = DateTime.UtcNow,
                TotalQueuedTasks = TotalQueuedTasks,
                ActiveWorkers = ActiveWorkers,
                UtilizationPercentage = UtilizationPercentage,
                QueueStats = GetQueueStatistics(),
                WorkerStats = _workerPool.GetStatistics(),
                PerformanceMetrics = _metricsCollector.GetMetrics(),
                LearningMetrics = _learningEngine.GetMetrics(),
                FaultToleranceStats = _faultManager.GetStatistics()
            };
        }

        /// <summary>
        /// Optimizes router configuration based on current workload;
        /// </summary>
        public async Task<OptimizationResult> OptimizeAsync(OptimizationScope scope = OptimizationScope.All)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("IntelligentRouter.Optimize"))
            {
                try
                {
                    _logger.LogInformation("Starting router optimization, Scope: {Scope}", scope);

                    var optimizationResults = new List<ComponentOptimizationResult>();

                    if (scope.HasFlag(OptimizationScope.WorkerPool))
                    {
                        var workerOptResult = await OptimizeWorkerPoolAsync();
                        optimizationResults.Add(workerOptResult);
                    }

                    if (scope.HasFlag(OptimizationScope.Queues))
                    {
                        var queueOptResult = await OptimizeQueuesAsync();
                        optimizationResults.Add(queueOptResult);
                    }

                    if (scope.HasFlag(OptimizationScope.RoutingStrategies))
                    {
                        var strategyOptResult = await OptimizeRoutingStrategiesAsync();
                        optimizationResults.Add(strategyOptResult);
                    }

                    if (scope.HasFlag(OptimizationScope.LoadBalancing))
                    {
                        var lbOptResult = await OptimizeLoadBalancingAsync();
                        optimizationResults.Add(lbOptResult);
                    }

                    var result = new OptimizationResult;
                    {
                        Timestamp = DateTime.UtcNow,
                        Scope = scope,
                        ComponentResults = optimizationResults,
                        OverallImprovement = CalculateOverallImprovement(optimizationResults)
                    };

                    _logger.LogInformation("Router optimization completed with {Improvement:P} improvement",
                        result.OverallImprovement);

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during router optimization");
                    throw new OptimizationException("Router optimization failed", ex);
                }
            }
        }

        /// <summary>
        /// Predicts future routing needs based on historical patterns;
        /// </summary>
        public async Task<RoutingPrediction> PredictRoutingNeedsAsync(
            TimeSpan predictionWindow,
            PredictionOptions options = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("IntelligentRouter.Predict"))
            {
                try
                {
                    options ??= PredictionOptions.Default;

                    // Analyze historical patterns;
                    var historicalPatterns = await _historyStore.AnalyzePatternsAsync(predictionWindow);

                    // Apply machine learning predictions;
                    var mlPredictions = await _learningEngine.PredictFutureWorkloadAsync(
                        historicalPatterns, predictionWindow);

                    // Consider current trends;
                    var currentTrends = _metricsCollector.AnalyzeCurrentTrends();

                    // Generate composite prediction;
                    var prediction = GenerateCompositePrediction(
                        historicalPatterns, mlPredictions, currentTrends, predictionWindow);

                    // Add resource recommendations;
                    prediction.ResourceRecommendations = await GenerateResourceRecommendationsAsync(prediction);

                    return prediction;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error predicting routing needs");
                    throw new PredictionException("Routing prediction failed", ex);
                }
            }
        }

        #region Private Methods;

        private void InitializePriorityQueues()
        {
            foreach (RoutingPriority priority in Enum.GetValues(typeof(RoutingPriority)))
            {
                _priorityQueues[priority] = new PriorityTaskQueue(priority, _config.QueueCapacity);
            }
        }

        private void StartBackgroundProcessors(CancellationToken cancellationToken)
        {
            // Start queue monitor;
            _ = Task.Run(async () => await MonitorQueuesAsync(cancellationToken), cancellationToken);

            // Start metrics collector;
            _ = Task.Run(async () => await CollectMetricsAsync(cancellationToken), cancellationToken);

            // Start learning processor;
            _ = Task.Run(async () => await ProcessLearningAsync(cancellationToken), cancellationToken);

            // Start fault detector;
            _ = Task.Run(async () => await DetectFaultsAsync(cancellationToken), cancellationToken);

            _logger.LogDebug("Background processors started");
        }

        private async Task CompletePendingTasksAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Completing {Count} pending tasks...", TotalQueuedTasks);

            var completionTasks = new List<Task>();

            foreach (var queue in _priorityQueues.Values.OrderByDescending(q => q.Priority))
            {
                while (queue.Count > 0)
                {
                    if (queue.TryDequeue(out var task))
                    {
                        completionTasks.Add(ProcessTaskForShutdownAsync(task, cancellationToken));
                    }

                    if (completionTasks.Count >= _config.ShutdownBatchSize)
                    {
                        await Task.WhenAll(completionTasks);
                        completionTasks.Clear();
                    }
                }
            }

            if (completionTasks.Any())
            {
                await Task.WhenAll(completionTasks);
            }

            _logger.LogInformation("All pending tasks completed");
        }

        private async Task ProcessTaskForShutdownAsync(RoutableTask task, CancellationToken cancellationToken)
        {
            try
            {
                // Try to complete task or save state;
                await _faultManager.HandleTaskDuringShutdownAsync(task, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing task {TaskId} during shutdown", task.Id);
            }
        }

        private void ValidateTask(RoutableTask task)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (task.Id == Guid.Empty)
                throw new ArgumentException("Task must have a valid ID", nameof(task));

            if (string.IsNullOrEmpty(task.TaskType))
                throw new ArgumentException("Task must have a type", nameof(task));
        }

        private async Task<TaskAnalysis> AnalyzeTaskAsync(RoutableTask task, RoutingContext context)
        {
            var analysis = new TaskAnalysis;
            {
                TaskId = task.Id,
                AnalysisTime = DateTime.UtcNow,
                TaskType = task.TaskType,
                EstimatedComplexity = EstimateTaskComplexity(task),
                ResourceRequirements = AnalyzeResourceRequirements(task),
                Dependencies = task.Dependencies?.ToList() ?? new List<string>(),
                Constraints = task.Constraints?.ToList() ?? new List<TaskConstraint>()
            };

            // Apply pattern recognition;
            if (_config.EnablePatternRecognition)
            {
                var patterns = await _patternRecognizer.RecognizeTaskPatternsAsync(task);
                analysis.RecognizedPatterns = patterns;
                analysis.PatternBasedComplexity = CalculatePatternBasedComplexity(patterns);
            }

            // Apply ML-based analysis if available;
            if (_config.EnableMLAnalysis)
            {
                var mlAnalysis = await _learningEngine.AnalyzeTaskAsync(task);
                analysis.MLAnalysis = mlAnalysis;
            }

            // Context-aware analysis;
            if (context != null)
            {
                analysis.ContextualFactors = AnalyzeContextualFactors(context);
                analysis.Priority = CalculateContextualPriority(task, context);
            }

            return analysis;
        }

        private async Task<RoutingDecision> MakeRoutingDecisionAsync(
            RoutableTask task,
            TaskAnalysis analysis,
            RoutingContext context)
        {
            // Get available routing strategies;
            var availableStrategies = await _strategyEngine.GetAvailableStrategiesAsync(
                task, analysis, context);

            // Evaluate each strategy;
            var evaluatedStrategies = new List<EvaluatedStrategy>();

            foreach (var strategy in availableStrategies)
            {
                var evaluation = await EvaluateRoutingStrategyAsync(strategy, task, analysis, context);
                evaluatedStrategies.Add(evaluation);
            }

            // Select best strategy;
            var selectedStrategy = SelectBestStrategy(evaluatedStrategies);

            // Determine target worker/queue;
            var target = await DetermineRoutingTargetAsync(selectedStrategy, task, analysis);

            return new RoutingDecision;
            {
                TaskId = task.Id,
                SelectedStrategy = selectedStrategy.Strategy,
                StrategyConfidence = selectedStrategy.ConfidenceScore,
                TargetQueue = target.Queue,
                TargetWorker = target.Worker,
                EstimatedProcessingTime = selectedStrategy.EstimatedProcessingTime,
                ResourceAllocation = selectedStrategy.ResourceAllocation,
                DecisionTime = DateTime.UtcNow;
            };
        }

        private async Task<RoutingDecision> ApplyLoadBalancingAsync(RoutingDecision decision)
        {
            if (!_config.EnableLoadBalancing)
                return decision;

            var loadBalanceResult = await _loadBalancer.BalanceDecisionAsync(decision, GetCurrentLoad());

            return new RoutingDecision;
            {
                TaskId = decision.TaskId,
                SelectedStrategy = decision.SelectedStrategy,
                StrategyConfidence = decision.StrategyConfidence,
                TargetQueue = loadBalanceResult.AdjustedQueue ?? decision.TargetQueue,
                TargetWorker = loadBalanceResult.AdjustedWorker ?? decision.TargetWorker,
                EstimatedProcessingTime = decision.EstimatedProcessingTime,
                ResourceAllocation = loadBalanceResult.AdjustedResources ?? decision.ResourceAllocation,
                DecisionTime = decision.DecisionTime,
                LoadBalancingApplied = true,
                LoadBalanceAdjustments = loadBalanceResult.Adjustments;
            };
        }

        private async Task<RoutingResult> ExecuteRoutingAsync(
            RoutableTask task,
            RoutingDecision decision,
            RoutingContext context)
        {
            try
            {
                // Get target queue;
                if (!_priorityQueues.TryGetValue(decision.TargetQueue, out var targetQueue))
                {
                    throw new RoutingException($"Target queue not found: {decision.TargetQueue}");
                }

                // Enqueue task;
                var enqueueResult = targetQueue.Enqueue(task, decision);
                if (!enqueueResult.Success)
                {
                    return RoutingResult.Failure(task.Id, $"Failed to enqueue task: {enqueueResult.ErrorMessage}");
                }

                // Dispatch to worker if specified;
                if (decision.TargetWorker != null)
                {
                    var dispatchResult = await DispatchToWorkerAsync(task, decision);
                    if (!dispatchResult.Success)
                    {
                        // Fallback to queue processing;
                        _logger.LogWarning("Worker dispatch failed for task {TaskId}, falling back to queue", task.Id);
                    }
                    else;
                    {
                        return RoutingResult.Success(task.Id, RoutingMethod.WorkerDirect, dispatchResult.WorkerId);
                    }
                }

                return RoutingResult.Success(task.Id, RoutingMethod.Queue, queuePriority: decision.TargetQueue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing routing for task {TaskId}", task.Id);
                throw new RoutingExecutionException($"Failed to execute routing for task {task.Id}", ex);
            }
        }

        private async Task DispatchToWorkerAsync(RoutableTask task, RoutingDecision decision)
        {
            var worker = await _workerPool.AcquireWorkerAsync(decision.ResourceAllocation);

            try
            {
                await worker.ExecuteTaskAsync(task, decision);
            }
            finally
            {
                _workerPool.ReleaseWorker(worker);
            }
        }

        private async Task RecordRoutingHistoryAsync(
            RoutableTask task,
            RoutingDecision decision,
            RoutingResult result)
        {
            var historyEntry = new RoutingHistoryEntry
            {
                TaskId = task.Id,
                TaskType = task.TaskType,
                RoutingDecision = decision,
                RoutingResult = result,
                Timestamp = DateTime.UtcNow,
                SystemState = GetCurrentSystemState()
            };

            await _historyStore.AddEntryAsync(historyEntry);
        }

        private async Task UpdateLearningModelAsync(
            RoutableTask task,
            RoutingDecision decision,
            RoutingResult result)
        {
            if (_config.EnableAdaptiveLearning)
            {
                var learningData = new RoutingLearningData;
                {
                    Task = task,
                    Decision = decision,
                    Result = result,
                    Context = GetCurrentSystemState()
                };

                await _learningEngine.LearnFromRoutingAsync(learningData);
            }
        }

        private async Task MonitorQueuesAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.QueueMonitoringInterval, cancellationToken);

                    foreach (var queue in _priorityQueues.Values)
                    {
                        await MonitorQueueHealthAsync(queue, cancellationToken);

                        // Process items if queue is getting full;
                        if (queue.UsagePercentage > _config.QueueProcessingThreshold)
                        {
                            await ProcessQueueBatchAsync(queue, cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in queue monitoring");
                }
            }
        }

        private async Task CollectMetricsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.MetricsCollectionInterval, cancellationToken);

                    var metrics = new RouterMetrics;
                    {
                        Timestamp = DateTime.UtcNow,
                        QueueMetrics = _priorityQueues.ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value.GetMetrics()),
                        WorkerMetrics = _workerPool.GetMetrics(),
                        SystemMetrics = GetSystemMetrics()
                    };

                    _metricsCollector.RecordMetrics(metrics);

                    // Check for alerts;
                    await CheckForAlertsAsync(metrics);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error collecting metrics");
                }
            }
        }

        private async Task ProcessLearningAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.LearningInterval, cancellationToken);

                    if (_config.EnableContinuousLearning)
                    {
                        await _learningEngine.ProcessLearningCycleAsync(cancellationToken);
                    }

                    // Optimize based on learning;
                    if (_config.EnableAutoOptimization)
                    {
                        await PerformAutoOptimizationAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in learning processing");
                }
            }
        }

        private async Task DetectFaultsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.FaultDetectionInterval, cancellationToken);

                    await _faultManager.DetectFaultsAsync(GetCurrentSystemState(), cancellationToken);

                    // Handle any detected faults;
                    var faults = _faultManager.GetDetectedFaults();
                    if (faults.Any())
                    {
                        await HandleDetectedFaultsAsync(faults, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in fault detection");
                }
            }
        }

        private SystemState GetCurrentSystemState()
        {
            return new SystemState;
            {
                Timestamp = DateTime.UtcNow,
                QueueStates = _priorityQueues.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.GetState()),
                WorkerPoolState = _workerPool.GetState(),
                SystemMetrics = GetSystemMetrics(),
                RoutingMetrics = _metricsCollector.GetCurrentMetrics()
            };
        }

        private SystemMetrics GetSystemMetrics()
        {
            return new SystemMetrics;
            {
                CpuUsage = EnvironmentMetrics.GetCpuUsage(),
                MemoryUsage = EnvironmentMetrics.GetMemoryUsage(),
                DiskUsage = EnvironmentMetrics.GetDiskUsage(),
                NetworkUsage = EnvironmentMetrics.GetNetworkUsage(),
                ProcessCount = EnvironmentMetrics.GetProcessCount()
            };
        }

        private async Task CheckForAlertsAsync(RouterMetrics metrics)
        {
            // Check queue alerts;
            foreach (var queueMetric in metrics.QueueMetrics)
            {
                if (queueMetric.Value.UsagePercentage > _config.QueueAlertThreshold)
                {
                    await RaiseAlertAsync(new QueueAlert;
                    {
                        QueuePriority = queueMetric.Key,
                        UsagePercentage = queueMetric.Value.UsagePercentage,
                        Message = $"Queue {queueMetric.Key} usage is high: {queueMetric.Value.UsagePercentage:F1}%"
                    });
                }
            }

            // Check worker alerts;
            var workerMetrics = metrics.WorkerMetrics;
            if (workerMetrics.UtilizationPercentage > _config.WorkerAlertThreshold)
            {
                await RaiseAlertAsync(new WorkerAlert;
                {
                    UtilizationPercentage = workerMetrics.UtilizationPercentage,
                    Message = $"Worker utilization is high: {workerMetrics.UtilizationPercentage:F1}%"
                });
            }

            // Check error rate alerts;
            var errorRate = _metricsCollector.ErrorRate;
            if (errorRate > _config.ErrorAlertThreshold)
            {
                await RaiseAlertAsync(new ErrorRateAlert;
                {
                    ErrorRate = errorRate,
                    Message = $"High error rate detected: {errorRate:P1}"
                });
            }
        }

        private async Task RaiseAlertAsync(RouterAlert alert)
        {
            alert.AlertTime = DateTime.UtcNow;
            alert.RouterId = GetHashCode();

            _metricsCollector.RecordAlert(alert);

            // Implement alert notification logic here;
            // This could integrate with NEDA.NotificationService;

            _logger.LogWarning("Router Alert: {AlertType} - {Message}", alert.GetType().Name, alert.Message);

            await Task.CompletedTask;
        }

        private async Task PerformAutoOptimizationAsync(CancellationToken cancellationToken)
        {
            try
            {
                var optimizationNeeded = await CheckOptimizationNeededAsync();
                if (optimizationNeeded)
                {
                    _logger.LogInformation("Auto-optimization triggered");

                    await OptimizeAsync(OptimizationScope.Minimal);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in auto-optimization");
            }
        }

        private async Task<bool> CheckOptimizationNeededAsync()
        {
            var metrics = _metricsCollector.GetCurrentMetrics();

            // Check performance degradation;
            if (metrics.AverageRoutingTime > _config.OptimizationThresholds.RoutingTimeThreshold)
                return true;

            // Check queue congestion;
            var maxQueueUsage = _priorityQueues.Values.Max(q => q.UsagePercentage);
            if (maxQueueUsage > _config.OptimizationThresholds.QueueUsageThreshold)
                return true;

            // Check worker efficiency;
            var workerMetrics = _workerPool.GetMetrics();
            if (workerMetrics.EfficiencyScore < _config.OptimizationThresholds.EfficiencyThreshold)
                return true;

            // Check error rate;
            if (_metricsCollector.ErrorRate > _config.OptimizationThresholds.ErrorRateThreshold)
                return true;

            return false;
        }

        private async Task HandleDetectedFaultsAsync(
            IEnumerable<RouterFault> faults,
            CancellationToken cancellationToken)
        {
            foreach (var fault in faults)
            {
                try
                {
                    _logger.LogWarning("Handling detected fault: {FaultType} - {Description}",
                        fault.FaultType, fault.Description);

                    var recoveryResult = await _faultManager.HandleFaultAsync(fault, cancellationToken);

                    if (recoveryResult.Success)
                    {
                        _logger.LogInformation("Fault recovered successfully: {FaultId}", fault.FaultId);
                    }
                    else;
                    {
                        _logger.LogError("Fault recovery failed: {FaultId}, Error: {Error}",
                            fault.FaultId, recoveryResult.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling fault {FaultId}", fault.FaultId);
                }
            }
        }

        #endregion;

        #region IDisposable Support;
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                _shutdownTokenSource?.Cancel();
                _shutdownTokenSource?.Dispose();

                _routingLock?.Dispose();

                _workerPool?.Dispose();

                foreach (var queue in _priorityQueues.Values)
                {
                    queue.Dispose();
                }

                _logger.LogInformation("IntelligentRouter disposed");
            }
        }
        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface IIntelligentRouter : IDisposable
    {
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(bool graceful = true, CancellationToken cancellationToken = default);
        Task<RoutingResult> RouteTaskAsync(RoutableTask task, RoutingContext context = null);
        Task<BatchRoutingResult> RouteBatchAsync(IEnumerable<RoutableTask> tasks, BatchRoutingOptions options = null);
        Task<RoutingPlan> GetRoutingPlanAsync(RoutableTask task, RoutingContext context = null);
        Task<ReRoutingResult> ReRouteAsync(Guid taskId, ReRoutingReason reason, ReRoutingOptions options = null);
        RouterStatistics GetStatistics();
        Task<OptimizationResult> OptimizeAsync(OptimizationScope scope = OptimizationScope.All);
        Task<RoutingPrediction> PredictRoutingNeedsAsync(TimeSpan predictionWindow, PredictionOptions options = null);
    }

    public class RoutingConfig;
    {
        public static RoutingConfig Default => new RoutingConfig;
        {
            InitialWorkers = 4,
            MaxWorkers = 16,
            WorkerPoolSize = 8,
            QueueCapacity = 1000,
            DefaultRoutingStrategy = RoutingStrategy.Intelligent,
            LoadBalancingStrategy = LoadBalancingStrategy.Adaptive,
            EnablePatternRecognition = true,
            EnableMLAnalysis = true,
            EnableAdaptiveLearning = true,
            EnableLoadBalancing = true,
            EnableContinuousLearning = true,
            EnableAutoOptimization = true,
            QueueMonitoringInterval = TimeSpan.FromSeconds(5),
            MetricsCollectionInterval = TimeSpan.FromSeconds(10),
            LearningInterval = TimeSpan.FromMinutes(1),
            FaultDetectionInterval = TimeSpan.FromSeconds(30),
            QueueProcessingThreshold = 0.8,
            QueueAlertThreshold = 0.9,
            WorkerAlertThreshold = 0.85,
            ErrorAlertThreshold = 0.05,
            ShutdownBatchSize = 10,
            HistoryRetention = TimeSpan.FromDays(7),
            FaultToleranceConfig = FaultToleranceConfig.Default,
            OptimizationThresholds = OptimizationThresholds.Default;
        };

        public int InitialWorkers { get; set; }
        public int MaxWorkers { get; set; }
        public int WorkerPoolSize { get; set; }
        public int QueueCapacity { get; set; }
        public RoutingStrategy DefaultRoutingStrategy { get; set; }
        public LoadBalancingStrategy LoadBalancingStrategy { get; set; }
        public bool EnablePatternRecognition { get; set; }
        public bool EnableMLAnalysis { get; set; }
        public bool EnableAdaptiveLearning { get; set; }
        public bool EnableLoadBalancing { get; set; }
        public bool EnableContinuousLearning { get; set; }
        public bool EnableAutoOptimization { get; set; }
        public TimeSpan QueueMonitoringInterval { get; set; }
        public TimeSpan MetricsCollectionInterval { get; set; }
        public TimeSpan LearningInterval { get; set; }
        public TimeSpan FaultDetectionInterval { get; set; }
        public double QueueProcessingThreshold { get; set; }
        public double QueueAlertThreshold { get; set; }
        public double WorkerAlertThreshold { get; set; }
        public double ErrorAlertThreshold { get; set; }
        public int ShutdownBatchSize { get; set; }
        public TimeSpan HistoryRetention { get; set; }
        public FaultToleranceConfig FaultToleranceConfig { get; set; }
        public OptimizationThresholds OptimizationThresholds { get; set; }
    }

    public class RoutableTask;
    {
        public Guid Id { get; set; }
        public string TaskType { get; set; }
        public object Payload { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public string[] Dependencies { get; set; }
        public TaskConstraint[] Constraints { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime? Deadline { get; set; }
        public int RetryCount { get; set; }
        public string Source { get; set; }

        public RoutableTask()
        {
            Id = Guid.NewGuid();
            CreatedTime = DateTime.UtcNow;
            Metadata = new Dictionary<string, object>();
        }
    }

    public class RoutingContext;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string SourceSystem { get; set; }
        public Dictionary<string, object> ContextData { get; set; }
        public RoutingPriority? OverridePriority { get; set; }
        public TimeSpan? Timeout { get; set; }
        public bool AllowReRouting { get; set; } = true;
        public bool TrackHistory { get; set; } = true;

        public RoutingContext()
        {
            ContextData = new Dictionary<string, object>();
        }
    }

    public class RoutingResult;
    {
        public Guid TaskId { get; set; }
        public bool Success { get; set; }
        public RoutingMethod RoutingMethod { get; set; }
        public string WorkerId { get; set; }
        public RoutingPriority? QueuePriority { get; set; }
        public DateTime RoutingTime { get; set; }
        public TimeSpan EstimatedWaitTime { get; set; }
        public string ErrorMessage { get; set; }
        public FaultRecoveryResult RecoveryResult { get; set; }

        public static RoutingResult Success(
            Guid taskId,
            RoutingMethod method,
            string workerId = null,
            RoutingPriority? queuePriority = null)
        {
            return new RoutingResult;
            {
                TaskId = taskId,
                Success = true,
                RoutingMethod = method,
                WorkerId = workerId,
                QueuePriority = queuePriority,
                RoutingTime = DateTime.UtcNow;
            };
        }

        public static RoutingResult Failure(Guid taskId, string errorMessage, FaultRecoveryResult recovery = null)
        {
            return new RoutingResult;
            {
                TaskId = taskId,
                Success = false,
                ErrorMessage = errorMessage,
                RecoveryResult = recovery,
                RoutingTime = DateTime.UtcNow;
            };
        }
    }

    public enum RoutingPriority;
    {
        Critical = 0,
        High = 1,
        Medium = 2,
        Low = 3,
        Background = 4;
    }

    public enum RoutingMethod;
    {
        Queue,
        WorkerDirect,
        Broadcast,
        Multicast;
    }

    public enum RoutingStrategy;
    {
        RoundRobin,
        LeastLoaded,
        FastestWorker,
        Intelligent,
        Predictive,
        ContextAware;
    }

    public enum LoadBalancingStrategy;
    {
        RoundRobin,
        Weighted,
        LeastConnections,
        Adaptive,
        Predictive;
    }

    public enum RouterStatus;
    {
        Initialized,
        Running,
        Stopping,
        StoppingGracefully,
        Stopped,
        Faulted;
    }

    [Flags]
    public enum OptimizationScope;
    {
        None = 0,
        WorkerPool = 1,
        Queues = 2,
        RoutingStrategies = 4,
        LoadBalancing = 8,
        Minimal = WorkerPool | Queues,
        All = WorkerPool | Queues | RoutingStrategies | LoadBalancing;
    }

    public enum ReRoutingReason;
    {
        WorkerFailure,
        Timeout,
        ResourceConstraint,
        PriorityChange,
        Optimization,
        Manual,
        SystemLoad;
    }

    [Serializable]
    public class RoutingException : Exception
    {
        public RoutingException() { }
        public RoutingException(string message) : base(message) { }
        public RoutingException(string message, Exception inner) : base(message, inner) { }
        protected RoutingException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    #endregion;
}
