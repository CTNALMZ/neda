using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.Common;
using NEDA.API.DTOs;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Tasks;

namespace NEDA.API.Middleware;
{
    /// <summary>
    /// Global error handling middleware for NEDA API;
    /// Provides centralized exception handling, logging, and structured error responses;
    /// </summary>
    public class ErrorHandlingMiddleware;
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ErrorHandlingMiddleware> _logger;
        private readonly ErrorHandlingSettings _settings;
        private readonly IErrorReporter _errorReporter;
        private readonly JsonSerializerOptions _jsonOptions;

        public ErrorHandlingMiddleware(
            RequestDelegate next,
            ILogger<ErrorHandlingMiddleware> logger,
            IOptions<ErrorHandlingSettings> settings,
            IErrorReporter errorReporter = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _errorReporter = errorReporter;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                IgnoreNullValues = true;
            };
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString();
            var correlationId = GetCorrelationId(context);
            var operationId = Guid.NewGuid().ToString();

            try
            {
                // Set operation context in response headers;
                context.Response.Headers["X-Operation-ID"] = operationId;
                context.Response.Headers["X-Request-ID"] = requestId;
                context.Response.Headers["X-Correlation-ID"] = correlationId;

                // Log request start;
                LogRequestStart(context, requestId, correlationId);

                await _next(context);

                stopwatch.Stop();

                // Log successful request completion;
                LogRequestCompletion(context, stopwatch.Elapsed, requestId);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await HandleExceptionAsync(context, ex, stopwatch.Elapsed, requestId, correlationId, operationId);
            }
            finally
            {
                // Ensure response headers are set;
                EnsureResponseHeaders(context);
            }
        }

        #region Exception Handling;
        private async Task HandleExceptionAsync(HttpContext context, Exception exception,
            TimeSpan duration, string requestId, string correlationId, string operationId)
        {
            // Determine error type and HTTP status code;
            var (errorType, statusCode, errorResponse) = MapExceptionToErrorResponse(exception, context);

            // Set error response properties;
            errorResponse.CorrelationId = correlationId;
            errorResponse.Timestamp = DateTime.UtcNow;

            // Log the error;
            LogError(context, exception, errorType, statusCode, duration, requestId);

            // Report error to monitoring systems;
            await ReportErrorAsync(context, exception, errorType, errorResponse, requestId);

            // Write error response;
            await WriteErrorResponseAsync(context, statusCode, errorResponse);

            // Log error response completion;
            LogErrorResponse(context, statusCode, duration, requestId);
        }

        private (ErrorType errorType, HttpStatusCode statusCode, ErrorResponse errorResponse)
            MapExceptionToErrorResponse(Exception exception, HttpContext context)
        {
            return exception switch;
            {
                // Validation errors;
                ValidationException validationEx => (
                    ErrorType.Validation,
                    HttpStatusCode.BadRequest,
                    CreateValidationErrorResponse(validationEx, context)
                ),

                // Authentication errors;
                AuthenticationException authEx => (
                    ErrorType.Authentication,
                    HttpStatusCode.Unauthorized,
                    CreateAuthenticationErrorResponse(authEx, context)
                ),

                // Authorization errors;
                AuthorizationException authzEx => (
                    ErrorType.Authorization,
                    HttpStatusCode.Forbidden,
                    CreateAuthorizationErrorResponse(authzEx, context)
                ),

                // Resource not found;
                ResourceNotFoundException notFoundEx => (
                    ErrorType.NotFound,
                    HttpStatusCode.NotFound,
                    CreateNotFoundErrorResponse(notFoundEx, context)
                ),

                // Business logic/conflict errors;
                BusinessException businessEx => (
                    ErrorType.Conflict,
                    HttpStatusCode.Conflict,
                    CreateBusinessErrorResponse(businessEx, context)
                ),

                // Rate limiting errors;
                RateLimitException rateLimitEx => (
                    ErrorType.RateLimit,
                    HttpStatusCode.TooManyRequests,
                    CreateRateLimitErrorResponse(rateLimitEx, context)
                ),

                // Service unavailable errors;
                ServiceUnavailableException serviceEx => (
                    ErrorType.System,
                    HttpStatusCode.ServiceUnavailable,
                    CreateServiceErrorResponse(serviceEx, context)
                ),

                // Database errors;
                DatabaseException dbEx => (
                    ErrorType.System,
                    HttpStatusCode.InternalServerError,
                    CreateDatabaseErrorResponse(dbEx, context)
                ),

                // Network errors;
                NetworkException networkEx => (
                    ErrorType.Network,
                    HttpStatusCode.BadGateway,
                    CreateNetworkErrorResponse(networkEx, context)
                ),

                // Timeout errors;
                TimeoutException timeoutEx => (
                    ErrorType.Timeout,
                    HttpStatusCode.GatewayTimeout,
                    CreateTimeoutErrorResponse(timeoutEx, context)
                ),

                // External service errors;
                ExternalServiceException externalEx => (
                    ErrorType.System,
                    HttpStatusCode.BadGateway,
                    CreateExternalServiceErrorResponse(externalEx, context)
                ),

                // General application errors;
                ApplicationException appEx => (
                    ErrorType.System,
                    HttpStatusCode.InternalServerError,
                    CreateApplicationErrorResponse(appEx, context)
                ),

                // Aggregate exceptions;
                AggregateException aggEx => (
                    ErrorType.System,
                    HttpStatusCode.InternalServerError,
                    CreateAggregateErrorResponse(aggEx, context)
                ),

                // Default for unhandled exceptions;
                _ => (
                    ErrorType.Unknown,
                    HttpStatusCode.InternalServerError,
                    CreateUnknownErrorResponse(exception, context)
                )
            };
        }
        #endregion;

        #region Error Response Creation;
        private ErrorResponse CreateValidationErrorResponse(ValidationException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "VALIDATION_ERROR",
                Message = ex.Message,
                ErrorType = ErrorType.Validation,
                ValidationErrors = ex.ValidationErrors,
                RecoverySuggestion = "Please correct the validation errors and try again.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/validation"
            };
        }

        private ErrorResponse CreateAuthenticationErrorResponse(AuthenticationException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "AUTHENTICATION_ERROR",
                Message = ex.Message,
                ErrorType = ErrorType.Authentication,
                RecoverySuggestion = "Please check your credentials and try again.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/authentication"
            };
        }

        private ErrorResponse CreateAuthorizationErrorResponse(AuthorizationException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "AUTHORIZATION_ERROR",
                Message = ex.Message,
                ErrorType = ErrorType.Authorization,
                RecoverySuggestion = "Contact your administrator for access permissions.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/authorization"
            };
        }

        private ErrorResponse CreateNotFoundErrorResponse(ResourceNotFoundException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "RESOURCE_NOT_FOUND",
                Message = ex.Message,
                ErrorType = ErrorType.NotFound,
                RecoverySuggestion = "Verify the resource identifier and try again.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/not-found"
            };
        }

        private ErrorResponse CreateBusinessErrorResponse(BusinessException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "BUSINESS_ERROR",
                Message = ex.Message,
                ErrorType = ErrorType.Conflict,
                RecoverySuggestion = ex.RecoverySuggestion ?? "Please review your request and try again.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/business"
            };
        }

        private ErrorResponse CreateRateLimitErrorResponse(RateLimitException ex, HttpContext context)
        {
            var resetTime = ex.ResetTime?.ToString("o") ?? DateTime.UtcNow.AddMinutes(1).ToString("o");

            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "RATE_LIMIT_EXCEEDED",
                Message = ex.Message,
                ErrorType = ErrorType.RateLimit,
                RecoverySuggestion = $"Please wait until {resetTime} before trying again.",
                Details = new;
                {
                    ex.Limit,
                    ex.Remaining,
                    ex.ResetTime,
                    ex.Window;
                },
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/rate-limit"
            };
        }

        private ErrorResponse CreateServiceErrorResponse(ServiceUnavailableException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "SERVICE_UNAVAILABLE",
                Message = ex.Message,
                ErrorType = ErrorType.System,
                RecoverySuggestion = "Please try again later. The service team has been notified.",
                Details = new;
                {
                    ex.ServiceName,
                    ex.EstimatedRecoveryTime;
                },
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/service-unavailable"
            };
        }

        private ErrorResponse CreateDatabaseErrorResponse(DatabaseException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "DATABASE_ERROR",
                Message = "A database error occurred while processing your request.",
                ErrorType = ErrorType.System,
                RecoverySuggestion = "Please try again later. The technical team has been notified.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/database"
            };
        }

        private ErrorResponse CreateNetworkErrorResponse(NetworkException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "NETWORK_ERROR",
                Message = ex.Message,
                ErrorType = ErrorType.Network,
                RecoverySuggestion = "Please check your network connection and try again.",
                Details = new;
                {
                    ex.Endpoint,
                    ex.Timeout;
                },
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/network"
            };
        }

        private ErrorResponse CreateTimeoutErrorResponse(TimeoutException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "REQUEST_TIMEOUT",
                Message = ex.Message,
                ErrorType = ErrorType.Timeout,
                RecoverySuggestion = "Please try again with a simpler request or contact support.",
                Details = new;
                {
                    ex.TimeoutDuration,
                    ex.Operation;
                },
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/timeout"
            };
        }

        private ErrorResponse CreateExternalServiceErrorResponse(ExternalServiceException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "EXTERNAL_SERVICE_ERROR",
                Message = ex.Message,
                ErrorType = ErrorType.System,
                RecoverySuggestion = "Please try again later. The external service is currently unavailable.",
                Details = new;
                {
                    ex.ServiceName,
                    ex.StatusCode,
                    ex.ResponseContent;
                },
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/external-service"
            };
        }

        private ErrorResponse CreateApplicationErrorResponse(ApplicationException ex, HttpContext context)
        {
            return new ErrorResponse;
            {
                ErrorCode = ex.ErrorCode ?? "APPLICATION_ERROR",
                Message = ex.Message,
                ErrorType = ErrorType.System,
                RecoverySuggestion = "Please try again later. The technical team has been notified.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/application"
            };
        }

        private ErrorResponse CreateAggregateErrorResponse(AggregateException ex, HttpContext context)
        {
            var innerErrors = ex.InnerExceptions.Select(innerEx => new;
            {
                Type = innerEx.GetType().Name,
                Message = innerEx.Message,
                StackTrace = _settings.IncludeStackTrace ? innerEx.StackTrace : null;
            }).ToList();

            return new ErrorResponse;
            {
                ErrorCode = "AGGREGATE_ERROR",
                Message = "Multiple errors occurred while processing your request.",
                ErrorType = ErrorType.System,
                Details = new;
                {
                    ErrorCount = ex.InnerExceptions.Count,
                    InnerErrors = innerErrors;
                },
                RecoverySuggestion = "Please try again with a different request or contact support.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/aggregate"
            };
        }

        private ErrorResponse CreateUnknownErrorResponse(Exception ex, HttpContext context)
        {
            var errorResponse = new ErrorResponse;
            {
                ErrorCode = "UNKNOWN_ERROR",
                Message = "An unexpected error occurred while processing your request.",
                ErrorType = ErrorType.Unknown,
                RecoverySuggestion = "Please try again later. The technical team has been notified.",
                DocumentationUrl = $"{_settings.DocumentationBaseUrl}/errors/unknown"
            };

            // Include stack trace in development mode;
            if (_settings.IncludeStackTrace)
            {
                errorResponse.StackTrace = ex.StackTrace;
                errorResponse.Details = new;
                {
                    ExceptionType = ex.GetType().FullName,
                    ExceptionMessage = ex.Message,
                    InnerException = ex.InnerException?.Message;
                };
            }

            // Mask sensitive information;
            if (_settings.MaskSensitiveData)
            {
                errorResponse.Message = MaskSensitiveData(errorResponse.Message);
                if (errorResponse.Details != null)
                {
                    errorResponse.Details = MaskSensitiveData(errorResponse.Details);
                }
            }

            return errorResponse;
        }
        #endregion;

        #region Logging and Reporting;
        private void LogRequestStart(HttpContext context, string requestId, string correlationId)
        {
            if (!_settings.LogRequestStart)
                return;

            _logger.LogDebug("Request started: {RequestId} | {CorrelationId} | {Method} {Path} | Client: {ClientIp}",
                requestId,
                correlationId,
                context.Request.Method,
                context.Request.Path,
                GetClientIpAddress(context));
        }

        private void LogRequestCompletion(HttpContext context, TimeSpan duration, string requestId)
        {
            var logLevel = GetLogLevelForStatusCode(context.Response.StatusCode);

            _logger.Log(logLevel,
                "Request completed: {RequestId} | {Method} {Path} | Status: {StatusCode} | Duration: {Duration}ms",
                requestId,
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                duration.TotalMilliseconds);
        }

        private void LogError(HttpContext context, Exception exception, ErrorType errorType,
            HttpStatusCode statusCode, TimeSpan duration, string requestId)
        {
            var logLevel = GetLogLevelForException(exception, errorType);

            _logger.Log(logLevel, exception,
                "Error handled: {RequestId} | {Method} {Path} | ErrorType: {ErrorType} | Status: {StatusCode} | Duration: {Duration}ms",
                requestId,
                context.Request.Method,
                context.Request.Path,
                errorType,
                (int)statusCode,
                duration.TotalMilliseconds);
        }

        private void LogErrorResponse(HttpContext context, HttpStatusCode statusCode, TimeSpan duration, string requestId)
        {
            _logger.LogDebug("Error response sent: {RequestId} | Status: {StatusCode} | TotalDuration: {Duration}ms",
                requestId,
                (int)statusCode,
                duration.TotalMilliseconds);
        }

        private async Task ReportErrorAsync(HttpContext context, Exception exception, ErrorType errorType,
            ErrorResponse errorResponse, string requestId)
        {
            if (_errorReporter == null || !_settings.EnableErrorReporting)
                return;

            try
            {
                var errorReport = new ErrorReport;
                {
                    RequestId = requestId,
                    CorrelationId = errorResponse.CorrelationId,
                    Timestamp = DateTime.UtcNow,
                    ErrorType = errorType.ToString(),
                    ErrorCode = errorResponse.ErrorCode,
                    Message = errorResponse.Message,
                    ExceptionType = exception.GetType().FullName,
                    StackTrace = _settings.IncludeStackTrace ? exception.StackTrace : null,
                    HttpMethod = context.Request.Method,
                    Path = context.Request.Path,
                    QueryString = context.Request.QueryString.ToString(),
                    ClientIp = GetClientIpAddress(context),
                    UserAgent = context.Request.Headers["User-Agent"].ToString(),
                    UserId = context.User?.FindFirst("userId")?.Value,
                    AdditionalData = new;
                    {
                        context.Request.Headers,
                        ExceptionDetails = exception.ToString()
                    }
                };

                await _errorReporter.ReportErrorAsync(errorReport);
            }
            catch (Exception reportingEx)
            {
                _logger.LogError(reportingEx, "Failed to report error: {RequestId}", requestId);
            }
        }
        #endregion;

        #region Utility Methods;
        private string GetCorrelationId(HttpContext context)
        {
            return context.Request.Headers["X-Correlation-ID"].FirstOrDefault() ??
                   context.Request.Headers["Correlation-ID"].FirstOrDefault() ??
                   Guid.NewGuid().ToString();
        }

        private string GetClientIpAddress(HttpContext context)
        {
            var ipAddress = context.Connection.RemoteIpAddress?.ToString();

            if (context.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                ipAddress = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            }
            else if (context.Request.Headers.ContainsKey("X-Real-IP"))
            {
                ipAddress = context.Request.Headers["X-Real-IP"].ToString();
            }

            return ipAddress ?? "Unknown";
        }

        private LogLevel GetLogLevelForStatusCode(int statusCode)
        {
            return statusCode switch;
            {
                >= 500 => LogLevel.Error,    // Server errors;
                >= 400 => LogLevel.Warning,  // Client errors;
                _ => LogLevel.Information    // Success;
            };
        }

        private LogLevel GetLogLevelForException(Exception exception, ErrorType errorType)
        {
            return errorType switch;
            {
                ErrorType.Validation => LogLevel.Information,
                ErrorType.Authentication => LogLevel.Warning,
                ErrorType.Authorization => LogLevel.Warning,
                ErrorType.NotFound => LogLevel.Information,
                ErrorType.RateLimit => LogLevel.Warning,
                ErrorType.Timeout => LogLevel.Warning,
                ErrorType.Network => LogLevel.Warning,
                _ => LogLevel.Error;
            };
        }

        private object MaskSensitiveData(object data)
        {
            // Implement sensitive data masking logic;
            // This would mask passwords, tokens, credit cards, etc.
            return data; // Simplified implementation;
        }

        private void EnsureResponseHeaders(HttpContext context)
        {
            // Ensure security headers;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["X-XSS-Protection"] = "1; mode=block";

            if (_settings.EnableCors)
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = _settings.AllowedOrigins;
                context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            }
        }

        private async Task WriteErrorResponseAsync(HttpContext context, HttpStatusCode statusCode, ErrorResponse errorResponse)
        {
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = "application/json";

            var json = JsonSerializer.Serialize(errorResponse, _jsonOptions);
            await context.Response.WriteAsync(json);
        }
        #endregion;
    }

    #region Supporting Classes;
    /// <summary>
    /// Error handling settings configuration;
    /// </summary>
    public class ErrorHandlingSettings;
    {
        public bool EnableGlobalErrorHandling { get; set; } = true;
        public bool IncludeStackTrace { get; set; } = false;
        public bool MaskSensitiveData { get; set; } = true;
        public bool EnableErrorReporting { get; set; } = true;
        public bool LogRequestStart { get; set; } = true;
        public bool EnableCors { get; set; } = true;
        public string AllowedOrigins { get; set; } = "*";
        public string DocumentationBaseUrl { get; set; } = "https://docs.neda.ai";
        public Dictionary<int, string> CustomErrorMessages { get; set; } = new Dictionary<int, string>();
    }

    /// <summary>
    /// Error report for monitoring systems;
    /// </summary>
    public class ErrorReport;
    {
        public string RequestId { get; set; }
        public string CorrelationId { get; set; }
        public DateTime Timestamp { get; set; }
        public string ErrorType { get; set; }
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public string ExceptionType { get; set; }
        public string StackTrace { get; set; }
        public string HttpMethod { get; set; }
        public string Path { get; set; }
        public string QueryString { get; set; }
        public string ClientIp { get; set; }
        public string UserAgent { get; set; }
        public string UserId { get; set; }
        public object AdditionalData { get; set; }
    }

    /// <summary>
    /// Interface for error reporting services;
    /// </summary>
    public interface IErrorReporter;
    {
        Task ReportErrorAsync(ErrorReport errorReport);
    }
    #endregion;
}
