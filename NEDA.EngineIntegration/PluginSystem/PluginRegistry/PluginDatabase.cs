using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration.AppSettings;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Dapper;
using System.Data;

namespace NEDA.EngineIntegration.PluginSystem.PluginRegistry
{
    /// <summary>
    /// Plugin veritabanı yönetim sınıfı;
    /// Plugin metadata, bağımlılıklar, sürüm bilgileri ve durum takibi;
    /// </summary>
    public class PluginDatabase : IPluginDatabase;
    {
        private readonly ILogger _logger;
        private readonly AppConfig _config;
        private readonly string _connectionString;
        private readonly string _databasePath;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _isInitialized = false;
        private readonly object _lock = new object();

        /// <summary>
        /// PluginDatabase constructor;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="config">Konfigürasyon</param>
        public PluginDatabase(ILogger logger, AppConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Database path configuration;
            var pluginDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NEDA",
                "Plugins"
            );

            Directory.CreateDirectory(pluginDirectory);
            _databasePath = Path.Combine(pluginDirectory, "plugins.db");
            _connectionString = $"Data Source={_databasePath};Cache=Shared";

            // JSON serialization options;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };

            _logger.LogInformation($"PluginDatabase initialized. Database path: {_databasePath}");
        }

        /// <summary>
        /// Database'i başlat ve tabloları oluştur;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            lock (_lock)
            {
                if (_isInitialized) return;

                try
                {
                    CreateDatabaseTables();
                    _isInitialized = true;
                    _logger.LogInformation("Plugin database initialized successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to initialize plugin database: {ex.Message}", ex);
                    throw new PluginDatabaseException("Database initialization failed", ex);
                }
            }
        }

        private void CreateDatabaseTables()
        {
            using var connection = CreateConnection();
            connection.Open();

            // Plugins tablosu;
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS Plugins (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT,
                    Version TEXT NOT NULL,
                    Author TEXT,
                    Company TEXT,
                    Copyright TEXT,
                    PluginType INTEGER NOT NULL,
                    Status INTEGER NOT NULL,
                    IsEnabled INTEGER DEFAULT 0,
                    IsSystemPlugin INTEGER DEFAULT 0,
                    InstallationDate DATETIME DEFAULT CURRENT_TIMESTAMP,
                    LastUpdated DATETIME DEFAULT CURRENT_TIMESTAMP,
                    AssemblyPath TEXT,
                    EntryPoint TEXT,
                    ConfigData TEXT,
                    Metadata TEXT;
                )");

            // Dependencies tablosu;
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS PluginDependencies (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PluginId TEXT NOT NULL,
                    DependencyId TEXT NOT NULL,
                    RequiredVersion TEXT,
                    IsOptional INTEGER DEFAULT 0,
                    FOREIGN KEY (PluginId) REFERENCES Plugins(Id) ON DELETE CASCADE,
                    UNIQUE(PluginId, DependencyId)
                )");

            // Compatibility tablosu;
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS PluginCompatibility (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PluginId TEXT NOT NULL,
                    NedaVersion TEXT NOT NULL,
                    MinVersion TEXT,
                    MaxVersion TEXT,
                    TestedVersion TEXT,
                    CompatibilityLevel INTEGER NOT NULL,
                    Notes TEXT,
                    FOREIGN KEY (PluginId) REFERENCES Plugins(Id) ON DELETE CASCADE;
                )");

            // Resources tablosu;
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS PluginResources (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PluginId TEXT NOT NULL,
                    ResourceType INTEGER NOT NULL,
                    ResourcePath TEXT NOT NULL,
                    ResourceHash TEXT,
                    SizeInBytes INTEGER,
                    LastModified DATETIME,
                    Metadata TEXT,
                    FOREIGN KEY (PluginId) REFERENCES Plugins(Id) ON DELETE CASCADE;
                )");

            // Settings tablosu;
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS PluginSettings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PluginId TEXT NOT NULL,
                    SettingKey TEXT NOT NULL,
                    SettingValue TEXT,
                    ValueType TEXT DEFAULT 'string',
                    IsEncrypted INTEGER DEFAULT 0,
                    Category TEXT,
                    Description TEXT,
                    LastModified DATETIME DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (PluginId) REFERENCES Plugins(Id) ON DELETE CASCADE,
                    UNIQUE(PluginId, SettingKey)
                )");

            // Statistics tablosu;
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS PluginStatistics (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PluginId TEXT NOT NULL,
                    LoadCount INTEGER DEFAULT 0,
                    LastLoadTime DATETIME,
                    TotalExecutionTimeMs INTEGER DEFAULT 0,
                    AverageLoadTimeMs INTEGER DEFAULT 0,
                    ErrorCount INTEGER DEFAULT 0,
                    LastError TEXT,
                    LastErrorTime DATETIME,
                    PerformanceScore REAL DEFAULT 0.0,
                    FOREIGN KEY (PluginId) REFERENCES Plugins(Id) ON DELETE CASCADE;
                )");

            // Indexes;
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_plugins_status ON Plugins(Status)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_plugins_enabled ON Plugins(IsEnabled)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_dependencies_plugin ON PluginDependencies(PluginId)");
            connection.Execute("CREATE INDEX IF NOT EXISTS idx_settings_plugin ON PluginSettings(PluginId)");

            _logger.LogDebug("Plugin database tables created successfully.");
        }

        /// <summary>
        /// Plugin kaydı ekle;
        /// </summary>
        public async Task<PluginRecord> AddPluginAsync(PluginInfo pluginInfo)
        {
            ValidateDatabaseInitialized();

            try
            {
                var record = new PluginRecord(pluginInfo);

                using var connection = CreateConnection();
                await connection.OpenAsync();

                using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    // Insert plugin record;
                    await connection.ExecuteAsync(@"
                        INSERT INTO Plugins; 
                        (Id, Name, Description, Version, Author, Company, Copyright, 
                         PluginType, Status, IsEnabled, IsSystemPlugin, AssemblyPath, 
                         EntryPoint, ConfigData, Metadata)
                        VALUES; 
                        (@Id, @Name, @Description, @Version, @Author, @Company, @Copyright,
                         @PluginType, @Status, @IsEnabled, @IsSystemPlugin, @AssemblyPath,
                         @EntryPoint, @ConfigData, @Metadata)",
                        record, transaction);

                    // Insert dependencies if any;
                    if (pluginInfo.Dependencies?.Any() == true)
                    {
                        foreach (var dependency in pluginInfo.Dependencies)
                        {
                            await connection.ExecuteAsync(@"
                                INSERT INTO PluginDependencies; 
                                (PluginId, DependencyId, RequiredVersion, IsOptional)
                                VALUES; 
                                (@PluginId, @DependencyId, @RequiredVersion, @IsOptional)",
                                new;
                                {
                                    PluginId = pluginInfo.Id,
                                    DependencyId = dependency.PluginId,
                                    RequiredVersion = dependency.RequiredVersion,
                                    IsOptional = dependency.IsOptional;
                                }, transaction);
                        }
                    }

                    // Insert compatibility info;
                    if (pluginInfo.Compatibility != null)
                    {
                        await connection.ExecuteAsync(@"
                            INSERT INTO PluginCompatibility; 
                            (PluginId, NedaVersion, MinVersion, MaxVersion, 
                             TestedVersion, CompatibilityLevel, Notes)
                            VALUES; 
                            (@PluginId, @NedaVersion, @MinVersion, @MaxVersion,
                             @TestedVersion, @CompatibilityLevel, @Notes)",
                            new;
                            {
                                PluginId = pluginInfo.Id,
                                pluginInfo.Compatibility.NedaVersion,
                                pluginInfo.Compatibility.MinVersion,
                                pluginInfo.Compatibility.MaxVersion,
                                pluginInfo.Compatibility.TestedVersion,
                                pluginInfo.Compatibility.CompatibilityLevel,
                                pluginInfo.Compatibility.Notes;
                            }, transaction);
                    }

                    // Create statistics entry
                    await connection.ExecuteAsync(@"
                        INSERT INTO PluginStatistics; 
                        (PluginId, LoadCount, PerformanceScore)
                        VALUES; 
                        (@PluginId, 0, 100.0)",
                        new { PluginId = pluginInfo.Id }, transaction);

                    await transaction.CommitAsync();

                    _logger.LogInformation($"Plugin '{pluginInfo.Name}' (v{pluginInfo.Version}) added to database.");
                    return record;
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError($"Failed to add plugin '{pluginInfo.Name}': {ex.Message}", ex);
                    throw new PluginDatabaseException("Failed to add plugin", ex);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in AddPluginAsync: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Plugin güncelle;
        /// </summary>
        public async Task UpdatePluginAsync(PluginInfo pluginInfo)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    // Update plugin record;
                    var updated = await connection.ExecuteAsync(@"
                        UPDATE Plugins SET;
                            Name = @Name,
                            Description = @Description,
                            Version = @Version,
                            Author = @Author,
                            Company = @Company,
                            Copyright = @Copyright,
                            PluginType = @PluginType,
                            Status = @Status,
                            AssemblyPath = @AssemblyPath,
                            EntryPoint = @EntryPoint,
                            ConfigData = @ConfigData,
                            Metadata = @Metadata,
                            LastUpdated = CURRENT_TIMESTAMP;
                        WHERE Id = @Id",
                        new;
                        {
                            pluginInfo.Id,
                            pluginInfo.Name,
                            pluginInfo.Description,
                            pluginInfo.Version,
                            pluginInfo.Author,
                            pluginInfo.Company,
                            pluginInfo.Copyright,
                            PluginType = (int)pluginInfo.PluginType,
                            Status = (int)pluginInfo.Status,
                            pluginInfo.AssemblyPath,
                            pluginInfo.EntryPoint,
                            ConfigData = JsonSerializer.Serialize(pluginInfo.ConfigData, _jsonOptions),
                            Metadata = JsonSerializer.Serialize(pluginInfo.Metadata, _jsonOptions)
                        }, transaction);

                    if (updated == 0)
                    {
                        throw new PluginNotFoundException($"Plugin with ID '{pluginInfo.Id}' not found.");
                    }

                    // Update dependencies;
                    await connection.ExecuteAsync(
                        "DELETE FROM PluginDependencies WHERE PluginId = @PluginId",
                        new { PluginId = pluginInfo.Id }, transaction);

                    if (pluginInfo.Dependencies?.Any() == true)
                    {
                        foreach (var dependency in pluginInfo.Dependencies)
                        {
                            await connection.ExecuteAsync(@"
                                INSERT INTO PluginDependencies; 
                                (PluginId, DependencyId, RequiredVersion, IsOptional)
                                VALUES; 
                                (@PluginId, @DependencyId, @RequiredVersion, @IsOptional)",
                                new;
                                {
                                    PluginId = pluginInfo.Id,
                                    DependencyId = dependency.PluginId,
                                    RequiredVersion = dependency.RequiredVersion,
                                    IsOptional = dependency.IsOptional;
                                }, transaction);
                        }
                    }

                    await transaction.CommitAsync();
                    _logger.LogInformation($"Plugin '{pluginInfo.Name}' updated in database.");
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError($"Failed to update plugin '{pluginInfo.Name}': {ex.Message}", ex);
                    throw new PluginDatabaseException("Failed to update plugin", ex);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in UpdatePluginAsync: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Plugin sil;
        /// </summary>
        public async Task<bool> RemovePluginAsync(string pluginId)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                // Get plugin info before deletion for logging;
                var plugin = await connection.QueryFirstOrDefaultAsync<PluginRecord>(
                    "SELECT * FROM Plugins WHERE Id = @PluginId",
                    new { PluginId = pluginId });

                if (plugin == null)
                {
                    _logger.LogWarning($"Plugin with ID '{pluginId}' not found for removal.");
                    return false;
                }

                // Check if plugin has dependent plugins;
                var dependentCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM PluginDependencies WHERE DependencyId = @PluginId",
                    new { PluginId = pluginId });

                if (dependentCount > 0)
                {
                    var dependentPlugins = await connection.QueryAsync<string>(
                        "SELECT DISTINCT p.Name FROM Plugins p " +
                        "JOIN PluginDependencies d ON p.Id = d.PluginId " +
                        "WHERE d.DependencyId = @PluginId",
                        new { PluginId = pluginId });

                    throw new PluginDependencyException(
                        $"Cannot remove plugin '{plugin.Name}' because it is required by: " +
                        $"{string.Join(", ", dependentPlugins)}");
                }

                // Delete plugin (cascade will handle related records)
                var deleted = await connection.ExecuteAsync(
                    "DELETE FROM Plugins WHERE Id = @PluginId",
                    new { PluginId = pluginId });

                if (deleted > 0)
                {
                    _logger.LogInformation($"Plugin '{plugin.Name}' removed from database.");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing plugin '{pluginId}': {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Plugin ID ile getir;
        /// </summary>
        public async Task<PluginRecord> GetPluginByIdAsync(string pluginId)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                var plugin = await connection.QueryFirstOrDefaultAsync<PluginRecord>(
                    "SELECT * FROM Plugins WHERE Id = @PluginId",
                    new { PluginId = pluginId });

                if (plugin == null)
                {
                    throw new PluginNotFoundException($"Plugin with ID '{pluginId}' not found.");
                }

                // Load dependencies;
                plugin.Dependencies = (await connection.QueryAsync<PluginDependencyRecord>(
                    "SELECT * FROM PluginDependencies WHERE PluginId = @PluginId",
                    new { PluginId = pluginId })).ToList();

                // Load compatibility;
                plugin.Compatibility = await connection.QueryFirstOrDefaultAsync<PluginCompatibilityRecord>(
                    "SELECT * FROM PluginCompatibility WHERE PluginId = @PluginId",
                    new { PluginId = pluginId });

                // Load statistics;
                plugin.Statistics = await connection.QueryFirstOrDefaultAsync<PluginStatisticsRecord>(
                    "SELECT * FROM PluginStatistics WHERE PluginId = @PluginId",
                    new { PluginId = pluginId });

                return plugin;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting plugin '{pluginId}': {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Tüm pluginleri getir;
        /// </summary>
        public async Task<IEnumerable<PluginRecord>> GetAllPluginsAsync(bool includeDisabled = false)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                var query = "SELECT * FROM Plugins";
                if (!includeDisabled)
                {
                    query += " WHERE IsEnabled = 1";
                }

                var plugins = await connection.QueryAsync<PluginRecord>(query);

                // Load additional data for each plugin;
                foreach (var plugin in plugins)
                {
                    plugin.Dependencies = (await connection.QueryAsync<PluginDependencyRecord>(
                        "SELECT * FROM PluginDependencies WHERE PluginId = @PluginId",
                        new { PluginId = plugin.Id })).ToList();

                    plugin.Statistics = await connection.QueryFirstOrDefaultAsync<PluginStatisticsRecord>(
                        "SELECT * FROM PluginStatistics WHERE PluginId = @PluginId",
                        new { PluginId = plugin.Id });
                }

                return plugins;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting all plugins: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Plugin durumunu güncelle;
        /// </summary>
        public async Task UpdatePluginStatusAsync(string pluginId, PluginStatus status, bool isEnabled)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                var updated = await connection.ExecuteAsync(@"
                    UPDATE Plugins SET; 
                        Status = @Status, 
                        IsEnabled = @IsEnabled,
                        LastUpdated = CURRENT_TIMESTAMP;
                    WHERE Id = @PluginId",
                    new;
                    {
                        PluginId = pluginId,
                        Status = (int)status,
                        IsEnabled = isEnabled ? 1 : 0;
                    });

                if (updated == 0)
                {
                    throw new PluginNotFoundException($"Plugin with ID '{pluginId}' not found.");
                }

                _logger.LogInformation($"Plugin '{pluginId}' status updated to {status}, Enabled: {isEnabled}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating plugin status '{pluginId}': {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Plugin ayarını kaydet;
        /// </summary>
        public async Task SavePluginSettingAsync(string pluginId, PluginSetting setting)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                // Check if setting exists;
                var existing = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM PluginSettings WHERE PluginId = @PluginId AND SettingKey = @Key",
                    new { PluginId = pluginId, setting.Key });

                if (existing > 0)
                {
                    // Update;
                    await connection.ExecuteAsync(@"
                        UPDATE PluginSettings SET;
                            SettingValue = @Value,
                            ValueType = @ValueType,
                            IsEncrypted = @IsEncrypted,
                            Category = @Category,
                            Description = @Description,
                            LastModified = CURRENT_TIMESTAMP;
                        WHERE PluginId = @PluginId AND SettingKey = @Key",
                        new;
                        {
                            PluginId = pluginId,
                            setting.Key,
                            setting.Value,
                            ValueType = setting.ValueType.ToString().ToLower(),
                            IsEncrypted = setting.IsEncrypted ? 1 : 0,
                            setting.Category,
                            setting.Description;
                        });
                }
                else;
                {
                    // Insert;
                    await connection.ExecuteAsync(@"
                        INSERT INTO PluginSettings; 
                        (PluginId, SettingKey, SettingValue, ValueType, IsEncrypted, Category, Description)
                        VALUES; 
                        (@PluginId, @Key, @Value, @ValueType, @IsEncrypted, @Category, @Description)",
                        new;
                        {
                            PluginId = pluginId,
                            setting.Key,
                            setting.Value,
                            ValueType = setting.ValueType.ToString().ToLower(),
                            IsEncrypted = setting.IsEncrypted ? 1 : 0,
                            setting.Category,
                            setting.Description;
                        });
                }

                _logger.LogDebug($"Setting '{setting.Key}' saved for plugin '{pluginId}'");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving setting for plugin '{pluginId}': {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Plugin ayarını getir;
        /// </summary>
        public async Task<PluginSetting> GetPluginSettingAsync(string pluginId, string key)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                var record = await connection.QueryFirstOrDefaultAsync<PluginSettingRecord>(
                    "SELECT * FROM PluginSettings WHERE PluginId = @PluginId AND SettingKey = @Key",
                    new { PluginId = pluginId, Key = key });

                if (record == null)
                {
                    return null;
                }

                return new PluginSetting;
                {
                    Key = record.SettingKey,
                    Value = record.SettingValue,
                    ValueType = Enum.Parse<SettingValueType>(record.ValueType, true),
                    IsEncrypted = record.IsEncrypted == 1,
                    Category = record.Category,
                    Description = record.Description;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting setting '{key}' for plugin '{pluginId}': {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Plugin istatistiğini güncelle;
        /// </summary>
        public async Task UpdatePluginStatisticsAsync(string pluginId, Action<PluginStatisticsRecord> updateAction)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                var statistics = await connection.QueryFirstOrDefaultAsync<PluginStatisticsRecord>(
                    "SELECT * FROM PluginStatistics WHERE PluginId = @PluginId",
                    new { PluginId = pluginId });

                if (statistics == null)
                {
                    // Create new statistics record;
                    statistics = new PluginStatisticsRecord;
                    {
                        PluginId = pluginId,
                        LoadCount = 0,
                        TotalExecutionTimeMs = 0,
                        AverageLoadTimeMs = 0,
                        ErrorCount = 0,
                        PerformanceScore = 100.0;
                    };

                    await connection.ExecuteAsync(@"
                        INSERT INTO PluginStatistics; 
                        (PluginId, LoadCount, TotalExecutionTimeMs, AverageLoadTimeMs, ErrorCount, PerformanceScore)
                        VALUES; 
                        (@PluginId, @LoadCount, @TotalExecutionTimeMs, @AverageLoadTimeMs, @ErrorCount, @PerformanceScore)",
                        statistics);
                }

                updateAction?.Invoke(statistics);

                // Recalculate average if needed;
                if (statistics.LoadCount > 0 && statistics.TotalExecutionTimeMs > 0)
                {
                    statistics.AverageLoadTimeMs = statistics.TotalExecutionTimeMs / statistics.LoadCount;
                }

                // Update in database;
                await connection.ExecuteAsync(@"
                    UPDATE PluginStatistics SET;
                        LoadCount = @LoadCount,
                        LastLoadTime = @LastLoadTime,
                        TotalExecutionTimeMs = @TotalExecutionTimeMs,
                        AverageLoadTimeMs = @AverageLoadTimeMs,
                        ErrorCount = @ErrorCount,
                        LastError = @LastError,
                        LastErrorTime = @LastErrorTime,
                        PerformanceScore = @PerformanceScore;
                    WHERE PluginId = @PluginId",
                    statistics);

                _logger.LogDebug($"Statistics updated for plugin '{pluginId}'");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error updating statistics for plugin '{pluginId}': {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Bağımlılıkları kontrol et;
        /// </summary>
        public async Task<IEnumerable<DependencyCheckResult>> CheckDependenciesAsync(string pluginId)
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                var dependencies = await connection.QueryAsync<PluginDependencyRecord>(
                    "SELECT * FROM PluginDependencies WHERE PluginId = @PluginId",
                    new { PluginId = pluginId });

                var results = new List<DependencyCheckResult>();

                foreach (var dependency in dependencies)
                {
                    var dependentPlugin = await connection.QueryFirstOrDefaultAsync<PluginRecord>(
                        "SELECT * FROM Plugins WHERE Id = @DependencyId AND IsEnabled = 1",
                        new { DependencyId = dependency.DependencyId });

                    var result = new DependencyCheckResult;
                    {
                        DependencyId = dependency.DependencyId,
                        RequiredVersion = dependency.RequiredVersion,
                        IsOptional = dependency.IsOptional == 1,
                        IsAvailable = dependentPlugin != null,
                        AvailableVersion = dependentPlugin?.Version,
                        IsCompatible = CheckVersionCompatibility(dependency.RequiredVersion, dependentPlugin?.Version)
                    };

                    results.Add(result);
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking dependencies for plugin '{pluginId}': {ex.Message}", ex);
                throw;
            }
        }

        private bool CheckVersionCompatibility(string requiredVersion, string availableVersion)
        {
            if (string.IsNullOrEmpty(requiredVersion) || string.IsNullOrEmpty(availableVersion))
                return true; // No version requirement;

            try
            {
                var required = new Version(requiredVersion);
                var available = new Version(availableVersion);

                // Check if available version meets minimum requirement;
                return available >= required;
            }
            catch (Exception)
            {
                // If version parsing fails, do simple string comparison;
                return string.Equals(requiredVersion, availableVersion, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Database bağlantısı oluştur;
        /// </summary>
        private IDbConnection CreateConnection()
        {
            return new SqliteConnection(_connectionString);
        }

        /// <summary>
        /// Database'in başlatıldığını doğrula;
        /// </summary>
        private void ValidateDatabaseInitialized()
        {
            if (!_isInitialized)
            {
                throw new PluginDatabaseException("Plugin database is not initialized. Call InitializeAsync first.");
            }
        }

        /// <summary>
        /// Database'i yedekle;
        /// </summary>
        public async Task BackupDatabaseAsync(string backupPath)
        {
            ValidateDatabaseInitialized();

            try
            {
                if (File.Exists(_databasePath))
                {
                    var backupFile = Path.Combine(backupPath, $"plugins_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db");
                    File.Copy(_databasePath, backupFile, true);

                    _logger.LogInformation($"Plugin database backed up to: {backupFile}");
                }
                else;
                {
                    _logger.LogWarning("Database file not found for backup.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to backup database: {ex.Message}", ex);
                throw new PluginDatabaseException("Database backup failed", ex);
            }
        }

        /// <summary>
        /// Database'i temizle (sadece dev/test ortamında)
        /// </summary>
        public async Task CleanDatabaseAsync()
        {
            if (_config.Environment != "Development")
            {
                throw new InvalidOperationException("Database cleanup is only allowed in development environment.");
            }

            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                await connection.ExecuteAsync("DELETE FROM PluginDependencies");
                await connection.ExecuteAsync("DELETE FROM PluginCompatibility");
                await connection.ExecuteAsync("DELETE FROM PluginResources");
                await connection.ExecuteAsync("DELETE FROM PluginSettings");
                await connection.ExecuteAsync("DELETE FROM PluginStatistics");
                await connection.ExecuteAsync("DELETE FROM Plugins");

                await connection.ExecuteAsync("VACUUM");

                _logger.LogInformation("Plugin database cleaned successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to clean database: {ex.Message}", ex);
                throw new PluginDatabaseException("Database cleanup failed", ex);
            }
        }

        /// <summary>
        /// Database istatistiklerini getir;
        /// </summary>
        public async Task<DatabaseStatistics> GetDatabaseStatisticsAsync()
        {
            ValidateDatabaseInitialized();

            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync();

                var stats = new DatabaseStatistics;
                {
                    TotalPlugins = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Plugins"),
                    EnabledPlugins = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Plugins WHERE IsEnabled = 1"),
                    SystemPlugins = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Plugins WHERE IsSystemPlugin = 1"),
                    ActivePlugins = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Plugins WHERE Status = 1"), // Active status;
                    TotalDependencies = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PluginDependencies"),
                    DatabaseSize = new FileInfo(_databasePath).Length,
                    LastBackup = GetLastBackupTime()
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting database statistics: {ex.Message}", ex);
                throw;
            }
        }

        private DateTime? GetLastBackupTime()
        {
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NEDA",
                "Backups"
            );

            if (!Directory.Exists(backupDir))
                return null;

            var backupFiles = Directory.GetFiles(backupDir, "plugins_backup_*.db");
            if (backupFiles.Length == 0)
                return null;

            var lastBackup = backupFiles;
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();

            return lastBackup?.LastWriteTime;
        }
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Plugin database interface;
    /// </summary>
    public interface IPluginDatabase;
    {
        Task InitializeAsync();
        Task<PluginRecord> AddPluginAsync(PluginInfo pluginInfo);
        Task UpdatePluginAsync(PluginInfo pluginInfo);
        Task<bool> RemovePluginAsync(string pluginId);
        Task<PluginRecord> GetPluginByIdAsync(string pluginId);
        Task<IEnumerable<PluginRecord>> GetAllPluginsAsync(bool includeDisabled = false);
        Task UpdatePluginStatusAsync(string pluginId, PluginStatus status, bool isEnabled);
        Task SavePluginSettingAsync(string pluginId, PluginSetting setting);
        Task<PluginSetting> GetPluginSettingAsync(string pluginId, string key);
        Task UpdatePluginStatisticsAsync(string pluginId, Action<PluginStatisticsRecord> updateAction);
        Task<IEnumerable<DependencyCheckResult>> CheckDependenciesAsync(string pluginId);
        Task BackupDatabaseAsync(string backupPath);
        Task CleanDatabaseAsync();
        Task<DatabaseStatistics> GetDatabaseStatisticsAsync();
    }

    /// <summary>
    /// Plugin kayıt modeli;
    /// </summary>
    public class PluginRecord;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Company { get; set; }
        public string Copyright { get; set; }
        public PluginType PluginType { get; set; }
        public PluginStatus Status { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsSystemPlugin { get; set; }
        public DateTime InstallationDate { get; set; }
        public DateTime LastUpdated { get; set; }
        public string AssemblyPath { get; set; }
        public string EntryPoint { get; set; }
        public string ConfigData { get; set; }
        public string Metadata { get; set; }

        // Navigation properties (loaded separately)
        public List<PluginDependencyRecord> Dependencies { get; set; }
        public PluginCompatibilityRecord Compatibility { get; set; }
        public PluginStatisticsRecord Statistics { get; set; }

        public PluginRecord() { }

        public PluginRecord(PluginInfo pluginInfo)
        {
            Id = pluginInfo.Id;
            Name = pluginInfo.Name;
            Description = pluginInfo.Description;
            Version = pluginInfo.Version;
            Author = pluginInfo.Author;
            Company = pluginInfo.Company;
            Copyright = pluginInfo.Copyright;
            PluginType = pluginInfo.PluginType;
            Status = pluginInfo.Status;
            IsEnabled = pluginInfo.IsEnabled;
            IsSystemPlugin = pluginInfo.IsSystemPlugin;
            AssemblyPath = pluginInfo.AssemblyPath;
            EntryPoint = pluginInfo.EntryPoint;

            // Serialize complex objects;
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            ConfigData = JsonSerializer.Serialize(pluginInfo.ConfigData, jsonOptions);
            Metadata = JsonSerializer.Serialize(pluginInfo.Metadata, jsonOptions);
        }
    }

    /// <summary>
    /// Bağımlılık kaydı;
    /// </summary>
    public class PluginDependencyRecord;
    {
        public int Id { get; set; }
        public string PluginId { get; set; }
        public string DependencyId { get; set; }
        public string RequiredVersion { get; set; }
        public int IsOptional { get; set; } // SQLite doesn't support bool directly;
    }

    /// <summary>
    /// Uyumluluk kaydı;
    /// </summary>
    public class PluginCompatibilityRecord;
    {
        public int Id { get; set; }
        public string PluginId { get; set; }
        public string NedaVersion { get; set; }
        public string MinVersion { get; set; }
        public string MaxVersion { get; set; }
        public string TestedVersion { get; set; }
        public CompatibilityLevel CompatibilityLevel { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// İstatistik kaydı;
    /// </summary>
    public class PluginStatisticsRecord;
    {
        public int Id { get; set; }
        public string PluginId { get; set; }
        public int LoadCount { get; set; }
        public DateTime? LastLoadTime { get; set; }
        public long TotalExecutionTimeMs { get; set; }
        public int AverageLoadTimeMs { get; set; }
        public int ErrorCount { get; set; }
        public string LastError { get; set; }
        public DateTime? LastErrorTime { get; set; }
        public double PerformanceScore { get; set; }
    }

    /// <summary>
    /// Plugin ayar kaydı;
    /// </summary>
    public class PluginSettingRecord;
    {
        public int Id { get; set; }
        public string PluginId { get; set; }
        public string SettingKey { get; set; }
        public string SettingValue { get; set; }
        public string ValueType { get; set; }
        public int IsEncrypted { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public DateTime LastModified { get; set; }
    }

    /// <summary>
    /// Database istatistikleri;
    /// </summary>
    public class DatabaseStatistics;
    {
        public int TotalPlugins { get; set; }
        public int EnabledPlugins { get; set; }
        public int SystemPlugins { get; set; }
        public int ActivePlugins { get; set; }
        public int TotalDependencies { get; set; }
        public long DatabaseSize { get; set; }
        public DateTime? LastBackup { get; set; }
    }

    /// <summary>
    /// Bağımlılık kontrol sonucu;
    /// </summary>
    public class DependencyCheckResult;
    {
        public string DependencyId { get; set; }
        public string RequiredVersion { get; set; }
        public bool IsOptional { get; set; }
        public bool IsAvailable { get; set; }
        public string AvailableVersion { get; set; }
        public bool IsCompatible { get; set; }
    }

    /// <summary>
    /// Plugin tipi enum;
    /// </summary>
    public enum PluginType;
    {
        Core = 0,
        Integration = 1,
        Tool = 2,
        Extension = 3,
        Theme = 4,
        LanguagePack = 5,
        Asset = 6,
        Custom = 99;
    }

    /// <summary>
    /// Plugin durum enum;
    /// </summary>
    public enum PluginStatus;
    {
        Installed = 0,
        Active = 1,
        Disabled = 2,
        Error = 3,
        Updating = 4,
        Uninstalling = 5;
    }

    /// <summary>
    /// Uyumluluk seviyesi;
    /// </summary>
    public enum CompatibilityLevel;
    {
        Unknown = 0,
        Incompatible = 1,
        Limited = 2,
        Compatible = 3,
        FullyCompatible = 4,
        Certified = 5;
    }

    /// <summary>
    /// Ayar değer tipi;
    /// </summary>
    public enum SettingValueType;
    {
        String,
        Integer,
        Boolean,
        Double,
        DateTime,
        Json;
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Plugin database exception;
    /// </summary>
    public class PluginDatabaseException : Exception
    {
        public PluginDatabaseException() { }
        public PluginDatabaseException(string message) : base(message) { }
        public PluginDatabaseException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin not found exception;
    /// </summary>
    public class PluginNotFoundException : Exception
    {
        public PluginNotFoundException() { }
        public PluginNotFoundException(string message) : base(message) { }
        public PluginNotFoundException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin dependency exception;
    /// </summary>
    public class PluginDependencyException : Exception
    {
        public PluginDependencyException() { }
        public PluginDependencyException(string message) : base(message) { }
        public PluginDependencyException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
