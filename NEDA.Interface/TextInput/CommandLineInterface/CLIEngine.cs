using MaterialDesignThemes.Wpf;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.Commands;
using NEDA.Core.Commands.Interfaces;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using NEDA.Interface.TextInput.CommandLineInterface.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.TextInput.CommandLineInterface;
{
    /// <summary>
    /// Profesyonel komut satırı arabirim motoru;
    /// Gelişmiş CLI özellikleri, otomasyon, scripting ve batch işlemleri;
    /// </summary>
    public class CLIEngine : ICLIEngine, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly ICommandParser _commandParser;
        private readonly ICommandExecutor _commandExecutor;
        private readonly ISystemManager _systemManager;

        private readonly object _syncLock = new object();
        private bool _isRunning;
        private bool _isDisposed;
        private Thread _cliThread;
        private CancellationTokenSource _cancellationTokenSource;
        private StreamWriter _outputWriter;
        private StreamReader _inputReader;

        // CLI Ayarları;
        private CLIConfiguration _configuration;
        private CLIState _currentState;
        private readonly Stack<CLIState> _stateStack = new Stack<CLIState>();

        // Geçmiş ve Tamamlama;
        private readonly List<string> _commandHistory = new List<string>();
        private int _historyIndex = -1;
        private readonly AutoCompleteEngine _autoCompleteEngine;

        // Session Bilgisi;
        private CLISession _currentSession;
        private readonly Dictionary<string, CLISession> _sessions = new Dictionary<string, CLISession>();

        // Değişkenler ve Ortam;
        private readonly Dictionary<string, object> _environmentVariables = new Dictionary<string, object>();
        private readonly Dictionary<string, Func<string[], string>> _registeredCommands = new Dictionary<string, Func<string[], string>>();
        private readonly Dictionary<string, ScriptFunction> _scriptFunctions = new Dictionary<string, ScriptFunction>();

        // Plugin Sistemi;
        private readonly List<ICLIPlugin> _plugins = new List<ICLIPlugin>();

        // Olaylar (Events)
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;
        public event EventHandler<CommandErrorEventArgs> CommandError;
        public event EventHandler<CLIStateChangedEventArgs> StateChanged;
        public event EventHandler<SessionEventArgs> SessionStarted;
        public event EventHandler<SessionEventArgs> SessionEnded;
        public event EventHandler<OutputEventArgs> OutputGenerated;

        /// <summary>
        /// CLIEngine Constructor;
        /// </summary>
        public CLIEngine(
            ILogger logger = null,
            ISecurityManager securityManager = null,
            ICommandParser commandParser = null,
            ICommandExecutor commandExecutor = null,
            ISystemManager systemManager = null)
        {
            _logger = logger ?? LogManager.GetLogger(typeof(CLIEngine));
            _securityManager = securityManager ?? SecurityManager.Instance;
            _commandParser = commandParser ?? new CommandParser();
            _commandExecutor = commandExecutor ?? new CommandExecutor();
            _systemManager = systemManager ?? SystemManager.Instance;

            // Varsayılan konfigürasyon;
            _configuration = new CLIConfiguration();

            // Otomatik tamamlama motoru;
            _autoCompleteEngine = new AutoCompleteEngine();

            // Mevcut oturum;
            _currentSession = new CLISession;
            {
                SessionId = Guid.NewGuid().ToString(),
                StartTime = DateTime.Now,
                UserName = Environment.UserName,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                HostName = Environment.MachineName;
            };

            // Varsayılan ortam değişkenlerini yükle;
            InitializeEnvironmentVariables();

            // Varsayılan komutları kaydet;
            RegisterDefaultCommands();

            // Plugin'leri yükle;
            LoadPlugins();

            _logger.Info("CLIEngine başlatıldı");
        }

        /// <summary>
        /// CLIEngine Constructor with configuration;
        /// </summary>
        public CLIEngine(CLIConfiguration configuration, ILogger logger = null)
            : this(logger)
        {
            if (configuration != null)
            {
                _configuration = configuration;
                ApplyConfiguration();
            }
        }

        #region Public Properties;

        /// <summary>
        /// CLI'nin çalışma durumu;
        /// </summary>
        public bool IsRunning;
        {
            get;
            {
                lock (_syncLock)
                {
                    return _isRunning;
                }
            }
        }

        /// <summary>
        /// Geçerli CLI durumu;
        /// </summary>
        public CLIState CurrentState;
        {
            get => _currentState;
            private set;
            {
                if (_currentState != value)
                {
                    var oldState = _currentState;
                    _currentState = value;
                    OnStateChanged(new CLIStateChangedEventArgs;
                    {
                        OldState = oldState,
                        NewState = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// CLI konfigürasyonu;
        /// </summary>
        public CLIConfiguration Configuration;
        {
            get => _configuration;
            set;
            {
                _configuration = value ?? throw new ArgumentNullException(nameof(value));
                ApplyConfiguration();
            }
        }

        /// <summary>
        /// Geçerli oturum;
        /// </summary>
        public CLISession CurrentSession => _currentSession;

        /// <summary>
        /// Komut geçmişi;
        /// </summary>
        public IReadOnlyList<string> CommandHistory => _commandHistory.AsReadOnly();

        /// <summary>
        /// Ortam değişkenleri;
        /// </summary>
        public IReadOnlyDictionary<string, object> EnvironmentVariables =>
            _environmentVariables.AsReadOnly();

        /// <summary>
        /// Çıktı akışı;
        /// </summary>
        public TextWriter Output => _outputWriter ?? Console.Out;

        /// <summary>
        /// Giriş akışı;
        /// </summary>
        public TextReader Input => _inputReader ?? Console.In;

        #endregion;

        #region Public Methods;

        /// <summary>
        /// CLI'yi başlat;
        /// </summary>
        public void Start()
        {
            lock (_syncLock)
            {
                if (_isRunning)
                {
                    _logger.Warn("CLI zaten çalışıyor");
                    return;
                }

                try
                {
                    _cancellationTokenSource = new CancellationTokenSource();

                    // CLI thread'ini başlat;
                    _cliThread = new Thread(RunCLI)
                    {
                        Name = "CLIEngine-MainThread",
                        IsBackground = true,
                        Priority = ThreadPriority.Normal;
                    };

                    _cliThread.Start(_cancellationTokenSource.Token);

                    _isRunning = true;
                    CurrentState = CLIState.Running;

                    // Oturum başlatma event'i;
                    OnSessionStarted(new SessionEventArgs;
                    {
                        Session = _currentSession,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Info("CLI başlatıldı");
                }
                catch (Exception ex)
                {
                    _logger.Error($"CLI başlatma hatası: {ex.Message}", ex);
                    throw new CLIEngineException("CLI başlatılamadı", ex);
                }
            }
        }

        /// <summary>
        /// CLI'yi durdur;
        /// </summary>
        public void Stop()
        {
            lock (_syncLock)
            {
                if (!_isRunning)
                {
                    return;
                }

                try
                {
                    _cancellationTokenSource?.Cancel();

                    // Thread'in bitmesini bekle;
                    if (_cliThread != null && _cliThread.IsAlive)
                    {
                        _cliThread.Join(TimeSpan.FromSeconds(5));
                    }

                    _isRunning = false;
                    CurrentState = CLIState.Stopped;

                    // Temizlik;
                    CleanupResources();

                    // Oturum sonlandırma event'i;
                    OnSessionEnded(new SessionEventArgs;
                    {
                        Session = _currentSession,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Info("CLI durduruldu");
                }
                catch (Exception ex)
                {
                    _logger.Error($"CLI durdurma hatası: {ex.Message}", ex);
                    throw new CLIEngineException("CLI durdurulamadı", ex);
                }
            }
        }

        /// <summary>
        /// Komut çalıştır;
        /// </summary>
        public async Task<CommandResult> ExecuteCommandAsync(string commandLine, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(commandLine, nameof(commandLine));

            var stopwatch = Stopwatch.StartNew();
            var result = new CommandResult();

            try
            {
                // Güvenlik kontrolü;
                if (!await CheckSecurityAsync(commandLine))
                {
                    result.Status = CommandStatus.SecurityDenied;
                    result.Error = "Bu komutu çalıştırmak için yetkiniz yok";
                    return result;
                }

                // Komutu parse et;
                var parsedCommand = await ParseCommandAsync(commandLine);

                if (parsedCommand == null)
                {
                    result.Status = CommandStatus.ParseError;
                    result.Error = "Komut parse edilemedi";
                    return result;
                }

                // Plugin ön işleme;
                var preprocessedCommand = await PreprocessCommandAsync(parsedCommand);

                // Komutu çalıştır;
                result = await ExecuteParsedCommandAsync(preprocessedCommand, cancellationToken);
                result.ExecutionTime = stopwatch.Elapsed;

                // Geçmişe ekle;
                AddToHistory(commandLine);

                // Event tetikle;
                OnCommandExecuted(new CommandExecutedEventArgs;
                {
                    CommandLine = commandLine,
                    Result = result,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Debug($"Komut çalıştırıldı: {commandLine} - Süre: {result.ExecutionTime.TotalMilliseconds}ms");
            }
            catch (OperationCanceledException)
            {
                result.Status = CommandStatus.Cancelled;
                result.Error = "Komut iptal edildi";
                _logger.Warn($"Komut iptal edildi: {commandLine}");
            }
            catch (Exception ex)
            {
                result.Status = CommandStatus.Error;
                result.Error = $"Komut hatası: {ex.Message}";
                result.Exception = ex;

                OnCommandError(new CommandErrorEventArgs;
                {
                    CommandLine = commandLine,
                    Exception = ex,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Error($"Komut çalıştırma hatası: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
            }

            return result;
        }

        /// <summary>
        /// Batch komutları çalıştır;
        /// </summary>
        public async Task<BatchResult> ExecuteBatchAsync(IEnumerable<string> commands,
            BatchOptions options = null, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(commands, nameof(commands));

            var batchOptions = options ?? new BatchOptions();
            var batchResult = new BatchResult();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.Info($"Batch işlemi başlatılıyor: {commands.Count()} komut");

                foreach (var command in commands)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        batchResult.Status = BatchStatus.Cancelled;
                        break;
                    }

                    var commandResult = await ExecuteCommandAsync(command, cancellationToken);
                    batchResult.CommandResults.Add(commandResult);

                    // Hata durumunda dur;
                    if (batchOptions.StopOnError && commandResult.Status != CommandStatus.Success)
                    {
                        batchResult.Status = BatchStatus.StoppedOnError;
                        break;
                    }

                    // Gecikme;
                    if (batchOptions.DelayBetweenCommands > 0)
                    {
                        await Task.Delay(batchOptions.DelayBetweenCommands, cancellationToken);
                    }
                }

                batchResult.ExecutionTime = stopwatch.Elapsed;
                batchResult.TotalCommands = commands.Count();
                batchResult.SuccessfulCommands = batchResult.CommandResults.Count(r => r.Status == CommandStatus.Success);

                _logger.Info($"Batch işlemi tamamlandı: {batchResult.SuccessfulCommands}/{batchResult.TotalCommands} başarılı");
            }
            catch (Exception ex)
            {
                batchResult.Status = BatchStatus.Error;
                batchResult.Error = $"Batch hatası: {ex.Message}";
                _logger.Error($"Batch işlemi hatası: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
            }

            return batchResult;
        }

        /// <summary>
        /// Script dosyası çalıştır;
        /// </summary>
        public async Task<ScriptResult> ExecuteScriptAsync(string scriptPath,
            ScriptOptions options = null, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(scriptPath, nameof(scriptPath));

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Script dosyası bulunamadı: {scriptPath}");
            }

            var scriptOptions = options ?? new ScriptOptions();
            var scriptResult = new ScriptResult { ScriptPath = scriptPath };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Script'i yükle;
                var scriptContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);
                var scriptLines = ParseScript(scriptContent);

                _logger.Info($"Script çalıştırılıyor: {scriptPath} - {scriptLines.Count} satır");

                // Script'i çalıştır;
                var batchResult = await ExecuteBatchAsync(scriptLines, new BatchOptions;
                {
                    StopOnError = scriptOptions.StopOnError,
                    DelayBetweenCommands = scriptOptions.DelayBetweenCommands;
                }, cancellationToken);

                scriptResult.BatchResult = batchResult;
                scriptResult.ExecutionTime = stopwatch.Elapsed;

                _logger.Info($"Script çalıştırma tamamlandı: {scriptPath}");
            }
            catch (Exception ex)
            {
                scriptResult.Status = ScriptStatus.Error;
                scriptResult.Error = $"Script hatası: {ex.Message}";
                _logger.Error($"Script çalıştırma hatası: {ex.Message}", ex);
            }
            finally
            {
                stopwatch.Stop();
            }

            return scriptResult;
        }

        /// <summary>
        /// Komut kaydet;
        /// </summary>
        public bool RegisterCommand(string commandName, Func<string[], string> commandHandler)
        {
            Guard.ArgumentNotNullOrEmpty(commandName, nameof(commandName));
            Guard.ArgumentNotNull(commandHandler, nameof(commandHandler));

            lock (_syncLock)
            {
                if (_registeredCommands.ContainsKey(commandName))
                {
                    _logger.Warn($"Komut zaten kayıtlı: {commandName}");
                    return false;
                }

                _registeredCommands[commandName] = commandHandler;
                _autoCompleteEngine.RegisterCommand(commandName);

                _logger.Debug($"Komut kaydedildi: {commandName}");
                return true;
            }
        }

        /// <summary>
        /// Plugin kaydet;
        /// </summary>
        public bool RegisterPlugin(ICLIPlugin plugin)
        {
            Guard.ArgumentNotNull(plugin, nameof(plugin));

            lock (_syncLock)
            {
                try
                {
                    plugin.Initialize(this);
                    _plugins.Add(plugin);

                    _logger.Info($"Plugin kaydedildi: {plugin.Name}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Plugin başlatma hatası: {ex.Message}", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Ortam değişkeni ayarla;
        /// </summary>
        public void SetEnvironmentVariable(string name, object value)
        {
            Guard.ArgumentNotNullOrEmpty(name, nameof(name));

            lock (_syncLock)
            {
                _environmentVariables[name] = value;
                _logger.Debug($"Ortam değişkeni ayarlandı: {name}={value}");
            }
        }

        /// <summary>
        /// Ortam değişkeni al;
        /// </summary>
        public object GetEnvironmentVariable(string name)
        {
            Guard.ArgumentNotNullOrEmpty(name, nameof(name));

            lock (_syncLock)
            {
                return _environmentVariables.TryGetValue(name, out var value) ? value : null;
            }
        }

        /// <summary>
        /// CLI prompt'u göster;
        /// </summary>
        public string GetPrompt()
        {
            var promptTemplate = _configuration.PromptTemplate ?? "[{user}@{host}:{path}]$ ";

            var prompt = promptTemplate;
                .Replace("{user}", _currentSession.UserName)
                .Replace("{host}", _currentSession.HostName)
                .Replace("{path}", GetShortPath(_currentSession.WorkingDirectory))
                .Replace("{time}", DateTime.Now.ToString("HH:mm"))
                .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"));

            return ColorizePrompt(prompt);
        }

        /// <summary>
        /// Otomatik tamamlama önerileri al;
        /// </summary>
        public List<string> GetAutoCompleteSuggestions(string partialCommand)
        {
            return _autoCompleteEngine.GetSuggestions(partialCommand);
        }

        /// <summary>
        /// Komut geçmişini temizle;
        /// </summary>
        public void ClearHistory()
        {
            lock (_syncLock)
            {
                _commandHistory.Clear();
                _historyIndex = -1;
                _logger.Debug("Komut geçmişi temizlendi");
            }
        }

        /// <summary>
        /// Geçmişteki bir komutu al;
        /// </summary>
        public string GetHistoryCommand(bool forward)
        {
            lock (_syncLock)
            {
                if (!_commandHistory.Any())
                    return string.Empty;

                if (forward)
                {
                    _historyIndex = Math.Min(_historyIndex + 1, _commandHistory.Count - 1);
                }
                else;
                {
                    _historyIndex = Math.Max(_historyIndex - 1, -1);
                }

                return _historyIndex >= 0 ? _commandHistory[_historyIndex] : string.Empty;
            }
        }

        /// <summary>
        /// CLI'den çıkış yap;
        /// </summary>
        public void Exit()
        {
            Stop();
        }

        /// <summary>
        /// CLI durumunu değiştir;
        /// </summary>
        public void ChangeState(CLIState newState)
        {
            CurrentState = newState;
        }

        /// <summary>
        /// Çıktı gönder;
        /// </summary>
        public void WriteOutput(string output, OutputType outputType = OutputType.Normal)
        {
            if (string.IsNullOrEmpty(output))
                return;

            var formattedOutput = FormatOutput(output, outputType);

            lock (_syncLock)
            {
                Output.Write(formattedOutput);
                Output.Flush();

                OnOutputGenerated(new OutputEventArgs;
                {
                    Output = output,
                    OutputType = outputType,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Çıktı satırı gönder;
        /// </summary>
        public void WriteLine(string line = "", OutputType outputType = OutputType.Normal)
        {
            WriteOutput(line + Environment.NewLine, outputType);
        }

        /// <summary>
        /// CLI yardımı göster;
        /// </summary>
        public void ShowHelp(string command = null)
        {
            if (string.IsNullOrEmpty(command))
            {
                WriteLine("Kullanılabilir Komutlar:", OutputType.Info);
                WriteLine("=======================", OutputType.Info);

                foreach (var cmd in _registeredCommands.Keys.OrderBy(k => k))
                {
                    WriteLine($"  {cmd}", OutputType.Normal);
                }

                WriteLine();
                WriteLine("Yardım için: help <komut>", OutputType.Info);
                WriteLine("Çıkış için: exit", OutputType.Info);
            }
            else;
            {
                ShowCommandHelp(command);
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// CLI ana döngüsü;
        /// </summary>
        private void RunCLI(object parameter)
        {
            var cancellationToken = (CancellationToken)parameter;

            try
            {
                WriteLine("NEDA CLI Engine - Başlatılıyor...", OutputType.Info);
                WriteLine($"Sürüm: {GetType().Assembly.GetName().Version}", OutputType.Info);
                WriteLine($"Oturum ID: {_currentSession.SessionId}", OutputType.Info);
                WriteLine();

                while (!cancellationToken.IsCancellationRequested && _isRunning)
                {
                    try
                    {
                        // Prompt göster;
                        var prompt = GetPrompt();
                        WriteOutput(prompt);

                        // Komut oku;
                        var commandLine = ReadCommandLine(cancellationToken);

                        if (string.IsNullOrWhiteSpace(commandLine))
                            continue;

                        // Komutu çalıştır;
                        var result = ExecuteCommandAsync(commandLine, cancellationToken).Result;

                        // Sonucu göster;
                        if (!string.IsNullOrEmpty(result.Output))
                        {
                            WriteLine(result.Output,
                                result.Status == CommandStatus.Success ? OutputType.Success : OutputType.Error);
                        }

                        if (result.Status == CommandStatus.Error && _configuration.ShowErrorDetails)
                        {
                            WriteLine($"Hata: {result.Error}", OutputType.Error);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        WriteLine($"CLI döngü hatası: {ex.Message}", OutputType.Error);
                        _logger.Error($"CLI döngü hatası: {ex.Message}", ex);

                        if (!_configuration.ContinueOnError)
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Fatal($"CLI ana döngü hatası: {ex.Message}", ex);
            }
            finally
            {
                WriteLine("CLI kapatılıyor...", OutputType.Info);
            }
        }

        /// <summary>
        /// Komut satırı oku;
        /// </summary>
        private string ReadCommandLine(CancellationToken cancellationToken)
        {
            var inputBuilder = new StringBuilder();
            int cursorPosition = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var keyInfo = ReadKey(true);

                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    WriteLine();
                    return inputBuilder.ToString();
                }
                else if (keyInfo.Key == ConsoleKey.Escape)
                {
                    inputBuilder.Clear();
                    cursorPosition = 0;
                    ClearCurrentLine();
                    WriteOutput(GetPrompt());
                }
                else if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (cursorPosition > 0)
                    {
                        inputBuilder.Remove(cursorPosition - 1, 1);
                        cursorPosition--;
                        RedrawInputLine(inputBuilder.ToString(), cursorPosition);
                    }
                }
                else if (keyInfo.Key == ConsoleKey.Delete)
                {
                    if (cursorPosition < inputBuilder.Length)
                    {
                        inputBuilder.Remove(cursorPosition, 1);
                        RedrawInputLine(inputBuilder.ToString(), cursorPosition);
                    }
                }
                else if (keyInfo.Key == ConsoleKey.LeftArrow)
                {
                    if (cursorPosition > 0)
                    {
                        cursorPosition--;
                        UpdateCursorPosition(cursorPosition);
                    }
                }
                else if (keyInfo.Key == ConsoleKey.RightArrow)
                {
                    if (cursorPosition < inputBuilder.Length)
                    {
                        cursorPosition++;
                        UpdateCursorPosition(cursorPosition);
                    }
                }
                else if (keyInfo.Key == ConsoleKey.UpArrow)
                {
                    var historyCommand = GetHistoryCommand(false);
                    if (!string.IsNullOrEmpty(historyCommand))
                    {
                        inputBuilder.Clear();
                        inputBuilder.Append(historyCommand);
                        cursorPosition = historyCommand.Length;
                        RedrawInputLine(historyCommand, cursorPosition);
                    }
                }
                else if (keyInfo.Key == ConsoleKey.DownArrow)
                {
                    var historyCommand = GetHistoryCommand(true);
                    inputBuilder.Clear();
                    inputBuilder.Append(historyCommand);
                    cursorPosition = historyCommand.Length;
                    RedrawInputLine(historyCommand, cursorPosition);
                }
                else if (keyInfo.Key == ConsoleKey.Tab)
                {
                    var suggestions = GetAutoCompleteSuggestions(inputBuilder.ToString());
                    if (suggestions.Any())
                    {
                        var suggestion = suggestions.First();
                        inputBuilder.Append(suggestion);
                        cursorPosition += suggestion.Length;
                        RedrawInputLine(inputBuilder.ToString(), cursorPosition);
                    }
                }
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    inputBuilder.Insert(cursorPosition, keyInfo.KeyChar);
                    cursorPosition++;
                    RedrawInputLine(inputBuilder.ToString(), cursorPosition);
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Komutu parse et;
        /// </summary>
        private async Task<ParsedCommand> ParseCommandAsync(string commandLine)
        {
            try
            {
                // Ortam değişkenlerini genişlet;
                var expandedLine = ExpandEnvironmentVariables(commandLine);

                // Komutu parse et;
                var parsedCommand = _commandParser.Parse(expandedLine);

                if (parsedCommand == null)
                {
                    WriteLine("Hata: Komut parse edilemedi", OutputType.Error);
                    return null;
                }

                // Alias kontrolü;
                if (_configuration.Aliases.ContainsKey(parsedCommand.CommandName))
                {
                    parsedCommand.CommandName = _configuration.Aliases[parsedCommand.CommandName];
                }

                return parsedCommand;
            }
            catch (Exception ex)
            {
                _logger.Error($"Komut parse hatası: {ex.Message}", ex);
                WriteLine($"Parse hatası: {ex.Message}", OutputType.Error);
                return null;
            }
        }

        /// <summary>
        /// Parse edilmiş komutu çalıştır;
        /// </summary>
        private async Task<CommandResult> ExecuteParsedCommandAsync(ParsedCommand command, CancellationToken cancellationToken)
        {
            var result = new CommandResult;
            {
                CommandName = command.CommandName,
                Arguments = command.Arguments.ToArray(),
                StartTime = DateTime.UtcNow;
            };

            try
            {
                // Yerel komutları kontrol et;
                if (_registeredCommands.ContainsKey(command.CommandName))
                {
                    var output = _registeredCommands[command.CommandName](command.Arguments.ToArray());
                    result.Output = output;
                    result.Status = CommandStatus.Success;
                }
                // CommandExecutor ile çalıştır;
                else if (_commandExecutor != null)
                {
                    var executionResult = await _commandExecutor.ExecuteAsync(command, cancellationToken);
                    result.Output = executionResult.Output;
                    result.Status = executionResult.Success ? CommandStatus.Success : CommandStatus.Error;
                    result.Error = executionResult.ErrorMessage;
                }
                // Bilinmeyen komut;
                else;
                {
                    result.Status = CommandStatus.NotFound;
                    result.Error = $"Komut bulunamadı: {command.CommandName}";
                }
            }
            catch (Exception ex)
            {
                result.Status = CommandStatus.Error;
                result.Error = $"Çalıştırma hatası: {ex.Message}";
                result.Exception = ex;
            }
            finally
            {
                result.EndTime = DateTime.UtcNow;
            }

            return result;
        }

        /// <summary>
        /// Komut ön işleme;
        /// </summary>
        private async Task<ParsedCommand> PreprocessCommandAsync(ParsedCommand command)
        {
            // Plugin'lerin ön işlemesini uygula;
            foreach (var plugin in _plugins)
            {
                try
                {
                    command = await plugin.PreprocessCommandAsync(command) ?? command;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Plugin ön işleme hatası: {ex.Message}", ex);
                }
            }

            return command;
        }

        /// <summary>
        /// Script içeriğini parse et;
        /// </summary>
        private List<string> ParseScript(string scriptContent)
        {
            var lines = new List<string>();
            var scriptLines = scriptContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            foreach (var line in scriptLines)
            {
                var trimmedLine = line.Trim();

                // Boş satır ve yorumları atla;
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                    continue;

                // Satır devam ettirme kontrolü;
                if (trimmedLine.EndsWith("\\"))
                {
                    // Multi-line komut;
                    var multiLine = trimmedLine.TrimEnd('\\');
                    // Implement multi-line logic;
                    lines.Add(multiLine);
                }
                else;
                {
                    lines.Add(trimmedLine);
                }
            }

            return lines;
        }

        /// <summary>
        /// Ortam değişkenlerini genişlet;
        /// </summary>
        private string ExpandEnvironmentVariables(string input)
        {
            var regex = new Regex(@"\$(\w+)|%(\w+)%");
            return regex.Replace(input, match =>
            {
                var varName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var value = GetEnvironmentVariable(varName)?.ToString();
                return value ?? match.Value;
            });
        }

        /// <summary>
        /// Güvenlik kontrolü;
        /// </summary>
        private async Task<bool> CheckSecurityAsync(string commandLine)
        {
            if (_securityManager == null || !_configuration.EnableSecurityChecks)
                return true;

            try
            {
                var hasPermission = await _securityManager.CheckCommandPermissionAsync(
                    _currentSession.UserName,
                    commandLine);

                if (!hasPermission)
                {
                    WriteLine("Hata: Bu komutu çalıştırmak için yetkiniz yok", OutputType.Error);
                    _logger.Warn($"Güvenlik ihlali: {_currentSession.UserName} - {commandLine}");
                }

                return hasPermission;
            }
            catch (Exception ex)
            {
                _logger.Error($"Güvenlik kontrol hatası: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Komutu geçmişe ekle;
        /// </summary>
        private void AddToHistory(string commandLine)
        {
            lock (_syncLock)
            {
                // Tekrar eden komutları önle;
                if (_commandHistory.LastOrDefault() != commandLine)
                {
                    _commandHistory.Add(commandLine);

                    // Geçmiş sınırı kontrolü;
                    if (_commandHistory.Count > _configuration.MaxHistorySize)
                    {
                        _commandHistory.RemoveAt(0);
                    }
                }

                _historyIndex = _commandHistory.Count;
            }
        }

        /// <summary>
        /// Konsol tuş okuma;
        /// </summary>
        private ConsoleKeyInfo ReadKey(bool intercept)
        {
            try
            {
                return Console.ReadKey(intercept);
            }
            catch (InvalidOperationException)
            {
                // Konsol olmayan ortamlar için;
                return new ConsoleKeyInfo();
            }
        }

        /// <summary>
        /// Giriş satırını yeniden çiz;
        /// </summary>
        private void RedrawInputLine(string input, int cursorPosition)
        {
            try
            {
                ClearCurrentLine();
                WriteOutput(GetPrompt() + input);
                UpdateCursorPosition(cursorPosition + GetPrompt().Length);
            }
            catch (Exception ex)
            {
                _logger.Error($"Satır yeniden çizme hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Mevcut satırı temizle;
        /// </summary>
        private void ClearCurrentLine()
        {
            try
            {
                var currentLine = Console.CursorTop;
                Console.SetCursorPosition(0, currentLine);
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, currentLine);
            }
            catch (Exception)
            {
                // Konsol erişim hatası;
            }
        }

        /// <summary>
        /// Cursor pozisyonunu güncelle;
        /// </summary>
        private void UpdateCursorPosition(int position)
        {
            try
            {
                Console.CursorLeft = position;
            }
            catch (Exception)
            {
                // Konsol erişim hatası;
            }
        }

        /// <summary>
        /// Kısa yol gösterimi;
        /// </summary>
        private string GetShortPath(string path)
        {
            if (path.Length <= 30)
                return path;

            var parts = path.Split(Path.DirectorySeparatorChar);
            if (parts.Length <= 2)
                return path;

            return $"...{Path.DirectorySeparatorChar}{parts[parts.Length - 2]}{Path.DirectorySeparatorChar}{parts[parts.Length - 1]}";
        }

        /// <summary>
        /// Prompt'u renklendir;
        /// </summary>
        private string ColorizePrompt(string prompt)
        {
            if (!_configuration.EnableColors)
                return prompt;

            // Renk kodlarını uygula;
            return $"\x1b[32m{prompt}\x1b[0m"; // Yeşil renk;
        }

        /// <summary>
        /// Çıktıyı formatla;
        /// </summary>
        private string FormatOutput(string output, OutputType outputType)
        {
            if (!_configuration.EnableColors)
                return output;

            var colorCode = outputType switch;
            {
                OutputType.Error => "\x1b[31m",     // Kırmızı;
                OutputType.Success => "\x1b[32m",   // Yeşil;
                OutputType.Warning => "\x1b[33m",   // Sarı;
                OutputType.Info => "\x1b[36m",      // Cyan;
                OutputType.Debug => "\x1b[90m",     // Gri;
                _ => "\x1b[0m"                      // Varsayılan;
            };

            return $"{colorCode}{output}\x1b[0m";
        }

        /// <summary>
        /// Ortam değişkenlerini başlat;
        /// </summary>
        private void InitializeEnvironmentVariables()
        {
            _environmentVariables["CLI_VERSION"] = GetType().Assembly.GetName().Version.ToString();
            _environmentVariables["USER"] = Environment.UserName;
            _environmentVariables["HOST"] = Environment.MachineName;
            _environmentVariables["OS"] = Environment.OSVersion.Platform.ToString();
            _environmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH");
            _environmentVariables["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _environmentVariables["PWD"] = Directory.GetCurrentDirectory();
            _environmentVariables["SESSION_ID"] = _currentSession.SessionId;
        }

        /// <summary>
        /// Varsayılan komutları kaydet;
        /// </summary>
        private void RegisterDefaultCommands()
        {
            RegisterCommand("help", args =>
            {
                ShowHelp(args.Length > 0 ? args[0] : null);
                return string.Empty;
            });

            RegisterCommand("exit", args =>
            {
                Exit();
                return "Çıkış yapılıyor...";
            });

            RegisterCommand("clear", args =>
            {
                Console.Clear();
                return string.Empty;
            });

            RegisterCommand("echo", args => string.Join(" ", args));

            RegisterCommand("pwd", args => Directory.GetCurrentDirectory());

            RegisterCommand("cd", args =>
            {
                if (args.Length == 0)
                {
                    return Directory.GetCurrentDirectory();
                }

                var path = args[0];
                if (path == "..")
                {
                    var parent = Directory.GetParent(Directory.GetCurrentDirectory());
                    if (parent != null)
                    {
                        Directory.SetCurrentDirectory(parent.FullName);
                    }
                }
                else;
                {
                    if (Directory.Exists(path))
                    {
                        Directory.SetCurrentDirectory(path);
                    }
                    else;
                    {
                        return $"Dizin bulunamadı: {path}";
                    }
                }

                _currentSession.WorkingDirectory = Directory.GetCurrentDirectory();
                return _currentSession.WorkingDirectory;
            });

            RegisterCommand("ls", args =>
            {
                var path = args.Length > 0 ? args[0] : ".";
                var files = Directory.GetFiles(path);
                var directories = Directory.GetDirectories(path);

                var result = new StringBuilder();
                result.AppendLine("Dizinler:");
                foreach (var dir in directories)
                {
                    result.AppendLine($"  {Path.GetFileName(dir)}/");
                }

                result.AppendLine("Dosyalar:");
                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    result.AppendLine($"  {fileInfo.Name} ({fileInfo.Length} bytes)");
                }

                return result.ToString();
            });

            RegisterCommand("history", args =>
            {
                var result = new StringBuilder();
                result.AppendLine("Komut Geçmişi:");
                for (int i = 0; i < _commandHistory.Count; i++)
                {
                    result.AppendLine($"  {i + 1}: {_commandHistory[i]}");
                }
                return result.ToString();
            });

            RegisterCommand("env", args =>
            {
                var result = new StringBuilder();
                result.AppendLine("Ortam Değişkenleri:");
                foreach (var kvp in _environmentVariables)
                {
                    result.AppendLine($"  {kvp.Key}={kvp.Value}");
                }
                return result.ToString();
            });

            RegisterCommand("alias", args =>
            {
                if (args.Length == 0)
                {
                    var result = new StringBuilder();
                    result.AppendLine("Alias'lar:");
                    foreach (var kvp in _configuration.Aliases)
                    {
                        result.AppendLine($"  {kvp.Key} -> {kvp.Value}");
                    }
                    return result.ToString();
                }
                else if (args.Length == 2)
                {
                    _configuration.Aliases[args[0]] = args[1];
                    return $"Alias oluşturuldu: {args[0]} -> {args[1]}";
                }

                return "Kullanım: alias [isim] [komut] veya alias";
            });
        }

        /// <summary>
        /// Plugin'leri yükle;
        /// </summary>
        private void LoadPlugins()
        {
            try
            {
                // Plugin dizinini tara;
                var pluginDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "CLI");
                if (Directory.Exists(pluginDir))
                {
                    var pluginFiles = Directory.GetFiles(pluginDir, "*.dll");

                    foreach (var pluginFile in pluginFiles)
                    {
                        try
                        {
                            var assembly = System.Reflection.Assembly.LoadFrom(pluginFile);
                            var pluginTypes = assembly.GetTypes()
                                .Where(t => typeof(ICLIPlugin).IsAssignableFrom(t) && !t.IsAbstract);

                            foreach (var pluginType in pluginTypes)
                            {
                                var plugin = (ICLIPlugin)Activator.CreateInstance(pluginType);
                                RegisterPlugin(plugin);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Plugin yükleme hatası: {pluginFile} - {ex.Message}", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Plugin yükleme sistemi hatası: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Konfigürasyonu uygula;
        /// </summary>
        private void ApplyConfiguration()
        {
            // Geçmiş boyutunu ayarla;
            if (_commandHistory.Count > _configuration.MaxHistorySize)
            {
                _commandHistory.RemoveRange(0, _commandHistory.Count - _configuration.MaxHistorySize);
            }

            // Konsol ayarları;
            if (_configuration.EnableColors && Console.ForegroundColor != _configuration.DefaultColor)
            {
                try
                {
                    Console.ForegroundColor = _configuration.DefaultColor;
                }
                catch (Exception)
                {
                    // Konsol renk desteği yok;
                }
            }

            _logger.Debug("CLI konfigürasyonu uygulandı");
        }

        /// <summary>
        /// Komut yardımı göster;
        /// </summary>
        private void ShowCommandHelp(string commandName)
        {
            // Built-in komut yardımları;
            var helpTexts = new Dictionary<string, string>
            {
                ["help"] = "help [komut] - Komut yardımı gösterir",
                ["exit"] = "exit - CLI'den çıkış yapar",
                ["clear"] = "clear - Ekranı temizler",
                ["echo"] = "echo [metin] - Metni ekrana yazar",
                ["pwd"] = "pwd - Çalışma dizinini gösterir",
                ["cd"] = "cd [dizin] - Dizin değiştirir",
                ["ls"] = "ls [dizin] - Dizin içeriğini listeler",
                ["history"] = "history - Komut geçmişini gösterir",
                ["env"] = "env - Ortam değişkenlerini gösterir",
                ["alias"] = "alias [isim] [komut] - Alias oluşturur"
            };

            if (helpTexts.ContainsKey(commandName))
            {
                WriteLine(helpTexts[commandName], OutputType.Info);
            }
            else;
            {
                WriteLine($"'{commandName}' komutu için yardım bulunamadı", OutputType.Warning);
            }
        }

        /// <summary>
        /// Kaynakları temizle;
        /// </summary>
        private void CleanupResources()
        {
            try
            {
                foreach (var plugin in _plugins)
                {
                    plugin.Dispose();
                }
                _plugins.Clear();

                _outputWriter?.Dispose();
                _inputReader?.Dispose();

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                _logger.Debug("CLI kaynakları temizlendi");
            }
            catch (Exception ex)
            {
                _logger.Error($"Kaynak temizleme hatası: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Event Triggers;

        protected virtual void OnCommandExecuted(CommandExecutedEventArgs e)
        {
            CommandExecuted?.Invoke(this, e);
        }

        protected virtual void OnCommandError(CommandErrorEventArgs e)
        {
            CommandError?.Invoke(this, e);
        }

        protected virtual void OnStateChanged(CLIStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        protected virtual void OnSessionStarted(SessionEventArgs e)
        {
            SessionStarted?.Invoke(this, e);
        }

        protected virtual void OnSessionEnded(SessionEventArgs e)
        {
            SessionEnded?.Invoke(this, e);
        }

        protected virtual void OnOutputGenerated(OutputEventArgs e)
        {
            OutputGenerated?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

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
                    Stop();
                    CleanupResources();
                    _logger.Info("CLIEngine kaynakları temizlendi");
                }

                _isDisposed = true;
            }
        }

        ~CLIEngine()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// CLI durumları;
    /// </summary>
    public enum CLIState;
    {
        Stopped,
        Starting,
        Running,
        Paused,
        Stopping,
        Error;
    }

    /// <summary>
    /// Çıktı türleri;
    /// </summary>
    public enum OutputType;
    {
        Normal,
        Error,
        Success,
        Warning,
        Info,
        Debug;
    }

    /// <summary>
    /// Komut durumları;
    /// </summary>
    public enum CommandStatus;
    {
        Success,
        Error,
        Cancelled,
        SecurityDenied,
        ParseError,
        NotFound;
    }

    /// <summary>
    /// Batch durumları;
    /// </summary>
    public enum BatchStatus;
    {
        Success,
        Error,
        Cancelled,
        StoppedOnError;
    }

    /// <summary>
    /// Script durumları;
    /// </summary>
    public enum ScriptStatus;
    {
        Success,
        Error,
        ValidationFailed;
    }

    /// <summary>
    /// CLI konfigürasyonu;
    /// </summary>
    public class CLIConfiguration;
    {
        public string PromptTemplate { get; set; } = "[{user}@{host}:{path}]$ ";
        public int MaxHistorySize { get; set; } = 1000;
        public bool EnableColors { get; set; } = true;
        public bool EnableSecurityChecks { get; set; } = true;
        public bool ShowErrorDetails { get; set; } = true;
        public bool ContinueOnError { get; set; } = true;
        public ConsoleColor DefaultColor { get; set; } = ConsoleColor.Gray;
        public Dictionary<string, string> Aliases { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// CLI oturumu;
    /// </summary>
    public class CLISession;
    {
        public string SessionId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string UserName { get; set; }
        public string HostName { get; set; }
        public string WorkingDirectory { get; set; }
        public List<string> ExecutedCommands { get; set; } = new List<string>();

        public TimeSpan Duration => (EndTime ?? DateTime.Now) - StartTime;
    }

    /// <summary>
    /// Komut sonucu;
    /// </summary>
    public class CommandResult;
    {
        public string CommandName { get; set; }
        public string[] Arguments { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public Exception Exception { get; set; }
        public CommandStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ExecutionTime => EndTime - StartTime;
    }

    /// <summary>
    /// Batch seçenekleri;
    /// </summary>
    public class BatchOptions;
    {
        public bool StopOnError { get; set; } = true;
        public int DelayBetweenCommands { get; set; } = 0; // ms;
    }

    /// <summary>
    /// Batch sonucu;
    /// </summary>
    public class BatchResult;
    {
        public BatchStatus Status { get; set; }
        public string Error { get; set; }
        public List<CommandResult> CommandResults { get; set; } = new List<CommandResult>();
        public int TotalCommands { get; set; }
        public int SuccessfulCommands { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    /// <summary>
    /// Script seçenekleri;
    /// </summary>
    public class ScriptOptions;
    {
        public bool StopOnError { get; set; } = true;
        public int DelayBetweenCommands { get; set; } = 0; // ms;
        public bool ValidateSyntax { get; set; } = true;
    }

    /// <summary>
    /// Script sonucu;
    /// </summary>
    public class ScriptResult;
    {
        public string ScriptPath { get; set; }
        public ScriptStatus Status { get; set; }
        public string Error { get; set; }
        public BatchResult BatchResult { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    /// <summary>
    /// Script fonksiyonu;
    /// </summary>
    public class ScriptFunction;
    {
        public string Name { get; set; }
        public List<string> Parameters { get; set; } = new List<string>();
        public List<string> Body { get; set; } = new List<string>();
    }

    /// <summary>
    /// Komut çalıştırıldı event args;
    /// </summary>
    public class CommandExecutedEventArgs : EventArgs;
    {
        public string CommandLine { get; set; }
        public CommandResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Komut hatası event args;
    /// </summary>
    public class CommandErrorEventArgs : EventArgs;
    {
        public string CommandLine { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// CLI durum değişti event args;
    /// </summary>
    public class CLIStateChangedEventArgs : EventArgs;
    {
        public CLIState OldState { get; set; }
        public CLIState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Oturum event args;
    /// </summary>
    public class SessionEventArgs : EventArgs;
    {
        public CLISession Session { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Çıktı event args;
    /// </summary>
    public class OutputEventArgs : EventArgs;
    {
        public string Output { get; set; }
        public OutputType OutputType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// CLI plugin interface;
    /// </summary>
    public interface ICLIPlugin : IDisposable
    {
        string Name { get; }
        string Version { get; }
        void Initialize(CLIEngine engine);
        Task<ParsedCommand> PreprocessCommandAsync(ParsedCommand command);
        Task<CommandResult> PostprocessResultAsync(CommandResult result);
    }

    /// <summary>
    /// Otomatik tamamlama motoru;
    /// </summary>
    public class AutoCompleteEngine;
    {
        private readonly HashSet<string> _commands = new HashSet<string>();
        private readonly List<string> _suggestions = new List<string>();

        public void RegisterCommand(string commandName)
        {
            _commands.Add(commandName);
        }

        public List<string> GetSuggestions(string partialCommand)
        {
            _suggestions.Clear();

            if (string.IsNullOrEmpty(partialCommand))
                return _suggestions;

            foreach (var command in _commands)
            {
                if (command.StartsWith(partialCommand, StringComparison.OrdinalIgnoreCase))
                {
                    _suggestions.Add(command.Substring(partialCommand.Length));
                }
            }

            return _suggestions;
        }
    }

    /// <summary>
    /// CLI engine interface;
    /// </summary>
    public interface ICLIEngine : IDisposable
    {
        bool IsRunning { get; }
        CLIState CurrentState { get; }
        CLIConfiguration Configuration { get; set; }
        CLISession CurrentSession { get; }
        IReadOnlyList<string> CommandHistory { get; }
        IReadOnlyDictionary<string, object> EnvironmentVariables { get; }

        event EventHandler<CommandExecutedEventArgs> CommandExecuted;
        event EventHandler<CommandErrorEventArgs> CommandError;
        event EventHandler<CLIStateChangedEventArgs> StateChanged;
        event EventHandler<SessionEventArgs> SessionStarted;
        event EventHandler<SessionEventArgs> SessionEnded;
        event EventHandler<OutputEventArgs> OutputGenerated;

        void Start();
        void Stop();
        Task<CommandResult> ExecuteCommandAsync(string commandLine, CancellationToken cancellationToken = default);
        Task<BatchResult> ExecuteBatchAsync(IEnumerable<string> commands, BatchOptions options = null, CancellationToken cancellationToken = default);
        Task<ScriptResult> ExecuteScriptAsync(string scriptPath, ScriptOptions options = null, CancellationToken cancellationToken = default);
        bool RegisterCommand(string commandName, Func<string[], string> commandHandler);
        bool RegisterPlugin(ICLIPlugin plugin);
        void SetEnvironmentVariable(string name, object value);
        object GetEnvironmentVariable(string name);
        string GetPrompt();
        List<string> GetAutoCompleteSuggestions(string partialCommand);
        void ClearHistory();
        string GetHistoryCommand(bool forward);
        void Exit();
        void ChangeState(CLIState newState);
        void WriteOutput(string output, OutputType outputType = OutputType.Normal);
        void WriteLine(string line = "", OutputType outputType = OutputType.Normal);
        void ShowHelp(string command = null);
    }

    /// <summary>
    /// CLI engine exception;
    /// </summary>
    public class CLIEngineException : Exception
    {
        public CLIEngineException(string message) : base(message) { }
        public CLIEngineException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
