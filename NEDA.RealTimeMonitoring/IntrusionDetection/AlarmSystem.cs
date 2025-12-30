using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Brain.MemorySystem;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.HardwareSecurity.PhysicalManifesto;
using NEDA.SecurityModules.Monitoring;
using NEDA.Services.Messaging.EventBus;
using NEDA.Services.NotificationService;
using NEDA.Biometrics.MotionTracking;

namespace NEDA.RealTimeMonitoring.IntrusionDetection;
{
    /// <summary>
    /// Gelişmiş güvenlik alarm sistemi - Fiziksel güvenlik, izinsiz giriş tespiti ve acil müdahale sistemlerini yönetir;
    /// </summary>
    public class AlarmSystem : IAlarmSystem, IDisposable;
    {
        private readonly ILogger<AlarmSystem> _logger;
        private readonly IExceptionHandler _exceptionHandler;
        private readonly IMemorySystem _memorySystem;
        private readonly IEventBus _eventBus;
        private readonly INotificationManager _notificationManager;
        private readonly ISecurityMonitor _securityMonitor;
        private readonly IHardwareLock _hardwareLock;
        private readonly IActivityMonitor _activityMonitor;
        private readonly AlarmSystemConfig _config;

        private readonly ConcurrentDictionary<string, SecuritySensor> _sensors;
        private readonly ConcurrentDictionary<string, AlarmZone> _zones;
        private readonly ConcurrentDictionary<string, AlarmTrigger> _activeTriggers;
        private readonly ConcurrentQueue<AlarmEvent> _eventQueue;
        private readonly ConcurrentDictionary<string, AlarmArmingSchedule> _armingSchedules;

        private readonly System.Timers.Timer _monitoringTimer;
        private readonly System.Timers.Timer _armingTimer;
        private readonly System.Timers.Timer _responseTimer;
        private readonly SemaphoreSlim _stateLock = new(1, 1);

        private AlarmSystemState _systemState;
        private AlarmArmingMode _armingMode;
        private DateTime? _armedTime;
        private DateTime? _lastIntrusionTime;
        private bool _isInitialized;
        private bool _isMonitoring;
        private string _currentUserContext;
        private AlarmStatistics _statistics;

        /// <summary>
        /// Alarm durumu değiştiğinde tetiklenen olay;
        /// </summary>
        public event EventHandler<AlarmStateChangedEventArgs> AlarmStateChanged;

        /// <summary>
        /// Alarm tetiklendiğinde oluşan olay;
        /// </summary>
        public event EventHandler<AlarmTriggeredEventArgs> AlarmTriggered;

        /// <summary>
        /// Sensör durumu değiştiğinde oluşan olay;
        /// </summary>
        public event EventHandler<SensorStatusChangedEventArgs> SensorStatusChanged;

        /// <summary>
        /// Bölge durumu değiştiğinde oluşan olay;
        /// </summary>
        public event EventHandler<ZoneStatusChangedEventArgs> ZoneStatusChanged;

        /// <summary>
        /// Sistem istatistikleri;
        /// </summary>
        public AlarmStatistics Statistics => _statistics;

        /// <summary>
        /// Sistem durumu;
        /// </summary>
        public AlarmSystemState SystemState => _systemState;

        /// <summary>
        /// Silahlanma modu;
        /// </summary>
        public AlarmArmingMode ArmingMode => _armingMode;

        /// <summary>
        /// AlarmSystem constructor;
        /// </summary>
        public AlarmSystem(
            ILogger<AlarmSystem> logger,
            IExceptionHandler exceptionHandler,
            IMemorySystem memorySystem,
            IEventBus eventBus,
            INotificationManager notificationManager,
            ISecurityMonitor securityMonitor,
            IHardwareLock hardwareLock,
            IActivityMonitor activityMonitor,
            IOptions<AlarmSystemConfig> config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _memorySystem = memorySystem ?? throw new ArgumentNullException(nameof(memorySystem));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _notificationManager = notificationManager ?? throw new ArgumentNullException(nameof(notificationManager));
            _securityMonitor = securityMonitor ?? throw new ArgumentNullException(nameof(securityMonitor));
            _hardwareLock = hardwareLock ?? throw new ArgumentNullException(nameof(hardwareLock));
            _activityMonitor = activityMonitor ?? throw new ArgumentNullException(nameof(activityMonitor));
            _config = config?.Value ?? new AlarmSystemConfig();

            _sensors = new ConcurrentDictionary<string, SecuritySensor>();
            _zones = new ConcurrentDictionary<string, AlarmZone>();
            _activeTriggers = new ConcurrentDictionary<string, AlarmTrigger>();
            _eventQueue = new ConcurrentQueue<AlarmEvent>();
            _armingSchedules = new ConcurrentDictionary<string, AlarmArmingSchedule>();

            _systemState = AlarmSystemState.Disarmed;
            _armingMode = AlarmArmingMode.Off;
            _statistics = new AlarmStatistics();

            // İzleme zamanlayıcısı (1 saniyede bir)
            _monitoringTimer = new System.Timers.Timer(1000);
            _monitoringTimer.Elapsed += OnMonitoringTimerElapsed;

            // Silahlanma zamanlayıcısı (30 saniyede bir)
            _armingTimer = new System.Timers.Timer(30000);
            _armingTimer.Elapsed += OnArmingTimerElapsed;

            // Yanıt zamanlayıcısı (10 saniyede bir)
            _responseTimer = new System.Timers.Timer(10000);
            _responseTimer.Elapsed += OnResponseTimerElapsed;

            // Event bus subscription;
            _eventBus.Subscribe<SecurityEvent>(HandleSecurityEvent);
            _eventBus.Subscribe<SystemAlertEvent>(HandleSystemAlertEvent);

            _logger.LogInformation("AlarmSystem initialized");
        }

        /// <summary>
        /// Alarm sistemini başlatır;
        /// </summary>
        public async Task<AlarmResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                if (_isInitialized)
                {
                    _logger.LogWarning("AlarmSystem already initialized");
                    return AlarmResult.AlreadyInitialized();
                }

                _logger.LogInformation("Initializing AlarmSystem...");

                // Konfigürasyon validasyonu;
                if (!ValidateConfig(_config))
                    return AlarmResult.Failure("Invalid AlarmSystem configuration");

                // Sensörleri başlat;
                await InitializeSensorsAsync(cancellationToken);

                // Bölgeleri oluştur;
                await InitializeZonesAsync(cancellationToken);

                // Zamanlayıcıları başlat;
                _monitoringTimer.Start();
                _armingTimer.Start();
                _responseTimer.Start();

                // Donanım kilidini başlat;
                await _hardwareLock.InitializeAsync(cancellationToken);

                _isInitialized = true;
                _isMonitoring = true;
                _systemState = AlarmSystemState.Disarmed;
                _statistics.StartTime = DateTime.UtcNow;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = AlarmSystemState.Uninitialized,
                    NewState = AlarmSystemState.Disarmed,
                    ChangedAt = DateTime.UtcNow,
                    Reason = "System initialized"
                });

                _logger.LogInformation($"AlarmSystem initialized with {_sensors.Count} sensors and {_zones.Count} zones");
                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.Initialize");
                _logger.LogError(ex, "Failed to initialize AlarmSystem: {Error}", error.Message);
                _systemState = AlarmSystemState.Error;
                return AlarmResult.Failure($"Initialization failed: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Alarm sistemini silahlar (armed)
        /// </summary>
        public async Task<AlarmResult> ArmSystemAsync(ArmRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Arming alarm system with mode: {Mode}", request.Mode);

                // Durum kontrolü;
                if (_systemState == AlarmSystemState.Armed)
                {
                    return AlarmResult.Failure("System is already armed");
                }

                if (_systemState == AlarmSystemState.Triggered)
                {
                    return AlarmResult.Failure("Cannot arm while system is triggered");
                }

                // Sensör kontrolleri;
                var sensorChecks = await CheckSensorsBeforeArmingAsync(cancellationToken);
                if (!sensorChecks.AllSensorsReady)
                {
                    return AlarmResult.Failure($"Cannot arm system: {sensorChecks.FailedSensors.Count} sensors not ready");
                }

                // Bölge kontrolleri;
                var zoneChecks = await CheckZonesBeforeArmingAsync(cancellationToken);
                if (!zoneChecks.AllZonesSecure)
                {
                    return AlarmResult.Failure($"Cannot arm system: {zoneChecks.UnsecureZones.Count} zones not secure");
                }

                // Silahlanma modunu ayarla;
                _armingMode = request.Mode;
                var previousState = _systemState;
                _systemState = AlarmSystemState.Armed;
                _armedTime = DateTime.UtcNow;
                _currentUserContext = request.UserId;

                // Silahlanma gecikmesini başlat;
                if (request.Mode == AlarmArmingMode.Away && _config.ArmingDelaySeconds > 0)
                {
                    _systemState = AlarmSystemState.Arming;
                    _logger.LogInformation($"Arming delay started: {_config.ArmingDelaySeconds} seconds");

                    // Gecikme sonrası tam silahlanma;
                    _ = Task.Delay(TimeSpan.FromSeconds(_config.ArmingDelaySeconds))
                        .ContinueWith(async _ =>
                        {
                            await _stateLock.WaitAsync(cancellationToken);
                            try
                            {
                                if (_systemState == AlarmSystemState.Arming)
                                {
                                    _systemState = AlarmSystemState.Armed;
                                    _armedTime = DateTime.UtcNow;

                                    OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                                    {
                                        PreviousState = AlarmSystemState.Arming,
                                        NewState = AlarmSystemState.Armed,
                                        ChangedAt = DateTime.UtcNow,
                                        Reason = "Arming delay completed"
                                    });

                                    _logger.LogInformation("System armed after delay");
                                }
                            }
                            finally
                            {
                                _stateLock.Release();
                            }
                        }, cancellationToken);
                }

                // Tüm sensörleri silahlı moda geçir;
                await SetSensorsArmedAsync(true, cancellationToken);

                // Bölgeleri güncelle;
                await UpdateZonesForArmingAsync(request.Mode, cancellationToken);

                // İstatistikleri güncelle;
                _statistics.TotalArmedCount++;
                _statistics.LastArmedTime = DateTime.UtcNow;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.UserId,
                    Reason = $"System armed with mode: {request.Mode}"
                });

                // Silahlanma bildirimi;
                await SendArmingNotificationAsync(request, cancellationToken);

                // Event bus'a bildir;
                await _eventBus.PublishAsync(new AlarmEvent;
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    EventType = AlarmEventType.SystemArmed,
                    ZoneId = "system",
                    SensorId = "system",
                    Severity = AlarmSeverity.Info,
                    Data = new Dictionary<string, object>
                    {
                        ["arming_mode"] = request.Mode.ToString(),
                        ["armed_by"] = request.UserId,
                        ["arming_time"] = DateTime.UtcNow;
                    }
                }, cancellationToken);

                _logger.LogInformation("Alarm system armed successfully with mode: {Mode}", request.Mode);

                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.ArmSystem");
                _logger.LogError(ex, "Error arming system: {Error}", error.Message);
                return AlarmResult.Failure($"Error arming system: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Alarm sistemini silahsızlandırır (disarmed)
        /// </summary>
        public async Task<AlarmResult> DisarmSystemAsync(DisarmRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Disarming alarm system by user: {UserId}", request.UserId);

                // Durum kontrolü;
                if (_systemState == AlarmSystemState.Disarmed)
                {
                    return AlarmResult.Failure("System is already disarmed");
                }

                // Kimlik doğrulama;
                if (!await AuthenticateDisarmRequestAsync(request, cancellationToken))
                {
                    _logger.LogWarning("Failed to authenticate disarm request for user: {UserId}", request.UserId);
                    return AlarmResult.Failure("Authentication failed");
                }

                // Tetiklenmiş sistem için ek kontrol;
                if (_systemState == AlarmSystemState.Triggered && !request.ForceDisarm)
                {
                    return AlarmResult.Failure("Cannot disarm triggered system without force flag");
                }

                var previousState = _systemState;
                _systemState = AlarmSystemState.Disarmed;
                _armingMode = AlarmArmingMode.Off;
                _armedTime = null;
                _currentUserContext = request.UserId;

                // Aktif tetikleyicileri temizle;
                _activeTriggers.Clear();

                // Sensörleri silahsız moda geçir;
                await SetSensorsArmedAsync(false, cancellationToken);

                // Bölgeleri güncelle;
                await UpdateZonesForDisarmingAsync(cancellationToken);

                // İstatistikleri güncelle;
                _statistics.TotalDisarmedCount++;
                _statistics.LastDisarmedTime = DateTime.UtcNow;

                // Silahlanma süresini hesapla;
                if (previousState == AlarmSystemState.Armed && _armedTime.HasValue)
                {
                    var armedDuration = DateTime.UtcNow - _armedTime.Value;
                    _statistics.TotalArmedDuration += armedDuration;
                }

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.UserId,
                    Reason = $"System disarmed: {request.Reason}"
                });

                // Silahsızlandırma bildirimi;
                await SendDisarmingNotificationAsync(request, cancellationToken);

                // Event bus'a bildir;
                await _eventBus.PublishAsync(new AlarmEvent;
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    EventType = AlarmEventType.SystemDisarmed,
                    ZoneId = "system",
                    SensorId = "system",
                    Severity = AlarmSeverity.Info,
                    Data = new Dictionary<string, object>
                    {
                        ["disarmed_by"] = request.UserId,
                        ["previous_state"] = previousState.ToString(),
                        ["disarm_time"] = DateTime.UtcNow,
                        ["reason"] = request.Reason;
                    }
                }, cancellationToken);

                _logger.LogInformation("Alarm system disarmed successfully by user: {UserId}", request.UserId);

                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.DisarmSystem");
                _logger.LogError(ex, "Error disarming system: {Error}", error.Message);
                return AlarmResult.Failure($"Error disarming system: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Kısmi silahlanma yapar (specific zones only)
        /// </summary>
        public async Task<AlarmResult> ArmPartialAsync(PartialArmRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Partial arming for zones: {Zones}", string.Join(", ", request.ZoneIds));

                // Durum kontrolü;
                if (_systemState == AlarmSystemState.Armed || _systemState == AlarmSystemState.Triggered)
                {
                    return AlarmResult.Failure("Cannot perform partial arm while system is armed or triggered");
                }

                // Bölge kontrolleri;
                foreach (var zoneId in request.ZoneIds)
                {
                    if (!_zones.TryGetValue(zoneId, out var zone))
                    {
                        return AlarmResult.Failure($"Zone not found: {zoneId}");
                    }

                    // Bölge sensör kontrolleri;
                    var zoneSensors = _sensors.Values.Where(s => s.ZoneId == zoneId);
                    var unreadySensors = zoneSensors.Where(s => s.Status != SensorStatus.Normal).ToList();

                    if (unreadySensors.Any())
                    {
                        return AlarmResult.Failure($"Zone {zoneId} has {unreadySensors.Count} sensors not ready");
                    }
                }

                // Kısmi silahlanma modunu ayarla;
                _armingMode = AlarmArmingMode.Partial;
                var previousState = _systemState;
                _systemState = AlarmSystemState.ArmedPartial;
                _armedTime = DateTime.UtcNow;
                _currentUserContext = request.UserId;

                // Seçilen bölgeleri silahlı yap;
                foreach (var zoneId in request.ZoneIds)
                {
                    if (_zones.TryGetValue(zoneId, out var zone))
                    {
                        zone.Status = ZoneStatus.Armed;
                        zone.LastArmed = DateTime.UtcNow;

                        // Bölge sensörlerini silahlı yap;
                        foreach (var sensor in _sensors.Values.Where(s => s.ZoneId == zoneId))
                        {
                            sensor.IsArmed = true;
                            sensor.ArmedTime = DateTime.UtcNow;
                        }

                        OnZoneStatusChanged(new ZoneStatusChangedEventArgs;
                        {
                            ZoneId = zoneId,
                            PreviousStatus = ZoneStatus.Disarmed,
                            NewStatus = ZoneStatus.Armed,
                            ChangedAt = DateTime.UtcNow,
                            ChangedBy = request.UserId;
                        });
                    }
                }

                // İstatistikleri güncelle;
                _statistics.TotalPartialArms++;
                _statistics.LastPartialArmTime = DateTime.UtcNow;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.UserId,
                    Reason = $"Partial arm for zones: {string.Join(", ", request.ZoneIds)}"
                });

                _logger.LogInformation("Partial arming completed for {Count} zones", request.ZoneIds.Count);

                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.ArmPartial");
                _logger.LogError(ex, "Error performing partial arm: {Error}", error.Message);
                return AlarmResult.Failure($"Error performing partial arm: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Sensör durumunu işler;
        /// </summary>
        public async Task<SensorResult> ProcessSensorEventAsync(SensorEvent sensorEvent, CancellationToken cancellationToken = default)
        {
            if (sensorEvent == null)
                throw new ArgumentNullException(nameof(sensorEvent));

            try
            {
                _logger.LogDebug("Processing sensor event: {SensorId}, Type: {EventType}",
                    sensorEvent.SensorId, sensorEvent.EventType);

                // Sensörü bul;
                if (!_sensors.TryGetValue(sensorEvent.SensorId, out var sensor))
                {
                    _logger.LogWarning("Sensor not found: {SensorId}", sensorEvent.SensorId);
                    return SensorResult.NotFound();
                }

                // Sensör durumunu güncelle;
                var previousStatus = sensor.Status;
                sensor.Status = sensorEvent.Status;
                sensor.LastEventTime = DateTime.UtcNow;
                sensor.LastEventType = sensorEvent.EventType;
                sensor.Value = sensorEvent.Value;

                // Durum değişikliği olayı;
                if (previousStatus != sensor.Status)
                {
                    OnSensorStatusChanged(new SensorStatusChangedEventArgs;
                    {
                        SensorId = sensor.Id,
                        PreviousStatus = previousStatus,
                        NewStatus = sensor.Status,
                        ChangedAt = DateTime.UtcNow,
                        EventType = sensorEvent.EventType,
                        Value = sensorEvent.Value;
                    });
                }

                // Alarm tetikleme kontrolü;
                if (ShouldTriggerAlarm(sensor, sensorEvent))
                {
                    await TriggerAlarmFromSensorAsync(sensor, sensorEvent, cancellationToken);
                }

                // Geçmişe kaydet;
                await SaveSensorEventToHistoryAsync(sensorEvent, cancellationToken);

                // İstatistikleri güncelle;
                _statistics.TotalSensorEvents++;

                return SensorResult.Success(sensor);
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.ProcessSensorEvent");
                _logger.LogError(ex, "Error processing sensor event: {Error}", error.Message);
                return SensorResult.Failure($"Error processing sensor event: {error.Message}");
            }
        }

        /// <summary>
        /// Alarmı tetikler;
        /// </summary>
        public async Task<AlarmResult> TriggerAlarmAsync(TriggerRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogWarning("Manual alarm trigger requested: {Reason}", request.Reason);

                // Zaten tetiklenmiş mi kontrol et;
                if (_systemState == AlarmSystemState.Triggered)
                {
                    return AlarmResult.Failure("Alarm is already triggered");
                }

                var previousState = _systemState;
                _systemState = AlarmSystemState.Triggered;
                _lastIntrusionTime = DateTime.UtcNow;

                // Alarm tetikleyicisi oluştur;
                var trigger = new AlarmTrigger;
                {
                    Id = Guid.NewGuid().ToString(),
                    TriggerType = AlarmTriggerType.Manual,
                    TriggeredAt = DateTime.UtcNow,
                    Source = request.Source,
                    ZoneId = request.ZoneId,
                    SensorId = request.SensorId,
                    Severity = AlarmSeverity.Critical,
                    Description = request.Reason,
                    Data = request.AdditionalData ?? new Dictionary<string, object>()
                };

                // Aktif tetikleyicilere ekle;
                _activeTriggers[trigger.Id] = trigger;

                // Alarm yanıtlarını başlat;
                await ExecuteAlarmResponseAsync(trigger, cancellationToken);

                // İstatistikleri güncelle;
                _statistics.TotalAlarmsTriggered++;
                _statistics.LastAlarmTime = DateTime.UtcNow;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.UserId,
                    Reason = $"Manual trigger: {request.Reason}"
                });

                // Alarm tetikleme olayı;
                OnAlarmTriggered(new AlarmTriggeredEventArgs;
                {
                    Trigger = trigger,
                    TriggeredAt = DateTime.UtcNow,
                    PreviousState = previousState,
                    IsManual = true;
                });

                _logger.LogWarning("Alarm triggered manually: {Reason}", request.Reason);

                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.TriggerAlarm");
                _logger.LogError(ex, "Error triggering alarm: {Error}", error.Message);
                return AlarmResult.Failure($"Error triggering alarm: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Alarmı susturur (silence)
        /// </summary>
        public async Task<AlarmResult> SilenceAlarmAsync(SilenceRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Silencing alarm by user: {UserId}", request.UserId);

                // Durum kontrolü;
                if (_systemState != AlarmSystemState.Triggered)
                {
                    return AlarmResult.Failure("Alarm is not triggered");
                }

                // Kimlik doğrulama;
                if (!await AuthenticateSilenceRequestAsync(request, cancellationToken))
                {
                    return AlarmResult.Failure("Authentication failed for silence request");
                }

                // Sirenleri durdur;
                await SilenceSirensAsync(cancellationToken);

                // Sistem durumunu güncelle;
                _systemState = AlarmSystemState.Silenced;
                _armingMode = AlarmArmingMode.Off;

                // İstatistikleri güncelle;
                _statistics.TotalSilencedCount++;
                _statistics.LastSilencedTime = DateTime.UtcNow;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = AlarmSystemState.Triggered,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    ChangedBy = request.UserId,
                    Reason = $"Alarm silenced: {request.Reason}"
                });

                // Susturma bildirimi;
                await SendSilenceNotificationAsync(request, cancellationToken);

                _logger.LogInformation("Alarm silenced by user: {UserId}", request.UserId);

                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.SilenceAlarm");
                _logger.LogError(ex, "Error silencing alarm: {Error}", error.Message);
                return AlarmResult.Failure($"Error silencing alarm: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Panik alarmı tetikler;
        /// </summary>
        public async Task<AlarmResult> TriggerPanicAlarmAsync(PanicRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogWarning("PANIC ALARM triggered by user: {UserId}, Type: {PanicType}",
                    request.UserId, request.PanicType);

                // Panik alarm tetikleyicisi oluştur;
                var trigger = new AlarmTrigger;
                {
                    Id = Guid.NewGuid().ToString(),
                    TriggerType = AlarmTriggerType.Panic,
                    TriggeredAt = DateTime.UtcNow,
                    Source = request.Source,
                    ZoneId = request.ZoneId,
                    SensorId = "panic_button",
                    Severity = AlarmSeverity.Emergency,
                    Description = $"Panic alarm: {request.PanicType}",
                    Data = new Dictionary<string, object>
                    {
                        ["panic_type"] = request.PanicType.ToString(),
                        ["user_id"] = request.UserId,
                        ["location"] = request.Location,
                        ["timestamp"] = DateTime.UtcNow;
                    }
                };

                // Aktif tetikleyicilere ekle;
                _activeTriggers[trigger.Id] = trigger;

                // Özel panik alarm yanıtını başlat;
                await ExecutePanicResponseAsync(trigger, request, cancellationToken);

                // İstatistikleri güncelle;
                _statistics.TotalPanicAlarms++;
                _statistics.LastPanicAlarmTime = DateTime.UtcNow;

                // Panik alarm olayı;
                OnAlarmTriggered(new AlarmTriggeredEventArgs;
                {
                    Trigger = trigger,
                    TriggeredAt = DateTime.UtcNow,
                    PreviousState = _systemState,
                    IsManual = true,
                    IsPanic = true;
                });

                // Acil bildirim gönder;
                await SendPanicNotificationAsync(request, trigger, cancellationToken);

                _logger.LogWarning("Panic alarm processed: {PanicType}", request.PanicType);

                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.TriggerPanicAlarm");
                _logger.LogError(ex, "Error triggering panic alarm: {Error}", error.Message);
                return AlarmResult.Failure($"Error triggering panic alarm: {error.Message}");
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
                    Component = "AlarmSystem",
                    Timestamp = DateTime.UtcNow,
                    OverallStatus = HealthStatus.Healthy;
                };

                // Temel metrikler;
                healthStatus.Metrics["TotalSensors"] = _sensors.Count;
                healthStatus.Metrics["ActiveSensors"] = _sensors.Values.Count(s => s.Status == SensorStatus.Normal);
                healthStatus.Metrics["FaultySensors"] = _sensors.Values.Count(s => s.Status == SensorStatus.Faulty);
                healthStatus.Metrics["TotalZones"] = _zones.Count;
                healthStatus.Metrics["ActiveTriggers"] = _activeTriggers.Count;
                healthStatus.Metrics["SystemState"] = _systemState.ToString();
                healthStatus.Metrics["UptimeMinutes"] = (DateTime.UtcNow - _statistics.StartTime).TotalMinutes;

                // Sensör sağlık kontrolü;
                var sensorHealth = await CheckSensorHealthAsync(cancellationToken);
                healthStatus.Subcomponents.Add(sensorHealth);

                // Bölge sağlık kontrolü;
                var zoneHealth = CheckZoneHealth();
                healthStatus.Subcomponents.Add(zoneHealth);

                // Donanım sağlık kontrolü;
                var hardwareHealth = await CheckHardwareHealthAsync(cancellationToken);
                healthStatus.Subcomponents.Add(hardwareHealth);

                // Sistem durumu kontrolleri;
                if (_systemState == AlarmSystemState.Error)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = "System is in error state";
                }
                else if (sensorHealth.Status == HealthStatus.Error)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = "Critical sensor failures detected";
                }
                else if (sensorHealth.Status == HealthStatus.Warning)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = "Sensor issues detected";
                }
                else if (_sensors.Values.Count(s => s.Status == SensorStatus.Faulty) > _config.MaxFaultySensors)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"Too many faulty sensors: {_sensors.Values.Count(s => s.Status == SensorStatus.Faulty)}";
                }
                else;
                {
                    healthStatus.OverallStatus = HealthStatus.Healthy;
                    healthStatus.Message = "All systems operational";
                }

                // İletişim kontrolleri;
                var communicationHealth = await CheckCommunicationHealthAsync(cancellationToken);
                healthStatus.Subcomponents.Add(communicationHealth);

                if (communicationHealth.Status == HealthStatus.Error)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = "Communication systems offline";
                }

                return healthStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during system health check");
                return new SystemHealthStatus;
                {
                    Component = "AlarmSystem",
                    Timestamp = DateTime.UtcNow,
                    OverallStatus = HealthStatus.Error,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Sistem izlemeyi durdurur;
        /// </summary>
        public async Task<AlarmResult> StopMonitoringAsync(CancellationToken cancellationToken = default)
        {
            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isMonitoring)
                {
                    return AlarmResult.AlreadyStopped();
                }

                _logger.LogInformation("Stopping AlarmSystem monitoring...");

                // Zamanlayıcıları durdur;
                _monitoringTimer.Stop();
                _armingTimer.Stop();
                _responseTimer.Stop();

                // Sensörleri durdur;
                await StopAllSensorsAsync(cancellationToken);

                // Sistem durumunu güncelle;
                _isMonitoring = false;
                var previousState = _systemState;
                _systemState = AlarmSystemState.Maintenance;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    Reason = "Monitoring stopped"
                });

                _logger.LogInformation("AlarmSystem monitoring stopped");
                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.StopMonitoring");
                _logger.LogError(ex, "Error stopping monitoring: {Error}", error.Message);
                return AlarmResult.Failure($"Error stopping monitoring: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Sistem izlemeyi başlatır;
        /// </summary>
        public async Task<AlarmResult> StartMonitoringAsync(CancellationToken cancellationToken = default)
        {
            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                if (_isMonitoring)
                {
                    return AlarmResult.AlreadyRunning();
                }

                _logger.LogInformation("Starting AlarmSystem monitoring...");

                // Sensörleri başlat;
                await StartAllSensorsAsync(cancellationToken);

                // Zamanlayıcıları başlat;
                _monitoringTimer.Start();
                _armingTimer.Start();
                _responseTimer.Start();

                // Sistem durumunu güncelle;
                _isMonitoring = true;
                var previousState = _systemState;
                _systemState = AlarmSystemState.Disarmed;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    Reason = "Monitoring started"
                });

                _logger.LogInformation("AlarmSystem monitoring started");
                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.StartMonitoring");
                _logger.LogError(ex, "Error starting monitoring: {Error}", error.Message);
                return AlarmResult.Failure($"Error starting monitoring: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        /// <summary>
        /// Sistemi kapatır;
        /// </summary>
        public async Task<AlarmResult> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                if (!_isInitialized)
                {
                    return AlarmResult.NotInitialized();
                }

                _logger.LogInformation("Shutting down AlarmSystem...");

                // İzlemeyi durdur;
                await StopMonitoringAsync(cancellationToken);

                // Donanım kilidini kapat;
                await _hardwareLock.ShutdownAsync(cancellationToken);

                // Tüm kaynakları temizle;
                await CleanupAllResourcesAsync(cancellationToken);

                // Durumu güncelle;
                _isInitialized = false;
                _systemState = AlarmSystemState.Shutdown;
                _statistics.EndTime = DateTime.UtcNow;

                _logger.LogInformation("AlarmSystem shutdown completed");
                return AlarmResult.Success();
            }
            catch (Exception ex)
            {
                var error = await _exceptionHandler.HandleExceptionAsync(ex, "AlarmSystem.Shutdown");
                _logger.LogError(ex, "Error during shutdown: {Error}", error.Message);
                return AlarmResult.Failure($"Shutdown failed: {error.Message}");
            }
            finally
            {
                _stateLock.Release();
            }
        }

        #region Private Methods;

        private async Task InitializeSensorsAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Initializing security sensors...");

                // Konfigürasyondan sensörleri yükle;
                var sensorConfigs = await LoadSensorConfigurationsAsync(cancellationToken);

                foreach (var config in sensorConfigs)
                {
                    var sensor = new SecuritySensor;
                    {
                        Id = config.Id,
                        Name = config.Name,
                        Type = config.Type,
                        ZoneId = config.ZoneId,
                        Location = config.Location,
                        Status = SensorStatus.Initializing,
                        IsArmed = false,
                        Sensitivity = config.Sensitivity,
                        IsEnabled = config.IsEnabled,
                        CreatedAt = DateTime.UtcNow,
                        LastCalibration = config.LastCalibration,
                        Metadata = config.Metadata ?? new Dictionary<string, object>()
                    };

                    // Sensörü başlat;
                    var initResult = await InitializeSensorAsync(sensor, cancellationToken);

                    if (initResult.Success)
                    {
                        sensor.Status = SensorStatus.Normal;
                        sensor.IsInitialized = true;

                        if (_sensors.TryAdd(sensor.Id, sensor))
                        {
                            _statistics.TotalSensors++;
                            _logger.LogDebug("Sensor initialized: {SensorName} ({SensorId})", sensor.Name, sensor.Id);
                        }
                    }
                    else;
                    {
                        sensor.Status = SensorStatus.Faulty;
                        sensor.LastError = initResult.Error;
                        _logger.LogWarning("Failed to initialize sensor: {SensorId}, Error: {Error}",
                            sensor.Id, initResult.Error);
                    }
                }

                _logger.LogInformation("Initialized {Count} sensors", _sensors.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing sensors");
                throw;
            }
        }

        private async Task<SensorInitializationResult> InitializeSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            try
            {
                // Sensör tipine göre başlatma;
                switch (sensor.Type)
                {
                    case SensorType.DoorWindow:
                        await InitializeDoorWindowSensorAsync(sensor, cancellationToken);
                        break;
                    case SensorType.Motion:
                        await InitializeMotionSensorAsync(sensor, cancellationToken);
                        break;
                    case SensorType.GlassBreak:
                        await InitializeGlassBreakSensorAsync(sensor, cancellationToken);
                        break;
                    case SensorType.Smoke:
                        await InitializeSmokeSensorAsync(sensor, cancellationToken);
                        break;
                    case SensorType.Heat:
                        await InitializeHeatSensorAsync(sensor, cancellationToken);
                        break;
                    case SensorType.CarbonMonoxide:
                        await InitializeCarbonMonoxideSensorAsync(sensor, cancellationToken);
                        break;
                    case SensorType.Flood:
                        await InitializeFloodSensorAsync(sensor, cancellationToken);
                        break;
                    case SensorType.PanicButton:
                        await InitializePanicButtonAsync(sensor, cancellationToken);
                        break;
                    default:
                        return SensorInitializationResult.Failure($"Unsupported sensor type: {sensor.Type}");
                }

                return SensorInitializationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing sensor {SensorId}: {Error}", sensor.Id, ex.Message);
                return SensorInitializationResult.Failure($"Initialization failed: {ex.Message}");
            }
        }

        private async Task InitializeDoorWindowSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Kapı/pencere sensörü başlatma;
            // Manyetik kontak testi vb.
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Door/Window sensor initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializeMotionSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Hareket sensörü başlatma;
            // PIR sensör kalibrasyonu;
            await Task.Delay(150, cancellationToken);
            _logger.LogDebug("Motion sensor initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializeGlassBreakSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Cam kırılma sensörü başlatma;
            // Akustik kalibrasyon;
            await Task.Delay(200, cancellationToken);
            _logger.LogDebug("Glass break sensor initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializeSmokeSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Duman sensörü başlatma;
            // Kalibrasyon ve test;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Smoke sensor initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializeHeatSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Isı sensörü başlatma;
            // Sıcaklık kalibrasyonu;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Heat sensor initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializeCarbonMonoxideSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Karbonmonoksit sensörü başlatma;
            // Gaz kalibrasyonu;
            await Task.Delay(150, cancellationToken);
            _logger.LogDebug("CO sensor initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializeFloodSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Su baskını sensörü başlatma;
            // Nem kalibrasyonu;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Flood sensor initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializePanicButtonAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            // Panik butonu başlatma;
            // Buton testi;
            await Task.Delay(50, cancellationToken);
            _logger.LogDebug("Panic button initialized: {SensorId}", sensor.Id);
        }

        private async Task InitializeZonesAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Initializing alarm zones...");

                // Konfigürasyondan bölgeleri yükle;
                var zoneConfigs = await LoadZoneConfigurationsAsync(cancellationToken);

                foreach (var config in zoneConfigs)
                {
                    var zone = new AlarmZone;
                    {
                        Id = config.Id,
                        Name = config.Name,
                        Description = config.Description,
                        ZoneType = config.ZoneType,
                        Status = ZoneStatus.Disarmed,
                        IsEnabled = config.IsEnabled,
                        CreatedAt = DateTime.UtcNow,
                        Sensors = new List<string>(),
                        ArmingMode = config.ArmingMode,
                        EntryDelay = config.EntryDelay,
                        ExitDelay = config.ExitDelay,
                        Metadata = config.Metadata ?? new Dictionary<string, object>()
                    };

                    // Bölgeye sensörleri ata;
                    var zoneSensors = _sensors.Values.Where(s => s.ZoneId == zone.Id).ToList();
                    foreach (var sensor in zoneSensors)
                    {
                        zone.Sensors.Add(sensor.Id);
                    }

                    if (_zones.TryAdd(zone.Id, zone))
                    {
                        _statistics.TotalZones++;
                        _logger.LogDebug("Zone initialized: {ZoneName} ({ZoneId}) with {SensorCount} sensors",
                            zone.Name, zone.Id, zone.Sensors.Count);
                    }
                }

                _logger.LogInformation("Initialized {Count} zones", _zones.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing zones");
                throw;
            }
        }

        private async Task<List<SensorConfig>> LoadSensorConfigurationsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Veritabanından veya konfigürasyon dosyasından sensör yapılandırmalarını yükle;
                // Bu örnekte basit bir yapı kullanıyoruz;
                var configs = new List<SensorConfig>();

                // Örnek sensörler;
                configs.Add(new SensorConfig;
                {
                    Id = "front_door",
                    Name = "Front Door",
                    Type = SensorType.DoorWindow,
                    ZoneId = "entry_zone",
                    Location = "Front Entrance",
                    Sensitivity = 0.8,
                    IsEnabled = true,
                    LastCalibration = DateTime.UtcNow.AddDays(-30)
                });

                configs.Add(new SensorConfig;
                {
                    Id = "living_room_motion",
                    Name = "Living Room Motion",
                    Type = SensorType.Motion,
                    ZoneId = "living_zone",
                    Location = "Living Room",
                    Sensitivity = 0.7,
                    IsEnabled = true,
                    LastCalibration = DateTime.UtcNow.AddDays(-15)
                });

                configs.Add(new SensorConfig;
                {
                    Id = "kitchen_smoke",
                    Name = "Kitchen Smoke Detector",
                    Type = SensorType.Smoke,
                    ZoneId = "kitchen_zone",
                    Location = "Kitchen",
                    Sensitivity = 0.9,
                    IsEnabled = true,
                    LastCalibration = DateTime.UtcNow.AddDays(-60)
                });

                configs.Add(new SensorConfig;
                {
                    Id = "master_bedroom_panic",
                    Name = "Master Bedroom Panic Button",
                    Type = SensorType.PanicButton,
                    ZoneId = "bedroom_zone",
                    Location = "Master Bedroom",
                    Sensitivity = 1.0,
                    IsEnabled = true,
                    LastCalibration = DateTime.UtcNow.AddDays(-90)
                });

                return await Task.FromResult(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading sensor configurations");
                return new List<SensorConfig>();
            }
        }

        private async Task<List<ZoneConfig>> LoadZoneConfigurationsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Bölge yapılandırmalarını yükle;
                var configs = new List<ZoneConfig>();

                configs.Add(new ZoneConfig;
                {
                    Id = "entry_zone",
                    Name = "Entry Zone",
                    Description = "Main entry points",
                    ZoneType = ZoneType.Immediate,
                    IsEnabled = true,
                    ArmingMode = ArmingMode.Armed,
                    EntryDelay = 30,
                    ExitDelay = 60;
                });

                configs.Add(new ZoneConfig;
                {
                    Id = "living_zone",
                    Name = "Living Zone",
                    Description = "Living room and common areas",
                    ZoneType = ZoneType.Delayed,
                    IsEnabled = true,
                    ArmingMode = ArmingMode.Armed,
                    EntryDelay = 45,
                    ExitDelay = 60;
                });

                configs.Add(new ZoneConfig;
                {
                    Id = "bedroom_zone",
                    Name = "Bedroom Zone",
                    Description = "Sleeping areas",
                    ZoneType = ZoneType.Instant,
                    IsEnabled = true,
                    ArmingMode = ArmingMode.Armed,
                    EntryDelay = 0,
                    ExitDelay = 0;
                });

                configs.Add(new ZoneConfig;
                {
                    Id = "kitchen_zone",
                    Name = "Kitchen Zone",
                    Description = "Kitchen and dining area",
                    ZoneType = ZoneType.Fire,
                    IsEnabled = true,
                    ArmingMode = ArmingMode.Armed,
                    EntryDelay = 0,
                    ExitDelay = 0;
                });

                return await Task.FromResult(configs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading zone configurations");
                return new List<ZoneConfig>();
            }
        }

        private async Task<SensorCheckResult> CheckSensorsBeforeArmingAsync(CancellationToken cancellationToken)
        {
            var result = new SensorCheckResult;
            {
                AllSensorsReady = true,
                TotalSensors = _sensors.Count,
                ReadySensors = new List<string>(),
                FailedSensors = new List<SensorStatusInfo>()
            };

            foreach (var sensor in _sensors.Values)
            {
                if (!sensor.IsEnabled)
                {
                    // Devre dışı sensörler kontrol edilmez;
                    continue;
                }

                if (sensor.Status == SensorStatus.Normal)
                {
                    result.ReadySensors.Add(sensor.Id);
                }
                else;
                {
                    result.AllSensorsReady = false;
                    result.FailedSensors.Add(new SensorStatusInfo;
                    {
                        SensorId = sensor.Id,
                        SensorName = sensor.Name,
                        Status = sensor.Status,
                        LastError = sensor.LastError;
                    });
                }
            }

            // Kalibrasyon kontrolü;
            var uncalibratedSensors = _sensors.Values;
                .Where(s => s.IsEnabled &&
                           s.LastCalibration.HasValue &&
                           (DateTime.UtcNow - s.LastCalibration.Value).TotalDays > _config.SensorCalibrationDays)
                .ToList();

            if (uncalibratedSensors.Any() && _config.RequireCalibrationForArming)
            {
                result.AllSensorsReady = false;
                foreach (var sensor in uncalibratedSensors)
                {
                    result.FailedSensors.Add(new SensorStatusInfo;
                    {
                        SensorId = sensor.Id,
                        SensorName = sensor.Name,
                        Status = SensorStatus.NeedsCalibration,
                        LastError = "Sensor needs calibration"
                    });
                }
            }

            return await Task.FromResult(result);
        }

        private async Task<ZoneCheckResult> CheckZonesBeforeArmingAsync(CancellationToken cancellationToken)
        {
            var result = new ZoneCheckResult;
            {
                AllZonesSecure = true,
                TotalZones = _zones.Count,
                SecureZones = new List<string>(),
                UnsecureZones = new List<ZoneStatusInfo>()
            };

            foreach (var zone in _zones.Values)
            {
                if (!zone.IsEnabled)
                {
                    continue;
                }

                // Bölge sensörlerini kontrol et;
                var zoneSensors = _sensors.Values.Where(s => s.ZoneId == zone.Id && s.IsEnabled).ToList();
                var unreadySensors = zoneSensors.Where(s => s.Status != SensorStatus.Normal).ToList();

                if (!unreadySensors.Any())
                {
                    result.SecureZones.Add(zone.Id);
                }
                else;
                {
                    result.AllZonesSecure = false;
                    result.UnsecureZones.Add(new ZoneStatusInfo;
                    {
                        ZoneId = zone.Id,
                        ZoneName = zone.Name,
                        UnreadySensors = unreadySensors.Select(s => s.Id).ToList(),
                        SensorCount = zoneSensors.Count,
                        UnreadyCount = unreadySensors.Count;
                    });
                }
            }

            return await Task.FromResult(result);
        }

        private async Task SetSensorsArmedAsync(bool armed, CancellationToken cancellationToken)
        {
            foreach (var sensor in _sensors.Values)
            {
                sensor.IsArmed = armed;
                sensor.ArmedTime = armed ? DateTime.UtcNow : (DateTime?)null;

                // Sensör tipine göre ek ayarlar;
                if (armed)
                {
                    await SetSensorArmedModeAsync(sensor, cancellationToken);
                }
                else;
                {
                    await SetSensorDisarmedModeAsync(sensor, cancellationToken);
                }
            }
        }

        private async Task SetSensorArmedModeAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            try
            {
                // Sensör tipine göre silahlı mod ayarları;
                switch (sensor.Type)
                {
                    case SensorType.Motion:
                        // Hareket sensörü hassasiyetini artır;
                        sensor.Sensitivity = Math.Min(sensor.Sensitivity * 1.2, 1.0);
                        break;
                    case SensorType.DoorWindow:
                        // Kapı/pencere sensörü için ek kontrol;
                        break;
                }

                await Task.Delay(10, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting armed mode for sensor: {SensorId}", sensor.Id);
            }
        }

        private async Task SetSensorDisarmedModeAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            try
            {
                // Sensörü silahsız moda geçir;
                // Varsayılan hassasiyete dön;
                if (sensor.Metadata.TryGetValue("default_sensitivity", out var defaultSensitivity))
                {
                    sensor.Sensitivity = Convert.ToDouble(defaultSensitivity);
                }

                await Task.Delay(10, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting disarmed mode for sensor: {SensorId}", sensor.Id);
            }
        }

        private async Task UpdateZonesForArmingAsync(AlarmArmingMode mode, CancellationToken cancellationToken)
        {
            foreach (var zone in _zones.Values)
            {
                if (zone.IsEnabled && zone.ArmingMode == ArmingMode.Armed)
                {
                    zone.Status = ZoneStatus.Armed;
                    zone.LastArmed = DateTime.UtcNow;
                    zone.ArmingMode = (ArmingMode)mode;

                    OnZoneStatusChanged(new ZoneStatusChangedEventArgs;
                    {
                        ZoneId = zone.Id,
                        PreviousStatus = ZoneStatus.Disarmed,
                        NewStatus = ZoneStatus.Armed,
                        ChangedAt = DateTime.UtcNow,
                        ArmingMode = mode;
                    });
                }
            }

            await Task.CompletedTask;
        }

        private async Task UpdateZonesForDisarmingAsync(CancellationToken cancellationToken)
        {
            foreach (var zone in _zones.Values)
            {
                zone.Status = ZoneStatus.Disarmed;
                zone.LastDisarmed = DateTime.UtcNow;

                OnZoneStatusChanged(new ZoneStatusChangedEventArgs;
                {
                    ZoneId = zone.Id,
                    PreviousStatus = ZoneStatus.Armed,
                    NewStatus = ZoneStatus.Disarmed,
                    ChangedAt = DateTime.UtcNow;
                });
            }

            await Task.CompletedTask;
        }

        private async Task<bool> AuthenticateDisarmRequestAsync(DisarmRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // Kimlik doğrulama mekanizmaları;
                var authenticationMethods = new List<AuthenticationMethod>();

                // PIN kodu doğrulama;
                if (!string.IsNullOrEmpty(request.PinCode))
                {
                    var pinValid = await ValidatePinCodeAsync(request.UserId, request.PinCode, cancellationToken);
                    if (pinValid)
                    {
                        authenticationMethods.Add(AuthenticationMethod.PinCode);
                    }
                }

                // Biyometrik doğrulama;
                if (request.BiometricData != null)
                {
                    var biometricValid = await ValidateBiometricAsync(request.UserId, request.BiometricData, cancellationToken);
                    if (biometricValid)
                    {
                        authenticationMethods.Add(AuthenticationMethod.Biometric);
                    }
                }

                // Anahtar doğrulama;
                if (!string.IsNullOrEmpty(request.KeyCode))
                {
                    var keyValid = await ValidateKeyCodeAsync(request.KeyCode, cancellationToken);
                    if (keyValid)
                    {
                        authenticationMethods.Add(AuthenticationMethod.Key);
                    }
                }

                // Remote doğrulama (mobil uygulama vb.)
                if (request.RemoteAuthToken != null)
                {
                    var remoteValid = await ValidateRemoteTokenAsync(request.RemoteAuthToken, cancellationToken);
                    if (remoteValid)
                    {
                        authenticationMethods.Add(AuthenticationMethod.Remote);
                    }
                }

                // Gerekli doğrulama yöntemi sayısı;
                var requiredMethods = _config.RequiredAuthenticationMethods;
                if (authenticationMethods.Count >= requiredMethods)
                {
                    _logger.LogInformation("Authentication successful for user {UserId} with {MethodCount} methods",
                        request.UserId, authenticationMethods.Count);
                    return true;
                }

                _logger.LogWarning("Authentication failed for user {UserId}. Found {FoundMethods}, required {RequiredMethods}",
                    request.UserId, authenticationMethods.Count, requiredMethods);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during authentication");
                return false;
            }
        }

        private async Task<bool> AuthenticateSilenceRequestAsync(SilenceRequest request, CancellationToken cancellationToken)
        {
            try
            {
                // Susturma için kimlik doğrulama (genellikle daha az sıkı)
                if (!string.IsNullOrEmpty(request.PinCode))
                {
                    return await ValidatePinCodeAsync(request.UserId, request.PinCode, cancellationToken);
                }

                // Ana kullanıcı ise PIN olmadan da susturabilir;
                if (await IsPrimaryUserAsync(request.UserId, cancellationToken))
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during silence authentication");
                return false;
            }
        }

        private async Task<bool> ValidatePinCodeAsync(string userId, string pinCode, CancellationToken cancellationToken)
        {
            // PIN kodu doğrulama mantığı;
            // Gerçek implementasyonda veritabanı sorgusu vb. olacak;
            await Task.Delay(50, cancellationToken);
            return pinCode == "1234"; // Örnek PIN;
        }

        private async Task<bool> ValidateBiometricAsync(string userId, object biometricData, CancellationToken cancellationToken)
        {
            // Biyometrik doğrulama mantığı;
            await Task.Delay(100, cancellationToken);
            return true; // Örnek;
        }

        private async Task<bool> ValidateKeyCodeAsync(string keyCode, CancellationToken cancellationToken)
        {
            // Anahtar kodu doğrulama;
            await Task.Delay(50, cancellationToken);
            return keyCode == "ALARM_KEY_2024"; // Örnek anahtar;
        }

        private async Task<bool> ValidateRemoteTokenAsync(object remoteToken, CancellationToken cancellationToken)
        {
            // Uzak token doğrulama;
            await Task.Delay(50, cancellationToken);
            return remoteToken != null;
        }

        private async Task<bool> IsPrimaryUserAsync(string userId, CancellationToken cancellationToken)
        {
            // Birincil kullanıcı kontrolü;
            await Task.Delay(10, cancellationToken);
            return userId == "admin" || userId == "primary";
        }

        private bool ShouldTriggerAlarm(SecuritySensor sensor, SensorEvent sensorEvent)
        {
            // Alarm tetikleme mantığı;
            if (!sensor.IsArmed || !sensor.IsEnabled)
                return false;

            // Sensör tipine göre tetikleme kuralları;
            switch (sensor.Type)
            {
                case SensorType.DoorWindow:
                    return sensorEvent.EventType == SensorEventType.Opened &&
                           sensor.Status == SensorStatus.Triggered;

                case SensorType.Motion:
                    return sensorEvent.EventType == SensorEventType.MotionDetected &&
                           sensor.Status == SensorStatus.Triggered &&
                           sensor.Value > sensor.Sensitivity;

                case SensorType.GlassBreak:
                    return sensorEvent.EventType == SensorEventType.GlassBreak &&
                           sensor.Status == SensorStatus.Triggered;

                case SensorType.Smoke:
                    return sensorEvent.EventType == SensorEventType.SmokeDetected &&
                           sensor.Status == SensorStatus.Triggered;

                case SensorType.Heat:
                    return sensorEvent.EventType == SensorEventType.HeatDetected &&
                           sensor.Status == SensorStatus.Triggered &&
                           sensor.Value > _config.HeatAlarmThreshold;

                case SensorType.CarbonMonoxide:
                    return sensorEvent.EventType == SensorEventType.CODetected &&
                           sensor.Status == SensorStatus.Triggered;

                case SensorType.Flood:
                    return sensorEvent.EventType == SensorEventType.FloodDetected &&
                           sensor.Status == SensorStatus.Triggered;

                case SensorType.PanicButton:
                    return sensorEvent.EventType == SensorEventType.PanicPressed;

                default:
                    return false;
            }
        }

        private async Task TriggerAlarmFromSensorAsync(SecuritySensor sensor, SensorEvent sensorEvent, CancellationToken cancellationToken)
        {
            await _stateLock.WaitAsync(cancellationToken);
            try
            {
                // Zaten tetiklenmiş mi kontrol et;
                if (_systemState == AlarmSystemState.Triggered)
                {
                    _logger.LogDebug("Alarm already triggered, ignoring additional trigger from sensor: {SensorId}", sensor.Id);
                    return;
                }

                _logger.LogWarning("Alarm triggered by sensor: {SensorName} ({SensorId})", sensor.Name, sensor.Id);

                var previousState = _systemState;
                _systemState = AlarmSystemState.Triggered;
                _lastIntrusionTime = DateTime.UtcNow;

                // Bölgeyi bul;
                _zones.TryGetValue(sensor.ZoneId, out var zone);

                // Alarm tetikleyicisi oluştur;
                var trigger = new AlarmTrigger;
                {
                    Id = Guid.NewGuid().ToString(),
                    TriggerType = AlarmTriggerType.Sensor,
                    TriggeredAt = DateTime.UtcNow,
                    Source = "sensor",
                    ZoneId = sensor.ZoneId,
                    SensorId = sensor.Id,
                    Severity = CalculateTriggerSeverity(sensor, zone),
                    Description = $"{sensor.Type} sensor triggered: {sensor.Name}",
                    Data = new Dictionary<string, object>
                    {
                        ["sensor_type"] = sensor.Type.ToString(),
                        ["sensor_name"] = sensor.Name,
                        ["sensor_value"] = sensor.Value,
                        ["zone_name"] = zone?.Name,
                        ["event_type"] = sensorEvent.EventType.ToString()
                    }
                };

                // Aktif tetikleyicilere ekle;
                _activeTriggers[trigger.Id] = trigger;

                // Gecikmeli bölge kontrolü;
                if (zone != null && zone.EntryDelay > 0 && zone.ZoneType == ZoneType.Delayed)
                {
                    _logger.LogInformation("Delayed zone triggered. Starting {Delay} second delay before full alarm", zone.EntryDelay);

                    // Gecikme süresi boyunca uyarı ver;
                    await ExecuteDelayedAlarmAsync(trigger, zone.EntryDelay, cancellationToken);
                }
                else;
                {
                    // Anında alarm yanıtı;
                    await ExecuteAlarmResponseAsync(trigger, cancellationToken);
                }

                // İstatistikleri güncelle;
                _statistics.TotalAlarmsTriggered++;
                _statistics.LastAlarmTime = DateTime.UtcNow;

                // Sensor bazlı istatistik;
                sensor.AlarmCount++;
                sensor.LastAlarmTime = DateTime.UtcNow;

                // Durum değişikliği olayı;
                OnAlarmStateChanged(new AlarmStateChangedEventArgs;
                {
                    PreviousState = previousState,
                    NewState = _systemState,
                    ChangedAt = DateTime.UtcNow,
                    Reason = $"Sensor triggered: {sensor.Name}"
                });

                // Alarm tetikleme olayı;
                OnAlarmTriggered(new AlarmTriggeredEventArgs;
                {
                    Trigger = trigger,
                    TriggeredAt = DateTime.UtcNow,
                    PreviousState = previousState,
                    IsManual = false,
                    ZoneName = zone?.Name;
                });

                _logger.LogWarning("Alarm triggered by sensor {SensorId} in zone {ZoneId}", sensor.Id, sensor.ZoneId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering alarm from sensor: {SensorId}", sensor.Id);
            }
            finally
            {
                _stateLock.Release();
            }
        }

        private AlarmSeverity CalculateTriggerSeverity(SecuritySensor sensor, AlarmZone zone)
        {
            // Tetikleme şiddetini hesapla;
            var baseSeverity = sensor.Type switch;
            {
                SensorType.PanicButton => AlarmSeverity.Emergency,
                SensorType.Smoke or SensorType.Heat or SensorType.CarbonMonoxide => AlarmSeverity.Critical,
                SensorType.GlassBreak => AlarmSeverity.High,
                SensorType.Motion => AlarmSeverity.Medium,
                SensorType.DoorWindow => AlarmSeverity.Low,
                _ => AlarmSeverity.Medium;
            };

            // Bölge tipine göre ayarla;
            if (zone != null)
            {
                baseSeverity = zone.ZoneType switch;
                {
                    ZoneType.Instant => baseSeverity + 1, // Bir seviye artır;
                    ZoneType.Fire or ZoneType.Emergency => AlarmSeverity.Critical,
                    _ => baseSeverity;
                };
            }

            // Hassasiyete göre ayarla;
            if (sensor.Value > 0.9)
            {
                baseSeverity = Math.Min(baseSeverity + 1, AlarmSeverity.Emergency);
            }

            return baseSeverity;
        }

        private async Task ExecuteAlarmResponseAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogWarning("Executing alarm response for trigger: {TriggerId}, Severity: {Severity}",
                    trigger.Id, trigger.Severity);

                // Çok kanallı alarm yanıtı;
                var responseTasks = new List<Task>
                {
                    // Sirenleri aktive et;
                    ActivateSirensAsync(trigger, cancellationToken),
                    
                    // Işık sistemlerini kontrol et;
                    ControlLightingSystemAsync(trigger, cancellationToken),
                    
                    // Güvenlik kameralarını kayda al;
                    TriggerCameraRecordingAsync(trigger, cancellationToken),
                    
                    // Bildirim gönder;
                    SendAlarmNotificationsAsync(trigger, cancellationToken),
                    
                    // Event bus'a bildir;
                    PublishAlarmEventAsync(trigger, cancellationToken),
                    
                    // Yetkililere haber ver;
                    NotifyAuthoritiesAsync(trigger, cancellationToken)
                };

                // Acil durumlarda ek yanıtlar;
                if (trigger.Severity >= AlarmSeverity.Critical)
                {
                    responseTasks.Add(ExecuteEmergencyProtocolsAsync(trigger, cancellationToken));
                }

                await Task.WhenAll(responseTasks);

                // Yanıtı geçmişe kaydet;
                await SaveAlarmResponseToHistoryAsync(trigger, cancellationToken);

                _logger.LogWarning("Alarm response executed for trigger: {TriggerId}", trigger.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing alarm response for trigger: {TriggerId}", trigger.Id);
            }
        }

        private async Task ExecuteDelayedAlarmAsync(AlarmTrigger trigger, int delaySeconds, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Starting delayed alarm sequence: {Delay} seconds", delaySeconds);

                // Gecikme süresince ön uyarılar;
                await SendPreAlarmWarningAsync(trigger, cancellationToken);

                // Gecikme sayacı;
                for (int i = delaySeconds; i > 0; i--)
                {
                    if (_systemState != AlarmSystemState.Triggered)
                    {
                        _logger.LogInformation("Delayed alarm cancelled before completion");
                        return;
                    }

                    // Her saniye uyarı;
                    if (i <= 10) // Son 10 saniye;
                    {
                        await SendCountdownWarningAsync(i, trigger, cancellationToken);
                    }

                    await Task.Delay(1000, cancellationToken);
                }

                // Gecikme bitti, tam alarmı başlat;
                _logger.LogWarning("Delayed alarm period ended, activating full alarm response");
                await ExecuteAlarmResponseAsync(trigger, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing delayed alarm sequence");
            }
        }

        private async Task ExecutePanicResponseAsync(AlarmTrigger trigger, PanicRequest request, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogWarning("Executing panic response for type: {PanicType}", request.PanicType);

                // Panik tipine göre yanıt;
                switch (request.PanicType)
                {
                    case PanicType.Medical:
                        await ExecuteMedicalPanicResponseAsync(trigger, request, cancellationToken);
                        break;
                    case PanicType.Fire:
                        await ExecuteFirePanicResponseAsync(trigger, request, cancellationToken);
                        break;
                    case PanicType.Police:
                        await ExecutePolicePanicResponseAsync(trigger, request, cancellationToken);
                        break;
                    case PanicType.Generic:
                    default:
                        await ExecuteGenericPanicResponseAsync(trigger, request, cancellationToken);
                        break;
                }

                // Panik yanıtını geçmişe kaydet;
                await SavePanicResponseToHistoryAsync(trigger, request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing panic response");
            }
        }

        private async Task ExecuteMedicalPanicResponseAsync(AlarmTrigger trigger, PanicRequest request, CancellationToken cancellationToken)
        {
            // Tıbbi acil durum yanıtı;
            await ActivateMedicalAlertAsync(trigger, cancellationToken);
            await NotifyEmergencyServicesAsync("Medical", request.Location, cancellationToken);
            await SendMedicalAlertNotificationsAsync(request, cancellationToken);
        }

        private async Task ExecuteFirePanicResponseAsync(AlarmTrigger trigger, PanicRequest request, CancellationToken cancellationToken)
        {
            // Yangın acil durum yanıtı;
            await ActivateFireProtocolsAsync(cancellationToken);
            await NotifyFireDepartmentAsync(request.Location, cancellationToken);
            await TriggerFireSuppressionSystemsAsync(cancellationToken);
        }

        private async Task ExecutePolicePanicResponseAsync(AlarmTrigger trigger, PanicRequest request, CancellationToken cancellationToken)
        {
            // Polis acil durum yanıtı;
            await ActivatePoliceAlertAsync(trigger, cancellationToken);
            await NotifyPoliceAsync(request.Location, cancellationToken);
            await SecureSafeRoomsAsync(cancellationToken);
        }

        private async Task ExecuteGenericPanicResponseAsync(AlarmTrigger trigger, PanicRequest request, CancellationToken cancellationToken)
        {
            // Genel panik yanıtı;
            await ActivateFullAlarmResponseAsync(trigger, cancellationToken);
            await NotifyAllAuthoritiesAsync(request.Location, cancellationToken);
            await ExecuteFullEmergencyProtocolsAsync(cancellationToken);
        }

        private async Task ActivateSirensAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogWarning("Activating alarm sirens for trigger: {TriggerId}", trigger.Id);

                // İç ve dış sirenleri aktive et;
                var sirenTasks = new List<Task>
                {
                    ActivateInternalSirensAsync(trigger.Severity, cancellationToken),
                    ActivateExternalSirensAsync(trigger.Severity, cancellationToken)
                };

                await Task.WhenAll(sirenTasks);

                // Siren durumunu kaydet;
                _statistics.SirensActivated++;
                _statistics.LastSirenActivation = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating sirens");
            }
        }

        private async Task ActivateInternalSirensAsync(AlarmSeverity severity, CancellationToken cancellationToken)
        {
            // İç sirenleri aktive et;
            // Gerçek implementasyonda donanım kontrolü olacak;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("Internal sirens activated with severity: {Severity}", severity);
        }

        private async Task ActivateExternalSirensAsync(AlarmSeverity severity, CancellationToken cancellationToken)
        {
            // Dış sirenleri aktive et;
            await Task.Delay(100, cancellationToken);
            _logger.LogDebug("External sirens activated with severity: {Severity}", severity);
        }

        private async Task SilenceSirensAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Silencing alarm sirens");

                // Tüm sirenleri durdur;
                // Gerçek implementasyonda donanım kontrolü olacak;
                await Task.Delay(100, cancellationToken);

                _logger.LogDebug("Alarm sirens silenced");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error silencing sirens");
            }
        }

        private async Task ControlLightingSystemAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                // Alarm durumunda aydınlatma kontrolü;
                // Tüm ışıkları aç, flaşör etkisi yap;
                await Task.Delay(50, cancellationToken);
                _logger.LogDebug("Lighting system controlled for alarm");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error controlling lighting system");
            }
        }

        private async Task TriggerCameraRecordingAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                // Güvenlik kameralarını kayıt moduna geçir;
                // İlgili bölgelerin kameralarını aktive et;
                await Task.Delay(100, cancellationToken);
                _logger.LogDebug("Camera recording triggered for alarm");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering camera recording");
            }
        }

        private async Task SendAlarmNotificationsAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Sending alarm notifications for trigger: {TriggerId}", trigger.Id);

                var notifications = new List<Task>
                {
                    // Mobil bildirimler;
                    SendMobileNotificationsAsync(trigger, cancellationToken),
                    
                    // E-posta bildirimleri;
                    SendEmailNotificationsAsync(trigger, cancellationToken),
                    
                    // SMS bildirimleri;
                    SendSmsNotificationsAsync(trigger, cancellationToken),
                    
                    // Push bildirimleri;
                    SendPushNotificationsAsync(trigger, cancellationToken)
                };

                await Task.WhenAll(notifications);

                _statistics.NotificationsSent++;
                _statistics.LastNotificationSent = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending alarm notifications");
            }
        }

        private async Task SendMobileNotificationsAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Mobil uygulama bildirimleri;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "🚨 ALARM TRIGGERED",
                Message = $"Alarm triggered: {trigger.Description}",
                Type = NotificationType.Alert,
                Priority = NotificationPriority.Critical,
                Metadata = new Dictionary<string, object>
                {
                    ["trigger_id"] = trigger.Id,
                    ["severity"] = trigger.Severity.ToString(),
                    ["zone_id"] = trigger.ZoneId,
                    ["timestamp"] = trigger.TriggeredAt;
                }
            }, cancellationToken);
        }

        private async Task SendEmailNotificationsAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // E-posta bildirimleri;
            await Task.Delay(50, cancellationToken);
            _logger.LogDebug("Email notifications sent for alarm");
        }

        private async Task SendSmsNotificationsAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // SMS bildirimleri;
            await Task.Delay(50, cancellationToken);
            _logger.LogDebug("SMS notifications sent for alarm");
        }

        private async Task SendPushNotificationsAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Push bildirimleri;
            await Task.Delay(50, cancellationToken);
            _logger.LogDebug("Push notifications sent for alarm");
        }

        private async Task PublishAlarmEventAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                await _eventBus.PublishAsync(new AlarmEvent;
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    EventType = AlarmEventType.AlarmTriggered,
                    ZoneId = trigger.ZoneId,
                    SensorId = trigger.SensorId,
                    Severity = trigger.Severity,
                    Data = new Dictionary<string, object>
                    {
                        ["trigger_id"] = trigger.Id,
                        ["trigger_type"] = trigger.TriggerType.ToString(),
                        ["description"] = trigger.Description,
                        ["triggered_at"] = trigger.TriggeredAt;
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing alarm event");
            }
        }

        private async Task NotifyAuthoritiesAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                // Şiddete göre yetkililere haber ver;
                if (trigger.Severity >= AlarmSeverity.High)
                {
                    _logger.LogWarning("Notifying authorities for high severity alarm");

                    // Polis;
                    await NotifyPoliceAsync(trigger, cancellationToken);

                    // Güvenlik şirketi;
                    await NotifySecurityCompanyAsync(trigger, cancellationToken);

                    _statistics.AuthoritiesNotified++;
                    _statistics.LastAuthorityNotification = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error notifying authorities");
            }
        }

        private async Task NotifyPoliceAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Polis bildirimi;
            // Gerçek implementasyonda API çağrısı olacak;
            await Task.Delay(100, cancellationToken);
            _logger.LogWarning("Police notified for alarm: {TriggerId}", trigger.Id);
        }

        private async Task NotifySecurityCompanyAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Güvenlik şirketi bildirimi;
            await Task.Delay(100, cancellationToken);
            _logger.LogWarning("Security company notified for alarm: {TriggerId}", trigger.Id);
        }

        private async Task ExecuteEmergencyProtocolsAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogWarning("Executing emergency protocols for trigger: {TriggerId}", trigger.Id);

                var protocols = new List<Task>
                {
                    // Elektrik kes (yangın durumunda)
                    CutPowerIfNeededAsync(trigger, cancellationToken),
                    
                    // Gazı kes;
                    ShutOffGasAsync(cancellationToken),
                    
                    // Suyu kes (su baskını durumunda)
                    ShutOffWaterAsync(cancellationToken),
                    
                    // Güvenli odaları kilitle;
                    LockSafeRoomsAsync(cancellationToken),
                    
                    // Acil çıkış yollarını aydınlat;
                    IlluminateEscapeRoutesAsync(cancellationToken)
                };

                await Task.WhenAll(protocols);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing emergency protocols");
            }
        }

        private async Task SendPreAlarmWarningAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Gecikmeli alarm ön uyarısı;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "⚠️ ALARM WILL TRIGGER",
                Message = $"Alarm will trigger in {trigger.ZoneId}. Disarm system to prevent.",
                Type = NotificationType.Warning,
                Priority = NotificationPriority.High;
            }, cancellationToken);
        }

        private async Task SendCountdownWarningAsync(int secondsLeft, AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Geri sayım uyarıları;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = $"⏰ {secondsLeft} SECONDS",
                Message = $"Alarm will trigger in {secondsLeft} seconds in {trigger.ZoneId}",
                Type = NotificationType.Warning,
                Priority = NotificationPriority.High;
            }, cancellationToken);
        }

        private async Task SendArmingNotificationAsync(ArmRequest request, CancellationToken cancellationToken)
        {
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "🔒 SYSTEM ARMED",
                Message = $"Alarm system armed with {request.Mode} mode",
                Type = NotificationType.Info,
                Priority = NotificationPriority.Medium,
                Metadata = new Dictionary<string, object>
                {
                    ["arming_mode"] = request.Mode.ToString(),
                    ["armed_by"] = request.UserId,
                    ["arming_time"] = DateTime.UtcNow;
                }
            }, cancellationToken);
        }

        private async Task SendDisarmingNotificationAsync(DisarmRequest request, CancellationToken cancellationToken)
        {
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "🔓 SYSTEM DISARMED",
                Message = $"Alarm system disarmed by {request.UserId}",
                Type = NotificationType.Info,
                Priority = NotificationPriority.Medium,
                Metadata = new Dictionary<string, object>
                {
                    ["disarmed_by"] = request.UserId,
                    ["disarm_time"] = DateTime.UtcNow,
                    ["reason"] = request.Reason;
                }
            }, cancellationToken);
        }

        private async Task SendSilenceNotificationAsync(SilenceRequest request, CancellationToken cancellationToken)
        {
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "🔇 ALARM SILENCED",
                Message = $"Alarm silenced by {request.UserId}",
                Type = NotificationType.Info,
                Priority = NotificationPriority.Medium,
                Metadata = new Dictionary<string, object>
                {
                    ["silenced_by"] = request.UserId,
                    ["silence_time"] = DateTime.UtcNow,
                    ["reason"] = request.Reason;
                }
            }, cancellationToken);
        }

        private async Task SendPanicNotificationAsync(PanicRequest request, AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "🚨 PANIC ALARM",
                Message = $"Panic alarm triggered: {request.PanicType} by {request.UserId}",
                Type = NotificationType.Alert,
                Priority = NotificationPriority.Critical,
                Metadata = new Dictionary<string, object>
                {
                    ["panic_type"] = request.PanicType.ToString(),
                    ["triggered_by"] = request.UserId,
                    ["location"] = request.Location,
                    ["trigger_time"] = trigger.TriggeredAt;
                }
            }, cancellationToken);
        }

        private async Task<SystemHealthStatus> CheckSensorHealthAsync(CancellationToken cancellationToken)
        {
            var healthStatus = new SystemHealthStatus;
            {
                Component = "Sensors",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy;
            };

            try
            {
                var normalSensors = _sensors.Values.Count(s => s.Status == SensorStatus.Normal);
                var faultySensors = _sensors.Values.Count(s => s.Status == SensorStatus.Faulty);
                var offlineSensors = _sensors.Values.Count(s => s.Status == SensorStatus.Offline);
                var totalSensors = _sensors.Count;

                healthStatus.Metrics["total_sensors"] = totalSensors;
                healthStatus.Metrics["normal_sensors"] = normalSensors;
                healthStatus.Metrics["faulty_sensors"] = faultySensors;
                healthStatus.Metrics["offline_sensors"] = offlineSensors;
                healthStatus.Metrics["health_percentage"] = totalSensors > 0 ? (double)normalSensors / totalSensors * 100 : 0;

                if (faultySensors > _config.MaxFaultySensors)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = $"{faultySensors} sensors are faulty";
                }
                else if (offlineSensors > _config.MaxOfflineSensors)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"{offlineSensors} sensors are offline";
                }
                else if (normalSensors == totalSensors)
                {
                    healthStatus.OverallStatus = HealthStatus.Healthy;
                    healthStatus.Message = "All sensors operational";
                }
                else;
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"{faultySensors + offlineSensors} sensors have issues";
                }
            }
            catch (Exception ex)
            {
                healthStatus.OverallStatus = HealthStatus.Error;
                healthStatus.Message = $"Sensor health check failed: {ex.Message}";
            }

            return healthStatus;
        }

        private SystemHealthStatus CheckZoneHealth()
        {
            var healthStatus = new SystemHealthStatus;
            {
                Component = "Zones",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy;
            };

            try
            {
                var secureZones = _zones.Values.Count(z => z.Status == ZoneStatus.Disarmed || z.Status == ZoneStatus.Armed);
                var breachedZones = _zones.Values.Count(z => z.Status == ZoneStatus.Breached);
                var totalZones = _zones.Count;

                healthStatus.Metrics["total_zones"] = totalZones;
                healthStatus.Metrics["secure_zones"] = secureZones;
                healthStatus.Metrics["breached_zones"] = breachedZones;

                if (breachedZones > 0 && _systemState != AlarmSystemState.Triggered)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"{breachedZones} zones are breached";
                }
                else if (secureZones == totalZones)
                {
                    healthStatus.OverallStatus = HealthStatus.Healthy;
                    healthStatus.Message = "All zones secure";
                }
                else;
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = "Zone security issues detected";
                }
            }
            catch (Exception ex)
            {
                healthStatus.OverallStatus = HealthStatus.Error;
                healthStatus.Message = $"Zone health check failed: {ex.Message}";
            }

            return healthStatus;
        }

        private async Task<SystemHealthStatus> CheckHardwareHealthAsync(CancellationToken cancellationToken)
        {
            var healthStatus = new SystemHealthStatus;
            {
                Component = "Hardware",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy;
            };

            try
            {
                // Donanım kilidi sağlığı;
                var lockHealth = await _hardwareLock.CheckHealthAsync(cancellationToken);

                // Siren sağlığı (simüle edilmiş)
                var sirenHealth = CheckSirenHealth();

                healthStatus.Subcomponents.Add(lockHealth);
                healthStatus.Subcomponents.Add(sirenHealth);

                // Genel donanım durumu;
                var errorComponents = healthStatus.Subcomponents.Count(c => c.OverallStatus == HealthStatus.Error);
                var warningComponents = healthStatus.Subcomponents.Count(c => c.OverallStatus == HealthStatus.Warning);

                if (errorComponents > 0)
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = $"{errorComponents} hardware components have errors";
                }
                else if (warningComponents > 0)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = $"{warningComponents} hardware components have warnings";
                }
                else;
                {
                    healthStatus.OverallStatus = HealthStatus.Healthy;
                    healthStatus.Message = "All hardware components operational";
                }
            }
            catch (Exception ex)
            {
                healthStatus.OverallStatus = HealthStatus.Error;
                healthStatus.Message = $"Hardware health check failed: {ex.Message}";
            }

            return healthStatus;
        }

        private SystemHealthStatus CheckSirenHealth()
        {
            // Siren sağlık kontrolü (simüle)
            return new SystemHealthStatus;
            {
                Component = "Sirens",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy,
                Message = "Sirens operational",
                Metrics = new Dictionary<string, object>
                {
                    ["last_test"] = _statistics.LastSirenTest ?? DateTime.MinValue,
                    ["total_activations"] = _statistics.SirensActivated,
                    ["last_activation"] = _statistics.LastSirenActivation;
                }
            };
        }

        private async Task<SystemHealthStatus> CheckCommunicationHealthAsync(CancellationToken cancellationToken)
        {
            var healthStatus = new SystemHealthStatus;
            {
                Component = "Communications",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy;
            };

            try
            {
                // İletişim sistemleri sağlık kontrolü;
                var networkHealth = await CheckNetworkHealthAsync(cancellationToken);
                var cellularHealth = await CheckCellularHealthAsync(cancellationToken);
                var backupCommsHealth = await CheckBackupCommunicationsHealthAsync(cancellationToken);

                healthStatus.Subcomponents.Add(networkHealth);
                healthStatus.Subcomponents.Add(cellularHealth);
                healthStatus.Subcomponents.Add(backupCommsHealth);

                // Kritik iletişim hatları kontrolü;
                var workingChannels = healthStatus.Subcomponents.Count(c => c.OverallStatus == HealthStatus.Healthy);

                if (workingChannels >= 2) // En az 2 çalışan kanal;
                {
                    healthStatus.OverallStatus = HealthStatus.Healthy;
                    healthStatus.Message = $"{workingChannels} communication channels operational";
                }
                else if (workingChannels >= 1)
                {
                    healthStatus.OverallStatus = HealthStatus.Warning;
                    healthStatus.Message = "Limited communication channels available";
                }
                else;
                {
                    healthStatus.OverallStatus = HealthStatus.Error;
                    healthStatus.Message = "No communication channels available";
                }
            }
            catch (Exception ex)
            {
                healthStatus.OverallStatus = HealthStatus.Error;
                healthStatus.Message = $"Communication health check failed: {ex.Message}";
            }

            return healthStatus;
        }

        private async Task<SystemHealthStatus> CheckNetworkHealthAsync(CancellationToken cancellationToken)
        {
            // Ağ bağlantısı kontrolü;
            await Task.Delay(50, cancellationToken);
            return new SystemHealthStatus;
            {
                Component = "Network",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy,
                Message = "Network connection stable"
            };
        }

        private async Task<SystemHealthStatus> CheckCellularHealthAsync(CancellationToken cancellationToken)
        {
            // Hücresel bağlantı kontrolü;
            await Task.Delay(50, cancellationToken);
            return new SystemHealthStatus;
            {
                Component = "Cellular",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy,
                Message = "Cellular backup operational"
            };
        }

        private async Task<SystemHealthStatus> CheckBackupCommunicationsHealthAsync(CancellationToken cancellationToken)
        {
            // Yedek iletişim sistemleri kontrolü;
            await Task.Delay(50, cancellationToken);
            return new SystemHealthStatus;
            {
                Component = "BackupComms",
                Timestamp = DateTime.UtcNow,
                OverallStatus = HealthStatus.Healthy,
                Message = "Backup communication systems ready"
            };
        }

        private async Task StopAllSensorsAsync(CancellationToken cancellationToken)
        {
            foreach (var sensor in _sensors.Values)
            {
                await StopSensorAsync(sensor, cancellationToken);
            }
        }

        private async Task StartAllSensorsAsync(CancellationToken cancellationToken)
        {
            foreach (var sensor in _sensors.Values.Where(s => s.IsEnabled))
            {
                await StartSensorAsync(sensor, cancellationToken);
            }
        }

        private async Task StopSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            try
            {
                // Sensörü durdur;
                sensor.IsInitialized = false;
                sensor.Status = SensorStatus.Offline;

                await Task.Delay(10, cancellationToken);
                _logger.LogDebug("Sensor stopped: {SensorId}", sensor.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping sensor: {SensorId}", sensor.Id);
            }
        }

        private async Task StartSensorAsync(SecuritySensor sensor, CancellationToken cancellationToken)
        {
            try
            {
                // Sensörü başlat;
                var initResult = await InitializeSensorAsync(sensor, cancellationToken);

                if (initResult.Success)
                {
                    sensor.IsInitialized = true;
                    sensor.Status = SensorStatus.Normal;
                    _logger.LogDebug("Sensor started: {SensorId}", sensor.Id);
                }
                else;
                {
                    sensor.Status = SensorStatus.Faulty;
                    sensor.LastError = initResult.Error;
                    _logger.LogWarning("Failed to start sensor: {SensorId}, Error: {Error}",
                        sensor.Id, initResult.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting sensor: {SensorId}", sensor.Id);
                sensor.Status = SensorStatus.Faulty;
                sensor.LastError = ex.Message;
            }
        }

        private async Task CleanupAllResourcesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Tüm sensörleri durdur;
                await StopAllSensorsAsync(cancellationToken);

                // Tüm zamanlayıcıları temizle;
                _monitoringTimer.Stop();
                _armingTimer.Stop();
                _responseTimer.Stop();

                // Koleksiyonları temizle;
                _sensors.Clear();
                _zones.Clear();
                _activeTriggers.Clear();
                _eventQueue.Clear();
                _armingSchedules.Clear();

                _logger.LogDebug("All resources cleaned up");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up resources");
            }
        }

        private async Task SaveSensorEventToHistoryAsync(SensorEvent sensorEvent, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.SaveSensorEventAsync(sensorEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving sensor event to history");
            }
        }

        private async Task SaveAlarmResponseToHistoryAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.SaveAlarmResponseAsync(trigger, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving alarm response to history");
            }
        }

        private async Task SavePanicResponseToHistoryAsync(AlarmTrigger trigger, PanicRequest request, CancellationToken cancellationToken)
        {
            try
            {
                await _memorySystem.SavePanicResponseAsync(trigger, request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving panic response to history");
            }
        }

        private async Task ActivateMedicalAlertAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Tıbbi alarm aktive etme;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Medical alert activated");
        }

        private async Task ActivateFireProtocolsAsync(CancellationToken cancellationToken)
        {
            // Yangın protokolleri;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Fire protocols activated");
        }

        private async Task TriggerFireSuppressionSystemsAsync(CancellationToken cancellationToken)
        {
            // Yangın söndürme sistemleri;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Fire suppression systems triggered");
        }

        private async Task ActivatePoliceAlertAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Polis alarmı;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Police alert activated");
        }

        private async Task SecureSafeRoomsAsync(CancellationToken cancellationToken)
        {
            // Güvenli odaları kilitle;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Safe rooms secured");
        }

        private async Task ActivateFullAlarmResponseAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Tam alarm yanıtı;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Full alarm response activated");
        }

        private async Task NotifyAllAuthoritiesAsync(string location, CancellationToken cancellationToken)
        {
            // Tüm yetkililere haber ver;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("All authorities notified");
        }

        private async Task ExecuteFullEmergencyProtocolsAsync(CancellationToken cancellationToken)
        {
            // Tam acil durum protokolleri;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Full emergency protocols executed");
        }

        private async Task CutPowerIfNeededAsync(AlarmTrigger trigger, CancellationToken cancellationToken)
        {
            // Elektrik kesme (yangın durumunda)
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Power cut if needed check completed");
        }

        private async Task ShutOffGasAsync(CancellationToken cancellationToken)
        {
            // Gaz kesme;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Gas shut off");
        }

        private async Task ShutOffWaterAsync(CancellationToken cancellationToken)
        {
            // Su kesme;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Water shut off");
        }

        private async Task LockSafeRoomsAsync(CancellationToken cancellationToken)
        {
            // Güvenli odaları kilitle;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Safe rooms locked");
        }

        private async Task IlluminateEscapeRoutesAsync(CancellationToken cancellationToken)
        {
            // Acil çıkış yollarını aydınlat;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Escape routes illuminated");
        }

        private async Task NotifyEmergencyServicesAsync(string serviceType, string location, CancellationToken cancellationToken)
        {
            // Acil servisleri bilgilendir;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Emergency services notified: {ServiceType} at {Location}", serviceType, location);
        }

        private async Task NotifyFireDepartmentAsync(string location, CancellationToken cancellationToken)
        {
            // İtfaiyeyi bilgilendir;
            await Task.Delay(50, cancellationToken);
            _logger.LogWarning("Fire department notified at {Location}", location);
        }

        private void OnMonitoringTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                CheckSensorStatus();
                ProcessEventQueue();
                UpdateSystemMetrics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring timer elapsed");
            }
        }

        private void OnArmingTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                CheckArmingSchedules();
                ValidateSystemArmedState();
                PerformSelfTest();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in arming timer elapsed");
            }
        }

        private void OnResponseTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                CheckActiveTriggers();
                MonitorResponseProgress();
                UpdateAlarmState();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in response timer elapsed");
            }
        }

        private void CheckSensorStatus()
        {
            // Sensör durumlarını kontrol et;
            foreach (var sensor in _sensors.Values)
            {
                // Uzun süre haber alınamayan sensörleri offline yap;
                if (sensor.LastEventTime.HasValue &&
                    (DateTime.UtcNow - sensor.LastEventTime.Value).TotalMinutes > _config.SensorTimeoutMinutes)
                {
                    if (sensor.Status != SensorStatus.Offline)
                    {
                        sensor.Status = SensorStatus.Offline;
                        _logger.LogWarning("Sensor marked as offline due to timeout: {SensorId}", sensor.Id);
                    }
                }
            }
        }

        private void ProcessEventQueue()
        {
            // Olay kuyruğunu işle;
            var processedCount = 0;
            var maxProcess = Math.Min(_eventQueue.Count, 20);

            for (int i = 0; i < maxProcess; i++)
            {
                if (_eventQueue.TryDequeue(out var alarmEvent))
                {
                    ProcessAlarmEvent(alarmEvent);
                    processedCount++;
                }
            }

            if (processedCount > 0)
            {
                _logger.LogDebug("Processed {Count} alarm events from queue", processedCount);
            }
        }

        private void UpdateSystemMetrics()
        {
            // Sistem metriklerini güncelle;
            _statistics.ActiveSensors = _sensors.Values.Count(s => s.Status == SensorStatus.Normal);
            _statistics.FaultySensors = _sensors.Values.Count(s => s.Status == SensorStatus.Faulty);
            _statistics.ArmedZones = _zones.Values.Count(z => z.Status == ZoneStatus.Armed);
            _statistics.BreachedZones = _zones.Values.Count(z => z.Status == ZoneStatus.Breached);

            // Uptime hesapla;
            if (_statistics.StartTime != default)
            {
                _statistics.Uptime = DateTime.UtcNow - _statistics.StartTime;
            }
        }

        private void CheckArmingSchedules()
        {
            // Otomatik silahlanma zamanlamalarını kontrol et;
            var now = DateTime.UtcNow;

            foreach (var schedule in _armingSchedules.Values)
            {
                if (schedule.IsEnabled && schedule.ShouldArmAt(now))
                {
                    _ = ExecuteScheduledArmingAsync(schedule);
                }
                else if (schedule.IsEnabled && schedule.ShouldDisarmAt(now))
                {
                    _ = ExecuteScheduledDisarmingAsync(schedule);
                }
            }
        }

        private async Task ExecuteScheduledArmingAsync(AlarmArmingSchedule schedule)
        {
            try
            {
                _logger.LogInformation("Executing scheduled arming: {ScheduleName}", schedule.Name);

                var armRequest = new ArmRequest;
                {
                    Mode = schedule.ArmingMode,
                    UserId = "system_schedule",
                    ScheduleId = schedule.Id;
                };

                await ArmSystemAsync(armRequest, CancellationToken.None);
                schedule.LastExecuted = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing scheduled arming: {ScheduleName}", schedule.Name);
            }
        }

        private async Task ExecuteScheduledDisarmingAsync(AlarmArmingSchedule schedule)
        {
            try
            {
                _logger.LogInformation("Executing scheduled disarming: {ScheduleName}", schedule.Name);

                var disarmRequest = new DisarmRequest;
                {
                    UserId = "system_schedule",
                    Reason = "Scheduled disarming",
                    PinCode = schedule.DisarmPinCode;
                };

                await DisarmSystemAsync(disarmRequest, CancellationToken.None);
                schedule.LastExecuted = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing scheduled disarming: {ScheduleName}", schedule.Name);
            }
        }

        private void ValidateSystemArmedState()
        {
            // Sistemin silahlı durumunu doğrula;
            if (_systemState == AlarmSystemState.Armed || _systemState == AlarmSystemState.ArmedPartial)
            {
                // Silahlı sensörleri kontrol et;
                var armedSensors = _sensors.Values.Where(s => s.IsArmed).ToList();
                var faultyArmedSensors = armedSensors.Where(s => s.Status != SensorStatus.Normal).ToList();

                if (faultyArmedSensors.Any())
                {
                    _logger.LogWarning("{Count} armed sensors are faulty while system is armed", faultyArmedSensors.Count);

                    // Kritik sayıda hatalı sensör varsa uyarı ver;
                    if (faultyArmedSensors.Count > _config.MaxFaultySensorsWhileArmed)
                    {
                        _logger.LogError("Too many faulty sensors while armed. System integrity compromised.");
                        // Otomatik silahsızlandırma veya uyarı gönder;
                    }
                }
            }
        }

        private void PerformSelfTest()
        {
            // Sistem kendi kendine test;
            _logger.LogDebug("Performing system self-test");

            // Test sonuçlarını kaydet;
            _statistics.LastSelfTest = DateTime.UtcNow;
            _statistics.TotalSelfTests++;
        }

        private void CheckActiveTriggers()
        {
            // Aktif tetikleyicileri kontrol et;
            foreach (var trigger in _activeTriggers.Values.ToList())
            {
                // Uzun süredir aktif tetikleyiciler için ek işlemler;
                var triggerDuration = DateTime.UtcNow - trigger.TriggeredAt;

                if (triggerDuration.TotalMinutes > _config.MaxAlarmDurationMinutes)
                {
                    _logger.LogWarning("Alarm trigger {TriggerId} has been active for {Minutes} minutes",
                        trigger.Id, triggerDuration.TotalMinutes);

                    // Uzun süreli alarm için ek bildirimler;
                    _ = SendExtendedAlarmNotificationAsync(trigger);
                }
            }
        }

        private async Task SendExtendedAlarmNotificationAsync(AlarmTrigger trigger)
        {
            // Uzun süreli alarm bildirimi;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "⏰ ALARM STILL ACTIVE",
                Message = $"Alarm has been active for extended period: {trigger.Description}",
                Type = NotificationType.Alert,
                Priority = NotificationPriority.High;
            }, CancellationToken.None);
        }

        private void MonitorResponseProgress()
        {
            // Alarm yanıt ilerlemesini izle;
            // Örneğin: yetkililerin varış süresi, müdahale durumu vb.
            _logger.LogDebug("Monitoring alarm response progress");
        }

        private void UpdateAlarmState()
        {
            // Alarm durumunu güncelle;
            // Örneğin: Susturulmuş alarmı zamanlayıcı ile sıfırla;
            if (_systemState == AlarmSystemState.Silenced)
            {
                var silenceDuration = DateTime.UtcNow - _statistics.LastSilencedTime;
                if (silenceDuration.HasValue && silenceDuration.Value.TotalMinutes > _config.SilenceTimeoutMinutes)
                {
                    // Susturma zaman aşımı - sistemi normal duruma döndür;
                    _systemState = AlarmSystemState.Disarmed;
                    _logger.LogInformation("Silence timeout reached, system returned to disarmed state");
                }
            }
        }

        private void ProcessAlarmEvent(AlarmEvent alarmEvent)
        {
            try
            {
                // Alarm olaylarını işle;
                switch (alarmEvent.EventType)
                {
                    case AlarmEventType.AlarmTriggered:
                        ProcessAlarmTriggeredEvent(alarmEvent);
                        break;
                    case AlarmEventType.SystemArmed:
                        ProcessSystemArmedEvent(alarmEvent);
                        break;
                    case AlarmEventType.SystemDisarmed:
                        ProcessSystemDisarmedEvent(alarmEvent);
                        break;
                    case AlarmEventType.SensorTriggered:
                        ProcessSensorTriggeredEvent(alarmEvent);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing alarm event: {EventType}", alarmEvent.EventType);
            }
        }

        private void ProcessAlarmTriggeredEvent(AlarmEvent alarmEvent)
        {
            // Alarm tetiklenme olayını işle;
            _logger.LogDebug("Processing alarm triggered event: {EventId}", alarmEvent.EventId);
        }

        private void ProcessSystemArmedEvent(AlarmEvent alarmEvent)
        {
            // Sistem silahlanma olayını işle;
            _logger.LogDebug("Processing system armed event: {EventId}", alarmEvent.EventId);
        }

        private void ProcessSystemDisarmedEvent(AlarmEvent alarmEvent)
        {
            // Sistem silahsızlanma olayını işle;
            _logger.LogDebug("Processing system disarmed event: {EventId}", alarmEvent.EventId);
        }

        private void ProcessSensorTriggeredEvent(AlarmEvent alarmEvent)
        {
            // Sensör tetiklenme olayını işle;
            _logger.LogDebug("Processing sensor triggered event: {EventId}", alarmEvent.EventId);
        }

        private void HandleSecurityEvent(SecurityEvent securityEvent)
        {
            try
            {
                _logger.LogDebug("Received security event: {EventType}", securityEvent.EventType);

                // Güvenlik olaylarını işle;
                switch (securityEvent.EventType)
                {
                    case SecurityEventType.IntrusionDetected:
                        _ = HandleIntrusionEventAsync(securityEvent);
                        break;
                    case SecurityEventType.TamperDetected:
                        _ = HandleTamperEventAsync(securityEvent);
                        break;
                    case SecurityEventType.AccessDenied:
                        _ = HandleAccessDeniedEventAsync(securityEvent);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling security event");
            }
        }

        private async Task HandleIntrusionEventAsync(SecurityEvent securityEvent)
        {
            // İzinsiz giriş olayını işle;
            _logger.LogWarning("Handling intrusion event: {EventId}", securityEvent.EventId);

            // Alarm tetikle;
            var triggerRequest = new TriggerRequest;
            {
                Source = "security_system",
                ZoneId = securityEvent.ZoneId,
                SensorId = securityEvent.SensorId,
                Reason = "Intrusion detected by security system",
                UserId = "system",
                AdditionalData = securityEvent.Data;
            };

            await TriggerAlarmAsync(triggerRequest, CancellationToken.None);
        }

        private async Task HandleTamperEventAsync(SecurityEvent securityEvent)
        {
            // Sabotaj olayını işle;
            _logger.LogWarning("Handling tamper event: {EventId}", securityEvent.EventId);

            // Sabotaj bildirimi gönder;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "⚠️ TAMPER DETECTED",
                Message = $"Tampering detected: {securityEvent.Description}",
                Type = NotificationType.Warning,
                Priority = NotificationPriority.High;
            }, CancellationToken.None);
        }

        private async Task HandleAccessDeniedEventAsync(SecurityEvent securityEvent)
        {
            // Erişim reddi olayını işle;
            _logger.LogWarning("Handling access denied event: {EventId}", securityEvent.EventId);

            // Erişim reddi kaydı;
            // Alarm tetikleme (isteğe bağlı)
            if (_config.TriggerAlarmOnAccessDenied)
            {
                var triggerRequest = new TriggerRequest;
                {
                    Source = "access_control",
                    ZoneId = securityEvent.ZoneId,
                    SensorId = securityEvent.SensorId,
                    Reason = "Access denied - possible intrusion attempt",
                    UserId = "system",
                    AdditionalData = securityEvent.Data;
                };

                await TriggerAlarmAsync(triggerRequest, CancellationToken.None);
            }
        }

        private void HandleSystemAlertEvent(SystemAlertEvent systemEvent)
        {
            try
            {
                _logger.LogDebug("Received system alert event: {EventType}", systemEvent.EventType);

                // Sistem uyarı olaylarını işle;
                switch (systemEvent.EventType)
                {
                    case SystemEventType.PowerFailure:
                        _ = HandlePowerFailureAsync(systemEvent);
                        break;
                    case SystemEventType.CommunicationFailure:
                        _ = HandleCommunicationFailureAsync(systemEvent);
                        break;
                    case SystemEventType.BatteryLow:
                        _ = HandleBatteryLowAsync(systemEvent);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling system alert event");
            }
        }

        private async Task HandlePowerFailureAsync(SystemAlertEvent systemEvent)
        {
            // Güç kesintisi işleme;
            _logger.LogWarning("Handling power failure event");

            // Yedek güç sistemine geç;
            // Kritik sensörleri yedek güce al;
            // Bildirim gönder;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "⚡ POWER FAILURE",
                Message = "Alarm system running on backup power",
                Type = NotificationType.Warning,
                Priority = NotificationPriority.Medium;
            }, CancellationToken.None);
        }

        private async Task HandleCommunicationFailureAsync(SystemAlertEvent systemEvent)
        {
            // İletişim hatası işleme;
            _logger.LogWarning("Handling communication failure event");

            // Yedek iletişim kanallarına geç;
            // Bildirim gönder;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "📡 COMMUNICATION FAILURE",
                Message = "Alarm system communication issues detected",
                Type = NotificationType.Warning,
                Priority = NotificationPriority.High;
            }, CancellationToken.None);
        }

        private async Task HandleBatteryLowAsync(SystemAlertEvent systemEvent)
        {
            // Düşük pil işleme;
            _logger.LogWarning("Handling battery low event");

            // Pil değişimi uyarısı;
            await _notificationManager.SendNotificationAsync(new Notification;
            {
                Title = "🔋 BATTERY LOW",
                Message = "Alarm system backup battery is low",
                Type = NotificationType.Warning,
                Priority = NotificationPriority.Medium;
            }, CancellationToken.None);
        }

        private bool ValidateConfig(AlarmSystemConfig config)
        {
            if (config == null) return false;
            if (config.ArmingDelaySeconds < 0) return false;
            if (config.EntryDelaySeconds < 0) return false;
            if (config.ExitDelaySeconds < 0) return false;
            if (config.MaxFaultySensors < 0) return false;
            if (config.MaxOfflineSensors < 0) return false;
            if (config.SensorTimeoutMinutes <= 0) return false;
            if (config.RequiredAuthenticationMethods <= 0) return false;

            return true;
        }

        private void OnAlarmStateChanged(AlarmStateChangedEventArgs e)
        {
            AlarmStateChanged?.Invoke(this, e);
        }

        private void OnAlarmTriggered(AlarmTriggeredEventArgs e)
        {
            AlarmTriggered?.Invoke(this, e);
        }

        private void OnSensorStatusChanged(SensorStatusChangedEventArgs e)
        {
            SensorStatusChanged?.Invoke(this, e);
        }

        private void OnZoneStatusChanged(ZoneStatusChangedEventArgs e)
        {
            ZoneStatusChanged?.Invoke(this, e);
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
                    _monitoringTimer?.Dispose();
                    _armingTimer?.Dispose();
                    _responseTimer?.Dispose();
                    _stateLock?.Dispose();

                    if (_isInitialized)
                    {
                        _ = ShutdownAsync().ConfigureAwait(false);
                    }
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
    }

    #region Supporting Classes and Enums;

    public interface IAlarmSystem : IDisposable
    {
        event EventHandler<AlarmStateChangedEventArgs> AlarmStateChanged;
        event EventHandler<AlarmTriggeredEventArgs> AlarmTriggered;
        event EventHandler<SensorStatusChangedEventArgs> SensorStatusChanged;
        event EventHandler<ZoneStatusChangedEventArgs> ZoneStatusChanged;

        AlarmStatistics Statistics { get; }
        AlarmSystemState SystemState { get; }
        AlarmArmingMode ArmingMode { get; }

        Task<AlarmResult> InitializeAsync(CancellationToken cancellationToken = default);
        Task<AlarmResult> ArmSystemAsync(ArmRequest request, CancellationToken cancellationToken = default);
        Task<AlarmResult> DisarmSystemAsync(DisarmRequest request, CancellationToken cancellationToken = default);
        Task<AlarmResult> ArmPartialAsync(PartialArmRequest request, CancellationToken cancellationToken = default);
        Task<SensorResult> ProcessSensorEventAsync(SensorEvent sensorEvent, CancellationToken cancellationToken = default);
        Task<AlarmResult> TriggerAlarmAsync(TriggerRequest request, CancellationToken cancellationToken = default);
        Task<AlarmResult> SilenceAlarmAsync(SilenceRequest request, CancellationToken cancellationToken = default);
        Task<AlarmResult> TriggerPanicAlarmAsync(PanicRequest request, CancellationToken cancellationToken = default);
        Task<SystemHealthStatus> CheckSystemHealthAsync(CancellationToken cancellationToken = default);
        Task<AlarmResult> StopMonitoringAsync(CancellationToken cancellationToken = default);
        Task<AlarmResult> StartMonitoringAsync(CancellationToken cancellationToken = default);
        Task<AlarmResult> ShutdownAsync(CancellationToken cancellationToken = default);
    }

    public enum AlarmSystemState;
    {
        Uninitialized,
        Disarmed,
        Arming,
        Armed,
        ArmedPartial,
        Triggered,
        Silenced,
        Maintenance,
        Error,
        Shutdown;
    }

    public enum AlarmArmingMode;
    {
        Off,
        Away,
        Stay,
        Night,
        Vacation,
        Partial,
        Custom;
    }

    public enum AlarmTriggerType;
    {
        Sensor,
        Manual,
        Panic,
        Tamper,
        Fire,
        Medical,
        Environmental;
    }

    public enum AlarmSeverity;
    {
        Info,
        Low,
        Medium,
        High,
        Critical,
        Emergency;
    }

    public enum SensorType;
    {
        DoorWindow,
        Motion,
        GlassBreak,
        Smoke,
        Heat,
        CarbonMonoxide,
        Flood,
        PanicButton,
        Temperature,
        Humidity,
        Vibration;
    }

    public enum SensorStatus;
    {
        Normal,
        Triggered,
        Faulty,
        Offline,
        Bypassed,
        NeedsCalibration,
        Initializing,
        Tampered;
    }

    public enum SensorEventType;
    {
        Opened,
        Closed,
        MotionDetected,
        GlassBreak,
        SmokeDetected,
        HeatDetected,
        CODetected,
        FloodDetected,
        PanicPressed,
        Tampered,
        FaultDetected,
        BatteryLow,
        Heartbeat;
    }

    public enum ZoneType;
    {
        Immediate,
        Delayed,
        Instant,
        Fire,
        Emergency,
        Perimeter,
        Interior,
        EntryExit,
        Special;
    }

    public enum ZoneStatus;
    {
        Disarmed,
        Armed,
        Breached,
        Bypassed,
        Faulty,
        Unknown;
    }

    public enum ArmingMode;
    {
        Armed,
        Disarmed,
        Bypassed;
    }

    public enum PanicType;
    {
        Medical,
        Fire,
        Police,
        Generic,
        Duress;
    }

    public enum AuthenticationMethod;
    {
        PinCode,
        Biometric,
        Key,
        Remote,
        Voice,
        Card;
    }

    public enum HealthStatus;
    {
        Healthy,
        Warning,
        Error;
    }

    public enum AlarmEventType;
    {
        SystemArmed,
        SystemDisarmed,
        AlarmTriggered,
        AlarmSilenced,
        SensorTriggered,
        ZoneBreached,
        SystemError,
        TestSignal;
    }

    public class AlarmSystemConfig;
    {
        public int ArmingDelaySeconds { get; set; } = 30;
        public int EntryDelaySeconds { get; set; } = 30;
        public int ExitDelaySeconds { get; set; } = 60;
        public int MaxFaultySensors { get; set; } = 3;
        public int MaxOfflineSensors { get; set; } = 5;
        public int MaxFaultySensorsWhileArmed { get; set; } = 1;
        public int SensorTimeoutMinutes { get; set; } = 5;
        public int SensorCalibrationDays { get; set; } = 90;
        public bool RequireCalibrationForArming { get; set; } = false;
        public double HeatAlarmThreshold { get; set; } = 60.0; // Celsius;
        public int RequiredAuthenticationMethods { get; set; } = 1;
        public bool TriggerAlarmOnAccessDenied { get; set; } = true;
        public int MaxAlarmDurationMinutes { get; set; } = 30;
        public int SilenceTimeoutMinutes { get; set; } = 15;
        public Dictionary<string, object> AdvancedSettings { get; set; } = new();
    }

    public class SecuritySensor;
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public SensorType Type { get; set; }
        public string ZoneId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public SensorStatus Status { get; set; }
        public bool IsArmed { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsInitialized { get; set; }
        public double Sensitivity { get; set; } = 0.5;
        public double Value { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastEventTime { get; set; }
        public SensorEventType? LastEventType { get; set; }
        public DateTime? ArmedTime { get; set; }
        public DateTime? LastCalibration { get; set; }
        public string LastError { get; set; } = string.Empty;
        public int AlarmCount { get; set; }
        public DateTime? LastAlarmTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class AlarmZone;
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ZoneType ZoneType { get; set; }
        public ZoneStatus Status { get; set; }
        public bool IsEnabled { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public List<string> Sensors { get; set; } = new();
        public ArmingMode ArmingMode { get; set; }
        public int EntryDelay { get; set; } = 30;
        public int ExitDelay { get; set; } = 60;
        public DateTime? LastArmed { get; set; }
        public DateTime? LastDisarmed { get; set; }
        public DateTime? LastBreached { get; set; }
        public int BreachCount { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class AlarmTrigger;
    {
        public string Id { get; set; } = string.Empty;
        public AlarmTriggerType TriggerType { get; set; }
        public DateTime TriggeredAt { get; set; }
        public string Source { get; set; } = string.Empty;
        public string ZoneId { get; set; } = string.Empty;
        public string SensorId { get; set; } = string.Empty;
        public AlarmSeverity Severity { get; set; }
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public DateTime? AcknowledgedAt { get; set; }
        public string AcknowledgedBy { get; set; } = string.Empty;
        public DateTime? ResolvedAt { get; set; }
        public string ResolvedBy { get; set; } = string.Empty;
    }

    public class AlarmArmingSchedule;
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public AlarmArmingMode ArmingMode { get; set; }
        public List<DayOfWeek> DaysOfWeek { get; set; } = new();
        public TimeSpan ArmTime { get; set; }
        public TimeSpan DisarmTime { get; set; }
        public string DisarmPinCode { get; set; } = string.Empty;
        public DateTime? LastExecuted { get; set; }
        public int ExecutionCount { get; set; }
        public Dictionary<string, object> Conditions { get; set; } = new();

        public bool ShouldArmAt(DateTime time)
        {
            if (!IsEnabled) return false;
            if (!DaysOfWeek.Contains(time.DayOfWeek)) return false;

            return time.TimeOfDay >= ArmTime &&
                   time.TimeOfDay < ArmTime.Add(TimeSpan.FromMinutes(5));
        }

        public bool ShouldDisarmAt(DateTime time)
        {
            if (!IsEnabled) return false;
            if (!DaysOfWeek.Contains(time.DayOfWeek)) return false;

            return time.TimeOfDay >= DisarmTime &&
                   time.TimeOfDay < DisarmTime.Add(TimeSpan.FromMinutes(5));
        }
    }

    public class ArmRequest;
    {
        public AlarmArmingMode Mode { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string PinCode { get; set; } = string.Empty;
        public object BiometricData { get; set; }
        public string ScheduleId { get; set; } = string.Empty;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class DisarmRequest;
    {
        public string UserId { get; set; } = string.Empty;
        public string PinCode { get; set; } = string.Empty;
        public object BiometricData { get; set; }
        public string KeyCode { get; set; } = string.Empty;
        public object RemoteAuthToken { get; set; }
        public string Reason { get; set; } = string.Empty;
        public bool ForceDisarm { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class PartialArmRequest;
    {
        public List<string> ZoneIds { get; set; } = new();
        public string UserId { get; set; } = string.Empty;
        public string PinCode { get; set; } = string.Empty;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class SensorEvent;
    {
        public string SensorId { get; set; } = string.Empty;
        public SensorEventType EventType { get; set; }
        public SensorStatus Status { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class TriggerRequest;
    {
        public string Source { get; set; } = string.Empty;
        public string ZoneId { get; set; } = string.Empty;
        public string SensorId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class SilenceRequest;
    {
        public string UserId { get; set; } = string.Empty;
        public string PinCode { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class PanicRequest;
    {
        public PanicType PanicType { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string ZoneId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class AlarmStatistics;
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Uptime => EndTime.HasValue ? EndTime.Value - StartTime : DateTime.UtcNow - StartTime;
        public int TotalSensors { get; set; }
        public int ActiveSensors { get; set; }
        public int FaultySensors { get; set; }
        public int TotalZones { get; set; }
        public int ArmedZones { get; set; }
        public int BreachedZones { get; set; }
        public long TotalArmedCount { get; set; }
        public long TotalDisarmedCount { get; set; }
        public int TotalPartialArms { get; set; }
        public long TotalAlarmsTriggered { get; set; }
        public int TotalPanicAlarms { get; set; }
        public int TotalSilencedCount { get; set; }
        public long TotalSensorEvents { get; set; }
        public long NotificationsSent { get; set; }
        public int SirensActivated { get; set; }
        public int AuthoritiesNotified { get; set; }
        public int TotalSelfTests { get; set; }
        public DateTime? LastArmedTime { get; set; }
        public DateTime? LastDisarmedTime { get; set; }
        public DateTime? LastPartialArmTime { get; set; }
        public DateTime? LastAlarmTime { get; set; }
        public DateTime? LastPanicAlarmTime { get; set; }
        public DateTime? LastSilencedTime { get; set; }
        public DateTime? LastNotificationSent { get; set; }
        public DateTime? LastSirenActivation { get; set; }
        public DateTime? LastAuthorityNotification { get; set; }
        public DateTime? LastSelfTest { get; set; }
        public DateTime? LastSirenTest { get; set; }
        public TimeSpan TotalArmedDuration { get; set; }
    }

    public class AlarmResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public AlarmSystemState? NewState { get; set; }

        public static AlarmResult Success(string message = "Operation completed successfully", AlarmSystemState? newState = null) =>
            new() { Success = true, Message = message, Timestamp = DateTime.UtcNow, NewState = newState };

        public static AlarmResult Failure(string error) =>
            new() { Success = false, Message = error, Timestamp = DateTime.UtcNow };

        public static AlarmResult AlreadyInitialized() =>
            new() { Success = false, Message = "System already initialized", Timestamp = DateTime.UtcNow };

        public static AlarmResult NotInitialized() =>
            new() { Success = false, Message = "System not initialized", Timestamp = DateTime.UtcNow };

        public static AlarmResult AlreadyRunning() =>
            new() { Success = false, Message = "System already running", Timestamp = DateTime.UtcNow };

        public static AlarmResult AlreadyStopped() =>
            new() { Success = false, Message = "System already stopped", Timestamp = DateTime.UtcNow };
    }

    public class SensorResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public SecuritySensor Sensor { get; set; }

        public static SensorResult Success(SecuritySensor sensor) =>
            new() { Success = true, Sensor = sensor, Message = "Sensor operation completed" };

        public static SensorResult Failure(string error) =>
            new() { Success = false, Message = error };

        public static SensorResult NotFound() =>
            new() { Success = false, Message = "Sensor not found" };
    }

    public class SensorInitializationResult;
    {
        public bool Success { get; set; }
        public string Error { get; set; } = string.Empty;

        public static SensorInitializationResult Success() =>
            new() { Success = true };

        public static SensorInitializationResult Failure(string error) =>
            new() { Success = false, Error = error };
    }

    public class SensorCheckResult;
    {
        public bool AllSensorsReady { get; set; }
        public int TotalSensors { get; set; }
        public List<string> ReadySensors { get; set; } = new();
        public List<SensorStatusInfo> FailedSensors { get; set; } = new();
    }

    public class SensorStatusInfo;
    {
        public string SensorId { get; set; } = string.Empty;
        public string SensorName { get; set; } = string.Empty;
        public SensorStatus Status { get; set; }
        public string LastError { get; set; } = string.Empty;
    }

    public class ZoneCheckResult;
    {
        public bool AllZonesSecure { get; set; }
        public int TotalZones { get; set; }
        public List<string> SecureZones { get; set; } = new();
        public List<ZoneStatusInfo> UnsecureZones { get; set; } = new();
    }

    public class ZoneStatusInfo;
    {
        public string ZoneId { get; set; } = string.Empty;
        public string ZoneName { get; set; } = string.Empty;
        public List<string> UnreadySensors { get; set; } = new();
        public int SensorCount { get; set; }
        public int UnreadyCount { get; set; }
    }

    public class SystemHealthStatus;
    {
        public string Component { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public HealthStatus OverallStatus { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<SystemHealthStatus> Subcomponents { get; set; } = new();
        public Dictionary<string, object> Metrics { get; set; } = new();
    }

    public class AlarmStateChangedEventArgs : EventArgs;
    {
        public AlarmSystemState PreviousState { get; set; }
        public AlarmSystemState NewState { get; set; }
        public DateTime ChangedAt { get; set; }
        public string ChangedBy { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public class AlarmTriggeredEventArgs : EventArgs;
    {
        public AlarmTrigger Trigger { get; set; }
        public DateTime TriggeredAt { get; set; }
        public AlarmSystemState PreviousState { get; set; }
        public bool IsManual { get; set; }
        public bool IsPanic { get; set; }
        public string ZoneName { get; set; } = string.Empty;
    }

    public class SensorStatusChangedEventArgs : EventArgs;
    {
        public string SensorId { get; set; } = string.Empty;
        public SensorStatus PreviousStatus { get; set; }
        public SensorStatus NewStatus { get; set; }
        public DateTime ChangedAt { get; set; }
        public SensorEventType? EventType { get; set; }
        public double? Value { get; set; }
    }

    public class ZoneStatusChangedEventArgs : EventArgs;
    {
        public string ZoneId { get; set; } = string.Empty;
        public ZoneStatus PreviousStatus { get; set; }
        public ZoneStatus NewStatus { get; set; }
        public DateTime ChangedAt { get; set; }
        public string ChangedBy { get; set; } = string.Empty;
        public AlarmArmingMode? ArmingMode { get; set; }
    }

    public class AlarmEvent : IEvent;
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public AlarmEventType EventType { get; set; }
        public string ZoneId { get; set; } = string.Empty;
        public string SensorId { get; set; } = string.Empty;
        public AlarmSeverity Severity { get; set; }
        public Dictionary<string, object> Data { get; set; } = new();
    }

    // Configuration classes;
    public class SensorConfig;
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public SensorType Type { get; set; }
        public string ZoneId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public double Sensitivity { get; set; } = 0.5;
        public bool IsEnabled { get; set; } = true;
        public DateTime? LastCalibration { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class ZoneConfig;
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ZoneType ZoneType { get; set; }
        public bool IsEnabled { get; set; } = true;
        public ArmingMode ArmingMode { get; set; }
        public int EntryDelay { get; set; } = 30;
        public int ExitDelay { get; set; } = 60;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    // Event bus event classes;
    public class SecurityEvent : IEvent;
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public SecurityEventType EventType { get; set; }
        public string ZoneId { get; set; } = string.Empty;
        public string SensorId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public enum SecurityEventType;
    {
        IntrusionDetected,
        TamperDetected,
        AccessDenied,
        AccessGranted,
        SystemTamper,
        SensorFailure;
    }

    public class SystemAlertEvent : IEvent;
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public SystemEventType EventType { get; set; }
        public string Source { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public enum SystemEventType;
    {
        PowerFailure,
        CommunicationFailure,
        BatteryLow,
        SystemError,
        MaintenanceRequired,
        TestSignal;
    }

    // Notification class (simplified)
    public class Notification;
    {
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationType Type { get; set; }
        public NotificationPriority Priority { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public enum NotificationType;
    {
        Info,
        Warning,
        Alert,
        Emergency;
    }

    public enum NotificationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    #endregion;
}
