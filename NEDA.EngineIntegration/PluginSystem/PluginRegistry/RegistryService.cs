using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.DependencyInjection;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.PluginSystem.PluginSDK;
using NEDA.PluginSystem.PluginManager;
using NEDA.PluginSystem.PluginLoader;

namespace NEDA.PluginSystem.PluginRegistry
{
    /// <summary>
    /// Registry service configuration;
    /// </summary>
    public class RegistryServiceConfig;
    {
        [JsonProperty("registryFilePath")]
        public string RegistryFilePath { get; set; } = "plugins/registry.json";

        [JsonProperty("autoSaveIntervalMinutes")]
        public int AutoSaveIntervalMinutes { get; set; } = 5;

        [JsonProperty("maxPluginLoadAttempts")]
        public int MaxPluginLoadAttempts { get; set; } = 3;

        [JsonProperty("pluginHealthCheckIntervalSeconds")]
        public int PluginHealthCheckIntervalSeconds { get; set; } = 30;

        [JsonProperty("dependencyResolutionTimeoutSeconds")]
        public int DependencyResolutionTimeoutSeconds { get; set; } = 60;

        [JsonProperty("enableAutoDiscovery")]
        public bool EnableAutoDiscovery { get; set; } = true;

        [JsonProperty("pluginScanPaths")]
        public List<string> PluginScanPaths { get; set; } = new List<string>
        {
            "plugins/",
            "extensions/"
        };

        [JsonProperty("blacklistedPlugins")]
        public List<string> BlacklistedPlugins { get; set; } = new List<string>();

        [JsonProperty("whitelistedPlugins")]
        public List<string> WhitelistedPlugins { get; set; } = new List<string>();

        [JsonProperty("enableRemoteRegistry")]
        public bool EnableRemoteRegistry { get; set; } = false;

        [JsonProperty("remoteRegistryUrl")]
        public string RemoteRegistryUrl { get; set; }

        [JsonProperty("cacheRegistryInMemory")]
        public bool CacheRegistryInMemory { get; set; } = true;

        [JsonProperty("registryBackupCount")]
        public int RegistryBackupCount { get; set; } = 5;

        [JsonProperty("enableRegistryCompression")]
        public bool EnableRegistryCompression { get; set; } = true;

        [JsonProperty("pluginIsolationLevel")]
        public PluginIsolationLevel IsolationLevel { get; set; } = PluginIsolationLevel.Partial;

        [JsonProperty("signatureValidationRequired")]
        public bool SignatureValidationRequired { get; set; } = true;

        [JsonProperty("maxConcurrentPluginLoads")]
        public int MaxConcurrentPluginLoads { get; set; } = 4;
    }

    /// <summary>
    /// Plugin isolation level;
    /// </summary>
    public enum PluginIsolationLevel;
    {
        None = 0,
        Partial = 1,
        Full = 2,
        Sandboxed = 3;
    }

    /// <summary>
    /// Registry statistics;
    /// </summary>
    public class RegistryStatistics;
    {
        [JsonProperty("totalPlugins")]
        public int TotalPlugins { get; set; }

        [JsonProperty("enabledPlugins")]
        public int EnabledPlugins { get; set; }

        [JsonProperty("disabledPlugins")]
        public int DisabledPlugins { get; set; }

        [JsonProperty("healthyPlugins")]
        public int HealthyPlugins { get; set; }

        [JsonProperty("errorPlugins")]
        public int ErrorPlugins { get; set; }

        [JsonProperty("totalCategories")]
        public int TotalCategories { get; set; }

        [JsonProperty("registrySizeBytes")]
        public long RegistrySizeBytes { get; set; }

        [JsonProperty("lastUpdateTime")]
        public DateTime LastUpdateTime { get; set; }

        [JsonProperty("loadTimeMs")]
        public long LoadTimeMs { get; set; }

        [JsonProperty("averagePluginLoadTimeMs")]
        public double AveragePluginLoadTimeMs { get; set; }

        [JsonProperty("dependencyGraphComplexity")]
        public int DependencyGraphComplexity { get; set; }
    }

    /// <summary>
    /// Plugin discovery result;
    /// </summary>
    public class PluginDiscoveryResult;
    {
        [JsonProperty("discoveredCount")]
        public int DiscoveredCount { get; set; }

        [JsonProperty("loadedCount")]
        public int LoadedCount { get; set; }

        [JsonProperty("failedCount")]
        public int FailedCount { get; set; }

        [JsonProperty("skippedCount")]
        public int SkippedCount { get; set; }

        [JsonProperty("discoveryTimeMs")]
        public long DiscoveryTimeMs { get; set; }

        [JsonProperty("discoveredPlugins")]
        public List<string> DiscoveredPlugins { get; set; } = new List<string>();

        [JsonProperty("failedPlugins")]
        public List<PluginLoadFailure> FailedPlugins { get; set; } = new List<PluginLoadFailure>();
    }

    /// <summary>
    /// Plugin load failure information;
    /// </summary>
    public class PluginLoadFailure;
    {
        [JsonProperty("pluginPath")]
        public string PluginPath { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonProperty("errorType")]
        public string ErrorType { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonProperty("retryCount")]
        public int RetryCount { get; set; }
    }

    /// <summary>
    /// Advanced registry service for managing plugin lifecycle, discovery, and coordination;
    /// </summary>
    public class RegistryService : IRegistryService, IDisposable;
    {
        private readonly IPluginRegistry _pluginRegistry
        private readonly IPluginLoader _pluginLoader;
        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly RegistryServiceConfig _config;
        private readonly ConcurrentDictionary<string, PluginDiscoveryResult> _discoveryResults;
        private readonly ConcurrentQueue<PluginLoadFailure> _loadFailures;
        private readonly Timer _autoSaveTimer;
        private readonly Timer _healthCheckTimer;
        private readonly SemaphoreSlim _loadSemaphore;
        private readonly object _syncLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;
        private RegistryStatistics _statistics;
        private string _registryBackupPath;

        /// <summary>
        /// Event fired when registry is saved;
        /// </summary>
        public event EventHandler<RegistryEventArgs> RegistrySaved;

        /// <summary>
        /// Event fired when registry is loaded;
        /// </summary>
        public event EventHandler<RegistryEventArgs> RegistryLoaded;

        /// <summary>
        /// Event fired when plugin discovery completes;
        /// </summary>
        public event EventHandler<PluginDiscoveryEventArgs> PluginDiscoveryCompleted;

        /// <summary>
        /// Event fired when registry statistics are updated;
        /// </summary>
        public event EventHandler<RegistryStatisticsEventArgs> RegistryStatisticsUpdated;

        /// <summary>
        /// Initializes a new instance of RegistryService;
        /// </summary>
        public RegistryService(
            IPluginRegistry pluginRegistry,
            IPluginLoader pluginLoader,
            ILogger logger,
            IServiceProvider serviceProvider)
        {
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            _pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            // Load configuration;
            _config = LoadConfiguration();

            // Initialize collections;
            _discoveryResults = new ConcurrentDictionary<string, PluginDiscoveryResult>();
            _loadFailures = new ConcurrentQueue<PluginLoadFailure>();
            _loadSemaphore = new SemaphoreSlim(_config.MaxConcurrentPluginLoads);
            _statistics = new RegistryStatistics();

            // Setup backup path;
            _registryBackupPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(_config.RegistryFilePath) ?? "plugins",
                "backups"
            );

            // Initialize timers;
            _autoSaveTimer = new Timer(AutoSaveCallback, null,
                TimeSpan.FromMinutes(_config.AutoSaveIntervalMinutes),
                TimeSpan.FromMinutes(_config.AutoSaveIntervalMinutes));

            _healthCheckTimer = new Timer(HealthCheckCallback, null,
                TimeSpan.FromSeconds(_config.PluginHealthCheckIntervalSeconds),
                TimeSpan.FromSeconds(_config.PluginHealthCheckIntervalSeconds));

            _logger.Info($"RegistryService initialized with config: {JsonConvert.SerializeObject(_config)}");
        }

        /// <summary>
        /// Initializes the registry service;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.Info("Initializing RegistryService...");

                // Create directories if they don't exist;
                EnsureDirectories();

                // Load existing registry
                await LoadRegistryAsync();

                // Auto-discover plugins if enabled;
                if (_config.EnableAutoDiscovery)
                {
                    await DiscoverAndLoadPluginsAsync();
                }

                // Update statistics;
                await UpdateStatisticsAsync();

                _isInitialized = true;
                _logger.Info("RegistryService initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"RegistryService initialization failed: {ex.Message}", ex);
                throw new RegistryServiceException("Failed to initialize RegistryService", ex);
            }
        }

        /// <summary>
        /// Discovers and loads plugins from configured scan paths;
        /// </summary>
        public async Task<PluginDiscoveryResult> DiscoverAndLoadPluginsAsync()
        {
            var discoveryId = Guid.NewGuid().ToString();
            var result = new PluginDiscoveryResult;
            {
                DiscoveredPlugins = new List<string>(),
                FailedPlugins = new List<PluginLoadFailure>()
            };

            var startTime = DateTime.UtcNow;

            try
            {
                _logger.Info($"Starting plugin discovery (ID: {discoveryId})");

                var discoveredAssemblies = new List<string>();

                // Scan all configured paths;
                foreach (var scanPath in _config.PluginScanPaths)
                {
                    var fullPath = ResolvePath(scanPath);
                    if (!System.IO.Directory.Exists(fullPath))
                    {
                        _logger.Debug($"Scan path does not exist: {fullPath}");
                        continue;
                    }

                    var assemblies = await ScanDirectoryForPluginsAsync(fullPath);
                    discoveredAssemblies.AddRange(assemblies);
                }

                result.DiscoveredCount = discoveredAssemblies.Count;
                _logger.Info($"Discovered {result.DiscoveredCount} potential plugin assemblies");

                // Load discovered plugins;
                var loadTasks = new List<Task<PluginLoadResult>>();
                foreach (var assemblyPath in discoveredAssemblies)
                {
                    loadTasks.Add(LoadPluginFromAssemblyAsync(assemblyPath, result));
                }

                // Wait for all loads to complete with timeout;
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_config.DependencyResolutionTimeoutSeconds));
                var loadTask = Task.WhenAll(loadTasks);

                var completedTask = await Task.WhenAny(loadTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException("Plugin loading timed out");
                }

                var loadResults = await loadTask;
                result.LoadedCount = loadResults.Count(r => r.Success);
                result.FailedCount = loadResults.Count(r => !r.Success);

                // Update discovery time;
                result.DiscoveryTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

                // Store result;
                _discoveryResults.TryAdd(discoveryId, result);

                // Fire event;
                OnPluginDiscoveryCompleted(new PluginDiscoveryEventArgs(discoveryId, result));

                _logger.Info($"Plugin discovery completed: {result.LoadedCount} loaded, {result.FailedCount} failed");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Plugin discovery failed: {ex.Message}", ex);
                result.FailedCount++;
                result.DiscoveryTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                throw new PluginDiscoveryException("Plugin discovery failed", ex);
            }
        }

        /// <summary>
        /// Loads a specific plugin assembly;
        /// </summary>
        public async Task<PluginLoadResult> LoadPluginAsync(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new ArgumentException("Assembly path cannot be null or empty", nameof(assemblyPath));

            await _loadSemaphore.WaitAsync();

            try
            {
                _logger.Info($"Loading plugin from: {assemblyPath}");

                // Check if plugin is blacklisted;
                var pluginName = System.IO.Path.GetFileNameWithoutExtension(assemblyPath);
                if (_config.BlacklistedPlugins.Contains(pluginName, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.Warn($"Plugin {pluginName} is blacklisted, skipping");
                    return new PluginLoadResult;
                    {
                        Success = false,
                        ErrorMessage = "Plugin is blacklisted",
                        PluginId = null;
                    };
                }

                // Check whitelist if configured;
                if (_config.WhitelistedPlugins.Any() &&
                    !_config.WhitelistedPlugins.Contains(pluginName, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.Warn($"Plugin {pluginName} is not in whitelist, skipping");
                    return new PluginLoadResult;
                    {
                        Success = false,
                        ErrorMessage = "Plugin is not in whitelist",
                        PluginId = null;
                    };
                }

                // Load assembly and plugin;
                var loadResult = await _pluginLoader.LoadPluginAsync(assemblyPath, _config.IsolationLevel);
                if (!loadResult.Success)
                {
                    RecordLoadFailure(assemblyPath, loadResult.ErrorMessage, loadResult.ErrorType);
                    return loadResult;
                }

                // Register plugin;
                var registerResult = _pluginRegistry.RegisterPlugin(
                    loadResult.Metadata,
                    loadResult.Assembly,
                    loadResult.PluginInstance;
                );

                if (registerResult != PluginRegistrationResult.Success)
                {
                    var errorMsg = $"Failed to register plugin: {registerResult}";
                    RecordLoadFailure(assemblyPath, errorMsg, registerResult.ToString());

                    return new PluginLoadResult;
                    {
                        Success = false,
                        ErrorMessage = errorMsg,
                        ErrorType = "RegistrationFailed",
                        PluginId = loadResult.Metadata?.Id;
                    };
                }

                _logger.Info($"Plugin {loadResult.Metadata.Name} loaded successfully");

                // Update statistics;
                await UpdateStatisticsAsync();

                return loadResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load plugin {assemblyPath}: {ex.Message}", ex);
                RecordLoadFailure(assemblyPath, ex.Message, ex.GetType().Name);

                return new PluginLoadResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ErrorType = ex.GetType().Name,
                    PluginId = null;
                };
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        /// <summary>
        /// Unloads a plugin;
        /// </summary>
        public async Task<PluginUnloadResult> UnloadPluginAsync(string pluginId, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID cannot be null or empty", nameof(pluginId));

            try
            {
                _logger.Info($"Unloading plugin: {pluginId}");

                var unregisterResult = _pluginRegistry.UnregisterPlugin(pluginId, force);
                if (unregisterResult != PluginUnregistrationResult.Success)
                {
                    return new PluginUnloadResult;
                    {
                        Success = false,
                        ErrorMessage = $"Unregistration failed: {unregisterResult}",
                        PluginId = pluginId;
                    };
                }

                // Unload assembly from loader;
                var unloadResult = await _pluginLoader.UnloadPluginAsync(pluginId);
                if (!unloadResult.Success)
                {
                    _logger.Warn($"Plugin {pluginId} unregistered but assembly unload failed: {unloadResult.ErrorMessage}");
                }

                // Update statistics;
                await UpdateStatisticsAsync();

                _logger.Info($"Plugin {pluginId} unloaded successfully");

                return new PluginUnloadResult;
                {
                    Success = true,
                    PluginId = pluginId;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to unload plugin {pluginId}: {ex.Message}", ex);
                return new PluginUnloadResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    PluginId = pluginId;
                };
            }
        }

        /// <summary>
        /// Reloads a plugin;
        /// </summary>
        public async Task<PluginReloadResult> ReloadPluginAsync(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID cannot be null or empty", nameof(pluginId));

            try
            {
                _logger.Info($"Reloading plugin: {pluginId}");

                // Get plugin metadata before unloading;
                var metadata = _pluginRegistry.GetPluginMetadata(pluginId);
                if (metadata == null)
                {
                    return new PluginReloadResult;
                    {
                        Success = false,
                        ErrorMessage = $"Plugin {pluginId} not found",
                        PluginId = pluginId;
                    };
                }

                // Unload plugin;
                var unloadResult = await UnloadPluginAsync(pluginId);
                if (!unloadResult.Success)
                {
                    return new PluginReloadResult;
                    {
                        Success = false,
                        ErrorMessage = $"Unload failed: {unloadResult.ErrorMessage}",
                        PluginId = pluginId;
                    };
                }

                // Wait a bit to ensure clean unload;
                await Task.Delay(100);

                // Reload plugin;
                var loadResult = await LoadPluginAsync(metadata.AssemblyPath);
                if (!loadResult.Success)
                {
                    return new PluginReloadResult;
                    {
                        Success = false,
                        ErrorMessage = $"Reload failed: {loadResult.ErrorMessage}",
                        PluginId = pluginId;
                    };
                }

                _logger.Info($"Plugin {pluginId} reloaded successfully");

                return new PluginReloadResult;
                {
                    Success = true,
                    PluginId = pluginId,
                    NewPluginId = loadResult.PluginId;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to reload plugin {pluginId}: {ex.Message}", ex);
                return new PluginReloadResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    PluginId = pluginId;
                };
            }
        }

        /// <summary>
        /// Gets registry statistics;
        /// </summary>
        public RegistryStatistics GetStatistics()
        {
            lock (_syncLock)
            {
                return new RegistryStatistics;
                {
                    TotalPlugins = _statistics.TotalPlugins,
                    EnabledPlugins = _statistics.EnabledPlugins,
                    DisabledPlugins = _statistics.DisabledPlugins,
                    HealthyPlugins = _statistics.HealthyPlugins,
                    ErrorPlugins = _statistics.ErrorPlugins,
                    TotalCategories = _statistics.TotalCategories,
                    RegistrySizeBytes = _statistics.RegistrySizeBytes,
                    LastUpdateTime = _statistics.LastUpdateTime,
                    LoadTimeMs = _statistics.LoadTimeMs,
                    AveragePluginLoadTimeMs = _statistics.AveragePluginLoadTimeMs,
                    DependencyGraphComplexity = _statistics.DependencyGraphComplexity;
                };
            }
        }

        /// <summary>
        /// Gets plugin discovery results;
        /// </summary>
        public IReadOnlyDictionary<string, PluginDiscoveryResult> GetDiscoveryResults()
        {
            return new Dictionary<string, PluginDiscoveryResult>(_discoveryResults);
        }

        /// <summary>
        /// Gets load failures;
        /// </summary>
        public IReadOnlyList<PluginLoadFailure> GetLoadFailures(int maxCount = 100)
        {
            return _loadFailures.Take(maxCount).ToList().AsReadOnly();
        }

        /// <summary>
        /// Saves registry state;
        /// </summary>
        public async Task SaveRegistryAsync(bool createBackup = true)
        {
            try
            {
                _logger.Info("Saving registry state...");

                if (createBackup && System.IO.File.Exists(_config.RegistryFilePath))
                {
                    await CreateRegistryBackupAsync();
                }

                await _pluginRegistry.SaveRegistryAsync(_config.RegistryFilePath);

                // Update registry size;
                await UpdateRegistrySizeAsync();

                // Fire event;
                OnRegistrySaved(new RegistryEventArgs(_config.RegistryFilePath, DateTime.UtcNow));

                _logger.Info("Registry state saved successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save registry: {ex.Message}", ex);
                throw new RegistryServiceException("Failed to save registry", ex);
            }
        }

        /// <summary>
        /// Loads registry state;
        /// </summary>
        public async Task LoadRegistryAsync()
        {
            try
            {
                _logger.Info("Loading registry state...");

                var startTime = DateTime.UtcNow;

                await _pluginRegistry.LoadRegistryAsync(_config.RegistryFilePath);

                _statistics.LoadTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

                // Update registry size;
                await UpdateRegistrySizeAsync();

                // Fire event;
                OnRegistryLoaded(new RegistryEventArgs(_config.RegistryFilePath, DateTime.UtcNow));

                _logger.Info("Registry state loaded successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load registry: {ex.Message}", ex);

                // Try to load from backup;
                await TryLoadFromBackupAsync();
            }
        }

        /// <summary>
        /// Clears all plugins from registry
        /// </summary>
        public async Task ClearRegistryAsync(bool shutdownPlugins = true)
        {
            try
            {
                _logger.Info("Clearing registry...");

                _pluginRegistry.ClearRegistry(shutdownPlugins);

                // Clear loader cache;
                await _pluginLoader.ClearCacheAsync();

                // Clear discovery results;
                _discoveryResults.Clear();

                // Clear load failures;
                while (_loadFailures.TryDequeue(out _)) { }

                // Update statistics;
                await UpdateStatisticsAsync();

                _logger.Info("Registry cleared successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to clear registry: {ex.Message}", ex);
                throw new RegistryServiceException("Failed to clear registry", ex);
            }
        }

        /// <summary>
        /// Validates registry integrity;
        /// </summary>
        public async Task<RegistryValidationResult> ValidateRegistryAsync()
        {
            var result = new RegistryValidationResult;
            {
                ValidationTime = DateTime.UtcNow,
                Issues = new List<RegistryValidationIssue>()
            };

            try
            {
                _logger.Info("Validating registry integrity...");

                var plugins = _pluginRegistry.GetAllPlugins(true).ToList();

                // Check for duplicate plugin IDs;
                var duplicateIds = plugins;
                    .GroupBy(p => p.Id)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                if (duplicateIds.Any())
                {
                    result.Issues.Add(new RegistryValidationIssue;
                    {
                        IssueType = ValidationIssueType.DuplicateId,
                        Message = $"Duplicate plugin IDs found: {string.Join(", ", duplicateIds)}",
                        Severity = ValidationSeverity.High;
                    });
                }

                // Check for missing dependencies;
                foreach (var plugin in plugins)
                {
                    foreach (var dependency in plugin.Dependencies)
                    {
                        if (!plugins.Any(p => p.Id == dependency))
                        {
                            result.Issues.Add(new RegistryValidationIssue;
                            {
                                IssueType = ValidationIssueType.MissingDependency,
                                Message = $"Plugin {plugin.Id} has missing dependency: {dependency}",
                                Severity = ValidationSeverity.Medium,
                                PluginId = plugin.Id;
                            });
                        }
                    }
                }

                // Check for circular dependencies;
                try
                {
                    _pluginRegistry.ResolveLoadOrder();
                }
                catch (PluginDependencyCycleException ex)
                {
                    result.Issues.Add(new RegistryValidationIssue;
                    {
                        IssueType = ValidationIssueType.CircularDependency,
                        Message = ex.Message,
                        Severity = ValidationSeverity.High;
                    });
                }

                // Check assembly file existence;
                foreach (var plugin in plugins.Where(p => !string.IsNullOrEmpty(p.AssemblyPath)))
                {
                    if (!System.IO.File.Exists(plugin.AssemblyPath))
                    {
                        result.Issues.Add(new RegistryValidationIssue;
                        {
                            IssueType = ValidationIssueType.MissingAssembly,
                            Message = $"Plugin {plugin.Id} assembly not found: {plugin.AssemblyPath}",
                            Severity = ValidationSeverity.Medium,
                            PluginId = plugin.Id;
                        });
                    }
                }

                result.IsValid = !result.Issues.Any(i => i.Severity == ValidationSeverity.High);
                result.TotalIssues = result.Issues.Count;

                _logger.Info($"Registry validation completed: {result.TotalIssues} issues found");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Registry validation failed: {ex.Message}", ex);
                result.Issues.Add(new RegistryValidationIssue;
                {
                    IssueType = ValidationIssueType.ValidationError,
                    Message = $"Validation failed: {ex.Message}",
                    Severity = ValidationSeverity.High;
                });
                result.IsValid = false;
                return result;
            }
        }

        /// <summary>
        /// Exports registry data;
        /// </summary>
        public async Task<RegistryExport> ExportRegistryAsync(ExportFormat format = ExportFormat.Json)
        {
            try
            {
                _logger.Info($"Exporting registry in {format} format...");

                var plugins = _pluginRegistry.GetAllPlugins(true).ToList();
                var statistics = GetStatistics();
                var discoveryResults = GetDiscoveryResults();
                var loadFailures = GetLoadFailures();

                var export = new RegistryExport;
                {
                    ExportTime = DateTime.UtcNow,
                    Format = format,
                    PluginCount = plugins.Count,
                    Statistics = statistics;
                };

                switch (format)
                {
                    case ExportFormat.Json:
                        export.Data = JsonConvert.SerializeObject(new;
                        {
                            Plugins = plugins,
                            Statistics = statistics,
                            DiscoveryResults = discoveryResults,
                            LoadFailures = loadFailures,
                            DependencyGraph = _pluginRegistry.GetDependencyGraph()
                        }, Formatting.Indented);
                        break;

                    case ExportFormat.Xml:
                        // XML serialization implementation;
                        var xmlSerializer = new System.Xml.Serialization.XmlSerializer(typeof(List<PluginMetadata>));
                        using (var writer = new System.IO.StringWriter())
                        {
                            xmlSerializer.Serialize(writer, plugins);
                            export.Data = writer.ToString();
                        }
                        break;

                    case ExportFormat.Csv:
                        // CSV implementation;
                        var csvLines = new List<string>
                        {
                            "Id,Name,Version,Author,IsEnabled,HealthStatus,ErrorCount"
                        };

                        foreach (var plugin in plugins)
                        {
                            csvLines.Add($"\"{plugin.Id}\",\"{plugin.Name}\",\"{plugin.Version}\",\"{plugin.Author}\",{plugin.IsEnabled},{plugin.HealthStatus},{plugin.ErrorCount}");
                        }

                        export.Data = string.Join(Environment.NewLine, csvLines);
                        break;
                }

                _logger.Info("Registry export completed successfully");

                return export;
            }
            catch (Exception ex)
            {
                _logger.Error($"Registry export failed: {ex.Message}", ex);
                throw new RegistryServiceException("Failed to export registry", ex);
            }
        }

        /// <summary>
        /// Imports registry data;
        /// </summary>
        public async Task<RegistryImportResult> ImportRegistryAsync(string importData, ImportFormat format = ImportFormat.Json)
        {
            try
            {
                _logger.Info($"Importing registry in {format} format...");

                var result = new RegistryImportResult;
                {
                    ImportTime = DateTime.UtcNow,
                    Format = format;
                };

                // Validate import data;
                if (string.IsNullOrWhiteSpace(importData))
                {
                    result.Success = false;
                    result.ErrorMessage = "Import data is empty";
                    return result;
                }

                List<PluginMetadata> importedPlugins;

                switch (format)
                {
                    case ImportFormat.Json:
                        importedPlugins = JsonConvert.DeserializeObject<List<PluginMetadata>>(importData);
                        break;

                    case ImportFormat.Xml:
                        var xmlSerializer = new System.Xml.Serialization.XmlSerializer(typeof(List<PluginMetadata>));
                        using (var reader = new System.IO.StringReader(importData))
                        {
                            importedPlugins = (List<PluginMetadata>)xmlSerializer.Deserialize(reader);
                        }
                        break;

                    default:
                        throw new NotSupportedException($"Import format {format} is not supported");
                }

                if (importedPlugins == null || !importedPlugins.Any())
                {
                    result.Success = false;
                    result.ErrorMessage = "No plugins found in import data";
                    return result;
                }

                // Clear current registry
                await ClearRegistryAsync(true);

                // Import plugins;
                foreach (var plugin in importedPlugins)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(plugin.AssemblyPath) && System.IO.File.Exists(plugin.AssemblyPath))
                        {
                            await LoadPluginAsync(plugin.AssemblyPath);
                            result.ImportedCount++;
                        }
                        else;
                        {
                            result.SkippedCount++;
                            _logger.Warn($"Skipping plugin {plugin.Id}: Assembly not found at {plugin.AssemblyPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        _logger.Error($"Failed to import plugin {plugin.Id}: {ex.Message}");
                    }
                }

                result.Success = true;
                result.TotalPlugins = importedPlugins.Count;

                // Save imported registry
                await SaveRegistryAsync();

                _logger.Info($"Registry import completed: {result.ImportedCount} imported, {result.FailedCount} failed");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Registry import failed: {ex.Message}", ex);
                return new RegistryImportResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ImportTime = DateTime.UtcNow,
                    Format = format;
                };
            }
        }

        /// <summary>
        /// Gets plugin by ID;
        /// </summary>
        public PluginMetadata GetPlugin(string pluginId)
        {
            return _pluginRegistry.GetPluginMetadata(pluginId);
        }

        /// <summary>
        /// Gets all plugins;
        /// </summary>
        public IReadOnlyCollection<PluginMetadata> GetAllPlugins(bool includeDisabled = false)
        {
            return _pluginRegistry.GetAllPlugins(includeDisabled);
        }

        /// <summary>
        /// Updates plugin configuration;
        /// </summary>
        public async Task<bool> UpdatePluginConfigAsync(string pluginId, JObject config)
        {
            try
            {
                _logger.Info($"Updating configuration for plugin: {pluginId}");

                var success = _pluginRegistry.UpdatePluginMetadata(pluginId, metadata =>
                {
                    // Store configuration in metadata or separate storage;
                    metadata.ConfigurationSchema = config.ToString();
                });

                if (success)
                {
                    await SaveRegistryAsync();
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update plugin config {pluginId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Performs health check on all plugins;
        /// </summary>
        public async Task<PluginHealthReport> PerformHealthCheckAsync()
        {
            var report = new PluginHealthReport;
            {
                CheckTime = DateTime.UtcNow,
                PluginStatuses = new Dictionary<string, PluginHealthStatus>()
            };

            try
            {
                _logger.Info("Performing plugin health check...");

                var plugins = _pluginRegistry.GetAllPlugins().ToList();

                foreach (var plugin in plugins)
                {
                    try
                    {
                        // Check if plugin instance is still valid;
                        if (plugin.PluginInstance == null)
                        {
                            report.PluginStatuses[plugin.Id] = PluginHealthStatus.Error;
                            _pluginRegistry.ReportPluginHealth(plugin.Id, PluginHealthStatus.Error, "Plugin instance is null");
                            continue;
                        }

                        // Optional: Call plugin's health check method if available;
                        var healthCheckable = plugin.PluginInstance as IHealthCheckable;
                        if (healthCheckable != null)
                        {
                            var pluginHealth = await healthCheckable.CheckHealthAsync();
                            report.PluginStatuses[plugin.Id] = pluginHealth;
                            _pluginRegistry.ReportPluginHealth(plugin.Id, pluginHealth);
                        }
                        else;
                        {
                            // Assume healthy if no health check method;
                            report.PluginStatuses[plugin.Id] = PluginHealthStatus.Healthy;
                        }
                    }
                    catch (Exception ex)
                    {
                        report.PluginStatuses[plugin.Id] = PluginHealthStatus.Error;
                        _pluginRegistry.ReportPluginHealth(plugin.Id, PluginHealthStatus.Error, ex.Message);
                        _logger.Error($"Health check failed for plugin {plugin.Id}: {ex.Message}", ex);
                    }
                }

                report.HealthyCount = report.PluginStatuses.Count(s => s.Value == PluginHealthStatus.Healthy);
                report.ErrorCount = report.PluginStatuses.Count(s => s.Value == PluginHealthStatus.Error);
                report.WarningCount = report.PluginStatuses.Count(s => s.Value == PluginHealthStatus.Warning);

                _logger.Info($"Health check completed: {report.HealthyCount} healthy, {report.ErrorCount} errors");

                return report;
            }
            catch (Exception ex)
            {
                _logger.Error($"Health check failed: {ex.Message}", ex);
                throw new RegistryServiceException("Health check failed", ex);
            }
        }

        #region Private Methods;

        private RegistryServiceConfig LoadConfiguration()
        {
            try
            {
                var configManager = _serviceProvider.GetService<IConfigurationManager>();
                if (configManager != null)
                {
                    return configManager.GetSection<RegistryServiceConfig>("PluginRegistry") ?? new RegistryServiceConfig();
                }

                // Fallback to default config;
                return new RegistryServiceConfig();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load registry configuration: {ex.Message}. Using defaults.");
                return new RegistryServiceConfig();
            }
        }

        private void EnsureDirectories()
        {
            try
            {
                // Ensure plugin directories exist;
                foreach (var scanPath in _config.PluginScanPaths)
                {
                    var fullPath = ResolvePath(scanPath);
                    if (!System.IO.Directory.Exists(fullPath))
                    {
                        System.IO.Directory.CreateDirectory(fullPath);
                        _logger.Debug($"Created directory: {fullPath}");
                    }
                }

                // Ensure backup directory exists;
                if (!System.IO.Directory.Exists(_registryBackupPath))
                {
                    System.IO.Directory.CreateDirectory(_registryBackupPath);
                    _logger.Debug($"Created backup directory: {_registryBackupPath}");
                }

                // Ensure registry file directory exists;
                var registryDir = System.IO.Path.GetDirectoryName(_config.RegistryFilePath);
                if (!string.IsNullOrEmpty(registryDir) && !System.IO.Directory.Exists(registryDir))
                {
                    System.IO.Directory.CreateDirectory(registryDir);
                    _logger.Debug($"Created registry directory: {registryDir}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to ensure directories: {ex.Message}", ex);
            }
        }

        private string ResolvePath(string relativePath)
        {
            if (System.IO.Path.IsPathRooted(relativePath))
                return relativePath;

            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
        }

        private async Task<List<string>> ScanDirectoryForPluginsAsync(string directoryPath)
        {
            var assemblies = new List<string>();

            try
            {
                // Look for DLL files;
                var dllFiles = System.IO.Directory.GetFiles(directoryPath, "*.dll", System.IO.SearchOption.AllDirectories);
                assemblies.AddRange(dllFiles);

                // Look for EXE files;
                var exeFiles = System.IO.Directory.GetFiles(directoryPath, "*.exe", System.IO.SearchOption.AllDirectories);
                assemblies.AddRange(exeFiles);

                _logger.Debug($"Scanned {directoryPath}: found {assemblies.Count} assemblies");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to scan directory {directoryPath}: {ex.Message}", ex);
            }

            return await Task.FromResult(assemblies);
        }

        private async Task<PluginLoadResult> LoadPluginFromAssemblyAsync(string assemblyPath, PluginDiscoveryResult discoveryResult)
        {
            try
            {
                var loadResult = await LoadPluginAsync(assemblyPath);

                if (loadResult.Success)
                {
                    discoveryResult.DiscoveredPlugins.Add(loadResult.PluginId);
                }
                else;
                {
                    discoveryResult.FailedPlugins.Add(new PluginLoadFailure;
                    {
                        PluginPath = assemblyPath,
                        ErrorMessage = loadResult.ErrorMessage,
                        ErrorType = loadResult.ErrorType,
                        Timestamp = DateTime.UtcNow,
                        RetryCount = 1;
                    });
                }

                return loadResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load plugin from {assemblyPath}: {ex.Message}", ex);

                discoveryResult.FailedPlugins.Add(new PluginLoadFailure;
                {
                    PluginPath = assemblyPath,
                    ErrorMessage = ex.Message,
                    ErrorType = ex.GetType().Name,
                    Timestamp = DateTime.UtcNow,
                    RetryCount = 1;
                });

                return new PluginLoadResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ErrorType = ex.GetType().Name,
                    PluginId = null;
                };
            }
        }

        private void RecordLoadFailure(string assemblyPath, string errorMessage, string errorType)
        {
            var failure = new PluginLoadFailure;
            {
                PluginPath = assemblyPath,
                ErrorMessage = errorMessage,
                ErrorType = errorType,
                Timestamp = DateTime.UtcNow,
                RetryCount = 1;
            };

            _loadFailures.Enqueue(failure);

            // Keep only last 1000 failures;
            while (_loadFailures.Count > 1000)
            {
                _loadFailures.TryDequeue(out _);
            }
        }

        private async Task UpdateStatisticsAsync()
        {
            try
            {
                var plugins = _pluginRegistry.GetAllPlugins(true).ToList();
                var categories = new HashSet<string>();

                foreach (var plugin in plugins)
                {
                    if (!string.IsNullOrEmpty(plugin.Category))
                    {
                        categories.Add(plugin.Category);
                    }
                }

                var dependencyGraph = _pluginRegistry.GetDependencyGraph();
                var complexity = dependencyGraph.Sum(kvp => kvp.Value.Count);

                lock (_syncLock)
                {
                    _statistics.TotalPlugins = plugins.Count;
                    _statistics.EnabledPlugins = plugins.Count(p => p.IsEnabled);
                    _statistics.DisabledPlugins = plugins.Count(p => !p.IsEnabled);
                    _statistics.HealthyPlugins = plugins.Count(p => p.HealthStatus == PluginHealthStatus.Healthy);
                    _statistics.ErrorPlugins = plugins.Count(p => p.HealthStatus == PluginHealthStatus.Error);
                    _statistics.TotalCategories = categories.Count;
                    _statistics.DependencyGraphComplexity = complexity;
                    _statistics.LastUpdateTime = DateTime.UtcNow;
                }

                // Update registry size;
                await UpdateRegistrySizeAsync();

                // Fire event;
                OnRegistryStatisticsUpdated(new RegistryStatisticsEventArgs(GetStatistics()));
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update statistics: {ex.Message}", ex);
            }
        }

        private async Task UpdateRegistrySizeAsync()
        {
            try
            {
                if (System.IO.File.Exists(_config.RegistryFilePath))
                {
                    var fileInfo = new System.IO.FileInfo(_config.RegistryFilePath);
                    lock (_syncLock)
                    {
                        _statistics.RegistrySizeBytes = fileInfo.Length;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update registry size: {ex.Message}", ex);
            }
        }

        private async Task CreateRegistryBackupAsync()
        {
            try
            {
                if (!System.IO.File.Exists(_config.RegistryFilePath))
                    return;

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var backupFileName = $"registry_backup_{timestamp}.json";
                var backupPath = System.IO.Path.Combine(_registryBackupPath, backupFileName);

                System.IO.File.Copy(_config.RegistryFilePath, backupPath, true);

                // Clean up old backups;
                await CleanupOldBackupsAsync();

                _logger.Debug($"Registry backup created: {backupPath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create registry backup: {ex.Message}", ex);
            }
        }

        private async Task CleanupOldBackupsAsync()
        {
            try
            {
                if (!System.IO.Directory.Exists(_registryBackupPath))
                    return;

                var backupFiles = System.IO.Directory.GetFiles(_registryBackupPath, "registry_backup_*.json")
                    .Select(f => new System.IO.FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ToList();

                if (backupFiles.Count > _config.RegistryBackupCount)
                {
                    var filesToDelete = backupFiles.Skip(_config.RegistryBackupCount);
                    foreach (var file in filesToDelete)
                    {
                        try
                        {
                            file.Delete();
                            _logger.Debug($"Deleted old backup: {file.Name}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"Failed to delete backup {file.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to cleanup old backups: {ex.Message}", ex);
            }
        }

        private async Task TryLoadFromBackupAsync()
        {
            try
            {
                if (!System.IO.Directory.Exists(_registryBackupPath))
                    return;

                var backupFiles = System.IO.Directory.GetFiles(_registryBackupPath, "registry_backup_*.json")
                    .Select(f => new System.IO.FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ToList();

                if (backupFiles.Any())
                {
                    var latestBackup = backupFiles.First().FullName;
                    _logger.Info($"Attempting to load from backup: {latestBackup}");

                    // Copy backup to registry file;
                    System.IO.File.Copy(latestBackup, _config.RegistryFilePath, true);

                    // Load from the copied file;
                    await _pluginRegistry.LoadRegistryAsync(_config.RegistryFilePath);

                    _logger.Info("Registry loaded from backup successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load from backup: {ex.Message}", ex);
                throw new RegistryServiceException("Failed to load registry from backup", ex);
            }
        }

        private async void AutoSaveCallback(object state)
        {
            try
            {
                if (_isDisposed || !_isInitialized)
                    return;

                await SaveRegistryAsync(createBackup: true);
            }
            catch (Exception ex)
            {
                _logger.Error($"Auto-save failed: {ex.Message}", ex);
            }
        }

        private async void HealthCheckCallback(object state)
        {
            try
            {
                if (_isDisposed || !_isInitialized)
                    return;

                await PerformHealthCheckAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Health check callback failed: {ex.Message}", ex);
            }
        }

        private void OnRegistrySaved(RegistryEventArgs e)
        {
            RegistrySaved?.Invoke(this, e);
        }

        private void OnRegistryLoaded(RegistryEventArgs e)
        {
            RegistryLoaded?.Invoke(this, e);
        }

        private void OnPluginDiscoveryCompleted(PluginDiscoveryEventArgs e)
        {
            PluginDiscoveryCompleted?.Invoke(this, e);
        }

        private void OnRegistryStatisticsUpdated(RegistryStatisticsEventArgs e)
        {
            RegistryStatisticsUpdated?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Stop timers;
                    _autoSaveTimer?.Dispose();
                    _healthCheckTimer?.Dispose();
                    _loadSemaphore?.Dispose();

                    // Save registry before disposal;
                    try
                    {
                        SaveRegistryAsync().Wait(5000);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to save registry on disposal: {ex.Message}", ex);
                    }

                    // Clear resources;
                    _pluginRegistry?.Dispose();

                    _logger.Info("RegistryService disposed");
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Registry service interface;
    /// </summary>
    public interface IRegistryService : IDisposable
    {
        event EventHandler<RegistryEventArgs> RegistrySaved;
        event EventHandler<RegistryEventArgs> RegistryLoaded;
        event EventHandler<PluginDiscoveryEventArgs> PluginDiscoveryCompleted;
        event EventHandler<RegistryStatisticsEventArgs> RegistryStatisticsUpdated;

        Task InitializeAsync();
        Task<PluginDiscoveryResult> DiscoverAndLoadPluginsAsync();
        Task<PluginLoadResult> LoadPluginAsync(string assemblyPath);
        Task<PluginUnloadResult> UnloadPluginAsync(string pluginId, bool force = false);
        Task<PluginReloadResult> ReloadPluginAsync(string pluginId);
        RegistryStatistics GetStatistics();
        IReadOnlyDictionary<string, PluginDiscoveryResult> GetDiscoveryResults();
        IReadOnlyList<PluginLoadFailure> GetLoadFailures(int maxCount = 100);
        Task SaveRegistryAsync(bool createBackup = true);
        Task LoadRegistryAsync();
        Task ClearRegistryAsync(bool shutdownPlugins = true);
        Task<RegistryValidationResult> ValidateRegistryAsync();
        Task<RegistryExport> ExportRegistryAsync(ExportFormat format = ExportFormat.Json);
        Task<RegistryImportResult> ImportRegistryAsync(string importData, ImportFormat format = ImportFormat.Json);
        PluginMetadata GetPlugin(string pluginId);
        IReadOnlyCollection<PluginMetadata> GetAllPlugins(bool includeDisabled = false);
        Task<bool> UpdatePluginConfigAsync(string pluginId, JObject config);
        Task<PluginHealthReport> PerformHealthCheckAsync();
    }

    /// <summary>
    /// Plugin load result;
    /// </summary>
    public class PluginLoadResult;
    {
        public bool Success { get; set; }
        public string PluginId { get; set; }
        public PluginMetadata Metadata { get; set; }
        public Assembly Assembly { get; set; }
        public IPlugin PluginInstance { get; set; }
        public string ErrorMessage { get; set; }
        public string ErrorType { get; set; }
        public TimeSpan LoadTime { get; set; }
    }

    /// <summary>
    /// Plugin unload result;
    /// </summary>
    public class PluginUnloadResult;
    {
        public bool Success { get; set; }
        public string PluginId { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan UnloadTime { get; set; }
    }

    /// <summary>
    /// Plugin reload result;
    /// </summary>
    public class PluginReloadResult;
    {
        public bool Success { get; set; }
        public string PluginId { get; set; }
        public string NewPluginId { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ReloadTime { get; set; }
    }

    /// <summary>
    /// Registry validation result;
    /// </summary>
    public class RegistryValidationResult;
    {
        public bool IsValid { get; set; }
        public int TotalIssues { get; set; }
        public List<RegistryValidationIssue> Issues { get; set; }
        public DateTime ValidationTime { get; set; }
    }

    /// <summary>
    /// Registry validation issue;
    /// </summary>
    public class RegistryValidationIssue;
    {
        public ValidationIssueType IssueType { get; set; }
        public string Message { get; set; }
        public ValidationSeverity Severity { get; set; }
        public string PluginId { get; set; }
    }

    /// <summary>
    /// Validation issue type;
    /// </summary>
    public enum ValidationIssueType;
    {
        DuplicateId = 1,
        MissingDependency = 2,
        CircularDependency = 3,
        MissingAssembly = 4,
        ValidationError = 5;
    }

    /// <summary>
    /// Validation severity;
    /// </summary>
    public enum ValidationSeverity;
    {
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    /// <summary>
    /// Registry export;
    /// </summary>
    public class RegistryExport;
    {
        public string Data { get; set; }
        public ExportFormat Format { get; set; }
        public int PluginCount { get; set; }
        public RegistryStatistics Statistics { get; set; }
        public DateTime ExportTime { get; set; }
    }

    /// <summary>
    /// Registry import result;
    /// </summary>
    public class RegistryImportResult;
    {
        public bool Success { get; set; }
        public int ImportedCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public int TotalPlugins { get; set; }
        public string ErrorMessage { get; set; }
        public ImportFormat Format { get; set; }
        public DateTime ImportTime { get; set; }
    }

    /// <summary>
    /// Plugin health report;
    /// </summary>
    public class PluginHealthReport;
    {
        public DateTime CheckTime { get; set; }
        public int HealthyCount { get; set; }
        public int WarningCount { get; set; }
        public int ErrorCount { get; set; }
        public Dictionary<string, PluginHealthStatus> PluginStatuses { get; set; }
    }

    /// <summary>
    /// Export format;
    /// </summary>
    public enum ExportFormat;
    {
        Json = 1,
        Xml = 2,
        Csv = 3;
    }

    /// <summary>
    /// Import format;
    /// </summary>
    public enum ImportFormat;
    {
        Json = 1,
        Xml = 2;
    }

    /// <summary>
    /// Registry event arguments;
    /// </summary>
    public class RegistryEventArgs : EventArgs;
    {
        public string RegistryPath { get; }
        public DateTime Timestamp { get; }
        public long SizeBytes { get; }

        public RegistryEventArgs(string registryPath, DateTime timestamp)
        {
            RegistryPath = registryPath;
            Timestamp = timestamp;

            try
            {
                if (System.IO.File.Exists(registryPath))
                {
                    SizeBytes = new System.IO.FileInfo(registryPath).Length;
                }
            }
            catch
            {
                SizeBytes = 0;
            }
        }
    }

    /// <summary>
    /// Plugin discovery event arguments;
    /// </summary>
    public class PluginDiscoveryEventArgs : EventArgs;
    {
        public string DiscoveryId { get; }
        public PluginDiscoveryResult Result { get; }
        public DateTime Timestamp { get; }

        public PluginDiscoveryEventArgs(string discoveryId, PluginDiscoveryResult result)
        {
            DiscoveryId = discoveryId;
            Result = result;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Registry statistics event arguments;
    /// </summary>
    public class RegistryStatisticsEventArgs : EventArgs;
    {
        public RegistryStatistics Statistics { get; }
        public DateTime Timestamp { get; }

        public RegistryStatisticsEventArgs(RegistryStatistics statistics)
        {
            Statistics = statistics;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Health checkable interface for plugins;
    /// </summary>
    public interface IHealthCheckable;
    {
        Task<PluginHealthStatus> CheckHealthAsync();
    }

    /// <summary>
    /// Registry service exception;
    /// </summary>
    public class RegistryServiceException : Exception
    {
        public RegistryServiceException(string message) : base(message) { }
        public RegistryServiceException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin discovery exception;
    /// </summary>
    public class PluginDiscoveryException : RegistryServiceException;
    {
        public PluginDiscoveryException(string message) : base(message) { }
        public PluginDiscoveryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
