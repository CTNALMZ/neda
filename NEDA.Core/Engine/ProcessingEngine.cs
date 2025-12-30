using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.Core.Common;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.Engine
{
    /// <summary>
    /// İşlem durumlarını temsil eden enum;
    /// </summary>
    public enum ProcessStatus
    {
        Pending,
        Running,
        Paused,
        Completed,
        Failed,
        Cancelled,
        Timeout
    }

    /// <summary>
    /// İşlem öncelik seviyeleri;
    /// </summary>
    public enum ProcessPriority
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3,
        Background = 4
    }

    /// <summary>
    /// İşlem talebi için DTO;
    /// </summary>
    public class ProcessingRequest
    {
        public string ProcessId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ProcessPriority Priority { get; set; } = ProcessPriority.Normal;
        public TimeSpan? Timeout { get; set; }
        public int MaxRetryAttempts { get; set; } = 3;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public Func<ProcessingContext, CancellationToken, Task<ProcessingResult>> ExecuteFunction { get; set; }
        public Action<ProcessingContext, ProcessingResult> OnCompleted { get; set; }
        public Action<ProcessingContext, Exception> OnError { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string RequestedBy { get; set; } = "system";
    }

    /// <summary>
    /// İşlem bağlamı;
    /// </summary>
    public class ProcessingContext
    {
        public string ProcessId { get; set; }
        public ProcessingRequest Request { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ProcessStatus Status { get; set; }
        public Exception LastError { get; set; }
        public int RetryCount { get; set; }
        public Dictionary<string, object> State { get; set; } = new Dictionary<string, object>();
        public CancellationToken CancellationToken { get; set; }
        public ILogger Logger { get; set; }
        public IServiceProvider ServiceProvider { get; set; }
    }

    /// <summary>
    /// İşlem sonucu;
    /// </summary>
    public class ProcessingResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();

        public static ProcessingResult Success(object data = null, string message = "Operation completed successfully")
        {
            return new ProcessingResult
            {
                IsSuccess = true,
                Message = message,
                Data = data,
                Metadata = new Dictionary<string, object>()
            };
        }

        public static ProcessingResult Failure(string error, Exception exception = null)
        {
            var result = new ProcessingResult
            {
                IsSuccess = false,
                Message = error,
                Data = null
            };

            if (exception != null)
            {
                result.Metadata["Exception"] = exception;
                result.Errors.Add(exception.Message);
            }

            return result;
        }
    }

    /// <summary>
    /// İşlem istatistikleri;
    /// </summary>
    public class ProcessingStatistics
    {
        public int TotalProcessed { get; set; }
        public int Successful { get; set; }
        public int Failed { get; set; }
        public int Pending { get; set; }
        public int Running { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public DateTime LastProcessed { get; set; }
        public Dictionary<ProcessPriority, int> PriorityStats { get; set; } = new Dictionary<ProcessPriority, int>();
        public Dictionary<string, int> ProcessTypeStats { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// İşlem motoru ayarları;
    /// </summary>
    public class ProcessingEngineSettings
    {
        public int MaxConcurrentProcesses { get; set; } = 10;
        public int MaxQueueSize { get; set; } = 1000;
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public bool EnableHealthMonitoring { get; set; } = true;
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
        public bool EnablePerformanceCounters { get; set; } = true;
        public bool EnableAutoScaling { get; set; } = false;
        public int MinWorkerThreads { get; set; } = 2;
        public int MaxWorkerThreads { get; set; } = 20;
        public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// İşlem motoru arayüzü;
    /// </summary>
    public interface IProcessingEngine : IDisposable
    {
        Task<ProcessingResult> ExecuteAsync(ProcessingRequest request, CancellationToken cancellationToken = default);
        Task<ProcessingResult> ExecuteBatchAsync(IEnumerable<ProcessingRequest> requests, CancellationToken cancellationToken = default);
        Task<bool> CancelProcessAsync(string processId, CancellationToken cancellationToken = default);
        Task<PauseResult> PauseProcessAsync(string processId, CancellationToken cancellationToken = default);
        Task<bool> ResumeProcessAsync(string processId, CancellationToken cancellationToken = default);
        Task<ProcessingStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<ProcessingContext>> GetActiveProcessesAsync(CancellationToken cancellationToken = default);
        Task<ProcessingContext> GetProcessContextAsync(string processId, CancellationToken cancellationToken = default);
        Task<bool> InitializeAsync(CancellationToken cancellationToken = default);
        Task<bool> ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
        event EventHandler<ProcessCompletedEventArgs> ProcessCompleted;
        event EventHandler<ProcessFailedEventArgs> ProcessFailed;
        event EventHandler<ProcessStatusChangedEventArgs> ProcessStatusChanged;
    }

    public class ProcessCompletedEventArgs : EventArgs
    {
        public string ProcessId { get; set; }
        public ProcessingResult Result { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime CompletedAt { get; set; }
    }

    public class ProcessFailedEventArgs : EventArgs
    {
        public string ProcessId { get; set; }
        public Exception Exception { get; set; }
        public int RetryCount { get; set; }
        public DateTime FailedAt { get; set; }
    }

    public class ProcessStatusChangedEventArgs : EventArgs
    {
        public string ProcessId { get; set; }
        public ProcessStatus OldStatus { get; set; }
        public ProcessStatus NewStatus { get; set; }
        public DateTime ChangedAt { get; set; }
    }

    public class PauseResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public DateTime? EstimatedResumeTime { get; set; }
    }

    /// <summary>
    /// İşlem motoru implementasyonu;
    /// </summary>
    public class ProcessingEngine : IProcessingEngine
    {
        private readonly ILogger<ProcessingEngine> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly ISecurityManager _securityManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ProcessingEngineSettings _settings;

        private readonly Dictionary<string, ProcessingContext> _activeProcesses;
        private readonly Dictionary<string, ProcessingContext> _completedProcesses;
        private readonly PriorityQueue<ProcessingRequest, ProcessPriority> _processQueue;
        private readonly List<Task> _workerTasks;
        private readonly SemaphoreSlim _concurrencyLimiter;
        private readonly SemaphoreSlim _queueSemaphore;
        private readonly CancellationTokenSource _shutdownCts;
        private readonly object _syncLock = new object();

        private bool _isInitialized = false;
        private bool _isShuttingDown = false;
        private Timer _healthCheckTimer;
        private Timer _performanceTimer;

        public event EventHandler<ProcessCompletedEventArgs> ProcessCompleted;
        public event EventHandler<ProcessFailedEventArgs> ProcessFailed;
        public event EventHandler<ProcessStatusChangedEventArgs> ProcessStatusChanged;

        public ProcessingEngine(
            ILogger<ProcessingEngine> logger,
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            ISecurityManager securityManager,
            ILoggerFactory loggerFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

            _settings = LoadSettings();
            _activeProcesses = new Dictionary<string, ProcessingContext>();
            _completedProcesses = new Dictionary<string, ProcessingContext>();
            _processQueue = new PriorityQueue<ProcessingRequest, ProcessPriority>();
            _workerTasks = new List<Task>();
            _concurrencyLimiter = new SemaphoreSlim(_settings.MaxConcurrentProcesses);
            _queueSemaphore = new SemaphoreSlim(1, 1);
            _shutdownCts = new CancellationTokenSource();

            _logger.LogInformation("ProcessingEngine initialized with MaxConcurrentProcesses: {MaxConcurrent}",
                _settings.MaxConcurrentProcesses);
        }

        public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("ProcessingEngine already initialized");
                    return true;
                }

                _logger.LogInformation("Initializing ProcessingEngine...");

                StartWorkerThreads();

                if (_settings.EnableHealthMonitoring)
                {
                    _healthCheckTimer = new Timer(
                        HealthCheckCallback,
                        null,
                        _settings.HealthCheckInterval,
                        _settings.HealthCheckInterval);
                }

                if (_settings.EnablePerformanceCounters)
                {
                    _performanceTimer = new Timer(
                        PerformanceMonitoringCallback,
                        null,
                        TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(10));
                }

                _isInitialized = true;
                _logger.LogInformation("ProcessingEngine initialized successfully");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ProcessingEngine");
                throw new ProcessingEngineException(
                    ErrorCodes.EngineInitializationFailed,
                    "Failed to initialize Processing Engine",
                    ex);
            }
        }

        public async Task<ProcessingResult> ExecuteAsync(ProcessingRequest request, CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);

            try
            {
                if (!await _securityManager.HasPermissionAsync(request.RequestedBy, "ProcessingEngine.Execute", cancellationToken))
                {
                    throw new UnauthorizedAccessException(
                        $"User '{request.RequestedBy}' does not have permission to execute processes");
                }

                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);

                if (_processQueue.Count >= _settings.MaxQueueSize)
                {
                    throw new ProcessingEngineException(
                        ErrorCodes.QueueFull,
                        $"Process queue is full. Max queue size: {_settings.MaxQueueSize}");
                }

                await _queueSemaphore.WaitAsync(linkedCts.Token);
                try
                {
                    _processQueue.Enqueue(request, request.Priority);
                    _logger.LogDebug("Process {ProcessId} enqueued with priority {Priority}",
                        request.ProcessId, request.Priority);
                }
                finally
                {
                    _queueSemaphore.Release();
                }

                var completionSource = new TaskCompletionSource<ProcessingResult>();
                var context = await GetProcessContextAsync(request.ProcessId, linkedCts.Token);

                if (context == null)
                {
                    return ProcessingResult.Failure($"Process {request.ProcessId} not found");
                }

                void OnProcessCompleted(object sender, ProcessCompletedEventArgs e)
                {
                    if (e.ProcessId == request.ProcessId)
                    {
                        completionSource.TrySetResult(e.Result);
                    }
                }

                void OnProcessFailed(object sender, ProcessFailedEventArgs e)
                {
                    if (e.ProcessId == request.ProcessId)
                    {
                        completionSource.TrySetResult(ProcessingResult.Failure(
                            $"Process failed: {e.Exception?.Message}", e.Exception));
                    }
                }

                ProcessCompleted += OnProcessCompleted;
                ProcessFailed += OnProcessFailed;

                try
                {
                    return await completionSource.Task.WaitAsync(
                        request.Timeout ?? _settings.DefaultTimeout,
                        linkedCts.Token);
                }
                catch (TimeoutException)
                {
                    await CancelProcessAsync(request.ProcessId, linkedCts.Token);
                    return ProcessingResult.Failure($"Process timeout after {request.Timeout ?? _settings.DefaultTimeout}");
                }
                finally
                {
                    ProcessCompleted -= OnProcessCompleted;
                    ProcessFailed -= OnProcessFailed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute process {ProcessId}", request.ProcessId);
                return ProcessingResult.Failure($"Execution failed: {ex.Message}", ex);
            }
        }

        public async Task<ProcessingResult> ExecuteBatchAsync(
            IEnumerable<ProcessingRequest> requests,
            CancellationToken cancellationToken = default)
        {
            var batchId = Guid.NewGuid().ToString();
            _logger.LogInformation("Starting batch execution {BatchId} with {Count} processes",
                batchId, requests.Count());

            var results = new List<ProcessingResult>();
            var tasks = new List<Task<ProcessingResult>>();

            foreach (var request in requests)
            {
                tasks.Add(ExecuteAsync(request, cancellationToken));

                if (tasks.Count >= _settings.MaxConcurrentProcesses)
                {
                    var completedTask = await Task.WhenAny(tasks);
                    tasks.Remove(completedTask);
                    results.Add(await completedTask);
                }
            }

            var remainingResults = await Task.WhenAll(tasks);
            results.AddRange(remainingResults);

            var successCount = results.Count(r => r.IsSuccess);
            var failureCount = results.Count(r => !r.IsSuccess);

            _logger.LogInformation("Batch {BatchId} completed: {Success} successful, {Failed} failed",
                batchId, successCount, failureCount);

            return ProcessingResult.Success(results,
                $"Batch completed: {successCount} successful, {failureCount} failed");
        }

        public async Task<bool> CancelProcessAsync(string processId, CancellationToken cancellationToken = default)
        {
            try
            {
                var context = await GetProcessContextAsync(processId, cancellationToken);
                if (context == null)
                {
                    _logger.LogWarning("Process {ProcessId} not found for cancellation", processId);
                    return false;
                }

                if (context.Status == ProcessStatus.Completed ||
                    context.Status == ProcessStatus.Failed ||
                    context.Status == ProcessStatus.Cancelled)
                {
                    _logger.LogWarning("Process {ProcessId} is already in terminal state: {Status}",
                        processId, context.Status);
                    return false;
                }

                await ChangeProcessStatusAsync(processId, ProcessStatus.Cancelled, cancellationToken);

                if (context.State.TryGetValue("CancellationTokenSource", out var ctsObj) &&
                    ctsObj is CancellationTokenSource cts)
                {
                    cts.Cancel();
                }

                _logger.LogInformation("Process {ProcessId} cancelled successfully", processId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel process {ProcessId}", processId);
                return false;
            }
        }

        public async Task<PauseResult> PauseProcessAsync(string processId, CancellationToken cancellationToken = default)
        {
            try
            {
                var context = await GetProcessContextAsync(processId, cancellationToken);
                if (context == null)
                {
                    return new PauseResult
                    {
                        IsSuccess = false,
                        Message = $"Process {processId} not found"
                    };
                }

                if (context.Status != ProcessStatus.Running)
                {
                    return new PauseResult
                    {
                        IsSuccess = false,
                        Message = $"Cannot pause process in {context.Status} state"
                    };
                }

                await ChangeProcessStatusAsync(processId, ProcessStatus.Paused, cancellationToken);

                _logger.LogInformation("Process {ProcessId} paused", processId);

                return new PauseResult
                {
                    IsSuccess = true,
                    Message = "Process paused successfully",
                    EstimatedResumeTime = DateTime.UtcNow.AddMinutes(5)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pause process {ProcessId}", processId);
                return new PauseResult
                {
                    IsSuccess = false,
                    Message = $"Failed to pause process: {ex.Message}"
                };
            }
        }

        public async Task<bool> ResumeProcessAsync(string processId, CancellationToken cancellationToken = default)
        {
            try
            {
                var context = await GetProcessContextAsync(processId, cancellationToken);
                if (context == null || context.Status != ProcessStatus.Paused)
                {
                    _logger.LogWarning("Process {ProcessId} not found or not paused", processId);
                    return false;
                }

                await ChangeProcessStatusAsync(processId, ProcessStatus.Running, cancellationToken);

                _logger.LogInformation("Process {ProcessId} resumed", processId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume process {ProcessId}", processId);
                return false;
            }
        }

        /// <summary>
        /// İstatistikleri getirir;
        /// </summary>
        public async Task<ProcessingStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        {
            var stats = new ProcessingStatistics
            {
                TotalProcessed = _completedProcesses.Count,
                Successful = _completedProcesses.Values.Count(p => p.Status == ProcessStatus.Completed),
                Failed = _completedProcesses.Values.Count(p => p.Status == ProcessStatus.Failed),
                Pending = _processQueue.Count,
                Running = _activeProcesses.Values.Count(p => p.Status == ProcessStatus.Running),
                LastProcessed = _completedProcesses.Values
                    .OrderByDescending(p => p.EndTime)
                    .FirstOrDefault()?.EndTime ?? DateTime.MinValue
            };

            foreach (ProcessPriority priority in Enum.GetValues(typeof(ProcessPriority)))
            {
                stats.PriorityStats[priority] = _completedProcesses.Values
                    .Count(p => p.Request?.Priority == priority);
            }

            stats.ProcessTypeStats = _completedProcesses.Values
                .GroupBy(p => p.Request?.Name)
                .ToDictionary(g => g.Key ?? "Unknown", g => g.Count());

            var completedTimes = _completedProcesses.Values
                .Where(p => p.StartTime != default && p.EndTime.HasValue)
                .Select(p => p.EndTime.Value - p.StartTime)
                .ToList();

            if (completedTimes.Any())
            {
                stats.AverageExecutionTime = TimeSpan.FromMilliseconds(
                    completedTimes.Average(t => t.TotalMilliseconds));
            }

            return stats;
        }

        public Task<IEnumerable<ProcessingContext>> GetActiveProcessesAsync(CancellationToken cancellationToken = default)
        {
            lock (_syncLock)
            {
                return Task.FromResult(_activeProcesses.Values.AsEnumerable());
            }
        }

        public Task<ProcessingContext> GetProcessContextAsync(string processId, CancellationToken cancellationToken = default)
        {
            lock (_syncLock)
            {
                if (_activeProcesses.TryGetValue(processId, out var context))
                {
                    return Task.FromResult(context);
                }

                if (_completedProcesses.TryGetValue(processId, out var completedContext))
                {
                    return Task.FromResult(completedContext);
                }

                return Task.FromResult<ProcessingContext>(null);
            }
        }

        public async Task<bool> ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (_isShuttingDown)
            {
                _logger.LogWarning("Shutdown already in progress");
                return false;
            }

            _isShuttingDown = true;
            _logger.LogInformation("Starting ProcessingEngine shutdown...");

            try
            {
                _healthCheckTimer?.Dispose();
                _performanceTimer?.Dispose();

                _shutdownCts.Cancel();

                var shutdownTimeout = timeout ?? _settings.ShutdownTimeout;
                var shutdownTask = Task.WhenAll(_workerTasks);

                if (await Task.WhenAny(shutdownTask, Task.Delay(shutdownTimeout, cancellationToken)) != shutdownTask)
                {
                    _logger.LogWarning("Shutdown timeout after {Timeout}. Forcing shutdown...", shutdownTimeout);

                    foreach (var context in _activeProcesses.Values.ToList())
                    {
                        await CancelProcessAsync(context.ProcessId, cancellationToken);
                    }
                }

                _workerTasks.Clear();

                _logger.LogInformation("ProcessingEngine shutdown completed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during shutdown");
                return false;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _healthCheckTimer?.Dispose();
                _performanceTimer?.Dispose();
                _concurrencyLimiter?.Dispose();
                _queueSemaphore?.Dispose();
                _shutdownCts?.Dispose();

                foreach (var context in _activeProcesses.Values)
                {
                    if (context.State.TryGetValue("CancellationTokenSource", out var ctsObj) &&
                        ctsObj is CancellationTokenSource cts)
                    {
                        cts.Dispose();
                    }
                }

                _logger.LogInformation("ProcessingEngine disposed");
            }
        }

        #region Private Methods

        private ProcessingEngineSettings LoadSettings()
        {
            var settings = new ProcessingEngineSettings();

            _configuration.GetSection("ProcessingEngine").Bind(settings);

            ThreadPool.SetMinThreads(settings.MinWorkerThreads, settings.MinWorkerThreads);
            ThreadPool.SetMaxThreads(settings.MaxWorkerThreads, settings.MaxWorkerThreads);

            return settings;
        }

        private void StartWorkerThreads()
        {
            for (int i = 0; i < _settings.MaxConcurrentProcesses; i++)
            {
                var workerTask = Task.Run(() => ProcessQueueWorkerAsync(_shutdownCts.Token), _shutdownCts.Token);
                _workerTasks.Add(workerTask);

                _logger.LogDebug("Worker thread {ThreadId} started", i + 1);
            }
        }

        private async Task ProcessQueueWorkerAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _concurrencyLimiter.WaitAsync(cancellationToken);

                    ProcessingRequest request = null;

                    await _queueSemaphore.WaitAsync(cancellationToken);
                    try
                    {
                        if (_processQueue.TryDequeue(out request, out var priority))
                        {
                            _logger.LogDebug("Dequeued process {ProcessId} with priority {Priority}",
                                request.ProcessId, priority);
                        }
                    }
                    finally
                    {
                        _queueSemaphore.Release();
                    }

                    if (request != null)
                    {
                        await ExecuteProcessAsync(request, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in queue worker");
                    await Task.Delay(1000, cancellationToken);
                }
                finally
                {
                    _concurrencyLimiter.Release();
                }
            }

            _logger.LogDebug("Worker thread exiting");
        }

        private async Task ExecuteProcessAsync(ProcessingRequest request, CancellationToken cancellationToken)
        {
            ProcessingContext context = null;
            var processCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                context = new ProcessingContext
                {
                    ProcessId = request.ProcessId,
                    Request = request,
                    StartTime = DateTime.UtcNow,
                    Status = ProcessStatus.Running,
                    CancellationToken = processCts.Token,
                    Logger = _loggerFactory.CreateLogger($"Process-{request.ProcessId}"),
                    ServiceProvider = _serviceProvider
                };

                context.State["CancellationTokenSource"] = processCts;

                lock (_syncLock)
                {
                    _activeProcesses[request.ProcessId] = context;
                }

                await ChangeProcessStatusAsync(request.ProcessId, ProcessStatus.Running, cancellationToken);

                _logger.LogInformation("Starting process {ProcessId}: {Name}",
                    request.ProcessId, request.Name);

                if (request.Timeout.HasValue)
                {
                    processCts.CancelAfter(request.Timeout.Value);
                }

                ProcessingResult result = null;
                Exception lastException = null;

                for (int retry = 0; retry <= request.MaxRetryAttempts; retry++)
                {
                    try
                    {
                        context.RetryCount = retry;  // ← noktalı virgül düzeltildi

                        if (retry > 0)
                        {
                            _logger.LogWarning("Retry {RetryCount}/{MaxRetries} for process {ProcessId}",
                                retry, request.MaxRetryAttempts, request.ProcessId);
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), processCts.Token);
                        }

                        result = await request.ExecuteFunction(context, processCts.Token);
                        break;
                    }
                    catch (OperationCanceledException) when (processCts.Token.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Process timeout after {request.Timeout ?? _settings.DefaultTimeout}");
                    }
                    catch (Exception ex) when (retry < request.MaxRetryAttempts)
                    {
                        lastException = ex;
                        _logger.LogWarning(ex, "Retryable error in process {ProcessId}, attempt {RetryCount}",
                            request.ProcessId, retry + 1);
                    }
                }

                if (result == null)
                {
                    result = ProcessingResult.Failure(
                        $"Process failed after {request.MaxRetryAttempts} retries",
                        lastException);
                }

                context.EndTime = DateTime.UtcNow;
                context.Status = result.IsSuccess ? ProcessStatus.Completed : ProcessStatus.Failed;

                if (result.IsSuccess)
                {
                    request.OnCompleted?.Invoke(context, result);
                    OnProcessCompleted(new ProcessCompletedEventArgs
                    {
                        ProcessId = request.ProcessId,
                        Result = result,
                        ExecutionTime = context.EndTime.Value - context.StartTime,
                        CompletedAt = context.EndTime.Value
                    });
                }
                else
                {
                    context.LastError = lastException;
                    request.OnError?.Invoke(context, lastException);
                    OnProcessFailed(new ProcessFailedEventArgs
                    {
                        ProcessId = request.ProcessId,
                        Exception = lastException,
                        RetryCount = context.RetryCount,
                        FailedAt = context.EndTime.Value
                    });
                }

                _logger.LogInformation("Process {ProcessId} completed with status {Status}",
                    request.ProcessId, context.Status);
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.EndTime = DateTime.UtcNow;
                    context.Status = ProcessStatus.Failed;
                    context.LastError = ex;

                    request.OnError?.Invoke(context, ex);

                    OnProcessFailed(new ProcessFailedEventArgs
                    {
                        ProcessId = request.ProcessId,
                        Exception = ex,
                        RetryCount = context.RetryCount,
                        FailedAt = context.EndTime.Value
                    });
                }

                _logger.LogError(ex, "Process {ProcessId} failed", request.ProcessId);
            }
            finally
            {
                if (context != null)
                {
                    lock (_syncLock)
                    {
                        _activeProcesses.Remove(request.ProcessId);
                        _completedProcesses[request.ProcessId] = context;

                        CleanupOldRecords();
                    }

                    await ChangeProcessStatusAsync(
                        request.ProcessId,
                        context.Status,
                        CancellationToken.None);
                }

                processCts?.Dispose();
            }
        }

        private async Task ChangeProcessStatusAsync(
            string processId,
            ProcessStatus newStatus,
            CancellationToken cancellationToken)
        {
            var context = await GetProcessContextAsync(processId, cancellationToken);
            if (context == null) return;

            var oldStatus = context.Status;
            context.Status = newStatus;

            OnProcessStatusChanged(new ProcessStatusChangedEventArgs
            {
                ProcessId = processId,
                OldStatus = oldStatus,
                NewStatus = newStatus,
                ChangedAt = DateTime.UtcNow
            });

            _logger.LogDebug("Process {ProcessId} status changed: {OldStatus} -> {NewStatus}",
                processId, oldStatus, newStatus);
        }

        private void CleanupOldRecords()
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-24);
            var keysToRemove = _completedProcesses
                .Where(kvp => kvp.Value.EndTime.HasValue && kvp.Value.EndTime.Value < cutoffTime) // .Value eklendi
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _completedProcesses.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                _logger.LogDebug("Cleaned up {Count} old process records", keysToRemove.Count);
            }
        }

        private void ValidateRequest(ProcessingRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Name))
                throw new ArgumentException("Process name cannot be empty", nameof(request.Name));

            if (request.ExecuteFunction == null)
                throw new ArgumentException("Execute function cannot be null", nameof(request.ExecuteFunction));

            if (request.Timeout.HasValue && request.Timeout.Value <= TimeSpan.Zero)
                throw new ArgumentException("Timeout must be positive", nameof(request.Timeout));

            if (request.MaxRetryAttempts < 0)
                throw new ArgumentException("Max retry attempts cannot be negative", nameof(request.MaxRetryAttempts));
        }

        private void HealthCheckCallback(object state)
        {
            try
            {
                var stats = GetStatisticsAsync(CancellationToken.None).GetAwaiter().GetResult();

                if (stats.Pending > _settings.MaxQueueSize * 0.8)
                {
                    _logger.LogWarning("Queue is {Percentage}% full",
                        (stats.Pending * 100.0 / _settings.MaxQueueSize).ToString("F1"));
                }

                if (stats.Running >= _settings.MaxConcurrentProcesses)
                {
                    _logger.LogWarning("All worker threads are busy");
                }

                var deadProcesses = _activeProcesses.Values
                    .Where(p => p.StartTime < DateTime.UtcNow.AddMinutes(-10))
                    .ToList();

                foreach (var deadProcess in deadProcesses)
                {
                    _logger.LogError("Dead process detected: {ProcessId}, running for {Duration}",
                        deadProcess.ProcessId, DateTime.UtcNow - deadProcess.StartTime);

                    CancelProcessAsync(deadProcess.ProcessId, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in health check callback");
            }
        }

        private void PerformanceMonitoringCallback(object state)
        {
            try
            {
                var stats = GetStatisticsAsync(CancellationToken.None).GetAwaiter().GetResult();

                _logger.LogDebug(
                    "Performance stats - Active: {Active}, Queue: {Queue}, Avg Time: {AvgTime}",
                    stats.Running, stats.Pending, stats.AverageExecutionTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in performance monitoring callback");
            }
        }

        private void OnProcessCompleted(ProcessCompletedEventArgs e)
        {
            ProcessCompleted?.Invoke(this, e);
        }

        private void OnProcessFailed(ProcessFailedEventArgs e)
        {
            ProcessFailed?.Invoke(this, e);
        }

        private void OnProcessStatusChanged(ProcessStatusChangedEventArgs e)
        {
            ProcessStatusChanged?.Invoke(this, e);
        }

        #endregion
    }

    /// <summary>
    /// Processing Engine özel exception'ı;
    /// </summary>
    public class ProcessingEngineException : Exception
    {
        public string ErrorCode { get; }
        public DateTime Timestamp { get; }

        public ProcessingEngineException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
            Timestamp = DateTime.UtcNow;
        }

        public ProcessingEngineException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Timestamp = DateTime.UtcNow;
        }
    }
}
