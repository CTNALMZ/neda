using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.KnowledgeBase.DataManagement.Repositories;
{
    /// <summary>
    /// Generic repository interface defining the contract for data access operations;
    /// Provides a unified API for CRUD operations and advanced querying capabilities;
    /// </summary>
    /// <typeparam name="TEntity">The entity type managed by this repository</typeparam>
    public interface IRepository<TEntity> : IDisposable where TEntity : class;
    {
        #region Properties;

        /// <summary>
        /// Gets current repository statistics including operation counts and performance metrics;
        /// </summary>
        RepositoryStatistics Statistics { get; }

        /// <summary>
        /// Gets the timestamp of the last operation performed by this repository;
        /// </summary>
        DateTime LastOperationTime { get; }

        /// <summary>
        /// Gets the total number of operations performed by this repository;
        /// </summary>
        long TotalOperations { get; }

        /// <summary>
        /// Gets the name of the entity type managed by this repository;
        /// </summary>
        string EntityTypeName { get; }

        #endregion;

        #region Basic CRUD Operations;

        /// <summary>
        /// Retrieves an entity by its primary key asynchronously;
        /// </summary>
        /// <param name="id">The primary key value of the entity to retrieve</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>The entity if found, otherwise null</returns>
        /// <exception cref="ArgumentNullException">Thrown when id is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<TEntity> GetByIdAsync(object id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves all entities from the repository with optional filtering and ordering;
        /// </summary>
        /// <param name="filter">Optional filter expression to apply to the query</param>
        /// <param name="orderBy">Optional ordering function to sort the results</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Collection of entities matching the criteria</returns>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<IEnumerable<TEntity>> GetAllAsync(
            Expression<Func<TEntity, bool>> filter = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a paged subset of entities with optional filtering and ordering;
        /// </summary>
        /// <param name="pageIndex">The page index (1-based)</param>
        /// <param name="pageSize">The number of items per page</param>
        /// <param name="filter">Optional filter expression to apply to the query</param>
        /// <param name="orderBy">Optional ordering function to sort the results</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Paged result containing the requested page of entities</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when pageIndex or pageSize is invalid</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<PagedResult<TEntity>> GetPagedAsync(
            int pageIndex,
            int pageSize,
            Expression<Func<TEntity, bool>> filter = null,
            Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>> orderBy = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Finds entities that match the specified predicate;
        /// </summary>
        /// <param name="predicate">The predicate to filter entities</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Collection of entities matching the predicate</returns>
        /// <exception cref="ArgumentNullException">Thrown when predicate is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<IEnumerable<TEntity>> FindAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the first entity that matches the predicate, or null if no match is found;
        /// </summary>
        /// <param name="predicate">The predicate to filter entities</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>The first matching entity or null</returns>
        /// <exception cref="ArgumentNullException">Thrown when predicate is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<TEntity> FirstOrDefaultAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Determines whether any entity matches the specified predicate;
        /// </summary>
        /// <param name="predicate">The predicate to test entities against</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>True if any entity matches the predicate, otherwise false</returns>
        /// <exception cref="ArgumentNullException">Thrown when predicate is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<bool> AnyAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the number of entities that match the specified predicate;
        /// </summary>
        /// <param name="predicate">Optional predicate to filter entities</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>The count of matching entities</returns>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<int> CountAsync(
            Expression<Func<TEntity, bool>> predicate = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds a new entity to the repository;
        /// </summary>
        /// <param name="entity">The entity to add</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>The added entity</returns>
        /// <exception cref="ArgumentNullException">Thrown when entity is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<TEntity> AddAsync(TEntity entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds a collection of entities to the repository;
        /// </summary>
        /// <param name="entities">The entities to add</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentNullException">Thrown when entities is null or empty</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing entity in the repository;
        /// </summary>
        /// <param name="entity">The entity to update</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentNullException">Thrown when entity is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates a collection of entities in the repository;
        /// </summary>
        /// <param name="entities">The entities to update</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentNullException">Thrown when entities is null or empty</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task UpdateRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes an entity by its primary key;
        /// </summary>
        /// <param name="id">The primary key value of the entity to remove</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>True if the entity was found and removed, otherwise false</returns>
        /// <exception cref="ArgumentNullException">Thrown when id is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<bool> RemoveByIdAsync(object id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes an entity from the repository;
        /// </summary>
        /// <param name="entity">The entity to remove</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentNullException">Thrown when entity is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task RemoveAsync(TEntity entity, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes a collection of entities from the repository;
        /// </summary>
        /// <param name="entities">The entities to remove</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Task representing the asynchronous operation</returns>
        /// <exception cref="ArgumentNullException">Thrown when entities is null or empty</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task RemoveRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes entities that match the specified predicate;
        /// </summary>
        /// <param name="predicate">The predicate to filter entities for removal</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>The number of entities removed</returns>
        /// <exception cref="ArgumentNullException">Thrown when predicate is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<int> RemoveWhereAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        #endregion;

        #region Advanced Query Operations;

        /// <summary>
        /// Executes a custom query with projection to a different result type;
        /// </summary>
        /// <typeparam name="TResult">The type of the result elements</typeparam>
        /// <param name="query">The query function that defines the projection</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>Collection of projected results</returns>
        /// <exception cref="ArgumentNullException">Thrown when query is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<IEnumerable<TResult>> QueryAsync<TResult>(
            Func<IQueryable<TEntity>, IQueryable<TResult>> query,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a raw SQL query and returns entities;
        /// </summary>
        /// <param name="sql">The SQL query string</param>
        /// <param name="parameters">Parameters for the SQL query</param>
        /// <returns>Collection of entities returned by the query</returns>
        /// <exception cref="ArgumentException">Thrown when sql is null or empty</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<IEnumerable<TEntity>> ExecuteSqlQueryAsync(
            string sql,
            params object[] parameters);

        /// <summary>
        /// Executes a raw SQL command (INSERT, UPDATE, DELETE)
        /// </summary>
        /// <param name="sql">The SQL command string</param>
        /// <param name="parameters">Parameters for the SQL command</param>
        /// <returns>The number of rows affected</returns>
        /// <exception cref="ArgumentException">Thrown when sql is null or empty</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<int> ExecuteSqlCommandAsync(
            string sql,
            params object[] parameters);

        /// <summary>
        /// Checks whether an entity with the specified primary key exists;
        /// </summary>
        /// <param name="id">The primary key value to check</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>True if an entity with the specified key exists, otherwise false</returns>
        /// <exception cref="ArgumentNullException">Thrown when id is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<bool> ExistsAsync(object id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves an entity by its primary key with related entities included (eager loading)
        /// </summary>
        /// <param name="id">The primary key value of the entity to retrieve</param>
        /// <param name="includeProperties">Expressions specifying which related entities to include</param>
        /// <returns>The entity with included related entities if found, otherwise null</returns>
        /// <exception cref="ArgumentNullException">Thrown when id is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<TEntity> GetByIdWithIncludesAsync(
            object id,
            params Expression<Func<TEntity, object>>[] includeProperties);

        /// <summary>
        /// Performs a batch update operation on entities matching the specified predicate;
        /// </summary>
        /// <param name="predicate">The predicate to filter entities for update</param>
        /// <param name="updateExpression">The expression defining how to update the entities</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>The number of entities updated</returns>
        /// <exception cref="ArgumentNullException">Thrown when predicate or updateExpression is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<int> BatchUpdateAsync(
            Expression<Func<TEntity, bool>> predicate,
            Expression<Func<TEntity, TEntity>> updateExpression,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Performs a batch delete operation on entities matching the specified predicate;
        /// </summary>
        /// <param name="predicate">The predicate to filter entities for deletion</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
        /// <returns>The number of entities deleted</returns>
        /// <exception cref="ArgumentNullException">Thrown when predicate is null</exception>
        /// <exception cref="RepositoryException">Thrown when the operation fails</exception>
        Task<int> BatchDeleteAsync(
            Expression<Func<TEntity, bool>> predicate,
            CancellationToken cancellationToken = default);

        #endregion;

        #region Utility Methods;

        /// <summary>
        /// Resets all repository statistics to their initial values;
        /// </summary>
        void ResetStatistics();

        /// <summary>
        /// Gets a copy of the current repository statistics;
        /// </summary>
        /// <returns>A new instance containing the current statistics</returns>
        RepositoryStatistics GetStatistics();

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Enumeration of repository operation types for statistical tracking;
    /// </summary>
    public enum OperationType;
    {
        /// <summary>Retrieve entity by primary key</summary>
        GetById,

        /// <summary>Retrieve all entities</summary>
        GetAll,

        /// <summary>Retrieve paged entities</summary>
        GetPaged,

        /// <summary>Find entities matching predicate</summary>
        Find,

        /// <summary>Get first entity matching predicate</summary>
        FirstOrDefault,

        /// <summary>Check if any entity matches predicate</summary>
        Any,

        /// <summary>Count entities</summary>
        Count,

        /// <summary>Add single entity</summary>
        Add,

        /// <summary>Add multiple entities</summary>
        AddRange,

        /// <summary>Update single entity</summary>
        Update,

        /// <summary>Update multiple entities</summary>
        UpdateRange,

        /// <summary>Remove entity</summary>
        Remove,

        /// <summary>Remove multiple entities</summary>
        RemoveRange,

        /// <summary>Remove entities matching predicate</summary>
        RemoveWhere,

        /// <summary>Execute custom query</summary>
        CustomQuery,

        /// <summary>Execute SQL query</summary>
        SqlQuery,

        /// <summary>Execute SQL command</summary>
        SqlCommand,

        /// <summary>Check entity existence</summary>
        Exists,

        /// <summary>Get entity with included relations</summary>
        GetWithIncludes,

        /// <summary>Perform batch update</summary>
        BatchUpdate,

        /// <summary>Perform batch delete</summary>
        BatchDelete;
    }

    /// <summary>
    /// Repository statistics for monitoring and diagnostics;
    /// </summary>
    public class RepositoryStatistics;
    {
        /// <summary>
        /// Total number of operations performed;
        /// </summary>
        public long TotalOperations { get; set; }

        /// <summary>
        /// Number of successful operations;
        /// </summary>
        public long SuccessfulOperations { get; set; }

        /// <summary>
        /// Number of failed operations;
        /// </summary>
        public long FailedOperations { get; set; }

        /// <summary>
        /// Count of operations by type;
        /// </summary>
        public Dictionary<OperationType, long> OperationCounts { get; set; } = new Dictionary<OperationType, long>();

        /// <summary>
        /// Average operation execution time in milliseconds;
        /// </summary>
        public double AverageOperationTime { get; set; }

        /// <summary>
        /// Type of the last operation performed;
        /// </summary>
        public OperationType LastOperation { get; set; }
    }

    /// <summary>
    /// Paged result container for paginated queries;
    /// </summary>
    /// <typeparam name="T">Type of items in the result</typeparam>
    public class PagedResult<T>
    {
        /// <summary>
        /// The items on the current page;
        /// </summary>
        public IEnumerable<T> Items { get; set; }

        /// <summary>
        /// Current page index (1-based)
        /// </summary>
        public int PageIndex { get; set; }

        /// <summary>
        /// Number of items per page;
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// Total number of items across all pages;
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// Total number of pages;
        /// </summary>
        public int TotalPages { get; set; }

        /// <summary>
        /// Indicates whether there is a previous page;
        /// </summary>
        public bool HasPreviousPage => PageIndex > 1;

        /// <summary>
        /// Indicates whether there is a next page;
        /// </summary>
        public bool HasNextPage => PageIndex < TotalPages;
    }

    /// <summary>
    /// Exception thrown when repository operations fail;
    /// </summary>
    public class RepositoryException : Exception
    {
        /// <summary>
        /// Initializes a new instance of RepositoryException;
        /// </summary>
        public RepositoryException() { }

        /// <summary>
        /// Initializes a new instance of RepositoryException with a message;
        /// </summary>
        public RepositoryException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of RepositoryException with a message and inner exception;
        /// </summary>
        public RepositoryException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
