using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.Configuration.AppSettings
{
    /// <summary>
    /// Setting change event arguments;
    /// </summary>
    public class SettingChangedEventArgs : EventArgs
    {
        public string SettingKey { get; }
        public object OldValue { get; }
        public object NewValue { get; }
        public string Category { get; }
        public DateTime ChangeTime { get; }

        public SettingChangedEventArgs(string settingKey, object oldValue, object newValue, string category)
        {
            SettingKey = settingKey ?? throw new ArgumentNullException(nameof(settingKey));
            OldValue = oldValue;
            NewValue = newValue;
            Category = category;
            ChangeTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Setting validation result;
    /// </summary>
    public class SettingValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public string SettingKey { get; set; }
        public object Value { get; set; }

        public static SettingValidationResult Success(string settingKey, object value)
        {
            return new SettingValidationResult
            {
                IsValid = true,
                SettingKey = settingKey,
                Value = value
            };
        }

        public static SettingValidationResult Error(string settingKey, object value, string errorMessage)
        {
            return new SettingValidationResult
            {
                IsValid = false,
                SettingKey = settingKey,
                Value = value,
                ErrorMessage = errorMessage
            };
        }
    }

    /// <summary>
    /// Setting metadata;
    /// </summary>
    public class SettingMetadata
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public Type ValueType { get; set; }
        public object DefaultValue { get; set; }
        public bool IsRequired { get; set; }
        public bool IsEncrypted { get; set; }
        public bool IsReadOnly { get; set; }
        public string[] AllowedValues { get; set; }
        public string ValidationRegex { get; set; }
        public int MinLength { get; set; } = -1;
        public int MaxLength { get; set; } = -1;
        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public string LastModifiedBy { get; set; }

        public SettingMetadata()
        {
            AllowedValues = Array.Empty<string>();
            CreatedDate = DateTime.UtcNow;
            ModifiedDate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Settings cache item;
    /// </summary>
    internal class SettingsCacheItem
    {
        public object Value { get; set; }
        public SettingMetadata Metadata { get; set; }
        public DateTime LastAccessed { get; set; }
        public DateTime LastModified { get; set; }
        public int AccessCount { get; set; }

        public SettingsCacheItem(object value, SettingMetadata metadata)
        {
            Value = value;
            Metadata = metadata;
            LastAccessed = DateTime.UtcNow;
            LastModified = DateTime.UtcNow;
            AccessCount = 1;
        }

        public void UpdateAccess()
        {
            LastAccessed = DateTime.UtcNow;
            AccessCount++;
        }

        public void UpdateModified()
        {
            LastModified = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Manages application settings with advanced features;
    /// </summary>
    public class SettingsManager : ISettingsManager
    {
        private readonly ILogger<SettingsManager> _logger;
        private readonly IConfiguration _configuration;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        private readonly ConcurrentDictionary<string, SettingsCacheItem> _settingsCache;
        private readonly ConcurrentDictionary<string, SettingMetadata> _settingsMetadata;
        private readonly ConcurrentDictionary<string, List<string>> _categoryIndex;

        private readonly string _settingsFilePath;
        private readonly string _metadataFilePath;
        private readonly string _backupDirectory;

        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
        private readonly Timer _autoSaveTimer;
        private readonly Timer _cacheCleanupTimer;

        private bool _isInitialized;
        private bool _isDirty;
        private readonly object _initializationLock = new object();

        private const int AUTO_SAVE_INTERVAL_MS = 30000; // 30 seconds;
        private const int CACHE_CLEANUP_INTERVAL_MS = 600000; // 10 minutes;
        private const int MAX_CACHE_SIZE = 10000;
        private const int CACHE_ITEM_LIFETIME_MINUTES = 60;

        /// <summary>
        /// Event triggered when a setting changes;
        /// </summary>
        public event EventHandler<SettingChangedEventArgs> SettingChanged;

        /// <summary>
        /// Initializes a new instance of SettingsManager;
        /// </summary>
        public SettingsManager(
            ILogger<SettingsManager> logger,
            IConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration;

            _settingsCache = new ConcurrentDictionary<string, SettingsCacheItem>(StringComparer.OrdinalIgnoreCase);
            _settingsMetadata = new ConcurrentDictionary<string, SettingMetadata>(StringComparer.OrdinalIgnoreCase);
            _categoryIndex = new ConcurrentDictionary<string, List<string>>();

            // Configure JSON serialization;
            _jsonSerializerOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // Set up file paths;
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "NEDA", "Settings");
            Directory.CreateDirectory(appFolder);

            _settingsFilePath = Path.Combine(appFolder, "settings.json");
            _metadataFilePath = Path.Combine(appFolder, "metadata.json");
            _backupDirectory = Path.Combine(appFolder, "Backups");
            Directory.CreateDirectory(_backupDirectory);

            // Initialize timers;
            _autoSaveTimer = new Timer(_ => AutoSaveCallback(), null, Timeout.Infinite, Timeout.Infinite);
            _cacheCleanupTimer = new Timer(_ => CacheCleanupCallback(), null, Timeout.Infinite, Timeout.Infinite);

            _isInitialized = false;
            _isDirty = false;

            _logger.LogInformation("SettingsManager initialized");
        }

        /// <summary>
        /// Initializes the settings manager;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized) return;

            lock (_initializationLock)
            {
                if (_isInitialized) return;

                try
                {
                    _logger.LogInformation("Initializing SettingsManager...");

                    // Load settings from file;
                    LoadSettingsFromFileAsync(cancellationToken).GetAwaiter().GetResult();

                    // Load metadata from file;
                    LoadMetadataFromFileAsync(cancellationToken).GetAwaiter().GetResult();

                    // Merge with configuration if provided;
                    MergeConfigurationAsync(cancellationToken).GetAwaiter().GetResult();

                    // Start auto-save timer;
                    _autoSaveTimer.Change(AUTO_SAVE_INTERVAL_MS, AUTO_SAVE_INTERVAL_MS);

                    // Start cache cleanup timer;
                    _cacheCleanupTimer.Change(CACHE_CLEANUP_INTERVAL_MS, CACHE_CLEANUP_INTERVAL_MS);

                    _isInitialized = true;

                    _logger.LogInformation($"SettingsManager initialized with {_settingsCache.Count} settings");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize SettingsManager");
                    throw new SettingsManagerException("Failed to initialize SettingsManager", ex);
                }
            }
        }

        /// <summary>
        /// Gets a setting value;
        /// </summary>
        public async Task<T> GetSettingAsync<T>(string key, T defaultValue = default,
            string category = "General", CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Setting key cannot be null or empty", nameof(key));

            try
            {
                // Check cache first;
                if (_settingsCache.TryGetValue(key, out var cacheItem))
                {
                    cacheItem.UpdateAccess();

                    if (cacheItem.Value is T typedValue)
                    {
                        return typedValue;
                    }

                    // Try to convert value;
                    try
                    {
                        return ConvertValue<T>(cacheItem.Value);
                    }
                    catch
                    {
                        _logger.LogWarning($"Type mismatch for setting '{key}'. Expected {typeof(T).Name}, got {cacheItem.Value?.GetType().Name}");
                    }
                }

                // Check metadata for default value;
                if (_settingsMetadata.TryGetValue(key, out var metadata))
                {
                    if (metadata.DefaultValue != null && metadata.DefaultValue is T defaultTypedValue)
                    {
                        // Cache the default value;
                        await SetSettingAsync(key, defaultTypedValue, category, false, cancellationToken);
                        return defaultTypedValue;
                    }
                }

                // Return provided default value and cache it;
                await SetSettingAsync(key, defaultValue, category, false, cancellationToken);
                return defaultValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get setting '{key}'");
                throw new SettingsOperationException($"Failed to get setting '{key}'", ex);
            }
        }

        /// <summary>
        /// Sets a setting value;
        /// </summary>
        public async Task SetSettingAsync<T>(string key, T value, string category = "General",
            bool saveImmediately = false, CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Setting key cannot be null or empty", nameof(key));

            try
            {
                // Get old value for event;
                object oldValue = null;
                _settingsCache.TryGetValue(key, out var oldCacheItem);
                if (oldCacheItem != null)
                {
                    oldValue = oldCacheItem.Value;
                }

                // Validate setting;
                var validationResult = await ValidateSettingAsync(key, value, category, cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new SettingsValidationException($"Setting validation failed for '{key}': {validationResult.ErrorMessage}");
                }

                // Get or create metadata;
                var metadata = await GetOrCreateMetadataAsync(key, value, category, cancellationToken);
                metadata.ModifiedDate = DateTime.UtcNow;

                // Create or update cache item;
                var cacheItem = new SettingsCacheItem(value, metadata);

                // Update cache;
                _settingsCache[key] = cacheItem;
                _isDirty = true;

                // Update category index;
                UpdateCategoryIndex(key, category);

                // Save if requested;
                if (saveImmediately)
                {
                    await SaveSettingsToFileAsync(cancellationToken);
                }

                // Raise event;
                OnSettingChanged(key, oldValue, value, category);

                _logger.LogDebug($"Setting '{key}' updated to '{value}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set setting '{key}'");
                throw new SettingsOperationException($"Failed to set setting '{key}'", ex);
            }
        }

        /// <summary>
        /// Deletes a setting;
        /// </summary>
        public async Task<bool> DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Setting key cannot be null or empty", nameof(key));

            try
            {
                // Get old value for event;
                object oldValue = null;
                if (_settingsCache.TryRemove(key, out var oldCacheItem))
                {
                    oldValue = oldCacheItem.Value;

                    // Remove from category index;
                    if (oldCacheItem.Metadata != null && !string.IsNullOrEmpty(oldCacheItem.Metadata.Category))
                    {
                        RemoveFromCategoryIndex(key, oldCacheItem.Metadata.Category);
                    }

                    _isDirty = true;

                    // Raise event;
                    OnSettingChanged(key, oldValue, null, oldCacheItem.Metadata?.Category);

                    _logger.LogDebug($"Setting '{key}' deleted");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete setting '{key}'");
                throw new SettingsOperationException($"Failed to delete setting '{key}'", ex);
            }
        }

        /// <summary>
        /// Gets all settings in a category;
        /// </summary>
        public async Task<Dictionary<string, object>> GetSettingsByCategoryAsync(string category,
            CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category cannot be null or empty", nameof(category));

            try
            {
                var result = new Dictionary<string, object>();

                if (_categoryIndex.TryGetValue(category, out var settingKeys))
                {
                    foreach (var key in settingKeys)
                    {
                        if (_settingsCache.TryGetValue(key, out var cacheItem))
                        {
                            result[key] = cacheItem.Value;
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get settings for category '{category}'");
                throw new SettingsOperationException($"Failed to get settings for category '{category}'", ex);
            }
        }

        /// <summary>
        /// Gets all categories;
        /// </summary>
        public async Task<IEnumerable<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);
            return _categoryIndex.Keys.OrderBy(k => k);
        }

        /// <summary>
        /// Searches settings by key or description;
        /// </summary>
        public async Task<Dictionary<string, object>> SearchSettingsAsync(string searchTerm,
            CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return _settingsCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value);
            }

            var term = searchTerm.ToLowerInvariant();

            return _settingsCache
                .Where(kvp =>
                    kvp.Key.ToLowerInvariant().Contains(term) ||
                    (kvp.Value.Metadata?.Description?.ToLowerInvariant()?.Contains(term) ?? false) ||
                    (kvp.Value.Metadata?.DisplayName?.ToLowerInvariant()?.Contains(term) ?? false))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value);
        }

        /// <summary>
        /// Gets setting metadata;
        /// </summary>
        public async Task<SettingMetadata> GetSettingMetadataAsync(string key, CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Setting key cannot be null or empty", nameof(key));

            if (_settingsMetadata.TryGetValue(key, out var metadata))
            {
                return metadata;
            }

            // Try to create metadata from cache;
            if (_settingsCache.TryGetValue(key, out var cacheItem) && cacheItem.Metadata != null)
            {
                return cacheItem.Metadata;
            }

            return null;
        }

        /// <summary>
        /// Sets setting metadata;
        /// </summary>
        public async Task SetSettingMetadataAsync(string key, SettingMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Setting key cannot be null or empty", nameof(key));

            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            try
            {
                metadata.Key = key;
                metadata.ModifiedDate = DateTime.UtcNow;

                _settingsMetadata[key] = metadata;
                _isDirty = true;

                // Update cache item if exists;
                if (_settingsCache.TryGetValue(key, out var cacheItem))
                {
                    cacheItem.Metadata = metadata;
                    cacheItem.UpdateModified();
                }

                // Update category index;
                UpdateCategoryIndex(key, metadata.Category);

                await SaveMetadataToFileAsync(cancellationToken);

                _logger.LogDebug($"Metadata updated for setting '{key}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set metadata for setting '{key}'");
                throw new SettingsOperationException($"Failed to set metadata for setting '{key}'", ex);
            }
        }

        /// <summary>
        /// Resets a setting to its default value;
        /// </summary>
        public async Task ResetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Setting key cannot be null or empty", nameof(key));

            try
            {
                if (_settingsMetadata.TryGetValue(key, out var metadata) && metadata.DefaultValue != null)
                {
                    await SetSettingAsync(key, metadata.DefaultValue, metadata.Category, true, cancellationToken);
                    _logger.LogDebug($"Setting '{key}' reset to default value");
                }
                else
                {
                    await DeleteSettingAsync(key, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reset setting '{key}'");
                throw new SettingsOperationException($"Failed to reset setting '{key}'", ex);
            }
        }

        /// <summary>
        /// Resets all settings to defaults;
        /// </summary>
        public async Task ResetAllSettingsAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            try
            {
                var keys = _settingsCache.Keys.ToList();

                foreach (var key in keys)
                {
                    await ResetSettingAsync(key, cancellationToken);
                }

                _logger.LogInformation("All settings reset to defaults");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset all settings");
                throw new SettingsOperationException("Failed to reset all settings", ex);
            }
        }

        /// <summary>
        /// Imports settings from a file;
        /// </summary>
        public async Task ImportSettingsAsync(string filePath, bool overwriteExisting = true,
            CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Settings file not found: {filePath}");

            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                _logger.LogInformation($"Importing settings from '{filePath}'");

                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var importedSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonSerializerOptions);

                if (importedSettings == null)
                    throw new SettingsImportException("Imported file contains no settings");

                int importedCount = 0;
                int skippedCount = 0;

                foreach (var kvp in importedSettings)
                {
                    if (!overwriteExisting && _settingsCache.ContainsKey(kvp.Key))
                    {
                        skippedCount++;
                        continue;
                    }

                    await SetSettingAsync(kvp.Key, kvp.Value, "Imported", false, cancellationToken);
                    importedCount++;
                }

                await SaveSettingsToFileAsync(cancellationToken);

                _logger.LogInformation($"Imported {importedCount} settings, skipped {skippedCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to import settings from '{filePath}'");
                throw new SettingsImportException($"Failed to import settings from '{filePath}'", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Exports settings to a file;
        /// </summary>
        public async Task ExportSettingsAsync(string filePath, string category = null,
            CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                _logger.LogInformation($"Exporting settings to '{filePath}'");

                Dictionary<string, object> settingsToExport;

                if (string.IsNullOrWhiteSpace(category))
                {
                    settingsToExport = _settingsCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value);
                }
                else
                {
                    settingsToExport = await GetSettingsByCategoryAsync(category, cancellationToken);
                }

                var json = JsonSerializer.Serialize(settingsToExport, _jsonSerializerOptions);
                await File.WriteAllTextAsync(filePath, json, cancellationToken);

                _logger.LogInformation($"Exported {settingsToExport.Count} settings to '{filePath}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to export settings to '{filePath}'");
                throw new SettingsExportException($"Failed to export settings to '{filePath}'", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Creates a backup of current settings;
        /// </summary>
        public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var backupFileName = $"settings_backup_{timestamp}.json";
                var backupPath = Path.Combine(_backupDirectory, backupFileName);

                await ExportSettingsAsync(backupPath, null, cancellationToken);

                // Also backup metadata;
                var metadataBackupFileName = $"metadata_backup_{timestamp}.json";
                var metadataBackupPath = Path.Combine(_backupDirectory, metadataBackupFileName);

                var metadataJson = JsonSerializer.Serialize(_settingsMetadata, _jsonSerializerOptions);
                await File.WriteAllTextAsync(metadataBackupPath, metadataJson, cancellationToken);

                _logger.LogInformation($"Created backup at '{backupPath}'");

                return backupPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create settings backup");
                throw new SettingsOperationException("Failed to create settings backup", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Gets available backups;
        /// </summary>
        public async Task<IEnumerable<string>> GetAvailableBackupsAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (!Directory.Exists(_backupDirectory))
                return Enumerable.Empty<string>();

            return Directory.GetFiles(_backupDirectory, "settings_backup_*.json")
                .OrderByDescending(f => f)
                .Select(Path.GetFileName);
        }

        /// <summary>
        /// Restores settings from a backup;
        /// </summary>
        public async Task RestoreFromBackupAsync(string backupFileName, CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(backupFileName))
                throw new ArgumentException("Backup file name cannot be null or empty", nameof(backupFileName));

            var backupPath = Path.Combine(_backupDirectory, backupFileName);

            if (!File.Exists(backupPath))
                throw new FileNotFoundException($"Backup file not found: {backupPath}");

            try
            {
                _logger.LogInformation($"Restoring settings from backup '{backupFileName}'");

                await ImportSettingsAsync(backupPath, true, cancellationToken);

                _logger.LogInformation($"Settings restored from backup '{backupFileName}'");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to restore settings from backup '{backupFileName}'");
                throw new SettingsOperationException($"Failed to restore settings from backup '{backupFileName}'", ex);
            }
        }

        /// <summary>
        /// Gets settings statistics;
        /// </summary>
        public async Task<Dictionary<string, object>> GetStatisticsAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            return new Dictionary<string, object>
            {
                ["TotalSettings"] = _settingsCache.Count,
                ["TotalCategories"] = _categoryIndex.Count,
                ["TotalMetadata"] = _settingsMetadata.Count,
                ["CacheSize"] = _settingsCache.Count,
                ["IsDirty"] = _isDirty,
                ["LastBackup"] = await GetLastBackupTimeAsync(cancellationToken),
                ["SettingsFilePath"] = _settingsFilePath,
                ["MetadataFilePath"] = _metadataFilePath,
                ["IsInitialized"] = _isInitialized
            };
        }

        /// <summary>
        /// Saves all settings to file;
        /// </summary>
        public async Task SaveAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                await SaveSettingsToFileAsync(cancellationToken);
                await SaveMetadataToFileAsync(cancellationToken);

                _isDirty = false;

                _logger.LogDebug("Settings saved to file");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save settings");
                throw new SettingsOperationException("Failed to save settings", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Clears all settings from memory (does not delete files)
        /// </summary>
        public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);

            try
            {
                _settingsCache.Clear();
                _categoryIndex.Clear();
                _isDirty = true;

                _logger.LogInformation("Settings cache cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear settings cache");
                throw new SettingsOperationException("Failed to clear settings cache", ex);
            }
        }

        #region Private Methods;

        private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (!_isInitialized)
            {
                await InitializeAsync(cancellationToken);
            }
        }

        private async Task LoadSettingsFromFileAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_settingsFilePath))
            {
                _logger.LogInformation("Settings file not found, starting with empty settings");
                return;
            }

            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                _logger.LogDebug($"Loading settings from '{_settingsFilePath}'");

                var json = await File.ReadAllTextAsync(_settingsFilePath, cancellationToken);
                var settings = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonSerializerOptions);

                if (settings != null)
                {
                    foreach (var kvp in settings)
                    {
                        var metadata = await GetOrCreateMetadataAsync(kvp.Key, kvp.Value, "General", cancellationToken);
                        var cacheItem = new SettingsCacheItem(kvp.Value, metadata);

                        _settingsCache[kvp.Key] = cacheItem;
                        UpdateCategoryIndex(kvp.Key, metadata.Category);
                    }

                    _logger.LogDebug($"Loaded {settings.Count} settings from file");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load settings from '{_settingsFilePath}'");
                throw new SettingsLoadException($"Failed to load settings from '{_settingsFilePath}'", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task LoadMetadataFromFileAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_metadataFilePath))
            {
                _logger.LogInformation("Metadata file not found, starting with empty metadata");
                return;
            }

            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                _logger.LogDebug($"Loading metadata from '{_metadataFilePath}'");

                var json = await File.ReadAllTextAsync(_metadataFilePath, cancellationToken);
                var metadata = JsonSerializer.Deserialize<Dictionary<string, SettingMetadata>>(json, _jsonSerializerOptions);

                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        _settingsMetadata[kvp.Key] = kvp.Value;
                        UpdateCategoryIndex(kvp.Key, kvp.Value.Category);
                    }

                    _logger.LogDebug($"Loaded {metadata.Count} metadata entries from file");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load metadata from '{_metadataFilePath}'");
                throw new SettingsLoadException($"Failed to load metadata from '{_metadataFilePath}'", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task MergeConfigurationAsync(CancellationToken cancellationToken)
        {
            if (_configuration == null) return;

            try
            {
                _logger.LogDebug("Merging configuration from IConfiguration");

                var configSettings = _configuration.AsEnumerable()
                    .Where(kvp => kvp.Value != null)
                    .ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);

                foreach (var kvp in configSettings)
                {
                    if (!_settingsCache.ContainsKey(kvp.Key))
                    {
                        await SetSettingAsync(kvp.Key, kvp.Value, "Configuration", false, cancellationToken);
                    }
                }

                _logger.LogDebug($"Merged {configSettings.Count} configuration settings");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to merge configuration settings");
            }
        }

        private async Task SaveSettingsToFileAsync(CancellationToken cancellationToken)
        {
            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                var settingsToSave = _settingsCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value);
                var json = JsonSerializer.Serialize(settingsToSave, _jsonSerializerOptions);

                // Write to temp file first;
                var tempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempFile, json, cancellationToken);

                // Replace original file;
                File.Copy(tempFile, _settingsFilePath, true);
                File.Delete(tempFile);

                _logger.LogDebug($"Saved {settingsToSave.Count} settings to file");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save settings to file");
                throw new SettingsSaveException("Failed to save settings to file", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task SaveMetadataToFileAsync(CancellationToken cancellationToken)
        {
            await _fileLock.WaitAsync(cancellationToken);

            try
            {
                var json = JsonSerializer.Serialize(_settingsMetadata, _jsonSerializerOptions);

                // Write to temp file first;
                var tempFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempFile, json, cancellationToken);

                // Replace original file;
                File.Copy(tempFile, _metadataFilePath, true);
                File.Delete(tempFile);

                _logger.LogDebug($"Saved {_settingsMetadata.Count} metadata entries to file");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save metadata to file");
                throw new SettingsSaveException("Failed to save metadata to file", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task<SettingMetadata> GetOrCreateMetadataAsync(string key, object value, string category,
            CancellationToken cancellationToken)
        {
            if (_settingsMetadata.TryGetValue(key, out var existingMetadata))
            {
                return existingMetadata;
            }

            // Create new metadata;
            var metadata = new SettingMetadata
            {
                Key = key,
                DisplayName = key.ToTitleCase(),
                Description = $"Setting for {key}",
                Category = category,
                ValueType = value?.GetType() ?? typeof(object),
                DefaultValue = value,
                IsRequired = false,
                IsEncrypted = false,
                IsReadOnly = false,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow
            };

            _settingsMetadata[key] = metadata;
            _isDirty = true;

            return metadata;
        }

        private async Task<SettingValidationResult> ValidateSettingAsync<T>(string key, T value, string category,
            CancellationToken cancellationToken)
        {
            // Get existing metadata for validation rules;
            if (_settingsMetadata.TryGetValue(key, out var metadata))
            {
                // Check if setting is read-only;
                if (metadata.IsReadOnly && _settingsCache.ContainsKey(key))
                {
                    return SettingValidationResult.Error(key, value, "Setting is read-only");
                }

                // Check allowed values;
                if (metadata.AllowedValues != null && metadata.AllowedValues.Length > 0)
                {
                    var stringValue = value?.ToString();
                    if (!metadata.AllowedValues.Contains(stringValue))
                    {
                        return SettingValidationResult.Error(key, value,
                            $"Value must be one of: {string.Join(", ", metadata.AllowedValues)}");
                    }
                }

                // Check regex validation;
                if (!string.IsNullOrEmpty(metadata.ValidationRegex) && value is string stringVal)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(stringVal, metadata.ValidationRegex))
                    {
                        return SettingValidationResult.Error(key, value,
                            $"Value does not match required pattern: {metadata.ValidationRegex}");
                    }
                }

                // Check length constraints for strings;
                if (value is string strValue)
                {
                    if (metadata.MinLength > 0 && strValue.Length < metadata.MinLength)
                    {
                        return SettingValidationResult.Error(key, value,
                            $"Value must be at least {metadata.MinLength} characters long");
                    }

                    if (metadata.MaxLength > 0 && strValue.Length > metadata.MaxLength)
                    {
                        return SettingValidationResult.Error(key, value,
                            $"Value must be at most {metadata.MaxLength} characters long");
                    }
                }

                // Check numeric range constraints;
                if (value is IComparable comparableValue && metadata.MinValue != null && metadata.MaxValue != null)
                {
                    if (comparableValue.CompareTo(metadata.MinValue) < 0 ||
                        comparableValue.CompareTo(metadata.MaxValue) > 0)
                    {
                        return SettingValidationResult.Error(key, value,
                            $"Value must be between {metadata.MinValue} and {metadata.MaxValue}");
                    }
                }
            }

            return SettingValidationResult.Success(key, value);
        }

        private void UpdateCategoryIndex(string key, string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return;

            _categoryIndex.AddOrUpdate(
                category,
                new List<string> { key },
                (cat, existingList) =>
                {
                    if (!existingList.Contains(key))
                    {
                        existingList.Add(key);
                    }
                    return existingList;
                });
        }

        private void RemoveFromCategoryIndex(string key, string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return;

            if (_categoryIndex.TryGetValue(category, out var existingList))
            {
                existingList.Remove(key);
                if (existingList.Count == 0)
                {
                    _categoryIndex.TryRemove(category, out _);
                }
            }
        }

        private T ConvertValue<T>(object value)
        {
            if (value == null)
                return default;

            if (value is T typedValue)
                return typedValue;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                // Try JSON conversion for complex types;
                var json = JsonSerializer.Serialize(value);
                return JsonSerializer.Deserialize<T>(json, _jsonSerializerOptions);
            }
        }

        private async Task<DateTime?> GetLastBackupTimeAsync(CancellationToken cancellationToken)
        {
            var backups = await GetAvailableBackupsAsync(cancellationToken);
            var lastBackup = backups.FirstOrDefault();

            if (lastBackup == null)
                return null;

            try
            {
                // Extract timestamp from filename: settings_backup_yyyyMMdd_HHmmss.json;
                var timestampStr = lastBackup.Replace("settings_backup_", "").Replace(".json", "");
                if (DateTime.TryParseExact(timestampStr, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var timestamp))
                {
                    return timestamp;
                }
            }
            catch
            {
                // Ignore parsing errors;
            }

            return null;
        }

        private void AutoSaveCallback()
        {
            if (_isDirty && _isInitialized)
            {
                try
                {
                    // Don't await - fire and forget;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await SaveAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Auto-save failed");
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-save callback failed");
                }
            }
        }

        private void CacheCleanupCallback()
        {
            try
            {
                // Remove old cache items;
                var cutoffTime = DateTime.UtcNow.AddMinutes(-CACHE_ITEM_LIFETIME_MINUTES);
                var oldItems = _settingsCache.Where(kvp => kvp.Value.LastAccessed < cutoffTime).ToList();

                foreach (var oldItem in oldItems)
                {
                    _settingsCache.TryRemove(oldItem.Key, out _);
                }

                // Limit cache size;
                if (_settingsCache.Count > MAX_CACHE_SIZE)
                {
                    var itemsToRemove = _settingsCache.OrderBy(kvp => kvp.Value.LastAccessed)
                        .Take(_settingsCache.Count - MAX_CACHE_SIZE)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in itemsToRemove)
                    {
                        _settingsCache.TryRemove(key, out _);
                    }

                    _logger.LogDebug($"Cache cleanup removed {itemsToRemove.Count} items");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup failed");
            }
        }

        private void OnSettingChanged(string key, object oldValue, object newValue, string category)
        {
            try
            {
                SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, oldValue, newValue, category));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in setting changed event handler");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            try
            {
                // Stop timers;
                _autoSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _cacheCleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // Save any unsaved changes;
                if (_isDirty)
                {
                    await SaveAsync();
                }

                // Dispose timers;
                _autoSaveTimer?.Dispose();
                _cacheCleanupTimer?.Dispose();

                // Dispose file lock;
                _fileLock?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during SettingsManager disposal");
            }
            finally
            {
                _disposed = true;
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().Wait();
        }

        #endregion;
    }

    #region Exceptions;

    public class SettingsManagerException : Exception
    {
        public SettingsManagerException(string message) : base(message) { }
        public SettingsManagerException(string message, Exception inner) : base(message, inner) { }
    }

    public class SettingsOperationException : SettingsManagerException
    {
        public SettingsOperationException(string message) : base(message) { }
        public SettingsOperationException(string message, Exception inner) : base(message, inner) { }
    }

    public class SettingsValidationException : SettingsManagerException
    {
        public SettingsValidationException(string message) : base(message) { }
        public SettingsValidationException(string message, Exception inner) : base(message, inner) { }
    }

    public class SettingsLoadException : SettingsManagerException
    {
        public SettingsLoadException(string message) : base(message) { }
        public SettingsLoadException(string message, Exception inner) : base(message, inner) { }
    }

    public class SettingsSaveException : SettingsManagerException
    {
        public SettingsSaveException(string message) : base(message) { }
        public SettingsSaveException(string message, Exception inner) : base(message, inner) { }
    }

    public class SettingsImportException : SettingsManagerException
    {
        public SettingsImportException(string message) : base(message) { }
        public SettingsImportException(string message, Exception inner) : base(message, inner) { }
    }

    public class SettingsExportException : SettingsManagerException
    {
        public SettingsExportException(string message) : base(message) { }
        public SettingsExportException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;

    /// <summary>
    /// Interface for settings manager;
    /// </summary>
    public interface ISettingsManager : IAsyncDisposable, IDisposable
    {
        event EventHandler<SettingChangedEventArgs> SettingChanged;

        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<T> GetSettingAsync<T>(string key, T defaultValue = default, string category = "General",
            CancellationToken cancellationToken = default);
        Task SetSettingAsync<T>(string key, T value, string category = "General", bool saveImmediately = false,
            CancellationToken cancellationToken = default);
        Task<bool> DeleteSettingAsync(string key, CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> GetSettingsByCategoryAsync(string category,
            CancellationToken cancellationToken = default);
        Task<IEnumerable<string>> GetCategoriesAsync(CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> SearchSettingsAsync(string searchTerm,
            CancellationToken cancellationToken = default);
        Task<SettingMetadata> GetSettingMetadataAsync(string key, CancellationToken cancellationToken = default);
        Task SetSettingMetadataAsync(string key, SettingMetadata metadata, CancellationToken cancellationToken = default);
        Task ResetSettingAsync(string key, CancellationToken cancellationToken = default);
        Task ResetAllSettingsAsync(CancellationToken cancellationToken = default);
        Task ImportSettingsAsync(string filePath, bool overwriteExisting = true,
            CancellationToken cancellationToken = default);
        Task ExportSettingsAsync(string filePath, string category = null, CancellationToken cancellationToken = default);
        Task<string> CreateBackupAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<string>> GetAvailableBackupsAsync(CancellationToken cancellationToken = default);
        Task RestoreFromBackupAsync(string backupFileName, CancellationToken cancellationToken = default);
        Task<Dictionary<string, object>> GetStatisticsAsync(CancellationToken cancellationToken = default);
        Task SaveAsync(CancellationToken cancellationToken = default);
        Task ClearCacheAsync(CancellationToken cancellationToken = default);
    }
}
