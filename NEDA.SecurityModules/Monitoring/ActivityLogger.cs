using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Logging;
using NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;

namespace NEDA.SecurityModules.Monitoring;
{
    /// <summary>
    /// Activity severity levels;
    /// </summary>
    public enum ActivitySeverity;
    {
        Information,
        Warning,
        Critical,
        Security,
        Performance,
        Debug;
    }

    /// <summary>
    /// Activity categories for classification;
    /// </summary>
    public enum ActivityCategory;
    {
        Authentication,
        Authorization,
        AccessControl,
        FileSystem,
        Network,
        Process,
        Registry,
        System,
        Application,
        Database,
        Security,
        Compliance,
        Audit,
        UserActivity,
        SystemHealth;
    }

    /// <summary>
    /// Activity log entry
    /// </summary>
    public class ActivityLogEntry
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public ActivitySeverity Severity { get; set; } = ActivitySeverity.Information;
        public ActivityCategory Category { get; set; }

        public string Source { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();

        public string Resource { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;

        public long DurationMs { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public string ParentActivityId { get; set; } = string.Empty;

        public bool IsSensitive { get; set; }
        public bool IsEncrypted { get; set; }
        public string Signature { get; set; } = string.Empty;

        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Activity log query parameters;
    /// </summary>
    public class ActivityLogQuery;
    {
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public List<ActivitySeverity> Severities { get; set; } = new List<ActivitySeverity>();
        public List<ActivityCategory> Categories { get; set; } = new List<ActivityCategory>();
        public string UserId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Resource { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public Dictionary<string, object> Filters { get; set; } = new Dictionary<string, object>();
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 100;
        public string SortBy { get; set; } = "Timestamp";
        public bool SortDescending { get; set; } = true;
        public bool IncludeSensitive { get; set; } = false;
    }

    /// <summary>
    /// Activity log statistics;
    /// </summary>
    public class ActivityLogStatistics;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int TotalEntries { get; set; }
        public Dictionary<ActivitySeverity, int> SeverityCounts { get; set; } = new Dictionary<ActivitySeverity, int>();
        public Dictionary<ActivityCategory, int> CategoryCounts { get; set; } = new Dictionary<ActivityCategory, int>();
        public Dictionary<string, int> UserActivityCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> SourceCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> OperationCounts { get; set; } = new Dictionary<string, int>();
        public double AverageDurationMs { get; set; }
        public int FailedOperations { get; set; }
        public int SuccessfulOperations { get; set; }
        public Dictionary<string, int> ErrorCodes { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Activity log configuration;
    /// </summary>
    public class ActivityLoggerConfiguration;
    {
        public string LogDirectory { get; set; } = "Logs/Activities";
        public string ArchiveDirectory { get; set; } = "Logs/Archives";
        public string DatabaseConnectionString { get; set; } = string.Empty;

        public bool EnableFileLogging { get; set; } = true;
        public bool EnableDatabaseLogging { get; set; } = false;
        public bool EnableConsoleLogging { get; set; } = false;
        public bool EnableEventLog { get; set; } = false;

        public int MaxFileSizeMB { get; set; } = 100;
        public int MaxArchiveFiles { get; set; } = 50;
        public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(30);

        public bool EnableRealTimeStreaming { get; set; } = true;
        public bool EnableCompression { get; set; } = true;
        public bool EnableEncryption { get; set; } = false;
        public string EncryptionKey { get; set; } = string.Empty;

        public int BatchSize { get; set; } = 100;
        public TimeSpan BatchInterval { get; set; } = TimeSpan.FromSeconds(5);
        public int MaxQueueSize { get; set; } = 10000;

        public List<ActivitySeverity> LogLevels { get; set; } = new List<ActivitySeverity>
        {
            ActivitySeverity.Security,
            ActivitySeverity.Critical,
            ActivitySeverity.Warning;
        };

        public Dictionary<ActivityCategory, bool> EnabledCategories { get; set; } =
            new Dictionary<ActivityCategory, bool>();

        public bool EnablePerformanceCounters { get; set; } = true;
        public bool EnableAnomalyDetection { get; set; } = true;
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Activity batch for efficient processing;
    /// </summary>
    private class ActivityBatch;
    {
        public List<ActivityLogEntry> Entries { get; } = new List<ActivityLogEntry>();
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public bool IsProcessing { get; set; }
    }

    /// <summary>
    /// Real-time activity monitoring and logging system;
    /// Provides comprehensive activity tracking with performance monitoring and anomaly detection;
    /// </summary>
    public class ActivityLogger : IActivityLogger, IDisposable;
    {
        private readonly ILogger<ActivityLogger> _logger;
        private readonly CryptoEngine _cryptoEngine;
        private readonly IAuditLogger _auditLogger;
        private readonly IOptions<ActivityLoggerConfiguration> _configuration;

        private readonly ConcurrentQueue<ActivityLogEntry> _activityQueue;
        private readonly ConcurrentDictionary<string, ActivityLogEntry> _activityCache;
        private readonly ConcurrentDictionary<string, Stopwatch> _activityTimers;
        private readonly ConcurrentDictionary<string, List<ActivityLogEntry>> _userSessions;

        private readonly Timer _batchTimer;
        private readonly Timer _cleanupTimer;
        private readonly Timer _statisticsTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;

        private readonly SemaphoreSlim _batchSemaphore;
        private readonly ReaderWriterLockSlim _cacheLock;

        private bool _isDisposed;
        private bool _isInitialized;
        private long _totalActivitiesLogged;
        private long _activitiesInQueue;
        private DateTime _startTime = DateTime.UtcNow;

        private readonly JsonSerializerSettings _jsonSettings;
        private readonly object _fileLock = new object();
        private readonly string _currentLogFile;
        private readonly PerformanceCounter _queueCounter;
        private readonly PerformanceCounter _throughputCounter;

        /// <summary>
        /// Event raised when activity is logged;
        /// </summary>
        public event EventHandler<ActivityLogEntry> ActivityLogged;

        /// <summary>
        /// Event raised when activity batch is processed;
        /// </summary>
        public event EventHandler<int> BatchProcessed;

        /// <summary>
        /// Event raised when anomaly is detected;
        /// </summary>
        public event EventHandler<ActivityLogEntry> AnomalyDetected;

        /// <summary>
        /// Event raised when statistics are updated;
        /// </summary>
        public event EventHandler<ActivityLogStatistics> StatisticsUpdated;

        /// <summary>
        /// Initializes a new instance of ActivityLogger;
        /// </summary>
        public ActivityLogger(
            ILogger<ActivityLogger> logger,
            CryptoEngine cryptoEngine,
            IAuditLogger auditLogger,
            IOptions<ActivityLoggerConfiguration> configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _activityQueue = new ConcurrentQueue<ActivityLogEntry>();
            _activityCache = new ConcurrentDictionary<string, ActivityLogEntry>();
            _activityTimers = new ConcurrentDictionary<string, Stopwatch>();
            _userSessions = new ConcurrentDictionary<string, List<ActivityLogEntry>>();

            _cancellationTokenSource = new CancellationTokenSource();
            _batchSemaphore = new SemaphoreSlim(1, 1);
            _cacheLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

            _jsonSettings = new JsonSerializerSettings;
            {
                Formatting = Newtonsoft.Json.Formatting.None,
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
            };

            // Initialize log file;
            _currentLogFile = GetCurrentLogFilePath();
            EnsureLogDirectory();

            // Initialize timers;
            _batchTimer = new Timer(ProcessActivityBatch, null,
                _configuration.Value.BatchInterval, _configuration.Value.BatchInterval);

            _cleanupTimer = new Timer(CleanupOldActivities, null,
                _configuration.Value.CleanupInterval, _configuration.Value.CleanupInterval);

            _statisticsTimer = new Timer(UpdateStatistics, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            // Initialize performance counters if enabled;
            if (_configuration.Value.EnablePerformanceCounters)
            {
                try
                {
                    _queueCounter = new PerformanceCounter("NEDA Activity Logger", "Queue Size", false);
                    _throughputCounter = new PerformanceCounter("NEDA Activity Logger", "Activities/Sec", false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize performance counters");
                }
            }

            _logger.LogInformation("ActivityLogger initialized with configuration: {@Config}",
                _configuration.Value);
        }

        /// <summary>
        /// Initializes the activity logger;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Starting ActivityLogger initialization...");

                // Initialize database if enabled;
                if (_configuration.Value.EnableDatabaseLogging)
                {
                    await InitializeDatabaseAsync(cancellationToken);
                }

                // Load existing sessions if any;
                await LoadExistingSessionsAsync(cancellationToken);

                _isInitialized = true;
                _logger.LogInformation("ActivityLogger initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize ActivityLogger");
                throw;
            }
        }

        /// <summary>
        /// Logs an activity entry
        /// </summary>
        public async Task LogActivityAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            // Check if activity should be logged based on configuration;
            if (!ShouldLogActivity(entry))
                return;

            try
            {
                // Start timer for performance measurement;
                var stopwatch = Stopwatch.StartNew();

                // Generate correlation ID if not provided;
                if (string.IsNullOrEmpty(entry.CorrelationId))
                {
                    entry.CorrelationId = Guid.NewGuid().ToString();
                }

                // Add system metadata;
                entry.Metadata["MachineName"] = Environment.MachineName;
                entry.Metadata["ProcessId"] = Process.GetCurrentProcess().Id;
                entry.Metadata["ThreadId"] = Thread.CurrentThread.ManagedThreadId;

                // Encrypt sensitive data if needed;
                if (entry.IsSensitive && _configuration.Value.EnableEncryption)
                {
                    await EncryptSensitiveDataAsync(entry, cancellationToken);
                }

                // Sign the activity for integrity;
                await SignActivityAsync(entry, cancellationToken);

                // Add to queue for batch processing;
                if (_activityQueue.Count < _configuration.Value.MaxQueueSize)
                {
                    _activityQueue.Enqueue(entry);
                    Interlocked.Increment(ref _activitiesInQueue);

                    // Update performance counters;
                    UpdatePerformanceCounters();

                    // Cache recent activities;
                    CacheActivity(entry);

                    // Update user session;
                    UpdateUserSession(entry);

                    // Check for anomalies;
                    CheckForAnomalies(entry);

                    // Raise event;
                    OnActivityLogged(entry);

                    stopwatch.Stop();
                    entry.DurationMs = stopwatch.ElapsedMilliseconds;

                    _logger.LogDebug("Activity logged: {Operation} by {User}",
                        entry.Operation, entry.UserName);
                }
                else;
                {
                    _logger.LogWarning("Activity queue is full. Dropping activity: {Operation}", entry.Operation);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log activity: {Operation}", entry.Operation);
                throw;
            }
        }

        /// <summary>
        /// Logs an activity with parameters;
        /// </summary>
        public async Task LogActivityAsync(
            ActivitySeverity severity,
            ActivityCategory category,
            string operation,
            string description,
            string userId = null,
            string userName = null,
            Dictionary<string, object> details = null,
            CancellationToken cancellationToken = default)
        {
            var entry = new ActivityLogEntry
            {
                Severity = severity,
                Category = category,
                Operation = operation,
                Description = description,
                UserId = userId ?? string.Empty,
                UserName = userName ?? string.Empty,
                Source = GetCallingSource(),
                Timestamp = DateTime.UtcNow;
            };

            if (details != null)
            {
                entry.Details = details;
            }

            await LogActivityAsync(entry, cancellationToken);
        }

        /// <summary>
        /// Starts timing an activity;
        /// </summary>
        public string StartActivity(
            string operation,
            string userId = null,
            string userName = null,
            ActivityCategory category = ActivityCategory.Application)
        {
            var activityId = Guid.NewGuid().ToString();
            var stopwatch = Stopwatch.StartNew();

            _activityTimers[activityId] = stopwatch;

            var entry = new ActivityLogEntry
            {
                Id = activityId,
                Operation = operation,
                UserId = userId ?? string.Empty,
                UserName = userName ?? string.Empty,
                Category = category,
                Source = GetCallingSource(),
                Result = "Started"
            };

            CacheActivity(entry);

            return activityId;
        }

        /// <summary>
        /// Completes a timed activity;
        /// </summary>
        public async Task CompleteActivityAsync(
            string activityId,
            string result = "Completed",
            Dictionary<string, object> details = null,
            CancellationToken cancellationToken = default)
        {
            if (!_activityTimers.TryRemove(activityId, out var stopwatch))
            {
                _logger.LogWarning("Activity not found: {ActivityId}", activityId);
                return;
            }

            stopwatch.Stop();

            if (_activityCache.TryGetValue(activityId, out var entry))
            {
                entry.Result = result;
                entry.DurationMs = stopwatch.ElapsedMilliseconds;

                if (details != null)
                {
                    entry.Details = details;
                }

                await LogActivityAsync(entry, cancellationToken);

                // Remove from cache after logging;
                _activityCache.TryRemove(activityId, out _);
            }
        }

        /// <summary>
        /// Queries activity logs;
        /// </summary>
        public async Task<List<ActivityLogEntry>> QueryActivitiesAsync(
            ActivityLogQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            try
            {
                var results = new List<ActivityLogEntry>();

                // Query from database if enabled;
                if (_configuration.Value.EnableDatabaseLogging)
                {
                    var dbResults = await QueryDatabaseAsync(query, cancellationToken);
                    results.AddRange(dbResults);
                }

                // Query from file logs;
                if (_configuration.Value.EnableFileLogging)
                {
                    var fileResults = await QueryFileLogsAsync(query, cancellationToken);
                    results.AddRange(fileResults);
                }

                // Apply in-memory filtering for cache;
                var cacheResults = QueryCache(query);
                results.AddRange(cacheResults);

                // Sort and paginate;
                return SortAndPaginate(results, query);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query activities");
                throw;
            }
        }

        /// <summary>
        /// Gets activity statistics;
        /// </summary>
        public async Task<ActivityLogStatistics> GetStatisticsAsync(
            DateTime? startTime = null,
            DateTime? endTime = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var stats = new ActivityLogStatistics;
                {
                    PeriodStart = startTime ?? DateTime.UtcNow.AddDays(-1),
                    PeriodEnd = endTime ?? DateTime.UtcNow;
                };

                var query = new ActivityLogQuery;
                {
                    StartTime = stats.PeriodStart,
                    EndTime = stats.PeriodEnd;
                };

                var activities = await QueryActivitiesAsync(query, cancellationToken);

                stats.TotalEntries = activities.Count;

                // Calculate severity counts;
                foreach (var severity in Enum.GetValues(typeof(ActivitySeverity)).Cast<ActivitySeverity>())
                {
                    stats.SeverityCounts[severity] = activities.Count(a => a.Severity == severity);
                }

                // Calculate category counts;
                foreach (var category in Enum.GetValues(typeof(ActivityCategory)).Cast<ActivityCategory>())
                {
                    stats.CategoryCounts[category] = activities.Count(a => a.Category == category);
                }

                // Calculate user activity counts;
                stats.UserActivityCounts = activities;
                    .Where(a => !string.IsNullOrEmpty(a.UserId))
                    .GroupBy(a => a.UserId)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Calculate source counts;
                stats.SourceCounts = activities;
                    .Where(a => !string.IsNullOrEmpty(a.Source))
                    .GroupBy(a => a.Source)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Calculate operation counts;
                stats.OperationCounts = activities;
                    .Where(a => !string.IsNullOrEmpty(a.Operation))
                    .GroupBy(a => a.Operation)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Calculate durations;
                var activitiesWithDuration = activities.Where(a => a.DurationMs > 0).ToList();
                if (activitiesWithDuration.Count > 0)
                {
                    stats.AverageDurationMs = activitiesWithDuration.Average(a => a.DurationMs);
                }

                // Calculate success/failure rates;
                stats.SuccessfulOperations = activities.Count(a =>
                    a.Result != null && a.Result.Contains("success", StringComparison.OrdinalIgnoreCase));
                stats.FailedOperations = activities.Count(a =>
                    a.Result != null && a.Result.Contains("fail", StringComparison.OrdinalIgnoreCase));

                // Update event;
                OnStatisticsUpdated(stats);

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get statistics");
                throw;
            }
        }

        /// <summary>
        /// Gets activities for a user session;
        /// </summary>
        public List<ActivityLogEntry> GetSessionActivities(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            return _userSessions.TryGetValue(sessionId, out var activities)
                ? activities.ToList()
                : new List<ActivityLogEntry>();
        }

        /// <summary>
        /// Exports activities to file;
        /// </summary>
        public async Task<string> ExportActivitiesAsync(
            ActivityLogQuery query,
            string format = "json",
            CancellationToken cancellationToken = default)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            try
            {
                var activities = await QueryActivitiesAsync(query, cancellationToken);

                return format.ToLowerInvariant() switch;
                {
                    "json" => ExportToJson(activities),
                    "csv" => ExportToCsv(activities),
                    "xml" => ExportToXml(activities),
                    _ => throw new NotSupportedException($"Export format not supported: {format}")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export activities");
                throw;
            }
        }

        /// <summary>
        /// Clears activity logs;
        /// </summary>
        public async Task ClearLogsAsync(
            DateTime? beforeDate = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var cutoffDate = beforeDate ?? DateTime.UtcNow.AddDays(-_configuration.Value.RetentionPeriod.Days);

                // Clear from database;
                if (_configuration.Value.EnableDatabaseLogging)
                {
                    await ClearDatabaseLogsAsync(cutoffDate, cancellationToken);
                }

                // Clear from file logs;
                if (_configuration.Value.EnableFileLogging)
                {
                    await ClearFileLogsAsync(cutoffDate, cancellationToken);
                }

                // Clear from cache;
                ClearCache(cutoffDate);

                _logger.LogInformation("Cleared activity logs before {CutoffDate}", cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear activity logs");
                throw;
            }
        }

        /// <summary>
        /// Gets performance metrics;
        /// </summary>
        public Dictionary<string, object> GetMetrics()
        {
            var metrics = new Dictionary<string, object>
            {
                ["TotalActivitiesLogged"] = _totalActivitiesLogged,
                ["ActivitiesInQueue"] = _activitiesInQueue,
                ["ActivityCacheSize"] = _activityCache.Count,
                ["UserSessions"] = _userSessions.Count,
                ["ActiveTimers"] = _activityTimers.Count,
                ["Uptime"] = DateTime.UtcNow - _startTime,
                ["QueueCapacity"] = _configuration.Value.MaxQueueSize,
                ["QueueUtilization"] = (double)_activitiesInQueue / _configuration.Value.MaxQueueSize * 100,
                ["IsInitialized"] = _isInitialized;
            };

            // Add throughput calculation;
            var uptimeSeconds = (DateTime.UtcNow - _startTime).TotalSeconds;
            if (uptimeSeconds > 0)
            {
                metrics["ActivitiesPerSecond"] = _totalActivitiesLogged / uptimeSeconds;
            }

            return metrics;
        }

        #region Private Helper Methods;

        /// <summary>
        /// Checks if activity should be logged based on configuration;
        /// </summary>
        private bool ShouldLogActivity(ActivityLogEntry entry)
        {
            var config = _configuration.Value;

            // Check severity level;
            if (!config.LogLevels.Contains(entry.Severity))
                return false;

            // Check category filter;
            if (config.EnabledCategories.TryGetValue(entry.Category, out var isEnabled) && !isEnabled)
                return false;

            return true;
        }

        /// <summary>
        /// Gets calling source for activity;
        /// </summary>
        private string GetCallingSource()
        {
            try
            {
                var stackTrace = new StackTrace(skipFrames: 2, fNeedFileInfo: false);
                var frame = stackTrace.GetFrame(0);
                var method = frame?.GetMethod();

                return method != null;
                    ? $"{method.DeclaringType?.FullName}.{method.Name}"
                    : "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Encrypts sensitive data in activity;
        /// </summary>
        private async Task EncryptSensitiveDataAsync(ActivityLogEntry entry, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(_configuration.Value.EncryptionKey))
                    return;

                // Encrypt details;
                if (entry.Details.Count > 0)
                {
                    var detailsJson = Newtonsoft.Json.JsonConvert.SerializeObject(entry.Details);
                    var encryptedDetails = await _cryptoEngine.EncryptAsync(
                        detailsJson,
                        _configuration.Value.EncryptionKey,
                        cancellationToken);

                    entry.Details = new Dictionary<string, object>
                    {
                        ["EncryptedData"] = encryptedDetails;
                    };
                }

                // Encrypt description if sensitive;
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    entry.Description = await _cryptoEngine.EncryptAsync(
                        entry.Description,
                        _configuration.Value.EncryptionKey,
                        cancellationToken);
                }

                entry.IsEncrypted = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to encrypt sensitive data");
                // Continue without encryption rather than failing;
            }
        }

        /// <summary>
        /// Signs activity for integrity;
        /// </summary>
        private async Task SignActivityAsync(ActivityLogEntry entry, CancellationToken cancellationToken)
        {
            try
            {
                var dataToSign = $"{entry.Id}|{entry.Timestamp:O}|{entry.Operation}|{entry.UserId}";
                entry.Signature = await _cryptoEngine.SignDataAsync(
                    dataToSign,
                    "SHA256",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sign activity");
                // Continue without signature rather than failing;
            }
        }

        /// <summary>
        /// Caches activity for fast retrieval;
        /// </summary>
        private void CacheActivity(ActivityLogEntry entry)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                // Limit cache size;
                if (_activityCache.Count >= 10000)
                {
                    var oldest = _activityCache.OrderBy(kv => kv.Value.Timestamp).First();
                    _activityCache.TryRemove(oldest.Key, out _);
                }

                _activityCache[entry.Id] = entry
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Updates user session tracking;
        /// </summary>
        private void UpdateUserSession(ActivityLogEntry entry)
        {
            if (string.IsNullOrEmpty(entry.SessionId))
                return;

            var sessionActivities = _userSessions.GetOrAdd(entry.SessionId, _ => new List<ActivityLogEntry>());

            // Limit session activities to prevent memory leak;
            if (sessionActivities.Count >= 1000)
            {
                sessionActivities.RemoveAt(0);
            }

            sessionActivities.Add(entry);
        }

        /// <summary>
        /// Checks for anomalous activities;
        /// </summary>
        private void CheckForAnomalies(ActivityLogEntry entry)
        {
            if (!_configuration.Value.EnableAnomalyDetection)
                return;

            try
            {
                // Check for rapid succession of failed logins;
                if (entry.Category == ActivityCategory.Authentication &&
                    entry.Result?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var recentFailures = _activityCache.Values;
                        .Where(a => a.Category == ActivityCategory.Authentication &&
                                   a.Result?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true &&
                                   a.Timestamp > DateTime.UtcNow.AddMinutes(-5) &&
                                   a.UserId == entry.UserId)
                        .Count();

                    if (recentFailures > 5)
                    {
                        _logger.LogWarning("Multiple failed login attempts detected for user {User}", entry.UserId);
                        OnAnomalyDetected(entry);
                    }
                }

                // Check for unusual time patterns;
                if (entry.Timestamp.Hour >= 22 || entry.Timestamp.Hour <= 5)
                {
                    if (entry.Category == ActivityCategory.System ||
                        entry.Category == ActivityCategory.Database)
                    {
                        _logger.LogWarning("Unusual activity time detected: {Operation} at {Time}",
                            entry.Operation, entry.Timestamp);
                        OnAnomalyDetected(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check for anomalies");
            }
        }

        /// <summary>
        /// Processes activity batch;
        /// </summary>
        private async void ProcessActivityBatch(object state)
        {
            if (!await _batchSemaphore.WaitAsync(0))
                return;

            try
            {
                var batch = new ActivityBatch();
                var batchSize = Math.Min(_configuration.Value.BatchSize, _activityQueue.Count);

                for (int i = 0; i < batchSize; i++)
                {
                    if (_activityQueue.TryDequeue(out var entry))
                    {
                        batch.Entries.Add(entry);
                        Interlocked.Decrement(ref _activitiesInQueue);
                    }
                }

                if (batch.Entries.Count > 0)
                {
                    batch.IsProcessing = true;

                    // Process batch asynchronously;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessBatchAsync(batch, _cancellationTokenSource.Token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to process activity batch");
                        }
                    });
                }
            }
            finally
            {
                _batchSemaphore.Release();
            }
        }

        /// <summary>
        /// Processes a batch of activities;
        /// </summary>
        private async Task ProcessBatchAsync(ActivityBatch batch, CancellationToken cancellationToken)
        {
            try
            {
                // Write to file;
                if (_configuration.Value.EnableFileLogging)
                {
                    await WriteToFileAsync(batch.Entries, cancellationToken);
                }

                // Write to database;
                if (_configuration.Value.EnableDatabaseLogging)
                {
                    await WriteToDatabaseAsync(batch.Entries, cancellationToken);
                }

                // Write to console;
                if (_configuration.Value.EnableConsoleLogging)
                {
                    WriteToConsole(batch.Entries);
                }

                // Write to event log;
                if (_configuration.Value.EnableEventLog)
                {
                    WriteToEventLog(batch.Entries);
                }

                // Update counters;
                Interlocked.Add(ref _totalActivitiesLogged, batch.Entries.Count);

                // Raise batch processed event;
                OnBatchProcessed(batch.Entries.Count);

                _logger.LogDebug("Processed batch of {Count} activities", batch.Entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process activity batch");

                // Re-queue failed entries;
                foreach (var entry in batch.Entries)
                {
                    if (_activityQueue.Count < _configuration.Value.MaxQueueSize)
                    {
                        _activityQueue.Enqueue(entry);
                        Interlocked.Increment(ref _activitiesInQueue);
                    }
                }
            }
            finally
            {
                batch.IsProcessing = false;
            }
        }

        /// <summary>
        /// Writes activities to file;
        /// </summary>
        private async Task WriteToFileAsync(List<ActivityLogEntry> entries, CancellationToken cancellationToken)
        {
            try
            {
                lock (_fileLock)
                {
                    // Check file size;
                    var fileInfo = new FileInfo(_currentLogFile);
                    if (fileInfo.Exists && fileInfo.Length > _configuration.Value.MaxFileSizeMB * 1024 * 1024)
                    {
                        ArchiveCurrentLogFile();
                    }

                    // Write entries;
                    using var writer = new StreamWriter(_currentLogFile, true, Encoding.UTF8);
                    foreach (var entry in entries)
                    {
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(entry, _jsonSettings);
                        writer.WriteLine(json);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write activities to file");
                throw;
            }
        }

        /// <summary>
        /// Writes activities to database;
        /// </summary>
        private async Task WriteToDatabaseAsync(List<ActivityLogEntry> entries, CancellationToken cancellationToken)
        {
            // Database implementation would go here;
            // For now, simulate database write;
            await Task.Delay(10, cancellationToken);
        }

        /// <summary>
        /// Writes activities to console;
        /// </summary>
        private void WriteToConsole(List<ActivityLogEntry> entries)
        {
            foreach (var entry in entries)
            {
                var color = entry.Severity switch;
                {
                    ActivitySeverity.Critical => ConsoleColor.Red,
                    ActivitySeverity.Security => ConsoleColor.Yellow,
                    ActivitySeverity.Warning => ConsoleColor.Magenta,
                    _ => ConsoleColor.Gray;
                };

                Console.ForegroundColor = color;
                Console.WriteLine($"[{entry.Timestamp:HH:mm:ss}] [{entry.Severity}] {entry.Operation}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Writes activities to event log;
        /// </summary>
        private void WriteToEventLog(List<ActivityLogEntry> entries)
        {
            // Event log implementation would go here;
        }

        /// <summary>
        /// Queries database for activities;
        /// </summary>
        private async Task<List<ActivityLogEntry>> QueryDatabaseAsync(
            ActivityLogQuery query,
            CancellationToken cancellationToken)
        {
            // Database query implementation would go here;
            // For now, return empty list;
            return await Task.FromResult(new List<ActivityLogEntry>());
        }

        /// <summary>
        /// Queries file logs for activities;
        /// </summary>
        private async Task<List<ActivityLogEntry>> QueryFileLogsAsync(
            ActivityLogQuery query,
            CancellationToken cancellationToken)
        {
            var results = new List<ActivityLogEntry>();

            try
            {
                var logFiles = GetLogFiles();

                foreach (var logFile in logFiles)
                {
                    var fileEntries = await ReadLogFileAsync(logFile, cancellationToken);
                    var filteredEntries = FilterEntries(fileEntries, query);
                    results.AddRange(filteredEntries);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query file logs");
            }

            return results;
        }

        /// <summary>
        /// Queries cache for activities;
        /// </summary>
        private List<ActivityLogEntry> QueryCache(ActivityLogQuery query)
        {
            _cacheLock.EnterReadLock();
            try
            {
                var entries = _activityCache.Values.ToList();
                return FilterEntries(entries, query);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Filters entries based on query;
        /// </summary>
        private List<ActivityLogEntry> FilterEntries(List<ActivityLogEntry> entries, ActivityLogQuery query)
        {
            return entries.Where(entry =>
            {
                // Time filter;
                if (query.StartTime.HasValue && entry.Timestamp < query.StartTime.Value)
                    return false;
                if (query.EndTime.HasValue && entry.Timestamp > query.EndTime.Value)
                    return false;

                // Severity filter;
                if (query.Severities.Count > 0 && !query.Severities.Contains(entry.Severity))
                    return false;

                // Category filter;
                if (query.Categories.Count > 0 && !query.Categories.Contains(entry.Category))
                    return false;

                // User filter;
                if (!string.IsNullOrEmpty(query.UserId) && entry.UserId != query.UserId)
                    return false;

                // Source filter;
                if (!string.IsNullOrEmpty(query.Source) && entry.Source != query.Source)
                    return false;

                // Operation filter;
                if (!string.IsNullOrEmpty(query.Operation) && entry.Operation != query.Operation)
                    return false;

                // Resource filter;
                if (!string.IsNullOrEmpty(query.Resource) && entry.Resource != query.Resource)
                    return false;

                // Correlation filter;
                if (!string.IsNullOrEmpty(query.CorrelationId) && entry.CorrelationId != query.CorrelationId)
                    return false;

                // Sensitivity filter;
                if (!query.IncludeSensitive && entry.IsSensitive)
                    return false;

                return true;
            }).ToList();
        }

        /// <summary>
        /// Sorts and paginates results;
        /// </summary>
        private List<ActivityLogEntry> SortAndPaginate(List<ActivityLogEntry> entries, ActivityLogQuery query)
        {
            // Sort;
            entries = query.SortBy.ToLowerInvariant() switch;
            {
                "timestamp" => query.SortDescending;
                    ? entries.OrderByDescending(e => e.Timestamp).ToList()
                    : entries.OrderBy(e => e.Timestamp).ToList(),
                "severity" => query.SortDescending;
                    ? entries.OrderByDescending(e => e.Severity).ToList()
                    : entries.OrderBy(e => e.Severity).ToList(),
                "operation" => query.SortDescending;
                    ? entries.OrderByDescending(e => e.Operation).ToList()
                    : entries.OrderBy(e => e.Operation).ToList(),
                _ => entries.OrderByDescending(e => e.Timestamp).ToList()
            };

            // Paginate;
            return entries.Skip(query.Skip).Take(query.Take).ToList();
        }

        /// <summary>
        /// Exports activities to JSON;
        /// </summary>
        private string ExportToJson(List<ActivityLogEntry> activities)
        {
            var settings = new JsonSerializerSettings;
            {
                Formatting = Newtonsoft.Json.Formatting.Indented,
                DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ"
            };

            return Newtonsoft.Json.JsonConvert.SerializeObject(activities, settings);
        }

        /// <summary>
        /// Exports activities to CSV;
        /// </summary>
        private string ExportToCsv(List<ActivityLogEntry> activities)
        {
            var csv = new StringBuilder();
            csv.AppendLine("Timestamp,Severity,Category,Operation,User,Source,Result,DurationMs");

            foreach (var activity in activities)
            {
                csv.AppendLine($"{activity.Timestamp:O},{activity.Severity},{activity.Category}," +
                              $"{EscapeCsv(activity.Operation)},{EscapeCsv(activity.UserName)}," +
                              $"{EscapeCsv(activity.Source)},{EscapeCsv(activity.Result)},{activity.DurationMs}");
            }

            return csv.ToString();
        }

        /// <summary>
        /// Exports activities to XML;
        /// </summary>
        private string ExportToXml(List<ActivityLogEntry> activities)
        {
            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine("<Activities>");

            foreach (var activity in activities)
            {
                xml.AppendLine($"  <Activity>");
                xml.AppendLine($"    <Id>{activity.Id}</Id>");
                xml.AppendLine($"    <Timestamp>{activity.Timestamp:O}</Timestamp>");
                xml.AppendLine($"    <Severity>{activity.Severity}</Severity>");
                xml.AppendLine($"    <Category>{activity.Category}</Category>");
                xml.AppendLine($"    <Operation>{EscapeXml(activity.Operation)}</Operation>");
                xml.AppendLine($"    <User>{EscapeXml(activity.UserName)}</User>");
                xml.AppendLine($"    <Result>{EscapeXml(activity.Result)}</Result>");
                xml.AppendLine($"  </Activity>");
            }

            xml.AppendLine("</Activities>");
            return xml.ToString();
        }

        /// <summary>
        /// Escapes CSV special characters;
        /// </summary>
        private string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        /// <summary>
        /// Escapes XML special characters;
        /// </summary>
        private string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value;
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        /// <summary>
        /// Gets current log file path;
        /// </summary>
        private string GetCurrentLogFilePath()
        {
            var dateStamp = DateTime.UtcNow.ToString("yyyyMMdd");
            return Path.Combine(_configuration.Value.LogDirectory, $"activity_{dateStamp}.log");
        }

        /// <summary>
        /// Ensures log directory exists;
        /// </summary>
        private void EnsureLogDirectory()
        {
            var logDir = _configuration.Value.LogDirectory;
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            var archiveDir = _configuration.Value.ArchiveDirectory;
            if (!Directory.Exists(archiveDir))
            {
                Directory.CreateDirectory(archiveDir);
            }
        }

        /// <summary>
        /// Archives current log file;
        /// </summary>
        private void ArchiveCurrentLogFile()
        {
            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var archiveFile = Path.Combine(
                    _configuration.Value.ArchiveDirectory,
                    $"activity_{timestamp}.log");

                File.Move(_currentLogFile, archiveFile);

                // Cleanup old archives;
                CleanupOldArchives();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to archive log file");
            }
        }

        /// <summary>
        /// Cleans up old archive files;
        /// </summary>
        private void CleanupOldArchives()
        {
            try
            {
                var archiveFiles = Directory.GetFiles(_configuration.Value.ArchiveDirectory, "activity_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                if (archiveFiles.Count > _configuration.Value.MaxArchiveFiles)
                {
                    foreach (var oldFile in archiveFiles.Skip(_configuration.Value.MaxArchiveFiles))
                    {
                        oldFile.Delete();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old archives");
            }
        }

        /// <summary>
        /// Gets all log files;
        /// </summary>
        private List<string> GetLogFiles()
        {
            var files = new List<string>();

            // Current log file;
            if (File.Exists(_currentLogFile))
            {
                files.Add(_currentLogFile);
            }

            // Archive files;
            files.AddRange(Directory.GetFiles(_configuration.Value.ArchiveDirectory, "activity_*.log"));

            return files;
        }

        /// <summary>
        /// Reads log file entries;
        /// </summary>
        private async Task<List<ActivityLogEntry>> ReadLogFileAsync(string filePath, CancellationToken cancellationToken)
        {
            var entries = new List<ActivityLogEntry>();

            try
            {
                using var reader = new StreamReader(filePath, Encoding.UTF8);
                string line;

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var entry = Newtonsoft.Json.JsonConvert.DeserializeObject<ActivityLogEntry>(line);
                        if (entry != null)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse log line: {Line}", line);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read log file: {FilePath}", filePath);
            }

            return entries;
        }

        /// <summary>
        /// Initializes database;
        /// </summary>
        private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
        {
            // Database initialization would go here;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Loads existing sessions;
        /// </summary>
        private async Task LoadExistingSessionsAsync(CancellationToken cancellationToken)
        {
            // Session loading would go here;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Clears database logs;
        /// </summary>
        private async Task ClearDatabaseLogsAsync(DateTime cutoffDate, CancellationToken cancellationToken)
        {
            // Database cleanup would go here;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Clears file logs;
        /// </summary>
        private async Task ClearFileLogsAsync(DateTime cutoffDate, CancellationToken cancellationToken)
        {
            try
            {
                var logFiles = GetLogFiles();

                foreach (var logFile in logFiles)
                {
                    var fileInfo = new FileInfo(logFile);
                    if (fileInfo.CreationTime < cutoffDate)
                    {
                        File.Delete(logFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear file logs");
                throw;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Clears cache;
        /// </summary>
        private void ClearCache(DateTime cutoffDate)
        {
            _cacheLock.EnterWriteLock();
            try
            {
                var keysToRemove = _activityCache;
                    .Where(kv => kv.Value.Timestamp < cutoffDate)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _activityCache.TryRemove(key, out _);
                }
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Cleans up old activities;
        /// </summary>
        private async void CleanupOldActivities(object state)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-_configuration.Value.RetentionPeriod.Days);
                await ClearLogsAsync(cutoffDate, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old activities");
            }
        }

        /// <summary>
        /// Updates statistics;
        /// </summary>
        private async void UpdateStatistics(object state)
        {
            try
            {
                var stats = await GetStatisticsAsync(
                    DateTime.UtcNow.AddHours(-1),
                    DateTime.UtcNow,
                    _cancellationTokenSource.Token);

                _logger.LogInformation("Activity statistics: {Stats}",
                    Newtonsoft.Json.JsonConvert.SerializeObject(stats, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update statistics");
            }
        }

        /// <summary>
        /// Updates performance counters;
        /// </summary>
        private void UpdatePerformanceCounters()
        {
            try
            {
                _queueCounter?.RawValue = _activitiesInQueue;

                // Calculate throughput;
                var elapsedSeconds = (DateTime.UtcNow - _startTime).TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    _throughputCounter?.RawValue = (long)(_totalActivitiesLogged / elapsedSeconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update performance counters");
            }
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnActivityLogged(ActivityLogEntry entry)
        {
            ActivityLogged?.Invoke(this, entry);
        }

        protected virtual void OnBatchProcessed(int count)
        {
            BatchProcessed?.Invoke(this, count);
        }

        protected virtual void OnAnomalyDetected(ActivityLogEntry entry)
        {
            AnomalyDetected?.Invoke(this, entry);
        }

        protected virtual void OnStatisticsUpdated(ActivityLogStatistics stats)
        {
            StatisticsUpdated?.Invoke(this, stats);
        }

        #endregion;

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();

            _batchTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _statisticsTimer?.Dispose();

            _batchSemaphore?.Dispose();
            _cacheLock?.Dispose();

            _queueCounter?.Dispose();
            _throughputCounter?.Dispose();

            _activityQueue.Clear();
            _activityCache.Clear();
            _activityTimers.Clear();
            _userSessions.Clear();

            _logger.LogInformation("ActivityLogger disposed");

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~ActivityLogger()
        {
            Dispose();
        }
    }

    /// <summary>
    /// Interface for activity logging;
    /// </summary>
    public interface IActivityLogger;
    {
        Task LogActivityAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default);
        Task LogActivityAsync(
            ActivitySeverity severity,
            ActivityCategory category,
            string operation,
            string description,
            string userId = null,
            string userName = null,
            Dictionary<string, object> details = null,
            CancellationToken cancellationToken = default);

        string StartActivity(
            string operation,
            string userId = null,
            string userName = null,
            ActivityCategory category = ActivityCategory.Application);

        Task CompleteActivityAsync(
            string activityId,
            string result = "Completed",
            Dictionary<string, object> details = null,
            CancellationToken cancellationToken = default);

        Task<List<ActivityLogEntry>> QueryActivitiesAsync(
            ActivityLogQuery query,
            CancellationToken cancellationToken = default);

        Task<ActivityLogStatistics> GetStatisticsAsync(
            DateTime? startTime = null,
            DateTime? endTime = null,
            CancellationToken cancellationToken = default);

        List<ActivityLogEntry> GetSessionActivities(string sessionId);

        Task<string> ExportActivitiesAsync(
            ActivityLogQuery query,
            string format = "json",
            CancellationToken cancellationToken = default);

        Task ClearLogsAsync(
            DateTime? beforeDate = null,
            CancellationToken cancellationToken = default);

        Dictionary<string, object> GetMetrics();
    }
}
