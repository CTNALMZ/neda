using Microsoft.Extensions.DependencyInjection;
using NEDA.AI.ComputerVision;
using NEDA.Core.Common;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.PluginSystem.PluginManager;
using NEDA.PluginSystem.PluginRegistry
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace NEDA.PluginSystem.PluginSDK;
{
    /// <summary>
    /// Plugin execution context providing runtime information and services;
    /// </summary>
    public class PluginExecutionContext;
    {
        /// <summary>
        /// Plugin ID;
        /// </summary>
        public string PluginId { get; set; }

        /// <summary>
        /// Plugin instance;
        /// </summary>
        public IPlugin Plugin { get; set; }

        /// <summary>
        /// Service provider for dependency injection;
        /// </summary>
        public IServiceProvider ServiceProvider { get; set; }

        /// <summary>
        /// Logger instance for the plugin;
        /// </summary>
        public ILogger Logger { get; set; }

        /// <summary>
        /// Plugin configuration;
        /// </summary>
        public JObject Configuration { get; set; }

        /// <summary>
        /// Plugin state storage;
        /// </summary>
        public Dictionary<string, object> State { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Plugin execution mode;
        /// </summary>
        public PluginExecutionMode ExecutionMode { get; set; }

        /// <summary>
        /// Security context for the plugin;
        /// </summary>
        public PluginSecurityContext SecurityContext { get; set; }

        /// <summary>
        /// Cancellation token for plugin operations;
        /// </summary>
        public System.Threading.CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// Plugin security context;
    /// </summary>
    public class PluginSecurityContext;
    {
        /// <summary>
        /// User identity;
        /// </summary>
        public string UserIdentity { get; set; }

        /// <summary>
        /// Required permissions;
        /// </summary>
        public List<string> RequiredPermissions { get; set; } = new List<string>();

        /// <summary>
        /// Granted permissions;
        /// </summary>
        public List<string> GrantedPermissions { get; set; } = new List<string>();

        /// <summary>
        /// Security level;
        /// </summary>
        public SecurityLevel SecurityLevel { get; set; }

        /// <summary>
        /// Authentication token;
        /// </summary>
        public string AuthenticationToken { get; set; }

        /// <summary>
        /// Checks if plugin has required permission;
        /// </summary>
        public bool HasPermission(string permission)
        {
            return GrantedPermissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Plugin execution mode;
    /// </summary>
    public enum PluginExecutionMode;
    {
        Normal = 0,
        Debug = 1,
        Safe = 2,
        Isolated = 3,
        HighPriority = 4,
        LowPriority = 5;
    }

    /// <summary>
    /// Security level for plugins;
    /// </summary>
    public enum SecurityLevel;
    {
        Untrusted = 0,
        Restricted = 1,
        Standard = 2,
        Trusted = 3,
        System = 4;
    }

    /// <summary>
    /// Plugin capability flags;
    /// </summary>
    [Flags]
    public enum PluginCapabilities;
    {
        None = 0,
        UI = 1 << 0,
        BackgroundProcessing = 1 << 1,
        FileSystemAccess = 1 << 2,
        NetworkAccess = 1 << 3,
        HardwareAccess = 1 << 4,
        DatabaseAccess = 1 << 5,
        ExternalProcess = 1 << 6,
        Configuration = 1 << 7,
        Logging = 1 << 8,
        Notifications = 1 << 9,
        ScheduledTasks = 1 << 10,
        WebService = 1 << 11,
        MachineLearning = 1 << 12,
        ComputerVision = 1 << 13,
        AudioProcessing = 1 << 14,
        VideoProcessing = 1 << 15,
        All = ~0;
    }

    /// <summary>
    /// Plugin status;
    /// </summary>
    public enum PluginStatus;
    {
        Uninitialized = 0,
        Initializing = 1,
        Initialized = 2,
        Starting = 3,
        Running = 4,
        Paused = 5,
        Stopping = 6,
        Stopped = 7,
        Error = 8,
        Disabled = 9;
    }

    /// <summary>
    /// Plugin metadata attribute for assembly-level metadata;
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public class PluginMetadataAttribute : Attribute;
    {
        public string Id { get; }
        public string Name { get; }
        public string Version { get; }
        public string Author { get; }
        public string Description { get; }

        public PluginMetadataAttribute(string id, string name, string version, string author, string description = "")
        {
            Id = id;
            Name = name;
            Version = version;
            Author = author;
            Description = description;
        }
    }

    /// <summary>
    /// Plugin category attribute;
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class PluginCategoryAttribute : Attribute;
    {
        public string Category { get; }

        public PluginCategoryAttribute(string category)
        {
            Category = category;
        }
    }

    /// <summary>
    /// Plugin dependency attribute;
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class PluginDependencyAttribute : Attribute;
    {
        public string PluginId { get; }
        public string MinVersion { get; }
        public string MaxVersion { get; }
        public bool Optional { get; }

        public PluginDependencyAttribute(string pluginId, string minVersion = null, string maxVersion = null, bool optional = false)
        {
            PluginId = pluginId;
            MinVersion = minVersion;
            MaxVersion = maxVersion;
            Optional = optional;
        }
    }

    /// <summary>
    /// Plugin capability attribute;
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class PluginCapabilityAttribute : Attribute;
    {
        public PluginCapabilities Capabilities { get; }

        public PluginCapabilityAttribute(PluginCapabilities capabilities)
        {
            Capabilities = capabilities;
        }
    }

    /// <summary>
    /// Plugin configuration attribute for properties;
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class PluginConfigurationAttribute : Attribute;
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string DefaultValue { get; set; }
        public bool Required { get; set; }
        public Type ValueType { get; set; }
        public string[] ValidValues { get; set; }
        public double MinValue { get; set; } = double.MinValue;
        public double MaxValue { get; set; } = double.MaxValue;

        public PluginConfigurationAttribute()
        {
        }

        public PluginConfigurationAttribute(string key)
        {
            Key = key;
        }
    }

    /// <summary>
    /// Plugin command attribute for method-level commands;
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class PluginCommandAttribute : Attribute;
    {
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public string[] Aliases { get; set; }
        public bool RequiresElevation { get; set; }

        public PluginCommandAttribute(string name, string description = "", string category = "General")
        {
            Name = name;
            Description = description;
            Category = category;
        }
    }

    /// <summary>
    /// Base class for all NEDA plugins providing common functionality and lifecycle management;
    /// </summary>
    public abstract class PluginBase : IPlugin, IHealthCheckable, IDisposable;
    {
        #region Protected Fields;

        protected readonly object _syncLock = new object();
        protected PluginExecutionContext _context;
        protected PluginStatus _status = PluginStatus.Uninitialized;
        protected DateTime _startTime;
        protected DateTime _lastActivityTime;
        protected List<IDisposable> _disposables = new List<IDisposable>();
        protected Dictionary<string, MethodInfo> _commands = new Dictionary<string, MethodInfo>();
        protected JObject _configuration;
        protected bool _isDisposed;

        #endregion;

        #region Public Properties;

        /// <summary>
        /// Gets the plugin ID;
        /// </summary>
        public virtual string Id => GetType().GetCustomAttribute<PluginMetadataAttribute>()?.Id;
            ?? GetType().Name;

        /// <summary>
        /// Gets the plugin name;
        /// </summary>
        public virtual string Name => GetType().GetCustomAttribute<PluginMetadataAttribute>()?.Name;
            ?? GetType().Name;

        /// <summary>
        /// Gets the plugin version;
        /// </summary>
        public virtual string Version => GetType().GetCustomAttribute<PluginMetadataAttribute>()?.Version;
            ?? "1.0.0";

        /// <summary>
        /// Gets the plugin author;
        /// </summary>
        public virtual string Author => GetType().GetCustomAttribute<PluginMetadataAttribute>()?.Author;
            ?? "Unknown";

        /// <summary>
        /// Gets the plugin description;
        /// </summary>
        public virtual string Description => GetType().GetCustomAttribute<PluginMetadataAttribute>()?.Description;
            ?? string.Empty;

        /// <summary>
        /// Gets the plugin category;
        /// </summary>
        public virtual string Category => GetType().GetCustomAttribute<PluginCategoryAttribute>()?.Category;
            ?? "General";

        /// <summary>
        /// Gets the plugin capabilities;
        /// </summary>
        public virtual PluginCapabilities Capabilities => GetType()
            .GetCustomAttribute<PluginCapabilityAttribute>()?.Capabilities;
            ?? PluginCapabilities.None;

        /// <summary>
        /// Gets the plugin dependencies;
        /// </summary>
        public virtual List<PluginDependencyAttribute> Dependencies => GetType()
            .GetCustomAttributes<PluginDependencyAttribute>()
            .ToList();

        /// <summary>
        /// Gets the plugin status;
        /// </summary>
        public PluginStatus Status;
        {
            get;
            {
                lock (_syncLock)
                {
                    return _status;
                }
            }
            protected set;
            {
                lock (_syncLock)
                {
                    if (_status != value)
                    {
                        var oldStatus = _status;
                        _status = value;
                        OnStatusChanged(oldStatus, value);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the plugin uptime;
        /// </summary>
        public TimeSpan Uptime => _startTime != DateTime.MinValue;
            ? DateTime.UtcNow - _startTime;
            : TimeSpan.Zero;

        /// <summary>
        /// Gets the plugin configuration;
        /// </summary>
        public virtual JObject Configuration => _configuration ??= LoadConfiguration();

        /// <summary>
        /// Gets or sets the execution context;
        /// </summary>
        public PluginExecutionContext Context;
        {
            get => _context;
            set;
            {
                _context = value;
                if (_context != null)
                {
                    _context.Plugin = this;
                }
            }
        }

        /// <summary>
        /// Gets the plugin logger;
        /// </summary>
        protected ILogger Logger => _context?.Logger;

        /// <summary>
        /// Gets the service provider;
        /// </summary>
        protected IServiceProvider ServiceProvider => _context?.ServiceProvider;

        #endregion;

        #region Events;

        /// <summary>
        /// Event raised when plugin status changes;
        /// </summary>
        public event EventHandler<PluginStatusChangedEventArgs> StatusChanged;

        /// <summary>
        /// Event raised when plugin configuration changes;
        /// </summary>
        public event EventHandler<PluginConfigurationChangedEventArgs> ConfigurationChanged;

        /// <summary>
        /// Event raised when plugin encounters an error;
        /// </summary>
        public event EventHandler<PluginErrorEventArgs> ErrorOccurred;

        /// <summary>
        /// Event raised when plugin performs an activity;
        /// </summary>
        public event EventHandler<PluginActivityEventArgs> ActivityPerformed;

        #endregion;

        #region Lifecycle Methods;

        /// <summary>
        /// Initializes the plugin with execution context;
        /// </summary>
        public virtual async Task InitializeAsync(IServiceProvider serviceProvider, ILogger logger = null)
        {
            if (Status >= PluginStatus.Initializing)
            {
                Logger?.Warn($"Plugin {Name} is already initialized or initializing");
                return;
            }

            try
            {
                Status = PluginStatus.Initializing;

                // Create execution context if not provided;
                if (_context == null)
                {
                    _context = new PluginExecutionContext;
                    {
                        PluginId = Id,
                        Plugin = this,
                        ServiceProvider = serviceProvider,
                        Logger = logger ?? CreateLogger(serviceProvider),
                        Configuration = Configuration,
                        ExecutionMode = PluginExecutionMode.Normal,
                        SecurityContext = new PluginSecurityContext;
                        {
                            SecurityLevel = SecurityLevel.Standard;
                        }
                    };
                }
                else;
                {
                    _context.ServiceProvider = serviceProvider;
                    _context.Logger = logger ?? _context.Logger ?? CreateLogger(serviceProvider);
                }

                // Validate plugin;
                ValidatePlugin();

                // Load configuration;
                await LoadConfigurationAsync();

                // Initialize commands;
                InitializeCommands();

                // Call derived class initialization;
                await OnInitializeAsync();

                Status = PluginStatus.Initialized;

                Logger?.Info($"Plugin {Name} v{Version} initialized successfully");
                OnActivityPerformed("Initialized", "Plugin initialized successfully");
            }
            catch (Exception ex)
            {
                Status = PluginStatus.Error;
                Logger?.Error($"Failed to initialize plugin {Name}: {ex.Message}", ex);
                OnErrorOccurred("InitializationError", ex);
                throw new PluginInitializationException($"Failed to initialize plugin {Name}", ex);
            }
        }

        /// <summary>
        /// Starts the plugin;
        /// </summary>
        public virtual async Task StartAsync()
        {
            if (Status != PluginStatus.Initialized && Status != PluginStatus.Stopped)
            {
                Logger?.Warn($"Plugin {Name} cannot start from status {Status}");
                return;
            }

            try
            {
                Status = PluginStatus.Starting;
                _startTime = DateTime.UtcNow;
                _lastActivityTime = DateTime.UtcNow;

                // Call derived class start;
                await OnStartAsync();

                Status = PluginStatus.Running;

                Logger?.Info($"Plugin {Name} started successfully");
                OnActivityPerformed("Started", "Plugin started successfully");
            }
            catch (Exception ex)
            {
                Status = PluginStatus.Error;
                Logger?.Error($"Failed to start plugin {Name}: {ex.Message}", ex);
                OnErrorOccurred("StartError", ex);
                throw new PluginStartException($"Failed to start plugin {Name}", ex);
            }
        }

        /// <summary>
        /// Stops the plugin;
        /// </summary>
        public virtual async Task StopAsync()
        {
            if (Status != PluginStatus.Running && Status != PluginStatus.Paused)
            {
                Logger?.Warn($"Plugin {Name} is not running, cannot stop");
                return;
            }

            try
            {
                Status = PluginStatus.Stopping;

                // Call derived class stop;
                await OnStopAsync();

                // Clean up disposables;
                CleanupDisposables();

                Status = PluginStatus.Stopped;

                Logger?.Info($"Plugin {Name} stopped successfully");
                OnActivityPerformed("Stopped", "Plugin stopped successfully");
            }
            catch (Exception ex)
            {
                Status = PluginStatus.Error;
                Logger?.Error($"Failed to stop plugin {Name}: {ex.Message}", ex);
                OnErrorOccurred("StopError", ex);
                throw new PluginStopException($"Failed to stop plugin {Name}", ex);
            }
        }

        /// <summary>
        /// Pauses the plugin;
        /// </summary>
        public virtual async Task PauseAsync()
        {
            if (Status != PluginStatus.Running)
            {
                Logger?.Warn($"Plugin {Name} is not running, cannot pause");
                return;
            }

            try
            {
                Status = PluginStatus.Paused;
                await OnPauseAsync();

                Logger?.Info($"Plugin {Name} paused");
                OnActivityPerformed("Paused", "Plugin paused");
            }
            catch (Exception ex)
            {
                Status = PluginStatus.Error;
                Logger?.Error($"Failed to pause plugin {Name}: {ex.Message}", ex);
                OnErrorOccurred("PauseError", ex);
                throw;
            }
        }

        /// <summary>
        /// Resumes the plugin;
        /// </summary>
        public virtual async Task ResumeAsync()
        {
            if (Status != PluginStatus.Paused)
            {
                Logger?.Warn($"Plugin {Name} is not paused, cannot resume");
                return;
            }

            try
            {
                Status = PluginStatus.Running;
                await OnResumeAsync();

                Logger?.Info($"Plugin {Name} resumed");
                OnActivityPerformed("Resumed", "Plugin resumed");
            }
            catch (Exception ex)
            {
                Status = PluginStatus.Error;
                Logger?.Error($"Failed to resume plugin {Name}: {ex.Message}", ex);
                OnErrorOccurred("ResumeError", ex);
                throw;
            }
        }

        /// <summary>
        /// Shuts down the plugin;
        /// </summary>
        public virtual async Task ShutdownAsync()
        {
            try
            {
                if (Status == PluginStatus.Running || Status == PluginStatus.Paused)
                {
                    await StopAsync();
                }

                Status = PluginStatus.Stopping;

                // Call derived class shutdown;
                await OnShutdownAsync();

                // Clean up all resources;
                CleanupAllResources();

                Status = PluginStatus.Stopped;

                Logger?.Info($"Plugin {Name} shutdown completed");
                OnActivityPerformed("Shutdown", "Plugin shutdown completed");
            }
            catch (Exception ex)
            {
                Status = PluginStatus.Error;
                Logger?.Error($"Failed to shutdown plugin {Name}: {ex.Message}", ex);
                OnErrorOccurred("ShutdownError", ex);
                throw;
            }
        }

        /// <summary>
        /// Performs health check on the plugin;
        /// </summary>
        public virtual async Task<PluginHealthStatus> CheckHealthAsync()
        {
            try
            {
                var health = await OnCheckHealthAsync();

                // Update last activity time if healthy;
                if (health == PluginHealthStatus.Healthy)
                {
                    _lastActivityTime = DateTime.UtcNow;
                }

                return health;
            }
            catch (Exception ex)
            {
                Logger?.Error($"Health check failed for plugin {Name}: {ex.Message}", ex);
                return PluginHealthStatus.Error;
            }
        }

        #endregion;

        #region Command System;

        /// <summary>
        /// Gets available commands for the plugin;
        /// </summary>
        public virtual Dictionary<string, PluginCommandInfo> GetCommands()
        {
            var commandInfos = new Dictionary<string, PluginCommandInfo>();

            foreach (var kvp in _commands)
            {
                var method = kvp.Value;
                var commandAttr = method.GetCustomAttribute<PluginCommandAttribute>();

                commandInfos[kvp.Key] = new PluginCommandInfo;
                {
                    Name = kvp.Key,
                    Description = commandAttr?.Description ?? string.Empty,
                    Category = commandAttr?.Category ?? "General",
                    Method = method,
                    RequiresElevation = commandAttr?.RequiresElevation ?? false,
                    Aliases = commandAttr?.Aliases ?? Array.Empty<string>()
                };
            }

            return commandInfos;
        }

        /// <summary>
        /// Executes a command;
        /// </summary>
        public virtual async Task<PluginCommandResult> ExecuteCommandAsync(string commandName, params object[] parameters)
        {
            if (!_commands.TryGetValue(commandName, out var method))
            {
                // Check aliases;
                var commandInfo = GetCommands().Values.FirstOrDefault(c =>
                    c.Aliases != null && c.Aliases.Contains(commandName, StringComparer.OrdinalIgnoreCase));

                if (commandInfo == null)
                {
                    return new PluginCommandResult;
                    {
                        Success = false,
                        ErrorMessage = $"Command '{commandName}' not found",
                        CommandName = commandName;
                    };
                }

                method = commandInfo.Method;
            }

            try
            {
                // Check elevation requirement;
                var commandAttr = method.GetCustomAttribute<PluginCommandAttribute>();
                if (commandAttr?.RequiresElevation == true &&
                    _context?.SecurityContext?.SecurityLevel < SecurityLevel.Trusted)
                {
                    return new PluginCommandResult;
                    {
                        Success = false,
                        ErrorMessage = "Insufficient privileges to execute this command",
                        CommandName = commandName;
                    };
                }

                // Execute command;
                object result;
                if (method.ReturnType == typeof(Task))
                {
                    var task = (Task)method.Invoke(this, parameters);
                    await task;
                    result = null;
                }
                else if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var task = (Task)method.Invoke(this, parameters);
                    await task;
                    result = task.GetType().GetProperty("Result")?.GetValue(task);
                }
                else;
                {
                    result = method.Invoke(this, parameters);
                }

                _lastActivityTime = DateTime.UtcNow;
                OnActivityPerformed($"CommandExecuted:{commandName}", $"Command '{commandName}' executed successfully");

                return new PluginCommandResult;
                {
                    Success = true,
                    Result = result,
                    CommandName = commandName,
                    ExecutionTime = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                Logger?.Error($"Failed to execute command '{commandName}': {ex.Message}", ex);
                OnErrorOccurred($"CommandError:{commandName}", ex);

                return new PluginCommandResult;
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    CommandName = commandName,
                    ExecutionTime = DateTime.UtcNow;
                };
            }
        }

        #endregion;

        #region Configuration Management;

        /// <summary>
        /// Updates plugin configuration;
        /// </summary>
        public virtual async Task UpdateConfigurationAsync(JObject newConfiguration)
        {
            try
            {
                var oldConfiguration = _configuration;
                _configuration = newConfiguration;

                // Apply configuration to properties;
                ApplyConfigurationToProperties();

                // Save configuration;
                await SaveConfigurationAsync();

                // Call derived class configuration update;
                await OnConfigurationUpdatedAsync(oldConfiguration, newConfiguration);

                OnConfigurationChanged(oldConfiguration, newConfiguration);

                Logger?.Info($"Configuration updated for plugin {Name}");
                OnActivityPerformed("ConfigurationUpdated", "Configuration updated");
            }
            catch (Exception ex)
            {
                Logger?.Error($"Failed to update configuration for plugin {Name}: {ex.Message}", ex);
                OnErrorOccurred("ConfigurationUpdateError", ex);
                throw;
            }
        }

        /// <summary>
        /// Gets configuration schema;
        /// </summary>
        public virtual JObject GetConfigurationSchema()
        {
            var schema = new JObject;
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray()
            };

            var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                var configAttr = property.GetCustomAttribute<PluginConfigurationAttribute>();
                if (configAttr == null)
                    continue;

                var key = configAttr.Key ?? property.Name;
                var propertySchema = new JObject;
                {
                    ["type"] = GetJsonType(property.PropertyType),
                    ["title"] = configAttr.DisplayName ?? property.Name,
                    ["description"] = configAttr.Description ?? string.Empty,
                    ["default"] = configAttr.DefaultValue ?? GetDefaultValue(property.PropertyType)
                };

                // Add constraints;
                if (configAttr.ValidValues != null && configAttr.ValidValues.Length > 0)
                {
                    propertySchema["enum"] = new JArray(configAttr.ValidValues);
                }

                if (configAttr.MinValue != double.MinValue)
                {
                    propertySchema["minimum"] = configAttr.MinValue;
                }

                if (configAttr.MaxValue != double.MaxValue)
                {
                    propertySchema["maximum"] = configAttr.MaxValue;
                }

                schema["properties"][key] = propertySchema;

                if (configAttr.Required)
                {
                    ((JArray)schema["required"]).Add(key);
                }
            }

            return schema;
        }

        /// <summary>
        /// Resets configuration to defaults;
        /// </summary>
        public virtual async Task ResetConfigurationAsync()
        {
            try
            {
                var defaultConfig = GetDefaultConfiguration();
                await UpdateConfigurationAsync(defaultConfig);

                Logger?.Info($"Configuration reset to defaults for plugin {Name}");
            }
            catch (Exception ex)
            {
                Logger?.Error($"Failed to reset configuration for plugin {Name}: {ex.Message}", ex);
                throw;
            }
        }

        #endregion;

        #region Protected Virtual Methods (Override Points)

        /// <summary>
        /// Called when plugin is initializing;
        /// </summary>
        protected virtual Task OnInitializeAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when plugin is starting;
        /// </summary>
        protected virtual Task OnStartAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when plugin is stopping;
        /// </summary>
        protected virtual Task OnStopAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when plugin is pausing;
        /// </summary>
        protected virtual Task OnPauseAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when plugin is resuming;
        /// </summary>
        protected virtual Task OnResumeAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when plugin is shutting down;
        /// </summary>
        protected virtual Task OnShutdownAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when configuration is updated;
        /// </summary>
        protected virtual Task OnConfigurationUpdatedAsync(JObject oldConfiguration, JObject newConfiguration)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called to perform health check;
        /// </summary>
        protected virtual Task<PluginHealthStatus> OnCheckHealthAsync()
        {
            // Default health check based on status;
            var health = Status switch;
            {
                PluginStatus.Running => PluginHealthStatus.Healthy,
                PluginStatus.Paused => PluginHealthStatus.Warning,
                PluginStatus.Error => PluginHealthStatus.Error,
                PluginStatus.Stopped => PluginHealthStatus.Disabled,
                _ => PluginHealthStatus.Unknown;
            };

            return Task.FromResult(health);
        }

        /// <summary>
        /// Called to validate plugin configuration;
        /// </summary>
        protected virtual Task<bool> ValidateConfigurationAsync(JObject configuration)
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// Called to get default configuration;
        /// </summary>
        protected virtual JObject GetDefaultConfiguration()
        {
            var config = new JObject();

            var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                var configAttr = property.GetCustomAttribute<PluginConfigurationAttribute>();
                if (configAttr == null)
                    continue;

                var key = configAttr.Key ?? property.Name;
                var defaultValue = configAttr.DefaultValue ?? GetDefaultValue(property.PropertyType);
                config[key] = JToken.FromObject(defaultValue);
            }

            return config;
        }

        /// <summary>
        /// Called to load configuration from persistent storage;
        /// </summary>
        protected virtual Task<JObject> LoadConfigurationFromStorageAsync()
        {
            // Default implementation: load from file;
            var configPath = GetConfigurationFilePath();
            if (System.IO.File.Exists(configPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(configPath);
                    return Task.FromResult(JObject.Parse(json));
                }
                catch (Exception ex)
                {
                    Logger?.Warn($"Failed to load configuration from {configPath}: {ex.Message}");
                }
            }

            return Task.FromResult<JObject>(null);
        }

        /// <summary>
        /// Called to save configuration to persistent storage;
        /// </summary>
        protected virtual Task SaveConfigurationToStorageAsync(JObject configuration)
        {
            // Default implementation: save to file;
            var configPath = GetConfigurationFilePath();
            try
            {
                var directory = System.IO.Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                System.IO.File.WriteAllText(configPath, configuration.ToString(Formatting.Indented));
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger?.Error($"Failed to save configuration to {configPath}: {ex.Message}", ex);
                throw;
            }
        }

        #endregion;

        #region Protected Helper Methods;

        /// <summary>
        /// Creates a logger for the plugin;
        /// </summary>
        protected virtual ILogger CreateLogger(IServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            if (loggerFactory != null)
            {
                return loggerFactory.CreateLogger($"Plugin.{Id}");
            }

            // Fallback to console logger;
            return new ConsoleLogger($"Plugin.{Id}");
        }

        /// <summary>
        /// Registers a disposable resource for automatic cleanup;
        /// </summary>
        protected void RegisterDisposable(IDisposable disposable)
        {
            if (disposable != null && !_disposables.Contains(disposable))
            {
                _disposables.Add(disposable);
            }
        }

        /// <summary>
        /// Unregisters a disposable resource;
        /// </summary>
        protected void UnregisterDisposable(IDisposable disposable)
        {
            _disposables.Remove(disposable);
        }

        /// <summary>
        /// Gets configuration file path;
        /// </summary>
        protected virtual string GetConfigurationFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, "NEDA", "Plugins", Id, "config.json");
        }

        /// <summary>
        /// Validates plugin requirements and dependencies;
        /// </summary>
        protected virtual void ValidatePlugin()
        {
            // Check required capabilities;
            if (Capabilities.HasFlag(PluginCapabilities.NetworkAccess) &&
                _context?.SecurityContext?.SecurityLevel < SecurityLevel.Trusted)
            {
                throw new PluginSecurityException($"Plugin {Name} requires network access but has insufficient security level");
            }

            // Validate configuration;
            var configValidation = ValidateConfigurationAsync(Configuration).GetAwaiter().GetResult();
            if (!configValidation)
            {
                throw new PluginConfigurationException($"Configuration validation failed for plugin {Name}");
            }
        }

        /// <summary>
        /// Updates last activity time;
        /// </summary>
        protected void UpdateActivity()
        {
            _lastActivityTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Logs an activity;
        /// </summary>
        protected void LogActivity(string activity, string message)
        {
            Logger?.Info($"[{activity}] {message}");
            OnActivityPerformed(activity, message);
        }

        /// <summary>
        /// Logs a warning;
        /// </summary>
        protected void LogWarning(string warning, string message)
        {
            Logger?.Warn($"[{warning}] {message}");
        }

        /// <summary>
        /// Logs an error;
        /// </summary>
        protected void LogError(string error, Exception exception)
        {
            Logger?.Error($"[{error}] {exception.Message}", exception);
            OnErrorOccurred(error, exception);
        }

        #endregion;

        #region Private Methods;

        private void InitializeCommands()
        {
            _commands.Clear();

            var methods = GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var commandAttr = method.GetCustomAttribute<PluginCommandAttribute>();
                if (commandAttr != null)
                {
                    var commandName = commandAttr.Name ?? method.Name;
                    _commands[commandName] = method;

                    // Register aliases;
                    if (commandAttr.Aliases != null)
                    {
                        foreach (var alias in commandAttr.Aliases)
                        {
                            if (!string.IsNullOrEmpty(alias) && !_commands.ContainsKey(alias))
                            {
                                _commands[alias] = method;
                            }
                        }
                    }
                }
            }
        }

        private async Task LoadConfigurationAsync()
        {
            // Try to load from storage;
            var storedConfig = await LoadConfigurationFromStorageAsync();

            if (storedConfig != null)
            {
                _configuration = storedConfig;
            }
            else;
            {
                // Use default configuration;
                _configuration = GetDefaultConfiguration();
                await SaveConfigurationAsync();
            }

            // Apply configuration to properties;
            ApplyConfigurationToProperties();
        }

        private JObject LoadConfiguration()
        {
            // Synchronous version for property getter;
            try
            {
                var configPath = GetConfigurationFilePath();
                if (System.IO.File.Exists(configPath))
                {
                    var json = System.IO.File.ReadAllText(configPath);
                    return JObject.Parse(json);
                }
            }
            catch
            {
                // Ignore errors in property getter;
            }

            return GetDefaultConfiguration();
        }

        private async Task SaveConfigurationAsync()
        {
            if (_configuration != null)
            {
                await SaveConfigurationToStorageAsync(_configuration);
            }
        }

        private void ApplyConfigurationToProperties()
        {
            if (_configuration == null)
                return;

            var properties = GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                var configAttr = property.GetCustomAttribute<PluginConfigurationAttribute>();
                if (configAttr == null)
                    continue;

                var key = configAttr.Key ?? property.Name;
                if (_configuration.TryGetValue(key, out var value))
                {
                    try
                    {
                        var convertedValue = ConvertValue(value, property.PropertyType);
                        property.SetValue(this, convertedValue);
                    }
                    catch (Exception ex)
                    {
                        Logger?.Warn($"Failed to apply configuration value for {key}: {ex.Message}");
                    }
                }
            }
        }

        private object ConvertValue(JToken value, Type targetType)
        {
            if (value == null || value.Type == JTokenType.Null)
                return null;

            try
            {
                return value.ToObject(targetType);
            }
            catch
            {
                // Fallback to type conversion;
                return Convert.ChangeType(value.ToString(), targetType);
            }
        }

        private string GetJsonType(Type type)
        {
            if (type == typeof(string))
                return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
                return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "number";
            if (type == typeof(bool))
                return "boolean";
            if (type == typeof(DateTime))
                return "string"; // date-time format;
            if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
                return "array";

            return "object";
        }

        private object GetDefaultValue(Type type)
        {
            if (type.IsValueType)
                return Activator.CreateInstance(type);

            return null;
        }

        private void CleanupDisposables()
        {
            foreach (var disposable in _disposables)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    Logger?.Warn($"Error disposing resource: {ex.Message}");
                }
            }
            _disposables.Clear();
        }

        private void CleanupAllResources()
        {
            CleanupDisposables();

            // Additional cleanup if needed;
            _context = null;
            _configuration = null;
            _commands.Clear();
        }

        private void OnStatusChanged(PluginStatus oldStatus, PluginStatus newStatus)
        {
            StatusChanged?.Invoke(this, new PluginStatusChangedEventArgs(Id, Name, oldStatus, newStatus));
        }

        private void OnConfigurationChanged(JObject oldConfiguration, JObject newConfiguration)
        {
            ConfigurationChanged?.Invoke(this, new PluginConfigurationChangedEventArgs(Id, Name, oldConfiguration, newConfiguration));
        }

        private void OnErrorOccurred(string errorCode, Exception exception)
        {
            ErrorOccurred?.Invoke(this, new PluginErrorEventArgs(Id, Name, errorCode, exception));
        }

        private void OnActivityPerformed(string activity, string message)
        {
            ActivityPerformed?.Invoke(this, new PluginActivityEventArgs(Id, Name, activity, message));
        }

        #endregion;

        #region IPlugin Implementation (Legacy Interface Support)

        Task IPlugin.Initialize(IServiceProvider serviceProvider)
        {
            return InitializeAsync(serviceProvider);
        }

        void IPlugin.Shutdown()
        {
            ShutdownAsync().Wait();
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    try
                    {
                        if (Status == PluginStatus.Running || Status == PluginStatus.Paused)
                        {
                            StopAsync().Wait(5000);
                        }

                        ShutdownAsync().Wait(5000);
                    }
                    catch (Exception ex)
                    {
                        Logger?.Error($"Error during plugin disposal: {ex.Message}");
                    }

                    CleanupAllResources();
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

        #region ConsoleLogger (Fallback Implementation)

        private class ConsoleLogger : ILogger;
        {
            private readonly string _category;

            public ConsoleLogger(string category)
            {
                _category = category;
            }

            public void Debug(string message) => WriteLog("DEBUG", message);
            public void Info(string message) => WriteLog("INFO", message);
            public void Warn(string message) => WriteLog("WARN", message);
            public void Error(string message) => WriteLog("ERROR", message);
            public void Error(string message, Exception exception) => WriteLog("ERROR", $"{message}: {exception}");

            private void WriteLog(string level, string message)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [{level}] [{_category}] {message}");
            }
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Main plugin interface;
    /// </summary>
    public interface IPlugin;
    {
        string Id { get; }
        string Name { get; }
        string Version { get; }
        string Author { get; }
        string Description { get; }
        string Category { get; }

        Task Initialize(IServiceProvider serviceProvider);
        void Shutdown();
    }

    /// <summary>
    /// Plugin command information;
    /// </summary>
    public class PluginCommandInfo;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public MethodInfo Method { get; set; }
        public bool RequiresElevation { get; set; }
        public string[] Aliases { get; set; }
    }

    /// <summary>
    /// Plugin command result;
    /// </summary>
    public class PluginCommandResult;
    {
        public bool Success { get; set; }
        public object Result { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public string CommandName { get; set; }
        public DateTime ExecutionTime { get; set; }
    }

    /// <summary>
    /// Plugin status changed event arguments;
    /// </summary>
    public class PluginStatusChangedEventArgs : EventArgs;
    {
        public string PluginId { get; }
        public string PluginName { get; }
        public PluginStatus OldStatus { get; }
        public PluginStatus NewStatus { get; }
        public DateTime Timestamp { get; }

        public PluginStatusChangedEventArgs(string pluginId, string pluginName, PluginStatus oldStatus, PluginStatus newStatus)
        {
            PluginId = pluginId;
            PluginName = pluginName;
            OldStatus = oldStatus;
            NewStatus = newStatus;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Plugin configuration changed event arguments;
    /// </summary>
    public class PluginConfigurationChangedEventArgs : EventArgs;
    {
        public string PluginId { get; }
        public string PluginName { get; }
        public JObject OldConfiguration { get; }
        public JObject NewConfiguration { get; }
        public DateTime Timestamp { get; }

        public PluginConfigurationChangedEventArgs(string pluginId, string pluginName, JObject oldConfiguration, JObject newConfiguration)
        {
            PluginId = pluginId;
            PluginName = pluginName;
            OldConfiguration = oldConfiguration;
            NewConfiguration = newConfiguration;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Plugin error event arguments;
    /// </summary>
    public class PluginErrorEventArgs : EventArgs;
    {
        public string PluginId { get; }
        public string PluginName { get; }
        public string ErrorCode { get; }
        public Exception Exception { get; }
        public DateTime Timestamp { get; }

        public PluginErrorEventArgs(string pluginId, string pluginName, string errorCode, Exception exception)
        {
            PluginId = pluginId;
            PluginName = pluginName;
            ErrorCode = errorCode;
            Exception = exception;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Plugin activity event arguments;
    /// </summary>
    public class PluginActivityEventArgs : EventArgs;
    {
        public string PluginId { get; }
        public string PluginName { get; }
        public string Activity { get; }
        public string Message { get; }
        public DateTime Timestamp { get; }

        public PluginActivityEventArgs(string pluginId, string pluginName, string activity, string message)
        {
            PluginId = pluginId;
            PluginName = pluginName;
            Activity = activity;
            Message = message;
            Timestamp = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Plugin initialization exception;
    /// </summary>
    public class PluginInitializationException : Exception
    {
        public PluginInitializationException(string message) : base(message) { }
        public PluginInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin start exception;
    /// </summary>
    public class PluginStartException : Exception
    {
        public PluginStartException(string message) : base(message) { }
        public PluginStartException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin stop exception;
    /// </summary>
    public class PluginStopException : Exception
    {
        public PluginStopException(string message) : base(message) { }
        public PluginStopException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Plugin security exception;
    /// </summary>
    public class PluginSecurityException : Exception
    {
        public PluginSecurityException(string message) : base(message) { }
    }

    /// <summary>
    /// Plugin configuration exception;
    /// </summary>
    public class PluginConfigurationException : Exception
    {
        public PluginConfigurationException(string message) : base(message) { }
        public PluginConfigurationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
