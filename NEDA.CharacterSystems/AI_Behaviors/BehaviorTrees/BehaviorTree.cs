using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Monitoring.Diagnostics;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees.BehaviorTree;

namespace NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees;
{
    /// <summary>
    /// Main Behavior Tree implementation for AI decision making and task execution;
    /// </summary>
    public interface IBehaviorTree;
    {
        Task<BehaviorStatus> ExecuteAsync(BehaviorContext context, CancellationToken cancellationToken = default);
        Task InitializeAsync(BehaviorTreeConfig config);
        Task ResetAsync();
        Task<BehaviorTreeState> GetCurrentStateAsync();
        Task<bool> ValidateAsync();
        Task<BehaviorDebugInfo> GetDebugInfoAsync();
    }

    /// <summary>
    /// Behavior Tree Node Base Class;
    /// </summary>
    public abstract class BehaviorNode;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public NodeType Type { get; set; }
        public int Priority { get; set; }
        public BehaviorCondition Precondition { get; set; }
        public List<BehaviorNode> Children { get; set; } = new List<BehaviorNode>();
        public BehaviorNode Parent { get; set; }

        public BehaviorNode()
        {
            Id = Guid.NewGuid().ToString();
        }

        public abstract Task<BehaviorStatus> ExecuteAsync(BehaviorContext context, CancellationToken cancellationToken);
        public abstract Task<bool> ValidateNodeAsync();

        public enum NodeType;
        {
            Composite,
            Decorator,
            Action,
            Condition;
        }
    }

    /// <summary>
    /// Main Behavior Tree Implementation;
    /// </summary>
    public class BehaviorTree : IBehaviorTree;
    {
        private readonly ILogger<BehaviorTree> _logger;
        private readonly IBlackboardSystem _blackboard;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IEventBus _eventBus;
        private readonly INodeFactory _nodeFactory;

        private BehaviorNode _rootNode;
        private BehaviorTreeConfig _config;
        private BehaviorTreeState _currentState;
        private readonly BehaviorExecutionHistory _executionHistory;
        private readonly PerformanceMetrics _performanceMetrics;
        private readonly CancellationTokenSource _treeCts;
        private readonly object _lockObject = new object();

        // Node cache for quick lookup;
        private readonly Dictionary<string, BehaviorNode> _nodeCache;
        private readonly Dictionary<string, NodeExecutionStats> _nodeStats;

        /// <summary>
        /// Behavior Tree constructor with dependency injection;
        /// </summary>
        public BehaviorTree(
            ILogger<BehaviorTree> logger,
            IBlackboardSystem blackboard,
            IDiagnosticTool diagnosticTool,
            IEventBus eventBus,
            INodeFactory nodeFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));

            _currentState = new BehaviorTreeState();
            _executionHistory = new BehaviorExecutionHistory();
            _performanceMetrics = new PerformanceMetrics();
            _treeCts = new CancellationTokenSource();

            _nodeCache = new Dictionary<string, BehaviorNode>();
            _nodeStats = new Dictionary<string, NodeExecutionStats>();

            _logger.LogInformation("Behavior Tree initialized with ID: {TreeId}", _currentState.TreeId);
        }

        /// <summary>
        /// Execute the behavior tree with given context;
        /// </summary>
        public async Task<BehaviorStatus> ExecuteAsync(BehaviorContext context, CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (_rootNode == null)
                throw new InvalidOperationException("Behavior Tree not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("BehaviorTreeExecution");
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _treeCts.Token);

            try
            {
                lock (_lockObject)
                {
                    _currentState.Status = TreeExecutionStatus.Running;
                    _currentState.LastExecutionTime = DateTime.UtcNow;
                    _currentState.ExecutionCount++;
                }

                _logger.LogDebug("Starting Behavior Tree execution for agent: {AgentId}", context.AgentId);

                // Pre-execution validation;
                if (!await PreExecutionValidationAsync(context))
                {
                    _logger.LogWarning("Pre-execution validation failed for agent: {AgentId}", context.AgentId);
                    return BehaviorStatus.Failure;
                }

                // Execute the tree starting from root node;
                var startTime = DateTime.UtcNow;
                var executionResult = await ExecuteNodeAsync(_rootNode, context, linkedCts.Token);
                var endTime = DateTime.UtcNow;

                // Update execution metrics;
                lock (_lockObject)
                {
                    _performanceMetrics.LastExecutionTime = endTime - startTime;
                    _performanceMetrics.TotalExecutionTime += _performanceMetrics.LastExecutionTime;

                    _currentState.Status = executionResult == BehaviorStatus.Running ?
                        TreeExecutionStatus.Running : TreeExecutionStatus.Idle;
                    _currentState.LastResult = executionResult;

                    _executionHistory.AddEntry(new BehaviorExecutionEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Result = executionResult,
                        ContextSnapshot = context.CreateSnapshot(),
                        ExecutionTime = _performanceMetrics.LastExecutionTime;
                    });
                }

                // Publish execution event;
                await _eventBus.PublishAsync(new BehaviorTreeExecutedEvent;
                {
                    TreeId = _currentState.TreeId,
                    AgentId = context.AgentId,
                    Result = executionResult,
                    ExecutionTime = _performanceMetrics.LastExecutionTime,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug("Behavior Tree execution completed with result: {Result} for agent: {AgentId}",
                    executionResult, context.AgentId);

                return executionResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Behavior Tree execution cancelled for agent: {AgentId}", context.AgentId);
                return BehaviorStatus.Failure;
            }
            catch (BehaviorTreeException ex)
            {
                _logger.LogError(ex, "Behavior Tree execution error for agent: {AgentId}", context.AgentId);

                await _eventBus.PublishAsync(new BehaviorTreeErrorEvent;
                {
                    TreeId = _currentState.TreeId,
                    AgentId = context.AgentId,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });

                return BehaviorStatus.Failure;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unexpected error during Behavior Tree execution for agent: {AgentId}",
                    context.AgentId);

                throw new BehaviorTreeException(
                    $"Unexpected error during Behavior Tree execution: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Initialize the behavior tree with configuration;
        /// </summary>
        public async Task InitializeAsync(BehaviorTreeConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            try
            {
                _logger.LogInformation("Initializing Behavior Tree with config: {ConfigName}", config.Name);

                _config = config;
                _currentState.TreeId = config.Id;
                _currentState.Name = config.Name;
                _currentState.Version = config.Version;

                // Build tree from configuration;
                await BuildTreeAsync(config);

                // Validate the tree structure;
                var isValid = await ValidateAsync();
                if (!isValid)
                {
                    throw new BehaviorTreeException("Behavior Tree validation failed after initialization");
                }

                // Initialize blackboard if provided;
                if (config.BlackboardConfig != null)
                {
                    await _blackboard.InitializeAsync(config.BlackboardConfig);
                }

                _currentState.Status = TreeExecutionStatus.Ready;

                await _eventBus.PublishAsync(new BehaviorTreeInitializedEvent;
                {
                    TreeId = _currentState.TreeId,
                    Name = config.Name,
                    Version = config.Version,
                    NodeCount = _nodeCache.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Behavior Tree initialized successfully with {NodeCount} nodes",
                    _nodeCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Behavior Tree");
                throw new BehaviorTreeException("Behavior Tree initialization failed", ex);
            }
        }

        /// <summary>
        /// Reset the behavior tree to initial state;
        /// </summary>
        public async Task ResetAsync()
        {
            try
            {
                _logger.LogDebug("Resetting Behavior Tree: {TreeId}", _currentState.TreeId);

                // Cancel any ongoing execution;
                _treeCts.Cancel();

                // Reset all nodes;
                foreach (var node in _nodeCache.Values)
                {
                    if (node is IResettable resettableNode)
                    {
                        await resettableNode.ResetAsync();
                    }
                }

                // Clear execution history and stats;
                lock (_lockObject)
                {
                    _executionHistory.Clear();
                    _nodeStats.Clear();
                    _performanceMetrics.Reset();

                    _currentState.Status = TreeExecutionStatus.Ready;
                    _currentState.LastResult = BehaviorStatus.Invalid;
                    _currentState.LastExecutionTime = null;
                }

                // Reset blackboard;
                await _blackboard.ResetAsync();

                await _eventBus.PublishAsync(new BehaviorTreeResetEvent;
                {
                    TreeId = _currentState.TreeId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Behavior Tree reset successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting Behavior Tree");
                throw new BehaviorTreeException("Failed to reset Behavior Tree", ex);
            }
        }

        /// <summary>
        /// Get current state of the behavior tree;
        /// </summary>
        public Task<BehaviorTreeState> GetCurrentStateAsync()
        {
            lock (_lockObject)
            {
                return Task.FromResult(_currentState.Clone());
            }
        }

        /// <summary>
        /// Validate the behavior tree structure and configuration;
        /// </summary>
        public async Task<bool> ValidateAsync()
        {
            try
            {
                _logger.LogDebug("Validating Behavior Tree: {TreeId}", _currentState.TreeId);

                if (_rootNode == null)
                {
                    _logger.LogWarning("Behavior Tree validation failed: Root node is null");
                    return false;
                }

                // Validate tree structure;
                var validationResult = await ValidateNodeRecursiveAsync(_rootNode);

                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Behavior Tree validation failed: {ErrorMessage}",
                        validationResult.ErrorMessage);
                    return false;
                }

                // Check for circular references;
                var circularCheck = await CheckForCircularReferencesAsync();
                if (circularCheck.HasCircular)
                {
                    _logger.LogWarning("Behavior Tree validation failed: Circular reference detected");
                    return false;
                }

                // Validate node connections;
                var connectionCheck = await ValidateNodeConnectionsAsync();
                if (!connectionCheck.IsValid)
                {
                    _logger.LogWarning("Behavior Tree validation failed: Invalid node connections");
                    return false;
                }

                _logger.LogDebug("Behavior Tree validation passed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Behavior Tree validation");
                return false;
            }
        }

        /// <summary>
        /// Get debug information for the behavior tree;
        /// </summary>
        public async Task<BehaviorDebugInfo> GetDebugInfoAsync()
        {
            var debugInfo = new BehaviorDebugInfo;
            {
                TreeId = _currentState.TreeId,
                TreeName = _currentState.Name,
                TreeVersion = _currentState.Version,
                TreeStatus = _currentState.Status,
                LastResult = _currentState.LastResult,
                ExecutionCount = _currentState.ExecutionCount,
                PerformanceMetrics = _performanceMetrics.Clone(),
                NodeCount = _nodeCache.Count,
                NodeStatistics = GetNodeStatistics(),
                ExecutionHistory = _executionHistory.GetRecentEntries(50),
                BlackboardSnapshot = await _blackboard.GetSnapshotAsync(),
                ValidationStatus = await ValidateAsync()
            };

            return debugInfo;
        }

        #region Private Methods;

        private async Task BuildTreeAsync(BehaviorTreeConfig config)
        {
            _nodeCache.Clear();

            // Create nodes from configuration;
            foreach (var nodeConfig in config.Nodes)
            {
                var node = await _nodeFactory.CreateNodeAsync(nodeConfig);
                _nodeCache[node.Id] = node;
            }

            // Build parent-child relationships;
            foreach (var relationship in config.NodeRelationships)
            {
                if (_nodeCache.TryGetValue(relationship.ParentId, out var parentNode) &&
                    _nodeCache.TryGetValue(relationship.ChildId, out var childNode))
                {
                    parentNode.Children.Add(childNode);
                    childNode.Parent = parentNode;
                }
                else;
                {
                    throw new BehaviorTreeException(
                        $"Invalid node relationship: Parent={relationship.ParentId}, Child={relationship.ChildId}");
                }
            }

            // Set root node;
            if (!_nodeCache.TryGetValue(config.RootNodeId, out _rootNode))
            {
                throw new BehaviorTreeException($"Root node not found: {config.RootNodeId}");
            }

            // Initialize node statistics;
            foreach (var node in _nodeCache.Values)
            {
                _nodeStats[node.Id] = new NodeExecutionStats;
                {
                    NodeId = node.Id,
                    NodeName = node.Name,
                    NodeType = node.Type;
                };
            }
        }

        private async Task<BehaviorStatus> ExecuteNodeAsync(
            BehaviorNode node,
            BehaviorContext context,
            CancellationToken cancellationToken)
        {
            if (node == null)
                return BehaviorStatus.Failure;

            var nodeStats = _nodeStats[node.Id];
            nodeStats.ExecutionCount++;

            try
            {
                // Check precondition;
                if (node.Precondition != null)
                {
                    var preconditionResult = await node.Precondition.EvaluateAsync(context, cancellationToken);
                    if (!preconditionResult)
                    {
                        nodeStats.FailureCount++;
                        return BehaviorStatus.Failure;
                    }
                }

                var startTime = DateTime.UtcNow;

                // Execute the node;
                var result = await node.ExecuteAsync(context, cancellationToken);

                var endTime = DateTime.UtcNow;
                var executionTime = endTime - startTime;

                // Update statistics;
                nodeStats.LastExecutionTime = executionTime;
                nodeStats.TotalExecutionTime += executionTime;

                switch (result)
                {
                    case BehaviorStatus.Success:
                        nodeStats.SuccessCount++;
                        break;
                    case BehaviorStatus.Failure:
                        nodeStats.FailureCount++;
                        break;
                    case BehaviorStatus.Running:
                        nodeStats.RunningCount++;
                        break;
                }

                // Log node execution;
                _logger.LogTrace("Node executed: {NodeName} ({NodeType}) -> {Result} in {ExecutionTime}ms",
                    node.Name, node.Type, result, executionTime.TotalMilliseconds);

                return result;
            }
            catch (OperationCanceledException)
            {
                nodeStats.CancelledCount++;
                throw;
            }
            catch (Exception ex)
            {
                nodeStats.ErrorCount++;
                _logger.LogError(ex, "Error executing node: {NodeName}", node.Name);
                throw new BehaviorTreeException($"Node execution failed: {node.Name}", ex);
            }
        }

        private async Task<bool> PreExecutionValidationAsync(BehaviorContext context)
        {
            // Check if tree is ready;
            if (_currentState.Status != TreeExecutionStatus.Ready &&
                _currentState.Status != TreeExecutionStatus.Idle)
            {
                _logger.LogWarning("Behavior Tree is not in ready state: {Status}", _currentState.Status);
                return false;
            }

            // Validate context;
            if (string.IsNullOrEmpty(context.AgentId))
            {
                _logger.LogWarning("Invalid context: AgentId is required");
                return false;
            }

            // Check blackboard availability;
            if (!await _blackboard.IsAvailableAsync())
            {
                _logger.LogWarning("Blackboard is not available");
                return false;
            }

            return true;
        }

        private async Task<NodeValidationResult> ValidateNodeRecursiveAsync(BehaviorNode node)
        {
            var result = new NodeValidationResult { NodeId = node.Id, NodeName = node.Name };

            try
            {
                // Validate the node itself;
                var nodeValid = await node.ValidateNodeAsync();
                if (!nodeValid)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Node validation failed: {node.Name}";
                    return result;
                }

                // Validate children recursively;
                foreach (var child in node.Children)
                {
                    var childResult = await ValidateNodeRecursiveAsync(child);
                    if (!childResult.IsValid)
                    {
                        result.IsValid = false;
                        result.ChildValidationErrors.Add(childResult);
                    }
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating node: {NodeName}", node.Name);
                result.IsValid = false;
                result.ErrorMessage = $"Validation error: {ex.Message}";
                return result;
            }
        }

        private async Task<CircularReferenceCheck> CheckForCircularReferencesAsync()
        {
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();

            var checkResult = new CircularReferenceCheck();

            async Task<bool> HasCycleAsync(BehaviorNode node)
            {
                if (node == null)
                    return false;

                var nodeId = node.Id;

                if (!visited.Contains(nodeId))
                {
                    visited.Add(nodeId);
                    recursionStack.Add(nodeId);

                    foreach (var child in node.Children)
                    {
                        if (!visited.Contains(child.Id) && await HasCycleAsync(child))
                        {
                            checkResult.CycleNodes.Add(child.Id);
                            return true;
                        }
                        else if (recursionStack.Contains(child.Id))
                        {
                            checkResult.CycleNodes.Add(child.Id);
                            return true;
                        }
                    }
                }

                recursionStack.Remove(nodeId);
                return false;
            }

            checkResult.HasCircular = await HasCycleAsync(_rootNode);
            return checkResult;
        }

        private async Task<ConnectionValidationResult> ValidateNodeConnectionsAsync()
        {
            var result = new ConnectionValidationResult();

            foreach (var node in _nodeCache.Values)
            {
                // Check if node has valid parent (except root)
                if (node != _rootNode && node.Parent == null)
                {
                    result.InvalidConnections.Add(new InvalidConnection;
                    {
                        NodeId = node.Id,
                        Issue = "Node has no parent",
                        Severity = ValidationSeverity.Error;
                    });
                }

                // Validate child connections based on node type;
                var connectionValid = await ValidateNodeConnectionsAsync(node);
                if (!connectionValid.IsValid)
                {
                    result.InvalidConnections.AddRange(connectionValid.Issues);
                }
            }

            result.IsValid = !result.InvalidConnections.Any(i => i.Severity == ValidationSeverity.Error);
            return result;
        }

        private async Task<NodeConnectionValidation> ValidateNodeConnectionsAsync(BehaviorNode node)
        {
            var validation = new NodeConnectionValidation { NodeId = node.Id };

            switch (node.Type)
            {
                case BehaviorNode.NodeType.Action:
                case BehaviorNode.NodeType.Condition:
                    // Action and Condition nodes should not have children;
                    if (node.Children.Any())
                    {
                        validation.Issues.Add(new InvalidConnection;
                        {
                            NodeId = node.Id,
                            Issue = $"{node.Type} nodes should not have children",
                            Severity = ValidationSeverity.Error;
                        });
                    }
                    break;

                case BehaviorNode.NodeType.Composite:
                    // Composite nodes should have at least one child;
                    if (!node.Children.Any())
                    {
                        validation.Issues.Add(new InvalidConnection;
                        {
                            NodeId = node.Id,
                            Issue = "Composite nodes should have at least one child",
                            Severity = ValidationSeverity.Error;
                        });
                    }
                    break;

                case BehaviorNode.NodeType.Decorator:
                    // Decorator nodes should have exactly one child;
                    if (node.Children.Count != 1)
                    {
                        validation.Issues.Add(new InvalidConnection;
                        {
                            NodeId = node.Id,
                            Issue = "Decorator nodes should have exactly one child",
                            Severity = ValidationSeverity.Error;
                        });
                    }
                    break;
            }

            validation.IsValid = !validation.Issues.Any(i => i.Severity == ValidationSeverity.Error);
            return validation;
        }

        private List<NodeStatistics> GetNodeStatistics()
        {
            return _nodeStats.Values.Select(stats => new NodeStatistics;
            {
                NodeId = stats.NodeId,
                NodeName = stats.NodeName,
                NodeType = stats.NodeType,
                ExecutionCount = stats.ExecutionCount,
                SuccessCount = stats.SuccessCount,
                FailureCount = stats.FailureCount,
                RunningCount = stats.RunningCount,
                CancelledCount = stats.CancelledCount,
                ErrorCount = stats.ErrorCount,
                LastExecutionTime = stats.LastExecutionTime,
                AverageExecutionTime = stats.ExecutionCount > 0 ?
                    stats.TotalExecutionTime / stats.ExecutionCount : TimeSpan.Zero;
            }).ToList();
        }

        #endregion;

        #region Supporting Classes and Enums;

        public enum BehaviorStatus;
        {
            Invalid,
            Success,
            Failure,
            Running;
        }

        public enum TreeExecutionStatus;
        {
            Uninitialized,
            Ready,
            Running,
            Paused,
            Idle,
            Error;
        }

        public enum ValidationSeverity;
        {
            Warning,
            Error,
            Critical;
        }

        public class BehaviorTreeState;
        {
            public string TreeId { get; set; } = Guid.NewGuid().ToString();
            public string Name { get; set; }
            public string Version { get; set; } = "1.0.0";
            public TreeExecutionStatus Status { get; set; } = TreeExecutionStatus.Uninitialized;
            public BehaviorStatus LastResult { get; set; } = BehaviorStatus.Invalid;
            public DateTime? LastExecutionTime { get; set; }
            public int ExecutionCount { get; set; }
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

            public BehaviorTreeState Clone()
            {
                return new BehaviorTreeState;
                {
                    TreeId = TreeId,
                    Name = Name,
                    Version = Version,
                    Status = Status,
                    LastResult = LastResult,
                    LastExecutionTime = LastExecutionTime,
                    ExecutionCount = ExecutionCount,
                    CreatedAt = CreatedAt;
                };
            }
        }

        public class BehaviorContext;
        {
            public string AgentId { get; set; }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;
            public string SessionId { get; set; } = Guid.NewGuid().ToString();

            public BehaviorContextSnapshot CreateSnapshot()
            {
                return new BehaviorContextSnapshot;
                {
                    AgentId = AgentId,
                    Parameters = new Dictionary<string, object>(Parameters),
                    Timestamp = Timestamp,
                    SessionId = SessionId;
                };
            }
        }

        public class BehaviorTreeConfig;
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Name { get; set; }
            public string Version { get; set; } = "1.0.0";
            public string Description { get; set; }
            public string RootNodeId { get; set; }
            public List<NodeConfig> Nodes { get; set; } = new List<NodeConfig>();
            public List<NodeRelationship> NodeRelationships { get; set; } = new List<NodeRelationship>();
            public BlackboardConfig BlackboardConfig { get; set; }
            public TreeExecutionSettings ExecutionSettings { get; set; } = new TreeExecutionSettings();
        }

        public class PerformanceMetrics;
        {
            public TimeSpan TotalExecutionTime { get; set; }
            public TimeSpan LastExecutionTime { get; set; }
            public DateTime MetricsStartTime { get; set; } = DateTime.UtcNow;
            public int TotalExecutions { get; set; }
            public double AverageExecutionTimeMs => TotalExecutions > 0 ?
                TotalExecutionTime.TotalMilliseconds / TotalExecutions : 0;

            public void Reset()
            {
                TotalExecutionTime = TimeSpan.Zero;
                LastExecutionTime = TimeSpan.Zero;
                MetricsStartTime = DateTime.UtcNow;
                TotalExecutions = 0;
            }

            public PerformanceMetrics Clone()
            {
                return new PerformanceMetrics;
                {
                    TotalExecutionTime = TotalExecutionTime,
                    LastExecutionTime = LastExecutionTime,
                    MetricsStartTime = MetricsStartTime,
                    TotalExecutions = TotalExecutions;
                };
            }
        }

        public class BehaviorDebugInfo;
        {
            public string TreeId { get; set; }
            public string TreeName { get; set; }
            public string TreeVersion { get; set; }
            public TreeExecutionStatus TreeStatus { get; set; }
            public BehaviorStatus LastResult { get; set; }
            public int ExecutionCount { get; set; }
            public PerformanceMetrics PerformanceMetrics { get; set; }
            public int NodeCount { get; set; }
            public List<NodeStatistics> NodeStatistics { get; set; }
            public List<BehaviorExecutionEntry> ExecutionHistory { get; set; }
            public BlackboardSnapshot BlackboardSnapshot { get; set; }
            public bool ValidationStatus { get; set; }
            public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        }

        public class NodeExecutionStats;
        {
            public string NodeId { get; set; }
            public string NodeName { get; set; }
            public BehaviorNode.NodeType NodeType { get; set; }
            public int ExecutionCount { get; set; }
            public int SuccessCount { get; set; }
            public int FailureCount { get; set; }
            public int RunningCount { get; set; }
            public int CancelledCount { get; set; }
            public int ErrorCount { get; set; }
            public TimeSpan LastExecutionTime { get; set; }
            public TimeSpan TotalExecutionTime { get; set; }
        }

        // Additional supporting classes...
        public class NodeConfig { }
        public class NodeRelationship { }
        public class BlackboardConfig { }
        public class TreeExecutionSettings { }
        public class BehaviorCondition { }
        public class NodeValidationResult { }
        public class CircularReferenceCheck { }
        public class ConnectionValidationResult { }
        public class InvalidConnection { }
        public class NodeConnectionValidation { }
        public class NodeStatistics { }
        public class BehaviorExecutionHistory { }
        public class BehaviorExecutionEntry { }
        public class BlackboardSnapshot { }

        #endregion;

        #region Events;

        public class BehaviorTreeInitializedEvent : IEvent;
        {
            public string TreeId { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
            public int NodeCount { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BehaviorTreeExecutedEvent : IEvent;
        {
            public string TreeId { get; set; }
            public string AgentId { get; set; }
            public BehaviorStatus Result { get; set; }
            public TimeSpan ExecutionTime { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BehaviorTreeErrorEvent : IEvent;
        {
            public string TreeId { get; set; }
            public string AgentId { get; set; }
            public string Error { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BehaviorTreeResetEvent : IEvent;
        {
            public string TreeId { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        #endregion;
    }

    #region Interfaces;

    public interface IBlackboardSystem;
    {
        Task InitializeAsync(BlackboardConfig config);
        Task ResetAsync();
        Task<bool> IsAvailableAsync();
        Task<BlackboardSnapshot> GetSnapshotAsync();
    }

    public interface INodeFactory;
    {
        Task<BehaviorNode> CreateNodeAsync(NodeConfig config);
    }

    public interface IResettable;
    {
        Task ResetAsync();
    }

    #endregion;

    #region Exceptions;

    public class BehaviorTreeException : Exception
    {
        public BehaviorTreeException() { }
        public BehaviorTreeException(string message) : base(message) { }
        public BehaviorTreeException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
