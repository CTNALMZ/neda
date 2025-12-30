using NEDA.Brain.NLP_Engine.SyntaxAnalysis;
using NEDA.Commands.CommandRegistry
using NEDA.Core.Commands;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NEDA.Commands;
{
    /// <summary>
    /// Advanced console command parser with natural language support;
    /// Supports complex command structures, pipes, redirections, and scripting;
    /// </summary>
    public class ConsoleParser : IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly CommandRegistry _commandRegistry
        private readonly Parser _syntaxParser;
        private readonly ConsoleParserConfiguration _configuration;
        private readonly CommandHistory _commandHistory;
        private readonly Dictionary<string, object> _environmentVariables;
        private bool _isInitialized;

        public event EventHandler<CommandParsedEventArgs> CommandParsed;
        public event EventHandler<ParseErrorEventArgs> ParseError;
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;

        public int MaxHistorySize { get; set; } = 1000;
        public bool EnableSyntaxHighlighting { get; set; } = true;
        public bool EnableAutoCompletion { get; set; } = true;
        public bool EnableHistorySearch { get; set; } = true;

        #endregion;

        #region Configuration Classes;

        public class ConsoleParserConfiguration;
        {
            public bool StrictParsing { get; set; } = false;
            public bool EnableWildcards { get; set; } = true;
            public bool EnableVariables { get; set; } = true;
            public bool EnablePipes { get; set; } = true;
            public bool EnableRedirections { get; set; } = true;
            public bool EnableCommandSubstitution { get; set; } = true;
            public bool EnableTabCompletion { get; set; } = true;
            public bool EnableSyntaxValidation { get; set; } = true;
            public char[] ArgumentDelimiters { get; set; } = { ' ', '\t', '\n', '\r' };
            public char[] QuoteCharacters { get; set; } = { '\"', '\'' };
            public char PipeCharacter { get; set; } = '|';
            public char RedirectionCharacter { get; set; } = '>';
            public string Prompt { get; set; } = "NEDA> ";
            public ConsoleColor PromptColor { get; set; } = ConsoleColor.Cyan;
            public ConsoleColor ErrorColor { get; set; } = ConsoleColor.Red;
            public ConsoleColor SuccessColor { get; set; } = ConsoleColor.Green;
            public ConsoleColor WarningColor { get; set; } = ConsoleColor.Yellow;
        }

        public enum ParseMode;
        {
            Standard,
            Interactive,
            Script,
            NaturalLanguage;
        }

        public enum TokenType;
        {
            Command,
            Argument,
            Option,
            Flag,
            StringLiteral,
            Number,
            Variable,
            Pipe,
            Redirection,
            Comment,
            Operator,
            Separator;
        }

        #endregion;

        #region Event Args Classes;

        public class CommandParsedEventArgs : EventArgs;
        {
            public ParsedCommand Command { get; }
            public DateTime Timestamp { get; }
            public ParseMode Mode { get; }
            public TimeSpan ParseTime { get; }

            public CommandParsedEventArgs(ParsedCommand command, DateTime timestamp,
                ParseMode mode, TimeSpan parseTime)
            {
                Command = command;
                Timestamp = timestamp;
                Mode = mode;
                ParseTime = parseTime;
            }
        }

        public class ParseErrorEventArgs : EventArgs;
        {
            public string Input { get; }
            public string ErrorMessage { get; }
            public ParseErrorType ErrorType { get; }
            public Exception Exception { get; }

            public ParseErrorEventArgs(string input, string errorMessage,
                ParseErrorType errorType, Exception exception = null)
            {
                Input = input;
                ErrorMessage = errorMessage;
                ErrorType = errorType;
                Exception = exception;
            }
        }

        public class CommandExecutedEventArgs : EventArgs;
        {
            public ParsedCommand Command { get; }
            public DateTime StartTime { get; }
            public DateTime EndTime { get; }
            public TimeSpan ExecutionTime => EndTime - StartTime;
            public object Result { get; }
            public bool Success { get; }
            public string Output { get; }

            public CommandExecutedEventArgs(ParsedCommand command, DateTime startTime,
                DateTime endTime, object result, bool success, string output)
            {
                Command = command;
                StartTime = startTime;
                EndTime = endTime;
                Result = result;
                Success = success;
                Output = output;
            }
        }

        public enum ParseErrorType;
        {
            SyntaxError,
            UnknownCommand,
            InvalidArgument,
            MissingArgument,
            PermissionDenied,
            RuntimeError;
        }

        #endregion;

        #region Data Structures;

        public class ParsedCommand;
        {
            public string OriginalInput { get; set; }
            public string CommandName { get; set; }
            public List<string> Arguments { get; set; } = new List<string>();
            public Dictionary<string, string> Options { get; set; } = new Dictionary<string, string>();
            public HashSet<string> Flags { get; set; } = new HashSet<string>();
            public List<Token> Tokens { get; set; } = new List<Token>();
            public ParseMode Mode { get; set; }
            public string WorkingDirectory { get; set; }
            public Dictionary<string, string> Environment { get; set; }
            public List<ParsedCommand> PipedCommands { get; set; } = new List<ParsedCommand>();
            public RedirectionInfo Redirection { get; set; }
            public bool IsBackground { get; set; }
            public int CommandId { get; set; }

            public override string ToString()
            {
                return $"{CommandName} {string.Join(" ", Arguments)}";
            }
        }

        public class Token;
        {
            public string Value { get; set; }
            public TokenType Type { get; set; }
            public int Position { get; set; }
            public int Length { get; set; }
            public bool IsQuoted { get; set; }
            public string OriginalText { get; set; }
        }

        public class RedirectionInfo;
        {
            public RedirectionType Type { get; set; }
            public string Target { get; set; }
            public bool Append { get; set; }
        }

        public enum RedirectionType;
        {
            Output,
            Error,
            Input,
            OutputAndError;
        }

        public class CommandHistory;
        {
            private readonly List<string> _history = new List<string>();
            private readonly object _lock = new object();
            private int _currentIndex = -1;

            public int MaxSize { get; set; } = 1000;

            public void Add(string command)
            {
                lock (_lock)
                {
                    if (!string.IsNullOrWhiteSpace(command) &&
                        (_history.Count == 0 || _history.Last() != command))
                    {
                        _history.Add(command);
                        if (_history.Count > MaxSize)
                        {
                            _history.RemoveAt(0);
                        }
                    }
                    _currentIndex = _history.Count;
                }
            }

            public string GetPrevious()
            {
                lock (_lock)
                {
                    if (_history.Count == 0) return null;
                    _currentIndex = Math.Max(0, _currentIndex - 1);
                    return _history[_currentIndex];
                }
            }

            public string GetNext()
            {
                lock (_lock)
                {
                    if (_history.Count == 0) return null;
                    _currentIndex = Math.Min(_history.Count, _currentIndex + 1);
                    return _currentIndex < _history.Count ? _history[_currentIndex] : "";
                }
            }

            public void Clear()
            {
                lock (_lock)
                {
                    _history.Clear();
                    _currentIndex = -1;
                }
            }

            public IReadOnlyList<string> GetAll() => _history.AsReadOnly();

            public bool Search(string pattern, out string match)
            {
                lock (_lock)
                {
                    for (int i = _history.Count - 1; i >= 0; i--)
                    {
                        if (_history[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            match = _history[i];
                            return true;
                        }
                    }
                    match = null;
                    return false;
                }
            }
        }

        #endregion;

        #region Constructor and Initialization;

        public ConsoleParser(ILogger logger = null,
            ConsoleParserConfiguration configuration = null,
            CommandRegistry commandRegistry = null)
        {
            _logger = logger ?? new DefaultLogger();
            _configuration = configuration ?? new ConsoleParserConfiguration();
            _commandRegistry = commandRegistry ?? new CommandRegistry();

            try
            {
                _syntaxParser = new Parser();
                _commandHistory = new CommandHistory { MaxSize = MaxHistorySize };
                _environmentVariables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                InitializeEnvironmentVariables();
                InitializeBuiltInCommands();

                _isInitialized = true;
                _logger.LogInformation("ConsoleParser initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize ConsoleParser: {ex.Message}", ex);
                throw new ParserInitializationException("Failed to initialize ConsoleParser", ex);
            }
        }

        private void InitializeEnvironmentVariables()
        {
            // System variables;
            _environmentVariables["NEDA_VERSION"] = "1.0.0";
            _environmentVariables["NEDA_HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _environmentVariables["OS"] = Environment.OSVersion.Platform.ToString();
            _environmentVariables["USER"] = Environment.UserName;
            _environmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH");
            _environmentVariables["PWD"] = Environment.CurrentDirectory;

            // Parser variables;
            _environmentVariables["PROMPT"] = _configuration.Prompt;
            _environmentVariables["MAX_HISTORY"] = MaxHistorySize;
        }

        private void InitializeBuiltInCommands()
        {
            // Register internal parser commands;
            _commandRegistry.RegisterCommand("help", "Display help information",
                args => DisplayHelp(args.Length > 0 ? args[0] : null));

            _commandRegistry.RegisterCommand("clear", "Clear the console",
                args => Console.Clear());

            _commandRegistry.RegisterCommand("history", "Display command history",
                args => DisplayHistory());

            _commandRegistry.RegisterCommand("echo", "Display a message",
                args => Console.WriteLine(string.Join(" ", args)));

            _commandRegistry.RegisterCommand("set", "Set environment variable",
                args => SetEnvironmentVariable(args));

            _commandRegistry.RegisterCommand("exit", "Exit the console",
                args => Environment.Exit(0));
        }

        #endregion;

        #region Public Methods;

        public ParsedCommand Parse(string input, ParseMode mode = ParseMode.Standard)
        {
            ValidateParserState();

            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Input cannot be null or empty", nameof(input));

            var startTime = DateTime.UtcNow;

            try
            {
                // Add to history (for interactive mode)
                if (mode == ParseMode.Interactive)
                {
                    _commandHistory.Add(input);
                }

                // Pre-process input;
                input = PreprocessInput(input, mode);

                // Tokenize;
                var tokens = Tokenize(input);

                // Parse command structure;
                var parsedCommand = ParseTokens(tokens, mode);
                parsedCommand.OriginalInput = input;
                parsedCommand.Mode = mode;
                parsedCommand.WorkingDirectory = Environment.CurrentDirectory;
                parsedCommand.Environment = new Dictionary<string, string>(
                    _environmentVariables.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));

                // Validate command;
                if (_configuration.EnableSyntaxValidation)
                {
                    ValidateCommand(parsedCommand);
                }

                var parseTime = DateTime.UtcNow - startTime;

                // Raise event;
                CommandParsed?.Invoke(this, new CommandParsedEventArgs(
                    parsedCommand, DateTime.UtcNow, mode, parseTime));

                _logger.LogDebug($"Parsed command: {parsedCommand.CommandName} " +
                               $"(tokens: {tokens.Count}, time: {parseTime.TotalMilliseconds}ms)");

                return parsedCommand;
            }
            catch (Exception ex)
            {
                var errorType = GetErrorType(ex);
                var errorMessage = $"Parse error: {ex.Message}";

                ParseError?.Invoke(this, new ParseErrorEventArgs(
                    input, errorMessage, errorType, ex));

                _logger.LogError($"Parse failed for input: '{input}': {ex.Message}", ex);

                if (_configuration.StrictParsing)
                {
                    throw new CommandParseException($"Failed to parse command: {input}", ex);
                }

                // Return a failed command for non-strict mode;
                return new ParsedCommand;
                {
                    OriginalInput = input,
                    CommandName = "error",
                    Arguments = new List<string> { ex.Message },
                    Mode = mode;
                };
            }
        }

        public async Task<ParsedCommand> ParseNaturalLanguageAsync(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Input cannot be null or empty", nameof(input));

            try
            {
                // Use NLP engine to parse natural language;
                var nlpResult = await _syntaxParser.ParseAsync(input);

                // Convert NLP result to command structure;
                var command = ConvertNlpToCommand(nlpResult);
                command.Mode = ParseMode.NaturalLanguage;

                return command;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Natural language parse failed: {ex.Message}", ex);
                throw new NaturalLanguageParseException("Failed to parse natural language input", ex);
            }
        }

        public string[] GetCompletions(string partialInput, int position)
        {
            if (string.IsNullOrEmpty(partialInput))
                return Array.Empty<string>();

            try
            {
                var tokens = Tokenize(partialInput.Substring(0, position));
                var currentToken = GetTokenAtPosition(tokens, position);

                var completions = new List<string>();

                // Command completion;
                if (tokens.Count == 0 || (tokens.Count == 1 && position >= partialInput.Length))
                {
                    completions.AddRange(_commandRegistry.GetCommandNames()
                        .Where(cmd => cmd.StartsWith(partialInput, StringComparison.OrdinalIgnoreCase)));
                }
                // Argument completion;
                else if (tokens.Count > 0)
                {
                    var commandName = tokens[0].Value;
                    if (_commandRegistry.CommandExists(commandName))
                    {
                        // Get argument completions from command registry
                        var commandInfo = _commandRegistry.GetCommandInfo(commandName);
                        if (commandInfo?.Completions != null)
                        {
                            completions.AddRange(commandInfo.Completions;
                                .Where(arg => arg.StartsWith(currentToken?.Value ?? "",
                                    StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                }

                // File system completion;
                if (currentToken != null &&
                    (currentToken.Value.StartsWith("./") ||
                     currentToken.Value.StartsWith("../") ||
                     currentToken.Value.Contains('/') ||
                     currentToken.Value.Contains('\\')))
                {
                    completions.AddRange(GetFileSystemCompletions(currentToken.Value));
                }

                // Variable completion;
                if (currentToken != null && currentToken.Value.StartsWith("$"))
                {
                    completions.AddRange(_environmentVariables.Keys;
                        .Where(key => key.StartsWith(currentToken.Value.Substring(1),
                            StringComparison.OrdinalIgnoreCase))
                        .Select(key => $"${key}"));
                }

                return completions.Distinct().ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Completion failed: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        public string FormatCommand(ParsedCommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            var parts = new List<string> { command.CommandName };

            // Add flags;
            if (command.Flags.Any())
            {
                parts.AddRange(command.Flags.Select(f => $"-{f}"));
            }

            // Add options;
            if (command.Options.Any())
            {
                parts.AddRange(command.Options.Select(kv => $"--{kv.Key}={kv.Value}"));
            }

            // Add arguments;
            if (command.Arguments.Any())
            {
                parts.AddRange(command.Arguments.Select(EscapeArgument));
            }

            // Add pipes;
            if (command.PipedCommands.Any())
            {
                parts.Add("|");
                parts.Add(FormatCommand(command.PipedCommands.First()));
            }

            // Add redirection;
            if (command.Redirection != null)
            {
                parts.Add(command.Redirection.Append ? ">>" : ">");
                parts.Add(command.Redirection.Target);
            }

            return string.Join(" ", parts);
        }

        public void SetEnvironmentVariable(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Variable name cannot be null or empty", nameof(name));

            _environmentVariables[name.ToUpperInvariant()] = value;
            _logger.LogDebug($"Environment variable set: {name}={value}");
        }

        public object GetEnvironmentVariable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Variable name cannot be null or empty", nameof(name));

            return _environmentVariables.TryGetValue(name.ToUpperInvariant(), out var value)
                ? value : null;
        }

        public IReadOnlyDictionary<string, object> GetEnvironmentVariables() =>
            _environmentVariables;

        public void ClearHistory()
        {
            _commandHistory.Clear();
            _logger.LogDebug("Command history cleared");
        }

        public IReadOnlyList<string> GetHistory() => _commandHistory.GetAll();

        public bool SearchHistory(string pattern, out string match)
        {
            return _commandHistory.Search(pattern, out match);
        }

        public ParserStatus GetStatus()
        {
            return new ParserStatus;
            {
                IsInitialized = _isInitialized,
                CommandsRegistered = _commandRegistry.GetCommandNames().Count(),
                HistorySize = _commandHistory.GetAll().Count,
                EnvironmentVariables = _environmentVariables.Count,
                Configuration = _configuration;
            };
        }

        #endregion;

        #region Private Methods;

        private void ValidateParserState()
        {
            if (!_isInitialized)
                throw new ParserNotInitializedException("ConsoleParser is not initialized");
        }

        private string PreprocessInput(string input, ParseMode mode)
        {
            // Trim and normalize whitespace;
            input = input.Trim();

            // Remove comments;
            if (input.Contains('#'))
            {
                input = input.Split('#')[0].Trim();
            }

            // Expand variables;
            if (_configuration.EnableVariables)
            {
                input = ExpandVariables(input);
            }

            // Handle command substitution;
            if (_configuration.EnableCommandSubstitution && input.Contains('$'))
            {
                input = ProcessCommandSubstitution(input);
            }

            return input;
        }

        private List<Token> Tokenize(string input)
        {
            var tokens = new List<Token>();
            var position = 0;
            var inQuote = false;
            var quoteChar = '\0';
            var currentToken = new StringBuilder();
            var tokenStart = 0;

            while (position < input.Length)
            {
                var currentChar = input[position];

                if (!inQuote && _configuration.QuoteCharacters.Contains(currentChar))
                {
                    // Start quoted section;
                    inQuote = true;
                    quoteChar = currentChar;
                    tokenStart = position;
                }
                else if (inQuote && currentChar == quoteChar)
                {
                    // End quoted section;
                    inQuote = false;
                    if (currentToken.Length > 0)
                    {
                        tokens.Add(CreateToken(currentToken.ToString(), tokenStart,
                            position - tokenStart + 1, true, input));
                        currentToken.Clear();
                    }
                }
                else if (!inQuote && _configuration.ArgumentDelimiters.Contains(currentChar))
                {
                    // End of token;
                    if (currentToken.Length > 0)
                    {
                        tokens.Add(CreateToken(currentToken.ToString(), tokenStart,
                            position - tokenStart, false, input));
                        currentToken.Clear();
                    }

                    // Check for special characters;
                    if (currentChar == _configuration.PipeCharacter && _configuration.EnablePipes)
                    {
                        tokens.Add(new Token;
                        {
                            Value = currentChar.ToString(),
                            Type = TokenType.Pipe,
                            Position = position,
                            Length = 1,
                            OriginalText = input;
                        });
                    }
                    else if (currentChar == _configuration.RedirectionCharacter &&
                             _configuration.EnableRedirections)
                    {
                        tokens.Add(new Token;
                        {
                            Value = currentChar.ToString(),
                            Type = TokenType.Redirection,
                            Position = position,
                            Length = 1,
                            OriginalText = input;
                        });
                    }
                }
                else;
                {
                    // Add character to current token;
                    if (currentToken.Length == 0)
                    {
                        tokenStart = position;
                    }
                    currentToken.Append(currentChar);
                }

                position++;
            }

            // Add last token if exists;
            if (currentToken.Length > 0)
            {
                tokens.Add(CreateToken(currentToken.ToString(), tokenStart,
                    position - tokenStart, inQuote, input));
            }

            return tokens;
        }

        private Token CreateToken(string value, int position, int length, bool isQuoted, string originalText)
        {
            var tokenType = DetermineTokenType(value, position, originalText);

            return new Token;
            {
                Value = value,
                Type = tokenType,
                Position = position,
                Length = length,
                IsQuoted = isQuoted,
                OriginalText = originalText;
            };
        }

        private TokenType DetermineTokenType(string value, int position, string originalText)
        {
            // Check position in command;
            if (position == 0)
                return TokenType.Command;

            // Check for options/flags;
            if (value.StartsWith("--"))
                return TokenType.Option;
            if (value.StartsWith("-"))
                return TokenType.Flag;

            // Check for variables;
            if (value.StartsWith("$"))
                return TokenType.Variable;

            // Check for numbers;
            if (double.TryParse(value, out _))
                return TokenType.Number;

            // Default to argument;
            return TokenType.Argument;
        }

        private ParsedCommand ParseTokens(List<Token> tokens, ParseMode mode)
        {
            var command = new ParsedCommand();
            var pipeIndex = tokens.FindIndex(t => t.Type == TokenType.Pipe);
            var redirectionIndex = tokens.FindIndex(t => t.Type == TokenType.Redirection);

            // Handle pipes;
            if (pipeIndex >= 0 && _configuration.EnablePipes)
            {
                var beforePipe = tokens.Take(pipeIndex).ToList();
                var afterPipe = tokens.Skip(pipeIndex + 1).ToList();

                command = ParseCommandTokens(beforePipe);
                command.PipedCommands.Add(ParseTokens(afterPipe, mode));
            }
            // Handle redirections;
            else if (redirectionIndex >= 0 && _configuration.EnableRedirections)
            {
                var beforeRedirect = tokens.Take(redirectionIndex).ToList();
                var afterRedirect = tokens.Skip(redirectionIndex + 1).ToList();

                command = ParseCommandTokens(beforeRedirect);
                command.Redirection = ParseRedirection(afterRedirect);
            }
            else;
            {
                command = ParseCommandTokens(tokens);
            }

            command.Tokens = tokens;
            return command;
        }

        private ParsedCommand ParseCommandTokens(List<Token> tokens)
        {
            if (tokens.Count == 0)
                throw new ParseException("No tokens to parse");

            var command = new ParsedCommand();
            command.CommandName = tokens[0].Value;

            for (int i = 1; i < tokens.Count; i++)
            {
                var token = tokens[i];

                switch (token.Type)
                {
                    case TokenType.Flag:
                        command.Flags.Add(token.Value.TrimStart('-'));
                        break;

                    case TokenType.Option:
                        var optionParts = token.Value.TrimStart('-').Split('=');
                        if (optionParts.Length == 2)
                        {
                            command.Options[optionParts[0]] = optionParts[1];
                        }
                        else;
                        {
                            command.Options[optionParts[0]] = "true";
                        }
                        break;

                    case TokenType.Argument:
                        command.Arguments.Add(token.Value);
                        break;

                    case TokenType.Variable:
                        var varName = token.Value.TrimStart('$');
                        command.Arguments.Add(_environmentVariables.TryGetValue(varName, out var value)
                            ? value.ToString() : "");
                        break;
                }
            }

            return command;
        }

        private RedirectionInfo ParseRedirection(List<Token> tokens)
        {
            if (tokens.Count < 1)
                throw new ParseException("Redirection target expected");

            var info = new RedirectionInfo;
            {
                Type = RedirectionType.Output,
                Target = tokens[0].Value,
                Append = tokens.Count > 1 && tokens[1].Value == ">"
            };

            return info;
        }

        private void ValidateCommand(ParsedCommand command)
        {
            // Check if command exists;
            if (!_commandRegistry.CommandExists(command.CommandName))
            {
                throw new UnknownCommandException($"Unknown command: {command.CommandName}");
            }

            // Get command info for validation;
            var commandInfo = _commandRegistry.GetCommandInfo(command.CommandName);

            // Validate required arguments;
            if (commandInfo?.RequiredArguments > command.Arguments.Count)
            {
                throw new MissingArgumentException(
                    $"Command '{command.CommandName}' requires at least {commandInfo.RequiredArguments} arguments");
            }

            // Validate options;
            if (commandInfo?.AllowedOptions != null)
            {
                foreach (var option in command.Options.Keys)
                {
                    if (!commandInfo.AllowedOptions.Contains(option))
                    {
                        throw new InvalidOptionException(
                            $"Invalid option '--{option}' for command '{command.CommandName}'");
                    }
                }
            }
        }

        private string ExpandVariables(string input)
        {
            var regex = new Regex(@"\$([A-Za-z_][A-Za-z0-9_]*)");
            return regex.Replace(input, match =>
            {
                var varName = match.Groups[1].Value;
                return _environmentVariables.TryGetValue(varName, out var value)
                    ? value.ToString() : "";
            });
        }

        private string ProcessCommandSubstitution(string input)
        {
            var regex = new Regex(@"\$\(([^)]+)\)");
            return regex.Replace(input, match =>
            {
                var command = match.Groups[1].Value;
                try
                {
                    // Parse and execute subcommand;
                    var subCommand = Parse(command, ParseMode.Script);
                    // In real implementation, this would execute the command;
                    return "[command output]";
                }
                catch
                {
                    return "";
                }
            });
        }

        private Token GetTokenAtPosition(List<Token> tokens, int position)
        {
            return tokens.LastOrDefault(t => position >= t.Position &&
                                           position <= t.Position + t.Length);
        }

        private string[] GetFileSystemCompletions(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(path) ?? ".";
                var pattern = Path.GetFileName(path) + "*";

                if (Directory.Exists(directory))
                {
                    return Directory.GetFileSystemEntries(directory, pattern)
                        .Select(Path.GetFileName)
                        .ToArray();
                }
            }
            catch
            {
                // Ignore filesystem errors;
            }

            return Array.Empty<string>();
        }

        private string EscapeArgument(string argument)
        {
            if (argument.Contains(' ') || argument.Contains('\t') ||
                argument.Contains('"') || argument.Contains('\''))
            {
                return $"\"{argument.Replace("\"", "\\\"")}\"";
            }
            return argument;
        }

        private ParseErrorType GetErrorType(Exception ex)
        {
            return ex switch;
            {
                UnknownCommandException => ParseErrorType.UnknownCommand,
                MissingArgumentException => ParseErrorType.MissingArgument,
                InvalidOptionException => ParseErrorType.InvalidArgument,
                _ => ParseErrorType.SyntaxError;
            };
        }

        private ParsedCommand ConvertNlpToCommand(object nlpResult)
        {
            // This is a simplified conversion;
            // In real implementation, this would convert NLP parse tree to command structure;
            return new ParsedCommand;
            {
                CommandName = "nlp_processed",
                Arguments = new List<string> { "NLP processing complete" },
                Mode = ParseMode.NaturalLanguage;
            };
        }

        #endregion;

        #region Built-in Command Handlers;

        private object DisplayHelp(string commandName = null)
        {
            if (string.IsNullOrEmpty(commandName))
            {
                Console.WriteLine("Available commands:");
                foreach (var cmd in _commandRegistry.GetCommandNames())
                {
                    var info = _commandRegistry.GetCommandInfo(cmd);
                    Console.WriteLine($"  {cmd,-20} {info?.Description ?? "No description"}");
                }
            }
            else;
            {
                var info = _commandRegistry.GetCommandInfo(commandName);
                if (info != null)
                {
                    Console.WriteLine($"{commandName}: {info.Description}");
                    if (!string.IsNullOrEmpty(info.Usage))
                    {
                        Console.WriteLine($"Usage: {info.Usage}");
                    }
                }
                else;
                {
                    Console.WriteLine($"Command not found: {commandName}");
                }
            }
            return null;
        }

        private object DisplayHistory()
        {
            var history = _commandHistory.GetAll();
            for (int i = 0; i < history.Count; i++)
            {
                Console.WriteLine($"{i + 1,4}: {history[i]}");
            }
            return null;
        }

        private object SetEnvironmentVariable(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: set <variable> <value>");
                return null;
            }

            SetEnvironmentVariable(args[0], args[1]);
            Console.WriteLine($"{args[0]} = {args[1]}");
            return null;
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _commandHistory?.Clear();
                    _environmentVariables?.Clear();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;

        #region Helper Classes;

        public class ParserStatus;
        {
            public bool IsInitialized { get; set; }
            public int CommandsRegistered { get; set; }
            public int HistorySize { get; set; }
            public int EnvironmentVariables { get; set; }
            public ConsoleParserConfiguration Configuration { get; set; }
        }

        #endregion;
    }

    #region Custom Exceptions;

    public class ParserInitializationException : Exception
    {
        public ParserInitializationException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class ParserNotInitializedException : InvalidOperationException;
    {
        public ParserNotInitializedException(string message)
            : base(message) { }
    }

    public class CommandParseException : Exception
    {
        public CommandParseException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class ParseException : Exception
    {
        public ParseException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class UnknownCommandException : Exception
    {
        public UnknownCommandException(string message)
            : base(message) { }
    }

    public class MissingArgumentException : Exception
    {
        public MissingArgumentException(string message)
            : base(message) { }
    }

    public class InvalidOptionException : Exception
    {
        public InvalidOptionException(string message)
            : base(message) { }
    }

    public class NaturalLanguageParseException : Exception
    {
        public NaturalLanguageParseException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    #endregion;

    #region Default Implementations;

    internal class DefaultLogger : ILogger;
    {
        public void LogInformation(string message) => Console.WriteLine($"[INFO] {message}");
        public void LogDebug(string message) => Console.WriteLine($"[DEBUG] {message}");
        public void LogWarning(string message) => Console.WriteLine($"[WARNING] {message}");
        public void LogError(string message, Exception ex = null) =>
            Console.WriteLine($"[ERROR] {message}: {ex?.Message}");
    }

    #endregion;
}
