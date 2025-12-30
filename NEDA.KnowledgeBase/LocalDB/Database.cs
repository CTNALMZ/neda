// NEDA.KnowledgeBase/LocalDB/Database.cs;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.KnowledgeBase.DataManagement.Migrations;
using NEDA.KnowledgeBase.DataManagement.DataAccess;

namespace NEDA.KnowledgeBase.LocalDB;
{
    /// <summary>
    /// Endüstriyel seviyede yerel veritabanı yönetim ve işlem motoru;
    /// Multi-database support, transaction management, ve advanced query processing sağlar;
    /// </summary>
    public class Database : IDisposable
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly DatabaseConfig _config;
        private readonly ConnectionManager _connectionManager;
        private readonly DatabaseValidator _validator;
        private readonly DatabaseOptimizer _optimizer;
        private readonly DatabaseBackupService _backupService;
        private readonly Dictionary<string, object> _cachedResults;
        private readonly object _dbLock = new object();
        private readonly Timer _maintenanceTimer;
        private readonly Timer _statisticsTimer;
        private readonly CancellationTokenSource _cts;
        private DbTransaction _currentTransaction;
        private DatabaseState _state;
        private DateTime _lastConnected;
        private DateTime _lastMaintenance;
        private int _totalQueries;
        private int _failedQueries;
        private readonly PerformanceMetrics _performanceMetrics;
        private bool _isInitialized;
        #endregion;

        #region Properties;
        /// <summary>
        /// Veritabanı durumu;
        /// </summary>
        public DatabaseState State;
        {
            get => _state;
            private set;
            {
                if (_state != value)
                {
                    var previousState = _state;
                    _state = value;
                    OnStateChanged(new DatabaseStateChangedEventArgs;
                    {
                        PreviousState = previousState,
                        NewState = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Veritabanı adı;
        /// </summary>
        public string DatabaseName => _config.DatabaseName;

        /// <summary>
        /// Veritabanı sağlayıcısı;
        /// </summary>
        public DatabaseProvider Provider => _config.Provider;

        /// <summary>
        /// Bağlantı durumu;
        /// </summary>
        public bool IsConnected => State == DatabaseState.Connected;

        /// <summary>
        /// Aktif transaction var mı?
        /// </summary>
        public bool HasActiveTransaction => _currentTransaction != null;

        /// <summary>
        /// Veritabanı boyutu (bytes)
        /// </summary>
        public long DatabaseSize { get; private set; }

        /// <summary>
        /// Tablo sayısı;
        /// </summary>
        public int TableCount { get; private set; }

        /// <summary>
        /// Toplam sorgu sayısı;
        /// </summary>
        public int TotalQueries => _totalQueries;

        /// <summary>
        /// Başarısız sorgu sayısı;
        /// </summary>
        public int FailedQueries => _failedQueries;

        /// <summary>
        /// Başarı oranı (%)
        /// </summary>
        public double SuccessRate => _totalQueries > 0;
            ? 100.0 - ((double)_failedQueries / _totalQueries * 100.0)
            : 100.0;

        /// <summary>
        /// Performans metrikleri;
        /// </summary>
        public PerformanceMetrics PerformanceMetrics => _performanceMetrics;

        /// <summary>
        /// Konfigürasyon ayarları;
        /// </summary>
        public DatabaseConfig Config => _config;

        /// <summary>
        /// Son bağlantı zamanı;
        /// </summary>
        public DateTime LastConnected => _lastConnected;

        /// <summary>
        /// Son bakım zamanı;
        /// </summary>
        public DateTime LastMaintenance => _lastMaintenance;
        #endregion;

        #region Events;
        /// <summary>
        /// Veritabanı durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<DatabaseStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Bağlantı açıldığında tetiklenir;
        /// </summary>
        public event EventHandler<ConnectionOpenedEventArgs> ConnectionOpened;

        /// <summary>
        /// Bağlantı kapandığında tetiklenir;
        /// </summary>
        public event EventHandler<ConnectionClosedEventArgs> ConnectionClosed;

        /// <summary>
        /// Sorgu çalıştırıldığında tetiklenir;
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

        /// <summary>
        /// Veritabanı bakımı yapıldığında tetiklenir;
        /// </summary>
        public event EventHandler<MaintenancePerformedEventArgs> MaintenancePerformed;

        /// <summary>
        /// Veritabanı optimize edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<DatabaseOptimizedEventArgs> DatabaseOptimized;

        /// <summary>
        /// Yedekleme yapıldığında tetiklenir;
        /// </summary>
        public event EventHandler<BackupCreatedEventArgs> BackupCreated;
        #endregion;

        #region Constructor;
        /// <summary>
        /// Database örneği oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="config">Veritabanı konfigürasyonu</param>
        public Database(ILogger logger, DatabaseConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? DatabaseConfig.Default;

            _validator = new DatabaseValidator(_logger);
            _optimizer = new DatabaseOptimizer(_logger);
            _backupService = new DatabaseBackupService(_logger, _config);
            _cachedResults = new Dictionary<string, object>();
            _cts = new CancellationTokenSource();
            _performanceMetrics = new PerformanceMetrics();

            InitializeConnectionManager();
            InitializeTimers();

            State = DatabaseState.Disconnected;

            _logger.Info($"Database instance created: {_config.DatabaseName}, " +
                        $"Provider: {_config.Provider}, " +
                        $"ConnectionString: {_config.GetMaskedConnectionString()}");
        }
        #endregion;

        #region Public Methods - Connection Management;
        /// <summary>
        /// Veritabanını başlatır ve bağlantı açar;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.Warning("Database already initialized");
                    return;
                }

                State = DatabaseState.Connecting;

                // Veritabanı dosyasını kontrol et;
                await EnsureDatabaseExistsAsync(cancellationToken);

                // Bağlantı yöneticisini başlat;
                _connectionManager.Initialize();

                // Bağlantıyı test et;
                await TestConnectionAsync(cancellationToken);

                // Veritabanı istatistiklerini yükle;
                await LoadDatabaseStatisticsAsync(cancellationToken);

                _isInitialized = true;
                State = DatabaseState.Connected;
                _lastConnected = DateTime.UtcNow;

                OnConnectionOpened(new ConnectionOpenedEventArgs;
                {
                    DatabaseName = _config.DatabaseName,
                    ConnectionTime = _lastConnected,
                    Provider = _config.Provider;
                });

                _logger.Info($"Database initialized successfully: {_config.DatabaseName}");
            }
            catch (Exception ex)
            {
                State = DatabaseState.Error;
                _logger.Error($"Failed to initialize database: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.DatabaseInitializationFailed,
                    $"Failed to initialize database {_config.DatabaseName}",
                    ex);
            }
        }

        /// <summary>
        /// Veritabanı bağlantısını test eder;
        /// </summary>
        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var command = connection.CreateCommand();
                    command.CommandText = GetTestQuery();

                    var result = await command.ExecuteScalarAsync(cancellationToken);

                    _logger.Debug($"Connection test successful: {_config.DatabaseName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Connection test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Veritabanını kapatır ve kaynakları serbest bırakır;
        /// </summary>
        public async Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                State = DatabaseState.Disconnecting;

                // Aktif transaction'ları rollback et;
                if (HasActiveTransaction)
                {
                    await RollbackTransactionAsync(cancellationToken);
                }

                // Bağlantı yöneticisini kapat;
                _connectionManager.Shutdown();

                // Timer'ları durdur;
                _maintenanceTimer?.Dispose();
                _statisticsTimer?.Dispose();

                // Cancellation token'ı iptal et;
                _cts.Cancel();

                State = DatabaseState.Disconnected;
                _isInitialized = false;

                OnConnectionClosed(new ConnectionClosedEventArgs;
                {
                    DatabaseName = _config.DatabaseName,
                    DisconnectTime = DateTime.UtcNow,
                    Reason = "Shutdown"
                });

                _logger.Info($"Database shutdown completed: {_config.DatabaseName}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during database shutdown: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Veritabanını yeniden başlatır;
        /// </summary>
        public async Task RestartAsync(CancellationToken cancellationToken = default)
        {
            await ShutdownAsync(cancellationToken);
            await InitializeAsync(cancellationToken);

            _logger.Info($"Database restarted: {_config.DatabaseName}");
        }
        #endregion;

        #region Public Methods - Query Execution;
        /// <summary>
        /// SQL sorgusu çalıştırır ve sonuç döndürmez;
        /// </summary>
        public async Task<int> ExecuteNonQueryAsync(
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var command = CreateCommand(connection, sql, parameters);
                    var startTime = DateTime.UtcNow;

                    var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);

                    RecordQueryExecution(sql, startTime, true);

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.NonQuery,
                        Sql = GetMaskedSql(sql),
                        ExecutionTime = DateTime.UtcNow,
                        Duration = DateTime.UtcNow - startTime,
                        AffectedRows = affectedRows;
                    });

                    return affectedRows;
                }
            }
            catch (Exception ex)
            {
                RecordQueryExecution(sql, DateTime.UtcNow, false);
                _logger.Error($"Failed to execute non-query: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.QueryExecutionFailed,
                    $"Failed to execute non-query: {GetMaskedSql(sql)}",
                    ex);
            }
        }

        /// <summary>
        /// SQL sorgusu çalıştırır ve tek değer döndürür;
        /// </summary>
        public async Task<T> ExecuteScalarAsync<T>(
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                // Cache kontrolü;
                var cacheKey = GetCacheKey(sql, parameters);
                if (_config.EnableQueryCaching && _cachedResults.TryGetValue(cacheKey, out var cached))
                {
                    _logger.Debug($"Cache hit for query: {GetMaskedSql(sql)}");
                    return (T)cached;
                }

                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var command = CreateCommand(connection, sql, parameters);
                    var startTime = DateTime.UtcNow;

                    var result = await command.ExecuteScalarAsync(cancellationToken);
                    var typedResult = result == DBNull.Value ? default(T) : (T)Convert.ChangeType(result, typeof(T));

                    RecordQueryExecution(sql, startTime, true);

                    // Cache'e kaydet;
                    if (_config.EnableQueryCaching && typedResult != null)
                    {
                        _cachedResults[cacheKey] = typedResult;
                    }

                    OnQueryExecuted(new QueryExecutedEventArgs;
                    {
                        QueryType = QueryType.Scalar,
                        Sql = GetMaskedSql(sql),
                        ExecutionTime = DateTime.UtcNow,
                        Duration = DateTime.UtcNow - startTime,
                        ResultType = typeof(T).Name;
                    });

                    return typedResult;
                }
            }
            catch (Exception ex)
            {
                RecordQueryExecution(sql, DateTime.UtcNow, false);
                _logger.Error($"Failed to execute scalar query: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.QueryExecutionFailed,
                    $"Failed to execute scalar query: {GetMaskedSql(sql)}",
                    ex);
            }
        }

        /// <summary>
        /// SQL sorgusu çalıştırır ve DataReader döndürür;
        /// </summary>
        public async Task<DbDataReader> ExecuteReaderAsync(
            string sql,
            object parameters = null,
            CommandBehavior behavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var connection = await _connectionManager.GetConnectionAsync(cancellationToken);
                var command = CreateCommand(connection, sql, parameters);
                var startTime = DateTime.UtcNow;

                var reader = await command.ExecuteReaderAsync(behavior, cancellationToken);

                RecordQueryExecution(sql, startTime, true);

                OnQueryExecuted(new QueryExecutedEventArgs;
                {
                    QueryType = QueryType.Reader,
                    Sql = GetMaskedSql(sql),
                    ExecutionTime = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - startTime;
                });

                return reader;
            }
            catch (Exception ex)
            {
                RecordQueryExecution(sql, DateTime.UtcNow, false);
                _logger.Error($"Failed to execute reader query: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.QueryExecutionFailed,
                    $"Failed to execute reader query: {GetMaskedSql(sql)}",
                    ex);
            }
        }

        /// <summary>
        /// SQL sorgusu çalıştırır ve liste döndürür;
        /// </summary>
        public async Task<List<T>> ExecuteListAsync<T>(
            string sql,
            object parameters = null,
            CancellationToken cancellationToken = default) where T : new()
        {
            ValidateDatabaseState();

            try
            {
                // Cache kontrolü;
                var cacheKey = GetCacheKey(sql, parameters);
                if (_config.EnableQueryCaching && _cachedResults.TryGetValue(cacheKey, out var cached))
                {
                    _logger.Debug($"Cache hit for list query: {GetMaskedSql(sql)}");
                    return (List<T>)cached;
                }

                var results = new List<T>();
                using (var reader = await ExecuteReaderAsync(sql, parameters, CommandBehavior.Default, cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var item = MapToEntity<T>(reader);
                        results.Add(item);
                    }
                }

                // Cache'e kaydet;
                if (_config.EnableQueryCaching && results.Count > 0)
                {
                    _cachedResults[cacheKey] = results;
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to execute list query: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.QueryExecutionFailed,
                    $"Failed to execute list query: {GetMaskedSql(sql)}",
                    ex);
            }
        }

        /// <summary>
        /// Stored procedure çalıştırır;
        /// </summary>
        public async Task<StoredProcedureResult> ExecuteStoredProcedureAsync(
            string procedureName,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var command = connection.CreateCommand();
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = procedureName;
                    command.CommandTimeout = _config.CommandTimeout;

                    if (parameters != null)
                    {
                        foreach (var param in parameters)
                        {
                            var dbParam = command.CreateParameter();
                            dbParam.ParameterName = param.Key;
                            dbParam.Value = param.Value ?? DBNull.Value;
                            command.Parameters.Add(dbParam);
                        }
                    }

                    var startTime = DateTime.UtcNow;

                    // Output parametreleri için;
                    var outputParams = new Dictionary<string, object>();

                    using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    {
                        var results = new List<Dictionary<string, object>>();

                        do;
                        {
                            var resultSet = new List<Dictionary<string, object>>();

                            while (await reader.ReadAsync(cancellationToken))
                            {
                                var row = new Dictionary<string, object>();
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    row[reader.GetName(i)] = reader.GetValue(i);
                                }
                                resultSet.Add(row);
                            }

                            results.AddRange(resultSet);
                        } while (await reader.NextResultAsync(cancellationToken));

                        // Output parametrelerini topla;
                        foreach (DbParameter param in command.Parameters)
                        {
                            if (param.Direction == ParameterDirection.Output ||
                                param.Direction == ParameterDirection.InputOutput ||
                                param.Direction == ParameterDirection.ReturnValue)
                            {
                                outputParams[param.ParameterName] = param.Value;
                            }
                        }

                        RecordQueryExecution($"EXEC {procedureName}", startTime, true);

                        return new StoredProcedureResult;
                        {
                            Results = results,
                            OutputParameters = outputParams,
                            ExecutionTime = DateTime.UtcNow - startTime;
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                RecordQueryExecution($"EXEC {procedureName}", DateTime.UtcNow, false);
                _logger.Error($"Failed to execute stored procedure {procedureName}: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.StoredProcedureFailed,
                    $"Failed to execute stored procedure {procedureName}",
                    ex);
            }
        }

        /// <summary>
        /// Batch SQL sorgusu çalıştırır;
        /// </summary>
        public async Task<BatchResult> ExecuteBatchAsync(
            List<string> sqlStatements,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync(cancellationToken))
                {
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);
                    var startTime = DateTime.UtcNow;
                    var results = new List<BatchStatementResult>();

                    try
                    {
                        for (int i = 0; i < sqlStatements.Count; i++)
                        {
                            var sql = sqlStatements[i];
                            var command = CreateCommand(connection, sql, null);
                            command.Transaction = transaction;

                            var statementStartTime = DateTime.UtcNow;
                            var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
                            var statementDuration = DateTime.UtcNow - statementStartTime;

                            results.Add(new BatchStatementResult;
                            {
                                StatementIndex = i,
                                Sql = GetMaskedSql(sql),
                                AffectedRows = affectedRows,
                                Duration = statementDuration,
                                Success = true;
                            });

                            RecordQueryExecution(sql, statementStartTime, true);
                        }

                        await transaction.CommitAsync(cancellationToken);

                        return new BatchResult;
                        {
                            StatementResults = results,
                            TotalDuration = DateTime.UtcNow - startTime,
                            Success = true;
                        };
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
                _logger.Error($"Failed to execute batch: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.BatchExecutionFailed,
                    "Failed to execute batch SQL statements",
                    ex);
            }
        }
        #endregion;

        #region Public Methods - Transaction Management;
        /// <summary>
        /// Transaction başlatır;
        /// </summary>
        public async Task BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                if (HasActiveTransaction)
                {
                    throw new DatabaseException(
                        ErrorCode.TransactionAlreadyActive,
                        "A transaction is already active");
                }

                var connection = await _connectionManager.GetConnectionAsync(cancellationToken);
                _currentTransaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);

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
                throw new DatabaseException(
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
                if (!HasActiveTransaction)
                {
                    throw new DatabaseException(
                        ErrorCode.NoActiveTransaction,
                        "No active transaction to commit");
                }

                await _currentTransaction.CommitAsync(cancellationToken);

                OnTransactionCommitted(new TransactionCommittedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    TransactionId = Guid.NewGuid().ToString()
                });

                _currentTransaction.Dispose();
                _currentTransaction = null;

                _logger.Debug("Transaction committed");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to commit transaction: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.TransactionCommitFailed,
                    "Failed to commit transaction",
                    ex);
            }
        }

        /// <summary>
        /// Transaction'ı rollback eder;
        /// </summary>
        public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (!HasActiveTransaction)
                {
                    throw new DatabaseException(
                        ErrorCode.NoActiveTransaction,
                        "No active transaction to rollback");
                }

                await _currentTransaction.RollbackAsync(cancellationToken);

                OnTransactionRolledBack(new TransactionRolledBackEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    TransactionId = Guid.NewGuid().ToString(),
                    Reason = "Rollback requested"
                });

                _currentTransaction.Dispose();
                _currentTransaction = null;

                _logger.Debug("Transaction rolled back");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to rollback transaction: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.TransactionRollbackFailed,
                    "Failed to rollback transaction",
                    ex);
            }
        }

        /// <summary>
        /// Transaction scope içinde işlem çalıştırır;
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

        #region Public Methods - Schema Management;
        /// <summary>
        /// Tablo oluşturur;
        /// </summary>
        public async Task<bool> CreateTableAsync(
            string tableName,
            List<TableColumn> columns,
            List<TableConstraint> constraints = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var sql = GenerateCreateTableSql(tableName, columns, constraints);
                await ExecuteNonQueryAsync(sql, null, cancellationToken);

                _logger.Info($"Table created: {tableName}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create table {tableName}: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.TableCreationFailed,
                    $"Failed to create table {tableName}",
                    ex);
            }
        }

        /// <summary>
        /// Tablo siler;
        /// </summary>
        public async Task<bool> DropTableAsync(
            string tableName,
            bool ifExists = true,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var sql = ifExists;
                    ? $"DROP TABLE IF EXISTS {tableName}"
                    : $"DROP TABLE {tableName}";

                await ExecuteNonQueryAsync(sql, null, cancellationToken);

                _logger.Info($"Table dropped: {tableName}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to drop table {tableName}: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.TableDeletionFailed,
                    $"Failed to drop table {tableName}",
                    ex);
            }
        }

        /// <summary>
        /// Tablo var mı kontrol eder;
        /// </summary>
        public async Task<bool> TableExistsAsync(
            string tableName,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var sql = GetTableExistsQuery(tableName);
                var result = await ExecuteScalarAsync<int>(sql, null, cancellationToken);
                return result > 0;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to check table existence {tableName}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Tüm tabloları listeler;
        /// </summary>
        public async Task<List<string>> GetTablesAsync(CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var sql = GetTablesQuery();
                var tables = await ExecuteListAsync<TableInfo>(sql, null, cancellationToken);
                return tables.Select(t => t.TableName).ToList();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get tables: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.SchemaQueryFailed,
                    "Failed to get tables",
                    ex);
            }
        }

        /// <summary>
        /// Tablo şemasını getirir;
        /// </summary>
        public async Task<TableSchema> GetTableSchemaAsync(
            string tableName,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var columns = await GetTableColumnsAsync(tableName, cancellationToken);
                var indexes = await GetTableIndexesAsync(tableName, cancellationToken);
                var foreignKeys = await GetTableForeignKeysAsync(tableName, cancellationToken);

                return new TableSchema;
                {
                    TableName = tableName,
                    Columns = columns,
                    Indexes = indexes,
                    ForeignKeys = foreignKeys,
                    RowCount = await GetTableRowCountAsync(tableName, cancellationToken)
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get table schema {tableName}: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.SchemaQueryFailed,
                    $"Failed to get table schema {tableName}",
                    ex);
            }
        }
        #endregion;

        #region Public Methods - Database Operations;
        /// <summary>
        /// Veritabanını yedekler;
        /// </summary>
        public async Task<BackupResult> BackupAsync(
            string backupPath = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var result = await _backupService.CreateBackupAsync(_connectionManager, backupPath, cancellationToken);

                OnBackupCreated(new BackupCreatedEventArgs;
                {
                    BackupFileName = result.BackupFileName,
                    BackupSize = result.BackupSize,
                    Timestamp = DateTime.UtcNow,
                    Success = true;
                });

                _logger.Info($"Database backup created: {result.BackupFileName}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to backup database: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.BackupFailed,
                    "Failed to backup database",
                    ex);
            }
        }

        /// <summary>
        /// Veritabanını optimize eder;
        /// </summary>
        public async Task OptimizeAsync(CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var startTime = DateTime.UtcNow;
                var result = await _optimizer.OptimizeDatabaseAsync(_connectionManager, cancellationToken);

                OnDatabaseOptimized(new DatabaseOptimizedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - startTime,
                    TablesOptimized = result.TablesOptimized,
                    IndexesRebuilt = result.IndexesRebuilt,
                    SpaceFreed = result.SpaceFreed;
                });

                _logger.Info($"Database optimized: {result.TablesOptimized} tables, " +
                           $"{result.IndexesRebuilt} indexes, " +
                           $"{FormatFileSize(result.SpaceFreed)} freed");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to optimize database: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.OptimizationFailed,
                    "Failed to optimize database",
                    ex);
            }
        }

        /// <summary>
        /// Veritabanını doğrular;
        /// </summary>
        public async Task<ValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                return await _validator.ValidateDatabaseAsync(_connectionManager, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to validate database: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.ValidationFailed,
                    "Failed to validate database",
                    ex);
            }
        }

        /// <summary>
        /// Veritabanı istatistiklerini günceller;
        /// </summary>
        public async Task UpdateStatisticsAsync(CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                DatabaseSize = await GetDatabaseSizeAsync(cancellationToken);
                TableCount = await GetTableCountAsync(cancellationToken);

                _logger.Debug($"Database statistics updated: Size={FormatFileSize(DatabaseSize)}, Tables={TableCount}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to update database statistics: {ex.Message}");
            }
        }

        /// <summary>
        /// Query cache'ini temizler;
        /// </summary>
        public void ClearQueryCache()
        {
            lock (_cachedResults)
            {
                _cachedResults.Clear();
                _logger.Debug("Query cache cleared");
            }
        }

        /// <summary>
        /// Veritabanı bakımını çalıştırır;
        /// </summary>
        public async Task PerformMaintenanceAsync(CancellationToken cancellationToken = default)
        {
            ValidateDatabaseState();

            try
            {
                var startTime = DateTime.UtcNow;

                // Cache temizle;
                ClearQueryCache();

                // Tablaları optimize et;
                await OptimizeAsync(cancellationToken);

                // İstatistikleri güncelle;
                await UpdateStatisticsAsync(cancellationToken);

                _lastMaintenance = DateTime.UtcNow;

                OnMaintenancePerformed(new MaintenancePerformedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    Duration = DateTime.UtcNow - startTime,
                    MaintenanceType = "Full"
                });

                _logger.Info($"Database maintenance performed in {(DateTime.UtcNow - startTime).TotalSeconds:F2}s");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to perform maintenance: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.MaintenanceFailed,
                    "Failed to perform database maintenance",
                    ex);
            }
        }

        /// <summary>
        /// Veritabanı durumunu getirir;
        /// </summary>
        public DatabaseStatus GetStatus()
        {
            return new DatabaseStatus;
            {
                Timestamp = DateTime.UtcNow,
                DatabaseName = _config.DatabaseName,
                State = State,
                IsConnected = IsConnected,
                HasActiveTransaction = HasActiveTransaction,
                DatabaseSize = DatabaseSize,
                TableCount = TableCount,
                TotalQueries = _totalQueries,
                FailedQueries = _failedQueries,
                SuccessRate = SuccessRate,
                LastConnected = _lastConnected,
                LastMaintenance = _lastMaintenance,
                PerformanceMetrics = _performanceMetrics.GetSnapshot()
            };
        }
        #endregion;

        #region Private Methods;
        private void InitializeConnectionManager()
        {
            var poolConfig = new ConnectionPoolConfig;
            {
                MaxPoolSize = _config.MaxConnectionPoolSize,
                MinPoolSize = _config.MinConnectionPoolSize,
                ConnectionTimeout = _config.ConnectionTimeout;
            };

            var failoverConfigs = new List<DatabaseFailoverConfig>
            {
                new DatabaseFailoverConfig;
                {
                    Name = _config.DatabaseName,
                    ConnectionString = _config.ConnectionString,
                    ProviderName = GetProviderName(_config.Provider),
                    IsPrimary = true;
                }
            };

            _connectionManager = new ConnectionManager(_logger, poolConfig, failoverConfigs);
        }

        private void InitializeTimers()
        {
            // Bakım timer'ı;
            _maintenanceTimer = new Timer(
                async _ => await PerformScheduledMaintenanceAsync(),
                null,
                TimeSpan.FromHours(_config.MaintenanceIntervalHours),
                TimeSpan.FromHours(_config.MaintenanceIntervalHours));

            // İstatistik timer'ı;
            _statisticsTimer = new Timer(
                async _ => await UpdateStatisticsAsync(),
                null,
                TimeSpan.FromMinutes(_config.StatisticsUpdateIntervalMinutes),
                TimeSpan.FromMinutes(_config.StatisticsUpdateIntervalMinutes));
        }

        private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
        {
            var databaseFile = GetDatabaseFilePath();

            if (!File.Exists(databaseFile) && _config.CreateIfNotExists)
            {
                _logger.Info($"Database file not found, creating: {databaseFile}");
                await CreateDatabaseFileAsync(databaseFile, cancellationToken);
            }
            else if (!File.Exists(databaseFile))
            {
                throw new DatabaseException(
                    ErrorCode.DatabaseNotFound,
                    $"Database file not found: {databaseFile}");
            }
        }

        private async Task CreateDatabaseFileAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));

                switch (_config.Provider)
                {
                    case DatabaseProvider.SQLite:
                        // SQLite için boş bir dosya oluştur;
                        File.WriteAllBytes(filePath, Array.Empty<byte>());

                        // Temel tabloları oluştur;
                        await InitializeSQLiteDatabaseAsync(cancellationToken);
                        break;

                    case DatabaseProvider.SqlServer:
                        // SQL Server için farklı bir yaklaşım gerekebilir;
                        await InitializeSqlServerDatabaseAsync(cancellationToken);
                        break;

                    default:
                        throw new NotSupportedException($"Provider not supported: {_config.Provider}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create database file: {ex.Message}", ex);
                throw;
            }
        }

        private async Task InitializeSQLiteDatabaseAsync(CancellationToken cancellationToken)
        {
            var initSql = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA temp_store = MEMORY;
                PRAGMA mmap_size = 268435456;
            ";

            await ExecuteNonQueryAsync(initSql, null, cancellationToken);
        }

        private async Task InitializeSqlServerDatabaseAsync(CancellationToken cancellationToken)
        {
            // SQL Server için başlangıç script'leri;
            // Burada gerekli başlangıç işlemleri yapılır;
        }

        private async Task LoadDatabaseStatisticsAsync(CancellationToken cancellationToken)
        {
            await UpdateStatisticsAsync(cancellationToken);
        }

        private string GetDatabaseFilePath()
        {
            if (_config.Provider == DatabaseProvider.SQLite)
            {
                var connectionString = _config.ConnectionString;
                var parts = connectionString.Split(';');

                foreach (var part in parts)
                {
                    if (part.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) ||
                        part.Trim().StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase))
                    {
                        var filePath = part.Split('=')[1].Trim();
                        return Path.IsPathRooted(filePath) ? filePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filePath);
                    }
                }
            }

            return string.Empty;
        }

        private string GetProviderName(DatabaseProvider provider)
        {
            return provider switch;
            {
                DatabaseProvider.SqlServer => "System.Data.SqlClient",
                DatabaseProvider.SQLite => "System.Data.SQLite",
                DatabaseProvider.PostgreSQL => "Npgsql",
                DatabaseProvider.MySQL => "MySql.Data.MySqlClient",
                _ => throw new NotSupportedException($"Provider not supported: {provider}")
            };
        }

        private string GetTestQuery()
        {
            return _config.Provider switch;
            {
                DatabaseProvider.SQLite => "SELECT 1",
                DatabaseProvider.SqlServer => "SELECT 1",
                DatabaseProvider.PostgreSQL => "SELECT 1",
                DatabaseProvider.MySQL => "SELECT 1",
                _ => "SELECT 1"
            };
        }

        private string GetTableExistsQuery(string tableName)
        {
            return _config.Provider switch;
            {
                DatabaseProvider.SQLite => $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'",
                DatabaseProvider.SqlServer => $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{tableName}'",
                DatabaseProvider.PostgreSQL => $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{tableName}'",
                DatabaseProvider.MySQL => $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{tableName}'",
                _ => throw new NotSupportedException($"Provider not supported: {_config.Provider}")
            };
        }

        private string GetTablesQuery()
        {
            return _config.Provider switch;
            {
                DatabaseProvider.SQLite => "SELECT name as TableName FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'",
                DatabaseProvider.SqlServer => "SELECT TABLE_NAME as TableName FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'",
                DatabaseProvider.PostgreSQL => "SELECT table_name as TableName FROM information_schema.tables WHERE table_schema = 'public'",
                DatabaseProvider.MySQL => "SELECT TABLE_NAME as TableName FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE()",
                _ => throw new NotSupportedException($"Provider not supported: {_config.Provider}")
            };
        }

        private async Task<int> GetDatabaseSizeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var filePath = GetDatabaseFilePath();
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    return (int)fileInfo.Length;
                }

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<int> GetTableCountAsync(CancellationToken cancellationToken)
        {
            try
            {
                var tables = await GetTablesAsync(cancellationToken);
                return tables.Count;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<long> GetTableRowCountAsync(string tableName, CancellationToken cancellationToken)
        {
            try
            {
                var sql = $"SELECT COUNT(*) FROM {tableName}";
                return await ExecuteScalarAsync<long>(sql, null, cancellationToken);
            }
            catch
            {
                return 0;
            }
        }

        private async Task<List<TableColumn>> GetTableColumnsAsync(string tableName, CancellationToken cancellationToken)
        {
            var sql = _config.Provider switch;
            {
                DatabaseProvider.SQLite => $"PRAGMA table_info({tableName})",
                DatabaseProvider.SqlServer =>
                    $"SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT " +
                    $"FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{tableName}'",
                _ => throw new NotSupportedException($"Provider not supported: {_config.Provider}")
            };

            return await ExecuteListAsync<TableColumn>(sql, null, cancellationToken);
        }

        private async Task<List<TableIndex>> GetTableIndexesAsync(string tableName, CancellationToken cancellationToken)
        {
            var sql = _config.Provider switch;
            {
                DatabaseProvider.SQLite => $"PRAGMA index_list({tableName})",
                DatabaseProvider.SqlServer =>
                    $"SELECT i.name, i.is_unique, c.name as column_name " +
                    $"FROM sys.indexes i " +
                    $"JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
                    $"JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id " +
                    $"WHERE OBJECT_NAME(i.object_id) = '{tableName}'",
                _ => throw new NotSupportedException($"Provider not supported: {_config.Provider}")
            };

            return await ExecuteListAsync<TableIndex>(sql, null, cancellationToken);
        }

        private async Task<List<ForeignKey>> GetTableForeignKeysAsync(string tableName, CancellationToken cancellationToken)
        {
            var sql = _config.Provider switch;
            {
                DatabaseProvider.SQLite => $"PRAGMA foreign_key_list({tableName})",
                DatabaseProvider.SqlServer =>
                    $"SELECT fk.name, OBJECT_NAME(fk.parent_object_id) as from_table, " +
                    $"COL_NAME(fkc.parent_object_id, fkc.parent_column_id) as from_column, " +
                    $"OBJECT_NAME(fk.referenced_object_id) as to_table, " +
                    $"COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) as to_column " +
                    $"FROM sys.foreign_keys fk " +
                    $"JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id " +
                    $"WHERE OBJECT_NAME(fk.parent_object_id) = '{tableName}'",
                _ => throw new NotSupportedException($"Provider not supported: {_config.Provider}")
            };

            return await ExecuteListAsync<ForeignKey>(sql, null, cancellationToken);
        }

        private string GenerateCreateTableSql(
            string tableName,
            List<TableColumn> columns,
            List<TableConstraint> constraints)
        {
            var columnDefs = columns.Select(c => $"{c.Name} {c.DataType} {c.Constraints}").ToList();
            var constraintDefs = constraints?.Select(c => c.Definition).ToList() ?? new List<string>();

            var allDefs = columnDefs.Concat(constraintDefs);
            return $"CREATE TABLE {tableName} ({string.Join(", ", allDefs)})";
        }

        private DbCommand CreateCommand(DatabaseConnection connection, string sql, object parameters)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _config.CommandTimeout;

            if (HasActiveTransaction)
            {
                command.Transaction = _currentTransaction;
            }

            if (parameters != null)
            {
                AddParameters(command, parameters);
            }

            return command;
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

        private T MapToEntity<T>(DbDataReader reader) where T : new()
        {
            var entity = new T();
            var properties = typeof(T).GetProperties();

            for (int i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                var property = properties.FirstOrDefault(p =>
                    p.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

                if (property != null && property.CanWrite && !reader.IsDBNull(i))
                {
                    var value = reader.GetValue(i);
                    property.SetValue(entity, Convert.ChangeType(value, property.PropertyType));
                }
            }

            return entity;
        }

        private string GetCacheKey(string sql, object parameters)
        {
            var paramString = parameters?.GetHashCode().ToString() ?? "null";
            return $"{sql}_{paramString}";
        }

        private string GetMaskedSql(string sql)
        {
            if (string.IsNullOrEmpty(sql) || sql.Length <= 100)
                return sql;

            return sql.Substring(0, 100) + "...";
        }

        private void RecordQueryExecution(string sql, DateTime startTime, bool success)
        {
            _totalQueries++;

            if (!success)
                _failedQueries++;

            var duration = DateTime.UtcNow - startTime;
            _performanceMetrics.RecordQuery(duration, success);
        }

        private async Task PerformScheduledMaintenanceAsync()
        {
            if (!_config.EnableScheduledMaintenance || !IsConnected)
                return;

            try
            {
                await PerformMaintenanceAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                _logger.Error($"Scheduled maintenance failed: {ex.Message}");
            }
        }

        private void ValidateDatabaseState()
        {
            if (!_isInitialized)
                throw new DatabaseException(
                    ErrorCode.DatabaseNotInitialized,
                    "Database must be initialized before use");

            if (State != DatabaseState.Connected)
                throw new DatabaseException(
                    ErrorCode.DatabaseNotConnected,
                    $"Database is not connected. Current state: {State}");
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void OnStateChanged(DatabaseStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        private void OnConnectionOpened(ConnectionOpenedEventArgs e)
        {
            ConnectionOpened?.Invoke(this, e);
        }

        private void OnConnectionClosed(ConnectionClosedEventArgs e)
        {
            ConnectionClosed?.Invoke(this, e);
        }

        private void OnQueryExecuted(QueryExecutedEventArgs e)
        {
            QueryExecuted?.Invoke(this, e);
        }

        private void OnTransactionStarted(TransactionStartedEventArgs e)
        {
            TransactionStarted?.Invoke(this, e);
        }

        private void OnTransactionCommitted(TransactionCommittedEventArgs e)
        {
            TransactionCommitted?.Invoke(this, e);
        }

        private void OnTransactionRolledBack(TransactionRolledBackEventArgs e)
        {
            TransactionRolledBack?.Invoke(this, e);
        }

        private void OnMaintenancePerformed(MaintenancePerformedEventArgs e)
        {
            MaintenancePerformed?.Invoke(this, e);
        }

        private void OnDatabaseOptimized(DatabaseOptimizedEventArgs e)
        {
            DatabaseOptimized?.Invoke(this, e);
        }

        private void OnBackupCreated(BackupCreatedEventArgs e)
        {
            BackupCreated?.Invoke(this, e);
        }
        #endregion;

        #region IDisposable Implementation;
        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cts?.Cancel();

                    try
                    {
                        ShutdownAsync().Wait(5000);
                    }
                    catch
                    {
                        // Ignore shutdown errors during dispose;
                    }

                    _maintenanceTimer?.Dispose();
                    _statisticsTimer?.Dispose();
                    _cts?.Dispose();
                    _connectionManager?.Dispose();

                    lock (_cachedResults)
                    {
                        _cachedResults.Clear();
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

        ~Database()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    /// <summary>
    /// Veritabanı durumları;
    /// </summary>
    public enum DatabaseState;
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting,
        Error,
        Maintenance;
    }

    /// <summary>
    /// Veritabanı sağlayıcısı;
    /// </summary>
    public enum DatabaseProvider;
    {
        SQLite,
        SqlServer,
        PostgreSQL,
        MySQL,
        Oracle,
        Unknown;
    }

    /// <summary>
    /// Sorgu tipi;
    /// </summary>
    public enum QueryType;
    {
        NonQuery,
        Scalar,
        Reader,
        StoredProcedure,
        Batch;
    }

    /// <summary>
    /// Veritabanı konfigürasyonu;
    /// </summary>
    public class DatabaseConfig;
    {
        public string DatabaseName { get; set; } = "NEDADatabase";
        public DatabaseProvider Provider { get; set; } = DatabaseProvider.SQLite;
        public string ConnectionString { get; set; } = "Data Source=NEDADatabase.db;Version=3;";
        public int MaxConnectionPoolSize { get; set; } = 100;
        public int MinConnectionPoolSize { get; set; } = 10;
        public int ConnectionTimeout { get; set; } = 30; // seconds;
        public int CommandTimeout { get; set; } = 30; // seconds;
        public bool EnableQueryCaching { get; set; } = true;
        public bool CreateIfNotExists { get; set; } = true;
        public bool EnableScheduledMaintenance { get; set; } = true;
        public int MaintenanceIntervalHours { get; set; } = 24;
        public int StatisticsUpdateIntervalMinutes { get; set; } = 60;
        public bool EnableAutoBackup { get; set; } = true;
        public int AutoBackupIntervalHours { get; set; } = 24;
        public string BackupPath { get; set; } = "./Backups";
        public int MaxBackupFiles { get; set; } = 30;
        public bool EnablePerformanceMetrics { get; set; } = true;
        public bool EnableDetailedLogging { get; set; } = true;

        public static DatabaseConfig Default => new DatabaseConfig();

        public string GetMaskedConnectionString()
        {
            // Hassas bilgileri maskele;
            var parts = ConnectionString.Split(';');
            var maskedParts = parts.Select(part =>
            {
                if (part.Trim().StartsWith("Password=", StringComparison.OrdinalIgnoreCase) ||
                    part.Trim().StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase))
                {
                    return "Password=*****";
                }
                return part;
            });

            return string.Join(";", maskedParts);
        }
    }

    /// <summary>
    /// Tablo sütunu;
    /// </summary>
    public class TableColumn;
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool IsNullable { get; set; } = true;
        public string DefaultValue { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsUnique { get; set; }
        public bool IsAutoIncrement { get; set; }
        public string Constraints { get; set; }
    }

    /// <summary>
    /// Tablo kısıtlaması;
    /// </summary>
    public class TableConstraint;
    {
        public string Name { get; set; }
        public ConstraintType Type { get; set; }
        public string Columns { get; set; }
        public string ReferenceTable { get; set; }
        public string ReferenceColumns { get; set; }
        public string Definition { get; set; }
    }

    /// <summary>
    /// Kısıtlama tipi;
    /// </summary>
    public enum ConstraintType;
    {
        PrimaryKey,
        ForeignKey,
        Unique,
        Check,
        Default;
    }

    /// <summary>
    /// Tablo bilgisi;
    /// </summary>
    public class TableInfo;
    {
        public string TableName { get; set; }
        public DateTime Created { get; set; }
        public long RowCount { get; set; }
        public long Size { get; set; }
    }

    /// <summary>
    /// Tablo şeması;
    /// </summary>
    public class TableSchema;
    {
        public string TableName { get; set; }
        public List<TableColumn> Columns { get; set; } = new List<TableColumn>();
        public List<TableIndex> Indexes { get; set; } = new List<TableIndex>();
        public List<ForeignKey> ForeignKeys { get; set; } = new List<ForeignKey>();
        public long RowCount { get; set; }
        public DateTime? LastAnalyzed { get; set; }
    }

    /// <summary>
    /// Tablo indeksi;
    /// </summary>
    public class TableIndex;
    {
        public string Name { get; set; }
        public bool IsUnique { get; set; }
        public string ColumnName { get; set; }
        public bool IsClustered { get; set; }
    }

    /// <summary>
    /// Foreign key;
    /// </summary>
    public class ForeignKey;
    {
        public string Name { get; set; }
        public string FromTable { get; set; }
        public string FromColumn { get; set; }
        public string ToTable { get; set; }
        public string ToColumn { get; set; }
        public string OnDelete { get; set; }
        public string OnUpdate { get; set; }
    }

    /// <summary>
    /// Stored procedure sonucu;
    /// </summary>
    public class StoredProcedureResult;
    {
        public List<Dictionary<string, object>> Results { get; set; } = new List<Dictionary<string, object>>();
        public Dictionary<string, object> OutputParameters { get; set; } = new Dictionary<string, object>();
        public TimeSpan ExecutionTime { get; set; }
        public bool Success { get; set; } = true;
    }

    /// <summary>
    /// Batch statement sonucu;
    /// </summary>
    public class BatchStatementResult;
    {
        public int StatementIndex { get; set; }
        public string Sql { get; set; }
        public int AffectedRows { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Batch sonucu;
    /// </summary>
    public class BatchResult;
    {
        public List<BatchStatementResult> StatementResults { get; set; } = new List<BatchStatementResult>();
        public TimeSpan TotalDuration { get; set; }
        public bool Success { get; set; }
    }

    /// <summary>
    /// Optimizasyon sonucu;
    /// </summary>
    public class OptimizationResult;
    {
        public int TablesOptimized { get; set; }
        public int IndexesRebuilt { get; set; }
        public long SpaceFreed { get; set; } // bytes;
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Validasyon sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public DateTime ValidationTime { get; set; }
    }

    /// <summary>
    /// Veritabanı durumu;
    /// </summary>
    public class DatabaseStatus;
    {
        public DateTime Timestamp { get; set; }
        public string DatabaseName { get; set; }
        public DatabaseState State { get; set; }
        public bool IsConnected { get; set; }
        public bool HasActiveTransaction { get; set; }
        public long DatabaseSize { get; set; }
        public int TableCount { get; set; }
        public int TotalQueries { get; set; }
        public int FailedQueries { get; set; }
        public double SuccessRate { get; set; }
        public DateTime LastConnected { get; set; }
        public DateTime LastMaintenance { get; set; }
        public PerformanceMetricsSnapshot PerformanceMetrics { get; set; }
    }

    /// <summary>
    /// Performans metrikleri;
    /// </summary>
    public class PerformanceMetrics;
    {
        private readonly List<double> _queryTimes;
        private readonly object _metricsLock = new object();
        private int _totalQueries;
        private int _successfulQueries;
        private DateTime _startTime;

        public PerformanceMetrics()
        {
            _queryTimes = new List<double>();
            _startTime = DateTime.UtcNow;
        }

        public void RecordQuery(TimeSpan duration, bool success)
        {
            lock (_metricsLock)
            {
                _totalQueries++;
                if (success)
                {
                    _successfulQueries++;
                }
                _queryTimes.Add(duration.TotalMilliseconds);

                // Sadece son 1000 sorguyu tut;
                if (_queryTimes.Count > 1000)
                {
                    _queryTimes.RemoveAt(0);
                }
            }
        }

        public PerformanceMetricsSnapshot GetSnapshot()
        {
            lock (_metricsLock)
            {
                return new PerformanceMetricsSnapshot;
                {
                    Timestamp = DateTime.UtcNow,
                    TotalQueries = _totalQueries,
                    SuccessfulQueries = _successfulQueries,
                    AverageQueryTime = _queryTimes.Count > 0 ? _queryTimes.Average() : 0,
                    MaxQueryTime = _queryTimes.Count > 0 ? _queryTimes.Max() : 0,
                    MinQueryTime = _queryTimes.Count > 0 ? _queryTimes.Min() : 0,
                    Uptime = DateTime.UtcNow - _startTime,
                    QueriesPerMinute = _totalQueries / Math.Max((DateTime.UtcNow - _startTime).TotalMinutes, 1)
                };
            }
        }
    }

    /// <summary>
    /// Performans metrikleri snapshot'ı;
    /// </summary>
    public class PerformanceMetricsSnapshot;
    {
        public DateTime Timestamp { get; set; }
        public int TotalQueries { get; set; }
        public int SuccessfulQueries { get; set; }
        public double AverageQueryTime { get; set; } // ms;
        public double MaxQueryTime { get; set; } // ms;
        public double MinQueryTime { get; set; } // ms;
        public TimeSpan Uptime { get; set; }
        public double QueriesPerMinute { get; set; }
        public double SuccessRate => TotalQueries > 0 ? (double)SuccessfulQueries / TotalQueries * 100 : 100;
    }

    // Event Args sınıfları;
    public class DatabaseStateChangedEventArgs : EventArgs;
    {
        public DatabaseState PreviousState { get; set; }
        public DatabaseState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ConnectionOpenedEventArgs : EventArgs;
    {
        public string DatabaseName { get; set; }
        public DateTime ConnectionTime { get; set; }
        public DatabaseProvider Provider { get; set; }
    }

    public class ConnectionClosedEventArgs : EventArgs;
    {
        public string DatabaseName { get; set; }
        public DateTime DisconnectTime { get; set; }
        public string Reason { get; set; }
    }

    public class QueryExecutedEventArgs : EventArgs;
    {
        public QueryType QueryType { get; set; }
        public string Sql { get; set; }
        public DateTime ExecutionTime { get; set; }
        public TimeSpan Duration { get; set; }
        public int AffectedRows { get; set; }
        public string ResultType { get; set; }
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

    public class MaintenancePerformedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public string MaintenanceType { get; set; }
    }

    public class DatabaseOptimizedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public int TablesOptimized { get; set; }
        public int IndexesRebuilt { get; set; }
        public long SpaceFreed { get; set; }
    }

    public class BackupCreatedEventArgs : EventArgs;
    {
        public string BackupFileName { get; set; }
        public long BackupSize { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
    }

    // Exception sınıfı;
    public class DatabaseException : Exception
    {
        public ErrorCode ErrorCode { get; }

        public DatabaseException(ErrorCode errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Yardımcı sınıflar (kısmi implementasyon)
    internal class DatabaseValidator;
    {
        private readonly ILogger _logger;

        public DatabaseValidator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<ValidationResult> ValidateDatabaseAsync(
            ConnectionManager connectionManager,
            CancellationToken cancellationToken)
        {
            var result = new ValidationResult;
            {
                ValidationTime = DateTime.UtcNow,
                IsValid = true;
            };

            try
            {
                using (var connection = await connectionManager.GetConnectionAsync(cancellationToken))
                {
                    // Bağlantı testi;
                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    var testResult = await command.ExecuteScalarAsync(cancellationToken);

                    if (testResult == null || Convert.ToInt32(testResult) != 1)
                    {
                        result.IsValid = false;
                        result.Issues.Add("Connection test failed");
                    }

                    // Tablo bütünlüğü kontrolü (SQLite için)
                    command.CommandText = "PRAGMA integrity_check";
                    var integrity = await command.ExecuteScalarAsync(cancellationToken);

                    if (integrity?.ToString() != "ok")
                    {
                        result.IsValid = false;
                        result.Issues.Add($"Database integrity check failed: {integrity}");
                    }

                    // Foreign key kontrolü;
                    command.CommandText = "PRAGMA foreign_key_check";
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    {
                        if (reader.HasRows)
                        {
                            result.IsValid = false;
                            result.Issues.Add("Foreign key constraints violation detected");
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Database validation failed: {ex.Message}", ex);
                result.IsValid = false;
                result.Issues.Add($"Validation error: {ex.Message}");
                return result;
            }
        }
    }

    internal class DatabaseOptimizer;
    {
        private readonly ILogger _logger;

        public DatabaseOptimizer(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<OptimizationResult> OptimizeDatabaseAsync(
            ConnectionManager connectionManager,
            CancellationToken cancellationToken)
        {
            var result = new OptimizationResult();
            var startTime = DateTime.UtcNow;

            try
            {
                using (var connection = await connectionManager.GetConnectionAsync(cancellationToken))
                {
                    // VACUUM (SQLite için)
                    var command = connection.CreateCommand();
                    command.CommandText = "VACUUM";
                    await command.ExecuteNonQueryAsync(cancellationToken);

                    // ANALYZE;
                    command.CommandText = "ANALYZE";
                    await command.ExecuteNonQueryAsync(cancellationToken);

                    // Indexleri yeniden oluştur;
                    var tables = await GetTablesAsync(connection, cancellationToken);
                    result.TablesOptimized = tables.Count;

                    foreach (var table in tables)
                    {
                        await RebuildTableIndexesAsync(connection, table, cancellationToken);
                    }

                    result.IndexesRebuilt = tables.Count;
                }

                result.Duration = DateTime.UtcNow - startTime;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Database optimization failed: {ex.Message}", ex);
                throw new DatabaseException(
                    ErrorCode.OptimizationFailed,
                    "Failed to optimize database",
                    ex);
            }
        }

        private async Task<List<string>> GetTablesAsync(
            DatabaseConnection connection,
            CancellationToken cancellationToken)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";

            var tables = new List<string>();
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            return tables;
        }

        private async Task RebuildTableIndexesAsync(
            DatabaseConnection connection,
            string tableName,
            CancellationToken cancellationToken)
        {
            try
            {
                var command = connection.CreateCommand();
                command.CommandText = $"REINDEX {tableName}";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to rebuild indexes for table {tableName}: {ex.Message}");
            }
        }
    }

    internal class DatabaseBackupService;
    {
        private readonly ILogger _logger;
        private readonly DatabaseConfig _config;

        public DatabaseBackupService(ILogger logger, DatabaseConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public async Task<BackupResult> CreateBackupAsync(
            ConnectionManager connectionManager,
            string backupPath = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                backupPath = backupPath ?? _config.BackupPath;
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var backupFileName = $"{_config.DatabaseName}_backup_{timestamp}.db";
                var fullPath = Path.Combine(backupPath, backupFileName);

                Directory.CreateDirectory(backupPath);

                // Veritabanını kopyala;
                await CopyDatabaseFileAsync(fullPath, cancellationToken);

                // Eski yedekleri temizle;
                CleanupOldBackups(backupPath);

                var fileInfo = new FileInfo(fullPath);

                return new BackupResult;
                {
                    BackupFileName = backupFileName,
                    BackupSize = fileInfo.Length,
                    Success = true;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Backup creation failed: {ex.Message}", ex);
                return new BackupResult;
                {
                    Success = false;
                };
            }
        }

        private async Task CopyDatabaseFileAsync(string destinationPath, CancellationToken cancellationToken)
        {
            // Önce veritabanını okuma modunda aç;
            using (var sourceStream = File.OpenRead(GetDatabaseFilePath()))
            using (var destinationStream = File.Create(destinationPath))
            {
                await sourceStream.CopyToAsync(destinationStream, 81920, cancellationToken);
            }
        }

        private string GetDatabaseFilePath()
        {
            var connectionString = _config.ConnectionString;
            var parts = connectionString.Split(';');

            foreach (var part in parts)
            {
                if (part.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                {
                    return part.Split('=')[1].Trim();
                }
            }

            throw new InvalidOperationException("Database file path not found in connection string");
        }

        private void CleanupOldBackups(string backupPath)
        {
            try
            {
                var backupFiles = Directory.GetFiles(backupPath, "*.db")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                if (backupFiles.Count > _config.MaxBackupFiles)
                {
                    for (int i = _config.MaxBackupFiles; i < backupFiles.Count; i++)
                    {
                        backupFiles[i].Delete();
                        _logger.Debug($"Deleted old backup: {backupFiles[i].Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to cleanup old backups: {ex.Message}");
            }
        }
    }
    #endregion;
}
