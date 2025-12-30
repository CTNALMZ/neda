using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Biometrics.BehaviorAnalysis;
using NEDA.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.MetricsCollector;
using NEDA.RealTimeMonitoring;

namespace NEDA.Biometrics.MotionTracking;
{
    /// <summary>
    /// Hareket ve aktivite izleme sistemini yönetir;
    /// Gerçek zamanlı aktivite analizi ve davranış takibi yapar;
    /// </summary>
    public class ActivityMonitor : IActivityMonitor, IDisposable;
    {
        private readonly ILogger<ActivityMonitor> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IBehaviorAnalyzer _behaviorAnalyzer;
        private readonly ConcurrentDictionary<string, ActivitySession> _activeSessions;
        private readonly ConcurrentDictionary<string, MotionData> _motionDataBuffer;
        private readonly System.Timers.Timer _monitoringTimer;
        private readonly System.Timers.Timer _analysisTimer;
        private readonly SemaphoreSlim _processingLock = new(1, 1);
        private readonly List<ActivityPattern> _knownPatterns;
        private MotionSensorConfig _currentConfig;
        private bool _isMonitoring;
        private bool _isDisposed;

        /// <summary>
        /// Aktivite verisi mevcut olduğunda tetiklenen olay;
        /// </summary>
        public event EventHandler<ActivityDataEventArgs> ActivityDataUpdated;

        /// <summary>
        /// Anormal aktivite tespit edildiğinde tetiklenen olay;
        /// </summary>
        public event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;

        /// <summary>
        /// Aktivite modu değiştiğinde tetiklenen olay;
        /// </summary>
        public event EventHandler<ActivityModeChangedEventArgs> ActivityModeChanged;

        /// <summary>
        /// Mevcut aktivite istatistikleri;
        /// </summary>
        public ActivityStatistics CurrentStatistics { get; private set; }

        /// <summary>
        /// Aktivite izleme durumu;
        /// </summary>
        public MonitorStatus Status { get; private set; }

        /// <summary>
        /// ActivityMonitor constructor;
        /// </summary>
        public ActivityMonitor(
            ILogger<ActivityMonitor> logger,
            IExceptionHandler exceptionHandler,
            IMetricsCollector metricsCollector,
            IDiagnosticTool diagnosticTool,
            IBehaviorAnalyzer behaviorAnalyzer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _behaviorAnalyzer = behaviorAnalyzer ?? throw new ArgumentNullException(nameof(behaviorAnalyzer));

            _activeSessions = new ConcurrentDictionary<string, ActivitySession>();
            _motionDataBuffer = new ConcurrentDictionary<string, MotionData>();
            _knownPatterns = LoadKnownActivityPatterns();
            CurrentStatistics = new ActivityStatistics();
            Status = MonitorStatus.Stopped;

            // İzleme zamanlayıcısını ayarla (100ms aralıklarla)
            _monitoringTimer = new System.Timers.Timer(100);
            _monitoringTimer.Elapsed += OnMonitoringTimerElapsed;

            // Analiz zamanlayıcısını ayarla (1 saniye aralıklarla)
            _analysisTimer = new System.Timers.Timer(1000);
            _analysisTimer.Elapsed += OnAnalysisTimerElapsed;

            _logger.LogInformation("ActivityMonitor initialized");
        }

        /// <summary>
        /// Aktivite izlemeyi belirtilen konfigürasyonla başlatır;
        /// </summary>
        public async Task<MonitoringResult> StartMonitoringAsync(MotionSensorConfig config, CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (_isMonitoring)
                {
                    _logger.LogWarning("Activity monitoring is already running");
                    return MonitoringResult.AlreadyRunning();
                }

                _logger.LogInformation("Starting activity monitoring...");

                // Konfigürasyon validasyonu;
                if (config == null)
                    throw new ArgumentNullException(nameof(config));

                if (!ValidateConfig(config))
                    return MonitoringResult.Failure("Invalid motion sensor configuration");

                _currentConfig = config;

                // Sensörleri başlat;
                var sensorTasks = config.EnabledSensors.Select(sensorType =>
                    InitializeSensorAsync(sensorType, config.SensorConfigurations[sensorType], cancellationToken));

                await Task.WhenAll(sensorTasks);

                // Zamanlayıcıları başlat;
                _monitoringTimer.Interval = config.SamplingIntervalMs;
                _monitoringTimer.Start();
                _analysisTimer.Start();

                _isMonitoring = true;
                Status = MonitorStatus.Running;
                CurrentStatistics.StartTime = DateTime.UtcNow;

                // Metrikleri başlat;
                await _metricsCollector.StartCollectingAsync("activity_monitoring", cancellationToken);

                _logger.LogInformation($"Activity monitoring started with {config.EnabledSensors.Count} sensors");
                return MonitoringResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ActivityMonitor.StartMonitoring");
                _logger.LogError(ex, "Failed to start activity monitoring: {Error}", error.Message);
                Status = MonitorStatus.Error;
                return MonitoringResult.Failure($"Failed to start monitoring: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Aktivite izlemeyi durdurur;
        /// </summary>
        public async Task<MonitoringResult> StopMonitoringAsync(CancellationToken cancellationToken = default)
        {
            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isMonitoring)
                {
                    _logger.LogWarning("Activity monitoring is not running");
                    return MonitoringResult.NotRunning();
                }

                _logger.LogInformation("Stopping activity monitoring...");

                // Zamanlayıcıları durdur;
                _monitoringTimer.Stop();
                _analysisTimer.Stop();

                // Tüm sensörleri kapat;
                await StopAllSensorsAsync(cancellationToken);

                // Aktif oturumları temizle;
                _activeSessions.Clear();
                _motionDataBuffer.Clear();

                _isMonitoring = false;
                Status = MonitorStatus.Stopped;
                CurrentStatistics.EndTime = DateTime.UtcNow;

                // Metrikleri durdur;
                await _metricsCollector.StopCollectingAsync("activity_monitoring", cancellationToken);

                // İstatistikleri kaydet;
                await SaveStatisticsAsync(cancellationToken);

                _logger.LogInformation("Activity monitoring stopped");
                return MonitoringResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ActivityMonitor.StopMonitoring");
                _logger.LogError(ex, "Error stopping activity monitoring: {Error}", error.Message);
                return MonitoringResult.Failure($"Error stopping monitoring: {error.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Hareket verisini işler ve analiz eder;
        /// </summary>
        public async Task<MotionAnalysisResult> ProcessMotionDataAsync(MotionData motionData, CancellationToken cancellationToken = default)
        {
            if (motionData == null)
                throw new ArgumentNullException(nameof(motionData));

            try
            {
                // Veriyi buffer'a ekle;
                var dataKey = $"{motionData.SensorId}_{DateTime.UtcNow.Ticks}";
                _motionDataBuffer[dataKey] = motionData;

                // İstatistikleri güncelle;
                UpdateStatistics(motionData);

                // Aktivite modunu analiz et;
                var activityMode = AnalyzeActivityMode(motionData);

                // Davranış analizi yap;
                var behaviorAnalysis = await _behaviorAnalyzer.AnalyzeBehaviorAsync(motionData, cancellationToken);

                // Anormallik kontrolü;
                var anomalyResult = await CheckForAnomaliesAsync(motionData, cancellationToken);
                if (anomalyResult.HasAnomaly)
                {
                    OnAnomalyDetected(new AnomalyDetectedEventArgs;
                    {
                        MotionData = motionData,
                        AnomalyType = anomalyResult.AnomalyType,
                        Severity = anomalyResult.Severity,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Aktivite verisini oluştur;
                var activityData = new ActivityData;
                {
                    Timestamp = DateTime.UtcNow,
                    MotionData = motionData,
                    ActivityMode = activityMode,
                    Intensity = CalculateIntensity(motionData),
                    CaloriesBurned = CalculateCalories(motionData, activityMode),
                    BehaviorAnalysis = behaviorAnalysis,
                    AnomalyDetected = anomalyResult.HasAnomaly;
                };

                // Olayı tetikle;
                OnActivityDataUpdated(new ActivityDataEventArgs;
                {
                    ActivityData = activityData,
                    SessionId = motionData.SessionId;
                });

                // Aktivite modu değiştiyse tetikle;
                if (CurrentStatistics.CurrentActivityMode != activityMode)
                {
                    var previousMode = CurrentStatistics.CurrentActivityMode;
                    CurrentStatistics.CurrentActivityMode = activityMode;

                    OnActivityModeChanged(new ActivityModeChangedEventArgs;
                    {
                        PreviousMode = previousMode,
                        NewMode = activityMode,
                        Timestamp = DateTime.UtcNow,
                        Trigger = motionData.ActivityType;
                    });
                }

                // Metrikleri güncelle;
                await _metricsCollector.RecordMetricAsync(
                    "motion_data_processed",
                    1,
                    new Dictionary<string, object>
                    {
                        ["sensor_id"] = motionData.SensorId,
                        ["activity_mode"] = activityMode.ToString(),
                        ["intensity"] = activityData.Intensity;
                    },
                    cancellationToken);

                return new MotionAnalysisResult;
                {
                    Success = true,
                    ActivityData = activityData,
                    AnomalyDetected = anomalyResult.HasAnomaly;
                };
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ActivityMonitor.ProcessMotionData");
                _logger.LogError(ex, "Error processing motion data: {Error}", error.Message);
                return MotionAnalysisResult.Failure($"Error processing motion data: {error.Message}");
            }
        }

        /// <summary>
        /// Belirli bir zaman aralığındaki aktivite verilerini getirir;
        /// </summary>
        public async Task<IEnumerable<ActivityData>> GetActivityHistoryAsync(
            DateTime startTime,
            DateTime endTime,
            ActivityFilter filter = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation($"Retrieving activity history from {startTime} to {endTime}");

                // Filtre uygula;
                var filteredData = await LoadActivityDataAsync(startTime, endTime, cancellationToken);

                if (filter != null)
                {
                    filteredData = ApplyFilter(filteredData, filter);
                }

                // Verileri analiz et;
                await AnalyzeActivityPatternsAsync(filteredData, cancellationToken);

                return filteredData;
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ActivityMonitor.GetActivityHistory");
                _logger.LogError(ex, "Error retrieving activity history: {Error}", error.Message);
                throw;
            }
        }

        /// <summary>
        /// Aktivite oturumu oluşturur;
        /// </summary>
        public async Task<ActivitySession> CreateActivitySessionAsync(
            string sessionName,
            ActivityType activityType,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var sessionId = Guid.NewGuid().ToString();
                var session = new ActivitySession;
                {
                    SessionId = sessionId,
                    SessionName = sessionName,
                    ActivityType = activityType,
                    StartTime = DateTime.UtcNow,
                    Status = SessionStatus.Active,
                    Metrics = new SessionMetrics()
                };

                if (_activeSessions.TryAdd(sessionId, session))
                {
                    _logger.LogInformation($"Created activity session: {sessionName} ({sessionId})");

                    // Oturum başlangıç metriklerini kaydet;
                    await _metricsCollector.RecordMetricAsync(
                        "activity_session_started",
                        1,
                        new Dictionary<string, object>
                        {
                            ["session_id"] = sessionId,
                            ["activity_type"] = activityType.ToString(),
                            ["session_name"] = sessionName;
                        },
                        cancellationToken);

                    return session;
                }

                throw new InvalidOperationException("Failed to create activity session");
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ActivityMonitor.CreateActivitySession");
                _logger.LogError(ex, "Error creating activity session: {Error}", error.Message);
                throw;
            }
        }

        /// <summary>
        /// Aktivite oturumunu sonlandırır;
        /// </summary>
        public async Task<SessionResult> EndActivitySessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    return SessionResult.Failure("Session not found");
                }

                session.EndTime = DateTime.UtcNow;
                session.Status = SessionStatus.Completed;
                session.Metrics.Duration = session.EndTime - session.StartTime;

                // Oturum metriklerini hesapla;
                await CalculateSessionMetricsAsync(session, cancellationToken);

                // Oturumu arşivle;
                await ArchiveSessionAsync(session, cancellationToken);

                _activeSessions.TryRemove(sessionId, out _);

                _logger.LogInformation($"Ended activity session: {session.SessionName} ({sessionId})");

                // Oturum bitiş metriklerini kaydet;
                await _metricsCollector.RecordMetricAsync(
                    "activity_session_ended",
                    1,
                    new Dictionary<string, object>
                    {
                        ["session_id"] = sessionId,
                        ["duration_seconds"] = session.Metrics.Duration.TotalSeconds,
                        ["calories_burned"] = session.Metrics.TotalCalories;
                    },
                    cancellationToken);

                return SessionResult.Success(session);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "ActivityMonitor.EndActivitySession");
                _logger.LogError(ex, "Error ending activity session: {Error}", error.Message);
                return SessionResult.Failure($"Error ending session: {error.Message}");
            }
        }

        /// <summary>
        /// Sistem durumunu kontrol eder;
        /// </summary>
        public async Task<SystemHealthStatus> CheckSystemHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var healthStatus = new SystemHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    Component = "ActivityMonitor",
                    OverallStatus = HealthStatus.Healthy;
                };

                // Sensör bağlantılarını kontrol et;
                var sensorHealth = await CheckSensorConnectionsAsync(cancellationToken);
                healthStatus.Subcomponents.Add(sensorHealth);

                // Veri akışını kontrol et;
                var dataFlowHealth = CheckDataFlowHealth();
                healthStatus.Subcomponents.Add(dataFlowHealth);

                // Bellek kullanımını kontrol et;
                var memoryHealth = CheckMemoryHealth();
                healthStatus.Subcomponents.Add(memoryHealth);

                // Genel durumu belirle;
                if (sensorHealth.Status == HealthStatus.Error || dataFlowHealth.Status == HealthStatus.Error)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                }
                else if (sensorHealth.Status == HealthStatus.Warning || dataFlowHealth.Status == HealthStatus.Warning)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                }

                healthStatus.Message = $"Activity Monitor health: {healthStatus.OverallStatus}";

                await _diagnosticTool.LogDiagnosticInfoAsync("activity_monitor_health_check", healthStatus, cancellationToken);

                return healthStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking system health");
                return new SystemHealthStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    Component = "ActivityMonitor",
                    OverallStatus = HealthStatus.Error,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        #region Private Methods;

        private async Task InitializeSensorAsync(SensorType sensorType, SensorConfig config, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation($"Initializing {sensorType} sensor...");

                // Sensör tipine göre başlatma işlemi;
                switch (sensorType)
                {
                    case SensorType.Accelerometer:
                        await InitializeAccelerometerAsync(config, cancellationToken);
                        break;
                    case SensorType.Gyroscope:
                        await InitializeGyroscopeAsync(config, cancellationToken);
                        break;
                    case SensorType.Magnetometer:
                        await InitializeMagnetometerAsync(config, cancellationToken);
                        break;
                    case SensorType.HeartRate:
                        await InitializeHeartRateSensorAsync(config, cancellationToken);
                        break;
                    default:
                        throw new NotSupportedException($"Sensor type {sensorType} is not supported");
                }

                _logger.LogInformation($"{sensorType} sensor initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to initialize {sensorType} sensor");
                throw;
            }
        }

        private async Task InitializeAccelerometerAsync(SensorConfig config, CancellationToken cancellationToken)
        {
            // Accelerometer başlatma mantığı;
            await Task.Delay(100, cancellationToken); // Simülasyon;

            _logger.LogDebug($"Accelerometer initialized with sensitivity: {config.Sensitivity}");

            await _metricsCollector.RecordMetricAsync(
                "sensor_initialized",
                1,
                new Dictionary<string, object> { ["sensor_type"] = "accelerometer" },
                cancellationToken);
        }

        private async Task InitializeGyroscopeAsync(SensorConfig config, CancellationToken cancellationToken)
        {
            // Gyroscope başlatma mantığı;
            await Task.Delay(100, cancellationToken); // Simülasyon;

            _logger.LogDebug($"Gyroscope initialized with sampling rate: {config.SamplingRate}Hz");

            await _metricsCollector.RecordMetricAsync(
                "sensor_initialized",
                1,
                new Dictionary<string, object> { ["sensor_type"] = "gyroscope" },
                cancellationToken);
        }

        private async Task InitializeMagnetometerAsync(SensorConfig config, CancellationToken cancellationToken)
        {
            // Magnetometer başlatma mantığı;
            await Task.Delay(100, cancellationToken); // Simülasyon;

            _logger.LogDebug($"Magnetometer initialized with calibration: {config.CalibrationMode}");

            await _metricsCollector.RecordMetricAsync(
                "sensor_initialized",
                1,
                new Dictionary<string, object> { ["sensor_type"] = "magnetometer" },
                cancellationToken);
        }

        private async Task InitializeHeartRateSensorAsync(SensorConfig config, CancellationToken cancellationToken)
        {
            // Kalp atış sensörü başlatma mantığı;
            await Task.Delay(100, cancellationToken); // Simülasyon;

            _logger.LogDebug($"Heart rate sensor initialized with precision: {config.Precision}");

            await _metricsCollector.RecordMetricAsync(
                "sensor_initialized",
                1,
                new Dictionary<string, object> { ["sensor_type"] = "heart_rate" },
                cancellationToken);
        }

        private async Task StopAllSensorsAsync(CancellationToken cancellationToken)
        {
            var stopTasks = _currentConfig.EnabledSensors.Select(sensorType =>
                StopSensorAsync(sensorType, cancellationToken));

            await Task.WhenAll(stopTasks);
            _logger.LogInformation("All sensors stopped");
        }

        private async Task StopSensorAsync(SensorType sensorType, CancellationToken cancellationToken)
        {
            try
            {
                // Sensör tipine göre kapatma işlemi;
                switch (sensorType)
                {
                    case SensorType.Accelerometer:
                        await StopAccelerometerAsync(cancellationToken);
                        break;
                    case SensorType.Gyroscope:
                        await StopGyroscopeAsync(cancellationToken);
                        break;
                    case SensorType.Magnetometer:
                        await StopMagnetometerAsync(cancellationToken);
                        break;
                    case SensorType.HeartRate:
                        await StopHeartRateSensorAsync(cancellationToken);
                        break;
                }

                _logger.LogDebug($"{sensorType} sensor stopped");

                await _metricsCollector.RecordMetricAsync(
                    "sensor_stopped",
                    1,
                    new Dictionary<string, object> { ["sensor_type"] = sensorType.ToString() },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping {sensorType} sensor");
            }
        }

        private async Task StopAccelerometerAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken); // Simülasyon;
        }

        private async Task StopGyroscopeAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken); // Simülasyon;
        }

        private async Task StopMagnetometerAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken); // Simülasyon;
        }

        private async Task StopHeartRateSensorAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken); // Simülasyon;
        }

        private void UpdateStatistics(MotionData motionData)
        {
            CurrentStatistics.TotalMotionDataPoints++;
            CurrentStatistics.LastUpdateTime = DateTime.UtcNow;

            // Aktivite tipine göre istatistik güncelle;
            switch (motionData.ActivityType)
            {
                case ActivityType.Walking:
                    CurrentStatistics.WalkingDuration += TimeSpan.FromMilliseconds(motionData.DurationMs);
                    break;
                case ActivityType.Running:
                    CurrentStatistics.RunningDuration += TimeSpan.FromMilliseconds(motionData.DurationMs);
                    break;
                case ActivityType.Cycling:
                    CurrentStatistics.CyclingDuration += TimeSpan.FromMilliseconds(motionData.DurationMs);
                    break;
                case ActivityType.Swimming:
                    CurrentStatistics.SwimmingDuration += TimeSpan.FromMilliseconds(motionData.DurationMs);
                    break;
            }

            // Kalori hesapla;
            var calories = CalculateCalories(motionData, CurrentStatistics.CurrentActivityMode);
            CurrentStatistics.TotalCaloriesBurned += calories;

            // Mesafe hesapla;
            var distance = CalculateDistance(motionData);
            CurrentStatistics.TotalDistance += distance;

            // Adım sayısı;
            if (motionData.StepCount.HasValue)
            {
                CurrentStatistics.TotalSteps += motionData.StepCount.Value;
            }
        }

        private ActivityMode AnalyzeActivityMode(MotionData motionData)
        {
            // Hareket verilerine göre aktivite modunu belirle;
            var acceleration = motionData.Acceleration;
            var speed = CalculateSpeed(motionData);
            var variance = CalculateMotionVariance(motionData);

            if (speed > 2.5 && variance > 0.8)
                return ActivityMode.Vigorous;
            else if (speed > 1.0 && variance > 0.4)
                return ActivityMode.Moderate;
            else if (speed > 0.5)
                return ActivityMode.Light;
            else;
                return ActivityMode.Sedentary;
        }

        private async Task<AnomalyDetectionResult> CheckForAnomaliesAsync(MotionData motionData, CancellationToken cancellationToken)
        {
            try
            {
                // Hareket verilerinde anormallik kontrolü;
                var accelerationMagnitude = Math.Sqrt(
                    motionData.Acceleration.X * motionData.Acceleration.X +
                    motionData.Acceleration.Y * motionData.Acceleration.Y +
                    motionData.Acceleration.Z * motionData.Acceleration.Z);

                var gyroMagnitude = Math.Sqrt(
                    motionData.Gyroscope.X * motionData.Gyroscope.X +
                    motionData.Gyroscope.Y * motionData.Gyroscope.Y +
                    motionData.Gyroscope.Z * motionData.Gyroscope.Z);

                // Anormallik eşik değerleri;
                var accelerationThreshold = _currentConfig?.AnomalyThresholds?.AccelerationThreshold ?? 15.0;
                var gyroThreshold = _currentConfig?.AnomalyThresholds?.GyroscopeThreshold ?? 20.0;

                var hasAnomaly = accelerationMagnitude > accelerationThreshold || gyroMagnitude > gyroThreshold;

                if (hasAnomaly)
                {
                    var anomalyType = DetermineAnomalyType(motionData);
                    var severity = CalculateAnomalySeverity(accelerationMagnitude, gyroMagnitude);

                    return new AnomalyDetectionResult;
                    {
                        HasAnomaly = true,
                        AnomalyType = anomalyType,
                        Severity = severity,
                        Confidence = CalculateAnomalyConfidence(motionData)
                    };
                }

                // Kalp atış hızı anormalliği;
                if (motionData.HeartRate.HasValue)
                {
                    var hrAnomaly = await CheckHeartRateAnomalyAsync(motionData.HeartRate.Value, cancellationToken);
                    if (hrAnomaly.HasAnomaly)
                    {
                        return hrAnomaly;
                    }
                }

                return new AnomalyDetectionResult { HasAnomaly = false };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for anomalies");
                return new AnomalyDetectionResult { HasAnomaly = false };
            }
        }

        private AnomalyType DetermineAnomalyType(MotionData motionData)
        {
            // Hareket özelliklerine göre anomali tipini belirle;
            var acceleration = motionData.Acceleration;
            var gyro = motionData.Gyroscope;

            // Düşme tespiti;
            if (acceleration.Y < -5 && Math.Abs(acceleration.Z) < 2)
                return AnomalyType.FallDetection;

            // Ani hareket;
            if (Math.Abs(acceleration.X) > 10 || Math.Abs(acceleration.Y) > 10 || Math.Abs(acceleration.Z) > 10)
                return AnomalyType.SuddenMovement;

            // Titreme/konvülsiyon;
            if (Math.Abs(gyro.X) > 15 || Math.Abs(gyro.Y) > 15 || Math.Abs(gyro.Z) > 15)
                return AnomalyType.Tremor;

            return AnomalyType.UnusualActivity;
        }

        private async Task<AnomalyDetectionResult> CheckHeartRateAnomalyAsync(double heartRate, CancellationToken cancellationToken)
        {
            // Normal kalp atış hızı aralığı;
            const double minNormalHR = 40;
            const double maxNormalHR = 180;

            if (heartRate < minNormalHR)
            {
                return new AnomalyDetectionResult;
                {
                    HasAnomaly = true,
                    AnomalyType = AnomalyType.LowHeartRate,
                    Severity = CalculateHRSeverity(heartRate, minNormalHR, false),
                    Confidence = 0.85;
                };
            }
            else if (heartRate > maxNormalHR)
            {
                return new AnomalyDetectionResult;
                {
                    HasAnomaly = true,
                    AnomalyType = AnomalyType.HighHeartRate,
                    Severity = CalculateHRSeverity(heartRate, maxNormalHR, true),
                    Confidence = 0.90;
                };
            }

            return new AnomalyDetectionResult { HasAnomaly = false };
        }

        private double CalculateIntensity(MotionData motionData)
        {
            // Hareket yoğunluğunu hesapla (0-10 arası)
            var acceleration = motionData.Acceleration;
            var accMagnitude = Math.Sqrt(acceleration.X * acceleration.X + acceleration.Y * acceleration.Y + acceleration.Z * acceleration.Z);

            var gyro = motionData.Gyroscope;
            var gyroMagnitude = Math.Sqrt(gyro.X * gyro.X + gyro.Y * gyro.Y + gyro.Z * gyro.Z);

            // Normalize et;
            var intensity = (accMagnitude / 20.0) + (gyroMagnitude / 30.0);
            return Math.Min(intensity * 10, 10);
        }

        private double CalculateCalories(MotionData motionData, ActivityMode mode)
        {
            // Aktivite moduna ve hareket verilerine göre kalori yakımını hesapla;
            var baseCalories = mode switch;
            {
                ActivityMode.Sedentary => 0.01,
                ActivityMode.Light => 0.05,
                ActivityMode.Moderate => 0.10,
                ActivityMode.Vigorous => 0.20,
                _ => 0.05;
            };

            var intensity = CalculateIntensity(motionData);
            var durationHours = motionData.DurationMs / 3600000.0;

            // MET (Metabolic Equivalent of Task) değerine göre kalori hesapla;
            var metValue = GetMETValue(mode, intensity);
            var weightKg = _currentConfig?.UserWeightKg ?? 70; // Varsayılan kilo;
            var calories = metValue * weightKg * durationHours;

            return calories;
        }

        private double GetMETValue(ActivityMode mode, double intensity)
        {
            return mode switch;
            {
                ActivityMode.Sedentary => 1.0 * intensity,
                ActivityMode.Light => 2.5 * intensity,
                ActivityMode.Moderate => 5.0 * intensity,
                ActivityMode.Vigorous => 8.0 * intensity,
                _ => 1.5 * intensity;
            };
        }

        private double CalculateDistance(MotionData motionData)
        {
            // Adım uzunluğuna göre mesafe hesapla;
            var stepLength = _currentConfig?.StepLengthMeters ?? 0.7; // Varsayılan adım uzunluğu;
            var steps = motionData.StepCount ?? EstimateSteps(motionData);

            return steps * stepLength;
        }

        private int EstimateSteps(MotionData motionData)
        {
            // Hareket verilerinden adım sayısını tahmin et;
            var accMagnitude = Math.Sqrt(
                motionData.Acceleration.X * motionData.Acceleration.X +
                motionData.Acceleration.Y * motionData.Acceleration.Y +
                motionData.Acceleration.Z * motionData.Acceleration.Z);

            // Eşik değere göre adım say;
            if (accMagnitude > 1.5)
            {
                var durationSeconds = motionData.DurationMs / 1000.0;
                var estimatedStepsPerSecond = 0.5; // Varsayılan adım hızı;
                return (int)(durationSeconds * estimatedStepsPerSecond);
            }

            return 0;
        }

        private double CalculateSpeed(MotionData motionData)
        {
            // Hareket hızını hesapla (m/s)
            var distance = CalculateDistance(motionData);
            var timeSeconds = motionData.DurationMs / 1000.0;

            return timeSeconds > 0 ? distance / timeSeconds : 0;
        }

        private double CalculateMotionVariance(MotionData motionData)
        {
            // Hareket varyansını hesapla;
            var acceleration = motionData.Acceleration;
            var meanAcc = (Math.Abs(acceleration.X) + Math.Abs(acceleration.Y) + Math.Abs(acceleration.Z)) / 3.0;

            var variance = Math.Pow(Math.Abs(acceleration.X) - meanAcc, 2) +
                          Math.Pow(Math.Abs(acceleration.Y) - meanAcc, 2) +
                          Math.Pow(Math.Abs(acceleration.Z) - meanAcc, 2);

            return variance / 3.0;
        }

        private AnomalySeverity CalculateAnomalySeverity(double accelerationMagnitude, double gyroMagnitude)
        {
            // Anomali şiddetini hesapla;
            var totalMagnitude = accelerationMagnitude + gyroMagnitude;

            if (totalMagnitude > 30) return AnomalySeverity.Critical;
            if (totalMagnitude > 20) return AnomalySeverity.High;
            if (totalMagnitude > 10) return AnomalySeverity.Medium;
            return AnomalySeverity.Low;
        }

        private AnomalySeverity CalculateHRSeverity(double heartRate, double threshold, bool isHigh)
        {
            var deviation = isHigh ? heartRate - threshold : threshold - heartRate;
            var ratio = deviation / threshold;

            if (ratio > 0.5) return AnomalySeverity.Critical;
            if (ratio > 0.3) return AnomalySeverity.High;
            if (ratio > 0.1) return AnomalySeverity.Medium;
            return AnomalySeverity.Low;
        }

        private double CalculateAnomalyConfidence(MotionData motionData)
        {
            // Anomali güven skorunu hesapla (0-1)
            var features = new[]
            {
                Math.Abs(motionData.Acceleration.X),
                Math.Abs(motionData.Acceleration.Y),
                Math.Abs(motionData.Acceleration.Z),
                Math.Abs(motionData.Gyroscope.X),
                Math.Abs(motionData.Gyroscope.Y),
                Math.Abs(motionData.Gyroscope.Z)
            };

            var maxFeature = features.Max();
            var confidence = Math.Min(maxFeature / 25.0, 1.0);

            return confidence;
        }

        private async Task<IEnumerable<ActivityData>> LoadActivityDataAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken)
        {
            // Veritabanından veya cache'ten aktivite verilerini yükle;
            // Burada simüle ediyoruz;
            await Task.Delay(100, cancellationToken);

            var mockData = new List<ActivityData>
            {
                new()
                {
                    Timestamp = startTime.AddHours(1),
                    ActivityMode = ActivityMode.Moderate,
                    Intensity = 5.5,
                    CaloriesBurned = 150,
                    MotionData = new MotionData;
                    {
                        SensorId = "sensor_001",
                        ActivityType = ActivityType.Walking,
                        DurationMs = 1800000 // 30 dakika;
                    }
                },
                new()
                {
                    Timestamp = startTime.AddHours(2),
                    ActivityMode = ActivityMode.Vigorous,
                    Intensity = 8.2,
                    CaloriesBurned = 300,
                    MotionData = new MotionData;
                    {
                        SensorId = "sensor_001",
                        ActivityType = ActivityType.Running,
                        DurationMs = 900000 // 15 dakika;
                    }
                }
            };

            return mockData.Where(d => d.Timestamp >= startTime && d.Timestamp <= endTime).ToList();
        }

        private IEnumerable<ActivityData> ApplyFilter(IEnumerable<ActivityData> data, ActivityFilter filter)
        {
            var filtered = data;

            if (filter.MinIntensity.HasValue)
                filtered = filtered.Where(d => d.Intensity >= filter.MinIntensity.Value);

            if (filter.MaxIntensity.HasValue)
                filtered = filtered.Where(d => d.Intensity <= filter.MaxIntensity.Value);

            if (filter.ActivityModes?.Any() == true)
                filtered = filtered.Where(d => filter.ActivityModes.Contains(d.ActivityMode));

            if (filter.MinCalories.HasValue)
                filtered = filtered.Where(d => d.CaloriesBurned >= filter.MinCalories.Value);

            if (filter.StartTime.HasValue)
                filtered = filtered.Where(d => d.Timestamp >= filter.StartTime.Value);

            if (filter.EndTime.HasValue)
                filtered = filtered.Where(d => d.Timestamp <= filter.EndTime.Value);

            return filtered;
        }

        private async Task AnalyzeActivityPatternsAsync(IEnumerable<ActivityData> activityData, CancellationToken cancellationToken)
        {
            // Aktivite desenlerini analiz et;
            var patterns = activityData;
                .GroupBy(d => d.ActivityMode)
                .Select(g => new ActivityPattern;
                {
                    ActivityMode = g.Key,
                    AverageIntensity = g.Average(d => d.Intensity),
                    TotalDuration = TimeSpan.FromMilliseconds(g.Sum(d => d.MotionData.DurationMs)),
                    Occurrences = g.Count()
                });

            // Bilinen desenlerle karşılaştır;
            foreach (var pattern in patterns)
            {
                var knownPattern = _knownPatterns.FirstOrDefault(p => p.ActivityMode == pattern.ActivityMode);
                if (knownPattern != null)
                {
                    var deviation = Math.Abs(pattern.AverageIntensity - knownPattern.AverageIntensity);
                    if (deviation > 2.0) // Önemli sapma;
                    {
                        _logger.LogWarning($"Significant deviation detected in {pattern.ActivityMode} pattern: {deviation:F2}");
                    }
                }
            }

            await Task.CompletedTask;
        }

        private async Task CalculateSessionMetricsAsync(ActivitySession session, CancellationToken cancellationToken)
        {
            // Oturum metriklerini hesapla;
            var sessionData = await LoadSessionDataAsync(session.SessionId, cancellationToken);

            if (sessionData?.Any() == true)
            {
                session.Metrics.AverageIntensity = sessionData.Average(d => d.Intensity);
                session.Metrics.MaxIntensity = sessionData.Max(d => d.Intensity);
                session.Metrics.TotalCalories = sessionData.Sum(d => d.CaloriesBurned);
                session.Metrics.PeakHeartRate = sessionData.Max(d => d.MotionData.HeartRate ?? 0);
                session.Metrics.AverageHeartRate = sessionData.Where(d => d.MotionData.HeartRate.HasValue)
                                                              .Average(d => d.MotionData.HeartRate!.Value);
            }
        }

        private async Task<IEnumerable<ActivityData>> LoadSessionDataAsync(string sessionId, CancellationToken cancellationToken)
        {
            // Oturum verilerini yükle (simülasyon)
            await Task.Delay(50, cancellationToken);
            return new List<ActivityData>();
        }

        private async Task ArchiveSessionAsync(ActivitySession session, CancellationToken cancellationToken)
        {
            // Oturumu arşivle (veritabanına kaydet)
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug($"Session archived: {session.SessionId}");
        }

        private async Task SaveStatisticsAsync(CancellationToken cancellationToken)
        {
            // İstatistikleri kalıcı depolamaya kaydet;
            await Task.Delay(200, cancellationToken);
            _logger.LogDebug("Activity statistics saved");
        }

        private async Task<SystemHealthStatus> CheckSensorConnectionsAsync(CancellationToken cancellationToken)
        {
            var status = new SystemHealthStatus;
            {
                Component = "Sensors",
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                var connectedSensors = _currentConfig?.EnabledSensors.Count ?? 0;
                var totalSensors = 4; // Toplam desteklenen sensör sayısı;

                if (connectedSensors == 0)
                {
                    status.Status = HealthStatus.Error;
                    status.Message = "No sensors connected";
                }
                else if (connectedSensors < totalSensors)
                {
                    status.Status = HealthStatus.Warning;
                    status.Message = $"{connectedSensors}/{totalSensors} sensors connected";
                }
                else;
                {
                    status.Status = HealthStatus.Healthy;
                    status.Message = "All sensors connected and working";
                }

                status.Metrics["connected_sensors"] = connectedSensors;
                status.Metrics["total_sensors"] = totalSensors;
            }
            catch (Exception ex)
            {
                status.Status = HealthStatus.Error;
                status.Message = $"Sensor check failed: {ex.Message}";
            }

            return await Task.FromResult(status);
        }

        private SystemHealthStatus CheckDataFlowHealth()
        {
            var status = new SystemHealthStatus;
            {
                Component = "DataFlow",
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                var bufferSize = _motionDataBuffer.Count;
                var dataRate = CalculateDataRate();

                if (bufferSize > 1000)
                {
                    status.Status = HealthStatus.Warning;
                    status.Message = $"High buffer size: {bufferSize}";
                }
                else if (dataRate < 5) // 5 veri/saniye'den az;
                {
                    status.Status = HealthStatus.Warning;
                    status.Message = $"Low data rate: {dataRate:F1}/s";
                }
                else;
                {
                    status.Status = HealthStatus.Healthy;
                    status.Message = $"Data flow normal: {dataRate:F1} data/s";
                }

                status.Metrics["buffer_size"] = bufferSize;
                status.Metrics["data_rate"] = dataRate;
            }
            catch (Exception ex)
            {
                status.Status = HealthStatus.Error;
                status.Message = $"Data flow check failed: {ex.Message}";
            }

            return status;
        }

        private SystemHealthStatus CheckMemoryHealth()
        {
            var status = new SystemHealthStatus;
            {
                Component = "Memory",
                Timestamp = DateTime.UtcNow;
            };

            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                var memoryMB = process.WorkingSet64 / (1024 * 1024);
                var memoryPercentage = (double)memoryMB / 1024 * 100; // 1GB referans;

                if (memoryPercentage > 80)
                {
                    status.Status = HealthStatus.Warning;
                    status.Message = $"High memory usage: {memoryMB}MB ({memoryPercentage:F1}%)";
                }
                else if (memoryPercentage > 95)
                {
                    status.Status = HealthStatus.Error;
                    status.Message = $"Critical memory usage: {memoryMB}MB ({memoryPercentage:F1}%)";
                }
                else;
                {
                    status.Status = HealthStatus.Healthy;
                    status.Message = $"Memory usage normal: {memoryMB}MB ({memoryPercentage:F1}%)";
                }

                status.Metrics["memory_mb"] = memoryMB;
                status.Metrics["memory_percentage"] = memoryPercentage;
            }
            catch (Exception ex)
            {
                status.Status = HealthStatus.Error;
                status.Message = $"Memory check failed: {ex.Message}";
            }

            return status;
        }

        private double CalculateDataRate()
        {
            // Son 10 saniyedeki veri akış hızını hesapla;
            var tenSecondsAgo = DateTime.UtcNow.AddSeconds(-10);
            var recentData = _motionDataBuffer.Values.Count(d => d.Timestamp > tenSecondsAgo);

            return recentData / 10.0;
        }

        private List<ActivityPattern> LoadKnownActivityPatterns()
        {
            // Bilinen aktivite desenlerini yükle;
            return new List<ActivityPattern>
            {
                new() { ActivityMode = ActivityMode.Sedentary, AverageIntensity = 1.0 },
                new() { ActivityMode = ActivityMode.Light, AverageIntensity = 3.0 },
                new() { ActivityMode = ActivityMode.Moderate, AverageIntensity = 5.0 },
                new() { ActivityMode = ActivityMode.Vigorous, AverageIntensity = 7.5 }
            };
        }

        private bool ValidateConfig(MotionSensorConfig config)
        {
            if (config == null) return false;
            if (config.SamplingIntervalMs < 10 || config.SamplingIntervalMs > 1000) return false;
            if (config.EnabledSensors == null || !config.EnabledSensors.Any()) return false;

            return true;
        }

        private void OnMonitoringTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_isMonitoring) return;

            try
            {
                // Periyodik izleme işlemleri;
                CheckActiveSessions();
                CleanupOldData();

                // Buffer boyutunu kontrol et;
                if (_motionDataBuffer.Count > 5000)
                {
                    _logger.LogWarning($"Motion data buffer is large: {_motionDataBuffer.Count} entries");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring timer elapsed event");
            }
        }

        private void OnAnalysisTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_isMonitoring) return;

            try
            {
                // Periyodik analiz işlemleri;
                AnalyzeBufferedData();
                UpdateHealthMetrics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in analysis timer elapsed event");
            }
        }

        private void CheckActiveSessions()
        {
            // Aktif oturumları kontrol et;
            var now = DateTime.UtcNow;
            var expiredSessions = _activeSessions.Values;
                .Where(s => s.Status == SessionStatus.Active &&
                           (now - s.StartTime).TotalHours > 24) // 24 saatten eski oturumlar;
                .ToList();

            foreach (var session in expiredSessions)
            {
                _logger.LogWarning($"Auto-ending expired session: {session.SessionName}");
                _ = EndActivitySessionAsync(session.SessionId);
            }
        }

        private void CleanupOldData()
        {
            // Eski verileri temizle (30 dakikadan eski)
            var cutoffTime = DateTime.UtcNow.AddMinutes(-30);
            var oldKeys = _motionDataBuffer.Keys;
                .Where(key => _motionDataBuffer.TryGetValue(key, out var data) && data.Timestamp < cutoffTime)
                .ToList();

            foreach (var key in oldKeys)
            {
                _motionDataBuffer.TryRemove(key, out _);
            }

            if (oldKeys.Count > 0)
            {
                _logger.LogDebug($"Cleaned up {oldKeys.Count} old motion data entries");
            }
        }

        private void AnalyzeBufferedData()
        {
            // Buffer'daki verileri analiz et;
            if (_motionDataBuffer.Count < 10) return;

            var recentData = _motionDataBuffer.Values;
                .OrderByDescending(d => d.Timestamp)
                .Take(100)
                .ToList();

            if (recentData.Count >= 10)
            {
                // Ortalama aktivite yoğunluğunu hesapla;
                var avgIntensity = recentData.Average(d =>
                    Math.Sqrt(d.Acceleration.X * d.Acceleration.X +
                             d.Acceleration.Y * d.Acceleration.Y +
                             d.Acceleration.Z * d.Acceleration.Z));

                // Trend analizi;
                AnalyzeTrends(recentData);
            }
        }

        private void AnalyzeTrends(List<MotionData> recentData)
        {
            // Aktivite trendlerini analiz et;
            var grouped = recentData;
                .GroupBy(d => d.ActivityType)
                .Select(g => new;
                {
                    Activity = g.Key,
                    Count = g.Count(),
                    AvgIntensity = g.Average(d => Math.Abs(d.Acceleration.Y))
                })
                .ToList();

            // Trend değişikliklerini logla;
            foreach (var group in grouped)
            {
                if (group.Count > 20) // Yeterli veri varsa;
                {
                    _logger.LogDebug($"Trend: {group.Activity} - {group.Count} samples, avg intensity: {group.AvgIntensity:F2}");
                }
            }
        }

        private void UpdateHealthMetrics()
        {
            // Sağlık metriklerini güncelle;
            CurrentStatistics.UpdateTime = DateTime.UtcNow;
            CurrentStatistics.BufferSize = _motionDataBuffer.Count;
            CurrentStatistics.ActiveSessions = _activeSessions.Count;
            CurrentStatistics.ProcessingLoad = CalculateProcessingLoad();
        }

        private double CalculateProcessingLoad()
        {
            // İşlem yükünü hesapla (0-100)
            var dataRate = CalculateDataRate();
            var load = (dataRate / 100.0) * 100; // 100 veri/saniye maksimum;

            return Math.Min(load, 100);
        }

        private void OnActivityDataUpdated(ActivityDataEventArgs e)
        {
            ActivityDataUpdated?.Invoke(this, e);
        }

        private void OnAnomalyDetected(AnomalyDetectedEventArgs e)
        {
            AnomalyDetected?.Invoke(this, e);
        }

        private void OnActivityModeChanged(ActivityModeChangedEventArgs e)
        {
            ActivityModeChanged?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _monitoringTimer?.Dispose();
                    _analysisTimer?.Dispose();
                    _processingLock?.Dispose();

                    if (_isMonitoring)
                    {
                        _ = StopMonitoringAsync().ConfigureAwait(false);
                    }
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
    }

    #region Supporting Classes and Enums;

    public interface IActivityMonitor : IDisposable
    {
        event EventHandler<ActivityDataEventArgs> ActivityDataUpdated;
        event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;
        event EventHandler<ActivityModeChangedEventArgs> ActivityModeChanged;

        ActivityStatistics CurrentStatistics { get; }
        MonitorStatus Status { get; }

        Task<MonitoringResult> StartMonitoringAsync(MotionSensorConfig config, CancellationToken cancellationToken = default);
        Task<MonitoringResult> StopMonitoringAsync(CancellationToken cancellationToken = default);
        Task<MotionAnalysisResult> ProcessMotionDataAsync(MotionData motionData, CancellationToken cancellationToken = default);
        Task<IEnumerable<ActivityData>> GetActivityHistoryAsync(DateTime startTime, DateTime endTime, ActivityFilter filter = null, CancellationToken cancellationToken = default);
        Task<ActivitySession> CreateActivitySessionAsync(string sessionName, ActivityType activityType, CancellationToken cancellationToken = default);
        Task<SessionResult> EndActivitySessionAsync(string sessionId, CancellationToken cancellationToken = default);
        Task<SystemHealthStatus> CheckSystemHealthAsync(CancellationToken cancellationToken = default);
    }

    public enum MonitorStatus;
    {
        Stopped,
        Initializing,
        Running,
        Paused,
        Error;
    }

    public enum ActivityMode;
    {
        Sedentary,
        Light,
        Moderate,
        Vigorous;
    }

    public enum ActivityType;
    {
        Walking,
        Running,
        Cycling,
        Swimming,
        StrengthTraining,
        Yoga,
        Other;
    }

    public enum SensorType;
    {
        Accelerometer,
        Gyroscope,
        Magnetometer,
        HeartRate,
        GPS;
    }

    public enum AnomalyType;
    {
        FallDetection,
        SuddenMovement,
        Tremor,
        LowHeartRate,
        HighHeartRate,
        UnusualActivity;
    }

    public enum AnomalySeverity;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum SessionStatus;
    {
        Active,
        Paused,
        Completed,
        Cancelled;
    }

    public enum HealthStatus;
    {
        Healthy,
        Warning,
        Error;
    }

    public class MotionSensorConfig;
    {
        public List<SensorType> EnabledSensors { get; set; } = new();
        public Dictionary<SensorType, SensorConfig> SensorConfigurations { get; set; } = new();
        public int SamplingIntervalMs { get; set; } = 100;
        public double? UserWeightKg { get; set; }
        public double? StepLengthMeters { get; set; }
        public AnomalyThresholds AnomalyThresholds { get; set; } = new();
    }

    public class SensorConfig;
    {
        public double Sensitivity { get; set; } = 1.0;
        public int SamplingRate { get; set; } = 100;
        public string CalibrationMode { get; set; } = "auto";
        public double Precision { get; set; } = 0.1;
    }

    public class AnomalyThresholds;
    {
        public double AccelerationThreshold { get; set; } = 15.0;
        public double GyroscopeThreshold { get; set; } = 20.0;
        public double HeartRateLowThreshold { get; set; } = 40.0;
        public double HeartRateHighThreshold { get; set; } = 180.0;
    }

    public class MotionData;
    {
        public string SensorId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public ActivityType ActivityType { get; set; }
        public int DurationMs { get; set; }

        public Vector3 Acceleration { get; set; } = new();
        public Vector3 Gyroscope { get; set; } = new();
        public Vector3 Magnetometer { get; set; } = new();

        public double? HeartRate { get; set; }
        public int? StepCount { get; set; }
        public double? Temperature { get; set; }

        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public struct Vector3;
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public Vector3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public class ActivityData;
    {
        public DateTime Timestamp { get; set; }
        public MotionData MotionData { get; set; } = new();
        public ActivityMode ActivityMode { get; set; }
        public double Intensity { get; set; }
        public double CaloriesBurned { get; set; }
        public BehaviorAnalysisResult BehaviorAnalysis { get; set; } = new();
        public bool AnomalyDetected { get; set; }
    }

    public class ActivityStatistics;
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public DateTime UpdateTime { get; set; }

        public int TotalMotionDataPoints { get; set; }
        public int TotalSteps { get; set; }
        public double TotalDistance { get; set; }
        public double TotalCaloriesBurned { get; set; }

        public TimeSpan WalkingDuration { get; set; }
        public TimeSpan RunningDuration { get; set; }
        public TimeSpan CyclingDuration { get; set; }
        public TimeSpan SwimmingDuration { get; set; }

        public ActivityMode CurrentActivityMode { get; set; }
        public int BufferSize { get; set; }
        public int ActiveSessions { get; set; }
        public double ProcessingLoad { get; set; }
    }

    public class ActivitySession;
    {
        public string SessionId { get; set; } = string.Empty;
        public string SessionName { get; set; } = string.Empty;
        public ActivityType ActivityType { get; set; }
        public SessionStatus Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public SessionMetrics Metrics { get; set; } = new();
    }

    public class SessionMetrics;
    {
        public TimeSpan Duration { get; set; }
        public double AverageIntensity { get; set; }
        public double MaxIntensity { get; set; }
        public double TotalCalories { get; set; }
        public double AverageHeartRate { get; set; }
        public double PeakHeartRate { get; set; }
        public double TotalDistance { get; set; }
        public int TotalSteps { get; set; }
    }

    public class ActivityFilter;
    {
        public double? MinIntensity { get; set; }
        public double? MaxIntensity { get; set; }
        public List<ActivityMode>? ActivityModes { get; set; }
        public double? MinCalories { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    public class ActivityPattern;
    {
        public ActivityMode ActivityMode { get; set; }
        public double AverageIntensity { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public int Occurrences { get; set; }
    }

    public class MonitoringResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new();

        public static MonitoringResult Success(string message = "Operation completed successfully") =>
            new() { Success = true, Message = message, Timestamp = DateTime.UtcNow };

        public static MonitoringResult Failure(string errorMessage) =>
            new() { Success = false, Message = errorMessage, Timestamp = DateTime.UtcNow };

        public static MonitoringResult AlreadyRunning() =>
            new() { Success = false, Message = "Monitoring is already running", Timestamp = DateTime.UtcNow };

        public static MonitoringResult NotRunning() =>
            new() { Success = false, Message = "Monitoring is not running", Timestamp = DateTime.UtcNow };
    }

    public class MotionAnalysisResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public ActivityData? ActivityData { get; set; }
        public bool AnomalyDetected { get; set; }
        public AnomalyDetectionResult? AnomalyDetails { get; set; }

        public static MotionAnalysisResult Success(ActivityData activityData) =>
            new() { Success = true, ActivityData = activityData, Message = "Analysis completed" };

        public static MotionAnalysisResult Failure(string errorMessage) =>
            new() { Success = false, Message = errorMessage };
    }

    public class AnomalyDetectionResult;
    {
        public bool HasAnomaly { get; set; }
        public AnomalyType AnomalyType { get; set; }
        public AnomalySeverity Severity { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Details { get; set; } = new();
    }

    public class SessionResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public ActivitySession? Session { get; set; }

        public static SessionResult Success(ActivitySession session) =>
            new() { Success = true, Session = session, Message = "Session operation completed" };

        public static SessionResult Failure(string errorMessage) =>
            new() { Success = false, Message = errorMessage };
    }

    public class SystemHealthStatus;
    {
        public DateTime Timestamp { get; set; }
        public string Component { get; set; } = string.Empty;
        public HealthStatus OverallStatus { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<SystemHealthStatus> Subcomponents { get; set; } = new();
        public Dictionary<string, object> Metrics { get; set; } = new();
    }

    public class ActivityDataEventArgs : EventArgs;
    {
        public ActivityData ActivityData { get; set; } = new();
        public string SessionId { get; set; } = string.Empty;
    }

    public class AnomalyDetectedEventArgs : EventArgs;
    {
        public MotionData MotionData { get; set; } = new();
        public AnomalyType AnomalyType { get; set; }
        public AnomalySeverity Severity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActivityModeChangedEventArgs : EventArgs;
    {
        public ActivityMode PreviousMode { get; set; }
        public ActivityMode NewMode { get; set; }
        public ActivityType Trigger { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BehaviorAnalysisResult;
    {
        public string PrimaryBehavior { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public Dictionary<string, double> BehaviorPatterns { get; set; } = new();
        public Dictionary<string, object> AdditionalInsights { get; set; } = new();
    }

    #endregion;
}
