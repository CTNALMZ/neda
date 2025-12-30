using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.Core.Common.Constants;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NEDA.Core.Configuration.EnvironmentManager
{
    /// <summary>
    /// Environment configuration manager that handles different environment settings;
    /// </summary>
    public class EnvironmentConfig : IDisposable
    {
        private readonly ILogger<EnvironmentConfig> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _environmentName;
        private readonly Dictionary<string, object> _cachedSettings;
        private readonly object _cacheLock = new object();
        private bool _disposed;

        /// <summary>
        /// Current environment name (Development, Staging, Production)
        /// </summary>
        public string EnvironmentName => _environmentName;

        /// <summary>
        /// Gets a value indicating whether the current environment is development;
        /// </summary>
        public bool IsDevelopment => _environmentName.Equals(EnvironmentConstants.Development, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets a value indicating whether the current environment is staging;
        /// </summary>
        public bool IsStaging => _environmentName.Equals(EnvironmentConstants.Staging, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets a value indicating whether the current environment is production;
        /// </summary>
        public bool IsProduction => _environmentName.Equals(EnvironmentConstants.Production, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the current environment configuration root path;
        /// </summary>
        public string ConfigurationPath { get; private set; }

        /// <summary>
        /// Gets all environment-specific settings;
        /// </summary>
        public IReadOnlyDictionary<string, string> EnvironmentSettings { get; private set; }

        /// <summary>
        /// Event raised when environment configuration changes;
        /// </summary>
        public event EventHandler<EnvironmentChangedEventArgs> EnvironmentChanged;

        /// <summary>
        /// Initializes a new instance of EnvironmentConfig;
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="configuration">Configuration root</param>
        public EnvironmentConfig(ILogger<EnvironmentConfig> logger, IConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _cachedSettings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Determine environment;
            _environmentName = DetermineEnvironment();
            ConfigurationPath = GetEnvironmentConfigurationPath();

            _logger.LogInformation("EnvironmentConfig initialized for environment: {Environment}", _environmentName);

            LoadEnvironmentSettings();
            InitializeFileWatcher();
        }

        /// <summary>
        /// Determines the current environment;
        /// </summary>
        /// <returns>Environment name</returns>
        private string DetermineEnvironment()
        {
            // Priority order for environment detection;
            var environmentSources = new Func<string>[]
            {
                () => Environment.GetEnvironmentVariable("NEDA_ENVIRONMENT"),
                () => Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                () => Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                () => _configuration["Environment"],
                () => _configuration["Environment:Name"],
                () => EnvironmentConstants.Development // Default fallback;
            };

            foreach (var source in environmentSources)
            {
                var env = source();
                if (!string.IsNullOrWhiteSpace(env))
                {
                    var normalizedEnv = env.Trim();

                    // Validate environment name;
                    if (IsValidEnvironment(normalizedEnv))
                    {
                        _logger.LogDebug("Environment determined as: {Environment} from source", normalizedEnv);
                        return normalizedEnv;
                    }
                    else
                    {
                        _logger.LogWarning("Invalid environment name detected: {Environment}, using default", normalizedEnv);
                    }
                }
            }

            return EnvironmentConstants.Development;
        }

        /// <summary>
        /// Validates if the environment name is valid;
        /// </summary>
        private bool IsValidEnvironment(string environmentName)
        {
            var validEnvironments = new[]
            {
                EnvironmentConstants.Development,
                EnvironmentConstants.Staging,
                EnvironmentConstants.Production,
                EnvironmentConstants.Test,
                EnvironmentConstants.QA
            };

            return validEnvironments.Contains(environmentName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the environment-specific configuration path;
        /// </summary>
        private string GetEnvironmentConfigurationPath()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var envConfigPath = Path.Combine(basePath, "Configuration", "EnvironmentConfigs", _environmentName);

            // Ensure directory exists;
            if (!Directory.Exists(envConfigPath))
            {
                Directory.CreateDirectory(envConfigPath);
                _logger.LogInformation("Created environment configuration directory: {Path}", envConfigPath);
            }

            return envConfigPath;
        }

        /// <summary>
        /// Loads environment-specific settings;
        /// </summary>
        private void LoadEnvironmentSettings()
        {
            try
            {
                var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Load from appsettings.{environment}.json;
                var appSettingsFile = Path.Combine(ConfigurationPath, $"appsettings.{_environmentName}.json");
                if (File.Exists(appSettingsFile))
                {
                    var jsonContent = File.ReadAllText(appSettingsFile);
                    var jsonSettings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonContent);

                    if (jsonSettings != null)
                    {
                        foreach (var kvp in jsonSettings)
                        {
                            settings[kvp.Key] = kvp.Value.ToString();
                        }
                    }

                    _logger.LogDebug("Loaded settings from {File}", appSettingsFile);
                }

                // Load from environment-specific config file;
                var envConfigFile = Path.Combine(ConfigurationPath, $"{_environmentName}Config.xml");
                if (File.Exists(envConfigFile))
                {
                    // XML parsing logic would go here;
                    _logger.LogDebug("Environment config file found: {File}", envConfigFile);
                }

                // Load environment variables with NEDA_ prefix;
                var envVars = Environment.GetEnvironmentVariables();
                foreach (var key in envVars.Keys)
                {
                    var keyStr = key?.ToString();
                    if (!string.IsNullOrEmpty(keyStr) &&
                        keyStr.StartsWith("NEDA_", StringComparison.OrdinalIgnoreCase))
                    {
                        var configKey = keyStr.Replace("NEDA_", "").Replace("__", ":");
                        var val = envVars[key]?.ToString();
                        if (val != null)
                        {
                            settings[configKey] = val;
                        }
                    }
                }

                EnvironmentSettings = settings;
                _logger.LogInformation("Loaded {Count} environment settings for {Environment}",
                    settings.Count, _environmentName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading environment settings for {Environment}", _environmentName);
                EnvironmentSettings = new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Initializes file watcher for configuration changes;
        /// </summary>
        private void InitializeFileWatcher()
        {
            try
            {
                var watcher = new FileSystemWatcher(ConfigurationPath)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    Filter = "*.json",
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };

                watcher.Changed += OnConfigurationFileChanged;
                watcher.Created += OnConfigurationFileChanged;
                watcher.Deleted += OnConfigurationFileChanged;
                watcher.Renamed += OnConfigurationFileChanged;

                _logger.LogDebug("File watcher initialized for {Path}", ConfigurationPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize file watcher for configuration changes");
            }
        }

        /// <summary>
        /// Handles configuration file changes;
        /// </summary>
        private void OnConfigurationFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                _logger.LogInformation("Configuration file changed: {File}, reloading settings", e.Name);

                // Clear cache;
                lock (_cacheLock)
                {
                    _cachedSettings.Clear();
                }

                // Reload settings;
                LoadEnvironmentSettings();

                // Raise event;
                EnvironmentChanged?.Invoke(this, new EnvironmentChangedEventArgs
                {
                    EnvironmentName = _environmentName,
                    ChangedFile = e.FullPath,
                    ChangeType = e.ChangeType
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling configuration file change for {File}", e.FullPath);
            }
        }

        /// <summary>
        /// Gets a configuration value by key;
        /// </summary>
        /// <typeparam name="T">Type of value</typeparam>
        /// <param name="key">Configuration key</param>
        /// <param name="defaultValue">Default value if not found</param>
        /// <returns>Configuration value</returns>
        public T GetValue<T>(string key, T defaultValue = default)
        {
            Guard.AgainstNullOrEmpty(key, nameof(key));

            try
            {
                // Check cache first;
                lock (_cacheLock)
                {
                    if (_cachedSettings.TryGetValue(key, out var cachedValue))
                    {
                        return (T)cachedValue;
                    }
                }

                T value = defaultValue;

                // Priority order for configuration sources;
                if (EnvironmentSettings.TryGetValue(key, out var envValue))
                {
                    value = ConvertValue<T>(envValue);
                }
                else if (_configuration[key] != null)
                {
                    value = _configuration.GetValue<T>(key);
                }

                // Cache the value;
                lock (_cacheLock)
                {
                    _cachedSettings[key] = value;
                }

                return value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configuration value for key: {Key}", key);
                return defaultValue;
            }
        }

        /// <summary>
        /// Gets a required configuration value (throws if not found)
        /// </summary>
        /// <typeparam name="T">Type of value</typeparam>
        /// <param name="key">Configuration key</param>
        /// <returns>Configuration value</returns>
        public T GetRequiredValue<T>(string key)
        {
            Guard.AgainstNullOrEmpty(key, nameof(key));

            var value = GetValue<T>(key);
            if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
            {
                throw new InvalidOperationException(
                    $"Required configuration value '{key}' is missing or empty for environment '{_environmentName}'");
            }

            return value;
        }

        /// <summary>
        /// Gets a connection string by name;
        /// </summary>
        /// <param name="name">Connection string name</param>
        /// <returns>Connection string</returns>
        public string GetConnectionString(string name)
        {
            Guard.AgainstNullOrEmpty(name, nameof(name));

            var connectionString = GetValue<string>($"ConnectionStrings:{name}");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                // Fallback to standard configuration;
                connectionString = _configuration.GetConnectionString(name);
            }

            return connectionString;
        }

        /// <summary>
        /// Gets a section of configuration values;
        /// </summary>
        /// <param name="sectionKey">Section key</param>
        /// <returns>Configuration section</returns>
        public IConfigurationSection GetSection(string sectionKey)
        {
            Guard.AgainstNullOrEmpty(sectionKey, nameof(sectionKey));

            return _configuration.GetSection(sectionKey);
        }

        /// <summary>
        /// Checks if a configuration key exists;
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <returns>True if key exists</returns>
        public bool KeyExists(string key)
        {
            Guard.AgainstNullOrEmpty(key, nameof(key));

            return EnvironmentSettings.ContainsKey(key) ||
                   _configuration[key] != null;
        }

        /// <summary>
        /// Gets all configuration keys;
        /// </summary>
        /// <returns>List of all configuration keys</returns>
        public IEnumerable<string> GetAllKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Add environment-specific keys;
            keys.UnionWith(EnvironmentSettings.Keys);

            // Add configuration keys;
            foreach (var section in _configuration.GetChildren())
            {
                CollectKeys(section, keys);
            }

            return keys;
        }

        /// <summary>
        /// Recursively collects configuration keys;
        /// </summary>
        private void CollectKeys(IConfigurationSection section, HashSet<string> keys, string prefix = "")
        {
            var currentKey = string.IsNullOrEmpty(prefix) ? section.Key : $"{prefix}:{section.Key}";

            if (section.Value != null)
            {
                keys.Add(currentKey);
            }

            foreach (var child in section.GetChildren())
            {
                CollectKeys(child, keys, currentKey);
            }
        }

        /// <summary>
        /// Converts string value to specified type;
        /// </summary>
        private T ConvertValue<T>(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return default;

            try
            {
                var type = typeof(T);

                if (type == typeof(string))
                    return (T)(object)value;

                if (type == typeof(bool))
                    return (T)(object)bool.Parse(value);

                if (type == typeof(int))
                    return (T)(object)int.Parse(value);

                if (type == typeof(long))
                    return (T)(object)long.Parse(value);

                if (type == typeof(double))
                    return (T)(object)double.Parse(value);

                if (type == typeof(decimal))
                    return (T)(object)decimal.Parse(value);

                if (type == typeof(DateTime))
                    return (T)(object)DateTime.Parse(value);

                if (type == typeof(TimeSpan))
                    return (T)(object)TimeSpan.Parse(value);

                if (type == typeof(Guid))
                    return (T)(object)Guid.Parse(value);

                if (type.IsEnum)
                    return (T)Enum.Parse(type, value);

                // For complex types, use JSON deserialization;
                return JsonSerializer.Deserialize<T>(value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting value '{Value}' to type {Type}", value, typeof(T).Name);
                throw new InvalidCastException($"Cannot convert '{value}' to type {typeof(T).Name}", ex);
            }
        }

        /// <summary>
        /// Switches to a different environment at runtime;
        /// </summary>
        /// <param name="newEnvironment">New environment name</param>
        /// <returns>True if switch was successful</returns>
        public bool SwitchEnvironment(string newEnvironment)
        {
            Guard.AgainstNullOrEmpty(newEnvironment, nameof(newEnvironment));

            if (!IsValidEnvironment(newEnvironment))
            {
                _logger.LogError("Invalid environment name: {Environment}", newEnvironment);
                return false;
            }

            if (_environmentName.Equals(newEnvironment, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Already in environment: {Environment}", newEnvironment);
                return true;
            }

            try
            {
                // Set environment variable;
                Environment.SetEnvironmentVariable("NEDA_ENVIRONMENT", newEnvironment);

                var oldEnvironment = _environmentName;

                _logger.LogInformation("Environment switch requested from {Old} to {New}",
                    oldEnvironment, newEnvironment);

                // Raise environment changed event;
                EnvironmentChanged?.Invoke(this, new EnvironmentChangedEventArgs
                {
                    OldEnvironmentName = oldEnvironment,
                    EnvironmentName = newEnvironment,
                    ChangeType = WatcherChangeTypes.All
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error switching environment from {Old} to {New}",
                    _environmentName, newEnvironment);
                return false;
            }
        }

        /// <summary>
        /// Validates all required configuration settings;
        /// </summary>
        /// <returns>Validation result with any missing settings</returns>
        public ConfigurationValidationResult ValidateConfiguration()
        {
            var result = new ConfigurationValidationResult();
            var requiredSettings = GetRequiredSettings();

            foreach (var setting in requiredSettings)
            {
                if (!KeyExists(setting.Key))
                {
                    result.MissingSettings.Add(setting.Key);
                    result.IsValid = false;
                }
                else
                {
                    var value = GetValue<string>(setting.Key);
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        result.EmptySettings.Add(setting.Key);
                        result.IsValid = false;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the list of required settings for current environment;
        /// </summary>
        private List<RequiredSetting> GetRequiredSettings()
        {
            var requiredSettings = new List<RequiredSetting>
            {
                new RequiredSetting("Database:ConnectionString", "Database connection string"),
                new RequiredSetting("Logging:LogLevel", "Logging level configuration"),
                new RequiredSetting("Security:EncryptionKey", "Encryption key for security operations")
            };

            // Add environment-specific required settings;
            if (IsProduction)
            {
                requiredSettings.Add(new RequiredSetting("Monitoring:ApplicationInsightsKey",
                    "Application Insights instrumentation key"));
                requiredSettings.Add(new RequiredSetting("Security:AuditEnabled",
                    "Audit logging enabled flag"));
            }

            return requiredSettings;
        }

        /// <summary>
        /// Clears all cached configuration values;
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _cachedSettings.Clear();
                _logger.LogDebug("Configuration cache cleared");
            }
        }

        /// <summary>
        /// Disposes the environment configuration;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected implementation of Dispose pattern;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Clean up managed resources;
                    _cachedSettings.Clear();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Destructor;
        /// </summary>
        ~EnvironmentConfig()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// Event arguments for environment changed events;
    /// </summary>
    public class EnvironmentChangedEventArgs : EventArgs
    {
        public string OldEnvironmentName { get; set; }
        public string EnvironmentName { get; set; }
        public string ChangedFile { get; set; }
        public WatcherChangeTypes ChangeType { get; set; }
    }

    /// <summary>
    /// Configuration validation result;
    /// </summary>
    public class ConfigurationValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> MissingSettings { get; } = new List<string>();
        public List<string> EmptySettings { get; } = new List<string>();

        public override string ToString()
        {
            if (IsValid)
                return "Configuration validation passed";

            var issues = new List<string>();

            if (MissingSettings.Count > 0)
                issues.Add($"Missing settings: {string.Join(", ", MissingSettings)}");

            if (EmptySettings.Count > 0)
                issues.Add($"Empty settings: {string.Join(", ", EmptySettings)}");

            return $"Configuration validation failed: {string.Join("; ", issues)}";
        }
    }

    /// <summary>
    /// Required setting definition;
    /// </summary>
    public class RequiredSetting
    {
        public string Key { get; }
        public string Description { get; }

        public RequiredSetting(string key, string description)
        {
            Key = key;
            Description = description;
        }
    }

    /// <summary>
    /// Environment constants;
    /// </summary>
    public static class EnvironmentConstants
    {
        public const string Development = "Development";
        public const string Staging = "Staging";
        public const string Production = "Production";
        public const string Test = "Test";
        public const string QA = "QA";
    }
}
