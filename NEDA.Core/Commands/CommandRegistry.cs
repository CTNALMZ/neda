using Microsoft.Extensions.Logging;
using NEDA.Communication.DialogSystem.TopicHandler;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NEDA.Core.Commands
{
    /// <summary>
    /// Command registration metadata;
    /// </summary>
    public class CommandMetadata
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public Type CommandType { get; set; }
        public string[] Aliases { get; set; }
        public string RequiredPermission { get; set; }
        public int MinimumArguments { get; set; }
        public int MaximumArguments { get; set; }
        public bool RequiresElevatedPrivileges { get; set; }
        public string[] SupportedPlatforms { get; set; }
        public DateTime RegistrationTime { get; set; }
        public Version Version { get; set; }
        public Dictionary<string, string> Parameters { get; set; }
        public string Usage { get; set; }

        public CommandMetadata()
        {
            Aliases = Array.Empty<string>();
            SupportedPlatforms = new[] { "Windows", "Linux", "macOS" };
            Parameters = new Dictionary<string, string>();
            Version = new Version(1, 0, 0);
        }
    }

    /// <summary>
    /// Command execution result;
    /// </summary>
    public class CommandExecutionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
        public DateTime ExecutionTime { get; set; }
        public TimeSpan Duration { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public CommandExecutionResult()
        {
            Metadata = new Dictionary<string, object>();
            ExecutionTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Base command interface;
    /// </summary>
    public interface ICommand
    {
        Task<CommandExecutionResult> ExecuteAsync(Dictionary<string, object> parameters);
        CommandMetadata GetMetadata();
        void ValidateParameters(Dictionary<string, object> parameters);
    }

    /// <summary>
    /// Command registry for managing and executing commands;
    /// </summary>
    public class CommandRegistry : ICommandRegistry
    {
        private readonly ILogger<CommandRegistry> _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IPermissionValidator _permissionValidator;
        private readonly ConcurrentDictionary<string, CommandMetadata> _commands;
        private readonly ConcurrentDictionary<string, ICommand> _commandInstances;
        private readonly ConcurrentDictionary<string, List<string>> _categoryIndex;
        private readonly ConcurrentDictionary<string, string> _aliasIndex;

        private bool _isInitialized;
        private readonly object _initializationLock = new object();
        private readonly List<Assembly> _registeredAssemblies;

        /// <summary>
        /// Initializes a new instance of the CommandRegistry
        /// </summary>
        public CommandRegistry(
            ILogger<CommandRegistry> logger,
            ISecurityManager securityManager,
            IPermissionValidator permissionValidator)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _permissionValidator = permissionValidator ?? throw new ArgumentNullException(nameof(permissionValidator));

            _commands = new ConcurrentDictionary<string, CommandMetadata>(StringComparer.OrdinalIgnoreCase);
            _commandInstances = new ConcurrentDictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);
            _categoryIndex = new ConcurrentDictionary<string, List<string>>();
            _aliasIndex = new ConcurrentDictionary<string, string>();
            _registeredAssemblies = new List<Assembly>();

            _isInitialized = false;

            _logger.LogInformation("CommandRegistry initialized");
        }

        /// <summary>
        /// Initializes the command registry
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            lock (_initializationLock)
            {
                if (_isInitialized) return;

                try
                {
                    _logger.LogInformation("Initializing CommandRegistry...");

                    // Auto-discover commands in current assembly;
                    DiscoverCommandsAsync(Assembly.GetExecutingAssembly()).GetAwaiter().GetResult();

                    // Register built-in commands;
                    RegisterBuiltInCommandsAsync().GetAwaiter().GetResult();

                    _isInitialized = true;

                    _logger.LogInformation($"CommandRegistry initialized with {_commands.Count} commands");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize CommandRegistry");
                    throw new CommandRegistryException("Failed to initialize CommandRegistry", ex);
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Registers a command with the registry
        /// </summary>
        public async Task RegisterCommandAsync<TCommand>() where TCommand : ICommand, new()
        {
            await ValidateRegistryInitializedAsync();

            try
            {
                var command = new TCommand();
                var metadata = command.GetMetadata();

                if (string.IsNullOrWhiteSpace(metadata.Name))
                {
                    throw new ArgumentException("Command name cannot be null or empty");
                }

                if (_commands.ContainsKey(metadata.Name))
                {
                    throw new CommandRegistrationException($"Command '{metadata.Name}' is already registered");
                }

                // Validate command metadata;
                ValidateCommandMetadata(metadata);

                // Register command;
                if (_commands.TryAdd(metadata.Name, metadata))
                {
                    _commandInstances[metadata.Name] = command;

                    // Update category index;
                    if (!string.IsNullOrEmpty(metadata.Category))
                    {
                        _categoryIndex.AddOrUpdate(
                            metadata.Category,
                            new List<string> { metadata.Name },
                            (key, existingList) =>
                            {
                                if (!existingList.Contains(metadata.Name))
                                {
                                    existingList.Add(metadata.Name);
                                }
                                return existingList;
                            });
                    }

                    // Register aliases;
                    if (metadata.Aliases != null)
                    {
                        foreach (var alias in metadata.Aliases)
                        {
                            if (!string.IsNullOrWhiteSpace(alias))
                            {
                                _aliasIndex[alias.ToLowerInvariant()] = metadata.Name;
                            }
                        }
                    }

                    _logger.LogInformation($"Command '{metadata.Name}' registered successfully");

                    // Log registration details;
                    await LogCommandRegistrationAsync(metadata);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to register command of type {typeof(TCommand).Name}");
                throw new CommandRegistrationException($"Failed to register command {typeof(TCommand).Name}", ex);
            }
        }

        /// <summary>
        /// Registers a command instance;
        /// </summary>
        public async Task RegisterCommandAsync(ICommand command)
        {
            await ValidateRegistryInitializedAsync();

            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            try
            {
                var metadata = command.GetMetadata();

                if (string.IsNullOrWhiteSpace(metadata.Name))
                {
                    throw new ArgumentException("Command name cannot be null or empty");
                }

                if (_commands.ContainsKey(metadata.Name))
                {
                    throw new CommandRegistrationException($"Command '{metadata.Name}' is already registered");
                }

                // Validate command metadata;
                ValidateCommandMetadata(metadata);

                // Register command;
                if (_commands.TryAdd(metadata.Name, metadata))
                {
                    _commandInstances[metadata.Name] = command;

                    // Update category index;
                    if (!string.IsNullOrEmpty(metadata.Category))
                    {
                        _categoryIndex.AddOrUpdate(
                            metadata.Category,
                            new List<string> { metadata.Name },
                            (key, existingList) =>
                            {
                                if (!existingList.Contains(metadata.Name))
                                {
                                    existingList.Add(metadata.Name);
                                }
                                return existingList;
                            });
                    }

                    // Register aliases;
                    if (metadata.Aliases != null)
                    {
                        foreach (var alias in metadata.Aliases)
                        {
                            if (!string.IsNullOrWhiteSpace(alias))
                            {
                                _aliasIndex[alias.ToLowerInvariant()] = metadata.Name;
                            }
                        }
                    }

                    _logger.LogInformation($"Command '{metadata.Name}' registered successfully");

                    // Log registration details;
                    await LogCommandRegistrationAsync(metadata);
                }
            }
            catch (Exception ex)
            {
                string cmdName = string.Empty;
                try
                {
                    cmdName = command.GetMetadata()?.Name;
                }
                catch
                {
                    // ignore
                }

                _logger.LogError(ex, $"Failed to register command '{cmdName}'");
                throw new CommandRegistrationException($"Failed to register command '{cmdName}'", ex);
            }
        }

        /// <summary>
        /// Unregisters a command;
        /// </summary>
        public async Task<bool> UnregisterCommandAsync(string commandName)
        {
            await ValidateRegistryInitializedAsync();

            if (string.IsNullOrWhiteSpace(commandName))
            {
                throw new ArgumentException("Command name cannot be null or empty", nameof(commandName));
            }

            try
            {
                var resolvedName = ResolveCommandName(commandName);

                if (_commands.TryRemove(resolvedName, out var metadata))
                {
                    _commandInstances.TryRemove(resolvedName, out _);

                    // Remove from category index;
                    if (!string.IsNullOrEmpty(metadata.Category) &&
                        _categoryIndex.TryGetValue(metadata.Category, out var categoryCommands))
                    {
                        categoryCommands.Remove(resolvedName);
                        if (categoryCommands.Count == 0)
                        {
                            _categoryIndex.TryRemove(metadata.Category, out _);
                        }
                    }

                    // Remove aliases;
                    if (metadata.Aliases != null)
                    {
                        foreach (var alias in metadata.Aliases)
                        {
                            if (!string.IsNullOrWhiteSpace(alias))
                            {
                                _aliasIndex.TryRemove(alias.ToLowerInvariant(), out _);
                            }
                        }
                    }

                    _logger.LogInformation($"Command '{resolvedName}' unregistered successfully");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to unregister command '{commandName}'");
                throw new CommandOperationException($"Failed to unregister command '{commandName}'", ex);
            }
        }

        /// <summary>
        /// Executes a command;
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteCommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null,
            string userId = null,
            bool skipPermissionCheck = false)
        {
            await ValidateRegistryInitializedAsync();

            if (string.IsNullOrWhiteSpace(commandName))
            {
                throw new ArgumentException("Command name cannot be null or empty", nameof(commandName));
            }

            var startTime = DateTime.UtcNow;
            var result = new CommandExecutionResult();

            try
            {
                var resolvedName = ResolveCommandName(commandName);

                if (!_commands.TryGetValue(resolvedName, out var metadata))
                {
                    throw new CommandNotFoundException($"Command '{commandName}' not found");
                }

                if (!_commandInstances.TryGetValue(resolvedName, out var command))
                {
                    throw new CommandNotFoundException($"Command instance for '{commandName}' not found");
                }

                // Check permissions if not skipped;
                if (!skipPermissionCheck && !string.IsNullOrEmpty(userId))
                {
                    var hasPermission = await CheckCommandPermissionAsync(metadata, userId);
                    if (!hasPermission)
                    {
                        throw new UnauthorizedCommandException(
                            $"User '{userId}' does not have permission to execute command '{resolvedName}'");
                    }
                }

                // Validate parameters;
                command.ValidateParameters(parameters ?? new Dictionary<string, object>());

                // Validate argument count;
                if (parameters != null)
                {
                    var argCount = parameters.Count;
                    if (argCount < metadata.MinimumArguments || argCount > metadata.MaximumArguments)
                    {
                        throw new ArgumentException(
                            $"Invalid number of arguments. Expected between {metadata.MinimumArguments} and {metadata.MaximumArguments}, got {argCount}");
                    }
                }

                // Execute command;
                _logger.LogInformation($"Executing command '{resolvedName}' for user '{userId}'");

                result = await command.ExecuteAsync(parameters ?? new Dictionary<string, object>());
                result.Duration = DateTime.UtcNow - startTime;

                // Log execution result;
                await LogCommandExecutionAsync(metadata, userId, result);

                _logger.LogInformation($"Command '{resolvedName}' executed successfully in {result.Duration.TotalMilliseconds}ms");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Command execution failed: {ex.Message}";
                result.Exception = ex;
                result.Duration = DateTime.UtcNow - startTime;

                _logger.LogError(ex, $"Failed to execute command '{commandName}'");

                // Log failed execution;
                await LogFailedCommandExecutionAsync(commandName, userId, ex);

                throw new CommandExecutionException($"Failed to execute command '{commandName}'", ex);
            }
        }

        /// <summary>
        /// Gets all registered commands;
        /// </summary>
        public async Task<IEnumerable<CommandMetadata>> GetAllCommandsAsync()
        {
            await ValidateRegistryInitializedAsync();
            return _commands.Values.OrderBy(c => c.Category).ThenBy(c => c.Name);
        }

        /// <summary>
        /// Gets commands by category;
        /// </summary>
        public async Task<IEnumerable<CommandMetadata>> GetCommandsByCategoryAsync(string category)
        {
            await ValidateRegistryInitializedAsync();

            if (string.IsNullOrWhiteSpace(category))
            {
                throw new ArgumentException("Category cannot be null or empty", nameof(category));
            }

            if (_categoryIndex.TryGetValue(category, out var commandNames))
            {
                return commandNames
                    .Select(name => _commands.TryGetValue(name, out var cmd) ? cmd : null)
                    .Where(cmd => cmd != null)
                    .OrderBy(cmd => cmd.Name);
            }

            return Enumerable.Empty<CommandMetadata>();
        }

        /// <summary>
        /// Searches commands by name or description;
        /// </summary>
        public async Task<IEnumerable<CommandMetadata>> SearchCommandsAsync(string searchTerm)
        {
            await ValidateRegistryInitializedAsync();

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return await GetAllCommandsAsync();
            }

            var term = searchTerm.ToLowerInvariant();

            return _commands.Values
                .Where(cmd =>
                    (!string.IsNullOrEmpty(cmd.Name) && cmd.Name.ToLowerInvariant().Contains(term)) ||
                    (!string.IsNullOrEmpty(cmd.Description) && cmd.Description.ToLowerInvariant().Contains(term)) ||
                    (cmd.Aliases != null && cmd.Aliases.Any(a => a.ToLowerInvariant().Contains(term))) ||
                    (!string.IsNullOrEmpty(cmd.Category) && cmd.Category.ToLowerInvariant().Contains(term)))
                .OrderBy(cmd => cmd.Name);
        }

        /// <summary>
        /// Gets command metadata;
        /// </summary>
        public async Task<CommandMetadata> GetCommandMetadataAsync(string commandName)
        {
            await ValidateRegistryInitializedAsync();

            if (string.IsNullOrWhiteSpace(commandName))
            {
                throw new ArgumentException("Command name cannot be null or empty", nameof(commandName));
            }

            var resolvedName = ResolveCommandName(commandName);

            if (_commands.TryGetValue(resolvedName, out var metadata))
            {
                return metadata;
            }

            throw new CommandNotFoundException($"Command '{commandName}' not found");
        }

        /// <summary>
        /// Checks if a command exists;
        /// </summary>
        public async Task<bool> CommandExistsAsync(string commandName)
        {
            await ValidateRegistryInitializedAsync();

            if (string.IsNullOrWhiteSpace(commandName))
            {
                return false;
            }

            try
            {
                var resolvedName = ResolveCommandName(commandName);
                return _commands.ContainsKey(resolvedName);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets all command categories;
        /// </summary>
        public async Task<IEnumerable<string>> GetCategoriesAsync()
        {
            await ValidateRegistryInitializedAsync();
            return _categoryIndex.Keys.OrderBy(k => k);
        }

        /// <summary>
        /// Discovers and registers commands from an assembly;
        /// </summary>
        public async Task<int> DiscoverCommandsAsync(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            if (_registeredAssemblies.Contains(assembly))
            {
                _logger.LogWarning($"Assembly '{assembly.FullName}' already scanned for commands");
                return 0;
            }

            try
            {
                _logger.LogInformation($"Scanning assembly '{assembly.FullName}' for commands...");

                var commandTypes = assembly.GetTypes()
                    .Where(t => typeof(ICommand).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .ToList();

                int registeredCount = 0;

                foreach (var commandType in commandTypes)
                {
                    try
                    {
                        if (Activator.CreateInstance(commandType) is ICommand command)
                        {
                            await RegisterCommandAsync(command);
                            registeredCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to create instance of command type '{commandType.Name}'");
                    }
                }

                _registeredAssemblies.Add(assembly);
                _logger.LogInformation($"Discovered and registered {registeredCount} commands from assembly '{assembly.FullName}'");

                return registeredCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to discover commands in assembly '{assembly.FullName}'");
                throw new CommandDiscoveryException($"Failed to discover commands in assembly '{assembly.FullName}'", ex);
            }
        }

        /// <summary>
        /// Clears all registered commands;
        /// </summary>
        public async Task ClearCommandsAsync()
        {
            await ValidateRegistryInitializedAsync();

            try
            {
                _commands.Clear();
                _commandInstances.Clear();
                _categoryIndex.Clear();
                _aliasIndex.Clear();
                _registeredAssemblies.Clear();

                _logger.LogInformation("All commands cleared from registry");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear commands");
                throw new CommandOperationException("Failed to clear commands", ex);
            }
        }

        /// <summary>
        /// Gets command statistics;
        /// </summary>
        public async Task<Dictionary<string, object>> GetStatisticsAsync()
        {
            await ValidateRegistryInitializedAsync();

            return new Dictionary<string, object>
            {
                ["TotalCommands"] = _commands.Count,
                ["TotalCategories"] = _categoryIndex.Count,
                ["TotalAliases"] = _aliasIndex.Count,
                ["RegisteredAssemblies"] = _registeredAssemblies.Count,
                ["IsInitialized"] = _isInitialized,
                ["LastUpdated"] = DateTime.UtcNow
            };
        }

        #region Private Methods

        private async Task ValidateRegistryInitializedAsync()
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }
        }

        private void ValidateCommandMetadata(CommandMetadata metadata)
        {
            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            if (string.IsNullOrWhiteSpace(metadata.Name))
            {
                throw new ArgumentException("Command name cannot be null or empty");
            }

            if (metadata.MinimumArguments < 0)
            {
                throw new ArgumentException("Minimum arguments cannot be negative");
            }

            if (metadata.MaximumArguments < metadata.MinimumArguments)
            {
                throw new ArgumentException("Maximum arguments cannot be less than minimum arguments");
            }

            // Validate name format;
            if (!System.Text.RegularExpressions.Regex.IsMatch(metadata.Name, @"^[a-zA-Z][a-zA-Z0-9\-_]*$"))
            {
                throw new ArgumentException("Command name can only contain letters, numbers, hyphens, and underscores, and must start with a letter");
            }
        }

        private string ResolveCommandName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            var lowerInput = input.ToLowerInvariant();

            // Check if input is an alias;
            if (_aliasIndex.TryGetValue(lowerInput, out var resolvedName))
            {
                return resolvedName;
            }

            // Check if input is a direct command name;
            if (_commands.ContainsKey(input))
            {
                return input;
            }

            // Try case-insensitive match;
            var matchingCommand = _commands.Keys.FirstOrDefault(k =>
                string.Equals(k, input, StringComparison.OrdinalIgnoreCase));

            if (matchingCommand != null)
            {
                return matchingCommand;
            }

            throw new CommandNotFoundException($"Command or alias '{input}' not found");
        }

        private async Task<bool> CheckCommandPermissionAsync(CommandMetadata metadata, string userId)
        {
            if (string.IsNullOrWhiteSpace(metadata.RequiredPermission))
            {
                return true; // No permission required;
            }

            try
            {
                return await _permissionValidator.HasPermissionAsync(userId, metadata.RequiredPermission);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to check permission '{metadata.RequiredPermission}' for user '{userId}'");
                return false;
            }
        }

        private async Task RegisterBuiltInCommandsAsync()
        {
            try
            {
                // Register built-in help command;
                await RegisterCommandAsync(new HelpCommand(this));

                _logger.LogDebug("Built-in commands registered");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register built-in commands");
                // Don't throw - built-in commands are optional;
            }
        }

        private async Task LogCommandRegistrationAsync(CommandMetadata metadata)
        {
            try
            {
                var logData = new Dictionary<string, object>
                {
                    ["CommandName"] = metadata.Name,
                    ["Category"] = metadata.Category,
                    ["Description"] = metadata.Description,
                    ["RequiredPermission"] = metadata.RequiredPermission,
                    ["Version"] = metadata.Version.ToString(),
                    ["RegistrationTime"] = DateTime.UtcNow
                };

                // Log to security audit log;
                await _securityManager.LogSecurityEventAsync(
                    "CommandRegistered",
                    $"Command '{metadata.Name}' registered",
                    logData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log command registration");
            }
        }

        private async Task LogCommandExecutionAsync(CommandMetadata metadata, string userId, CommandExecutionResult result)
        {
            try
            {
                var logData = new Dictionary<string, object>
                {
                    ["CommandName"] = metadata.Name,
                    ["UserId"] = userId,
                    ["Success"] = result.Success,
                    ["Duration"] = result.Duration.TotalMilliseconds,
                    ["ExecutionTime"] = DateTime.UtcNow,
                    ["ResultMessage"] = result.Message
                };

                // Log to security audit log;
                await _securityManager.LogSecurityEventAsync(
                    "CommandExecuted",
                    $"Command '{metadata.Name}' executed by user '{userId}'",
                    logData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log command execution");
            }
        }

        private async Task LogFailedCommandExecutionAsync(string commandName, string userId, Exception exception)
        {
            try
            {
                var logData = new Dictionary<string, object>
                {
                    ["CommandName"] = commandName,
                    ["UserId"] = userId,
                    ["Error"] = exception.Message,
                    ["StackTrace"] = exception.StackTrace,
                    ["ExecutionTime"] = DateTime.UtcNow
                };

                // Log to security audit log;
                await _securityManager.LogSecurityEventAsync(
                    "CommandExecutionFailed",
                    $"Command '{commandName}' execution failed for user '{userId}'",
                    logData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log failed command execution");
            }
        }

        #endregion

        #region Built-in Commands

        /// <summary>
        /// Built-in help command;
        /// </summary>
        private class HelpCommand : ICommand
        {
            private readonly CommandRegistry _registry;

            public HelpCommand(CommandRegistry registry)
            {
                _registry = registry;
            }

            public async Task<CommandExecutionResult> ExecuteAsync(Dictionary<string, object> parameters)
            {
                var result = new CommandExecutionResult();

                try
                {
                    var commands = await _registry.GetAllCommandsAsync();
                    var categories = await _registry.GetCategoriesAsync();

                    var helpData = new
                    {
                        TotalCommands = commands.Count(),
                        Commands = commands.Select(c => new
                        {
                            c.Name,
                            c.Description,
                            c.Category,
                            c.Usage,
                            c.RequiredPermission,
                            Aliases = c.Aliases
                        }),
                        Categories = categories
                    };

                    result.Success = true;
                    result.Message = "Available commands retrieved successfully";
                    result.Data = helpData;
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = $"Failed to get help: {ex.Message}";
                    result.Exception = ex;
                }

                return result;
            }

            public CommandMetadata GetMetadata()
            {
                return new CommandMetadata
                {
                    Name = "help",
                    Description = "Displays help information about available commands",
                    Category = "System",
                    Aliases = new[] { "?", "h", "commands" },
                    MinimumArguments = 0,
                    MaximumArguments = 1,
                    RequiredPermission = "System.ViewHelp",
                    Usage = "help [command_name]",
                    Parameters = new Dictionary<string, string>
                    {
                        ["command_name"] = "Optional. Name of command to get specific help for"
                    }
                };
            }

            public void ValidateParameters(Dictionary<string, object> parameters)
            {
                // No validation needed for help command;
            }
        }

        #endregion
    }

    #region Exceptions

    public class CommandRegistryException : Exception
    {
        public CommandRegistryException(string message) : base(message) { }
        public CommandRegistryException(string message, Exception inner) : base(message, inner) { }
    }

    public class CommandRegistrationException : CommandRegistryException
    {
        public CommandRegistrationException(string message) : base(message) { }
        public CommandRegistrationException(string message, Exception inner) : base(message, inner) { }
    }

    public class CommandNotFoundException : CommandRegistryException
    {
        public CommandNotFoundException(string message) : base(message) { }
        public CommandNotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class CommandExecutionException : CommandRegistryException
    {
        public CommandExecutionException(string message) : base(message) { }
        public CommandExecutionException(string message, Exception inner) : base(message, inner) { }
    }

    public class UnauthorizedCommandException : CommandRegistryException
    {
        public UnauthorizedCommandException(string message) : base(message) { }
        public UnauthorizedCommandException(string message, Exception inner) : base(message, inner) { }
    }

    public class CommandOperationException : CommandRegistryException
    {
        public CommandOperationException(string message) : base(message) { }
        public CommandOperationException(string message, Exception inner) : base(message, inner) { }
    }

    public class CommandDiscoveryException : CommandRegistryException
    {
        public CommandDiscoveryException(string message) : base(message) { }
        public CommandDiscoveryException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion

    /// <summary>
    /// Interface for command registry
    /// </summary>
    public interface ICommandRegistry
    {
        Task InitializeAsync();
        Task RegisterCommandAsync<TCommand>() where TCommand : ICommand, new();
        Task RegisterCommandAsync(ICommand command);
        Task<bool> UnregisterCommandAsync(string commandName);
        Task<CommandExecutionResult> ExecuteCommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null,
            string userId = null,
            bool skipPermissionCheck = false);
        Task<IEnumerable<CommandMetadata>> GetAllCommandsAsync();
        Task<IEnumerable<CommandMetadata>> GetCommandsByCategoryAsync(string category);
        Task<IEnumerable<CommandMetadata>> SearchCommandsAsync(string searchTerm);
        Task<CommandMetadata> GetCommandMetadataAsync(string commandName);
        Task<bool> CommandExistsAsync(string commandName);
        Task<IEnumerable<string>> GetCategoriesAsync();
        Task<int> DiscoverCommandsAsync(Assembly assembly);
        Task ClearCommandsAsync();
        Task<Dictionary<string, object>> GetStatisticsAsync();
    }
}
