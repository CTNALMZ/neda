using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.Middleware;
using NEDA.ContentCreation.ExportPipelines;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.ContentCreation.ExportPipelines;
{
    /// <summary>
    /// Export işlemleri için batch işleme motoru;
    /// </summary>
    public interface IBatchExporter;
    {
        /// <summary>
        /// Batch export işlemini başlatır;
        /// </summary>
        Task<BatchExportResult> ExportBatchAsync(BatchExportRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Batch export durumunu getirir;
        /// </summary>
        Task<BatchExportStatus> GetBatchStatusAsync(Guid batchId);

        /// <summary>
        /// Batch export işlemini iptal eder;
        /// </summary>
        Task<bool> CancelBatchAsync(Guid batchId);

        /// <summary>
        /// Batch export geçmişini getirir;
        /// </summary>
        Task<IEnumerable<BatchExportHistory>> GetExportHistoryAsync(int limit = 100);
    }

    /// <summary>
    /// Batch export request modeli;
    /// </summary>
    public class BatchExportRequest;
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Description { get; set; }
        public List<ExportItem> Items { get; set; } = new();
        public ExportFormat Format { get; set; }
        public ExportQuality Quality { get; set; }
        public CompressionLevel Compression { get; set; }
        public string OutputDirectory { get; set; }
        public bool OverwriteExisting { get; set; }
        public bool CreateSubdirectories { get; set; }
        public bool GenerateReport { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
        public int MaxParallelOperations { get; set; } = 4;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string CreatedBy { get; set; }
    }

    /// <summary>
    /// Export öğesi;
    /// </summary>
    public class ExportItem;
    {
        public string Id { get; set; }
        public string SourcePath { get; set; }
        public string Name { get; set; }
        public ExportType Type { get; set; }
        public Dictionary<string, object> ExportSettings { get; set; } = new();
        public int Priority { get; set; } = 1;
    }

    /// <summary>
    /// Batch export sonucu;
    /// </summary>
    public class BatchExportResult;
    {
        public Guid BatchId { get; set; }
        public ExportStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int TotalItems { get; set; }
        public int SuccessfulExports { get; set; }
        public int FailedExports { get; set; }
        public List<ExportResult> Results { get; set; } = new();
        public string OutputDirectory { get; set; }
        public string ReportPath { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Statistics { get; set; } = new();
        public TimeSpan Duration => (EndTime ?? DateTime.UtcNow) - StartTime;
    }

    /// <summary>
    /// Export durumu;
    /// </summary>
    public class BatchExportStatus;
    {
        public Guid BatchId { get; set; }
        public ExportStatus Status { get; set; }
        public int ProcessedItems { get; set; }
        public int TotalItems { get; set; }
        public double ProgressPercentage => TotalItems > 0 ? (ProcessedItems * 100.0) / TotalItems : 0;
        public DateTime StartTime { get; set; }
        public DateTime? EstimatedCompletionTime { get; set; }
        public string CurrentOperation { get; set; }
        public List<string> RecentErrors { get; set; } = new();
    }

    /// <summary>
    /// Export geçmişi;
    /// </summary>
    public class BatchExportHistory;
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public ExportStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int TotalItems { get; set; }
        public int SuccessfulExports { get; set; }
        public string CreatedBy { get; set; }
        public TimeSpan Duration => (EndTime ?? DateTime.UtcNow) - StartTime;
    }

    /// <summary>
    /// Tekil export sonucu;
    /// </summary>
    public class ExportResult;
    {
        public string ItemId { get; set; }
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public ExportStatus Status { get; set; }
        public long FileSize { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Batch exporter konfigürasyonu;
    /// </summary>
    public class BatchExporterConfig;
    {
        public int DefaultMaxParallelOperations { get; set; } = 4;
        public int MaxRetryAttempts { get; set; } = 3;
        public int RetryDelayMilliseconds { get; set; } = 1000;
        public int MaxBatchSize { get; set; } = 1000;
        public string DefaultOutputDirectory { get; set; } = "Exports";
        public bool EnableCompression { get; set; } = true;
        public bool EnableLogging { get; set; } = true;
        public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromHours(1);
        public Dictionary<ExportFormat, FormatSettings> FormatSettings { get; set; } = new();
    }

    /// <summary>
    /// Format ayarları;
    /// </summary>
    public class FormatSettings;
    {
        public string Extension { get; set; }
        public string MimeType { get; set; }
        public bool SupportsCompression { get; set; }
        public Dictionary<string, object> DefaultSettings { get; set; } = new();
    }

    /// <summary>
    /// Batch exporter implementasyonu;
    /// </summary>
    public class BatchExporter : IBatchExporter, IDisposable;
    {
        private readonly ILogger<BatchExporter> _logger;
        private readonly IExportManager _exportManager;
        private readonly BatchExporterConfig _config;
        private readonly IErrorReporter _errorReporter;
        private readonly Dictionary<Guid, BatchExportSession> _activeSessions = new();
        private readonly object _sessionLock = new();
        private readonly SemaphoreSlim _parallelSemaphore;
        private bool _disposed;

        public BatchExporter(
            ILogger<BatchExporter> logger,
            IExportManager exportManager,
            IOptions<BatchExporterConfig> config,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exportManager = exportManager ?? throw new ArgumentNullException(nameof(exportManager));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            _parallelSemaphore = new SemaphoreSlim(_config.DefaultMaxParallelOperations);

            _logger.LogInformation("BatchExporter initialized with {MaxParallelOperations} max parallel operations",
                _config.DefaultMaxParallelOperations);
        }

        /// <inheritdoc/>
        public async Task<BatchExportResult> ExportBatchAsync(BatchExportRequest request, CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);

            var session = CreateExportSession(request);
            var result = new BatchExportResult;
            {
                BatchId = request.Id,
                Status = ExportStatus.Queued,
                StartTime = DateTime.UtcNow,
                TotalItems = request.Items.Count,
                OutputDirectory = request.OutputDirectory ?? _config.DefaultOutputDirectory;
            };

            try
            {
                _logger.LogInformation("Starting batch export {BatchId} with {ItemCount} items",
                    request.Id, request.Items.Count);

                RegisterSession(session);

                await ProcessBatchAsync(session, result, cancellationToken);

                if (request.GenerateReport)
                {
                    await GenerateExportReportAsync(result);
                }

                _logger.LogInformation("Batch export {BatchId} completed: {Successful}/{Total} successful",
                    request.Id, result.SuccessfulExports, result.TotalItems);
            }
            catch (OperationCanceledException)
            {
                result.Status = ExportStatus.Cancelled;
                _logger.LogWarning("Batch export {BatchId} was cancelled", request.Id);
            }
            catch (Exception ex)
            {
                result.Status = ExportStatus.Failed;
                result.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Batch export {BatchId} failed: {ErrorMessage}", request.Id, ex.Message);

                await _errorReporter.ReportErrorAsync(ex, new;
                {
                    BatchId = request.Id,
                    Request = request,
                    Result = result;
                });
            }
            finally
            {
                result.EndTime = DateTime.UtcNow;
                UnregisterSession(request.Id);

                if (session != null)
                {
                    await session.DisposeAsync();
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<BatchExportStatus> GetBatchStatusAsync(Guid batchId)
        {
            lock (_sessionLock)
            {
                if (!_activeSessions.TryGetValue(batchId, out var session))
                {
                    throw new KeyNotFoundException($"No active session found for batch ID: {batchId}");
                }

                return new BatchExportStatus;
                {
                    BatchId = batchId,
                    Status = session.Status,
                    ProcessedItems = session.ProcessedCount,
                    TotalItems = session.TotalCount,
                    StartTime = session.StartTime,
                    EstimatedCompletionTime = CalculateEstimatedCompletionTime(session),
                    CurrentOperation = session.CurrentOperation,
                    RecentErrors = session.RecentErrors.Take(5).ToList()
                };
            }
        }

        /// <inheritdoc/>
        public async Task<bool> CancelBatchAsync(Guid batchId)
        {
            lock (_sessionLock)
            {
                if (!_activeSessions.TryGetValue(batchId, out var session))
                {
                    return false;
                }

                session.CancellationTokenSource.Cancel();
                session.Status = ExportStatus.Cancelling;

                _logger.LogInformation("Batch export {BatchId} cancellation requested", batchId);
                return true;
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<BatchExportHistory>> GetExportHistoryAsync(int limit = 100)
        {
            // Burada gerçek uygulamada veritabanından çekilir;
            // Örnek olarak boş liste döndürüyoruz;
            return new List<BatchExportHistory>();
        }

        private BatchExportSession CreateExportSession(BatchExportRequest request)
        {
            var cts = new CancellationTokenSource(_config.OperationTimeout);

            return new BatchExportSession;
            {
                BatchId = request.Id,
                Request = request,
                Status = ExportStatus.Queued,
                StartTime = DateTime.UtcNow,
                TotalCount = request.Items.Count,
                CancellationTokenSource = cts,
                ProgressCallbacks = new List<Action<ExportProgress>>()
            };
        }

        private void RegisterSession(BatchExportSession session)
        {
            lock (_sessionLock)
            {
                _activeSessions[session.BatchId] = session;
            }
        }

        private void UnregisterSession(Guid batchId)
        {
            lock (_sessionLock)
            {
                _activeSessions.Remove(batchId);
            }
        }

        private async Task ProcessBatchAsync(BatchExportSession session, BatchExportResult result, CancellationToken cancellationToken)
        {
            session.Status = ExportStatus.Processing;
            result.Status = ExportStatus.Processing;

            // Çıktı dizinini oluştur;
            EnsureOutputDirectory(result.OutputDirectory);

            // Öğeleri önceliğe göre sırala;
            var sortedItems = session.Request.Items;
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.Name)
                .ToList();

            var parallelOptions = new ParallelOptions;
            {
                MaxDegreeOfParallelism = session.Request.MaxParallelOperations,
                CancellationToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    session.CancellationTokenSource.Token).Token;
            };

            var exportTasks = new List<Task<ExportResult>>();

            foreach (var item in sortedItems)
            {
                var exportTask = ProcessExportItemAsync(item, session, parallelOptions.CancellationToken);
                exportTasks.Add(exportTask);
            }

            // Tüm export işlemlerini bekleyelim;
            var exportResults = await Task.WhenAll(exportTasks);

            // Sonuçları topla;
            result.Results.AddRange(exportResults);
            result.SuccessfulExports = exportResults.Count(r => r.Status == ExportStatus.Completed);
            result.FailedExports = exportResults.Count(r => r.Status == ExportStatus.Failed);
            result.Status = result.FailedExports == 0 ? ExportStatus.Completed :
                          result.SuccessfulExports > 0 ? ExportStatus.PartiallyCompleted : ExportStatus.Failed;

            // İstatistikleri hesapla;
            CalculateStatistics(result);
        }

        private async Task<ExportResult> ProcessExportItemAsync(ExportItem item, BatchExportSession session, CancellationToken cancellationToken)
        {
            var result = new ExportResult;
            {
                ItemId = item.Id,
                SourcePath = item.SourcePath,
                Status = ExportStatus.Processing;
            };

            var startTime = DateTime.UtcNow;

            try
            {
                session.CurrentOperation = $"Exporting {item.Name}";

                // Semaphore ile paralel işlem limiti;
                await _parallelSemaphore.WaitAsync(cancellationToken);

                try
                {
                    _logger.LogDebug("Exporting item {ItemId}: {ItemName}", item.Id, item.Name);

                    // Export manager ile export işlemi;
                    var exportResult = await _exportManager.ExportAsync(new ExportRequest;
                    {
                        SourcePath = item.SourcePath,
                        OutputDirectory = session.Request.OutputDirectory ?? _config.DefaultOutputDirectory,
                        Format = session.Request.Format,
                        Quality = session.Request.Quality,
                        Compression = session.Request.Compression,
                        Settings = item.ExportSettings,
                        CreateSubdirectory = session.Request.CreateSubdirectories,
                        OverwriteExisting = session.Request.OverwriteExisting;
                    }, cancellationToken);

                    result.OutputPath = exportResult.OutputPath;
                    result.FileSize = exportResult.FileSize;
                    result.Status = ExportStatus.Completed;
                    result.Metadata = exportResult.Metadata;
                }
                finally
                {
                    _parallelSemaphore.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Status = ExportStatus.Failed;
                result.ErrorMessage = ex.Message;

                lock (_sessionLock)
                {
                    session.RecentErrors.Add($"Item {item.Id}: {ex.Message}");
                    if (session.RecentErrors.Count > 10)
                    {
                        session.RecentErrors.RemoveAt(0);
                    }
                }

                _logger.LogError(ex, "Failed to export item {ItemId}: {ErrorMessage}", item.Id, ex.Message);
            }
            finally
            {
                result.ProcessingTime = DateTime.UtcNow - startTime;
                Interlocked.Increment(ref session.ProcessedCount);

                UpdateProgress(session);
            }

            return result;
        }

        private async Task GenerateExportReportAsync(BatchExportResult result)
        {
            try
            {
                var reportPath = Path.Combine(result.OutputDirectory, $"ExportReport_{result.BatchId}.json");

                var reportData = new;
                {
                    result.BatchId,
                    result.Status,
                    result.StartTime,
                    result.EndTime,
                    result.TotalItems,
                    result.SuccessfulExports,
                    result.FailedExports,
                    result.Duration,
                    result.Statistics,
                    Results = result.Results.Select(r => new;
                    {
                        r.ItemId,
                        r.SourcePath,
                        r.OutputPath,
                        r.Status,
                        r.FileSize,
                        r.ProcessingTime,
                        r.ErrorMessage;
                    })
                };

                var json = System.Text.Json.JsonSerializer.Serialize(reportData,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                await File.WriteAllTextAsync(reportPath, json);
                result.ReportPath = reportPath;

                _logger.LogInformation("Export report generated: {ReportPath}", reportPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate export report for batch {BatchId}", result.BatchId);
            }
        }

        private void ValidateRequest(BatchExportRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (request.Items == null || request.Items.Count == 0)
                throw new ArgumentException("Export items cannot be empty", nameof(request));

            if (request.Items.Count > _config.MaxBatchSize)
                throw new ArgumentException($"Maximum batch size exceeded. Max: {_config.MaxBatchSize}, Actual: {request.Items.Count}", nameof(request));

            if (string.IsNullOrWhiteSpace(request.OutputDirectory) && string.IsNullOrWhiteSpace(_config.DefaultOutputDirectory))
                throw new ArgumentException("Output directory must be specified", nameof(request));

            if (request.MaxParallelOperations < 1 || request.MaxParallelOperations > 20)
                throw new ArgumentException("Max parallel operations must be between 1 and 20", nameof(request));

            // Öğeleri doğrula;
            foreach (var item in request.Items)
            {
                if (string.IsNullOrWhiteSpace(item.SourcePath))
                    throw new ArgumentException($"Item {item.Id} has empty source path");

                if (!File.Exists(item.SourcePath) && !Directory.Exists(item.SourcePath))
                    throw new FileNotFoundException($"Source not found: {item.SourcePath}", item.SourcePath);
            }
        }

        private void EnsureOutputDirectory(string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                _logger.LogDebug("Created output directory: {OutputDirectory}", outputDirectory);
            }
        }

        private void UpdateProgress(BatchExportSession session)
        {
            // Progress callback'leri çağır;
            var progress = new ExportProgress;
            {
                BatchId = session.BatchId,
                ProcessedCount = session.ProcessedCount,
                TotalCount = session.TotalCount,
                ProgressPercentage = (session.ProcessedCount * 100.0) / session.TotalCount,
                CurrentOperation = session.CurrentOperation;
            };

            foreach (var callback in session.ProgressCallbacks)
            {
                try
                {
                    callback(progress);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error in progress callback");
                }
            }
        }

        private DateTime? CalculateEstimatedCompletionTime(BatchExportSession session)
        {
            if (session.ProcessedCount == 0 || session.Status != ExportStatus.Processing)
                return null;

            var elapsed = DateTime.UtcNow - session.StartTime;
            var itemsPerSecond = session.ProcessedCount / elapsed.TotalSeconds;

            if (itemsPerSecond > 0)
            {
                var remainingItems = session.TotalCount - session.ProcessedCount;
                var remainingSeconds = remainingItems / itemsPerSecond;
                return DateTime.UtcNow.AddSeconds(remainingSeconds);
            }

            return null;
        }

        private void CalculateStatistics(BatchExportResult result)
        {
            if (result.Results.Count == 0)
                return;

            var completedResults = result.Results.Where(r => r.Status == ExportStatus.Completed).ToList();

            result.Statistics["TotalProcessingTime"] = result.Duration.TotalSeconds;
            result.Statistics["AverageProcessingTimePerItem"] = completedResults.Average(r => r.ProcessingTime.TotalSeconds);
            result.Statistics["TotalExportedSize"] = completedResults.Sum(r => r.FileSize);
            result.Statistics["AverageFileSize"] = completedResults.Count > 0 ? completedResults.Average(r => r.FileSize) : 0;
            result.Statistics["SuccessRate"] = (double)result.SuccessfulExports / result.TotalItems * 100;

            // Format dağılımı;
            var formatGroups = result.Results;
                .GroupBy(r => Path.GetExtension(r.OutputPath ?? ""))
                .ToDictionary(g => g.Key, g => g.Count());

            result.Statistics["FormatDistribution"] = formatGroups;
        }

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
                    _parallelSemaphore?.Dispose();

                    lock (_sessionLock)
                    {
                        foreach (var session in _activeSessions.Values)
                        {
                            try
                            {
                                session.CancellationTokenSource?.Cancel();
                                session.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error disposing session {BatchId}", session.BatchId);
                            }
                        }
                        _activeSessions.Clear();
                    }
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Export session sınıfı;
    /// </summary>
    internal class BatchExportSession : IAsyncDisposable;
    {
        public Guid BatchId { get; set; }
        public BatchExportRequest Request { get; set; }
        public ExportStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public int ProcessedCount { get; set; }
        public int TotalCount { get; set; }
        public string CurrentOperation { get; set; }
        public List<string> RecentErrors { get; set; } = new();
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public List<Action<ExportProgress>> ProgressCallbacks { get; set; }

        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource?.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Export progress modeli;
    /// </summary>
    public class ExportProgress;
    {
        public Guid BatchId { get; set; }
        public int ProcessedCount { get; set; }
        public int TotalCount { get; set; }
        public double ProgressPercentage { get; set; }
        public string CurrentOperation { get; set; }
    }

    // Enum'lar;
    public enum ExportStatus;
    {
        Queued,
        Processing,
        Completed,
        PartiallyCompleted,
        Failed,
        Cancelled,
        Cancelling;
    }

    public enum ExportType;
    {
        Model3D,
        Texture,
        Animation,
        Audio,
        Video,
        Document,
        Other;
    }

    public enum ExportFormat;
    {
        FBX,
        OBJ,
        GLTF,
        STL,
        PNG,
        JPG,
        TIFF,
        MP4,
        AVI,
        MP3,
        WAV,
        JSON,
        XML,
        Custom;
    }

    public enum ExportQuality;
    {
        Low,
        Medium,
        High,
        Ultra,
        Custom;
    }

    public enum CompressionLevel;
    {
        None,
        Fast,
        Normal,
        Maximum;
    }

    // Dependency Injection extension'ı;
    public static class BatchExporterExtensions;
    {
        public static IServiceCollection AddBatchExporter(this IServiceCollection services, Action<BatchExporterConfig> configure = null)
        {
            services.Configure<BatchExporterConfig>(configure ?? (config => { }));

            services.AddSingleton<IBatchExporter, BatchExporter>();
            services.AddSingleton<IExportManager, ExportManager>(); // ExportManager implementasyonu gerekiyor;

            return services;
        }
    }
}
