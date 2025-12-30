using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.AI.KnowledgeBase;

namespace NEDA.AI.KnowledgeBase.LongTermMemory;
{
    /// <summary>
    /// Advanced Long-Term Memory system for storing, retrieving, and managing persistent knowledge;
    /// Implements hierarchical memory structure with semantic organization and intelligent recall;
    /// </summary>
    public class LongTermMemory : ILongTermMemory, IDisposable;
    {
        private readonly ILogger<LongTermMemory> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IMemoryCache _memoryCache;
        private readonly AppConfig _appConfig;
        private readonly MemoryStorageEngine _storageEngine;
        private readonly MemoryIndexEngine _indexEngine;
        private readonly MemoryRetrievalEngine _retrievalEngine;
        private readonly MemoryConsolidationEngine _consolidationEngine;
        private readonly MemoryAssociativeEngine _associativeEngine;
        private readonly MemoryPruningEngine _pruningEngine;
        private readonly SemaphoreSlim _accessLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _backgroundTasksCts = new CancellationTokenSource();
        private readonly Dictionary<string, MemoryAccessPattern> _accessPatterns;
        private MemoryMetrics _metrics;
        private bool _disposed;
        private bool _isInitialized;

        /// <summary>
        /// Event raised when new memory is stored;
        /// </summary>
        public event EventHandler<MemoryStoredEventArgs> MemoryStored;

        /// <summary>
        /// Event raised when memory is retrieved;
        /// </summary>
        public event EventHandler<MemoryRetrievedEventArgs> MemoryRetrieved;

        /// <summary>
        /// Event raised when memory is consolidated;
        /// </summary>
        public event EventHandler<MemoryConsolidatedEventArgs> MemoryConsolidated;

        /// <summary>
        /// Event raised when memory is pruned;
        /// </summary>
        public event EventHandler<MemoryPrunedEventArgs> MemoryPruned;

        /// <summary>
        /// Initializes a new instance of LongTermMemory;
        /// </summary>
        public LongTermMemory(
            ILogger<LongTermMemory> logger,
            IAuditLogger auditLogger,
            IServiceProvider serviceProvider,
            IMemoryCache memoryCache,
            AppConfig appConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            // Initialize sub-engines;
            _storageEngine = new MemoryStorageEngine(logger, appConfig);
            _indexEngine = new MemoryIndexEngine(logger);
            _retrievalEngine = new MemoryRetrievalEngine(logger, _indexEngine);
            _consolidationEngine = new MemoryConsolidationEngine(logger);
            _associativeEngine = new MemoryAssociativeEngine(logger);
            _pruningEngine = new MemoryPruningEngine(logger, appConfig);

            _accessPatterns = new Dictionary<string, MemoryAccessPattern>();
            _metrics = new MemoryMetrics();

            _logger.LogInformation("LongTermMemory initialized");
        }

        /// <summary>
        /// Initializes the memory system;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                _logger.LogWarning("LongTermMemory already initialized");
                return;
            }

            await _accessLock.WaitAsync();
            try
            {
                _logger.LogInformation("Initializing LongTermMemory system...");

                // Load existing memories;
                await _storageEngine.InitializeAsync();

                // Build indexes;
                await _indexEngine.BuildIndexesAsync(_storageEngine.GetAllMemories());

                // Initialize metrics;
                _metrics = await CalculateMetricsAsync();

                // Start background tasks;
                StartBackgroundTasks();

                _isInitialized = true;

                _logger.LogInformation(
                    "LongTermMemory initialized successfully. Loaded {MemoryCount} memories, {IndexCount} indexes",
                    _metrics.TotalMemories, _metrics.IndexCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize LongTermMemory");
                throw new MemorySystemException("Failed to initialize LongTermMemory",
                    ErrorCodes.MEMORY_INITIALIZATION_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Stores a new memory with semantic organization;
        /// </summary>
        /// <param name="memory">Memory to store</param>
        /// <param name="context">Storage context</param>
        /// <returns>Storage result</returns>
        public async Task<MemoryStorageResult> StoreAsync(MemoryItem memory, MemoryContext context)
        {
            Guard.ThrowIfNull(memory, nameof(memory));
            Guard.ThrowIfNull(context, nameof(context));

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            MemoryStorageResult result = null;

            try
            {
                _logger.LogDebug("Storing memory: {MemoryId} of type {MemoryType}",
                    memory.Id, memory.Type);

                // Validate memory;
                var validationResult = await ValidateMemoryAsync(memory);
                if (!validationResult.IsValid)
                {
                    throw new MemoryValidationException(
                        $"Memory validation failed: {string.Join(", ", validationResult.Errors)}",
                        memory.Id, validationResult.Errors);
                }

                // Enhance memory with metadata and context;
                var enhancedMemory = await EnhanceMemoryAsync(memory, context);

                // Store in persistent storage;
                var storageResult = await _storageEngine.StoreAsync(enhancedMemory);
                if (!storageResult.Success)
                {
                    throw new MemoryStorageException(
                        $"Failed to store memory: {storageResult.ErrorMessage}",
                        memory.Id, ErrorCodes.MEMORY_STORAGE_FAILED);
                }

                // Index the memory;
                var indexResult = await _indexEngine.IndexAsync(enhancedMemory);
                if (!indexResult.Success)
                {
                    // Rollback storage if indexing fails;
                    await _storageEngine.DeleteAsync(memory.Id);
                    throw new MemoryIndexException(
                        $"Failed to index memory: {indexResult.ErrorMessage}",
                        memory.Id, ErrorCodes.MEMORY_INDEXING_FAILED);
                }

                // Update cache;
                UpdateCache(enhancedMemory);

                // Update access patterns;
                UpdateAccessPattern(memory.Id, MemoryOperation.Store);

                // Create associations with existing memories;
                await CreateAssociationsAsync(enhancedMemory);

                // Trigger consolidation if needed;
                if (ShouldConsolidate())
                {
                    _ = Task.Run(() => ConsolidateMemoriesAsync());
                }

                // Build result;
                result = new MemoryStorageResult;
                {
                    Success = true,
                    MemoryId = memory.Id,
                    StorageId = storageResult.StorageId,
                    Indexed = true,
                    AssociationsCreated = enhancedMemory.Associations.Count,
                    StorageTime = DateTime.UtcNow - startTime;
                };

                // Raise event;
                OnMemoryStored(new MemoryStoredEventArgs;
                {
                    MemoryId = memory.Id,
                    MemoryType = memory.Type,
                    Timestamp = DateTime.UtcNow,
                    Context = context,
                    StorageResult = result;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "MemoryStored",
                    $"Stored memory '{memory.Id}' of type '{memory.Type}'",
                    new Dictionary<string, object>
                    {
                        ["memoryId"] = memory.Id,
                        ["memoryType"] = memory.Type.ToString(),
                        ["category"] = memory.Category.ToString(),
                        ["importance"] = memory.Importance,
                        ["storageTime"] = result.StorageTime.TotalMilliseconds,
                        ["associations"] = enhancedMemory.Associations.Count;
                    });

                // Update metrics;
                Interlocked.Increment(ref _metrics.TotalMemories);
                _metrics.LastStorageTime = DateTime.UtcNow;

                _logger.LogInformation(
                    "Memory stored successfully: {MemoryId} in {StorageTime}ms with {AssociationCount} associations",
                    memory.Id, result.StorageTime.TotalMilliseconds, enhancedMemory.Associations.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store memory: {MemoryId}", memory.Id);

                result = new MemoryStorageResult;
                {
                    Success = false,
                    MemoryId = memory.Id,
                    ErrorMessage = ex.Message,
                    ErrorCode = ex is MemorySystemException mse ? mse.ErrorCode : ErrorCodes.MEMORY_STORAGE_FAILED,
                    StorageTime = DateTime.UtcNow - startTime;
                };

                throw new MemoryStorageException($"Failed to store memory '{memory.Id}'",
                    memory.Id, ErrorCodes.MEMORY_STORAGE_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Retrieves memories based on query with semantic understanding;
        /// </summary>
        /// <param name="query">Retrieval query</param>
        /// <param name="options">Retrieval options</param>
        /// <returns>Retrieved memories</returns>
        public async Task<MemoryRetrievalResult> RetrieveAsync(MemoryQuery query, MemoryRetrievalOptions options = null)
        {
            Guard.ThrowIfNull(query, nameof(query));
            options ??= new MemoryRetrievalOptions();

            await EnsureInitializedAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogDebug("Retrieving memories with query: {QueryType}", query.Type);

            try
            {
                // Check cache first;
                if (options.UseCache)
                {
                    var cachedResult = await TryGetFromCacheAsync(query, options);
                    if (cachedResult != null)
                    {
                        _logger.LogDebug("Cache hit for query: {QueryType}", query.Type);
                        UpdateAccessPattern("cache", MemoryOperation.Retrieve);
                        return cachedResult;
                    }
                }

                // Execute retrieval based on query type;
                List<MemoryItem> memories;
                switch (query.Type)
                {
                    case MemoryQueryType.Semantic:
                        memories = await RetrieveSemanticAsync(query, options);
                        break;
                    case MemoryQueryType.Temporal:
                        memories = await RetrieveTemporalAsync(query, options);
                        break;
                    case MemoryQueryType.Associative:
                        memories = await RetrieveAssociativeAsync(query, options);
                        break;
                    case MemoryQueryType.Contextual:
                        memories = await RetrieveContextualAsync(query, options);
                        break;
                    case MemoryQueryType.Pattern:
                        memories = await RetrievePatternAsync(query, options);
                        break;
                    default:
                        memories = await RetrieveDefaultAsync(query, options);
                        break;
                }

                // Apply filters and sorting;
                memories = await ApplyFiltersAsync(memories, options);

                // Limit results;
                if (options.MaxResults > 0 && memories.Count > options.MaxResults)
                {
                    memories = memories.Take(options.MaxResults).ToList();
                }

                // Calculate relevance scores;
                var scoredMemories = await CalculateRelevanceScoresAsync(memories, query, options);

                // Update access patterns;
                UpdateAccessPattern(query.Type.ToString(), MemoryOperation.Retrieve);

                // Build result;
                var result = new MemoryRetrievalResult;
                {
                    Success = true,
                    Query = query,
                    Memories = scoredMemories,
                    TotalMatches = memories.Count,
                    RetrievedCount = scoredMemories.Count,
                    RetrievalTime = DateTime.UtcNow - startTime,
                    CacheHit = false;
                };

                // Cache the result if configured;
                if (options.CacheResult && options.UseCache)
                {
                    await CacheResultAsync(query, options, result);
                }

                // Raise event;
                OnMemoryRetrieved(new MemoryRetrievedEventArgs;
                {
                    Query = query,
                    Result = result,
                    Timestamp = DateTime.UtcNow;
                });

                // Update metrics;
                Interlocked.Increment(ref _metrics.TotalRetrievals);
                _metrics.LastRetrievalTime = DateTime.UtcNow;
                _metrics.AverageRetrievalTime = CalculateMovingAverage(
                    _metrics.AverageRetrievalTime, _metrics.TotalRetrievals, result.RetrievalTime.TotalMilliseconds);

                _logger.LogDebug(
                    "Retrieved {MemoryCount} memories in {RetrievalTime}ms",
                    result.RetrievedCount, result.RetrievalTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve memories with query: {QueryType}", query.Type);
                throw new MemoryRetrievalException(
                    $"Failed to retrieve memories: {ex.Message}",
                    query, ErrorCodes.MEMORY_RETRIEVAL_FAILED, ex);
            }
        }

        /// <summary>
        /// Updates an existing memory;
        /// </summary>
        /// <param name="memoryId">Memory identifier</param>
        /// <param name="updater">Update function</param>
        /// <param name="context">Update context</param>
        /// <returns>Update result</returns>
        public async Task<MemoryUpdateResult> UpdateAsync(string memoryId,
            Func<MemoryItem, Task<MemoryItem>> updater, MemoryContext context)
        {
            Guard.ThrowIfNullOrEmpty(memoryId, nameof(memoryId));
            Guard.ThrowIfNull(updater, nameof(updater));

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            MemoryUpdateResult result = null;

            try
            {
                _logger.LogDebug("Updating memory: {MemoryId}", memoryId);

                // Retrieve existing memory;
                var existingMemory = await _storageEngine.GetAsync(memoryId);
                if (existingMemory == null)
                {
                    throw new MemoryNotFoundException($"Memory '{memoryId}' not found", memoryId);
                }

                // Apply update;
                var updatedMemory = await updater(existingMemory);

                // Validate updated memory;
                var validationResult = await ValidateMemoryAsync(updatedMemory);
                if (!validationResult.IsValid)
                {
                    throw new MemoryValidationException(
                        $"Updated memory validation failed: {string.Join(", ", validationResult.Errors)}",
                        memoryId, validationResult.Errors);
                }

                // Preserve original metadata;
                updatedMemory.CreatedAt = existingMemory.CreatedAt;
                updatedMemory.UpdatedAt = DateTime.UtcNow;
                updatedMemory.Version = existingMemory.Version + 1;
                updatedMemory.UpdateHistory = existingMemory.UpdateHistory ?? new List<MemoryUpdateRecord>();
                updatedMemory.UpdateHistory.Add(new MemoryUpdateRecord;
                {
                    Timestamp = DateTime.UtcNow,
                    PreviousVersion = existingMemory.Version,
                    Context = context,
                    Changes = CalculateChanges(existingMemory, updatedMemory)
                });

                // Store updated memory;
                var storageResult = await _storageEngine.UpdateAsync(updatedMemory);
                if (!storageResult.Success)
                {
                    throw new MemoryStorageException(
                        $"Failed to update memory: {storageResult.ErrorMessage}",
                        memoryId, ErrorCodes.MEMORY_UPDATE_FAILED);
                }

                // Re-index updated memory;
                var indexResult = await _indexEngine.ReindexAsync(updatedMemory);
                if (!indexResult.Success)
                {
                    // Rollback update if reindexing fails;
                    await _storageEngine.UpdateAsync(existingMemory);
                    throw new MemoryIndexException(
                        $"Failed to reindex updated memory: {indexResult.ErrorMessage}",
                        memoryId, ErrorCodes.MEMORY_REINDEXING_FAILED);
                }

                // Update cache;
                UpdateCache(updatedMemory);

                // Update associations;
                await UpdateAssociationsAsync(existingMemory, updatedMemory);

                // Build result;
                result = new MemoryUpdateResult;
                {
                    Success = true,
                    MemoryId = memoryId,
                    OldVersion = existingMemory.Version,
                    NewVersion = updatedMemory.Version,
                    Changes = CalculateChanges(existingMemory, updatedMemory),
                    UpdateTime = DateTime.UtcNow - startTime;
                };

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "MemoryUpdated",
                    $"Updated memory '{memoryId}' from v{existingMemory.Version} to v{updatedMemory.Version}",
                    new Dictionary<string, object>
                    {
                        ["memoryId"] = memoryId,
                        ["oldVersion"] = existingMemory.Version,
                        ["newVersion"] = updatedMemory.Version,
                        ["changeCount"] = result.Changes.Count,
                        ["updateTime"] = result.UpdateTime.TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Memory updated successfully: {MemoryId} v{OldVersion} -> v{NewVersion}",
                    memoryId, existingMemory.Version, updatedMemory.Version);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update memory: {MemoryId}", memoryId);

                result = new MemoryUpdateResult;
                {
                    Success = false,
                    MemoryId = memoryId,
                    ErrorMessage = ex.Message,
                    ErrorCode = ex is MemorySystemException mse ? mse.ErrorCode : ErrorCodes.MEMORY_UPDATE_FAILED,
                    UpdateTime = DateTime.UtcNow - startTime;
                };

                throw new MemoryUpdateException($"Failed to update memory '{memoryId}'",
                    memoryId, ErrorCodes.MEMORY_UPDATE_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Consolidates related memories to improve organization and reduce redundancy;
        /// </summary>
        /// <param name="options">Consolidation options</param>
        /// <returns>Consolidation result</returns>
        public async Task<MemoryConsolidationResult> ConsolidateAsync(MemoryConsolidationOptions options = null)
        {
            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting memory consolidation...");

            try
            {
                options ??= new MemoryConsolidationOptions();

                // Get memories eligible for consolidation;
                var eligibleMemories = await GetMemoriesForConsolidationAsync(options);

                if (eligibleMemories.Count < 2)
                {
                    _logger.LogInformation("Not enough memories for consolidation: {Count}", eligibleMemories.Count);
                    return new MemoryConsolidationResult;
                    {
                        Success = true,
                        ConsolidatedCount = 0,
                        OriginalCount = 0,
                        ReductionPercentage = 0,
                        ConsolidationTime = DateTime.UtcNow - startTime,
                        Message = "Not enough memories for consolidation"
                    };
                }

                // Group memories by similarity;
                var groups = await GroupMemoriesBySimilarityAsync(eligibleMemories, options);

                var consolidationResults = new List<MemoryConsolidationGroup>();
                var consolidatedCount = 0;
                var originalCount = eligibleMemories.Count;

                // Consolidate each group;
                foreach (var group in groups)
                {
                    if (group.Memories.Count >= options.MinGroupSize)
                    {
                        var groupResult = await ConsolidateGroupAsync(group, options);
                        consolidationResults.Add(groupResult);
                        consolidatedCount += groupResult.ConsolidatedMemories.Count;
                    }
                }

                // Build result;
                var result = new MemoryConsolidationResult;
                {
                    Success = true,
                    OriginalCount = originalCount,
                    ConsolidatedCount = consolidatedCount,
                    ReductionPercentage = originalCount > 0 ?
                        (1 - (double)consolidatedCount / originalCount) * 100 : 0,
                    Groups = consolidationResults,
                    ConsolidationTime = DateTime.UtcNow - startTime;
                };

                // Raise event;
                OnMemoryConsolidated(new MemoryConsolidatedEventArgs;
                {
                    Result = result,
                    Timestamp = DateTime.UtcNow;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "MemoriesConsolidated",
                    $"Consolidated {originalCount} memories into {consolidatedCount} memories",
                    new Dictionary<string, object>
                    {
                        ["originalCount"] = originalCount,
                        ["consolidatedCount"] = consolidatedCount,
                        ["reductionPercentage"] = result.ReductionPercentage,
                        ["groupCount"] = consolidationResults.Count,
                        ["consolidationTime"] = result.ConsolidationTime.TotalMilliseconds;
                    });

                _logger.LogInformation(
                    "Memory consolidation completed: {Original} -> {Consolidated} ({Reduction}% reduction) in {Time}ms",
                    originalCount, consolidatedCount, result.ReductionPercentage,
                    result.ConsolidationTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory consolidation failed");
                throw new MemoryConsolidationException(
                    "Memory consolidation failed",
                    ErrorCodes.MEMORY_CONSOLIDATION_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Prunes obsolete, redundant, or low-importance memories;
        /// </summary>
        /// <param name="options">Pruning options</param>
        /// <returns>Pruning result</returns>
        public async Task<MemoryPruningResult> PruneAsync(MemoryPruningOptions options = null)
        {
            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting memory pruning...");

            try
            {
                options ??= new MemoryPruningOptions();

                // Get memories eligible for pruning;
                var eligibleMemories = await GetMemoriesForPruningAsync(options);

                if (!eligibleMemories.Any())
                {
                    _logger.LogInformation("No memories eligible for pruning");
                    return new MemoryPruningResult;
                    {
                        Success = true,
                        PrunedCount = 0,
                        TotalSizeFreed = 0,
                        PruningTime = DateTime.UtcNow - startTime,
                        Message = "No memories eligible for pruning"
                    };
                }

                var prunedMemories = new List<MemoryItem>();
                var totalSizeFreed = 0L;

                // Apply pruning strategies;
                foreach (var memory in eligibleMemories)
                {
                    var shouldPrune = await ShouldPruneMemoryAsync(memory, options);
                    if (shouldPrune)
                    {
                        // Delete memory;
                        await _storageEngine.DeleteAsync(memory.Id);
                        await _indexEngine.RemoveFromIndexAsync(memory.Id);

                        // Remove from cache;
                        RemoveFromCache(memory.Id);

                        prunedMemories.Add(memory);
                        totalSizeFreed += memory.SizeInBytes;

                        _logger.LogDebug("Pruned memory: {MemoryId}", memory.Id);
                    }
                }

                // Build result;
                var result = new MemoryPruningResult;
                {
                    Success = true,
                    PrunedCount = prunedMemories.Count,
                    PrunedMemories = prunedMemories,
                    TotalSizeFreed = totalSizeFreed,
                    PruningTime = DateTime.UtcNow - startTime;
                };

                // Raise event;
                OnMemoryPruned(new MemoryPrunedEventArgs;
                {
                    Result = result,
                    Timestamp = DateTime.UtcNow;
                });

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "MemoriesPruned",
                    $"Pruned {prunedMemories.Count} memories, freed {totalSizeFreed} bytes",
                    new Dictionary<string, object>
                    {
                        ["prunedCount"] = prunedMemories.Count,
                        ["sizeFreed"] = totalSizeFreed,
                        ["pruningTime"] = result.PruningTime.TotalMilliseconds,
                        ["pruningStrategy"] = options.Strategy.ToString()
                    });

                // Update metrics;
                Interlocked.Add(ref _metrics.TotalPrunedMemories, prunedMemories.Count);
                _metrics.TotalSizeFreed += totalSizeFreed;

                _logger.LogInformation(
                    "Memory pruning completed: {PrunedCount} memories pruned, {SizeFreed} bytes freed in {Time}ms",
                    prunedMemories.Count, totalSizeFreed, result.PruningTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory pruning failed");
                throw new MemoryPruningException(
                    "Memory pruning failed",
                    ErrorCodes.MEMORY_PRUNING_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Creates associations between memories;
        /// </summary>
        /// <param name="sourceMemoryId">Source memory identifier</param>
        /// <param name="targetMemoryId">Target memory identifier</param>
        /// <param name="associationType">Type of association</param>
        /// <param name="strength">Association strength</param>
        /// <returns>Association result</returns>
        public async Task<MemoryAssociationResult> CreateAssociationAsync(string sourceMemoryId,
            string targetMemoryId, AssociationType associationType, double strength = 1.0)
        {
            Guard.ThrowIfNullOrEmpty(sourceMemoryId, nameof(sourceMemoryId));
            Guard.ThrowIfNullOrEmpty(targetMemoryId, nameof(targetMemoryId));

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;

            try
            {
                // Get both memories;
                var sourceMemory = await _storageEngine.GetAsync(sourceMemoryId);
                var targetMemory = await _storageEngine.GetAsync(targetMemoryId);

                if (sourceMemory == null)
                    throw new MemoryNotFoundException($"Source memory '{sourceMemoryId}' not found", sourceMemoryId);
                if (targetMemory == null)
                    throw new MemoryNotFoundException($"Target memory '{targetMemoryId}' not found", targetMemoryId);

                // Create association;
                var association = new MemoryAssociation;
                {
                    SourceMemoryId = sourceMemoryId,
                    TargetMemoryId = targetMemoryId,
                    Type = associationType,
                    Strength = strength,
                    CreatedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow;
                };

                // Add association to both memories;
                sourceMemory.Associations.Add(association);
                targetMemory.Associations.Add(new MemoryAssociation;
                {
                    SourceMemoryId = targetMemoryId,
                    TargetMemoryId = sourceMemoryId,
                    Type = associationType,
                    Strength = strength,
                    CreatedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow;
                });

                // Update memories;
                await _storageEngine.UpdateAsync(sourceMemory);
                await _storageEngine.UpdateAsync(targetMemory);

                // Update indexes;
                await _indexEngine.UpdateAssociationsAsync(sourceMemory);
                await _indexEngine.UpdateAssociationsAsync(targetMemory);

                // Update cache;
                UpdateCache(sourceMemory);
                UpdateCache(targetMemory);

                // Build result;
                var result = new MemoryAssociationResult;
                {
                    Success = true,
                    SourceMemoryId = sourceMemoryId,
                    TargetMemoryId = targetMemoryId,
                    AssociationType = associationType,
                    Strength = strength,
                    AssociationTime = DateTime.UtcNow - startTime;
                };

                _logger.LogDebug(
                    "Created association: {SourceMemoryId} -> {TargetMemoryId} ({AssociationType}, Strength: {Strength})",
                    sourceMemoryId, targetMemoryId, associationType, strength);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create association: {Source} -> {Target}",
                    sourceMemoryId, targetMemoryId);
                throw new MemoryAssociationException(
                    $"Failed to create association between '{sourceMemoryId}' and '{targetMemoryId}'",
                    ErrorCodes.MEMORY_ASSOCIATION_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Gets memory metrics and statistics;
        /// </summary>
        /// <returns>Memory metrics</returns>
        public async Task<MemoryMetrics> GetMetricsAsync()
        {
            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            try
            {
                // Update metrics with current data;
                _metrics = await CalculateMetricsAsync();
                return _metrics;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Exports memories for backup or transfer;
        /// </summary>
        /// <param name="options">Export options</param>
        /// <returns>Exported memory package</returns>
        public async Task<MemoryExportPackage> ExportAsync(MemoryExportOptions options = null)
        {
            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting memory export...");

            try
            {
                options ??= new MemoryExportOptions();

                // Get memories to export;
                var memories = options.MemoryIds?.Any() == true;
                    ? await GetMemoriesByIdsAsync(options.MemoryIds)
                    : await _storageEngine.GetAllMemoriesAsync();

                // Apply filters;
                memories = await ApplyExportFiltersAsync(memories, options);

                // Create export package;
                var package = new MemoryExportPackage;
                {
                    PackageId = Guid.NewGuid().ToString(),
                    ExportTimestamp = DateTime.UtcNow,
                    ExportOptions = options,
                    Memories = memories,
                    Metadata = new ExportMetadata;
                    {
                        TotalMemories = memories.Count,
                        TotalSize = memories.Sum(m => m.SizeInBytes),
                        ExportFormat = options.Format,
                        CompressionLevel = options.CompressionLevel,
                        IncludeAssociations = options.IncludeAssociations,
                        IncludeMetadata = options.IncludeMetadata;
                    }
                };

                // Compress if requested;
                if (options.CompressionLevel > 0)
                {
                    package = await CompressPackageAsync(package, options.CompressionLevel);
                }

                // Encrypt if requested;
                if (!string.IsNullOrEmpty(options.EncryptionKey))
                {
                    package = await EncryptPackageAsync(package, options.EncryptionKey);
                }

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "MemoriesExported",
                    $"Exported {memories.Count} memories",
                    new Dictionary<string, object>
                    {
                        ["packageId"] = package.PackageId,
                        ["memoryCount"] = memories.Count,
                        ["totalSize"] = package.Metadata.TotalSize,
                        ["exportTime"] = (DateTime.UtcNow - startTime).TotalMilliseconds,
                        ["compressed"] = options.CompressionLevel > 0,
                        ["encrypted"] = !string.IsNullOrEmpty(options.EncryptionKey)
                    });

                _logger.LogInformation(
                    "Memory export completed: {MemoryCount} memories exported in {ExportTime}ms",
                    memories.Count, (DateTime.UtcNow - startTime).TotalMilliseconds);

                return package;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory export failed");
                throw new MemoryExportException(
                    "Memory export failed",
                    ErrorCodes.MEMORY_EXPORT_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Imports memories from a package;
        /// </summary>
        /// <param name="package">Memory package to import</param>
        /// <param name="options">Import options</param>
        /// <returns>Import result</returns>
        public async Task<MemoryImportResult> ImportAsync(MemoryExportPackage package,
            MemoryImportOptions options = null)
        {
            Guard.ThrowIfNull(package, nameof(package));

            await EnsureInitializedAsync();
            await _accessLock.WaitAsync();

            var startTime = DateTime.UtcNow;
            _logger.LogInformation("Starting memory import...");

            try
            {
                options ??= new MemoryImportOptions();

                // Decrypt if needed;
                if (!string.IsNullOrEmpty(options.DecryptionKey))
                {
                    package = await DecryptPackageAsync(package, options.DecryptionKey);
                }

                // Decompress if needed;
                if (package.Metadata.CompressionLevel > 0)
                {
                    package = await DecompressPackageAsync(package);
                }

                var importResults = new List<MemoryImportDetail>();
                var importedCount = 0;
                var skippedCount = 0;
                var failedCount = 0;

                foreach (var memory in package.Memories)
                {
                    try
                    {
                        // Check if memory already exists;
                        var existingMemory = await _storageEngine.GetAsync(memory.Id);

                        if (existingMemory != null)
                        {
                            switch (options.ConflictResolution)
                            {
                                case ImportConflictResolution.Skip:
                                    importResults.Add(new MemoryImportDetail;
                                    {
                                        MemoryId = memory.Id,
                                        Status = ImportStatus.Skipped,
                                        Reason = "Memory already exists",
                                        ExistingVersion = existingMemory.Version,
                                        ImportedVersion = memory.Version;
                                    });
                                    skippedCount++;
                                    continue;

                                case ImportConflictResolution.Overwrite:
                                    // Delete existing memory;
                                    await _storageEngine.DeleteAsync(memory.Id);
                                    await _indexEngine.RemoveFromIndexAsync(memory.Id);
                                    break;

                                case ImportConflictResolution.Merge:
                                    // Merge memories;
                                    memory = await MergeMemoriesAsync(existingMemory, memory);
                                    break;
                            }
                        }

                        // Import memory;
                        await StoreAsync(memory, new MemoryContext;
                        {
                            Source = "Import",
                            Operation = MemoryOperation.Import,
                            Metadata = new Dictionary<string, object>
                            {
                                ["importPackageId"] = package.PackageId,
                                ["importTimestamp"] = package.ExportTimestamp;
                            }
                        });

                        importResults.Add(new MemoryImportDetail;
                        {
                            MemoryId = memory.Id,
                            Status = ImportStatus.Imported,
                            Version = memory.Version,
                            Action = existingMemory != null ? "Overwritten" : "Added"
                        });
                        importedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to import memory: {MemoryId}", memory.Id);

                        importResults.Add(new MemoryImportDetail;
                        {
                            MemoryId = memory.Id,
                            Status = ImportStatus.Failed,
                            Reason = ex.Message,
                            Error = ex;
                        });
                        failedCount++;
                    }
                }

                // Build result;
                var result = new MemoryImportResult;
                {
                    Success = importedCount > 0 || skippedCount > 0,
                    PackageId = package.PackageId,
                    ImportedCount = importedCount,
                    SkippedCount = skippedCount,
                    FailedCount = failedCount,
                    ImportDetails = importResults,
                    ImportTime = DateTime.UtcNow - startTime;
                };

                // Audit log;
                await _auditLogger.LogSecurityEventAsync(
                    "MemoriesImported",
                    $"Imported {importedCount} memories, skipped {skippedCount}, failed {failedCount}",
                    new Dictionary<string, object>
                    {
                        ["packageId"] = package.PackageId,
                        ["importedCount"] = importedCount,
                        ["skippedCount"] = skippedCount,
                        ["failedCount"] = failedCount,
                        ["importTime"] = result.ImportTime.TotalMilliseconds,
                        ["conflictResolution"] = options.ConflictResolution.ToString()
                    });

                _logger.LogInformation(
                    "Memory import completed: {Imported} imported, {Skipped} skipped, {Failed} failed in {Time}ms",
                    importedCount, skippedCount, failedCount, result.ImportTime.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory import failed");
                throw new MemoryImportException(
                    "Memory import failed",
                    ErrorCodes.MEMORY_IMPORT_FAILED, ex);
            }
            finally
            {
                _accessLock.Release();
            }
        }

        #region Private Helper Methods;

        private async Task EnsureInitializedAsync()
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }
        }

        private async Task<MemoryItem> EnhanceMemoryAsync(MemoryItem memory, MemoryContext context)
        {
            var enhancedMemory = memory.Clone();

            // Add context metadata;
            enhancedMemory.Context = context;
            enhancedMemory.CreatedAt = DateTime.UtcNow;
            enhancedMemory.UpdatedAt = DateTime.UtcNow;
            enhancedMemory.Version = 1;
            enhancedMemory.AccessCount = 0;
            enhancedMemory.LastAccessed = DateTime.UtcNow;

            // Calculate importance if not provided;
            if (enhancedMemory.Importance <= 0)
            {
                enhancedMemory.Importance = await CalculateImportanceAsync(enhancedMemory, context);
            }

            // Generate embeddings for semantic search;
            enhancedMemory.Embeddings = await GenerateEmbeddingsAsync(enhancedMemory);

            // Extract key concepts;
            enhancedMemory.KeyConcepts = await ExtractKeyConceptsAsync(enhancedMemory);

            // Calculate size;
            enhancedMemory.SizeInBytes = CalculateMemorySize(enhancedMemory);

            return enhancedMemory;
        }

        private async Task<MemoryValidationResult> ValidateMemoryAsync(MemoryItem memory)
        {
            var result = new MemoryValidationResult;
            {
                MemoryId = memory.Id,
                ValidatedAt = DateTime.UtcNow;
            };

            // Required fields validation;
            if (string.IsNullOrEmpty(memory.Id))
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "MEMORY_ID_REQUIRED",
                    Message = "Memory ID is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            if (string.IsNullOrEmpty(memory.Content))
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "MEMORY_CONTENT_REQUIRED",
                    Message = "Memory content is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            if (memory.Type == MemoryType.Unknown)
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "MEMORY_TYPE_REQUIRED",
                    Message = "Memory type is required",
                    Severity = ValidationSeverity.Error;
                });
            }

            // Content size validation;
            if (memory.Content.Length > _appConfig.MemorySettings.MaxMemorySize)
            {
                result.Errors.Add(new ValidationError;
                {
                    Code = "MEMORY_SIZE_EXCEEDED",
                    Message = $"Memory size exceeds maximum allowed size of {_appConfig.MemorySettings.MaxMemorySize} characters",
                    Severity = ValidationSeverity.Error;
                });
            }

            // Semantic validation;
            var semanticValidation = await ValidateSemanticsAsync(memory);
            if (!semanticValidation.IsValid)
            {
                result.Errors.AddRange(semanticValidation.Errors);
            }

            result.IsValid = !result.Errors.Any(e => e.Severity == ValidationSeverity.Error);
            return result;
        }

        private async Task CreateAssociationsAsync(MemoryItem memory)
        {
            // Find similar existing memories;
            var similarMemories = await FindSimilarMemoriesAsync(memory);

            foreach (var similarMemory in similarMemories)
            {
                // Calculate association strength;
                var strength = await CalculateAssociationStrengthAsync(memory, similarMemory);

                if (strength >= _appConfig.MemorySettings.MinAssociationStrength)
                {
                    await CreateAssociationAsync(
                        memory.Id,
                        similarMemory.Id,
                        AssociationType.Semantic,
                        strength);
                }
            }

            // Create temporal associations with recent memories;
            var recentMemories = await GetRecentMemoriesAsync(10); // Last 10 memories;
            foreach (var recentMemory in recentMemories.Where(m => m.Id != memory.Id))
            {
                await CreateAssociationAsync(
                    memory.Id,
                    recentMemory.Id,
                    AssociationType.Temporal,
                    0.5); // Default temporal association strength;
            }
        }

        private async Task UpdateAssociationsAsync(MemoryItem oldMemory, MemoryItem newMemory)
        {
            // Remove old associations that are no longer relevant;
            var associationsToRemove = oldMemory.Associations;
                .Where(a => !IsAssociationStillRelevant(a, newMemory))
                .ToList();

            foreach (var association in associationsToRemove)
            {
                await RemoveAssociationAsync(association.SourceMemoryId, association.TargetMemoryId);
            }

            // Create new associations based on updated memory;
            await CreateAssociationsAsync(newMemory);
        }

        private async Task<List<MemoryItem>> RetrieveSemanticAsync(MemoryQuery query, MemoryRetrievalOptions options)
        {
            // Use embeddings for semantic search;
            var queryEmbedding = await GenerateEmbeddingForQueryAsync(query);
            return await _retrievalEngine.RetrieveBySimilarityAsync(queryEmbedding, options.MaxResults);
        }

        private async Task<List<MemoryItem>> RetrieveTemporalAsync(MemoryQuery query, MemoryRetrievalOptions options)
        {
            var timeRange = query.Parameters.GetValueOrDefault("timeRange") as TimeRange?;
            if (timeRange.HasValue)
            {
                return await _retrievalEngine.RetrieveByTimeRangeAsync(timeRange.Value, options.MaxResults);
            }

            // Default: retrieve recent memories;
            return await _retrievalEngine.RetrieveRecentAsync(options.MaxResults);
        }

        private async Task<List<MemoryItem>> RetrieveAssociativeAsync(MemoryQuery query, MemoryRetrievalOptions options)
        {
            var memoryId = query.Parameters.GetValueOrDefault("memoryId") as string;
            if (!string.IsNullOrEmpty(memoryId))
            {
                return await _retrievalEngine.RetrieveAssociationsAsync(memoryId, options.MaxResults);
            }

            return new List<MemoryItem>();
        }

        private async Task<List<MemoryItem>> RetrieveContextualAsync(MemoryQuery query, MemoryRetrievalOptions options)
        {
            var context = query.Parameters.GetValueOrDefault("context") as MemoryContext;
            if (context != null)
            {
                return await _retrievalEngine.RetrieveByContextAsync(context, options.MaxResults);
            }

            return new List<MemoryItem>();
        }

        private async Task<List<MemoryItem>> RetrievePatternAsync(MemoryQuery query, MemoryRetrievalOptions options)
        {
            var pattern = query.Parameters.GetValueOrDefault("pattern") as MemoryPattern;
            if (pattern != null)
            {
                return await _retrievalEngine.RetrieveByPatternAsync(pattern, options.MaxResults);
            }

            return new List<MemoryItem>();
        }

        private async Task<List<MemoryItem>> RetrieveDefaultAsync(MemoryQuery query, MemoryRetrievalOptions options)
        {
            // Default retrieval: combine multiple strategies;
            var results = new List<MemoryItem>();

            // Add keyword search results;
            if (query.Keywords?.Any() == true)
            {
                var keywordResults = await _retrievalEngine.RetrieveByKeywordsAsync(
                    query.Keywords, options.MaxResults / 2);
                results.AddRange(keywordResults);
            }

            // Add category-based results;
            if (query.Categories?.Any() == true)
            {
                var categoryResults = await _retrievalEngine.RetrieveByCategoriesAsync(
                    query.Categories, options.MaxResults / 2);
                results.AddRange(categoryResults);
            }

            // Remove duplicates;
            return results.DistinctBy(m => m.Id).ToList();
        }

        private async Task<List<ScoredMemory>> CalculateRelevanceScoresAsync(
            List<MemoryItem> memories, MemoryQuery query, MemoryRetrievalOptions options)
        {
            var scoredMemories = new List<ScoredMemory>();

            foreach (var memory in memories)
            {
                var score = await CalculateRelevanceScoreAsync(memory, query, options);

                scoredMemories.Add(new ScoredMemory;
                {
                    Memory = memory,
                    RelevanceScore = score,
                    Explanation = await GenerateRelevanceExplanationAsync(memory, query, score)
                });

                // Update access count and timestamp;
                memory.AccessCount++;
                memory.LastAccessed = DateTime.UtcNow;
                await _storageEngine.UpdateAccessInfoAsync(memory.Id, memory.AccessCount, memory.LastAccessed);
            }

            // Sort by relevance score;
            return scoredMemories;
                .OrderByDescending(sm => sm.RelevanceScore)
                .ToList();
        }

        private async Task<double> CalculateRelevanceScoreAsync(
            MemoryItem memory, MemoryQuery query, MemoryRetrievalOptions options)
        {
            double score = 0.0;
            double totalWeight = 0.0;

            // Semantic similarity score;
            if (options.UseSemanticScoring)
            {
                var semanticWeight = 0.4;
                var semanticScore = await CalculateSemanticSimilarityAsync(memory, query);
                score += semanticScore * semanticWeight;
                totalWeight += semanticWeight;
            }

            // Temporal relevance score;
            if (options.UseTemporalScoring)
            {
                var temporalWeight = 0.2;
                var temporalScore = CalculateTemporalRelevance(memory);
                score += temporalScore * temporalWeight;
                totalWeight += temporalWeight;
            }

            // Importance score;
            if (options.UseImportanceScoring)
            {
                var importanceWeight = 0.2;
                var importanceScore = memory.Importance / 10.0; // Normalize to 0-1;
                score += importanceScore * importanceWeight;
                totalWeight += importanceWeight;
            }

            // Access frequency score;
            if (options.UseFrequencyScoring)
            {
                var frequencyWeight = 0.1;
                var frequencyScore = CalculateAccessFrequencyScore(memory);
                score += frequencyScore * frequencyWeight;
                totalWeight += frequencyWeight;
            }

            // Context match score;
            if (options.UseContextScoring && query.Context != null)
            {
                var contextWeight = 0.1;
                var contextScore = await CalculateContextMatchScoreAsync(memory, query.Context);
                score += contextScore * contextWeight;
                totalWeight += contextWeight;
            }

            // Normalize score;
            return totalWeight > 0 ? score / totalWeight : 0;
        }

        private async Task<bool> ShouldPruneMemoryAsync(MemoryItem memory, MemoryPruningOptions options)
        {
            switch (options.Strategy)
            {
                case PruningStrategy.AgeBased:
                    return (DateTime.UtcNow - memory.LastAccessed).TotalDays > options.MaxAgeDays;

                case PruningStrategy.ImportanceBased:
                    return memory.Importance < options.MinImportance;

                case PruningStrategy.AccessBased:
                    return memory.AccessCount < options.MinAccessCount;

                case PruningStrategy.SizeBased:
                    return memory.SizeInBytes > options.MaxMemorySize;

                case PruningStrategy.RedundancyBased:
                    var similarMemories = await FindSimilarMemoriesAsync(memory);
                    return similarMemories.Count > options.MaxSimilarMemories;

                case PruningStrategy.Combined:
                    var ageScore = (DateTime.UtcNow - memory.LastAccessed).TotalDays / options.MaxAgeDays;
                    var importanceScore = 1 - (memory.Importance / 10.0);
                    var accessScore = 1 - ((double)memory.AccessCount / options.MinAccessCount);
                    var combinedScore = (ageScore + importanceScore + accessScore) / 3;
                    return combinedScore > options.PruningThreshold;

                default:
                    return false;
            }
        }

        private void UpdateCache(MemoryItem memory)
        {
            var cacheKey = $"memory_{memory.Id}";
            var cacheOptions = new MemoryCacheEntryOptions;
            {
                SlidingExpiration = TimeSpan.FromMinutes(_appConfig.MemorySettings.CacheDurationMinutes),
                Size = memory.SizeInBytes;
            };

            _memoryCache.Set(cacheKey, memory, cacheOptions);
        }

        private void RemoveFromCache(string memoryId)
        {
            var cacheKey = $"memory_{memoryId}";
            _memoryCache.Remove(cacheKey);
        }

        private async Task<MemoryRetrievalResult> TryGetFromCacheAsync(MemoryQuery query, MemoryRetrievalOptions options)
        {
            var cacheKey = GenerateCacheKey(query, options);
            if (_memoryCache.TryGetValue(cacheKey, out MemoryRetrievalResult cachedResult))
            {
                // Update access pattern for cache hit;
                UpdateAccessPattern("cache", MemoryOperation.Retrieve);
                return cachedResult;
            }

            return null;
        }

        private async Task CacheResultAsync(MemoryQuery query, MemoryRetrievalOptions options,
            MemoryRetrievalResult result)
        {
            var cacheKey = GenerateCacheKey(query, options);
            var cacheOptions = new MemoryCacheEntryOptions;
            {
                SlidingExpiration = TimeSpan.FromMinutes(_appConfig.MemorySettings.QueryCacheDurationMinutes),
                Size = result.Memories.Sum(m => m.Memory.SizeInBytes)
            };

            _memoryCache.Set(cacheKey, result, cacheOptions);
        }

        private string GenerateCacheKey(MemoryQuery query, MemoryRetrievalOptions options)
        {
            return $"query_{query.Type}_{query.GetHashCode()}_{options.GetHashCode()}";
        }

        private void UpdateAccessPattern(string key, MemoryOperation operation)
        {
            if (!_accessPatterns.ContainsKey(key))
            {
                _accessPatterns[key] = new MemoryAccessPattern;
                {
                    Key = key,
                    Operation = operation;
                };
            }

            var pattern = _accessPatterns[key];
            pattern.AccessCount++;
            pattern.LastAccessed = DateTime.UtcNow;
            pattern.AverageAccessInterval = CalculateMovingAverage(
                pattern.AverageAccessInterval, pattern.AccessCount,
                (DateTime.UtcNow - pattern.LastAccessed).TotalSeconds);
        }

        private bool ShouldConsolidate()
        {
            // Consolidate if memory count exceeds threshold or time since last consolidation is too long;
            var timeSinceLastConsolidation = DateTime.UtcNow - _metrics.LastConsolidationTime;
            return _metrics.TotalMemories > _appConfig.MemorySettings.ConsolidationThreshold ||
                   timeSinceLastConsolidation.TotalHours > _appConfig.MemorySettings.ConsolidationIntervalHours;
        }

        private async Task ConsolidateMemoriesAsync()
        {
            try
            {
                await ConsolidateAsync(new MemoryConsolidationOptions;
                {
                    Strategy = ConsolidationStrategy.Semantic,
                    MinSimilarity = 0.7,
                    MinGroupSize = 2;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background memory consolidation failed");
            }
        }

        private void StartBackgroundTasks()
        {
            // Start consolidation task;
            _ = Task.Run(async () =>
            {
                while (!_backgroundTasksCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromHours(
                            _appConfig.MemorySettings.ConsolidationIntervalHours),
                            _backgroundTasksCts.Token);

                        if (ShouldConsolidate())
                        {
                            await ConsolidateMemoriesAsync();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background consolidation task failed");
                    }
                }
            });

            // Start pruning task;
            _ = Task.Run(async () =>
            {
                while (!_backgroundTasksCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromDays(1), _backgroundTasksCts.Token);

                        // Check if pruning is needed;
                        if (_metrics.TotalMemories > _appConfig.MemorySettings.PruningThreshold)
                        {
                            await PruneAsync(new MemoryPruningOptions;
                            {
                                Strategy = PruningStrategy.Combined,
                                MaxAgeDays = 30,
                                MinImportance = 3,
                                MinAccessCount = 1,
                                PruningThreshold = 0.7;
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background pruning task failed");
                    }
                }
            });

            _logger.LogInformation("Background tasks started");
        }

        private async Task<MemoryMetrics> CalculateMetricsAsync()
        {
            var memories = await _storageEngine.GetAllMemoriesAsync();

            return new MemoryMetrics;
            {
                TotalMemories = memories.Count,
                TotalSizeBytes = memories.Sum(m => m.SizeInBytes),
                AverageMemorySize = memories.Average(m => m.SizeInBytes),
                MemoryTypeDistribution = memories.GroupBy(m => m.Type)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                CategoryDistribution = memories.GroupBy(m => m.Category)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                ImportanceDistribution = memories.GroupBy(m => Math.Round(m.Importance))
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                IndexCount = _indexEngine.GetIndexCount(),
                CacheHitRate = await CalculateCacheHitRateAsync(),
                AverageRetrievalTime = _metrics.AverageRetrievalTime,
                LastStorageTime = _metrics.LastStorageTime,
                LastRetrievalTime = _metrics.LastRetrievalTime,
                LastConsolidationTime = _metrics.LastConsolidationTime,
                TotalRetrievals = _metrics.TotalRetrievals,
                TotalPrunedMemories = _metrics.TotalPrunedMemories,
                TotalSizeFreed = _metrics.TotalSizeFreed,
                CalculatedAt = DateTime.UtcNow;
            };
        }

        private double CalculateMovingAverage(double currentAverage, long count, double newValue)
        {
            return (currentAverage * (count - 1) + newValue) / count;
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnMemoryStored(MemoryStoredEventArgs e)
        {
            MemoryStored?.Invoke(this, e);
        }

        protected virtual void OnMemoryRetrieved(MemoryRetrievedEventArgs e)
        {
            MemoryRetrieved?.Invoke(this, e);
        }

        protected virtual void OnMemoryConsolidated(MemoryConsolidatedEventArgs e)
        {
            MemoryConsolidated?.Invoke(this, e);
        }

        protected virtual void OnMemoryPruned(MemoryPrunedEventArgs e)
        {
            MemoryPruned?.Invoke(this, e);
        }

        #endregion;

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose implementation;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cancel background tasks;
                    _backgroundTasksCts?.Cancel();
                    _backgroundTasksCts?.Dispose();

                    // Release lock;
                    _accessLock?.Dispose();

                    // Dispose sub-engines;
                    _storageEngine?.Dispose();
                    _indexEngine?.Dispose();
                    _retrievalEngine?.Dispose();
                    _consolidationEngine?.Dispose();
                    _associativeEngine?.Dispose();
                    _pruningEngine?.Dispose();
                }
                _disposed = true;
            }
        }

        #region Supporting Classes;

        /// <summary>
        /// Long-term memory interface;
        /// </summary>
        public interface ILongTermMemory : IDisposable
        {
            Task InitializeAsync();
            Task<MemoryStorageResult> StoreAsync(MemoryItem memory, MemoryContext context);
            Task<MemoryRetrievalResult> RetrieveAsync(MemoryQuery query, MemoryRetrievalOptions options);
            Task<MemoryUpdateResult> UpdateAsync(string memoryId, Func<MemoryItem, Task<MemoryItem>> updater,
                MemoryContext context);
            Task<MemoryConsolidationResult> ConsolidateAsync(MemoryConsolidationOptions options);
            Task<MemoryPruningResult> PruneAsync(MemoryPruningOptions options);
            Task<MemoryAssociationResult> CreateAssociationAsync(string sourceMemoryId, string targetMemoryId,
                AssociationType associationType, double strength);
            Task<MemoryMetrics> GetMetricsAsync();
            Task<MemoryExportPackage> ExportAsync(MemoryExportOptions options);
            Task<MemoryImportResult> ImportAsync(MemoryExportPackage package, MemoryImportOptions options);

            event EventHandler<MemoryStoredEventArgs> MemoryStored;
            event EventHandler<MemoryRetrievedEventArgs> MemoryRetrieved;
            event EventHandler<MemoryConsolidatedEventArgs> MemoryConsolidated;
            event EventHandler<MemoryPrunedEventArgs> MemoryPruned;
        }

        /// <summary>
        /// Memory item representing a single long-term memory;
        /// </summary>
        public class MemoryItem : ICloneable;
        {
            public string Id { get; set; }
            public string Content { get; set; }
            public MemoryType Type { get; set; }
            public MemoryCategory Category { get; set; }
            public double Importance { get; set; } // 1-10 scale;
            public List<string> Tags { get; set; } = new List<string>();
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
            public List<MemoryAssociation> Associations { get; set; } = new List<MemoryAssociation>();
            public MemoryEmbedding Embeddings { get; set; }
            public List<string> KeyConcepts { get; set; } = new List<string>();
            public MemoryContext Context { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
            public DateTime LastAccessed { get; set; }
            public int AccessCount { get; set; }
            public int Version { get; set; }
            public List<MemoryUpdateRecord> UpdateHistory { get; set; } = new List<MemoryUpdateRecord>();
            public long SizeInBytes { get; set; }

            public object Clone()
            {
                return new MemoryItem;
                {
                    Id = Id,
                    Content = Content,
                    Type = Type,
                    Category = Category,
                    Importance = Importance,
                    Tags = new List<string>(Tags),
                    Metadata = new Dictionary<string, object>(Metadata),
                    Associations = Associations.Select(a => (MemoryAssociation)a.Clone()).ToList(),
                    Embeddings = (MemoryEmbedding)Embeddings?.Clone(),
                    KeyConcepts = new List<string>(KeyConcepts),
                    Context = (MemoryContext)Context?.Clone(),
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt,
                    LastAccessed = LastAccessed,
                    AccessCount = AccessCount,
                    Version = Version,
                    UpdateHistory = UpdateHistory.Select(r => (MemoryUpdateRecord)r.Clone()).ToList(),
                    SizeInBytes = SizeInBytes;
                };
            }
        }

        /// <summary>
        /// Memory query for retrieval;
        /// </summary>
        public class MemoryQuery;
        {
            public MemoryQueryType Type { get; set; }
            public List<string> Keywords { get; set; } = new List<string>();
            public List<MemoryCategory> Categories { get; set; } = new List<MemoryCategory>();
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public MemoryContext Context { get; set; }
        }

        /// <summary>
        /// Memory context for storage and retrieval;
        /// </summary>
        public class MemoryContext : ICloneable;
        {
            public string Source { get; set; }
            public MemoryOperation Operation { get; set; }
            public string UserId { get; set; }
            public string SessionId { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
            public DateTime Timestamp { get; set; } = DateTime.UtcNow;

            public object Clone()
            {
                return new MemoryContext;
                {
                    Source = Source,
                    Operation = Operation,
                    UserId = UserId,
                    SessionId = SessionId,
                    Metadata = new Dictionary<string, object>(Metadata),
                    Timestamp = Timestamp;
                };
            }
        }

        /// <summary>
        /// Memory association between memories;
        /// </summary>
        public class MemoryAssociation : ICloneable;
        {
            public string SourceMemoryId { get; set; }
            public string TargetMemoryId { get; set; }
            public AssociationType Type { get; set; }
            public double Strength { get; set; } // 0-1 scale;
            public DateTime CreatedAt { get; set; }
            public DateTime LastAccessed { get; set; }
            public int AccessCount { get; set; }

            public object Clone()
            {
                return new MemoryAssociation;
                {
                    SourceMemoryId = SourceMemoryId,
                    TargetMemoryId = TargetMemoryId,
                    Type = Type,
                    Strength = Strength,
                    CreatedAt = CreatedAt,
                    LastAccessed = LastAccessed,
                    AccessCount = AccessCount;
                };
            }
        }

        /// <summary>
        /// Memory embedding for semantic search;
        /// </summary>
        public class MemoryEmbedding : ICloneable;
        {
            public List<float> Vector { get; set; } = new List<float>();
            public string Model { get; set; }
            public DateTime GeneratedAt { get; set; }
            public int Dimension { get; set; }

            public object Clone()
            {
                return new MemoryEmbedding;
                {
                    Vector = new List<float>(Vector),
                    Model = Model,
                    GeneratedAt = GeneratedAt,
                    Dimension = Dimension;
                };
            }
        }

        /// <summary>
        /// Memory storage result;
        /// </summary>
        public class MemoryStorageResult;
        {
            public bool Success { get; set; }
            public string MemoryId { get; set; }
            public string StorageId { get; set; }
            public bool Indexed { get; set; }
            public int AssociationsCreated { get; set; }
            public TimeSpan StorageTime { get; set; }
            public string ErrorMessage { get; set; }
            public string ErrorCode { get; set; }
        }

        /// <summary>
        /// Memory retrieval result;
        /// </summary>
        public class MemoryRetrievalResult;
        {
            public bool Success { get; set; }
            public MemoryQuery Query { get; set; }
            public List<ScoredMemory> Memories { get; set; } = new List<ScoredMemory>();
            public int TotalMatches { get; set; }
            public int RetrievedCount { get; set; }
            public TimeSpan RetrievalTime { get; set; }
            public bool CacheHit { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Scored memory with relevance information;
        /// </summary>
        public class ScoredMemory;
        {
            public MemoryItem Memory { get; set; }
            public double RelevanceScore { get; set; }
            public string Explanation { get; set; }
            public Dictionary<string, double> ScoreBreakdown { get; set; } = new Dictionary<string, double>();
        }

        /// <summary>
        /// Memory update result;
        /// </summary>
        public class MemoryUpdateResult;
        {
            public bool Success { get; set; }
            public string MemoryId { get; set; }
            public int OldVersion { get; set; }
            public int NewVersion { get; set; }
            public List<MemoryChange> Changes { get; set; } = new List<MemoryChange>();
            public TimeSpan UpdateTime { get; set; }
            public string ErrorMessage { get; set; }
            public string ErrorCode { get; set; }
        }

        /// <summary>
        /// Memory consolidation result;
        /// </summary>
        public class MemoryConsolidationResult;
        {
            public bool Success { get; set; }
            public int OriginalCount { get; set; }
            public int ConsolidatedCount { get; set; }
            public double ReductionPercentage { get; set; }
            public List<MemoryConsolidationGroup> Groups { get; set; } = new List<MemoryConsolidationGroup>();
            public TimeSpan ConsolidationTime { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Memory pruning result;
        /// </summary>
        public class MemoryPruningResult;
        {
            public bool Success { get; set; }
            public int PrunedCount { get; set; }
            public List<MemoryItem> PrunedMemories { get; set; } = new List<MemoryItem>();
            public long TotalSizeFreed { get; set; }
            public TimeSpan PruningTime { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Memory association result;
        /// </summary>
        public class MemoryAssociationResult;
        {
            public bool Success { get; set; }
            public string SourceMemoryId { get; set; }
            public string TargetMemoryId { get; set; }
            public AssociationType AssociationType { get; set; }
            public double Strength { get; set; }
            public TimeSpan AssociationTime { get; set; }
        }

        /// <summary>
        /// Memory metrics and statistics;
        /// </summary>
        public class MemoryMetrics;
        {
            public int TotalMemories { get; set; }
            public long TotalSizeBytes { get; set; }
            public double AverageMemorySize { get; set; }
            public Dictionary<string, int> MemoryTypeDistribution { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> CategoryDistribution { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> ImportanceDistribution { get; set; } = new Dictionary<string, int>();
            public int IndexCount { get; set; }
            public double CacheHitRate { get; set; }
            public double AverageRetrievalTime { get; set; }
            public DateTime LastStorageTime { get; set; }
            public DateTime LastRetrievalTime { get; set; }
            public DateTime LastConsolidationTime { get; set; }
            public long TotalRetrievals { get; set; }
            public long TotalPrunedMemories { get; set; }
            public long TotalSizeFreed { get; set; }
            public DateTime CalculatedAt { get; set; }
        }

        /// <summary>
        /// Memory export package;
        /// </summary>
        public class MemoryExportPackage;
        {
            public string PackageId { get; set; }
            public DateTime ExportTimestamp { get; set; }
            public MemoryExportOptions ExportOptions { get; set; }
            public List<MemoryItem> Memories { get; set; } = new List<MemoryItem>();
            public ExportMetadata Metadata { get; set; }
        }

        /// <summary>
        /// Memory import result;
        /// </summary>
        public class MemoryImportResult;
        {
            public bool Success { get; set; }
            public string PackageId { get; set; }
            public int ImportedCount { get; set; }
            public int SkippedCount { get; set; }
            public int FailedCount { get; set; }
            public List<MemoryImportDetail> ImportDetails { get; set; } = new List<MemoryImportDetail>();
            public TimeSpan ImportTime { get; set; }
        }

        #endregion;

        #region Enums;

        /// <summary>
        /// Memory types;
        /// </summary>
        public enum MemoryType;
        {
            Unknown,
            Fact,
            Experience,
            Skill,
            Procedure,
            Concept,
            Relationship,
            Pattern,
            Event,
            Goal,
            Preference,
            Insight;
        }

        /// <summary>
        /// Memory categories;
        /// </summary>
        public enum MemoryCategory;
        {
            General,
            Personal,
            Professional,
            Technical,
            Creative,
            Social,
            Emotional,
            Strategic,
            Tactical,
            Historical;
        }

        /// <summary>
        /// Memory query types;
        /// </summary>
        public enum MemoryQueryType;
        {
            Semantic,
            Temporal,
            Associative,
            Contextual,
            Pattern,
            Keyword,
            Category,
            Hybrid;
        }

        /// <summary>
        /// Memory operations;
        /// </summary>
        public enum MemoryOperation;
        {
            Store,
            Retrieve,
            Update,
            Delete,
            Consolidate,
            Prune,
            Export,
            Import;
        }

        /// <summary>
        /// Association types;
        /// </summary>
        public enum AssociationType;
        {
            Semantic,
            Temporal,
            Contextual,
            Causal,
            Analogical,
            Complementary,
            Contrastive,
            Hierarchical;
        }

        /// <summary>
        /// Consolidation strategies;
        /// </summary>
        public enum ConsolidationStrategy;
        {
            Semantic,
            Temporal,
            Contextual,
            Hierarchical,
            Hybrid;
        }

        /// <summary>
        /// Pruning strategies;
        /// </summary>
        public enum PruningStrategy;
        {
            AgeBased,
            ImportanceBased,
            AccessBased,
            SizeBased,
            RedundancyBased,
            Combined;
        }

        /// <summary>
        /// Import conflict resolution strategies;
        /// </summary>
        public enum ImportConflictResolution;
        {
            Skip,
            Overwrite,
            Merge;
        }

        /// <summary>
        /// Import status;
        /// </summary>
        public enum ImportStatus;
        {
            Pending,
            Imported,
            Skipped,
            Failed;
        }

        #endregion;
    }

    #region Exceptions;

    /// <summary>
    /// Base exception for memory system errors;
    /// </summary>
    public class MemorySystemException : Exception
    {
        public string ErrorCode { get; }

        public MemorySystemException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public MemorySystemException(string message, string errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Memory not found exception;
    /// </summary>
    public class MemoryNotFoundException : MemorySystemException;
    {
        public string MemoryId { get; }

        public MemoryNotFoundException(string message, string memoryId)
            : base(message, ErrorCodes.MEMORY_NOT_FOUND)
        {
            MemoryId = memoryId;
        }
    }

    /// <summary>
    /// Memory validation exception;
    /// </summary>
    public class MemoryValidationException : MemorySystemException;
    {
        public string MemoryId { get; }
        public List<ValidationError> Errors { get; }

        public MemoryValidationException(string message, string memoryId, List<ValidationError> errors)
            : base(message, ErrorCodes.MEMORY_VALIDATION_FAILED)
        {
            MemoryId = memoryId;
            Errors = errors;
        }
    }

    /// <summary>
    /// Memory storage exception;
    /// </summary>
    public class MemoryStorageException : MemorySystemException;
    {
        public string MemoryId { get; }

        public MemoryStorageException(string message, string memoryId, string errorCode)
            : base(message, errorCode)
        {
            MemoryId = memoryId;
        }

        public MemoryStorageException(string message, string memoryId, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
            MemoryId = memoryId;
        }
    }

    /// <summary>
    /// Memory retrieval exception;
    /// </summary>
    public class MemoryRetrievalException : MemorySystemException;
    {
        public MemoryQuery Query { get; }

        public MemoryRetrievalException(string message, MemoryQuery query, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
            Query = query;
        }
    }

    /// <summary>
    /// Memory update exception;
    /// </summary>
    public class MemoryUpdateException : MemorySystemException;
    {
        public string MemoryId { get; }

        public MemoryUpdateException(string message, string memoryId, string errorCode)
            : base(message, errorCode)
        {
            MemoryId = memoryId;
        }

        public MemoryUpdateException(string message, string memoryId, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
            MemoryId = memoryId;
        }
    }

    /// <summary>
    /// Memory consolidation exception;
    /// </summary>
    public class MemoryConsolidationException : MemorySystemException;
    {
        public MemoryConsolidationException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Memory pruning exception;
    /// </summary>
    public class MemoryPruningException : MemorySystemException;
    {
        public MemoryPruningException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Memory association exception;
    /// </summary>
    public class MemoryAssociationException : MemorySystemException;
    {
        public MemoryAssociationException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Memory export exception;
    /// </summary>
    public class MemoryExportException : MemorySystemException;
    {
        public MemoryExportException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Memory import exception;
    /// </summary>
    public class MemoryImportException : MemorySystemException;
    {
        public MemoryImportException(string message, string errorCode, Exception innerException)
            : base(message, errorCode, innerException)
        {
        }
    }

    /// <summary>
    /// Memory index exception;
    /// </summary>
    public class MemoryIndexException : MemorySystemException;
    {
        public string MemoryId { get; }

        public MemoryIndexException(string message, string memoryId, string errorCode)
            : base(message, errorCode)
        {
            MemoryId = memoryId;
        }
    }

    #endregion;

    #region Supporting Infrastructure;

    /// <summary>
    /// Memory storage engine for persistent storage;
    /// </summary>
    internal class MemoryStorageEngine : IDisposable
    {
        private readonly ILogger<MemoryStorageEngine> _logger;
        private readonly AppConfig _appConfig;
        private readonly IMemoryStore _store;
        private bool _disposed;

        public MemoryStorageEngine(ILogger<MemoryStorageEngine> logger, AppConfig appConfig)
        {
            _logger = logger;
            _appConfig = appConfig;
            _store = CreateMemoryStore();
        }

        public async Task InitializeAsync()
        {
            await _store.InitializeAsync();
            _logger.LogInformation("MemoryStorageEngine initialized");
        }

        public async Task<MemoryStorageResult> StoreAsync(MemoryItem memory)
        {
            // Implementation depends on the storage backend;
            // This is a simplified version;
            await Task.Delay(1);
            return new MemoryStorageResult;
            {
                Success = true,
                StorageId = Guid.NewGuid().ToString()
            };
        }

        public async Task<MemoryItem> GetAsync(string memoryId)
        {
            // Implementation depends on the storage backend;
            await Task.Delay(1);
            return null;
        }

        public async Task<MemoryStorageResult> UpdateAsync(MemoryItem memory)
        {
            // Implementation depends on the storage backend;
            await Task.Delay(1);
            return new MemoryStorageResult;
            {
                Success = true;
            };
        }

        public async Task<bool> DeleteAsync(string memoryId)
        {
            // Implementation depends on the storage backend;
            await Task.Delay(1);
            return true;
        }

        public async Task UpdateAccessInfoAsync(string memoryId, int accessCount, DateTime lastAccessed)
        {
            // Implementation depends on the storage backend;
            await Task.Delay(1);
        }

        public List<MemoryItem> GetAllMemories()
        {
            // Implementation depends on the storage backend;
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> GetAllMemoriesAsync()
        {
            // Implementation depends on the storage backend;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        private IMemoryStore CreateMemoryStore()
        {
            // Create appropriate store based on configuration;
            return new InMemoryStore();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _store?.Dispose();
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Memory index engine for fast retrieval;
    /// </summary>
    internal class MemoryIndexEngine : IDisposable
    {
        private readonly ILogger<MemoryIndexEngine> _logger;
        private readonly Dictionary<string, MemoryIndex> _indexes;
        private bool _disposed;

        public MemoryIndexEngine(ILogger<MemoryIndexEngine> logger)
        {
            _logger = logger;
            _indexes = new Dictionary<string, MemoryIndex>();
        }

        public async Task<IndexResult> IndexAsync(MemoryItem memory)
        {
            // Add to all relevant indexes;
            await AddToSemanticIndexAsync(memory);
            await AddToTemporalIndexAsync(memory);
            await AddToCategoryIndexAsync(memory);
            await AddToKeywordIndexAsync(memory);

            return new IndexResult { Success = true };
        }

        public async Task<IndexResult> ReindexAsync(MemoryItem memory)
        {
            // Remove from indexes;
            await RemoveFromIndexAsync(memory.Id);

            // Re-add to indexes;
            return await IndexAsync(memory);
        }

        public async Task<bool> RemoveFromIndexAsync(string memoryId)
        {
            // Remove from all indexes;
            foreach (var index in _indexes.Values)
            {
                await index.RemoveAsync(memoryId);
            }

            return true;
        }

        public async Task UpdateAssociationsAsync(MemoryItem memory)
        {
            // Update association index;
            if (!_indexes.ContainsKey("associations"))
            {
                _indexes["associations"] = new AssociationIndex();
            }

            await _indexes["associations"].UpdateAsync(memory);
        }

        public async Task BuildIndexesAsync(List<MemoryItem> memories)
        {
            foreach (var memory in memories)
            {
                await IndexAsync(memory);
            }
        }

        public int GetIndexCount()
        {
            return _indexes.Count;
        }

        private async Task AddToSemanticIndexAsync(MemoryItem memory)
        {
            if (!_indexes.ContainsKey("semantic"))
            {
                _indexes["semantic"] = new SemanticIndex();
            }

            await _indexes["semantic"].AddAsync(memory);
        }

        private async Task AddToTemporalIndexAsync(MemoryItem memory)
        {
            if (!_indexes.ContainsKey("temporal"))
            {
                _indexes["temporal"] = new TemporalIndex();
            }

            await _indexes["temporal"].AddAsync(memory);
        }

        private async Task AddToCategoryIndexAsync(MemoryItem memory)
        {
            if (!_indexes.ContainsKey("category"))
            {
                _indexes["category"] = new CategoryIndex();
            }

            await _indexes["category"].AddAsync(memory);
        }

        private async Task AddToKeywordIndexAsync(MemoryItem memory)
        {
            if (!_indexes.ContainsKey("keyword"))
            {
                _indexes["keyword"] = new KeywordIndex();
            }

            await _indexes["keyword"].AddAsync(memory);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    foreach (var index in _indexes.Values)
                    {
                        index.Dispose();
                    }
                    _indexes.Clear();
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Memory retrieval engine;
    /// </summary>
    internal class MemoryRetrievalEngine : IDisposable
    {
        private readonly ILogger<MemoryRetrievalEngine> _logger;
        private readonly MemoryIndexEngine _indexEngine;
        private bool _disposed;

        public MemoryRetrievalEngine(ILogger<MemoryRetrievalEngine> logger, MemoryIndexEngine indexEngine)
        {
            _logger = logger;
            _indexEngine = indexEngine;
        }

        public async Task<List<MemoryItem>> RetrieveBySimilarityAsync(MemoryEmbedding queryEmbedding, int maxResults)
        {
            // Implementation for semantic similarity search;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> RetrieveByTimeRangeAsync(TimeRange timeRange, int maxResults)
        {
            // Implementation for temporal retrieval;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> RetrieveRecentAsync(int maxResults)
        {
            // Implementation for recent memories retrieval;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> RetrieveAssociationsAsync(string memoryId, int maxResults)
        {
            // Implementation for associative retrieval;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> RetrieveByContextAsync(MemoryContext context, int maxResults)
        {
            // Implementation for contextual retrieval;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> RetrieveByPatternAsync(MemoryPattern pattern, int maxResults)
        {
            // Implementation for pattern-based retrieval;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> RetrieveByKeywordsAsync(List<string> keywords, int maxResults)
        {
            // Implementation for keyword-based retrieval;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public async Task<List<MemoryItem>> RetrieveByCategoriesAsync(List<MemoryCategory> categories, int maxResults)
        {
            // Implementation for category-based retrieval;
            await Task.Delay(1);
            return new List<MemoryItem>();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Memory consolidation engine;
    /// </summary>
    internal class MemoryConsolidationEngine : IDisposable
    {
        private readonly ILogger<MemoryConsolidationEngine> _logger;
        private bool _disposed;

        public MemoryConsolidationEngine(ILogger<MemoryConsolidationEngine> logger)
        {
            _logger = logger;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Memory associative engine;
    /// </summary>
    internal class MemoryAssociativeEngine : IDisposable
    {
        private readonly ILogger<MemoryAssociativeEngine> _logger;
        private bool _disposed;

        public MemoryAssociativeEngine(ILogger<MemoryAssociativeEngine> logger)
        {
            _logger = logger;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Memory pruning engine;
    /// </summary>
    internal class MemoryPruningEngine : IDisposable
    {
        private readonly ILogger<MemoryPruningEngine> _logger;
        private readonly AppConfig _appConfig;
        private bool _disposed;

        public MemoryPruningEngine(ILogger<MemoryPruningEngine> logger, AppConfig appConfig)
        {
            _logger = logger;
            _appConfig = appConfig;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    #endregion;

    #region Supporting Types;

    public class MemoryRetrievalOptions;
    {
        public int MaxResults { get; set; } = 10;
        public bool UseCache { get; set; } = true;
        public bool CacheResult { get; set; } = true;
        public bool UseSemanticScoring { get; set; } = true;
        public bool UseTemporalScoring { get; set; } = true;
        public bool UseImportanceScoring { get; set; } = true;
        public bool UseFrequencyScoring { get; set; } = true;
        public bool UseContextScoring { get; set; } = true;
        public Dictionary<string, object> Filters { get; set; } = new Dictionary<string, object>();
        public SortOrder SortOrder { get; set; } = SortOrder.Relevance;
    }

    public class MemoryConsolidationOptions;
    {
        public ConsolidationStrategy Strategy { get; set; } = ConsolidationStrategy.Semantic;
        public double MinSimilarity { get; set; } = 0.7;
        public int MinGroupSize { get; set; } = 2;
        public int MaxGroupSize { get; set; } = 10;
        public bool PreserveOriginalMemories { get; set; } = false;
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
    }

    public class MemoryPruningOptions;
    {
        public PruningStrategy Strategy { get; set; } = PruningStrategy.Combined;
        public int MaxAgeDays { get; set; } = 30;
        public double MinImportance { get; set; } = 3;
        public int MinAccessCount { get; set; } = 1;
        public long MaxMemorySize { get; set; } = 1024 * 1024; // 1MB;
        public int MaxSimilarMemories { get; set; } = 5;
        public double PruningThreshold { get; set; } = 0.7;
        public List<string> ProtectedMemoryIds { get; set; } = new List<string>();
    }

    public class MemoryExportOptions;
    {
        public List<string> MemoryIds { get; set; }
        public ExportFormat Format { get; set; } = ExportFormat.Json;
        public int CompressionLevel { get; set; } = 0;
        public string EncryptionKey { get; set; }
        public bool IncludeAssociations { get; set; } = true;
        public bool IncludeMetadata { get; set; } = true;
        public Dictionary<string, object> Filters { get; set; } = new Dictionary<string, object>();
    }

    public class MemoryImportOptions;
    {
        public ImportConflictResolution ConflictResolution { get; set; } = ImportConflictResolution.Skip;
        public string DecryptionKey { get; set; }
        public bool ValidateBeforeImport { get; set; } = true;
        public bool PreserveTimestamps { get; set; } = false;
        public Dictionary<string, object> AdditionalMetadata { get; set; } = new Dictionary<string, object>();
    }

    public class MemoryAccessPattern;
    {
        public string Key { get; set; }
        public MemoryOperation Operation { get; set; }
        public long AccessCount { get; set; }
        public DateTime LastAccessed { get; set; }
        public double AverageAccessInterval { get; set; }
    }

    public class MemoryUpdateRecord : ICloneable;
    {
        public DateTime Timestamp { get; set; }
        public int PreviousVersion { get; set; }
        public MemoryContext Context { get; set; }
        public List<MemoryChange> Changes { get; set; } = new List<MemoryChange>();

        public object Clone()
        {
            return new MemoryUpdateRecord;
            {
                Timestamp = Timestamp,
                PreviousVersion = PreviousVersion,
                Context = (MemoryContext)Context?.Clone(),
                Changes = Changes.Select(c => (MemoryChange)c.Clone()).ToList()
            };
        }
    }

    public class MemoryChange : ICloneable;
    {
        public string Field { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public ChangeType Type { get; set; }

        public object Clone()
        {
            return new MemoryChange;
            {
                Field = Field,
                OldValue = OldValue,
                NewValue = NewValue,
                Type = Type;
            };
        }
    }

    public class MemoryConsolidationGroup;
    {
        public string GroupId { get; set; }
        public List<MemoryItem> OriginalMemories { get; set; } = new List<MemoryItem>();
        public List<MemoryItem> ConsolidatedMemories { get; set; } = new List<MemoryItem>();
        public double AverageSimilarity { get; set; }
        public ConsolidationStrategy Strategy { get; set; }
    }

    public class MemoryImportDetail;
    {
        public string MemoryId { get; set; }
        public ImportStatus Status { get; set; }
        public int Version { get; set; }
        public int? ExistingVersion { get; set; }
        public int? ImportedVersion { get; set; }
        public string Action { get; set; }
        public string Reason { get; set; }
        public Exception Error { get; set; }
    }

    public class ExportMetadata;
    {
        public int TotalMemories { get; set; }
        public long TotalSize { get; set; }
        public ExportFormat ExportFormat { get; set; }
        public int CompressionLevel { get; set; }
        public bool IncludeAssociations { get; set; }
        public bool IncludeMetadata { get; set; }
        public DateTime ExportTimestamp { get; set; }
    }

    public class MemoryValidationResult;
    {
        public string MemoryId { get; set; }
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public DateTime ValidatedAt { get; set; }
    }

    public class ValidationError;
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public ValidationSeverity Severity { get; set; }
        public string Field { get; set; }
    }

    public class IndexResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int IndexedCount { get; set; }
    }

    #endregion;

    #region Enums;

    public enum SortOrder;
    {
        Relevance,
        Chronological,
        Importance,
        AccessFrequency,
        Size;
    }

    public enum ExportFormat;
    {
        Json,
        Xml,
        Binary,
        Compressed;
    }

    public enum ChangeType;
    {
        Added,
        Modified,
        Removed;
    }

    public enum ValidationSeverity;
    {
        Information,
        Warning,
        Error;
    }

    #endregion;

    #region Error Codes;

    public static class ErrorCodes;
    {
        // Memory System Errors;
        public const string MEMORY_INITIALIZATION_FAILED = "LTM001";
        public const string MEMORY_NOT_FOUND = "LTM002";
        public const string MEMORY_VALIDATION_FAILED = "LTM003";
        public const string MEMORY_STORAGE_FAILED = "LTM004";
        public const string MEMORY_RETRIEVAL_FAILED = "LTM005";
        public const string MEMORY_UPDATE_FAILED = "LTM006";
        public const string MEMORY_CONSOLIDATION_FAILED = "LTM007";
        public const string MEMORY_PRUNING_FAILED = "LTM008";
        public const string MEMORY_ASSOCIATION_FAILED = "LTM009";
        public const string MEMORY_EXPORT_FAILED = "LTM010";
        public const string MEMORY_IMPORT_FAILED = "LTM011";

        // Index Errors;
        public const string MEMORY_INDEXING_FAILED = "LTM101";
        public const string MEMORY_REINDEXING_FAILED = "LTM102";

        // Cache Errors;
        public const string MEMORY_CACHE_FAILED = "LTM201";

        // Storage Errors;
        public const string MEMORY_STORAGE_FULL = "LTM301";
        public const string MEMORY_STORAGE_CORRUPTED = "LTM302";
    }

    #endregion;
}
