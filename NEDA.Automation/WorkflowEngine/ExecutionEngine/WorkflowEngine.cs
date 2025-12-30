using NEDA.Automation.WorkflowEngine.ActivityLibrary;
using NEDA.Automation.WorkflowEngine.ExecutionEngine;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Automation.WorkflowEngine;
{
    /// <summary>
    /// Workflow execution modes;
    /// </summary>
    public enum WorkflowExecutionMode;
    {
        /// <summary>
        /// Execute activities sequentially;
        /// </summary>
        Sequential = 0,

        /// <summary>
        /// Execute activities in parallel when possible;
        /// </summary>
        Parallel = 1,

        /// <summary>
        /// Execute based on conditions and branching;
        /// </summary>
        Conditional = 2,

        /// <summary>
        /// Execute with compensation support (Saga pattern)
        /// </summary>
        Compensating = 3,

        /// <summary>
        /// Execute with human intervention steps;
        /// </summary>
        HumanInLoop = 4,

        /// <summary>
        /// Execute with event-driven triggers;
        /// </summary>
        EventDriven = 5,

        /// <summary>
        /// Execute with retry and error handling;
        /// </summary>
        Resilient = 6,

        /// <summary>
        /// Execute with priority-based scheduling;
        /// </summary>
        PriorityBased = 7;
    }

    /// <summary>
    /// Workflow execution status;
    /// </summary>
    public enum WorkflowStatus;
    {
        /// <summary>
        /// Workflow is created but not started;
        /// </summary>
        Created = 0,

        /// <summary>
        /// Workflow is being initialized;
        /// </summary>
        Initializing = 1,

        /// <summary>
        /// Workflow is ready to execute;
        /// </summary>
        Ready = 2,

        /// <summary>
        /// Workflow is currently executing;
        /// </summary>
        Executing = 3,

        /// <summary>
        /// Workflow execution completed successfully;
        /// </summary>
        Completed = 4,

        /// <summary>
        /// Workflow execution failed;
        /// </summary>
        Failed = 5,

        /// <summary>
        /// Workflow execution was cancelled;
        /// </summary>
        Cancelled = 6,

        /// <summary>
        /// Workflow is paused and can be resumed;
        /// </summary>
        Paused = 7,

        /// <summary>
        /// Workflow is waiting for external event;
        /// </summary>
        Waiting = 8,

        /// <summary>
        /// Workflow execution timed out;
        /// </summary>
        TimedOut = 9,

        /// <summary>
        /// Workflow is being compensated (rolled back)
        /// </summary>
        Compensating = 10,

        /// <summary>
        /// Workflow compensation completed;
        /// </summary>
        Compensated = 11,

        /// <summary>
        /// Workflow is in error state;
        /// </summary>
        Error = 12,

        /// <summary>
        /// Workflow is suspended;
        /// </summary>
        Suspended = 13;
    }

    /// <summary>
    /// Workflow definition;
    /// </summary>
    public class WorkflowDefinition;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; } = "1.0.0";
        public WorkflowExecutionMode ExecutionMode { get; set; } = WorkflowExecutionMode.Sequential;
        public List<ActivityDefinition> Activities { get; } = new();
        public Dictionary<string, object> InputSchema { get; } = new();
        public Dictionary<string, object> OutputSchema { get; } = new();
        public Dictionary<string, object> Configuration { get; } = new();
        public List<string> Tags { get; } = new();
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public DateTime? ModifiedTime { get; set; }
        public string Author { get; set; }

        public ActivityDefinition AddActivity(string name, Type activityType,
            Dictionary<string, object> configuration = null)
        {
            var activityDef = new ActivityDefinition;
            {
                Id = Guid.NewGuid(),
                Name = name,
                ActivityType = activityType,
                Configuration = configuration ?? new Dictionary<string, object>()
            };

            Activities.Add(activityDef);
            ModifiedTime = DateTime.UtcNow;

            return activityDef;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                throw new InvalidOperationException("Workflow name is required");

            if (Activities.Count == 0)
                throw new InvalidOperationException("Workflow must have at least one activity");

            foreach (var activity in Activities)
            {
                if (!typeof(ActivityBase).IsAssignableFrom(activity.ActivityType))
                    throw new InvalidOperationException($"Activity type must inherit from ActivityBase: {activity.ActivityType}");
            }
        }
    }

    /// <summary>
    /// Activity definition within workflow;
    /// </summary>
    public class ActivityDefinition;
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public Type ActivityType { get; set; }
        public Dictionary<string, object> Configuration { get; } = new();
        public List<string> Dependencies { get; } = new(); // Activity names this depends on;
        public Dictionary<string, string> InputMappings { get; } = new(); // Workflow input -> Activity input;
        public Dictionary<string, string> OutputMappings { get; } = new(); // Activity output -> Workflow output;
        public Dictionary<string, object> CustomSettings { get; } = new();
        public int RetryCount { get; set; } = 3;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
        public int Priority { get; set; } = 5;

        public ActivityBase CreateInstance(ILogger logger)
        {
            var instance = (ActivityBase)Activator.CreateInstance(ActivityType, logger);
            instance.Name = Name;

            // Apply configuration;
            foreach (var kvp in Configuration)
            {
                instance.Configuration[kvp.Key] = kvp.Value;
            }

            // Apply custom settings;
            foreach (var kvp in CustomSettings)
            {
                instance.Metadata[kvp.Key] = kvp.Value;
            }

            // Configure retry policy;
            instance.RetryPolicy.MaxRetries = RetryCount;

            // Configure timeout policy;
            instance.TimeoutPolicy.ExecutionTimeout = Timeout;

            return instance;
        }
    }

    /// <summary>
    /// Workflow instance;
    /// </summary>
    public class WorkflowInstance;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid DefinitionId { get; set; }
        public string Name { get; set; }
        public WorkflowStatus Status { get; set; } = WorkflowStatus.Created;
        public Dictionary<string, object> InputData { get; } = new();
        public Dictionary<string, object> OutputData { get; } = new();
        public Dictionary<string, object> ContextData { get; } = new();
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public DateTime? StartedTime { get; set; }
        public DateTime? CompletedTime { get; set; }
        public TimeSpan Duration => (CompletedTime ?? DateTime.UtcNow) - (StartedTime ?? CreatedTime);
        public string StartedBy { get; set; }
        public string CompletedBy { get; set; }
        public Exception Error { get; set; }
        public List<ActivityInstance> ActivityInstances { get; } = new();
        public Dictionary<string, object> Metadata { get; } = new();

        public ActivityInstance CurrentActivity => ActivityInstances.LastOrDefault(a =>
            a.Status == ActivityState.Executing ||
            a.Status == ActivityState.Waiting);

        public bool IsCompleted => Status == WorkflowStatus.Completed ||
                                  Status == WorkflowStatus.Failed ||
                                  Status == WorkflowStatus.Cancelled ||
                                  Status == WorkflowStatus.TimedOut ||
                                  Status == WorkflowStatus.Compensated;

        public void Start(string startedBy = null)
        {
            if (Status != WorkflowStatus.Created && Status != WorkflowStatus.Ready)
                throw new InvalidOperationException($"Cannot start workflow in status: {Status}");

            Status = WorkflowStatus.Executing;
            StartedTime = DateTime.UtcNow;
            StartedBy = startedBy ?? "System";
        }

        public void Complete(bool success, string completedBy = null, Exception error = null)
        {
            Status = success ? WorkflowStatus.Completed : WorkflowStatus.Failed;
            CompletedTime = DateTime.UtcNow;
            CompletedBy = completedBy ?? "System";
            Error = error;
        }

        public void Cancel(string cancelledBy = null)
        {
            Status = WorkflowStatus.Cancelled;
            CompletedTime = DateTime.UtcNow;
            CompletedBy = cancelledBy ?? "System";
        }

        public void AddActivityInstance(ActivityInstance activityInstance)
        {
            ActivityInstances.Add(activityInstance);
        }
    }

    /// <summary>
    /// Activity instance within workflow instance;
    /// </summary>
    public class ActivityInstance;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid WorkflowInstanceId { get; set; }
        public Guid ActivityDefinitionId { get; set; }
        public string Name { get; set; }
        public ActivityState Status { get; set; } = ActivityState.Created;
        public Dictionary<string, object> InputData { get; } = new();
        public Dictionary<string, object> OutputData { get; } = new();
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public DateTime? StartedTime { get; set; }
        public DateTime? CompletedTime { get; set; }
        public TimeSpan Duration => (CompletedTime ?? DateTime.UtcNow) - (StartedTime ?? CreatedTime);
        public Exception Error { get; set; }
        public int RetryCount { get; set; }
        public ActivityResult LastResult { get; set; }
        public Dictionary<string, object> Metadata { get; } = new();

        public bool IsCompleted => Status == ActivityState.Completed ||
                                  Status == ActivityState.Failed ||
                                  Status == ActivityState.Cancelled ||
                                  Status == ActivityState.TimedOut ||
                                  Status == ActivityState.Compensated;

        public void Start()
        {
            if (Status != ActivityState.Created && Status != ActivityState.Scheduled)
                throw new InvalidOperationException($"Cannot start activity in status: {Status}");

            Status = ActivityState.Executing;
            StartedTime = DateTime.UtcNow;
        }

        public void Complete(ActivityResult result)
        {
            Status = result.State;
            CompletedTime = DateTime.UtcNow;
            Error = result.Error;
            LastResult = result;

            // Copy output data;
            foreach (var kvp in result.OutputData)
            {
                OutputData[kvp.Key] = kvp.Value;
            }
        }

        public void Retry(Exception error = null)
        {
            Status = ActivityState.Created;
            StartedTime = null;
            CompletedTime = null;
            RetryCount++;
            Error = error;
        }
    }

    /// <summary>
    /// Workflow execution statistics;
    /// </summary>
    public class WorkflowExecutionStats;
    {
        public int TotalWorkflows { get; set; }
        public int CompletedWorkflows { get; set; }
        public int FailedWorkflows { get; set; }
        public int ActiveWorkflows { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public double SuccessRate => TotalWorkflows > 0 ?
            (double)CompletedWorkflows / TotalWorkflows * 100 : 0;
        public Dictionary<WorkflowStatus, int> WorkflowsByStatus { get; } = new();
        public Dictionary<string, int> ActivitiesByType { get; } = new();
        public Dictionary<string, double> ActivitySuccessRates { get; } = new();
        public int TotalActivitiesExecuted { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }

        public override string ToString()
        {
            return $"Workflows: {TotalWorkflows} total, {ActiveWorkflows} active, " +
                   $"Success: {SuccessRate:F1}%, Avg Time: {AverageExecutionTime.TotalSeconds:F1}s, " +
                   $"Activities: {TotalActivitiesExecuted}";
        }
    }

    /// <summary>
    /// Workflow execution context;
    /// </summary>
    public class WorkflowExecutionContext;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WorkflowInstance WorkflowInstance { get; set; }
        public WorkflowDefinition WorkflowDefinition { get; set; }
        public ExecutionContext ExecutionContext { get; set; }
        public ILogger Logger { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Dictionary<string, object> SharedData { get; } = new();
        public List<ActivityBase> ActivityInstances { get; } = new();
        public DateTime CreatedTime { get; } = DateTime.UtcNow;

        public T GetSharedData<T>(string key, T defaultValue = default)
        {
            if (SharedData.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                    return typedValue;

                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        public void SetSharedData(string key, object value)
        {
            SharedData[key] = value;
        }

        public void Log(string message, LogLevel level = LogLevel.Information)
        {
            Logger?.Log(level, $"[Workflow:{WorkflowInstance.Name}] {message}");
        }
    }

    /// <summary>
    /// Main Workflow Engine for orchestration and execution;
    /// </summary>
    public class WorkflowEngine : IDisposable
    {
        private readonly ILogger _logger;
        private readonly StateManager _stateManager;
        private readonly ConcurrentDictionary<Guid, WorkflowDefinition> _definitions = new();
        private readonly ConcurrentDictionary<Guid, WorkflowInstance> _instances = new();
        private readonly ConcurrentDictionary<Guid, WorkflowExecutionContext> _executionContexts = new();
        private readonly Timer _cleanupTimer;
        private readonly Timer _statisticsTimer;
        private readonly Timer _monitoringTimer;
        private readonly CancellationTokenSource _cts = new();
        private bool _isDisposed;

        // Execution queues;
        private readonly ConcurrentQueue<WorkflowExecutionContext> _executionQueue = new();
        private readonly ConcurrentDictionary<Guid, Task> _executionTasks = new();
        private readonly SemaphoreSlim _executionSemaphore;

        // Statistics;
        private readonly WorkflowExecutionStats _stats = new();
        private readonly object _statsLock = new();
        private DateTime _startTime = DateTime.UtcNow;

        // Configuration;
        private readonly WorkflowEngineConfig _config;

        /// <summary>
        /// Event fired when workflow status changes;
        /// </summary>
        public event EventHandler<WorkflowStatusChangedEventArgs> WorkflowStatusChanged;

        /// <summary>
        /// Event fired when activity completes;
        /// </summary>
        public event EventHandler<WorkflowActivityCompletedEventArgs> ActivityCompleted;

        /// <summary>
        /// Event fired when workflow completes;
        /// </summary>
        public event EventHandler<WorkflowCompletedEventArgs> WorkflowCompleted;

        /// <summary>
        /// Event fired when statistics are updated;
        /// </summary>
        public event EventHandler<WorkflowStatisticsUpdatedEventArgs> StatisticsUpdated;

        /// <summary>
        /// Event fired when error occurs;
        /// </summary>
        public event EventHandler<WorkflowErrorEventArgs> ErrorOccurred;

        public WorkflowEngine(ILogger logger, StateManager stateManager = null,
            WorkflowEngineConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stateManager = stateManager ?? new StateManager(logger);
            _config = config ?? new WorkflowEngineConfig();
            _executionSemaphore = new SemaphoreSlim(_config.MaxConcurrentWorkflows,
                _config.MaxConcurrentWorkflows);

            InitializeTimers();
            StartExecutionWorkers();

            _logger.LogInformation($"WorkflowEngine initialized with config: {_config}");
        }

        /// <summary>
        /// Register a workflow definition;
        /// </summary>
        public Guid RegisterWorkflow(WorkflowDefinition definition)
        {
            definition.Validate();

            if (_definitions.TryAdd(definition.Id, definition))
            {
                _logger.LogInformation($"Workflow registered: {definition.Name} ({definition.Id})");
                return definition.Id;
            }

            throw new InvalidOperationException($"Workflow already registered: {definition.Name}");
        }

        /// <summary>
        /// Create workflow instance from definition;
        /// </summary>
        public WorkflowInstance CreateInstance(Guid definitionId,
            Dictionary<string, object> inputData = null,
            string instanceName = null,
            Dictionary<string, object> metadata = null)
        {
            if (!_definitions.TryGetValue(definitionId, out var definition))
                throw new KeyNotFoundException($"Workflow definition not found: {definitionId}");

            var instance = new WorkflowInstance;
            {
                DefinitionId = definitionId,
                Name = instanceName ?? $"{definition.Name}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Status = WorkflowStatus.Created;
            };

            // Copy input data;
            if (inputData != null)
            {
                foreach (var kvp in inputData)
                    instance.InputData[kvp.Key] = kvp.Value;
            }

            // Copy metadata;
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                    instance.Metadata[kvp.Key] = kvp.Value;
            }

            // Validate input against schema;
            ValidateInputData(definition, instance.InputData);

            _instances[instance.Id] = instance;
            UpdateStatistics();

            _logger.LogInformation($"Workflow instance created: {instance.Name} ({instance.Id})");

            return instance;
        }

        /// <summary>
        /// Execute workflow instance;
        /// </summary>
        public async Task<WorkflowInstance> ExecuteWorkflowAsync(Guid instanceId,
            string startedBy = null,
            CancellationToken cancellationToken = default)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
                throw new KeyNotFoundException($"Workflow instance not found: {instanceId}");

            if (instance.IsCompleted)
                throw new InvalidOperationException($"Cannot execute completed workflow: {instance.Status}");

            // Create execution context;
            var context = await CreateExecutionContextAsync(instance, startedBy, cancellationToken);
            _executionContexts[instance.Id] = context;

            // Enqueue for execution;
            _executionQueue.Enqueue(context);

            _logger.LogInformation($"Workflow execution enqueued: {instance.Name}");

            // Wait for completion if synchronous execution is requested;
            if (_config.ExecutionMode == WorkflowExecutionMode.Synchronous)
            {
                await WaitForWorkflowCompletionAsync(instanceId, cancellationToken);
            }

            return instance;
        }

        /// <summary>
        /// Execute workflow with input data;
        /// </summary>
        public async Task<WorkflowInstance> ExecuteWorkflowAsync(Guid definitionId,
            Dictionary<string, object> inputData = null,
            string instanceName = null,
            string startedBy = null,
            CancellationToken cancellationToken = default)
        {
            var instance = CreateInstance(definitionId, inputData, instanceName);
            return await ExecuteWorkflowAsync(instance.Id, startedBy, cancellationToken);
        }

        /// <summary>
        /// Cancel workflow execution;
        /// </summary>
        public async Task<bool> CancelWorkflowAsync(Guid instanceId,
            string cancelledBy = null,
            CancellationToken cancellationToken = default)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
                return false;

            if (instance.IsCompleted)
                return false;

            // Cancel execution context if exists;
            if (_executionContexts.TryGetValue(instanceId, out var context))
            {
                context.CancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                    context.CancellationToken, cancellationToken).Token;
            }

            // Update instance status;
            instance.Cancel(cancelledBy);

            // Fire status change event;
            OnWorkflowStatusChanged(instance, WorkflowStatus.Cancelled, cancelledBy);

            _logger.LogInformation($"Workflow cancelled: {instance.Name} by {cancelledBy}");

            return true;
        }

        /// <summary>
        /// Pause workflow execution;
        /// </summary>
        public async Task<bool> PauseWorkflowAsync(Guid instanceId,
            string pausedBy = null,
            CancellationToken cancellationToken = default)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
                return false;

            if (instance.Status != WorkflowStatus.Executing)
                return false;

            instance.Status = WorkflowStatus.Paused;

            // Pause execution context;
            if (_executionContexts.TryGetValue(instanceId, out var context))
            {
                // Signal pause through cancellation token;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(100));
                context.CancellationToken = cts.Token;
            }

            OnWorkflowStatusChanged(instance, WorkflowStatus.Paused, pausedBy);

            _logger.LogInformation($"Workflow paused: {instance.Name} by {pausedBy}");

            return true;
        }

        /// <summary>
        /// Resume paused workflow;
        /// </summary>
        public async Task<bool> ResumeWorkflowAsync(Guid instanceId,
            string resumedBy = null,
            CancellationToken cancellationToken = default)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
                return false;

            if (instance.Status != WorkflowStatus.Paused)
                return false;

            instance.Status = WorkflowStatus.Executing;

            // Re-enqueue for execution;
            if (_executionContexts.TryGetValue(instanceId, out var context))
            {
                _executionQueue.Enqueue(context);
            }

            OnWorkflowStatusChanged(instance, WorkflowStatus.Executing, resumedBy);

            _logger.LogInformation($"Workflow resumed: {instance.Name} by {resumedBy}");

            return true;
        }

        /// <summary>
        /// Get workflow instance by ID;
        /// </summary>
        public WorkflowInstance GetWorkflowInstance(Guid instanceId)
        {
            return _instances.TryGetValue(instanceId, out var instance) ? instance : null;
        }

        /// <summary>
        /// Get all workflow instances;
        /// </summary>
        public List<WorkflowInstance> GetWorkflowInstances(WorkflowStatus? status = null,
            Guid? definitionId = null,
            DateTime? fromDate = null,
            DateTime? toDate = null)
        {
            var query = _instances.Values.AsEnumerable();

            if (status.HasValue)
                query = query.Where(i => i.Status == status.Value);

            if (definitionId.HasValue)
                query = query.Where(i => i.DefinitionId == definitionId.Value);

            if (fromDate.HasValue)
                query = query.Where(i => i.CreatedTime >= fromDate.Value);

            if (toDate.HasValue)
                query = query.Where(i => i.CreatedTime <= toDate.Value);

            return query.OrderByDescending(i => i.CreatedTime).ToList();
        }

        /// <summary>
        /// Get workflow execution statistics;
        /// </summary>
        public WorkflowExecutionStats GetStatistics()
        {
            lock (_statsLock)
            {
                _stats.TotalWorkflows = _instances.Count;
                _stats.CompletedWorkflows = _instances.Count(i => i.Status == WorkflowStatus.Completed);
                _stats.FailedWorkflows = _instances.Count(i => i.Status == WorkflowStatus.Failed);
                _stats.ActiveWorkflows = _instances.Count(i =>
                    i.Status == WorkflowStatus.Executing ||
                    i.Status == WorkflowStatus.Paused ||
                    i.Status == WorkflowStatus.Waiting);

                // Calculate average execution time for completed workflows;
                var completedWorkflows = _instances.Values;
                    .Where(i => i.Status == WorkflowStatus.Completed && i.CompletedTime.HasValue)
                    .ToList();

                if (completedWorkflows.Count > 0)
                {
                    _stats.AverageExecutionTime = TimeSpan.FromTicks(
                        (long)completedWorkflows.Average(w => w.Duration.Ticks));
                }

                // Calculate workflows by status;
                _stats.WorkflowsByStatus.Clear();
                foreach (var instance in _instances.Values)
                {
                    if (!_stats.WorkflowsByStatus.ContainsKey(instance.Status))
                        _stats.WorkflowsByStatus[instance.Status] = 0;
                    _stats.WorkflowsByStatus[instance.Status]++;
                }

                // Calculate activity statistics;
                _stats.TotalActivitiesExecuted = _instances.Values;
                    .Sum(i => i.ActivityInstances.Count);

                _stats.ActivitiesByType.Clear();
                _stats.ActivitySuccessRates.Clear();

                foreach (var instance in _instances.Values)
                {
                    foreach (var activity in instance.ActivityInstances)
                    {
                        var activityType = activity.Name;
                        if (!_stats.ActivitiesByType.ContainsKey(activityType))
                            _stats.ActivitiesByType[activityType] = 0;
                        _stats.ActivitiesByType[activityType]++;

                        // Calculate success rate per activity type;
                        if (!_stats.ActivitySuccessRates.ContainsKey(activityType))
                        {
                            var activitiesOfType = _instances.Values;
                                .SelectMany(i => i.ActivityInstances)
                                .Where(a => a.Name == activityType)
                                .ToList();

                            var successful = activitiesOfType.Count(a =>
                                a.Status == ActivityState.Completed ||
                                a.Status == ActivityState.Compensated);

                            _stats.ActivitySuccessRates[activityType] =
                                activitiesOfType.Count > 0 ?
                                (double)successful / activitiesOfType.Count * 100 : 0;
                        }
                    }
                }

                return _stats;
            }
        }

        /// <summary>
        /// Get workflow execution context;
        /// </summary>
        public WorkflowExecutionContext GetExecutionContext(Guid instanceId)
        {
            return _executionContexts.TryGetValue(instanceId, out var context) ? context : null;
        }

        /// <summary>
        /// Cleanup completed workflows;
        /// </summary>
        public async Task<int> CleanupCompletedWorkflowsAsync(TimeSpan olderThan,
            CancellationToken cancellationToken = default)
        {
            var cutoffTime = DateTime.UtcNow - olderThan;
            var completedWorkflows = _instances.Values;
                .Where(i => i.IsCompleted && i.CompletedTime.HasValue &&
                           i.CompletedTime.Value < cutoffTime)
                .Select(i => i.Id)
                .ToList();

            int removedCount = 0;

            foreach (var instanceId in completedWorkflows)
            {
                if (await RemoveWorkflowInstanceAsync(instanceId, cancellationToken))
                    removedCount++;
            }

            if (removedCount > 0)
            {
                _logger.LogInformation($"Cleaned up {removedCount} completed workflows older than {olderThan}");
            }

            return removedCount;
        }

        /// <summary>
        /// Export workflow instance to JSON;
        /// </summary>
        public async Task<string> ExportWorkflowInstanceAsync(Guid instanceId,
            bool includeActivityDetails = true,
            CancellationToken cancellationToken = default)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
                throw new KeyNotFoundException($"Workflow instance not found: {instanceId}");

            if (!_definitions.TryGetValue(instance.DefinitionId, out var definition))
                throw new KeyNotFoundException($"Workflow definition not found: {instance.DefinitionId}");

            var exportData = new;
            {
                Instance = instance,
                Definition = new;
                {
                    definition.Id,
                    definition.Name,
                    definition.Version,
                    definition.ExecutionMode;
                },
                Activities = includeActivityDetails ? instance.ActivityInstances : null,
                ExecutionContext = _executionContexts.TryGetValue(instanceId, out var context) ?
                    new { context.Id, context.CreatedTime } : null;
            };

            return JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });
        }

        /// <summary>
        /// Import workflow instance from JSON;
        /// </summary>
        public async Task<WorkflowInstance> ImportWorkflowInstanceAsync(string json,
            CancellationToken cancellationToken = default)
        {
            var importData = JsonSerializer.Deserialize<WorkflowImportData>(json);
            if (importData == null)
                throw new InvalidOperationException("Failed to parse import JSON");

            // Create or update instance;
            var instance = importData.Instance;
            instance.Id = Guid.NewGuid(); // Generate new ID;

            // Register definition if needed;
            if (!_definitions.ContainsKey(instance.DefinitionId))
            {
                var definition = new WorkflowDefinition;
                {
                    Id = instance.DefinitionId,
                    Name = importData.Definition.Name,
                    Version = importData.Definition.Version,
                    ExecutionMode = importData.Definition.ExecutionMode;
                };
                _definitions[definition.Id] = definition;
            }

            _instances[instance.Id] = instance;

            _logger.LogInformation($"Workflow instance imported: {instance.Name} ({instance.Id})");

            return instance;
        }

        /// <summary>
        /// Wait for workflow completion;
        /// </summary>
        public async Task WaitForWorkflowCompletionAsync(Guid instanceId,
            CancellationToken cancellationToken = default)
        {
            var instance = GetWorkflowInstance(instanceId);
            if (instance == null)
                throw new KeyNotFoundException($"Workflow instance not found: {instanceId}");

            while (!instance.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
                instance = GetWorkflowInstance(instanceId); // Refresh instance;
            }
        }

        /// <summary>
        /// Execute activity within workflow context;
        /// </summary>
        public async Task<ActivityResult> ExecuteActivityAsync(
            WorkflowExecutionContext context,
            ActivityDefinition activityDef,
            Dictionary<string, object> inputData = null,
            CancellationToken cancellationToken = default)
        {
            var activityInstance = new ActivityInstance;
            {
                WorkflowInstanceId = context.WorkflowInstance.Id,
                ActivityDefinitionId = activityDef.Id,
                Name = activityDef.Name;
            };

            context.WorkflowInstance.AddActivityInstance(activityInstance);

            try
            {
                // Create activity instance;
                var activity = activityDef.CreateInstance(_logger);
                context.ActivityInstances.Add(activity);

                // Prepare activity context;
                var activityContext = new ActivityExecutionContext;
                {
                    WorkflowInstanceId = context.WorkflowInstance.Id,
                    Activity = activity,
                    CancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                        context.CancellationToken, cancellationToken).Token,
                    Logger = _logger;
                };

                // Copy input data;
                if (inputData != null)
                {
                    foreach (var kvp in inputData)
                        activityContext.InputData[kvp.Key] = kvp.Value;
                }

                // Copy shared data from workflow context;
                foreach (var kvp in context.SharedData)
                    activityContext.SharedData[kvp.Key] = kvp.Value;

                // Start activity instance;
                activityInstance.Start();

                // Execute activity;
                var result = await activity.ExecuteAsync(activityContext);

                // Complete activity instance;
                activityInstance.Complete(result);

                // Update shared data with activity outputs;
                foreach (var kvp in result.OutputData)
                {
                    context.SetSharedData(kvp.Key, kvp.Value);
                }

                // Fire activity completed event;
                OnActivityCompleted(context.WorkflowInstance, activityInstance, result);

                return result;
            }
            catch (Exception ex)
            {
                activityInstance.Error = ex;
                activityInstance.Status = ActivityState.Failed;

                // Fire error event;
                OnErrorOccurred(context.WorkflowInstance, activityInstance, ex);

                throw;
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

        #region Private Methods;

        private void InitializeTimers()
        {
            // Cleanup timer (every 10 minutes)
            _cleanupTimer = new Timer(async _ =>
            {
                try
                {
                    await CleanupCompletedWorkflowsAsync(
                        TimeSpan.FromHours(_config.CompletedWorkflowRetentionHours),
                        _cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Cleanup timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

            // Statistics timer (every 30 seconds)
            _statisticsTimer = new Timer(_ =>
            {
                try
                {
                    var stats = GetStatistics();
                    OnStatisticsUpdated(stats);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Statistics timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            // Monitoring timer (every 60 seconds)
            _monitoringTimer = new Timer(_ =>
            {
                try
                {
                    MonitorWorkflowHealth();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Monitoring timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        private void StartExecutionWorkers()
        {
            // Start worker tasks for concurrent execution;
            for (int i = 0; i < _config.MaxConcurrentWorkflows; i++)
            {
                var workerTask = Task.Run(() => ExecutionWorkerAsync(i, _cts.Token), _cts.Token);
                _executionTasks[Guid.NewGuid()] = workerTask;
            }

            _logger.LogInformation($"Started {_config.MaxConcurrentWorkflows} execution workers");
        }

        private async Task ExecutionWorkerAsync(int workerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug($"Execution worker {workerId} started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for execution slot;
                    await _executionSemaphore.WaitAsync(cancellationToken);

                    // Get next workflow from queue;
                    if (_executionQueue.TryDequeue(out var context))
                    {
                        try
                        {
                            await ExecuteWorkflowContextAsync(context, cancellationToken);
                        }
                        finally
                        {
                            _executionSemaphore.Release();
                        }
                    }
                    else;
                    {
                        _executionSemaphore.Release();
                        await Task.Delay(100, cancellationToken); // Wait before retry
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Execution worker {workerId} error: {ex.Message}");
                    await Task.Delay(1000, cancellationToken); // Wait before retry
                }
            }

            _logger.LogDebug($"Execution worker {workerId} stopped");
        }

        private async Task ExecuteWorkflowContextAsync(WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            var instance = context.WorkflowInstance;
            var definition = context.WorkflowDefinition;

            try
            {
                // Update instance status;
                instance.Start(context.WorkflowInstance.StartedBy);
                OnWorkflowStatusChanged(instance, WorkflowStatus.Executing, instance.StartedBy);

                context.Log($"Workflow execution started: {instance.Name}");

                // Execute based on execution mode;
                switch (definition.ExecutionMode)
                {
                    case WorkflowExecutionMode.Sequential:
                        await ExecuteSequentialAsync(context, definition, cancellationToken);
                        break;

                    case WorkflowExecutionMode.Parallel:
                        await ExecuteParallelAsync(context, definition, cancellationToken);
                        break;

                    case WorkflowExecutionMode.Conditional:
                        await ExecuteConditionalAsync(context, definition, cancellationToken);
                        break;

                    case WorkflowExecutionMode.Compensating:
                        await ExecuteCompensatingAsync(context, definition, cancellationToken);
                        break;

                    case WorkflowExecutionMode.HumanInLoop:
                        await ExecuteHumanInLoopAsync(context, definition, cancellationToken);
                        break;

                    case WorkflowExecutionMode.EventDriven:
                        await ExecuteEventDrivenAsync(context, definition, cancellationToken);
                        break;

                    case WorkflowExecutionMode.Resilient:
                        await ExecuteResilientAsync(context, definition, cancellationToken);
                        break;

                    case WorkflowExecutionMode.PriorityBased:
                        await ExecutePriorityBasedAsync(context, definition, cancellationToken);
                        break;
                }

                // Complete workflow;
                instance.Complete(true, "WorkflowEngine");
                OnWorkflowStatusChanged(instance, WorkflowStatus.Completed, "WorkflowEngine");
                OnWorkflowCompleted(instance, true, null);

                context.Log($"Workflow execution completed successfully: {instance.Name}, " +
                           $"Duration: {instance.Duration.TotalSeconds:F1}s");
            }
            catch (OperationCanceledException)
            {
                instance.Cancel("System");
                OnWorkflowStatusChanged(instance, WorkflowStatus.Cancelled, "System");
                OnWorkflowCompleted(instance, false, new TaskCanceledException("Workflow execution cancelled"));

                context.Log($"Workflow execution cancelled: {instance.Name}");
            }
            catch (Exception ex)
            {
                instance.Complete(false, "WorkflowEngine", ex);
                OnWorkflowStatusChanged(instance, WorkflowStatus.Failed, "WorkflowEngine");
                OnWorkflowCompleted(instance, false, ex);
                OnErrorOccurred(instance, null, ex);

                context.Log($"Workflow execution failed: {instance.Name}, Error: {ex.Message}", LogLevel.Error);

                // Handle compensation if configured;
                if (definition.ExecutionMode == WorkflowExecutionMode.Compensating)
                {
                    await CompensateWorkflowAsync(context, ex, cancellationToken);
                }
            }
            finally
            {
                // Cleanup execution context;
                _executionContexts.TryRemove(instance.Id, out _);

                // Update statistics;
                UpdateStatistics();
            }
        }

        private async Task ExecuteSequentialAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            foreach (var activityDef in definition.Activities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Check dependencies;
                if (!AreDependenciesMet(activityDef, context))
                {
                    context.Log($"Activity dependencies not met: {activityDef.Name}", LogLevel.Warning);
                    continue;
                }

                // Prepare input data;
                var inputData = PrepareActivityInputs(activityDef, context);

                // Execute activity;
                var result = await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);

                if (!result.Success)
                {
                    throw new InvalidOperationException($"Activity failed: {activityDef.Name}, Error: {result.Error?.Message}");
                }

                // Map outputs to workflow context;
                MapActivityOutputs(activityDef, result, context);
            }
        }

        private async Task ExecuteParallelAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            var tasks = new List<Task<ActivityResult>>();
            var semaphore = new SemaphoreSlim(_config.MaxParallelActivities);

            foreach (var activityDef in definition.Activities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Check dependencies;
                if (!AreDependenciesMet(activityDef, context))
                    continue;

                tasks.Add(ExecuteActivityWithSemaphoreAsync(
                    context, activityDef, semaphore, cancellationToken));
            }

            // Wait for all tasks with timeout;
            var timeoutTask = Task.Delay(_config.ParallelExecutionTimeout, cancellationToken);
            var completedTask = await Task.WhenAny(Task.WhenAll(tasks), timeoutTask);

            if (completedTask == timeoutTask)
                throw new TimeoutException($"Parallel execution timeout after {_config.ParallelExecutionTimeout}");

            // Check for failures;
            var results = await Task.WhenAll(tasks);
            var failedResults = results.Where(r => !r.Success).ToList();

            if (failedResults.Count > 0)
            {
                throw new AggregateException(
                    "One or more parallel activities failed",
                    failedResults.Select(r => r.Error ?? new Exception("Unknown error")));
            }
        }

        private async Task<ActivityResult> ExecuteActivityWithSemaphoreAsync(
            WorkflowExecutionContext context,
            ActivityDefinition activityDef,
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                var inputData = PrepareActivityInputs(activityDef, context);
                return await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task ExecuteConditionalAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            // Conditional execution would evaluate conditions and branch;
            // For now, execute sequentially with condition checking;
            foreach (var activityDef in definition.Activities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Check condition if specified;
                if (activityDef.CustomSettings.TryGetValue("Condition", out var conditionObj) &&
                    conditionObj is string condition)
                {
                    if (!EvaluateCondition(condition, context))
                    {
                        context.Log($"Condition not met, skipping activity: {activityDef.Name}");
                        continue;
                    }
                }

                // Execute activity;
                var inputData = PrepareActivityInputs(activityDef, context);
                var result = await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);

                if (!result.Success)
                {
                    // Check if failure should stop workflow;
                    if (activityDef.CustomSettings.TryGetValue("StopOnFailure", out var stopObj) &&
                        stopObj is bool stopOnFailure && stopOnFailure)
                    {
                        throw new InvalidOperationException($"Activity failed and StopOnFailure is true: {activityDef.Name}");
                    }
                }
            }
        }

        private async Task ExecuteCompensatingAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            var completedActivities = new Stack<ActivityInstance>();

            try
            {
                foreach (var activityDef in definition.Activities)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var inputData = PrepareActivityInputs(activityDef, context);
                    var result = await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);

                    if (result.Success)
                    {
                        // Track successful activities for potential compensation;
                        var activityInstance = context.WorkflowInstance.ActivityInstances.Last();
                        completedActivities.Push(activityInstance);
                    }
                    else;
                    {
                        // Start compensation for completed activities;
                        await CompensateActivitiesAsync(context, completedActivities, cancellationToken);
                        throw new InvalidOperationException($"Activity failed, compensation triggered: {activityDef.Name}");
                    }
                }
            }
            catch
            {
                // Compensate on any exception;
                await CompensateActivitiesAsync(context, completedActivities, cancellationToken);
                throw;
            }
        }

        private async Task ExecuteHumanInLoopAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            foreach (var activityDef in definition.Activities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Check if activity requires human approval;
                if (activityDef.CustomSettings.TryGetValue("RequiresApproval", out var requiresApproval) &&
                    requiresApproval is bool && (bool)requiresApproval)
                {
                    context.Log($"Waiting for human approval for activity: {activityDef.Name}");

                    // Set workflow to waiting state;
                    context.WorkflowInstance.Status = WorkflowStatus.Waiting;
                    OnWorkflowStatusChanged(context.WorkflowInstance, WorkflowStatus.Waiting, "System");

                    // Wait for approval (simulated)
                    await WaitForHumanApprovalAsync(activityDef, context, cancellationToken);

                    // Resume workflow;
                    context.WorkflowInstance.Status = WorkflowStatus.Executing;
                    OnWorkflowStatusChanged(context.WorkflowInstance, WorkflowStatus.Executing, "System");
                }

                // Execute activity;
                var inputData = PrepareActivityInputs(activityDef, context);
                await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);
            }
        }

        private async Task ExecuteEventDrivenAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            // Event-driven execution would wait for external events;
            // For now, execute sequentially with event simulation;
            foreach (var activityDef in definition.Activities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Check if activity waits for event;
                if (activityDef.CustomSettings.TryGetValue("WaitForEvent", out var eventNameObj) &&
                    eventNameObj is string eventName)
                {
                    context.Log($"Waiting for event: {eventName} for activity: {activityDef.Name}");

                    // Simulate event waiting;
                    await WaitForEventAsync(eventName, context, cancellationToken);
                }

                // Execute activity;
                var inputData = PrepareActivityInputs(activityDef, context);
                await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);
            }
        }

        private async Task ExecuteResilientAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            foreach (var activityDef in definition.Activities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                int retryCount = 0;
                bool success = false;
                Exception lastError = null;

                while (retryCount <= activityDef.RetryCount && !success)
                {
                    try
                    {
                        var inputData = PrepareActivityInputs(activityDef, context);
                        var result = await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);
                        success = result.Success;

                        if (!success)
                        {
                            lastError = result.Error;
                            retryCount++;

                            if (retryCount <= activityDef.RetryCount)
                            {
                                context.Log($"Activity failed, retry {retryCount}/{activityDef.RetryCount}: {activityDef.Name}",
                                    LogLevel.Warning);
                                await Task.Delay(CalculateRetryDelay(retryCount), cancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        retryCount++;

                        if (retryCount <= activityDef.RetryCount)
                        {
                            context.Log($"Activity exception, retry {retryCount}/{activityDef.RetryCount}: {activityDef.Name}",
                                LogLevel.Warning);
                            await Task.Delay(CalculateRetryDelay(retryCount), cancellationToken);
                        }
                    }
                }

                if (!success)
                {
                    throw new InvalidOperationException(
                        $"Activity failed after {activityDef.RetryCount} retries: {activityDef.Name}",
                        lastError);
                }
            }
        }

        private async Task ExecutePriorityBasedAsync(WorkflowExecutionContext context,
            WorkflowDefinition definition,
            CancellationToken cancellationToken)
        {
            // Sort activities by priority (descending)
            var sortedActivities = definition.Activities;
                .OrderByDescending(a => a.Priority)
                .ToList();

            foreach (var activityDef in sortedActivities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var inputData = PrepareActivityInputs(activityDef, context);
                await ExecuteActivityAsync(context, activityDef, inputData, cancellationToken);
            }
        }

        private async Task CompensateWorkflowAsync(WorkflowExecutionContext context,
            Exception error,
            CancellationToken cancellationToken)
        {
            context.Log($"Starting workflow compensation due to error: {error.Message}");

            context.WorkflowInstance.Status = WorkflowStatus.Compensating;
            OnWorkflowStatusChanged(context.WorkflowInstance, WorkflowStatus.Compensating, "System");

            try
            {
                // Compensate activities in reverse order;
                var completedActivities = context.WorkflowInstance.ActivityInstances;
                    .Where(a => a.Status == ActivityState.Completed)
                    .OrderByDescending(a => a.CompletedTime)
                    .ToList();

                foreach (var activityInstance in completedActivities)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Find activity definition;
                    var activityDef = context.WorkflowDefinition.Activities;
                        .FirstOrDefault(a => a.Id == activityInstance.ActivityDefinitionId);

                    if (activityDef != null)
                    {
                        var activity = activityDef.CreateInstance(_logger);

                        // Create compensation context;
                        var compensationContext = new ActivityExecutionContext;
                        {
                            WorkflowInstanceId = context.WorkflowInstance.Id,
                            Activity = activity,
                            CancellationToken = cancellationToken,
                            Logger = _logger;
                        };

                        // Execute compensation;
                        await activity.CompensateAsync(activityInstance.LastResult, compensationContext);

                        context.Log($"Activity compensated: {activityDef.Name}");
                    }
                }

                context.WorkflowInstance.Status = WorkflowStatus.Compensated;
                OnWorkflowStatusChanged(context.WorkflowInstance, WorkflowStatus.Compensated, "System");

                context.Log($"Workflow compensation completed successfully");
            }
            catch (Exception compEx)
            {
                context.WorkflowInstance.Status = WorkflowStatus.Error;
                OnWorkflowStatusChanged(context.WorkflowInstance, WorkflowStatus.Error, "System");

                context.Log($"Workflow compensation failed: {compEx.Message}", LogLevel.Error);
                throw new AggregateException("Workflow execution and compensation failed", error, compEx);
            }
        }

        private async Task CompensateActivitiesAsync(WorkflowExecutionContext context,
            Stack<ActivityInstance> completedActivities,
            CancellationToken cancellationToken)
        {
            context.Log($"Starting activity compensation");

            while (completedActivities.Count > 0)
            {
                var activityInstance = completedActivities.Pop();

                // Find activity definition;
                var activityDef = context.WorkflowDefinition.Activities;
                    .FirstOrDefault(a => a.Id == activityInstance.ActivityDefinitionId);

                if (activityDef != null && activityInstance.LastResult != null)
                {
                    var activity = activityDef.CreateInstance(_logger);

                    var compensationContext = new ActivityExecutionContext;
                    {
                        WorkflowInstanceId = context.WorkflowInstance.Id,
                        Activity = activity,
                        CancellationToken = cancellationToken,
                        Logger = _logger;
                    };

                    await activity.CompensateAsync(activityInstance.LastResult, compensationContext);

                    context.Log($"Activity compensated: {activityDef.Name}");
                }
            }
        }

        private async Task<WorkflowExecutionContext> CreateExecutionContextAsync(
            WorkflowInstance instance,
            string startedBy,
            CancellationToken cancellationToken)
        {
            if (!_definitions.TryGetValue(instance.DefinitionId, out var definition))
                throw new KeyNotFoundException($"Workflow definition not found: {instance.DefinitionId}");

            // Create execution context;
            var executionContext = new ExecutionContext(_logger, $"Workflow_{instance.Name}");
            await executionContext.InitializeAsync(cancellationToken);

            // Store workflow data in execution context;
            await executionContext.SetDataAsync("WorkflowInstance", instance,
                expiry: TimeSpan.FromHours(24));
            await executionContext.SetDataAsync("WorkflowDefinition", definition,
                expiry: TimeSpan.FromHours(24));

            // Copy input data to execution context;
            foreach (var kvp in instance.InputData)
            {
                await executionContext.SetDataAsync($"Input_{kvp.Key}", kvp.Value,
                    expiry: TimeSpan.FromHours(24));
            }

            // Create workflow execution context;
            var context = new WorkflowExecutionContext;
            {
                WorkflowInstance = instance,
                WorkflowDefinition = definition,
                ExecutionContext = executionContext,
                Logger = _logger,
                CancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _cts.Token).Token;
            };

            // Copy input data to shared data;
            foreach (var kvp in instance.InputData)
            {
                context.SetSharedData(kvp.Key, kvp.Value);
            }

            instance.StartedBy = startedBy ?? "System";

            return context;
        }

        private bool AreDependenciesMet(ActivityDefinition activityDef, WorkflowExecutionContext context)
        {
            if (activityDef.Dependencies.Count == 0)
                return true;

            foreach (var dependency in activityDef.Dependencies)
            {
                var dependencyInstance = context.WorkflowInstance.ActivityInstances;
                    .LastOrDefault(a => a.Name == dependency);

                if (dependencyInstance == null ||
                    !(dependencyInstance.Status == ActivityState.Completed ||
                      dependencyInstance.Status == ActivityState.Compensated))
                {
                    return false;
                }
            }

            return true;
        }

        private Dictionary<string, object> PrepareActivityInputs(ActivityDefinition activityDef,
            WorkflowExecutionContext context)
        {
            var inputData = new Dictionary<string, object>();

            // Map inputs from workflow context;
            foreach (var mapping in activityDef.InputMappings)
            {
                if (context.SharedData.TryGetValue(mapping.Value, out var value))
                {
                    inputData[mapping.Key] = value;
                }
                else if (context.WorkflowInstance.InputData.TryGetValue(mapping.Value, out value))
                {
                    inputData[mapping.Key] = value;
                }
            }

            // Add custom settings as inputs;
            foreach (var kvp in activityDef.CustomSettings)
            {
                if (!inputData.ContainsKey(kvp.Key))
                    inputData[kvp.Key] = kvp.Value;
            }

            return inputData;
        }

        private void MapActivityOutputs(ActivityDefinition activityDef, ActivityResult result,
            WorkflowExecutionContext context)
        {
            foreach (var mapping in activityDef.OutputMappings)
            {
                if (result.OutputData.TryGetValue(mapping.Key, out var value))
                {
                    context.SetSharedData(mapping.Value, value);
                    context.WorkflowInstance.OutputData[mapping.Value] = value;
                }
            }
        }

        private bool EvaluateCondition(string condition, WorkflowExecutionContext context)
        {
            // Simple condition evaluation;
            // In production, would use expression evaluator;
            try
            {
                // For now, check if condition is a boolean value in shared data;
                if (context.SharedData.TryGetValue(condition, out var value) &&
                    value is bool boolValue)
                {
                    return boolValue;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task WaitForHumanApprovalAsync(ActivityDefinition activityDef,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Simulate waiting for human approval;
            // In production, would integrate with approval system;
            var timeout = activityDef.CustomSettings.TryGetValue("ApprovalTimeout", out var timeoutObj) &&
                         timeoutObj is TimeSpan approvalTimeout ?
                         approvalTimeout : TimeSpan.FromHours(24);

            var approvalTask = Task.Delay(timeout, cancellationToken);

            // Simulate approval after random delay (for testing)
            var randomDelay = TimeSpan.FromSeconds(new Random().Next(1, 5));
            var simulatedApproval = Task.Delay(randomDelay, cancellationToken);

            await Task.WhenAny(approvalTask, simulatedApproval);

            if (approvalTask.IsCompleted)
            {
                throw new TimeoutException($"Human approval timeout for activity: {activityDef.Name}");
            }
        }

        private async Task WaitForEventAsync(string eventName,
            WorkflowExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Simulate event waiting;
            // In production, would use event bus or message queue;
            var timeout = TimeSpan.FromMinutes(5);
            var eventTask = Task.Delay(timeout, cancellationToken);

            // Simulate event after random delay;
            var randomDelay = TimeSpan.FromSeconds(new Random().Next(1, 10));
            var simulatedEvent = Task.Delay(randomDelay, cancellationToken);

            await Task.WhenAny(eventTask, simulatedEvent);

            if (eventTask.IsCompleted)
            {
                throw new TimeoutException($"Event timeout: {eventName}");
            }
        }

        private TimeSpan CalculateRetryDelay(int retryCount)
        {
            // Exponential backoff with jitter;
            var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
            var jitter = new Random().NextDouble() * 0.2 - 0.1; // ±10% jitter;
            return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * (1 + jitter));
        }

        private void ValidateInputData(WorkflowDefinition definition, Dictionary<string, object> inputData)
        {
            // Validate required inputs;
            foreach (var kvp in definition.InputSchema)
            {
                if (kvp.Value is Dictionary<string, object> schema &&
                    schema.TryGetValue("Required", out var requiredObj) &&
                    requiredObj is bool required && required)
                {
                    if (!inputData.ContainsKey(kvp.Key))
                        throw new ArgumentException($"Required input not provided: {kvp.Key}");
                }
            }
        }

        private async Task<bool> RemoveWorkflowInstanceAsync(Guid instanceId,
            CancellationToken cancellationToken)
        {
            // Remove from instances;
            var removed = _instances.TryRemove(instanceId, out var instance);

            if (removed && instance != null)
            {
                // Remove execution context;
                _executionContexts.TryRemove(instanceId, out _);

                // Cancel any execution task;
                if (_executionTasks.Values.Any(t => t.Status == TaskStatus.Running))
                {
                    // In production, would properly cancel the task;
                }

                _logger.LogDebug($"Workflow instance removed: {instance.Name}");
            }

            return removed;
        }

        private void MonitorWorkflowHealth()
        {
            // Check for stuck workflows;
            var stuckWorkflows = _instances.Values;
                .Where(i => i.Status == WorkflowStatus.Executing &&
                           i.StartedTime.HasValue &&
                           DateTime.UtcNow - i.StartedTime.Value > _config.StuckWorkflowTimeout)
                .ToList();

            foreach (var workflow in stuckWorkflows)
            {
                _logger.LogWarning($"Stuck workflow detected: {workflow.Name}, " +
                                 $"Running for: {DateTime.UtcNow - workflow.StartedTime.Value}");

                // Take action based on configuration;
                if (_config.AutoHandleStuckWorkflows)
                {
                    _ = CancelWorkflowAsync(workflow.Id, "HealthMonitor", _cts.Token);
                }
            }

            // Check execution queue size;
            if (_executionQueue.Count > _config.MaxQueueSizeWarningThreshold)
            {
                _logger.LogWarning($"Execution queue size exceeded threshold: {_executionQueue.Count}");
            }
        }

        private void UpdateStatistics()
        {
            // Statistics are updated in GetStatistics() method;
        }

        private void OnWorkflowStatusChanged(WorkflowInstance instance,
            WorkflowStatus newStatus, string changedBy)
        {
            WorkflowStatusChanged?.Invoke(this, new WorkflowStatusChangedEventArgs;
            {
                WorkflowInstanceId = instance.Id,
                WorkflowName = instance.Name,
                OldStatus = instance.Status,
                NewStatus = newStatus,
                ChangedBy = changedBy,
                ChangeTime = DateTime.UtcNow;
            });
        }

        private void OnActivityCompleted(WorkflowInstance instance,
            ActivityInstance activityInstance, ActivityResult result)
        {
            ActivityCompleted?.Invoke(this, new WorkflowActivityCompletedEventArgs;
            {
                WorkflowInstanceId = instance.Id,
                WorkflowName = instance.Name,
                ActivityInstanceId = activityInstance.Id,
                ActivityName = activityInstance.Name,
                Result = result,
                CompletionTime = DateTime.UtcNow;
            });
        }

        private void OnWorkflowCompleted(WorkflowInstance instance, bool success, Exception error)
        {
            WorkflowCompleted?.Invoke(this, new WorkflowCompletedEventArgs;
            {
                WorkflowInstanceId = instance.Id,
                WorkflowName = instance.Name,
                Success = success,
                Error = error,
                Duration = instance.Duration,
                CompletionTime = DateTime.UtcNow;
            });
        }

        private void OnStatisticsUpdated(WorkflowExecutionStats stats)
        {
            StatisticsUpdated?.Invoke(this, new WorkflowStatisticsUpdatedEventArgs;
            {
                Statistics = stats,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnErrorOccurred(WorkflowInstance instance, ActivityInstance activityInstance, Exception error)
        {
            ErrorOccurred?.Invoke(this, new WorkflowErrorEventArgs;
            {
                WorkflowInstanceId = instance?.Id ?? Guid.Empty,
                WorkflowName = instance?.Name,
                ActivityInstanceId = activityInstance?.Id,
                ActivityName = activityInstance?.Name,
                Error = error,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _cts.Cancel();
                    _cleanupTimer?.Dispose();
                    _statisticsTimer?.Dispose();
                    _monitoringTimer?.Dispose();
                    _executionSemaphore?.Dispose();
                    _stateManager?.Dispose();

                    // Wait for execution tasks to complete;
                    Task.WaitAll(_executionTasks.Values.ToArray(), TimeSpan.FromSeconds(30));

                    _logger.LogInformation("WorkflowEngine disposed");
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Nested Classes;

        private class WorkflowImportData;
        {
            public WorkflowInstance Instance { get; set; }
            public WorkflowDefinitionInfo Definition { get; set; }
        }

        private class WorkflowDefinitionInfo;
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public WorkflowExecutionMode ExecutionMode { get; set; }
        }

        #endregion;
    }

    /// <summary>
    /// Workflow engine configuration;
    /// </summary>
    public class WorkflowEngineConfig;
    {
        public int MaxConcurrentWorkflows { get; set; } = 10;
        public int MaxParallelActivities { get; set; } = 4;
        public TimeSpan ParallelExecutionTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan StuckWorkflowTimeout { get; set; } = TimeSpan.FromHours(1);
        public int MaxQueueSizeWarningThreshold { get; set; } = 1000;
        public int CompletedWorkflowRetentionHours { get; set; } = 24;
        public bool AutoHandleStuckWorkflows { get; set; } = true;
        public WorkflowExecutionMode ExecutionMode { get; set; } = WorkflowExecutionMode.Sequential;

        public override string ToString()
        {
            return $"MaxConcurrent: {MaxConcurrentWorkflows}, MaxParallel: {MaxParallelActivities}, " +
                   $"StuckTimeout: {StuckWorkflowTimeout}, ExecutionMode: {ExecutionMode}";
        }
    }

    /// <summary>
    /// Workflow status changed event arguments;
    /// </summary>
    public class WorkflowStatusChangedEventArgs : EventArgs;
    {
        public Guid WorkflowInstanceId { get; set; }
        public string WorkflowName { get; set; }
        public WorkflowStatus OldStatus { get; set; }
        public WorkflowStatus NewStatus { get; set; }
        public string ChangedBy { get; set; }
        public DateTime ChangeTime { get; set; }
    }

    /// <summary>
    /// Workflow activity completed event arguments;
    /// </summary>
    public class WorkflowActivityCompletedEventArgs : EventArgs;
    {
        public Guid WorkflowInstanceId { get; set; }
        public string WorkflowName { get; set; }
        public Guid ActivityInstanceId { get; set; }
        public string ActivityName { get; set; }
        public ActivityResult Result { get; set; }
        public DateTime CompletionTime { get; set; }
    }

    /// <summary>
    /// Workflow completed event arguments;
    /// </summary>
    public class WorkflowCompletedEventArgs : EventArgs;
    {
        public Guid WorkflowInstanceId { get; set; }
        public string WorkflowName { get; set; }
        public bool Success { get; set; }
        public Exception Error { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime CompletionTime { get; set; }
    }

    /// <summary>
    /// Workflow statistics updated event arguments;
    /// </summary>
    public class WorkflowStatisticsUpdatedEventArgs : EventArgs;
    {
        public WorkflowExecutionStats Statistics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Workflow error event arguments;
    /// </summary>
    public class WorkflowErrorEventArgs : EventArgs;
    {
        public Guid WorkflowInstanceId { get; set; }
        public string WorkflowName { get; set; }
        public Guid ActivityInstanceId { get; set; }
        public string ActivityName { get; set; }
        public Exception Error { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
