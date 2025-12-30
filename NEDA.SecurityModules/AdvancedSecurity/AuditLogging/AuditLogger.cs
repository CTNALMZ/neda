using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Services.Messaging.EventBus;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.Reporting;

namespace NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
{
    /// <summary>
    /// Severity levels for audit events;
    /// </summary>
    public enum AuditSeverity;
    {
        Debug = 0,
        Information = 1,
        Warning = 2,
        Error = 3,
        Critical = 4;
    }

    /// <summary>
    /// Audit event categories for classification;
    /// </summary>
    public enum AuditCategory;
    {
        Authentication = 0,
        Authorization = 1,
        DataAccess = 2,
        SystemConfiguration = 3,
        SecurityPolicy = 4,
        Compliance = 5,
        UserActivity = 6,
        SystemEvent = 7,
        BusinessProcess = 8,
        NetworkActivity = 9,
        FileSystem = 10,
        Database = 11,
        Application = 12;
    }

    /// <summary>
    /// Audit event status;
    /// </summary>
    public enum AuditStatus;
    {
        Success = 0,
        Failure = 1,
        Warning = 2,
        Pending = 3,
        Cancelled = 4;
    }

    /// <summary>
    /// Represents a detailed audit log entry
    /// </summary>
    public class AuditLogEntry
    {
        public string AuditId { get; set; }
        public DateTime Timestamp { get; set; }
        public AuditCategory Category { get; set; }
        public AuditSeverity Severity { get; set; }
        public AuditStatus Status { get; set; }
        public string EventName { get; set; }
        public string Description { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string UserDomain { get; set; }
        public string SourceSystem { get; set; }
        public string SourceIP { get; set; }
        public string MachineName { get; set; }
        public string ProcessId { get; set; }
        public string ThreadId { get; set; }
        public string CorrelationId { get; set; }
        public string SessionId { get; set; }
        public string RequestId { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public Dictionary<string, object> ExtendedProperties { get; set; }
        public List<string> Tags { get; set; }
        public string StackTrace { get; set; }
        public Exception Exception { get; set; }
        public long? ExecutionTimeMs { get; set; }
        public bool IsCompliant { get; set; }
        public string ComplianceRule { get; set; }

        public AuditLogEntry()
        {
            AuditId = Guid.NewGuid().ToString("N");
            Timestamp = DateTime.UtcNow;
            Properties = new Dictionary<string, object>();
            ExtendedProperties = new Dictionary<string, object>();
            Tags = new List<string>();
            IsCompliant = true;
        }
    }

    /// <summary>
    /// Search criteria for audit log queries;
    /// </summary>
    public class AuditSearchCriteria;
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public AuditSeverity? MinSeverity { get; set; }
        public List<AuditCategory> Categories { get; set; }
        public List<AuditStatus> Statuses { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string SourceSystem { get; set; }
        public string EventName { get; set; }
        public string CorrelationId { get; set; }
        public string SearchText { get; set; }
        public bool? IsCompliant { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public string OrderBy { get; set; }
        public bool? Descending { get; set; }
    }

    /// <summary>
    /// Configuration for audit logging;
    /// </summary>
    public class AuditLogConfiguration;
    {
        public string StoragePath { get; set; }
        public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(365);
        public int MaxFileSizeMB { get; set; } = 100;
        public bool EnableCompression { get; set; } = true;
        public bool EnableEncryption { get; set; } = true;
        public AuditSeverity MinimumLogLevel { get; set; } = AuditSeverity.Information;
        public List<AuditCategory> ExcludedCategories { get; set; }
        public bool EnableRealTimeMonitoring { get; set; } = true;
        public bool EnablePerformanceMetrics { get; set; } = true;
        public TimeSpan BatchFlushInterval { get; set; } = TimeSpan.FromSeconds(30);
        public int BatchSize { get; set; } = 100;
    }

    /// <summary>
    /// Professional-grade audit logging system with compliance tracking,
    /// real-time monitoring, and advanced search capabilities;
    /// </summary>
    public class AuditLogger : IAuditLogger, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IEventBus _eventBus;
        private readonly IComplianceChecker _complianceChecker;
        private readonly IReportGenerator _reportGenerator;
        private readonly IDiagnosticTool _diagnosticTool;

        private readonly AuditLogConfiguration _configuration;
        private readonly string _auditStoragePath;
        private readonly SemaphoreSlim _storageLock = new SemaphoreSlim(1, 1);
        private readonly Queue<AuditLogEntry> _auditQueue = new Queue<AuditLogEntry>();
        private readonly Timer _batchProcessingTimer;
        private readonly Timer _cleanupTimer;
        private readonly Dictionary<string, AuditLogEntry> _inMemoryCache = new Dictionary<string, AuditLogEntry>();
        private readonly object _syncRoot = new object();

        private bool _disposed = false;
        private long _totalLogEntries = 0;
        private long _failedLogEntries = 0;
        private DateTime _startTime = DateTime.UtcNow;

        // Performance counters;
        private readonly PerformanceCounter _logsPerSecondCounter;
        private readonly PerformanceCounter _averageLogSizeCounter;
        private readonly PerformanceCounter _errorRateCounter;

        /// <summary>
        /// Event raised when a new audit entry is logged;
        /// </summary>
        public event EventHandler<AuditLoggedEventArgs> AuditLogged;

        /// <summary>
        /// Event raised when a compliance violation is detected;
        /// </summary>
        public event EventHandler<ComplianceViolationEventArgs> ComplianceViolationDetected;

        /// <summary>
        /// Initializes a new instance of AuditLogger;
        /// </summary>
        public AuditLogger(
            ILogger logger,
            ISecurityManager securityManager,
            IEventBus eventBus,
            IComplianceChecker complianceChecker,
            IReportGenerator reportGenerator,
            IDiagnosticTool diagnosticTool,
            AuditLogConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _complianceChecker = complianceChecker ?? throw new ArgumentNullException(nameof(complianceChecker));
            _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));

            _configuration = configuration ?? new AuditLogConfiguration();

            // Configure storage path;
            _auditStoragePath = _configuration.StoragePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NEDA",
                "AuditLogs");

            EnsureStorageDirectory();

            // Initialize batch processing timer;
            _batchProcessingTimer = new Timer(
                ProcessBatch,
                null,
                _configuration.BatchFlushInterval,
                _configuration.BatchFlushInterval);

            // Initialize cleanup timer (runs daily)
            _cleanupTimer = new Timer(
                CleanupOldLogs,
                null,
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(24));

            // Initialize performance counters;
            _logsPerSecondCounter = new PerformanceCounter("NEDA Audit Logs", "Logs Per Second");
            _averageLogSizeCounter = new PerformanceCounter("NEDA Audit Logs", "Average Log Size");
            _errorRateCounter = new PerformanceCounter("NEDA Audit Logs", "Error Rate");

            _logger.LogInformation("AuditLogger initialized", new;
            {
                StoragePath = _auditStoragePath,
                MinimumLogLevel = _configuration.MinimumLogLevel,
                RetentionPeriod = _configuration.RetentionPeriod;
            });
        }

        /// <summary>
        /// Logs an audit event with detailed context information;
        /// </summary>
        public async Task<string> LogAsync(
            AuditCategory category,
            string eventName,
            string description,
            AuditSeverity severity = AuditSeverity.Information,
            AuditStatus status = AuditStatus.Success,
            Dictionary<string, object> properties = null,
            Exception exception = null,
            string correlationId = null,
            ClaimsPrincipal user = null,
            long? executionTimeMs = null)
        {
            // Check if logging is enabled for this category and severity;
            if (!ShouldLog(category, severity))
            {
                return null;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var auditEntry = CreateAuditEntry(
                    category,
                    eventName,
                    description,
                    severity,
                    status,
                    properties,
                    exception,
                    correlationId,
                    user,
                    executionTimeMs);

                // Check compliance;
                await CheckComplianceAsync(auditEntry);

                // Add to processing queue;
                lock (_syncRoot)
                {
                    _auditQueue.Enqueue(auditEntry);
                    _inMemoryCache[auditEntry.AuditId] = auditEntry
                    _totalLogEntries++;
                }

                // Update performance counters;
                UpdatePerformanceCounters(auditEntry);

                // Raise event for real-time monitoring;
                OnAuditLogged(new AuditLoggedEventArgs(auditEntry));

                // For high severity events, trigger immediate processing;
                if (severity >= AuditSeverity.Error)
                {
                    await FlushAsync();
                }

                stopwatch.Stop();
                auditEntry.Properties["LogProcessingTimeMs"] = stopwatch.ElapsedMilliseconds;

                return auditEntry.AuditId;
            }
            catch (Exception ex)
            {
                _failedLogEntries++;
                _logger.LogError("Failed to log audit event", ex);
                throw new AuditLogException("Failed to log audit event", ex);
            }
        }

        /// <summary>
        /// Logs user authentication events;
        /// </summary>
        public async Task<string> LogAuthenticationAsync(
            string userId,
            string userName,
            string authenticationMethod,
            AuditStatus status,
            string sourceIP = null,
            Dictionary<string, object> additionalInfo = null)
        {
            var properties = new Dictionary<string, object>
            {
                ["AuthenticationMethod"] = authenticationMethod,
                ["AuthenticationTimestamp"] = DateTime.UtcNow,
                ["SourceIP"] = sourceIP,
                ["UserAgent"] = GetUserAgent()
            };

            if (additionalInfo != null)
            {
                foreach (var kvp in additionalInfo)
                {
                    properties[kvp.Key] = kvp.Value;
                }
            }

            var severity = status == AuditStatus.Success ? AuditSeverity.Information : AuditSeverity.Warning;

            return await LogAsync(
                AuditCategory.Authentication,
                "UserAuthentication",
                $"User authentication attempt: {status}",
                severity,
                status,
                properties,
                correlationId: GenerateCorrelationId(),
                user: CreateClaimsPrincipal(userId, userName));
        }

        /// <summary>
        /// Logs authorization events;
        /// </summary>
        public async Task<string> LogAuthorizationAsync(
            string userId,
            string userName,
            string resource,
            string action,
            AuditStatus status,
            string reason = null)
        {
            var properties = new Dictionary<string, object>
            {
                ["Resource"] = resource,
                ["Action"] = action,
                ["AuthorizationTimestamp"] = DateTime.UtcNow,
                ["Reason"] = reason;
            };

            var severity = status == AuditStatus.Success ? AuditSeverity.Information : AuditSeverity.Warning;

            return await LogAsync(
                AuditCategory.Authorization,
                "AccessControl",
                $"Authorization check for {action} on {resource}: {status}",
                severity,
                status,
                properties,
                correlationId: GenerateCorrelationId(),
                user: CreateClaimsPrincipal(userId, userName));
        }

        /// <summary>
        /// Logs data access events;
        /// </summary>
        public async Task<string> LogDataAccessAsync(
            string userId,
            string userName,
            string operation,
            string entityType,
            string entityId,
            Dictionary<string, object> beforeState = null,
            Dictionary<string, object> afterState = null,
            AuditStatus status = AuditStatus.Success)
        {
            var properties = new Dictionary<string, object>
            {
                ["Operation"] = operation,
                ["EntityType"] = entityType,
                ["EntityId"] = entityId,
                ["DataAccessTimestamp"] = DateTime.UtcNow;
            };

            if (beforeState != null)
            {
                properties["BeforeState"] = beforeState;
            }

            if (afterState != null)
            {
                properties["AfterState"] = afterState;
            }

            var severity = operation.ToUpperInvariant() switch;
            {
                "DELETE" => AuditSeverity.Warning,
                "UPDATE" => AuditSeverity.Information,
                _ => AuditSeverity.Debug;
            };

            return await LogAsync(
                AuditCategory.DataAccess,
                "DataAccess",
                $"{operation} operation on {entityType} (ID: {entityId})",
                severity,
                status,
                properties,
                correlationId: GenerateCorrelationId(),
                user: CreateClaimsPrincipal(userId, userName));
        }

        /// <summary>
        /// Logs system configuration changes;
        /// </summary>
        public async Task<string> LogConfigurationChangeAsync(
            string userId,
            string userName,
            string configurationSection,
            string settingName,
            object oldValue,
            object newValue,
            string reason = null)
        {
            var properties = new Dictionary<string, object>
            {
                ["ConfigurationSection"] = configurationSection,
                ["SettingName"] = settingName,
                ["OldValue"] = oldValue,
                ["NewValue"] = newValue,
                ["ChangeTimestamp"] = DateTime.UtcNow,
                ["Reason"] = reason;
            };

            return await LogAsync(
                AuditCategory.SystemConfiguration,
                "ConfigurationChange",
                $"Configuration changed: {configurationSection}.{settingName}",
                AuditSeverity.Warning,
                AuditStatus.Success,
                properties,
                correlationId: GenerateCorrelationId(),
                user: CreateClaimsPrincipal(userId, userName));
        }

        /// <summary>
        /// Logs security policy events;
        /// </summary>
        public async Task<string> LogSecurityPolicyAsync(
            AuditSeverity severity,
            string policyName,
            string description,
            AuditStatus status,
            Dictionary<string, object> details = null)
        {
            var properties = new Dictionary<string, object>
            {
                ["PolicyName"] = policyName,
                ["PolicyEventTimestamp"] = DateTime.UtcNow;
            };

            if (details != null)
            {
                foreach (var kvp in details)
                {
                    properties[kvp.Key] = kvp.Value;
                }
            }

            return await LogAsync(
                AuditCategory.SecurityPolicy,
                "SecurityPolicy",
                description,
                severity,
                status,
                properties,
                correlationId: GenerateCorrelationId());
        }

        /// <summary>
        /// Logs a security incident for compliance reporting;
        /// </summary>
        public async Task<string> LogSecurityIncidentAsync(
            string incidentId,
            string category,
            string severity,
            string description,
            string sourceSystem)
        {
            var properties = new Dictionary<string, object>
            {
                ["IncidentId"] = incidentId,
                ["IncidentCategory"] = category,
                ["IncidentSeverity"] = severity,
                ["SourceSystem"] = sourceSystem,
                ["IncidentTimestamp"] = DateTime.UtcNow;
            };

            return await LogAsync(
                AuditCategory.Compliance,
                "SecurityIncident",
                description,
                ParseSeverity(severity),
                AuditStatus.Warning,
                properties,
                correlationId: incidentId);
        }

        /// <summary>
        /// Logs user activity for monitoring;
        /// </summary>
        public async Task<string> LogUserActivityAsync(
            string userId,
            string userName,
            string activity,
            string resource,
            Dictionary<string, object> details = null)
        {
            var properties = new Dictionary<string, object>
            {
                ["Activity"] = activity,
                ["Resource"] = resource,
                ["ActivityTimestamp"] = DateTime.UtcNow;
            };

            if (details != null)
            {
                foreach (var kvp in details)
                {
                    properties[kvp.Key] = kvp.Value;
                }
            }

            return await LogAsync(
                AuditCategory.UserActivity,
                "UserActivity",
                $"{userName} performed {activity} on {resource}",
                AuditSeverity.Information,
                AuditStatus.Success,
                properties,
                correlationId: GenerateCorrelationId(),
                user: CreateClaimsPrincipal(userId, userName));
        }

        /// <summary>
        /// Retrieves audit log entry by ID;
        /// </summary>
        public async Task<AuditLogEntry> GetAuditEntryAsync(string auditId)
        {
            if (string.IsNullOrWhiteSpace(auditId))
                throw new ArgumentException("Audit ID is required", nameof(auditId));

            // Check in-memory cache first;
            lock (_syncRoot)
            {
                if (_inMemoryCache.TryGetValue(auditId, out var cachedEntry))
                {
                    return await Task.FromResult(cachedEntry);
                }
            }

            // Load from storage;
            return await LoadAuditEntryFromStorageAsync(auditId);
        }

        /// <summary>
        /// Searches audit logs based on criteria;
        /// </summary>
        public async Task<List<AuditLogEntry>> SearchAuditLogsAsync(AuditSearchCriteria criteria)
        {
            var results = new List<AuditLogEntry>();

            // Search in-memory cache first for recent entries;
            lock (_syncRoot)
            {
                var cachedEntries = _inMemoryCache.Values.Where(e => MatchesCriteria(e, criteria));
                results.AddRange(cachedEntries);
            }

            // If we need more results or have date range restrictions, search files;
            if (criteria.StartDate.HasValue || criteria.EndDate.HasValue ||
                results.Count < (criteria.Limit ?? int.MaxValue))
            {
                var fileResults = await SearchAuditFilesAsync(criteria, results.Count);
                results.AddRange(fileResults);
            }

            // Apply ordering;
            if (!string.IsNullOrWhiteSpace(criteria.OrderBy))
            {
                results = OrderResults(results, criteria.OrderBy, criteria.Descending ?? false);
            }

            // Apply limit and offset;
            if (criteria.Offset.HasValue || criteria.Limit.HasValue)
            {
                var offset = criteria.Offset ?? 0;
                var limit = criteria.Limit ?? results.Count;
                results = results.Skip(offset).Take(limit).ToList();
            }

            return results;
        }

        /// <summary>
        /// Gets audit statistics for reporting;
        /// </summary>
        public async Task<AuditStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            var criteria = new AuditSearchCriteria;
            {
                StartDate = startDate,
                EndDate = endDate;
            };

            var entries = await SearchAuditLogsAsync(criteria);

            return new AuditStatistics;
            {
                TotalEntries = entries.Count,
                TotalEntriesOverall = _totalLogEntries,
                FailedEntries = _failedLogEntries,
                Uptime = DateTime.UtcNow - _startTime,
                ByCategory = entries.GroupBy(e => e.Category)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                BySeverity = entries.GroupBy(e => e.Severity)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                ByStatus = entries.GroupBy(e => e.Status)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                ByUser = entries.GroupBy(e => e.UserName)
                    .ToDictionary(g => g.Key ?? "System", g => g.Count()),
                AverageResponseTime = entries.Where(e => e.ExecutionTimeMs.HasValue)
                    .Average(e => e.ExecutionTimeMs) ?? 0,
                ComplianceRate = entries.Count > 0 ?
                    (double)entries.Count(e => e.IsCompliant) / entries.Count * 100 : 100;
            };
        }

        /// <summary>
        /// Exports audit logs in specified format;
        /// </summary>
        public async Task<byte[]> ExportAuditLogsAsync(
            AuditSearchCriteria criteria,
            ExportFormat format = ExportFormat.Json,
            bool includeSensitiveData = false)
        {
            var entries = await SearchAuditLogsAsync(criteria);

            // Filter sensitive data if needed;
            if (!includeSensitiveData)
            {
                entries = SanitizeSensitiveData(entries);
            }

            switch (format)
            {
                case ExportFormat.Json:
                    return await ExportToJsonAsync(entries);

                case ExportFormat.Csv:
                    return await ExportToCsvAsync(entries);

                case ExportFormat.Xml:
                    return await ExportToXmlAsync(entries);

                case ExportFormat.Pdf:
                    return await ExportToPdfAsync(entries, criteria);

                default:
                    throw new NotSupportedException($"Export format {format} is not supported");
            }
        }

        /// <summary>
        /// Flushes all pending audit logs to storage;
        /// </summary>
        public async Task FlushAsync()
        {
            await ProcessBatchInternalAsync(forceFlush: true);
        }

        /// <summary>
        /// Archives old audit logs based on retention policy;
        /// </summary>
        public async Task<int> ArchiveOldLogsAsync(DateTime olderThan)
        {
            var archivedCount = 0;
            var archivePath = Path.Combine(_auditStoragePath, "Archive");

            if (!Directory.Exists(archivePath))
            {
                Directory.CreateDirectory(archivePath);
            }

            var auditFiles = Directory.GetFiles(_auditStoragePath, "*.audit")
                .Where(f => File.GetCreationTimeUtc(f) < olderThan);

            foreach (var file in auditFiles)
            {
                try
                {
                    var archiveFile = Path.Combine(archivePath, Path.GetFileName(file));
                    File.Move(file, archiveFile);
                    archivedCount++;

                    _logger.LogInformation($"Archived audit log file: {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to archive file: {file}", ex);
                }
            }

            return await Task.FromResult(archivedCount);
        }

        /// <summary>
        /// Validates audit trail for compliance requirements;
        /// </summary>
        public async Task<ComplianceValidationResult> ValidateComplianceAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> complianceRules = null)
        {
            var criteria = new AuditSearchCriteria;
            {
                StartDate = startDate,
                EndDate = endDate;
            };

            var entries = await SearchAuditLogsAsync(criteria);
            var result = new ComplianceValidationResult;
            {
                ValidationDate = DateTime.UtcNow,
                PeriodStart = startDate,
                PeriodEnd = endDate,
                TotalEntriesValidated = entries.Count;
            };

            // Check for missing required audit events;
            var missingEvents = await CheckForMissingRequiredEvents(entries, complianceRules);
            result.MissingRequiredEvents = missingEvents;

            // Check for tampering;
            result.TamperingDetected = await CheckForTampering(entries);

            // Check retention compliance;
            result.RetentionCompliant = await CheckRetentionCompliance();

            // Calculate compliance score;
            result.ComplianceScore = CalculateComplianceScore(result);

            // Log validation result;
            await LogAsync(
                AuditCategory.Compliance,
                "ComplianceValidation",
                $"Compliance validation completed for period {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                result.ComplianceScore >= 95 ? AuditSeverity.Information : AuditSeverity.Warning,
                AuditStatus.Success,
                new Dictionary<string, object>
                {
                    ["ComplianceScore"] = result.ComplianceScore,
                    ["MissingEvents"] = missingEvents,
                    ["TamperingDetected"] = result.TamperingDetected,
                    ["RetentionCompliant"] = result.RetentionCompliant;
                });

            return result;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Flush remaining logs;
                    FlushAsync().GetAwaiter().GetResult();

                    // Dispose timers;
                    _batchProcessingTimer?.Dispose();
                    _cleanupTimer?.Dispose();

                    // Dispose performance counters;
                    _logsPerSecondCounter?.Dispose();
                    _averageLogSizeCounter?.Dispose();
                    _errorRateCounter?.Dispose();

                    _storageLock?.Dispose();
                }
                _disposed = true;
            }
        }

        private void EnsureStorageDirectory()
        {
            if (!Directory.Exists(_auditStoragePath))
            {
                Directory.CreateDirectory(_auditStoragePath);
            }
        }

        private bool ShouldLog(AuditCategory category, AuditSeverity severity)
        {
            // Check minimum log level;
            if (severity < _configuration.MinimumLogLevel)
                return false;

            // Check excluded categories;
            if (_configuration.ExcludedCategories?.Contains(category) == true)
                return false;

            return true;
        }

        private AuditLogEntry CreateAuditEntry(
            AuditCategory category,
            string eventName,
            string description,
            AuditSeverity severity,
            AuditStatus status,
            Dictionary<string, object> properties,
            Exception exception,
            string correlationId,
            ClaimsPrincipal user,
            long? executionTimeMs)
        {
            var entry = new AuditLogEntry
            {
                Category = category,
                EventName = eventName,
                Description = description,
                Severity = severity,
                Status = status,
                ExecutionTimeMs = executionTimeMs,
                Exception = exception,
                CorrelationId = correlationId ?? GenerateCorrelationId(),
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId.ToString(),
                ThreadId = Thread.CurrentThread.ManagedThreadId.ToString(),
                SessionId = GetSessionId(),
                RequestId = GetRequestId()
            };

            // Add user information;
            if (user != null)
            {
                entry.UserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                entry.UserName = user.Identity?.Name;
                entry.UserDomain = user.FindFirst(ClaimTypes.GroupSid)?.Value;
            }
            else;
            {
                entry.UserId = "System";
                entry.UserName = "System";
            }

            // Add properties;
            if (properties != null)
            {
                foreach (var kvp in properties)
                {
                    entry.Properties[kvp.Key] = kvp.Value;
                }
            }

            // Add system properties;
            entry.Properties["AssemblyVersion"] = GetAssemblyVersion();
            entry.Properties["OSVersion"] = Environment.OSVersion.ToString();
            entry.Properties["CLRVersion"] = Environment.Version.ToString();
            entry.Properties["WorkingSet"] = Environment.WorkingSet;

            // Add source IP if available;
            entry.SourceIP = GetClientIPAddress();

            // Add stack trace for errors;
            if (exception != null)
            {
                entry.StackTrace = exception.StackTrace;
                entry.Tags.Add("Exception");
            }

            // Add tags based on category and severity;
            entry.Tags.Add(category.ToString());
            entry.Tags.Add(severity.ToString());
            entry.Tags.Add(status.ToString());

            return entry
        }

        private async Task CheckComplianceAsync(AuditLogEntry entry)
        {
            try
            {
                // Check if the entry violates any compliance rules;
                var complianceResult = await _complianceChecker.CheckAuditComplianceAsync(entry);

                entry.IsCompliant = complianceResult.IsCompliant;
                entry.ComplianceRule = complianceResult.RuleName;

                if (!entry.IsCompliant)
                {
                    entry.Severity = AuditSeverity.Warning;
                    entry.Tags.Add("ComplianceViolation");

                    // Raise compliance violation event;
                    OnComplianceViolationDetected(new ComplianceViolationEventArgs(
                        entry.AuditId,
                        complianceResult.RuleName,
                        complianceResult.ViolationDescription,
                        entry.Timestamp));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to check compliance", ex);
                entry.IsCompliant = false;
                entry.ComplianceRule = "ComplianceCheckFailed";
            }
        }

        private async Task StoreAuditEntryAsync(AuditLogEntry entry)
        {
            await _storageLock.WaitAsync();

            try
            {
                // Generate filename with timestamp and category for better organization;
                var timestamp = entry.Timestamp.ToString("yyyyMMdd_HHmmss");
                var filename = $"{timestamp}_{entry.Category}_{entry.AuditId}.audit";
                var filePath = Path.Combine(_auditStoragePath, filename);

                // Serialize and compress if enabled;
                var json = System.Text.Json.JsonSerializer.Serialize(entry, new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                });

                var data = System.Text.Encoding.UTF8.GetBytes(json);

                if (_configuration.EnableCompression)
                {
                    data = CompressData(data);
                }

                if (_configuration.EnableEncryption)
                {
                    data = await EncryptDataAsync(data);
                }

                await File.WriteAllBytesAsync(filePath, data);

                // Update file metadata;
                File.SetCreationTimeUtc(filePath, entry.Timestamp);
                File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        private async Task<AuditLogEntry> LoadAuditEntryFromStorageAsync(string auditId)
        {
            var pattern = $"*_{auditId}.audit";
            var files = Directory.GetFiles(_auditStoragePath, pattern);

            if (files.Length == 0)
                return null;

            var filePath = files[0];
            return await LoadAuditEntryFromFileAsync(filePath);
        }

        private async Task<AuditLogEntry> LoadAuditEntryFromFileAsync(string filePath)
        {
            try
            {
                var data = await File.ReadAllBytesAsync(filePath);

                if (_configuration.EnableEncryption)
                {
                    data = await DecryptDataAsync(data);
                }

                if (_configuration.EnableCompression)
                {
                    data = DecompressData(data);
                }

                var json = System.Text.Encoding.UTF8.GetString(data);
                return System.Text.Json.JsonSerializer.Deserialize<AuditLogEntry>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load audit entry from file: {filePath}", ex);
                return null;
            }
        }

        private async Task<List<AuditLogEntry>> SearchAuditFilesAsync(AuditSearchCriteria criteria, int alreadyFound = 0)
        {
            var results = new List<AuditLogEntry>();
            var limit = criteria.Limit ?? int.MaxValue;

            // Get all audit files sorted by creation time (newest first)
            var auditFiles = Directory.GetFiles(_auditStoragePath, "*.audit")
                .OrderByDescending(f => File.GetCreationTimeUtc(f));

            foreach (var file in auditFiles)
            {
                if (results.Count >= limit - alreadyFound)
                    break;

                try
                {
                    var entry = await LoadAuditEntryFromFileAsync(file);
                    if (entry != null && MatchesCriteria(entry, criteria))
                    {
                        results.Add(entry);
                    }
                }
                catch
                {
                    // Skip corrupted files;
                    continue;
                }
            }

            return results;
        }

        private bool MatchesCriteria(AuditLogEntry entry, AuditSearchCriteria criteria)
        {
            if (criteria.StartDate.HasValue && entry.Timestamp < criteria.StartDate.Value)
                return false;

            if (criteria.EndDate.HasValue && entry.Timestamp > criteria.EndDate.Value)
                return false;

            if (criteria.MinSeverity.HasValue && entry.Severity < criteria.MinSeverity.Value)
                return false;

            if (criteria.Categories != null && criteria.Categories.Any() &&
                !criteria.Categories.Contains(entry.Category))
                return false;

            if (criteria.Statuses != null && criteria.Statuses.Any() &&
                !criteria.Statuses.Contains(entry.Status))
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.UserId) &&
                !entry.UserId?.Contains(criteria.UserId, StringComparison.OrdinalIgnoreCase) == true)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.UserName) &&
                !entry.UserName?.Contains(criteria.UserName, StringComparison.OrdinalIgnoreCase) == true)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.SourceSystem) &&
                !entry.SourceSystem?.Contains(criteria.SourceSystem, StringComparison.OrdinalIgnoreCase) == true)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.EventName) &&
                !entry.EventName?.Contains(criteria.EventName, StringComparison.OrdinalIgnoreCase) == true)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.CorrelationId) &&
                entry.CorrelationId != criteria.CorrelationId)
                return false;

            if (!string.IsNullOrWhiteSpace(criteria.SearchText))
            {
                var searchText = criteria.SearchText.ToLowerInvariant();
                if (!entry.Description.ToLowerInvariant().Contains(searchText) &&
                    !entry.EventName.ToLowerInvariant().Contains(searchText) &&
                    !entry.Properties.Any(p => p.Value?.ToString()?.ToLowerInvariant().Contains(searchText) == true))
                    return false;
            }

            if (criteria.IsCompliant.HasValue && entry.IsCompliant != criteria.IsCompliant.Value)
                return false;

            return true;
        }

        private List<AuditLogEntry> OrderResults(List<AuditLogEntry> entries, string orderBy, bool descending)
        {
            return orderBy.ToLowerInvariant() switch;
            {
                "timestamp" => descending ?
                    entries.OrderByDescending(e => e.Timestamp).ToList() :
                    entries.OrderBy(e => e.Timestamp).ToList(),

                "severity" => descending ?
                    entries.OrderByDescending(e => e.Severity).ToList() :
                    entries.OrderBy(e => e.Severity).ToList(),

                "category" => descending ?
                    entries.OrderByDescending(e => e.Category).ToList() :
                    entries.OrderBy(e => e.Category).ToList(),

                "username" => descending ?
                    entries.OrderByDescending(e => e.UserName).ToList() :
                    entries.OrderBy(e => e.UserName).ToList(),

                _ => entries;
            };
        }

        private void ProcessBatch(object state)
        {
            ProcessBatchInternalAsync().GetAwaiter().GetResult();
        }

        private async Task ProcessBatchInternalAsync(bool forceFlush = false)
        {
            List<AuditLogEntry> batch = null;

            lock (_syncRoot)
            {
                if (_auditQueue.Count > 0 && (forceFlush || _auditQueue.Count >= _configuration.BatchSize))
                {
                    batch = new List<AuditLogEntry>();
                    var batchSize = forceFlush ? _auditQueue.Count : Math.Min(_configuration.BatchSize, _auditQueue.Count);

                    for (int i = 0; i < batchSize; i++)
                    {
                        batch.Add(_auditQueue.Dequeue());
                    }
                }
            }

            if (batch != null && batch.Count > 0)
            {
                await ProcessBatchEntriesAsync(batch);
            }
        }

        private async Task ProcessBatchEntriesAsync(List<AuditLogEntry> batch)
        {
            var tasks = new List<Task>();

            foreach (var entry in batch)
            {
                tasks.Add(StoreAuditEntryAsync(entry));
            }

            await Task.WhenAll(tasks);

            // Publish batch processed event;
            await _eventBus.PublishAsync(new AuditBatchProcessedEvent;
            {
                BatchSize = batch.Count,
                ProcessedAt = DateTime.UtcNow,
                Categories = batch.Select(e => e.Category).Distinct().ToList()
            });

            _logger.LogDebug($"Processed audit batch of {batch.Count} entries");
        }

        private void CleanupOldLogs(object state)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow - _configuration.RetentionPeriod;
                var oldFiles = Directory.GetFiles(_auditStoragePath, "*.audit")
                    .Where(f => File.GetCreationTimeUtc(f) < cutoffDate);

                foreach (var file in oldFiles)
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogInformation($"Deleted old audit log file: {Path.GetFileName(file)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to delete old audit log file: {file}", ex);
                    }
                }

                // Also clean up in-memory cache;
                lock (_syncRoot)
                {
                    var oldEntries = _inMemoryCache.Where(kvp => kvp.Value.Timestamp < cutoffDate)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in oldEntries)
                    {
                        _inMemoryCache.Remove(key);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error during audit log cleanup", ex);
            }
        }

        private void UpdatePerformanceCounters(AuditLogEntry entry)
        {
            try
            {
                // Calculate entry size (approximate)
                var entrySize = System.Text.Json.JsonSerializer.Serialize(entry).Length;
                _averageLogSizeCounter.RawValue = entrySize;

                // Update logs per second;
                _logsPerSecondCounter.Increment();

                // Update error rate;
                if (entry.Severity >= AuditSeverity.Error)
                {
                    _errorRateCounter.Increment();
                }
            }
            catch
            {
                // Performance counter errors should not affect logging;
            }
        }

        private byte[] CompressData(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
            {
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private byte[] DecompressData(byte[] data)
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
            {
                gzip.CopyTo(output);
            }
            return output.ToArray();
        }

        private async Task<byte[]> EncryptDataAsync(byte[] data)
        {
            // Use system's data protection API or custom encryption;
            // For production, use proper key management;
            return await Task.FromResult(data);
        }

        private async Task<byte[]> DecryptDataAsync(byte[] data)
        {
            return await Task.FromResult(data);
        }

        private List<AuditLogEntry> SanitizeSensitiveData(List<AuditLogEntry> entries)
        {
            var sensitiveProperties = new[] { "password", "token", "secret", "key", "creditcard", "ssn" };

            foreach (var entry in entries)
            {
                // Remove sensitive properties;
                var sensitiveKeys = entry.Properties.Keys;
                    .Where(k => sensitiveProperties.Any(sp => k.IndexOf(sp, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                foreach (var key in sensitiveKeys)
                {
                    entry.Properties[key] = "***REDACTED***";
                }

                // Sanitize extended properties;
                if (entry.ExtendedProperties != null)
                {
                    var extendedSensitiveKeys = entry.ExtendedProperties.Keys;
                        .Where(k => sensitiveProperties.Any(sp => k.IndexOf(sp, StringComparison.OrdinalIgnoreCase) >= 0))
                        .ToList();

                    foreach (var key in extendedSensitiveKeys)
                    {
                        entry.ExtendedProperties[key] = "***REDACTED***";
                    }
                }
            }

            return entries;
        }

        private async Task<byte[]> ExportToJsonAsync(List<AuditLogEntry> entries)
        {
            var options = new System.Text.Json.JsonSerializerOptions;
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            };

            var json = System.Text.Json.JsonSerializer.Serialize(entries, options);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        private async Task<byte[]> ExportToCsvAsync(List<AuditLogEntry> entries)
        {
            using var memoryStream = new MemoryStream();
            using var writer = new StreamWriter(memoryStream);

            // Write header;
            writer.WriteLine("Timestamp,Category,Severity,Status,EventName,UserName,Description,IsCompliant");

            // Write data;
            foreach (var entry in entries)
            {
                var line = string.Join(",",
                    entry.Timestamp.ToString("O"),
                    entry.Category,
                    entry.Severity,
                    entry.Status,
                    CsvEscape(entry.EventName),
                    CsvEscape(entry.UserName),
                    CsvEscape(entry.Description),
                    entry.IsCompliant);

                writer.WriteLine(line);
            }

            writer.Flush();
            return memoryStream.ToArray();
        }

        private async Task<byte[]> ExportToXmlAsync(List<AuditLogEntry> entries)
        {
            using var memoryStream = new MemoryStream();
            using var writer = System.Xml.XmlWriter.Create(memoryStream, new System.Xml.XmlWriterSettings;
            {
                Indent = true,
                Encoding = System.Text.Encoding.UTF8;
            });

            writer.WriteStartDocument();
            writer.WriteStartElement("AuditLogs");

            foreach (var entry in entries)
            {
                writer.WriteStartElement("AuditEntry");
                writer.WriteAttributeString("Id", entry.AuditId);
                writer.WriteAttributeString("Timestamp", entry.Timestamp.ToString("O"));
                writer.WriteAttributeString("Category", entry.Category.ToString());
                writer.WriteAttributeString("Severity", entry.Severity.ToString());
                writer.WriteAttributeString("Status", entry.Status.ToString());

                writer.WriteElementString("EventName", entry.EventName);
                writer.WriteElementString("UserName", entry.UserName);
                writer.WriteElementString("Description", entry.Description);
                writer.WriteElementString("IsCompliant", entry.IsCompliant.ToString());

                writer.WriteEndElement(); // AuditEntry
            }

            writer.WriteEndElement(); // AuditLogs;
            writer.WriteEndDocument();
            writer.Flush();

            return memoryStream.ToArray();
        }

        private async Task<byte[]> ExportToPdfAsync(List<AuditLogEntry> entries, AuditSearchCriteria criteria)
        {
            // Generate PDF report using report generator;
            var reportData = new AuditReportData;
            {
                Entries = entries,
                Criteria = criteria,
                GeneratedAt = DateTime.UtcNow,
                GeneratedBy = Environment.UserName;
            };

            return await _reportGenerator.GenerateAuditReportAsync(reportData);
        }

        private string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        private async Task<List<string>> CheckForMissingRequiredEvents(List<AuditLogEntry> entries, List<string> complianceRules)
        {
            var missingEvents = new List<string>();

            // Check for required daily events;
            var days = entries.Select(e => e.Timestamp.Date).Distinct();
            foreach (var day in days)
            {
                var dailyEntries = entries.Where(e => e.Timestamp.Date == day);

                // Check for system startup/shutdown events;
                if (!dailyEntries.Any(e => e.EventName == "SystemStartup"))
                {
                    missingEvents.Add($"SystemStartup on {day:yyyy-MM-dd}");
                }
            }

            return await Task.FromResult(missingEvents);
        }

        private async Task<bool> CheckForTampering(List<AuditLogEntry> entries)
        {
            // Check for gaps in sequence or timestamps;
            var orderedEntries = entries.OrderBy(e => e.Timestamp).ToList();

            for (int i = 1; i < orderedEntries.Count; i++)
            {
                var timeGap = orderedEntries[i].Timestamp - orderedEntries[i - 1].Timestamp;

                // Large gaps might indicate tampering;
                if (timeGap > TimeSpan.FromHours(6) && timeGap < TimeSpan.FromDays(1))
                {
                    // Check if this is during expected working hours;
                    if (orderedEntries[i - 1].Timestamp.Hour >= 8 && orderedEntries[i - 1].Timestamp.Hour <= 18)
                    {
                        return true;
                    }
                }
            }

            return await Task.FromResult(false);
        }

        private async Task<bool> CheckRetentionCompliance()
        {
            var oldestFile = Directory.GetFiles(_auditStoragePath, "*.audit")
                .Select(f => File.GetCreationTimeUtc(f))
                .DefaultIfEmpty(DateTime.UtcNow)
                .Min();

            var age = DateTime.UtcNow - oldestFile;
            return await Task.FromResult(age <= _configuration.RetentionPeriod);
        }

        private double CalculateComplianceScore(ComplianceValidationResult result)
        {
            var score = 100.0;

            // Deduct for missing events;
            score -= result.MissingRequiredEvents.Count * 5;

            // Deduct for tampering;
            if (result.TamperingDetected)
                score -= 30;

            // Deduct for retention non-compliance;
            if (!result.RetentionCompliant)
                score -= 20;

            return Math.Max(0, score);
        }

        private string GenerateCorrelationId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private ClaimsPrincipal CreateClaimsPrincipal(string userId, string userName)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId ?? "Unknown"),
                new Claim(ClaimTypes.Name, userName ?? "Unknown")
            };

            var identity = new ClaimsIdentity(claims, "AuditLogger");
            return new ClaimsPrincipal(identity);
        }

        private AuditSeverity ParseSeverity(string severity)
        {
            return Enum.TryParse<AuditSeverity>(severity, true, out var result)
                ? result;
                : AuditSeverity.Information;
        }

        private string GetSessionId()
        {
            return System.Web.HttpContext.Current?.Session?.SessionID ?? "NoSession";
        }

        private string GetRequestId()
        {
            return System.Web.HttpContext.Current?.Request?.Headers["X-Request-ID"] ?? Guid.NewGuid().ToString("N");
        }

        private string GetClientIPAddress()
        {
            return System.Web.HttpContext.Current?.Request?.UserHostAddress ?? "127.0.0.1";
        }

        private string GetUserAgent()
        {
            return System.Web.HttpContext.Current?.Request?.UserAgent ?? "Unknown";
        }

        private string GetAssemblyVersion()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
        }

        private void OnAuditLogged(AuditLoggedEventArgs e)
        {
            AuditLogged?.Invoke(this, e);
        }

        private void OnComplianceViolationDetected(ComplianceViolationEventArgs e)
        {
            ComplianceViolationDetected?.Invoke(this, e);
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Audit statistics for reporting and monitoring;
    /// </summary>
    public class AuditStatistics;
    {
        public int TotalEntries { get; set; }
        public long TotalEntriesOverall { get; set; }
        public long FailedEntries { get; set; }
        public TimeSpan Uptime { get; set; }
        public Dictionary<string, int> ByCategory { get; set; }
        public Dictionary<string, int> BySeverity { get; set; }
        public Dictionary<string, int> ByStatus { get; set; }
        public Dictionary<string, int> ByUser { get; set; }
        public double AverageResponseTime { get; set; }
        public double ComplianceRate { get; set; }
    }

    /// <summary>
    /// Compliance validation result;
    /// </summary>
    public class ComplianceValidationResult;
    {
        public DateTime ValidationDate { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int TotalEntriesValidated { get; set; }
        public List<string> MissingRequiredEvents { get; set; }
        public bool TamperingDetected { get; set; }
        public bool RetentionCompliant { get; set; }
        public double ComplianceScore { get; set; }
    }

    /// <summary>
    /// Event arguments for audit logged event;
    /// </summary>
    public class AuditLoggedEventArgs : EventArgs;
    {
        public AuditLogEntry AuditEntry { get; }

        public AuditLoggedEventArgs(AuditLogEntry auditEntry)
        {
            AuditEntry = auditEntry
        }
    }

    /// <summary>
    /// Event arguments for compliance violation;
    /// </summary>
    public class ComplianceViolationEventArgs : EventArgs;
    {
        public string AuditId { get; }
        public string RuleName { get; }
        public string ViolationDescription { get; }
        public DateTime DetectedAt { get; }

        public ComplianceViolationEventArgs(
            string auditId,
            string ruleName,
            string violationDescription,
            DateTime detectedAt)
        {
            AuditId = auditId;
            RuleName = ruleName;
            ViolationDescription = violationDescription;
            DetectedAt = detectedAt;
        }
    }

    /// <summary>
    /// Event published when audit batch is processed;
    /// </summary>
    public class AuditBatchProcessedEvent : IEvent;
    {
        public int BatchSize { get; set; }
        public DateTime ProcessedAt { get; set; }
        public List<AuditCategory> Categories { get; set; }
    }

    /// <summary>
    /// Data for audit report generation;
    /// </summary>
    public class AuditReportData;
    {
        public List<AuditLogEntry> Entries { get; set; }
        public AuditSearchCriteria Criteria { get; set; }
        public DateTime GeneratedAt { get; set; }
        public string GeneratedBy { get; set; }
    }

    /// <summary>
    /// Custom exception for audit logging errors;
    /// </summary>
    public class AuditLogException : Exception
    {
        public AuditLogException(string message) : base(message) { }
        public AuditLogException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Interface for audit logging;
    /// </summary>
    public interface IAuditLogger;
    {
        Task<string> LogAsync(
            AuditCategory category,
            string eventName,
            string description,
            AuditSeverity severity = AuditSeverity.Information,
            AuditStatus status = AuditStatus.Success,
            Dictionary<string, object> properties = null,
            Exception exception = null,
            string correlationId = null,
            ClaimsPrincipal user = null,
            long? executionTimeMs = null);

        Task<string> LogAuthenticationAsync(
            string userId,
            string userName,
            string authenticationMethod,
            AuditStatus status,
            string sourceIP = null,
            Dictionary<string, object> additionalInfo = null);

        Task<string> LogAuthorizationAsync(
            string userId,
            string userName,
            string resource,
            string action,
            AuditStatus status,
            string reason = null);

        Task<string> LogDataAccessAsync(
            string userId,
            string userName,
            string operation,
            string entityType,
            string entityId,
            Dictionary<string, object> beforeState = null,
            Dictionary<string, object> afterState = null,
            AuditStatus status = AuditStatus.Success);

        Task<string> LogConfigurationChangeAsync(
            string userId,
            string userName,
            string configurationSection,
            string settingName,
            object oldValue,
            object newValue,
            string reason = null);

        Task<string> LogSecurityPolicyAsync(
            AuditSeverity severity,
            string policyName,
            string description,
            AuditStatus status,
            Dictionary<string, object> details = null);

        Task<string> LogSecurityIncidentAsync(
            string incidentId,
            string category,
            string severity,
            string description,
            string sourceSystem);

        Task<string> LogUserActivityAsync(
            string userId,
            string userName,
            string activity,
            string resource,
            Dictionary<string, object> details = null);

        Task<AuditLogEntry> GetAuditEntryAsync(string auditId);
        Task<List<AuditLogEntry>> SearchAuditLogsAsync(AuditSearchCriteria criteria);
        Task<AuditStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null);
        Task<byte[]> ExportAuditLogsAsync(
            AuditSearchCriteria criteria,
            ExportFormat format = ExportFormat.Json,
            bool includeSensitiveData = false);
        Task FlushAsync();
        Task<int> ArchiveOldLogsAsync(DateTime olderThan);
        Task<ComplianceValidationResult> ValidateComplianceAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> complianceRules = null);

        event EventHandler<AuditLoggedEventArgs> AuditLogged;
        event EventHandler<ComplianceViolationEventArgs> ComplianceViolationDetected;
    }

    #endregion;
}
