using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.NeuralNetwork.PatternRecognition;
using NEDA.Monitoring.PerformanceCounters;

namespace NEDA.NeuralNetwork.AdaptiveLearning.FeedbackIntegration;
{
    /// <summary>
    /// Geri bildirim entegrasyon motoru - öğrenme sürecine geri bildirimleri entegre eder;
    /// </summary>
    public interface IFeedbackEngine : IDisposable
    {
        /// <summary>
        /// Geri bildirim motorunu başlatır;
        /// </summary>
        Task InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Yeni geri bildirim ekler;
        /// </summary>
        /// <param name="feedback">Geri bildirim verisi</param>
        Task<FeedbackResult> AddFeedbackAsync(FeedbackData feedback, CancellationToken cancellationToken = default);

        /// <summary>
        /// Toplu geri bildirim ekler;
        /// </summary>
        Task<BatchFeedbackResult> AddBatchFeedbackAsync(IEnumerable<FeedbackData> feedbacks, CancellationToken cancellationToken = default);

        /// <summary>
        /// Geri bildirime dayalı öğrenme ayarlarını günceller;
        /// </summary>
        Task<LearningAdjustment> AdjustLearningAsync(AdjustmentRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Performans geri bildirimini işler;
        /// </summary>
        Task<PerformanceFeedback> ProcessPerformanceFeedbackAsync(PerformanceMetrics metrics, CancellationToken cancellationToken = default);

        /// <summary>
        /// Geri bildirim kalitesini değerlendirir;
        /// </summary>
        Task<FeedbackQuality> EvaluateFeedbackQualityAsync(FeedbackData feedback, CancellationToken cancellationToken = default);

        /// <summary>
        /// Geri bildirim döngüsünü çalıştırır;
        /// </summary>
        Task<FeedbackCycleResult> RunFeedbackCycleAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Motor durumunu getirir;
        /// </summary>
        FeedbackEngineState GetState();
    }

    /// <summary>
    /// Geri bildirim motoru implementasyonu;
    /// </summary>
    public class FeedbackEngine : IFeedbackEngine;
    {
        private readonly ILogger<FeedbackEngine> _logger;
        private readonly IOptions<FeedbackEngineOptions> _options;
        private readonly ITrainingEngine _trainingEngine;
        private readonly IPerformanceFeedback _performanceFeedback;
        private readonly IMetricsCalculator _metricsCalculator;
        private readonly IFeedbackQualityAnalyzer _qualityAnalyzer;
        private readonly IFeedbackStorage _feedbackStorage;
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);
        private bool _isInitialized;
        private bool _isDisposed;
        private FeedbackEngineState _state;
        private readonly List<FeedbackCycle> _feedbackCycles = new List<FeedbackCycle>();
        private readonly object _stateLock = new object();

        /// <summary>
        /// Geri bildirim motoru constructor;
        /// </summary>
        public FeedbackEngine(
            ILogger<FeedbackEngine> logger,
            IOptions<FeedbackEngineOptions> options,
            ITrainingEngine trainingEngine,
            IPerformanceFeedback performanceFeedback,
            IMetricsCalculator metricsCalculator,
            IFeedbackQualityAnalyzer qualityAnalyzer,
            IFeedbackStorage feedbackStorage)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _trainingEngine = trainingEngine ?? throw new ArgumentNullException(nameof(trainingEngine));
            _performanceFeedback = performanceFeedback ?? throw new ArgumentNullException(nameof(performanceFeedback));
            _metricsCalculator = metricsCalculator ?? throw new ArgumentNullException(nameof(metricsCalculator));
            _qualityAnalyzer = qualityAnalyzer ?? throw new ArgumentNullException(nameof(qualityAnalyzer));
            _feedbackStorage = feedbackStorage ?? throw new ArgumentNullException(nameof(feedbackStorage));

            _state = new FeedbackEngineState;
            {
                Status = EngineStatus.NotInitialized,
                TotalFeedbackCount = 0,
                LastCycleTime = DateTime.MinValue,
                AverageFeedbackScore = 0.0,
                CycleCount = 0;
            };

            _logger.LogInformation("FeedbackEngine initialized with {Options}", options.Value);
        }

        /// <summary>
        /// Geri bildirim motorunu başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("FeedbackEngine already initialized");
                return;
            }

            try
            {
                _logger.LogInformation("Initializing FeedbackEngine...");

                // Başlangıç kontrolleri;
                await ValidateDependenciesAsync(cancellationToken);

                // Geri bildirim depolama başlatma;
                await _feedbackStorage.InitializeAsync(cancellationToken);

                // Önceki geri bildirimleri yükle;
                await LoadHistoricalFeedbackAsync(cancellationToken);

                // Geri bildirim işleme pipeline'ını başlat;
                await InitializeFeedbackPipelineAsync(cancellationToken);

                lock (_stateLock)
                {
                    _state.Status = EngineStatus.Running;
                    _state.InitializedTime = DateTime.UtcNow;
                    _isInitialized = true;
                }

                _logger.LogInformation("FeedbackEngine initialized successfully at {Time}", DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize FeedbackEngine");
                lock (_stateLock)
                {
                    _state.Status = EngineStatus.Error;
                    _state.LastError = ex.Message;
                }
                throw new FeedbackEngineException("FeedbackEngine initialization failed", ex);
            }
        }

        /// <summary>
        /// Yeni geri bildirim ekler;
        /// </summary>
        public async Task<FeedbackResult> AddFeedbackAsync(FeedbackData feedback, CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            if (feedback == null)
                throw new ArgumentNullException(nameof(feedback));

            if (!ValidateFeedback(feedback))
                throw new InvalidFeedbackException("Feedback validation failed");

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogDebug("Processing new feedback: {FeedbackId}", feedback.Id);

                // Geri bildirim kalitesini değerlendir;
                var quality = await _qualityAnalyzer.AnalyzeAsync(feedback, cancellationToken);

                // Geçerli kalite kontrolü;
                if (quality.Score < _options.Value.MinimumQualityThreshold)
                {
                    _logger.LogWarning("Feedback quality too low: {Score} (min: {Threshold})",
                        quality.Score, _options.Value.MinimumQualityThreshold);

                    return new FeedbackResult;
                    {
                        Success = false,
                        FeedbackId = feedback.Id,
                        Message = "Feedback quality below minimum threshold",
                        QualityScore = quality.Score,
                        Recommendations = quality.Recommendations;
                    };
                }

                // Geri bildirimi işle;
                var processedFeedback = await ProcessFeedbackAsync(feedback, quality, cancellationToken);

                // Depolama;
                await _feedbackStorage.StoreAsync(processedFeedback, cancellationToken);

                // Öğrenme ayarlarını güncelle;
                var adjustment = await UpdateLearningFromFeedbackAsync(processedFeedback, cancellationToken);

                // Durum güncelle;
                UpdateStateWithNewFeedback(processedFeedback, quality);

                _logger.LogInformation("Feedback processed successfully: {FeedbackId}, Quality: {Quality}",
                    feedback.Id, quality.Score);

                return new FeedbackResult;
                {
                    Success = true,
                    FeedbackId = feedback.Id,
                    Message = "Feedback processed successfully",
                    QualityScore = quality.Score,
                    AdjustmentsMade = adjustment,
                    ProcessedAt = DateTime.UtcNow;
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Toplu geri bildirim ekler;
        /// </summary>
        public async Task<BatchFeedbackResult> AddBatchFeedbackAsync(
            IEnumerable<FeedbackData> feedbacks,
            CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            if (feedbacks == null)
                throw new ArgumentNullException(nameof(feedbacks));

            var feedbackList = feedbacks.ToList();
            if (!feedbackList.Any())
                return new BatchFeedbackResult { TotalProcessed = 0 };

            _logger.LogInformation("Processing batch feedback with {Count} items", feedbackList.Count);

            var results = new List<FeedbackResult>();
            var successful = 0;
            var failed = 0;

            // Paralel işleme (yapılandırmaya göre)
            var maxDegreeOfParallelism = _options.Value.MaxBatchProcessingParallelism;
            var options = new ParallelOptions;
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = cancellationToken;
            };

            await Parallel.ForEachAsync(feedbackList, options, async (feedback, ct) =>
            {
                try
                {
                    var result = await AddFeedbackAsync(feedback, ct);
                    lock (results)
                    {
                        results.Add(result);
                        if (result.Success) successful++;
                        else failed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process feedback in batch: {FeedbackId}", feedback?.Id);
                    lock (results)
                    {
                        failed++;
                    }
                }
            });

            // Toplu işlem sonrası optimizasyon;
            if (successful > 0)
            {
                await OptimizeAfterBatchProcessingAsync(successful, cancellationToken);
            }

            return new BatchFeedbackResult;
            {
                TotalProcessed = feedbackList.Count,
                Successful = successful,
                Failed = failed,
                Results = results,
                BatchCompletedAt = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Geri bildirime dayalı öğrenme ayarlarını günceller;
        /// </summary>
        public async Task<LearningAdjustment> AdjustLearningAsync(
            AdjustmentRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _logger.LogInformation("Adjusting learning based on feedback: {AdjustmentType}", request.AdjustmentType);

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                // Mevcut performans metriklerini al;
                var currentMetrics = await _metricsCalculator.CalculateCurrentMetricsAsync(cancellationToken);

                // Geri bildirim analizi;
                var feedbackAnalysis = await AnalyzeRecentFeedbackAsync(
                    request.LookbackPeriod,
                    cancellationToken);

                // Ayarlamaları hesapla;
                var adjustments = CalculateLearningAdjustments(
                    currentMetrics,
                    feedbackAnalysis,
                    request);

                // Eğitim motoruna uygula;
                var appliedAdjustments = await ApplyAdjustmentsToTrainingEngineAsync(
                    adjustments,
                    cancellationToken);

                // Performans izleme başlat;
                await StartAdjustmentMonitoringAsync(appliedAdjustments, cancellationToken);

                _logger.LogInformation("Learning adjustments applied: {Adjustments}", appliedAdjustments);

                return appliedAdjustments;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Performans geri bildirimini işler;
        /// </summary>
        public async Task<PerformanceFeedback> ProcessPerformanceFeedbackAsync(
            PerformanceMetrics metrics,
            CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            if (metrics == null)
                throw new ArgumentNullException(nameof(metrics));

            _logger.LogDebug("Processing performance feedback for model: {ModelId}", metrics.ModelId);

            var performanceFeedback = await _performanceFeedback.AnalyzePerformanceAsync(
                metrics,
                cancellationToken);

            // Performans trend analizi;
            var trendAnalysis = await AnalyzePerformanceTrendAsync(metrics, cancellationToken);
            performanceFeedback.TrendAnalysis = trendAnalysis;

            // Öneriler oluştur;
            var recommendations = await GeneratePerformanceRecommendationsAsync(
                performanceFeedback,
                cancellationToken);
            performanceFeedback.Recommendations = recommendations;

            // Kritik performans düşüşü kontrolü;
            if (performanceFeedback.IsCriticalDecline)
            {
                _logger.LogWarning("Critical performance decline detected: {DeclinePercentage}%",
                    performanceFeedback.DeclinePercentage);

                await HandleCriticalPerformanceDeclineAsync(performanceFeedback, cancellationToken);
            }

            // Geri bildirim döngüsüne ekle;
            await UpdateFeedbackCycleWithPerformanceAsync(performanceFeedback, cancellationToken);

            return performanceFeedback;
        }

        /// <summary>
        /// Geri bildirim kalitesini değerlendirir;
        /// </summary>
        public async Task<FeedbackQuality> EvaluateFeedbackQualityAsync(
            FeedbackData feedback,
            CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            if (feedback == null)
                throw new ArgumentNullException(nameof(feedback));

            return await _qualityAnalyzer.AnalyzeAsync(feedback, cancellationToken);
        }

        /// <summary>
        /// Geri bildirim döngüsünü çalıştırır;
        /// </summary>
        public async Task<FeedbackCycleResult> RunFeedbackCycleAsync(CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            _logger.LogInformation("Starting feedback cycle #{CycleNumber}", _state.CycleCount + 1);

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                var cycleStartTime = DateTime.UtcNow;

                // 1. Bekleyen geri bildirimleri topla;
                var pendingFeedback = await _feedbackStorage.GetPendingFeedbackAsync(cancellationToken);

                // 2. Geri bildirimleri işle;
                var processingResult = await ProcessFeedbackCycleAsync(pendingFeedback, cancellationToken);

                // 3. Öğrenme ayarlarını güncelle;
                var learningResult = await UpdateLearningFromCycleAsync(processingResult, cancellationToken);

                // 4. Performans değerlendirmesi yap;
                var performanceResult = await EvaluateCyclePerformanceAsync(cancellationToken);

                // 5. Döngü sonuçlarını kaydet;
                var cycle = new FeedbackCycle;
                {
                    CycleNumber = _state.CycleCount + 1,
                    StartTime = cycleStartTime,
                    EndTime = DateTime.UtcNow,
                    FeedbackProcessed = processingResult.TotalProcessed,
                    LearningAdjustments = learningResult.AdjustmentsApplied,
                    PerformanceMetrics = performanceResult,
                    Success = processingResult.SuccessRate >= _options.Value.MinimumSuccessRate;
                };

                _feedbackCycles.Add(cycle);

                // Durum güncelle;
                lock (_stateLock)
                {
                    _state.CycleCount++;
                    _state.LastCycleTime = DateTime.UtcNow;
                    _state.LastCycleSuccess = cycle.Success;
                    _state.AverageFeedbackScore = CalculateAverageFeedbackScore();
                }

                _logger.LogInformation("Feedback cycle #{CycleNumber} completed: {Success}, " +
                    "Processed: {Processed}, Adjustments: {Adjustments}",
                    cycle.CycleNumber, cycle.Success, cycle.FeedbackProcessed,
                    cycle.LearningAdjustments.Count);

                return new FeedbackCycleResult;
                {
                    Cycle = cycle,
                    ProcessingResult = processingResult,
                    LearningResult = learningResult,
                    PerformanceResult = performanceResult;
                };
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Motor durumunu getirir;
        /// </summary>
        public FeedbackEngineState GetState()
        {
            lock (_stateLock)
            {
                return _state.Clone();
            }
        }

        #region Private Methods;

        private void EnsureInitialized()
        {
            if (!_isInitialized)
                throw new FeedbackEngineException("FeedbackEngine not initialized. Call InitializeAsync first.");
        }

        private bool ValidateFeedback(FeedbackData feedback)
        {
            if (string.IsNullOrEmpty(feedback.Id))
                return false;

            if (feedback.Timestamp == default)
                return false;

            if (feedback.Score < 0 || feedback.Score > 1.0)
                return false;

            return true;
        }

        private async Task ValidateDependenciesAsync(CancellationToken cancellationToken)
        {
            var tasks = new[]
            {
                ValidateServiceAsync(_trainingEngine, nameof(_trainingEngine), cancellationToken),
                ValidateServiceAsync(_performanceFeedback, nameof(_performanceFeedback), cancellationToken),
                ValidateServiceAsync(_metricsCalculator, nameof(_metricsCalculator), cancellationToken),
                ValidateServiceAsync(_qualityAnalyzer, nameof(_qualityAnalyzer), cancellationToken),
                ValidateServiceAsync(_feedbackStorage, nameof(_feedbackStorage), cancellationToken)
            };

            await Task.WhenAll(tasks);
        }

        private async Task ValidateServiceAsync(object service, string serviceName, CancellationToken cancellationToken)
        {
            try
            {
                // Servis durum kontrolü;
                if (service is IHealthCheck healthCheck)
                {
                    var health = await healthCheck.CheckHealthAsync(cancellationToken);
                    if (health.Status != HealthStatus.Healthy)
                    {
                        throw new FeedbackEngineException($"Service {serviceName} is not healthy: {health.Status}");
                    }
                }
            }
            catch (Exception ex)
            {
                throw new FeedbackEngineException($"Service validation failed for {serviceName}", ex);
            }
        }

        private async Task LoadHistoricalFeedbackAsync(CancellationToken cancellationToken)
        {
            try
            {
                var historicalFeedback = await _feedbackStorage.GetHistoricalFeedbackAsync(
                    _options.Value.HistoricalDataLookbackDays,
                    cancellationToken);

                if (historicalFeedback.Any())
                {
                    lock (_stateLock)
                    {
                        _state.TotalFeedbackCount = historicalFeedback.Count;
                        _state.AverageFeedbackScore = historicalFeedback.Average(f => f.Score);
                    }

                    _logger.LogInformation("Loaded {Count} historical feedback items", historicalFeedback.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load historical feedback");
            }
        }

        private async Task InitializeFeedbackPipelineAsync(CancellationToken cancellationToken)
        {
            // Geri bildirim işleme pipeline'ını başlat;
            // Burada çeşitli pipeline bileşenleri başlatılır;
            await Task.Delay(100, cancellationToken); // Simülasyon;

            _logger.LogDebug("Feedback pipeline initialized");
        }

        private async Task<ProcessedFeedback> ProcessFeedbackAsync(
            FeedbackData feedback,
            FeedbackQuality quality,
            CancellationToken cancellationToken)
        {
            // Geri bildirim önişleme;
            var preprocessed = PreprocessFeedback(feedback);

            // Özellik çıkarımı;
            var features = ExtractFeedbackFeatures(preprocessed);

            // Anomali tespiti;
            var isAnomaly = await DetectFeedbackAnomalyAsync(features, cancellationToken);

            return new ProcessedFeedback;
            {
                OriginalFeedback = feedback,
                PreprocessedData = preprocessed,
                Features = features,
                Quality = quality,
                IsAnomaly = isAnomaly,
                ProcessingTimestamp = DateTime.UtcNow,
                ProcessingVersion = GetCurrentProcessingVersion()
            };
        }

        private async Task<LearningAdjustment> UpdateLearningFromFeedbackAsync(
            ProcessedFeedback feedback,
            CancellationToken cancellationToken)
        {
            // Geri bildirime göre öğrenme parametrelerini ayarla;
            var adjustmentRequest = new AdjustmentRequest;
            {
                AdjustmentType = AdjustmentType.Incremental,
                FeedbackData = feedback,
                Priority = CalculateAdjustmentPriority(feedback),
                LookbackPeriod = TimeSpan.FromHours(24)
            };

            return await AdjustLearningAsync(adjustmentRequest, cancellationToken);
        }

        private void UpdateStateWithNewFeedback(ProcessedFeedback feedback, FeedbackQuality quality)
        {
            lock (_stateLock)
            {
                _state.TotalFeedbackCount++;
                _state.LastFeedbackTime = DateTime.UtcNow;
                _state.AverageFeedbackScore =
                    ((_state.AverageFeedbackScore * (_state.TotalFeedbackCount - 1)) + feedback.OriginalFeedback.Score)
                    / _state.TotalFeedbackCount;

                if (feedback.IsAnomaly)
                {
                    _state.AnomalyCount++;
                }

                if (quality.Score >= _options.Value.HighQualityThreshold)
                {
                    _state.HighQualityFeedbackCount++;
                }
            }
        }

        private async Task OptimizeAfterBatchProcessingAsync(int successfulCount, CancellationToken cancellationToken)
        {
            // Toplu işlem sonrası optimizasyon;
            if (successfulCount >= _options.Value.BatchOptimizationThreshold)
            {
                _logger.LogDebug("Running post-batch optimization for {Count} items", successfulCount);

                await _feedbackStorage.OptimizeStorageAsync(cancellationToken);

                // Önbellek temizleme;
                ClearFeedbackCache();

                // İstatistik güncelleme;
                await UpdateStatisticsAsync(cancellationToken);
            }
        }

        private async Task<FeedbackAnalysis> AnalyzeRecentFeedbackAsync(
            TimeSpan lookbackPeriod,
            CancellationToken cancellationToken)
        {
            var recentFeedback = await _feedbackStorage.GetFeedbackInPeriodAsync(
                DateTime.UtcNow - lookbackPeriod,
                DateTime.UtcNow,
                cancellationToken);

            return new FeedbackAnalysis;
            {
                TotalFeedback = recentFeedback.Count,
                AverageScore = recentFeedback.Any() ? recentFeedback.Average(f => f.Score) : 0,
                ScoreDistribution = CalculateScoreDistribution(recentFeedback),
                CommonIssues = ExtractCommonIssues(recentFeedback),
                TrendingTopics = DetectTrendingTopics(recentFeedback),
                AnalysisTimestamp = DateTime.UtcNow;
            };
        }

        private LearningAdjustment CalculateLearningAdjustments(
            CurrentMetrics currentMetrics,
            FeedbackAnalysis feedbackAnalysis,
            AdjustmentRequest request)
        {
            var adjustment = new LearningAdjustment;
            {
                RequestId = request.RequestId,
                AdjustmentType = request.AdjustmentType,
                AppliedAt = DateTime.UtcNow,
                Parameters = new Dictionary<string, object>()
            };

            // Geri bildirim skoruna göre learning rate ayarı;
            if (feedbackAnalysis.AverageScore < 0.3)
            {
                // Düşük skor - learning rate'i düşür;
                adjustment.Parameters["learning_rate"] = currentMetrics.LearningRate * 0.5;
                adjustment.Reason = "Low feedback scores detected";
            }
            else if (feedbackAnalysis.AverageScore > 0.8)
            {
                // Yüksek skor - learning rate'i artır;
                adjustment.Parameters["learning_rate"] = currentMetrics.LearningRate * 1.2;
                adjustment.Reason = "High feedback scores detected";
            }

            // Anomali tespiti varsa;
            if (feedbackAnalysis.TotalFeedback > 10 &&
                feedbackAnalysis.CommonIssues.Any(i => i.Severity > 0.7))
            {
                adjustment.Parameters["regularization"] = currentMetrics.Regularization * 1.5;
                adjustment.Parameters["batch_size"] = Math.Max(32, currentMetrics.BatchSize / 2);
                adjustment.Reason += "; High severity issues detected";
            }

            return adjustment;
        }

        private async Task<LearningAdjustment> ApplyAdjustmentsToTrainingEngineAsync(
            LearningAdjustment adjustment,
            CancellationToken cancellationToken)
        {
            // Eğitim motoruna ayarlamaları uygula;
            await _trainingEngine.AdjustParametersAsync(adjustment.Parameters, cancellationToken);

            adjustment.AppliedSuccessfully = true;
            adjustment.ActualApplicationTime = DateTime.UtcNow;

            return adjustment;
        }

        private async Task StartAdjustmentMonitoringAsync(
            LearningAdjustment adjustment,
            CancellationToken cancellationToken)
        {
            // Ayarlama sonrası performans izleme;
            _ = Task.Run(async () =>
            {
                try
                {
                    await MonitorAdjustmentImpactAsync(adjustment, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to monitor adjustment impact");
                }
            }, cancellationToken);
        }

        private async Task MonitorAdjustmentImpactAsync(
            LearningAdjustment adjustment,
            CancellationToken cancellationToken)
        {
            var monitoringDuration = _options.Value.AdjustmentMonitoringDuration;
            var endTime = DateTime.UtcNow + monitoringDuration;

            while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);

                var metrics = await _metricsCalculator.CalculateCurrentMetricsAsync(cancellationToken);

                // Performans değişikliğini kontrol et;
                var change = CalculatePerformanceChange(metrics);

                if (Math.Abs(change) > 0.1) // %10'dan fazla değişiklik;
                {
                    _logger.LogInformation("Significant performance change detected after adjustment: {Change}%",
                        change * 100);

                    // Gerekirse ek ayarlamalar yap;
                    if (change < -0.15) // %15'ten fazla düşüş;
                    {
                        await RollbackAdjustmentIfNeededAsync(adjustment, cancellationToken);
                        break;
                    }
                }
            }
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _processingLock?.Dispose();

                    lock (_stateLock)
                    {
                        _state.Status = EngineStatus.Stopped;
                    }

                    _logger.LogInformation("FeedbackEngine disposed");
                }

                _isDisposed = true;
            }
        }

        ~FeedbackEngine()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Geri bildirim motoru seçenekleri;
    /// </summary>
    public class FeedbackEngineOptions;
    {
        public int MaxBatchProcessingParallelism { get; set; } = 4;
        public double MinimumQualityThreshold { get; set; } = 0.3;
        public double HighQualityThreshold { get; set; } = 0.8;
        public int BatchOptimizationThreshold { get; set; } = 100;
        public int HistoricalDataLookbackDays { get; set; } = 30;
        public double MinimumSuccessRate { get; set; } = 0.7;
        public TimeSpan AdjustmentMonitoringDuration { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Geri bildirim verisi;
    /// </summary>
    public class FeedbackData;
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public DateTime Timestamp { get; set; }
        public double Score { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public string FeedbackType { get; set; }
        public string Content { get; set; }
        public List<string> Tags { get; set; }
    }

    /// <summary>
    /// Geri bildirim sonucu;
    /// </summary>
    public class FeedbackResult;
    {
        public bool Success { get; set; }
        public string FeedbackId { get; set; }
        public string Message { get; set; }
        public double QualityScore { get; set; }
        public LearningAdjustment AdjustmentsMade { get; set; }
        public DateTime ProcessedAt { get; set; }
        public List<string> Recommendations { get; set; }
    }

    /// <summary>
    /// Toplu geri bildirim sonucu;
    /// </summary>
    public class BatchFeedbackResult;
    {
        public int TotalProcessed { get; set; }
        public int Successful { get; set; }
        public int Failed { get; set; }
        public List<FeedbackResult> Results { get; set; }
        public DateTime BatchCompletedAt { get; set; }
    }

    /// <summary>
    /// Geri bildirim kalitesi;
    /// </summary>
    public class FeedbackQuality;
    {
        public double Score { get; set; }
        public List<string> Issues { get; set; }
        public List<string> Recommendations { get; set; }
        public Dictionary<string, double> QualityMetrics { get; set; }
    }

    /// <summary>
    /// Öğrenme ayarlaması;
    /// </summary>
    public class LearningAdjustment;
    {
        public string RequestId { get; set; }
        public AdjustmentType AdjustmentType { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public bool AppliedSuccessfully { get; set; }
        public DateTime AppliedAt { get; set; }
        public DateTime? ActualApplicationTime { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Geri bildirim motoru durumu;
    /// </summary>
    public class FeedbackEngineState;
    {
        public EngineStatus Status { get; set; }
        public DateTime? InitializedTime { get; set; }
        public int TotalFeedbackCount { get; set; }
        public int HighQualityFeedbackCount { get; set; }
        public int AnomalyCount { get; set; }
        public DateTime? LastFeedbackTime { get; set; }
        public DateTime? LastCycleTime { get; set; }
        public bool LastCycleSuccess { get; set; }
        public int CycleCount { get; set; }
        public double AverageFeedbackScore { get; set; }
        public string LastError { get; set; }

        public FeedbackEngineState Clone()
        {
            return (FeedbackEngineState)MemberwiseClone();
        }
    }

    /// <summary>
    /// Motor durum enum;
    /// </summary>
    public enum EngineStatus;
    {
        NotInitialized,
        Initializing,
        Running,
        Paused,
        Error,
        Stopped;
    }

    /// <summary>
    /// Ayarlama tipi enum;
    /// </summary>
    public enum AdjustmentType;
    {
        Incremental,
        Corrective,
        Optimizing,
        Emergency;
    }

    /// <summary>
    /// Geri bildirim motoru exception;
    /// </summary>
    public class FeedbackEngineException : Exception
    {
        public FeedbackEngineException(string message) : base(message) { }
        public FeedbackEngineException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Geçersiz geri bildirim exception;
    /// </summary>
    public class InvalidFeedbackException : FeedbackEngineException;
    {
        public InvalidFeedbackException(string message) : base(message) { }
    }

    #endregion;
}
