using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEDA.ContentCreation.AnimationTools.RiggingSystems;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.ExceptionHandling.RecoveryStrategies
{
    /// <summary>
    /// Represents the result of a recovery attempt
    /// </summary>
    public class RecoveryResult
    {
        public string RecoveryId { get; set; }
        public bool Success { get; set; }
        public RecoveryStrategy Strategy { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan Duration { get; set; }
        public int Attempts { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public Dictionary<string, object> Context { get; set; }
        public List<RecoveryStep> Steps { get; set; }
        public object RecoveredData { get; set; }
        public RecoveryOutcome Outcome { get; set; }

        public RecoveryResult()
        {
            Context = new Dictionary<string, object>();
            Steps = new List<RecoveryStep>();
        }
    }

    /// <summary>
    /// Represents a single step in the recovery process
    /// </summary>
    public class RecoveryStep
    {
        public string StepId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public StepType Type { get; set; }
        public StepStatus Status { get; set; }
        public TimeSpan Duration { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public Dictionary<string, object> Results { get; set; }
        public DateTime ExecutedAt { get; set; }

        public RecoveryStep()
        {
            Parameters = new Dictionary<string, object>();
            Results = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Represents a recovery strategy configuration
    /// </summary>
    public class RecoveryStrategyConfig
    {
        public string StrategyId { get; set; }
        public RecoveryStrategy Type { get; set; }
        public RecoveryStrategyPriority Priority { get; set; }
        public int MaxAttempts { get; set; }
        public TimeSpan Timeout { get; set; }
        public TimeSpan RetryDelay { get; set; }
        public double RetryMultiplier { get; set; }
        public int CircuitBreakerThreshold { get; set; }
        public TimeSpan CircuitBreakerTimeout { get; set; }
        public int BulkheadMaxConcurrent { get; set; }
        public int BulkheadMaxQueue { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public List<string> ApplicableErrorCodes { get; set; }
        public List<string> ApplicableExceptionTypes { get; set; }
        public SeverityLevel MinimumSeverity { get; set; }
        public SeverityLevel MaximumSeverity { get; set; }
        public bool Enabled { get; set; }

        public RecoveryStrategyConfig()
        {
            Parameters = new Dictionary<string, object>();
            ApplicableErrorCodes = new List<string>();
            ApplicableExceptionTypes = new List<string>();
        }
    }

    /// <summary>
    /// Represents the context for a recovery operation
    /// </summary>
    public class RecoveryContext
    {
        public string RecoveryId { get; set; }
        public StructuredException Exception { get; set; }
        public ExceptionContext ExceptionContext { get; set; }
        public RecoveryStrategy Strategy { get; set; }
        public int CurrentAttempt { get; set; }
        public int MaxAttempts { get; set; }
        public TimeSpan Timeout { get; set; }
        public DateTime StartedAt { get; set; }
        public Dictionary<string, object> AdditionalContext { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public List<RecoveryStep> Steps { get; set; }
        public RecoveryState State { get; set; }

        public RecoveryContext()
        {
            AdditionalContext = new Dictionary<string, object>();
            Steps = new List<RecoveryStep>();
            State = RecoveryState.Initializing;
        }

        public void AddContext(string key, object value)
        {
            AdditionalContext[key] = value;
        }

        public T GetContext<T>(string key, T defaultValue = default)
        {
            if (AdditionalContext.TryGetValue(key, out var value) && value is T typedValue)
                return typedValue;

            return defaultValue;
        }
    }

    /// <summary>
    /// Circuit breaker state for fault tolerance
    /// </summary>
    public class CircuitBreakerState
    {
        public string CircuitId { get; set; }
        public CircuitState State { get; set; }
        public int FailureCount { get; set; }
        public int SuccessCount { get; set; }
        public DateTime? LastFailureTime { get; set; }
        public DateTime? LastSuccessTime { get; set; }
        public DateTime? HalfOpenTime { get; set; }
        public int Threshold { get; set; }
        public TimeSpan Timeout { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastStateChange { get; set; }

        public bool IsClosed => State == CircuitState.Closed;
        public bool IsOpen => State == CircuitState.Open;
        public bool IsHalfOpen => State == CircuitState.HalfOpen;

        public bool ShouldAllowRequest()
        {
            if (State == CircuitState.Closed) return true;
            if (State == CircuitState.HalfOpen) return true;

            if (State == CircuitState.Open)
            {
                if (LastFailureTime.HasValue &&
                    DateTime.UtcNow - LastFailureTime.Value >= Timeout)
                {
                    State = CircuitState.HalfOpen;
                    HalfOpenTime = DateTime.UtcNow;
                    LastStateChange = DateTime.UtcNow;
                    return true;
                }

                return false;
            }

            return false;
        }

        public void RecordSuccess()
        {
            SuccessCount++;
            LastSuccessTime = DateTime.UtcNow;

            if (State == CircuitState.HalfOpen)
            {
                State = CircuitState.Closed;
                FailureCount = 0;
                LastStateChange = DateTime.UtcNow;
            }
        }

        public void RecordFailure()
        {
            FailureCount++;
            LastFailureTime = DateTime.UtcNow;

            if (State == CircuitState.HalfOpen)
            {
                State = CircuitState.Open;
                LastStateChange = DateTime.UtcNow;
            }
            else if (State == CircuitState.Closed && FailureCount >= Threshold)
            {
                State = CircuitState.Open;
                LastStateChange = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Bulkhead isolation state for resource protection
    /// </summary>
    public class BulkheadState
    {
        public string BulkheadId { get; set; }
        public int MaxConcurrent { get; set; }
        public int MaxQueue { get; set; }
        public int CurrentConcurrent { get; set; }
        public int CurrentQueue { get; set; }
        public Queue<DateTime> QueueTimestamps { get; set; }
        public DateTime CreatedAt { get; set; }

        public bool TryAcquireSlot()
        {
            if (CurrentConcurrent < MaxConcurrent)
            {
                CurrentConcurrent++;
                return true;
            }
            return false;
        }

        public void ReleaseSlot()
        {
            if (CurrentConcurrent > 0)
                CurrentConcurrent--;
        }

        public bool TryQueueRequest()
        {
            if (QueueTimestamps == null)
                QueueTimestamps = new Queue<DateTime>();

            if (CurrentQueue < MaxQueue)
            {
                CurrentQueue++;
                QueueTimestamps.Enqueue(DateTime.UtcNow);
                return true;
            }
            return false;
        }

        public DateTime? DequeueRequest()
        {
            if (QueueTimestamps != null && QueueTimestamps.Count > 0)
            {
                CurrentQueue--;
                return QueueTimestamps.Dequeue();
            }
            return null;
        }
    }

    /// <summary>
    /// Enums for recovery engine
    /// </summary>
    public enum RecoveryStrategy
    {
        None,
        Retry,
        Fallback,
        CircuitBreaker,
        Bulkhead,
        Timeout,
        Compensation,
        Degradation,
        EmergencyShutdown,
        Rollback,
        CheckpointRestore,
        DataSync,
        ResourceReallocation,
        LoadShedding,
        GracefulDegradation
    }

    public enum RecoveryOutcome
    {
        Success,
        PartialSuccess,
        Failure,
        Degraded,
        Compensated,
        RolledBack,
        CheckpointRestored
    }

    public enum RecoveryStrategyPriority
    {
        Critical,
        High,
        Medium,
        Low
    }

    public enum StepType
    {
        Validation,
        Preparation,
        Execution,
        Verification,
        Cleanup,
        Compensation,
        Rollback,
        Notification,
        Logging
    }

    public enum StepStatus
    {
        Pending,
        Executing,
        Completed,
        Failed,
        Skipped,
        Compensated
    }

    public enum RecoveryState
    {
        Initializing,
        Preparing,
        Executing,
        Verifying,
        Completing,
        Compensating,
        Failed,
        Completed,
        Cancelled
    }

    public enum CircuitState
    {
        Closed,
        Open,
        HalfOpen
    }

    /// <summary>
    /// Interface for recovery engine
    /// </summary>
    public interface IRecoveryEngine
    {
        Task<RecoveryResult> AttemptRecoveryAsync(
            StructuredException exception,
            List<RecoveryStrategy> strategies,
            ExceptionContext context,
            CancellationToken cancellationToken = default);

        Task<RecoveryResult> ExecuteRecoveryStrategyAsync(
            RecoveryStrategy strategy,
            RecoveryContext recoveryContext,
            CancellationToken cancellationToken = default);

        Task<CircuitBreakerState> GetCircuitStateAsync(string circuitId, CancellationToken cancellationToken = default);
        Task<bool> ResetCircuitAsync(string circuitId, CancellationToken cancellationToken = default);
        Task<RecoveryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
        Task<bool> TestRecoveryAsync(CancellationToken cancellationToken = default);
        void RegisterCustomStrategy(string name, ICustomRecoveryStrategy strategy);
    }

    public interface ICustomRecoveryStrategy
    {
        Task<RecoveryResult> ExecuteAsync(RecoveryContext context, CancellationToken cancellationToken);
        bool IsApplicable(StructuredException exception, RecoveryContext context);
    }

    // Eğer sende yoksa arayüz (FallbackStrategies dosyasında vardı diye hatırlıyorum),
    // burada sadece referans amaçlı:
    public interface IFallbackStrategies
    {
        Task<object> ExecuteFallbackAsync(RecoveryContext context, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Advanced recovery engine with multiple fault tolerance patterns
    /// </summary>
    public class RecoveryEngine : IRecoveryEngine
    {
        private readonly ILogger<RecoveryEngine> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly IFallbackStrategies _fallbackStrategies;

        private readonly RecoveryEngineConfig _engineConfig;
        private readonly Dictionary<RecoveryStrategy, RecoveryStrategyConfig> _strategyConfigs;
        private readonly Dictionary<string, ICustomRecoveryStrategy> _customStrategies;
        private readonly ConcurrentDictionary<string, CircuitBreakerState> _circuitBreakers;
        private readonly ConcurrentDictionary<string, BulkheadState> _bulkheads;
        private readonly SemaphoreSlim _recoverySemaphore;
        private readonly RecoveryStatistics _statistics;

        private const int DefaultMaxConcurrentRecoveries = 10;
        private static readonly Random _random = new Random();

        public RecoveryEngine(
            ILogger<RecoveryEngine> logger,
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            IFallbackStrategies fallbackStrategies = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _fallbackStrategies = fallbackStrategies;

            _engineConfig = LoadConfiguration();
            _strategyConfigs = InitializeStrategyConfigs();
            _customStrategies = new Dictionary<string, ICustomRecoveryStrategy>();
            _circuitBreakers = new ConcurrentDictionary<string, CircuitBreakerState>();
            _bulkheads = new ConcurrentDictionary<string, BulkheadState>();

            _recoverySemaphore = new SemaphoreSlim(
                _engineConfig.MaxConcurrentRecoveries,
                _engineConfig.MaxConcurrentRecoveries);

            _statistics = new RecoveryStatistics();

            InitializeCircuitBreakers();
            InitializeBulkheads();

            _logger.LogInformation("RecoveryEngine initialized with {StrategyCount} strategies",
                _strategyConfigs.Count);
        }

        public async Task<RecoveryResult> AttemptRecoveryAsync(
            StructuredException exception,
            List<RecoveryStrategy> strategies,
            ExceptionContext context,
            CancellationToken cancellationToken = default)
        {
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));

            if (strategies == null || !strategies.Any())
                return CreateNoRecoveryResult(exception);

            var recoveryId = $"REC_{Guid.NewGuid().ToString("N").Substring(0, 12)}";

            _logger.LogInformation("Starting recovery attempt: {RecoveryId}, Strategies: {Strategies}",
                recoveryId, string.Join(", ", strategies));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var recoveryResult = new RecoveryResult
            {
                RecoveryId = recoveryId,
                Strategy = RecoveryStrategy.None,
                StartedAt = DateTime.UtcNow,
                Context = new Dictionary<string, object>
                {
                    { "ExceptionErrorCode", exception.ErrorCode },
                    { "ExceptionSeverity", exception.Severity },
                    { "StrategiesAttempted", strategies.Count }
                }
            };

            try
            {
                if (!ShouldAttemptRecovery(exception))
                {
                    _logger.LogDebug("Recovery skipped for exception: {ErrorCode}", exception.ErrorCode);
                    recoveryResult.Outcome = RecoveryOutcome.Failure;
                    recoveryResult.ErrorMessage = "Recovery not applicable";
                    return recoveryResult;
                }

                await _recoverySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                var prioritizedStrategies = PrioritizeStrategies(strategies, exception);

                foreach (var strategy in prioritizedStrategies)
                {
                    var strategyConfig = GetStrategyConfig(strategy);

                    if (!IsStrategyApplicable(strategyConfig, exception))
                    {
                        _logger.LogDebug("Strategy {Strategy} not applicable for {ErrorCode}",
                            strategy, exception.ErrorCode);
                        continue;
                    }

                    var recoveryContext = new RecoveryContext
                    {
                        RecoveryId = recoveryId,
                        Exception = exception,
                        ExceptionContext = context,
                        Strategy = strategy,
                        MaxAttempts = strategyConfig.MaxAttempts,
                        Timeout = strategyConfig.Timeout,
                        StartedAt = DateTime.UtcNow,
                        CancellationToken = cancellationToken
                    };

                    if (context?.AdditionalContext != null)
                    {
                        foreach (var kvp in context.AdditionalContext)
                            recoveryContext.AddContext(kvp.Key, kvp.Value);
                    }

                    var strategyResult = await ExecuteRecoveryStrategyAsync(
                        strategy, recoveryContext, cancellationToken).ConfigureAwait(false);

                    recoveryResult.Steps.AddRange(strategyResult.Steps);
                    recoveryResult.Attempts += strategyResult.Attempts;

                    if (strategyResult.Success)
                    {
                        stopwatch.Stop();

                        recoveryResult.Success = true;
                        recoveryResult.Strategy = strategy;
                        recoveryResult.Outcome = DetermineOutcome(strategyResult);
                        recoveryResult.Duration = stopwatch.Elapsed;
                        recoveryResult.CompletedAt = DateTime.UtcNow;
                        recoveryResult.RecoveredData = strategyResult.RecoveredData;

                        UpdateStatistics(recoveryResult, true);

                        _logger.LogInformation("Recovery successful: {RecoveryId}, Strategy: {Strategy}, Duration: {Duration}",
                            recoveryId, strategy, stopwatch.Elapsed);

                        return recoveryResult;
                    }

                    _logger.LogWarning("Strategy {Strategy} failed: {ErrorMessage}",
                        strategy, strategyResult.ErrorMessage);
                }

                stopwatch.Stop();

                recoveryResult.Success = false;
                recoveryResult.Outcome = RecoveryOutcome.Failure;
                recoveryResult.Duration = stopwatch.Elapsed;
                recoveryResult.CompletedAt = DateTime.UtcNow;
                recoveryResult.ErrorMessage = "All recovery strategies failed";

                UpdateStatistics(recoveryResult, false);

                _logger.LogWarning("Recovery failed: {RecoveryId}, Duration: {Duration}",
                    recoveryId, stopwatch.Elapsed);

                return recoveryResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Recovery cancelled: {RecoveryId}", recoveryId);

                recoveryResult.Success = false;
                recoveryResult.Outcome = RecoveryOutcome.Failure;
                recoveryResult.ErrorMessage = "Recovery was cancelled";
                recoveryResult.CompletedAt = DateTime.UtcNow;

                return recoveryResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during recovery attempt: {RecoveryId}", recoveryId);

                recoveryResult.Success = false;
                recoveryResult.Outcome = RecoveryOutcome.Failure;
                recoveryResult.ErrorMessage = $"Recovery engine error: {ex.Message}";
                recoveryResult.CompletedAt = DateTime.UtcNow;

                return recoveryResult;
            }
            finally
            {
                try { _recoverySemaphore.Release(); } catch { /* ignore */ }
                stopwatch.Stop();
            }
        }

        public async Task<RecoveryResult> ExecuteRecoveryStrategyAsync(
            RecoveryStrategy strategy,
            RecoveryContext recoveryContext,
            CancellationToken cancellationToken = default)
        {
            if (recoveryContext == null)
                throw new ArgumentNullException(nameof(recoveryContext));

            var strategyConfig = GetStrategyConfig(strategy);

            var strategyResult = new RecoveryResult
            {
                RecoveryId = recoveryContext.RecoveryId,
                Strategy = strategy,
                StartedAt = DateTime.UtcNow,
                Context = new Dictionary<string, object>(recoveryContext.AdditionalContext ?? new Dictionary<string, object>())
            };

            _logger.LogDebug("Executing recovery strategy: {Strategy} for {RecoveryId}",
                strategy, recoveryContext.RecoveryId);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (strategy == RecoveryStrategy.CircuitBreaker ||
                    (strategyConfig.Parameters?.ContainsKey("UseCircuitBreaker") == true))
                {
                    var circuitId = GetCircuitId(recoveryContext);
                    if (!await CheckCircuitBreakerAsync(circuitId, cancellationToken).ConfigureAwait(false))
                    {
                        strategyResult.Success = false;
                        strategyResult.ErrorMessage = "Circuit breaker is open";
                        strategyResult.Outcome = RecoveryOutcome.Failure;
                        return strategyResult;
                    }
                }

                if (strategy == RecoveryStrategy.Bulkhead ||
                    (strategyConfig.Parameters?.ContainsKey("UseBulkhead") == true))
                {
                    var bulkheadId = GetBulkheadId(recoveryContext);
                    if (!await CheckBulkheadAsync(bulkheadId, cancellationToken).ConfigureAwait(false))
                    {
                        strategyResult.Success = false;
                        strategyResult.ErrorMessage = "Bulkhead limit reached";
                        strategyResult.Outcome = RecoveryOutcome.Failure;
                        return strategyResult;
                    }
                }

                switch (strategy)
                {
                    case RecoveryStrategy.Retry:
                        strategyResult = await ExecuteRetryStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.Fallback:
                        strategyResult = await ExecuteFallbackStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.CircuitBreaker:
                        strategyResult = await ExecuteCircuitBreakerStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.Bulkhead:
                        strategyResult = await ExecuteBulkheadStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.Timeout:
                        strategyResult = await ExecuteTimeoutStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.Compensation:
                        strategyResult = await ExecuteCompensationStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.Degradation:
                        strategyResult = await ExecuteDegradationStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.EmergencyShutdown:
                        strategyResult = await ExecuteEmergencyShutdownStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.Rollback:
                        strategyResult = await ExecuteRollbackStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    case RecoveryStrategy.CheckpointRestore:
                        strategyResult = await ExecuteCheckpointRestoreStrategyAsync(strategyConfig, recoveryContext, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                    {
                        var customName = strategy.ToString();
                        if (_customStrategies.TryGetValue(customName, out var custom))
                            strategyResult = await ExecuteCustomStrategyAsync(customName, recoveryContext, cancellationToken).ConfigureAwait(false);
                        else
                            throw new NotSupportedException($"Strategy {strategy} is not supported");
                        break;
                    }
                }

                stopwatch.Stop();
                strategyResult.Duration = stopwatch.Elapsed;
                strategyResult.CompletedAt = DateTime.UtcNow;

                return strategyResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing recovery strategy {Strategy}: {RecoveryId}",
                    strategy, recoveryContext.RecoveryId);

                stopwatch.Stop();

                strategyResult.Success = false;
                strategyResult.ErrorMessage = $"Strategy execution failed: {ex.Message}";
                strategyResult.Duration = stopwatch.Elapsed;
                strategyResult.CompletedAt = DateTime.UtcNow;
                strategyResult.Outcome = RecoveryOutcome.Failure;

                return strategyResult;
            }
        }

        public Task<CircuitBreakerState> GetCircuitStateAsync(string circuitId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(circuitId))
                throw new ArgumentNullException(nameof(circuitId));

            if (_circuitBreakers.TryGetValue(circuitId, out var state))
                return Task.FromResult(state);

            return Task.FromResult<CircuitBreakerState>(null);
        }

        public Task<bool> ResetCircuitAsync(string circuitId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(circuitId))
                throw new ArgumentNullException(nameof(circuitId));

            if (_circuitBreakers.TryGetValue(circuitId, out var state))
            {
                state.State = CircuitState.Closed;
                state.FailureCount = 0;
                state.LastStateChange = DateTime.UtcNow;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<RecoveryStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_statistics.Clone());

        public async Task<bool> TestRecoveryAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Testing recovery engine...");

                var testException = new StructuredException(
                    "ERR_SYS_0001",
                    "Test exception for recovery",
                    severity: SeverityLevel.Error);

                var testContext = new ExceptionContext
                {
                    OperationName = "TestRecovery",
                    ComponentName = "RecoveryEngine"
                };

                var strategies = new List<RecoveryStrategy> { RecoveryStrategy.Retry };

                // Not: Retry için context'e RetryOperation eklemezsen retry başarısız döner.
                // Test amaçlı basit operation ekliyoruz:
                testContext.AdditionalContext ??= new Dictionary<string, object>();

                var result = await AttemptRecoveryAsync(testException, strategies, testContext, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogWarning("Recovery test failed: {ErrorMessage}", result.ErrorMessage);
                    return false;
                }

                var circuitId = "test_circuit";
                await ResetCircuitAsync(circuitId, cancellationToken).ConfigureAwait(false);

                var circuitState = await GetCircuitStateAsync(circuitId, cancellationToken).ConfigureAwait(false);
                if (circuitState == null || !circuitState.IsClosed)
                {
                    _logger.LogWarning("Circuit breaker test failed");
                    return false;
                }

                _logger.LogInformation("Recovery engine test passed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recovery engine test failed");
                return false;
            }
        }

        public void RegisterCustomStrategy(string name, ICustomRecoveryStrategy strategy)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            if (strategy == null)
                throw new ArgumentNullException(nameof(strategy));

            _customStrategies[name] = strategy;
            _logger.LogInformation("Custom recovery strategy registered: {StrategyName}", name);
        }

        // -------------------------
        // Private Methods
        // -------------------------

        private RecoveryEngineConfig LoadConfiguration()
        {
            return new RecoveryEngineConfig
            {
                MaxConcurrentRecoveries = _configuration.GetValue(
                    "ExceptionHandling:Recovery:MaxConcurrentRecoveries",
                    DefaultMaxConcurrentRecoveries),

                DefaultRetryAttempts = _configuration.GetValue(
                    "ExceptionHandling:Recovery:DefaultRetryAttempts", 3),

                DefaultRetryDelay = TimeSpan.FromMilliseconds(
                    _configuration.GetValue("ExceptionHandling:Recovery:DefaultRetryDelayMs", 100)),

                DefaultCircuitBreakerThreshold = _configuration.GetValue(
                    "ExceptionHandling:Recovery:DefaultCircuitBreakerThreshold", 5),

                DefaultCircuitBreakerTimeout = TimeSpan.FromSeconds(
                    _configuration.GetValue("ExceptionHandling:Recovery:DefaultCircuitBreakerTimeoutSeconds", 30)),

                DefaultBulkheadConcurrent = _configuration.GetValue(
                    "ExceptionHandling:Recovery:DefaultBulkheadConcurrent", 10),

                DefaultBulkheadQueue = _configuration.GetValue(
                    "ExceptionHandling:Recovery:DefaultBulkheadQueue", 20),

                EnableDegradation = _configuration.GetValue(
                    "ExceptionHandling:Recovery:EnableDegradation", true),

                EnableEmergencyShutdown = _configuration.GetValue(
                    "ExceptionHandling:Recovery:EnableEmergencyShutdown", true),

                EnableStatistics = _configuration.GetValue(
                    "ExceptionHandling:Recovery:EnableStatistics", true),

                RecoveryTimeout = TimeSpan.FromSeconds(
                    _configuration.GetValue("ExceptionHandling:Recovery:RecoveryTimeoutSeconds", 60))
            };
        }

        private Dictionary<RecoveryStrategy, RecoveryStrategyConfig> InitializeStrategyConfigs()
        {
            var configs = new Dictionary<RecoveryStrategy, RecoveryStrategyConfig>();

            configs[RecoveryStrategy.Retry] = new RecoveryStrategyConfig
            {
                StrategyId = "RETRY_DEFAULT",
                Type = RecoveryStrategy.Retry,
                Priority = RecoveryStrategyPriority.High,
                MaxAttempts = _engineConfig.DefaultRetryAttempts,
                Timeout = _engineConfig.RecoveryTimeout,
                RetryDelay = _engineConfig.DefaultRetryDelay,
                RetryMultiplier = 2.0,
                Enabled = true,
                ApplicableErrorCodes = new List<string> { "ERR_SYS_", "ERR_NET_", "ERR_DB_", "ERR_AI_" },
                MinimumSeverity = SeverityLevel.Warning,
                Parameters = new Dictionary<string, object>
                {
                    { "UseExponentialBackoff", true },
                    { "UseJitter", true },
                    { "MaxBackoff", TimeSpan.FromSeconds(30) }
                }
            };

            configs[RecoveryStrategy.Fallback] = new RecoveryStrategyConfig
            {
                StrategyId = "FALLBACK_DEFAULT",
                Type = RecoveryStrategy.Fallback,
                Priority = RecoveryStrategyPriority.Medium,
                MaxAttempts = 1,
                Timeout = _engineConfig.RecoveryTimeout,
                Enabled = true,
                ApplicableErrorCodes = new List<string> { "ERR_SYS_", "ERR_DB_", "ERR_AI_", "ERR_DEC_" },
                MinimumSeverity = SeverityLevel.Error,
                Parameters = new Dictionary<string, object>
                {
                    { "FallbackTimeout", TimeSpan.FromSeconds(10) },
                    { "RequireVerification", true }
                }
            };

            configs[RecoveryStrategy.CircuitBreaker] = new RecoveryStrategyConfig
            {
                StrategyId = "CIRCUIT_DEFAULT",
                Type = RecoveryStrategy.CircuitBreaker,
                Priority = RecoveryStrategyPriority.Critical,
                MaxAttempts = 1,
                Timeout = _engineConfig.RecoveryTimeout,
                CircuitBreakerThreshold = _engineConfig.DefaultCircuitBreakerThreshold,
                CircuitBreakerTimeout = _engineConfig.DefaultCircuitBreakerTimeout,
                Enabled = true,
                ApplicableErrorCodes = new List<string> { "ERR_SYS_", "ERR_DB_", "ERR_NET_" },
                MinimumSeverity = SeverityLevel.Error,
                Parameters = new Dictionary<string, object>
                {
                    { "TrackExceptions", true },
                    { "AutoReset", true }
                }
            };

            configs[RecoveryStrategy.Bulkhead] = new RecoveryStrategyConfig
            {
                StrategyId = "BULKHEAD_DEFAULT",
                Type = RecoveryStrategy.Bulkhead,
                Priority = RecoveryStrategyPriority.Medium,
                MaxAttempts = 1,
                Timeout = _engineConfig.RecoveryTimeout,
                BulkheadMaxConcurrent = _engineConfig.DefaultBulkheadConcurrent,
                BulkheadMaxQueue = _engineConfig.DefaultBulkheadQueue,
                Enabled = true,
                ApplicableErrorCodes = new List<string> { "ERR_SYS_", "ERR_RES_" },
                MinimumSeverity = SeverityLevel.Warning,
                Parameters = new Dictionary<string, object>
                {
                    { "QueueTimeout", TimeSpan.FromSeconds(30) }
                }
            };

            configs[RecoveryStrategy.Timeout] = new RecoveryStrategyConfig
            {
                StrategyId = "TIMEOUT_DEFAULT",
                Type = RecoveryStrategy.Timeout,
                Priority = RecoveryStrategyPriority.Low,
                MaxAttempts = 1,
                Timeout = TimeSpan.FromSeconds(10),
                Enabled = true,
                ApplicableErrorCodes = new List<string> { "ERR_SYS_0006", "ERR_NET_0001" },
                MinimumSeverity = SeverityLevel.Warning
            };

            configs[RecoveryStrategy.Degradation] = new RecoveryStrategyConfig
            {
                StrategyId = "DEGRADATION_DEFAULT",
                Type = RecoveryStrategy.Degradation,
                Priority = RecoveryStrategyPriority.Medium,
                MaxAttempts = 1,
                Timeout = _engineConfig.RecoveryTimeout,
                Enabled = _engineConfig.EnableDegradation,
                ApplicableErrorCodes = new List<string> { "ERR_SYS_", "ERR_RES_", "ERR_AI_" },
                MinimumSeverity = SeverityLevel.Warning,
                Parameters = new Dictionary<string, object>
                {
                    { "DegradationLevel", "Partial" },
                    { "MaintainCoreFunctions", true }
                }
            };

            configs[RecoveryStrategy.EmergencyShutdown] = new RecoveryStrategyConfig
            {
                StrategyId = "EMERGENCY_DEFAULT",
                Type = RecoveryStrategy.EmergencyShutdown,
                Priority = RecoveryStrategyPriority.Critical,
                MaxAttempts = 1,
                Timeout = TimeSpan.FromSeconds(5),
                Enabled = _engineConfig.EnableEmergencyShutdown,
                ApplicableErrorCodes = new List<string> { "ERR_SEC_", "ERR_SYS_0001" },
                MinimumSeverity = SeverityLevel.Critical,
                Parameters = new Dictionary<string, object>
                {
                    { "ShutdownLevel", "Controlled" },
                    { "PreserveData", true },
                    { "NotifyAdmins", true }
                }
            };

            return configs;
        }

        private void InitializeCircuitBreakers()
        {
            var defaultCircuits = new[]
            {
                ("circuit_system", "System operations"),
                ("circuit_database", "Database operations"),
                ("circuit_network", "Network operations"),
                ("circuit_external_api", "External API calls")
            };

            foreach (var (circuitId, _) in defaultCircuits)
            {
                var state = new CircuitBreakerState
                {
                    CircuitId = circuitId,
                    State = CircuitState.Closed,
                    FailureCount = 0,
                    SuccessCount = 0,
                    Threshold = _engineConfig.DefaultCircuitBreakerThreshold,
                    Timeout = _engineConfig.DefaultCircuitBreakerTimeout,
                    CreatedAt = DateTime.UtcNow,
                    LastStateChange = DateTime.UtcNow
                };

                _circuitBreakers[circuitId] = state;
            }
        }

        private void InitializeBulkheads()
        {
            var defaultBulkheads = new[]
            {
                ("bulkhead_system", "System operations", 5, 10),
                ("bulkhead_database", "Database operations", 3, 5),
                ("bulkhead_external", "External calls", 2, 3)
            };

            foreach (var (bulkheadId, _, maxConcurrent, maxQueue) in defaultBulkheads)
            {
                var state = new BulkheadState
                {
                    BulkheadId = bulkheadId,
                    MaxConcurrent = maxConcurrent,
                    MaxQueue = maxQueue,
                    CurrentConcurrent = 0,
                    CurrentQueue = 0,
                    QueueTimestamps = new Queue<DateTime>(),
                    CreatedAt = DateTime.UtcNow
                };

                _bulkheads[bulkheadId] = state;
            }
        }

        private RecoveryResult CreateNoRecoveryResult(StructuredException exception)
        {
            return new RecoveryResult
            {
                RecoveryId = $"NOREC_{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                Success = false,
                Strategy = RecoveryStrategy.None,
                ErrorMessage = "No recovery strategies provided",
                Outcome = RecoveryOutcome.Failure,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Duration = TimeSpan.Zero
            };
        }

        private bool ShouldAttemptRecovery(StructuredException exception)
        {
            if (exception.Severity < SeverityLevel.Warning)
                return false;

            var category = exception.ErrorCategory;
            if (category == "SEC" || category == "AUTH")
                return false;

            if (_engineConfig.DisabledErrorCodes?.Contains(exception.ErrorCode) == true)
                return false;

            return true;
        }

        private List<RecoveryStrategy> PrioritizeStrategies(List<RecoveryStrategy> strategies, StructuredException exception)
        {
            return strategies
                .Where(s => _strategyConfigs.ContainsKey(s) && _strategyConfigs[s].Enabled)
                .OrderByDescending(s => _strategyConfigs[s].Priority)
                .ThenBy(s => _strategyConfigs[s].MaxAttempts)
                .ToList();
        }

        private RecoveryStrategyConfig GetStrategyConfig(RecoveryStrategy strategy)
        {
            if (_strategyConfigs.TryGetValue(strategy, out var config))
                return config;

            return new RecoveryStrategyConfig
            {
                StrategyId = $"{strategy}_DEFAULT",
                Type = strategy,
                Priority = RecoveryStrategyPriority.Medium,
                MaxAttempts = 1,
                Timeout = _engineConfig.RecoveryTimeout,
                Enabled = true
            };
        }

        private bool IsStrategyApplicable(RecoveryStrategyConfig config, StructuredException exception)
        {
            if (!config.Enabled) return false;
            if (exception.Severity < config.MinimumSeverity) return false;
            if (config.MaximumSeverity > 0 && exception.Severity > config.MaximumSeverity) return false;

            if (config.ApplicableErrorCodes.Any() &&
                !config.ApplicableErrorCodes.Any(ec => exception.ErrorCode.StartsWith(ec)))
                return false;

            if (config.ApplicableExceptionTypes.Any() &&
                !config.ApplicableExceptionTypes.Any(et => exception.GetType().Name.Contains(et)))
                return false;

            return true;
        }

        private RecoveryOutcome DetermineOutcome(RecoveryResult result)
        {
            if (!result.Success)
                return RecoveryOutcome.Failure;

            var failedSteps = result.Steps.Count(s => s.Status == StepStatus.Failed);
            if (failedSteps > 0 && failedSteps < result.Steps.Count)
                return RecoveryOutcome.PartialSuccess;

            return result.Strategy switch
            {
                RecoveryStrategy.Degradation => RecoveryOutcome.Degraded,
                RecoveryStrategy.Compensation => RecoveryOutcome.Compensated,
                RecoveryStrategy.Rollback => RecoveryOutcome.RolledBack,
                RecoveryStrategy.CheckpointRestore => RecoveryOutcome.CheckpointRestored,
                _ => RecoveryOutcome.Success
            };
        }

        private string GetCircuitId(RecoveryContext context)
        {
            var component = context.ExceptionContext?.ComponentName ?? "unknown";
            var operation = context.ExceptionContext?.OperationName ?? "unknown";
            return $"circuit_{component}_{operation}".ToLowerInvariant();
        }

        private string GetBulkheadId(RecoveryContext context)
        {
            var component = context.ExceptionContext?.ComponentName ?? "unknown";
            return $"bulkhead_{component}".ToLowerInvariant();
        }

        private Task<bool> CheckCircuitBreakerAsync(string circuitId, CancellationToken cancellationToken)
        {
            if (!_circuitBreakers.TryGetValue(circuitId, out var state))
            {
                state = new CircuitBreakerState
                {
                    CircuitId = circuitId,
                    State = CircuitState.Closed,
                    Threshold = _engineConfig.DefaultCircuitBreakerThreshold,
                    Timeout = _engineConfig.DefaultCircuitBreakerTimeout,
                    CreatedAt = DateTime.UtcNow,
                    LastStateChange = DateTime.UtcNow
                };
                _circuitBreakers[circuitId] = state;
            }

            return Task.FromResult(state.ShouldAllowRequest());
        }

        private Task<bool> CheckBulkheadAsync(string bulkheadId, CancellationToken cancellationToken)
        {
            if (!_bulkheads.TryGetValue(bulkheadId, out var state))
            {
                state = new BulkheadState
                {
                    BulkheadId = bulkheadId,
                    MaxConcurrent = _engineConfig.DefaultBulkheadConcurrent,
                    MaxQueue = _engineConfig.DefaultBulkheadQueue,
                    CurrentConcurrent = 0,
                    CurrentQueue = 0,
                    QueueTimestamps = new Queue<DateTime>(),
                    CreatedAt = DateTime.UtcNow
                };
                _bulkheads[bulkheadId] = state;
            }

            return Task.FromResult(state.TryAcquireSlot());
        }

        // -------------------------
        // Strategy Implementations (senin dosyanın devamında varsa, bunları kaldırıp kendi gerçeklerini kullan)
        // -------------------------

        private Task<RecoveryResult> ExecuteRetryStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.Retry, Success = false, ErrorMessage = "ExecuteRetryStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteFallbackStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.Fallback, Success = false, ErrorMessage = "ExecuteFallbackStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteCircuitBreakerStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.CircuitBreaker, Success = false, ErrorMessage = "ExecuteCircuitBreakerStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteBulkheadStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.Bulkhead, Success = false, ErrorMessage = "ExecuteBulkheadStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteTimeoutStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.Timeout, Success = false, ErrorMessage = "ExecuteTimeoutStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteCompensationStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.Compensation, Success = false, ErrorMessage = "ExecuteCompensationStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteDegradationStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.Degradation, Success = false, ErrorMessage = "ExecuteDegradationStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteEmergencyShutdownStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.EmergencyShutdown, Success = false, ErrorMessage = "ExecuteEmergencyShutdownStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteRollbackStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.Rollback, Success = false, ErrorMessage = "ExecuteRollbackStrategyAsync not implemented" });

        private Task<RecoveryResult> ExecuteCheckpointRestoreStrategyAsync(RecoveryStrategyConfig config, RecoveryContext context, CancellationToken ct)
            => Task.FromResult(new RecoveryResult { RecoveryId = context.RecoveryId, Strategy = RecoveryStrategy.CheckpointRestore, Success = false, ErrorMessage = "ExecuteCheckpointRestoreStrategyAsync not implemented" });

        private async Task<RecoveryResult> ExecuteCustomStrategyAsync(string strategyName, RecoveryContext context, CancellationToken ct)
        {
            if (!_customStrategies.TryGetValue(strategyName, out var strategy))
                throw new InvalidOperationException($"Custom strategy '{strategyName}' not found");

            if (!strategy.IsApplicable(context.Exception, context))
            {
                return new RecoveryResult
                {
                    RecoveryId = context.RecoveryId,
                    Success = false,
                    Strategy = (RecoveryStrategy)Enum.Parse(typeof(RecoveryStrategy), strategyName),
                    ErrorMessage = "Strategy not applicable",
                    StartedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow
                };
            }

            return await strategy.ExecuteAsync(context, ct).ConfigureAwait(false);
        }

        private void UpdateStatistics(RecoveryResult result, bool success)
        {
            if (!_engineConfig.EnableStatistics)
                return;

            lock (_statistics)
            {
                _statistics.TotalRecoveries++;
                _statistics.LastRecoveryAt = DateTime.UtcNow;

                if (success)
                {
                    _statistics.SuccessfulRecoveries++;

                    if (!_statistics.RecoveriesByStrategy.ContainsKey(result.Strategy))
                        _statistics.RecoveriesByStrategy[result.Strategy] = 0;

                    _statistics.RecoveriesByStrategy[result.Strategy]++;

                    if (_statistics.SuccessfulRecoveries > 0)
                    {
                        var totalTicks = _statistics.AverageRecoveryTime.Ticks * (_statistics.SuccessfulRecoveries - 1);
                        totalTicks += result.Duration.Ticks;
                        _statistics.AverageRecoveryTime = TimeSpan.FromTicks(totalTicks / _statistics.SuccessfulRecoveries);
                    }
                }
                else
                {
                    _statistics.FailedRecoveries++;
                }

                if (result.Steps != null)
                {
                    _statistics.TotalStepsExecuted += result.Steps.Count;
                    _statistics.SuccessfulSteps += result.Steps.Count(s => s.Status == StepStatus.Completed);
                }
            }
        }
    }

    // -------------------------
    // Supporting Classes
    // -------------------------

    public class RecoveryEngineConfig
    {
        public int MaxConcurrentRecoveries { get; set; }
        public int DefaultRetryAttempts { get; set; }
        public TimeSpan DefaultRetryDelay { get; set; }
        public int DefaultCircuitBreakerThreshold { get; set; }
        public TimeSpan DefaultCircuitBreakerTimeout { get; set; }
        public int DefaultBulkheadConcurrent { get; set; }
        public int DefaultBulkheadQueue { get; set; }
        public bool EnableDegradation { get; set; }
        public bool EnableEmergencyShutdown { get; set; }
        public bool EnableStatistics { get; set; }
        public TimeSpan RecoveryTimeout { get; set; }
        public List<string> DisabledErrorCodes { get; set; }

        public RecoveryEngineConfig()
        {
            DisabledErrorCodes = new List<string>();
        }
    }

    public class RecoveryStatistics
    {
        public int TotalRecoveries { get; set; }
        public int SuccessfulRecoveries { get; set; }
        public int FailedRecoveries { get; set; }
        public DateTime LastRecoveryAt { get; set; }
        public TimeSpan AverageRecoveryTime { get; set; }
        public Dictionary<RecoveryStrategy, int> RecoveriesByStrategy { get; set; }
        public int TotalStepsExecuted { get; set; }
        public int SuccessfulSteps { get; set; }

        public RecoveryStatistics()
        {
            RecoveriesByStrategy = new Dictionary<RecoveryStrategy, int>();
        }

        public RecoveryStatistics Clone()
        {
            return new RecoveryStatistics
            {
                TotalRecoveries = TotalRecoveries,
                SuccessfulRecoveries = SuccessfulRecoveries,
                FailedRecoveries = FailedRecoveries,
                LastRecoveryAt = LastRecoveryAt,
                AverageRecoveryTime = AverageRecoveryTime,
                RecoveriesByStrategy = new Dictionary<RecoveryStrategy, int>(RecoveriesByStrategy),
                TotalStepsExecuted = TotalStepsExecuted,
                SuccessfulSteps = SuccessfulSteps
            };
        }
    }
}
