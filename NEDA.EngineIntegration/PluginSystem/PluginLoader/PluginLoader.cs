using NEDA.AI.ComputerVision;
using NEDA.ContentCreation.AssetPipeline.ImportManagers;
using NEDA.Core.Common;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.SecurityModules.AdvancedSecurity;
using NEDA.SecurityModules.AdvancedSecurity.Authentication;
using NEDA.SecurityModules.AdvancedSecurity.Authorization;
using NEDA.Services.FileService;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static NEDA.Build.PackageManager.DependencyResolver;

namespace NEDA.EngineIntegration.PluginSystem.PluginLoader;
{
    /// <summary>
    /// Plugin yükleme, yönetme ve izolasyon sistemini sağlayan ana sınıf;
    /// Güvenli sandbox ortamında plugin yürütme, dependency resolution ve version management;
    /// </summary>
    public class PluginLoader : IPluginLoader, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IFileManager _fileManager;
        private readonly ISecurityValidator _securityValidator;
        private readonly IAuthService _authService;
        private readonly IPermissionService _permissionService;

        private readonly Dictionary<string, PluginContext> _loadedPlugins;
        private readonly Dictionary<string, AssemblyLoadContext> _assemblyContexts;
        private readonly Dictionary<string, PluginManifest> _pluginManifests;
        private readonly List<AssemblyDependencyResolver> _dependencyResolvers;

        private readonly PluginConfiguration _configuration;
        private readonly PluginSandboxManager _sandboxManager;
        private readonly PluginDependencyResolver _dependencyResolver;
        private readonly PluginValidator _validator;

        private bool _isDisposed;
        private readonly object _pluginLock = new object();
        private readonly SemaphoreSlim _loadSemaphore;
        private readonly Timer _pluginMonitorTimer;

        /// <summary>
        /// PluginLoader constructor;
        /// </summary>
        public PluginLoader(
            ILogger logger,
            IFileManager fileManager,
            ISecurityValidator securityValidator = null,
            IAuthService authService = null,
            IPermissionService permissionService = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _securityValidator = securityValidator;
            _authService = authService;
            _permissionService = permissionService;

            _loadedPlugins = new Dictionary<string, PluginContext>(StringComparer.OrdinalIgnoreCase);
            _assemblyContexts = new Dictionary<string, AssemblyLoadContext>();
            _pluginManifests = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);
            _dependencyResolvers = new List<AssemblyDependencyResolver>();

            _configuration = LoadConfiguration();
            _sandboxManager = new PluginSandboxManager(logger, _configuration);
            _dependencyResolver = new PluginDependencyResolver(logger);
            _validator = new PluginValidator(logger, securityValidator);

            _loadSemaphore = new SemaphoreSlim(_configuration.MaxConcurrentLoads, _configuration.MaxConcurrentLoads);

            // Initialize plugin monitoring;
            _pluginMonitorTimer = new Timer(
                MonitorPluginsCallback,
                null,
                TimeSpan.FromMinutes(_configuration.MonitoringIntervalMinutes),
                TimeSpan.FromMinutes(_configuration.MonitoringIntervalMinutes));

            InitializeDefaultPluginPaths();
            LoadPluginManifests();

            _logger.Info($"PluginLoader initialized. Plugin directories: {_configuration.PluginDirectories.Count}");
        }

        /// <summary>
        /// Plugin yükleme ana metodu;
        /// </summary>
        public async Task<PluginLoadResult> LoadPluginAsync(
            string pluginPath,
            LoadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(pluginPath, nameof(pluginPath));

            options ??= LoadOptions.Default;
            var pluginId = await GetPluginIdentifierAsync(pluginPath);

            _logger.Info($"Loading plugin: {pluginId} from {pluginPath}");

            // Check if already loaded;
            if (_loadedPlugins.ContainsKey(pluginId) && !options.ForceReload)
            {
                _logger.Warning($"Plugin {pluginId} is already loaded");
                return new PluginLoadResult;
                {
                    PluginId = pluginId,
                    Status = LoadStatus.AlreadyLoaded,
                    Context = _loadedPlugins[pluginId]
                };
            }

            // Acquire load slot;
            await _loadSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Step 1: Validate plugin;
                var validationResult = await ValidatePluginAsync(pluginPath, options, cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new PluginValidationException(
                        $"Plugin validation failed: {validationResult.ErrorMessage}");
                }

                // Step 2: Extract plugin manifest;
                var manifest = await ExtractPluginManifestAsync(pluginPath, cancellationToken);

                // Step 3: Create isolated context;
                var context = await CreatePluginContextAsync(pluginPath, manifest, options, cancellationToken);

                // Step 4: Resolve dependencies;
                await ResolveDependenciesAsync(context, cancellationToken);

                // Step 5: Load assemblies;
                await LoadAssembliesAsync(context, cancellationToken);

                // Step 6: Initialize plugin;
                await InitializePluginAsync(context, cancellationToken);

                // Step 7: Register plugin;
                RegisterPlugin(context, pluginId);

                _logger.Info($"Plugin loaded successfully: {pluginId} v{manifest.Version}");

                return new PluginLoadResult;
                {
                    PluginId = pluginId,
                    Status = LoadStatus.Success,
                    Context = context,
                    Manifest = manifest,
                    LoadTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load plugin {pluginId}: {ex.Message}", ex);
                throw new PluginLoadException($"Failed to load plugin from {pluginPath}", ex);
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        /// <summary>
        /// Çoklu plugin yükleme;
        /// </summary>
        public async Task<BatchLoadResult> LoadPluginsAsync(
            IEnumerable<string> pluginPaths,
            BatchLoadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(pluginPaths, nameof(pluginPaths));

            options ??= BatchLoadOptions.Default;

            var results = new List<PluginLoadResult>();
            var failedLoads = new List<FailedPluginLoad>();

            _logger.Info($"Loading {pluginPaths.Count()} plugins in batch mode");

            // Create load tasks;
            var loadTasks = new List<Task<PluginLoadResult>>();

            foreach (var pluginPath in pluginPaths)
            {
                loadTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        return await LoadPluginAsync(pluginPath, options.LoadOptions, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to load plugin {pluginPath}: {ex.Message}");

                        failedLoads.Add(new FailedPluginLoad;
                        {
                            PluginPath = pluginPath,
                            ErrorMessage = ex.Message,
                            Exception = ex,
                            Timestamp = DateTime.UtcNow;
                        });

                        return null;
                    }
                }));
            }

            // Execute with concurrency limit;
            var semaphore = new SemaphoreSlim(options.MaxConcurrentLoads);
            var tasksWithConcurrency = loadTasks.Select(async task =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    return await task;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var loadResults = await Task.WhenAll(tasksWithConcurrency);

            // Process results;
            foreach (var result in loadResults)
            {
                if (result != null)
                {
                    results.Add(result);
                }
            }

            return new BatchLoadResult;
            {
                TotalPlugins = pluginPaths.Count(),
                SuccessfulLoads = results,
                FailedLoads = failedLoads,
                StartTime = DateTime.UtcNow - TimeSpan.FromSeconds(30), // Approximation;
                EndTime = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Plugin kaldırma;
        /// </summary>
        public async Task<UnloadResult> UnloadPluginAsync(
            string pluginId,
            UnloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(pluginId, nameof(pluginId));

            options ??= UnloadOptions.Default;

            _logger.Info($"Unloading plugin: {pluginId}");

            lock (_pluginLock)
            {
                if (!_loadedPlugins.TryGetValue(pluginId, out var context))
                {
                    throw new PluginNotFoundException($"Plugin {pluginId} not found");
                }

                // Check if plugin can be unloaded;
                if (!context.IsUnloadable && !options.ForceUnload)
                {
                    throw new PluginUnloadException(
                        $"Plugin {pluginId} cannot be unloaded (IsUnloadable = false)");
                }
            }

            try
            {
                // Step 1: Notify plugin about unloading;
                await NotifyPluginUnloadingAsync(pluginId, options, cancellationToken);

                // Step 2: Dispose plugin resources;
                await DisposePluginResourcesAsync(pluginId, cancellationToken);

                // Step 3: Unload assemblies;
                await UnloadAssembliesAsync(pluginId, cancellationToken);

                // Step 4: Remove from registry
                UnregisterPlugin(pluginId);

                // Step 5: Cleanup sandbox;
                await CleanupPluginSandboxAsync(pluginId, cancellationToken);

                _logger.Info($"Plugin unloaded successfully: {pluginId}");

                return new UnloadResult;
                {
                    PluginId = pluginId,
                    IsSuccess = true,
                    UnloadTime = DateTime.UtcNow,
                    FreedMemory = 0 // Would be calculated from context;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to unload plugin {pluginId}: {ex.Message}", ex);

                if (options.ThrowOnFailure)
                {
                    throw new PluginUnloadException($"Failed to unload plugin {pluginId}", ex);
                }

                return new UnloadResult;
                {
                    PluginId = pluginId,
                    IsSuccess = false,
                    ErrorMessage = ex.Message,
                    UnloadTime = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Tüm plugin'leri kaldırma;
        /// </summary>
        public async Task<BatchUnloadResult> UnloadAllPluginsAsync(
            UnloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= UnloadOptions.Default;

            var pluginIds = GetLoadedPluginIds().ToList();
            _logger.Info($"Unloading all {pluginIds.Count} plugins");

            var results = new List<UnloadResult>();
            var failedUnloads = new List<FailedUnload>();

            foreach (var pluginId in pluginIds)
            {
                try
                {
                    var result = await UnloadPluginAsync(pluginId, options, cancellationToken);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to unload plugin {pluginId}: {ex.Message}");

                    failedUnloads.Add(new FailedUnload;
                    {
                        PluginId = pluginId,
                        ErrorMessage = ex.Message,
                        Exception = ex;
                    });

                    if (options.StopOnFirstError)
                    {
                        break;
                    }
                }
            }

            return new BatchUnloadResult;
            {
                TotalPlugins = pluginIds.Count,
                SuccessfulUnloads = results,
                FailedUnloads = failedUnloads,
                UnloadTime = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Plugin yeniden yükleme;
        /// </summary>
        public async Task<ReloadResult> ReloadPluginAsync(
            string pluginId,
            ReloadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(pluginId, nameof(pluginId));

            options ??= ReloadOptions.Default;

            _logger.Info($"Reloading plugin: {pluginId}");

            PluginContext oldContext = null;
            string pluginPath = null;

            lock (_pluginLock)
            {
                if (!_loadedPlugins.TryGetValue(pluginId, out oldContext))
                {
                    throw new PluginNotFoundException($"Plugin {pluginId} not found");
                }

                pluginPath = oldContext.PluginPath;
            }

            try
            {
                // Step 1: Unload plugin;
                var unloadResult = await UnloadPluginAsync(pluginId, options.UnloadOptions, cancellationToken);

                if (!unloadResult.IsSuccess && options.RequireCleanUnload)
                {
                    throw new PluginReloadException(
                        $"Failed to unload plugin {pluginId} during reload");
                }

                // Step 2: Wait if specified;
                if (options.DelayBeforeReload > TimeSpan.Zero)
                {
                    await Task.Delay(options.DelayBeforeReload, cancellationToken);
                }

                // Step 3: Reload plugin;
                var loadOptions = options.LoadOptions ?? LoadOptions.Default;
                loadOptions.ForceReload = true;

                var loadResult = await LoadPluginAsync(pluginPath, loadOptions, cancellationToken);

                return new ReloadResult;
                {
                    PluginId = pluginId,
                    IsSuccess = loadResult.Status == LoadStatus.Success,
                    OldContext = oldContext,
                    NewContext = loadResult.Context,
                    ReloadTime = DateTime.UtcNow,
                    UnloadResult = unloadResult,
                    LoadResult = loadResult;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to reload plugin {pluginId}: {ex.Message}", ex);
                throw new PluginReloadException($"Failed to reload plugin {pluginId}", ex);
            }
        }

        /// <summary>
        /// Plugin instance oluşturma;
        /// </summary>
        public async Task<T> CreatePluginInstanceAsync<T>(
            string pluginId,
            InstanceOptions options = null,
            CancellationToken cancellationToken = default) where T : class, IPlugin;
        {
            Guard.ArgumentNotNullOrEmpty(pluginId, nameof(pluginId));

            options ??= InstanceOptions.Default;

            PluginContext context;
            lock (_pluginLock)
            {
                if (!_loadedPlugins.TryGetValue(pluginId, out context))
                {
                    throw new PluginNotFoundException($"Plugin {pluginId} not found");
                }
            }

            // Check permissions;
            if (_permissionService != null && !string.IsNullOrEmpty(options.RequiredPermission))
            {
                var hasPermission = await _permissionService.HasPermissionAsync(
                    options.UserId,
                    options.RequiredPermission,
                    cancellationToken);

                if (!hasPermission)
                {
                    throw new UnauthorizedAccessException(
                        $"User {options.UserId} does not have permission '{options.RequiredPermission}' to create plugin instance");
                }
            }

            try
            {
                // Create instance in plugin's context;
                var instance = await CreateInstanceInContextAsync<T>(context, options, cancellationToken);

                // Initialize instance;
                await InitializePluginInstanceAsync(instance, context, options, cancellationToken);

                // Register instance;
                RegisterPluginInstance(pluginId, instance, options.InstanceId);

                _logger.Debug($"Plugin instance created: {pluginId}.{options.InstanceId}");

                return instance;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create plugin instance for {pluginId}: {ex.Message}", ex);
                throw new PluginInstanceException($"Failed to create instance of plugin {pluginId}", ex);
            }
        }

        /// <summary>
        /// Plugin bilgilerini getir;
        /// </summary>
        public PluginInfo GetPluginInfo(string pluginId)
        {
            Guard.ArgumentNotNullOrEmpty(pluginId, nameof(pluginId));

            lock (_pluginLock)
            {
                if (!_loadedPlugins.TryGetValue(pluginId, out var context))
                {
                    throw new PluginNotFoundException($"Plugin {pluginId} not found");
                }

                return new PluginInfo;
                {
                    PluginId = pluginId,
                    Name = context.Manifest.Name,
                    Version = context.Manifest.Version,
                    Description = context.Manifest.Description,
                    Author = context.Manifest.Author,
                    Status = context.Status,
                    LoadTime = context.LoadTime,
                    IsIsolated = context.IsIsolated,
                    IsUnloadable = context.IsUnloadable,
                    AssemblyCount = context.LoadedAssemblies.Count,
                    Dependencies = context.Manifest.Dependencies,
                    Permissions = context.Manifest.RequiredPermissions,
                    FilePath = context.PluginPath;
                };
            }
        }

        /// <summary>
        /// Yüklü tüm plugin'leri listele;
        /// </summary>
        public IEnumerable<PluginInfo> GetLoadedPlugins()
        {
            lock (_pluginLock)
            {
                return _loadedPlugins.Values;
                    .Select(context => GetPluginInfo(context.PluginId))
                    .ToList();
            }
        }

        /// <summary>
        /// Plugin dizinlerini tarama;
        /// </summary>
        public async Task<IEnumerable<DiscoveredPlugin>> DiscoverPluginsAsync(
            string directory = null,
            CancellationToken cancellationToken = default)
        {
            var searchDirectory = directory ?? _configuration.DefaultPluginDirectory;

            if (!Directory.Exists(searchDirectory))
            {
                _logger.Warning($"Plugin directory not found: {searchDirectory}");
                return Enumerable.Empty<DiscoveredPlugin>();
            }

            _logger.Info($"Discovering plugins in: {searchDirectory}");

            var discoveredPlugins = new List<DiscoveredPlugin>();

            // Search for plugin files;
            var pluginFiles = Directory.GetFiles(searchDirectory, "*.dll", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(searchDirectory, "*.npl", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(searchDirectory, "*.plugin", SearchOption.AllDirectories));

            foreach (var pluginFile in pluginFiles)
            {
                try
                {
                    var manifest = await TryExtractManifestAsync(pluginFile, cancellationToken);
                    if (manifest != null)
                    {
                        var discovered = new DiscoveredPlugin;
                        {
                            FilePath = pluginFile,
                            Manifest = manifest,
                            FileSize = new FileInfo(pluginFile).Length,
                            LastModified = File.GetLastWriteTimeUtc(pluginFile),
                            IsValid = true;
                        };

                        discoveredPlugins.Add(discovered);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to extract manifest from {pluginFile}: {ex.Message}");
                }
            }

            _logger.Info($"Discovered {discoveredPlugins.Count} plugins in {searchDirectory}");
            return discoveredPlugins;
        }

        /// <summary>
        /// Plugin bağımlılıklarını çözümle;
        /// </summary>
        public async Task<DependencyResolutionResult> ResolveDependenciesAsync(
            string pluginPath,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(pluginPath, nameof(pluginPath));

            var manifest = await ExtractPluginManifestAsync(pluginPath, cancellationToken);
            return await _dependencyResolver.ResolveAsync(manifest, pluginPath, cancellationToken);
        }

        /// <summary>
        /// Plugin yapılandırmasını güncelle;
        /// </summary>
        public void UpdateConfiguration(Action<PluginConfiguration> configAction)
        {
            Guard.ArgumentNotNull(configAction, nameof(configAction));

            lock (_pluginLock)
            {
                configAction.Invoke(_configuration);
                SaveConfiguration(_configuration);

                // Update monitoring timer;
                _pluginMonitorTimer.Change(
                    TimeSpan.FromMinutes(_configuration.MonitoringIntervalMinutes),
                    TimeSpan.FromMinutes(_configuration.MonitoringIntervalMinutes));

                _logger.Info("PluginLoader configuration updated");
            }
        }

        /// <summary>
        /// Kaynakları temizle;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Stop monitoring;
                    _pluginMonitorTimer?.Dispose();

                    // Unload all plugins;
                    Task.Run(async () => await UnloadAllPluginsAsync()).Wait();

                    // Dispose contexts;
                    foreach (var context in _assemblyContexts.Values)
                    {
                        context.Unload();
                    }

                    _assemblyContexts.Clear();
                    _loadedPlugins.Clear();
                    _pluginManifests.Clear();

                    foreach (var resolver in _dependencyResolvers)
                    {
                        resolver.Dispose();
                    }
                    _dependencyResolvers.Clear();

                    _loadSemaphore?.Dispose();
                    _sandboxManager?.Dispose();
                }

                _isDisposed = true;
                _logger.Info("PluginLoader disposed");
            }
        }

        #region Private Methods;

        private async Task<PluginValidationResult> ValidatePluginAsync(
            string pluginPath,
            LoadOptions options,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Validating plugin: {pluginPath}");

            var result = await _validator.ValidateAsync(pluginPath, options, cancellationToken);

            if (!result.IsValid && options.StrictValidation)
            {
                throw new PluginValidationException(
                    $"Plugin validation failed with {result.Issues.Count} issues");
            }

            return result;
        }

        private async Task<PluginManifest> ExtractPluginManifestAsync(
            string pluginPath,
            CancellationToken cancellationToken)
        {
            // Check cache first;
            var pluginId = await GetPluginIdentifierAsync(pluginPath);
            if (_pluginManifests.TryGetValue(pluginId, out var cachedManifest))
            {
                return cachedManifest;
            }

            try
            {
                // Extract manifest from assembly or separate file;
                var manifestPath = Path.Combine(Path.GetDirectoryName(pluginPath), "plugin.manifest.json");

                if (File.Exists(manifestPath))
                {
                    var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                    var manifest = JsonConvert.DeserializeObject<PluginManifest>(json);
                    manifest.FilePath = pluginPath;

                    _pluginManifests[pluginId] = manifest;
                    return manifest;
                }

                // Try to extract from assembly attributes;
                var assembly = Assembly.LoadFrom(pluginPath);
                var manifestFromAssembly = ExtractManifestFromAssembly(assembly, pluginPath);

                _pluginManifests[pluginId] = manifestFromAssembly;
                return manifestFromAssembly;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to extract manifest from {pluginPath}: {ex.Message}", ex);
                throw new PluginManifestException($"Failed to extract plugin manifest from {pluginPath}", ex);
            }
        }

        private async Task<PluginContext> CreatePluginContextAsync(
            string pluginPath,
            PluginManifest manifest,
            LoadOptions options,
            CancellationToken cancellationToken)
        {
            var contextId = Guid.NewGuid().ToString();
            var contextName = $"PluginContext_{manifest.Name}_{contextId}";

            _logger.Debug($"Creating plugin context: {contextName}");

            // Create isolated assembly load context if required;
            AssemblyLoadContext assemblyContext = null;
            if (options.IsolationLevel >= IsolationLevel.AssemblyIsolation)
            {
                assemblyContext = new PluginAssemblyLoadContext(contextName, pluginPath);
                _assemblyContexts[contextId] = assemblyContext;
            }

            // Create sandbox if required;
            Sandbox sandbox = null;
            if (options.IsolationLevel >= IsolationLevel.FullIsolation)
            {
                sandbox = await _sandboxManager.CreateSandboxAsync(contextId, manifest, cancellationToken);
            }

            var context = new PluginContext;
            {
                ContextId = contextId,
                PluginId = await GetPluginIdentifierAsync(pluginPath),
                PluginPath = pluginPath,
                Manifest = manifest,
                AssemblyContext = assemblyContext,
                Sandbox = sandbox,
                LoadOptions = options,
                IsIsolated = assemblyContext != null,
                IsUnloadable = options.IsolationLevel != IsolationLevel.FullIsolation,
                Status = PluginStatus.Created,
                LoadTime = DateTime.UtcNow;
            };

            return context;
        }

        private async Task ResolveDependenciesAsync(
            PluginContext context,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Resolving dependencies for plugin: {context.PluginId}");

            var resolutionResult = await _dependencyResolver.ResolveAsync(
                context.Manifest,
                context.PluginPath,
                cancellationToken);

            context.Dependencies = resolutionResult.ResolvedDependencies;
            context.DependencyIssues = resolutionResult.Issues;

            if (resolutionResult.HasMissingDependencies && context.LoadOptions.StrictDependencyResolution)
            {
                throw new DependencyResolutionException(
                    $"Missing dependencies for plugin {context.PluginId}: " +
                    string.Join(", ", resolutionResult.MissingDependencies));
            }

            // Create dependency resolvers for each dependency path;
            foreach (var dependencyPath in resolutionResult.DependencyPaths)
            {
                var resolver = new AssemblyDependencyResolver(dependencyPath);
                _dependencyResolvers.Add(resolver);
                context.DependencyResolvers.Add(resolver);
            }
        }

        private async Task LoadAssembliesAsync(
            PluginContext context,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Loading assemblies for plugin: {context.PluginId}");

            var mainAssemblyPath = context.PluginPath;

            // Load main assembly;
            Assembly mainAssembly;
            if (context.AssemblyContext != null)
            {
                using (var stream = File.OpenRead(mainAssemblyPath))
                {
                    mainAssembly = context.AssemblyContext.LoadFromStream(stream);
                }
            }
            else;
            {
                mainAssembly = Assembly.LoadFrom(mainAssemblyPath);
            }

            context.MainAssembly = mainAssembly;
            context.LoadedAssemblies.Add(mainAssembly);

            // Load dependency assemblies;
            foreach (var dependency in context.Dependencies)
            {
                try
                {
                    Assembly dependencyAssembly;
                    var dependencyPath = dependency.FilePath;

                    if (context.AssemblyContext != null)
                    {
                        using (var stream = File.OpenRead(dependencyPath))
                        {
                            dependencyAssembly = context.AssemblyContext.LoadFromStream(stream);
                        }
                    }
                    else;
                    {
                        dependencyAssembly = Assembly.LoadFrom(dependencyPath);
                    }

                    context.LoadedAssemblies.Add(dependencyAssembly);
                    context.DependencyAssemblies[dependency.Name] = dependencyAssembly;
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to load dependency {dependency.Name}: {ex.Message}");

                    if (context.LoadOptions.StrictDependencyLoading)
                    {
                        throw new DependencyLoadException(
                            $"Failed to load dependency {dependency.Name} for plugin {context.PluginId}", ex);
                    }
                }
            }

            context.Status = PluginStatus.AssembliesLoaded;
        }

        private async Task InitializePluginAsync(
            PluginContext context,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Initializing plugin: {context.PluginId}");

            try
            {
                // Find plugin entry point;
                var pluginType = FindPluginType(context.MainAssembly);
                if (pluginType == null)
                {
                    throw new PluginInitializationException(
                        $"No IPlugin implementation found in {context.MainAssembly.FullName}");
                }

                // Create plugin instance (but don't initialize yet)
                context.PluginType = pluginType;

                // Initialize sandbox if exists;
                if (context.Sandbox != null)
                {
                    await context.Sandbox.InitializeAsync(cancellationToken);
                }

                context.Status = PluginStatus.Initialized;
                context.InitializationTime = DateTime.UtcNow;

                _logger.Debug($"Plugin initialized: {context.PluginId}");
            }
            catch (Exception ex)
            {
                context.Status = PluginStatus.InitializationFailed;
                context.LastError = ex;

                _logger.Error($"Plugin initialization failed: {context.PluginId}", ex);
                throw new PluginInitializationException(
                    $"Failed to initialize plugin {context.PluginId}", ex);
            }
        }

        private void RegisterPlugin(PluginContext context, string pluginId)
        {
            lock (_pluginLock)
            {
                _loadedPlugins[pluginId] = context;
                _pluginManifests[pluginId] = context.Manifest;
            }

            context.Status = PluginStatus.Loaded;

            // Fire plugin loaded event;
            OnPluginLoaded?.Invoke(this, new PluginLoadedEventArgs;
            {
                PluginId = pluginId,
                Context = context,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void UnregisterPlugin(string pluginId)
        {
            PluginContext context;
            lock (_pluginLock)
            {
                if (_loadedPlugins.TryGetValue(pluginId, out context))
                {
                    _loadedPlugins.Remove(pluginId);
                    _pluginManifests.Remove(pluginId);

                    if (_assemblyContexts.TryGetValue(context.ContextId, out var assemblyContext))
                    {
                        _assemblyContexts.Remove(context.ContextId);
                    }
                }
            }

            if (context != null)
            {
                context.Status = PluginStatus.Unloaded;

                // Fire plugin unloaded event;
                OnPluginUnloaded?.Invoke(this, new PluginUnloadedEventArgs;
                {
                    PluginId = pluginId,
                    Context = context,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private async Task<T> CreateInstanceInContextAsync<T>(
            PluginContext context,
            InstanceOptions options,
            CancellationToken cancellationToken) where T : class, IPlugin;
        {
            try
            {
                // Create instance;
                object instance;
                if (context.AssemblyContext != null)
                {
                    // Create in isolated context;
                    instance = Activator.CreateInstance(context.PluginType);
                }
                else;
                {
                    // Create in default context;
                    instance = Activator.CreateInstance(context.PluginType);
                }

                var pluginInstance = instance as T;
                if (pluginInstance == null)
                {
                    throw new PluginInstanceException(
                        $"Plugin {context.PluginId} does not implement {typeof(T).Name}");
                }

                return pluginInstance;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create plugin instance: {ex.Message}", ex);
                throw new PluginInstanceException($"Failed to create instance of {context.PluginId}", ex);
            }
        }

        private async Task InitializePluginInstanceAsync<T>(
            T instance,
            PluginContext context,
            InstanceOptions options,
            CancellationToken cancellationToken) where T : class, IPlugin;
        {
            try
            {
                // Create initialization context;
                var initContext = new PluginInitializationContext;
                {
                    PluginId = context.PluginId,
                    InstanceId = options.InstanceId,
                    Configuration = options.Configuration,
                    ServiceProvider = options.ServiceProvider,
                    Logger = _logger.CreateChildLogger($"{context.PluginId}.{options.InstanceId}"),
                    CancellationToken = cancellationToken;
                };

                // Initialize plugin;
                await instance.InitializeAsync(initContext);

                // Register instance in context;
                context.Instances[options.InstanceId] = new PluginInstanceInfo;
                {
                    InstanceId = options.InstanceId,
                    Instance = instance,
                    Type = typeof(T),
                    CreatedAt = DateTime.UtcNow,
                    Status = PluginInstanceStatus.Initialized;
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize plugin instance: {ex.Message}", ex);
                throw new PluginInitializationException(
                    $"Failed to initialize instance of plugin {context.PluginId}", ex);
            }
        }

        private void RegisterPluginInstance(string pluginId, IPlugin instance, string instanceId)
        {
            // Instance tracking would be implemented here;
        }

        private async Task NotifyPluginUnloadingAsync(
            string pluginId,
            UnloadOptions options,
            CancellationToken cancellationToken)
        {
            PluginContext context;
            lock (_pluginLock)
            {
                context = _loadedPlugins[pluginId];
            }

            // Notify all instances;
            foreach (var instanceInfo in context.Instances.Values)
            {
                try
                {
                    await instanceInfo.Instance.OnUnloadingAsync(new UnloadContext;
                    {
                        PluginId = pluginId,
                        InstanceId = instanceInfo.InstanceId,
                        IsShuttingDown = options.IsShuttingDown,
                        Reason = options.Reason;
                    });
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Plugin instance {instanceInfo.InstanceId} failed to handle unloading: {ex.Message}");
                }
            }

            // Fire plugin unloading event;
            OnPluginUnloading?.Invoke(this, new PluginUnloadingEventArgs;
            {
                PluginId = pluginId,
                Context = context,
                Options = options,
                Timestamp = DateTime.UtcNow;
            });
        }

        private async Task DisposePluginResourcesAsync(
            string pluginId,
            CancellationToken cancellationToken)
        {
            PluginContext context;
            lock (_pluginLock)
            {
                context = _loadedPlugins[pluginId];
            }

            // Dispose all instances;
            foreach (var instanceInfo in context.Instances.Values.OfType<IDisposable>())
            {
                try
                {
                    instanceInfo.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to dispose plugin instance: {ex.Message}");
                }
            }

            context.Instances.Clear();
        }

        private async Task UnloadAssembliesAsync(
            string pluginId,
            CancellationToken cancellationToken)
        {
            PluginContext context;
            lock (_pluginLock)
            {
                context = _loadedPlugins[pluginId];
            }

            if (context.AssemblyContext != null)
            {
                // Unload isolated assembly context;
                context.AssemblyContext.Unload();

                // Wait for unload to complete;
                await Task.Delay(100, cancellationToken); // Give GC time to collect;

                // Force GC to collect unloaded assemblies;
                for (int i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            context.LoadedAssemblies.Clear();
            context.DependencyAssemblies.Clear();
        }

        private async Task CleanupPluginSandboxAsync(
            string pluginId,
            CancellationToken cancellationToken)
        {
            PluginContext context;
            lock (_pluginLock)
            {
                context = _loadedPlugins[pluginId];
            }

            if (context.Sandbox != null)
            {
                await _sandboxManager.CleanupSandboxAsync(context.Sandbox, cancellationToken);
            }
        }

        private Type FindPluginType(Assembly assembly)
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                    {
                        return type;
                    }
                }

                return null;
            }
            catch (ReflectionTypeLoadException ex)
            {
                _logger.Error($"Failed to load types from assembly {assembly.FullName}: {ex.Message}", ex);
                throw new PluginLoadException($"Failed to load types from assembly {assembly.FullName}", ex);
            }
        }

        private PluginManifest ExtractManifestFromAssembly(Assembly assembly, string filePath)
        {
            var attributes = assembly.GetCustomAttributesData();

            var manifest = new PluginManifest;
            {
                FilePath = filePath,
                Name = assembly.GetName().Name,
                Version = assembly.GetName().Version.ToString(),
                Description = GetAssemblyAttribute(assembly, typeof(AssemblyDescriptionAttribute))?.Description,
                Author = GetAssemblyAttribute(assembly, typeof(AssemblyCompanyAttribute))?.Company,
                PluginType = "Standard",
                TargetFramework = assembly.ImageRuntimeVersion;
            };

            // Extract additional attributes;
            var pluginAttribute = assembly.GetCustomAttribute<PluginAttribute>();
            if (pluginAttribute != null)
            {
                manifest.Name = pluginAttribute.Name ?? manifest.Name;
                manifest.Description = pluginAttribute.Description ?? manifest.Description;
                manifest.PluginType = pluginAttribute.PluginType;
                manifest.RequiredPermissions = pluginAttribute.RequiredPermissions?.ToList();
            }

            return manifest;
        }

        private T GetAssemblyAttribute<T>(Assembly assembly) where T : Attribute;
        {
            return assembly.GetCustomAttribute<T>();
        }

        private async Task<string> GetPluginIdentifierAsync(string pluginPath)
        {
            var manifest = await TryExtractManifestAsync(pluginPath, CancellationToken.None);
            if (manifest != null)
            {
                return $"{manifest.Name}_{manifest.Version}";
            }

            return Path.GetFileNameWithoutExtension(pluginPath);
        }

        private async Task<PluginManifest> TryExtractManifestAsync(
            string pluginPath,
            CancellationToken cancellationToken)
        {
            try
            {
                return await ExtractPluginManifestAsync(pluginPath, cancellationToken);
            }
            catch
            {
                return null;
            }
        }

        private void InitializeDefaultPluginPaths()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            _configuration.PluginDirectories.Add(Path.Combine(appData, "NEDA", "Plugins"));
            _configuration.PluginDirectories.Add(Path.Combine(programData, "NEDA", "Plugins"));
            _configuration.PluginDirectories.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"));

            _configuration.DefaultPluginDirectory = _configuration.PluginDirectories.First();

            // Create directories if they don't exist;
            foreach (var dir in _configuration.PluginDirectories)
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
        }

        private void LoadPluginManifests()
        {
            var manifestCachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEDA",
                "PluginCache",
                "manifests.json");

            if (File.Exists(manifestCachePath))
            {
                try
                {
                    var json = File.ReadAllText(manifestCachePath);
                    var cache = JsonConvert.DeserializeObject<Dictionary<string, PluginManifest>>(json);

                    foreach (var kvp in cache)
                    {
                        _pluginManifests[kvp.Key] = kvp.Value;
                    }

                    _logger.Info($"Loaded {_pluginManifests.Count} plugin manifests from cache");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to load plugin manifest cache: {ex.Message}");
                }
            }
        }

        private void SaveConfiguration(PluginConfiguration config)
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NEDA",
                "pluginloader.config.json");

            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(configPath, json);
        }

        private PluginConfiguration LoadConfiguration()
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NEDA",
                "pluginloader.config.json");

            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    return JsonConvert.DeserializeObject<PluginConfiguration>(json);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to load plugin loader configuration: {ex.Message}");
                }
            }

            // Default configuration;
            return new PluginConfiguration;
            {
                MaxConcurrentLoads = 5,
                DefaultIsolationLevel = IsolationLevel.AssemblyIsolation,
                EnableSandboxing = true,
                SandboxMemoryLimitMB = 512,
                SandboxTimeoutSeconds = 30,
                MonitoringIntervalMinutes = 5,
                CachePluginManifests = true,
                ValidateAssemblies = true,
                EnableSignatureVerification = true,
                AllowNativeLibraries = false,
                MaxPluginSizeMB = 100,
                AutoDiscoverPlugins = true;
            };
        }

        private void MonitorPluginsCallback(object state)
        {
            try
            {
                MonitorPluginsAsync().Wait();
            }
            catch (Exception ex)
            {
                _logger.Error($"Plugin monitoring failed: {ex.Message}", ex);
            }
        }

        private async Task MonitorPluginsAsync()
        {
            _logger.Debug("Monitoring loaded plugins");

            var pluginIds = GetLoadedPluginIds().ToList();

            foreach (var pluginId in pluginIds)
            {
                try
                {
                    var context = _loadedPlugins[pluginId];

                    // Check plugin health;
                    var health = await CheckPluginHealthAsync(context);
                    if (!health.IsHealthy)
                    {
                        _logger.Warning($"Plugin {pluginId} is unhealthy: {health.Message}");

                        // Try to recover or unload;
                        if (_configuration.AutoRecoverUnhealthyPlugins)
                        {
                            await TryRecoverPluginAsync(pluginId);
                        }
                    }

                    // Check for updates;
                    if (_configuration.CheckForUpdates)
                    {
                        await CheckForPluginUpdatesAsync(pluginId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to monitor plugin {pluginId}: {ex.Message}");
                }
            }
        }

        private async Task<PluginHealth> CheckPluginHealthAsync(PluginContext context)
        {
            // Implement health checking logic;
            return new PluginHealth;
            {
                IsHealthy = true,
                PluginId = context.PluginId,
                CheckTime = DateTime.UtcNow;
            };
        }

        private async Task TryRecoverPluginAsync(string pluginId)
        {
            // Implement recovery logic;
        }

        private async Task CheckForPluginUpdatesAsync(string pluginId)
        {
            // Implement update checking logic;
        }

        private IEnumerable<string> GetLoadedPluginIds()
        {
            lock (_pluginLock)
            {
                return _loadedPlugins.Keys.ToList();
            }
        }

        #endregion;

        #region Events;

        public event EventHandler<PluginLoadedEventArgs> OnPluginLoaded;
        public event EventHandler<PluginUnloadedEventArgs> OnPluginUnloaded;
        public event EventHandler<PluginUnloadingEventArgs> OnPluginUnloading;
        public event EventHandler<PluginInstanceCreatedEventArgs> OnPluginInstanceCreated;

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IPluginLoader : IDisposable
    {
        Task<PluginLoadResult> LoadPluginAsync(string pluginPath, LoadOptions options = null, CancellationToken cancellationToken = default);
        Task<BatchLoadResult> LoadPluginsAsync(IEnumerable<string> pluginPaths, BatchLoadOptions options = null, CancellationToken cancellationToken = default);
        Task<UnloadResult> UnloadPluginAsync(string pluginId, UnloadOptions options = null, CancellationToken cancellationToken = default);
        Task<BatchUnloadResult> UnloadAllPluginsAsync(UnloadOptions options = null, CancellationToken cancellationToken = default);
        Task<ReloadResult> ReloadPluginAsync(string pluginId, ReloadOptions options = null, CancellationToken cancellationToken = default);
        Task<T> CreatePluginInstanceAsync<T>(string pluginId, InstanceOptions options = null, CancellationToken cancellationToken = default) where T : class, IPlugin;
        PluginInfo GetPluginInfo(string pluginId);
        IEnumerable<PluginInfo> GetLoadedPlugins();
        Task<IEnumerable<DiscoveredPlugin>> DiscoverPluginsAsync(string directory = null, CancellationToken cancellationToken = default);
        Task<DependencyResolutionResult> ResolveDependenciesAsync(string pluginPath, CancellationToken cancellationToken = default);
        void UpdateConfiguration(Action<PluginConfiguration> configAction);
    }

    public class PluginContext;
    {
        public string ContextId { get; set; }
        public string PluginId { get; set; }
        public string PluginPath { get; set; }
        public PluginManifest Manifest { get; set; }
        public AssemblyLoadContext AssemblyContext { get; set; }
        public Sandbox Sandbox { get; set; }
        public LoadOptions LoadOptions { get; set; }
        public PluginStatus Status { get; set; }
        public bool IsIsolated { get; set; }
        public bool IsUnloadable { get; set; }
        public DateTime LoadTime { get; set; }
        public DateTime InitializationTime { get; set; }
        public Exception LastError { get; set; }

        // Assembly information;
        public Assembly MainAssembly { get; set; }
        public Type PluginType { get; set; }
        public List<Assembly> LoadedAssemblies { get; } = new List<Assembly>();
        public Dictionary<string, Assembly> DependencyAssemblies { get; } = new Dictionary<string, Assembly>();
        public List<AssemblyDependencyResolver> DependencyResolvers { get; } = new List<AssemblyDependencyResolver>();

        // Dependencies;
        public List<PluginDependency> Dependencies { get; set; } = new List<PluginDependency>();
        public List<DependencyIssue> DependencyIssues { get; set; } = new List<DependencyIssue>();

        // Instances;
        public Dictionary<string, PluginInstanceInfo> Instances { get; } = new Dictionary<string, PluginInstanceInfo>();
    }

    public class PluginManifest;
    {
        public string FilePath { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string PluginType { get; set; }
        public string TargetFramework { get; set; }
        public List<string> RequiredPermissions { get; set; } = new List<string>();
        public List<PluginDependency> Dependencies { get; set; } = new List<PluginDependency>();
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        public List<string> SupportedPlatforms { get; set; } = new List<string>();
        public string MinimumNedaVersion { get; set; }
        public string MaximumNedaVersion { get; set; }
        public bool RequiresIsolation { get; set; }
        public bool SupportsUnloading { get; set; } = true;
    }

    public class LoadOptions;
    {
        public static LoadOptions Default => new LoadOptions();

        public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.AssemblyIsolation;
        public bool ForceReload { get; set; }
        public bool StrictValidation { get; set; } = true;
        public bool StrictDependencyResolution { get; set; } = true;
        public bool StrictDependencyLoading { get; set; }
        public bool EnableSecurityScan { get; set; } = true;
        public bool EnableSignatureVerification { get; set; } = true;
        public bool LoadDependencies { get; set; } = true;
        public Dictionary<string, object> InitialConfiguration { get; set; } = new Dictionary<string, object>();
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    }

    public class BatchLoadOptions;
    {
        public static BatchLoadOptions Default => new BatchLoadOptions();

        public LoadOptions LoadOptions { get; set; } = LoadOptions.Default;
        public int MaxConcurrentLoads { get; set; } = 3;
        public bool StopOnFirstError { get; set; }
        public bool ContinueOnValidationWarning { get; set; } = true;
    }

    public class UnloadOptions;
    {
        public static UnloadOptions Default => new UnloadOptions();

        public bool ForceUnload { get; set; }
        public bool ThrowOnFailure { get; set; } = true;
        public bool IsShuttingDown { get; set; }
        public string Reason { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool CleanupFiles { get; set; } = true;
    }

    public class ReloadOptions;
    {
        public static ReloadOptions Default => new ReloadOptions();

        public UnloadOptions UnloadOptions { get; set; } = UnloadOptions.Default;
        public LoadOptions LoadOptions { get; set; }
        public bool RequireCleanUnload { get; set; } = true;
        public TimeSpan DelayBeforeReload { get; set; } = TimeSpan.FromSeconds(1);
        public bool PreserveState { get; set; }
    }

    public class InstanceOptions;
    {
        public static InstanceOptions Default => new InstanceOptions();

        public string InstanceId { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public string RequiredPermission { get; set; }
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        public object ServiceProvider { get; set; }
        public bool EnableLogging { get; set; } = true;
        public IsolationLevel InstanceIsolation { get; set; } = IsolationLevel.None;
    }

    public class PluginLoadResult;
    {
        public string PluginId { get; set; }
        public LoadStatus Status { get; set; }
        public PluginContext Context { get; set; }
        public PluginManifest Manifest { get; set; }
        public DateTime LoadTime { get; set; }
        public TimeSpan LoadDuration { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public Dictionary<string, object> AdditionalInfo { get; set; } = new Dictionary<string, object>();
    }

    public class BatchLoadResult;
    {
        public int TotalPlugins { get; set; }
        public List<PluginLoadResult> SuccessfulLoads { get; set; } = new List<PluginLoadResult>();
        public List<FailedPluginLoad> FailedLoads { get; set; } = new List<FailedPluginLoad>();
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public double SuccessRate => TotalPlugins > 0 ? (SuccessfulLoads.Count * 100.0) / TotalPlugins : 0;
    }

    public class UnloadResult;
    {
        public string PluginId { get; set; }
        public bool IsSuccess { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime UnloadTime { get; set; }
        public long FreedMemory { get; set; }
        public List<string> UnloadedAssemblies { get; set; } = new List<string>();
        public Dictionary<string, object> AdditionalInfo { get; set; } = new Dictionary<string, object>();
    }

    public class ReloadResult;
    {
        public string PluginId { get; set; }
        public bool IsSuccess { get; set; }
        public PluginContext OldContext { get; set; }
        public PluginContext NewContext { get; set; }
        public DateTime ReloadTime { get; set; }
        public UnloadResult UnloadResult { get; set; }
        public PluginLoadResult LoadResult { get; set; }
        public TimeSpan TotalDuration => (UnloadResult?.UnloadTime ?? DateTime.UtcNow) - ReloadTime;
    }

    public class PluginInfo;
    {
        public string PluginId { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public PluginStatus Status { get; set; }
        public DateTime LoadTime { get; set; }
        public bool IsIsolated { get; set; }
        public bool IsUnloadable { get; set; }
        public int AssemblyCount { get; set; }
        public List<PluginDependency> Dependencies { get; set; } = new List<PluginDependency>();
        public List<string> Permissions { get; set; } = new List<string>();
        public string FilePath { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; } = new Dictionary<string, object>();
    }

    public class DiscoveredPlugin;
    {
        public string FilePath { get; set; }
        public PluginManifest Manifest { get; set; }
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }
        public bool IsValid { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public enum LoadStatus;
    {
        Success,
        AlreadyLoaded,
        ValidationFailed,
        DependencyFailed,
        SecurityDenied,
        Timeout,
        UnknownError;
    }

    public enum PluginStatus;
    {
        Created,
        AssembliesLoaded,
        Initialized,
        Loaded,
        InitializationFailed,
        Unloaded,
        Error;
    }

    public enum IsolationLevel;
    {
        None,
        AssemblyIsolation,
        AppDomainIsolation,
        FullIsolation;
    }

    public class PluginConfiguration;
    {
        public List<string> PluginDirectories { get; } = new List<string>();
        public string DefaultPluginDirectory { get; set; }
        public int MaxConcurrentLoads { get; set; } = 5;
        public IsolationLevel DefaultIsolationLevel { get; set; } = IsolationLevel.AssemblyIsolation;
        public bool EnableSandboxing { get; set; } = true;
        public int SandboxMemoryLimitMB { get; set; } = 512;
        public int SandboxTimeoutSeconds { get; set; } = 30;
        public int MonitoringIntervalMinutes { get; set; } = 5;
        public bool CachePluginManifests { get; set; } = true;
        public bool ValidateAssemblies { get; set; } = true;
        public bool EnableSignatureVerification { get; set; } = true;
        public bool AllowNativeLibraries { get; set; } = false;
        public int MaxPluginSizeMB { get; set; } = 100;
        public bool AutoDiscoverPlugins { get; set; } = true;
        public bool AutoRecoverUnhealthyPlugins { get; set; } = true;
        public bool CheckForUpdates { get; set; } = false;
        public Dictionary<string, object> AdvancedSettings { get; set; } = new Dictionary<string, object>();
    }

    #endregion;

    #region Custom AssemblyLoadContext;

    internal class PluginAssemblyLoadContext : AssemblyLoadContext;
    {
        private readonly string _pluginPath;
        private readonly ILogger _logger;
        private readonly AssemblyDependencyResolver _resolver;

        public PluginAssemblyLoadContext(string name, string pluginPath, ILogger logger = null)
            : base(name, isCollectible: true)
        {
            _pluginPath = pluginPath;
            _logger = logger;
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            _logger?.Debug($"Loading assembly: {assemblyName.FullName}");

            // First try to resolve via dependency resolver;
            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            // Try to load from plugin directory;
            var pluginDir = Path.GetDirectoryName(_pluginPath);
            var possiblePaths = new[]
            {
                Path.Combine(pluginDir, $"{assemblyName.Name}.dll"),
                Path.Combine(pluginDir, "lib", $"{assemblyName.Name}.dll"),
                Path.Combine(pluginDir, "dependencies", $"{assemblyName.Name}.dll")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return LoadFromAssemblyPath(path);
                }
            }

            // Return null to allow fallback to default context;
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }

    #endregion;

    #region Custom Exceptions;

    public class PluginLoadException : Exception
    {
        public string PluginPath { get; }

        public PluginLoadException(string message) : base(message) { }
        public PluginLoadException(string message, Exception innerException) : base(message, innerException) { }
        public PluginLoadException(string pluginPath, string message) : base(message)
        {
            PluginPath = pluginPath;
        }
    }

    public class PluginValidationException : PluginLoadException;
    {
        public PluginValidationException(string message) : base(message) { }
        public PluginValidationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PluginManifestException : PluginLoadException;
    {
        public PluginManifestException(string message) : base(message) { }
        public PluginManifestException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class DependencyResolutionException : PluginLoadException;
    {
        public DependencyResolutionException(string message) : base(message) { }
        public DependencyResolutionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class DependencyLoadException : PluginLoadException;
    {
        public DependencyLoadException(string message) : base(message) { }
        public DependencyLoadException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PluginInitializationException : PluginLoadException;
    {
        public PluginInitializationException(string message) : base(message) { }
        public PluginInitializationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PluginNotFoundException : Exception
    {
        public string PluginId { get; }

        public PluginNotFoundException(string message) : base(message) { }
        public PluginNotFoundException(string pluginId, string message) : base(message)
        {
            PluginId = pluginId;
        }
    }

    public class PluginUnloadException : Exception
    {
        public PluginUnloadException(string message) : base(message) { }
        public PluginUnloadException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PluginReloadException : Exception
    {
        public PluginReloadException(string message) : base(message) { }
        public PluginReloadException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PluginInstanceException : Exception
    {
        public PluginInstanceException(string message) : base(message) { }
        public PluginInstanceException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    // Additional supporting classes would be defined here...
}
