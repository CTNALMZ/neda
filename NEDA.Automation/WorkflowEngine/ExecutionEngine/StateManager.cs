using NEDA.Core.Logging;
using NEDA.Core.Common.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace NEDA.Automation.WorkflowEngine.ExecutionEngine;
{
    /// <summary>
    /// State persistence modes;
    /// </summary>
    public enum StatePersistenceMode;
    {
        /// <summary>
        /// State is kept only in memory;
        /// </summary>
        InMemory = 0,

        /// <summary>
        /// State is persisted to disk;
        /// </summary>
        Persistent = 1,

        /// <summary>
        /// State is replicated across multiple nodes;
        /// </summary>
        Distributed = 2,

        /// <summary>
        /// State is persisted with high durability (multiple replicas)
        /// </summary>
        HighlyAvailable = 3,

        /// <summary>
        /// State is encrypted and persisted;
        /// </summary>
        SecurePersistent = 4;
    }

    /// <summary>
    /// State consistency levels;
    /// </summary>
    public enum StateConsistencyLevel;
    {
        /// <summary>
        /// Eventual consistency;
        /// </summary>
        Eventual = 0,

        /// <summary>
        /// Causal consistency;
        /// </summary>
        Causal = 1,

        /// <summary>
        /// Sequential consistency;
        /// </summary>
        Sequential = 2,

        /// <summary>
        /// Linearizable consistency;
        /// </summary>
        Linearizable = 3,

        /// <summary>
        /// Strong consistency (immediate)
        /// </summary>
        Strong = 4;
    }

    /// <summary>
    /// State snapshot metadata;
    /// </summary>
    public class StateSnapshot;
    {
        public Guid SnapshotId { get; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreatedTime { get; } = DateTime.UtcNow;
        public DateTime? ExpiryTime { get; set; }
        public long SizeBytes { get; set; }
        public Dictionary<string, object> Metadata { get; } = new();
        public string Checksum { get; set; }
        public string CreatedBy { get; set; }
        public StatePersistenceMode PersistenceMode { get; set; }
        public bool IsCompressed { get; set; }
        public bool IsEncrypted { get; set; }

        public bool IsExpired => ExpiryTime.HasValue && DateTime.UtcNow > ExpiryTime.Value;

        public override string ToString()
        {
            return $"{Name} ({SnapshotId:N}) - {CreatedTime:yyyy-MM-dd HH:mm:ss} - {SizeBytes:N0} bytes";
        }
    }

    /// <summary>
    /// State transition record;
    /// </summary>
    public class StateTransition;
    {
        public Guid TransitionId { get; } = Guid.NewGuid();
        public Guid StateId { get; set; }
        public string FromState { get; set; }
        public string ToState { get; set; }
        public DateTime TransitionTime { get; } = DateTime.UtcNow;
        public string TriggeredBy { get; set; }
        public Dictionary<string, object> TransitionData { get; } = new();
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; } = true;
        public Exception Error { get; set; }
        public List<string> Tags { get; } = new();

        public override string ToString()
        {
            return $"{FromState} → {ToState} by {TriggeredBy} at {TransitionTime:HH:mm:ss.fff}";
        }
    }

    /// <summary>
    /// State lock information;
    /// </summary>
    public class StateLock;
    {
        public string LockId { get; } = Guid.NewGuid().ToString();
        public string ResourceId { get; set; }
        public string Owner { get; set; }
        public DateTime AcquiredTime { get; } = DateTime.UtcNow;
        public DateTime? ExpiryTime { get; set; }
        public LockMode Mode { get; set; }
        public int Depth { get; set; } = 1;
        public string CallStack { get; set; }

        public bool IsExpired => ExpiryTime.HasValue && DateTime.UtcNow > ExpiryTime.Value;
        public bool IsValid => !IsExpired;

        public override string ToString()
        {
            return $"{ResourceId} - {Mode} lock by {Owner} (expires: {ExpiryTime:HH:mm:ss})";
        }
    }

    /// <summary>
    /// Lock modes for state access;
    /// </summary>
    public enum LockMode;
    {
        /// <summary>
        /// Shared read lock;
        /// </summary>
        Shared = 0,

        /// <summary>
        /// Exclusive write lock;
        /// </summary>
        Exclusive = 1,

        /// <summary>
        /// Update lock (can upgrade to exclusive)
        /// </summary>
        Update = 2,

        /// <summary>
        /// Intent shared lock;
        /// </summary>
        IntentShared = 3,

        /// <summary>
        /// Intent exclusive lock;
        /// </summary>
        IntentExclusive = 4,

        /// <summary>
        /// Schema modification lock;
        /// </summary>
        SchemaModification = 5;
    }

    /// <summary>
    /// State manager statistics;
    /// </summary>
    public class StateManagerStats;
    {
        public int TotalStatesManaged { get; set; }
        public int ActiveStates { get; set; }
        public int SnapshotsCount { get; set; }
        public int TransitionsCount { get; set; }
        public int ActiveLocks { get; set; }
        public long TotalMemoryUsageBytes { get; set; }
        public double AverageStateSizeBytes { get; set; }
        public TimeSpan Uptime { get; set; }
        public Dictionary<string, int> StatesByType { get; } = new();
        public Dictionary<StatePersistenceMode, int> StatesByPersistence { get; } = new();
        public int LockWaitTimeouts { get; set; }
        public int DeadlockDetections { get; set; }
        public int StateLoads { get; set; }
        public int StateSaves { get; set; }

        public override string ToString()
        {
            return $"States: {ActiveStates}/{TotalStatesManaged}, " +
                   $"Snapshots: {SnapshotsCount}, Transitions: {TransitionsCount}, " +
                   $"Memory: {TotalMemoryUsageBytes:N0} bytes, Uptime: {Uptime:hh\\:mm\\:ss}";
        }
    }

    /// <summary>
    /// State change event arguments;
    /// </summary>
    public class StateChangedEventArgs : EventArgs;
    {
        public Guid StateId { get; set; }
        public string StateType { get; set; }
        public string OldState { get; set; }
        public string NewState { get; set; }
        public DateTime ChangeTime { get; set; } = DateTime.UtcNow;
        public string ChangedBy { get; set; }
        public Dictionary<string, object> ChangeData { get; } = new();
        public bool IsSnapshot { get; set; }
    }

    /// <summary>
    /// State conflict resolution strategies;
    /// </summary>
    public enum ConflictResolutionStrategy;
    {
        /// <summary>
        /// Last write wins;
        /// </summary>
        LastWriteWins = 0,

        /// <summary>
        /// First write wins;
        /// </summary>
        FirstWriteWins = 1,

        /// <summary>
        /// Merge conflicting changes;
        /// </summary>
        Merge = 2,

        /// <summary>
        /// Custom conflict handler;
        /// </summary>
        Custom = 3,

        /// <summary>
        /// Throw exception on conflict;
        /// </summary>
        Throw = 4,

        /// <summary>
        /// Use version vectors for resolution;
        /// </summary>
        VersionVector = 5;
    }

    /// <summary>
    /// State version information;
    /// </summary>
    public class StateVersion;
    {
        public long Version { get; set; }
        public DateTime Timestamp { get; set; }
        public string ModifiedBy { get; set; }
        public Guid ModificationId { get; set; }
        public Dictionary<string, long> VectorClock { get; } = new();

        public bool IsConcurrentWith(StateVersion other)
        {
            if (other == null) return false;

            var thisIsGreater = false;
            var otherIsGreater = false;

            foreach (var node in VectorClock.Keys.Union(other.VectorClock.Keys))
            {
                var thisValue = VectorClock.GetValueOrDefault(node, 0);
                var otherValue = other.VectorClock.GetValueOrDefault(node, 0);

                if (thisValue > otherValue) thisIsGreater = true;
                if (otherValue > thisValue) otherIsGreater = true;
            }

            return thisIsGreater && otherIsGreater;
        }

        public override string ToString()
        {
            return $"v{Version} at {Timestamp:yyyy-MM-dd HH:mm:ss.fff} by {ModifiedBy}";
        }
    }

    /// <summary>
    /// Advanced state manager for workflow state management;
    /// </summary>
    public class StateManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Guid, ManagedState> _states = new();
        private readonly ConcurrentDictionary<string, StateLock> _locks = new();
        private readonly ConcurrentDictionary<Guid, StateSnapshot> _snapshots = new();
        private readonly ConcurrentDictionary<Guid, List<StateTransition>> _stateTransitions = new();
        private readonly Timer _cleanupTimer;
        private readonly Timer _persistenceTimer;
        private readonly Timer _statisticsTimer;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _statsLock = new();
        private bool _isDisposed;

        // Statistics;
        private readonly StateManagerStats _stats = new();
        private DateTime _startTime = DateTime.UtcNow;
        private long _totalMemoryUsage = 0;
        private int _lockWaitTimeouts = 0;
        private int _deadlockDetections = 0;

        // Configuration;
        private readonly StateManagerConfig _config;

        /// <summary>
        /// Event fired when state changes;
        /// </summary>
        public event EventHandler<StateChangedEventArgs> StateChanged;

        /// <summary>
        /// Event fired when snapshot is created;
        /// </summary>
        public event EventHandler<SnapshotCreatedEventArgs> SnapshotCreated;

        /// <summary>
        /// Event fired when lock is acquired or released;
        /// </summary>
        public event EventHandler<LockEventArgs> LockChanged;

        /// <summary>
        /// Event fired when statistics are updated;
        /// </summary>
        public event EventHandler<StatisticsUpdatedEventArgs> StatisticsUpdated;

        public StateManager(ILogger logger, StateManagerConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? new StateManagerConfig();

            InitializeTimers();
            LoadPersistedStates();

            _logger.LogInformation($"StateManager initialized with config: {_config}");
        }

        /// <summary>
        /// Register a new state for management;
        /// </summary>
        public async Task<Guid> RegisterStateAsync(object state,
            string stateType = "Generic",
            StatePersistenceMode persistenceMode = StatePersistenceMode.InMemory,
            string owner = null,
            Dictionary<string, object> metadata = null,
            CancellationToken cancellationToken = default)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));

            var stateId = Guid.NewGuid();
            var managedState = new ManagedState;
            {
                Id = stateId,
                State = state,
                StateType = stateType,
                PersistenceMode = persistenceMode,
                Owner = owner ?? "System",
                CreatedTime = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                Version = new StateVersion;
                {
                    Version = 1,
                    Timestamp = DateTime.UtcNow,
                    ModifiedBy = owner ?? "System",
                    ModificationId = Guid.NewGuid()
                }
            };

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                    managedState.Metadata[kvp.Key] = kvp.Value;
            }

            // Initialize vector clock if distributed;
            if (persistenceMode == StatePersistenceMode.Distributed ||
                persistenceMode == StatePersistenceMode.HighlyAvailable)
            {
                managedState.Version.VectorClock[GetNodeId()] = 1;
            }

            // Store in memory;
            _states[stateId] = managedState;

            // Persist if needed;
            if (persistenceMode != StatePersistenceMode.InMemory)
            {
                await PersistStateAsync(managedState, cancellationToken);
            }

            // Update statistics;
            UpdateStatistics();

            _logger.LogInformation($"State registered: {stateId} ({stateType}), Persistence: {persistenceMode}");

            return stateId;
        }

        /// <summary>
        /// Get state by ID;
        /// </summary>
        public async Task<T> GetStateAsync<T>(Guid stateId,
            bool acquireLock = false,
            LockMode lockMode = LockMode.Shared,
            TimeSpan? lockTimeout = null,
            CancellationToken cancellationToken = default)
        {
            if (!_states.TryGetValue(stateId, out var managedState))
            {
                // Try to load from persistence;
                managedState = await LoadStateAsync(stateId, cancellationToken);
                if (managedState == null)
                    throw new KeyNotFoundException($"State not found: {stateId}");

                _states[stateId] = managedState;
            }

            // Check if state is expired;
            if (managedState.ExpiryTime.HasValue && DateTime.UtcNow > managedState.ExpiryTime.Value)
            {
                await RemoveStateAsync(stateId, cancellationToken);
                throw new InvalidOperationException($"State has expired: {stateId}");
            }

            // Acquire lock if requested;
            if (acquireLock)
            {
                var lockKey = $"state_{stateId}";
                var lockAcquired = await AcquireLockAsync(lockKey, lockMode,
                    lockTimeout ?? _config.DefaultLockTimeout,
                    managedState.Owner, cancellationToken);

                if (!lockAcquired)
                    throw new TimeoutException($"Failed to acquire lock for state: {stateId}");
            }

            // Update access time;
            managedState.LastAccessed = DateTime.UtcNow;

            // Update statistics;
            Interlocked.Increment(ref _stats.StateLoads);

            return (T)managedState.State;
        }

        /// <summary>
        /// Update state with new value;
        /// </summary>
        public async Task UpdateStateAsync<T>(Guid stateId, T newState,
            string modifiedBy = null,
            ConflictResolutionStrategy conflictStrategy = ConflictResolutionStrategy.LastWriteWins,
            Dictionary<string, object> changeData = null,
            CancellationToken cancellationToken = default)
        {
            if (!_states.TryGetValue(stateId, out var managedState))
                throw new KeyNotFoundException($"State not found: {stateId}");

            // Acquire exclusive lock;
            var lockKey = $"state_{stateId}";
            var lockAcquired = await AcquireLockAsync(lockKey, LockMode.Exclusive,
                _config.DefaultLockTimeout, modifiedBy ?? managedState.Owner, cancellationToken);

            if (!lockAcquired)
                throw new TimeoutException($"Failed to acquire exclusive lock for state update: {stateId}");

            try
            {
                // Check for conflicts;
                var oldState = managedState.State;
                var oldVersion = managedState.Version;

                // Create new version;
                var newVersion = new StateVersion;
                {
                    Version = oldVersion.Version + 1,
                    Timestamp = DateTime.UtcNow,
                    ModifiedBy = modifiedBy ?? managedState.Owner,
                    ModificationId = Guid.NewGuid()
                };

                // Handle distributed versioning;
                if (managedState.PersistenceMode == StatePersistenceMode.Distributed ||
                    managedState.PersistenceMode == StatePersistenceMode.HighlyAvailable)
                {
                    newVersion.VectorClock = new Dictionary<string, long>(oldVersion.VectorClock);
                    var nodeId = GetNodeId();
                    newVersion.VectorClock[nodeId] = newVersion.VectorClock.GetValueOrDefault(nodeId, 0) + 1;

                    // Check for concurrent modifications;
                    if (IsConcurrentModification(oldVersion, newVersion))
                    {
                        await HandleConflictAsync(managedState, newState, conflictStrategy, cancellationToken);
                    }
                }

                // Update state;
                managedState.State = newState;
                managedState.Version = newVersion;
                managedState.LastModified = DateTime.UtcNow;
                managedState.ModificationCount++;

                // Record transition;
                var transition = new StateTransition;
                {
                    StateId = stateId,
                    FromState = GetStateString(oldState),
                    ToState = GetStateString(newState),
                    TriggeredBy = modifiedBy ?? managedState.Owner,
                    Duration = DateTime.UtcNow - managedState.LastModified,
                    Success = true;
                };

                if (changeData != null)
                {
                    foreach (var kvp in changeData)
                        transition.TransitionData[kvp.Key] = kvp.Value;
                }

                AddTransition(stateId, transition);

                // Fire state change event;
                OnStateChanged(stateId, managedState.StateType,
                    GetStateString(oldState), GetStateString(newState),
                    modifiedBy, changeData);

                // Persist if needed;
                if (managedState.PersistenceMode != StatePersistenceMode.InMemory)
                {
                    await PersistStateAsync(managedState, cancellationToken);
                }

                // Update statistics;
                Interlocked.Increment(ref _stats.StateSaves);

                _logger.LogDebug($"State updated: {stateId}, Version: {newVersion.Version}, " +
                               $"Modified by: {modifiedBy}");
            }
            finally
            {
                // Release lock;
                await ReleaseLockAsync(lockKey, cancellationToken);
            }
        }

        /// <summary>
        /// Create snapshot of current state;
        /// </summary>
        public async Task<Guid> CreateSnapshotAsync(Guid stateId,
            string name = null,
            string description = null,
            bool includeTransitions = false,
            TimeSpan? expiry = null,
            CancellationToken cancellationToken = default)
        {
            if (!_states.TryGetValue(stateId, out var managedState))
                throw new KeyNotFoundException($"State not found: {stateId}");

            // Acquire shared lock for snapshot;
            var lockKey = $"state_{stateId}";
            var lockAcquired = await AcquireLockAsync(lockKey, LockMode.Shared,
                _config.DefaultLockTimeout, "SnapshotService", cancellationToken);

            if (!lockAcquired)
                throw new TimeoutException($"Failed to acquire lock for snapshot: {stateId}");

            try
            {
                var snapshotId = Guid.NewGuid();
                var snapshotData = SerializeState(managedState.State);

                // Create snapshot metadata;
                var snapshot = new StateSnapshot;
                {
                    Name = name ?? $"Snapshot_{stateId:N}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                    Description = description ?? $"Snapshot of {managedState.StateType} state",
                    CreatedTime = DateTime.UtcNow,
                    ExpiryTime = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null,
                    SizeBytes = snapshotData.Length,
                    CreatedBy = "SnapshotService",
                    PersistenceMode = managedState.PersistenceMode,
                    IsEncrypted = managedState.PersistenceMode == StatePersistenceMode.SecurePersistent;
                };

                // Calculate checksum;
                using var sha256 = SHA256.Create();
                snapshot.Checksum = BitConverter.ToString(sha256.ComputeHash(snapshotData))
                    .Replace("-", "").ToLowerInvariant();

                // Store snapshot;
                _snapshots[snapshotId] = snapshot;

                // Persist snapshot data;
                await PersistSnapshotAsync(snapshotId, snapshotData, cancellationToken);

                // Include transitions if requested;
                if (includeTransitions && _stateTransitions.TryGetValue(stateId, out var transitions))
                {
                    var transitionsData = SerializeState(transitions);
                    await PersistSnapshotTransitionsAsync(snapshotId, transitionsData, cancellationToken);
                }

                // Update statistics;
                Interlocked.Increment(ref _stats.SnapshotsCount);

                // Fire event;
                OnSnapshotCreated(snapshotId, snapshot, managedState);

                _logger.LogInformation($"Snapshot created: {snapshotId} for state {stateId}, " +
                                     $"Size: {snapshot.SizeBytes:N0} bytes");

                return snapshotId;
            }
            finally
            {
                await ReleaseLockAsync(lockKey, cancellationToken);
            }
        }

        /// <summary>
        /// Restore state from snapshot;
        /// </summary>
        public async Task RestoreFromSnapshotAsync(Guid stateId, Guid snapshotId,
            bool restoreTransitions = false,
            string restoredBy = null,
            CancellationToken cancellationToken = default)
        {
            if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
                throw new KeyNotFoundException($"Snapshot not found: {snapshotId}");

            if (snapshot.IsExpired)
                throw new InvalidOperationException($"Snapshot has expired: {snapshotId}");

            // Acquire exclusive lock for restore;
            var lockKey = $"state_{stateId}";
            var lockAcquired = await AcquireLockAsync(lockKey, LockMode.Exclusive,
                _config.DefaultLockTimeout, restoredBy ?? "RestoreService", cancellationToken);

            if (!lockAcquired)
                throw new TimeoutException($"Failed to acquire lock for restore: {stateId}");

            try
            {
                // Load snapshot data;
                var snapshotData = await LoadSnapshotDataAsync(snapshotId, cancellationToken);
                if (snapshotData == null)
                    throw new InvalidOperationException($"Snapshot data not found: {snapshotId}");

                // Verify checksum;
                using var sha256 = SHA256.Create();
                var checksum = BitConverter.ToString(sha256.ComputeHash(snapshotData))
                    .Replace("-", "").ToLowerInvariant();

                if (checksum != snapshot.Checksum)
                    throw new InvalidOperationException($"Snapshot checksum mismatch: {snapshotId}");

                // Deserialize state;
                var restoredState = DeserializeState(snapshotData);

                // Get current state (if any)
                var oldState = _states.TryGetValue(stateId, out var current) ?
                    current.State : null;

                // Update or create state;
                if (current != null)
                {
                    current.State = restoredState;
                    current.LastModified = DateTime.UtcNow;
                    current.Version = new StateVersion;
                    {
                        Version = current.Version.Version + 1,
                        Timestamp = DateTime.UtcNow,
                        ModifiedBy = restoredBy ?? "RestoreService",
                        ModificationId = Guid.NewGuid()
                    };
                }
                else;
                {
                    current = new ManagedState;
                    {
                        Id = stateId,
                        State = restoredState,
                        StateType = snapshot.Metadata.GetValueOrDefault("StateType") as string ?? "Unknown",
                        PersistenceMode = snapshot.PersistenceMode,
                        Owner = restoredBy ?? "RestoreService",
                        CreatedTime = DateTime.UtcNow,
                        LastModified = DateTime.UtcNow,
                        Version = new StateVersion;
                        {
                            Version = 1,
                            Timestamp = DateTime.UtcNow,
                            ModifiedBy = restoredBy ?? "RestoreService",
                            ModificationId = Guid.NewGuid()
                        }
                    };
                    _states[stateId] = current;
                }

                // Restore transitions if requested;
                if (restoreTransitions)
                {
                    var transitionsData = await LoadSnapshotTransitionsAsync(snapshotId, cancellationToken);
                    if (transitionsData != null)
                    {
                        var transitions = DeserializeState<List<StateTransition>>(transitionsData);
                        _stateTransitions[stateId] = transitions;
                    }
                }

                // Persist if needed;
                if (current.PersistenceMode != StatePersistenceMode.InMemory)
                {
                    await PersistStateAsync(current, cancellationToken);
                }

                // Record restore transition;
                var transition = new StateTransition;
                {
                    StateId = stateId,
                    FromState = GetStateString(oldState),
                    ToState = GetStateString(restoredState),
                    TriggeredBy = restoredBy ?? "RestoreService",
                    Duration = TimeSpan.Zero,
                    Success = true,
                    Tags = { "Restore", $"Snapshot:{snapshotId}" }
                };

                AddTransition(stateId, transition);

                // Fire events;
                OnStateChanged(stateId, current.StateType,
                    GetStateString(oldState), GetStateString(restoredState),
                    restoredBy, new Dictionary<string, object> { ["SnapshotId"] = snapshotId });

                _logger.LogInformation($"State restored from snapshot: {stateId} ← {snapshotId}");
            }
            finally
            {
                await ReleaseLockAsync(lockKey, cancellationToken);
            }
        }

        /// <summary>
        /// Acquire lock on a resource;
        /// </summary>
        public async Task<bool> AcquireLockAsync(string resourceId,
            LockMode mode,
            TimeSpan timeout,
            string owner,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException("Resource ID cannot be empty", nameof(resourceId));

            var startTime = DateTime.UtcNow;
            var lockKey = $"lock_{resourceId}";

            while (DateTime.UtcNow - startTime < timeout)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                // Check for deadlocks;
                if (DetectDeadlock(resourceId, owner, mode))
                {
                    Interlocked.Increment(ref _deadlockDetections);
                    _logger.LogWarning($"Deadlock detected for resource: {resourceId}, owner: {owner}");

                    if (_config.DeadlockResolution == DeadlockResolution.Abort)
                        return false;

                    // Try deadlock resolution;
                    if (await ResolveDeadlockAsync(resourceId, cancellationToken))
                        continue;
                }

                if (_locks.TryGetValue(lockKey, out var existingLock))
                {
                    // Check lock compatibility;
                    if (IsLockCompatible(existingLock.Mode, mode) && existingLock.Owner == owner)
                    {
                        // Same owner, increase lock depth;
                        existingLock.Depth++;
                        existingLock.ExpiryTime = DateTime.UtcNow.Add(_config.DefaultLockDuration);

                        OnLockChanged(resourceId, existingLock, LockEventType.Deepened);
                        return true;
                    }
                    else if (existingLock.IsExpired)
                    {
                        // Expired lock, remove it;
                        _locks.TryRemove(lockKey, out _);
                        continue;
                    }

                    // Lock is held by someone else, wait;
                    await Task.Delay(_config.LockRetryInterval, cancellationToken);
                    continue;
                }
                else;
                {
                    // Create new lock;
                    var newLock = new StateLock;
                    {
                        ResourceId = resourceId,
                        Owner = owner,
                        Mode = mode,
                        ExpiryTime = DateTime.UtcNow.Add(_config.DefaultLockDuration),
                        CallStack = Environment.StackTrace;
                    };

                    if (_locks.TryAdd(lockKey, newLock))
                    {
                        OnLockChanged(resourceId, newLock, LockEventType.Acquired);
                        return true;
                    }
                }
            }

            // Timeout;
            Interlocked.Increment(ref _lockWaitTimeouts);
            _logger.LogWarning($"Lock timeout for resource: {resourceId}, owner: {owner}, mode: {mode}");
            return false;
        }

        /// <summary>
        /// Release lock on a resource;
        /// </summary>
        public async Task<bool> ReleaseLockAsync(string resourceId,
            CancellationToken cancellationToken = default)
        {
            var lockKey = $"lock_{resourceId}";

            if (_locks.TryGetValue(lockKey, out var existingLock))
            {
                if (existingLock.Depth > 1)
                {
                    // Decrease lock depth;
                    existingLock.Depth--;
                    existingLock.ExpiryTime = DateTime.UtcNow.Add(_config.DefaultLockDuration);

                    OnLockChanged(resourceId, existingLock, LockEventType.Shallow);
                    return true;
                }
                else;
                {
                    // Remove lock completely;
                    if (_locks.TryRemove(lockKey, out _))
                    {
                        OnLockChanged(resourceId, existingLock, LockEventType.Released);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Force release all locks owned by a specific owner;
        /// </summary>
        public async Task<int> ForceReleaseLocksAsync(string owner,
            CancellationToken cancellationToken = default)
        {
            var locksToRelease = _locks;
                .Where(kvp => kvp.Value.Owner == owner)
                .Select(kvp => kvp.Key)
                .ToList();

            int releasedCount = 0;

            foreach (var lockKey in locksToRelease)
            {
                if (await ReleaseLockAsync(lockKey.Replace("lock_", ""), cancellationToken))
                    releasedCount++;
            }

            _logger.LogInformation($"Force released {releasedCount} locks owned by: {owner}");
            return releasedCount;
        }

        /// <summary>
        /// Get state transitions for analysis;
        /// </summary>
        public List<StateTransition> GetStateTransitions(Guid stateId,
            DateTime? from = null, DateTime? to = null)
        {
            if (_stateTransitions.TryGetValue(stateId, out var transitions))
            {
                var filtered = transitions.AsEnumerable();

                if (from.HasValue)
                    filtered = filtered.Where(t => t.TransitionTime >= from.Value);

                if (to.HasValue)
                    filtered = filtered.Where(t => t.TransitionTime <= to.Value);

                return filtered.ToList();
            }

            return new List<StateTransition>();
        }

        /// <summary>
        /// Get state metadata;
        /// </summary>
        public async Task<Dictionary<string, object>> GetStateMetadataAsync(Guid stateId,
            CancellationToken cancellationToken = default)
        {
            if (!_states.TryGetValue(stateId, out var managedState))
            {
                // Try to load from persistence;
                managedState = await LoadStateAsync(stateId, cancellationToken);
                if (managedState == null)
                    throw new KeyNotFoundException($"State not found: {stateId}");
            }

            return new Dictionary<string, object>
            {
                ["Id"] = managedState.Id,
                ["StateType"] = managedState.StateType,
                ["PersistenceMode"] = managedState.PersistenceMode,
                ["Owner"] = managedState.Owner,
                ["CreatedTime"] = managedState.CreatedTime,
                ["LastModified"] = managedState.LastModified,
                ["LastAccessed"] = managedState.LastAccessed,
                ["ModificationCount"] = managedState.ModificationCount,
                ["Version"] = managedState.Version.Version,
                ["ExpiryTime"] = managedState.ExpiryTime,
                ["SizeBytes"] = GetStateSize(managedState.State)
            };
        }

        /// <summary>
        /// Remove state from management;
        /// </summary>
        public async Task<bool> RemoveStateAsync(Guid stateId,
            CancellationToken cancellationToken = default)
        {
            // Force release any locks on this state;
            await ForceReleaseLocksAsync($"state_{stateId}", cancellationToken);

            // Remove from memory;
            var removed = _states.TryRemove(stateId, out var managedState);

            if (removed && managedState != null)
            {
                // Remove from persistence;
                if (managedState.PersistenceMode != StatePersistenceMode.InMemory)
                {
                    await DeletePersistedStateAsync(stateId, cancellationToken);
                }

                // Remove transitions;
                _stateTransitions.TryRemove(stateId, out _);

                // Update statistics;
                UpdateStatistics();

                _logger.LogInformation($"State removed: {stateId} ({managedState.StateType})");
            }

            return removed;
        }

        /// <summary>
        /// Cleanup expired states and snapshots;
        /// </summary>
        public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
        {
            int cleanedCount = 0;

            // Cleanup expired states;
            var expiredStates = _states;
                .Where(kvp => kvp.Value.ExpiryTime.HasValue &&
                             DateTime.UtcNow > kvp.Value.ExpiryTime.Value)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var stateId in expiredStates)
            {
                if (await RemoveStateAsync(stateId, cancellationToken))
                    cleanedCount++;
            }

            // Cleanup expired snapshots;
            var expiredSnapshots = _snapshots;
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var snapshotId in expiredSnapshots)
            {
                if (_snapshots.TryRemove(snapshotId, out _))
                {
                    await DeletePersistedSnapshotAsync(snapshotId, cancellationToken);
                    cleanedCount++;
                }
            }

            // Cleanup old transitions;
            var cutoffTime = DateTime.UtcNow.AddDays(-_config.TransitionRetentionDays);
            foreach (var kvp in _stateTransitions)
            {
                kvp.Value.RemoveAll(t => t.TransitionTime < cutoffTime);
                if (kvp.Value.Count == 0)
                    _stateTransitions.TryRemove(kvp.Key, out _);
            }

            if (cleanedCount > 0)
            {
                _logger.LogInformation($"Cleanup completed: {cleanedCount} items removed");
            }

            return cleanedCount;
        }

        /// <summary>
        /// Get state manager statistics;
        /// </summary>
        public StateManagerStats GetStatistics()
        {
            lock (_statsLock)
            {
                _stats.TotalStatesManaged = _states.Count;
                _stats.ActiveStates = _states.Count(s => !s.Value.ExpiryTime.HasValue ||
                                                         s.Value.ExpiryTime.Value > DateTime.UtcNow);
                _stats.SnapshotsCount = _snapshots.Count;
                _stats.TransitionsCount = _stateTransitions.Sum(t => t.Value.Count);
                _stats.ActiveLocks = _locks.Count;
                _stats.TotalMemoryUsageBytes = _totalMemoryUsage;
                _stats.AverageStateSizeBytes = _states.Count > 0 ?
                    _totalMemoryUsage / _states.Count : 0;
                _stats.Uptime = DateTime.UtcNow - _startTime;
                _stats.LockWaitTimeouts = _lockWaitTimeouts;
                _stats.DeadlockDetections = _deadlockDetections;
                _stats.StateLoads = _stats.StateLoads;
                _stats.StateSaves = _stats.StateSaves;

                // Calculate states by type;
                _stats.StatesByType.Clear();
                foreach (var state in _states.Values)
                {
                    if (!_stats.StatesByType.ContainsKey(state.StateType))
                        _stats.StatesByType[state.StateType] = 0;
                    _stats.StatesByType[state.StateType]++;
                }

                // Calculate states by persistence mode;
                _stats.StatesByPersistence.Clear();
                foreach (var state in _states.Values)
                {
                    if (!_stats.StatesByPersistence.ContainsKey(state.PersistenceMode))
                        _stats.StatesByPersistence[state.PersistenceMode] = 0;
                    _stats.StatesByPersistence[state.PersistenceMode]++;
                }

                return _stats;
            }
        }

        /// <summary>
        /// Export state to JSON;
        /// </summary>
        public async Task<string> ExportStateAsync(Guid stateId,
            bool includeTransitions = false,
            CancellationToken cancellationToken = default)
        {
            if (!_states.TryGetValue(stateId, out var managedState))
                throw new KeyNotFoundException($"State not found: {stateId}");

            var exportData = new;
            {
                StateId = stateId,
                StateType = managedState.StateType,
                State = managedState.State,
                Metadata = new;
                {
                    managedState.Owner,
                    managedState.CreatedTime,
                    managedState.LastModified,
                    managedState.Version,
                    managedState.PersistenceMode;
                },
                Transitions = includeTransitions && _stateTransitions.TryGetValue(stateId, out var transitions) ?
                    transitions : null;
            };

            return JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });
        }

        /// <summary>
        /// Import state from JSON;
        /// </summary>
        public async Task<Guid> ImportStateAsync(string json,
            string owner = null,
            CancellationToken cancellationToken = default)
        {
            var importData = JsonSerializer.Deserialize<StateImportData>(json);
            if (importData == null)
                throw new InvalidOperationException("Failed to parse import JSON");

            var stateId = importData.StateId ?? Guid.NewGuid();

            await RegisterStateAsync(
                importData.State,
                importData.StateType,
                importData.Metadata?.PersistenceMode ?? StatePersistenceMode.InMemory,
                owner ?? importData.Metadata?.Owner ?? "ImportService",
                new Dictionary<string, object>
                {
                    ["ImportedFrom"] = importData.Metadata?.Source ?? "JSON",
                    ["ImportTime"] = DateTime.UtcNow;
                },
                cancellationToken);

            // Import transitions if present;
            if (importData.Transitions != null)
            {
                _stateTransitions[stateId] = importData.Transitions;
            }

            _logger.LogInformation($"State imported: {stateId} ({importData.StateType})");

            return stateId;
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
            // Cleanup timer (every 5 minutes)
            _cleanupTimer = new Timer(async _ =>
            {
                try
                {
                    await CleanupExpiredAsync(_cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Cleanup timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            // Persistence timer (every 30 seconds)
            _persistenceTimer = new Timer(async _ =>
            {
                try
                {
                    await PersistDirtyStatesAsync(_cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Persistence timer error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            // Statistics timer (every 60 seconds)
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
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        private void LoadPersistedStates()
        {
            // In production, this would load states from persistent storage;
            // For now, just log that we would load;
            _logger.LogDebug("Persisted states loading would occur here");
        }

        private async Task PersistStateAsync(ManagedState state, CancellationToken cancellationToken)
        {
            // Simulate persistence delay;
            await Task.Delay(10, cancellationToken);

            // In production, this would save to database, file system, etc.
            var data = SerializeState(state.State);
            _logger.LogDebug($"State persisted: {state.Id}, Size: {data.Length} bytes");
        }

        private async Task<ManagedState> LoadStateAsync(Guid stateId, CancellationToken cancellationToken)
        {
            // Simulate loading delay;
            await Task.Delay(10, cancellationToken);

            // In production, this would load from persistent storage;
            _logger.LogDebug($"State loaded from persistence: {stateId}");
            return null; // Return null for simulation;
        }

        private async Task PersistSnapshotAsync(Guid snapshotId, byte[] data, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            _logger.LogDebug($"Snapshot persisted: {snapshotId}, Size: {data.Length} bytes");
        }

        private async Task<byte[]> LoadSnapshotDataAsync(Guid snapshotId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            _logger.LogDebug($"Snapshot data loaded: {snapshotId}");
            return new byte[0]; // Empty for simulation;
        }

        private async Task PersistSnapshotTransitionsAsync(Guid snapshotId, byte[] data, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            _logger.LogDebug($"Snapshot transitions persisted: {snapshotId}");
        }

        private async Task<byte[]> LoadSnapshotTransitionsAsync(Guid snapshotId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            _logger.LogDebug($"Snapshot transitions loaded: {snapshotId}");
            return null;
        }

        private async Task DeletePersistedStateAsync(Guid stateId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            _logger.LogDebug($"Persisted state deleted: {stateId}");
        }

        private async Task DeletePersistedSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            _logger.LogDebug($"Persisted snapshot deleted: {snapshotId}");
        }

        private async Task PersistDirtyStatesAsync(CancellationToken cancellationToken)
        {
            var dirtyStates = _states.Values;
                .Where(s => s.LastModified > s.LastPersisted)
                .ToList();

            foreach (var state in dirtyStates)
            {
                if (state.PersistenceMode != StatePersistenceMode.InMemory)
                {
                    await PersistStateAsync(state, cancellationToken);
                    state.LastPersisted = DateTime.UtcNow;
                }
            }

            if (dirtyStates.Count > 0)
            {
                _logger.LogDebug($"Persisted {dirtyStates.Count} dirty states");
            }
        }

        private byte[] SerializeState(object state)
        {
            return JsonSerializer.SerializeToUtf8Bytes(state);
        }

        private T DeserializeState<T>(byte[] data)
        {
            return JsonSerializer.Deserialize<T>(data);
        }

        private object DeserializeState(byte[] data)
        {
            return JsonSerializer.Deserialize<object>(data);
        }

        private string GetStateString(object state)
        {
            if (state == null) return "null";
            return state.GetType().Name;
        }

        private long GetStateSize(object state)
        {
            if (state == null) return 0;
            var serialized = SerializeState(state);
            return serialized.Length;
        }

        private string GetNodeId()
        {
            return Environment.MachineName;
        }

        private bool IsConcurrentModification(StateVersion oldVersion, StateVersion newVersion)
        {
            return oldVersion.IsConcurrentWith(newVersion);
        }

        private async Task HandleConflictAsync(ManagedState managedState, object newState,
            ConflictResolutionStrategy strategy, CancellationToken cancellationToken)
        {
            _logger.LogWarning($"Conflict detected for state: {managedState.Id}, Strategy: {strategy}");

            switch (strategy)
            {
                case ConflictResolutionStrategy.LastWriteWins:
                    // Keep the new state (default behavior)
                    break;

                case ConflictResolutionStrategy.FirstWriteWins:
                    // Revert to old state;
                    managedState.State = newState;
                    break;

                case ConflictResolutionStrategy.Merge:
                    // Attempt to merge (implementation depends on state type)
                    await MergeStatesAsync(managedState, newState, cancellationToken);
                    break;

                case ConflictResolutionStrategy.Throw:
                    throw new InvalidOperationException($"State conflict detected: {managedState.Id}");

                case ConflictResolutionStrategy.VersionVector:
                    // Use vector clock to resolve;
                    await ResolveWithVersionVectorAsync(managedState, newState, cancellationToken);
                    break;

                case ConflictResolutionStrategy.Custom:
                    // Call custom conflict handler;
                    if (_config.CustomConflictHandler != null)
                    {
                        await _config.CustomConflictHandler(managedState, newState, cancellationToken);
                    }
                    break;
            }
        }

        private async Task MergeStatesAsync(ManagedState managedState, object newState,
            CancellationToken cancellationToken)
        {
            // Default merge implementation (would be type-specific in production)
            // For now, just use last write wins;
            managedState.State = newState;
            await Task.CompletedTask;
        }

        private async Task ResolveWithVersionVectorAsync(ManagedState managedState, object newState,
            CancellationToken cancellationToken)
        {
            // Simple vector clock resolution;
            // In production, would compare vector clocks and merge;
            managedState.State = newState;
            await Task.CompletedTask;
        }

        private bool IsLockCompatible(LockMode heldMode, LockMode requestedMode)
        {
            // Lock compatibility matrix;
            var compatibility = new Dictionary<LockMode, LockMode[]>
            {
                [LockMode.Shared] = new[] { LockMode.Shared, LockMode.IntentShared },
                [LockMode.Exclusive] = new LockMode[0], // Incompatible with everything;
                [LockMode.Update] = new[] { LockMode.Shared, LockMode.IntentShared },
                [LockMode.IntentShared] = new[] { LockMode.Shared, LockMode.Update, LockMode.IntentShared, LockMode.IntentExclusive },
                [LockMode.IntentExclusive] = new[] { LockMode.IntentShared, LockMode.IntentExclusive },
                [LockMode.SchemaModification] = new LockMode[0] // Incompatible with everything;
            };

            return compatibility[heldMode].Contains(requestedMode);
        }

        private bool DetectDeadlock(string resourceId, string owner, LockMode mode)
        {
            // Simple deadlock detection;
            // In production, would use wait-for graph or timeout-based detection;
            return false;
        }

        private async Task<bool> ResolveDeadlockAsync(string resourceId, CancellationToken cancellationToken)
        {
            // Simple deadlock resolution: release oldest lock;
            var oldestLock = _locks.Values;
                .Where(l => l.ResourceId != resourceId)
                .OrderBy(l => l.AcquiredTime)
                .FirstOrDefault();

            if (oldestLock != null)
            {
                await ReleaseLockAsync(oldestLock.ResourceId, cancellationToken);
                return true;
            }

            return false;
        }

        private void AddTransition(Guid stateId, StateTransition transition)
        {
            var transitions = _stateTransitions.GetOrAdd(stateId, _ => new List<StateTransition>());
            transitions.Add(transition);

            // Keep only recent transitions;
            if (transitions.Count > _config.MaxTransitionsPerState)
            {
                transitions.RemoveRange(0, transitions.Count - _config.MaxTransitionsPerState);
            }
        }

        private void UpdateStatistics()
        {
            _totalMemoryUsage = _states.Values.Sum(s => GetStateSize(s.State));
        }

        private void OnStateChanged(Guid stateId, string stateType, string oldState,
            string newState, string changedBy, Dictionary<string, object> changeData)
        {
            StateChanged?.Invoke(this, new StateChangedEventArgs;
            {
                StateId = stateId,
                StateType = stateType,
                OldState = oldState,
                NewState = newState,
                ChangeTime = DateTime.UtcNow,
                ChangedBy = changedBy,
                ChangeData = changeData ?? new Dictionary<string, object>()
            });
        }

        private void OnSnapshotCreated(Guid snapshotId, StateSnapshot snapshot, ManagedState state)
        {
            SnapshotCreated?.Invoke(this, new SnapshotCreatedEventArgs;
            {
                SnapshotId = snapshotId,
                Snapshot = snapshot,
                StateId = state.Id,
                StateType = state.StateType,
                CreatedTime = DateTime.UtcNow;
            });
        }

        private void OnLockChanged(string resourceId, StateLock stateLock, LockEventType eventType)
        {
            LockChanged?.Invoke(this, new LockEventArgs;
            {
                ResourceId = resourceId,
                Lock = stateLock,
                EventType = eventType,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnStatisticsUpdated(StateManagerStats stats)
        {
            StatisticsUpdated?.Invoke(this, new StatisticsUpdatedEventArgs;
            {
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
                    _persistenceTimer?.Dispose();
                    _statisticsTimer?.Dispose();

                    // Persist all states before disposal;
                    _ = PersistDirtyStatesAsync(CancellationToken.None).ConfigureAwait(false);

                    _logger.LogInformation("StateManager disposed");
                }

                _isDisposed = true;
            }
        }

        #endregion;

        #region Nested Classes;

        private class ManagedState;
        {
            public Guid Id { get; set; }
            public object State { get; set; }
            public string StateType { get; set; }
            public StatePersistenceMode PersistenceMode { get; set; }
            public string Owner { get; set; }
            public DateTime CreatedTime { get; set; }
            public DateTime LastModified { get; set; }
            public DateTime LastAccessed { get; set; }
            public DateTime LastPersisted { get; set; }
            public int ModificationCount { get; set; }
            public StateVersion Version { get; set; }
            public DateTime? ExpiryTime { get; set; }
            public Dictionary<string, object> Metadata { get; } = new();
        }

        private class StateImportData;
        {
            public Guid? StateId { get; set; }
            public string StateType { get; set; }
            public object State { get; set; }
            public StateMetadata Metadata { get; set; }
            public List<StateTransition> Transitions { get; set; }
        }

        private class StateMetadata;
        {
            public string Owner { get; set; }
            public DateTime CreatedTime { get; set; }
            public StateVersion Version { get; set; }
            public StatePersistenceMode PersistenceMode { get; set; }
            public string Source { get; set; }
        }

        #endregion;
    }

    /// <summary>
    /// State manager configuration;
    /// </summary>
    public class StateManagerConfig;
    {
        public TimeSpan DefaultLockDuration { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan DefaultLockTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan LockRetryInterval { get; set; } = TimeSpan.FromMilliseconds(100);
        public int MaxTransitionsPerState { get; set; } = 1000;
        public int TransitionRetentionDays { get; set; } = 30;
        public DeadlockResolution DeadlockResolution { get; set; } = DeadlockResolution.Abort;
        public Func<ManagedState, object, CancellationToken, Task> CustomConflictHandler { get; set; }

        public override string ToString()
        {
            return $"LockDuration: {DefaultLockDuration}, LockTimeout: {DefaultLockTimeout}, " +
                   $"MaxTransitions: {MaxTransitionsPerState}, DeadlockResolution: {DeadlockResolution}";
        }
    }

    /// <summary>
    /// Deadlock resolution strategies;
    /// </summary>
    public enum DeadlockResolution;
    {
        Abort = 0,
        Wait = 1,
        KillOldest = 2,
        KillYoungest = 3,
        Custom = 4;
    }

    /// <summary>
    /// Snapshot created event arguments;
    /// </summary>
    public class SnapshotCreatedEventArgs : EventArgs;
    {
        public Guid SnapshotId { get; set; }
        public StateSnapshot Snapshot { get; set; }
        public Guid StateId { get; set; }
        public string StateType { get; set; }
        public DateTime CreatedTime { get; set; }
    }

    /// <summary>
    /// Lock event arguments;
    /// </summary>
    public class LockEventArgs : EventArgs;
    {
        public string ResourceId { get; set; }
        public StateLock Lock { get; set; }
        public LockEventType EventType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Lock event types;
    /// </summary>
    public enum LockEventType;
    {
        Acquired = 0,
        Released = 1,
        Deepened = 2,
        Shallow = 3,
        Expired = 4,
        ForceReleased = 5;
    }

    /// <summary>
    /// Statistics updated event arguments;
    /// </summary>
    public class StatisticsUpdatedEventArgs : EventArgs;
    {
        public StateManagerStats Statistics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Helper classes for internal use;
    internal class ManagedState;
    {
        public Guid Id { get; set; }
        public object State { get; set; }
        public string StateType { get; set; }
        public StatePersistenceMode PersistenceMode { get; set; }
        public string Owner { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime LastPersisted { get; set; }
        public int ModificationCount { get; set; }
        public StateVersion Version { get; set; }
        public DateTime? ExpiryTime { get; set; }
        public Dictionary<string, object> Metadata { get; } = new();
    }
}
