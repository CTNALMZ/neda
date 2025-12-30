using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NEDA.Core.Commands
{
    /// <summary>
    /// Executes registered commands using the command registry.
    /// </summary>
    public interface ICommandExecutor
    {
        Task<CommandResult> ExecuteAsync(
            string commandName,
            IDictionary<string, object>? arguments = null,
            CancellationToken cancellationToken = default);

        Task<CommandResult> ExecuteAsync(
            ParsedCommand command,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Default implementation of ICommandExecutor.
    /// </summary>
    public sealed class CommandExecutor : ICommandExecutor
    {
        private readonly ILogger<CommandExecutor> _logger;
        private readonly ICommandRegistry _registry;

        public CommandExecutor(
            ILogger<CommandExecutor> logger,
            ICommandRegistry registry)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// Executes a command using its name and argument dictionary.
        /// </summary>
        public Task<CommandResult> ExecuteAsync(
            string commandName,
            IDictionary<string, object>? arguments = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(commandName))
            {
                throw new ArgumentException("Command name cannot be null or empty.", nameof(commandName));
            }

            var parsed = new ParsedCommand(
                commandName,
                arguments ?? new Dictionary<string, object>());

            return ExecuteAsync(parsed, cancellationToken);
        }

        /// <summary>
        /// Executes a parsed command.
        /// </summary>
        public async Task<CommandResult> ExecuteAsync(
            ParsedCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            if (!_registry.TryGetCommand(command.Name, out var handler))
            {
                _logger.LogWarning("Unknown command: {CommandName}", command.Name);
                return CommandResult.Failed($"Unknown command: {command.Name}");
            }

            try
            {
                _logger.LogDebug("Executing command {CommandName}", command.Name);

                var context = new CommandContext(command.Name, command.Arguments);

                var result = await handler.ExecuteAsync(context, cancellationToken)
                                          .ConfigureAwait(false);

                _logger.LogInformation(
                    "Command {CommandName} completed. Success={Success}",
                    command.Name,
                    result.Success);

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Command {CommandName} was cancelled.", command.Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command {CommandName} failed.", command.Name);
                return CommandResult.FromException(ex, $"Command '{command.Name}' failed.");
            }
        }
    }

    /// <summary>
    /// Represents a command that can be executed.
    /// </summary>
    public interface ICommand
    {
        string Name { get; }
        string Description { get; }

        Task<CommandResult> ExecuteAsync(
            CommandContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Registry that stores and resolves commands.
    /// </summary>
    public interface ICommandRegistry
    {
        bool TryGetCommand(string name, out ICommand command);
        IEnumerable<ICommand> GetAll();
    }

    /// <summary>
    /// Parsed representation of a command.
    /// Usually produced by CommandParser.
    /// </summary>
    public sealed class ParsedCommand
    {
        public string Name { get; }
        public IDictionary<string, object> Arguments { get; }

        public ParsedCommand(
            string name,
            IDictionary<string, object> arguments)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        }
    }

    /// <summary>
    /// Context passed to command handlers during execution.
    /// </summary>
    public sealed class CommandContext
    {
        public string Name { get; }
        public IDictionary<string, object> Arguments { get; }

        public CommandContext(
            string name,
            IDictionary<string, object> arguments)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        }

        public T GetArgument<T>(string key, T defaultValue = default!)
        {
            if (Arguments.TryGetValue(key, out var value) && value is T typed)
            {
                return typed;
            }

            return defaultValue;
        }
    }

    /// <summary>
    /// Result of a command execution.
    /// </summary>
    public sealed class CommandResult
    {
        public bool Success { get; }
        public string? Message { get; }
        public object? Data { get; }
        public Exception? Error { get; }

        private CommandResult(bool success, string? message, object? data, Exception? error)
        {
            Success = success;
            Message = message;
            Data = data;
            Error = error;
        }

        public static CommandResult Ok(string? message = null, object? data = null)
        {
            return new CommandResult(true, message, data, null);
        }

        public static CommandResult Failed(string message, Exception? error = null, object? data = null)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                message = "Command execution failed.";
            }

            return new CommandResult(false, message, data, error);
        }

        public static CommandResult FromException(Exception ex, string? message = null)
        {
            if (ex == null) throw new ArgumentNullException(nameof(ex));

            return new CommandResult(
                success: false,
                message: message ?? ex.Message,
                data: null,
                error: ex);
        }
    }
}
