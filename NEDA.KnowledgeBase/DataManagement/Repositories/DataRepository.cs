// NEDA.KnowledgeBase/DataManagement/Repositories/DataRepository.cs;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.KnowledgeBase.DataManagement.DataAccess;

namespace NEDA.KnowledgeBase.DataManagement.Repositories;
{
    /// <summary>
    /// Endüstriyel seviyede generic data repository ve data access layer;
    /// CQRS pattern, caching, transaction yönetimi ve advanced query desteği sağlar;
    /// </summary>
    /// <typeparam name="TEntity">Entity tipi</typeparam>
    /// <typeparam name="TKey">Primary key tipi</typeparam>
    public class DataRepository<TEntity, TKey> : IRepository<TEntity, TKey>, IDisposable;
        where TEntity : class, IEntity<TKey>, new()
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly ConnectionManager _connectionManager;
        private readonly RepositoryConfig _config;
        private readonly DataRepositoryCache<TEntity, TKey> _cache;
        private readonly DataRepositoryValidator<TEntity, TKey> _validator;
        private readonly QueryBuilder<TEntity> _queryBuilder;
        private readonly BulkOperations<TEntity> _bulkOperations;
        private readonly object _transactionLock = new object();
        private DbTransaction _currentTransaction;
        private bool _disposed;
        private int _queryCount;
        private readonly DateTime _createdAt;
        #endregion;

        #region Properties;
        /// <summary>
        /// Repository durumu;
        /// </summary>
        public RepositoryState State { get; private set; }

        /// <summary>
        /// Cache durumu;
        /// </summary>
        public CacheStatus CacheStatus => _cache.GetStatus();

        /// <summary>
        /// Toplam sorgu sayısı;
        /// </summary>
        public int TotalQueryCount => _queryCount;

        /// <summary>
        /// Konfigürasyon ayarları;
        /// </summary>
        public RepositoryConfig Config => _config;

        /// <summary>
        /// Repository metrikleri;
        /// </summary>
        public RepositoryMetrics Metrics => GetCurrentMetrics();

        /// <summary>
        /// Aktif transaction var mı?
        /// </summary>
        public bool HasActiveTransaction => _currentTransaction != null;

        /// <summary>
        /// Repository oluşturulma zamanı;
        /// </summary>
        public DateTime CreatedAt => _createdAt;
        #endregion;

        #region Events;
        /// <summary>
        /// Entity eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<EntityAddedEventArgs<TEntity>> EntityAdded;

        /// <summary>
        /// Entity güncellendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<EntityUpdatedEventArgs<TEntity>> EntityUpdated;

        /// <summary>
        /// Entity silindiğinde tetiklenir;
        /// </summary>
        public event EventHandler<EntityDeletedEventArgs<TKey>> EntityDeleted;

        /// <summary>
        /// Cache temizlendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<CacheClearedEventArgs> CacheCleared;

        /// <summary>
        /// Query çalıştırıldığında tetiklenir;
        /// </summary>
        public event EventHandler<QueryExecutedEventArgs> QueryExecuted;

        /// <summary>
        /// Transaction başlatıldığında tetiklenir;
        /// </summary>
        public event EventHandler<TransactionStartedEventArgs> TransactionStarted;

        /// <summary>
        /// Transaction commit edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<TransactionCommittedEventArgs> TransactionCommitted;

        /// <summary>
        /// Transaction rollback edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<TransactionRolledBackEventArgs> TransactionRolledBack;
        #endregion;

        #region Constructor;
        /// <summary>
        /// DataRepository örneği oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="connectionManager">Bağlantı yöneticisi</param>
        /// <param name="config">Repository konfigürasyonu</param>
        public DataRepository(
            ILogger logger,
            ConnectionManager connectionManager,
            RepositoryConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _config = config ?? RepositoryConfig.Default;

            _cache = new DataRepositoryCache<TEntity, TKey>(_config.CacheConfig, _logger);
            _validator = new DataRepositoryValidator<TEntity, TKey>(_config, _logger);
            _queryBuilder = new QueryBuilder<TEntity>(_config, _logger);
            _bulkOperations = new BulkOperations<TEntity>(_config, _logger);

            _createdAt = DateTime.UtcNow;
            State = RepositoryState.Ready;

            _logger.Info($"DataRepository initialized for {typeof(TEntity).Name}, " +
                        $"Cache: {_config.EnableCaching}, " +
                        $"Validation: {_config.EnableValidation}");
        }
        #endregion;

        #region CRUD Operations - Sync;
        /// <summary>
        /// ID'ye göre entity getirir;
        /// </summary>
        public TEntity GetById(TKey id)
        {
            ValidateRepositoryState();

            try
            {
                _queryCount++;

                // Cache kontrolü;
                if (_config.EnableCaching)
                {
                    var cached = _cache.Get(id);
                    if (cached != null)
                    {
                        _logger.Debug($"Cache hit for {typeof(TEntity).Name} with ID: {id}");
                        return cached;
                    }
                }

                using (var connection = _connectionManager.GetConnectionAsync().Result)
                {
                    var query = _queryBuilder.BuildGetByIdQuery(id);
                    var parameters = _queryBuilder.BuildIdParameter(id);

                    var entity = ExecuteQuerySingle<TEntity>(connection, query, parameters);

                    if (entity != null && _config.EnableCaching)
                    {
                        _cache.Set(id, entity);
                    }

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.GetById,
                        EntityType = typeof(TEntity).Name,
                        ExecutionTime = DateTime.UtcNow,
                        CacheHit = false;
                    });

                    return entity;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get {typeof(TEntity).Name} with ID {id}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryGetFailed,
                    $"Failed to get {typeof(TEntity).Name} with ID {id}",
                    ex);
            }
        }

        /// <summary>
        /// Tüm entity'leri getirir;
        /// </summary>
        public IEnumerable<TEntity> GetAll()
        {
            ValidateRepositoryState();

            try
            {
                _queryCount++;

                // Cache kontrolü (sadece tüm entity'ler cache'leniyorsa)
                if (_config.EnableCaching && _config.CacheAllEntities)
                {
                    var cached = _cache.GetAll();
                    if (cached != null)
                    {
                        _logger.Debug($"Cache hit for all {typeof(TEntity).Name} entities");
                        return cached;
                    }
                }

                using (var connection = _connectionManager.GetConnectionAsync().Result)
                {
                    var query = _queryBuilder.BuildGetAllQuery();
                    var entities = ExecuteQueryList<TEntity>(connection, query);

                    if (_config.EnableCaching && _config.CacheAllEntities && entities != null)
                    {
                        _cache.SetAll(entities);
                    }

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.GetAll,
                        EntityType = typeof(TEntity).Name,
                        ExecutionTime = DateTime.UtcNow,
                        CacheHit = false,
                        ResultCount = entities?.Count() ?? 0;
                    });

                    return entities ?? Enumerable.Empty<TEntity>();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get all {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryGetAllFailed,
                    $"Failed to get all {typeof(TEntity).Name} entities",
                    ex);
            }
        }

        /// <summary>
        /// Entity ekler;
        /// </summary>
        public TEntity Add(TEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            ValidateRepositoryState();

            try
            {
                // Validasyon;
                if (_config.EnableValidation)
                {
                    var validationResult = _validator.Validate(entity, ValidationType.Insert);
                    if (!validationResult.IsValid)
                    {
                        throw new RepositoryException(
                            ErrorCode.ValidationFailed,
                            $"Entity validation failed: {string.Join(", ", validationResult.Errors)}");
                    }
                }

                using (var connection = _connectionManager.GetConnectionAsync().Result)
                {
                    var transaction = connection.BeginTransaction();

                    try
                    {
                        var query = _queryBuilder.BuildInsertQuery(entity);
                        var parameters = _queryBuilder.BuildInsertParameters(entity);

                        var newId = ExecuteInsert(connection, transaction, query, parameters);

                        // Auto-generated ID için entity'i güncelle;
                        if (newId != null)
                        {
                            var idProperty = typeof(TEntity).GetProperty("Id");
                            if (idProperty != null && idProperty.CanWrite)
                            {
                                idProperty.SetValue(entity, newId);
                            }
                        }

                        transaction.Commit();

                        // Cache'i güncelle;
                        if (_config.EnableCaching)
                        {
                            _cache.Set(entity.Id, entity);
                            if (_config.CacheAllEntities)
                            {
                                _cache.InvalidateAllCache();
                            }
                        }

                        OnEntityAdded(new EntityAddedEventArgs<TEntity>
                        {
                            Entity = entity,
                            Timestamp = DateTime.UtcNow,
                            OperationId = Guid.NewGuid().ToString()
                        });

                        _logger.Debug($"Added {typeof(TEntity).Name} with ID: {entity.Id}");

                        return entity;
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add {typeof(TEntity).Name}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryAddFailed,
                    $"Failed to add {typeof(TEntity).Name}",
                    ex);
            }
        }

        /// <summary>
        /// Entity günceller;
        /// </summary>
        public bool Update(TEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            ValidateRepositoryState();

            try
            {
                // Validasyon;
                if (_config.EnableValidation)
                {
                    var validationResult = _validator.Validate(entity, ValidationType.Update);
                    if (!validationResult.IsValid)
                    {
                        throw new RepositoryException(
                            ErrorCode.ValidationFailed,
                            $"Entity validation failed: {string.Join(", ", validationResult.Errors)}");
                    }
                }

                using (var connection = _connectionManager.GetConnectionAsync().Result)
                {
                    var transaction = connection.BeginTransaction();

                    try
                    {
                        var query = _queryBuilder.BuildUpdateQuery(entity);
                        var parameters = _queryBuilder.BuildUpdateParameters(entity);

                        var affectedRows = ExecuteNonQuery(connection, transaction, query, parameters);

                        transaction.Commit();

                        if (affectedRows > 0)
                        {
                            // Cache'i güncelle;
                            if (_config.EnableCaching)
                            {
                                _cache.Set(entity.Id, entity);
                                if (_config.CacheAllEntities)
                                {
                                    _cache.InvalidateAllCache();
                                }
                            }

                            OnEntityUpdated(new EntityUpdatedEventArgs<TEntity>
                            {
                                Entity = entity,
                                Timestamp = DateTime.UtcNow,
                                OperationId = Guid.NewGuid().ToString()
                            });

                            _logger.Debug($"Updated {typeof(TEntity).Name} with ID: {entity.Id}");
                        }

                        return affectedRows > 0;
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update {typeof(TEntity).Name} with ID {entity.Id}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryUpdateFailed,
                    $"Failed to update {typeof(TEntity).Name} with ID {entity.Id}",
                    ex);
            }
        }

        /// <summary>
        /// Entity siler;
        /// </summary>
        public bool Delete(TKey id)
        {
            ValidateRepositoryState();

            try
            {
                using (var connection = _connectionManager.GetConnectionAsync().Result)
                {
                    var transaction = connection.BeginTransaction();

                    try
                    {
                        var query = _queryBuilder.BuildDeleteQuery(id);
                        var parameters = _queryBuilder.BuildIdParameter(id);

                        var affectedRows = ExecuteNonQuery(connection, transaction, query, parameters);

                        transaction.Commit();

                        if (affectedRows > 0)
                        {
                            // Cache'den sil;
                            if (_config.EnableCaching)
                            {
                                _cache.Remove(id);
                                if (_config.CacheAllEntities)
                                {
                                    _cache.InvalidateAllCache();
                                }
                            }

                            OnEntityDeleted(new EntityDeletedEventArgs<TKey>
                            {
                                EntityId = id,
                                Timestamp = DateTime.UtcNow,
                                OperationId = Guid.NewGuid().ToString()
                            });

                            _logger.Debug($"Deleted {typeof(TEntity).Name} with ID: {id}");
                        }

                        return affectedRows > 0;
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete {typeof(TEntity).Name} with ID {id}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryDeleteFailed,
                    $"Failed to delete {typeof(TEntity).Name} with ID {id}",
                    ex);
            }
        }
        #endregion;

        #region CRUD Operations - Async;
        /// <summary>
        /// ID'ye göre entity getirir (async)
        /// </summary>
        public async Task<TEntity> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
        {
            ValidateRepositoryState();

            try
            {
                _queryCount++;

                // Cache kontrolü;
                if (_config.EnableCaching)
                {
                    var cached = _cache.Get(id);
                    if (cached != null)
                    {
                        _logger.Debug($"Cache hit for {typeof(TEntity).Name} with ID: {id}");
                        return cached;
                    }
                }

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var query = _queryBuilder.BuildGetByIdQuery(id);
                    var parameters = _queryBuilder.BuildIdParameter(id);

                    var entity = await ExecuteQuerySingleAsync<TEntity>(connection, query, parameters, cancellationToken);

                    if (entity != null && _config.EnableCaching)
                    {
                        _cache.Set(id, entity);
                    }

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.GetById,
                        EntityType = typeof(TEntity).Name,
                        ExecutionTime = DateTime.UtcNow,
                        CacheHit = false;
                    });

                    return entity;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get {typeof(TEntity).Name} with ID {id}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryGetFailed,
                    $"Failed to get {typeof(TEntity).Name} with ID {id}",
                    ex);
            }
        }

        /// <summary>
        /// Tüm entity'leri getirir (async)
        /// </summary>
        public async Task<IEnumerable<TEntity>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            ValidateRepositoryState();

            try
            {
                _queryCount++;

                // Cache kontrolü;
                if (_config.EnableCaching && _config.CacheAllEntities)
                {
                    var cached = _cache.GetAll();
                    if (cached != null)
                    {
                        _logger.Debug($"Cache hit for all {typeof(TEntity).Name} entities");
                        return cached;
                    }
                }

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var query = _queryBuilder.BuildGetAllQuery();
                    var entities = await ExecuteQueryListAsync<TEntity>(connection, query, cancellationToken);

                    if (_config.EnableCaching && _config.CacheAllEntities && entities != null)
                    {
                        _cache.SetAll(entities);
                    }

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.GetAll,
                        EntityType = typeof(TEntity).Name,
                        ExecutionTime = DateTime.UtcNow,
                        CacheHit = false,
                        ResultCount = entities?.Count() ?? 0;
                    });

                    return entities ?? Enumerable.Empty<TEntity>();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get all {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryGetAllFailed,
                    $"Failed to get all {typeof(TEntity).Name} entities",
                    ex);
            }
        }

        /// <summary>
        /// Entity ekler (async)
        /// </summary>
        public async Task<TEntity> AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            ValidateRepositoryState();

            try
            {
                // Validasyon;
                if (_config.EnableValidation)
                {
                    var validationResult = await _validator.ValidateAsync(entity, ValidationType.Insert, cancellationToken);
                    if (!validationResult.IsValid)
                    {
                        throw new RepositoryException(
                            ErrorCode.ValidationFailed,
                            $"Entity validation failed: {string.Join(", ", validationResult.Errors)}");
                    }
                }

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);

                    try
                    {
                        var query = _queryBuilder.BuildInsertQuery(entity);
                        var parameters = _queryBuilder.BuildInsertParameters(entity);

                        var newId = await ExecuteInsertAsync(connection, transaction, query, parameters, cancellationToken);

                        // Auto-generated ID için entity'i güncelle;
                        if (newId != null)
                        {
                            var idProperty = typeof(TEntity).GetProperty("Id");
                            if (idProperty != null && idProperty.CanWrite)
                            {
                                idProperty.SetValue(entity, newId);
                            }
                        }

                        await transaction.CommitAsync(cancellationToken);

                        // Cache'i güncelle;
                        if (_config.EnableCaching)
                        {
                            _cache.Set(entity.Id, entity);
                            if (_config.CacheAllEntities)
                            {
                                _cache.InvalidateAllCache();
                            }
                        }

                        OnEntityAdded(new EntityAddedEventArgs<TEntity>
                        {
                            Entity = entity,
                            Timestamp = DateTime.UtcNow,
                            OperationId = Guid.NewGuid().ToString()
                        });

                        _logger.Debug($"Added {typeof(TEntity).Name} with ID: {entity.Id}");

                        return entity;
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add {typeof(TEntity).Name}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryAddFailed,
                    $"Failed to add {typeof(TEntity).Name}",
                    ex);
            }
        }

        /// <summary>
        /// Entity günceller (async)
        /// </summary>
        public async Task<bool> UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            ValidateRepositoryState();

            try
            {
                // Validasyon;
                if (_config.EnableValidation)
                {
                    var validationResult = await _validator.ValidateAsync(entity, ValidationType.Update, cancellationToken);
                    if (!validationResult.IsValid)
                    {
                        throw new RepositoryException(
                            ErrorCode.ValidationFailed,
                            $"Entity validation failed: {string.Join(", ", validationResult.Errors)}");
                    }
                }

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);

                    try
                    {
                        var query = _queryBuilder.BuildUpdateQuery(entity);
                        var parameters = _queryBuilder.BuildUpdateParameters(entity);

                        var affectedRows = await ExecuteNonQueryAsync(connection, transaction, query, parameters, cancellationToken);

                        await transaction.CommitAsync(cancellationToken);

                        if (affectedRows > 0)
                        {
                            // Cache'i güncelle;
                            if (_config.EnableCaching)
                            {
                                _cache.Set(entity.Id, entity);
                                if (_config.CacheAllEntities)
                                {
                                    _cache.InvalidateAllCache();
                                }
                            }

                            OnEntityUpdated(new EntityUpdatedEventArgs<TEntity>
                            {
                                Entity = entity,
                                Timestamp = DateTime.UtcNow,
                                OperationId = Guid.NewGuid().ToString()
                            });

                            _logger.Debug($"Updated {typeof(TEntity).Name} with ID: {entity.Id}");
                        }

                        return affectedRows > 0;
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update {typeof(TEntity).Name} with ID {entity.Id}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryUpdateFailed,
                    $"Failed to update {typeof(TEntity).Name} with ID {entity.Id}",
                    ex);
            }
        }

        /// <summary>
        /// Entity siler (async)
        /// </summary>
        public async Task<bool> DeleteAsync(TKey id, CancellationToken cancellationToken = default)
        {
            ValidateRepositoryState();

            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);

                    try
                    {
                        var query = _queryBuilder.BuildDeleteQuery(id);
                        var parameters = _queryBuilder.BuildIdParameter(id);

                        var affectedRows = await ExecuteNonQueryAsync(connection, transaction, query, parameters, cancellationToken);

                        await transaction.CommitAsync(cancellationToken);

                        if (affectedRows > 0)
                        {
                            // Cache'den sil;
                            if (_config.EnableCaching)
                            {
                                _cache.Remove(id);
                                if (_config.CacheAllEntities)
                                {
                                    _cache.InvalidateAllCache();
                                }
                            }

                            OnEntityDeleted(new EntityDeletedEventArgs<TKey>
                            {
                                EntityId = id,
                                Timestamp = DateTime.UtcNow,
                                OperationId = Guid.NewGuid().ToString()
                            });

                            _logger.Debug($"Deleted {typeof(TEntity).Name} with ID: {id}");
                        }

                        return affectedRows > 0;
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete {typeof(TEntity).Name} with ID {id}: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryDeleteFailed,
                    $"Failed to delete {typeof(TEntity).Name} with ID {id}",
                    ex);
            }
        }
        #endregion;

        #region Query Operations;
        /// <summary>
        /// Filtreye göre entity'leri getirir;
        /// </summary>
        public async Task<IEnumerable<TEntity>> FindAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            ValidateRepositoryState();

            try
            {
                _queryCount++;

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var query = _queryBuilder.BuildFindQuery(predicate);
                    var parameters = _queryBuilder.BuildPredicateParameters(predicate);

                    var entities = await ExecuteQueryListAsync<TEntity>(connection, query, parameters, cancellationToken);

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.Find,
                        EntityType = typeof(TEntity).Name,
                        ExecutionTime = DateTime.UtcNow,
                        CacheHit = false,
                        ResultCount = entities?.Count() ?? 0;
                    });

                    return entities ?? Enumerable.Empty<TEntity>();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to find {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryFindFailed,
                    $"Failed to find {typeof(TEntity).Name} entities",
                    ex);
            }
        }

        /// <summary>
        /// Sayfalı sorgu çalıştırır;
        /// </summary>
        public async Task<PagedResult<TEntity>> GetPagedAsync(
            int pageNumber,
            int pageSize,
            Expression<Func<TEntity, bool>> predicate = null,
            Expression<Func<TEntity, object>> orderBy = null,
            bool ascending = true,
            CancellationToken cancellationToken = default)
        {
            ValidateRepositoryState();

            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = _config.DefaultPageSize;

            try
            {
                _queryCount++;

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var query = _queryBuilder.BuildPagedQuery(pageNumber, pageSize, predicate, orderBy, ascending);
                    var parameters = _queryBuilder.BuildPagedParameters(pageNumber, pageSize, predicate);

                    var entities = await ExecuteQueryListAsync<TEntity>(connection, query, parameters, cancellationToken);
                    var totalCount = await GetCountAsync(predicate, cancellationToken);

                    var result = new PagedResult<TEntity>
                    {
                        Items = entities ?? Enumerable.Empty<TEntity>(),
                        PageNumber = pageNumber,
                        PageSize = pageSize,
                        TotalCount = totalCount,
                        TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
                    };

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.Paged,
                        EntityType = typeof(TEntity).Name,
                        ExecutionTime = DateTime.UtcNow,
                        CacheHit = false,
                        ResultCount = result.Items.Count()
                    });

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get paged {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryPagedQueryFailed,
                    $"Failed to get paged {typeof(TEntity).Name} entities",
                    ex);
            }
        }

        /// <summary>
        /// Toplam sayıyı getirir;
        /// </summary>
        public async Task<int> GetCountAsync(
            Expression<Func<TEntity, bool>> predicate = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRepositoryState();

            try
            {
                _queryCount++;

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var query = _queryBuilder.BuildCountQuery(predicate);
                    var parameters = _queryBuilder.BuildPredicateParameters(predicate);

                    var count = await ExecuteScalarAsync<int>(connection, query, parameters, cancellationToken);

                    return count;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get count of {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryCountFailed,
                    $"Failed to get count of {typeof(TEntity).Name} entities",
                    ex);
            }
        }

        /// <summary>
        /// Custom SQL sorgusu çalıştırır;
        /// </summary>
        public async Task<IEnumerable<TResult>> ExecuteQueryAsync<TResult>(
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("SQL query cannot be null or empty", nameof(sql));

            ValidateRepositoryState();

            try
            {
                _queryCount++;

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var results = await ExecuteQueryListAsync<TResult>(connection, sql, parameters, cancellationToken);

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.Custom,
                        EntityType = typeof(TResult).Name,
                        ExecutionTime = DateTime.UtcNow,
                        CacheHit = false,
                        ResultCount = results?.Count() ?? 0;
                    });

                    return results ?? Enumerable.Empty<TResult>();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to execute custom query: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryCustomQueryFailed,
                    "Failed to execute custom query",
                    ex);
            }
        }
        #endregion;

        #region Bulk Operations;
        /// <summary>
        /// Toplu entity ekler;
        /// </summary>
        public async Task<int> BulkInsertAsync(
            IEnumerable<TEntity> entities,
            CancellationToken cancellationToken = default)
        {
            if (entities == null)
                throw new ArgumentNullException(nameof(entities));

            ValidateRepositoryState();

            try
            {
                var entityList = entities.ToList();
                if (entityList.Count == 0)
                    return 0;

                // Validasyon;
                if (_config.EnableValidation)
                {
                    foreach (var entity in entityList)
                    {
                        var validationResult = await _validator.ValidateAsync(entity, ValidationType.Insert, cancellationToken);
                        if (!validationResult.IsValid)
                        {
                            throw new RepositoryException(
                                ErrorCode.ValidationFailed,
                                $"Entity validation failed: {string.Join(", ", validationResult.Errors)}");
                        }
                    }
                }

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);

                    try
                    {
                        var insertedCount = await _bulkOperations.BulkInsertAsync(
                            connection,
                            transaction,
                            entityList,
                            cancellationToken);

                        await transaction.CommitAsync(cancellationToken);

                        // Cache'i güncelle;
                        if (_config.EnableCaching)
                        {
                            foreach (var entity in entityList)
                            {
                                _cache.Set(entity.Id, entity);
                            }
                            if (_config.CacheAllEntities)
                            {
                                _cache.InvalidateAllCache();
                            }
                        }

                        _logger.Debug($"Bulk inserted {insertedCount} {typeof(TEntity).Name} entities");

                        return insertedCount;
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to bulk insert {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryBulkInsertFailed,
                    $"Failed to bulk insert {typeof(TEntity).Name} entities",
                    ex);
            }
        }

        /// <summary>
        /// Toplu entity günceller;
        /// </summary>
        public async Task<int> BulkUpdateAsync(
            IEnumerable<TEntity> entities,
            CancellationToken cancellationToken = default)
        {
            if (entities == null)
                throw new ArgumentNullException(nameof(entities));

            ValidateRepositoryState();

            try
            {
                var entityList = entities.ToList();
                if (entityList.Count == 0)
                    return 0;

                // Validasyon;
                if (_config.EnableValidation)
                {
                    foreach (var entity in entityList)
                    {
                        var validationResult = await _validator.ValidateAsync(entity, ValidationType.Update, cancellationToken);
                        if (!validationResult.IsValid)
                        {
                            throw new RepositoryException(
                                ErrorCode.ValidationFailed,
                                $"Entity validation failed: {string.Join(", ", validationResult.Errors)}");
                        }
                    }
                }

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);

                    try
                    {
                        var updatedCount = await _bulkOperations.BulkUpdateAsync(
                            connection,
                            transaction,
                            entityList,
                            cancellationToken);

                        await transaction.CommitAsync(cancellationToken);

                        // Cache'i güncelle;
                        if (_config.EnableCaching)
                        {
                            foreach (var entity in entityList)
                            {
                                _cache.Set(entity.Id, entity);
                            }
                            if (_config.CacheAllEntities)
                            {
                                _cache.InvalidateAllCache();
                            }
                        }

                        _logger.Debug($"Bulk updated {updatedCount} {typeof(TEntity).Name} entities");

                        return updatedCount;
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to bulk update {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryBulkUpdateFailed,
                    $"Failed to bulk update {typeof(TEntity).Name} entities",
                    ex);
            }
        }

        /// <summary>
        /// Toplu entity siler;
        /// </summary>
        public async Task<int> BulkDeleteAsync(
            IEnumerable<TKey> ids,
            CancellationToken cancellationToken = default)
        {
            if (ids == null)
                throw new ArgumentNullException(nameof(ids));

            ValidateRepositoryState();

            try
            {
                var idList = ids.ToList();
                if (idList.Count == 0)
                    return 0;

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);

                    try
                    {
                        var deletedCount = await _bulkOperations.BulkDeleteAsync(
                            connection,
                            transaction,
                            idList,
                            cancellationToken);

                        await transaction.CommitAsync(cancellationToken);

                        // Cache'den sil;
                        if (_config.EnableCaching)
                        {
                            foreach (var id in idList)
                            {
                                _cache.Remove(id);
                            }
                            if (_config.CacheAllEntities)
                            {
                                _cache.InvalidateAllCache();
                            }
                        }

                        _logger.Debug($"Bulk deleted {deletedCount} {typeof(TEntity).Name} entities");

                        return deletedCount;
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to bulk delete {typeof(TEntity).Name} entities: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.RepositoryBulkDeleteFailed,
                    $"Failed to bulk delete {typeof(TEntity).Name} entities",
                    ex);
            }
        }
        #endregion;

        #region Transaction Management;
        /// <summary>
        /// Transaction başlatır;
        /// </summary>
        public async Task BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default)
        {
            ValidateRepositoryState();

            try
            {
                lock (_transactionLock)
                {
                    if (_currentTransaction != null)
                    {
                        throw new RepositoryException(
                            ErrorCode.TransactionAlreadyStarted,
                            "A transaction is already in progress");
                    }
                }

                var connection = await _connectionManager.GetConnectionAsync(cancellationToken);
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);

                lock (_transactionLock)
                {
                    _currentTransaction = transaction;
                }

                OnTransactionStarted(new TransactionStartedEventArgs;
                {
                    IsolationLevel = isolationLevel,
                    Timestamp = DateTime.UtcNow,
                    TransactionId = Guid.NewGuid().ToString()
                });

                _logger.Debug($"Transaction started with isolation level: {isolationLevel}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to begin transaction: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.TransactionStartFailed,
                    "Failed to begin transaction",
                    ex);
            }
        }

        /// <summary>
        /// Transaction'ı commit eder;
        /// </summary>
        public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                lock (_transactionLock)
                {
                    if (_currentTransaction == null)
                    {
                        throw new RepositoryException(
                            ErrorCode.NoActiveTransaction,
                            "No active transaction to commit");
                    }
                }

                await _currentTransaction.CommitAsync(cancellationToken);

                OnTransactionCommitted(new TransactionCommittedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    TransactionId = Guid.NewGuid().ToString()
                });

                _logger.Debug("Transaction committed");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to commit transaction: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.TransactionCommitFailed,
                    "Failed to commit transaction",
                    ex);
            }
            finally
            {
                lock (_transactionLock)
                {
                    _currentTransaction?.Dispose();
                    _currentTransaction = null;
                }
            }
        }

        /// <summary>
        /// Transaction'ı rollback eder;
        /// </summary>
        public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                lock (_transactionLock)
                {
                    if (_currentTransaction == null)
                    {
                        throw new RepositoryException(
                            ErrorCode.NoActiveTransaction,
                            "No active transaction to rollback");
                    }
                }

                await _currentTransaction.RollbackAsync(cancellationToken);

                OnTransactionRolledBack(new TransactionRolledBackEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    TransactionId = Guid.NewGuid().ToString()
                });

                _logger.Debug("Transaction rolled back");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to rollback transaction: {ex.Message}", ex);
                throw new RepositoryException(
                    ErrorCode.TransactionRollbackFailed,
                    "Failed to rollback transaction",
                    ex);
            }
            finally
            {
                lock (_transactionLock)
                {
                    _currentTransaction?.Dispose();
                    _currentTransaction = null;
                }
            }
        }

        /// <summary>
        /// Transaction scope içinde çalıştırır;
        /// </summary>
        public async Task ExecuteInTransactionAsync(
            Func<Task> action,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            await BeginTransactionAsync(isolationLevel, cancellationToken);

            try
            {
                await action();
                await CommitTransactionAsync(cancellationToken);
            }
            catch (Exception)
            {
                await RollbackTransactionAsync(cancellationToken);
                throw;
            }
        }
        #endregion;

        #region Cache Management;
        /// <summary>
        /// Cache'i temizler;
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();

            OnCacheCleared(new CacheClearedEventArgs;
            {
                Timestamp = DateTime.UtcNow,
                CacheType = typeof(TEntity).Name;
            });

            _logger.Debug($"Cache cleared for {typeof(TEntity).Name}");
        }

        /// <summary>
        /// Belirli entity'i cache'den kaldırır;
        /// </summary>
        public void InvalidateCache(TKey id)
        {
            _cache.Remove(id);
            _logger.Debug($"Cache invalidated for {typeof(TEntity).Name} with ID: {id}");
        }

        /// <summary>
        /// Tüm cache'i geçersiz kılar;
        /// </summary>
        public void InvalidateAllCache()
        {
            _cache.InvalidateAllCache();
            _logger.Debug($"All cache invalidated for {typeof(TEntity).Name}");
        }

        /// <summary>
        /// Cache istatistiklerini getirir;
        /// </summary>
        public CacheStatistics GetCacheStatistics()
        {
            return _cache.GetStatistics();
        }
        #endregion;

        #region Utility Methods;
        /// <summary>
        /// Repository metriklerini getirir;
        /// </summary>
        public RepositoryMetrics GetMetrics()
        {
            return GetCurrentMetrics();
        }

        /// <summary>
        /// Repository durumunu kontrol eder;
        /// </summary>
        public RepositoryStatus GetStatus()
        {
            return new RepositoryStatus;
            {
                Timestamp = DateTime.UtcNow,
                EntityType = typeof(TEntity).Name,
                QueryCount = _queryCount,
                CacheStatus = CacheStatus,
                State = State,
                HasActiveTransaction = HasActiveTransaction,
                CreatedAt = _createdAt;
            };
        }

        /// <summary>
        /// Repository'yi yeniden başlatır;
        /// </summary>
        public void Reset()
        {
            ClearCache();
            _queryCount = 0;
            _logger.Info($"Repository reset for {typeof(TEntity).Name}");
        }
        #endregion;

        #region Private Methods;
        private void ValidateRepositoryState()
        {
            if (_disposed)
                throw new RepositoryException(
                    ErrorCode.RepositoryDisposed,
                    "Repository has been disposed");

            if (State == RepositoryState.Error)
                throw new RepositoryException(
                    ErrorCode.RepositoryInErrorState,
                    "Repository is in error state");
        }

        private RepositoryMetrics GetCurrentMetrics()
        {
            var cacheStats = _cache.GetStatistics();

            return new RepositoryMetrics;
            {
                Timestamp = DateTime.UtcNow,
                EntityType = typeof(TEntity).Name,
                TotalQueries = _queryCount,
                CacheHitRate = cacheStats.HitRate,
                CacheSize = cacheStats.CurrentSize,
                CacheMaxSize = cacheStats.MaxSize,
                State = State,
                Uptime = DateTime.UtcNow - _createdAt;
            };
        }

        // Database execution helper methods;
        private T ExecuteQuerySingle<T>(DatabaseConnection connection, string sql, object parameters = null)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    return DataMapper.MapToEntity<T>(reader);
                }
            }

            return default;
        }

        private async Task<T> ExecuteQuerySingleAsync<T>(
            DatabaseConnection connection,
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    return DataMapper.MapToEntity<T>(reader);
                }
            }

            return default;
        }

        private IEnumerable<T> ExecuteQueryList<T>(DatabaseConnection connection, string sql, object parameters = null)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            var results = new List<T>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    results.Add(DataMapper.MapToEntity<T>(reader));
                }
            }

            return results;
        }

        private async Task<IEnumerable<T>> ExecuteQueryListAsync<T>(
            DatabaseConnection connection,
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            var results = new List<T>();
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(DataMapper.MapToEntity<T>(reader));
                }
            }

            return results;
        }

        private int ExecuteNonQuery(
            DatabaseConnection connection,
            DbTransaction transaction,
            string sql,
            object parameters = null)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            return command.ExecuteNonQuery();
        }

        private async Task<int> ExecuteNonQueryAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private object ExecuteInsert(
            DatabaseConnection connection,
            DbTransaction transaction,
            string sql,
            object parameters = null)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            // Auto-generated ID'yi döndür;
            if (_config.UseAutoGeneratedIds)
            {
                command.CommandText += "; SELECT SCOPE_IDENTITY();";
                return command.ExecuteScalar();
            }

            command.ExecuteNonQuery();
            return null;
        }

        private async Task<object> ExecuteInsertAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            // Auto-generated ID'yi döndür;
            if (_config.UseAutoGeneratedIds)
            {
                command.CommandText += "; SELECT SCOPE_IDENTITY();";
                return await command.ExecuteScalarAsync(cancellationToken);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
            return null;
        }

        private async Task<T> ExecuteScalarAsync<T>(
            DatabaseConnection connection,
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result == null || result == DBNull.Value)
                return default;

            return (T)Convert.ChangeType(result, typeof(T));
        }

        private void AddParameters(DbCommand command, object parameters)
        {
            if (parameters is Dictionary<string, object> dict)
            {
                foreach (var kvp in dict)
                {
                    var param = command.CreateParameter();
                    param.ParameterName = kvp.Key;
                    param.Value = kvp.Value ?? DBNull.Value;
                    command.Parameters.Add(param);
                }
            }
            else;
            {
                // Reflection ile property'leri al;
                var properties = parameters.GetType().GetProperties();
                foreach (var prop in properties)
                {
                    var param = command.CreateParameter();
                    param.ParameterName = "@" + prop.Name;
                    param.Value = prop.GetValue(parameters) ?? DBNull.Value;
                    command.Parameters.Add(param);
                }
            }
        }
        #endregion;

        #region Event Methods;
        protected virtual void OnEntityAdded(EntityAddedEventArgs<TEntity> e)
        {
            EntityAdded?.Invoke(this, e);
        }

        protected virtual void OnEntityUpdated(EntityUpdatedEventArgs<TEntity> e)
        {
            EntityUpdated?.Invoke(this, e);
        }

        protected virtual void OnEntityDeleted(EntityDeletedEventArgs<TKey> e)
        {
            EntityDeleted?.Invoke(this, e);
        }

        protected virtual void OnCacheCleared(CacheClearedEventArgs e)
        {
            CacheCleared?.Invoke(this, e);
        }

        protected virtual void OnQueryExecuted(QueryExecutedEventArgs e)
        {
            QueryExecuted?.Invoke(this, e);
        }

        protected virtual void OnTransactionStarted(TransactionStartedEventArgs e)
        {
            TransactionStarted?.Invoke(this, e);
        }

        protected virtual void OnTransactionCommitted(TransactionCommittedEventArgs e)
        {
            TransactionCommitted?.Invoke(this, e);
        }

        protected virtual void OnTransactionRolledBack(TransactionRolledBackEventArgs e)
        {
            TransactionRolledBack?.Invoke(this, e);
        }
        #endregion;

        #region IDisposable Implementation;
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    State = RepositoryState.Disposing;

                    // Transaction'ı temizle;
                    lock (_transactionLock)
                    {
                        _currentTransaction?.Dispose();
                        _currentTransaction = null;
                    }

                    // Cache'i temizle;
                    _cache?.Dispose();

                    State = RepositoryState.Disposed;
                    _logger.Info($"DataRepository disposed for {typeof(TEntity).Name}");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~DataRepository()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    /// <summary>
    /// Repository durumları;
    /// </summary>
    public enum RepositoryState;
    {
        Ready,
        Busy,
        Error,
        Disposing,
        Disposed;
    }

    /// <summary>
    /// Sorgu tipi;
    /// </summary>
    public enum QueryType;
    {
        GetById,
        GetAll,
        Find,
        Paged,
        Custom,
        Count,
        Insert,
        Update,
        Delete;
    }

    /// <summary>
    /// Validasyon tipi;
    /// </summary>
    public enum ValidationType;
    {
        Insert,
        Update,
        Delete,
        Custom;
    }

    /// <summary>
    /// Repository konfigürasyonu;
    /// </summary>
    public class RepositoryConfig;
    {
        public bool EnableCaching { get; set; } = true;
        public bool CacheAllEntities { get; set; } = false;
        public int DefaultPageSize { get; set; } = 50;
        public int CommandTimeout { get; set; } = 30; // seconds;
        public bool EnableValidation { get; set; } = true;
        public bool UseAutoGeneratedIds { get; set; } = true;
        public int BulkOperationBatchSize { get; set; } = 1000;
        public bool EnableSoftDelete { get; set; } = false;
        public string SoftDeleteColumn { get; set; } = "IsDeleted";
        public bool EnableAuditLogging { get; set; } = false;
        public CacheConfig CacheConfig { get; set; } = CacheConfig.Default;

        public static RepositoryConfig Default => new RepositoryConfig();
    }

    /// <summary>
    /// Cache konfigürasyonu;
    /// </summary>
    public class CacheConfig;
    {
        public int MaxCacheSize { get; set; } = 10000;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(30);
        public bool EnableSlidingExpiration { get; set; } = true;
        public CacheEvictionPolicy EvictionPolicy { get; set; } = CacheEvictionPolicy.LRU;
        public bool EnableCompression { get; set; } = false;

        public static CacheConfig Default => new CacheConfig();
    }

    /// <summary>
    /// Cache eviction politikası;
    /// </summary>
    public enum CacheEvictionPolicy;
    {
        LRU,  // Least Recently Used;
        LFU,  // Least Frequently Used;
        FIFO, // First In First Out;
        Random;
    }

    /// <summary>
    /// Cache durumu;
    /// </summary>
    public class CacheStatus;
    {
        public bool IsEnabled { get; set; }
        public int CurrentSize { get; set; }
        public int MaxSize { get; set; }
        public double HitRate { get; set; }
        public DateTime LastCleared { get; set; }
        public CacheHealthStatus HealthStatus { get; set; }
    }

    /// <summary>
    /// Cache sağlık durumu;
    /// </summary>
    public enum CacheHealthStatus;
    {
        Healthy,
        Warning,
        Critical,
        Disabled;
    }

    /// <summary>
    /// Cache istatistikleri;
    /// </summary>
    public class CacheStatistics;
    {
        public int TotalRequests { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public double HitRate { get; set; }
        public int CurrentSize { get; set; }
        public int MaxSize { get; set; }
        public int EvictionCount { get; set; }
        public TimeSpan AverageRetrievalTime { get; set; }
    }

    /// <summary>
    /// Repository metrikleri;
    /// </summary>
    public class RepositoryMetrics;
    {
        public DateTime Timestamp { get; set; }
        public string EntityType { get; set; }
        public int TotalQueries { get; set; }
        public double CacheHitRate { get; set; }
        public int CacheSize { get; set; }
        public int CacheMaxSize { get; set; }
        public RepositoryState State { get; set; }
        public TimeSpan Uptime { get; set; }
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Repository durumu;
    /// </summary>
    public class RepositoryStatus;
    {
        public DateTime Timestamp { get; set; }
        public string EntityType { get; set; }
        public int QueryCount { get; set; }
        public CacheStatus CacheStatus { get; set; }
        public RepositoryState State { get; set; }
        public bool HasActiveTransaction { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Sayfalı sonuç;
    /// </summary>
    public class PagedResult<T>
    {
        public IEnumerable<T> Items { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;
    }

    /// <summary>
    /// Validasyon sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public Dictionary<string, string> FieldErrors { get; set; } = new Dictionary<string, string>();
    }

    // Event Args sınıfları;
    public class EntityAddedEventArgs<TEntity> : EventArgs;
    {
        public TEntity Entity { get; set; }
        public DateTime Timestamp { get; set; }
        public string OperationId { get; set; }
    }

    public class EntityUpdatedEventArgs<TEntity> : EventArgs;
    {
        public TEntity Entity { get; set; }
        public DateTime Timestamp { get; set; }
        public string OperationId { get; set; }
    }

    public class EntityDeletedEventArgs<TKey> : EventArgs;
    {
        public TKey EntityId { get; set; }
        public DateTime Timestamp { get; set; }
        public string OperationId { get; set; }
    }

    public class CacheClearedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public string CacheType { get; set; }
        public string Reason { get; set; }
    }

    public class QueryExecutedEventArgs : EventArgs;
    {
        public QueryType QueryType { get; set; }
        public string EntityType { get; set; }
        public DateTime ExecutionTime { get; set; }
        public bool CacheHit { get; set; }
        public int ResultCount { get; set; }
        public TimeSpan ExecutionDuration { get; set; }
    }

    public class TransactionStartedEventArgs : EventArgs;
    {
        public IsolationLevel IsolationLevel { get; set; }
        public DateTime Timestamp { get; set; }
        public string TransactionId { get; set; }
    }

    public class TransactionCommittedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public string TransactionId { get; set; }
    }

    public class TransactionRolledBackEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public string TransactionId { get; set; }
        public string Reason { get; set; }
    }

    // Exception sınıfı;
    public class RepositoryException : Exception
    {
        public ErrorCode ErrorCode { get; }

        public RepositoryException(ErrorCode errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Yardımcı sınıflar (kısmi implementasyon)
    internal class DataRepositoryCache<TEntity, TKey> : IDisposable
    {
        private readonly CacheConfig _config;
        private readonly ILogger _logger;
        private readonly Dictionary<TKey, CacheItem<TEntity>> _cache;
        private readonly object _cacheLock = new object();
        private int _totalRequests;
        private int _cacheHits;

        public DataRepositoryCache(CacheConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
            _cache = new Dictionary<TKey, CacheItem<TEntity>>();
        }

        public TEntity Get(TKey id)
        {
            lock (_cacheLock)
            {
                _totalRequests++;

                if (!_config.MaxCacheSize > 0 || _cache.Count == 0)
                    return default;

                if (_cache.TryGetValue(id, out var cacheItem))
                {
                    if (cacheItem.ExpiresAt < DateTime.UtcNow)
                    {
                        _cache.Remove(id);
                        return default;
                    }

                    // Sliding expiration;
                    if (_config.EnableSlidingExpiration)
                    {
                        cacheItem.LastAccessed = DateTime.UtcNow;
                        cacheItem.ExpiresAt = DateTime.UtcNow.Add(_config.CacheDuration);
                    }

                    _cacheHits++;
                    return cacheItem.Value;
                }

                return default;
            }
        }

        public IEnumerable<TEntity> GetAll()
        {
            lock (_cacheLock)
            {
                _totalRequests++;

                if (_cache.Count == 0)
                    return null;

                var now = DateTime.UtcNow;
                var validItems = _cache.Where(kv => kv.Value.ExpiresAt > now)
                                      .Select(kv => kv.Value.Value)
                                      .ToList();

                if (validItems.Count == _cache.Count)
                {
                    _cacheHits++;
                    return validItems;
                }

                // Expired items'ları temizle;
                var expiredKeys = _cache.Where(kv => kv.Value.ExpiresAt <= now)
                                       .Select(kv => kv.Key)
                                       .ToList();

                foreach (var key in expiredKeys)
                {
                    _cache.Remove(key);
                }

                return null;
            }
        }

        public void Set(TKey id, TEntity entity)
        {
            if (!_config.MaxCacheSize > 0)
                return;

            lock (_cacheLock)
            {
                // Cache size kontrolü;
                if (_cache.Count >= _config.MaxCacheSize)
                {
                    EvictCacheItems();
                }

                var cacheItem = new CacheItem<TEntity>
                {
                    Value = entity,
                    Created = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(_config.CacheDuration)
                };

                _cache[id] = cacheItem;
            }
        }

        public void SetAll(IEnumerable<TEntity> entities)
        {
            if (!_config.MaxCacheSize > 0 || !_config.CacheAllEntities)
                return;

            lock (_cacheLock)
            {
                _cache.Clear();

                foreach (var entity in entities)
                {
                    var idProperty = typeof(TEntity).GetProperty("Id");
                    if (idProperty != null)
                    {
                        var id = (TKey)idProperty.GetValue(entity);
                        Set(id, entity);
                    }
                }
            }
        }

        public void Remove(TKey id)
        {
            lock (_cacheLock)
            {
                _cache.Remove(id);
            }
        }

        public void Clear()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                _totalRequests = 0;
                _cacheHits = 0;
            }
        }

        public void InvalidateAllCache()
        {
            lock (_cacheLock)
            {
                _cache.Clear();
            }
        }

        public CacheStatus GetStatus()
        {
            lock (_cacheLock)
            {
                return new CacheStatus;
                {
                    IsEnabled = _config.MaxCacheSize > 0,
                    CurrentSize = _cache.Count,
                    MaxSize = _config.MaxCacheSize,
                    HitRate = _totalRequests > 0 ? (double)_cacheHits / _totalRequests * 100 : 0,
                    HealthStatus = GetHealthStatus()
                };
            }
        }

        public CacheStatistics GetStatistics()
        {
            lock (_cacheLock)
            {
                return new CacheStatistics;
                {
                    TotalRequests = _totalRequests,
                    CacheHits = _cacheHits,
                    CacheMisses = _totalRequests - _cacheHits,
                    HitRate = _totalRequests > 0 ? (double)_cacheHits / _totalRequests * 100 : 0,
                    CurrentSize = _cache.Count,
                    MaxSize = _config.MaxCacheSize,
                    EvictionCount = 0 // Takip edilmiyor şu an;
                };
            }
        }

        private void EvictCacheItems()
        {
            switch (_config.EvictionPolicy)
            {
                case CacheEvictionPolicy.LRU:
                    // En az kullanılanı sil;
                    var lruKey = _cache.OrderBy(kv => kv.Value.LastAccessed)
                                      .FirstOrDefault().Key;
                    if (lruKey != null)
                    {
                        _cache.Remove(lruKey);
                    }
                    break;

                case CacheEvictionPolicy.LFU:
                    // En az sıklıkta kullanılanı sil (basit implementasyon)
                    var lfuKey = _cache.OrderBy(kv => kv.Value.AccessCount)
                                      .FirstOrDefault().Key;
                    if (lfuKey != null)
                    {
                        _cache.Remove(lfuKey);
                    }
                    break;

                case CacheEvictionPolicy.FIFO:
                    // İlk ekleneni sil;
                    var fifoKey = _cache.OrderBy(kv => kv.Value.Created)
                                       .FirstOrDefault().Key;
                    if (fifoKey != null)
                    {
                        _cache.Remove(fifoKey);
                    }
                    break;

                case CacheEvictionPolicy.Random:
                    // Rastgele sil;
                    var randomKey = _cache.Keys.ElementAt(new Random().Next(_cache.Count));
                    _cache.Remove(randomKey);
                    break;
            }
        }

        private CacheHealthStatus GetHealthStatus()
        {
            if (_config.MaxCacheSize <= 0)
                return CacheHealthStatus.Disabled;

            var usage = (double)_cache.Count / _config.MaxCacheSize * 100;

            if (usage > 90)
                return CacheHealthStatus.Critical;
            else if (usage > 70)
                return CacheHealthStatus.Warning;
            else;
                return CacheHealthStatus.Healthy;
        }

        public void Dispose()
        {
            Clear();
        }

        private class CacheItem<T>
        {
            public T Value { get; set; }
            public DateTime Created { get; set; }
            public DateTime LastAccessed { get; set; }
            public DateTime ExpiresAt { get; set; }
            public int AccessCount { get; set; }
        }
    }

    internal class DataRepositoryValidator<TEntity, TKey>
    {
        private readonly RepositoryConfig _config;
        private readonly ILogger _logger;

        public DataRepositoryValidator(RepositoryConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public ValidationResult Validate(TEntity entity, ValidationType validationType)
        {
            var result = new ValidationResult { IsValid = true };

            if (entity == null)
            {
                result.IsValid = false;
                result.Errors.Add("Entity cannot be null");
                return result;
            }

            // Required field kontrolü;
            var requiredProperties = typeof(TEntity).GetProperties()
                .Where(p => p.GetCustomAttributes(typeof(RequiredAttribute), true).Any())
                .ToList();

            foreach (var prop in requiredProperties)
            {
                var value = prop.GetValue(entity);
                if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
                {
                    result.IsValid = false;
                    result.FieldErrors[prop.Name] = $"{prop.Name} is required";
                }
            }

            // String length kontrolü;
            var stringProperties = typeof(TEntity).GetProperties()
                .Where(p => p.PropertyType == typeof(string))
                .ToList();

            foreach (var prop in stringProperties)
            {
                var lengthAttr = prop.GetCustomAttributes(typeof(MaxLengthAttribute), true)
                                   .FirstOrDefault() as MaxLengthAttribute;

                if (lengthAttr != null)
                {
                    var value = prop.GetValue(entity) as string;
                    if (value != null && value.Length > lengthAttr.Length)
                    {
                        result.IsValid = false;
                        result.FieldErrors[prop.Name] =
                            $"{prop.Name} cannot exceed {lengthAttr.Length} characters";
                    }
                }
            }

            // Range kontrolü (sayısal değerler için)
            var numericProperties = typeof(TEntity).GetProperties()
                .Where(p => p.PropertyType.IsNumericType())
                .ToList();

            foreach (var prop in numericProperties)
            {
                var rangeAttr = prop.GetCustomAttributes(typeof(RangeAttribute), true)
                                  .FirstOrDefault() as RangeAttribute;

                if (rangeAttr != null)
                {
                    var value = Convert.ToDecimal(prop.GetValue(entity));
                    if (value < (decimal)rangeAttr.Minimum || value > (decimal)rangeAttr.Maximum)
                    {
                        result.IsValid = false;
                        result.FieldErrors[prop.Name] =
                            $"{prop.Name} must be between {rangeAttr.Minimum} and {rangeAttr.Maximum}";
                    }
                }
            }

            // Email format kontrolü;
            var emailProperties = typeof(TEntity).GetProperties()
                .Where(p => p.GetCustomAttributes(typeof(EmailAddressAttribute), true).Any())
                .ToList();

            foreach (var prop in emailProperties)
            {
                var value = prop.GetValue(entity) as string;
                if (!string.IsNullOrWhiteSpace(value) && !IsValidEmail(value))
                {
                    result.IsValid = false;
                    result.FieldErrors[prop.Name] = $"{prop.Name} is not a valid email address";
                }
            }

            return result;
        }

        public async Task<ValidationResult> ValidateAsync(
            TEntity entity,
            ValidationType validationType,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => Validate(entity, validationType), cancellationToken);
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }
    }

    internal class QueryBuilder<TEntity>
    {
        private readonly RepositoryConfig _config;
        private readonly ILogger _logger;
        private readonly string _tableName;
        private readonly List<string> _columnNames;

        public QueryBuilder(RepositoryConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;

            // Tablo adını belirle (convention veya attribute)
            var tableAttr = typeof(TEntity).GetCustomAttributes(typeof(TableAttribute), true)
                                         .FirstOrDefault() as TableAttribute;
            _tableName = tableAttr?.Name ?? typeof(TEntity).Name + "s";

            // Kolon adlarını belirle;
            _columnNames = typeof(TEntity).GetProperties()
                .Where(p => !p.GetCustomAttributes(typeof(NotMappedAttribute), true).Any())
                .Select(p =>
                {
                    var columnAttr = p.GetCustomAttributes(typeof(ColumnAttribute), true)
                                    .FirstOrDefault() as ColumnAttribute;
                    return columnAttr?.Name ?? p.Name;
                })
                .ToList();
        }

        public string BuildGetByIdQuery(TKey id)
        {
            var idColumn = GetIdColumnName();
            return $"SELECT * FROM {_tableName} WHERE {idColumn} = @Id";
        }

        public string BuildGetAllQuery()
        {
            var whereClause = _config.EnableSoftDelete;
                ? $" WHERE {_config.SoftDeleteColumn} = 0"
                : "";

            return $"SELECT * FROM {_tableName}{whereClause}";
        }

        public string BuildFindQuery(Expression<Func<TEntity, bool>> predicate)
        {
            var whereClause = ExpressionParser.Parse(predicate);
            var softDeleteClause = _config.EnableSoftDelete;
                ? $" AND {_config.SoftDeleteColumn} = 0"
                : "";

            return $"SELECT * FROM {_tableName} WHERE {whereClause}{softDeleteClause}";
        }

        public string BuildPagedQuery(
            int pageNumber,
            int pageSize,
            Expression<Func<TEntity, bool>> predicate = null,
            Expression<Func<TEntity, object>> orderBy = null,
            bool ascending = true)
        {
            var offset = (pageNumber - 1) * pageSize;
            var whereClause = predicate != null;
                ? $" WHERE {ExpressionParser.Parse(predicate)}"
                : "";

            var softDeleteClause = _config.EnableSoftDelete;
                ? $"{(string.IsNullOrEmpty(whereClause) ? " WHERE " : " AND ")}{_config.SoftDeleteColumn} = 0"
                : "";

            var orderByClause = orderBy != null;
                ? $" ORDER BY {ExpressionParser.ParseOrderBy(orderBy)} {(ascending ? "ASC" : "DESC")}"
                : $" ORDER BY {GetIdColumnName()}";

            return $"SELECT * FROM {_tableName}{whereClause}{softDeleteClause}{orderByClause} " +
                   $"OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";
        }

        public string BuildCountQuery(Expression<Func<TEntity, bool>> predicate = null)
        {
            var whereClause = predicate != null;
                ? $" WHERE {ExpressionParser.Parse(predicate)}"
                : "";

            var softDeleteClause = _config.EnableSoftDelete;
                ? $"{(string.IsNullOrEmpty(whereClause) ? " WHERE " : " AND ")}{_config.SoftDeleteColumn} = 0"
                : "";

            return $"SELECT COUNT(*) FROM {_tableName}{whereClause}{softDeleteClause}";
        }

        public string BuildInsertQuery(TEntity entity)
        {
            var columns = string.Join(", ", _columnNames);
            var parameters = string.Join(", ", _columnNames.Select(c => "@" + c));

            return $"INSERT INTO {_tableName} ({columns}) VALUES ({parameters})";
        }

        public string BuildUpdateQuery(TEntity entity)
        {
            var idColumn = GetIdColumnName();
            var setClause = string.Join(", ", _columnNames;
                .Where(c => c != idColumn)
                .Select(c => $"{c} = @{c}"));

            return $"UPDATE {_tableName} SET {setClause} WHERE {idColumn} = @Id";
        }

        public string BuildDeleteQuery(TKey id)
        {
            var idColumn = GetIdColumnName();

            if (_config.EnableSoftDelete)
            {
                return $"UPDATE {_tableName} SET {_config.SoftDeleteColumn} = 1 WHERE {idColumn} = @Id";
            }

            return $"DELETE FROM {_tableName} WHERE {idColumn} = @Id";
        }

        public object BuildIdParameter(TKey id)
        {
            return new { Id = id };
        }

        public object BuildInsertParameters(TEntity entity)
        {
            var parameters = new Dictionary<string, object>();

            foreach (var prop in typeof(TEntity).GetProperties())
            {
                if (prop.GetCustomAttributes(typeof(NotMappedAttribute), true).Any())
                    continue;

                var columnAttr = prop.GetCustomAttributes(typeof(ColumnAttribute), true)
                                   .FirstOrDefault() as ColumnAttribute;
                var columnName = columnAttr?.Name ?? prop.Name;

                parameters[columnName] = prop.GetValue(entity) ?? DBNull.Value;
            }

            return parameters;
        }

        public object BuildUpdateParameters(TEntity entity)
        {
            var parameters = BuildInsertParameters(entity);
            var idProperty = typeof(TEntity).GetProperty("Id");
            if (idProperty != null)
            {
                ((Dictionary<string, object>)parameters)["Id"] = idProperty.GetValue(entity);
            }

            return parameters;
        }

        public object BuildPredicateParameters(Expression<Func<TEntity, bool>> predicate)
        {
            return ExpressionParser.ExtractParameters(predicate);
        }

        public object BuildPagedParameters(
            int pageNumber,
            int pageSize,
            Expression<Func<TEntity, bool>> predicate = null)
        {
            var parameters = new Dictionary<string, object>();

            if (predicate != null)
            {
                var predicateParams = BuildPredicateParameters(predicate);
                if (predicateParams is Dictionary<string, object> dict)
                {
                    foreach (var kvp in dict)
                    {
                        parameters[kvp.Key] = kvp.Value;
                    }
                }
            }

            return parameters;
        }

        private string GetIdColumnName()
        {
            var idProperty = typeof(TEntity).GetProperty("Id");
            if (idProperty == null)
                throw new InvalidOperationException("Entity must have an Id property");

            var columnAttr = idProperty.GetCustomAttributes(typeof(ColumnAttribute), true)
                                     .FirstOrDefault() as ColumnAttribute;

            return columnAttr?.Name ?? "Id";
        }
    }

    internal class BulkOperations<TEntity>
    {
        private readonly RepositoryConfig _config;
        private readonly ILogger _logger;

        public BulkOperations(RepositoryConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<int> BulkInsertAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            List<TEntity> entities,
            CancellationToken cancellationToken = default)
        {
            // Batch processing;
            var insertedCount = 0;
            var batchSize = _config.BulkOperationBatchSize;

            for (int i = 0; i < entities.Count; i += batchSize)
            {
                var batch = entities.Skip(i).Take(batchSize).ToList();
                insertedCount += await ProcessBatchInsertAsync(connection, transaction, batch, cancellationToken);
            }

            return insertedCount;
        }

        private async Task<int> ProcessBatchInsertAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            List<TEntity> batch,
            CancellationToken cancellationToken)
        {
            // Burada bulk insert için database provider'a özgü implementasyon yapılmalı;
            // Örnek: SQL Server için SqlBulkCopy, PostgreSQL için COPY komutu;

            var queryBuilder = new QueryBuilder<TEntity>(_config, _logger);

            foreach (var entity in batch)
            {
                var query = queryBuilder.BuildInsertQuery(entity);
                var parameters = queryBuilder.BuildInsertParameters(entity);

                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = query;

                foreach (var param in ((Dictionary<string, object>)parameters))
                {
                    var dbParam = command.CreateParameter();
                    dbParam.ParameterName = "@" + param.Key;
                    dbParam.Value = param.Value ?? DBNull.Value;
                    command.Parameters.Add(dbParam);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return batch.Count;
        }

        public async Task<int> BulkUpdateAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            List<TEntity> entities,
            CancellationToken cancellationToken = default)
        {
            var updatedCount = 0;
            var batchSize = _config.BulkOperationBatchSize;

            for (int i = 0; i < entities.Count; i += batchSize)
            {
                var batch = entities.Skip(i).Take(batchSize).ToList();
                updatedCount += await ProcessBatchUpdateAsync(connection, transaction, batch, cancellationToken);
            }

            return updatedCount;
        }

        private async Task<int> ProcessBatchUpdateAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            List<TEntity> batch,
            CancellationToken cancellationToken)
        {
            var queryBuilder = new QueryBuilder<TEntity>(_config, _logger);

            foreach (var entity in batch)
            {
                var query = queryBuilder.BuildUpdateQuery(entity);
                var parameters = queryBuilder.BuildUpdateParameters(entity);

                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = query;

                foreach (var param in ((Dictionary<string, object>)parameters))
                {
                    var dbParam = command.CreateParameter();
                    dbParam.ParameterName = "@" + param.Key;
                    dbParam.Value = param.Value ?? DBNull.Value;
                    command.Parameters.Add(dbParam);
                }

                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0)
                {
                    updatedCount++;
                }
            }

            return updatedCount;
        }

        public async Task<int> BulkDeleteAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            List<TKey> ids,
            CancellationToken cancellationToken = default)
        {
            var deletedCount = 0;
            var batchSize = _config.BulkOperationBatchSize;

            for (int i = 0; i < ids.Count; i += batchSize)
            {
                var batch = ids.Skip(i).Take(batchSize).ToList();
                deletedCount += await ProcessBatchDeleteAsync(connection, transaction, batch, cancellationToken);
            }

            return deletedCount;
        }

        private async Task<int> ProcessBatchDeleteAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            List<TKey> batch,
            CancellationToken cancellationToken)
        {
            var queryBuilder = new QueryBuilder<TEntity>(_config, _logger);
            var idList = string.Join(",", batch.Select(id => $"'{id}'"));

            var query = $"DELETE FROM {typeof(TEntity).Name}s WHERE Id IN ({idList})";

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query;

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            return affected;
        }
    }

    // Attribute sınıfları;
    [AttributeUsage(AttributeTargets.Property)]
    public class RequiredAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property)]
    public class MaxLengthAttribute : Attribute;
    {
        public int Length { get; }
        public MaxLengthAttribute(int length) { Length = length; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class RangeAttribute : Attribute;
    {
        public object Minimum { get; }
        public object Maximum { get; }
        public RangeAttribute(double minimum, double maximum)
        {
            Minimum = minimum;
            Maximum = maximum;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class EmailAddressAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public class TableAttribute : Attribute;
    {
        public string Name { get; }
        public TableAttribute(string name) { Name = name; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class ColumnAttribute : Attribute;
    {
        public string Name { get; }
        public ColumnAttribute(string name) { Name = name; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class NotMappedAttribute : Attribute { }

    // Interface'ler;
    public interface IEntity<TKey>
    {
        TKey Id { get; set; }
    }

    // Static helper sınıfları;
    internal static class DataMapper;
    {
        public static T MapToEntity<T>(DbDataReader reader)
        {
            var entity = Activator.CreateInstance<T>();
            var properties = typeof(T).GetProperties();

            for (int i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                var property = properties.FirstOrDefault(p =>
                    p.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase) ||
                    p.GetCustomAttributes(typeof(ColumnAttribute), true)
                     .Cast<ColumnAttribute>()
                     .Any(a => a.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase)));

                if (property != null && property.CanWrite && !reader.IsDBNull(i))
                {
                    var value = reader.GetValue(i);
                    property.SetValue(entity, Convert.ChangeType(value, property.PropertyType));
                }
            }

            return entity;
        }
    }

    internal static class ExpressionParser;
    {
        public static string Parse(Expression<Func<TEntity, bool>> predicate)
        {
            // Basit implementasyon - gerçek projede ExpressionVisitor kullanılmalı;
            return predicate.Body.ToString();
        }

        public static string ParseOrderBy(Expression<Func<TEntity, object>> orderBy)
        {
            return orderBy.Body.ToString();
        }

        public static object ExtractParameters(Expression<Func<TEntity, bool>> predicate)
        {
            // Basit implementasyon;
            return new { };
        }
    }

    internal static class TypeExtensions;
    {
        public static bool IsNumericType(this Type type)
        {
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    return true;
                default:
                    return false;
            }
        }
    }
    #endregion;
}
