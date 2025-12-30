// NEDA.KnowledgeBase/DataManagement/DataAccess/ConnectionManager.cs;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace NEDA.KnowledgeBase.DataManagement.DataAccess;
{
    /// <summary>
    /// Endüstriyel seviyede veritabanı bağlantı yönetimi ve havuzlama sistemi;
    /// Connection pooling, failover, load balancing ve transaction yönetimi sağlar;
    /// </summary>
    public class ConnectionManager : IDisposable
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly ConnectionPoolConfig _config;
        private readonly object _poolLock = new object();
        private readonly List<DatabaseConnection> _connectionPool;
        private readonly Dictionary<string, ConnectionStatistics> _connectionStats;
        private readonly Timer _poolMaintenanceTimer;
        private readonly Timer _healthCheckTimer;
        private readonly CancellationTokenSource _cts;
        private readonly Random _random = new Random();
        private int _activeConnections;
        private int _totalConnectionsCreated;
        private int _totalConnectionsClosed;
        private DateTime _lastPoolCleanup;
        private bool _isDisposed;
        private readonly List<DatabaseFailoverConfig> _failoverConfigs;
        private int _currentPrimaryIndex;
        private readonly ConnectionValidator _connectionValidator;
        private readonly ConnectionFactory _connectionFactory;
        #endregion;

        #region Properties;
        /// <summary>
        /// Bağlantı yöneticisi durumu;
        /// </summary>
        public ConnectionManagerState State { get; private set; }

        /// <summary>
        /// Toplam aktif bağlantı sayısı;
        /// </summary>
        public int ActiveConnections => _activeConnections;

        /// <summary>
        /// Havuzdaki mevcut bağlantı sayısı;
        /// </summary>
        public int AvailableConnections;
        {
            get;
            {
                lock (_poolLock)
                {
                    return _connectionPool.Count;
                }
            }
        }

        /// <summary>
        /// Toplam oluşturulan bağlantı sayısı;
        /// </summary>
        public int TotalConnectionsCreated => _totalConnectionsCreated;

        /// <summary>
        /// Toplam kapatılan bağlantı sayısı;
        /// </summary>
        public int TotalConnectionsClosed => _totalConnectionsClosed;

        /// <summary>
        /// Havuz kapasitesi;
        /// </summary>
        public int PoolCapacity => _config.MaxPoolSize;

        /// <summary>
        /// Havuz kullanım yüzdesi;
        /// </summary>
        public double PoolUsagePercentage;
        {
            get;
            {
                var total = _config.MaxPoolSize;
                if (total == 0) return 0;
                return (double)_activeConnections / total * 100;
            }
        }

        /// <summary>
        /// Bağlantı metrikleri;
        /// </summary>
        public ConnectionMetrics CurrentMetrics => GetCurrentMetrics();

        /// <summary>
        /// Mevcut primary bağlantı konfigürasyonu;
        /// </summary>
        public DatabaseFailoverConfig CurrentPrimaryConfig =>
            _failoverConfigs[_currentPrimaryIndex];
        #endregion;

        #region Events;
        /// <summary>
        /// Bağlantı havuzu durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<PoolStatusChangedEventArgs> PoolStatusChanged;

        /// <summary>
        /// Bağlantı alındığında tetiklenir;
        /// </summary>
        public event EventHandler<ConnectionAcquiredEventArgs> ConnectionAcquired;

        /// <summary>
        /// Bağlantı döndürüldüğünde tetiklenir;
        /// </summary>
        public event EventHandler<ConnectionReturnedEventArgs> ConnectionReturned;

        /// <summary>
        /// Bağlantı hatası oluştuğunda tetiklenir;
        /// </summary>
        public event EventHandler<ConnectionErrorEventArgs> ConnectionError;

        /// <summary>
        /// Failover gerçekleştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<FailoverEventArgs> FailoverOccurred;

        /// <summary>
        /// Havuz bakımı tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<PoolMaintenanceEventArgs> PoolMaintenanceCompleted;
        #endregion;

        #region Constructor;
        /// <summary>
        /// ConnectionManager örneği oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="config">Bağlantı havuzu konfigürasyonu</param>
        /// <param name="connectionConfigs">Bağlantı konfigürasyonları</param>
        public ConnectionManager(
            ILogger logger,
            ConnectionPoolConfig config = null,
            List<DatabaseFailoverConfig> connectionConfigs = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? ConnectionPoolConfig.Default;
            _connectionPool = new List<DatabaseConnection>(_config.MaxPoolSize);
            _connectionStats = new Dictionary<string, ConnectionStatistics>();
            _failoverConfigs = connectionConfigs ?? new List<DatabaseFailoverConfig>();
            _cts = new CancellationTokenSource();

            _connectionValidator = new ConnectionValidator(_config, _logger);
            _connectionFactory = new ConnectionFactory(_config, _logger);

            _lastPoolCleanup = DateTime.UtcNow;
            State = ConnectionManagerState.Stopped;

            InitializeTimers();

            _logger.Info($"ConnectionManager initialized with MaxPoolSize: {_config.MaxPoolSize}, " +
                        $"MinPoolSize: {_config.MinPoolSize}, " +
                        $"ConnectionTimeout: {_config.ConnectionTimeout}s");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Bağlantı yöneticisini başlatır;
        /// </summary>
        public void Initialize()
        {
            try
            {
                if (State != ConnectionManagerState.Stopped)
                {
                    _logger.Warning("ConnectionManager already initialized or starting");
                    return;
                }

                State = ConnectionManagerState.Initializing;

                // Bağlantı konfigürasyonlarını doğrula;
                ValidateConnectionConfigs();

                // Minimum bağlantı sayısı kadar bağlantı oluştur;
                InitializeConnectionPool();

                // Health check başlat;
                StartHealthChecks();

                State = ConnectionManagerState.Running;

                OnPoolStatusChanged(new PoolStatusChangedEventArgs;
                {
                    PreviousState = ConnectionManagerState.Stopped,
                    NewState = State,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"ConnectionManager started successfully with {AvailableConnections} connections");
            }
            catch (Exception ex)
            {
                State = ConnectionManagerState.Error;
                _logger.Error($"Failed to initialize ConnectionManager: {ex.Message}", ex);
                throw new ConnectionManagerException(
                    ErrorCode.ConnectionManagerInitializationFailed,
                    "Failed to initialize connection manager",
                    ex);
            }
        }

        /// <summary>
        /// Bağlantı havuzundan bağlantı alır;
        /// </summary>
        /// <returns>Açık bağlantı nesnesi</returns>
        public async Task<DatabaseConnection> GetConnectionAsync()
        {
            return await GetConnectionAsync(null, CancellationToken.None);
        }

        /// <summary>
        /// Bağlantı havuzundan bağlantı alır (belirli database için)
        /// </summary>
        /// <param name="databaseName">Database adı</param>
        /// <returns>Açık bağlantı nesnesi</returns>
        public async Task<DatabaseConnection> GetConnectionAsync(string databaseName)
        {
            return await GetConnectionAsync(databaseName, CancellationToken.None);
        }

        /// <summary>
        /// Bağlantı havuzundan bağlantı alır (cancellation token ile)
        /// </summary>
        public async Task<DatabaseConnection> GetConnectionAsync(
            string databaseName,
            CancellationToken cancellationToken)
        {
            ValidateManagerState();

            try
            {
                var connection = await AcquireConnectionFromPoolAsync(databaseName, cancellationToken);

                OnConnectionAcquired(new ConnectionAcquiredEventArgs;
                {
                    ConnectionId = connection.ConnectionId,
                    DatabaseName = connection.DatabaseName,
                    AcquisitionTime = DateTime.UtcNow,
                    WaitTime = connection.AcquisitionTime;
                });

                _logger.Debug($"Connection acquired: {connection.ConnectionId}, " +
                            $"Database: {connection.DatabaseName}");

                return connection;
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("Connection acquisition cancelled");
                throw;
            }
            catch (Exception ex)
            {
                OnConnectionError(new ConnectionErrorEventArgs;
                {
                    ErrorCode = ErrorCode.ConnectionAcquisitionFailed,
                    ErrorMessage = $"Failed to acquire connection: {ex.Message}",
                    ConnectionId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Error($"Failed to acquire connection: {ex.Message}", ex);
                throw new ConnectionManagerException(
                    ErrorCode.ConnectionAcquisitionFailed,
                    "Failed to acquire database connection",
                    ex);
            }
        }

        /// <summary>
        /// Bağlantıyı havuzuna geri döndürür;
        /// </summary>
        /// <param name="connection">Döndürülecek bağlantı</param>
        /// <param name="resetConnection">Bağlantıyı sıfırla</param>
        public void ReturnConnection(DatabaseConnection connection, bool resetConnection = false)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            try
            {
                lock (_poolLock)
                {
                    if (connection.IsDisposed || connection.IsInvalid)
                    {
                        // Geçersiz bağlantıyı kapat;
                        CloseConnection(connection);
                        return;
                    }

                    if (resetConnection)
                    {
                        connection.Reset();
                    }

                    // Bağlantı hala açık mı kontrol et;
                    if (connection.State != ConnectionState.Open)
                    {
                        try
                        {
                            connection.Open();
                        }
                        catch
                        {
                            CloseConnection(connection);
                            return;
                        }
                    }

                    // Havuz dolu mu kontrol et;
                    if (_connectionPool.Count >= _config.MaxPoolSize)
                    {
                        // En eski bağlantıyı kapat;
                        var oldestConnection = FindOldestIdleConnection();
                        if (oldestConnection != null)
                        {
                            CloseConnection(oldestConnection);
                        }
                    }

                    // Bağlantıyı havuz'a ekle;
                    connection.LastUsed = DateTime.UtcNow;
                    connection.IsInUse = false;
                    _connectionPool.Add(connection);

                    _activeConnections--;

                    UpdateConnectionStatistics(connection.ConnectionId, false);

                    OnConnectionReturned(new ConnectionReturnedEventArgs;
                    {
                        ConnectionId = connection.ConnectionId,
                        DatabaseName = connection.DatabaseName,
                        ReturnTime = DateTime.UtcNow,
                        TotalUsageTime = DateTime.UtcNow.Subtract(connection.AcquisitionTime)
                    });

                    _logger.Debug($"Connection returned: {connection.ConnectionId}, " +
                                $"Database: {connection.DatabaseName}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error returning connection {connection.ConnectionId}: {ex.Message}");
                CloseConnection(connection);
            }
        }

        /// <summary>
        /// Transaction scope içinde çalışacak bağlantı alır;
        /// </summary>
        /// <param name="isolationLevel">Transaction isolation level</param>
        /// <returns>Transaction bağlantısı</returns>
        public async Task<TransactionConnection> GetTransactionConnectionAsync(
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            var connection = await GetConnectionAsync();
            return new TransactionConnection(connection, isolationLevel);
        }

        /// <summary>
        /// Tüm bağlantıları temizler ve havuzu sıfırlar;
        /// </summary>
        public void ClearPool()
        {
            lock (_poolLock)
            {
                _logger.Info($"Clearing connection pool with {_connectionPool.Count} connections");

                foreach (var connection in _connectionPool)
                {
                    CloseConnection(connection);
                }

                _connectionPool.Clear();
                _activeConnections = 0;

                _logger.Info("Connection pool cleared");
            }
        }

        /// <summary>
        /// Bağlantı havuzu durumunu getirir;
        /// </summary>
        public ConnectionPoolStatus GetPoolStatus()
        {
            lock (_poolLock)
            {
                return new ConnectionPoolStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    ActiveConnections = _activeConnections,
                    AvailableConnections = _connectionPool.Count,
                    PoolSize = _config.MaxPoolSize,
                    MinPoolSize = _config.MinPoolSize,
                    UsagePercentage = PoolUsagePercentage,
                    TotalCreated = _totalConnectionsCreated,
                    TotalClosed = _totalConnectionsClosed,
                    State = State;
                };
            }
        }

        /// <summary>
        /// Bağlantı metriklerini getirir;
        /// </summary>
        public ConnectionMetrics GetMetrics()
        {
            return GetCurrentMetrics();
        }

        /// <summary>
        /// Bağlantıyı manuel olarak kapatır;
        /// </summary>
        public void CloseConnection(DatabaseConnection connection)
        {
            if (connection == null)
                return;

            try
            {
                lock (_poolLock)
                {
                    if (!connection.IsDisposed)
                    {
                        connection.Close();
                        connection.Dispose();

                        _totalConnectionsClosed++;
                        _activeConnections--;

                        _logger.Debug($"Connection closed: {connection.ConnectionId}");
                    }

                    // Havuz'dan kaldır;
                    _connectionPool.Remove(connection);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error closing connection {connection.ConnectionId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Failover yapar (birincil veritabanı başarısız olursa)
        /// </summary>
        public bool PerformFailover()
        {
            if (_failoverConfigs.Count <= 1)
            {
                _logger.Warning("Cannot perform failover: No failover configurations available");
                return false;
            }

            try
            {
                lock (_poolLock)
                {
                    var oldPrimary = _currentPrimaryIndex;

                    // Bir sonraki konfigürasyona geç;
                    _currentPrimaryIndex = (_currentPrimaryIndex + 1) % _failoverConfigs.Count;

                    // Havuz'u temizle;
                    ClearPool();

                    // Yeni bağlantılar oluştur;
                    InitializeConnectionPool();

                    OnFailoverOccurred(new FailoverEventArgs;
                    {
                        OldPrimaryConfig = _failoverConfigs[oldPrimary],
                        NewPrimaryConfig = _failoverConfigs[_currentPrimaryIndex],
                        Timestamp = DateTime.UtcNow,
                        Reason = "Manual failover"
                    });

                    _logger.Info($"Failover completed: {_failoverConfigs[oldPrimary].Name} -> " +
                               $"{_failoverConfigs[_currentPrimaryIndex].Name}");

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failover failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Bağlantı havuzunu düzenler (eski bağlantıları temizler)
        /// </summary>
        public void CleanupPool()
        {
            lock (_poolLock)
            {
                var removedCount = 0;
                var now = DateTime.UtcNow;

                // Minimum havuz boyutunu koru;
                var connectionsToKeep = Math.Max(_config.MinPoolSize, _activeConnections);

                for (int i = _connectionPool.Count - 1; i >= 0; i--)
                {
                    var connection = _connectionPool[i];

                    // Çok eski bağlantıları kapat;
                    if (now.Subtract(connection.LastUsed).TotalMinutes > _config.ConnectionIdleTimeoutMinutes &&
                        _connectionPool.Count > connectionsToKeep)
                    {
                        CloseConnection(connection);
                        _connectionPool.RemoveAt(i);
                        removedCount++;
                    }
                    // Geçersiz bağlantıları kapat;
                    else if (connection.IsInvalid || connection.IsDisposed)
                    {
                        CloseConnection(connection);
                        _connectionPool.RemoveAt(i);
                        removedCount++;
                    }
                }

                _lastPoolCleanup = now;

                OnPoolMaintenanceCompleted(new PoolMaintenanceEventArgs;
                {
                    Timestamp = now,
                    RemovedConnections = removedCount,
                    RemainingConnections = _connectionPool.Count,
                    Reason = "Scheduled cleanup"
                });

                if (removedCount > 0)
                {
                    _logger.Debug($"Pool cleanup removed {removedCount} connections");
                }
            }
        }

        /// <summary>
        /// Bağlantı yöneticisini durdurur;
        /// </summary>
        public void Shutdown()
        {
            try
            {
                if (State == ConnectionManagerState.Stopped || State == ConnectionManagerState.Stopping)
                    return;

                State = ConnectionManagerState.Stopping;

                // Timers'ı durdur;
                _poolMaintenanceTimer?.Dispose();
                _healthCheckTimer?.Dispose();

                // Cancellation token'ı iptal et;
                _cts.Cancel();

                // Tüm bağlantıları kapat;
                ClearPool();

                State = ConnectionManagerState.Stopped;

                OnPoolStatusChanged(new PoolStatusChangedEventArgs;
                {
                    PreviousState = ConnectionManagerState.Running,
                    NewState = State,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info("ConnectionManager shutdown completed");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during shutdown: {ex.Message}", ex);
                throw;
            }
        }
        #endregion;

        #region Private Methods;
        private void InitializeTimers()
        {
            // Havuz bakım timer'ı;
            _poolMaintenanceTimer = new Timer(
                _ => CleanupPool(),
                null,
                TimeSpan.FromMinutes(_config.PoolMaintenanceIntervalMinutes),
                TimeSpan.FromMinutes(_config.PoolMaintenanceIntervalMinutes));

            // Health check timer'ı;
            _healthCheckTimer = new Timer(
                _ => PerformHealthChecks(),
                null,
                TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds),
                TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds));
        }

        private void ValidateConnectionConfigs()
        {
            if (_failoverConfigs.Count == 0)
            {
                throw new ConnectionManagerException(
                    ErrorCode.NoConnectionConfigurations,
                    "No database connection configurations provided");
            }

            foreach (var config in _failoverConfigs)
            {
                if (string.IsNullOrWhiteSpace(config.ConnectionString))
                {
                    throw new ConnectionManagerException(
                        ErrorCode.InvalidConnectionString,
                        $"Invalid connection string for configuration: {config.Name}");
                }
            }

            _currentPrimaryIndex = 0;
        }

        private void InitializeConnectionPool()
        {
            lock (_poolLock)
            {
                for (int i = 0; i < _config.MinPoolSize; i++)
                {
                    try
                    {
                        var connection = CreateConnection();
                        if (connection != null)
                        {
                            _connectionPool.Add(connection);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to create initial connection: {ex.Message}");
                    }
                }

                _logger.Info($"Initialized connection pool with {_connectionPool.Count} connections");
            }
        }

        private async Task<DatabaseConnection> AcquireConnectionFromPoolAsync(
            string databaseName,
            CancellationToken cancellationToken)
        {
            var waitStartTime = DateTime.UtcNow;
            var timeoutTime = waitStartTime.AddSeconds(_config.ConnectionTimeout);

            while (DateTime.UtcNow < timeoutTime && !cancellationToken.IsCancellationRequested)
            {
                DatabaseConnection connection = null;

                lock (_poolLock)
                {
                    // Havuz'dan uygun bağlantı bul;
                    connection = FindAvailableConnection(databaseName);

                    if (connection == null && _connectionPool.Count < _config.MaxPoolSize)
                    {
                        // Yeni bağlantı oluştur;
                        connection = CreateConnection(databaseName);
                    }

                    if (connection != null)
                    {
                        // Bağlantıyı kullanıma al;
                        connection.IsInUse = true;
                        connection.AcquisitionTime = DateTime.UtcNow;
                        _connectionPool.Remove(connection);
                        _activeConnections++;

                        UpdateConnectionStatistics(connection.ConnectionId, true);

                        return connection;
                    }
                }

                // Bağlantı yoksa bekle;
                if (cancellationToken.IsCancellationRequested)
                    break;

                await Task.Delay(100, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Connection acquisition cancelled");
            }

            throw new ConnectionManagerException(
                ErrorCode.ConnectionPoolExhausted,
                $"Connection pool exhausted. Timeout after {_config.ConnectionTimeout} seconds");
        }

        private DatabaseConnection FindAvailableConnection(string databaseName)
        {
            foreach (var connection in _connectionPool)
            {
                if (!connection.IsInUse && !connection.IsInvalid && !connection.IsDisposed)
                {
                    if (string.IsNullOrEmpty(databaseName) ||
                        connection.DatabaseName == databaseName)
                    {
                        // Bağlantıyı test et;
                        if (_connectionValidator.Validate(connection))
                        {
                            return connection;
                        }
                        else;
                        {
                            // Geçersiz bağlantıyı kapat;
                            CloseConnection(connection);
                            _connectionPool.Remove(connection);
                        }
                    }
                }
            }

            return null;
        }

        private DatabaseConnection FindOldestIdleConnection()
        {
            DatabaseConnection oldest = null;
            var oldestTime = DateTime.MaxValue;

            foreach (var connection in _connectionPool)
            {
                if (!connection.IsInUse && connection.LastUsed < oldestTime)
                {
                    oldest = connection;
                    oldestTime = connection.LastUsed;
                }
            }

            return oldest;
        }

        private DatabaseConnection CreateConnection(string databaseName = null)
        {
            try
            {
                var config = _failoverConfigs[_currentPrimaryIndex];

                // Database name override;
                var connectionString = config.ConnectionString;
                if (!string.IsNullOrEmpty(databaseName))
                {
                    connectionString = UpdateConnectionStringDatabase(connectionString, databaseName);
                }

                var connection = _connectionFactory.CreateConnection(
                    connectionString,
                    config.ProviderName,
                    databaseName);

                connection.Open();

                _totalConnectionsCreated++;

                _logger.Debug($"Created new connection: {connection.ConnectionId}, " +
                            $"Database: {connection.DatabaseName}");

                return connection;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create database connection: {ex.Message}", ex);

                // Failover dene;
                if (_failoverConfigs.Count > 1)
                {
                    _logger.Info("Attempting failover due to connection creation failure");
                    PerformFailover();
                    return CreateConnection(databaseName); // Recursive retry
                }

                throw new ConnectionManagerException(
                    ErrorCode.ConnectionCreationFailed,
                    "Failed to create database connection",
                    ex);
            }
        }

        private string UpdateConnectionStringDatabase(string connectionString, string databaseName)
        {
            // Bu metod connection string'deki database'i günceller;
            // Gerçek implementasyonda connection string parsing yapılmalı;
            return connectionString;
        }

        private void UpdateConnectionStatistics(string connectionId, bool isAcquired)
        {
            if (!_connectionStats.TryGetValue(connectionId, out var stats))
            {
                stats = new ConnectionStatistics { ConnectionId = connectionId };
                _connectionStats[connectionId] = stats;
            }

            if (isAcquired)
            {
                stats.AcquisitionCount++;
                stats.LastAcquired = DateTime.UtcNow;
            }
            else;
            {
                stats.ReturnCount++;
                stats.LastReturned = DateTime.UtcNow;
            }
        }

        private void StartHealthChecks()
        {
            Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await PerformHealthChecksAsync(_cts.Token);
                        await Task.Delay(TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds),
                                       _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Health check error: {ex.Message}");
                    }
                }
            }, _cts.Token);
        }

        private async Task PerformHealthChecksAsync(CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            lock (_poolLock)
            {
                foreach (var connection in _connectionPool)
                {
                    if (!connection.IsInUse && !cancellationToken.IsCancellationRequested)
                    {
                        tasks.Add(Task.Run(() => _connectionValidator.HealthCheck(connection)));
                    }
                }
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        private void PerformHealthChecks()
        {
            try
            {
                // Sync health check - timer callback için;
                lock (_poolLock)
                {
                    foreach (var connection in _connectionPool)
                    {
                        if (!connection.IsInUse)
                        {
                            try
                            {
                                if (!_connectionValidator.HealthCheck(connection))
                                {
                                    _logger.Warning($"Health check failed for connection: {connection.ConnectionId}");
                                    CloseConnection(connection);
                                    _connectionPool.Remove(connection);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"Health check error for {connection.ConnectionId}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Health check process error: {ex.Message}", ex);
            }
        }

        private ConnectionMetrics GetCurrentMetrics()
        {
            lock (_poolLock)
            {
                var metrics = new ConnectionMetrics;
                {
                    Timestamp = DateTime.UtcNow,
                    ActiveConnections = _activeConnections,
                    AvailableConnections = _connectionPool.Count,
                    TotalConnectionsCreated = _totalConnectionsCreated,
                    TotalConnectionsClosed = _totalConnectionsClosed,
                    PoolSize = _config.MaxPoolSize,
                    MinPoolSize = _config.MinPoolSize,
                    ConnectionTimeout = _config.ConnectionTimeout,
                    WaitTimeAverage = CalculateAverageWaitTime(),
                    UsagePercentage = PoolUsagePercentage,
                    FailedConnections = CalculateFailedConnections(),
                    HealthStatus = GetHealthStatus()
                };

                return metrics;
            }
        }

        private TimeSpan CalculateAverageWaitTime()
        {
            if (_connectionStats.Count == 0)
                return TimeSpan.Zero;

            var totalWait = TimeSpan.Zero;
            var count = 0;

            foreach (var stats in _connectionStats.Values)
            {
                if (stats.LastAcquired != DateTime.MinValue && stats.LastReturned != DateTime.MinValue)
                {
                    totalWait += stats.LastReturned.Subtract(stats.LastAcquired);
                    count++;
                }
            }

            return count > 0 ? TimeSpan.FromTicks(totalWait.Ticks / count) : TimeSpan.Zero;
        }

        private int CalculateFailedConnections()
        {
            var failed = 0;
            lock (_poolLock)
            {
                foreach (var connection in _connectionPool)
                {
                    if (connection.IsInvalid || connection.ErrorCount > 0)
                    {
                        failed++;
                    }
                }
            }
            return failed;
        }

        private ConnectionHealthStatus GetHealthStatus()
        {
            var usage = PoolUsagePercentage;

            if (usage > 90)
                return ConnectionHealthStatus.Critical;
            else if (usage > 70)
                return ConnectionHealthStatus.Warning;
            else if (_connectionPool.Count < _config.MinPoolSize)
                return ConnectionHealthStatus.Degraded;
            else;
                return ConnectionHealthStatus.Healthy;
        }

        private void ValidateManagerState()
        {
            if (State != ConnectionManagerState.Running)
            {
                throw new ConnectionManagerException(
                    ErrorCode.ConnectionManagerNotRunning,
                    "ConnectionManager is not running");
            }

            if (_isDisposed)
            {
                throw new ConnectionManagerException(
                    ErrorCode.ConnectionManagerDisposed,
                    "ConnectionManager has been disposed");
            }
        }

        private void OnPoolStatusChanged(PoolStatusChangedEventArgs e)
        {
            PoolStatusChanged?.Invoke(this, e);
        }

        private void OnConnectionAcquired(ConnectionAcquiredEventArgs e)
        {
            ConnectionAcquired?.Invoke(this, e);
        }

        private void OnConnectionReturned(ConnectionReturnedEventArgs e)
        {
            ConnectionReturned?.Invoke(this, e);
        }

        private void OnConnectionError(ConnectionErrorEventArgs e)
        {
            ConnectionError?.Invoke(this, e);
        }

        private void OnFailoverOccurred(FailoverEventArgs e)
        {
            FailoverOccurred?.Invoke(this, e);
        }

        private void OnPoolMaintenanceCompleted(PoolMaintenanceEventArgs e)
        {
            PoolMaintenanceCompleted?.Invoke(this, e);
        }
        #endregion;

        #region IDisposable Implementation;
        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    Shutdown();

                    _poolMaintenanceTimer?.Dispose();
                    _healthCheckTimer?.Dispose();
                    _cts?.Dispose();

                    lock (_poolLock)
                    {
                        foreach (var connection in _connectionPool)
                        {
                            connection?.Dispose();
                        }
                        _connectionPool.Clear();
                    }

                    _connectionStats.Clear();
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ConnectionManager()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    /// <summary>
    /// Bağlantı yöneticisi durumları;
    /// </summary>
    public enum ConnectionManagerState;
    {
        Stopped,
        Initializing,
        Running,
        Stopping,
        Error;
    }

    /// <summary>
    /// Bağlantı sağlık durumu;
    /// </summary>
    public enum ConnectionHealthStatus;
    {
        Healthy,
        Degraded,
        Warning,
        Critical,
        Offline;
    }

    /// <summary>
    /// Bağlantı havuzu konfigürasyonu;
    /// </summary>
    public class ConnectionPoolConfig;
    {
        public int MaxPoolSize { get; set; } = 100;
        public int MinPoolSize { get; set; } = 10;
        public int ConnectionTimeout { get; set; } = 30; // seconds;
        public int ConnectionIdleTimeoutMinutes { get; set; } = 30;
        public int PoolMaintenanceIntervalMinutes { get; set; } = 5;
        public int HealthCheckIntervalSeconds { get; set; } = 60;
        public int CommandTimeout { get; set; } = 30; // seconds;
        public bool EnableConnectionResiliency { get; set; } = true;
        public int MaxRetryAttempts { get; set; } = 3;
        public int RetryDelayMilliseconds { get; set; } = 1000;
        public bool EnableStatistics { get; set; } = true;
        public bool EnableDetailedLogging { get; set; } = false;

        public static ConnectionPoolConfig Default => new ConnectionPoolConfig();
    }

    /// <summary>
    /// Database failover konfigürasyonu;
    /// </summary>
    public class DatabaseFailoverConfig;
    {
        public string Name { get; set; }
        public string ConnectionString { get; set; }
        public string ProviderName { get; set; } = "System.Data.SqlClient";
        public bool IsPrimary { get; set; } = true;
        public int Priority { get; set; } = 1;
        public int Weight { get; set; } = 100;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Database bağlantı sınıfı;
    /// </summary>
    public class DatabaseConnection : DbConnection;
    {
        private readonly DbConnection _innerConnection;
        private readonly string _connectionId;

        public string ConnectionId => _connectionId;
        public string DatabaseName { get; }
        public DateTime Created { get; }
        public DateTime LastUsed { get; set; }
        public DateTime AcquisitionTime { get; set; }
        public bool IsInUse { get; set; }
        public bool IsInvalid { get; private set; }
        public int ErrorCount { get; private set; }
        public ConnectionStatistics Statistics { get; set; }

        public DatabaseConnection(DbConnection innerConnection, string databaseName)
        {
            _innerConnection = innerConnection ?? throw new ArgumentNullException(nameof(innerConnection));
            DatabaseName = databaseName;
            _connectionId = Guid.NewGuid().ToString("N");
            Created = DateTime.UtcNow;
            LastUsed = Created;
            IsInUse = false;
        }

        public void Reset()
        {
            // Bağlantıyı sıfırla (gerekiyorsa)
            if (_innerConnection.State == ConnectionState.Open)
            {
                // Connection reset logic;
            }
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            return _innerConnection.BeginTransaction(isolationLevel);
        }

        public override void ChangeDatabase(string databaseName)
        {
            _innerConnection.ChangeDatabase(databaseName);
        }

        public override void Close()
        {
            _innerConnection.Close();
            IsInUse = false;
            LastUsed = DateTime.UtcNow;
        }

        public override void Open()
        {
            try
            {
                if (_innerConnection.State != ConnectionState.Open)
                {
                    _innerConnection.Open();
                }
                IsInvalid = false;
            }
            catch (Exception)
            {
                IsInvalid = true;
                ErrorCount++;
                throw;
            }
        }

        public override string ConnectionString;
        {
            get => _innerConnection.ConnectionString;
            set => _innerConnection.ConnectionString = value;
        }

        public override string Database => _innerConnection.Database;
        public override string DataSource => _innerConnection.DataSource;
        public override string ServerVersion => _innerConnection.ServerVersion;
        public override ConnectionState State => _innerConnection.State;

        protected override DbCommand CreateDbCommand()
        {
            return _innerConnection.CreateCommand();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerConnection?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Transaction bağlantı sınıfı;
    /// </summary>
    public class TransactionConnection : IDisposable
    {
        private readonly DatabaseConnection _connection;
        private DbTransaction _transaction;

        public string ConnectionId => _connection.ConnectionId;
        public DbTransaction Transaction => _transaction;
        public DatabaseConnection InnerConnection => _connection;

        public TransactionConnection(DatabaseConnection connection, IsolationLevel isolationLevel)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _transaction = connection.BeginTransaction(isolationLevel);
        }

        public void Commit()
        {
            _transaction?.Commit();
            _transaction = null;
        }

        public void Rollback()
        {
            _transaction?.Rollback();
            _transaction = null;
        }

        public void Dispose()
        {
            _transaction?.Dispose();
            _transaction = null;
        }
    }

    /// <summary>
    /// Bağlantı metrikleri;
    /// </summary>
    public class ConnectionMetrics;
    {
        public DateTime Timestamp { get; set; }
        public int ActiveConnections { get; set; }
        public int AvailableConnections { get; set; }
        public int TotalConnectionsCreated { get; set; }
        public int TotalConnectionsClosed { get; set; }
        public int PoolSize { get; set; }
        public int MinPoolSize { get; set; }
        public int ConnectionTimeout { get; set; }
        public TimeSpan WaitTimeAverage { get; set; }
        public double UsagePercentage { get; set; }
        public int FailedConnections { get; set; }
        public ConnectionHealthStatus HealthStatus { get; set; }
        public Dictionary<string, double> PerformanceMetrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Bağlantı havuzu durumu;
    /// </summary>
    public class ConnectionPoolStatus;
    {
        public DateTime Timestamp { get; set; }
        public int ActiveConnections { get; set; }
        public int AvailableConnections { get; set; }
        public int PoolSize { get; set; }
        public int MinPoolSize { get; set; }
        public double UsagePercentage { get; set; }
        public int TotalCreated { get; set; }
        public int TotalClosed { get; set; }
        public ConnectionManagerState State { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Bağlantı istatistikleri;
    /// </summary>
    public class ConnectionStatistics;
    {
        public string ConnectionId { get; set; }
        public int AcquisitionCount { get; set; }
        public int ReturnCount { get; set; }
        public DateTime LastAcquired { get; set; }
        public DateTime LastReturned { get; set; }
        public TimeSpan TotalUsageTime { get; set; }
        public int ErrorCount { get; set; }
        public DateTime Created { get; set; }
    }

    // Event Args sınıfları;
    public class PoolStatusChangedEventArgs : EventArgs;
    {
        public ConnectionManagerState PreviousState { get; set; }
        public ConnectionManagerState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ConnectionAcquiredEventArgs : EventArgs;
    {
        public string ConnectionId { get; set; }
        public string DatabaseName { get; set; }
        public DateTime AcquisitionTime { get; set; }
        public TimeSpan WaitTime { get; set; }
    }

    public class ConnectionReturnedEventArgs : EventArgs;
    {
        public string ConnectionId { get; set; }
        public string DatabaseName { get; set; }
        public DateTime ReturnTime { get; set; }
        public TimeSpan TotalUsageTime { get; set; }
    }

    public class ConnectionErrorEventArgs : EventArgs;
    {
        public ErrorCode ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string ConnectionId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class FailoverEventArgs : EventArgs;
    {
        public DatabaseFailoverConfig OldPrimaryConfig { get; set; }
        public DatabaseFailoverConfig NewPrimaryConfig { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    public class PoolMaintenanceEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public int RemovedConnections { get; set; }
        public int RemainingConnections { get; set; }
        public string Reason { get; set; }
    }

    // Exception sınıfı;
    public class ConnectionManagerException : Exception
    {
        public ErrorCode ErrorCode { get; }

        public ConnectionManagerException(ErrorCode errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Yardımcı sınıflar;
    internal class ConnectionValidator;
    {
        private readonly ConnectionPoolConfig _config;
        private readonly ILogger _logger;

        public ConnectionValidator(ConnectionPoolConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public bool Validate(DatabaseConnection connection)
        {
            try
            {
                if (connection.IsDisposed || connection.IsInvalid)
                    return false;

                if (connection.State != ConnectionState.Open)
                {
                    connection.Open();
                }

                // Basit bir test sorgusu çalıştır;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT 1";
                    command.CommandTimeout = 5;
                    var result = command.ExecuteScalar();
                    return result != null && Convert.ToInt32(result) == 1;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Connection validation failed for {connection.ConnectionId}: {ex.Message}");
                return false;
            }
        }

        public bool HealthCheck(DatabaseConnection connection)
        {
            return Validate(connection);
        }
    }

    internal class ConnectionFactory;
    {
        private readonly ConnectionPoolConfig _config;
        private readonly ILogger _logger;

        public ConnectionFactory(ConnectionPoolConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public DatabaseConnection CreateConnection(string connectionString, string providerName, string databaseName)
        {
            try
            {
                var factory = DbProviderFactories.GetFactory(providerName);
                var innerConnection = factory.CreateConnection();

                if (innerConnection == null)
                {
                    throw new InvalidOperationException($"Unable to create connection for provider: {providerName}");
                }

                innerConnection.ConnectionString = connectionString;

                var connection = new DatabaseConnection(innerConnection, databaseName);
                connection.Statistics = new ConnectionStatistics;
                {
                    ConnectionId = connection.ConnectionId,
                    Created = DateTime.UtcNow;
                };

                return connection;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create connection factory: {ex.Message}", ex);
                throw;
            }
        }
    }
    #endregion;
}
