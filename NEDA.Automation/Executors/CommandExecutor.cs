using NEDA.API.Versioning;
using NEDA.Brain.DecisionMaking;
using NEDA.Brain.NeuralNetwork;
using NEDA.Communication.DialogSystem;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Services.Messaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.Core.Commands;
{
    /// <summary>
    /// Executes commands with advanced processing, security, and AI integration;
    /// </summary>
    public class CommandExecutor : ICommandExecutor, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IPermissionValidator _permissionValidator;
        private readonly ICommandRegistry _commandRegistry
        private readonly IAuditLogger _auditLogger;
        private readonly IConversationEngine _conversationEngine;
        private readonly IEventBus _eventBus;
        private readonly INeuralNetwork _neuralNetwork;
        private readonly IDecisionEngine _decisionEngine;

        private readonly ConcurrentDictionary<Guid, CommandExecution> _activeExecutions;
        private readonly ConcurrentDictionary<string, CommandPerformance> _performanceMetrics;
        private readonly ConcurrentQueue<CommandRequest> _priorityQueue;
        private readonly ConcurrentQueue<CommandRequest> _normalQueue;

        private readonly SemaphoreSlim _queueSemaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _syncLock = new object();

        private Task _executionTask;
        private Task _monitoringTask;
        private bool _disposed = false;
        private int _maxConcurrentExecutions;
        private int _currentExecutions;

        /// <summary>
        /// Maximum concurrent command executions;
        /// </summary>
        public int MaxConcurrentExecutions;
        {
            get => _maxConcurrentExecutions;
            set;
            {
                if (value < 1) throw new ArgumentException("Max concurrent executions must be at least 1");
                _maxConcurrentExecutions = value;
            }
        }

        /// <summary>
        /// Command execution timeout;
        /// </summary>
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether to enable AI optimization;
        /// </summary>
        public bool EnableAIOptimization { get; set; } = true;

        /// <summary>
        /// Whether to enable predictive execution;
        /// </summary>
        public bool EnablePredictiveExecution { get; set; } = true;

        /// <summary>
        /// Current execution statistics;
        /// </summary>
        public ExecutionStatistics Statistics { get; private set; }

        /// <summary>
        /// Initialize a new CommandExecutor;
        /// </summary>
        public CommandExecutor(
            ILogger logger,
            ISecurityManager securityManager,
            IPermissionValidator permissionValidator,
            ICommandRegistry commandRegistry,
            IAuditLogger auditLogger,
            IConversationEngine conversationEngine = null,
            IEventBus eventBus = null,
            INeuralNetwork neuralNetwork = null,
            IDecisionEngine decisionEngine = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _permissionValidator = permissionValidator ?? throw new ArgumentNullException(nameof(permissionValidator));
            _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _conversationEngine = conversationEngine;
            _eventBus = eventBus;
            _neuralNetwork = neuralNetwork;
            _decisionEngine = decisionEngine;

            _activeExecutions = new ConcurrentDictionary<Guid, CommandExecution>();
            _performanceMetrics = new ConcurrentDictionary<string, CommandPerformance>();
            _priorityQueue = new ConcurrentQueue<CommandRequest>();
            _normalQueue = new ConcurrentQueue<CommandRequest>();

            MaxConcurrentExecutions = Environment.ProcessorCount * 2;
            Statistics = new ExecutionStatistics();

            InitializePerformanceMetrics();
            StartExecutionEngine();
            StartMonitoring();

            _logger.Information("CommandExecutor initialized successfully");
        }

        /// <summary>
        /// Execute a command asynchronously;
        /// </summary>
        public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var executionId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;
            var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            timeoutTokenSource.CancelAfter(request.Timeout ?? DefaultTimeout);

            CommandExecution execution = null;

            try
            {
                // Log execution start;
                await _auditLogger.LogCommandExecutionStartAsync(executionId, request);

                // Validate and pre-process request;
                var validationResult = await ValidateAndPreprocessAsync(request);
                if (!validationResult.IsValid)
                {
                    return CreateErrorResult(validationResult.ErrorMessage, CommandExecutionStatus.ValidationFailed);
                }

                // Check permissions;
                var permissionResult = await CheckPermissionsAsync(request);
                if (!permissionResult.IsAllowed)
                {
                    await _auditLogger.LogSecurityViolationAsync(executionId, request, permissionResult);
                    return CreateErrorResult($"Permission denied: {permissionResult.Reason}", CommandExecutionStatus.PermissionDenied);
                }

                // Get command handler;
                var commandHandler = _commandRegistry.GetCommandHandler(request.CommandName);
                if (commandHandler == null)
                {
                    return CreateErrorResult($"Command '{request.CommandName}' not found", CommandExecutionStatus.CommandNotFound);
                }

                // Create execution context;
                var context = CreateExecutionContext(request, executionId);

                // Create execution tracking object;
                execution = new CommandExecution;
                {
                    Id = executionId,
                    Request = request,
                    Context = context,
                    StartTime = startTime,
                    Status = CommandExecutionStatus.Executing,
                    CancellationToken = timeoutTokenSource.Token;
                };

                // Add to active executions;
                _activeExecutions[executionId] = execution;
                Interlocked.Increment(ref _currentExecutions);

                // Update statistics;
                UpdateStatistics(CommandExecutionStatus.Executing);

                // Execute command;
                CommandResult result;
                try
                {
                    // Pre-execution hook;
                    await OnPreExecutionAsync(execution);

                    // Execute the command;
                    result = await commandHandler.ExecuteAsync(request, context, timeoutTokenSource.Token);

                    // Post-execution hook;
                    await OnPostExecutionAsync(execution, result);
                }
                catch (OperationCanceledException) when (timeoutTokenSource.IsCancellationRequested)
                {
                    result = CreateErrorResult("Command execution timeout", CommandExecutionStatus.Timeout);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Command execution error: {ex.Message}", ex);
                    result = CreateErrorResult($"Execution error: {ex.Message}", CommandExecutionStatus.Failed, ex);
                }

                // Set execution end time and status;
                execution.EndTime = DateTime.UtcNow;
                execution.Status = result.Status;
                execution.Result = result;

                // Update performance metrics;
                UpdatePerformanceMetrics(request.CommandName, execution);

                // Update statistics;
                UpdateStatistics(result.Status);

                // Log completion;
                await _auditLogger.LogCommandExecutionCompleteAsync(executionId, request, result);

                // Publish execution event;
                await PublishExecutionEventAsync(execution, result);

                // AI learning from execution;
                if (EnableAIOptimization && _neuralNetwork != null)
                {
                    await LearnFromExecutionAsync(execution, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error in command execution: {ex.Message}", ex);
                return CreateErrorResult($"System error: {ex.Message}", CommandExecutionStatus.SystemError, ex);
            }
            finally
            {
                // Clean up;
                if (execution != null)
                {
                    _activeExecutions.TryRemove(executionId, out _);
                    Interlocked.Decrement(ref _currentExecutions);
                }

                timeoutTokenSource?.Dispose();
            }
        }

        /// <summary>
        /// Execute a command with priority (bypasses queue)
        /// </summary>
        public async Task<CommandResult> ExecuteWithPriorityAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // Mark as priority;
            request.Priority = CommandPriority.High;

            // Execute immediately;
            return await ExecuteAsync(request, cancellationToken);
        }

        /// <summary>
        /// Execute a batch of commands;
        /// </summary>
        public async Task<BatchExecutionResult> ExecuteBatchAsync(IEnumerable<CommandRequest> requests, BatchExecutionOptions options = null)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));

            options ??= new BatchExecutionOptions();
            var batchId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            var batchResult = new BatchExecutionResult;
            {
                BatchId = batchId,
                StartTime = startTime,
                Options = options;
            };

            var executionTasks = new List<Task<CommandResult>>();
            var requestList = requests.ToList();

            _logger.Information($"Starting batch execution {batchId} with {requestList.Count} commands");

            // Apply AI optimization if enabled;
            if (EnableAIOptimization && options.OptimizeExecutionOrder && _neuralNetwork != null)
            {
                requestList = await OptimizeExecutionOrderAsync(requestList);
            }

            // Execute commands based on options;
            if (options.ExecuteInParallel)
            {
                // Parallel execution with concurrency limit;
                var semaphore = new SemaphoreSlim(options.MaxConcurrent ?? MaxConcurrentExecutions);

                foreach (var request in requestList)
                {
                    var task = ExecuteWithConcurrencyControlAsync(request, semaphore, options.CancellationToken);
                    executionTasks.Add(task);
                }

                await Task.WhenAll(executionTasks);
            }
            else;
            {
                // Sequential execution;
                foreach (var request in requestList)
                {
                    var result = await ExecuteAsync(request, options.CancellationToken);
                    executionTasks.Add(Task.FromResult(result));
                }
            }

            // Collect results;
            var results = new List<CommandResult>();
            foreach (var task in executionTasks)
            {
                results.Add(await task);
            }

            batchResult.EndTime = DateTime.UtcNow;
            batchResult.Results = results;
            batchResult.SuccessCount = results.Count(r => r.Status == CommandExecutionStatus.Completed);
            batchResult.FailedCount = results.Count(r => r.Status != CommandExecutionStatus.Completed);
            batchResult.TotalDuration = batchResult.EndTime - batchResult.StartTime;

            // Analyze batch results;
            await AnalyzeBatchResultsAsync(batchResult);

            _logger.Information($"Batch execution {batchId} completed: {batchResult.SuccessCount} successful, {batchResult.FailedCount} failed");

            return batchResult;
        }

        /// <summary>
        /// Queue a command for execution;
        /// </summary>
        public async Task<Guid> QueueCommandAsync(CommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var queueId = Guid.NewGuid();
            request.QueueId = queueId;

            try
            {
                await _queueSemaphore.WaitAsync();

                if (request.Priority >= CommandPriority.High)
                {
                    _priorityQueue.Enqueue(request);
                    _logger.Debug($"Queued priority command: {request.CommandName} (ID: {queueId})");
                }
                else;
                {
                    _normalQueue.Enqueue(request);
                    _logger.Debug($"Queued normal command: {request.CommandName} (ID: {queueId})");
                }

                // Trigger execution engine if idle;
                if (_currentExecutions < MaxConcurrentExecutions)
                {
                    _ = Task.Run(() => ProcessQueuedCommandsAsync());
                }

                return queueId;
            }
            finally
            {
                _queueSemaphore.Release();
            }
        }

        /// <summary>
        /// Cancel a queued or executing command;
        /// </summary>
        public async Task<bool> CancelCommandAsync(Guid executionId, string reason = null)
        {
            if (_activeExecutions.TryGetValue(executionId, out var execution))
            {
                try
                {
                    execution.CancellationTokenSource?.Cancel();
                    execution.Status = CommandExecutionStatus.Cancelled;
                    execution.CancellationReason = reason;

                    await _auditLogger.LogCommandCancellationAsync(executionId, reason);

                    _logger.Information($"Command {executionId} cancelled: {reason}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error cancelling command {executionId}: {ex.Message}", ex);
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Get execution status;
        /// </summary>
        public CommandExecutionStatus GetExecutionStatus(Guid executionId)
        {
            if (_activeExecutions.TryGetValue(executionId, out var execution))
            {
                return execution.Status;
            }

            return CommandExecutionStatus.NotFound;
        }

        /// <summary>
        /// Get performance metrics for a command;
        /// </summary>
        public CommandPerformance GetCommandPerformance(string commandName)
        {
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Command name cannot be null or empty", nameof(commandName));

            if (_performanceMetrics.TryGetValue(commandName, out var performance))
            {
                return performance.Clone();
            }

            return new CommandPerformance { CommandName = commandName };
        }

        /// <summary>
        /// Get all active executions;
        /// </summary>
        public IReadOnlyList<CommandExecution> GetActiveExecutions()
        {
            return _activeExecutions.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Clear command queue;
        /// </summary>
        public async Task ClearQueueAsync(QueueClearOptions options = null)
        {
            options ??= new QueueClearOptions();

            try
            {
                await _queueSemaphore.WaitAsync();

                int clearedCount = 0;

                if (options.ClearPriorityQueue)
                {
                    while (_priorityQueue.TryDequeue(out _))
                    {
                        clearedCount++;
                    }
                }

                if (options.ClearNormalQueue)
                {
                    while (_normalQueue.TryDequeue(out _))
                    {
                        clearedCount++;
                    }
                }

                _logger.Information($"Cleared command queue: {clearedCount} commands removed");

                if (options.LogAudit)
                {
                    await _auditLogger.LogQueueClearedAsync(clearedCount, options.Reason);
                }
            }
            finally
            {
                _queueSemaphore.Release();
            }
        }

        /// <summary>
        /// Optimize command execution using AI;
        /// </summary>
        public async Task<ExecutionOptimization> OptimizeExecutionAsync(IEnumerable<CommandRequest> commands)
        {
            if (!EnableAIOptimization || _neuralNetwork == null)
            {
                return new ExecutionOptimization { IsOptimized = false };
            }

            var commandList = commands.ToList();
            if (commandList.Count == 0)
            {
                return new ExecutionOptimization { IsOptimized = false };
            }

            try
            {
                var optimization = new ExecutionOptimization;
                {
                    OriginalCommands = commandList,
                    OptimizationDate = DateTime.UtcNow;
                };

                // Analyze command dependencies;
                var dependencies = await AnalyzeDependenciesAsync(commandList);

                // Predict execution times;
                var predictedTimes = await PredictExecutionTimesAsync(commandList);

                // Optimize execution order;
                var optimizedOrder = await OptimizeExecutionOrderInternalAsync(commandList, dependencies, predictedTimes);

                // Calculate expected improvements;
                var improvements = await CalculateOptimizationImprovementsAsync(commandList, optimizedOrder);

                optimization.OptimizedCommands = optimizedOrder;
                optimization.ExpectedImprovement = improvements;
                optimization.IsOptimized = true;
                optimization.OptimizationStrategy = GetOptimizationStrategy(commandList);

                return optimization;
            }
            catch (Exception ex)
            {
                _logger.Error($"AI optimization failed: {ex.Message}", ex);
                return new ExecutionOptimization { IsOptimized = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Validate command syntax and parameters;
        /// </summary>
        public async Task<CommandValidationResult> ValidateCommandAsync(CommandRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var result = new CommandValidationResult;
            {
                CommandName = request.CommandName,
                ValidationTime = DateTime.UtcNow;
            };

            try
            {
                // Check if command exists;
                var handler = _commandRegistry.GetCommandHandler(request.CommandName);
                if (handler == null)
                {
                    result.IsValid = false;
                    result.Errors.Add($"Command '{request.CommandName}' not found");
                    return result;
                }

                // Validate parameters;
                var parameterValidation = await handler.ValidateParametersAsync(request.Parameters);
                if (!parameterValidation.IsValid)
                {
                    result.IsValid = false;
                    result.Errors.AddRange(parameterValidation.Errors);
                    return result;
                }

                // Check resource requirements;
                var resourceCheck = await CheckResourceRequirementsAsync(request);
                if (!resourceCheck.Available)
                {
                    result.IsValid = false;
                    result.Warnings.Add($"Resource warning: {resourceCheck.Message}");
                }

                result.IsValid = true;
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Command validation error: {ex.Message}", ex);
                result.IsValid = false;
                result.Errors.Add($"Validation error: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Start the execution engine;
        /// </summary>
        private void StartExecutionEngine()
        {
            _executionTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessQueuedCommandsAsync();
                        await Task.Delay(100, _cts.Token); // Small delay to prevent tight loop;
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Execution engine error: {ex.Message}", ex);
                        await Task.Delay(1000, _cts.Token); // Wait on error;
                    }
                }
            }, _cts.Token);
        }

        /// <summary>
        /// Start monitoring task;
        /// </summary>
        private void StartMonitoring()
        {
            _monitoringTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                        await PerformMonitoringAsync();
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Monitoring error: {ex.Message}", ex);
                    }
                }
            }, _cts.Token);
        }

        /// <summary>
        /// Process queued commands;
        /// </summary>
        private async Task ProcessQueuedCommandsAsync()
        {
            while (_currentExecutions < MaxConcurrentExecutions && !_cts.Token.IsCancellationRequested)
            {
                CommandRequest request = null;

                // Try priority queue first;
                if (!_priorityQueue.TryDequeue(out request))
                {
                    // Then normal queue;
                    if (!_normalQueue.TryDequeue(out request))
                    {
                        break; // No more commands in queue;
                    }
                }

                if (request != null)
                {
                    // Execute command in background;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecuteAsync(request, _cts.Token);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Error executing queued command {request.CommandName}: {ex.Message}", ex);
                        }
                    }, _cts.Token);
                }

                // Small delay to prevent overwhelming the system;
                await Task.Delay(10, _cts.Token);
            }
        }

        /// <summary>
        /// Perform monitoring tasks;
        /// </summary>
        private async Task PerformMonitoringAsync()
        {
            // Check for stalled executions;
            var stalledExecutions = _activeExecutions.Values;
                .Where(e => e.Status == CommandExecutionStatus.Executing)
                .Where(e => (DateTime.UtcNow - e.StartTime) > TimeSpan.FromMinutes(5))
                .ToList();

            foreach (var execution in stalledExecutions)
            {
                _logger.Warning($"Stalled execution detected: {execution.Id} - {execution.Request.CommandName}");

                // Attempt to cancel stalled execution;
                await CancelCommandAsync(execution.Id, "Execution stalled - timeout");
            }

            // Update performance statistics;
            UpdatePerformanceStatistics();

            // Check system load;
            CheckSystemLoad();

            // Predictive execution if enabled;
            if (EnablePredictiveExecution && _neuralNetwork != null)
            {
                await PerformPredictiveExecutionAsync();
            }
        }

        /// <summary>
        /// Perform predictive execution;
        /// </summary>
        private async Task PerformPredictiveExecutionAsync()
        {
            try
            {
                // Analyze patterns and predict next commands;
                var predictedCommands = await PredictNextCommandsAsync();

                foreach (var prediction in predictedCommands.Where(p => p.Confidence > 0.7))
                {
                    // Pre-load resources or warm up for predicted command;
                    await PreloadForPredictedCommandAsync(prediction);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Predictive execution error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Validate and pre-process command request;
        /// </summary>
        private async Task<ValidationResult> ValidateAndPreprocessAsync(CommandRequest request)
        {
            var result = new ValidationResult();

            if (string.IsNullOrWhiteSpace(request.CommandName))
            {
                result.IsValid = false;
                result.ErrorMessage = "Command name is required";
                return result;
            }

            // Sanitize inputs;
            request.Parameters = SanitizeParameters(request.Parameters);

            // Apply AI preprocessing if available;
            if (_conversationEngine != null && request.Source == CommandSource.Conversation)
            {
                request = await _conversationEngine.PreprocessCommandAsync(request);
            }

            result.IsValid = true;
            return result;
        }

        /// <summary>
        /// Check permissions for command execution;
        /// </summary>
        private async Task<PermissionResult> CheckPermissionsAsync(CommandRequest request)
        {
            var result = new PermissionResult;
            {
                CommandName = request.CommandName,
                UserId = request.UserId,
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Check security clearance;
                var clearance = await _securityManager.CheckClearanceAsync(request.UserId, request.CommandName);
                if (!clearance.IsCleared)
                {
                    result.IsAllowed = false;
                    result.Reason = $"Security clearance denied: {clearance.Reason}";
                    return result;
                }

                // Check specific permissions;
                var permissionCheck = await _permissionValidator.ValidateAsync(
                    request.UserId,
                    request.CommandName,
                    request.Parameters);

                result.IsAllowed = permissionCheck.IsAllowed;
                result.Reason = permissionCheck.IsAllowed ? "Permission granted" : permissionCheck.Reason;
                result.Details = permissionCheck.Details;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Permission check error: {ex.Message}", ex);
                result.IsAllowed = false;
                result.Reason = $"Permission check error: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Create execution context;
        /// </summary>
        private ExecutionContext CreateExecutionContext(CommandRequest request, Guid executionId)
        {
            return new ExecutionContext;
            {
                ExecutionId = executionId,
                UserId = request.UserId,
                SessionId = request.SessionId,
                CommandName = request.CommandName,
                Parameters = request.Parameters,
                StartTime = DateTime.UtcNow,
                SecurityContext = new SecurityContext;
                {
                    UserId = request.UserId,
                    IPAddress = request.IPAddress,
                    UserAgent = request.UserAgent,
                    AuthenticationLevel = request.AuthenticationLevel;
                },
                Environment = new ExecutionEnvironment;
                {
                    MachineName = Environment.MachineName,
                    ProcessorCount = Environment.ProcessorCount,
                    AvailableMemory = GetAvailableMemory(),
                    CurrentDirectory = Environment.CurrentDirectory;
                }
            };
        }

        /// <summary>
        /// Pre-execution hook;
        /// </summary>
        private async Task OnPreExecutionAsync(CommandExecution execution)
        {
            // Log pre-execution;
            _logger.Debug($"Starting command execution: {execution.Request.CommandName} (ID: {execution.Id})");

            // Publish pre-execution event;
            if (_eventBus != null)
            {
                await _eventBus.PublishAsync(new CommandExecutionEvent;
                {
                    ExecutionId = execution.Id,
                    CommandName = execution.Request.CommandName,
                    Status = CommandExecutionStatus.Starting,
                    Timestamp = DateTime.UtcNow,
                    UserId = execution.Request.UserId;
                });
            }

            // AI pre-execution analysis;
            if (_neuralNetwork != null && EnableAIOptimization)
            {
                await AnalyzeExecutionPatternAsync(execution);
            }
        }

        /// <summary>
        /// Post-execution hook;
        /// </summary>
        private async Task OnPostExecutionAsync(CommandExecution execution, CommandResult result)
        {
            // Log post-execution;
            _logger.Debug($"Completed command execution: {execution.Request.CommandName} (ID: {execution.Id}) - Status: {result.Status}");

            // Publish post-execution event;
            if (_eventBus != null)
            {
                await _eventBus.PublishAsync(new CommandExecutionEvent;
                {
                    ExecutionId = execution.Id,
                    CommandName = execution.Request.CommandName,
                    Status = result.Status,
                    Timestamp = DateTime.UtcNow,
                    UserId = execution.Request.UserId,
                    Result = result;
                });
            }
        }

        /// <summary>
        /// Publish execution event;
        /// </summary>
        private async Task PublishExecutionEventAsync(CommandExecution execution, CommandResult result)
        {
            if (_eventBus == null) return;

            try
            {
                var @event = new CommandExecutedEvent;
                {
                    ExecutionId = execution.Id,
                    CommandName = execution.Request.CommandName,
                    Status = result.Status,
                    StartTime = execution.StartTime,
                    EndTime = execution.EndTime ?? DateTime.UtcNow,
                    Duration = (execution.EndTime ?? DateTime.UtcNow) - execution.StartTime,
                    UserId = execution.Request.UserId,
                    Success = result.Status == CommandExecutionStatus.Completed,
                    ResultData = result.Data,
                    ErrorMessage = result.ErrorMessage;
                };

                await _eventBus.PublishAsync(@event);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to publish execution event: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Learn from execution using AI;
        /// </summary>
        private async Task LearnFromExecutionAsync(CommandExecution execution, CommandResult result)
        {
            try
            {
                if (_neuralNetwork == null) return;

                var learningData = new ExecutionLearningData;
                {
                    CommandName = execution.Request.CommandName,
                    Parameters = execution.Request.Parameters,
                    ExecutionTime = (execution.EndTime ?? DateTime.UtcNow) - execution.StartTime,
                    Success = result.Status == CommandExecutionStatus.Completed,
                    ResourceUsage = execution.ResourceUsage,
                    Context = execution.Context.Environment;
                };

                await _neuralNetwork.LearnFromExecutionAsync(learningData);

                // Update decision engine if available;
                if (_decisionEngine != null)
                {
                    await _decisionEngine.RecordExecutionOutcomeAsync(execution, result);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"AI learning error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Execute with concurrency control;
        /// </summary>
        private async Task<CommandResult> ExecuteWithConcurrencyControlAsync(
            CommandRequest request, SemaphoreSlim semaphore, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                return await ExecuteAsync(request, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Optimize execution order using AI;
        /// </summary>
        private async Task<List<CommandRequest>> OptimizeExecutionOrderAsync(List<CommandRequest> commands)
        {
            if (_neuralNetwork == null || commands.Count <= 1) return commands;

            try
            {
                var optimization = await _neuralNetwork.OptimizeExecutionOrderAsync(commands);
                return optimization.OptimizedCommands ?? commands;
            }
            catch (Exception ex)
            {
                _logger.Error($"Execution order optimization failed: {ex.Message}", ex);
                return commands;
            }
        }

        /// <summary>
        /// Analyze batch results;
        /// </summary>
        private async Task AnalyzeBatchResultsAsync(BatchExecutionResult batchResult)
        {
            // Update batch performance metrics;
            foreach (var result in batchResult.Results)
            {
                // Implementation for batch analysis;
            }

            // AI analysis if available;
            if (_neuralNetwork != null)
            {
                await _neuralNetwork.AnalyzeBatchResultsAsync(batchResult);
            }
        }

        /// <summary>
        /// Initialize performance metrics;
        /// </summary>
        private void InitializePerformanceMetrics()
        {
            // Initialize metrics for all registered commands;
            var commands = _commandRegistry.GetAllCommands();
            foreach (var command in commands)
            {
                _performanceMetrics[command] = new CommandPerformance;
                {
                    CommandName = command,
                    LastExecution = DateTime.MinValue;
                };
            }
        }

        /// <summary>
        /// Update performance metrics;
        /// </summary>
        private void UpdatePerformanceMetrics(string commandName, CommandExecution execution)
        {
            if (!_performanceMetrics.TryGetValue(commandName, out var performance))
            {
                performance = new CommandPerformance { CommandName = commandName };
                _performanceMetrics[commandName] = performance;
            }

            var duration = (execution.EndTime ?? DateTime.UtcNow) - execution.StartTime;

            performance.TotalExecutions++;
            performance.SuccessfulExecutions += execution.Status == CommandExecutionStatus.Completed ? 1 : 0;
            performance.TotalExecutionTime += duration;
            performance.AverageExecutionTime = performance.TotalExecutionTime / performance.TotalExecutions;
            performance.LastExecution = execution.StartTime;
            performance.LastStatus = execution.Status;

            // Update min/max execution times;
            if (duration < performance.MinExecutionTime || performance.MinExecutionTime == TimeSpan.Zero)
                performance.MinExecutionTime = duration;
            if (duration > performance.MaxExecutionTime)
                performance.MaxExecutionTime = duration;
        }

        /// <summary>
        /// Update statistics;
        /// </summary>
        private void UpdateStatistics(CommandExecutionStatus status)
        {
            lock (_syncLock)
            {
                Statistics.TotalExecutions++;

                switch (status)
                {
                    case CommandExecutionStatus.Completed:
                        Statistics.SuccessfulExecutions++;
                        break;
                    case CommandExecutionStatus.Failed:
                        Statistics.FailedExecutions++;
                        break;
                    case CommandExecutionStatus.Timeout:
                        Statistics.TimeoutExecutions++;
                        break;
                    case CommandExecutionStatus.PermissionDenied:
                        Statistics.PermissionDeniedExecutions++;
                        break;
                }

                Statistics.LastUpdate = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Update performance statistics;
        /// </summary>
        private void UpdatePerformanceStatistics()
        {
            // Implementation for updating aggregate statistics;
            Statistics.AverageExecutionTime = CalculateAverageExecutionTime();
            Statistics.SuccessRate = Statistics.TotalExecutions > 0;
                ? (double)Statistics.SuccessfulExecutions / Statistics.TotalExecutions * 100;
                : 0;
        }

        /// <summary>
        /// Check system load;
        /// </summary>
        private void CheckSystemLoad()
        {
            var load = (double)_currentExecutions / MaxConcurrentExecutions * 100;

            if (load > 90)
            {
                _logger.Warning($"High system load detected: {load:F1}% ({_currentExecutions}/{MaxConcurrentExecutions} executions)");

                // Implement load shedding if needed;
                if (load > 95)
                {
                    ShedLoad();
                }
            }
        }

        /// <summary>
        /// Shed load when system is overloaded;
        /// </summary>
        private void ShedLoad()
        {
            // Cancel low priority executions;
            var lowPriorityExecutions = _activeExecutions.Values;
                .Where(e => e.Request.Priority == CommandPriority.Low)
                .Take(2) // Cancel up to 2 low priority executions;
                .ToList();

            foreach (var execution in lowPriorityExecutions)
            {
                _ = CancelCommandAsync(execution.Id, "Load shedding");
            }

            _logger.Warning($"Load shedding activated: cancelled {lowPriorityExecutions.Count} low priority executions");
        }

        /// <summary>
        /// Sanitize parameters;
        /// </summary>
        private Dictionary<string, object> SanitizeParameters(Dictionary<string, object> parameters)
        {
            if (parameters == null) return new Dictionary<string, object>();

            var sanitized = new Dictionary<string, object>();

            foreach (var kvp in parameters)
            {
                // Basic sanitization - in production, implement proper sanitization based on parameter types;
                var value = kvp.Value;

                if (value is string stringValue)
                {
                    // Trim strings;
                    value = stringValue.Trim();
                }

                sanitized[kvp.Key] = value;
            }

            return sanitized;
        }

        /// <summary>
        /// Check resource requirements;
        /// </summary>
        private async Task<ResourceCheckResult> CheckResourceRequirementsAsync(CommandRequest request)
        {
            // Implementation for resource checking;
            await Task.CompletedTask;
            return new ResourceCheckResult { Available = true };
        }

        /// <summary>
        /// Create error result;
        /// </summary>
        private CommandResult CreateErrorResult(string errorMessage, CommandExecutionStatus status, Exception exception = null)
        {
            return new CommandResult;
            {
                Status = status,
                Success = false,
                ErrorMessage = errorMessage,
                Exception = exception,
                ExecutionTime = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Calculate average execution time;
        /// </summary>
        private TimeSpan CalculateAverageExecutionTime()
        {
            var validMetrics = _performanceMetrics.Values;
                .Where(p => p.TotalExecutions > 0)
                .ToList();

            if (validMetrics.Count == 0) return TimeSpan.Zero;

            var totalTime = validMetrics.Sum(p => p.TotalExecutionTime.TotalMilliseconds);
            var totalExecutions = validMetrics.Sum(p => p.TotalExecutions);

            return TimeSpan.FromMilliseconds(totalTime / totalExecutions);
        }

        /// <summary>
        /// Get available memory;
        /// </summary>
        private long GetAvailableMemory()
        {
            // Implementation for getting available memory;
            return 0; // Placeholder;
        }

        /// <summary>
        /// Predict next commands;
        /// </summary>
        private async Task<List<CommandPrediction>> PredictNextCommandsAsync()
        {
            if (_neuralNetwork == null) return new List<CommandPrediction>();

            try
            {
                return await _neuralNetwork.PredictNextCommandsAsync();
            }
            catch (Exception ex)
            {
                _logger.Error($"Command prediction error: {ex.Message}", ex);
                return new List<CommandPrediction>();
            }
        }

        /// <summary>
        /// Preload for predicted command;
        /// </summary>
        private async Task PreloadForPredictedCommandAsync(CommandPrediction prediction)
        {
            // Implementation for preloading resources;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Analyze dependencies;
        /// </summary>
        private async Task<DependencyAnalysis> AnalyzeDependenciesAsync(List<CommandRequest> commands)
        {
            // Implementation for dependency analysis;
            await Task.CompletedTask;
            return new DependencyAnalysis();
        }

        /// <summary>
        /// Predict execution times;
        /// </summary>
        private async Task<Dictionary<string, TimeSpan>> PredictExecutionTimesAsync(List<CommandRequest> commands)
        {
            var predictions = new Dictionary<string, TimeSpan>();

            foreach (var command in commands)
            {
                if (_performanceMetrics.TryGetValue(command.CommandName, out var performance))
                {
                    predictions[command.CommandName] = performance.AverageExecutionTime;
                }
                else;
                {
                    predictions[command.CommandName] = TimeSpan.FromSeconds(1); // Default prediction;
                }
            }

            await Task.CompletedTask;
            return predictions;
        }

        /// <summary>
        /// Optimize execution order internally;
        /// </summary>
        private async Task<List<CommandRequest>> OptimizeExecutionOrderInternalAsync(
            List<CommandRequest> commands, DependencyAnalysis dependencies, Dictionary<string, TimeSpan> predictedTimes)
        {
            // Simple optimization: sort by predicted execution time (shortest first)
            var optimized = commands;
                .OrderBy(c => predictedTimes.GetValueOrDefault(c.CommandName, TimeSpan.MaxValue))
                .ToList();

            await Task.CompletedTask;
            return optimized;
        }

        /// <summary>
        /// Calculate optimization improvements;
        /// </summary>
        private async Task<OptimizationImprovements> CalculateOptimizationImprovementsAsync(
            List<CommandRequest> original, List<CommandRequest> optimized)
        {
            // Implementation for improvement calculation;
            await Task.CompletedTask;
            return new OptimizationImprovements();
        }

        /// <summary>
        /// Get optimization strategy;
        /// </summary>
        private string GetOptimizationStrategy(List<CommandRequest> commands)
        {
            if (commands.Count > 10) return "BatchParallelOptimization";
            if (commands.Any(c => c.Priority >= CommandPriority.High)) return "PriorityBased";
            return "SequentialOptimization";
        }

        /// <summary>
        /// Analyze execution pattern;
        /// </summary>
        private async Task AnalyzeExecutionPatternAsync(CommandExecution execution)
        {
            // Implementation for pattern analysis;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cts.Cancel();

                    try
                    {
                        _executionTask?.Wait(TimeSpan.FromSeconds(5));
                        _monitoringTask?.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (AggregateException)
                    {
                        // Expected on cancellation;
                    }

                    _executionTask?.Dispose();
                    _monitoringTask?.Dispose();
                    _cts.Dispose();
                    _queueSemaphore.Dispose();

                    // Cancel all active executions;
                    foreach (var execution in _activeExecutions.Values)
                    {
                        execution.CancellationTokenSource?.Cancel();
                    }
                }

                _disposed = true;
            }
        }
    }

    // Supporting classes and interfaces;

    public interface ICommandExecutor : IDisposable
    {
        int MaxConcurrentExecutions { get; set; }
        TimeSpan DefaultTimeout { get; set; }
        bool EnableAIOptimization { get; set; }
        bool EnablePredictiveExecution { get; set; }
        ExecutionStatistics Statistics { get; }

        Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default);
        Task<CommandResult> ExecuteWithPriorityAsync(CommandRequest request, CancellationToken cancellationToken = default);
        Task<BatchExecutionResult> ExecuteBatchAsync(IEnumerable<CommandRequest> requests, BatchExecutionOptions options = null);
        Task<Guid> QueueCommandAsync(CommandRequest request);
        Task<bool> CancelCommandAsync(Guid executionId, string reason = null);
        CommandExecutionStatus GetExecutionStatus(Guid executionId);
        CommandPerformance GetCommandPerformance(string commandName);
        IReadOnlyList<CommandExecution> GetActiveExecutions();
        Task ClearQueueAsync(QueueClearOptions options = null);
        Task<ExecutionOptimization> OptimizeExecutionAsync(IEnumerable<CommandRequest> commands);
        Task<CommandValidationResult> ValidateCommandAsync(CommandRequest request);
    }

    public class CommandRequest;
    {
        public string CommandName { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public CommandPriority Priority { get; set; } = CommandPriority.Normal;
        public CommandSource Source { get; set; } = CommandSource.API;
        public TimeSpan? Timeout { get; set; }
        public Guid? QueueId { get; set; }
        public string IPAddress { get; set; }
        public string UserAgent { get; set; }
        public AuthenticationLevel AuthenticationLevel { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public class CommandResult;
    {
        public CommandExecutionStatus Status { get; set; }
        public bool Success { get; set; }
        public object Data { get; set; }
        public string Message { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public DateTime ExecutionTime { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class CommandExecution;
    {
        public Guid Id { get; set; }
        public CommandRequest Request { get; set; }
        public ExecutionContext Context { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public CommandExecutionStatus Status { get; set; }
        public CommandResult Result { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public string CancellationReason { get; set; }
        public ResourceUsage ResourceUsage { get; set; } = new ResourceUsage();
    }

    public class ExecutionContext;
    {
        public Guid ExecutionId { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public string CommandName { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public DateTime StartTime { get; set; }
        public SecurityContext SecurityContext { get; set; }
        public ExecutionEnvironment Environment { get; set; }
        public Dictionary<string, object> CustomData { get; set; } = new Dictionary<string, object>();
    }

    public class SecurityContext;
    {
        public string UserId { get; set; }
        public string IPAddress { get; set; }
        public string UserAgent { get; set; }
        public AuthenticationLevel AuthenticationLevel { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
        public List<string> Permissions { get; set; } = new List<string>();
    }

    public class ExecutionEnvironment;
    {
        public string MachineName { get; set; }
        public int ProcessorCount { get; set; }
        public long AvailableMemory { get; set; }
        public string CurrentDirectory { get; set; }
        public string OSVersion { get; set; }
        public bool Is64BitProcess { get; set; }
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
    }

    public class ResourceUsage;
    {
        public long MemoryUsed { get; set; }
        public TimeSpan CpuTime { get; set; }
        public int ThreadsUsed { get; set; }
        public List<string> FilesAccessed { get; set; } = new List<string>();
        public List<string> NetworkConnections { get; set; } = new List<string>();
    }

    public class CommandPerformance;
    {
        public string CommandName { get; set; }
        public long TotalExecutions { get; set; }
        public long SuccessfulExecutions { get; set; }
        public TimeSpan TotalExecutionTime { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public TimeSpan MinExecutionTime { get; set; }
        public TimeSpan MaxExecutionTime { get; set; }
        public DateTime LastExecution { get; set; }
        public CommandExecutionStatus LastStatus { get; set; }
        public Dictionary<string, int> ErrorCounts { get; set; } = new Dictionary<string, int>();

        public CommandPerformance Clone()
        {
            return new CommandPerformance;
            {
                CommandName = CommandName,
                TotalExecutions = TotalExecutions,
                SuccessfulExecutions = SuccessfulExecutions,
                TotalExecutionTime = TotalExecutionTime,
                AverageExecutionTime = AverageExecutionTime,
                MinExecutionTime = MinExecutionTime,
                MaxExecutionTime = MaxExecutionTime,
                LastExecution = LastExecution,
                LastStatus = LastStatus,
                ErrorCounts = new Dictionary<string, int>(ErrorCounts)
            };
        }
    }

    public class ExecutionStatistics;
    {
        public long TotalExecutions { get; set; }
        public long SuccessfulExecutions { get; set; }
        public long FailedExecutions { get; set; }
        public long TimeoutExecutions { get; set; }
        public long PermissionDeniedExecutions { get; set; }
        public double SuccessRate { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public DateTime LastUpdate { get; set; }
        public int CurrentConcurrentExecutions { get; set; }
        public int QueueSize { get; set; }
    }

    public class BatchExecutionResult;
    {
        public Guid BatchId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public List<CommandResult> Results { get; set; } = new List<CommandResult>();
        public BatchExecutionOptions Options { get; set; }
        public Dictionary<string, object> BatchMetadata { get; set; } = new Dictionary<string, object>();
    }

    public class BatchExecutionOptions;
    {
        public bool ExecuteInParallel { get; set; } = true;
        public int? MaxConcurrent { get; set; }
        public bool OptimizeExecutionOrder { get; set; } = true;
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
        public bool ContinueOnError { get; set; } = false;
        public Dictionary<string, string> BatchParameters { get; set; } = new Dictionary<string, string>();
    }

    public class QueueClearOptions;
    {
        public bool ClearPriorityQueue { get; set; } = true;
        public bool ClearNormalQueue { get; set; } = true;
        public bool LogAudit { get; set; } = true;
        public string Reason { get; set; }
    }

    public class ExecutionOptimization;
    {
        public bool IsOptimized { get; set; }
        public List<CommandRequest> OriginalCommands { get; set; }
        public List<CommandRequest> OptimizedCommands { get; set; }
        public OptimizationImprovements ExpectedImprovement { get; set; }
        public DateTime OptimizationDate { get; set; }
        public string OptimizationStrategy { get; set; }
        public string Error { get; set; }
    }

    public class OptimizationImprovements;
    {
        public double ExpectedTimeReductionPercent { get; set; }
        public TimeSpan ExpectedTimeSaved { get; set; }
        public double ExpectedResourceReductionPercent { get; set; }
        public List<string> OptimizationNotes { get; set; } = new List<string>();
    }

    public class CommandValidationResult;
    {
        public string CommandName { get; set; }
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public DateTime ValidationTime { get; set; }
        public Dictionary<string, object> ValidationDetails { get; set; } = new Dictionary<string, object>();
    }

    public class CommandPrediction;
    {
        public string CommandName { get; set; }
        public double Confidence { get; set; }
        public DateTime PredictedTime { get; set; }
        public Dictionary<string, object> PredictedParameters { get; set; } = new Dictionary<string, object>();
    }

    public class DependencyAnalysis;
    {
        public Dictionary<string, List<string>> Dependencies { get; set; } = new Dictionary<string, List<string>>();
        public List<DependencyConflict> Conflicts { get; set; } = new List<DependencyConflict>();
    }

    public class DependencyConflict;
    {
        public string Command1 { get; set; }
        public string Command2 { get; set; }
        public string ConflictType { get; set; }
        public string Resolution { get; set; }
    }

    public class ExecutionLearningData;
    {
        public string CommandName { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public bool Success { get; set; }
        public ResourceUsage ResourceUsage { get; set; }
        public ExecutionEnvironment Context { get; set; }
    }

    // Enums;
    public enum CommandExecutionStatus;
    {
        Pending,
        Starting,
        Executing,
        Completed,
        Failed,
        Timeout,
        Cancelled,
        PermissionDenied,
        ValidationFailed,
        CommandNotFound,
        SystemError,
        NotFound;
    }

    public enum CommandPriority;
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3;
    }

    public enum CommandSource;
    {
        API,
        CLI,
        UI,
        Conversation,
        Schedule,
        Event,
        System;
    }

    public enum AuthenticationLevel;
    {
        Anonymous,
        Basic,
        Authenticated,
        Elevated,
        Admin,
        System;
    }

    // Supporting interfaces from other modules;
    public interface ICommandRegistry
    {
        ICommandHandler GetCommandHandler(string commandName);
        IEnumerable<string> GetAllCommands();
    }

    public interface ICommandHandler;
    {
        Task<CommandResult> ExecuteAsync(CommandRequest request, ExecutionContext context, CancellationToken cancellationToken);
        Task<ParameterValidationResult> ValidateParametersAsync(Dictionary<string, object> parameters);
    }

    public class ParameterValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    // Internal helper classes;
    internal class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }

    internal class PermissionResult;
    {
        public string CommandName { get; set; }
        public string UserId { get; set; }
        public bool IsAllowed { get; set; }
        public string Reason { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
        public DateTime Timestamp { get; set; }
    }

    internal class ResourceCheckResult;
    {
        public bool Available { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    // Event classes;
    public class CommandExecutionEvent : IEvent;
    {
        public Guid ExecutionId { get; set; }
        public string CommandName { get; set; }
        public CommandExecutionStatus Status { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserId { get; set; }
        public CommandResult Result { get; set; }
    }

    public class CommandExecutedEvent : IEvent;
    {
        public Guid ExecutionId { get; set; }
        public string CommandName { get; set; }
        public CommandExecutionStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public string UserId { get; set; }
        public bool Success { get; set; }
        public object ResultData { get; set; }
        public string ErrorMessage { get; set; }
    }

    public interface IEvent { }
}
