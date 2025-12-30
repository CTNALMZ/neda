using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.Common;
using NEDA.API.DTOs;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace NEDA.API.Middleware;
{
    /// <summary>
    Comprehensive logging middleware for NEDA API;
    /// Provides detailed request/response logging, performance monitoring, and audit trail;
    /// </summary>
    public class LoggingMiddleware;
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<LoggingMiddleware> _logger;
        private readonly LoggingSettings _settings;
        private readonly IAuditLogger _auditLogger;
        private readonly IPerformanceMonitor _performanceMonitor;

        public LoggingMiddleware(
            RequestDelegate next,
            ILogger<LoggingMiddleware> logger,
            IOptions<LoggingSettings> settings,
            IAuditLogger auditLogger = null,
            IPerformanceMonitor performanceMonitor = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _auditLogger = auditLogger;
            _performanceMonitor = performanceMonitor;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString();
            var correlationId = GetCorrelationId(context);
            var sessionId = GetSessionId(context);
            var userId = GetUserId(context);

            // Create log context;
            var logContext = new LogContext;
            {
                RequestId = requestId,
                CorrelationId = correlationId,
                SessionId = sessionId,
                UserId = userId,
                ClientIp = GetClientIpAddress(context),
                UserAgent = GetUserAgent(context),
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                // Set context in response headers;
                SetResponseHeaders(context, logContext);

                // Log request start;
                await LogRequestStartAsync(context, logContext);

                // Capture original response body for logging;
                var originalResponseBody = context.Response.Body;
                using var responseBodyStream = new MemoryStream();
                context.Response.Body = responseBodyStream;

                // Capture request body if enabled;
                string requestBody = null;
                if (_settings.LogRequestBody)
                {
                    requestBody = await CaptureRequestBodyAsync(context.Request);
                }

                // Execute the request pipeline;
                await _next(context);

                stopwatch.Stop();

                // Capture response body;
                string responseBody = null;
                if (_settings.LogResponseBody && ShouldLogResponseBody(context))
                {
                    responseBody = await CaptureResponseBodyAsync(context.Response, responseBodyStream);
                }

                // Log request completion;
                await LogRequestCompletionAsync(context, logContext, stopwatch.Elapsed, requestBody, responseBody);

                // Copy captured response back to original stream;
                await responseBodyStream.CopyToAsync(originalResponseBody);
                context.Response.Body = originalResponseBody;

                // Update performance metrics;
                UpdatePerformanceMetrics(context, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await LogRequestErrorAsync(context, logContext, stopwatch.Elapsed, ex);
                throw; // Let error handling middleware handle the exception;
            }
            finally
            {
                // Ensure cleanup;
                EnsureCleanup();
            }
        }

        #region Request Logging;
        private async Task LogRequestStartAsync(HttpContext context, LogContext logContext)
        {
            if (!_settings.LogRequestStart)
                return;

            var requestInfo = new RequestLog;
            {
                LogContext = logContext,
                HttpMethod = context.Request.Method,
                Path = context.Request.Path,
                QueryString = context.Request.QueryString.ToString(),
                Headers = GetFilteredHeaders(context.Request.Headers),
                Protocol = context.Request.Protocol,
                Scheme = context.Request.Scheme,
                Host = context.Request.Host.ToString(),
                Timestamp = DateTime.UtcNow;
            };

            // Log to different sinks based on configuration;
            if (_settings.LogToConsole)
            {
                LogToConsole("REQUEST_START", requestInfo);
            }

            if (_settings.LogToFile)
            {
                await LogToFileAsync("REQUEST_START", requestInfo);
            }

            if (_settings.EnableStructuredLogging)
            {
                LogStructured("RequestStart", requestInfo);
            }

            // Log to audit trail if enabled;
            if (_settings.EnableAuditLogging && _auditLogger != null)
            {
                await _auditLogger.LogRequestStartAsync(requestInfo);
            }
        }

        private async Task LogRequestCompletionAsync(HttpContext context, LogContext logContext,
            TimeSpan duration, string requestBody, string responseBody)
        {
            var logLevel = GetLogLevelForStatusCode(context.Response.StatusCode);

            var responseInfo = new ResponseLog;
            {
                LogContext = logContext,
                StatusCode = context.Response.StatusCode,
                Headers = GetFilteredHeaders(context.Response.Headers),
                ContentType = context.Response.ContentType,
                ContentLength = context.Response.ContentLength,
                Duration = duration,
                RequestBody = _settings.LogRequestBody ? requestBody : null,
                ResponseBody = _settings.LogResponseBody ? responseBody : null,
                Timestamp = DateTime.UtcNow;
            };

            // Log to different sinks;
            if (_settings.LogToConsole)
            {
                LogToConsole("REQUEST_COMPLETE", responseInfo);
            }

            if (_settings.LogToFile)
            {
                await LogToFileAsync("REQUEST_COMPLETE", responseInfo);
            }

            if (_settings.EnableStructuredLogging)
            {
                LogStructured("RequestComplete", responseInfo);
            }

            // Log to audit trail;
            if (_settings.EnableAuditLogging && _auditLogger != null)
            {
                await _auditLogger.LogRequestCompleteAsync(responseInfo);
            }

            // Log with appropriate log level;
            _logger.Log(logLevel,
                "Request {RequestId} completed: {Method} {Path} -> {StatusCode} in {Duration}ms",
                logContext.RequestId,
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                duration.TotalMilliseconds);
        }

        private async Task LogRequestErrorAsync(HttpContext context, LogContext logContext,
            TimeSpan duration, Exception exception)
        {
            var errorLog = new ErrorLog;
            {
                LogContext = logContext,
                ExceptionType = exception.GetType().Name,
                ExceptionMessage = exception.Message,
                StackTrace = _settings.IncludeStackTrace ? exception.StackTrace : null,
                Duration = duration,
                Timestamp = DateTime.UtcNow;
            };

            // Log to different sinks;
            if (_settings.LogToConsole)
            {
                LogToConsole("REQUEST_ERROR", errorLog);
            }

            if (_settings.LogToFile)
            {
                await LogToFileAsync("REQUEST_ERROR", errorLog);
            }

            if (_settings.EnableStructuredLogging)
            {
                LogStructured("RequestError", errorLog);
            }

            // Log to audit trail;
            if (_settings.EnableAuditLogging && _auditLogger != null)
            {
                await _auditLogger.LogRequestErrorAsync(errorLog);
            }

            // Log exception;
            _logger.LogError(exception,
                "Request {RequestId} failed: {Method} {Path} after {Duration}ms",
                logContext.RequestId,
                context.Request.Method,
                context.Request.Path,
                duration.TotalMilliseconds);
        }
        #endregion;

        #region Body Capture Methods;
        private async Task<string> CaptureRequestBodyAsync(HttpRequest request)
        {
            if (!request.Body.CanSeek)
            {
                return "[Non-readable stream]";
            }

            try
            {
                // Enable buffering to read request body multiple times;
                request.EnableBuffering();

                // Read the stream;
                using var reader = new StreamReader(request.Body, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);

                var body = await reader.ReadToEndAsync();

                // Reset the position so the next middleware can read it;
                request.Body.Position = 0;

                // Apply body size limit;
                if (_settings.MaxRequestBodySize > 0 && body.Length > _settings.MaxRequestBodySize)
                {
                    return $"[Body truncated: {body.Length} bytes > {_settings.MaxRequestBodySize} limit]";
                }

                // Mask sensitive data;
                if (_settings.MaskSensitiveData)
                {
                    body = MaskSensitiveData(body);
                }

                return body;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture request body");
                return $"[Error capturing body: {ex.Message}]";
            }
        }

        private async Task<string> CaptureResponseBodyAsync(HttpResponse response, MemoryStream responseBodyStream)
        {
            if (responseBodyStream.Length == 0)
            {
                return "[Empty response]";
            }

            try
            {
                responseBodyStream.Position = 0;
                using var reader = new StreamReader(responseBodyStream, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);

                var body = await reader.ReadToEndAsync();

                // Reset position for copying;
                responseBodyStream.Position = 0;

                // Apply body size limit;
                if (_settings.MaxResponseBodySize > 0 && body.Length > _settings.MaxResponseBodySize)
                {
                    return $"[Body truncated: {body.Length} bytes > {_settings.MaxResponseBodySize} limit]";
                }

                // Mask sensitive data;
                if (_settings.MaskSensitiveData)
                {
                    body = MaskSensitiveData(body);
                }

                return body;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture response body");
                return $"[Error capturing body: {ex.Message}]";
            }
        }

        private bool ShouldLogResponseBody(HttpContext context)
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

            // Skip logging for certain content types;
            var contentType = context.Response.ContentType?.ToLowerInvariant() ?? "";
            if (contentType.Contains("octet-stream") ||
                contentType.Contains("pdf") ||
                contentType.Contains("image"))
            {
                return false;
            }

            // Skip logging for certain paths;
            if (_settings.ExcludeResponseBodyPaths.Any(pattern =>
                path.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Skip large responses;
            if (context.Response.ContentLength > _settings.MaxResponseBodySize &&
                _settings.MaxResponseBodySize > 0)
            {
                return false;
            }

            return true;
        }
        #endregion;

        #region Logging Methods;
        private void LogToConsole(string eventType, object data)
        {
            try
            {
                var logEntry = new;
                {
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Event = eventType,
                    Data = data;
                };

                var json = System.Text.Json.JsonSerializer.Serialize(logEntry, new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                });

                Console.WriteLine(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log to console");
            }
        }

        private async Task LogToFileAsync(string eventType, object data)
        {
            try
            {
                var logEntry = new;
                {
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Event = eventType,
                    Data = data;
                };

                var json = System.Text.Json.JsonSerializer.Serialize(logEntry, new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                });

                var logDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");
                Directory.CreateDirectory(logDirectory);

                var logFile = Path.Combine(logDirectory, $"neda-api-{DateTime.UtcNow:yyyy-MM-dd}.log");
                await File.AppendAllTextAsync(logFile, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log to file");
            }
        }

        private void LogStructured(string eventName, object data)
        {
            try
            {
                using (_logger.BeginScope(new Dictionary<string, object>
                {
                    ["Event"] = eventName,
                    ["Timestamp"] = DateTime.UtcNow,
                    ["Data"] = data;
                }))
                {
                    _logger.LogInformation("Structured log event: {EventName}", eventName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log structured data");
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

        private string GetSessionId(HttpContext context)
        {
            return context.Request.Headers["X-Session-ID"].FirstOrDefault() ??
                   context.Request.Cookies["NEDA-Session"] ??
                   "Unknown";
        }

        private string GetUserId(HttpContext context)
        {
            return context.User?.FindFirst("userId")?.Value ??
                   context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                   "Anonymous";
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

        private string GetUserAgent(HttpContext context)
        {
            return context.Request.Headers["User-Agent"].ToString() ?? "Unknown";
        }

        private Dictionary<string, string> GetFilteredHeaders(IHeaderDictionary headers)
        {
            var filtered = new Dictionary<string, string>();

            foreach (var header in headers)
            {
                // Skip sensitive headers;
                if (_settings.SensitiveHeaders.Any(h =>
                    header.Key.Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    filtered[header.Key] = "[MASKED]";
                }
                else;
                {
                    filtered[header.Key] = string.Join(", ", header.Value);
                }
            }

            return filtered;
        }

        private void SetResponseHeaders(HttpContext context, LogContext logContext)
        {
            context.Response.Headers["X-Request-ID"] = logContext.RequestId;
            context.Response.Headers["X-Correlation-ID"] = logContext.CorrelationId;
            context.Response.Headers["X-Session-ID"] = logContext.SessionId;
        }

        private LogLevel GetLogLevelForStatusCode(int statusCode)
        {
            return statusCode switch;
            {
                >= 500 => LogLevel.Error,    // Server errors;
                >= 400 => LogLevel.Warning,  // Client errors;
                >= 300 => LogLevel.Information, // Redirections;
                _ => LogLevel.Information    // Success;
            };
        }

        private string MaskSensitiveData(string data)
        {
            if (string.IsNullOrEmpty(data))
                return data;

            // Mask passwords;
            data = System.Text.RegularExpressions.Regex.Replace(
                data,
                @"(""password""\s*:\s*"")[^""]*("")",
                "$1***$2",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Mask tokens;
            data = System.Text.RegularExpressions.Regex.Replace(
                data,
                @"(Bearer\s+)[a-zA-Z0-9\-_]+?\.[a-zA-Z0-9\-_]+?\.[a-zA-Z0-9\-_]+",
                "$1***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Mask API keys;
            data = System.Text.RegularExpressions.Regex.Replace(
                data,
                @"(""api[_-]?key""\s*:\s*"")[^""]*("")",
                "$1***$2",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Mask credit cards;
            data = System.Text.RegularExpressions.Regex.Replace(
                data,
                @"\b(?:\d[ -]*?){13,16}\b",
                "***************");

            return data;
        }

        private void UpdatePerformanceMetrics(HttpContext context, TimeSpan duration)
        {
            if (_performanceMonitor == null)
                return;

            try
            {
                _performanceMonitor.RecordRequest(
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    duration);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update performance metrics");
            }
        }

        private void EnsureCleanup()
        {
            // Any cleanup logic if needed;
        }
        #endregion;

        #region Supporting Classes;
        /// <summary>
        /// Logging settings configuration;
        /// </summary>
        public class LoggingSettings;
        {
            public bool LogRequestStart { get; set; } = true;
            public bool LogRequestBody { get; set; } = false;
            public bool LogResponseBody { get; set; } = false;
            public int MaxRequestBodySize { get; set; } = 1024 * 10; // 10KB;
            public int MaxResponseBodySize { get; set; } = 1024 * 50; // 50KB;
            public bool IncludeStackTrace { get; set; } = false;
            public bool MaskSensitiveData { get; set; } = true;
            public bool LogToConsole { get; set; } = true;
            public bool LogToFile { get; set; } = true;
            public bool EnableStructuredLogging { get; set; } = true;
            public bool EnableAuditLogging { get; set; } = true;
            public List<string> SensitiveHeaders { get; set; } = new List<string>
            {
                "authorization",
                "cookie",
                "set-cookie",
                "x-api-key",
                "x-access-token",
                "password",
                "secret"
            };
            public List<string> ExcludeResponseBodyPaths { get; set; } = new List<string>
            {
                "/api/files/download",
                "/api/export/",
                "/api/import/"
            };
        }

        /// <summary>
        /// Log context for tracking request information;
        /// </summary>
        public class LogContext;
        {
            public string RequestId { get; set; }
            public string CorrelationId { get; set; }
            public string SessionId { get; set; }
            public string UserId { get; set; }
            public string ClientIp { get; set; }
            public string UserAgent { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Request log information;
        /// </summary>
        public class RequestLog;
        {
            public LogContext LogContext { get; set; }
            public string HttpMethod { get; set; }
            public string Path { get; set; }
            public string QueryString { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public string Protocol { get; set; }
            public string Scheme { get; set; }
            public string Host { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Response log information;
        /// </summary>
        public class ResponseLog;
        {
            public LogContext LogContext { get; set; }
            public int StatusCode { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public string ContentType { get; set; }
            public long? ContentLength { get; set; }
            public TimeSpan Duration { get; set; }
            public string RequestBody { get; set; }
            public string ResponseBody { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Error log information;
        /// </summary>
        public class ErrorLog;
        {
            public LogContext LogContext { get; set; }
            public string ExceptionType { get; set; }
            public string ExceptionMessage { get; set; }
            public string StackTrace { get; set; }
            public TimeSpan Duration { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Interface for audit logging;
        /// </summary>
        public interface IAuditLogger;
        {
            Task LogRequestStartAsync(RequestLog requestLog);
            Task LogRequestCompleteAsync(ResponseLog responseLog);
            Task LogRequestErrorAsync(ErrorLog errorLog);
        }

        /// <summary>
        /// Interface for performance monitoring;
        /// </summary>
        public interface IPerformanceMonitor;
        {
            void RecordRequest(string method, string path, int statusCode, TimeSpan duration);
        }
        #endregion;
    }
}
