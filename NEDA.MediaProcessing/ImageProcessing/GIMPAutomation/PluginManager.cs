using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NEDA.Logging;
using NEDA.Common.Utilities;
using NEDA.EngineIntegration.PluginSystem.PluginLoader;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;

namespace NEDA.EngineIntegration.PluginSystem.PluginManager;
{
    /// <summary>
    /// Advanced plugin management system with support for dynamic loading, unloading, dependency resolution,
    /// versioning, and sandboxed execution environments;
    /// </summary>
    public class PluginManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly PluginLoader _pluginLoader;
        private readonly AssemblyResolver _assemblyResolver;
        private readonly PluginRegistry _pluginRegistry
        private readonly PluginDependencyResolver _dependencyResolver;
        private readonly PluginSecurityManager _securityManager;

        private readonly ConcurrentDictionary<string, PluginContext> _loadedPlugins;
        private readonly ConcurrentDictionary<Type, List<IPlugin>> _pluginsByType;
        private readonly PluginSettings _settings;
        private readonly PluginEventBus _eventBus;

        private bool _isInitialized;
        private bool _isDisposed;
        private readonly object _syncLock = new object();

        /// <summary>
        /// Plugin manager settings;
        /// </summary>
        public class PluginSettings;
        {
            public List<string> PluginDirectories { get; set; } = new List<string>();
            public List<string> AssemblyProbingPaths { get; set; } = new List<string>();
            public bool EnableHotReload { get; set; } = false;
            public bool EnableSandboxing { get; set; } = true;
            public bool EnableDependencyValidation { get; set; } = true;
            public bool EnableVersionChecking { get; set; } = true;
            public TimeSpan PluginTimeout { get; set; } = TimeSpan.FromSeconds(30);
            public int MaxConcurrentLoads { get; set; } = 4;
            public bool LoadOnStartup { get; set; } = true;
            public PluginLoadMode DefaultLoadMode { get; set; } = PluginLoadMode.Lazy;
            public SecurityPolicy SecurityPolicy { get; set; } = new SecurityPolicy();
        }

        /// <summary>
        /// Plugin loading modes;
        /// </summary>
        public enum PluginLoadMode;
        {
            Immediate,
            Lazy,
            OnDemand,
            Background;
        }

        /// <summary>
        /// Plugin lifecycle states;
        /// </summary>
        public enum PluginState;
        {
            NotLoaded,
            Loading,
            Loaded,
            Initializing,
            Initialized,
            Starting,
            Running,
            Stopping,
            Stopped,
            Unloading,
            Unloaded,
            Error,
            Disabled;
        }

        /// <summary>
        /// Initialize PluginManager with dependencies;
        /// </summary>
        public PluginManager(
            IServiceProvider serviceProvider,
            ILogger logger,
            IConfiguration configuration,
            PluginRegistry pluginRegistry)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _pluginRegistry = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));

            // Initialize components;
            _assemblyResolver = new AssemblyResolver();
            _pluginLoader = new PluginLoader(_assemblyResolver, logger);
            _dependencyResolver = new PluginDependencyResolver(logger);
            _securityManager = new PluginSecurityManager(configuration, logger);

            _loadedPlugins = new ConcurrentDictionary<string, PluginContext>();
            _pluginsByType = new ConcurrentDictionary<Type, List<IPlugin>>();
            _eventBus = new PluginEventBus(logger);

            // Load settings;
            _settings = LoadSettings();

            _logger.LogInformation("PluginManager initialized");
        }

        /// <summary>
        /// Initialize the plugin manager and discover available plugins;
        /// </summary>
        public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return true;

            lock (_syncLock)
            {
                if (_isInitialized)
                    return true;

                try
                {
                    _logger.LogInformation("Initializing PluginManager");

                    // Initialize assembly resolver with probing paths;
                    _assemblyResolver.AddProbingPaths(_settings.AssemblyProbingPaths);

                    // Initialize security manager;
                    _securityManager.Initialize(_settings.SecurityPolicy);

                    // Discover available plugins;
                    var discoveredPlugins = DiscoverPlugins();
                    _logger.LogInformation($"Discovered {discoveredPlugins.Count} plugins");

                    // Register discovered plugins;
                    foreach (var pluginInfo in discoveredPlugins)
                    {
                        _pluginRegistry.Register(pluginInfo);
                    }

                    // Load plugins if configured;
                    if (_settings.LoadOnStartup)
                    {
                        Task.Run(() => LoadConfiguredPluginsAsync(cancellationToken));
                    }

                    _isInitialized = true;

                    _logger.LogInformation("PluginManager initialized successfully");
                    _eventBus.Publish(new PluginManagerInitializedEvent());

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize PluginManager");
                    return false;
                }
            }
        }

        /// <summary>
        /// Load a plugin by its identifier;
        /// </summary>
        public async Task<PluginLoadResult> LoadPluginAsync(string pluginId, PluginLoadOptions options = null)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID cannot be empty", nameof(pluginId));

            try
            {
                _logger.LogInformation($"Loading plugin: {pluginId}");

                // Check if already loaded;
                if (_loadedPlugins.TryGetValue(pluginId, out var existingContext))
                {
                    _logger.LogWarning($"Plugin {pluginId} is already loaded");
                    return new PluginLoadResult;
                    {
                        Success = true,
                        PluginId = pluginId,
                        Message = "Plugin already loaded",
                        PluginContext = existingContext;
                    };
                }

                // Get plugin metadata from registry
                var pluginInfo = _pluginRegistry.GetPlugin(pluginId);
                if (pluginInfo == null)
                {
                    throw new PluginNotFoundException($"Plugin {pluginId} not found in registry");
                }

                // Validate security;
                var securityResult = await _securityManager.ValidatePluginAsync(pluginInfo);
                if (!securityResult.IsAllowed)
                {
                    throw new PluginSecurityException($"Plugin {pluginId} failed security validation: {securityResult.Reason}");
                }

                // Resolve dependencies;
                var dependencies = await _dependencyResolver.ResolveDependenciesAsync(pluginInfo);
                if (!dependencies.Resolved)
                {
                    throw new PluginDependencyException($"Unresolved dependencies for plugin {pluginId}: {string.Join(", ", dependencies.MissingDependencies)}");
                }

                // Create plugin context;
                var context = new PluginContext(pluginInfo)
                {
                    State = PluginState.Loading;
                };

                // Load dependencies first;
                foreach (var dependency in dependencies.Dependencies)
                {
                    if (!_loadedPlugins.ContainsKey(dependency.PluginId))
                    {
                        await LoadPluginAsync(dependency.PluginId, new PluginLoadOptions { IsDependency = true });
                    }
                }

                // Load the plugin assembly;
                var assembly = await _pluginLoader.LoadPluginAsync(pluginInfo.AssemblyPath, _settings.EnableSandboxing);

                // Create plugin instance;
                var pluginInstance = CreatePluginInstance(assembly, pluginInfo);
                if (pluginInstance == null)
                {
                    throw new PluginLoadException($"Failed to create instance of plugin {pluginId}");
                }

                context.PluginInstance = pluginInstance;
                context.Assembly = assembly;
                context.State = PluginState.Loaded;

                // Initialize the plugin;
                await InitializePluginAsync(context, options);

                // Register plugin;
                if (!_loadedPlugins.TryAdd(pluginId, context))
                {
                    throw new PluginLoadException($"Failed to register plugin {pluginId}");
                }

                // Register by type;
                RegisterPluginByType(pluginInstance);

                // Update registry
                pluginInfo.LastLoadTime = DateTime.UtcNow;
                _pluginRegistry.Update(pluginInfo);

                _logger.LogInformation($"Plugin loaded successfully: {pluginId} v{pluginInfo.Version}");
                _eventBus.Publish(new PluginLoadedEvent(pluginInfo));

                return new PluginLoadResult;
                {
                    Success = true,
                    PluginId = pluginId,
                    PluginContext = context,
                    Message = "Plugin loaded successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load plugin: {pluginId}");

                return new PluginLoadResult;
                {
                    Success = false,
                    PluginId = pluginId,
                    Error = ex,
                    Message = $"Failed to load plugin: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Unload a plugin by its identifier;
        /// </summary>
        public async Task<PluginUnloadResult> UnloadPluginAsync(string pluginId, bool force = false)
        {
            ValidateInitialized();

            if (!_loadedPlugins.TryGetValue(pluginId, out var context))
            {
                return new PluginUnloadResult;
                {
                    Success = false,
                    PluginId = pluginId,
                    Message = $"Plugin {pluginId} is not loaded"
                };
            }

            try
            {
                _logger.LogInformation($"Unloading plugin: {pluginId}");

                // Check if other plugins depend on this one;
                if (!force)
                {
                    var dependents = GetDependentPlugins(pluginId);
                    if (dependents.Any())
                    {
                        throw new PluginUnloadException($"Cannot unload plugin {pluginId}: {dependents.Count} plugins depend on it");
                    }
                }

                context.State = PluginState.Stopping;

                // Stop the plugin if it's running;
                if (context.PluginInstance is IRunnablePlugin runnablePlugin && context.IsRunning)
                {
                    await runnablePlugin.StopAsync();
                    context.IsRunning = false;
                }

                // Uninitialize the plugin;
                if (context.IsInitialized)
                {
                    await UninitializePluginAsync(context);
                }

                // Unregister from type registry
                UnregisterPluginByType(context.PluginInstance);

                // Remove from loaded plugins;
                _loadedPlugins.TryRemove(pluginId, out _);

                // Unload assembly;
                if (_settings.EnableSandboxing)
                {
                    await _pluginLoader.UnloadPluginAsync(context.Assembly);
                }

                context.State = PluginState.Unloaded;
                context.PluginInstance = null;
                context.Assembly = null;

                _logger.LogInformation($"Plugin unloaded: {pluginId}");
                _eventBus.Publish(new PluginUnloadedEvent(pluginId));

                return new PluginUnloadResult;
                {
                    Success = true,
                    PluginId = pluginId,
                    Message = "Plugin unloaded successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to unload plugin: {pluginId}");

                context.State = PluginState.Error;

                return new PluginUnloadResult;
                {
                    Success = false,
                    PluginId = pluginId,
                    Error = ex,
                    Message = $"Failed to unload plugin: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Get all loaded plugins;
        /// </summary>
        public IEnumerable<IPlugin> GetAllPlugins()
        {
            return _loadedPlugins.Values;
                .Where(c => c.PluginInstance != null)
                .Select(c => c.PluginInstance);
        }

        /// <summary>
        /// Get plugins of a specific type;
        /// </summary>
        public IEnumerable<T> GetPlugins<T>() where T : IPlugin;
        {
            var type = typeof(T);
            if (_pluginsByType.TryGetValue(type, out var plugins))
            {
                return plugins.OfType<T>();
            }
            return Enumerable.Empty<T>();
        }

        /// <summary>
        /// Get plugin context by ID;
        /// </summary>
        public PluginContext GetPluginContext(string pluginId)
        {
            _loadedPlugins.TryGetValue(pluginId, out var context);
            return context;
        }

        /// <summary>
        /// Get plugin instance by ID;
        /// </summary>
        public T GetPlugin<T>(string pluginId) where T : IPlugin;
        {
            if (_loadedPlugins.TryGetValue(pluginId, out var context))
            {
                return (T)context.PluginInstance;
            }
            return default;
        }

        /// <summary>
        /// Execute a function on all plugins of a specific type;
        /// </summary>
        public async Task ExecuteOnPluginsAsync<T>(Func<T, Task> action, CancellationToken cancellationToken = default) where T : IPlugin;
        {
            var plugins = GetPlugins<T>().ToList();

            if (plugins.Count == 0)
                return;

            var tasks = plugins.Select(plugin => ExecuteOnPluginAsync(plugin, action, cancellationToken));
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Reload a plugin (unload and load again)
        /// </summary>
        public async Task<PluginReloadResult> ReloadPluginAsync(string pluginId, PluginLoadOptions options = null)
        {
            try
            {
                _logger.LogInformation($"Reloading plugin: {pluginId}");

                // Unload first;
                var unloadResult = await UnloadPluginAsync(pluginId, true);
                if (!unloadResult.Success)
                {
                    return new PluginReloadResult;
                    {
                        Success = false,
                        PluginId = pluginId,
                        Message = $"Failed to unload during reload: {unloadResult.Message}"
                    };
                }

                // Wait a bit to ensure clean unload;
                await Task.Delay(100);

                // Load again;
                var loadResult = await LoadPluginAsync(pluginId, options);

                return new PluginReloadResult;
                {
                    Success = loadResult.Success,
                    PluginId = pluginId,
                    LoadResult = loadResult,
                    UnloadResult = unloadResult,
                    Message = loadResult.Success ? "Plugin reloaded successfully" : loadResult.Message;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reload plugin: {pluginId}");
                throw new PluginReloadException($"Failed to reload plugin {pluginId}", ex);
            }
        }

        /// <summary>
        /// Enable or disable a plugin;
        /// </summary>
        public async Task<bool> SetPluginEnabledAsync(string pluginId, bool enabled)
        {
            if (!_loadedPlugins.TryGetValue(pluginId, out var context))
            {
                throw new PluginNotFoundException($"Plugin {pluginId} not found");
            }

            try
            {
                if (enabled && context.State == PluginState.Disabled)
                {
                    // Enable the plugin;
                    await LoadPluginAsync(pluginId);
                    return true;
                }
                else if (!enabled && context.State != PluginState.Disabled)
                {
                    // Disable the plugin;
                    await UnloadPluginAsync(pluginId, true);
                    context.State = PluginState.Disabled;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to set enabled state for plugin: {pluginId}");
                return false;
            }
        }

        /// <summary>
        /// Get plugin manager statistics;
        /// </summary>
        public PluginManagerStatistics GetStatistics()
        {
            return new PluginManagerStatistics;
            {
                TotalLoadedPlugins = _loadedPlugins.Count,
                TotalRegisteredPlugins = _pluginRegistry.GetAllPlugins().Count(),
                LoadedPluginsByState = _loadedPlugins.Values;
                    .GroupBy(p => p.State)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AssemblyLoadCount = _assemblyResolver.GetStatistics().LoadCount,
                PluginLoadFailures = _pluginLoader.GetStatistics().FailedLoads;
            };
        }

        /// <summary>
        /// Dispose plugin manager and all loaded plugins;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            lock (_syncLock)
            {
                if (_isDisposed) return;

                try
                {
                    _logger.LogInformation("Disposing PluginManager");

                    // Unload all plugins;
                    var unloadTasks = _loadedPlugins.Keys;
                        .Select(id => UnloadPluginAsync(id, true))
                        .ToList();

                    Task.WhenAll(unloadTasks).Wait(TimeSpan.FromSeconds(30));

                    // Dispose components;
                    _pluginLoader?.Dispose();
                    _assemblyResolver?.Dispose();
                    _securityManager?.Dispose();
                    _eventBus?.Dispose();

                    _isDisposed = true;
                    _logger.LogInformation("PluginManager disposed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing PluginManager");
                }
            }
        }

        #region Private Methods;

        private PluginSettings LoadSettings()
        {
            var settings = new PluginSettings();

            try
            {
                var configSection = _configuration.GetSection("PluginSystem");
                if (configSection.Exists())
                {
                    configSection.Bind(settings);
                }

                // Add default directories if none specified;
                if (!settings.PluginDirectories.Any())
                {
                    settings.PluginDirectories.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"));
                    settings.PluginDirectories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NEDA", "Plugins"));
                }

                return settings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin settings, using defaults");
                return settings;
            }
        }

        private List<PluginInfo> DiscoverPlugins()
        {
            var discoveredPlugins = new List<PluginInfo>();

            foreach (var directory in _settings.PluginDirectories)
            {
                try
                {
                    if (!Directory.Exists(directory))
                    {
                        _logger.LogDebug($"Plugin directory does not exist: {directory}");
                        continue;
                    }

                    var pluginFiles = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories);
                    _logger.LogDebug($"Found {pluginFiles.Length} DLLs in {directory}");

                    foreach (var file in pluginFiles)
                    {
                        try
                        {
                            var pluginInfo = _pluginLoader.DiscoverPlugin(file);
                            if (pluginInfo != null)
                            {
                                discoveredPlugins.Add(pluginInfo);
                                _logger.LogDebug($"Discovered plugin: {pluginInfo.Name} in {file}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to discover plugin in {file}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to scan plugin directory: {directory}");
                }
            }

            return discoveredPlugins;
        }

        private async Task LoadConfiguredPluginsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var pluginsToLoad = _pluginRegistry.GetAllPlugins()
                    .Where(p => p.AutoLoad && p.IsEnabled)
                    .OrderBy(p => p.LoadOrder)
                    .ToList();

                _logger.LogInformation($"Loading {pluginsToLoad.Count} configured plugins");

                var semaphore = new SemaphoreSlim(_settings.MaxConcurrentLoads);
                var loadTasks = new List<Task>();

                foreach (var pluginInfo in pluginsToLoad)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    await semaphore.WaitAsync(cancellationToken);

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await LoadPluginAsync(pluginInfo.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to load plugin {pluginInfo.Name}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    loadTasks.Add(task);
                }

                await Task.WhenAll(loadTasks);

                _logger.LogInformation("Configured plugins loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load configured plugins");
            }
        }

        private IPlugin CreatePluginInstance(Assembly assembly, PluginInfo pluginInfo)
        {
            try
            {
                var pluginType = assembly.GetTypes()
                    .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);

                if (pluginType == null)
                {
                    throw new PluginLoadException($"No IPlugin implementation found in {pluginInfo.AssemblyPath}");
                }

                // Create instance with dependency injection;
                var instance = ActivatorUtilities.CreateInstance(_serviceProvider, pluginType) as IPlugin;

                if (instance == null)
                {
                    throw new PluginLoadException($"Failed to create instance of {pluginType.Name}");
                }

                return instance;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create plugin instance for {pluginInfo.Id}");
                throw;
            }
        }

        private async Task InitializePluginAsync(PluginContext context, PluginLoadOptions options)
        {
            try
            {
                context.State = PluginState.Initializing;

                var plugin = context.PluginInstance;
                var pluginInfo = context.PluginInfo;

                // Set plugin properties;
                plugin.Id = pluginInfo.Id;
                plugin.Name = pluginInfo.Name;
                plugin.Version = pluginInfo.Version;
                plugin.Description = pluginInfo.Description;

                // Initialize plugin;
                var initContext = new PluginInitializationContext;
                {
                    ServiceProvider = _serviceProvider,
                    Configuration = _configuration,
                    Logger = _logger,
                    Options = options;
                };

                await plugin.InitializeAsync(initContext);

                context.IsInitialized = true;
                context.State = PluginState.Initialized;
                context.InitializedTime = DateTime.UtcNow;

                // Start if it's a runnable plugin;
                if (plugin is IRunnablePlugin runnablePlugin && (options?.AutoStart ?? true))
                {
                    await StartPluginAsync(context);
                }

                _logger.LogDebug($"Plugin initialized: {pluginInfo.Id}");
            }
            catch (Exception ex)
            {
                context.State = PluginState.Error;
                throw new PluginInitializationException($"Failed to initialize plugin {context.PluginInfo.Id}", ex);
            }
        }

        private async Task UninitializePluginAsync(PluginContext context)
        {
            try
            {
                if (context.PluginInstance != null)
                {
                    await context.PluginInstance.ShutdownAsync();
                }

                context.IsInitialized = false;
                context.State = PluginState.Stopped;

                _logger.LogDebug($"Plugin uninitialized: {context.PluginInfo.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error uninitializing plugin {context.PluginInfo.Id}");
            }
        }

        private async Task StartPluginAsync(PluginContext context)
        {
            if (context.PluginInstance is IRunnablePlugin runnablePlugin && !context.IsRunning)
            {
                try
                {
                    context.State = PluginState.Starting;
                    await runnablePlugin.StartAsync();
                    context.IsRunning = true;
                    context.State = PluginState.Running;

                    _logger.LogDebug($"Plugin started: {context.PluginInfo.Id}");
                }
                catch (Exception ex)
                {
                    context.State = PluginState.Error;
                    throw new PluginStartException($"Failed to start plugin {context.PluginInfo.Id}", ex);
                }
            }
        }

        private void RegisterPluginByType(IPlugin plugin)
        {
            var pluginType = plugin.GetType();
            var interfaces = pluginType.GetInterfaces()
                .Where(i => typeof(IPlugin).IsAssignableFrom(i));

            foreach (var interfaceType in interfaces)
            {
                var plugins = _pluginsByType.GetOrAdd(interfaceType, _ => new List<IPlugin>());
                lock (plugins)
                {
                    if (!plugins.Contains(plugin))
                    {
                        plugins.Add(plugin);
                    }
                }
            }
        }

        private void UnregisterPluginByType(IPlugin plugin)
        {
            if (plugin == null) return;

            var pluginType = plugin.GetType();
            var interfaces = pluginType.GetInterfaces()
                .Where(i => typeof(IPlugin).IsAssignableFrom(i));

            foreach (var interfaceType in interfaces)
            {
                if (_pluginsByType.TryGetValue(interfaceType, out var plugins))
                {
                    lock (plugins)
                    {
                        plugins.Remove(plugin);
                    }
                }
            }
        }

        private List<PluginInfo> GetDependentPlugins(string pluginId)
        {
            var dependents = new List<PluginInfo>();

            foreach (var context in _loadedPlugins.Values)
            {
                if (context.PluginInfo.Dependencies?.Any(d => d.PluginId == pluginId) == true)
                {
                    dependents.Add(context.PluginInfo);
                }
            }

            return dependents;
        }

        private async Task ExecuteOnPluginAsync<T>(T plugin, Func<T, Task> action, CancellationToken cancellationToken) where T : IPlugin;
        {
            try
            {
                var timeoutTask = Task.Delay(_settings.PluginTimeout, cancellationToken);
                var pluginTask = action(plugin);

                await Task.WhenAny(pluginTask, timeoutTask);

                if (pluginTask.IsCompleted)
                {
                    await pluginTask;
                }
                else;
                {
                    throw new TimeoutException($"Plugin operation timed out after {_settings.PluginTimeout}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing action on plugin {plugin.Name}");
                throw;
            }
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("PluginManager is not initialized. Call InitializeAsync first.");
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Plugin context containing runtime information;
        /// </summary>
        public class PluginContext;
        {
            public PluginInfo PluginInfo { get; }
            public IPlugin PluginInstance { get; set; }
            public Assembly Assembly { get; set; }
            public PluginState State { get; set; }
            public bool IsInitialized { get; set; }
            public bool IsRunning { get; set; }
            public DateTime LoadedTime { get; set; }
            public DateTime InitializedTime { get; set; }
            public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();

            public PluginContext(PluginInfo pluginInfo)
            {
                PluginInfo = pluginInfo ?? throw new ArgumentNullException(nameof(pluginInfo));
                LoadedTime = DateTime.UtcNow;
                State = PluginState.NotLoaded;
            }
        }

        /// <summary>
        /// Plugin load options;
        /// </summary>
        public class PluginLoadOptions;
        {
            public bool AutoStart { get; set; } = true;
            public PluginLoadMode LoadMode { get; set; } = PluginLoadMode.Immediate;
            public bool IsDependency { get; set; } = false;
            public Dictionary<string, object> InitializationData { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Plugin load result;
        /// </summary>
        public class PluginLoadResult;
        {
            public bool Success { get; set; }
            public string PluginId { get; set; }
            public PluginContext PluginContext { get; set; }
            public Exception Error { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Plugin unload result;
        /// </summary>
        public class PluginUnloadResult;
        {
            public bool Success { get; set; }
            public string PluginId { get; set; }
            public Exception Error { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Plugin reload result;
        /// </summary>
        public class PluginReloadResult;
        {
            public bool Success { get; set; }
            public string PluginId { get; set; }
            public PluginLoadResult LoadResult { get; set; }
            public PluginUnloadResult UnloadResult { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Plugin manager statistics;
        /// </summary>
        public class PluginManagerStatistics;
        {
            public int TotalLoadedPlugins { get; set; }
            public int TotalRegisteredPlugins { get; set; }
            public Dictionary<PluginState, int> LoadedPluginsByState { get; set; }
            public int AssemblyLoadCount { get; set; }
            public int PluginLoadFailures { get; set; }
        }

        /// <summary>
        /// Security policy for plugins;
        /// </summary>
        public class SecurityPolicy;
        {
            public bool RequireDigitalSignature { get; set; } = false;
            public List<string> AllowedPublishers { get; set; } = new List<string>();
            public List<string> RestrictedAssemblies { get; set; } = new List<string>();
            public bool EnableCodeAccessSecurity { get; set; } = true;
            public bool EnableAppDomainIsolation { get; set; } = true;
            public List<string> AllowedPermissions { get; set; } = new List<string>();
        }

        #endregion;

        #region Events;

        public class PluginManagerInitializedEvent { }
        public class PluginLoadedEvent;
        {
            public PluginInfo PluginInfo { get; }
            public PluginLoadedEvent(PluginInfo pluginInfo) => PluginInfo = pluginInfo;
        }
        public class PluginUnloadedEvent;
        {
            public string PluginId { get; }
            public PluginUnloadedEvent(string pluginId) => PluginId = pluginId;
        }

        #endregion;

        #region Exceptions;

        public class PluginNotFoundException : Exception
        {
            public PluginNotFoundException(string message) : base(message) { }
        }

        public class PluginLoadException : Exception
        {
            public PluginLoadException(string message) : base(message) { }
            public PluginLoadException(string message, Exception inner) : base(message, inner) { }
        }

        public class PluginUnloadException : Exception
        {
            public PluginUnloadException(string message) : base(message) { }
            public PluginUnloadException(string message, Exception inner) : base(message, inner) { }
        }

        public class PluginSecurityException : Exception
        {
            public PluginSecurityException(string message) : base(message) { }
        }

        public class PluginDependencyException : Exception
        {
            public PluginDependencyException(string message) : base(message) { }
        }

        public class PluginInitializationException : Exception
        {
            public PluginInitializationException(string message) : base(message) { }
            public PluginInitializationException(string message, Exception inner) : base(message, inner) { }
        }

        public class PluginStartException : Exception
        {
            public PluginStartException(string message) : base(message) { }
            public PluginStartException(string message, Exception inner) : base(message, inner) { }
        }

        public class PluginReloadException : Exception
        {
            public PluginReloadException(string message) : base(message) { }
            public PluginReloadException(string message, Exception inner) : base(message, inner) { }
        }

        #endregion;
    }
}

// NEDA.EngineIntegration/PluginSystem/PluginSDK/IPlugin.cs;
namespace NEDA.EngineIntegration.PluginSystem.PluginSDK;
{
    /// <summary>
    /// Base interface for all plugins;
    /// </summary>
    public interface IPlugin;
    {
        string Id { get; set; }
        string Name { get; set; }
        Version Version { get; set; }
        string Description { get; set; }

        Task InitializeAsync(PluginInitializationContext context);
        Task ShutdownAsync();
    }

    /// <summary>
    /// Interface for plugins that can be started and stopped;
    /// </summary>
    public interface IRunnablePlugin : IPlugin;
    {
        Task StartAsync();
        Task StopAsync();
        bool IsRunning { get; }
    }

    /// <summary>
    /// Plugin initialization context;
    /// </summary>
    public class PluginInitializationContext;
    {
        public IServiceProvider ServiceProvider { get; set; }
        public IConfiguration Configuration { get; set; }
        public ILogger Logger { get; set; }
        public PluginManager.PluginLoadOptions Options { get; set; }
    }
}

// NEDA.EngineIntegration/PluginSystem/PluginRegistry/PluginRegistry.cs (partial for context)
namespace NEDA.EngineIntegration.PluginSystem.PluginRegistry
{
    /// <summary>
    /// Plugin registry for storing plugin metadata;
    /// </summary>
    public class PluginRegistry
    {
        private readonly ConcurrentDictionary<string, PluginInfo> _plugins = new ConcurrentDictionary<string, PluginInfo>();

        public void Register(PluginInfo pluginInfo)
        {
            _plugins[pluginInfo.Id] = pluginInfo;
        }

        public PluginInfo GetPlugin(string pluginId)
        {
            _plugins.TryGetValue(pluginId, out var pluginInfo);
            return pluginInfo;
        }

        public IEnumerable<PluginInfo> GetAllPlugins()
        {
            return _plugins.Values;
        }

        public void Update(PluginInfo pluginInfo)
        {
            _plugins[pluginInfo.Id] = pluginInfo;
        }
    }

    /// <summary>
    /// Plugin metadata;
    /// </summary>
    public class PluginInfo;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Version Version { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string AssemblyPath { get; set; }
        public List<PluginDependency> Dependencies { get; set; } = new List<PluginDependency>();
        public bool AutoLoad { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int LoadOrder { get; set; }
        public DateTime LastLoadTime { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Plugin dependency information;
    /// </summary>
    public class PluginDependency;
    {
        public string PluginId { get; set; }
        public Version MinVersion { get; set; }
        public Version MaxVersion { get; set; }
        public bool IsOptional { get; set; }
    }
}
