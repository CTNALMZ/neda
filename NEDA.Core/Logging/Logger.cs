using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Configuration.EnvironmentManager;
using NEDA.Core.ExceptionHandling.ErrorCodes;

namespace NEDA.Core.Logging
{
    /// <summary>
    /// High-performance, thread-safe logging implementation with multiple log targets;
    /// Supports structured logging, log levels, and real-time log monitoring;
    /// </summary>
    public class Logger : ILogger, IDisposable
    {
        #region Fields and Properties

        private readonly string _categoryName;
        private readonly LogManager _logManager;
        private readonly AppConfig _appConfig;
        private readonly ConcurrentQueue<LogEntry> _logQueue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly SemaphoreSlim _flushSemaphore;
        private readonly object _fileLock = new object();
        private bool _disposed;

        private static readonly ConcurrentDictionary<string, Logger> _loggers = new();
        private static readonly object _staticLock = new object();

        #endregion

        #region Nested Types

        /// <summary>
        /// Represents a single log entry with structured data;
        /// </summary>
        private class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public LogLevel LogLevel { get; set; }
            public string Category { get; set; }
            public string Message { get; set; }
            public Exception Exception { get; set; }
            public string CorrelationId { get; set; }
            public string UserId { get; set; }
            public object[] Scopes { get; set; }
            public object[] Args { get; set; }
            public string FormattedMessage { get; set; }
        }

        /// <summary>
        /// Log scope for contextual logging;
        /// </summary>
        private class LogScope : IDisposable
        {
            private readonly Logger _logger;
            private readonly string _scopeName;
            private readonly object _scopeValue;
            private bool _disposed;

            public LogScope(Logger logger, string scopeName, object scopeValue)
            {
                _logger = logger;
                _scopeName = scopeName;
                _scopeValue = scopeValue;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _logger.EndScope(_scopeName);
                _disposed = true;
            }
        }

        #endregion

        #region Constructor and Factory

        /// <summary>
        /// Private constructor - use CreateLogger factory method;
        /// </summary>
        private Logger(string categoryName, LogManager logManager, AppConfig appConfig)
        {
            _categoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            _logQueue = new ConcurrentQueue<LogEntry>();
            _cancellationTokenSource = new CancellationTokenSource();
            _flushSemaphore = new SemaphoreSlim(1, 1);

            // Start background log processor;
            Task.Run(async () => await ProcessLogQueueAsync(_cancellationTokenSource.Token));

            LogInitialization();
        }

        /// <summary>
        /// Factory method for creating or retrieving logger instances;
        /// </summary>
        public static Logger CreateLogger(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
                categoryName = "Default";

            return _loggers.GetOrAdd(categoryName, name =>
            {
                var config = SettingsManager.LoadConfiguration();
                var logManager = LogManager.Instance;

                return new Logger(name, logManager, config);
            });
        }

        /// <summary>
        /// Creates a logger for the specified type;
        /// </summary>
        public static Logger CreateLogger<T>() where T : class
        {
            return CreateLogger(typeof(T).FullName);
        }

        #endregion

        #region ILogger Implementation

        /// <summary>
        /// Checks if the given log level is enabled;
        /// </summary>
        public bool IsEnabled(LogLevel logLevel)
        {
            if (_disposed)
                return false;

            var minLevel = _appConfig.Logging?.MinimumLevel ?? LogLevel.Information;
            return logLevel >= minLevel;
        }

        /// <summary>
        /// Begins a logical operation scope;
        /// </summary>
        public IDisposable BeginScope<TState>(TState state)
        {
            if (_disposed)
                return new LogScope(this, string.Empty, null);

            var scopeName = state?.ToString() ?? "UnknownScope";
            _logManager.PushScope(scopeName, state);
            return new LogScope(this, scopeName, state);
        }

        /// <summary>
        /// Writes a log entry
        /// </summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel) || _disposed)
                return;

            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            try
            {
                var message = formatter(state, exception);
                if (string.IsNullOrEmpty(message) && exception == null)
                    return;

                var logEntry = CreateLogEntry(logLevel, message, exception, state);

                // Add to queue for asynchronous processing;
                _logQueue.Enqueue(logEntry);

                // Trigger immediate write for high priority logs;
                if (logLevel >= LogLevel.Error)
                {
                    Task.Run(() => WriteImmediateLogAsync(logEntry));
                }
            }
            catch (Exception ex)
            {
                // Fallback logging to prevent logging failures from crashing application;
                SafeFallbackLog($"Logging failed: {ex.Message}", logLevel);
            }
        }

        #endregion

        #region Core Logging Methods

        /// <summary>
        /// Logs a verbose message (most detailed)
        /// </summary>
        public void LogVerbose(string message, params object[] args)
        {
            Log(LogLevel.Trace, 0, args, null, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs a debug message (development/diagnostic)
        /// </summary>
        public void LogDebug(string message, params object[] args)
        {
            Log(LogLevel.Debug, 0, args, null, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs an informational message;
        /// </summary>
        public void LogInformation(string message, params object[] args)
        {
            Log(LogLevel.Information, 0, args, null, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs a warning message;
        /// </summary>
        public void LogWarning(string message, params object[] args)
        {
            Log(LogLevel.Warning, 0, args, null, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs an error message;
        /// </summary>
        public void LogError(string message, params object[] args)
        {
            Log(LogLevel.Error, 0, args, null, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs an error with exception details;
        /// </summary>
        public void LogError(Exception exception, string message, params object[] args)
        {
            Log(LogLevel.Error, 0, args, exception, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs a critical/fatal error;
        /// </summary>
        public void LogCritical(string message, params object[] args)
        {
            Log(LogLevel.Critical, 0, args, null, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs a critical error with exception;
        /// </summary>
        public void LogCritical(Exception exception, string message, params object[] args)
        {
            Log(LogLevel.Critical, 0, args, exception, (s, e) =>
                string.Format(message, args ?? Array.Empty<object>()));
        }

        /// <summary>
        /// Logs performance metrics;
        /// </summary>
        public void LogPerformance(string operation, TimeSpan duration, params object[] additionalData)
        {
            var message = $"Performance: {operation} completed in {duration.TotalMilliseconds}ms";
            Log(LogLevel.Information, 0, additionalData, null, (s, e) => message);
        }

        /// <summary>
        /// Logs business transaction;
        /// </summary>
        public void LogTransaction(string transactionType, string transactionId,
            string status, object data = null)
        {
            var message = $"Transaction: {transactionType} [{transactionId}] - {status}";
            Log(LogLevel.Information, 0, data, null, (s, e) => message);
        }

        #endregion

        #region Private Helper Methods

        private LogEntry CreateLogEntry<TState>(LogLevel logLevel, string message,
            Exception exception, TState state)
        {
            var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
            var userId = Thread.CurrentPrincipal?.Identity?.Name ?? "System";

            return new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                LogLevel = logLevel,
                Category = _categoryName,
                Message = message,
                Exception = exception,
                CorrelationId = correlationId,
                UserId = userId,
                Scopes = _logManager.GetCurrentScopes(),
                Args = state as object[],
                FormattedMessage = FormatLogMessage(logLevel, message, exception, correlationId)
            };
        }

        private string FormatLogMessage(LogLevel logLevel, string message,
            Exception exception, string correlationId)
        {
            var sb = new StringBuilder();

            // Timestamp;
            sb.Append($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] ");

            // Log Level;
            sb.Append($"[{logLevel.ToString().ToUpper()}] ");

            // Correlation ID;
            if (!string.IsNullOrEmpty(correlationId))
                sb.Append($"[CID: {correlationId}] ");

            // Category;
            sb.Append($"[{_categoryName}] ");

            // Message;
            sb.Append(message);

            // Exception details;
            if (exception != null)
            {
                sb.AppendLine();
                sb.AppendLine($"Exception: {exception.GetType().Name}");
                sb.AppendLine($"Message: {exception.Message}");
                sb.AppendLine($"Stack Trace: {exception.StackTrace}");

                if (exception.InnerException != null)
                {
                    sb.AppendLine($"Inner Exception: {exception.InnerException.Message}");
                }
            }

            // Current scopes;
            var scopes = _logManager.GetCurrentScopes();
            if (scopes != null && scopes.Length > 0)
            {
                sb.AppendLine();
                sb.Append("Scopes: ");
                foreach (var scope in scopes)
                {
                    sb.Append($"{scope} | ");
                }
            }

            return sb.ToString();
        }

        private async Task ProcessLogQueueAsync(CancellationToken cancellationToken)
        {
            var batch = new System.Collections.Generic.List<LogEntry>(100);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Batch processing for performance;
                    while (_logQueue.TryDequeue(out var entry) && batch.Count < 100)
                    {
                        batch.Add(entry);
                    }

                    if (batch.Count > 0)
                    {
                        await WriteLogBatchAsync(batch);
                        batch.Clear();
                    }

                    // Throttle processing;
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SafeFallbackLog($"Log queue processing failed: {ex.Message}", LogLevel.Error);
                    await Task.Delay(1000, cancellationToken);
                }
            }

            // Flush remaining logs on shutdown;
            await FlushAsync();
        }

        private async Task WriteLogBatchAsync(System.Collections.Generic.List<LogEntry> entries)
        {
            await _flushSemaphore.WaitAsync();

            try
            {
                // Write to file;
                if (_appConfig.Logging?.EnableFileLogging == true)
                {
                    await WriteToFileAsync(entries);
                }

                // Write to console if in development;
                if (_appConfig.Environment == EnvironmentType.Development)
                {
                    WriteToConsole(entries);
                }

                // Send to log manager for distribution;
                _logManager.ProcessLogEntries(entries);
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        private async Task WriteToFileAsync(System.Collections.Generic.List<LogEntry> entries)
        {
            try
            {
                var logDirectory = _appConfig.Logging?.LogDirectory ??
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                var logFile = Path.Combine(logDirectory,
                    $"log_{DateTime.UtcNow:yyyyMMdd}.txt");

                var logLines = entries.Select(e => e.FormattedMessage);
                var logContent = string.Join(Environment.NewLine, logLines) + Environment.NewLine;

                // Use async file writing with retry
                await RetryFileWriteAsync(logFile, logContent);

                // Rotate logs if needed;
                await RotateLogsAsync(logDirectory);
            }
            catch (Exception ex)
            {
                SafeFallbackLog($"File logging failed: {ex.Message}", LogLevel.Error);
            }
        }

        private async Task RetryFileWriteAsync(string filePath, string content, int maxRetries = 3)
        {
            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    await File.AppendAllTextAsync(filePath, content);
                    return;
                }
                catch (IOException) when (retry < maxRetries - 1)
                {
                    await Task.Delay(100 * (retry + 1));
                }
            }
        }

        private async Task RotateLogsAsync(string logDirectory)
        {
            try
            {
                var maxAge = _appConfig.Logging?.MaxLogAgeDays ?? 30;
                var maxSize = _appConfig.Logging?.MaxLogSizeMB ?? 100;

                var cutoffDate = DateTime.UtcNow.AddDays(-maxAge);

                foreach (var file in Directory.GetFiles(logDirectory, "log_*.txt"))
                {
                    var fileInfo = new FileInfo(file);

                    // Delete old files;
                    if (fileInfo.CreationTimeUtc < cutoffDate)
                    {
                        File.Delete(file);
                        continue;
                    }

                    // Rotate large files;
                    if (fileInfo.Length > maxSize * 1024 * 1024)
                    {
                        var newName = $"{Path.GetFileNameWithoutExtension(file)}_" +
                                      $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";
                        var newPath = Path.Combine(logDirectory, newName);
                        File.Move(file, newPath);
                    }
                }
            }
            catch (Exception ex)
            {
                SafeFallbackLog($"Log rotation failed: {ex.Message}", LogLevel.Warning);
            }
        }

        private void WriteToConsole(System.Collections.Generic.List<LogEntry> entries)
        {
            foreach (var entry in entries)
            {
                var originalColor = Console.ForegroundColor;

                try
                {
                    Console.ForegroundColor = GetConsoleColor(entry.LogLevel);
                    Console.WriteLine(entry.FormattedMessage);
                }
                finally
                {
                    Console.ForegroundColor = originalColor;
                }
            }
        }

        private ConsoleColor GetConsoleColor(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => ConsoleColor.Gray,
                LogLevel.Debug => ConsoleColor.Blue,
                LogLevel.Information => ConsoleColor.Green,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Critical => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };
        }

        private async Task WriteImmediateLogAsync(LogEntry logEntry)
        {
            try
            {
                await _flushSemaphore.WaitAsync();

                // Immediate write for critical errors;
                if (_appConfig.Logging?.EnableImmediateLogging == true)
                {
                    var immediatePath = Path.Combine(
                        _appConfig.Logging.LogDirectory ?? "Logs",
                        "immediate.log");

                    await File.AppendAllTextAsync(immediatePath,
                        $"[IMMEDIATE] {logEntry.FormattedMessage}{Environment.NewLine}");
                }
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        private void SafeFallbackLog(string message, LogLevel level)
        {
            try
            {
                // Try to write to event log as last resort;
                if (!EventLog.SourceExists("NEDA"))
                    EventLog.CreateEventSource("NEDA", "Application");

                EventLog.WriteEntry("NEDA", message,
                    level >= LogLevel.Error ? EventLogEntryType.Error : EventLogEntryType.Information);
            }
            catch
            {
                // Ultimate fallback - console;
                Console.WriteLine($"[FALLBACK {level}] {message}");
            }
        }

        private void LogInitialization()
        {
            LogInformation("Logger initialized for category: {Category}", _categoryName);
            LogInformation("Log level: {LogLevel}, File logging: {FileLogging}, Directory: {LogDirectory}",
                _appConfig.Logging?.MinimumLevel ?? LogLevel.Information,
                _appConfig.Logging?.EnableFileLogging ?? false,
                _appConfig.Logging?.LogDirectory ?? "Default");
        }

        private void EndScope(string scopeName)
        {
            _logManager.PopScope(scopeName);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Flushes all pending log entries;
        /// </summary>
        public async Task FlushAsync()
        {
            await _flushSemaphore.WaitAsync();

            try
            {
                while (_logQueue.TryDequeue(out var entry))
                {
                    await WriteToFileAsync(new System.Collections.Generic.List<LogEntry> { entry });
                }
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        /// <summary>
        /// Gets the current log file path;
        /// </summary>
        public string GetCurrentLogFilePath()
        {
            var logDirectory = _appConfig.Logging?.LogDirectory ??
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

            return Path.Combine(logDirectory, $"log_{DateTime.UtcNow:yyyyMMdd}.txt");
        }

        /// <summary>
        /// Gets recent log entries for diagnostics;
        /// </summary>
        public async Task<string[]> GetRecentLogsAsync(int maxLines = 1000)
        {
            var logFile = GetCurrentLogFilePath();

            if (!File.Exists(logFile))
                return Array.Empty<string>();

            try
            {
                var lines = await File.ReadAllLinesAsync(logFile);
                return lines.TakeLast(maxLines).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _cancellationTokenSource.Cancel();

                // Flush remaining logs;
                FlushAsync().GetAwaiter().GetResult();

                _cancellationTokenSource.Dispose();
                _flushSemaphore.Dispose();

                // Remove from static dictionary;
                _loggers.TryRemove(_categoryName, out _);
            }

            _disposed = true;
        }

        ~Logger()
        {
            Dispose(false);
        }

        #endregion
    }
}
