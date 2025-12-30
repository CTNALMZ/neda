using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Common.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace NEDA.Core.Logging
{
    /// <summary>
    /// Sistem genelinde audit log (denetim kaydı) tutmak için kullanılan servis.
    /// Tüm kritik işlemlerin kim tarafından, ne zaman ve ne yapıldığını kaydeder.
    /// </summary>
    public class AuditLogger : IAuditLogger, IDisposable
    {
        private readonly ILogger<AuditLogger> _logger;
        private readonly AuditLogConfiguration _config;
        private readonly IAuditLogStorage _storage;
        private readonly ISystemClock _systemClock;
        private readonly object _lock = new object();
        private readonly Queue<AuditLogEntry> _logQueue = new Queue<AuditLogEntry>();
        private bool _isProcessingQueue = false;

        /// <summary>
        /// AuditLogger constructor;
        /// </summary>
        public AuditLogger(
            ILogger<AuditLogger> logger,
            AuditLogConfiguration config,
            IAuditLogStorage storage,
            ISystemClock systemClock)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _systemClock = systemClock ?? throw new ArgumentNullException(nameof(systemClock));

            InitializeQueueProcessor();
        }

        /// <summary>
        /// Audit log kaydı oluşturur;
        /// </summary>
        public async Task LogAsync(AuditLogEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            try
            {
                // Entry'yi tamamla;
                CompleteEntry(entry);

                // Kritik log ise hemen kaydet;
                if (entry.Severity == AuditLogSeverity.Critical ||
                    entry.Category == AuditLogCategory.Security)
                {
                    await SaveLogImmediatelyAsync(entry);
                }
                else
                {
                    // Queue'ya ekle;
                    lock (_lock)
                    {
                        _logQueue.Enqueue(entry);

                        // Queue threshold kontrolü;
                        if (_logQueue.Count >= _config.QueueThreshold)
                        {
                            _ = ProcessQueueAsync();
                        }
                    }
                }

                _logger.LogDebug("Audit log queued: {EventId}", entry.EventId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log audit entry: {EventId}", entry?.EventId);
                throw new AuditLogException(ErrorCodes.AUDIT_LOG_FAILED,
                    $"Failed to log audit entry: {entry?.EventId}", ex);
            }
        }

        /// <summary>
        /// Belirli bir kullanıcı için audit log kaydı oluşturur;
        /// </summary>
        public async Task LogForUserAsync(string userId, string action, string details,
            AuditLogSeverity severity = AuditLogSeverity.Information)
        {
            var entry = new AuditLogEntry
            {
                UserId = userId,
                Action = action,
                Details = details,
                Severity = severity,
                Category = AuditLogCategory.UserActivity
            };

            await LogAsync(entry);
        }

        /// <summary>
        /// Sistem olayı için audit log kaydı oluşturur;
        /// </summary>
        public async Task LogSystemEventAsync(string eventName, string description,
            AuditLogSeverity severity = AuditLogSeverity.Information)
        {
            var entry = new AuditLogEntry
            {
                Action = eventName,
                Details = description,
                Severity = severity,
                Category = AuditLogCategory.SystemEvent,
                UserId = "SYSTEM"
            };

            await LogAsync(entry);
        }

        /// <summary>
        /// Belirli bir zaman aralığındaki audit logları getirir;
        /// </summary>
        public async Task<IEnumerable<AuditLogEntry>> GetLogsAsync(
            DateTime from,
            DateTime to,
            string userId = null,
            AuditLogCategory? category = null)
        {
            try
            {
                // Önce queue'daki logları işle;
                await ProcessQueueAsync();

                return await _storage.GetLogsAsync(from, to, userId, category);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve audit logs");
                throw new AuditLogException(ErrorCodes.AUDIT_LOG_RETRIEVAL_FAILED,
                    "Failed to retrieve audit logs", ex);
            }
        }

        /// <summary>
        /// Belirli bir olay ID'sine göre log getirir;
        /// </summary>
        public async Task<AuditLogEntry> GetLogByEventIdAsync(string eventId)
        {
            try
            {
                return await _storage.GetLogByEventIdAsync(eventId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve audit log by event ID: {EventId}", eventId);
                throw new AuditLogException(ErrorCodes.AUDIT_LOG_RETRIEVAL_FAILED,
                    $"Failed to retrieve audit log by event ID: {eventId}", ex);
            }
        }

        /// <summary>
        /// Audit loglarını temizler (config'e göre retention policy)
        /// </summary>
        public async Task CleanupOldLogsAsync()
        {
            try
            {
                var cutoffDate = _systemClock.UtcNow.AddDays(-_config.RetentionDays);
                await _storage.DeleteLogsOlderThanAsync(cutoffDate);

                _logger.LogInformation("Cleaned up audit logs older than {CutoffDate}", cutoffDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old audit logs");
                throw new AuditLogException(ErrorCodes.AUDIT_LOG_CLEANUP_FAILED,
                    "Failed to cleanup old audit logs", ex);
            }
        }

        /// <summary>
        /// Audit log istatistiklerini getirir;
        /// </summary>
        public async Task<AuditLogStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            try
            {
                var start = startDate ?? _systemClock.UtcNow.AddDays(-30);
                var end = endDate ?? _systemClock.UtcNow;

                return await _storage.GetStatisticsAsync(start, end);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get audit log statistics");
                throw new AuditLogException(ErrorCodes.AUDIT_LOG_STATISTICS_FAILED,
                    "Failed to get audit log statistics", ex);
            }
        }

        /// <summary>
        /// Export audit logs to file;
        /// </summary>
        public async Task<string> ExportLogsAsync(DateTime from, DateTime to, ExportFormat format)
        {
            try
            {
                var logs = await GetLogsAsync(from, to);

                return format switch
                {
                    ExportFormat.Json => ExportToJson(logs),
                    ExportFormat.Csv => ExportToCsv(logs),
                    ExportFormat.Xml => ExportToXml(logs),
                    _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export audit logs");
                throw new AuditLogException(ErrorCodes.AUDIT_LOG_EXPORT_FAILED,
                    "Failed to export audit logs", ex);
            }
        }

        #region Private Methods

        private void CompleteEntry(AuditLogEntry entry)
        {
            entry.EventId = Guid.NewGuid().ToString();
            entry.Timestamp = _systemClock.UtcNow;
            entry.MachineName = Environment.MachineName;
            entry.ApplicationName = _config.ApplicationName;

            if (string.IsNullOrEmpty(entry.IpAddress))
            {
                entry.IpAddress = GetClientIpAddress();
            }
        }

        private async Task SaveLogImmediatelyAsync(AuditLogEntry entry)
        {
            try
            {
                await _storage.SaveAsync(entry);
                _logger.LogDebug("Critical audit log saved immediately: {EventId}", entry.EventId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save critical audit log: {EventId}", entry.EventId);
                // Fallback: dosyaya kaydet;
                SaveToFallbackStorage(entry);
            }
        }

        private void SaveToFallbackStorage(AuditLogEntry entry)
        {
            try
            {
                if (!_config.EnableFallbackStorage)
                    return;

                if (!Directory.Exists(_config.FallbackPath))
                {
                    Directory.CreateDirectory(_config.FallbackPath);
                }

                var logPath = Path.Combine(_config.FallbackPath,
                    $"audit_fallback_{DateTime.Now:yyyyMMdd}.log");

                var logLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}|{entry.ToJson()}{Environment.NewLine}";
                File.AppendAllText(logPath, logLine);

                _logger.LogWarning("Audit log saved to fallback storage: {Path}", logPath);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "CRITICAL: Failed to save audit log to fallback storage!");
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (_isProcessingQueue) return;

            List<AuditLogEntry> batch = null;

            lock (_lock)
            {
                if (_isProcessingQueue) return;
                _isProcessingQueue = true;

                var batchSize = Math.Min(_logQueue.Count, _config.BatchSize);
                batch = new List<AuditLogEntry>(batchSize);

                for (int i = 0; i < batchSize; i++)
                {
                    batch.Add(_logQueue.Dequeue());
                }
            }

            try
            {
                if (batch != null && batch.Count > 0)
                {
                    await _storage.SaveBatchAsync(batch);
                    _logger.LogDebug("Processed {Count} audit logs from queue", batch.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process audit log queue");
                // Batch'i fallback storage'a kaydet;
                if (batch != null)
                {
                    foreach (var entry in batch)
                    {
                        SaveToFallbackStorage(entry);
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isProcessingQueue = false;
                }
            }
        }

        private void InitializeQueueProcessor()
        {
            // Periyodik queue processing;
            var timer = new System.Threading.Timer(
                _ => _ = ProcessQueueAsync(),
                null,
                TimeSpan.FromSeconds(_config.ProcessIntervalSeconds),
                TimeSpan.FromSeconds(_config.ProcessIntervalSeconds));
            // Timer referansı field'da tutulursa dispose edilebilir;
            // şu anlık fire-and-forget kullanılıyor.
        }

        private string GetClientIpAddress()
        {
            // Implementation depends on your application context;
            // This is a simplified version;
            return System.Net.Dns.GetHostName();
        }

        private string ExportToJson(IEnumerable<AuditLogEntry> logs)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            return JsonSerializer.Serialize(logs, options);
        }

        private string ExportToCsv(IEnumerable<AuditLogEntry> logs)
        {
            var csvLines = new List<string>
            {
                "Timestamp,EventId,UserId,Action,Category,Severity,Details,IpAddress,MachineName"
            };

            foreach (var log in logs)
            {
                var escapedDetails = log.Details?.Replace("\"", "\"\"") ?? "";
                csvLines.Add(
                    $"\"{log.Timestamp:yyyy-MM-dd HH:mm:ss}\"," +
                    $"\"{log.EventId}\"," +
                    $"\"{log.UserId}\"," +
                    $"\"{log.Action}\"," +
                    $"\"{log.Category}\"," +
                    $"\"{log.Severity}\"," +
                    $"\"{escapedDetails}\"," +
                    $"\"{log.IpAddress}\"," +
                    $"\"{log.MachineName}\"");
            }

            return string.Join(Environment.NewLine, csvLines);
        }

        private string ExportToXml(IEnumerable<AuditLogEntry> logs)
        {
            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine("<AuditLogs>");

            foreach (var log in logs)
            {
                xml.AppendLine("  <LogEntry>");
                xml.AppendLine($"    <Timestamp>{log.Timestamp:yyyy-MM-dd HH:mm:ss}</Timestamp>");
                xml.AppendLine($"    <EventId>{log.EventId}</EventId>");
                xml.AppendLine($"    <UserId>{log.UserId}</UserId>");
                xml.AppendLine($"    <Action>{log.Action}</Action>");
                xml.AppendLine($"    <Category>{log.Category}</Category>");
                xml.AppendLine($"    <Severity>{log.Severity}</Severity>");
                xml.AppendLine($"    <Details><![CDATA[{log.Details}]]></Details>");
                xml.AppendLine($"    <IpAddress>{log.IpAddress}</IpAddress>");
                xml.AppendLine($"    <MachineName>{log.MachineName}</MachineName>");
                xml.AppendLine("  </LogEntry>");
            }

            xml.AppendLine("</AuditLogs>");
            return xml.ToString();
        }

        #endregion

        #region IDisposable Implementation

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        // Son queue'yu işle;
                        ProcessQueueAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to flush audit log queue on dispose");
                    }
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~AuditLogger()
        {
            Dispose(false);
        }

        #endregion
    }

    /// <summary>
    /// Audit log entry model;
    /// </summary>
    public class AuditLogEntry
    {
        public string EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserId { get; set; }
        public string Action { get; set; }
        public string Details { get; set; }
        public AuditLogCategory Category { get; set; }
        public AuditLogSeverity Severity { get; set; }
        public string IpAddress { get; set; }
        public string MachineName { get; set; }
        public string ApplicationName { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();

        public string ToJson()
        {
            return JsonSerializer.Serialize(this);
        }
    }

    /// <summary>
    /// Audit log categories;
    /// </summary>
    public enum AuditLogCategory
    {
        UserActivity,
        Security,
        SystemEvent,
        DataAccess,
        ConfigurationChange,
        ApplicationError,
        Compliance,
        Performance
    }

    /// <summary>
    /// Audit log severity levels;
    /// </summary>
    public enum AuditLogSeverity
    {
        Debug,
        Information,
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Export formats for audit logs;
    /// </summary>
    public enum ExportFormat
    {
        Json,
        Csv,
        Xml
    }

    /// <summary>
    /// Audit log configuration;
    /// </summary>
    public class AuditLogConfiguration
    {
        public string ApplicationName { get; set; } = "NEDA";
        public int RetentionDays { get; set; } = 365;
        public int QueueThreshold { get; set; } = 100;
        public int BatchSize { get; set; } = 50;
        public int ProcessIntervalSeconds { get; set; } = 30;
        public string FallbackPath { get; set; } = "Logs/Audit";
        public bool EnableFallbackStorage { get; set; } = true;
    }

    /// <summary>
    /// Audit log statistics;
    /// </summary>
    public class AuditLogStatistics
    {
        public int TotalLogs { get; set; }
        public int CriticalCount { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public int InformationCount { get; set; }
        public Dictionary<AuditLogCategory, int> CountByCategory { get; set; }
        public Dictionary<string, int> TopUsers { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
    }

    /// <summary>
    /// Audit log storage interface;
    /// </summary>
    public interface IAuditLogStorage
    {
        Task SaveAsync(AuditLogEntry entry);
        Task SaveBatchAsync(IEnumerable<AuditLogEntry> entries);
        Task<IEnumerable<AuditLogEntry>> GetLogsAsync(
            DateTime from, DateTime to,
            string userId = null,
            AuditLogCategory? category = null);
        Task<AuditLogEntry> GetLogByEventIdAsync(string eventId);
        Task DeleteLogsOlderThanAsync(DateTime cutoffDate);
        Task<AuditLogStatistics> GetStatisticsAsync(DateTime start, DateTime end);
    }

    /// <summary>
    /// System clock abstraction for testability;
    /// </summary>
    public interface ISystemClock
    {
        DateTime UtcNow { get; }
        DateTime Now { get; }
    }

    /// <summary>
    /// Audit logger interface;
    /// </summary>
    public interface IAuditLogger : IDisposable
    {
        Task LogAsync(AuditLogEntry entry);
        Task LogForUserAsync(string userId, string action, string details,
            AuditLogSeverity severity = AuditLogSeverity.Information);
        Task LogSystemEventAsync(string eventName, string description,
            AuditLogSeverity severity = AuditLogSeverity.Information);
        Task<IEnumerable<AuditLogEntry>> GetLogsAsync(
            DateTime from, DateTime to,
            string userId = null,
            AuditLogCategory? category = null);
        Task<AuditLogEntry> GetLogByEventIdAsync(string eventId);
        Task CleanupOldLogsAsync();
        Task<AuditLogStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null);
        Task<string> ExportLogsAsync(DateTime from, DateTime to, ExportFormat format);
    }

    /// <summary>
    /// Custom exception for audit logging;
    /// </summary>
    public class AuditLogException : Exception
    {
        public string ErrorCode { get; }

        public AuditLogException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public AuditLogException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }
}
