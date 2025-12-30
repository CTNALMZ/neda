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
using NEDA.Biometrics.FaceRecognition;
using NEDA.Biometrics.MotionTracking;
using NEDA.SecurityModules.Monitoring;

namespace NEDA.RealTimeMonitoring.PresenceAnalysis;
{
    /// <summary>
    /// Varlık tespit motoru arayüzü.
    /// </summary>
    public interface IPresenceDetector : IDisposable
    {
        /// <summary>
        /// Gerçek zamanlı varlık tespitini başlatır.
        /// </summary>
        /// <param name="config">Tespit konfigürasyonu.</param>
        Task StartDetectionAsync(PresenceDetectionConfig config);

        /// <summary>
        /// Varlık tespitini durdurur.
        /// </summary>
        Task StopDetectionAsync();

        /// <summary>
        /// Belirli bir alandaki varlık durumunu getirir.
        /// </summary>
        /// <param name="areaId">Alan ID.</param>
        Task<PresenceStatus> GetPresenceStatusAsync(string areaId);

        /// <summary>
        /// Tüm izlenen alanlardaki varlık durumunu getirir.
        /// </summary>
        Task<List<AreaPresenceStatus>> GetAllAreasStatusAsync();

        /// <summary>
        /// Varlık istatistiklerini getirir.
        /// </summary>
        /// <param name="timeRange">Zaman aralığı.</param>
        Task<PresenceStatistics> GetStatisticsAsync(TimeRange timeRange);

        /// <summary>
        /// Varlık trendlerini analiz eder.
        /// </summary>
        /// <param name="areaId">Alan ID.</param>
        /// <param name="days">Gün sayısı.</param>
        Task<PresenceTrend> AnalyzeTrendsAsync(string areaId, int days);

        /// <summary>
        /// Anomalik varlık durumlarını tespit eder.
        /// </summary>
        Task<List<PresenceAnomaly>> DetectAnomaliesAsync();

        /// <summary>
        /// Varlık verilerini dışa aktarır.
        /// </summary>
        /// <param name="format">Dışa aktarma formatı.</param>
        Task<byte[]> ExportPresenceDataAsync(ExportFormat format);

        /// <summary>
        /// Tespit motoru durumunu getirir.
        /// </summary>
        PresenceDetectorStatus GetStatus();
    }

    /// <summary>
    /// Varlık tespit motoru ana sınıfı.
    /// </summary>
    public class PresenceDetector : IPresenceDetector;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly IVisionEngine _visionEngine;
        private readonly IFaceDetector _faceDetector;
        private readonly IMotionSensor _motionSensor;
        private readonly IThreatAnalyzer _threatAnalyzer;

        private readonly DetectionEngine _detectionEngine;
        private readonly AreaManager _areaManager;
        private readonly PresenceAnalyzer _presenceAnalyzer;
        private readonly AnomalyDetector _anomalyDetector;
        private readonly DataExporter _dataExporter;

        private readonly ConcurrentDictionary<string, AreaPresenceData> _areaData;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _areaDetectionTasks;
        private readonly ConcurrentQueue<DetectionEvent> _detectionQueue;
        private readonly SemaphoreSlim _processingLock;
        private readonly Timer _analysisTimer;
        private readonly Timer _cleanupTimer;

        private PresenceDetectionConfig _config;
        private bool _disposed;
        private bool _isRunning;
        private DateTime _startTime;
        private int _totalDetections;

        /// <summary>
        /// Varlık tespit motoru oluşturur.
        /// </summary>
        public PresenceDetector(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IEventBus eventBus,
            IVisionEngine visionEngine,
            IFaceDetector faceDetector,
            IMotionSensor motionSensor,
            IThreatAnalyzer threatAnalyzer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _visionEngine = visionEngine ?? throw new ArgumentNullException(nameof(visionEngine));
            _faceDetector = faceDetector ?? throw new ArgumentNullException(nameof(faceDetector));
            _motionSensor = motionSensor ?? throw new ArgumentNullException(nameof(motionSensor));
            _threatAnalyzer = threatAnalyzer ?? throw new ArgumentNullException(nameof(threatAnalyzer));

            _detectionEngine = new DetectionEngine();
            _areaManager = new AreaManager();
            _presenceAnalyzer = new PresenceAnalyzer();
            _anomalyDetector = new AnomalyDetector();
            _dataExporter = new DataExporter();

            _areaData = new ConcurrentDictionary<string, AreaPresenceData>();
            _areaDetectionTasks = new ConcurrentDictionary<string, CancellationTokenSource>();
            _detectionQueue = new ConcurrentQueue<DetectionEvent>();
            _processingLock = new SemaphoreSlim(1, 1);

            // Analiz timer'ı: her 5 dakikada bir;
            _analysisTimer = new Timer(PerformPeriodicAnalysis, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            // Temizlik timer'ı: her saat başı;
            _cleanupTimer = new Timer(CleanupOldData, null,
                TimeSpan.FromHours(1), TimeSpan.FromHours(1));

            _eventBus.Subscribe<MotionDetectedEvent>(HandleMotionDetection);
            _eventBus.Subscribe<FaceDetectedEvent>(HandleFaceDetection);
            _eventBus.Subscribe<CameraStatusEvent>(HandleCameraStatus);

            _logger.LogInformation("PresenceDetector initialized");
        }

        /// <summary>
        /// Gerçek zamanlı varlık tespitini başlatır.
        /// </summary>
        public async Task StartDetectionAsync(PresenceDetectionConfig config)
        {
            if (_isRunning) return;

            await _processingLock.WaitAsync();
            try
            {
                _config = config ?? throw new ArgumentNullException(nameof(config));

                // Alanları yapılandır;
                await ConfigureAreasAsync(config.Areas);

                // Tespit motorlarını başlat;
                await InitializeDetectionEnginesAsync(config);

                // Her alan için tespit task'larını başlat;
                await StartAreaDetectionTasksAsync();

                _isRunning = true;
                _startTime = DateTime.UtcNow;
                _totalDetections = 0;

                _logger.LogInformation($"Presence detection started with {config.Areas.Count} areas");

                var @event = new DetectionStartedEvent;
                {
                    StartTime = _startTime,
                    AreaCount = config.Areas.Count,
                    DetectionMethods = config.DetectionMethods,
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start presence detection");
                throw;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Varlık tespitini durdurur.
        /// </summary>
        public async Task StopDetectionAsync()
        {
            if (!_isRunning) return;

            await _processingLock.WaitAsync();
            try
            {
                // Tüm tespit task'larını durdur;
                foreach (var task in _areaDetectionTasks)
                {
                    task.Value.Cancel();
                }
                _areaDetectionTasks.Clear();

                // Tespit motorlarını durdur;
                await StopDetectionEnginesAsync();

                _isRunning = false;

                _logger.LogInformation("Presence detection stopped");

                var @event = new DetectionStoppedEvent;
                {
                    StopTime = DateTime.UtcNow,
                    Uptime = DateTime.UtcNow - _startTime,
                    TotalDetections = _totalDetections,
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
        /// Belirli bir alandaki varlık durumunu getirir.
        /// </summary>
        public async Task<PresenceStatus> GetPresenceStatusAsync(string areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId))
                throw new ArgumentException("Area ID cannot be null or empty", nameof(areaId));

            if (!_areaData.TryGetValue(areaId, out var areaData))
            {
                return new PresenceStatus;
                {
                    AreaId = areaId,
                    IsActive = false,
                    Message = "Area not found"
                };
            }

            var now = DateTime.UtcNow;
            var recentDetections = areaData.Detections;
                .Where(d => d.Timestamp > now.AddMinutes(-5))
                .ToList();

            var status = new PresenceStatus;
            {
                AreaId = areaId,
                AreaName = areaData.Name,
                IsActive = recentDetections.Any(),
                LastDetectionTime = areaData.LastDetectionTime,
                CurrentCount = await EstimateCurrentCountAsync(areaId),
                DetectionCount5Min = recentDetections.Count,
                AveragePresenceDuration = await CalculateAverageDurationAsync(areaId),
                Confidence = CalculatePresenceConfidence(recentDetections)
            };

            return status;
        }

        /// <summary>
        /// Tüm izlenen alanlardaki varlık durumunu getirir.
        /// </summary>
        public async Task<List<AreaPresenceStatus>> GetAllAreasStatusAsync()
        {
            var statuses = new List<AreaPresenceStatus>();

            foreach (var areaId in _areaData.Keys)
            {
                var status = await GetPresenceStatusAsync(areaId);
                statuses.Add(new AreaPresenceStatus;
                {
                    AreaId = areaId,
                    Status = status,
                    IsMonitored = _areaDetectionTasks.ContainsKey(areaId)
                });
            }

            return statuses;
        }

        /// <summary>
        /// Varlık istatistiklerini getirir.
        /// </summary>
        public async Task<PresenceStatistics> GetStatisticsAsync(TimeRange timeRange)
        {
            var statistics = new PresenceStatistics;
            {
                TimeRange = timeRange,
                TotalAreas = _areaData.Count,
                ActiveAreas = await GetActiveAreasCountAsync(),
                TotalDetections = await GetTotalDetectionsAsync(timeRange),
                AveragePresenceDuration = await CalculateAveragePresenceDurationAsync(timeRange),
                PeakTimes = await CalculatePeakTimesAsync(timeRange),
                DetectionMethods = await GetDetectionMethodStatsAsync(timeRange),
                Areas = await GetAreaStatisticsAsync(timeRange)
            };

            return statistics;
        }

        /// <summary>
        /// Varlık trendlerini analiz eder.
        /// </summary>
        public async Task<PresenceTrend> AnalyzeTrendsAsync(string areaId, int days)
        {
            if (string.IsNullOrWhiteSpace(areaId))
                throw new ArgumentException("Area ID cannot be null or empty", nameof(areaId));

            if (!_areaData.TryGetValue(areaId, out var areaData))
            {
                throw new ArgumentException($"Area {areaId} not found", nameof(areaId));
            }

            var endDate = DateTime.UtcNow;
            var startDate = endDate.AddDays(-days);

            var hourlyData = await _presenceAnalyzer.AnalyzeHourlyTrendsAsync(areaData, startDate, endDate);
            var dailyPatterns = await _presenceAnalyzer.AnalyzeDailyPatternsAsync(areaData, startDate, endDate);
            var weeklyTrends = await _presenceAnalyzer.AnalyzeWeeklyTrendsAsync(areaData, startDate, endDate);

            return new PresenceTrend;
            {
                AreaId = areaId,
                AnalysisPeriod = days,
                HourlyData = hourlyData,
                DailyPatterns = dailyPatterns,
                WeeklyTrends = weeklyTrends,
                PeakHours = CalculatePeakHours(hourlyData),
                BusiestDays = CalculateBusiestDays(dailyPatterns),
                TrendDirection = CalculateTrendDirection(hourlyData)
            };
        }

        /// <summary>
        /// Anomalik varlık durumlarını tespit eder.
        /// </summary>
        public async Task<List<PresenceAnomaly>> DetectAnomaliesAsync()
        {
            var anomalies = new List<PresenceAnomaly>();

            foreach (var areaData in _areaData.Values)
            {
                var areaAnomalies = await _anomalyDetector.DetectAnomaliesAsync(areaData);
                anomalies.AddRange(areaAnomalies);
            }

            // Anomali tespiti yapıldığında event yayınla;
            if (anomalies.Any())
            {
                var @event = new AnomaliesDetectedEvent;
                {
                    AnomalyCount = anomalies.Count,
                    HighSeverityCount = anomalies.Count(a => a.Severity >= AnomalySeverity.High),
                    Timestamp = DateTime.UtcNow;
                };

                await _eventBus.PublishAsync(@event);

                _logger.LogWarning($"Detected {anomalies.Count} presence anomalies");
            }

            return anomalies;
        }

        /// <summary>
        /// Varlık verilerini dışa aktarır.
        /// </summary>
        public async Task<byte[]> ExportPresenceDataAsync(ExportFormat format)
        {
            var exportData = new PresenceExportData;
            {
                Areas = _areaData.Values.ToList(),
                ExportTime = DateTime.UtcNow,
                Config = _config;
            };

            return await _dataExporter.ExportAsync(exportData, format);
        }

        /// <summary>
        /// Tespit motoru durumunu getirir.
        /// </summary>
        public PresenceDetectorStatus GetStatus()
        {
            return new PresenceDetectorStatus;
            {
                IsRunning = _isRunning,
                StartTime = _startTime,
                Uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                ActiveAreas = _areaDetectionTasks.Count,
                TotalDetections = _totalDetections,
                QueueSize = _detectionQueue.Count,
                DetectionEngines = GetDetectionEngineStatus(),
                LastAnalysisTime = DateTime.UtcNow;
            };
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
                    StopDetectionAsync().Wait(TimeSpan.FromSeconds(5));

                    _analysisTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _processingLock?.Dispose();

                    foreach (var cts in _areaDetectionTasks.Values)
                    {
                        cts.Dispose();
                    }
                    _areaDetectionTasks.Clear();

                    if (_eventBus != null)
                    {
                        _eventBus.Unsubscribe<MotionDetectedEvent>(HandleMotionDetection);
                        _eventBus.Unsubscribe<FaceDetectedEvent>(HandleFaceDetection);
                        _eventBus.Unsubscribe<CameraStatusEvent>(HandleCameraStatus);
                    }

                    _detectionEngine.Dispose();
                    _dataExporter.Dispose();
                }

                _disposed = true;
            }
        }

        #region Private Methods;

        private async Task ConfigureAreasAsync(List<MonitoringArea> areas)
        {
            foreach (var area in areas)
            {
                var areaData = new AreaPresenceData;
                {
                    AreaId = area.Id,
                    Name = area.Name,
                    Type = area.Type,
                    Sensors = area.Sensors,
                    CreatedAt = DateTime.UtcNow,
                    Detections = new List<PresenceDetection>(),
                    Statistics = new AreaStatistics()
                };

                _areaData[area.Id] = areaData;
                _areaManager.RegisterArea(area);

                _logger.LogDebug($"Area configured: {area.Name} ({area.Id})");
            }
        }

        private async Task InitializeDetectionEnginesAsync(PresenceDetectionConfig config)
        {
            // Görüntü işleme motorunu başlat;
            if (config.DetectionMethods.Contains(DetectionMethod.VideoAnalysis))
            {
                await _visionEngine.InitializeAsync(config.VisionConfig);
                _logger.LogDebug("Vision engine initialized");
            }

            // Yüz tanıma motorunu başlat;
            if (config.DetectionMethods.Contains(DetectionMethod.FaceRecognition))
            {
                await _faceDetector.InitializeAsync();
                _logger.LogDebug("Face detector initialized");
            }

            // Hareket sensörlerini başlat;
            if (config.DetectionMethods.Contains(DetectionMethod.MotionSensors))
            {
                await _motionSensor.InitializeAsync();
                _logger.LogDebug("Motion sensor initialized");
            }

            // Tespit motorunu başlat;
            _detectionEngine.Initialize(config);
        }

        private async Task StartAreaDetectionTasksAsync()
        {
            foreach (var area in _config.Areas)
            {
                var cancellationTokenSource = new CancellationTokenSource();
                _areaDetectionTasks[area.Id] = cancellationTokenSource;

                // Her alan için ayrı tespit task'ı başlat;
                _ = Task.Run(() => MonitorAreaAsync(area.Id, cancellationTokenSource.Token),
                    cancellationTokenSource.Token);

                _logger.LogDebug($"Detection task started for area: {area.Name}");
            }
        }

        private async Task MonitorAreaAsync(string areaId, CancellationToken cancellationToken)
        {
            if (!_areaData.TryGetValue(areaId, out var areaData))
                return;

            var areaConfig = _config.Areas.FirstOrDefault(a => a.Id == areaId);
            if (areaConfig == null) return;

            _logger.LogInformation($"Started monitoring area: {areaData.Name}");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Tespit yap;
                        var detectionResult = await PerformDetectionAsync(areaConfig, areaData);

                        if (detectionResult.HasDetection)
                        {
                            await ProcessDetectionAsync(areaId, detectionResult);
                        }

                        // Tespit aralığı kadar bekle;
                        await Task.Delay(_config.DetectionInterval, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error monitoring area {areaId}");
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                }
            }
            finally
            {
                _logger.LogInformation($"Stopped monitoring area: {areaData.Name}");
            }
        }

        private async Task<DetectionResult> PerformDetectionAsync(MonitoringArea area, AreaPresenceData areaData)
        {
            var result = new DetectionResult;
            {
                AreaId = area.Id,
                Timestamp = DateTime.UtcNow,
                DetectionMethods = new List<DetectionMethod>()
            };

            // Görüntü analizi ile tespit;
            if (_config.DetectionMethods.Contains(DetectionMethod.VideoAnalysis) &&
                area.Sensors.Any(s => s.Type == SensorType.Camera))
            {
                var cameraResult = await PerformVideoAnalysisAsync(area);
                if (cameraResult.HasDetection)
                {
                    result.HasDetection = true;
                    result.DetectionMethods.Add(DetectionMethod.VideoAnalysis);
                    result.DetectedObjects.AddRange(cameraResult.DetectedObjects);
                    result.Confidence = Math.Max(result.Confidence, cameraResult.Confidence);
                }
            }

            // Hareket sensörleri ile tespit;
            if (_config.DetectionMethods.Contains(DetectionMethod.MotionSensors) &&
                area.Sensors.Any(s => s.Type == SensorType.Motion))
            {
                var motionResult = await PerformMotionDetectionAsync(area);
                if (motionResult.HasDetection)
                {
                    result.HasDetection = true;
                    result.DetectionMethods.Add(DetectionMethod.MotionSensors);
                    result.Confidence = Math.Max(result.Confidence, motionResult.Confidence);
                }
            }

            // Yüz tanıma ile tespit;
            if (_config.DetectionMethods.Contains(DetectionMethod.FaceRecognition) &&
                area.Sensors.Any(s => s.Type == SensorType.Camera))
            {
                var faceResult = await PerformFaceDetectionAsync(area);
                if (faceResult.HasDetection)
                {
                    result.HasDetection = true;
                    result.DetectionMethods.Add(DetectionMethod.FaceRecognition);
                    result.DetectedFaces.AddRange(faceResult.DetectedFaces);
                    result.Confidence = Math.Max(result.Confidence, faceResult.Confidence);
                }
            }

            return result;
        }

        private async Task<VideoAnalysisResult> PerformVideoAnalysisAsync(MonitoringArea area)
        {
            var camera = area.Sensors.FirstOrDefault(s => s.Type == SensorType.Camera);
            if (camera == null) return new VideoAnalysisResult();

            try
            {
                var frame = await _visionEngine.CaptureFrameAsync(camera.Id);
                var analysis = await _visionEngine.AnalyzeFrameAsync(frame, _config.VisionConfig);

                return new VideoAnalysisResult;
                {
                    HasDetection = analysis.HasObjects,
                    DetectedObjects = analysis.Objects,
                    Confidence = analysis.Confidence,
                    FrameData = frame;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Video analysis failed for camera {camera.Id}");
                return new VideoAnalysisResult();
            }
        }

        private async Task<MotionDetectionResult> PerformMotionDetectionAsync(MonitoringArea area)
        {
            var motionSensors = area.Sensors.Where(s => s.Type == SensorType.Motion).ToList();
            if (!motionSensors.Any()) return new MotionDetectionResult();

            try
            {
                var sensorReadings = new List<SensorReading>();

                foreach (var sensor in motionSensors)
                {
                    var reading = await _motionSensor.ReadSensorAsync(sensor.Id);
                    sensorReadings.Add(reading);
                }

                var hasMotion = sensorReadings.Any(r => r.Value > _config.MotionThreshold);
                var confidence = sensorReadings.Max(r => r.Value);

                return new MotionDetectionResult;
                {
                    HasDetection = hasMotion,
                    SensorReadings = sensorReadings,
                    Confidence = confidence;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Motion detection failed");
                return new MotionDetectionResult();
            }
        }

        private async Task<FaceDetectionResult> PerformFaceDetectionAsync(MonitoringArea area)
        {
            var camera = area.Sensors.FirstOrDefault(s => s.Type == SensorType.Camera);
            if (camera == null) return new FaceDetectionResult();

            try
            {
                var frame = await _visionEngine.CaptureFrameAsync(camera.Id);
                var faces = await _faceDetector.DetectFacesAsync(frame);

                return new FaceDetectionResult;
                {
                    HasDetection = faces.Any(),
                    DetectedFaces = faces,
                    Confidence = faces.Any() ? faces.Max(f => f.Confidence) : 0.0;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Face detection failed for camera {camera.Id}");
                return new FaceDetectionResult();
            }
        }

        private async Task ProcessDetectionAsync(string areaId, DetectionResult detectionResult)
        {
            if (!_areaData.TryGetValue(areaId, out var areaData))
                return;

            // Tespit kaydı oluştur;
            var detection = new PresenceDetection;
            {
                Id = Guid.NewGuid(),
                AreaId = areaId,
                Timestamp = detectionResult.Timestamp,
                HasDetection = detectionResult.HasDetection,
                DetectionMethods = detectionResult.DetectionMethods,
                DetectedObjects = detectionResult.DetectedObjects,
                DetectedFaces = detectionResult.DetectedFaces,
                Confidence = detectionResult.Confidence,
                Metadata = new Dictionary<string, object>
                {
                    ["sensor_data"] = detectionResult.SensorData;
                }
            };

            // Alan verilerine ekle;
            areaData.Detections.Add(detection);
            areaData.LastDetectionTime = detection.Timestamp;

            // İstatistikleri güncelle;
            UpdateAreaStatistics(areaData, detection);

            // Sıraya ekle;
            _detectionQueue.Enqueue(new DetectionEvent;
            {
                Detection = detection,
                AreaData = areaData;
            });

            // Toplam tespit sayısını artır;
            Interlocked.Increment(ref _totalDetections);

            // Metrikleri kaydet;
            _metricsCollector.RecordMetric("presence.detection", 1);
            _metricsCollector.RecordMetric($"presence.detection.{areaId}", 1);

            // Tespit yapıldığında event yayınla;
            var @event = new PresenceDetectedEvent;
            {
                AreaId = areaId,
                Detection = detection,
                AreaName = areaData.Name,
                Timestamp = DateTime.UtcNow;
            };

            await _eventBus.PublishAsync(@event);

            _logger.LogDebug($"Presence detected in area {areaId}, confidence: {detection.Confidence:P0}");

            // Yüksek güvenilirlikli tespitler için ek analiz;
            if (detection.Confidence > 0.8)
            {
                await PerformDeepAnalysisAsync(detection, areaData);
            }
        }

        private void UpdateAreaStatistics(AreaPresenceData areaData, PresenceDetection detection)
        {
            var stats = areaData.Statistics;

            stats.TotalDetections++;

            if (detection.HasDetection)
            {
                stats.PresenceDetections++;
                stats.LastPresenceTime = detection.Timestamp;

                // Tespit metotlarına göre istatistik;
                foreach (var method in detection.DetectionMethods)
                {
                    if (!stats.DetectionMethodCounts.ContainsKey(method))
                        stats.DetectionMethodCounts[method] = 0;
                    stats.DetectionMethodCounts[method]++;
                }
            }
            else;
            {
                stats.AbsenceDetections++;
            }

            // Saat bazlı istatistik;
            var hour = detection.Timestamp.Hour;
            if (!stats.HourlyCounts.ContainsKey(hour))
                stats.HourlyCounts[hour] = 0;
            stats.HourlyCounts[hour]++;
        }

        private async Task PerformDeepAnalysisAsync(PresenceDetection detection, AreaPresenceData areaData)
        {
            try
            {
                // Güvenlik analizi;
                var securityEvent = new SecurityEvent;
                {
                    Id = Guid.NewGuid(),
                    EventType = "PresenceDetection",
                    Timestamp = detection.Timestamp,
                    SourceIp = areaData.Name,
                    Metadata = new Dictionary<string, object>
                    {
                        ["area_id"] = areaData.AreaId,
                        ["detection_methods"] = detection.DetectionMethods,
                        ["confidence"] = detection.Confidence,
                        ["objects"] = detection.DetectedObjects;
                    }
                };

                await _threatAnalyzer.AnalyzeSecurityEventAsync(securityEvent);

                // Anomali tespiti;
                var anomalyCheck = await _anomalyDetector.CheckDetectionAnomalyAsync(detection, areaData);
                if (anomalyCheck.IsAnomaly)
                {
                    _logger.LogWarning($"Anomalous detection in area {areaData.AreaId}: {anomalyCheck.Reason}");

                    var anomalyEvent = new PresenceAnomalyDetectedEvent;
                    {
                        AreaId = areaData.AreaId,
                        Detection = detection,
                        AnomalyType = anomalyCheck.AnomalyType,
                        Severity = anomalyCheck.Severity,
                        Timestamp = DateTime.UtcNow;
                    };

                    await _eventBus.PublishAsync(anomalyEvent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deep analysis failed");
            }
        }

        private async Task<int> EstimateCurrentCountAsync(string areaId)
        {
            if (!_areaData.TryGetValue(areaId, out var areaData))
                return 0;

            var recentDetections = areaData.Detections;
                .Where(d => d.Timestamp > DateTime.UtcNow.AddMinutes(-2))
                .ToList();

            if (!recentDetections.Any()) return 0;

            // Yüz tanıma sonuçlarına göre sayım;
            var faceCount = recentDetections;
                .SelectMany(d => d.DetectedFaces)
                .Select(f => f.FaceId)
                .Distinct()
                .Count();

            if (faceCount > 0) return faceCount;

            // Nesne tespitine göre sayım;
            var objectCount = recentDetections;
                .SelectMany(d => d.DetectedObjects)
                .Count(o => o.Type == "person");

            return objectCount;
        }

        private async Task<TimeSpan> CalculateAverageDurationAsync(string areaId)
        {
            if (!_areaData.TryGetValue(areaId, out var areaData))
                return TimeSpan.Zero;

            return await _presenceAnalyzer.CalculateAveragePresenceDurationAsync(areaData);
        }

        private double CalculatePresenceConfidence(List<PresenceDetection> recentDetections)
        {
            if (!recentDetections.Any()) return 0.0;

            return recentDetections.Average(d => d.Confidence);
        }

        private async Task<int> GetActiveAreasCountAsync()
        {
            int activeCount = 0;

            foreach (var areaId in _areaData.Keys)
            {
                var status = await GetPresenceStatusAsync(areaId);
                if (status.IsActive) activeCount++;
            }

            return activeCount;
        }

        private async Task<int> GetTotalDetectionsAsync(TimeRange timeRange)
        {
            int total = 0;

            foreach (var areaData in _areaData.Values)
            {
                total += areaData.Detections;
                    .Count(d => d.Timestamp >= timeRange.Start && d.Timestamp <= timeRange.End);
            }

            return total;
        }

        private async Task<TimeSpan> CalculateAveragePresenceDurationAsync(TimeRange timeRange)
        {
            var durations = new List<TimeSpan>();

            foreach (var areaData in _areaData.Values)
            {
                var areaDuration = await _presenceAnalyzer.CalculateAverageDurationInRangeAsync(
                    areaData, timeRange.Start, timeRange.End);
                durations.Add(areaDuration);
            }

            return durations.Any() ? TimeSpan.FromTicks((long)durations.Average(d => d.Ticks)) : TimeSpan.Zero;
        }

        private async Task<List<PeakTime>> CalculatePeakTimesAsync(TimeRange timeRange)
        {
            var peakTimes = new List<PeakTime>();

            foreach (var areaData in _areaData.Values)
            {
                var areaPeaks = await _presenceAnalyzer.CalculatePeakTimesAsync(areaData, timeRange);
                peakTimes.AddRange(areaPeaks);
            }

            return peakTimes;
                .GroupBy(p => p.Hour)
                .Select(g => new PeakTime;
                {
                    Hour = g.Key,
                    AverageCount = (int)g.Average(p => p.Count),
                    AreaCount = g.Count()
                })
                .OrderByDescending(p => p.AverageCount)
                .Take(5)
                .ToList();
        }

        private async Task<Dictionary<DetectionMethod, int>> GetDetectionMethodStatsAsync(TimeRange timeRange)
        {
            var stats = new Dictionary<DetectionMethod, int>();

            foreach (var areaData in _areaData.Values)
            {
                var areaStats = await _presenceAnalyzer.GetDetectionMethodStatsAsync(areaData, timeRange);

                foreach (var kvp in areaStats)
                {
                    if (!stats.ContainsKey(kvp.Key))
                        stats[kvp.Key] = 0;
                    stats[kvp.Key] += kvp.Value;
                }
            }

            return stats;
        }

        private async Task<List<AreaStatistic>> GetAreaStatisticsAsync(TimeRange timeRange)
        {
            var statistics = new List<AreaStatistic>();

            foreach (var areaData in _areaData.Values)
            {
                var stat = await _presenceAnalyzer.GetAreaStatisticsAsync(areaData, timeRange);
                statistics.Add(stat);
            }

            return statistics;
        }

        private List<PeakHour> CalculatePeakHours(Dictionary<int, double> hourlyData)
        {
            return hourlyData;
                .Select(kvp => new PeakHour { Hour = kvp.Key, AveragePresence = kvp.Value })
                .OrderByDescending(h => h.AveragePresence)
                .Take(3)
                .ToList();
        }

        private List<BusiestDay> CalculateBusiestDays(Dictionary<DayOfWeek, double> dailyPatterns)
        {
            return dailyPatterns;
                .Select(kvp => new BusiestDay { Day = kvp.Key, AveragePresence = kvp.Value })
                .OrderByDescending(d => d.AveragePresence)
                .Take(2)
                .ToList();
        }

        private TrendDirection CalculateTrendDirection(Dictionary<int, double> hourlyData)
        {
            if (hourlyData.Count < 2) return TrendDirection.Stable;

            var recentHours = hourlyData;
                .Where(kvp => kvp.Key >= DateTime.UtcNow.Hour - 3 && kvp.Key <= DateTime.UtcNow.Hour)
                .ToList();

            if (recentHours.Count < 2) return TrendDirection.Stable;

            var first = recentHours.First().Value;
            var last = recentHours.Last().Value;

            if (last > first * 1.3) return TrendDirection.Increasing;
            if (last < first * 0.7) return TrendDirection.Decreasing;
            return TrendDirection.Stable;
        }

        private Dictionary<string, DetectionEngineStatus> GetDetectionEngineStatus()
        {
            return new Dictionary<string, DetectionEngineStatus>
            {
                ["VisionEngine"] = new DetectionEngineStatus;
                {
                    IsActive = _visionEngine.IsInitialized,
                    LastActivity = DateTime.UtcNow;
                },
                ["FaceDetector"] = new DetectionEngineStatus;
                {
                    IsActive = _faceDetector.IsInitialized,
                    LastActivity = DateTime.UtcNow;
                },
                ["MotionSensor"] = new DetectionEngineStatus;
                {
                    IsActive = _motionSensor.IsInitialized,
                    LastActivity = DateTime.UtcNow;
                }
            };
        }

        private async Task StopDetectionEnginesAsync()
        {
            if (_visionEngine.IsInitialized)
            {
                await _visionEngine.ShutdownAsync();
            }

            if (_faceDetector.IsInitialized)
            {
                await _faceDetector.ShutdownAsync();
            }

            if (_motionSensor.IsInitialized)
            {
                await _motionSensor.ShutdownAsync();
            }

            _detectionEngine.Shutdown();
        }

        private void PerformPeriodicAnalysis(object state)
        {
            try
            {
                Task.Run(async () =>
                {
                    // Periyodik analiz yap;
                    await DetectAnomaliesAsync();

                    // İstatistikleri güncelle;
                    await UpdateStatisticsAsync();

                    // Sistem durumunu kontrol et;
                    await CheckSystemHealthAsync();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic analysis");
            }
        }

        private void CleanupOldData(object state)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow.AddDays(-_config.DataRetentionDays);

                foreach (var areaData in _areaData.Values)
                {
                    // Eski tespitleri temizle;
                    var oldDetections = areaData.Detections;
                        .Where(d => d.Timestamp < cutoffTime)
                        .ToList();

                    foreach (var oldDetection in oldDetections)
                    {
                        areaData.Detections.Remove(oldDetection);
                    }

                    if (oldDetections.Any())
                    {
                        _logger.LogDebug($"Cleaned up {oldDetections.Count} old detections from area {areaData.AreaId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old data");
            }
        }

        private async Task UpdateStatisticsAsync()
        {
            // İstatistikleri güncelle;
            foreach (var areaData in _areaData.Values)
            {
                await _presenceAnalyzer.UpdateStatisticsAsync(areaData);
            }
        }

        private async Task CheckSystemHealthAsync()
        {
            var healthStatus = new DetectionSystemHealth;
            {
                Timestamp = DateTime.UtcNow,
                AreaCount = _areaData.Count,
                ActiveDetectionTasks = _areaDetectionTasks.Count,
                QueueSize = _detectionQueue.Count,
                EngineStatus = GetDetectionEngineStatus()
            };

            // Sağlık kontrolü event'i yayınla;
            var @event = new SystemHealthCheckEvent;
            {
                HealthStatus = healthStatus,
                Timestamp = DateTime.UtcNow;
            };

            await _eventBus.PublishAsync(@event);
        }

        private void HandleMotionDetection(MotionDetectedEvent @event)
        {
            // Hareket tespiti event'ini işle;
            Task.Run(async () =>
            {
                var areaId = @event.AreaId;
                if (!_areaData.TryGetValue(areaId, out var areaData))
                    return;

                var detection = new PresenceDetection;
                {
                    Id = Guid.NewGuid(),
                    AreaId = areaId,
                    Timestamp = @event.Timestamp,
                    HasDetection = true,
                    DetectionMethods = new List<DetectionMethod> { DetectionMethod.MotionSensors },
                    Confidence = @event.Confidence,
                    Metadata = new Dictionary<string, object>
                    {
                        ["motion_intensity"] = @event.Intensity,
                        ["sensor_id"] = @event.SensorId;
                    }
                };

                areaData.Detections.Add(detection);
                areaData.LastDetectionTime = detection.Timestamp;

                _logger.LogDebug($"Motion detected in area {areaId}, intensity: {@event.Intensity}");
            });
        }

        private void HandleFaceDetection(FaceDetectedEvent @event)
        {
            // Yüz tespiti event'ini işle;
            Task.Run(async () =>
            {
                var areaId = @event.AreaId;
                if (!_areaData.TryGetValue(areaId, out var areaData))
                    return;

                var detection = new PresenceDetection;
                {
                    Id = Guid.NewGuid(),
                    AreaId = areaId,
                    Timestamp = @event.Timestamp,
                    HasDetection = true,
                    DetectionMethods = new List<DetectionMethod> { DetectionMethod.FaceRecognition },
                    DetectedFaces = @event.Faces,
                    Confidence = @event.Confidence,
                    Metadata = new Dictionary<string, object>
                    {
                        ["face_count"] = @event.Faces.Count,
                        ["camera_id"] = @event.CameraId;
                    }
                };

                areaData.Detections.Add(detection);
                areaData.LastDetectionTime = detection.Timestamp;

                _logger.LogDebug($"{@event.Faces.Count} faces detected in area {areaId}");
            });
        }

        private void HandleCameraStatus(CameraStatusEvent @event)
        {
            // Kamera durumu event'ini işle;
            if (!@event.IsActive)
            {
                _logger.LogWarning($"Camera {@event.CameraId} is inactive: {@event.StatusMessage}");
            }
        }

        #endregion;

        #region Internal Supporting Classes;

        /// <summary>
        /// Tespit motoru.
        /// </summary>
        internal class DetectionEngine : IDisposable
        {
            private PresenceDetectionConfig _config;
            private bool _initialized;

            public void Initialize(PresenceDetectionConfig config)
            {
                _config = config;
                _initialized = true;
            }

            public void Shutdown()
            {
                _initialized = false;
            }

            public void Dispose()
            {
                Shutdown();
            }
        }

        /// <summary>
        /// Alan yöneticisi.
        /// </summary>
        internal class AreaManager;
        {
            private readonly Dictionary<string, MonitoringArea> _areas;

            public AreaManager()
            {
                _areas = new Dictionary<string, MonitoringArea>();
            }

            public void RegisterArea(MonitoringArea area)
            {
                _areas[area.Id] = area;
            }

            public MonitoringArea GetArea(string areaId)
            {
                return _areas.TryGetValue(areaId, out var area) ? area : null;
            }

            public List<MonitoringArea> GetAllAreas()
            {
                return _areas.Values.ToList();
            }
        }

        /// <summary>
        /// Varlık analiz motoru.
        /// </summary>
        internal class PresenceAnalyzer;
        {
            public async Task<Dictionary<int, double>> AnalyzeHourlyTrendsAsync(
                AreaPresenceData areaData, DateTime startDate, DateTime endDate)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var hourlyData = new Dictionary<int, double>();
                var relevantDetections = areaData.Detections;
                    .Where(d => d.Timestamp >= startDate && d.Timestamp <= endDate)
                    .ToList();

                for (int hour = 0; hour < 24; hour++)
                {
                    var hourDetections = relevantDetections;
                        .Where(d => d.Timestamp.Hour == hour)
                        .ToList();

                    hourlyData[hour] = hourDetections.Any() ?
                        hourDetections.Average(d => d.Confidence) : 0.0;
                }

                return hourlyData;
            }

            public async Task<Dictionary<DayOfWeek, double>> AnalyzeDailyPatternsAsync(
                AreaPresenceData areaData, DateTime startDate, DateTime endDate)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var dailyPatterns = new Dictionary<DayOfWeek, double>();
                var relevantDetections = areaData.Detections;
                    .Where(d => d.Timestamp >= startDate && d.Timestamp <= endDate)
                    .ToList();

                foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
                {
                    var dayDetections = relevantDetections;
                        .Where(d => d.Timestamp.DayOfWeek == day)
                        .ToList();

                    dailyPatterns[day] = dayDetections.Any() ?
                        dayDetections.Average(d => d.Confidence) : 0.0;
                }

                return dailyPatterns;
            }

            public async Task<Dictionary<DayOfWeek, Dictionary<int, double>>> AnalyzeWeeklyTrendsAsync(
                AreaPresenceData areaData, DateTime startDate, DateTime endDate)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var weeklyTrends = new Dictionary<DayOfWeek, Dictionary<int, double>>();

                foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
                {
                    weeklyTrends[day] = new Dictionary<int, double>();

                    for (int hour = 0; hour < 24; hour++)
                    {
                        var hourDetections = areaData.Detections;
                            .Where(d => d.Timestamp >= startDate && d.Timestamp <= endDate &&
                                       d.Timestamp.DayOfWeek == day &&
                                       d.Timestamp.Hour == hour)
                            .ToList();

                        weeklyTrends[day][hour] = hourDetections.Any() ?
                            hourDetections.Average(d => d.Confidence) : 0.0;
                    }
                }

                return weeklyTrends;
            }

            public async Task<TimeSpan> CalculateAveragePresenceDurationAsync(AreaPresenceData areaData)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var detections = areaData.Detections;
                    .Where(d => d.HasDetection)
                    .OrderBy(d => d.Timestamp)
                    .ToList();

                if (detections.Count < 2) return TimeSpan.Zero;

                var durations = new List<TimeSpan>();
                DateTime? sessionStart = null;

                foreach (var detection in detections)
                {
                    if (!sessionStart.HasValue)
                    {
                        sessionStart = detection.Timestamp;
                    }
                    else if ((detection.Timestamp - sessionStart.Value) > TimeSpan.FromMinutes(5))
                    {
                        // Yeni oturum;
                        durations.Add(detection.Timestamp - sessionStart.Value);
                        sessionStart = detection.Timestamp;
                    }
                }

                return durations.Any() ? TimeSpan.FromTicks((long)durations.Average(d => d.Ticks)) : TimeSpan.Zero;
            }

            public async Task<TimeSpan> CalculateAverageDurationInRangeAsync(
                AreaPresenceData areaData, DateTime start, DateTime end)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;
                return TimeSpan.FromMinutes(15); // Örnek değer;
            }

            public async Task<List<PeakTime>> CalculatePeakTimesAsync(AreaPresenceData areaData, TimeRange timeRange)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;
                return new List<PeakTime>();
            }

            public async Task<Dictionary<DetectionMethod, int>> GetDetectionMethodStatsAsync(
                AreaPresenceData areaData, TimeRange timeRange)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var stats = new Dictionary<DetectionMethod, int>();
                var relevantDetections = areaData.Detections;
                    .Where(d => d.Timestamp >= timeRange.Start && d.Timestamp <= timeRange.End)
                    .ToList();

                foreach (var detection in relevantDetections)
                {
                    foreach (var method in detection.DetectionMethods)
                    {
                        if (!stats.ContainsKey(method))
                            stats[method] = 0;
                        stats[method]++;
                    }
                }

                return stats;
            }

            public async Task<AreaStatistic> GetAreaStatisticsAsync(AreaPresenceData areaData, TimeRange timeRange)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                var relevantDetections = areaData.Detections;
                    .Where(d => d.Timestamp >= timeRange.Start && d.Timestamp <= timeRange.End)
                    .ToList();

                return new AreaStatistic;
                {
                    AreaId = areaData.AreaId,
                    DetectionCount = relevantDetections.Count,
                    PresenceCount = relevantDetections.Count(d => d.HasDetection),
                    AverageConfidence = relevantDetections.Any() ?
                        relevantDetections.Average(d => d.Confidence) : 0.0;
                };
            }

            public async Task UpdateStatisticsAsync(AreaPresenceData areaData)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;
                // İstatistik güncelleme işlemleri;
            }
        }

        /// <summary>
        /// Anomali dedektörü.
        /// </summary>
        internal class AnomalyDetector;
        {
            public async Task<List<PresenceAnomaly>> DetectAnomaliesAsync(AreaPresenceData areaData)
            {
                await Task.Delay(20); // Simüle edilmiş işlem;
                return new List<PresenceAnomaly>();
            }

            public async Task<AnomalyCheckResult> CheckDetectionAnomalyAsync(
                PresenceDetection detection, AreaPresenceData areaData)
            {
                await Task.Delay(10); // Simüle edilmiş işlem;

                // Basit anomali kontrolü;
                var recentDetections = areaData.Detections;
                    .Where(d => d.Timestamp > DateTime.UtcNow.AddHours(-1))
                    .ToList();

                var anomalyCount = recentDetections.Count(d => d.Confidence > 0.9);

                return new AnomalyCheckResult;
                {
                    IsAnomaly = anomalyCount > 10,
                    AnomalyType = anomalyCount > 10 ? "HighFrequency" : "None",
                    Severity = anomalyCount > 10 ? AnomalySeverity.Medium : AnomalySeverity.None,
                    Reason = anomalyCount > 10 ? "Unusual high frequency of detections" : "Normal"
                };
            }
        }

        /// <summary>
        /// Veri dışa aktarıcı.
        /// </summary>
        internal class DataExporter : IDisposable
        {
            public async Task<byte[]> ExportAsync(PresenceExportData data, ExportFormat format)
            {
                await Task.Delay(50); // Simüle edilmiş işlem;

                switch (format)
                {
                    case ExportFormat.Json:
                        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                    case ExportFormat.Csv:
                        return GenerateCsv(data);

                    case ExportFormat.Xml:
                        return GenerateXml(data);

                    default:
                        throw new NotSupportedException($"Export format {format} not supported");
                }
            }

            private byte[] GenerateCsv(PresenceExportData data)
            {
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.IO.StreamWriter(stream);

                writer.WriteLine("AreaId,AreaName,DetectionCount,LastDetection");

                foreach (var area in data.Areas)
                {
                    writer.WriteLine($"{area.AreaId},{area.Name},{area.Detections.Count},{area.LastDetectionTime:o}");
                }

                writer.Flush();
                return stream.ToArray();
            }

            private byte[] GenerateXml(PresenceExportData data)
            {
                var xml = new System.Xml.XmlDocument();
                var root = xml.CreateElement("PresenceData");
                xml.AppendChild(root);

                var exportTime = xml.CreateElement("ExportTime");
                exportTime.InnerText = data.ExportTime.ToString("o");
                root.AppendChild(exportTime);

                var areas = xml.CreateElement("Areas");
                root.AppendChild(areas);

                foreach (var area in data.Areas)
                {
                    var areaElement = xml.CreateElement("Area");
                    areaElement.SetAttribute("Id", area.AreaId);
                    areaElement.SetAttribute("Name", area.Name);
                    areaElement.SetAttribute("DetectionCount", area.Detections.Count.ToString());
                    areas.AppendChild(areaElement);
                }

                using var stream = new System.IO.MemoryStream();
                xml.Save(stream);
                return stream.ToArray();
            }

            public void Dispose()
            {
                // Kaynak temizleme;
            }
        }

        #endregion;
    }

    #region Public Models and Enums;

    /// <summary>
    /// Varlık tespit konfigürasyonu.
    /// </summary>
    public class PresenceDetectionConfig;
    {
        public List<MonitoringArea> Areas { get; set; } = new List<MonitoringArea>();
        public List<DetectionMethod> DetectionMethods { get; set; } = new List<DetectionMethod>();
        public TimeSpan DetectionInterval { get; set; } = TimeSpan.FromSeconds(2);
        public double MotionThreshold { get; set; } = 0.3;
        public double ConfidenceThreshold { get; set; } = 0.5;
        public VisionConfig VisionConfig { get; set; }
        public int DataRetentionDays { get; set; } = 30;
        public Dictionary<string, object> AdvancedSettings { get; set; }
    }

    /// <summary>
    /// İzleme alanı.
    /// </summary>
    public class MonitoringArea;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public AreaType Type { get; set; }
        public List<SensorInfo> Sensors { get; set; } = new List<SensorInfo>();
        public Dictionary<string, object> Settings { get; set; }
        public GeoLocation Location { get; set; }
        public List<string> AuthorizedPersonnel { get; set; }
    }

    /// <summary>
    /// Varlık durumu.
    /// </summary>
    public class PresenceStatus;
    {
        public string AreaId { get; set; }
        public string AreaName { get; set; }
        public bool IsActive { get; set; }
        public DateTime? LastDetectionTime { get; set; }
        public int CurrentCount { get; set; }
        public int DetectionCount5Min { get; set; }
        public TimeSpan AveragePresenceDuration { get; set; }
        public double Confidence { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Alan varlık durumu.
    /// </summary>
    public class AreaPresenceStatus;
    {
        public string AreaId { get; set; }
        public PresenceStatus Status { get; set; }
        public bool IsMonitored { get; set; }
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Varlık istatistikleri.
    /// </summary>
    public class PresenceStatistics;
    {
        public TimeRange TimeRange { get; set; }
        public int TotalAreas { get; set; }
        public int ActiveAreas { get; set; }
        public int TotalDetections { get; set; }
        public TimeSpan AveragePresenceDuration { get; set; }
        public List<PeakTime> PeakTimes { get; set; }
        public Dictionary<DetectionMethod, int> DetectionMethods { get; set; }
        public List<AreaStatistic> Areas { get; set; }
    }

    /// <summary>
    /// Varlık trendi.
    /// </summary>
    public class PresenceTrend;
    {
        public string AreaId { get; set; }
        public int AnalysisPeriod { get; set; }
        public Dictionary<int, double> HourlyData { get; set; }
        public Dictionary<DayOfWeek, double> DailyPatterns { get; set; }
        public Dictionary<DayOfWeek, Dictionary<int, double>> WeeklyTrends { get; set; }
        public List<PeakHour> PeakHours { get; set; }
        public List<BusiestDay> BusiestDays { get; set; }
        public TrendDirection TrendDirection { get; set; }
        public double TrendStrength { get; set; }
    }

    /// <summary>
    /// Varlık anomalisi.
    /// </summary>
    public class PresenceAnomaly;
    {
        public string AreaId { get; set; }
        public AnomalyType Type { get; set; }
        public AnomalySeverity Severity { get; set; }
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Details { get; set; }
    }

    /// <summary>
    /// Alan varlık verisi.
    /// </summary>
    public class AreaPresenceData;
    {
        public string AreaId { get; set; }
        public string Name { get; set; }
        public AreaType Type { get; set; }
        public List<SensorInfo> Sensors { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastDetectionTime { get; set; }
        public List<PresenceDetection> Detections { get; set; }
        public AreaStatistics Statistics { get; set; }
    }

    /// <summary>
    /// Tespit sonucu.
    /// </summary>
    public class DetectionResult;
    {
        public string AreaId { get; set; }
        public DateTime Timestamp { get; set; }
        public bool HasDetection { get; set; }
        public List<DetectionMethod> DetectionMethods { get; set; }
        public List<DetectedObject> DetectedObjects { get; set; }
        public List<DetectedFace> DetectedFaces { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> SensorData { get; set; }
    }

    /// <summary>
    /// Varlık tespiti.
    /// </summary>
    public class PresenceDetection;
    {
        public Guid Id { get; set; }
        public string AreaId { get; set; }
        public DateTime Timestamp { get; set; }
        public bool HasDetection { get; set; }
        public List<DetectionMethod> DetectionMethods { get; set; }
        public List<DetectedObject> DetectedObjects { get; set; }
        public List<DetectedFace> DetectedFaces { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Tespit motoru durumu.
    /// </summary>
    public class PresenceDetectorStatus;
    {
        public bool IsRunning { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActiveAreas { get; set; }
        public int TotalDetections { get; set; }
        public int QueueSize { get; set; }
        public Dictionary<string, DetectionEngineStatus> DetectionEngines { get; set; }
        public DateTime LastAnalysisTime { get; set; }
    }

    /// <summary>
    /// Sensör bilgisi.
    /// </summary>
    public class SensorInfo;
    {
        public string Id { get; set; }
        public SensorType Type { get; set; }
        public string Name { get; set; }
        public string Model { get; set; }
        public Dictionary<string, object> Specifications { get; set; }
        public DateTime LastCalibration { get; set; }
        public bool IsActive { get; set; }
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
    /// Tepe zamanı.
    /// </summary>
    public class PeakTime;
    {
        public int Hour { get; set; }
        public int AverageCount { get; set; }
        public int AreaCount { get; set; }
    }

    /// <summary>
    /// Alan istatistiği.
    /// </summary>
    public class AreaStatistic;
    {
        public string AreaId { get; set; }
        public int DetectionCount { get; set; }
        public int PresenceCount { get; set; }
        public double AverageConfidence { get; set; }
    }

    /// <summary>
    /// Tepe saati.
    /// </summary>
    public class PeakHour;
    {
        public int Hour { get; set; }
        public double AveragePresence { get; set; }
    }

    /// <summary>
    /// En yoğun gün.
    /// </summary>
    public class BusiestDay;
    {
        public DayOfWeek Day { get; set; }
        public double AveragePresence { get; set; }
    }

    /// <summary>
    /// Alan istatistikleri.
    /// </summary>
    public class AreaStatistics;
    {
        public int TotalDetections { get; set; }
        public int PresenceDetections { get; set; }
        public int AbsenceDetections { get; set; }
        public DateTime? LastPresenceTime { get; set; }
        public Dictionary<DetectionMethod, int> DetectionMethodCounts { get; set; } = new Dictionary<DetectionMethod, int>();
        public Dictionary<int, int> HourlyCounts { get; set; } = new Dictionary<int, int>();
    }

    /// <summary>
    /// Görüntü analizi sonucu.
    /// </summary>
    public class VideoAnalysisResult;
    {
        public bool HasDetection { get; set; }
        public List<DetectedObject> DetectedObjects { get; set; }
        public double Confidence { get; set; }
        public byte[] FrameData { get; set; }
    }

    /// <summary>
    /// Hareket tespiti sonucu.
    /// </summary>
    public class MotionDetectionResult;
    {
        public bool HasDetection { get; set; }
        public List<SensorReading> SensorReadings { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Yüz tespiti sonucu.
    /// </summary>
    public class FaceDetectionResult;
    {
        public bool HasDetection { get; set; }
        public List<DetectedFace> DetectedFaces { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Tespit event'i.
    /// </summary>
    public class DetectionEvent;
    {
        public PresenceDetection Detection { get; set; }
        public AreaPresenceData AreaData { get; set; }
    }

    /// <summary>
    /// Varlık dışa aktarma verisi.
    /// </summary>
    public class PresenceExportData;
    {
        public List<AreaPresenceData> Areas { get; set; }
        public DateTime ExportTime { get; set; }
        public PresenceDetectionConfig Config { get; set; }
    }

    /// <summary>
    /// Tespit motoru durumu.
    /// </summary>
    public class DetectionEngineStatus;
    {
        public bool IsActive { get; set; }
        public DateTime LastActivity { get; set; }
        public Dictionary<string, object> Metrics { get; set; }
    }

    /// <summary>
    /// Tespit sistemi sağlığı.
    /// </summary>
    public class DetectionSystemHealth;
    {
        public DateTime Timestamp { get; set; }
        public int AreaCount { get; set; }
        public int ActiveDetectionTasks { get; set; }
        public int QueueSize { get; set; }
        public Dictionary<string, DetectionEngineStatus> EngineStatus { get; set; }
        public Dictionary<string, object> HealthMetrics { get; set; }
    }

    /// <summary>
    /// Anomali kontrol sonucu.
    /// </summary>
    public class AnomalyCheckResult;
    {
        public bool IsAnomaly { get; set; }
        public string AnomalyType { get; set; }
        public AnomalySeverity Severity { get; set; }
        public string Reason { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Tespit metodu.
    /// </summary>
    public enum DetectionMethod;
    {
        VideoAnalysis,
        MotionSensors,
        ThermalImaging,
        FaceRecognition,
        SoundDetection,
        Radar;
    }

    /// <summary>
    /// Alan türü.
    /// </summary>
    public enum AreaType;
    {
        Room,
        Corridor,
        Entrance,
        Office,
        Warehouse,
        Outdoor,
        RestrictedArea;
    }

    /// <summary>
    /// Sensör türü.
    /// </summary>
    public enum SensorType;
    {
        Camera,
        Motion,
        Thermal,
        Proximity,
        Acoustic,
        Radar,
        Lidar;
    }

    /// <summary>
    /// Dışa aktarma formatı.
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Csv,
        Xml;
    }

    /// <summary>
    /// Trend yönü.
    /// </summary>
    public enum TrendDirection;
    {
        Increasing,
        Decreasing,
        Stable,
        Fluctuating;
    }

    /// <summary>
    /// Anomali türü.
    /// </summary>
    public enum AnomalyType;
    {
        None,
        HighFrequency,
        UnusualTime,
        UnauthorizedPresence,
        ProlongedPresence,
        MultiplePeople,
        EquipmentTampering;
    }

    /// <summary>
    /// Anomali şiddeti.
    /// </summary>
    public enum AnomalySeverity;
    {
        None,
        Low,
        Medium,
        High,
        Critical;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Tespit başlatıldı event'i.
    /// </summary>
    public class DetectionStartedEvent : IEvent;
    {
        public DateTime StartTime { get; set; }
        public int AreaCount { get; set; }
        public List<DetectionMethod> DetectionMethods { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tespit durduruldu event'i.
    /// </summary>
    public class DetectionStoppedEvent : IEvent;
    {
        public DateTime StopTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int TotalDetections { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Varlık tespit edildi event'i.
    /// </summary>
    public class PresenceDetectedEvent : IEvent;
    {
        public string AreaId { get; set; }
        public string AreaName { get; set; }
        public PresenceDetection Detection { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hareket tespit edildi event'i.
    /// </summary>
    public class MotionDetectedEvent : IEvent;
    {
        public string AreaId { get; set; }
        public string SensorId { get; set; }
        public double Intensity { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Yüz tespit edildi event'i.
    /// </summary>
    public class FaceDetectedEvent : IEvent;
    {
        public string AreaId { get; set; }
        public string CameraId { get; set; }
        public List<DetectedFace> Faces { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kamera durumu event'i.
    /// </summary>
    public class CameraStatusEvent : IEvent;
    {
        public string CameraId { get; set; }
        public bool IsActive { get; set; }
        public string StatusMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Anomaliler tespit edildi event'i.
    /// </summary>
    public class AnomaliesDetectedEvent : IEvent;
    {
        public int AnomalyCount { get; set; }
        public int HighSeverityCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Varlık anomalisi tespit edildi event'i.
    /// </summary>
    public class PresenceAnomalyDetectedEvent : IEvent;
    {
        public string AreaId { get; set; }
        public PresenceDetection Detection { get; set; }
        public AnomalyType AnomalyType { get; set; }
        public AnomalySeverity Severity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sistem sağlık kontrolü event'i.
    /// </summary>
    public class SystemHealthCheckEvent : IEvent;
    {
        public DetectionSystemHealth HealthStatus { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}
