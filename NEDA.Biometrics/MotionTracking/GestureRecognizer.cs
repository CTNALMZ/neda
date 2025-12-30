using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Statistics;
using Microsoft.ML;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NEDA.Animation.SequenceEditor.CinematicDirector;
using NEDA.API.Middleware;
using NEDA.Biometrics.FaceRecognition;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.Common;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.Services.Messaging.EventBus;
using Newtonsoft.Json;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.MotionTracking;
{
    /// <summary>
    /// Jest Tanıma Sistemi - El ve vücut hareketlerini tanır ve yorumlar;
    /// Özellikler: Real-time gesture recognition, multi-modal input, machine learning, pattern matching;
    /// Tasarım desenleri: Strategy, Chain of Responsibility, Observer, Template Method, Factory;
    /// </summary>
    public class GestureRecognizer : IGestureRecognizer, IDisposable;
    {
        #region Fields;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IErrorReporter _errorReporter;
        private readonly IPerformanceMonitor _performanceMonitor;

        private readonly ConcurrentDictionary<string, GestureSession> _activeSessions;
        private readonly ConcurrentDictionary<string, GesturePattern> _gesturePatterns;
        private readonly ConcurrentDictionary<string, UserGestureProfile> _userProfiles;
        private readonly ConcurrentQueue<GestureFrame> _frameQueue;
        private readonly ConcurrentBag<GestureRecognitionResult> _recognitionResults;

        private readonly List<IGestureDetector> _detectors;
        private readonly GestureClassifier _gestureClassifier;
        private readonly GestureAnalyzer _gestureAnalyzer;
        private readonly PatternMatcher _patternMatcher;
        private readonly SequenceRecognizer _sequenceRecognizer;
        private readonly ConfidenceCalculator _confidenceCalculator;
        private readonly GestureSmoother _gestureSmoother;
        private readonly GestureValidator _gestureValidator;

        private readonly Subject<GestureEvent> _gestureSubject;
        private readonly Subject<GestureRecognitionResult> _recognitionSubject;
        private readonly Subject<GestureTrainingEvent> _trainingSubject;

        private readonly InferenceSession _inferenceSession;
        private readonly Net _dnnNet;
        private readonly MLContext _mlContext;

        private readonly Timer _processingTimer;
        private readonly Timer _cleanupTimer;
        private readonly Timer _modelUpdateTimer;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly ManualResetEventSlim _processingEvent;
        private readonly object _syncLock = new object();

        private GestureRecognizerConfiguration _configuration;
        private GestureRecognizerState _state;
        private bool _isInitialized;
        private bool _isProcessing;
        private DateTime _startTime;

        private long _totalFramesProcessed;
        private long _totalGesturesRecognized;
        private long _totalHandsDetected;
        private long _totalBodiesDetected;

        private readonly Stopwatch _performanceStopwatch;
        private readonly ConcurrentBag<GesturePerformanceMetric> _performanceMetrics;
        private readonly GestureCache _gestureCache;
        private readonly ModelManager _modelManager;

        #endregion;

        #region Properties;

        /// <summary>
        /// Jest tanıyıcı durumu;
        /// </summary>
        public GestureRecognizerState State;
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
        /// Konfigürasyon;
        /// </summary>
        public GestureRecognizerConfiguration Configuration;
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
        /// Aktif session sayısı;
        /// </summary>
        public int ActiveSessionCount => _activeSessions.Count;

        /// <summary>
        /// Kayıtlı gesture pattern sayısı;
        /// </summary>
        public int PatternCount => _gesturePatterns.Count;

        /// <summary>
        /// İşlenen toplam frame sayısı;
        /// </summary>
        public long TotalFramesProcessed => _totalFramesProcessed;

        /// <summary>
        /// Tanınan toplam gesture sayısı;
        /// </summary>
        public long TotalGesturesRecognized => _totalGesturesRecognized;

        /// <summary>
        /// Tespit edilen toplam el sayısı;
        /// </summary>
        public long TotalHandsDetected => _totalHandsDetected;

        /// <summary>
        /// Tespit edilen toplam vücut sayısı;
        /// </summary>
        public long TotalBodiesDetected => _totalBodiesDetected;

        /// <summary>
        /// Çalışma süresi;
        /// </summary>
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;

        /// <summary>
        /// Frame kuyruğu boyutu;
        /// </summary>
        public int QueueSize => _frameQueue.Count;

        /// <summary>
        /// Model yüklü mü?
        /// </summary>
        public bool IsModelLoaded => _inferenceSession != null && _dnnNet != null;

        /// <summary>
        /// Model bilgisi;
        /// </summary>
        public ModelInfo ModelInfo { get; private set; }

        /// <summary>
        /// Frame işleme hızı (FPS)
        /// </summary>
        public double FramesPerSecond { get; private set; }

        /// <summary>
        /// Ortalama işleme süresi (ms)
        /// </summary>
        public double AverageProcessingTimeMs { get; private set; }

        /// <summary>
        /// Bellek kullanımı (MB)
        /// </summary>
        public double MemoryUsageMB => GetMemoryUsageMB();

        /// <summary>
        /// GPU kullanımı;
        /// </summary>
        public bool IsUsingGPU { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Gesture tespit edildi event'i;
        /// </summary>
        public event EventHandler<GestureDetectedEventArgs> GestureDetected;

        /// <summary>
        /// Gesture tanındı event'i;
        /// </summary>
        public event EventHandler<GestureRecognizedEventArgs> GestureRecognized;

        /// <summary>
        /// El tespit edildi event'i;
        /// </summary>
        public event EventHandler<HandDetectedEventArgs> HandDetected;

        /// <summary>
        /// Vücut tespit edildi event'i;
        /// </summary>
        public event EventHandler<BodyDetectedEventArgs> BodyDetected;

        /// <summary>
        /// Gesture sequence tamamlandı event'i;
        /// </summary>
        public event EventHandler<GestureSequenceCompletedEventArgs> GestureSequenceCompleted;

        /// <summary>
        /// Session başladı event'i;
        /// </summary>
        public event EventHandler<GestureSessionStartedEventArgs> SessionStarted;

        /// <summary>
        /// Session sonlandı event'i;
        /// </summary>
        public event EventHandler<GestureSessionEndedEventArgs> SessionEnded;

        /// <summary>
        /// Model eğitildi event'i;
        /// </summary>
        public event EventHandler<GestureModelTrainedEventArgs> ModelTrained;

        /// <summary>
        /// Durum değişti event'i;
        /// </summary>
        public event EventHandler<GestureRecognizerStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Performans metrikleri güncellendi event'i;
        /// </summary>
        public event EventHandler<GesturePerformanceMetricsUpdatedEventArgs> PerformanceMetricsUpdated;

        #endregion;

        #region Constructor;

        /// <summary>
        /// GestureRecognizer constructor;
        /// </summary>
        public GestureRecognizer(
            ILogger logger,
            IEventBus eventBus,
            IErrorReporter errorReporter,
            IPerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            // Concurrent koleksiyonlar;
            _activeSessions = new ConcurrentDictionary<string, GestureSession>();
            _gesturePatterns = new ConcurrentDictionary<string, GesturePattern>();
            _userProfiles = new ConcurrentDictionary<string, UserGestureProfile>();
            _frameQueue = new ConcurrentQueue<GestureFrame>();
            _recognitionResults = new ConcurrentBag<GestureRecognitionResult>();

            // Reactive subjects;
            _gestureSubject = new Subject<GestureEvent>();
            _recognitionSubject = new Subject<GestureRecognitionResult>();
            _trainingSubject = new Subject<GestureTrainingEvent>();

            // ML Context;
            _mlContext = new MLContext(seed: 1);

            // Bileşenleri başlat;
            _gestureClassifier = new GestureClassifier(logger);
            _gestureAnalyzer = new GestureAnalyzer(logger);
            _patternMatcher = new PatternMatcher(logger);
            _sequenceRecognizer = new SequenceRecognizer(logger);
            _confidenceCalculator = new ConfidenceCalculator(logger);
            _gestureSmoother = new GestureSmoother(logger);
            _gestureValidator = new GestureValidator(logger);
            _gestureCache = new GestureCache(logger);
            _modelManager = new ModelManager(logger);

            // Detector'ları başlat;
            _detectors = InitializeDetectors();

            // Timer'lar;
            _processingTimer = new Timer(ProcessingCallback, null, Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer = new Timer(CleanupCallback, null, Timeout.Infinite, Timeout.Infinite);
            _modelUpdateTimer = new Timer(ModelUpdateCallback, null, Timeout.Infinite, Timeout.Infinite);

            // İptal token'ı;
            _cancellationTokenSource = new CancellationTokenSource();
            _processingEvent = new ManualResetEventSlim(false);

            // Performans araçları;
            _performanceStopwatch = new Stopwatch();
            _performanceMetrics = new ConcurrentBag<GesturePerformanceMetric>();

            // Varsayılan konfigürasyon;
            _configuration = GestureRecognizerConfiguration.Default;

            State = GestureRecognizerState.Stopped;

            _logger.LogInformation("GestureRecognizer initialized successfully", GetType());
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Jest tanıyıcıyı başlat;
        /// </summary>
        public async Task InitializeAsync(GestureRecognizerConfiguration configuration = null)
        {
            try
            {
                _performanceMonitor.StartOperation("InitializeGestureRecognizer");
                _logger.LogDebug("Initializing gesture recognizer");

                if (_isInitialized)
                {
                    _logger.LogWarning("Gesture recognizer is already initialized");
                    return;
                }

                // Konfigürasyonu ayarla;
                if (configuration != null)
                {
                    _configuration = configuration;
                }

                // Model yükle;
                await LoadModelsAsync();

                // Bileşenleri yapılandır;
                _gestureClassifier.Configure(_configuration.ClassificationConfig);
                _gestureAnalyzer.Configure(_configuration.AnalysisConfig);
                _patternMatcher.Configure(_configuration.PatternConfig);
                _sequenceRecognizer.Configure(_configuration.SequenceConfig);
                _confidenceCalculator.Configure(_configuration.ConfidenceConfig);
                _gestureSmoother.Configure(_configuration.SmoothingConfig);
                _gestureValidator.Configure(_configuration.ValidationConfig);
                _gestureCache.Configure(_configuration.CacheConfig);
                _modelManager.Configure(_configuration.ModelConfig);

                // Detector'ları yapılandır;
                foreach (var detector in _detectors)
                {
                    detector.Configure(_configuration);
                }

                // Gesture pattern'lerini yükle;
                await LoadGesturePatternsAsync();

                // Kullanıcı profillerini yükle;
                await LoadUserProfilesAsync();

                // Frame işleme thread'ini başlat;
                StartFrameProcessing();

                // Timer'ları başlat;
                StartTimers();

                // Reactive stream'leri başlat;
                StartReactiveStreams();

                _isInitialized = true;
                _startTime = DateTime.UtcNow;
                State = GestureRecognizerState.Running;

                _logger.LogInformation($"Gesture recognizer initialized successfully with {_detectors.Count} detectors");
                _eventBus.Publish(new GestureRecognizerInitializedEvent(this));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize gesture recognizer");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.InitializationFailed);
                throw new GestureRecognizerException("Failed to initialize gesture recognizer", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("InitializeGestureRecognizer");
            }
        }

        /// <summary>
        /// Yeni session başlat;
        /// </summary>
        public async Task<GestureSession> StartSessionAsync(
            string sessionId,
            SessionType sessionType = SessionType.Default,
            GestureSessionContext context = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("StartGestureSession");
                _logger.LogDebug($"Starting gesture session: {sessionId}, Type: {sessionType}");

                // Session var mı kontrol et;
                if (_activeSessions.ContainsKey(sessionId))
                {
                    _logger.LogWarning($"Session already exists: {sessionId}");
                    return _activeSessions[sessionId];
                }

                // Yeni session oluştur;
                var session = new GestureSession(sessionId, sessionType, context ?? GestureSessionContext.Default)
                {
                    StartTime = DateTime.UtcNow,
                    Status = GestureSessionStatus.Active,
                    Configuration = _configuration.SessionConfig;
                };

                // Session'a ekle;
                if (!_activeSessions.TryAdd(sessionId, session))
                {
                    throw new InvalidOperationException($"Failed to add session: {sessionId}");
                }

                // Event tetikle;
                OnSessionStarted(session);

                _logger.LogInformation($"Gesture session started: {sessionId}, Type: {sessionType}");

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start gesture session");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.StartSessionFailed);
                throw new GestureRecognizerException("Failed to start gesture session", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StartGestureSession");
            }
        }

        /// <summary>
        /// Frame işle;
        /// </summary>
        public async Task<GestureRecognitionResult> ProcessFrameAsync(
            byte[] frameData,
            FrameType frameType,
            string sessionId = null,
            GestureRecognitionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("ProcessGestureFrame");
                _logger.LogTrace($"Processing gesture frame: {frameData.Length} bytes, Type: {frameType}");

                _performanceStopwatch.Restart();

                // Session kontrolü;
                GestureSession session = null;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    _activeSessions.TryGetValue(sessionId, out session);
                }

                // Seçenekleri hazırla;
                var recognitionOptions = options ?? GestureRecognitionOptions.Default;

                // Frame'i hazırla;
                var gestureFrame = await PrepareFrameAsync(frameData, frameType, recognitionOptions);
                if (gestureFrame == null)
                {
                    throw new GestureRecognizerException("Failed to prepare gesture frame");
                }

                // Session bilgilerini ekle;
                if (session != null)
                {
                    gestureFrame.SessionId = sessionId;
                    session.TotalFrames++;
                    session.LastFrameTime = DateTime.UtcNow;
                }

                // Frame'i kuyruğa ekle;
                _frameQueue.Enqueue(gestureFrame);
                _processingEvent.Set();

                // Frame'i işle;
                var result = await RecognizeGestureAsync(gestureFrame, session, recognitionOptions);

                // Session bilgilerini ekle;
                if (session != null)
                {
                    result.SessionId = sessionId;
                    UpdateSessionMetrics(session, result);
                }

                // İstatistikleri güncelle;
                UpdateStatistics(result);

                // Sonuçları cache'e ekle;
                await CacheRecognitionResultAsync(result);

                // Event tetikle;
                OnGestureRecognized(result);

                _performanceStopwatch.Stop();
                UpdatePerformanceMetrics(_performanceStopwatch.ElapsedMilliseconds, result.IsRecognized);

                _logger.LogTrace($"Gesture frame processed: {result.IsRecognized}, Gesture: {result.GestureType}, Confidence: {result.Confidence:F4}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process gesture frame");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.ProcessFrameFailed);
                throw new GestureRecognizerException("Failed to process gesture frame", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("ProcessGestureFrame");
            }
        }

        /// <summary>
        /// Batch frame işle;
        /// </summary>
        public async Task<BatchGestureRecognitionResult> ProcessFramesBatchAsync(
            IEnumerable<byte[]> framesData,
            FrameType frameType,
            string sessionId = null,
            GestureRecognitionOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("ProcessGestureFramesBatch");
                _logger.LogDebug($"Processing batch of {framesData.Count()} gesture frames");

                var batchResults = new List<GestureRecognitionResult>();
                var failedFrames = new List<FailedGestureFrame>();
                var totalRecognized = 0;

                // Paralel işleme;
                var tasks = framesData.Select(async (frameData, index) =>
                {
                    try
                    {
                        var result = await ProcessFrameAsync(frameData, frameType, sessionId, options);
                        lock (batchResults)
                        {
                            batchResults.Add(result);
                            if (result.IsRecognized)
                            {
                                totalRecognized++;
                            }
                        }
                        return (Success: true, Index: index, Result: result, Error: (string)null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to process gesture frame {index}");
                        return (Success: false, Index: index, Result: (GestureRecognitionResult)null, Error: ex.Message);
                    }
                });

                var results = await Task.WhenAll(tasks);

                // Başarısız frame'leri topla;
                foreach (var result in results.Where(r => !r.Success))
                {
                    failedFrames.Add(new FailedGestureFrame;
                    {
                        Index = result.Index,
                        Error = result.Error,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var batchResult = new BatchGestureRecognitionResult;
                {
                    Timestamp = DateTime.UtcNow,
                    TotalFrames = framesData.Count(),
                    ProcessedFrames = batchResults.Count,
                    FailedFrames = failedFrames.Count,
                    TotalRecognized = totalRecognized,
                    Results = batchResults,
                    FailedFramesList = failedFrames,
                    AverageConfidence = batchResults.Any() ? batchResults.Average(r => r.Confidence) : 0,
                    RecognitionRate = batchResults.Any() ? (double)totalRecognized / batchResults.Count : 0,
                    ProcessingTimeMs = _performanceStopwatch.ElapsedMilliseconds;
                };

                // Event tetikle;
                _eventBus.Publish(new BatchGestureRecognitionCompletedEvent(batchResult));

                _logger.LogDebug($"Batch gesture recognition completed: {batchResults.Count} processed, {failedFrames.Count} failed, {totalRecognized} recognized");

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process gesture frames batch");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.BatchProcessingFailed);
                throw new GestureRecognizerException("Failed to process gesture frames batch", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("ProcessGestureFramesBatch");
            }
        }

        /// <summary>
        /// Real-time video akışını işle;
        /// </summary>
        public async Task<VideoStreamResult> ProcessVideoStreamAsync(
            string streamUrl,
            string sessionId = null,
            VideoStreamOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("ProcessGestureVideoStream");
                _logger.LogDebug($"Processing gesture video stream: {streamUrl}");

                var streamOptions = options ?? VideoStreamOptions.Default;
                var session = await StartSessionAsync(sessionId ?? Guid.NewGuid().ToString(), SessionType.VideoStream);

                var result = new VideoStreamResult;
                {
                    StreamUrl = streamUrl,
                    SessionId = session.SessionId,
                    StartTime = DateTime.UtcNow,
                    Status = StreamStatus.Starting;
                };

                // Video stream'ini başlat;
                await StartVideoStreamProcessingAsync(streamUrl, session, streamOptions);

                result.Status = StreamStatus.Running;

                _logger.LogInformation($"Gesture video stream processing started: {streamUrl}, Session: {session.SessionId}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process gesture video stream");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.VideoStreamFailed);
                throw new GestureRecognizerException("Failed to process gesture video stream", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("ProcessGestureVideoStream");
            }
        }

        /// <summary>
        /// Gesture pattern'i kaydet;
        /// </summary>
        public async Task<PatternRegistrationResult> RegisterGesturePatternAsync(
            GesturePattern pattern,
            IEnumerable<byte[]> trainingFrames = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("RegisterGesturePattern");
                _logger.LogDebug($"Registering gesture pattern: {pattern.Name}");

                if (pattern == null)
                {
                    throw new ArgumentNullException(nameof(pattern));
                }

                // Pattern'i kaydet;
                if (!_gesturePatterns.TryAdd(pattern.Id, pattern))
                {
                    throw new InvalidOperationException($"Pattern already exists: {pattern.Id}");
                }

                // Pattern matcher'a ekle;
                await _patternMatcher.AddPatternAsync(pattern);

                // Eğitim frame'leri varsa, modeli güncelle;
                if (trainingFrames != null && trainingFrames.Any())
                {
                    await UpdateModelWithPatternAsync(pattern, trainingFrames);
                }

                var result = new PatternRegistrationResult;
                {
                    PatternId = pattern.Id,
                    PatternName = pattern.Name,
                    IsRegistered = true,
                    Timestamp = DateTime.UtcNow,
                    TrainingSamples = trainingFrames?.Count() ?? 0;
                };

                _logger.LogInformation($"Gesture pattern registered: {pattern.Name}, Type: {pattern.GestureType}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register gesture pattern");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.PatternRegistrationFailed);
                throw new GestureRecognizerException("Failed to register gesture pattern", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("RegisterGesturePattern");
            }
        }

        /// <summary>
        /// Kullanıcı gesture profilini getir;
        /// </summary>
        public async Task<UserGestureProfile> GetUserGestureProfileAsync(string userId)
        {
            ValidateInitialized();

            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
                }

                if (_userProfiles.TryGetValue(userId, out var profile))
                {
                    return profile;
                }

                // Profili yükle;
                profile = await LoadUserGestureProfileAsync(userId);
                if (profile != null)
                {
                    _userProfiles[userId] = profile;
                }

                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get user gesture profile");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.GetProfileFailed);
                throw new GestureRecognizerException("Failed to get user gesture profile", ex);
            }
        }

        /// <summary>
        /// Model eğit;
        /// </summary>
        public async Task<GestureTrainingResult> TrainModelAsync(
            GestureTrainingData trainingData,
            TrainingOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("TrainGestureModel");
                _logger.LogDebug("Training gesture recognition model");

                State = GestureRecognizerState.Training;

                var trainingOptions = options ?? TrainingOptions.Default;
                var result = await _modelManager.TrainAsync(trainingData, trainingOptions);

                if (result.IsSuccessful)
                {
                    // Yeni modeli yükle;
                    await LoadModelAsync(result.ModelPath);

                    // Pattern'leri yeniden yükle;
                    await ReloadPatternsAsync();

                    State = GestureRecognizerState.Running;
                }
                else;
                {
                    State = GestureRecognizerState.Error;
                    throw new GestureRecognizerException($"Model training failed: {result.ErrorMessage}");
                }

                // Event tetikle;
                OnModelTrained(result);

                _logger.LogInformation($"Gesture model training completed: {result.IsSuccessful}, accuracy: {result.Accuracy:F4}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to train gesture model");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.TrainingFailed);
                State = GestureRecognizerState.Error;
                throw new GestureRecognizerException("Failed to train gesture model", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("TrainGestureModel");
            }
        }

        /// <summary>
        /// Model yükle;
        /// </summary>
        public async Task<bool> LoadModelAsync(string modelPath, ModelOptions options = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("LoadGestureModel");
                _logger.LogDebug($"Loading gesture model from: {modelPath}");

                var modelOptions = options ?? ModelOptions.Default;
                var model = await _modelManager.LoadGestureModelAsync(modelPath, modelOptions);

                if (model != null)
                {
                    // Mevcut modeli temizle;
                    _inferenceSession?.Dispose();

                    // Yeni modeli yükle;
                    _inferenceSession = model.InferenceSession;
                    _dnnNet = model.DnnNet;
                    ModelInfo = model.Info;
                    IsUsingGPU = model.IsUsingGPU;

                    // Detector'ları güncelle;
                    foreach (var detector in _detectors)
                    {
                        detector.SetModel(model);
                    }

                    _logger.LogInformation($"Gesture model loaded successfully: {model.Info.Name}, version: {model.Info.Version}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load gesture model");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.ModelLoadFailed);
                throw new GestureRecognizerException("Failed to load gesture model", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("LoadGestureModel");
            }
        }

        /// <summary>
        /// Performans metriklerini getir;
        /// </summary>
        public async Task<GestureRecognizerMetrics> GetMetricsAsync()
        {
            try
            {
                var metrics = new GestureRecognizerMetrics;
                {
                    Timestamp = DateTime.UtcNow,
                    Uptime = Uptime,
                    State = State,
                    IsInitialized = _isInitialized,
                    IsProcessing = _isProcessing,
                    IsModelLoaded = IsModelLoaded,
                    ActiveSessionCount = ActiveSessionCount,
                    PatternCount = PatternCount,
                    TotalFramesProcessed = _totalFramesProcessed,
                    TotalGesturesRecognized = _totalGesturesRecognized,
                    TotalHandsDetected = _totalHandsDetected,
                    TotalBodiesDetected = _totalBodiesDetected,
                    QueueSize = QueueSize,
                    FramesPerSecond = FramesPerSecond,
                    AverageProcessingTimeMs = AverageProcessingTimeMs,
                    MemoryUsageMB = MemoryUsageMB,
                    IsUsingGPU = IsUsingGPU,
                    ModelInfo = ModelInfo,
                    Configuration = _configuration;
                };

                // Detector metriklerini ekle;
                metrics.DetectorMetrics = _detectors;
                    .Select(d => d.GetMetrics())
                    .ToList();

                // Session metriklerini ekle;
                metrics.SessionMetrics = _activeSessions.Values;
                    .Select(s => s.Metrics)
                    .ToList();

                // Performans metriklerini ekle;
                metrics.PerformanceMetrics = _performanceMetrics.ToList();

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get gesture recognizer metrics");
                return new GestureRecognizerMetrics;
                {
                    Timestamp = DateTime.UtcNow,
                    Error = ex.Message;
                };
            }
        }

        /// <summary>
        /// Jest tanımayı durdur;
        /// </summary>
        public async Task StopAsync()
        {
            try
            {
                _performanceMonitor.StartOperation("StopGestureRecognizer");
                _logger.LogDebug("Stopping gesture recognizer");

                if (!_isInitialized)
                {
                    return;
                }

                State = GestureRecognizerState.Stopping;

                // İptal token'ını tetikle;
                _cancellationTokenSource.Cancel();

                // Timer'ları durdur;
                StopTimers();

                // Aktif session'ları sonlandır;
                foreach (var sessionId in _activeSessions.Keys.ToList())
                {
                    try
                    {
                        await EndSessionAsync(sessionId, GestureSessionEndReason.RecognizerStopped);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to end session during stop: {sessionId}");
                    }
                }

                // Stream'leri durdur;
                StopReactiveStreams();

                // Verileri kaydet;
                await SaveDataAsync();

                // Kaynakları temizle;
                await CleanupResourcesAsync();

                _isInitialized = false;
                State = GestureRecognizerState.Stopped;

                _logger.LogInformation("Gesture recognizer stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop gesture recognizer");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.StopFailed);
                throw new GestureRecognizerException("Failed to stop gesture recognizer", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StopGestureRecognizer");
            }
        }

        /// <summary>
        /// Session'ı sonlandır;
        /// </summary>
        public async Task<GestureSessionResult> EndSessionAsync(
            string sessionId,
            GestureSessionEndReason reason = GestureSessionEndReason.Completed)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("EndGestureSession");
                _logger.LogDebug($"Ending gesture session: {sessionId}, Reason: {reason}");

                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    throw new SessionNotFoundException($"Session not found: {sessionId}");
                }

                session.EndTime = DateTime.UtcNow;
                session.Status = GestureSessionStatus.Ended;
                session.EndReason = reason;

                // Session metriklerini kaydet;
                await SaveSessionMetricsAsync(session);

                // Session'ı kaldır;
                _activeSessions.TryRemove(sessionId, out _);

                var result = new GestureSessionResult;
                {
                    SessionId = sessionId,
                    StartTime = session.StartTime,
                    EndTime = session.EndTime,
                    Duration = session.EndTime - session.StartTime,
                    TotalFrames = session.TotalFrames,
                    TotalGestures = session.TotalGestures,
                    Status = session.Status,
                    EndReason = reason,
                    Metrics = session.Metrics;
                };

                // Event tetikle;
                OnSessionEnded(result);

                _logger.LogInformation($"Gesture session ended: {sessionId}, Duration: {result.Duration.TotalSeconds:F1}s, Frames: {session.TotalFrames}, Gestures: {session.TotalGestures}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to end gesture session");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.EndSessionFailed);
                throw new GestureRecognizerException("Failed to end gesture session", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("EndGestureSession");
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// İnitilize kontrolü yap;
        /// </summary>
        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new GestureRecognizerException("Gesture recognizer is not initialized. Call InitializeAsync first.");
            }

            if (State == GestureRecognizerState.Error)
            {
                throw new GestureRecognizerException("Gesture recognizer is in error state. Check logs for details.");
            }
        }

        /// <summary>
        /// Detector'ları başlat;
        /// </summary>
        private List<IGestureDetector> InitializeDetectors()
        {
            var detectors = new List<IGestureDetector>
            {
                new HandDetector(_logger),
                new BodyPoseDetector(_logger),
                new FaceLandmarkDetector(_logger),
                new MotionDetector(_logger),
                new DepthSensorDetector(_logger),
                new SkeletonTracker(_logger),
                new KeypointDetector(_logger),
                new GestureFeatureExtractor(_logger)
            };

            _logger.LogDebug($"Initialized {detectors.Count} gesture detectors");
            return detectors;
        }

        /// <summary>
        /// Model'leri yükle;
        /// </summary>
        private async Task LoadModelsAsync()
        {
            try
            {
                _logger.LogDebug("Loading gesture recognition models");

                // Model manager'dan model yükle;
                var model = await _modelManager.LoadDefaultGestureModelAsync(_configuration.ModelConfig);

                if (model != null)
                {
                    _inferenceSession = model.InferenceSession;
                    _dnnNet = model.DnnNet;
                    ModelInfo = model.Info;
                    IsUsingGPU = model.IsUsingGPU;

                    _logger.LogInformation($"Gesture model loaded: {model.Info.Name}, Input: {model.Info.InputShape}, Output: {model.Info.OutputShape}");
                }
                else;
                {
                    throw new GestureRecognizerException("Failed to load gesture recognition model");
                }

                // Detector modellerini yükle;
                foreach (var detector in _detectors)
                {
                    await detector.LoadModelAsync(_configuration.ModelConfig);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load models");
                throw;
            }
        }

        /// <summary>
        /// Frame işlemeyi başlat;
        /// </summary>
        private void StartFrameProcessing()
        {
            Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        // Frame bekleyin;
                        _processingEvent.Wait(_cancellationTokenSource.Token);
                        _processingEvent.Reset();

                        // Frame'leri işle;
                        await ProcessFramesAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        // İptal edildi, normal çıkış;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in frame processing loop");
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Frame'leri işle;
        /// </summary>
        private async Task ProcessFramesAsync()
        {
            try
            {
                _isProcessing = true;
                _performanceStopwatch.Restart();

                int processedCount = 0;
                var batchStartTime = DateTime.UtcNow;

                while (!_frameQueue.IsEmpty && processedCount < _configuration.MaxBatchSize)
                {
                    if (_frameQueue.TryDequeue(out var gestureFrame))
                    {
                        try
                        {
                            await ProcessSingleFrameAsync(gestureFrame);
                            processedCount++;
                            Interlocked.Increment(ref _totalFramesProcessed);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to process frame: {gestureFrame.FrameId}");
                            _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.FrameProcessingFailed);
                        }
                    }
                }

                _performanceStopwatch.Stop();

                // Performans metriklerini güncelle;
                if (processedCount > 0)
                {
                    var batchDuration = _performanceStopwatch.ElapsedMilliseconds;
                    var processingTimeMs = batchDuration / processedCount;
                    var fps = processedCount / (batchDuration / 1000.0);

                    UpdatePerformanceMetrics(processedCount, batchDuration, processingTimeMs, fps);

                    _logger.LogTrace($"Processed {processedCount} frames in {batchDuration}ms ({processingTimeMs:F1}ms/frame, {fps:F1} FPS)");
                }

                _isProcessing = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch frame processing");
                _errorReporter.ReportError(ex, ErrorCodes.GestureRecognizer.BatchFrameProcessingFailed);
                _isProcessing = false;
            }
        }

        /// <summary>
        /// Tek frame'i işle;
        /// </summary>
        private async Task ProcessSingleFrameAsync(GestureFrame gestureFrame)
        {
            try
            {
                // Detector'ları çalıştır;
                var detectionResults = new List<DetectionResult>();
                foreach (var detector in _detectors)
                {
                    var result = await detector.DetectAsync(gestureFrame);
                    if (result != null)
                    {
                        detectionResults.Add(result);

                        // İstatistikleri güncelle;
                        if (result.DetectionType == DetectionType.Hand)
                        {
                            Interlocked.Increment(ref _totalHandsDetected);
                            OnHandDetected(result);
                        }
                        else if (result.DetectionType == DetectionType.Body)
                        {
                            Interlocked.Increment(ref _totalBodiesDetected);
                            OnBodyDetected(result);
                        }
                    }
                }

                // Gesture'leri çıkar;
                var gestureFeatures = await ExtractGestureFeaturesAsync(gestureFrame, detectionResults);

                // Gesture'leri sınıflandır;
                var classificationResult = await _gestureClassifier.ClassifyAsync(gestureFeatures);

                // Pattern eşleştirme;
                var patternResult = await _patternMatcher.MatchAsync(gestureFeatures);

                // Sequence tanıma;
                var sequenceResult = await _sequenceRecognizer.RecognizeAsync(gestureFrame, gestureFeatures);

                // Güven skoru hesapla;
                var confidenceResult = await _confidenceCalculator.CalculateAsync(gestureFeatures, classificationResult, patternResult);

                // Smoothing uygula;
                var smoothedResult = await _gestureSmoother.SmoothAsync(classificationResult, confidenceResult);

                // Validasyon;
                var validationResult = await _gestureValidator.ValidateAsync(smoothedResult, gestureFrame);

                // Sonuç oluştur;
                var recognitionResult = new GestureRecognitionResult;
                {
                    FrameId = gestureFrame.FrameId,
                    SessionId = gestureFrame.SessionId,
                    Timestamp = DateTime.UtcNow,
                    IsRecognized = validationResult.IsValid && smoothedResult.Confidence > _configuration.ConfidenceThreshold,
                    GestureType = smoothedResult.GestureType,
                    Confidence = smoothedResult.Confidence,
                    Features = gestureFeatures,
                    DetectionResults = detectionResults,
                    ClassificationResult = classificationResult,
                    PatternResult = patternResult,
                    SequenceResult = sequenceResult,
                    ConfidenceResult = confidenceResult,
                    SmoothingResult = smoothedResult,
                    ValidationResult = validationResult,
                    Metadata = gestureFrame.Metadata;
                };

                // Gesture tanındı mı kontrol et;
                if (recognitionResult.IsRecognized)
                {
                    Interlocked.Increment(ref _totalGesturesRecognized);

                    // Gesture event'i oluştur;
                    var gestureEvent = new GestureEvent;
                    {
                        FrameId = gestureFrame.FrameId,
                        SessionId = gestureFrame.SessionId,
                        GestureType = recognitionResult.GestureType,
                        Confidence = recognitionResult.Confidence,
                        Timestamp = DateTime.UtcNow,
                        Features = recognitionResult.Features,
                        Metadata = new Dictionary<string, object>
                        {
                            ["DetectionCount"] = detectionResults.Count,
                            ["FrameType"] = gestureFrame.FrameType.ToString()
                        }
                    };

                    // Reactive subject'e gönder;
                    _gestureSubject.OnNext(gestureEvent);
                    _recognitionSubject.OnNext(recognitionResult);

                    // Gesture tanındı event'ini tetikle;
                    OnGestureDetected(gestureEvent);

                    // Sequence tamamlandı mı kontrol et;
                    if (sequenceResult.IsSequenceCompleted)
                    {
                        OnGestureSequenceCompleted(sequenceResult);
                    }
                }

                // Sonuçları kaydet;
                _recognitionResults.Add(recognitionResult);

                // Session varsa güncelle;
                if (!string.IsNullOrEmpty(gestureFrame.SessionId) &&
                    _activeSessions.TryGetValue(gestureFrame.SessionId, out var session))
                {
                    UpdateSessionWithFrame(session, gestureFrame, recognitionResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing single frame: {gestureFrame.FrameId}");
                throw;
            }
        }

        /// <summary>
        /// Frame'i hazırla;
        /// </summary>
        private async Task<GestureFrame> PrepareFrameAsync(
            byte[] frameData,
            FrameType frameType,
            GestureRecognitionOptions options)
        {
            try
            {
                var frameId = Guid.NewGuid().ToString();

                // Frame'i decode et;
                using var memoryStream = new MemoryStream(frameData);
                using var mat = Mat.FromStream(memoryStream, ImreadModes.Color);

                if (mat.Empty())
                {
                    throw new GestureRecognizerException("Failed to decode frame");
                }

                // Ön işleme;
                var processedMat = await PreprocessFrameAsync(mat, options);

                // Özellikler çıkar;
                var features = await ExtractFrameFeaturesAsync(processedMat);

                // Gesture frame oluştur;
                var gestureFrame = new GestureFrame;
                {
                    FrameId = frameId,
                    FrameType = frameType,
                    Timestamp = DateTime.UtcNow,
                    Width = processedMat.Width,
                    Height = processedMat.Height,
                    Channels = processedMat.Channels(),
                    Data = frameData,
                    ProcessedData = processedMat.ToBytes(),
                    Features = features,
                    Metadata = new Dictionary<string, object>
                    {
                        ["OriginalSize"] = frameData.Length,
                        ["ProcessedSize"] = processedMat.ToBytes().Length,
                        ["Options"] = options.ToDictionary()
                    }
                };

                return gestureFrame;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prepare gesture frame");
                throw new GestureRecognizerException("Failed to prepare gesture frame", ex);
            }
        }

        /// <summary>
        /// Frame ön işleme;
        /// </summary>
        private async Task<Mat> PreprocessFrameAsync(Mat frame, GestureRecognitionOptions options)
        {
            try
            {
                var processedFrame = frame.Clone();

                // Boyutlandırma;
                if (options.ResizeFrame)
                {
                    var targetSize = _configuration.PreprocessingConfig.TargetSize;
                    Cv2.Resize(processedFrame, processedFrame, new Size(targetSize.Width, targetSize.Height));
                }

                // Renk uzayı dönüşümü;
                if (options.ConvertColorSpace)
                {
                    Cv2.CvtColor(processedFrame, processedFrame, ColorConversionCodes.BGR2RGB);
                }

                // Normalizasyon;
                if (options.NormalizeFrame)
                {
                    processedFrame.ConvertTo(processedFrame, MatType.CV_32F, 1.0 / 255.0);
                }

                // Gürültü azaltma;
                if (options.DenoiseFrame)
                {
                    Cv2.GaussianBlur(processedFrame, processedFrame, new Size(3, 3), 0);
                }

                // Kontrast ayarı;
                if (options.AdjustContrast)
                {
                    processedFrame.ConvertTo(processedFrame, -1, options.ContrastAlpha, options.ContrastBeta);
                }

                return processedFrame;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to preprocess frame");
                return frame;
            }
        }

        /// <summary>
        /// Frame özelliklerini çıkar;
        /// </summary>
        private async Task<FrameFeatures> ExtractFrameFeaturesAsync(Mat frame)
        {
            try
            {
                var features = new FrameFeatures;
                {
                    Timestamp = DateTime.UtcNow;
                };

                // Histogram özellikleri;
                using var histograms = new Mat();
                Cv2.CalcHist(new[] { frame }, new[] { 0, 1, 2 }, null, histograms, 3, new[] { 256, 256, 256 }, new[] { new Rangef(0, 256), new Rangef(0, 256), new Rangef(0, 256) });

                // Edge özellikleri;
                using var edges = new Mat();
                Cv2.Canny(frame, edges, 50, 150);

                // Blob özellikleri;
                var keypoints = new List<KeyPoint>();
                using var detector = ORB.Create();
                detector.Detect(frame, keypoints);

                // Özellik vektörü;
                features.Histogram = histograms.ToFloatArray();
                features.EdgeDensity = Cv2.CountNonZero(edges) / (double)(edges.Width * edges.Height);
                features.KeypointCount = keypoints.Count;
                features.Brightness = frame.Mean().Val0;
                features.Contrast = CalculateContrast(frame);
                features.Sharpness = CalculateSharpness(frame);

                return features;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract frame features");
                return new FrameFeatures();
            }
        }

        /// <summary>
        /// Kontrast hesapla;
        /// </summary>
        private double CalculateContrast(Mat frame)
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

            var mean = gray.Mean();
            var stdDev = new MCvScalar();
            var meanDev = new MCvScalar();
            Cv2.MeanStdDev(gray, out mean, out stdDev);

            return stdDev.Val0;
        }

        /// <summary>
        /// Keskinlik hesapla;
        /// </summary>
        private double CalculateSharpness(Mat frame)
        {
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

            using var laplacian = new Mat();
            Cv2.Laplacian(gray, laplacian, MatType.CV_64F);

            var mean = new MCvScalar();
            var stdDev = new MCvScalar();
            Cv2.MeanStdDev(laplacian, out mean, out stdDev);

            return stdDev.Val0 * stdDev.Val0;
        }

        /// <summary>
        /// Gesture özelliklerini çıkar;
        /// </summary>
        private async Task<GestureFeatures> ExtractGestureFeaturesAsync(
            GestureFrame frame,
            List<DetectionResult> detectionResults)
        {
            try
            {
                var features = new GestureFeatures;
                {
                    FrameId = frame.FrameId,
                    Timestamp = frame.Timestamp,
                    FrameFeatures = frame.Features;
                };

                // Tüm detection sonuçlarından özellikleri topla;
                foreach (var detection in detectionResults)
                {
                    switch (detection.DetectionType)
                    {
                        case DetectionType.Hand:
                            var handFeatures = ExtractHandFeatures(detection);
                            features.HandFeatures.Add(handFeatures);
                            break;

                        case DetectionType.Body:
                            var bodyFeatures = ExtractBodyFeatures(detection);
                            features.BodyFeatures = bodyFeatures;
                            break;

                        case DetectionType.Face:
                            var faceFeatures = ExtractFaceFeatures(detection);
                            features.FaceFeatures = faceFeatures;
                            break;

                        case DetectionType.Keypoint:
                            var keypointFeatures = ExtractKeypointFeatures(detection);
                            features.KeypointFeatures.AddRange(keypointFeatures);
                            break;
                    }
                }

                // Spatial özellikler;
                features.SpatialFeatures = CalculateSpatialFeatures(features);

                // Temporal özellikler (önceki frame'lerden)
                var temporalFeatures = await CalculateTemporalFeaturesAsync(frame, features);
                features.TemporalFeatures = temporalFeatures;

                // Motion özellikleri;
                features.MotionFeatures = CalculateMotionFeatures(features);

                return features;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract gesture features");
                return new GestureFeatures();
            }
        }

        /// <summary>
        /// El özelliklerini çıkar;
        /// </summary>
        private HandFeatures ExtractHandFeatures(DetectionResult detection)
        {
            var handFeatures = new HandFeatures;
            {
                HandId = detection.Id,
                Confidence = detection.Confidence,
                BoundingBox = detection.BoundingBox,
                Landmarks = detection.Landmarks,
                IsLeftHand = detection.Metadata.ContainsKey("IsLeftHand") && (bool)detection.Metadata["IsLeftHand"],
                GestureType = detection.Metadata.ContainsKey("GestureType") ? (GestureType)detection.Metadata["GestureType"] : GestureType.Unknown;
            };

            // El açıları;
            if (detection.Landmarks != null && detection.Landmarks.Count >= 21)
            {
                handFeatures.FingerAngles = CalculateFingerAngles(detection.Landmarks);
                handFeatures.PalmOrientation = CalculatePalmOrientation(detection.Landmarks);
                handFeatures.HandRotation = CalculateHandRotation(detection.Landmarks);
            }

            // El shape özellikleri;
            handFeatures.HandShape = CalculateHandShape(detection.Landmarks);
            handFeatures.HandSize = CalculateHandSize(detection.BoundingBox);

            return handFeatures;
        }

        /// <summary>
        /// Vücut özelliklerini çıkar;
        /// </summary>
        private BodyFeatures ExtractBodyFeatures(DetectionResult detection)
        {
            var bodyFeatures = new BodyFeatures;
            {
                BodyId = detection.Id,
                Confidence = detection.Confidence,
                BoundingBox = detection.BoundingBox,
                Keypoints = detection.Keypoints,
                PoseType = detection.Metadata.ContainsKey("PoseType") ? (PoseType)detection.Metadata["PoseType"] : PoseType.Unknown;
            };

            // Vücut açıları;
            if (detection.Keypoints != null && detection.Keypoints.Count >= 17)
            {
                bodyFeatures.JointAngles = CalculateJointAngles(detection.Keypoints);
                bodyFeatures.PoseOrientation = CalculatePoseOrientation(detection.Keypoints);
                bodyFeatures.BodyRotation = CalculateBodyRotation(detection.Keypoints);
            }

            // Vücut oranları;
            bodyFeatures.BodyProportions = CalculateBodyProportions(detection.Keypoints);

            return bodyFeatures;
        }

        /// <summary>
        /// Yüz özelliklerini çıkar;
        /// </summary>
        private FaceFeatures ExtractFaceFeatures(DetectionResult detection)
        {
            var faceFeatures = new FaceFeatures;
            {
                FaceId = detection.Id,
                Confidence = detection.Confidence,
                BoundingBox = detection.BoundingBox,
                Landmarks = detection.Landmarks,
                Expression = detection.Metadata.ContainsKey("Expression") ? (FacialExpression)detection.Metadata["Expression"] : FacialExpression.Neutral;
            };

            // Yüz ifadesi özellikleri;
            if (detection.Landmarks != null && detection.Landmarks.Count >= 68)
            {
                faceFeatures.EyeFeatures = CalculateEyeFeatures(detection.Landmarks);
                faceFeatures.MouthFeatures = CalculateMouthFeatures(detection.Landmarks);
                faceFeatures.EyebrowFeatures = CalculateEyebrowFeatures(detection.Landmarks);
            }

            // Head pose;
            faceFeatures.HeadPose = CalculateHeadPose(detection.Landmarks);

            return faceFeatures;
        }

        /// <summary>
        /// Keypoint özelliklerini çıkar;
        /// </summary>
        private List<KeypointFeatures> ExtractKeypointFeatures(DetectionResult detection)
        {
            var keypointFeatures = new List<KeypointFeatures>();

            if (detection.Keypoints != null)
            {
                foreach (var keypoint in detection.Keypoints)
                {
                    keypointFeatures.Add(new KeypointFeatures;
                    {
                        Id = keypoint.Id,
                        X = keypoint.X,
                        Y = keypoint.Y,
                        Confidence = keypoint.Confidence,
                        Type = keypoint.Type;
                    });
                }
            }

            return keypointFeatures;
        }

        /// <summary>
        /// Spatial özellikleri hesapla;
        /// </summary>
        private SpatialFeatures CalculateSpatialFeatures(GestureFeatures features)
        {
            var spatialFeatures = new SpatialFeatures();

            // El pozisyonları;
            if (features.HandFeatures.Any())
            {
                spatialFeatures.HandPositions = features.HandFeatures;
                    .Select(h => new Vector2(h.BoundingBox.X + h.BoundingBox.Width / 2, h.BoundingBox.Y + h.BoundingBox.Height / 2))
                    .ToList();

                spatialFeatures.HandDistances = CalculateDistances(spatialFeatures.HandPositions);
            }

            // Vücut pozisyonu;
            if (features.BodyFeatures != null)
            {
                spatialFeatures.BodyPosition = new Vector2(
                    features.BodyFeatures.BoundingBox.X + features.BodyFeatures.BoundingBox.Width / 2,
                    features.BodyFeatures.BoundingBox.Y + features.BodyFeatures.BoundingBox.Height / 2;
                );
            }

            // Yüz pozisyonu;
            if (features.FaceFeatures != null)
            {
                spatialFeatures.FacePosition = new Vector2(
                    features.FaceFeatures.BoundingBox.X + features.FaceFeatures.BoundingBox.Width / 2,
                    features.FaceFeatures.BoundingBox.Y + features.FaceFeatures.BoundingBox.Height / 2;
                );
            }

            // Relative pozisyonlar;
            spatialFeatures.RelativePositions = CalculateRelativePositions(spatialFeatures);

            return spatialFeatures;
        }

        /// <summary>
        /// Temporal özellikleri hesapla;
        /// </summary>
        private async Task<TemporalFeatures> CalculateTemporalFeaturesAsync(GestureFrame frame, GestureFeatures currentFeatures)
        {
            var temporalFeatures = new TemporalFeatures();

            // Önceki frame'leri getir;
            var previousFrames = await _gestureCache.GetRecentFramesAsync(frame.SessionId, 10);
            if (previousFrames.Any())
            {
                // Motion vektörleri;
                temporalFeatures.MotionVectors = CalculateMotionVectors(previousFrames, currentFeatures);

                // Velocity;
                temporalFeatures.Velocity = CalculateVelocity(temporalFeatures.MotionVectors);

                // Acceleration;
                temporalFeatures.Acceleration = CalculateAcceleration(temporalFeatures.Velocity);

                // Jerk (ivmenin değişim hızı)
                temporalFeatures.Jerk = CalculateJerk(temporalFeatures.Acceleration);

                // Periodicity;
                temporalFeatures.Periodicity = CalculatePeriodicity(temporalFeatures.MotionVectors);
            }

            return temporalFeatures;
        }

        /// <summary>
        /// Motion özelliklerini hesapla;
        /// </summary>
        private MotionFeatures CalculateMotionFeatures(GestureFeatures features)
        {
            var motionFeatures = new MotionFeatures();

            // El hareketleri;
            if (features.HandFeatures.Any())
            {
                motionFeatures.HandMotion = CalculateHandMotion(features.HandFeatures);
            }

            // Vücut hareketleri;
            if (features.BodyFeatures != null)
            {
                motionFeatures.BodyMotion = CalculateBodyMotion(features.BodyFeatures);
            }

            // Yüz hareketleri;
            if (features.FaceFeatures != null)
            {
                motionFeatures.FacialMotion = CalculateFacialMotion(features.FaceFeatures);
            }

            // Genel motion;
            motionFeatures.OverallMotion = CalculateOverallMotion(motionFeatures);

            return motionFeatures;
        }

        /// <summary>
        /// Gesture tanı;
        /// </summary>
        private async Task<GestureRecognitionResult> RecognizeGestureAsync(
            GestureFrame gestureFrame,
            GestureSession session,
            GestureRecognitionOptions options)
        {
            // Frame'i kuyruğa ekle ve async olarak işlenmesini bekle;
            // Gerçek işleme ProcessSingleFrameAsync'de yapılıyor;

            // Geçici sonuç döndür;
            return new GestureRecognitionResult;
            {
                FrameId = gestureFrame.FrameId,
                SessionId = gestureFrame.SessionId,
                Timestamp = DateTime.UtcNow,
                IsRecognized = false,
                GestureType = GestureType.Unknown,
                Confidence = 0,
                Features = gestureFrame.Features,
                ProcessingStatus = ProcessingStatus.Queued;
            };
        }

        /// <summary>
        /// Session'ı frame ile güncelle;
        /// </summary>
        private void UpdateSessionWithFrame(GestureSession session, GestureFrame frame, GestureRecognitionResult result)
        {
            lock (session.Metrics)
            {
                session.TotalFrames++;
                session.LastFrameTime = DateTime.UtcNow;

                if (result.IsRecognized)
                {
                    session.TotalGestures++;
                    session.LastGestureTime = DateTime.UtcNow;
                    session.RecentGestures.Enqueue(result);

                    // Recent gestures queue boyutunu kontrol et;
                    while (session.RecentGestures.Count > _configuration.MaxRecentGestures)
                    {
                        session.RecentGestures.TryDequeue(out _);
                    }
                }

                // Frame metrikleri;
                session.Metrics.TotalFrames++;
                session.Metrics.FramesPerSecond = session.Metrics.TotalFrames / (DateTime.UtcNow - session.StartTime).TotalSeconds;

                // Gesture metrikleri;
                if (result.IsRecognized)
                {
                    session.Metrics.TotalGestures++;
                    session.Metrics.RecognitionRate = (double)session.Metrics.TotalGestures / session.Metrics.TotalFrames;

                    // Gesture tipi istatistikleri;
                    if (!session.Metrics.GestureTypeCounts.ContainsKey(result.GestureType))
                    {
                        session.Metrics.GestureTypeCounts[result.GestureType] = 0;
                    }
                    session.Metrics.GestureTypeCounts[result.GestureType]++;

                    // Confidence istatistikleri;
                    session.Metrics.AverageConfidence = (session.Metrics.AverageConfidence * (session.Metrics.TotalGestures - 1) + result.Confidence) / session.Metrics.TotalGestences;
                    session.Metrics.MinConfidence = Math.Min(session.Metrics.MinConfidence, result.Confidence);
                    session.Metrics.MaxConfidence = Math.Max(session.Metrics.MaxConfidence, result.Confidence);
                }

                // Detection metrikleri;
                if (result.DetectionResults != null)
                {
                    foreach (var detection in result.DetectionResults)
                    {
                        switch (detection.DetectionType)
                        {
                            case DetectionType.Hand:
                                session.Metrics.TotalHandsDetected++;
                                break;
                            case DetectionType.Body:
                                session.Metrics.TotalBodiesDetected++;
                                break;
                            case DetectionType.Face:
                                session.Metrics.TotalFacesDetected++;
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// İstatistikleri güncelle;
        /// </summary>
        private void UpdateStatistics(GestureRecognitionResult result)
        {
            lock (_syncLock)
            {
                _totalFramesProcessed++;

                if (result.IsRecognized)
                {
                    _totalGesturesRecognized++;
                }

                if (result.DetectionResults != null)
                {
                    foreach (var detection in result.DetectionResults)
                    {
                        if (detection.DetectionType == DetectionType.Hand)
                        {
                            _totalHandsDetected++;
                        }
                        else if (detection.DetectionType == DetectionType.Body)
                        {
                            _totalBodiesDetected++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Performans metriklerini güncelle;
        /// </summary>
        private void UpdatePerformanceMetrics(int framesProcessed, long batchDurationMs, double avgProcessingTimeMs, double fps)
        {
            lock (_syncLock)
            {
                // Hareketli ortalama hesapla;
                FramesPerSecond = (FramesPerSecond * 0.7) + (fps * 0.3);
                AverageProcessingTimeMs = (AverageProcessingTimeMs * 0.7) + (avgProcessingTimeMs * 0.3);

                // Performans metriği kaydet;
                var metric = new GesturePerformanceMetric;
                {
                    Timestamp = DateTime.UtcNow,
                    OperationName = "ProcessFrames",
                    DurationMs = batchDurationMs,
                    FramesProcessed = framesProcessed,
                    FramesPerSecond = fps,
                    AverageProcessingTimeMs = avgProcessingTimeMs,
                    MemoryUsageMB = MemoryUsageMB,
                    QueueSize = QueueSize,
                    ActiveSessions = ActiveSessionCount;
                };

                _performanceMetrics.Add(metric);

                // Eski metrikleri temizle;
                if (_performanceMetrics.Count > _configuration.MaxPerformanceMetrics)
                {
                    var oldMetrics = _performanceMetrics;
                        .OrderBy(m => m.Timestamp)
                        .Take(_performanceMetrics.Count - _configuration.MaxPerformanceMetrics)
                        .ToList();

                    foreach (var oldMetric in oldMetrics)
                    {
                        _performanceMetrics.TryTake(out _);
                    }
                }

                // Event tetikle;
                OnPerformanceMetricsUpdated();
            }
        }

        /// <summary>
        /// Timer callback'leri;
        /// </summary>
        private void ProcessingCallback(object state)
        {
            try
            {
                // İşleme durumunu kontrol et;
                CheckProcessingStatus();

                // Queue boyutunu optimize et;
                OptimizeQueue();

                // Session timeout kontrolü;
                CheckSessionTimeouts();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in processing callback");
            }
        }

        private void CleanupCallback(object state)
        {
            try
            {
                // Eski sonuçları temizle;
                CleanupOldResults();

                // Bellek optimizasyonu;
                OptimizeMemoryUsage();

                // Cache temizleme;
                _gestureCache.Cleanup();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup callback");
            }
        }

        private void ModelUpdateCallback(object state)
        {
            try
            {
                // Model güncellemelerini kontrol et;
                CheckModelUpdates();

                // Pattern'leri yenile;
                RefreshPatterns();

                // Profil güncellemeleri;
                UpdateUserProfiles();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in model update callback");
            }
        }

        /// <summary>
        /// Timer'ları başlat;
        /// </summary>
        private void StartTimers()
        {
            _processingTimer.Change(
                _configuration.ProcessingInterval,
                _configuration.ProcessingInterval);

            _cleanupTimer.Change(
                _configuration.CleanupInterval,
                _configuration.CleanupInterval);

            _modelUpdateTimer.Change(
                _configuration.ModelUpdateInterval,
                _configuration.ModelUpdateInterval);

            _logger.LogDebug("Timers started successfully");
        }

        /// <summary>
        /// Timer'ları durdur;
        /// </summary>
        private void StopTimers()
        {
            _processingTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _modelUpdateTimer.Change(Timeout.Infinite, Timeout.Infinite);

            _logger.LogDebug("Timers stopped successfully");
        }

        /// <summary>
        /// Reactive stream'leri başlat;
        /// </summary>
        private void StartReactiveStreams()
        {
            // Gesture stream'i;
            _gestureSubject;
                .Buffer(TimeSpan.FromSeconds(_configuration.StreamBufferSeconds))
                .Where(buffer => buffer.Any())
                .Subscribe(async buffer =>
                {
                    try
                    {
                        await ProcessGestureBufferAsync(buffer);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing gesture buffer");
                    }
                }, _cancellationTokenSource.Token);

            // Recognition stream'i;
            _recognitionSubject;
                .Subscribe(result =>
                {
                    _eventBus.Publish(new GestureRecognizedEvent(result));
                }, _cancellationTokenSource.Token);

            // Training stream'i;
            _trainingSubject;
                .Subscribe(trainingEvent =>
                {
                    _eventBus.Publish(new GestureTrainingEvent(trainingEvent));
                }, _cancellationTokenSource.Token);

            _logger.LogDebug("Reactive streams started successfully");
        }

        /// <summary>
        /// Reactive stream'leri durdur;
        /// </summary>
        private void StopReactiveStreams()
        {
            _gestureSubject.OnCompleted();
            _recognitionSubject.OnCompleted();
            _trainingSubject.OnCompleted();

            _logger.LogDebug("Reactive streams stopped successfully");
        }

        /// <summary>
        /// Gesture buffer'ını işle;
        /// </summary>
        private async Task ProcessGestureBufferAsync(IList<GestureEvent> gestures)
        {
            if (!gestures.Any())
                return;

            try
            {
                // Batch analizi;
                var analysisResult = await _gestureAnalyzer.AnalyzeBatchAsync(gestures);

                // Sequence tanıma;
                var sequenceResult = await _sequenceRecognizer.RecognizeSequenceAsync(gestures);

                // EventBus'a gönder;
                _eventBus.Publish(new GestureBatchProcessedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    GestureCount = gestures.Count,
                    AnalysisResult = analysisResult,
                    SequenceResult = sequenceResult;
                });

                _logger.LogTrace($"Processed gesture buffer: {gestures.Count} gestures");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing gesture buffer");
            }
        }

        /// <summary>
        /// Video stream işlemeyi başlat;
        /// </summary>
        private async Task StartVideoStreamProcessingAsync(
            string streamUrl,
            GestureSession session,
            VideoStreamOptions options)
        {
            // Video stream işleme implementasyonu;
            // OpenCV veya FFmpeg kullanarak implemente edilebilir;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Gesture pattern'lerini yükle;
        /// </summary>
        private async Task LoadGesturePatternsAsync()
        {
            try
            {
                // Varsayılan gesture pattern'lerini yükle;
                var defaultPatterns = GesturePattern.GetDefaultPatterns();
                foreach (var pattern in defaultPatterns)
                {
                    _gesturePatterns[pattern.Id] = pattern;
                    await _patternMatcher.AddPatternAsync(pattern);
                }

                // Özel pattern'leri yükle;
                var customPatterns = await LoadCustomGesturePatternsAsync();
                foreach (var pattern in customPatterns)
                {
                    _gesturePatterns[pattern.Id] = pattern;
                    await _patternMatcher.AddPatternAsync(pattern);
                }

                _logger.LogInformation($"Loaded {_gesturePatterns.Count} gesture patterns");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load gesture patterns");
                throw;
            }
        }

        /// <summary>
        /// Özel gesture pattern'lerini yükle;
        /// </summary>
        private async Task<List<GesturePattern>> LoadCustomGesturePatternsAsync()
        {
            // Pattern'leri dosya sisteminden veya veritabanından yükle;
            await Task.CompletedTask;
            return new List<GesturePattern>();
        }

        /// <summary>
        /// Pattern'leri yeniden yükle;
        /// </summary>
        private async Task ReloadPatternsAsync()
        {
            try
            {
                // Pattern'leri temizle;
                _gesturePatterns.Clear();
                await _patternMatcher.ClearPatternsAsync();

                // Yeniden yükle;
                await LoadGesturePatternsAsync();

                _logger.LogDebug("Gesture patterns reloaded");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload gesture patterns");
            }
        }

        /// <summary>
        /// Kullanıcı profillerini yükle;
        /// </summary>
        private async Task LoadUserProfilesAsync()
        {
            try
            {
                // Kullanıcı profillerini yükle;
                await Task.CompletedTask;

                _logger.LogDebug("User gesture profiles loaded");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load user gesture profiles");
                throw;
            }
        }

        /// <summary>
        /// Kullanıcı gesture profilini yükle;
        /// </summary>
        private async Task<UserGestureProfile> LoadUserGestureProfileAsync(string userId)
        {
            // Kullanıcı profilini yükle;
            await Task.CompletedTask;
            return null;
        }

        /// <summary>
        /// Model'i pattern ile güncelle;
        /// </summary>
        private async Task UpdateModelWithPatternAsync(GesturePattern pattern, IEnumerable<byte[]> trainingFrames)
        {
            try
            {
                // Training verilerini hazırla;
                var trainingData = new GestureTrainingData;
                {
                    PatternId = pattern.Id,
                    PatternName = pattern.Name,
                    GestureType = pattern.GestureType,
                    TrainingFrames = trainingFrames.ToList(),
                    Timestamp = DateTime.UtcNow;
                };

                // Model'i güncelle;
                await _modelManager.UpdateModelAsync(trainingData);

                _logger.LogDebug($"Model updated with pattern: {pattern.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update model with pattern: {pattern.Name}");
                throw;
            }
        }

        /// <summary>
        /// Recognition sonucunu cache'e ekle;
        /// </summary>
        private async Task CacheRecognitionResultAsync(GestureRecognitionResult result)
        {
            try
            {
                await _gestureCache.AddResultAsync(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cache recognition result");
            }
        }

        /// <summary>
        /// Session metriklerini kaydet;
        /// </summary>
        private async Task SaveSessionMetricsAsync(GestureSession session)
        {
            try
            {
                // Session metriklerini kaydet;
                await Task.CompletedTask;

                _logger.LogTrace($"Session metrics saved: {session.SessionId}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to save session metrics: {session.SessionId}");
            }
        }

        /// <summary>
        /// Verileri kaydet;
        /// </summary>
        private async Task SaveDataAsync()
        {
            try
            {
                // Recognition sonuçlarını kaydet;
                await SaveRecognitionResultsAsync();

                // Kullanıcı profillerini kaydet;
                await SaveUserGestureProfilesAsync();

                // Pattern'leri kaydet;
                await SaveGesturePatternsAsync();

                _logger.LogDebug("Gesture data saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save gesture data");
            }
        }

        /// <summary>
        /// Recognition sonuçlarını kaydet;
        /// </summary>
        private async Task SaveRecognitionResultsAsync()
        {
            // Recognition sonuçlarını kaydet;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Kullanıcı gesture profillerini kaydet;
        /// </summary>
        private async Task SaveUserGestureProfilesAsync()
        {
            // Kullanıcı profillerini kaydet;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Gesture pattern'lerini kaydet;
        /// </summary>
        private async Task SaveGesturePatternsAsync()
        {
            // Pattern'leri kaydet;
            await Task.CompletedTask;
        }

        /// <summary>
        /// İşleme durumunu kontrol et;
        /// </summary>
        private void CheckProcessingStatus()
        {
            try
            {
                var metrics = GetMetricsAsync().GetAwaiter().GetResult();

                // Queue boyutu kontrolü;
                if (metrics.QueueSize > _configuration.MaxQueueSizeWarning)
                {
                    _logger.LogWarning($"Queue size is high: {metrics.QueueSize}");
                    _eventBus.Publish(new GestureSystemHealthWarningEvent;
                    {
                        Component = "GestureRecognizer",
                        Metric = "QueueSize",
                        Value = metrics.QueueSize,
                        Threshold = _configuration.MaxQueueSizeWarning,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // FPS kontrolü;
                if (metrics.FramesPerSecond < _configuration.MinFramesPerSecond)
                {
                    _logger.LogWarning($"Frame processing rate is low: {metrics.FramesPerSecond:F1} FPS");
                }

                // Bellek kullanımı kontrolü;
                if (metrics.MemoryUsageMB > _configuration.MaxMemoryUsageMB)
                {
                    _logger.LogWarning($"Memory usage is high: {metrics.MemoryUsageMB:F1} MB");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking processing status");
            }
        }

        /// <summary>
        /// Queue boyutunu optimize et;
        /// </summary>
        private void OptimizeQueue()
        {
            try
            {
                // Queue boyutu çok büyükse, eski frame'leri at;
                if (_frameQueue.Count > _configuration.MaxQueueSize)
                {
                    int removedCount = 0;
                    while (_frameQueue.Count > _configuration.MaxQueueSize / 2)
                    {
                        if (_frameQueue.TryDequeue(out _))
                        {
                            removedCount++;
                        }
                    }

                    if (removedCount > 0)
                    {
                        _logger.LogWarning($"Optimized queue: removed {removedCount} frames, new size: {_frameQueue.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing queue");
            }
        }

        /// <summary>
        /// Session timeout kontrolü;
        /// </summary>
        private void CheckSessionTimeouts()
        {
            var timeoutThreshold = DateTime.UtcNow.AddSeconds(-_configuration.SessionTimeoutSeconds);

            foreach (var session in _activeSessions.Values)
            {
                if (session.LastFrameTime < timeoutThreshold)
                {
                    _logger.LogWarning($"Session timeout: {session.SessionId}, Last frame: {session.LastFrameTime}");

                    // Session'ı sonlandır;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await EndSessionAsync(session.SessionId, GestureSessionEndReason.Timeout);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to end timed out session: {session.SessionId}");
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Eski sonuçları temizle;
        /// </summary>
        private void CleanupOldResults()
        {
            try
            {
                var retentionTime = DateTime.UtcNow.AddHours(-_configuration.ResultRetentionHours);
                var oldResults = _recognitionResults;
                    .Where(result => result.Timestamp < retentionTime)
                    .ToList();

                int removedCount = 0;
                foreach (var result in oldResults)
                {
                    if (_recognitionResults.TryTake(out _))
                    {
                        removedCount++;
                    }
                }

                if (removedCount > 0)
                {
                    _logger.LogDebug($"Cleaned up {removedCount} old recognition results");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old results");
            }
        }

        /// <summary>
        /// Bellek optimizasyonu;
        /// </summary>
        private void OptimizeMemoryUsage()
        {
            try
            {
                // GC çağır;
                if (MemoryUsageMB > _configuration.MemoryOptimizationThresholdMB)
                {
                    GC.Collect(2, GCCollectionMode.Optimized, false, true);
                    GC.WaitForPendingFinalizers();

                    _logger.LogDebug($"Memory optimized, current usage: {MemoryUsageMB:F1} MB");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing memory");
            }
        }

        /// <summary>
        /// Model güncellemelerini kontrol et;
        /// </summary>
        private void CheckModelUpdates()
        {
            try
            {
                // Model güncellemelerini kontrol et;
                var updateAvailable = _modelManager.CheckForUpdates();
                if (updateAvailable)
                {
                    _logger.LogInformation("Gesture model update available");
                    _eventBus.Publish(new GestureModelUpdateAvailableEvent());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking model updates");
            }
        }

        /// <summary>
        /// Pattern'leri yenile;
        /// </summary>
        private void RefreshPatterns()
        {
            try
            {
                // Pattern'leri yenile;
                Task.Run(async () =>
                {
                    try
                    {
                        await ReloadPatternsAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error refreshing patterns");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing patterns");
            }
        }

        /// <summary>
        /// Kullanıcı profillerini güncelle;
        /// </summary>
        private void UpdateUserProfiles()
        {
            try
            {
                // Kullanıcı profillerini güncelle;
                foreach (var profile in _userProfiles.Values)
                {
                    profile.LastUpdated = DateTime.UtcNow;
                }

                _logger.LogTrace("User profiles updated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user profiles");
            }
        }

        /// <summary>
        /// Bellek kullanımını getir;
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
        /// Konfigürasyonu güncelle;
        /// </summary>
        private void UpdateConfiguration()
        {
            try
            {
                // Bileşenleri yeniden yapılandır;
                _gestureClassifier.Configure(_configuration.ClassificationConfig);
                _gestureAnalyzer.Configure(_configuration.AnalysisConfig);
                _patternMatcher.Configure(_configuration.PatternConfig);
                _sequenceRecognizer.Configure(_configuration.SequenceConfig);
                _confidenceCalculator.Configure(_configuration.ConfidenceConfig);
                _gestureSmoother.Configure(_configuration.SmoothingConfig);
                _gestureValidator.Configure(_configuration.ValidationConfig);
                _gestureCache.Configure(_configuration.CacheConfig);
                _modelManager.Configure(_configuration.ModelConfig);

                // Detector'ları güncelle;
                foreach (var detector in _detectors)
                {
                    detector.Configure(_configuration);
                }

                // Timer'ları yeniden başlat;
                if (_isInitialized)
                {
                    StopTimers();
                    StartTimers();
                }

                _logger.LogInformation("Gesture recognizer configuration updated");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update configuration");
                throw new GestureRecognizerException("Failed to update configuration", ex);
            }
        }

        /// <summary>
        /// Kaynakları temizle;
        /// </summary>
        private async Task CleanupResourcesAsync()
        {
            try
            {
                // Detector'ları temizle;
                foreach (var detector in _detectors)
                {
                    if (detector is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }

                // Bileşenleri temizle;
                _gestureClassifier.Dispose();
                _gestureAnalyzer.Dispose();
                _patternMatcher.Dispose();
                _sequenceRecognizer.Dispose();
                _confidenceCalculator.Dispose();
                _gestureSmoother.Dispose();
                _gestureValidator.Dispose();
                _gestureCache.Dispose();
                _modelManager.Dispose();

                // ML kaynaklarını temizle;
                _inferenceSession?.Dispose();
                _dnnNet?.Dispose();

                // Concurrent koleksiyonları temizle;
                _activeSessions.Clear();
                _gesturePatterns.Clear();
                _userProfiles.Clear();
                while (_frameQueue.TryDequeue(out _)) { }
                _recognitionResults.Clear();
                _performanceMetrics.Clear();

                // Timer'ları temizle;
                _processingTimer.Dispose();
                _cleanupTimer.Dispose();
                _modelUpdateTimer.Dispose();

                // İptal token'ını temizle;
                _cancellationTokenSource.Dispose();
                _processingEvent.Dispose();

                _logger.LogDebug("Gesture recognizer resources cleaned up");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup resources");
            }
        }

        #endregion;

        #region Helper Methods (Spatial, Temporal, Motion Calculations)

        private List<double> CalculateFingerAngles(List<Point> landmarks)
        {
            // Parmak açılarını hesapla;
            return new List<double>();
        }

        private Vector3 CalculatePalmOrientation(List<Point> landmarks)
        {
            // Avuç içi yönelimini hesapla;
            return Vector3.Zero;
        }

        private Vector3 CalculateHandRotation(List<Point> landmarks)
        {
            // El rotasyonunu hesapla;
            return Vector3.Zero;
        }

        private HandShape CalculateHandShape(List<Point> landmarks)
        {
            // El şeklini hesapla;
            return HandShape.Unknown;
        }

        private double CalculateHandSize(Rect boundingBox)
        {
            // El boyutunu hesapla;
            return boundingBox.Width * boundingBox.Height;
        }

        private Dictionary<string, double> CalculateJointAngles(List<KeyPoint> keypoints)
        {
            // Eklem açılarını hesapla;
            return new Dictionary<string, double>();
        }

        private Vector3 CalculatePoseOrientation(List<KeyPoint> keypoints)
        {
            // Poz yönelimini hesapla;
            return Vector3.Zero;
        }

        private Vector3 CalculateBodyRotation(List<KeyPoint> keypoints)
        {
            // Vücut rotasyonunu hesapla;
            return Vector3.Zero;
        }

        private Dictionary<string, double> CalculateBodyProportions(List<KeyPoint> keypoints)
        {
            // Vücut oranlarını hesapla;
            return new Dictionary<string, double>();
        }

        private EyeFeatures CalculateEyeFeatures(List<Point> landmarks)
        {
            // Göz özelliklerini hesapla;
            return new EyeFeatures();
        }

        private MouthFeatures CalculateMouthFeatures(List<Point> landmarks)
        {
            // Ağız özelliklerini hesapla;
            return new MouthFeatures();
        }

        private EyebrowFeatures CalculateEyebrowFeatures(List<Point> landmarks)
        {
            // Kaş özelliklerini hesapla;
            return new EyebrowFeatures();
        }

        private HeadPose CalculateHeadPose(List<Point> landmarks)
        {
            // Baş pozunu hesapla;
            return new HeadPose();
        }

        private List<double> CalculateDistances(List<Vector2> positions)
        {
            // Mesafeleri hesapla;
            return new List<double>();
        }

        private Dictionary<string, double> CalculateRelativePositions(SpatialFeatures spatialFeatures)
        {
            // Relative pozisyonları hesapla;
            return new Dictionary<string, double>();
        }

        private List<MotionVector> CalculateMotionVectors(List<GestureFrame> previousFrames, GestureFeatures currentFeatures)
        {
            // Motion vektörlerini hesapla;
            return new List<MotionVector>();
        }

        private Dictionary<string, double> CalculateVelocity(List<MotionVector> motionVectors)
        {
            // Hızı hesapla;
            return new Dictionary<string, double>();
        }

        private Dictionary<string, double> CalculateAcceleration(Dictionary<string, double> velocity)
        {
            // İvmeyi hesapla;
            return new Dictionary<string, double>();
        }

        private Dictionary<string, double> CalculateJerk(Dictionary<string, double> acceleration)
        {
            // Jerk'i hesapla;
            return new Dictionary<string, double>();
        }

        private double CalculatePeriodicity(List<MotionVector> motionVectors)
        {
            // Periyodikliği hesapla;
            return 0;
        }

        private HandMotion CalculateHandMotion(List<HandFeatures> handFeatures)
        {
            // El hareketini hesapla;
            return new HandMotion();
        }

        private BodyMotion CalculateBodyMotion(BodyFeatures bodyFeatures)
        {
            // Vücut hareketini hesapla;
            return new BodyMotion();
        }

        private FacialMotion CalculateFacialMotion(FaceFeatures faceFeatures)
        {
            // Yüz hareketini hesapla;
            return new FacialMotion();
        }

        private OverallMotion CalculateOverallMotion(MotionFeatures motionFeatures)
        {
            // Genel hareketi hesapla;
            return new OverallMotion();
        }

        #endregion;

        #region Event Triggers;

        /// <summary>
        /// Gesture tespit edildi event'ini tetikle;
        /// </summary>
        protected virtual void OnGestureDetected(GestureEvent gestureEvent)
        {
            GestureDetected?.Invoke(this, new GestureDetectedEventArgs(gestureEvent));
            _eventBus.Publish(new GestureDetectedEvent(gestureEvent));
        }

        /// <summary>
        /// Gesture tanındı event'ini tetikle;
        /// </summary>
        protected virtual void OnGestureRecognized(GestureRecognitionResult result)
        {
            GestureRecognized?.Invoke(this, new GestureRecognizedEventArgs(result));
        }

        /// <summary>
        /// El tespit edildi event'ini tetikle;
        /// </summary>
        protected virtual void OnHandDetected(DetectionResult result)
        {
            HandDetected?.Invoke(this, new HandDetectedEventArgs(result));
            _eventBus.Publish(new HandDetectedEvent(result));
        }

        /// <summary>
        /// Vücut tespit edildi event'ini tetikle;
        /// </summary>
        protected virtual void OnBodyDetected(DetectionResult result)
        {
            BodyDetected?.Invoke(this, new BodyDetectedEventArgs(result));
            _eventBus.Publish(new BodyDetectedEvent(result));
        }

        /// <summary>
        /// Gesture sequence tamamlandı event'ini tetikle;
        /// </summary>
        protected virtual void OnGestureSequenceCompleted(SequenceRecognitionResult result)
        {
            GestureSequenceCompleted?.Invoke(this, new GestureSequenceCompletedEventArgs(result));
            _eventBus.Publish(new GestureSequenceCompletedEvent(result));
        }

        /// <summary>
        /// Session başladı event'ini tetikle;
        /// </summary>
        protected virtual void OnSessionStarted(GestureSession session)
        {
            SessionStarted?.Invoke(this, new GestureSessionStartedEventArgs(session));
            _eventBus.Publish(new GestureSessionStartedEvent(session));
        }

        /// <summary>
        /// Session sonlandı event'ini tetikle;
        /// </summary>
        protected virtual void OnSessionEnded(GestureSessionResult result)
        {
            SessionEnded?.Invoke(this, new GestureSessionEndedEventArgs(result));
            _eventBus.Publish(new GestureSessionEndedEvent(result));
        }

        /// <summary>
        /// Model eğitildi event'ini tetikle;
        /// </summary>
        protected virtual void OnModelTrained(GestureTrainingResult result)
        {
            ModelTrained?.Invoke(this, new GestureModelTrainedEventArgs(result));
            _eventBus.Publish(new GestureModelTrainedEvent(result));
        }

        /// <summary>
        /// Durum değişti event'ini tetikle;
        /// </summary>
        protected virtual void OnStateChanged()
        {
            StateChanged?.Invoke(this, new GestureRecognizerStateChangedEventArgs(_state));
            _eventBus.Publish(new GestureRecognizerStateChangedEvent(_state));
        }

        /// <summary>
        /// Performans metrikleri güncellendi event'ini tetikle;
        /// </summary>
        protected virtual void OnPerformanceMetricsUpdated()
        {
            PerformanceMetricsUpdated?.Invoke(this, new GesturePerformanceMetricsUpdatedEventArgs(_performanceMetrics.ToList()));
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        /// <summary>
        /// Dispose pattern implementation;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Managed kaynakları temizle;
                    if (_isInitialized)
                    {
                        StopAsync().GetAwaiter().GetResult();
                    }

                    _cancellationTokenSource?.Dispose();
                    _processingEvent?.Dispose();

                    _processingTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _modelUpdateTimer?.Dispose();

                    _gestureSubject?.Dispose();
                    _recognitionSubject?.Dispose();
                    _trainingSubject?.Dispose();

                    _inferenceSession?.Dispose();
                    _dnnNet?.Dispose();

                    foreach (var detector in _detectors)
                    {
                        if (detector is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    _gestureClassifier?.Dispose();
                    _gestureAnalyzer?.Dispose();
                    _patternMatcher?.Dispose();
                    _sequenceRecognizer?.Dispose();
                    _confidenceCalculator?.Dispose();
                    _gestureSmoother?.Dispose();
                    _gestureValidator?.Dispose();
                    _gestureCache?.Dispose();
                    _modelManager?.Dispose();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Dispose;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~GestureRecognizer()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Jest tanıyıcı durumu;
    /// </summary>
    public enum GestureRecognizerState;
    {
        Stopped,
        Initializing,
        Running,
        Training,
        Stopping,
        Error;
    }

    /// <summary>
    /// Frame tipi;
    /// </summary>
    public enum FrameType;
    {
        RGB,
        Depth,
        Infrared,
        Grayscale,
        MultiModal;
    }

    /// <summary>
    /// Gesture tipi;
    /// </summary>
    public enum GestureType;
    {
        Unknown,
        HandWave,
        ThumbsUp,
        ThumbsDown,
        OK,
        Peace,
        Rock,
        Point,
        Grab,
        Pinch,
        SwipeLeft,
        SwipeRight,
        SwipeUp,
        SwipeDown,
        ZoomIn,
        ZoomOut,
        Rotate,
        Shake,
        Clap,
        Salute,
        Custom;
    }

    /// <summary>
    /// Session tipi;
    /// </summary>
    public enum SessionType;
    {
        Default,
        VideoStream,
        RealTime,
        Batch,
        Training,
        Testing;
    }

    /// <summary>
    /// Gesture session durumu;
    /// </summary>
    public enum GestureSessionStatus;
    {
        Active,
        Ended,
        Timeout,
        Error;
    }

    /// <summary>
    /// Gesture session sonlandırma nedeni;
    /// </summary>
    public enum GestureSessionEndReason;
    {
        Completed,
        Timeout,
        UserRequest,
        Error,
        RecognizerStopped;
    }

    /// <summary>
    /// Detection tipi;
    /// </summary>
    public enum DetectionType;
    {
        Unknown,
        Hand,
        Body,
        Face,
        Object,
        Keypoint;
    }

    /// <summary>
    /// Pose tipi;
    /// </summary>
    public enum PoseType;
    {
        Unknown,
        Standing,
        Sitting,
        Walking,
        Running,
        Jumping,
        Lying,
        Custom;
    }

    /// <summary>
    /// Yüz ifadesi;
    /// </summary>
    public enum FacialExpression;
    {
        Neutral,
        Happy,
        Sad,
        Angry,
        Surprised,
        Fearful,
        Disgusted,
        Contempt;
    }

    /// <summary>
    /// El şekli;
    /// </summary>
    public enum HandShape;
    {
        Unknown,
        Open,
        Closed,
        Pointing,
        Pinching,
        Grabbing,
        LShape,
        OKShape,
        PeaceShape,
        ThumbsUp,
        ThumbsDown;
    }

    /// <summary>
    /// İşleme durumu;
    /// </summary>
    public enum ProcessingStatus;
    {
        Pending,
        Queued,
        Processing,
        Completed,
        Failed;
    }

    /// <summary>
    /// Stream durumu;
    /// </summary>
    public enum StreamStatus;
    {
        Starting,
        Running,
        Paused,
        Stopped,
        Error;
    }

    /// <summary>
    /// Gesture frame;
    /// </summary>
    public class GestureFrame;
    {
        public string FrameId { get; set; } = Guid.NewGuid().ToString();
        public string SessionId { get; set; }
        public FrameType FrameType { get; set; }
        public DateTime Timestamp { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Channels { get; set; }
        public byte[] Data { get; set; }
        public byte[] ProcessedData { get; set; }
        public FrameFeatures Features { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Frame özellikleri;
    /// </summary>
    public class FrameFeatures;
    {
        public DateTime Timestamp { get; set; }
        public float[] Histogram { get; set; }
        public double EdgeDensity { get; set; }
        public int KeypointCount { get; set; }
        public double Brightness { get; set; }
        public double Contrast { get; set; }
        public double Sharpness { get; set; }
        public Dictionary<string, object> AdditionalFeatures { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Gesture özellikleri;
    /// </summary>
    public class GestureFeatures;
    {
        public string FrameId { get; set; }
        public DateTime Timestamp { get; set; }
        public FrameFeatures FrameFeatures { get; set; }
        public List<HandFeatures> HandFeatures { get; set; } = new List<HandFeatures>();
        public BodyFeatures BodyFeatures { get; set; }
        public FaceFeatures FaceFeatures { get; set; }
        public List<KeypointFeatures> KeypointFeatures { get; set; } = new List<KeypointFeatures>();
        public SpatialFeatures SpatialFeatures { get; set; }
        public TemporalFeatures TemporalFeatures { get; set; }
        public MotionFeatures MotionFeatures { get; set; }
    }

    /// <summary>
    /// El özellikleri;
    /// </summary>
    public class HandFeatures;
    {
        public string HandId { get; set; }
        public double Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public List<Point> Landmarks { get; set; }
        public bool IsLeftHand { get; set; }
        public GestureType GestureType { get; set; }
        public List<double> FingerAngles { get; set; } = new List<double>();
        public Vector3 PalmOrientation { get; set; }
        public Vector3 HandRotation { get; set; }
        public HandShape HandShape { get; set; }
        public double HandSize { get; set; }
        public Dictionary<string, object> AdditionalFeatures { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Vücut özellikleri;
    /// </summary>
    public class BodyFeatures;
    {
        public string BodyId { get; set; }
        public double Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public List<KeyPoint> Keypoints { get; set; }
        public PoseType PoseType { get; set; }
        public Dictionary<string, double> JointAngles { get; set; } = new Dictionary<string, double>();
        public Vector3 PoseOrientation { get; set; }
        public Vector3 BodyRotation { get; set; }
        public Dictionary<string, double> BodyProportions { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, object> AdditionalFeatures { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Yüz özellikleri;
    /// </summary>
    public class FaceFeatures;
    {
        public string FaceId { get; set; }
        public double Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public List<Point> Landmarks { get; set; }
        public FacialExpression Expression { get; set; }
        public EyeFeatures EyeFeatures { get; set; }
        public MouthFeatures MouthFeatures { get; set; }
        public EyebrowFeatures EyebrowFeatures { get; set; }
        public HeadPose HeadPose { get; set; }
        public Dictionary<string, object> AdditionalFeatures { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Keypoint özellikleri;
    /// </summary>
    public class KeypointFeatures;
    {
        public string Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Confidence { get; set; }
        public string Type { get; set; }
    }

    /// <summary>
    /// Spatial özellikler;
    /// </summary>
    public class SpatialFeatures;
    {
        public List<Vector2> HandPositions { get; set; } = new List<Vector2>();
        public List<double> HandDistances { get; set; } = new List<double>();
        public Vector2 BodyPosition { get; set; }
        public Vector2 FacePosition { get; set; }
        public Dictionary<string, double> RelativePositions { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Temporal özellikler;
    /// </summary>
    public class TemporalFeatures;
    {
        public List<MotionVector> MotionVectors { get; set; } = new List<MotionVector>();
        public Dictionary<string, double> Velocity { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> Acceleration { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> Jerk { get; set; } = new Dictionary<string, double>();
        public double Periodicity { get; set; }
    }

    /// <summary>
    /// Motion özellikleri;
    /// </summary>
    public class MotionFeatures;
    {
        public HandMotion HandMotion { get; set; }
        public BodyMotion BodyMotion { get; set; }
        public FacialMotion FacialMotion { get; set; }
        public OverallMotion OverallMotion { get; set; }
    }

    /// <summary>
    /// Motion vektörü;
    /// </summary>
    public class MotionVector;
    {
        public string SourceId { get; set; }
        public Vector2 StartPoint { get; set; }
        public Vector2 EndPoint { get; set; }
        public double Magnitude { get; set; }
        public double Direction { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    /// <summary>
    /// El hareketi;
    /// </summary>
    public class HandMotion;
    {
        public double Speed { get; set; }
        public double Acceleration { get; set; }
        public double Jerk { get; set; }
        public double Smoothness { get; set; }
        public List<Vector2> Trajectory { get; set; } = new List<Vector2>();
    }

    /// <summary>
    /// Vücut hareketi;
    /// </summary>
    public class BodyMotion;
    {
        public double Speed { get; set; }
        public double Acceleration { get; set; }
        public double Stability { get; set; }
        public Dictionary<string, double> JointMotions { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Yüz hareketi;
    /// </summary>
    public class FacialMotion;
    {
        public Dictionary<string, double> ExpressionChanges { get; set; } = new Dictionary<string, double>();
        public double HeadRotationSpeed { get; set; }
        public double EyeMovementSpeed { get; set; }
    }

    /// <summary>
    /// Genel hareket;
    /// </summary>
    public class OverallMotion;
    {
        public double OverallSpeed { get; set; }
        public double OverallAcceleration { get; set; }
        public double MotionComplexity { get; set; }
        public double MotionFluidity { get; set; }
    }

    /// <summary>
    /// Göz özellikleri;
    /// </summary>
    public class EyeFeatures;
    {
        public double EyeAspectRatio { get; set; }
        public double PupilSize { get; set; }
        public double GazeDirectionX { get; set; }
        public double GazeDirectionY { get; set; }
        public bool IsBlinking { get; set; }
    }

    /// <summary>
    /// Ağız özellikleri;
    /// </summary>
    public class MouthFeatures;
    {
        public double MouthAspectRatio { get; set; }
        public double LipDistance { get; set; }
        public double SmileIntensity { get; set; }
    }

    /// <summary>
    /// Kaş özellikleri;
    /// </summary>
    public class EyebrowFeatures;
    {
        public double EyebrowHeight { get; set; }
        public double EyebrowAngle { get; set; }
    }

    /// <summary>
    /// Baş poz'u;
    /// </summary>
    public class HeadPose;
    {
        public double Pitch { get; set; }
        public double Yaw { get; set; }
        public double Roll { get; set; }
    }

    /// <summary>
    /// KeyPoint;
    /// </summary>
    public class KeyPoint;
    {
        public string Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Confidence { get; set; }
        public string Type { get; set; }
    }

    /// <summary>
    /// Detection sonucu;
    /// </summary>
    public class DetectionResult;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DetectionType DetectionType { get; set; }
        public double Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public List<Point> Landmarks { get; set; }
        public List<KeyPoint> Keypoints { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Gesture tanıma sonucu;
    /// </summary>
    public class GestureRecognitionResult;
    {
        public string FrameId { get; set; }
        public string SessionId { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsRecognized { get; set; }
        public GestureType GestureType { get; set; }
        public double Confidence { get; set; }
        public GestureFeatures Features { get; set; }
        public List<DetectionResult> DetectionResults { get; set; }
        public ClassificationResult ClassificationResult { get; set; }
        public PatternMatchResult PatternResult { get; set; }
        public SequenceRecognitionResult SequenceResult { get; set; }
        public ConfidenceResult ConfidenceResult { get; set; }
        public SmoothingResult SmoothingResult { get; set; }
        public ValidationResult ValidationResult { get; set; }
        public ProcessingStatus ProcessingStatus { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Sınıflandırma sonucu;
    /// </summary>
    public class ClassificationResult;
    {
        public GestureType GestureType { get; set; }
        public double Confidence { get; set; }
        public List<GestureProbability> Probabilities { get; set; } = new List<GestureProbability>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Gesture olasılığı;
    /// </summary>
    public class GestureProbability;
    {
        public GestureType GestureType { get; set; }
        public double Probability { get; set; }
    }

    /// <summary>
    /// Pattern eşleştirme sonucu;
    /// </summary>
    public class PatternMatchResult;
    {
        public bool IsMatched { get; set; }
        public string PatternId { get; set; }
        public string PatternName { get; set; }
        public double MatchScore { get; set; }
        public List<GestureFrame> MatchedFrames { get; set; } = new List<GestureFrame>();
    }

    /// <summary>
    /// Sequence tanıma sonucu;
    /// </summary>
    public class SequenceRecognitionResult;
    {
        public bool IsSequenceCompleted { get; set; }
        public string SequenceId { get; set; }
        public string SequenceName { get; set; }
        public double SequenceConfidence { get; set; }
        public List<GestureRecognitionResult> SequenceGestures { get; set; } = new List<GestureRecognitionResult>();
    }

    /// <summary>
    /// Güven sonucu;
    /// </summary>
    public class ConfidenceResult;
    {
        public double OverallConfidence { get; set; }
        public Dictionary<string, double> ComponentConfidences { get; set; } = new Dictionary<string, double>();
        public double Uncertainty { get; set; }
    }

    /// <summary>
    /// Smoothing sonucu;
    /// </summary>
    public class SmoothingResult;
    {
        public GestureType GestureType { get; set; }
        public double Confidence { get; set; }
        public List<double> ConfidenceHistory { get; set; } = new List<double>();
        public double Smoothness { get; set; }
    }

    /// <summary>
    /// Validasyon sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
        public double ValidationScore { get; set; }
    }

    /// <summary>
    /// Gesture event'i;
    /// </summary>
    public class GestureEvent;
    {
        public string FrameId { get; set; }
        public string SessionId { get; set; }
        public GestureType GestureType { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
        public GestureFeatures Features { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Gesture session'ı;
    /// </summary>
    public class GestureSession;
    {
        public string SessionId { get; }
        public SessionType SessionType { get; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastFrameTime { get; set; }
        public DateTime LastGestureTime { get; set; }
        public GestureSessionStatus Status { get; set; }
        public GestureSessionEndReason? EndReason { get; set; }
        public GestureSessionContext Context { get; set; }
        public GestureSessionConfiguration Configuration { get; set; }
        public GestureSessionMetrics Metrics { get; } = new GestureSessionMetrics();
        public int TotalFrames { get; set; }
        public int TotalGestures { get; set; }
        public ConcurrentQueue<GestureRecognitionResult> RecentGestures { get; } = new ConcurrentQueue<GestureRecognitionResult>();

        public GestureSession(string sessionId, SessionType sessionType, GestureSessionContext context)
        {
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            SessionType = sessionType;
            Context = context ?? throw new ArgumentNullException(nameof(context));
        }
    }

    /// <summary>
    /// Gesture session metrikleri;
    /// </summary>
    public class GestureSessionMetrics;
    {
        public int TotalFrames { get; set; }
        public int TotalGestures { get; set; }
        public int TotalHandsDetected { get; set; }
        public int TotalBodiesDetected { get; set; }
        public int TotalFacesDetected { get; set; }
        public double FramesPerSecond { get; set; }
        public double RecognitionRate { get; set; }
        public double AverageConfidence { get; set; }
        public double MinConfidence { get; set; } = double.MaxValue;
        public double MaxConfidence { get; set; } = double.MinValue;
        public Dictionary<GestureType, int> GestureTypeCounts { get; set; } = new Dictionary<GestureType, int>();
        public Dictionary<string, double> CustomMetrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Kullanıcı gesture profili;
    /// </summary>
    public class UserGestureProfile;
    {
        public string UserId { get; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime LastActivity { get; set; }
        public int TotalSessions { get; set; }
        public int TotalGestures { get; set; }
        public Dictionary<GestureType, int> GestureCounts { get; set; } = new Dictionary<GestureType, int>();
        public Dictionary<string, GesturePattern> CommonPatterns { get; set; } = new Dictionary<string, GesturePattern>();
        public UserGestureProfileConfiguration Configuration { get; set; }
        public Dictionary<string, object> Preferences { get; set; } = new Dictionary<string, object>();

        public UserGestureProfile(string userId)
        {
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        }
    }

    /// <summary>
    /// Gesture pattern'i;
    /// </summary>
    public class GesturePattern;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Description { get; set; }
        public GestureType GestureType { get; set; }
        public List<GestureFrame> Sequence { get; set; } = new List<GestureFrame>();
        public Dictionary<string, object> Conditions { get; set; } = new Dictionary<string, object>();
        public double Threshold { get; set; } = 0.7;
        public int Priority { get; set; } = 1;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public bool IsActive { get; set; } = true;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public static List<GesturePattern> GetDefaultPatterns()
        {
            return new List<GesturePattern>
            {
                new GesturePattern;
                {
                    Name = "HandWave",
                    Description = "El sallama hareketi",
                    GestureType = GestureType.HandWave,
                    Conditions = new Dictionary<string, object>
                    {
                        ["MinHandMovement"] = 50,
                        ["DurationSeconds"] = 1;
                    },
                    Priority = 2;
                },
                new GesturePattern;
                {
                    Name = "ThumbsUp",
                    Description = "Başparmak yukarı",
                    GestureType = GestureType.ThumbsUp,
                    Conditions = new Dictionary<string, object>
                    {
                        ["ThumbAngle"] = 45,
                        ["ConfidenceThreshold"] = 0.8;
                    },
                    Priority = 3;
                }
            };
        }
    }

    /// <summary>
    /// Gesture tanıyıcı metrikleri;
    /// </summary>
    public class GestureRecognizerMetrics;
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Uptime { get; set; }
        public GestureRecognizerState State { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsProcessing { get; set; }
        public bool IsModelLoaded { get; set; }
        public int ActiveSessionCount { get; set; }
        public int PatternCount { get; set; }
        public long TotalFramesProcessed { get; set; }
        public long TotalGesturesRecognized { get; set; }
        public long TotalHandsDetected { get; set; }
        public long TotalBodiesDetected { get; set; }
        public int QueueSize { get; set; }
        public double FramesPerSecond { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double MemoryUsageMB { get; set; }
        public bool IsUsingGPU { get; set; }
        public ModelInfo ModelInfo { get; set; }
        public GestureRecognizerConfiguration Configuration { get; set; }
        public List<DetectorMetrics> DetectorMetrics { get; set; } = new List<DetectorMetrics>();
        public List<GestureSessionMetrics> SessionMetrics { get; set; } = new List<GestureSessionMetrics>();
        public List<GesturePerformanceMetric> PerformanceMetrics { get; set; } = new List<GesturePerformanceMetric>();
        public string Error { get; set; }
    }

    /// <summary>
    /// Gesture performans metriği;
    /// </summary>
    public class GesturePerformanceMetric;
    {
        public DateTime Timestamp { get; set; }
        public string OperationName { get; set; }
        public long DurationMs { get; set; }
        public int FramesProcessed { get; set; }
        public double FramesPerSecond { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public double MemoryUsageMB { get; set; }
        public int QueueSize { get; set; }
        public int ActiveSessions { get; set; }
        public Dictionary<string, object> CustomMetrics { get; set; } = new Dictionary<string, object>();
    }

    #endregion;
}
