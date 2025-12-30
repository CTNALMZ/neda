using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Logging;
using NEDA.SecurityModules.Encryption;

namespace NEDA.Interface.InteractionManager.ConversationHistory;
{
    /// <summary>
    /// Represents a conversation log entry with comprehensive metadata;
    /// </summary>
    public class ConversationLogEntry
    {
        /// <summary>
        /// Unique identifier for the log entry
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Session identifier for grouping related conversations;
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// User identifier associated with the conversation;
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// Message content (encrypted at rest)
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Type of message (UserInput, SystemResponse, Error, etc.)
        /// </summary>
        public MessageType MessageType { get; set; }

        /// <summary>
        /// Context or intent of the conversation;
        /// </summary>
        public string Context { get; set; }

        /// <summary>
        /// Sentiment analysis score (-1.0 to 1.0)
        /// </summary>
        public double SentimentScore { get; set; }

        /// <summary>
        /// Confidence level of the response (0.0 to 1.0)
        /// </summary>
        public double Confidence { get; set; }

        /// <summary>
        /// Request processing duration in milliseconds;
        /// </summary>
        public long ProcessingTimeMs { get; set; }

        /// <summary>
        /// Source of the message (Voice, Text, API, etc.)
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Additional metadata in JSON format;
        /// </summary>
        public string Metadata { get; set; }

        /// <summary>
        /// Timestamp when the entry was created;
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// IP address or device identifier;
        /// </summary>
        public string SourceAddress { get; set; }

        /// <summary>
        /// Application or module that generated the log;
        /// </summary>
        public string Application { get; set; }

        /// <summary>
        /// Version of the application;
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// Language of the conversation;
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Whether the entry has been archived;
        /// </summary>
        public bool IsArchived { get; set; }

        /// <summary>
        /// Tags for categorization and search;
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Type of message in conversation;
    /// </summary>
    public enum MessageType;
    {
        UserInput,
        SystemResponse,
        SystemCommand,
        Error,
        Warning,
        Information,
        Debug,
        Confirmation,
        Clarification,
        Authentication,
        Authorization;
    }

    /// <summary>
    /// Interface for conversation log storage operations;
    /// </summary>
    public interface ILogStore;
    {
        /// <summary>
        /// Stores a conversation log entry asynchronously;
        /// </summary>
        Task<Guid> StoreAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a specific log entry by ID;
        /// </summary>
        Task<ConversationLogEntry> RetrieveAsync(Guid entryId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves conversation logs for a specific session;
        /// </summary>
        Task<IEnumerable<ConversationLogEntry>> GetSessionLogsAsync(string sessionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves conversation logs for a specific user;
        /// </summary>
        Task<IEnumerable<ConversationLogEntry>> GetUserLogsAsync(string userId, int limit = 100, CancellationToken cancellationToken = default);

        /// <summary>
        /// Searches logs based on criteria;
        /// </summary>
        Task<IEnumerable<ConversationLogEntry>> SearchAsync(LogSearchCriteria criteria, CancellationToken cancellationToken = default);

        /// <summary>
        /// Archives old logs based on retention policy;
        /// </summary>
        Task<int> ArchiveOldLogsAsync(DateTime cutoffDate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Purges logs permanently based on criteria;
        /// </summary>
        Task<int> PurgeLogsAsync(LogPurgeCriteria criteria, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets conversation statistics;
        /// </summary>
        Task<ConversationStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Exports logs to specified format;
        /// </summary>
        Task<byte[]> ExportLogsAsync(ExportFormat format, LogExportCriteria criteria, CancellationToken cancellationToken = default);

        /// <summary>
        /// Compresses logs for storage optimization;
        /// </summary>
        Task CompressLogsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates log store health and integrity;
        /// </summary>
        Task<LogStoreHealth> ValidateHealthAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Search criteria for log queries;
    /// </summary>
    public class LogSearchCriteria;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public MessageType? MessageType { get; set; }
        public string Context { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string SearchText { get; set; }
        public double? MinConfidence { get; set; }
        public double? MaxConfidence { get; set; }
        public string Source { get; set; }
        public string[] Tags { get; set; }
        public bool? IsArchived { get; set; }
        public int Skip { get; set; }
        public int Take { get; set; } = 100;
        public string OrderBy { get; set; } = "CreatedAt";
        public bool Descending { get; set; } = true;
    }

    /// <summary>
    /// Criteria for purging logs;
    /// </summary>
    public class LogPurgeCriteria;
    {
        public DateTime? OlderThan { get; set; }
        public MessageType? MessageType { get; set; }
        public string SessionId { get; set; }
        public bool IncludeArchived { get; set; }
        public int? MaxEntriesToPurge { get; set; }
    }

    /// <summary>
    /// Criteria for log export;
    /// </summary>
    public class LogExportCriteria;
    {
        public LogSearchCriteria SearchCriteria { get; set; }
        public bool IncludeMetadata { get; set; }
        public bool CompressOutput { get; set; }
        public string Timezone { get; set; } = "UTC";
    }

    /// <summary>
    /// Export formats for logs;
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Xml,
        Csv,
        Text,
        Pdf,
        Excel;
    }

    /// <summary>
    /// Statistics about conversations;
    /// </summary>
    public class ConversationStatistics;
    {
        public int TotalConversations { get; set; }
        public int ActiveSessions { get; set; }
        public int UniqueUsers { get; set; }
        public double AverageConfidence { get; set; }
        public double AverageResponseTimeMs { get; set; }
        public Dictionary<MessageType, int> MessageTypeCounts { get; set; }
        public Dictionary<string, int> ContextDistribution { get; set; }
        public DateTime? FirstLogDate { get; set; }
        public DateTime? LastLogDate { get; set; }
        public int ArchivedCount { get; set; }
        public Dictionary<string, int> LanguageDistribution { get; set; }
        public Dictionary<string, int> SourceDistribution { get; set; }
    }

    /// <summary>
    /// Health status of the log store;
    /// </summary>
    public class LogStoreHealth;
    {
        public bool IsHealthy { get; set; }
        public string Status { get; set; }
        public long TotalEntries { get; set; }
        public long StorageSizeBytes { get; set; }
        public DateTime? LastBackupDate { get; set; }
        public Dictionary<string, object> Metrics { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> Errors { get; set; }
    }

    /// <summary>
    /// Implementation of conversation log storage with encryption and compression support;
    /// </summary>
    public class LogStore : ILogStore, IDisposable;
    {
        private readonly ILogger<LogStore> _logger;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IDbConnection _dbConnection;
        private readonly IAuditLogger _auditLogger;
        private readonly SemaphoreSlim _storageLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        private const string LogTableName = "ConversationLogs";
        private const string ArchiveTableName = "ArchivedConversationLogs";
        private const int MaxBatchSize = 1000;
        private const int CompressionThreshold = 10000;

        /// <summary>
        /// Initializes a new instance of LogStore;
        /// </summary>
        public LogStore(
            ILogger<LogStore> logger,
            ICryptoEngine cryptoEngine,
            IDbConnection dbConnection,
            IAuditLogger auditLogger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _dbConnection = dbConnection ?? throw new ArgumentNullException(nameof(dbConnection));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

            InitializeDatabase();
        }

        /// <summary>
        /// Initializes the database schema if not exists;
        /// </summary>
        private void InitializeDatabase()
        {
            try
            {
                _storageLock.Wait();

                if (_dbConnection.State != ConnectionState.Open)
                {
                    _dbConnection.Open();
                }

                using var command = _dbConnection.CreateCommand();

                // Create main logs table;
                command.CommandText = $@"
                    CREATE TABLE IF NOT EXISTS {LogTableName} (
                        Id TEXT PRIMARY KEY,
                        SessionId TEXT NOT NULL,
                        UserId TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        MessageType INTEGER NOT NULL,
                        Context TEXT,
                        SentimentScore REAL,
                        Confidence REAL,
                        ProcessingTimeMs INTEGER,
                        Source TEXT,
                        Metadata TEXT,
                        CreatedAt DATETIME NOT NULL,
                        SourceAddress TEXT,
                        Application TEXT,
                        Version TEXT,
                        Language TEXT,
                        IsArchived INTEGER DEFAULT 0,
                        Tags TEXT,
                        INDEX idx_session (SessionId),
                        INDEX idx_user (UserId),
                        INDEX idx_created (CreatedAt),
                        INDEX idx_type (MessageType),
                        INDEX idx_context (Context),
                        INDEX idx_archived (IsArchived)
                    )";

                command.ExecuteNonQuery();

                // Create archived logs table;
                command.CommandText = $@"
                    CREATE TABLE IF NOT EXISTS {ArchiveTableName} (
                        Id TEXT PRIMARY KEY,
                        SessionId TEXT NOT NULL,
                        UserId TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        MessageType INTEGER NOT NULL,
                        Context TEXT,
                        SentimentScore REAL,
                        Confidence REAL,
                        ProcessingTimeMs INTEGER,
                        Source TEXT,
                        Metadata TEXT,
                        CreatedAt DATETIME NOT NULL,
                        SourceAddress TEXT,
                        Application TEXT,
                        Version TEXT,
                        Language TEXT,
                        ArchivedAt DATETIME NOT NULL,
                        CompressionLevel INTEGER,
                        Tags TEXT,
                        INDEX idx_archive_session (SessionId),
                        INDEX idx_archive_created (CreatedAt),
                        INDEX idx_archive_archived (ArchivedAt)
                    )";

                command.ExecuteNonQuery();

                _logger.LogInformation("LogStore database initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize LogStore database");
                throw new LogStoreException("Database initialization failed", ex);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<Guid> StoreAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                // Encrypt sensitive content;
                var encryptedContent = await _cryptoEngine.EncryptAsync(entry.Content, cancellationToken);

                using var command = _dbConnection.CreateCommand();
                command.CommandText = $@"
                    INSERT INTO {LogTableName} (
                        Id, SessionId, UserId, Content, MessageType, Context, 
                        SentimentScore, Confidence, ProcessingTimeMs, Source,
                        Metadata, CreatedAt, SourceAddress, Application, 
                        Version, Language, IsArchived, Tags;
                    ) VALUES (
                        @Id, @SessionId, @UserId, @Content, @MessageType, @Context,
                        @SentimentScore, @Confidence, @ProcessingTimeMs, @Source,
                        @Metadata, @CreatedAt, @SourceAddress, @Application,
                        @Version, @Language, @IsArchived, @Tags;
                    )";

                AddParameters(command, entry, encryptedContent);

                await command.ExecuteNonQueryAsync(cancellationToken);

                // Log audit trail;
                await _auditLogger.LogAsync(new AuditEntry
                {
                    Action = "LogStore.Store",
                    EntityId = entry.Id.ToString(),
                    EntityType = "ConversationLog",
                    UserId = entry.UserId,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Stored log entry for session: {entry.SessionId}"
                }, cancellationToken);

                _logger.LogDebug("Stored conversation log entry {EntryId} for session {SessionId}",
                    entry.Id, entry.SessionId);

                return entry.Id;
            }
            catch (DbException dbEx)
            {
                _logger.LogError(dbEx, "Database error while storing log entry");
                throw new LogStoreException("Failed to store log entry", dbEx);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Log storage operation was cancelled");
                throw;
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<ConversationLogEntry> RetrieveAsync(Guid entryId, CancellationToken cancellationToken = default)
        {
            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                using var command = _dbConnection.CreateCommand();
                command.CommandText = $@"
                    SELECT * FROM {LogTableName} 
                    WHERE Id = @Id AND IsArchived = 0;
                    UNION ALL;
                    SELECT * FROM {ArchiveTableName} 
                    WHERE Id = @Id";

                command.Parameters.Add(CreateParameter("@Id", entryId.ToString()));

                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    var entry = MapToEntry(reader);

                    // Decrypt content;
                    entry.Content = await _cryptoEngine.DecryptAsync(entry.Content, cancellationToken);

                    return entry
                }

                return null;
            }
            catch (DbException dbEx)
            {
                _logger.LogError(dbEx, "Database error while retrieving log entry {EntryId}", entryId);
                throw new LogStoreException($"Failed to retrieve log entry {entryId}", dbEx);
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<ConversationLogEntry>> GetSessionLogsAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                using var command = _dbConnection.CreateCommand();
                command.CommandText = $@"
                    SELECT * FROM {LogTableName} 
                    WHERE SessionId = @SessionId AND IsArchived = 0;
                    ORDER BY CreatedAt DESC;
                    LIMIT 500";

                command.Parameters.Add(CreateParameter("@SessionId", sessionId));

                var entries = new List<ConversationLogEntry>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var entry = MapToEntry(reader);
                    entry.Content = await _cryptoEngine.DecryptAsync(entry.Content, cancellationToken);
                    entries.Add(entry);
                }

                return entries;
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<ConversationLogEntry>> GetUserLogsAsync(string userId, int limit = 100, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (limit <= 0 || limit > 1000)
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000");

            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                using var command = _dbConnection.CreateCommand();
                command.CommandText = $@"
                    SELECT * FROM {LogTableName} 
                    WHERE UserId = @UserId AND IsArchived = 0;
                    ORDER BY CreatedAt DESC;
                    LIMIT @Limit";

                command.Parameters.Add(CreateParameter("@UserId", userId));
                command.Parameters.Add(CreateParameter("@Limit", limit));

                var entries = new List<ConversationLogEntry>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var entry = MapToEntry(reader);
                    entry.Content = await _cryptoEngine.DecryptAsync(entry.Content, cancellationToken);
                    entries.Add(entry);
                }

                return entries;
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<ConversationLogEntry>> SearchAsync(LogSearchCriteria criteria, CancellationToken cancellationToken = default)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                var query = BuildSearchQuery(criteria, out var parameters);
                using var command = _dbConnection.CreateCommand();
                command.CommandText = query;

                foreach (var param in parameters)
                {
                    command.Parameters.Add(param);
                }

                var entries = new List<ConversationLogEntry>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var entry = MapToEntry(reader);
                    entry.Content = await _cryptoEngine.DecryptAsync(entry.Content, cancellationToken);
                    entries.Add(entry);
                }

                return entries;
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<int> ArchiveOldLogsAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
        {
            if (cutoffDate > DateTime.UtcNow.AddDays(-1))
                throw new ArgumentException("Cutoff date must be at least 1 day in the past", nameof(cutoffDate));

            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                using var transaction = _dbConnection.BeginTransaction();

                try
                {
                    // Move old logs to archive table;
                    using var command = _dbConnection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $@"
                        INSERT INTO {ArchiveTableName} 
                        SELECT *, @ArchivedAt, 0, Tags; 
                        FROM {LogTableName} 
                        WHERE CreatedAt < @CutoffDate AND IsArchived = 0;
                        LIMIT {MaxBatchSize}";

                    command.Parameters.Add(CreateParameter("@CutoffDate", cutoffDate));
                    command.Parameters.Add(CreateParameter("@ArchivedAt", DateTime.UtcNow));

                    var archivedCount = await command.ExecuteNonQueryAsync(cancellationToken);

                    if (archivedCount > 0)
                    {
                        // Delete archived logs from main table;
                        command.CommandText = $@"
                            DELETE FROM {LogTableName} 
                            WHERE CreatedAt < @CutoffDate AND IsArchived = 0;
                            LIMIT {archivedCount}";

                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }

                    transaction.Commit();

                    _logger.LogInformation("Archived {Count} log entries older than {CutoffDate}",
                        archivedCount, cutoffDate);

                    return archivedCount;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<int> PurgeLogsAsync(LogPurgeCriteria criteria, CancellationToken cancellationToken = default)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                var query = BuildPurgeQuery(criteria, out var parameters);
                using var command = _dbConnection.CreateCommand();
                command.CommandText = query;

                foreach (var param in parameters)
                {
                    command.Parameters.Add(param);
                }

                if (criteria.MaxEntriesToPurge.HasValue)
                {
                    command.CommandText += $" LIMIT {criteria.MaxEntriesToPurge.Value}";
                }

                var purgedCount = await command.ExecuteNonQueryAsync(cancellationToken);

                // Log audit trail;
                await _auditLogger.LogAsync(new AuditEntry
                {
                    Action = "LogStore.Purge",
                    EntityType = "ConversationLog",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Purged {purgedCount} log entries based on criteria"
                }, cancellationToken);

                _logger.LogInformation("Purged {Count} log entries", purgedCount);

                return purgedCount;
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<ConversationStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null, CancellationToken cancellationToken = default)
        {
            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                using var command = _dbConnection.CreateCommand();
                command.CommandText = @"
                    SELECT; 
                        COUNT(*) as Total,
                        COUNT(DISTINCT SessionId) as ActiveSessions,
                        COUNT(DISTINCT UserId) as UniqueUsers,
                        AVG(Confidence) as AvgConfidence,
                        AVG(ProcessingTimeMs) as AvgResponseTime,
                        MIN(CreatedAt) as FirstLog,
                        MAX(CreatedAt) as LastLog,
                        SUM(CASE WHEN IsArchived = 1 THEN 1 ELSE 0 END) as ArchivedCount;
                    FROM ConversationLogs;
                    WHERE (@StartDate IS NULL OR CreatedAt >= @StartDate)
                      AND (@EndDate IS NULL OR CreatedAt <= @EndDate)";

                command.Parameters.Add(CreateParameter("@StartDate", startDate));
                command.Parameters.Add(CreateParameter("@EndDate", endDate));

                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    var stats = new ConversationStatistics;
                    {
                        TotalConversations = reader.GetInt32(0),
                        ActiveSessions = reader.GetInt32(1),
                        UniqueUsers = reader.GetInt32(2),
                        AverageConfidence = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                        AverageResponseTimeMs = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                        FirstLogDate = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
                        LastLogDate = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                        ArchivedCount = reader.GetInt32(7),
                        MessageTypeCounts = new Dictionary<MessageType, int>(),
                        ContextDistribution = new Dictionary<string, int>(),
                        LanguageDistribution = new Dictionary<string, int>(),
                        SourceDistribution = new Dictionary<string, int>()
                    };

                    // Get additional distribution statistics;
                    await PopulateDistributionStats(stats, startDate, endDate, cancellationToken);

                    return stats;
                }

                return new ConversationStatistics();
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<byte[]> ExportLogsAsync(ExportFormat format, LogExportCriteria criteria, CancellationToken cancellationToken = default)
        {
            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            var logs = await SearchAsync(criteria.SearchCriteria, cancellationToken);

            switch (format)
            {
                case ExportFormat.Json:
                    return await ExportAsJsonAsync(logs, criteria, cancellationToken);
                case ExportFormat.Csv:
                    return await ExportAsCsvAsync(logs, criteria, cancellationToken);
                case ExportFormat.Xml:
                    return await ExportAsXmlAsync(logs, criteria, cancellationToken);
                default:
                    throw new NotSupportedException($"Export format {format} is not supported");
            }
        }

        /// <inheritdoc/>
        public async Task CompressLogsAsync(CancellationToken cancellationToken = default)
        {
            await _storageLock.WaitAsync(cancellationToken);

            try
            {
                // Check if compression is needed;
                using var countCommand = _dbConnection.CreateCommand();
                countCommand.CommandText = $"SELECT COUNT(*) FROM {ArchiveTableName} WHERE CompressionLevel IS NULL OR CompressionLevel = 0";

                var uncompressedCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

                if (uncompressedCount < CompressionThreshold)
                {
                    _logger.LogDebug("Compression not needed: only {Count} uncompressed entries", uncompressedCount);
                    return;
                }

                using var transaction = _dbConnection.BeginTransaction();

                try
                {
                    // Batch compress logs;
                    for (int i = 0; i < uncompressedCount; i += MaxBatchSize)
                    {
                        using var command = _dbConnection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = $@"
                            UPDATE {ArchiveTableName} 
                            SET Content = @CompressedContent, 
                                CompressionLevel = 1,
                                ArchivedAt = ArchivedAt;
                            WHERE (CompressionLevel IS NULL OR CompressionLevel = 0)
                            LIMIT {MaxBatchSize}";

                        // Note: In a real implementation, you would compress the content here;
                        // For demonstration, we're just marking them as compressed;
                        command.Parameters.Add(CreateParameter("@CompressedContent", "[COMPRESSED]"));

                        await command.ExecuteNonQueryAsync(cancellationToken);

                        if (cancellationToken.IsCancellationRequested)
                            break;
                    }

                    transaction.Commit();
                    _logger.LogInformation("Compressed log entries completed");
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            finally
            {
                _storageLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<LogStoreHealth> ValidateHealthAsync(CancellationToken cancellationToken = default)
        {
            var health = new LogStoreHealth;
            {
                IsHealthy = true,
                Status = "Healthy",
                Warnings = new List<string>(),
                Errors = new List<string>(),
                Metrics = new Dictionary<string, object>()
            };

            try
            {
                // Check database connection;
                if (_dbConnection.State != ConnectionState.Open)
                {
                    health.IsHealthy = false;
                    health.Errors.Add("Database connection is not open");
                }

                // Check table existence;
                using var command = _dbConnection.CreateCommand();
                command.CommandText = @"
                    SELECT; 
                        (SELECT COUNT(*) FROM ConversationLogs) as ActiveCount,
                        (SELECT COUNT(*) FROM ArchivedConversationLogs) as ArchivedCount,
                        (SELECT MAX(CreatedAt) FROM ConversationLogs) as LastEntry";

                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    health.TotalEntries = reader.GetInt64(0) + reader.GetInt64(1);
                    health.Metrics["ActiveEntries"] = reader.GetInt64(0);
                    health.Metrics["ArchivedEntries"] = reader.GetInt64(1);

                    var lastEntry = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
                    if (lastEntry.HasValue && (DateTime.UtcNow - lastEntry.Value).TotalDays > 7)
                    {
                        health.Warnings.Add($"No new entries for {(DateTime.UtcNow - lastEntry.Value).TotalDays:F1} days");
                    }
                }

                // Check storage size (simplified)
                command.CommandText = "PRAGMA page_count; PRAGMA page_size;";
                health.StorageSizeBytes = await EstimateStorageSizeAsync(command, cancellationToken);

                // Validate encryption/decryption;
                var testContent = "Health check test";
                var encrypted = await _cryptoEngine.EncryptAsync(testContent, cancellationToken);
                var decrypted = await _cryptoEngine.DecryptAsync(encrypted, cancellationToken);

                if (decrypted != testContent)
                {
                    health.IsHealthy = false;
                    health.Errors.Add("Encryption/decryption validation failed");
                }

                health.Status = health.IsHealthy ? "Healthy" : "Unhealthy";
                return health;
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.Status = "Error";
                health.Errors.Add($"Health check failed: {ex.Message}");
                return health;
            }
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _storageLock?.Dispose();
                _dbConnection?.Dispose();
                _disposed = true;
            }
        }

        #region Private Helper Methods;

        private void AddParameters(DbCommand command, ConversationLogEntry entry, string encryptedContent)
        {
            command.Parameters.Add(CreateParameter("@Id", entry.Id.ToString()));
            command.Parameters.Add(CreateParameter("@SessionId", entry.SessionId));
            command.Parameters.Add(CreateParameter("@UserId", entry.UserId));
            command.Parameters.Add(CreateParameter("@Content", encryptedContent));
            command.Parameters.Add(CreateParameter("@MessageType", (int)entry.MessageType));
            command.Parameters.Add(CreateParameter("@Context", entry.Context));
            command.Parameters.Add(CreateParameter("@SentimentScore", entry.SentimentScore));
            command.Parameters.Add(CreateParameter("@Confidence", entry.Confidence));
            command.Parameters.Add(CreateParameter("@ProcessingTimeMs", entry.ProcessingTimeMs));
            command.Parameters.Add(CreateParameter("@Source", entry.Source));
            command.Parameters.Add(CreateParameter("@Metadata", entry.Metadata));
            command.Parameters.Add(CreateParameter("@CreatedAt", entry.CreatedAt));
            command.Parameters.Add(CreateParameter("@SourceAddress", entry.SourceAddress));
            command.Parameters.Add(CreateParameter("@Application", entry.Application));
            command.Parameters.Add(CreateParameter("@Version", entry.Version));
            command.Parameters.Add(CreateParameter("@Language", entry.Language));
            command.Parameters.Add(CreateParameter("@IsArchived", entry.IsArchived));
            command.Parameters.Add(CreateParameter("@Tags", entry.Tags != null ? string.Join(",", entry.Tags) : null));
        }

        private ConversationLogEntry MapToEntry(DbDataReader reader)
        {
            return new ConversationLogEntry
            {
                Id = Guid.Parse(reader["Id"].ToString()),
                SessionId = reader["SessionId"].ToString(),
                UserId = reader["UserId"].ToString(),
                Content = reader["Content"].ToString(),
                MessageType = (MessageType)Convert.ToInt32(reader["MessageType"]),
                Context = reader["Context"] as string,
                SentimentScore = reader["SentimentScore"] as double? ?? 0,
                Confidence = reader["Confidence"] as double? ?? 0,
                ProcessingTimeMs = Convert.ToInt64(reader["ProcessingTimeMs"]),
                Source = reader["Source"] as string,
                Metadata = reader["Metadata"] as string,
                CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
                SourceAddress = reader["SourceAddress"] as string,
                Application = reader["Application"] as string,
                Version = reader["Version"] as string,
                Language = reader["Language"] as string,
                IsArchived = Convert.ToBoolean(reader["IsArchived"]),
                Tags = (reader["Tags"] as string)?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string>()
            };
        }

        private DbParameter CreateParameter(string name, object value)
        {
            var param = _dbConnection.CreateCommand().CreateParameter();
            param.ParameterName = name;
            param.Value = value ?? DBNull.Value;
            return param;
        }

        private string BuildSearchQuery(LogSearchCriteria criteria, out List<DbParameter> parameters)
        {
            parameters = new List<DbParameter>();
            var conditions = new List<string>();
            var orderBy = criteria.OrderBy ?? "CreatedAt";
            var orderDirection = criteria.Descending ? "DESC" : "ASC";

            if (!string.IsNullOrEmpty(criteria.SessionId))
            {
                conditions.Add("SessionId = @SessionId");
                parameters.Add(CreateParameter("@SessionId", criteria.SessionId));
            }

            if (!string.IsNullOrEmpty(criteria.UserId))
            {
                conditions.Add("UserId = @UserId");
                parameters.Add(CreateParameter("@UserId", criteria.UserId));
            }

            if (criteria.MessageType.HasValue)
            {
                conditions.Add("MessageType = @MessageType");
                parameters.Add(CreateParameter("@MessageType", (int)criteria.MessageType.Value));
            }

            if (!string.IsNullOrEmpty(criteria.Context))
            {
                conditions.Add("Context LIKE @Context");
                parameters.Add(CreateParameter("@Context", $"%{criteria.Context}%"));
            }

            if (criteria.StartDate.HasValue)
            {
                conditions.Add("CreatedAt >= @StartDate");
                parameters.Add(CreateParameter("@StartDate", criteria.StartDate.Value));
            }

            if (criteria.EndDate.HasValue)
            {
                conditions.Add("CreatedAt <= @EndDate");
                parameters.Add(CreateParameter("@EndDate", criteria.EndDate.Value));
            }

            if (!string.IsNullOrEmpty(criteria.SearchText))
            {
                conditions.Add("(Content LIKE @SearchText OR Context LIKE @SearchText OR Metadata LIKE @SearchText)");
                parameters.Add(CreateParameter("@SearchText", $"%{criteria.SearchText}%"));
            }

            if (criteria.MinConfidence.HasValue)
            {
                conditions.Add("Confidence >= @MinConfidence");
                parameters.Add(CreateParameter("@MinConfidence", criteria.MinConfidence.Value));
            }

            if (criteria.MaxConfidence.HasValue)
            {
                conditions.Add("Confidence <= @MaxConfidence");
                parameters.Add(CreateParameter("@MaxConfidence", criteria.MaxConfidence.Value));
            }

            if (!string.IsNullOrEmpty(criteria.Source))
            {
                conditions.Add("Source = @Source");
                parameters.Add(CreateParameter("@Source", criteria.Source));
            }

            if (criteria.IsArchived.HasValue)
            {
                conditions.Add("IsArchived = @IsArchived");
                parameters.Add(CreateParameter("@IsArchived", criteria.IsArchived.Value ? 1 : 0));
            }

            var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
            var query = $@"
                SELECT * FROM {LogTableName} 
                {whereClause}
                ORDER BY {orderBy} {orderDirection}
                LIMIT {criteria.Take} OFFSET {criteria.Skip}";

            return query;
        }

        private string BuildPurgeQuery(LogPurgeCriteria criteria, out List<DbParameter> parameters)
        {
            parameters = new List<DbParameter>();
            var conditions = new List<string>();

            if (criteria.OlderThan.HasValue)
            {
                conditions.Add("CreatedAt < @OlderThan");
                parameters.Add(CreateParameter("@OlderThan", criteria.OlderThan.Value));
            }

            if (criteria.MessageType.HasValue)
            {
                conditions.Add("MessageType = @MessageType");
                parameters.Add(CreateParameter("@MessageType", (int)criteria.MessageType.Value));
            }

            if (!string.IsNullOrEmpty(criteria.SessionId))
            {
                conditions.Add("SessionId = @SessionId");
                parameters.Add(CreateParameter("@SessionId", criteria.SessionId));
            }

            if (!criteria.IncludeArchived)
            {
                conditions.Add("IsArchived = 0");
            }

            var whereClause = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
            return $"DELETE FROM {LogTableName} {whereClause}";
        }

        private async Task PopulateDistributionStats(ConversationStatistics stats, DateTime? startDate, DateTime? endDate, CancellationToken cancellationToken)
        {
            using var command = _dbConnection.CreateCommand();
            command.CommandText = @"
                SELECT MessageType, COUNT(*) 
                FROM ConversationLogs;
                WHERE (@StartDate IS NULL OR CreatedAt >= @StartDate)
                  AND (@EndDate IS NULL OR CreatedAt <= @EndDate)
                GROUP BY MessageType";

            command.Parameters.Add(CreateParameter("@StartDate", startDate));
            command.Parameters.Add(CreateParameter("@EndDate", endDate));

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = (MessageType)reader.GetInt32(0);
                stats.MessageTypeCounts[type] = reader.GetInt32(1);
            }

            // Similar queries for Context, Language, Source distributions...
            // Implementation simplified for brevity;
        }

        private async Task<byte[]> ExportAsJsonAsync(IEnumerable<ConversationLogEntry> logs, LogExportCriteria criteria, CancellationToken cancellationToken)
        {
            var exportData = logs.Select(log => new;
            {
                log.Id,
                log.SessionId,
                log.UserId,
                Content = log.Content, // Already decrypted;
                MessageType = log.MessageType.ToString(),
                log.Context,
                log.SentimentScore,
                log.Confidence,
                log.ProcessingTimeMs,
                log.Source,
                Metadata = criteria.IncludeMetadata ? log.Metadata : null,
                CreatedAt = TimeZoneInfo.ConvertTimeFromUtc(log.CreatedAt,
                    TimeZoneInfo.FindSystemTimeZoneById(criteria.Timezone)),
                log.SourceAddress,
                log.Application,
                log.Version,
                log.Language,
                log.Tags;
            });

            var json = System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions;
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });

            var bytes = System.Text.Encoding.UTF8.GetBytes(json);

            if (criteria.CompressOutput)
            {
                // In real implementation, compress the bytes;
                // For now, return as-is;
            }

            return bytes;
        }

        private async Task<byte[]> ExportAsCsvAsync(IEnumerable<ConversationLogEntry> logs, LogExportCriteria criteria, CancellationToken cancellationToken)
        {
            using var memoryStream = new System.IO.MemoryStream();
            using var writer = new System.IO.StreamWriter(memoryStream);

            // Write header;
            writer.WriteLine("Id,SessionId,UserId,Content,MessageType,Context,SentimentScore,Confidence,ProcessingTimeMs,Source,Metadata,CreatedAt,SourceAddress,Application,Version,Language,Tags");

            foreach (var log in logs)
            {
                var createdAt = TimeZoneInfo.ConvertTimeFromUtc(log.CreatedAt,
                    TimeZoneInfo.FindSystemTimeZoneById(criteria.Timezone));

                // Escape CSV special characters;
                var escapedContent = $"\"{log.Content.Replace("\"", "\"\"")}\"";
                var escapedMetadata = criteria.IncludeMetadata ? $"\"{log.Metadata?.Replace("\"", "\"\"")}\"" : "";
                var tags = log.Tags != null ? string.Join(";", log.Tags) : "";

                writer.WriteLine($"{log.Id},{log.SessionId},{log.UserId},{escapedContent},{log.MessageType},{log.Context},{log.SentimentScore},{log.Confidence},{log.ProcessingTimeMs},{log.Source},{escapedMetadata},{createdAt:yyyy-MM-dd HH:mm:ss},{log.SourceAddress},{log.Application},{log.Version},{log.Language},{tags}");
            }

            writer.Flush();
            return memoryStream.ToArray();
        }

        private async Task<byte[]> ExportAsXmlAsync(IEnumerable<ConversationLogEntry> logs, LogExportCriteria criteria, CancellationToken cancellationToken)
        {
            using var memoryStream = new System.IO.MemoryStream();
            using var writer = System.Xml.XmlWriter.Create(memoryStream, new System.Xml.XmlWriterSettings;
            {
                Indent = true,
                Encoding = System.Text.Encoding.UTF8;
            });

            writer.WriteStartDocument();
            writer.WriteStartElement("ConversationLogs");
            writer.WriteAttributeString("ExportDate", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            writer.WriteAttributeString("Timezone", criteria.Timezone);
            writer.WriteAttributeString("Count", logs.Count().ToString());

            foreach (var log in logs)
            {
                writer.WriteStartElement("LogEntry");
                writer.WriteAttributeString("Id", log.Id.ToString());
                writer.WriteAttributeString("SessionId", log.SessionId);
                writer.WriteAttributeString("UserId", log.UserId);
                writer.WriteAttributeString("MessageType", log.MessageType.ToString());
                writer.WriteAttributeString("CreatedAt",
                    TimeZoneInfo.ConvertTimeFromUtc(log.CreatedAt,
                        TimeZoneInfo.FindSystemTimeZoneById(criteria.Timezone)).ToString("yyyy-MM-ddTHH:mm:ss"));

                writer.WriteElementString("Content", log.Content);
                writer.WriteElementString("Context", log.Context);
                writer.WriteElementString("SentimentScore", log.SentimentScore.ToString("F2"));
                writer.WriteElementString("Confidence", log.Confidence.ToString("F2"));
                writer.WriteElementString("ProcessingTimeMs", log.ProcessingTimeMs.ToString());
                writer.WriteElementString("Source", log.Source);

                if (criteria.IncludeMetadata && !string.IsNullOrEmpty(log.Metadata))
                {
                    writer.WriteElementString("Metadata", log.Metadata);
                }

                writer.WriteElementString("SourceAddress", log.SourceAddress);
                writer.WriteElementString("Application", log.Application);
                writer.WriteElementString("Version", log.Version);
                writer.WriteElementString("Language", log.Language);

                if (log.Tags?.Any() == true)
                {
                    writer.WriteStartElement("Tags");
                    foreach (var tag in log.Tags)
                    {
                        writer.WriteElementString("Tag", tag);
                    }
                    writer.WriteEndElement();
                }

                writer.WriteEndElement(); // LogEntry
            }

            writer.WriteEndElement(); // ConversationLogs;
            writer.WriteEndDocument();
            writer.Flush();

            return memoryStream.ToArray();
        }

        private async Task<long> EstimateStorageSizeAsync(DbCommand command, CancellationToken cancellationToken)
        {
            try
            {
                command.CommandText = "PRAGMA page_count;";
                var pageCount = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));

                command.CommandText = "PRAGMA page_size;";
                var pageSize = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));

                return pageCount * pageSize;
            }
            catch
            {
                // Fallback estimation;
                return 0;
            }
        }

        #endregion;
    }

    /// <summary>
    /// Custom exception for LogStore operations;
    /// </summary>
    public class LogStoreException : Exception
    {
        public LogStoreException() { }
        public LogStoreException(string message) : base(message) { }
        public LogStoreException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Audit entry for tracking log store operations;
    /// </summary>
    internal class AuditEntry
    {
        public string Action { get; set; }
        public string EntityId { get; set; }
        public string EntityType { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
    }
}
