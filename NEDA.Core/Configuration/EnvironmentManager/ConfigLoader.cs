using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.Middleware;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Configuration.ConfigValidators;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.Brain.NLP_Engine.SyntaxAnalysis.GrammarEngine;
using static NEDA.Core.Common.Constants;

namespace NEDA.Core.Configuration.EnvironmentManager
{
    /// <summary>
    /// Advanced configuration loader for NEDA application with support for multiple sources,
    /// environment-specific overrides, hot reload, and validation;
    /// </summary>
    public class ConfigLoader : IConfigLoader, IDisposable
    {
        private readonly ILogger<ConfigLoader> _logger;
        private readonly IConfigValidator _configValidator;
        private readonly IErrorReporter _errorReporter;

        private IConfigurationRoot _configuration;
        private AppConfig _appConfig;
        private readonly string _basePath;
        private readonly string _environmentName;
        private readonly List<IConfigurationSource> _additionalSources;
        private readonly FileSystemWatcher _configWatcher;
        private readonly Timer _reloadTimer;
        private bool _isDisposed;
        private bool _isInitialized;
        private readonly object _lockObject = new object();
        private readonly SemaphoreSlim _reloadSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Event raised when configuration is reloaded;
        /// </summary>
        public event EventHandler<ConfigurationReloadedEventArgs> ConfigurationReloaded;

        /// <summary>
        /// Event raised when configuration validation fails;
        /// </summary>
        public event EventHandler<ConfigurationValidationFailedEventArgs> ConfigurationValidationFailed;

        /// <summary>
        /// Gets the current application configuration;
        /// </summary>
        public AppConfig CurrentConfig
        {
            get
            {
                if (_appConfig == null && _isInitialized)
                {
                    throw new ConfigurationNotLoadedException("Configuration has not been loaded yet");
                }
                return _appConfig;
            }
            private set => _appConfig = value;
        }

        /// <summary>
        /// Gets the underlying IConfigurationRoot;
        /// </summary>
        public IConfigurationRoot ConfigurationRoot => _configuration;

        /// <summary>
        /// Gets a value indicating whether hot reload is enabled;
        /// </summary>
        public bool HotReloadEnabled { get; private set; }

        /// <summary>
        /// Gets the environment name;
        /// </summary>
        public string EnvironmentName => _environmentName;

        /// <summary>
        /// Gets the configuration file path;
        /// </summary>
        public string ConfigFilePath { get; private set; }

        /// <summary>
        /// Gets the last reload time;
        /// </summary>
        public DateTime LastReloadTime { get; private set; }

        /// <summary>
        /// Gets the number of configuration reloads;
        /// </summary>
        public int ReloadCount { get; private set; }

        /// <summary>
        /// Initializes a new instance of ConfigLoader;
        /// </summary>
        public ConfigLoader(
            ILogger<ConfigLoader> logger,
            IConfigValidator configValidator,
            IErrorReporter errorReporter,
            string basePath = null,
            string environmentName = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configValidator = configValidator ?? throw new ArgumentNullException(nameof(configValidator));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            _basePath = basePath ?? Directory.GetCurrentDirectory();
            _environmentName = environmentName ?? GetEnvironmentName();
            _additionalSources = new List<IConfigurationSource>();

            // Initialize file system watcher for hot reload;
            _configWatcher = new FileSystemWatcher(_basePath)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                Filter = "*.json",
                IncludeSubdirectories = false,
                EnableRaisingEvents = false
            };

            _configWatcher.Changed += OnConfigFileChanged;
            _configWatcher.Created += OnConfigFileChanged;
            _configWatcher.Deleted += OnConfigFileChanged;
            _configWatcher.Renamed += OnConfigFileRenamed;

            // Initialize reload timer for periodic checks;
            _reloadTimer = new Timer(OnReloadTimerTick, null, Timeout.Infinite, Timeout.Infinite);

            _logger.LogInformation("ConfigLoader initialized for environment: {EnvironmentName}, base path: {BasePath}",
                _environmentName, _basePath);
        }

        /// <summary>
        /// Loads configuration from all configured sources;
        /// </summary>
        public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
        {
            return await LoadAsync(ConfigLoadOptions.Default, cancellationToken);
        }

        /// <summary>
        /// Loads configuration with specified options;
        /// </summary>
        public async Task<AppConfig> LoadAsync(ConfigLoadOptions options, CancellationToken cancellationToken = default)
        {
            if (_isInitialized && !options.ForceReload)
            {
                _logger.LogDebug("Configuration already loaded, returning cached config");
                return CurrentConfig;
            }

            await _reloadSemaphore.WaitAsync(cancellationToken);

            try
            {
                _logger.LogInformation("Loading configuration with options: {@Options}", options);

                // Build configuration from all sources;
                var configBuilder = new ConfigurationBuilder()
                    .SetBasePath(_basePath)
                    .AddDefaultSources(_environmentName, options);

                // Add additional sources;
                foreach (var source in _additionalSources)
                {
                    configBuilder.Add(source);
                }

                // Add command line arguments if provided;
                if (options.CommandLineArgs != null && options.CommandLineArgs.Length > 0)
                {
                    configBuilder.AddCommandLine(options.CommandLineArgs);
                }

                // Add environment variables;
                if (options.LoadEnvironmentVariables)
                {
                    configBuilder.AddEnvironmentVariables("NEDA_");
                }

                // Add user secrets for development;
                if (options.LoadUserSecrets && _environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        configBuilder.AddUserSecrets<ConfigLoader>();
                        _logger.LogDebug("User secrets added for development environment");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load user secrets");
                    }
                }

                // Build configuration;
                _configuration = configBuilder.Build();

                // Map to AppConfig;
                CurrentConfig = MapConfiguration(_configuration);

                // Set config file path;
                ConfigFilePath = Path.Combine(_basePath, $"appsettings.{_environmentName}.json");

                // Validate configuration;
                await ValidateConfigurationAsync(options.ValidationMode, cancellationToken);

                // Apply post-load actions;
                await ApplyPostLoadActionsAsync(options, cancellationToken);

                // Enable hot reload if requested;
                if (options.EnableHotReload)
                {
                    EnableHotReload(options.HotReloadInterval);
                }

                _isInitialized = true;
                LastReloadTime = DateTime.UtcNow;

                _logger.LogInformation("Configuration loaded successfully. Environment: {EnvironmentName}, Config file: {ConfigFilePath}",
                    _environmentName, ConfigFilePath);

                return CurrentConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load configuration");
                await _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = "ConfigLoader.LoadAsync",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.High
                });

                throw new ConfigurationLoadException("Failed to load configuration", ex);
            }
            finally
            {
                _reloadSemaphore.Release();
            }
        }

        /// <summary>
        /// Reloads configuration from sources;
        /// </summary>
        public async Task<AppConfig> ReloadAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Reloading configuration...");

            var oldConfig = CurrentConfig?.Clone();

            try
            {
                var options = new ConfigLoadOptions
                {
                    ForceReload = true,
                    ValidationMode = ValidationMode.Strict,
                    EnableHotReload = HotReloadEnabled
                };

                var newConfig = await LoadAsync(options, cancellationToken);

                ReloadCount++;

                // Raise reload event;
                OnConfigurationReloaded(new ConfigurationReloadedEventArgs
                {
                    OldConfig = oldConfig,
                    NewConfig = newConfig,
                    ReloadTime = DateTime.UtcNow,
                    ReloadCount = ReloadCount
                });

                _logger.LogInformation("Configuration reloaded successfully. Reload count: {ReloadCount}", ReloadCount);

                return newConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload configuration");
                await _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = "ConfigLoader.ReloadAsync",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.Medium
                });

                throw;
            }
        }

        /// <summary>
        /// Adds an additional configuration source;
        /// </summary>
        public void AddSource(IConfigurationSource source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            lock (_lockObject)
            {
                _additionalSources.Add(source);
                _logger.LogDebug("Added configuration source: {SourceType}", source.GetType().Name);
            }
        }

        /// <summary>
        /// Adds a JSON configuration file;
        /// </summary>
        public void AddJsonFile(string path, bool optional = false, bool reloadOnChange = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            var fullPath = Path.Combine(_basePath, path);
            AddSource(new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
            {
                Path = fullPath,
                Optional = optional,
                ReloadOnChange = reloadOnChange
            });

            _logger.LogDebug("Added JSON configuration file: {Path} (Optional: {Optional}, ReloadOnChange: {ReloadOnChange})",
                fullPath, optional, reloadOnChange);
        }

        /// <summary>
        /// Adds an XML configuration file;
        /// </summary>
        public void AddXmlFile(string path, bool optional = false, bool reloadOnChange = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            var fullPath = Path.Combine(_basePath, path);
            AddSource(new Microsoft.Extensions.Configuration.Xml.XmlConfigurationSource
            {
                Path = fullPath,
                Optional = optional,
                ReloadOnChange = reloadOnChange
            });

            _logger.LogDebug("Added XML configuration file: {Path} (Optional: {Optional}, ReloadOnChange: {ReloadOnChange})",
                fullPath, optional, reloadOnChange);
        }

        /// <summary>
        /// Adds environment variables with specified prefix;
        /// </summary>
        public void AddEnvironmentVariables(string prefix = "NEDA_")
        {
            AddSource(new Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource
            {
                Prefix = prefix
            });

            _logger.LogDebug("Added environment variables with prefix: {Prefix}", prefix);
        }

        /// <summary>
        /// Adds command line arguments;
        /// </summary>
        public void AddCommandLine(string[] args)
        {
            if (args == null)
                throw new ArgumentNullException(nameof(args));

            AddSource(new Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationSource
            {
                Args = args
            });

            _logger.LogDebug("Added command line arguments: {ArgCount} arguments", args.Length);
        }

        /// <summary>
        /// Enables configuration hot reload;
        /// </summary>
        public void EnableHotReload(TimeSpan? checkInterval = null)
        {
            if (HotReloadEnabled)
            {
                _logger.LogDebug("Hot reload is already enabled");
                return;
            }

            try
            {
                // Enable file system watcher;
                _configWatcher.Path = _basePath;
                _configWatcher.EnableRaisingEvents = true;

                // Set up periodic reload timer;
                var interval = checkInterval ?? TimeSpan.FromSeconds(30);
                _reloadTimer.Change(interval, interval);

                HotReloadEnabled = true;

                _logger.LogInformation("Hot reload enabled with check interval: {Interval}", interval);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enable hot reload");
                HotReloadEnabled = false;
            }
        }

        /// <summary>
        /// Disables configuration hot reload;
        /// </summary>
        public void DisableHotReload()
        {
            if (!HotReloadEnabled)
            {
                return;
            }

            try
            {
                _configWatcher.EnableRaisingEvents = false;
                _reloadTimer.Change(Timeout.Infinite, Timeout.Infinite);
                HotReloadEnabled = false;

                _logger.LogInformation("Hot reload disabled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to disable hot reload");
            }
        }

        /// <summary>
        /// Gets a configuration value by key;
        /// </summary>
        public string GetValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (_configuration == null)
                throw new ConfigurationNotLoadedException("Configuration has not been loaded");

            return _configuration[key];
        }

        /// <summary>
        /// Gets a configuration value by key with default value;
        /// </summary>
        public string GetValue(string key, string defaultValue)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (_configuration == null)
                return defaultValue;

            return _configuration[key] ?? defaultValue;
        }

        /// <summary>
        /// Gets a configuration section;
        /// </summary>
        public IConfigurationSection GetSection(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (_configuration == null)
                throw new ConfigurationNotLoadedException("Configuration has not been loaded");

            return _configuration.GetSection(key);
        }

        /// <summary>
        /// Gets all configuration values as a dictionary;
        /// </summary>
        public Dictionary<string, string> GetAllValues()
        {
            if (_configuration == null)
                throw new ConfigurationNotLoadedException("Configuration has not been loaded");

            return _configuration.AsEnumerable().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Exports configuration to a file;
        /// </summary>
        public async Task ExportToFileAsync(string filePath, ExportFormat format = ExportFormat.Json, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (CurrentConfig == null)
                throw new ConfigurationNotLoadedException("Configuration has not been loaded");

            try
            {
                string content;

                switch (format)
                {
                    case ExportFormat.Json:
                        content = CurrentConfig.ToJson();
                        break;
                    case ExportFormat.Xml:
                        content = SerializeToXml(CurrentConfig);
                        break;
                    case ExportFormat.Yaml:
                        content = SerializeToYaml(CurrentConfig);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(format), format, null);
                }

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken);

                _logger.LogInformation("Configuration exported to {FilePath} in {Format} format", filePath, format);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export configuration to file: {FilePath}", filePath);
                throw new ConfigurationExportException($"Failed to export configuration to {filePath}", ex);
            }
        }

        /// <summary>
        /// Imports configuration from a file;
        /// </summary>
        public async Task<AppConfig> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Configuration file not found: {filePath}", filePath);

            try
            {
                var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                var format = DetectFileFormat(filePath);

                AppConfig importedConfig;

                switch (format)
                {
                    case ExportFormat.Json:
                        importedConfig = AppConfig.FromJson(content);
                        break;
                    case ExportFormat.Xml:
                        importedConfig = DeserializeFromXml(content);
                        break;
                    case ExportFormat.Yaml:
                        importedConfig = DeserializeFromYaml(content);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported file format: {format}");
                }

                // Validate imported configuration;
                await _configValidator.Validate(importedConfig, ValidationMode.Strict);

                // Merge with current configuration;
                if (CurrentConfig != null)
                {
                    CurrentConfig.Merge(importedConfig);
                }
                else
                {
                    CurrentConfig = importedConfig;
                }

                _logger.LogInformation("Configuration imported from {FilePath}", filePath);

                return CurrentConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import configuration from file: {FilePath}", filePath);
                throw new ConfigurationImportException($"Failed to import configuration from {filePath}", ex);
            }
        }

        /// <summary>
        /// Creates a configuration snapshot;
        /// </summary>
        public ConfigSnapshot CreateSnapshot()
        {
            if (CurrentConfig == null)
                throw new ConfigurationNotLoadedException("Configuration has not been loaded");

            return new ConfigSnapshot
            {
                Config = CurrentConfig.Clone(),
                Timestamp = DateTime.UtcNow,
                Environment = _environmentName,
                Version = CurrentConfig.ApplicationSettings?.Version ?? "1.0.0"
            };
        }

        /// <summary>
        /// Restores configuration from a snapshot;
        /// </summary>
        public async Task RestoreFromSnapshotAsync(ConfigSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            if (snapshot.Config == null)
                throw new ArgumentException("Snapshot configuration cannot be null", nameof(snapshot));

            try
            {
                // Validate snapshot configuration;
                await _configValidator.Validate(snapshot.Config, ValidationMode.Strict);

                // Restore configuration;
                CurrentConfig = snapshot.Config;

                _logger.LogInformation("Configuration restored from snapshot taken at {Timestamp}", snapshot.Timestamp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore configuration from snapshot");
                throw new ConfigurationRestoreException("Failed to restore configuration from snapshot", ex);
            }
        }

        #region Private Methods

        private string GetEnvironmentName()
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                     Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
                     "Production";

            _logger.LogDebug("Detected environment: {Environment}", env);
            return env;
        }

        private AppConfig MapConfiguration(IConfiguration configuration)
        {
            try
            {
                var config = new AppConfig();
                configuration.Bind(config);
                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to map configuration to AppConfig");
                throw new ConfigurationMappingException("Failed to map configuration to AppConfig model", ex);
            }
        }

        private async Task ValidateConfigurationAsync(ValidationMode validationMode, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("Validating configuration with mode: {ValidationMode}", validationMode);

                var validationResult = await Task.Run(() =>
                    _configValidator.Validate(CurrentConfig, validationMode), cancellationToken);

                if (!validationResult.IsValid && validationMode == ValidationMode.Strict)
                {
                    var validationReport = _configValidator.GetValidationReport();
                    var errorMessage = $"Configuration validation failed: {validationResult.Message}";

                    OnConfigurationValidationFailed(new ConfigurationValidationFailedEventArgs
                    {
                        ValidationReport = validationReport,
                        Config = CurrentConfig,
                        ErrorMessage = errorMessage
                    });

                    throw new ConfigurationValidationException(errorMessage, _configValidator.ValidationResults);
                }

                _logger.LogInformation("Configuration validation completed: {IsValid}", validationResult.IsValid);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Configuration validation was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Configuration validation failed");
                await _errorReporter.ReportErrorAsync(new ErrorReport
                {
                    ErrorCode = ErrorCodes.ConfigurationError,
                    Exception = ex,
                    Context = "ConfigLoader.ValidateConfigurationAsync",
                    Timestamp = DateTime.UtcNow,
                    Severity = ErrorSeverity.High
                });
                throw;
            }
        }

        private async Task ApplyPostLoadActionsAsync(ConfigLoadOptions options, CancellationToken cancellationToken)
        {
            if (options.ApplyEnvironmentOverrides)
            {
                ApplyEnvironmentOverrides();
            }

            if (options.EncryptSensitiveData)
            {
                await EncryptSensitiveDataAsync(cancellationToken);
            }

            if (options.ResolvePlaceholders)
            {
                ResolvePlaceholders();
            }
        }

        private void ApplyEnvironmentOverrides()
        {
            // Apply environment-specific overrides;
            var env = CurrentConfig.ApplicationSettings?.Environment ?? EnvironmentType.Production;

            switch (env)
            {
                case EnvironmentType.Production:
                    // Ensure HTTPS is enabled in production;
                    CurrentConfig.SecuritySettings.RequireHttps = true;
                    // Disable debug mode;
                    CurrentConfig.ApplicationSettings.DebugMode = false;
                    // Set appropriate log level;
                    CurrentConfig.LoggingSettings.LogLevel = LogLevel.Information;
                    break;

                case EnvironmentType.Development:
                    // Allow HTTP in development;
                    CurrentConfig.SecuritySettings.RequireHttps = false;
                    // Enable debug mode;
                    CurrentConfig.ApplicationSettings.DebugMode = true;
                    // Set verbose logging;
                    CurrentConfig.LoggingSettings.LogLevel = LogLevel.Debug;
                    break;
            }

            _logger.LogDebug("Applied environment overrides for {Environment}", env);
        }

        private async Task EncryptSensitiveDataAsync(CancellationToken cancellationToken)
        {
            // This would encrypt sensitive data like connection strings, API keys, etc.
            // For now, we'll just log what would be encrypted;
            var sensitiveFields = new List<string>();

            if (!string.IsNullOrWhiteSpace(CurrentConfig.DatabaseSettings?.ConnectionString))
                sensitiveFields.Add("Database.ConnectionString");

            if (!string.IsNullOrWhiteSpace(CurrentConfig.SecuritySettings?.JwtSecret))
                sensitiveFields.Add("Security.JwtSecret");

            if (!string.IsNullOrWhiteSpace(CurrentConfig.SecuritySettings?.EncryptionKey))
                sensitiveFields.Add("Security.EncryptionKey");

            if (sensitiveFields.Count > 0)
            {
                _logger.LogDebug("Sensitive data fields identified for encryption: {Fields}",
                    string.Join(", ", sensitiveFields));
                // Actual encryption implementation would go here;
                await Task.Delay(100, cancellationToken); // Simulate encryption work;
            }
        }

        private void ResolvePlaceholders()
        {
            // Resolve placeholders like ${ENV_VAR} in configuration values;
            // This is a simplified implementation;
            var resolvedCount = 0;

            // Example: Resolve environment variables in connection string;
            if (CurrentConfig.DatabaseSettings?.ConnectionString != null)
            {
                var original = CurrentConfig.DatabaseSettings.ConnectionString;
                var resolved = ResolveEnvironmentVariables(original);
                if (original != resolved)
                {
                    CurrentConfig.DatabaseSettings.ConnectionString = resolved;
                    resolvedCount++;
                }
            }

            if (resolvedCount > 0)
            {
                _logger.LogDebug("Resolved {Count} configuration placeholders", resolvedCount);
            }
        }

        private string ResolveEnvironmentVariables(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            // Simple environment variable resolution: ${VAR_NAME}
            var result = value;
            var startIndex = result.IndexOf("${", StringComparison.Ordinal);

            while (startIndex >= 0)
            {
                var endIndex = result.IndexOf("}", startIndex, StringComparison.Ordinal);
                if (endIndex < 0) break;

                var varName = result.Substring(startIndex + 2, endIndex - startIndex - 2);
                var varValue = Environment.GetEnvironmentVariable(varName);

                if (!string.IsNullOrWhiteSpace(varValue))
                {
                    result = result.Replace("${" + varName + "}", varValue);
                }

                startIndex = result.IndexOf("${", startIndex + 1, StringComparison.Ordinal);
            }

            return result;
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!HotReloadEnabled)
                return;

            // Filter for configuration files;
            if (e.Name.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                e.Name.EndsWith($"appsettings.{_environmentName}.json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Configuration file changed: {FileName}, change type: {ChangeType}", e.Name, e.ChangeType);

                // Debounce reload to avoid multiple rapid reloads;
                Task.Delay(500).ContinueWith(async _ =>
                {
                    try
                    {
                        await ReloadAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reload configuration after file change");
                    }
                });
            }
        }

        private void OnConfigFileRenamed(object sender, RenamedEventArgs e)
        {
            if (!HotReloadEnabled)
                return;

            _logger.LogDebug("Configuration file renamed: {OldName} -> {NewName}", e.OldName, e.Name);
            OnConfigFileChanged(sender, e);
        }

        private void OnReloadTimerTick(object state)
        {
            if (!HotReloadEnabled)
                return;

            Task.Run(async () =>
            {
                try
                {
                    _logger.LogDebug("Periodic configuration reload check");
                    await ReloadAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to reload configuration on timer tick");
                }
            });
        }

        private ExportFormat DetectFileFormat(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            return extension switch
            {
                ".json" => ExportFormat.Json,
                ".xml" => ExportFormat.Xml,
                ".yaml" or ".yml" => ExportFormat.Yaml,
                _ => throw new NotSupportedException($"Unsupported file format: {extension}")
            };
        }

        private string SerializeToXml(AppConfig config)
        {
            // Simplified XML serialization - in production, use XmlSerializer or similar;
            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            xml.AppendLine("<Configuration>");
            xml.AppendLine($"  <ApplicationName>{config.ApplicationSettings?.Name}</ApplicationName>");
            xml.AppendLine($"  <Environment>{config.ApplicationSettings?.Environment}</Environment>");
            xml.AppendLine("</Configuration>");
            return xml.ToString();
        }

        private string SerializeToYaml(AppConfig config)
        {
            // Simplified YAML serialization - in production, use YamlDotNet or similar;
            var yaml = new System.Text.StringBuilder();
            yaml.AppendLine("application:");
            yaml.AppendLine($"  name: {config.ApplicationSettings?.Name}");
            yaml.AppendLine($"  environment: {config.ApplicationSettings?.Environment}");
            yaml.AppendLine($"  version: {config.ApplicationSettings?.Version}");
            return yaml.ToString();
        }

        private AppConfig DeserializeFromXml(string xml)
        {
            // Simplified XML deserialization;
            // In production, implement proper XML deserialization;
            throw new NotImplementedException("XML deserialization not implemented in this example");
        }

        private AppConfig DeserializeFromYaml(string yaml)
        {
            // Simplified YAML deserialization;
            // In production, implement proper YAML deserialization;
            throw new NotImplementedException("YAML deserialization not implemented in this example");
        }

        private void OnConfigurationReloaded(ConfigurationReloadedEventArgs e)
        {
            ConfigurationReloaded?.Invoke(this, e);
        }

        private void OnConfigurationValidationFailed(ConfigurationValidationFailedEventArgs e)
        {
            ConfigurationValidationFailed?.Invoke(this, e);
        }

        #endregion

        #region IDisposable Implementation

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _configWatcher?.Dispose();
                    _reloadTimer?.Dispose();
                    _reloadSemaphore?.Dispose();
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ConfigLoader()
        {
            Dispose(false);
        }

        #endregion
    }

    #region Supporting Classes and Enums

    /// <summary>
    /// Configuration load options;
    /// </summary>
    public class ConfigLoadOptions
    {
        /// <summary>
        /// Gets or sets whether to force reload even if already loaded;
        /// </summary>
        public bool ForceReload { get; set; } = false;

        /// <summary>
        /// Gets or sets the validation mode;
        /// </summary>
        public ValidationMode ValidationMode { get; set; } = ValidationMode.Strict;

        /// <summary>
        /// Gets or sets whether to enable hot reload;
        /// </summary>
        public bool EnableHotReload { get; set; } = false;

        /// <summary>
        /// Gets or sets the hot reload check interval;
        /// </summary>
        public TimeSpan HotReloadInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets whether to load environment variables;
        /// </summary>
        public bool LoadEnvironmentVariables { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to load user secrets (development only)
        /// </summary>
        public bool LoadUserSecrets { get; set; } = true;

        /// <summary>
        /// Gets or sets command line arguments to include;
        /// </summary>
        public string[] CommandLineArgs { get; set; }

        /// <summary>
        /// Gets or sets whether to apply environment-specific overrides;
        /// </summary>
        public bool ApplyEnvironmentOverrides { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to encrypt sensitive data;
        /// </summary>
        public bool EncryptSensitiveData { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to resolve placeholders;
        /// </summary>
        public bool ResolvePlaceholders { get; set; } = true;

        /// <summary>
        /// Gets or sets configuration file paths to load;
        /// </summary>
        public string[] AdditionalConfigFiles { get; set; }

        /// <summary>
        /// Gets the default load options;
        /// </summary>
        public static ConfigLoadOptions Default => new ConfigLoadOptions();
    }

    /// <summary>
    /// Export format enumeration;
    /// </summary>
    public enum ExportFormat
    {
        Json = 0,
        Xml = 1,
        Yaml = 2
    }

    /// <summary>
    /// Configuration snapshot for backup/restore;
    /// </summary>
    public class ConfigSnapshot
    {
        /// <summary>
        /// Gets or sets the configuration data;
        /// </summary>
        public AppConfig Config { get; set; }

        /// <summary>
        /// Gets or sets the snapshot timestamp;
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the environment name;
        /// </summary>
        public string Environment { get; set; }

        /// <summary>
        /// Gets or sets the configuration version;
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// Gets or sets additional metadata;
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Event arguments for configuration reloaded event;
    /// </summary>
    public class ConfigurationReloadedEventArgs : EventArgs
    {
        public AppConfig OldConfig { get; set; }
        public AppConfig NewConfig { get; set; }
        public DateTime ReloadTime { get; set; }
        public int ReloadCount { get; set; }
        public string Reason { get; set; } = "Manual reload";
    }

    /// <summary>
    /// Event arguments for configuration validation failed event;
    /// </summary>
    public class ConfigurationValidationFailedEventArgs : EventArgs
    {
        public ValidationReport ValidationReport { get; set; }
        public AppConfig Config { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Interface for configuration loader;
    /// </summary>
    public interface IConfigLoader : IDisposable
    {
        Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);
        Task<AppConfig> LoadAsync(ConfigLoadOptions options, CancellationToken cancellationToken = default);
        Task<AppConfig> ReloadAsync(CancellationToken cancellationToken = default);

        void AddSource(IConfigurationSource source);
        void AddJsonFile(string path, bool optional = false, bool reloadOnChange = false);
        void AddXmlFile(string path, bool optional = false, bool reloadOnChange = false);
        void AddEnvironmentVariables(string prefix = "NEDA_");
        void AddCommandLine(string[] args);

        void EnableHotReload(TimeSpan? checkInterval = null);
        void DisableHotReload();

        string GetValue(string key);
        string GetValue(string key, string defaultValue);
        IConfigurationSection GetSection(string key);
        Dictionary<string, string> GetAllValues();

        Task ExportToFileAsync(string filePath, ExportFormat format = ExportFormat.Json, CancellationToken cancellationToken = default);
        Task<AppConfig> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default);

        ConfigSnapshot CreateSnapshot();
        Task RestoreFromSnapshotAsync(ConfigSnapshot snapshot, CancellationToken cancellationToken = default);

        AppConfig CurrentConfig { get; }
        IConfigurationRoot ConfigurationRoot { get; }
        bool HotReloadEnabled { get; }
        string EnvironmentName { get; }

        event EventHandler<ConfigurationReloadedEventArgs> ConfigurationReloaded;
        event EventHandler<ConfigurationValidationFailedEventArgs> ConfigurationValidationFailed;
    }

    #endregion

    #region Custom Exceptions

    public class ConfigurationLoadException : Exception
    {
        public ConfigurationLoadException() { }
        public ConfigurationLoadException(string message) : base(message) { }
        public ConfigurationLoadException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationNotLoadedException : Exception
    {
        public ConfigurationNotLoadedException() { }
        public ConfigurationNotLoadedException(string message) : base(message) { }
        public ConfigurationNotLoadedException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationMappingException : Exception
    {
        public ConfigurationMappingException() { }
        public ConfigurationMappingException(string message) : base(message) { }
        public ConfigurationMappingException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationExportException : Exception
    {
        public ConfigurationExportException() { }
        public ConfigurationExportException(string message) : base(message) { }
        public ConfigurationExportException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationImportException : Exception
    {
        public ConfigurationImportException() { }
        public ConfigurationImportException(string message) : base(message) { }
        public ConfigurationImportException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConfigurationRestoreException : Exception
    {
        public ConfigurationRestoreException() { }
        public ConfigurationRestoreException(string message) : base(message) { }
        public ConfigurationRestoreException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion

    #region Extension Methods

    /// <summary>
    /// Extension methods for ConfigurationBuilder;
    /// </summary>
    public static class ConfigurationBuilderExtensions
    {
        /// <summary>
        /// Adds default configuration sources for NEDA;
        /// </summary>
        public static IConfigurationBuilder AddDefaultSources(this IConfigurationBuilder builder,
            string environmentName, ConfigLoadOptions options)
        {
            // Base configuration;
            builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            // Environment-specific configuration;
            if (!string.IsNullOrEmpty(environmentName))
            {
                builder.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);
            }

            // Additional configuration files;
            if (options.AdditionalConfigFiles != null)
            {
                foreach (var file in options.AdditionalConfigFiles)
                {
                    builder.AddJsonFile(file, optional: true, reloadOnChange: true);
                }
            }

            return builder;
        }
    }

    #endregion
}
