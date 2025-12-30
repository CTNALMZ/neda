using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Monitoring.Diagnostics;
using NEDA.Services.Messaging.EventBus;
using NEDA.Services.Messaging.MessageQueue;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.AI_Behaviors.BlackboardSystems.Blackboard;

namespace NEDA.CharacterSystems.AI_Behaviors.BlackboardSystems;
{
    /// <summary>
    /// Main Blackboard system interface for AI knowledge sharing and data storage;
    /// </summary>
    public interface IBlackboard : IDisposable
    {
        Task<bool> SetValueAsync<T>(string key, T value, BlackboardEntryOptions options = null);
        Task<BlackboardResult<T>> GetValueAsync<T>(string key);
        Task<bool> RemoveValueAsync(string key);
        Task<bool> ContainsKeyAsync(string key);
        Task ClearAsync();

        Task<bool> SubscribeAsync(string key, IBlackboardObserver observer);
        Task<bool> UnsubscribeAsync(string key, IBlackboardObserver observer);

        Task<BlackboardSnapshot> GetSnapshotAsync();
        Task<bool> RestoreFromSnapshotAsync(BlackboardSnapshot snapshot);

        Task<IEnumerable<BlackboardEntryInfo>> GetEntriesAsync();
        Task<int> GetEntryCountAsync();

        Task<bool> LockEntryAsync(string key, TimeSpan timeout);
        Task<bool> UnlockEntryAsync(string key);

        Task<BlackboardStats> GetStatisticsAsync();
        Task<bool> ValidateAsync();
    }

    /// <summary>
    /// Main Blackboard implementation for AI behavior coordination;
    /// </summary>
    public class Blackboard : IBlackboard;
    {
        private readonly ILogger<Blackboard> _logger;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IEventBus _eventBus;
        private readonly IMessageBroker _messageBroker;

        // Main storage with thread safety;
        private readonly ConcurrentDictionary<string, BlackboardEntry> _storage;

        // Observers for key changes;
        private readonly ConcurrentDictionary<string, List<IBlackboardObserver>> _observers;

        // Entry locks for synchronization;
        private readonly ConcurrentDictionary<string, EntryLock> _entryLocks;

        // Statistics and monitoring;
        private readonly BlackboardStatistics _statistics;
        private readonly PerformanceMetrics _performanceMetrics;

        // Configuration;
        private BlackboardConfig _config;
        private bool _isInitialized;
        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);

        // Cleanup and maintenance;
        private Timer _cleanupTimer;
        private Timer _statisticsTimer;
        private readonly CancellationTokenSource _shutdownCts;

        /// <summary>
        /// Blackboard constructor with dependency injection;
        /// </summary>
        public Blackboard(
            ILogger<Blackboard> logger,
            IDiagnosticTool diagnosticTool,
            IEventBus eventBus,
            IMessageBroker messageBroker = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _messageBroker = messageBroker;

            _storage = new ConcurrentDictionary<string, BlackboardEntry>();
            _observers = new ConcurrentDictionary<string, List<IBlackboardObserver>>();
            _entryLocks = new ConcurrentDictionary<string, EntryLock>();

            _statistics = new BlackboardStatistics();
            _performanceMetrics = new PerformanceMetrics();

            _shutdownCts = new CancellationTokenSource();

            _logger.LogInformation("Blackboard initialized with default configuration");
        }

        /// <summary>
        /// Initialize blackboard with configuration;
        /// </summary>
        public async Task InitializeAsync(BlackboardConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            await _initializationLock.WaitAsync();

            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("Blackboard already initialized");
                    return;
                }

                _logger.LogInformation("Initializing Blackboard with configuration: {ConfigName}", config.Name);

                _config = config;

                // Set up cleanup timer if enabled;
                if (config.EnableAutoCleanup && config.CleanupInterval > TimeSpan.Zero)
                {
                    _cleanupTimer = new Timer(
                        async _ => await PerformCleanupAsync(),
                        null,
                        config.CleanupInterval,
                        config.CleanupInterval);

                    _logger.LogDebug("Auto-cleanup timer started with interval: {Interval}", config.CleanupInterval);
                }

                // Set up statistics timer if enabled;
                if (config.EnableStatistics && config.StatisticsInterval > TimeSpan.Zero)
                {
                    _statisticsTimer = new Timer(
                        async _ => await UpdateStatisticsAsync(),
                        null,
                        config.StatisticsInterval,
                        config.StatisticsInterval);

                    _logger.LogDebug("Statistics timer started with interval: {Interval}", config.StatisticsInterval);
                }

                // Initialize with default values if provided;
                if (config.DefaultEntries != null && config.DefaultEntries.Any())
                {
                    await InitializeDefaultEntriesAsync(config.DefaultEntries);
                }

                // Set up message broker integration if configured;
                if (_messageBroker != null && config.EnableMessageBrokerIntegration)
                {
                    await SetupMessageBrokerIntegrationAsync();
                }

                _isInitialized = true;

                await _eventBus.PublishAsync(new BlackboardInitializedEvent;
                {
                    BlackboardId = _config.Id,
                    Name = config.Name,
                    DefaultEntryCount = config.DefaultEntries?.Count ?? 0,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Blackboard initialized successfully with ID: {BlackboardId}", _config.Id);
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        /// <summary>
        /// Set a value in the blackboard;
        /// </summary>
        public async Task<bool> SetValueAsync<T>(string key, T value, BlackboardEntryOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("BlackboardSetValue");

            try
            {
                // Validate key format;
                if (!IsValidKey(key))
                {
                    _logger.LogWarning("Invalid key format: {Key}", key);
                    return false;
                }

                // Check entry lock;
                if (await IsEntryLockedAsync(key))
                {
                    _logger.LogWarning("Entry is locked: {Key}", key);
                    return false;
                }

                // Apply access control;
                if (!await CheckAccessControlAsync(key, BlackboardOperation.Write))
                {
                    _logger.LogWarning("Access denied for key: {Key}", key);
                    return false;
                }

                var startTime = DateTime.UtcNow;
                var previousValue = default(object);
                var previousVersion = 0;

                // Create or update entry
                var entry = new BlackboardEntry
                {
                    Key = key,
                    Value = value,
                    ValueType = typeof(T),
                    Options = options ?? new BlackboardEntryOptions(),
                    Version = 1,
                    LastModified = DateTime.UtcNow,
                    ModifiedBy = GetCurrentContext(),
                    AccessCount = 0;
                };

                var result = _storage.AddOrUpdate(key,
                    // Add new entry
                    entry,
                    // Update existing entry
                    (k, existing) =>
                    {
                        previousValue = existing.Value;
                        previousVersion = existing.Version;

                        // Check version conflict if enabled;
                        if (options?.CheckVersionConflict == true &&
                            existing.Version != options.ExpectedVersion)
                        {
                            throw new BlackboardVersionConflictException(
                                $"Version conflict for key '{key}'. Expected: {options.ExpectedVersion}, Actual: {existing.Version}");
                        }

                        // Check if value actually changed;
                        if (EqualityComparer<T>.Default.Equals((T)existing.Value, value) &&
                            !options?.ForceUpdate == true)
                        {
                            return existing; // No change needed;
                        }

                        existing.Value = value;
                        existing.ValueType = typeof(T);
                        existing.Options = options ?? existing.Options;
                        existing.Version++;
                        existing.LastModified = DateTime.UtcNow;
                        existing.ModifiedBy = GetCurrentContext();

                        return existing;
                    });

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Update statistics;
                Interlocked.Increment(ref _statistics.SetOperations);
                _performanceMetrics.RecordOperation(BlackboardOperation.Write, duration);

                // Notify observers if value changed;
                if (result.Version > previousVersion || previousValue == null)
                {
                    await NotifyObserversAsync(key, result, previousValue, BlackboardChangeType.Updated);

                    // Publish event;
                    await _eventBus.PublishAsync(new BlackboardValueChangedEvent;
                    {
                        BlackboardId = _config.Id,
                        Key = key,
                        NewValue = value,
                        OldValue = previousValue,
                        Version = result.Version,
                        ChangeType = previousValue == null ?
                            BlackboardChangeType.Added : BlackboardChangeType.Updated,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Publish to message broker if configured;
                    if (_messageBroker != null && _config.EnableMessageBrokerIntegration)
                    {
                        await _messageBroker.PublishAsync(new BlackboardMessage;
                        {
                            Operation = "SET",
                            Key = key,
                            Value = value,
                            Timestamp = DateTime.UtcNow;
                        });
                    }

                    _logger.LogDebug("Set value for key: {Key} (v{Version}) in {Duration}ms",
                        key, result.Version, duration.TotalMilliseconds);
                }
                else;
                {
                    _logger.LogDebug("Value unchanged for key: {Key}", key);
                }

                return true;
            }
            catch (BlackboardVersionConflictException ex)
            {
                _logger.LogWarning(ex, "Version conflict for key: {Key}", key);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting value for key: {Key}", key);
                _performanceMetrics.RecordError(BlackboardOperation.Write);
                throw new BlackboardException($"Failed to set value for key '{key}'", ex);
            }
        }

        /// <summary>
        /// Get a value from the blackboard;
        /// </summary>
        public async Task<BlackboardResult<T>> GetValueAsync<T>(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("BlackboardGetValue");

            try
            {
                var startTime = DateTime.UtcNow;

                // Check access control;
                if (!await CheckAccessControlAsync(key, BlackboardOperation.Read))
                {
                    _logger.LogWarning("Access denied for key: {Key}", key);
                    return BlackboardResult<T>.AccessDenied();
                }

                // Try to get the entry
                if (!_storage.TryGetValue(key, out var entry))
                {
                    Interlocked.Increment(ref _statistics.MissCount);
                    _logger.LogDebug("Key not found: {Key}", key);
                    return BlackboardResult<T>.NotFound();
                }

                // Check if entry is expired;
                if (entry.Options?.ExpirationTime.HasValue == true &&
                    entry.Options.ExpirationTime.Value < DateTime.UtcNow)
                {
                    _logger.LogDebug("Entry expired: {Key}", key);

                    // Remove expired entry
                    _storage.TryRemove(key, out _);
                    Interlocked.Increment(ref _statistics.ExpiredCount);

                    return BlackboardResult<T>.Expired();
                }

                // Check type compatibility;
                if (entry.ValueType != typeof(T) &&
                    !typeof(T).IsAssignableFrom(entry.ValueType))
                {
                    _logger.LogWarning("Type mismatch for key: {Key}. Expected: {ExpectedType}, Actual: {ActualType}",
                        key, typeof(T), entry.ValueType);
                    return BlackboardResult<T>.TypeMismatch();
                }

                // Update access statistics;
                entry.AccessCount++;
                entry.LastAccessed = DateTime.UtcNow;

                var value = (T)entry.Value;
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Update statistics;
                Interlocked.Increment(ref _statistics.GetOperations);
                Interlocked.Increment(ref _statistics.HitCount);
                _performanceMetrics.RecordOperation(BlackboardOperation.Read, duration);

                _logger.LogTrace("Get value for key: {Key} in {Duration}ms", key, duration.TotalMilliseconds);

                return BlackboardResult<T>.Success(value, entry.Version, entry.LastModified);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting value for key: {Key}", key);
                _performanceMetrics.RecordError(BlackboardOperation.Read);
                return BlackboardResult<T>.Error(ex.Message);
            }
        }

        /// <summary>
        /// Remove a value from the blackboard;
        /// </summary>
        public async Task<bool> RemoveValueAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("BlackboardRemoveValue");

            try
            {
                // Check access control;
                if (!await CheckAccessControlAsync(key, BlackboardOperation.Delete))
                {
                    _logger.LogWarning("Access denied for key: {Key}", key);
                    return false;
                }

                // Check entry lock;
                if (await IsEntryLockedAsync(key))
                {
                    _logger.LogWarning("Entry is locked: {Key}", key);
                    return false;
                }

                var startTime = DateTime.UtcNow;

                // Remove the entry
                var removed = _storage.TryRemove(key, out var removedEntry);

                if (removed)
                {
                    var endTime = DateTime.UtcNow;
                    var duration = endTime - startTime;

                    // Update statistics;
                    Interlocked.Increment(ref _statistics.RemoveOperations);
                    _performanceMetrics.RecordOperation(BlackboardOperation.Delete, duration);

                    // Notify observers;
                    await NotifyObserversAsync(key, removedEntry, null, BlackboardChangeType.Removed);

                    // Publish event;
                    await _eventBus.PublishAsync(new BlackboardValueChangedEvent;
                    {
                        BlackboardId = _config.Id,
                        Key = key,
                        OldValue = removedEntry?.Value,
                        ChangeType = BlackboardChangeType.Removed,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Remove observers for this key;
                    _observers.TryRemove(key, out _);

                    // Remove entry lock if exists;
                    _entryLocks.TryRemove(key, out _);

                    _logger.LogDebug("Removed key: {Key} in {Duration}ms", key, duration.TotalMilliseconds);
                }
                else;
                {
                    _logger.LogDebug("Key not found for removal: {Key}", key);
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing key: {Key}", key);
                _performanceMetrics.RecordError(BlackboardOperation.Delete);
                return false;
            }
        }

        /// <summary>
        /// Check if blackboard contains a key;
        /// </summary>
        public async Task<bool> ContainsKeyAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            try
            {
                var exists = _storage.ContainsKey(key);

                if (exists)
                {
                    // Check if entry is expired;
                    if (_storage.TryGetValue(key, out var entry) &&
                        entry.Options?.ExpirationTime.HasValue == true &&
                        entry.Options.ExpirationTime.Value < DateTime.UtcNow)
                    {
                        // Auto-remove expired entry
                        _storage.TryRemove(key, out _);
                        Interlocked.Increment(ref _statistics.ExpiredCount);
                        return false;
                    }
                }

                Interlocked.Increment(ref _statistics.ContainsOperations);
                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking key existence: {Key}", key);
                return false;
            }
        }

        /// <summary>
        /// Clear all entries from the blackboard;
        /// </summary>
        public async Task ClearAsync()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("BlackboardClear");

            try
            {
                _logger.LogInformation("Clearing blackboard: {BlackboardId}", _config.Id);

                var startTime = DateTime.UtcNow;
                var entryCount = _storage.Count;

                // Clear all collections;
                _storage.Clear();
                _observers.Clear();
                _entryLocks.Clear();

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Update statistics;
                Interlocked.Increment(ref _statistics.ClearOperations);
                _performanceMetrics.RecordOperation(BlackboardOperation.Clear, duration);

                // Publish event;
                await _eventBus.PublishAsync(new BlackboardClearedEvent;
                {
                    BlackboardId = _config.Id,
                    ClearedEntryCount = entryCount,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Blackboard cleared. Removed {EntryCount} entries in {Duration}ms",
                    entryCount, duration.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing blackboard");
                throw new BlackboardException("Failed to clear blackboard", ex);
            }
        }

        /// <summary>
        /// Subscribe to changes for a specific key;
        /// </summary>
        public async Task<bool> SubscribeAsync(string key, IBlackboardObserver observer)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (observer == null)
                throw new ArgumentNullException(nameof(observer));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            try
            {
                var observers = _observers.GetOrAdd(key, _ => new List<IBlackboardObserver>());

                lock (observers)
                {
                    if (!observers.Contains(observer))
                    {
                        observers.Add(observer);

                        _logger.LogDebug("Observer subscribed to key: {Key}. Total observers: {ObserverCount}",
                            key, observers.Count);

                        return true;
                    }
                }

                _logger.LogDebug("Observer already subscribed to key: {Key}", key);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error subscribing observer to key: {Key}", key);
                return false;
            }
        }

        /// <summary>
        /// Unsubscribe from changes for a specific key;
        /// </summary>
        public async Task<bool> UnsubscribeAsync(string key, IBlackboardObserver observer)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (observer == null)
                throw new ArgumentNullException(nameof(observer));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            try
            {
                if (_observers.TryGetValue(key, out var observers))
                {
                    lock (observers)
                    {
                        var removed = observers.Remove(observer);

                        if (removed)
                        {
                            _logger.LogDebug("Observer unsubscribed from key: {Key}. Remaining observers: {ObserverCount}",
                                key, observers.Count);

                            // Remove key from observers dictionary if empty;
                            if (observers.Count == 0)
                            {
                                _observers.TryRemove(key, out _);
                            }
                        }

                        return removed;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unsubscribing observer from key: {Key}", key);
                return false;
            }
        }

        /// <summary>
        /// Get a snapshot of the current blackboard state;
        /// </summary>
        public async Task<BlackboardSnapshot> GetSnapshotAsync()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("BlackboardGetSnapshot");

            try
            {
                var startTime = DateTime.UtcNow;

                var snapshot = new BlackboardSnapshot;
                {
                    BlackboardId = _config.Id,
                    Timestamp = DateTime.UtcNow,
                    Entries = new Dictionary<string, BlackboardEntrySnapshot>(),
                    Statistics = _statistics.Clone(),
                    Configuration = _config.Clone()
                };

                // Create snapshots of all entries;
                foreach (var kvp in _storage)
                {
                    var entry = kvp.Value;

                    // Skip expired entries;
                    if (entry.Options?.ExpirationTime.HasValue == true &&
                        entry.Options.ExpirationTime.Value < DateTime.UtcNow)
                    {
                        continue;
                    }

                    snapshot.Entries[kvp.Key] = new BlackboardEntrySnapshot;
                    {
                        Key = entry.Key,
                        Value = entry.Value,
                        ValueType = entry.ValueType,
                        Version = entry.Version,
                        LastModified = entry.LastModified,
                        LastAccessed = entry.LastAccessed,
                        AccessCount = entry.AccessCount,
                        Options = entry.Options?.Clone()
                    };
                }

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                _logger.LogDebug("Created snapshot with {EntryCount} entries in {Duration}ms",
                    snapshot.Entries.Count, duration.TotalMilliseconds);

                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating blackboard snapshot");
                throw new BlackboardException("Failed to create blackboard snapshot", ex);
            }
        }

        /// <summary>
        /// Restore blackboard from a snapshot;
        /// </summary>
        public async Task<bool> RestoreFromSnapshotAsync(BlackboardSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            using var activity = _diagnosticTool.StartActivity("BlackboardRestoreFromSnapshot");

            try
            {
                _logger.LogInformation("Restoring blackboard from snapshot. Snapshot timestamp: {Timestamp}",
                    snapshot.Timestamp);

                var startTime = DateTime.UtcNow;

                // Clear current state;
                await ClearAsync();

                // Restore entries from snapshot;
                foreach (var kvp in snapshot.Entries)
                {
                    var entrySnapshot = kvp.Value;

                    var entry = new BlackboardEntry
                    {
                        Key = entrySnapshot.Key,
                        Value = entrySnapshot.Value,
                        ValueType = entrySnapshot.ValueType,
                        Options = entrySnapshot.Options,
                        Version = entrySnapshot.Version,
                        LastModified = entrySnapshot.LastModified,
                        LastAccessed = entrySnapshot.LastAccessed,
                        AccessCount = entrySnapshot.AccessCount;
                    };

                    _storage[entry.Key] = entry
                }

                // Update statistics;
                _statistics.RestoreFrom(snapshot.Statistics);

                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;

                // Publish event;
                await _eventBus.PublishAsync(new BlackboardRestoredEvent;
                {
                    BlackboardId = _config.Id,
                    SnapshotTimestamp = snapshot.Timestamp,
                    RestoredEntryCount = snapshot.Entries.Count,
                    RestoreDuration = duration,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Blackboard restored from snapshot. Restored {EntryCount} entries in {Duration}ms",
                    snapshot.Entries.Count, duration.TotalMilliseconds);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring blackboard from snapshot");
                throw new BlackboardException("Failed to restore blackboard from snapshot", ex);
            }
        }

        /// <summary>
        /// Get information about all entries;
        /// </summary>
        public async Task<IEnumerable<BlackboardEntryInfo>> GetEntriesAsync()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            try
            {
                var entries = new List<BlackboardEntryInfo>();

                foreach (var kvp in _storage)
                {
                    var entry = kvp.Value;

                    // Skip expired entries;
                    if (entry.Options?.ExpirationTime.HasValue == true &&
                        entry.Options.ExpirationTime.Value < DateTime.UtcNow)
                    {
                        continue;
                    }

                    entries.Add(new BlackboardEntryInfo;
                    {
                        Key = entry.Key,
                        ValueType = entry.ValueType?.Name ?? "Unknown",
                        Version = entry.Version,
                        LastModified = entry.LastModified,
                        LastAccessed = entry.LastAccessed,
                        AccessCount = entry.AccessCount,
                        IsLocked = _entryLocks.ContainsKey(entry.Key),
                        HasObservers = _observers.ContainsKey(entry.Key)
                    });
                }

                return entries;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting entries list");
                return Enumerable.Empty<BlackboardEntryInfo>();
            }
        }

        /// <summary>
        /// Get count of entries in blackboard;
        /// </summary>
        public async Task<int> GetEntryCountAsync()
        {
            if (!_isInitialized)
                return 0;

            try
            {
                // Count only non-expired entries;
                var count = _storage.Count(kvp =>
                    !(kvp.Value.Options?.ExpirationTime.HasValue == true &&
                      kvp.Value.Options.ExpirationTime.Value < DateTime.UtcNow));

                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting entry count");
                return 0;
            }
        }

        /// <summary>
        /// Lock an entry for exclusive access;
        /// </summary>
        public async Task<bool> LockEntryAsync(string key, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            try
            {
                var lockEntry = new EntryLock;
                {
                    Key = key,
                    LockedAt = DateTime.UtcNow,
                    Timeout = timeout,
                    LockId = Guid.NewGuid().ToString()
                };

                var added = _entryLocks.TryAdd(key, lockEntry);

                if (added)
                {
                    _logger.LogDebug("Entry locked: {Key} (LockId: {LockId})", key, lockEntry.LockId);

                    // Start timeout monitor;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(timeout);

                        if (_entryLocks.TryGetValue(key, out var existingLock) &&
                            existingLock.LockId == lockEntry.LockId)
                        {
                            _entryLocks.TryRemove(key, out _);
                            _logger.LogDebug("Entry lock expired: {Key}", key);
                        }
                    });
                }

                return added;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error locking entry: {Key}", key);
                return false;
            }
        }

        /// <summary>
        /// Unlock an entry
        /// </summary>
        public async Task<bool> UnlockEntryAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (!_isInitialized)
                throw new InvalidOperationException("Blackboard not initialized. Call InitializeAsync first.");

            try
            {
                var removed = _entryLocks.TryRemove(key, out var lockEntry);

                if (removed)
                {
                    _logger.LogDebug("Entry unlocked: {Key} (LockId: {LockId})", key, lockEntry?.LockId);
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unlocking entry: {Key}", key);
                return false;
            }
        }

        /// <summary>
        /// Get blackboard statistics;
        /// </summary>
        public async Task<BlackboardStats> GetStatisticsAsync()
        {
            if (!_isInitialized)
                return new BlackboardStats();

            try
            {
                var stats = new BlackboardStats;
                {
                    TotalEntries = await GetEntryCountAsync(),
                    Statistics = _statistics.Clone(),
                    PerformanceMetrics = _performanceMetrics.GetMetrics(),
                    Uptime = DateTime.UtcNow - _statistics.StartTime,
                    IsInitialized = _isInitialized;
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                return new BlackboardStats();
            }
        }

        /// <summary>
        /// Validate blackboard state and configuration;
        /// </summary>
        public async Task<bool> ValidateAsync()
        {
            if (!_isInitialized)
                return false;

            try
            {
                // Check storage integrity;
                var storageValid = await ValidateStorageIntegrityAsync();
                if (!storageValid)
                {
                    _logger.LogWarning("Storage integrity validation failed");
                    return false;
                }

                // Check configuration;
                var configValid = await ValidateConfigurationAsync();
                if (!configValid)
                {
                    _logger.LogWarning("Configuration validation failed");
                    return false;
                }

                // Check memory usage;
                var memoryValid = await ValidateMemoryUsageAsync();
                if (!memoryValid)
                {
                    _logger.LogWarning("Memory usage validation failed");
                    return false;
                }

                _logger.LogDebug("Blackboard validation passed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during blackboard validation");
                return false;
            }
        }

        /// <summary>
        /// Dispose blackboard resources;
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
                _shutdownCts.Cancel();

                _cleanupTimer?.Dispose();
                _statisticsTimer?.Dispose();
                _initializationLock?.Dispose();
                _shutdownCts?.Dispose();

                // Clear all data;
                _storage.Clear();
                _observers.Clear();
                _entryLocks.Clear();

                _logger.LogInformation("Blackboard disposed: {BlackboardId}", _config?.Id);
            }
        }

        #region Private Methods;

        private async Task InitializeDefaultEntriesAsync(IEnumerable<BlackboardDefaultEntry> defaultEntries)
        {
            foreach (var defaultEntry in defaultEntries)
            {
                try
                {
                    await SetValueAsync(
                        defaultEntry.Key,
                        defaultEntry.Value,
                        defaultEntry.Options);

                    _logger.LogDebug("Initialized default entry: {Key}", defaultEntry.Key);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error initializing default entry: {Key}", defaultEntry.Key);
                }
            }
        }

        private async Task SetupMessageBrokerIntegrationAsync()
        {
            try
            {
                await _messageBroker.SubscribeAsync<BlackboardMessage>("blackboard.updates",
                    async message =>
                    {
                        // Handle incoming blackboard messages;
                        await HandleIncomingBlackboardMessageAsync(message);
                    });

                _logger.LogInformation("Message broker integration setup completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting up message broker integration");
            }
        }

        private async Task PerformCleanupAsync()
        {
            try
            {
                var cleanupStart = DateTime.UtcNow;
                var expiredCount = 0;

                foreach (var kvp in _storage)
                {
                    var entry = kvp.Value;

                    // Check if entry is expired;
                    if (entry.Options?.ExpirationTime.HasValue == true &&
                        entry.Options.ExpirationTime.Value < DateTime.UtcNow)
                    {
                        if (_storage.TryRemove(kvp.Key, out _))
                        {
                            expiredCount++;

                            // Notify observers of expiration;
                            await NotifyObserversAsync(kvp.Key, entry, null, BlackboardChangeType.Expired);
                        }
                    }
                }

                if (expiredCount > 0)
                {
                    _logger.LogDebug("Cleanup completed. Removed {ExpiredCount} expired entries", expiredCount);

                    Interlocked.Add(ref _statistics.ExpiredCount, expiredCount);

                    await _eventBus.PublishAsync(new BlackboardCleanupCompletedEvent;
                    {
                        BlackboardId = _config.Id,
                        RemovedEntryCount = expiredCount,
                        CleanupDuration = DateTime.UtcNow - cleanupStart,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup");
            }
        }

        private async Task UpdateStatisticsAsync()
        {
            try
            {
                var currentStats = await GetStatisticsAsync();

                await _eventBus.PublishAsync(new BlackboardStatisticsUpdatedEvent;
                {
                    BlackboardId = _config.Id,
                    Statistics = currentStats,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogTrace("Statistics updated for blackboard: {BlackboardId}", _config.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating statistics");
            }
        }

        private async Task NotifyObserversAsync(string key, BlackboardEntry entry, object oldValue, BlackboardChangeType changeType)
        {
            if (_observers.TryGetValue(key, out var observers))
            {
                var notificationTasks = new List<Task>();

                foreach (var observer in observers.ToList()) // Create copy for thread safety;
                {
                    notificationTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await observer.OnBlackboardValueChangedAsync(new BlackboardChangeNotification;
                            {
                                Key = key,
                                NewValue = entry?.Value,
                                OldValue = oldValue,
                                ChangeType = changeType,
                                Version = entry?.Version ?? 0,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error notifying observer for key: {Key}", key);
                        }
                    }));
                }

                await Task.WhenAll(notificationTasks);
            }
        }

        private async Task<bool> CheckAccessControlAsync(string key, BlackboardOperation operation)
        {
            // In a real implementation, this would check user permissions, roles, etc.
            // For now, return true for all operations;

            await Task.CompletedTask;
            return true;
        }

        private async Task<bool> IsEntryLockedAsync(string key)
        {
            if (_entryLocks.TryGetValue(key, out var lockEntry))
            {
                // Check if lock is expired;
                if (DateTime.UtcNow - lockEntry.LockedAt > lockEntry.Timeout)
                {
                    _entryLocks.TryRemove(key, out _);
                    return false;
                }

                return true;
            }

            return false;
        }

        private bool IsValidKey(string key)
        {
            // Basic key validation;
            return !string.IsNullOrWhiteSpace(key) &&
                   key.Length <= _config?.MaxKeyLength &&
                   !key.Contains("..") &&
                   !key.StartsWith("_system_");
        }

        private string GetCurrentContext()
        {
            // In real implementation, get current user/agent context;
            return System.Threading.Thread.CurrentThread.ManagedThreadId.ToString();
        }

        private async Task<bool> ValidateStorageIntegrityAsync()
        {
            try
            {
                foreach (var kvp in _storage)
                {
                    var entry = kvp.Value;

                    // Check if key matches;
                    if (entry.Key != kvp.Key)
                    {
                        _logger.LogWarning("Key mismatch in storage: Expected={Expected}, Actual={Actual}",
                            kvp.Key, entry.Key);
                        return false;
                    }

                    // Check if value is not null (unless explicitly allowed)
                    if (entry.Value == null && !entry.Options?.AllowNullValue == true)
                    {
                        _logger.LogWarning("Null value found for key: {Key}", kvp.Key);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating storage integrity");
                return false;
            }
        }

        private async Task<bool> ValidateConfigurationAsync()
        {
            if (_config == null)
                return false;

            // Validate configuration values;
            if (_config.MaxKeyLength <= 0)
                return false;

            if (_config.MaxEntryCount <= 0)
                return false;

            if (_config.CleanupInterval <= TimeSpan.Zero)
                return false;

            return true;
        }

        private async Task<bool> ValidateMemoryUsageAsync()
        {
            try
            {
                var entryCount = await GetEntryCountAsync();

                // Check if we're approaching max entry count;
                if (_config.MaxEntryCount > 0 && entryCount > _config.MaxEntryCount * 0.9)
                {
                    _logger.LogWarning("Blackboard approaching max entry count: {Current}/{Max}",
                        entryCount, _config.MaxEntryCount);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating memory usage");
                return false;
            }
        }

        private async Task HandleIncomingBlackboardMessageAsync(BlackboardMessage message)
        {
            try
            {
                switch (message.Operation?.ToUpper())
                {
                    case "SET":
                        await SetValueAsync(message.Key, message.Value);
                        break;

                    case "REMOVE":
                        await RemoveValueAsync(message.Key);
                        break;

                    case "CLEAR":
                        await ClearAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling incoming blackboard message");
            }
        }

        #endregion;

        #region Supporting Classes and Enums;

        public enum BlackboardOperation;
        {
            Read,
            Write,
            Delete,
            Clear,
            Lock,
            Unlock;
        }

        public enum BlackboardChangeType;
        {
            Added,
            Updated,
            Removed,
            Expired,
            Locked,
            Unlocked;
        }

        public class BlackboardEntry
        {
            public string Key { get; set; }
            public object Value { get; set; }
            public Type ValueType { get; set; }
            public BlackboardEntryOptions Options { get; set; }
            public int Version { get; set; }
            public DateTime LastModified { get; set; }
            public DateTime? LastAccessed { get; set; }
            public string ModifiedBy { get; set; }
            public long AccessCount { get; set; }
        }

        public class BlackboardEntryOptions;
        {
            public TimeSpan? TimeToLive { get; set; }
            public DateTime? ExpirationTime { get; set; }
            public int? ExpectedVersion { get; set; }
            public bool CheckVersionConflict { get; set; }
            public bool ForceUpdate { get; set; }
            public bool AllowNullValue { get; set; }
            public AccessLevel RequiredAccessLevel { get; set; } = AccessLevel.ReadWrite;

            public BlackboardEntryOptions Clone()
            {
                return new BlackboardEntryOptions;
                {
                    TimeToLive = TimeToLive,
                    ExpirationTime = ExpirationTime,
                    ExpectedVersion = ExpectedVersion,
                    CheckVersionConflict = CheckVersionConflict,
                    ForceUpdate = ForceUpdate,
                    AllowNullValue = AllowNullValue,
                    RequiredAccessLevel = RequiredAccessLevel;
                };
            }
        }

        public class BlackboardResult<T>
        {
            public bool Success { get; private set; }
            public T Value { get; private set; }
            public int Version { get; private set; }
            public DateTime LastModified { get; private set; }
            public BlackboardError Error { get; private set; }

            public static BlackboardResult<T> SuccessResult(T value, int version, DateTime lastModified)
            {
                return new BlackboardResult<T>
                {
                    Success = true,
                    Value = value,
                    Version = version,
                    LastModified = lastModified,
                    Error = BlackboardError.None;
                };
            }

            public static BlackboardResult<T> NotFound()
            {
                return new BlackboardResult<T>
                {
                    Success = false,
                    Error = BlackboardError.KeyNotFound;
                };
            }

            public static BlackboardResult<T> Expired()
            {
                return new BlackboardResult<T>
                {
                    Success = false,
                    Error = BlackboardError.EntryExpired;
                };
            }

            public static BlackboardResult<T> TypeMismatch()
            {
                return new BlackboardResult<T>
                {
                    Success = false,
                    Error = BlackboardError.TypeMismatch;
                };
            }

            public static BlackboardResult<T> AccessDenied()
            {
                return new BlackboardResult<T>
                {
                    Success = false,
                    Error = BlackboardError.AccessDenied;
                };
            }

            public static BlackboardResult<T> Error(string message)
            {
                return new BlackboardResult<T>
                {
                    Success = false,
                    Error = new BlackboardError;
                    {
                        Code = BlackboardErrorCode.InternalError,
                        Message = message;
                    }
                };
            }

            public static BlackboardResult<T> Locked()
            {
                return new BlackboardResult<T>
                {
                    Success = false,
                    Error = BlackboardError.EntryLocked;
                };
            }
        }

        public class BlackboardStatistics;
        {
            public DateTime StartTime { get; } = DateTime.UtcNow;
            public long GetOperations;
            public long SetOperations;
            public long RemoveOperations;
            public long ClearOperations;
            public long ContainsOperations;
            public long HitCount;
            public long MissCount;
            public long ExpiredCount;

            public BlackboardStatistics Clone()
            {
                return new BlackboardStatistics;
                {
                    GetOperations = Interlocked.Read(ref GetOperations),
                    SetOperations = Interlocked.Read(ref SetOperations),
                    RemoveOperations = Interlocked.Read(ref RemoveOperations),
                    ClearOperations = Interlocked.Read(ref ClearOperations),
                    ContainsOperations = Interlocked.Read(ref ContainsOperations),
                    HitCount = Interlocked.Read(ref HitCount),
                    MissCount = Interlocked.Read(ref MissCount),
                    ExpiredCount = Interlocked.Read(ref ExpiredCount)
                };
            }

            public void RestoreFrom(BlackboardStatistics source)
            {
                Interlocked.Exchange(ref GetOperations, source.GetOperations);
                Interlocked.Exchange(ref SetOperations, source.SetOperations);
                Interlocked.Exchange(ref RemoveOperations, source.RemoveOperations);
                Interlocked.Exchange(ref ClearOperations, source.ClearOperations);
                Interlocked.Exchange(ref ContainsOperations, source.ContainsOperations);
                Interlocked.Exchange(ref HitCount, source.HitCount);
                Interlocked.Exchange(ref MissCount, source.MissCount);
                Interlocked.Exchange(ref ExpiredCount, source.ExpiredCount);
            }
        }

        public class PerformanceMetrics;
        {
            private readonly ConcurrentDictionary<BlackboardOperation, OperationMetrics> _metrics;

            public PerformanceMetrics()
            {
                _metrics = new ConcurrentDictionary<BlackboardOperation, OperationMetrics>();

                foreach (BlackboardOperation operation in Enum.GetValues(typeof(BlackboardOperation)))
                {
                    _metrics[operation] = new OperationMetrics();
                }
            }

            public void RecordOperation(BlackboardOperation operation, TimeSpan duration)
            {
                var metrics = _metrics[operation];
                Interlocked.Increment(ref metrics.Count);
                Interlocked.Add(ref metrics.TotalDurationTicks, duration.Ticks);

                // Update min/max;
                var currentMin = Interlocked.Read(ref metrics.MinDurationTicks);
                while (duration.Ticks < currentMin || currentMin == 0)
                {
                    var original = Interlocked.CompareExchange(ref metrics.MinDurationTicks, duration.Ticks, currentMin);
                    if (original == currentMin) break;
                    currentMin = original;
                }

                var currentMax = Interlocked.Read(ref metrics.MaxDurationTicks);
                while (duration.Ticks > currentMax)
                {
                    var original = Interlocked.CompareExchange(ref metrics.MaxDurationTicks, duration.Ticks, currentMax);
                    if (original == currentMax) break;
                    currentMax = original;
                }
            }

            public void RecordError(BlackboardOperation operation)
            {
                var metrics = _metrics[operation];
                Interlocked.Increment(ref metrics.ErrorCount);
            }

            public Dictionary<BlackboardOperation, OperationMetrics> GetMetrics()
            {
                return _metrics.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone());
            }

            public class OperationMetrics;
            {
                public long Count;
                public long ErrorCount;
                public long TotalDurationTicks;
                public long MinDurationTicks;
                public long MaxDurationTicks;

                public TimeSpan AverageDuration => Count > 0 ?
                    TimeSpan.FromTicks(TotalDurationTicks / Count) : TimeSpan.Zero;

                public TimeSpan MinDuration => TimeSpan.FromTicks(MinDurationTicks);
                public TimeSpan MaxDuration => TimeSpan.FromTicks(MaxDurationTicks);

                public OperationMetrics Clone()
                {
                    return new OperationMetrics;
                    {
                        Count = Interlocked.Read(ref Count),
                        ErrorCount = Interlocked.Read(ref ErrorCount),
                        TotalDurationTicks = Interlocked.Read(ref TotalDurationTicks),
                        MinDurationTicks = Interlocked.Read(ref MinDurationTicks),
                        MaxDurationTicks = Interlocked.Read(ref MaxDurationTicks)
                    };
                }
            }
        }

        public class EntryLock;
        {
            public string Key { get; set; }
            public string LockId { get; set; }
            public DateTime LockedAt { get; set; }
            public TimeSpan Timeout { get; set; }
        }

        // Additional supporting classes...
        public class BlackboardConfig { }
        public class BlackboardDefaultEntry { }
        public class BlackboardSnapshot { }
        public class BlackboardEntrySnapshot { }
        public class BlackboardStats { }
        public class BlackboardEntryInfo { }
        public class BlackboardError { }
        public class BlackboardMessage { }
        public class BlackboardChangeNotification { }

        public enum AccessLevel { ReadOnly, ReadWrite, Admin }
        public enum BlackboardErrorCode { None, KeyNotFound, TypeMismatch, AccessDenied, EntryLocked, EntryExpired, InternalError }

        #endregion;

        #region Events;

        public class BlackboardInitializedEvent : IEvent;
        {
            public string BlackboardId { get; set; }
            public string Name { get; set; }
            public int DefaultEntryCount { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BlackboardValueChangedEvent : IEvent;
        {
            public string BlackboardId { get; set; }
            public string Key { get; set; }
            public object NewValue { get; set; }
            public object OldValue { get; set; }
            public int Version { get; set; }
            public BlackboardChangeType ChangeType { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BlackboardClearedEvent : IEvent;
        {
            public string BlackboardId { get; set; }
            public int ClearedEntryCount { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BlackboardRestoredEvent : IEvent;
        {
            public string BlackboardId { get; set; }
            public DateTime SnapshotTimestamp { get; set; }
            public int RestoredEntryCount { get; set; }
            public TimeSpan RestoreDuration { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BlackboardCleanupCompletedEvent : IEvent;
        {
            public string BlackboardId { get; set; }
            public int RemovedEntryCount { get; set; }
            public TimeSpan CleanupDuration { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        public class BlackboardStatisticsUpdatedEvent : IEvent;
        {
            public string BlackboardId { get; set; }
            public BlackboardStats Statistics { get; set; }
            public DateTime Timestamp { get; set; }
            public string EventId { get; set; } = Guid.NewGuid().ToString();
        }

        #endregion;
    }

    #region Interfaces;

    public interface IBlackboardObserver;
    {
        Task OnBlackboardValueChangedAsync(BlackboardChangeNotification notification);
    }

    #endregion;

    #region Exceptions;

    public class BlackboardException : Exception
    {
        public BlackboardException() { }
        public BlackboardException(string message) : base(message) { }
        public BlackboardException(string message, Exception inner) : base(message, inner) { }
    }

    public class BlackboardVersionConflictException : BlackboardException;
    {
        public BlackboardVersionConflictException() { }
        public BlackboardVersionConflictException(string message) : base(message) { }
        public BlackboardVersionConflictException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;
}
