using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.AI.ReinforcementLearning;
using NEDA.API.DTOs;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.ExceptionHandling.ErrorReporting;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.Core.ExceptionHandling.GlobalExceptionHandler
{
    /// <summary>
    /// Global Exception Handling Middleware;
    /// Tüm HTTP request'ler için merkezi hata yönetimi sağlar;
    /// </summary>
    public class ErrorHandlerMiddleware : IMiddleware
    {
        private readonly ILogger<ErrorHandlerMiddleware> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly ISecurityManager _securityManager;
        private readonly IAppConfig _appConfig;
        private readonly IAuditLogger _auditLogger;
        private readonly RequestTrackingService _requestTracker;

        private readonly JsonSerializerOptions _jsonOptions;
        private readonly bool _includeDetailsInResponse;
        private readonly List<string> _sensitiveHeaders = new() { "Authorization", "Cookie", "X-API-Key" };

        public ErrorHandlerMiddleware(
            ILogger<ErrorHandlerMiddleware> logger,
            IErrorReporter errorReporter,
            ISecurityManager securityManager,
            IOptions<AppConfig> appConfigOptions,
            IAuditLogger auditLogger,
            RequestTrackingService requestTracker)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _appConfig = appConfigOptions?.Value ?? throw new ArgumentNullException(nameof(appConfigOptions));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _requestTracker = requestTracker ?? throw new ArgumentNullException(nameof(requestTracker));

            _includeDetailsInResponse = _appConfig.Environment == Environments.Development ||
                                       _appConfig.Environment == Environments.Staging;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = _includeDetailsInResponse
            };
        }

        public async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            var stopwatch = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString();
            var correlationId = GetOrCreateCorrelationId(context);

            context.Items["RequestId"] = requestId;
            context.Items["CorrelationId"] = correlationId;
            context.Items["RequestStartTime"] = DateTime.UtcNow;

            LogRequest(context, requestId, correlationId);

            try
            {
                var trackingInfo = await _requestTracker.StartTrackingAsync(context, requestId);
                context.Items["TrackingInfo"] = trackingInfo;

                await next(context);

                LogResponse(context, requestId, stopwatch.Elapsed);

                await _requestTracker.CompleteTrackingAsync(trackingInfo, context.Response.StatusCode);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();

                await HandleExceptionAsync(context, exception, requestId, correlationId, stopwatch.Elapsed);

                await LogErrorToAuditAsync(context, exception, requestId);
            }
            finally
            {
                if (context.Items.TryGetValue("TrackingInfo", out var trackingInfoObj) &&
                    trackingInfoObj is RequestTrackingInfo trackingInfo)
                {
                    await _requestTracker.CleanupTrackingAsync(trackingInfo);
                }

                CleanupRequestResources(context);
            }
        }

        private async Task HandleExceptionAsync(
            HttpContext context,
            Exception exception,
            string requestId,
            string correlationId,
            TimeSpan elapsedTime)
        {
            _logger.LogError(exception,
                "Unhandled exception in request {RequestId} ({CorrelationId}): {Message}",
                requestId, correlationId, exception.Message);

            var errorContext = CreateErrorContext(context, exception, requestId, correlationId);

            var errorReport = await CreateErrorReportAsync(exception, errorContext, elapsedTime);

            await WriteErrorResponseAsync(context, exception, errorReport, elapsedTime);

            if (IsCriticalError(exception, errorReport.ErrorCode))
            {
                await SendCriticalAlertAsync(errorReport, context);
            }

            UpdateErrorMetrics(errorReport.ErrorCode, elapsedTime);
        }

        private ErrorContext CreateErrorContext(
            HttpContext context,
            Exception exception,
            string requestId,
            string correlationId)
        {
            var errorContext = new ErrorContext
            {
                Component = "API",
                Operation = $"{context.Request.Method} {context.Request.Path}",
                UserId = GetUserId(context),
                SessionId = GetSessionId(context),
                CorrelationId = correlationId,
                RequestPath = context.Request.Path,
                HttpMethod = context.Request.Method,
                Headers = SanitizeHeaders(context.Request.Headers),
                AdditionalData = CollectAdditionalContextData(context, exception, requestId)
            };

            return errorContext;
        }

        private async Task<ErrorReport> CreateErrorReportAsync(
            Exception exception,
            ErrorContext errorContext,
            TimeSpan elapsedTime)
        {
            try
            {
                var errorReport = await _errorReporter.ReportErrorAsync(exception, errorContext);

                errorReport.AdditionalData["RequestDuration"] = elapsedTime.TotalMilliseconds;
                errorReport.AdditionalData["IsApiError"] = true;

                return errorReport;
            }
            catch (Exception reportingEx)
            {
                _logger.LogCritical(reportingEx,
                    "Failed to report error: {Message}. Original error: {OriginalError}",
                    reportingEx.Message, exception.Message);

                return CreateFallbackErrorReport(exception, errorContext, reportingEx);
            }
        }

        private async Task WriteErrorResponseAsync(
            HttpContext context,
            Exception exception,
            ErrorReport errorReport,
            TimeSpan elapsedTime)
        {
            // Not: HttpResponse.Clear() bazı sürümlerde yoksa burada hata alabilirsin.
            // O durumda alternatif olarak Body.SetLength(0) + headers temizleme gerekir.
            context.Response.Clear();

            context.Response.ContentType = "application/json";

            var (statusCode, errorResponse) = CreateErrorResponse(exception, errorReport, elapsedTime);
            context.Response.StatusCode = (int)statusCode;

            AddCorsHeaders(context);
            AddSecurityHeaders(context);

            var jsonResponse = JsonSerializer.Serialize(errorResponse, _jsonOptions);
            await context.Response.WriteAsync(jsonResponse);

            LogErrorResponse(context, statusCode, errorReport.ReportId, elapsedTime);
        }

        private (HttpStatusCode statusCode, ErrorResponseDTO response) CreateErrorResponse(
            Exception exception,
            ErrorReport errorReport,
            TimeSpan elapsedTime)
        {
            var errorCode = errorReport.ErrorCode;
            var severity = ErrorCodes.GetErrorSeverity(errorCode);

            var statusCode = DetermineHttpStatusCode(exception, errorCode);

            var errorResponse = new ErrorResponseDTO
            {
                ErrorCode = errorCode,
                Message = GetUserFriendlyMessage(exception, errorCode),
                ReportId = errorReport.ReportId,
                Timestamp = DateTime.UtcNow,
                Path = errorReport.Context.RequestPath,
                Method = errorReport.Context.HttpMethod
            };

            if (_includeDetailsInResponse)
            {
                errorResponse.Details = new ErrorDetailsDTO
                {
                    ExceptionType = exception.GetType().Name,
                    ExceptionMessage = exception.Message,
                    StackTrace = GetSanitizedStackTrace(exception),
                    InnerException = exception.InnerException?.Message,
                    ErrorCategory = ErrorCodes.GetErrorCategory(errorCode).ToString(),
                    Severity = severity.ToString(),
                    Resolution = ErrorCodes.GetErrorResolution(errorCode),
                    RequestDuration = elapsedTime.TotalMilliseconds
                };

                if (exception is ValidationException validationEx)
                {
                    errorResponse.Details.ValidationErrors = validationEx.Errors;
                }
            }

            errorResponse.Metadata = new Dictionary<string, object>
            {
                ["Severity"] = severity.ToString(),
                ["Category"] = ErrorCodes.GetErrorCategory(errorCode).ToString(),
                ["Environment"] = _appConfig.Environment,
                ["Version"] = _appConfig.Version,
                ["CorrelationId"] = errorReport.Context.CorrelationId
            };

            return (statusCode, errorResponse);
        }

        private async Task SendCriticalAlertAsync(ErrorReport errorReport, HttpContext context)
        {
            try
            {
                var recipients = GetAlertRecipientsForError(errorReport.ErrorCode, context);

                foreach (var recipient in recipients)
                {
                    await _errorReporter.SendCriticalAlertAsync(errorReport, recipient);
                }
            }
            catch (Exception alertEx)
            {
                _logger.LogError(alertEx, "Failed to send critical alert for error report: {ReportId}",
                    errorReport.ReportId);
            }
        }

        #region Helper Methods;

        private string GetOrCreateCorrelationId(HttpContext context)
        {
            if (context.Request.Headers.TryGetValue("X-Correlation-ID", out var correlationId) &&
                !string.IsNullOrEmpty(correlationId))
            {
                return correlationId!;
            }

            return Guid.NewGuid().ToString();
        }

        private string GetUserId(HttpContext context)
        {
            var userIdClaim = context.User?.FindFirst("sub") ??
                            context.User?.FindFirst("userId") ??
                            context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);

            return userIdClaim?.Value ?? "Anonymous";
        }

        private string GetSessionId(HttpContext context)
        {
            var sessionId = context.Session?.Id ??
                          context.Request.Cookies["SessionId"] ??
                          context.Request.Headers["X-Session-ID"].ToString();

            return !string.IsNullOrEmpty(sessionId) ? sessionId : Guid.NewGuid().ToString();
        }

        private Dictionary<string, string> SanitizeHeaders(IHeaderDictionary headers)
        {
            var sanitized = new Dictionary<string, string>();

            foreach (var header in headers)
            {
                if (_sensitiveHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    sanitized[header.Key] = "[REDACTED]";
                }
                else
                {
                    sanitized[header.Key] = header.Value.ToString();
                }
            }

            return sanitized;
        }

        private Dictionary<string, object> CollectAdditionalContextData(
            HttpContext context,
            Exception exception,
            string requestId)
        {
            var data = new Dictionary<string, object>
            {
                ["RequestId"] = requestId,
                ["ClientIp"] = GetClientIpAddress(context),
                ["UserAgent"] = context.Request.Headers.UserAgent.ToString(),
                ["QueryString"] = context.Request.QueryString.ToString(),
                ["ContentType"] = context.Request.ContentType ?? string.Empty,
                ["ContentLength"] = context.Request.ContentLength ?? 0,
                ["IsHttps"] = context.Request.IsHttps,
                ["Host"] = context.Request.Host.ToString(),
                ["Scheme"] = context.Request.Scheme
            };

            if (context.Request.Query.Any())
            {
                data["QueryParameters"] = context.Request.Query
                    .ToDictionary(q => q.Key, q => q.Value.ToString());
            }

            var routeData = context.GetRouteData();
            if (routeData?.Values != null)
            {
                data["RouteData"] = routeData.Values;
            }

            if (exception.Data?.Count > 0)
            {
                data["ExceptionData"] = exception.Data
                    .Cast<System.Collections.DictionaryEntry>()
                    .ToDictionary(
                        e => e.Key?.ToString() ?? string.Empty,
                        e => e.Value?.ToString() ?? string.Empty);
            }

            return data;
        }

        private string GetClientIpAddress(HttpContext context)
        {
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
            if (!string.IsNullOrEmpty(forwardedFor))
            {
                var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
                return ips.FirstOrDefault()?.Trim() ?? context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
            }

            return context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        }

        private ErrorReport CreateFallbackErrorReport(
            Exception originalException,
            ErrorContext errorContext,
            Exception reportingException)
        {
            return new ErrorReport
            {
                ReportId = Guid.NewGuid().ToString(),
                ErrorCode = ErrorCodes.System.RUNTIME_EXCEPTION,
                ExceptionType = originalException.GetType().FullName ?? "Unknown",
                ExceptionMessage = $"Original: {originalException.Message}. Reporting failed: {reportingException.Message}",
                StackTrace = originalException.StackTrace ?? string.Empty,
                Severity = ErrorCodes.ErrorSeverity.Critical,
                Timestamp = DateTime.UtcNow,
                Context = errorContext,
                UserId = errorContext.UserId,
                SessionId = errorContext.SessionId,
                Component = errorContext.Component,
                Operation = errorContext.Operation,
                Environment = _appConfig.Environment,
                Version = _appConfig.Version,
                Status = ReportStatus.New,
                AdditionalData = new Dictionary<string, object>
                {
                    ["IsFallbackReport"] = true,
                    ["ReportingError"] = reportingException.Message
                }
            };
        }

        private HttpStatusCode DetermineHttpStatusCode(Exception exception, int errorCode)
        {
            return exception switch
            {
                ValidationException => HttpStatusCode.BadRequest,
                AuthenticationException => HttpStatusCode.Unauthorized,
                AuthorizationException => HttpStatusCode.Forbidden,
                NotFoundException => HttpStatusCode.NotFound,
                ConflictException => HttpStatusCode.Conflict,
                RateLimitException => HttpStatusCode.TooManyRequests,
                ServiceUnavailableException => HttpStatusCode.ServiceUnavailable,
                TimeoutException => HttpStatusCode.RequestTimeout,

                NedaException nedaEx => ErrorCodes.HttpStatusMappings
                    .GetHttpStatusFromErrorCode(nedaEx.ErrorCode),

                _ => ErrorCodes.HttpStatusMappings.GetHttpStatusFromErrorCode(errorCode)
            };
        }

        private string GetUserFriendlyMessage(Exception exception, int errorCode)
        {
            if (exception is ValidationException validationEx && validationEx.Errors.Any())
            {
                return "One or more validation errors occurred.";
            }

            if (exception is AuthenticationException)
            {
                return "Authentication failed. Please check your credentials.";
            }

            if (exception is AuthorizationException)
            {
                return "You do not have permission to perform this action.";
            }

            if (exception is NotFoundException)
            {
                return "The requested resource was not found.";
            }

            var errorMessage = ErrorCodes.GetErrorDescription(errorCode);

            if (_appConfig.Environment == Environments.Production &&
                !IsUserFriendlyError(errorCode))
            {
                return "An unexpected error occurred. Please try again later.";
            }

            return errorMessage;
        }

        private string GetSanitizedStackTrace(Exception exception)
        {
            if (string.IsNullOrEmpty(exception.StackTrace))
                return string.Empty;

            if (_appConfig.Environment == Environments.Production)
            {
                var lines = exception.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return string.Join('\n', lines.Take(5));
            }

            return exception.StackTrace;
        }

        private bool IsUserFriendlyError(int errorCode)
        {
            var userFriendlyCategories = new[]
            {
                ErrorCodes.ErrorCategory.Validation,
                ErrorCodes.ErrorCategory.Security,
                ErrorCodes.ErrorCategory.Business
            };

            var category = ErrorCodes.GetErrorCategory(errorCode);
            return userFriendlyCategories.Contains(category);
        }

        private bool IsCriticalError(Exception exception, int errorCode)
        {
            var severity = ErrorCodes.GetErrorSeverity(errorCode);

            if (severity >= ErrorCodes.ErrorSeverity.Critical)
                return true;

            return exception is OutOfMemoryException ||
                   exception is StackOverflowException ||
                   exception is ThreadAbortException;
        }

        private List<AlertRecipient> GetAlertRecipientsForError(int errorCode, HttpContext context)
        {
            var recipients = new List<AlertRecipient>();
            var severity = ErrorCodes.GetErrorSeverity(errorCode);
            var category = ErrorCodes.GetErrorCategory(errorCode);

            recipients.Add(new AlertRecipient
            {
                Name = "DevOps Team",
                ContactInfo = _appConfig.ErrorReporting?.DevOpsEmail ?? "devops@neda.ai",
                Channel = AlertChannel.Email,
                Severities = new List<ErrorCodes.ErrorSeverity>
                {
                    ErrorCodes.ErrorSeverity.Critical,
                    ErrorCodes.ErrorSeverity.Fatal
                }
            });

            if (category == ErrorCodes.ErrorCategory.Security &&
                severity >= ErrorCodes.ErrorSeverity.Error)
            {
                recipients.Add(new AlertRecipient
                {
                    Name = "Security Team",
                    ContactInfo = _appConfig.ErrorReporting?.SecurityEmail ?? "security@neda.ai",
                    Channel = AlertChannel.Slack,
                    Severities = new List<ErrorCodes.ErrorSeverity>
                    {
                        ErrorCodes.ErrorSeverity.Error,
                        ErrorCodes.ErrorSeverity.Critical,
                        ErrorCodes.ErrorSeverity.Fatal
                    }
                });
            }

            if (category == ErrorCodes.ErrorCategory.AI &&
                severity >= ErrorCodes.ErrorSeverity.Error)
            {
                recipients.Add(new AlertRecipient
                {
                    Name = "AI Engineering Team",
                    ContactInfo = _appConfig.ErrorReporting?.AIEngineeringEmail ?? "ai-eng@neda.ai",
                    Channel = AlertChannel.Teams,
                    Severities = new List<ErrorCodes.ErrorSeverity>
                    {
                        ErrorCodes.ErrorSeverity.Error,
                        ErrorCodes.ErrorSeverity.Critical
                    }
                });
            }

            return recipients;
        }

        private void UpdateErrorMetrics(int errorCode, TimeSpan elapsedTime)
        {
            // placeholder
        }

        private void AddCorsHeaders(HttpContext context)
        {
            if (context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                return;

            context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Authorization, X-API-Key");
            context.Response.Headers.Append("Access-Control-Allow-Credentials", "true");
        }

        private void AddSecurityHeaders(HttpContext context)
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");

            if (!context.Response.Headers.ContainsKey("Content-Security-Policy"))
            {
                context.Response.Headers.Append("Content-Security-Policy",
                    "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';");
            }

            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

            context.Response.Headers.Append("Permissions-Policy",
                "geolocation=(), microphone=(), camera=()");
        }

        private void LogRequest(HttpContext context, string requestId, string correlationId)
        {
            var logData = new
            {
                RequestId = requestId,
                CorrelationId = correlationId,
                Method = context.Request.Method,
                Path = context.Request.Path,
                QueryString = context.Request.QueryString,
                ClientIp = GetClientIpAddress(context),
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                ContentType = context.Request.ContentType,
                ContentLength = context.Request.ContentLength,
                Headers = SanitizeHeaders(context.Request.Headers)
            };

            _logger.LogInformation("Request started: {@LogData}", logData);
        }

        private void LogResponse(HttpContext context, string requestId, TimeSpan elapsedTime)
        {
            var logData = new
            {
                RequestId = requestId,
                StatusCode = context.Response.StatusCode,
                Duration = elapsedTime.TotalMilliseconds,
                ContentType = context.Response.ContentType,
                ContentLength = context.Response.ContentLength
            };

            var logLevel = context.Response.StatusCode >= 400 ?
                LogLevel.Warning : LogLevel.Information;

            _logger.Log(logLevel, "Request completed: {@LogData}", logData);
        }

        private void LogErrorResponse(
            HttpContext context,
            HttpStatusCode statusCode,
            string reportId,
            TimeSpan elapsedTime)
        {
            var logData = new
            {
                ReportId = reportId,
                StatusCode = (int)statusCode,
                StatusText = statusCode.ToString(),
                Duration = elapsedTime.TotalMilliseconds,
                Path = context.Request.Path,
                Method = context.Request.Method,
                ClientIp = GetClientIpAddress(context)
            };

            _logger.LogError("Error response sent: {@LogData}", logData);
        }

        private async Task LogErrorToAuditAsync(
            HttpContext context,
            Exception exception,
            string requestId)
        {
            try
            {
                await _auditLogger.LogErrorAsync(new AuditErrorEntry
                {
                    RequestId = requestId,
                    UserId = GetUserId(context),
                    Timestamp = DateTime.UtcNow,
                    Path = context.Request.Path,
                    Method = context.Request.Method,
                    StatusCode = context.Response.StatusCode,
                    ExceptionType = exception.GetType().Name,
                    ExceptionMessage = exception.Message,
                    ClientIp = GetClientIpAddress(context),
                    UserAgent = context.Request.Headers.UserAgent.ToString()
                });
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Failed to log error to audit: {Message}", auditEx.Message);
            }
        }

        private void CleanupRequestResources(HttpContext context)
        {
            try
            {
                context.Items.Clear();

                // AsyncLocal temizliği burada geçerli değil (Current yok). İhtiyaç varsa
                // projede kullanılan spesifik AsyncLocal instance'larını burada sıfırlamalısın.
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Error during request resource cleanup");
            }
        }

        #endregion;
    }

    #region Supporting Classes;

    public class RequestTrackingInfo
    {
        public string TrackingId { get; set; } = Guid.NewGuid().ToString();
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class RequestTrackingService
    {
        private readonly ILogger<RequestTrackingService> _logger;
        private readonly Dictionary<string, RequestTrackingInfo> _activeRequests = new();
        private readonly object _lock = new object();

        public RequestTrackingService(ILogger<RequestTrackingService> logger)
        {
            _logger = logger;
        }

        public async Task<RequestTrackingInfo> StartTrackingAsync(HttpContext context, string requestId)
        {
            var trackingInfo = new RequestTrackingInfo
            {
                TrackingId = Guid.NewGuid().ToString(),
                StartTime = DateTime.UtcNow,
                RequestId = requestId,
                CorrelationId = context.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString(),
                Path = context.Request.Path,
                Method = context.Request.Method,
                Metadata = new Dictionary<string, object>
                {
                    ["ClientIp"] = GetClientIpAddress(context),
                    ["UserAgent"] = context.Request.Headers.UserAgent.ToString(),
                    ["ContentType"] = context.Request.ContentType ?? string.Empty
                }
            };

            lock (_lock)
            {
                _activeRequests[trackingInfo.TrackingId] = trackingInfo;
            }

            _logger.LogDebug("Started tracking request: {TrackingId}", trackingInfo.TrackingId);

            return trackingInfo;
        }

        public async Task CompleteTrackingAsync(RequestTrackingInfo trackingInfo, int statusCode)
        {
            trackingInfo.EndTime = DateTime.UtcNow;
            trackingInfo.Metadata["StatusCode"] = statusCode;
            trackingInfo.Metadata["Duration"] = (trackingInfo.EndTime - trackingInfo.StartTime)?.TotalMilliseconds ?? 0;

            _logger.LogDebug("Completed tracking request: {TrackingId}, Duration: {Duration}ms",
                trackingInfo.TrackingId, trackingInfo.Metadata["Duration"]);
        }

        public async Task CleanupTrackingAsync(RequestTrackingInfo trackingInfo)
        {
            lock (_lock)
            {
                _activeRequests.Remove(trackingInfo.TrackingId);
            }

            _logger.LogDebug("Cleaned up tracking for request: {TrackingId}", trackingInfo.TrackingId);
        }

        public List<RequestTrackingInfo> GetActiveRequests()
        {
            lock (_lock)
            {
                return _activeRequests.Values.ToList();
            }
        }

        private string GetClientIpAddress(HttpContext context)
        {
            return context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        }
    }

    public class AuditErrorEntry
    {
        public string RequestId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Path { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public string ExceptionType { get; set; } = string.Empty;
        public string ExceptionMessage { get; set; } = string.Empty;
        public string ClientIp { get; set; } = string.Empty;
        public string UserAgent { get; set; } = string.Empty;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    #endregion;

    #region Custom Exceptions;

    public class ValidationException : Exception
    {
        public Dictionary<string, string[]> Errors { get; }

        public ValidationException(Dictionary<string, string[]> errors)
            : base("Validation failed")
        {
            Errors = errors;
        }

        public ValidationException(string message, Dictionary<string, string[]> errors)
            : base(message)
        {
            Errors = errors;
        }
    }

    public class AuthenticationException : Exception
    {
        public AuthenticationException(string message) : base(message) { }
        public AuthenticationException(string message, Exception inner) : base(message, inner) { }
    }

    public class AuthorizationException : Exception
    {
        public AuthorizationException(string message) : base(message) { }
        public AuthorizationException(string message, Exception inner) : base(message, inner) { }
    }

    public class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message) { }
        public NotFoundException(string message, Exception inner) : base(message, inner) { }
    }

    public class ConflictException : Exception
    {
        public ConflictException(string message) : base(message) { }
        public ConflictException(string message, Exception inner) : base(message, inner) { }
    }

    public class RateLimitException : Exception
    {
        public TimeSpan RetryAfter { get; }

        public RateLimitException(string message, TimeSpan retryAfter)
            : base(message)
        {
            RetryAfter = retryAfter;
        }
    }

    public class ServiceUnavailableException : Exception
    {
        public ServiceUnavailableException(string message) : base(message) { }
        public ServiceUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    #endregion;

    #region Extension Methods;

    public static class ErrorHandlerMiddlewareExtensions
    {
        public static IApplicationBuilder UseNedaErrorHandler(this IApplicationBuilder app)
        {
            return app.UseMiddleware<ErrorHandlerMiddleware>();
        }

        public static IServiceCollection AddNedaErrorHandling(this IServiceCollection services)
        {
            services.AddScoped<ErrorHandlerMiddleware>();
            services.AddSingleton<RequestTrackingService>();

            services.AddExceptionHandler<ErrorHandlerMiddleware>();

            services.Configure<ExceptionHandlerOptions>(options =>
            {
                options.ExceptionHandlingPath = "/error";
                options.AllowStatusCode404Response = true;
            });

            return services;
        }

        public static IEndpointConventionBuilder MapNedaErrorHandler(this IEndpointRouteBuilder endpoints)
        {
            return endpoints.Map("/error", async (HttpContext context) =>
            {
                var exceptionHandler = context.Features.Get<IExceptionHandlerFeature>();
                var exception = exceptionHandler?.Error;

                if (exception != null)
                {
                    var errorReporter = context.RequestServices.GetRequiredService<IErrorReporter>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<ErrorHandlerMiddleware>>();

                    logger.LogError(exception, "Global error handler caught exception");

                    var errorContext = new ErrorContext
                    {
                        Component = "GlobalErrorHandler",
                        Operation = "ErrorEndpoint",
                        UserId = "System",
                        SessionId = context.Session?.Id ?? Guid.NewGuid().ToString(),
                        RequestPath = context.Request.Path,
                        HttpMethod = context.Request.Method,
                        AdditionalData = new Dictionary<string, object>
                        {
                            ["IsGlobalErrorHandler"] = true,
                            ["OriginalPath"] = exceptionHandler?.Path ?? string.Empty
                        }
                    };

                    await errorReporter.ReportErrorAsync(exception, errorContext);
                }

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

                var response = new
                {
                    ErrorCode = ErrorCodes.System.RUNTIME_EXCEPTION,
                    Message = "An unexpected error occurred.",
                    Timestamp = DateTime.UtcNow
                };

                await context.Response.WriteAsJsonAsync(response);
            });
        }
    }

    #endregion;
}
