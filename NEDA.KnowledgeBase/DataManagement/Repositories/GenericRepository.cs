using Microsoft.EntityFrameworkCore;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.KnowledgeBase.DataManagement.DataAccess;
using NEDA.KnowledgeBase.LocalDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.KnowledgeBase.DataManagement.Repositories;
{
    /// <summary>
    /// Generic repository implementation with comprehensive CRUD operations;
    /// and advanced query capabilities for entity framework core;
    /// </summary>
    /// <typeparam name="TEntity">The entity type</typeparam>
    public class GenericRepository<TEntity> : IRepository<TEntity> where TEntity : class;
    {
        #region Fields and Properties;

        protected readonly DataContext _context;
        protected readonly DbSet<TEntity> _dbSet;
        protected readonly ILogger _logger;
        protected readonly IExceptionHandler _exceptionHandler;

        private readonly object _syncLock = new object();
        private bool _disposed = false;

        /// <summary>
        /// Current query statistics;
        /// </summary>
        public RepositoryStatistics Statistics { get; private set; }

        /// <summary>
        /// Last operation timestamp;
        /// </summary>
        public DateTime LastOperationTime { get; private set; }

        /// <summary>
        /// Total operations performed;
        /// </summary>
        public long TotalOperations { get; private set; }

        /// <summary>
        /// Entity type name;
        /// </summary>
        public string EntityTypeName => typeof(TEntity).Name;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of GenericRepository;
        /// </summary>
        public GenericRepository(DataContext context, ILogger logger, IExceptionHandler exceptionHandler)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dbSet = context.Set<TEntity>() ?? throw new InvalidOperationException($"DbSet not found for entity: {typeof(TEntity).Name}");
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));

            Statistics = new RepositoryStatistics();
            LastOperationTime = DateTime.UtcNow;
            TotalOperations = 0;

            _logger.LogInformation($"GenericRepository initialized for entity: {EntityTypeName}");
        }

        #endregion;

        #region CRUD Operations;

        /// <summary>
        /// Gets an entity by its primary key;
        /// </summary>
        public virtual async Task<TEntity> GetByIdAsync(object id, CancellationToken cancellationToken = default)
        {
            ValidateId(id);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var entity = await _dbSet.FindAsync(new[] { id }, cancellationToken);

                    UpdateStatistics(OperationType.GetById, entity != null);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"GetByIdAsync for {EntityTypeName}: {(entity != null ? "Found" : "Not found")}");

                    return entity;
                }, $"Failed to get {EntityTypeName} by id");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting {EntityTypeName} by id {id}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets all entities with optional filtering and sorting;
        /// </summary>
        public virtual async Task<IEnumerable<TEntity>> GetAllAsync(
            Expression<Func<TEntity, bool>> filter = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    IQueryable<TEntity> query = _dbSet;

                    // Apply filter if provided;
                    if (filter != null)
                    {
                        query = query.Where(filter);
                    }

                    // Apply ordering if provided;
                    if (orderBy != null)
                    {
                        query = orderBy(query);
                    }

                    var result = await query.ToListAsync(cancellationToken);

                    UpdateStatistics(OperationType.GetAll, result.Count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"GetAllAsync for {EntityTypeName}: {result.Count} records retrieved");

                    return result;
                }, $"Failed to get all {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting all {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets entities with pagination support;
        /// </summary>
        public virtual async Task<PagedResult<TEntity>> GetPagedAsync(
            int pageIndex,
            int pageSize,
            Expression<Func<TEntity, bool>> filter = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy = null,
            CancellationToken cancellationToken = default)
        {
            ValidatePagination(pageIndex, pageSize);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    IQueryable<TEntity> query = _dbSet;

                    // Apply filter if provided;
                    if (filter != null)
                    {
                        query = query.Where(filter);
                    }

                    // Get total count before pagination;
                    var totalCount = await query.CountAsync(cancellationToken);

                    // Apply ordering if provided;
                    if (orderBy != null)
                    {
                        query = orderBy(query);
                    }

                    // Apply pagination;
                    var items = await query;
                        .Skip((pageIndex - 1) * pageSize)
                        .Take(pageSize)
                        .ToListAsync(cancellationToken);

                    var result = new PagedResult<TEntity>
                    {
                        Items = items,
                        PageIndex = pageIndex,
                        PageSize = pageSize,
                        TotalCount = totalCount,
                        TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
                    };

                    UpdateStatistics(OperationType.GetPaged, items.Count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"GetPagedAsync for {EntityTypeName}: Page {pageIndex}/{result.TotalPages}, {items.Count} records");

                    return result;
                }, $"Failed to get paged {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting paged {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Finds entities based on a predicate;
        /// </summary>
        public virtual async Task<IEnumerable<TEntity>> FindAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            ValidatePredicate(predicate);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var result = await _dbSet.Where(predicate).ToListAsync(cancellationToken);

                    UpdateStatistics(OperationType.Find, result.Count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"FindAsync for {EntityTypeName}: {result.Count} records found");

                    return result;
                }, $"Failed to find {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error finding {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the first entity matching the predicate;
        /// </summary>
        public virtual async Task<TEntity> FirstOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            ValidatePredicate(predicate);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var entity = await _dbSet.FirstOrDefaultAsync(predicate, cancellationToken);

                    UpdateStatistics(OperationType.FirstOrDefault, entity != null);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"FirstOrDefaultAsync for {EntityTypeName}: {(entity != null ? "Found" : "Not found")}");

                    return entity;
                }, $"Failed to get first or default {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting first or default {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Checks if any entity matches the predicate;
        /// </summary>
        public virtual async Task<bool> AnyAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            ValidatePredicate(predicate);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var result = await _dbSet.AnyAsync(predicate, cancellationToken);

                    UpdateStatistics(OperationType.Any, 1);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"AnyAsync for {EntityTypeName}: {result}");

                    return result;
                }, $"Failed to check any {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking any {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the count of entities matching the predicate;
        /// </summary>
        public virtual async Task<int> CountAsync(
            Expression<Func<TEntity, bool>> predicate = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var count = predicate == null;
                        ? await _dbSet.CountAsync(cancellationToken)
                        : await _dbSet.CountAsync(predicate, cancellationToken);

                    UpdateStatistics(OperationType.Count, 1);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"CountAsync for {EntityTypeName}: {count}");

                    return count;
                }, $"Failed to count {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error counting {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Adds a new entity to the repository;
        /// </summary>
        public virtual async Task<TEntity> AddAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            ValidateEntity(entity);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    await _dbSet.AddAsync(entity, cancellationToken);

                    UpdateStatistics(OperationType.Add, 1);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"AddAsync for {EntityTypeName}: Entity added");

                    return entity;
                }, $"Failed to add {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Adds multiple entities to the repository;
        /// </summary>
        public virtual async Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        {
            ValidateEntities(entities);

            try
            {
                await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    await _dbSet.AddRangeAsync(entities, cancellationToken);

                    var count = entities.Count();
                    UpdateStatistics(OperationType.AddRange, count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"AddRangeAsync for {EntityTypeName}: {count} entities added");
                }, $"Failed to add range of {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding range of {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Updates an existing entity;
        /// </summary>
        public virtual Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            ValidateEntity(entity);

            try
            {
                return _exceptionHandler.ExecuteWithHandlingAsync(() =>
                {
                    _dbSet.Update(entity);

                    UpdateStatistics(OperationType.Update, 1);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"UpdateAsync for {EntityTypeName}: Entity updated");

                    return Task.CompletedTask;
                }, $"Failed to update {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Updates multiple entities;
        /// </summary>
        public virtual Task UpdateRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        {
            ValidateEntities(entities);

            try
            {
                return _exceptionHandler.ExecuteWithHandlingAsync(() =>
                {
                    _dbSet.UpdateRange(entities);

                    var count = entities.Count();
                    UpdateStatistics(OperationType.UpdateRange, count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"UpdateRangeAsync for {EntityTypeName}: {count} entities updated");

                    return Task.CompletedTask;
                }, $"Failed to update range of {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating range of {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Removes an entity by its primary key;
        /// </summary>
        public virtual async Task<bool> RemoveByIdAsync(object id, CancellationToken cancellationToken = default)
        {
            ValidateId(id);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var entity = await GetByIdAsync(id, cancellationToken);
                    if (entity == null)
                    {
                        _logger.LogWarning($"RemoveByIdAsync for {EntityTypeName}: Entity with id {id} not found");
                        return false;
                    }

                    _dbSet.Remove(entity);

                    UpdateStatistics(OperationType.Remove, 1);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"RemoveByIdAsync for {EntityTypeName}: Entity with id {id} removed");

                    return true;
                }, $"Failed to remove {EntityTypeName} by id");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing {EntityTypeName} by id {id}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Removes an entity;
        /// </summary>
        public virtual Task RemoveAsync(TEntity entity, CancellationToken cancellationToken = default)
        {
            ValidateEntity(entity);

            try
            {
                return _exceptionHandler.ExecuteWithHandlingAsync(() =>
                {
                    _dbSet.Remove(entity);

                    UpdateStatistics(OperationType.Remove, 1);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"RemoveAsync for {EntityTypeName}: Entity removed");

                    return Task.CompletedTask;
                }, $"Failed to remove {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Removes multiple entities;
        /// </summary>
        public virtual Task RemoveRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        {
            ValidateEntities(entities);

            try
            {
                return _exceptionHandler.ExecuteWithHandlingAsync(() =>
                {
                    _dbSet.RemoveRange(entities);

                    var count = entities.Count();
                    UpdateStatistics(OperationType.RemoveRange, count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"RemoveRangeAsync for {EntityTypeName}: {count} entities removed");

                    return Task.CompletedTask;
                }, $"Failed to remove range of {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing range of {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Removes entities based on a predicate;
        /// </summary>
        public virtual async Task<int> RemoveWhereAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            ValidatePredicate(predicate);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var entitiesToRemove = await _dbSet.Where(predicate).ToListAsync(cancellationToken);

                    if (entitiesToRemove.Any())
                    {
                        _dbSet.RemoveRange(entitiesToRemove);
                    }

                    var count = entitiesToRemove.Count;
                    UpdateStatistics(OperationType.RemoveWhere, count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"RemoveWhereAsync for {EntityTypeName}: {count} entities removed");

                    return count;
                }, $"Failed to remove {EntityTypeName} where predicate matches");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing {EntityTypeName} where predicate: {ex.Message}");
                throw;
            }
        }

        #endregion;

        #region Advanced Operations;

        /// <summary>
        /// Executes a custom query with projection;
        /// </summary>
        public virtual async Task<IEnumerable<TResult>> QueryAsync<TResult>(
            Func<IQueryable<TEntity>, IQueryable<TResult>> query,
            CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var queryable = query(_dbSet);
                    var result = await queryable.ToListAsync(cancellationToken);

                    UpdateStatistics(OperationType.CustomQuery, result.Count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"QueryAsync for {EntityTypeName}: {result.Count} records returned");

                    return result;
                }, $"Failed to execute custom query for {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing custom query for {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Executes a stored procedure or raw SQL query;
        /// </summary>
        public virtual async Task<IEnumerable<TEntity>> ExecuteSqlQueryAsync(
            string sql,
            params object[] parameters)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new ArgumentException("SQL query cannot be null or empty", nameof(sql));
            }

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var result = await _dbSet.FromSqlRaw(sql, parameters).ToListAsync();

                    UpdateStatistics(OperationType.SqlQuery, result.Count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"ExecuteSqlQueryAsync for {EntityTypeName}: {result.Count} records returned");

                    return result;
                }, $"Failed to execute SQL query for {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing SQL query for {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Executes a stored procedure or raw SQL command;
        /// </summary>
        public virtual async Task<int> ExecuteSqlCommandAsync(
            string sql,
            params object[] parameters)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new ArgumentException("SQL command cannot be null or empty", nameof(sql));
            }

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var result = await _context.Database.ExecuteSqlRawAsync(sql, parameters);

                    UpdateStatistics(OperationType.SqlCommand, result);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"ExecuteSqlCommandAsync for {EntityTypeName}: {result} rows affected");

                    return result;
                }, $"Failed to execute SQL command for {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing SQL command for {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Checks if entity exists by primary key;
        /// </summary>
        public virtual async Task<bool> ExistsAsync(object id, CancellationToken cancellationToken = default)
        {
            ValidateId(id);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var entity = await GetByIdAsync(id, cancellationToken);
                    var exists = entity != null;

                    UpdateStatistics(OperationType.Exists, 1);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"ExistsAsync for {EntityTypeName}: id {id} {(exists ? "exists" : "does not exist")}");

                    return exists;
                }, $"Failed to check existence of {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking existence of {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets entity with included related entities;
        /// </summary>
        public virtual async Task<TEntity> GetByIdWithIncludesAsync(
            object id,
            params Expression<Func<TEntity, object>>[] includeProperties)
        {
            ValidateId(id);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    IQueryable<TEntity> query = _dbSet;

                    // Include specified properties;
                    foreach (var includeProperty in includeProperties)
                    {
                        query = query.Include(includeProperty);
                    }

                    var entity = await query.FirstOrDefaultAsync(e => EF.Property<object>(e, "Id") == id);

                    UpdateStatistics(OperationType.GetWithIncludes, entity != null);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"GetByIdWithIncludesAsync for {EntityTypeName}: {(entity != null ? "Found" : "Not found")}");

                    return entity;
                }, $"Failed to get {EntityTypeName} with includes");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting {EntityTypeName} with includes: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Performs a batch update operation;
        /// </summary>
        public virtual async Task<int> BatchUpdateAsync(
            Expression<Func<TEntity, bool>> predicate,
            Expression<Func<TEntity, TEntity>> updateExpression,
            CancellationToken cancellationToken = default)
        {
            ValidatePredicate(predicate);

            if (updateExpression == null)
            {
                throw new ArgumentNullException(nameof(updateExpression));
            }

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var entities = await _dbSet.Where(predicate).ToListAsync(cancellationToken);

                    if (!entities.Any())
                    {
                        _logger.LogDebug($"BatchUpdateAsync for {EntityTypeName}: No entities to update");
                        return 0;
                    }

                    // Apply update expression to each entity;
                    foreach (var entity in entities)
                    {
                        // This is a simplified implementation;
                        // In real scenarios, you might use a more sophisticated approach;
                        var compiledUpdate = updateExpression.Compile();
                        compiledUpdate(entity);
                    }

                    var count = entities.Count;
                    UpdateStatistics(OperationType.BatchUpdate, count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"BatchUpdateAsync for {EntityTypeName}: {count} entities updated");

                    return count;
                }, $"Failed to batch update {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error batch updating {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Performs a batch delete operation;
        /// </summary>
        public virtual async Task<int> BatchDeleteAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default)
        {
            ValidatePredicate(predicate);

            try
            {
                return await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var entities = await _dbSet.Where(predicate).ToListAsync(cancellationToken);

                    if (!entities.Any())
                    {
                        _logger.LogDebug($"BatchDeleteAsync for {EntityTypeName}: No entities to delete");
                        return 0;
                    }

                    _dbSet.RemoveRange(entities);

                    var count = entities.Count;
                    UpdateStatistics(OperationType.BatchDelete, count);
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"BatchDeleteAsync for {EntityTypeName}: {count} entities deleted");

                    return count;
                }, $"Failed to batch delete {EntityTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error batch deleting {EntityTypeName}: {ex.Message}");
                throw;
            }
        }

        #endregion;

        #region Utility Methods;

        /// <summary>
        /// Resets repository statistics;
        /// </summary>
        public virtual void ResetStatistics()
        {
            lock (_syncLock)
            {
                Statistics = new RepositoryStatistics();
                TotalOperations = 0;
                LastOperationTime = DateTime.UtcNow;

                _logger.LogDebug($"Statistics reset for {EntityTypeName} repository");
            }
        }

        /// <summary>
        /// Gets repository statistics;
        /// </summary>
        public virtual RepositoryStatistics GetStatistics()
        {
            lock (_syncLock)
            {
                return new RepositoryStatistics;
                {
                    TotalOperations = Statistics.TotalOperations,
                    SuccessfulOperations = Statistics.SuccessfulOperations,
                    FailedOperations = Statistics.FailedOperations,
                    OperationCounts = new Dictionary<OperationType, long>(Statistics.OperationCounts),
                    AverageOperationTime = Statistics.AverageOperationTime,
                    LastOperation = Statistics.LastOperation;
                };
            }
        }

        /// <summary>
        /// Validates entity before operation;
        /// </summary>
        protected virtual void ValidateEntity(TEntity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }
        }

        /// <summary>
        /// Validates entities collection before operation;
        /// </summary>
        protected virtual void ValidateEntities(IEnumerable<TEntity> entities)
        {
            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (!entities.Any())
            {
                throw new ArgumentException("Entities collection cannot be empty", nameof(entities));
            }
        }

        /// <summary>
        /// Validates predicate before operation;
        /// </summary>
        protected virtual void ValidatePredicate(Expression<Func<TEntity, bool>> predicate)
        {
            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }
        }

        /// <summary>
        /// Validates id before operation;
        /// </summary>
        protected virtual void ValidateId(object id)
        {
            if (id == null)
            {
                throw new ArgumentNullException(nameof(id));
            }
        }

        /// <summary>
        /// Validates pagination parameters;
        /// </summary>
        protected virtual void ValidatePagination(int pageIndex, int pageSize)
        {
            if (pageIndex < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index must be greater than 0");
            }

            if (pageSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than 0");
            }

            if (pageSize > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size cannot exceed 1000");
            }
        }

        /// <summary>
        /// Updates statistics after operation;
        /// </summary>
        protected virtual void UpdateStatistics(OperationType operationType, int itemsAffected)
        {
            lock (_syncLock)
            {
                TotalOperations++;

                Statistics.TotalOperations++;
                Statistics.OperationCounts[operationType] = Statistics.OperationCounts.GetValueOrDefault(operationType) + 1;

                if (itemsAffected > 0)
                {
                    Statistics.SuccessfulOperations++;
                }
                else if (operationType != OperationType.Any && operationType != OperationType.Exists && operationType != OperationType.Count)
                {
                    Statistics.FailedOperations++;
                }

                Statistics.LastOperation = operationType;
            }
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Context is disposed by UnitOfWork or DI container;
                    // No need to dispose here;
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Generic repository interface;
    /// </summary>
    /// <typeparam name="TEntity">The entity type</typeparam>
    public interface IRepository<TEntity> : IDisposable where TEntity : class;
    {
        /// <summary>
        /// Current query statistics;
        /// </summary>
        RepositoryStatistics Statistics { get; }

        /// <summary>
        /// Last operation timestamp;
        /// </summary>
        DateTime LastOperationTime { get; }

        /// <summary>
        /// Total operations performed;
        /// </summary>
        long TotalOperations { get; }

        /// <summary>
        /// Entity type name;
        /// </summary>
        string EntityTypeName { get; }

        // CRUD Operations;
        Task<TEntity> GetByIdAsync(object id, CancellationToken cancellationToken = default);
        Task<IEnumerable<TEntity>> GetAllAsync(
            Expression<Func<TEntity, bool>> filter = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy = null,
            CancellationToken cancellationToken = default);
        Task<PagedResult<TEntity>> GetPagedAsync(
            int pageIndex,
            int pageSize,
            Expression<Func<TEntity, bool>> filter = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy = null,
            CancellationToken cancellationToken = default);
        Task<IEnumerable<TEntity>> FindAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);
        Task<TEntity> FirstOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);
        Task<bool> AnyAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);
        Task<int> CountAsync(
            Expression<Func<TEntity, bool>> predicate = null,
            CancellationToken cancellationToken = default);
        Task<TEntity> AddAsync(TEntity entity, CancellationToken cancellationToken = default);
        Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);
        Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);
        Task UpdateRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);
        Task<bool> RemoveByIdAsync(object id, CancellationToken cancellationToken = default);
        Task RemoveAsync(TEntity entity, CancellationToken cancellationToken = default);
        Task RemoveRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);
        Task<int> RemoveWhereAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        // Advanced Operations;
        Task<IEnumerable<TResult>> QueryAsync<TResult>(
            Func<IQueryable<TEntity>, IQueryable<TResult>> query,
            CancellationToken cancellationToken = default);
        Task<IEnumerable<TEntity>> ExecuteSqlQueryAsync(
            string sql,
            params object[] parameters);
        Task<int> ExecuteSqlCommandAsync(
            string sql,
            params object[] parameters);
        Task<bool> ExistsAsync(object id, CancellationToken cancellationToken = default);
        Task<TEntity> GetByIdWithIncludesAsync(
            object id,
            params Expression<Func<TEntity, object>>[] includeProperties);
        Task<int> BatchUpdateAsync(
            Expression<Func<TEntity, bool>> predicate,
            Expression<Func<TEntity, TEntity>> updateExpression,
            CancellationToken cancellationToken = default);
        Task<int> BatchDeleteAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        // Utility Methods;
        void ResetStatistics();
        RepositoryStatistics GetStatistics();
    }

    /// <summary>
    /// Repository operation types;
    /// </summary>
    public enum OperationType;
    {
        GetById,
        GetAll,
        GetPaged,
        Find,
        FirstOrDefault,
        Any,
        Count,
        Add,
        AddRange,
        Update,
        UpdateRange,
        Remove,
        RemoveRange,
        RemoveWhere,
        CustomQuery,
        SqlQuery,
        SqlCommand,
        Exists,
        GetWithIncludes,
        BatchUpdate,
        BatchDelete;
    }

    /// <summary>
    /// Repository statistics;
    /// </summary>
    public class RepositoryStatistics;
    {
        public long TotalOperations { get; set; }
        public long SuccessfulOperations { get; set; }
        public long FailedOperations { get; set; }
        public Dictionary<OperationType, long> OperationCounts { get; set; } = new Dictionary<OperationType, long>();
        public double AverageOperationTime { get; set; }
        public OperationType LastOperation { get; set; }
    }

    /// <summary>
    /// Paged result for paginated queries;
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    public class PagedResult<T>
    {
        public IEnumerable<T> Items { get; set; }
        public int PageIndex { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }

        public bool HasPreviousPage => PageIndex > 1;
        public bool HasNextPage => PageIndex < TotalPages;
    }

    #endregion;
}
