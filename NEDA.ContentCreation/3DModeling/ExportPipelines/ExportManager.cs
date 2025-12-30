// NEDA.ContentCreation/3DModeling/ExportPipelines/ExportManager.cs;

using Microsoft.Extensions.Logging;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.ContentCreation.ExportPipelines.Exporters;
using NEDA.ContentCreation.ExportPipelines.Interfaces;
using NEDA.ContentCreation.ExportPipelines.Models;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.ContentCreation.ExportPipelines;
{
    /// <summary>
    /// 3D model dışa aktarma işlemlerini yöneten merkezi yönetici sınıfı;
    /// </summary>
    public class ExportManager : IExportManager, IDisposable;
    {
        private readonly ILogger<ExportManager> _logger;
        private readonly IExportPipelineFactory _pipelineFactory;
        private readonly IExportValidator _exportValidator;
        private readonly IExportOptimizer _exportOptimizer;
        private readonly IExportProgressTracker _progressTracker;
        private readonly Dictionary<string, ExportSession> _activeSessions;
        private readonly SemaphoreSlim _sessionLock;
        private bool _disposed;

        /// <summary>
        /// Dışa aktarma işlemi başlatıldığında tetiklenen event;
        /// </summary>
        public event EventHandler<ExportStartedEventArgs> OnExportStarted;

        /// <summary>
        /// Dışa aktarma ilerlemesi güncellendiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<ExportProgressEventArgs> OnExportProgress;

        /// <summary>
        /// Dışa aktarma tamamlandığında tetiklenen event;
        /// </summary>
        public event EventHandler<ExportCompletedEventArgs> OnExportCompleted;

        /// <summary>
        /// Dışa aktarma başarısız olduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<ExportFailedEventArgs> OnExportFailed;

        /// <summary>
        /// ExportManager constructor;
        /// </summary>
        public ExportManager(
            ILogger<ExportManager> logger,
            IExportPipelineFactory pipelineFactory,
            IExportValidator exportValidator,
            IExportOptimizer exportOptimizer,
            IExportProgressTracker progressTracker)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
            _exportValidator = exportValidator ?? throw new ArgumentNullException(nameof(exportValidator));
            _exportOptimizer = exportOptimizer ?? throw new ArgumentNullException(nameof(exportOptimizer));
            _progressTracker = progressTracker ?? throw new ArgumentNullException(nameof(progressTracker));

            _activeSessions = new Dictionary<string, ExportSession>();
            _sessionLock = new SemaphoreSlim(1, 1);

            _logger.LogInformation("ExportManager initialized successfully");
        }

        /// <summary>
        /// Tek bir 3D modeli dışa aktarır;
        /// </summary>
        /// <param name="request">Dışa aktarma isteği</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Dışa aktarma sonucu</returns>
        public async Task<ExportResult> ExportSingleModelAsync(ExportRequest request, CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(request, nameof(request));
            Guard.ArgumentNotNull(request.ModelData, nameof(request.ModelData));

            var sessionId = Guid.NewGuid().ToString();

            try
            {
                _logger.LogInformation("Starting single model export. SessionId: {SessionId}, Format: {Format}",
                    sessionId, request.ExportFormat);

                // İstek validasyonu;
                var validationResult = await _exportValidator.ValidateRequestAsync(request, cancellationToken);
                if (!validationResult.IsValid)
                {
                    throw new ExportValidationException($"Export request validation failed: {validationResult.ErrorMessage}");
                }

                // Export pipeline oluştur;
                var pipeline = _pipelineFactory.CreatePipeline(request.ExportFormat);

                // Model optimizasyonu;
                var optimizedModel = await _exportOptimizer.OptimizeForExportAsync(
                    request.ModelData,
                    request.OptimizationOptions,
                    cancellationToken);

                // Export session oluştur;
                var session = new ExportSession(sessionId, request, optimizedModel, pipeline);
                await AddSessionAsync(session);

                // Event tetikle;
                OnExportStarted?.Invoke(this, new ExportStartedEventArgs;
                {
                    SessionId = sessionId,
                    Request = request,
                    StartTime = DateTime.UtcNow;
                });

                // İlerleme takibi başlat;
                _progressTracker.StartTracking(sessionId, request.ModelData.ComplexityLevel);

                // Dışa aktarma işlemini gerçekleştir;
                var result = await ExecuteExportWithRetryAsync(session, cancellationToken);

                // Event tetikle;
                OnExportCompleted?.Invoke(this, new ExportCompletedEventArgs;
                {
                    SessionId = sessionId,
                    Result = result,
                    Duration = DateTime.UtcNow - session.StartTime;
                });

                _logger.LogInformation("Single model export completed successfully. SessionId: {SessionId}, File: {OutputPath}",
                    sessionId, result.OutputPath);

                await RemoveSessionAsync(sessionId);
                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Export operation cancelled. SessionId: {SessionId}", sessionId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export single model. SessionId: {SessionId}", sessionId);

                OnExportFailed?.Invoke(this, new ExportFailedEventArgs;
                {
                    SessionId = sessionId,
                    Request = request,
                    Error = ex,
                    ErrorTime = DateTime.UtcNow;
                });

                await RemoveSessionAsync(sessionId);
                throw new ExportOperationException($"Failed to export model: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Birden fazla modeli toplu olarak dışa aktarır;
        /// </summary>
        /// <param name="requests">Dışa aktarma istekleri listesi</param>
        /// <param name="options">Toplu dışa aktarma seçenekleri</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Toplu dışa aktarma sonuçları</returns>
        public async Task<BatchExportResult> ExportBatchAsync(
            IEnumerable<ExportRequest> requests,
            BatchExportOptions options,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(requests, nameof(requests));
            Guard.ArgumentNotNull(options, nameof(options));

            var batchId = Guid.NewGuid().ToString();
            var requestList = requests.ToList();

            try
            {
                _logger.LogInformation("Starting batch export. BatchId: {BatchId}, ModelCount: {Count}",
                    batchId, requestList.Count);

                var results = new List<ExportResult>();
                var failedExports = new List<FailedExport>();

                // Paralel veya sıralı işleme;
                if (options.UseParallelProcessing && options.MaxDegreeOfParallelism > 1)
                {
                    var parallelOptions = new ParallelOptions;
                    {
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                        CancellationToken = cancellationToken;
                    };

                    await Parallel.ForEachAsync(requestList, parallelOptions, async (request, ct) =>
                    {
                        try
                        {
                            var result = await ExportSingleModelAsync(request, ct);
                            results.Add(result);
                        }
                        catch (Exception ex)
                        {
                            failedExports.Add(new FailedExport;
                            {
                                Request = request,
                                Error = ex.Message,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                    });
                }
                else;
                {
                    foreach (var request in requestList)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        try
                        {
                            var result = await ExportSingleModelAsync(request, cancellationToken);
                            results.Add(result);
                        }
                        catch (Exception ex)
                        {
                            failedExports.Add(new FailedExport;
                            {
                                Request = request,
                                Error = ex.Message,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                    }
                }

                var batchResult = new BatchExportResult;
                {
                    BatchId = batchId,
                    TotalModels = requestList.Count,
                    SuccessfulExports = results.Count,
                    FailedExports = failedExports.Count,
                    Results = results,
                    FailedRequests = failedExports,
                    CompletionTime = DateTime.UtcNow;
                };

                _logger.LogInformation("Batch export completed. BatchId: {BatchId}, Success: {SuccessCount}, Failed: {FailedCount}",
                    batchId, results.Count, failedExports.Count);

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch export failed. BatchId: {BatchId}", batchId);
                throw new ExportOperationException($"Batch export failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Aktif dışa aktarma oturumlarını getirir;
        /// </summary>
        /// <returns>Aktif oturum listesi</returns>
        public async Task<IReadOnlyList<ExportSessionInfo>> GetActiveSessionsAsync()
        {
            await _sessionLock.WaitAsync();
            try
            {
                return _activeSessions.Values;
                    .Select(s => s.GetSessionInfo())
                    .ToList()
                    .AsReadOnly();
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Belirli bir oturumu iptal eder;
        /// </summary>
        /// <param name="sessionId">Oturum ID'si</param>
        /// <returns>İptal başarı durumu</returns>
        public async Task<bool> CancelExportAsync(string sessionId)
        {
            Guard.ArgumentNotNullOrEmpty(sessionId, nameof(sessionId));

            await _sessionLock.WaitAsync();
            try
            {
                if (_activeSessions.TryGetValue(sessionId, out var session))
                {
                    session.Cancel();
                    _logger.LogInformation("Export session cancelled. SessionId: {SessionId}", sessionId);
                    return true;
                }

                _logger.LogWarning("Export session not found for cancellation. SessionId: {SessionId}", sessionId);
                return false;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        /// <summary>
        /// Desteklenen dışa aktarma formatlarını getirir;
        /// </summary>
        /// <returns>Desteklenen format listesi</returns>
        public IReadOnlyList<ExportFormatInfo> GetSupportedFormats()
        {
            return new List<ExportFormatInfo>
            {
                new ExportFormatInfo { Format = ExportFormat.FBX, Extension = ".fbx", Description = "Autodesk FBX" },
                new ExportFormatInfo { Format = ExportFormat.OBJ, Extension = ".obj", Description = "Wavefront OBJ" },
                new ExportFormatInfo { Format = ExportFormat.GLTF, Extension = ".gltf", Description = "GL Transmission Format" },
                new ExportFormatInfo { Format = ExportFormat.GLB, Extension = ".glb", Description = "GL Binary" },
                new ExportFormatInfo { Format = ExportFormat.STL, Extension = ".stl", Description = "Stereolithography" },
                new ExportFormatInfo { Format = ExportFormat.PLY, Extension = ".ply", Description = "Polygon File Format" },
                new ExportFormatInfo { Format = ExportFormat.USD, Extension = ".usd", Description = "Universal Scene Description" },
                new ExportFormatInfo { Format = ExportFormat.ABC, Extension = ".abc", Description = "Alembic" }
            }.AsReadOnly();
        }

        /// <summary>
        /// Export istatistiklerini getirir;
        /// </summary>
        /// <returns>Export istatistikleri</returns>
        public async Task<ExportStatistics> GetStatisticsAsync()
        {
            // Gerçek uygulamada bu veriler database'den gelmeli;
            return await Task.FromResult(new ExportStatistics;
            {
                TotalExports = 0,
                SuccessfulExports = 0,
                FailedExports = 0,
                AverageExportTime = TimeSpan.Zero,
                MostUsedFormat = ExportFormat.FBX,
                LastExportTime = DateTime.UtcNow;
            });
        }

        #region Private Methods;

        private async Task<ExportResult> ExecuteExportWithRetryAsync(ExportSession session, CancellationToken cancellationToken)
        {
            const int maxRetries = 3;
            var retryCount = 0;
            TimeSpan delay = TimeSpan.FromSeconds(1);

            while (retryCount <= maxRetries)
            {
                try
                {
                    // İlerleme güncellemesi;
                    session.UpdateProgress(10 + retryCount * 5, $"Starting export attempt {retryCount + 1}");

                    OnExportProgress?.Invoke(this, new ExportProgressEventArgs;
                    {
                        SessionId = session.SessionId,
                        ProgressPercentage = session.ProgressPercentage,
                        StatusMessage = session.StatusMessage,
                        CurrentOperation = $"Export attempt {retryCount + 1}"
                    });

                    // Dışa aktarma işlemi;
                    var result = await session.Pipeline.ExportAsync(session.ModelData, session.Request.ExportOptions, cancellationToken);

                    // İlerleme güncellemesi;
                    session.UpdateProgress(100, "Export completed successfully");

                    return result;
                }
                catch (Exception ex) when (retryCount < maxRetries && !(ex is OperationCanceledException))
                {
                    retryCount++;
                    _logger.LogWarning(ex, "Export attempt {RetryCount} failed for session {SessionId}",
                        retryCount, session.SessionId);

                    if (retryCount <= maxRetries)
                    {
                        await Task.Delay(delay, cancellationToken);
                        delay = TimeSpan.FromSeconds(delay.TotalSeconds * 2); // Exponential backoff;
                    }
                }
            }

            throw new ExportOperationException($"Export failed after {maxRetries} retries");
        }

        private async Task AddSessionAsync(ExportSession session)
        {
            await _sessionLock.WaitAsync();
            try
            {
                _activeSessions[session.SessionId] = session;
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private async Task RemoveSessionAsync(string sessionId)
        {
            await _sessionLock.WaitAsync();
            try
            {
                if (_activeSessions.ContainsKey(sessionId))
                {
                    _activeSessions.Remove(sessionId);
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _sessionLock?.Dispose();

                    // Tüm aktif oturumları iptal et;
                    foreach (var session in _activeSessions.Values)
                    {
                        session.Cancel();
                    }
                    _activeSessions.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ExportManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Event Args Classes;

    public class ExportStartedEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public ExportRequest Request { get; set; }
        public DateTime StartTime { get; set; }
    }

    public class ExportProgressEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public int ProgressPercentage { get; set; }
        public string StatusMessage { get; set; }
        public string CurrentOperation { get; set; }
    }

    public class ExportCompletedEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public ExportResult Result { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public class ExportFailedEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public ExportRequest Request { get; set; }
        public Exception Error { get; set; }
        public DateTime ErrorTime { get; set; }
    }

    #endregion;
}
