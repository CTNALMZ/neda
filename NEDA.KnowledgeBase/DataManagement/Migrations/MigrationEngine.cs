// NEDA.KnowledgeBase/Migrations/MigrationEngine.cs;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.KnowledgeBase.DataManagement.DataAccess;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.KnowledgeBase.Migrations;
{
    /// <summary>
    /// Endüstriyel seviyede database migration ve schema yönetim motoru;
    /// Version control, rollback, seed data ve deployment yönetimi sağlar;
    /// </summary>
    public class MigrationEngine : IDisposable
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly MigrationConfig _config;
        private readonly ConnectionManager _connectionManager;
        private readonly object _migrationLock = new object();
        private readonly Dictionary<string, DatabaseMigration> _migrations;
        private readonly Dictionary<string, MigrationHistory> _history;
        private readonly MigrationScriptParser _scriptParser;
        private readonly MigrationValidator _validator;
        private readonly MigrationGenerator _generator;
        private readonly CancellationTokenSource _cts;
        private bool _isInitialized;
        private bool _isMigrating;
        private string _currentVersion;
        private string _targetVersion;
        private readonly List<IMigrationHook> _migrationHooks;
        private readonly MigrationRollbackManager _rollbackManager;
        private readonly MigrationBackupService _backupService;
        #endregion;

        #region Properties;
        /// <summary>
        /// Migration motoru durumu;
        /// </summary>
        public MigrationEngineState State { get; private set; }

        /// <summary>
        /// Mevcut database versiyonu;
        /// </summary>
        public string CurrentVersion => _currentVersion;

        /// <summary>
        /// Hedef versiyon;
        /// </summary>
        public string TargetVersion => _targetVersion;

        /// <summary>
        /// Toplam migration sayısı;
        /// </summary>
        public int TotalMigrations => _migrations.Count;

        /// <summary>
        /// Uygulanmış migration sayısı;
        /// </summary>
        public int AppliedMigrations => _history.Count;

        /// <summary>
        /// Bekleyen migration sayısı;
        /// </summary>
        public int PendingMigrations => _migrations.Count - _history.Count;

        /// <summary>
        /// Migration metrikleri;
        /// </summary>
        public MigrationMetrics CurrentMetrics => GetCurrentMetrics();

        /// <summary>
        /// Konfigürasyon ayarları;
        /// </summary>
        public MigrationConfig Config => _config;

        /// <summary>
        /// Migration tarihçesi;
        /// </summary>
        public IReadOnlyList<MigrationHistory> History => _history.Values.OrderBy(h => h.AppliedAt).ToList();
        #endregion;

        #region Events;
        /// <summary>
        /// Migration başladığında tetiklenir;
        /// </summary>
        public event EventHandler<MigrationStartedEventArgs> MigrationStarted;

        /// <summary>
        /// Migration tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<MigrationCompletedEventArgs> MigrationCompleted;

        /// <summary>
        /// Migration başarısız olduğunda tetiklenir;
        /// </summary>
        public event EventHandler<MigrationFailedEventArgs> MigrationFailed;

        /// <summary>
        /// Migration ilerlemesi güncellendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<MigrationProgressEventArgs> MigrationProgress;

        /// <summary>
        /// Rollback başladığında tetiklenir;
        /// </summary>
        public event EventHandler<RollbackStartedEventArgs> RollbackStarted;

        /// <summary>
        /// Rollback tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<RollbackCompletedEventArgs> RollbackCompleted;

        /// <summary>
        /// Seed data uygulandığında tetiklenir;
        /// </summary>
        public event EventHandler<SeedDataAppliedEventArgs> SeedDataApplied;

        /// <summary>
        /// Database backup alındığında tetiklenir;
        /// </summary>
        public event EventHandler<BackupCreatedEventArgs> BackupCreated;
        #endregion;

        #region Constructor;
        /// <summary>
        /// MigrationEngine örneği oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="connectionManager">Bağlantı yöneticisi</param>
        /// <param name="config">Migration konfigürasyonu</param>
        public MigrationEngine(
            ILogger logger,
            ConnectionManager connectionManager,
            MigrationConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _config = config ?? MigrationConfig.Default;

            _migrations = new Dictionary<string, DatabaseMigration>();
            _history = new Dictionary<string, MigrationHistory>();
            _migrationHooks = new List<IMigrationHook>();
            _cts = new CancellationTokenSource();

            _scriptParser = new MigrationScriptParser(_logger);
            _validator = new MigrationValidator(_logger);
            _generator = new MigrationGenerator(_config, _logger);
            _rollbackManager = new MigrationRollbackManager(_logger, _config);
            _backupService = new MigrationBackupService(_logger, _config);

            State = MigrationEngineState.Stopped;

            _logger.Info($"MigrationEngine initialized with Provider: {_config.DatabaseProvider}, " +
                        $"SchemaHistoryTable: {_config.SchemaHistoryTable}");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Migration motorunu başlatır;
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.Warning("MigrationEngine already initialized");
                    return;
                }

                State = MigrationEngineState.Initializing;

                // Migration tablosunu oluştur;
                await EnsureMigrationTableExistsAsync();

                // Mevcut migration tarihçesini yükle;
                await LoadMigrationHistoryAsync();

                // Migration script'lerini yükle;
                await LoadMigrationsAsync();

                // Mevcut versiyonu belirle;
                _currentVersion = DetermineCurrentVersion();

                _isInitialized = true;
                State = MigrationEngineState.Ready;

                _logger.Info($"MigrationEngine initialized successfully. " +
                           $"Current version: {_currentVersion}, " +
                           $"Migrations: {_migrations.Count}, " +
                           $"Applied: {_history.Count}");
            }
            catch (Exception ex)
            {
                State = MigrationEngineState.Error;
                _logger.Error($"Failed to initialize MigrationEngine: {ex.Message}", ex);
                throw new MigrationException(
                    ErrorCode.MigrationEngineInitializationFailed,
                    "Failed to initialize migration engine",
                    ex);
            }
        }

        /// <summary>
        /// Tüm bekleyen migration'ları uygular;
        /// </summary>
        public async Task MigrateAsync()
        {
            await MigrateAsync(null, CancellationToken.None);
        }

        /// <summary>
        /// Belirli bir versiyona kadar migration uygular;
        /// </summary>
        public async Task MigrateAsync(string targetVersion, CancellationToken cancellationToken = default)
        {
            ValidateEngineState();

            try
            {
                lock (_migrationLock)
                {
                    if (_isMigrating)
                        throw new MigrationException(
                            ErrorCode.MigrationInProgress,
                            "Another migration is currently in progress");

                    _isMigrating = true;
                }

                _targetVersion = targetVersion ?? GetLatestVersion();

                OnMigrationStarted(new MigrationStartedEventArgs;
                {
                    CurrentVersion = _currentVersion,
                    TargetVersion = _targetVersion,
                    StartTime = DateTime.UtcNow,
                    TotalMigrations = GetPendingMigrationsCount(_targetVersion)
                });

                // Backup al (konfigürasyona göre)
                if (_config.CreateBackupBeforeMigration)
                {
                    await CreateBackupAsync();
                }

                // Migration hook'larını çalıştır (öncesi)
                await ExecutePreMigrationHooksAsync();

                // Migration'ları uygula;
                var result = await ApplyMigrationsAsync(_targetVersion, cancellationToken);

                // Migration hook'larını çalıştır (sonrası)
                await ExecutePostMigrationHooksAsync();

                // Versiyonu güncelle;
                _currentVersion = _targetVersion;

                OnMigrationCompleted(new MigrationCompletedEventArgs;
                {
                    PreviousVersion = result.PreviousVersion,
                    NewVersion = _currentVersion,
                    EndTime = DateTime.UtcNow,
                    Duration = result.Duration,
                    AppliedMigrations = result.AppliedMigrations,
                    Success = true;
                });

                _logger.Info($"Migration completed successfully: {result.PreviousVersion} -> {_currentVersion}, " +
                           $"Applied: {result.AppliedMigrations.Count}, " +
                           $"Duration: {result.Duration.TotalSeconds:F2}s");
            }
            catch (OperationCanceledException)
            {
                _logger.Warning("Migration cancelled by user");
                throw;
            }
            catch (Exception ex)
            {
                OnMigrationFailed(new MigrationFailedEventArgs;
                {
                    CurrentVersion = _currentVersion,
                    TargetVersion = _targetVersion,
                    ErrorMessage = ex.Message,
                    Timestamp = DateTime.UtcNow,
                    ErrorCode = ErrorCode.MigrationFailed;
                });

                _logger.Error($"Migration failed: {ex.Message}", ex);
                throw new MigrationException(
                    ErrorCode.MigrationFailed,
                    "Migration failed",
                    ex);
            }
            finally
            {
                lock (_migrationLock)
                {
                    _isMigrating = false;
                }
            }
        }

        /// <summary>
        /// Belirli bir versiyona geri döner (rollback)
        /// </summary>
        public async Task RollbackAsync(string targetVersion)
        {
            await RollbackAsync(targetVersion, CancellationToken.None);
        }

        /// <summary>
        /// Belirli bir versiyona geri döner (cancellation token ile)
        /// </summary>
        public async Task RollbackAsync(string targetVersion, CancellationToken cancellationToken)
        {
            ValidateEngineState();

            if (string.IsNullOrEmpty(targetVersion))
                throw new ArgumentException("Target version cannot be null or empty", nameof(targetVersion));

            try
            {
                OnRollbackStarted(new RollbackStartedEventArgs;
                {
                    CurrentVersion = _currentVersion,
                    TargetVersion = targetVersion,
                    StartTime = DateTime.UtcNow,
                    RollbackType = RollbackType.Version;
                });

                // Rollback hook'larını çalıştır (öncesi)
                await ExecutePreRollbackHooksAsync();

                // Rollback uygula;
                var result = await _rollbackManager.RollbackAsync(
                    _connectionManager,
                    _migrations,
                    _history,
                    targetVersion,
                    cancellationToken);

                // Tarihçeyi güncelle;
                await UpdateHistoryAfterRollbackAsync(result.RolledBackMigrations);

                // Versiyonu güncelle;
                _currentVersion = targetVersion;

                OnRollbackCompleted(new RollbackCompletedEventArgs;
                {
                    PreviousVersion = result.PreviousVersion,
                    NewVersion = _currentVersion,
                    EndTime = DateTime.UtcNow,
                    Duration = result.Duration,
                    RolledBackMigrations = result.RolledBackMigrations,
                    Success = true;
                });

                _logger.Info($"Rollback completed successfully: {result.PreviousVersion} -> {_currentVersion}, " +
                           $"Rolled back: {result.RolledBackMigrations.Count}, " +
                           $"Duration: {result.Duration.TotalSeconds:F2}s");
            }
            catch (Exception ex)
            {
                _logger.Error($"Rollback failed: {ex.Message}", ex);
                throw new MigrationException(
                    ErrorCode.RollbackFailed,
                    "Rollback failed",
                    ex);
            }
        }

        /// <summary>
        /// Son migration'ı geri alır;
        /// </summary>
        public async Task RollbackLastAsync()
        {
            if (_history.Count == 0)
                throw new MigrationException(
                    ErrorCode.NoMigrationsToRollback,
                    "No migrations to rollback");

            var lastMigration = _history.Values;
                .OrderByDescending(h => h.AppliedAt)
                .FirstOrDefault();

            if (lastMigration != null)
            {
                await RollbackAsync(lastMigration.Version);
            }
        }

        /// <summary>
        /// Yeni migration script'i oluşturur;
        /// </summary>
        public async Task<MigrationScript> GenerateMigrationAsync(
            string migrationName,
            MigrationType type = MigrationType.Schema)
        {
            try
            {
                var script = await _generator.GenerateMigrationAsync(migrationName, type);

                // Script'i doğrula;
                await _validator.ValidateMigrationScriptAsync(script);

                _logger.Info($"Migration script generated: {script.MigrationName}, " +
                           $"Version: {script.Version}, " +
                           $"Type: {type}");

                return script;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate migration script: {ex.Message}", ex);
                throw new MigrationException(
                    ErrorCode.MigrationGenerationFailed,
                    "Failed to generate migration script",
                    ex);
            }
        }

        /// <summary>
        /// Seed data uygular;
        /// </summary>
        public async Task ApplySeedDataAsync(SeedData seedData)
        {
            if (seedData == null)
                throw new ArgumentNullException(nameof(seedData));

            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync())
                {
                    var transaction = await connection.BeginTransactionAsync();

                    try
                    {
                        var applied = 0;
                        foreach (var script in seedData.SeedScripts)
                        {
                            await ExecuteSeedScriptAsync(connection, transaction, script);
                            applied++;

                            OnMigrationProgress(new MigrationProgressEventArgs;
                            {
                                CurrentStep = applied,
                                TotalSteps = seedData.SeedScripts.Count,
                                Message = $"Applying seed data: {script.TableName}",
                                ProgressPercentage = (double)applied / seedData.SeedScripts.Count * 100;
                            });
                        }

                        await transaction.CommitAsync();

                        OnSeedDataApplied(new SeedDataAppliedEventArgs;
                        {
                            SeedDataName = seedData.Name,
                            AppliedScripts = applied,
                            Timestamp = DateTime.UtcNow,
                            Success = true;
                        });

                        _logger.Info($"Seed data applied: {seedData.Name}, " +
                                   $"Scripts: {applied}, " +
                                   $"Tables: {seedData.SeedScripts.Select(s => s.TableName).Distinct().Count()}");
                    }
                    catch (Exception)
                    {
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to apply seed data: {ex.Message}", ex);
                throw new MigrationException(
                    ErrorCode.SeedDataFailed,
                    "Failed to apply seed data",
                    ex);
            }
        }

        /// <summary>
        /// Database backup oluşturur;
        /// </summary>
        public async Task<BackupResult> CreateBackupAsync()
        {
            try
            {
                var result = await _backupService.CreateBackupAsync(_connectionManager);

                OnBackupCreated(new BackupCreatedEventArgs;
                {
                    BackupFileName = result.BackupFileName,
                    BackupSize = result.BackupSize,
                    Timestamp = DateTime.UtcNow,
                    Success = true;
                });

                _logger.Info($"Database backup created: {result.BackupFileName}, " +
                           $"Size: {FormatFileSize(result.BackupSize)}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create backup: {ex.Message}", ex);
                throw new MigrationException(
                    ErrorCode.BackupFailed,
                    "Failed to create database backup",
                    ex);
            }
        }

        /// <summary>
        /// Migration hook'u ekler;
        /// </summary>
        public void AddMigrationHook(IMigrationHook hook)
        {
            if (hook == null)
                throw new ArgumentNullException(nameof(hook));

            lock (_migrationLock)
            {
                if (!_migrationHooks.Contains(hook))
                {
                    _migrationHooks.Add(hook);
                    _logger.Debug($"Migration hook added: {hook.GetType().Name}");
                }
            }
        }

        /// <summary>
        /// Migration hook'u kaldırır;
        /// </summary>
        public bool RemoveMigrationHook(IMigrationHook hook)
        {
            lock (_migrationLock)
            {
                var removed = _migrationHooks.Remove(hook);
                if (removed)
                {
                    _logger.Debug($"Migration hook removed: {hook.GetType().Name}");
                }
                return removed;
            }
        }

        /// <summary>
        /// Migration durumunu kontrol eder;
        /// </summary>
        public MigrationStatus CheckStatus()
        {
            lock (_migrationLock)
            {
                return new MigrationStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    CurrentVersion = _currentVersion,
                    LatestVersion = GetLatestVersion(),
                    TotalMigrations = _migrations.Count,
                    AppliedMigrations = _history.Count,
                    PendingMigrations = PendingMigrations,
                    IsInitialized = _isInitialized,
                    IsMigrating = _isMigrating,
                    State = State,
                    MigrationHealth = CheckMigrationHealth()
                };
            }
        }

        /// <summary>
        /// Migration script'lerini yeniden yükler;
        /// </summary>
        public async Task ReloadMigrationsAsync()
        {
            ValidateEngineState();

            lock (_migrationLock)
            {
                _migrations.Clear();
            }

            await LoadMigrationsAsync();

            _logger.Info("Migrations reloaded successfully");
        }

        /// <summary>
        /// Migration tarihçesini temizler (sadece geliştirme ortamında)
        /// </summary>
        public async Task ClearHistoryAsync()
        {
            if (!_config.AllowHistoryClear)
            {
                throw new MigrationException(
                    ErrorCode.HistoryClearNotAllowed,
                    "Clearing migration history is not allowed in current configuration");
            }

            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync())
                {
                    var command = connection.CreateCommand();
                    command.CommandText = $"TRUNCATE TABLE {_config.SchemaHistoryTable}";
                    await command.ExecuteNonQueryAsync();
                }

                _history.Clear();
                _currentVersion = "0.0.0";

                _logger.Warning("Migration history cleared");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to clear migration history: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Migration motorunu durdurur;
        /// </summary>
        public void Shutdown()
        {
            try
            {
                State = MigrationEngineState.Stopping;

                _cts.Cancel();

                _migrations.Clear();
                _history.Clear();
                _migrationHooks.Clear();

                State = MigrationEngineState.Stopped;
                _isInitialized = false;

                _logger.Info("MigrationEngine shutdown completed");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during shutdown: {ex.Message}", ex);
                throw;
            }
        }
        #endregion;

        #region Private Methods;
        private async Task EnsureMigrationTableExistsAsync()
        {
            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync())
                {
                    var tableExists = await CheckTableExistsAsync(connection, _config.SchemaHistoryTable);

                    if (!tableExists)
                    {
                        await CreateMigrationTableAsync(connection);
                        _logger.Info($"Migration table created: {_config.SchemaHistoryTable}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to ensure migration table exists: {ex.Message}", ex);
                throw;
            }
        }

        private async Task<bool> CheckTableExistsAsync(DatabaseConnection connection, string tableName)
        {
            var query = _config.DatabaseProvider switch;
            {
                DatabaseProvider.SqlServer =>
                    "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tableName",
                DatabaseProvider.PostgreSQL =>
                    "SELECT 1 FROM information_schema.tables WHERE table_name = @tableName",
                DatabaseProvider.MySQL =>
                    "SELECT 1 FROM information_schema.tables WHERE table_name = @tableName",
                DatabaseProvider.SQLite =>
                    "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@tableName",
                _ => throw new NotSupportedException($"Database provider not supported: {_config.DatabaseProvider}")
            };

            var command = connection.CreateCommand();
            command.CommandText = query;
            command.AddParameter("@tableName", tableName);

            var result = await command.ExecuteScalarAsync();
            return result != null;
        }

        private async Task CreateMigrationTableAsync(DatabaseConnection connection)
        {
            var createTableSql = _config.DatabaseProvider switch;
            {
                DatabaseProvider.SqlServer => $@"
                    CREATE TABLE [{_config.SchemaHistoryTable}] (
                        [MigrationId] NVARCHAR(255) PRIMARY KEY,
                        [Version] NVARCHAR(50) NOT NULL,
                        [Description] NVARCHAR(500) NULL,
                        [Script] NVARCHAR(MAX) NOT NULL,
                        [Checksum] NVARCHAR(64) NOT NULL,
                        [AppliedBy] NVARCHAR(100) NOT NULL,
                        [AppliedAt] DATETIME2 NOT NULL,
                        [ExecutionTime] INT NOT NULL,
                        [Success] BIT NOT NULL;
                    )",
                DatabaseProvider.PostgreSQL => $@"
                    CREATE TABLE {_config.SchemaHistoryTable} (
                        MigrationId VARCHAR(255) PRIMARY KEY,
                        Version VARCHAR(50) NOT NULL,
                        Description VARCHAR(500) NULL,
                        Script TEXT NOT NULL,
                        Checksum VARCHAR(64) NOT NULL,
                        AppliedBy VARCHAR(100) NOT NULL,
                        AppliedAt TIMESTAMP NOT NULL,
                        ExecutionTime INTEGER NOT NULL,
                        Success BOOLEAN NOT NULL;
                    )",
                _ => throw new NotSupportedException($"Database provider not supported: {_config.DatabaseProvider}")
            };

            var command = connection.CreateCommand();
            command.CommandText = createTableSql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task LoadMigrationHistoryAsync()
        {
            try
            {
                using (var connection = await _connectionManager.GetConnectionAsync())
                {
                    var command = connection.CreateCommand();
                    command.CommandText = $@"
                        SELECT MigrationId, Version, Description, Script, Checksum, 
                               AppliedBy, AppliedAt, ExecutionTime, Success;
                        FROM {_config.SchemaHistoryTable}
                        WHERE Success = 1;
                        ORDER BY AppliedAt";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var history = new MigrationHistory;
                            {
                                MigrationId = reader.GetString(0),
                                Version = reader.GetString(1),
                                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Script = reader.GetString(3),
                                Checksum = reader.GetString(4),
                                AppliedBy = reader.GetString(5),
                                AppliedAt = reader.GetDateTime(6),
                                ExecutionTime = reader.GetInt32(7),
                                Success = reader.GetBoolean(8)
                            };

                            _history[history.MigrationId] = history;
                        }
                    }
                }

                _logger.Debug($"Loaded {_history.Count} migration history records");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load migration history: {ex.Message}", ex);
                throw;
            }
        }

        private async Task LoadMigrationsAsync()
        {
            try
            {
                var migrationFiles = Directory.GetFiles(
                    _config.MigrationScriptsPath,
                    "*.sql",
                    SearchOption.AllDirectories);

                foreach (var file in migrationFiles.OrderBy(f => f))
                {
                    var migration = await _scriptParser.ParseMigrationFileAsync(file);

                    if (migration != null && !_migrations.ContainsKey(migration.Version))
                    {
                        _migrations[migration.Version] = migration;

                        // Checksum kontrolü;
                        if (_history.ContainsKey(migration.MigrationId))
                        {
                            var history = _history[migration.MigrationId];
                            if (history.Checksum != migration.Checksum)
                            {
                                _logger.Warning($"Migration checksum mismatch: {migration.Version}. " +
                                              $"Expected: {history.Checksum}, Actual: {migration.Checksum}");
                            }
                        }
                    }
                }

                _logger.Debug($"Loaded {_migrations.Count} migration scripts");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load migrations: {ex.Message}", ex);
                throw;
            }
        }

        private string DetermineCurrentVersion()
        {
            if (_history.Count == 0)
                return "0.0.0";

            var latestApplied = _history.Values;
                .OrderByDescending(h => h.AppliedAt)
                .FirstOrDefault();

            return latestApplied?.Version ?? "0.0.0";
        }

        private string GetLatestVersion()
        {
            if (_migrations.Count == 0)
                return _currentVersion;

            return _migrations.Values;
                .Select(m => m.Version)
                .OrderBy(v => v)
                .LastOrDefault();
        }

        private int GetPendingMigrationsCount(string targetVersion)
        {
            var pending = GetPendingMigrations(targetVersion);
            return pending.Count();
        }

        private IEnumerable<DatabaseMigration> GetPendingMigrations(string targetVersion)
        {
            var appliedVersions = new HashSet<string>(_history.Values.Select(h => h.Version));

            return _migrations.Values;
                .Where(m => VersionComparer.Compare(m.Version, _currentVersion) > 0 &&
                           (string.IsNullOrEmpty(targetVersion) ||
                            VersionComparer.Compare(m.Version, targetVersion) <= 0) &&
                           !appliedVersions.Contains(m.Version))
                .OrderBy(m => m.Version);
        }

        private async Task<MigrationResult> ApplyMigrationsAsync(string targetVersion, CancellationToken cancellationToken)
        {
            var result = new MigrationResult;
            {
                PreviousVersion = _currentVersion,
                StartTime = DateTime.UtcNow;
            };

            var pendingMigrations = GetPendingMigrations(targetVersion).ToList();

            if (pendingMigrations.Count == 0)
            {
                _logger.Info("No pending migrations to apply");
                return result;
            }

            using (var connection = await _connectionManager.GetConnectionAsync())
            {
                var transaction = await connection.BeginTransactionAsync();

                try
                {
                    for (int i = 0; i < pendingMigrations.Count; i++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            await transaction.RollbackAsync();
                            throw new OperationCanceledException("Migration cancelled");
                        }

                        var migration = pendingMigrations[i];
                        await ApplyMigrationAsync(connection, transaction, migration, i + 1, pendingMigrations.Count);

                        result.AppliedMigrations.Add(migration);
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        private async Task ApplyMigrationAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            DatabaseMigration migration,
            int currentStep,
            int totalSteps)
        {
            var startTime = DateTime.UtcNow;
            var migrationId = migration.MigrationId;

            try
            {
                OnMigrationProgress(new MigrationProgressEventArgs;
                {
                    CurrentStep = currentStep,
                    TotalSteps = totalSteps,
                    Message = $"Applying migration: {migration.Version} - {migration.Description}",
                    ProgressPercentage = (double)currentStep / totalSteps * 100;
                });

                // Migration script'ini çalıştır;
                await ExecuteMigrationScriptAsync(connection, transaction, migration.UpScript);

                // Tarihçeye kaydet;
                var history = new MigrationHistory;
                {
                    MigrationId = migrationId,
                    Version = migration.Version,
                    Description = migration.Description,
                    Script = migration.UpScript,
                    Checksum = migration.Checksum,
                    AppliedBy = Environment.UserName,
                    AppliedAt = DateTime.UtcNow,
                    ExecutionTime = (int)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    Success = true;
                };

                await SaveMigrationHistoryAsync(connection, transaction, history);

                _logger.Info($"Migration applied successfully: {migration.Version}, " +
                           $"Duration: {history.ExecutionTime}ms");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to apply migration {migration.Version}: {ex.Message}", ex);

                // Hata tarihçesini kaydet;
                await SaveMigrationErrorAsync(connection, transaction, migration, ex);

                throw;
            }
        }

        private async Task ExecuteMigrationScriptAsync(DatabaseConnection connection, DbTransaction transaction, string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return;

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = script;
            command.CommandTimeout = _config.MigrationTimeoutSeconds;

            await command.ExecuteNonQueryAsync();
        }

        private async Task ExecuteSeedScriptAsync(DatabaseConnection connection, DbTransaction transaction, SeedScript script)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = script.Script;
            command.CommandTimeout = _config.MigrationTimeoutSeconds;

            if (script.Parameters != null)
            {
                foreach (var param in script.Parameters)
                {
                    command.AddParameter(param.Key, param.Value);
                }
            }

            await command.ExecuteNonQueryAsync();
        }

        private async Task SaveMigrationHistoryAsync(DatabaseConnection connection, DbTransaction transaction, MigrationHistory history)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $@"
                INSERT INTO {_config.SchemaHistoryTable} 
                (MigrationId, Version, Description, Script, Checksum, AppliedBy, AppliedAt, ExecutionTime, Success)
                VALUES (@MigrationId, @Version, @Description, @Script, @Checksum, @AppliedBy, @AppliedAt, @ExecutionTime, @Success)";

            command.AddParameter("@MigrationId", history.MigrationId);
            command.AddParameter("@Version", history.Version);
            command.AddParameter("@Description", history.Description ?? (object)DBNull.Value);
            command.AddParameter("@Script", history.Script);
            command.AddParameter("@Checksum", history.Checksum);
            command.AddParameter("@AppliedBy", history.AppliedBy);
            command.AddParameter("@AppliedAt", history.AppliedAt);
            command.AddParameter("@ExecutionTime", history.ExecutionTime);
            command.AddParameter("@Success", history.Success);

            await command.ExecuteNonQueryAsync();

            _history[history.MigrationId] = history;
        }

        private async Task SaveMigrationErrorAsync(
            DatabaseConnection connection,
            DbTransaction transaction,
            DatabaseMigration migration,
            Exception error)
        {
            try
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $@"
                    INSERT INTO {_config.SchemaHistoryTable} 
                    (MigrationId, Version, Description, Script, Checksum, AppliedBy, AppliedAt, ExecutionTime, Success)
                    VALUES (@MigrationId, @Version, @Description, @Script, @Checksum, @AppliedBy, @AppliedAt, @ExecutionTime, @Success)";

                command.AddParameter("@MigrationId", migration.MigrationId);
                command.AddParameter("@Version", migration.Version);
                command.AddParameter("@Description", $"{migration.Description} - FAILED: {error.Message}");
                command.AddParameter("@Script", migration.UpScript);
                command.AddParameter("@Checksum", migration.Checksum);
                command.AddParameter("@AppliedBy", Environment.UserName);
                command.AddParameter("@AppliedAt", DateTime.UtcNow);
                command.AddParameter("@ExecutionTime", 0);
                command.AddParameter("@Success", false);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save migration error: {ex.Message}");
            }
        }

        private async Task UpdateHistoryAfterRollbackAsync(List<DatabaseMigration> rolledBackMigrations)
        {
            using (var connection = await _connectionManager.GetConnectionAsync())
            {
                var transaction = await connection.BeginTransactionAsync();

                try
                {
                    foreach (var migration in rolledBackMigrations)
                    {
                        var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = $"DELETE FROM {_config.SchemaHistoryTable} WHERE MigrationId = @MigrationId";
                        command.AddParameter("@MigrationId", migration.MigrationId);

                        await command.ExecuteNonQueryAsync();

                        _history.Remove(migration.MigrationId);
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
        }

        private async Task ExecutePreMigrationHooksAsync()
        {
            foreach (var hook in _migrationHooks.OfType<IPreMigrationHook>())
            {
                try
                {
                    await hook.BeforeMigrationAsync(_currentVersion, _targetVersion);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Pre-migration hook failed: {ex.Message}");
                }
            }
        }

        private async Task ExecutePostMigrationHooksAsync()
        {
            foreach (var hook in _migrationHooks.OfType<IPostMigrationHook>())
            {
                try
                {
                    await hook.AfterMigrationAsync(_currentVersion, _targetVersion);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Post-migration hook failed: {ex.Message}");
                }
            }
        }

        private async Task ExecutePreRollbackHooksAsync()
        {
            foreach (var hook in _migrationHooks.OfType<IPreRollbackHook>())
            {
                try
                {
                    await hook.BeforeRollbackAsync(_currentVersion, _targetVersion);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Pre-rollback hook failed: {ex.Message}");
                }
            }
        }

        private MigrationHealthStatus CheckMigrationHealth()
        {
            if (_history.Count == 0 && _migrations.Count == 0)
                return MigrationHealthStatus.Healthy;

            // Checksum kontrolü;
            foreach (var history in _history.Values)
            {
                if (!_migrations.TryGetValue(history.MigrationId, out var migration))
                {
                    return MigrationHealthStatus.Warning;
                }

                if (history.Checksum != migration.Checksum)
                {
                    return MigrationHealthStatus.Critical;
                }
            }

            return MigrationHealthStatus.Healthy;
        }

        private MigrationMetrics GetCurrentMetrics()
        {
            return new MigrationMetrics;
            {
                Timestamp = DateTime.UtcNow,
                CurrentVersion = _currentVersion,
                LatestVersion = GetLatestVersion(),
                TotalMigrations = _migrations.Count,
                AppliedMigrations = _history.Count,
                FailedMigrations = _history.Values.Count(h => !h.Success),
                AverageExecutionTime = _history.Values.Where(h => h.Success).Average(h => h.ExecutionTime),
                TotalExecutionTime = _history.Values.Where(h => h.Success).Sum(h => h.ExecutionTime),
                LastMigrationTime = _history.Values.OrderByDescending(h => h.AppliedAt).FirstOrDefault()?.AppliedAt,
                HealthStatus = CheckMigrationHealth()
            };
        }

        private void ValidateEngineState()
        {
            if (!_isInitialized)
                throw new MigrationException(
                    ErrorCode.MigrationEngineNotInitialized,
                    "MigrationEngine must be initialized before use");

            if (State != MigrationEngineState.Ready)
                throw new MigrationException(
                    ErrorCode.MigrationEngineNotReady,
                    $"MigrationEngine is not ready. Current state: {State}");
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

        private void OnMigrationStarted(MigrationStartedEventArgs e)
        {
            MigrationStarted?.Invoke(this, e);
        }

        private void OnMigrationCompleted(MigrationCompletedEventArgs e)
        {
            MigrationCompleted?.Invoke(this, e);
        }

        private void OnMigrationFailed(MigrationFailedEventArgs e)
        {
            MigrationFailed?.Invoke(this, e);
        }

        private void OnMigrationProgress(MigrationProgressEventArgs e)
        {
            MigrationProgress?.Invoke(this, e);
        }

        private void OnRollbackStarted(RollbackStartedEventArgs e)
        {
            RollbackStarted?.Invoke(this, e);
        }

        private void OnRollbackCompleted(RollbackCompletedEventArgs e)
        {
            RollbackCompleted?.Invoke(this, e);
        }

        private void OnSeedDataApplied(SeedDataAppliedEventArgs e)
        {
            SeedDataApplied?.Invoke(this, e);
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
                    Shutdown();

                    _cts?.Dispose();

                    _migrations.Clear();
                    _history.Clear();
                    _migrationHooks.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~MigrationEngine()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    /// <summary>
    /// Migration motoru durumları;
    /// </summary>
    public enum MigrationEngineState;
    {
        Stopped,
        Initializing,
        Ready,
        Migrating,
        RollingBack,
        Error,
        Stopping;
    }

    /// <summary>
    /// Migration sağlık durumu;
    /// </summary>
    public enum MigrationHealthStatus;
    {
        Healthy,
        Warning,
        Critical,
        Unknown;
    }

    /// <summary>
    /// Migration tipi;
    /// </summary>
    public enum MigrationType;
    {
        Schema,
        Data,
        Seed,
        Maintenance,
        Custom;
    }

    /// <summary>
    /// Rollback tipi;
    /// </summary>
    public enum RollbackType;
    {
        Version,
        LastMigration,
        All,
        Custom;
    }

    /// <summary>
    /// Database provider tipleri;
    /// </summary>
    public enum DatabaseProvider;
    {
        SqlServer,
        PostgreSQL,
        MySQL,
        SQLite,
        Oracle,
        Unknown;
    }

    /// <summary>
    /// Migration konfigürasyonu;
    /// </summary>
    public class MigrationConfig;
    {
        public string MigrationScriptsPath { get; set; } = "./Migrations/Scripts";
        public string SchemaHistoryTable { get; set; } = "__MigrationHistory";
        public DatabaseProvider DatabaseProvider { get; set; } = DatabaseProvider.SqlServer;
        public int MigrationTimeoutSeconds { get; set; } = 300;
        public bool CreateBackupBeforeMigration { get; set; } = true;
        public bool EnableTransaction { get; set; } = true;
        public bool ValidateChecksums { get; set; } = true;
        public bool AllowHistoryClear { get; set; } = false;
        public string BackupPath { get; set; } = "./Backups";
        public int MaxBackupFiles { get; set; } = 10;
        public bool EnableHooks { get; set; } = true;
        public bool EnableProgressTracking { get; set; } = true;

        public static MigrationConfig Default => new MigrationConfig();
    }

    /// <summary>
    /// Database migration sınıfı;
    /// </summary>
    public class DatabaseMigration;
    {
        public string MigrationId { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public MigrationType Type { get; set; }
        public string UpScript { get; set; }
        public string DownScript { get; set; }
        public string Checksum { get; set; }
        public DateTime Created { get; set; }
        public string Author { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Migration tarihçesi;
    /// </summary>
    public class MigrationHistory;
    {
        public string MigrationId { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Script { get; set; }
        public string Checksum { get; set; }
        public string AppliedBy { get; set; }
        public DateTime AppliedAt { get; set; }
        public int ExecutionTime { get; set; } // milisaniye;
        public bool Success { get; set; }
    }

    /// <summary>
    /// Migration script'i;
    /// </summary>
    public class MigrationScript;
    {
        public string ScriptId { get; set; }
        public string MigrationName { get; set; }
        public string Version { get; set; }
        public string UpScript { get; set; }
        public string DownScript { get; set; }
        public string FilePath { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Seed data sınıfı;
    /// </summary>
    public class SeedData;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<SeedScript> SeedScripts { get; set; } = new List<SeedScript>();
        public DateTime Created { get; set; }
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Seed script sınıfı;
    /// </summary>
    public class SeedScript;
    {
        public string TableName { get; set; }
        public string Script { get; set; }
        public int Order { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Migration sonucu;
    /// </summary>
    public class MigrationResult;
    {
        public string PreviousVersion { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public List<DatabaseMigration> AppliedMigrations { get; set; } = new List<DatabaseMigration>();
        public bool Success => AppliedMigrations.All(m => !string.IsNullOrEmpty(m.Version));
    }

    /// <summary>
    /// Rollback sonucu;
    /// </summary>
    public class RollbackResult;
    {
        public string PreviousVersion { get; set; }
        public string NewVersion { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public List<DatabaseMigration> RolledBackMigrations { get; set; } = new List<DatabaseMigration>();
        public bool Success { get; set; }
    }

    /// <summary>
    /// Backup sonucu;
    /// </summary>
    public class BackupResult;
    {
        public string BackupFileName { get; set; }
        public long BackupSize { get; set; }
        public DateTime BackupTime { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Migration durumu;
    /// </summary>
    public class MigrationStatus;
    {
        public DateTime Timestamp { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public int TotalMigrations { get; set; }
        public int AppliedMigrations { get; set; }
        public int PendingMigrations { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsMigrating { get; set; }
        public MigrationEngineState State { get; set; }
        public MigrationHealthStatus MigrationHealth { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Migration metrikleri;
    /// </summary>
    public class MigrationMetrics;
    {
        public DateTime Timestamp { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public int TotalMigrations { get; set; }
        public int AppliedMigrations { get; set; }
        public int FailedMigrations { get; set; }
        public double AverageExecutionTime { get; set; }
        public long TotalExecutionTime { get; set; }
        public DateTime? LastMigrationTime { get; set; }
        public MigrationHealthStatus HealthStatus { get; set; }
        public Dictionary<string, double> PerformanceMetrics { get; set; } = new Dictionary<string, double>();
    }

    // Event Args sınıfları;
    public class MigrationStartedEventArgs : EventArgs;
    {
        public string CurrentVersion { get; set; }
        public string TargetVersion { get; set; }
        public DateTime StartTime { get; set; }
        public int TotalMigrations { get; set; }
    }

    public class MigrationCompletedEventArgs : EventArgs;
    {
        public string PreviousVersion { get; set; }
        public string NewVersion { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public List<DatabaseMigration> AppliedMigrations { get; set; }
        public bool Success { get; set; }
    }

    public class MigrationFailedEventArgs : EventArgs;
    {
        public string CurrentVersion { get; set; }
        public string TargetVersion { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
        public ErrorCode ErrorCode { get; set; }
    }

    public class MigrationProgressEventArgs : EventArgs;
    {
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public string Message { get; set; }
        public double ProgressPercentage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class RollbackStartedEventArgs : EventArgs;
    {
        public string CurrentVersion { get; set; }
        public string TargetVersion { get; set; }
        public DateTime StartTime { get; set; }
        public RollbackType RollbackType { get; set; }
    }

    public class RollbackCompletedEventArgs : EventArgs;
    {
        public string PreviousVersion { get; set; }
        public string NewVersion { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public List<DatabaseMigration> RolledBackMigrations { get; set; }
        public bool Success { get; set; }
    }

    public class SeedDataAppliedEventArgs : EventArgs;
    {
        public string SeedDataName { get; set; }
        public int AppliedScripts { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
    }

    public class BackupCreatedEventArgs : EventArgs;
    {
        public string BackupFileName { get; set; }
        public long BackupSize { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
    }

    // Exception sınıfı;
    public class MigrationException : Exception
    {
        public ErrorCode ErrorCode { get; }

        public MigrationException(ErrorCode errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Migration hook interface'leri;
    public interface IMigrationHook { }

    public interface IPreMigrationHook : IMigrationHook;
    {
        Task BeforeMigrationAsync(string currentVersion, string targetVersion);
    }

    public interface IPostMigrationHook : IMigrationHook;
    {
        Task AfterMigrationAsync(string previousVersion, string newVersion);
    }

    public interface IPreRollbackHook : IMigrationHook;
    {
        Task BeforeRollbackAsync(string currentVersion, string targetVersion);
    }

    // Yardımcı sınıflar (kısmi implementasyon)
    internal class MigrationScriptParser;
    {
        private readonly ILogger _logger;

        public MigrationScriptParser(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<DatabaseMigration> ParseMigrationFileAsync(string filePath)
        {
            try
            {
                var content = await File.ReadAllTextAsync(filePath);
                var fileName = Path.GetFileNameWithoutExtension(filePath);

                // Script formatını parse et;
                // Format: VERSION__DESCRIPTION.sql;
                var parts = fileName.Split("__");
                if (parts.Length < 2)
                {
                    _logger.Warning($"Invalid migration file name format: {fileName}");
                    return null;
                }

                var migration = new DatabaseMigration;
                {
                    MigrationId = Guid.NewGuid().ToString("N"),
                    Version = parts[0].TrimStart('V'),
                    Description = parts[1].Replace('_', ' '),
                    UpScript = content,
                    Created = File.GetCreationTime(filePath),
                    Author = Environment.UserName,
                    Checksum = CalculateChecksum(content)
                };

                // Down script'i varsa ayır;
                if (content.Contains("-- DOWN"))
                {
                    var sections = content.Split(new[] { "-- DOWN" }, StringSplitOptions.RemoveEmptyEntries);
                    if (sections.Length > 1)
                    {
                        migration.UpScript = sections[0];
                        migration.DownScript = sections[1];
                    }
                }

                return migration;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to parse migration file {filePath}: {ex.Message}");
                return null;
            }
        }

        private string CalculateChecksum(string content)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var hash = md5.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }
    }

    internal class MigrationValidator;
    {
        private readonly ILogger _logger;

        public MigrationValidator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<bool> ValidateMigrationScriptAsync(MigrationScript script)
        {
            try
            {
                // Syntax kontrolü;
                if (string.IsNullOrWhiteSpace(script.UpScript))
                {
                    throw new MigrationException(
                        ErrorCode.InvalidMigrationScript,
                        "Up script cannot be empty");
                }

                // SQL injection kontrolü (basit)
                if (script.UpScript.Contains("DROP DATABASE") ||
                    script.UpScript.Contains("DELETE FROM"))
                {
                    _logger.Warning("Migration script contains potentially dangerous operations");
                }

                // Version format kontrolü;
                if (!IsValidVersion(script.Version))
                {
                    throw new MigrationException(
                        ErrorCode.InvalidVersionFormat,
                        $"Invalid version format: {script.Version}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Migration script validation failed: {ex.Message}");
                throw;
            }
        }

        private bool IsValidVersion(string version)
        {
            // SemVer formatı: X.Y.Z;
            var parts = version.Split('.');
            return parts.Length == 3 &&
                   parts.All(p => int.TryParse(p, out _));
        }
    }

    internal class MigrationGenerator;
    {
        private readonly MigrationConfig _config;
        private readonly ILogger _logger;

        public MigrationGenerator(MigrationConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<MigrationScript> GenerateMigrationAsync(string migrationName, MigrationType type)
        {
            try
            {
                var version = GenerateVersion();
                var scriptId = Guid.NewGuid().ToString("N");
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

                var script = new MigrationScript;
                {
                    ScriptId = scriptId,
                    MigrationName = migrationName,
                    Version = version,
                    GeneratedAt = DateTime.UtcNow;
                };

                // Template oluştur;
                var template = GenerateMigrationTemplate(migrationName, type, version);

                // Dosyaya kaydet;
                var fileName = $"V{version}__{migrationName.Replace(' ', '_')}.sql";
                var filePath = Path.Combine(_config.MigrationScriptsPath, fileName);

                await File.WriteAllTextAsync(filePath, template);

                script.FilePath = filePath;
                script.UpScript = template;

                return script;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate migration: {ex.Message}", ex);
                throw;
            }
        }

        private string GenerateVersion()
        {
            var now = DateTime.UtcNow;
            return $"{now.Year}.{now.Month}.{now.Day}_{now.Hour}{now.Minute}{now.Second}";
        }

        private string GenerateMigrationTemplate(string migrationName, MigrationType type, string version)
        {
            var template = new StringBuilder();

            template.AppendLine($"-- Migration: {migrationName}");
            template.AppendLine($"-- Version: {version}");
            template.AppendLine($"-- Type: {type}");
            template.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            template.AppendLine($"-- Author: {Environment.UserName}");
            template.AppendLine();

            switch (type)
            {
                case MigrationType.Schema:
                    template.AppendLine("-- UP: Schema changes");
                    template.AppendLine("BEGIN TRANSACTION;");
                    template.AppendLine();
                    template.AppendLine("-- Add your schema changes here");
                    template.AppendLine("-- Example: CREATE TABLE, ALTER TABLE, etc.");
                    template.AppendLine();
                    template.AppendLine("COMMIT TRANSACTION;");
                    template.AppendLine();
                    template.AppendLine("-- DOWN: Rollback schema changes");
                    template.AppendLine("-- BEGIN TRANSACTION;");
                    template.AppendLine("-- Add your rollback script here");
                    template.AppendLine("-- COMMIT TRANSACTION;");
                    break;

                case MigrationType.Data:
                    template.AppendLine("-- UP: Data changes");
                    template.AppendLine("BEGIN TRANSACTION;");
                    template.AppendLine();
                    template.AppendLine("-- Add your data changes here");
                    template.AppendLine("-- Example: INSERT, UPDATE, DELETE");
                    template.AppendLine();
                    template.AppendLine("COMMIT TRANSACTION;");
                    break;

                case MigrationType.Seed:
                    template.AppendLine("-- UP: Seed data");
                    template.AppendLine("BEGIN TRANSACTION;");
                    template.AppendLine();
                    template.AppendLine("-- Add your seed data here");
                    template.AppendLine("-- Example: INSERT INTO table VALUES (...)");
                    template.AppendLine();
                    template.AppendLine("COMMIT TRANSACTION;");
                    break;
            }

            return template.ToString();
        }
    }

    internal class MigrationRollbackManager;
    {
        private readonly ILogger _logger;
        private readonly MigrationConfig _config;

        public MigrationRollbackManager(ILogger logger, MigrationConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public async Task<RollbackResult> RollbackAsync(
            ConnectionManager connectionManager,
            Dictionary<string, DatabaseMigration> migrations,
            Dictionary<string, MigrationHistory> history,
            string targetVersion,
            CancellationToken cancellationToken)
        {
            var result = new RollbackResult;
            {
                PreviousVersion = history.Values.OrderByDescending(h => h.AppliedAt).FirstOrDefault()?.Version ?? "0.0.0",
                StartTime = DateTime.UtcNow;
            };

            var migrationsToRollback = GetMigrationsToRollback(migrations, history, targetVersion).ToList();

            using (var connection = await connectionManager.GetConnectionAsync())
            {
                var transaction = await connection.BeginTransactionAsync();

                try
                {
                    for (int i = migrationsToRollback.Count - 1; i >= 0; i--)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            await transaction.RollbackAsync();
                            throw new OperationCanceledException("Rollback cancelled");
                        }

                        var migration = migrationsToRollback[i];
                        await RollbackMigrationAsync(connection, transaction, migration);

                        result.RolledBackMigrations.Add(migration);
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            result.NewVersion = targetVersion;
            result.Success = true;

            return result;
        }

        private IEnumerable<DatabaseMigration> GetMigrationsToRollback(
            Dictionary<string, DatabaseMigration> migrations,
            Dictionary<string, MigrationHistory> history,
            string targetVersion)
        {
            var appliedMigrations = history.Values;
                .Where(h => h.Success)
                .OrderByDescending(h => h.AppliedAt)
                .ToList();

            foreach (var historyEntry in appliedMigrations)
            {
                if (VersionComparer.Compare(historyEntry.Version, targetVersion) <= 0)
                    break;

                if (migrations.TryGetValue(historyEntry.MigrationId, out var migration))
                {
                    yield return migration;
                }
            }
        }

        private async Task RollbackMigrationAsync(DatabaseConnection connection, DbTransaction transaction, DatabaseMigration migration)
        {
            if (string.IsNullOrWhiteSpace(migration.DownScript))
            {
                _logger.Warning($"No down script for migration {migration.Version}");
                return;
            }

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migration.DownScript;
            command.CommandTimeout = _config.MigrationTimeoutSeconds;

            await command.ExecuteNonQueryAsync();

            _logger.Info($"Migration rolled back: {migration.Version}");
        }
    }

    internal class MigrationBackupService;
    {
        private readonly ILogger _logger;
        private readonly MigrationConfig _config;

        public MigrationBackupService(ILogger logger, MigrationConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public async Task<BackupResult> CreateBackupAsync(ConnectionManager connectionManager)
        {
            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var backupFileName = $"Backup_{timestamp}.bak";
                var backupPath = Path.Combine(_config.BackupPath, backupFileName);

                // Backup klasörünü oluştur;
                Directory.CreateDirectory(_config.BackupPath);

                using (var connection = await connectionManager.GetConnectionAsync())
                {
                    var command = connection.CreateCommand();

                    // Database provider'a göre backup komutu;
                    switch (_config.DatabaseProvider)
                    {
                        case DatabaseProvider.SqlServer:
                            command.CommandText = $"BACKUP DATABASE [{connection.Database}] TO DISK = '{backupPath}'";
                            break;
                        case DatabaseProvider.PostgreSQL:
                            command.CommandText = $"pg_dump -Fc {connection.Database} > {backupPath}";
                            break;
                        default:
                            throw new NotSupportedException($"Backup not supported for provider: {_config.DatabaseProvider}");
                    }

                    await command.ExecuteNonQueryAsync();
                }

                // Eski backup'ları temizle;
                CleanupOldBackups();

                var fileInfo = new FileInfo(backupPath);

                return new BackupResult;
                {
                    BackupFileName = backupFileName,
                    BackupSize = fileInfo.Length,
                    BackupTime = DateTime.UtcNow,
                    Success = true;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Backup creation failed: {ex.Message}", ex);
                return new BackupResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message;
                };
            }
        }

        private void CleanupOldBackups()
        {
            try
            {
                var backupFiles = Directory.GetFiles(_config.BackupPath, "*.bak")
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

    internal static class VersionComparer;
    {
        public static int Compare(string version1, string version2)
        {
            if (version1 == version2) return 0;

            var v1Parts = version1.Split('.');
            var v2Parts = version2.Split('.');

            for (int i = 0; i < Math.Max(v1Parts.Length, v2Parts.Length); i++)
            {
                var v1Part = i < v1Parts.Length ? int.Parse(v1Parts[i]) : 0;
                var v2Part = i < v2Parts.Length ? int.Parse(v2Parts[i]) : 0;

                if (v1Part != v2Part)
                    return v1Part.CompareTo(v2Part);
            }

            return 0;
        }
    }

    // Extension methods;
    public static class DbCommandExtensions;
    {
        public static void AddParameter(this DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
    #endregion;
}
