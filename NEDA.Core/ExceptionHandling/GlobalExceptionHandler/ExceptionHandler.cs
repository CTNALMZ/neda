using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.ExceptionHandling.GlobalExceptionHandler
{
    /// <summary>
    /// Represents a structured exception with additional context.
    /// </summary>
    public class StructuredException : Exception
    {
        public string ErrorCode { get; }
        public string ErrorCategory { get; }
        public Dictionary<string, object> Context { get; }
        public SeverityLevel Severity { get; }
        public DateTime Timestamp { get; }
        public string CorrelationId { get; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public string RequestPath { get; set; }
        public Dictionary<string, object> AdditionalData { get; }

        public StructuredException(
            string errorCode,
            string message,
            Exception innerException = null,
            Dictionary<string, object> context = null,
            SeverityLevel severity = SeverityLevel.Error,
            string correlationId = null,
            string userId = null,
            string sessionId = null,
            string requestPath = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode ?? "ERR_SYS_0001";
            ErrorCategory = ExtractCategoryFromCode(ErrorCode);
            Context = context ?? new Dictionary<string, object>();
            Severity = severity;
            Timestamp = DateTime.UtcNow;
            CorrelationId = correlationId ?? GenerateCorrelationId();
            UserId = userId;
            SessionId = sessionId;
            RequestPath = requestPath;
            AdditionalData = new Dictionary<string, object>();

            AddStandardContextData();
        }

        private string ExtractCategoryFromCode(string errorCode)
        {
            if (string.IsNullOrEmpty(errorCode) || errorCode.Length < 7)
                return "SYS";

            // ERR_XXX_0001 -> XXX
            // index 4 length 3
            var prefix = errorCode.Substring(4, 3);
            return prefix.ToUpperInvariant();
        }

        private string GenerateCorrelationId()
            => $"CORR_{Guid.NewGuid().ToString("N").Substring(0, 12)}";

        private void AddStandardContextData()
        {
            Context["MachineName"] = Environment.MachineName;
            Context["OSVersion"] = Environment.OSVersion.ToString();
            Context["ProcessId"] = Environment.ProcessId;
            Context["Timestamp"] = Timestamp;
            Context["UtcOffset"] = TimeZoneInfo.Local.GetUtcOffset(Timestamp);
        }

        public void AddContext(string key, object value)
        {
            if (!string.IsNullOrEmpty(key))
                Context[key] = value;
        }

        public void AddAdditionalData(string key, object value)
        {
            if (!string.IsNullOrEmpty(key))
                AdditionalData[key] = value;
        }

        public string ToJson()
        {
            var exceptionInfo = new
            {
                ErrorCode,
                ErrorCategory,
                Message = Message,
                Severity = Severity.ToString(),
                Timestamp,
                CorrelationId,
                UserId,
                SessionId,
                RequestPath,
                Context,
                AdditionalData,
                InnerException = InnerException?.Message,
                StackTrace = StackTrace
            };

            return JsonSerializer.Serialize(exceptionInfo, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
    }

    /// <summary>
    /// Represents the result of exception handling.
    /// </summary>
    public class ExceptionHandlingResult
    {
        public string HandlingId { get; set; }
        public string OriginalErrorCode { get; set; }
        public string FinalErrorCode { get; set; }
        public string UserMessage { get; set; }
        public string TechnicalMessage { get; set; }
        public SeverityLevel Severity { get; set; }
        public bool WasHandled { get; set; }
        public bool WasRecovered { get; set; }
        public RecoveryStrategy UsedRecoveryStrategy { get; set; }
        public TimeSpan HandlingDuration { get; set; }
        public List<ExceptionAction> ActionsTaken { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime HandledAt { get; set; }
    }

    public class ExceptionAction
    {
        public string ActionId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ActionType Type { get; set; }
        public ActionStatus Status { get; set; }
        public TimeSpan Duration { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime ExecutedAt { get; set; }
    }

    public class ExceptionHandlingConfig
    {
        public bool EnableGlobalHandling { get; set; }
        public bool EnableRecoveryStrategies { get; set; }
        public bool EnableErrorReporting { get; set; }
        public bool EnableDetailedLogging { get; set; }
        public bool EnablePerformanceTracking { get; set; }
        public int MaxHandlingDepth { get; set; }
        public TimeSpan DefaultTimeout { get; set; }
        public Dictionary<string, ExceptionPolicy> Policies { get; set; }
        public List<string> IgnoredExceptions { get; set; }
        public bool IncludeSensitiveData { get; set; }
        public bool MaskSensitiveDataInLogs { get; set; }
        public string DefaultUserMessage { get; set; }
    }

    public class ExceptionPolicy
    {
        public string PolicyId { get; set; }
        public List<string> ErrorCodes { get; set; }
        public List<string> ExceptionTypes { get; set; }
        public SeverityLevel MinimumSeverity { get; set; }
        public List<RecoveryStrategy> RecoveryStrategies { get; set; }
        public string DefaultUserMessage { get; set; }
        public bool ShouldRethrow { get; set; }
        public bool ShouldLog { get; set; }
        public bool ShouldReport { get; set; }
        public int MaxRetryAttempts { get; set; }
        public TimeSpan RetryDelay { get; set; }
        public List<ExceptionAction> PreHandlingActions { get; set; }
        public List<ExceptionAction> PostHandlingActions { get; set; }
    }

    public enum SeverityLevel
    {
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical,
        Fatal
    }

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
        EmergencyShutdown
    }

    public enum ActionType
    {
        Logging,
        Notification,
        Recovery,
        Compensation,
        Cleanup,
        Validation,
        Escalation,
        Documentation
    }

    public enum ActionStatus
    {
        Pending,
        Executing,
        Completed,
        Failed,
        Skipped,
        Cancelled
    }

    public enum HandlingMode
    {
        Synchronous,
        Asynchronous,
        Background
    }

    public interface IExceptionHandler
    {
        Task<ExceptionHandlingResult> HandleExceptionAsync(
            Exception exception,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default);

        Task<ExceptionHandlingResult> HandleStructuredExceptionAsync(
            StructuredException exception,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default);

        Task<T> ExecuteWithHandlingAsync<T>(
            Func<Task<T>> operation,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default);

        Task ExecuteWithHandlingAsync(
            Func<Task> operation,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default);

        void RegisterPolicy(ExceptionPolicy policy);
        void RegisterPolicyForType<TException>(ExceptionPolicy policy) where TException : Exception;

        Task<ExceptionHandlingStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
        Task<bool> TestHandlerAsync(CancellationToken cancellationToken = default);
    }

    public class ExceptionContext
    {
        public string OperationName { get; set; }
        public string ComponentName { get; set; }
        public string ModuleName { get; set; }
        public HandlingMode HandlingMode { get; set; }
        public Dictionary<string, object> AdditionalContext { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public string RequestId { get; set; }
        public string CorrelationId { get; set; }
        public bool ForceRecovery { get; set; }
        public bool BypassPolicies { get; set; }
        public TimeSpan? Timeout { get; set; }
        public List<string> Tags { get; set; }

        public ExceptionContext()
        {
            AdditionalContext = new Dictionary<string, object>();
            Tags = new List<string>();
            HandlingMode = HandlingMode.Synchronous;
        }

        public void AddContext(string key, object value)
        {
            if (!string.IsNullOrEmpty(key))
                AdditionalContext[key] = value;
        }

        public void AddTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return;

            if (!Tags.Contains(tag))
                Tags.Add(tag);
        }
    }

    /// <summary>
    /// Advanced global exception handler with recovery strategies.
    /// </summary>
    public class ExceptionHandler : IExceptionHandler
    {
        private readonly ILogger<ExceptionHandler> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly IRecoveryEngine _recoveryEngine;
        private readonly IErrorReporter _errorReporter;
        private readonly IDiagnosticTool _diagnosticTool;

        private readonly ExceptionHandlingConfig _handlerConfig;
        private readonly Dictionary<string, ExceptionPolicy> _policies;
        private readonly Dictionary<Type, ExceptionPolicy> _typePolicies;
        private readonly SemaphoreSlim _handlingSemaphore;
        private readonly Dictionary<string, ExceptionHandlingSession> _activeSessions;
        private readonly ExceptionHandlingStatistics _statistics;

        private const int MaxConcurrentHandlings = 20;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() }
        };

        public ExceptionHandler(
            ILogger<ExceptionHandler> logger,
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            IRecoveryEngine recoveryEngine,
            IErrorReporter errorReporter,
            IDiagnosticTool diagnosticTool)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _recoveryEngine = recoveryEngine ?? throw new ArgumentNullException(nameof(recoveryEngine));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));

            _handlerConfig = LoadConfiguration();
            _policies = new Dictionary<string, ExceptionPolicy>();
            _typePolicies = new Dictionary<Type, ExceptionPolicy>();
            _handlingSemaphore = new SemaphoreSlim(MaxConcurrentHandlings, MaxConcurrentHandlings);
            _activeSessions = new Dictionary<string, ExceptionHandlingSession>();
            _statistics = new ExceptionHandlingStatistics();

            InitializeDefaultPolicies();

            _logger.LogInformation("ExceptionHandler initialized with {PolicyCount} policies",
                _policies.Count + _typePolicies.Count);
        }

        public async Task<ExceptionHandlingResult> HandleExceptionAsync(
            Exception exception,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));

            var handlingId = $"HND_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            var session = CreateHandlingSession(handlingId, exception, context);

            _logger.LogInformation("Starting exception handling: {HandlingId}, Type: {ExceptionType}",
                handlingId, exception.GetType().Name);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            bool semaphoreAcquired = false;

            try
            {
                if (ShouldIgnoreException(exception))
                {
                    _logger.LogDebug("Exception ignored: {ExceptionType}", exception.GetType().Name);
                    return CreateIgnoredResult(handlingId, exception);
                }

                var structuredException = exception as StructuredException
                    ?? ConvertToStructuredException(exception, context);

                session.StructuredException = structuredException;

                await _handlingSemaphore.WaitAsync(cancellationToken);
                semaphoreAcquired = true;

                lock (_activeSessions)
                {
                    _activeSessions[handlingId] = session;
                }

                var result = await ExecuteHandlingPipelineAsync(session, cancellationToken);

                stopwatch.Stop();
                result.HandlingDuration = stopwatch.Elapsed;

                UpdateStatistics(result);

                _logger.LogInformation("Exception handling completed: {HandlingId}, Result: {Result}",
                    handlingId, result.WasHandled ? "Handled" : "Failed");

                return result;
            }
            catch (Exception handlingError)
            {
                _logger.LogError(handlingError, "Error during exception handling: {HandlingId}", handlingId);
                return CreateFallbackResult(handlingId, exception, handlingError);
            }
            finally
            {
                lock (_activeSessions)
                {
                    _activeSessions.Remove(handlingId);
                }

                if (semaphoreAcquired)
                    _handlingSemaphore.Release();

                stopwatch.Stop();
            }
        }

        public async Task<ExceptionHandlingResult> HandleStructuredExceptionAsync(
            StructuredException exception,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));

            var handlingId = $"HND_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            var session = CreateHandlingSession(handlingId, exception, context);
            session.StructuredException = exception;

            _logger.LogInformation("Starting structured exception handling: {HandlingId}, ErrorCode: {ErrorCode}",
                handlingId, exception.ErrorCode);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            bool semaphoreAcquired = false;

            try
            {
                if (ShouldIgnoreException(exception))
                {
                    _logger.LogDebug("Structured exception ignored: {ErrorCode}", exception.ErrorCode);
                    return CreateIgnoredResult(handlingId, exception);
                }

                await _handlingSemaphore.WaitAsync(cancellationToken);
                semaphoreAcquired = true;

                lock (_activeSessions)
                {
                    _activeSessions[handlingId] = session;
                }

                var result = await ExecuteHandlingPipelineAsync(session, cancellationToken);

                stopwatch.Stop();
                result.HandlingDuration = stopwatch.Elapsed;

                UpdateStatistics(result);

                _logger.LogInformation("Structured exception handling completed: {HandlingId}, Result: {Result}",
                    handlingId, result.WasHandled ? "Handled" : "Failed");

                return result;
            }
            catch (Exception handlingError)
            {
                _logger.LogError(handlingError, "Error during structured exception handling: {HandlingId}", handlingId);
                return CreateFallbackResult(handlingId, exception, handlingError);
            }
            finally
            {
                lock (_activeSessions)
                {
                    _activeSessions.Remove(handlingId);
                }

                if (semaphoreAcquired)
                    _handlingSemaphore.Release();

                stopwatch.Stop();
            }
        }

        public async Task<T> ExecuteWithHandlingAsync<T>(
            Func<Task<T>> operation,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            context ??= new ExceptionContext();
            context.OperationName = operation.Method.Name;

            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                var result = await HandleExceptionAsync(ex, context, cancellationToken);

                if (result.WasHandled && (result.Metadata == null || !result.Metadata.ContainsKey("ShouldRethrow")))
                    return default;

                throw new StructuredException(
                    result.FinalErrorCode ?? "ERR_SYS_0001",
                    result.UserMessage ?? _handlerConfig.DefaultUserMessage,
                    ex,
                    context?.AdditionalContext,
                    result.Severity,
                    context?.CorrelationId);
            }
        }

        public async Task ExecuteWithHandlingAsync(
            Func<Task> operation,
            ExceptionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            context ??= new ExceptionContext();
            context.OperationName = operation.Method.Name;

            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                var result = await HandleExceptionAsync(ex, context, cancellationToken);

                if (result.WasHandled && (result.Metadata == null || !result.Metadata.ContainsKey("ShouldRethrow")))
                    return;

                throw new StructuredException(
                    result.FinalErrorCode ?? "ERR_SYS_0001",
                    result.UserMessage ?? _handlerConfig.DefaultUserMessage,
                    ex,
                    context?.AdditionalContext,
                    result.Severity,
                    context?.CorrelationId);
            }
        }

        public void RegisterPolicy(ExceptionPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            if (string.IsNullOrEmpty(policy.PolicyId))
                policy.PolicyId = $"POL_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            _policies[policy.PolicyId] = policy;
            _logger.LogInformation("Exception policy registered: {PolicyId}", policy.PolicyId);
        }

        public void RegisterPolicyForType<TException>(ExceptionPolicy policy) where TException : Exception
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            if (string.IsNullOrEmpty(policy.PolicyId))
                policy.PolicyId = $"TYP_{typeof(TException).Name}_{Guid.NewGuid().ToString("N").Substring(0, 4)}";

            _typePolicies[typeof(TException)] = policy;

            _logger.LogInformation("Exception policy registered for type {ExceptionType}: {PolicyId}",
                typeof(TException).Name, policy.PolicyId);
        }

        public Task<ExceptionHandlingStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_statistics.Clone());

        public async Task<bool> TestHandlerAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Testing exception handler...");

                var testException = new InvalidOperationException("Test exception for handler validation");
                var context = new ExceptionContext
                {
                    OperationName = "TestHandler",
                    ComponentName = "ExceptionHandler",
                    Tags = new List<string> { "Test", "Validation" }
                };

                var result = await HandleExceptionAsync(testException, context, cancellationToken);
                if (!result.WasHandled)
                {
                    _logger.LogWarning("Test failed: Exception was not handled");
                    return false;
                }

                var structuredException = new StructuredException(
                    "ERR_SYS_0001",
                    "Test structured exception",
                    severity: SeverityLevel.Warning);

                var structuredResult = await HandleStructuredExceptionAsync(structuredException, context, cancellationToken);
                if (!structuredResult.WasHandled)
                {
                    _logger.LogWarning("Test failed: Structured exception was not handled");
                    return false;
                }

                _logger.LogInformation("Exception handler test passed");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception handler test failed");
                return false;
            }
        }

        #region Private Methods

        private ExceptionHandlingConfig LoadConfiguration()
        {
            return new ExceptionHandlingConfig
            {
                EnableGlobalHandling = _configuration.GetValue("ExceptionHandling:EnableGlobalHandling", true),
                EnableRecoveryStrategies = _configuration.GetValue("ExceptionHandling:EnableRecoveryStrategies", true),
                EnableErrorReporting = _configuration.GetValue("ExceptionHandling:EnableErrorReporting", true),
                EnableDetailedLogging = _configuration.GetValue("ExceptionHandling:EnableDetailedLogging", true),
                EnablePerformanceTracking = _configuration.GetValue("ExceptionHandling:EnablePerformanceTracking", true),
                MaxHandlingDepth = _configuration.GetValue("ExceptionHandling:MaxHandlingDepth", 5),
                DefaultTimeout = TimeSpan.FromSeconds(_configuration.GetValue("ExceptionHandling:DefaultTimeoutSeconds", 30)),
                IncludeSensitiveData = _configuration.GetValue("ExceptionHandling:IncludeSensitiveData", false),
                MaskSensitiveDataInLogs = _configuration.GetValue("ExceptionHandling:MaskSensitiveDataInLogs", true),
                DefaultUserMessage = _configuration.GetValue(
                    "ExceptionHandling:DefaultUserMessage",
                    "An unexpected error occurred. Please try again later."),
                Policies = LoadPoliciesFromConfig(),
                IgnoredExceptions = _configuration.GetSection("ExceptionHandling:IgnoredExceptions")
                    .Get<List<string>>() ?? new List<string>()
            };
        }

        private Dictionary<string, ExceptionPolicy> LoadPoliciesFromConfig()
        {
            var policies = new Dictionary<string, ExceptionPolicy>();

            var policyConfigs = _configuration.GetSection("ExceptionHandling:Policies").GetChildren();
            foreach (var policyConfig in policyConfigs)
            {
                var policy = new ExceptionPolicy
                {
                    PolicyId = policyConfig.Key,
                    ErrorCodes = policyConfig.GetSection("ErrorCodes").Get<List<string>>(),
                    ExceptionTypes = policyConfig.GetSection("ExceptionTypes").Get<List<string>>(),
                    MinimumSeverity = policyConfig.GetValue("MinimumSeverity", SeverityLevel.Error),
                    DefaultUserMessage = policyConfig.GetValue<string>("DefaultUserMessage"),
                    ShouldRethrow = policyConfig.GetValue("ShouldRethrow", false),
                    ShouldLog = policyConfig.GetValue("ShouldLog", true),
                    ShouldReport = policyConfig.GetValue("ShouldReport", true),
                    MaxRetryAttempts = policyConfig.GetValue("MaxRetryAttempts", 3),
                    RetryDelay = TimeSpan.FromMilliseconds(policyConfig.GetValue("RetryDelayMilliseconds", 100.0))
                };

                policies[policy.PolicyId] = policy;
            }

            return policies;
        }

        private void InitializeDefaultPolicies()
        {
            var systemPolicy = new ExceptionPolicy
            {
                PolicyId = "POL_SYSTEM_DEFAULT",
                ErrorCodes = new List<string> { "ERR_SYS_", "ERR_DB_", "ERR_NET_" },
                MinimumSeverity = SeverityLevel.Error,
                DefaultUserMessage = "A system error occurred. Our team has been notified.",
                ShouldRethrow = false,
                ShouldLog = true,
                ShouldReport = true,
                MaxRetryAttempts = 0,
                RecoveryStrategies = new List<RecoveryStrategy>
                {
                    RecoveryStrategy.Retry,
                    RecoveryStrategy.Fallback
                }
            };
            RegisterPolicy(systemPolicy);

            var businessPolicy = new ExceptionPolicy
            {
                PolicyId = "POL_BUSINESS_DEFAULT",
                ErrorCodes = new List<string> { "ERR_BUS_", "ERR_VAL_" },
                MinimumSeverity = SeverityLevel.Warning,
                DefaultUserMessage = "Please check your input and try again.",
                ShouldRethrow = false,
                ShouldLog = true,
                ShouldReport = false,
                MaxRetryAttempts = 0
            };
            RegisterPolicy(businessPolicy);

            var securityPolicy = new ExceptionPolicy
            {
                PolicyId = "POL_SECURITY_DEFAULT",
                ErrorCodes = new List<string> { "ERR_SEC_", "ERR_AUTH_" },
                MinimumSeverity = SeverityLevel.Critical,
                DefaultUserMessage = "Security error detected. Please contact support.",
                ShouldRethrow = true,
                ShouldLog = true,
                ShouldReport = true,
                MaxRetryAttempts = 0,
                RecoveryStrategies = new List<RecoveryStrategy>
                {
                    RecoveryStrategy.EmergencyShutdown
                }
            };
            RegisterPolicy(securityPolicy);

            RegisterPolicyForType<ArgumentNullException>(new ExceptionPolicy
            {
                PolicyId = "POL_TYP_ARGUMENTNULL",
                DefaultUserMessage = "Required parameter is missing.",
                ShouldRethrow = false,
                ShouldLog = true,
                ShouldReport = false
            });

            RegisterPolicyForType<InvalidOperationException>(new ExceptionPolicy
            {
                PolicyId = "POL_TYP_INVALIDOPERATION",
                DefaultUserMessage = "The operation cannot be performed in the current state.",
                ShouldRethrow = false,
                ShouldLog = true,
                ShouldReport = false
            });

            RegisterPolicyForType<TimeoutException>(new ExceptionPolicy
            {
                PolicyId = "POL_TYP_TIMEOUT",
                DefaultUserMessage = "The operation timed out. Please try again.",
                ShouldRethrow = false,
                ShouldLog = true,
                ShouldReport = true,
                RecoveryStrategies = new List<RecoveryStrategy>
                {
                    RecoveryStrategy.Retry,
                    RecoveryStrategy.Timeout
                }
            });

            RegisterPolicyForType<UnauthorizedAccessException>(new ExceptionPolicy
            {
                PolicyId = "POL_TYP_UNAUTHORIZED",
                DefaultUserMessage = "Access denied. Please check your permissions.",
                ShouldRethrow = true,
                ShouldLog = true,
                ShouldReport = true
            });
        }

        private ExceptionHandlingSession CreateHandlingSession(string handlingId, Exception exception, ExceptionContext context)
        {
            return new ExceptionHandlingSession
            {
                SessionId = handlingId,
                OriginalException = exception,
                Context = context ?? new ExceptionContext(),
                StartedAt = DateTime.UtcNow,
                Status = HandlingStatus.Pending,
                Actions = new List<ExceptionAction>(),
                Metadata = new Dictionary<string, object>()
            };
        }

        private bool ShouldIgnoreException(Exception exception)
        {
            if (!_handlerConfig.EnableGlobalHandling)
                return true;

            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;

            if (_handlerConfig.IgnoredExceptions != null &&
                _handlerConfig.IgnoredExceptions.Contains(exceptionType))
                return true;

            if (exception is StructuredException structuredEx &&
                structuredEx.Severity < SeverityLevel.Warning)
                return true;

            if (exception is OperationCanceledException)
                return true;

            return false;
        }

        private StructuredException ConvertToStructuredException(Exception exception, ExceptionContext context)
        {
            var errorCode = ExtractErrorCodeFromException(exception);
            var severity = DetermineSeverity(exception);

            string requestPath = null;
            if (context?.AdditionalContext != null &&
                context.AdditionalContext.TryGetValue("RequestPath", out var rp) &&
                rp != null)
            {
                requestPath = rp.ToString();
            }

            var structuredException = new StructuredException(
                errorCode,
                exception.Message,
                exception.InnerException,
                context?.AdditionalContext,
                severity,
                context?.CorrelationId,
                context?.UserId,
                context?.SessionId,
                requestPath);

            if (exception.Data != null && exception.Data.Count > 0)
            {
                foreach (var key in exception.Data.Keys)
                {
                    var k = key?.ToString();
                    if (!string.IsNullOrEmpty(k))
                        structuredException.AddContext(k, exception.Data[key]);
                }
            }

            structuredException.AddContext("StackTrace", exception.StackTrace);
            return structuredException;
        }

        private string ExtractErrorCodeFromException(Exception exception)
        {
            // switch expression (doğru sentaks)
            return exception switch
            {
                ArgumentNullException => "ERR_VAL_0001",
                ArgumentException => "ERR_VAL_0002",
                InvalidOperationException => "ERR_SYS_0002",
                TimeoutException => "ERR_SYS_0006",
                UnauthorizedAccessException => "ERR_AUTH_0002",
                System.IO.IOException => "ERR_FS_0001",
                System.Data.Common.DbException => "ERR_DB_0001",
                System.Net.Http.HttpRequestException => "ERR_NET_0001",
                System.Security.SecurityException => "ERR_SEC_0001",
                _ => "ERR_SYS_0001"
            };
        }

        private SeverityLevel DetermineSeverity(Exception exception)
        {
            return exception switch
            {
                UnauthorizedAccessException => SeverityLevel.Critical,
                System.Security.SecurityException => SeverityLevel.Critical,
                System.Data.Common.DbException => SeverityLevel.Error,
                System.IO.IOException => SeverityLevel.Error,
                TimeoutException => SeverityLevel.Warning,
                ArgumentException => SeverityLevel.Warning,
                InvalidOperationException => SeverityLevel.Warning,
                _ => SeverityLevel.Error
            };
        }

        private async Task<ExceptionHandlingResult> ExecuteHandlingPipelineAsync(
            ExceptionHandlingSession session,
            CancellationToken cancellationToken)
        {
            var structuredException = session.StructuredException;
            var context = session.Context;

            _logger.LogDebug("Executing handling pipeline for: {ErrorCode}", structuredException.ErrorCode);

            var result = new ExceptionHandlingResult
            {
                HandlingId = session.SessionId,
                OriginalErrorCode = structuredException.ErrorCode,
                FinalErrorCode = structuredException.ErrorCode,
                Severity = structuredException.Severity,
                WasHandled = false,
                WasRecovered = false,
                ActionsTaken = new List<ExceptionAction>(),
                Metadata = new Dictionary<string, object>(),
                HandledAt = DateTime.UtcNow
            };

            var policy = FindApplicablePolicy(structuredException, context);
            session.Metadata["PolicyId"] = policy?.PolicyId ?? "NO_POLICY";

            if (policy?.PreHandlingActions != null && policy.PreHandlingActions.Any())
                await ExecutePreHandlingActionsAsync(policy, session, result, cancellationToken);

            result.UserMessage = DetermineUserMessage(structuredException, policy, context);
            result.TechnicalMessage = structuredException.Message;

            if (policy == null || policy.ShouldLog)
                await LogExceptionAsync(structuredException, context, result, cancellationToken);

            if (_handlerConfig.EnableRecoveryStrategies &&
                policy?.RecoveryStrategies != null &&
                policy.RecoveryStrategies.Any())
            {
                var recoveryResult = await AttemptRecoveryAsync(structuredException, policy.RecoveryStrategies, context, cancellationToken);
                result.WasRecovered = recoveryResult.Success;
                result.UsedRecoveryStrategy = recoveryResult.Strategy;
                result.Metadata["RecoveryResult"] = recoveryResult;

                if (recoveryResult.Success)
                    result.FinalErrorCode = $"{structuredException.ErrorCode}_RECOVERED";
            }

            if (_handlerConfig.EnableErrorReporting && (policy == null || policy.ShouldReport))
                await ReportErrorAsync(structuredException, context, result, cancellationToken);

            if (policy?.PostHandlingActions != null && policy.PostHandlingActions.Any())
                await ExecutePostHandlingActionsAsync(policy, session, result, cancellationToken);

            result.WasHandled = DetermineIfHandled(structuredException, policy, result.WasRecovered);

            if (policy != null && policy.ShouldRethrow)
                result.Metadata["ShouldRethrow"] = true;

            session.Status = result.WasHandled ? HandlingStatus.Handled : HandlingStatus.Failed;
            session.CompletedAt = DateTime.UtcNow;
            session.Result = result;

            return result;
        }

        private ExceptionPolicy FindApplicablePolicy(StructuredException exception, ExceptionContext context)
        {
            if (context?.BypassPolicies == true)
                return null;

            // type policy
            var exceptionType = exception.GetType();
            while (exceptionType != null && exceptionType != typeof(object))
            {
                if (_typePolicies.TryGetValue(exceptionType, out var typePolicy))
                    return typePolicy;

                exceptionType = exceptionType.BaseType;
            }

            // registered policies
            foreach (var policy in _policies.Values)
            {
                if (policy.ErrorCodes != null)
                {
                    foreach (var pattern in policy.ErrorCodes)
                    {
                        if (!string.IsNullOrEmpty(pattern) &&
                            exception.ErrorCode.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) &&
                            exception.Severity >= policy.MinimumSeverity)
                        {
                            return policy;
                        }
                    }
                }

                if (policy.ExceptionTypes != null)
                {
                    var exceptionTypeName = exception.GetType().FullName ?? exception.GetType().Name;
                    foreach (var pattern in policy.ExceptionTypes)
                    {
                        if (!string.IsNullOrEmpty(pattern) &&
                            (exceptionTypeName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                             exceptionTypeName.Contains(pattern, StringComparison.OrdinalIgnoreCase)) &&
                            exception.Severity >= policy.MinimumSeverity)
                        {
                            return policy;
                        }
                    }
                }
            }

            // config policies
            if (_handlerConfig.Policies != null)
            {
                foreach (var policy in _handlerConfig.Policies.Values)
                {
                    if (policy.ErrorCodes != null && policy.ErrorCodes.Any())
                    {
                        if (policy.ErrorCodes.Any(p => !string.IsNullOrEmpty(p) &&
                                                       exception.ErrorCode.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        {
                            return policy;
                        }
                    }
                }
            }

            return null;
        }

        private string DetermineUserMessage(StructuredException exception, ExceptionPolicy policy, ExceptionContext context)
        {
            if (context?.AdditionalContext?.ContainsKey("UserMessage") == true)
                return context.AdditionalContext["UserMessage"]?.ToString();

            if (!string.IsNullOrEmpty(policy?.DefaultUserMessage))
                return policy.DefaultUserMessage;

            try
            {
                var resourceMessage = GetMessageFromErrorCode(exception.ErrorCode);
                if (!string.IsNullOrEmpty(resourceMessage))
                    return FormatErrorMessage(resourceMessage, exception.Context);
            }
            catch
            {
                // ignore
            }

            return _handlerConfig.DefaultUserMessage;
        }

        private string GetMessageFromErrorCode(string errorCode)
        {
            return errorCode switch
            {
                "ERR_SYS_0001" => "An unexpected system error occurred.",
                "ERR_AUTH_0001" => "Authentication failed. Invalid credentials.",
                "ERR_VAL_0001" => "Required field '{FieldName}' is missing.",
                "ERR_DB_0001" => "Database operation failed.",
                "ERR_NET_0001" => "Network connection failed.",
                "ERR_SEC_0001" => "Security violation detected.",
                _ => null
            };
        }

        private string FormatErrorMessage(string template, Dictionary<string, object> context)
        {
            if (string.IsNullOrEmpty(template) || context == null)
                return template;

            var result = template;
            foreach (var kvp in context)
                result = result.Replace($"{{{kvp.Key}}}", kvp.Value?.ToString() ?? string.Empty);

            return result;
        }

        private async Task ExecutePreHandlingActionsAsync(
            ExceptionPolicy policy,
            ExceptionHandlingSession session,
            ExceptionHandlingResult result,
            CancellationToken cancellationToken)
        {
            foreach (var actionTemplate in policy.PreHandlingActions)
            {
                var action = CreateActionFromTemplate(actionTemplate, session);
                action.Status = ActionStatus.Executing;
                action.ExecutedAt = DateTime.UtcNow;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await ExecuteActionAsync(action, session, cancellationToken);
                    action.Status = ActionStatus.Completed;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pre-handling action failed: {ActionId}", action.ActionId);
                    action.Status = ActionStatus.Failed;
                    action.ErrorMessage = ex.Message;
                }
                finally
                {
                    sw.Stop();
                    action.Duration = sw.Elapsed;
                    result.ActionsTaken.Add(action);
                    session.Actions.Add(action);
                }
            }
        }

        private async Task ExecutePostHandlingActionsAsync(
            ExceptionPolicy policy,
            ExceptionHandlingSession session,
            ExceptionHandlingResult result,
            CancellationToken cancellationToken)
        {
            foreach (var actionTemplate in policy.PostHandlingActions)
            {
                var action = CreateActionFromTemplate(actionTemplate, session);
                action.Status = ActionStatus.Executing;
                action.ExecutedAt = DateTime.UtcNow;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await ExecuteActionAsync(action, session, cancellationToken);
                    action.Status = ActionStatus.Completed;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-handling action failed: {ActionId}", action.ActionId);
                    action.Status = ActionStatus.Failed;
                    action.ErrorMessage = ex.Message;
                }
                finally
                {
                    sw.Stop();
                    action.Duration = sw.Elapsed;
                    result.ActionsTaken.Add(action);
                    session.Actions.Add(action);
                }
            }
        }

        private ExceptionAction CreateActionFromTemplate(ExceptionAction template, ExceptionHandlingSession session)
        {
            return new ExceptionAction
            {
                ActionId = $"ACT_{Guid.NewGuid().ToString("N").Substring(0, 6)}",
                Name = template?.Name,
                Description = template?.Description,
                Type = template?.Type ?? ActionType.Logging,
                Status = ActionStatus.Pending
            };
        }

        private async Task ExecuteActionAsync(ExceptionAction action, ExceptionHandlingSession session, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Executing action: {ActionName} ({ActionType})", action.Name, action.Type);

            switch (action.Type)
            {
                case ActionType.Logging:
                    await Task.CompletedTask;
                    break;

                case ActionType.Notification:
                    await Task.CompletedTask;
                    break;

                case ActionType.Cleanup:
                    await Task.CompletedTask;
                    break;

                case ActionType.Escalation:
                    await Task.CompletedTask;
                    break;

                default:
                    _logger.LogWarning("Unknown action type: {ActionType}", action.Type);
                    break;
            }
        }

        private async Task LogExceptionAsync(
            StructuredException exception,
            ExceptionContext context,
            ExceptionHandlingResult result,
            CancellationToken cancellationToken)
        {
            var logAction = new ExceptionAction
            {
                ActionId = $"LOG_{Guid.NewGuid().ToString("N").Substring(0, 4)}",
                Name = "ExceptionLogging",
                Description = "Log exception details",
                Type = ActionType.Logging,
                Status = ActionStatus.Executing,
                ExecutedAt = DateTime.UtcNow
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LogExceptionToLogger(exception, context);
                if (_handlerConfig.EnableDetailedLogging)
                    LogDetailedException(exception, context);

                logAction.Status = ActionStatus.Completed;
                result.ActionsTaken.Add(logAction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log exception: {ErrorCode}", exception.ErrorCode);
                logAction.Status = ActionStatus.Failed;
                logAction.ErrorMessage = ex.Message;
                result.ActionsTaken.Add(logAction);
            }
            finally
            {
                sw.Stop();
                logAction.Duration = sw.Elapsed;
            }

            await Task.CompletedTask;
        }

        private void LogExceptionToLogger(StructuredException exception, ExceptionContext context)
        {
            var logLevel = ConvertSeverityToLogLevel(exception.Severity);
            var message = $"Exception [{exception.ErrorCode}]: {exception.Message}";

            if (exception.Severity >= SeverityLevel.Error)
                _logger.Log(logLevel, exception, message);
            else
                _logger.Log(logLevel, message);

            if (_handlerConfig.EnableDetailedLogging)
            {
                var structuredData = new
                {
                    ErrorCode = exception.ErrorCode,
                    ErrorCategory = exception.ErrorCategory,
                    CorrelationId = exception.CorrelationId,
                    UserId = exception.UserId,
                    SessionId = exception.SessionId,
                    RequestPath = exception.RequestPath,
                    Context = MaskSensitiveData(exception.Context),
                    Timestamp = exception.Timestamp,
                    Component = context?.ComponentName,
                    Operation = context?.OperationName
                };

                _logger.Log(logLevel, "Exception details: {@ExceptionDetails}", structuredData);
            }
        }

        private void LogDetailedException(StructuredException exception, ExceptionContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== EXCEPTION DETAILS [{exception.ErrorCode}] ===");
            sb.AppendLine($"Message: {exception.Message}");
            sb.AppendLine($"Category: {exception.ErrorCategory}");
            sb.AppendLine($"Severity: {exception.Severity}");
            sb.AppendLine($"CorrelationId: {exception.CorrelationId}");
            sb.AppendLine($"Timestamp: {exception.Timestamp:O}");

            if (!string.IsNullOrEmpty(exception.UserId))
                sb.AppendLine($"UserId: {exception.UserId}");

            if (!string.IsNullOrEmpty(context?.ComponentName))
                sb.AppendLine($"Component: {context.ComponentName}");

            if (!string.IsNullOrEmpty(context?.OperationName))
                sb.AppendLine($"Operation: {context.OperationName}");

            sb.AppendLine("=== CONTEXT ===");
            foreach (var kvp in MaskSensitiveData(exception.Context) ?? new Dictionary<string, object>())
                sb.AppendLine($"{kvp.Key}: {kvp.Value}");

            if (exception.InnerException != null)
            {
                sb.AppendLine("=== INNER EXCEPTION ===");
                sb.AppendLine($"Type: {exception.InnerException.GetType().Name}");
                sb.AppendLine($"Message: {exception.InnerException.Message}");
            }

            _logger.LogDebug(sb.ToString());
        }

        private Dictionary<string, object> MaskSensitiveData(Dictionary<string, object> data)
        {
            if (!_handlerConfig.MaskSensitiveDataInLogs || data == null)
                return data;

            var masked = new Dictionary<string, object>(data);
            var sensitiveKeys = new[] { "password", "token", "secret", "key", "credential", "ssn", "creditcard" };

            foreach (var key in masked.Keys.ToList())
            {
                var keyLower = key.ToLowerInvariant();
                if (sensitiveKeys.Any(sk => keyLower.Contains(sk)))
                    masked[key] = "***MASKED***";
            }

            return masked;
        }

        private LogLevel ConvertSeverityToLogLevel(SeverityLevel severity)
        {
            return severity switch
            {
                SeverityLevel.Trace => LogLevel.Trace,
                SeverityLevel.Debug => LogLevel.Debug,
                SeverityLevel.Information => LogLevel.Information,
                SeverityLevel.Warning => LogLevel.Warning,
                SeverityLevel.Error => LogLevel.Error,
                SeverityLevel.Critical => LogLevel.Critical,
                SeverityLevel.Fatal => LogLevel.Critical,
                _ => LogLevel.Error
            };
        }

        private async Task<RecoveryResult> AttemptRecoveryAsync(
            StructuredException exception,
            List<RecoveryStrategy> strategies,
            ExceptionContext context,
            CancellationToken cancellationToken)
        {
            if (_recoveryEngine == null)
                return new RecoveryResult { Success = false, Strategy = RecoveryStrategy.None };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var recoveryResult = await _recoveryEngine.AttemptRecoveryAsync(exception, strategies, context, cancellationToken);
                sw.Stop();
                recoveryResult.Duration = sw.Elapsed;
                return recoveryResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recovery attempt failed: {ErrorCode}", exception.ErrorCode);
                sw.Stop();

                return new RecoveryResult
                {
                    Success = false,
                    Strategy = RecoveryStrategy.None,
                    ErrorMessage = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        private async Task ReportErrorAsync(
            StructuredException exception,
            ExceptionContext context,
            ExceptionHandlingResult result,
            CancellationToken cancellationToken)
        {
            if (_errorReporter == null)
                return;

            var reportAction = new ExceptionAction
            {
                ActionId = $"REP_{Guid.NewGuid().ToString("N").Substring(0, 4)}",
                Name = "ErrorReporting",
                Description = "Report error to monitoring systems",
                Type = ActionType.Notification,
                Status = ActionStatus.Executing,
                ExecutedAt = DateTime.UtcNow
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _errorReporter.ReportErrorAsync(exception, context, cancellationToken);
                reportAction.Status = ActionStatus.Completed;
                result.ActionsTaken.Add(reportAction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting failed: {ErrorCode}", exception.ErrorCode);
                reportAction.Status = ActionStatus.Failed;
                reportAction.ErrorMessage = ex.Message;
                result.ActionsTaken.Add(reportAction);
            }
            finally
            {
                sw.Stop();
                reportAction.Duration = sw.Elapsed;
            }
        }

        private bool DetermineIfHandled(StructuredException exception, ExceptionPolicy policy, bool wasRecovered)
        {
            if (wasRecovered) return true;
            if (policy == null) return true;
            return !policy.ShouldRethrow;
        }

        private ExceptionHandlingResult CreateIgnoredResult(string handlingId, Exception exception)
        {
            var se = exception as StructuredException;

            return new ExceptionHandlingResult
            {
                HandlingId = handlingId,
                OriginalErrorCode = se?.ErrorCode ?? "UNKNOWN",
                FinalErrorCode = se?.ErrorCode ?? "UNKNOWN",
                UserMessage = "Exception ignored",
                TechnicalMessage = exception.Message,
                Severity = se?.Severity ?? SeverityLevel.Debug,
                WasHandled = true,
                WasRecovered = false,
                HandlingDuration = TimeSpan.Zero,
                ActionsTaken = new List<ExceptionAction>(),
                Metadata = new Dictionary<string, object> { { "Ignored", true } },
                HandledAt = DateTime.UtcNow
            };
        }

        private ExceptionHandlingResult CreateFallbackResult(string handlingId, Exception originalException, Exception handlingError)
        {
            var se = originalException as StructuredException;

            return new ExceptionHandlingResult
            {
                HandlingId = handlingId,
                OriginalErrorCode = se?.ErrorCode ?? "UNKNOWN",
                FinalErrorCode = "ERR_SYS_0001",
                UserMessage = _handlerConfig.DefaultUserMessage,
                TechnicalMessage = $"Original: {originalException.Message}, Handler: {handlingError.Message}",
                Severity = SeverityLevel.Critical,
                WasHandled = false,
                WasRecovered = false,
                HandlingDuration = TimeSpan.Zero,
                ActionsTaken = new List<ExceptionAction>(),
                Metadata = new Dictionary<string, object>
                {
                    { "HandlerFailed", true },
                    { "HandlerError", handlingError.Message }
                },
                HandledAt = DateTime.UtcNow
            };
        }

        private void UpdateStatistics(ExceptionHandlingResult result)
        {
            lock (_statistics)
            {
                _statistics.TotalHandled++;
                _statistics.LastHandledAt = DateTime.UtcNow;

                if (result.WasHandled) _statistics.SuccessfullyHandled++;
                else _statistics.FailedHandlings++;

                if (result.WasRecovered) _statistics.RecoveredExceptions++;

                if (!_statistics.HandlingsBySeverity.ContainsKey(result.Severity))
                    _statistics.HandlingsBySeverity[result.Severity] = 0;
                _statistics.HandlingsBySeverity[result.Severity]++;

                var category = (!string.IsNullOrEmpty(result.OriginalErrorCode) && result.OriginalErrorCode.Length >= 7)
                    ? result.OriginalErrorCode.Substring(4, 3)
                    : "UNK";

                if (!_statistics.HandlingsByCategory.ContainsKey(category))
                    _statistics.HandlingsByCategory[category] = 0;
                _statistics.HandlingsByCategory[category]++;

                if (_statistics.TotalHandled > 0)
                {
                    var totalTicks = _statistics.AverageHandlingTime.Ticks * (_statistics.TotalHandled - 1);
                    totalTicks += result.HandlingDuration.Ticks;
                    _statistics.AverageHandlingTime = TimeSpan.FromTicks(totalTicks / _statistics.TotalHandled);
                }
            }
        }

        #endregion

        #region Supporting Types

        public class ExceptionHandlingSession
        {
            public string SessionId { get; set; }
            public Exception OriginalException { get; set; }
            public StructuredException StructuredException { get; set; }
            public ExceptionContext Context { get; set; }
            public HandlingStatus Status { get; set; }
            public DateTime StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public List<ExceptionAction> Actions { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
            public ExceptionHandlingResult Result { get; set; }
        }

        public enum HandlingStatus
        {
            Pending,
            Processing,
            Handled,
            Failed,
            Cancelled,
            Ignored
        }

        public class RecoveryResult
        {
            public bool Success { get; set; }
            public RecoveryStrategy Strategy { get; set; }
            public string ErrorMessage { get; set; }
            public TimeSpan Duration { get; set; }
            public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
        }

        public class ExceptionHandlingStatistics
        {
            public int TotalHandled { get; set; }
            public int SuccessfullyHandled { get; set; }
            public int FailedHandlings { get; set; }
            public int RecoveredExceptions { get; set; }
            public DateTime LastHandledAt { get; set; }
            public TimeSpan AverageHandlingTime { get; set; }
            public Dictionary<SeverityLevel, int> HandlingsBySeverity { get; set; } = new Dictionary<SeverityLevel, int>();
            public Dictionary<string, int> HandlingsByCategory { get; set; } = new Dictionary<string, int>();

            public ExceptionHandlingStatistics Clone()
            {
                return new ExceptionHandlingStatistics
                {
                    TotalHandled = TotalHandled,
                    SuccessfullyHandled = SuccessfullyHandled,
                    FailedHandlings = FailedHandlings,
                    RecoveredExceptions = RecoveredExceptions,
                    LastHandledAt = LastHandledAt,
                    AverageHandlingTime = AverageHandlingTime,
                    HandlingsBySeverity = new Dictionary<SeverityLevel, int>(HandlingsBySeverity),
                    HandlingsByCategory = new Dictionary<string, int>(HandlingsByCategory)
                };
            }
        }

        #endregion
    }

    // Bu interface/servisler projende başka yerde var diye varsaydım.
    // Yoksa "type not found" çıkar, o zaman onları da tek dosya halinde stublayıp bağlarız.
    public interface IRecoveryEngine
    {
        Task<ExceptionHandler.RecoveryResult> AttemptRecoveryAsync(
            StructuredException exception,
            List<RecoveryStrategy> strategies,
            ExceptionContext context,
            CancellationToken cancellationToken);
    }

    public interface IErrorReporter
    {
        Task ReportErrorAsync(
            StructuredException exception,
            ExceptionContext context,
            CancellationToken cancellationToken);
    }

    public interface IDiagnosticTool
    {
        // placeholder
    }
}
