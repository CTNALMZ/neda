using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YourNamespace.Security.IntrusionDetection;
{
    #region Interfaces;

    /// <summary>
    /// Interface for intrusion detection sensors;
    /// </summary>
    public interface IIntrusionSensor;
    {
        /// <summary>
        /// Sensor unique identifier;
        /// </summary>
        string SensorId { get; }

        /// <summary>
        /// Sensor name;
        /// </summary>
        string SensorName { get; }

        /// <summary>
        /// Sensor type;
        /// </summary>
        SensorType SensorType { get; }

        /// <summary>
        /// Current sensor status;
        /// </summary>
        SensorStatus Status { get; }

        /// <summary>
        /// Location where the sensor is installed;
        /// </summary>
        string Location { get; set; }

        /// <summary>
        /// Whether the sensor is armed/enabled;
        /// </summary>
        bool IsArmed { get; set; }

        /// <summary>
        /// Sensitivity level (1-10)
        /// </summary>
        int Sensitivity { get; set; }

        /// <summary>
        /// Initialize the sensor;
        /// </summary>
        Task<bool> InitializeAsync();

        /// <summary>
        /// Arm the sensor for intrusion detection;
        /// </summary>
        Task<bool> ArmAsync();

        /// <summary>
        /// Disarm the sensor;
        /// </summary>
        Task<bool> DisarmAsync();

        /// <summary>
        /// Test the sensor functionality;
        /// </summary>
        Task<SensorTestResult> TestAsync();

        /// <summary>
        /// Calibrate the sensor;
        /// </summary>
        Task<CalibrationResult> CalibrateAsync();

        /// <summary>
        /// Get sensor diagnostics;
        /// </summary>
        Task<SensorDiagnostics> GetDiagnosticsAsync();

        /// <summary>
        /// Subscribe to intrusion events;
        /// </summary>
        void SubscribeToEvents(IIntrusionEventHandler eventHandler);

        /// <summary>
        /// Get recent intrusion events;
        /// </summary>
        Task<IEnumerable<IntrusionEvent>> GetRecentEventsAsync(int count = 50);

        /// <summary>
        /// Clear sensor event history;
        /// </summary>
        Task ClearHistoryAsync();

        /// <summary>
        /// Start continuous monitoring;
        /// </summary>
        Task StartMonitoringAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stop continuous monitoring;
        /// </summary>
        Task StopMonitoringAsync();
    }

    /// <summary>
    /// Interface for intrusion event handlers;
    /// </summary>
    public interface IIntrusionEventHandler;
    {
        Task OnIntrusionDetectedAsync(IntrusionEvent intrusionEvent);
        Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus);
        Task OnSensorErrorAsync(string sensorId, Exception error);
        Task OnSensorCalibratedAsync(string sensorId, CalibrationResult result);
        Task OnSensorTestedAsync(string sensorId, SensorTestResult result);
    }

    /// <summary>
    /// Interface for intrusion detection system;
    /// </summary>
    public interface IIntrusionDetectionSystem;
    {
        /// <summary>
        /// Register a sensor with the system;
        /// </summary>
        Task<bool> RegisterSensorAsync(IIntrusionSensor sensor);

        /// <summary>
        /// Unregister a sensor;
        /// </summary>
        Task<bool> UnregisterSensorAsync(string sensorId);

        /// <summary>
        /// Get all registered sensors;
        /// </summary>
        Task<IEnumerable<IIntrusionSensor>> GetSensorsAsync();

        /// <summary>
        /// Get sensor by ID;
        /// </summary>
        Task<IIntrusionSensor> GetSensorAsync(string sensorId);

        /// <summary>
        /// Arm all sensors;
        /// </summary>
        Task<int> ArmAllSensorsAsync();

        /// <summary>
        /// Disarm all sensors;
        /// </summary>
        Task<int> DisarmAllSensorsAsync();

        /// <summary>
        /// Get system status;
        /// </summary>
        Task<SystemStatus> GetSystemStatusAsync();

        /// <summary>
        /// Get intrusion statistics;
        /// </summary>
        Task<IntrusionStatistics> GetStatisticsAsync(DateTime? fromDate = null, DateTime? toDate = null);

        /// <summary>
        /// Configure intrusion detection rules;
        /// </summary>
        Task ConfigureRulesAsync(IEnumerable<DetectionRule> rules);

        /// <summary>
        /// Analyze patterns from intrusion events;
        /// </summary>
        Task<PatternAnalysisResult> AnalyzePatternsAsync(DateTime? fromDate = null, DateTime? toDate = null);

        /// <summary>
        /// Start the intrusion detection system;
        /// </summary>
        Task StartSystemAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stop the intrusion detection system;
        /// </summary>
        Task StopSystemAsync();

        /// <summary>
        /// Subscribe to system events;
        /// </summary>
        void SubscribeToSystemEvents(IIntrusionEventHandler eventHandler);

        /// <summary>
        /// Perform system-wide test;
        /// </summary>
        Task<SystemTestResult> PerformSystemTestAsync();
    }

    #endregion;

    #region Main Implementation - Intrusion Detection System;

    /// <summary>
    /// Intrusion Detection System implementation;
    /// </summary>
    public class IntrusionDetectionSystem : IIntrusionDetectionSystem, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger<IntrusionDetectionSystem> _logger;
        private readonly IntrusionDetectionOptions _options;
        private readonly ConcurrentDictionary<string, IIntrusionSensor> _sensors;
        private readonly ConcurrentDictionary<string, SensorInfo> _sensorInfo;
        private readonly ConcurrentQueue<IntrusionEvent> _intrusionEvents;
        private readonly ConcurrentBag<IIntrusionEventHandler> _eventHandlers;
        private readonly List<DetectionRule> _detectionRules;
        private readonly System.Timers.Timer _healthCheckTimer;
        private readonly System.Timers.Timer _patternAnalysisTimer;
        private readonly object _lockObject = new object();
        private readonly CancellationTokenSource _globalCancellationTokenSource;
        private Task _monitoringTask;
        private bool _isRunning;
        private bool _isDisposed;
        private DateTime _systemStartTime;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of IntrusionDetectionSystem;
        /// </summary>
        public IntrusionDetectionSystem(
            ILogger<IntrusionDetectionSystem> logger,
            IOptions<IntrusionDetectionOptions> options = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? new IntrusionDetectionOptions();

            _sensors = new ConcurrentDictionary<string, IIntrusionSensor>();
            _sensorInfo = new ConcurrentDictionary<string, SensorInfo>();
            _intrusionEvents = new ConcurrentQueue<IntrusionEvent>();
            _eventHandlers = new ConcurrentBag<IIntrusionEventHandler>();
            _detectionRules = new List<DetectionRule>();
            _globalCancellationTokenSource = new CancellationTokenSource();

            _healthCheckTimer = new System.Timers.Timer;
            {
                Interval = _options.HealthCheckInterval.TotalMilliseconds,
                AutoReset = true;
            };
            _healthCheckTimer.Elapsed += OnHealthCheckTimerElapsed;

            _patternAnalysisTimer = new System.Timers.Timer;
            {
                Interval = _options.PatternAnalysisInterval.TotalMilliseconds,
                AutoReset = true;
            };
            _patternAnalysisTimer.Elapsed += OnPatternAnalysisTimerElapsed;

            InitializeDefaultRules();
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Register a sensor with the system;
        /// </summary>
        public async Task<bool> RegisterSensorAsync(IIntrusionSensor sensor)
        {
            if (sensor == null)
                throw new ArgumentNullException(nameof(sensor));

            if (string.IsNullOrWhiteSpace(sensor.SensorId))
                throw new ArgumentException("Sensor must have a valid ID", nameof(sensor));

            try
            {
                // Initialize sensor;
                var initialized = await sensor.InitializeAsync();
                if (!initialized)
                {
                    _logger.LogWarning("Failed to initialize sensor {SensorId}", sensor.SensorId);
                    return false;
                }

                // Subscribe to sensor events;
                sensor.SubscribeToEvents(new SensorEventHandler(this, sensor.SensorId, _logger));

                // Add to sensors collection;
                if (_sensors.TryAdd(sensor.SensorId, sensor))
                {
                    // Create sensor info;
                    var sensorInfo = new SensorInfo;
                    {
                        SensorId = sensor.SensorId,
                        SensorName = sensor.SensorName,
                        SensorType = sensor.SensorType,
                        Location = sensor.Location,
                        RegisteredAt = DateTime.Now,
                        LastStatusChange = DateTime.Now,
                        LastEventTime = null,
                        TotalEvents = 0,
                        IsArmed = sensor.IsArmed;
                    };

                    _sensorInfo.TryAdd(sensor.SensorId, sensorInfo);

                    _logger.LogInformation(
                        "Sensor registered: {SensorName} ({SensorId}) at {Location}",
                        sensor.SensorName, sensor.SensorId, sensor.Location);

                    await TriggerSystemEventAsync($"Sensor '{sensor.SensorName}' registered successfully");
                    return true;
                }

                _logger.LogWarning("Sensor {SensorId} already registered", sensor.SensorId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register sensor {SensorId}", sensor.SensorId);
                return false;
            }
        }

        /// <summary>
        /// Unregister a sensor;
        /// </summary>
        public async Task<bool> UnregisterSensorAsync(string sensorId)
        {
            if (string.IsNullOrWhiteSpace(sensorId))
                throw new ArgumentException("Sensor ID is required", nameof(sensorId));

            if (_sensors.TryRemove(sensorId, out var sensor))
            {
                _sensorInfo.TryRemove(sensorId, out _);

                try
                {
                    await sensor.StopMonitoringAsync();
                    _logger.LogInformation("Sensor {SensorId} unregistered", sensorId);
                    await TriggerSystemEventAsync($"Sensor '{sensor.SensorName}' unregistered");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping sensor {SensorId}", sensorId);
                }
            }

            return false;
        }

        /// <summary>
        /// Get all registered sensors;
        /// </summary>
        public Task<IEnumerable<IIntrusionSensor>> GetSensorsAsync()
        {
            return Task.FromResult(_sensors.Values.AsEnumerable());
        }

        /// <summary>
        /// Get sensor by ID;
        /// </summary>
        public Task<IIntrusionSensor> GetSensorAsync(string sensorId)
        {
            if (string.IsNullOrWhiteSpace(sensorId))
                throw new ArgumentException("Sensor ID is required", nameof(sensorId));

            _sensors.TryGetValue(sensorId, out var sensor);
            return Task.FromResult(sensor);
        }

        /// <summary>
        /// Arm all sensors;
        /// </summary>
        public async Task<int> ArmAllSensorsAsync()
        {
            int armedCount = 0;
            var tasks = new List<Task<bool>>();

            foreach (var sensor in _sensors.Values)
            {
                tasks.Add(sensor.ArmAsync());
            }

            var results = await Task.WhenAll(tasks);
            armedCount = results.Count(r => r);

            _logger.LogInformation("Armed {ArmedCount} of {TotalCount} sensors", armedCount, _sensors.Count);
            await TriggerSystemEventAsync($"Armed {armedCount} sensors");

            return armedCount;
        }

        /// <summary>
        /// Disarm all sensors;
        /// </summary>
        public async Task<int> DisarmAllSensorsAsync()
        {
            int disarmedCount = 0;
            var tasks = new List<Task<bool>>();

            foreach (var sensor in _sensors.Values)
            {
                tasks.Add(sensor.DisarmAsync());
            }

            var results = await Task.WhenAll(tasks);
            disarmedCount = results.Count(r => r);

            _logger.LogInformation("Disarmed {DisarmedCount} of {TotalCount} sensors", disarmedCount, _sensors.Count);
            await TriggerSystemEventAsync($"Disarmed {disarmedCount} sensors");

            return disarmedCount;
        }

        /// <summary>
        /// Get system status;
        /// </summary>
        public Task<SystemStatus> GetSystemStatusAsync()
        {
            var status = new SystemStatus;
            {
                SystemId = _options.SystemId,
                SystemName = _options.SystemName,
                IsRunning = _isRunning,
                StartTime = _systemStartTime,
                Uptime = _isRunning ? DateTime.Now - _systemStartTime : TimeSpan.Zero,
                TotalSensors = _sensors.Count,
                ArmedSensors = _sensors.Values.Count(s => s.IsArmed),
                ActiveAlarms = _intrusionEvents.Count(e =>
                    e.EventType == IntrusionEventType.IntrusionDetected &&
                    e.ResolvedAt == null),
                LastEventTime = _intrusionEvents.LastOrDefault()?.Timestamp,
                MemoryUsage = Process.GetCurrentProcess().WorkingSet64,
                CpuUsage = GetCpuUsage()
            };

            // Get sensor status breakdown;
            status.SensorStatusBreakdown = _sensors.Values;
                .GroupBy(s => s.Status)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            return Task.FromResult(status);
        }

        /// <summary>
        /// Get intrusion statistics;
        /// </summary>
        public Task<IntrusionStatistics> GetStatisticsAsync(DateTime? fromDate = null, DateTime? toDate = null)
        {
            var events = _intrusionEvents.ToList();

            if (fromDate.HasValue)
            {
                events = events.Where(e => e.Timestamp >= fromDate.Value).ToList();
            }

            if (toDate.HasValue)
            {
                events = events.Where(e => e.Timestamp <= toDate.Value).ToList();
            }

            var statistics = new IntrusionStatistics;
            {
                TotalEvents = events.Count,
                IntrusionEvents = events.Count(e => e.EventType == IntrusionEventType.IntrusionDetected),
                TamperEvents = events.Count(e => e.EventType == IntrusionEventType.TamperDetected),
                ErrorEvents = events.Count(e => e.EventType == IntrusionEventType.SensorError),
                TestEvents = events.Count(e => e.EventType == IntrusionEventType.SensorTest),
                CalibrationEvents = events.Count(e => e.EventType == IntrusionEventType.SensorCalibration),

                EventsBySeverity = events;
                    .Where(e => e.Severity.HasValue)
                    .GroupBy(e => e.Severity.Value)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),

                EventsBySensor = events;
                    .GroupBy(e => e.SensorId)
                    .ToDictionary(g => g.Key, g => g.Count()),

                EventsByHour = CalculateEventsByHour(events),

                AverageResponseTime = events;
                    .Where(e => e.ResponseTime.HasValue)
                    .Select(e => e.ResponseTime.Value)
                    .DefaultIfEmpty(TimeSpan.Zero)
                    .Average(t => t.TotalSeconds),

                GeneratedAt = DateTime.Now;
            };

            return Task.FromResult(statistics);
        }

        /// <summary>
        /// Configure intrusion detection rules;
        /// </summary>
        public Task ConfigureRulesAsync(IEnumerable<DetectionRule> rules)
        {
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            lock (_detectionRules)
            {
                _detectionRules.Clear();
                _detectionRules.AddRange(rules);
            }

            _logger.LogInformation("Configured {RuleCount} detection rules", rules.Count());
            return Task.CompletedTask;
        }

        /// <summary>
        /// Analyze patterns from intrusion events;
        /// </summary>
        public Task<PatternAnalysisResult> AnalyzePatternsAsync(DateTime? fromDate = null, DateTime? toDate = null)
        {
            var events = _intrusionEvents.ToList();

            if (fromDate.HasValue)
            {
                events = events.Where(e => e.Timestamp >= fromDate.Value).ToList();
            }

            if (toDate.HasValue)
            {
                events = events.Where(e => e.Timestamp <= toDate.Value).ToList();
            }

            var result = new PatternAnalysisResult;
            {
                TotalEventsAnalyzed = events.Count,
                AnalysisTime = DateTime.Now,
                Patterns = new List<DetectedPattern>()
            };

            if (events.Count < 2)
            {
                return Task.FromResult(result);
            }

            // Analyze temporal patterns;
            var temporalPatterns = AnalyzeTemporalPatterns(events);
            result.Patterns.AddRange(temporalPatterns);

            // Analyze sensor correlation patterns;
            var correlationPatterns = AnalyzeCorrelationPatterns(events);
            result.Patterns.AddRange(correlationPatterns);

            // Calculate risk score;
            result.RiskScore = CalculateRiskScore(events);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Start the intrusion detection system;
        /// </summary>
        public async Task StartSystemAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning)
                return;

            lock (_lockObject)
            {
                if (_isRunning)
                    return;

                _isRunning = true;
            }

            try
            {
                _systemStartTime = DateTime.Now;

                // Start timers;
                _healthCheckTimer.Start();
                _patternAnalysisTimer.Start();

                // Start monitoring task;
                _monitoringTask = Task.Run(() => MonitorSensorsAsync(cancellationToken), cancellationToken);

                // Initialize all sensors;
                await InitializeAllSensorsAsync();

                _logger.LogInformation("Intrusion Detection System started successfully");
                await TriggerSystemEventAsync("System started");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.LogError(ex, "Failed to start Intrusion Detection System");
                throw new InvalidOperationException("Failed to start system", ex);
            }
        }

        /// <summary>
        /// Stop the intrusion detection system;
        /// </summary>
        public async Task StopSystemAsync()
        {
            if (!_isRunning)
                return;

            try
            {
                // Stop timers;
                _healthCheckTimer.Stop();
                _patternAnalysisTimer.Stop();

                // Cancel monitoring;
                _globalCancellationTokenSource.Cancel();

                // Wait for monitoring to stop;
                if (_monitoringTask != null && !_monitoringTask.IsCompleted)
                {
                    await _monitoringTask.WaitAsync(TimeSpan.FromSeconds(30));
                }

                // Disarm all sensors;
                await DisarmAllSensorsAsync();

                _isRunning = false;
                _logger.LogInformation("Intrusion Detection System stopped");
                await TriggerSystemEventAsync("System stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Intrusion Detection System");
                throw new InvalidOperationException("Failed to stop system", ex);
            }
        }

        /// <summary>
        /// Subscribe to system events;
        /// </summary>
        public void SubscribeToSystemEvents(IIntrusionEventHandler eventHandler)
        {
            if (eventHandler == null)
                throw new ArgumentNullException(nameof(eventHandler));

            _eventHandlers.Add(eventHandler);
            _logger.LogInformation("System event handler subscribed: {EventHandlerType}",
                eventHandler.GetType().Name);
        }

        /// <summary>
        /// Perform system-wide test;
        /// </summary>
        public async Task<SystemTestResult> PerformSystemTestAsync()
        {
            var result = new SystemTestResult;
            {
                TestId = Guid.NewGuid().ToString(),
                StartTime = DateTime.Now,
                SensorsTested = new List<SensorTestResult>(),
                OverallStatus = SystemTestStatus.Passed;
            };

            _logger.LogInformation("Starting system test {TestId}", result.TestId);

            try
            {
                var testTasks = new List<Task<SensorTestResult>>();

                foreach (var sensor in _sensors.Values)
                {
                    testTasks.Add(sensor.TestAsync());
                }

                var testResults = await Task.WhenAll(testTasks);
                result.SensorsTested.AddRange(testResults);

                // Check for failures;
                var failedTests = testResults.Where(r => r.Status != SensorTestStatus.Passed).ToList();
                if (failedTests.Any())
                {
                    result.OverallStatus = SystemTestStatus.Failed;
                    result.FailedSensors = failedTests.Select(r => r.SensorId).ToList();
                    result.ErrorMessage = $"{failedTests.Count} sensor(s) failed testing";
                }
                else;
                {
                    result.OverallStatus = SystemTestStatus.Passed;
                }

                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;

                _logger.LogInformation("System test {TestId} completed: {Status}",
                    result.TestId, result.OverallStatus);

                await TriggerSystemEventAsync($"System test completed: {result.OverallStatus}");

                return result;
            }
            catch (Exception ex)
            {
                result.OverallStatus = SystemTestStatus.Failed;
                result.ErrorMessage = ex.Message;
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;

                _logger.LogError(ex, "System test {TestId} failed", result.TestId);
                return result;
            }
        }

        #endregion;

        #region Internal Methods (Called by SensorEventHandler)

        internal async Task OnIntrusionDetectedAsync(IntrusionEvent intrusionEvent)
        {
            try
            {
                // Validate event;
                if (intrusionEvent == null ||
                    string.IsNullOrWhiteSpace(intrusionEvent.SensorId) ||
                    !_sensors.ContainsKey(intrusionEvent.SensorId))
                {
                    _logger.LogWarning("Invalid intrusion event received");
                    return;
                }

                // Assign unique ID if not present;
                if (string.IsNullOrWhiteSpace(intrusionEvent.EventId))
                {
                    intrusionEvent.EventId = Guid.NewGuid().ToString();
                }

                // Set timestamp if not set;
                if (intrusionEvent.Timestamp == default)
                {
                    intrusionEvent.Timestamp = DateTime.Now;
                }

                // Apply detection rules;
                await ApplyDetectionRulesAsync(intrusionEvent);

                // Update sensor info;
                if (_sensorInfo.TryGetValue(intrusionEvent.SensorId, out var sensorInfo))
                {
                    sensorInfo.LastEventTime = intrusionEvent.Timestamp;
                    sensorInfo.TotalEvents++;
                    sensorInfo.LastEventType = intrusionEvent.EventType;
                }

                // Add to event queue;
                _intrusionEvents.Enqueue(intrusionEvent);

                // Limit queue size;
                while (_intrusionEvents.Count > _options.MaxEventHistory)
                {
                    _intrusionEvents.TryDequeue(out _);
                }

                // Trigger response based on severity;
                await TriggerIntrusionResponseAsync(intrusionEvent);

                // Log the event;
                _logger.LogWarning(
                    "INTRUSION DETECTED: Sensor {SensorId} at {Location} - {EventType} (Severity: {Severity})",
                    intrusionEvent.SensorId,
                    intrusionEvent.Location,
                    intrusionEvent.EventType,
                    intrusionEvent.Severity);

                // Notify event handlers;
                await NotifyEventHandlersAsync(intrusionEvent);

                // Check for alarm conditions;
                await CheckAlarmConditionsAsync(intrusionEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing intrusion event");
            }
        }

        internal async Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus)
        {
            if (_sensorInfo.TryGetValue(sensorId, out var sensorInfo))
            {
                sensorInfo.LastStatusChange = DateTime.Now;
                sensorInfo.LastStatus = newStatus;

                _logger.LogInformation(
                    "Sensor {SensorId} status changed: {OldStatus} -> {NewStatus}",
                    sensorId, oldStatus, newStatus);

                // Create status change event;
                var statusEvent = new IntrusionEvent;
                {
                    EventId = Guid.NewGuid().ToString(),
                    SensorId = sensorId,
                    EventType = IntrusionEventType.StatusChange,
                    Timestamp = DateTime.Now,
                    Location = sensorInfo.Location,
                    Details = $"Status changed from {oldStatus} to {newStatus}",
                    Severity = GetSeverityForStatusChange(oldStatus, newStatus)
                };

                _intrusionEvents.Enqueue(statusEvent);

                // Notify event handlers;
                var tasks = new List<Task>();
                foreach (var handler in _eventHandlers)
                {
                    try
                    {
                        tasks.Add(handler.OnSensorStatusChangedAsync(sensorId, oldStatus, newStatus));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                    }
                }

                await Task.WhenAll(tasks);
            }
        }

        internal async Task OnSensorErrorAsync(string sensorId, Exception error)
        {
            _logger.LogError(error, "Sensor {SensorId} error", sensorId);

            // Create error event;
            var errorEvent = new IntrusionEvent;
            {
                EventId = Guid.NewGuid().ToString(),
                SensorId = sensorId,
                EventType = IntrusionEventType.SensorError,
                Timestamp = DateTime.Now,
                Details = error.Message,
                Severity = IntrusionSeverity.High;
            };

            if (_sensorInfo.TryGetValue(sensorId, out var sensorInfo))
            {
                errorEvent.Location = sensorInfo.Location;
            }

            _intrusionEvents.Enqueue(errorEvent);

            // Notify event handlers;
            var tasks = new List<Task>();
            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnSensorErrorAsync(sensorId, error));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        internal async Task OnSensorCalibratedAsync(string sensorId, CalibrationResult result)
        {
            _logger.LogInformation("Sensor {SensorId} calibrated: {Status}", sensorId, result.Status);

            // Create calibration event;
            var calibrationEvent = new IntrusionEvent;
            {
                EventId = Guid.NewGuid().ToString(),
                SensorId = sensorId,
                EventType = IntrusionEventType.SensorCalibration,
                Timestamp = DateTime.Now,
                Details = $"Calibration: {result.Status} - {result.Message}",
                Severity = IntrusionSeverity.Low;
            };

            if (_sensorInfo.TryGetValue(sensorId, out var sensorInfo))
            {
                calibrationEvent.Location = sensorInfo.Location;
            }

            _intrusionEvents.Enqueue(calibrationEvent);

            // Notify event handlers;
            var tasks = new List<Task>();
            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnSensorCalibratedAsync(sensorId, result));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        internal async Task OnSensorTestedAsync(string sensorId, SensorTestResult result)
        {
            _logger.LogInformation("Sensor {SensorId} tested: {Status}", sensorId, result.Status);

            // Create test event;
            var testEvent = new IntrusionEvent;
            {
                EventId = Guid.NewGuid().ToString(),
                SensorId = sensorId,
                EventType = IntrusionEventType.SensorTest,
                Timestamp = DateTime.Now,
                Details = $"Test: {result.Status} - {result.Message}",
                Severity = result.Status == SensorTestStatus.Passed ? IntrusionSeverity.Low : IntrusionSeverity.Medium;
            };

            if (_sensorInfo.TryGetValue(sensorId, out var sensorInfo))
            {
                testEvent.Location = sensorInfo.Location;
            }

            _intrusionEvents.Enqueue(testEvent);

            // Notify event handlers;
            var tasks = new List<Task>();
            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnSensorTestedAsync(sensorId, result));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Initialize all sensors;
        /// </summary>
        private async Task InitializeAllSensorsAsync()
        {
            foreach (var sensor in _sensors.Values)
            {
                try
                {
                    await sensor.InitializeAsync();
                    _logger.LogDebug("Sensor {SensorId} initialized", sensor.SensorId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize sensor {SensorId}", sensor.SensorId);
                }
            }
        }

        /// <summary>
        /// Monitor sensors continuously;
        /// </summary>
        private async Task MonitorSensorsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    // Start monitoring for each sensor;
                    var monitoringTasks = new List<Task>();
                    foreach (var sensor in _sensors.Values)
                    {
                        if (sensor.IsArmed && sensor.Status == SensorStatus.Active)
                        {
                            monitoringTasks.Add(sensor.StartMonitoringAsync(cancellationToken));
                        }
                    }

                    // Wait a bit before checking again;
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in sensor monitoring loop");
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }
        }

        /// <summary>
        /// Initialize default detection rules;
        /// </summary>
        private void InitializeDefaultRules()
        {
            _detectionRules.Add(new DetectionRule;
            {
                RuleId = "R001",
                Name = "Multiple Intrusions - Same Sensor",
                Description = "Trigger if same sensor detects multiple intrusions within short time",
                Condition = "Count(Events.Where(e => e.SensorId == @sensorId && 
                                  e.EventType == IntrusionEventType.IntrusionDetected &&
                                  e.Timestamp > DateTime.Now.AddMinutes(-5))) >= 3",
                Action = "RaiseSeverity(IntrusionSeverity.Critical); NotifySecurityTeam();",
                Priority = 1;
            });
            
            _detectionRules.Add(new DetectionRule;
            {
                RuleId = "R002",
                Name = "Multiple Sensors - Same Area",
                Description = "Trigger if multiple sensors in same area detect intrusions",
                Condition = "Count(Events.Where(e => e.Location == @location && 
                                  e.EventType == IntrusionEventType.IntrusionDetected && 
                                  e.Timestamp > DateTime.Now.AddMinutes(-2))) >= 2",
                Action = "RaiseSeverity(IntrusionSeverity.High); ActivateAlarm();",
                Priority = 2;
            });
            
            _detectionRules.Add(new DetectionRule;
            {
                RuleId = "R003",
                Name = "Tamper Detection",
                Description = "Immediate action on tamper detection",
                Condition = "Event.EventType == IntrusionEventType.TamperDetected",
                Action = "RaiseSeverity(IntrusionSeverity.Critical); NotifySecurityTeam(); LockdownArea();",
                Priority = 1;
            });
        }

        /// <summary>
        /// Apply detection rules to an intrusion event;
        /// </summary>
        private async Task ApplyDetectionRulesAsync(IntrusionEvent intrusionEvent)
{
    lock (_detectionRules)
    {
        foreach (var rule in _detectionRules.OrderBy(r => r.Priority))
        {
            try
            {
                // Simplified rule evaluation - in production, use a rules engine;
                if (ShouldTriggerRule(rule, intrusionEvent))
                {
                    intrusionEvent.TriggeredRules.Add(rule.RuleId);
                    intrusionEvent.Severity = Math.Max((int)intrusionEvent.Severity, (int)IntrusionSeverity.High);

                    _logger.LogInformation(
                        "Rule {RuleId} triggered for event {EventId}",
                        rule.RuleId, intrusionEvent.EventId);

                    // Execute rule actions;
                    await ExecuteRuleActionsAsync(rule, intrusionEvent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating rule {RuleId}", rule.RuleId);
            }
        }
    }
}

/// <summary>
/// Check if a rule should be triggered;
/// </summary>
private bool ShouldTriggerRule(DetectionRule rule, IntrusionEvent currentEvent)
{
    // Simplified rule evaluation;
    // In production, use a proper rules engine like NRules, RulesEngine, or create custom DSL;

    var recentEvents = _intrusionEvents;
        .Where(e => e.Timestamp > DateTime.Now.AddMinutes(-10))
        .ToList();

    // Rule R001: Multiple intrusions from same sensor;
    if (rule.RuleId == "R001")
    {
        var sensorEvents = recentEvents;
            .Where(e => e.SensorId == currentEvent.SensorId &&
                       e.EventType == IntrusionEventType.IntrusionDetected)
            .ToList();

        return sensorEvents.Count >= 2; // Already have current event + at least 1 more;
    }

    // Rule R002: Multiple sensors in same area;
    if (rule.RuleId == "R002")
    {
        var locationEvents = recentEvents;
            .Where(e => e.Location == currentEvent.Location &&
                       e.EventType == IntrusionEventType.IntrusionDetected)
            .Select(e => e.SensorId)
            .Distinct()
            .ToList();

        return locationEvents.Count >= 2;
    }

    // Rule R003: Tamper detection;
    if (rule.RuleId == "R003")
    {
        return currentEvent.EventType == IntrusionEventType.TamperDetected;
    }

    return false;
}

/// <summary>
/// Execute rule actions;
/// </summary>
private async Task ExecuteRuleActionsAsync(DetectionRule rule, IntrusionEvent intrusionEvent)
{
    // Execute actions based on rule;
    foreach (var action in rule.Action.Split(';'))
    {
        var trimmedAction = action.Trim();
        if (string.IsNullOrEmpty(trimmedAction))
            continue;

        await ExecuteActionAsync(trimmedAction, intrusionEvent);
    }
}

/// <summary>
/// Execute a single action;
/// </summary>
private async Task ExecuteActionAsync(string action, IntrusionEvent intrusionEvent)
{
    if (action.StartsWith("RaiseSeverity"))
    {
        // Parse and set severity;
        if (action.Contains("Critical"))
            intrusionEvent.Severity = IntrusionSeverity.Critical;
        else if (action.Contains("High"))
            intrusionEvent.Severity = IntrusionSeverity.High;
    }
    else if (action.StartsWith("NotifySecurityTeam"))
    {
        await NotifySecurityTeamAsync(intrusionEvent);
    }
    else if (action.StartsWith("ActivateAlarm"))
    {
        await ActivateAlarmAsync(intrusionEvent);
    }
    else if (action.StartsWith("LockdownArea"))
    {
        await LockdownAreaAsync(intrusionEvent.Location);
    }
}

/// <summary>
/// Trigger intrusion response;
/// </summary>
private async Task TriggerIntrusionResponseAsync(IntrusionEvent intrusionEvent)
{
    var responseStart = DateTime.Now;

    try
    {
        // Log intrusion for audit;
        await LogIntrusionForAuditAsync(intrusionEvent);

        // Take action based on severity;
        switch (intrusionEvent.Severity)
        {
            case IntrusionSeverity.Critical:
                await HandleCriticalIntrusionAsync(intrusionEvent);
                break;

            case IntrusionSeverity.High:
                await HandleHighSeverityIntrusionAsync(intrusionEvent);
                break;

            case IntrusionSeverity.Medium:
                await HandleMediumSeverityIntrusionAsync(intrusionEvent);
                break;

            case IntrusionSeverity.Low:
                await HandleLowSeverityIntrusionAsync(intrusionEvent);
                break;
        }

        intrusionEvent.ResponseTime = DateTime.Now - responseStart;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error in intrusion response for event {EventId}", intrusionEvent.EventId);
        intrusionEvent.ResponseTime = DateTime.Now - responseStart;
    }
}

/// <summary>
/// Handle critical intrusion;
/// </summary>
private async Task HandleCriticalIntrusionAsync(IntrusionEvent intrusionEvent)
{
    // Activate all alarms;
    await ActivateAlarmAsync(intrusionEvent);

    // Notify security team immediately;
    await NotifySecurityTeamAsync(intrusionEvent, priority: "CRITICAL");

    // Notify authorities if configured;
    if (_options.NotifyAuthoritiesOnCritical)
    {
        await NotifyAuthoritiesAsync(intrusionEvent);
    }

    // Lockdown affected area;
    await LockdownAreaAsync(intrusionEvent.Location);

    // Record video/audio if available;
    await RecordEvidenceAsync(intrusionEvent);
}

/// <summary>
/// Handle high severity intrusion;
/// </summary>
private async Task HandleHighSeverityIntrusionAsync(IntrusionEvent intrusionEvent)
{
    // Activate local alarm;
    await ActivateAlarmAsync(intrusionEvent);

    // Notify security team;
    await NotifySecurityTeamAsync(intrusionEvent, priority: "HIGH");

    // Record evidence;
    await RecordEvidenceAsync(intrusionEvent);
}

/// <summary>
/// Handle medium severity intrusion;
/// </summary>
private async Task HandleMediumSeverityIntrusionAsync(IntrusionEvent intrusionEvent)
{
    // Log and notify;
    await NotifySecurityTeamAsync(intrusionEvent, priority: "MEDIUM");

    // Monitor situation;
    await MonitorSituationAsync(intrusionEvent);
}

/// <summary>
/// Handle low severity intrusion;
/// </summary>
private async Task HandleLowSeverityIntrusionAsync(IntrusionEvent intrusionEvent)
{
    // Log the event;
    _logger.LogInformation("Low severity intrusion: {EventId}", intrusionEvent.EventId);

    // Schedule follow-up check;
    await ScheduleFollowUpCheckAsync(intrusionEvent);
}

/// <summary>
/// Activate alarm;
/// </summary>
private async Task ActivateAlarmAsync(IntrusionEvent intrusionEvent)
{
    _logger.LogWarning("ACTIVATING ALARM for intrusion {EventId}", intrusionEvent.EventId);

    // In production, this would trigger physical alarms, sirens, etc.
    // For now, we'll log and simulate;

    var alarmEvent = new IntrusionEvent;
    {
        EventId = Guid.NewGuid().ToString(),
        SensorId = "SYSTEM",
        EventType = IntrusionEventType.AlarmActivated,
        Timestamp = DateTime.Now,
        Location = intrusionEvent.Location,
        Details = $"Alarm activated due to intrusion {intrusionEvent.EventId}",
        Severity = intrusionEvent.Severity,
        ParentEventId = intrusionEvent.EventId;
    };

    _intrusionEvents.Enqueue(alarmEvent);

    // Simulate alarm activation;
    await Task.Delay(100); // Simulate network/device communication;

    await TriggerSystemEventAsync($"Alarm activated at {intrusionEvent.Location}");
}

/// <summary>
/// Notify security team;
/// </summary>
private async Task NotifySecurityTeamAsync(IntrusionEvent intrusionEvent, string priority = "NORMAL")
{
    _logger.LogInformation("Notifying security team about intrusion {EventId}", intrusionEvent.EventId);

    // In production, this would send emails, SMS, push notifications, etc.

    var notificationEvent = new IntrusionEvent;
    {
        EventId = Guid.NewGuid().ToString(),
        SensorId = "SYSTEM",
        EventType = IntrusionEventType.SecurityNotified,
        Timestamp = DateTime.Now,
        Location = intrusionEvent.Location,
        Details = $"Security team notified (Priority: {priority}) about intrusion {intrusionEvent.EventId}",
        Severity = intrusionEvent.Severity,
        ParentEventId = intrusionEvent.EventId;
    };

    _intrusionEvents.Enqueue(notificationEvent);

    await Task.Delay(50); // Simulate notification delay;
}

/// <summary>
/// Notify authorities;
/// </summary>
private async Task NotifyAuthoritiesAsync(IntrusionEvent intrusionEvent)
{
    _logger.LogWarning("Notifying authorities about critical intrusion {EventId}", intrusionEvent.EventId);

    // This would typically call emergency services API;

    var authorityEvent = new IntrusionEvent;
    {
        EventId = Guid.NewGuid().ToString(),
        SensorId = "SYSTEM",
        EventType = IntrusionEventType.AuthoritiesNotified,
        Timestamp = DateTime.Now,
        Location = intrusionEvent.Location,
        Details = $"Authorities notified about critical intrusion {intrusionEvent.EventId}",
        Severity = IntrusionSeverity.Critical,
        ParentEventId = intrusionEvent.EventId;
    };

    _intrusionEvents.Enqueue(authorityEvent);
}

/// <summary>
/// Lockdown area;
/// </summary>
private async Task LockdownAreaAsync(string location)
{
    _logger.LogWarning("LOCKING DOWN area: {Location}", location);

    // This would trigger door locks, gate closures, etc.

    var lockdownEvent = new IntrusionEvent;
    {
        EventId = Guid.NewGuid().ToString(),
        SensorId = "SYSTEM",
        EventType = IntrusionEventType.AreaLockdown,
        Timestamp = DateTime.Now,
        Location = location,
        Details = $"Area lockdown initiated",
        Severity = IntrusionSeverity.Critical;
    };

    _intrusionEvents.Enqueue(lockdownEvent);
}

/// <summary>
/// Record evidence;
/// </summary>
private async Task RecordEvidenceAsync(IntrusionEvent intrusionEvent)
{
    _logger.LogInformation("Recording evidence for intrusion {EventId}", intrusionEvent.EventId);

    // This would trigger CCTV recording, audio recording, etc.

    var evidenceEvent = new IntrusionEvent;
    {
        EventId = Guid.NewGuid().ToString(),
        SensorId = "SYSTEM",
        EventType = IntrusionEventType.EvidenceRecorded,
        Timestamp = DateTime.Now,
        Location = intrusionEvent.Location,
        Details = $"Evidence recording started for intrusion {intrusionEvent.EventId}",
        Severity = intrusionEvent.Severity,
        ParentEventId = intrusionEvent.EventId;
    };

    _intrusionEvents.Enqueue(evidenceEvent);
}

/// <summary>
/// Monitor situation;
/// </summary>
private async Task MonitorSituationAsync(IntrusionEvent intrusionEvent)
{
    // Set up monitoring for the affected area;
    _logger.LogInformation("Monitoring situation for intrusion {EventId}", intrusionEvent.EventId);

    // This could involve increasing sensor sensitivity in the area,
    // scheduling periodic checks, etc.

    await Task.CompletedTask;
}

/// <summary>
/// Schedule follow-up check;
/// </summary>
private async Task ScheduleFollowUpCheckAsync(IntrusionEvent intrusionEvent)
{
    // Schedule a check after some time;
    _logger.LogInformation("Scheduling follow-up check for intrusion {EventId}", intrusionEvent.EventId);

    // Implementation would use a scheduler/timer;
    await Task.CompletedTask;
}

/// <summary>
/// Log intrusion for audit;
/// </summary>
private async Task LogIntrusionForAuditAsync(IntrusionEvent intrusionEvent)
{
    // In production, this would write to audit database;
    _logger.LogInformation(
        "AUDIT: Intrusion {EventId} - Sensor: {SensorId}, Location: {Location}, Type: {EventType}, Severity: {Severity}",
        intrusionEvent.EventId,
        intrusionEvent.SensorId,
        intrusionEvent.Location,
        intrusionEvent.EventType,
        intrusionEvent.Severity);

    await Task.CompletedTask;
}

/// <summary>
/// Check for alarm conditions;
/// </summary>
private async Task CheckAlarmConditionsAsync(IntrusionEvent intrusionEvent)
{
    // Check if we need to activate alarms based on multiple events;
    var recentIntrusions = _intrusionEvents;
        .Where(e => e.EventType == IntrusionEventType.IntrusionDetected &&
                   e.Timestamp > DateTime.Now.AddMinutes(-_options.AlarmActivationThresholdMinutes))
        .ToList();

    if (recentIntrusions.Count >= _options.MinIntrusionsForAlarm)
    {
        await ActivateAlarmAsync(intrusionEvent);
    }
}

/// <summary>
/// Calculate events by hour;
/// </summary>
private Dictionary<int, int> CalculateEventsByHour(List<IntrusionEvent> events)
{
    var result = new Dictionary<int, int>();

    for (int hour = 0; hour < 24; hour++)
    {
        result[hour] = events.Count(e => e.Timestamp.Hour == hour);
    }

    return result;
}

/// <summary>
/// Analyze temporal patterns;
/// </summary>
private List<DetectedPattern> AnalyzeTemporalPatterns(List<IntrusionEvent> events)
{
    var patterns = new List<DetectedPattern>();

    if (events.Count < 10)
        return patterns;

    // Group by hour to find peak times;
    var hourlyCounts = events;
        .GroupBy(e => e.Timestamp.Hour)
        .Select(g => new { Hour = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .ToList();

    if (hourlyCounts.First().Count >= 3)
    {
        patterns.Add(new DetectedPattern;
        {
            PatternType = PatternType.Temporal,
            Description = $"Peak intrusion activity between {hourlyCounts.First().Hour}:00 - {hourlyCounts.First().Hour + 1}:00",
            Confidence = Math.Min(0.9, hourlyCounts.First().Count / 10.0),
            RecommendedAction = "Increase monitoring during peak hours"
        });
    }

    // Check for patterns by day of week;
    var dailyCounts = events;
        .GroupBy(e => e.Timestamp.DayOfWeek)
        .Select(g => new { Day = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .ToList();

    if (dailyCounts.First().Count >= 5)
    {
        patterns.Add(new DetectedPattern;
        {
            PatternType = PatternType.Temporal,
            Description = $"Increased activity on {dailyCounts.First().Day}s",
            Confidence = Math.Min(0.8, dailyCounts.First().Count / 20.0),
            RecommendedAction = "Schedule additional security personnel"
        });
    }

    return patterns;
}

/// <summary>
/// Analyze correlation patterns;
/// </summary>
private List<DetectedPattern> AnalyzeCorrelationPatterns(List<IntrusionEvent> events)
{
    var patterns = new List<DetectedPattern>();

    // Find sensor correlations;
    var sensorPairs = new Dictionary<string, int>();
    var sensorList = events.Select(e => e.SensorId).Distinct().ToList();

    for (int i = 0; i < sensorList.Count; i++)
    {
        for (int j = i + 1; j < sensorList.Count; j++)
        {
            var sensor1 = sensorList[i];
            var sensor2 = sensorList[j];

            var simultaneousEvents = events;
                .Where(e => (e.SensorId == sensor1 || e.SensorId == sensor2) &&
                           e.EventType == IntrusionEventType.IntrusionDetected)
                .GroupBy(e => e.Timestamp.Date)
                .Count(g => g.Count() >= 2);

            if (simultaneousEvents >= 3)
            {
                var key = $"{sensor1}-{sensor2}";
                sensorPairs[key] = simultaneousEvents;
            }
        }
    }

    foreach (var pair in sensorPairs.OrderByDescending(p => p.Value).Take(3))
    {
        patterns.Add(new DetectedPattern;
        {
            PatternType = PatternType.Correlation,
            Description = $"Sensors {pair.Key} often trigger together",
            Confidence = Math.Min(0.7, pair.Value / 10.0),
            RecommendedAction = "Review sensor placement and coverage"
        });
    }

    return patterns;
}

/// <summary>
/// Calculate risk score;
/// </summary>
private double CalculateRiskScore(List<IntrusionEvent> events)
{
    if (!events.Any())
        return 0.0;

    var intrusionEvents = events;
        .Where(e => e.EventType == IntrusionEventType.IntrusionDetected)
        .ToList();

    if (!intrusionEvents.Any())
        return 0.0;

    // Calculate based on:
    // 1. Number of intrusions (40%)
    // 2. Average severity (30%)
    // 3. Recency (30%)

    var countScore = Math.Min(1.0, intrusionEvents.Count / 20.0) * 0.4;

    var severityScore = intrusionEvents;
        .Average(e => (int)e.Severity / 4.0) * 0.3;

    var mostRecent = intrusionEvents.Max(e => e.Timestamp);
    var hoursSinceLast = (DateTime.Now - mostRecent).TotalHours;
    var recencyScore = Math.Exp(-hoursSinceLast / 24.0) * 0.3; // Decay over 24 hours;

    return Math.Min(1.0, (countScore + severityScore + recencyScore) * 100);
}

/// <summary>
/// Get CPU usage (simplified)
/// </summary>
private double GetCpuUsage()
{
    try
    {
        var process = Process.GetCurrentProcess();
        var startTime = process.StartTime;
        var totalProcessorTime = process.TotalProcessorTime;
        var runTime = DateTime.Now - startTime;

        return (totalProcessorTime.TotalMilliseconds / (runTime.TotalMilliseconds * Environment.ProcessorCount)) * 100;
    }
    catch
    {
        return 0.0;
    }
}

/// <summary>
/// Get severity for status change;
/// </summary>
private IntrusionSeverity GetSeverityForStatusChange(SensorStatus oldStatus, SensorStatus newStatus)
{
    // Critical status changes;
    if (newStatus == SensorStatus.Error ||
        newStatus == SensorStatus.Tampered ||
        newStatus == SensorStatus.Offline)
    {
        return IntrusionSeverity.High;
    }

    // Normal status changes;
    if ((oldStatus == SensorStatus.Inactive && newStatus == SensorStatus.Active) ||
        (oldStatus == SensorStatus.Active && newStatus == SensorStatus.Inactive))
    {
        return IntrusionSeverity.Low;
    }

    return IntrusionSeverity.Informational;
}

/// <summary>
/// Trigger system event;
/// </summary>
private async Task TriggerSystemEventAsync(string message)
{
    var systemEvent = new IntrusionEvent;
    {
        EventId = Guid.NewGuid().ToString(),
        SensorId = "SYSTEM",
        EventType = IntrusionEventType.SystemEvent,
        Timestamp = DateTime.Now,
        Details = message,
        Severity = IntrusionSeverity.Informational;
    };

    _intrusionEvents.Enqueue(systemEvent);

    await Task.CompletedTask;
}

/// <summary>
/// Notify all event handlers;
/// </summary>
private async Task NotifyEventHandlersAsync(IntrusionEvent intrusionEvent)
{
    var tasks = new List<Task>();

    foreach (var handler in _eventHandlers)
    {
        try
        {
            tasks.Add(handler.OnIntrusionDetectedAsync(intrusionEvent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
        }
    }

    await Task.WhenAll(tasks);
}

/// <summary>
/// Handle health check timer;
/// </summary>
private async void OnHealthCheckTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
{
    try
    {
        await PerformHealthCheckAsync();
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error in health check timer");
    }
}

/// <summary>
/// Perform system health check;
/// </summary>
private async Task PerformHealthCheckAsync()
{
    var healthySensors = 0;
    var totalSensors = _sensors.Count;

    foreach (var sensor in _sensors.Values)
    {
        try
        {
            var diagnostics = await sensor.GetDiagnosticsAsync();
            if (diagnostics.HealthStatus == SensorHealthStatus.Healthy)
            {
                healthySensors++;
            }
            else;
            {
                _logger.LogWarning("Sensor {SensorId} health check failed: {Status}",
                    sensor.SensorId, diagnostics.HealthStatus);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed for sensor {SensorId}", sensor.SensorId);
        }
    }

    var healthPercentage = totalSensors > 0 ? (healthySensors * 100.0 / totalSensors) : 100.0;

    if (healthPercentage < 80.0)
    {
        _logger.LogWarning("System health check: {Healthy}/{Total} sensors healthy ({Percentage:F1}%)",
            healthySensors, totalSensors, healthPercentage);
    }
    else;
    {
        _logger.LogDebug("System health check: {Healthy}/{Total} sensors healthy ({Percentage:F1}%)",
            healthySensors, totalSensors, healthPercentage);
    }
}

/// <summary>
/// Handle pattern analysis timer;
/// </summary>
private async void OnPatternAnalysisTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
{
    try
    {
        await AnalyzePatternsAsync(DateTime.Now.AddHours(-24), DateTime.Now);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error in pattern analysis timer");
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
            _healthCheckTimer?.Dispose();
            _patternAnalysisTimer?.Dispose();
            _globalCancellationTokenSource?.Dispose();
        }

        _isDisposed = true;
    }
}

#endregion;

#region Nested Classes;

/// <summary>
/// Sensor event handler (bridges sensor events to system)
/// </summary>
private class SensorEventHandler : IIntrusionEventHandler;
{
    private readonly IntrusionDetectionSystem _system;
    private readonly string _sensorId;
    private readonly ILogger _logger;

    public SensorEventHandler(IntrusionDetectionSystem system, string sensorId, ILogger logger)
    {
        _system = system;
        _sensorId = sensorId;
        _logger = logger;
    }

    public Task OnIntrusionDetectedAsync(IntrusionEvent intrusionEvent)
    {
        return _system.OnIntrusionDetectedAsync(intrusionEvent);
    }

    public Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus)
    {
        return _system.OnSensorStatusChangedAsync(sensorId, oldStatus, newStatus);
    }

    public Task OnSensorErrorAsync(string sensorId, Exception error)
    {
        return _system.OnSensorErrorAsync(sensorId, error);
    }

    public Task OnSensorCalibratedAsync(string sensorId, CalibrationResult result)
    {
        return _system.OnSensorCalibratedAsync(sensorId, result);
    }

    public Task OnSensorTestedAsync(string sensorId, SensorTestResult result)
    {
        return _system.OnSensorTestedAsync(sensorId, result);
    }
}

        #endregion;
    }

    #endregion;

    #region Sensor Implementations;

    /// <summary>
    /// Base class for intrusion sensors;
    /// </summary>
    public abstract class BaseIntrusionSensor : IIntrusionSensor;
{
    #region Protected Fields;

    protected readonly ILogger _logger;
    protected readonly List<IIntrusionEventHandler> _eventHandlers;
    protected readonly object _lockObject = new object();
    protected CancellationTokenSource _monitoringCancellationTokenSource;
    protected Task _monitoringTask;
    protected bool _isMonitoring;
    protected DateTime _lastDetectionTime;
    protected int _detectionCount;
    protected Random _random;

    #endregion;

    #region Properties;

    public string SensorId { get; protected set; }
    public string SensorName { get; protected set; }
    public abstract SensorType SensorType { get; }
    public SensorStatus Status { get; protected set; }
    public string Location { get; set; }
    public bool IsArmed { get; set; }
    public int Sensitivity { get; set; } = 5;

    #endregion;

    #region Constructors;

    protected BaseIntrusionSensor(string sensorId, string sensorName, ILogger logger)
    {
        SensorId = sensorId ?? throw new ArgumentNullException(nameof(sensorId));
        SensorName = sensorName ?? throw new ArgumentNullException(nameof(sensorName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _eventHandlers = new List<IIntrusionEventHandler>();
        _random = new Random();
        Status = SensorStatus.Inactive;
        IsArmed = false;
    }

    #endregion;

    #region Public Methods;

    public virtual async Task<bool> InitializeAsync()
    {
        lock (_lockObject)
        {
            if (Status != SensorStatus.Inactive)
            {
                _logger.LogWarning("Sensor {SensorId} already initialized", SensorId);
                return false;
            }

            Status = SensorStatus.Initializing;
        }

        try
        {
            // Perform sensor-specific initialization;
            var initialized = await InitializeSensorAsync();

            if (initialized)
            {
                Status = SensorStatus.Active;
                _logger.LogInformation("Sensor {SensorId} initialized successfully", SensorId);
                await NotifyStatusChangeAsync(SensorStatus.Inactive, SensorStatus.Active);
                return true;
            }
            else;
            {
                Status = SensorStatus.Error;
                _logger.LogError("Sensor {SensorId} initialization failed", SensorId);
                await NotifyStatusChangeAsync(SensorStatus.Initializing, SensorStatus.Error);
                return false;
            }
        }
        catch (Exception ex)
        {
            Status = SensorStatus.Error;
            _logger.LogError(ex, "Error initializing sensor {SensorId}", SensorId);
            await NotifyStatusChangeAsync(SensorStatus.Initializing, SensorStatus.Error);
            await NotifyErrorAsync(ex);
            return false;
        }
    }

    public virtual async Task<bool> ArmAsync()
    {
        if (!IsArmed)
        {
            IsArmed = true;
            _logger.LogInformation("Sensor {SensorId} armed", SensorId);
            await TriggerEventAsync(IntrusionEventType.SensorArmed, "Sensor armed");
            return true;
        }

        return false;
    }

    public virtual async Task<bool> DisarmAsync()
    {
        if (IsArmed)
        {
            IsArmed = false;
            _logger.LogInformation("Sensor {SensorId} disarmed", SensorId);
            await TriggerEventAsync(IntrusionEventType.SensorDisarmed, "Sensor disarmed");
            return true;
        }

        return false;
    }

    public virtual async Task<SensorTestResult> TestAsync()
    {
        var result = new SensorTestResult;
        {
            SensorId = SensorId,
            TestId = Guid.NewGuid().ToString(),
            StartTime = DateTime.Now;
        };

        _logger.LogInformation("Testing sensor {SensorId}", SensorId);

        try
        {
            // Perform sensor-specific test;
            var testPassed = await PerformSensorTestAsync();

            if (testPassed)
            {
                result.Status = SensorTestStatus.Passed;
                result.Message = "Sensor test passed successfully";
                _logger.LogInformation("Sensor {SensorId} test passed", SensorId);
            }
            else;
            {
                result.Status = SensorTestStatus.Failed;
                result.Message = "Sensor test failed";
                _logger.LogWarning("Sensor {SensorId} test failed", SensorId);
            }
        }
        catch (Exception ex)
        {
            result.Status = SensorTestStatus.Error;
            result.Message = $"Test error: {ex.Message}";
            _logger.LogError(ex, "Sensor {SensorId} test error", SensorId);
        }

        result.EndTime = DateTime.Now;
        result.Duration = result.EndTime - result.StartTime;

        await NotifyTestedAsync(result);
        return result;
    }

    public virtual async Task<CalibrationResult> CalibrateAsync()
    {
        var result = new CalibrationResult;
        {
            SensorId = SensorId,
            CalibrationId = Guid.NewGuid().ToString(),
            StartTime = DateTime.Now;
        };

        _logger.LogInformation("Calibrating sensor {SensorId}", SensorId);

        try
        {
            Status = SensorStatus.Calibrating;
            await NotifyStatusChangeAsync(SensorStatus.Active, SensorStatus.Calibrating);

            // Perform sensor-specific calibration;
            var calibrationData = await PerformCalibrationAsync();

            result.Status = CalibrationStatus.Completed;
            result.Message = "Calibration completed successfully";
            result.CalibrationData = calibrationData;

            _logger.LogInformation("Sensor {SensorId} calibration completed", SensorId);
        }
        catch (Exception ex)
        {
            result.Status = CalibrationStatus.Failed;
            result.Message = $"Calibration failed: {ex.Message}";
            _logger.LogError(ex, "Sensor {SensorId} calibration failed", SensorId);
        }

        result.EndTime = DateTime.Now;
        result.Duration = result.EndTime - result.StartTime;

        Status = SensorStatus.Active;
        await NotifyStatusChangeAsync(SensorStatus.Calibrating, SensorStatus.Active);
        await NotifyCalibratedAsync(result);

        return result;
    }

    public virtual Task<SensorDiagnostics> GetDiagnosticsAsync()
    {
        var diagnostics = new SensorDiagnostics;
        {
            SensorId = SensorId,
            Timestamp = DateTime.Now,
            Status = Status,
            IsArmed = IsArmed,
            Sensitivity = Sensitivity,
            Location = Location,
            Uptime = GetUptime(),
            DetectionCount = _detectionCount,
            LastDetectionTime = _lastDetectionTime,
            IsMonitoring = _isMonitoring,
            HealthStatus = GetHealthStatus(),
            BatteryLevel = GetBatteryLevel(),
            SignalStrength = GetSignalStrength(),
            FirmwareVersion = GetFirmwareVersion()
        };

        return Task.FromResult(diagnostics);
    }

    public void SubscribeToEvents(IIntrusionEventHandler eventHandler)
    {
        if (eventHandler == null)
            throw new ArgumentNullException(nameof(eventHandler));

        lock (_eventHandlers)
        {
            if (!_eventHandlers.Contains(eventHandler))
            {
                _eventHandlers.Add(eventHandler);
            }
        }
    }

    public virtual Task<IEnumerable<IntrusionEvent>> GetRecentEventsAsync(int count = 50)
    {
        // Base implementation returns empty list;
        // Derived classes can override to provide actual events;
        return Task.FromResult(Enumerable.Empty<IntrusionEvent>());
    }

    public virtual Task ClearHistoryAsync()
    {
        _detectionCount = 0;
        _lastDetectionTime = default;
        return Task.CompletedTask;
    }

    public virtual async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (!IsArmed)
        {
            _logger.LogWarning("Sensor {SensorId} cannot start monitoring when disarmed", SensorId);
            return;
        }

        if (_isMonitoring)
        {
            _logger.LogDebug("Sensor {SensorId} already monitoring", SensorId);
            return;
        }

        lock (_lockObject)
        {
            if (_isMonitoring)
                return;

            _isMonitoring = true;
            _monitoringCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        _logger.LogInformation("Sensor {SensorId} started monitoring", SensorId);

        // Start monitoring in background;
        _monitoringTask = Task.Run(async () =>
        {
            await MonitorAsync(_monitoringCancellationTokenSource.Token);
        }, cancellationToken);
    }

    public virtual async Task StopMonitoringAsync()
    {
        if (!_isMonitoring)
            return;

        lock (_lockObject)
        {
            if (!_isMonitoring)
                return;

            _isMonitoring = false;
            _monitoringCancellationTokenSource?.Cancel();
        }

        try
        {
            if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            {
                await _monitoringTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping monitoring for sensor {SensorId}", SensorId);
        }

        _logger.LogInformation("Sensor {SensorId} stopped monitoring", SensorId);
    }

    #endregion;

    #region Protected Abstract Methods;

    protected abstract Task<bool> InitializeSensorAsync();
    protected abstract Task<bool> PerformSensorTestAsync();
    protected abstract Task<Dictionary<string, object>> PerformCalibrationAsync();
    protected abstract Task MonitorAsync(CancellationToken cancellationToken);

    #endregion;

    #region Protected Virtual Methods;

    protected virtual TimeSpan GetUptime()
    {
        // Base implementation - can be overridden;
        return TimeSpan.Zero;
    }

    protected virtual SensorHealthStatus GetHealthStatus()
    {
        return Status == SensorStatus.Active ? SensorHealthStatus.Healthy : SensorHealthStatus.Unhealthy;
    }

    protected virtual double? GetBatteryLevel()
    {
        // Base implementation - can be overridden by battery-powered sensors;
        return null;
    }

    protected virtual double? GetSignalStrength()
    {
        // Base implementation - can be overridden by wireless sensors;
        return null;
    }

    protected virtual string GetFirmwareVersion()
    {
        // Base implementation;
        return "1.0.0";
    }

    protected virtual async Task TriggerIntrusionDetectionAsync(string details = null)
    {
        if (!IsArmed)
            return;

        _detectionCount++;
        _lastDetectionTime = DateTime.Now;

        var intrusionEvent = new IntrusionEvent;
        {
            EventId = Guid.NewGuid().ToString(),
            SensorId = SensorId,
            SensorName = SensorName,
            EventType = IntrusionEventType.IntrusionDetected,
            Timestamp = DateTime.Now,
            Location = Location,
            Details = details ?? $"Intrusion detected by {SensorName}",
            Severity = GetIntrusionSeverity()
        };

        await NotifyIntrusionDetectedAsync(intrusionEvent);
    }

    protected virtual async Task TriggerTamperDetectionAsync(string details = null)
    {
        _logger.LogWarning("Tamper detected on sensor {SensorId}", SensorId);

        var tamperEvent = new IntrusionEvent;
        {
            EventId = Guid.NewGuid().ToString(),
            SensorId = SensorId,
            SensorName = SensorName,
            EventType = IntrusionEventType.TamperDetected,
            Timestamp = DateTime.Now,
            Location = Location,
            Details = details ?? $"Tamper detected on {SensorName}",
            Severity = IntrusionSeverity.High;
        };

        await NotifyIntrusionDetectedAsync(tamperEvent);
    }

    protected virtual IntrusionSeverity GetIntrusionSeverity()
    {
        // Adjust severity based on sensitivity;
        return Sensitivity switch;
        {
            >= 9 => IntrusionSeverity.Critical,
            >= 7 => IntrusionSeverity.High,
            >= 5 => IntrusionSeverity.Medium,
            _ => IntrusionSeverity.Low;
        };
    }

    #endregion;

    #region Protected Helper Methods;

    protected async Task NotifyIntrusionDetectedAsync(IntrusionEvent intrusionEvent)
    {
        var tasks = new List<Task>();

        lock (_eventHandlers)
        {
            foreach (var handler in _eventHandlers)
            {
                tasks.Add(handler.OnIntrusionDetectedAsync(intrusionEvent));
            }
        }

        await Task.WhenAll(tasks);
    }

    protected async Task NotifyStatusChangeAsync(SensorStatus oldStatus, SensorStatus newStatus)
    {
        Status = newStatus;

        var tasks = new List<Task>();

        lock (_eventHandlers)
        {
            foreach (var handler in _eventHandlers)
            {
                tasks.Add(handler.OnSensorStatusChangedAsync(SensorId, oldStatus, newStatus));
            }
        }

        await Task.WhenAll(tasks);
    }

    protected async Task NotifyErrorAsync(Exception error)
    {
        var tasks = new List<Task>();

        lock (_eventHandlers)
        {
            foreach (var handler in _eventHandlers)
            {
                tasks.Add(handler.OnSensorErrorAsync(SensorId, error));
            }
        }

        await Task.WhenAll(tasks);
    }

    protected async Task NotifyCalibratedAsync(CalibrationResult result)
    {
        var tasks = new List<Task>();

        lock (_eventHandlers)
        {
            foreach (var handler in _eventHandlers)
            {
                tasks.Add(handler.OnSensorCalibratedAsync(SensorId, result));
            }
        }

        await Task.WhenAll(tasks);
    }

    protected async Task NotifyTestedAsync(SensorTestResult result)
    {
        var tasks = new List<Task>();

        lock (_eventHandlers)
        {
            foreach (var handler in _eventHandlers)
            {
                tasks.Add(handler.OnSensorTestedAsync(SensorId, result));
            }
        }

        await Task.WhenAll(tasks);
    }

    protected async Task TriggerEventAsync(IntrusionEventType eventType, string details)
    {
        var eventObj = new IntrusionEvent;
        {
            EventId = Guid.NewGuid().ToString(),
            SensorId = SensorId,
            SensorName = SensorName,
            EventType = eventType,
            Timestamp = DateTime.Now,
            Location = Location,
            Details = details,
            Severity = IntrusionSeverity.Informational;
        };

        await NotifyIntrusionDetectedAsync(eventObj);
    }

    #endregion;
}

/// <summary>
/// Motion detection sensor;
/// </summary>
public class MotionSensor : BaseIntrusionSensor;
{
    private readonly MotionSensorOptions _options;
    private DateTime _startTime;

    public override SensorType SensorType => SensorType.Motion;

    public MotionSensor(
        string sensorId,
        string sensorName,
        MotionSensorOptions options,
        ILogger<MotionSensor> logger)
        : base(sensorId, sensorName, logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Sensitivity = options.DefaultSensitivity;
        Location = options.DefaultLocation;
    }

    protected override async Task<bool> InitializeSensorAsync()
    {
        _startTime = DateTime.Now;

        // Simulate sensor hardware initialization;
        await Task.Delay(1000);

        // Check if sensor is responding;
        var isResponding = await CheckSensorResponseAsync();

        if (!isResponding)
        {
            _logger.LogError("Motion sensor {SensorId} not responding", SensorId);
            return false;
        }

        return true;
    }

    protected override async Task<bool> PerformSensorTestAsync()
    {
        // Test sensor by simulating motion detection;
        _logger.LogDebug("Testing motion sensor {SensorId}", SensorId);

        // Simulate test sequence;
        await Task.Delay(500);

        // Check various parameters;
        var rangeTest = await TestDetectionRangeAsync();
        var sensitivityTest = await TestSensitivityAsync();

        return rangeTest && sensitivityTest;
    }

    protected override async Task<Dictionary<string, object>> PerformCalibrationAsync()
    {
        _logger.LogDebug("Calibrating motion sensor {SensorId}", SensorId);

        var calibrationData = new Dictionary<string, object>();

        // Calibrate detection range;
        calibrationData["detection_range"] = await CalibrateDetectionRangeAsync();

        // Calibrate sensitivity;
        calibrationData["sensitivity_levels"] = await CalibrateSensitivityLevelsAsync();

        // Calibrate noise filtering;
        calibrationData["noise_threshold"] = await CalibrateNoiseThresholdAsync();

        // Update sensor settings based on calibration;
        await ApplyCalibrationSettingsAsync(calibrationData);

        return calibrationData;
    }

    protected override async Task MonitorAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Motion sensor {SensorId} monitoring started", SensorId);

        var lastMotionTime = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested && _isMonitoring)
        {
            try
            {
                // Check for motion (in real implementation, this would read from hardware)
                var motionDetected = await CheckForMotionAsync();

                if (motionDetected)
                {
                    var timeSinceLastMotion = DateTime.Now - lastMotionTime;

                    // Apply sensitivity-based detection logic;
                    if (timeSinceLastMotion.TotalSeconds > GetDetectionCooldown())
                    {
                        _logger.LogDebug("Motion detected by sensor {SensorId}", SensorId);
                        await TriggerIntrusionDetectionAsync("Motion detected in monitored area");
                        lastMotionTime = DateTime.Now;
                    }
                }

                // Check for tampering;
                var isTampered = await CheckForTamperingAsync();
                if (isTampered)
                {
                    await TriggerTamperDetectionAsync("Motion sensor tampering detected");
                }

                // Sleep based on polling interval;
                await Task.Delay(_options.PollingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in motion sensor monitoring loop");
                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.LogInformation("Motion sensor {SensorId} monitoring stopped", SensorId);
    }

    protected override TimeSpan GetUptime()
    {
        return DateTime.Now - _startTime;
    }

    private async Task<bool> CheckSensorResponseAsync()
    {
        // Simulate hardware check;
        await Task.Delay(100);
        return _random.Next(100) > 10; // 90% success rate;
    }

    private async Task<bool> TestDetectionRangeAsync()
    {
        await Task.Delay(200);
        return _random.Next(100) > 5; // 95% success rate;
    }

    private async Task<bool> TestSensitivityAsync()
    {
        await Task.Delay(200);
        return _random.Next(100) > 5; // 95% success rate;
    }

    private async Task<double> CalibrateDetectionRangeAsync()
    {
        await Task.Delay(300);
        return 10.0 + (_random.NextDouble() * 5.0); // 10-15 meters;
    }

    private async Task<int[]> CalibrateSensitivityLevelsAsync()
    {
        await Task.Delay(300);
        return new[] { 1, 3, 5, 7, 9 };
    }

    private async Task<double> CalibrateNoiseThresholdAsync()
    {
        await Task.Delay(300);
        return 0.1 + (_random.NextDouble() * 0.3); // 0.1-0.4 threshold;
    }

    private async Task ApplyCalibrationSettingsAsync(Dictionary<string, object> calibrationData)
    {
        await Task.Delay(100);
        _logger.LogDebug("Applied calibration settings for sensor {SensorId}", SensorId);
    }

    private async Task<bool> CheckForMotionAsync()
    {
        // Simulate motion detection based on sensitivity;
        await Task.Delay(50);

        // Higher sensitivity = more likely to detect;
        var detectionProbability = Sensitivity * 10; // 10-100%
        return _random.Next(100) < detectionProbability;
    }

    private async Task<bool> CheckForTamperingAsync()
    {
        // Simulate tamper detection (e.g., sensor moved, cover opened)
        await Task.Delay(50);
        return _random.Next(1000) < 2; // 0.2% chance of tamper;
    }

    private double GetDetectionCooldown()
    {
        // Higher sensitivity = shorter cooldown (more sensitive)
        return 5.0 - (Sensitivity * 0.4); // 5 to 1 seconds;
    }
}

public class MotionSensorOptions;
{
    public int DefaultSensitivity { get; set; } = 5;
    public string DefaultLocation { get; set; } = "Unknown";
    public int PollingInterval { get; set; } = 1000; // ms;
    public double DetectionRange { get; set; } = 10.0; // meters;
    public double FieldOfView { get; set; } = 90.0; // degrees;
}

/// <summary>
Door / Window contact sensor;
    /// </summary>
    public class ContactSensor : BaseIntrusionSensor;
{
    private readonly ContactSensorOptions _options;
    private bool _isOpen;
    private DateTime _startTime;

    public override SensorType SensorType => SensorType.Contact;

    public ContactSensor(
        string sensorId,
        string sensorName,
        ContactSensorOptions options,
        ILogger<ContactSensor> logger)
        : base(sensorId, sensorName, logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isOpen = false;
        Sensitivity = options.DefaultSensitivity;
        Location = options.DefaultLocation;
    }

    protected override async Task<bool> InitializeSensorAsync()
    {
        _startTime = DateTime.Now;

        // Simulate sensor initialization;
        await Task.Delay(800);

        // Check initial state;
        _isOpen = await GetContactStateAsync();

        _logger.LogInformation(
            "Contact sensor {SensorId} initialized - Initial state: {State}",
            SensorId, _isOpen ? "Open" : "Closed");

        return true;
    }

    protected override async Task<bool> PerformSensorTestAsync()
    {
        _logger.LogDebug("Testing contact sensor {SensorId}", SensorId);

        // Test open state detection;
        var openTest = await TestOpenStateAsync();

        // Test closed state detection;
        var closedTest = await TestClosedStateAsync();

        // Test tamper detection;
        var tamperTest = await TestTamperDetectionAsync();

        return openTest && closedTest && tamperTest;
    }

    protected override async Task<Dictionary<string, object>> PerformCalibrationAsync()
    {
        _logger.LogDebug("Calibrating contact sensor {SensorId}", SensorId);

        var calibrationData = new Dictionary<string, object>();

        // Calibrate open/close thresholds;
        calibrationData["open_threshold"] = await CalibrateOpenThresholdAsync();
        calibrationData["close_threshold"] = await CalibrateCloseThresholdAsync();

        // Calibrate debounce time;
        calibrationData["debounce_ms"] = await CalibrateDebounceTimeAsync();

        // Update sensor settings;
        await ApplyCalibrationSettingsAsync(calibrationData);

        return calibrationData;
    }

    protected override async Task MonitorAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Contact sensor {SensorId} monitoring started", SensorId);

        var lastStateChangeTime = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested && _isMonitoring)
        {
            try
            {
                var currentState = await GetContactStateAsync();

                if (currentState != _isOpen)
                {
                    var timeSinceLastChange = DateTime.Now - lastStateChangeTime;

                    // Apply debouncing to avoid false triggers;
                    if (timeSinceLastChange.TotalMilliseconds > _options.DebounceTimeMs)
                    {
                        _isOpen = currentState;
                        lastStateChangeTime = DateTime.Now;

                        if (_isOpen)
                        {
                            _logger.LogWarning("Contact sensor {SensorId} detected OPEN", SensorId);
                            await TriggerIntrusionDetectionAsync(
                                $"Door/Window opened at {Location}");
                        }
                        else;
                        {
                            _logger.LogInformation("Contact sensor {SensorId} detected CLOSED", SensorId);
                            await TriggerEventAsync(
                                IntrusionEventType.SensorNormal,
                                "Door/Window closed");
                        }
                    }
                }

                // Check for tampering;
                var isTampered = await CheckForTamperingAsync();
                if (isTampered)
                {
                    await TriggerTamperDetectionAsync("Contact sensor tampering detected");
                }

                // Check battery (if applicable)
                await CheckBatteryLevelAsync();

                await Task.Delay(_options.PollingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in contact sensor monitoring loop");
                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.LogInformation("Contact sensor {SensorId} monitoring stopped", SensorId);
    }

    protected override TimeSpan GetUptime()
    {
        return DateTime.Now - _startTime;
    }

    protected override double? GetBatteryLevel()
    {
        // Simulate battery level for wireless sensors;
        return _options.IsWireless ? 85.0 + (_random.NextDouble() * 10.0) : null;
    }

    private async Task<bool> GetContactStateAsync()
    {
        // Simulate reading from hardware;
        await Task.Delay(50);

        // Simulate state changes;
        if (_random.Next(1000) < 10) // 1% chance of state change;
        {
            return !_isOpen;
        }

        return _isOpen;
    }

    private async Task<bool> TestOpenStateAsync()
    {
        await Task.Delay(200);
        return true; // Simulate successful test;
    }

    private async Task<bool> TestClosedStateAsync()
    {
        await Task.Delay(200);
        return true; // Simulate successful test;
    }

    private async Task<bool> TestTamperDetectionAsync()
    {
        await Task.Delay(200);
        return _random.Next(100) > 5; // 95% success rate;
    }

    private async Task<double> CalibrateOpenThresholdAsync()
    {
        await Task.Delay(300);
        return 0.8 + (_random.NextDouble() * 0.4); // 0.8-1.2 threshold;
    }

    private async Task<double> CalibrateCloseThresholdAsync()
    {
        await Task.Delay(300);
        return 0.1 + (_random.NextDouble() * 0.2); // 0.1-0.3 threshold;
    }

    private async Task<int> CalibrateDebounceTimeAsync()
    {
        await Task.Delay(300);
        return 50 + _random.Next(100); // 50-150 ms;
    }

    private async Task ApplyCalibrationSettingsAsync(Dictionary<string, object> calibrationData)
    {
        await Task.Delay(100);
        _logger.LogDebug("Applied calibration settings for contact sensor {SensorId}", SensorId);
    }

    private async Task<bool> CheckForTamperingAsync()
    {
        await Task.Delay(50);
        return _random.Next(5000) < 1; // 0.02% chance of tamper;
    }

    private async Task CheckBatteryLevelAsync()
    {
        if (_options.IsWireless)
        {
            // Simulate battery check;
            await Task.Delay(50);

            // Low battery warning;
            if (_random.Next(10000) < 1) // 0.01% chance per check;
            {
                _logger.LogWarning("Contact sensor {SensorId} battery low", SensorId);
                await TriggerEventAsync(
                    IntrusionEventType.LowBattery,
                    "Sensor battery low - please replace");
            }
        }
    }
}

public class ContactSensorOptions;
{
    public int DefaultSensitivity { get; set; } = 5;
    public string DefaultLocation { get; set; } = "Unknown";
    public int PollingInterval { get; set; } = 500; // ms;
    public int DebounceTimeMs { get; set; } = 100; // ms;
    public bool IsWireless { get; set; } = true;
    public bool NormallyOpen { get; set; } = true; // Normally open or normally closed contacts;
}

/// <summary>
/// Glass break sensor;
/// </summary>
public class GlassBreakSensor : BaseIntrusionSensor;
{
    private readonly GlassBreakSensorOptions _options;
    private DateTime _startTime;

    public override SensorType SensorType => SensorType.GlassBreak;

    public GlassBreakSensor(
        string sensorId,
        string sensorName,
        GlassBreakSensorOptions options,
        ILogger<GlassBreakSensor> logger)
        : base(sensorId, sensorName, logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Sensitivity = options.DefaultSensitivity;
        Location = options.DefaultLocation;
    }

    protected override async Task<bool> InitializeSensorAsync()
    {
        _startTime = DateTime.Now;

        // Simulate acoustic sensor initialization;
        await Task.Delay(1200);

        // Calibrate to ambient noise level;
        var calibrated = await CalibrateToAmbientNoiseAsync();

        if (!calibrated)
        {
            _logger.LogError("Glass break sensor {SensorId} failed ambient noise calibration", SensorId);
            return false;
        }

        return true;
    }

    protected override async Task<bool> PerformSensorTestAsync()
    {
        _logger.LogDebug("Testing glass break sensor {SensorId}", SensorId);

        // Test frequency detection;
        var freqTest = await TestFrequencyDetectionAsync();

        // Test amplitude detection;
        var ampTest = await TestAmplitudeDetectionAsync();

        // Test pattern recognition;
        var patternTest = await TestPatternRecognitionAsync();

        return freqTest && ampTest && patternTest;
    }

    protected override async Task<Dictionary<string, object>> PerformCalibrationAsync()
    {
        _logger.LogDebug("Calibrating glass break sensor {SensorId}", SensorId);

        var calibrationData = new Dictionary<string, object>();

        // Calibrate frequency thresholds;
        calibrationData["frequency_low"] = await CalibrateLowFrequencyThresholdAsync();
        calibrationData["frequency_high"] = await CalibrateHighFrequencyThresholdAsync();

        // Calibrate amplitude thresholds;
        calibrationData["amplitude_min"] = await CalibrateMinAmplitudeAsync();
        calibrationData["amplitude_max"] = await CalibrateMaxAmplitudeAsync();

        // Calibrate pattern recognition;
        calibrationData["pattern_templates"] = await CalibratePatternTemplatesAsync();

        // Update sensor settings;
        await ApplyCalibrationSettingsAsync(calibrationData);

        return calibrationData;
    }

    protected override async Task MonitorAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Glass break sensor {SensorId} monitoring started", SensorId);

        var lastDetectionTime = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested && _isMonitoring)
        {
            try
            {
                // Monitor for glass break sounds;
                var glassBreakDetected = await CheckForGlassBreakAsync();

                if (glassBreakDetected)
                {
                    var timeSinceLastDetection = DateTime.Now - lastDetectionTime;

                    // Apply cooldown to avoid multiple triggers for same event;
                    if (timeSinceLastDetection.TotalSeconds > GetDetectionCooldown())
                    {
                        _logger.LogWarning("Glass break detected by sensor {SensorId}", SensorId);
                        await TriggerIntrusionDetectionAsync(
                            $"Glass break detected at {Location}");
                        lastDetectionTime = DateTime.Now;
                    }
                }

                // Check for tampering;
                var isTampered = await CheckForTamperingAsync();
                if (isTampered)
                {
                    await TriggerTamperDetectionAsync("Glass break sensor tampering detected");
                }

                // Update ambient noise calibration periodically;
                if (DateTime.Now.Minute % 15 == 0) // Every 15 minutes;
                {
                    await UpdateAmbientNoiseCalibrationAsync();
                }

                await Task.Delay(_options.PollingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in glass break sensor monitoring loop");
                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.LogInformation("Glass break sensor {SensorId} monitoring stopped", SensorId);
    }

    protected override TimeSpan GetUptime()
    {
        return DateTime.Now - _startTime;
    }

    protected override IntrusionSeverity GetIntrusionSeverity()
    {
        // Glass break is always high severity;
        return IntrusionSeverity.High;
    }

    private async Task<bool> CalibrateToAmbientNoiseAsync()
    {
        _logger.LogDebug("Calibrating glass break sensor to ambient noise");

        // Simulate listening to ambient noise for 3 seconds;
        await Task.Delay(3000);

        // Analyze ambient noise;
        var ambientLevel = await AnalyzeAmbientNoiseAsync();

        // Set threshold above ambient level;
        await SetDetectionThresholdAsync(ambientLevel * 1.5);

        return true;
    }

    private async Task<bool> TestFrequencyDetectionAsync()
    {
        await Task.Delay(300);

        // Simulate testing different frequencies;
        var lowFreqTest = await TestFrequencyRangeAsync(1000, 3000); // Low frequency range;
        var highFreqTest = await TestFrequencyRangeAsync(3000, 6000); // High frequency range;

        return lowFreqTest && highFreqTest;
    }

    private async Task<bool> TestAmplitudeDetectionAsync()
    {
        await Task.Delay(300);
        return _random.Next(100) > 2; // 98% success rate;
    }

    private async Task<bool> TestPatternRecognitionAsync()
    {
        await Task.Delay(400);
        return _random.Next(100) > 5; // 95% success rate;
    }

    private async Task<double> CalibrateLowFrequencyThresholdAsync()
    {
        await Task.Delay(300);
        return 1000.0 + (_random.NextDouble() * 500.0); // 1000-1500 Hz;
    }

    private async Task<double> CalibrateHighFrequencyThresholdAsync()
    {
        await Task.Delay(300);
        return 4000.0 + (_random.NextDouble() * 2000.0); // 4000-6000 Hz;
    }

    private async Task<double> CalibrateMinAmplitudeAsync()
    {
        await Task.Delay(300);
        return 0.5 + (_random.NextDouble() * 0.5); // 0.5-1.0;
    }

    private async Task<double> CalibrateMaxAmplitudeAsync()
    {
        await Task.Delay(300);
        return 2.0 + (_random.NextDouble() * 3.0); // 2.0-5.0;
    }

    private async Task<string[]> CalibratePatternTemplatesAsync()
    {
        await Task.Delay(500);
        return new[] { "sharp_impact", "cracking", "shattering" };
    }

    private async Task ApplyCalibrationSettingsAsync(Dictionary<string, object> calibrationData)
    {
        await Task.Delay(100);
        _logger.LogDebug("Applied calibration settings for glass break sensor {SensorId}", SensorId);
    }

    private async Task<bool> CheckForGlassBreakAsync()
    {
        await Task.Delay(100);

        // Higher sensitivity = more likely to detect;
        var detectionProbability = Sensitivity * 8; // 8-80%

        // Also check for specific frequency patterns;
        var hasGlassBreakPattern = await CheckForGlassBreakPatternAsync();

        return hasGlassBreakPattern && (_random.Next(100) < detectionProbability);
    }

    private async Task<bool> CheckForGlassBreakPatternAsync()
    {
        // Simulate pattern recognition;
        await Task.Delay(50);
        return _random.Next(100) < 70; // 70% chance of matching glass break pattern;
    }

    private async Task<bool> CheckForTamperingAsync()
    {
        await Task.Delay(50);
        return _random.Next(10000) < 1; // 0.01% chance of tamper;
    }

    private async Task<double> AnalyzeAmbientNoiseAsync()
    {
        await Task.Delay(100);
        return 0.2 + (_random.NextDouble() * 0.3); // 0.2-0.5 ambient level;
    }

    private async Task SetDetectionThresholdAsync(double threshold)
    {
        await Task.Delay(50);
        _logger.LogDebug("Glass break detection threshold set to {Threshold:F2}", threshold);
    }

    private async Task UpdateAmbientNoiseCalibrationAsync()
    {
        await Task.Delay(200);
        _logger.LogDebug("Updated ambient noise calibration for sensor {SensorId}", SensorId);
    }

    private async Task<bool> TestFrequencyRangeAsync(double minFreq, double maxFreq)
    {
        await Task.Delay(100);
        return _random.Next(100) > 3; // 97% success rate;
    }

    private double GetDetectionCooldown()
    {
        return 10.0 - (Sensitivity * 0.8); // 10 to 2 seconds;
    }
}

public class GlassBreakSensorOptions;
{
    public int DefaultSensitivity { get; set; } = 5;
    public string DefaultLocation { get; set; } = "Unknown";
    public int PollingInterval { get; set; } = 100; // ms (needs faster polling for audio)
    public double DetectionRange { get; set; } = 7.5; // meters;
    public double FrequencyRangeLow { get; set; } = 1000.0; // Hz;
    public double FrequencyRangeHigh { get; set; } = 6000.0; // Hz;
}

#endregion;

#region Models and Enums;

/// <summary>
/// Intrusion event;
/// </summary>
public class IntrusionEvent;
{
    public string EventId { get; set; }
    public string SensorId { get; set; }
    public string SensorName { get; set; }
    public IntrusionEventType EventType { get; set; }
    public DateTime Timestamp { get; set; }
    public string Location { get; set; }
    public string Details { get; set; }
    public IntrusionSeverity Severity { get; set; }
    public TimeSpan? ResponseTime { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string ResolvedBy { get; set; }
    public string ResolutionNotes { get; set; }
    public string ParentEventId { get; set; }
    public List<string> TriggeredRules { get; set; }
    public Dictionary<string, object> Metadata { get; set; }

    public IntrusionEvent()
    {
        TriggeredRules = new List<string>();
        Metadata = new Dictionary<string, object>();
        Severity = IntrusionSeverity.Medium;
    }
}

/// <summary>
/// Sensor information;
/// </summary>
public class SensorInfo;
{
    public string SensorId { get; set; }
    public string SensorName { get; set; }
    public SensorType SensorType { get; set; }
    public string Location { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime LastStatusChange { get; set; }
    public SensorStatus LastStatus { get; set; }
    public DateTime? LastEventTime { get; set; }
    public IntrusionEventType? LastEventType { get; set; }
    public int TotalEvents { get; set; }
    public bool IsArmed { get; set; }
}

/// <summary>
/// Sensor diagnostics;
/// </summary>
public class SensorDiagnostics;
{
    public string SensorId { get; set; }
    public DateTime Timestamp { get; set; }
    public SensorStatus Status { get; set; }
    public bool IsArmed { get; set; }
    public int Sensitivity { get; set; }
    public string Location { get; set; }
    public TimeSpan Uptime { get; set; }
    public int DetectionCount { get; set; }
    public DateTime LastDetectionTime { get; set; }
    public bool IsMonitoring { get; set; }
    public SensorHealthStatus HealthStatus { get; set; }
    public double? BatteryLevel { get; set; } // Percentage;
    public double? SignalStrength { get; set; } // dBm or percentage;
    public string FirmwareVersion { get; set; }
    public Dictionary<string, object> AdditionalMetrics { get; set; }

    public SensorDiagnostics()
    {
        AdditionalMetrics = new Dictionary<string, object>();
    }
}

/// <summary>
/// Sensor test result;
/// </summary>
public class SensorTestResult;
{
    public string SensorId { get; set; }
    public string TestId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public SensorTestStatus Status { get; set; }
    public string Message { get; set; }
    public Dictionary<string, object> TestData { get; set; }

    public SensorTestResult()
    {
        TestData = new Dictionary<string, object>();
    }
}

/// <summary>
/// Calibration result;
/// </summary>
public class CalibrationResult;
{
    public string SensorId { get; set; }
    public string CalibrationId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public CalibrationStatus Status { get; set; }
    public string Message { get; set; }
    public Dictionary<string, object> CalibrationData { get; set; }

    public CalibrationResult()
    {
        CalibrationData = new Dictionary<string, object>();
    }
}

/// <summary>
/// System status;
/// </summary>
public class SystemStatus;
{
    public string SystemId { get; set; }
    public string SystemName { get; set; }
    public bool IsRunning { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Uptime { get; set; }
    public int TotalSensors { get; set; }
    public int ArmedSensors { get; set; }
    public int ActiveAlarms { get; set; }
    public DateTime? LastEventTime { get; set; }
    public double MemoryUsage { get; set; } // bytes;
    public double CpuUsage { get; set; } // percentage;
    public Dictionary<string, int> SensorStatusBreakdown { get; set; }

    public SystemStatus()
    {
        SensorStatusBreakdown = new Dictionary<string, int>();
    }
}

/// <summary>
/// Intrusion statistics;
/// </summary>
public class IntrusionStatistics;
{
    public int TotalEvents { get; set; }
    public int IntrusionEvents { get; set; }
    public int TamperEvents { get; set; }
    public int ErrorEvents { get; set; }
    public int TestEvents { get; set; }
    public int CalibrationEvents { get; set; }
    public Dictionary<string, int> EventsBySeverity { get; set; }
    public Dictionary<string, int> EventsBySensor { get; set; }
    public Dictionary<int, int> EventsByHour { get; set; }
    public double AverageResponseTime { get; set; } // seconds;
    public DateTime GeneratedAt { get; set; }

    public IntrusionStatistics()
    {
        EventsBySeverity = new Dictionary<string, int>();
        EventsBySensor = new Dictionary<string, int>();
        EventsByHour = new Dictionary<int, int>();
    }
}

/// <summary>
/// Detection rule;
/// </summary>
public class DetectionRule;
{
    public string RuleId { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Condition { get; set; }
    public string Action { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastTriggered { get; set; }
    public int TriggerCount { get; set; }
}

/// <summary>
/// Pattern analysis result;
/// </summary>
public class PatternAnalysisResult;
{
    public int TotalEventsAnalyzed { get; set; }
    public DateTime AnalysisTime { get; set; }
    public double RiskScore { get; set; } // 0-100;
    public List<DetectedPattern> Patterns { get; set; }
    public Dictionary<string, double> RiskFactors { get; set; }

    public PatternAnalysisResult()
    {
        Patterns = new List<DetectedPattern>();
        RiskFactors = new Dictionary<string, double>();
    }
}

/// <summary>
/// Detected pattern;
/// </summary>
public class DetectedPattern;
{
    public PatternType PatternType { get; set; }
    public string Description { get; set; }
    public double Confidence { get; set; } // 0-1;
    public string RecommendedAction { get; set; }
    public Dictionary<string, object> PatternData { get; set; }

    public DetectedPattern()
    {
        PatternData = new Dictionary<string, object>();
    }
}

/// <summary>
/// System test result;
/// </summary>
public class SystemTestResult;
{
    public string TestId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public SystemTestStatus OverallStatus { get; set; }
    public string ErrorMessage { get; set; }
    public List<SensorTestResult> SensorsTested { get; set; }
    public List<string> FailedSensors { get; set; }

    public SystemTestResult()
    {
        SensorsTested = new List<SensorTestResult>();
        FailedSensors = new List<string>();
    }
}

/// <summary>
/// Intrusion detection options;
/// </summary>
public class IntrusionDetectionOptions;
{
    public string SystemId { get; set; } = Guid.NewGuid().ToString();
    public string SystemName { get; set; } = "Intrusion Detection System";
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PatternAnalysisInterval { get; set; } = TimeSpan.FromHours(1);
    public int MaxEventHistory { get; set; } = 10000;
    public int MinIntrusionsForAlarm { get; set; } = 2;
    public int AlarmActivationThresholdMinutes { get; set; } = 5;
    public bool NotifyAuthoritiesOnCritical { get; set; } = false;
    public string AuthoritiesContact { get; set; }
    public string SecurityTeamContact { get; set; }
    public TimeSpan EventRetentionPeriod { get; set; } = TimeSpan.FromDays(90);
}

#endregion;

#region Enums;

/// <summary>
/// Sensor types;
/// </summary>
public enum SensorType;
{
    Motion = 0,
    Contact = 1,
    GlassBreak = 2,
    Vibration = 3,
    Acoustic = 4,
    Thermal = 5,
    Camera = 6,
    Pressure = 7,
    Proximity = 8,
    Other = 99;
}

/// <summary>
/// Sensor status;
/// </summary>
public enum SensorStatus;
{
    Inactive = 0,
    Initializing = 1,
    Active = 2,
    Armed = 3,
    Monitoring = 4,
    Calibrating = 5,
    Error = 6,
    Tampered = 7,
    Offline = 8,
    Maintenance = 9;
}

/// <summary>
/// Sensor health status;
/// </summary>
public enum SensorHealthStatus;
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Unhealthy = 3,
    Critical = 4;
}

/// <summary>
/// Sensor test status;
/// </summary>
public enum SensorTestStatus;
{
    NotTested = 0,
    Passed = 1,
    Failed = 2,
    Error = 3;
}

/// <summary>
/// Calibration status;
/// </summary>
public enum CalibrationStatus;
{
    NotCalibrated = 0,
    InProgress = 1,
    Completed = 2,
    Failed = 3;
}

/// <summary>
/// System test status;
/// </summary>
public enum SystemTestStatus;
{
    NotTested = 0,
    Passed = 1,
    Failed = 2,
    Partial = 3;
}

/// <summary>
/// Intrusion event types;
/// </summary>
public enum IntrusionEventType;
{
    IntrusionDetected = 0,
    TamperDetected = 1,
    SensorError = 2,
    SensorTest = 3,
    SensorCalibration = 4,
    SensorArmed = 5,
    SensorDisarmed = 6,
    SensorNormal = 7,
    LowBattery = 8,
    StatusChange = 9,
    AlarmActivated = 10,
    SecurityNotified = 11,
    AuthoritiesNotified = 12,
    AreaLockdown = 13,
    EvidenceRecorded = 14,
    SystemEvent = 15;
}

/// <summary>
/// Intrusion severity levels;
/// </summary>
public enum IntrusionSeverity;
{
    Informational = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4;
}

/// <summary>
/// Pattern types;
/// </summary>
public enum PatternType;
{
    Temporal = 0,
    Spatial = 1,
    Correlation = 2,
    Behavioral = 3,
    Sequential = 4;
}

#endregion;

#region Service Registration;

/// <summary>
/// Service collection extensions for Intrusion Detection;
/// </summary>
public static class IntrusionDetectionServiceCollectionExtensions;
{
    public static IServiceCollection AddIntrusionDetectionSystem(
        this IServiceCollection services,
        Action<IntrusionDetectionOptions> configureOptions = null)
    {
        var options = new IntrusionDetectionOptions();
        configureOptions?.Invoke(options);

        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IIntrusionDetectionSystem, IntrusionDetectionSystem>();

        // Register common sensor types;
        services.AddTransient<MotionSensor>();
        services.AddTransient<ContactSensor>();
        services.AddTransient<GlassBreakSensor>();

        return services;
    }

    public static IServiceCollection AddMotionSensor(
        this IServiceCollection services,
        string sensorId,
        string sensorName,
        Action<MotionSensorOptions> configureOptions)
    {
        var options = new MotionSensorOptions();
        configureOptions.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IIntrusionSensor>(sp =>
            new MotionSensor(
                sensorId,
                sensorName,
                options,
                sp.GetRequiredService<ILogger<MotionSensor>>()));

        return services;
    }

    public static IServiceCollection AddContactSensor(
        this IServiceCollection services,
        string sensorId,
        string sensorName,
        Action<ContactSensorOptions> configureOptions)
    {
        var options = new ContactSensorOptions();
        configureOptions.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IIntrusionSensor>(sp =>
            new ContactSensor(
                sensorId,
                sensorName,
                options,
                sp.GetRequiredService<ILogger<ContactSensor>>()));

        return services;
    }
}

#endregion;

#region Usage Examples;

/// <summary>
/// Example usage of Intrusion Detection System;
/// </summary>
public static class IntrusionDetectionExamples;
{
    public static async Task ExampleUsage()
    {
        // Create logger;
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
        });

        var logger = loggerFactory.CreateLogger<IntrusionDetectionSystem>();

        // Create intrusion detection system;
        var options = new IntrusionDetectionOptions;
        {
            SystemName = "Office Building Security",
            HealthCheckInterval = TimeSpan.FromMinutes(2),
            MinIntrusionsForAlarm = 2,
            AlarmActivationThresholdMinutes = 3;
        };

        var intrusionSystem = new IntrusionDetectionSystem(
            logger,
            Options.Create(options));

        // Create event handler for logging;
        var eventHandler = new LoggingIntrusionEventHandler(
            loggerFactory.CreateLogger<LoggingIntrusionEventHandler>());

        intrusionSystem.SubscribeToSystemEvents(eventHandler);

        // Start the system;
        await intrusionSystem.StartSystemAsync();

        try
        {
            // Create and register sensors;
            var motionSensorOptions = new MotionSensorOptions;
            {
                DefaultLocation = "Main Entrance",
                DefaultSensitivity = 7,
                PollingInterval = 1000;
            };

            var motionSensor = new MotionSensor(
                "MOTION-001",
                "Main Entrance Motion Sensor",
                motionSensorOptions,
                loggerFactory.CreateLogger<MotionSensor>());

            await intrusionSystem.RegisterSensorAsync(motionSensor);

            var contactSensorOptions = new ContactSensorOptions;
            {
                DefaultLocation = "Front Door",
                DefaultSensitivity = 5,
                PollingInterval = 500,
                IsWireless = true;
            };

            var contactSensor = new ContactSensor(
                "CONTACT-001",
                "Front Door Contact Sensor",
                contactSensorOptions,
                loggerFactory.CreateLogger<ContactSensor>());

            await intrusionSystem.RegisterSensorAsync(contactSensor);

            // Arm all sensors;
            await intrusionSystem.ArmAllSensorsAsync();

            // Get system status;
            var status = await intrusionSystem.GetSystemStatusAsync();
            Console.WriteLine($"System Status: Running={status.IsRunning}, Sensors={status.TotalSensors}, Armed={status.ArmedSensors}");

            // Perform system test;
            var testResult = await intrusionSystem.PerformSystemTestAsync();
            Console.WriteLine($"System Test: {testResult.OverallStatus}");

            // Simulate some activity;
            await Task.Delay(TimeSpan.FromMinutes(2));

            // Get statistics;
            var stats = await intrusionSystem.GetStatisticsAsync(
                DateTime.Now.AddHours(-1),
                DateTime.Now);

            Console.WriteLine($"Events in last hour: {stats.TotalEvents}");
            Console.WriteLine($"Intrusions: {stats.IntrusionEvents}");

            // Analyze patterns;
            var patterns = await intrusionSystem.AnalyzePatternsAsync(
                DateTime.Now.AddDays(-1),
                DateTime.Now);

            Console.WriteLine($"Pattern analysis found {patterns.Patterns.Count} patterns");
            Console.WriteLine($"Risk score: {patterns.RiskScore:F1}");

            // Wait for monitoring;
            await Task.Delay(TimeSpan.FromMinutes(5));

            // Disarm sensors;
            await intrusionSystem.DisarmAllSensorsAsync();
        }
        finally
        {
            // Stop the system;
            await intrusionSystem.StopSystemAsync();
            intrusionSystem.Dispose();
        }
    }

    /// <summary>
    /// Example event handler;
    /// </summary>
    public class LoggingIntrusionEventHandler : IIntrusionEventHandler;
    {
        private readonly ILogger<LoggingIntrusionEventHandler> _logger;

        public LoggingIntrusionEventHandler(ILogger<LoggingIntrusionEventHandler> logger)
        {
            _logger = logger;
        }

        public Task OnIntrusionDetectedAsync(IntrusionEvent intrusionEvent)
        {
            _logger.LogWarning(
                "INTRUSION: {EventType} at {Location} (Severity: {Severity}) - {Details}",
                intrusionEvent.EventType,
                intrusionEvent.Location,
                intrusionEvent.Severity,
                intrusionEvent.Details);

            return Task.CompletedTask;
        }

        public Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus)
        {
            _logger.LogInformation(
                "Sensor {SensorId} status changed: {OldStatus} -> {NewStatus}",
                sensorId, oldStatus, newStatus);

            return Task.CompletedTask;
        }

        public Task OnSensorErrorAsync(string sensorId, Exception error)
        {
            _logger.LogError(
                error,
                "Sensor {SensorId} error",
                sensorId);

            return Task.CompletedTask;
        }

        public Task OnSensorCalibratedAsync(string sensorId, CalibrationResult result)
        {
            _logger.LogInformation(
                "Sensor {SensorId} calibration {Status}: {Message}",
                sensorId, result.Status, result.Message);

            return Task.CompletedTask;
        }

        public Task OnSensorTestedAsync(string sensorId, SensorTestResult result)
        {
            _logger.LogInformation(
                "Sensor {SensorId} test {Status}: {Message}",
                sensorId, result.Status, result.Message);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Example security response system;
    /// </summary>
    public class SecurityResponseSystem : IIntrusionEventHandler;
    {
        private readonly ILogger<SecurityResponseSystem> _logger;
        private readonly INotificationService _notificationService;
        private readonly IAlarmSystem _alarmSystem;

        public SecurityResponseSystem(
            ILogger<SecurityResponseSystem> logger,
            INotificationService notificationService,
            IAlarmSystem alarmSystem)
        {
            _logger = logger;
            _notificationService = notificationService;
            _alarmSystem = alarmSystem;
        }

        public async Task OnIntrusionDetectedAsync(IntrusionEvent intrusionEvent)
        {
            switch (intrusionEvent.Severity)
            {
                case IntrusionSeverity.Critical:
                case IntrusionSeverity.High:
                    await HandleHighSeverityIntrusion(intrusionEvent);
                    break;

                case IntrusionSeverity.Medium:
                    await HandleMediumSeverityIntrusion(intrusionEvent);
                    break;

                case IntrusionSeverity.Low:
                    await HandleLowSeverityIntrusion(intrusionEvent);
                    break;
            }
        }

        private async Task HandleHighSeverityIntrusion(IntrusionEvent intrusionEvent)
        {
            // Activate alarms;
            await _alarmSystem.ActivateAlarmAsync(intrusionEvent.Location);

            // Notify security team;
            await _notificationService.SendCriticalAlertAsync(
                "Security Team",
                $"CRITICAL INTRUSION at {intrusionEvent.Location}",
                $"Type: {intrusionEvent.EventType}\nDetails: {intrusionEvent.Details}");

            // Notify authorities if needed;
            if (intrusionEvent.Severity == IntrusionSeverity.Critical)
            {
                await _notificationService.NotifyAuthoritiesAsync(
                    intrusionEvent.Location,
                    intrusionEvent.Details);
            }

            _logger.LogCritical(
                "High severity intrusion handled: {EventId} at {Location}",
                intrusionEvent.EventId,
                intrusionEvent.Location);
        }

        private async Task HandleMediumSeverityIntrusion(IntrusionEvent intrusionEvent)
        {
            // Notify security team;
            await _notificationService.SendAlertAsync(
                "Security Team",
                $"INTRUSION ALERT at {intrusionEvent.Location}",
                $"Type: {intrusionEvent.EventType}\nDetails: {intrusionEvent.Details}");

            _logger.LogWarning(
                "Medium severity intrusion handled: {EventId} at {Location}",
                intrusionEvent.EventId,
                intrusionEvent.Location);
        }

        private async Task HandleLowSeverityIntrusion(IntrusionEvent intrusionEvent)
        {
            // Log for review;
            _logger.LogInformation(
                "Low severity intrusion logged: {EventId} at {Location}",
                intrusionEvent.EventId,
                intrusionEvent.Location);

            await Task.CompletedTask;
        }

        public Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus)
        {
            // Log status changes;
            _logger.LogInformation(
                "Sensor {SensorId} status: {OldStatus} -> {NewStatus}",
                sensorId, oldStatus, newStatus);

            return Task.CompletedTask;
        }

        public Task OnSensorErrorAsync(string sensorId, Exception error)
        {
            // Notify maintenance team;
            _logger.LogError(
                error,
                "Sensor {SensorId} requires maintenance",
                sensorId);

            return Task.CompletedTask;
        }

        public Task OnSensorCalibratedAsync(string sensorId, CalibrationResult result)
        {
            _logger.LogInformation(
                "Sensor {SensorId} calibration completed: {Status}",
                sensorId, result.Status);

            return Task.CompletedTask;
        }

        public Task OnSensorTestedAsync(string sensorId, SensorTestResult result)
        {
            _logger.LogInformation(
                "Sensor {SensorId} test completed: {Status}",
                sensorId, result.Status);

            return Task.CompletedTask;
        }
    }

    // Example interfaces for dependencies;
    public interface INotificationService;
    {
        Task SendCriticalAlertAsync(string recipient, string subject, string message);
        Task SendAlertAsync(string recipient, string subject, string message);
        Task NotifyAuthoritiesAsync(string location, string details);
    }

    public interface IAlarmSystem;
    {
        Task ActivateAlarmAsync(string location);
        Task DeactivateAlarmAsync(string location);
        Task<bool> IsAlarmActiveAsync(string location);
    }
}

    #endregion;
}
