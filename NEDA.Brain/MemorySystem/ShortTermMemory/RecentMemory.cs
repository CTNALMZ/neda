using Microsoft.Extensions.Logging;
using NEDA.AI.KnowledgeBase.LongTermMemory;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.AI.KnowledgeBase.LongTermMemory.LongTermMemory;

namespace NEDA.Brain.MemorySystem.ShortTermMemory;
{
    /// <summary>
    /// Recent Memory Manager - Manages short-term memory with temporal decay and; 
    /// intelligent retention policies for recently accessed information;
    /// </summary>
    public class RecentMemory : IShortTermMemory, IDisposable;
    {
        private readonly ILogger<RecentMemory> _logger;
        private readonly RecentMemoryConfig _config;

        // Primary storage for recent memories;
        private readonly ConcurrentDictionary<Guid, RecentMemoryItem> _memoryStore;

        // Temporal index for quick time-based queries;
        private readonly SortedDictionary<DateTime, List<Guid>> _temporalIndex;

        // Tag-based index for categorical access;
        private readonly ConcurrentDictionary<string, HashSet<Guid>> _tagIndex;

        // Context-based index;
        private readonly ConcurrentDictionary<string, HashSet<Guid>> _contextIndex;

        // Priority queue for memory retention decisions;
        private readonly PriorityQueue<MemoryEvictionCandidate> _evictionQueue;

        // Statistics and monitoring;
        private readonly RecentMemoryMetrics _metrics;

        // Cleanup timer and synchronization;
        private readonly Timer _cleanupTimer;
        private readonly ReaderWriterLockSlim _storeLock;
        private readonly CancellationTokenSource _disposalTokenSource;
        private bool _disposed = false;

        /// <summary>
        /// Gets the current count of items in recent memory;
        /// </summary>
        public int Count => _memoryStore.Count;

        /// <summary>
        /// Gets the maximum capacity of recent memory;
        /// </summary>
        public int Capacity => _config.MaxItems;

        /// <summary>
        /// Gets the memory usage percentage;
        /// </summary>
        public double UsagePercentage => (double)_memoryStore.Count / _config.MaxItems * 100;

        /// <summary>
        /// Initializes a new instance of RecentMemory;
        /// </summary>
        public RecentMemory(
            ILogger<RecentMemory> logger,
            RecentMemoryConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? RecentMemoryConfig.Default;

            _memoryStore = new ConcurrentDictionary<Guid, RecentMemoryItem>();
            _temporalIndex = new SortedDictionary<DateTime, List<Guid>>();
            _tagIndex = new ConcurrentDictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            _contextIndex = new ConcurrentDictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            _evictionQueue = new PriorityQueue<MemoryEvictionCandidate>(_config.MaxItems);
            _metrics = new RecentMemoryMetrics();

            _storeLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            _disposalTokenSource = new CancellationTokenSource();

            // Initialize cleanup timer;
            _cleanupTimer = new Timer(
                CleanupExpiredMemories,
                null,
                _config.CleanupInterval,
                _config.CleanupInterval);

            _logger.LogInformation("RecentMemory initialized with capacity: {Capacity}, Cleanup: {CleanupInterval}",
                _config.MaxItems, _config.CleanupInterval);
        }

        /// <summary>
        /// Stores a new item in recent memory;
        /// </summary>
        public async Task<MemoryStorageResult> StoreAsync(MemoryItem item, StorageContext context = null)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.Store"))
            {
                _storeLock.EnterWriteLock();
                try
                {
                    _metrics.IncrementStoreAttempts();

                    if (_memoryStore.Count >= _config.MaxItems)
                    {
                        await PerformEvictionAsync();
                    }

                    var recentItem = new RecentMemoryItem(item)
                    {
                        LastAccessed = DateTime.UtcNow,
                        AccessCount = 1,
                        StorageTime = DateTime.UtcNow,
                        Context = context?.ContextType ?? "default",
                        Importance = CalculateInitialImportance(item, context)
                    };

                    // Apply decay configuration;
                    ApplyDecayConfiguration(recentItem);

                    // Store in primary dictionary;
                    if (!_memoryStore.TryAdd(item.Id, recentItem))
                    {
                        _metrics.IncrementStoreFailures();
                        _logger.LogWarning("Failed to store memory item {Id} - already exists", item.Id);
                        return MemoryStorageResult.Failure($"Memory item {item.Id} already exists");
                    }

                    // Update indexes;
                    UpdateTemporalIndex(item.Id, recentItem.StorageTime);
                    UpdateTagIndex(item.Id, item.Tags);
                    UpdateContextIndex(item.Id, recentItem.Context);

                    // Add to eviction queue;
                    var evictionScore = CalculateEvictionScore(recentItem);
                    _evictionQueue.Enqueue(new MemoryEvictionCandidate(item.Id, evictionScore));

                    _metrics.IncrementStoreSuccess();
                    _metrics.UpdateMemoryUsage(_memoryStore.Count, _config.MaxItems);

                    _logger.LogDebug("Stored memory item {Id} in recent memory. Current count: {Count}",
                        item.Id, _memoryStore.Count);

                    return MemoryStorageResult.Success(item.Id, MemoryStorageType.ShortTerm);
                }
                catch (Exception ex)
                {
                    _metrics.IncrementStoreErrors();
                    _logger.LogError(ex, "Error storing memory item {Id}", item.Id);
                    throw new MemoryStorageException($"Failed to store memory item {item.Id}", ex);
                }
                finally
                {
                    _storeLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Retrieves a memory item by its ID;
        /// </summary>
        public async Task<MemoryRetrievalResult> RetrieveAsync(Guid memoryId)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.Retrieve"))
            {
                _storeLock.EnterUpgradeableReadLock();
                try
                {
                    _metrics.IncrementRetrievalAttempts();

                    if (!_memoryStore.TryGetValue(memoryId, out var recentItem))
                    {
                        _metrics.IncrementRetrievalMisses();
                        _logger.LogDebug("Memory item {Id} not found in recent memory", memoryId);
                        return MemoryRetrievalResult.NotFound($"Memory item {memoryId} not found in recent memory");
                    }

                    // Update access statistics;
                    _storeLock.EnterWriteLock();
                    try
                    {
                        UpdateAccessStatistics(recentItem);
                        UpdateTemporalIndex(memoryId, DateTime.UtcNow);

                        // Recalculate eviction score after access;
                        var newEvictionScore = CalculateEvictionScore(recentItem);
                        _evictionQueue.UpdatePriority(memoryId.ToString(), newEvictionScore);
                    }
                    finally
                    {
                        _storeLock.ExitWriteLock();
                    }

                    _metrics.IncrementRetrievalSuccess();
                    _logger.LogDebug("Retrieved memory item {Id} from recent memory", memoryId);

                    return MemoryRetrievalResult.Success(recentItem.ToMemoryItem());
                }
                catch (Exception ex)
                {
                    _metrics.IncrementRetrievalErrors();
                    _logger.LogError(ex, "Error retrieving memory item {Id}", memoryId);
                    throw new MemoryRetrievalException($"Failed to retrieve memory item {memoryId}", ex);
                }
                finally
                {
                    _storeLock.ExitUpgradeableReadLock();
                }
            }
        }

        /// <summary>
        /// Updates an existing memory item;
        /// </summary>
        public async Task<MemoryUpdateResult> UpdateAsync(Guid memoryId, MemoryItem updatedItem)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.Update"))
            {
                _storeLock.EnterWriteLock();
                try
                {
                    _metrics.IncrementUpdateAttempts();

                    if (!_memoryStore.TryGetValue(memoryId, out var existingItem))
                    {
                        _metrics.IncrementUpdateFailures();
                        return MemoryUpdateResult.NotFound($"Memory item {memoryId} not found");
                    }

                    // Remove old indexes;
                    RemoveFromTagIndex(memoryId, existingItem.Tags);
                    RemoveFromContextIndex(memoryId, existingItem.Context);

                    // Update the item;
                    existingItem.UpdateFrom(updatedItem);
                    existingItem.LastModified = DateTime.UtcNow;
                    existingItem.Importance = RecalculateImportance(existingItem);

                    // Update indexes with new data;
                    UpdateTagIndex(memoryId, updatedItem.Tags);
                    UpdateContextIndex(memoryId, existingItem.Context);

                    // Update eviction score;
                    var newEvictionScore = CalculateEvictionScore(existingItem);
                    _evictionQueue.UpdatePriority(memoryId.ToString(), newEvictionScore);

                    _metrics.IncrementUpdateSuccess();
                    _logger.LogDebug("Updated memory item {Id} in recent memory", memoryId);

                    return MemoryUpdateResult.Success(memoryId);
                }
                catch (Exception ex)
                {
                    _metrics.IncrementUpdateErrors();
                    _logger.LogError(ex, "Error updating memory item {Id}", memoryId);
                    throw new MemoryUpdateException($"Failed to update memory item {memoryId}", ex);
                }
                finally
                {
                    _storeLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Removes a memory item;
        /// </summary>
        public async Task<MemoryRemovalResult> RemoveAsync(Guid memoryId)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.Remove"))
            {
                _storeLock.EnterWriteLock();
                try
                {
                    _metrics.IncrementRemovalAttempts();

                    if (!_memoryStore.TryRemove(memoryId, out var removedItem))
                    {
                        _metrics.IncrementRemovalFailures();
                        return MemoryRemovalResult.NotFound($"Memory item {memoryId} not found");
                    }

                    // Clean up indexes;
                    RemoveFromTemporalIndex(memoryId, removedItem.StorageTime);
                    RemoveFromTagIndex(memoryId, removedItem.Tags);
                    RemoveFromContextIndex(memoryId, removedItem.Context);

                    // Remove from eviction queue;
                    _evictionQueue.Remove(memoryId.ToString());

                    _metrics.IncrementRemovalSuccess();
                    _metrics.UpdateMemoryUsage(_memoryStore.Count, _config.MaxItems);

                    _logger.LogDebug("Removed memory item {Id} from recent memory", memoryId);

                    return MemoryRemovalResult.Success(memoryId);
                }
                catch (Exception ex)
                {
                    _metrics.IncrementRemovalErrors();
                    _logger.LogError(ex, "Error removing memory item {Id}", memoryId);
                    throw new MemoryRemovalException($"Failed to remove memory item {memoryId}", ex);
                }
                finally
                {
                    _storeLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Retrieves all memory items matching the specified query;
        /// </summary>
        public async Task<IEnumerable<MemoryItem>> QueryAsync(MemoryQuery query)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.Query"))
            {
                _storeLock.EnterReadLock();
                try
                {
                    _metrics.IncrementQueryAttempts();

                    var results = new List<MemoryItem>();
                    var candidateIds = new HashSet<Guid>();

                    // Apply query filters;
                    if (query.Tags != null && query.Tags.Any())
                    {
                        foreach (var tag in query.Tags)
                        {
                            if (_tagIndex.TryGetValue(tag, out var taggedIds))
                            {
                                candidateIds.UnionWith(taggedIds);
                            }
                        }
                    }
                    else;
                    {
                        // If no tags specified, start with all IDs;
                        candidateIds.UnionWith(_memoryStore.Keys);
                    }

                    // Filter by context;
                    if (!string.IsNullOrEmpty(query.Context))
                    {
                        if (_contextIndex.TryGetValue(query.Context, out var contextIds))
                        {
                            candidateIds.IntersectWith(contextIds);
                        }
                        else;
                        {
                            candidateIds.Clear();
                        }
                    }

                    // Filter by time range;
                    if (query.StartTime.HasValue || query.EndTime.HasValue)
                    {
                        var timeFilteredIds = GetIdsInTimeRange(query.StartTime, query.EndTime);
                        candidateIds.IntersectWith(timeFilteredIds);
                    }

                    // Apply importance threshold;
                    if (query.MinImportance > 0)
                    {
                        candidateIds.RemoveWhere(id =>
                        {
                            if (_memoryStore.TryGetValue(id, out var item))
                            {
                                return item.Importance < query.MinImportance;
                            }
                            return true;
                        });
                    }

                    // Convert to memory items;
                    foreach (var id in candidateIds)
                    {
                        if (_memoryStore.TryGetValue(id, out var item))
                        {
                            results.Add(item.ToMemoryItem());
                        }
                    }

                    // Apply sorting;
                    results = ApplySorting(results, query.SortBy, query.SortDescending).ToList();

                    // Apply pagination;
                    if (query.Skip > 0 || query.Take > 0)
                    {
                        results = results.Skip(query.Skip).Take(query.Take).ToList();
                    }

                    _metrics.IncrementQuerySuccess();
                    _logger.LogDebug("Query returned {Count} results from recent memory", results.Count);

                    return results;
                }
                catch (Exception ex)
                {
                    _metrics.IncrementQueryErrors();
                    _logger.LogError(ex, "Error querying recent memory");
                    throw new MemoryQueryException("Failed to query recent memory", ex);
                }
                finally
                {
                    _storeLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Gets all memory items in recent memory;
        /// </summary>
        public async Task<IEnumerable<MemoryItem>> GetAllAsync()
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.GetAll"))
            {
                _storeLock.EnterReadLock();
                try
                {
                    return _memoryStore.Values;
                        .Select(item => item.ToMemoryItem())
                        .ToList();
                }
                finally
                {
                    _storeLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Clears all items from recent memory;
        /// </summary>
        public async Task ClearAsync()
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.Clear"))
            {
                _storeLock.EnterWriteLock();
                try
                {
                    _memoryStore.Clear();
                    _temporalIndex.Clear();
                    _tagIndex.Clear();
                    _contextIndex.Clear();
                    _evictionQueue.Clear();

                    _metrics.Reset();
                    _metrics.UpdateMemoryUsage(0, _config.MaxItems);

                    _logger.LogInformation("Recent memory cleared");
                }
                finally
                {
                    _storeLock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Gets the most recently accessed items;
        /// </summary>
        public async Task<IEnumerable<MemoryItem>> GetRecentItemsAsync(int count)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.GetRecentItems"))
            {
                _storeLock.EnterReadLock();
                try
                {
                    return _memoryStore.Values;
                        .OrderByDescending(item => item.LastAccessed)
                        .Take(count)
                        .Select(item => item.ToMemoryItem())
                        .ToList();
                }
                finally
                {
                    _storeLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Gets frequently accessed items;
        /// </summary>
        public async Task<IEnumerable<MemoryItem>> GetFrequentItemsAsync(int count)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.GetFrequentItems"))
            {
                _storeLock.EnterReadLock();
                try
                {
                    return _memoryStore.Values;
                        .OrderByDescending(item => item.AccessCount)
                        .Take(count)
                        .Select(item => item.ToMemoryItem())
                        .ToList();
                }
                finally
                {
                    _storeLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Gets important items based on calculated importance score;
        /// </summary>
        public async Task<IEnumerable<MemoryItem>> GetImportantItemsAsync(int count)
        {
            using (var activity = Diagnostics.ActivitySource.StartActivity("RecentMemory.GetImportantItems"))
            {
                _storeLock.EnterReadLock();
                try
                {
                    return _memoryStore.Values;
                        .OrderByDescending(item => item.Importance)
                        .Take(count)
                        .Select(item => item.ToMemoryItem())
                        .ToList();
                }
                finally
                {
                    _storeLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Gets memory usage statistics;
        /// </summary>
        public RecentMemoryMetrics GetMetrics() => _metrics.Clone();

        /// <summary>
        /// Gets detailed diagnostics information;
        /// </summary>
        public RecentMemoryDiagnostics GetDiagnostics()
        {
            _storeLock.EnterReadLock();
            try
            {
                return new RecentMemoryDiagnostics;
                {
                    TotalItems = _memoryStore.Count,
                    Capacity = _config.MaxItems,
                    UsagePercentage = UsagePercentage,
                    TagIndexSize = _tagIndex.Sum(kv => kv.Value.Count),
                    ContextIndexSize = _contextIndex.Sum(kv => kv.Value.Count),
                    TemporalIndexSize = _temporalIndex.Sum(kv => kv.Value.Count),
                    EvictionQueueSize = _evictionQueue.Count,
                    OldestItem = _memoryStore.Values.OrderBy(v => v.StorageTime).FirstOrDefault()?.StorageTime,
                    NewestItem = _memoryStore.Values.OrderByDescending(v => v.StorageTime).FirstOrDefault()?.StorageTime,
                    AverageImportance = _memoryStore.Values.Average(v => v.Importance),
                    Metrics = _metrics.Clone()
                };
            }
            finally
            {
                _storeLock.ExitReadLock();
            }
        }

        #region Private Methods;

        private void CleanupExpiredMemories(object state)
        {
            if (_disposed) return;

            try
            {
                _storeLock.EnterWriteLock();
                try
                {
                    var now = DateTime.UtcNow;
                    var expiredIds = new List<Guid>();

                    // Find expired memories based on TTL;
                    foreach (var kvp in _memoryStore)
                    {
                        var item = kvp.Value;
                        var age = now - item.LastAccessed;

                        if (age > item.TimeToLive ||
                           (item.MaxIdleTime.HasValue && age > item.MaxIdleTime.Value))
                        {
                            expiredIds.Add(kvp.Key);
                        }
                    }

                    // Remove expired items;
                    foreach (var id in expiredIds)
                    {
                        if (_memoryStore.TryRemove(id, out var removedItem))
                        {
                            RemoveFromTemporalIndex(id, removedItem.StorageTime);
                            RemoveFromTagIndex(id, removedItem.Tags);
                            RemoveFromContextIndex(id, removedItem.Context);
                            _evictionQueue.Remove(id.ToString());

                            _metrics.IncrementExpiredItems();
                            _logger.LogDebug("Cleaned up expired memory item {Id}", id);
                        }
                    }

                    if (expiredIds.Count > 0)
                    {
                        _logger.LogInformation("Cleanup removed {Count} expired memory items", expiredIds.Count);
                    }
                }
                finally
                {
                    _storeLock.ExitWriteLock();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during recent memory cleanup");
            }
        }

        private async Task PerformEvictionAsync()
        {
            _logger.LogDebug("Performing memory eviction. Current count: {Count}", _memoryStore.Count);

            var itemsToEvict = Math.Max(1, (int)(_config.MaxItems * _config.EvictionPercentage));
            var evictedCount = 0;

            while (_memoryStore.Count > _config.MaxItems - itemsToEvict && _evictionQueue.Count > 0)
            {
                var candidate = _evictionQueue.Dequeue();
                if (candidate != null && _memoryStore.TryRemove(candidate.MemoryId, out var evictedItem))
                {
                    RemoveFromTemporalIndex(candidate.MemoryId, evictedItem.StorageTime);
                    RemoveFromTagIndex(candidate.MemoryId, evictedItem.Tags);
                    RemoveFromContextIndex(candidate.MemoryId, evictedItem.Context);

                    evictedCount++;
                    _metrics.IncrementEvictedItems();

                    _logger.LogDebug("Evicted memory item {Id} with score {Score}",
                        candidate.MemoryId, candidate.EvictionScore);
                }
            }

            if (evictedCount > 0)
            {
                _logger.LogInformation("Evicted {Count} items from recent memory", evictedCount);
            }
        }

        private double CalculateInitialImportance(MemoryItem item, StorageContext context)
        {
            double importance = 0.5; // Base importance;

            // Content-based factors;
            if (!string.IsNullOrEmpty(item.Content))
            {
                var contentLength = item.Content.Length;
                importance += Math.Min(contentLength / 1000.0, 0.2); // Length bonus;
            }

            // Tag-based factors;
            if (item.Tags != null)
            {
                importance += (item.Tags.Length * 0.05);

                // Priority tags;
                var priorityTags = new[] { "important", "critical", "urgent", "remember" };
                importance += item.Tags.Count(tag =>
                    priorityTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) * 0.1;
            }

            // Context-based factors;
            if (context != null)
            {
                switch (context.Priority)
                {
                    case StoragePriority.Critical:
                        importance += 0.3;
                        break;
                    case StoragePriority.High:
                        importance += 0.2;
                        break;
                    case StoragePriority.Medium:
                        importance += 0.1;
                        break;
                }

                if (context.IsUserInitiated)
                {
                    importance += 0.15;
                }
            }

            // Emotional weight factor;
            if (item.EmotionalWeight.HasValue)
            {
                importance += Math.Abs(item.EmotionalWeight.Value) * 0.1;
            }

            return Math.Min(Math.Max(importance, 0.0), 1.0);
        }

        private double RecalculateImportance(RecentMemoryItem item)
        {
            var baseImportance = item.BaseImportance;

            // Access-based adjustments;
            var accessBonus = Math.Log10(item.AccessCount + 1) * 0.1;

            // Recency penalty (older items lose importance)
            var ageInHours = (DateTime.UtcNow - item.StorageTime).TotalHours;
            var recencyPenalty = ageInHours * _config.ImportanceDecayRate;

            // Context persistence bonus;
            var contextBonus = item.Context == "session" ? 0.1 : 0.0;

            var newImportance = baseImportance + accessBonus - recencyPenalty + contextBonus;
            return Math.Min(Math.Max(newImportance, 0.0), 1.0);
        }

        private void ApplyDecayConfiguration(RecentMemoryItem item)
        {
            item.TimeToLive = _config.DefaultTimeToLive;
            item.DecayRate = _config.DecayRate;

            // Adjust based on importance;
            if (item.Importance > 0.8)
            {
                item.TimeToLive = item.TimeToLive.Add(TimeSpan.FromHours(2));
                item.DecayRate *= 0.8; // Slower decay for important items;
            }
            else if (item.Importance < 0.3)
            {
                item.TimeToLive = TimeSpan.FromHours(1);
                item.DecayRate *= 1.2; // Faster decay for unimportant items;
            }
        }

        private double CalculateEvictionScore(RecentMemoryItem item)
        {
            // Lower score = more likely to be evicted;
            double score = 0.0;

            // Importance factor (higher importance = higher score)
            score += item.Importance * 0.4;

            // Recency factor (more recent = higher score)
            var hoursSinceAccess = (DateTime.UtcNow - item.LastAccessed).TotalHours;
            var recencyFactor = Math.Exp(-hoursSinceAccess / 24.0); // 24-hour half-life;
            score += recencyFactor * 0.3;

            // Access frequency factor (more accesses = higher score)
            var frequencyFactor = Math.Log10(item.AccessCount + 1) / 3.0; // Log scale, max ~1;
            score += frequencyFactor * 0.2;

            // Context factor (session context = higher score)
            var contextFactor = item.Context == "session" ? 0.1 : 0.0;
            score += contextFactor;

            // Age penalty (older = lower score)
            var ageInHours = (DateTime.UtcNow - item.StorageTime).TotalHours;
            var agePenalty = ageInHours * 0.01;
            score -= agePenalty;

            return Math.Max(score, 0.0);
        }

        private void UpdateAccessStatistics(RecentMemoryItem item)
        {
            item.LastAccessed = DateTime.UtcNow;
            item.AccessCount++;
            item.Importance = RecalculateImportance(item);
        }

        private void UpdateTemporalIndex(Guid memoryId, DateTime timestamp)
        {
            var dateKey = timestamp.Date;

            if (!_temporalIndex.TryGetValue(dateKey, out var idList))
            {
                idList = new List<Guid>();
                _temporalIndex[dateKey] = idList;
            }

            if (!idList.Contains(memoryId))
            {
                idList.Add(memoryId);
            }
        }

        private void UpdateTagIndex(Guid memoryId, string[] tags)
        {
            if (tags == null) return;

            foreach (var tag in tags)
            {
                var tagSet = _tagIndex.GetOrAdd(tag, _ => new HashSet<Guid>());
                tagSet.Add(memoryId);
            }
        }

        private void UpdateContextIndex(Guid memoryId, string context)
        {
            if (string.IsNullOrEmpty(context)) return;

            var contextSet = _contextIndex.GetOrAdd(context, _ => new HashSet<Guid>());
            contextSet.Add(memoryId);
        }

        private void RemoveFromTemporalIndex(Guid memoryId, DateTime timestamp)
        {
            var dateKey = timestamp.Date;

            if (_temporalIndex.TryGetValue(dateKey, out var idList))
            {
                idList.Remove(memoryId);
                if (idList.Count == 0)
                {
                    _temporalIndex.Remove(dateKey);
                }
            }
        }

        private void RemoveFromTagIndex(Guid memoryId, string[] tags)
        {
            if (tags == null) return;

            foreach (var tag in tags)
            {
                if (_tagIndex.TryGetValue(tag, out var tagSet))
                {
                    tagSet.Remove(memoryId);
                    if (tagSet.Count == 0)
                    {
                        _tagIndex.TryRemove(tag, out _);
                    }
                }
            }
        }

        private void RemoveFromContextIndex(Guid memoryId, string context)
        {
            if (string.IsNullOrEmpty(context)) return;

            if (_contextIndex.TryGetValue(context, out var contextSet))
            {
                contextSet.Remove(memoryId);
                if (contextSet.Count == 0)
                {
                    _contextIndex.TryRemove(context, out _);
                }
            }
        }

        private HashSet<Guid> GetIdsInTimeRange(DateTime? startTime, DateTime? endTime)
        {
            var result = new HashSet<Guid>();

            foreach (var kvp in _temporalIndex)
            {
                if ((!startTime.HasValue || kvp.Key >= startTime.Value.Date) &&
                    (!endTime.HasValue || kvp.Key <= endTime.Value.Date))
                {
                    result.UnionWith(kvp.Value);
                }
            }

            return result;
        }

        private IEnumerable<MemoryItem> ApplySorting(
            List<MemoryItem> items,
            MemorySortBy sortBy,
            bool sortDescending)
        {
            return sortBy switch;
            {
                MemorySortBy.Recency => sortDescending;
                    ? items.OrderByDescending(i => i.Timestamp)
                    : items.OrderBy(i => i.Timestamp),

                MemorySortBy.Importance => sortDescending;
                    ? items.OrderByDescending(i => i.Importance)
                    : items.OrderBy(i => i.Importance),

                MemorySortBy.AccessCount => sortDescending;
                    ? items.OrderByDescending(i => i.AccessCount)
                    : items.OrderBy(i => i.AccessCount),

                MemorySortBy.Alphabetical => sortDescending;
                    ? items.OrderByDescending(i => i.Title)
                    : items.OrderBy(i => i.Title),

                _ => items;
            };
        }

        #endregion;

        #region IDisposable Support;
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _disposalTokenSource.Cancel();

                _cleanupTimer?.Dispose();
                _storeLock?.Dispose();
                _disposalTokenSource?.Dispose();

                _logger.LogInformation("RecentMemory disposed");
            }
        }
        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface IShortTermMemory;
    {
        Task<MemoryStorageResult> StoreAsync(MemoryItem item, StorageContext context = null);
        Task<MemoryRetrievalResult> RetrieveAsync(Guid memoryId);
        Task<MemoryUpdateResult> UpdateAsync(Guid memoryId, MemoryItem updatedItem);
        Task<MemoryRemovalResult> RemoveAsync(Guid memoryId);
        Task<IEnumerable<MemoryItem>> QueryAsync(MemoryQuery query);
        Task<IEnumerable<MemoryItem>> GetAllAsync();
        Task ClearAsync();
        Task<IEnumerable<MemoryItem>> GetRecentItemsAsync(int count);
        Task<IEnumerable<MemoryItem>> GetFrequentItemsAsync(int count);
        Task<IEnumerable<MemoryItem>> GetImportantItemsAsync(int count);
        RecentMemoryMetrics GetMetrics();
        RecentMemoryDiagnostics GetDiagnostics();
    }

    public class RecentMemoryConfig;
    {
        public static RecentMemoryConfig Default => new RecentMemoryConfig;
        {
            MaxItems = 1000,
            DefaultTimeToLive = TimeSpan.FromHours(6),
            CleanupInterval = TimeSpan.FromMinutes(5),
            EvictionPercentage = 0.1,
            DecayRate = 0.01,
            ImportanceDecayRate = 0.001,
            EnableIndexing = true;
        };

        public int MaxItems { get; set; }
        public TimeSpan DefaultTimeToLive { get; set; }
        public TimeSpan CleanupInterval { get; set; }
        public double EvictionPercentage { get; set; }
        public double DecayRate { get; set; }
        public double ImportanceDecayRate { get; set; }
        public bool EnableIndexing { get; set; }
    }

    public class RecentMemoryItem;
    {
        public Guid Id { get; }
        public string Title { get; private set; }
        public string Content { get; private set; }
        public string[] Tags { get; private set; }
        public double Importance { get; set; }
        public double BaseImportance { get; }
        public DateTime StorageTime { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime LastModified { get; set; }
        public int AccessCount { get; set; }
        public TimeSpan TimeToLive { get; set; }
        public TimeSpan? MaxIdleTime { get; set; }
        public double DecayRate { get; set; }
        public string Context { get; set; }
        public double? EmotionalWeight { get; private set; }
        public string Location { get; private set; }

        public RecentMemoryItem(MemoryItem source)
        {
            Id = source.Id;
            Title = source.Title;
            Content = source.Content;
            Tags = source.Tags;
            BaseImportance = source.Importance;
            Importance = source.Importance;
            EmotionalWeight = source.EmotionalWeight;
            Location = source.Location;
            StorageTime = source.Timestamp;
            LastAccessed = source.Timestamp;
            LastModified = source.Timestamp;
        }

        public void UpdateFrom(MemoryItem source)
        {
            Title = source.Title;
            Content = source.Content;
            Tags = source.Tags;
            EmotionalWeight = source.EmotionalWeight;
            Location = source.Location;
            LastModified = DateTime.UtcNow;
        }

        public MemoryItem ToMemoryItem()
        {
            return new MemoryItem;
            {
                Id = Id,
                Title = Title,
                Content = Content,
                Tags = Tags,
                Importance = Importance,
                EmotionalWeight = EmotionalWeight,
                Location = Location,
                Timestamp = StorageTime,
                AccessCount = AccessCount,
                StorageType = MemoryStorageType.ShortTerm;
            };
        }
    }

    public class MemoryEvictionCandidate : IComparable<MemoryEvictionCandidate>
    {
        public Guid MemoryId { get; }
        public double EvictionScore { get; }
        public DateTime Created { get; }

        public MemoryEvictionCandidate(Guid memoryId, double evictionScore)
        {
            MemoryId = memoryId;
            EvictionScore = evictionScore;
            Created = DateTime.UtcNow;
        }

        public int CompareTo(MemoryEvictionCandidate other)
        {
            // Lower score = higher priority for eviction;
            return EvictionScore.CompareTo(other.EvictionScore);
        }
    }

    public class RecentMemoryMetrics;
    {
        private long _storeAttempts;
        private long _storeSuccess;
        private long _storeFailures;
        private long _storeErrors;
        private long _retrievalAttempts;
        private long _retrievalSuccess;
        private long _retrievalMisses;
        private long _retrievalErrors;
        private long _updateAttempts;
        private long _updateSuccess;
        private long _updateFailures;
        private long _updateErrors;
        private long _removalAttempts;
        private long _removalSuccess;
        private long _removalFailures;
        private long _removalErrors;
        private long _queryAttempts;
        private long _querySuccess;
        private long _queryErrors;
        private long _evictedItems;
        private long _expiredItems;
        private long _currentItems;
        private long _maxItems;

        public long StoreAttempts => Interlocked.Read(ref _storeAttempts);
        public long StoreSuccess => Interlocked.Read(ref _storeSuccess);
        public long StoreFailures => Interlocked.Read(ref _storeFailures);
        public long StoreErrors => Interlocked.Read(ref _storeErrors);
        public long RetrievalAttempts => Interlocked.Read(ref _retrievalAttempts);
        public long RetrievalSuccess => Interlocked.Read(ref _retrievalSuccess);
        public long RetrievalMisses => Interlocked.Read(ref _retrievalMisses);
        public long RetrievalErrors => Interlocked.Read(ref _retrievalErrors);
        public long UpdateAttempts => Interlocked.Read(ref _updateAttempts);
        public long UpdateSuccess => Interlocked.Read(ref _updateSuccess);
        public long UpdateFailures => Interlocked.Read(ref _updateFailures);
        public long UpdateErrors => Interlocked.Read(ref _updateErrors);
        public long RemovalAttempts => Interlocked.Read(ref _removalAttempts);
        public long RemovalSuccess => Interlocked.Read(ref _removalSuccess);
        public long RemovalFailures => Interlocked.Read(ref _removalFailures);
        public long RemovalErrors => Interlocked.Read(ref _removalErrors);
        public long QueryAttempts => Interlocked.Read(ref _queryAttempts);
        public long QuerySuccess => Interlocked.Read(ref _querySuccess);
        public long QueryErrors => Interlocked.Read(ref _queryErrors);
        public long EvictedItems => Interlocked.Read(ref _evictedItems);
        public long ExpiredItems => Interlocked.Read(ref _expiredItems);
        public long CurrentItems => Interlocked.Read(ref _currentItems);
        public long MaxItems => Interlocked.Read(ref _maxItems);

        public double StoreSuccessRate => StoreAttempts > 0 ? (double)StoreSuccess / StoreAttempts : 0;
        public double RetrievalSuccessRate => RetrievalAttempts > 0 ? (double)RetrievalSuccess / RetrievalAttempts : 0;
        public double UpdateSuccessRate => UpdateAttempts > 0 ? (double)UpdateSuccess / UpdateAttempts : 0;
        public double RemovalSuccessRate => RemovalAttempts > 0 ? (double)RemovalSuccess / RemovalAttempts : 0;
        public double MemoryUsagePercentage => MaxItems > 0 ? (double)CurrentItems / MaxItems * 100 : 0;

        public void IncrementStoreAttempts() => Interlocked.Increment(ref _storeAttempts);
        public void IncrementStoreSuccess() => Interlocked.Increment(ref _storeSuccess);
        public void IncrementStoreFailures() => Interlocked.Increment(ref _storeFailures);
        public void IncrementStoreErrors() => Interlocked.Increment(ref _storeErrors);
        public void IncrementRetrievalAttempts() => Interlocked.Increment(ref _retrievalAttempts);
        public void IncrementRetrievalSuccess() => Interlocked.Increment(ref _retrievalSuccess);
        public void IncrementRetrievalMisses() => Interlocked.Increment(ref _retrievalMisses);
        public void IncrementRetrievalErrors() => Interlocked.Increment(ref _retrievalErrors);
        public void IncrementUpdateAttempts() => Interlocked.Increment(ref _updateAttempts);
        public void IncrementUpdateSuccess() => Interlocked.Increment(ref _updateSuccess);
        public void IncrementUpdateFailures() => Interlocked.Increment(ref _updateFailures);
        public void IncrementUpdateErrors() => Interlocked.Increment(ref _updateErrors);
        public void IncrementRemovalAttempts() => Interlocked.Increment(ref _removalAttempts);
        public void IncrementRemovalSuccess() => Interlocked.Increment(ref _removalSuccess);
        public void IncrementRemovalFailures() => Interlocked.Increment(ref _removalFailures);
        public void IncrementRemovalErrors() => Interlocked.Increment(ref _removalErrors);
        public void IncrementQueryAttempts() => Interlocked.Increment(ref _queryAttempts);
        public void IncrementQuerySuccess() => Interlocked.Increment(ref _querySuccess);
        public void IncrementQueryErrors() => Interlocked.Increment(ref _queryErrors);
        public void IncrementEvictedItems() => Interlocked.Increment(ref _evictedItems);
        public void IncrementExpiredItems() => Interlocked.Increment(ref _expiredItems);

        public void UpdateMemoryUsage(long currentItems, long maxItems)
        {
            Interlocked.Exchange(ref _currentItems, currentItems);
            Interlocked.Exchange(ref _maxItems, maxItems);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _storeAttempts, 0);
            Interlocked.Exchange(ref _storeSuccess, 0);
            Interlocked.Exchange(ref _storeFailures, 0);
            Interlocked.Exchange(ref _storeErrors, 0);
            Interlocked.Exchange(ref _retrievalAttempts, 0);
            Interlocked.Exchange(ref _retrievalSuccess, 0);
            Interlocked.Exchange(ref _retrievalMisses, 0);
            Interlocked.Exchange(ref _retrievalErrors, 0);
            Interlocked.Exchange(ref _updateAttempts, 0);
            Interlocked.Exchange(ref _updateSuccess, 0);
            Interlocked.Exchange(ref _updateFailures, 0);
            Interlocked.Exchange(ref _updateErrors, 0);
            Interlocked.Exchange(ref _removalAttempts, 0);
            Interlocked.Exchange(ref _removalSuccess, 0);
            Interlocked.Exchange(ref _removalFailures, 0);
            Interlocked.Exchange(ref _removalErrors, 0);
            Interlocked.Exchange(ref _queryAttempts, 0);
            Interlocked.Exchange(ref _querySuccess, 0);
            Interlocked.Exchange(ref _queryErrors, 0);
            Interlocked.Exchange(ref _evictedItems, 0);
            Interlocked.Exchange(ref _expiredItems, 0);
        }

        public RecentMemoryMetrics Clone()
        {
            return new RecentMemoryMetrics;
            {
                _storeAttempts = StoreAttempts,
                _storeSuccess = StoreSuccess,
                _storeFailures = StoreFailures,
                _storeErrors = StoreErrors,
                _retrievalAttempts = RetrievalAttempts,
                _retrievalSuccess = RetrievalSuccess,
                _retrievalMisses = RetrievalMisses,
                _retrievalErrors = RetrievalErrors,
                _updateAttempts = UpdateAttempts,
                _updateSuccess = UpdateSuccess,
                _updateFailures = UpdateFailures,
                _updateErrors = UpdateErrors,
                _removalAttempts = RemovalAttempts,
                _removalSuccess = RemovalSuccess,
                _removalFailures = RemovalFailures,
                _removalErrors = RemovalErrors,
                _queryAttempts = QueryAttempts,
                _querySuccess = QuerySuccess,
                _queryErrors = QueryErrors,
                _evictedItems = EvictedItems,
                _expiredItems = ExpiredItems,
                _currentItems = CurrentItems,
                _maxItems = MaxItems;
            };
        }
    }

    public class RecentMemoryDiagnostics;
    {
        public int TotalItems { get; set; }
        public int Capacity { get; set; }
        public double UsagePercentage { get; set; }
        public int TagIndexSize { get; set; }
        public int ContextIndexSize { get; set; }
        public int TemporalIndexSize { get; set; }
        public int EvictionQueueSize { get; set; }
        public DateTime? OldestItem { get; set; }
        public DateTime? NewestItem { get; set; }
        public double AverageImportance { get; set; }
        public RecentMemoryMetrics Metrics { get; set; }

        public override string ToString()
        {
            return $"RecentMemory: {TotalItems}/{Capacity} items ({UsagePercentage:F1}%), " +
                   $"Importance: {AverageImportance:F2}, " +
                   $"Store SR: {Metrics?.StoreSuccessRate:P1}, " +
                   $"Retrieval SR: {Metrics?.RetrievalSuccessRate:P1}";
        }
    }

    #endregion;
}
