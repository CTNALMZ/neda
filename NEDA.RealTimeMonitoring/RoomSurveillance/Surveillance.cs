using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Logging;
using NEDA.Services.EventBus;
using NEDA.Monitoring.MetricsCollector;
using NEDA.AI.ComputerVision;
using NEDA.SecurityModules.Monitoring;
using NEDA.RealTimeMonitoring.PresenceAnalysis;
using NEDA.Biometrics.FaceRecognition;

namespace NEDA.RealTimeMonitoring.RoomSurveillance;
{
    /// <summary>
    /// Gözetim sistemi ana arayüzü.
    /// </summary>
    public interface ISurveillanceSystem : IDisposable
    {
        /// <summary>
        /// Gözetim sistemini başlatır.
        /// </summary>
        /// <param name="config">Gözetim konfigürasyonu.</param>
        Task StartSurveillanceAsync(SurveillanceConfig config);

        /// <summary>
        /// Gözetim sistemini durdurur.
        /// </summary>
        Task StopSurveillanceAsync();

        /// <summary>
        /// Yeni bir kamera ekler.
        /// </summary>
        /// <param name="camera">Kamera bilgileri.</param>
        Task AddCameraAsync(SurveillanceCamera camera);

        /// <summary>
        /// Kamerayı kaldırır.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        Task RemoveCameraAsync(string cameraId);

        /// <summary>
        /// Kameranın görüntüsünü getirir.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <param name="quality">Görüntü kalitesi.</param>
        Task<CameraFeed> GetCameraFeedAsync(string cameraId, VideoQuality quality = VideoQuality.Medium);

        /// <summary>
        /// Kayıt başlatır.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <param name="recordingConfig">Kayıt konfigürasyonu.</param>
        Task StartRecordingAsync(string cameraId, RecordingConfig recordingConfig = null);

        /// <summary>
        /// Kayıt durdurur.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <returns>Kayıt bilgileri.</returns>
        Task<RecordingInfo> StopRecordingAsync(string cameraId);

        /// <summary>
        /// Hareket tespiti başlatır.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <param name="motionConfig">Hareket konfigürasyonu.</param>
        Task StartMotionDetectionAsync(string cameraId, MotionDetectionConfig motionConfig = null);

        /// <summary>
        /// Hareket tespiti durdurur.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        Task StopMotionDetectionAsync(string cameraId);

        /// <summary>
        /// Yüz tanıma başlatır.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <param name="faceConfig">Yüz tanıma konfigürasyonu.</param>
        Task StartFaceRecognitionAsync(string cameraId, FaceRecognitionConfig faceConfig = null);

        /// <summary>
        /// Yüz tanıma durdurur.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        Task StopFaceRecognitionAsync(string cameraId);

        /// <summary>
        /// Alarm durumunu ayarlar.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <param name="alarmEnabled">Alarm durumu.</param>
        Task SetAlarmStatusAsync(string cameraId, bool alarmEnabled);

        /// <summary>
        /// Kameradan görüntü yakalar.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <param name="captureConfig">Yakalama konfigürasyonu.</param>
        Task<CapturedImage> CaptureImageAsync(string cameraId, CaptureConfig captureConfig = null);

        /// <summary>
        /// Kayıtları arar.
        /// </summary>
        /// <param name="searchCriteria">Arama kriterleri.</param>
        Task<List<RecordingInfo>> SearchRecordingsAsync(RecordingSearchCriteria searchCriteria);

        /// <summary>
        /// Sistem durumunu getirir.
        /// </summary>
        SurveillanceStatus GetStatus();

        /// <summary>
        /// İstatistikleri getirir.
        /// </summary>
        /// <param name="timeRange">Zaman aralığı.</param>
        Task<SurveillanceStatistics> GetStatisticsAsync(TimeRange timeRange);

        /// <summary>
        /// Canlı görüntü akışı başlatır.
        /// </summary>
        /// <param name="cameraId">Kamera ID.</param>
        /// <param name="streamConfig">Akış konfigürasyonu.</param>
        Task<VideoStream> StartLiveStreamAsync(string cameraId, StreamConfig streamConfig);

        /// <summary>
        /// Canlı görüntü akışı durdurur.
        /// </summary>
        /// <param name="streamId">Akış ID.</param>
        Task StopLiveStreamAsync(Guid streamId);

        /// <summary>
        /// Gözetim olaylarını izlemeye başlar.
        /// </summary>
        /// <param name="observer">Gözlemci.</param>
        IDisposable Subscribe(IObserver<SurveillanceEvent> observer);
    }

    /// <summary>
    /// Gözetim sistemi ana sınıfı.
    /// </summary>
    public class SurveillanceSystem : ISurveillanceSystem;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly IVisionEngine _visionEngine;
        private readonly IFaceDetector _faceDetector;
        private readonly IThreatAnalyzer _threatAnalyzer;
        private readonly IPresenceDetector _presenceDetector;

        private readonly CameraManager _cameraManager;
        private readonly RecordingManager _recordingManager;
        private readonly MotionDetector _motionDetector;
        private readonly FaceRecognitionEngine _faceRecognition;
        private readonly AlarmSystem _alarmSystem;
        private readonly AnalyticsEngine _analyticsEngine;
        private readonly StreamManager _streamManager;

        private readonly ConcurrentDictionary<string, CameraSession> _cameraSessions;
        private readonly ConcurrentDictionary<Guid, VideoStream> _activeStreams;
        private readonly ConcurrentBag<IObserver<SurveillanceEvent>> _observers;
        private readonly SemaphoreSlim _processingLock;
        private readonly CancellationTokenSource _shutdownTokenSource;
        private readonly Timer _healthCheckTimer;
        private readonly Timer _cleanupTimer;

        private SurveillanceConfig _config;
        private bool _disposed;
        private bool _isRunning;
        private DateTime _startTime;

        /// <summary>
        /// Gözetim sistemi oluşturur.
        /// </summary>
        public SurveillanceSystem(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IEventBus eventBus,
            IVisionEngine visionEngine,
            IFaceDetector faceDetector,
            IThreatAnalyzer threatAnalyzer,
            IPresenceDetector presenceDetector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _visionEngine = visionEngine ?? throw new ArgumentNullException(nameof(visionEngine));
            _faceDetector = faceDetector ?? throw new ArgumentNullException(nameof(faceDetector));
            _threatAnalyzer = threatAnalyzer ?? throw new ArgumentNullException(nameof(threatAnalyzer));
            _presenceDetector = presenceDetector ?? throw new ArgumentNullException(nameof(presenceDetector));

            _cameraManager = new CameraManager();
            _recordingManager = new RecordingManager();
            _motionDetector = new MotionDetector();
            _faceRecognition = new FaceRecognitionEngine();
            _alarmSystem = new AlarmSystem();
            _analyticsEngine = new AnalyticsEngine();
            _streamManager = new StreamManager();

            _cameraSessions = new ConcurrentDictionary<string, CameraSession>();
            _activeStreams = new ConcurrentDictionary<Guid, VideoStream>();
            _observers = new ConcurrentBag<IObserver<SurveillanceEvent>>();
            _processingLock = new SemaphoreSlim(1, 1);
            _shutdownTokenSource = new CancellationTokenSource();

            // Sağlık kontrol timer'ı: her 1 dakikada bir;
            _healthCheckTimer = new Timer(PerformHealthCheck, null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

            // Temizlik timer'ı: her saat başı;
            _cleanupTimer = new Timer(CleanupOldData, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            _eventBus.Subscribe<MotionDetectedEvent>(HandleMotionDetection);
            _eventBus.Subscribe<FaceRecognizedEvent>(HandleFaceRecognition);
            _eventBus.Subscribe<IntrusionDetectedEvent>(HandleIntrusionDetection);

            _logger.LogInformation("SurveillanceSystem initialized");
        }

        /// <summary>
        /// Gözetim sistemini başlatır.
        /// </summary>
        public async Task StartSurveillanceAsync(SurveillanceConfig config)
        {
            if (_isRunning) return;

            await _processingLock.WaitAsync();
            try
            {
                _config = config ?? throw new ArgumentNullException(nameof(config));

                // Sistem bileşenlerini başlat;
                await InitializeSystemComponentsAsync(config);

                // Kameraları başlat;
                await InitializeCamerasAsync(config.Cameras);

                // Analiz motorlarını başlat;
                await StartAnalyticsEnginesAsync(config);

                _isRunning = true;
                _startTime = DateTime.UtcNow;

                _logger.LogInformation($"Surveillance system started with {config.Cameras.Count} cameras");

                var @event = new SurveillanceStartedEvent;
                {
                    StartTime = _startTime,
                    CameraCount = config.Cameras.Count,
                    Features = config.Features,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.SystemStarted,
                    CameraId = "SYSTEM",
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["cameraCount"] = config.Cameras.Count,
                        ["features"] = config.Features;
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start surveillance system");
                throw;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Gözetim sistemini durdurur.
        /// </summary>
        public async Task StopSurveillanceAsync()
        {
            if (!_isRunning) return;

            await _processingLock.WaitAsync();
            try
            {
                // Tüm kamera oturumlarını durdur;
                foreach (var session in _cameraSessions.Values)
                {
                    await StopCameraSessionAsync(session);
                }
                _cameraSessions.Clear();

                // Tüm akışları durdur;
                foreach (var stream in _activeStreams.Values)
                {
                    await _streamManager.StopStreamAsync(stream.Id);
                }
                _activeStreams.Clear();

                // Analiz motorlarını durdur;
                await StopAnalyticsEnginesAsync();

                _isRunning = false;

                _logger.LogInformation("Surveillance system stopped");

                var @event = new SurveillanceStoppedEvent;
                {
                    StopTime = DateTime.UtcNow,
                    Uptime = DateTime.UtcNow - _startTime,
                    TotalRecordings = await _recordingManager.GetTotalRecordingsCountAsync(),
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.SystemStopped,
                    CameraId = "SYSTEM",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Yeni bir kamera ekler.
        /// </summary>
        public async Task AddCameraAsync(SurveillanceCamera camera)
        {
            ValidateCamera(camera);

            await _processingLock.WaitAsync();
            try
            {
                // Kamerayı yöneticiye ekle;
                await _cameraManager.AddCameraAsync(camera);

                // Kamera oturumu oluştur;
                var session = new CameraSession;
                {
                    CameraId = camera.Id,
                    Camera = camera,
                    Status = CameraStatus.Connected,
                    CreatedAt = DateTime.UtcNow,
                    LastActivity = DateTime.UtcNow;
                };

                _cameraSessions[camera.Id] = session;

                // Varsayılan özellikleri başlat;
                if (_config?.Features.Contains(SurveillanceFeature.MotionDetection) == true)
                {
                    await StartMotionDetectionAsync(camera.Id);
                }

                _logger.LogInformation($"Camera added: {camera.Name} ({camera.Id})");

                var @event = new CameraAddedEvent;
                {
                    CameraId = camera.Id,
                    CameraName = camera.Name,
                    CameraType = camera.Type,
                    Location = camera.Location,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.CameraAdded,
                    CameraId = camera.Id,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["cameraName"] = camera.Name,
                        ["cameraType"] = camera.Type;
                    }
                });
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Kamerayı kaldırır.
        /// </summary>
        public async Task RemoveCameraAsync(string cameraId)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            await _processingLock.WaitAsync();
            try
            {
                if (!_cameraSessions.TryRemove(cameraId, out var session))
                {
                    throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
                }

                // Kamera oturumunu durdur;
                await StopCameraSessionAsync(session);

                // Kamerayı yöneticiden kaldır;
                await _cameraManager.RemoveCameraAsync(cameraId);

                _logger.LogInformation($"Camera removed: {cameraId}");

                var @event = new CameraRemovedEvent;
                {
                    CameraId = cameraId,
                    CameraName = session.Camera.Name,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.CameraRemoved,
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Kameranın görüntüsünü getirir.
        /// </summary>
        public async Task<CameraFeed> GetCameraFeedAsync(string cameraId, VideoQuality quality = VideoQuality.Medium)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            try
            {
                var feed = await _cameraManager.GetCameraFeedAsync(cameraId, quality);

                // Oturum aktivitesini güncelle;
                session.LastActivity = DateTime.UtcNow;

                _metricsCollector.RecordMetric("surveillance.feed.requested", 1);

                return feed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get camera feed for {cameraId}");
                throw;
            }
        }

        /// <summary>
        /// Kayıt başlatır.
        /// </summary>
        public async Task StartRecordingAsync(string cameraId, RecordingConfig recordingConfig = null)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                // Kayıt zaten başlamışsa;
                if (session.IsRecording)
                {
                    _logger.LogWarning($"Recording already active for camera {cameraId}");
                    return;
                }

                var config = recordingConfig ?? new RecordingConfig;
                {
                    Quality = VideoQuality.High,
                    Duration = TimeSpan.FromHours(1),
                    MotionTriggered = false,
                    StoragePath = _config?.StorageSettings?.RecordingPath;
                };

                // Kayıt başlat;
                var recording = await _recordingManager.StartRecordingAsync(cameraId, config);

                // Oturumu güncelle;
                session.IsRecording = true;
                session.CurrentRecording = recording;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Recording started for camera {cameraId}");

                var @event = new RecordingStartedEvent;
                {
                    CameraId = cameraId,
                    RecordingId = recording.Id,
                    StartTime = recording.StartTime,
                    Config = config,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.RecordingStarted,
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["recordingId"] = recording.Id,
                        ["startTime"] = recording.StartTime;
                    }
                });
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Kayıt durdurur.
        /// </summary>
        public async Task<RecordingInfo> StopRecordingAsync(string cameraId)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                if (!session.IsRecording || session.CurrentRecording == null)
                {
                    throw new InvalidOperationException($"No active recording for camera {cameraId}");
                }

                // Kayıt durdur;
                var recording = await _recordingManager.StopRecordingAsync(session.CurrentRecording.Id);

                // Oturumu güncelle;
                session.IsRecording = false;
                session.CurrentRecording = null;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Recording stopped for camera {cameraId}, duration: {recording.Duration}");

                var @event = new RecordingStoppedEvent;
                {
                    CameraId = cameraId,
                    RecordingId = recording.Id,
                    Duration = recording.Duration,
                    FileSize = recording.FileSize,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.RecordingStopped,
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow,
                    Data = new Dictionary<string, object>
                    {
                        ["recordingId"] = recording.Id,
                        ["duration"] = recording.Duration,
                        ["fileSize"] = recording.FileSize;
                    }
                });

                return recording;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Hareket tespiti başlatır.
        /// </summary>
        public async Task StartMotionDetectionAsync(string cameraId, MotionDetectionConfig motionConfig = null)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                if (session.IsMotionDetectionActive)
                {
                    _logger.LogWarning($"Motion detection already active for camera {cameraId}");
                    return;
                }

                var config = motionConfig ?? new MotionDetectionConfig;
                {
                    Sensitivity = 0.7,
                    MinimumArea = 100,
                    EnableAlerts = true,
                    CooldownPeriod = TimeSpan.FromSeconds(10)
                };

                // Hareket tespiti başlat;
                await _motionDetector.StartDetectionAsync(cameraId, config);

                // Oturumu güncelle;
                session.IsMotionDetectionActive = true;
                session.MotionDetectionConfig = config;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Motion detection started for camera {cameraId}");

                var @event = new MotionDetectionStartedEvent;
                {
                    CameraId = cameraId,
                    Config = config,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Hareket tespiti durdurur.
        /// </summary>
        public async Task StopMotionDetectionAsync(string cameraId)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                if (!session.IsMotionDetectionActive)
                {
                    _logger.LogWarning($"Motion detection not active for camera {cameraId}");
                    return;
                }

                // Hareket tespiti durdur;
                await _motionDetector.StopDetectionAsync(cameraId);

                // Oturumu güncelle;
                session.IsMotionDetectionActive = false;
                session.MotionDetectionConfig = null;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Motion detection stopped for camera {cameraId}");

                var @event = new MotionDetectionStoppedEvent;
                {
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Yüz tanıma başlatır.
        /// </summary>
        public async Task StartFaceRecognitionAsync(string cameraId, FaceRecognitionConfig faceConfig = null)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                if (session.IsFaceRecognitionActive)
                {
                    _logger.LogWarning($"Face recognition already active for camera {cameraId}");
                    return;
                }

                var config = faceConfig ?? new FaceRecognitionConfig;
                {
                    ConfidenceThreshold = 0.8,
                    EnableIdentification = true,
                    EnableDatabaseLookup = true,
                    AlertOnUnknown = true;
                };

                // Yüz tanıma başlat;
                await _faceRecognition.StartRecognitionAsync(cameraId, config);

                // Oturumu güncelle;
                session.IsFaceRecognitionActive = true;
                session.FaceRecognitionConfig = config;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Face recognition started for camera {cameraId}");

                var @event = new FaceRecognitionStartedEvent;
                {
                    CameraId = cameraId,
                    Config = config,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Yüz tanıma durdurur.
        /// </summary>
        public async Task StopFaceRecognitionAsync(string cameraId)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                if (!session.IsFaceRecognitionActive)
                {
                    _logger.LogWarning($"Face recognition not active for camera {cameraId}");
                    return;
                }

                // Yüz tanıma durdur;
                await _faceRecognition.StopRecognitionAsync(cameraId);

                // Oturumu güncelle;
                session.IsFaceRecognitionActive = false;
                session.FaceRecognitionConfig = null;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Face recognition stopped for camera {cameraId}");

                var @event = new FaceRecognitionStoppedEvent;
                {
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Alarm durumunu ayarlar.
        /// </summary>
        public async Task SetAlarmStatusAsync(string cameraId, bool alarmEnabled)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                if (session.AlarmEnabled == alarmEnabled)
                {
                    return; // Durum değişmedi;
                }

                // Alarm durumunu ayarla;
                if (alarmEnabled)
                {
                    await _alarmSystem.EnableAlarmAsync(cameraId);
                }
                else;
                {
                    await _alarmSystem.DisableAlarmAsync(cameraId);
                }

                // Oturumu güncelle;
                session.AlarmEnabled = alarmEnabled;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Alarm {(alarmEnabled ? "enabled" : "disabled")} for camera {cameraId}");

                var @event = new AlarmStatusChangedEvent;
                {
                    CameraId = cameraId,
                    AlarmEnabled = alarmEnabled,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = alarmEnabled ? SurveillanceEventType.AlarmEnabled : SurveillanceEventType.AlarmDisabled,
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Kameradan görüntü yakalar.
        /// </summary>
        public async Task<CapturedImage> CaptureImageAsync(string cameraId, CaptureConfig captureConfig = null)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            try
            {
                var config = captureConfig ?? new CaptureConfig;
                {
                    Quality = ImageQuality.High,
                    Format = ImageFormat.Jpeg,
                    IncludeMetadata = true;
                };

                // Görüntü yakala;
                var image = await _cameraManager.CaptureImageAsync(cameraId, config);

                // Oturum aktivitesini güncelle;
                session.LastActivity = DateTime.UtcNow;

                _metricsCollector.RecordMetric("surveillance.image.captured", 1);

                return image;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to capture image from camera {cameraId}");
                throw;
            }
        }

        /// <summary>
        /// Kayıtları arar.
        /// </summary>
        public async Task<List<RecordingInfo>> SearchRecordingsAsync(RecordingSearchCriteria searchCriteria)
        {
            if (searchCriteria == null)
                throw new ArgumentNullException(nameof(searchCriteria));

            return await _recordingManager.SearchRecordingsAsync(searchCriteria);
        }

        /// <summary>
        /// Sistem durumunu getirir.
        /// </summary>
        public SurveillanceStatus GetStatus()
        {
            return new SurveillanceStatus;
            {
                IsRunning = _isRunning,
                StartTime = _startTime,
                Uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                CameraCount = _cameraSessions.Count,
                ActiveRecordings = _cameraSessions.Values.Count(s => s.IsRecording),
                ActiveStreams = _activeStreams.Count,
                MotionDetectionActive = _cameraSessions.Values.Count(s => s.IsMotionDetectionActive),
                FaceRecognitionActive = _cameraSessions.Values.Count(s => s.IsFaceRecognitionActive),
                StorageUsage = _recordingManager.GetStorageUsage(),
                LastActivity = _cameraSessions.Values.Max(s => s.LastActivity) ?? DateTime.UtcNow;
            };
        }

        /// <summary>
        /// İstatistikleri getirir.
        /// </summary>
        public async Task<SurveillanceStatistics> GetStatisticsAsync(TimeRange timeRange)
        {
            var statistics = new SurveillanceStatistics;
            {
                TimeRange = timeRange,
                TotalRecordings = await _recordingManager.GetTotalRecordingsCountAsync(),
                TotalMotionEvents = await _motionDetector.GetMotionEventCountAsync(timeRange),
                TotalFaceRecognitions = await _faceRecognition.GetRecognitionCountAsync(timeRange),
                TotalAlarms = await _alarmSystem.GetAlarmCountAsync(timeRange),
                StorageUsage = _recordingManager.GetStorageUsage(),
                CameraStatistics = await GetCameraStatisticsAsync(timeRange),
                ActivityHeatmap = await GenerateActivityHeatmapAsync(timeRange)
            };

            return statistics;
        }

        /// <summary>
        /// Canlı görüntü akışı başlatır.
        /// </summary>
        public async Task<VideoStream> StartLiveStreamAsync(string cameraId, StreamConfig streamConfig)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(cameraId));

            if (!_cameraSessions.TryGetValue(cameraId, out var session))
            {
                throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
            }

            await _processingLock.WaitAsync();
            try
            {
                // Akış başlat;
                var stream = await _streamManager.StartStreamAsync(cameraId, streamConfig);

                // Akışı kaydet;
                _activeStreams[stream.Id] = stream;

                // Oturumu güncelle;
                session.LastActivity = DateTime.UtcNow;

                _logger.LogInformation($"Live stream started for camera {cameraId}, stream ID: {stream.Id}");

                var @event = new LiveStreamStartedEvent;
                {
                    CameraId = cameraId,
                    StreamId = stream.Id,
                    StreamConfig = streamConfig,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                return stream;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Canlı görüntü akışı durdurur.
        /// </summary>
        public async Task StopLiveStreamAsync(Guid streamId)
        {
            await _processingLock.WaitAsync();
            try
            {
                if (!_activeStreams.TryRemove(streamId, out var stream))
                {
                    throw new ArgumentException($"Stream {streamId} not found", nameof(streamId));
                }

                // Akış durdur;
                await _streamManager.StopStreamAsync(streamId);

                _logger.LogInformation($"Live stream stopped: {streamId}");

                var @event = new LiveStreamStoppedEvent;
                {
                    CameraId = stream.CameraId,
                    StreamId = streamId,
                    Duration = DateTime.UtcNow - stream.StartTime,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Gözetim olaylarını izlemeye başlar.
        /// </summary>
        public IDisposable Subscribe(IObserver<SurveillanceEvent> observer)
        {
            _observers.Add(observer);
            return new Unsubscriber<SurveillanceEvent>(_observers, observer);
        }

        /// <summary>
        /// Dispose pattern implementasyonu.
        /// </summary>
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
                    _shutdownTokenSource.Cancel();
                    StopSurveillanceAsync().Wait(TimeSpan.FromSeconds(5));

                    _healthCheckTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _processingLock?.Dispose();
                    _shutdownTokenSource?.Dispose();

                    if (_eventBus != null)
                    {
                        _eventBus.Unsubscribe<MotionDetectedEvent>(HandleMotionDetection);
                        _eventBus.Unsubscribe<FaceRecognizedEvent>(HandleFaceRecognition);
                        _eventBus.Unsubscribe<IntrusionDetectedEvent>(HandleIntrusionDetection);
                    }

                    // Tüm gözlemcileri tamamla;
                    foreach (var observer in _observers)
                    {
                        observer.OnCompleted();
                    }
                    _observers.Clear();

                    _cameraManager.Dispose();
                    _recordingManager.Dispose();
                    _motionDetector.Dispose();
                    _faceRecognition.Dispose();
                    _alarmSystem.Dispose();
                    _streamManager.Dispose();
                }

                _disposed = true;
            }
        }

        #region Private Methods;

        private async Task InitializeSystemComponentsAsync(SurveillanceConfig config)
        {
            // Görüntü işleme motorunu başlat;
            await _visionEngine.InitializeAsync(config.VisionConfig);

            // Yüz tanıma motorunu başlat;
            await _faceDetector.InitializeAsync();

            // Kayıt yöneticisini başlat;
            await _recordingManager.InitializeAsync(config.StorageSettings);

            _logger.LogDebug("System components initialized");
        }

        private async Task InitializeCamerasAsync(List<SurveillanceCamera> cameras)
        {
            foreach (var camera in cameras)
            {
                try
                {
                    await AddCameraAsync(camera);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to initialize camera {camera.Name}");
                }
            }
        }

        private async Task StartAnalyticsEnginesAsync(SurveillanceConfig config)
        {
            // Analiz motorlarını yapılandıra göre başlat;
            if (config.Features.Contains(SurveillanceFeature.MotionDetection))
            {
                foreach (var camera in config.Cameras)
                {
                    await StartMotionDetectionAsync(camera.Id);
                }
            }

            if (config.Features.Contains(SurveillanceFeature.FaceRecognition))
            {
                foreach (var camera in config.Cameras)
                {
                    await StartFaceRecognitionAsync(camera.Id);
                }
            }

            if (config.Features.Contains(SurveillanceFeature.PresenceDetection))
            {
                // Varlık tespiti için alanları yapılandır;
                var presenceConfig = new PresenceDetectionConfig;
                {
                    Areas = config.Cameras.Select(c => new MonitoringArea;
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Type = AreaType.Room,
                        Sensors = new List<SensorInfo>
                        {
                            new SensorInfo;
                            {
                                Id = c.Id,
                                Type = SensorType.Camera,
                                Name = c.Name;
                            }
                        }
                    }).ToList(),
                    DetectionMethods = new List<DetectionMethod> { DetectionMethod.VideoAnalysis },
                    DetectionInterval = TimeSpan.FromSeconds(2)
                };

                await _presenceDetector.StartDetectionAsync(presenceConfig);
            }
        }

        private async Task StopAnalyticsEnginesAsync()
        {
            // Tüm analiz motorlarını durdur;
            foreach (var session in _cameraSessions.Values)
            {
                if (session.IsMotionDetectionActive)
                {
                    await StopMotionDetectionAsync(session.CameraId);
                }

                if (session.IsFaceRecognitionActive)
                {
                    await StopFaceRecognitionAsync(session.CameraId);
                }

                if (session.AlarmEnabled)
                {
                    await SetAlarmStatusAsync(session.CameraId, false);
                }
            }

            // Varlık tespitini durdur;
            await _presenceDetector.StopDetectionAsync();
        }

        private async Task StopCameraSessionAsync(CameraSession session)
        {
            try
            {
                // Kayıt varsa durdur;
                if (session.IsRecording)
                {
                    await StopRecordingAsync(session.CameraId);
                }

                // Hareket tespiti aktifse durdur;
                if (session.IsMotionDetectionActive)
                {
                    await StopMotionDetectionAsync(session.CameraId);
                }

                // Yüz tanıma aktifse durdur;
                if (session.IsFaceRecognitionActive)
                {
                    await StopFaceRecognitionAsync(session.CameraId);
                }

                // Alarm aktifse kapat;
                if (session.AlarmEnabled)
                {
                    await SetAlarmStatusAsync(session.CameraId, false);
                }

                _logger.LogInformation($"Camera session stopped: {session.CameraId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping camera session {session.CameraId}");
            }
        }

        private void ValidateCamera(SurveillanceCamera camera)
        {
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            if (string.IsNullOrWhiteSpace(camera.Id))
                throw new ArgumentException("Camera ID cannot be null or empty", nameof(camera.Id));

            if (string.IsNullOrWhiteSpace(camera.Name))
                throw new ArgumentException("Camera name cannot be null or empty", nameof(camera.Name));

            if (string.IsNullOrWhiteSpace(camera.StreamUrl))
                throw new ArgumentException("Stream URL cannot be null or empty", nameof(camera.StreamUrl));
        }

        private async Task<List<CameraStatistics>> GetCameraStatisticsAsync(TimeRange timeRange)
        {
            var statistics = new List<CameraStatistics>();

            foreach (var session in _cameraSessions.Values)
            {
                var camStats = new CameraStatistics;
                {
                    CameraId = session.CameraId,
                    CameraName = session.Camera.Name,
                    Uptime = DateTime.UtcNow - session.CreatedAt,
                    RecordingCount = await _recordingManager.GetCameraRecordingCountAsync(session.CameraId, timeRange),
                    MotionEvents = await _motionDetector.GetCameraMotionEventsAsync(session.CameraId, timeRange),
                    RecognizedFaces = await _faceRecognition.GetCameraRecognitionsAsync(session.CameraId, timeRange)
                };

                statistics.Add(camStats);
            }

            return statistics;
        }

        private async Task<ActivityHeatmap> GenerateActivityHeatmapAsync(TimeRange timeRange)
        {
            var heatmap = new ActivityHeatmap;
            {
                TimeRange = timeRange,
                HourlyActivity = new Dictionary<int, double>()
            };

            // Saatlik aktivite verilerini topla;
            for (int hour = 0; hour < 24; hour++)
            {
                var hourStart = DateTime.UtcNow.Date.AddHours(hour);
                var hourEnd = hourStart.AddHours(1);

                var hourRange = new TimeRange(hourStart, hourEnd);
                var motionCount = await _motionDetector.GetMotionEventCountAsync(hourRange);

                heatmap.HourlyActivity[hour] = motionCount;
            }

            return heatmap;
        }

        private void PerformHealthCheck(object state)
        {
            try
            {
                Task.Run(async () =>
                {
                    foreach (var session in _cameraSessions.Values)
                    {
                        await CheckCameraHealthAsync(session);
                    }

                    // Sistem sağlık kontrolü event'i yayınla;
                    var healthStatus = new SystemHealthStatus;
                    {
                        Timestamp = DateTime.UtcNow,
                        CameraCount = _cameraSessions.Count,
                        HealthyCameras = _cameraSessions.Values.Count(s => s.Status == CameraStatus.Connected),
                        StorageHealth = _recordingManager.GetStorageHealth(),
                        AverageLatency = await CalculateAverageLatencyAsync()
                    };

                    var @event = new HealthCheckEvent;
                    {
                        HealthStatus = healthStatus,
                        Timestamp = DateTime.UtcNow;
                    };

                    await _eventBus.PublishAsync(@event);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
            }
        }

        private async Task CheckCameraHealthAsync(CameraSession session)
        {
            try
            {
                // Kamera bağlantısını kontrol et;
                var isConnected = await _cameraManager.CheckCameraConnectionAsync(session.CameraId);

                var previousStatus = session.Status;
                session.Status = isConnected ? CameraStatus.Connected : CameraStatus.Disconnected;

                if (previousStatus != session.Status)
                {
                    _logger.LogWarning($"Camera {session.CameraId} status changed: {previousStatus} -> {session.Status}");

                    var @event = new CameraStatusChangedEvent;
                    {
                        CameraId = session.CameraId,
                        PreviousStatus = previousStatus,
                        NewStatus = session.Status,
                        Timestamp = DateTime.UtcNow;
                    };

                    await _eventBus.PublishAsync(@event);

                    // Gözlemcilere bildir;
                    NotifyObservers(new SurveillanceEvent;
                    {
                        Type = SurveillanceEventType.CameraStatusChanged,
                        CameraId = session.CameraId,
                        Timestamp = DateTime.UtcNow,
                        Data = new Dictionary<string, object>
                        {
                            ["previousStatus"] = previousStatus,
                            ["newStatus"] = session.Status;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking camera health for {session.CameraId}");
                session.Status = CameraStatus.Error;
            }
        }

        private async Task<double> CalculateAverageLatencyAsync()
        {
            double totalLatency = 0;
            int cameraCount = 0;

            foreach (var session in _cameraSessions.Values)
            {
                try
                {
                    var latency = await _cameraManager.GetCameraLatencyAsync(session.CameraId);
                    totalLatency += latency;
                    cameraCount++;
                }
                catch
                {
                    // Kamera erişilemezse görmezden gel;
                }
            }

            return cameraCount > 0 ? totalLatency / cameraCount : 0;
        }

        private void CleanupOldData(object state)
        {
            try
            {
                // Eski kayıtları temizle;
                _recordingManager.CleanupOldRecordings(_config?.StorageSettings?.RetentionDays ?? 30);

                // Eski görüntüleri temizle;
                _cameraManager.CleanupOldCaptures(TimeSpan.FromDays(7));

                _logger.LogDebug("Old data cleanup completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old data");
            }
        }

        private void NotifyObservers(SurveillanceEvent @event)
        {
            foreach (var observer in _observers)
            {
                try
                {
                    observer.OnNext(@event);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying observer");
                }
            }
        }

        private async void HandleMotionDetection(MotionDetectedEvent @event)
        {
            try
            {
                if (!_cameraSessions.TryGetValue(@event.CameraId, out var session))
                    return;

                // Hareket tespiti olayını işle;
                session.LastMotionTime = DateTime.UtcNow;
                session.MotionEventCount++;

                _logger.LogDebug($"Motion detected on camera {@event.CameraId}, intensity: {@event.Intensity}");

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.MotionDetected,
                    CameraId = @event.CameraId,
                    Timestamp = @event.Timestamp,
                    Data = new Dictionary<string, object>
                    {
                        ["intensity"] = @event.Intensity,
                        ["confidence"] = @event.Confidence;
                    }
                });

                // Alarm aktifse tetikle;
                if (session.AlarmEnabled)
                {
                    await TriggerAlarmOnMotionAsync(@event.CameraId, @event);
                }

                // Hareket tetiklemeli kayıt başlat;
                if (_config?.RecordingSettings?.MotionTriggeredRecording == true && !session.IsRecording)
                {
                    await StartMotionTriggeredRecordingAsync(@event.CameraId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling motion detection");
            }
        }

        private async void HandleFaceRecognition(FaceRecognizedEvent @event)
        {
            try
            {
                if (!_cameraSessions.TryGetValue(@event.CameraId, out var session))
                    return;

                _logger.LogDebug($"Face recognized on camera {@event.CameraId}, confidence: {@event.Confidence}");

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.FaceRecognized,
                    CameraId = @event.CameraId,
                    Timestamp = @event.Timestamp,
                    Data = new Dictionary<string, object>
                    {
                        ["faceId"] = @event.FaceId,
                        ["confidence"] = @event.Confidence,
                        ["personName"] = @event.PersonName;
                    }
                });

                // Bilinmeyen yüz alarmı;
                if (@event.IsUnknown && session.AlarmEnabled)
                {
                    await TriggerUnknownFaceAlarmAsync(@event.CameraId, @event);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling face recognition");
            }
        }

        private async void HandleIntrusionDetection(IntrusionDetectedEvent @event)
        {
            try
            {
                // İzinsiz giriş tespiti;
                _logger.LogWarning($"Intrusion detected: {@event.Description}");

                // Gözlemcilere bildir;
                NotifyObservers(new SurveillanceEvent;
                {
                    Type = SurveillanceEventType.IntrusionDetected,
                    CameraId = @event.CameraId,
                    Timestamp = @event.Timestamp,
                    Data = new Dictionary<string, object>
                    {
                        ["description"] = @event.Description,
                        ["severity"] = @event.Severity;
                    }
                });

                // Alarm tetikle;
                await _alarmSystem.TriggerAlarmAsync(@event.CameraId, "Intrusion detected");

                // Güvenlik olayı olarak kaydet;
                var securityEvent = new SecurityEvent;
                {
                    Id = Guid.NewGuid(),
                    EventType = "IntrusionDetection",
                    Timestamp = @event.Timestamp,
                    SourceIp = @event.CameraId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["description"] = @event.Description,
                        ["severity"] = @event.Severity;
                    }
                };

                await _threatAnalyzer.AnalyzeSecurityEventAsync(securityEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling intrusion detection");
            }
        }

        private async Task TriggerAlarmOnMotionAsync(string cameraId, MotionDetectedEvent motionEvent)
        {
            try
            {
                // Hassasiyete göre alarm tetikle;
                if (motionEvent.Intensity > _config?.AlarmSettings?.MotionSensitivity ?? 0.7)
                {
                    await _alarmSystem.TriggerAlarmAsync(cameraId, "Motion detected");

                    var @event = new AlarmTriggeredEvent;
                    {
                        CameraId = cameraId,
                        Reason = "Motion detected",
                        Intensity = motionEvent.Intensity,
                        Timestamp = DateTime.UtcNow;
                    };

                    await _eventBus.PublishAsync(@event);

                    _logger.LogWarning($"Alarm triggered on camera {cameraId} due to motion");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error triggering alarm on motion for camera {cameraId}");
            }
        }

        private async Task TriggerUnknownFaceAlarmAsync(string cameraId, FaceRecognizedEvent faceEvent)
        {
            try
            {
                await _alarmSystem.TriggerAlarmAsync(cameraId, "Unknown face detected");

                var @event = new AlarmTriggeredEvent;
                {
                    CameraId = cameraId,
                    Reason = "Unknown face detected",
                    Confidence = faceEvent.Confidence,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _logger.LogWarning($"Alarm triggered on camera {cameraId} due to unknown face");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error triggering unknown face alarm for camera {cameraId}");
            }
        }

        private async Task StartMotionTriggeredRecordingAsync(string cameraId)
        {
            try
            {
                var recordingConfig = new RecordingConfig;
                {
                    Quality = _config?.RecordingSettings?.Quality ?? VideoQuality.Medium,
                    Duration = TimeSpan.FromMinutes(_config?.RecordingSettings?.MotionRecordingDuration ?? 5),
                    MotionTriggered = true,
                    StoragePath = _config?.StorageSettings?.RecordingPath;
                };

                await StartRecordingAsync(cameraId, recordingConfig);

                _logger.LogInformation($"Motion-triggered recording started for camera {cameraId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error starting motion-triggered recording for camera {cameraId}");
            }
        }

        #endregion;

        #region Internal Supporting Classes;

        /// <summary>
        /// Kamera yöneticisi.
        /// </summary>
        internal class CameraManager : IDisposable
        {
            private readonly ConcurrentDictionary<string, SurveillanceCamera> _cameras;

            public CameraManager()
            {
                _cameras = new ConcurrentDictionary<string, SurveillanceCamera>();
            }

            public async Task AddCameraAsync(SurveillanceCamera camera)
            {
                if (!_cameras.TryAdd(camera.Id, camera))
                {
                    throw new InvalidOperationException($"Camera {camera.Id} already exists");
                }

                // Kamera bağlantısını test et;
                var isConnected = await TestCameraConnectionAsync(camera);
                if (!isConnected)
                {
                    _cameras.TryRemove(camera.Id, out _);
                    throw new InvalidOperationException($"Camera {camera.Id} connection test failed");
                }
            }

            public async Task RemoveCameraAsync(string cameraId)
            {
                if (!_cameras.TryRemove(cameraId, out _))
                {
                    throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
                }
            }

            public async Task<CameraFeed> GetCameraFeedAsync(string cameraId, VideoQuality quality)
            {
                if (!_cameras.TryGetValue(cameraId, out var camera))
                {
                    throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
                }

                // Kamera görüntüsünü al;
                var feed = new CameraFeed;
                {
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow,
                    Quality = quality,
                    FrameData = await GetFrameDataAsync(camera, quality)
                };

                return feed;
            }

            public async Task<CapturedImage> CaptureImageAsync(string cameraId, CaptureConfig config)
            {
                if (!_cameras.TryGetValue(cameraId, out var camera))
                {
                    throw new ArgumentException($"Camera {cameraId} not found", nameof(cameraId));
                }

                // Görüntü yakala;
                var image = new CapturedImage;
                {
                    Id = Guid.NewGuid(),
                    CameraId = cameraId,
                    Timestamp = DateTime.UtcNow,
                    Format = config.Format,
                    Quality = config.Quality,
                    ImageData = await CaptureFrameAsync(camera, config)
                };

                return image;
            }

            public async Task<bool> CheckCameraConnectionAsync(string cameraId)
            {
                if (!_cameras.TryGetValue(cameraId, out var camera))
                    return false;

                return await TestCameraConnectionAsync(camera);
            }

            public async Task<double> GetCameraLatencyAsync(string cameraId)
            {
                // Kamera gecikmesini ölç;
                await Task.Delay(10); // Simüle edilmiş işlem;
                return 0.15; // 150ms;
            }

            public void CleanupOldCaptures(TimeSpan retentionPeriod)
            {
                // Eski yakalamaları temizle;
            }

            private async Task<bool> TestCameraConnectionAsync(SurveillanceCamera camera)
            {
                // Kamera bağlantı testi;
                await Task.Delay(50); // Simüle edilmiş işlem;
                return true; // Başarılı;
            }

            private async Task<byte[]> GetFrameDataAsync(SurveillanceCamera camera, VideoQuality quality)
            {
                // Kamera görüntüsünü al;
                await Task.Delay(20); // Simüle edilmiş işlem;
                return new byte[1024]; // Örnek veri;
            }

            private async Task<byte[]> CaptureFrameAsync(SurveillanceCamera camera, CaptureConfig config)
            {
                // Görüntü yakala;
                await Task.Delay(30); // Simüle edilmiş işlem;
                return new byte[2048]; // Örnek veri;
            }

            public void Dispose()
            {
                _cameras.Clear();
            }
        }

        /// <summary>
        /// Kayıt yöneticisi.
        /// </summary>
        internal class RecordingManager : IDisposable
        {
            private readonly ConcurrentDictionary<Guid, RecordingSession> _recordings;

            public RecordingManager()
            {
                _recordings = new ConcurrentDictionary<Guid, RecordingSession>();
            }

            public async Task InitializeAsync(StorageSettings storageSettings)
            {
                // Kayıt depolamayı başlat;
                await Task.Delay(100);
            }

            public async Task<RecordingInfo> StartRecordingAsync(string cameraId, RecordingConfig config)
            {
                var recording = new RecordingInfo;
                {
                    Id = Guid.NewGuid(),
                    CameraId = cameraId,
                    StartTime = DateTime.UtcNow,
                    Config = config,
                    Status = RecordingStatus.Recording;
                };

                var session = new RecordingSession;
                {
                    Recording = recording,
                    CancellationTokenSource = new CancellationTokenSource()
                };

                if (!_recordings.TryAdd(recording.Id, session))
                {
                    throw new InvalidOperationException($"Recording {recording.Id} already exists");
                }

                // Kayıt task'ını başlat;
                _ = Task.Run(() => RecordAsync(session), session.CancellationTokenSource.Token);

                return recording;
            }

            public async Task<RecordingInfo> StopRecordingAsync(Guid recordingId)
            {
                if (!_recordings.TryRemove(recordingId, out var session))
                {
                    throw new ArgumentException($"Recording {recordingId} not found", nameof(recordingId));
                }

                // Kayıt durdur;
                session.CancellationTokenSource.Cancel();
                session.Recording.EndTime = DateTime.UtcNow;
                session.Recording.Duration = session.Recording.EndTime - session.Recording.StartTime;
                session.Recording.Status = RecordingStatus.Completed;
                session.Recording.FileSize = CalculateFileSize(session.Recording);

                // Kaydı kaydet;
                await SaveRecordingAsync(session.Recording);

                return session.Recording;
            }

            public async Task<List<RecordingInfo>> SearchRecordingsAsync(RecordingSearchCriteria criteria)
            {
                // Kayıtları ara;
                await Task.Delay(50); // Simüle edilmiş işlem;
                return new List<RecordingInfo>();
            }

            public async Task<int> GetTotalRecordingsCountAsync()
            {
                return _recordings.Count;
            }

            public async Task<int> GetCameraRecordingCountAsync(string cameraId, TimeRange timeRange)
            {
                // Kameraya göre kayıt sayısını getir;
                await Task.Delay(10); // Simüle edilmiş işlem;
                return 0;
            }

            public StorageUsage GetStorageUsage()
            {
                return new StorageUsage;
                {
                    TotalSpace = 1000000000, // 1GB;
                    UsedSpace = 250000000,   // 250MB;
                    AvailableSpace = 750000000 // 750MB;
                };
            }

            public StorageHealth GetStorageHealth()
            {
                var usage = GetStorageUsage();
                return new StorageHealth;
                {
                    UsagePercentage = (double)usage.UsedSpace / usage.TotalSpace,
                    IsHealthy = usage.AvailableSpace > usage.TotalSpace * 0.1 // %10'dan fazla boş alan;
                };
            }

            public void CleanupOldRecordings(int retentionDays)
            {
                // Eski kayıtları temizle;
            }

            private async Task RecordAsync(RecordingSession session)
            {
                var cancellationToken = session.CancellationTokenSource.Token;

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        // Kayıt yap;
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Kayıt durduruldu;
                }
                catch (Exception ex)
                {
                    session.Recording.Status = RecordingStatus.Error;
                    session.Recording.ErrorMessage = ex.Message;
                }
            }

            private long CalculateFileSize(RecordingInfo recording)
            {
                // Dosya boyutunu hesapla;
                return (long)(recording.Duration.TotalMinutes * 1024 * 1024); // 1MB/dakika;
            }

            private async Task SaveRecordingAsync(RecordingInfo recording)
            {
                // Kaydı kaydet;
                await Task.Delay(50);
            }

            public void Dispose()
            {
                foreach (var session in _recordings.Values)
                {
                    session.CancellationTokenSource.Cancel();
                    session.CancellationTokenSource.Dispose();
                }
                _recordings.Clear();
            }
        }

        /// <summary>
        /// Hareket dedektörü.
        /// </summary>
        internal class MotionDetector : IDisposable
        {
            private readonly ConcurrentDictionary<string, MotionDetectionSession> _sessions;

            public MotionDetector()
            {
                _sessions = new ConcurrentDictionary<string, MotionDetectionSession>();
            }

            public async Task StartDetectionAsync(string cameraId, MotionDetectionConfig config)
            {
                var session = new MotionDetectionSession;
                {
                    CameraId = cameraId,
                    Config = config,
                    CancellationTokenSource = new CancellationTokenSource(),
                    IsActive = true;
                };

                if (!_sessions.TryAdd(cameraId, session))
                {
                    throw new InvalidOperationException($"Motion detection already active for camera {cameraId}");
                }

                // Tespit task'ını başlat;
                _ = Task.Run(() => DetectMotionAsync(session), session.CancellationTokenSource.Token);
            }

            public async Task StopDetectionAsync(string cameraId)
            {
                if (!_sessions.TryRemove(cameraId, out var session))
                {
                    throw new ArgumentException($"Motion detection not active for camera {cameraId}", nameof(cameraId));
                }

                session.CancellationTokenSource.Cancel();
                session.CancellationTokenSource.Dispose();
            }

            public async Task<int> GetMotionEventCountAsync(TimeRange timeRange)
            {
                // Hareket olay sayısını getir;
                await Task.Delay(10); // Simüle edilmiş işlem;
                return 0;
            }

            public async Task<List<MotionEvent>> GetCameraMotionEventsAsync(string cameraId, TimeRange timeRange)
            {
                // Kameraya göre hareket olaylarını getir;
                await Task.Delay(10); // Simüle edilmiş işlem;
                return new List<MotionEvent>();
            }

            private async Task DetectMotionAsync(MotionDetectionSession session)
            {
                var cancellationToken = session.CancellationTokenSource.Token;

                try
                {
                    while (!cancellationToken.IsCancellationRequested && session.IsActive)
                    {
                        // Hareket tespiti yap;
                        await Task.Delay(100, cancellationToken);

                        // Simüle edilmiş hareket tespiti;
                        var hasMotion = new Random().NextDouble() > 0.8;
                        if (hasMotion)
                        {
                            // Hareket tespit edildi event'i yayınla;
                            var @event = new MotionDetectedEvent;
                            {
                                CameraId = session.CameraId,
                                Intensity = new Random().NextDouble(),
                                Confidence = 0.8 + new Random().NextDouble() * 0.2,
                                Timestamp = DateTime.UtcNow;
                            };

                            // Event bus'a yayınla (gerçek uygulamada)
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Tespit durduruldu;
                }
                catch (Exception ex)
                {
                    // Hata logla;
                }
            }

            public void Dispose()
            {
                foreach (var session in _sessions.Values)
                {
                    session.CancellationTokenSource.Cancel();
                    session.CancellationTokenSource.Dispose();
                }
                _sessions.Clear();
            }
        }

        /// <summary>
        /// Yüz tanıma motoru.
        /// </summary>
        internal class FaceRecognitionEngine : IDisposable
        {
            public async Task StartRecognitionAsync(string cameraId, FaceRecognitionConfig config)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;
            }

            public async Task StopRecognitionAsync(string cameraId)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;
            }

            public async Task<int> GetRecognitionCountAsync(TimeRange timeRange)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;
                return 0;
            }

            public async Task<List<FaceRecognitionEvent>> GetCameraRecognitionsAsync(string cameraId, TimeRange timeRange)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;
                return new List<FaceRecognitionEvent>();
            }

            public void Dispose()
            {
                // Kaynak temizleme;
            }
        }

        /// <summary>
        /// Alarm sistemi.
        /// </summary>
        internal class AlarmSystem : IDisposable
        {
            public async Task EnableAlarmAsync(string cameraId)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;
            }

            public async Task DisableAlarmAsync(string cameraId)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;
            }

            public async Task TriggerAlarmAsync(string cameraId, string reason)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;
            }

            public async Task<int> GetAlarmCountAsync(TimeRange timeRange)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;
                return 0;
            }

            public void Dispose()
            {
                // Kaynak temizleme;
            }
        }

        /// <summary>
        /// Analiz motoru.
        /// </summary>
        internal class AnalyticsEngine : IDisposable
        {
            public void Dispose()
            {
                // Kaynak temizleme;
            }
        }

        /// <summary>
        /// Akış yöneticisi.
        /// </summary>
        internal class StreamManager : IDisposable
        {
            public async Task<VideoStream> StartStreamAsync(string cameraId, StreamConfig config)
            {
                var stream = new VideoStream;
                {
                    Id = Guid.NewGuid(),
                    CameraId = cameraId,
                    StartTime = DateTime.UtcNow,
                    Config = config,
                    Status = StreamStatus.Active;
                };

                // Akış başlat;
                await Task.Delay(100); // Simüle edilmiş işlem;

                return stream;
            }

            public async Task StopStreamAsync(Guid streamId)
            {
                // Akış durdur;
                await Task.Delay(50); // Simüle edilmiş işlem;
            }

            public void Dispose()
            {
                // Kaynak temizleme;
            }
        }

        /// <summary>
        /// Kamera oturumu.
        /// </summary>
        internal class CameraSession;
        {
            public string CameraId { get; set; }
            public SurveillanceCamera Camera { get; set; }
            public CameraStatus Status { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? LastActivity { get; set; }
            public bool IsRecording { get; set; }
            public RecordingInfo CurrentRecording { get; set; }
            public bool IsMotionDetectionActive { get; set; }
            public MotionDetectionConfig MotionDetectionConfig { get; set; }
            public bool IsFaceRecognitionActive { get; set; }
            public FaceRecognitionConfig FaceRecognitionConfig { get; set; }
            public bool AlarmEnabled { get; set; }
            public DateTime? LastMotionTime { get; set; }
            public int MotionEventCount { get; set; }
        }

        /// <summary>
        /// Kayıt oturumu.
        /// </summary>
        internal class RecordingSession;
        {
            public RecordingInfo Recording { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; set; }
        }

        /// <summary>
        /// Hareket tespit oturumu.
        /// </summary>
        internal class MotionDetectionSession;
        {
            public string CameraId { get; set; }
            public MotionDetectionConfig Config { get; set; }
            public CancellationTokenSource CancellationTokenSource { get; set; }
            public bool IsActive { get; set; }
        }

        /// <summary>
        /// Abonelik iptal edici.
        /// </summary>
        private class Unsubscriber<T> : IDisposable
        {
            private readonly ConcurrentBag<IObserver<T>> _observers;
            private readonly IObserver<T> _observer;

            public Unsubscriber(ConcurrentBag<IObserver<T>> observers, IObserver<T> observer)
            {
                _observers = observers;
                _observer = observer;
            }

            public void Dispose()
            {
                if (_observer != null && _observers.Contains(_observer))
                {
                    _observers.TryTake(out _);
                }
            }
        }

        #endregion;
    }

    #region Public Models and Enums;

    /// <summary>
    /// Gözetim konfigürasyonu.
    /// </summary>
    public class SurveillanceConfig;
    {
        public List<SurveillanceCamera> Cameras { get; set; } = new List<SurveillanceCamera>();
        public List<SurveillanceFeature> Features { get; set; } = new List<SurveillanceFeature>();
        public StorageSettings StorageSettings { get; set; }
        public RecordingSettings RecordingSettings { get; set; }
        public AlarmSettings AlarmSettings { get; set; }
        public VisionConfig VisionConfig { get; set; }
        public Dictionary<string, object> AdvancedSettings { get; set; }
    }

    /// <summary>
    /// Gözetim kamerası.
    /// </summary>
    public class SurveillanceCamera;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public CameraType Type { get; set; }
        public string StreamUrl { get; set; }
        public string RtspUrl { get; set; }
        public CameraLocation Location { get; set; }
        public CameraSpecifications Specifications { get; set; }
        public Dictionary<string, object> Settings { get; set; }
        public List<string> Capabilities { get; set; } = new List<string>();
        public DateTime? LastMaintenance { get; set; }
    }

    /// <summary>
    /// Kamera görüntüsü.
    /// </summary>
    public class CameraFeed;
    {
        public string CameraId { get; set; }
        public DateTime Timestamp { get; set; }
        public VideoQuality Quality { get; set; }
        public byte[] FrameData { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Kayıt konfigürasyonu.
    /// </summary>
    public class RecordingConfig;
    {
        public VideoQuality Quality { get; set; } = VideoQuality.Medium;
        public TimeSpan? Duration { get; set; }
        public bool MotionTriggered { get; set; }
        public string StoragePath { get; set; }
        public VideoCodec Codec { get; set; } = VideoCodec.H264;
        public Dictionary<string, object> Settings { get; set; }
    }

    /// <summary>
    /// Kayıt bilgileri.
    /// </summary>
    public class RecordingInfo;
    {
        public Guid Id { get; set; }
        public string CameraId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
        public RecordingConfig Config { get; set; }
        public RecordingStatus Status { get; set; }
        public long? FileSize { get; set; }
        public string FilePath { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Hareket tespit konfigürasyonu.
    /// </summary>
    public class MotionDetectionConfig;
    {
        public double Sensitivity { get; set; } = 0.7;
        public int MinimumArea { get; set; } = 100;
        public bool EnableAlerts { get; set; } = true;
        public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromSeconds(10);
        public Dictionary<string, object> AdvancedSettings { get; set; }
    }

    /// <summary>
    /// Yüz tanıma konfigürasyonu.
    /// </summary>
    public class FaceRecognitionConfig;
    {
        public double ConfidenceThreshold { get; set; } = 0.8;
        public bool EnableIdentification { get; set; } = true;
        public bool EnableDatabaseLookup { get; set; } = true;
        public bool AlertOnUnknown { get; set; } = true;
        public Dictionary<string, object> Settings { get; set; }
    }

    /// <summary>
    /// Yakalanan görüntü.
    /// </summary>
    public class CapturedImage;
    {
        public Guid Id { get; set; }
        public string CameraId { get; set; }
        public DateTime Timestamp { get; set; }
        public ImageFormat Format { get; set; }
        public ImageQuality Quality { get; set; }
        public byte[] ImageData { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Kayıt arama kriterleri.
    /// </summary>
    public class RecordingSearchCriteria;
    {
        public string CameraId { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public RecordingStatus? Status { get; set; }
        public bool? MotionTriggered { get; set; }
        public string SearchText { get; set; }
        public int Limit { get; set; } = 100;
        public int Offset { get; set; } = 0;
    }

    /// <summary>
    /// Gözetim durumu.
    /// </summary>
    public class SurveillanceStatus;
    {
        public bool IsRunning { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int CameraCount { get; set; }
        public int ActiveRecordings { get; set; }
        public int ActiveStreams { get; set; }
        public int MotionDetectionActive { get; set; }
        public int FaceRecognitionActive { get; set; }
        public StorageUsage StorageUsage { get; set; }
        public DateTime LastActivity { get; set; }
    }

    /// <summary>
    /// Gözetim istatistikleri.
    /// </summary>
    public class SurveillanceStatistics;
    {
        public TimeRange TimeRange { get; set; }
        public int TotalRecordings { get; set; }
        public int TotalMotionEvents { get; set; }
        public int TotalFaceRecognitions { get; set; }
        public int TotalAlarms { get; set; }
        public StorageUsage StorageUsage { get; set; }
        public List<CameraStatistics> CameraStatistics { get; set; }
        public ActivityHeatmap ActivityHeatmap { get; set; }
    }

    /// <summary>
    /// Video akışı.
    /// </summary>
    public class VideoStream;
    {
        public Guid Id { get; set; }
        public string CameraId { get; set; }
        public DateTime StartTime { get; set; }
        public StreamConfig Config { get; set; }
        public StreamStatus Status { get; set; }
        public string StreamUrl { get; set; }
        public Dictionary<string, object> Statistics { get; set; }
    }

    /// <summary>
    /// Gözetim olayı.
    /// </summary>
    public class SurveillanceEvent;
    {
        public SurveillanceEventType Type { get; set; }
        public string CameraId { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    /// <summary>
    /// Depolama ayarları.
    /// </summary>
    public class StorageSettings;
    {
        public string RecordingPath { get; set; }
        public string SnapshotPath { get; set; }
        public int RetentionDays { get; set; } = 30;
        public long MaxStorageSize { get; set; } = 10737418240; // 10GB;
        public StorageType StorageType { get; set; } = StorageType.Local;
        public Dictionary<string, object> CloudSettings { get; set; }
    }

    /// <summary>
    /// Kayıt ayarları.
    /// </summary>
    public class RecordingSettings;
    {
        public VideoQuality Quality { get; set; } = VideoQuality.Medium;
        public bool MotionTriggeredRecording { get; set; } = true;
        public int MotionRecordingDuration { get; set; } = 5; // dakika;
        public bool ContinuousRecording { get; set; }
        public Dictionary<string, object> AdvancedSettings { get; set; }
    }

    /// <summary>
    /// Alarm ayarları.
    /// </summary>
    public class AlarmSettings;
    {
        public double MotionSensitivity { get; set; } = 0.7;
        public bool EnableAudioAlarm { get; set; } = true;
        public bool EnableNotification { get; set; } = true;
        public List<string> NotificationChannels { get; set; } = new List<string> { "Email", "SMS" };
        public Dictionary<string, object> IntegrationSettings { get; set; }
    }

    /// <summary>
    /// Kamera konumu.
    /// </summary>
    public class CameraLocation;
    {
        public string Room { get; set; }
        public string Building { get; set; }
        public GeoLocation Coordinates { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Kamera özellikleri.
    /// </summary>
    public class CameraSpecifications;
    {
        public string Resolution { get; set; }
        public double FrameRate { get; set; }
        public string LensType { get; set; }
        public double FieldOfView { get; set; }
        public bool Infrared { get; set; }
        public bool PanTiltZoom { get; set; }
        public Dictionary<string, object> TechnicalSpecs { get; set; }
    }

    /// <summary>
    /// Depolama kullanımı.
    /// </summary>
    public class StorageUsage;
    {
        public long TotalSpace { get; set; }
        public long UsedSpace { get; set; }
        public long AvailableSpace => TotalSpace - UsedSpace;
        public double UsagePercentage => TotalSpace > 0 ? (double)UsedSpace / TotalSpace : 0;
    }

    /// <summary>
    /// Kamera istatistikleri.
    /// </summary>
    public class CameraStatistics;
    {
        public string CameraId { get; set; }
        public string CameraName { get; set; }
        public TimeSpan Uptime { get; set; }
        public int RecordingCount { get; set; }
        public List<MotionEvent> MotionEvents { get; set; }
        public List<FaceRecognitionEvent> RecognizedFaces { get; set; }
    }

    /// <summary>
    /// Aktivite ısı haritası.
    /// </summary>
    public class ActivityHeatmap;
    {
        public TimeRange TimeRange { get; set; }
        public Dictionary<int, double> HourlyActivity { get; set; }
        public Dictionary<DayOfWeek, double> DailyActivity { get; set; }
    }

    /// <summary>
    /// Akış konfigürasyonu.
    /// </summary>
    public class StreamConfig;
    {
        public VideoQuality Quality { get; set; } = VideoQuality.Medium;
        public int MaxBitrate { get; set; } = 2000; // kbps;
        public string Protocol { get; set; } = "RTSP";
        public Dictionary<string, object> Settings { get; set; }
    }

    /// <summary>
    /// Yakalama konfigürasyonu.
    /// </summary>
    public class CaptureConfig;
    {
        public ImageQuality Quality { get; set; } = ImageQuality.High;
        public ImageFormat Format { get; set; } = ImageFormat.Jpeg;
        public bool IncludeMetadata { get; set; } = true;
        public Dictionary<string, object> Settings { get; set; }
    }

    /// <summary>
    /// Sistem sağlık durumu.
    /// </summary>
    public class SystemHealthStatus;
    {
        public DateTime Timestamp { get; set; }
        public int CameraCount { get; set; }
        public int HealthyCameras { get; set; }
        public StorageHealth StorageHealth { get; set; }
        public double AverageLatency { get; set; }
        public Dictionary<string, object> Metrics { get; set; }
    }

    /// <summary>
    /// Depolama sağlığı.
    /// </summary>
    public class StorageHealth;
    {
        public double UsagePercentage { get; set; }
        public bool IsHealthy { get; set; }
        public Dictionary<string, object> HealthIndicators { get; set; }
    }

    /// <summary>
    /// Zaman aralığı.
    /// </summary>
    public struct TimeRange;
    {
        public DateTime Start { get; }
        public DateTime End { get; }

        public TimeRange(DateTime start, DateTime end)
        {
            if (start >= end)
                throw new ArgumentException("Start time must be before end time");

            Start = start;
            End = end;
        }
    }

    /// <summary>
    /// Coğrafi konum.
    /// </summary>
    public class GeoLocation;
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Hareket olayı.
    /// </summary>
    public class MotionEvent;
    {
        public DateTime Timestamp { get; set; }
        public double Intensity { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Yüz tanıma olayı.
    /// </summary>
    public class FaceRecognitionEvent;
    {
        public DateTime Timestamp { get; set; }
        public string FaceId { get; set; }
        public string PersonName { get; set; }
        public double Confidence { get; set; }
        public bool IsUnknown { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Gözetim özelliği.
    /// </summary>
    public enum SurveillanceFeature;
    {
        MotionDetection,
        FaceRecognition,
        ObjectDetection,
        LicensePlateRecognition,
        PresenceDetection,
        AudioDetection,
        ThermalImaging,
        NightVision;
    }

    /// <summary>
    /// Kamera türü.
    /// </summary>
    public enum CameraType;
    {
        IPCamera,
        Webcam,
        SecurityCamera,
        PTZCamera,
        ThermalCamera,
        PanoramicCamera;
    }

    /// <summary>
    /// Video kalitesi.
    /// </summary>
    public enum VideoQuality;
    {
        Low,
        Medium,
        High,
        Ultra;
    }

    /// <summary>
    /// Kayıt durumu.
    /// </summary>
    public enum RecordingStatus;
    {
        Recording,
        Paused,
        Completed,
        Error,
        Cancelled;
    }

    /// <summary>
    /// Kamera durumu.
    /// </summary>
    public enum CameraStatus;
    {
        Connected,
        Disconnected,
        Error,
        Maintenance;
    }

    /// <summary>
    /// Görüntü formatı.
    /// </summary>
    public enum ImageFormat;
    {
        Jpeg,
        Png,
        Bmp,
        Tiff;
    }

    /// <summary>
    /// Görüntü kalitesi.
    /// </summary>
    public enum ImageQuality;
    {
        Low,
        Medium,
        High;
    }

    /// <summary>
    /// Depolama türü.
    /// </summary>
    public enum StorageType;
    {
        Local,
        Network,
        Cloud;
    }

    /// <summary>
    /// Video codec.
    /// </summary>
    public enum VideoCodec;
    {
        H264,
        H265,
        VP8,
        VP9,
        MJPEG;
    }

    /// <summary>
    /// Akış durumu.
    /// </summary>
    public enum StreamStatus;
    {
        Active,
        Paused,
        Stopped,
        Error;
    }

    /// <summary>
    /// Gözetim olay türü.
    /// </summary>
    public enum SurveillanceEventType;
    {
        SystemStarted,
        SystemStopped,
        CameraAdded,
        CameraRemoved,
        CameraStatusChanged,
        RecordingStarted,
        RecordingStopped,
        MotionDetected,
        FaceRecognized,
        IntrusionDetected,
        AlarmEnabled,
        AlarmDisabled,
        AlarmTriggered,
        HealthCheck;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Gözetim başlatıldı event'i.
    /// </summary>
    public class SurveillanceStartedEvent : IEvent;
    {
        public DateTime StartTime { get; set; }
        public int CameraCount { get; set; }
        public List<SurveillanceFeature> Features { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Gözetim durduruldu event'i.
    /// </summary>
    public class SurveillanceStoppedEvent : IEvent;
    {
        public DateTime StopTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int TotalRecordings { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kamera eklendi event'i.
    /// </summary>
    public class CameraAddedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public string CameraName { get; set; }
        public CameraType CameraType { get; set; }
        public CameraLocation Location { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kamera kaldırıldı event'i.
    /// </summary>
    public class CameraRemovedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public string CameraName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kamera durumu değişti event'i.
    /// </summary>
    public class CameraStatusChangedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public CameraStatus PreviousStatus { get; set; }
        public CameraStatus NewStatus { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kayıt başlatıldı event'i.
    /// </summary>
    public class RecordingStartedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public Guid RecordingId { get; set; }
        public DateTime StartTime { get; set; }
        public RecordingConfig Config { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kayıt durduruldu event'i.
    /// </summary>
    public class RecordingStoppedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public Guid RecordingId { get; set; }
        public TimeSpan Duration { get; set; }
        public long? FileSize { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hareket tespiti başlatıldı event'i.
    /// </summary>
    public class MotionDetectionStartedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public MotionDetectionConfig Config { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hareket tespiti durduruldu event'i.
    /// </summary>
    public class MotionDetectionStoppedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Yüz tanıma başlatıldı event'i.
    /// </summary>
    public class FaceRecognitionStartedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public FaceRecognitionConfig Config { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Yüz tanıma durduruldu event'i.
    /// </summary>
    public class FaceRecognitionStoppedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Alarm durumu değişti event'i.
    /// </summary>
    public class AlarmStatusChangedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public bool AlarmEnabled { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hareket tespit edildi event'i.
    /// </summary>
    public class MotionDetectedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public double Intensity { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Yüz tanındı event'i.
    /// </summary>
    public class FaceRecognizedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public string FaceId { get; set; }
        public string PersonName { get; set; }
        public double Confidence { get; set; }
        public bool IsUnknown { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// İzinsiz giriş tespit edildi event'i.
    /// </summary>
    public class IntrusionDetectedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public string Description { get; set; }
        public int Severity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Alarm tetiklendi event'i.
    /// </summary>
    public class AlarmTriggeredEvent : IEvent;
    {
        public string CameraId { get; set; }
        public string Reason { get; set; }
        public double? Intensity { get; set; }
        public double? Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Canlı akış başlatıldı event'i.
    /// </summary>
    public class LiveStreamStartedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public Guid StreamId { get; set; }
        public StreamConfig StreamConfig { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Canlı akış durduruldu event'i.
    /// </summary>
    public class LiveStreamStoppedEvent : IEvent;
    {
        public string CameraId { get; set; }
        public Guid StreamId { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sağlık kontrolü event'i.
    /// </summary>
    public class HealthCheckEvent : IEvent;
    {
        public SystemHealthStatus HealthStatus { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}
