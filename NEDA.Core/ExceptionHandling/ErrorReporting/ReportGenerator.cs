// Remove this define when integrating into your real NEDA project to avoid type duplication.
#define NEDA_STANDALONE

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Monitoring.Reporting
{
    #region Domain Models

    /// <summary>
    /// Represents a generated report with metadata and content.
    /// </summary>
    public class GeneratedReport
    {
        public string ReportId { get; set; }
        public ReportType ReportType { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTime GeneratedAt { get; set; }
        public TimeSpan GenerationDuration { get; set; }
        public ReportTimeRange TimeRange { get; set; }
        public ReportFormat Format { get; set; }
        public byte[] Content { get; set; }
        public string ContentType { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<ReportSection> Sections { get; set; } = new();
        public ReportStatus Status { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Represents a section within a report.
    /// </summary>
    public class ReportSection
    {
        public string SectionId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public SectionType Type { get; set; }
        public object Data { get; set; }
        public List<ReportChart> Charts { get; set; } = new();
        public List<ReportTable> Tables { get; set; } = new();
        public List<ReportMetric> Metrics { get; set; } = new();
        public Dictionary<string, object> SectionMetadata { get; set; } = new();
    }

    /// <summary>
    /// Represents a chart in a report.
    /// </summary>
    public class ReportChart
    {
        public string ChartId { get; set; }
        public string Title { get; set; }
        public ChartType Type { get; set; }
        public ChartData Data { get; set; }
        public ChartOptions Options { get; set; }
        public string Description { get; set; }
        public byte[] ImageData { get; set; } // For image-based export
        public string ImageFormat { get; set; }
    }

    /// <summary>
    /// Represents a table in a report.
    /// </summary>
    public class ReportTable
    {
        public string TableId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public List<TableColumn> Columns { get; set; } = new();
        public List<Dictionary<string, object>> Rows { get; set; } = new();
        public TableOptions Options { get; set; }
        public List<TableSummary> Summaries { get; set; } = new();
    }

    /// <summary>
    /// Represents a metric in a report.
    /// </summary>
    public class ReportMetric
    {
        public string MetricId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public MetricValue CurrentValue { get; set; }
        public MetricValue PreviousValue { get; set; }
        public MetricValue BaselineValue { get; set; }
        public double ChangePercentage { get; set; }
        public MetricTrend Trend { get; set; }
        public string Unit { get; set; }
        public MetricStatus Status { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Report generation request.
    /// </summary>
    public class ReportRequest
    {
        public string RequestId { get; set; }
        public ReportType ReportType { get; set; }
        public ReportTimeRange TimeRange { get; set; }
        public ReportFormat Format { get; set; } = ReportFormat.JSON;
        public List<string> Sections { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
        public ReportPriority Priority { get; set; } = ReportPriority.Normal;
        public bool IncludeCharts { get; set; } = true;
        public bool IncludeRawData { get; set; }
        public bool IncludeExecutiveSummary { get; set; } = true;
        public string CustomTemplate { get; set; }
        public string RecipientEmail { get; set; }
        public bool ScheduleRecurring { get; set; }
        public TimeSpan? Timeout { get; set; }
    }

    /// <summary>
    /// Report template configuration.
    /// </summary>
    public class ReportTemplate
    {
        public string TemplateId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public ReportType ReportType { get; set; }
        public List<TemplateSection> Sections { get; set; } = new();
        public TemplateStyle Style { get; set; }
        public Dictionary<string, object> DefaultParameters { get; set; } = new();
        public bool IsSystemTemplate { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastModified { get; set; }
    }

    /// <summary>
    /// Chart data structure.
    /// </summary>
    public class ChartData
    {
        public List<string> Labels { get; set; } = new();
        public List<ChartDataset> Datasets { get; set; } = new();
    }

    /// <summary>
    /// Chart dataset.
    /// </summary>
    public class ChartDataset
    {
        public string Label { get; set; }
        public List<double> Data { get; set; } = new();
        public string BackgroundColor { get; set; }
        public string BorderColor { get; set; }
        public double BorderWidth { get; set; } = 1;
        public bool Fill { get; set; }
        public string Type { get; set; }
        public double Tension { get; set; }
        public double PointRadius { get; set; } = 2;
    }

    /// <summary>
    /// Report generation configuration.
    /// </summary>
    public class ReportGenerationConfig
    {
        public string OutputDirectory { get; set; }
        public int MaxConcurrentGenerations { get; set; }
        public TimeSpan DefaultTimeout { get; set; }
        public long MaxReportSize { get; set; }
        public bool EnableCompression { get; set; }
        public bool EnableEncryption { get; set; }
        public List<ReportType> AvailableReportTypes { get; set; } = new();
        public Dictionary<string, string> FormatSettings { get; set; } = new();
    }

    public enum ReportType
    {
        SystemHealth,
        PerformanceMetrics,
        SecurityAudit,
        UsageAnalytics,
        ErrorAnalysis,
        CapacityPlanning,
        CostAnalysis,
        ComplianceReport,
        ExecutiveSummary,
        Custom
    }

    public enum ReportFormat
    {
        PDF,
        HTML,
        Excel,
        CSV,
        JSON,
        XML,
        Word,
        PowerPoint,
        Markdown,
        Text
    }

    public enum ReportStatus
    {
        Pending,
        Generating,
        Completed,
        Failed,
        Cancelled,
        Scheduled
    }

    public enum SectionType
    {
        ExecutiveSummary,
        Metrics,
        PerformanceMetrics,
        Charts,
        Tables,
        Recommendations,
        RawData,
        Analysis,
        Trends,
        Alerts
    }

    public enum ChartType
    {
        Line,
        Bar,
        Pie,
        Doughnut,
        Area,
        Scatter,
        Bubble,
        Radar,
        Heatmap,
        Gauge
    }

    public enum MetricTrend
    {
        Improving,
        Stable,
        Declining,
        Volatile,
        Unknown
    }

    public enum MetricStatus
    {
        Normal,
        Warning,
        Critical,
        Information,
        Maintenance
    }

    public enum ReportPriority
    {
        High,
        Normal,
        Low,
        Background
    }

    /// <summary>
    /// Time range for report data.
    /// </summary>
    public class ReportTimeRange
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeGranularity Granularity { get; set; }

        public TimeSpan Duration => EndTime - StartTime;

        public static ReportTimeRange Last24Hours => new ReportTimeRange
        {
            StartTime = DateTime.UtcNow.AddHours(-24),
            EndTime = DateTime.UtcNow,
            Granularity = TimeGranularity.Hourly
        };

        public static ReportTimeRange Last7Days => new ReportTimeRange
        {
            StartTime = DateTime.UtcNow.AddDays(-7),
            EndTime = DateTime.UtcNow,
            Granularity = TimeGranularity.Daily
        };

        public static ReportTimeRange Last30Days => new ReportTimeRange
        {
            StartTime = DateTime.UtcNow.AddDays(-30),
            EndTime = DateTime.UtcNow,
            Granularity = TimeGranularity.Daily
        };
    }

    public enum TimeGranularity
    {
        Minute,
        Hourly,
        Daily,
        Weekly,
        Monthly,
        Quarterly,
        Yearly
    }

    #endregion

    #region Interfaces

    public interface IReportGenerator
    {
        Task<NEDA.Core.Common.Result<GeneratedReport>> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken = default);
        Task<NEDA.Core.Common.Result<GeneratedReport>> GetReportAsync(string reportId, CancellationToken cancellationToken = default);
        Task<NEDA.Core.Common.Result<IEnumerable<GeneratedReport>>> GetReportsByTypeAsync(ReportType reportType, DateTime? fromDate = null, CancellationToken cancellationToken = default);
        Task<NEDA.Core.Common.Result<bool>> ScheduleReportAsync(ReportRequest request, Schedule schedule, CancellationToken cancellationToken = default);
        Task<NEDA.Core.Common.Result<bool>> CancelReportGenerationAsync(string reportId, CancellationToken cancellationToken = default);
        Task<NEDA.Core.Common.Result<IEnumerable<ReportTemplate>>> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default);
        Task<NEDA.Core.Common.Result<ReportTemplate>> CreateTemplateAsync(ReportTemplate template, CancellationToken cancellationToken = default);
        Task<NEDA.Core.Common.Result<ReportStatistics>> GetGenerationStatisticsAsync(CancellationToken cancellationToken = default);
    }

    public interface IReportGeneratorStrategy
    {
        Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken);
    }

    public interface IReportStorage
    {
        Task<NEDA.Core.Common.Result<bool>> StoreReportAsync(GeneratedReport report, CancellationToken cancellationToken);
        Task<NEDA.Core.Common.Result<GeneratedReport>> GetReportAsync(string reportId, CancellationToken cancellationToken);
        Task<IEnumerable<GeneratedReport>> GetReportsByTypeAsync(ReportType reportType, DateTime? fromDate, CancellationToken cancellationToken);
        Task<IEnumerable<GeneratedReport>> GetReportsSinceAsync(DateTime fromDate, CancellationToken cancellationToken);
        Task<int> GetTotalReportCountAsync(CancellationToken cancellationToken);
        Task<int> GetReportCountSinceAsync(DateTime fromDate, CancellationToken cancellationToken);
        Task<IEnumerable<ReportTemplate>> GetTemplatesAsync(CancellationToken cancellationToken);
        Task SaveTemplateAsync(ReportTemplate template, CancellationToken cancellationToken);
    }

    public interface IReportScheduler
    {
        Task<bool> ScheduleReportAsync(ReportRequest request, Schedule schedule, CancellationToken cancellationToken);
        Task<bool> UnscheduleReportAsync(string scheduleId, CancellationToken cancellationToken);
        Task<IEnumerable<ScheduledReport>> GetScheduledReportsAsync(CancellationToken cancellationToken);
    }

    public interface IChartBuilder
    {
        Task<ReportChart> CreateChartAsync(ChartType type, ChartData data, ChartOptions options, CancellationToken cancellationToken);
        Task<byte[]> RenderChartToImageAsync(ReportChart chart, string format, CancellationToken cancellationToken);
    }

    public interface IExportService
    {
        Task<FormattedReport> ExportToPdfAsync(GeneratedReport report, CancellationToken cancellationToken);
        Task<FormattedReport> ExportToHtmlAsync(GeneratedReport report, CancellationToken cancellationToken);
        Task<FormattedReport> ExportToExcelAsync(GeneratedReport report, CancellationToken cancellationToken);
        Task<FormattedReport> ExportToCsvAsync(GeneratedReport report, CancellationToken cancellationToken);
        bool SupportsFormat(ExportFormat format);
    }

    public enum ExportFormat
    {
        PDF,
        HTML,
        Excel,
        CSV,
        Word,
        PowerPoint
    }

    #endregion

    #region Supporting Models

    public class ReportGenerationTask
    {
        public string TaskId { get; set; }
        public string ReportId { get; set; }
        public ReportRequest Request { get; set; }
        public ReportStatus Status { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Error { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }
    }

    public class FormattedReport
    {
        public byte[] Content { get; set; }
        public string ContentType { get; set; }
        public string FileExtension { get; set; }
    }

    public class ReportStatistics
    {
        public int TotalReportsGenerated { get; set; }
        public int ActiveGenerations { get; set; }
        public int AvailableSlots { get; set; }
        public int Last24HoursCount { get; set; }
        public TimeSpan AverageGenerationTime { get; set; }
        public ReportType MostPopularReportType { get; set; }
    }

    public class TemplateSection
    {
        public string SectionId { get; set; }
        public string Title { get; set; }
        public SectionType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class TemplateStyle
    {
        public string Theme { get; set; }
        public string FontFamily { get; set; }
        public string PrimaryColor { get; set; }
        public string SecondaryColor { get; set; }
        public bool IncludeLogo { get; set; }
        public string LogoPath { get; set; }
    }

    public class ChartOptions
    {
        public bool ShowLegend { get; set; } = true;
        public bool ShowGridLines { get; set; } = true;
        public string XAxisTitle { get; set; }
        public string YAxisTitle { get; set; }
        public ChartScales Scales { get; set; }
        public AnimationOptions Animation { get; set; }
    }

    public class ChartScales
    {
        public AxisOptions X { get; set; }
        public AxisOptions Y { get; set; }
    }

    public class AxisOptions
    {
        public string Type { get; set; }
        public bool BeginAtZero { get; set; } = true;
        public GridLineOptions GridLines { get; set; }
    }

    public class GridLineOptions
    {
        public bool Display { get; set; } = true;
        public string Color { get; set; }
    }

    public class AnimationOptions
    {
        public bool Enabled { get; set; } = true;
        public int Duration { get; set; } = 300;
        public string Easing { get; set; } = "easeOutQuad";
    }

    public class TableColumn
    {
        public string Id { get; set; }
        public string Header { get; set; }
        public string DataType { get; set; }
        public string Format { get; set; }
        public int Width { get; set; }
        public bool Sortable { get; set; } = true;
        public bool Filterable { get; set; } = true;
    }

    public class TableOptions
    {
        public bool ShowHeader { get; set; } = true;
        public bool StripedRows { get; set; } = true;
        public bool Bordered { get; set; } = true;
        public bool Hoverable { get; set; } = true;
        public int PageSize { get; set; } = 50;
        public bool ShowPagination { get; set; } = true;
    }

    public class TableSummary
    {
        public string ColumnId { get; set; }
        public string Operation { get; set; }
        public object Value { get; set; }
        public string Label { get; set; }
    }

    public class MetricValue
    {
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public string Unit { get; set; }
    }

    public class Schedule
    {
        public ScheduleType Type { get; set; }
        public int Interval { get; set; } = 1;
        public TimeSpan TimeOfDay { get; set; }
        public DayOfWeek DayOfWeek { get; set; }
        public int DayOfMonth { get; set; }
        public DateTime StartDate { get; set; } = DateTime.UtcNow;
        public DateTime? EndDate { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public enum ScheduleType
    {
        Daily,
        Weekly,
        Monthly,
        Quarterly,
        Yearly,
        Custom
    }

    public class ScheduledReport
    {
        public string ScheduleId { get; set; }
        public ReportRequest Request { get; set; }
        public Schedule Schedule { get; set; }
        public DateTime NextRunTime { get; set; }
        public DateTime? LastRunTime { get; set; }
        public bool IsEnabled { get; set; } = true;
    }

    #endregion

    #region ReportGenerator Implementation

    /// <summary>
    /// Advanced report generator with multi-format support.
    /// </summary>
    public class ReportGenerator : IReportGenerator
    {
        private readonly ILogger<ReportGenerator> _logger;
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;

        private readonly NEDA.Monitoring.Diagnostics.IPerformanceMonitor _performanceMonitor;
        private readonly NEDA.Monitoring.MetricsCollector.IMetricsEngine _metricsEngine;
        private readonly NEDA.Monitoring.Diagnostics.IHealthChecker _healthChecker;
        private readonly IChartBuilder _chartBuilder;
        private readonly IExportService _exportService;

        private readonly ReportGenerationConfig _generationConfig;
        private readonly SemaphoreSlim _generationSemaphore;
        private readonly Dictionary<string, ReportGenerationTask> _activeGenerations;
        private readonly Dictionary<ReportType, IReportGeneratorStrategy> _generationStrategies;
        private readonly IReportStorage _reportStorage;
        private readonly IReportScheduler _reportScheduler;

        private const int DefaultMaxConcurrentGenerations = 5;
        private const long DefaultMaxReportSize = 100 * 1024 * 1024; // 100 MB

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public ReportGenerator(
            ILogger<ReportGenerator> logger,
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            NEDA.Monitoring.Diagnostics.IPerformanceMonitor performanceMonitor,
            NEDA.Monitoring.MetricsCollector.IMetricsEngine metricsEngine,
            NEDA.Monitoring.Diagnostics.IHealthChecker healthChecker,
            IChartBuilder chartBuilder,
            IExportService exportService,
            IReportStorage reportStorage,
            IReportScheduler reportScheduler)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _metricsEngine = metricsEngine ?? throw new ArgumentNullException(nameof(metricsEngine));
            _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
            _chartBuilder = chartBuilder ?? throw new ArgumentNullException(nameof(chartBuilder));
            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
            _reportStorage = reportStorage ?? throw new ArgumentNullException(nameof(reportStorage));
            _reportScheduler = reportScheduler ?? throw new ArgumentNullException(nameof(reportScheduler));

            _generationConfig = LoadConfiguration();
            _generationSemaphore = new SemaphoreSlim(_generationConfig.MaxConcurrentGenerations, _generationConfig.MaxConcurrentGenerations);
            _activeGenerations = new Dictionary<string, ReportGenerationTask>();
            _generationStrategies = InitializeGenerationStrategies();

            _logger.LogInformation("ReportGenerator initialized with {StrategyCount} strategies", _generationStrategies.Count);
        }

        public async Task<NEDA.Core.Common.Result<GeneratedReport>> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                return NEDA.Core.Common.Result<GeneratedReport>.Failure("Report request cannot be null");

            request.RequestId ??= Guid.NewGuid().ToString("N");

            var reportId = $"REP_{Guid.NewGuid():N}".Substring(0, 16);

            _logger.LogInformation("Starting report generation: {ReportId}, Type: {ReportType}", reportId, request.ReportType);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ReportGenerationTask generationTask = null;
            var semaphoreAcquired = false;

            try
            {
                var validationResult = ValidateReportRequest(request);
                if (validationResult.IsFailure)
                    return NEDA.Core.Common.Result<GeneratedReport>.Failure($"Request validation failed: {validationResult.Error}");

                // Try acquire slot
                semaphoreAcquired = await _generationSemaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                if (!semaphoreAcquired)
                {
                    if (request.Priority == ReportPriority.High)
                    {
                        await _generationSemaphore.WaitAsync(request.Timeout ?? _generationConfig.DefaultTimeout, cancellationToken).ConfigureAwait(false);
                        semaphoreAcquired = true;
                    }
                    else
                    {
                        return NEDA.Core.Common.Result<GeneratedReport>.Failure("Report generation queue is full. Please try again later or schedule the report.");
                    }
                }

                generationTask = new ReportGenerationTask
                {
                    TaskId = Guid.NewGuid().ToString("N"),
                    ReportId = reportId,
                    Request = request,
                    Status = ReportStatus.Generating,
                    StartedAt = DateTime.UtcNow,
                    CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                };

                lock (_activeGenerations)
                {
                    _activeGenerations[reportId] = generationTask;
                }

                var report = await ExecuteReportGenerationAsync(generationTask, cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                report.GenerationDuration = stopwatch.Elapsed;

                var storageResult = await _reportStorage.StoreReportAsync(report, cancellationToken).ConfigureAwait(false);
                if (storageResult.IsFailure)
                    _logger.LogWarning("Failed to store report {ReportId}: {Error}", reportId, storageResult.Error);

                _logger.LogInformation("Report generation completed: {ReportId}, Duration: {Duration}", reportId, stopwatch.Elapsed);
                return NEDA.Core.Common.Result<GeneratedReport>.Success(report);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Report generation cancelled: {ReportId}", reportId);
                if (generationTask != null)
                {
                    generationTask.Status = ReportStatus.Cancelled;
                    generationTask.CompletedAt = DateTime.UtcNow;
                }
                return NEDA.Core.Common.Result<GeneratedReport>.Failure("Report generation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during report generation: {ReportId}", reportId);
                if (generationTask != null)
                {
                    generationTask.Status = ReportStatus.Failed;
                    generationTask.Error = ex.Message;
                    generationTask.CompletedAt = DateTime.UtcNow;
                }
                return NEDA.Core.Common.Result<GeneratedReport>.Failure($"Report generation failed: {ex.Message}");
            }
            finally
            {
                if (generationTask != null)
                {
                    generationTask.CompletedAt = DateTime.UtcNow;
                    lock (_activeGenerations)
                    {
                        _activeGenerations.Remove(reportId);
                    }
                }

                if (semaphoreAcquired)
                    _generationSemaphore.Release();

                stopwatch.Stop();
            }
        }

        public async Task<NEDA.Core.Common.Result<GeneratedReport>> GetReportAsync(string reportId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(reportId))
                return NEDA.Core.Common.Result<GeneratedReport>.Failure("Report ID cannot be empty");

            try
            {
                lock (_activeGenerations)
                {
                    if (_activeGenerations.TryGetValue(reportId, out var generationTask))
                    {
                        return NEDA.Core.Common.Result<GeneratedReport>.Success(new GeneratedReport
                        {
                            ReportId = reportId,
                            Status = generationTask.Status,
                            GeneratedAt = generationTask.StartedAt,
                            Title = $"Generating: {generationTask.Request.ReportType}",
                            Description = "Report is currently being generated"
                        });
                    }
                }

                var storageResult = await _reportStorage.GetReportAsync(reportId, cancellationToken).ConfigureAwait(false);
                if (storageResult.IsFailure)
                    return NEDA.Core.Common.Result<GeneratedReport>.Failure($"Report not found: {reportId}");

                return NEDA.Core.Common.Result<GeneratedReport>.Success(storageResult.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving report: {ReportId}", reportId);
                return NEDA.Core.Common.Result<GeneratedReport>.Failure($"Failed to retrieve report: {ex.Message}");
            }
        }

        public async Task<NEDA.Core.Common.Result<IEnumerable<GeneratedReport>>> GetReportsByTypeAsync(ReportType reportType, DateTime? fromDate = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _reportStorage.GetReportsByTypeAsync(reportType, fromDate, cancellationToken).ConfigureAwait(false);
                return NEDA.Core.Common.Result<IEnumerable<GeneratedReport>>.Success(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving reports by type: {ReportType}", reportType);
                return NEDA.Core.Common.Result<IEnumerable<GeneratedReport>>.Failure($"Failed to retrieve reports: {ex.Message}");
            }
        }

        public async Task<NEDA.Core.Common.Result<bool>> ScheduleReportAsync(ReportRequest request, Schedule schedule, CancellationToken cancellationToken = default)
        {
            if (request == null)
                return NEDA.Core.Common.Result<bool>.Failure("Report request cannot be null");

            if (schedule == null)
                return NEDA.Core.Common.Result<bool>.Failure("Schedule cannot be null");

            try
            {
                var ok = await _reportScheduler.ScheduleReportAsync(request, schedule, cancellationToken).ConfigureAwait(false);
                return NEDA.Core.Common.Result<bool>.Success(ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling report: {RequestId}", request.RequestId);
                return NEDA.Core.Common.Result<bool>.Failure($"Failed to schedule report: {ex.Message}");
            }
        }

        public async Task<NEDA.Core.Common.Result<bool>> CancelReportGenerationAsync(string reportId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(reportId))
                return NEDA.Core.Common.Result<bool>.Failure("Report ID cannot be empty");

            try
            {
                ReportGenerationTask generationTask;
                lock (_activeGenerations)
                {
                    if (!_activeGenerations.TryGetValue(reportId, out generationTask))
                        return NEDA.Core.Common.Result<bool>.Failure($"No active generation found for report: {reportId}");
                }

                generationTask.CancellationTokenSource?.Cancel();
                generationTask.Status = ReportStatus.Cancelled;
                generationTask.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation("Report generation cancelled: {ReportId}", reportId);
                return NEDA.Core.Common.Result<bool>.Success(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling report generation: {ReportId}", reportId);
                return NEDA.Core.Common.Result<bool>.Failure($"Failed to cancel report generation: {ex.Message}");
            }
        }

        public async Task<NEDA.Core.Common.Result<IEnumerable<ReportTemplate>>> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var templates = await LoadReportTemplatesAsync(cancellationToken).ConfigureAwait(false);
                return NEDA.Core.Common.Result<IEnumerable<ReportTemplate>>.Success(templates);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading report templates");
                return NEDA.Core.Common.Result<IEnumerable<ReportTemplate>>.Failure($"Failed to load templates: {ex.Message}");
            }
        }

        public async Task<NEDA.Core.Common.Result<ReportTemplate>> CreateTemplateAsync(ReportTemplate template, CancellationToken cancellationToken = default)
        {
            if (template == null)
                return NEDA.Core.Common.Result<ReportTemplate>.Failure("Template cannot be null");

            try
            {
                template.TemplateId = $"TMP_{Guid.NewGuid():N}".Substring(0, 12);
                template.CreatedAt = DateTime.UtcNow;

                var validationResult = ValidateTemplate(template);
                if (validationResult.IsFailure)
                    return NEDA.Core.Common.Result<ReportTemplate>.Failure($"Template validation failed: {validationResult.Error}");

                await _reportStorage.SaveTemplateAsync(template, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Template created: {TemplateId}", template.TemplateId);

                return NEDA.Core.Common.Result<ReportTemplate>.Success(template);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating template");
                return NEDA.Core.Common.Result<ReportTemplate>.Failure($"Failed to create template: {ex.Message}");
            }
        }

        public async Task<NEDA.Core.Common.Result<ReportStatistics>> GetGenerationStatisticsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var statistics = new ReportStatistics
                {
                    TotalReportsGenerated = await _reportStorage.GetTotalReportCountAsync(cancellationToken).ConfigureAwait(false),
                    ActiveGenerations = _activeGenerations.Count,
                    AvailableSlots = _generationSemaphore.CurrentCount,
                    Last24HoursCount = await _reportStorage.GetReportCountSinceAsync(DateTime.UtcNow.AddHours(-24), cancellationToken).ConfigureAwait(false),
                    AverageGenerationTime = await CalculateAverageGenerationTimeAsync(cancellationToken).ConfigureAwait(false),
                    MostPopularReportType = await GetMostPopularReportTypeAsync(cancellationToken).ConfigureAwait(false)
                };

                return NEDA.Core.Common.Result<ReportStatistics>.Success(statistics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting generation statistics");
                return NEDA.Core.Common.Result<ReportStatistics>.Failure($"Failed to get statistics: {ex.Message}");
            }
        }

        #region Private Methods

        private ReportGenerationConfig LoadConfiguration()
        {
            var cfg = new ReportGenerationConfig
            {
                OutputDirectory = _configuration.GetValue(
                    "Monitoring:Reporting:OutputDirectory",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports")),
                MaxConcurrentGenerations = _configuration.GetValue(
                    "Monitoring:Reporting:MaxConcurrentGenerations",
                    DefaultMaxConcurrentGenerations),
                DefaultTimeout = TimeSpan.FromSeconds(
                    _configuration.GetValue("Monitoring:Reporting:DefaultTimeoutSeconds", 300)),
                MaxReportSize = _configuration.GetValue(
                    "Monitoring:Reporting:MaxReportSizeBytes",
                    DefaultMaxReportSize),
                EnableCompression = _configuration.GetValue(
                    "Monitoring:Reporting:EnableCompression",
                    true),
                EnableEncryption = _configuration.GetValue(
                    "Monitoring:Reporting:EnableEncryption",
                    false),
                AvailableReportTypes = _configuration.GetSection("Monitoring:Reporting:AvailableReportTypes")
                    .Get<List<ReportType>>() ?? new List<ReportType>
                    {
                        ReportType.SystemHealth,
                        ReportType.PerformanceMetrics,
                        ReportType.SecurityAudit,
                        ReportType.UsageAnalytics
                    },
                FormatSettings = _configuration.GetSection("Monitoring:Reporting:FormatSettings")
                    .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>()
            };

            Directory.CreateDirectory(cfg.OutputDirectory);
            return cfg;
        }

        private Dictionary<ReportType, IReportGeneratorStrategy> InitializeGenerationStrategies()
        {
            return new Dictionary<ReportType, IReportGeneratorStrategy>
            {
                [ReportType.SystemHealth] = new SystemHealthReportStrategy(
                    _serviceProvider.GetRequiredService<ILogger<SystemHealthReportStrategy>>(),
                    _healthChecker,
                    _metricsEngine,
                    _chartBuilder),

                [ReportType.PerformanceMetrics] = new PerformanceMetricsReportStrategy(
                    _serviceProvider.GetRequiredService<ILogger<PerformanceMetricsReportStrategy>>(),
                    _performanceMonitor,
                    _metricsEngine,
                    _chartBuilder),

                [ReportType.SecurityAudit] = new SecurityAuditReportStrategy(
                    _serviceProvider.GetRequiredService<ILogger<SecurityAuditReportStrategy>>()),

                [ReportType.UsageAnalytics] = new UsageAnalyticsReportStrategy(
                    _serviceProvider.GetRequiredService<ILogger<UsageAnalyticsReportStrategy>>(),
                    _metricsEngine),

                [ReportType.ErrorAnalysis] = new ErrorAnalysisReportStrategy(
                    _serviceProvider.GetRequiredService<ILogger<ErrorAnalysisReportStrategy>>()),

                [ReportType.CapacityPlanning] = new CapacityPlanningReportStrategy(
                    _serviceProvider.GetRequiredService<ILogger<CapacityPlanningReportStrategy>>(),
                    _performanceMonitor),

                [ReportType.ExecutiveSummary] = new ExecutiveSummaryReportStrategy(
                    _serviceProvider.GetRequiredService<ILogger<ExecutiveSummaryReportStrategy>>(),
                    _serviceProvider)
            };
        }

        private NEDA.Core.Common.Result<bool> ValidateReportRequest(ReportRequest request)
        {
            var errors = new List<string>();

            request.RequestId ??= Guid.NewGuid().ToString("N");
            request.TimeRange ??= ReportTimeRange.Last24Hours;

            if (request.TimeRange.StartTime >= request.TimeRange.EndTime)
                errors.Add("Start time must be before end time");

            if (request.TimeRange.Duration > TimeSpan.FromDays(365))
                errors.Add("Time range cannot exceed 1 year");

            if (request.Timeout.HasValue && request.Timeout.Value > TimeSpan.FromHours(1))
                errors.Add("Timeout cannot exceed 1 hour");

            if (!_generationConfig.AvailableReportTypes.Contains(request.ReportType))
                errors.Add($"Report type {request.ReportType} is not available");

            if (request.Format == ReportFormat.PDF && !_exportService.SupportsFormat(ExportFormat.PDF))
                errors.Add("PDF export is not supported");

            return errors.Any()
                ? NEDA.Core.Common.Result<bool>.Failure(string.Join("; ", errors))
                : NEDA.Core.Common.Result<bool>.Success(true);
        }

        private async Task<GeneratedReport> ExecuteReportGenerationAsync(ReportGenerationTask generationTask, CancellationToken cancellationToken)
        {
            var request = generationTask.Request;
            var reportId = generationTask.ReportId;

            _logger.LogDebug("Executing report generation: {ReportId}, Strategy selection for {ReportType}", reportId, request.ReportType);

            if (!_generationStrategies.TryGetValue(request.ReportType, out var strategy))
                throw new InvalidOperationException($"No strategy found for report type: {request.ReportType}");

            // Strategy generates semantic content (sections, metrics, etc.)
            var reportContent = await strategy.GenerateReportAsync(request, cancellationToken).ConfigureAwait(false);
            reportContent.Sections ??= new List<ReportSection>();

            // Ensure top-level metadata
            reportContent.ReportId = reportId;
            reportContent.ReportType = request.ReportType;
            reportContent.GeneratedAt = DateTime.UtcNow;
            reportContent.TimeRange = request.TimeRange;
            reportContent.Format = request.Format;
            reportContent.Title = GenerateReportTitle(request.ReportType, request.TimeRange);
            reportContent.Description = GenerateReportDescription(request.ReportType, request.TimeRange);

            // Format/export to chosen output
            var formatted = await FormatReportAsync(reportContent, request.Format, cancellationToken).ConfigureAwait(false);

            reportContent.Content = formatted.Content;
            reportContent.ContentType = formatted.ContentType;
            reportContent.FileName = GenerateFileName(request.ReportType, request.Format);
            reportContent.FileSize = formatted.Content?.Length ?? 0;
            reportContent.Status = ReportStatus.Completed;

            reportContent.Metadata ??= new Dictionary<string, object>();
            reportContent.Metadata["RequestId"] = request.RequestId;
            reportContent.Metadata["Strategy"] = strategy.GetType().Name;
            reportContent.Metadata["SectionsIncluded"] = request.Sections?.Count ?? 0;
            reportContent.Metadata["IncludeCharts"] = request.IncludeCharts;
            reportContent.Metadata["IncludeRawData"] = request.IncludeRawData;
            reportContent.Metadata["Priority"] = request.Priority.ToString();

            if (reportContent.FileSize > _generationConfig.MaxReportSize)
            {
                _logger.LogWarning("Report size {FileSize} exceeds limit {MaxSize}", reportContent.FileSize, _generationConfig.MaxReportSize);
                reportContent.Metadata["SizeWarning"] = true;
                reportContent.Metadata["OriginalSize"] = reportContent.FileSize;

                if (_generationConfig.EnableCompression)
                    reportContent = await CompressReportAsync(reportContent, cancellationToken).ConfigureAwait(false);
            }

            return reportContent;
        }

        private async Task<FormattedReport> FormatReportAsync(GeneratedReport report, ReportFormat format, CancellationToken cancellationToken)
        {
            try
            {
                switch (format)
                {
                    case ReportFormat.PDF:
                        return await _exportService.ExportToPdfAsync(report, cancellationToken).ConfigureAwait(false);

                    case ReportFormat.HTML:
                        return await _exportService.ExportToHtmlAsync(report, cancellationToken).ConfigureAwait(false);

                    case ReportFormat.Excel:
                        return await _exportService.ExportToExcelAsync(report, cancellationToken).ConfigureAwait(false);

                    case ReportFormat.CSV:
                        return await _exportService.ExportToCsvAsync(report, cancellationToken).ConfigureAwait(false);

                    case ReportFormat.JSON:
                        return await ExportToJsonAsync(report, cancellationToken).ConfigureAwait(false);

                    case ReportFormat.XML:
                        return await ExportToXmlAsync(report, cancellationToken).ConfigureAwait(false);

                    case ReportFormat.Markdown:
                        return await ExportToMarkdownAsync(report, cancellationToken).ConfigureAwait(false);

                    case ReportFormat.Text:
                        return await ExportToTextAsync(report, cancellationToken).ConfigureAwait(false);

                    default:
                        return await ExportToJsonAsync(report, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error formatting report to {Format}. Falling back to JSON.", format);
                return await ExportToJsonAsync(report, cancellationToken).ConfigureAwait(false);
            }
        }

        private Task<FormattedReport> ExportToJsonAsync(GeneratedReport report, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(report, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            return Task.FromResult(new FormattedReport
            {
                Content = bytes,
                ContentType = "application/json",
                FileExtension = ".json"
            });
        }

        private Task<FormattedReport> ExportToXmlAsync(GeneratedReport report, CancellationToken cancellationToken)
        {
            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine($"<Report id=\"{EscapeXml(report.ReportId)}\">");
            xml.AppendLine($"  <Title>{EscapeXml(report.Title)}</Title>");
            xml.AppendLine($"  <GeneratedAt>{report.GeneratedAt:o}</GeneratedAt>");
            xml.AppendLine($"  <Type>{EscapeXml(report.ReportType.ToString())}</Type>");
            xml.AppendLine("</Report>");

            var bytes = Encoding.UTF8.GetBytes(xml.ToString());
            return Task.FromResult(new FormattedReport
            {
                Content = bytes,
                ContentType = "application/xml",
                FileExtension = ".xml"
            });
        }

        private Task<FormattedReport> ExportToMarkdownAsync(GeneratedReport report, CancellationToken cancellationToken)
        {
            var markdown = new StringBuilder();

            markdown.AppendLine($"# {report.Title}");
            markdown.AppendLine();
            markdown.AppendLine($"**Generated:** {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
            markdown.AppendLine($"**Time Range:** {report.TimeRange.StartTime:yyyy-MM-dd} to {report.TimeRange.EndTime:yyyy-MM-dd}");
            markdown.AppendLine($"**Report Type:** {report.ReportType}");
            markdown.AppendLine();

            if (report.Sections != null)
            {
                foreach (var section in report.Sections)
                {
                    markdown.AppendLine($"## {section.Title}");
                    markdown.AppendLine();

                    if (!string.IsNullOrEmpty(section.Description))
                    {
                        markdown.AppendLine(section.Description);
                        markdown.AppendLine();
                    }

                    if (section.Metrics != null && section.Metrics.Any())
                    {
                        markdown.AppendLine("### Metrics");
                        markdown.AppendLine();

                        foreach (var metric in section.Metrics)
                        {
                            var current = metric.CurrentValue?.Value ?? 0;
                            var unit = metric.Unit ?? metric.CurrentValue?.Unit ?? "";
                            markdown.AppendLine($"- **{metric.Name}:** {current} {unit} (Trend: {metric.Trend}, Change: {metric.ChangePercentage:+0.##%;-0.##%;0%})");
                        }

                        markdown.AppendLine();
                    }
                }
            }

            var bytes = Encoding.UTF8.GetBytes(markdown.ToString());
            return Task.FromResult(new FormattedReport
            {
                Content = bytes,
                ContentType = "text/markdown",
                FileExtension = ".md"
            });
        }

        private Task<FormattedReport> ExportToTextAsync(GeneratedReport report, CancellationToken cancellationToken)
        {
            var text = new StringBuilder();

            text.AppendLine(report.Title);
            text.AppendLine(new string('=', report.Title?.Length ?? 10));
            text.AppendLine();
            text.AppendLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
            text.AppendLine($"Time Range: {report.TimeRange.StartTime:yyyy-MM-dd} to {report.TimeRange.EndTime:yyyy-MM-dd}");
            text.AppendLine($"Report Type: {report.ReportType}");
            text.AppendLine();

            if (report.Sections != null)
            {
                foreach (var section in report.Sections)
                {
                    text.AppendLine(section.Title);
                    text.AppendLine(new string('-', section.Title?.Length ?? 10));
                    text.AppendLine();

                    if (!string.IsNullOrEmpty(section.Description))
                    {
                        text.AppendLine(section.Description);
                        text.AppendLine();
                    }
                }
            }

            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            return Task.FromResult(new FormattedReport
            {
                Content = bytes,
                ContentType = "text/plain",
                FileExtension = ".txt"
            });
        }

        private async Task<GeneratedReport> CompressReportAsync(GeneratedReport report, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Compressing report: {ReportId}, Original size: {Size}", report.ReportId, report.FileSize);

                await using var compressedStream = new MemoryStream();
                await using (var gzipStream = new System.IO.Compression.GZipStream(compressedStream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                {
                    if (report.Content != null && report.Content.Length > 0)
                        await gzipStream.WriteAsync(report.Content, 0, report.Content.Length, cancellationToken).ConfigureAwait(false);
                }

                var compressedContent = compressedStream.ToArray();
                var originalSize = report.Content?.Length ?? 0;
                var compressionRatio = originalSize == 0 ? 1.0 : (double)compressedContent.Length / originalSize;

                report.Content = compressedContent;
                report.FileSize = compressedContent.Length;
                report.ContentType = "application/gzip";

                var baseName = Path.GetFileNameWithoutExtension(report.FileName ?? "report");
                report.FileName = $"{baseName}.gz";

                report.Metadata["Compressed"] = true;
                report.Metadata["CompressionRatio"] = compressionRatio;
                report.Metadata["OriginalSize"] = originalSize;

                _logger.LogInformation("Report compressed: {ReportId}, New size: {Size}, Ratio: {Ratio}", report.ReportId, report.FileSize, compressionRatio);
                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error compressing report: {ReportId}", report.ReportId);
                return report;
            }
        }

        private string GenerateReportTitle(ReportType reportType, ReportTimeRange timeRange)
        {
            var timeRangeStr = GetTimeRangeString(timeRange);

            return reportType switch
            {
                ReportType.SystemHealth => $"System Health Report - {timeRangeStr}",
                ReportType.PerformanceMetrics => $"Performance Metrics Report - {timeRangeStr}",
                ReportType.SecurityAudit => $"Security Audit Report - {timeRangeStr}",
                ReportType.UsageAnalytics => $"Usage Analytics Report - {timeRangeStr}",
                ReportType.ErrorAnalysis => $"Error Analysis Report - {timeRangeStr}",
                ReportType.CapacityPlanning => $"Capacity Planning Report - {timeRangeStr}",
                ReportType.CostAnalysis => $"Cost Analysis Report - {timeRangeStr}",
                ReportType.ComplianceReport => $"Compliance Report - {timeRangeStr}",
                ReportType.ExecutiveSummary => $"Executive Summary Report - {timeRangeStr}",
                _ => $"Custom Report - {timeRangeStr}"
            };
        }

        private string GenerateReportDescription(ReportType reportType, ReportTimeRange timeRange)
        {
            return reportType switch
            {
                ReportType.SystemHealth => "Comprehensive analysis of system health metrics including availability, performance, and resource utilization.",
                ReportType.PerformanceMetrics => "Detailed performance metrics analysis covering response times, throughput, error rates, and resource consumption.",
                ReportType.SecurityAudit => "Security audit report detailing access patterns, authentication events, and potential security threats.",
                ReportType.UsageAnalytics => "Usage analytics report showing user activity patterns, feature adoption, and engagement metrics.",
                ReportType.ErrorAnalysis => "Error analysis report identifying error patterns, root causes, and impact analysis.",
                ReportType.CapacityPlanning => "Capacity planning report forecasting resource requirements based on current usage trends.",
                ReportType.ExecutiveSummary => "High-level executive summary with key metrics, trends, and recommendations for decision makers.",
                _ => "Custom report generated based on specified parameters."
            };
        }

        private string GenerateFileName(ReportType reportType, ReportFormat format)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var reportTypeStr = reportType.ToString().ToLowerInvariant();
            var extension = GetFileExtension(format);

            return $"report_{reportTypeStr}_{timestamp}{extension}";
        }

        private string GetFileExtension(ReportFormat format)
        {
            return format switch
            {
                ReportFormat.PDF => ".pdf",
                ReportFormat.HTML => ".html",
                ReportFormat.Excel => ".xlsx",
                ReportFormat.CSV => ".csv",
                ReportFormat.JSON => ".json",
                ReportFormat.XML => ".xml",
                ReportFormat.Word => ".docx",
                ReportFormat.PowerPoint => ".pptx",
                ReportFormat.Markdown => ".md",
                ReportFormat.Text => ".txt",
                _ => ".txt"
            };
        }

        private string GetTimeRangeString(ReportTimeRange timeRange)
        {
            if (timeRange.StartTime.Date == timeRange.EndTime.Date)
                return timeRange.StartTime.ToString("MMMM dd, yyyy", CultureInfo.InvariantCulture);

            var startStr = timeRange.StartTime.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);
            var endStr = timeRange.EndTime.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture);
            return $"{startStr} to {endStr}";
        }

        private async Task<IEnumerable<ReportTemplate>> LoadReportTemplatesAsync(CancellationToken cancellationToken)
        {
            var templates = new List<ReportTemplate>();

            templates.AddRange(new[]
            {
                new ReportTemplate
                {
                    TemplateId = "SYS_HEALTH_DAILY",
                    Name = "Daily System Health",
                    Description = "Daily system health check report",
                    ReportType = ReportType.SystemHealth,
                    IsSystemTemplate = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-30),
                    Sections = new List<TemplateSection>
                    {
                        new TemplateSection { SectionId = "health_summary", Title = "System Health Summary", Type = SectionType.ExecutiveSummary },
                        new TemplateSection { SectionId = "performance_metrics", Title = "Performance Metrics", Type = SectionType.PerformanceMetrics }
                    },
                    DefaultParameters = new Dictionary<string, object>
                    {
                        { "TimeRange", ReportTimeRange.Last24Hours },
                        { "IncludeCharts", true },
                        { "IncludeExecutiveSummary", true }
                    }
                },
                new ReportTemplate
                {
                    TemplateId = "PERF_WEEKLY",
                    Name = "Weekly Performance",
                    Description = "Weekly performance metrics report",
                    ReportType = ReportType.PerformanceMetrics,
                    IsSystemTemplate = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-30),
                    Sections = new List<TemplateSection>
                    {
                        new TemplateSection { SectionId = "perf_kpis", Title = "KPIs", Type = SectionType.Metrics },
                        new TemplateSection { SectionId = "perf_trends", Title = "Trends", Type = SectionType.Trends }
                    },
                    DefaultParameters = new Dictionary<string, object>
                    {
                        { "TimeRange", ReportTimeRange.Last7Days },
                        { "IncludeCharts", true },
                        { "Format", ReportFormat.PDF }
                    }
                },
                new ReportTemplate
                {
                    TemplateId = "EXEC_MONTHLY",
                    Name = "Monthly Executive Summary",
                    Description = "Monthly summary for executive review",
                    ReportType = ReportType.ExecutiveSummary,
                    IsSystemTemplate = true,
                    CreatedAt = DateTime.UtcNow.AddDays(-30),
                    Sections = new List<TemplateSection>
                    {
                        new TemplateSection { SectionId = "executive_summary", Title = "Executive Summary", Type = SectionType.ExecutiveSummary },
                        new TemplateSection { SectionId = "recommendations", Title = "Recommendations", Type = SectionType.Recommendations }
                    },
                    DefaultParameters = new Dictionary<string, object>
                    {
                        { "TimeRange", ReportTimeRange.Last30Days },
                        { "IncludeCharts", true },
                        { "IncludeExecutiveSummary", true },
                        { "Format", ReportFormat.PDF }
                    }
                }
            });

            try
            {
                var customTemplates = await _reportStorage.GetTemplatesAsync(cancellationToken).ConfigureAwait(false);
                if (customTemplates != null)
                    templates.AddRange(customTemplates);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load custom templates");
            }

            return templates;
        }

        private NEDA.Core.Common.Result<bool> ValidateTemplate(ReportTemplate template)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(template.Name))
                errors.Add("Template name is required");

            if (string.IsNullOrWhiteSpace(template.Description))
                errors.Add("Template description is required");

            if (template.Sections == null || !template.Sections.Any())
                errors.Add("Template must have at least one section");

            return errors.Any()
                ? NEDA.Core.Common.Result<bool>.Failure(string.Join("; ", errors))
                : NEDA.Core.Common.Result<bool>.Success(true);
        }

        private async Task<TimeSpan> CalculateAverageGenerationTimeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var recentReports = await _reportStorage.GetReportsSinceAsync(DateTime.UtcNow.AddDays(-7), cancellationToken).ConfigureAwait(false);
                if (recentReports == null || !recentReports.Any())
                    return TimeSpan.Zero;

                var averageTicks = (long)recentReports.Average(r => r.GenerationDuration.Ticks);
                return TimeSpan.FromTicks(averageTicks);
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private async Task<ReportType> GetMostPopularReportTypeAsync(CancellationToken cancellationToken)
        {
            try
            {
                var recentReports = await _reportStorage.GetReportsSinceAsync(DateTime.UtcNow.AddDays(-30), cancellationToken).ConfigureAwait(false);
                if (recentReports == null || !recentReports.Any())
                    return ReportType.SystemHealth;

                var grouped = recentReports
                    .GroupBy(r => r.ReportType)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();

                return grouped?.Key ?? ReportType.SystemHealth;
            }
            catch
            {
                return ReportType.SystemHealth;
            }
        }

        private static string EscapeXml(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        #endregion

        #region Strategies

        public class SystemHealthReportStrategy : IReportGeneratorStrategy
        {
            private readonly ILogger<SystemHealthReportStrategy> _logger;
            private readonly NEDA.Monitoring.Diagnostics.IHealthChecker _healthChecker;
            private readonly NEDA.Monitoring.MetricsCollector.IMetricsEngine _metricsEngine;
            private readonly IChartBuilder _chartBuilder;

            public SystemHealthReportStrategy(
                ILogger<SystemHealthReportStrategy> logger,
                NEDA.Monitoring.Diagnostics.IHealthChecker healthChecker,
                NEDA.Monitoring.MetricsCollector.IMetricsEngine metricsEngine,
                IChartBuilder chartBuilder)
            {
                _logger = logger;
                _healthChecker = healthChecker;
                _metricsEngine = metricsEngine;
                _chartBuilder = chartBuilder;
            }

            public async Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Generating system health report");

                var report = new GeneratedReport
                {
                    ReportType = ReportType.SystemHealth,
                    Sections = new List<ReportSection>()
                };

                // Health summary
                var healthResult = await _healthChecker.CheckSystemHealthAsync(cancellationToken).ConfigureAwait(false);

                report.Sections.Add(new ReportSection
                {
                    SectionId = "health_summary",
                    Title = "System Health Summary",
                    Type = SectionType.ExecutiveSummary,
                    Data = healthResult,
                    Metrics = GenerateHealthMetrics(healthResult)
                });

                // Metrics
                var metrics = await _metricsEngine.GetSystemMetricsAsync(
                    request.TimeRange.StartTime,
                    request.TimeRange.EndTime,
                    cancellationToken).ConfigureAwait(false);

                report.Sections.Add(new ReportSection
                {
                    SectionId = "performance_metrics",
                    Title = "Performance Metrics",
                    Type = SectionType.PerformanceMetrics,
                    Data = metrics,
                    Metrics = ConvertToReportMetrics(metrics)
                });

                if (request.IncludeCharts)
                {
                    report.Sections.Add(new ReportSection
                    {
                        SectionId = "charts",
                        Title = "Health Trends",
                        Type = SectionType.Charts,
                        Charts = await GenerateHealthChartsAsync(metrics, cancellationToken).ConfigureAwait(false)
                    });
                }

                return report;
            }

            private List<ReportMetric> GenerateHealthMetrics(NEDA.Monitoring.Diagnostics.SystemHealth health)
            {
                var score = health?.OverallScore ?? 0;

                return new List<ReportMetric>
                {
                    new ReportMetric
                    {
                        MetricId = "overall_health",
                        Name = "Overall Health",
                        Description = "Overall system health score",
                        CurrentValue = new MetricValue { Value = score, Timestamp = DateTime.UtcNow, Unit = "%" },
                        Unit = "%",
                        Status = score >= 90 ? MetricStatus.Normal :
                                 score >= 70 ? MetricStatus.Warning : MetricStatus.Critical,
                        Trend = MetricTrend.Stable
                    }
                };
            }

            private List<ReportMetric> ConvertToReportMetrics(NEDA.Monitoring.MetricsCollector.SystemMetrics metrics)
            {
                // Adapt as your real SystemMetrics model evolves
                return new List<ReportMetric>
                {
                    new ReportMetric
                    {
                        MetricId = "cpu_avg",
                        Name = "CPU Avg",
                        Description = "Average CPU usage",
                        CurrentValue = new MetricValue { Value = metrics?.CpuAvg ?? 0, Timestamp = DateTime.UtcNow, Unit = "%" },
                        Unit = "%",
                        Trend = MetricTrend.Unknown,
                        Status = MetricStatus.Information
                    },
                    new ReportMetric
                    {
                        MetricId = "ram_avg",
                        Name = "RAM Avg",
                        Description = "Average RAM usage",
                        CurrentValue = new MetricValue { Value = metrics?.RamAvg ?? 0, Timestamp = DateTime.UtcNow, Unit = "%" },
                        Unit = "%",
                        Trend = MetricTrend.Unknown,
                        Status = MetricStatus.Information
                    }
                };
            }

            private async Task<List<ReportChart>> GenerateHealthChartsAsync(NEDA.Monitoring.MetricsCollector.SystemMetrics metrics, CancellationToken cancellationToken)
            {
                // Minimal example chart generation
                var charts = new List<ReportChart>();

                if (_chartBuilder == null)
                    return charts;

                var data = new ChartData
                {
                    Labels = new List<string> { "CPU Avg", "RAM Avg" },
                    Datasets = new List<ChartDataset>
                    {
                        new ChartDataset
                        {
                            Label = "Usage",
                            Data = new List<double> { metrics?.CpuAvg ?? 0, metrics?.RamAvg ?? 0 },
                            Fill = true
                        }
                    }
                };

                var options = new ChartOptions
                {
                    ShowLegend = true,
                    ShowGridLines = true,
                    XAxisTitle = "Metric",
                    YAxisTitle = "Percent"
                };

                var chart = await _chartBuilder.CreateChartAsync(ChartType.Bar, data, options, cancellationToken).ConfigureAwait(false);
                chart.ChartId = "usage_bar";
                chart.Title = "CPU/RAM Average Usage";
                charts.Add(chart);

                return charts;
            }
        }

        public class PerformanceMetricsReportStrategy : IReportGeneratorStrategy
        {
            private readonly ILogger<PerformanceMetricsReportStrategy> _logger;
            private readonly NEDA.Monitoring.Diagnostics.IPerformanceMonitor _performanceMonitor;
            private readonly NEDA.Monitoring.MetricsCollector.IMetricsEngine _metricsEngine;
            private readonly IChartBuilder _chartBuilder;

            public PerformanceMetricsReportStrategy(
                ILogger<PerformanceMetricsReportStrategy> logger,
                NEDA.Monitoring.Diagnostics.IPerformanceMonitor performanceMonitor,
                NEDA.Monitoring.MetricsCollector.IMetricsEngine metricsEngine,
                IChartBuilder chartBuilder)
            {
                _logger = logger;
                _performanceMonitor = performanceMonitor;
                _metricsEngine = metricsEngine;
                _chartBuilder = chartBuilder;
            }

            public async Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Generating performance metrics report");

                var report = new GeneratedReport
                {
                    ReportType = ReportType.PerformanceMetrics,
                    Sections = new List<ReportSection>()
                };

                // Example: gather metrics
                var metrics = await _metricsEngine.GetSystemMetricsAsync(request.TimeRange.StartTime, request.TimeRange.EndTime, cancellationToken).ConfigureAwait(false);

                report.Sections.Add(new ReportSection
                {
                    SectionId = "perf_overview",
                    Title = "Performance Overview",
                    Type = SectionType.Analysis,
                    Description = "Performance overview for the selected time range",
                    Data = metrics,
                    Metrics = new List<ReportMetric>
                    {
                        new ReportMetric
                        {
                            MetricId = "latency_p95",
                            Name = "Latency (P95)",
                            Description = "95th percentile latency",
                            CurrentValue = new MetricValue { Value = metrics?.LatencyP95Ms ?? 0, Timestamp = DateTime.UtcNow, Unit = "ms" },
                            Unit = "ms",
                            Status = MetricStatus.Information,
                            Trend = MetricTrend.Unknown
                        }
                    }
                });

                // Optional charts
                if (request.IncludeCharts && _chartBuilder != null)
                {
                    var data = new ChartData
                    {
                        Labels = new List<string> { "Latency P95 (ms)" },
                        Datasets = new List<ChartDataset>
                        {
                            new ChartDataset { Label = "Latency", Data = new List<double> { metrics?.LatencyP95Ms ?? 0 } }
                        }
                    };
                    var chart = await _chartBuilder.CreateChartAsync(ChartType.Bar, data, new ChartOptions(), cancellationToken).ConfigureAwait(false);
                    chart.ChartId = "latency_p95";
                    chart.Title = "Latency P95";
                    report.Sections.Add(new ReportSection
                    {
                        SectionId = "perf_charts",
                        Title = "Performance Charts",
                        Type = SectionType.Charts,
                        Charts = new List<ReportChart> { chart }
                    });
                }

                return report;
            }
        }

        public class SecurityAuditReportStrategy : IReportGeneratorStrategy
        {
            private readonly ILogger<SecurityAuditReportStrategy> _logger;
            public SecurityAuditReportStrategy(ILogger<SecurityAuditReportStrategy> logger) => _logger = logger;

            public Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Generating security audit report");
                return Task.FromResult(new GeneratedReport
                {
                    ReportType = ReportType.SecurityAudit,
                    Sections = new List<ReportSection>
                    {
                        new ReportSection
                        {
                            SectionId = "security_audit",
                            Title = "Security Audit",
                            Type = SectionType.Analysis,
                            Description = "Security audit placeholder (connect your auth/log sources here)."
                        }
                    }
                });
            }
        }

        public class UsageAnalyticsReportStrategy : IReportGeneratorStrategy
        {
            private readonly ILogger<UsageAnalyticsReportStrategy> _logger;
            private readonly NEDA.Monitoring.MetricsCollector.IMetricsEngine _metricsEngine;

            public UsageAnalyticsReportStrategy(ILogger<UsageAnalyticsReportStrategy> logger, NEDA.Monitoring.MetricsCollector.IMetricsEngine metricsEngine)
            {
                _logger = logger;
                _metricsEngine = metricsEngine;
            }

            public Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Generating usage analytics report");
                return Task.FromResult(new GeneratedReport
                {
                    ReportType = ReportType.UsageAnalytics,
                    Sections = new List<ReportSection>
                    {
                        new ReportSection
                        {
                            SectionId = "usage_analytics",
                            Title = "Usage Analytics",
                            Type = SectionType.Analysis,
                            Description = "Usage analytics placeholder (connect feature usage telemetry here)."
                        }
                    }
                });
            }
        }

        public class ErrorAnalysisReportStrategy : IReportGeneratorStrategy
        {
            private readonly ILogger<ErrorAnalysisReportStrategy> _logger;
            public ErrorAnalysisReportStrategy(ILogger<ErrorAnalysisReportStrategy> logger) => _logger = logger;

            public Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Generating error analysis report");
                return Task.FromResult(new GeneratedReport
                {
                    ReportType = ReportType.ErrorAnalysis,
                    Sections = new List<ReportSection>
                    {
                        new ReportSection
                        {
                            SectionId = "error_analysis",
                            Title = "Error Analysis",
                            Type = SectionType.Analysis,
                            Description = "Error analysis placeholder (wire with your ExceptionHandler & logs)."
                        }
                    }
                });
            }
        }

        public class CapacityPlanningReportStrategy : IReportGeneratorStrategy
        {
            private readonly ILogger<CapacityPlanningReportStrategy> _logger;
            private readonly NEDA.Monitoring.Diagnostics.IPerformanceMonitor _performanceMonitor;

            public CapacityPlanningReportStrategy(ILogger<CapacityPlanningReportStrategy> logger, NEDA.Monitoring.Diagnostics.IPerformanceMonitor performanceMonitor)
            {
                _logger = logger;
                _performanceMonitor = performanceMonitor;
            }

            public Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Generating capacity planning report");
                return Task.FromResult(new GeneratedReport
                {
                    ReportType = ReportType.CapacityPlanning,
                    Sections = new List<ReportSection>
                    {
                        new ReportSection
                        {
                            SectionId = "capacity_planning",
                            Title = "Capacity Planning",
                            Type = SectionType.Trends,
                            Description = "Capacity planning placeholder (add forecasting based on historical metrics)."
                        }
                    }
                });
            }
        }

        public class ExecutiveSummaryReportStrategy : IReportGeneratorStrategy
        {
            private readonly ILogger<ExecutiveSummaryReportStrategy> _logger;
            private readonly IServiceProvider _serviceProvider;

            public ExecutiveSummaryReportStrategy(ILogger<ExecutiveSummaryReportStrategy> logger, IServiceProvider serviceProvider)
            {
                _logger = logger;
                _serviceProvider = serviceProvider;
            }

            public Task<GeneratedReport> GenerateReportAsync(ReportRequest request, CancellationToken cancellationToken)
            {
                _logger.LogInformation("Generating executive summary report");

                var report = new GeneratedReport
                {
                    ReportType = ReportType.ExecutiveSummary,
                    Sections = new List<ReportSection>
                    {
                        new ReportSection
                        {
                            SectionId = "executive_summary",
                            Title = "Executive Summary",
                            Type = SectionType.ExecutiveSummary,
                            Description = "High-level overview of system performance and key metrics"
                        },
                        new ReportSection
                        {
                            SectionId = "key_metrics",
                            Title = "Key Performance Indicators",
                            Type = SectionType.Metrics,
                            Description = "Critical metrics for executive decision making"
                        },
                        new ReportSection
                        {
                            SectionId = "recommendations",
                            Title = "Recommendations",
                            Type = SectionType.Recommendations,
                            Description = "Actionable recommendations based on analysis"
                        }
                    }
                };

                return Task.FromResult(report);
            }
        }

        #endregion
    }

    #endregion
}

#if NEDA_STANDALONE
// -------------------------
// STANDALONE STUBS (ONLY FOR COMPILING THIS FILE ALONE)
// Remove #define NEDA_STANDALONE to use your real project implementations.
// -------------------------

namespace NEDA.Core.Common
{
    public class Result<T>
    {
        public bool IsSuccess { get; }
        public bool IsFailure => !IsSuccess;
        public T Value { get; }
        public string Error { get; }

        private Result(bool isSuccess, T value, string error)
        {
            IsSuccess = isSuccess;
            Value = value;
            Error = error;
        }

        public static Result<T> Success(T value) => new Result<T>(true, value, null);
        public static Result<T> Failure(string error) => new Result<T>(false, default, error);
    }
}

namespace NEDA.Monitoring.Diagnostics
{
    public interface IPerformanceMonitor { }

    public interface IHealthChecker
    {
        Task<SystemHealth> CheckSystemHealthAsync(CancellationToken cancellationToken);
    }

    public class SystemHealth
    {
        public double OverallScore { get; set; } = 85;
    }

    public class DummyHealthChecker : IHealthChecker
    {
        public Task<SystemHealth> CheckSystemHealthAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SystemHealth { OverallScore = 92 });
    }
}

namespace NEDA.Monitoring.MetricsCollector
{
    public interface IMetricsEngine
    {
        Task<SystemMetrics> GetSystemMetricsAsync(DateTime start, DateTime end, CancellationToken cancellationToken);
    }

    public class SystemMetrics
    {
        public double CpuAvg { get; set; } = 40;
        public double RamAvg { get; set; } = 55;
        public double LatencyP95Ms { get; set; } = 120;
    }

    public class DummyMetricsEngine : IMetricsEngine
    {
        public Task<SystemMetrics> GetSystemMetricsAsync(DateTime start, DateTime end, CancellationToken cancellationToken)
            => Task.FromResult(new SystemMetrics());
    }
}
#endif
