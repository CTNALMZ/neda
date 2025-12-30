using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Common;
using NEDA.KnowledgeBase.DataManagement.Repositories;
using NEDA.KnowledgeBase.LocalDB;

namespace NEDA.KnowledgeBase.DataManagement.DataAccess;
{
    /// <summary>
    /// Unit of Work pattern implementation for managing database transactions;
    /// and coordinating work across multiple repositories with a single context;
    /// </summary>
    public class UnitOfWork : IUnitOfWork, IDisposable;
    {
        #region Fields and Properties;

        private readonly DataContext _context;
        private readonly ILogger _logger;
        private readonly IExceptionHandler _exceptionHandler;

        private IDbContextTransaction _currentTransaction;
        private bool _disposed;
        private readonly object _syncLock = new object();

        private readonly Dictionary<Type, object> _repositories;
        private readonly Dictionary<string, object> _customRepositories;

        private bool _isTransactionActive;
        private IsolationLevel _currentIsolationLevel;
        private readonly Stack<TransactionScope> _transactionScopes;

        /// <summary>
        /// Current transaction status;
        /// </summary>
        public TransactionStatus TransactionStatus { get; private set; }

        /// <summary>
        /// Current transaction ID;
        /// </summary>
        public Guid? CurrentTransactionId { get; private set; }

        /// <summary>
        /// Total operations performed in current unit;
        /// </summary>
        public int OperationsCount { get; private set; }

        /// <summary>
        /// Last operation timestamp;
        /// </summary>
        public DateTime LastOperationTime { get; private set; }

        /// <summary>
        /// Indicates if the unit of work is in a transaction;
        /// </summary>
        public bool IsInTransaction => _isTransactionActive || (_currentTransaction != null);

        /// <summary>
        /// Database connection state;
        /// </summary>
        public ConnectionState ConnectionState => GetConnectionState();

        #endregion;

        #region Events;

        /// <summary>
        /// Event triggered when transaction begins;
        /// </summary>
        public event EventHandler<TransactionStartedEventArgs> TransactionStarted;

        /// <summary>
        /// Event triggered when transaction commits;
        /// </summary>
        public event EventHandler<TransactionCommittedEventArgs> TransactionCommitted;

        /// <summary>
        /// Event triggered when transaction rolls back;
        /// </summary>
        public event EventHandler<TransactionRolledBackEventArgs> TransactionRolledBack;

        /// <summary>
        /// Event triggered when operation is performed;
        /// </summary>
        public event EventHandler<OperationPerformedEventArgs> OperationPerformed;

        /// <summary>
        /// Event triggered when unit of work is disposed;
        /// </summary>
        public event EventHandler<UnitOfWorkDisposedEventArgs> UnitOfWorkDisposed;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of UnitOfWork;
        /// </summary>
        public UnitOfWork(DataContext context, ILogger logger, IExceptionHandler exceptionHandler)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));

            _repositories = new Dictionary<Type, object>();
            _customRepositories = new Dictionary<string, object>();
            _transactionScopes = new Stack<TransactionScope>();

            TransactionStatus = TransactionStatus.NoTransaction;
            _isTransactionActive = false;
            _disposed = false;
            OperationsCount = 0;
            LastOperationTime = DateTime.UtcNow;

            _logger.LogInformation("UnitOfWork initialized");
        }

        #endregion;

        #region Repository Access;

        /// <summary>
        /// Gets the repository for the specified entity type;
        /// </summary>
        public IRepository<TEntity> GetRepository<TEntity>() where TEntity : class;
        {
            CheckDisposed();

            var type = typeof(TEntity);

            lock (_syncLock)
            {
                if (!_repositories.TryGetValue(type, out var repository))
                {
                    repository = new GenericRepository<TEntity>(_context);
                    _repositories[type] = repository;

                    _logger.LogDebug($"Repository created for entity: {type.Name}");
                }

                return (IRepository<TEntity>)repository;
            }
        }

        /// <summary>
        /// Gets a custom repository by type;
        /// </summary>
        public TRepository GetCustomRepository<TRepository>() where TRepository : class;
        {
            CheckDisposed();

            var type = typeof(TRepository);

            lock (_syncLock)
            {
                if (!_customRepositories.TryGetValue(type.FullName, out var repository))
                {
                    // Create instance using dependency injection or reflection;
                    repository = CreateCustomRepository<TRepository>();
                    _customRepositories[type.FullName] = repository;

                    _logger.LogDebug($"Custom repository created: {type.Name}");
                }

                return (TRepository)repository;
            }
        }

        /// <summary>
        /// Registers a custom repository instance;
        /// </summary>
        public void RegisterCustomRepository<TRepository>(TRepository repository) where TRepository : class;
        {
            if (repository == null)
            {
                throw new ArgumentNullException(nameof(repository));
            }

            CheckDisposed();

            var type = typeof(TRepository);

            lock (_syncLock)
            {
                _customRepositories[type.FullName] = repository;
                _logger.LogDebug($"Custom repository registered: {type.Name}");
            }
        }

        /// <summary>
        /// Creates a specific repository for the entity type;
        /// </summary>
        public IRepository<TEntity> CreateSpecificRepository<TEntity>() where TEntity : class;
        {
            CheckDisposed();

            // This allows for creating specialized repositories that implement IRepository<T>
            return new GenericRepository<TEntity>(_context);
        }

        #endregion;

        #region Transaction Management;

        /// <summary>
        /// Begins a new transaction with default isolation level;
        /// </summary>
        public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            await BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        }

        /// <summary>
        /// Begins a new transaction with specified isolation level;
        /// </summary>
        public async Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            if (_isTransactionActive)
            {
                throw new InvalidOperationException("A transaction is already active");
            }

            try
            {
                await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    _currentTransaction = await _context.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
                    _currentIsolationLevel = isolationLevel;
                    _isTransactionActive = true;
                    CurrentTransactionId = Guid.NewGuid();
                    TransactionStatus = TransactionStatus.Active;

                    _logger.LogInformation($"Transaction started: {CurrentTransactionId}, Isolation: {isolationLevel}");

                    TransactionStarted?.Invoke(this, new TransactionStartedEventArgs;
                    {
                        TransactionId = CurrentTransactionId.Value,
                        IsolationLevel = isolationLevel,
                        Timestamp = DateTime.UtcNow;
                    });
                }, "Failed to begin transaction");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error beginning transaction: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Creates a transaction scope for distributed transactions;
        /// </summary>
        public TransactionScope CreateTransactionScope(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            TimeSpan? timeout = null)
        {
            CheckDisposed();

            var transactionOptions = new TransactionOptions;
            {
                IsolationLevel = System.Transactions.IsolationLevel.ReadCommitted,
                Timeout = timeout ?? TimeSpan.FromMinutes(5)
            };

            // Convert EF isolation level to System.Transactions isolation level;
            transactionOptions.IsolationLevel = ConvertIsolationLevel(isolationLevel);

            var scope = new TransactionScope(
                TransactionScopeOption.Required,
                transactionOptions,
                TransactionScopeAsyncFlowOption.Enabled);

            _transactionScopes.Push(scope);
            _isTransactionActive = true;
            TransactionStatus = TransactionStatus.Active;

            _logger.LogDebug($"Transaction scope created with timeout: {transactionOptions.Timeout}");

            return scope;
        }

        /// <summary>
        /// Commits the current transaction;
        /// </summary>
        public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            if (!_isTransactionActive || _currentTransaction == null)
            {
                throw new InvalidOperationException("No active transaction to commit");
            }

            try
            {
                await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    await SaveChangesAsync(cancellationToken);
                    await _currentTransaction.CommitAsync(cancellationToken);

                    _logger.LogInformation($"Transaction committed: {CurrentTransactionId}");

                    TransactionCommitted?.Invoke(this, new TransactionCommittedEventArgs;
                    {
                        TransactionId = CurrentTransactionId.Value,
                        OperationsCount = OperationsCount,
                        Timestamp = DateTime.UtcNow;
                    });

                    ResetTransactionState();
                }, "Failed to commit transaction");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error committing transaction: {ex.Message}");
                await RollbackTransactionAsync(cancellationToken);
                throw;
            }
        }

        /// <summary>
        /// Rolls back the current transaction;
        /// </summary>
        public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            if (!_isTransactionActive || _currentTransaction == null)
            {
                throw new InvalidOperationException("No active transaction to rollback");
            }

            try
            {
                await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    await _currentTransaction.RollbackAsync(cancellationToken);

                    _logger.LogWarning($"Transaction rolled back: {CurrentTransactionId}");

                    TransactionRolledBack?.Invoke(this, new TransactionRolledBackEventArgs;
                    {
                        TransactionId = CurrentTransactionId.Value,
                        OperationsCount = OperationsCount,
                        Timestamp = DateTime.UtcNow,
                        Reason = "Explicit rollback"
                    });

                    ResetTransactionState();
                }, "Failed to rollback transaction");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error rolling back transaction: {ex.Message}");
                ResetTransactionState();
                throw;
            }
        }

        /// <summary>
        /// Executes an operation within a transaction;
        /// </summary>
        public async Task ExecuteInTransactionAsync(Func<Task> operation,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            CheckDisposed();

            bool transactionStartedHere = false;

            try
            {
                // Start transaction if not already in one;
                if (!_isTransactionActive)
                {
                    await BeginTransactionAsync(isolationLevel, cancellationToken);
                    transactionStartedHere = true;
                }

                // Execute the operation;
                await operation();

                // Commit if we started the transaction;
                if (transactionStartedHere)
                {
                    await CommitTransactionAsync(cancellationToken);
                }
            }
            catch (Exception)
            {
                // Rollback if we started the transaction;
                if (transactionStartedHere)
                {
                    await RollbackTransactionAsync(cancellationToken);
                }
                throw;
            }
        }

        /// <summary>
        /// Executes an operation within a transaction and returns result;
        /// </summary>
        public async Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            CheckDisposed();

            bool transactionStartedHere = false;

            try
            {
                // Start transaction if not already in one;
                if (!_isTransactionActive)
                {
                    await BeginTransactionAsync(isolationLevel, cancellationToken);
                    transactionStartedHere = true;
                }

                // Execute the operation;
                var result = await operation();

                // Commit if we started the transaction;
                if (transactionStartedHere)
                {
                    await CommitTransactionAsync(cancellationToken);
                }

                return result;
            }
            catch (Exception)
            {
                // Rollback if we started the transaction;
                if (transactionStartedHere)
                {
                    await RollbackTransactionAsync(cancellationToken);
                }
                throw;
            }
        }

        /// <summary>
        /// Completes a transaction scope;
        /// </summary>
        public void CompleteTransactionScope()
        {
            CheckDisposed();

            if (_transactionScopes.Count == 0)
            {
                throw new InvalidOperationException("No active transaction scope");
            }

            try
            {
                var scope = _transactionScopes.Pop();
                scope.Complete();
                scope.Dispose();

                _isTransactionActive = _transactionScopes.Count > 0;
                TransactionStatus = _isTransactionActive ? TransactionStatus.Active : TransactionStatus.Completed;

                _logger.LogDebug("Transaction scope completed");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error completing transaction scope: {ex.Message}");
                throw;
            }
        }

        #endregion;

        #region Save Operations;

        /// <summary>
        /// Saves all changes made in this unit to the database;
        /// </summary>
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            try
            {
                var changes = await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var result = await _context.SaveChangesAsync(cancellationToken);

                    OperationsCount++;
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"Changes saved: {result} entities affected");

                    OperationPerformed?.Invoke(this, new OperationPerformedEventArgs;
                    {
                        OperationType = OperationType.SaveChanges,
                        EntitiesAffected = result,
                        Timestamp = DateTime.UtcNow;
                    });

                    return result;
                }, "Failed to save changes");

                return changes;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError($"Database update error: {dbEx.Message}");
                throw new UnitOfWorkException("Database update failed", dbEx);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving changes: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Saves changes with acceptance of all changes on success;
        /// </summary>
        public async Task<int> SaveChangesAndAcceptAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            try
            {
                var result = await SaveChangesAsync(cancellationToken);
                AcceptAllChanges();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in SaveChangesAndAccept: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Accepts all changes in the context;
        /// </summary>
        public void AcceptAllChanges()
        {
            CheckDisposed();

            try
            {
                _context.ChangeTracker.AcceptAllChanges();

                _logger.LogDebug("All changes accepted");

                OperationPerformed?.Invoke(this, new OperationPerformedEventArgs;
                {
                    OperationType = OperationType.AcceptChanges,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error accepting changes: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Discards all pending changes;
        /// </summary>
        public void DiscardChanges()
        {
            CheckDisposed();

            try
            {
                var entries = _context.ChangeTracker.Entries();

                foreach (var entry in entries)
                {
                    switch (entry.State)
                    {
                        case EntityState.Added:
                            entry.State = EntityState.Detached;
                            break;
                        case EntityState.Modified:
                        case EntityState.Deleted:
                            entry.Reload();
                            break;
                    }
                }

                _logger.LogDebug("All changes discarded");

                OperationPerformed?.Invoke(this, new OperationPerformedEventArgs;
                {
                    OperationType = OperationType.DiscardChanges,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error discarding changes: {ex.Message}");
                throw;
            }
        }

        #endregion;

        #region Batch Operations;

        /// <summary>
        /// Executes a batch of operations within a single transaction;
        /// </summary>
        public async Task<int> ExecuteBatchAsync(IEnumerable<Func<Task>> operations,
            CancellationToken cancellationToken = default)
        {
            if (operations == null)
            {
                throw new ArgumentNullException(nameof(operations));
            }

            CheckDisposed();

            var operationCount = 0;

            try
            {
                await BeginTransactionAsync(cancellationToken);

                foreach (var operation in operations)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await operation();
                    operationCount++;
                }

                await CommitTransactionAsync(cancellationToken);

                _logger.LogInformation($"Batch executed: {operationCount} operations");

                return operationCount;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Batch execution failed: {ex.Message}");
                await RollbackTransactionAsync(cancellationToken);
                throw;
            }
        }

        /// <summary>
        /// Executes a raw SQL command;
        /// </summary>
        public async Task<int> ExecuteSqlCommandAsync(string sql, CancellationToken cancellationToken = default)
        {
            return await ExecuteSqlCommandAsync(sql, null, cancellationToken);
        }

        /// <summary>
        /// Executes a raw SQL command with parameters;
        /// </summary>
        public async Task<int> ExecuteSqlCommandAsync(string sql, IEnumerable<object> parameters,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new ArgumentException("SQL command cannot be null or empty", nameof(sql));
            }

            CheckDisposed();

            try
            {
                var result = await _exceptionHandler.ExecuteWithHandlingAsync(async () =>
                {
                    var affectedRows = parameters == null;
                        ? await _context.Database.ExecuteSqlRawAsync(sql, cancellationToken)
                        : await _context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

                    OperationsCount++;
                    LastOperationTime = DateTime.UtcNow;

                    _logger.LogDebug($"SQL command executed: {affectedRows} rows affected");

                    OperationPerformed?.Invoke(this, new OperationPerformedEventArgs;
                    {
                        OperationType = OperationType.SqlCommand,
                        EntitiesAffected = affectedRows,
                        Timestamp = DateTime.UtcNow,
                        Details = sql;
                    });

                    return affectedRows;
                }, "Failed to execute SQL command");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error executing SQL command: {ex.Message}");
                throw;
            }
        }

        #endregion;

        #region Health and Diagnostics;

        /// <summary>
        /// Checks if the database connection is healthy;
        /// </summary>
        public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            try
            {
                var canConnect = await _context.Database.CanConnectAsync(cancellationToken);

                _logger.LogDebug($"Database health check: {(canConnect ? "Healthy" : "Unhealthy")}");

                return canConnect;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Health check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets unit of work statistics;
        /// </summary>
        public UnitOfWorkStatistics GetStatistics()
        {
            CheckDisposed();

            return new UnitOfWorkStatistics;
            {
                OperationsCount = OperationsCount,
                LastOperationTime = LastOperationTime,
                ActiveTransaction = IsInTransaction,
                TransactionId = CurrentTransactionId,
                RepositoryCount = _repositories.Count + _customRepositories.Count,
                ContextEntries = _context.ChangeTracker.Entries().Count()
            };
        }

        /// <summary>
        /// Resets the unit of work state;
        /// </summary>
        public async Task ResetAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            try
            {
                // Rollback any active transaction;
                if (_isTransactionActive && _currentTransaction != null)
                {
                    await RollbackTransactionAsync(cancellationToken);
                }

                // Clear all transaction scopes;
                while (_transactionScopes.Count > 0)
                {
                    var scope = _transactionScopes.Pop();
                    scope.Dispose();
                }

                // Clear repositories;
                lock (_syncLock)
                {
                    _repositories.Clear();
                    _customRepositories.Clear();
                }

                // Reset counters;
                OperationsCount = 0;
                LastOperationTime = DateTime.UtcNow;

                _logger.LogInformation("UnitOfWork reset");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resetting UnitOfWork: {ex.Message}");
                throw;
            }
        }

        #endregion;

        #region Private Methods;

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UnitOfWork));
            }
        }

        private void ResetTransactionState()
        {
            _currentTransaction?.Dispose();
            _currentTransaction = null;
            _isTransactionActive = false;
            CurrentTransactionId = null;
            TransactionStatus = TransactionStatus.NoTransaction;
        }

        private System.Transactions.IsolationLevel ConvertIsolationLevel(IsolationLevel efIsolationLevel)
        {
            return efIsolationLevel switch;
            {
                IsolationLevel.Chaos => System.Transactions.IsolationLevel.Chaos,
                IsolationLevel.ReadUncommitted => System.Transactions.IsolationLevel.ReadUncommitted,
                IsolationLevel.ReadCommitted => System.Transactions.IsolationLevel.ReadCommitted,
                IsolationLevel.RepeatableRead => System.Transactions.IsolationLevel.RepeatableRead,
                IsolationLevel.Serializable => System.Transactions.IsolationLevel.Serializable,
                IsolationLevel.Snapshot => System.Transactions.IsolationLevel.Snapshot,
                _ => System.Transactions.IsolationLevel.ReadCommitted;
            };
        }

        private ConnectionState GetConnectionState()
        {
            try
            {
                var connection = _context.Database.GetDbConnection();
                return connection.State;
            }
            catch
            {
                return ConnectionState.Broken;
            }
        }

        private TRepository CreateCustomRepository<TRepository>() where TRepository : class;
        {
            var type = typeof(TRepository);

            // Try to create instance with constructor that takes DataContext;
            var constructor = type.GetConstructor(new[] { typeof(DataContext) });
            if (constructor != null)
            {
                return (TRepository)constructor.Invoke(new object[] { _context });
            }

            // Try parameterless constructor;
            constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor != null)
            {
                return (TRepository)constructor.Invoke(null);
            }

            throw new InvalidOperationException($"Cannot create instance of {type.Name}. No suitable constructor found.");
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    try
                    {
                        // Rollback any active transaction;
                        if (_isTransactionActive && _currentTransaction != null)
                        {
                            _currentTransaction.Rollback();
                            _logger.LogWarning("Active transaction rolled back during disposal");
                        }

                        _currentTransaction?.Dispose();

                        // Dispose all transaction scopes;
                        while (_transactionScopes.Count > 0)
                        {
                            var scope = _transactionScopes.Pop();
                            scope.Dispose();
                        }

                        // Clear repositories;
                        lock (_syncLock)
                        {
                            _repositories.Clear();
                            _customRepositories.Clear();
                        }

                        // Context will be disposed by the calling code or DI container;

                        _logger.LogInformation("UnitOfWork disposed");

                        UnitOfWorkDisposed?.Invoke(this, new UnitOfWorkDisposedEventArgs;
                        {
                            Timestamp = DateTime.UtcNow,
                            OperationsPerformed = OperationsCount;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error during UnitOfWork disposal: {ex.Message}");
                    }
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
    /// Interface for Unit of Work pattern;
    /// </summary>
    public interface IUnitOfWork : IDisposable
    {
        /// <summary>
        /// Current transaction status;
        /// </summary>
        TransactionStatus TransactionStatus { get; }

        /// <summary>
        /// Current transaction ID;
        /// </summary>
        Guid? CurrentTransactionId { get; }

        /// <summary>
        /// Total operations performed;
        /// </summary>
        int OperationsCount { get; }

        /// <summary>
        /// Last operation timestamp;
        /// </summary>
        DateTime LastOperationTime { get; }

        /// <summary>
        /// Indicates if the unit of work is in a transaction;
        /// </summary>
        bool IsInTransaction { get; }

        /// <summary>
        /// Database connection state;
        /// </summary>
        ConnectionState ConnectionState { get; }

        /// <summary>
        /// Gets the repository for the specified entity type;
        /// </summary>
        IRepository<TEntity> GetRepository<TEntity>() where TEntity : class;

        /// <summary>
        /// Gets a custom repository by type;
        /// </summary>
        TRepository GetCustomRepository<TRepository>() where TRepository : class;

        /// <summary>
        /// Registers a custom repository instance;
        /// </summary>
        void RegisterCustomRepository<TRepository>(TRepository repository) where TRepository : class;

        /// <summary>
        /// Creates a specific repository for the entity type;
        /// </summary>
        IRepository<TEntity> CreateSpecificRepository<TEntity>() where TEntity : class;

        /// <summary>
        /// Begins a new transaction with default isolation level;
        /// </summary>
        Task BeginTransactionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Begins a new transaction with specified isolation level;
        /// </summary>
        Task BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a transaction scope for distributed transactions;
        /// </summary>
        TransactionScope CreateTransactionScope(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            TimeSpan? timeout = null);

        /// <summary>
        /// Commits the current transaction;
        /// </summary>
        Task CommitTransactionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Rolls back the current transaction;
        /// </summary>
        Task RollbackTransactionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes an operation within a transaction;
        /// </summary>
        Task ExecuteInTransactionAsync(Func<Task> operation,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes an operation within a transaction and returns result;
        /// </summary>
        Task<TResult> ExecuteInTransactionAsync<TResult>(Func<Task<TResult>> operation,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Completes a transaction scope;
        /// </summary>
        void CompleteTransactionScope();

        /// <summary>
        /// Saves all changes made in this unit to the database;
        /// </summary>
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves changes with acceptance of all changes on success;
        /// </summary>
        Task<int> SaveChangesAndAcceptAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Accepts all changes in the context;
        /// </summary>
        void AcceptAllChanges();

        /// <summary>
        /// Discards all pending changes;
        /// </summary>
        void DiscardChanges();

        /// <summary>
        /// Executes a batch of operations within a single transaction;
        /// </summary>
        Task<int> ExecuteBatchAsync(IEnumerable<Func<Task>> operations,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a raw SQL command;
        /// </summary>
        Task<int> ExecuteSqlCommandAsync(string sql, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a raw SQL command with parameters;
        /// </summary>
        Task<int> ExecuteSqlCommandAsync(string sql, IEnumerable<object> parameters,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if the database connection is healthy;
        /// </summary>
        Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets unit of work statistics;
        /// </summary>
        UnitOfWorkStatistics GetStatistics();

        /// <summary>
        /// Resets the unit of work state;
        /// </summary>
        Task ResetAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Transaction status enumeration;
    /// </summary>
    public enum TransactionStatus;
    {
        NoTransaction,
        Active,
        Committed,
        RolledBack,
        Failed;
    }

    /// <summary>
    /// Connection state enumeration;
    /// </summary>
    public enum ConnectionState;
    {
        Closed,
        Open,
        Connecting,
        Executing,
        Fetching,
        Broken;
    }

    /// <summary>
    /// Operation type enumeration;
    /// </summary>
    public enum OperationType;
    {
        SaveChanges,
        AcceptChanges,
        DiscardChanges,
        SqlCommand,
        TransactionStart,
        TransactionCommit,
        TransactionRollback;
    }

    /// <summary>
    /// Unit of work statistics;
    /// </summary>
    public class UnitOfWorkStatistics;
    {
        public int OperationsCount { get; set; }
        public DateTime LastOperationTime { get; set; }
        public bool ActiveTransaction { get; set; }
        public Guid? TransactionId { get; set; }
        public int RepositoryCount { get; set; }
        public int ContextEntries { get; set; }
    }

    #endregion;

    #region Event Arguments;

    /// <summary>
    /// Event arguments for transaction started;
    /// </summary>
    public class TransactionStartedEventArgs : EventArgs;
    {
        public Guid TransactionId { get; set; }
        public IsolationLevel IsolationLevel { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for transaction committed;
    /// </summary>
    public class TransactionCommittedEventArgs : EventArgs;
    {
        public Guid TransactionId { get; set; }
        public int OperationsCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for transaction rolled back;
    /// </summary>
    public class TransactionRolledBackEventArgs : EventArgs;
    {
        public Guid TransactionId { get; set; }
        public int OperationsCount { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Event arguments for operation performed;
    /// </summary>
    public class OperationPerformedEventArgs : EventArgs;
    {
        public OperationType OperationType { get; set; }
        public int EntitiesAffected { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Event arguments for unit of work disposed;
    /// </summary>
    public class UnitOfWorkDisposedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public int OperationsPerformed { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Unit of Work exception;
    /// </summary>
    public class UnitOfWorkException : Exception
    {
        public UnitOfWorkException() { }
        public UnitOfWorkException(string message) : base(message) { }
        public UnitOfWorkException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
