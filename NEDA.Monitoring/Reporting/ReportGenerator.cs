// NEDA.Monitoring/Reporting/ReportGenerator.cs;

using Microsoft.Extensions.Logging;
using NEDA.AI.MachineLearning;
using NEDA.Brain.DecisionMaking;
using NEDA.Common.Utilities;
using NEDA.Configuration.AppSettings;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Monitoring.HealthChecks;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.SecurityModules.AuditLogging;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Net.Mime;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Monitoring.Reporting;
{
    /// <summary>
    /// Advanced report generation engine with multi-format support,
    /// data visualization, scheduling, and automated distribution;
    /// </summary>
    public class ReportGenerator : IReportGenerator, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ISystemHealth _systemHealth;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IAuditLogger _auditLogger;
        private readonly IChartBuilder _chartBuilder;
        private readonly IExportService _exportService;
        private readonly ReportGeneratorConfiguration _configuration;

        private readonly Dictionary<string, IReportTemplate> _templates;
        private readonly Dictionary<string, IReportDataSource> _dataSources;
        private readonly Dictionary<string, ScheduledReport> _scheduledReports;
        private readonly ReportCache _cache;

        private Timer _schedulerTimer;
        private Timer _cleanupTimer;
        private Timer _cacheUpdateTimer;
        private bool _isRunning;
        private readonly object _syncLock = new object();

        private static readonly TimeSpan DefaultSchedulerInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan DefaultCacheUpdateInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Initialize ReportGenerator with required dependencies;
        /// </summary>
        public ReportGenerator(
            ILogger logger,
            IEventBus eventBus,
            IPerformanceMonitor performanceMonitor,
            ISystemHealth systemHealth,
            IMetricsCollector metricsCollector,
            IAuditLogger auditLogger,
            IChartBuilder chartBuilder,
            IExportService exportService,
            ReportGeneratorConfiguration configuration = null)
        {
            _logger = logger ?? LoggerFactory.CreateDefaultLogger();
            _eventBus = eventBus ?? new NullEventBus();
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _systemHealth = systemHealth ?? throw new ArgumentNullException(nameof(systemHealth));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _chartBuilder = chartBuilder ?? new DefaultChartBuilder();
            _exportService = exportService ?? new DefaultExportService();
            _configuration = configuration ?? ReportGeneratorConfiguration.Default;

            _templates = new Dictionary<string, IReportTemplate>();
            _dataSources = new Dictionary<string, IReportDataSource>();
            _scheduledReports = new Dictionary<string, ScheduledReport>();
            _cache = new ReportCache(_configuration.CacheConfiguration);

            InitializeBuiltInTemplates();
            InitializeDataSources();
            InitializeScheduler();

            _logger.LogInformation("ReportGenerator initialized");
        }

        /// <summary>
        /// Generate a report with specified parameters;
        /// </summary>
        public async Task<ReportResult> GenerateReportAsync(ReportRequest request)
        {
            ValidateReportRequest(request);

            var generationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            _logger.LogInformation($"Starting report generation: {generationId}, Type: {request.ReportType}");

            try
            {
                // Check cache first if enabled;
                if (request.UseCache && _configuration.CacheEnabled)
                {
                    var cachedReport = await _cache.GetAsync(request);
                    if (cachedReport != null)
                    {
                        _logger.LogDebug($"Report retrieved from cache: {generationId}");
                        return new ReportResult;
                        {
                            ReportId = generationId,
                            GenerationId = generationId,
                            IsCached = true,
                            ReportData = cachedReport.ReportData,
                            Format = request.Format,
                            Size = cachedReport.Size,
                            GeneratedAt = cachedReport.GeneratedAt,
                            GenerationTime = TimeSpan.Zero;
                        };
                    }
                }

                // Phase 1: Data Collection;
                var collectedData = await CollectReportDataAsync(request);

                // Phase 2: Data Processing;
                var processedData = await ProcessReportDataAsync(collectedData, request);

                // Phase 3: Template Application;
                var templatedData = await ApplyTemplateAsync(processedData, request);

                // Phase 4: Format Conversion;
                var formattedData = await ConvertToFormatAsync(templatedData, request);

                // Phase 5: Final Assembly;
                var reportData = await AssembleReportAsync(formattedData, request);

                // Calculate generation time;
                var generationTime = DateTime.UtcNow - startTime;

                // Create report result;
                var result = new ReportResult;
                {
                    ReportId = Guid.NewGuid().ToString(),
                    GenerationId = generationId,
                    IsCached = false,
                    ReportData = reportData,
                    Format = request.Format,
                    Size = reportData.Length,
                    GeneratedAt = DateTime.UtcNow,
                    GenerationTime = generationTime,
                    Metadata = new Dictionary<string, object>
                    {
                        { "DataPoints", collectedData.DataPoints },
                        { "ProcessingSteps", processedData.ProcessingSteps },
                        { "TemplateUsed", templatedData.TemplateName },
                        { "CompressionRatio", CalculateCompressionRatio(collectedData.RawData, reportData) }
                    }
                };

                // Cache the result if enabled;
                if (request.UseCache && _configuration.CacheEnabled)
                {
                    await _cache.StoreAsync(request, result);
                }

                // Log report generation;
                await LogReportGenerationAsync(request, result);

                // Publish generation event;
                _eventBus.Publish(new ReportGeneratedEvent;
                {
                    ReportId = result.ReportId,
                    GenerationId = generationId,
                    ReportType = request.ReportType,
                    Format = request.Format,
                    Size = result.Size,
                    GenerationTime = generationTime,
                    IsCached = false,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Report generation completed: {generationId}, " +
                                      $"Time: {generationTime.TotalSeconds:F2}s, Size: {result.Size:N0} bytes");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Report generation failed: {generationId}");

                _eventBus.Publish(new ReportGenerationFailedEvent;
                {
                    GenerationId = generationId,
                    ReportType = request.ReportType,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });

                throw new ReportGenerationException($"Failed to generate report: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generate report in batch mode for multiple requests;
        /// </summary>
        public async Task<IEnumerable<ReportResult>> GenerateReportsBatchAsync(IEnumerable<ReportRequest> requests)
        {
            var batchId = Guid.NewGuid().ToString();
            var batchStartTime = DateTime.UtcNow;

            _logger.LogInformation($"Starting batch report generation: {batchId}, Count: {requests.Count()}");

            var tasks = requests.Select(r => GenerateReportAsync(r));
            var results = await Task.WhenAll(tasks);

            var batchTime = DateTime.UtcNow - batchStartTime;

            _eventBus.Publish(new BatchReportGeneratedEvent;
            {
                BatchId = batchId,
                ReportCount = results.Length,
                TotalGenerationTime = batchTime,
                AverageReportTime = batchTime.TotalSeconds / results.Length,
                Timestamp = DateTime.UtcNow;
            });

            _logger.LogInformation($"Batch report generation completed: {batchId}, " +
                                  $"Time: {batchTime.TotalSeconds:F2}s, Count: {results.Length}");

            return results;
        }

        /// <summary>
        /// Schedule a report for automatic generation;
        /// </summary>
        public async Task<ScheduledReport> ScheduleReportAsync(ReportSchedule schedule)
        {
            ValidateSchedule(schedule);

            var scheduledReport = new ScheduledReport;
            {
                ScheduleId = Guid.NewGuid().ToString(),
                Schedule = schedule,
                NextRunTime = CalculateNextRunTime(schedule),
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                RunCount = 0,
                LastRunTime = null,
                LastRunStatus = null;
            };

            lock (_syncLock)
            {
                _scheduledReports[scheduledReport.ScheduleId] = scheduledReport;
            }

            _logger.LogInformation($"Report scheduled: {scheduledReport.ScheduleId}, " +
                                  $"Type: {schedule.ReportRequest.ReportType}, " +
                                  $"Schedule: {schedule.ScheduleType}, " +
                                  $"Next Run: {scheduledReport.NextRunTime:yyyy-MM-dd HH:mm}");

            _eventBus.Publish(new ReportScheduledEvent;
            {
                ScheduleId = scheduledReport.ScheduleId,
                ReportType = schedule.ReportRequest.ReportType,
                ScheduleType = schedule.ScheduleType,
                NextRunTime = scheduledReport.NextRunTime,
                Timestamp = DateTime.UtcNow;
            });

            return scheduledReport;
        }

        /// <summary>
        /// Cancel a scheduled report;
        /// </summary>
        public void CancelScheduledReport(string scheduleId)
        {
            lock (_syncLock)
            {
                if (_scheduledReports.TryGetValue(scheduleId, out var scheduledReport))
                {
                    scheduledReport.IsActive = false;
                    scheduledReport.CancelledAt = DateTime.UtcNow;

                    _logger.LogInformation($"Scheduled report cancelled: {scheduleId}");

                    _eventBus.Publish(new ReportScheduleCancelledEvent;
                    {
                        ScheduleId = scheduleId,
                        Timestamp = DateTime.UtcNow;
                    });
                }
                else;
                {
                    throw new ScheduleNotFoundException($"Schedule '{scheduleId}' not found");
                }
            }
        }

        /// <summary>
        /// Get all scheduled reports;
        /// </summary>
        public IEnumerable<ScheduledReport> GetScheduledReports(bool activeOnly = true)
        {
            lock (_syncLock)
            {
                return _scheduledReports.Values;
                    .Where(r => !activeOnly || r.IsActive)
                    .OrderBy(r => r.NextRunTime)
                    .ToList();
            }
        }

        /// <summary>
        /// Execute a scheduled report immediately;
        /// </summary>
        public async Task<ReportResult> ExecuteScheduledReportAsync(string scheduleId)
        {
            ScheduledReport scheduledReport;

            lock (_syncLock)
            {
                if (!_scheduledReports.TryGetValue(scheduleId, out scheduledReport))
                    throw new ScheduleNotFoundException($"Schedule '{scheduleId}' not found");
            }

            _logger.LogInformation($"Executing scheduled report immediately: {scheduleId}");

            try
            {
                var result = await GenerateReportAsync(scheduledReport.Schedule.ReportRequest);

                // Update schedule;
                scheduledReport.LastRunTime = DateTime.UtcNow;
                scheduledReport.LastRunStatus = ReportRunStatus.Success;
                scheduledReport.RunCount++;

                return result;
            }
            catch (Exception ex)
            {
                scheduledReport.LastRunTime = DateTime.UtcNow;
                scheduledReport.LastRunStatus = ReportRunStatus.Failed;
                scheduledReport.LastError = ex.Message;

                throw;
            }
        }

        /// <summary>
        /// Register a custom report template;
        /// </summary>
        public void RegisterTemplate(string name, IReportTemplate template)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Template name cannot be empty", nameof(name));

            if (template == null)
                throw new ArgumentNullException(nameof(template));

            lock (_syncLock)
            {
                _templates[name] = template;
            }

            _logger.LogInformation($"Report template registered: {name}");
        }

        /// <summary>
        /// Register a custom data source;
        /// </summary>
        public void RegisterDataSource(string name, IReportDataSource dataSource)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Data source name cannot be empty", nameof(name));

            if (dataSource == null)
                throw new ArgumentNullException(nameof(dataSource));

            lock (_syncLock)
            {
                _dataSources[name] = dataSource;
            }

            _logger.LogInformation($"Report data source registered: {name}");
        }

        /// <summary>
        /// Get available report templates;
        /// </summary>
        public IEnumerable<TemplateInfo> GetAvailableTemplates()
        {
            lock (_syncLock)
            {
                return _templates.Select(kvp => new TemplateInfo;
                {
                    Name = kvp.Key,
                    Description = kvp.Value.Description,
                    SupportedFormats = kvp.Value.SupportedFormats,
                    LastModified = kvp.Value.LastModified;
                }).ToList();
            }
        }

        /// <summary>
        /// Export report to file;
        /// </summary>
        public async Task<ExportResult> ExportReportToFileAsync(ReportResult report, string filePath)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty", nameof(filePath));

            var exportStartTime = DateTime.UtcNow;
            var exportId = Guid.NewGuid().ToString();

            _logger.LogInformation($"Exporting report to file: {exportId}, Path: {filePath}");

            try
            {
                // Ensure directory exists;
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write report data to file;
                await File.WriteAllBytesAsync(filePath, report.ReportData);

                // Get file info;
                var fileInfo = new FileInfo(filePath);

                var result = new ExportResult;
                {
                    ExportId = exportId,
                    FilePath = filePath,
                    FileSize = fileInfo.Length,
                    Format = report.Format,
                    ExportTime = DateTime.UtcNow - exportStartTime,
                    GeneratedAt = DateTime.UtcNow;
                };

                // Publish export event;
                _eventBus.Publish(new ReportExportedEvent;
                {
                    ExportId = exportId,
                    ReportId = report.ReportId,
                    FilePath = filePath,
                    FileSize = fileInfo.Length,
                    Format = report.Format,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Report exported to file: {exportId}, Size: {fileInfo.Length:N0} bytes");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Report export failed: {exportId}");
                throw new ReportExportException($"Failed to export report: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Send report via email;
        /// </summary>
        public async Task<EmailResult> SendReportByEmailAsync(ReportResult report, EmailOptions options)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            ValidateEmailOptions(options);

            var emailStartTime = DateTime.UtcNow;
            var emailId = Guid.NewGuid().ToString();

            _logger.LogInformation($"Sending report by email: {emailId}, To: {string.Join(", ", options.ToAddresses)}");

            try
            {
                using var mailMessage = new MailMessage();

                // Set sender;
                mailMessage.From = new MailAddress(options.FromAddress, options.FromName);

                // Set recipients;
                foreach (var address in options.ToAddresses)
                {
                    mailMessage.To.Add(address);
                }

                // Set CC if provided;
                if (options.CcAddresses != null)
                {
                    foreach (var address in options.CcAddresses)
                    {
                        mailMessage.CC.Add(address);
                    }
                }

                // Set BCC if provided;
                if (options.BccAddresses != null)
                {
                    foreach (var address in options.BccAddresses)
                    {
                        mailMessage.Bcc.Add(address);
                    }
                }

                // Set subject;
                mailMessage.Subject = options.Subject ??
                    $"Report: {report.Metadata?.GetValueOrDefault("ReportTitle", "Untitled")}";

                // Set body;
                mailMessage.Body = options.Body ??
                    $"Please find the attached report.\n\nGenerated: {report.GeneratedAt:yyyy-MM-dd HH:mm}\nSize: {FormatSize(report.Size)}";
                mailMessage.IsBodyHtml = options.IsHtml;

                // Add attachment;
                var attachment = new Attachment(
                    new MemoryStream(report.ReportData),
                    $"report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.{GetFileExtension(report.Format)}",
                    GetMimeType(report.Format));

                mailMessage.Attachments.Add(attachment);

                // Send email (implementation depends on email service)
                await SendEmailAsync(mailMessage, options.SmtpConfiguration);

                // Dispose attachment;
                attachment.Dispose();

                var result = new EmailResult;
                {
                    EmailId = emailId,
                    ReportId = report.ReportId,
                    RecipientCount = options.ToAddresses.Count(),
                    SentAt = DateTime.UtcNow,
                    DeliveryTime = DateTime.UtcNow - emailStartTime;
                };

                // Publish email event;
                _eventBus.Publish(new ReportEmailedEvent;
                {
                    EmailId = emailId,
                    ReportId = report.ReportId,
                    RecipientCount = result.RecipientCount,
                    Format = report.Format,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Report sent by email: {emailId}, Recipients: {result.RecipientCount}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send report by email: {emailId}");
                throw new EmailDeliveryException($"Failed to send report by email: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get report generation statistics;
        /// </summary>
        public ReportStatistics GetStatistics()
        {
            var stats = new ReportStatistics;
            {
                TotalReportsGenerated = _cache.TotalReportsGenerated,
                CacheHitRate = _cache.HitRate,
                CacheSize = _cache.CurrentSize,
                CacheMaxSize = _cache.MaxSize,
                ScheduledReportsCount = _scheduledReports.Count(r => r.Value.IsActive),
                TemplatesCount = _templates.Count,
                DataSourcesCount = _dataSources.Count,
                LastCleanupTime = _cache.LastCleanupTime,
                GenerationSuccessRate = CalculateSuccessRate()
            };

            return stats;
        }

        /// <summary>
        /// Clear report cache;
        /// </summary>
        public async Task<CacheClearResult> ClearCacheAsync()
        {
            var clearStartTime = DateTime.UtcNow;
            var clearId = Guid.NewGuid().ToString();

            _logger.LogInformation($"Clearing report cache: {clearId}");

            try
            {
                var clearedItems = await _cache.ClearAsync();

                var result = new CacheClearResult;
                {
                    ClearId = clearId,
                    ClearedItems = clearedItems,
                    ClearTime = DateTime.UtcNow - clearStartTime,
                    ClearedAt = DateTime.UtcNow;
                };

                _eventBus.Publish(new CacheClearedEvent;
                {
                    ClearId = clearId,
                    ClearedItems = clearedItems,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Report cache cleared: {clearId}, Items: {clearedItems}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to clear report cache: {clearId}");
                throw new CacheClearException($"Failed to clear report cache: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Start report scheduler;
        /// </summary>
        public void StartScheduler()
        {
            if (_isRunning)
                return;

            lock (_syncLock)
            {
                if (_isRunning)
                    return;

                _schedulerTimer?.Change(TimeSpan.Zero, _configuration.SchedulerInterval ?? DefaultSchedulerInterval);
                _cleanupTimer?.Change(_configuration.CleanupInterval ?? DefaultCleanupInterval,
                                     _configuration.CleanupInterval ?? DefaultCleanupInterval);
                _cacheUpdateTimer?.Change(_configuration.CacheUpdateInterval ?? DefaultCacheUpdateInterval,
                                        _configuration.CacheUpdateInterval ?? DefaultCacheUpdateInterval);

                _isRunning = true;

                _logger.LogInformation("Report scheduler started");
            }
        }

        /// <summary>
        /// Stop report scheduler;
        /// </summary>
        public void StopScheduler()
        {
            if (!_isRunning)
                return;

            lock (_syncLock)
            {
                if (!_isRunning)
                    return;

                _schedulerTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _cacheUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                _isRunning = false;

                _logger.LogInformation("Report scheduler stopped");
            }
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            StopScheduler();

            _schedulerTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _cacheUpdateTimer?.Dispose();

            _cache?.Dispose();

            GC.SuppressFinalize(this);
        }

        #region Private Implementation Methods;

        private void InitializeBuiltInTemplates()
        {
            // Performance Report Template;
            RegisterTemplate("PerformanceReport", new PerformanceReportTemplate());

            // Health Report Template;
            RegisterTemplate("HealthReport", new HealthReportTemplate());

            // Metrics Report Template;
            RegisterTemplate("MetricsReport", new MetricsReportTemplate());

            // Audit Report Template;
            RegisterTemplate("AuditReport", new AuditReportTemplate());

            // Diagnostic Report Template;
            RegisterTemplate("DiagnosticReport", new DiagnosticReportTemplate());

            // Executive Summary Template;
            RegisterTemplate("ExecutiveSummary", new ExecutiveSummaryTemplate());

            // Detailed Analysis Template;
            RegisterTemplate("DetailedAnalysis", new DetailedAnalysisTemplate());

            // Custom templates from configuration;
            foreach (var templateConfig in _configuration.CustomTemplates)
            {
                try
                {
                    var templateType = Type.GetType(templateConfig.TemplateClass);
                    if (templateType != null && typeof(IReportTemplate).IsAssignableFrom(templateType))
                    {
                        var template = Activator.CreateInstance(templateType) as IReportTemplate;
                        if (template != null)
                        {
                            RegisterTemplate(templateConfig.Name, template);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to load custom template: {templateConfig.Name}");
                }
            }
        }

        private void InitializeDataSources()
        {
            // Performance Data Source;
            RegisterDataSource("Performance", new PerformanceDataSource(_performanceMonitor));

            // Health Data Source;
            RegisterDataSource("Health", new HealthDataSource(_systemHealth));

            // Metrics Data Source;
            RegisterDataSource("Metrics", new MetricsDataSource(_metricsCollector));

            // Audit Data Source;
            RegisterDataSource("Audit", new AuditDataSource(_auditLogger));

            // System Data Source;
            RegisterDataSource("System", new SystemDataSource());

            // Custom data sources from configuration;
            foreach (var dataSourceConfig in _configuration.CustomDataSources)
            {
                try
                {
                    var dataSourceType = Type.GetType(dataSourceConfig.DataSourceClass);
                    if (dataSourceType != null && typeof(IReportDataSource).IsAssignableFrom(dataSourceType))
                    {
                        var dataSource = Activator.CreateInstance(dataSourceType) as IReportDataSource;
                        if (dataSource != null)
                        {
                            RegisterDataSource(dataSourceConfig.Name, dataSource);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to load custom data source: {dataSourceConfig.Name}");
                }
            }
        }

        private void InitializeScheduler()
        {
            _schedulerTimer = new Timer(async _ => await ProcessScheduledReportsAsync(), null,
                Timeout.Infinite, Timeout.Infinite);

            _cleanupTimer = new Timer(async _ => await CleanupOldReportsAsync(), null,
                Timeout.Infinite, Timeout.Infinite);

            _cacheUpdateTimer = new Timer(async _ => await UpdateCacheAsync(), null,
                Timeout.Infinite, Timeout.Infinite);
        }

        private async Task ProcessScheduledReportsAsync()
        {
            if (!_isRunning)
                return;

            var processStartTime = DateTime.UtcNow;
            var now = DateTime.UtcNow;

            try
            {
                _logger.LogDebug("Processing scheduled reports");

                List<ScheduledReport> reportsToRun;

                lock (_syncLock)
                {
                    reportsToRun = _scheduledReports.Values;
                        .Where(r => r.IsActive && r.NextRunTime <= now)
                        .ToList();
                }

                foreach (var scheduledReport in reportsToRun)
                {
                    try
                    {
                        _logger.LogInformation($"Running scheduled report: {scheduledReport.ScheduleId}");

                        // Generate report;
                        var result = await GenerateReportAsync(scheduledReport.Schedule.ReportRequest);

                        // Update schedule;
                        scheduledReport.LastRunTime = now;
                        scheduledReport.LastRunStatus = ReportRunStatus.Success;
                        scheduledReport.RunCount++;
                        scheduledReport.NextRunTime = CalculateNextRunTime(scheduledReport.Schedule);

                        // Distribute report if configured;
                        if (scheduledReport.Schedule.DistributionOptions != null)
                        {
                            await DistributeReportAsync(result, scheduledReport.Schedule.DistributionOptions);
                        }

                        _eventBus.Publish(new ScheduledReportRunEvent;
                        {
                            ScheduleId = scheduledReport.ScheduleId,
                            ReportId = result.ReportId,
                            RunTime = now,
                            Status = ReportRunStatus.Success,
                            NextRunTime = scheduledReport.NextRunTime,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                    catch (Exception ex)
                    {
                        scheduledReport.LastRunTime = now;
                        scheduledReport.LastRunStatus = ReportRunStatus.Failed;
                        scheduledReport.LastError = ex.Message;

                        _logger.LogError(ex, $"Scheduled report failed: {scheduledReport.ScheduleId}");

                        _eventBus.Publish(new ScheduledReportRunEvent;
                        {
                            ScheduleId = scheduledReport.ScheduleId,
                            RunTime = now,
                            Status = ReportRunStatus.Failed,
                            Error = ex.Message,
                            Timestamp = DateTime.UtcNow;
                        });

                        // Retry logic;
                        if (scheduledReport.Schedule.RetryOnFailure &&
                            scheduledReport.ConsecutiveFailures < scheduledReport.Schedule.MaxRetries)
                        {
                            scheduledReport.ConsecutiveFailures++;
                            scheduledReport.NextRunTime = now.AddMinutes(5); // Retry in 5 minutes;
                        }
                    }
                }

                _logger.LogDebug($"Scheduled reports processed: {reportsToRun.Count}, " +
                               $"Time: {(DateTime.UtcNow - processStartTime).TotalSeconds:F2}s");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled report processing failed");
            }
        }

        private async Task CleanupOldReportsAsync()
        {
            if (!_isRunning)
                return;

            try
            {
                _logger.LogDebug("Cleaning up old reports");

                // Cleanup cache;
                var cacheCleaned = await _cache.CleanupAsync();

                // Cleanup expired scheduled reports;
                lock (_syncLock)
                {
                    var expiredSchedules = _scheduledReports.Values;
                        .Where(r => !r.IsActive &&
                               r.CancelledAt.HasValue &&
                               DateTime.UtcNow - r.CancelledAt.Value > TimeSpan.FromDays(7))
                        .ToList();

                    foreach (var schedule in expiredSchedules)
                    {
                        _scheduledReports.Remove(schedule.ScheduleId);
                    }

                    _logger.LogDebug($"Cleanup completed, Cache: {cacheCleaned} items, " +
                                   $"Schedules: {expiredSchedules.Count} removed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report cleanup failed");
            }
        }

        private async Task UpdateCacheAsync()
        {
            if (!_isRunning)
                return;

            try
            {
                _logger.LogDebug("Updating report cache");

                // Update cache statistics;
                await _cache.UpdateStatisticsAsync();

                // Pre-cache frequently requested reports;
                if (_configuration.PreCacheEnabled)
                {
                    await PreCacheFrequentReportsAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache update failed");
            }
        }

        private async Task PreCacheFrequentReportsAsync()
        {
            // Identify frequently requested reports;
            var frequentRequests = _cache.GetFrequentRequests(_configuration.PreCacheThreshold);

            foreach (var request in frequentRequests)
            {
                try
                {
                    if (!await _cache.ContainsAsync(request))
                    {
                        _logger.LogDebug($"Pre-caching report: {request.ReportType}");
                        await GenerateReportAsync(request);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to pre-cache report: {request.ReportType}");
                }
            }
        }

        private async Task<CollectedData> CollectReportDataAsync(ReportRequest request)
        {
            var collectionStartTime = DateTime.UtcNow;
            var collectedData = new CollectedData();

            _logger.LogDebug($"Collecting report data for: {request.ReportType}");

            try
            {
                // Determine required data sources based on report type;
                var requiredSources = DetermineRequiredDataSources(request);

                // Collect data from each source;
                foreach (var sourceName in requiredSources)
                {
                    if (_dataSources.TryGetValue(sourceName, out var dataSource))
                    {
                        var sourceData = await dataSource.GetDataAsync(request);
                        collectedData.AddSourceData(sourceName, sourceData);
                    }
                    else;
                    {
                        _logger.LogWarning($"Data source not found: {sourceName}");
                    }
                }

                // Add metadata;
                collectedData.Metadata.Add("CollectionTime", DateTime.UtcNow);
                collectedData.Metadata.Add("CollectionDuration", DateTime.UtcNow - collectionStartTime);
                collectedData.Metadata.Add("ReportParameters", request.Parameters);
                collectedData.Metadata.Add("DataSources", requiredSources);

                // Validate collected data;
                await ValidateCollectedDataAsync(collectedData, request);

                collectedData.CollectionDuration = DateTime.UtcNow - collectionStartTime;

                _logger.LogDebug($"Data collection completed: {collectedData.DataPoints} data points, " +
                               $"Time: {collectedData.CollectionDuration.TotalSeconds:F2}s");

                return collectedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report data collection failed");
                throw new DataCollectionException("Failed to collect report data", ex);
            }
        }

        private IEnumerable<string> DetermineRequiredDataSources(ReportRequest request)
        {
            var sources = new List<string>();

            switch (request.ReportType)
            {
                case ReportType.Performance:
                    sources.Add("Performance");
                    sources.Add("System");
                    break;

                case ReportType.Health:
                    sources.Add("Health");
                    sources.Add("Performance");
                    break;

                case ReportType.Metrics:
                    sources.Add("Metrics");
                    break;

                case ReportType.Audit:
                    sources.Add("Audit");
                    break;

                case ReportType.Diagnostic:
                    sources.Add("Performance");
                    sources.Add("Health");
                    sources.Add("Metrics");
                    sources.Add("System");
                    break;

                case ReportType.ExecutiveSummary:
                    sources.Add("Performance");
                    sources.Add("Health");
                    sources.Add("Metrics");
                    break;

                case ReportType.Custom:
                    if (request.Parameters.TryGetValue("DataSources", out var customSources))
                    {
                        sources.AddRange(customSources.ToString().Split(','));
                    }
                    break;
            }

            return sources.Distinct();
        }

        private async Task<ProcessedData> ProcessReportDataAsync(CollectedData collectedData, ReportRequest request)
        {
            var processingStartTime = DateTime.UtcNow;
            var processedData = new ProcessedData();

            _logger.LogDebug($"Processing report data for: {request.ReportType}");

            try
            {
                // Step 1: Data Cleaning;
                var cleanedData = await CleanDataAsync(collectedData);

                // Step 2: Data Transformation;
                var transformedData = await TransformDataAsync(cleanedData, request);

                // Step 3: Data Aggregation;
                var aggregatedData = await AggregateDataAsync(transformedData, request);

                // Step 4: Data Enrichment;
                var enrichedData = await EnrichDataAsync(aggregatedData, request);

                // Step 5: Data Analysis;
                var analyzedData = await AnalyzeDataAsync(enrichedData, request);

                // Step 6: Data Formatting;
                var formattedData = await FormatForOutputAsync(analyzedData, request);

                // Assemble processed data;
                processedData.RawData = collectedData.RawData;
                processedData.CleanedData = cleanedData;
                processedData.TransformedData = transformedData;
                processedData.AggregatedData = aggregatedData;
                processedData.EnrichedData = enrichedData;
                processedData.AnalyzedData = analyzedData;
                processedData.FormattedData = formattedData;
                processedData.Metadata = collectedData.Metadata;
                processedData.ProcessingSteps = new List<string>
                {
                    "Data Cleaning",
                    "Data Transformation",
                    "Data Aggregation",
                    "Data Enrichment",
                    "Data Analysis",
                    "Data Formatting"
                };
                processedData.ProcessingDuration = DateTime.UtcNow - processingStartTime;

                _logger.LogDebug($"Data processing completed: {processedData.ProcessingSteps.Count} steps, " +
                               $"Time: {processedData.ProcessingDuration.TotalSeconds:F2}s");

                return processedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report data processing failed");
                throw new DataProcessingException("Failed to process report data", ex);
            }
        }

        private async Task<TemplatedData> ApplyTemplateAsync(ProcessedData processedData, ReportRequest request)
        {
            var templatingStartTime = DateTime.UtcNow;

            _logger.LogDebug($"Applying template for: {request.ReportType}");

            try
            {
                // Determine which template to use;
                var templateName = DetermineTemplate(request);

                if (!_templates.TryGetValue(templateName, out var template))
                {
                    throw new TemplateNotFoundException($"Template '{templateName}' not found");
                }

                // Check if template supports requested format;
                if (!template.SupportedFormats.Contains(request.Format))
                {
                    throw new FormatNotSupportedException($"Template '{templateName}' does not support format '{request.Format}'");
                }

                // Apply template;
                var templatedData = await template.ApplyAsync(processedData, request);

                templatedData.TemplateName = templateName;
                templatedData.TemplatingDuration = DateTime.UtcNow - templatingStartTime;

                _logger.LogDebug($"Template applied: {templateName}, " +
                               $"Time: {templatedData.TemplatingDuration.TotalSeconds:F2}s");

                return templatedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template application failed");
                throw new TemplateApplicationException("Failed to apply template", ex);
            }
        }

        private string DetermineTemplate(ReportRequest request)
        {
            // Check if template is specified in parameters;
            if (request.Parameters.TryGetValue("Template", out var templateName))
            {
                return templateName.ToString();
            }

            // Default templates based on report type;
            return request.ReportType switch;
            {
                ReportType.Performance => "PerformanceReport",
                ReportType.Health => "HealthReport",
                ReportType.Metrics => "MetricsReport",
                ReportType.Audit => "AuditReport",
                ReportType.Diagnostic => "DiagnosticReport",
                ReportType.ExecutiveSummary => "ExecutiveSummary",
                ReportType.DetailedAnalysis => "DetailedAnalysis",
                _ => "DefaultReport"
            };
        }

        private async Task<FormattedData> ConvertToFormatAsync(TemplatedData templatedData, ReportRequest request)
        {
            var formattingStartTime = DateTime.UtcNow;

            _logger.LogDebug($"Converting to format: {request.Format}");

            try
            {
                byte[] formattedBytes;

                switch (request.Format)
                {
                    case ReportFormat.Pdf:
                        formattedBytes = await _exportService.ExportToPdfAsync(templatedData);
                        break;

                    case ReportFormat.Html:
                        formattedBytes = await _exportService.ExportToHtmlAsync(templatedData);
                        break;

                    case ReportFormat.Excel:
                        formattedBytes = await _exportService.ExportToExcelAsync(templatedData);
                        break;

                    case ReportFormat.Csv:
                        formattedBytes = await _exportService.ExportToCsvAsync(templatedData);
                        break;

                    case ReportFormat.Xml:
                        formattedBytes = await _exportService.ExportToXmlAsync(templatedData);
                        break;

                    case ReportFormat.Json:
                        formattedBytes = await _exportService.ExportToJsonAsync(templatedData);
                        break;

                    case ReportFormat.Word:
                        formattedBytes = await _exportService.ExportToWordAsync(templatedData);
                        break;

                    case ReportFormat.PowerPoint:
                        formattedBytes = await _exportService.ExportToPowerPointAsync(templatedData);
                        break;

                    default:
                        throw new FormatNotSupportedException($"Format '{request.Format}' is not supported");
                }

                var formattedData = new FormattedData;
                {
                    Data = formattedBytes,
                    Format = request.Format,
                    Size = formattedBytes.Length,
                    FormattingDuration = DateTime.UtcNow - formattingStartTime;
                };

                _logger.LogDebug($"Format conversion completed: {request.Format}, " +
                               $"Size: {formattedData.Size:N0} bytes, " +
                               $"Time: {formattedData.FormattingDuration.TotalSeconds:F2}s");

                return formattedData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Format conversion failed");
                throw new FormatConversionException($"Failed to convert to format '{request.Format}'", ex);
            }
        }

        private async Task<byte[]> AssembleReportAsync(FormattedData formattedData, ReportRequest request)
        {
            var assemblyStartTime = DateTime.UtcNow;

            _logger.LogDebug("Assembling final report");

            try
            {
                byte[] reportData;

                // Apply compression if requested;
                if (request.CompressionEnabled && _configuration.CompressionEnabled)
                {
                    reportData = await CompressDataAsync(formattedData.Data);
                }
                else;
                {
                    reportData = formattedData.Data;
                }

                // Add digital signature if requested;
                if (request.SignReport && _configuration.DigitalSignatureEnabled)
                {
                    reportData = await SignReportAsync(reportData);
                }

                // Add watermark if requested;
                if (request.AddWatermark && _configuration.WatermarkEnabled)
                {
                    reportData = await AddWatermarkAsync(reportData, request);
                }

                var assemblyTime = DateTime.UtcNow - assemblyStartTime;

                _logger.LogDebug($"Report assembly completed, " +
                               $"Final size: {reportData.Length:N0} bytes, " +
                               $"Time: {assemblyTime.TotalSeconds:F2}s");

                return reportData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report assembly failed");
                throw new ReportAssemblyException("Failed to assemble final report", ex);
            }
        }

        private async Task<byte[]> CompressDataAsync(byte[] data)
        {
            using var compressedStream = new System.IO.MemoryStream();
            using var gzipStream = new System.IO.Compression.GZipStream(compressedStream,
                System.IO.Compression.CompressionLevel.Optimal);

            await gzipStream.WriteAsync(data, 0, data.Length);
            await gzipStream.FlushAsync();

            return compressedStream.ToArray();
        }

        private async Task<byte[]> SignReportAsync(byte[] data)
        {
            // Digital signature implementation;
            // This would use certificates and cryptographic signing;
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(data);

            // In production, this would use proper certificate-based signing;
            return data.Concat(hash).ToArray();
        }

        private async Task<byte[]> AddWatermarkAsync(byte[] data, ReportRequest request)
        {
            // Watermark implementation;
            // This would depend on the format and watermark type;
            return data; // Simplified for this example;
        }

        private async Task DistributeReportAsync(ReportResult report, DistributionOptions options)
        {
            if (options == null)
                return;

            _logger.LogDebug($"Distributing report: {report.ReportId}");

            try
            {
                // Email distribution;
                if (options.EmailDistribution != null && options.EmailDistribution.Enabled)
                {
                    await SendReportByEmailAsync(report, options.EmailDistribution);
                }

                // File system distribution;
                if (options.FileSystemDistribution != null && options.FileSystemDistribution.Enabled)
                {
                    await ExportReportToFileAsync(report, options.FileSystemDistribution.FilePath);
                }

                // Web service distribution;
                if (options.WebServiceDistribution != null && options.WebServiceDistribution.Enabled)
                {
                    await UploadToWebServiceAsync(report, options.WebServiceDistribution);
                }

                _logger.LogDebug($"Report distribution completed: {report.ReportId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Report distribution failed: {report.ReportId}");
                throw new DistributionException("Failed to distribute report", ex);
            }
        }

        private async Task UploadToWebServiceAsync(ReportResult report, WebServiceDistribution options)
        {
            // Web service upload implementation;
            // This would use HttpClient to upload to a REST API;
            using var client = new System.Net.Http.HttpClient();
            var content = new System.Net.Http.ByteArrayContent(report.ReportData);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetMimeType(report.Format));

            var response = await client.PostAsync(options.EndpointUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                throw new WebServiceException($"Web service upload failed: {response.StatusCode}");
            }
        }

        private async Task SendEmailAsync(MailMessage mailMessage, SmtpConfiguration smtpConfig)
        {
            using var smtpClient = new SmtpClient;
            {
                Host = smtpConfig.Host,
                Port = smtpConfig.Port,
                EnableSsl = smtpConfig.EnableSsl,
                Credentials = new System.Net.NetworkCredential(smtpConfig.Username, smtpConfig.Password)
            };

            await smtpClient.SendMailAsync(mailMessage);
        }

        private async Task LogReportGenerationAsync(ReportRequest request, ReportResult result)
        {
            var logEntry = new ReportGenerationLog;
            {
                ReportId = result.ReportId,
                GenerationId = result.GenerationId,
                ReportType = request.ReportType,
                Format = request.Format,
                Size = result.Size,
                GenerationTime = result.GenerationTime,
                GeneratedAt = result.GeneratedAt,
                IsCached = result.IsCached,
                Parameters = request.Parameters;
            };

            await _auditLogger.LogReportGenerationAsync(logEntry);
        }

        private DateTime CalculateNextRunTime(ReportSchedule schedule)
        {
            var now = DateTime.UtcNow;

            return schedule.ScheduleType switch;
            {
                ScheduleType.Once => schedule.StartTime,
                ScheduleType.Daily => CalculateNextDailyRun(schedule, now),
                ScheduleType.Weekly => CalculateNextWeeklyRun(schedule, now),
                ScheduleType.Monthly => CalculateNextMonthlyRun(schedule, now),
                ScheduleType.Hourly => now.AddHours(1),
                ScheduleType.Minutely => now.AddMinutes(schedule.IntervalMinutes ?? 5),
                ScheduleType.Custom => CalculateCustomSchedule(schedule, now),
                _ => throw new NotSupportedException($"Schedule type '{schedule.ScheduleType}' not supported")
            };
        }

        private DateTime CalculateNextDailyRun(ReportSchedule schedule, DateTime now)
        {
            var nextRun = schedule.StartTime.Date.Add(schedule.DailyTime ?? TimeSpan.FromHours(9));

            if (nextRun <= now)
            {
                nextRun = nextRun.AddDays(1);
            }

            return nextRun;
        }

        private DateTime CalculateNextWeeklyRun(ReportSchedule schedule, DateTime now)
        {
            var nextRun = schedule.StartTime;
            var targetDay = schedule.WeeklyDay ?? DayOfWeek.Monday;

            while (nextRun.DayOfWeek != targetDay || nextRun <= now)
            {
                nextRun = nextRun.AddDays(1);
            }

            return nextRun.Date.Add(schedule.DailyTime ?? TimeSpan.FromHours(9));
        }

        private DateTime CalculateNextMonthlyRun(ReportSchedule schedule, DateTime now)
        {
            var nextRun = schedule.StartTime;
            var targetDay = schedule.MonthlyDay ?? 1;

            while (nextRun.Day != targetDay || nextRun <= now)
            {
                nextRun = nextRun.AddMonths(1);
            }

            return nextRun.Date.Add(schedule.DailyTime ?? TimeSpan.FromHours(9));
        }

        private DateTime CalculateCustomSchedule(ReportSchedule schedule, DateTime now)
        {
            // Custom cron-like schedule parsing would go here;
            return now.AddMinutes(5); // Simplified;
        }

        private void ValidateReportRequest(ReportRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.ReportType))
                throw new ArgumentException("Report type cannot be empty", nameof(request));

            if (!Enum.IsDefined(typeof(ReportFormat), request.Format))
                throw new ArgumentException($"Invalid report format: {request.Format}", nameof(request));
        }

        private void ValidateSchedule(ReportSchedule schedule)
        {
            if (schedule == null)
                throw new ArgumentNullException(nameof(schedule));

            if (schedule.ReportRequest == null)
                throw new ArgumentException("Schedule must have a report request", nameof(schedule));
        }

        private void ValidateEmailOptions(EmailOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            if (!options.ToAddresses.Any())
                throw new ArgumentException("At least one recipient address is required", nameof(options));

            if (string.IsNullOrWhiteSpace(options.FromAddress))
                throw new ArgumentException("Sender address cannot be empty", nameof(options));
        }

        private async Task ValidateCollectedDataAsync(CollectedData data, ReportRequest request)
        {
            if (data == null || !data.SourceData.Any())
                throw new DataValidationException("No data collected for report");

            // Check data quality thresholds;
            if (_configuration.DataQualityThresholds != null)
            {
                foreach (var threshold in _configuration.DataQualityThresholds)
                {
                    if (!await threshold.IsSatisfiedAsync(data))
                    {
                        throw new DataQualityException($"Data quality threshold not met: {threshold.Name}");
                    }
                }
            }
        }

        private async Task<CleanedData> CleanDataAsync(CollectedData collectedData)
        {
            // Data cleaning implementation;
            // Remove duplicates, handle missing values, fix formatting issues;
            var cleanedData = new CleanedData();

            foreach (var source in collectedData.SourceData)
            {
                var cleaned = await CleanDataSourceAsync(source.Value);
                cleanedData.AddSourceData(source.Key, cleaned);
            }

            return cleanedData;
        }

        private async Task<DataTable> CleanDataSourceAsync(object data)
        {
            // Data cleaning logic;
            return new DataTable(); // Simplified;
        }

        private async Task<TransformedData> TransformDataAsync(CleanedData cleanedData, ReportRequest request)
        {
            // Data transformation implementation;
            // Normalize, scale, encode categorical variables, etc.
            var transformedData = new TransformedData();

            // Transformation logic here;

            return transformedData;
        }

        private async Task<AggregatedData> AggregateDataAsync(TransformedData transformedData, ReportRequest request)
        {
            // Data aggregation implementation;
            // Group by, sum, average, count, etc.
            var aggregatedData = new AggregatedData();

            // Aggregation logic here;

            return aggregatedData;
        }

        private async Task<EnrichedData> EnrichDataAsync(AggregatedData aggregatedData, ReportRequest request)
        {
            // Data enrichment implementation;
            // Add derived fields, calculations, lookups, etc.
            var enrichedData = new EnrichedData();

            // Enrichment logic here;

            return enrichedData;
        }

        private async Task<AnalyzedData> AnalyzeDataAsync(EnrichedData enrichedData, ReportRequest request)
        {
            // Data analysis implementation;
            // Statistical analysis, trend detection, pattern recognition, etc.
            var analyzedData = new AnalyzedData();

            // Analysis logic here;

            return analyzedData;
        }

        private async Task<FormattedForOutput> FormatForOutputAsync(AnalyzedData analyzedData, ReportRequest request)
        {
            // Format data for output;
            // Create DataTables, charts, summaries, etc.
            var formattedData = new FormattedForOutput();

            // Formatting logic here;

            return formattedData;
        }

        private double CalculateCompressionRatio(byte[] original, byte[] compressed)
        {
            if (original == null || original.Length == 0)
                return 0;

            return (double)compressed.Length / original.Length;
        }

        private double CalculateSuccessRate()
        {
            // Calculate report generation success rate;
            var total = _cache.TotalReportsGenerated + _cache.FailedGenerations;
            if (total == 0)
                return 100;

            return (double)_cache.TotalReportsGenerated / total * 100;
        }

        private string GetFileExtension(ReportFormat format)
        {
            return format switch;
            {
                ReportFormat.Pdf => "pdf",
                ReportFormat.Html => "html",
                ReportFormat.Excel => "xlsx",
                ReportFormat.Csv => "csv",
                ReportFormat.Xml => "xml",
                ReportFormat.Json => "json",
                ReportFormat.Word => "docx",
                ReportFormat.PowerPoint => "pptx",
                _ => "txt"
            };
        }

        private string GetMimeType(ReportFormat format)
        {
            return format switch;
            {
                ReportFormat.Pdf => "application/pdf",
                ReportFormat.Html => "text/html",
                ReportFormat.Excel => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ReportFormat.Csv => "text/csv",
                ReportFormat.Xml => "application/xml",
                ReportFormat.Json => "application/json",
                ReportFormat.Word => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ReportFormat.PowerPoint => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => "application/octet-stream"
            };
        }

        private string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        #endregion;
    }

    #region Core Interfaces and Classes;

    /// <summary>
    /// Report generator interface;
    /// </summary>
    public interface IReportGenerator : IDisposable
    {
        Task<ReportResult> GenerateReportAsync(ReportRequest request);
        Task<IEnumerable<ReportResult>> GenerateReportsBatchAsync(IEnumerable<ReportRequest> requests);
        Task<ScheduledReport> ScheduleReportAsync(ReportSchedule schedule);
        void CancelScheduledReport(string scheduleId);
        IEnumerable<ScheduledReport> GetScheduledReports(bool activeOnly = true);
        Task<ReportResult> ExecuteScheduledReportAsync(string scheduleId);
        void RegisterTemplate(string name, IReportTemplate template);
        void RegisterDataSource(string name, IReportDataSource dataSource);
        IEnumerable<TemplateInfo> GetAvailableTemplates();
        Task<ExportResult> ExportReportToFileAsync(ReportResult report, string filePath);
        Task<EmailResult> SendReportByEmailAsync(ReportResult report, EmailOptions options);
        ReportStatistics GetStatistics();
        Task<CacheClearResult> ClearCacheAsync();
        void StartScheduler();
        void StopScheduler();
    }

    /// <summary>
    /// Report template interface;
    /// </summary>
    public interface IReportTemplate;
    {
        string Name { get; }
        string Description { get; }
        IEnumerable<ReportFormat> SupportedFormats { get; }
        DateTime LastModified { get; }
        Task<TemplatedData> ApplyAsync(ProcessedData data, ReportRequest request);
    }

    /// <summary>
    /// Report data source interface;
    /// </summary>
    public interface IReportDataSource;
    {
        string Name { get; }
        Task<object> GetDataAsync(ReportRequest request);
    }

    /// <summary>
    /// Chart builder interface for data visualization;
    /// </summary>
    public interface IChartBuilder;
    {
        Task<byte[]> CreateChartAsync(ChartData data, ChartType type);
        Task<byte[]> CreateDashboardAsync(IEnumerable<ChartData> charts);
    }

    /// <summary>
    /// Export service interface for format conversion;
    /// </summary>
    public interface IExportService;
    {
        Task<byte[]> ExportToPdfAsync(TemplatedData data);
        Task<byte[]> ExportToHtmlAsync(TemplatedData data);
        Task<byte[]> ExportToExcelAsync(TemplatedData data);
        Task<byte[]> ExportToCsvAsync(TemplatedData data);
        Task<byte[]> ExportToXmlAsync(TemplatedData data);
        Task<byte[]> ExportToJsonAsync(TemplatedData data);
        Task<byte[]> ExportToWordAsync(TemplatedData data);
        Task<byte[]> ExportToPowerPointAsync(TemplatedData data);
    }

    #endregion;

    #region Data Models;

    /// <summary>
    /// Report request containing generation parameters;
    /// </summary>
    public class ReportRequest;
    {
        public string ReportType { get; set; }
        public ReportFormat Format { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public bool UseCache { get; set; } = true;
        public bool CompressionEnabled { get; set; } = true;
        public bool SignReport { get; set; } = false;
        public bool AddWatermark { get; set; } = false;
        public TimeSpan? CacheDuration { get; set; }
        public string RequestedBy { get; set; }
        public PriorityLevel Priority { get; set; } = PriorityLevel.Normal;
    }

    /// <summary>
    /// Report generation result;
    /// </summary>
    public class ReportResult;
    {
        public string ReportId { get; set; }
        public string GenerationId { get; set; }
        public bool IsCached { get; set; }
        public byte[] ReportData { get; set; }
        public ReportFormat Format { get; set; }
        public long Size { get; set; }
        public DateTime GeneratedAt { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Scheduled report configuration;
    /// </summary>
    public class ReportSchedule;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ReportRequest ReportRequest { get; set; }
        public ScheduleType ScheduleType { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
        public TimeSpan? DailyTime { get; set; }
        public DayOfWeek? WeeklyDay { get; set; }
        public int? MonthlyDay { get; set; }
        public int? IntervalMinutes { get; set; }
        public DistributionOptions DistributionOptions { get; set; }
        public bool RetryOnFailure { get; set; } = true;
        public int MaxRetries { get; set; } = 3;
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Scheduled report instance;
    /// </summary>
    public class ScheduledReport;
    {
        public string ScheduleId { get; set; }
        public ReportSchedule Schedule { get; set; }
        public DateTime NextRunTime { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; }
        public int RunCount { get; set; }
        public DateTime? LastRunTime { get; set; }
        public ReportRunStatus? LastRunStatus { get; set; }
        public string LastError { get; set; }
        public DateTime? CancelledAt { get; set; }
        public int ConsecutiveFailures { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Distribution options for scheduled reports;
    /// </summary>
    public class DistributionOptions;
    {
        public EmailDistribution EmailDistribution { get; set; }
        public FileSystemDistribution FileSystemDistribution { get; set; }
        public WebServiceDistribution WebServiceDistribution { get; set; }
    }

    /// <summary>
    /// Email distribution options;
    /// </summary>
    public class EmailDistribution : EmailOptions;
    {
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// File system distribution options;
    /// </summary>
    public class FileSystemDistribution;
    {
        public bool Enabled { get; set; } = true;
        public string FilePath { get; set; }
        public bool OverwriteExisting { get; set; } = true;
        public bool CreateBackup { get; set; } = false;
    }

    /// <summary>
    /// Web service distribution options;
    /// </summary>
    public class WebServiceDistribution;
    {
        public bool Enabled { get; set; } = true;
        public string EndpointUrl { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new();
        public AuthenticationType Authentication { get; set; } = AuthenticationType.None;
        public string Username { get; set; }
        public string Password { get; set; }
        public string ApiKey { get; set; }
    }

    /// <summary>
    /// Email options for report distribution;
    /// </summary>
    public class EmailOptions;
    {
        public IEnumerable<string> ToAddresses { get; set; }
        public IEnumerable<string> CcAddresses { get; set; }
        public IEnumerable<string> BccAddresses { get; set; }
        public string FromAddress { get; set; }
        public string FromName { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public bool IsHtml { get; set; } = true;
        public SmtpConfiguration SmtpConfiguration { get; set; }
    }

    /// <summary>
    /// SMTP configuration for email delivery;
    /// </summary>
    public class SmtpConfiguration;
    {
        public string Host { get; set; }
        public int Port { get; set; } = 587;
        public bool EnableSsl { get; set; } = true;
        public string Username { get; set; }
        public string Password { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Report generator configuration;
    /// </summary>
    public class ReportGeneratorConfiguration;
    {
        public TimeSpan? SchedulerInterval { get; set; }
        public TimeSpan? CleanupInterval { get; set; }
        public TimeSpan? CacheUpdateInterval { get; set; }
        public bool CacheEnabled { get; set; } = true;
        public CacheConfiguration CacheConfiguration { get; set; } = new();
        public bool PreCacheEnabled { get; set; } = false;
        public int PreCacheThreshold { get; set; } = 5;
        public bool CompressionEnabled { get; set; } = true;
        public bool DigitalSignatureEnabled { get; set; } = false;
        public bool WatermarkEnabled { get; set; } = false;
        public List<TemplateConfig> CustomTemplates { get; set; } = new();
        public List<DataSourceConfig> CustomDataSources { get; set; } = new();
        public List<IDataQualityThreshold> DataQualityThresholds { get; set; } = new();

        public static ReportGeneratorConfiguration Default => new()
        {
            SchedulerInterval = TimeSpan.FromMinutes(1),
            CleanupInterval = TimeSpan.FromHours(1),
            CacheUpdateInterval = TimeSpan.FromMinutes(5),
            CacheEnabled = true,
            PreCacheEnabled = false,
            PreCacheThreshold = 5,
            CompressionEnabled = true,
            DigitalSignatureEnabled = false,
            WatermarkEnabled = false;
        };
    }

    /// <summary>
    /// Cache configuration;
    /// </summary>
    public class CacheConfiguration;
    {
        public long MaxSize { get; set; } = 100 * 1024 * 1024; // 100 MB;
        public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromHours(1);
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(30);
        public int MaxEntries { get; set; } = 1000;
        public bool EnableStatistics { get; set; } = true;
    }

    /// <summary>
    /// Template configuration;
    /// </summary>
    public class TemplateConfig;
    {
        public string Name { get; set; }
        public string TemplateClass { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    /// <summary>
    /// Data source configuration;
    /// </summary>
    public class DataSourceConfig;
    {
        public string Name { get; set; }
        public string DataSourceClass { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    /// <summary>
    /// Template information;
    /// </summary>
    public class TemplateInfo;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public IEnumerable<ReportFormat> SupportedFormats { get; set; }
        public DateTime LastModified { get; set; }
    }

    /// <summary>
    /// Export result;
    /// </summary>
    public class ExportResult;
    {
        public string ExportId { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public ReportFormat Format { get; set; }
        public TimeSpan ExportTime { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>
    /// Email result;
    /// </summary>
    public class EmailResult;
    {
        public string EmailId { get; set; }
        public string ReportId { get; set; }
        public int RecipientCount { get; set; }
        public DateTime SentAt { get; set; }
        public TimeSpan DeliveryTime { get; set; }
    }

    /// <summary>
    /// Cache clear result;
    /// </summary>
    public class CacheClearResult;
    {
        public string ClearId { get; set; }
        public int ClearedItems { get; set; }
        public TimeSpan ClearTime { get; set; }
        public DateTime ClearedAt { get; set; }
    }

    /// <summary>
    /// Report statistics;
    /// </summary>
    public class ReportStatistics;
    {
        public int TotalReportsGenerated { get; set; }
        public double CacheHitRate { get; set; }
        public long CacheSize { get; set; }
        public long CacheMaxSize { get; set; }
        public int ScheduledReportsCount { get; set; }
        public int TemplatesCount { get; set; }
        public int DataSourcesCount { get; set; }
        public DateTime? LastCleanupTime { get; set; }
        public double GenerationSuccessRate { get; set; }
    }

    /// <summary>
    /// Report generation log entry
    /// </summary>
    public class ReportGenerationLog;
    {
        public string ReportId { get; set; }
        public string GenerationId { get; set; }
        public string ReportType { get; set; }
        public ReportFormat Format { get; set; }
        public long Size { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public DateTime GeneratedAt { get; set; }
        public bool IsCached { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Report types;
    /// </summary>
    public enum ReportType;
    {
        Performance,
        Health,
        Metrics,
        Audit,
        Diagnostic,
        ExecutiveSummary,
        DetailedAnalysis,
        Custom;
    }

    /// <summary>
    /// Report formats;
    /// </summary>
    public enum ReportFormat;
    {
        Pdf,
        Html,
        Excel,
        Csv,
        Xml,
        Json,
        Word,
        PowerPoint,
        Text;
    }

    /// <summary>
    /// Schedule types;
    /// </summary>
    public enum ScheduleType;
    {
        Once,
        Daily,
        Weekly,
        Monthly,
        Hourly,
        Minutely,
        Custom;
    }

    /// <summary>
    /// Report run status;
    /// </summary>
    public enum ReportRunStatus;
    {
        Pending,
        Running,
        Success,
        Failed,
        Cancelled;
    }

    /// <summary>
    /// Priority levels;
    /// </summary>
    public enum PriorityLevel;
    {
        Low,
        Normal,
        High,
        Critical;
    }

    /// <summary>
    /// Authentication types for web service distribution;
    /// </summary>
    public enum AuthenticationType;
    {
        None,
        Basic,
        Bearer,
        ApiKey;
    }

    /// <summary>
    /// Chart types for data visualization;
    /// </summary>
    public enum ChartType;
    {
        Line,
        Bar,
        Pie,
        Area,
        Scatter,
        HeatMap,
        Gauge,
        Donut;
    }

    #endregion;

    #region Data Processing Classes;

    /// <summary>
    /// Collected data from sources;
    /// </summary>
    public class CollectedData;
    {
        public Dictionary<string, object> SourceData { get; } = new();
        public Dictionary<string, object> Metadata { get; } = new();
        public int DataPoints { get; set; }
        public TimeSpan CollectionDuration { get; set; }
        public byte[] RawData { get; set; }

        public void AddSourceData(string sourceName, object data)
        {
            SourceData[sourceName] = data;
            DataPoints += EstimateDataPoints(data);
        }

        private int EstimateDataPoints(object data)
        {
            // Estimate data points based on data type;
            if (data is DataTable table)
                return table.Rows.Count;
            if (data is IEnumerable<object> collection)
                return collection.Count();
            return 1;
        }
    }

    /// <summary>
    /// Processed data after all transformations;
    /// </summary>
    public class ProcessedData;
    {
        public CollectedData RawData { get; set; }
        public CleanedData CleanedData { get; set; }
        public TransformedData TransformedData { get; set; }
        public AggregatedData AggregatedData { get; set; }
        public EnrichedData EnrichedData { get; set; }
        public AnalyzedData AnalyzedData { get; set; }
        public FormattedForOutput FormattedData { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<string> ProcessingSteps { get; set; } = new();
        public TimeSpan ProcessingDuration { get; set; }
    }

    /// <summary>
    /// Templated data after template application;
    /// </summary>
    public class TemplatedData;
    {
        public ProcessedData ProcessedData { get; set; }
        public string TemplateName { get; set; }
        public Dictionary<string, object> TemplateParameters { get; set; } = new();
        public byte[] RenderedContent { get; set; }
        public List<ChartData> Charts { get; set; } = new();
        public TimeSpan TemplatingDuration { get; set; }
    }

    /// <summary>
    /// Formatted data ready for output;
    /// </summary>
    public class FormattedData;
    {
        public byte[] Data { get; set; }
        public ReportFormat Format { get; set; }
        public long Size { get; set; }
        public TimeSpan FormattingDuration { get; set; }
    }

    /// <summary>
    /// Chart data for visualization;
    /// </summary>
    public class ChartData;
    {
        public string Title { get; set; }
        public ChartType Type { get; set; }
        public Dictionary<string, double> DataSeries { get; set; } = new();
        public Dictionary<string, object> Options { get; set; } = new();
        public string XAxisLabel { get; set; }
        public string YAxisLabel { get; set; }
        public bool ShowLegend { get; set; } = true;
        public bool ShowGrid { get; set; } = true;
    }

    // Additional data processing classes would be defined here...
    // CleanedData, TransformedData, AggregatedData, EnrichedData, AnalyzedData, FormattedForOutput;

    #endregion;

    #region Exception Classes;

    /// <summary>
    /// Report generation exception;
    /// </summary>
    public class ReportGenerationException : Exception
    {
        public ReportGenerationException(string message) : base(message) { }
        public ReportGenerationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Template not found exception;
    /// </summary>
    public class TemplateNotFoundException : Exception
    {
        public TemplateNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Schedule not found exception;
    /// </summary>
    public class ScheduleNotFoundException : Exception
    {
        public ScheduleNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Report export exception;
    /// </summary>
    public class ReportExportException : Exception
    {
        public ReportExportException(string message) : base(message) { }
        public ReportExportException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Email delivery exception;
    /// </summary>
    public class EmailDeliveryException : Exception
    {
        public EmailDeliveryException(string message) : base(message) { }
        public EmailDeliveryException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Web service exception;
    /// </summary>
    public class WebServiceException : Exception
    {
        public WebServiceException(string message) : base(message) { }
    }

    /// <summary>
    /// Cache clear exception;
    /// </summary>
    public class CacheClearException : Exception
    {
        public CacheClearException(string message) : base(message) { }
        public CacheClearException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Data collection exception;
    /// </summary>
    public class DataCollectionException : Exception
    {
        public DataCollectionException(string message) : base(message) { }
        public DataCollectionException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Data processing exception;
    /// </summary>
    public class DataProcessingException : Exception
    {
        public DataProcessingException(string message) : base(message) { }
        public DataProcessingException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Template application exception;
    /// </summary>
    public class TemplateApplicationException : Exception
    {
        public TemplateApplicationException(string message) : base(message) { }
        public TemplateApplicationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Format not supported exception;
    /// </summary>
    public class FormatNotSupportedException : Exception
    {
        public FormatNotSupportedException(string message) : base(message) { }
    }

    /// <summary>
    /// Format conversion exception;
    /// </summary>
    public class FormatConversionException : Exception
    {
        public FormatConversionException(string message) : base(message) { }
        public FormatConversionException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Report assembly exception;
    /// </summary>
    public class ReportAssemblyException : Exception
    {
        public ReportAssemblyException(string message) : base(message) { }
        public ReportAssemblyException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Distribution exception;
    /// </summary>
    public class DistributionException : Exception
    {
        public DistributionException(string message) : base(message) { }
        public DistributionException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Data validation exception;
    /// </summary>
    public class DataValidationException : Exception
    {
        public DataValidationException(string message) : base(message) { }
    }

    /// <summary>
    /// Data quality exception;
    /// </summary>
    public class DataQualityException : Exception
    {
        public DataQualityException(string message) : base(message) { }
    }

    #endregion;

    #region Event Classes;

    /// <summary>
    /// Report generated event;
    /// </summary>
    public class ReportGeneratedEvent;
    {
        public string ReportId { get; set; }
        public string GenerationId { get; set; }
        public string ReportType { get; set; }
        public ReportFormat Format { get; set; }
        public long Size { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public bool IsCached { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Report generation failed event;
    /// </summary>
    public class ReportGenerationFailedEvent;
    {
        public string GenerationId { get; set; }
        public string ReportType { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Batch report generated event;
    /// </summary>
    public class BatchReportGeneratedEvent;
    {
        public string BatchId { get; set; }
        public int ReportCount { get; set; }
        public TimeSpan TotalGenerationTime { get; set; }
        public double AverageReportTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Report scheduled event;
    /// </summary>
    public class ReportScheduledEvent;
    {
        public string ScheduleId { get; set; }
        public string ReportType { get; set; }
        public ScheduleType ScheduleType { get; set; }
        public DateTime NextRunTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Report schedule cancelled event;
    /// </summary>
    public class ReportScheduleCancelledEvent;
    {
        public string ScheduleId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Scheduled report run event;
    /// </summary>
    public class ScheduledReportRunEvent;
    {
        public string ScheduleId { get; set; }
        public string ReportId { get; set; }
        public DateTime RunTime { get; set; }
        public ReportRunStatus Status { get; set; }
        public string Error { get; set; }
        public DateTime NextRunTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Report exported event;
    /// </summary>
    public class ReportExportedEvent;
    {
        public string ExportId { get; set; }
        public string ReportId { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public ReportFormat Format { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Report emailed event;
    /// </summary>
    public class ReportEmailedEvent;
    {
        public string EmailId { get; set; }
        public string ReportId { get; set; }
        public int RecipientCount { get; set; }
        public ReportFormat Format { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Cache cleared event;
    /// </summary>
    public class CacheClearedEvent;
    {
        public string ClearId { get; set; }
        public int ClearedItems { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Internal Supporting Classes;

    /// <summary>
    /// Report cache implementation;
    /// </summary>
    internal class ReportCache : IDisposable
    {
        private readonly Dictionary<string, CacheEntry> _cache = new();
        private readonly CacheConfiguration _configuration;
        private readonly object _syncLock = new();
        private long _currentSize;
        private int _hitCount;
        private int _missCount;

        public long CurrentSize => _currentSize;
        public long MaxSize => _configuration.MaxSize;
        public int HitCount => _hitCount;
        public int MissCount => _missCount;
        public int TotalRequests => _hitCount + _missCount;
        public double HitRate => TotalRequests > 0 ? (double)_hitCount / TotalRequests * 100 : 0;
        public int TotalReportsGenerated { get; private set; }
        public int FailedGenerations { get; private set; }
        public DateTime? LastCleanupTime { get; private set; }

        public ReportCache(CacheConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<ReportResult> GetAsync(ReportRequest request)
        {
            var cacheKey = GenerateCacheKey(request);

            lock (_syncLock)
            {
                if (_cache.TryGetValue(cacheKey, out var entry))
                {
                    if (entry.ExpirationTime > DateTime.UtcNow)
                    {
                        _hitCount++;
                        return entry.ReportResult;
                    }
                    else;
                    {
                        // Entry expired, remove it;
                        _cache.Remove(cacheKey);
                        _currentSize -= entry.Size;
                    }
                }

                _missCount++;
                return null;
            }
        }

        public async Task StoreAsync(ReportRequest request, ReportResult result)
        {
            var cacheKey = GenerateCacheKey(request);
            var expirationTime = DateTime.UtcNow.Add(request.CacheDuration ?? _configuration.DefaultDuration);
            var size = result.Size;

            lock (_syncLock)
            {
                // Check if we need to make space;
                while (_currentSize + size > _configuration.MaxSize && _cache.Count > 0)
                {
                    RemoveOldestEntry();
                }

                // Check if we've reached max entries;
                if (_cache.Count >= _configuration.MaxSize)
                {
                    RemoveOldestEntry();
                }

                var entry = new CacheEntry
                {
                    Key = cacheKey,
                    ReportResult = result,
                    ExpirationTime = expirationTime,
                    Size = size,
                    StoredAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow;
                };

                _cache[cacheKey] = entry
                _currentSize += size;

                TotalReportsGenerated++;
            }
        }

        public async Task<bool> ContainsAsync(ReportRequest request)
        {
            var cacheKey = GenerateCacheKey(request);

            lock (_syncLock)
            {
                return _cache.ContainsKey(cacheKey);
            }
        }

        public async Task<int> CleanupAsync()
        {
            lock (_syncLock)
            {
                var expiredKeys = _cache.Where(kvp => kvp.Value.ExpirationTime <= DateTime.UtcNow)
                                       .Select(kvp => kvp.Key)
                                       .ToList();

                foreach (var key in expiredKeys)
                {
                    if (_cache.TryGetValue(key, out var entry))
                    {
                        _currentSize -= entry.Size;
                    }
                    _cache.Remove(key);
                }

                LastCleanupTime = DateTime.UtcNow;
                return expiredKeys.Count;
            }
        }

        public async Task<int> ClearAsync()
        {
            lock (_syncLock)
            {
                var count = _cache.Count;
                _cache.Clear();
                _currentSize = 0;
                return count;
            }
        }

        public async Task UpdateStatisticsAsync()
        {
            // Update cache statistics;
            // This could involve logging, monitoring, etc.
        }

        public IEnumerable<ReportRequest> GetFrequentRequests(int threshold)
        {
            // Return frequently requested reports;
            // This would track request frequencies;
            return Enumerable.Empty<ReportRequest>();
        }

        private string GenerateCacheKey(ReportRequest request)
        {
            using var sha256 = SHA256.Create();
            var keyData = $"{request.ReportType}:{request.Format}:{string.Join(":", request.Parameters.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}";
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyData));
            return Convert.ToBase64String(hash);
        }

        private void RemoveOldestEntry()
        {
            if (_cache.Count == 0)
                return;

            var oldest = _cache.OrderBy(kvp => kvp.Value.LastAccessed).First();
            _currentSize -= oldest.Value.Size;
            _cache.Remove(oldest.Key);
        }

        public void Dispose()
        {
            _cache.Clear();
            GC.SuppressFinalize(this);
        }

        private class CacheEntry
        {
            public string Key { get; set; }
            public ReportResult ReportResult { get; set; }
            public DateTime ExpirationTime { get; set; }
            public long Size { get; set; }
            public DateTime StoredAt { get; set; }
            public DateTime LastAccessed { get; set; }
        }
    }

    /// <summary>
    /// Data quality threshold interface;
    /// </summary>
    public interface IDataQualityThreshold;
    {
        string Name { get; }
        Task<bool> IsSatisfiedAsync(CollectedData data);
    }

    /// <summary>
    /// Default chart builder implementation;
    /// </summary>
    internal class DefaultChartBuilder : IChartBuilder;
    {
        public Task<byte[]> CreateChartAsync(ChartData data, ChartType type)
        {
            // Chart generation implementation;
            // Could use libraries like Chart.js, D3.js, or System.Drawing;
            return Task.FromResult(new byte[0]);
        }

        public Task<byte[]> CreateDashboardAsync(IEnumerable<ChartData> charts)
        {
            // Dashboard creation implementation;
            return Task.FromResult(new byte[0]);
        }
    }

    /// <summary>
    /// Default export service implementation;
    /// </summary>
    internal class DefaultExportService : IExportService;
    {
        public Task<byte[]> ExportToPdfAsync(TemplatedData data)
        {
            // PDF export implementation using libraries like iTextSharp or PDFSharp;
            return Task.FromResult(new byte[0]);
        }

        public Task<byte[]> ExportToHtmlAsync(TemplatedData data)
        {
            // HTML export implementation;
            return Task.FromResult(Encoding.UTF8.GetBytes("<html><body>Report</body></html>"));
        }

        public Task<byte[]> ExportToExcelAsync(TemplatedData data)
        {
            // Excel export implementation using libraries like EPPlus or ClosedXML;
            return Task.FromResult(new byte[0]);
        }

        public Task<byte[]> ExportToCsvAsync(TemplatedData data)
        {
            // CSV export implementation;
            return Task.FromResult(Encoding.UTF8.GetBytes("data1,data2,data3"));
        }

        public Task<byte[]> ExportToXmlAsync(TemplatedData data)
        {
            // XML export implementation;
            return Task.FromResult(Encoding.UTF8.GetBytes("<report><data>value</data></report>"));
        }

        public Task<byte[]> ExportToJsonAsync(TemplatedData data)
        {
            // JSON export implementation;
            return Task.FromResult(Encoding.UTF8.GetBytes("{ \"report\": \"data\" }"));
        }

        public Task<byte[]> ExportToWordAsync(TemplatedData data)
        {
            // Word export implementation;
            return Task.FromResult(new byte[0]);
        }

        public Task<byte[]> ExportToPowerPointAsync(TemplatedData data)
        {
            // PowerPoint export implementation;
            return Task.FromResult(new byte[0]);
        }
    }

    // Built-in template implementations would go here...
    // PerformanceReportTemplate, HealthReportTemplate, etc.

    // Built-in data source implementations would go here...
    // PerformanceDataSource, HealthDataSource, etc.

    #endregion;
}
