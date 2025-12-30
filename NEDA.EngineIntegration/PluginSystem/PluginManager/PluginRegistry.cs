using NEDA.AI.ComputerVision;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginManager;
using NEDA.PluginSystem.PluginSDK;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml;

namespace NEDA.PluginSystem.PluginManager;
{
    /// <summary>
    /// Plugin registration metadata structure;
    /// </summary>
    public class PluginMetadata;
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("author")]
        public string Author { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("assemblyPath")]
        public string AssemblyPath { get; set; }

        [JsonProperty("entryType")]
        public string EntryType { get; set; }

        [JsonProperty("dependencies")]
        public List<string> Dependencies { get; set; } = new List<string>();

        [JsonProperty("compatibility")]
        public Version Compatibility { get; set; }

        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonProperty("lastLoaded")]
        public DateTime LastLoaded { get; set; }

        [JsonProperty("loadOrder")]
        public int LoadOrder { get; set; }

        [JsonProperty("configurationSchema")]
        public string ConfigurationSchema { get; set; }

        [JsonProperty("requiredPermissions")]
        public List<string> RequiredPermissions { get; set; } = new List<string>();

        [JsonProperty("healthStatus")]
        public PluginHealthStatus HealthStatus { get; set; }

        [JsonProperty("errorCount")]
        public int ErrorCount { get; set; }

        [JsonIgnore]
        public Assembly PluginAssembly { get; set; }

        [JsonIgnore]
        public IPlugin PluginInstance { get; set; }

        [JsonIgnore]
        public Type PluginType { get; set; }
    }

    /// <summary>
    /// Plugin health status enumeration;
    /// </summary>
    public enum PluginHealthStatus;
    {
        Unknown = 0,
        Healthy = 1,
        Warning = 2,
        Error = 3,
        Disabled = 4,
        Loading = 5;
    }

    /// <summary>
    /// Plugin dependency graph node;
    /// </summary>
    public class DependencyNode;
    {
        public string PluginId { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<string> Dependents { get; set; } = new List<string>();
        public bool IsResolved { get; set; }
    }

    /// <summary>
    /// Central registry for managing all plugins in the NEDA system;
    /// Implements thread-safe operations and dependency resolution;
    /// </summary>
    public class PluginRegistry : IPluginRegistry, IDisposable;
    {
        private readonly ConcurrentDictionary<string, PluginMetadata> _plugins;
        private readonly ConcurrentDictionary<string, DependencyNode> _dependencyGraph;
        private readonly ConcurrentDictionary<string, List<IPlugin>> _categoryIndex;
        private readonly ILogger _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly object _syncLock = new object();
        private bool _isDisposed;
        private string _registryFilePath;

        /// <summary>
        /// Event fired when plugin is registered;
        /// </summary>
        public event EventHandler<PluginEventArgs> PluginRegistered;

        /// <summary>
        /// Event fired when plugin is unregistered;
        /// </summary>
        public event EventHandler<PluginEventArgs> PluginUnregistered;

        /// <summary>
        /// Event fired when plugin status changes;
        /// </summary>
        public event EventHandler<PluginStatusEventArgs> PluginStatusChanged;

        /// <summary>
        /// Initializes a new instance of PluginRegistry
        /// </summary>
        public PluginRegistry(ILogger logger, IServiceProvider serviceProvider)
        {
            _plugins = new ConcurrentDictionary<string, PluginMetadata>();
            _dependencyGraph = new ConcurrentDictionary<string, DependencyNode>();
            _categoryIndex = new ConcurrentDictionary<string, List<IPlugin>>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

            _logger.Info("PluginRegistry initialized");
        }

        /// <summary>
        /// Registers a plugin with the system;
        /// </summary>
        /// <param name="metadata">Plugin metadata</param>
        /// <param name="assembly">Plugin assembly</param>
        /// <param name="pluginInstance">Plugin instance</param>
        /// <returns>Registration result</returns>
        public PluginRegistrationResult RegisterPlugin(PluginMetadata metadata, Assembly assembly, IPlugin pluginInstance)
        {
            ValidateMetadata(metadata);

            lock (_syncLock)
            {
                if (_plugins.ContainsKey(metadata.Id))
                {
                    _logger.Warn($"Plugin {metadata.Id} is already registered");
                    return PluginRegistrationResult.AlreadyRegistered;
                }

                try
                {
                    // Validate dependencies;
                    var dependencyResult = ValidateDependencies(metadata);
                    if (!dependencyResult.IsValid)
                    {
                        _logger.Error($"Dependency validation failed for plugin {metadata.Id}: {dependencyResult.ErrorMessage}");
                        return PluginRegistrationResult.DependencyError;
                    }

                    // Store metadata;
                    metadata.PluginAssembly = assembly;
                    metadata.PluginInstance = pluginInstance;
                    metadata.PluginType = pluginInstance.GetType();
                    metadata.LastLoaded = DateTime.UtcNow;
                    metadata.HealthStatus = PluginHealthStatus.Healthy;

                    if (!_plugins.TryAdd(metadata.Id, metadata))
                    {
                        throw new PluginRegistrationException($"Failed to add plugin {metadata.Id} to registry");
                    }

                    // Update dependency graph;
                    UpdateDependencyGraph(metadata);

                    // Index by category;
                    IndexPluginByCategory(pluginInstance);

                    // Initialize plugin;
                    InitializePlugin(metadata);

                    // Fire event;
                    OnPluginRegistered(new PluginEventArgs(metadata.Id, metadata.Name));

                    _logger.Info($"Plugin {metadata.Name} v{metadata.Version} registered successfully");

                    return PluginRegistrationResult.Success;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to register plugin {metadata.Id}: {ex.Message}", ex);
                    return PluginRegistrationResult.InitializationError;
                }
            }
        }

        /// <summary>
        /// Unregisters a plugin from the system;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <param name="force">Force unregistration even if plugin has dependents</param>
        /// <returns>Unregistration result</returns>
        public PluginUnregistrationResult UnregisterPlugin(string pluginId, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID cannot be null or empty", nameof(pluginId));

            lock (_syncLock)
            {
                if (!_plugins.TryGetValue(pluginId, out var metadata))
                {
                    return PluginUnregistrationResult.NotFound;
                }

                // Check if other plugins depend on this one;
                if (!force && HasDependents(pluginId))
                {
                    var dependents = GetDependents(pluginId);
                    _logger.Warn($"Cannot unregister plugin {pluginId}. Dependents: {string.Join(", ", dependents)}");
                    return PluginUnregistrationResult.HasDependents;
                }

                try
                {
                    // Shutdown plugin;
                    ShutdownPlugin(metadata);

                    // Remove from registry
                    if (!_plugins.TryRemove(pluginId, out _))
                    {
                        throw new PluginRegistrationException($"Failed to remove plugin {pluginId} from registry");
                    }

                    // Remove from dependency graph;
                    RemoveFromDependencyGraph(pluginId);

                    // Remove from category index;
                    RemoveFromCategoryIndex(metadata.PluginInstance);

                    // Fire event;
                    OnPluginUnregistered(new PluginEventArgs(pluginId, metadata.Name));

                    _logger.Info($"Plugin {metadata.Name} unregistered successfully");

                    return PluginUnregistrationResult.Success;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to unregister plugin {pluginId}: {ex.Message}", ex);
                    return PluginUnregistrationResult.ShutdownError;
                }
            }
        }

        /// <summary>
        /// Gets plugin metadata by ID;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <returns>Plugin metadata or null if not found</returns>
        public PluginMetadata GetPluginMetadata(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID cannot be null or empty", nameof(pluginId));

            return _plugins.TryGetValue(pluginId, out var metadata) ? metadata : null;
        }

        /// <summary>
        /// Gets all registered plugins;
        /// </summary>
        /// <param name="includeDisabled">Include disabled plugins</param>
        /// <returns>Collection of plugin metadata</returns>
        public IReadOnlyCollection<PluginMetadata> GetAllPlugins(bool includeDisabled = false)
        {
            if (includeDisabled)
            {
                return _plugins.Values.ToList().AsReadOnly();
            }

            return _plugins.Values;
                .Where(p => p.IsEnabled && p.HealthStatus != PluginHealthStatus.Disabled)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets plugins by category;
        /// </summary>
        /// <param name="category">Plugin category</param>
        /// <returns>List of plugins in the category</returns>
        public List<IPlugin> GetPluginsByCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category cannot be null or empty", nameof(category));

            return _categoryIndex.TryGetValue(category, out var plugins)
                ? new List<IPlugin>(plugins)
                : new List<IPlugin>();
        }

        /// <summary>
        /// Enables a disabled plugin;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <returns>True if successful</returns>
        public bool EnablePlugin(string pluginId)
        {
            return SetPluginEnabledState(pluginId, true);
        }

        /// <summary>
        /// Disables an enabled plugin;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <returns>True if successful</returns>
        public bool DisablePlugin(string pluginId)
        {
            return SetPluginEnabledState(pluginId, false);
        }

        /// <summary>
        /// Updates plugin metadata;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <param name="updater">Metadata updater function</param>
        /// <returns>True if successful</returns>
        public bool UpdatePluginMetadata(string pluginId, Action<PluginMetadata> updater)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID cannot be null or empty", nameof(pluginId));

            if (updater == null)
                throw new ArgumentNullException(nameof(updater));

            lock (_syncLock)
            {
                if (!_plugins.TryGetValue(pluginId, out var metadata))
                {
                    _logger.Warn($"Plugin {pluginId} not found for update");
                    return false;
                }

                try
                {
                    var oldStatus = metadata.HealthStatus;
                    var oldEnabled = metadata.IsEnabled;

                    updater(metadata);

                    // Fire status change event if needed;
                    if (oldStatus != metadata.HealthStatus || oldEnabled != metadata.IsEnabled)
                    {
                        OnPluginStatusChanged(new PluginStatusEventArgs(
                            pluginId,
                            metadata.Name,
                            oldStatus,
                            metadata.HealthStatus,
                            oldEnabled,
                            metadata.IsEnabled;
                        ));
                    }

                    _logger.Info($"Plugin {pluginId} metadata updated");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to update plugin {pluginId} metadata: {ex.Message}", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Gets plugin instance by ID;
        /// </summary>
        /// <typeparam name="T">Plugin interface type</typeparam>
        /// <param name="pluginId">Plugin identifier</param>
        /// <returns>Plugin instance or null</returns>
        public T GetPluginInstance<T>(string pluginId) where T : class, IPlugin;
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID cannot be null or empty", nameof(pluginId));

            if (!_plugins.TryGetValue(pluginId, out var metadata))
            {
                return null;
            }

            return metadata.PluginInstance as T;
        }

        /// <summary>
        /// Gets all plugin instances of specified type;
        /// </summary>
        /// <typeparam name="T">Plugin interface type</typeparam>
        /// <returns>Collection of plugin instances</returns>
        public IEnumerable<T> GetPluginInstances<T>() where T : class, IPlugin;
        {
            return _plugins.Values;
                .Where(p => p.IsEnabled && p.HealthStatus == PluginHealthStatus.Healthy)
                .Select(p => p.PluginInstance)
                .OfType<T>()
                .ToList();
        }

        /// <summary>
        /// Resolves plugin dependencies in correct load order;
        /// </summary>
        /// <returns>Ordered list of plugin IDs</returns>
        public List<string> ResolveLoadOrder()
        {
            lock (_syncLock)
            {
                var sorted = new List<string>();
                var visited = new HashSet<string>();
                var tempMark = new HashSet<string>();

                foreach (var node in _dependencyGraph.Values)
                {
                    Visit(node.PluginId, visited, tempMark, sorted);
                }

                return sorted;
            }
        }

        /// <summary>
        /// Validates plugin dependencies;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <returns>Validation result</returns>
        public DependencyValidationResult ValidateDependencies(string pluginId)
        {
            if (!_plugins.TryGetValue(pluginId, out var metadata))
            {
                return new DependencyValidationResult;
                {
                    IsValid = false,
                    ErrorMessage = $"Plugin {pluginId} not found"
                };
            }

            return ValidateDependencies(metadata);
        }

        /// <summary>
        /// Saves registry state to persistent storage;
        /// </summary>
        /// <param name="filePath">Path to save registry</param>
        public async Task SaveRegistryAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            try
            {
                var registryData = _plugins.Values;
                    .Select(p => new;
                    {
                        p.Id,
                        p.Name,
                        p.Version,
                        p.Author,
                        p.Description,
                        p.AssemblyPath,
                        p.EntryType,
                        p.Dependencies,
                        p.Compatibility,
                        p.IsEnabled,
                        p.LastLoaded,
                        p.LoadOrder,
                        p.ConfigurationSchema,
                        p.RequiredPermissions,
                        p.HealthStatus,
                        p.ErrorCount;
                    })
                    .ToList();

                var json = JsonConvert.SerializeObject(registryData, Formatting.Indented);
                await System.IO.File.WriteAllTextAsync(filePath, json);

                _registryFilePath = filePath;
                _logger.Info($"Plugin registry saved to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save plugin registry: {ex.Message}", ex);
                throw new PluginRegistryException("Failed to save registry", ex);
            }
        }

        /// <summary>
        /// Loads registry state from persistent storage;
        /// </summary>
        /// <param name="filePath">Path to load registry from</param>
        public async Task LoadRegistryAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!System.IO.File.Exists(filePath))
            {
                _logger.Warn($"Registry file not found: {filePath}");
                return;
            }

            try
            {
                var json = await System.IO.File.ReadAllTextAsync(filePath);
                var registryData = JsonConvert.DeserializeObject<List<PluginMetadata>>(json);

                lock (_syncLock)
                {
                    _plugins.Clear();
                    _dependencyGraph.Clear();
                    _categoryIndex.Clear();

                    foreach (var metadata in registryData)
                    {
                        // Rebuild in-memory structures without loading assemblies;
                        _plugins.TryAdd(metadata.Id, metadata);
                        UpdateDependencyGraph(metadata);
                    }
                }

                _registryFilePath = filePath;
                _logger.Info($"Plugin registry loaded from {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load plugin registry: {ex.Message}", ex);
                throw new PluginRegistryException("Failed to load registry", ex);
            }
        }

        /// <summary>
        /// Gets plugin dependency graph;
        /// </summary>
        /// <returns>Dependency graph representation</returns>
        public Dictionary<string, List<string>> GetDependencyGraph()
        {
            return _dependencyGraph.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Dependencies.ToList()
            );
        }

        /// <summary>
        /// Gets plugins that depend on specified plugin;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <returns>List of dependent plugin IDs</returns>
        public List<string> GetDependents(string pluginId)
        {
            if (_dependencyGraph.TryGetValue(pluginId, out var node))
            {
                return new List<string>(node.Dependents);
            }
            return new List<string>();
        }

        /// <summary>
        /// Reports plugin health status;
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <param name="status">Health status</param>
        /// <param name="errorMessage">Optional error message</param>
        public void ReportPluginHealth(string pluginId, PluginHealthStatus status, string errorMessage = null)
        {
            UpdatePluginMetadata(pluginId, metadata =>
            {
                var oldStatus = metadata.HealthStatus;
                metadata.HealthStatus = status;

                if (status == PluginHealthStatus.Error)
                {
                    metadata.ErrorCount++;
                    _logger.Error($"Plugin {pluginId} reported error: {errorMessage}");
                }

                if (oldStatus != status)
                {
                    _logger.Info($"Plugin {pluginId} health status changed from {oldStatus} to {status}");
                }
            });
        }

        /// <summary>
        /// Clears all plugins from registry
        /// </summary>
        /// <param name="shutdownPlugins">Shutdown plugins before clearing</param>
        public void ClearRegistry(bool shutdownPlugins = true)
        {
            lock (_syncLock)
            {
                var pluginIds = _plugins.Keys.ToList();

                foreach (var pluginId in pluginIds)
                {
                    try
                    {
                        if (shutdownPlugins && _plugins.TryGetValue(pluginId, out var metadata))
                        {
                            ShutdownPlugin(metadata);
                        }

                        _plugins.TryRemove(pluginId, out _);
                        RemoveFromDependencyGraph(pluginId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Error clearing plugin {pluginId}: {ex.Message}", ex);
                    }
                }

                _categoryIndex.Clear();
                _logger.Info("Plugin registry cleared");
            }
        }

        #region Private Methods;

        private void ValidateMetadata(PluginMetadata metadata)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            if (string.IsNullOrWhiteSpace(metadata.Id))
                throw new PluginRegistrationException("Plugin ID is required");

            if (string.IsNullOrWhiteSpace(metadata.Name))
                throw new PluginRegistrationException("Plugin name is required");

            if (string.IsNullOrWhiteSpace(metadata.Version))
                throw new PluginRegistrationException("Plugin version is required");

            if (metadata.PluginInstance == null)
                throw new PluginRegistrationException("Plugin instance is required");

            // Validate version format;
            if (!System.Version.TryParse(metadata.Version, out _))
            {
                throw new PluginRegistrationException("Invalid version format");
            }
        }

        private DependencyValidationResult ValidateDependencies(PluginMetadata metadata)
        {
            var result = new DependencyValidationResult { IsValid = true };

            foreach (var dependency in metadata.Dependencies)
            {
                if (!_plugins.TryGetValue(dependency, out var depMetadata))
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Dependency {dependency} not found";
                    break;
                }

                if (!depMetadata.IsEnabled || depMetadata.HealthStatus != PluginHealthStatus.Healthy)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Dependency {dependency} is not in healthy state";
                    break;
                }

                // Check version compatibility if specified;
                if (metadata.Compatibility != null)
                {
                    var depVersion = System.Version.Parse(depMetadata.Version);
                    if (depVersion < metadata.Compatibility)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = $"Dependency {dependency} version {depVersion} is lower than required {metadata.Compatibility}";
                        break;
                    }
                }
            }

            return result;
        }

        private void UpdateDependencyGraph(PluginMetadata metadata)
        {
            var node = _dependencyGraph.GetOrAdd(metadata.Id, id => new DependencyNode { PluginId = id });
            node.Dependencies = new List<string>(metadata.Dependencies);
            node.IsResolved = metadata.Dependencies.All(d => _plugins.ContainsKey(d));

            // Update dependents for each dependency;
            foreach (var dependency in metadata.Dependencies)
            {
                var depNode = _dependencyGraph.GetOrAdd(dependency, id => new DependencyNode { PluginId = id });
                if (!depNode.Dependents.Contains(metadata.Id))
                {
                    depNode.Dependents.Add(metadata.Id);
                }
            }
        }

        private void RemoveFromDependencyGraph(string pluginId)
        {
            if (_dependencyGraph.TryRemove(pluginId, out var node))
            {
                // Remove this plugin from dependents lists;
                foreach (var dependency in node.Dependencies)
                {
                    if (_dependencyGraph.TryGetValue(dependency, out var depNode))
                    {
                        depNode.Dependents.Remove(pluginId);
                    }
                }
            }
        }

        private void IndexPluginByCategory(IPlugin plugin)
        {
            var category = plugin.Category ?? "Uncategorized";
            var pluginsInCategory = _categoryIndex.GetOrAdd(category, new List<IPlugin>());

            if (!pluginsInCategory.Contains(plugin))
            {
                pluginsInCategory.Add(plugin);
            }
        }

        private void RemoveFromCategoryIndex(IPlugin plugin)
        {
            var category = plugin.Category ?? "Uncategorized";
            if (_categoryIndex.TryGetValue(category, out var pluginsInCategory))
            {
                pluginsInCategory.Remove(plugin);
                if (pluginsInCategory.Count == 0)
                {
                    _categoryIndex.TryRemove(category, out _);
                }
            }
        }

        private void InitializePlugin(PluginMetadata metadata)
        {
            try
            {
                metadata.PluginInstance.Initialize(_serviceProvider);
                metadata.HealthStatus = PluginHealthStatus.Healthy;
                _logger.Debug($"Plugin {metadata.Id} initialized successfully");
            }
            catch (Exception ex)
            {
                metadata.HealthStatus = PluginHealthStatus.Error;
                metadata.ErrorCount++;
                _logger.Error($"Failed to initialize plugin {metadata.Id}: {ex.Message}", ex);
                throw new PluginInitializationException($"Failed to initialize plugin {metadata.Id}", ex);
            }
        }

        private void ShutdownPlugin(PluginMetadata metadata)
        {
            try
            {
                metadata.PluginInstance.Shutdown();
                _logger.Debug($"Plugin {metadata.Id} shutdown successfully");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error during plugin {metadata.Id} shutdown: {ex.Message}");
                // Continue with shutdown even if plugin throws;
            }
        }

        private bool HasDependents(string pluginId)
        {
            return _dependencyGraph.TryGetValue(pluginId, out var node) && node.Dependents.Any();
        }

        private bool SetPluginEnabledState(string pluginId, bool enabled)
        {
            lock (_syncLock)
            {
                if (!_plugins.TryGetValue(pluginId, out var metadata))
                {
                    _logger.Warn($"Plugin {pluginId} not found for enable/disable");
                    return false;
                }

                if (metadata.IsEnabled == enabled)
                {
                    return true; // Already in desired state;
                }

                try
                {
                    if (enabled)
                    {
                        // Validate dependencies before enabling;
                        var validation = ValidateDependencies(metadata);
                        if (!validation.IsValid)
                        {
                            _logger.Error($"Cannot enable plugin {pluginId}: {validation.ErrorMessage}");
                            return false;
                        }

                        InitializePlugin(metadata);
                        metadata.IsEnabled = true;
                        _logger.Info($"Plugin {pluginId} enabled");
                    }
                    else;
                    {
                        // Check if other plugins depend on this one;
                        if (HasDependents(pluginId))
                        {
                            _logger.Warn($"Cannot disable plugin {pluginId} because other plugins depend on it");
                            return false;
                        }

                        ShutdownPlugin(metadata);
                        metadata.IsEnabled = false;
                        _logger.Info($"Plugin {pluginId} disabled");
                    }

                    OnPluginStatusChanged(new PluginStatusEventArgs(
                        pluginId,
                        metadata.Name,
                        metadata.HealthStatus,
                        metadata.HealthStatus,
                        !enabled,
                        enabled;
                    ));

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to {(enabled ? "enable" : "disable")} plugin {pluginId}: {ex.Message}", ex);
                    return false;
                }
            }
        }

        private void Visit(string pluginId, HashSet<string> visited, HashSet<string> tempMark, List<string> sorted)
        {
            if (tempMark.Contains(pluginId))
            {
                throw new PluginDependencyCycleException($"Circular dependency detected involving plugin {pluginId}");
            }

            if (visited.Contains(pluginId))
            {
                return;
            }

            tempMark.Add(pluginId);

            if (_dependencyGraph.TryGetValue(pluginId, out var node))
            {
                foreach (var dependency in node.Dependencies)
                {
                    Visit(dependency, visited, tempMark, sorted);
                }
            }

            tempMark.Remove(pluginId);
            visited.Add(pluginId);
            sorted.Add(pluginId);
        }

        private void OnPluginRegistered(PluginEventArgs e)
        {
            PluginRegistered?.Invoke(this, e);
        }

        private void OnPluginUnregistered(PluginEventArgs e)
        {
            PluginUnregistered?.Invoke(this, e);
        }

        private void OnPluginStatusChanged(PluginStatusEventArgs e)
        {
            PluginStatusChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Save registry state on disposal;
                    if (!string.IsNullOrEmpty(_registryFilePath))
                    {
                        try
                        {
                            SaveRegistryAsync(_registryFilePath).Wait(5000);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Failed to save registry on disposal: {ex.Message}", ex);
                        }
                    }

                    // Clear all plugins;
                    ClearRegistry(shutdownPlugins: true);

                    _logger.Info("PluginRegistry disposed");
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
    /// Plugin registry interface for dependency injection;
    /// </summary>
    public interface IPluginRegistry : IDisposable
    {
        event EventHandler<PluginEventArgs> PluginRegistered;
        event EventHandler<PluginEventArgs> PluginUnregistered;
        event EventHandler<PluginStatusEventArgs> PluginStatusChanged;

        PluginRegistrationResult RegisterPlugin(PluginMetadata metadata, Assembly assembly, IPlugin pluginInstance);
        PluginUnregistrationResult UnregisterPlugin(string pluginId, bool force = false);
        PluginMetadata GetPluginMetadata(string pluginId);
        IReadOnlyCollection<PluginMetadata> GetAllPlugins(bool includeDisabled = false);
        List<IPlugin> GetPluginsByCategory(string category);
        bool EnablePlugin(string pluginId);
        bool DisablePlugin(string pluginId);
        bool UpdatePluginMetadata(string pluginId, Action<PluginMetadata> updater);
        T GetPluginInstance<T>(string pluginId) where T : class, IPlugin;
        IEnumerable<T> GetPluginInstances<T>() where T : class, IPlugin;
        List<string> ResolveLoadOrder();
        DependencyValidationResult ValidateDependencies(string pluginId);
        Task SaveRegistryAsync(string filePath);
        Task LoadRegistryAsync(string filePath);
        Dictionary<string, List<string>> GetDependencyGraph();
        List<string> GetDependents(string pluginId);
        void ReportPluginHealth(string pluginId, PluginHealthStatus status, string errorMessage = null);
        void ClearRegistry(bool shutdownPlugins = true);
    }

    /// <summary>
    /// Plugin registration result enumeration;
    /// </summary>
    public enum PluginRegistrationResult;
    {
        Success = 0,
        AlreadyRegistered = 1,
        DependencyError = 2,
        InitializationError = 3,
        ValidationError = 4;
    }

    /// <summary>
    /// Plugin unregistration result enumeration;
    /// </summary>
    public enum PluginUnregistrationResult;
    {
        Success = 0,
        NotFound = 1,
        HasDependents = 2,
        ShutdownError = 3;
    }

    /// <summary>
    /// Dependency validation result;
    /// </summary>
    public class DependencyValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Plugin event arguments;
    /// </summary>
    public class PluginEventArgs : EventArgs;
    {
        public string PluginId { get; }
        public string PluginName { get; }
        public DateTime Timestamp { get; }

        public PluginEventArgs(string pluginId, string pluginName)
        {
            PluginId = pluginId;
            PluginName = pluginName;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Plugin status event arguments;
    /// </summary>
    public class PluginStatusEventArgs : PluginEventArgs;
    {
        public PluginHealthStatus OldStatus { get; }
        public PluginHealthStatus NewStatus { get; }
        public bool OldEnabled { get; }
        public bool NewEnabled { get; }

        public PluginStatusEventArgs(string pluginId, string pluginName,
            PluginHealthStatus oldStatus, PluginHealthStatus newStatus,
            bool oldEnabled, bool newEnabled)
            : base(pluginId, pluginName)
        {
            OldStatus = oldStatus;
            NewStatus = newStatus;
            OldEnabled = oldEnabled;
            NewEnabled = newEnabled;
        }
    }

    /// <summary>
    /// Plugin registry exception;
    /// </summary>
    public class PluginRegistryException : Exception
    {
        public PluginRegistryException(string message) : base(message) { }
        public PluginRegistryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin registration exception;
    /// </summary>
    public class PluginRegistrationException : PluginRegistryException;
    {
        public PluginRegistrationException(string message) : base(message) { }
    }

    /// <summary>
    /// Plugin initialization exception;
    /// </summary>
    public class PluginInitializationException : PluginRegistryException;
    {
        public PluginInitializationException(string message) : base(message) { }
        public PluginInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin dependency cycle exception;
    /// </summary>
    public class PluginDependencyCycleException : PluginRegistryException;
    {
        public PluginDependencyCycleException(string message) : base(message) { }
    }

    #endregion;
}
