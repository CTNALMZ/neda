// NEDA.RealTimeMonitoring/PresenceAnalysis/ActivityMonitor.cs;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.RealTimeMonitoring.PresenceAnalysis;
{
    /// <summary>
    /// Endüstriyel seviyede aktivite izleme ve analiz motoru;
    /// Gerçek zamanlı aktivite takibi, anomali tespiti ve davranış analizi sağlar;
    /// </summary>
    public class ActivityMonitor : IObservable<ActivityEvent>, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly ActivityMonitorConfig _config;
        private readonly object _syncLock = new object();
        private readonly Subject<ActivityEvent> _activityStream;
        private readonly Dictionary<string, ActivitySensor> _activeSensors;
        private readonly Dictionary<string, ActivityPattern> _detectedPatterns;
        private readonly List<ActivityThreshold> _thresholds;
        private readonly ActivityMetricsCollector _metricsCollector;
        private readonly ActivityAnalyzer _activityAnalyzer;
        private readonly CancellationTokenSource _monitoringCts;
        private Timer _periodicAnalysisTimer;
        private Timer _metricsCollectionTimer;
        private DateTime _lastActivityTimestamp;
        private ActivityState _currentState;
        private bool _isMonitoring;
        private bool _isInitialized;
        private int _totalActivityCount;
        private int _concurrentActivityCount;
        #endregion;

        #region Properties;
        /// <summary>
        /// Aktivite izleyici durumu;
        /// </summary>
        public ActivityMonitorState State { get; private set; }

        /// <summary>
        /// Mevcut aktivite durumu;
        /// </summary>
        public ActivityState CurrentState => _currentState;

        /// <summary>
        /// Aktif sensör sayısı;
        /// </summary>
        public int ActiveSensorCount => _activeSensors.Count;

        /// <summary>
        /// Toplam tespit edilen aktivite sayısı;
        /// </summary>
        public int TotalActivityCount => _totalActivityCount;

        /// <summary>
        /// Eşzamanlı aktivite sayısı;
        /// </summary>
        public int ConcurrentActivityCount => _concurrentActivityCount;

        /// <summary>
        /// Aktivite metrikleri;
        /// </summary>
        public ActivityMetrics CurrentMetrics => _metricsCollector.GetCurrentMetrics();

        /// <summary>
        /// Konfigürasyon ayarları;
        /// </summary>
        public ActivityMonitorConfig Config => _config;

        /// <summary>
        /// Aktivite veri akışı (observable)
        /// </summary>
        public IObservable<ActivityEvent> ActivityStream => _activityStream.AsObservable();
        #endregion;

        #region Events;
        /// <summary>
        /// Yeni aktivite tespit edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ActivityDetectedEventArgs> ActivityDetected;

        /// <summary>
        /// Aktivite durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ActivityStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Anomali tespit edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<AnomalyDetectedEventArgs> AnomalyDetected;

        /// <summary>
        /// Aktivite eşiği aşıldığında tetiklenir;
        /// </summary>
        public event EventHandler<ThresholdExceededEventArgs> ThresholdExceeded;

        /// <summary>
        /// Aktivite modeli değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<PatternChangedEventArgs> PatternChanged;

        /// <summary>
        /// İzleme durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<MonitoringStatusChangedEventArgs> MonitoringStatusChanged;
        #endregion;

        #region Constructor;
        /// <summary>
        /// ActivityMonitor örneği oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="config">Aktivite izleme konfigürasyonu</param>
        public ActivityMonitor(ILogger logger, ActivityMonitorConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? ActivityMonitorConfig.Default;

            _activityStream = new Subject<ActivityEvent>();
            _activeSensors = new Dictionary<string, ActivitySensor>();
            _detectedPatterns = new Dictionary<string, ActivityPattern>();
            _thresholds = new List<ActivityThreshold>();
            _metricsCollector = new ActivityMetricsCollector(_config);
            _activityAnalyzer = new ActivityAnalyzer(_config, _logger);
            _monitoringCts = new CancellationTokenSource();

            _currentState = ActivityState.Idle;
            _lastActivityTimestamp = DateTime.MinValue;
            State = ActivityMonitorState.Stopped;

            InitializeThresholds();

            _logger.Info($"ActivityMonitor initialized with Sensitivity: {_config.Sensitivity}, " +
                        $"AnalysisInterval: {_config.AnalysisIntervalMs}ms, " +
                        $"ThresholdCount: {_thresholds.Count}");
        }
        #endregion;

        #region Public Methods;
        /// <summary>
        /// Aktivite izleyiciyi başlatır;
        /// </summary>
        public void Initialize()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_isInitialized)
                    {
                        _logger.Warning("ActivityMonitor already initialized");
                        return;
                    }

                    InitializeSensors();
                    InitializeTimers();
                    StartBackgroundProcessing();

                    _isInitialized = true;
                    _isMonitoring = true;
                    State = ActivityMonitorState.Running;

                    OnMonitoringStatusChanged(new MonitoringStatusChangedEventArgs;
                    {
                        IsMonitoring = true,
                        Timestamp = DateTime.UtcNow,
                        Message = "Activity monitoring started"
                    });

                    _logger.Info("ActivityMonitor initialized and started successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize ActivityMonitor: {ex.Message}", ex);
                throw new ActivityMonitoringException(
                    ErrorCode.ActivityMonitorInitializationFailed,
                    "Failed to initialize activity monitor",
                    ex);
            }
        }

        /// <summary>
        /// Aktivite sensörü kaydeder;
        /// </summary>
        /// <param name="sensor">Kaydedilecek sensör</param>
        /// <returns>Kayıt başarılı mı?</returns>
        public bool RegisterSensor(ActivitySensor sensor)
        {
            if (sensor == null)
                throw new ArgumentNullException(nameof(sensor));

            try
            {
                lock (_syncLock)
                {
                    if (_activeSensors.ContainsKey(sensor.SensorId))
                    {
                        _logger.Warning($"Sensor already registered: {sensor.SensorId}");
                        return false;
                    }

                    sensor.ActivityDetected += OnSensorActivityDetected;
                    sensor.SensorStatusChanged += OnSensorStatusChanged;

                    _activeSensors.Add(sensor.SensorId, sensor);

                    _logger.Debug($"Registered sensor: {sensor.SensorId} ({sensor.SensorType})");

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to register sensor {sensor.SensorId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Aktivite sensörünü kaldırır;
        /// </summary>
        /// <param name="sensorId">Kaldırılacak sensör ID'si</param>
        /// <returns>Kaldırma başarılı mı?</returns>
        public bool UnregisterSensor(string sensorId)
        {
            if (string.IsNullOrWhiteSpace(sensorId))
                return false;

            try
            {
                lock (_syncLock)
                {
                    if (!_activeSensors.TryGetValue(sensorId, out var sensor))
                    {
                        _logger.Warning($"Sensor not found: {sensorId}");
                        return false;
                    }

                    sensor.ActivityDetected -= OnSensorActivityDetected;
                    sensor.SensorStatusChanged -= OnSensorStatusChanged;
                    sensor.Dispose();

                    _activeSensors.Remove(sensorId);

                    _logger.Debug($"Unregistered sensor: {sensorId}");

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to unregister sensor {sensorId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Aktivite eşiği ekler;
        /// </summary>
        /// <param name="threshold">Eklenecek eşik</param>
        public void AddThreshold(ActivityThreshold threshold)
        {
            if (threshold == null)
                throw new ArgumentNullException(nameof(threshold));

            lock (_syncLock)
            {
                _thresholds.Add(threshold);
                threshold.ThresholdExceeded += OnThresholdExceeded;

                _logger.Debug($"Added activity threshold: {threshold.ThresholdType}, " +
                            $"Value: {threshold.ThresholdValue}, " +
                            $"Operator: {threshold.ComparisonOperator}");
            }
        }

        /// <summary>
        /// Aktivite eşiğini kaldırır;
        /// </summary>
        /// <param name="thresholdId">Kaldırılacak eşik ID'si</param>
        public bool RemoveThreshold(string thresholdId)
        {
            lock (_syncLock)
            {
                var threshold = _thresholds.FirstOrDefault(t => t.ThresholdId == thresholdId);
                if (threshold == null)
                    return false;

                threshold.ThresholdExceeded -= OnThresholdExceeded;
                threshold.Dispose();

                _thresholds.Remove(threshold);

                _logger.Debug($"Removed activity threshold: {thresholdId}");

                return true;
            }
        }

        /// <summary>
        /// Aktivite verisi işler;
        /// </summary>
        /// <param name="activityData">İşlenecek aktivite verisi</param>
        public async Task ProcessActivityAsync(ActivityData activityData)
        {
            if (activityData == null)
                throw new ArgumentNullException(nameof(activityData));

            if (!_isMonitoring)
                throw new ActivityMonitoringException(
                    ErrorCode.MonitoringNotActive,
                    "Activity monitoring is not active");

            try
            {
                // Aktivite analizi yap;
                var analysisResult = await _activityAnalyzer.AnalyzeAsync(activityData);

                // Aktivite olayı oluştur;
                var activityEvent = new ActivityEvent;
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    ActivityData = activityData,
                    AnalysisResult = analysisResult,
                    SensorId = activityData.SensorId,
                    ActivityType = activityData.ActivityType,
                    Confidence = analysisResult.Confidence,
                    IsAnomaly = analysisResult.IsAnomaly;
                };

                // Aktivite akışına yayınla;
                _activityStream.OnNext(activityEvent);

                // Metrikleri güncelle;
                UpdateMetrics(activityEvent);

                // Durumu kontrol et;
                UpdateActivityState(activityEvent);

                // Eşik kontrollerini yap;
                CheckThresholds(activityEvent);

                // Event tetikle;
                OnActivityDetected(new ActivityDetectedEventArgs;
                {
                    ActivityEvent = activityEvent,
                    Timestamp = activityEvent.Timestamp;
                });

                // Anomali kontrolü;
                if (activityEvent.IsAnomaly)
                {
                    OnAnomalyDetected(new AnomalyDetectedEventArgs;
                    {
                        ActivityEvent = activityEvent,
                        AnomalyType = analysisResult.AnomalyType,
                        Severity = analysisResult.AnomalySeverity,
                        Timestamp = activityEvent.Timestamp;
                    });
                }

                _logger.Debug($"Processed activity: {activityData.ActivityType}, " +
                            $"Sensor: {activityData.SensorId}, " +
                            $"Confidence: {analysisResult.Confidence:F2}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to process activity: {ex.Message}", ex);
                throw new ActivityMonitoringException(
                    ErrorCode.ActivityProcessingFailed,
                    "Failed to process activity data",
                    ex);
            }
        }

        /// <summary>
        /// Aktivite modelini analiz eder;
        /// </summary>
        /// <returns>Aktivite analiz raporu</returns>
        public async Task<ActivityAnalysisReport> AnalyzePatternsAsync()
        {
            try
            {
                lock (_syncLock)
                {
                    var patterns = _detectedPatterns.Values.ToList();
                    var metrics = _metricsCollector.GetHistoricalMetrics(
                        TimeSpan.FromMinutes(_config.HistoricalAnalysisMinutes));

                    var analysis = _activityAnalyzer.AnalyzePatterns(patterns, metrics);

                    // Yeni modelleri kaydet;
                    foreach (var pattern in analysis.DetectedPatterns)
                    {
                        if (!_detectedPatterns.ContainsKey(pattern.PatternId))
                        {
                            _detectedPatterns.Add(pattern.PatternId, pattern);
                            OnPatternChanged(new PatternChangedEventArgs;
                            {
                                Pattern = pattern,
                                ChangeType = PatternChangeType.NewPattern,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                    }

                    _logger.Info($"Pattern analysis completed: {analysis.PatternCount} patterns, " +
                               $"Trend: {analysis.TrendDirection}");

                    return analysis;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to analyze patterns: {ex.Message}", ex);
                throw new ActivityMonitoringException(
                    ErrorCode.PatternAnalysisFailed,
                    "Failed to analyze activity patterns",
                    ex);
            }
        }

        /// <summary>
        /// Aktivite tahmini yapar;
        /// </summary>
        /// <param name="timeWindow">Tahmin zaman penceresi</param>
        /// <returns>Aktivite tahmini</returns>
        public ActivityPrediction PredictActivity(TimeSpan timeWindow)
        {
            try
            {
                var historicalData = _metricsCollector.GetHistoricalMetrics(timeWindow);
                var patterns = _detectedPatterns.Values.ToList();

                var prediction = _activityAnalyzer.PredictActivity(historicalData, patterns, timeWindow);

                _logger.Debug($"Activity prediction generated for {timeWindow.TotalMinutes} minutes, " +
                            $"ExpectedActivityLevel: {prediction.ExpectedActivityLevel}");

                return prediction;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to predict activity: {ex.Message}", ex);
                throw new ActivityMonitoringException(
                    ErrorCode.ActivityPredictionFailed,
                    "Failed to predict activity",
                    ex);
            }
        }

        /// <summary>
        /// İzlemeyi durdurur;
        /// </summary>
        public void StopMonitoring()
        {
            try
            {
                lock (_syncLock)
                {
                    if (!_isMonitoring)
                        return;

                    _isMonitoring = false;
                    State = ActivityMonitorState.Stopping;

                    // Timers'ı durdur;
                    _periodicAnalysisTimer?.Dispose();
                    _metricsCollectionTimer?.Dispose();

                    // Background işlemi durdur;
                    _monitoringCts.Cancel();

                    // Sensörleri temizle;
                    foreach (var sensor in _activeSensors.Values)
                    {
                        sensor.ActivityDetected -= OnSensorActivityDetected;
                        sensor.SensorStatusChanged -= OnSensorStatusChanged;
                        sensor.Dispose();
                    }
                    _activeSensors.Clear();

                    // Eşikleri temizle;
                    foreach (var threshold in _thresholds)
                    {
                        threshold.ThresholdExceeded -= OnThresholdExceeded;
                        threshold.Dispose();
                    }
                    _thresholds.Clear();

                    State = ActivityMonitorState.Stopped;

                    OnMonitoringStatusChanged(new MonitoringStatusChangedEventArgs;
                    {
                        IsMonitoring = false,
                        Timestamp = DateTime.UtcNow,
                        Message = "Activity monitoring stopped"
                    });

                    _logger.Info("ActivityMonitor stopped successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error while stopping ActivityMonitor: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Metrikleri sıfırlar;
        /// </summary>
        public void ResetMetrics()
        {
            lock (_syncLock)
            {
                _metricsCollector.Reset();
                _totalActivityCount = 0;
                _concurrentActivityCount = 0;
                _lastActivityTimestamp = DateTime.MinValue;

                _logger.Debug("Activity metrics reset");
            }
        }

        /// <summary>
        /// Aktivite geçmişini getirir;
        /// </summary>
        /// <param name="timeSpan">Zaman aralığı</param>
        /// <returns>Aktivite geçmişi</returns>
        public List<ActivityEvent> GetActivityHistory(TimeSpan timeSpan)
        {
            try
            {
                // Burada genellikle bir veritabanı sorgusu olur;
                // Şimdilik örnek bir implementasyon;
                return new List<ActivityEvent>();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get activity history: {ex.Message}", ex);
                throw new ActivityMonitoringException(
                    ErrorCode.HistoryRetrievalFailed,
                    "Failed to retrieve activity history",
                    ex);
            }
        }

        /// <summary>
        /// Canlı aktivite durumunu getirir;
        /// </summary>
        /// <returns>Canlı aktivite durumu</returns>
        public LiveActivityStatus GetLiveStatus()
        {
            lock (_syncLock)
            {
                return new LiveActivityStatus;
                {
                    Timestamp = DateTime.UtcNow,
                    CurrentState = _currentState,
                    ActiveSensors = _activeSensors.Count,
                    ConcurrentActivities = _concurrentActivityCount,
                    LastActivityTime = _lastActivityTimestamp,
                    Metrics = _metricsCollector.GetCurrentMetrics(),
                    IsMonitoring = _isMonitoring;
                };
            }
        }
        #endregion;

        #region Private Methods;
        private void InitializeSensors()
        {
            // Varsayılan sensörleri başlat;
            if (_config.EnableDefaultSensors)
            {
                // Hareket sensörü;
                if (_config.EnableMotionDetection)
                {
                    var motionSensor = new MotionSensor(
                        $"MotionSensor_{Guid.NewGuid():N}",
                        _config.MotionSensitivity);
                    RegisterSensor(motionSensor);
                }

                // Ses sensörü;
                if (_config.EnableAudioDetection)
                {
                    var audioSensor = new AudioSensor(
                        $"AudioSensor_{Guid.NewGuid():N}",
                        _config.AudioThreshold);
                    RegisterSensor(audioSensor);
                }

                // Isı sensörü;
                if (_config.EnableThermalDetection)
                {
                    var thermalSensor = new ThermalSensor(
                        $"ThermalSensor_{Guid.NewGuid():N}",
                        _config.ThermalThreshold);
                    RegisterSensor(thermalSensor);
                }
            }
        }

        private void InitializeTimers()
        {
            // Periyodik analiz timer'ı;
            _periodicAnalysisTimer = new Timer(
                async _ => await PerformPeriodicAnalysisAsync(),
                null,
                TimeSpan.FromMilliseconds(_config.AnalysisIntervalMs),
                TimeSpan.FromMilliseconds(_config.AnalysisIntervalMs));

            // Metrik toplama timer'ı;
            _metricsCollectionTimer = new Timer(
                _ => CollectMetrics(),
                null,
                TimeSpan.FromSeconds(_config.MetricsCollectionIntervalSec),
                TimeSpan.FromSeconds(_config.MetricsCollectionIntervalSec));
        }

        private void InitializeThresholds()
        {
            // Varsayılan eşikleri ekle;
            if (_config.EnableDefaultThresholds)
            {
                // Aktivite sayısı eşiği;
                AddThreshold(new ActivityThreshold(
                    "HighActivityCount",
                    ThresholdType.ActivityCount,
                    _config.HighActivityThreshold,
                    ComparisonOperator.GreaterThan));

                // Aktivite süresi eşiği;
                AddThreshold(new ActivityThreshold(
                    "LongActivityDuration",
                    ThresholdType.ActivityDuration,
                    _config.LongActivityThresholdSec,
                    ComparisonOperator.GreaterThan));

                // Sessizlik süresi eşiği;
                AddThreshold(new ActivityThreshold(
                    "LongInactivity",
                    ThresholdType.InactivityDuration,
                    _config.InactivityThresholdSec,
                    ComparisonOperator.GreaterThan));
            }
        }

        private void StartBackgroundProcessing()
        {
            // Background işlemleri başlat;
            Task.Run(async () =>
            {
                while (!_monitoringCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessBackgroundTasksAsync(_monitoringCts.Token);
                        await Task.Delay(1000, _monitoringCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // İptal edildi, normal çıkış;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Background processing error: {ex.Message}", ex);
                    }
                }
            }, _monitoringCts.Token);
        }

        private async Task ProcessBackgroundTasksAsync(CancellationToken cancellationToken)
        {
            // Durum kontrolü;
            CheckActivityStateTimeout();

            // Pattern analizi;
            if (DateTime.UtcNow.Subtract(_lastActivityTimestamp).TotalSeconds >
                _config.IdleAnalysisIntervalSec)
            {
                await AnalyzePatternsAsync();
            }

            // Sensör durum kontrolü;
            CheckSensorStatus();
        }

        private void CheckActivityStateTimeout()
        {
            if (_currentState == ActivityState.Active)
            {
                var inactiveTime = DateTime.UtcNow.Subtract(_lastActivityTimestamp);
                if (inactiveTime.TotalSeconds > _config.InactivityTimeoutSec)
                {
                    UpdateState(ActivityState.Idle);
                }
            }
        }

        private void CheckSensorStatus()
        {
            foreach (var sensor in _activeSensors.Values)
            {
                if (!sensor.IsActive && sensor.LastStatusChange.AddSeconds(30) < DateTime.UtcNow)
                {
                    _logger.Warning($"Sensor {sensor.SensorId} is inactive");
                }
            }
        }

        private async Task PerformPeriodicAnalysisAsync()
        {
            try
            {
                if (!_isMonitoring)
                    return;

                var report = await AnalyzePatternsAsync();

                // Trend analizini logla;
                if (report.TrendDirection != ActivityTrend.Stable)
                {
                    _logger.Info($"Activity trend detected: {report.TrendDirection}, " +
                               $"Confidence: {report.TrendConfidence:F2}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Periodic analysis failed: {ex.Message}", ex);
            }
        }

        private void CollectMetrics()
        {
            try
            {
                if (!_isMonitoring)
                    return;

                _metricsCollector.CollectSample();

                // Eşzamanlı aktivite sayısını güncelle;
                _concurrentActivityCount = CalculateConcurrentActivities();
            }
            catch (Exception ex)
            {
                _logger.Error($"Metrics collection failed: {ex.Message}", ex);
            }
        }

        private int CalculateConcurrentActivities()
        {
            // Son 5 saniye içindeki aktivite sayısını hesapla;
            var recentActivities = _metricsCollector.GetRecentActivities(TimeSpan.FromSeconds(5));
            return recentActivities.Count;
        }

        private void UpdateMetrics(ActivityEvent activityEvent)
        {
            _totalActivityCount++;
            _lastActivityTimestamp = activityEvent.Timestamp;

            _metricsCollector.RecordActivity(activityEvent);
        }

        private void UpdateActivityState(ActivityEvent activityEvent)
        {
            var newState = DetermineActivityState(activityEvent);

            if (newState != _currentState)
            {
                var previousState = _currentState;
                _currentState = newState;

                OnStateChanged(new ActivityStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = newState,
                    ActivityEvent = activityEvent,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Debug($"Activity state changed: {previousState} -> {newState}");
            }
        }

        private ActivityState DetermineActivityState(ActivityEvent activityEvent)
        {
            // Aktivite durumunu belirle;
            var activityLevel = activityEvent.AnalysisResult?.ActivityLevel ?? 0;

            if (activityLevel > _config.HighActivityThreshold)
                return ActivityState.High;
            else if (activityLevel > _config.MediumActivityThreshold)
                return ActivityState.Active;
            else if (activityLevel > _config.LowActivityThreshold)
                return ActivityState.Low;
            else;
                return ActivityState.Idle;
        }

        private void CheckThresholds(ActivityEvent activityEvent)
        {
            foreach (var threshold in _thresholds)
            {
                if (threshold.Check(activityEvent))
                {
                    // Eşik aşıldı, event zaten threshold içinden tetikleniyor;
                    _logger.Debug($"Threshold exceeded: {threshold.ThresholdType}, " +
                                $"Value: {threshold.GetCurrentValue(activityEvent)}");
                }
            }
        }

        private void UpdateState(ActivityState newState)
        {
            if (_currentState == newState)
                return;

            var previousState = _currentState;
            _currentState = newState;

            OnStateChanged(new ActivityStateChangedEventArgs;
            {
                PreviousState = previousState,
                NewState = newState,
                Timestamp = DateTime.UtcNow;
            });
        }
        #endregion;

        #region Event Handlers;
        private void OnSensorActivityDetected(object sender, SensorActivityEventArgs e)
        {
            try
            {
                var activityData = new ActivityData;
                {
                    SensorId = e.SensorId,
                    ActivityType = e.ActivityType,
                    Intensity = e.Intensity,
                    Timestamp = e.Timestamp,
                    Location = e.Location,
                    RawData = e.RawData;
                };

                // Asenkron işleme, await yapmadan;
                Task.Run(() => ProcessActivityAsync(activityData));
            }
            catch (Exception ex)
            {
                _logger.Error($"Error handling sensor activity: {ex.Message}", ex);
            }
        }

        private void OnSensorStatusChanged(object sender, SensorStatusEventArgs e)
        {
            _logger.Debug($"Sensor {e.SensorId} status changed: {e.PreviousStatus} -> {e.NewStatus}");

            if (e.NewStatus == SensorStatus.Error || e.NewStatus == SensorStatus.Disconnected)
            {
                _logger.Warning($"Sensor {e.SensorId} has problematic status: {e.NewStatus}");
            }
        }

        private void OnThresholdExceeded(object sender, ThresholdExceededEventArgs e)
        {
            ThresholdExceeded?.Invoke(this, e);

            _logger.Warning($"Activity threshold exceeded: {e.ThresholdType}, " +
                          $"Value: {e.ExceededValue}, Limit: {e.ThresholdValue}");
        }
        #endregion;

        #region Protected Event Methods;
        protected virtual void OnActivityDetected(ActivityDetectedEventArgs e)
        {
            ActivityDetected?.Invoke(this, e);
        }

        protected virtual void OnStateChanged(ActivityStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        protected virtual void OnAnomalyDetected(AnomalyDetectedEventArgs e)
        {
            AnomalyDetected?.Invoke(this, e);
        }

        protected virtual void OnThresholdExceeded(ThresholdExceededEventArgs e)
        {
            ThresholdExceeded?.Invoke(this, e);
        }

        protected virtual void OnPatternChanged(PatternChangedEventArgs e)
        {
            PatternChanged?.Invoke(this, e);
        }

        protected virtual void OnMonitoringStatusChanged(MonitoringStatusChangedEventArgs e)
        {
            MonitoringStatusChanged?.Invoke(this, e);
        }
        #endregion;

        #region IObservable Implementation;
        public IDisposable Subscribe(IObserver<ActivityEvent> observer)
        {
            return _activityStream.Subscribe(observer);
        }
        #endregion;

        #region IDisposable Implementation;
        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    StopMonitoring();

                    _activityStream?.OnCompleted();
                    _activityStream?.Dispose();

                    _periodicAnalysisTimer?.Dispose();
                    _metricsCollectionTimer?.Dispose();

                    _monitoringCts?.Dispose();

                    foreach (var sensor in _activeSensors.Values)
                    {
                        sensor.Dispose();
                    }
                    _activeSensors.Clear();

                    foreach (var threshold in _thresholds)
                    {
                        threshold.Dispose();
                    }
                    _thresholds.Clear();

                    _detectedPatterns.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ActivityMonitor()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    /// <summary>
    /// Aktivite izleyici durumları;
    /// </summary>
    public enum ActivityMonitorState;
    {
        Stopped,
        Initializing,
        Running,
        Stopping,
        Error;
    }

    /// <summary>
    /// Aktivite durumları;
    /// </summary>
    public enum ActivityState;
    {
        Idle,
        Low,
        Active,
        High,
        Critical;
    }

    /// <summary>
    /// Aktivite trend yönü;
    /// </summary>
    public enum ActivityTrend;
    {
        Increasing,
        Decreasing,
        Stable,
        Volatile;
    }

    /// <summary>
    /// Aktivite izleme konfigürasyonu;
    /// </summary>
    public class ActivityMonitorConfig;
    {
        public float Sensitivity { get; set; } = 0.7f;
        public int AnalysisIntervalMs { get; set; } = 5000;
        public int MetricsCollectionIntervalSec { get; set; } = 10;
        public int HistoricalAnalysisMinutes { get; set; } = 60;
        public int InactivityTimeoutSec { get; set; } = 300;
        public int IdleAnalysisIntervalSec { get; set; } = 30;

        public bool EnableDefaultSensors { get; set; } = true;
        public bool EnableMotionDetection { get; set; } = true;
        public bool EnableAudioDetection { get; set; } = true;
        public bool EnableThermalDetection { get; set; } = false;
        public bool EnableDefaultThresholds { get; set; } = true;

        public float MotionSensitivity { get; set; } = 0.5f;
        public float AudioThreshold { get; set; } = 0.3f;
        public float ThermalThreshold { get; set; } = 30.0f;

        public float LowActivityThreshold { get; set; } = 0.1f;
        public float MediumActivityThreshold { get; set; } = 0.3f;
        public float HighActivityThreshold { get; set; } = 0.7f;
        public int LongActivityThresholdSec { get; set; } = 60;
        public int InactivityThresholdSec { get; set; } = 300;

        public static ActivityMonitorConfig Default => new ActivityMonitorConfig();
    }

    /// <summary>
    /// Aktivite verisi;
    /// </summary>
    public class ActivityData;
    {
        public string SensorId { get; set; }
        public ActivityType ActivityType { get; set; }
        public float Intensity { get; set; }
        public DateTime Timestamp { get; set; }
        public string Location { get; set; }
        public byte[] RawData { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Aktivite olayı;
    /// </summary>
    public class ActivityEvent;
    {
        public string EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public ActivityData ActivityData { get; set; }
        public ActivityAnalysisResult AnalysisResult { get; set; }
        public string SensorId { get; set; }
        public ActivityType ActivityType { get; set; }
        public float Confidence { get; set; }
        public bool IsAnomaly { get; set; }
    }

    /// <summary>
    /// Aktivite analiz sonucu;
    /// </summary>
    public class ActivityAnalysisResult;
    {
        public float Confidence { get; set; }
        public float ActivityLevel { get; set; }
        public bool IsAnomaly { get; set; }
        public string AnomalyType { get; set; }
        public float AnomalySeverity { get; set; }
        public List<string> DetectedPatterns { get; set; } = new List<string>();
        public Dictionary<string, float> Features { get; set; } = new Dictionary<string, float>();
    }

    /// <summary>
    /// Aktivite metrikleri;
    /// </summary>
    public class ActivityMetrics;
    {
        public DateTime Timestamp { get; set; }
        public int ActivityCount { get; set; }
        public float AverageIntensity { get; set; }
        public float PeakIntensity { get; set; }
        public TimeSpan AverageDuration { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public Dictionary<ActivityType, int> ActivityTypeDistribution { get; set; } = new Dictionary<ActivityType, int>();
        public float ActivityFrequency { get; set; }
        public int ConcurrentActivities { get; set; }
    }

    /// <summary>
    /// Aktivite analiz raporu;
    /// </summary>
    public class ActivityAnalysisReport;
    {
        public DateTime AnalysisTime { get; set; }
        public int PatternCount { get; set; }
        public ActivityTrend TrendDirection { get; set; }
        public float TrendConfidence { get; set; }
        public List<ActivityPattern> DetectedPatterns { get; set; } = new List<ActivityPattern>();
        public List<AnomalyDetection> Anomalies { get; set; } = new List<AnomalyDetection>();
        public ActivityMetrics CurrentMetrics { get; set; }
        public List<Recommendation> Recommendations { get; set; } = new List<Recommendation>();
    }

    /// <summary>
    /// Aktivite tahmini;
    /// </summary>
    public class ActivityPrediction;
    {
        public DateTime PredictionTime { get; set; }
        public TimeSpan PredictionWindow { get; set; }
        public float ExpectedActivityLevel { get; set; }
        public float Confidence { get; set; }
        public List<PredictedActivity> PredictedActivities { get; set; } = new List<PredictedActivity>();
        public Dictionary<DateTime, float> ActivityTimeline { get; set; } = new Dictionary<DateTime, float>();
    }

    /// <summary>
    /// Canlı aktivite durumu;
    /// </summary>
    public class LiveActivityStatus;
    {
        public DateTime Timestamp { get; set; }
        public ActivityState CurrentState { get; set; }
        public int ActiveSensors { get; set; }
        public int ConcurrentActivities { get; set; }
        public DateTime LastActivityTime { get; set; }
        public ActivityMetrics Metrics { get; set; }
        public bool IsMonitoring { get; set; }
        public string StatusMessage { get; set; }
    }

    // Event Args sınıfları;
    public class ActivityDetectedEventArgs : EventArgs;
    {
        public ActivityEvent ActivityEvent { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ActivityStateChangedEventArgs : EventArgs;
    {
        public ActivityState PreviousState { get; set; }
        public ActivityState NewState { get; set; }
        public ActivityEvent ActivityEvent { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class AnomalyDetectedEventArgs : EventArgs;
    {
        public ActivityEvent ActivityEvent { get; set; }
        public string AnomalyType { get; set; }
        public float Severity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ThresholdExceededEventArgs : EventArgs;
    {
        public string ThresholdId { get; set; }
        public ThresholdType ThresholdType { get; set; }
        public float ExceededValue { get; set; }
        public float ThresholdValue { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PatternChangedEventArgs : EventArgs;
    {
        public ActivityPattern Pattern { get; set; }
        public PatternChangeType ChangeType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MonitoringStatusChangedEventArgs : EventArgs;
    {
        public bool IsMonitoring { get; set; }
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
    }

    // Exception sınıfı;
    public class ActivityMonitoringException : Exception
    {
        public ErrorCode ErrorCode { get; }

        public ActivityMonitoringException(ErrorCode errorCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Temel sensör sınıfı (soyut)
    public abstract class ActivitySensor : IDisposable
    {
        public string SensorId { get; }
        public SensorType SensorType { get; protected set; }
        public SensorStatus Status { get; protected set; }
        public bool IsActive => Status == SensorStatus.Active;
        public DateTime LastStatusChange { get; protected set; }

        public event EventHandler<SensorActivityEventArgs> ActivityDetected;
        public event EventHandler<SensorStatusEventArgs> SensorStatusChanged;

        protected ActivitySensor(string sensorId)
        {
            SensorId = sensorId;
            Status = SensorStatus.Initializing;
            LastStatusChange = DateTime.UtcNow;
        }

        protected virtual void OnActivityDetected(SensorActivityEventArgs e)
        {
            ActivityDetected?.Invoke(this, e);
        }

        protected virtual void OnStatusChanged(SensorStatus newStatus)
        {
            var previousStatus = Status;
            Status = newStatus;
            LastStatusChange = DateTime.UtcNow;

            SensorStatusChanged?.Invoke(this, new SensorStatusEventArgs;
            {
                SensorId = SensorId,
                PreviousStatus = previousStatus,
                NewStatus = newStatus,
                Timestamp = DateTime.UtcNow;
            });
        }

        public abstract void Start();
        public abstract void Stop();
        public abstract void Dispose();
    }

    // Sensör implementasyonları;
    public class MotionSensor : ActivitySensor;
    {
        private readonly float _sensitivity;

        public MotionSensor(string sensorId, float sensitivity) : base(sensorId)
        {
            SensorType = SensorType.Motion;
            _sensitivity = sensitivity;
            OnStatusChanged(SensorStatus.Active);
        }

        public override void Start()
        {
            OnStatusChanged(SensorStatus.Active);
        }

        public override void Stop()
        {
            OnStatusChanged(SensorStatus.Inactive);
        }

        public override void Dispose()
        {
            Stop();
        }

        // Simüle edilmiş hareket algılama;
        public void SimulateMotion(float intensity, string location)
        {
            OnActivityDetected(new SensorActivityEventArgs;
            {
                SensorId = SensorId,
                ActivityType = ActivityType.Motion,
                Intensity = intensity,
                Timestamp = DateTime.UtcNow,
                Location = location,
                RawData = BitConverter.GetBytes(intensity)
            });
        }
    }

    public class AudioSensor : ActivitySensor;
    {
        private readonly float _threshold;

        public AudioSensor(string sensorId, float threshold) : base(sensorId)
        {
            SensorType = SensorType.Audio;
            _threshold = threshold;
            OnStatusChanged(SensorStatus.Active);
        }

        public override void Start() => OnStatusChanged(SensorStatus.Active);
        public override void Stop() => OnStatusChanged(SensorStatus.Inactive);
        public override void Dispose() => Stop();
    }

    public class ThermalSensor : ActivitySensor;
    {
        private readonly float _threshold;

        public ThermalSensor(string sensorId, float threshold) : base(sensorId)
        {
            SensorType = SensorType.Thermal;
            _threshold = threshold;
            OnStatusChanged(SensorStatus.Active);
        }

        public override void Start() => OnStatusChanged(SensorStatus.Active);
        public override void Stop() => OnStatusChanged(SensorStatus.Inactive);
        public override void Dispose() => Stop();
    }

    // Yardımcı sınıflar (kısmi implementasyon)
    internal class ActivityMetricsCollector;
    {
        private readonly List<ActivityMetrics> _historicalMetrics;
        private readonly ActivityMonitorConfig _config;
        private ActivityMetrics _currentMetrics;

        public ActivityMetricsCollector(ActivityMonitorConfig config)
        {
            _config = config;
            _historicalMetrics = new List<ActivityMetrics>();
            _currentMetrics = new ActivityMetrics { Timestamp = DateTime.UtcNow };
        }

        public void RecordActivity(ActivityEvent activityEvent)
        {
            // Metrikleri güncelle;
            _currentMetrics.ActivityCount++;
            _currentMetrics.AverageIntensity =
                (_currentMetrics.AverageIntensity * (_currentMetrics.ActivityCount - 1) +
                 activityEvent.ActivityData.Intensity) / _currentMetrics.ActivityCount;

            _currentMetrics.PeakIntensity = Math.Max(
                _currentMetrics.PeakIntensity,
                activityEvent.ActivityData.Intensity);

            // Aktivite tipi dağılımı;
            if (_currentMetrics.ActivityTypeDistribution.ContainsKey(activityEvent.ActivityType))
                _currentMetrics.ActivityTypeDistribution[activityEvent.ActivityType]++;
            else;
                _currentMetrics.ActivityTypeDistribution[activityEvent.ActivityType] = 1;
        }

        public void CollectSample()
        {
            _currentMetrics.Timestamp = DateTime.UtcNow;
            _historicalMetrics.Add(_currentMetrics.Clone());

            // Eski verileri temizle;
            var cutoffTime = DateTime.UtcNow.AddMinutes(-_config.HistoricalAnalysisMinutes);
            _historicalMetrics.RemoveAll(m => m.Timestamp < cutoffTime);

            // Yeni metrik nesnesi oluştur;
            _currentMetrics = new ActivityMetrics { Timestamp = DateTime.UtcNow };
        }

        public ActivityMetrics GetCurrentMetrics() => _currentMetrics.Clone();

        public List<ActivityMetrics> GetHistoricalMetrics(TimeSpan timeSpan)
        {
            var cutoffTime = DateTime.UtcNow.Subtract(timeSpan);
            return _historicalMetrics;
                .Where(m => m.Timestamp >= cutoffTime)
                .ToList();
        }

        public List<ActivityEvent> GetRecentActivities(TimeSpan timeSpan)
        {
            // Burada gerçek implementasyonda aktivite geçmişi sorgulanır;
            return new List<ActivityEvent>();
        }

        public void Reset()
        {
            _historicalMetrics.Clear();
            _currentMetrics = new ActivityMetrics { Timestamp = DateTime.UtcNow };
        }
    }

    internal class ActivityAnalyzer;
    {
        private readonly ActivityMonitorConfig _config;
        private readonly ILogger _logger;

        public ActivityAnalyzer(ActivityMonitorConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<ActivityAnalysisResult> AnalyzeAsync(ActivityData activityData)
        {
            return await Task.Run(() =>
            {
                // Aktivite analizi yap;
                var result = new ActivityAnalysisResult;
                {
                    Confidence = CalculateConfidence(activityData),
                    ActivityLevel = activityData.Intensity,
                    IsAnomaly = CheckForAnomaly(activityData),
                    AnomalyType = DetectAnomalyType(activityData),
                    AnomalySeverity = CalculateAnomalySeverity(activityData)
                };

                return result;
            });
        }

        public ActivityAnalysisReport AnalyzePatterns(List<ActivityPattern> patterns, List<ActivityMetrics> metrics)
        {
            var report = new ActivityAnalysisReport;
            {
                AnalysisTime = DateTime.UtcNow,
                PatternCount = patterns.Count,
                TrendDirection = AnalyzeTrend(metrics),
                TrendConfidence = CalculateTrendConfidence(metrics),
                CurrentMetrics = metrics.LastOrDefault() ?? new ActivityMetrics()
            };

            return report;
        }

        public ActivityPrediction PredictActivity(List<ActivityMetrics> historicalData,
                                                 List<ActivityPattern> patterns,
                                                 TimeSpan timeWindow)
        {
            var prediction = new ActivityPrediction;
            {
                PredictionTime = DateTime.UtcNow,
                PredictionWindow = timeWindow,
                ExpectedActivityLevel = PredictActivityLevel(historicalData, patterns),
                Confidence = CalculatePredictionConfidence(historicalData, patterns)
            };

            return prediction;
        }

        private float CalculateConfidence(ActivityData activityData)
        {
            // Basit bir güven skoru hesaplaması;
            return Math.Min(1.0f, activityData.Intensity * _config.Sensitivity);
        }

        private bool CheckForAnomaly(ActivityData activityData)
        {
            // Anomali kontrolü;
            return activityData.Intensity > 0.9f || activityData.Intensity < 0.1f;
        }

        private string DetectAnomalyType(ActivityData activityData)
        {
            return activityData.Intensity > 0.9f ? "HighIntensity" : "LowIntensity";
        }

        private float CalculateAnomalySeverity(ActivityData activityData)
        {
            return Math.Abs(activityData.Intensity - 0.5f) * 2.0f;
        }

        private ActivityTrend AnalyzeTrend(List<ActivityMetrics> metrics)
        {
            if (metrics.Count < 2)
                return ActivityTrend.Stable;

            var recent = metrics.Skip(Math.Max(0, metrics.Count - 5)).ToList();
            var old = metrics.Take(Math.Min(5, metrics.Count - 5)).ToList();

            if (recent.Count == 0 || old.Count == 0)
                return ActivityTrend.Stable;

            var recentAvg = recent.Average(m => m.ActivityCount);
            var oldAvg = old.Average(m => m.ActivityCount);

            if (recentAvg > oldAvg * 1.2)
                return ActivityTrend.Increasing;
            else if (recentAvg < oldAvg * 0.8)
                return ActivityTrend.Decreasing;
            else;
                return ActivityTrend.Stable;
        }

        private float CalculateTrendConfidence(List<ActivityMetrics> metrics)
        {
            return metrics.Count > 10 ? 0.8f : 0.5f;
        }

        private float PredictActivityLevel(List<ActivityMetrics> historicalData, List<ActivityPattern> patterns)
        {
            if (historicalData.Count == 0)
                return 0.5f;

            // Basit tahmin: son aktivite seviyesinin ortalaması;
            return historicalData.Average(m => m.ActivityCount / 100.0f);
        }

        private float CalculatePredictionConfidence(List<ActivityMetrics> historicalData, List<ActivityPattern> patterns)
        {
            var dataCount = historicalData.Count;
            var patternCount = patterns.Count;

            return Math.Min(1.0f, (dataCount * 0.01f + patternCount * 0.05f));
        }
    }

    // Enum'lar ve diğer tip tanımları;
    public enum ActivityType;
    {
        Motion,
        Audio,
        Thermal,
        Presence,
        Interaction,
        Unknown;
    }

    public enum SensorType;
    {
        Motion,
        Audio,
        Thermal,
        Pressure,
        Proximity,
        Camera;
    }

    public enum SensorStatus;
    {
        Initializing,
        Active,
        Inactive,
        Error,
        Disconnected;
    }

    public enum ThresholdType;
    {
        ActivityCount,
        ActivityDuration,
        InactivityDuration,
        Intensity,
        Frequency;
    }

    public enum ComparisonOperator;
    {
        GreaterThan,
        LessThan,
        EqualTo,
        NotEqualTo;
    }

    public enum PatternChangeType;
    {
        NewPattern,
        UpdatedPattern,
        RemovedPattern;
    }

    // ActivityThreshold sınıfı;
    public class ActivityThreshold : IDisposable
    {
        public string ThresholdId { get; }
        public ThresholdType ThresholdType { get; }
        public float ThresholdValue { get; }
        public ComparisonOperator ComparisonOperator { get; }

        public event EventHandler<ThresholdExceededEventArgs> ThresholdExceeded;

        public ActivityThreshold(string thresholdId, ThresholdType type, float value, ComparisonOperator op)
        {
            ThresholdId = thresholdId;
            ThresholdType = type;
            ThresholdValue = value;
            ComparisonOperator = op;
        }

        public bool Check(ActivityEvent activityEvent)
        {
            var currentValue = GetCurrentValue(activityEvent);
            var exceeded = Evaluate(currentValue);

            if (exceeded)
            {
                OnThresholdExceeded(new ThresholdExceededEventArgs;
                {
                    ThresholdId = ThresholdId,
                    ThresholdType = ThresholdType,
                    ExceededValue = currentValue,
                    ThresholdValue = ThresholdValue,
                    Timestamp = DateTime.UtcNow;
                });
            }

            return exceeded;
        }

        public float GetCurrentValue(ActivityEvent activityEvent)
        {
            return ThresholdType switch;
            {
                ThresholdType.Intensity => activityEvent.ActivityData.Intensity,
                _ => 0.0f;
            };
        }

        private bool Evaluate(float value)
        {
            return ComparisonOperator switch;
            {
                ComparisonOperator.GreaterThan => value > ThresholdValue,
                ComparisonOperator.LessThan => value < ThresholdValue,
                ComparisonOperator.EqualTo => Math.Abs(value - ThresholdValue) < 0.001f,
                ComparisonOperator.NotEqualTo => Math.Abs(value - ThresholdValue) > 0.001f,
                _ => false;
            };
        }

        protected virtual void OnThresholdExceeded(ThresholdExceededEventArgs e)
        {
            ThresholdExceeded?.Invoke(this, e);
        }

        public void Dispose()
        {
            // Kaynak temizleme;
        }
    }

    // Diğer event args sınıfları;
    public class SensorActivityEventArgs : EventArgs;
    {
        public string SensorId { get; set; }
        public ActivityType ActivityType { get; set; }
        public float Intensity { get; set; }
        public DateTime Timestamp { get; set; }
        public string Location { get; set; }
        public byte[] RawData { get; set; }
    }

    public class SensorStatusEventArgs : EventArgs;
    {
        public string SensorId { get; set; }
        public SensorStatus PreviousStatus { get; set; }
        public SensorStatus NewStatus { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // ActivityPattern sınıfı (kısmi)
    public class ActivityPattern;
    {
        public string PatternId { get; set; }
        public string PatternName { get; set; }
        public ActivityType ActivityType { get; set; }
        public TimeSpan Duration { get; set; }
        public float Frequency { get; set; }
        public DateTime FirstDetected { get; set; }
        public DateTime LastDetected { get; set; }
        public int DetectionCount { get; set; }
        public float Confidence { get; set; }
        public Dictionary<string, object> PatternData { get; set; } = new Dictionary<string, object>();
    }

    public class AnomalyDetection;
    {
        public string AnomalyId { get; set; }
        public string Type { get; set; }
        public float Severity { get; set; }
        public DateTime DetectedAt { get; set; }
        public ActivityEvent RelatedActivity { get; set; }
    }

    public class Recommendation;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public RecommendationPriority Priority { get; set; }
        public List<string> Actions { get; set; } = new List<string>();
    }

    public enum RecommendationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public class PredictedActivity;
    {
        public ActivityType Type { get; set; }
        public DateTime ExpectedTime { get; set; }
        public float ExpectedIntensity { get; set; }
        public float Confidence { get; set; }
    }

    // Extension methods;
    public static class ActivityMetricsExtensions;
    {
        public static ActivityMetrics Clone(this ActivityMetrics metrics)
        {
            return new ActivityMetrics;
            {
                Timestamp = metrics.Timestamp,
                ActivityCount = metrics.ActivityCount,
                AverageIntensity = metrics.AverageIntensity,
                PeakIntensity = metrics.PeakIntensity,
                AverageDuration = metrics.AverageDuration,
                TotalDuration = metrics.TotalDuration,
                ActivityTypeDistribution = new Dictionary<ActivityType, int>(metrics.ActivityTypeDistribution),
                ActivityFrequency = metrics.ActivityFrequency,
                ConcurrentActivities = metrics.ConcurrentActivities;
            };
        }
    }
    #endregion;
}
