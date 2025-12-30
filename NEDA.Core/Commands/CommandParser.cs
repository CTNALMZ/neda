using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Commands.Interfaces;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Common.Extensions;

namespace NEDA.Core.Commands
{
    /// <summary>
    /// Parses and validates user commands with support for natural language and structured input;
    /// </summary>
    public class CommandParser : ICommandParser
    {
        private readonly ILogger<CommandParser> _logger;
        private readonly ICommandRegistry _commandRegistry;
        private readonly IErrorHandler _errorHandler;

        // Command patterns and regex definitions;
        private static readonly Dictionary<CommandType, Regex> CommandPatterns = new()
        {
            { CommandType.NaturalLanguage, new Regex(@"^(?<verb>\w+)(?:\s+(?<parameters>.+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase) },
            { CommandType.Structured, new Regex(@"^(?<command>/[\w-]+)(?:\s+(?<args>.*))?$", RegexOptions.Compiled) },
            { CommandType.Script, new Regex(@"^@(?<script>\w+)(?:\((?<params>[^)]*)\))?$", RegexOptions.Compiled) }
        };

        private static readonly Regex ParameterExtractor = new(
            @"(?:(?<key>[a-zA-Z_][\w-]*)\s*[:=]\s*)?(?<value>(?:""[^""]*""|'[^']*'|[^\s""']+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );

        /// <summary>
        /// Initializes a new instance of CommandParser;
        /// </summary>
        public CommandParser(
            ILogger<CommandParser> logger,
            ICommandRegistry commandRegistry,
            IErrorHandler errorHandler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));

            _logger.LogInformation("CommandParser initialized");
        }

        /// <summary>
        /// Parses raw input string into structured command object;
        /// </summary>
        /// <param name="input">Raw user input</param>
        /// <param name="context">Parsing context including user, session, etc.</param>
        /// <returns>Parsed command object</returns>
        public async Task<ParsedCommand> ParseAsync(string input, CommandContext context)
        {
            try
            {
                _logger.LogDebug("Parsing command input: {Input}", input.SanitizeForLog());

                if (string.IsNullOrWhiteSpace(input))
                {
                    throw new CommandParsingException(
                        "Input cannot be empty",
                        ErrorCodes.Command.EmptyInput,
                        SeverityLevel.Warning
                    );
                }

                // Normalize input;
                var normalizedInput = NormalizeInput(input);

                // Detect command type;
                var commandType = DetectCommandType(normalizedInput);

                // Parse based on type;
                ParsedCommand parsedCommand = commandType switch
                {
                    CommandType.NaturalLanguage => await ParseNaturalLanguageAsync(normalizedInput, context),
                    CommandType.Structured => await ParseStructuredCommandAsync(normalizedInput, context),
                    CommandType.Script => await ParseScriptCommandAsync(normalizedInput, context),
                    _ => throw new CommandParsingException(
                        $"Unsupported command type: {commandType}",
                        ErrorCodes.Command.UnsupportedType,
                        SeverityLevel.Error
                    )
                };

                // Validate parsed command;
                await ValidateParsedCommandAsync(parsedCommand, context);

                _logger.LogInformation("Successfully parsed command: {CommandName} for user: {UserId}",
                    parsedCommand.Name, context.UserId);

                return parsedCommand;
            }
            catch (CommandParsingException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse command: {Input}", input.SanitizeForLog());
                await _errorHandler.HandleErrorAsync(ex, "CommandParser.ParseAsync");

                throw new CommandParsingException(
                    $"Failed to parse command: {ex.Message}",
                    ErrorCodes.Command.ParseFailed,
                    SeverityLevel.Error,
                    ex
                );
            }
        }

        /// <summary>
        /// Parses natural language commands using NLP techniques;
        /// </summary>
        private async Task<ParsedCommand> ParseNaturalLanguageAsync(string input, CommandContext context)
        {
            _logger.LogDebug("Parsing natural language command: {Input}", input.SanitizeForLog());

            var match = CommandPatterns[CommandType.NaturalLanguage].Match(input);
            if (!match.Success)
            {
                throw new CommandParsingException(
                    "Invalid natural language command format",
                    ErrorCodes.Command.InvalidFormat,
                    SeverityLevel.Warning
                );
            }

            var verb = match.Groups["verb"].Value.Trim().ToLowerInvariant();
            var parametersText = match.Groups["parameters"].Value;

            // Extract command name from verb (could use AI/NLP here)
            var commandName = await ResolveCommandFromVerbAsync(verb, context);

            // Parse parameters;
            var parameters = ParseParameters(parametersText);

            // Extract flags and options;
            var (parsedParams, flags) = ExtractFlagsAndOptions(parameters);

            return new ParsedCommand
            {
                Name = commandName,
                OriginalInput = input,
                Parameters = parsedParams,
                Flags = flags,
                Type = CommandType.NaturalLanguage,
                Context = context,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Parses structured commands (e.g., /command param1 param2)
        /// </summary>
        private async Task<ParsedCommand> ParseStructuredCommandAsync(string input, CommandContext context)
        {
            _logger.LogDebug("Parsing structured command: {Input}", input.SanitizeForLog());

            var match = CommandPatterns[CommandType.Structured].Match(input);
            if (!match.Success)
            {
                throw new CommandParsingException(
                    "Invalid structured command format",
                    ErrorCodes.Command.InvalidFormat,
                    SeverityLevel.Warning
                );
            }

            var commandName = match.Groups["command"].Value.TrimStart('/').ToLowerInvariant();
            var argsText = match.Groups["args"].Value;

            // Validate command exists;
            if (!await _commandRegistry.CommandExistsAsync(commandName))
            {
                throw new CommandParsingException(
                    $"Command '{commandName}' not found",
                    ErrorCodes.Command.NotFound,
                    SeverityLevel.Warning
                );
            }

            // Parse arguments;
            var parameters = ParseArguments(argsText);
            var (parsedParams, flags) = ExtractFlagsAndOptions(parameters);

            return new ParsedCommand
            {
                Name = commandName,
                OriginalInput = input,
                Parameters = parsedParams,
                Flags = flags,
                Type = CommandType.Structured,
                Context = context,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Parses script commands (e.g., @script(param1, param2))
        /// </summary>
        private async Task<ParsedCommand> ParseScriptCommandAsync(string input, CommandContext context)
        {
            _logger.LogDebug("Parsing script command: {Input}", input.SanitizeForLog());

            var match = CommandPatterns[CommandType.Script].Match(input);
            if (!match.Success)
            {
                throw new CommandParsingException(
                    "Invalid script command format",
                    ErrorCodes.Command.InvalidFormat,
                    SeverityLevel.Warning
                );
            }

            var scriptName = match.Groups["script"].Value;
            var paramsText = match.Groups["params"].Value;

            // Parse parameters from function-like syntax;
            var parameters = ParseScriptParameters(paramsText);

            // Get command metadata for script;
            var commandMetadata = await _commandRegistry.GetCommandMetadataAsync(scriptName);
            if (commandMetadata == null)
            {
                throw new CommandParsingException(
                    $"Script '{scriptName}' not registered",
                    ErrorCodes.Command.NotFound,
                    SeverityLevel.Error
                );
            }

            // Validate parameter count;
            if (commandMetadata.MinParameters > 0 &&
                parameters.Count < commandMetadata.MinParameters)
            {
                throw new CommandParsingException(
                    $"Script '{scriptName}' requires at least {commandMetadata.MinParameters} parameters",
                    ErrorCodes.Command.InsufficientParameters,
                    SeverityLevel.Warning
                );
            }

            return new ParsedCommand
            {
                Name = scriptName,
                OriginalInput = input,
                Parameters = parameters,
                Flags = new Dictionary<string, string>(),
                Type = CommandType.Script,
                Context = context,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Parses key-value parameters from text;
        /// </summary>
        private Dictionary<string, string> ParseParameters(string parametersText)
        {
            if (string.IsNullOrWhiteSpace(parametersText))
                return new Dictionary<string, string>();

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var matches = ParameterExtractor.Matches(parametersText);

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var key = match.Groups["key"].Success
                    ? match.Groups["key"].Value.Trim()
                    : $"param{i + 1}";

                var value = match.Groups["value"].Value.Trim().Trim('"', '\'');

                parameters[key] = value;
            }

            return parameters;
        }

        /// <summary>
        /// Parses positional arguments;
        /// </summary>
        private Dictionary<string, string> ParseArguments(string argsText)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(argsText))
                return parameters;

            var args = SplitArguments(argsText);
            for (int i = 0; i < args.Length; i++)
            {
                parameters[$"param{i + 1}"] = args[i];
            }

            return parameters;
        }

        /// <summary>
        /// Parses script parameters from function syntax;
        /// </summary>
        private Dictionary<string, string> ParseScriptParameters(string paramsText)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(paramsText))
                return parameters;

            var paramList = paramsText.Split(',')
                .Select(p => p.Trim().Trim('"', '\''))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            for (int i = 0; i < paramList.Count; i++)
            {
                parameters[$"arg{i + 1}"] = paramList[i];
            }

            return parameters;
        }

        /// <summary>
        /// Extracts flags (e.g., --verbose, -v) from parameters;
        /// </summary>
        private (Dictionary<string, string> Parameters, Dictionary<string, string> Flags)
            ExtractFlagsAndOptions(Dictionary<string, string> allParams)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in allParams)
            {
                if (kvp.Key.StartsWith("--") || kvp.Key.StartsWith("-"))
                {
                    var flagName = kvp.Key.TrimStart('-');
                    flags[flagName] = kvp.Value;
                }
                else
                {
                    parameters[kvp.Key] = kvp.Value;
                }
            }

            return (parameters, flags);
        }

        /// <summary>
        /// Normalizes input by removing extra spaces and standardizing quotes;
        /// </summary>
        private string NormalizeInput(string input)
        {
            return Regex.Replace(input.Trim(), @"\s+", " ");
        }

        /// <summary>
        /// Detects the type of command based on input pattern;
        /// </summary>
        private CommandType DetectCommandType(string input)
        {
            if (input.StartsWith("/"))
                return CommandType.Structured;

            if (input.StartsWith("@"))
                return CommandType.Script;

            return CommandType.NaturalLanguage;
        }

        /// <summary>
        /// Resolves command name from natural language verb;
        /// </summary>
        private async Task<string> ResolveCommandFromVerbAsync(string verb, CommandContext context)
        {
            // First, check if verb is a direct command;
            if (await _commandRegistry.CommandExistsAsync(verb))
                return verb;

            // Try to find synonyms or similar commands;
            var synonyms = await _commandRegistry.GetCommandSynonymsAsync(verb);
            if (synonyms.Any())
                return synonyms.First();

            // Use AI/NLP to resolve intent (simplified for now)
            var resolvedCommand = await ResolveIntentAsync(verb, context);
            if (!string.IsNullOrEmpty(resolvedCommand))
                return resolvedCommand;

            throw new CommandParsingException(
                $"Cannot resolve command from verb: {verb}",
                ErrorCodes.Command.UnresolvedVerb,
                SeverityLevel.Warning
            );
        }

        /// <summary>
        /// Resolves user intent using AI/NLP (placeholder for actual implementation)
        /// </summary>
        private async Task<string?> ResolveIntentAsync(string verb, CommandContext context)
        {
            // This would integrate with NEDA.Brain.NLP_Engine in real implementation;
            // For now, return a simple mapping;
            var intentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["create"] = "new",
                ["make"] = "new",
                ["build"] = "new",
                ["generate"] = "new",
                ["delete"] = "remove",
                ["remove"] = "delete",
                ["destroy"] = "delete",
                ["show"] = "display",
                ["display"] = "show",
                ["list"] = "show",
                ["run"] = "execute",
                ["execute"] = "run",
                ["start"] = "run",
                ["stop"] = "terminate",
                ["end"] = "terminate",
                ["kill"] = "terminate",
                ["help"] = "assist",
                ["assist"] = "help",
                ["support"] = "help"
            };

            await Task.CompletedTask; // Async placeholder;

            return intentMap.TryGetValue(verb, out var command) ? command : null;
        }

        /// <summary>
        /// Splits arguments while respecting quoted strings;
        /// </summary>
        private string[] SplitArguments(string argsText)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            char quoteChar = '\0';

            for (int i = 0; i < argsText.Length; i++)
            {
                char c = argsText[i];

                if (c == '"' || c == '\'')
                {
                    if (!inQuotes)
                    {
                        inQuotes = true;
                        quoteChar = c;
                    }
                    else if (c == quoteChar)
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                args.Add(current.ToString());
            }

            return args.ToArray();
        }

        /// <summary>
        /// Validates parsed command against registry and context;
        /// </summary>
        private async Task ValidateParsedCommandAsync(ParsedCommand command, CommandContext context)
        {
            var metadata = await _commandRegistry.GetCommandMetadataAsync(command.Name);

            if (metadata == null)
            {
                throw new CommandParsingException(
                    $"Command '{command.Name}' is not registered",
                    ErrorCodes.Command.NotFound,
                    SeverityLevel.Error
                );
            }

            // Check permissions;
            if (!await _commandRegistry.HasPermissionAsync(command.Name, context.UserId, context.UserRole))
            {
                throw new CommandParsingException(
                    $"User does not have permission to execute '{command.Name}'",
                    ErrorCodes.Command.PermissionDenied,
                    SeverityLevel.Error
                );
            }

            // Validate required parameters;
            foreach (var requiredParam in metadata.RequiredParameters)
            {
                if (!command.Parameters.ContainsKey(requiredParam))
                {
                    throw new CommandParsingException(
                        $"Required parameter '{requiredParam}' is missing for command '{command.Name}'",
                        ErrorCodes.Command.MissingParameter,
                        SeverityLevel.Warning
                    );
                }
            }

            // Validate parameter types if specified;
            foreach (var param in command.Parameters)
            {
                if (metadata.ParameterTypes.TryGetValue(param.Key, out var expectedType))
                {
                    if (!IsValidParameterType(param.Value, expectedType))
                    {
                        throw new CommandParsingException(
                            $"Parameter '{param.Key}' has invalid type. Expected: {expectedType}",
                            ErrorCodes.Command.InvalidParameterType,
                            SeverityLevel.Warning
                        );
                    }
                }
            }

            // Validate command is enabled;
            if (!metadata.IsEnabled)
            {
                throw new CommandParsingException(
                    $"Command '{command.Name}' is currently disabled",
                    ErrorCodes.Command.Disabled,
                    SeverityLevel.Warning
                );
            }
        }

        /// <summary>
        /// Validates parameter value against expected type;
        /// </summary>
        private bool IsValidParameterType(string value, string expectedType)
        {
            return expectedType.ToLowerInvariant() switch
            {
                "string" => true, // Everything can be string;
                "int" => int.TryParse(value, out _),
                "float" => float.TryParse(value, out _),
                "double" => double.TryParse(value, out _),
                "bool" => bool.TryParse(value, out _),
                "datetime" => DateTime.TryParse(value, out _),
                "guid" => Guid.TryParse(value, out _),
                "email" => Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
                "url" => Uri.TryCreate(value, UriKind.Absolute, out _),
                _ => true // Unknown type, accept anyway;
            };
        }

        /// <summary>
        /// Extracts parameters from command input for specific command;
        /// </summary>
        public async Task<Dictionary<string, object>> ExtractParametersAsync(
            string commandName,
            string input,
            CommandContext context)
        {
            try
            {
                var parsedCommand = await ParseAsync(input, context);

                if (!parsedCommand.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CommandParsingException(
                        $"Input does not match expected command '{commandName}'",
                        ErrorCodes.Command.Mismatch,
                        SeverityLevel.Warning
                    );
                }

                // Convert string parameters to typed objects based on command metadata;
                var metadata = await _commandRegistry.GetCommandMetadataAsync(commandName);
                var typedParameters = new Dictionary<string, object>();

                if (metadata == null)
                {
                    return parsedCommand.Parameters.ToDictionary(k => k.Key, v => (object)v.Value);
                }

                foreach (var param in parsedCommand.Parameters)
                {
                    if (metadata.ParameterTypes.TryGetValue(param.Key, out var typeName))
                    {
                        typedParameters[param.Key] = ConvertParameter(param.Value, typeName);
                    }
                    else
                    {
                        typedParameters[param.Key] = param.Value;
                    }
                }

                return typedParameters;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract parameters for command: {CommandName}", commandName);
                throw;
            }
        }

        /// <summary>
        /// Converts string parameter to specified type;
        /// </summary>
        private object ConvertParameter(string value, string typeName)
        {
            return typeName.ToLowerInvariant() switch
            {
                "int" => int.Parse(value),
                "float" => float.Parse(value),
                "double" => double.Parse(value),
                "bool" => bool.Parse(value),
                "datetime" => DateTime.Parse(value),
                "guid" => Guid.Parse(value),
                _ => value
            };
        }

        /// <summary>
        /// Provides suggestions for command completion;
        /// </summary>
        public async Task<IEnumerable<string>> GetCommandSuggestionsAsync(
            string partialInput,
            CommandContext context)
        {
            try
            {
                var normalized = partialInput.Trim();
                var commands = await _commandRegistry.GetAvailableCommandsAsync(context.UserId, context.UserRole);

                // Filter commands based on partial input;
                var suggestions = commands
                    .Where(c => c.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c)
                    .Take(10) // Limit suggestions;
                    .ToList();

                // Also suggest based on synonyms or similar terms;
                if (suggestions.Count < 5)
                {
                    var similar = await _commandRegistry.FindSimilarCommandsAsync(normalized);
                    suggestions.AddRange(similar.Where(s => !suggestions.Contains(s)));
                }

                return suggestions.Distinct();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get command suggestions for: {Input}", partialInput.SanitizeForLog());
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            // Clean up any resources if needed;
            _logger.LogInformation("CommandParser disposed");
        }
    }

    /// <summary>
    /// Represents a parsed command with all metadata;
    /// </summary>
    public class ParsedCommand
    {
        public string Name { get; set; }
        public string OriginalInput { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new();
        public Dictionary<string, string> Flags { get; set; } = new();
        public CommandType Type { get; set; }
        public CommandContext Context { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Command execution context;
    /// </summary>
    public class CommandContext
    {
        public string UserId { get; set; }
        public string UserRole { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> SessionData { get; set; } = new();
        public string WorkingDirectory { get; set; }
        public string Environment { get; set; }
        public string Language { get; set; } = "en-US";
        public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Types of commands supported;
    /// </summary>
    public enum CommandType
    {
        NaturalLanguage,
        Structured,
        Script
    }
}

namespace NEDA.Core.Commands.Interfaces
{
    /// <summary>
    /// Interface for command parsing functionality;
    /// </summary>
    public interface ICommandParser : IDisposable
    {
        Task<ParsedCommand> ParseAsync(string input, CommandContext context);
        Task<Dictionary<string, object>> ExtractParametersAsync(string commandName, string input, CommandContext context);
        Task<IEnumerable<string>> GetCommandSuggestionsAsync(string partialInput, CommandContext context);
    }

    /// <summary>
    /// Interface for command registry
    /// </summary>
    public interface ICommandRegistry
    {
        Task<bool> CommandExistsAsync(string commandName);
        Task<CommandMetadata?> GetCommandMetadataAsync(string commandName);
        Task<bool> HasPermissionAsync(string commandName, string userId, string userRole);
        Task<IEnumerable<string>> GetAvailableCommandsAsync(string userId, string userRole);
        Task<IEnumerable<string>> GetCommandSynonymsAsync(string verb);
        Task<IEnumerable<string>> FindSimilarCommandsAsync(string searchTerm);
    }

    /// <summary>
    /// Command metadata;
    /// </summary>
    public class CommandMetadata
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public IEnumerable<string> RequiredParameters { get; set; } = new List<string>();
        public Dictionary<string, string> ParameterTypes { get; set; } = new();
        public int RequiredParameterCount => RequiredParameters?.Count() ?? 0;
        public bool IsEnabled { get; set; } = true;
        public IEnumerable<string> AllowedRoles { get; set; } = new List<string>();
        public int MinParameters { get; set; }
        public int MaxParameters { get; set; }
        public string ReturnType { get; set; }
        public IEnumerable<string> Aliases { get; set; } = new List<string>();
        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
        public string Version { get; set; }
    }
}

namespace NEDA.Core.ExceptionHandling
{
    /// <summary>
    /// Error handler interface;
    /// </summary>
    public interface IErrorHandler
    {
        Task HandleErrorAsync(Exception exception, string context);
    }

    /// <summary>
    /// Error codes for command parsing;
    /// </summary>
    public static class ErrorCodes
    {
        public static class Command
        {
            public const string EmptyInput = "CMD_001";
            public const string InvalidFormat = "CMD_002";
            public const string UnsupportedType = "CMD_003";
            public const string ParseFailed = "CMD_004";
            public const string NotFound = "CMD_005";
            public const string PermissionDenied = "CMD_006";
            public const string MissingParameter = "CMD_007";
            public const string InvalidParameterType = "CMD_008";
            public const string Disabled = "CMD_009";
            public const string UnresolvedVerb = "CMD_010";
            public const string InsufficientParameters = "CMD_011";
            public const string Mismatch = "CMD_012";
        }
    }

    /// <summary>
    /// Severity levels for errors;
    /// </summary>
    public enum SeverityLevel
    {
        Information,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Exception thrown when command parsing fails;
    /// </summary>
    public class CommandParsingException : Exception
    {
        public string ErrorCode { get; }
        public SeverityLevel Severity { get; }

        public CommandParsingException(string message, string errorCode, SeverityLevel severity)
            : base(message)
        {
            ErrorCode = errorCode;
            Severity = severity;
        }

        public CommandParsingException(string message, string errorCode, SeverityLevel severity, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Severity = severity;
        }
    }
}

namespace NEDA.Core.Logging
{
    /// <summary>
    /// Logging extension methods;
    /// </summary>
    public static class LoggingExtensions
    {
        public static string SanitizeForLog(this string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Remove sensitive data like passwords, tokens, etc.
            var sanitized = Regex.Replace(input,
                @"(password|token|key|secret|auth)=[^&\s]+",
                "$1=***",
                RegexOptions.IgnoreCase);

            // Limit length for logs;
            return sanitized.Length > 500
                ? sanitized.Substring(0, 500) + "..."
                : sanitized;
        }
    }
}

namespace NEDA.Core.Common.Extensions
{
    /// <summary>
    /// Common string extensions;
    /// </summary>
    public static class StringExtensions
    {
        public static bool Contains(this string source, string toCheck, StringComparison comp)
        {
            return source?.IndexOf(toCheck, comp) >= 0;
        }
    }
}
