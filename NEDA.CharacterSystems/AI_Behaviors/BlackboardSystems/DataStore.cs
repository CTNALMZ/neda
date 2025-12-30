// NEDA.Brain/NLP_Engine/SemanticUnderstanding/DataStore.cs;
using Dapper;
using NEDA.AI.NaturalLanguage;
using NEDA.Brain.MemorySystem.RecallMechanism;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Brain.NLP_Engine.SemanticUnderstanding;
{
    /// <summary>
    /// Industrial-grade Data Store for NEDA AI Brain - Multi-model data storage and retrieval;
    /// Supports relational, document, graph, and key-value data models with intelligent caching;
    /// </summary>
    public sealed class DataStore : IDataStore, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly ConnectionMultiplexer _redisConnection;

        private readonly ConcurrentDictionary<string, DatabaseConnection> _connections;
        private readonly ConcurrentDictionary<string, DataModel> _models;
        private readonly ConcurrentDictionary<string, QueryCache> _queryCache;
        private readonly ConcurrentDictionary<string, DataIndex> _indices;

        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _migrationSemaphore;
        private readonly Timer _cleanupTimer;
        private readonly Timer _healthCheckTimer;

        private bool _isInitialized;
        private bool _isDisposed;
        private int _totalOperations;
        private DateTime _startTime;

        public DataStoreConfiguration Configuration { get; private set; }
        public StoreStatus Status { get; private set; }
        public int TotalConnections => _connections.Count;
        public int TotalModels => _models.Count;
        public int TotalIndices => _indices.Count;
        public long CacheHits { get; private set; }
        public long CacheMisses { get; private set; }
        public double CacheHitRatio => TotalCacheRequests > 0 ? (double)CacheHits / TotalCacheRequests * 100 : 0;
        public long TotalCacheRequests => CacheHits + CacheMisses;

        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<DataOperationEventArgs> DataOperationCompleted;
        public event EventHandler<CacheEventEventArgs> CacheEventOccurred;
        public event EventHandler<MigrationEventArgs> MigrationCompleted;

        #endregion;

        #region Constructor;

        public DataStore(
            ILogger logger,
            IExceptionHandler exceptionHandler,
            PerformanceMonitor performanceMonitor = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _performanceMonitor = performanceMonitor ?? new PerformanceMonitor();

            _connections = new ConcurrentDictionary<string, DatabaseConnection>();
            _models = new ConcurrentDictionary<string, DataModel>();
            _queryCache = new ConcurrentDictionary<string, QueryCache>();
            _indices = new ConcurrentDictionary<string, DataIndex>();
            _migrationSemaphore = new SemaphoreSlim(1, 1);

            Configuration = new DataStoreConfiguration();
            Status = StoreStatus.Stopped;
            _startTime = DateTime.UtcNow;

            // Initialize Redis connection if configured;
            if (!string.IsNullOrEmpty(Configuration.RedisConnectionString))
            {
                _redisConnection = ConnectionMultiplexer.Connect(Configuration.RedisConnectionString);
            }

            _logger.Info("Data Store initialized");
        }

        #endregion;

        #region Public Methods;

        public async Task InitializeAsync(DataStoreConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                throw new InvalidOperationException("Data Store already initialized");

            try
            {
                _logger.Info("Initializing Data Store...");
                Status = StoreStatus.Initializing;

                Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
                ValidateConfiguration();

                // Initialize primary connections;
                await InitializeConnectionsAsync(cancellationToken);

                // Load data models;
                await LoadDataModelsAsync(cancellationToken);

                // Build indices;
                await BuildIndicesAsync(cancellationToken);

                // Initialize cache;
                await InitializeCacheAsync(cancellationToken);

                // Setup timers;
                _cleanupTimer = new Timer(CleanupCallback, null,
                    TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
                _healthCheckTimer = new Timer(HealthCheckCallback, null,
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

                _isInitialized = true;
                Status = StoreStatus.Running;

                _logger.Info($"Data Store initialized with {_connections.Count} connections, {_models.Count} models");
            }
            catch (Exception ex)
            {
                Status = StoreStatus.Error;
                _logger.Error($"Failed to initialize Data Store: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "DataStore.Initialize");
                throw new DataStoreInitializationException("Failed to initialize Data Store", ex);
            }
        }

        public async Task<QueryResult<T>> QueryAsync<T>(
            string query,
            object parameters = null,
            QueryOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Query cannot be empty", nameof(query));

            var queryId = GenerateQueryId(query, parameters);
            var executionId = GenerateExecutionId();

            try
            {
                _logger.Debug($"Executing query: {queryId}");

                // Check cache first;
                if (options?.UseCache != false && Configuration.EnableQueryCaching)
                {
                    var cachedResult = await GetCachedResultAsync<T>(queryId, cancellationToken);
                    if (cachedResult != null)
                    {
                        CacheHits++;
                        _logger.Debug($"Cache hit for query: {queryId}");

                        OnCacheEventOccurred(new CacheEventEventArgs;
                        {
                            EventType = CacheEventType.Hit,
                            Key = queryId,
                            Size = cachedResult.Size,
                            Timestamp = DateTime.UtcNow;
                        });

                        return cachedResult;
                    }
                    CacheMisses++;
                }

                using var perfScope = _performanceMonitor.StartMeasurement($"DataStore.Query.{typeof(T).Name}");

                // Determine which connection to use;
                var connection = await GetConnectionForQueryAsync(query, cancellationToken);

                // Execute query;
                var result = await ExecuteQueryAsync<T>(connection, query, parameters, options, cancellationToken);
                result.QueryId = queryId;
                result.ExecutionId = executionId;

                // Cache the result;
                if (options?.UseCache != false && Configuration.EnableQueryCaching && result.IsSuccess)
                {
                    await CacheResultAsync(queryId, result, cancellationToken);
                }

                // Raise operation completed event;
                OnDataOperationCompleted(new DataOperationEventArgs;
                {
                    OperationId = executionId,
                    OperationType = DataOperationType.Query,
                    Query = query,
                    Duration = result.ExecutionTime,
                    Success = result.IsSuccess,
                    RowsAffected = result.Data?.Count() ?? 0,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Debug($"Query completed: {queryId} - Rows: {result.Data?.Count() ?? 0}, Time: {result.ExecutionTime.TotalMilliseconds:F0}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Query failed: {queryId} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.Query.{queryId}");

                return new QueryResult<T>
                {
                    QueryId = queryId,
                    ExecutionId = executionId,
                    IsSuccess = false,
                    Error = ex.Message,
                    ExecutionTime = TimeSpan.Zero;
                };
            }
            finally
            {
                Interlocked.Increment(ref _totalOperations);
            }
        }

        public async Task<ExecuteResult> ExecuteAsync(
            string command,
            object parameters = null,
            CommandOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command cannot be empty", nameof(command));

            var commandId = GenerateCommandId(command, parameters);
            var executionId = GenerateExecutionId();

            try
            {
                _logger.Debug($"Executing command: {commandId}");

                using var perfScope = _performanceMonitor.StartMeasurement("DataStore.Execute");

                // Determine which connection to use;
                var connection = await GetConnectionForCommandAsync(command, cancellationToken);

                // Execute command;
                var result = await ExecuteCommandAsync(connection, command, parameters, options, cancellationToken);
                result.CommandId = commandId;
                result.ExecutionId = executionId;

                // Invalidate cache if needed;
                if (options?.InvalidateCache != false && Configuration.EnableQueryCaching)
                {
                    await InvalidateCacheForCommandAsync(command, cancellationToken);
                }

                // Raise operation completed event;
                OnDataOperationCompleted(new DataOperationEventArgs;
                {
                    OperationId = executionId,
                    OperationType = DataOperationType.Execute,
                    Query = command,
                    Duration = result.ExecutionTime,
                    Success = result.IsSuccess,
                    RowsAffected = result.RowsAffected,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Debug($"Command completed: {commandId} - Rows: {result.RowsAffected}, Time: {result.ExecutionTime.TotalMilliseconds:F0}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Command failed: {commandId} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.Execute.{commandId}");

                return new ExecuteResult;
                {
                    CommandId = commandId,
                    ExecutionId = executionId,
                    IsSuccess = false,
                    Error = ex.Message,
                    ExecutionTime = TimeSpan.Zero;
                };
            }
            finally
            {
                Interlocked.Increment(ref _totalOperations);
            }
        }

        public async Task<BulkInsertResult> BulkInsertAsync<T>(
            string tableName,
            IEnumerable<T> data,
            BulkInsertOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Table name cannot be empty", nameof(tableName));

            if (data == null || !data.Any())
                throw new ArgumentException("Data cannot be null or empty", nameof(data));

            var operationId = GenerateOperationId("bulk_insert", tableName);

            try
            {
                _logger.Info($"Bulk insert to {tableName}: {data.Count()} records");

                using var perfScope = _performanceMonitor.StartMeasurement($"DataStore.BulkInsert.{tableName}");

                var connection = await GetConnectionForTableAsync(tableName, cancellationToken);
                var result = await ExecuteBulkInsertAsync(connection, tableName, data, options, cancellationToken);

                // Invalidate cache for this table;
                await InvalidateCacheForTableAsync(tableName, cancellationToken);

                // Update indices;
                await UpdateIndicesForBulkInsertAsync(tableName, data, cancellationToken);

                _logger.Info($"Bulk insert completed: {tableName} - {result.RowsAffected} rows, Time: {result.ExecutionTime.TotalMilliseconds:F0}ms");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Bulk insert failed: {tableName} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.BulkInsert.{operationId}");
                throw;
            }
        }

        public async Task<TransactionResult> ExecuteTransactionAsync(
            Func<IDbTransaction, Task> transactionAction,
            TransactionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (transactionAction == null)
                throw new ArgumentNullException(nameof(transactionAction));

            var transactionId = GenerateTransactionId();

            try
            {
                _logger.Debug($"Starting transaction: {transactionId}");

                var connection = await GetPrimaryConnectionAsync(cancellationToken);
                using var dbConnection = await connection.GetOpenConnectionAsync(cancellationToken);

                using var transaction = dbConnection.BeginTransaction(options?.IsolationLevel ?? IsolationLevel.ReadCommitted);

                try
                {
                    await transactionAction(transaction);

                    if (options?.AutoCommit != false)
                    {
                        transaction.Commit();
                        _logger.Debug($"Transaction committed: {transactionId}");
                    }

                    return new TransactionResult;
                    {
                        TransactionId = transactionId,
                        IsSuccess = true,
                        IsCommitted = options?.AutoCommit != false,
                        ExecutionTime = TimeSpan.Zero // Would track actual time;
                    };
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.Error($"Transaction rolled back: {transactionId} - {ex.Message}", ex);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Transaction failed: {transactionId} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.ExecuteTransaction.{transactionId}");

                return new TransactionResult;
                {
                    TransactionId = transactionId,
                    IsSuccess = false,
                    Error = ex.Message,
                    ExecutionTime = TimeSpan.Zero;
                };
            }
        }

        public async Task<DataModel> GetModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(modelName))
                throw new ArgumentException("Model name cannot be empty", nameof(modelName));

            if (_models.TryGetValue(modelName, out var model))
            {
                return model;
            }

            // Try to load model from database;
            try
            {
                model = await LoadModelFromDatabaseAsync(modelName, cancellationToken);
                if (model != null)
                {
                    _models[modelName] = model;
                    return model;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load model {modelName} from database: {ex.Message}");
            }

            throw new ModelNotFoundException($"Data model not found: {modelName}");
        }

        public async Task<DataModel> CreateModelAsync(
            string modelName,
            ModelDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(modelName))
                throw new ArgumentException("Model name cannot be empty", nameof(modelName));

            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (_models.ContainsKey(modelName))
                throw new InvalidOperationException($"Model already exists: {modelName}");

            try
            {
                _logger.Info($"Creating data model: {modelName}");

                // Validate model definition;
                ValidateModelDefinition(definition);

                // Create model in database;
                await CreateModelInDatabaseAsync(modelName, definition, cancellationToken);

                // Create model object;
                var model = new DataModel(modelName, definition);
                _models[modelName] = model;

                // Build indices for the model;
                await BuildIndicesForModelAsync(model, cancellationToken);

                _logger.Info($"Data model created: {modelName} with {definition.Fields.Count} fields");

                return model;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create model {modelName}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.CreateModel.{modelName}");
                throw new ModelCreationException($"Failed to create model {modelName}", ex);
            }
        }

        public async Task<bool> UpdateModelAsync(
            string modelName,
            ModelUpdate update,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(modelName))
                throw new ArgumentException("Model name cannot be empty", nameof(modelName));

            if (update == null)
                throw new ArgumentNullException(nameof(update));

            if (!_models.TryGetValue(modelName, out var existingModel))
                throw new ModelNotFoundException($"Model not found: {modelName}");

            try
            {
                _logger.Info($"Updating data model: {modelName}");

                await _migrationSemaphore.WaitAsync(cancellationToken);

                try
                {
                    // Apply migration;
                    var migrationResult = await ApplyModelMigrationAsync(modelName, update, cancellationToken);

                    // Update model in memory;
                    var updatedModel = existingModel.ApplyUpdate(update);
                    _models[modelName] = updatedModel;

                    // Rebuild indices if needed;
                    if (update.RebuildIndices)
                    {
                        await RebuildIndicesForModelAsync(modelName, cancellationToken);
                    }

                    OnMigrationCompleted(new MigrationEventArgs;
                    {
                        ModelName = modelName,
                        MigrationType = MigrationType.ModelUpdate,
                        Success = migrationResult.Success,
                        Duration = migrationResult.Duration,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Info($"Data model updated: {modelName}");

                    return migrationResult.Success;
                }
                finally
                {
                    _migrationSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update model {modelName}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.UpdateModel.{modelName}");
                return false;
            }
        }

        public async Task<SearchResult<T>> SearchAsync<T>(
            string modelName,
            SearchCriteria criteria,
            SearchOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(modelName))
                throw new ArgumentException("Model name cannot be empty", nameof(modelName));

            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            var searchId = GenerateSearchId(modelName, criteria);

            try
            {
                _logger.Debug($"Searching model {modelName}: {criteria.Conditions.Count} conditions");

                // Check cache first;
                if (options?.UseCache != false && Configuration.EnableSearchCaching)
                {
                    var cachedResult = await GetCachedSearchResultAsync<T>(searchId, cancellationToken);
                    if (cachedResult != null)
                    {
                        CacheHits++;
                        return cachedResult;
                    }
                    CacheMisses++;
                }

                using var perfScope = _performanceMonitor.StartMeasurement($"DataStore.Search.{modelName}");

                // Get model;
                var model = await GetModelAsync(modelName, cancellationToken);

                // Check if we can use indices;
                var useIndex = options?.UseIndex != false && await CanUseIndexForSearchAsync(modelName, criteria, cancellationToken);

                SearchResult<T> result;

                if (useIndex)
                {
                    // Use index for faster search;
                    result = await SearchUsingIndexAsync<T>(modelName, criteria, options, cancellationToken);
                }
                else;
                {
                    // Use database query;
                    var query = BuildSearchQuery(modelName, criteria, options);
                    var queryResult = await QueryAsync<T>(query, criteria.Parameters, new QueryOptions;
                    {
                        UseCache = false;
                    }, cancellationToken);

                    result = new SearchResult<T>
                    {
                        SearchId = searchId,
                        ModelName = modelName,
                        IsSuccess = queryResult.IsSuccess,
                        Data = queryResult.Data,
                        TotalCount = queryResult.Data?.Count() ?? 0,
                        ExecutionTime = queryResult.ExecutionTime,
                        UsedIndex = false;
                    };
                }

                // Cache the result;
                if (options?.UseCache != false && Configuration.EnableSearchCaching && result.IsSuccess)
                {
                    await CacheSearchResultAsync(searchId, result, cancellationToken);
                }

                _logger.Debug($"Search completed: {modelName} - Results: {result.TotalCount}, Time: {result.ExecutionTime.TotalMilliseconds:F0}ms, UsedIndex: {result.UsedIndex}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Search failed: {modelName} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.Search.{searchId}");

                return new SearchResult<T>
                {
                    SearchId = searchId,
                    ModelName = modelName,
                    IsSuccess = false,
                    Error = ex.Message,
                    ExecutionTime = TimeSpan.Zero;
                };
            }
        }

        public async Task<AggregateResult> AggregateAsync(
            string modelName,
            AggregateCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(modelName))
                throw new ArgumentException("Model name cannot be empty", nameof(modelName));

            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            var aggregateId = GenerateAggregateId(modelName, criteria);

            try
            {
                _logger.Debug($"Aggregating model {modelName}: {criteria.Aggregations.Count} aggregations");

                using var perfScope = _performanceMonitor.StartMeasurement($"DataStore.Aggregate.{modelName}");

                var query = BuildAggregateQuery(modelName, criteria);
                var result = await QueryAsync<dynamic>(query, criteria.Parameters, new QueryOptions;
                {
                    UseCache = false;
                }, cancellationToken);

                var aggregateResult = new AggregateResult;
                {
                    AggregateId = aggregateId,
                    ModelName = modelName,
                    IsSuccess = result.IsSuccess,
                    Results = result.Data?.ToList(),
                    ExecutionTime = result.ExecutionTime;
                };

                _logger.Debug($"Aggregation completed: {modelName} - Time: {aggregateResult.ExecutionTime.TotalMilliseconds:F0}ms");

                return aggregateResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Aggregation failed: {modelName} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.Aggregate.{aggregateId}");

                return new AggregateResult;
                {
                    AggregateId = aggregateId,
                    ModelName = modelName,
                    IsSuccess = false,
                    Error = ex.Message,
                    ExecutionTime = TimeSpan.Zero;
                };
            }
        }

        public async Task<bool> CreateIndexAsync(
            string modelName,
            IndexDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(modelName))
                throw new ArgumentException("Model name cannot be empty", nameof(modelName));

            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (!_models.ContainsKey(modelName))
                throw new ModelNotFoundException($"Model not found: {modelName}");

            var indexId = GenerateIndexId(modelName, definition);

            try
            {
                _logger.Info($"Creating index: {indexId}");

                // Create index in database;
                await CreateIndexInDatabaseAsync(modelName, definition, cancellationToken);

                // Create index object;
                var index = new DataIndex(indexId, modelName, definition);
                _indices[indexId] = index;

                // Build index;
                await BuildIndexAsync(index, cancellationToken);

                _logger.Info($"Index created: {indexId} on {modelName}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create index {indexId}: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.CreateIndex.{indexId}");
                return false;
            }
        }

        public async Task<DataStoreMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                var metrics = new DataStoreMetrics;
                {
                    Status = Status,
                    Uptime = DateTime.UtcNow - _startTime,
                    TotalOperations = _totalOperations,
                    TotalConnections = _connections.Count,
                    TotalModels = _models.Count,
                    TotalIndices = _indices.Count,
                    CacheHitRatio = CacheHitRatio,
                    CacheHits = CacheHits,
                    CacheMisses = CacheMisses,
                    TotalCacheSize = await GetTotalCacheSizeAsync(cancellationToken),
                    Timestamp = DateTime.UtcNow;
                };

                // Get connection metrics;
                foreach (var connection in _connections.Values)
                {
                    metrics.ConnectionMetrics.Add(new ConnectionMetrics;
                    {
                        Name = connection.Name,
                        Type = connection.Type,
                        IsConnected = connection.IsConnected,
                        LastActivity = connection.LastActivity,
                        TotalQueries = connection.TotalQueries;
                    });
                }

                // Get model metrics;
                foreach (var model in _models.Values)
                {
                    var rowCount = await GetModelRowCountAsync(model.Name, cancellationToken);
                    metrics.ModelMetrics.Add(new ModelMetrics;
                    {
                        Name = model.Name,
                        RowCount = rowCount,
                        FieldCount = model.Fields.Count,
                        IndexCount = _indices.Count(kvp => kvp.Value.ModelName == model.Name)
                    });
                }

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get metrics: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "DataStore.GetMetrics");
                throw;
            }
        }

        public async Task<bool> ClearCacheAsync(CacheClearOptions options = null, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            try
            {
                _logger.Info("Clearing data store cache...");

                var result = true;

                // Clear query cache;
                _queryCache.Clear();

                // Clear Redis cache if configured;
                if (_redisConnection != null && _redisConnection.IsConnected)
                {
                    var db = _redisConnection.GetDatabase();
                    await db.ExecuteAsync("FLUSHDB");
                }

                // Clear in-memory cache;
                CacheHits = 0;
                CacheMisses = 0;

                _logger.Info("Data store cache cleared");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to clear cache: {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, "DataStore.ClearCache");
                return false;
            }
        }

        public async Task<bool> BackupAsync(string backupPath, BackupOptions options = null, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(backupPath))
                throw new ArgumentException("Backup path cannot be empty", nameof(backupPath));

            var backupId = GenerateBackupId();

            try
            {
                _logger.Info($"Starting backup: {backupId} to {backupPath}");

                // Create backup directory;
                Directory.CreateDirectory(backupPath);

                // Backup each database;
                var backupTasks = new List<Task<bool>>();
                foreach (var connection in _connections.Values.Where(c => c.Type == DatabaseType.Relational))
                {
                    backupTasks.Add(BackupDatabaseAsync(connection, backupPath, backupId, cancellationToken));
                }

                var results = await Task.WhenAll(backupTasks);
                var success = results.All(r => r);

                if (success)
                {
                    // Backup models and indices;
                    await BackupMetadataAsync(backupPath, backupId, cancellationToken);

                    // Backup cache if configured;
                    if (options?.IncludeCache == true)
                    {
                        await BackupCacheAsync(backupPath, backupId, cancellationToken);
                    }

                    _logger.Info($"Backup completed: {backupId}");
                }
                else;
                {
                    _logger.Error($"Backup failed: {backupId}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.Error($"Backup failed: {backupId} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.Backup.{backupId}");
                return false;
            }
        }

        public async Task<bool> RestoreAsync(string backupPath, RestoreOptions options = null, CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(backupPath))
                throw new ArgumentException("Backup path cannot be empty", nameof(backupPath));

            if (!Directory.Exists(backupPath))
                throw new DirectoryNotFoundException($"Backup directory not found: {backupPath}");

            var restoreId = GenerateRestoreId();

            try
            {
                _logger.Info($"Starting restore: {restoreId} from {backupPath}");

                // Stop operations during restore;
                var previousStatus = Status;
                Status = StoreStatus.Maintenance;

                try
                {
                    // Clear existing data;
                    await ClearAllDataAsync(cancellationToken);

                    // Restore databases;
                    var restoreTasks = new List<Task<bool>>();
                    var backupFiles = Directory.GetFiles(backupPath, "*.backup");

                    foreach (var backupFile in backupFiles)
                    {
                        restoreTasks.Add(RestoreDatabaseAsync(backupFile, cancellationToken));
                    }

                    var results = await Task.WhenAll(restoreTasks);
                    var success = results.All(r => r);

                    if (success)
                    {
                        // Restore metadata;
                        await RestoreMetadataAsync(backupPath, cancellationToken);

                        // Restore cache if configured;
                        if (options?.RestoreCache == true)
                        {
                            await RestoreCacheAsync(backupPath, cancellationToken);
                        }

                        // Rebuild indices;
                        await RebuildAllIndicesAsync(cancellationToken);

                        _logger.Info($"Restore completed: {restoreId}");
                    }
                    else;
                    {
                        _logger.Error($"Restore failed: {restoreId}");
                    }

                    return success;
                }
                finally
                {
                    Status = previousStatus;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Restore failed: {restoreId} - {ex.Message}", ex);
                await _exceptionHandler.HandleExceptionAsync(ex, $"DataStore.Restore.{restoreId}");
                return false;
            }
        }

        #endregion;

        #region Private Methods;

        private async Task InitializeConnectionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info("Initializing database connections...");

                // Primary relational database;
                if (!string.IsNullOrEmpty(Configuration.PrimaryConnectionString))
                {
                    var primaryConnection = new DatabaseConnection(
                        "primary",
                        DatabaseType.Relational,
                        Configuration.PrimaryConnectionString,
                        _logger);

                    await primaryConnection.InitializeAsync(cancellationToken);
                    _connections["primary"] = primaryConnection;

                    OnConnectionStateChanged(new ConnectionStateChangedEventArgs;
                    {
                        ConnectionName = "primary",
                        IsConnected = true,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Secondary databases;
                foreach (var secondaryConfig in Configuration.SecondaryConnections)
                {
                    try
                    {
                        var connection = new DatabaseConnection(
                            secondaryConfig.Name,
                            secondaryConfig.Type,
                            secondaryConfig.ConnectionString,
                            _logger);

                        await connection.InitializeAsync(cancellationToken);
                        _connections[secondaryConfig.Name] = connection;

                        OnConnectionStateChanged(new ConnectionStateChangedEventArgs;
                        {
                            ConnectionName = secondaryConfig.Name,
                            IsConnected = true,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to initialize secondary connection {secondaryConfig.Name}: {ex.Message}");
                    }
                }

                _logger.Info($"Database connections initialized: {_connections.Count} connections");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize connections: {ex.Message}", ex);
                throw;
            }
        }

        private async Task LoadDataModelsAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info("Loading data models...");

                // Load from database metadata;
                var connection = await GetPrimaryConnectionAsync(cancellationToken);
                var models = await LoadModelsFromDatabaseAsync(connection, cancellationToken);

                foreach (var model in models)
                {
                    _models[model.Name] = model;
                }

                // Load predefined models;
                await LoadPredefinedModelsAsync(cancellationToken);

                _logger.Info($"Data models loaded: {_models.Count} models");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load data models: {ex.Message}", ex);
                throw;
            }
        }

        private async Task LoadPredefinedModelsAsync(CancellationToken cancellationToken)
        {
            // Semantic knowledge model;
            var semanticModel = new DataModel("semantic_knowledge", new ModelDefinition;
            {
                Description = "Semantic knowledge storage for NLP understanding",
                Fields = new List<ModelField>
                {
                    new ModelField { Name = "id", Type = FieldType.String, IsPrimaryKey = true },
                    new ModelField { Name = "concept", Type = FieldType.String, IsIndexed = true },
                    new ModelField { Name = "meaning", Type = FieldType.Text },
                    new ModelField { Name = "context", Type = FieldType.Text },
                    new ModelField { Name = "relationships", Type = FieldType.Json },
                    new ModelField { Name = "confidence", Type = FieldType.Decimal },
                    new ModelField { Name = "created_at", Type = FieldType.DateTime },
                    new ModelField { Name = "updated_at", Type = FieldType.DateTime }
                }
            });

            _models["semantic_knowledge"] = semanticModel;

            // Entity recognition model;
            var entityModel = new DataModel("entities", new ModelDefinition;
            {
                Description = "Named entity recognition storage",
                Fields = new List<ModelField>
                {
                    new ModelField { Name = "id", Type = FieldType.String, IsPrimaryKey = true },
                    new ModelField { Name = "text", Type = FieldType.String, IsIndexed = true },
                    new ModelField { Name = "entity_type", Type = FieldType.String, IsIndexed = true },
                    new ModelField { Name = "start_index", Type = FieldType.Integer },
                    new ModelField { Name = "end_index", Type = FieldType.Integer },
                    new ModelField { Name = "confidence", Type = FieldType.Decimal },
                    new ModelField { Name = "metadata", Type = FieldType.Json },
                    new ModelField { Name = "created_at", Type = FieldType.DateTime }
                }
            });

            _models["entities"] = entityModel;

            // User intent model;
            var intentModel = new DataModel("user_intents", new ModelDefinition;
            {
                Description = "User intent classification storage",
                Fields = new List<ModelField>
                {
                    new ModelField { Name = "id", Type = FieldType.String, IsPrimaryKey = true },
                    new ModelField { Name = "user_id", Type = FieldType.String, IsIndexed = true },
                    new ModelField { Name = "input_text", Type = FieldType.Text },
                    new ModelField { Name = "detected_intent", Type = FieldType.String, IsIndexed = true },
                    new ModelField { Name = "confidence", Type = FieldType.Decimal },
                    new ModelField { Name = "parameters", Type = FieldType.Json },
                    new ModelField { Name = "response", Type = FieldType.Text },
                    new ModelField { Name = "timestamp", Type = FieldType.DateTime, IsIndexed = true }
                }
            });

            _models["user_intents"] = intentModel;

            await Task.CompletedTask;
        }

        private async Task BuildIndicesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info("Building indices...");

                foreach (var model in _models.Values)
                {
                    await BuildIndicesForModelAsync(model, cancellationToken);
                }

                _logger.Info($"Indices built: {_indices.Count} indices");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to build indices: {ex.Message}", ex);
                throw;
            }
        }

        private async Task InitializeCacheAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info("Initializing cache...");

                if (_redisConnection != null && _redisConnection.IsConnected)
                {
                    _logger.Info("Redis cache initialized");
                }
                else;
                {
                    _logger.Info("Using in-memory cache");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to initialize cache: {ex.Message}");
            }
        }

        private async Task<QueryResult<T>> ExecuteQueryAsync<T>(
            DatabaseConnection connection,
            string query,
            object parameters,
            QueryOptions options,
            CancellationToken cancellationToken)
        {
            using var perfScope = _performanceMonitor.StartMeasurement($"ExecuteQuery.{typeof(T).Name}");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var dbConnection = await connection.GetOpenConnectionAsync(cancellationToken);

                IEnumerable<T> data;

                if (options?.CommandType == CommandType.StoredProcedure)
                {
                    data = await dbConnection.QueryAsync<T>(
                        query,
                        parameters,
                        commandType: CommandType.StoredProcedure);
                }
                else;
                {
                    var commandDefinition = new CommandDefinition(
                        commandText: query,
                        parameters: parameters,
                        commandTimeout: options?.TimeoutSeconds ?? Configuration.DefaultQueryTimeout,
                        cancellationToken: cancellationToken);

                    data = await dbConnection.QueryAsync<T>(commandDefinition);
                }

                stopwatch.Stop();

                return new QueryResult<T>
                {
                    IsSuccess = true,
                    Data = data,
                    ExecutionTime = stopwatch.Elapsed;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                throw new QueryExecutionException($"Failed to execute query: {ex.Message}", ex);
            }
        }

        private async Task<ExecuteResult> ExecuteCommandAsync(
            DatabaseConnection connection,
            string command,
            object parameters,
            CommandOptions options,
            CancellationToken cancellationToken)
        {
            using var perfScope = _performanceMonitor.StartMeasurement("ExecuteCommand");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var dbConnection = await connection.GetOpenConnectionAsync(cancellationToken);

                int rowsAffected;

                if (options?.CommandType == CommandType.StoredProcedure)
                {
                    rowsAffected = await dbConnection.ExecuteAsync(
                        command,
                        parameters,
                        commandType: CommandType.StoredProcedure);
                }
                else;
                {
                    var commandDefinition = new CommandDefinition(
                        commandText: command,
                        parameters: parameters,
                        commandTimeout: options?.TimeoutSeconds ?? Configuration.DefaultCommandTimeout,
                        cancellationToken: cancellationToken);

                    rowsAffected = await dbConnection.ExecuteAsync(commandDefinition);
                }

                stopwatch.Stop();

                return new ExecuteResult;
                {
                    IsSuccess = true,
                    RowsAffected = rowsAffected,
                    ExecutionTime = stopwatch.Elapsed;
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                throw new CommandExecutionException($"Failed to execute command: {ex.Message}", ex);
            }
        }

        private async Task<BulkInsertResult> ExecuteBulkInsertAsync<T>(
            DatabaseConnection connection,
            string tableName,
            IEnumerable<T> data,
            BulkInsertOptions options,
            CancellationToken cancellationToken)
        {
            using var perfScope = _performanceMonitor.StartMeasurement($"BulkInsert.{tableName}");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var dbConnection = await connection.GetOpenConnectionAsync(cancellationToken);

                // Use SqlBulkCopy for SQL Server, otherwise use batch inserts;
                if (connection.Type == DatabaseType.Relational &&
                    dbConnection is SqlConnection sqlConnection)
                {
                    using var bulkCopy = new SqlBulkCopy(sqlConnection)
                    {
                        DestinationTableName = tableName,
                        BatchSize = options?.BatchSize ?? 1000,
                        BulkCopyTimeout = options?.TimeoutSeconds ?? 30;
                    };

                    using var dataTable = ConvertToDataTable(data, tableName);
                    await bulkCopy.WriteToServerAsync(dataTable, cancellationToken);

                    stopwatch.Stop();

                    return new BulkInsertResult;
                    {
                        IsSuccess = true,
                        RowsAffected = dataTable.Rows.Count,
                        ExecutionTime = stopwatch.Elapsed;
                    };
                }
                else;
                {
                    // Use batch inserts for other databases;
                    var batchSize = options?.BatchSize ?? 100;
                    var batches = data.Chunk(batchSize);
                    var totalRows = 0;

                    foreach (var batch in batches)
                    {
                        // Build batch insert query;
                        var query = BuildBatchInsertQuery(tableName, batch, options);
                        var parameters = BuildBatchInsertParameters(batch);

                        var rowsAffected = await dbConnection.ExecuteAsync(query, parameters);
                        totalRows += rowsAffected;
                    }

                    stopwatch.Stop();

                    return new BulkInsertResult;
                    {
                        IsSuccess = true,
                        RowsAffected = totalRows,
                        ExecutionTime = stopwatch.Elapsed;
                    };
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                throw new BulkInsertException($"Failed to execute bulk insert: {ex.Message}", ex);
            }
        }

        private async Task<DatabaseConnection> GetConnectionForQueryAsync(string query, CancellationToken cancellationToken)
        {
            // Simple routing logic - can be enhanced with more sophisticated routing;
            return await GetPrimaryConnectionAsync(cancellationToken);
        }

        private async Task<DatabaseConnection> GetConnectionForCommandAsync(string command, CancellationToken cancellationToken)
        {
            return await GetPrimaryConnectionAsync(cancellationToken);
        }

        private async Task<DatabaseConnection> GetConnectionForTableAsync(string tableName, CancellationToken cancellationToken)
        {
            // Could implement sharding logic here;
            return await GetPrimaryConnectionAsync(cancellationToken);
        }

        private async Task<DatabaseConnection> GetPrimaryConnectionAsync(CancellationToken cancellationToken)
        {
            if (_connections.TryGetValue("primary", out var connection))
            {
                if (!connection.IsConnected)
                {
                    await connection.ReconnectAsync(cancellationToken);
                }
                return connection;
            }

            throw new ConnectionNotFoundException("Primary database connection not found");
        }

        private async Task<QueryResult<T>> GetCachedResultAsync<T>(string queryId, CancellationToken cancellationToken)
        {
            if (_redisConnection != null && _redisConnection.IsConnected)
            {
                try
                {
                    var db = _redisConnection.GetDatabase();
                    var cachedJson = await db.StringGetAsync(queryId);

                    if (!cachedJson.IsNullOrEmpty)
                    {
                        var cachedResult = JsonConvert.DeserializeObject<CachedQueryResult<T>>(cachedJson);

                        // Check expiration;
                        if (cachedResult.ExpiresAt > DateTime.UtcNow)
                        {
                            return new QueryResult<T>
                            {
                                QueryId = queryId,
                                IsSuccess = true,
                                Data = cachedResult.Data,
                                ExecutionTime = TimeSpan.Zero,
                                IsCached = true;
                            };
                        }
                        else;
                        {
                            // Remove expired cache entry
                            await db.KeyDeleteAsync(queryId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to get cached result from Redis: {ex.Message}");
                }
            }

            // Check in-memory cache;
            if (_queryCache.TryGetValue(queryId, out var cacheEntry))
            {
                if (cacheEntry.ExpiresAt > DateTime.UtcNow)
                {
                    var cachedResult = JsonConvert.DeserializeObject<IEnumerable<T>>(cacheEntry.Data);

                    return new QueryResult<T>
                    {
                        QueryId = queryId,
                        IsSuccess = true,
                        Data = cachedResult,
                        ExecutionTime = TimeSpan.Zero,
                        IsCached = true;
                    };
                }
                else;
                {
                    _queryCache.TryRemove(queryId, out _);
                }
            }

            return null;
        }

        private async Task CacheResultAsync<T>(string queryId, QueryResult<T> result, CancellationToken cancellationToken)
        {
            var cacheEntry = new CachedQueryResult<T>
            {
                Data = result.Data,
                ExpiresAt = DateTime.UtcNow.Add(Configuration.CacheDuration),
                Size = EstimateSize(result.Data)
            };

            var cacheJson = JsonConvert.SerializeObject(cacheEntry);

            if (_redisConnection != null && _redisConnection.IsConnected)
            {
                try
                {
                    var db = _redisConnection.GetDatabase();
                    await db.StringSetAsync(queryId, cacheJson, Configuration.CacheDuration);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to cache result in Redis: {ex.Message}");
                    // Fall back to in-memory cache;
                    CacheInMemory(queryId, cacheJson);
                }
            }
            else;
            {
                CacheInMemory(queryId, cacheJson);
            }
        }

        private void CacheInMemory(string key, string data)
        {
            var cacheEntry = new QueryCache;
            {
                Key = key,
                Data = data,
                ExpiresAt = DateTime.UtcNow.Add(Configuration.CacheDuration),
                LastAccessed = DateTime.UtcNow;
            };

            _queryCache[key] = cacheEntry

            // Clean up if cache is too large;
            if (_queryCache.Count > Configuration.MaxMemoryCacheEntries)
            {
                var oldestEntries = _queryCache.OrderBy(kvp => kvp.Value.LastAccessed)
                    .Take(_queryCache.Count - Configuration.MaxMemoryCacheEntries / 2)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var oldKey in oldestEntries)
                {
                    _queryCache.TryRemove(oldKey, out _);
                }
            }
        }

        private async Task InvalidateCacheForCommandAsync(string command, CancellationToken cancellationToken)
        {
            // Parse command to determine affected tables;
            var affectedTables = ParseAffectedTables(command);

            foreach (var table in affectedTables)
            {
                await InvalidateCacheForTableAsync(table, cancellationToken);
            }
        }

        private async Task InvalidateCacheForTableAsync(string tableName, CancellationToken cancellationToken)
        {
            // Invalidate Redis cache;
            if (_redisConnection != null && _redisConnection.IsConnected)
            {
                try
                {
                    var db = _redisConnection.GetDatabase();
                    var pattern = $"*{tableName}*";
                    var keys = _redisConnection.GetServer(_redisConnection.GetEndPoints().First())
                        .Keys(pattern: pattern)
                        .ToArray();

                    if (keys.Length > 0)
                    {
                        await db.KeyDeleteAsync(keys);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to invalidate Redis cache for table {tableName}: {ex.Message}");
                }
            }

            // Invalidate in-memory cache;
            var keysToRemove = _queryCache.Keys;
                .Where(k => k.Contains(tableName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _queryCache.TryRemove(key, out _);
            }
        }

        private async Task UpdateIndicesForBulkInsertAsync<T>(string tableName, IEnumerable<T> data, CancellationToken cancellationToken)
        {
            var indices = _indices.Values.Where(i => i.ModelName == tableName).ToList();

            foreach (var index in indices)
            {
                await UpdateIndexAsync(index, data, cancellationToken);
            }
        }

        private async Task<SearchResult<T>> SearchUsingIndexAsync<T>(
            string modelName,
            SearchCriteria criteria,
            SearchOptions options,
            CancellationToken cancellationToken)
        {
            // Find appropriate index;
            var index = FindIndexForSearch(modelName, criteria);

            if (index == null)
                throw new IndexNotFoundException($"No suitable index found for search on {modelName}");

            // Use index to find records;
            var indexResults = await SearchIndexAsync(index, criteria, cancellationToken);

            // Retrieve full records;
            var connection = await GetPrimaryConnectionAsync(cancellationToken);
            var query = BuildQueryForIndexResults(modelName, indexResults, criteria);

            var queryResult = await QueryAsync<T>(query, null, new QueryOptions { UseCache = false }, cancellationToken);

            return new SearchResult<T>
            {
                ModelName = modelName,
                IsSuccess = queryResult.IsSuccess,
                Data = queryResult.Data,
                TotalCount = queryResult.Data?.Count() ?? 0,
                ExecutionTime = queryResult.ExecutionTime,
                UsedIndex = true,
                IndexName = index.Name;
            };
        }

        private string BuildSearchQuery(string modelName, SearchCriteria criteria, SearchOptions options)
        {
            var builder = new StringBuilder();
            builder.Append($"SELECT * FROM {modelName}");

            if (criteria.Conditions.Any())
            {
                builder.Append(" WHERE ");
                var conditions = criteria.Conditions.Select(c => $"{c.Field} {c.Operator} @{c.Field}");
                builder.Append(string.Join(" AND ", conditions));
            }

            if (!string.IsNullOrEmpty(options?.OrderBy))
            {
                builder.Append($" ORDER BY {options.OrderBy}");

                if (options.OrderDescending)
                    builder.Append(" DESC");
            }

            if (options?.Limit > 0)
            {
                builder.Append($" LIMIT {options.Limit}");

                if (options?.Offset > 0)
                    builder.Append($" OFFSET {options.Offset}");
            }

            return builder.ToString();
        }

        private string BuildAggregateQuery(string modelName, AggregateCriteria criteria)
        {
            var builder = new StringBuilder();
            builder.Append("SELECT ");

            var aggregates = criteria.Aggregations.Select(a => $"{a.Function}({a.Field}) AS {a.Alias}");
            builder.Append(string.Join(", ", aggregates));

            builder.Append($" FROM {modelName}");

            if (criteria.Conditions.Any())
            {
                builder.Append(" WHERE ");
                var conditions = criteria.Conditions.Select(c => $"{c.Field} {c.Operator} @{c.Field}");
                builder.Append(string.Join(" AND ", conditions));
            }

            if (!string.IsNullOrEmpty(criteria.GroupBy))
            {
                builder.Append($" GROUP BY {criteria.GroupBy}");
            }

            if (!string.IsNullOrEmpty(criteria.Having))
            {
                builder.Append($" HAVING {criteria.Having}");
            }

            return builder.ToString();
        }

        private void CleanupCallback(object state)
        {
            if (!_isInitialized || _isDisposed)
                return;

            try
            {
                // Clean up expired cache entries;
                CleanupExpiredCache();

                // Clean up old connections;
                CleanupIdleConnections();

                // Update statistics;
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                _logger.Error($"Cleanup failed: {ex.Message}", ex);
            }
        }

        private void HealthCheckCallback(object state)
        {
            if (!_isInitialized || _isDisposed)
                return;

            try
            {
                // Check connection health;
                foreach (var connection in _connections.Values)
                {
                    if (!connection.IsConnected)
                    {
                        _logger.Warn($"Connection {connection.Name} is not connected");

                        // Try to reconnect;
                        connection.ReconnectAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }

                // Check cache health;
                CheckCacheHealth();

                // Log system status;
                LogSystemStatus();
            }
            catch (Exception ex)
            {
                _logger.Error($"Health check failed: {ex.Message}", ex);
            }
        }

        private void CleanupExpiredCache()
        {
            var expiredKeys = _queryCache.Where(kvp => kvp.Value.ExpiresAt < DateTime.UtcNow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _queryCache.TryRemove(key, out _);
            }

            if (expiredKeys.Count > 0)
            {
                _logger.Debug($"Cleaned up {expiredKeys.Count} expired cache entries");
            }
        }

        private void CleanupIdleConnections()
        {
            // Close connections that have been idle for too long;
            var idleThreshold = TimeSpan.FromMinutes(30);
            var now = DateTime.UtcNow;

            foreach (var connection in _connections.Values)
            {
                if (connection.LastActivity.HasValue &&
                    (now - connection.LastActivity.Value) > idleThreshold)
                {
                    connection.Close();
                    _logger.Debug($"Closed idle connection: {connection.Name}");
                }
            }
        }

        private void CheckCacheHealth()
        {
            if (_redisConnection != null && !_redisConnection.IsConnected)
            {
                _logger.Warn("Redis cache is not connected");
            }

            var memoryCacheSize = _queryCache.Count;
            if (memoryCacheSize > Configuration.MaxMemoryCacheEntries * 0.9)
            {
                _logger.Warn($"Memory cache is near capacity: {memoryCacheSize}/{Configuration.MaxMemoryCacheEntries}");
            }
        }

        private void LogSystemStatus()
        {
            var uptime = DateTime.UtcNow - _startTime;
            var cacheRatio = CacheHitRatio;

            _logger.Debug($"Data Store Status: Uptime={uptime:hh\\:mm\\:ss}, Operations={_totalOperations}, CacheHitRatio={cacheRatio:F1}%");
        }

        private void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(Configuration.PrimaryConnectionString))
                throw new ConfigurationException("Primary connection string is required");

            if (Configuration.DefaultQueryTimeout <= 0)
                throw new ConfigurationException("DefaultQueryTimeout must be greater than 0");

            if (Configuration.CacheDuration <= TimeSpan.Zero)
                throw new ConfigurationException("CacheDuration must be greater than zero");

            if (Configuration.MaxMemoryCacheEntries <= 0)
                throw new ConfigurationException("MaxMemoryCacheEntries must be greater than 0");
        }

        private void ValidateModelDefinition(ModelDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (definition.Fields == null || !definition.Fields.Any())
                throw new InvalidOperationException("Model must have at least one field");

            var primaryKeyCount = definition.Fields.Count(f => f.IsPrimaryKey);
            if (primaryKeyCount == 0)
                throw new InvalidOperationException("Model must have at least one primary key field");

            // Check for duplicate field names;
            var fieldNames = definition.Fields.Select(f => f.Name).ToList();
            var duplicateNames = fieldNames.GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateNames.Any())
                throw new InvalidOperationException($"Duplicate field names: {string.Join(", ", duplicateNames)}");
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Data Store not initialized. Call InitializeAsync first.");

            if (Status != StoreStatus.Running)
                throw new InvalidOperationException($"Data Store is not running. Current status: {Status}");
        }

        private string GenerateQueryId(string query, object parameters)
        {
            var queryHash = ComputeHash($"{query}_{JsonConvert.SerializeObject(parameters)}");
            return $"query_{queryHash}";
        }

        private string GenerateExecutionId()
        {
            return $"exec_{Guid.NewGuid():N}";
        }

        private string GenerateCommandId(string command, object parameters)
        {
            var commandHash = ComputeHash($"{command}_{JsonConvert.SerializeObject(parameters)}");
            return $"cmd_{commandHash}";
        }

        private string GenerateOperationId(string operationType, string target)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            return $"{operationType}_{target}_{timestamp}";
        }

        private string GenerateTransactionId()
        {
            return $"tx_{Guid.NewGuid():N}";
        }

        private string GenerateSearchId(string modelName, SearchCriteria criteria)
        {
            var criteriaHash = ComputeHash($"{modelName}_{JsonConvert.SerializeObject(criteria)}");
            return $"search_{criteriaHash}";
        }

        private string GenerateAggregateId(string modelName, AggregateCriteria criteria)
        {
            var criteriaHash = ComputeHash($"{modelName}_{JsonConvert.SerializeObject(criteria)}");
            return $"agg_{criteriaHash}";
        }

        private string GenerateIndexId(string modelName, IndexDefinition definition)
        {
            var definitionHash = ComputeHash($"{modelName}_{JsonConvert.SerializeObject(definition)}");
            return $"idx_{definitionHash}";
        }

        private string GenerateBackupId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            return $"backup_{timestamp}";
        }

        private string GenerateRestoreId()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            return $"restore_{timestamp}";
        }

        private string ComputeHash(string input)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash).Replace("/", "_").Replace("+", "-").Substring(0, 16);
        }

        private long EstimateSize<T>(IEnumerable<T> data)
        {
            if (data == null) return 0;

            try
            {
                var json = JsonConvert.SerializeObject(data);
                return Encoding.UTF8.GetByteCount(json);
            }
            catch
            {
                return 0;
            }
        }

        private DataTable ConvertToDataTable<T>(IEnumerable<T> data, string tableName)
        {
            var dataTable = new DataTable(tableName);

            if (!data.Any()) return dataTable;

            // Get properties of the first item;
            var properties = typeof(T).GetProperties();

            // Create columns;
            foreach (var prop in properties)
            {
                dataTable.Columns.Add(prop.Name, Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType);
            }

            // Add rows;
            foreach (var item in data)
            {
                var row = dataTable.NewRow();

                foreach (var prop in properties)
                {
                    var value = prop.GetValue(item) ?? DBNull.Value;
                    row[prop.Name] = value;
                }

                dataTable.Rows.Add(row);
            }

            return dataTable;
        }

        #endregion;

        #region Event Handlers;

        private void OnConnectionStateChanged(ConnectionStateChangedEventArgs e)
        {
            ConnectionStateChanged?.Invoke(this, e);
        }

        private void OnDataOperationCompleted(DataOperationEventArgs e)
        {
            DataOperationCompleted?.Invoke(this, e);
        }

        private void OnCacheEventOccurred(CacheEventEventArgs e)
        {
            CacheEventOccurred?.Invoke(this, e);
        }

        private void OnMigrationCompleted(MigrationEventArgs e)
        {
            MigrationCompleted?.Invoke(this, e);
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
                Status = StoreStatus.Stopping;

                _cleanupTimer?.Dispose();
                _healthCheckTimer?.Dispose();
                _migrationSemaphore?.Dispose();
                _redisConnection?.Dispose();

                // Close all connections;
                foreach (var connection in _connections.Values)
                {
                    connection.Dispose();
                }
                _connections.Clear();

                Status = StoreStatus.Stopped;
                _isDisposed = true;

                _logger.Info("Data Store disposed");
            }
        }

        ~DataStore()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    public interface IDataStore;
    {
        Task<QueryResult<T>> QueryAsync<T>(string query, object parameters = null, QueryOptions options = null, CancellationToken cancellationToken = default);
        Task<ExecuteResult> ExecuteAsync(string command, object parameters = null, CommandOptions options = null, CancellationToken cancellationToken = default);
        Task<BulkInsertResult> BulkInsertAsync<T>(string tableName, IEnumerable<T> data, BulkInsertOptions options = null, CancellationToken cancellationToken = default);
        Task<TransactionResult> ExecuteTransactionAsync(Func<IDbTransaction, Task> transactionAction, TransactionOptions options = null, CancellationToken cancellationToken = default);
        Task<DataModel> GetModelAsync(string modelName, CancellationToken cancellationToken = default);
        Task<DataModel> CreateModelAsync(string modelName, ModelDefinition definition, CancellationToken cancellationToken = default);
        Task<bool> UpdateModelAsync(string modelName, ModelUpdate update, CancellationToken cancellationToken = default);
        Task<SearchResult<T>> SearchAsync<T>(string modelName, SearchCriteria criteria, SearchOptions options = null, CancellationToken cancellationToken = default);
        Task<AggregateResult> AggregateAsync(string modelName, AggregateCriteria criteria, CancellationToken cancellationToken = default);
        Task<bool> CreateIndexAsync(string modelName, IndexDefinition definition, CancellationToken cancellationToken = default);
        Task<DataStoreMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
        Task<bool> ClearCacheAsync(CacheClearOptions options = null, CancellationToken cancellationToken = default);
        Task<bool> BackupAsync(string backupPath, BackupOptions options = null, CancellationToken cancellationToken = default);
        Task<bool> RestoreAsync(string backupPath, RestoreOptions options = null, CancellationToken cancellationToken = default);
    }

    public class DataStoreConfiguration;
    {
        public string PrimaryConnectionString { get; set; }
        public List<SecondaryConnectionConfig> SecondaryConnections { get; set; } = new List<SecondaryConnectionConfig>();
        public string RedisConnectionString { get; set; }
        public int DefaultQueryTimeout { get; set; } = 30; // seconds;
        public int DefaultCommandTimeout { get; set; } = 60; // seconds;
        public bool EnableQueryCaching { get; set; } = true;
        public bool EnableSearchCaching { get; set; } = true;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);
        public int MaxMemoryCacheEntries { get; set; } = 10000;
        public int MaxConnectionPoolSize { get; set; } = 100;
        public bool EnableQueryLogging { get; set; } = true;
        public bool EnablePerformanceMetrics { get; set; } = true;
        public string DataDirectory { get; set; } = "Data/Store";
    }

    public class SecondaryConnectionConfig;
    {
        public string Name { get; set; }
        public DatabaseType Type { get; set; }
        public string ConnectionString { get; set; }
        public ConnectionRole Role { get; set; } = ConnectionRole.ReadOnly;
    }

    public enum DatabaseType;
    {
        Relational,
        Document,
        Graph,
        KeyValue,
        TimeSeries;
    }

    public enum ConnectionRole;
    {
        ReadWrite,
        ReadOnly,
        WriteOnly,
        Backup;
    }

    public enum StoreStatus;
    {
        Stopped,
        Initializing,
        Running,
        Stopping,
        Error,
        Maintenance;
    }

    public class QueryOptions;
    {
        public CommandType? CommandType { get; set; }
        public int? TimeoutSeconds { get; set; }
        public bool? UseCache { get; set; } = true;
        public string CacheKey { get; set; }
        public TimeSpan? CacheDuration { get; set; }
    }

    public class CommandOptions;
    {
        public CommandType? CommandType { get; set; }
        public int? TimeoutSeconds { get; set; }
        public bool? InvalidateCache { get; set; } = true;
        public IsolationLevel? IsolationLevel { get; set; }
    }

    public class BulkInsertOptions;
    {
        public int? BatchSize { get; set; } = 1000;
        public int? TimeoutSeconds { get; set; } = 30;
        public bool CreateTableIfNotExists { get; set; } = false;
        public bool TruncateTable { get; set; } = false;
    }

    public class TransactionOptions;
    {
        public IsolationLevel? IsolationLevel { get; set; } = System.Data.IsolationLevel.ReadCommitted;
        public bool AutoCommit { get; set; } = true;
        public int? TimeoutSeconds { get; set; }
    }

    public class SearchOptions;
    {
        public string OrderBy { get; set; }
        public bool OrderDescending { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public bool? UseCache { get; set; } = true;
        public bool? UseIndex { get; set; } = true;
    }

    public class CacheClearOptions;
    {
        public bool ClearQueryCache { get; set; } = true;
        public bool ClearSearchCache { get; set; } = true;
        public bool ClearModelCache { get; set; } = true;
        public string Pattern { get; set; }
    }

    public class BackupOptions;
    {
        public bool IncludeCache { get; set; } = false;
        public bool Compress { get; set; } = true;
        public bool Verify { get; set; } = true;
        public string EncryptionKey { get; set; }
    }

    public class RestoreOptions;
    {
        public bool RestoreCache { get; set; } = false;
        public bool ClearExisting { get; set; } = true;
        public string DecryptionKey { get; set; }
    }

    public class QueryResult<T>
    {
        public string QueryId { get; set; }
        public string ExecutionId { get; set; }
        public bool IsSuccess { get; set; }
        public IEnumerable<T> Data { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
        public bool IsCached { get; set; }
        public long Size { get; set; }
    }

    public class ExecuteResult;
    {
        public string CommandId { get; set; }
        public string ExecutionId { get; set; }
        public bool IsSuccess { get; set; }
        public int RowsAffected { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
    }

    public class BulkInsertResult;
    {
        public bool IsSuccess { get; set; }
        public int RowsAffected { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
    }

    public class TransactionResult;
    {
        public string TransactionId { get; set; }
        public bool IsSuccess { get; set; }
        public bool IsCommitted { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
    }

    public class SearchResult<T>
    {
        public string SearchId { get; set; }
        public string ModelName { get; set; }
        public bool IsSuccess { get; set; }
        public IEnumerable<T> Data { get; set; }
        public int TotalCount { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
        public bool UsedIndex { get; set; }
        public string IndexName { get; set; }
    }

    public class AggregateResult;
    {
        public string AggregateId { get; set; }
        public string ModelName { get; set; }
        public bool IsSuccess { get; set; }
        public List<dynamic> Results { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public string Error { get; set; }
    }

    public class DataModel;
    {
        public string Name { get; }
        public ModelDefinition Definition { get; }
        public List<ModelField> Fields => Definition.Fields;
        public string Description => Definition.Description;
        public DateTime CreatedAt { get; }
        public DateTime UpdatedAt { get; private set; }

        public DataModel(string name, ModelDefinition definition)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }

        public DataModel ApplyUpdate(ModelUpdate update)
        {
            var newDefinition = new ModelDefinition;
            {
                Description = update.Description ?? Definition.Description,
                Fields = new List<ModelField>(Definition.Fields)
            };

            // Apply field updates;
            foreach (var fieldUpdate in update.FieldUpdates)
            {
                var existingField = newDefinition.Fields.FirstOrDefault(f => f.Name == fieldUpdate.FieldName);
                if (existingField != null)
                {
                    // Update existing field;
                    if (fieldUpdate.NewName != null)
                        existingField.Name = fieldUpdate.NewName;

                    if (fieldUpdate.NewType.HasValue)
                        existingField.Type = fieldUpdate.NewType.Value;

                    if (fieldUpdate.IsIndexed.HasValue)
                        existingField.IsIndexed = fieldUpdate.IsIndexed.Value;

                    if (fieldUpdate.IsRequired.HasValue)
                        existingField.IsRequired = fieldUpdate.IsRequired.Value;
                }
                else if (fieldUpdate.Operation == FieldOperation.Add)
                {
                    // Add new field;
                    newDefinition.Fields.Add(new ModelField;
                    {
                        Name = fieldUpdate.NewName ?? fieldUpdate.FieldName,
                        Type = fieldUpdate.NewType ?? FieldType.String,
                        IsIndexed = fieldUpdate.IsIndexed ?? false,
                        IsRequired = fieldUpdate.IsRequired ?? false;
                    });
                }
                else if (fieldUpdate.Operation == FieldOperation.Remove)
                {
                    // Remove field;
                    newDefinition.Fields.RemoveAll(f => f.Name == fieldUpdate.FieldName);
                }
            }

            UpdatedAt = DateTime.UtcNow;
            return new DataModel(Name, newDefinition);
        }
    }

    public class ModelDefinition;
    {
        public string Description { get; set; }
        public List<ModelField> Fields { get; set; } = new List<ModelField>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public class ModelField;
    {
        public string Name { get; set; }
        public FieldType Type { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsIndexed { get; set; }
        public bool IsRequired { get; set; }
        public object DefaultValue { get; set; }
        public int? MaxLength { get; set; }
        public string ValidationRule { get; set; }
    }

    public enum FieldType;
    {
        String,
        Integer,
        Decimal,
        Boolean,
        DateTime,
        Text,
        Json,
        Binary;
    }

    public class ModelUpdate;
    {
        public string Description { get; set; }
        public List<FieldUpdate> FieldUpdates { get; set; } = new List<FieldUpdate>();
        public bool RebuildIndices { get; set; } = true;
    }

    public class FieldUpdate;
    {
        public string FieldName { get; set; }
        public FieldOperation Operation { get; set; }
        public string NewName { get; set; }
        public FieldType? NewType { get; set; }
        public bool? IsIndexed { get; set; }
        public bool? IsRequired { get; set; }
        public object NewDefaultValue { get; set; }
    }

    public enum FieldOperation;
    {
        Add,
        Modify,
        Remove;
    }

    public class SearchCriteria;
    {
        public List<SearchCondition> Conditions { get; set; } = new List<SearchCondition>();
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class SearchCondition;
    {
        public string Field { get; set; }
        public string Operator { get; set; } // =, !=, >, <, >=, <=, LIKE, IN, etc.
        public object Value { get; set; }
        public string LogicalOperator { get; set; } = "AND"; // AND, OR;
    }

    public class AggregateCriteria;
    {
        public List<Aggregation> Aggregations { get; set; } = new List<Aggregation>();
        public List<SearchCondition> Conditions { get; set; } = new List<SearchCondition>();
        public string GroupBy { get; set; }
        public string Having { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class Aggregation;
    {
        public string Function { get; set; } // COUNT, SUM, AVG, MIN, MAX;
        public string Field { get; set; }
        public string Alias { get; set; }
    }

    public class DataIndex;
    {
        public string Name { get; }
        public string ModelName { get; }
        public IndexDefinition Definition { get; }
        public IndexStatus Status { get; set; }
        public DateTime CreatedAt { get; }
        public DateTime LastUpdated { get; set; }
        public long TotalEntries { get; set; }

        public DataIndex(string name, string modelName, IndexDefinition definition)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ModelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            CreatedAt = DateTime.UtcNow;
            LastUpdated = DateTime.UtcNow;
            Status = IndexStatus.Building;
        }
    }

    public class IndexDefinition;
    {
        public List<string> Fields { get; set; } = new List<string>();
        public bool IsUnique { get; set; }
        public IndexType Type { get; set; } = IndexType.BTree;
        public string FilterCondition { get; set; }
        public Dictionary<string, string> Options { get; set; } = new Dictionary<string, string>();
    }

    public enum IndexType;
    {
        BTree,
        Hash,
        FullText,
        Spatial,
        Bitmap;
    }

    public enum IndexStatus;
    {
        Building,
        Active,
        Rebuilding,
        Disabled,
        Error;
    }

    public class DataStoreMetrics;
    {
        public StoreStatus Status { get; set; }
        public TimeSpan Uptime { get; set; }
        public int TotalOperations { get; set; }
        public int TotalConnections { get; set; }
        public int TotalModels { get; set; }
        public int TotalIndices { get; set; }
        public double CacheHitRatio { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public long TotalCacheSize { get; set; }
        public List<ConnectionMetrics> ConnectionMetrics { get; set; } = new List<ConnectionMetrics>();
        public List<ModelMetrics> ModelMetrics { get; set; } = new List<ModelMetrics>();
        public DateTime Timestamp { get; set; }
    }

    public class ConnectionMetrics;
    {
        public string Name { get; set; }
        public DatabaseType Type { get; set; }
        public bool IsConnected { get; set; }
        public DateTime? LastActivity { get; set; }
        public int TotalQueries { get; set; }
        public int FailedQueries { get; set; }
        public TimeSpan AverageQueryTime { get; set; }
        public int ActiveConnections { get; set; }
    }

    public class ModelMetrics;
    {
        public string Name { get; set; }
        public long RowCount { get; set; }
        public int FieldCount { get; set; }
        public int IndexCount { get; set; }
        public long TotalSize { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    internal class DatabaseConnection : IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly ConcurrentBag<DbConnection> _connectionPool;
        private readonly SemaphoreSlim _poolSemaphore;
        private bool _isInitialized;

        public string Name { get; }
        public DatabaseType Type { get; }
        public bool IsConnected { get; private set; }
        public DateTime? LastActivity { get; private set; }
        public int TotalQueries { get; private set; }

        public DatabaseConnection(string name, DatabaseType type, string connectionString, ILogger logger)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type;
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _connectionPool = new ConcurrentBag<DbConnection>();
            _poolSemaphore = new SemaphoreSlim(10, 10); // Maximum 10 concurrent connections;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (_isInitialized)
                return;

            try
            {
                // Test connection;
                await using (var testConnection = CreateConnection())
                {
                    await testConnection.OpenAsync(cancellationToken);
                    IsConnected = true;
                }

                // Initialize connection pool;
                for (int i = 0; i < 5; i++) // Initial pool size;
                {
                    var connection = CreateConnection();
                    _connectionPool.Add(connection);
                }

                _isInitialized = true;
                LastActivity = DateTime.UtcNow;

                _logger.Debug($"Database connection '{Name}' initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize database connection '{Name}': {ex.Message}", ex);
                IsConnected = false;
                throw;
            }
        }

        public async Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {
            await _poolSemaphore.WaitAsync(cancellationToken);

            try
            {
                if (!_connectionPool.TryTake(out var connection))
                {
                    connection = CreateConnection();
                }

                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken);
                }

                LastActivity = DateTime.UtcNow;
                return new PooledConnection(connection, this);
            }
            catch
            {
                _poolSemaphore.Release();
                throw;
            }
        }

        public void ReturnConnection(DbConnection connection)
        {
            if (connection != null && connection.State == ConnectionState.Open)
            {
                _connectionPool.Add(connection);
            }
            else;
            {
                connection?.Dispose();
            }

            _poolSemaphore.Release();
        }

        public async Task ReconnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.Info($"Reconnecting database connection '{Name}'...");

                // Clear existing connections;
                while (_connectionPool.TryTake(out var connection))
                {
                    connection.Dispose();
                }

                // Test new connection;
                await using (var testConnection = CreateConnection())
                {
                    await testConnection.OpenAsync(cancellationToken);
                    IsConnected = true;
                }

                // Reinitialize pool;
                for (int i = 0; i < 5; i++)
                {
                    var connection = CreateConnection();
                    _connectionPool.Add(connection);
                }

                LastActivity = DateTime.UtcNow;
                _logger.Info($"Database connection '{Name}' reconnected successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to reconnect database connection '{Name}': {ex.Message}", ex);
                IsConnected = false;
                throw;
            }
        }

        public void Close()
        {
            while (_connectionPool.TryTake(out var connection))
            {
                connection.Dispose();
            }

            IsConnected = false;
        }

        private DbConnection CreateConnection()
        {
            return Type switch;
            {
                DatabaseType.Relational => new SqlConnection(_connectionString),
                // Add other database types as needed;
                _ => throw new NotSupportedException($"Database type {Type} is not supported")
            };
        }

        public void Dispose()
        {
            Close();
            _poolSemaphore.Dispose();
        }

        private class PooledConnection : DbConnection;
        {
            private readonly DbConnection _innerConnection;
            private readonly DatabaseConnection _parent;

            public PooledConnection(DbConnection innerConnection, DatabaseConnection parent)
            {
                _innerConnection = innerConnection ?? throw new ArgumentNullException(nameof(innerConnection));
                _parent = parent ?? throw new ArgumentNullException(nameof(parent));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _parent.ReturnConnection(_innerConnection);
                }
            }

            // Implement all abstract members by delegating to _innerConnection;
            public override string ConnectionString;
            {
                get => _innerConnection.ConnectionString;
                set => _innerConnection.ConnectionString = value;
            }

            public override string Database => _innerConnection.Database;
            public override ConnectionState State => _innerConnection.State;
            public override string DataSource => _innerConnection.DataSource;
            public override string ServerVersion => _innerConnection.ServerVersion;

            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
                => _innerConnection.BeginTransaction(isolationLevel);

            public override void ChangeDatabase(string databaseName)
                => _innerConnection.ChangeDatabase(databaseName);

            public override void Close()
                => _parent.ReturnConnection(_innerConnection);

            public override void Open()
                => throw new InvalidOperationException("Use OpenAsync instead");

            protected override DbCommand CreateDbCommand()
                => _innerConnection.CreateCommand();
        }
    }

    internal class QueryCache;
    {
        public string Key { get; set; }
        public string Data { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime LastAccessed { get; set; }
        public long Size { get; set; }
    }

    internal class CachedQueryResult<T>
    {
        public IEnumerable<T> Data { get; set; }
        public DateTime ExpiresAt { get; set; }
        public long Size { get; set; }
    }

    public class ConnectionStateChangedEventArgs : EventArgs;
    {
        public string ConnectionName { get; set; }
        public bool IsConnected { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class DataOperationEventArgs : EventArgs;
    {
        public string OperationId { get; set; }
        public DataOperationType OperationType { get; set; }
        public string Query { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
        public int RowsAffected { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum DataOperationType;
    {
        Query,
        Execute,
        Insert,
        Update,
        Delete,
        Transaction;
    }

    public class CacheEventEventArgs : EventArgs;
    {
        public CacheEventType EventType { get; set; }
        public string Key { get; set; }
        public long Size { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum CacheEventType;
    {
        Hit,
        Miss,
        Set,
        Delete,
        Expired;
    }

    public class MigrationEventArgs : EventArgs;
    {
        public string ModelName { get; set; }
        public MigrationType MigrationType { get; set; }
        public bool Success { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum MigrationType;
    {
        ModelCreate,
        ModelUpdate,
        ModelDelete,
        IndexCreate,
        IndexRebuild,
        SchemaUpdate;
    }

    // Exceptions;
    public class DataStoreException : Exception
    {
        public DataStoreException(string message) : base(message) { }
        public DataStoreException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class DataStoreInitializationException : DataStoreException;
    {
        public DataStoreInitializationException(string message) : base(message) { }
        public DataStoreInitializationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ConfigurationException : DataStoreException;
    {
        public ConfigurationException(string message) : base(message) { }
    }

    public class QueryExecutionException : DataStoreException;
    {
        public QueryExecutionException(string message) : base(message) { }
        public QueryExecutionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class CommandExecutionException : DataStoreException;
    {
        public CommandExecutionException(string message) : base(message) { }
        public CommandExecutionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class BulkInsertException : DataStoreException;
    {
        public BulkInsertException(string message) : base(message) { }
        public BulkInsertException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ModelNotFoundException : DataStoreException;
    {
        public ModelNotFoundException(string message) : base(message) { }
    }

    public class ModelCreationException : DataStoreException;
    {
        public ModelCreationException(string message) : base(message) { }
        public ModelCreationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ConnectionNotFoundException : DataStoreException;
    {
        public ConnectionNotFoundException(string message) : base(message) { }
    }

    public class IndexNotFoundException : DataStoreException;
    {
        public IndexNotFoundException(string message) : base(message) { }
    }

    #endregion;
}
