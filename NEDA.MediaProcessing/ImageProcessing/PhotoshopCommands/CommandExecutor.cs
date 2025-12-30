using NEDA.API.Middleware;
using NEDA.API.Versioning;
using NEDA.Automation.Executors;
using NEDA.Brain.DecisionMaking;
using NEDA.Brain.IntentRecognition;
using NEDA.Brain.IntentRecognition.CommandParser;
using NEDA.Build.CI_CD;
using NEDA.Commands.Attributes;
using NEDA.Commands.Interfaces;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.Security;
using NEDA.ExceptionHandling;
using NEDA.GameDesign.GameplayDesign.MechanicsDesign;
using NEDA.Logging;
using NEDA.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Commands;
{
    /// <summary>
    /// Komut yürütme motoru - tüm komutların merkezi yürütücüsü;
    /// </summary>
    public class CommandExecutor : ICommandExecutor, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly ISecurityManager _securityManager;
        private readonly IPermissionValidator _permissionValidator;
        private readonly IIntentDetector _intentDetector;
        private readonly ICommandRegistry _commandRegistry
        private readonly IRiskAnalyzer _riskAnalyzer;
        private readonly ITaskExecutor _taskExecutor;

        private readonly Dictionary<string, CommandExecutionResult> _executionHistory;
        private readonly Dictionary<Guid, CommandSession> _activeSessions;
        private readonly Dictionary<string, CommandPerformanceMetrics> _performanceMetrics;
        private readonly CommandCache _commandCache;
        private readonly CommandPipeline _executionPipeline;
        private readonly CommandScheduler _commandScheduler;

        private readonly object _lockObject = new object();
        private readonly SemaphoreSlim _concurrencySemaphore;
        private bool _isProcessing;
        private int _maxConcurrentCommands;
        private DateTime _lastCleanupTime;

        private const int MAX_HISTORY_SIZE = 10000;
        private const int SESSION_TIMEOUT_MINUTES = 30;
        private const int DEFAULT_CONCURRENCY_LIMIT = 10;
        private const int CACHE_SIZE_MB = 500;
        private const int MAX_COMMAND_TIMEOUT_MS = 300000; // 5 dakika;

        #endregion;

        #region Properties;

        /// <summary>
        /// Aktif komut sayısı;
        /// </summary>
        public int ActiveCommandCount;
        {
            get;
            {
                lock (_lockObject)
                {
                    return _activeSessions.Count(s => s.Value.Status == CommandSessionStatus.Running);
                }
            }
        }

        /// <summary>
        /// Toplam yürütülen komut sayısı;
        /// </summary>
        public int TotalExecutions => _executionHistory.Count;

        /// <summary>
        /// Başarılı yürütme oranı (%)
        /// </summary>
        public float SuccessRate;
        {
            get;
            {
                if (_executionHistory.Count == 0)
                    return 0;

                var successful = _executionHistory.Count(r => r.Value.Status == CommandStatus.Completed);
                return (successful * 100.0f) / _executionHistory.Count;
            }
        }

        /// <summary>
        /// Maksimum eşzamanlı komut sayısı;
        /// </summary>
        public int MaxConcurrentCommands;
        {
            get => _maxConcurrentCommands;
            set;
            {
                if (value < 1)
                    throw new ArgumentException("Max concurrent commands must be at least 1");

                _maxConcurrentCommands = value;
                _concurrencySemaphore?.Dispose();
                _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentCommands, _maxConcurrentCommands);

                _logger.Info($"Max concurrent commands set to {value}", GetType().Name);
            }
        }

        /// <summary>
        /// Varsayılan yürütme zaman aşımı (ms)
        /// </summary>
        public int DefaultTimeout { get; set; } = 60000;

        /// <summary>
        /// Otomatik temizleme etkin;
        /// </summary>
        public bool AutoCleanupEnabled { get; set; } = true;

        /// <summary>
        /// Performans izleme etkin;
        /// </summary>
        public bool PerformanceMonitoringEnabled { get; set; } = true;

        /// <summary>
        /// Komut önbelleği etkin;
        /// </summary>
        public bool CommandCachingEnabled { get; set; } = true;

        /// <summary>
        /// Gerçek zamanlı yürütme etkin;
        /// </summary>
        public bool RealTimeExecutionEnabled { get; set; } = true;

        /// <summary>
        /// Risk analizi etkin;
        /// </summary>
        public bool RiskAnalysisEnabled { get; set; } = true;

        /// <summary>
        /// Hata tolerans modu;
        /// </summary>
        public ErrorToleranceMode ErrorTolerance { get; set; } = ErrorToleranceMode.Strict;

        #endregion;

        #region Events;

        /// <summary>
        /// Komut yürütme başladığında tetiklenen event;
        /// </summary>
        public event EventHandler<CommandExecutionStartedEventArgs> CommandExecutionStarted;

        /// <summary>
        /// Komut yürütme tamamlandığında tetiklenen event;
        /// </summary>
        public event EventHandler<CommandExecutionCompletedEventArgs> CommandExecutionCompleted;

        /// <summary>
        /// Komut yürütme başarısız olduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<CommandExecutionFailedEventArgs> CommandExecutionFailed;

        /// <summary>
        /// Komut iptal edildiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<CommandExecutionCancelledEventArgs> CommandExecutionCancelled;

        /// <summary>
        /// Komut zaman aşımına uğradığında tetiklenen event;
        /// </summary>
        public event EventHandler<CommandExecutionTimedOutEventArgs> CommandExecutionTimedOut;

        /// <summary>
        /// Komut durumu değiştiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<CommandStatusChangedEventArgs> CommandStatusChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// CommandExecutor sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public CommandExecutor(
            ILogger logger,
            IErrorHandler errorHandler,
            ISecurityManager securityManager,
            IPermissionValidator permissionValidator,
            IIntentDetector intentDetector,
            ICommandRegistry commandRegistry,
            IRiskAnalyzer riskAnalyzer = null,
            ITaskExecutor taskExecutor = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _permissionValidator = permissionValidator ?? throw new ArgumentNullException(nameof(permissionValidator));
            _intentDetector = intentDetector ?? throw new ArgumentNullException(nameof(intentDetector));
            _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
            _riskAnalyzer = riskAnalyzer;
            _taskExecutor = taskExecutor;

            _executionHistory = new Dictionary<string, CommandExecutionResult>();
            _activeSessions = new Dictionary<Guid, CommandSession>();
            _performanceMetrics = new Dictionary<string, CommandPerformanceMetrics>();
            _commandCache = new CommandCache(CACHE_SIZE_MB);
            _executionPipeline = new CommandPipeline();
            _commandScheduler = new CommandScheduler();

            _maxConcurrentCommands = DEFAULT_CONCURRENCY_LIMIT;
            _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentCommands, _maxConcurrentCommands);
            _lastCleanupTime = DateTime.Now;

            InitializeExecutionPipeline();

            _logger.Info("CommandExecutor initialized", GetType().Name);
        }

        #endregion;

        #region Public Methods - Command Execution;

        /// <summary>
        /// Komutu yürütür;
        /// </summary>
        /// <param name="command">Yürütülecek komut</param>
        /// <param name="context">Yürütme bağlamı</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Yürütme sonucu</returns>
        public async Task<CommandExecutionResult> ExecuteCommandAsync(
            ICommand command,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            ValidateCommand(command);
            context ??= CreateDefaultContext();

            // Eşzamanlılık kontrolü;
            await _concurrencySemaphore.WaitAsync(cancellationToken);

            CommandSession session = null;
            CommandExecutionResult result = null;

            try
            {
                // Oturum oluştur;
                session = CreateCommandSession(command, context);

                // Tarihçe anahtarı;
                var historyKey = GenerateHistoryKey(command, context);

                lock (_lockObject)
                {
                    _activeSessions[session.SessionId] = session;
                }

                OnCommandExecutionStarted(new CommandExecutionStartedEventArgs(session));

                // Yürütme işlemi;
                result = await ExecuteCommandInternalAsync(command, context, session, cancellationToken);

                // Sonuç kaydet;
                lock (_lockObject)
                {
                    _executionHistory[historyKey] = result;

                    if (_executionHistory.Count > MAX_HISTORY_SIZE)
                    {
                        CleanupOldHistory();
                    }
                }

                // Performans metriklerini güncelle;
                if (PerformanceMonitoringEnabled)
                {
                    UpdatePerformanceMetrics(command, result);
                }

                // Durum event'ini tetikle;
                OnCommandExecutionCompleted(new CommandExecutionCompletedEventArgs(result, session));

                return result;
            }
            catch (OperationCanceledException)
            {
                var cancelResult = CreateCancelledResult(command, context, session);
                OnCommandExecutionCancelled(new CommandExecutionCancelledEventArgs(cancelResult, session));
                return cancelResult;
            }
            catch (Exception ex)
            {
                result = CreateFailedResult(command, context, session, ex);
                OnCommandExecutionFailed(new CommandExecutionFailedEventArgs(result, session, ex));
                return result;
            }
            finally
            {
                // Oturumu temizle;
                if (session != null)
                {
                    CleanupSession(session);
                }

                _concurrencySemaphore.Release();

                // Otomatik temizleme;
                if (AutoCleanupEnabled && (DateTime.Now - _lastCleanupTime).TotalMinutes > 30)
                {
                    PerformCleanup();
                }
            }
        }

        /// <summary>
        /// Komut metnini yürütür (doğal dil işleme ile)
        /// </summary>
        /// <param name="commandText">Komut metni</param>
        /// <param name="context">Yürütme bağlamı</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Yürütme sonucu</returns>
        public async Task<CommandExecutionResult> ExecuteCommandTextAsync(
            string commandText,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(commandText))
                throw new ArgumentException("Command text cannot be null or empty", nameof(commandText));

            context ??= CreateDefaultContext();

            try
            {
                // Niyet tespiti;
                var intentResult = await _intentDetector.DetectIntentAsync(commandText, context, cancellationToken);

                if (!intentResult.Success || intentResult.CommandType == null)
                {
                    return CreateFailedResultFromIntent(intentResult, context);
                }

                // Komut oluştur;
                var command = await CreateCommandFromIntentAsync(intentResult, context, cancellationToken);

                if (command == null)
                {
                    return CreateCommandNotFoundResult(intentResult, context);
                }

                // Komutu yürüt;
                return await ExecuteCommandAsync(command, context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to execute command text: {ex.Message}", GetType().Name);
                return CreateFailedResultFromException(ex, commandText, context);
            }
        }

        /// <summary>
        /// Birden fazla komutu sırayla yürütür;
        /// </summary>
        public async Task<BatchExecutionResult> ExecuteCommandsAsync(
            IEnumerable<ICommand> commands,
            ExecutionContext context = null,
            BatchExecutionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));

            var commandList = commands.ToList();
            if (commandList.Count == 0)
                throw new ArgumentException("Command list cannot be empty", nameof(commands));

            context ??= CreateDefaultContext();
            options ??= new BatchExecutionOptions();

            var result = new BatchExecutionResult;
            {
                StartTime = DateTime.Now,
                TotalCommands = commandList.Count,
                Options = options;
            };

            try
            {
                // Batch pipeline'ı yapılandır;
                var pipeline = ConfigureBatchPipeline(options);

                foreach (var command in commandList)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Status = BatchExecutionStatus.Cancelled;
                        break;
                    }

                    try
                    {
                        // Pipeline'dan geçir;
                        var processedCommand = await pipeline.ProcessCommandAsync(command, context, cancellationToken);

                        // Komutu yürüt;
                        var commandResult = await ExecuteCommandAsync(processedCommand, context, cancellationToken);

                        result.CommandResults.Add(commandResult);

                        if (commandResult.Status == CommandStatus.Completed)
                        {
                            result.SuccessfulCommands++;
                        }
                        else if (commandResult.Status == CommandStatus.Failed)
                        {
                            result.FailedCommands++;
                            result.Errors.Add(new BatchExecutionError;
                            {
                                Command = command,
                                Error = commandResult.Error,
                                Timestamp = DateTime.Now;
                            });

                            if (options.StopOnFailure)
                            {
                                result.Status = BatchExecutionStatus.PartialFailure;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.FailedCommands++;
                        result.Errors.Add(new BatchExecutionError;
                        {
                            Command = command,
                            Error = ex.Message,
                            Timestamp = DateTime.Now;
                        });

                        if (options.StopOnFailure)
                        {
                            result.Status = BatchExecutionStatus.Failed;
                            break;
                        }
                    }
                }

                result.EndTime = DateTime.Now;
                result.Status = result.Status == BatchExecutionStatus.Running;
                    ? (result.FailedCommands == 0 ? BatchExecutionStatus.Completed : BatchExecutionStatus.PartialFailure)
                    : result.Status;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Batch execution failed: {ex.Message}", GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Komut komut dosyasını yürütür;
        /// </summary>
        public async Task<ScriptExecutionResult> ExecuteScriptAsync(
            CommandScript script,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (script == null)
                throw new ArgumentNullException(nameof(script));

            ValidateScript(script);
            context ??= CreateDefaultContext();

            var result = new ScriptExecutionResult;
            {
                Script = script,
                StartTime = DateTime.Now;
            };

            try
            {
                // Script doğrulama;
                if (!await ValidateScriptAsync(script, context, cancellationToken))
                {
                    result.Status = ScriptExecutionStatus.ValidationFailed;
                    result.Error = "Script validation failed";
                    return result;
                }

                // Her komutu yürüt;
                foreach (var command in script.Commands)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Status = ScriptExecutionStatus.Cancelled;
                        break;
                    }

                    var commandResult = await ExecuteCommandAsync(command, context, cancellationToken);
                    result.CommandResults.Add(commandResult);

                    if (commandResult.Status == CommandStatus.Failed && script.StopOnError)
                    {
                        result.Status = ScriptExecutionStatus.Failed;
                        result.Error = $"Command failed: {commandResult.Command.Name}";
                        break;
                    }
                }

                result.EndTime = DateTime.Now;
                result.Status = result.Status == ScriptExecutionStatus.Running;
                    ? ScriptExecutionStatus.Completed;
                    : result.Status;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script execution failed: {ex.Message}", GetType().Name);

                result.Status = ScriptExecutionStatus.Failed;
                result.Error = ex.Message;
                result.EndTime = DateTime.Now;

                return result;
            }
        }

        /// <summary>
        /// Komutu planlanmış olarak çalıştırır;
        /// </summary>
        public ScheduledCommand ScheduleCommand(
            ICommand command,
            DateTime scheduledTime,
            ExecutionContext context = null,
            ScheduleOptions options = null)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (scheduledTime <= DateTime.Now)
                throw new ArgumentException("Scheduled time must be in the future", nameof(scheduledTime));

            context ??= CreateDefaultContext();
            options ??= new ScheduleOptions();

            var scheduledCommand = new ScheduledCommand;
            {
                Command = command,
                Context = context,
                ScheduledTime = scheduledTime,
                Options = options,
                Status = ScheduledCommandStatus.Pending;
            };

            _commandScheduler.Schedule(scheduledCommand);

            _logger.Info($"Command scheduled: {command.Name} at {scheduledTime}", GetType().Name);

            return scheduledCommand;
        }

        /// <summary>
        /// Yürütmeyi iptal eder;
        /// </summary>
        public bool CancelExecution(Guid sessionId)
        {
            lock (_lockObject)
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                    return false;

                if (session.CancellationTokenSource != null && !session.CancellationTokenSource.IsCancellationRequested)
                {
                    session.CancellationTokenSource.Cancel();
                    session.Status = CommandSessionStatus.Cancelling;

                    OnCommandStatusChanged(new CommandStatusChangedEventArgs;
                    {
                        SessionId = sessionId,
                        OldStatus = CommandSessionStatus.Running,
                        NewStatus = CommandSessionStatus.Cancelling,
                        Timestamp = DateTime.Now;
                    });

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Tüm aktif yürütmeleri iptal eder;
        /// </summary>
        public void CancelAllExecutions()
        {
            List<Guid> sessionIds;

            lock (_lockObject)
            {
                sessionIds = _activeSessions.Keys.ToList();
            }

            foreach (var sessionId in sessionIds)
            {
                CancelExecution(sessionId);
            }

            _logger.Info("All command executions cancelled", GetType().Name);
        }

        #endregion;

        #region Public Methods - Command Management;

        /// <summary>
        /// Komutun geçerliliğini doğrular;
        /// </summary>
        public async Task<CommandValidationResult> ValidateCommandAsync(
            ICommand command,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            context ??= CreateDefaultContext();

            var result = new CommandValidationResult;
            {
                Command = command,
                Timestamp = DateTime.Now;
            };

            try
            {
                // 1. Komut varlık kontrolü;
                if (!_commandRegistry.IsCommandRegistered(command.GetType()))
                {
                    result.IsValid = false;
                    result.Errors.Add("Command is not registered in the registry");
                    return result;
                }

                // 2. İzin kontrolü;
                var permissionResult = await _permissionValidator.ValidateCommandPermissionAsync(
                    command, context.User, cancellationToken);

                if (!permissionResult.IsAuthorized)
                {
                    result.IsValid = false;
                    result.Errors.Add($"Permission denied: {permissionResult.DeniedReason}");
                    result.ValidationType |= CommandValidationType.Permission;
                }

                // 3. Risk analizi;
                if (RiskAnalysisEnabled && _riskAnalyzer != null)
                {
                    var riskResult = await _riskAnalyzer.AnalyzeCommandRiskAsync(command, context, cancellationToken);
                    result.RiskAssessment = riskResult;

                    if (riskResult.RiskLevel == RiskLevel.High || riskResult.RiskLevel == RiskLevel.Critical)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"High risk command: {riskResult.RiskDescription}");
                        result.ValidationType |= CommandValidationType.Risk;
                    }
                }

                // 4. Parametre doğrulama;
                var parameterErrors = ValidateCommandParameters(command);
                if (parameterErrors.Any())
                {
                    result.IsValid = false;
                    result.Errors.AddRange(parameterErrors);
                    result.ValidationType |= CommandValidationType.Parameters;
                }

                // 5. Bağımlılık kontrolü;
                var dependencyErrors = await ValidateCommandDependenciesAsync(command, context, cancellationToken);
                if (dependencyErrors.Any())
                {
                    result.IsValid = false;
                    result.Errors.AddRange(dependencyErrors);
                    result.ValidationType |= CommandValidationType.Dependencies;
                }

                result.IsValid = result.Errors.Count == 0;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Command validation failed: {ex.Message}", GetType().Name);

                result.IsValid = false;
                result.Errors.Add($"Validation error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Komut istatistiklerini alır;
        /// </summary>
        public CommandStatistics GetCommandStatistics(string commandName = null)
        {
            var stats = new CommandStatistics;
            {
                TotalExecutions = _executionHistory.Count,
                ActiveSessions = _activeSessions.Count,
                CacheHitRate = _commandCache.HitRate,
                CacheSize = _commandCache.SizeMB;
            };

            if (!string.IsNullOrEmpty(commandName))
            {
                var commandResults = _executionHistory;
                    .Where(r => r.Value.Command.Name == commandName)
                    .Select(r => r.Value)
                    .ToList();

                stats.CommandName = commandName;
                stats.CommandExecutions = commandResults.Count;
                stats.SuccessfulExecutions = commandResults.Count(r => r.Status == CommandStatus.Completed);
                stats.FailedExecutions = commandResults.Count(r => r.Status == CommandStatus.Failed);
                stats.AverageExecutionTime = commandResults.Any()
                    ? commandResults.Average(r => r.ExecutionTime.TotalMilliseconds)
                    : 0;
            }
            else;
            {
                stats.SuccessfulExecutions = _executionHistory.Count(r => r.Value.Status == CommandStatus.Completed);
                stats.FailedExecutions = _executionHistory.Count(r => r.Value.Status == CommandStatus.Failed);
                stats.AverageExecutionTime = _executionHistory.Any()
                    ? _executionHistory.Average(r => r.Value.ExecutionTime.TotalMilliseconds)
                    : 0;
            }

            return stats;
        }

        /// <summary>
        /// Komut performans metriklerini alır;
        /// </summary>
        public CommandPerformanceMetrics GetPerformanceMetrics(string commandName)
        {
            if (string.IsNullOrEmpty(commandName))
                throw new ArgumentException("Command name cannot be null or empty", nameof(commandName));

            lock (_lockObject)
            {
                if (_performanceMetrics.TryGetValue(commandName, out var metrics))
                    return metrics.Clone();
            }

            return new CommandPerformanceMetrics { CommandName = commandName };
        }

        /// <summary>
        /// Komut önbelleğini temizler;
        /// </summary>
        public void ClearCommandCache()
        {
            _commandCache.Clear();
            _logger.Info("Command cache cleared", GetType().Name);
        }

        /// <summary>
        /// Yürütme geçmişini temizler;
        /// </summary>
        public void ClearExecutionHistory()
        {
            lock (_lockObject)
            {
                _executionHistory.Clear();
                _performanceMetrics.Clear();
            }

            _logger.Info("Execution history cleared", GetType().Name);
        }

        /// <summary>
        /// Yürütme pipeline'ını yapılandırır;
        /// </summary>
        public void ConfigurePipeline(PipelineConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            _executionPipeline.Configure(configuration);
            _logger.Info("Execution pipeline configured", GetType().Name);
        }

        #endregion;

        #region Private Methods - Core Execution;

        /// <summary>
        /// Komutu dahili olarak yürütür;
        /// </summary>
        private async Task<CommandExecutionResult> ExecuteCommandInternalAsync(
            ICommand command,
            ExecutionContext context,
            CommandSession session,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;
            CommandExecutionResult result = null;

            try
            {
                // 1. Doğrulama;
                var validationResult = await ValidateCommandAsync(command, context, cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new CommandValidationException("Command validation failed", validationResult);
                }

                // 2. Önbellek kontrolü;
                if (CommandCachingEnabled)
                {
                    var cacheKey = GenerateCacheKey(command, context);
                    if (_commandCache.TryGet(cacheKey, out var cachedResult))
                    {
                        _logger.Debug($"Command retrieved from cache: {command.Name}", GetType().Name);

                        cachedResult = cachedResult.Clone();
                        cachedResult.Cached = true;
                        cachedResult.CacheKey = cacheKey;
                        cachedResult.ExecutionTime = DateTime.Now - startTime;

                        return cachedResult;
                    }
                }

                // 3. Pipeline işleme;
                var processedCommand = await _executionPipeline.ProcessCommandAsync(command, context, cancellationToken);

                // 4. Zaman aşımı yapılandırması;
                var timeout = GetCommandTimeout(command);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                // 5. Komut yürütme;
                OnCommandStatusChanged(new CommandStatusChangedEventArgs;
                {
                    SessionId = session.SessionId,
                    OldStatus = CommandSessionStatus.Validated,
                    NewStatus = CommandSessionStatus.Running,
                    Timestamp = DateTime.Now;
                });

                object commandResult = null;

                if (RealTimeExecutionEnabled)
                {
                    commandResult = await processedCommand.ExecuteAsync(context, timeoutCts.Token);
                }
                else;
                {
                    // Simülasyon modu;
                    commandResult = await SimulateCommandExecutionAsync(processedCommand, context, timeoutCts.Token);
                }

                // 6. Sonuç oluşturma;
                var executionTime = DateTime.Now - startTime;
                result = CreateSuccessResult(processedCommand, context, session, commandResult, executionTime);

                // 7. Önbelleğe ekle;
                if (CommandCachingEnabled && result.CanBeCached)
                {
                    var cacheKey = GenerateCacheKey(command, context);
                    _commandCache.Add(cacheKey, result);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                var executionTime = DateTime.Now - startTime;
                result = CreateCancelledResult(command, context, session);
                result.ExecutionTime = executionTime;

                throw;
            }
            catch (CommandTimeoutException)
            {
                var executionTime = DateTime.Now - startTime;
                result = CreateTimeoutResult(command, context, session, executionTime);

                OnCommandExecutionTimedOut(new CommandExecutionTimedOutEventArgs(result, session));

                return result;
            }
            catch (Exception ex)
            {
                var executionTime = DateTime.Now - startTime;

                // Hata tolerans moduna göre işle;
                if (ErrorTolerance == ErrorToleranceMode.Tolerant)
                {
                    result = CreatePartialSuccessResult(command, context, session, ex, executionTime);
                    _logger.Warning($"Command executed with errors (tolerant mode): {command.Name}", GetType().Name);
                }
                else;
                {
                    throw;
                }

                return result;
            }
        }

        /// <summary>
        /// Komut oturumu oluşturur;
        /// </summary>
        private CommandSession CreateCommandSession(ICommand command, ExecutionContext context)
        {
            var session = new CommandSession;
            {
                SessionId = Guid.NewGuid(),
                Command = command,
                Context = context,
                StartTime = DateTime.Now,
                Status = CommandSessionStatus.Created,
                CancellationTokenSource = new CancellationTokenSource()
            };

            // Meta veriler;
            session.Metadata.Add("User", context.User?.Identity?.Name ?? "System");
            session.Metadata.Add("CommandType", command.GetType().Name);
            session.Metadata.Add("Priority", context.Priority.ToString());

            return session;
        }

        /// <summary>
        /// Başarılı sonuç oluşturur;
        /// </summary>
        private CommandExecutionResult CreateSuccessResult(
            ICommand command,
            ExecutionContext context,
            CommandSession session,
            object result,
            TimeSpan executionTime)
        {
            return new CommandExecutionResult;
            {
                Command = command,
                Status = CommandStatus.Completed,
                Result = result,
                Error = null,
                ExecutionTime = executionTime,
                StartTime = session.StartTime,
                EndTime = DateTime.Now,
                SessionId = session.SessionId,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    { "Success", true },
                    { "ExecutionMode", RealTimeExecutionEnabled ? "RealTime" : "Simulation" }
                }
            };
        }

        /// <summary>
        /// Başarısız sonuç oluşturur;
        /// </summary>
        private CommandExecutionResult CreateFailedResult(
            ICommand command,
            ExecutionContext context,
            CommandSession session,
            Exception exception)
        {
            return new CommandExecutionResult;
            {
                Command = command,
                Status = CommandStatus.Failed,
                Result = null,
                Error = exception.Message,
                Exception = exception,
                ExecutionTime = DateTime.Now - (session?.StartTime ?? DateTime.Now),
                StartTime = session?.StartTime ?? DateTime.Now,
                EndTime = DateTime.Now,
                SessionId = session?.SessionId ?? Guid.Empty,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    { "Success", false },
                    { "ExceptionType", exception.GetType().Name }
                }
            };
        }

        /// <summary>
        /// İptal edilmiş sonuç oluşturur;
        /// </summary>
        private CommandExecutionResult CreateCancelledResult(
            ICommand command,
            ExecutionContext context,
            CommandSession session)
        {
            return new CommandExecutionResult;
            {
                Command = command,
                Status = CommandStatus.Cancelled,
                Result = null,
                Error = "Command execution was cancelled",
                ExecutionTime = DateTime.Now - (session?.StartTime ?? DateTime.Now),
                StartTime = session?.StartTime ?? DateTime.Now,
                EndTime = DateTime.Now,
                SessionId = session?.SessionId ?? Guid.Empty,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    { "Success", false },
                    { "Cancelled", true }
                }
            };
        }

        /// <summary>
        /// Zaman aşımı sonucu oluşturur;
        /// </summary>
        private CommandExecutionResult CreateTimeoutResult(
            ICommand command,
            ExecutionContext context,
            CommandSession session,
            TimeSpan executionTime)
        {
            return new CommandExecutionResult;
            {
                Command = command,
                Status = CommandStatus.TimedOut,
                Result = null,
                Error = $"Command execution timed out after {executionTime.TotalSeconds} seconds",
                ExecutionTime = executionTime,
                StartTime = session?.StartTime ?? DateTime.Now,
                EndTime = DateTime.Now,
                SessionId = session?.SessionId ?? Guid.Empty,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    { "Success", false },
                    { "TimedOut", true },
                    { "TimeoutDuration", executionTime }
                }
            };
        }

        /// <summary>
        /// Kısmi başarı sonucu oluşturur;
        /// </summary>
        private CommandExecutionResult CreatePartialSuccessResult(
            ICommand command,
            ExecutionContext context,
            CommandSession session,
            Exception exception,
            TimeSpan executionTime)
        {
            return new CommandExecutionResult;
            {
                Command = command,
                Status = CommandStatus.PartialSuccess,
                Result = null,
                Error = $"Command executed with errors: {exception.Message}",
                Exception = exception,
                ExecutionTime = executionTime,
                StartTime = session?.StartTime ?? DateTime.Now,
                EndTime = DateTime.Now,
                SessionId = session?.SessionId ?? Guid.Empty,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    { "Success", false },
                    { "PartialSuccess", true },
                    { "ErrorTolerance", ErrorToleranceMode.Tolerant.ToString() }
                }
            };
        }

        #endregion;

        #region Private Methods - Validation;

        /// <summary>
        /// Komut parametrelerini doğrular;
        /// </summary>
        private List<string> ValidateCommandParameters(ICommand command)
        {
            var errors = new List<string>();
            var properties = command.GetType().GetProperties();

            foreach (var property in properties)
            {
                var attributes = property.GetCustomAttributes<ParameterValidationAttribute>(true);
                foreach (var attribute in attributes)
                {
                    var value = property.GetValue(command);
                    if (!attribute.IsValid(value, out var error))
                    {
                        errors.Add($"Parameter validation failed for {property.Name}: {error}");
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// Komut bağımlılıklarını doğrular;
        /// </summary>
        private async Task<List<string>> ValidateCommandDependenciesAsync(
            ICommand command,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            var errors = new List<string>();

            // 1. Registry bağımlılıkları;
            var dependencies = _commandRegistry.GetCommandDependencies(command.GetType());
            foreach (var dependency in dependencies)
            {
                if (!_commandRegistry.IsCommandRegistered(dependency))
                {
                    errors.Add($"Required command dependency not found: {dependency.Name}");
                }
            }

            // 2. Sistem bağımlılıkları;
            if (command is ISystemDependent systemDependent)
            {
                var systemChecks = await systemDependent.CheckSystemDependenciesAsync(context, cancellationToken);
                if (!systemChecks.All(c => c.IsSatisfied))
                {
                    var failedChecks = systemChecks.Where(c => !c.IsSatisfied);
                    errors.AddRange(failedChecks.Select(c => $"System dependency failed: {c.DependencyName} - {c.FailureReason}"));
                }
            }

            return errors;
        }

        /// <summary>
        /// Komut script'ini doğrular;
        /// </summary>
        private async Task<bool> ValidateScriptAsync(
            CommandScript script,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            // Script seviyesi doğrulamalar;
            if (script.Commands.Count == 0)
            {
                _logger.Error("Script contains no commands", GetType().Name);
                return false;
            }

            if (script.MaxExecutionTime.HasValue && script.MaxExecutionTime.Value <= TimeSpan.Zero)
            {
                _logger.Error("Invalid max execution time", GetType().Name);
                return false;
            }

            // Her komutu doğrula;
            foreach (var command in script.Commands)
            {
                var validationResult = await ValidateCommandAsync(command, context, cancellationToken);
                if (!validationResult.IsValid)
                {
                    _logger.Error($"Script validation failed for command {command.Name}: {string.Join(", ", validationResult.Errors)}", GetType().Name);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Komutun geçerliliğini doğrular;
        /// </summary>
        private void ValidateCommand(ICommand command)
        {
            if (string.IsNullOrEmpty(command.Name))
                throw new ArgumentException("Command name cannot be null or empty");

            if (string.IsNullOrEmpty(command.Description))
                throw new ArgumentException("Command description cannot be null or empty");
        }

        /// <summary>
        /// Script'in geçerliliğini doğrular;
        /// </summary>
        private void ValidateScript(CommandScript script)
        {
            if (string.IsNullOrEmpty(script.Name))
                throw new ArgumentException("Script name cannot be null or empty");

            if (script.Commands == null || script.Commands.Count == 0)
                throw new ArgumentException("Script must contain at least one command");
        }

        #endregion;

        #region Private Methods - Utility;

        /// <summary>
        /// Varsayılan bağlam oluşturur;
        /// </summary>
        private ExecutionContext CreateDefaultContext()
        {
            return new ExecutionContext;
            {
                User = _securityManager.CurrentUser,
                Priority = ExecutionPriority.Normal,
                ExecutionMode = RealTimeExecutionEnabled ? ExecutionMode.RealTime : ExecutionMode.Simulation,
                Timestamp = DateTime.Now,
                CorrelationId = Guid.NewGuid()
            };
        }

        /// <summary>
        /// Komut zaman aşımını alır;
        /// </summary>
        private int GetCommandTimeout(ICommand command)
        {
            var timeoutAttribute = command.GetType().GetCustomAttribute<CommandTimeoutAttribute>();
            return timeoutAttribute?.TimeoutMilliseconds ?? DefaultTimeout;
        }

        /// <summary>
        /// Tarihçe anahtarı oluşturur;
        /// </summary>
        private string GenerateHistoryKey(ICommand command, ExecutionContext context)
        {
            return $"{command.GetType().FullName}_{context.CorrelationId}_{DateTime.Now.Ticks}";
        }

        /// <summary>
        /// Önbellek anahtarı oluşturur;
        /// </summary>
        private string GenerateCacheKey(ICommand command, ExecutionContext context)
        {
            // Komut hash'i + bağlam bilgileri;
            var commandHash = command.GetHashCode();
            var contextHash = context.GetHashCode();
            return $"{commandHash}_{contextHash}_{command.Name}";
        }

        /// <summary>
        /// Oturumu temizler;
        /// </summary>
        private void CleanupSession(CommandSession session)
        {
            lock (_lockObject)
            {
                if (_activeSessions.ContainsKey(session.SessionId))
                {
                    session.EndTime = DateTime.Now;
                    session.Status = CommandSessionStatus.Completed;
                    session.CancellationTokenSource?.Dispose();

                    _activeSessions.Remove(session.SessionId);
                }
            }
        }

        /// <summary>
        /// Eski tarihçeyi temizler;
        /// </summary>
        private void CleanupOldHistory()
        {
            var toRemove = _executionHistory;
                .OrderBy(e => e.Value.EndTime)
                .Take(_executionHistory.Count - MAX_HISTORY_SIZE)
                .Select(e => e.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _executionHistory.Remove(key);
            }

            _logger.Debug($"Cleaned up {toRemove.Count} old history entries", GetType().Name);
        }

        /// <summary>
        /// Performans metriklerini günceller;
        /// </summary>
        private void UpdatePerformanceMetrics(ICommand command, CommandExecutionResult result)
        {
            var commandName = command.Name;

            lock (_lockObject)
            {
                if (!_performanceMetrics.TryGetValue(commandName, out var metrics))
                {
                    metrics = new CommandPerformanceMetrics { CommandName = commandName };
                    _performanceMetrics[commandName] = metrics;
                }

                metrics.TotalExecutions++;
                metrics.TotalExecutionTime += result.ExecutionTime;

                if (result.Status == CommandStatus.Completed)
                {
                    metrics.SuccessfulExecutions++;
                    metrics.LastSuccessfulExecution = result.EndTime;
                }
                else if (result.Status == CommandStatus.Failed)
                {
                    metrics.FailedExecutions++;
                    metrics.LastFailedExecution = result.EndTime;
                }

                if (result.ExecutionTime > metrics.SlowestExecution)
                {
                    metrics.SlowestExecution = result.ExecutionTime;
                }

                if (metrics.FastestExecution == TimeSpan.Zero || result.ExecutionTime < metrics.FastestExecution)
                {
                    metrics.FastestExecution = result.ExecutionTime;
                }

                metrics.AverageExecutionTime = metrics.TotalExecutionTime / metrics.TotalExecutions;
                metrics.SuccessRate = (metrics.SuccessfulExecutions * 100.0f) / metrics.TotalExecutions;
            }
        }

        /// <summary>
        /// Temizleme işlemi yapar;
        /// </summary>
        private void PerformCleanup()
        {
            try
            {
                // Zaman aşımına uğramış oturumları temizle;
                var timedOutSessions = _activeSessions;
                    .Where(s => (DateTime.Now - s.Value.StartTime).TotalMinutes > SESSION_TIMEOUT_MINUTES)
                    .ToList();

                foreach (var session in timedOutSessions)
                {
                    CancelExecution(session.Key);
                }

                // Önbelleği temizle;
                _commandCache.Cleanup();

                _lastCleanupTime = DateTime.Now;
                _logger.Debug("Cleanup performed", GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.Error($"Cleanup failed: {ex.Message}", GetType().Name);
            }
        }

        /// <summary>
        /// Execution pipeline'ını başlatır;
        /// </summary>
        private void InitializeExecutionPipeline()
        {
            _executionPipeline.AddMiddleware(new SecurityValidationMiddleware(_permissionValidator));
            _executionPipeline.AddMiddleware(new LoggingMiddleware(_logger));
            _executionPipeline.AddMiddleware(new RiskAnalysisMiddleware(_riskAnalyzer));
            _executionPipeline.AddMiddleware(new PerformanceMonitoringMiddleware());

            _logger.Info("Execution pipeline initialized with 4 middleware", GetType().Name);
        }

        /// <summary>
        /// Batch pipeline'ını yapılandırır;
        /// </summary>
        private ICommandPipeline ConfigureBatchPipeline(BatchExecutionOptions options)
        {
            var pipeline = new CommandPipeline();

            if (options.EnableParallelProcessing)
            {
                pipeline.AddMiddleware(new ParallelProcessingMiddleware(options.MaxDegreeOfParallelism));
            }

            if (options.EnableTransaction)
            {
                pipeline.AddMiddleware(new TransactionMiddleware());
            }

            if (options.EnableRollback)
            {
                pipeline.AddMiddleware(new RollbackMiddleware());
            }

            return pipeline;
        }

        /// <summary>
        /// Komut simülasyonu yapar;
        /// </summary>
        private async Task<object> SimulateCommandExecutionAsync(
            ICommand command,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            _logger.Info($"Simulating command execution: {command.Name}", GetType().Name);

            // Simülasyon süresi;
            await Task.Delay(100, cancellationToken);

            return new SimulationResult;
            {
                CommandName = command.Name,
                Simulated = true,
                Timestamp = DateTime.Now;
            };
        }

        /// <summary>
        /// Niyet sonucundan komut oluşturur;
        /// </summary>
        private async Task<ICommand> CreateCommandFromIntentAsync(
            IntentDetectionResult intentResult,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            try
            {
                // Komut türüne göre oluştur;
                var commandType = intentResult.CommandType;
                var command = _commandRegistry.CreateCommand(commandType, intentResult.Parameters);

                if (command == null)
                {
                    _logger.Error($"Failed to create command of type {commandType}", GetType().Name);
                    return null;
                }

                // Parametreleri ayarla;
                foreach (var param in intentResult.Parameters)
                {
                    await SetCommandParameterAsync(command, param.Key, param.Value, cancellationToken);
                }

                return command;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create command from intent: {ex.Message}", GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// Komut parametresini ayarlar;
        /// </summary>
        private async Task SetCommandParameterAsync(
            ICommand command,
            string parameterName,
            object value,
            CancellationToken cancellationToken)
        {
            var property = command.GetType().GetProperty(parameterName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                // Tip dönüşümü;
                var targetType = property.PropertyType;
                var convertedValue = ConvertParameterValue(value, targetType);
                property.SetValue(command, convertedValue);
            }
        }

        /// <summary>
        /// Parametre değerini dönüştürür;
        /// </summary>
        private object ConvertParameterValue(object value, Type targetType)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            if (targetType.IsAssignableFrom(value.GetType()))
                return value;

            try
            {
                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return value;
            }
        }

        #endregion;

        #region Private Methods - Result Creation;

        /// <summary>
        /// Niyet sonucundan başarısız sonuç oluşturur;
        /// </summary>
        private CommandExecutionResult CreateFailedResultFromIntent(
            IntentDetectionResult intentResult,
            ExecutionContext context)
        {
            return new CommandExecutionResult;
            {
                Command = null,
                Status = CommandStatus.Failed,
                Result = null,
                Error = intentResult.Error ?? "Intent detection failed",
                ExecutionTime = TimeSpan.Zero,
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    { "IntentDetected", false },
                    { "Confidence", intentResult.Confidence }
                }
            };
        }

        /// <summary>
        /// Komut bulunamadı sonucu oluşturur;
        /// </summary>
        private CommandExecutionResult CreateCommandNotFoundResult(
            IntentDetectionResult intentResult,
            ExecutionContext context)
        {
            return new CommandExecutionResult;
            {
                Command = null,
                Status = CommandStatus.Failed,
                Result = null,
                Error = $"Command not found for type: {intentResult.CommandType}",
                ExecutionTime = TimeSpan.Zero,
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                Context = context;
            };
        }

        /// <summary>
        /// İstisnadan başarısız sonuç oluşturur;
        /// </summary>
        private CommandExecutionResult CreateFailedResultFromException(
            Exception ex,
            string commandText,
            ExecutionContext context)
        {
            return new CommandExecutionResult;
            {
                Command = null,
                Status = CommandStatus.Failed,
                Result = null,
                Error = ex.Message,
                Exception = ex,
                ExecutionTime = TimeSpan.Zero,
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    { "CommandText", commandText },
                    { "ExceptionType", ex.GetType().Name }
                }
            };
        }

        #endregion;

        #region Event Invokers;

        protected virtual void OnCommandExecutionStarted(CommandExecutionStartedEventArgs e)
        {
            CommandExecutionStarted?.Invoke(this, e);
        }

        protected virtual void OnCommandExecutionCompleted(CommandExecutionCompletedEventArgs e)
        {
            CommandExecutionCompleted?.Invoke(this, e);
        }

        protected virtual void OnCommandExecutionFailed(CommandExecutionFailedEventArgs e)
        {
            CommandExecutionFailed?.Invoke(this, e);
        }

        protected virtual void OnCommandExecutionCancelled(CommandExecutionCancelledEventArgs e)
        {
            CommandExecutionCancelled?.Invoke(this, e);
        }

        protected virtual void OnCommandExecutionTimedOut(CommandExecutionTimedOutEventArgs e)
        {
            CommandExecutionTimedOut?.Invoke(this, e);
        }

        protected virtual void OnCommandStatusChanged(CommandStatusChangedEventArgs e)
        {
            CommandStatusChanged?.Invoke(this, e);
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
                    CancelAllExecutions();

                    _concurrencySemaphore?.Dispose();

                    lock (_lockObject)
                    {
                        _activeSessions.Clear();
                        _executionHistory.Clear();
                        _performanceMetrics.Clear();
                    }

                    _commandCache?.Clear();

                    _logger.Info("CommandExecutor disposed", GetType().Name);
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~CommandExecutor()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public enum CommandStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled,
        TimedOut,
        PartialSuccess,
        Validated;
    }

    public enum CommandSessionStatus;
    {
        Created,
        Validated,
        Running,
        Completed,
        Failed,
        Cancelling,
        Cancelled;
    }

    public enum ExecutionPriority;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    public enum ExecutionMode;
    {
        RealTime,
        Simulation,
        Debug;
    }

    public enum ErrorToleranceMode;
    {
        Strict,     // Hata olursa durur;
        Tolerant,   // Hata tolere eder;
        Aggressive  // Hataları görmezden gelir;
    }

    public enum BatchExecutionStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        PartialFailure,
        Cancelled;
    }

    public enum ScriptExecutionStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        ValidationFailed,
        Cancelled;
    }

    public enum ScheduledCommandStatus;
    {
        Pending,
        Scheduled,
        Running,
        Completed,
        Failed,
        Cancelled;
    }

    public enum CommandValidationType;
    {
        None = 0,
        Permission = 1,
        Risk = 2,
        Parameters = 4,
        Dependencies = 8,
        All = 15;
    }

    public class ExecutionContext;
    {
        public IUser User { get; set; }
        public ExecutionPriority Priority { get; set; }
        public ExecutionMode ExecutionMode { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid CorrelationId { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    public class CommandSession;
    {
        public Guid SessionId { get; set; }
        public ICommand Command { get; set; }
        public ExecutionContext Context { get; set; }
        public CommandSessionStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => StartTime != default && EndTime.HasValue;
            ? EndTime.Value - StartTime;
            : DateTime.Now - StartTime;
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class CommandExecutionResult;
    {
        public ICommand Command { get; set; }
        public CommandStatus Status { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
        public Exception Exception { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public Guid SessionId { get; set; }
        public ExecutionContext Context { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        // Cache özellikleri;
        public bool Cached { get; set; }
        public string CacheKey { get; set; }
        public bool CanBeCached => Status == CommandStatus.Completed && !Cached;

        public CommandExecutionResult Clone()
        {
            return new CommandExecutionResult;
            {
                Command = this.Command,
                Status = this.Status,
                Result = this.Result,
                Error = this.Error,
                Exception = this.Exception,
                ExecutionTime = this.ExecutionTime,
                StartTime = this.StartTime,
                EndTime = this.EndTime,
                SessionId = this.SessionId,
                Context = this.Context,
                Metadata = new Dictionary<string, object>(this.Metadata),
                Cached = this.Cached,
                CacheKey = this.CacheKey;
            };
        }
    }

    public class BatchExecutionResult;
    {
        public List<CommandExecutionResult> CommandResults { get; set; } = new List<CommandExecutionResult>();
        public BatchExecutionStatus Status { get; set; } = BatchExecutionStatus.Running;
        public int TotalCommands { get; set; }
        public int SuccessfulCommands { get; set; }
        public int FailedCommands { get; set; }
        public List<BatchExecutionError> Errors { get; set; } = new List<BatchExecutionError>();
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalDuration => EndTime - StartTime;
        public BatchExecutionOptions Options { get; set; }
    }

    public class BatchExecutionError;
    {
        public ICommand Command { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
    }

    public class BatchExecutionOptions;
    {
        public bool EnableParallelProcessing { get; set; }
        public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
        public bool StopOnFailure { get; set; } = true;
        public bool EnableTransaction { get; set; }
        public bool EnableRollback { get; set; }
        public TimeSpan? Timeout { get; set; }
    }

    public class ScriptExecutionResult;
    {
        public CommandScript Script { get; set; }
        public List<CommandExecutionResult> CommandResults { get; set; } = new List<CommandExecutionResult>();
        public ScriptExecutionStatus Status { get; set; } = ScriptExecutionStatus.Running;
        public string Error { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalDuration => EndTime - StartTime;
    }

    public class CommandScript;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<ICommand> Commands { get; set; } = new List<ICommand>();
        public bool StopOnError { get; set; } = true;
        public TimeSpan? MaxExecutionTime { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class ScheduledCommand;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public ICommand Command { get; set; }
        public ExecutionContext Context { get; set; }
        public DateTime ScheduledTime { get; set; }
        public DateTime? ExecutionTime { get; set; }
        public ScheduledCommandStatus Status { get; set; }
        public ScheduleOptions Options { get; set; }
        public CommandExecutionResult Result { get; set; }
    }

    public class ScheduleOptions;
    {
        public bool Recurring { get; set; }
        public TimeSpan? RecurrenceInterval { get; set; }
        public int? MaxRecurrences { get; set; }
        public DateTime? EndTime { get; set; }
    }

    public class CommandValidationResult;
    {
        public ICommand Command { get; set; }
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public CommandValidationType ValidationType { get; set; }
        public RiskAssessment RiskAssessment { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class CommandStatistics;
    {
        public string CommandName { get; set; }
        public int TotalExecutions { get; set; }
        public int CommandExecutions { get; set; }
        public int SuccessfulExecutions { get; set; }
        public int FailedExecutions { get; set; }
        public int ActiveSessions { get; set; }
        public double AverageExecutionTime { get; set; }
        public float CacheHitRate { get; set; }
        public int CacheSize { get; set; }
    }

    public class CommandPerformanceMetrics;
    {
        public string CommandName { get; set; }
        public int TotalExecutions { get; set; }
        public int SuccessfulExecutions { get; set; }
        public int FailedExecutions { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public TimeSpan FastestExecution { get; set; }
        public TimeSpan SlowestExecution { get; set; }
        public DateTime LastSuccessfulExecution { get; set; }
        public DateTime LastFailedExecution { get; set; }
        public float SuccessRate { get; set; }

        public CommandPerformanceMetrics Clone()
        {
            return new CommandPerformanceMetrics;
            {
                CommandName = this.CommandName,
                TotalExecutions = this.TotalExecutions,
                SuccessfulExecutions = this.SuccessfulExecutions,
                FailedExecutions = this.FailedExecutions,
                TotalExecutionTime = this.TotalExecutionTime,
                AverageExecutionTime = this.AverageExecutionTime,
                FastestExecution = this.FastestExecution,
                SlowestExecution = this.SlowestExecution,
                LastSuccessfulExecution = this.LastSuccessfulExecution,
                LastFailedExecution = this.LastFailedExecution,
                SuccessRate = this.SuccessRate;
            };
        }
    }

    public class CommandCache;
    {
        private readonly Dictionary<string, CommandExecutionResult> _cache;
        private readonly int _maxSizeMB;
        private long _currentSizeBytes;
        private int _hits;
        private int _misses;

        public CommandCache(int maxSizeMB)
        {
            _maxSizeMB = maxSizeMB;
            _cache = new Dictionary<string, CommandExecutionResult>();
        }

        public bool TryGet(string key, out CommandExecutionResult result)
        {
            if (_cache.TryGetValue(key, out result))
            {
                _hits++;
                return true;
            }

            _misses++;
            return false;
        }

        public void Add(string key, CommandExecutionResult result)
        {
            var size = EstimateSize(result);

            // Eski öğeleri temizle;
            while (_currentSizeBytes + size > _maxSizeMB * 1024L * 1024L && _cache.Count > 0)
            {
                var oldestKey = _cache.Keys.First();
                _currentSizeBytes -= EstimateSize(_cache[oldestKey]);
                _cache.Remove(oldestKey);
            }

            _cache[key] = result.Clone();
            _currentSizeBytes += size;
        }

        public void Clear()
        {
            _cache.Clear();
            _currentSizeBytes = 0;
            _hits = 0;
            _misses = 0;
        }

        public void Cleanup()
        {
            // 1 saatten eski öğeleri temizle;
            var threshold = DateTime.Now.AddHours(-1);
            var toRemove = _cache.Where(kvp => kvp.Value.EndTime < threshold).ToList();

            foreach (var item in toRemove)
            {
                _currentSizeBytes -= EstimateSize(item.Value);
                _cache.Remove(item.Key);
            }
        }

        private long EstimateSize(CommandExecutionResult result)
        {
            // Basit bir boyut tahmini;
            var size = 100L; // Temel boyut;
            size += result.Error?.Length ?? 0;
            size += result.Metadata?.Sum(m => m.Key.Length + (m.Value?.ToString()?.Length ?? 0)) ?? 0;
            return size;
        }

        public int SizeMB => (int)(_currentSizeBytes / (1024 * 1024));
        public float HitRate => _hits + _misses > 0 ? (_hits * 100.0f) / (_hits + _misses) : 0;
    }

    #endregion;

    #region Event Args Classes;

    public class CommandExecutionStartedEventArgs : EventArgs;
    {
        public CommandSession Session { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public CommandExecutionStartedEventArgs(CommandSession session)
        {
            Session = session;
        }
    }

    public class CommandExecutionCompletedEventArgs : EventArgs;
    {
        public CommandExecutionResult Result { get; }
        public CommandSession Session { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public CommandExecutionCompletedEventArgs(CommandExecutionResult result, CommandSession session)
        {
            Result = result;
            Session = session;
        }
    }

    public class CommandExecutionFailedEventArgs : EventArgs;
    {
        public CommandExecutionResult Result { get; }
        public CommandSession Session { get; }
        public Exception Exception { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public CommandExecutionFailedEventArgs(CommandExecutionResult result, CommandSession session, Exception exception)
        {
            Result = result;
            Session = session;
            Exception = exception;
        }
    }

    public class CommandExecutionCancelledEventArgs : EventArgs;
    {
        public CommandExecutionResult Result { get; }
        public CommandSession Session { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public CommandExecutionCancelledEventArgs(CommandExecutionResult result, CommandSession session)
        {
            Result = result;
            Session = session;
        }
    }

    public class CommandExecutionTimedOutEventArgs : EventArgs;
    {
        public CommandExecutionResult Result { get; }
        public CommandSession Session { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public CommandExecutionTimedOutEventArgs(CommandExecutionResult result, CommandSession session)
        {
            Result = result;
            Session = session;
        }
    }

    public class CommandStatusChangedEventArgs : EventArgs;
    {
        public Guid SessionId { get; set; }
        public CommandSessionStatus OldStatus { get; set; }
        public CommandSessionStatus NewStatus { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    #endregion;
}
