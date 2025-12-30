using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace YourNamespace.Utilities.Logging;
{
    /// <summary>
    /// Behavior logger for recording user operations, system behaviors, and other key events;
    /// </summary>
    public class BehaviorLogger : IDisposable
    {
        #region Singleton Pattern;

        private static readonly Lazy<BehaviorLogger> _instance =
            new Lazy<BehaviorLogger>(() => new BehaviorLogger());

        public static BehaviorLogger Instance => _instance.Value;

        #endregion;

        #region Constants;

        private const string DEFAULT_LOG_DIRECTORY = "Logs/Behavior";
        private const string LOG_FILE_PREFIX = "Behavior_";
        private const string LOG_FILE_EXTENSION = ".log";
        private const int MAX_LOG_FILE_SIZE_MB = 10;
        private const int MAX_LOG_FILES = 100;

        #endregion;

        #region Fields;

        private readonly object _lockObject = new object();
        private StreamWriter _logWriter;
        private string _currentLogFilePath;
        private bool _isInitialized;
        private bool _isDisposed;
        private LogLevel _minimumLogLevel = LogLevel.INFO;

        #endregion;

        #region Properties;

        /// <summary>
        /// Directory where log files are saved;
        /// </summary>
        public string LogDirectory { get; private set; }

        /// <summary>
        /// Whether logging is enabled;
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Whether to output logs to console;
        /// </summary>
        public bool OutputToConsole { get; set; }

        /// <summary>
        /// Whether to record logs in files;
        /// </summary>
        public bool OutputToFile { get; set; } = true;

        /// <summary>
        /// Minimum log level to record;
        /// </summary>
        public LogLevel MinimumLogLevel;
        {
            get => _minimumLogLevel;
            set => _minimumLogLevel = value;
        }

        #endregion;

        #region Constructors;

        /// <summary>
        /// Private constructor (Singleton pattern)
        /// </summary>
        private BehaviorLogger()
        {
            Initialize(DEFAULT_LOG_DIRECTORY);
        }

        /// <summary>
        /// Creates a behavior logger with the specified directory;
        /// </summary>
        /// <param name="logDirectory">Log directory</param>
        public BehaviorLogger(string logDirectory)
        {
            Initialize(logDirectory);
        }

        #endregion;

        #region Initialization;

        /// <summary>
        /// Initializes the logger;
        /// </summary>
        /// <param name="logDirectory">Log directory</param>
        public void Initialize(string logDirectory = null)
        {
            if (_isInitialized) return;

            lock (_lockObject)
            {
                if (_isInitialized) return;

                LogDirectory = logDirectory ?? DEFAULT_LOG_DIRECTORY;

                try
                {
                    // Ensure log directory exists;
                    if (!Directory.Exists(LogDirectory))
                    {
                        Directory.CreateDirectory(LogDirectory);
                    }

                    // Clean up old log files;
                    CleanupOldLogFiles();

                    // Create new log file;
                    CreateNewLogFile();

                    _isInitialized = true;

                    LogInfo("BehaviorLogger initialized successfully.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to initialize BehaviorLogger: {ex.Message}");
                    // Fallback: only output to console;
                    OutputToFile = false;
                    OutputToConsole = true;
                }
            }
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Logs an information level message;
        /// </summary>
        /// <param name="action">Action description</param>
        /// <param name="userId">User ID (optional)</param>
        /// <param name="details">Additional details (optional)</param>
        public void LogInfo(string action, string userId = null, Dictionary<string, string> details = null)
        {
            Log(LogLevel.INFO, action, userId, details);
        }

        /// <summary>
        /// Logs a warning level message;
        /// </summary>
        /// <param name="action">Action description</param>
        /// <param name="userId">User ID (optional)</param>
        /// <param name="details">Additional details (optional)</param>
        public void LogWarning(string action, string userId = null, Dictionary<string, string> details = null)
        {
            Log(LogLevel.WARNING, action, userId, details);
        }

        /// <summary>
        /// Logs an error level message;
        /// </summary>
        /// <param name="action">Action description</param>
        /// <param name="userId">User ID (optional)</param>
        /// <param name="details">Additional details (optional)</param>
        /// <param name="exception">Exception object (optional)</param>
        public void LogError(string action, string userId = null,
            Dictionary<string, string> details = null, Exception exception = null)
        {
            var extendedDetails = details ?? new Dictionary<string, string>();

            if (exception != null)
            {
                extendedDetails["Exception"] = exception.Message;
                extendedDetails["StackTrace"] = exception.StackTrace;

                if (exception.InnerException != null)
                {
                    extendedDetails["InnerException"] = exception.InnerException.Message;
                }
            }

            Log(LogLevel.ERROR, action, userId, extendedDetails);
        }

        /// <summary>
        /// Logs user login behavior;
        /// </summary>
        /// <param name="userId">User ID</param>
        /// <param name="username">Username</param>
        /// <param name="ipAddress">IP address</param>
        /// <param name="status">Login status (success/failure)</param>
        /// <param name="reason">Failure reason (optional)</param>
        public void LogLogin(string userId, string username, string ipAddress,
            string status, string reason = null)
        {
            var details = new Dictionary<string, string>
            {
                { "Username", username },
                { "IPAddress", ipAddress },
                { "Status", status }
            };

            if (!string.IsNullOrEmpty(reason))
            {
                details["Reason"] = reason;
            }

            LogInfo("UserLogin", userId, details);
        }

        /// <summary>
        /// Logs user operation behavior;
        /// </summary>
        /// <param name="userId">User ID</param>
        /// <param name="operation">Operation name</param>
        /// <param name="target">Operation target</param>
        /// <param name="result">Operation result</param>
        /// <param name="additionalInfo">Additional information (optional)</param>
        public void LogOperation(string userId, string operation, string target,
            string result, Dictionary<string, string> additionalInfo = null)
        {
            var details = new Dictionary<string, string>
            {
                { "Operation", operation },
                { "Target", target },
                { "Result", result }
            };

            if (additionalInfo != null)
            {
                foreach (var kvp in additionalInfo)
                {
                    details[kvp.Key] = kvp.Value;
                }
            }

            LogInfo("UserOperation", userId, details);
        }

        /// <summary>
        /// Logs system behavior;
        /// </summary>
        /// <param name="component">Component name</param>
        /// <param name="action">Action description</param>
        /// <param name="status">Status</param>
        /// <param name="details">Additional details (optional)</param>
        public void LogSystemBehavior(string component, string action,
            string status, Dictionary<string, string> details = null)
        {
            var extendedDetails = details ?? new Dictionary<string, string>();
            extendedDetails["Component"] = component;
            extendedDetails["Action"] = action;
            extendedDetails["Status"] = status;

            LogInfo("SystemBehavior", null, extendedDetails);
        }

        /// <summary>
        /// Logs asynchronously;
        /// </summary>
        public async Task LogAsync(LogLevel level, string action, string userId = null,
            Dictionary<string, string> details = null)
        {
            await Task.Run(() => Log(level, action, userId, details));
        }

        /// <summary>
        /// Gets recent logs;
        /// </summary>
        /// <param name="count">Number of log entries</param>
        /// <returns>List of log entries</returns>
        public List<string> GetRecentLogs(int count = 100)
        {
            var logs = new List<string>();

            try
            {
                lock (_lockObject)
                {
                    if (File.Exists(_currentLogFilePath))
                    {
                        var lines = File.ReadAllLines(_currentLogFilePath);
                        logs.AddRange(lines.TakeLast(count));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read logs: {ex.Message}");
            }

            return logs;
        }

        /// <summary>
        /// Clears all logs;
        /// </summary>
        public void ClearAllLogs()
        {
            try
            {
                lock (_lockObject)
                {
                    if (Directory.Exists(LogDirectory))
                    {
                        var logFiles = Directory.GetFiles(LogDirectory,
                            $"{LOG_FILE_PREFIX}*{LOG_FILE_EXTENSION}");

                        foreach (var file in logFiles)
                        {
                            File.Delete(file);
                        }

                        // Create new log file;
                        CreateNewLogFile();

                        LogInfo("All logs have been cleared.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear logs: {ex.Message}");
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Core logging method;
        /// </summary>
        private void Log(LogLevel level, string action, string userId,
            Dictionary<string, string> details)
        {
            if (!IsEnabled || level < _minimumLogLevel) return;

            try
            {
                Initialize(); // Ensure initialized;

                var timestamp = DateTime.Now;
                var logEntry = BuildLogEntry(level, timestamp, action, userId, details);

                // Output to console;
                if (OutputToConsole)
                {
                    WriteToConsole(level, logEntry);
                }

                // Output to file;
                if (OutputToFile && _logWriter != null)
                {
                    WriteToFile(logEntry);
                }

                // Check if log file needs to be rolled over;
                CheckAndRollLogFile();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to log behavior: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a log entry string;
        /// </summary>
        private string BuildLogEntry(LogLevel level, DateTime timestamp,
            string action, string userId, Dictionary<string, string> details)
        {
            var sb = new StringBuilder();

            // Timestamp;
            sb.Append($"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] ");

            // Log level;
            sb.Append($"[{level}] ");

            // Action;
            sb.Append($"Action: {action} ");

            // User ID (if any)
            if (!string.IsNullOrEmpty(userId))
            {
                sb.Append($"User: {userId} ");
            }

            // Details;
            if (details != null && details.Count > 0)
            {
                sb.Append("Details: {");
                bool isFirst = true;

                foreach (var kvp in details)
                {
                    if (!isFirst) sb.Append(", ");
                    sb.Append($"{kvp.Key}: {kvp.Value}");
                    isFirst = false;
                }

                sb.Append("}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Writes log entry to console;
        /// </summary>
        private void WriteToConsole(LogLevel level, string logEntry)
        {
            var originalColor = Console.ForegroundColor;

            try
            {
                Console.ForegroundColor = GetConsoleColor(level);
                Console.WriteLine(logEntry);
            }
            finally
            {
                Console.ForegroundColor = originalColor;
            }
        }

        /// <summary>
        /// Gets console color based on log level;
        /// </summary>
        private ConsoleColor GetConsoleColor(LogLevel level)
        {
            return level switch;
            {
                LogLevel.INFO => ConsoleColor.White,
                LogLevel.WARNING => ConsoleColor.Yellow,
                LogLevel.ERROR => ConsoleColor.Red,
                _ => ConsoleColor.Gray;
            };
        }

        /// <summary>
        /// Writes log entry to file;
        /// </summary>
        private void WriteToFile(string logEntry)
        {
            lock (_lockObject)
            {
                if (_logWriter != null)
                {
                    _logWriter.WriteLine(logEntry);
                    _logWriter.Flush();
                }
            }
        }

        /// <summary>
        /// Creates a new log file;
        /// </summary>
        private void CreateNewLogFile()
        {
            if (!OutputToFile) return;

            lock (_lockObject)
            {
                try
                {
                    // Close existing writer;
                    _logWriter?.Dispose();

                    // Generate new filename;
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    _currentLogFilePath = Path.Combine(LogDirectory,
                        $"{LOG_FILE_PREFIX}{timestamp}{LOG_FILE_EXTENSION}");

                    // Create new writer;
                    _logWriter = new StreamWriter(_currentLogFilePath, true, Encoding.UTF8)
                    {
                        AutoFlush = false // We manually flush for performance control;
                    };

                    // Write file header;
                    _logWriter.WriteLine($"# Behavior Log File");
                    _logWriter.WriteLine($"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _logWriter.WriteLine($"# Version: {GetType().Assembly.GetName().Version}");
                    _logWriter.WriteLine();
                    _logWriter.Flush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to create log file: {ex.Message}");
                    _logWriter = null;
                }
            }
        }

        /// <summary>
        /// Checks if log file needs to be rolled over and creates a new one if needed;
        /// </summary>
        private void CheckAndRollLogFile()
        {
            if (!OutputToFile || _logWriter == null) return;

            try
            {
                lock (_lockObject)
                {
                    var fileInfo = new FileInfo(_currentLogFilePath);

                    // If file exceeds maximum size, create new log file;
                    if (fileInfo.Exists && fileInfo.Length > MAX_LOG_FILE_SIZE_MB * 1024 * 1024)
                    {
                        LogInfo("Log file reached maximum size, creating new log file.");
                        CreateNewLogFile();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to check log file size: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up old log files;
        /// </summary>
        private void CleanupOldLogFiles()
        {
            try
            {
                var logFiles = Directory.GetFiles(LogDirectory,
                    $"{LOG_FILE_PREFIX}*{LOG_FILE_EXTENSION}")
                    .OrderByDescending(f => f)
                    .ToList();

                // Delete old files exceeding the maximum count;
                if (logFiles.Count > MAX_LOG_FILES)
                {
                    foreach (var oldFile in logFiles.Skip(MAX_LOG_FILES))
                    {
                        File.Delete(oldFile);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to cleanup old log files: {ex.Message}");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    lock (_lockObject)
                    {
                        _logWriter?.Dispose();
                        _logWriter = null;
                    }
                }

                _isDisposed = true;
            }
        }

        ~BehaviorLogger()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Helper Enums and Classes;

    /// <summary>
    /// Log levels;
    /// </summary>
    public enum LogLevel;
    {
        /// <summary>
        /// Information level;
        /// </summary>
        INFO = 1,

        /// <summary>
        /// Warning level;
        /// </summary>
        WARNING = 2,

        /// <summary>
        /// Error level;
        /// </summary>
        ERROR = 3;
    }

    /// <summary>
    /// Behavior log item model;
    /// </summary>
    public class BehaviorLogItem;
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Action { get; set; }
        public string UserId { get; set; }
        public Dictionary<string, string> Details { get; set; }

        public BehaviorLogItem()
        {
            Details = new Dictionary<string, string>();
        }

        public override string ToString()
        {
            var detailsStr = string.Join(", ",
                Details.Select(kvp => $"{kvp.Key}: {kvp.Value}"));

            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] " +
                   $"Action: {Action} User: {UserId ?? "N/A"} " +
                   $"Details: {detailsStr}";
        }
    }

    #endregion;
}
