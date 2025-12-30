using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NEDA.Common;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.Services.Messaging.EventBus;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using System.IO;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace NEDA.Biometrics.FaceRecognition;
{
    /// <summary>
    /// İleri Seviye Yüz Tespit ve Analiz Motoru;
    /// Özellikler: Çoklu model, GPU desteği, real-time işleme, AI optimizasyonu;
    /// Tasarım desenleri: Strategy, Factory, Observer, Builder, Decorator;
    /// </summary>
    public class FaceDetector : IFaceDetector, IDisposable;
    {
        #region Fields;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IErrorReporter _errorReporter;
        private readonly IPerformanceMonitor _performanceMonitor;

        private DetectionEngine _detectionEngine;
        private FaceModel _currentModel;
        private DetectionConfiguration _configuration;
        private DetectionState _state;
        private readonly ModelManager _modelManager;
        private readonly Preprocessor _preprocessor;
        private readonly Postprocessor _postprocessor;
        private readonly QualityAnalyzer _qualityAnalyzer;
        private readonly LandmarkDetector _landmarkDetector;
        private readonly AlignmentEngine _alignmentEngine;
        private readonly FeatureExtractor _featureExtractor;
        private readonly FaceTracker _faceTracker;
        private readonly RealTimeProcessor _realTimeProcessor;
        private readonly InferenceOptimizer _inferenceOptimizer;
        private readonly CancellationTokenSource _cancellationTokenSource;

        private readonly ConcurrentDictionary<string, DetectionSession> _activeSessions;
        private readonly ConcurrentBag<DetectionResult> _detectionHistory;
        private readonly ConcurrentDictionary<DetectionAlgorithm, int> _algorithmStatistics;
        private readonly ConcurrentQueue<DetectionRequest> _requestQueue;
        private readonly ConcurrentDictionary<int, TrackingData> _faceTracking;

        private bool _isInitialized;
        private bool _isProcessing;
        private DateTime _lastDetectionTime;
        private int _totalDetections;
        private int _totalFrames;
        private int _detectionErrors;
        private readonly object _syncLock = new object();
        private readonly List<IDetectionAlgorithm> _algorithms;
        private readonly HardwareAccelerator _hardwareAccelerator;
        private readonly MemoryPool _memoryPool;
        private VideoCapture _videoCapture;
        private Mat _currentFrame;
        private Stopwatch _performanceStopwatch;
        private readonly List<PerformanceMetric> _performanceMetrics;

        #endregion;

        #region Properties;

        /// <summary>
        /// Tespit durumu;
        /// </summary>
        public DetectionState State;
        {
            get => _state;
            private set;
            {
                if (_state != value)
                {
                    _state = value;
                    OnStateChanged();
                }
            }
        }

        /// <summary>
        /// Aktif model;
        /// </summary>
        public FaceModel CurrentModel;
        {
            get => _currentModel;
            private set;
            {
                _currentModel = value;
                OnPropertyChanged(nameof(CurrentModel));
                OnPropertyChanged(nameof(ModelInfo));
            }
        }

        /// <summary>
        /// Model bilgileri;
        /// </summary>
        public ModelInfo ModelInfo => CurrentModel?.Info;

        /// <summary>
        /// Konfigürasyon;
        /// </summary>
        public DetectionConfiguration Configuration;
        {
            get => _configuration;
            set;
            {
                _configuration = value ?? throw new ArgumentNullException(nameof(value));
                UpdateConfiguration();
            }
        }

        /// <summary>
        /// Başlatıldı mı?
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// İşlemde mi?
        /// </summary>
        public bool IsProcessing => _isProcessing;

        /// <summary>
        /// Aktif session'lar;
        /// </summary>
        public IReadOnlyDictionary<string, DetectionSession> ActiveSessions => _activeSessions;

        /// <summary>
        /// Algoritma istatistikleri;
        /// </summary>
        public IReadOnlyDictionary<DetectionAlgorithm, int> AlgorithmStatistics => _algorithmStatistics;

        /// <summary>
        /// Tespit geçmişi;
        /// </summary>
        public IReadOnlyCollection<DetectionResult> DetectionHistory => _detectionHistory;

        /// <summary>
        /// Toplam tespit sayısı;
        /// </summary>
        public int TotalDetections => _totalDetections;

        /// <summary>
        /// Toplam frame sayısı;
        /// </summary>
        public int TotalFrames => _totalFrames;

        /// <summary>
        /// Tespit hata oranı;
        /// </summary>
        public double DetectionErrorRate => _totalFrames > 0 ? (double)_detectionErrors / _totalFrames : 0;

        /// <summary>
        /// Son tespit zamanı;
        /// </summary>
        public DateTime LastDetectionTime => _lastDetectionTime;

        /// <summary>
        /// FPS (Frames Per Second)
        /// </summary>
        public double CurrentFPS { get; private set; }

        /// <summary>
        /// Ortalama tespit süresi (ms)
        /// </summary>
        public double AverageDetectionTimeMs { get; private set; }

        /// <summary>
        /// Performans metrikleri;
        /// </summary>
        public IReadOnlyList<PerformanceMetric> PerformanceMetrics => _performanceMetrics;

        /// <summary>
        /// Kullanılabilir algoritmalar;
        /// </summary>
        public IReadOnlyList<IDetectionAlgorithm> AvailableAlgorithms => _algorithms;

        /// <summary>
        /// Takip edilen yüz sayısı;
        /// </summary>
        public int TrackedFacesCount => _faceTracking.Count;

        #endregion;

        #region Events;

        /// <summary>
        /// Yüz tespit edildi event'i;
        /// </summary>
        public event EventHandler<FaceDetectedEventArgs> FaceDetected;

        /// <summary>
        /// Yüz takip edildi event'i;
        /// </summary>
        public event EventHandler<FaceTrackedEventArgs> FaceTracked;

        /// <summary>
        /// Landmark tespit edildi event'i;
        /// </summary>
        public event EventHandler<LandmarksDetectedEventArgs> LandmarksDetected;

        /// <summary>
        /// Yüz özellikleri çıkarıldı event'i;
        /// </summary>
        public event EventHandler<FeaturesExtractedEventArgs> FeaturesExtracted;

        /// <summary>
        /// Kalite analizi tamamlandı event'i;
        /// </summary>
        public event EventHandler<QualityAnalyzedEventArgs> QualityAnalyzed;

        /// <summary>
        /// Durum değişti event'i;
        /// </summary>
        public event EventHandler<DetectionStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Real-time analiz tamamlandı event'i;
        /// </summary>
        public event EventHandler<RealTimeAnalysisCompletedEventArgs> RealTimeAnalysisCompleted;

        /// <summary>
        /// Performans metrikleri güncellendi event'i;
        /// </summary>
        public event EventHandler<PerformanceMetricsUpdatedEventArgs> PerformanceMetricsUpdated;

        #endregion;

        #region Constructor;

        /// <summary>
        /// FaceDetector constructor;
        /// </summary>
        public FaceDetector(
            ILogger logger,
            IEventBus eventBus,
            IErrorReporter errorReporter,
            IPerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            // Bileşenleri başlat;
            _modelManager = new ModelManager(logger);
            _preprocessor = new Preprocessor(logger);
            _postprocessor = new Postprocessor(logger);
            _qualityAnalyzer = new QualityAnalyzer(logger);
            _landmarkDetector = new LandmarkDetector(logger);
            _alignmentEngine = new AlignmentEngine(logger);
            _featureExtractor = new FeatureExtractor(logger);
            _faceTracker = new FaceTracker(logger);
            _realTimeProcessor = new RealTimeProcessor(logger);
            _inferenceOptimizer = new InferenceOptimizer(logger);
            _hardwareAccelerator = new HardwareAccelerator(logger);
            _memoryPool = new MemoryPool(logger);

            _cancellationTokenSource = new CancellationTokenSource();

            // Concurrent koleksiyonlar;
            _activeSessions = new ConcurrentDictionary<string, DetectionSession>();
            _detectionHistory = new ConcurrentBag<DetectionResult>();
            _algorithmStatistics = new ConcurrentDictionary<DetectionAlgorithm, int>();
            _requestQueue = new ConcurrentQueue<DetectionRequest>();
            _faceTracking = new ConcurrentDictionary<int, TrackingData>();

            // Algoritmaları başlat;
            _algorithms = InitializeAlgorithms();

            // Performans araçları;
            _performanceStopwatch = new Stopwatch();
            _performanceMetrics = new List<PerformanceMetric>();

            // Varsayılan konfigürasyon;
            _configuration = DetectionConfiguration.Default;

            State = DetectionState.Stopped;

            _logger.LogInformation("FaceDetector initialized successfully", GetType());
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Yüz tespitörünü başlat;
        /// </summary>
        public async Task InitializeAsync(DetectionConfiguration configuration = null)
        {
            try
            {
                _performanceMonitor.StartOperation("InitializeFaceDetector");
                _logger.LogDebug("Initializing face detector");

                if (_isInitialized)
                {
                    _logger.LogWarning("Face detector is already initialized");
                    return;
                }

                // Konfigürasyonu ayarla;
                if (configuration != null)
                {
                    _configuration = configuration;
                }

                // Donanım hızlandırıcıyı başlat;
                await _hardwareAccelerator.InitializeAsync(_configuration.HardwareConfig);

                // Model yükle;
                await LoadOrCreateModelAsync();

                // Ön işlemciyi yapılandır;
                _preprocessor.Configure(_configuration.PreprocessingConfig);

                // Tespit motorunu oluştur;
                _detectionEngine = CreateDetectionEngine(_configuration.DetectionMode);
                await _detectionEngine.InitializeAsync(_currentModel);

                // Algoritmaları yapılandır;
                foreach (var algorithm in _algorithms)
                {
                    algorithm.Configure(_configuration);
                }

                // Bellek havuzunu başlat;
                _memoryPool.Initialize(_configuration.MemoryConfig);

                // Çıkarım optimizasyonu;
                await _inferenceOptimizer.OptimizeAsync(_currentModel, _configuration.InferenceConfig);

                // Gerçek zamanlı işlemciyi başlat;
                await _realTimeProcessor.StartAsync(_cancellationTokenSource.Token);

                _isInitialized = true;
                State = DetectionState.Ready;

                // Performans izleme başlat;
                StartPerformanceMonitoring();

                _logger.LogInformation("Face detector initialized successfully");
                _eventBus.Publish(new FaceDetectorInitializedEvent(this));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize face detector");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.InitializationFailed);
                throw new FaceDetectionException("Failed to initialize face detector", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("InitializeFaceDetector");
            }
        }

        /// <summary>
        /// Yeni tespit session'ı başlat;
        /// </summary>
        public async Task<DetectionSession> StartSessionAsync(
            string sessionId,
            SessionType sessionType,
            SessionContext context = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("StartDetectionSession");
                _logger.LogDebug($"Starting detection session: {sessionId}, type: {sessionType}");

                // Session var mı kontrol et;
                if (_activeSessions.ContainsKey(sessionId))
                {
                    _logger.LogWarning($"Session already exists: {sessionId}");
                    return _activeSessions[sessionId];
                }

                // Yeni session oluştur;
                var session = new DetectionSession(sessionId, sessionType, context ?? SessionContext.Default)
                {
                    StartTime = DateTime.UtcNow,
                    Status = SessionStatus.Active,
                    Configuration = _configuration;
                };

                // Session'a ekle;
                if (!_activeSessions.TryAdd(sessionId, session))
                {
                    throw new InvalidOperationException($"Failed to add session: {sessionId}");
                }

                // Gerçek zamanlı işlemciye bildir;
                await _realTimeProcessor.RegisterSessionAsync(session);

                // Video yakalama başlat (eğer video session ise)
                if (sessionType == SessionType.VideoStream || sessionType == SessionType.Webcam)
                {
                    await StartVideoCaptureAsync(session);
                }

                // Event tetikle;
                _eventBus.Publish(new DetectionSessionStartedEvent(session));

                _logger.LogInformation($"Detection session started: {sessionId}");

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start detection session");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.StartSessionFailed);
                throw new FaceDetectionException("Failed to start detection session", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StartDetectionSession");
            }
        }

        /// <summary>
        /// Görüntüde yüz tespiti yap;
        /// </summary>
        public async Task<DetectionResult> DetectFacesAsync(
            byte[] imageData,
            string sessionId = null,
            DetectionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("DetectFaces");
                _logger.LogDebug($"Detecting faces in image, size: {imageData.Length} bytes");

                _performanceStopwatch.Restart();

                // Session kontrolü;
                DetectionSession session = null;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    _activeSessions.TryGetValue(sessionId, out session);
                }

                // Seçenekleri hazırla;
                var detectionOptions = options ?? DetectionOptions.Default;

                // Bellekten görüntü al;
                using var imageBuffer = _memoryPool.Allocate(imageData.Length);
                imageData.CopyTo(imageBuffer.Memory);

                // Görüntüyü yükle;
                using var mat = await LoadImageAsync(imageBuffer.Memory);
                if (mat == null || mat.Empty())
                {
                    throw new FaceDetectionException("Failed to load image");
                }

                // Ön işleme;
                var processedImage = await _preprocessor.ProcessAsync(mat, detectionOptions.Preprocessing);

                // Tespit yap;
                var detectionResult = await PerformDetectionAsync(processedImage, detectionOptions, session);

                // Son işleme;
                detectionResult = await _postprocessor.ProcessAsync(detectionResult, detectionOptions.Postprocessing);

                // Kalite analizi;
                if (detectionOptions.AnalyzeQuality && detectionResult.Faces.Any())
                {
                    detectionResult = await AnalyzeQualityAsync(detectionResult, processedImage);
                }

                // Landmark tespiti;
                if (detectionOptions.DetectLandmarks && detectionResult.Faces.Any())
                {
                    detectionResult = await DetectLandmarksAsync(detectionResult, processedImage);
                }

                // Yüz hizalaması;
                if (detectionOptions.AlignFaces && detectionResult.Faces.Any(f => f.Landmarks?.Any() == true))
                {
                    detectionResult = await AlignFacesAsync(detectionResult, processedImage);
                }

                // Özellik çıkarımı;
                if (detectionOptions.ExtractFeatures && detectionResult.Faces.Any())
                {
                    detectionResult = await ExtractFeaturesAsync(detectionResult, processedImage);
                }

                // Session bilgilerini ekle;
                if (session != null)
                {
                    detectionResult.SessionId = sessionId;
                    session.TotalFrames++;
                    session.LastActivity = DateTime.UtcNow;
                }

                // İstatistikleri güncelle;
                UpdateStatistics(detectionResult);

                // Tespit geçmişine ekle;
                _detectionHistory.Add(detectionResult);

                // Event tetikle;
                OnFaceDetected(detectionResult);

                _performanceStopwatch.Stop();
                UpdatePerformanceMetrics(_performanceStopwatch.ElapsedMilliseconds, detectionResult.Faces.Count);

                _logger.LogDebug($"Face detection completed: {detectionResult.Faces.Count} faces found");

                return detectionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect faces");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.DetectionFailed);
                throw new FaceDetectionException("Failed to detect faces", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("DetectFaces");
            }
        }

        /// <summary>
        /// Batch görüntüde yüz tespiti yap;
        /// </summary>
        public async Task<BatchDetectionResult> DetectFacesBatchAsync(
            IEnumerable<byte[]> imagesData,
            string sessionId = null,
            DetectionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("DetectFacesBatch");
                _logger.LogDebug($"Detecting faces in batch of {imagesData.Count()} images");

                var batchResults = new List<DetectionResult>();
                var failedImages = new List<FailedImage>();
                var totalFaces = 0;

                // Paralel işleme;
                var tasks = imagesData.Select(async (imageData, index) =>
                {
                    try
                    {
                        var result = await DetectFacesAsync(imageData, sessionId, options);
                        lock (batchResults)
                        {
                            batchResults.Add(result);
                            totalFaces += result.Faces.Count;
                        }
                        return (Success: true, Index: index, Result: result, Error: (string)null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to detect faces in image {index}");
                        return (Success: false, Index: index, Result: (DetectionResult)null, Error: ex.Message);
                    }
                });

                var results = await Task.WhenAll(tasks);

                // Başarısız görüntüleri topla;
                foreach (var result in results.Where(r => !r.Success))
                {
                    failedImages.Add(new FailedImage;
                    {
                        Index = result.Index,
                        Error = result.Error,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var batchResult = new BatchDetectionResult;
                {
                    Timestamp = DateTime.UtcNow,
                    TotalImages = imagesData.Count(),
                    SuccessfulDetections = batchResults.Count,
                    FailedDetections = failedImages.Count,
                    TotalFaces = totalFaces,
                    Results = batchResults,
                    FailedImages = failedImages,
                    AverageFacesPerImage = batchResults.Any() ? (double)totalFaces / batchResults.Count : 0;
                };

                // Event tetikle;
                _eventBus.Publish(new BatchDetectionCompletedEvent(batchResult));

                _logger.LogInformation($"Batch detection completed: {batchResults.Count} successful, {failedImages.Count} failed, {totalFaces} total faces");

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect faces batch");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.BatchDetectionFailed);
                throw new FaceDetectionException("Failed to detect faces batch", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("DetectFacesBatch");
            }
        }

        /// <summary>
        /// Videoda yüz tespiti yap;
        /// </summary>
        public async Task<VideoDetectionResult> DetectFacesInVideoAsync(
            string videoPath,
            string sessionId = null,
            VideoDetectionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("DetectFacesInVideo");
                _logger.LogDebug($"Detecting faces in video: {videoPath}");

                if (!File.Exists(videoPath))
                {
                    throw new FileNotFoundException($"Video file not found: {videoPath}");
                }

                // Session oluştur;
                var session = await StartSessionAsync(
                    sessionId ?? Guid.NewGuid().ToString(),
                    SessionType.VideoStream,
                    new SessionContext { VideoPath = videoPath });

                var detectionOptions = options ?? VideoDetectionOptions.Default;
                var frameResults = new List<FrameDetectionResult>();
                var totalFaces = 0;
                var frameCount = 0;

                // Video yakalayıcı oluştur;
                using var capture = new VideoCapture(videoPath);
                if (!capture.IsOpened())
                {
                    throw new FaceDetectionException($"Failed to open video: {videoPath}");
                }

                var fps = capture.Fps;
                var totalFrames = capture.FrameCount;
                var frameInterval = detectionOptions.FrameInterval;

                _logger.LogInformation($"Video info: {totalFrames} frames, {fps} FPS");

                // Frame'leri işle;
                using var frame = new Mat();
                while (capture.Read(frame) && !frame.Empty())
                {
                    if (frameCount % frameInterval != 0)
                    {
                        frameCount++;
                        continue;
                    }

                    try
                    {
                        // Frame'i işle;
                        var frameData = frame.ToBytes(".jpg");
                        var detectionResult = await DetectFacesAsync(frameData, session.SessionId, detectionOptions.DetectionOptions);

                        var frameResult = new FrameDetectionResult;
                        {
                            FrameNumber = frameCount,
                            Timestamp = TimeSpan.FromSeconds(frameCount / fps),
                            DetectionResult = detectionResult,
                            FrameSize = new Size(frame.Width, frame.Height)
                        };

                        frameResults.Add(frameResult);
                        totalFaces += detectionResult.Faces.Count;

                        // Yüz takibi;
                        if (detectionOptions.EnableTracking && detectionResult.Faces.Any())
                        {
                            await TrackFacesAsync(detectionResult, frameCount);
                        }

                        // İlerleme raporu;
                        if (frameCount % 100 == 0)
                        {
                            _logger.LogDebug($"Processed {frameCount}/{totalFrames} frames, {totalFaces} faces detected");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to process frame {frameCount}");
                    }

                    frameCount++;
                }

                // Session'ı sonlandır;
                await EndSessionAsync(session.SessionId, SessionEndReason.Completed);

                var videoResult = new VideoDetectionResult;
                {
                    VideoPath = videoPath,
                    SessionId = session.SessionId,
                    Timestamp = DateTime.UtcNow,
                    TotalFrames = frameCount,
                    ProcessedFrames = frameResults.Count,
                    TotalFaces = totalFaces,
                    FrameResults = frameResults,
                    VideoInfo = new VideoInfo;
                    {
                        Duration = TimeSpan.FromSeconds(totalFrames / fps),
                        FrameRate = fps,
                        Resolution = new Size(capture.FrameWidth, capture.FrameHeight),
                        TotalFrames = totalFrames;
                    }
                };

                _logger.LogInformation($"Video detection completed: {totalFaces} faces in {frameResults.Count} frames");

                return videoResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect faces in video");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.VideoDetectionFailed);
                throw new FaceDetectionException("Failed to detect faces in video", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("DetectFacesInVideo");
            }
        }

        /// <summary>
        /// Gerçek zamanlı yüz tespiti başlat;
        /// </summary>
        public async Task StartRealTimeDetectionAsync(
            RealTimeSource source,
            string sessionId = null,
            RealTimeOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("StartRealTimeDetection");
                _logger.LogDebug($"Starting real-time detection from source: {source}");

                if (State == DetectionState.Processing)
                {
                    _logger.LogWarning("Real-time detection is already running");
                    return;
                }

                // Session oluştur;
                var session = await StartSessionAsync(
                    sessionId ?? Guid.NewGuid().ToString(),
                    SessionType.RealTime,
                    new SessionContext { Source = source.ToString() });

                var realTimeOptions = options ?? RealTimeOptions.Default;

                State = DetectionState.Processing;

                // Video yakalama başlat;
                await StartVideoCaptureAsync(session, source);

                // Gerçek zamanlı işleme döngüsü;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (State == DetectionState.Processing &&
                               !_cancellationTokenSource.Token.IsCancellationRequested &&
                               _videoCapture != null && _videoCapture.IsOpened())
                        {
                            await ProcessRealTimeFrameAsync(session, realTimeOptions);

                            // FPS kontrolü;
                            if (realTimeOptions.TargetFPS > 0)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1.0 / realTimeOptions.TargetFPS));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Real-time detection cancelled");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Real-time detection failed");
                        _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.RealTimeDetectionFailed);
                    }
                    finally
                    {
                        State = DetectionState.Ready;
                    }
                }, _cancellationTokenSource.Token);

                _logger.LogInformation("Real-time detection started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start real-time detection");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.StartRealTimeFailed);
                throw new FaceDetectionException("Failed to start real-time detection", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StartRealTimeDetection");
            }
        }

        /// <summary>
        /// Gerçek zamanlı tespiti durdur;
        /// </summary>
        public async Task StopRealTimeDetectionAsync(string sessionId)
        {
            try
            {
                _performanceMonitor.StartOperation("StopRealTimeDetection");
                _logger.LogDebug($"Stopping real-time detection for session: {sessionId}");

                if (State != DetectionState.Processing)
                {
                    _logger.LogWarning("Real-time detection is not running");
                    return;
                }

                State = DetectionState.Ready;

                // Video yakalamayı durdur;
                StopVideoCapture();

                // Session'ı sonlandır;
                await EndSessionAsync(sessionId, SessionEndReason.Stopped);

                _logger.LogInformation("Real-time detection stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop real-time detection");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.StopRealTimeFailed);
                throw new FaceDetectionException("Failed to stop real-time detection", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StopRealTimeDetection");
            }
        }

        /// <summary>
        /// Yüz takibi başlat;
        /// </summary>
        public async Task StartFaceTrackingAsync(
            string sessionId,
            TrackingOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("StartFaceTracking");
                _logger.LogDebug($"Starting face tracking for session: {sessionId}");

                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    throw new InvalidOperationException($"Session not found: {sessionId}");
                }

                var trackingOptions = options ?? TrackingOptions.Default;

                // Face tracker'ı başlat;
                await _faceTracker.InitializeAsync(session, trackingOptions);

                session.IsTrackingEnabled = true;

                _logger.LogInformation("Face tracking started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start face tracking");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.StartTrackingFailed);
                throw new FaceDetectionException("Failed to start face tracking", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StartFaceTracking");
            }
        }

        /// <summary>
        /// Landmark tespiti yap;
        /// </summary>
        public async Task<LandmarkDetectionResult> DetectLandmarksAsync(
            byte[] faceImage,
            Rectangle faceRect,
            LandmarkOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("DetectLandmarks");
                _logger.LogDebug($"Detecting landmarks for face at {faceRect}");

                // Yüz görüntüsünü kırp;
                using var mat = await LoadImageAsync(faceImage);
                using var faceMat = new Mat(mat, faceRect);

                if (faceMat.Empty())
                {
                    throw new FaceDetectionException("Failed to extract face image");
                }

                var landmarkOptions = options ?? LandmarkOptions.Default;

                // Landmark tespiti yap;
                var landmarks = await _landmarkDetector.DetectAsync(faceMat, landmarkOptions);

                var result = new LandmarkDetectionResult;
                {
                    FaceRectangle = faceRect,
                    Landmarks = landmarks,
                    Confidence = landmarks.Average(l => l.Confidence),
                    Timestamp = DateTime.UtcNow;
                };

                // Event tetikle;
                OnLandmarksDetected(result);

                _logger.LogDebug($"Landmarks detected: {landmarks.Count} points");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect landmarks");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.LandmarkDetectionFailed);
                throw new FaceDetectionException("Failed to detect landmarks", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("DetectLandmarks");
            }
        }

        /// <summary>
        /// Yüz özelliklerini çıkar;
        /// </summary>
        public async Task<FeatureExtractionResult> ExtractFeaturesAsync(
            byte[] faceImage,
            Rectangle faceRect,
            ExtractionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("ExtractFeatures");
                _logger.LogDebug($"Extracting features for face at {faceRect}");

                // Yüz görüntüsünü kırp;
                using var mat = await LoadImageAsync(faceImage);
                using var faceMat = new Mat(mat, faceRect);

                if (faceMat.Empty())
                {
                    throw new FaceDetectionException("Failed to extract face image");
                }

                var extractionOptions = options ?? ExtractionOptions.Default;

                // Özellik çıkarımı yap;
                var features = await _featureExtractor.ExtractAsync(faceMat, extractionOptions);

                var result = new FeatureExtractionResult;
                {
                    FaceRectangle = faceRect,
                    Features = features,
                    FeatureVector = features.ToFeatureVector(),
                    Confidence = features.Confidence,
                    Timestamp = DateTime.UtcNow;
                };

                // Event tetikle;
                OnFeaturesExtracted(result);

                _logger.LogDebug($"Features extracted: {features.FeatureCount} features");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract features");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.FeatureExtractionFailed);
                throw new FaceDetectionException("Failed to extract features", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("ExtractFeatures");
            }
        }

        /// <summary>
        /// Yüz kalitesini analiz et;
        /// </summary>
        public async Task<QualityAnalysisResult> AnalyzeQualityAsync(
            byte[] faceImage,
            Rectangle faceRect,
            QualityOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("AnalyzeQuality");
                _logger.LogDebug($"Analyzing quality for face at {faceRect}");

                // Yüz görüntüsünü kırp;
                using var mat = await LoadImageAsync(faceImage);
                using var faceMat = new Mat(mat, faceRect);

                if (faceMat.Empty())
                {
                    throw new FaceDetectionException("Failed to extract face image");
                }

                var qualityOptions = options ?? QualityOptions.Default;

                // Kalite analizi yap;
                var qualityResult = await _qualityAnalyzer.AnalyzeAsync(faceMat, qualityOptions);

                var result = new QualityAnalysisResult;
                {
                    FaceRectangle = faceRect,
                    QualityScore = qualityResult.OverallScore,
                    BlurScore = qualityResult.BlurScore,
                    LightingScore = qualityResult.LightingScore,
                    PoseScore = qualityResult.PoseScore,
                    OcclusionScore = qualityResult.OcclusionScore,
                    IsAcceptable = qualityResult.IsAcceptable,
                    Recommendations = qualityResult.Recommendations,
                    Timestamp = DateTime.UtcNow;
                };

                // Event tetikle;
                OnQualityAnalyzed(result);

                _logger.LogDebug($"Quality analyzed: {qualityResult.OverallScore:F2} score");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze quality");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.QualityAnalysisFailed);
                throw new FaceDetectionException("Failed to analyze quality", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("AnalyzeQuality");
            }
        }

        /// <summary>
        /// Yüz hizalaması yap;
        /// </summary>
        public async Task<AlignmentResult> AlignFaceAsync(
            byte[] faceImage,
            List<LandmarkPoint> landmarks,
            AlignmentOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("AlignFace");
                _logger.LogDebug($"Aligning face with {landmarks.Count} landmarks");

                using var mat = await LoadImageAsync(faceImage);
                if (mat.Empty())
                {
                    throw new FaceDetectionException("Failed to load face image");
                }

                var alignmentOptions = options ?? AlignmentOptions.Default;

                // Yüz hizalaması yap;
                var alignedImage = await _alignmentEngine.AlignAsync(mat, landmarks, alignmentOptions);

                var result = new AlignmentResult;
                {
                    OriginalImage = faceImage,
                    AlignedImage = alignedImage.ToBytes(".jpg"),
                    TransformationMatrix = alignedImage.TransformationMatrix,
                    AlignmentScore = alignedImage.AlignmentScore,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogDebug($"Face aligned successfully: {alignedImage.AlignmentScore:F2} score");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to align face");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.AlignmentFailed);
                throw new FaceDetectionException("Failed to align face", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("AlignFace");
            }
        }

        /// <summary>
        /// Session'ı sonlandır;
        /// </summary>
        public async Task<SessionAnalysisResult> EndSessionAsync(
            string sessionId,
            SessionEndReason reason = SessionEndReason.Completed)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("EndDetectionSession");
                _logger.LogDebug($"Ending detection session: {sessionId}, reason: {reason}");

                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    throw new InvalidOperationException($"Session not found: {sessionId}");
                }

                // Session durumunu güncelle;
                session.EndTime = DateTime.UtcNow;
                session.Status = SessionStatus.Completed;
                session.EndReason = reason;

                // Video yakalamayı durdur;
                if (session.SessionType == SessionType.VideoStream ||
                    session.SessionType == SessionType.Webcam ||
                    session.SessionType == SessionType.RealTime)
                {
                    StopVideoCapture();
                }

                // Final analizi yap;
                var analysisResult = await AnalyzeSessionAsync(session);
                session.AnalysisResult = analysisResult;

                // Session'ı aktif listesinden çıkar;
                if (!_activeSessions.TryRemove(sessionId, out _))
                {
                    _logger.LogWarning($"Failed to remove session: {sessionId}");
                }

                // Gerçek zamanlı işlemciye bildir;
                await _realTimeProcessor.UnregisterSessionAsync(sessionId);

                // Event tetikle;
                _eventBus.Publish(new DetectionSessionEndedEvent(session, analysisResult));

                _logger.LogInformation($"Detection session ended: {sessionId}");

                return analysisResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to end detection session");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.EndSessionFailed);
                throw new FaceDetectionException("Failed to end detection session", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("EndDetectionSession");
            }
        }

        /// <summary>
        /// Modeli eğit/güncelle;
        /// </summary>
        public async Task<TrainingResult> TrainModelAsync(
            IEnumerable<TrainingSample> trainingData,
            TrainingConfiguration trainingConfig = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("TrainFaceModel");
                _logger.LogDebug("Training face detection model");

                if (_isProcessing)
                {
                    throw new InvalidOperationException("Training is already in progress");
                }

                _isProcessing = true;
                State = DetectionState.Training;

                // Eğitim konfigürasyonu;
                var config = trainingConfig ?? TrainingConfiguration.Default;

                // Veriyi hazırla;
                var preparedData = await PrepareTrainingDataAsync(trainingData);

                // Model eğit;
                var trainingResult = await _modelManager.TrainAsync(
                    _currentModel,
                    preparedData,
                    config,
                    _cancellationTokenSource.Token);

                // Modeli güncelle;
                CurrentModel = trainingResult.TrainedModel;

                // Modeli kaydet;
                await _modelManager.SaveModelAsync(CurrentModel);

                // Tespit motorunu güncelle;
                await _detectionEngine.UpdateModelAsync(CurrentModel);

                _isProcessing = false;
                State = DetectionState.Ready;

                // Event tetikle;
                _eventBus.Publish(new FaceModelTrainedEvent(trainingResult));

                _logger.LogInformation($"Face model trained successfully: Accuracy={trainingResult.Accuracy:F4}");

                return trainingResult;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Model training was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to train face model");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.TrainingFailed);
                throw new FaceDetectionException("Failed to train face model", ex);
            }
            finally
            {
                _isProcessing = false;
                _performanceMonitor.EndOperation("TrainFaceModel");
            }
        }

        /// <summary>
        /// Performans metriklerini getir;
        /// </summary>
        public PerformanceReport GetPerformanceReport()
        {
            return new PerformanceReport;
            {
                Timestamp = DateTime.UtcNow,
                TotalFrames = TotalFrames,
                TotalDetections = TotalDetections,
                CurrentFPS = CurrentFPS,
                AverageDetectionTimeMs = AverageDetectionTimeMs,
                DetectionErrorRate = DetectionErrorRate,
                TrackedFacesCount = TrackedFacesCount,
                MemoryUsageMB = GetMemoryUsageMB(),
                GPUUsagePercent = GetGPUUsagePercent(),
                PerformanceMetrics = _performanceMetrics.ToList()
            };
        }

        /// <summary>
        /// Yüz tespitörünü sıfırla;
        /// </summary>
        public async Task ResetAsync()
        {
            try
            {
                _performanceMonitor.StartOperation("ResetFaceDetector");
                _logger.LogDebug("Resetting face detector");

                // Aktif session'ları sonlandır;
                foreach (var sessionId in _activeSessions.Keys.ToList())
                {
                    await EndSessionAsync(sessionId, SessionEndReason.SystemReset);
                }

                // Koleksiyonları temizle;
                _detectionHistory.Clear();
                _algorithmStatistics.Clear();
                _faceTracking.Clear();
                _performanceMetrics.Clear();

                while (_requestQueue.TryDequeue(out _)) { }

                // Video yakalamayı durdur;
                StopVideoCapture();

                // Bellek havuzunu temizle;
                _memoryPool.Clear();

                // Modeli sıfırla;
                CurrentModel = await CreateNewModelAsync();

                // Tespit motorunu yeniden başlat;
                await _detectionEngine.InitializeAsync(CurrentModel);

                // İstatistikleri sıfırla;
                _totalDetections = 0;
                _totalFrames = 0;
                _detectionErrors = 0;
                _lastDetectionTime = DateTime.MinValue;
                CurrentFPS = 0;
                AverageDetectionTimeMs = 0;

                State = DetectionState.Ready;

                _logger.LogInformation("Face detector reset successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset face detector");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDetection.ResetFailed);
                throw new FaceDetectionException("Failed to reset face detector", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("ResetFaceDetector");
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Algoritmaları başlat;
        /// </summary>
        private List<IDetectionAlgorithm> InitializeAlgorithms()
        {
            var algorithms = new List<IDetectionAlgorithm>
            {
                new HaarCascadeAlgorithm(_logger),
                new LBPAlgorithm(_logger),
                new DLibHogAlgorithm(_logger),
                new MTCNNAlgorithm(_logger),
                new RetinaFaceAlgorithm(_logger),
                new YOLOFaceAlgorithm(_logger),
                new CenterFaceAlgorithm(_logger),
                new MobileFaceNetAlgorithm(_logger),
                new BlazeFaceAlgorithm(_logger),
                new UltraLightAlgorithm(_logger)
            };

            return algorithms;
        }

        /// <summary>
        /// Model yükle veya oluştur;
        /// </summary>
        private async Task LoadOrCreateModelAsync()
        {
            try
            {
                // Model manager'dan yükle;
                var modelId = $"face_{_configuration.DetectionMode}_{_configuration.ModelType}";
                var model = await _modelManager.LoadModelAsync(modelId);

                if (model != null)
                {
                    CurrentModel = model;
                    _logger.LogInformation($"Loaded existing face model: {model.Info.Name}");
                }
                else;
                {
                    // Yeni model oluştur;
                    CurrentModel = await CreateNewModelAsync();
                    _logger.LogInformation($"Created new face model: {CurrentModel.Info.Name}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load or create face model");
                throw;
            }
        }

        /// <summary>
        /// Yeni model oluştur;
        /// </summary>
        private async Task<FaceModel> CreateNewModelAsync()
        {
            var modelInfo = new ModelInfo;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"FaceDetector_{_configuration.DetectionMode}_{_configuration.ModelType}",
                Version = "1.0.0",
                Algorithm = _configuration.ModelType.ToString(),
                InputSize = _configuration.InputSize,
                CreatedDate = DateTime.UtcNow,
                LastModified = DateTime.UtcNow,
                Parameters = _configuration.GetModelParameters()
            };

            var model = new FaceModel(modelInfo);

            // Modeli kaydet;
            await _modelManager.SaveModelAsync(model);

            return model;
        }

        /// <summary>
        /// Tespit motoru oluştur;
        /// </summary>
        private DetectionEngine CreateDetectionEngine(DetectionMode mode)
        {
            return mode switch;
            {
                DetectionMode.CPU => new CPUDetectionEngine(_logger, _configuration),
                DetectionMode.GPU => new GPUDetectionEngine(_logger, _configuration),
                DetectionMode.NPU => new NPUDetectionEngine(_logger, _configuration),
                DetectionMode.Hybrid => new HybridDetectionEngine(_logger, _configuration),
                DetectionMode.Cloud => new CloudDetectionEngine(_logger, _configuration),
                _ => new CPUDetectionEngine(_logger, _configuration)
            };
        }

        /// <summary>
        /// Görüntü yükle;
        /// </summary>
        private async Task<Mat> LoadImageAsync(byte[] imageData)
        {
            return await Task.Run(() =>
            {
                try
                {
                    return Cv2.ImDecode(imageData, ImreadModes.Color);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to decode image");
                    throw new FaceDetectionException("Failed to decode image", ex);
                }
            });
        }

        private async Task<Mat> LoadImageAsync(Memory<byte> imageData)
        {
            return await LoadImageAsync(imageData.ToArray());
        }

        /// <summary>
        /// Tespit yap;
        /// </summary>
        private async Task<DetectionResult> PerformDetectionAsync(
            Mat image,
            DetectionOptions options,
            DetectionSession session)
        {
            var faces = new List<DetectedFace>();
            var selectedAlgorithm = options.Algorithm ?? _configuration.DefaultAlgorithm;

            // Algoritma seç;
            var algorithm = _algorithms.FirstOrDefault(a => a.AlgorithmType == selectedAlgorithm);
            if (algorithm == null)
            {
                algorithm = _algorithms.First();
                _logger.LogWarning($"Algorithm {selectedAlgorithm} not found, using {algorithm.AlgorithmType}");
            }

            try
            {
                // Tespit yap;
                faces = await algorithm.DetectAsync(image, options, _cancellationTokenSource.Token);

                // Algoritma istatistiklerini güncelle;
                _algorithmStatistics.AddOrUpdate(
                    algorithm.AlgorithmType,
                    1,
                    (_, count) => count + 1);

                _logger.LogDebug($"Algorithm {algorithm.AlgorithmType} detected {faces.Count} faces");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Algorithm {algorithm.AlgorithmType} failed");
                _detectionErrors++;

                // Fallback algoritma kullan;
                if (_configuration.EnableFallback)
                {
                    var fallbackAlgorithm = _algorithms.First(a => a.IsFallback);
                    faces = await fallbackAlgorithm.DetectAsync(image, options, _cancellationTokenSource.Token);
                    _logger.LogDebug($"Fallback algorithm detected {faces.Count} faces");
                }
            }

            // Confidence threshold uygula;
            if (options.ConfidenceThreshold > 0)
            {
                faces = faces.Where(f => f.Confidence >= options.ConfidenceThreshold).ToList();
            }

            // NMS (Non-Maximum Suppression) uygula;
            if (options.EnableNMS && faces.Count > 1)
            {
                faces = ApplyNMS(faces, options.NMSThreshold);
            }

            // Sonuç oluştur;
            var result = new DetectionResult;
            {
                Timestamp = DateTime.UtcNow,
                ImageSize = new Size(image.Width, image.Height),
                Faces = faces,
                AlgorithmUsed = algorithm.AlgorithmType,
                DetectionTimeMs = _performanceStopwatch.ElapsedMilliseconds,
                SessionId = session?.SessionId;
            };

            return result;
        }

        /// <summary>
        /// Non-Maximum Suppression uygula;
        /// </summary>
        private List<DetectedFace> ApplyNMS(List<DetectedFace> faces, double threshold)
        {
            if (!faces.Any())
                return faces;

            var sortedFaces = faces.OrderByDescending(f => f.Confidence).ToList();
            var selectedFaces = new List<DetectedFace>();

            while (sortedFaces.Any())
            {
                // En yüksek confidence'lı yüzü seç;
                var currentFace = sortedFaces[0];
                selectedFaces.Add(currentFace);
                sortedFaces.RemoveAt(0);

                // Overlap hesapla ve threshold'un altındakileri kaldır;
                sortedFaces = sortedFaces.Where(face =>
                {
                    var iou = CalculateIoU(currentFace.BoundingBox, face.BoundingBox);
                    return iou < threshold;
                }).ToList();
            }

            return selectedFaces;
        }

        /// <summary>
        /// IoU (Intersection over Union) hesapla;
        /// </summary>
        private double CalculateIoU(Rectangle rect1, Rectangle rect2)
        {
            var intersection = rect1.Intersect(rect2);
            if (intersection.IsEmpty)
                return 0;

            var intersectionArea = intersection.Width * intersection.Height;
            var unionArea = (rect1.Width * rect1.Height) + (rect2.Width * rect2.Height) - intersectionArea;

            return intersectionArea / (double)unionArea;
        }

        /// <summary>
        /// Kalite analizi yap;
        /// </summary>
        private async Task<DetectionResult> AnalyzeQualityAsync(DetectionResult result, Mat image)
        {
            var analyzedFaces = new List<DetectedFace>();

            foreach (var face in result.Faces)
            {
                try
                {
                    // Yüz görüntüsünü kırp;
                    using var faceMat = new Mat(image, face.BoundingBox);

                    // Kalite analizi yap;
                    var qualityResult = await _qualityAnalyzer.AnalyzeAsync(faceMat, new QualityOptions;
                    {
                        AnalyzeBlur = true,
                        AnalyzeLighting = true,
                        AnalyzePose = true,
                        AnalyzeOcclusion = true,
                        MinAcceptableScore = _configuration.MinQualityScore;
                    });

                    // Yüz bilgilerini güncelle;
                    face.QualityScore = qualityResult.OverallScore;
                    face.IsQualityAcceptable = qualityResult.IsAcceptable;
                    face.QualityAnalysis = new FaceQuality;
                    {
                        BlurScore = qualityResult.BlurScore,
                        LightingScore = qualityResult.LightingScore,
                        PoseScore = qualityResult.PoseScore,
                        OcclusionScore = qualityResult.OcclusionScore,
                        Recommendations = qualityResult.Recommendations;
                    };

                    analyzedFaces.Add(face);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to analyze quality for face at {face.BoundingBox}");
                    analyzedFaces.Add(face); // Analiz edilemeyen yüzü olduğu gibi ekle;
                }
            }

            result.Faces = analyzedFaces;
            return result;
        }

        /// <summary>
        /// Landmark tespiti yap;
        /// </summary>
        private async Task<DetectionResult> DetectLandmarksAsync(DetectionResult result, Mat image)
        {
            var landmarkedFaces = new List<DetectedFace>();

            foreach (var face in result.Faces)
            {
                try
                {
                    // Yüz görüntüsünü kırp;
                    using var faceMat = new Mat(image, face.BoundingBox);

                    // Landmark tespiti yap;
                    var landmarks = await _landmarkDetector.DetectAsync(faceMat, new LandmarkOptions;
                    {
                        ModelType = LandmarkModelType.DLIB_68,
                        ConfidenceThreshold = _configuration.LandmarkConfidenceThreshold;
                    });

                    // Yüz bilgilerini güncelle;
                    face.Landmarks = landmarks;
                    face.HasLandmarks = landmarks.Any();

                    landmarkedFaces.Add(face);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to detect landmarks for face at {face.BoundingBox}");
                    landmarkedFaces.Add(face);
                }
            }

            result.Faces = landmarkedFaces;
            return result;
        }

        /// <summary>
        /// Yüz hizalaması yap;
        /// </summary>
        private async Task<DetectionResult> AlignFacesAsync(DetectionResult result, Mat image)
        {
            var alignedFaces = new List<DetectedFace>();

            foreach (var face in result.Faces.Where(f => f.HasLandmarks))
            {
                try
                {
                    // Yüz görüntüsünü kırp;
                    using var faceMat = new Mat(image, face.BoundingBox);

                    // Yüz hizalaması yap;
                    var alignedImage = await _alignmentEngine.AlignAsync(faceMat, face.Landmarks, new AlignmentOptions;
                    {
                        TargetSize = _configuration.AlignmentTargetSize,
                        EyePosition = _configuration.EyePosition,
                        EnableCrop = true;
                    });

                    // Yüz bilgilerini güncelle;
                    face.AlignedImage = alignedImage.AlignedImage?.ToBytes(".jpg");
                    face.AlignmentScore = alignedImage.AlignmentScore;
                    face.IsAligned = alignedImage.AlignmentScore >= _configuration.MinAlignmentScore;

                    alignedFaces.Add(face);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to align face at {face.BoundingBox}");
                    alignedFaces.Add(face);
                }
            }

            result.Faces = alignedFaces;
            return result;
        }

        /// <summary>
        /// Özellik çıkarımı yap;
        /// </summary>
        private async Task<DetectionResult> ExtractFeaturesAsync(DetectionResult result, Mat image)
        {
            var featuredFaces = new List<DetectedFace>();

            foreach (var face in result.Faces)
            {
                try
                {
                    // Yüz görüntüsünü kırp (hizalanmışsa onu kullan)
                    Mat faceMat;
                    if (face.IsAligned && face.AlignedImage != null)
                    {
                        faceMat = await LoadImageAsync(face.AlignedImage);
                    }
                    else;
                    {
                        faceMat = new Mat(image, face.BoundingBox);
                    }

                    using (faceMat)
                    {
                        // Özellik çıkarımı yap;
                        var features = await _featureExtractor.ExtractAsync(faceMat, new ExtractionOptions;
                        {
                            ModelType = FeatureModelType.Facenet,
                            NormalizeFeatures = true,
                            DimensionalityReduction = _configuration.FeatureDimensionality;
                        });

                        // Yüz bilgilerini güncelle;
                        face.Features = features;
                        face.FeatureVector = features.ToFeatureVector();
                        face.HasFeatures = features.FeatureCount > 0;
                    }

                    featuredFaces.Add(face);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to extract features for face at {face.BoundingBox}");
                    featuredFaces.Add(face);
                }
            }

            result.Faces = featuredFaces;
            return result;
        }

        /// <summary>
        /// Video yakalamayı başlat;
        /// </summary>
        private async Task StartVideoCaptureAsync(DetectionSession session, RealTimeSource? source = null)
        {
            try
            {
                if (_videoCapture != null && _videoCapture.IsOpened())
                {
                    _videoCapture.Release();
                }

                if (source.HasValue)
                {
                    // Gerçek zamanlı kaynak;
                    switch (source.Value)
                    {
                        case RealTimeSource.Webcam:
                            _videoCapture = new VideoCapture(0);
                            break;
                        case RealTimeSource.IPCamera:
                            _videoCapture = new VideoCapture(session.Context.Source);
                            break;
                        case RealTimeSource.RTSP:
                            _videoCapture = new VideoCapture(session.Context.Source);
                            break;
                    }
                }
                else if (session.SessionType == SessionType.VideoStream)
                {
                    // Video dosyası;
                    _videoCapture = new VideoCapture(session.Context.VideoPath);
                }
                else if (session.SessionType == SessionType.Webcam)
                {
                    // Webcam;
                    _videoCapture = new VideoCapture(0);
                }

                if (_videoCapture == null || !_videoCapture.IsOpened())
                {
                    throw new FaceDetectionException("Failed to open video source");
                }

                // Video özelliklerini ayarla;
                if (_configuration.VideoConfig != null)
                {
                    _videoCapture.Set(VideoCaptureProperties.FrameWidth, _configuration.VideoConfig.FrameWidth);
                    _videoCapture.Set(VideoCaptureProperties.FrameHeight, _configuration.VideoConfig.FrameHeight);
                    _videoCapture.Set(VideoCaptureProperties.Fps, _configuration.VideoConfig.FPS);
                }

                _currentFrame = new Mat();

                _logger.LogDebug($"Video capture started: {session.SessionType}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start video capture");
                throw new FaceDetectionException("Failed to start video capture", ex);
            }
        }

        /// <summary>
        /// Video yakalamayı durdur;
        /// </summary>
        private void StopVideoCapture()
        {
            try
            {
                if (_videoCapture != null)
                {
                    if (_videoCapture.IsOpened())
                    {
                        _videoCapture.Release();
                    }
                    _videoCapture.Dispose();
                    _videoCapture = null;
                }

                if (_currentFrame != null)
                {
                    _currentFrame.Dispose();
                    _currentFrame = null;
                }

                _logger.LogDebug("Video capture stopped");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping video capture");
            }
        }

        /// <summary>
        /// Gerçek zamanlı frame işle;
        /// </summary>
        private async Task ProcessRealTimeFrameAsync(DetectionSession session, RealTimeOptions options)
        {
            try
            {
                if (_videoCapture == null || !_videoCapture.IsOpened())
                    return;

                // Frame oku;
                if (!_videoCapture.Read(_currentFrame) || _currentFrame.Empty())
                    return;

                // Frame'i işle;
                var frameData = _currentFrame.ToBytes(".jpg");
                var detectionResult = await DetectFacesAsync(frameData, session.SessionId, options.DetectionOptions);

                // Yüz takibi;
                if (options.EnableTracking && detectionResult.Faces.Any())
                {
                    await TrackFacesAsync(detectionResult, session.TotalFrames);
                }

                // Real-time analiz event'i;
                OnRealTimeAnalysisCompleted(detectionResult);

                // FPS hesapla;
                CalculateFPS();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process real-time frame");
                _detectionErrors++;
            }
        }

        /// <summary>
        /// Yüz takibi yap;
        /// </summary>
        private async Task TrackFacesAsync(DetectionResult detectionResult, int frameNumber)
        {
            try
            {
                var trackedFaces = await _faceTracker.TrackAsync(detectionResult, frameNumber);

                // Takip verilerini güncelle;
                foreach (var trackedFace in trackedFaces)
                {
                    _faceTracking[trackedFace.TrackId] = trackedFace;

                    // Event tetikle;
                    OnFaceTracked(trackedFace);
                }

                // Eski takip verilerini temizle;
                CleanupOldTracks(frameNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to track faces");
            }
        }

        /// <summary>
        /// Eski takip verilerini temizle;
        /// </summary>
        private void CleanupOldTracks(int currentFrame)
        {
            var maxAge = _configuration.TrackingMaxAge;
            var tracksToRemove = _faceTracking;
                .Where(kvp => currentFrame - kvp.Value.LastSeenFrame > maxAge)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var trackId in tracksToRemove)
            {
                _faceTracking.TryRemove(trackId, out _);
            }
        }

        /// <summary>
        /// İstatistikleri güncelle;
        /// </summary>
        private void UpdateStatistics(DetectionResult result)
        {
            Interlocked.Increment(ref _totalFrames);
            Interlocked.Add(ref _totalDetections, result.Faces.Count);
            _lastDetectionTime = DateTime.UtcNow;

            if (!result.Faces.Any())
            {
                Interlocked.Increment(ref _detectionErrors);
            }
        }

        /// <summary>
        /// Performans metriklerini güncelle;
        /// </summary>
        private void UpdatePerformanceMetrics(long detectionTimeMs, int faceCount)
        {
            var metric = new PerformanceMetric;
            {
                Timestamp = DateTime.UtcNow,
                DetectionTimeMs = detectionTimeMs,
                FaceCount = faceCount,
                MemoryUsageMB = GetMemoryUsageMB(),
                FrameNumber = _totalFrames;
            };

            _performanceMetrics.Add(metric);

            // Eski metrikleri temizle;
            if (_performanceMetrics.Count > 1000)
            {
                _performanceMetrics.RemoveAt(0);
            }

            // Ortalama tespit süresini güncelle;
            UpdateAverageDetectionTime(detectionTimeMs);

            // Event tetikle;
            OnPerformanceMetricsUpdated();
        }

        /// <summary>
        /// Ortalama tespit süresini güncelle;
        /// </summary>
        private void UpdateAverageDetectionTime(long detectionTimeMs)
        {
            if (AverageDetectionTimeMs == 0)
            {
                AverageDetectionTimeMs = detectionTimeMs;
            }
            else;
            {
                // Exponential moving average;
                AverageDetectionTimeMs = (AverageDetectionTimeMs * 0.9) + (detectionTimeMs * 0.1);
            }
        }

        /// <summary>
        /// FPS hesapla;
        /// </summary>
        private void CalculateFPS()
        {
            // Son 30 frame'in süresini kullan;
            var recentMetrics = _performanceMetrics;
                .Where(m => (DateTime.UtcNow - m.Timestamp).TotalSeconds < 5)
                .ToList();

            if (recentMetrics.Count >= 2)
            {
                var totalTime = recentMetrics.Sum(m => m.DetectionTimeMs);
                CurrentFPS = recentMetrics.Count / (totalTime / 1000.0);
            }
        }

        /// <summary>
        /// Bellek kullanımını getir (MB)
        /// </summary>
        private double GetMemoryUsageMB()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                return process.WorkingSet64 / (1024.0 * 1024.0);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// GPU kullanımını getir (%)
        /// </summary>
        private double GetGPUUsagePercent()
        {
            // GPU kullanımı izleme - platforma özel implementasyon gerekir;
            return 0; // Varsayılan;
        }

        /// <summary>
        /// Performans izlemeyi başlat;
        /// </summary>
        private void StartPerformanceMonitoring()
        {
            _ = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested && _isInitialized)
                {
                    try
                    {
                        // Her 5 saniyede bir performans metrikleri güncelle;
                        await Task.Delay(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token);

                        // FPS hesapla;
                        CalculateFPS();

                        // Bellek kullanımını güncelle;
                        var memoryUsage = GetMemoryUsageMB();
                        var gpuUsage = GetGPUUsagePercent();

                        // Log (sadece debug modda)
                        if (_configuration.EnablePerformanceLogging)
                        {
                            _logger.LogDebug($"Performance: {CurrentFPS:F1} FPS, {memoryUsage:F1} MB, {AverageDetectionTimeMs:F1} ms");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Performance monitoring failed");
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Eğitim verisini hazırla;
        /// </summary>
        private async Task<List<ProcessedSample>> PrepareTrainingDataAsync(IEnumerable<TrainingSample> trainingData)
        {
            var processedSamples = new List<ProcessedSample>();

            foreach (var sample in trainingData)
            {
                try
                {
                    using var image = await LoadImageAsync(sample.ImageData);
                    using var processedImage = await _preprocessor.ProcessForTrainingAsync(image, sample.Annotations);

                    var processedSample = new ProcessedSample;
                    {
                        ImageData = processedImage.ToBytes(".jpg"),
                        Annotations = sample.Annotations,
                        Metadata = sample.Metadata;
                    };

                    processedSamples.Add(processedSample);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to process training sample");
                }
            }

            return processedSamples;
        }

        /// <summary>
        /// Session analizi yap;
        /// </summary>
        private async Task<SessionAnalysisResult> AnalyzeSessionAsync(DetectionSession session)
        {
            var analysisResult = new SessionAnalysisResult;
            {
                SessionId = session.SessionId,
                SessionType = session.SessionType,
                StartTime = session.StartTime,
                EndTime = session.EndTime.Value,
                Duration = session.EndTime.Value - session.StartTime,
                TotalFrames = session.TotalFrames,
                Configuration = session.Configuration;
            };

            // Tespit geçmişinden analiz yap;
            var sessionDetections = _detectionHistory;
                .Where(r => r.SessionId == session.SessionId)
                .ToList();

            if (sessionDetections.Any())
            {
                analysisResult.TotalFaces = sessionDetections.Sum(r => r.Faces.Count);
                analysisResult.AverageFacesPerFrame = (double)analysisResult.TotalFaces / session.TotalFrames;
                analysisResult.MaxFacesInFrame = sessionDetections.Max(r => r.Faces.Count);
                analysisResult.MinFacesInFrame = sessionDetections.Min(r => r.Faces.Count);

                // Kalite analizi;
                var qualityScores = sessionDetections;
                    .SelectMany(r => r.Faces)
                    .Where(f => f.QualityScore.HasValue)
                    .Select(f => f.QualityScore.Value)
                    .ToList();

                if (qualityScores.Any())
                {
                    analysisResult.AverageQualityScore = qualityScores.Average();
                    analysisResult.HighQualityFaces = qualityScores.Count(s => s >= _configuration.HighQualityThreshold);
                    analysisResult.LowQualityFaces = qualityScores.Count(s => s < _configuration.MinQualityScore);
                }

                // Algoritma istatistikleri;
                analysisResult.AlgorithmUsage = sessionDetections;
                    .GroupBy(r => r.AlgorithmUsed)
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            return analysisResult;
        }

        /// <summary>
        /// Konfigürasyonu güncelle;
        /// </summary>
        private void UpdateConfiguration()
        {
            if (_detectionEngine != null && _isInitialized)
            {
                _detectionEngine.UpdateConfiguration(_configuration);
                _preprocessor.Configure(_configuration.PreprocessingConfig);

                foreach (var algorithm in _algorithms)
                {
                    algorithm.Configure(_configuration);
                }

                _inferenceOptimizer.UpdateConfiguration(_configuration.InferenceConfig);
                _hardwareAccelerator.UpdateConfiguration(_configuration.HardwareConfig);
                _memoryPool.UpdateConfiguration(_configuration.MemoryConfig);
            }
        }

        /// <summary>
        /// Başlatıldığını doğrula;
        /// </summary>
        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Face detector is not initialized. Call InitializeAsync first.");
        }

        #endregion;

        #region Event Triggers;

        private void OnFaceDetected(DetectionResult result)
        {
            FaceDetected?.Invoke(this, new FaceDetectedEventArgs(result));
            _eventBus.Publish(new FaceDetectedEvent(result));
        }

        private void OnFaceTracked(TrackingData trackingData)
        {
            FaceTracked?.Invoke(this, new FaceTrackedEventArgs(trackingData));
            _eventBus.Publish(new FaceTrackedEvent(trackingData));
        }

        private void OnLandmarksDetected(LandmarkDetectionResult result)
        {
            LandmarksDetected?.Invoke(this, new LandmarksDetectedEventArgs(result));
            _eventBus.Publish(new LandmarksDetectedEvent(result));
        }

        private void OnFeaturesExtracted(FeatureExtractionResult result)
        {
            FeaturesExtracted?.Invoke(this, new FeaturesExtractedEventArgs(result));
            _eventBus.Publish(new FeaturesExtractedEvent(result));
        }

        private void OnQualityAnalyzed(QualityAnalysisResult result)
        {
            QualityAnalyzed?.Invoke(this, new QualityAnalyzedEventArgs(result));
            _eventBus.Publish(new QualityAnalyzedEvent(result));
        }

        private void OnStateChanged()
        {
            StateChanged?.Invoke(this, new DetectionStateChangedEventArgs(State));
            _eventBus.Publish(new DetectionStateChangedEvent(State));
        }

        private void OnRealTimeAnalysisCompleted(DetectionResult result)
        {
            RealTimeAnalysisCompleted?.Invoke(this, new RealTimeAnalysisCompletedEventArgs(result));
        }

        private void OnPerformanceMetricsUpdated()
        {
            PerformanceMetricsUpdated?.Invoke(this, new PerformanceMetricsUpdatedEventArgs(GetPerformanceReport()));
        }

        private void OnPropertyChanged(string propertyName)
        {
            // INotifyPropertyChanged implementasyonu için;
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Managed kaynakları temizle;
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();

                    _detectionEngine?.Dispose();
                    _realTimeProcessor?.Dispose();
                    _faceTracker?.Dispose();
                    _memoryPool?.Dispose();
                    _hardwareAccelerator?.Dispose();

                    // Algoritmaları temizle;
                    foreach (var algorithm in _algorithms)
                    {
                        if (algorithm is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    // Video yakalamayı durdur;
                    StopVideoCapture();

                    // Event subscription'ları temizle;
                    FaceDetected = null;
                    FaceTracked = null;
                    LandmarksDetected = null;
                    FeaturesExtracted = null;
                    QualityAnalyzed = null;
                    StateChanged = null;
                    RealTimeAnalysisCompleted = null;
                    PerformanceMetricsUpdated = null;
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;

        #region Supporting Classes and Enums;

        /// <summary>
        /// Tespit durumları;
        /// </summary>
        public enum DetectionState;
        {
            Stopped,
            Initializing,
            Ready,
            Training,
            Processing,
            Error;
        }

        /// <summary>
        /// Tespit modları;
        /// </summary>
        public enum DetectionMode;
        {
            CPU,
            GPU,
            NPU,
            Hybrid,
            Cloud;
        }

        /// <summary>
        /// Model tipleri;
        /// </summary>
        public enum ModelType;
        {
            HaarCascade,
            LBP,
            DLibHOG,
            MTCNN,
            RetinaFace,
            YOLOFace,
            CenterFace,
            MobileFaceNet,
            BlazeFace,
            UltraLight,
            Custom;
        }

        /// <summary>
        /// Tespit algoritmaları;
        /// </summary>
        public enum DetectionAlgorithm;
        {
            HaarCascade,
            LBP,
            DLibHOG,
            MTCNN,
            RetinaFace,
            YOLOFace,
            CenterFace,
            MobileFaceNet,
            BlazeFace,
            UltraLight;
        }

        /// <summary>
        /// Session tipleri;
        /// </summary>
        public enum SessionType;
        {
            Image,
            VideoStream,
            Webcam,
            RealTime,
            Batch;
        }

        /// <summary>
        /// Session durumları;
        /// </summary>
        public enum SessionStatus;
        {
            Active,
            Paused,
            Completed,
            Error;
        }

        /// <summary>
        /// Session sonlandırma sebepleri;
        /// </summary>
        public enum SessionEndReason;
        {
            Completed,
            Stopped,
            Error,
            Timeout,
            SystemReset;
        }

        /// <summary>
        /// Gerçek zamanlı kaynaklar;
        /// </summary>
        public enum RealTimeSource;
        {
            Webcam,
            IPCamera,
            RTSP,
            ScreenCapture;
        }

        /// <summary>
        /// Landmark model tipleri;
        /// </summary>
        public enum LandmarkModelType;
        {
            DLIB_68,
            DLIB_5,
            MTCNN_5,
            FaceMesh,
            Custom;
        }

        /// <summary>
        /// Özellik model tipleri;
        /// </summary>
        public enum FeatureModelType;
        {
            Facenet,
            ArcFace,
            VGGFace,
            DeepFace,
            OpenFace,
            Custom;
        }

        /// <summary>
        /// Kalite metrikleri;
        /// </summary>
        public enum QualityMetric;
        {
            Blur,
            Lighting,
            Pose,
            Occlusion,
            Resolution,
            Contrast,
            Noise;
        }

        #endregion;
    }

    #region Data Classes;

    /// <summary>
    /// Tespit edilen yüz;
    /// </summary>
    public class DetectedFace;
    {
        public int Id { get; set; }
        public Rectangle BoundingBox { get; set; }
        public double Confidence { get; set; }
        public double? QualityScore { get; set; }
        public bool IsQualityAcceptable { get; set; }
        public FaceQuality QualityAnalysis { get; set; }
        public List<LandmarkPoint> Landmarks { get; set; } = new List<LandmarkPoint>();
        public bool HasLandmarks => Landmarks?.Any() == true;
        public byte[] AlignedImage { get; set; }
        public double? AlignmentScore { get; set; }
        public bool IsAligned { get; set; }
        public FaceFeatures Features { get; set; }
        public float[] FeatureVector { get; set; }
        public bool HasFeatures => Features != null;
        public int? TrackId { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Yüz kalitesi;
    /// </summary>
    public class FaceQuality;
    {
        public double BlurScore { get; set; }
        public double LightingScore { get; set; }
        public double PoseScore { get; set; }
        public double OcclusionScore { get; set; }
        public List<string> Recommendations { get; set; } = new List<string>();
    }

    /// <summary>
    /// Landmark noktası;
    /// </summary>
    public class LandmarkPoint;
    {
        public int Id { get; set; }
        public Point Position { get; set; }
        public double Confidence { get; set; }
        public string Type { get; set; } // Eye, Nose, Mouth, etc.
    }

    /// <summary>
    /// Yüz özellikleri;
    /// </summary>
    public class FaceFeatures;
    {
        public int FeatureCount => FeatureVector?.Length ?? 0;
        public float[] FeatureVector { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, float> FeatureMap { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Tespit sonucu;
    /// </summary>
    public class DetectionResult;
    {
        public DateTime Timestamp { get; set; }
        public Size ImageSize { get; set; }
        public List<DetectedFace> Faces { get; set; } = new List<DetectedFace>();
        public DetectionAlgorithm AlgorithmUsed { get; set; }
        public long DetectionTimeMs { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Batch tespit sonucu;
    /// </summary>
    public class BatchDetectionResult;
    {
        public DateTime Timestamp { get; set; }
        public int TotalImages { get; set; }
        public int SuccessfulDetections { get; set; }
        public int FailedDetections { get; set; }
        public int TotalFaces { get; set; }
        public double AverageFacesPerImage { get; set; }
        public List<DetectionResult> Results { get; set; } = new List<DetectionResult>();
        public List<FailedImage> FailedImages { get; set; } = new List<FailedImage>();
    }

    /// <summary>
    /// Başarısız görüntü;
    /// </summary>
    public class FailedImage;
    {
        public int Index { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Video tespit sonucu;
    /// </summary>
    public class VideoDetectionResult;
    {
        public string VideoPath { get; set; }
        public string SessionId { get; set; }
        public DateTime Timestamp { get; set; }
        public int TotalFrames { get; set; }
        public int ProcessedFrames { get; set; }
        public int TotalFaces { get; set; }
        public List<FrameDetectionResult> FrameResults { get; set; } = new List<FrameDetectionResult>();
        public VideoInfo VideoInfo { get; set; }
    }

    /// <summary>
    /// Frame tespit sonucu;
    /// </summary>
    public class FrameDetectionResult;
    {
        public int FrameNumber { get; set; }
        public TimeSpan Timestamp { get; set; }
        public DetectionResult DetectionResult { get; set; }
        public Size FrameSize { get; set; }
    }

    /// <summary>
    /// Video bilgileri;
    /// </summary>
    public class VideoInfo;
    {
        public TimeSpan Duration { get; set; }
        public double FrameRate { get; set; }
        public Size Resolution { get; set; }
        public int TotalFrames { get; set; }
        public string Codec { get; set; }
    }

    /// <summary>
    /// Takip verileri;
    /// </summary>
    public class TrackingData;
    {
        public int TrackId { get; set; }
        public Rectangle CurrentBoundingBox { get; set; }
        public Rectangle[] Trajectory { get; set; }
        public int FirstSeenFrame { get; set; }
        public int LastSeenFrame { get; set; }
        public int Age => LastSeenFrame - FirstSeenFrame;
        public int HitCount { get; set; }
        public int MissCount { get; set; }
        public double Confidence { get; set; }
        public DetectedFace CurrentFace { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Landmark tespit sonucu;
    /// </summary>
    public class LandmarkDetectionResult;
    {
        public Rectangle FaceRectangle { get; set; }
        public List<LandmarkPoint> Landmarks { get; set; } = new List<LandmarkPoint>();
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Özellik çıkarım sonucu;
    /// </summary>
    public class FeatureExtractionResult;
    {
        public Rectangle FaceRectangle { get; set; }
        public FaceFeatures Features { get; set; }
        public float[] FeatureVector { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kalite analiz sonucu;
    /// </summary>
    public class QualityAnalysisResult;
    {
        public Rectangle FaceRectangle { get; set; }
        public double QualityScore { get; set; }
        public double BlurScore { get; set; }
        public double LightingScore { get; set; }
        public double PoseScore { get; set; }
        public double OcclusionScore { get; set; }
        public bool IsAcceptable { get; set; }
        public List<string> Recommendations { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hizalama sonucu;
    /// </summary>
    public class AlignmentResult;
    {
        public byte[] OriginalImage { get; set; }
        public byte[] AlignedImage { get; set; }
        public float[,] TransformationMatrix { get; set; }
        public double AlignmentScore { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tespit seçenekleri;
    /// </summary>
    public class DetectionOptions;
    {
        public DetectionAlgorithm? Algorithm { get; set; }
        public double ConfidenceThreshold { get; set; } = 0.5;
        public bool EnableNMS { get; set; } = true;
        public double NMSThreshold { get; set; } = 0.3;
        public bool AnalyzeQuality { get; set; } = true;
        public bool DetectLandmarks { get; set; } = true;
        public bool AlignFaces { get; set; } = true;
        public bool ExtractFeatures { get; set; } = false;
        public PreprocessingOptions Preprocessing { get; set; } = new PreprocessingOptions();
        public PostprocessingOptions Postprocessing { get; set; } = new PostprocessingOptions();

        public static DetectionOptions Default => new DetectionOptions();
    }

    /// <summary>
    /// Video tespit seçenekleri;
    /// </summary>
    public class VideoDetectionOptions;
    {
        public int FrameInterval { get; set; } = 1;
        public bool EnableTracking { get; set; } = true;
        public DetectionOptions DetectionOptions { get; set; } = DetectionOptions.Default;

        public static VideoDetectionOptions Default => new VideoDetectionOptions();
    }

    /// <summary>
    /// Gerçek zamanlı seçenekler;
    /// </summary>
    public class RealTimeOptions;
    {
        public double TargetFPS { get; set; } = 30;
        public bool EnableTracking { get; set; } = true;
        public DetectionOptions DetectionOptions { get; set; } = DetectionOptions.Default;

        public static RealTimeOptions Default => new RealTimeOptions();
    }

    /// <summary>
    /// Takip seçenekleri;
    /// </summary>
    public class TrackingOptions;
    {
        public int MaxAge { get; set; } = 30;
        public int MinHits { get; set; } = 3;
        public double IOUThreshold { get; set; } = 0.3;
        public string TrackerType { get; set; } = "KCF";

        public static TrackingOptions Default => new TrackingOptions();
    }

    /// <summary>
    /// Landmark seçenekleri;
    /// </summary>
    public class LandmarkOptions;
    {
        public LandmarkModelType ModelType { get; set; } = LandmarkModelType.DLIB_68;
        public double ConfidenceThreshold { get; set; } = 0.5;
        public bool EnableRefinement { get; set; } = true;

        public static LandmarkOptions Default => new LandmarkOptions();
    }

    /// <summary>
    /// Özellik çıkarım seçenekleri;
    /// </summary>
    public class ExtractionOptions;
    {
        public FeatureModelType ModelType { get; set; } = FeatureModelType.Facenet;
        public bool NormalizeFeatures { get; set; } = true;
        public int? DimensionalityReduction { get; set; }

        public static ExtractionOptions Default => new ExtractionOptions();
    }

    /// <summary>
    /// Kalite seçenekleri;
    /// </summary>
    public class QualityOptions;
    {
        public bool AnalyzeBlur { get; set; } = true;
        public bool AnalyzeLighting { get; set; } = true;
        public bool AnalyzePose { get; set; } = true;
        public bool AnalyzeOcclusion { get; set; } = true;
        public double MinAcceptableScore { get; set; } = 0.5;

        public static QualityOptions Default => new QualityOptions();
    }

    /// <summary>
    /// Hizalama seçenekleri;
    /// </summary>
    public class AlignmentOptions;
    {
        public Size TargetSize { get; set; } = new Size(112, 112);
        public PointF EyePosition { get; set; } = new PointF(0.35f, 0.35f);
        public bool EnableCrop { get; set; } = true;

        public static AlignmentOptions Default => new AlignmentOptions();
    }

    /// <summary>
    /// Ön işleme seçenekleri;
    /// </summary>
    public class PreprocessingOptions;
    {
        public bool ConvertToGray { get; set; } = false;
        public bool Normalize { get; set; } = true;
        public bool EqualizeHistogram { get; set; } = true;
        public Size ResizeTo { get; set; } = Size.Empty;
        public bool ApplyCLAHE { get; set; } = true;

        public static PreprocessingOptions Default => new PreprocessingOptions();
    }

    /// <summary>
    /// Son işleme seçenekleri;
    /// </summary>
    public class PostprocessingOptions;
    {
        public bool FilterBySize { get; set; } = true;
        public int MinFaceSize { get; set; } = 20;
        public int MaxFaceSize { get; set; } = 1000;
        public bool FilterByAspectRatio { get; set; } = true;
        public double MinAspectRatio { get; set; } = 0.5;
        public double MaxAspectRatio { get; set; } = 2.0;

        public static PostprocessingOptions Default => new PostprocessingOptions();
    }

    /// <summary>
    /// Tespit konfigürasyonu;
    /// </summary>
    public class DetectionConfiguration;
    {
        public DetectionMode DetectionMode { get; set; } = DetectionMode.CPU;
        public ModelType ModelType { get; set; } = ModelType.RetinaFace;
        public DetectionAlgorithm DefaultAlgorithm { get; set; } = DetectionAlgorithm.RetinaFace;
        public Size InputSize { get; set; } = new Size(640, 480);
        public double ConfidenceThreshold { get; set; } = 0.7;
        public double MinQualityScore { get; set; } = 0.5;
        public double HighQualityThreshold { get; set; } = 0.8;
        public double LandmarkConfidenceThreshold { get; set; } = 0.5;
        public double MinAlignmentScore { get; set; } = 0.6;
        public int FeatureDimensionality { get; set; } = 512;
        public Size AlignmentTargetSize { get; set; } = new Size(112, 112);
        public PointF EyePosition { get; set; } = new PointF(0.35f, 0.35f);
        public int TrackingMaxAge { get; set; } = 30;
        public bool EnableFallback { get; set; } = true;
        public bool EnablePerformanceLogging { get; set; } = false;
        public PreprocessingConfig PreprocessingConfig { get; set; } = new PreprocessingConfig();
        public InferenceConfig InferenceConfig { get; set; } = new InferenceConfig();
        public HardwareConfig HardwareConfig { get; set; } = new HardwareConfig();
        public MemoryConfig MemoryConfig { get; set; } = new MemoryConfig();
        public VideoConfig VideoConfig { get; set; } = new VideoConfig();

        public static DetectionConfiguration Default => new DetectionConfiguration();

        public Dictionary<string, object> GetModelParameters()
        {
            return new Dictionary<string, object>
            {
                ["DetectionMode"] = DetectionMode.ToString(),
                ["ModelType"] = ModelType.ToString(),
                ["InputSize"] = $"{InputSize.Width}x{InputSize.Height}",
                ["ConfidenceThreshold"] = ConfidenceThreshold,
                ["MinQualityScore"] = MinQualityScore;
            };
        }
    }

    /// <summary>
    /// Ön işleme konfigürasyonu;
    /// </summary>
    public class PreprocessingConfig;
    {
        public bool EnableHistogramEqualization { get; set; } = true;
        public bool EnableContrastEnhancement { get; set; } = true;
        public bool EnableNoiseReduction { get; set; } = true;
        public double GammaCorrection { get; set; } = 1.0;
        public int CLAHEClipLimit { get; set; } = 2;

        public static PreprocessingConfig Default => new PreprocessingConfig();
    }

    /// <summary>
    /// Çıkarım konfigürasyonu;
    /// </summary>
    public class InferenceConfig;
    {
        public int BatchSize { get; set; } = 1;
        public bool UseFP16 { get; set; } = false;
        public bool UseINT8 { get; set; } = false;
        public int ThreadCount { get; set; } = 4;
        public string ExecutionProvider { get; set; } = "CPU";

        public static InferenceConfig Default => new InferenceConfig();
    }

    /// <summary>
    /// Donanım konfigürasyonu;
    /// </summary>
    public class HardwareConfig;
    {
        public bool UseGPU { get; set; } = true;
        public int GPUDeviceId { get; set; } = 0;
        public bool UseTensorRT { get; set; } = false;
        public int TensorRTCacheSize { get; set; } = 1024;
        public bool UseOpenVINO { get; set; } = false;

        public static HardwareConfig Default => new HardwareConfig();
    }

    /// <summary>
    /// Bellek konfigürasyonu;
    /// </summary>
    public class MemoryConfig;
    {
        public int MaxPoolSizeMB { get; set; } = 512;
        public int BufferSize { get; set; } = 10;
        public bool EnableMemoryMapping { get; set; } = true;

        public static MemoryConfig Default => new MemoryConfig();
    }

    /// <summary>
    /// Video konfigürasyonu;
    /// </summary>
    public class VideoConfig;
    {
        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 720;
        public int FPS { get; set; } = 30;
        public string Codec { get; set; } = "MJPG";

        public static VideoConfig Default => new VideoConfig();
    }

    /// <summary>
    /// Tespit session'ı;
    /// </summary>
    public class DetectionSession;
    {
        public string SessionId { get; }
        public SessionType SessionType { get; }
        public SessionContext Context { get; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;
        public SessionStatus Status { get; set; }
        public SessionEndReason? EndReason { get; set; }
        public DateTime LastActivity { get; set; }
        public int TotalFrames { get; set; }
        public bool IsTrackingEnabled { get; set; }
        public DetectionConfiguration Configuration { get; set; }
        public SessionAnalysisResult AnalysisResult { get; set; }

        public DetectionSession(string sessionId, SessionType sessionType, SessionContext context)
        {
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            SessionType = sessionType;
            Context = context ?? throw new ArgumentNullException(nameof(context));
        }
    }

    /// <summary>
    /// Session context'i;
    /// </summary>
    public class SessionContext;
    {
        public string VideoPath { get; set; }
        public string Source { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public static SessionContext Default => new SessionContext();
    }

    /// <summary>
    /// Session analiz sonucu;
    /// </summary>
    public class SessionAnalysisResult;
    {
        public string SessionId { get; set; }
        public SessionType SessionType { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public int TotalFrames { get; set; }
        public int TotalFaces { get; set; }
        public double AverageFacesPerFrame { get; set; }
        public int MaxFacesInFrame { get; set; }
        public int MinFacesInFrame { get; set; }
        public double AverageQualityScore { get; set; }
        public int HighQualityFaces { get; set; }
        public int LowQualityFaces { get; set; }
        public Dictionary<DetectionAlgorithm, int> AlgorithmUsage { get; set; } = new Dictionary<DetectionAlgorithm, int>();
        public DetectionConfiguration Configuration { get; set; }
    }

    /// <summary>
    /// Performans metrikleri;
    /// </summary>
    public class PerformanceMetric;
    {
        public DateTime Timestamp { get; set; }
        public long DetectionTimeMs { get; set; }
        public int FaceCount { get; set; }
        public double MemoryUsageMB { get; set; }
        public int FrameNumber { get; set; }
    }

    /// <summary>
    /// Performans raporu;
    /// </summary>
    public class PerformanceReport;
    {
        public DateTime Timestamp { get; set; }
        public int TotalFrames { get; set; }
        public int TotalDetections { get; set; }
        public double CurrentFPS { get; set; }
        public double AverageDetectionTimeMs { get; set; }
        public double DetectionErrorRate { get; set; }
        public int TrackedFacesCount { get; set; }
        public double MemoryUsageMB { get; set; }
        public double GPUUsagePercent { get; set; }
        public List<PerformanceMetric> PerformanceMetrics { get; set; } = new List<PerformanceMetric>();
    }

    /// <summary>
    /// Tespit isteği;
    /// </summary>
    public class DetectionRequest;
    {
        public byte[] ImageData { get; set; }
        public DetectionOptions Options { get; set; }
        public string SessionId { get; set; }
        public Guid RequestId { get; set; } = Guid.NewGuid();
        public DateTime RequestTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Eğitim örneği;
    /// </summary>
    public class TrainingSample;
    {
        public byte[] ImageData { get; set; }
        public List<Rectangle> Annotations { get; set; } = new List<Rectangle>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// İşlenmiş örnek;
    /// </summary>
    public class ProcessedSample;
    {
        public byte[] ImageData { get; set; }
        public List<Rectangle> Annotations { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Eğitim konfigürasyonu;
    /// </summary>
    public class TrainingConfiguration;
    {
        public int Epochs { get; set; } = 100;
        public int BatchSize { get; set; } = 32;
        public double LearningRate { get; set; } = 0.001;
        public double ValidationSplit { get; set; } = 0.2;
        public string Optimizer { get; set; } = "Adam";

        public static TrainingConfiguration Default => new TrainingConfiguration();
    }

    /// <summary>
    /// Eğitim sonucu;
    /// </summary>
    public class TrainingResult;
    {
        public FaceModel TrainedModel { get; set; }
        public double Accuracy { get; set; }
        public double Loss { get; set; }
        public int TrainingTimeSeconds { get; set; }
        public Dictionary<string, object> TrainingMetrics { get; set; } = new Dictionary<string, object>();
    }

    #endregion;

    #region Event Args Classes;

    public class FaceDetectedEventArgs : EventArgs;
    {
        public DetectionResult Result { get; }

        public FaceDetectedEventArgs(DetectionResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }
    }

    public class FaceTrackedEventArgs : EventArgs;
    {
        public TrackingData TrackingData { get; }

        public FaceTrackedEventArgs(TrackingData trackingData)
        {
            TrackingData = trackingData ?? throw new ArgumentNullException(nameof(trackingData));
        }
    }

    public class LandmarksDetectedEventArgs : EventArgs;
    {
        public LandmarkDetectionResult Result { get; }

        public LandmarksDetectedEventArgs(LandmarkDetectionResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }
    }

    public class FeaturesExtractedEventArgs : EventArgs;
    {
        public FeatureExtractionResult Result { get; }

        public FeaturesExtractedEventArgs(FeatureExtractionResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }
    }

    public class QualityAnalyzedEventArgs : EventArgs;
    {
        public QualityAnalysisResult Result { get; }

        public QualityAnalyzedEventArgs(QualityAnalysisResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }
    }

    public class DetectionStateChangedEventArgs : EventArgs;
    {
        public DetectionState State { get; }

        public DetectionStateChangedEventArgs(DetectionState state)
        {
            State = state;
        }
    }

    public class RealTimeAnalysisCompletedEventArgs : EventArgs;
    {
        public DetectionResult Result { get; }

        public RealTimeAnalysisCompletedEventArgs(DetectionResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }
    }

    public class PerformanceMetricsUpdatedEventArgs : EventArgs;
    {
        public PerformanceReport Report { get; }

        public PerformanceMetricsUpdatedEventArgs(PerformanceReport report)
        {
            Report = report ?? throw new ArgumentNullException(nameof(report));
        }
    }

    #endregion;
}
