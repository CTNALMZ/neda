using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.KnowledgeBase.DataManagement.Configuration;
using NEDA.KnowledgeBase.DataManagement.DataAccess;
using NEDA.KnowledgeBase.DataManagement.Entities;
using NEDA.KnowledgeBase.DataManagement.Repositories;
using NEDA.KnowledgeBase.DataManagement.SeedData;
using NEDA.KnowledgeBase.DataManagement.Models;
using NEDA.KnowledgeBase.DataManagement.Specifications;
using NEDA.KnowledgeBase.DataManagement.Exceptions;
using NEDA.KnowledgeBase.DataManagement.Caching;
using NEDA.KnowledgeBase.DataManagement.Indexing;
using NEDA.KnowledgeBase.DataManagement.Validation;

namespace NEDA.KnowledgeBase.DataManagement;
{
    /// <summary>
    /// Central data management service that orchestrates all data operations,
    /// provides high-level abstractions, and ensures data consistency across the system;
    /// </summary>
    public class DataManager : IDataManager, IDisposable;
    {
        #region Private Fields;

        private readonly IDataContext _dataContext;
        private readonly ILogger<DataManager> _logger;
        private readonly DataManagementConfig _config;
        private readonly IServiceProvider _serviceProvider;
        private readonly IDataIndexer _dataIndexer;
        private readonly IQueryCacheManager _cacheManager;
        private readonly IDataValidator _dataValidator;
        private readonly IDataSeeder _dataSeeder;
        private readonly SemaphoreSlim _operationSemaphore;
        private readonly Dictionary<Type, object> _repositories;
        private readonly List<IDisposable> _disposableResources;
        private bool _isDisposed;
        private readonly object _repositoryLock = new object();

        #endregion;

        #region Public Properties;

        /// <summary>
        /// Gets the current data context;
        /// </summary>
        public IDataContext DataContext => _dataContext;

        /// <summary>
        /// Gets whether the data manager is initialized;
        /// </summary>
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Gets the current operation mode;
        /// </summary>
        public DataOperationMode OperationMode { get; private set; }

        /// <summary>
        /// Gets data management statistics;
        /// </summary>
        public DataManagerStatistics Statistics { get; private set; }

        /// <summary>
        /// Gets the configuration;
        /// </summary>
        public DataManagementConfig Configuration => _config;

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when data operation starts;
        /// </summary>
        public event EventHandler<DataOperationStartedEventArgs> OperationStarted;

        /// <summary>
        /// Event raised when data operation completes;
        /// </summary>
        public event EventHandler<DataOperationCompletedEventArgs> OperationCompleted;

        /// <summary>
        /// Event raised when data operation fails;
        /// </summary>
        public event EventHandler<DataOperationFailedEventArgs> OperationFailed;

        /// <summary>
        /// Event raised when data is indexed;
        /// </summary>
        public event EventHandler<DataIndexedEventArgs> DataIndexed;

        /// <summary>
        /// Event raised when cache is updated;
        /// </summary>
        public event EventHandler<CacheUpdatedEventArgs> CacheUpdated;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of the DataManager;
        /// </summary>
        /// <param name="dataContext">Data context</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="config">Data management configuration</param>
        /// <param name="serviceProvider">Service provider</param>
        /// <param name="dataIndexer">Data indexer</param>
        /// <param name="cacheManager">Cache manager</param>
        /// <param name="dataValidator">Data validator</param>
        /// <param name="dataSeeder">Data seeder</param>
        public DataManager(
            IDataContext dataContext,
            ILogger<DataManager> logger,
            IOptions<DataManagementConfig> config,
            IServiceProvider serviceProvider,
            IDataIndexer dataIndexer = null,
            IQueryCacheManager cacheManager = null,
            IDataValidator dataValidator = null,
            IDataSeeder dataSeeder = null)
        {
            _dataContext = dataContext ?? throw new ArgumentNullException(nameof(dataContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _dataIndexer = dataIndexer;
            _cacheManager = cacheManager;
            _dataValidator = dataValidator;
            _dataSeeder = dataSeeder;

            _operationSemaphore = new SemaphoreSlim(_config.MaxConcurrentOperations);
            _repositories = new Dictionary<Type, object>();
            _disposableResources = new List<IDisposable>();

            Statistics = new DataManagerStatistics();
            OperationMode = DataOperationMode.Normal;

            _logger.LogInformation("DataManager initialized");
        }

        #endregion;

        #region Initialization;

        /// <summary>
        /// Initializes the data manager and performs system checks;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (IsInitialized)
            {
                _logger.LogWarning("DataManager is already initialized");
                return;
            }

            try
            {
                _logger.LogInformation("Initializing DataManager...");

                // Check database connection;
                if (!await _dataContext.TestConnectionAsync(cancellationToken))
                {
                    throw new DataManagementException("Cannot connect to database");
                }

                // Run database migrations if enabled;
                if (_config.EnableAutoMigrations)
                {
                    await MigrateDatabaseAsync(cancellationToken);
                }

                // Initialize data indexer if available;
                if (_dataIndexer != null && _config.EnableIndexing)
                {
                    await _dataIndexer.InitializeAsync(cancellationToken);
                }

                // Seed initial data if configured;
                if (_config.EnableAutoSeeding && _dataSeeder != null)
                {
                    await _dataSeeder.SeedAsync(cancellationToken);
                }

                // Warm up caches if configured;
                if (_config.EnableCacheWarmup && _cacheManager != null)
                {
                    await WarmupCachesAsync(cancellationToken);
                }

                IsInitialized = true;
                OperationMode = DataOperationMode.Normal;

                _logger.LogInformation("DataManager initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize DataManager");
                throw new DataManagementException("Failed to initialize DataManager", ex);
            }
        }

        /// <summary>
        /// Migrates database to latest version;
        /// </summary>
        private async Task MigrateDatabaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Running database migrations...");
                await _dataContext.MigrateAsync(cancellationToken);
                _logger.LogInformation("Database migrations completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database migration failed");
                if (_config.FailOnMigrationError)
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Warms up caches for better performance;
        /// </summary>
        private async Task WarmupCachesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Warming up caches...");

                // Warm up frequently accessed data;
                var warmupTasks = new List<Task>();

                if (_config.CacheWarmupEntities.Contains("KnowledgeArticles"))
                {
                    warmupTasks.Add(WarmupKnowledgeArticlesCacheAsync(cancellationToken));
                }

                if (_config.CacheWarmupEntities.Contains("SystemSettings"))
                {
                    warmupTasks.Add(WarmupSystemSettingsCacheAsync(cancellationToken));
                }

                await Task.WhenAll(warmupTasks);

                _logger.LogInformation("Cache warmup completed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cache warmup failed");
            }
        }

        /// <summary>
        /// Warms up knowledge articles cache;
        /// </summary>
        private async Task WarmupKnowledgeArticlesCacheAsync(CancellationToken cancellationToken)
        {
            var repository = GetRepository<KnowledgeArticle, Guid>();
            var articles = await repository.GetAllAsync(
                predicate: a => a.IsFeatured,
                cancellationToken: cancellationToken);

            _logger.LogDebug("Warmed up knowledge articles cache with {Count} featured articles", articles.Count);
        }

        /// <summary>
        /// Warms up system settings cache;
        /// </summary>
        private async Task WarmupSystemSettingsCacheAsync(CancellationToken cancellationToken)
        {
            var repository = GetRepository<SystemSetting, Guid>();
            var settings = await repository.GetAllAsync(cancellationToken: cancellationToken);

            _logger.LogDebug("Warmed up system settings cache with {Count} settings", settings.Count);
        }

        #endregion;

        #region Repository Management;

        /// <summary>
        /// Gets a repository for the specified entity type;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        public IRepository<TEntity, TKey> GetRepository<TEntity, TKey>()
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            var entityType = typeof(TEntity);

            lock (_repositoryLock)
            {
                if (!_repositories.TryGetValue(entityType, out var repository))
                {
                    repository = CreateRepository<TEntity, TKey>();
                    _repositories[entityType] = repository;

                    _logger.LogDebug("Created repository for entity type: {EntityType}", entityType.Name);
                }

                return (IRepository<TEntity, TKey>)repository;
            }
        }

        /// <summary>
        /// Gets a repository for entities with Guid keys;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        public IRepository<TEntity> GetRepository<TEntity>()
            where TEntity : class, IEntity<Guid>
        {
            return GetRepository<TEntity, Guid>();
        }

        /// <summary>
        /// Creates a new repository instance;
        /// </summary>
        private IRepository<TEntity, TKey> CreateRepository<TEntity, TKey>()
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            var repositoryType = typeof(DataRepository<,>).MakeGenericType(typeof(TEntity), typeof(TKey));

            // Get metrics service if configured;
            IRepositoryMetrics metrics = null;
            if (_config.EnableRepositoryMetrics)
            {
                metrics = _serviceProvider.GetService(typeof(IRepositoryMetrics)) as IRepositoryMetrics;
            }

            var repository = Activator.CreateInstance(
                repositoryType,
                _dataContext,
                _logger,
                _cacheManager,
                metrics) as IRepository<TEntity, TKey>;

            if (repository is IDisposable disposableRepository)
            {
                _disposableResources.Add(disposableRepository);
            }

            return repository;
        }

        /// <summary>
        /// Registers a custom repository;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TRepository">Repository type</typeparam>
        public void RegisterRepository<TEntity, TRepository>(TRepository repository)
            where TRepository : class;
        {
            if (repository == null)
                throw new ArgumentNullException(nameof(repository));

            var entityType = typeof(TEntity);

            lock (_repositoryLock)
            {
                if (_repositories.ContainsKey(entityType))
                {
                    _logger.LogWarning("Repository for entity type {EntityType} already exists, replacing", entityType.Name);
                }

                _repositories[entityType] = repository;

                if (repository is IDisposable disposable)
                {
                    _disposableResources.Add(disposable);
                }

                _logger.LogDebug("Registered custom repository for entity type: {EntityType}", entityType.Name);
            }
        }

        /// <summary>
        /// Clears all repository caches;
        /// </summary>
        public async Task ClearRepositoryCachesAsync(CancellationToken cancellationToken = default)
        {
            if (_cacheManager == null)
                return;

            try
            {
                _logger.LogInformation("Clearing repository caches...");
                await _cacheManager.ClearAllAsync(cancellationToken);
                _logger.LogInformation("Repository caches cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear repository caches");
            }
        }

        #endregion;

        #region High-Level Operations;

        /// <summary>
        /// Executes an operation within a managed context;
        /// </summary>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="operation">Operation to execute</param>
        /// <param name="operationName">Operation name for logging</param>
        /// <param name="requiresTransaction">Whether the operation requires a transaction</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<TResult> ExecuteOperationAsync<TResult>(
            Func<Task<TResult>> operation,
            string operationName = null,
            bool requiresTransaction = false,
            CancellationToken cancellationToken = default)
        {
            operationName ??= $"Operation_{Guid.NewGuid():N}";

            await _operationSemaphore.WaitAsync(cancellationToken);

            var operationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                Statistics.OperationStarted();
                OnOperationStarted(new DataOperationStartedEventArgs(operationId, operationName, startTime));

                _logger.LogDebug("Starting operation: {OperationName} (ID: {OperationId})", operationName, operationId);

                TResult result;

                if (requiresTransaction)
                {
                    result = await ExecuteInTransactionAsync(operation, operationName, cancellationToken);
                }
                else;
                {
                    result = await operation();
                }

                var duration = DateTime.UtcNow - startTime;
                Statistics.OperationCompleted(duration);

                OnOperationCompleted(new DataOperationCompletedEventArgs(
                    operationId, operationName, startTime, duration, result));

                _logger.LogDebug("Operation completed: {OperationName} (Duration: {Duration}ms)",
                    operationName, duration.TotalMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                var duration = DateTime.UtcNow - startTime;
                Statistics.OperationFailed(duration);

                OnOperationFailed(new DataOperationFailedEventArgs(
                    operationId, operationName, startTime, duration, ex));

                _logger.LogError(ex, "Operation failed: {OperationName} (Duration: {Duration}ms)",
                    operationName, duration.TotalMilliseconds);

                throw;
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        /// <summary>
        /// Executes multiple operations in batch;
        /// </summary>
        /// <typeparam name="TResult">Result type</typeparam>
        /// <param name="operations">Operations to execute</param>
        /// <param name="batchSize">Batch size for processing</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<IReadOnlyList<TResult>> ExecuteBatchAsync<TResult>(
            IEnumerable<Func<Task<TResult>>> operations,
            int batchSize = 10,
            CancellationToken cancellationToken = default)
        {
            if (operations == null)
                throw new ArgumentNullException(nameof(operations));

            var operationList = operations.ToList();
            if (!operationList.Any())
                return new List<TResult>().AsReadOnly();

            var results = new List<TResult>();
            var batches = operationList.Chunk(batchSize);

            _logger.LogInformation("Executing batch of {Total} operations in batches of {BatchSize}",
                operationList.Count, batchSize);

            foreach (var batch in batches)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var batchTasks = batch.Select(op =>
                    ExecuteOperationAsync(op, "BatchOperation", false, cancellationToken));

                var batchResults = await Task.WhenAll(batchTasks);
                results.AddRange(batchResults);

                _logger.LogDebug("Completed batch with {Count} operations", batch.Length);
            }

            return results.AsReadOnly();
        }

        /// <summary>
        /// Executes an operation within a transaction;
        /// </summary>
        private async Task<TResult> ExecuteInTransactionAsync<TResult>(
            Func<Task<TResult>> operation,
            string operationName,
            CancellationToken cancellationToken)
        {
            await using var transaction = await _dataContext.BeginTransactionAsync();

            try
            {
                var result = await operation();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        #endregion;

        #region Data Query Operations;

        /// <summary>
        /// Performs a complex query with filtering, sorting, and pagination;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="specification">Query specification</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<PagedResult<TEntity>> QueryAsync<TEntity, TKey>(
            ISpecification<TEntity> specification,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            return await ExecuteOperationAsync(async () =>
            {
                var repository = GetRepository<TEntity, TKey>();
                return await repository.GetPagedAsync(specification, cancellationToken);
            }, $"Query_{typeof(TEntity).Name}", false, cancellationToken);
        }

        /// <summary>
        /// Searches entities using full-text search;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="searchTerm">Search term</param>
        /// <param name="pageNumber">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<PagedResult<TEntity>> SearchAsync<TEntity, TKey>(
            string searchTerm,
            int pageNumber = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                throw new ArgumentException("Search term cannot be empty", nameof(searchTerm));

            return await ExecuteOperationAsync(async () =>
            {
                // Use indexer if available;
                if (_dataIndexer != null && _config.EnableIndexing)
                {
                    var searchResults = await _dataIndexer.SearchAsync<TEntity>(
                        searchTerm, pageNumber, pageSize, cancellationToken);

                    if (searchResults != null && searchResults.Items.Any())
                    {
                        return searchResults;
                    }
                }

                // Fallback to repository search;
                var repository = GetRepository<TEntity, TKey>();
                var specification = CreateSearchSpecification<TEntity>(searchTerm, pageNumber, pageSize);
                return await repository.GetPagedAsync(specification, cancellationToken);
            }, $"Search_{typeof(TEntity).Name}", false, cancellationToken);
        }

        /// <summary>
        /// Creates search specification for repository fallback;
        /// </summary>
        private ISpecification<TEntity> CreateSearchSpecification<TEntity>(
            string searchTerm,
            int pageNumber,
            int pageSize)
        {
            // This would be implemented based on entity-specific search logic;
            // For now, return a null specification (override in derived classes)
            return null;
        }

        /// <summary>
        /// Gets related entities with eager loading;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="entityId">Entity id</param>
        /// <param name="includeProperties">Properties to include</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<TEntity> GetWithRelatedAsync<TEntity, TKey>(
            TKey entityId,
            string[] includeProperties = null,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            return await ExecuteOperationAsync(async () =>
            {
                var repository = GetRepository<TEntity, TKey>();

                // Convert include properties to expressions;
                var includeExpressions = includeProperties?
                    .Select(prop => CreateIncludeExpression<TEntity>(prop))
                    .ToArray();

                return await repository.GetByIdAsync(entityId, cancellationToken, includeExpressions);
            }, $"GetWithRelated_{typeof(TEntity).Name}", false, cancellationToken);
        }

        /// <summary>
        /// Creates include expression from property path;
        /// </summary>
        private Expression<Func<TEntity, object>> CreateIncludeExpression<TEntity>(string propertyPath)
        {
            var parameter = Expression.Parameter(typeof(TEntity), "x");
            Expression property = parameter;

            foreach (var member in propertyPath.Split('.'))
            {
                property = Expression.PropertyOrField(property, member);
            }

            var lambda = Expression.Lambda<Func<TEntity, object>>(
                Expression.Convert(property, typeof(object)), parameter);

            return lambda;
        }

        #endregion;

        #region Data Modification Operations;

        /// <summary>
        /// Creates a new entity with validation;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="entity">Entity to create</param>
        /// <param name="validate">Whether to validate the entity</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<TEntity> CreateAsync<TEntity, TKey>(
            TEntity entity,
            bool validate = true,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            return await ExecuteOperationAsync(async () =>
            {
                // Validate entity if requested;
                if (validate && _dataValidator != null)
                {
                    var validationResult = await _dataValidator.ValidateAsync(entity, cancellationToken);
                    if (!validationResult.IsValid)
                    {
                        throw new DataValidationException("Entity validation failed", validationResult.Errors);
                    }
                }

                var repository = GetRepository<TEntity, TKey>();
                var createdEntity = await repository.AddAsync(entity, cancellationToken);

                // Index the entity if indexing is enabled;
                if (_dataIndexer != null && _config.EnableIndexing)
                {
                    await IndexEntityAsync(createdEntity, cancellationToken);
                }

                // Update cache;
                await UpdateEntityCacheAsync(createdEntity, cancellationToken);

                Statistics.EntityCreated();

                return createdEntity;
            }, $"Create_{typeof(TEntity).Name}", true, cancellationToken);
        }

        /// <summary>
        /// Updates an existing entity;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="entityId">Entity id</param>
        /// <param name="updateAction">Update action</param>
        /// <param name="validate">Whether to validate after update</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<TEntity> UpdateAsync<TEntity, TKey>(
            TKey entityId,
            Action<TEntity> updateAction,
            bool validate = true,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            if (updateAction == null)
                throw new ArgumentNullException(nameof(updateAction));

            return await ExecuteOperationAsync(async () =>
            {
                var repository = GetRepository<TEntity, TKey>();
                var entity = await repository.GetByIdAsync(entityId, cancellationToken);

                if (entity == null)
                {
                    throw new EntityNotFoundException(typeof(TEntity).Name, entityId.ToString());
                }

                // Apply update;
                updateAction(entity);

                // Validate if requested;
                if (validate && _dataValidator != null)
                {
                    var validationResult = await _dataValidator.ValidateAsync(entity, cancellationToken);
                    if (!validationResult.IsValid)
                    {
                        throw new DataValidationException("Entity validation failed after update", validationResult.Errors);
                    }
                }

                var updatedEntity = await repository.UpdateAsync(entity, cancellationToken);

                // Re-index the entity;
                if (_dataIndexer != null && _config.EnableIndexing)
                {
                    await IndexEntityAsync(updatedEntity, cancellationToken);
                }

                // Update cache;
                await UpdateEntityCacheAsync(updatedEntity, cancellationToken);

                Statistics.EntityUpdated();

                return updatedEntity;
            }, $"Update_{typeof(TEntity).Name}", true, cancellationToken);
        }

        /// <summary>
        /// Deletes an entity;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="entityId">Entity id</param>
        /// <param name="permanent">Whether to delete permanently</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<bool> DeleteAsync<TEntity, TKey>(
            TKey entityId,
            bool permanent = false,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            return await ExecuteOperationAsync(async () =>
            {
                var repository = GetRepository<TEntity, TKey>();
                bool deleted;

                if (permanent)
                {
                    deleted = await repository.PermanentDeleteAsync(entityId, cancellationToken);
                }
                else;
                {
                    deleted = await repository.DeleteAsync(entityId, cancellationToken);
                }

                if (deleted)
                {
                    // Remove from index;
                    if (_dataIndexer != null && _config.EnableIndexing)
                    {
                        await _dataIndexer.RemoveAsync<TEntity>(entityId, cancellationToken);
                    }

                    // Remove from cache;
                    await RemoveEntityFromCacheAsync<TEntity, TKey>(entityId, cancellationToken);

                    Statistics.EntityDeleted(permanent);
                }

                return deleted;
            }, $"Delete_{typeof(TEntity).Name}", true, cancellationToken);
        }

        /// <summary>
        /// Creates multiple entities in batch;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="entities">Entities to create</param>
        /// <param name="batchSize">Batch size for processing</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<IReadOnlyList<TEntity>> CreateBatchAsync<TEntity, TKey>(
            IEnumerable<TEntity> entities,
            int batchSize = 100,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            if (entities == null)
                throw new ArgumentNullException(nameof(entities));

            var entityList = entities.ToList();
            if (!entityList.Any())
                return new List<TEntity>().AsReadOnly();

            return await ExecuteOperationAsync(async () =>
            {
                var repository = GetRepository<TEntity, TKey>();
                var createdEntities = new List<TEntity>();

                // Process in batches;
                var batches = entityList.Chunk(batchSize);

                foreach (var batch in batches)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var batchEntities = await repository.AddRangeAsync(batch, cancellationToken);
                    createdEntities.AddRange(batchEntities);

                    // Index batch entities;
                    if (_dataIndexer != null && _config.EnableIndexing)
                    {
                        await IndexEntitiesAsync(batchEntities, cancellationToken);
                    }

                    Statistics.BatchCreated(batch.Length);

                    _logger.LogDebug("Created batch of {Count} {EntityType} entities",
                        batch.Length, typeof(TEntity).Name);
                }

                return createdEntities.AsReadOnly();
            }, $"CreateBatch_{typeof(TEntity).Name}", true, cancellationToken);
        }

        #endregion;

        #region Indexing Operations;

        /// <summary>
        /// Indexes an entity for search;
        /// </summary>
        private async Task IndexEntityAsync<TEntity>(TEntity entity, CancellationToken cancellationToken)
            where TEntity : class;
        {
            try
            {
                await _dataIndexer.IndexAsync(entity, cancellationToken);

                OnDataIndexed(new DataIndexedEventArgs(
                    typeof(TEntity).Name,
                    1,
                    DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index entity of type {EntityType}", typeof(TEntity).Name);
            }
        }

        /// <summary>
        /// Indexes multiple entities;
        /// </summary>
        private async Task IndexEntitiesAsync<TEntity>(
            IEnumerable<TEntity> entities,
            CancellationToken cancellationToken)
            where TEntity : class;
        {
            try
            {
                var entityList = entities.ToList();
                if (!entityList.Any())
                    return;

                await _dataIndexer.IndexBatchAsync(entityList, cancellationToken);

                OnDataIndexed(new DataIndexedEventArgs(
                    typeof(TEntity).Name,
                    entityList.Count,
                    DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index {Count} entities of type {EntityType}",
                    entities.Count(), typeof(TEntity).Name);
            }
        }

        /// <summary>
        /// Rebuilds index for all entities of a type;
        /// </summary>
        /// <typeparam name="TEntity">Entity type</typeparam>
        /// <typeparam name="TKey">Entity key type</typeparam>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task RebuildIndexAsync<TEntity, TKey>(CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>
        {
            if (_dataIndexer == null)
                throw new InvalidOperationException("Data indexer is not configured");

            await ExecuteOperationAsync(async () =>
            {
                _logger.LogInformation("Rebuilding index for {EntityType}...", typeof(TEntity).Name);

                var repository = GetRepository<TEntity, TKey>();
                var allEntities = await repository.GetAllAsync(cancellationToken: cancellationToken);

                await _dataIndexer.RebuildIndexAsync(allEntities, cancellationToken);

                _logger.LogInformation("Index rebuilt for {Count} {EntityType} entities",
                    allEntities.Count, typeof(TEntity).Name);

                return allEntities.Count;
            }, $"RebuildIndex_{typeof(TEntity).Name}", false, cancellationToken);
        }

        #endregion;

        #region Caching Operations;

        /// <summary>
        /// Updates entity cache;
        /// </summary>
        private async Task UpdateEntityCacheAsync<TEntity>(TEntity entity, CancellationToken cancellationToken)
            where TEntity : class;
        {
            if (_cacheManager == null)
                return;

            try
            {
                var cacheKey = $"entity_{typeof(TEntity).Name}_{GetEntityId(entity)}";
                await _cacheManager.SetAsync(cacheKey, entity, TimeSpan.FromHours(1), cancellationToken);

                OnCacheUpdated(new CacheUpdatedEventArgs(cacheKey, "Update", DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update cache for entity of type {EntityType}", typeof(TEntity).Name);
            }
        }

        /// <summary>
        /// Removes entity from cache;
        /// </summary>
        private async Task RemoveEntityFromCacheAsync<TEntity, TKey>(TKey entityId, CancellationToken cancellationToken)
            where TEntity : class;
        {
            if (_cacheManager == null)
                return;

            try
            {
                var cacheKey = $"entity_{typeof(TEntity).Name}_{entityId}";
                await _cacheManager.RemoveAsync(cacheKey, cancellationToken);

                OnCacheUpdated(new CacheUpdatedEventArgs(cacheKey, "Remove", DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove entity from cache: {EntityType}/{EntityId}",
                    typeof(TEntity).Name, entityId);
            }
        }

        /// <summary>
        /// Gets entity id for caching;
        /// </summary>
        private object GetEntityId<TEntity>(TEntity entity)
            where TEntity : class;
        {
            var idProperty = typeof(TEntity).GetProperty("Id");
            return idProperty?.GetValue(entity);
        }

        /// <summary>
        /// Clears all caches;
        /// </summary>
        public async Task ClearAllCachesAsync(CancellationToken cancellationToken = default)
        {
            if (_cacheManager == null)
                return;

            await ExecuteOperationAsync(async () =>
            {
                _logger.LogInformation("Clearing all caches...");
                await _cacheManager.ClearAllAsync(cancellationToken);
                _logger.LogInformation("All caches cleared");
                return true;
            }, "ClearAllCaches", false, cancellationToken);
        }

        #endregion;

        #region Data Integrity Operations;

        /// <summary>
        /// Validates data integrity;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<DataIntegrityReport> ValidateDataIntegrityAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteOperationAsync(async () =>
            {
                _logger.LogInformation("Validating data integrity...");

                var report = new DataIntegrityReport;
                {
                    Timestamp = DateTime.UtcNow,
                    Checks = new List<DataIntegrityCheck>()
                };

                // Check knowledge articles;
                report.Checks.Add(await ValidateKnowledgeArticlesAsync(cancellationToken));

                // Check system settings;
                report.Checks.Add(await ValidateSystemSettingsAsync(cancellationToken));

                // Check database consistency;
                report.Checks.Add(await ValidateDatabaseConsistencyAsync(cancellationToken));

                // Calculate overall status;
                report.Status = report.Checks.All(c => c.Status == DataIntegrityStatus.Healthy)
                    ? DataIntegrityStatus.Healthy;
                    : DataIntegrityStatus.Warning;

                report.Duration = DateTime.UtcNow - report.Timestamp;

                _logger.LogInformation("Data integrity validation completed with status: {Status}", report.Status);

                return report;
            }, "ValidateDataIntegrity", false, cancellationToken);
        }

        /// <summary>
        /// Validates knowledge articles;
        /// </summary>
        private async Task<DataIntegrityCheck> ValidateKnowledgeArticlesAsync(CancellationToken cancellationToken)
        {
            var check = new DataIntegrityCheck;
            {
                Name = "KnowledgeArticles",
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                var repository = GetRepository<KnowledgeArticle, Guid>();
                var articles = await repository.GetAllAsync(cancellationToken: cancellationToken);

                check.TotalCount = articles.Count;
                check.InvalidCount = articles.Count(a =>
                    string.IsNullOrWhiteSpace(a.Title) ||
                    string.IsNullOrWhiteSpace(a.Content));

                check.Status = check.InvalidCount == 0;
                    ? DataIntegrityStatus.Healthy;
                    : DataIntegrityStatus.Warning;

                check.Message = $"Found {check.TotalCount} articles, {check.InvalidCount} invalid";
            }
            catch (Exception ex)
            {
                check.Status = DataIntegrityStatus.Error;
                check.Message = $"Validation failed: {ex.Message}";
                check.Error = ex;
            }

            return check;
        }

        /// <summary>
        /// Validates system settings;
        /// </summary>
        private async Task<DataIntegrityCheck> ValidateSystemSettingsAsync(CancellationToken cancellationToken)
        {
            var check = new DataIntegrityCheck;
            {
                Name = "SystemSettings",
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                var repository = GetRepository<SystemSetting, Guid>();
                var settings = await repository.GetAllAsync(cancellationToken: cancellationToken);

                check.TotalCount = settings.Count;

                // Check for required settings;
                var requiredSettings = new[] { "System.Version", "System.Name" };
                var missingSettings = requiredSettings.Except(settings.Select(s => s.Key)).ToList();

                check.InvalidCount = missingSettings.Count;
                check.Status = check.InvalidCount == 0;
                    ? DataIntegrityStatus.Healthy;
                    : DataIntegrityStatus.Error;

                check.Message = check.InvalidCount == 0;
                    ? $"All {check.TotalCount} settings are valid"
                    : $"Missing required settings: {string.Join(", ", missingSettings)}";
            }
            catch (Exception ex)
            {
                check.Status = DataIntegrityStatus.Error;
                check.Message = $"Validation failed: {ex.Message}";
                check.Error = ex;
            }

            return check;
        }

        /// <summary>
        /// Validates database consistency;
        /// </summary>
        private async Task<DataIntegrityCheck> ValidateDatabaseConsistencyAsync(CancellationToken cancellationToken)
        {
            var check = new DataIntegrityCheck;
            {
                Name = "DatabaseConsistency",
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Execute database consistency check;
                var diagnostics = await _dataContext.GetDiagnosticsAsync(cancellationToken);

                check.Status = diagnostics.IsHealthy;
                    ? DataIntegrityStatus.Healthy;
                    : DataIntegrityStatus.Error;

                check.Message = diagnostics.IsHealthy;
                    ? "Database is healthy and consistent"
                    : "Database has consistency issues";
            }
            catch (Exception ex)
            {
                check.Status = DataIntegrityStatus.Error;
                check.Message = $"Consistency check failed: {ex.Message}";
                check.Error = ex;
            }

            return check;
        }

        /// <summary>
        /// Repairs data integrity issues;
        /// </summary>
        public async Task<DataRepairReport> RepairDataIntegrityAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteOperationAsync(async () =>
            {
                _logger.LogInformation("Starting data integrity repair...");

                var report = new DataRepairReport;
                {
                    Timestamp = DateTime.UtcNow,
                    Repairs = new List<DataRepairAction>()
                };

                // Validate first;
                var integrityReport = await ValidateDataIntegrityAsync(cancellationToken);

                foreach (var check in integrityReport.Checks.Where(c => c.Status != DataIntegrityStatus.Healthy))
                {
                    var repairAction = await RepairCheckAsync(check, cancellationToken);
                    report.Repairs.Add(repairAction);
                }

                report.Status = report.Repairs.All(r => r.Success)
                    ? DataRepairStatus.Completed;
                    : DataRepairStatus.Partial;

                report.Duration = DateTime.UtcNow - report.Timestamp;

                _logger.LogInformation("Data integrity repair completed with status: {Status}", report.Status);

                return report;
            }, "RepairDataIntegrity", true, cancellationToken);
        }

        /// <summary>
        /// Repairs a specific integrity check;
        /// </summary>
        private async Task<DataRepairAction> RepairCheckAsync(
            DataIntegrityCheck check,
            CancellationToken cancellationToken)
        {
            var repair = new DataRepairAction;
            {
                CheckName = check.Name,
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                switch (check.Name)
                {
                    case "KnowledgeArticles":
                        await RepairKnowledgeArticlesAsync(cancellationToken);
                        repair.Message = "Repaired knowledge articles";
                        repair.Success = true;
                        break;

                    case "SystemSettings":
                        await RepairSystemSettingsAsync(cancellationToken);
                        repair.Message = "Repaired system settings";
                        repair.Success = true;
                        break;

                    default:
                        repair.Message = $"No repair strategy for {check.Name}";
                        repair.Success = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                repair.Message = $"Repair failed: {ex.Message}";
                repair.Success = false;
                repair.Error = ex;
            }

            return repair;
        }

        /// <summary>
        /// Repairs knowledge articles;
        /// </summary>
        private async Task RepairKnowledgeArticlesAsync(CancellationToken cancellationToken)
        {
            var repository = GetRepository<KnowledgeArticle, Guid>();
            var articles = await repository.GetAllAsync(cancellationToken: cancellationToken);

            foreach (var article in articles.Where(a => string.IsNullOrWhiteSpace(a.Title)))
            {
                article.Title = "Untitled Article";
                await repository.UpdateAsync(article, cancellationToken);
            }
        }

        /// <summary>
        /// Repairs system settings;
        /// </summary>
        private async Task RepairSystemSettingsAsync(CancellationToken cancellationToken)
        {
            var repository = GetRepository<SystemSetting, Guid>();
            var requiredSettings = new Dictionary<string, string>
            {
                ["System.Version"] = "1.0.0",
                ["System.Name"] = "NEDA System"
            };

            foreach (var setting in requiredSettings)
            {
                var existing = (await repository.GetAllAsync(
                    predicate: s => s.Key == setting.Key,
                    cancellationToken: cancellationToken)).FirstOrDefault();

                if (existing == null)
                {
                    var newSetting = new SystemSetting;
                    {
                        Id = Guid.NewGuid(),
                        Key = setting.Key,
                        Value = setting.Value,
                        Description = $"Auto-repaired: {setting.Key}",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow;
                    };

                    await repository.AddAsync(newSetting, cancellationToken);
                }
            }
        }

        #endregion;

        #region Event Handlers;

        /// <summary>
        /// Raises the OperationStarted event;
        /// </summary>
        protected virtual void OnOperationStarted(DataOperationStartedEventArgs e)
        {
            OperationStarted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the OperationCompleted event;
        /// </summary>
        protected virtual void OnOperationCompleted(DataOperationCompletedEventArgs e)
        {
            OperationCompleted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the OperationFailed event;
        /// </summary>
        protected virtual void OnOperationFailed(DataOperationFailedEventArgs e)
        {
            OperationFailed?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the DataIndexed event;
        /// </summary>
        protected virtual void OnDataIndexed(DataIndexedEventArgs e)
        {
            DataIndexed?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the CacheUpdated event;
        /// </summary>
        protected virtual void OnCacheUpdated(CacheUpdatedEventArgs e)
        {
            CacheUpdated?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Disposes the data manager and all resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _logger.LogInformation("Disposing DataManager...");

                    try
                    {
                        // Dispose all repositories;
                        foreach (var disposable in _disposableResources)
                        {
                            try
                            {
                                disposable.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error disposing resource");
                            }
                        }

                        // Dispose semaphore;
                        _operationSemaphore.Dispose();

                        _logger.LogInformation("DataManager disposed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during DataManager disposal");
                    }
                }

                _isDisposed = true;
            }
        }

        /// <summary>
        /// Async dispose;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
            Dispose(false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Async dispose core;
        /// </summary>
        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (!_isDisposed)
            {
                try
                {
                    // Async dispose of resources;
                    foreach (var disposable in _disposableResources.OfType<IAsyncDisposable>())
                    {
                        try
                        {
                            await disposable.DisposeAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error async disposing resource");
                        }
                    }

                    // Async dispose of semaphore;
                    await _operationSemaphore.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during async disposal of DataManager");
                }
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~DataManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    /// <summary>
    /// Interface for data management operations;
    /// </summary>
    public interface IDataManager : IDisposable, IAsyncDisposable;
    {
        // Properties;
        IDataContext DataContext { get; }
        bool IsInitialized { get; }
        DataOperationMode OperationMode { get; }
        DataManagerStatistics Statistics { get; }
        DataManagementConfig Configuration { get; }

        // Events;
        event EventHandler<DataOperationStartedEventArgs> OperationStarted;
        event EventHandler<DataOperationCompletedEventArgs> OperationCompleted;
        event EventHandler<DataOperationFailedEventArgs> OperationFailed;
        event EventHandler<DataIndexedEventArgs> DataIndexed;
        event EventHandler<CacheUpdatedEventArgs> CacheUpdated;

        // Initialization;
        Task InitializeAsync(CancellationToken cancellationToken = default);

        // Repository Management;
        IRepository<TEntity, TKey> GetRepository<TEntity, TKey>()
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;
        IRepository<TEntity> GetRepository<TEntity>()
            where TEntity : class, IEntity<Guid>;
        void RegisterRepository<TEntity, TRepository>(TRepository repository)
            where TRepository : class;
        Task ClearRepositoryCachesAsync(CancellationToken cancellationToken = default);

        // Operations;
        Task<TResult> ExecuteOperationAsync<TResult>(
            Func<Task<TResult>> operation,
            string operationName = null,
            bool requiresTransaction = false,
            CancellationToken cancellationToken = default);
        Task<IReadOnlyList<TResult>> ExecuteBatchAsync<TResult>(
            IEnumerable<Func<Task<TResult>>> operations,
            int batchSize = 10,
            CancellationToken cancellationToken = default);

        // Query Operations;
        Task<PagedResult<TEntity>> QueryAsync<TEntity, TKey>(
            ISpecification<TEntity> specification,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;
        Task<PagedResult<TEntity>> SearchAsync<TEntity, TKey>(
            string searchTerm,
            int pageNumber = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;
        Task<TEntity> GetWithRelatedAsync<TEntity, TKey>(
            TKey entityId,
            string[] includeProperties = null,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;

        // Modification Operations;
        Task<TEntity> CreateAsync<TEntity, TKey>(
            TEntity entity,
            bool validate = true,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;
        Task<TEntity> UpdateAsync<TEntity, TKey>(
            TKey entityId,
            Action<TEntity> updateAction,
            bool validate = true,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;
        Task<bool> DeleteAsync<TEntity, TKey>(
            TKey entityId,
            bool permanent = false,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;
        Task<IReadOnlyList<TEntity>> CreateBatchAsync<TEntity, TKey>(
            IEnumerable<TEntity> entities,
            int batchSize = 100,
            CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;

        // Indexing Operations;
        Task RebuildIndexAsync<TEntity, TKey>(CancellationToken cancellationToken = default)
            where TEntity : class, IEntity<TKey>
            where TKey : IEquatable<TKey>;

        // Caching Operations;
        Task ClearAllCachesAsync(CancellationToken cancellationToken = default);

        // Data Integrity Operations;
        Task<DataIntegrityReport> ValidateDataIntegrityAsync(CancellationToken cancellationToken = default);
        Task<DataRepairReport> RepairDataIntegrityAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Data operation mode enumeration;
    /// </summary>
    public enum DataOperationMode;
    {
        Normal,
        ReadOnly,
        Maintenance,
        Recovery;
    }

    /// <summary>
    /// Data manager statistics;
    /// </summary>
    public class DataManagerStatistics;
    {
        public long OperationsStarted { get; private set; }
        public long OperationsCompleted { get; private set; }
        public long OperationsFailed { get; private set; }
        public long EntitiesCreated { get; private set; }
        public long EntitiesUpdated { get; private set; }
        public long EntitiesDeleted { get; private set; }
        public long EntitiesDeletedPermanently { get; private set; }
        public long BatchOperations { get; private set; }
        public TimeSpan TotalOperationTime { get; private set; }
        public DateTime StartTime { get; private set; }
        public DateTime LastOperationTime { get; private set; }

        public DataManagerStatistics()
        {
            StartTime = DateTime.UtcNow;
            LastOperationTime = DateTime.UtcNow;
        }

        public void OperationStarted()
        {
            OperationsStarted++;
            UpdateLastOperation();
        }

        public void OperationCompleted(TimeSpan duration)
        {
            OperationsCompleted++;
            TotalOperationTime += duration;
            UpdateLastOperation();
        }

        public void OperationFailed(TimeSpan duration)
        {
            OperationsFailed++;
            TotalOperationTime += duration;
            UpdateLastOperation();
        }

        public void EntityCreated()
        {
            EntitiesCreated++;
            UpdateLastOperation();
        }

        public void EntityUpdated()
        {
            EntitiesUpdated++;
            UpdateLastOperation();
        }

        public void EntityDeleted(bool permanent = false)
        {
            EntitiesDeleted++;
            if (permanent) EntitiesDeletedPermanently++;
            UpdateLastOperation();
        }

        public void BatchCreated(int count)
        {
            BatchOperations += count;
            UpdateLastOperation();
        }

        private void UpdateLastOperation() => LastOperationTime = DateTime.UtcNow;

        public double AverageOperationTime => OperationsCompleted > 0;
            ? TotalOperationTime.TotalMilliseconds / OperationsCompleted;
            : 0;

        public double SuccessRate => OperationsStarted > 0;
            ? (double)OperationsCompleted / OperationsStarted * 100;
            : 100;

        public TimeSpan Uptime => DateTime.UtcNow - StartTime;
    }

    /// <summary>
    /// Data integrity status enumeration;
    /// </summary>
    public enum DataIntegrityStatus;
    {
        Healthy,
        Warning,
        Error;
    }

    /// <summary>
    /// Data integrity check result;
    /// </summary>
    public class DataIntegrityCheck;
    {
        public string Name { get; set; }
        public DataIntegrityStatus Status { get; set; }
        public int TotalCount { get; set; }
        public int InvalidCount { get; set; }
        public string Message { get; set; }
        public Exception Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Data integrity report;
    /// </summary>
    public class DataIntegrityReport;
    {
        public DataIntegrityStatus Status { get; set; }
        public List<DataIntegrityCheck> Checks { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Data repair status enumeration;
    /// </summary>
    public enum DataRepairStatus;
    {
        Completed,
        Partial,
        Failed;
    }

    /// <summary>
    /// Data repair action;
    /// </summary>
    public class DataRepairAction;
    {
        public string CheckName { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public Exception Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Data repair report;
    /// </summary>
    public class DataRepairReport;
    {
        public DataRepairStatus Status { get; set; }
        public List<DataRepairAction> Repairs { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
    }

    #endregion;
}
