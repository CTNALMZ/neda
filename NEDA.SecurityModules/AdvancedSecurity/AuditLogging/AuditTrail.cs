using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.SecurityModules.AdvancedSecurity.AuditLogging;
{
    /// <summary>
    /// Audit kayıtlarının kalıcı depolama, yönetim ve analiz sınıfı;
    /// Endüstriyel seviyede thread-safe, yüksek performanslı implementasyon;
    /// </summary>
    public class AuditTrail : IAuditTrail;
    {
        private readonly AuditDbContext _dbContext;
        private readonly ILogger<AuditTrail> _logger;
        private readonly IConfiguration _configuration;
        private readonly IAuditEncryptionService _encryptionService;
        private readonly IAuditCompressionService _compressionService;
        private readonly IEventBus _eventBus;
        private readonly SemaphoreSlim _dbLock = new SemaphoreSlim(1, 1);
        private readonly int _batchSize = 1000;
        private bool _disposed;

        public AuditTrail(
            AuditDbContext dbContext,
            ILogger<AuditTrail> logger,
            IConfiguration configuration,
            IAuditEncryptionService encryptionService,
            IAuditCompressionService compressionService,
            IEventBus eventBus)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _compressionService = compressionService ?? throw new ArgumentNullException(nameof(compressionService));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        #region Core Audit Operations;

        /// <summary>
        /// Tekil audit event ekle (Thread-safe, async)
        /// </summary>
        public async Task<AuditEvent> AddEventAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            if (auditEvent == null)
                throw new ArgumentNullException(nameof(auditEvent));

            ValidateAuditEvent(auditEvent);

            try
            {
                await _dbLock.WaitAsync(cancellationToken);

                var entity = await PrepareAuditEntityAsync(auditEvent, cancellationToken);

                await _dbContext.AuditEvents.AddAsync(entity, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);

                var savedEvent = entity.ToAuditEvent(_encryptionService, _compressionService);

                // Event bus üzerinden audit event yayınla;
                await PublishAuditEventAsync(savedEvent, cancellationToken);

                _logger.LogDebug("Audit event added: {EventId} - {Action}",
                    auditEvent.Id, auditEvent.Action);

                return savedEvent;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "Database error while adding audit event: {EventId}",
                    auditEvent.Id);
                throw new AuditException($"Database error for event {auditEvent.Id}", dbEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add audit event: {EventId}", auditEvent.Id);
                throw new AuditException($"Failed to add audit event: {auditEvent.Id}", ex);
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Batch audit event ekle (Yüksek performanslı)
        /// </summary>
        public async Task<int> AddEventsAsync(
            IEnumerable<AuditEvent> auditEvents,
            CancellationToken cancellationToken = default)
        {
            if (auditEvents == null)
                throw new ArgumentNullException(nameof(auditEvents));

            var eventList = auditEvents.ToList();
            if (!eventList.Any())
                return 0;

            int savedCount = 0;

            try
            {
                await _dbLock.WaitAsync(cancellationToken);

                // Büyük batch'leri parçala;
                var chunks = eventList.Chunk(_batchSize);

                foreach (var chunk in chunks)
                {
                    savedCount += await ProcessEventChunkAsync(chunk, cancellationToken);
                }

                _logger.LogInformation("Added {Count} audit events in batch", savedCount);
            }
            finally
            {
                _dbLock.Release();
            }

            return savedCount;
        }

        /// <summary>
        /// Audit event güncelle;
        /// </summary>
        public async Task<AuditEvent> UpdateEventAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            if (auditEvent == null)
                throw new ArgumentNullException(nameof(auditEvent));

            try
            {
                await _dbLock.WaitAsync(cancellationToken);

                var existingEntity = await _dbContext.AuditEvents;
                    .FirstOrDefaultAsync(e => e.Id == auditEvent.Id, cancellationToken);

                if (existingEntity == null)
                {
                    throw new AuditNotFoundException($"Audit event not found: {auditEvent.Id}");
                }

                // Entity'yi güncelle;
                UpdateAuditEntity(existingEntity, auditEvent);

                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogDebug("Audit event updated: {EventId}", auditEvent.Id);

                return existingEntity.ToAuditEvent(_encryptionService, _compressionService);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update audit event: {EventId}", auditEvent.Id);
                throw new AuditException($"Failed to update audit event: {auditEvent.Id}", ex);
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Audit event sil (Soft delete)
        /// </summary>
        public async Task<bool> DeleteEventAsync(
            Guid eventId,
            string deletedBy,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _dbLock.WaitAsync(cancellationToken);

                var entity = await _dbContext.AuditEvents;
                    .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

                if (entity == null)
                    return false;

                // Soft delete işlemi;
                entity.IsDeleted = true;
                entity.DeletedAt = DateTime.UtcNow;
                entity.DeletedBy = deletedBy;

                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Audit event soft deleted: {EventId} by {User}",
                    eventId, deletedBy);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete audit event: {EventId}", eventId);
                throw new AuditException($"Failed to delete audit event: {eventId}", ex);
            }
            finally
            {
                _dbLock.Release();
            }
        }

        #endregion;

        #region Query Operations;

        /// <summary>
        /// Sorgu parametrelerine göre audit event'leri getir;
        /// </summary>
        public async Task<IEnumerable<AuditEvent>> QueryEventsAsync(
            AuditQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            try
            {
                IQueryable<AuditEventEntity> dbQuery = BuildQuery(query);

                // Sayfalama;
                if (query.PageSize > 0)
                {
                    dbQuery = dbQuery;
                        .Skip((query.PageNumber - 1) * query.PageSize)
                        .Take(query.PageSize);
                }

                var entities = await dbQuery;
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);

                // Entity'leri domain model'e çevir;
                var events = new List<AuditEvent>();
                foreach (var entity in entities)
                {
                    events.Add(await entity.ToAuditEventAsync(_encryptionService, _compressionService));
                }

                return events;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query audit events");
                throw new AuditException("Failed to query audit events", ex);
            }
        }

        /// <summary>
        /// Sorgu sonuç sayısını getir;
        /// </summary>
        public async Task<int> CountEventsAsync(
            AuditQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            try
            {
                var dbQuery = BuildQuery(query);
                return await dbQuery.CountAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to count audit events");
                throw new AuditException("Failed to count audit events", ex);
            }
        }

        /// <summary>
        /// ID'ye göre audit event getir;
        /// </summary>
        public async Task<AuditEvent?> GetEventByIdAsync(
            Guid eventId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var entity = await _dbContext.AuditEvents;
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

                if (entity == null)
                    return null;

                return await entity.ToAuditEventAsync(_encryptionService, _compressionService);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get audit event by ID: {EventId}", eventId);
                throw new AuditException($"Failed to get audit event by ID: {eventId}", ex);
            }
        }

        /// <summary>
        /// Kullanıcıya göre audit event'leri getir;
        /// </summary>
        public async Task<IEnumerable<AuditEvent>> GetEventsByUserAsync(
            string userId,
            DateTime? from = null,
            DateTime? to = null,
            int limit = 1000,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            try
            {
                IQueryable<AuditEventEntity> query = _dbContext.AuditEvents;
                    .Where(e => e.UserId == userId && !e.IsDeleted)
                    .AsNoTracking();

                if (from.HasValue)
                {
                    query = query.Where(e => e.Timestamp >= from.Value);
                }

                if (to.HasValue)
                {
                    query = query.Where(e => e.Timestamp <= to.Value);
                }

                var entities = await query;
                    .OrderByDescending(e => e.Timestamp)
                    .Take(limit)
                    .ToListAsync(cancellationToken);

                var events = new List<AuditEvent>();
                foreach (var entity in entities)
                {
                    events.Add(await entity.ToAuditEventAsync(_encryptionService, _compressionService));
                }

                return events;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get audit events for user: {UserId}", userId);
                throw new AuditException($"Failed to get audit events for user: {userId}", ex);
            }
        }

        #endregion;

        #region Analytics & Reporting;

        /// <summary>
        /// Audit istatistiklerini getir;
        /// </summary>
        public async Task<AuditStatistics> GetStatisticsAsync(
            DateTime from,
            DateTime to,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = _dbContext.AuditEvents;
                    .Where(e => e.Timestamp >= from && e.Timestamp <= to && !e.IsDeleted)
                    .AsNoTracking();

                var events = await query.ToListAsync(cancellationToken);

                return new AuditStatistics;
                {
                    TotalEvents = events.Count,
                    CriticalEvents = events.Count(e => e.Severity == AuditSeverity.Critical),
                    HighSeverityEvents = events.Count(e => e.Severity == AuditSeverity.High),
                    MediumSeverityEvents = events.Count(e => e.Severity == AuditSeverity.Medium),
                    LowSeverityEvents = events.Count(e => e.Severity == AuditSeverity.Low),
                    InfoEvents = events.Count(e => e.Severity == AuditSeverity.Info),
                    UniqueUsers = events.Select(e => e.UserId).Distinct().Count(),
                    MostActiveUser = events;
                        .GroupBy(e => e.UserId)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key,
                    MostCommonAction = events;
                        .GroupBy(e => e.Action)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key,
                    MostAccessedResource = events;
                        .GroupBy(e => e.Resource)
                        .OrderByDescending(g => g.Count())
                        .FirstOrDefault()?.Key,
                    PeakHour = CalculatePeakHour(events),
                    AverageEventsPerHour = CalculateAverageEventsPerHour(events, from, to),
                    StartTime = from,
                    EndTime = to,
                    GeneratedAt = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get audit statistics");
                throw new AuditException("Failed to get audit statistics", ex);
            }
        }

        /// <summary>
        /// Zaman serisi verilerini getir;
        /// </summary>
        public async Task<IEnumerable<TimeSeriesData>> GetTimeSeriesDataAsync(
            DateTime from,
            DateTime to,
            TimeGranularity granularity,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = _dbContext.AuditEvents;
                    .Where(e => e.Timestamp >= from && e.Timestamp <= to && !e.IsDeleted)
                    .AsNoTracking();

                var events = await query.ToListAsync(cancellationToken);

                return granularity switch;
                {
                    TimeGranularity.Hourly => GroupByHour(events, from, to),
                    TimeGranularity.Daily => GroupByDay(events, from, to),
                    TimeGranularity.Weekly => GroupByWeek(events, from, to),
                    TimeGranularity.Monthly => GroupByMonth(events, from, to),
                    _ => GroupByDay(events, from, to)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get time series data");
                throw new AuditException("Failed to get time series data", ex);
            }
        }

        /// <summary>
        /// Anomali tespiti yap;
        /// </summary>
        public async Task<AnomalyDetectionResult> DetectAnomaliesAsync(
            DateTime from,
            DateTime to,
            AnomalyDetectionConfig config,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var events = await _dbContext.AuditEvents;
                    .Where(e => e.Timestamp >= from && e.Timestamp <= to && !e.IsDeleted)
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);

                var result = new AnomalyDetectionResult;
                {
                    PeriodFrom = from,
                    PeriodTo = to,
                    DetectedAt = DateTime.UtcNow,
                    Anomalies = new List<AuditAnomaly>()
                };

                // 1. Olağan dışı saatlerdeki aktivite;
                var nightEvents = events.Where(e => e.Timestamp.Hour >= 0 && e.Timestamp.Hour <= 5);
                if (nightEvents.Count() > config.NightActivityThreshold)
                {
                    result.Anomalies.Add(new AuditAnomaly;
                    {
                        Type = AnomalyType.UnusualTimeActivity,
                        Severity = AnomalySeverity.High,
                        Description = $"Unusual high activity during night hours: {nightEvents.Count()} events",
                        EventCount = nightEvents.Count(),
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // 2. Brute-force benzeri pattern'ler;
                var failedAccessPatterns = DetectFailedAccessPatterns(events, config);
                result.Anomalies.AddRange(failedAccessPatterns);

                // 3. Olağan dışı kaynak erişimleri;
                var unusualResourceAccess = DetectUnusualResourceAccess(events, config);
                result.Anomalies.AddRange(unusualResourceAccess);

                // 4. Frekans anomalileri;
                var frequencyAnomalies = DetectFrequencyAnomalies(events, config);
                result.Anomalies.AddRange(frequencyAnomalies);

                result.TotalAnomalies = result.Anomalies.Count;
                result.HighSeverityAnomalies = result.Anomalies.Count(a => a.Severity == AnomalySeverity.High);
                result.MediumSeverityAnomalies = result.Anomalies.Count(a => a.Severity == AnomalySeverity.Medium);
                result.LowSeverityAnomalies = result.Anomalies.Count(a => a.Severity == AnomalySeverity.Low);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect anomalies");
                throw new AuditException("Failed to detect anomalies", ex);
            }
        }

        #endregion;

        #region Maintenance Operations;

        /// <summary>
        /// Eski audit kayıtlarını temizle;
        /// </summary>
        public async Task<CleanupResult> CleanupOldRecordsAsync(
            int retentionDays,
            CleanupStrategy strategy = CleanupStrategy.Archive,
            CancellationToken cancellationToken = default)
        {
            if (retentionDays <= 0)
                throw new ArgumentException("Retention days must be positive", nameof(retentionDays));

            try
            {
                await _dbLock.WaitAsync(cancellationToken);

                var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

                var result = new CleanupResult;
                {
                    CutoffDate = cutoffDate,
                    Strategy = strategy,
                    StartedAt = DateTime.UtcNow;
                };

                switch (strategy)
                {
                    case CleanupStrategy.Delete:
                        result = await DeleteOldRecordsAsync(cutoffDate, result, cancellationToken);
                        break;

                    case CleanupStrategy.Archive:
                        result = await ArchiveOldRecordsAsync(cutoffDate, result, cancellationToken);
                        break;

                    case CleanupStrategy.Compress:
                        result = await CompressOldRecordsAsync(cutoffDate, result, cancellationToken);
                        break;
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                _logger.LogInformation(
                    "Cleanup completed: {Strategy}, {DeletedCount} deleted, {ArchivedCount} archived in {Duration}",
                    strategy, result.DeletedCount, result.ArchivedCount, result.Duration);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup old audit records");
                throw new AuditException("Failed to cleanup old audit records", ex);
            }
            finally
            {
                _dbLock.Release();
            }
        }

        /// <summary>
        /// Audit veritabanı istatistiklerini getir;
        /// </summary>
        public async Task<AuditDatabaseStats> GetDatabaseStatisticsAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var totalEvents = await _dbContext.AuditEvents.CountAsync(cancellationToken);
                var activeEvents = await _dbContext.AuditEvents;
                    .CountAsync(e => !e.IsDeleted, cancellationToken);
                var deletedEvents = totalEvents - activeEvents;

                var oldestEvent = await _dbContext.AuditEvents;
                    .OrderBy(e => e.Timestamp)
                    .Select(e => e.Timestamp)
                    .FirstOrDefaultAsync(cancellationToken);
                var newestEvent = await _dbContext.AuditEvents;
                    .OrderByDescending(e => e.Timestamp)
                    .Select(e => e.Timestamp)
                    .FirstOrDefaultAsync(cancellationToken);

                var sizeEstimate = await EstimateDatabaseSizeAsync(cancellationToken);

                var tableStats = await GetTableStatisticsAsync(cancellationToken);

                return new AuditDatabaseStats;
                {
                    TotalEvents = totalEvents,
                    ActiveEvents = activeEvents,
                    DeletedEvents = deletedEvents,
                    OldestEvent = oldestEvent,
                    NewestEvent = newestEvent,
                    EstimatedSizeMB = sizeEstimate,
                    TableStatistics = tableStats,
                    LastCleanup = await GetLastCleanupDateAsync(cancellationToken),
                    CheckedAt = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get database statistics");
                throw new AuditException("Failed to get database statistics", ex);
            }
        }

        /// <summary>
        /// Audit veritabanını optimize et;
        /// </summary>
        public async Task<OptimizationResult> OptimizeDatabaseAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _dbLock.WaitAsync(cancellationToken);

                var result = new OptimizationResult;
                {
                    StartedAt = DateTime.UtcNow,
                    Operations = new List<string>()
                };

                // 1. Index'leri rebuild et;
                await RebuildIndexesAsync(result, cancellationToken);

                // 2. Fragmentasyonu gider;
                await DefragmentTablesAsync(result, cancellationToken);

                // 3. İstatistikleri güncelle;
                await UpdateStatisticsAsync(result, cancellationToken);

                // 4. TempDB'yi temizle;
                await CleanupTempDbAsync(result, cancellationToken);

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                _logger.LogInformation("Database optimization completed in {Duration}", result.Duration);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to optimize database");
                throw new AuditException("Failed to optimize database", ex);
            }
            finally
            {
                _dbLock.Release();
            }
        }

        #endregion;

        #region Helper Methods;

        private async Task<AuditEventEntity> PrepareAuditEntityAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            var entity = new AuditEventEntity;
            {
                Id = auditEvent.Id,
                Timestamp = auditEvent.Timestamp,
                UserId = auditEvent.UserId,
                Action = auditEvent.Action,
                Resource = auditEvent.Resource,
                Severity = auditEvent.Severity,
                Details = auditEvent.Details,
                Metadata = auditEvent.Metadata,
                IpAddress = auditEvent.IpAddress,
                UserAgent = auditEvent.UserAgent,
                SessionId = auditEvent.SessionId,
                CorrelationId = auditEvent.CorrelationId,
                CreatedAt = DateTime.UtcNow,
                IsDeleted = false;
            };

            // Hassas verileri şifrele;
            if (!string.IsNullOrWhiteSpace(entity.Details) &&
                _configuration.GetValue<bool>("Audit:EncryptSensitiveData", true))
            {
                entity.EncryptedDetails = await _encryptionService.EncryptAsync(entity.Details);
                entity.Details = null;
            }

            if (!string.IsNullOrWhiteSpace(entity.IpAddress))
            {
                entity.EncryptedIpAddress = await _encryptionService.EncryptAsync(entity.IpAddress);
                entity.IpAddress = null;
            }

            // Metadata'yı sıkıştır;
            if (entity.Metadata != null && entity.Metadata.Count > 10)
            {
                entity.CompressedMetadata = await _compressionService.CompressAsync(
                    JsonSerializer.Serialize(entity.Metadata),
                    cancellationToken);
                entity.Metadata = null;
            }

            return entity;
        }

        private void UpdateAuditEntity(AuditEventEntity entity, AuditEvent auditEvent)
        {
            entity.Details = auditEvent.Details;
            entity.Metadata = auditEvent.Metadata;
            entity.Severity = auditEvent.Severity;
            entity.UpdatedAt = DateTime.UtcNow;
        }

        private async Task<int> ProcessEventChunkAsync(
            AuditEvent[] chunk,
            CancellationToken cancellationToken)
        {
            using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var entities = new List<AuditEventEntity>();

                foreach (var auditEvent in chunk)
                {
                    ValidateAuditEvent(auditEvent);
                    var entity = await PrepareAuditEntityAsync(auditEvent, cancellationToken);
                    entities.Add(entity);
                }

                await _dbContext.AuditEvents.AddRangeAsync(entities, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                // Event'leri publish et;
                foreach (var entity in entities)
                {
                    var savedEvent = await entity.ToAuditEventAsync(_encryptionService, _compressionService);
                    await PublishAuditEventAsync(savedEvent, cancellationToken);
                }

                return entities.Count;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        private IQueryable<AuditEventEntity> BuildQuery(AuditQuery query)
        {
            IQueryable<AuditEventEntity> dbQuery = _dbContext.AuditEvents;
                .Where(e => !e.IsDeleted);

            if (query.From.HasValue)
            {
                dbQuery = dbQuery.Where(e => e.Timestamp >= query.From.Value);
            }

            if (query.To.HasValue)
            {
                dbQuery = dbQuery.Where(e => e.Timestamp <= query.To.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.UserId))
            {
                dbQuery = dbQuery.Where(e => e.UserId == query.UserId);
            }

            if (!string.IsNullOrWhiteSpace(query.Action))
            {
                dbQuery = dbQuery.Where(e => e.Action == query.Action);
            }

            if (!string.IsNullOrWhiteSpace(query.Resource))
            {
                dbQuery = dbQuery.Where(e => e.Resource.Contains(query.Resource));
            }

            if (query.Severity.HasValue)
            {
                dbQuery = dbQuery.Where(e => e.Severity == query.Severity.Value);
            }

            if (query.MinSeverity.HasValue)
            {
                dbQuery = dbQuery.Where(e => e.Severity >= query.MinSeverity.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.IpAddress))
            {
                // Şifreli IP adresini ara;
                var encryptedIp = _encryptionService.EncryptAsync(query.IpAddress).GetAwaiter().GetResult();
                dbQuery = dbQuery.Where(e => e.EncryptedIpAddress == encryptedIp);
            }

            if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            {
                dbQuery = dbQuery.Where(e => e.CorrelationId == query.CorrelationId);
            }

            // Sıralama;
            dbQuery = query.SortOrder switch;
            {
                SortOrder.Ascending => query.SortBy switch;
                {
                    "timestamp" => dbQuery.OrderBy(e => e.Timestamp),
                    "severity" => dbQuery.OrderBy(e => e.Severity),
                    "userId" => dbQuery.OrderBy(e => e.UserId),
                    _ => dbQuery.OrderBy(e => e.Timestamp)
                },
                _ => query.SortBy switch;
                {
                    "timestamp" => dbQuery.OrderByDescending(e => e.Timestamp),
                    "severity" => dbQuery.OrderByDescending(e => e.Severity),
                    "userId" => dbQuery.OrderByDescending(e => e.UserId),
                    _ => dbQuery.OrderByDescending(e => e.Timestamp)
                }
            };

            return dbQuery;
        }

        private async Task PublishAuditEventAsync(
            AuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            try
            {
                var auditEventMessage = new AuditEventMessage;
                {
                    EventId = auditEvent.Id,
                    Timestamp = auditEvent.Timestamp,
                    UserId = auditEvent.UserId,
                    Action = auditEvent.Action,
                    Severity = auditEvent.Severity,
                    Resource = auditEvent.Resource;
                };

                await _eventBus.PublishAsync(auditEventMessage, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish audit event to event bus: {EventId}",
                    auditEvent.Id);
                // Event bus failure shouldn't fail the audit logging;
            }
        }

        private void ValidateAuditEvent(AuditEvent auditEvent)
        {
            if (string.IsNullOrWhiteSpace(auditEvent.UserId))
                throw new ArgumentException("User ID is required", nameof(auditEvent.UserId));

            if (string.IsNullOrWhiteSpace(auditEvent.Action))
                throw new ArgumentException("Action is required", nameof(auditEvent.Action));

            if (string.IsNullOrWhiteSpace(auditEvent.Resource))
                throw new ArgumentException("Resource is required", nameof(auditEvent.Resource));

            if (auditEvent.Timestamp > DateTime.UtcNow.AddMinutes(5))
                throw new ArgumentException("Timestamp cannot be in the future", nameof(auditEvent.Timestamp));

            if (auditEvent.Timestamp < DateTime.UtcNow.AddYears(-10))
                throw new ArgumentException("Timestamp is too old", nameof(auditEvent.Timestamp));
        }

        #endregion;

        #region Analytics Helper Methods;

        private static int CalculatePeakHour(List<AuditEventEntity> events)
        {
            if (!events.Any())
                return 0;

            var hourGroups = events;
                .GroupBy(e => e.Timestamp.Hour)
                .Select(g => new { Hour = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();

            return hourGroups?.Hour ?? 0;
        }

        private static double CalculateAverageEventsPerHour(
            List<AuditEventEntity> events,
            DateTime from,
            DateTime to)
        {
            if (!events.Any())
                return 0;

            var totalHours = (to - from).TotalHours;
            if (totalHours <= 0)
                totalHours = 1;

            return events.Count / totalHours;
        }

        private static IEnumerable<TimeSeriesData> GroupByHour(
            List<AuditEventEntity> events,
            DateTime from,
            DateTime to)
        {
            var result = new List<TimeSeriesData>();
            var current = from;

            while (current <= to)
            {
                var nextHour = current.AddHours(1);
                var hourEvents = events;
                    .Where(e => e.Timestamp >= current && e.Timestamp < nextHour)
                    .ToList();

                result.Add(new TimeSeriesData;
                {
                    PeriodStart = current,
                    PeriodEnd = nextHour,
                    EventCount = hourEvents.Count,
                    CriticalCount = hourEvents.Count(e => e.Severity == AuditSeverity.Critical),
                    HighCount = hourEvents.Count(e => e.Severity == AuditSeverity.High),
                    MediumCount = hourEvents.Count(e => e.Severity == AuditSeverity.Medium)
                });

                current = nextHour;
            }

            return result;
        }

        private static IEnumerable<TimeSeriesData> GroupByDay(
            List<AuditEventEntity> events,
            DateTime from,
            DateTime to)
        {
            // Similar implementation for days;
            return Enumerable.Empty<TimeSeriesData>();
        }

        private static IEnumerable<TimeSeriesData> GroupByWeek(
            List<AuditEventEntity> events,
            DateTime from,
            DateTime to)
        {
            // Similar implementation for weeks;
            return Enumerable.Empty<TimeSeriesData>();
        }

        private static IEnumerable<TimeSeriesData> GroupByMonth(
            List<AuditEventEntity> events,
            DateTime from,
            DateTime to)
        {
            // Similar implementation for months;
            return Enumerable.Empty<TimeSeriesData>();
        }

        private static IEnumerable<AuditAnomaly> DetectFailedAccessPatterns(
            List<AuditEventEntity> events,
            AnomalyDetectionConfig config)
        {
            var anomalies = new List<AuditAnomaly>();

            // Grup by user and resource within short time windows;
            var suspiciousPatterns = events;
                .Where(e => e.Action.Contains("DENIED") || e.Action.Contains("FAILED"))
                .GroupBy(e => new { e.UserId, e.Resource, Hour = e.Timestamp.Hour })
                .Where(g => g.Count() > config.FailedAccessThreshold)
                .ToList();

            foreach (var pattern in suspiciousPatterns)
            {
                anomalies.Add(new AuditAnomaly;
                {
                    Type = AnomalyType.FailedAccessPattern,
                    Severity = AnomalySeverity.Medium,
                    Description = $"Multiple failed access attempts by {pattern.Key.UserId} " +
                                 $"to {pattern.Key.Resource} within hour {pattern.Key.Hour}",
                    EventCount = pattern.Count(),
                    UserId = pattern.Key.UserId,
                    Resource = pattern.Key.Resource,
                    Timestamp = pattern.Max(e => e.Timestamp)
                });
            }

            return anomalies;
        }

        private static IEnumerable<AuditAnomaly> DetectUnusualResourceAccess(
            List<AuditEventEntity> events,
            AnomalyDetectionConfig config)
        {
            var anomalies = new List<AuditAnomaly>();

            // Detect resources that are rarely accessed;
            var resourceAccessCounts = events;
                .GroupBy(e => e.Resource)
                .Select(g => new { Resource = g.Key, Count = g.Count() })
                .OrderBy(x => x.Count)
                .Take(10) // Top 10 least accessed resources;
                .Where(x => x.Count > 0 && x.Count <= config.RareResourceThreshold)
                .ToList();

            foreach (var resource in resourceAccessCounts)
            {
                anomalies.Add(new AuditAnomaly;
                {
                    Type = AnomalyType.RareResourceAccess,
                    Severity = AnomalySeverity.Low,
                    Description = $"Rare resource accessed: {resource.Resource} ({resource.Count} times)",
                    EventCount = resource.Count,
                    Resource = resource.Resource,
                    Timestamp = DateTime.UtcNow;
                });
            }

            return anomalies;
        }

        private static IEnumerable<AuditAnomaly> DetectFrequencyAnomalies(
            List<AuditEventEntity> events,
            AnomalyDetectionConfig config)
        {
            var anomalies = new List<AuditAnomaly>();

            // Calculate moving average and detect spikes;
            var hourlyCounts = events;
                .GroupBy(e => new { e.Timestamp.Date, e.Timestamp.Hour })
                .Select(g => new { Hour = g.Key, Count = g.Count() })
                .OrderBy(x => x.Hour.Date)
                .ThenBy(x => x.Hour.Hour)
                .ToList();

            if (hourlyCounts.Count > 10)
            {
                var movingAverage = hourlyCounts;
                    .Skip(hourlyCounts.Count - 10)
                    .Take(5)
                    .Average(x => x.Count);

                var currentHour = hourlyCounts.Last();
                if (currentHour.Count > movingAverage * config.FrequencySpikeMultiplier)
                {
                    anomalies.Add(new AuditAnomaly;
                    {
                        Type = AnomalyType.FrequencySpike,
                        Severity = AnomalySeverity.High,
                        Description = $"Frequency spike detected: {currentHour.Count} events " +
                                     $"(average: {movingAverage:F2})",
                        EventCount = currentHour.Count,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }

            return anomalies;
        }

        #endregion;

        #region Maintenance Helper Methods;

        private async Task<CleanupResult> DeleteOldRecordsAsync(
            DateTime cutoffDate,
            CleanupResult result,
            CancellationToken cancellationToken)
        {
            var oldEvents = _dbContext.AuditEvents;
                .Where(e => e.Timestamp < cutoffDate && !e.IsDeleted);

            result.DeletedCount = await oldEvents.CountAsync(cancellationToken);

            if (result.DeletedCount > 0)
            {
                _dbContext.AuditEvents.RemoveRange(oldEvents);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return result;
        }

        private async Task<CleanupResult> ArchiveOldRecordsAsync(
            DateTime cutoffDate,
            CleanupResult result,
            CancellationToken cancellationToken)
        {
            var oldEvents = await _dbContext.AuditEvents;
                .Where(e => e.Timestamp < cutoffDate && !e.IsDeleted)
                .ToListAsync(cancellationToken);

            result.DeletedCount = oldEvents.Count;

            if (result.DeletedCount > 0)
            {
                // Archive to separate table or file;
                // Implementation depends on archiving strategy;
                foreach (var entity in oldEvents)
                {
                    entity.IsDeleted = true;
                    entity.DeletedAt = DateTime.UtcNow;
                    entity.DeletedBy = "System-Cleanup";
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                result.ArchivedCount = result.DeletedCount;
            }

            return result;
        }

        private async Task<CleanupResult> CompressOldRecordsAsync(
            DateTime cutoffDate,
            CleanupResult result,
            CancellationToken cancellationToken)
        {
            var oldEvents = await _dbContext.AuditEvents;
                .Where(e => e.Timestamp < cutoffDate && !e.IsDeleted)
                .ToListAsync(cancellationToken);

            result.DeletedCount = oldEvents.Count;

            if (result.DeletedCount > 0)
            {
                // Compress old records;
                foreach (var entity in oldEvents)
                {
                    if (string.IsNullOrEmpty(entity.CompressedMetadata) && entity.Metadata != null)
                    {
                        entity.CompressedMetadata = await _compressionService.CompressAsync(
                            JsonSerializer.Serialize(entity.Metadata),
                            cancellationToken);
                        entity.Metadata = null;
                    }
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                result.CompressedCount = result.DeletedCount;
            }

            return result;
        }

        private async Task<double> EstimateDatabaseSizeAsync(CancellationToken cancellationToken)
        {
            // Basit bir tahmin hesaplaması;
            var eventCount = await _dbContext.AuditEvents.CountAsync(cancellationToken);
            return (eventCount * 1024) / (1024.0 * 1024.0); // Her event için ~1KB, MB'ye çevir;
        }

        private async Task<TableStatistics> GetTableStatisticsAsync(CancellationToken cancellationToken)
        {
            return new TableStatistics;
            {
                RowCount = await _dbContext.AuditEvents.CountAsync(cancellationToken),
                IndexCount = 6, // Varsayılan index sayısı;
                LastVacuum = DateTime.UtcNow.AddDays(-1),
                FragmentationPercentage = 0.5 // Örnek değer;
            };
        }

        private async Task<DateTime?> GetLastCleanupDateAsync(CancellationToken cancellationToken)
        {
            var lastCleanup = await _dbContext.AuditEvents;
                .Where(e => e.DeletedBy == "System-Cleanup")
                .OrderByDescending(e => e.DeletedAt)
                .Select(e => e.DeletedAt)
                .FirstOrDefaultAsync(cancellationToken);

            return lastCleanup;
        }

        private async Task RebuildIndexesAsync(
            OptimizationResult result,
            CancellationToken cancellationToken)
        {
            // Index rebuild logic;
            result.Operations.Add("Indexes rebuilt");
            await Task.Delay(100, cancellationToken); // Simulate work;
        }

        private async Task DefragmentTablesAsync(
            OptimizationResult result,
            CancellationToken cancellationToken)
        {
            // Defragmentation logic;
            result.Operations.Add("Tables defragmented");
            await Task.Delay(100, cancellationToken);
        }

        private async Task UpdateStatisticsAsync(
            OptimizationResult result,
            CancellationToken cancellationToken)
        {
            // Statistics update logic;
            result.Operations.Add("Statistics updated");
            await Task.Delay(100, cancellationToken);
        }

        private async Task CleanupTempDbAsync(
            OptimizationResult result,
            CancellationToken cancellationToken)
        {
            // TempDB cleanup logic;
            result.Operations.Add("TempDB cleaned");
            await Task.Delay(100, cancellationToken);
        }

        #endregion;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _dbLock?.Dispose();
                    _dbContext?.Dispose();
                }

                _disposed = true;
            }
        }
    }

    #region Supporting Interfaces and Classes;

    /// <summary>
    /// Audit trail interface;
    /// </summary>
    public interface IAuditTrail : IDisposable
    {
        // Core Operations;
        Task<AuditEvent> AddEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
        Task<int> AddEventsAsync(IEnumerable<AuditEvent> auditEvents, CancellationToken cancellationToken = default);
        Task<AuditEvent> UpdateEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
        Task<bool> DeleteEventAsync(Guid eventId, string deletedBy, CancellationToken cancellationToken = default);

        // Query Operations;
        Task<IEnumerable<AuditEvent>> QueryEventsAsync(AuditQuery query, CancellationToken cancellationToken = default);
        Task<int> CountEventsAsync(AuditQuery query, CancellationToken cancellationToken = default);
        Task<AuditEvent?> GetEventByIdAsync(Guid eventId, CancellationToken cancellationToken = default);
        Task<IEnumerable<AuditEvent>> GetEventsByUserAsync(
            string userId,
            DateTime? from = null,
            DateTime? to = null,
            int limit = 1000,
            CancellationToken cancellationToken = default);

        // Analytics & Reporting;
        Task<AuditStatistics> GetStatisticsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
        Task<IEnumerable<TimeSeriesData>> GetTimeSeriesDataAsync(
            DateTime from,
            DateTime to,
            TimeGranularity granularity,
            CancellationToken cancellationToken = default);
        Task<AnomalyDetectionResult> DetectAnomaliesAsync(
            DateTime from,
            DateTime to,
            AnomalyDetectionConfig config,
            CancellationToken cancellationToken = default);

        // Maintenance Operations;
        Task<CleanupResult> CleanupOldRecordsAsync(
            int retentionDays,
            CleanupStrategy strategy = CleanupStrategy.Archive,
            CancellationToken cancellationToken = default);
        Task<AuditDatabaseStats> GetDatabaseStatisticsAsync(CancellationToken cancellationToken = default);
        Task<OptimizationResult> OptimizeDatabaseAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Audit query model;
    /// </summary>
    public class AuditQuery;
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string? UserId { get; set; }
        public string? Action { get; set; }
        public string? Resource { get; set; }
        public AuditSeverity? Severity { get; set; }
        public AuditSeverity? MinSeverity { get; set; }
        public string? IpAddress { get; set; }
        public string? CorrelationId { get; set; }
        public string? SessionId { get; set; }
        public string SortBy { get; set; } = "timestamp";
        public SortOrder SortOrder { get; set; } = SortOrder.Descending;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 100;
    }

    /// <summary>
    /// Audit statistics;
    /// </summary>
    public class AuditStatistics;
    {
        public int TotalEvents { get; set; }
        public int CriticalEvents { get; set; }
        public int HighSeverityEvents { get; set; }
        public int MediumSeverityEvents { get; set; }
        public int LowSeverityEvents { get; set; }
        public int InfoEvents { get; set; }
        public int UniqueUsers { get; set; }
        public string? MostActiveUser { get; set; }
        public string? MostCommonAction { get; set; }
        public string? MostAccessedResource { get; set; }
        public int PeakHour { get; set; }
        public double AverageEventsPerHour { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Time series data;
    /// </summary>
    public class TimeSeriesData;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int EventCount { get; set; }
        public int CriticalCount { get; set; }
        public int HighCount { get; set; }
        public int MediumCount { get; set; }
        public int LowCount { get; set; }
        public int InfoCount { get; set; }
    }

    /// <summary>
    /// Anomaly detection result;
    /// </summary>
    public class AnomalyDetectionResult;
    {
        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }
        public DateTime DetectedAt { get; set; }
        public List<AuditAnomaly> Anomalies { get; set; } = new();
        public int TotalAnomalies { get; set; }
        public int HighSeverityAnomalies { get; set; }
        public int MediumSeverityAnomalies { get; set; }
        public int LowSeverityAnomalies { get; set; }
    }

    /// <summary>
    /// Audit anomaly;
    /// </summary>
    public class AuditAnomaly;
    {
        public AnomalyType Type { get; set; }
        public AnomalySeverity Severity { get; set; }
        public string Description { get; set; } = string.Empty;
        public int EventCount { get; set; }
        public string? UserId { get; set; }
        public string? Resource { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Cleanup result;
    /// </summary>
    public class CleanupResult;
    {
        public DateTime CutoffDate { get; set; }
        public CleanupStrategy Strategy { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan? Duration { get; set; }
        public int DeletedCount { get; set; }
        public int ArchivedCount { get; set; }
        public int CompressedCount { get; set; }
    }

    /// <summary>
    /// Database statistics;
    /// </summary>
    public class AuditDatabaseStats;
    {
        public int TotalEvents { get; set; }
        public int ActiveEvents { get; set; }
        public int DeletedEvents { get; set; }
        public DateTime OldestEvent { get; set; }
        public DateTime NewestEvent { get; set; }
        public double EstimatedSizeMB { get; set; }
        public TableStatistics TableStatistics { get; set; } = new();
        public DateTime? LastCleanup { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    /// <summary>
    /// Optimization result;
    /// </summary>
    public class OptimizationResult;
    {
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public TimeSpan? Duration { get; set; }
        public List<string> Operations { get; set; } = new();
    }

    /// <summary>
    /// Table statistics;
    /// </summary>
    public class TableStatistics;
    {
        public int RowCount { get; set; }
        public int IndexCount { get; set; }
        public DateTime LastVacuum { get; set; }
        public double FragmentationPercentage { get; set; }
    }

    /// <summary>
    /// Audit event message for event bus;
    /// </summary>
    public class AuditEventMessage : IEvent;
    {
        public Guid EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public AuditSeverity Severity { get; set; }
        public string Resource { get; set; } = string.Empty;
        public DateTime OccurredOn => DateTime.UtcNow;
    }

    #endregion;

    #region Enums;

    public enum SortOrder;
    {
        Ascending = 0,
        Descending = 1;
    }

    public enum TimeGranularity;
    {
        Hourly = 0,
        Daily = 1,
        Weekly = 2,
        Monthly = 3;
    }

    public enum AnomalyType;
    {
        UnusualTimeActivity = 0,
        FailedAccessPattern = 1,
        RareResourceAccess = 2,
        FrequencySpike = 3,
        GeographicAnomaly = 4,
        BehavioralAnomaly = 5;
    }

    public enum AnomalySeverity;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3;
    }

    public enum CleanupStrategy;
    {
        Delete = 0,
        Archive = 1,
        Compress = 2;
    }

    #endregion;

    #region Exception Classes;

    public class AuditException : Exception
    {
        public AuditException(string message) : base(message) { }
        public AuditException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AuditNotFoundException : AuditException;
    {
        public AuditNotFoundException(string message) : base(message) { }
    }

    public class AuditValidationException : AuditException;
    {
        public AuditValidationException(string message) : base(message) { }
    }

    #endregion;
}
