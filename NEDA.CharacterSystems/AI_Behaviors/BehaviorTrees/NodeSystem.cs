// NEDA.CharacterSystems/AI_Behaviors/BehaviorTrees/NodeSystem.cs;
using NEDA.Brain;
using NEDA.CharacterSystems.AI_Behaviors.BlackboardSystems;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees;
{
    /// <summary>
    /// Advanced Behavior Tree Node System for NEDA AI Characters;
    /// Implements hierarchical behavior modeling with state management and execution control;
    /// </summary>
    public sealed class NodeSystem : INodeSystem, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly Blackboard _blackboard;
        private readonly PerformanceMonitor _performanceMonitor;

        private readonly Dictionary<string, BehaviorTree> _behaviorTrees;
        private readonly Dictionary<string, NodeInstance> _activeNodes;
        private readonly PriorityQueue<NodeTask, int> _executionQueue;
        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _executionSemaphore;

        private CancellationTokenSource _executionCts;
        private Task _executionTask;
        private Timer _maintenanceTimer;
        private Timer _debugTimer;

        private bool _isInitialized;
        private bool _isDisposed;
        private bool _isExecuting;

        public NodeSystemConfiguration Configuration { get; private set; }
        public SystemStatus Status { get; private set; }
        public int TotalBehaviorTrees => _behaviorTrees.Count;
        public int ActiveNodes => _activeNodes.Count;
        public int QueuedTasks => _executionQueue.Count;

        public event EventHandler<TreeExecutionStartedEventArgs> TreeExecutionStarted;
        public event EventHandler<TreeExecutionCompletedEventArgs> TreeExecutionCompleted;
        public event EventHandler<NodeExecutionEventArgs> NodeExecutionStarted;
        public event EventHandler<NodeExecutionEventArgs> NodeExecutionCompleted;
        public event EventHandler<BehaviorTreeLoadedEventArgs> BehaviorTreeLoaded;
        public event EventHandler<SystemStateChangedEventArgs> SystemStateChanged;

        #endregion;

        #region Constructor;

        public NodeSystem(
            ILogger logger,
            IExceptionHandler exceptionHandler,
            Blackboard blackboard = null,
            PerformanceMonitor performanceMonitor = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _blackboard = blackboard ?? new Blackboard(logger);
            _performanceMonitor = performanceMonitor ?? new PerformanceMonitor();

            _behaviorTrees = new Dictionary<string, BehaviorTree>(StringComparer.OrdinalIgnoreCase);
            _activeNodes = new Dictionary<string, NodeInstance>();
            _executionQueue = new PriorityQueue<NodeTask, int>();
            _executionSemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);

            Configuration = new NodeSystemConfiguration();
            Status = SystemStatus.Stopped;

            _logger.Info("Node System initialized");
        }

        #endregion;

        #region Public Methods;

        public async Task InitializeAsync(NodeSystemConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                throw new InvalidOperationException("Node System already initialized");

            try
            {
                _logger.Info("Initializing Node System...");
                Status = SystemStatus.Initializing;

                Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
                ValidateConfiguration();

                // Initialize blackboard;
                await _blackboard.InitializeAsync(cancellationToken);

                // Load built-in behavior trees;
                await LoadBuiltInBehaviorTreesAsync(cancellationToken);

                // Setup maintenance timer;
                _maintenanceTimer = new Timer(MaintenanceCallback, null,
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

                // Setup debug timer if enabled;
                if (Configuration.EnableDebugMonitoring)
                {
                    _debugTimer = new Timer(DebugCallback, null,
                        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                }

                _isInitialized = true;
                Status = SystemStatus.Running;

                OnSystemStateChanged(new SystemStateChangedEventArgs;
                {
                    PreviousState = SystemStatus.Initializing,
                    CurrentState = Status,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Node System initialized. Max parallel executions: {Configuration.MaxParallelExecutions}");
            }
            catch (Exception ex)
            {
                Status = SystemStatus.Error;
                _logger.Error($"Failed to initialize Node System: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NodeSystem.Initialize");
                throw new NodeSystemInitializationException("Failed to initialize Node System", ex);
            }
        }

        public async Task<BehaviorTree> LoadBehaviorTreeAsync(
            string treeId,
            BehaviorTreeDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            try
            {
                _logger.Info($"Loading behavior tree: {treeId}");

                // Validate tree definition;
                ValidateBehaviorTreeDefinition(definition);

                // Create behavior tree;
                var behaviorTree = new BehaviorTree(treeId, definition, _logger, _blackboard);

                // Initialize tree;
                await behaviorTree.InitializeAsync(cancellationToken);

                lock (_syncLock)
                {
                    _behaviorTrees[treeId] = behaviorTree;
                }

                // Raise event;
                OnBehaviorTreeLoaded(new BehaviorTreeLoadedEventArgs;
                {
                    TreeId = treeId,
                    TreeName = definition.Name,
                    NodeCount = definition.RootNode?.GetTotalNodeCount() ?? 0,
                    LoadTime = DateTime.UtcNow;
                });

                _logger.Info($"Behavior tree loaded: {treeId} ({definition.Name}) with {definition.RootNode?.GetTotalNodeCount() ?? 0} nodes");

                return behaviorTree;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load behavior tree {treeId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.LoadBehaviorTree.{treeId}");
                throw new BehaviorTreeLoadException($"Failed to load behavior tree {treeId}", ex);
            }
        }

        public async Task<BehaviorTree> LoadBehaviorTreeFromJsonAsync(
            string treeId,
            string jsonContent,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            if (string.IsNullOrWhiteSpace(jsonContent))
                throw new ArgumentException("JSON content cannot be empty", nameof(jsonContent));

            try
            {
                _logger.Debug($"Loading behavior tree from JSON: {treeId}");

                // Deserialize JSON;
                var settings = new JsonSerializerSettings;
                {
                    TypeNameHandling = TypeNameHandling.Auto,
                    Converters = new List<JsonConverter> { new StringEnumConverter() }
                };

                var definition = JsonConvert.DeserializeObject<BehaviorTreeDefinition>(jsonContent, settings);

                if (definition == null)
                    throw new InvalidOperationException("Failed to deserialize behavior tree definition");

                return await LoadBehaviorTreeAsync(treeId, definition, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load behavior tree from JSON {treeId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.LoadBehaviorTreeFromJson.{treeId}");
                throw;
            }
        }

        public async Task<TreeExecutionResult> ExecuteBehaviorTreeAsync(
            string treeId,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            BehaviorTree behaviorTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(treeId, out behaviorTree))
                    throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
            }

            var executionId = GenerateExecutionId(treeId);
            var execution = new TreeExecution(executionId, treeId, context ?? new ExecutionContext());

            try
            {
                _logger.Info($"Executing behavior tree: {treeId} (Execution: {executionId})");

                // Raise tree execution started event;
                OnTreeExecutionStarted(new TreeExecutionStartedEventArgs;
                {
                    ExecutionId = executionId,
                    TreeId = treeId,
                    Context = execution.Context,
                    StartTime = DateTime.UtcNow;
                });

                // Execute behavior tree;
                var result = await behaviorTree.ExecuteAsync(execution.Context, cancellationToken);

                execution.Result = result;
                execution.CompletionTime = DateTime.UtcNow;

                // Raise tree execution completed event;
                OnTreeExecutionCompleted(new TreeExecutionCompletedEventArgs;
                {
                    ExecutionId = executionId,
                    TreeId = treeId,
                    Result = result,
                    Duration = execution.Duration,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Behavior tree execution completed: {treeId} (Status: {result.Status}, Duration: {execution.Duration.TotalMilliseconds:F0}ms)");

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"Behavior tree execution cancelled: {treeId}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Behavior tree execution failed: {treeId} - {ex.Message}", ex);

                var errorResult = new TreeExecutionResult;
                {
                    ExecutionId = executionId,
                    TreeId = treeId,
                    Status = NodeStatus.Failure,
                    Error = ex.Message,
                    CompletedAt = DateTime.UtcNow;
                };

                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.ExecuteBehaviorTree.{executionId}");
                return errorResult;
            }
        }

        public async Task<TreeExecutionResult> ExecuteBehaviorTreeWithBlackboardAsync(
            string treeId,
            Dictionary<string, object> blackboardData,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            // Create context with blackboard data;
            var context = new ExecutionContext();
            if (blackboardData != null)
            {
                foreach (var kvp in blackboardData)
                {
                    context.SetValue(kvp.Key, kvp.Value);
                }
            }

            return await ExecuteBehaviorTreeAsync(treeId, context, cancellationToken);
        }

        public async Task<NodeTaskResult> ExecuteNodeAsync(
            string treeId,
            string nodeId,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Node ID cannot be empty", nameof(nodeId));

            BehaviorTree behaviorTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(treeId, out behaviorTree))
                    throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
            }

            var taskId = GenerateTaskId(treeId, nodeId);
            var nodeTask = new NodeTask(taskId, treeId, nodeId, context ?? new ExecutionContext());

            try
            {
                // Queue the task for execution;
                lock (_syncLock)
                {
                    _executionQueue.Enqueue(nodeTask, nodeTask.Priority);
                }

                _logger.Debug($"Node task queued: {treeId}.{nodeId} (Task: {taskId}, Priority: {nodeTask.Priority})");

                // Wait for execution (with timeout)
                var completionSource = new TaskCompletionSource<NodeTaskResult>();
                nodeTask.CompletionSource = completionSource;

                using var timeoutCts = new CancellationTokenSource(Configuration.NodeExecutionTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var result = await completionSource.Task.WaitAsync(linkedCts.Token);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"Node task cancelled: {treeId}.{nodeId}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Node task failed: {treeId}.{nodeId} - {ex.Message}", ex);

                var errorResult = new NodeTaskResult;
                {
                    TaskId = taskId,
                    TreeId = treeId,
                    NodeId = nodeId,
                    Status = NodeStatus.Failure,
                    Error = ex.Message,
                    CompletedAt = DateTime.UtcNow;
                };

                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.ExecuteNode.{taskId}");
                return errorResult;
            }
        }

        public async Task StartExecutionEngineAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (_isExecuting)
                throw new InvalidOperationException("Execution engine already running");

            try
            {
                _logger.Info("Starting Node System execution engine...");

                _executionCts = new CancellationTokenSource();
                _isExecuting = true;

                // Start execution task;
                _executionTask = Task.Run(() => ExecuteQueuedTasksAsync(_executionCts.Token), _executionCts.Token);

                _logger.Info("Node System execution engine started");
            }
            catch (Exception ex)
            {
                _isExecuting = false;
                _logger.Error($"Failed to start execution engine: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NodeSystem.StartExecutionEngine");
                throw;
            }
        }

        public async Task StopExecutionEngineAsync(CancellationToken cancellationToken = default)
        {
            if (!_isExecuting)
                return;

            try
            {
                _logger.Info("Stopping Node System execution engine...");

                _executionCts?.Cancel();

                if (_executionTask != null)
                {
                    await _executionTask.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }

                _isExecuting = false;

                _logger.Info("Node System execution engine stopped");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to stop execution engine: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NodeSystem.StopExecutionEngine");
                throw;
            }
        }

        public BehaviorTree GetBehaviorTree(string treeId)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            lock (_syncLock)
            {
                if (_behaviorTrees.TryGetValue(treeId, out var behaviorTree))
                {
                    return behaviorTree;
                }
            }

            throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
        }

        public IReadOnlyList<BehaviorTreeInfo> GetBehaviorTreeList()
        {
            ValidateInitialized();

            lock (_syncLock)
            {
                return _behaviorTrees.Values;
                    .Select(t => new BehaviorTreeInfo;
                    {
                        TreeId = t.Id,
                        Name = t.Name,
                        Description = t.Description,
                        NodeCount = t.GetNodeCount(),
                        LastExecution = t.LastExecutionTime,
                        ExecutionCount = t.ExecutionCount;
                    })
                    .ToList();
            }
        }

        public async Task<NodeSnapshot> GetNodeSnapshotAsync(string treeId, string nodeId, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Node ID cannot be empty", nameof(nodeId));

            BehaviorTree behaviorTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(treeId, out behaviorTree))
                    throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
            }

            return await behaviorTree.GetNodeSnapshotAsync(nodeId, cancellationToken);
        }

        public async Task<TreeSnapshot> GetTreeSnapshotAsync(string treeId, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            BehaviorTree behaviorTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(treeId, out behaviorTree))
                    throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
            }

            return await behaviorTree.GetSnapshotAsync(cancellationToken);
        }

        public async Task<SystemDiagnostics> GetSystemDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                var diagnostics = new SystemDiagnostics;
                {
                    SystemStatus = Status,
                    TotalBehaviorTrees = _behaviorTrees.Count,
                    ActiveNodes = _activeNodes.Count,
                    QueuedTasks = _executionQueue.Count,
                    IsExecutionEngineRunning = _isExecuting,
                    MemoryUsage = GC.GetTotalMemory(false),
                    Timestamp = DateTime.UtcNow;
                };

                // Get tree statistics;
                lock (_syncLock)
                {
                    diagnostics.BehaviorTreeStats = _behaviorTrees.Values;
                        .Select(t => new TreeStatistics;
                        {
                            TreeId = t.Id,
                            Name = t.Name,
                            ExecutionCount = t.ExecutionCount,
                            AverageExecutionTime = t.AverageExecutionTime,
                            SuccessRate = t.SuccessRate,
                            LastExecution = t.LastExecutionTime;
                        })
                        .ToList();
                }

                // Get node statistics;
                diagnostics.NodeExecutionStats = await GetNodeExecutionStatisticsAsync(cancellationToken);

                return diagnostics;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get system diagnostics: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "NodeSystem.GetSystemDiagnostics");
                throw;
            }
        }

        public async Task<bool> SaveBehaviorTreeAsync(string treeId, string filePath, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty", nameof(filePath));

            BehaviorTree behaviorTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(treeId, out behaviorTree))
                    throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
            }

            try
            {
                await behaviorTree.SaveToFileAsync(filePath, cancellationToken);
                _logger.Info($"Behavior tree saved: {treeId} to {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save behavior tree {treeId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.SaveBehaviorTree.{treeId}");
                return false;
            }
        }

        public async Task<bool> UnloadBehaviorTreeAsync(string treeId, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            lock (_syncLock)
            {
                if (!_behaviorTrees.ContainsKey(treeId))
                    return false;
            }

            try
            {
                // Stop any active executions for this tree;
                await StopTreeExecutionsAsync(treeId, cancellationToken);

                // Unload the tree;
                lock (_syncLock)
                {
                    if (_behaviorTrees.Remove(treeId, out var behaviorTree))
                    {
                        behaviorTree.Dispose();
                    }
                }

                _logger.Info($"Behavior tree unloaded: {treeId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to unload behavior tree {treeId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.UnloadBehaviorTree.{treeId}");
                return false;
            }
        }

        public async Task<BehaviorTree> CloneBehaviorTreeAsync(
            string sourceTreeId,
            string newTreeId,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sourceTreeId))
                throw new ArgumentException("Source tree ID cannot be empty", nameof(sourceTreeId));

            if (string.IsNullOrWhiteSpace(newTreeId))
                throw new ArgumentException("New tree ID cannot be empty", nameof(newTreeId));

            BehaviorTree sourceTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(sourceTreeId, out sourceTree))
                    throw new BehaviorTreeNotFoundException($"Source behavior tree not found: {sourceTreeId}");

                if (_behaviorTrees.ContainsKey(newTreeId))
                    throw new InvalidOperationException($"Behavior tree already exists: {newTreeId}");
            }

            try
            {
                // Get source tree definition;
                var sourceDefinition = sourceTree.GetDefinition();

                // Create a deep copy of the definition;
                var clonedDefinition = CloneBehaviorTreeDefinition(sourceDefinition);
                clonedDefinition.Name = $"{clonedDefinition.Name} (Clone)";

                // Load the cloned tree;
                return await LoadBehaviorTreeAsync(newTreeId, clonedDefinition, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to clone behavior tree {sourceTreeId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.CloneBehaviorTree.{sourceTreeId}");
                throw;
            }
        }

        public async Task<bool> ValidateBehaviorTreeAsync(string treeId, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            BehaviorTree behaviorTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(treeId, out behaviorTree))
                    throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
            }

            return await behaviorTree.ValidateAsync(cancellationToken);
        }

        public async Task<NodeProfilingResult> ProfileNodeAsync(
            string treeId,
            string nodeId,
            int iterations = 100,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(treeId))
                throw new ArgumentException("Tree ID cannot be empty", nameof(treeId));

            if (string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("Node ID cannot be empty", nameof(nodeId));

            BehaviorTree behaviorTree;
            lock (_syncLock)
            {
                if (!_behaviorTrees.TryGetValue(treeId, out behaviorTree))
                    throw new BehaviorTreeNotFoundException($"Behavior tree not found: {treeId}");
            }

            try
            {
                _logger.Info($"Profiling node: {treeId}.{nodeId} ({iterations} iterations)");

                var results = new List<TimeSpan>();
                var statusResults = new Dictionary<NodeStatus, int>();

                for (int i = 0; i < iterations; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var context = new ExecutionContext();
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    var result = await behaviorTree.ExecuteNodeAsync(nodeId, context, cancellationToken);

                    stopwatch.Stop();
                    results.Add(stopwatch.Elapsed);

                    // Count status occurrences;
                    if (statusResults.ContainsKey(result.Status))
                        statusResults[result.Status]++;
                    else;
                        statusResults[result.Status] = 1;

                    // Small delay between iterations;
                    if (i < iterations - 1)
                        await Task.Delay(10, cancellationToken);
                }

                var profilingResult = new NodeProfilingResult;
                {
                    TreeId = treeId,
                    NodeId = nodeId,
                    Iterations = results.Count,
                    TotalTime = TimeSpan.FromTicks(results.Sum(r => r.Ticks)),
                    AverageTime = TimeSpan.FromTicks((long)results.Average(r => r.Ticks)),
                    MinTime = results.Min(),
                    MaxTime = results.Max(),
                    StatusDistribution = statusResults,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.Info($"Node profiling completed: {treeId}.{nodeId} - Avg: {profilingResult.AverageTime.TotalMilliseconds:F2}ms");

                return profilingResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to profile node {treeId}.{nodeId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.ProfileNode.{treeId}.{nodeId}");
                throw;
            }
        }

        #endregion;

        #region Private Methods;

        private async Task ExecuteQueuedTasksAsync(CancellationToken cancellationToken)
        {
            _logger.Debug("Node task execution engine started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for tasks or cancellation;
                    await Task.Delay(100, cancellationToken);

                    // Process queued tasks;
                    await ProcessQueuedTasksAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in task execution engine: {ex.Message}", ex);
                    await _exceptionHandler.HandleExceptionAsync(ex, "NodeSystem.ExecuteQueuedTasks");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            _logger.Debug("Node task execution engine stopped");
        }

        private async Task ProcessQueuedTasksAsync(CancellationToken cancellationToken)
        {
            var tasksToExecute = new List<NodeTask>();

            // Get tasks from queue (up to max parallel executions)
            lock (_syncLock)
            {
                while (_executionQueue.Count > 0 && tasksToExecute.Count < Configuration.MaxParallelExecutions)
                {
                    if (_executionQueue.TryDequeue(out var task, out _))
                    {
                        tasksToExecute.Add(task);
                    }
                }
            }

            if (tasksToExecute.Count == 0)
                return;

            // Execute tasks in parallel;
            var executionTasks = tasksToExecute.Select(task =>
                ExecuteSingleNodeTaskAsync(task, cancellationToken));

            await Task.WhenAll(executionTasks);
        }

        private async Task ExecuteSingleNodeTaskAsync(NodeTask task, CancellationToken cancellationToken)
        {
            var instanceId = GenerateInstanceId(task.TreeId, task.NodeId);

            try
            {
                // Acquire semaphore for parallel execution control;
                await _executionSemaphore.WaitAsync(cancellationToken);

                lock (_syncLock)
                {
                    _activeNodes[instanceId] = new NodeInstance(instanceId, task);
                }

                // Get behavior tree;
                BehaviorTree behaviorTree;
                lock (_syncLock)
                {
                    if (!_behaviorTrees.TryGetValue(task.TreeId, out behaviorTree))
                        throw new BehaviorTreeNotFoundException($"Behavior tree not found: {task.TreeId}");
                }

                _logger.Debug($"Executing node task: {task.TreeId}.{task.NodeId} (Task: {task.TaskId})");

                // Raise node execution started event;
                OnNodeExecutionStarted(new NodeExecutionEventArgs;
                {
                    TaskId = task.TaskId,
                    TreeId = task.TreeId,
                    NodeId = task.NodeId,
                    InstanceId = instanceId,
                    StartTime = DateTime.UtcNow,
                    Context = task.Context;
                });

                // Execute the node;
                var result = await behaviorTree.ExecuteNodeAsync(task.NodeId, task.Context, cancellationToken);

                // Complete the task;
                task.CompletionSource?.TrySetResult(new NodeTaskResult;
                {
                    TaskId = task.TaskId,
                    TreeId = task.TreeId,
                    NodeId = task.NodeId,
                    Status = result.Status,
                    ResultData = result.ResultData,
                    ExecutionTime = result.ExecutionTime,
                    CompletedAt = DateTime.UtcNow;
                });

                // Raise node execution completed event;
                OnNodeExecutionCompleted(new NodeExecutionEventArgs;
                {
                    TaskId = task.TaskId,
                    TreeId = task.TreeId,
                    NodeId = task.NodeId,
                    InstanceId = instanceId,
                    Status = result.Status,
                    ExecutionTime = result.ExecutionTime,
                    EndTime = DateTime.UtcNow,
                    Context = task.Context;
                });

                _logger.Debug($"Node task completed: {task.TreeId}.{task.NodeId} (Status: {result.Status}, Time: {result.ExecutionTime.TotalMilliseconds:F0}ms)");
            }
            catch (Exception ex)
            {
                _logger.Error($"Node task failed: {task.TreeId}.{task.NodeId} - {ex.Message}", ex);

                // Complete with error;
                task.CompletionSource?.TrySetResult(new NodeTaskResult;
                {
                    TaskId = task.TaskId,
                    TreeId = task.TreeId,
                    NodeId = task.NodeId,
                    Status = NodeStatus.Failure,
                    Error = ex.Message,
                    CompletedAt = DateTime.UtcNow;
                });

                await _exceptionHandler.HandleExceptionAsync(ex, $"NodeSystem.ExecuteSingleNodeTask.{task.TaskId}");
            }
            finally
            {
                lock (_syncLock)
                {
                    _activeNodes.Remove(instanceId);
                }

                _executionSemaphore.Release();
            }
        }

        private async Task LoadBuiltInBehaviorTreesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info("Loading built-in behavior trees...");

                // Basic AI behaviors;
                await LoadBasicBehaviorsAsync(cancellationToken);

                // Combat behaviors;
                await LoadCombatBehaviorsAsync(cancellationToken);

                // Navigation behaviors;
                await LoadNavigationBehaviorsAsync(cancellationToken);

                // Social behaviors;
                await LoadSocialBehaviorsAsync(cancellationToken);

                _logger.Info("Built-in behavior trees loaded");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load some built-in behavior trees: {ex.Message}");
            }
        }

        private async Task LoadBasicBehaviorsAsync(CancellationToken cancellationToken)
        {
            var idleTree = new BehaviorTreeDefinition;
            {
                Name = "Basic Idle Behavior",
                Description = "Basic idle behavior with random actions",
                Version = "1.0.0",
                RootNode = new SelectorNode("idle-root")
                {
                    Children = new List<BehaviorTreeNode>
                    {
                        new ConditionNode("check-bored")
                        {
                            Condition = ctx => (bool)(ctx.GetValue("is_bored") ?? false),
                            Child = new SequenceNode("bored-actions")
                            {
                                Children = new List<BehaviorTreeNode>
                                {
                                    new ActionNode("look-around")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(1000, ct);
                                            ctx.SetValue("last_action", "looked_around");
                                            return NodeStatus.Success;
                                        }
                                    },
                                    new ActionNode("stretch")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(500, ct);
                                            ctx.SetValue("last_action", "stretched");
                                            return NodeStatus.Success;
                                        }
                                    }
                                }
                            }
                        },
                        new ActionNode("stand-idle")
                        {
                            Action = async (ctx, ct) =>
                            {
                                await Task.Delay(2000, ct);
                                ctx.SetValue("boredom_level",
                                    ((int)(ctx.GetValue("boredom_level") ?? 0)) + 1);
                                return NodeStatus.Success;
                            }
                        }
                    }
                }
            };

            await LoadBehaviorTreeAsync("basic-idle", idleTree, cancellationToken);
        }

        private async Task LoadCombatBehaviorsAsync(CancellationToken cancellationToken)
        {
            var combatTree = new BehaviorTreeDefinition;
            {
                Name = "Basic Combat AI",
                Description = "Basic combat behavior for AI enemies",
                Version = "1.0.0",
                RootNode = new SelectorNode("combat-root")
                {
                    Children = new List<BehaviorTreeNode>
                    {
                        new ConditionNode("check-low-health")
                        {
                            Condition = ctx =>
                            {
                                var health = (int)(ctx.GetValue("health") ?? 100);
                                var maxHealth = (int)(ctx.GetValue("max_health") ?? 100);
                                return health < maxHealth * 0.3;
                            },
                            Child = new SequenceNode("retreat-actions")
                            {
                                Children = new List<BehaviorTreeNode>
                                {
                                    new ActionNode("find-cover")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(500, ct);
                                            ctx.SetValue("has_cover", true);
                                            return NodeStatus.Success;
                                        }
                                    },
                                    new ActionNode("use-health-pack")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(1000, ct);
                                            var health = (int)(ctx.GetValue("health") ?? 0);
                                            ctx.SetValue("health", health + 50);
                                            return NodeStatus.Success;
                                        }
                                    }
                                }
                            }
                        },
                        new ConditionNode("check-enemy-in-range")
                        {
                            Condition = ctx => (bool)(ctx.GetValue("enemy_in_range") ?? false),
                            Child = new SelectorNode("attack-options")
                            {
                                Children = new List<BehaviorTreeNode>
                                {
                                    new ActionNode("melee-attack")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(300, ct);
                                            ctx.SetValue("last_attack", "melee");
                                            return NodeStatus.Success;
                                        }
                                    },
                                    new ActionNode("ranged-attack")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(500, ct);
                                            ctx.SetValue("last_attack", "ranged");
                                            return NodeStatus.Success;
                                        }
                                    }
                                }
                            }
                        },
                        new ActionNode("patrol")
                        {
                            Action = async (ctx, ct) =>
                            {
                                await Task.Delay(1000, ct);
                                ctx.SetValue("patrol_progress",
                                    ((int)(ctx.GetValue("patrol_progress") ?? 0)) + 1);
                                return NodeStatus.Success;
                            }
                        }
                    }
                }
            };

            await LoadBehaviorTreeAsync("basic-combat", combatTree, cancellationToken);
        }

        private async Task LoadNavigationBehaviorsAsync(CancellationToken cancellationToken)
        {
            var navigationTree = new BehaviorTreeDefinition;
            {
                Name = "Navigation AI",
                Description = "Pathfinding and navigation behavior",
                Version = "1.0.0",
                RootNode = new SequenceNode("navigation-root")
                {
                    Children = new List<BehaviorTreeNode>
                    {
                        new ConditionNode("has-destination")
                        {
                            Condition = ctx => ctx.GetValue("destination") != null,
                            Child = new SelectorNode("pathfinding-options")
                            {
                                Children = new List<BehaviorTreeNode>
                                {
                                    new ActionNode("calculate-path")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(200, ct);
                                            ctx.SetValue("path_calculated", true);
                                            return NodeStatus.Success;
                                        }
                                    },
                                    new ActionNode("find-alternative-route")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(300, ct);
                                            ctx.SetValue("alternative_route", true);
                                            return NodeStatus.Success;
                                        }
                                    }
                                }
                            }
                        },
                        new ActionNode("move-to-destination")
                        {
                            Action = async (ctx, ct) =>
                            {
                                await Task.Delay(100, ct);
                                ctx.SetValue("position", "updated");
                                return NodeStatus.Success;
                            }
                        }
                    }
                }
            };

            await LoadBehaviorTreeAsync("navigation", navigationTree, cancellationToken);
        }

        private async Task LoadSocialBehaviorsAsync(CancellationToken cancellationToken)
        {
            var socialTree = new BehaviorTreeDefinition;
            {
                Name = "Social Interactions",
                Description = "Social behavior for NPC interactions",
                Version = "1.0.0",
                RootNode = new SelectorNode("social-root")
                {
                    Children = new List<BehaviorTreeNode>
                    {
                        new ConditionNode("player-nearby")
                        {
                            Condition = ctx => (bool)(ctx.GetValue("player_nearby") ?? false),
                            Child = new SequenceNode("interaction-sequence")
                            {
                                Children = new List<BehaviorTreeNode>
                                {
                                    new ActionNode("greet-player")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(500, ct);
                                            ctx.SetValue("greeted", true);
                                            return NodeStatus.Success;
                                        }
                                    },
                                    new DecoratorNode("conversation-decorator", new ActionNode("start-conversation")
                                    {
                                        Action = async (ctx, ct) =>
                                        {
                                            await Task.Delay(1000, ct);
                                            ctx.SetValue("in_conversation", true);
                                            return NodeStatus.Success;
                                        }
                                    })
                                    {
                                        Condition = ctx => (bool)(ctx.GetValue("player_interested") ?? true)
                                    }
                                }
                            }
                        },
                        new ActionNode("socialize-with-npcs")
                        {
                            Action = async (ctx, ct) =>
                            {
                                await Task.Delay(1500, ct);
                                ctx.SetValue("social_meter",
                                    ((int)(ctx.GetValue("social_meter") ?? 0)) + 1);
                                return NodeStatus.Success;
                            }
                        }
                    }
                }
            };

            await LoadBehaviorTreeAsync("social", socialTree, cancellationToken);
        }

        private async Task StopTreeExecutionsAsync(string treeId, CancellationToken cancellationToken)
        {
            // Remove queued tasks for this tree;
            lock (_syncLock)
            {
                var tasksToRemove = new List<NodeTask>();
                var tempQueue = new PriorityQueue<NodeTask, int>();

                while (_executionQueue.Count > 0)
                {
                    if (_executionQueue.TryDequeue(out var task, out var priority))
                    {
                        if (task.TreeId.Equals(treeId, StringComparison.OrdinalIgnoreCase))
                        {
                            tasksToRemove.Add(task);
                        }
                        else;
                        {
                            tempQueue.Enqueue(task, priority);
                        }
                    }
                }

                // Restore remaining tasks;
                while (tempQueue.Count > 0)
                {
                    if (tempQueue.TryDequeue(out var task, out var priority))
                    {
                        _executionQueue.Enqueue(task, priority);
                    }
                }

                // Cancel removed tasks;
                foreach (var task in tasksToRemove)
                {
                    task.CompletionSource?.TrySetCanceled(cancellationToken);
                }
            }

            // Cancel active nodes for this tree;
            var activeNodesToCancel = new List<string>();

            lock (_syncLock)
            {
                foreach (var kvp in _activeNodes)
                {
                    if (kvp.Value.Task.TreeId.Equals(treeId, StringComparison.OrdinalIgnoreCase))
                    {
                        activeNodesToCancel.Add(kvp.Key);
                    }
                }
            }

            foreach (var instanceId in activeNodesToCancel)
            {
                lock (_syncLock)
                {
                    if (_activeNodes.TryGetValue(instanceId, out var instance))
                    {
                        instance.Task.CompletionSource?.TrySetCanceled(cancellationToken);
                    }
                }
            }
        }

        private async Task<List<NodeExecutionStatistics>> GetNodeExecutionStatisticsAsync(CancellationToken cancellationToken)
        {
            var statistics = new List<NodeExecutionStatistics>();

            lock (_syncLock)
            {
                foreach (var tree in _behaviorTrees.Values)
                {
                    var nodeStats = tree.GetNodeExecutionStatistics();
                    statistics.AddRange(nodeStats);
                }
            }

            return statistics;
        }

        private BehaviorTreeDefinition CloneBehaviorTreeDefinition(BehaviorTreeDefinition source)
        {
            // Simple deep clone using JSON serialization;
            var settings = new JsonSerializerSettings;
            {
                TypeNameHandling = TypeNameHandling.All,
                Formatting = Formatting.Indented;
            };

            var json = JsonConvert.SerializeObject(source, settings);
            return JsonConvert.DeserializeObject<BehaviorTreeDefinition>(json, settings);
        }

        private void ValidateConfiguration()
        {
            if (Configuration.MaxParallelExecutions <= 0)
                throw new ConfigurationException("MaxParallelExecutions must be greater than 0");

            if (Configuration.NodeExecutionTimeout <= TimeSpan.Zero)
                throw new ConfigurationException("NodeExecutionTimeout must be greater than zero");

            if (Configuration.MaxBehaviorTreeMemory <= 0)
                throw new ConfigurationException("MaxBehaviorTreeMemory must be greater than 0");
        }

        private void ValidateBehaviorTreeDefinition(BehaviorTreeDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new InvalidOperationException("Behavior tree name cannot be empty");

            if (definition.RootNode == null)
                throw new InvalidOperationException("Behavior tree must have a root node");

            // Validate node structure;
            ValidateNode(definition.RootNode);
        }

        private void ValidateNode(BehaviorTreeNode node)
        {
            if (node == null)
                return;

            if (string.IsNullOrWhiteSpace(node.Id))
                throw new InvalidOperationException("Node ID cannot be empty");

            // Check for circular references;
            var visited = new HashSet<string>();
            CheckForCircularReferences(node, visited);

            // Validate child nodes;
            if (node is CompositeNode composite)
            {
                if (composite.Children == null)
                    throw new InvalidOperationException($"Composite node {node.Id} must have children");

                if (composite.Children.Count == 0)
                    throw new InvalidOperationException($"Composite node {node.Id} must have at least one child");

                foreach (var child in composite.Children)
                {
                    ValidateNode(child);
                }
            }
            else if (node is DecoratorNode decorator)
            {
                if (decorator.Child == null)
                    throw new InvalidOperationException($"Decorator node {node.Id} must have a child");

                ValidateNode(decorator.Child);
            }
        }

        private void CheckForCircularReferences(BehaviorTreeNode node, HashSet<string> visited)
        {
            if (node == null)
                return;

            if (visited.Contains(node.Id))
                throw new InvalidOperationException($"Circular reference detected at node {node.Id}");

            visited.Add(node.Id);

            if (node is CompositeNode composite)
            {
                foreach (var child in composite.Children)
                {
                    CheckForCircularReferences(child, visited);
                }
            }
            else if (node is DecoratorNode decorator)
            {
                CheckForCircularReferences(decorator.Child, visited);
            }

            visited.Remove(node.Id);
        }

        private void MaintenanceCallback(object state)
        {
            if (!_isInitialized || _isDisposed)
                return;

            try
            {
                _logger.Debug("Running Node System maintenance...");

                // Clean up old data;
                CleanupOldData();

                // Check memory usage;
                CheckMemoryUsage();

                // Update statistics;
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                _logger.Error($"Maintenance failed: {ex.Message}", ex);
            }
        }

        private void DebugCallback(object state)
        {
            if (!_isInitialized || _isDisposed || !Configuration.EnableDebugMonitoring)
                return;

            try
            {
                var activeCount = _activeNodes.Count;
                var queueCount = _executionQueue.Count;
                var treeCount = _behaviorTrees.Count;
                var memory = GC.GetTotalMemory(false) / 1024 / 1024;

                _logger.Debug($"Node System Debug: Trees={treeCount}, Active={activeCount}, Queued={queueCount}, Memory={memory}MB");
            }
            catch (Exception ex)
            {
                _logger.Error($"Debug monitoring failed: {ex.Message}", ex);
            }
        }

        private void CleanupOldData()
        {
            try
            {
                // Clean up completed tasks from memory;
                // This would typically involve cleaning up old execution logs, etc.

                _logger.Debug("Node System data cleanup completed");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Data cleanup failed: {ex.Message}");
            }
        }

        private void CheckMemoryUsage()
        {
            var memory = GC.GetTotalMemory(false);
            var limit = Configuration.MaxBehaviorTreeMemory * 1024 * 1024;

            if (memory > limit * 0.8) // 80% of limit;
            {
                _logger.Warn($"Memory usage high: {memory / 1024 / 1024}MB of {Configuration.MaxBehaviorTreeMemory}MB limit");

                // Force garbage collection if needed;
                if (memory > limit * 0.9) // 90% of limit;
                {
                    _logger.Info("Forcing garbage collection due to high memory usage");
                    GC.Collect();
                }
            }
        }

        private void UpdateStatistics()
        {
            // Update system statistics;
            // This could involve writing stats to a database, file, etc.
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Node System not initialized. Call InitializeAsync first.");

            if (Status != SystemStatus.Running)
                throw new InvalidOperationException($"Node System is not running. Current status: {Status}");
        }

        private string GenerateExecutionId(string treeId)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            return $"{treeId}-{timestamp}-{random}";
        }

        private string GenerateTaskId(string treeId, string nodeId)
        {
            var timestamp = DateTime.UtcNow.ToString("HHmmssfff");
            var random = new Random().Next(100, 999);
            return $"{treeId}.{nodeId}.{timestamp}{random}";
        }

        private string GenerateInstanceId(string treeId, string nodeId)
        {
            return $"{treeId}.{nodeId}.{Guid.NewGuid():N}";
        }

        #endregion;

        #region Event Handlers;

        private void OnTreeExecutionStarted(TreeExecutionStartedEventArgs e)
        {
            TreeExecutionStarted?.Invoke(this, e);
        }

        private void OnTreeExecutionCompleted(TreeExecutionCompletedEventArgs e)
        {
            TreeExecutionCompleted?.Invoke(this, e);
        }

        private void OnNodeExecutionStarted(NodeExecutionEventArgs e)
        {
            NodeExecutionStarted?.Invoke(this, e);
        }

        private void OnNodeExecutionCompleted(NodeExecutionEventArgs e)
        {
            NodeExecutionCompleted?.Invoke(this, e);
        }

        private void OnBehaviorTreeLoaded(BehaviorTreeLoadedEventArgs e)
        {
            BehaviorTreeLoaded?.Invoke(this, e);
        }

        private void OnSystemStateChanged(SystemStateChangedEventArgs e)
        {
            SystemStateChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                Status = SystemStatus.Stopping;

                // Stop execution engine;
                StopExecutionEngineAsync().Wait(5000);

                // Dispose timers;
                _maintenanceTimer?.Dispose();
                _debugTimer?.Dispose();
                _executionCts?.Dispose();
                _executionSemaphore?.Dispose();

                // Dispose behavior trees;
                lock (_syncLock)
                {
                    foreach (var tree in _behaviorTrees.Values)
                    {
                        tree.Dispose();
                    }
                    _behaviorTrees.Clear();
                    _activeNodes.Clear();
                    _executionQueue.Clear();
                }

                Status = SystemStatus.Stopped;
                _isDisposed = true;

                _logger.Info("Node System disposed");
            }
        }

        ~NodeSystem()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    public interface INodeSystem;
    {
        Task<BehaviorTree> LoadBehaviorTreeAsync(string treeId, BehaviorTreeDefinition definition, CancellationToken cancellationToken = default);
        Task<BehaviorTree> LoadBehaviorTreeFromJsonAsync(string treeId, string jsonContent, CancellationToken cancellationToken = default);
        Task<TreeExecutionResult> ExecuteBehaviorTreeAsync(string treeId, ExecutionContext context = null, CancellationToken cancellationToken = default);
        Task<TreeExecutionResult> ExecuteBehaviorTreeWithBlackboardAsync(string treeId, Dictionary<string, object> blackboardData, CancellationToken cancellationToken = default);
        Task<NodeTaskResult> ExecuteNodeAsync(string treeId, string nodeId, ExecutionContext context = null, CancellationToken cancellationToken = default);
        Task StartExecutionEngineAsync(CancellationToken cancellationToken = default);
        Task StopExecutionEngineAsync(CancellationToken cancellationToken = default);
        BehaviorTree GetBehaviorTree(string treeId);
        IReadOnlyList<BehaviorTreeInfo> GetBehaviorTreeList();
        Task<NodeSnapshot> GetNodeSnapshotAsync(string treeId, string nodeId, CancellationToken cancellationToken = default);
        Task<TreeSnapshot> GetTreeSnapshotAsync(string treeId, CancellationToken cancellationToken = default);
        Task<SystemDiagnostics> GetSystemDiagnosticsAsync(CancellationToken cancellationToken = default);
        Task<bool> SaveBehaviorTreeAsync(string treeId, string filePath, CancellationToken cancellationToken = default);
        Task<bool> UnloadBehaviorTreeAsync(string treeId, CancellationToken cancellationToken = default);
        Task<BehaviorTree> CloneBehaviorTreeAsync(string sourceTreeId, string newTreeId, CancellationToken cancellationToken = default);
        Task<bool> ValidateBehaviorTreeAsync(string treeId, CancellationToken cancellationToken = default);
        Task<NodeProfilingResult> ProfileNodeAsync(string treeId, string nodeId, int iterations = 100, CancellationToken cancellationToken = default);
    }

    public class NodeSystemConfiguration;
    {
        public int MaxParallelExecutions { get; set; } = 10;
        public TimeSpan NodeExecutionTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxBehaviorTreeMemory { get; set; } = 512; // MB;
        public bool EnableDebugMonitoring { get; set; } = true;
        public bool EnablePerformanceProfiling { get; set; } = true;
        public bool EnableExecutionLogging { get; set; } = true;
        public int MaxExecutionHistory { get; set; } = 1000;
        public string DataDirectory { get; set; } = "Data/BehaviorTrees";
    }

    public enum SystemStatus;
    {
        Stopped,
        Initializing,
        Running,
        Stopping,
        Error;
    }

    public class TreeExecution;
    {
        public string ExecutionId { get; }
        public string TreeId { get; }
        public ExecutionContext Context { get; }
        public TreeExecutionResult Result { get; set; }
        public DateTime StartTime { get; }
        public DateTime? CompletionTime { get; set; }

        public TimeSpan Duration => (CompletionTime ?? DateTime.UtcNow) - StartTime;

        public TreeExecution(string executionId, string treeId, ExecutionContext context)
        {
            ExecutionId = executionId;
            TreeId = treeId;
            Context = context ?? throw new ArgumentNullException(nameof(context));
            StartTime = DateTime.UtcNow;
        }
    }

    public class NodeTask;
    {
        public string TaskId { get; }
        public string TreeId { get; }
        public string NodeId { get; }
        public ExecutionContext Context { get; }
        public int Priority { get; set; } = 5;
        public TaskCompletionSource<NodeTaskResult> CompletionSource { get; set; }
        public DateTime CreatedAt { get; }

        public NodeTask(string taskId, string treeId, string nodeId, ExecutionContext context)
        {
            TaskId = taskId;
            TreeId = treeId;
            NodeId = nodeId;
            Context = context ?? throw new ArgumentNullException(nameof(context));
            CreatedAt = DateTime.UtcNow;
        }
    }

    internal class NodeInstance;
    {
        public string InstanceId { get; }
        public NodeTask Task { get; }
        public DateTime StartTime { get; }

        public NodeInstance(string instanceId, NodeTask task)
        {
            InstanceId = instanceId;
            Task = task ?? throw new ArgumentNullException(nameof(task));
            StartTime = DateTime.UtcNow;
        }
    }

    public class TreeExecutionResult;
    {
        public string ExecutionId { get; set; }
        public string TreeId { get; set; }
        public NodeStatus Status { get; set; }
        public object ResultData { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
        public Dictionary<string, object> BlackboardSnapshot { get; set; } = new Dictionary<string, object>();
        public List<NodeExecutionRecord> NodeExecutionHistory { get; set; } = new List<NodeExecutionRecord>();
        public DateTime CompletedAt { get; set; }
    }

    public class NodeTaskResult;
    {
        public string TaskId { get; set; }
        public string TreeId { get; set; }
        public string NodeId { get; set; }
        public NodeStatus Status { get; set; }
        public object ResultData { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
        public DateTime CompletedAt { get; set; }
    }

    public class BehaviorTreeInfo;
    {
        public string TreeId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int NodeCount { get; set; }
        public DateTime? LastExecution { get; set; }
        public int ExecutionCount { get; set; }
        public double SuccessRate { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
    }

    public class SystemDiagnostics;
    {
        public SystemStatus SystemStatus { get; set; }
        public int TotalBehaviorTrees { get; set; }
        public int ActiveNodes { get; set; }
        public int QueuedTasks { get; set; }
        public bool IsExecutionEngineRunning { get; set; }
        public long MemoryUsage { get; set; }
        public List<TreeStatistics> BehaviorTreeStats { get; set; } = new List<TreeStatistics>();
        public List<NodeExecutionStatistics> NodeExecutionStats { get; set; } = new List<NodeExecutionStatistics>();
        public DateTime Timestamp { get; set; }
    }

    public class TreeStatistics;
    {
        public string TreeId { get; set; }
        public string Name { get; set; }
        public int ExecutionCount { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public double SuccessRate { get; set; }
        public DateTime? LastExecution { get; set; }
    }

    public class NodeExecutionStatistics;
    {
        public string TreeId { get; set; }
        public string NodeId { get; set; }
        public string NodeType { get; set; }
        public int ExecutionCount { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public Dictionary<NodeStatus, int> StatusCount { get; set; } = new Dictionary<NodeStatus, int>();
        public DateTime LastExecution { get; set; }
    }

    public class NodeProfilingResult;
    {
        public string TreeId { get; set; }
        public string NodeId { get; set; }
        public int Iterations { get; set; }
        public TimeSpan TotalTime { get; set; }
        public TimeSpan AverageTime { get; set; }
        public TimeSpan MinTime { get; set; }
        public TimeSpan MaxTime { get; set; }
        public Dictionary<NodeStatus, int> StatusDistribution { get; set; } = new Dictionary<NodeStatus, int>();
        public DateTime Timestamp { get; set; }
    }

    // Event Arguments;
    public class TreeExecutionStartedEventArgs : EventArgs;
    {
        public string ExecutionId { get; set; }
        public string TreeId { get; set; }
        public ExecutionContext Context { get; set; }
        public DateTime StartTime { get; set; }
    }

    public class TreeExecutionCompletedEventArgs : EventArgs;
    {
        public string ExecutionId { get; set; }
        public string TreeId { get; set; }
        public TreeExecutionResult Result { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class NodeExecutionEventArgs : EventArgs;
    {
        public string TaskId { get; set; }
        public string TreeId { get; set; }
        public string NodeId { get; set; }
        public string InstanceId { get; set; }
        public NodeStatus? Status { get; set; }
        public ExecutionContext Context { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    public class BehaviorTreeLoadedEventArgs : EventArgs;
    {
        public string TreeId { get; set; }
        public string TreeName { get; set; }
        public int NodeCount { get; set; }
        public DateTime LoadTime { get; set; }
    }

    public class SystemStateChangedEventArgs : EventArgs;
    {
        public SystemStatus PreviousState { get; set; }
        public SystemStatus CurrentState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Exceptions;
    public class NodeSystemInitializationException : Exception
    {
        public NodeSystemInitializationException(string message) : base(message) { }
        public NodeSystemInitializationException(string message, Exception inner) : base(message, inner) { }
    }

    public class BehaviorTreeNotFoundException : Exception
    {
        public BehaviorTreeNotFoundException(string message) : base(message) { }
    }

    public class BehaviorTreeLoadException : Exception
    {
        public BehaviorTreeLoadException(string message) : base(message) { }
        public BehaviorTreeLoadException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationException : Exception
    {
        public ConfigurationException(string message) : base(message) { }
    }

    #endregion;
}
