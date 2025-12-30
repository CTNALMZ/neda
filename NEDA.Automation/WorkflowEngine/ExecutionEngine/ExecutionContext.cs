using NEDA.Automation.WorkflowEngine.ActivityLibrary;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Automation.WorkflowEngine;
{
    /// <summary>
    /// Execution context state for workflow management;
    /// </summary>
    public enum ExecutionContextState;
    {
        /// <summary>
        /// Context is created but not yet initialized;
        /// </summary>
        Created = 0,

        /// <summary>
        /// Context is being initialized;
        /// </summary>
        Initializing = 1,

        /// <summary>
        /// Context is ready for execution;
        /// </summary>
        Ready = 2,

        /// <summary>
        /// Context is currently executing;
        /// </summary>
        Executing = 3,

        /// <summary>
        /// Context execution completed successfully;
        /// </summary>
        Completed = 4,

        /// <summary>
        /// Context execution failed;
        /// </summary>
        Failed = 5,

        /// <summary>
        /// Context execution was cancelled;
        /// </summary>
        Cancelled = 6,

        /// <summary>
        /// Context is paused and can be resumed;
        /// </summary>
        Paused = 7,

        /// <summary>
        /// Context is being compensated (rolled back)
        /// </summary>
        Compensating = 8,

        /// <summary>
        /// Context compensation completed;
        /// </summary>
        Compensated = 9,

        /// <summary>
        /// Context is timed out;
        /// </summary>
        TimedOut = 10,

        /// <summary>
        /// Context is in error state and requires intervention;
        /// </summary>
        Error = 11,

        /// <summary>
        /// Context is being disposed;
        /// </summary>
        Disposing = 12,

        /// <summary>
        /// Context has been disposed;
        /// </summary>
        Disposed = 13;
    }

    /// <summary>
    /// Execution context scope for data isolation;
    /// </summary>
    public enum ContextScope;
    {
        /// <summary>
        /// Global scope - accessible across entire workflow;
        /// </summary>
        Global = 0,

        /// <summary>
        /// Workflow scope - accessible within workflow instance;
        /// </summary>
        Workflow = 1,

        /// <summary>
        /// Activity scope - accessible within activity execution;
        /// </summary>
        Activity = 2,

        /// <summary>
        /// Transaction scope - accessible within transaction boundary;
        /// </summary>
        Transaction = 3,

        /// <summary>
        /// Session scope - accessible within user session;
        /// </summary>
        Session = 4,

        /// <summary>
        /// Request scope - accessible within HTTP request;
        /// </summary>
        Request = 5,

        /// <summary>
        /// Thread scope - accessible within thread boundary;
        /// </summary>
        Thread = 6,

        /// <summary>
        /// Custom scope - user-defined isolation boundary;
        /// </summary>
        Custom = 7;
    }

    /// <summary>
    /// Context data security level;
    /// </summary>
    public enum DataSecurityLevel;
    {
        /// <summary>
        /// Public data - no restrictions;
        /// </summary>
        Public = 0,

        /// <summary>
        /// Internal data - restricted to system;
        /// </summary>
        Internal = 1,

        /// <summary>
        /// Confidential data - requires authorization;
        /// </summary>
        Confidential = 2,

        /// <summary>
        /// Secret data - encrypted storage required;
        /// </summary>
        Secret = 3,

        /// <summary>
        /// Top secret data - highest level of protection;
        /// </summary>
        TopSecret = 4;
    }

    /// <summary>
    /// Context data entry with metadata;
    /// </summary>
    public class ContextDataEntry
    {
        public string Key { get; set; }
        public object Value { get; set; }
        public Type ValueType { get; set; }
        public ContextScope Scope { get; set; }
        public DataSecurityLevel SecurityLevel { get; set; }
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public DateTime ModifiedTime { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiryTime { get; set; }
        public string Owner { get; set; }
        public Dictionary<string, object> Metadata { get; } = new();
        public List<string> Tags { get; } = new();
        public bool IsEncrypted { get; set; }
        public byte[] EncryptionIV { get; set; }

        public bool IsExpired => ExpiryTime.HasValue && DateTime.UtcNow > ExpiryTime.Value;

        public T GetValue<T>(T defaultValue = default)
        {
            if (Value == null) return defaultValue;

            if (Value is T typedValue)
                return typedValue;

            try
            {
                return (T)Convert.ChangeType(Value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        public void UpdateValue(object newValue)
        {
            Value = newValue;
            ModifiedTime = DateTime.UtcNow;
            ValueType = newValue?.GetType();
        }

        public override string ToString()
        {
            return $"{Key}: {ValueType?.Name} [{Scope}]";
        }
    }

    /// <summary>
    /// Execution context statistics;
    /// </summary>
    public class ExecutionContextStats;
    {
        public Guid ContextId { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime? LastActivityTime { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public int TotalActivitiesExecuted { get; set; }
        public int SuccessfulActivities { get; set; }
        public int FailedActivities { get; set; }
        public int DataEntriesCount { get; set; }
        public int MemoryUsageKB { get; set; }
        public Dictionary<ContextScope, int> DataByScope { get; } = new();
        public Dictionary<string, int> ActivityExecutionCount { get; } = new();
        public double SuccessRate => TotalActivitiesExecuted > 0 ?
            (double)SuccessfulActivities / TotalActivitiesExecuted * 100 : 0;

        public override string ToString()
        {
            return $"Context {ContextId}: Activities={TotalActivitiesExecuted}, " +
                   $"Success={SuccessRate:F1}%, Data={DataEntriesCount}, " +
                   $"Time={TotalExecutionTime.TotalSeconds:F1}s";
        }
    }

    /// <summary>
    /// Execution context event arguments;
    /// </summary>
    public class ExecutionContextEventArgs : EventArgs;
    {
        public Guid ContextId { get; set; }
        public string ContextName { get; set; }
        public ExecutionContextState OldState { get; set; }
        public ExecutionContextState NewState { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> EventData { get; } = new();
    }

    /// <summary>
    /// Context data changed event arguments;
    /// </summary>
    public class ContextDataChangedEventArgs : EventArgs;
    {
        public Guid ContextId { get; set; }
        public string Key { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public ContextScope Scope { get; set; }
        public DataChangeType ChangeType { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Data change types;
    /// </summary>
    public enum DataChangeType;
    {
        Added = 0,
        Updated = 1,
        Removed = 2,
        Expired = 3,
        Encrypted = 4,
        Decrypted = 5;
    }

    /// <summary>
    /// Main execution context for workflow orchestration;
    /// </summary>
    [JsonConverter(typeof(ExecutionContextJsonConverter))]
    public class ExecutionContext : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, ContextDataEntry> _dataStore = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _dataLocks = new();
        private readonly List<ActivityExecutionContext> _activityContexts = new();
        private readonly object _stateLock = new();
        private readonly Timer _cleanupTimer;
        private readonly Timer _statisticsTimer;
        private readonly CancellationTokenSource _cts = new();
        private bool _isDisposed;

        private ExecutionContextState _state = ExecutionContextState.Created;
        private int _activityExecutionCount = 0;
        private DateTime _createdTime = DateTime.UtcNow;
        private DateTime? _lastActivityTime;
        private TimeSpan _totalExecutionTime = TimeSpan.Zero;
        private int _successfulActivities = 0;
        private int _failedActivities = 0;
        private Aes _encryptionAlgorithm;

        // Statistics;
        private ExecutionContextStats _stats = new();
        private readonly object _statsLock = new();

        /// <summary>
        /// Context unique identifier;
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// Context name for identification;
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Context description;
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Parent context ID (for nested contexts)
        /// </summary>
        public Guid? ParentContextId { get; set; }

        /// <summary>
        /// Workflow instance ID;
        /// </summary>
        public Guid WorkflowInstanceId { get; set; }

        /// <summary>
        /// Execution context state;
        /// </summary>
        public ExecutionContextState State;
        {
            get { lock (_stateLock) return _state; }
            private set;
            {
                lock (_stateLock)
                {
                    if (_state != value)
                    {
                        var oldState = _state;
                        _state = value;
                        OnStateChanged(oldState, value);
                    }
                }
            }
        }

        /// <summary>
        /// Context priority for execution scheduling;
        /// </summary>
        public int Priority { get; set; } = 5;

        /// <summary>
        /// Maximum execution time before timeout;
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Cancellation token for context execution;
        /// </summary>
        public CancellationToken CancellationToken => _cts.Token;

        /// <summary>
        /// Logger instance for context logging;
        /// </summary>
        public ILogger Logger => _logger;

        /// <summary>
        /// Context creation time;
        /// </summary>
        public DateTime CreatedTime => _createdTime;

        /// <summary>
        /// Last activity execution time;
        /// </summary>
        public DateTime? LastActivityTime => _lastActivityTime;

        /// <summary>
        /// Total execution time across all activities;
        /// </summary>
        public TimeSpan TotalExecutionTime => _totalExecutionTime;

        /// <summary>
        /// Number of activities executed;
        /// </summary>
        public int ActivityExecutionCount => _activityExecutionCount;

        /// <summary>
        /// Number of successful activities;
        /// </summary>
        public int SuccessfulActivities => _successfulActivities;

        /// <summary>
        /// Number of failed activities;
        /// </summary>
        public int FailedActivities => _failedActivities;

        /// <summary>
        /// Success rate (0-100)
        /// </summary>
        public double SuccessRate => _activityExecutionCount > 0 ?
            (double)_successfulActivities / _activityExecutionCount * 100 : 0;

        /// <summary>
        /// Is context currently executing;
        /// </summary>
        public bool IsExecuting => State == ExecutionContextState.Executing;

        /// <summary>
        /// Is context completed;
        /// </summary>
        public bool IsCompleted => State == ExecutionContextState.Completed;

        /// <summary>
        /// Is context in error state;
        /// </summary>
        public bool IsInError => State == ExecutionContextState.Error ||
                                State == ExecutionContextState.Failed;

        /// <summary>
        /// Context configuration settings;
        /// </summary>
        public Dictionary<string, object> Configuration { get; } = new();

        /// <summary>
        /// Context metadata;
        /// </summary>
        public Dictionary<string, object> Metadata { get; } = new();

        /// <summary>
        /// Context tags for categorization;
        /// </summary>
        public List<string> Tags { get; } = new();

        /// <summary>
        /// Activity execution contexts history;
        /// </summary>
        public IReadOnlyList<ActivityExecutionContext> ActivityContexts => _activityContexts.AsReadOnly();

        /// <summary>
        /// Event fired when context state changes;
        /// </summary>
        public event EventHandler<ExecutionContextEventArgs> StateChanged;

        /// <summary>
        /// Event fired when context data changes;
        /// </summary>
        public event EventHandler<ContextDataChangedEventArgs> DataChanged;

        /// <summary>
        /// Event fired when activity completes;
        /// </summary>
        public event EventHandler<ActivityCompletedEventArgs> ActivityCompleted;

        /// <summary>
        /// Event fired when context statistics are updated;
        /// </summary>
        public event EventHandler<StatisticsUpdatedEventArgs> StatisticsUpdated;

        /// <summary>
        /// Constructor;
        /// </summary>
        public ExecutionContext(ILogger logger, string name = null, string description = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Name = name ?? $"Context_{Id.ToString().Substring(0, 8)}";
            Description = description;

            InitializeEncryption();
            InitializeTimers();

            _logger.LogInformation($"ExecutionContext created: {Name} ({Id})");
        }

        /// <summary>
        /// Initialize context for execution;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (State != ExecutionContextState.Created)
                throw new InvalidOperationException($"Context already initialized. Current state: {State}");

            State = ExecutionContextState.Initializing;

            try
            {
                // Load configuration if needed;
                await LoadConfigurationAsync(cancellationToken);

                // Initialize data store;
                await InitializeDataStoreAsync(cancellationToken);

                // Set up security context;
                await InitializeSecurityContextAsync(cancellationToken);

                State = ExecutionContextState.Ready;

                _logger.LogInformation($"ExecutionContext initialized: {Name}");
            }
            catch (Exception ex)
            {
                State = ExecutionContextState.Error;
                _logger.LogError($"Failed to initialize context {Name}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Execute an activity within this context;
        /// </summary>
        public async Task<ActivityResult> ExecuteActivityAsync(
            ActivityBase activity,
            Dictionary<string, object> inputData = null,
            CancellationToken cancellationToken = default)
        {
            if (activity == null)
                throw new ArgumentNullException(nameof(activity));

            if (State != ExecutionContextState.Ready && State != ExecutionContextState.Executing)
                throw new InvalidOperationException($"Context not ready for execution. Current state: {State}");

            // Create combined cancellation token;
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _cts.Token, cancellationToken);

            var activityContext = new ActivityExecutionContext;
            {
                WorkflowInstanceId = WorkflowInstanceId,
                Activity = activity,
                CancellationToken = linkedCts.Token,
                Logger = _logger;
            };

            // Set context reference;
            activityContext.ContextProperties["ExecutionContextId"] = Id;
            activityContext.ContextProperties["ExecutionContextName"] = Name;

            // Copy relevant data to activity context;
            await PrepareActivityContextAsync(activityContext, inputData);

            // Add to history;
            _activityContexts.Add(activityContext);

            // Update state;
            if (State != ExecutionContextState.Executing)
                State = ExecutionContextState.Executing;

            _lastActivityTime = DateTime.UtcNow;

            ActivityResult result = null;
            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogInformation($"Executing activity: {activity.Name} in context: {Name}");

                // Execute activity;
                result = await activity.ExecuteAsync(activityContext);

                // Update statistics;
                Interlocked.Increment(ref _activityExecutionCount);

                if (result.Success)
                    Interlocked.Increment(ref _successfulActivities);
                else;
                    Interlocked.Increment(ref _failedActivities);

                var executionTime = DateTime.UtcNow - startTime;
                _totalExecutionTime += executionTime;

                // Store activity results in context;
                await StoreActivityResultsAsync(activityContext, result);

                // Fire completion event;
                OnActivityCompleted(activity, result, executionTime);

                _logger.LogInformation($"Activity completed: {activity.Name}, " +
                                      $"Success: {result.Success}, Time: {executionTime.TotalMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _activityExecutionCount);
                Interlocked.Increment(ref _failedActivities);

                result = ActivityResult.FailureResult(
                    Guid.NewGuid(), activity.Name, ex);

                _logger.LogError($"Activity execution failed: {activity.Name}, Error: {ex.Message}");

                // Set context to error state if it's a critical failure;
                if (IsCriticalError(ex))
                    State = ExecutionContextState.Error;

                throw;
            }
            finally
            {
                linkedCts.Dispose();
                UpdateStatistics();
            }
        }

        /// <summary>
        /// Execute multiple activities in sequence;
        /// </summary>
        public async Task<List<ActivityResult>> ExecuteActivitiesAsync(
            List<ActivityBase> activities,
            Dictionary<string, object> commonInputData = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ActivityResult>();

            foreach (var activity in activities)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var result = await ExecuteActivityAsync(activity, commonInputData, cancellationToken);
                results.Add(result);

                // Stop on failure if configured;
                if (!result.Success && Configuration.TryGetValue("StopOnFailure", out var stop) &&
                    stop is bool stopOnFailure && stopOnFailure)
                    break;
            }

            return results;
        }

        /// <summary>
        /// Execute activities in parallel;
        /// </summary>
        public async Task<List<ActivityResult>> ExecuteActivitiesParallelAsync(
            List<ActivityBase> activities,
            Dictionary<string, object> commonInputData = null,
            int maxConcurrency = 4,
            CancellationToken cancellationToken = default)
        {
            var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task<ActivityResult>>();

            foreach (var activity in activities)
            {
                tasks.Add(ExecuteActivityWithSemaphoreAsync(
                    activity, commonInputData, semaphore, cancellationToken));
            }

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        /// <summary>
        /// Set data in context with specified scope;
        /// </summary>
        public async Task SetDataAsync(string key, object value,
            ContextScope scope = ContextScope.Activity,
            DataSecurityLevel securityLevel = DataSecurityLevel.Internal,
            TimeSpan? expiry = null,
            bool encrypt = false,
            string owner = null,
            Dictionary<string, object> metadata = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            var dataLock = _dataLocks.GetOrAdd(key, k => new SemaphoreSlim(1, 1));
            await dataLock.WaitAsync(cancellationToken);

            try
            {
                // Check if value needs encryption;
                object finalValue = value;
                byte[] iv = null;
                bool isEncrypted = false;

                if (encrypt && value != null && securityLevel >= DataSecurityLevel.Secret)
                {
                    finalValue = await EncryptDataAsync(value, cancellationToken);
                    iv = _encryptionAlgorithm.IV;
                    isEncrypted = true;
                }

                // Create or update data entry
                if (_dataStore.TryGetValue(key, out var existingEntry))
                {
                    var oldValue = existingEntry.Value;
                    existingEntry.UpdateValue(finalValue);
                    existingEntry.Scope = scope;
                    existingEntry.SecurityLevel = securityLevel;
                    existingEntry.ExpiryTime = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null;
                    existingEntry.Owner = owner ?? existingEntry.Owner;
                    existingEntry.IsEncrypted = isEncrypted;
                    existingEntry.EncryptionIV = iv;

                    if (metadata != null)
                    {
                        foreach (var kvp in metadata)
                            existingEntry.Metadata[kvp.Key] = kvp.Value;
                    }

                    OnDataChanged(key, oldValue, finalValue, scope, DataChangeType.Updated);

                    _logger.LogDebug($"Context data updated: {key}, Scope: {scope}, Encrypted: {isEncrypted}");
                }
                else;
                {
                    var entry = new ContextDataEntry
                    {
                        Key = key,
                        Value = finalValue,
                        ValueType = value?.GetType(),
                        Scope = scope,
                        SecurityLevel = securityLevel,
                        ExpiryTime = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null,
                        Owner = owner ?? "System",
                        IsEncrypted = isEncrypted,
                        EncryptionIV = iv;
                    };

                    if (metadata != null)
                    {
                        foreach (var kvp in metadata)
                            entry.Metadata[kvp.Key] = kvp.Value;
                    }

                    _dataStore[key] = entry
                    OnDataChanged(key, null, finalValue, scope, DataChangeType.Added);

                    _logger.LogDebug($"Context data added: {key}, Scope: {scope}, Encrypted: {isEncrypted}");
                }

                UpdateStatistics();
            }
            finally
            {
                dataLock.Release();
            }
        }

        /// <summary>
        /// Get data from context with type conversion;
        /// </summary>
        public async Task<T> GetDataAsync<T>(string key,
            T defaultValue = default,
            bool decryptIfEncrypted = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                return defaultValue;

            if (!_dataStore.TryGetValue(key, out var entry))
                return defaultValue;

            // Check expiry;
            if (entry.IsExpired)
            {
                await RemoveDataAsync(key, cancellationToken);
                return defaultValue;
            }

            // Check security access;
            if (!HasAccessToData(entry))
            {
                _logger.LogWarning($"Access denied to data: {key}, SecurityLevel: {entry.SecurityLevel}");
                return defaultValue;
            }

            // Decrypt if needed;
            object value = entry.Value;
            if (decryptIfEncrypted && entry.IsEncrypted && value is byte[] encryptedData)
            {
                try
                {
                    value = await DecryptDataAsync(encryptedData, entry.EncryptionIV, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to decrypt data {key}: {ex.Message}");
                    return defaultValue;
                }
            }

            // Convert to requested type;
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

        /// <summary>
        /// Get all data entries matching criteria;
        /// </summary>
        public List<ContextDataEntry> GetDataEntries(
            ContextScope? scope = null,
            DataSecurityLevel? maxSecurityLevel = null,
            string owner = null,
            bool includeExpired = false)
        {
            var query = _dataStore.Values.AsEnumerable();

            if (scope.HasValue)
                query = query.Where(e => e.Scope == scope.Value);

            if (maxSecurityLevel.HasValue)
                query = query.Where(e => e.SecurityLevel <= maxSecurityLevel.Value);

            if (!string.IsNullOrEmpty(owner))
                query = query.Where(e => e.Owner == owner);

            if (!includeExpired)
                query = query.Where(e => !e.IsExpired);

            return query.ToList();
        }

        /// <summary>
        /// Remove data from context;
        /// </summary>
        public async Task<bool> RemoveDataAsync(string key, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            var dataLock = _dataLocks.GetOrAdd(key, k => new SemaphoreSlim(1, 1));
            await dataLock.WaitAsync(cancellationToken);

            try
            {
                if (_dataStore.TryRemove(key, out var entry))
                {
                    OnDataChanged(key, entry.Value, null, entry.Scope, DataChangeType.Removed);
                    _dataLocks.TryRemove(key, out _);

                    _logger.LogDebug($"Context data removed: {key}");
                    return true;
                }

                return false;
            }
            finally
            {
                dataLock.Release();
            }
        }

        /// <summary>
        /// Clear all data in specified scope;
        /// </summary>
        public async Task<int> ClearDataAsync(ContextScope? scope = null,
            CancellationToken cancellationToken = default)
        {
            var keysToRemove = _dataStore;
                .Where(kvp => !scope.HasValue || kvp.Value.Scope == scope.Value)
                .Select(kvp => kvp.Key)
                .ToList();

            int removedCount = 0;

            foreach (var key in keysToRemove)
            {
                if (await RemoveDataAsync(key, cancellationToken))
                    removedCount++;

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            _logger.LogInformation($"Cleared {removedCount} data entries from context");
            return removedCount;
        }

        /// <summary>
        /// Check if data exists in context;
        /// </summary>
        public bool ContainsData(string key, bool checkExpiry = true)
        {
            if (!_dataStore.TryGetValue(key, out var entry))
                return false;

            if (checkExpiry && entry.IsExpired)
                return false;

            return true;
        }

        /// <summary>
        /// Create a child context with shared data;
        /// </summary>
        public ExecutionContext CreateChildContext(string name = null,
            bool inheritData = true,
            ContextScope? dataScopeFilter = null)
        {
            var childContext = new ExecutionContext(_logger, name)
            {
                ParentContextId = Id,
                WorkflowInstanceId = WorkflowInstanceId,
                Priority = Priority,
                Timeout = Timeout;
            };

            // Copy configuration;
            foreach (var kvp in Configuration)
                childContext.Configuration[kvp.Key] = kvp.Value;

            // Copy metadata;
            foreach (var kvp in Metadata)
                childContext.Metadata[kvp.Key] = kvp.Value;

            // Inherit data if requested;
            if (inheritData)
            {
                var dataToInherit = GetDataEntries(dataScopeFilter, null, null, false);
                foreach (var entry in dataToInherit)
                {
                    // Create copy of entry for child context;
                    var childEntry = new ContextDataEntry
                    {
                        Key = entry.Key,
                        Value = entry.Value,
                        ValueType = entry.ValueType,
                        Scope = entry.Scope,
                        SecurityLevel = entry.SecurityLevel,
                        Owner = entry.Owner,
                        IsEncrypted = entry.IsEncrypted,
                        EncryptionIV = entry.EncryptionIV,
                        ExpiryTime = entry.ExpiryTime;
                    };

                    foreach (var metadata in entry.Metadata)
                        childEntry.Metadata[metadata.Key] = metadata.Value;

                    childContext._dataStore[entry.Key] = childEntry
                }
            }

            _logger.LogInformation($"Child context created: {childContext.Name}, Parent: {Name}");

            return childContext;
        }

        /// <summary>
        /// Merge data from another context;
        /// </summary>
        public async Task MergeContextAsync(ExecutionContext sourceContext,
            bool overwriteExisting = false,
            ContextScope? scopeFilter = null,
            CancellationToken cancellationToken = default)
        {
            if (sourceContext == null)
                throw new ArgumentNullException(nameof(sourceContext));

            var sourceData = sourceContext.GetDataEntries(scopeFilter, null, null, false);

            foreach (var entry in sourceData)
            {
                if (overwriteExisting || !ContainsData(entry.Key))
                {
                    await SetDataAsync(
                        entry.Key,
                        entry.Value,
                        entry.Scope,
                        entry.SecurityLevel,
                        entry.ExpiryTime.HasValue ? entry.ExpiryTime.Value - entry.CreatedTime : (TimeSpan?)null,
                        entry.IsEncrypted,
                        entry.Owner,
                        entry.Metadata,
                        cancellationToken);
                }
            }

            _logger.LogInformation($"Merged data from context: {sourceContext.Name}, Entries: {sourceData.Count}");
        }

        /// <summary>
        /// Pause context execution;
        /// </summary>
        public async Task PauseAsync(CancellationToken cancellationToken = default)
        {
            if (State != ExecutionContextState.Executing)
                throw new InvalidOperationException($"Cannot pause context in state: {State}");

            State = ExecutionContextState.Paused;
            _cts.Cancel();

            await Task.Delay(100, cancellationToken); // Allow cancellation to propagate;

            _logger.LogInformation($"Context paused: {Name}");
        }

        /// <summary>
        /// Resume context execution;
        /// </summary>
        public async Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            if (State != ExecutionContextState.Paused)
                throw new InvalidOperationException($"Cannot resume context in state: {State}");

            // Create new cancellation token source;
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            State = ExecutionContextState.Ready;

            _logger.LogInformation($"Context resumed: {Name}");
        }

        /// <summary>
        /// Cancel context execution;
        /// </summary>
        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            if (State == ExecutionContextState.Cancelled || State == ExecutionContextState.Completed)
                return;

            var oldState = State;
            State = ExecutionContextState.Cancelled;
            _cts.Cancel();

            await Task.Delay(100, cancellationToken); // Allow cancellation to propagate;

            _logger.LogInformation($"Context cancelled: {Name}, Previous state: {oldState}");
        }

        /// <summary>
        /// Complete context execution;
        /// </summary>
        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (State != ExecutionContextState.Executing && State != ExecutionContextState.Ready)
                throw new InvalidOperationException($"Cannot complete context in state: {State}");

            // Perform cleanup;
            await CleanupAsync(cancellationToken);

            State = ExecutionContextState.Completed;

            _logger.LogInformation($"Context completed: {Name}");
        }

        /// <summary>
        /// Compensate (rollback) context execution;
        /// </summary>
        public async Task CompensateAsync(CancellationToken cancellationToken = default)
        {
            if (State != ExecutionContextState.Failed && State != ExecutionContextState.Error)
                throw new InvalidOperationException($"Cannot compensate context in state: {State}");

            State = ExecutionContextState.Compensating;

            try
            {
                // Execute compensation for each activity in reverse order;
                for (int i = _activityContexts.Count - 1; i >= 0; i--)
                {
                    var activityContext = _activityContexts[i];
                    if (activityContext.Activity != null)
                    {
                        try
                        {
                            await activityContext.Activity.CompensateAsync(
                                activityContext.LastResult, activityContext, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Compensation failed for activity {activityContext.Activity.Name}: {ex.Message}");
                        }
                    }
                }

                // Rollback data changes;
                await RollbackDataChangesAsync(cancellationToken);

                State = ExecutionContextState.Compensated;

                _logger.LogInformation($"Context compensation completed: {Name}");
            }
            catch (Exception ex)
            {
                State = ExecutionContextState.Error;
                _logger.LogError($"Context compensation failed: {Name}, Error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get context statistics;
        /// </summary>
        public ExecutionContextStats GetStatistics()
        {
            lock (_statsLock)
            {
                _stats.ContextId = Id;
                _stats.CreatedTime = _createdTime;
                _stats.LastActivityTime = _lastActivityTime;
                _stats.TotalExecutionTime = _totalExecutionTime;
                _stats.TotalActivitiesExecuted = _activityExecutionCount;
                _stats.SuccessfulActivities = _successfulActivities;
                _stats.FailedActivities = _failedActivities;
                _stats.DataEntriesCount = _dataStore.Count;
                _stats.MemoryUsageKB = (int)(GC.GetTotalMemory(false) / 1024);

                // Calculate data by scope;
                _stats.DataByScope.Clear();
                foreach (var entry in _dataStore.Values)
                {
                    if (!_stats.DataByScope.ContainsKey(entry.Scope))
                        _stats.DataByScope[entry.Scope] = 0;
                    _stats.DataByScope[entry.Scope]++;
                }

                // Calculate activity execution counts;
                _stats.ActivityExecutionCount.Clear();
                foreach (var activityContext in _activityContexts)
                {
                    if (activityContext.Activity != null)
                    {
                        var activityName = activityContext.Activity.Name;
                        if (!_stats.ActivityExecutionCount.ContainsKey(activityName))
                            _stats.ActivityExecutionCount[activityName] = 0;
                        _stats.ActivityExecutionCount[activityName]++;
                    }
                }

                return _stats;
            }
        }

        /// <summary>
        /// Export context data to JSON;
        /// </summary>
        public async Task<string> ExportToJsonAsync(bool includeSensitiveData = false,
            CancellationToken cancellationToken = default)
        {
            var exportData = new;
            {
                ContextId = Id,
                Name = Name,
                Description = Description,
                State = State.ToString(),
                CreatedTime = _createdTime,
                TotalExecutionTime = _totalExecutionTime,
                ActivityExecutionCount = _activityExecutionCount,
                Configuration = Configuration,
                Metadata = Metadata,
                Tags = Tags,
                Data = await ExportDataAsync(includeSensitiveData, cancellationToken),
                Activities = _activityContexts.Select(ac => new;
                {
                    ActivityName = ac.Activity?.Name,
                    ActivityId = ac.Activity?.Id,
                    ExecutionTime = ac.LastResult?.Duration,
                    Success = ac.LastResult?.Success;
                }).ToList()
            };

            return JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });
        }

        /// <summary>
        /// Import context data from JSON;
        /// </summary>
        public async Task ImportFromJsonAsync(string json,
            bool overwriteExisting = false,
            CancellationToken cancellationToken = default)
        {
            var importData = JsonSerializer.Deserialize<ContextImportData>(
                json, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

            if (importData == null)
                throw new InvalidOperationException("Failed to parse import JSON");

            // Import configuration;
            if (overwriteExisting || Configuration.Count == 0)
            {
                Configuration.Clear();
                foreach (var kvp in importData.Configuration)
                    Configuration[kvp.Key] = kvp.Value;
            }

            // Import metadata;
            if (overwriteExisting || Metadata.Count == 0)
            {
                Metadata.Clear();
                foreach (var kvp in importData.Metadata)
                    Metadata[kvp.Key] = kvp.Value;
            }

            // Import data;
            foreach (var dataEntry in importData.Data)
            {
                if (overwriteExisting || !ContainsData(dataEntry.Key))
                {
                    await SetDataAsync(
                        dataEntry.Key,
                        dataEntry.Value,
                        dataEntry.Scope,
                        dataEntry.SecurityLevel,
                        dataEntry.ExpiryTime.HasValue ? dataEntry.ExpiryTime.Value - DateTime.UtcNow : (TimeSpan?)null,
                        dataEntry.IsEncrypted,
                        dataEntry.Owner,
                        dataEntry.Metadata,
                        cancellationToken);
                }
            }

            _logger.LogInformation($"Context imported from JSON: {Name}, Data entries: {importData.Data.Count}");
        }

        /// <summary>
        /// Dispose context resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private void InitializeEncryption()
        {
            _encryptionAlgorithm = Aes.Create();
            _encryptionAlgorithm.KeySize = 256;
            _encryptionAlgorithm.GenerateKey();

            // Store encryption key in secure way;
            var keyBytes = _encryptionAlgorithm.Key;
            var ivBytes = _encryptionAlgorithm.IV;

            // In production, use secure storage like Windows DPAPI or Azure Key Vault;
            Configuration["EncryptionKeyHash"] = Convert.ToBase64String(SHA256.HashData(keyBytes));
            Configuration["EncryptionIVHash"] = Convert.ToBase64String(SHA256.HashData(ivBytes));
        }

        private void InitializeTimers()
        {
            // Cleanup timer for expired data (every 5 minutes)
            _cleanupTimer = new Timer(CleanupExpiredData, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            // Statistics timer (every 30 seconds)
            _statisticsTimer = new Timer(UpdateStatistics, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private async Task LoadConfigurationAsync(CancellationToken cancellationToken)
        {
            // Load configuration from external source if configured;
            if (Configuration.TryGetValue("ConfigurationSource", out var source))
            {
                // Implementation would load from file, database, etc.
                await Task.Delay(100, cancellationToken); // Simulate loading;
            }
        }

        private async Task InitializeDataStoreAsync(CancellationToken cancellationToken)
        {
            // Initialize with default data;
            await SetDataAsync("ExecutionContextId", Id,
                ContextScope.Global, DataSecurityLevel.Internal,
                owner: "System", cancellationToken: cancellationToken);

            await SetDataAsync("ExecutionContextName", Name,
                ContextScope.Global, DataSecurityLevel.Internal,
                owner: "System", cancellationToken: cancellationToken);

            await SetDataAsync("CreatedTime", _createdTime,
                ContextScope.Global, DataSecurityLevel.Internal,
                owner: "System", cancellationToken: cancellationToken);

            await SetDataAsync("MachineName", Environment.MachineName,
                ContextScope.Global, DataSecurityLevel.Internal,
                owner: "System", cancellationToken: cancellationToken);

            await SetDataAsync("UserName", Environment.UserName,
                ContextScope.Global, DataSecurityLevel.Internal,
                owner: "System", cancellationToken: cancellationToken);
        }

        private async Task InitializeSecurityContextAsync(CancellationToken cancellationToken)
        {
            // Initialize security context;
            var securityContext = new;
            {
                Identity = Environment.UserName,
                Machine = Environment.MachineName,
                Domain = Environment.UserDomainName,
                ProcessId = Environment.ProcessId,
                Is64Bit = Environment.Is64BitProcess,
                OSVersion = Environment.OSVersion.VersionString;
            };

            await SetDataAsync("SecurityContext", securityContext,
                ContextScope.Global, DataSecurityLevel.Confidential,
                encrypt: true, owner: "System", cancellationToken: cancellationToken);
        }

        private async Task PrepareActivityContextAsync(ActivityExecutionContext activityContext,
            Dictionary<string, object> inputData)
        {
            // Copy relevant context data to activity context;
            var relevantData = GetDataEntries(ContextScope.Global, DataSecurityLevel.Confidential, null, false);

            foreach (var entry in relevantData)
            {
                if (HasAccessToData(entry))
                {
                    var value = await GetDataAsync<object>(entry.Key,
                        decryptIfEncrypted: false,
                        cancellationToken: activityContext.CancellationToken);

                    activityContext.SharedData[entry.Key] = value;
                }
            }

            // Add input data;
            if (inputData != null)
            {
                foreach (var kvp in inputData)
                {
                    activityContext.InputData[kvp.Key] = kvp.Value;
                }
            }
        }

        private async Task StoreActivityResultsAsync(ActivityExecutionContext activityContext,
            ActivityResult result)
        {
            // Store activity results in context;
            var resultKey = $"ActivityResult_{activityContext.Activity.Id}_{Guid.NewGuid():N}";

            await SetDataAsync(resultKey, result,
                ContextScope.Activity, DataSecurityLevel.Internal,
                expiry: TimeSpan.FromDays(7),
                owner: "System",
                metadata: new Dictionary<string, object>
                {
                    ["ActivityName"] = activityContext.Activity.Name,
                    ["ActivityId"] = activityContext.Activity.Id,
                    ["ExecutionTime"] = result.Duration,
                    ["Success"] = result.Success;
                },
                cancellationToken: activityContext.CancellationToken);

            // Store activity output data;
            foreach (var kvp in result.OutputData)
            {
                var outputKey = $"ActivityOutput_{activityContext.Activity.Name}_{kvp.Key}";
                await SetDataAsync(outputKey, kvp.Value,
                    ContextScope.Activity, DataSecurityLevel.Internal,
                    expiry: TimeSpan.FromDays(1),
                    owner: "System",
                    cancellationToken: activityContext.CancellationToken);
            }
        }

        private async Task<byte[]> EncryptDataAsync(object data, CancellationToken cancellationToken)
        {
            if (data == null) return null;

            var json = JsonSerializer.Serialize(data);
            var plainBytes = Encoding.UTF8.GetBytes(json);

            _encryptionAlgorithm.GenerateIV();
            using var encryptor = _encryptionAlgorithm.CreateEncryptor();
            using var ms = new MemoryStream();

            // Write IV first;
            await ms.WriteAsync(_encryptionAlgorithm.IV, 0, _encryptionAlgorithm.IV.Length, cancellationToken);

            // Encrypt data;
            using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
            await cs.WriteAsync(plainBytes, 0, plainBytes.Length, cancellationToken);
            cs.FlushFinalBlock();

            return ms.ToArray();
        }

        private async Task<object> DecryptDataAsync(byte[] encryptedData, byte[] iv,
            CancellationToken cancellationToken)
        {
            if (encryptedData == null || encryptedData.Length == 0) return null;

            _encryptionAlgorithm.IV = iv;
            using var decryptor = _encryptionAlgorithm.CreateDecryptor();
            using var ms = new MemoryStream();
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Write);

            // Decrypt data (skip IV which is already set)
            await cs.WriteAsync(encryptedData, 0, encryptedData.Length, cancellationToken);
            cs.FlushFinalBlock();

            var decryptedBytes = ms.ToArray();
            var json = Encoding.UTF8.GetString(decryptedBytes);

            return JsonSerializer.Deserialize<object>(json);
        }

        private bool HasAccessToData(ContextDataEntry entry)
        {
            // Implement access control logic;
            // For now, allow access to all non-secret data;
            return entry.SecurityLevel <= DataSecurityLevel.Confidential;
        }

        private bool IsCriticalError(Exception ex)
        {
            // Define what constitutes a critical error;
            return ex is OutOfMemoryException ||
                   ex is StackOverflowException ||
                   ex is SecurityException ||
                   ex is CryptographicException;
        }

        private async Task<ActivityResult> ExecuteActivityWithSemaphoreAsync(
            ActivityBase activity,
            Dictionary<string, object> inputData,
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                return await ExecuteActivityAsync(activity, inputData, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task RollbackDataChangesAsync(CancellationToken cancellationToken)
        {
            // Implement data change tracking and rollback;
            // For now, just log the operation;
            _logger.LogInformation($"Rolling back data changes for context: {Name}");
            await Task.Delay(100, cancellationToken);
        }

        private async Task<Dictionary<string, object>> ExportDataAsync(bool includeSensitiveData,
            CancellationToken cancellationToken)
        {
            var exportedData = new Dictionary<string, object>();
            var dataEntries = GetDataEntries(null,
                includeSensitiveData ? null : DataSecurityLevel.Confidential,
                null, false);

            foreach (var entry in dataEntries)
            {
                if (!includeSensitiveData && entry.IsEncrypted)
                    continue;

                var value = await GetDataAsync<object>(entry.Key,
                    decryptIfEncrypted: includeSensitiveData && entry.IsEncrypted,
                    cancellationToken: cancellationToken);

                exportedData[entry.Key] = new;
                {
                    Value = value,
                    Type = entry.ValueType?.Name,
                    Scope = entry.Scope.ToString(),
                    SecurityLevel = entry.SecurityLevel.ToString(),
                    CreatedTime = entry.CreatedTime,
                    ExpiryTime = entry.ExpiryTime,
                    IsEncrypted = entry.IsEncrypted,
                    Owner = entry.Owner;
                };
            }

            return exportedData;
        }

        private void CleanupExpiredData(object state)
        {
            try
            {
                var expiredKeys = _dataStore;
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    if (_dataStore.TryRemove(key, out var entry))
                    {
                        OnDataChanged(key, entry.Value, null, entry.Scope, DataChangeType.Expired);
                        _logger.LogDebug($"Expired data removed: {key}");
                    }
                }

                if (expiredKeys.Count > 0)
                {
                    UpdateStatistics();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error cleaning up expired data: {ex.Message}");
            }
        }

        private void UpdateStatistics(object state = null)
        {
            try
            {
                var stats = GetStatistics();
                OnStatisticsUpdated(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating statistics: {ex.Message}");
            }
        }

        private async Task CleanupAsync(CancellationToken cancellationToken)
        {
            // Cleanup resources;
            await ClearDataAsync(null, cancellationToken);

            // Dispose activity contexts;
            foreach (var activityContext in _activityContexts)
            {
                if (activityContext.Activity is IDisposable disposable)
                    disposable.Dispose();
            }
            _activityContexts.Clear();
        }

        private void OnStateChanged(ExecutionContextState oldState, ExecutionContextState newState)
        {
            StateChanged?.Invoke(this, new ExecutionContextEventArgs;
            {
                ContextId = Id,
                ContextName = Name,
                OldState = oldState,
                NewState = newState,
                EventData =
                {
                    ["Timestamp"] = DateTime.UtcNow,
                    ["ThreadId"] = Thread.CurrentThread.ManagedThreadId;
                }
            });
        }

        private void OnDataChanged(string key, object oldValue, object newValue,
            ContextScope scope, DataChangeType changeType)
        {
            DataChanged?.Invoke(this, new ContextDataChangedEventArgs;
            {
                ContextId = Id,
                Key = key,
                OldValue = oldValue,
                NewValue = newValue,
                Scope = scope,
                ChangeType = changeType;
            });
        }

        private void OnActivityCompleted(ActivityBase activity, ActivityResult result, TimeSpan executionTime)
        {
            ActivityCompleted?.Invoke(this, new ActivityCompletedEventArgs;
            {
                ContextId = Id,
                ContextName = Name,
                ActivityId = activity.Id,
                ActivityName = activity.Name,
                Result = result,
                ExecutionTime = executionTime,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnStatisticsUpdated(ExecutionContextStats stats)
        {
            StatisticsUpdated?.Invoke(this, new StatisticsUpdatedEventArgs;
            {
                ContextId = Id,
                ContextName = Name,
                Statistics = stats,
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
                    _encryptionAlgorithm?.Dispose();

                    // Cleanup data locks;
                    foreach (var lockObj in _dataLocks.Values)
                        lockObj.Dispose();
                    _dataLocks.Clear();

                    // Clear data store;
                    _dataStore.Clear();

                    // Clear activity contexts;
                    _activityContexts.Clear();

                    State = ExecutionContextState.Disposed;

                    _logger.LogInformation($"ExecutionContext disposed: {Name}");
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Nested Classes;

        private class ContextImportData;
        {
            public Dictionary<string, object> Configuration { get; set; } = new();
            public Dictionary<string, object> Metadata { get; set; } = new();
            public List<ContextDataEntry> Data { get; set; } = new();
        }

        #endregion;
    }

    /// <summary>
    /// Activity completed event arguments;
    /// </summary>
    public class ActivityCompletedEventArgs : EventArgs;
    {
        public Guid ContextId { get; set; }
        public string ContextName { get; set; }
        public Guid ActivityId { get; set; }
        public string ActivityName { get; set; }
        public ActivityResult Result { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Statistics updated event arguments;
    /// </summary>
    public class StatisticsUpdatedEventArgs : EventArgs;
    {
        public Guid ContextId { get; set; }
        public string ContextName { get; set; }
        public ExecutionContextStats Statistics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// JSON converter for ExecutionContext;
    /// </summary>
    public class ExecutionContextJsonConverter : JsonConverter<ExecutionContext>
    {
        public override ExecutionContext Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Deserialization would be complex due to runtime state;
            // In practice, we would store/load context from persistent storage;
            throw new NotSupportedException("ExecutionContext cannot be directly deserialized. Use ImportFromJsonAsync instead.");
        }

        public override void Write(Utf8JsonWriter writer, ExecutionContext value, JsonSerializerOptions options)
        {
            // Serialization would export context data;
            var json = value.ExportToJsonAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            writer.WriteRawValue(json);
        }
    }
}
