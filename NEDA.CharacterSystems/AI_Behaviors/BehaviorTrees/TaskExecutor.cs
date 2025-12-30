// NEDA.Automation/Executors/TaskExecutor.cs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Dynamic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security;
using System.Security.Principal;
using System.Management;
using System.Runtime.InteropServices;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;
using NEDA.Automation.Executors.Configuration;
using NEDA.Automation.Executors.Contracts;
using NEDA.Automation.Executors.Exceptions;
using NEDA.Automation.Executors.Models;
using NEDA.Automation.Executors.Results;
using NEDA.Automation.Executors.Validators;
using NEDA.Automation.Executors.Scripting;
using NEDA.Automation.Executors.Security;
using NEDA.Automation.Executors.Monitoring;
using NEDA.Automation.Executors.Retry
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.SecurityModules.AdvancedSecurity.Authentication;

namespace NEDA.Automation.Executors;
{
    /// <summary>
    /// Advanced task execution engine with support for multiple task types,
    /// parallel execution, dependency management, and comprehensive monitoring.
    /// </summary>
    public class TaskExecutor : ITaskExecutor, IDisposable;
    {
        #region Constants;

        private const int DEFAULT_MAX_CONCURRENT_TASKS = 10;
        private const int DEFAULT_TASK_TIMEOUT_SECONDS = 3600; // 1 hour;
        private const int DEFAULT_HEARTBEAT_INTERVAL_SECONDS = 10;
        private const int MAX_TASK_OUTPUT_SIZE = 10 * 1024 * 1024; // 10MB;
        private const string TASK_STATE_FILE_EXTENSION = ".task.state";
        private const string TASK_LOG_FILE_PREFIX = "task_";

        #endregion;

        #region Private Fields;

        private readonly TaskExecutorConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ITaskValidator _validator;
        private readonly ITaskSecurityManager _securityManager;
        private readonly ITaskMonitor _taskMonitor;
        private readonly IRetryStrategy _retryStrategy;
        private readonly Dictionary<string, ITaskHandler> _taskHandlers;
        private readonly Dictionary<string, IScriptEngine> _scriptEngines;
        private readonly Dictionary<string, TaskExecution> _activeExecutions;
        private readonly Dictionary<string, TaskExecutionContext> _executionContexts;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly object _syncLock = new object();
        private readonly Timer _heartbeatTimer;
        private readonly Timer _cleanupTimer;
        private readonly CancellationTokenSource _globalCancellation;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _disposed;
        private bool _isInitialized;
        private int _totalExecutions;
        private long _totalExecutionTimeMs;
        private long _peakMemoryUsage;
        private int _peakConcurrentTasks;

        #endregion;

        #region Properties;

        /// <summary>
        /// Gets the configuration used by this task executor.
        /// </summary>
        public TaskExecutorConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets the number of currently active task executions.
        /// </summary>
        public int ActiveExecutionCount => _activeExecutions.Count;

        /// <summary>
        /// Gets the total number of task executions since startup.
        /// </summary>
        public int TotalExecutionCount => _totalExecutions;

        /// <summary>
        /// Gets the peak number of concurrent tasks executed.
        /// </summary>
        public int PeakConcurrentTasks => _peakConcurrentTasks;

        /// <summary>
        /// Gets the peak memory usage in bytes.
        /// </summary>
        public long PeakMemoryUsageBytes => _peakMemoryUsage;

        /// <summary>
        /// Gets the average task execution time in milliseconds.
        /// </summary>
        public double AverageExecutionTimeMs => _totalExecutions > 0 ?
            (double)_totalExecutionTimeMs / _totalExecutions : 0;

        /// <summary>
        /// Gets the task execution statistics.
        /// </summary>
        public TaskExecutionStatistics Statistics => GetStatistics();

        /// <summary>
        /// Gets whether the task executor is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets the supported task types.
        /// </summary>
        public IReadOnlyList<string> SupportedTaskTypes => _taskHandlers.Keys.ToList().AsReadOnly();

        /// <summary>
        /// Gets the supported script languages.
        /// </summary>
        public IReadOnlyList<string> SupportedScriptLanguages => _scriptEngines.Keys.ToList().AsReadOnly();

        #endregion;

        #region Events;

        /// <summary>
        /// Occurs when a task execution starts.
        /// </summary>
        public event EventHandler<TaskExecutionStartedEventArgs> TaskExecutionStarted;

        /// <summary>
        /// Occurs when a task execution completes.
        /// </summary>
        public event EventHandler<TaskExecutionCompletedEventArgs> TaskExecutionCompleted;

        /// <summary>
        /// Occurs when a task execution fails.
        /// </summary>
        public event EventHandler<TaskExecutionFailedEventArgs> TaskExecutionFailed;

        /// <summary>
        /// Occurs when a task execution times out.
        /// </summary>
        public event EventHandler<TaskExecutionTimeoutEventArgs> TaskExecutionTimeout;

        /// <summary>
        /// Occurs when task progress is reported.
        /// </summary>
        public event EventHandler<TaskProgressEventArgs> TaskProgress;

        /// <summary>
        /// Occurs when task output is generated.
        /// </summary>
        public event EventHandler<TaskOutputEventArgs> TaskOutput;

        /// <summary>
        /// Occurs when task status changes.
        /// </summary>
        public event EventHandler<TaskStatusChangedEventArgs> TaskStatusChanged;

        /// <summary>
        /// Occurs when a task is queued.
        /// </summary>
        public event EventHandler<TaskQueuedEventArgs> TaskQueued;

        /// <summary>
        /// Occurs when a task is cancelled.
        /// </summary>
        public event EventHandler<TaskCancelledEventArgs> TaskCancelled;

        /// <summary>
        /// Occurs when task dependencies are resolved.
        /// </summary>
        public event EventHandler<TaskDependenciesResolvedEventArgs> TaskDependenciesResolved;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of the TaskExecutor class with default configuration.
        /// </summary>
        public TaskExecutor()
            : this(new TaskExecutorConfiguration(), null, null, null, null, null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the TaskExecutor class with specified configuration.
        /// </summary>
        /// <param name="configuration">The task executor configuration.</param>
        /// <param name="logger">The logger instance (optional).</param>
        /// <param name="performanceMonitor">The performance monitor (optional).</param>
        /// <param name="validator">The task validator (optional).</param>
        /// <param name="securityManager">The security manager (optional).</param>
        /// <param name="taskMonitor">The task monitor (optional).</param>
        /// <param name="retryStrategy">The retry strategy (optional).</param>
        public TaskExecutor(
            TaskExecutorConfiguration configuration,
            ILogger logger = null,
            IPerformanceMonitor performanceMonitor = null,
            ITaskValidator validator = null,
            ITaskSecurityManager securityManager = null,
            ITaskMonitor taskMonitor = null,
            IRetryStrategy retryStrategy = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? LogManager.GetLogger(typeof(TaskExecutor));
            _performanceMonitor = performanceMonitor;
            _validator = validator ?? new TaskValidator();
            _securityManager = securityManager ?? new TaskSecurityManager();
            _taskMonitor = taskMonitor ?? new TaskMonitor();
            _retryStrategy = retryStrategy ?? new ExponentialBackoffRetryStrategy();

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };

            _taskHandlers = new Dictionary<string, ITaskHandler>();
            _scriptEngines = new Dictionary<string, IScriptEngine>();
            _activeExecutions = new Dictionary<string, TaskExecution>();
            _executionContexts = new Dictionary<string, TaskExecutionContext>();

            _concurrencySemaphore = new SemaphoreSlim(
                _configuration.MaxConcurrentTasks,
                _configuration.MaxConcurrentTasks);

            _globalCancellation = new CancellationTokenSource();

            InitializeDefaultHandlers();
            InitializeScriptEngines();

            // Initialize timers;
            _heartbeatTimer = new Timer(
                HeartbeatCallback,
                null,
                TimeSpan.FromSeconds(DEFAULT_HEARTBEAT_INTERVAL_SECONDS),
                TimeSpan.FromSeconds(DEFAULT_HEARTBEAT_INTERVAL_SECONDS));

            _cleanupTimer = new Timer(
                CleanupCallback,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        #endregion;

        #region Public Methods - Initialization;

        /// <summary>
        /// Initializes the task executor asynchronously.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.Info("Initializing TaskExecutor...");

                // Create required directories;
                CreateRequiredDirectories();

                // Initialize security manager;
                await _securityManager.InitializeAsync(cancellationToken);

                // Initialize task monitor;
                await _taskMonitor.InitializeAsync(cancellationToken);

                // Initialize task handlers;
                await InitializeTaskHandlersAsync(cancellationToken);

                // Initialize script engines;
                await InitializeScriptEnginesAsync(cancellationToken);

                // Restore interrupted executions;
                await RestoreInterruptedExecutionsAsync(cancellationToken);

                _isInitialized = true;
                _logger.Info("TaskExecutor initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize TaskExecutor: {ex.Message}", ex);
                throw new TaskExecutorInitializationException(
                    "Failed to initialize TaskExecutor", ex);
            }
        }

        /// <summary>
        /// Registers a custom task handler.
        /// </summary>
        /// <param name="taskType">The task type.</param>
        /// <param name="handler">The task handler.</param>
        public void RegisterTaskHandler(string taskType, ITaskHandler handler)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(taskType))
                throw new ArgumentException("Task type cannot be null or empty", nameof(taskType));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_syncLock)
            {
                if (_taskHandlers.ContainsKey(taskType))
                {
                    throw new InvalidOperationException(
                        $"Task handler for '{taskType}' is already registered");
                }

                _taskHandlers[taskType] = handler;
                _logger.Info($"Registered task handler: {taskType}");
            }
        }

        /// <summary>
        /// Unregisters a task handler.
        /// </summary>
        /// <param name="taskType">The task type.</param>
        /// <returns>True if the handler was unregistered; otherwise, false.</returns>
        public bool UnregisterTaskHandler(string taskType)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(taskType))
                return false;

            lock (_syncLock)
            {
                if (_taskHandlers.TryGetValue(taskType, out var handler))
                {
                    handler.Dispose();
                    _taskHandlers.Remove(taskType);
                    _logger.Info($"Unregistered task handler: {taskType}");
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Registers a custom script engine.
        /// </summary>
        /// <param name="language">The script language.</param>
        /// <param name="engine">The script engine.</param>
        public void RegisterScriptEngine(string language, IScriptEngine engine)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(language))
                throw new ArgumentException("Language cannot be null or empty", nameof(language));

            if (engine == null)
                throw new ArgumentNullException(nameof(engine));

            lock (_syncLock)
            {
                if (_scriptEngines.ContainsKey(language))
                {
                    throw new InvalidOperationException(
                        $"Script engine for '{language}' is already registered");
                }

                _scriptEngines[language] = engine;
                _logger.Info($"Registered script engine: {language}");
            }
        }

        /// <summary>
        /// Unregisters a script engine.
        /// </summary>
        /// <param name="language">The script language.</param>
        /// <returns>True if the engine was unregistered; otherwise, false.</returns>
        public bool UnregisterScriptEngine(string language)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(language))
                return false;

            lock (_syncLock)
            {
                if (_scriptEngines.TryGetValue(language, out var engine))
                {
                    engine.Dispose();
                    _scriptEngines.Remove(language);
                    _logger.Info($"Unregistered script engine: {language}");
                    return true;
                }

                return false;
            }
        }

        #endregion;

        #region Public Methods - Task Execution;

        /// <summary>
        /// Executes a task asynchronously.
        /// </summary>
        /// <param name="request">The task execution request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The task execution result.</returns>
        public async Task<TaskExecutionResult> ExecuteTaskAsync(
            TaskExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _validator.ValidateExecutionRequest(request);

            // Apply security validation;
            await _securityManager.ValidateTaskExecutionAsync(request, cancellationToken);

            // Wait for semaphore slot;
            var semaphoreAcquired = await _concurrencySemaphore.WaitAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);

            if (!semaphoreAcquired)
            {
                throw new TaskExecutionException(
                    "Could not acquire task execution slot within timeout");
            }

            string executionId = GenerateExecutionId(request);
            TaskExecution execution = null;

            try
            {
                // Create execution context;
                execution = new TaskExecution;
                {
                    Id = executionId,
                    TaskId = request.TaskId,
                    TaskType = request.TaskType,
                    Name = request.Name,
                    Description = request.Description,
                    Status = TaskExecutionStatus.Queued,
                    QueuedAt = DateTime.UtcNow,
                    CreatedBy = request.CreatedBy ?? GetCurrentUser(),
                    Priority = request.Priority,
                    Timeout = request.Timeout ?? TimeSpan.FromSeconds(_configuration.DefaultTaskTimeoutSeconds),
                    Parameters = request.Parameters ?? new Dictionary<string, object>(),
                    Tags = request.Tags ?? new List<string>(),
                    Metadata = request.Metadata ?? new Dictionary<string, object>()
                };

                // Add to active executions;
                lock (_syncLock)
                {
                    _activeExecutions[executionId] = execution;
                    UpdatePeakConcurrentTasks();
                }

                // Fire queued event;
                OnTaskQueued(new TaskQueuedEventArgs;
                {
                    ExecutionId = executionId,
                    TaskId = request.TaskId,
                    TaskName = request.Name,
                    TaskType = request.TaskType,
                    QueuedAt = execution.QueuedAt,
                    CreatedBy = execution.CreatedBy,
                    Priority = execution.Priority;
                });

                // Start execution in background;
                var executionTask = ExecuteTaskInternalAsync(
                    execution,
                    request,
                    cancellationToken);

                // Link completion;
                execution.CompletionTask = executionTask.ContinueWith(t =>
                {
                    // Clean up execution context;
                    lock (_syncLock)
                    {
                        _executionContexts.Remove(executionId);
                    }
                }, TaskScheduler.Default);

                // Return immediately with execution ID;
                return new TaskExecutionResult;
                {
                    ExecutionId = executionId,
                    Status = TaskExecutionStatus.Queued,
                    Message = "Task execution queued successfully",
                    QueuedAt = execution.QueuedAt;
                };
            }
            catch (Exception ex)
            {
                // Release semaphore on error;
                _concurrencySemaphore.Release();

                // Clean up execution;
                if (execution != null)
                {
                    lock (_syncLock)
                    {
                        _activeExecutions.Remove(executionId);
                    }
                }

                _logger.Error($"Failed to queue task execution: {ex.Message}", ex);
                throw new TaskExecutionException(
                    $"Failed to queue task execution: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Executes multiple tasks in parallel.
        /// </summary>
        /// <param name="requests">The task execution requests.</param>
        /// <param name="maxParallel">Maximum parallel executions.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The task execution results.</returns>
        public async Task<IReadOnlyList<TaskExecutionResult>> ExecuteTasksAsync(
            IEnumerable<TaskExecutionRequest> requests,
            int? maxParallel = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            var requestList = requests.ToList();
            if (requestList.Count == 0)
                return new List<TaskExecutionResult>().AsReadOnly();

            var maxConcurrent = maxParallel ?? _configuration.MaxConcurrentTasks;
            var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            var tasks = new List<Task<TaskExecutionResult>>();
            var results = new List<TaskExecutionResult>();

            _logger.Info($"Executing {requestList.Count} tasks (max parallel: {maxConcurrent})");

            foreach (var request in requestList)
            {
                await semaphore.WaitAsync(cancellationToken);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        return await ExecuteTaskAsync(request, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);

                tasks.Add(task);
            }

            // Wait for all tasks to complete;
            var completedTasks = await Task.WhenAll(tasks);
            results.AddRange(completedTasks.Where(r => r != null));

            _logger.Info($"Completed executing {results.Count} tasks");

            return results.AsReadOnly();
        }

        /// <summary>
        /// Gets a task execution by ID.
        /// </summary>
        /// <param name="executionId">The execution ID.</param>
        /// <returns>The task execution, or null if not found.</returns>
        public TaskExecution GetTaskExecution(string executionId)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                return null;

            lock (_syncLock)
            {
                return _activeExecutions.TryGetValue(executionId, out var execution)
                    ? execution;
                    : null;
            }
        }

        /// <summary>
        /// Gets all active task executions.
        /// </summary>
        /// <returns>A list of all active task executions.</returns>
        public IReadOnlyList<TaskExecution> GetActiveExecutions()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            lock (_syncLock)
            {
                return _activeExecutions.Values.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Gets task execution history.
        /// </summary>
        /// <param name="criteria">The search criteria.</param>
        /// <returns>A list of historical task executions.</returns>
        public async Task<IReadOnlyList<TaskExecutionHistory>> GetExecutionHistoryAsync(
            ExecutionHistoryCriteria criteria)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            var history = new List<TaskExecutionHistory>();
            var historyDir = Path.Combine(_configuration.WorkingDirectory, "history");

            if (!Directory.Exists(historyDir))
                return history.AsReadOnly();

            try
            {
                var historyFiles = Directory.GetFiles(historyDir, "*.history.json");

                foreach (var file in historyFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var executionHistory = JsonSerializer.Deserialize<TaskExecutionHistory>(
                            json, _jsonOptions);

                        if (executionHistory != null && MatchesCriteria(executionHistory, criteria))
                        {
                            history.Add(executionHistory);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to load history file {file}: {ex.Message}");
                    }
                }

                // Sort by execution time (newest first)
                return history;
                    .OrderByDescending(h => h.StartedAt)
                    .ToList()
                    .AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load execution history: {ex.Message}", ex);
                throw new TaskExecutorException("Failed to load execution history", ex);
            }
        }

        /// <summary>
        /// Cancels a task execution.
        /// </summary>
        /// <param name="executionId">The execution ID to cancel.</param>
        /// <param name="reason">The reason for cancellation.</param>
        /// <returns>True if the execution was cancelled; otherwise, false.</returns>
        public async Task<bool> CancelTaskExecutionAsync(
            string executionId,
            string reason = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                return false;

            TaskExecution execution;
            TaskExecutionContext context;

            lock (_syncLock)
            {
                if (!_activeExecutions.TryGetValue(executionId, out execution) ||
                    !_executionContexts.TryGetValue(executionId, out context))
                    return false;

                // Check if execution can be cancelled;
                if (!execution.CanCancel)
                    return false;

                // Update status;
                execution.Status = TaskExecutionStatus.Cancelling;
                execution.CancellationRequestedAt = DateTime.UtcNow;
                execution.CancellationReason = reason;
            }

            try
            {
                // Notify execution to cancel;
                context.CancellationSource.Cancel();

                // Wait for cancellation to complete;
                await Task.WhenAny(
                    execution.CompletionTask,
                    Task.Delay(TimeSpan.FromSeconds(30)));

                OnTaskCancelled(new TaskCancelledEventArgs;
                {
                    ExecutionId = executionId,
                    TaskId = execution.TaskId,
                    TaskName = execution.Name,
                    CancelledAt = DateTime.UtcNow,
                    Reason = reason;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to cancel task execution {executionId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Retries a failed task execution.
        /// </summary>
        /// <param name="executionId">The execution ID to retry.</param>
        /// <param name="parameters">Optional override parameters.</param>
        /// <returns>The new execution result.</returns>
        public async Task<TaskExecutionResult> RetryTaskExecutionAsync(
            string executionId,
            Dictionary<string, object> parameters = null)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution ID cannot be null or empty", nameof(executionId));

            // Load execution history;
            var history = await LoadExecutionHistoryAsync(executionId);
            if (history == null)
                throw new KeyNotFoundException($"Execution history for '{executionId}' not found");

            // Create retry request;
            var request = new TaskExecutionRequest;
            {
                TaskId = history.TaskId,
                TaskType = history.TaskType,
                Name = history.Name,
                Description = history.Description,
                CreatedBy = history.CreatedBy,
                Priority = history.Priority,
                Timeout = history.Timeout,
                Parameters = parameters ?? history.Parameters,
                Tags = history.Tags,
                Metadata = new Dictionary<string, object>
                {
                    ["retryOf"] = executionId,
                    ["originalExecutionTime"] = history.StartedAt;
                }
            };

            return await ExecuteTaskAsync(request);
        }

        /// <summary>
        /// Executes a script task.
        /// </summary>
        /// <param name="script">The script to execute.</param>
        /// <param name="language">The script language.</param>
        /// <param name="parameters">Script parameters.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The script execution result.</returns>
        public async Task<ScriptExecutionResult> ExecuteScriptAsync(
            string script,
            string language = "powershell",
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(script))
                throw new ArgumentException("Script cannot be null or empty", nameof(script));

            if (!_scriptEngines.TryGetValue(language, out var engine))
            {
                throw new ScriptExecutionException($"Unsupported script language: {language}");
            }

            var request = new TaskExecutionRequest;
            {
                TaskId = $"script_{Guid.NewGuid():N}",
                TaskType = "Script",
                Name = $"Script Execution ({language})",
                Description = $"Execute {language} script",
                Parameters = new Dictionary<string, object>
                {
                    ["script"] = script,
                    ["language"] = language,
                    ["parameters"] = parameters ?? new Dictionary<string, object>()
                }
            };

            var result = await ExecuteTaskAsync(request, cancellationToken);

            return new ScriptExecutionResult;
            {
                ExecutionId = result.ExecutionId,
                Script = script,
                Language = language,
                Parameters = parameters,
                Status = result.Status,
                Output = result.Output,
                Error = result.Error,
                ExitCode = result.ExitCode,
                ExecutionTime = result.ExecutionTime;
            };
        }

        /// <summary>
        /// Executes a command line task.
        /// </summary>
        /// <param name="command">The command to execute.</param>
        /// <param name="arguments">Command arguments.</param>
        /// <param name="workingDirectory">Working directory.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The command execution result.</returns>
        public async Task<CommandExecutionResult> ExecuteCommandAsync(
            string command,
            string arguments = null,
            string workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command cannot be null or empty", nameof(command));

            var request = new TaskExecutionRequest;
            {
                TaskId = $"cmd_{Guid.NewGuid():N}",
                TaskType = "Command",
                Name = $"Command Execution: {command}",
                Description = $"Execute command: {command}",
                Parameters = new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["arguments"] = arguments,
                    ["workingDirectory"] = workingDirectory,
                    ["captureOutput"] = true,
                    ["redirectStandardError"] = true;
                }
            };

            var result = await ExecuteTaskAsync(request, cancellationToken);

            return new CommandExecutionResult;
            {
                ExecutionId = result.ExecutionId,
                Command = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Status = result.Status,
                Output = result.Output,
                Error = result.Error,
                ExitCode = result.ExitCode,
                ExecutionTime = result.ExecutionTime;
            };
        }

        #endregion;

        #region Private Methods - Task Execution;

        private async Task ExecuteTaskInternalAsync(
            TaskExecution execution,
            TaskExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _globalCancellation.Token);

            var context = new TaskExecutionContext;
            {
                Execution = execution,
                Request = request,
                CancellationSource = executionCancellation,
                WorkingDirectory = GetExecutionWorkingDirectory(execution.Id),
                LogFile = GetLogFilePath(execution.Id)
            };

            lock (_syncLock)
            {
                _executionContexts[execution.Id] = context;
            }

            execution.StartedAt = DateTime.UtcNow;
            execution.Status = TaskExecutionStatus.Running;

            // Create log file directory;
            Directory.CreateDirectory(Path.GetDirectoryName(context.LogFile));

            using var logWriter = new StreamWriter(context.LogFile, append: true);

            try
            {
                // Fire execution started event;
                OnTaskExecutionStarted(new TaskExecutionStartedEventArgs;
                {
                    ExecutionId = execution.Id,
                    TaskId = execution.TaskId,
                    TaskName = execution.Name,
                    TaskType = execution.TaskType,
                    StartedAt = execution.StartedAt,
                    CreatedBy = execution.CreatedBy,
                    Priority = execution.Priority;
                });

                // Log execution start;
                await LogExecutionEventAsync(logWriter, execution,
                    "Task execution started", LogLevel.Info);

                // Create working directory;
                Directory.CreateDirectory(context.WorkingDirectory);

                // Execute task with timeout;
                var timeoutTask = Task.Delay(execution.Timeout, executionCancellation.Token);
                var executionTask = ExecuteTaskWithRetryAsync(context, logWriter);

                var completedTask = await Task.WhenAny(executionTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Task timed out;
                    executionCancellation.Cancel();

                    execution.Status = TaskExecutionStatus.TimedOut;
                    execution.TimeoutOccurred = true;
                    execution.Errors.Add($"Task execution timed out after {execution.Timeout.TotalSeconds} seconds");

                    await LogExecutionEventAsync(logWriter, execution,
                        $"Task execution timed out after {execution.Timeout.TotalSeconds} seconds",
                        LogLevel.Error);

                    OnTaskExecutionTimeout(new TaskExecutionTimeoutEventArgs;
                    {
                        ExecutionId = execution.Id,
                        TaskId = execution.TaskId,
                        TaskName = execution.Name,
                        Timeout = execution.Timeout,
                        TimedOutAt = DateTime.UtcNow;
                    });
                }
                else;
                {
                    // Task completed (successfully or with error)
                    await executionTask;
                }

                // Mark execution as completed;
                execution.CompletedAt = DateTime.UtcNow;
                execution.ExecutionTime = stopwatch.Elapsed;

                // Update statistics;
                Interlocked.Increment(ref _totalExecutions);
                Interlocked.Add(ref _totalExecutionTimeMs, (long)stopwatch.Elapsed.TotalMilliseconds);

                // Update peak memory usage;
                UpdatePeakMemoryUsage();

                // Save execution history;
                await SaveExecutionHistoryAsync(execution);

                // Log completion;
                await LogExecutionEventAsync(logWriter, execution,
                    $"Task execution completed with status: {execution.Status} in {execution.ExecutionTime.TotalSeconds:F1} seconds",
                    LogLevel.Info);

                // Fire completion event;
                if (execution.Status == TaskExecutionStatus.Completed)
                {
                    OnTaskExecutionCompleted(new TaskExecutionCompletedEventArgs;
                    {
                        ExecutionId = execution.Id,
                        TaskId = execution.TaskId,
                        TaskName = execution.Name,
                        TaskType = execution.TaskType,
                        StartedAt = execution.StartedAt,
                        CompletedAt = execution.CompletedAt,
                        ExecutionTime = execution.ExecutionTime,
                        Status = execution.Status,
                        Output = execution.Output,
                        ExitCode = execution.ExitCode;
                    });
                }
                else if (execution.Status == TaskExecutionStatus.Failed ||
                         execution.Status == TaskExecutionStatus.TimedOut)
                {
                    OnTaskExecutionFailed(new TaskExecutionFailedEventArgs;
                    {
                        ExecutionId = execution.Id,
                        TaskId = execution.TaskId,
                        TaskName = execution.Name,
                        TaskType = execution.TaskType,
                        StartedAt = execution.StartedAt,
                        FailedAt = execution.CompletedAt.Value,
                        ExecutionTime = execution.ExecutionTime,
                        Status = execution.Status,
                        Error = execution.Errors.LastOrDefault(),
                        ExitCode = execution.ExitCode,
                        Output = execution.Output;
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Execution was cancelled;
                execution.CompletedAt = DateTime.UtcNow;
                execution.Status = TaskExecutionStatus.Cancelled;
                execution.ExecutionTime = stopwatch.Elapsed;

                await LogExecutionEventAsync(logWriter, execution,
                    $"Task execution cancelled after {execution.ExecutionTime.TotalSeconds:F1} seconds",
                    LogLevel.Warning);

                await SaveExecutionHistoryAsync(execution);

                OnTaskCancelled(new TaskCancelledEventArgs;
                {
                    ExecutionId = execution.Id,
                    TaskId = execution.TaskId,
                    TaskName = execution.Name,
                    CancelledAt = execution.CompletedAt.Value,
                    Reason = execution.CancellationReason;
                });
            }
            catch (Exception ex)
            {
                // Execution failed with unexpected error;
                execution.CompletedAt = DateTime.UtcNow;
                execution.Status = TaskExecutionStatus.Failed;
                execution.ExecutionTime = stopwatch.Elapsed;
                execution.Errors.Add(ex.Message);

                await LogExecutionEventAsync(logWriter, execution,
                    $"Task execution failed after {execution.ExecutionTime.TotalSeconds:F1} seconds: {ex.Message}",
                    LogLevel.Error, ex);

                await SaveExecutionHistoryAsync(execution);

                OnTaskExecutionFailed(new TaskExecutionFailedEventArgs;
                {
                    ExecutionId = execution.Id,
                    TaskId = execution.TaskId,
                    TaskName = execution.Name,
                    TaskType = execution.TaskType,
                    StartedAt = execution.StartedAt,
                    FailedAt = execution.CompletedAt.Value,
                    ExecutionTime = execution.ExecutionTime,
                    Status = execution.Status,
                    Error = ex.Message,
                    Exception = ex;
                });
            }
            finally
            {
                // Clean up;
                execution.CompletionTask = Task.CompletedTask;
                executionCancellation.Dispose();

                // Remove from active executions;
                lock (_syncLock)
                {
                    _activeExecutions.Remove(execution.Id);
                }

                // Release semaphore;
                _concurrencySemaphore.Release();

                stopwatch.Stop();

                // Update status;
                OnTaskStatusChanged(new TaskStatusChangedEventArgs;
                {
                    ExecutionId = execution.Id,
                    OldStatus = TaskExecutionStatus.Running,
                    NewStatus = execution.Status,
                    ChangedAt = DateTime.UtcNow;
                });
            }
        }

        private async Task ExecuteTaskWithRetryAsync(
            TaskExecutionContext context,
            StreamWriter logWriter)
        {
            var execution = context.Execution;
            var request = context.Request;
            var cancellationToken = context.CancellationSource.Token;

            int attempt = 0;
            int maxRetries = request.MaxRetries ?? _configuration.DefaultMaxRetries;
            TimeSpan initialDelay = request.RetryDelay ?? _configuration.DefaultRetryDelay;

            while (attempt <= maxRetries)
            {
                attempt++;

                try
                {
                    await LogExecutionEventAsync(logWriter, execution,
                        $"Starting execution attempt {attempt}/{maxRetries + 1}",
                        LogLevel.Info);

                    // Execute the task;
                    await ExecuteSingleTaskAsync(context, logWriter, cancellationToken);

                    // If we get here, execution succeeded;
                    execution.Status = TaskExecutionStatus.Completed;
                    break;
                }
                catch (Exception ex) when (attempt <= maxRetries && !cancellationToken.IsCancellationRequested)
                {
                    // Calculate retry delay;
                    var retryDelay = _retryStrategy.GetRetryDelay(attempt, initialDelay);

                    await LogExecutionEventAsync(logWriter, execution,
                        $"Attempt {attempt} failed: {ex.Message}. Retrying in {retryDelay.TotalSeconds:F1} seconds...",
                        LogLevel.Warning, ex);

                    // Wait before retry
                    await Task.Delay(retryDelay, cancellationToken);

                    // Update execution state for retry
                    execution.RetryCount = attempt - 1;
                    execution.LastRetryAt = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    // No more retries or cancellation requested;
                    execution.Status = TaskExecutionStatus.Failed;
                    execution.Errors.Add($"Failed after {attempt} attempts: {ex.Message}");
                    execution.ExitCode = -1;
                    throw;
                }
            }
        }

        private async Task ExecuteSingleTaskAsync(
            TaskExecutionContext context,
            StreamWriter logWriter,
            CancellationToken cancellationToken)
        {
            var execution = context.Execution;
            var request = context.Request;

            // Get task handler;
            if (!_taskHandlers.TryGetValue(request.TaskType, out var handler))
            {
                throw new TaskExecutionException($"No handler registered for task type: {request.TaskType}");
            }

            // Prepare task data;
            var taskData = new TaskData;
            {
                ExecutionId = execution.Id,
                TaskId = execution.TaskId,
                Name = execution.Name,
                Type = execution.TaskType,
                Parameters = execution.Parameters,
                WorkingDirectory = context.WorkingDirectory,
                CreatedBy = execution.CreatedBy,
                Priority = execution.Priority,
                Metadata = execution.Metadata;
            };

            // Resolve dependencies if needed;
            if (request.Dependencies != null && request.Dependencies.Count > 0)
            {
                await ResolveTaskDependenciesAsync(execution, request, logWriter, cancellationToken);
            }

            // Execute task using handler;
            var result = await handler.ExecuteAsync(taskData, cancellationToken);

            // Update execution with result;
            execution.Output = result.Output;
            execution.ErrorOutput = result.Error;
            execution.ExitCode = result.ExitCode;
            execution.ResourceUsage = result.ResourceUsage;
            execution.Artifacts = result.Artifacts;

            // Check if task succeeded;
            if (!result.Success)
            {
                execution.Errors.Add(result.Error ?? "Task execution failed");
                throw new TaskExecutionException($"Task execution failed: {result.Error}");
            }

            // Log success;
            await LogExecutionEventAsync(logWriter, execution,
                $"Task execution succeeded with exit code {result.ExitCode}",
                LogLevel.Info);
        }

        private async Task ResolveTaskDependenciesAsync(
            TaskExecution execution,
            TaskExecutionRequest request,
            StreamWriter logWriter,
            CancellationToken cancellationToken)
        {
            await LogExecutionEventAsync(logWriter, execution,
                $"Resolving {request.Dependencies.Count} task dependencies",
                LogLevel.Info);

            var resolvedDependencies = new List<TaskDependencyResult>();

            foreach (var dependency in request.Dependencies)
            {
                try
                {
                    // Create dependency request;
                    var depRequest = new TaskExecutionRequest;
                    {
                        TaskId = dependency.TaskId,
                        TaskType = dependency.TaskType,
                        Name = dependency.Name,
                        Description = dependency.Description,
                        Parameters = dependency.Parameters,
                        Timeout = dependency.Timeout,
                        Priority = TaskPriority.Low, // Dependencies run with lower priority;
                        Tags = new List<string> { "dependency" }
                    };

                    // Execute dependency;
                    var depResult = await ExecuteTaskAsync(depRequest, cancellationToken);

                    resolvedDependencies.Add(new TaskDependencyResult;
                    {
                        TaskId = dependency.TaskId,
                        ExecutionId = depResult.ExecutionId,
                        Status = depResult.Status,
                        Success = depResult.Status == TaskExecutionStatus.Completed;
                    });

                    if (depResult.Status != TaskExecutionStatus.Completed)
                    {
                        throw new TaskDependencyException(
                            $"Dependency task {dependency.TaskId} failed with status: {depResult.Status}");
                    }
                }
                catch (Exception ex)
                {
                    await LogExecutionEventAsync(logWriter, execution,
                        $"Dependency {dependency.TaskId} failed: {ex.Message}",
                        LogLevel.Error, ex);

                    throw new TaskDependencyException(
                        $"Failed to resolve dependency {dependency.TaskId}", ex);
                }
            }

            // Fire dependencies resolved event;
            OnTaskDependenciesResolved(new TaskDependenciesResolvedEventArgs;
            {
                ExecutionId = execution.Id,
                TaskId = execution.TaskId,
                Dependencies = resolvedDependencies,
                ResolvedCount = resolvedDependencies.Count(d => d.Success),
                FailedCount = resolvedDependencies.Count(d => !d.Success)
            });

            await LogExecutionEventAsync(logWriter, execution,
                $"Successfully resolved {resolvedDependencies.Count(d => d.Success)} dependencies",
                LogLevel.Info);
        }

        #endregion;

        #region Private Methods - Helper Methods;

        private void InitializeDefaultHandlers()
        {
            // Register default task handlers;
            RegisterTaskHandler("Command", new CommandTaskHandler());
            RegisterTaskHandler("Script", new ScriptTaskHandler());
            RegisterTaskHandler("PowerShell", new PowerShellTaskHandler());
            RegisterTaskHandler("Batch", new BatchTaskHandler());
            RegisterTaskHandler("Python", new PythonTaskHandler());
            RegisterTaskHandler("NodeJS", new NodeJSTaskHandler());
            RegisterTaskHandler("Process", new ProcessTaskHandler());
            RegisterTaskHandler("FileOperation", new FileOperationTaskHandler());
            RegisterTaskHandler("Database", new DatabaseTaskHandler());
            RegisterTaskHandler("WebRequest", new WebRequestTaskHandler());
            RegisterTaskHandler("Custom", new CustomTaskHandler());
        }

        private void InitializeScriptEngines()
        {
            // Register default script engines;
            RegisterScriptEngine("powershell", new PowerShellEngine());
            RegisterScriptEngine("python", new PythonEngine());
            RegisterScriptEngine("javascript", new JavaScriptEngine());
            RegisterScriptEngine("lua", new LuaEngine());
        }

        private async Task InitializeTaskHandlersAsync(CancellationToken cancellationToken)
        {
            foreach (var handler in _taskHandlers.Values)
            {
                try
                {
                    await handler.InitializeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to initialize task handler {handler.GetType().Name}: {ex.Message}", ex);
                }
            }
        }

        private async Task InitializeScriptEnginesAsync(CancellationToken cancellationToken)
        {
            foreach (var engine in _scriptEngines.Values)
            {
                try
                {
                    await engine.InitializeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to initialize script engine {engine.GetType().Name}: {ex.Message}", ex);
                }
            }
        }

        private void CreateRequiredDirectories()
        {
            var directories = new[]
            {
                _configuration.WorkingDirectory,
                Path.Combine(_configuration.WorkingDirectory, "tasks"),
                Path.Combine(_configuration.WorkingDirectory, "workspace"),
                Path.Combine(_configuration.WorkingDirectory, "history"),
                Path.Combine(_configuration.WorkingDirectory, "logs"),
                Path.Combine(_configuration.WorkingDirectory, "artifacts"),
                Path.Combine(_configuration.WorkingDirectory, "state")
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
        }

        private async Task RestoreInterruptedExecutionsAsync(CancellationToken cancellationToken)
        {
            var stateDir = Path.Combine(_configuration.WorkingDirectory, "state");
            if (!Directory.Exists(stateDir))
                return;

            var stateFiles = Directory.GetFiles(stateDir, $"*{TASK_STATE_FILE_EXTENSION}");

            foreach (var stateFile in stateFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var json = await File.ReadAllTextAsync(stateFile, cancellationToken);
                    var execution = JsonSerializer.Deserialize<TaskExecution>(json, _jsonOptions);

                    if (execution != null && execution.Status == TaskExecutionStatus.Running)
                    {
                        // Mark as failed since we don't know the actual state;
                        execution.Status = TaskExecutionStatus.Failed;
                        execution.CompletedAt = DateTime.UtcNow;
                        execution.Errors.Add("Task interrupted due to system restart");

                        // Save to history;
                        await SaveExecutionHistoryAsync(execution);

                        // Delete state file;
                        File.Delete(stateFile);

                        _logger.Warn($"Restored interrupted execution: {execution.Id}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to restore execution state from {stateFile}: {ex.Message}");
                }
            }
        }

        private string GenerateExecutionId(TaskExecutionRequest request)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            var prefix = request.TaskId.Replace("-", "").Substring(0, Math.Min(4, request.TaskId.Length)).ToUpper();

            return $"{prefix}-{timestamp}-{random}";
        }

        private string GetExecutionWorkingDirectory(string executionId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "workspace",
                executionId);
        }

        private string GetLogFilePath(string executionId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "logs",
                $"{TASK_LOG_FILE_PREFIX}{executionId}.log");
        }

        private string GetStateFilePath(string executionId)
        {
            return Path.Combine(_configuration.WorkingDirectory,
                "state",
                $"{executionId}{TASK_STATE_FILE_EXTENSION}");
        }

        private async Task SaveExecutionHistoryAsync(TaskExecution execution)
        {
            var history = execution.ToHistory();
            var historyFile = Path.Combine(_configuration.WorkingDirectory,
                "history",
                $"{execution.Id}.history.json");

            var json = JsonSerializer.Serialize(history, _jsonOptions);
            await File.WriteAllTextAsync(historyFile, json, Encoding.UTF8);
        }

        private async Task<TaskExecutionHistory> LoadExecutionHistoryAsync(string executionId)
        {
            var historyFile = Path.Combine(_configuration.WorkingDirectory,
                "history",
                $"{executionId}.history.json");

            if (!File.Exists(historyFile))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(historyFile);
                return JsonSerializer.Deserialize<TaskExecutionHistory>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to load execution history for {executionId}: {ex.Message}");
                return null;
            }
        }

        private bool MatchesCriteria(TaskExecutionHistory history, ExecutionHistoryCriteria criteria)
        {
            if (criteria == null)
                return true;

            if (!string.IsNullOrWhiteSpace(criteria.TaskId) &&
                history.TaskId != criteria.TaskId)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.TaskType) &&
                history.TaskType != criteria.TaskType)
                return false;

            if (criteria.Status.HasValue && history.Status != criteria.Status.Value)
                return false;

            if (criteria.StartedAfter.HasValue && history.StartedAt < criteria.StartedAfter.Value)
                return false;

            if (criteria.StartedBefore.HasValue && history.StartedAt > criteria.StartedBefore.Value)
                return false;

            if (criteria.MinDuration.HasValue && history.ExecutionTime < criteria.MinDuration.Value)
                return false;

            if (criteria.MaxDuration.HasValue && history.ExecutionTime > criteria.MaxDuration.Value)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.CreatedBy) &&
                history.CreatedBy != criteria.CreatedBy)
                return false;

            if (criteria.HasErrors.HasValue)
            {
                if (criteria.HasErrors.Value && history.Errors.Count == 0)
                    return false;
                if (!criteria.HasErrors.Value && history.Errors.Count > 0)
                    return false;
            }

            return true;
        }

        private async Task LogExecutionEventAsync(
            StreamWriter logWriter,
            TaskExecution execution,
            string message,
            LogLevel level,
            Exception exception = null)
        {
            var logEntry = new TaskLogEntry
            {
                Timestamp = DateTime.UtcNow,
                ExecutionId = execution.Id,
                TaskId = execution.TaskId,
                Level = level,
                Message = message,
                Exception = exception?.ToString()
            };

            var json = JsonSerializer.Serialize(logEntry, _jsonOptions);
            await logWriter.WriteLineAsync(json);
            await logWriter.FlushAsync();

            // Also log to system logger;
            switch (level)
            {
                case LogLevel.Debug:
                    _logger.Debug($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Info:
                    _logger.Info($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Warning:
                    _logger.Warn($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Error:
                    _logger.Error($"[{execution.Id}] {message}", exception);
                    break;
                case LogLevel.Fatal:
                    _logger.Fatal($"[{execution.Id}] {message}", exception);
                    break;
            }

            // Fire progress event for informational messages;
            if (level == LogLevel.Info)
            {
                OnTaskProgress(new TaskProgressEventArgs;
                {
                    ExecutionId = execution.Id,
                    TaskId = execution.TaskId,
                    Message = message,
                    ProgressPercentage = execution.ProgressPercentage,
                    Timestamp = DateTime.UtcNow;
                });
            }

            // Fire output event;
            OnTaskOutput(new TaskOutputEventArgs;
            {
                ExecutionId = execution.Id,
                TaskId = execution.TaskId,
                OutputType = level == LogLevel.Error ? TaskOutputType.Error : TaskOutputType.Standard,
                Output = message,
                Timestamp = DateTime.UtcNow;
            });
        }

        private string GetCurrentUser()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return WindowsIdentity.GetCurrent().Name;
                }
                else;
                {
                    return Environment.UserName;
                }
            }
            catch
            {
                return "SYSTEM";
            }
        }

        private void UpdatePeakConcurrentTasks()
        {
            var currentCount = _activeExecutions.Count;
            if (currentCount > _peakConcurrentTasks)
            {
                Interlocked.Exchange(ref _peakConcurrentTasks, currentCount);
            }
        }

        private void UpdatePeakMemoryUsage()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var memoryUsage = process.WorkingSet64;

                if (memoryUsage > _peakMemoryUsage)
                {
                    Interlocked.Exchange(ref _peakMemoryUsage, memoryUsage);
                }
            }
            catch
            {
                // Ignore memory monitoring errors;
            }
        }

        private TaskExecutionStatistics GetStatistics()
        {
            lock (_syncLock)
            {
                var activeExecutions = _activeExecutions.Values.ToList();

                return new TaskExecutionStatistics;
                {
                    TotalExecutions = _totalExecutions,
                    ActiveExecutions = activeExecutions.Count,
                    QueuedExecutions = activeExecutions.Count(e => e.Status == TaskExecutionStatus.Queued),
                    RunningExecutions = activeExecutions.Count(e => e.Status == TaskExecutionStatus.Running),
                    SuccessfulExecutions = _totalExecutions - activeExecutions.Count, // Simplified;
                    FailedExecutions = 0, // Would need to track in history;
                    CancelledExecutions = 0, // Would need to track in history;
                    TimedOutExecutions = 0, // Would need to track in history;
                    AverageExecutionTimeMs = AverageExecutionTimeMs,
                    PeakConcurrentTasks = _peakConcurrentTasks,
                    PeakMemoryUsageBytes = _peakMemoryUsage,
                    MaxConcurrentTasks = _configuration.MaxConcurrentTasks,
                    AvailableSlots = _concurrencySemaphore.CurrentCount,
                    LastUpdated = DateTime.UtcNow;
                };
            }
        }

        #endregion;

        #region Private Methods - Timer Callbacks;

        private void HeartbeatCallback(object state)
        {
            if (_disposed || !_isInitialized)
                return;

            try
            {
                // Save state of active executions;
                SaveActiveExecutionsState();

                // Update performance metrics;
                UpdatePerformanceMetrics();

                // Check for task timeouts;
                CheckTaskTimeouts();

                // Monitor resource usage;
                MonitorResourceUsage();
            }
            catch (Exception ex)
            {
                _logger.Error($"Heartbeat callback failed: {ex.Message}", ex);
            }
        }

        private void CleanupCallback(object state)
        {
            if (_disposed || !_isInitialized)
                return;

            try
            {
                // Clean up old workspace directories;
                CleanupWorkspaceDirectories();

                // Clean up old logs;
                CleanupOldLogs();

                // Archive old history files;
                ArchiveHistoryFiles();
            }
            catch (Exception ex)
            {
                _logger.Error($"Cleanup callback failed: {ex.Message}", ex);
            }
        }

        private void SaveActiveExecutionsState()
        {
            lock (_syncLock)
            {
                foreach (var execution in _activeExecutions.Values)
                {
                    if (execution.Status == TaskExecutionStatus.Running)
                    {
                        try
                        {
                            var stateFile = GetStateFilePath(execution.Id);
                            var json = JsonSerializer.Serialize(execution, _jsonOptions);
                            File.WriteAllText(stateFile, json, Encoding.UTF8);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"Failed to save state for execution {execution.Id}: {ex.Message}");
                        }
                    }
                }
            }
        }

        private void UpdatePerformanceMetrics()
        {
            if (_performanceMonitor == null)
                return;

            try
            {
                _performanceMonitor.UpdateCounter("TaskExecutor.ActiveExecutions", ActiveExecutionCount);
                _performanceMonitor.UpdateCounter("TaskExecutor.TotalExecutions", TotalExecutionCount);
                _performanceMonitor.UpdateCounter("TaskExecutor.AverageExecutionTime", AverageExecutionTimeMs);
                _performanceMonitor.UpdateCounter("TaskExecutor.PeakConcurrentTasks", PeakConcurrentTasks);
                _performanceMonitor.UpdateCounter("TaskExecutor.PeakMemoryUsageMB", PeakMemoryUsageBytes / 1024.0 / 1024);

                var stats = GetStatistics();
                _performanceMonitor.UpdateCounter("TaskExecutor.QueuedExecutions", stats.QueuedExecutions);
                _performanceMonitor.UpdateCounter("TaskExecutor.RunningExecutions", stats.RunningExecutions);
                _performanceMonitor.UpdateCounter("TaskExecutor.AvailableSlots", stats.AvailableSlots);

                // Update handler-specific metrics;
                foreach (var handler in _taskHandlers.Values)
                {
                    var handlerMetrics = handler.GetMetrics();
                    foreach (var metric in handlerMetrics)
                    {
                        _performanceMonitor.UpdateCounter($"TaskExecutor.Handler.{handler.GetType().Name}.{metric.Key}", metric.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to update performance metrics: {ex.Message}");
            }
        }

        private void CheckTaskTimeouts()
        {
            var now = DateTime.UtcNow;

            lock (_syncLock)
            {
                foreach (var execution in _activeExecutions.Values)
                {
                    if (execution.Status == TaskExecutionStatus.Running &&
                        execution.StartedAt.HasValue &&
                        now - execution.StartedAt.Value > execution.Timeout)
                    {
                        _logger.Warn($"Execution {execution.Id} has timed out, cancelling...");

                        // Try to cancel the execution;
                        if (_executionContexts.TryGetValue(execution.Id, out var context))
                        {
                            context.CancellationSource.Cancel();

                            // Update status;
                            execution.Status = TaskExecutionStatus.TimedOut;
                            execution.TimeoutOccurred = true;
                            execution.CompletedAt = now;
                            execution.Errors.Add($"Execution timed out after {execution.Timeout.TotalSeconds} seconds");

                            // Remove from active executions;
                            _activeExecutions.Remove(execution.Id);

                            // Save to history;
                            SaveExecutionHistoryAsync(execution).ConfigureAwait(false);

                            // Release semaphore;
                            _concurrencySemaphore.Release();

                            // Fire timeout event;
                            OnTaskExecutionTimeout(new TaskExecutionTimeoutEventArgs;
                            {
                                ExecutionId = execution.Id,
                                TaskId = execution.TaskId,
                                TaskName = execution.Name,
                                Timeout = execution.Timeout,
                                TimedOutAt = now;
                            });
                        }
                    }
                }
            }
        }

        private void MonitorResourceUsage()
        {
            try
            {
                var process = Process.GetCurrentProcess();

                // Monitor CPU usage;
                var cpuTime = process.TotalProcessorTime;

                // Monitor memory usage;
                var memoryUsage = process.WorkingSet64;
                UpdatePeakMemoryUsage();

                // Monitor thread count;
                var threadCount = process.Threads.Count;

                // Log warnings if thresholds exceeded;
                if (memoryUsage > _configuration.MemoryUsageWarningThreshold)
                {
                    _logger.Warn($"High memory usage: {memoryUsage / 1024 / 1024}MB");
                }

                if (threadCount > _configuration.ThreadCountWarningThreshold)
                {
                    _logger.Warn($"High thread count: {threadCount}");
                }

                // Check disk space;
                var drive = new DriveInfo(Path.GetPathRoot(_configuration.WorkingDirectory));
                var freeSpacePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100;

                if (freeSpacePercent < 10)
                {
                    _logger.Warn($"Low disk space: {freeSpacePercent:F1}% free on {drive.Name}");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Resource monitoring failed: {ex.Message}");
            }
        }

        private void CleanupWorkspaceDirectories()
        {
            var workspaceDir = Path.Combine(_configuration.WorkingDirectory, "workspace");
            if (!Directory.Exists(workspaceDir))
                return;

            var cutoffDate = DateTime.UtcNow.AddHours(-_configuration.WorkspaceCleanupHours);

            foreach (var dir in Directory.GetDirectories(workspaceDir))
            {
                try
                {
                    var dirInfo = new DirectoryInfo(dir);
                    if (dirInfo.LastWriteTimeUtc < cutoffDate)
                    {
                        Directory.Delete(dir, true);
                        _logger.Debug($"Cleaned up workspace directory: {dir}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to clean up workspace directory {dir}: {ex.Message}");
                }
            }
        }

        private void CleanupOldLogs()
        {
            var logsDir = Path.Combine(_configuration.WorkingDirectory, "logs");
            if (!Directory.Exists(logsDir))
                return;

            var cutoffDate = DateTime.UtcNow.AddDays(-_configuration.LogRetentionDays);

            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffDate)
                    {
                        File.Delete(file);
                        _logger.Debug($"Cleaned up log file: {file}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to clean up log file {file}: {ex.Message}");
                }
            }
        }

        private void ArchiveHistoryFiles()
        {
            var historyDir = Path.Combine(_configuration.WorkingDirectory, "history");
            if (!Directory.Exists(historyDir))
                return;

            var cutoffDate = DateTime.UtcNow.AddDays(-_configuration.HistoryRetentionDays);
            var archiveDir = Path.Combine(_configuration.WorkingDirectory, "archive");

            if (!Directory.Exists(archiveDir))
            {
                Directory.CreateDirectory(archiveDir);
            }

            foreach (var file in Directory.GetFiles(historyDir, "*.history.json"))
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffDate)
                    {
                        var archiveFile = Path.Combine(archiveDir, $"{Path.GetFileName(file)}.gz");

                        // Compress and archive;
                        using var originalFileStream = File.OpenRead(file);
                        using var archiveFileStream = File.Create(archiveFile);
                        using var compressionStream = new GZipStream(archiveFileStream, CompressionMode.Compress);
                        originalFileStream.CopyTo(compressionStream);

                        // Delete original file;
                        File.Delete(file);

                        _logger.Debug($"Archived history file: {file} -> {archiveFile}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Failed to archive history file {file}: {ex.Message}");
                }
            }
        }

        #endregion;

        #region Private Methods - Event Handling;

        private void OnTaskExecutionStarted(TaskExecutionStartedEventArgs e)
        {
            TaskExecutionStarted?.Invoke(this, e);
        }

        private void OnTaskExecutionCompleted(TaskExecutionCompletedEventArgs e)
        {
            TaskExecutionCompleted?.Invoke(this, e);
        }

        private void OnTaskExecutionFailed(TaskExecutionFailedEventArgs e)
        {
            TaskExecutionFailed?.Invoke(this, e);
        }

        private void OnTaskExecutionTimeout(TaskExecutionTimeoutEventArgs e)
        {
            TaskExecutionTimeout?.Invoke(this, e);
        }

        private void OnTaskProgress(TaskProgressEventArgs e)
        {
            TaskProgress?.Invoke(this, e);
        }

        private void OnTaskOutput(TaskOutputEventArgs e)
        {
            TaskOutput?.Invoke(this, e);
        }

        private void OnTaskStatusChanged(TaskStatusChangedEventArgs e)
        {
            TaskStatusChanged?.Invoke(this, e);
        }

        private void OnTaskQueued(TaskQueuedEventArgs e)
        {
            TaskQueued?.Invoke(this, e);
        }

        private void OnTaskCancelled(TaskCancelledEventArgs e)
        {
            TaskCancelled?.Invoke(this, e);
        }

        private void OnTaskDependenciesResolved(TaskDependenciesResolvedEventArgs e)
        {
            TaskDependenciesResolved?.Invoke(this, e);
        }

        #endregion;

        #region Validation Methods;

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TaskExecutor));
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("TaskExecutor is not initialized");
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cancel all operations;
                    _globalCancellation.Cancel();

                    // Dispose timers;
                    _heartbeatTimer?.Dispose();
                    _cleanupTimer?.Dispose();

                    // Wait for active executions to complete;
                    Task.WhenAll(_activeExecutions.Values;
                        .Select(e => e.CompletionTask ?? Task.CompletedTask))
                        .Wait(TimeSpan.FromSeconds(30));

                    // Dispose task handlers;
                    foreach (var handler in _taskHandlers.Values)
                    {
                        handler.Dispose();
                    }

                    // Dispose script engines;
                    foreach (var engine in _scriptEngines.Values)
                    {
                        engine.Dispose();
                    }

                    // Dispose semaphore;
                    _concurrencySemaphore?.Dispose();

                    // Dispose cancellation token source;
                    _globalCancellation.Dispose();

                    // Dispose dependencies;
                    _validator?.Dispose();
                    _securityManager?.Dispose();
                    _taskMonitor?.Dispose();
                    _retryStrategy?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~TaskExecutor()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Task execution context.
        /// </summary>
        private class TaskExecutionContext;
        {
            public TaskExecution Execution { get; set; }
            public TaskExecutionRequest Request { get; set; }
            public CancellationTokenSource CancellationSource { get; set; }
            public string WorkingDirectory { get; set; }
            public string LogFile { get; set; }
        }

        #endregion;
    }
}
