using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.Services.FileService;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.SecurityModules.AdvancedSecurity;
using NEDA.SecurityModules.AdvancedSecurity.Authentication;
using NEDA.SecurityModules.AdvancedSecurity.Authorization;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.EngineIntegration.PluginSystem.PluginManager;
{
    /// <summary>
    /// Advanced plugin management system with dependency injection, security, and hot-reload capabilities;
    /// </summary>
    public interface IPluginManager : IDisposable
    {
        /// <summary>
        /// Gets all available plugins (loaded and discovered)
        /// </summary>
        IEnumerable<IPluginInfo> AvailablePlugins { get; }

        /// <summary>
        /// Gets all currently loaded plugins;
        /// </summary>
        IEnumerable<IPluginInstance> LoadedPlugins { get; }

        /// <summary>
        /// Gets plugin by its unique identifier;
        /// </summary>
        IPluginInstance GetPlugin(Guid pluginId);

        /// <summary>
        /// Gets plugin by its name;
        /// </summary>
        IPluginInstance GetPlugin(string pluginName);

        /// <summary>
        /// Loads a plugin from assembly;
        /// </summary>
        Task<PluginLoadResult> LoadPluginAsync(PluginLoadRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads multiple plugins in batch;
        /// </summary>
        Task<BatchLoadResult> LoadPluginsAsync(BatchLoadRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Unloads a plugin;
        /// </summary>
        Task<UnloadResult> UnloadPluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Unloads all plugins;
        /// </summary>
        Task<UnloadResult> UnloadAllPluginsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Enables a plugin;
        /// </summary>
        Task<EnableResult> EnablePluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disables a plugin;
        /// </summary>
        Task<DisableResult> DisablePluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Configures a plugin;
        /// </summary>
        Task<ConfigureResult> ConfigurePluginAsync(PluginConfigureRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a plugin command;
        /// </summary>
        Task<CommandResult> ExecuteCommandAsync(PluginCommandRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Scans directory for available plugins;
        /// </summary>
        Task<ScanResult> ScanForPluginsAsync(string directoryPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates plugin compatibility;
        /// </summary>
        Task<ValidationResult> ValidatePluginAsync(PluginValidationRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets plugin dependencies;
        /// </summary>
        Task<DependencyResult> GetDependenciesAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Installs a plugin from package;
        /// </summary>
        Task<InstallResult> InstallPluginAsync(PluginInstallRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates a plugin to newer version;
        /// </summary>
        Task<UpdateResult> UpdatePluginAsync(PluginUpdateRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Uninstalls a plugin completely;
        /// </summary>
        Task<UninstallResult> UninstallPluginAsync(Guid pluginId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets plugin performance statistics;
        /// </summary>
        PluginStatistics GetPluginStatistics(Guid pluginId);

        /// <summary>
        /// Gets overall plugin system statistics;
        /// </summary>
        PluginSystemStatistics GetSystemStatistics();

        /// <summary>
        /// Event fired when plugin state changes;
        /// </summary>
        event EventHandler<PluginStateChangedEventArgs> PluginStateChanged;

        /// <summary>
        /// Event fired when plugin is loaded;
        /// </summary>
        event EventHandler<PluginLoadedEventArgs> PluginLoaded;

        /// <summary>
        /// Event fired when plugin is unloaded;
        /// </summary>
        event EventHandler<PluginUnloadedEventArgs> PluginUnloaded;

        /// <summary>
        /// Event fired when plugin error occurs;
        /// </summary>
        event EventHandler<PluginErrorEventArgs> PluginError;
    }

    /// <summary>
    /// Main plugin manager implementation;
    /// </summary>
    public class PluginManager : IPluginManager;
    {
        private readonly ILogger _logger;
        private readonly IFileManager _fileManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IPluginLoader _pluginLoader;
        private readonly IPluginRegistry _pluginRegistry
        private readonly IAuthorizationService _authorizationService;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IEventBus _eventBus;
        private readonly PluginManagerConfiguration _configuration;
        private readonly ConcurrentDictionary<Guid, IPluginInstance> _loadedPlugins;
        private readonly ConcurrentDictionary<string, PluginMetadata> _availablePlugins;
        private readonly ConcurrentDictionary<Guid, PluginStatistics> _pluginStatistics;
        private readonly SemaphoreSlim _pluginOperationLock;
        private readonly PluginDependencyResolver _dependencyResolver;
        private readonly PluginSecurityValidator _securityValidator;
        private readonly PluginCompatibilityChecker _compatibilityChecker;
        private readonly PluginPerformanceMonitor _performanceMonitorInternal;
        private readonly PluginLifecycleManager _lifecycleManager;
        private bool _disposed;
        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of PluginManager;
        /// </summary>
        public PluginManager(
            ILogger logger,
            IFileManager fileManager,
            IPerformanceMonitor performanceMonitor,
            IPluginLoader pluginLoader,
            IPluginRegistry pluginRegistry,
            IAuthorizationService authorizationService,
            ICryptoEngine cryptoEngine,
            IEventBus eventBus,
            PluginManagerConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _loadedPlugins = new ConcurrentDictionary<Guid, IPluginInstance>();
            _availablePlugins = new ConcurrentDictionary<string, PluginMetadata>();
            _pluginStatistics = new ConcurrentDictionary<Guid, PluginStatistics>();
            _pluginOperationLock = new SemaphoreSlim(1, 1);

            _dependencyResolver = new PluginDependencyResolver(_logger, _pluginRegistry);
            _securityValidator = new PluginSecurityValidator(_logger, _cryptoEngine, _configuration);
            _compatibilityChecker = new PluginCompatibilityChecker(_logger, _configuration);
            _performanceMonitorInternal = new PluginPerformanceMonitor(_logger, _performanceMonitor);
            _lifecycleManager = new PluginLifecycleManager(_logger, _eventBus, _configuration);

            InitializeEventHandlers();

            _logger.LogInformation("PluginManager initialized with configuration: {Configuration}", _configuration);
        }

        private void InitializeEventHandlers()
        {
            _pluginLoader.PluginLoadError += OnPluginLoadError;
            _pluginRegistry.PluginRegistered += OnPluginRegistered;
            _pluginRegistry.PluginUnregistered += OnPluginUnregistered;
        }

        private void OnPluginLoadError(object sender, PluginLoadErrorEventArgs e)
        {
            _logger.LogError(e.Exception, "Plugin load error: {PluginPath}", e.PluginPath);
            PluginError?.Invoke(this, new PluginErrorEventArgs;
            {
                PluginId = e.PluginId,
                PluginName = e.PluginName,
                Error = e.Exception,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnPluginRegistered(object sender, PluginRegisteredEventArgs e)
        {
            _availablePlugins[e.PluginMetadata.Name] = e.PluginMetadata;
            _logger.LogDebug("Plugin registered: {PluginName} ({PluginId})",
                e.PluginMetadata.Name, e.PluginMetadata.Id);
        }

        private void OnPluginUnregistered(object sender, PluginUnregisteredEventArgs e)
        {
            _availablePlugins.TryRemove(e.PluginName, out _);
            _logger.LogDebug("Plugin unregistered: {PluginName}", e.PluginName);
        }

        /// <inheritdoc/>
        public IEnumerable<IPluginInfo> AvailablePlugins => _availablePlugins.Values;

        /// <inheritdoc/>
        public IEnumerable<IPluginInstance> LoadedPlugins => _loadedPlugins.Values;

        /// <inheritdoc/>
        public IPluginInstance GetPlugin(Guid pluginId)
        {
            return _loadedPlugins.TryGetValue(pluginId, out var plugin) ? plugin : null;
        }

        /// <inheritdoc/>
        public IPluginInstance GetPlugin(string pluginName)
        {
            return _loadedPlugins.Values.FirstOrDefault(p =>
                string.Equals(p.Metadata.Name, pluginName, StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc/>
        public async Task<PluginLoadResult> LoadPluginAsync(PluginLoadRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            await _pluginOperationLock.WaitAsync(cancellationToken);

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var performanceCounter = _performanceMonitor.StartCounter("PluginLoad", operationId.ToString());

            try
            {
                _logger.LogInformation("Loading plugin. Operation: {OperationId}, Path: {PluginPath}",
                    operationId, request.PluginPath);

                // Validate request;
                var validationResult = await ValidateLoadRequestAsync(request, cancellationToken);
                if (!validationResult.IsValid)
                {
                    return PluginLoadResult.Failure(validationResult.Errors, $"Plugin load validation failed");
                }

                // Check if plugin is already loaded;
                var existingPlugin = _loadedPlugins.Values.FirstOrDefault(p =>
                    string.Equals(p.Metadata.AssemblyPath, request.PluginPath, StringComparison.OrdinalIgnoreCase));

                if (existingPlugin != null)
                {
                    _logger.LogWarning("Plugin already loaded: {PluginPath}, PluginId: {PluginId}",
                        request.PluginPath, existingPlugin.Metadata.Id);

                    return PluginLoadResult.Failure(
                        new[] { "Plugin is already loaded" },
                        $"Plugin '{existingPlugin.Metadata.Name}' is already loaded");
                }

                // Validate plugin security;
                var securityResult = await _securityValidator.ValidatePluginAsync(request.PluginPath, cancellationToken);
                if (!securityResult.IsValid)
                {
                    _logger.LogWarning("Plugin security validation failed: {PluginPath}, Issues: {Issues}",
                        request.PluginPath, string.Join(", ", securityResult.Issues));

                    return PluginLoadResult.Failure(
                        securityResult.Issues,
                        $"Plugin security validation failed: {string.Join(", ", securityResult.Issues)}");
                }

                // Check plugin compatibility;
                var compatibilityResult = await _compatibilityChecker.CheckCompatibilityAsync(request.PluginPath, cancellationToken);
                if (!compatibilityResult.IsCompatible)
                {
                    _logger.LogWarning("Plugin compatibility check failed: {PluginPath}, Issues: {Issues}",
                        request.PluginPath, string.Join(", ", compatibilityResult.Issues));

                    return PluginLoadResult.Failure(
                        compatibilityResult.Issues,
                        $"Plugin compatibility check failed: {string.Join(", ", compatibilityResult.Issues)}");
                }

                // Resolve dependencies;
                var dependencies = await _dependencyResolver.ResolveDependenciesAsync(request.PluginPath, cancellationToken);
                if (!dependencies.AllResolved && !request.ForceLoad)
                {
                    var missingDeps = dependencies.MissingDependencies.Select(d => d.Name).ToList();
                    _logger.LogWarning("Plugin dependencies not resolved: {PluginPath}, Missing: {MissingDeps}",
                        request.PluginPath, string.Join(", ", missingDeps));

                    return PluginLoadResult.Failure(
                        new[] { $"Missing dependencies: {string.Join(", ", missingDeps)}" },
                        $"Plugin dependencies not resolved");
                }

                // Load missing dependencies first;
                if (dependencies.MissingDependencies.Any())
                {
                    _logger.LogInformation("Loading {Count} missing dependencies for plugin: {PluginPath}",
                        dependencies.MissingDependencies.Count, request.PluginPath);

                    foreach (var dependency in dependencies.MissingDependencies)
                    {
                        var depLoadResult = await LoadDependencyAsync(dependency, cancellationToken);
                        if (!depLoadResult.Success)
                        {
                            return PluginLoadResult.Failure(
                                depLoadResult.Errors,
                                $"Failed to load dependency '{dependency.Name}': {string.Join(", ", depLoadResult.Errors)}");
                        }
                    }
                }

                // Create plugin context;
                var context = new PluginLoadContext;
                {
                    Request = request,
                    OperationId = operationId,
                    Dependencies = dependencies,
                    CancellationToken = cancellationToken,
                    PerformanceCounter = performanceCounter;
                };

                // Load plugin assembly;
                var assembly = await _pluginLoader.LoadAssemblyAsync(request.PluginPath, cancellationToken);
                if (assembly == null)
                {
                    return PluginLoadResult.Failure(
                        new[] { "Failed to load plugin assembly" },
                        $"Could not load assembly from: {request.PluginPath}");
                }

                // Discover plugin types;
                var pluginTypes = await DiscoverPluginTypesAsync(assembly, cancellationToken);
                if (!pluginTypes.Any())
                {
                    return PluginLoadResult.Failure(
                        new[] { "No valid plugin types found in assembly" },
                        $"Assembly does not contain any valid plugin types: {request.PluginPath}");
                }

                // Create plugin instances;
                var pluginInstances = new List<IPluginInstance>();
                foreach (var pluginType in pluginTypes)
                {
                    try
                    {
                        var pluginInstance = await CreatePluginInstanceAsync(pluginType, context, cancellationToken);
                        if (pluginInstance != null)
                        {
                            pluginInstances.Add(pluginInstance);

                            // Initialize plugin statistics;
                            _pluginStatistics[pluginInstance.Metadata.Id] = new PluginStatistics;
                            {
                                PluginId = pluginInstance.Metadata.Id,
                                PluginName = pluginInstance.Metadata.Name,
                                LoadTime = DateTime.UtcNow;
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create plugin instance for type: {PluginType}", pluginType.FullName);
                    }
                }

                if (!pluginInstances.Any())
                {
                    return PluginLoadResult.Failure(
                        new[] { "Failed to create any plugin instances" },
                        $"Could not create plugin instances from assembly: {request.PluginPath}");
                }

                // Register plugins;
                foreach (var pluginInstance in pluginInstances)
                {
                    var registerResult = await RegisterPluginAsync(pluginInstance, cancellationToken);
                    if (!registerResult.Success)
                    {
                        _logger.LogError("Failed to register plugin: {PluginName}, Errors: {Errors}",
                            pluginInstance.Metadata.Name, string.Join(", ", registerResult.Errors));

                        // Cleanup already registered plugins;
                        foreach (var pi in pluginInstances.Where(pi => pi != pluginInstance))
                        {
                            await UnregisterPluginAsync(pi.Metadata.Id, cancellationToken);
                        }

                        return PluginLoadResult.Failure(
                            registerResult.Errors,
                            $"Plugin registration failed: {pluginInstance.Metadata.Name}");
                    }
                }

                // Initialize plugins;
                var initializedPlugins = new List<IPluginInstance>();
                foreach (var pluginInstance in pluginInstances)
                {
                    var initResult = await InitializePluginAsync(pluginInstance, context, cancellationToken);
                    if (initResult.Success)
                    {
                        initializedPlugins.Add(pluginInstance);
                        _loadedPlugins[pluginInstance.Metadata.Id] = pluginInstance;

                        // Update statistics;
                        if (_pluginStatistics.TryGetValue(pluginInstance.Metadata.Id, out var stats))
                        {
                            stats.InitializationTime = DateTime.UtcNow;
                            stats.State = PluginState.Initialized;
                        }

                        // Fire events;
                        OnPluginLoaded(pluginInstance);
                    }
                    else;
                    {
                        _logger.LogError("Failed to initialize plugin: {PluginName}, Errors: {Errors}",
                            pluginInstance.Metadata.Name, string.Join(", ", initResult.Errors));
                    }
                }

                stopwatch.Stop();
                performanceCounter.Stop();

                _logger.LogInformation("Plugin load completed. Operation: {OperationId}, Loaded: {LoadedCount}/{TotalCount}, Duration: {Duration}ms",
                    operationId, initializedPlugins.Count, pluginInstances.Count, stopwatch.ElapsedMilliseconds);

                return PluginLoadResult.Success(
                    initializedPlugins.Select(p => p.Metadata).ToList(),
                    new PluginLoadStatistics;
                    {
                        TotalPluginsLoaded = initializedPlugins.Count,
                        TotalPluginsDiscovered = pluginInstances.Count,
                        LoadDuration = stopwatch.Elapsed,
                        DependenciesResolved = dependencies.ResolvedDependencies.Count,
                        DependenciesMissing = dependencies.MissingDependencies.Count;
                    });
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Plugin load operation {OperationId} was cancelled", operationId);
                performanceCounter.RecordMetric("Cancelled", 1);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin load failed. Operation: {OperationId}, Path: {PluginPath}",
                    operationId, request.PluginPath);

                performanceCounter.RecordMetric("Failed", 1);
                throw new PluginLoadException($"Plugin load failed: {ex.Message}", ex, operationId);
            }
            finally
            {
                _pluginOperationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<BatchLoadResult> LoadPluginsAsync(BatchLoadRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var performanceCounter = _performanceMonitor.StartCounter("BatchPluginLoad", operationId.ToString());

            _logger.LogInformation("Batch loading plugins. Operation: {OperationId}, Count: {PluginCount}",
                operationId, request.Plugins.Count);

            var results = new List<PluginLoadResult>();
            var loadedCount = 0;
            var failedCount = 0;
            var totalStats = new BatchLoadStatistics();

            try
            {
                // Group plugins by directory for efficient loading;
                var pluginsByDirectory = request.Plugins;
                    .GroupBy(p => Path.GetDirectoryName(p.PluginPath))
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var directoryGroup in pluginsByDirectory)
                {
                    // Scan directory for all plugins;
                    var scanResult = await ScanForPluginsAsync(directoryGroup.Key, cancellationToken);
                    if (!scanResult.Success)
                    {
                        _logger.LogWarning("Failed to scan directory: {Directory}, Skipping {Count} plugins",
                            directoryGroup.Key, directoryGroup.Value.Count);
                        continue;
                    }

                    // Load plugins in parallel with limited concurrency;
                    var parallelOptions = new ParallelOptions;
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Min(_configuration.MaxParallelLoads, directoryGroup.Value.Count)
                    };

                    await Parallel.ForEachAsync(
                        directoryGroup.Value,
                        parallelOptions,
                        async (pluginRequest, ct) =>
                        {
                            try
                            {
                                var result = await LoadPluginAsync(pluginRequest, ct);
                                lock (results)
                                {
                                    results.Add(result);
                                    if (result.Success)
                                        loadedCount++;
                                    else;
                                        failedCount++;

                                    totalStats.TotalPluginsLoaded += result.Statistics?.TotalPluginsLoaded ?? 0;
                                    totalStats.TotalPluginsDiscovered += result.Statistics?.TotalPluginsDiscovered ?? 0;
                                    totalStats.TotalDependenciesResolved += result.Statistics?.DependenciesResolved ?? 0;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to load plugin in batch: {PluginPath}", pluginRequest.PluginPath);
                                lock (results)
                                {
                                    results.Add(PluginLoadResult.Failure(new[] { ex.Message }, $"Batch load failed: {ex.Message}"));
                                    failedCount++;
                                }
                            }
                        });
                }

                stopwatch.Stop();
                performanceCounter.Stop();

                _logger.LogInformation("Batch plugin load completed. Operation: {OperationId}, Total: {Total}, Loaded: {Loaded}, Failed: {Failed}, Duration: {Duration}ms",
                    operationId, request.Plugins.Count, loadedCount, failedCount, stopwatch.ElapsedMilliseconds);

                return new BatchLoadResult;
                {
                    OperationId = operationId,
                    TotalRequests = request.Plugins.Count,
                    SuccessfulLoads = loadedCount,
                    FailedLoads = failedCount,
                    Results = results,
                    Statistics = totalStats,
                    Duration = stopwatch.Elapsed,
                    Success = failedCount == 0;
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Batch plugin load operation {OperationId} was cancelled", operationId);
                performanceCounter.RecordMetric("Cancelled", 1);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch plugin load failed. Operation: {OperationId}", operationId);
                performanceCounter.RecordMetric("Failed", 1);
                throw new PluginLoadException($"Batch plugin load failed: {ex.Message}", ex, operationId);
            }
        }

        /// <inheritdoc/>
        public async Task<UnloadResult> UnloadPluginAsync(Guid pluginId, CancellationToken cancellationToken = default)
        {
            await _pluginOperationLock.WaitAsync(cancellationToken);

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!_loadedPlugins.TryGetValue(pluginId, out var plugin))
                {
                    return UnloadResult.Failure(
                        new[] { $"Plugin not found: {pluginId}" },
                        $"Plugin with ID {pluginId} is not loaded");
                }

                _logger.LogInformation("Unloading plugin. Operation: {OperationId}, Plugin: {PluginName} ({PluginId})",
                    operationId, plugin.Metadata.Name, pluginId);

                // Check if plugin can be unloaded (no active operations)
                var canUnloadResult = await CanUnloadPluginAsync(plugin, cancellationToken);
                if (!canUnloadResult.CanUnload)
                {
                    return UnloadResult.Failure(
                        canUnloadResult.Reasons,
                        $"Cannot unload plugin '{plugin.Metadata.Name}': {string.Join(", ", canUnloadResult.Reasons)}");
                }

                // Get dependent plugins;
                var dependents = GetDependentPlugins(pluginId);
                if (dependents.Any() && !canUnloadResult.ForceUnload)
                {
                    var dependentNames = dependents.Select(d => d.Metadata.Name).ToList();
                    return UnloadResult.Failure(
                        new[] { $"Plugin has dependent plugins: {string.Join(", ", dependentNames)}" },
                        $"Cannot unload plugin '{plugin.Metadata.Name}' because it has dependents");
                }

                // Shutdown plugin;
                var shutdownResult = await ShutdownPluginAsync(plugin, cancellationToken);
                if (!shutdownResult.Success)
                {
                    _logger.LogWarning("Plugin shutdown failed: {PluginName}, Errors: {Errors}",
                        plugin.Metadata.Name, string.Join(", ", shutdownResult.Errors));

                    // Continue with unload anyway if force is enabled;
                    if (!shutdownResult.ForceUnload)
                    {
                        return UnloadResult.Failure(
                            shutdownResult.Errors,
                            $"Plugin shutdown failed: {plugin.Metadata.Name}");
                    }
                }

                // Unregister plugin;
                var unregisterResult = await UnregisterPluginAsync(pluginId, cancellationToken);
                if (!unregisterResult.Success)
                {
                    _logger.LogError("Plugin unregistration failed: {PluginName}, Errors: {Errors}",
                        plugin.Metadata.Name, string.Join(", ", unregisterResult.Errors));
                }

                // Remove from loaded plugins;
                _loadedPlugins.TryRemove(pluginId, out _);
                _pluginStatistics.TryRemove(pluginId, out _);

                // Fire event;
                OnPluginUnloaded(plugin);

                stopwatch.Stop();

                _logger.LogInformation("Plugin unloaded successfully. Operation: {OperationId}, Plugin: {PluginName}, Duration: {Duration}ms",
                    operationId, plugin.Metadata.Name, stopwatch.ElapsedMilliseconds);

                return UnloadResult.Success(
                    plugin.Metadata,
                    new UnloadStatistics;
                    {
                        PluginId = pluginId,
                        PluginName = plugin.Metadata.Name,
                        UnloadDuration = stopwatch.Elapsed,
                        DependentPluginsUnloaded = dependents.Count,
                        ResourcesReleased = shutdownResult.ResourcesReleased;
                    });
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Plugin unload operation {OperationId} was cancelled", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin unload failed. Operation: {OperationId}, PluginId: {PluginId}",
                    operationId, pluginId);

                throw new PluginUnloadException($"Plugin unload failed: {ex.Message}", ex, operationId);
            }
            finally
            {
                _pluginOperationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UnloadResult> UnloadAllPluginsAsync(CancellationToken cancellationToken = default)
        {
            await _pluginOperationLock.WaitAsync(cancellationToken);

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Unloading all plugins. Operation: {OperationId}, Count: {PluginCount}",
                    operationId, _loadedPlugins.Count);

                var results = new List<UnloadResult>();
                var unloadedCount = 0;
                var failedCount = 0;

                // Unload plugins in reverse dependency order;
                var pluginsByDependency = OrderPluginsByDependency(_loadedPlugins.Values, reverse: true);

                foreach (var plugin in pluginsByDependency)
                {
                    try
                    {
                        var result = await UnloadPluginAsync(plugin.Metadata.Id, cancellationToken);
                        results.Add(result);

                        if (result.Success)
                            unloadedCount++;
                        else;
                            failedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to unload plugin: {PluginName}", plugin.Metadata.Name);
                        failedCount++;
                    }
                }

                stopwatch.Stop();

                _logger.LogInformation("All plugins unloaded. Operation: {OperationId}, Total: {Total}, Unloaded: {Unloaded}, Failed: {Failed}, Duration: {Duration}ms",
                    operationId, _loadedPlugins.Count, unloadedCount, failedCount, stopwatch.ElapsedMilliseconds);

                return new UnloadResult;
                {
                    OperationId = operationId,
                    Success = failedCount == 0,
                    PluginMetadata = null,
                    Statistics = new UnloadStatistics;
                    {
                        TotalPlugins = _loadedPlugins.Count,
                        PluginsUnloaded = unloadedCount,
                        PluginsFailed = failedCount,
                        UnloadDuration = stopwatch.Elapsed;
                    },
                    Errors = results.Where(r => !r.Success).SelectMany(r => r.Errors).ToList()
                };
            }
            finally
            {
                _pluginOperationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<EnableResult> EnablePluginAsync(Guid pluginId, CancellationToken cancellationToken = default)
        {
            if (!_loadedPlugins.TryGetValue(pluginId, out var plugin))
            {
                return EnableResult.Failure(
                    new[] { $"Plugin not found: {pluginId}" },
                    $"Plugin with ID {pluginId} is not loaded");
            }

            var operationId = Guid.NewGuid();

            try
            {
                _logger.LogInformation("Enabling plugin. Operation: {OperationId}, Plugin: {PluginName}",
                    operationId, plugin.Metadata.Name);

                // Check authorization;
                if (!await CheckAuthorizationAsync(plugin, PluginOperation.Enable, cancellationToken))
                {
                    return EnableResult.Failure(
                        new[] { "Authorization failed" },
                        $"Not authorized to enable plugin: {plugin.Metadata.Name}");
                }

                // Enable plugin;
                var enableResult = await _lifecycleManager.EnablePluginAsync(plugin, cancellationToken);
                if (!enableResult.Success)
                {
                    return EnableResult.Failure(
                        enableResult.Errors,
                        $"Failed to enable plugin: {plugin.Metadata.Name}");
                }

                // Update statistics;
                if (_pluginStatistics.TryGetValue(pluginId, out var stats))
                {
                    stats.State = PluginState.Enabled;
                    stats.LastEnabledTime = DateTime.UtcNow;
                }

                // Fire event;
                OnPluginStateChanged(plugin, PluginState.Enabled);

                _logger.LogInformation("Plugin enabled successfully. Operation: {OperationId}, Plugin: {PluginName}",
                    operationId, plugin.Metadata.Name);

                return EnableResult.Success(plugin.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enable plugin. Operation: {OperationId}, PluginId: {PluginId}",
                    operationId, pluginId);

                throw new PluginOperationException($"Failed to enable plugin: {ex.Message}", ex, operationId);
            }
        }

        /// <inheritdoc/>
        public async Task<DisableResult> DisablePluginAsync(Guid pluginId, CancellationToken cancellationToken = default)
        {
            if (!_loadedPlugins.TryGetValue(pluginId, out var plugin))
            {
                return DisableResult.Failure(
                    new[] { $"Plugin not found: {pluginId}" },
                    $"Plugin with ID {pluginId} is not loaded");
            }

            var operationId = Guid.NewGuid();

            try
            {
                _logger.LogInformation("Disabling plugin. Operation: {OperationId}, Plugin: {PluginName}",
                    operationId, plugin.Metadata.Name);

                // Check authorization;
                if (!await CheckAuthorizationAsync(plugin, PluginOperation.Disable, cancellationToken))
                {
                    return DisableResult.Failure(
                        new[] { "Authorization failed" },
                        $"Not authorized to disable plugin: {plugin.Metadata.Name}");
                }

                // Check if plugin can be disabled;
                var canDisableResult = await CanDisablePluginAsync(plugin, cancellationToken);
                if (!canDisableResult.CanDisable)
                {
                    return DisableResult.Failure(
                        canDisableResult.Reasons,
                        $"Cannot disable plugin '{plugin.Metadata.Name}': {string.Join(", ", canDisableResult.Reasons)}");
                }

                // Disable plugin;
                var disableResult = await _lifecycleManager.DisablePluginAsync(plugin, cancellationToken);
                if (!disableResult.Success)
                {
                    return DisableResult.Failure(
                        disableResult.Errors,
                        $"Failed to disable plugin: {plugin.Metadata.Name}");
                }

                // Update statistics;
                if (_pluginStatistics.TryGetValue(pluginId, out var stats))
                {
                    stats.State = PluginState.Disabled;
                    stats.LastDisabledTime = DateTime.UtcNow;
                }

                // Fire event;
                OnPluginStateChanged(plugin, PluginState.Disabled);

                _logger.LogInformation("Plugin disabled successfully. Operation: {OperationId}, Plugin: {PluginName}",
                    operationId, plugin.Metadata.Name);

                return DisableResult.Success(plugin.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to disable plugin. Operation: {OperationId}, PluginId: {PluginId}",
                    operationId, pluginId);

                throw new PluginOperationException($"Failed to disable plugin: {ex.Message}", ex, operationId);
            }
        }

        /// <inheritdoc/>
        public async Task<ConfigureResult> ConfigurePluginAsync(PluginConfigureRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!_loadedPlugins.TryGetValue(request.PluginId, out var plugin))
            {
                return ConfigureResult.Failure(
                    new[] { $"Plugin not found: {request.PluginId}" },
                    $"Plugin with ID {request.PluginId} is not loaded");
            }

            var operationId = Guid.NewGuid();

            try
            {
                _logger.LogInformation("Configuring plugin. Operation: {OperationId}, Plugin: {PluginName}",
                    operationId, plugin.Metadata.Name);

                // Check authorization;
                if (!await CheckAuthorizationAsync(plugin, PluginOperation.Configure, cancellationToken))
                {
                    return ConfigureResult.Failure(
                        new[] { "Authorization failed" },
                        $"Not authorized to configure plugin: {plugin.Metadata.Name}");
                }

                // Validate configuration;
                var validationResult = await ValidateConfigurationAsync(request.Configuration, plugin, cancellationToken);
                if (!validationResult.IsValid)
                {
                    return ConfigureResult.Failure(
                        validationResult.Errors,
                        $"Configuration validation failed for plugin: {plugin.Metadata.Name}");
                }

                // Apply configuration;
                var configureResult = await _lifecycleManager.ConfigurePluginAsync(plugin, request.Configuration, cancellationToken);
                if (!configureResult.Success)
                {
                    return ConfigureResult.Failure(
                        configureResult.Errors,
                        $"Failed to configure plugin: {plugin.Metadata.Name}");
                }

                // Update plugin metadata;
                plugin.Metadata.Configuration = request.Configuration;
                plugin.Metadata.LastConfigured = DateTime.UtcNow;

                _logger.LogInformation("Plugin configured successfully. Operation: {OperationId}, Plugin: {PluginName}",
                    operationId, plugin.Metadata.Name);

                return ConfigureResult.Success(plugin.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure plugin. Operation: {OperationId}, PluginId: {PluginId}",
                    operationId, request.PluginId);

                throw new PluginOperationException($"Failed to configure plugin: {ex.Message}", ex, operationId);
            }
        }

        /// <inheritdoc/>
        public async Task<CommandResult> ExecuteCommandAsync(PluginCommandRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!_loadedPlugins.TryGetValue(request.PluginId, out var plugin))
            {
                return CommandResult.Failure(
                    new[] { $"Plugin not found: {request.PluginId}" },
                    $"Plugin with ID {request.PluginId} is not loaded");
            }

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var performanceCounter = _performanceMonitor.StartCounter("PluginCommand", operationId.ToString());

            try
            {
                _logger.LogInformation("Executing plugin command. Operation: {OperationId}, Plugin: {PluginName}, Command: {Command}",
                    operationId, plugin.Metadata.Name, request.Command);

                // Check authorization;
                if (!await CheckAuthorizationAsync(plugin, PluginOperation.ExecuteCommand, cancellationToken))
                {
                    return CommandResult.Failure(
                        new[] { "Authorization failed" },
                        $"Not authorized to execute command on plugin: {plugin.Metadata.Name}");
                }

                // Check if plugin is enabled;
                if (plugin.State != PluginState.Enabled)
                {
                    return CommandResult.Failure(
                        new[] { $"Plugin is not enabled. Current state: {plugin.State}" },
                        $"Cannot execute command on disabled plugin: {plugin.Metadata.Name}");
                }

                // Execute command;
                var commandResult = await _lifecycleManager.ExecuteCommandAsync(plugin, request, cancellationToken);
                if (!commandResult.Success)
                {
                    return CommandResult.Failure(
                        commandResult.Errors,
                        $"Command execution failed for plugin: {plugin.Metadata.Name}");
                }

                stopwatch.Stop();
                performanceCounter.Stop();

                // Update statistics;
                if (_pluginStatistics.TryGetValue(request.PluginId, out var stats))
                {
                    stats.CommandsExecuted++;
                    stats.TotalCommandExecutionTime += stopwatch.Elapsed;
                    stats.LastCommandExecutionTime = DateTime.UtcNow;
                }

                _logger.LogInformation("Plugin command executed successfully. Operation: {OperationId}, Plugin: {PluginName}, Duration: {Duration}ms",
                    operationId, plugin.Metadata.Name, stopwatch.ElapsedMilliseconds);

                return CommandResult.Success(
                    commandResult.Result,
                    new CommandExecutionStatistics;
                    {
                        PluginId = request.PluginId,
                        PluginName = plugin.Metadata.Name,
                        Command = request.Command,
                        ExecutionTime = stopwatch.Elapsed,
                        Success = true;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin command execution failed. Operation: {OperationId}, PluginId: {PluginId}, Command: {Command}",
                    operationId, request.PluginId, request.Command);

                performanceCounter.RecordMetric("Failed", 1);
                throw new PluginCommandException($"Plugin command execution failed: {ex.Message}", ex, operationId);
            }
        }

        /// <inheritdoc/>
        public async Task<ScanResult> ScanForPluginsAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(directoryPath))
                throw new ArgumentException("Directory path cannot be null or empty", nameof(directoryPath));

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Scanning for plugins. Operation: {OperationId}, Directory: {Directory}",
                    operationId, directoryPath);

                // Validate directory;
                if (!await _fileManager.DirectoryExistsAsync(directoryPath))
                {
                    return ScanResult.Failure(
                        new[] { $"Directory does not exist: {directoryPath}" },
                        $"Directory not found: {directoryPath}");
                }

                // Get all DLL files in directory;
                var dllFiles = await _fileManager.GetFilesAsync(directoryPath, "*.dll", SearchOption.AllDirectories, cancellationToken);
                var exeFiles = await _fileManager.GetFilesAsync(directoryPath, "*.exe", SearchOption.AllDirectories, cancellationToken);

                var allFiles = dllFiles.Concat(exeFiles).Distinct().ToList();

                _logger.LogDebug("Found {FileCount} potential plugin files in directory: {Directory}",
                    allFiles.Count, directoryPath);

                var discoveredPlugins = new List<PluginMetadata>();
                var scanErrors = new List<ScanError>();

                // Scan each file;
                foreach (var filePath in allFiles)
                {
                    try
                    {
                        var pluginMetadata = await ScanPluginFileAsync(filePath, cancellationToken);
                        if (pluginMetadata != null)
                        {
                            discoveredPlugins.Add(pluginMetadata);
                            _logger.LogDebug("Discovered plugin: {PluginName} at {FilePath}",
                                pluginMetadata.Name, filePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        scanErrors.Add(new ScanError;
                        {
                            FilePath = filePath,
                            Error = ex.Message,
                            Timestamp = DateTime.UtcNow;
                        });

                        _logger.LogWarning(ex, "Failed to scan plugin file: {FilePath}", filePath);
                    }
                }

                stopwatch.Stop();

                _logger.LogInformation("Plugin scan completed. Operation: {OperationId}, Directory: {Directory}, Found: {PluginCount}, Errors: {ErrorCount}, Duration: {Duration}ms",
                    operationId, directoryPath, discoveredPlugins.Count, scanErrors.Count, stopwatch.ElapsedMilliseconds);

                return ScanResult.Success(
                    discoveredPlugins,
                    new ScanStatistics;
                    {
                        Directory = directoryPath,
                        FilesScanned = allFiles.Count,
                        PluginsDiscovered = discoveredPlugins.Count,
                        ScanErrors = scanErrors.Count,
                        ScanDuration = stopwatch.Elapsed;
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin scan failed. Operation: {OperationId}, Directory: {Directory}",
                    operationId, directoryPath);

                throw new PluginScanException($"Plugin scan failed: {ex.Message}", ex, operationId);
            }
        }

        /// <inheritdoc/>
        public async Task<ValidationResult> ValidatePluginAsync(PluginValidationRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Validating plugin. Operation: {OperationId}, Path: {PluginPath}",
                    operationId, request.PluginPath);

                var errors = new List<string>();
                var warnings = new List<string>();
                var validationDetails = new Dictionary<string, object>();

                // File existence check;
                if (!await _fileManager.ExistsAsync(request.PluginPath))
                {
                    errors.Add($"Plugin file does not exist: {request.PluginPath}");
                    return CreateValidationResult(false, errors, warnings, stopwatch);
                }

                // Security validation;
                var securityResult = await _securityValidator.ValidatePluginAsync(request.PluginPath, cancellationToken);
                if (!securityResult.IsValid)
                {
                    errors.AddRange(securityResult.Issues);
                    validationDetails["security"] = securityResult.Details;
                }

                // Compatibility validation;
                var compatibilityResult = await _compatibilityChecker.CheckCompatibilityAsync(request.PluginPath, cancellationToken);
                if (!compatibilityResult.IsCompatible)
                {
                    errors.AddRange(compatibilityResult.Issues);
                    validationDetails["compatibility"] = compatibilityResult.Details;
                }

                // Dependency validation;
                var dependencyResult = await _dependencyResolver.ResolveDependenciesAsync(request.PluginPath, cancellationToken);
                if (!dependencyResult.AllResolved)
                {
                    var missingDeps = dependencyResult.MissingDependencies.Select(d => d.Name).ToList();
                    warnings.Add($"Missing dependencies: {string.Join(", ", missingDeps)}");
                    validationDetails["dependencies"] = dependencyResult;
                }

                // Assembly validation;
                try
                {
                    var assemblyValidation = await ValidateAssemblyAsync(request.PluginPath, cancellationToken);
                    if (!assemblyValidation.IsValid)
                    {
                        errors.AddRange(assemblyValidation.Errors);
                        validationDetails["assembly"] = assemblyValidation.Details;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Assembly validation failed: {ex.Message}");
                }

                stopwatch.Stop();

                var isValid = !errors.Any();
                var status = isValid ? "VALID" : "INVALID";

                _logger.LogInformation("Plugin validation completed. Operation: {OperationId}, Path: {PluginPath}, Status: {Status}, Errors: {ErrorCount}, Warnings: {WarningCount}, Duration: {Duration}ms",
                    operationId, request.PluginPath, status, errors.Count, warnings.Count, stopwatch.ElapsedMilliseconds);

                return CreateValidationResult(isValid, errors, warnings, stopwatch, validationDetails);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin validation failed. Operation: {OperationId}, Path: {PluginPath}",
                    operationId, request.PluginPath);

                throw new PluginValidationException($"Plugin validation failed: {ex.Message}", ex, operationId);
            }
        }

        /// <inheritdoc/>
        public async Task<DependencyResult> GetDependenciesAsync(Guid pluginId, CancellationToken cancellationToken = default)
        {
            if (!_loadedPlugins.TryGetValue(pluginId, out var plugin))
            {
                return DependencyResult.Failure(
                    new[] { $"Plugin not found: {pluginId}" },
                    $"Plugin with ID {pluginId} is not loaded");
            }

            try
            {
                var dependencies = await _dependencyResolver.ResolveDependenciesAsync(plugin.Metadata.AssemblyPath, cancellationToken);

                return new DependencyResult;
                {
                    Success = true,
                    PluginId = pluginId,
                    PluginName = plugin.Metadata.Name,
                    Dependencies = dependencies.ResolvedDependencies,
                    MissingDependencies = dependencies.MissingDependencies,
                    DependencyGraph = dependencies.DependencyGraph,
                    AllResolved = dependencies.AllResolved;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get dependencies for plugin: {PluginId}", pluginId);
                throw new PluginDependencyException($"Failed to get dependencies: {ex.Message}", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<InstallResult> InstallPluginAsync(PluginInstallRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            await _pluginOperationLock.WaitAsync(cancellationToken);

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Installing plugin. Operation: {OperationId}, Source: {SourcePath}",
                    operationId, request.SourcePath);

                // Validate source;
                if (!await _fileManager.ExistsAsync(request.SourcePath))
                {
                    return InstallResult.Failure(
                        new[] { $"Source file does not exist: {request.SourcePath}" },
                        $"Installation source not found: {request.SourcePath}");
                }

                // Determine installation directory;
                var installDir = GetInstallationDirectory(request.InstallationScope);
                await _fileManager.EnsureDirectoryExistsAsync(installDir);

                // Extract/copy plugin files;
                var extractResult = await ExtractPluginFilesAsync(request, installDir, cancellationToken);
                if (!extractResult.Success)
                {
                    return InstallResult.Failure(
                        extractResult.Errors,
                        $"Failed to extract plugin files: {string.Join(", ", extractResult.Errors)}");
                }

                // Scan for plugins in installation directory;
                var scanResult = await ScanForPluginsAsync(installDir, cancellationToken);
                if (!scanResult.Success || !scanResult.Plugins.Any())
                {
                    return InstallResult.Failure(
                        new[] { "No valid plugins found in installation package" },
                        $"Installation package does not contain valid plugins");
                }

                // Load installed plugins;
                var loadRequests = scanResult.Plugins.Select(p => new PluginLoadRequest;
                {
                    PluginPath = p.AssemblyPath,
                    LoadConfiguration = request.LoadConfiguration,
                    ForceLoad = request.ForceInstall;
                }).ToList();

                var loadResult = await LoadPluginsAsync(new BatchLoadRequest { Plugins = loadRequests }, cancellationToken);

                stopwatch.Stop();

                _logger.LogInformation("Plugin installation completed. Operation: {OperationId}, Installed: {PluginCount}, Duration: {Duration}ms",
                    operationId, loadResult.SuccessfulLoads, stopwatch.ElapsedMilliseconds);

                return new InstallResult;
                {
                    OperationId = operationId,
                    Success = loadResult.Success,
                    InstallationDirectory = installDir,
                    InstalledPlugins = scanResult.Plugins,
                    LoadResults = loadResult.Results,
                    Statistics = new InstallStatistics;
                    {
                        InstallationDuration = stopwatch.Elapsed,
                        PluginsInstalled = loadResult.SuccessfulLoads,
                        PluginsFailed = loadResult.FailedLoads,
                        InstallationScope = request.InstallationScope;
                    },
                    Errors = loadResult.Results.Where(r => !r.Success).SelectMany(r => r.Errors).ToList()
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Plugin installation operation {OperationId} was cancelled", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin installation failed. Operation: {OperationId}, Source: {SourcePath}",
                    operationId, request.SourcePath);

                throw new PluginInstallException($"Plugin installation failed: {ex.Message}", ex, operationId);
            }
            finally
            {
                _pluginOperationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UpdateResult> UpdatePluginAsync(PluginUpdateRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            await _pluginOperationLock.WaitAsync(cancellationToken);

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!_loadedPlugins.TryGetValue(request.PluginId, out var plugin))
                {
                    return UpdateResult.Failure(
                        new[] { $"Plugin not found: {request.PluginId}" },
                        $"Plugin with ID {request.PluginId} is not loaded");
                }

                _logger.LogInformation("Updating plugin. Operation: {OperationId}, Plugin: {PluginName}, Version: {CurrentVersion} -> {TargetVersion}",
                    operationId, plugin.Metadata.Name, plugin.Metadata.Version, request.TargetVersion);

                // Backup current plugin;
                var backupResult = await BackupPluginAsync(plugin, cancellationToken);
                if (!backupResult.Success)
                {
                    _logger.LogWarning("Plugin backup failed: {PluginName}, Continuing with update", plugin.Metadata.Name);
                }

                // Unload current plugin;
                var unloadResult = await UnloadPluginAsync(request.PluginId, cancellationToken);
                if (!unloadResult.Success)
                {
                    return UpdateResult.Failure(
                        unloadResult.Errors,
                        $"Failed to unload plugin for update: {plugin.Metadata.Name}");
                }

                // Download/install new version;
                var installRequest = new PluginInstallRequest;
                {
                    SourcePath = request.UpdatePackagePath,
                    InstallationScope = InstallationScope.User,
                    LoadConfiguration = request.LoadConfiguration,
                    ForceInstall = request.ForceUpdate;
                };

                var installResult = await InstallPluginAsync(installRequest, cancellationToken);

                stopwatch.Stop();

                _logger.LogInformation("Plugin update completed. Operation: {OperationId}, Plugin: {PluginName}, Success: {Success}, Duration: {Duration}ms",
                    operationId, plugin.Metadata.Name, installResult.Success, stopwatch.ElapsedMilliseconds);

                return new UpdateResult;
                {
                    OperationId = operationId,
                    Success = installResult.Success,
                    PluginId = request.PluginId,
                    PluginName = plugin.Metadata.Name,
                    PreviousVersion = plugin.Metadata.Version,
                    NewVersion = request.TargetVersion,
                    InstallationResult = installResult,
                    Statistics = new UpdateStatistics;
                    {
                        UpdateDuration = stopwatch.Elapsed,
                        BackupCreated = backupResult.Success,
                        BackupPath = backupResult.BackupPath;
                    },
                    Errors = installResult.Errors;
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Plugin update operation {OperationId} was cancelled", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin update failed. Operation: {OperationId}, PluginId: {PluginId}",
                    operationId, request.PluginId);

                throw new PluginUpdateException($"Plugin update failed: {ex.Message}", ex, operationId);
            }
            finally
            {
                _pluginOperationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UninstallResult> UninstallPluginAsync(Guid pluginId, CancellationToken cancellationToken = default)
        {
            await _pluginOperationLock.WaitAsync(cancellationToken);

            var operationId = Guid.NewGuid();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (!_loadedPlugins.TryGetValue(pluginId, out var plugin))
                {
                    return UninstallResult.Failure(
                        new[] { $"Plugin not found: {pluginId}" },
                        $"Plugin with ID {pluginId} is not loaded");
                }

                _logger.LogInformation("Uninstalling plugin. Operation: {OperationId}, Plugin: {PluginName}",
                    operationId, plugin.Metadata.Name);

                // Unload plugin first;
                var unloadResult = await UnloadPluginAsync(pluginId, cancellationToken);
                if (!unloadResult.Success && !unloadResult.ForceUnload)
                {
                    return UninstallResult.Failure(
                        unloadResult.Errors,
                        $"Failed to unload plugin for uninstallation: {plugin.Metadata.Name}");
                }

                // Delete plugin files;
                var deleteResult = await DeletePluginFilesAsync(plugin, cancellationToken);
                if (!deleteResult.Success)
                {
                    return UninstallResult.Failure(
                        deleteResult.Errors,
                        $"Failed to delete plugin files: {plugin.Metadata.Name}");
                }

                // Remove from registry
                await _pluginRegistry.UnregisterPluginAsync(plugin.Metadata.Name, cancellationToken);

                stopwatch.Stop();

                _logger.LogInformation("Plugin uninstalled successfully. Operation: {OperationId}, Plugin: {PluginName}, Duration: {Duration}ms",
                    operationId, plugin.Metadata.Name, stopwatch.ElapsedMilliseconds);

                return UninstallResult.Success(
                    plugin.Metadata,
                    new UninstallStatistics;
                    {
                        PluginId = pluginId,
                        PluginName = plugin.Metadata.Name,
                        UninstallDuration = stopwatch.Elapsed,
                        FilesDeleted = deleteResult.FilesDeleted,
                        DirectoriesDeleted = deleteResult.DirectoriesDeleted;
                    });
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Plugin uninstallation operation {OperationId} was cancelled", operationId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin uninstallation failed. Operation: {OperationId}, PluginId: {PluginId}",
                    operationId, pluginId);

                throw new PluginUninstallException($"Plugin uninstallation failed: {ex.Message}", ex, operationId);
            }
            finally
            {
                _pluginOperationLock.Release();
            }
        }

        /// <inheritdoc/>
        public PluginStatistics GetPluginStatistics(Guid pluginId)
        {
            return _pluginStatistics.TryGetValue(pluginId, out var stats)
                ? stats.Clone()
                : new PluginStatistics { PluginId = pluginId };
        }

        /// <inheritdoc/>
        public PluginSystemStatistics GetSystemStatistics()
        {
            return new PluginSystemStatistics;
            {
                TotalPluginsLoaded = _loadedPlugins.Count,
                TotalPluginsAvailable = _availablePlugins.Count,
                TotalCommandsExecuted = _pluginStatistics.Values.Sum(s => s.CommandsExecuted),
                TotalMemoryUsage = _pluginStatistics.Values.Sum(s => s.MemoryUsage),
                AverageLoadTime = _pluginStatistics.Values.Where(s => s.LoadTime.HasValue)
                    .Average(s => (DateTime.UtcNow - s.LoadTime.Value).TotalMilliseconds),
                PluginsByState = _loadedPlugins.Values;
                    .GroupBy(p => p.State)
                    .ToDictionary(g => g.Key, g => g.Count()),
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <inheritdoc/>
        public event EventHandler<PluginStateChangedEventArgs> PluginStateChanged;

        /// <inheritdoc/>
        public event EventHandler<PluginLoadedEventArgs> PluginLoaded;

        /// <inheritdoc/>
        public event EventHandler<PluginUnloadedEventArgs> PluginUnloaded;

        /// <inheritdoc/>
        public event EventHandler<PluginErrorEventArgs> PluginError;

        #region Private Helper Methods;

        private async Task<ValidationResult> ValidateLoadRequestAsync(PluginLoadRequest request, CancellationToken cancellationToken)
        {
            var errors = new List<string>();

            // Check if file exists;
            if (!await _fileManager.ExistsAsync(request.PluginPath))
            {
                errors.Add($"Plugin file does not exist: {request.PluginPath}");
            }

            // Check file extension;
            var extension = Path.GetExtension(request.PluginPath)?.ToLowerInvariant();
            if (extension != ".dll" && extension != ".exe")
            {
                errors.Add($"Invalid plugin file extension: {extension}. Expected .dll or .exe");
            }

            // Check file size;
            var fileSize = await _fileManager.GetFileSizeAsync(request.PluginPath);
            if (fileSize > _configuration.MaxPluginSize)
            {
                errors.Add($"Plugin file size ({fileSize} bytes) exceeds maximum limit ({_configuration.MaxPluginSize} bytes)");
            }

            return new ValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors;
            };
        }

        private async Task<PluginLoadResult> LoadDependencyAsync(PluginDependency dependency, CancellationToken cancellationToken)
        {
            // Try to find dependency in known locations;
            var dependencyPath = await FindDependencyAsync(dependency, cancellationToken);
            if (string.IsNullOrEmpty(dependencyPath))
            {
                return PluginLoadResult.Failure(
                    new[] { $"Dependency not found: {dependency.Name} {dependency.Version}" },
                    $"Could not locate dependency: {dependency.Name}");
            }

            var loadRequest = new PluginLoadRequest;
            {
                PluginPath = dependencyPath,
                LoadConfiguration = new PluginLoadConfiguration;
                {
                    IsDependency = true,
                    LoadBehavior = LoadBehavior.Lazy;
                }
            };

            return await LoadPluginAsync(loadRequest, cancellationToken);
        }

        private async Task<string> FindDependencyAsync(PluginDependency dependency, CancellationToken cancellationToken)
        {
            // Search in plugin directories;
            foreach (var pluginDir in _configuration.PluginDirectories)
            {
                var searchPattern = $"{dependency.Name}*.dll";
                var files = await _fileManager.GetFilesAsync(pluginDir, searchPattern, SearchOption.AllDirectories, cancellationToken);

                foreach (var file in files)
                {
                    try
                    {
                        var metadata = await ScanPluginFileAsync(file, cancellationToken);
                        if (metadata != null && metadata.Name == dependency.Name)
                        {
                            // Check version compatibility;
                            if (IsVersionCompatible(metadata.Version, dependency.Version))
                            {
                                return file;
                            }
                        }
                    }
                    catch
                    {
                        // Continue searching;
                    }
                }
            }

            return null;
        }

        private bool IsVersionCompatible(string actualVersion, string requiredVersion)
        {
            // Simple version compatibility check;
            // In production, use proper version comparison;
            return actualVersion == requiredVersion || requiredVersion == "*";
        }

        private async Task<List<Type>> DiscoverPluginTypesAsync(Assembly assembly, CancellationToken cancellationToken)
        {
            var pluginTypes = new List<Type>();

            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (await IsPluginTypeAsync(type, cancellationToken))
                    {
                        pluginTypes.Add(type);
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                _logger.LogError(ex, "Failed to load types from assembly: {Assembly}", assembly.FullName);
                throw new PluginDiscoveryException($"Failed to discover plugin types: {ex.Message}", ex);
            }

            return pluginTypes;
        }

        private async Task<bool> IsPluginTypeAsync(Type type, CancellationToken cancellationToken)
        {
            // Check if type implements IPlugin interface;
            if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            {
                // Check for required attributes;
                var pluginAttribute = type.GetCustomAttribute<PluginAttribute>();
                if (pluginAttribute != null)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<IPluginInstance> CreatePluginInstanceAsync(Type pluginType, PluginLoadContext context, CancellationToken cancellationToken)
        {
            try
            {
                // Create instance;
                var pluginInstance = Activator.CreateInstance(pluginType) as IPlugin;
                if (pluginInstance == null)
                {
                    throw new PluginCreationException($"Failed to create instance of type: {pluginType.FullName}");
                }

                // Get plugin metadata;
                var pluginAttribute = pluginType.GetCustomAttribute<PluginAttribute>();
                var metadata = new PluginMetadata;
                {
                    Id = Guid.NewGuid(),
                    Name = pluginAttribute?.Name ?? pluginType.Name,
                    Description = pluginAttribute?.Description ?? string.Empty,
                    Version = pluginAttribute?.Version ?? "1.0.0",
                    Author = pluginAttribute?.Author ?? string.Empty,
                    AssemblyPath = context.Request.PluginPath,
                    AssemblyName = pluginType.Assembly.FullName,
                    TypeName = pluginType.FullName,
                    LoadTime = DateTime.UtcNow,
                    Dependencies = context.Dependencies.ResolvedDependencies.Select(d => d.Name).ToList(),
                    Configuration = new Dictionary<string, object>()
                };

                // Create plugin instance wrapper;
                var instance = new PluginInstance;
                {
                    Metadata = metadata,
                    Plugin = pluginInstance,
                    State = PluginState.Created,
                    LoadContext = context;
                };

                return instance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create plugin instance for type: {PluginType}", pluginType.FullName);
                throw new PluginCreationException($"Failed to create plugin instance: {ex.Message}", ex);
            }
        }

        private async Task<RegistrationResult> RegisterPluginAsync(IPluginInstance pluginInstance, CancellationToken cancellationToken)
        {
            try
            {
                // Register in plugin registry
                await _pluginRegistry.RegisterPluginAsync(pluginInstance.Metadata, cancellationToken);

                // Add to available plugins;
                _availablePlugins[pluginInstance.Metadata.Name] = pluginInstance.Metadata;

                return RegistrationResult.Success(pluginInstance.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register plugin: {PluginName}", pluginInstance.Metadata.Name);
                return RegistrationResult.Failure(new[] { ex.Message }, $"Plugin registration failed: {ex.Message}");
            }
        }

        private async Task<RegistrationResult> UnregisterPluginAsync(Guid pluginId, CancellationToken cancellationToken)
        {
            try
            {
                if (_loadedPlugins.TryGetValue(pluginId, out var plugin))
                {
                    await _pluginRegistry.UnregisterPluginAsync(plugin.Metadata.Name, cancellationToken);
                    _availablePlugins.TryRemove(plugin.Metadata.Name, out _);
                }

                return RegistrationResult.Success(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unregister plugin: {PluginId}", pluginId);
                return RegistrationResult.Failure(new[] { ex.Message }, $"Plugin unregistration failed: {ex.Message}");
            }
        }

        private async Task<InitializationResult> InitializePluginAsync(IPluginInstance pluginInstance, PluginLoadContext context, CancellationToken cancellationToken)
        {
            try
            {
                // Initialize plugin;
                var initRequest = new PluginInitializeRequest;
                {
                    PluginId = pluginInstance.Metadata.Id,
                    Configuration = context.Request.LoadConfiguration?.InitialConfiguration ?? new Dictionary<string, object>(),
                    Context = new PluginContext;
                    {
                        PluginManager = this,
                        EventBus = _eventBus,
                        Logger = _logger,
                        Configuration = _configuration;
                    }
                };

                await pluginInstance.Plugin.InitializeAsync(initRequest, cancellationToken);

                pluginInstance.State = PluginState.Initialized;

                return InitializationResult.Success(pluginInstance.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize plugin: {PluginName}", pluginInstance.Metadata.Name);
                return InitializationResult.Failure(new[] { ex.Message }, $"Plugin initialization failed: {ex.Message}");
            }
        }

        private async Task<CanUnloadResult> CanUnloadPluginAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            var reasons = new List<string>();
            var canUnload = true;

            // Check if plugin is in a state that can be unloaded;
            if (plugin.State == PluginState.Initializing || plugin.State == PluginState.ShuttingDown)
            {
                reasons.Add($"Plugin is in {plugin.State} state");
                canUnload = false;
            }

            // Check if plugin has active operations;
            var activeOps = await GetActiveOperationsAsync(plugin, cancellationToken);
            if (activeOps.Any())
            {
                reasons.Add($"Plugin has {activeOps.Count} active operations");
                canUnload = false;
            }

            // Check plugin-specific constraints;
            if (plugin.Plugin is IManagedPlugin managedPlugin)
            {
                var canUnloadResult = await managedPlugin.CanUnloadAsync(cancellationToken);
                if (!canUnloadResult.CanUnload)
                {
                    reasons.AddRange(canUnloadResult.Reasons);
                    canUnload = false;
                }
            }

            return new CanUnloadResult;
            {
                CanUnload = canUnload,
                Reasons = reasons,
                ForceUnload = reasons.Count == 0 || reasons.All(r => r.Contains("active operations")) // Allow force unload for active ops;
            };
        }

        private async Task<List<PluginOperation>> GetActiveOperationsAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            var activeOps = new List<PluginOperation>();

            // Implementation would track active operations;
            await Task.Delay(1, cancellationToken); // Placeholder;

            return activeOps;
        }

        private List<IPluginInstance> GetDependentPlugins(Guid pluginId)
        {
            var dependents = new List<IPluginInstance>();

            foreach (var plugin in _loadedPlugins.Values)
            {
                if (plugin.Metadata.Dependencies.Contains(pluginId.ToString()))
                {
                    dependents.Add(plugin);
                }
            }

            return dependents;
        }

        private List<IPluginInstance> OrderPluginsByDependency(IEnumerable<IPluginInstance> plugins, bool reverse = false)
        {
            // Simple dependency ordering;
            var ordered = plugins.OrderBy(p => p.Metadata.Dependencies.Count).ToList();
            return reverse ? ordered.AsEnumerable().Reverse().ToList() : ordered;
        }

        private async Task<ShutdownResult> ShutdownPluginAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            try
            {
                plugin.State = PluginState.ShuttingDown;

                // Shutdown plugin;
                if (plugin.Plugin is IManagedPlugin managedPlugin)
                {
                    await managedPlugin.ShutdownAsync(cancellationToken);
                }
                else;
                {
                    await plugin.Plugin.ShutdownAsync(cancellationToken);
                }

                plugin.State = PluginState.Shutdown;

                return ShutdownResult.Success(plugin.Metadata, 0); // Resources released count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin shutdown failed: {PluginName}", plugin.Metadata.Name);
                return ShutdownResult.Failure(new[] { ex.Message }, $"Plugin shutdown failed: {ex.Message}");
            }
        }

        private async Task<bool> CheckAuthorizationAsync(IPluginInstance plugin, PluginOperation operation, CancellationToken cancellationToken)
        {
            try
            {
                // Check if authorization is required;
                if (!_configuration.RequireAuthorization)
                    return true;

                // Create authorization context;
                var context = new AuthorizationContext;
                {
                    PluginId = plugin.Metadata.Id,
                    PluginName = plugin.Metadata.Name,
                    Operation = operation.ToString(),
                    Timestamp = DateTime.UtcNow;
                };

                // Check authorization;
                var result = await _authorizationService.CheckAccessAsync(context, cancellationToken);
                return result.IsAuthorized;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Authorization check failed for plugin: {PluginName}, Operation: {Operation}",
                    plugin.Metadata.Name, operation);

                // Deny access if authorization fails;
                return false;
            }
        }

        private async Task<CanDisableResult> CanDisablePluginAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            var reasons = new List<string>();
            var canDisable = true;

            // Check if plugin can be disabled in its current state;
            if (plugin.State != PluginState.Enabled && plugin.State != PluginState.Disabled)
            {
                reasons.Add($"Plugin is in {plugin.State} state");
                canDisable = false;
            }

            // Check plugin-specific constraints;
            if (plugin.Plugin is IManagedPlugin managedPlugin)
            {
                var canDisableResult = await managedPlugin.CanDisableAsync(cancellationToken);
                if (!canDisableResult.CanDisable)
                {
                    reasons.AddRange(canDisableResult.Reasons);
                    canDisable = false;
                }
            }

            return new CanDisableResult;
            {
                CanDisable = canDisable,
                Reasons = reasons;
            };
        }

        private async Task<ValidationResult> ValidateConfigurationAsync(Dictionary<string, object> configuration, IPluginInstance plugin, CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            try
            {
                // Plugin-specific configuration validation;
                if (plugin.Plugin is IConfigurablePlugin configurablePlugin)
                {
                    var configResult = await configurablePlugin.ValidateConfigurationAsync(configuration, cancellationToken);
                    if (!configResult.IsValid)
                    {
                        errors.AddRange(configResult.Errors);
                        warnings.AddRange(configResult.Warnings);
                    }
                }

                // System-level configuration validation;
                foreach (var key in configuration.Keys)
                {
                    if (key.StartsWith("system."))
                    {
                        if (!_configuration.AllowedSystemConfigurations.Contains(key))
                        {
                            errors.Add($"System configuration key not allowed: {key}");
                        }
                    }
                }

                return new ValidationResult;
                {
                    IsValid = !errors.Any(),
                    Errors = errors,
                    Warnings = warnings;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Configuration validation failed for plugin: {PluginName}", plugin.Metadata.Name);
                return new ValidationResult;
                {
                    IsValid = false,
                    Errors = new List<string> { $"Configuration validation failed: {ex.Message}" }
                };
            }
        }

        private async Task<PluginMetadata> ScanPluginFileAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                // Load assembly for scanning;
                var assembly = Assembly.LoadFrom(filePath);

                // Find plugin types;
                var pluginTypes = new List<Type>();
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                    {
                        pluginTypes.Add(type);
                    }
                }

                if (!pluginTypes.Any())
                    return null;

                // Get metadata from first plugin type;
                var pluginType = pluginTypes.First();
                var pluginAttribute = pluginType.GetCustomAttribute<PluginAttribute>();

                return new PluginMetadata;
                {
                    Id = Guid.NewGuid(),
                    Name = pluginAttribute?.Name ?? pluginType.Name,
                    Description = pluginAttribute?.Description ?? string.Empty,
                    Version = pluginAttribute?.Version ?? "1.0.0",
                    Author = pluginAttribute?.Author ?? string.Empty,
                    AssemblyPath = filePath,
                    AssemblyName = assembly.FullName,
                    TypeName = pluginType.FullName,
                    DiscoveredTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to scan plugin file: {FilePath}", filePath);
                return null;
            }
        }

        private async Task<ValidationResult> ValidateAssemblyAsync(string filePath, CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var details = new Dictionary<string, object>();

            try
            {
                // Check if assembly can be loaded;
                var assembly = Assembly.LoadFrom(filePath);
                details["assemblyName"] = assembly.FullName;
                details["location"] = assembly.Location;

                // Check for strong name;
                var assemblyName = assembly.GetName();
                var publicKey = assemblyName.GetPublicKey();
                details["hasStrongName"] = publicKey != null && publicKey.Length > 0;

                // Check target framework;
                var targetFramework = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
                details["targetFramework"] = targetFramework?.FrameworkName;

                return new ValidationResult;
                {
                    IsValid = true,
                    Errors = errors,
                    Details = details;
                };
            }
            catch (BadImageFormatException ex)
            {
                errors.Add($"Invalid assembly format: {ex.Message}");
                return new ValidationResult;
                {
                    IsValid = false,
                    Errors = errors,
                    Details = details;
                };
            }
            catch (Exception ex)
            {
                errors.Add($"Assembly validation failed: {ex.Message}");
                return new ValidationResult;
                {
                    IsValid = false,
                    Errors = errors,
                    Details = details;
                };
            }
        }

        private string GetInstallationDirectory(InstallationScope scope)
        {
            return scope switch;
            {
                InstallationScope.System => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    _configuration.ApplicationName, "Plugins"),
                InstallationScope.User => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    _configuration.ApplicationName, "Plugins"),
                InstallationScope.Application => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"),
                _ => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins")
            };
        }

        private async Task<ExtractResult> ExtractPluginFilesAsync(PluginInstallRequest request, string installDir, CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var extractedFiles = new List<string>();

            try
            {
                // Determine file type and extract accordingly;
                var extension = Path.GetExtension(request.SourcePath)?.ToLowerInvariant();

                switch (extension)
                {
                    case ".zip":
                        // Extract zip archive;
                        await ExtractZipAsync(request.SourcePath, installDir, cancellationToken);
                        break;
                    case ".dll":
                    case ".exe":
                        // Copy single file;
                        var destPath = Path.Combine(installDir, Path.GetFileName(request.SourcePath));
                        await _fileManager.CopyAsync(request.SourcePath, destPath, true, cancellationToken);
                        extractedFiles.Add(destPath);
                        break;
                    default:
                        errors.Add($"Unsupported package format: {extension}");
                        break;
                }

                return new ExtractResult;
                {
                    Success = !errors.Any(),
                    ExtractedFiles = extractedFiles,
                    InstallationDirectory = installDir,
                    Errors = errors;
                };
            }
            catch (Exception ex)
            {
                errors.Add($"Extraction failed: {ex.Message}");
                return new ExtractResult;
                {
                    Success = false,
                    Errors = errors;
                };
            }
        }

        private async Task ExtractZipAsync(string zipPath, string extractDir, CancellationToken cancellationToken)
        {
            // Implementation for zip extraction;
            await Task.Delay(100, cancellationToken); // Placeholder;
        }

        private async Task<BackupResult> BackupPluginAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            try
            {
                var backupDir = Path.Combine(_configuration.BackupDirectory,
                    plugin.Metadata.Name,
                    DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));

                await _fileManager.EnsureDirectoryExistsAsync(backupDir);

                // Copy plugin files;
                var pluginDir = Path.GetDirectoryName(plugin.Metadata.AssemblyPath);
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    await _fileManager.CopyDirectoryAsync(pluginDir, backupDir, true, cancellationToken);
                }

                return BackupResult.Success(backupDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin backup failed: {PluginName}", plugin.Metadata.Name);
                return BackupResult.Failure(new[] { ex.Message }, $"Plugin backup failed: {ex.Message}");
            }
        }

        private async Task<DeleteResult> DeletePluginFilesAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var filesDeleted = 0;
            var directoriesDeleted = 0;

            try
            {
                var pluginDir = Path.GetDirectoryName(plugin.Metadata.AssemblyPath);
                if (!string.IsNullOrEmpty(pluginDir) && await _fileManager.DirectoryExistsAsync(pluginDir))
                {
                    // Delete plugin directory;
                    await _fileManager.DeleteDirectoryAsync(pluginDir, true, cancellationToken);
                    directoriesDeleted++;
                }

                return new DeleteResult;
                {
                    Success = true,
                    FilesDeleted = filesDeleted,
                    DirectoriesDeleted = directoriesDeleted;
                };
            }
            catch (Exception ex)
            {
                errors.Add($"Deletion failed: {ex.Message}");
                return new DeleteResult;
                {
                    Success = false,
                    Errors = errors;
                };
            }
        }

        private ValidationResult CreateValidationResult(bool isValid, List<string> errors, List<string> warnings,
            System.Diagnostics.Stopwatch stopwatch, Dictionary<string, object> details = null)
        {
            return new ValidationResult;
            {
                IsValid = isValid,
                Errors = errors,
                Warnings = warnings,
                ValidationTime = stopwatch.Elapsed,
                Details = details ?? new Dictionary<string, object>()
            };
        }

        private void OnPluginLoaded(IPluginInstance plugin)
        {
            PluginLoaded?.Invoke(this, new PluginLoadedEventArgs;
            {
                PluginId = plugin.Metadata.Id,
                PluginName = plugin.Metadata.Name,
                PluginMetadata = plugin.Metadata,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnPluginUnloaded(IPluginInstance plugin)
        {
            PluginUnloaded?.Invoke(this, new PluginUnloadedEventArgs;
            {
                PluginId = plugin.Metadata.Id,
                PluginName = plugin.Metadata.Name,
                PluginMetadata = plugin.Metadata,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnPluginStateChanged(IPluginInstance plugin, PluginState newState)
        {
            PluginStateChanged?.Invoke(this, new PluginStateChangedEventArgs;
            {
                PluginId = plugin.Metadata.Id,
                PluginName = plugin.Metadata.Name,
                OldState = plugin.State,
                NewState = newState,
                Timestamp = DateTime.UtcNow;
            });

            plugin.State = newState;
        }

        #endregion;

        /// <summary>
        /// Clean up resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // Unload all plugins;
                UnloadAllPluginsAsync().GetAwaiter().GetResult();

                _pluginOperationLock?.Dispose();

                // Unsubscribe events;
                if (_pluginLoader != null)
                {
                    _pluginLoader.PluginLoadError -= OnPluginLoadError;
                }

                if (_pluginRegistry != null)
                {
                    _pluginRegistry.PluginRegistered -= OnPluginRegistered;
                    _pluginRegistry.PluginUnregistered -= OnPluginUnregistered;
                }

                // Dispose components;
                (_pluginLoader as IDisposable)?.Dispose();
                (_pluginRegistry as IDisposable)?.Dispose();
                (_dependencyResolver as IDisposable)?.Dispose();
                (_securityValidator as IDisposable)?.Dispose();
                (_compatibilityChecker as IDisposable)?.Dispose();
                (_performanceMonitorInternal as IDisposable)?.Dispose();
                (_lifecycleManager as IDisposable)?.Dispose();

                _loadedPlugins.Clear();
                _availablePlugins.Clear();
                _pluginStatistics.Clear();
            }

            _disposed = true;
        }

        ~PluginManager()
        {
            Dispose(false);
        }
    }

    #region Supporting Types and Interfaces;

    public class PluginLoadRequest;
    {
        public required string PluginPath { get; set; }
        public PluginLoadConfiguration LoadConfiguration { get; set; } = new PluginLoadConfiguration();
        public bool ForceLoad { get; set; } = false;
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    public class BatchLoadRequest;
    {
        public required List<PluginLoadRequest> Plugins { get; set; }
        public BatchLoadOptions Options { get; set; } = new BatchLoadOptions();
    }

    public class PluginConfigureRequest;
    {
        public required Guid PluginId { get; set; }
        public required Dictionary<string, object> Configuration { get; set; }
        public bool ApplyImmediately { get; set; } = true;
        public bool ValidateOnly { get; set; } = false;
    }

    public class PluginCommandRequest;
    {
        public required Guid PluginId { get; set; }
        public required string Command { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public CommandExecutionOptions Options { get; set; } = new CommandExecutionOptions();
    }

    public class PluginValidationRequest;
    {
        public required string PluginPath { get; set; }
        public ValidationLevel ValidationLevel { get; set; } = ValidationLevel.Standard;
        public Dictionary<string, object> ValidationOptions { get; set; } = new Dictionary<string, object>();
    }

    public class PluginInstallRequest;
    {
        public required string SourcePath { get; set; }
        public InstallationScope InstallationScope { get; set; } = InstallationScope.User;
        public PluginLoadConfiguration LoadConfiguration { get; set; } = new PluginLoadConfiguration();
        public bool ForceInstall { get; set; } = false;
        public Dictionary<string, object> InstallationOptions { get; set; } = new Dictionary<string, object>();
    }

    public class PluginUpdateRequest;
    {
        public required Guid PluginId { get; set; }
        public required string UpdatePackagePath { get; set; }
        public required string TargetVersion { get; set; }
        public PluginLoadConfiguration LoadConfiguration { get; set; } = new PluginLoadConfiguration();
        public bool ForceUpdate { get; set; } = false;
        public bool CreateBackup { get; set; } = true;
        public Dictionary<string, object> UpdateOptions { get; set; } = new Dictionary<string, object>();
    }

    public class PluginLoadResult;
    {
        public bool Success { get; set; }
        public List<PluginMetadata> LoadedPlugins { get; set; } = new List<PluginMetadata>();
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public PluginLoadStatistics Statistics { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public static PluginLoadResult Success(List<PluginMetadata> loadedPlugins, PluginLoadStatistics statistics)
        {
            return new PluginLoadResult;
            {
                Success = true,
                LoadedPlugins = loadedPlugins,
                Statistics = statistics;
            };
        }

        public static PluginLoadResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new PluginLoadResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class BatchLoadResult;
    {
        public Guid OperationId { get; set; }
        public bool Success { get; set; }
        public int TotalRequests { get; set; }
        public int SuccessfulLoads { get; set; }
        public int FailedLoads { get; set; }
        public List<PluginLoadResult> Results { get; set; } = new List<PluginLoadResult>();
        public BatchLoadStatistics Statistics { get; set; } = new BatchLoadStatistics();
        public TimeSpan Duration { get; set; }
    }

    public class UnloadResult;
    {
        public Guid OperationId { get; set; }
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public UnloadStatistics Statistics { get; set; }
        public bool ForceUnload { get; set; } = false;

        public static UnloadResult Success(PluginMetadata metadata, UnloadStatistics statistics)
        {
            return new UnloadResult;
            {
                Success = true,
                PluginMetadata = metadata,
                Statistics = statistics;
            };
        }

        public static UnloadResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new UnloadResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class EnableResult;
    {
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();

        public static EnableResult Success(PluginMetadata metadata)
        {
            return new EnableResult;
            {
                Success = true,
                PluginMetadata = metadata;
            };
        }

        public static EnableResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new EnableResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class DisableResult;
    {
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();

        public static DisableResult Success(PluginMetadata metadata)
        {
            return new DisableResult;
            {
                Success = true,
                PluginMetadata = metadata;
            };
        }

        public static DisableResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new DisableResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class ConfigureResult;
    {
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();

        public static ConfigureResult Success(PluginMetadata metadata)
        {
            return new ConfigureResult;
            {
                Success = true,
                PluginMetadata = metadata;
            };
        }

        public static ConfigureResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new ConfigureResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class CommandResult;
    {
        public bool Success { get; set; }
        public object Result { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public CommandExecutionStatistics Statistics { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public static CommandResult Success(object result, CommandExecutionStatistics statistics)
        {
            return new CommandResult;
            {
                Success = true,
                Result = result,
                Statistics = statistics;
            };
        }

        public static CommandResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new CommandResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class ScanResult;
    {
        public bool Success { get; set; }
        public List<PluginMetadata> Plugins { get; set; } = new List<PluginMetadata>();
        public List<ScanError> Errors { get; set; } = new List<ScanError>();
        public ScanStatistics Statistics { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public static ScanResult Success(List<PluginMetadata> plugins, ScanStatistics statistics)
        {
            return new ScanResult;
            {
                Success = true,
                Plugins = plugins,
                Statistics = statistics;
            };
        }

        public static ScanResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new ScanResult;
            {
                Success = false,
                Errors = errorList.Select(e => new ScanError { Error = e }).ToList()
            };
        }
    }

    public class DependencyResult;
    {
        public bool Success { get; set; }
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public List<PluginDependency> Dependencies { get; set; } = new List<PluginDependency>();
        public List<PluginDependency> MissingDependencies { get; set; } = new List<PluginDependency>();
        public DependencyGraph DependencyGraph { get; set; }
        public bool AllResolved { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public static DependencyResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new DependencyResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class InstallResult;
    {
        public Guid OperationId { get; set; }
        public bool Success { get; set; }
        public string InstallationDirectory { get; set; }
        public List<PluginMetadata> InstalledPlugins { get; set; } = new List<PluginMetadata>();
        public List<PluginLoadResult> LoadResults { get; set; } = new List<PluginLoadResult>();
        public InstallStatistics Statistics { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class UpdateResult;
    {
        public Guid OperationId { get; set; }
        public bool Success { get; set; }
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public string PreviousVersion { get; set; }
        public string NewVersion { get; set; }
        public InstallResult InstallationResult { get; set; }
        public UpdateStatistics Statistics { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public static UpdateResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new UpdateResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class UninstallResult;
    {
        public Guid OperationId { get; set; }
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public UninstallStatistics Statistics { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public static UninstallResult Success(PluginMetadata metadata, UninstallStatistics statistics)
        {
            return new UninstallResult;
            {
                Success = true,
                PluginMetadata = metadata,
                Statistics = statistics;
            };
        }

        public static UninstallResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new UninstallResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class PluginStatistics;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public PluginState State { get; set; }
        public DateTime? LoadTime { get; set; }
        public DateTime? InitializationTime { get; set; }
        public DateTime? LastEnabledTime { get; set; }
        public DateTime? LastDisabledTime { get; set; }
        public DateTime? LastCommandExecutionTime { get; set; }
        public long CommandsExecuted { get; set; }
        public TimeSpan TotalCommandExecutionTime { get; set; }
        public long MemoryUsage { get; set; } // in bytes;
        public long CpuUsage { get; set; } // percentage;
        public List<PluginOperation> RecentOperations { get; set; } = new List<PluginOperation>();
        public Dictionary<string, object> CustomMetrics { get; set; } = new Dictionary<string, object>();

        public PluginStatistics Clone()
        {
            return new PluginStatistics;
            {
                PluginId = PluginId,
                PluginName = PluginName,
                State = State,
                LoadTime = LoadTime,
                InitializationTime = InitializationTime,
                LastEnabledTime = LastEnabledTime,
                LastDisabledTime = LastDisabledTime,
                LastCommandExecutionTime = LastCommandExecutionTime,
                CommandsExecuted = CommandsExecuted,
                TotalCommandExecutionTime = TotalCommandExecutionTime,
                MemoryUsage = MemoryUsage,
                CpuUsage = CpuUsage,
                RecentOperations = new List<PluginOperation>(RecentOperations),
                CustomMetrics = new Dictionary<string, object>(CustomMetrics)
            };
        }
    }

    public class PluginSystemStatistics;
    {
        public int TotalPluginsLoaded { get; set; }
        public int TotalPluginsAvailable { get; set; }
        public long TotalCommandsExecuted { get; set; }
        public long TotalMemoryUsage { get; set; } // in bytes;
        public double AverageLoadTime { get; set; } // in milliseconds;
        public Dictionary<PluginState, int> PluginsByState { get; set; } = new Dictionary<PluginState, int>();
        public DateTime Timestamp { get; set; }
    }

    public class PluginLoadStatistics;
    {
        public int TotalPluginsLoaded { get; set; }
        public int TotalPluginsDiscovered { get; set; }
        public TimeSpan LoadDuration { get; set; }
        public int DependenciesResolved { get; set; }
        public int DependenciesMissing { get; set; }
        public long MemoryUsageBefore { get; set; }
        public long MemoryUsageAfter { get; set; }
        public long MemoryIncrease => MemoryUsageAfter - MemoryUsageBefore;
    }

    public class BatchLoadStatistics;
    {
        public int TotalPluginsLoaded { get; set; }
        public int TotalPluginsDiscovered { get; set; }
        public int TotalDependenciesResolved { get; set; }
        public int TotalDirectoriesScanned { get; set; }
        public TimeSpan TotalLoadDuration { get; set; }
        public double AverageLoadTimePerPlugin { get; set; }
    }

    public class UnloadStatistics;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public TimeSpan UnloadDuration { get; set; }
        public int DependentPluginsUnloaded { get; set; }
        public int ResourcesReleased { get; set; }
        public int TotalPlugins { get; set; }
        public int PluginsUnloaded { get; set; }
        public int PluginsFailed { get; set; }
    }

    public class CommandExecutionStatistics;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public string Command { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public Dictionary<string, object> PerformanceMetrics { get; set; } = new Dictionary<string, object>();
    }

    public class ScanStatistics;
    {
        public string Directory { get; set; }
        public int FilesScanned { get; set; }
        public int PluginsDiscovered { get; set; }
        public int ScanErrors { get; set; }
        public TimeSpan ScanDuration { get; set; }
        public Dictionary<string, int> FileTypes { get; set; } = new Dictionary<string, int>();
    }

    public class InstallStatistics;
    {
        public TimeSpan InstallationDuration { get; set; }
        public int PluginsInstalled { get; set; }
        public int PluginsFailed { get; set; }
        public InstallationScope InstallationScope { get; set; }
        public long DiskSpaceUsed { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    public class UpdateStatistics;
    {
        public TimeSpan UpdateDuration { get; set; }
        public bool BackupCreated { get; set; }
        public string BackupPath { get; set; }
        public long DiskSpaceSaved { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    public class UninstallStatistics;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public TimeSpan UninstallDuration { get; set; }
        public int FilesDeleted { get; set; }
        public int DirectoriesDeleted { get; set; }
        public long DiskSpaceFreed { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    public class PluginMetadata : IPluginInfo;
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string AssemblyPath { get; set; }
        public string AssemblyName { get; set; }
        public string TypeName { get; set; }
        public DateTime? LoadTime { get; set; }
        public DateTime? DiscoveredTime { get; set; }
        public DateTime? LastConfigured { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        public PluginCompatibility Compatibility { get; set; }
        public SecurityLevel SecurityLevel { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class PluginLoadConfiguration;
    {
        public LoadBehavior LoadBehavior { get; set; } = LoadBehavior.Eager;
        public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.Partial;
        public Dictionary<string, object> InitialConfiguration { get; set; } = new Dictionary<string, object>();
        public bool IsDependency { get; set; } = false;
        public bool EnableLogging { get; set; } = true;
        public bool EnableMonitoring { get; set; } = true;
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    public class BatchLoadOptions;
    {
        public int MaxParallelLoads { get; set; } = Environment.ProcessorCount;
        public bool StopOnFirstError { get; set; } = false;
        public bool ContinueWithPartialResults { get; set; } = true;
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    public class CommandExecutionOptions;
    {
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool ValidateParameters { get; set; } = true;
        public bool LogExecution { get; set; } = true;
        public ExecutionPriority Priority { get; set; } = ExecutionPriority.Normal;
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    public class PluginManagerConfiguration;
    {
        public string ApplicationName { get; set; } = "NEDA";
        public List<string> PluginDirectories { get; set; } = new List<string>();
        public long MaxPluginSize { get; set; } = 100 * 1024 * 1024; // 100MB;
        public int MaxParallelLoads { get; set; } = Environment.ProcessorCount;
        public bool RequireAuthorization { get; set; } = true;
        public bool EnableSandboxing { get; set; } = true;
        public bool EnableAutoDiscovery { get; set; } = true;
        public bool EnableHotReload { get; set; } = false;
        public string BackupDirectory { get; set; } = "PluginBackups";
        public List<string> AllowedSystemConfigurations { get; set; } = new List<string>();
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }

    public class PluginLoadContext;
    {
        public PluginLoadRequest Request { get; set; }
        public Guid OperationId { get; set; }
        public DependencyResolutionResult Dependencies { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public IPerformanceCounter PerformanceCounter { get; set; }
        public Dictionary<string, object> State { get; set; } = new Dictionary<string, object>();
    }

    public class ScanError;
    {
        public string FilePath { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    public class PluginOperation;
    {
        public Guid OperationId { get; set; }
        public Guid PluginId { get; set; }
        public string OperationType { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public bool Success { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Result { get; set; } = new Dictionary<string, object>();
    }

    public enum PluginState;
    {
        Unknown,
        Discovered,
        Created,
        Initializing,
        Initialized,
        Enabling,
        Enabled,
        Disabling,
        Disabled,
        Configuring,
        ShuttingDown,
        Shutdown,
        Unloaded,
        Error;
    }

    public enum LoadBehavior;
    {
        Eager,
        Lazy,
        OnDemand;
    }

    public enum IsolationLevel;
    {
        None,
        Partial,
        Full,
        Sandboxed;
    }

    public enum ValidationLevel;
    {
        None,
        Basic,
        Standard,
        Strict,
        Exhaustive;
    }

    public enum InstallationScope;
    {
        System,
        User,
        Application,
        Temporary;
    }

    public enum ExecutionPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    public enum SecurityLevel;
    {
        Unknown,
        Trusted,
        Verified,
        Unverified,
        Restricted,
        Blocked;
    }

    public enum PluginOperationType;
    {
        Load,
        Unload,
        Enable,
        Disable,
        Configure,
        ExecuteCommand,
        Install,
        Update,
        Uninstall;
    }

    public interface IPluginInstance;
    {
        PluginMetadata Metadata { get; }
        IPlugin Plugin { get; }
        PluginState State { get; set; }
        PluginLoadContext LoadContext { get; }
        Dictionary<string, object> RuntimeData { get; }
    }

    public interface IPluginInfo;
    {
        Guid Id { get; }
        string Name { get; }
        string Description { get; }
        string Version { get; }
        string Author { get; }
    }

    public interface IPlugin;
    {
        Task InitializeAsync(PluginInitializeRequest request, CancellationToken cancellationToken);
        Task ShutdownAsync(CancellationToken cancellationToken);
        Task<object> ExecuteCommandAsync(string command, Dictionary<string, object> parameters, CancellationToken cancellationToken);
    }

    public interface IManagedPlugin : IPlugin;
    {
        Task<CanUnloadResult> CanUnloadAsync(CancellationToken cancellationToken);
        Task<CanDisableResult> CanDisableAsync(CancellationToken cancellationToken);
        Task<ValidationResult> ValidateConfigurationAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken);
    }

    public interface IConfigurablePlugin : IPlugin;
    {
        Task<ConfigurationResult> ConfigureAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken);
    }

    public class PluginInitializeRequest;
    {
        public Guid PluginId { get; set; }
        public Dictionary<string, object> Configuration { get; set; }
        public PluginContext Context { get; set; }
    }

    public class PluginContext;
    {
        public IPluginManager PluginManager { get; set; }
        public IEventBus EventBus { get; set; }
        public ILogger Logger { get; set; }
        public PluginManagerConfiguration Configuration { get; set; }
        public Dictionary<string, object> SharedData { get; set; } = new Dictionary<string, object>();
    }

    public class CanUnloadResult;
    {
        public bool CanUnload { get; set; }
        public List<string> Reasons { get; set; } = new List<string>();
        public bool ForceUnload { get; set; } = false;
    }

    public class CanDisableResult;
    {
        public bool CanDisable { get; set; }
        public List<string> Reasons { get; set; } = new List<string>();
    }

    public class ConfigurationResult;
    {
        public bool Success { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, object> AppliedConfiguration { get; set; } = new Dictionary<string, object>();
    }

    public class PluginDependency;
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public bool IsRequired { get; set; } = true;
        public string MinimumVersion { get; set; }
        public string MaximumVersion { get; set; }
        public DependencyType Type { get; set; }
    }

    public class DependencyResolutionResult;
    {
        public List<PluginDependency> ResolvedDependencies { get; set; } = new List<PluginDependency>();
        public List<PluginDependency> MissingDependencies { get; set; } = new List<PluginDependency>();
        public DependencyGraph DependencyGraph { get; set; }
        public bool AllResolved { get; set; }
    }

    public class DependencyGraph;
    {
        public Dictionary<string, List<string>> AdjacencyList { get; set; } = new Dictionary<string, List<string>>();
        public List<DependencyCycle> Cycles { get; set; } = new List<DependencyCycle>();
        public bool HasCycles => Cycles.Any();
    }

    public class DependencyCycle;
    {
        public List<string> Plugins { get; set; } = new List<string>();
    }

    public enum DependencyType;
    {
        Assembly,
        Plugin,
        System,
        External;
    }

    public class PluginCompatibility;
    {
        public Version MinSystemVersion { get; set; }
        public Version MaxSystemVersion { get; set; }
        public List<string> SupportedPlatforms { get; set; } = new List<string>();
        public List<string> RequiredFeatures { get; set; } = new List<string>();
        public CompatibilityStatus Status { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
    }

    public enum CompatibilityStatus;
    {
        Unknown,
        Compatible,
        PartiallyCompatible,
        Incompatible,
        RequiresUpdate;
    }

    public class ExtractResult;
    {
        public bool Success { get; set; }
        public List<string> ExtractedFiles { get; set; } = new List<string>();
        public string InstallationDirectory { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class BackupResult;
    {
        public bool Success { get; set; }
        public string BackupPath { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public static BackupResult Success(string backupPath)
        {
            return new BackupResult;
            {
                Success = true,
                BackupPath = backupPath;
            };
        }

        public static BackupResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new BackupResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class DeleteResult;
    {
        public bool Success { get; set; }
        public int FilesDeleted { get; set; }
        public int DirectoriesDeleted { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class RegistrationResult;
    {
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public static RegistrationResult Success(PluginMetadata metadata)
        {
            return new RegistrationResult;
            {
                Success = true,
                PluginMetadata = metadata;
            };
        }

        public static RegistrationResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new RegistrationResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class InitializationResult;
    {
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public static InitializationResult Success(PluginMetadata metadata)
        {
            return new InitializationResult;
            {
                Success = true,
                PluginMetadata = metadata;
            };
        }

        public static InitializationResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new InitializationResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    public class ShutdownResult;
    {
        public bool Success { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public int ResourcesReleased { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        public static ShutdownResult Success(PluginMetadata metadata, int resourcesReleased)
        {
            return new ShutdownResult;
            {
                Success = true,
                PluginMetadata = metadata,
                ResourcesReleased = resourcesReleased;
            };
        }

        public static ShutdownResult Failure(IEnumerable<string> errors, string message = null)
        {
            var errorList = errors.ToList();
            if (!string.IsNullOrEmpty(message))
                errorList.Insert(0, message);

            return new ShutdownResult;
            {
                Success = false,
                Errors = errorList;
            };
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class PluginAttribute : Attribute;
    {
        public string Name { get; }
        public string Version { get; set; } = "1.0.0";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public string[] Dependencies { get; set; } = Array.Empty<string>();

        public PluginAttribute(string name)
        {
            Name = name;
        }
    }

    public class PluginInstance : IPluginInstance;
    {
        public PluginMetadata Metadata { get; set; }
        public IPlugin Plugin { get; set; }
        public PluginState State { get; set; }
        public PluginLoadContext LoadContext { get; set; }
        public Dictionary<string, object> RuntimeData { get; set; } = new Dictionary<string, object>();
    }

    #region Event Arguments;

    public class PluginStateChangedEventArgs : EventArgs;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public PluginState OldState { get; set; }
        public PluginState NewState { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public class PluginLoadedEventArgs : EventArgs;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public class PluginUnloadedEventArgs : EventArgs;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public PluginMetadata PluginMetadata { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public class PluginErrorEventArgs : EventArgs;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public Exception Error { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    public class PluginLoadErrorEventArgs : EventArgs;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public string PluginPath { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PluginRegisteredEventArgs : EventArgs;
    {
        public PluginMetadata PluginMetadata { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PluginUnregisteredEventArgs : EventArgs;
    {
        public string PluginName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class PluginLoadException : Exception
    {
        public Guid OperationId { get; }
        public DateTime Timestamp { get; }

        public PluginLoadException(string message) : base(message)
        {
            Timestamp = DateTime.UtcNow;
        }

        public PluginLoadException(string message, Exception innerException) : base(message, innerException)
        {
            Timestamp = DateTime.UtcNow;
        }

        public PluginLoadException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class PluginUnloadException : Exception
    {
        public Guid OperationId { get; }

        public PluginUnloadException(string message) : base(message) { }
        public PluginUnloadException(string message, Exception innerException) : base(message, innerException) { }
        public PluginUnloadException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginOperationException : Exception
    {
        public Guid OperationId { get; }

        public PluginOperationException(string message) : base(message) { }
        public PluginOperationException(string message, Exception innerException) : base(message, innerException) { }
        public PluginOperationException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginCommandException : Exception
    {
        public Guid OperationId { get; }

        public PluginCommandException(string message) : base(message) { }
        public PluginCommandException(string message, Exception innerException) : base(message, innerException) { }
        public PluginCommandException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginScanException : Exception
    {
        public Guid OperationId { get; }

        public PluginScanException(string message) : base(message) { }
        public PluginScanException(string message, Exception innerException) : base(message, innerException) { }
        public PluginScanException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginValidationException : Exception
    {
        public Guid OperationId { get; }

        public PluginValidationException(string message) : base(message) { }
        public PluginValidationException(string message, Exception innerException) : base(message, innerException) { }
        public PluginValidationException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginDependencyException : Exception
    {
        public PluginDependencyException(string message) : base(message) { }
        public PluginDependencyException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PluginInstallException : Exception
    {
        public Guid OperationId { get; }

        public PluginInstallException(string message) : base(message) { }
        public PluginInstallException(string message, Exception innerException) : base(message, innerException) { }
        public PluginInstallException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginUpdateException : Exception
    {
        public Guid OperationId { get; }

        public PluginUpdateException(string message) : base(message) { }
        public PluginUpdateException(string message, Exception innerException) : base(message, innerException) { }
        public PluginUpdateException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginUninstallException : Exception
    {
        public Guid OperationId { get; }

        public PluginUninstallException(string message) : base(message) { }
        public PluginUninstallException(string message, Exception innerException) : base(message, innerException) { }
        public PluginUninstallException(string message, Exception innerException, Guid operationId)
            : base(message, innerException)
        {
            OperationId = operationId;
        }
    }

    public class PluginDiscoveryException : Exception
    {
        public PluginDiscoveryException(string message) : base(message) { }
        public PluginDiscoveryException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PluginCreationException : Exception
    {
        public PluginCreationException(string message) : base(message) { }
        public PluginCreationException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Internal Components;

    internal class PluginDependencyResolver;
    {
        private readonly ILogger _logger;
        private readonly IPluginRegistry _pluginRegistry

        public PluginDependencyResolver(ILogger logger, IPluginRegistry pluginRegistry)
        {
            _logger = logger;
            _pluginRegistry = pluginRegistry
        }

        public async Task<DependencyResolutionResult> ResolveDependenciesAsync(string pluginPath, CancellationToken cancellationToken)
        {
            var result = new DependencyResolutionResult();

            try
            {
                // Load assembly and read dependencies;
                var assembly = Assembly.LoadFrom(pluginPath);
                var pluginAttribute = assembly.GetCustomAttribute<PluginAttribute>();

                if (pluginAttribute?.Dependencies != null)
                {
                    foreach (var depName in pluginAttribute.Dependencies)
                    {
                        var dependency = await ResolveDependencyAsync(depName, cancellationToken);
                        if (dependency != null)
                        {
                            result.ResolvedDependencies.Add(dependency);
                        }
                        else;
                        {
                            result.MissingDependencies.Add(new PluginDependency;
                            {
                                Name = depName,
                                Version = "unknown",
                                IsRequired = true;
                            });
                        }
                    }
                }

                result.AllResolved = !result.MissingDependencies.Any();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve dependencies for plugin: {PluginPath}", pluginPath);
                throw new PluginDependencyException($"Dependency resolution failed: {ex.Message}", ex);
            }
        }

        private async Task<PluginDependency> ResolveDependencyAsync(string dependencyName, CancellationToken cancellationToken)
        {
            // Check if dependency is already loaded;
            var pluginInfo = await _pluginRegistry.GetPluginInfoAsync(dependencyName, cancellationToken);
            if (pluginInfo != null)
            {
                return new PluginDependency;
                {
                    Name = dependencyName,
                    Version = pluginInfo.Version,
                    IsRequired = true;
                };
            }

            // Check system assemblies;
            var systemAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == dependencyName);

            if (systemAssembly != null)
            {
                return new PluginDependency;
                {
                    Name = dependencyName,
                    Version = systemAssembly.GetName().Version.ToString(),
                    IsRequired = true,
                    Type = DependencyType.System;
                };
            }

            return null;
        }

        public void Dispose()
        {
            // Cleanup if needed;
        }
    }

    internal class PluginSecurityValidator;
    {
        private readonly ILogger _logger;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly PluginManagerConfiguration _configuration;

        public PluginSecurityValidator(ILogger logger, ICryptoEngine cryptoEngine, PluginManagerConfiguration configuration)
        {
            _logger = logger;
            _cryptoEngine = cryptoEngine;
            _configuration = configuration;
        }

        public async Task<SecurityValidationResult> ValidatePluginAsync(string pluginPath, CancellationToken cancellationToken)
        {
            var result = new SecurityValidationResult();

            try
            {
                // Check file signature;
                var signatureValid = await ValidateSignatureAsync(pluginPath, cancellationToken);
                if (!signatureValid)
                {
                    result.Issues.Add("Plugin signature is invalid or missing");
                    result.SecurityLevel = SecurityLevel.Unverified;
                }

                // Check file permissions;
                var permissionsValid = await CheckPermissionsAsync(pluginPath, cancellationToken);
                if (!permissionsValid)
                {
                    result.Issues.Add("Plugin has excessive permissions");
                    result.SecurityLevel = SecurityLevel.Restricted;
                }

                // Scan for malicious code patterns;
                var maliciousCodeFound = await ScanForMaliciousCodeAsync(pluginPath, cancellationToken);
                if (maliciousCodeFound)
                {
                    result.Issues.Add("Potential malicious code detected");
                    result.SecurityLevel = SecurityLevel.Blocked;
                }

                // Calculate hash for verification;
                var fileHash = await CalculateFileHashAsync(pluginPath, cancellationToken);
                result.FileHash = fileHash;

                if (!result.Issues.Any())
                {
                    result.IsValid = true;
                    result.SecurityLevel = SecurityLevel.Verified;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Security validation failed for plugin: {PluginPath}", pluginPath);
                result.Issues.Add($"Security validation failed: {ex.Message}");
                return result;
            }
        }

        private async Task<bool> ValidateSignatureAsync(string pluginPath, CancellationToken cancellationToken)
        {
            // Implementation for signature validation;
            await Task.Delay(1, cancellationToken); // Placeholder;
            return true; // Assume valid for now;
        }

        private async Task<bool> CheckPermissionsAsync(string pluginPath, CancellationToken cancellationToken)
        {
            // Implementation for permission checking;
            await Task.Delay(1, cancellationToken); // Placeholder;
            return true; // Assume safe for now;
        }

        private async Task<bool> ScanForMaliciousCodeAsync(string pluginPath, CancellationToken cancellationToken)
        {
            // Implementation for malicious code scanning;
            await Task.Delay(1, cancellationToken); // Placeholder;
            return false; // Assume safe for now;
        }

        private async Task<string> CalculateFileHashAsync(string pluginPath, CancellationToken cancellationToken)
        {
            using var stream = File.OpenRead(pluginPath);
            var hash = await _cryptoEngine.ComputeHashAsync(stream, cancellationToken);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public void Dispose()
        {
            // Cleanup if needed;
        }
    }

    internal class PluginCompatibilityChecker;
    {
        private readonly ILogger _logger;
        private readonly PluginManagerConfiguration _configuration;

        public PluginCompatibilityChecker(ILogger logger, PluginManagerConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<CompatibilityCheckResult> CheckCompatibilityAsync(string pluginPath, CancellationToken cancellationToken)
        {
            var result = new CompatibilityCheckResult();

            try
            {
                // Load assembly for inspection;
                var assembly = Assembly.LoadFrom(pluginPath);
                var assemblyName = assembly.GetName();

                // Check .NET version compatibility;
                var netVersion = assemblyName.Version;
                var currentVersion = Environment.Version;

                if (netVersion.Major > currentVersion.Major)
                {
                    result.Issues.Add($"Plugin requires .NET {netVersion.Major}.x, current is {currentVersion.Major}.x");
                    result.IsCompatible = false;
                }

                // Check platform compatibility;
                var platformAttribute = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
                if (platformAttribute != null)
                {
                    // Parse platform requirements;
                    if (!IsPlatformCompatible(platformAttribute.FrameworkName))
                    {
                        result.Issues.Add($"Plugin requires framework: {platformAttribute.FrameworkName}");
                        result.IsCompatible = false;
                    }
                }

                // Check assembly references;
                var referencedAssemblies = assembly.GetReferencedAssemblies();
                foreach (var refAssembly in referencedAssemblies)
                {
                    try
                    {
                        Assembly.Load(refAssembly);
                    }
                    catch (FileNotFoundException)
                    {
                        result.Issues.Add($"Missing referenced assembly: {refAssembly.Name} {refAssembly.Version}");
                        result.IsCompatible = false;
                    }
                }

                if (result.Issues.Count == 0)
                {
                    result.IsCompatible = true;
                    result.CompatibilityStatus = CompatibilityStatus.Compatible;
                }
                else if (result.Issues.Count <= 2)
                {
                    result.CompatibilityStatus = CompatibilityStatus.PartiallyCompatible;
                }
                else;
                {
                    result.CompatibilityStatus = CompatibilityStatus.Incompatible;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compatibility check failed for plugin: {PluginPath}", pluginPath);
                result.Issues.Add($"Compatibility check failed: {ex.Message}");
                result.IsCompatible = false;
                result.CompatibilityStatus = CompatibilityStatus.Unknown;
                return result;
            }
        }

        private bool IsPlatformCompatible(string frameworkName)
        {
            // Simple platform compatibility check;
            return frameworkName.Contains("netstandard") ||
                   frameworkName.Contains("netcoreapp") ||
                   frameworkName.Contains("net") && frameworkName.Contains(Environment.Version.Major.ToString());
        }

        public void Dispose()
        {
            // Cleanup if needed;
        }
    }

    internal class PluginPerformanceMonitor;
    {
        private readonly ILogger _logger;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ConcurrentDictionary<Guid, IPerformanceCounter> _pluginCounters;

        public PluginPerformanceMonitor(ILogger logger, IPerformanceMonitor performanceMonitor)
        {
            _logger = logger;
            _performanceMonitor = performanceMonitor;
            _pluginCounters = new ConcurrentDictionary<Guid, IPerformanceCounter>();
        }

        public void StartMonitoring(Guid pluginId, string pluginName)
        {
            var counter = _performanceMonitor.StartCounter("Plugin", $"{pluginName}_{pluginId}");
            _pluginCounters[pluginId] = counter;
        }

        public void StopMonitoring(Guid pluginId)
        {
            if (_pluginCounters.TryRemove(pluginId, out var counter))
            {
                counter.Stop();
            }
        }

        public void RecordMetric(Guid pluginId, string metric, double value)
        {
            if (_pluginCounters.TryGetValue(pluginId, out var counter))
            {
                counter.RecordMetric(metric, value);
            }
        }

        public PluginStatistics GetStatistics(Guid pluginId)
        {
            if (_pluginCounters.TryGetValue(pluginId, out var counter))
            {
                var metrics = counter.GetMetrics();
                return new PluginStatistics;
                {
                    PluginId = pluginId,
                    MemoryUsage = (long)(metrics.GetValueOrDefault("MemoryUsage", 0)),
                    CpuUsage = (long)(metrics.GetValueOrDefault("CpuUsage", 0))
                };
            }

            return new PluginStatistics { PluginId = pluginId };
        }

        public void Dispose()
        {
            foreach (var counter in _pluginCounters.Values)
            {
                counter.Stop();
            }
            _pluginCounters.Clear();
        }
    }

    internal class PluginLifecycleManager;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly PluginManagerConfiguration _configuration;

        public PluginLifecycleManager(ILogger logger, IEventBus eventBus, PluginManagerConfiguration configuration)
        {
            _logger = logger;
            _eventBus = eventBus;
            _configuration = configuration;
        }

        public async Task<EnableResult> EnablePluginAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            try
            {
                plugin.State = PluginState.Enabling;

                // Fire enabling event;
                await _eventBus.PublishAsync(new PluginEnablingEvent;
                {
                    PluginId = plugin.Metadata.Id,
                    PluginName = plugin.Metadata.Name,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                // Enable plugin logic;
                // This would involve calling plugin-specific enable methods;

                plugin.State = PluginState.Enabled;

                // Fire enabled event;
                await _eventBus.PublishAsync(new PluginEnabledEvent;
                {
                    PluginId = plugin.Metadata.Id,
                    PluginName = plugin.Metadata.Name,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                return EnableResult.Success(plugin.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enable plugin: {PluginName}", plugin.Metadata.Name);
                plugin.State = PluginState.Error;
                return EnableResult.Failure(new[] { ex.Message }, $"Enable failed: {ex.Message}");
            }
        }

        public async Task<DisableResult> DisablePluginAsync(IPluginInstance plugin, CancellationToken cancellationToken)
        {
            try
            {
                plugin.State = PluginState.Disabling;

                // Fire disabling event;
                await _eventBus.PublishAsync(new PluginDisablingEvent;
                {
                    PluginId = plugin.Metadata.Id,
                    PluginName = plugin.Metadata.Name,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                // Disable plugin logic;

                plugin.State = PluginState.Disabled;

                // Fire disabled event;
                await _eventBus.PublishAsync(new PluginDisabledEvent;
                {
                    PluginId = plugin.Metadata.Id,
                    PluginName = plugin.Metadata.Name,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                return DisableResult.Success(plugin.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to disable plugin: {PluginName}", plugin.Metadata.Name);
                plugin.State = PluginState.Error;
                return DisableResult.Failure(new[] { ex.Message }, $"Disable failed: {ex.Message}");
            }
        }

        public async Task<ConfigureResult> ConfigurePluginAsync(IPluginInstance plugin, Dictionary<string, object> configuration, CancellationToken cancellationToken)
        {
            try
            {
                plugin.State = PluginState.Configuring;

                // Apply configuration;
                if (plugin.Plugin is IConfigurablePlugin configurablePlugin)
                {
                    var configResult = await configurablePlugin.ConfigureAsync(configuration, cancellationToken);
                    if (!configResult.Success)
                    {
                        return ConfigureResult.Failure(configResult.Errors, $"Configuration failed");
                    }
                }

                plugin.State = PluginState.Enabled; // Return to enabled state;

                return ConfigureResult.Success(plugin.Metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure plugin: {PluginName}", plugin.Metadata.Name);
                plugin.State = PluginState.Error;
                return ConfigureResult.Failure(new[] { ex.Message }, $"Configuration failed: {ex.Message}");
            }
        }

        public async Task<CommandResult> ExecuteCommandAsync(IPluginInstance plugin, PluginCommandRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // Execute plugin command;
                var result = await plugin.Plugin.ExecuteCommandAsync(request.Command, request.Parameters, cancellationToken);

                return CommandResult.Success(result, new CommandExecutionStatistics;
                {
                    PluginId = request.PluginId,
                    PluginName = plugin.Metadata.Name,
                    Command = request.Command,
                    Success = true,
                    StartTime = DateTime.UtcNow.AddSeconds(-1), // Placeholder;
                    EndTime = DateTime.UtcNow()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command execution failed for plugin: {PluginName}, Command: {Command}",
                    plugin.Metadata.Name, request.Command);

                return CommandResult.Failure(new[] { ex.Message }, $"Command execution failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // Cleanup if needed;
        }
    }

    public class SecurityValidationResult;
    {
        public bool IsValid { get; set; }
        public SecurityLevel SecurityLevel { get; set; } = SecurityLevel.Unknown;
        public List<string> Issues { get; set; } = new List<string>();
        public string FileHash { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    public class CompatibilityCheckResult;
    {
        public bool IsCompatible { get; set; }
        public CompatibilityStatus CompatibilityStatus { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    #region Events;

    public class PluginEnablingEvent : IEvent;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => Timestamp;
    }

    public class PluginEnabledEvent : IEvent;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => Timestamp;
    }

    public class PluginDisablingEvent : IEvent;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => Timestamp;
    }

    public class PluginDisabledEvent : IEvent;
    {
        public Guid PluginId { get; set; }
        public string PluginName { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId => Guid.NewGuid();
        public DateTime OccurredOn => Timestamp;
    }

    #endregion;

    #endregion;
}
