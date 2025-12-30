using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using NEDA.Monitoring.Diagnostics;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Core.ExceptionHandling.ErrorReporting;

/// <summary>
/// Hata raporlama ve analiz sistemi;
/// Hataları toplar, analiz eder ve ilgili sistemlere bildirir;
/// </summary>
public interface IErrorReporter : IDisposable
{
    /// <summary>
    /// Hata raporlamayı başlat;
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hata raporu oluştur;
    /// </summary>
    Task<ErrorReport> ReportErrorAsync(
        Exception exception,
        ErrorContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kritik hata bildirimi gönder;
    /// </summary>
    Task SendCriticalAlertAsync(ErrorReport report, AlertRecipient recipient);

    /// <summary>
    /// Hata istatistiklerini getir;
    /// </summary>
    Task<ErrorStatistics> GetErrorStatisticsAsync(TimeRange timeRange);

    /// <summary>
    /// Benzer hataları grupla;
    /// </summary>
    Task<List<ErrorGroup>> GroupSimilarErrorsAsync(TimeRange timeRange);

    /// <summary>
    /// Hata trend analizi yap;
    /// </summary>
    Task<ErrorTrendAnalysis> AnalyzeErrorTrendsAsync(TimeRange timeRange);

    /// <summary>
    /// Hata için önerilen çözümleri getir;
    /// </summary>
    Task<List<ErrorResolution>> GetRecommendedResolutionsAsync(int errorCode);

    /// <summary>
    /// Hata raporunu arşivle;
    /// </summary>
    Task ArchiveErrorReportAsync(string reportId, ArchiveReason reason);

    /// <summary>
    /// Canlı hata izleme durumunu getir;
    /// </summary>
    Task<LiveErrorMonitoringStatus> GetLiveMonitoringStatusAsync();

    /// <summary>
    /// Hata pattern'larını tespit et;
    /// </summary>
    Task<List<ErrorPattern>> DetectErrorPatternsAsync(TimeRange timeRange);

    /// <summary>
    /// Hata bildirim gönderimini durdur;
    /// </summary>
    Task PauseNotificationsAsync(TimeSpan duration, string reason);

    /// <summary>
    /// Hata bildirim gönderimini devam ettir;
    /// </summary>
    Task ResumeNotificationsAsync();
}

/// <summary>
/// Hata raporlama motoru - Implementasyon;
/// </summary>
public class ErrorReporter : IErrorReporter
{
    private readonly ILogger<ErrorReporter> _logger;
    private readonly AppConfig _appConfig;
    private readonly ISecurityManager _securityManager;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly IHealthChecker _healthChecker;
    private readonly IDiagnosticTool _diagnosticTool;
    private readonly IEventBus _eventBus;
    private readonly ISystemManager _systemManager;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ErrorReportDatabase _errorDatabase;
    private readonly ErrorAnalysisEngine _analysisEngine;
    private readonly AlertNotificationService _alertService;
    private readonly ErrorPatternDetector _patternDetector;

    private bool _isInitialized;
    private bool _isDisposed;
    private bool _notificationsPaused;
    private DateTime _notificationsPausedUntil = DateTime.MinValue;
    private string _pauseReason = string.Empty;

    private readonly SemaphoreSlim _reportingLock = new(1, 1);
    private readonly CancellationTokenSource _backgroundTasksCts = new();
    private Task? _batchProcessingTask;
    private Task? _cleanupTask;

    private readonly Dictionary<int, ErrorRateCounter> _errorRateCounters = new();
    private readonly object _countersLock = new();

    /// <summary>
    /// Constructor;
    /// </summary>
    public ErrorReporter(
        ILogger<ErrorReporter> logger,
        IOptions<AppConfig> appConfigOptions,
        ISecurityManager securityManager,
        IPerformanceMonitor performanceMonitor,
        IHealthChecker healthChecker,
        IDiagnosticTool diagnosticTool,
        IEventBus eventBus,
        ISystemManager systemManager,
        IHttpClientFactory httpClientFactory,
        ErrorReportDatabase errorDatabase,
        ErrorAnalysisEngine analysisEngine,
        AlertNotificationService alertService,
        ErrorPatternDetector patternDetector)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _appConfig = appConfigOptions?.Value ?? throw new ArgumentNullException(nameof(appConfigOptions));
        _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
        _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

        _errorDatabase = errorDatabase ?? throw new ArgumentNullException(nameof(errorDatabase));
        _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
        _alertService = alertService ?? throw new ArgumentNullException(nameof(alertService));
        _patternDetector = patternDetector ?? throw new ArgumentNullException(nameof(patternDetector));

        InitializeErrorRateCounters();

        _logger.LogInformation("ErrorReporter initialized");
    }

    /// <summary>
    /// Başlatma;
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            _logger.LogWarning("ErrorReporter already initialized");
            return;
        }

        try
        {
            _logger.LogInformation("Starting ErrorReporter initialization...");

            // 1. Güvenlik kontrolü;
            await _securityManager.ValidateReportingPermissionsAsync(cancellationToken);

            // 2. Veritabanını başlat;
            await _errorDatabase.InitializeAsync(cancellationToken);

            // 3. Analiz motorunu başlat;
            await _analysisEngine.InitializeAsync(cancellationToken);

            // 4. Alert servisini başlat;
            await _alertService.InitializeAsync(cancellationToken);

            // 5. Pattern detector'ı başlat;
            await _patternDetector.InitializeAsync(cancellationToken);

            // 6. Arka plan görevlerini başlat;
            StartBackgroundTasks();

            // 7. Event subscription;
            await SubscribeToSystemEventsAsync();

            _isInitialized = true;

            _logger.LogInformation("ErrorReporter initialization completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ErrorReporter initialization failed");
            throw new ErrorReportingInitializationException(
                ErrorCodes.System.SYSTEM_INITIALIZATION_FAILED,
                "Failed to initialize ErrorReporter",
                ex);
        }
    }

    /// <summary>
    /// Hata raporu oluştur;
    /// </summary>
    public async Task<ErrorReport> ReportErrorAsync(
        Exception exception,
        ErrorContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateInitialization();
        if (exception == null) throw new ArgumentNullException(nameof(exception));
        context ??= new ErrorContext();

        await _reportingLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var reportId = GenerateReportId();
            var timestamp = DateTime.UtcNow;

            _logger.LogDebug("Creating error report: {ReportId}", reportId);

            // 1. Hata bilgilerini topla;
            var errorInfo = await CollectErrorInformationAsync(exception, context, timestamp).ConfigureAwait(false);

            // 2. Sistem durumunu kaydet;
            var systemState = await CaptureSystemStateAsync(timestamp).ConfigureAwait(false);

            // 3. Hata kodunu belirle;
            var errorCode = DetermineErrorCode(exception, context);
            var severity = ErrorCodes.GetErrorSeverity(errorCode);

            // 4. Hata raporu oluştur;
            var report = new ErrorReport
            {
                ReportId = reportId,
                ErrorCode = errorCode,
                ExceptionType = exception.GetType().FullName ?? "Unknown",
                ExceptionMessage = exception.Message,
                StackTrace = exception.StackTrace ?? string.Empty,
                InnerException = exception.InnerException?.Message,
                Severity = severity,
                Timestamp = timestamp,
                Context = context,
                ErrorInformation = errorInfo,
                SystemState = systemState,
                UserId = context.UserId,
                SessionId = context.SessionId,
                Component = context.Component,
                Operation = context.Operation,
                Environment = _appConfig.Environment,
                Version = _appConfig.Version
            };

            // 5. Hata oranını güncelle;
            UpdateErrorRate(errorCode, severity);

            // 6. Veritabanına kaydet;
            await _errorDatabase.StoreReportAsync(report, cancellationToken).ConfigureAwait(false);

            // 7. Analiz motoruna gönder;
            await _analysisEngine.AnalyzeErrorAsync(report, cancellationToken).ConfigureAwait(false);

            // 8. Kritik hatalar için bildirim gönder;
            if (severity >= ErrorCodes.ErrorSeverity.Critical && !_notificationsPaused)
            {
                await SendCriticalAlertInternalAsync(report, cancellationToken).ConfigureAwait(false);
            }

            // 9. Event yayınla;
            await PublishErrorEventAsync(report, cancellationToken).ConfigureAwait(false);

            // 10. Hata pattern kontrolü;
            await CheckForErrorPatternsAsync(report, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Error reported: {ReportId}, Code: {ErrorCode}, Severity: {Severity}",
                reportId, errorCode, severity);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create error report");
            throw new ErrorReportingException(
                ErrorCodes.Application.PROCESSING_FAILED,
                "Failed to create error report",
                ex);
        }
        finally
        {
            _reportingLock.Release();
        }
    }

    /// <summary>
    /// Kritik hata bildirimi gönder;
    /// </summary>
    public async Task SendCriticalAlertAsync(ErrorReport report, AlertRecipient recipient)
    {
        ValidateInitialization();
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (recipient == null) throw new ArgumentNullException(nameof(recipient));

        try
        {
            if (_notificationsPaused && DateTime.UtcNow < _notificationsPausedUntil)
            {
                _logger.LogWarning("Notifications are paused until {PausedUntil}. Reason: {Reason}",
                    _notificationsPausedUntil, _pauseReason);
                return;
            }

            _logger.LogInformation("Sending critical alert for report: {ReportId}", report.ReportId);

            var alertMessage = CreateAlertMessage(report);

            switch (recipient.Channel)
            {
                case AlertChannel.Email:
                    await _alertService.SendEmailAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
                case AlertChannel.SMS:
                    await _alertService.SendSmsAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
                case AlertChannel.Push:
                    await _alertService.SendPushAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
                case AlertChannel.Slack:
                    await _alertService.SendSlackAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
                case AlertChannel.Teams:
                    await _alertService.SendTeamsAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
                case AlertChannel.Webhook:
                    await _alertService.SendWebhookAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
                case AlertChannel.PagerDuty:
                    await _alertService.SendPagerDutyAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
                default:
                    await _alertService.SendWebhookAlertAsync(recipient, alertMessage).ConfigureAwait(false);
                    break;
            }

            await _errorDatabase.LogAlertAsync(report.ReportId, recipient, DateTime.UtcNow).ConfigureAwait(false);

            _logger.LogInformation("Critical alert sent for report: {ReportId}", report.ReportId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send critical alert for report: {ReportId}", report.ReportId);
            throw new AlertDeliveryException(
                ErrorCodes.Integration.API_CALL_FAILED,
                "Failed to send critical alert",
                ex);
        }
    }

    /// <summary>
    /// Hata istatistiklerini getir;
    /// </summary>
    public async Task<ErrorStatistics> GetErrorStatisticsAsync(TimeRange timeRange)
    {
        ValidateInitialization();
        timeRange ??= new TimeRange();

        try
        {
            _logger.LogDebug("Getting error statistics for time range: {TimeRange}", timeRange);

            var statistics = new ErrorStatistics
            {
                TimeRange = timeRange,
                TotalErrors = await _errorDatabase.GetErrorCountAsync(timeRange).ConfigureAwait(false),
                ErrorBySeverity = await _errorDatabase.GetErrorsBySeverityAsync(timeRange).ConfigureAwait(false),
                ErrorByCategory = await _errorDatabase.GetErrorsByCategoryAsync(timeRange).ConfigureAwait(false),
                ErrorByComponent = await _errorDatabase.GetErrorsByComponentAsync(timeRange).ConfigureAwait(false),
                TopErrorCodes = await _errorDatabase.GetTopErrorCodesAsync(timeRange, 10).ConfigureAwait(false),
                MeanTimeBetweenErrors = await CalculateMeanTimeBetweenErrorsAsync(timeRange).ConfigureAwait(false),
                ErrorResolutionRate = await CalculateResolutionRateAsync(timeRange).ConfigureAwait(false),
                RecentTrend = await CalculateRecentTrendAsync(timeRange).ConfigureAwait(false)
            };

            lock (_countersLock)
            {
                statistics.CurrentErrorRates = _errorRateCounters.Values
                    .Select(c => new ErrorRate
                    {
                        ErrorCode = c.ErrorCode,
                        CountLastHour = c.GetCountLastHour(),
                        CountLastDay = c.GetCountLastDay(),
                        RatePerMinute = c.GetRatePerMinute()
                    })
                    .ToList();
            }

            return statistics;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get error statistics");
            throw new ErrorAnalysisException(
                ErrorCodes.Data.DATA_AGGREGATION_FAILED,
                "Failed to get error statistics",
                ex);
        }
    }

    /// <summary>
    /// Benzer hataları grupla;
    /// </summary>
    public async Task<List<ErrorGroup>> GroupSimilarErrorsAsync(TimeRange timeRange)
    {
        ValidateInitialization();
        timeRange ??= new TimeRange();

        try
        {
            _logger.LogDebug("Grouping similar errors for time range: {TimeRange}", timeRange);

            var errors = await _errorDatabase.GetErrorsInTimeRangeAsync(timeRange).ConfigureAwait(false);

            var groups = await _analysisEngine.GroupSimilarErrorsAsync(errors).ConfigureAwait(false);

            foreach (var group in groups)
            {
                group.RootCauseAnalysis = await _analysisEngine.AnalyzeRootCauseAsync(group).ConfigureAwait(false);
                group.RecommendedActions = await GetRecommendedActionsForGroupAsync(group).ConfigureAwait(false);
                group.Trend = await AnalyzeGroupTrendAsync(group, timeRange).ConfigureAwait(false);
            }

            return groups
                .OrderByDescending(g => g.ErrorCount)
                .ThenByDescending(g => g.Severity)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to group similar errors");
            throw new ErrorAnalysisException(
                ErrorCodes.AI.AI_PROCESSING_FAILED,
                "Failed to group similar errors",
                ex);
        }
    }

    /// <summary>
    /// Hata trend analizi;
    /// </summary>
    public async Task<ErrorTrendAnalysis> AnalyzeErrorTrendsAsync(TimeRange timeRange)
    {
        ValidateInitialization();
        timeRange ??= new TimeRange();

        try
        {
            _logger.LogDebug("Analyzing error trends for time range: {TimeRange}", timeRange);

            var analysis = new ErrorTrendAnalysis
            {
                TimeRange = timeRange,
                OverallTrend = await CalculateOverallTrendAsync(timeRange).ConfigureAwait(false),
                CategoryTrends = await CalculateCategoryTrendsAsync(timeRange).ConfigureAwait(false),
                ComponentTrends = await CalculateComponentTrendsAsync(timeRange).ConfigureAwait(false),
                PeakHours = await IdentifyPeakErrorHoursAsync(timeRange).ConfigureAwait(false),
                CorrelationWithSystemLoad = await CorrelateWithSystemLoadAsync(timeRange).ConfigureAwait(false),
                SeasonalityPatterns = await DetectSeasonalityPatternsAsync(timeRange).ConfigureAwait(false),
                Prediction = await PredictFutureErrorsAsync(timeRange).ConfigureAwait(false)
            };

            analysis.Anomalies = await DetectTrendAnomaliesAsync(analysis).ConfigureAwait(false);
            analysis.Recommendations = await GenerateTrendRecommendationsAsync(analysis).ConfigureAwait(false);

            return analysis;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze error trends");
            throw new ErrorAnalysisException(
                ErrorCodes.Analytics.ANALYTICS_PROCESSING_FAILED,
                "Failed to analyze error trends",
                ex);
        }
    }

    /// <summary>
    /// Önerilen çözümleri getir;
    /// </summary>
    public async Task<List<ErrorResolution>> GetRecommendedResolutionsAsync(int errorCode)
    {
        ValidateInitialization();

        try
        {
            _logger.LogDebug("Getting recommended resolutions for error code: {ErrorCode}", errorCode);

            var resolutions = new List<ErrorResolution>();

            var builtInResolution = ErrorCodes.GetErrorResolution(errorCode);
            if (!string.IsNullOrEmpty(builtInResolution))
            {
                resolutions.Add(new ErrorResolution
                {
                    Type = ResolutionType.BuiltIn,
                    Description = builtInResolution,
                    Confidence = 0.8f,
                    Source = "System"
                });
            }

            var historicalResolutions = await _errorDatabase.GetHistoricalResolutionsAsync(errorCode).ConfigureAwait(false);
            if (historicalResolutions != null)
                resolutions.AddRange(historicalResolutions);

            var aiResolutions = await _analysisEngine.GenerateResolutionsAsync(errorCode).ConfigureAwait(false);
            if (aiResolutions != null)
                resolutions.AddRange(aiResolutions);

            var externalResolutions = await GetExternalResolutionsAsync(errorCode).ConfigureAwait(false);
            if (externalResolutions != null)
                resolutions.AddRange(externalResolutions);

            return resolutions
                .OrderByDescending(r => r.Confidence)
                .ThenByDescending(r => r.Priority)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recommended resolutions");
            throw new ErrorAnalysisException(
                ErrorCodes.AI.AI_PROCESSING_FAILED,
                "Failed to get recommended resolutions",
                ex);
        }
    }

    /// <summary>
    /// Hata raporunu arşivle;
    /// </summary>
    public async Task ArchiveErrorReportAsync(string reportId, ArchiveReason reason)
    {
        ValidateInitialization();
        if (string.IsNullOrWhiteSpace(reportId))
            throw new ArgumentException("ReportId cannot be empty", nameof(reportId));

        try
        {
            _logger.LogInformation("Archiving error report: {ReportId}, Reason: {Reason}", reportId, reason);

            await _errorDatabase.ArchiveReportAsync(reportId, reason, DateTime.UtcNow).ConfigureAwait(false);

            await _eventBus.PublishAsync(new ErrorReportArchivedEvent
            {
                ReportId = reportId,
                ArchiveTime = DateTime.UtcNow,
                Reason = reason,
                ArchivedBy = _securityManager.GetCurrentUserId()
            }).ConfigureAwait(false);

            _logger.LogInformation("Error report archived: {ReportId}", reportId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive error report: {ReportId}", reportId);
            throw new ErrorReportingException(
                ErrorCodes.Data.DATA_PARSING_FAILED,
                "Failed to archive error report",
                ex);
        }
    }

    /// <summary>
    /// Canlı hata izleme durumu;
    /// </summary>
    public async Task<LiveErrorMonitoringStatus> GetLiveMonitoringStatusAsync()
    {
        ValidateInitialization();

        var status = new LiveErrorMonitoringStatus
        {
            IsActive = _isInitialized,
            LastUpdate = DateTime.UtcNow,
            TotalReportsProcessed = await _errorDatabase.GetTotalReportCountAsync().ConfigureAwait(false),
            ReportsInLastHour = await _errorDatabase.GetReportCountSinceAsync(DateTime.UtcNow.AddHours(-1)).ConfigureAwait(false),
            CurrentErrorRate = CalculateCurrentErrorRate(),
            ActiveAlerts = await _alertService.GetActiveAlertCountAsync().ConfigureAwait(false),
            SystemHealth = await _healthChecker.GetSystemHealthAsync().ConfigureAwait(false)
        };

        lock (_countersLock)
        {
            status.ErrorRateCounters = _errorRateCounters.Values
                .Where(c => c.GetCountLastHour() > 0)
                .Select(c => new ErrorCounterStatus
                {
                    ErrorCode = c.ErrorCode,
                    CountLastHour = c.GetCountLastHour(),
                    CountLastDay = c.GetCountLastDay(),
                    LastOccurrence = c.LastOccurrence
                })
                .ToList();
        }

        if (_notificationsPaused)
        {
            status.NotificationsStatus = new NotificationsStatus
            {
                IsPaused = true,
                PausedUntil = _notificationsPausedUntil,
                PauseReason = _pauseReason
            };
        }

        return status;
    }

    /// <summary>
    /// Hata pattern'larını tespit et;
    /// </summary>
    public async Task<List<ErrorPattern>> DetectErrorPatternsAsync(TimeRange timeRange)
    {
        ValidateInitialization();
        timeRange ??= new TimeRange();

        try
        {
            _logger.LogDebug("Detecting error patterns for time range: {TimeRange}", timeRange);

            var patterns = await _patternDetector.DetectPatternsAsync(timeRange).ConfigureAwait(false);

            foreach (var pattern in patterns)
            {
                pattern.ImpactAnalysis = await AnalyzePatternImpactAsync(pattern).ConfigureAwait(false);
                pattern.PreventionStrategies = await GeneratePreventionStrategiesAsync(pattern).ConfigureAwait(false);
                pattern.MonitoringRecommendations = await GenerateMonitoringRecommendationsAsync(pattern).ConfigureAwait(false);
            }

            return patterns
                .OrderByDescending(p => p.Confidence)
                .ThenByDescending(p => p.Frequency)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect error patterns");
            throw new ErrorAnalysisException(
                ErrorCodes.AI.PATTERN_RECOGNITION_FAILED,
                "Failed to detect error patterns",
                ex);
        }
    }

    public Task PauseNotificationsAsync(TimeSpan duration, string reason)
    {
        _notificationsPaused = true;
        _notificationsPausedUntil = DateTime.UtcNow.Add(duration);
        _pauseReason = reason ?? string.Empty;

        _logger.LogWarning(
            "Error notifications paused until {PausedUntil}. Reason: {Reason}",
            _notificationsPausedUntil, _pauseReason);

        _ = _eventBus.PublishAsync(new NotificationsPausedEvent
        {
            PausedAt = DateTime.UtcNow,
            PausedUntil = _notificationsPausedUntil,
            Reason = _pauseReason,
            Duration = duration
        });

        return Task.CompletedTask;
    }

    public Task ResumeNotificationsAsync()
    {
        _notificationsPaused = false;
        _notificationsPausedUntil = DateTime.MinValue;
        _pauseReason = string.Empty;

        _logger.LogInformation("Error notifications resumed");

        _ = _eventBus.PublishAsync(new NotificationsResumedEvent
        {
            ResumedAt = DateTime.UtcNow
        });

        return Task.CompletedTask;
    }

    #region Private Helpers

    private void InitializeErrorRateCounters()
    {
        var errorCodes = ErrorCodes.GetAllErrorCodes().Select(ec => ec.Code);
        foreach (var errorCode in errorCodes)
        {
            _errorRateCounters[errorCode] = new ErrorRateCounter(errorCode);
        }
    }

    private void StartBackgroundTasks()
    {
        _batchProcessingTask = Task.Run(async () =>
        {
            while (!_backgroundTasksCts.Token.IsCancellationRequested)
            {
                try
                {
                    await ProcessBatchReportsAsync(_backgroundTasksCts.Token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(30), _backgroundTasksCts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Batch processing task failed"); }
            }
        }, _backgroundTasksCts.Token);

        _cleanupTask = Task.Run(async () =>
        {
            while (!_backgroundTasksCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), _backgroundTasksCts.Token).ConfigureAwait(false);
                    await CleanupOldReportsAsync(_backgroundTasksCts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Cleanup task failed"); }
            }
        }, _backgroundTasksCts.Token);
    }

    private async Task SubscribeToSystemEventsAsync()
    {
        await _eventBus.SubscribeAsync<SystemHealthDegradedEvent>(async @event =>
        {
            var context = new ErrorContext
            {
                Component = "System",
                Operation = "HealthMonitoring",
                UserId = "System",
                SessionId = Guid.NewGuid().ToString(),
                AdditionalData = new Dictionary<string, object>
                {
                    ["HealthStatus"] = @event.HealthStatus,
                    ["DegradedComponents"] = @event.DegradedComponents
                }
            };

            var exception = new SystemHealthException(
                $"System health degraded: {@event.HealthStatus}");

            await ReportErrorAsync(exception, context).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await _eventBus.SubscribeAsync<PerformanceThresholdExceededEvent>(async @event =>
        {
            var context = new ErrorContext
            {
                Component = "Performance",
                Operation = "Monitoring",
                UserId = "System",
                SessionId = Guid.NewGuid().ToString(),
                AdditionalData = new Dictionary<string, object>
                {
                    ["Metric"] = @event.MetricName,
                    ["Value"] = @event.CurrentValue,
                    ["Threshold"] = @event.Threshold,
                    ["Component"] = @event.Component
                }
            };

            var exception = new PerformanceException(
                $"Performance threshold exceeded: {@event.MetricName} = {@event.CurrentValue}");

            await ReportErrorAsync(exception, context).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private string GenerateReportId()
        => $"ERR-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}".Substring(0, 32);

    private async Task<ErrorInformation> CollectErrorInformationAsync(
        Exception exception,
        ErrorContext context,
        DateTime timestamp)
    {
        var inner = exception.InnerException != null
            ? new InnerExceptionInfo
            {
                Type = exception.InnerException.GetType().FullName ?? "Unknown",
                Message = exception.InnerException.Message,
                StackTrace = exception.InnerException.StackTrace ?? string.Empty
            }
            : null;

        var errorInfo = new ErrorInformation
        {
            ExceptionType = exception.GetType().FullName ?? "Unknown",
            ExceptionMessage = exception.Message,
            StackTrace = exception.StackTrace ?? string.Empty,
            Source = exception.Source,
            HelpLink = exception.HelpLink,
            HResult = exception.HResult,
            Data = exception.Data?.Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(
                    e => e.Key?.ToString() ?? string.Empty,
                    e => e.Value?.ToString() ?? string.Empty)
                ?? new Dictionary<string, string>(),
            InnerException = inner,
            Timestamp = timestamp
        };

        errorInfo.DiagnosticInfo = await _diagnosticTool.CaptureDiagnosticsAsync(exception, context).ConfigureAwait(false);
        errorInfo.PerformanceCounters = await _performanceMonitor.GetCurrentCountersAsync().ConfigureAwait(false);

        if (ShouldCaptureMemoryDump(exception))
        {
            errorInfo.MemoryDumpInfo = await _diagnosticTool.CaptureMemoryInfoAsync().ConfigureAwait(false);
        }

        return errorInfo;
    }

    private async Task<SystemState> CaptureSystemStateAsync(DateTime timestamp)
    {
        var systemState = new SystemState
        {
            Timestamp = timestamp,
            Environment = _appConfig.Environment,
            Version = _appConfig.Version,
            MachineName = Environment.MachineName,
            OSVersion = Environment.OSVersion.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            MemoryUsage = GC.GetTotalMemory(false),
            WorkingSet = Environment.WorkingSet,
            Is64BitProcess = Environment.Is64BitProcess,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            UserName = Environment.UserName,
            UserDomainName = Environment.UserDomainName,
            CurrentDirectory = Environment.CurrentDirectory,
            SystemDirectory = Environment.SystemDirectory,
            TickCount = Environment.TickCount64
        };

        systemState.ProcessInfo = await _systemManager.GetProcessInfoAsync().ConfigureAwait(false);
        systemState.NetworkInfo = await _systemManager.GetNetworkInfoAsync().ConfigureAwait(false);
        systemState.DiskInfo = await _systemManager.GetDiskInfoAsync().ConfigureAwait(false);
        systemState.ServiceStatus = await _systemManager.GetServiceStatusAsync().ConfigureAwait(false);

        systemState.CpuUsage = await _performanceMonitor.GetCpuUsageAsync().ConfigureAwait(false);
        systemState.MemoryUsagePercent = await _performanceMonitor.GetMemoryUsagePercentageAsync().ConfigureAwait(false);
        systemState.DiskUsage = await _performanceMonitor.GetDiskUsageAsync().ConfigureAwait(false);
        systemState.NetworkUsage = await _performanceMonitor.GetNetworkUsageAsync().ConfigureAwait(false);

        return systemState;
    }

    private int DetermineErrorCode(Exception exception, ErrorContext context)
    {
        return exception switch
        {
            NedaException nedaEx => nedaEx.ErrorCode,
            SecurityException => ErrorCodes.Security.AUTHENTICATION_FAILED,
            UnauthorizedAccessException => ErrorCodes.Security.AUTHORIZATION_DENIED,
            ArgumentException => ErrorCodes.Validation.INPUT_VALIDATION_FAILED,
            InvalidOperationException => ErrorCodes.Application.BUSINESS_RULE_VIOLATION,
            TimeoutException => ErrorCodes.Network.CONNECTION_TIMEOUT,
            IOException => ErrorCodes.System.IO_EXCEPTION,
            OutOfMemoryException => ErrorCodes.System.OUT_OF_MEMORY,
            StackOverflowException => ErrorCodes.System.STACK_OVERFLOW,
            DivideByZeroException => ErrorCodes.System.RUNTIME_EXCEPTION,
            NotImplementedException => ErrorCodes.System.NOT_IMPLEMENTED,
            NotSupportedException => ErrorCodes.System.NOT_SUPPORTED,
            _ => ErrorCodes.System.RUNTIME_EXCEPTION
        };
    }

    private void UpdateErrorRate(int errorCode, ErrorCodes.ErrorSeverity severity)
    {
        lock (_countersLock)
        {
            if (_errorRateCounters.TryGetValue(errorCode, out var counter))
            {
                counter.Increment();

                if (severity >= ErrorCodes.ErrorSeverity.Critical)
                {
                    var rate = counter.GetRatePerMinute();
                    if (rate > _appConfig.ErrorReporting.CriticalErrorRateThreshold)
                    {
                        _logger.LogWarning(
                            "Critical error rate threshold exceeded for error {ErrorCode}: {Rate}/min",
                            errorCode, rate);

                        _ = _eventBus.PublishAsync(new ErrorRateThresholdExceededEvent
                        {
                            ErrorCode = errorCode,
                            CurrentRate = rate,
                            Threshold = _appConfig.ErrorReporting.CriticalErrorRateThreshold,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                }
            }
        }
    }

    private async Task SendCriticalAlertInternalAsync(ErrorReport report, CancellationToken cancellationToken)
    {
        var recipients = _appConfig.ErrorReporting.CriticalAlertRecipients ?? new List<AlertRecipient>();

        foreach (var recipient in recipients)
        {
            try
            {
                await SendCriticalAlertAsync(report, recipient).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send alert to recipient: {Recipient}", recipient.Name);
            }
        }
    }

    private AlertMessage CreateAlertMessage(ErrorReport report)
    {
        return new AlertMessage
        {
            Title = $"🚨 CRITICAL ERROR: {report.ErrorCode}",
            Message = $"""
                ⚠️ Critical Error Detected

                Error Code: {report.ErrorCode}
                Severity: {report.Severity}
                Component: {report.Component}
                Time: {report.Timestamp:yyyy-MM-dd HH:mm:ss} UTC
                User: {report.UserId}

                Exception: {report.ExceptionMessage}

                System State:
                • CPU: {report.SystemState.CpuUsage}%
                • Memory: {report.SystemState.MemoryUsagePercent}%
                • Environment: {report.Environment}

                Action Required: Immediate investigation needed.
                """,
            Priority = AlertPriority.Critical,
            Category = "Error",
            Metadata = new Dictionary<string, object>
            {
                ["ReportId"] = report.ReportId,
                ["ErrorCode"] = report.ErrorCode,
                ["Component"] = report.Component,
                ["Environment"] = report.Environment
            }
        };
    }

    private Task PublishErrorEventAsync(ErrorReport report, CancellationToken cancellationToken)
    {
        return _eventBus.PublishAsync(new ErrorReportedEvent
        {
            ReportId = report.ReportId,
            ErrorCode = report.ErrorCode,
            Severity = report.Severity,
            Timestamp = report.Timestamp,
            Component = report.Component,
            UserId = report.UserId,
            Environment = report.Environment
        });
    }

    private async Task CheckForErrorPatternsAsync(ErrorReport report, CancellationToken cancellationToken)
    {
        var similarErrors = await _errorDatabase.GetSimilarErrorsAsync(
            report.ErrorCode,
            report.Component,
            TimeSpan.FromHours(1)).ConfigureAwait(false);

        if (similarErrors.Count >= _appConfig.ErrorReporting.PatternDetectionThreshold)
        {
            _logger.LogWarning(
                "Potential error pattern detected: {ErrorCode} occurred {Count} times in last hour",
                report.ErrorCode, similarErrors.Count + 1);

            await _eventBus.PublishAsync(new ErrorPatternDetectedEvent
            {
                ErrorCode = report.ErrorCode,
                Component = report.Component,
                OccurrenceCount = similarErrors.Count + 1,
                TimeWindow = TimeSpan.FromHours(1),
                FirstOccurrence = similarErrors.FirstOrDefault()?.Timestamp ?? report.Timestamp,
                LastOccurrence = report.Timestamp
            }).ConfigureAwait(false);
        }
    }

    private async Task ProcessBatchReportsAsync(CancellationToken cancellationToken)
    {
        var batchSize = _appConfig.ErrorReporting.BatchProcessingSize;
        var pendingReports = await _errorDatabase.GetPendingReportsAsync(batchSize, cancellationToken).ConfigureAwait(false);

        if (!pendingReports.Any())
            return;

        _logger.LogDebug("Processing batch of {Count} error reports", pendingReports.Count);

        await _analysisEngine.BatchAnalyzeAsync(pendingReports, cancellationToken).ConfigureAwait(false);

        var criticalReports = pendingReports
            .Where(r => r.Severity >= ErrorCodes.ErrorSeverity.Critical)
            .ToList();

        if (criticalReports.Any() && !_notificationsPaused)
        {
            await SendBatchAlertsAsync(criticalReports, cancellationToken).ConfigureAwait(false);
        }

        await _errorDatabase.MarkReportsAsProcessedAsync(
            pendingReports.Select(r => r.ReportId).ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupOldReportsAsync(CancellationToken cancellationToken)
    {
        var retentionDays = _appConfig.ErrorReporting.RetentionDays;
        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

        _logger.LogDebug("Cleaning up error reports older than {CutoffDate}", cutoffDate);

        var deletedCount = await _errorDatabase.DeleteOldReportsAsync(cutoffDate, cancellationToken).ConfigureAwait(false);

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old error reports", deletedCount);
        }
    }

    private async Task SendBatchAlertsAsync(List<ErrorReport> reports, CancellationToken cancellationToken)
    {
        var summary = new BatchErrorSummary
        {
            TotalErrors = reports.Count,
            CriticalCount = reports.Count(r => r.Severity == ErrorCodes.ErrorSeverity.Critical),
            ErrorCount = reports.Count(r => r.Severity == ErrorCodes.ErrorSeverity.Error),
            TopErrorCodes = reports
                .GroupBy(r => r.ErrorCode)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .ToDictionary(g => g.Key, g => g.Count()),
            TimeRange = new TimeRange
            {
                Start = reports.Min(r => r.Timestamp),
                End = reports.Max(r => r.Timestamp)
            }
        };

        await _alertService.SendBatchSummaryAlertAsync(summary, cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldCaptureMemoryDump(Exception exception)
    {
        var errorCode = DetermineErrorCode(exception, new ErrorContext());
        var severity = ErrorCodes.GetErrorSeverity(errorCode);

        return severity >= ErrorCodes.ErrorSeverity.Critical ||
               errorCode == ErrorCodes.System.OUT_OF_MEMORY ||
               errorCode == ErrorCodes.Resource.MEMORY_EXHAUSTED;
    }

    private double CalculateCurrentErrorRate()
    {
        lock (_countersLock)
        {
            var totalLastHour = _errorRateCounters.Values.Sum(c => c.GetCountLastHour());
            return totalLastHour / 60.0;
        }
    }

    private void ValidateInitialization()
    {
        if (!_isInitialized)
        {
            throw new ErrorReportingInitializationException(
                ErrorCodes.System.SYSTEM_NOT_INITIALIZED,
                "ErrorReporter is not initialized. Call InitializeAsync first.");
        }
    }

    #region Statistics Calculations

    private async Task<TimeSpan> CalculateMeanTimeBetweenErrorsAsync(TimeRange timeRange)
    {
        var errors = await _errorDatabase.GetErrorsInTimeRangeAsync(timeRange).ConfigureAwait(false);
        if (errors.Count < 2) return TimeSpan.Zero;

        var sorted = errors.OrderBy(e => e.Timestamp).ToList();
        var total = sorted.Last().Timestamp - sorted.First().Timestamp;
        var intervals = errors.Count - 1;

        return TimeSpan.FromTicks(total.Ticks / intervals);
    }

    private async Task<double> CalculateResolutionRateAsync(TimeRange timeRange)
    {
        var resolvedCount = await _errorDatabase.GetResolvedErrorCountAsync(timeRange).ConfigureAwait(false);
        var totalCount = await _errorDatabase.GetErrorCountAsync(timeRange).ConfigureAwait(false);
        return totalCount > 0 ? (double)resolvedCount / totalCount : 0;
    }

    private async Task<TrendDirection> CalculateRecentTrendAsync(TimeRange timeRange)
    {
        var recentEnd = timeRange.End;
        var recentStart = recentEnd.AddHours(-1);
        var previousEnd = recentStart;
        var previousStart = previousEnd.AddHours(-1);

        var recentCount = await _errorDatabase.GetErrorCountAsync(new TimeRange(recentStart, recentEnd)).ConfigureAwait(false);
        var previousCount = await _errorDatabase.GetErrorCountAsync(new TimeRange(previousStart, previousEnd)).ConfigureAwait(false);

        if (previousCount == 0) return TrendDirection.Stable;

        var ratio = (double)recentCount / previousCount;

        return ratio switch
        {
            > 1.5 => TrendDirection.Increasing,
            < 0.67 => TrendDirection.Decreasing,
            _ => TrendDirection.Stable
        };
    }

    private async Task<TrendDirection> CalculateOverallTrendAsync(TimeRange timeRange)
    {
        var quarterDuration = (timeRange.End - timeRange.Start) / 4;
        var counts = new List<int>();

        for (int i = 0; i < 4; i++)
        {
            var start = timeRange.Start.Add(quarterDuration * i);
            var end = start.Add(quarterDuration);
            var count = await _errorDatabase.GetErrorCountAsync(new TimeRange(start, end)).ConfigureAwait(false);
            counts.Add(count);
        }

        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        int n = counts.Count;

        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += counts[i];
            sumXY += i * counts[i];
            sumX2 += i * i;
        }

        var denom = (n * sumX2 - sumX * sumX);
        var slope = denom == 0 ? 0 : (n * sumXY - sumX * sumY) / denom;

        return slope switch
        {
            > 0.1 => TrendDirection.Increasing,
            < -0.1 => TrendDirection.Decreasing,
            _ => TrendDirection.Stable
        };
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _backgroundTasksCts.Cancel();
            _backgroundTasksCts.Dispose();

            _reportingLock.Dispose();

            _errorDatabase?.Dispose();
            _analysisEngine?.Dispose();
            _alertService?.Dispose();
            _patternDetector?.Dispose();
        }

        _isDisposed = true;
    }

    #endregion

    #region Placeholder Methods for External Integrations

    private Task<List<ErrorResolution>> GetExternalResolutionsAsync(int errorCode)
        => Task.FromResult(new List<ErrorResolution>());

    private Task<List<ErrorPatternImpact>> AnalyzePatternImpactAsync(ErrorPattern pattern)
        => Task.FromResult(new List<ErrorPatternImpact>());

    private Task<List<PreventionStrategy>> GeneratePreventionStrategiesAsync(ErrorPattern pattern)
        => Task.FromResult(new List<PreventionStrategy>());

    private Task<List<MonitoringRecommendation>> GenerateMonitoringRecommendationsAsync(ErrorPattern pattern)
        => Task.FromResult(new List<MonitoringRecommendation>());

    private Task<List<ErrorCategoryTrend>> CalculateCategoryTrendsAsync(TimeRange timeRange)
        => Task.FromResult(new List<ErrorCategoryTrend>());

    private Task<List<ComponentTrend>> CalculateComponentTrendsAsync(TimeRange timeRange)
        => Task.FromResult(new List<ComponentTrend>());

    private Task<List<PeakHour>> IdentifyPeakErrorHoursAsync(TimeRange timeRange)
        => Task.FromResult(new List<PeakHour>());

    private Task<CorrelationResult> CorrelateWithSystemLoadAsync(TimeRange timeRange)
        => Task.FromResult(new CorrelationResult());

    private Task<List<SeasonalityPattern>> DetectSeasonalityPatternsAsync(TimeRange timeRange)
        => Task.FromResult(new List<SeasonalityPattern>());

    private Task<ErrorPrediction> PredictFutureErrorsAsync(TimeRange timeRange)
        => Task.FromResult(new ErrorPrediction());

    private Task<List<TrendAnomaly>> DetectTrendAnomaliesAsync(ErrorTrendAnalysis analysis)
        => Task.FromResult(new List<TrendAnomaly>());

    private Task<List<TrendRecommendation>> GenerateTrendRecommendationsAsync(ErrorTrendAnalysis analysis)
        => Task.FromResult(new List<TrendRecommendation>());

    private Task<List<RecommendedAction>> GetRecommendedActionsForGroupAsync(ErrorGroup group)
        => Task.FromResult(new List<RecommendedAction>());

    private Task<GroupTrend> AnalyzeGroupTrendAsync(ErrorGroup group, TimeRange timeRange)
        => Task.FromResult(new GroupTrend());

    #endregion

    #endregion
}

#region Supporting Classes

/// <summary>
/// Hata context bilgisi;
/// </summary>
public class ErrorContext
{
    public string Component { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public Dictionary<string, object> AdditionalData { get; set; } = new();
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public string RequestPath { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>
/// Hata raporu;
/// </summary>
public class ErrorReport
{
    public string ReportId { get; set; } = string.Empty;
    public int ErrorCode { get; set; }
    public string ExceptionType { get; set; } = string.Empty;
    public string ExceptionMessage { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
    public string? InnerException { get; set; }
    public ErrorCodes.ErrorSeverity Severity { get; set; }
    public DateTime Timestamp { get; set; }
    public ErrorContext Context { get; set; } = new();
    public ErrorInformation ErrorInformation { get; set; } = new();
    public SystemState SystemState { get; set; } = new();
    public string UserId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Component { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public ReportStatus Status { get; set; } = ReportStatus.New;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNotes { get; set; }
    public string? AssignedTo { get; set; }
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Detaylı hata bilgisi;
/// </summary>
public class ErrorInformation
{
    public string ExceptionType { get; set; } = string.Empty;
    public string ExceptionMessage { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? HelpLink { get; set; }
    public int HResult { get; set; }
    public Dictionary<string, string> Data { get; set; } = new();
    public InnerExceptionInfo? InnerException { get; set; }
    public DateTime Timestamp { get; set; }
    public Dictionary<string, object> DiagnosticInfo { get; set; } = new();
    public Dictionary<string, PerformanceCounterValue> PerformanceCounters { get; set; } = new();
    public MemoryInfo? MemoryDumpInfo { get; set; }
}

public class InnerExceptionInfo
{
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
}

public class SystemState
{
    public DateTime Timestamp { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string OSVersion { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public long MemoryUsage { get; set; }
    public long WorkingSet { get; set; }
    public bool Is64BitProcess { get; set; }
    public bool Is64BitOperatingSystem { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserDomainName { get; set; } = string.Empty;
    public string CurrentDirectory { get; set; } = string.Empty;
    public string SystemDirectory { get; set; } = string.Empty;
    public long TickCount { get; set; }
    public ProcessInfo ProcessInfo { get; set; } = new();
    public NetworkInfo NetworkInfo { get; set; } = new();
    public DiskInfo DiskInfo { get; set; } = new();
    public ServiceStatusInfo ServiceStatus { get; set; } = new();
    public double CpuUsage { get; set; }
    public double MemoryUsagePercent { get; set; }
    public DiskUsageInfo DiskUsage { get; set; } = new();
    public NetworkUsageInfo NetworkUsage { get; set; } = new();
}

public class ErrorStatistics
{
    public TimeRange TimeRange { get; set; } = new();
    public int TotalErrors { get; set; }
    public Dictionary<ErrorCodes.ErrorSeverity, int> ErrorBySeverity { get; set; } = new();
    public Dictionary<ErrorCodes.ErrorCategory, int> ErrorByCategory { get; set; } = new();
    public Dictionary<string, int> ErrorByComponent { get; set; } = new();
    public Dictionary<int, int> TopErrorCodes { get; set; } = new();
    public TimeSpan MeanTimeBetweenErrors { get; set; }
    public double ErrorResolutionRate { get; set; }
    public TrendDirection RecentTrend { get; set; }
    public List<ErrorRate> CurrentErrorRates { get; set; } = new();
}

public class ErrorRate
{
    public int ErrorCode { get; set; }
    public int CountLastHour { get; set; }
    public int CountLastDay { get; set; }
    public double RatePerMinute { get; set; }
}

public class ErrorGroup
{
    public string GroupId { get; set; } = Guid.NewGuid().ToString();
    public string PatternHash { get; set; } = string.Empty;
    public List<int> ErrorCodes { get; set; } = new();
    public List<string> Components { get; set; } = new();
    public ErrorCodes.ErrorSeverity Severity { get; set; }
    public int ErrorCount { get; set; }
    public DateTime FirstOccurrence { get; set; }
    public DateTime LastOccurrence { get; set; }
    public string CommonStackTrace { get; set; } = string.Empty;
    public string CommonMessagePattern { get; set; } = string.Empty;
    public RootCauseAnalysis RootCauseAnalysis { get; set; } = new();
    public List<RecommendedAction> RecommendedActions { get; set; } = new();
    public GroupTrend Trend { get; set; } = new();
}

public class ErrorTrendAnalysis
{
    public TimeRange TimeRange { get; set; } = new();
    public TrendDirection OverallTrend { get; set; }
    public List<ErrorCategoryTrend> CategoryTrends { get; set; } = new();
    public List<ComponentTrend> ComponentTrends { get; set; } = new();
    public List<PeakHour> PeakHours { get; set; } = new();
    public CorrelationResult CorrelationWithSystemLoad { get; set; } = new();
    public List<SeasonalityPattern> SeasonalityPatterns { get; set; } = new();
    public ErrorPrediction Prediction { get; set; } = new();
    public List<TrendAnomaly> Anomalies { get; set; } = new();
    public List<TrendRecommendation> Recommendations { get; set; } = new();
}

public class ErrorResolution
{
    public string ResolutionId { get; set; } = Guid.NewGuid().ToString();
    public ResolutionType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public float Confidence { get; set; }
    public int Priority { get; set; }
    public string Source { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class LiveErrorMonitoringStatus
{
    public bool IsActive { get; set; }
    public DateTime LastUpdate { get; set; }
    public int TotalReportsProcessed { get; set; }
    public int ReportsInLastHour { get; set; }
    public double CurrentErrorRate { get; set; }
    public int ActiveAlerts { get; set; }
    public SystemHealth SystemHealth { get; set; } = new();
    public List<ErrorCounterStatus> ErrorRateCounters { get; set; } = new();
    public NotificationsStatus? NotificationsStatus { get; set; }
}

public class ErrorPattern
{
    public string PatternId { get; set; } = Guid.NewGuid().ToString();
    public string PatternType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Frequency { get; set; }
    public float Confidence { get; set; }
    public TimeSpan DetectionWindow { get; set; }
    public List<int> AssociatedErrorCodes { get; set; } = new();
    public List<string> AffectedComponents { get; set; } = new();
    public DateTime FirstDetected { get; set; }
    public DateTime LastDetected { get; set; }
    public List<ErrorPatternImpact> ImpactAnalysis { get; set; } = new();
    public List<PreventionStrategy> PreventionStrategies { get; set; } = new();
    public List<MonitoringRecommendation> MonitoringRecommendations { get; set; } = new();
}

public class AlertRecipient
{
    public string Name { get; set; } = string.Empty;
    public string ContactInfo { get; set; } = string.Empty;
    public AlertChannel Channel { get; set; }
    public List<ErrorCodes.ErrorSeverity> Severities { get; set; } = new();
    public List<string> Components { get; set; } = new();
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public bool IsEnabled { get; set; } = true;
}

public class AlertMessage
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public AlertPriority Priority { get; set; }
    public string Category { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public List<string> Actions { get; set; } = new();
}

public class TimeRange
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    public TimeRange()
    {
        Start = DateTime.UtcNow.AddHours(-24);
        End = DateTime.UtcNow;
    }

    public TimeRange(DateTime start, DateTime end)
    {
        Start = start;
        End = end;
    }
}

public enum ReportStatus
{
    New,
    Analyzing,
    Resolved,
    Archived,
    Ignored
}

public enum ResolutionType
{
    BuiltIn,
    Historical,
    AIGenerated,
    External,
    Manual
}

public enum TrendDirection
{
    Increasing,
    Decreasing,
    Stable,
    Volatile
}

public enum AlertChannel
{
    Email,
    SMS,
    Push,
    Slack,
    Teams,
    Webhook,
    PagerDuty
}

public enum AlertPriority
{
    Low,
    Medium,
    High,
    Critical
}

public enum ArchiveReason
{
    Resolved,
    Duplicate,
    FalsePositive,
    Outdated,
    Manual
}

#endregion

#region Events

public class ErrorReportedEvent : IEvent
{
    public string ReportId { get; set; } = string.Empty;
    public int ErrorCode { get; set; }
    public ErrorCodes.ErrorSeverity Severity { get; set; }
    public DateTime Timestamp { get; set; }
    public string Component { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
}

public class ErrorReportArchivedEvent : IEvent
{
    public string ReportId { get; set; } = string.Empty;
    public DateTime ArchiveTime { get; set; }
    public ArchiveReason Reason { get; set; }
    public string ArchivedBy { get; set; } = string.Empty;
}

public class ErrorRateThresholdExceededEvent : IEvent
{
    public int ErrorCode { get; set; }
    public double CurrentRate { get; set; }
    public double Threshold { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ErrorPatternDetectedEvent : IEvent
{
    public int ErrorCode { get; set; }
    public string Component { get; set; } = string.Empty;
    public int OccurrenceCount { get; set; }
    public TimeSpan TimeWindow { get; set; }
    public DateTime FirstOccurrence { get; set; }
    public DateTime LastOccurrence { get; set; }
}

public class NotificationsPausedEvent : IEvent
{
    public DateTime PausedAt { get; set; }
    public DateTime PausedUntil { get; set; }
    public string Reason { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

public class NotificationsResumedEvent : IEvent
{
    public DateTime ResumedAt { get; set; }
}

#endregion

#region Exceptions

public class ErrorReportingException : NedaException
{
    public ErrorReportingException(int errorCode, string message)
        : base(errorCode, message) { }

    public ErrorReportingException(int errorCode, string message, Exception inner)
        : base(errorCode, message, inner) { }
}

public class ErrorReportingInitializationException : ErrorReportingException
{
    public ErrorReportingInitializationException(int errorCode, string message)
        : base(errorCode, message) { }

    public ErrorReportingInitializationException(int errorCode, string message, Exception inner)
        : base(errorCode, message, inner) { }
}

public class ErrorAnalysisException : ErrorReportingException
{
    public ErrorAnalysisException(int errorCode, string message)
        : base(errorCode, message) { }

    public ErrorAnalysisException(int errorCode, string message, Exception inner)
        : base(errorCode, message, inner) { }
}

public class AlertDeliveryException : ErrorReportingException
{
    public AlertDeliveryException(int errorCode, string message)
        : base(errorCode, message) { }

    public AlertDeliveryException(int errorCode, string message, Exception inner)
        : base(errorCode, message, inner) { }
}

#endregion
