// NEDA.Monitoring/Reporting/ExportService.cs;

using ClosedXML.Excel;
using CsvHelper;
using iTextSharp.text;
using iTextSharp.text.pdf;
using NEDA.Common.Utilities;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.Logging;
using NEDA.Services.Messaging.EventBus;
using Newtonsoft.Json;
using OpenCvSharp.ImgHash;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Xml;
using System.Xml.Serialization;

namespace NEDA.Monitoring.Reporting;
{
    /// <summary>
    /// Advanced export service for generating and exporting reports in multiple formats;
    /// Provides comprehensive export capabilities for system data, metrics, and reports;
    /// </summary>
    public class ExportService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ExportConfiguration _configuration;
        private readonly List<ExportTemplate> _templates;
        private readonly Dictionary<string, IExportFormatHandler> _formatHandlers;
        private readonly SemaphoreSlim _exportLock = new SemaphoreSlim(1, 1);

        private bool _isDisposed;
        private DateTime _lastCleanupTime;
        private readonly object _templateLock = new object();

        /// <summary>
        /// Event triggered when export operation starts;
        /// </summary>
        public event EventHandler<ExportStartedEventArgs> OnExportStarted;

        /// <summary>
        /// Event triggered when export operation completes;
        /// </summary>
        public event EventHandler<ExportCompletedEventArgs> OnExportCompleted;

        /// <summary>
        /// Event triggered when export operation fails;
        /// </summary>
        public event EventHandler<ExportFailedEventArgs> OnExportFailed;

        /// <summary>
        /// Event triggered during export progress;
        /// </summary>
        public event EventHandler<ExportProgressEventArgs> OnExportProgress;

        /// <summary>
        /// Current service status;
        /// </summary>
        public ExportServiceStatus Status { get; private set; }

        /// <summary>
        /// Export service configuration;
        /// </summary>
        public ExportConfiguration Configuration => _configuration;

        /// <summary>
        /// Initialize export service with dependencies;
        /// </summary>
        public ExportService(ILogger logger, IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _configuration = LoadConfiguration();
            _templates = new List<ExportTemplate>();
            _formatHandlers = new Dictionary<string, IExportFormatHandler>();

            InitializeFormatHandlers();
            LoadTemplates();

            Status = ExportServiceStatus.Ready;
            _logger.LogInformation("ExportService initialized successfully", "ExportService");
        }

        /// <summary>
        /// Export data to specified format;
        /// </summary>
        public async Task<ExportResult> ExportAsync<T>(
            IEnumerable<T> data,
            ExportOptions options,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var exportId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                Status = ExportServiceStatus.Exporting;

                // Trigger start event;
                OnExportStarted?.Invoke(this, new ExportStartedEventArgs;
                {
                    ExportId = exportId,
                    Format = options.Format,
                    Options = options,
                    StartTime = startTime;
                });

                _logger.LogInformation($"Starting export {exportId} to {options.Format}", "ExportService");

                // Get format handler;
                var handler = GetFormatHandler(options.Format);
                if (handler == null)
                    throw new FormatNotSupportedException($"Format '{options.Format}' is not supported");

                // Prepare export context;
                var context = new ExportContext<T>
                {
                    ExportId = exportId,
                    Data = data,
                    Options = options,
                    CancellationToken = cancellationToken;
                };

                // Execute export;
                var result = await ExecuteExportAsync(handler, context);

                // Update result with additional information;
                result.ExportId = exportId;
                result.Duration = DateTime.UtcNow - startTime;

                // Trigger completion event;
                OnExportCompleted?.Invoke(this, new ExportCompletedEventArgs;
                {
                    ExportId = exportId,
                    Result = result,
                    CompletionTime = DateTime.UtcNow;
                });

                _logger.LogInformation($"Export {exportId} completed successfully: {result.FileSizeBytes} bytes",
                    "ExportService");

                Status = ExportServiceStatus.Ready;
                return result;
            }
            catch (OperationCanceledException)
            {
                Status = ExportServiceStatus.Ready;
                _logger.LogInformation($"Export {exportId} was cancelled", "ExportService");

                throw;
            }
            catch (Exception ex)
            {
                Status = ExportServiceStatus.Error;

                // Trigger failure event;
                OnExportFailed?.Invoke(this, new ExportFailedEventArgs;
                {
                    ExportId = exportId,
                    Format = options.Format,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    FailureTime = DateTime.UtcNow;
                });

                _logger.LogError($"Export {exportId} failed: {ex.Message}", "ExportService", ex);
                throw new ExportException(ExportErrorCodes.ExportFailed, $"Export failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Export data using a template;
        /// </summary>
        public async Task<ExportResult> ExportWithTemplateAsync<T>(
            IEnumerable<T> data,
            string templateName,
            Dictionary<string, object> templateParameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(templateName))
                throw new ArgumentException("Template name cannot be empty", nameof(templateName));

            var template = GetTemplate(templateName);
            if (template == null)
                throw new TemplateNotFoundException($"Template '{templateName}' not found");

            var options = new ExportOptions;
            {
                Format = template.Format,
                FileName = template.OutputFileName,
                TemplateName = templateName,
                Parameters = templateParameters ?? new Dictionary<string, object>()
            };

            // Merge template parameters with provided parameters;
            if (template.DefaultParameters != null)
            {
                foreach (var param in template.DefaultParameters)
                {
                    if (!options.Parameters.ContainsKey(param.Key))
                    {
                        options.Parameters[param.Key] = param.Value;
                    }
                }
            }

            return await ExportAsync(data, options, cancellationToken);
        }

        /// <summary>
        /// Export data to multiple formats in batch;
        /// </summary>
        public async Task<List<ExportResult>> ExportToMultipleFormatsAsync<T>(
            IEnumerable<T> data,
            IEnumerable<string> formats,
            ExportOptions baseOptions = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var results = new List<ExportResult>();
            var tasks = new List<Task<ExportResult>>();

            foreach (var format in formats.Distinct())
            {
                var options = baseOptions?.Clone() ?? new ExportOptions();
                options.Format = format;

                tasks.Add(ExportAsync(data, options, cancellationToken));
            }

            try
            {
                results = (await Task.WhenAll(tasks)).ToList();
            }
            catch (AggregateException ex)
            {
                _logger.LogError($"Batch export failed: {ex.Message}", "ExportService", ex);
                throw new ExportException(ExportErrorCodes.BatchExportFailed, "Batch export failed", ex);
            }

            return results;
        }

        /// <summary>
        /// Stream export for large datasets;
        /// </summary>
        public async Task<StreamingExportResult> StreamExportAsync<T>(
            IAsyncEnumerable<T> dataStream,
            ExportOptions options,
            IProgress<ExportProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var exportId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                Status = ExportServiceStatus.Exporting;

                // Trigger start event;
                OnExportStarted?.Invoke(this, new ExportStartedEventArgs;
                {
                    ExportId = exportId,
                    Format = options.Format,
                    Options = options,
                    StartTime = startTime,
                    IsStreaming = true;
                });

                _logger.LogInformation($"Starting streaming export {exportId} to {options.Format}", "ExportService");

                // Get format handler;
                var handler = GetFormatHandler(options.Format);
                if (handler == null)
                    throw new FormatNotSupportedException($"Format '{options.Format}' is not supported");

                // Create temporary file for streaming output;
                var tempFilePath = Path.Combine(_configuration.TempDirectory, $"{exportId}_{Guid.NewGuid():N}.tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath));

                var streamingResult = new StreamingExportResult;
                {
                    ExportId = exportId,
                    Format = options.Format,
                    FilePath = tempFilePath,
                    StartTime = startTime;
                };

                // Execute streaming export;
                await ExecuteStreamingExportAsync(handler, dataStream, tempFilePath, options,
                    progress, cancellationToken);

                streamingResult.EndTime = DateTime.UtcNow;
                streamingResult.Duration = streamingResult.EndTime - streamingResult.StartTime;

                // Get file info;
                var fileInfo = new FileInfo(tempFilePath);
                streamingResult.FileSizeBytes = fileInfo.Length;
                streamingResult.Success = true;

                // Trigger completion event;
                OnExportCompleted?.Invoke(this, new ExportCompletedEventArgs;
                {
                    ExportId = exportId,
                    Result = new ExportResult;
                    {
                        ExportId = exportId,
                        Format = options.Format,
                        FilePath = tempFilePath,
                        FileSizeBytes = streamingResult.FileSizeBytes,
                        Success = true;
                    },
                    CompletionTime = DateTime.UtcNow,
                    IsStreaming = true;
                });

                _logger.LogInformation($"Streaming export {exportId} completed: {streamingResult.FileSizeBytes} bytes",
                    "ExportService");

                Status = ExportServiceStatus.Ready;
                return streamingResult;
            }
            catch (OperationCanceledException)
            {
                Status = ExportServiceStatus.Ready;
                _logger.LogInformation($"Streaming export {exportId} was cancelled", "ExportService");
                throw;
            }
            catch (Exception ex)
            {
                Status = ExportServiceStatus.Error;

                OnExportFailed?.Invoke(this, new ExportFailedEventArgs;
                {
                    ExportId = exportId,
                    Format = options.Format,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    FailureTime = DateTime.UtcNow,
                    IsStreaming = true;
                });

                _logger.LogError($"Streaming export {exportId} failed: {ex.Message}", "ExportService", ex);
                throw new ExportException(ExportErrorCodes.StreamingExportFailed, "Streaming export failed", ex);
            }
        }

        /// <summary>
        /// Register a custom export template;
        /// </summary>
        public void RegisterTemplate(ExportTemplate template)
        {
            ValidateNotDisposed();

            if (template == null)
                throw new ArgumentNullException(nameof(template));

            lock (_templateLock)
            {
                // Remove existing template with same name;
                _templates.RemoveAll(t => t.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase));

                // Add new template;
                _templates.Add(template);

                _logger.LogInformation($"Registered template: {template.Name}", "ExportService");
            }
        }

        /// <summary>
        /// Unregister a template;
        /// </summary>
        public bool UnregisterTemplate(string templateName)
        {
            ValidateNotDisposed();

            lock (_templateLock)
            {
                var removed = _templates.RemoveAll(t => t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));

                if (removed > 0)
                {
                    _logger.LogInformation($"Unregistered template: {templateName}", "ExportService");
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Get all available templates;
        /// </summary>
        public List<ExportTemplate> GetTemplates()
        {
            ValidateNotDisposed();

            lock (_templateLock)
            {
                return new List<ExportTemplate>(_templates);
            }
        }

        /// <summary>
        /// Get template by name;
        /// </summary>
        public ExportTemplate GetTemplate(string templateName)
        {
            ValidateNotDisposed();

            lock (_templateLock)
            {
                return _templates.FirstOrDefault(t =>
                    t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Register a custom format handler;
        /// </summary>
        public void RegisterFormatHandler(IExportFormatHandler handler)
        {
            ValidateNotDisposed();

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            _formatHandlers[handler.Format] = handler;
            _logger.LogInformation($"Registered format handler for {handler.Format}", "ExportService");
        }

        /// <summary>
        /// Get available export formats;
        /// </summary>
        public List<string> GetAvailableFormats()
        {
            ValidateNotDisposed();

            return _formatHandlers.Keys.ToList();
        }

        /// <summary>
        /// Cleanup temporary files;
        /// </summary>
        public void CleanupTempFiles(TimeSpan olderThan)
        {
            ValidateNotDisposed();

            try
            {
                var cutoffTime = DateTime.UtcNow - olderThan;
                var tempDir = _configuration.TempDirectory;

                if (!Directory.Exists(tempDir))
                    return;

                var files = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
                var deletedCount = 0;

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastWriteTimeUtc < cutoffTime)
                        {
                            fileInfo.Delete();
                            deletedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to delete temp file {file}: {ex.Message}", "ExportService");
                    }
                }

                _lastCleanupTime = DateTime.UtcNow;

                if (deletedCount > 0)
                {
                    _logger.LogInformation($"Cleaned up {deletedCount} temporary files", "ExportService");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Temp file cleanup failed: {ex.Message}", "ExportService", ex);
            }
        }

        /// <summary>
        /// Generate export preview;
        /// </summary>
        public async Task<ExportPreview> GeneratePreviewAsync<T>(
            IEnumerable<T> sampleData,
            ExportOptions options,
            int sampleSize = 10,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (sampleData == null)
                throw new ArgumentNullException(nameof(sampleData));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            try
            {
                var sample = sampleData.Take(sampleSize).ToList();
                var handler = GetFormatHandler(options.Format);

                if (handler == null)
                    throw new FormatNotSupportedException($"Format '{options.Format}' is not supported");

                var preview = new ExportPreview;
                {
                    Format = options.Format,
                    SampleSize = sample.Count,
                    EstimatedSizeBytes = EstimateSize(sample),
                    Supported = handler.CanHandle(options.Format),
                    SampleData = await GeneratePreviewDataAsync(handler, sample, options, cancellationToken)
                };

                return preview;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Preview generation failed: {ex.Message}", "ExportService", ex);
                throw new ExportException(ExportErrorCodes.PreviewFailed, "Preview generation failed", ex);
            }
        }

        /// <summary>
        /// Validate export options;
        /// </summary>
        public ExportValidationResult ValidateExportOptions(ExportOptions options)
        {
            ValidateNotDisposed();

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var result = new ExportValidationResult;
            {
                Options = options,
                IsValid = true;
            };

            // Validate format;
            if (string.IsNullOrWhiteSpace(options.Format))
            {
                result.IsValid = false;
                result.Errors.Add("Export format is required");
            }
            else if (!_formatHandlers.ContainsKey(options.Format))
            {
                result.IsValid = false;
                result.Errors.Add($"Format '{options.Format}' is not supported");
            }

            // Validate file name;
            if (string.IsNullOrWhiteSpace(options.FileName))
            {
                result.IsValid = false;
                result.Errors.Add("File name is required");
            }
            else if (options.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                result.IsValid = false;
                result.Errors.Add("File name contains invalid characters");
            }

            // Validate output directory;
            if (!string.IsNullOrWhiteSpace(options.OutputDirectory) && !Directory.Exists(options.OutputDirectory))
            {
                try
                {
                    Directory.CreateDirectory(options.OutputDirectory);
                }
                catch (Exception ex)
                {
                    result.IsValid = false;
                    result.Errors.Add($"Output directory cannot be created: {ex.Message}");
                }
            }

            return result;
        }

        #region Private Methods;

        private ExportConfiguration LoadConfiguration()
        {
            return new ExportConfiguration;
            {
                TempDirectory = Path.Combine(Path.GetTempPath(), "NEDA", "Exports"),
                MaxConcurrentExports = Environment.ProcessorCount * 2,
                DefaultExportDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NEDA Exports"),
                MaxFileSizeBytes = 100L * 1024 * 1024, // 100 MB;
                EnableCompression = true,
                KeepTempFilesDays = 7,
                EnableDetailedLogging = true;
            };
        }

        private void InitializeFormatHandlers()
        {
            // Register built-in format handlers;
            RegisterFormatHandler(new CsvExportHandler());
            RegisterFormatHandler(new JsonExportHandler());
            RegisterFormatHandler(new XmlExportHandler());
            RegisterFormatHandler(new ExcelExportHandler());
            RegisterFormatHandler(new PdfExportHandler());
            RegisterFormatHandler(new HtmlExportHandler());
        }

        private void LoadTemplates()
        {
            // Load built-in templates;
            var builtInTemplates = new[]
            {
                new ExportTemplate;
                {
                    Name = "SystemMetrics_CSV",
                    DisplayName = "System Metrics (CSV)",
                    Description = "Export system metrics in CSV format",
                    Format = "CSV",
                    OutputFileName = "system_metrics_{timestamp:yyyyMMdd_HHmmss}.csv",
                    DefaultParameters = new Dictionary<string, object>
                    {
                        ["IncludeHeaders"] = true,
                        ["Delimiter"] = ",",
                        ["TimestampFormat"] = "yyyy-MM-dd HH:mm:ss"
                    }
                },
                new ExportTemplate;
                {
                    Name = "PerformanceReport_Excel",
                    DisplayName = "Performance Report (Excel)",
                    Description = "Export performance data to Excel with formatting",
                    Format = "Excel",
                    OutputFileName = "performance_report_{timestamp:yyyyMMdd}.xlsx",
                    DefaultParameters = new Dictionary<string, object>
                    {
                        ["IncludeCharts"] = true,
                        ["AutoFitColumns"] = true,
                        ["SheetName"] = "Performance Data"
                    }
                },
                new ExportTemplate;
                {
                    Name = "AuditLog_JSON",
                    DisplayName = "Audit Log (JSON)",
                    Description = "Export audit logs in JSON format",
                    Format = "JSON",
                    OutputFileName = "audit_log_{timestamp:yyyyMMdd}.json",
                    DefaultParameters = new Dictionary<string, object>
                    {
                        ["Indented"] = true,
                        ["IncludeMetadata"] = true;
                    }
                },
                new ExportTemplate;
                {
                    Name = "HealthReport_PDF",
                    DisplayName = "Health Report (PDF)",
                    Description = "Generate health report in PDF format",
                    Format = "PDF",
                    OutputFileName = "health_report_{timestamp:yyyyMMdd}.pdf",
                    DefaultParameters = new Dictionary<string, object>
                    {
                        ["IncludeCoverPage"] = true,
                        ["PageSize"] = "A4",
                        ["Orientation"] = "Portrait"
                    }
                }
            };

            foreach (var template in builtInTemplates)
            {
                RegisterTemplate(template);
            }
        }

        private IExportFormatHandler GetFormatHandler(string format)
        {
            if (_formatHandlers.TryGetValue(format, out var handler))
                return handler;

            // Try case-insensitive match;
            var matchingKey = _formatHandlers.Keys;
                .FirstOrDefault(k => k.Equals(format, StringComparison.OrdinalIgnoreCase));

            return matchingKey != null ? _formatHandlers[matchingKey] : null;
        }

        private async Task<ExportResult> ExecuteExportAsync<T>(
            IExportFormatHandler handler,
            ExportContext<T> context)
        {
            await _exportLock.WaitAsync(context.CancellationToken);

            try
            {
                var progress = new ExportProgressReporter(context.ExportId, OnExportProgress);

                // Prepare output path;
                var outputPath = PrepareOutputPath(context.Options);

                // Execute the export;
                await handler.ExportAsync(context.Data, outputPath, context.Options,
                    progress, context.CancellationToken);

                // Get file information;
                var fileInfo = new FileInfo(outputPath);

                return new ExportResult;
                {
                    ExportId = context.ExportId,
                    Format = context.Options.Format,
                    FilePath = outputPath,
                    FileSizeBytes = fileInfo.Length,
                    Success = true,
                    GeneratedAt = DateTime.UtcNow;
                };
            }
            finally
            {
                _exportLock.Release();
            }
        }

        private async Task ExecuteStreamingExportAsync<T>(
            IExportFormatHandler handler,
            IAsyncEnumerable<T> dataStream,
            string outputPath,
            ExportOptions options,
            IProgress<ExportProgress> progress,
            CancellationToken cancellationToken)
        {
            var exportProgress = new ExportProgressReporter(Guid.NewGuid(), OnExportProgress);
            var combinedProgress = new CombinedProgressReporter(progress, exportProgress);

            await handler.ExportStreamAsync(dataStream, outputPath, options,
                combinedProgress, cancellationToken);
        }

        private string PrepareOutputPath(ExportOptions options)
        {
            string directory;

            if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                directory = options.OutputDirectory;
            }
            else if (!string.IsNullOrWhiteSpace(_configuration.DefaultExportDirectory))
            {
                directory = _configuration.DefaultExportDirectory;
            }
            else;
            {
                directory = _configuration.TempDirectory;
            }

            // Create directory if it doesn't exist;
            Directory.CreateDirectory(directory);

            // Resolve file name with placeholders;
            var fileName = ResolveFileName(options.FileName);

            // Ensure unique file name;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            var counter = 0;

            string finalPath;
            do;
            {
                var suffix = counter > 0 ? $" ({counter})" : "";
                finalPath = Path.Combine(directory, $"{baseName}{suffix}{extension}");
                counter++;
            } while (File.Exists(finalPath) && options.OverwriteExisting == false);

            return finalPath;
        }

        private string ResolveFileName(string fileNameTemplate)
        {
            var result = fileNameTemplate;
            var timestamp = DateTime.UtcNow;

            // Replace common placeholders;
            result = result.Replace("{timestamp}", timestamp.ToString("yyyyMMdd_HHmmss"));
            result = result.Replace("{date}", timestamp.ToString("yyyyMMdd"));
            result = result.Replace("{time}", timestamp.ToString("HHmmss"));
            result = result.Replace("{guid}", Guid.NewGuid().ToString("N"));

            // Handle custom format placeholders like {timestamp:format}
            var startIndex = 0;
            while ((startIndex = result.IndexOf("{timestamp:", startIndex)) != -1)
            {
                var endIndex = result.IndexOf("}", startIndex);
                if (endIndex == -1) break;

                var format = result.Substring(startIndex + 11, endIndex - startIndex - 11);
                var formattedTimestamp = timestamp.ToString(format);

                result = result.Remove(startIndex, endIndex - startIndex + 1);
                result = result.Insert(startIndex, formattedTimestamp);

                startIndex += formattedTimestamp.Length;
            }

            return result;
        }

        private async Task<string> GeneratePreviewDataAsync<T>(
            IExportFormatHandler handler,
            IEnumerable<T> sampleData,
            ExportOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                // Create temporary file for preview;
                var tempFile = Path.Combine(_configuration.TempDirectory,
                    $"preview_{Guid.NewGuid():N}.tmp");

                await handler.ExportAsync(sampleData, tempFile, options, null, cancellationToken);

                // Read first few bytes for preview;
                using (var fileStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read))
                using (var reader = new StreamReader(fileStream))
                {
                    var buffer = new char[1024];
                    var bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length);

                    // Clean up temp file;
                    try { File.Delete(tempFile); } catch { }

                    return new string(buffer, 0, bytesRead);
                }
            }
            catch
            {
                // If preview generation fails, return empty string;
                return string.Empty;
            }
        }

        private long EstimateSize<T>(IEnumerable<T> data)
        {
            if (data == null || !data.Any())
                return 0;

            // Rough estimation based on sample;
            var sample = data.Take(10).ToList();
            if (!sample.Any())
                return 0;

            // Estimate based on JSON serialization;
            try
            {
                var json = JsonConvert.SerializeObject(sample.First());
                var bytesPerItem = Encoding.UTF8.GetByteCount(json);
                var totalItems = data.Count();

                return bytesPerItem * totalItems;
            }
            catch
            {
                // Fallback estimation;
                return 100 * data.Count();
            }
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ExportService));
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _exportLock?.Dispose();

                    // Cleanup temporary files;
                    CleanupTempFiles(TimeSpan.FromDays(0));

                    Status = ExportServiceStatus.Disposed;
                    _logger.LogInformation("ExportService disposed", "ExportService");
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;

        #region Inner Classes for Format Handlers;

        /// <summary>
        /// CSV export handler;
        /// </summary>
        private class CsvExportHandler : IExportFormatHandler;
        {
            public string Format => "CSV";

            public async Task ExportAsync<T>(IEnumerable<T> data, string outputPath, ExportOptions options,
                IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                using (var writer = new StreamWriter(outputPath))
                using (var csv = new CsvHelper.CsvWriter(writer, System.Globalization.CultureInfo.InvariantCulture))
                {
                    if (options.Parameters.TryGetValue("IncludeHeaders", out var includeHeadersObj) &&
                        includeHeadersObj is bool includeHeaders && includeHeaders)
                    {
                        csv.WriteHeader<T>();
                        await csv.NextRecordAsync();
                    }

                    var items = data.ToList();
                    var total = items.Count;
                    var processed = 0;

                    foreach (var item in items)
                    {
                        csv.WriteRecord(item);
                        await csv.NextRecordAsync();

                        processed++;
                        if (processed % 100 == 0)
                        {
                            progress?.Report(new ExportProgress;
                            {
                                TotalItems = total,
                                ProcessedItems = processed,
                                Percentage = (double)processed / total * 100;
                            });
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            public async Task ExportStreamAsync<T>(IAsyncEnumerable<T> dataStream, string outputPath,
                ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                using (var writer = new StreamWriter(outputPath))
                using (var csv = new CsvHelper.CsvWriter(writer, System.Globalization.CultureInfo.InvariantCulture))
                {
                    if (options.Parameters.TryGetValue("IncludeHeaders", out var includeHeadersObj) &&
                        includeHeadersObj is bool includeHeaders && includeHeaders)
                    {
                        csv.WriteHeader<T>();
                        await csv.NextRecordAsync();
                    }

                    var processed = 0;
                    await foreach (var item in dataStream.WithCancellation(cancellationToken))
                    {
                        csv.WriteRecord(item);
                        await csv.NextRecordAsync();

                        processed++;
                        if (processed % 100 == 0)
                        {
                            progress?.Report(new ExportProgress;
                            {
                                ProcessedItems = processed,
                                Percentage = processed // Streaming doesn't know total;
                            });
                        }
                    }
                }
            }

            public bool CanHandle(string format) => format.Equals("CSV", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// JSON export handler;
        /// </summary>
        private class JsonExportHandler : IExportFormatHandler;
        {
            public string Format => "JSON";

            public async Task ExportAsync<T>(IEnumerable<T> data, string outputPath, ExportOptions options,
                IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                var settings = new JsonSerializerSettings;
                {
                    Formatting = options.Parameters.TryGetValue("Indented", out var indentedObj) &&
                                indentedObj is bool indented && indented;
                        ? Formatting.Indented;
                        : Formatting.None;
                };

                var json = JsonConvert.SerializeObject(data, settings);
                await File.WriteAllTextAsync(outputPath, json, cancellationToken);

                progress?.Report(new ExportProgress { Percentage = 100 });
            }

            public async Task ExportStreamAsync<T>(IAsyncEnumerable<T> dataStream, string outputPath,
                ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                using (var writer = new StreamWriter(outputPath))
                using (var jsonWriter = new JsonTextWriter(writer))
                {
                    var serializer = new JsonSerializer;
                    {
                        Formatting = options.Parameters.TryGetValue("Indented", out var indentedObj) &&
                                    indentedObj is bool indented && indented;
                            ? Formatting.Indented;
                            : Formatting.None;
                    };

                    await jsonWriter.WriteStartArrayAsync(cancellationToken);

                    var processed = 0;
                    await foreach (var item in dataStream.WithCancellation(cancellationToken))
                    {
                        serializer.Serialize(jsonWriter, item);

                        processed++;
                        if (processed % 100 == 0)
                        {
                            progress?.Report(new ExportProgress { ProcessedItems = processed });
                        }
                    }

                    await jsonWriter.WriteEndArrayAsync(cancellationToken);
                }
            }

            public bool CanHandle(string format) => format.Equals("JSON", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// XML export handler;
        /// </summary>
        private class XmlExportHandler : IExportFormatHandler;
        {
            public string Format => "XML";

            public async Task ExportAsync<T>(IEnumerable<T> data, string outputPath, ExportOptions options,
                IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                var serializer = new XmlSerializer(typeof(List<T>));
                using (var writer = new StreamWriter(outputPath))
                {
                    serializer.Serialize(writer, data.ToList());
                }

                await Task.CompletedTask;
                progress?.Report(new ExportProgress { Percentage = 100 });
            }

            public async Task ExportStreamAsync<T>(IAsyncEnumerable<T> dataStream, string outputPath,
                ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                // XML streaming requires special handling - collect all data first;
                var data = new List<T>();
                await foreach (var item in dataStream.WithCancellation(cancellationToken))
                {
                    data.Add(item);
                }

                await ExportAsync(data, outputPath, options, progress, cancellationToken);
            }

            public bool CanHandle(string format) => format.Equals("XML", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Excel export handler;
        /// </summary>
        private class ExcelExportHandler : IExportFormatHandler;
        {
            public string Format => "Excel";

            public async Task ExportAsync<T>(IEnumerable<T> data, string outputPath, ExportOptions options,
                IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                using (var workbook = new XLWorkbook())
                {
                    var sheetName = options.Parameters.TryGetValue("SheetName", out var sheetNameObj)
                        ? sheetNameObj.ToString()
                        : "Sheet1";

                    var worksheet = workbook.Worksheets.Add(sheetName);

                    var items = data.ToList();
                    var total = items.Count;

                    // Write headers;
                    if (total > 0)
                    {
                        var properties = typeof(T).GetProperties();
                        for (int i = 0; i < properties.Length; i++)
                        {
                            worksheet.Cell(1, i + 1).Value = properties[i].Name;
                        }

                        // Write data;
                        for (int row = 0; row < total; row++)
                        {
                            var item = items[row];
                            for (int col = 0; col < properties.Length; col++)
                            {
                                var value = properties[col].GetValue(item);
                                worksheet.Cell(row + 2, col + 1).Value = value?.ToString() ?? string.Empty;
                            }

                            if (row % 100 == 0)
                            {
                                progress?.Report(new ExportProgress;
                                {
                                    TotalItems = total,
                                    ProcessedItems = row + 1,
                                    Percentage = (double)(row + 1) / total * 100;
                                });
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    // Auto-fit columns if requested;
                    if (options.Parameters.TryGetValue("AutoFitColumns", out var autoFitObj) &&
                        autoFitObj is bool autoFit && autoFit)
                    {
                        worksheet.Columns().AdjustToContents();
                    }

                    workbook.SaveAs(outputPath);
                }

                await Task.CompletedTask;
            }

            public async Task ExportStreamAsync<T>(IAsyncEnumerable<T> dataStream, string outputPath,
                ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                // Excel streaming requires collecting data first;
                var data = new List<T>();
                await foreach (var item in dataStream.WithCancellation(cancellationToken))
                {
                    data.Add(item);
                }

                await ExportAsync(data, outputPath, options, progress, cancellationToken);
            }

            public bool CanHandle(string format) =>
                format.Equals("Excel", StringComparison.OrdinalIgnoreCase) ||
                format.Equals("XLSX", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// PDF export handler;
        /// </summary>
        private class PdfExportHandler : IExportFormatHandler;
        {
            public string Format => "PDF";

            public async Task ExportAsync<T>(IEnumerable<T> data, string outputPath, ExportOptions options,
                IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                using (var document = new Document())
                {
                    PdfWriter.GetInstance(document, new FileStream(outputPath, FileMode.Create));

                    document.Open();

                    // Add title;
                    var title = new Paragraph("Export Report")
                    {
                        Alignment = Element.ALIGN_CENTER,
                        SpacingAfter = 20f;
                    };
                    document.Add(title);

                    // Add generation time;
                    document.Add(new Paragraph($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
                    {
                        SpacingAfter = 20f;
                    });

                    // Create table;
                    var items = data.ToList();
                    if (items.Any())
                    {
                        var properties = typeof(T).GetProperties();
                        var table = new PdfPTable(properties.Length)
                        {
                            WidthPercentage = 100;
                        };

                        // Add headers;
                        foreach (var property in properties)
                        {
                            table.AddCell(new PdfPCell(new Phrase(property.Name))
                            {
                                BackgroundColor = BaseColor.LIGHT_GRAY;
                            });
                        }

                        // Add data;
                        for (int i = 0; i < items.Count; i++)
                        {
                            var item = items[i];
                            foreach (var property in properties)
                            {
                                var value = property.GetValue(item);
                                table.AddCell(new PdfPCell(new Phrase(value?.ToString() ?? string.Empty)));
                            }

                            if (i % 50 == 0)
                            {
                                progress?.Report(new ExportProgress;
                                {
                                    TotalItems = items.Count,
                                    ProcessedItems = i + 1,
                                    Percentage = (double)(i + 1) / items.Count * 100;
                                });
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        document.Add(table);
                    }

                    document.Close();
                }

                await Task.CompletedTask;
                progress?.Report(new ExportProgress { Percentage = 100 });
            }

            public async Task ExportStreamAsync<T>(IAsyncEnumerable<T> dataStream, string outputPath,
                ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                // PDF streaming requires collecting data first;
                var data = new List<T>();
                await foreach (var item in dataStream.WithCancellation(cancellationToken))
                {
                    data.Add(item);
                }

                await ExportAsync(data, outputPath, options, progress, cancellationToken);
            }

            public bool CanHandle(string format) => format.Equals("PDF", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// HTML export handler;
        /// </summary>
        private class HtmlExportHandler : IExportFormatHandler;
        {
            public string Format => "HTML";

            public async Task ExportAsync<T>(IEnumerable<T> data, string outputPath, ExportOptions options,
                IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                var html = new StringBuilder();

                html.AppendLine("<!DOCTYPE html>");
                html.AppendLine("<html>");
                html.AppendLine("<head>");
                html.AppendLine("<meta charset='UTF-8'>");
                html.AppendLine("<title>Export Report</title>");
                html.AppendLine("<style>");
                html.AppendLine("table { border-collapse: collapse; width: 100%; }");
                html.AppendLine("th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }");
                html.AppendLine("th { background-color: #f2f2f2; }");
                html.AppendLine("tr:nth-child(even) { background-color: #f9f9f9; }");
                html.AppendLine("</style>");
                html.AppendLine("</head>");
                html.AppendLine("<body>");
                html.AppendLine("<h1>Export Report</h1>");
                html.AppendLine($"<p>Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>");

                var items = data.ToList();
                if (items.Any())
                {
                    html.AppendLine("<table>");
                    html.AppendLine("<thead><tr>");

                    var properties = typeof(T).GetProperties();
                    foreach (var property in properties)
                    {
                        html.AppendLine($"<th>{property.Name}</th>");
                    }

                    html.AppendLine("</tr></thead>");
                    html.AppendLine("<tbody>");

                    for (int i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        html.AppendLine("<tr>");

                        foreach (var property in properties)
                        {
                            var value = property.GetValue(item);
                            html.AppendLine($"<td>{value?.ToString() ?? string.Empty}</td>");
                        }

                        html.AppendLine("</tr>");

                        if (i % 100 == 0)
                        {
                            progress?.Report(new ExportProgress;
                            {
                                TotalItems = items.Count,
                                ProcessedItems = i + 1,
                                Percentage = (double)(i + 1) / items.Count * 100;
                            });
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    html.AppendLine("</tbody>");
                    html.AppendLine("</table>");
                }

                html.AppendLine("</body>");
                html.AppendLine("</html>");

                await File.WriteAllTextAsync(outputPath, html.ToString(), cancellationToken);
                progress?.Report(new ExportProgress { Percentage = 100 });
            }

            public async Task ExportStreamAsync<T>(IAsyncEnumerable<T> dataStream, string outputPath,
                ExportOptions options, IProgress<ExportProgress> progress, CancellationToken cancellationToken)
            {
                // HTML streaming requires collecting data first;
                var data = new List<T>();
                await foreach (var item in dataStream.WithCancellation(cancellationToken))
                {
                    data.Add(item);
                }

                await ExportAsync(data, outputPath, options, progress, cancellationToken);
            }

            public bool CanHandle(string format) => format.Equals("HTML", StringComparison.OrdinalIgnoreCase);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Export service status;
    /// </summary>
    public enum ExportServiceStatus;
    {
        Ready,
        Exporting,
        Error,
        Disposed;
    }

    /// <summary>
    /// Export format enumeration;
    /// </summary>
    public static class ExportFormats;
    {
        public const string CSV = "CSV";
        public const string JSON = "JSON";
        public const string XML = "XML";
        public const string Excel = "Excel";
        public const string PDF = "PDF";
        public const string HTML = "HTML";
    }

    /// <summary>
    /// Export configuration;
    /// </summary>
    public class ExportConfiguration;
    {
        public string TempDirectory { get; set; }
        public string DefaultExportDirectory { get; set; }
        public int MaxConcurrentExports { get; set; }
        public long MaxFileSizeBytes { get; set; }
        public bool EnableCompression { get; set; }
        public int KeepTempFilesDays { get; set; }
        public bool EnableDetailedLogging { get; set; }
    }

    /// <summary>
    /// Export options;
    /// </summary>
    public class ExportOptions;
    {
        public string Format { get; set; }
        public string FileName { get; set; }
        public string OutputDirectory { get; set; }
        public string TemplateName { get; set; }
        public bool OverwriteExisting { get; set; }
        public bool IncludeMetadata { get; set; } = true;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public ExportOptions Clone()
        {
            return new ExportOptions;
            {
                Format = Format,
                FileName = FileName,
                OutputDirectory = OutputDirectory,
                TemplateName = TemplateName,
                OverwriteExisting = OverwriteExisting,
                IncludeMetadata = IncludeMetadata,
                Parameters = new Dictionary<string, object>(Parameters)
            };
        }
    }

    /// <summary>
    /// Export template;
    /// </summary>
    public class ExportTemplate;
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Format { get; set; }
        public string OutputFileName { get; set; }
        public Dictionary<string, object> DefaultParameters { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
        public DateTime LastModifiedTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Export result;
    /// </summary>
    public class ExportResult;
    {
        public Guid ExportId { get; set; }
        public string Format { get; set; }
        public string FilePath { get; set; }
        public long FileSizeBytes { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime GeneratedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Streaming export result;
    /// </summary>
    public class StreamingExportResult;
    {
        public Guid ExportId { get; set; }
        public string Format { get; set; }
        public string FilePath { get; set; }
        public long FileSizeBytes { get; set; }
        public bool Success { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Export progress information;
    /// </summary>
    public class ExportProgress;
    {
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public double Percentage { get; set; }
        public string CurrentOperation { get; set; }
        public TimeSpan EstimatedRemainingTime { get; set; }
    }

    /// <summary>
    /// Export preview;
    /// </summary>
    public class ExportPreview;
    {
        public string Format { get; set; }
        public int SampleSize { get; set; }
        public long EstimatedSizeBytes { get; set; }
        public bool Supported { get; set; }
        public string SampleData { get; set; }
    }

    /// <summary>
    /// Export validation result;
    /// </summary>
    public class ExportValidationResult;
    {
        public ExportOptions Options { get; set; }
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Export context;
    /// </summary>
    public class ExportContext<T>
    {
        public Guid ExportId { get; set; }
        public IEnumerable<T> Data { get; set; }
        public ExportOptions Options { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// Export format handler interface;
    /// </summary>
    public interface IExportFormatHandler;
    {
        string Format { get; }
        Task ExportAsync<T>(IEnumerable<T> data, string outputPath, ExportOptions options,
            IProgress<ExportProgress> progress, CancellationToken cancellationToken);
        Task ExportStreamAsync<T>(IAsyncEnumerable<T> dataStream, string outputPath, ExportOptions options,
            IProgress<ExportProgress> progress, CancellationToken cancellationToken);
        bool CanHandle(string format);
    }

    /// <summary>
    /// Event arguments for export started;
    /// </summary>
    public class ExportStartedEventArgs : EventArgs;
    {
        public Guid ExportId { get; set; }
        public string Format { get; set; }
        public ExportOptions Options { get; set; }
        public DateTime StartTime { get; set; }
        public bool IsStreaming { get; set; }
    }

    /// <summary>
    /// Event arguments for export completed;
    /// </summary>
    public class ExportCompletedEventArgs : EventArgs;
    {
        public Guid ExportId { get; set; }
        public ExportResult Result { get; set; }
        public DateTime CompletionTime { get; set; }
        public bool IsStreaming { get; set; }
    }

    /// <summary>
    /// Event arguments for export failed;
    /// </summary>
    public class ExportFailedEventArgs : EventArgs;
    {
        public Guid ExportId { get; set; }
        public string Format { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public DateTime FailureTime { get; set; }
        public bool IsStreaming { get; set; }
    }

    /// <summary>
    /// Event arguments for export progress;
    /// </summary>
    public class ExportProgressEventArgs : EventArgs;
    {
        public Guid ExportId { get; set; }
        public ExportProgress Progress { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Export-specific exception;
    /// </summary>
    public class ExportException : Exception
    {
        public string ErrorCode { get; }

        public ExportException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public ExportException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Format not supported exception;
    /// </summary>
    public class FormatNotSupportedException : Exception
    {
        public FormatNotSupportedException(string message) : base(message) { }
    }

    /// <summary>
    /// Template not found exception;
    /// </summary>
    public class TemplateNotFoundException : Exception
    {
        public TemplateNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Export error codes;
    /// </summary>
    public static class ExportErrorCodes;
    {
        public const string ExportFailed = "EXPORT_001";
        public const string BatchExportFailed = "EXPORT_002";
        public const string StreamingExportFailed = "EXPORT_003";
        public const string PreviewFailed = "EXPORT_004";
        public const string InvalidOptions = "EXPORT_005";
        public const string FormatNotSupported = "EXPORT_006";
        public const string TemplateNotFound = "EXPORT_007";
    }

    /// <summary>
    /// Progress reporter for exports;
    /// </summary>
    internal class ExportProgressReporter : IProgress<ExportProgress>
    {
        private readonly Guid _exportId;
        private readonly EventHandler<ExportProgressEventArgs> _progressHandler;

        public ExportProgressReporter(Guid exportId, EventHandler<ExportProgressEventArgs> progressHandler)
        {
            _exportId = exportId;
            _progressHandler = progressHandler;
        }

        public void Report(ExportProgress value)
        {
            _progressHandler?.Invoke(this, new ExportProgressEventArgs;
            {
                ExportId = _exportId,
                Progress = value,
                Timestamp = DateTime.UtcNow;
            });
        }
    }

    /// <summary>
    /// Combined progress reporter;
    /// </summary>
    internal class CombinedProgressReporter : IProgress<ExportProgress>
    {
        private readonly IProgress<ExportProgress>[] _reporters;

        public CombinedProgressReporter(params IProgress<ExportProgress>[] reporters)
        {
            _reporters = reporters.Where(r => r != null).ToArray();
        }

        public void Report(ExportProgress value)
        {
            foreach (var reporter in _reporters)
            {
                reporter.Report(value);
            }
        }
    }

    #endregion;
}
