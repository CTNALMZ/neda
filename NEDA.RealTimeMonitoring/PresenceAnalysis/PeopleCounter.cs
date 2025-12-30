using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YourNamespace.Monitoring.PeopleCounting;
{
    #region Interfaces;

    /// <summary>
    /// Manages people counting and occupancy monitoring;
    /// </summary>
    public interface IPeopleCounter;
    {
        /// <summary>
        /// Registers a person entering a zone;
        /// </summary>
        Task<CountResult> PersonEnteredAsync(string zoneId, PersonInfo personInfo = null);

        /// <summary>
        /// Registers a person leaving a zone;
        /// </summary>
        Task<CountResult> PersonExitedAsync(string zoneId, PersonInfo personInfo = null);

        /// <summary>
        /// Gets current count for a zone;
        /// </summary>
        Task<ZoneCount> GetZoneCountAsync(string zoneId);

        /// <summary>
        /// Gets counts for all zones;
        /// </summary>
        Task<IEnumerable<ZoneCount>> GetAllZoneCountsAsync();

        /// <summary>
        /// Gets occupancy statistics;
        /// </summary>
        Task<OccupancyStatistics> GetOccupancyStatisticsAsync(string zoneId = null);

        /// <summary>
        /// Gets people movement history;
        /// </summary>
        Task<IEnumerable<MovementRecord>> GetMovementHistoryAsync(
            string zoneId = null,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            int limit = 1000);

        /// <summary>
        /// Gets real-time people density;
        /// </summary>
        Task<PeopleDensity> GetPeopleDensityAsync(string zoneId);

        /// <summary>
        /// Gets peak hours analysis;
        /// </summary>
        Task<PeakHoursAnalysis> GetPeakHoursAnalysisAsync(string zoneId, int days = 7);

        /// <summary>
        /// Configures a counting zone;
        /// </summary>
        Task<bool> ConfigureZoneAsync(CountingZone zone);

        /// <summary>
        /// Removes a counting zone;
        /// </summary>
        Task<bool> RemoveZoneAsync(string zoneId);

        /// <summary>
        /// Performs automatic calibration;
        /// </summary>
        Task<CalibrationResult> CalibrateAsync(string zoneId);

        /// <summary>
        /// Gets system diagnostics;
        /// </summary>
        Task<CounterDiagnostics> GetDiagnosticsAsync();

        /// <summary>
        /// Resets counts for a zone;
        /// </summary>
        Task<bool> ResetCountsAsync(string zoneId);

        /// <summary>
        /// Starts continuous monitoring;
        /// </summary>
        Task StartMonitoringAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops continuous monitoring;
        /// </summary>
        Task StopMonitoringAsync();

        /// <summary>
        /// Subscribes to counting events;
        /// </summary>
        void SubscribeToEvents(IPeopleCountEventHandler eventHandler);
    }

    /// <summary>
    /// Interface for counting sensors;
    /// </summary>
    public interface ICountingSensor;
    {
        string SensorId { get; }
        string SensorName { get; }
        SensorType SensorType { get; }
        string ZoneId { get; }
        SensorStatus Status { get; }
        int DetectionRange { get; }
        double Accuracy { get; }

        Task<bool> InitializeAsync();
        Task<SensorReading> GetReadingAsync();
        Task<CalibrationResult> CalibrateAsync();
        Task<SensorDiagnostics> GetDiagnosticsAsync();
        Task StartMonitoringAsync(CancellationToken cancellationToken);
        Task StopMonitoringAsync();
    }

    /// <summary>
    /// Interface for people count event handlers;
    /// </summary>
    public interface IPeopleCountEventHandler;
    {
        Task OnPersonEnteredAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount);
        Task OnPersonExitedAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount);
        Task OnZoneCapacityExceededAsync(string zoneId, ZoneCount currentCount, int capacityLimit);
        Task OnZoneOccupancyChangedAsync(string zoneId, ZoneCount oldCount, ZoneCount newCount);
        Task OnCountingErrorAsync(string zoneId, Exception error);
        Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus);
    }

    /// <summary>
    /// Interface for people detection algorithms;
    /// </summary>
    public interface IPeopleDetectionAlgorithm;
    {
        string AlgorithmId { get; }
        string AlgorithmName { get; }
        DetectionMethod DetectionMethod { get; }
        double ConfidenceThreshold { get; set; }

        Task<DetectionResult> DetectPeopleAsync(byte[] sensorData);
        Task<CalibrationResult> CalibrateAsync(byte[] calibrationData);
        Task<AlgorithmMetrics> GetMetricsAsync();
    }

    #endregion;

    #region Main Implementation;

    /// <summary>
    /// People counter implementation;
    /// </summary>
    public class PeopleCounter : IPeopleCounter, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger<PeopleCounter> _logger;
        private readonly PeopleCounterOptions _options;
        private readonly ConcurrentDictionary<string, CountingZone> _zones;
        private readonly ConcurrentDictionary<string, ZoneCount> _zoneCounts;
        private readonly ConcurrentDictionary<string, ICountingSensor> _sensors;
        private readonly ConcurrentDictionary<string, IPeopleDetectionAlgorithm> _algorithms;
        private readonly ConcurrentQueue<MovementRecord> _movementHistory;
        private readonly ConcurrentBag<IPeopleCountEventHandler> _eventHandlers;
        private readonly System.Timers.Timer _healthCheckTimer;
        private readonly System.Timers.Timer _analyticsTimer;
        private readonly System.Timers.Timer _cleanupTimer;
        private readonly object _lockObject = new object();
        private readonly CancellationTokenSource _globalCancellationTokenSource;
        private Task _monitoringTask;
        private bool _isRunning;
        private bool _isDisposed;
        private DateTime _startTime;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of PeopleCounter;
        /// </summary>
        public PeopleCounter(
            ILogger<PeopleCounter> logger,
            IOptions<PeopleCounterOptions> options = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? new PeopleCounterOptions();

            _zones = new ConcurrentDictionary<string, CountingZone>();
            _zoneCounts = new ConcurrentDictionary<string, ZoneCount>();
            _sensors = new ConcurrentDictionary<string, ICountingSensor>();
            _algorithms = new ConcurrentDictionary<string, IPeopleDetectionAlgorithm>();
            _movementHistory = new ConcurrentQueue<MovementRecord>();
            _eventHandlers = new ConcurrentBag<IPeopleCountEventHandler>();
            _globalCancellationTokenSource = new CancellationTokenSource();

            _healthCheckTimer = new System.Timers.Timer;
            {
                Interval = _options.HealthCheckInterval.TotalMilliseconds,
                AutoReset = true;
            };
            _healthCheckTimer.Elapsed += OnHealthCheckTimerElapsed;

            _analyticsTimer = new System.Timers.Timer;
            {
                Interval = _options.AnalyticsUpdateInterval.TotalMilliseconds,
                AutoReset = true;
            };
            _analyticsTimer.Elapsed += OnAnalyticsTimerElapsed;

            _cleanupTimer = new System.Timers.Timer;
            {
                Interval = _options.HistoryCleanupInterval.TotalMilliseconds,
                AutoReset = true;
            };
            _cleanupTimer.Elapsed += OnCleanupTimerElapsed;

            InitializeDefaultAlgorithms();
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Registers a person entering a zone;
        /// </summary>
        public async Task<CountResult> PersonEnteredAsync(string zoneId, PersonInfo personInfo = null)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                throw new ArgumentException("Zone ID is required", nameof(zoneId));

            if (!_zones.ContainsKey(zoneId))
                throw new InvalidOperationException($"Zone '{zoneId}' is not configured");

            var zone = _zones[zoneId];

            // Validate zone is active;
            if (!zone.IsActive)
            {
                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = $"Zone '{zoneId}' is not active",
                    Timestamp = DateTime.Now;
                };
            }

            // Get current count;
            var currentCount = await GetZoneCountAsync(zoneId);
            var oldCount = new ZoneCount;
            {
                ZoneId = currentCount.ZoneId,
                ZoneName = currentCount.ZoneName,
                CurrentCount = currentCount.CurrentCount,
                LastUpdated = currentCount.LastUpdated;
            };

            // Apply counting rules;
            var validationResult = await ValidateEntryAsync(zone, currentCount, personInfo);
            if (!validationResult.IsValid)
            {
                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = validationResult.ErrorMessage,
                    Timestamp = DateTime.Now,
                    CurrentCount = currentCount;
                };
            }

            try
            {
                // Update count;
                currentCount.CurrentCount++;
                currentCount.EntriesCount++;
                currentCount.LastUpdated = DateTime.Now;
                currentCount.LastEntryTime = DateTime.Now;

                if (_zoneCounts.TryUpdate(zoneId, currentCount, currentCount))
                {
                    // Create movement record;
                    var movementRecord = new MovementRecord;
                    {
                        RecordId = Guid.NewGuid().ToString(),
                        ZoneId = zoneId,
                        ZoneName = zone.ZoneName,
                        Direction = MovementDirection.Enter,
                        Timestamp = DateTime.Now,
                        PersonInfo = personInfo,
                        PreviousCount = oldCount.CurrentCount,
                        NewCount = currentCount.CurrentCount,
                        IsManual = personInfo?.IsManualEntry ?? false;
                    };

                    // Apply algorithm-based validation if available;
                    if (personInfo?.DetectionAlgorithmId != null &&
                        _algorithms.TryGetValue(personInfo.DetectionAlgorithmId, out var algorithm))
                    {
                        movementRecord.DetectionConfidence = personInfo.DetectionConfidence;
                        movementRecord.AlgorithmId = algorithm.AlgorithmId;
                    }

                    _movementHistory.Enqueue(movementRecord);

                    // Limit history size;
                    while (_movementHistory.Count > _options.MaxHistorySize)
                    {
                        _movementHistory.TryDequeue(out _);
                    }

                    // Update zone analytics;
                    await UpdateZoneAnalyticsAsync(zoneId, movementRecord);

                    // Check capacity limits;
                    await CheckCapacityLimitsAsync(zone, currentCount);

                    // Trigger events;
                    await TriggerPersonEnteredEventAsync(zoneId, personInfo, currentCount);
                    await TriggerOccupancyChangedEventAsync(zoneId, oldCount, currentCount);

                    _logger.LogInformation(
                        "Person entered zone {ZoneName} ({ZoneId}). Count: {OldCount} -> {NewCount}",
                        zone.ZoneName, zoneId, oldCount.CurrentCount, currentCount.CurrentCount);

                    return new CountResult;
                    {
                        Success = true,
                        CurrentCount = currentCount,
                        MovementRecord = movementRecord,
                        Timestamp = DateTime.Now;
                    };
                }

                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = "Failed to update count",
                    Timestamp = DateTime.Now,
                    CurrentCount = oldCount;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing person entry for zone {ZoneId}", zoneId);
                await TriggerCountingErrorEventAsync(zoneId, ex);

                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = $"Error: {ex.Message}",
                    Timestamp = DateTime.Now,
                    CurrentCount = oldCount;
                };
            }
        }

        /// <summary>
        /// Registers a person leaving a zone;
        /// </summary>
        public async Task<CountResult> PersonExitedAsync(string zoneId, PersonInfo personInfo = null)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                throw new ArgumentException("Zone ID is required", nameof(zoneId));

            if (!_zones.ContainsKey(zoneId))
                throw new InvalidOperationException($"Zone '{zoneId}' is not configured");

            var zone = _zones[zoneId];

            // Validate zone is active;
            if (!zone.IsActive)
            {
                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = $"Zone '{zoneId}' is not active",
                    Timestamp = DateTime.Now;
                };
            }

            // Get current count;
            var currentCount = await GetZoneCountAsync(zoneId);
            var oldCount = new ZoneCount;
            {
                ZoneId = currentCount.ZoneId,
                ZoneName = currentCount.ZoneName,
                CurrentCount = currentCount.CurrentCount,
                LastUpdated = currentCount.LastUpdated;
            };

            // Validate exit (can't go below zero)
            if (currentCount.CurrentCount <= 0)
            {
                _logger.LogWarning("Attempted exit from empty zone {ZoneId}", zoneId);

                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = "Cannot exit from empty zone",
                    Timestamp = DateTime.Now,
                    CurrentCount = currentCount;
                };
            }

            try
            {
                // Update count;
                currentCount.CurrentCount--;
                currentCount.ExitsCount++;
                currentCount.LastUpdated = DateTime.Now;
                currentCount.LastExitTime = DateTime.Now;

                if (_zoneCounts.TryUpdate(zoneId, currentCount, currentCount))
                {
                    // Create movement record;
                    var movementRecord = new MovementRecord;
                    {
                        RecordId = Guid.NewGuid().ToString(),
                        ZoneId = zoneId,
                        ZoneName = zone.ZoneName,
                        Direction = MovementDirection.Exit,
                        Timestamp = DateTime.Now,
                        PersonInfo = personInfo,
                        PreviousCount = oldCount.CurrentCount,
                        NewCount = currentCount.CurrentCount,
                        IsManual = personInfo?.IsManualEntry ?? false;
                    };

                    // Apply algorithm-based validation if available;
                    if (personInfo?.DetectionAlgorithmId != null &&
                        _algorithms.TryGetValue(personInfo.DetectionAlgorithmId, out var algorithm))
                    {
                        movementRecord.DetectionConfidence = personInfo.DetectionConfidence;
                        movementRecord.AlgorithmId = algorithm.AlgorithmId;
                    }

                    _movementHistory.Enqueue(movementRecord);

                    // Limit history size;
                    while (_movementHistory.Count > _options.MaxHistorySize)
                    {
                        _movementHistory.TryDequeue(out _);
                    }

                    // Update zone analytics;
                    await UpdateZoneAnalyticsAsync(zoneId, movementRecord);

                    // Trigger events;
                    await TriggerPersonExitedEventAsync(zoneId, personInfo, currentCount);
                    await TriggerOccupancyChangedEventAsync(zoneId, oldCount, currentCount);

                    _logger.LogInformation(
                        "Person exited zone {ZoneName} ({ZoneId}). Count: {OldCount} -> {NewCount}",
                        zone.ZoneName, zoneId, oldCount.CurrentCount, currentCount.CurrentCount);

                    return new CountResult;
                    {
                        Success = true,
                        CurrentCount = currentCount,
                        MovementRecord = movementRecord,
                        Timestamp = DateTime.Now;
                    };
                }

                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = "Failed to update count",
                    Timestamp = DateTime.Now,
                    CurrentCount = oldCount;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing person exit for zone {ZoneId}", zoneId);
                await TriggerCountingErrorEventAsync(zoneId, ex);

                return new CountResult;
                {
                    Success = false,
                    ErrorMessage = $"Error: {ex.Message}",
                    Timestamp = DateTime.Now,
                    CurrentCount = oldCount;
                };
            }
        }

        /// <summary>
        /// Gets current count for a zone;
        /// </summary>
        public Task<ZoneCount> GetZoneCountAsync(string zoneId)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                throw new ArgumentException("Zone ID is required", nameof(zoneId));

            if (_zoneCounts.TryGetValue(zoneId, out var count))
            {
                return Task.FromResult(count);
            }

            // If zone exists but no count yet, create default;
            if (_zones.TryGetValue(zoneId, out var zone))
            {
                var newCount = new ZoneCount;
                {
                    ZoneId = zoneId,
                    ZoneName = zone.ZoneName,
                    CurrentCount = 0,
                    LastUpdated = DateTime.Now,
                    MaxCapacity = zone.MaxCapacity,
                    AreaSqm = zone.AreaSqm;
                };

                _zoneCounts.TryAdd(zoneId, newCount);
                return Task.FromResult(newCount);
            }

            throw new InvalidOperationException($"Zone '{zoneId}' is not configured");
        }

        /// <summary>
        /// Gets counts for all zones;
        /// </summary>
        public Task<IEnumerable<ZoneCount>> GetAllZoneCountsAsync()
        {
            return Task.FromResult(_zoneCounts.Values.AsEnumerable());
        }

        /// <summary>
        /// Gets occupancy statistics;
        /// </summary>
        public async Task<OccupancyStatistics> GetOccupancyStatisticsAsync(string zoneId = null)
        {
            var statistics = new OccupancyStatistics;
            {
                GeneratedAt = DateTime.Now,
                TimeRange = "Current"
            };

            IEnumerable<ZoneCount> zoneCounts;

            if (string.IsNullOrEmpty(zoneId))
            {
                zoneCounts = _zoneCounts.Values;
                statistics.TotalZones = zoneCounts.Count();
            }
            else;
            {
                var count = await GetZoneCountAsync(zoneId);
                zoneCounts = new[] { count };
                statistics.TotalZones = 1;
            }

            foreach (var zoneCount in zoneCounts)
            {
                var zoneStats = new ZoneStatistics;
                {
                    ZoneId = zoneCount.ZoneId,
                    ZoneName = zoneCount.ZoneName,
                    CurrentCount = zoneCount.CurrentCount,
                    MaxCapacity = zoneCount.MaxCapacity,
                    OccupancyPercentage = zoneCount.MaxCapacity > 0;
                        ? (zoneCount.CurrentCount * 100.0 / zoneCount.MaxCapacity)
                        : 0,
                    EntriesToday = zoneCount.EntriesToday,
                    ExitsToday = zoneCount.ExitsToday,
                    PeakCountToday = zoneCount.PeakCountToday,
                    PeakTimeToday = zoneCount.PeakTimeToday,
                    AverageDwellTime = zoneCount.AverageDwellTime,
                    IsOverCapacity = zoneCount.CurrentCount > zoneCount.MaxCapacity;
                };

                statistics.ZoneStatistics.Add(zoneStats);

                // Update totals;
                statistics.TotalPeople += zoneCount.CurrentCount;
                statistics.TotalCapacity += zoneCount.MaxCapacity;
                statistics.TotalEntriesToday += zoneCount.EntriesToday;
                statistics.TotalExitsToday += zoneCount.ExitsToday;

                if (zoneCount.CurrentCount > statistics.MaxOccupancy)
                {
                    statistics.MaxOccupancy = zoneCount.CurrentCount;
                    statistics.MaxOccupiedZone = zoneCount.ZoneName;
                }
            }

            if (statistics.TotalCapacity > 0)
            {
                statistics.OverallOccupancyPercentage =
                    (statistics.TotalPeople * 100.0 / statistics.TotalCapacity);
            }

            return statistics;
        }

        /// <summary>
        /// Gets people movement history;
        /// </summary>
        public Task<IEnumerable<MovementRecord>> GetMovementHistoryAsync(
            string zoneId = null,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            int limit = 1000)
        {
            var query = _movementHistory.AsEnumerable();

            if (!string.IsNullOrEmpty(zoneId))
            {
                query = query.Where(r => r.ZoneId == zoneId);
            }

            if (fromDate.HasValue)
            {
                query = query.Where(r => r.Timestamp >= fromDate.Value);
            }

            if (toDate.HasValue)
            {
                query = query.Where(r => r.Timestamp <= toDate.Value);
            }

            return Task.FromResult(query;
                .OrderByDescending(r => r.Timestamp)
                .Take(limit));
        }

        /// <summary>
        /// Gets real-time people density;
        /// </summary>
        public async Task<PeopleDensity> GetPeopleDensityAsync(string zoneId)
        {
            var zoneCount = await GetZoneCountAsync(zoneId);

            if (zoneCount.AreaSqm <= 0)
            {
                throw new InvalidOperationException($"Zone '{zoneId}' has no area defined");
            }

            var density = new PeopleDensity;
            {
                ZoneId = zoneId,
                ZoneName = zoneCount.ZoneName,
                Timestamp = DateTime.Now,
                PeopleCount = zoneCount.CurrentCount,
                AreaSqm = zoneCount.AreaSqm,
                DensityPerSqm = zoneCount.CurrentCount / zoneCount.AreaSqm;
            };

            // Calculate density level;
            if (density.DensityPerSqm >= _options.HighDensityThreshold)
            {
                density.DensityLevel = DensityLevel.High;
                density.Recommendation = "Consider crowd control measures";
            }
            else if (density.DensityPerSqm >= _options.MediumDensityThreshold)
            {
                density.DensityLevel = DensityLevel.Medium;
                density.Recommendation = "Monitor closely";
            }
            else;
            {
                density.DensityLevel = DensityLevel.Low;
                density.Recommendation = "Normal occupancy";
            }

            // Calculate personal space;
            if (zoneCount.CurrentCount > 0)
            {
                density.AveragePersonalSpaceSqm = zoneCount.AreaSqm / zoneCount.CurrentCount;
            }

            return density;
        }

        /// <summary>
        /// Gets peak hours analysis;
        /// </summary>
        public async Task<PeakHoursAnalysis> GetPeakHoursAnalysisAsync(string zoneId, int days = 7)
        {
            var analysis = new PeakHoursAnalysis;
            {
                ZoneId = zoneId,
                AnalysisPeriodDays = days,
                AnalysisDate = DateTime.Now;
            };

            // Get movement history for the specified period;
            var fromDate = DateTime.Now.AddDays(-days);
            var movementHistory = await GetMovementHistoryAsync(
                zoneId, fromDate, DateTime.Now, 10000);

            if (!movementHistory.Any())
            {
                analysis.Message = "Insufficient data for analysis";
                return analysis;
            }

            // Group by hour and day of week;
            var hourlyData = movementHistory;
                .GroupBy(r => new { r.Timestamp.Hour, r.Timestamp.DayOfWeek })
                .Select(g => new;
                {
                    Hour = g.Key.Hour,
                    DayOfWeek = g.Key.DayOfWeek,
                    Entries = g.Count(r => r.Direction == MovementDirection.Enter),
                    Exits = g.Count(r => r.Direction == MovementDirection.Exit)
                })
                .ToList();

            // Calculate average per hour;
            var hourlyAverages = Enumerable.Range(0, 24)
                .Select(hour => new HourlyAverage;
                {
                    Hour = hour,
                    AverageEntries = hourlyData;
                        .Where(d => d.Hour == hour)
                        .Average(d => (double)d.Entries),
                    AverageExits = hourlyData;
                        .Where(d => d.Hour == hour)
                        .Average(d => (double)d.Exits),
                    TotalOccurrences = hourlyData.Count(d => d.Hour == hour)
                })
                .ToList();

            analysis.HourlyAverages = hourlyAverages;

            // Find peak hours;
            var peakEntryHour = hourlyAverages;
                .OrderByDescending(h => h.AverageEntries)
                .FirstOrDefault();

            var peakExitHour = hourlyAverages;
                .OrderByDescending(h => h.AverageExits)
                .FirstOrDefault();

            if (peakEntryHour != null)
            {
                analysis.PeakEntryHour = peakEntryHour.Hour;
                analysis.PeakEntryAverage = peakEntryHour.AverageEntries;
            }

            if (peakExitHour != null)
            {
                analysis.PeakExitHour = peakExitHour.Hour;
                analysis.PeakExitAverage = peakExitHour.AverageExits;
            }

            // Calculate busiest day;
            var dailyData = movementHistory;
                .GroupBy(r => r.Timestamp.DayOfWeek)
                .Select(g => new;
                {
                    Day = g.Key,
                    TotalMovements = g.Count()
                })
                .OrderByDescending(d => d.TotalMovements)
                .ToList();

            if (dailyData.Any())
            {
                analysis.BusiestDay = dailyData.First().Day.ToString();
                analysis.Recommendations = GeneratePeakHourRecommendations(analysis);
            }

            return analysis;
        }

        /// <summary>
        /// Configures a counting zone;
        /// </summary>
        public async Task<bool> ConfigureZoneAsync(CountingZone zone)
        {
            if (zone == null)
                throw new ArgumentNullException(nameof(zone));

            if (string.IsNullOrWhiteSpace(zone.ZoneId))
                throw new ArgumentException("Zone ID is required", nameof(zone.ZoneId));

            try
            {
                // Add or update zone;
                _zones.AddOrUpdate(zone.ZoneId, zone, (id, existing) => zone);

                // Initialize count for zone if not exists;
                if (!_zoneCounts.ContainsKey(zone.ZoneId))
                {
                    var zoneCount = new ZoneCount;
                    {
                        ZoneId = zone.ZoneId,
                        ZoneName = zone.ZoneName,
                        CurrentCount = 0,
                        LastUpdated = DateTime.Now,
                        MaxCapacity = zone.MaxCapacity,
                        AreaSqm = zone.AreaSqm,
                        IsActive = zone.IsActive;
                    };

                    _zoneCounts.TryAdd(zone.ZoneId, zoneCount);
                }

                // Initialize sensors for zone;
                await InitializeZoneSensorsAsync(zone);

                _logger.LogInformation(
                    "Zone configured: {ZoneName} ({ZoneId}) - Capacity: {Capacity}, Area: {Area} sqm",
                    zone.ZoneName, zone.ZoneId, zone.MaxCapacity, zone.AreaSqm);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to configure zone {ZoneId}", zone.ZoneId);
                return false;
            }
        }

        /// <summary>
        /// Removes a counting zone;
        /// </summary>
        public Task<bool> RemoveZoneAsync(string zoneId)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                throw new ArgumentException("Zone ID is required", nameof(zoneId));

            var zoneRemoved = _zones.TryRemove(zoneId, out _);
            var countRemoved = _zoneCounts.TryRemove(zoneId, out _);

            // Remove associated sensors;
            var zoneSensors = _sensors.Where(s => s.Value.ZoneId == zoneId).ToList();
            foreach (var sensor in zoneSensors)
            {
                _sensors.TryRemove(sensor.Key, out _);
            }

            _logger.LogInformation("Zone removed: {ZoneId}", zoneId);

            return Task.FromResult(zoneRemoved || countRemoved);
        }

        /// <summary>
        /// Performs automatic calibration;
        /// </summary>
        public async Task<CalibrationResult> CalibrateAsync(string zoneId)
        {
            var result = new CalibrationResult;
            {
                ZoneId = zoneId,
                CalibrationId = Guid.NewGuid().ToString(),
                StartTime = DateTime.Now;
            };

            _logger.LogInformation("Starting calibration for zone {ZoneId}", zoneId);

            try
            {
                // Get zone sensors;
                var zoneSensors = _sensors.Values;
                    .Where(s => s.ZoneId == zoneId)
                    .ToList();

                if (!zoneSensors.Any())
                {
                    result.Status = CalibrationStatus.Failed;
                    result.Message = "No sensors found for calibration";
                    result.EndTime = DateTime.Now;
                    return result;
                }

                // Calibrate each sensor;
                var calibrationTasks = new List<Task<SensorCalibrationResult>>();
                foreach (var sensor in zoneSensors)
                {
                    calibrationTasks.Add(CalibrateSensorAsync(sensor));
                }

                var sensorResults = await Task.WhenAll(calibrationTasks);
                result.SensorResults.AddRange(sensorResults);

                // Calculate overall calibration;
                var successfulCalibrations = sensorResults.Count(r => r.Status == CalibrationStatus.Completed);
                var totalSensors = sensorResults.Length;

                if (successfulCalibrations == totalSensors)
                {
                    result.Status = CalibrationStatus.Completed;
                    result.Message = $"All {totalSensors} sensors calibrated successfully";
                }
                else if (successfulCalibrations > 0)
                {
                    result.Status = CalibrationStatus.Partial;
                    result.Message = $"{successfulCalibrations} of {totalSensors} sensors calibrated";
                }
                else;
                {
                    result.Status = CalibrationStatus.Failed;
                    result.Message = "All sensor calibrations failed";
                }

                // Update zone accuracy;
                if (successfulCalibrations > 0)
                {
                    var averageAccuracy = sensorResults;
                        .Where(r => r.Accuracy.HasValue)
                        .Average(r => r.Accuracy.Value);

                    result.AverageAccuracy = averageAccuracy;

                    if (_zoneCounts.TryGetValue(zoneId, out var zoneCount))
                    {
                        zoneCount.CalibrationAccuracy = averageAccuracy;
                        zoneCount.LastCalibration = DateTime.Now;
                    }
                }

                _logger.LogInformation(
                    "Calibration completed for zone {ZoneId}: {Status}",
                    zoneId, result.Status);
            }
            catch (Exception ex)
            {
                result.Status = CalibrationStatus.Failed;
                result.Message = $"Calibration error: {ex.Message}";
                result.ErrorDetails = ex.ToString();

                _logger.LogError(ex, "Calibration failed for zone {ZoneId}", zoneId);
            }

            result.EndTime = DateTime.Now;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        /// <summary>
        /// Gets system diagnostics;
        /// </summary>
        public async Task<CounterDiagnostics> GetDiagnosticsAsync()
        {
            var diagnostics = new CounterDiagnostics;
            {
                SystemId = _options.SystemId,
                SystemName = _options.SystemName,
                IsRunning = _isRunning,
                StartTime = _startTime,
                Uptime = _isRunning ? DateTime.Now - _startTime : TimeSpan.Zero,
                TotalZones = _zones.Count,
                ActiveZones = _zones.Count(z => z.Value.IsActive),
                TotalSensors = _sensors.Count,
                ActiveSensors = _sensors.Count(s => s.Value.Status == SensorStatus.Active),
                TotalAlgorithms = _algorithms.Count,
                MovementHistorySize = _movementHistory.Count,
                MemoryUsage = Process.GetCurrentProcess().WorkingSet64,
                GeneratedAt = DateTime.Now;
            };

            // Get sensor status breakdown;
            diagnostics.SensorStatusBreakdown = _sensors.Values;
                .GroupBy(s => s.Status)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            // Get zone occupancy summary;
            diagnostics.ZoneOccupancySummary = _zoneCounts.Values;
                .Select(z => new ZoneOccupancySummary;
                {
                    ZoneId = z.ZoneId,
                    ZoneName = z.ZoneName,
                    CurrentCount = z.CurrentCount,
                    MaxCapacity = z.MaxCapacity,
                    OccupancyPercentage = z.MaxCapacity > 0;
                        ? (z.CurrentCount * 100.0 / z.MaxCapacity)
                        : 0,
                    LastUpdated = z.LastUpdated;
                })
                .ToList();

            // Check for issues;
            diagnostics.Issues = await DetectIssuesAsync();

            return diagnostics;
        }

        /// <summary>
        /// Resets counts for a zone;
        /// </summary>
        public Task<bool> ResetCountsAsync(string zoneId)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
                throw new ArgumentException("Zone ID is required", nameof(zoneId));

            if (!_zoneCounts.ContainsKey(zoneId))
                return Task.FromResult(false);

            var oldCount = _zoneCounts[zoneId];
            var newCount = new ZoneCount;
            {
                ZoneId = zoneId,
                ZoneName = oldCount.ZoneName,
                CurrentCount = 0,
                LastUpdated = DateTime.Now,
                MaxCapacity = oldCount.MaxCapacity,
                AreaSqm = oldCount.AreaSqm,
                IsActive = oldCount.IsActive;
            };

            var success = _zoneCounts.TryUpdate(zoneId, newCount, oldCount);

            if (success)
            {
                _logger.LogInformation("Counts reset for zone {ZoneId}", zoneId);
                TriggerOccupancyChangedEventAsync(zoneId, oldCount, newCount);
            }

            return Task.FromResult(success);
        }

        /// <summary>
        /// Starts continuous monitoring;
        /// </summary>
        public async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
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
                _startTime = DateTime.Now;

                // Start timers;
                _healthCheckTimer.Start();
                _analyticsTimer.Start();
                _cleanupTimer.Start();

                // Start monitoring task;
                _monitoringTask = Task.Run(() => MonitorSensorsAsync(cancellationToken), cancellationToken);

                // Initialize all sensors;
                await InitializeAllSensorsAsync();

                _logger.LogInformation("PeopleCounter started successfully");
                await TriggerSystemEventAsync("System started");
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger.LogError(ex, "Failed to start PeopleCounter");
                throw new InvalidOperationException("Failed to start system", ex);
            }
        }

        /// <summary>
        /// Stops continuous monitoring;
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (!_isRunning)
                return;

            try
            {
                // Stop timers;
                _healthCheckTimer.Stop();
                _analyticsTimer.Stop();
                _cleanupTimer.Stop();

                // Cancel monitoring;
                _globalCancellationTokenSource.Cancel();

                // Wait for monitoring to stop;
                if (_monitoringTask != null && !_monitoringTask.IsCompleted)
                {
                    await _monitoringTask.WaitAsync(TimeSpan.FromSeconds(30));
                }

                // Stop all sensors;
                await StopAllSensorsAsync();

                _isRunning = false;
                _logger.LogInformation("PeopleCounter stopped");
                await TriggerSystemEventAsync("System stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping PeopleCounter");
                throw new InvalidOperationException("Failed to stop system", ex);
            }
        }

        /// <summary>
        /// Subscribes to counting events;
        /// </summary>
        public void SubscribeToEvents(IPeopleCountEventHandler eventHandler)
        {
            if (eventHandler == null)
                throw new ArgumentNullException(nameof(eventHandler));

            _eventHandlers.Add(eventHandler);
            _logger.LogInformation("Event handler subscribed: {EventHandlerType}",
                eventHandler.GetType().Name);
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Initialize default detection algorithms;
        /// </summary>
        private void InitializeDefaultAlgorithms()
        {
            // Register default algorithms;
            RegisterAlgorithm(new SimpleThresholdAlgorithm());
            RegisterAlgorithm(new MotionDetectionAlgorithm());
            RegisterAlgorithm(new ThermalDetectionAlgorithm());

            // You can add more algorithms based on your needs;
            // RegisterAlgorithm(new DeepLearningAlgorithm());
            // RegisterAlgorithm(new ComputerVisionAlgorithm());
        }

        /// <summary>
        /// Register a detection algorithm;
        /// </summary>
        private void RegisterAlgorithm(IPeopleDetectionAlgorithm algorithm)
        {
            if (algorithm == null)
                throw new ArgumentNullException(nameof(algorithm));

            _algorithms.AddOrUpdate(
                algorithm.AlgorithmId,
                algorithm,
                (id, existing) => algorithm);

            _logger.LogInformation(
                "Algorithm registered: {AlgorithmName} ({AlgorithmId})",
                algorithm.AlgorithmName, algorithm.AlgorithmId);
        }

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
        /// Initialize sensors for a zone;
        /// </summary>
        private async Task InitializeZoneSensorsAsync(CountingZone zone)
        {
            // Create default sensors for zone if none exist;
            var zoneSensors = _sensors.Values;
                .Where(s => s.ZoneId == zone.ZoneId)
                .ToList();

            if (!zoneSensors.Any() && zone.AutoCreateSensors)
            {
                // Create entry sensor;
                var entrySensor = CreateDefaultSensor(
                    $"{zone.ZoneId}-ENTRY",
                    "Entry Sensor",
                    zone.ZoneId,
                    SensorType.Infrared);

                await RegisterSensorAsync(entrySensor);

                // Create exit sensor;
                var exitSensor = CreateDefaultSensor(
                    $"{zone.ZoneId}-EXIT",
                    "Exit Sensor",
                    zone.ZoneId,
                    SensorType.Infrared);

                await RegisterSensorAsync(exitSensor);

                _logger.LogInformation("Created default sensors for zone {ZoneId}", zone.ZoneId);
            }
        }

        /// <summary>
        /// Create default sensor;
        /// </summary>
        private ICountingSensor CreateDefaultSensor(string sensorId, string sensorName, string zoneId, SensorType sensorType)
        {
            return sensorType switch;
            {
                SensorType.Infrared => new InfraredSensor(
                    sensorId, sensorName, zoneId,
                    new InfraredSensorOptions { DetectionRange = 5 }),

                SensorType.Thermal => new ThermalSensor(
                    sensorId, sensorName, zoneId,
                    new ThermalSensorOptions { TemperatureThreshold = 30.0 }),

                SensorType.Ultrasonic => new UltrasonicSensor(
                    sensorId, sensorName, zoneId,
                    new UltrasonicSensorOptions { RangeCm = 500 }),

                SensorType.Laser => new LaserSensor(
                    sensorId, sensorName, zoneId,
                    new LaserSensorOptions { BeamCount = 2 }),

                SensorType.Camera => new CameraSensor(
                    sensorId, sensorName, zoneId,
                    new CameraSensorOptions { Resolution = "1920x1080" }),

                _ => new InfraredSensor(
                    sensorId, sensorName, zoneId,
                    new InfraredSensorOptions { DetectionRange = 5 })
            };
        }

        /// <summary>
        /// Register a sensor;
        /// </summary>
        private async Task<bool> RegisterSensorAsync(ICountingSensor sensor)
        {
            try
            {
                await sensor.InitializeAsync();

                if (_sensors.TryAdd(sensor.SensorId, sensor))
                {
                    _logger.LogInformation(
                        "Sensor registered: {SensorName} ({SensorId}) in zone {ZoneId}",
                        sensor.SensorName, sensor.SensorId, sensor.ZoneId);

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register sensor {SensorId}", sensor.SensorId);
                return false;
            }
        }

        /// <summary>
        /// Validate person entry
        /// </summary>
        private async Task<ValidationResult> ValidateEntryAsync(CountingZone zone, ZoneCount currentCount, PersonInfo personInfo)
        {
            var result = new ValidationResult { IsValid = true };

            // Check zone capacity;
            if (zone.MaxCapacity > 0 && currentCount.CurrentCount >= zone.MaxCapacity)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Zone capacity ({zone.MaxCapacity}) reached";
                return result;
            }

            // Check entry restrictions;
            if (zone.EntryRestrictions != null && zone.EntryRestrictions.Any())
            {
                if (personInfo != null)
                {
                    // Check age restrictions;
                    if (zone.EntryRestrictions.Contains(EntryRestriction.AdultsOnly) &&
                        personInfo.Age < 18)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Adults only zone";
                        return result;
                    }

                    // Check membership requirements;
                    if (zone.EntryRestrictions.Contains(EntryRestriction.MembersOnly) &&
                        !personInfo.IsMember)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Members only zone";
                        return result;
                    }
                }
            }

            // Check time-based restrictions;
            if (zone.AccessHours != null && zone.AccessHours.Any())
            {
                var currentTime = DateTime.Now.TimeOfDay;
                var currentDay = DateTime.Now.DayOfWeek;

                var allowedHours = zone.AccessHours;
                    .FirstOrDefault(h => h.DayOfWeek == currentDay);

                if (allowedHours != null)
                {
                    if (currentTime < allowedHours.OpenTime || currentTime > allowedHours.CloseTime)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = $"Zone accessible only between {allowedHours.OpenTime} and {allowedHours.CloseTime}";
                        return result;
                    }
                }
            }

            // Apply algorithm validation if available;
            if (personInfo?.DetectionAlgorithmId != null &&
                _algorithms.TryGetValue(personInfo.DetectionAlgorithmId, out var algorithm))
            {
                if (personInfo.DetectionConfidence < algorithm.ConfidenceThreshold)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Low detection confidence: {personInfo.DetectionConfidence:F2}";
                    return result;
                }
            }

            return result;
        }

        /// <summary>
        /// Update zone analytics;
        /// </summary>
        private async Task UpdateZoneAnalyticsAsync(string zoneId, MovementRecord movementRecord)
        {
            if (!_zoneCounts.TryGetValue(zoneId, out var zoneCount))
                return;

            // Update today's statistics;
            var today = DateTime.Today;
            if (movementRecord.Timestamp.Date == today)
            {
                if (movementRecord.Direction == MovementDirection.Enter)
                {
                    zoneCount.EntriesToday++;
                }
                else;
                {
                    zoneCount.ExitsToday++;
                }

                // Update peak count;
                if (zoneCount.CurrentCount > zoneCount.PeakCountToday)
                {
                    zoneCount.PeakCountToday = zoneCount.CurrentCount;
                    zoneCount.PeakTimeToday = DateTime.Now;
                }
            }
            else;
            {
                // Reset daily counters if it's a new day;
                zoneCount.EntriesToday = movementRecord.Direction == MovementDirection.Enter ? 1 : 0;
                zoneCount.ExitsToday = movementRecord.Direction == MovementDirection.Exit ? 1 : 0;
                zoneCount.PeakCountToday = zoneCount.CurrentCount;
                zoneCount.PeakTimeToday = DateTime.Now;
            }

            // Update dwell time calculations (simplified)
            if (movementRecord.Direction == MovementDirection.Exit)
            {
                // Find corresponding entry for this person (simplified)
                var entryTime = await FindCorrespondingEntryTimeAsync(zoneId, movementRecord);
                if (entryTime.HasValue)
                {
                    var dwellTime = movementRecord.Timestamp - entryTime.Value;
                    UpdateDwellTimeStatistics(zoneCount, dwellTime);
                }
            }

            _zoneCounts.TryUpdate(zoneId, zoneCount, zoneCount);
        }

        /// <summary>
        /// Find corresponding entry time for an exit;
        /// </summary>
        private async Task<DateTime?> FindCorrespondingEntryTimeAsync(string zoneId, MovementRecord exitRecord)
        {
            // Simplified implementation;
            // In production, you would track individual person IDs or use more sophisticated matching;

            var recentEntries = await GetMovementHistoryAsync(
                zoneId,
                DateTime.Now.AddMinutes(-30),
                DateTime.Now,
                100);

            var matchingEntry = recentEntries;
                .Where(r => r.Direction == MovementDirection.Enter)
                .OrderByDescending(r => r.Timestamp)
                .FirstOrDefault();

            return matchingEntry?.Timestamp;
        }

        /// <summary>
        /// Update dwell time statistics;
        /// </summary>
        private void UpdateDwellTimeStatistics(ZoneCount zoneCount, TimeSpan dwellTime)
        {
            // Update total dwell time;
            zoneCount.TotalDwellTime += dwellTime;
            zoneCount.TotalVisits++;

            // Update average;
            if (zoneCount.TotalVisits > 0)
            {
                zoneCount.AverageDwellTime = TimeSpan.FromSeconds(
                    zoneCount.TotalDwellTime.TotalSeconds / zoneCount.TotalVisits);
            }

            // Update min/max;
            if (zoneCount.MinDwellTime == TimeSpan.Zero || dwellTime < zoneCount.MinDwellTime)
            {
                zoneCount.MinDwellTime = dwellTime;
            }

            if (dwellTime > zoneCount.MaxDwellTime)
            {
                zoneCount.MaxDwellTime = dwellTime;
            }
        }

        /// <summary>
        /// Check capacity limits;
        /// </summary>
        private async Task CheckCapacityLimitsAsync(CountingZone zone, ZoneCount currentCount)
        {
            if (zone.MaxCapacity <= 0)
                return;

            var occupancyPercentage = (currentCount.CurrentCount * 100.0) / zone.MaxCapacity;

            // Check if capacity is exceeded;
            if (currentCount.CurrentCount > zone.MaxCapacity)
            {
                await TriggerCapacityExceededEventAsync(zone.ZoneId, currentCount, zone.MaxCapacity);

                _logger.LogWarning(
                    "Zone {ZoneName} capacity exceeded: {Current}/{Max} ({Percentage:F1}%)",
                    zone.ZoneName, currentCount.CurrentCount, zone.MaxCapacity, occupancyPercentage);
            }
            // Check warning threshold;
            else if (zone.CapacityWarningThreshold > 0 &&
                     occupancyPercentage >= zone.CapacityWarningThreshold)
            {
                _logger.LogInformation(
                    "Zone {ZoneName} approaching capacity: {Current}/{Max} ({Percentage:F1}%)",
                    zone.ZoneName, currentCount.CurrentCount, zone.MaxCapacity, occupancyPercentage);
            }
        }

        /// <summary>
        /// Calibrate a sensor;
        /// </summary>
        private async Task<SensorCalibrationResult> CalibrateSensorAsync(ICountingSensor sensor)
        {
            var result = new SensorCalibrationResult;
            {
                SensorId = sensor.SensorId,
                SensorName = sensor.SensorName,
                StartTime = DateTime.Now;
            };

            try
            {
                var calibrationResult = await sensor.CalibrateAsync();

                result.Status = calibrationResult.Status;
                result.Message = calibrationResult.Message;
                result.Accuracy = calibrationResult.Accuracy;
                result.CalibrationData = calibrationResult.CalibrationData;

                // Update sensor status;
                await TriggerSensorStatusChangedEventAsync(
                    sensor.SensorId,
                    sensor.Status,
                    calibrationResult.Status == CalibrationStatus.Completed ?
                        SensorStatus.Active : SensorStatus.NeedsCalibration);
            }
            catch (Exception ex)
            {
                result.Status = CalibrationStatus.Failed;
                result.Message = $"Calibration error: {ex.Message}";
                result.ErrorDetails = ex.ToString();
            }

            result.EndTime = DateTime.Now;
            result.Duration = result.EndTime - result.StartTime;

            return result;
        }

        /// <summary>
        /// Detect system issues;
        /// </summary>
        private async Task<List<SystemIssue>> DetectIssuesAsync()
        {
            var issues = new List<SystemIssue>();

            // Check sensor status;
            foreach (var sensor in _sensors.Values)
            {
                if (sensor.Status == SensorStatus.Error ||
                    sensor.Status == SensorStatus.Offline)
                {
                    issues.Add(new SystemIssue;
                    {
                        IssueType = IssueType.SensorFailure,
                        SourceId = sensor.SensorId,
                        Description = $"Sensor {sensor.SensorName} is {sensor.Status}",
                        Severity = IssueSeverity.High,
                        DetectedAt = DateTime.Now;
                    });
                }
            }

            // Check zone capacity issues;
            foreach (var zoneCount in _zoneCounts.Values)
            {
                if (zoneCount.MaxCapacity > 0 &&
                    zoneCount.CurrentCount > zoneCount.MaxCapacity)
                {
                    issues.Add(new SystemIssue;
                    {
                        IssueType = IssueType.CapacityExceeded,
                        SourceId = zoneCount.ZoneId,
                        Description = $"Zone {zoneCount.ZoneName} exceeded capacity: {zoneCount.CurrentCount}/{zoneCount.MaxCapacity}",
                        Severity = IssueSeverity.Medium,
                        DetectedAt = DateTime.Now;
                    });
                }
            }

            // Check calibration status;
            foreach (var zoneCount in _zoneCounts.Values)
            {
                if (zoneCount.LastCalibration.HasValue)
                {
                    var daysSinceCalibration = (DateTime.Now - zoneCount.LastCalibration.Value).TotalDays;
                    if (daysSinceCalibration > _options.CalibrationWarningDays)
                    {
                        issues.Add(new SystemIssue;
                        {
                            IssueType = IssueType.NeedsCalibration,
                            SourceId = zoneCount.ZoneId,
                            Description = $"Zone {zoneCount.ZoneName} needs calibration (last: {daysSinceCalibration:F0} days ago)",
                            Severity = IssueSeverity.Low,
                            DetectedAt = DateTime.Now;
                        });
                    }
                }
            }

            return issues;
        }

        /// <summary>
        /// Generate peak hour recommendations;
        /// </summary>
        private List<string> GeneratePeakHourRecommendations(PeakHoursAnalysis analysis)
        {
            var recommendations = new List<string>();

            if (analysis.PeakEntryAverage > 50) // High traffic;
            {
                recommendations.Add("Consider adding more entry points during peak hours");
                recommendations.Add("Implement queue management system");
            }

            if (analysis.PeakEntryHour >= 8 && analysis.PeakEntryHour <= 10) // Morning peak;
            {
                recommendations.Add("Morning peak detected - consider staggered opening times");
            }

            if (analysis.PeakExitHour >= 17 && analysis.PeakExitHour <= 19) // Evening peak;
            {
                recommendations.Add("Evening peak detected - consider extended closing times");
            }

            if (analysis.HourlyAverages.Any(h => h.AverageEntries == 0))
            {
                recommendations.Add("Some hours have zero entries - consider promotional activities");
            }

            return recommendations;
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
                    // Monitor each sensor;
                    foreach (var sensor in _sensors.Values.Where(s => s.Status == SensorStatus.Active))
                    {
                        try
                        {
                            var reading = await sensor.GetReadingAsync();
                            if (reading != null && reading.IsValid)
                            {
                                await ProcessSensorReadingAsync(sensor, reading);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error reading sensor {SensorId}", sensor.SensorId);
                        }
                    }

                    // Small delay to prevent CPU spinning;
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in sensor monitoring loop");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Process sensor reading;
        /// </summary>
        private async Task ProcessSensorReadingAsync(ICountingSensor sensor, SensorReading reading)
        {
            // Determine movement direction based on sensor position and reading;
            // This is a simplified implementation;
            var zoneId = sensor.ZoneId;
            var movementDirection = DetermineMovementDirection(sensor, reading);

            if (movementDirection == MovementDirection.Enter)
            {
                await PersonEnteredAsync(zoneId, new PersonInfo;
                {
                    DetectionAlgorithmId = "SENSOR_BASED",
                    DetectionConfidence = reading.Confidence,
                    Timestamp = reading.Timestamp;
                });
            }
            else if (movementDirection == MovementDirection.Exit)
            {
                await PersonExitedAsync(zoneId, new PersonInfo;
                {
                    DetectionAlgorithmId = "SENSOR_BASED",
                    DetectionConfidence = reading.Confidence,
                    Timestamp = reading.Timestamp;
                });
            }
        }

        /// <summary>
        /// Determine movement direction from sensor reading;
        /// </summary>
        private MovementDirection DetermineMovementDirection(ICountingSensor sensor, SensorReading reading)
        {
            // Simplified logic - in production, this would use sensor-specific logic;
            // For example, dual-beam sensors can determine direction based on beam break sequence;

            if (sensor.SensorName.Contains("ENTRY", StringComparison.OrdinalIgnoreCase))
                return MovementDirection.Enter;

            if (sensor.SensorName.Contains("EXIT", StringComparison.OrdinalIgnoreCase))
                return MovementDirection.Exit;

            // Default based on sensor type;
            return sensor.SensorType switch;
            {
                SensorType.Infrared => MovementDirection.Enter, // Assume entry for single IR;
                SensorType.Thermal => MovementDirection.Enter,
                SensorType.Ultrasonic => MovementDirection.Enter,
                _ => MovementDirection.Enter;
            };
        }

        /// <summary>
        /// Stop all sensors;
        /// </summary>
        private async Task StopAllSensorsAsync()
        {
            foreach (var sensor in _sensors.Values)
            {
                try
                {
                    await sensor.StopMonitoringAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping sensor {SensorId}", sensor.SensorId);
                }
            }
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
        }

        /// <summary>
        /// Handle analytics timer;
        /// </summary>
        private async void OnAnalyticsTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                await UpdateAnalyticsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in analytics timer");
            }
        }

        /// <summary>
        /// Update analytics;
        /// </summary>
        private async Task UpdateAnalyticsAsync()
        {
            // Update hourly statistics;
            var now = DateTime.Now;
            if (now.Minute == 0) // Every hour;
            {
                foreach (var zoneCount in _zoneCounts.Values)
                {
                    // Update hourly counts;
                    if (!zoneCount.HourlyCounts.ContainsKey(now.Hour))
                    {
                        zoneCount.HourlyCounts[now.Hour] = 0;
                    }
                    zoneCount.HourlyCounts[now.Hour] = zoneCount.CurrentCount;
                }
            }
        }

        /// <summary>
        /// Handle cleanup timer;
        /// </summary>
        private async void OnCleanupTimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                await CleanupOldDataAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup timer");
            }
        }

        /// <summary>
        /// Cleanup old data;
        /// </summary>
        private async Task CleanupOldDataAsync()
        {
            var cutoffDate = DateTime.Now.AddDays(-_options.HistoryRetentionDays);
            var recordsToRemove = _movementHistory;
                .Where(r => r.Timestamp < cutoffDate)
                .ToList();

            // Remove old records (simplified - in production use circular buffer or database)
            foreach (var record in recordsToRemove)
            {
                // Just dequeue - this is simplified;
                if (_movementHistory.TryDequeue(out _))
                {
                    // Record removed;
                }
            }

            if (recordsToRemove.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old movement records", recordsToRemove.Count);
            }

            await Task.CompletedTask;
        }

        #endregion;

        #region Event Triggering Methods;

        private async Task TriggerPersonEnteredEventAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount)
        {
            var tasks = new List<Task>();

            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnPersonEnteredAsync(zoneId, personInfo, currentCount));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task TriggerPersonExitedEventAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount)
        {
            var tasks = new List<Task>();

            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnPersonExitedAsync(zoneId, personInfo, currentCount));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task TriggerCapacityExceededEventAsync(string zoneId, ZoneCount currentCount, int capacityLimit)
        {
            var tasks = new List<Task>();

            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnZoneCapacityExceededAsync(zoneId, currentCount, capacityLimit));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task TriggerOccupancyChangedEventAsync(string zoneId, ZoneCount oldCount, ZoneCount newCount)
        {
            var tasks = new List<Task>();

            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnZoneOccupancyChangedAsync(zoneId, oldCount, newCount));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task TriggerCountingErrorEventAsync(string zoneId, Exception error)
        {
            var tasks = new List<Task>();

            foreach (var handler in _eventHandlers)
            {
                try
                {
                    tasks.Add(handler.OnCountingErrorAsync(zoneId, error));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event handler {HandlerType}", handler.GetType().Name);
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task TriggerSensorStatusChangedEventAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus)
        {
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

        private async Task TriggerSystemEventAsync(string message)
        {
            // Log system event;
            _logger.LogInformation("System event: {Message}", message);
            await Task.CompletedTask;
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
                    _analyticsTimer?.Dispose();
                    _cleanupTimer?.Dispose();
                    _globalCancellationTokenSource?.Dispose();
                }

                _isDisposed = true;
            }
        }

        #endregion;
    }

    #endregion;

    #region Sensor Implementations;

    /// <summary>
    /// Base class for counting sensors;
    /// </summary>
    public abstract class BaseCountingSensor : ICountingSensor;
    {
        #region Protected Fields;

        protected readonly ILogger _logger;
        protected readonly object _lockObject = new object();
        protected CancellationTokenSource _monitoringCancellationTokenSource;
        protected Task _monitoringTask;
        protected bool _isMonitoring;
        protected DateTime _startTime;
        protected Random _random;

        #endregion;

        #region Properties;

        public string SensorId { get; protected set; }
        public string SensorName { get; protected set; }
        public abstract SensorType SensorType { get; }
        public string ZoneId { get; protected set; }
        public SensorStatus Status { get; protected set; }
        public int DetectionRange { get; protected set; }
        public double Accuracy { get; protected set; }

        #endregion;

        #region Constructors;

        protected BaseCountingSensor(string sensorId, string sensorName, string zoneId, ILogger logger)
        {
            SensorId = sensorId ?? throw new ArgumentNullException(nameof(sensorId));
            SensorName = sensorName ?? throw new ArgumentNullException(nameof(sensorName));
            ZoneId = zoneId ?? throw new ArgumentNullException(nameof(zoneId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _random = new Random();
            Status = SensorStatus.Inactive;
            DetectionRange = 5; // Default 5 meters;
            Accuracy = 0.95; // Default 95% accuracy;
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
                _startTime = DateTime.Now;

                // Perform sensor-specific initialization;
                var initialized = await InitializeSensorAsync();

                if (initialized)
                {
                    Status = SensorStatus.Active;
                    _logger.LogInformation("Sensor {SensorId} initialized successfully", SensorId);
                    return true;
                }
                else;
                {
                    Status = SensorStatus.Error;
                    _logger.LogError("Sensor {SensorId} initialization failed", SensorId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Status = SensorStatus.Error;
                _logger.LogError(ex, "Error initializing sensor {SensorId}", SensorId);
                return false;
            }
        }

        public abstract Task<SensorReading> GetReadingAsync();

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

                // Perform sensor-specific calibration;
                var calibrationData = await PerformCalibrationAsync();

                result.Status = CalibrationStatus.Completed;
                result.Message = "Calibration completed successfully";
                result.CalibrationData = calibrationData;
                result.Accuracy = await CalculateAccuracyAsync();

                Accuracy = result.Accuracy.Value;

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
            return result;
        }

        public virtual Task<SensorDiagnostics> GetDiagnosticsAsync()
        {
            var diagnostics = new SensorDiagnostics;
            {
                SensorId = SensorId,
                SensorName = SensorName,
                SensorType = SensorType.ToString(),
                ZoneId = ZoneId,
                Status = Status,
                IsMonitoring = _isMonitoring,
                Uptime = DateTime.Now - _startTime,
                Accuracy = Accuracy,
                DetectionRange = DetectionRange,
                HealthStatus = GetHealthStatus(),
                BatteryLevel = GetBatteryLevel(),
                SignalStrength = GetSignalStrength(),
                LastReadingTime = GetLastReadingTime(),
                ReadingCount = GetReadingCount()
            };

            return Task.FromResult(diagnostics);
        }

        public virtual async Task StartMonitoringAsync(CancellationToken cancellationToken)
        {
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
        protected abstract Task<Dictionary<string, object>> PerformCalibrationAsync();
        protected abstract Task MonitorAsync(CancellationToken cancellationToken);

        #endregion;

        #region Protected Virtual Methods;

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

        protected virtual DateTime? GetLastReadingTime()
        {
            // Base implementation;
            return DateTime.Now.AddSeconds(-10);
        }

        protected virtual long GetReadingCount()
        {
            // Base implementation;
            return 0;
        }

        protected virtual async Task<double> CalculateAccuracyAsync()
        {
            // Simulate accuracy calculation;
            await Task.Delay(100);
            return 0.95 + (_random.NextDouble() * 0.05); // 95-100%
        }

        #endregion;
    }

    /// <summary>
    /// Infrared sensor implementation;
    /// </summary>
    public class InfraredSensor : BaseCountingSensor;
    {
        private readonly InfraredSensorOptions _options;
        private DateTime _lastDetection;
        private long _readingCount;

        public override SensorType SensorType => SensorType.Infrared;

        public InfraredSensor(
            string sensorId,
            string sensorName,
            string zoneId,
            InfraredSensorOptions options,
            ILogger<InfraredSensor> logger = null)
            : base(sensorId, sensorName, zoneId, logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            DetectionRange = options.DetectionRange;
        }

        protected override async Task<bool> InitializeSensorAsync()
        {
            _logger.LogDebug("Initializing infrared sensor {SensorId}", SensorId);

            // Simulate hardware initialization;
            await Task.Delay(500);

            // Check if sensor is responding;
            var isResponding = await TestSensorResponseAsync();

            if (!isResponding)
            {
                _logger.LogError("Infrared sensor {SensorId} not responding", SensorId);
                return false;
            }

            // Set detection parameters;
            await SetDetectionParametersAsync();

            return true;
        }

        protected override async Task<SensorReading> GetReadingAsync()
        {
            try
            {
                var reading = new SensorReading;
                {
                    SensorId = SensorId,
                    Timestamp = DateTime.Now,
                    ReadingType = "IR_DETECTION"
                };

                // Simulate infrared detection;
                var hasDetection = await CheckForInfraredDetectionAsync();

                if (hasDetection)
                {
                    reading.IsValid = true;
                    reading.DetectedObjects = 1;
                    reading.Confidence = 0.85 + (_random.NextDouble() * 0.15); // 85-100%
                    reading.RawData = new Dictionary<string, object>
                    {
                        { "ir_intensity", 150 + _random.Next(100) },
                        { "ambient_light", 50 + _random.Next(50) },
                        { "detection_duration_ms", 100 + _random.Next(200) }
                    };

                    _lastDetection = DateTime.Now;
                    _readingCount++;
                }
                else;
                {
                    reading.IsValid = false;
                    reading.DetectedObjects = 0;
                    reading.Confidence = 0.0;
                }

                return reading;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reading from infrared sensor {SensorId}", SensorId);
                return new SensorReading;
                {
                    SensorId = SensorId,
                    Timestamp = DateTime.Now,
                    IsValid = false,
                    ErrorMessage = ex.Message;
                };
            }
        }

        protected override async Task<Dictionary<string, object>> PerformCalibrationAsync()
        {
            _logger.LogDebug("Calibrating infrared sensor {SensorId}", SensorId);

            var calibrationData = new Dictionary<string, object>();

            // Calibrate sensitivity;
            calibrationData["sensitivity"] = await CalibrateSensitivityAsync();

            // Calibrate detection threshold;
            calibrationData["detection_threshold"] = await CalibrateDetectionThresholdAsync();

            // Calibrate ambient light compensation;
            calibrationData["ambient_compensation"] = await CalibrateAmbientCompensationAsync();

            // Update accuracy;
            Accuracy = 0.97 + (_random.NextDouble() * 0.03); // 97-100%

            return calibrationData;
        }

        protected override async Task MonitorAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Infrared sensor {SensorId} monitoring started", SensorId);

            while (!cancellationToken.IsCancellationRequested && _isMonitoring)
            {
                try
                {
                    // Get reading;
                    var reading = await GetReadingAsync();

                    if (reading.IsValid && reading.DetectedObjects > 0)
                    {
                        // Process detection;
                        await ProcessDetectionAsync(reading);
                    }

                    // Check sensor health;
                    await CheckSensorHealthAsync();

                    // Sleep based on polling interval;
                    await Task.Delay(_options.PollingInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in infrared sensor monitoring loop");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            _logger.LogInformation("Infrared sensor {SensorId} monitoring stopped", SensorId);
        }

        protected override DateTime? GetLastReadingTime()
        {
            return _lastDetection;
        }

        protected override long GetReadingCount()
        {
            return _readingCount;
        }

        private async Task<bool> TestSensorResponseAsync()
        {
            await Task.Delay(100);
            return _random.Next(100) > 5; // 95% success rate;
        }

        private async Task SetDetectionParametersAsync()
        {
            await Task.Delay(50);
            _logger.LogDebug("Set detection parameters for IR sensor {SensorId}", SensorId);
        }

        private async Task<bool> CheckForInfraredDetectionAsync()
        {
            await Task.Delay(10);

            // Simulate detection based on sensitivity;
            var detectionProbability = _options.Sensitivity * 10; // 0-100%
            return _random.Next(100) < detectionProbability;
        }

        private async Task<double> CalibrateSensitivityAsync()
        {
            await Task.Delay(200);
            return 0.5 + (_random.NextDouble() * 0.5); // 0.5-1.0;
        }

        private async Task<double> CalibrateDetectionThresholdAsync()
        {
            await Task.Delay(200);
            return 100.0 + (_random.NextDouble() * 50.0); // 100-150;
        }

        private async Task<double> CalibrateAmbientCompensationAsync()
        {
            await Task.Delay(200);
            return 0.8 + (_random.NextDouble() * 0.4); // 0.8-1.2;
        }

        private async Task ProcessDetectionAsync(SensorReading reading)
        {
            // Process the detection (e.g., count people, determine direction)
            await Task.Delay(10);
            _logger.LogDebug("IR sensor {SensorId} detected object with confidence {Confidence:F2}",
                SensorId, reading.Confidence);
        }

        private async Task CheckSensorHealthAsync()
        {
            // Periodic health check;
            await Task.Delay(5000); // Check every 5 seconds;

            var healthCheck = _random.Next(1000) < 995; // 99.5% healthy;
            if (!healthCheck)
            {
                Status = SensorStatus.Error;
                _logger.LogWarning("Infrared sensor {SensorId} health check failed", SensorId);
            }
        }
    }

    public class InfraredSensorOptions;
    {
        public int DetectionRange { get; set; } = 5; // meters;
        public int PollingInterval { get; set; } = 100; // ms;
        public int Sensitivity { get; set; } = 8; // 1-10;
        public bool AutoCalibration { get; set; } = true;
        public int BeamCount { get; set; } = 1; // Single or dual beam;
    }

    /// <summary>
    /// Thermal sensor implementation;
    /// </summary>
    public class ThermalSensor : BaseCountingSensor;
    {
        private readonly ThermalSensorOptions _options;
        private DateTime _lastDetection;
        private long _readingCount;

        public override SensorType SensorType => SensorType.Thermal;

        public ThermalSensor(
            string sensorId,
            string sensorName,
            string zoneId,
            ThermalSensorOptions options,
            ILogger<ThermalSensor> logger = null)
            : base(sensorId, sensorName, zoneId, logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            DetectionRange = options.DetectionRange;
        }

        protected override async Task<bool> InitializeSensorAsync()
        {
            _logger.LogDebug("Initializing thermal sensor {SensorId}", SensorId);

            // Simulate hardware initialization;
            await Task.Delay(800);

            // Check if sensor is responding;
            var isResponding = await TestSensorResponseAsync();

            if (!isResponding)
            {
                _logger.LogError("Thermal sensor {SensorId} not responding", SensorId);
                return false;
            }

            // Calibrate to ambient temperature;
            await CalibrateToAmbientAsync();

            return true;
        }

        protected override async Task<SensorReading> GetReadingAsync()
        {
            try
            {
                var reading = new SensorReading;
                {
                    SensorId = SensorId,
                    Timestamp = DateTime.Now,
                    ReadingType = "THERMAL_DETECTION"
                };

                // Simulate thermal detection;
                var temperatureReading = await ReadTemperatureAsync();
                var hasPerson = await DetectPersonFromTemperatureAsync(temperatureReading);

                if (hasPerson)
                {
                    reading.IsValid = true;
                    reading.DetectedObjects = 1;
                    reading.Confidence = 0.90 + (_random.NextDouble() * 0.10); // 90-100%
                    reading.RawData = new Dictionary<string, object>
                    {
                        { "temperature_c", temperatureReading },
                        { "ambient_temp", _options.AmbientTemperature },
                        { "thermal_gradient", temperatureReading - _options.AmbientTemperature },
                        { "pixel_count", 100 + _random.Next(50) }
                    };

                    _lastDetection = DateTime.Now;
                    _readingCount++;
                }
                else;
                {
                    reading.IsValid = false;
                    reading.DetectedObjects = 0;
                    reading.Confidence = 0.0;
                }

                return reading;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reading from thermal sensor {SensorId}", SensorId);
                return new SensorReading;
                {
                    SensorId = SensorId,
                    Timestamp = DateTime.Now,
                    IsValid = false,
                    ErrorMessage = ex.Message;
                };
            }
        }

        protected override async Task<Dictionary<string, object>> PerformCalibrationAsync()
        {
            _logger.LogDebug("Calibrating thermal sensor {SensorId}", SensorId);

            var calibrationData = new Dictionary<string, object>();

            // Calibrate temperature baseline;
            calibrationData["temperature_baseline"] = await CalibrateTemperatureBaselineAsync();

            // Calibrate detection threshold;
            calibrationData["temperature_threshold"] = await CalibrateTemperatureThresholdAsync();

            // Calibrate thermal gradient;
            calibrationData["thermal_gradient"] = await CalibrateThermalGradientAsync();

            // Update accuracy;
            Accuracy = 0.96 + (_random.NextDouble() * 0.04); // 96-100%

            return calibrationData;
        }

        protected override async Task MonitorAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Thermal sensor {SensorId} monitoring started", SensorId);

            while (!cancellationToken.IsCancellationRequested && _isMonitoring)
            {
                try
                {
                    // Get reading;
                    var reading = await GetReadingAsync();

                    if (reading.IsValid && reading.DetectedObjects > 0)
                    {
                        // Process detection;
                        await ProcessDetectionAsync(reading);
                    }

                    // Update ambient temperature periodically;
                    if (DateTime.Now.Minute % 5 == 0)
                    {
                        await UpdateAmbientTemperatureAsync();
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
                    _logger.LogError(ex, "Error in thermal sensor monitoring loop");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            _logger.LogInformation("Thermal sensor {SensorId} monitoring stopped", SensorId);
        }

        protected override DateTime? GetLastReadingTime()
        {
            return _lastDetection;
        }

        protected override long GetReadingCount()
        {
            return _readingCount;
        }

        private async Task<bool> TestSensorResponseAsync()
        {
            await Task.Delay(100);
            return _random.Next(100) > 3; // 97% success rate;
        }

        private async Task CalibrateToAmbientAsync()
        {
            await Task.Delay(300);
            _logger.LogDebug("Calibrated thermal sensor to ambient temperature");
        }

        private async Task<double> ReadTemperatureAsync()
        {
            await Task.Delay(20);

            // Simulate temperature reading;
            var baseTemp = _options.AmbientTemperature;

            // Add random variation;
            var variation = (_random.NextDouble() - 0.5) * 2.0; // -1 to +1;
            var hasPerson = _random.Next(100) < 40; // 40% chance of detecting a person;

            if (hasPerson)
            {
                return baseTemp + _options.TemperatureThreshold + variation;
            }

            return baseTemp + variation;
        }

        private async Task<bool> DetectPersonFromTemperatureAsync(double temperature)
        {
            await Task.Delay(10);

            // Detect person based on temperature difference from ambient;
            var temperatureDifference = temperature - _options.AmbientTemperature;
            return temperatureDifference >= _options.TemperatureThreshold;
        }

        private async Task<double> CalibrateTemperatureBaselineAsync()
        {
            await Task.Delay(300);
            return _options.AmbientTemperature + (_random.NextDouble() * 2.0);
        }

        private async Task<double> CalibrateTemperatureThresholdAsync()
        {
            await Task.Delay(300);
            return 2.0 + (_random.NextDouble() * 3.0); // 2-5 degrees;
        }

        private async Task<double> CalibrateThermalGradientAsync()
        {
            await Task.Delay(300);
            return 1.5 + (_random.NextDouble() * 1.5); // 1.5-3.0;
        }

        private async Task ProcessDetectionAsync(SensorReading reading)
        {
            // Process the thermal detection;
            await Task.Delay(10);
            _logger.LogDebug("Thermal sensor {SensorId} detected person with confidence {Confidence:F2}",
                SensorId, reading.Confidence);
        }

        private async Task UpdateAmbientTemperatureAsync()
        {
            // Simulate ambient temperature update;
            await Task.Delay(100);
            _options.AmbientTemperature = 20.0 + (_random.NextDouble() * 5.0); // 20-25°C;
        }
    }

    public class ThermalSensorOptions;
    {
        public int DetectionRange { get; set; } = 10; // meters;
        public int PollingInterval { get; set; } = 200; // ms;
        public double TemperatureThreshold { get; set; } = 3.0; // °C above ambient;
        public double AmbientTemperature { get; set; } = 22.0; // °C;
        public int ThermalResolution { get; set; } = 32; // 32x32 pixels;
        public double FieldOfView { get; set; } = 60.0; // degrees;
    }

    #endregion;

    #region Algorithm Implementations;

    /// <summary>
    /// Simple threshold-based detection algorithm;
    /// </summary>
    public class SimpleThresholdAlgorithm : IPeopleDetectionAlgorithm;
    {
        public string AlgorithmId => "SIMPLE_THRESHOLD";
        public string AlgorithmName => "Simple Threshold Detection";
        public DetectionMethod DetectionMethod => DetectionMethod.Threshold;
        public double ConfidenceThreshold { get; set; } = 0.75;

        public Task<DetectionResult> DetectPeopleAsync(byte[] sensorData)
        {
            // Simplified implementation;
            var result = new DetectionResult;
            {
                AlgorithmId = AlgorithmId,
                Timestamp = DateTime.Now,
                DetectedPeople = 0,
                Confidence = 0.0;
            };

            // Simulate detection;
            var random = new Random();
            var hasDetection = random.Next(100) < 60; // 60% chance;

            if (hasDetection)
            {
                result.DetectedPeople = 1;
                result.Confidence = 0.8 + (random.NextDouble() * 0.2); // 80-100%
                result.IsValid = true;
            }
            else;
            {
                result.IsValid = false;
            }

            return Task.FromResult(result);
        }

        public Task<CalibrationResult> CalibrateAsync(byte[] calibrationData)
        {
            var result = new CalibrationResult;
            {
                Status = CalibrationStatus.Completed,
                Message = "Simple threshold algorithm calibrated",
                Accuracy = 0.85;
            };

            return Task.FromResult(result);
        }

        public Task<AlgorithmMetrics> GetMetricsAsync()
        {
            var metrics = new AlgorithmMetrics;
            {
                AlgorithmId = AlgorithmId,
                ProcessingTimeMs = 10.5,
                Accuracy = 0.85,
                FalsePositiveRate = 0.05,
                FalseNegativeRate = 0.10,
                TotalDetections = 1000,
                LastUpdated = DateTime.Now;
            };

            return Task.FromResult(metrics);
        }
    }

    /// <summary>
    /// Motion detection algorithm;
    /// </summary>
    public class MotionDetectionAlgorithm : IPeopleDetectionAlgorithm;
    {
        public string AlgorithmId => "MOTION_DETECTION";
        public string AlgorithmName => "Motion Detection";
        public DetectionMethod DetectionMethod => DetectionMethod.Motion;
        public double ConfidenceThreshold { get; set; } = 0.70;

        public Task<DetectionResult> DetectPeopleAsync(byte[] sensorData)
        {
            var result = new DetectionResult;
            {
                AlgorithmId = AlgorithmId,
                Timestamp = DateTime.Now;
            };

            // Simulate motion detection;
            var random = new Random();
            var motionLevel = random.NextDouble();

            if (motionLevel > 0.3) // 70% chance of detection;
            {
                result.DetectedPeople = random.Next(1, 5); // 1-4 people;
                result.Confidence = 0.7 + (motionLevel * 0.3); // 70-100%
                result.IsValid = true;
                result.Metadata = new Dictionary<string, object>
                {
                    { "motion_level", motionLevel },
                    { "motion_area", random.Next(10, 100) }
                };
            }
            else;
            {
                result.IsValid = false;
                result.Confidence = motionLevel;
            }

            return Task.FromResult(result);
        }

        public Task<CalibrationResult> CalibrateAsync(byte[] calibrationData)
        {
            var result = new CalibrationResult;
            {
                Status = CalibrationStatus.Completed,
                Message = "Motion detection algorithm calibrated",
                Accuracy = 0.90;
            };

            return Task.FromResult(result);
        }

        public Task<AlgorithmMetrics> GetMetricsAsync()
        {
            var metrics = new AlgorithmMetrics;
            {
                AlgorithmId = AlgorithmId,
                ProcessingTimeMs = 25.0,
                Accuracy = 0.90,
                FalsePositiveRate = 0.03,
                FalseNegativeRate = 0.05,
                TotalDetections = 2000,
                LastUpdated = DateTime.Now;
            };

            return Task.FromResult(metrics);
        }
    }

    /// <summary>
    /// Thermal detection algorithm;
    /// </summary>
    public class ThermalDetectionAlgorithm : IPeopleDetectionAlgorithm;
    {
        public string AlgorithmId => "THERMAL_DETECTION";
        public string AlgorithmName => "Thermal Detection";
        public DetectionMethod DetectionMethod => DetectionMethod.Thermal;
        public double ConfidenceThreshold { get; set; } = 0.80;

        public Task<DetectionResult> DetectPeopleAsync(byte[] sensorData)
        {
            var result = new DetectionResult;
            {
                AlgorithmId = AlgorithmId,
                Timestamp = DateTime.Now;
            };

            // Simulate thermal detection;
            var random = new Random();
            var hasHeatSignature = random.Next(100) < 75; // 75% chance;

            if (hasHeatSignature)
            {
                result.DetectedPeople = random.Next(1, 3); // 1-2 people;
                result.Confidence = 0.85 + (random.NextDouble() * 0.15); // 85-100%
                result.IsValid = true;
                result.Metadata = new Dictionary<string, object>
                {
                    { "temperature_avg", 32.0 + random.NextDouble() * 3.0 },
                    { "heat_sources", result.DetectedPeople }
                };
            }
            else;
            {
                result.IsValid = false;
                result.Confidence = random.NextDouble() * 0.3; // 0-30%
            }

            return Task.FromResult(result);
        }

        public Task<CalibrationResult> CalibrateAsync(byte[] calibrationData)
        {
            var result = new CalibrationResult;
            {
                Status = CalibrationStatus.Completed,
                Message = "Thermal detection algorithm calibrated",
                Accuracy = 0.95;
            };

            return Task.FromResult(result);
        }

        public Task<AlgorithmMetrics> GetMetricsAsync()
        {
            var metrics = new AlgorithmMetrics;
            {
                AlgorithmId = AlgorithmId,
                ProcessingTimeMs = 50.0,
                Accuracy = 0.95,
                FalsePositiveRate = 0.02,
                FalseNegativeRate = 0.03,
                TotalDetections = 1500,
                LastUpdated = DateTime.Now;
            };

            return Task.FromResult(metrics);
        }
    }

    #endregion;

    #region Models;

    /// <summary>
    /// Counting zone configuration;
    /// </summary>
    public class CountingZone;
    {
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public string Description { get; set; }
        public double AreaSqm { get; set; }
        public int MaxCapacity { get; set; }
        public int CapacityWarningThreshold { get; set; } = 80; // Percentage;
        public List<EntryRestriction> EntryRestrictions { get; set; }
        public List<AccessHours> AccessHours { get; set; }
        public bool IsActive { get; set; } = true;
        public bool AutoCreateSensors { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastModified { get; set; }

        public CountingZone()
        {
            EntryRestrictions = new List<EntryRestriction>();
            AccessHours = new List<AccessHours>();
            CreatedAt = DateTime.Now;
        }
    }

    /// <summary>
    /// Zone count information;
    /// </summary>
    public class ZoneCount;
    {
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public int CurrentCount { get; set; }
        public int MaxCapacity { get; set; }
        public double AreaSqm { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime? LastEntryTime { get; set; }
        public DateTime? LastExitTime { get; set; }
        public int EntriesToday { get; set; }
        public int ExitsToday { get; set; }
        public int PeakCountToday { get; set; }
        public DateTime? PeakTimeToday { get; set; }
        public int TotalEntries { get; set; }
        public int TotalExits { get; set; }
        public TimeSpan TotalDwellTime { get; set; }
        public TimeSpan AverageDwellTime { get; set; }
        public TimeSpan MinDwellTime { get; set; }
        public TimeSpan MaxDwellTime { get; set; }
        public int TotalVisits { get; set; }
        public double CalibrationAccuracy { get; set; }
        public DateTime? LastCalibration { get; set; }
        public bool IsActive { get; set; } = true;
        public Dictionary<int, int> HourlyCounts { get; set; }

        public ZoneCount()
        {
            HourlyCounts = new Dictionary<int, int>();
        }
    }

    /// <summary>
    /// Count result;
    /// </summary>
    public class CountResult;
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public ZoneCount CurrentCount { get; set; }
        public MovementRecord MovementRecord { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Person information;
    /// </summary>
    public class PersonInfo;
    {
        public string PersonId { get; set; }
        public int? Age { get; set; }
        public Gender? Gender { get; set; }
        public bool IsMember { get; set; }
        public string MembershipId { get; set; }
        public string DetectionAlgorithmId { get; set; }
        public double DetectionConfidence { get; set; }
        public bool IsManualEntry { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public PersonInfo()
        {
            Metadata = new Dictionary<string, object>();
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Movement record;
    /// </summary>
    public class MovementRecord;
    {
        public string RecordId { get; set; }
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public MovementDirection Direction { get; set; }
        public DateTime Timestamp { get; set; }
        public PersonInfo PersonInfo { get; set; }
        public int PreviousCount { get; set; }
        public int NewCount { get; set; }
        public double? DetectionConfidence { get; set; }
        public string AlgorithmId { get; set; }
        public bool IsManual { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public MovementRecord()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Occupancy statistics;
    /// </summary>
    public class OccupancyStatistics;
    {
        public DateTime GeneratedAt { get; set; }
        public string TimeRange { get; set; }
        public int TotalZones { get; set; }
        public int TotalPeople { get; set; }
        public int TotalCapacity { get; set; }
        public double OverallOccupancyPercentage { get; set; }
        public int MaxOccupancy { get; set; }
        public string MaxOccupiedZone { get; set; }
        public int TotalEntriesToday { get; set; }
        public int TotalExitsToday { get; set; }
        public List<ZoneStatistics> ZoneStatistics { get; set; }

        public OccupancyStatistics()
        {
            ZoneStatistics = new List<ZoneStatistics>();
        }
    }

    /// <summary>
    /// Zone statistics;
    /// </summary>
    public class ZoneStatistics;
    {
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public int CurrentCount { get; set; }
        public int MaxCapacity { get; set; }
        public double OccupancyPercentage { get; set; }
        public int EntriesToday { get; set; }
        public int ExitsToday { get; set; }
        public int PeakCountToday { get; set; }
        public DateTime? PeakTimeToday { get; set; }
        public TimeSpan AverageDwellTime { get; set; }
        public bool IsOverCapacity { get; set; }
    }

    /// <summary>
    /// People density information;
    /// </summary>
    public class PeopleDensity;
    {
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public DateTime Timestamp { get; set; }
        public int PeopleCount { get; set; }
        public double AreaSqm { get; set; }
        public double DensityPerSqm { get; set; }
        public double AveragePersonalSpaceSqm { get; set; }
        public DensityLevel DensityLevel { get; set; }
        public string Recommendation { get; set; }
    }

    /// <summary>
    /// Peak hours analysis;
    /// </summary>
    public class PeakHoursAnalysis;
    {
        public string ZoneId { get; set; }
        public int AnalysisPeriodDays { get; set; }
        public DateTime AnalysisDate { get; set; }
        public int PeakEntryHour { get; set; }
        public double PeakEntryAverage { get; set; }
        public int PeakExitHour { get; set; }
        public double PeakExitAverage { get; set; }
        public string BusiestDay { get; set; }
        public string Message { get; set; }
        public List<HourlyAverage> HourlyAverages { get; set; }
        public List<string> Recommendations { get; set; }

        public PeakHoursAnalysis()
        {
            HourlyAverages = new List<HourlyAverage>();
            Recommendations = new List<string>();
        }
    }

    /// <summary>
    /// Hourly average data;
    /// </summary>
    public class HourlyAverage;
    {
        public int Hour { get; set; }
        public double AverageEntries { get; set; }
        public double AverageExits { get; set; }
        public int TotalOccurrences { get; set; }
    }

    /// <summary>
    /// Counter diagnostics;
    /// </summary>
    public class CounterDiagnostics;
    {
        public string SystemId { get; set; }
        public string SystemName { get; set; }
        public bool IsRunning { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime { get; set; }
        public int TotalZones { get; set; }
        public int ActiveZones { get; set; }
        public int TotalSensors { get; set; }
        public int ActiveSensors { get; set; }
        public int TotalAlgorithms { get; set; }
        public int MovementHistorySize { get; set; }
        public long MemoryUsage { get; set; }
        public DateTime GeneratedAt { get; set; }
        public Dictionary<string, int> SensorStatusBreakdown { get; set; }
        public List<ZoneOccupancySummary> ZoneOccupancySummary { get; set; }
        public List<SystemIssue> Issues { get; set; }

        public CounterDiagnostics()
        {
            SensorStatusBreakdown = new Dictionary<string, int>();
            ZoneOccupancySummary = new List<ZoneOccupancySummary>();
            Issues = new List<SystemIssue>();
        }
    }

    /// <summary>
    /// Zone occupancy summary;
    /// </summary>
    public class ZoneOccupancySummary;
    {
        public string ZoneId { get; set; }
        public string ZoneName { get; set; }
        public int CurrentCount { get; set; }
        public int MaxCapacity { get; set; }
        public double OccupancyPercentage { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// System issue;
    /// </summary>
    public class SystemIssue;
    {
        public IssueType IssueType { get; set; }
        public string SourceId { get; set; }
        public string Description { get; set; }
        public IssueSeverity Severity { get; set; }
        public DateTime DetectedAt { get; set; }
        public string Resolution { get; set; }
    }

    /// <summary>
    /// Sensor reading;
    /// </summary>
    public class SensorReading;
    {
        public string SensorId { get; set; }
        public DateTime Timestamp { get; set; }
        public string ReadingType { get; set; }
        public bool IsValid { get; set; }
        public int DetectedObjects { get; set; }
        public double Confidence { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> RawData { get; set; }

        public SensorReading()
        {
            RawData = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Sensor diagnostics;
    /// </summary>
    public class SensorDiagnostics;
    {
        public string SensorId { get; set; }
        public string SensorName { get; set; }
        public string SensorType { get; set; }
        public string ZoneId { get; set; }
        public SensorStatus Status { get; set; }
        public bool IsMonitoring { get; set; }
        public TimeSpan Uptime { get; set; }
        public double Accuracy { get; set; }
        public int DetectionRange { get; set; }
        public SensorHealthStatus HealthStatus { get; set; }
        public double? BatteryLevel { get; set; }
        public double? SignalStrength { get; set; }
        public DateTime? LastReadingTime { get; set; }
        public long ReadingCount { get; set; }
        public Dictionary<string, object> AdditionalMetrics { get; set; }

        public SensorDiagnostics()
        {
            AdditionalMetrics = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Detection result;
    /// </summary>
    public class DetectionResult;
    {
        public string AlgorithmId { get; set; }
        public DateTime Timestamp { get; set; }
        public int DetectedPeople { get; set; }
        public double Confidence { get; set; }
        public bool IsValid { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public DetectionResult()
        {
            Metadata = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Algorithm metrics;
    /// </summary>
    public class AlgorithmMetrics;
    {
        public string AlgorithmId { get; set; }
        public double ProcessingTimeMs { get; set; }
        public double Accuracy { get; set; }
        public double FalsePositiveRate { get; set; }
        public double FalseNegativeRate { get; set; }
        public long TotalDetections { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Calibration result;
    /// </summary>
    public class CalibrationResult;
    {
        public string ZoneId { get; set; }
        public string SensorId { get; set; }
        public string CalibrationId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public CalibrationStatus Status { get; set; }
        public string Message { get; set; }
        public double? Accuracy { get; set; }
        public double? AverageAccuracy { get; set; }
        public string ErrorDetails { get; set; }
        public Dictionary<string, object> CalibrationData { get; set; }
        public List<SensorCalibrationResult> SensorResults { get; set; }

        public CalibrationResult()
        {
            CalibrationData = new Dictionary<string, object>();
            SensorResults = new List<SensorCalibrationResult>();
        }
    }

    /// <summary>
    /// Sensor calibration result;
    /// </summary>
    public class SensorCalibrationResult;
    {
        public string SensorId { get; set; }
        public string SensorName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public CalibrationStatus Status { get; set; }
        public string Message { get; set; }
        public double? Accuracy { get; set; }
        public string ErrorDetails { get; set; }
        public Dictionary<string, object> CalibrationData { get; set; }
    }

    /// <summary>
    /// Access hours for a zone;
    /// </summary>
    public class AccessHours;
    {
        public DayOfWeek DayOfWeek { get; set; }
        public TimeSpan OpenTime { get; set; }
        public TimeSpan CloseTime { get; set; }
        public bool Is24Hours { get; set; }
    }

    /// <summary>
    /// Validation result;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }

        public static ValidationResult Success() => new ValidationResult { IsValid = true };
        public static ValidationResult Failure(string errorMessage) =>
            new ValidationResult { IsValid = false, ErrorMessage = errorMessage };
    }

    /// <summary>
    /// People counter options;
    /// </summary>
    public class PeopleCounterOptions;
    {
        public string SystemId { get; set; } = Guid.NewGuid().ToString();
        public string SystemName { get; set; } = "People Counting System";
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan AnalyticsUpdateInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan HistoryCleanupInterval { get; set; } = TimeSpan.FromHours(1);
        public int MaxHistorySize { get; set; } = 100000;
        public int HistoryRetentionDays { get; set; } = 90;
        public int CalibrationWarningDays { get; set; } = 30;
        public double HighDensityThreshold { get; set; } = 0.5; // People per square meter;
        public double MediumDensityThreshold { get; set; } = 0.2; // People per square meter;
        public bool EnableAutomaticCalibration { get; set; } = true;
        public int DefaultZoneCapacity { get; set; } = 100;
        public double DefaultZoneArea { get; set; } = 100.0; // Square meters;
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Movement direction;
    /// </summary>
    public enum MovementDirection;
    {
        Enter = 0,
        Exit = 1;
    }

    /// <summary>
    /// Sensor types;
    /// </summary>
    public enum SensorType;
    {
        Infrared = 0,
        Thermal = 1,
        Ultrasonic = 2,
        Laser = 3,
        Camera = 4,
        Pressure = 5,
        Radar = 6,
        Lidar = 7,
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
        Calibrating = 3,
        Error = 4,
        Offline = 5,
        NeedsCalibration = 6,
        Maintenance = 7;
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
    /// Calibration status;
    /// </summary>
    public enum CalibrationStatus;
    {
        NotCalibrated = 0,
        InProgress = 1,
        Completed = 2,
        Failed = 3,
        Partial = 4;
    }

    /// <summary>
    /// Detection method;
    /// </summary>
    public enum DetectionMethod;
    {
        Threshold = 0,
        Motion = 1,
        Thermal = 2,
        ComputerVision = 3,
        DeepLearning = 4,
        Hybrid = 5;
    }

    /// <summary>
    /// Entry restrictions;
    /// </summary>
    public enum EntryRestriction;
    {
        None = 0,
        AdultsOnly = 1,
        ChildrenOnly = 2,
        MembersOnly = 3,
        StaffOnly = 4,
        TicketRequired = 5,
        ReservationRequired = 6;
    }

    /// <summary>
    /// Gender;
    /// </summary>
    public enum Gender;
    {
        Unknown = 0,
        Male = 1,
        Female = 2,
        Other = 3;
    }

    /// <summary>
    /// Density level;
    /// </summary>
    public enum DensityLevel;
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        VeryHigh = 4,
        Critical = 5;
    }

    /// <summary>
    /// Issue type;
    /// </summary>
    public enum IssueType;
    {
        SensorFailure = 0,
        CapacityExceeded = 1,
        NeedsCalibration = 2,
        CommunicationError = 3,
        PowerIssue = 4,
        EnvironmentalIssue = 5,
        Other = 99;
    }

    /// <summary>
    /// Issue severity;
    /// </summary>
    public enum IssueSeverity;
    {
        Info = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4;
    }

    #endregion;

    #region Service Registration;

    /// <summary>
    /// Service collection extensions for PeopleCounter;
    /// </summary>
    public static class PeopleCounterServiceCollectionExtensions;
    {
        public static IServiceCollection AddPeopleCounter(
            this IServiceCollection services,
            Action<PeopleCounterOptions> configureOptions = null)
        {
            var options = new PeopleCounterOptions();
            configureOptions?.Invoke(options);

            services.AddSingleton(Options.Create(options));
            services.AddSingleton<IPeopleCounter, PeopleCounter>();

            // Register default sensors and algorithms;
            services.AddSingleton<SimpleThresholdAlgorithm>();
            services.AddSingleton<MotionDetectionAlgorithm>();
            services.AddSingleton<ThermalDetectionAlgorithm>();

            return services;
        }

        public static IServiceCollection AddInfraredSensor(
            this IServiceCollection services,
            string sensorId,
            string sensorName,
            string zoneId,
            Action<InfraredSensorOptions> configureOptions)
        {
            var options = new InfraredSensorOptions();
            configureOptions.Invoke(options);

            services.AddSingleton(options);
            services.AddSingleton<ICountingSensor>(sp =>
                new InfraredSensor(
                    sensorId,
                    sensorName,
                    zoneId,
                    options,
                    sp.GetRequiredService<ILogger<InfraredSensor>>()));

            return services;
        }

        public static IServiceCollection AddThermalSensor(
            this IServiceCollection services,
            string sensorId,
            string sensorName,
            string zoneId,
            Action<ThermalSensorOptions> configureOptions)
        {
            var options = new ThermalSensorOptions();
            configureOptions.Invoke(options);

            services.AddSingleton(options);
            services.AddSingleton<ICountingSensor>(sp =>
                new ThermalSensor(
                    sensorId,
                    sensorName,
                    zoneId,
                    options,
                    sp.GetRequiredService<ILogger<ThermalSensor>>()));

            return services;
        }
    }

    #endregion;

    #region Usage Examples;

    /// <summary>
    /// Example usage of PeopleCounter;
    /// </summary>
    public static class PeopleCounterExamples;
    {
        public static async Task ExampleUsage()
        {
            // Create logger;
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
            });

            var logger = loggerFactory.CreateLogger<PeopleCounter>();

            // Create people counter;
            var options = new PeopleCounterOptions;
            {
                SystemName = "Shopping Mall People Counter",
                MaxHistorySize = 50000,
                HighDensityThreshold = 0.4,
                MediumDensityThreshold = 0.15;
            };

            var peopleCounter = new PeopleCounter(logger, Options.Create(options));

            // Create event handler for logging;
            var eventHandler = new LoggingPeopleCountEventHandler(
                loggerFactory.CreateLogger<LoggingPeopleCountEventHandler>());

            peopleCounter.SubscribeToEvents(eventHandler);

            // Start the system;
            await peopleCounter.StartMonitoringAsync();

            try
            {
                // Configure zones;
                var entranceZone = new CountingZone;
                {
                    ZoneId = "ENTRANCE",
                    ZoneName = "Main Entrance",
                    AreaSqm = 50.0,
                    MaxCapacity = 200,
                    CapacityWarningThreshold = 85,
                    IsActive = true,
                    AutoCreateSensors = true;
                };

                await peopleCounter.ConfigureZoneAsync(entranceZone);

                var foodCourtZone = new CountingZone;
                {
                    ZoneId = "FOOD_COURT",
                    ZoneName = "Food Court",
                    AreaSqm = 300.0,
                    MaxCapacity = 500,
                    CapacityWarningThreshold = 80,
                    EntryRestrictions = new List<EntryRestriction> { EntryRestriction.None },
                    IsActive = true,
                    AutoCreateSensors = true;
                };

                await peopleCounter.ConfigureZoneAsync(foodCourtZone);

                // Simulate people movement;
                for (int i = 0; i < 50; i++)
                {
                    await peopleCounter.PersonEnteredAsync("ENTRANCE", new PersonInfo;
                    {
                        PersonId = $"CUST-{i:000}",
                        IsManualEntry = false,
                        DetectionAlgorithmId = "THERMAL_DETECTION",
                        DetectionConfidence = 0.92;
                    });

                    await Task.Delay(100);
                }

                // Get current counts;
                var entranceCount = await peopleCounter.GetZoneCountAsync("ENTRANCE");
                Console.WriteLine($"Entrance count: {entranceCount.CurrentCount}/{entranceCount.MaxCapacity}");

                // Get occupancy statistics;
                var stats = await peopleCounter.GetOccupancyStatisticsAsync();
                Console.WriteLine($"Total people in system: {stats.TotalPeople}");
                Console.WriteLine($"Overall occupancy: {stats.OverallOccupancyPercentage:F1}%");

                // Get density information;
                var density = await peopleCounter.GetPeopleDensityAsync("ENTRANCE");
                Console.WriteLine($"Entrance density: {density.DensityPerSqm:F2} people/sq.m ({density.DensityLevel})");

                // Simulate some exits;
                for (int i = 0; i < 20; i++)
                {
                    await peopleCounter.PersonExitedAsync("ENTRANCE", new PersonInfo;
                    {
                        PersonId = $"CUST-{i:000}",
                        IsManualEntry = false;
                    });

                    await Task.Delay(50);
                }

                // Get updated count;
                entranceCount = await peopleCounter.GetZoneCountAsync("ENTRANCE");
                Console.WriteLine($"Updated entrance count: {entranceCount.CurrentCount}");

                // Get movement history;
                var history = await peopleCounter.GetMovementHistoryAsync("ENTRANCE", limit: 10);
                Console.WriteLine($"Recent movements: {history.Count()} records");

                // Perform calibration;
                var calibrationResult = await peopleCounter.CalibrateAsync("ENTRANCE");
                Console.WriteLine($"Calibration result: {calibrationResult.Status}");

                // Get diagnostics;
                var diagnostics = await peopleCounter.GetDiagnosticsAsync();
                Console.WriteLine($"System diagnostics: {diagnostics.TotalZones} zones, {diagnostics.TotalSensors} sensors");

                // Get peak hours analysis;
                var peakAnalysis = await peopleCounter.GetPeakHoursAnalysisAsync("ENTRANCE", 7);
                Console.WriteLine($"Peak entry hour: {peakAnalysis.PeakEntryHour}:00");

                // Wait for monitoring;
                await Task.Delay(TimeSpan.FromMinutes(2));
            }
            finally
            {
                // Stop the system;
                await peopleCounter.StopMonitoringAsync();
                peopleCounter.Dispose();
            }
        }

        /// <summary>
        /// Example event handler;
        /// </summary>
        public class LoggingPeopleCountEventHandler : IPeopleCountEventHandler;
        {
            private readonly ILogger<LoggingPeopleCountEventHandler> _logger;

            public LoggingPeopleCountEventHandler(ILogger<LoggingPeopleCountEventHandler> logger)
            {
                _logger = logger;
            }

            public Task OnPersonEnteredAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount)
            {
                _logger.LogInformation(
                    "Person entered zone {ZoneId}: {PersonId}. Count: {CurrentCount}/{MaxCapacity}",
                    zoneId, personInfo?.PersonId ?? "Unknown", currentCount.CurrentCount, currentCount.MaxCapacity);

                return Task.CompletedTask;
            }

            public Task OnPersonExitedAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount)
            {
                _logger.LogInformation(
                    "Person exited zone {ZoneId}: {PersonId}. Count: {CurrentCount}/{MaxCapacity}",
                    zoneId, personInfo?.PersonId ?? "Unknown", currentCount.CurrentCount, currentCount.MaxCapacity);

                return Task.CompletedTask;
            }

            public Task OnZoneCapacityExceededAsync(string zoneId, ZoneCount currentCount, int capacityLimit)
            {
                _logger.LogWarning(
                    "Zone {ZoneId} capacity exceeded: {CurrentCount}/{CapacityLimit} ({Percentage:F1}%)",
                    zoneId, currentCount.CurrentCount, capacityLimit,
                    (currentCount.CurrentCount * 100.0 / capacityLimit));

                return Task.CompletedTask;
            }

            public Task OnZoneOccupancyChangedAsync(string zoneId, ZoneCount oldCount, ZoneCount newCount)
            {
                _logger.LogDebug(
                    "Zone {ZoneId} occupancy changed: {OldCount} -> {NewCount} (Δ{Delta})",
                    zoneId, oldCount.CurrentCount, newCount.CurrentCount,
                    newCount.CurrentCount - oldCount.CurrentCount);

                return Task.CompletedTask;
            }

            public Task OnCountingErrorAsync(string zoneId, Exception error)
            {
                _logger.LogError(
                    error,
                    "Counting error in zone {ZoneId}",
                    zoneId);

                return Task.CompletedTask;
            }

            public Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus)
            {
                _logger.LogInformation(
                    "Sensor {SensorId} status changed: {OldStatus} -> {NewStatus}",
                    sensorId, oldStatus, newStatus);

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Example occupancy management system;
        /// </summary>
        public class OccupancyManagementSystem : IPeopleCountEventHandler;
        {
            private readonly ILogger<OccupancyManagementSystem> _logger;
            private readonly INotificationService _notificationService;
            private readonly IDisplaySystem _displaySystem;

            public OccupancyManagementSystem(
                ILogger<OccupancyManagementSystem> logger,
                INotificationService notificationService,
                IDisplaySystem displaySystem)
            {
                _logger = logger;
                _notificationService = notificationService;
                _displaySystem = displaySystem;
            }

            public async Task OnPersonEnteredAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount)
            {
                // Update display;
                await _displaySystem.UpdateZoneDisplayAsync(zoneId, currentCount);

                // Check capacity warnings;
                if (currentCount.MaxCapacity > 0)
                {
                    var occupancyPercentage = (currentCount.CurrentCount * 100.0) / currentCount.MaxCapacity;

                    if (occupancyPercentage >= 90)
                    {
                        await _notificationService.SendAlertAsync(
                            "Security",
                            $"High Occupancy Alert - {zoneId}",
                            $"Zone {zoneId} is at {occupancyPercentage:F1}% capacity");
                    }
                }
            }

            public async Task OnPersonExitedAsync(string zoneId, PersonInfo personInfo, ZoneCount currentCount)
            {
                // Update display;
                await _displaySystem.UpdateZoneDisplayAsync(zoneId, currentCount);
            }

            public async Task OnZoneCapacityExceededAsync(string zoneId, ZoneCount currentCount, int capacityLimit)
            {
                // Critical alert;
                await _notificationService.SendCriticalAlertAsync(
                    "Security",
                    $"CAPACITY EXCEEDED - {zoneId}",
                    $"Zone {zoneId} has exceeded capacity: {currentCount.CurrentCount}/{capacityLimit}");

                // Update display to show warning;
                await _displaySystem.ShowWarningAsync(zoneId, "CAPACITY EXCEEDED");

                _logger.LogCritical(
                    "Zone {ZoneId} capacity exceeded: {Current}/{Max}",
                    zoneId, currentCount.CurrentCount, capacityLimit);
            }

            public Task OnZoneOccupancyChangedAsync(string zoneId, ZoneCount oldCount, ZoneCount newCount)
            {
                // Log occupancy change;
                _logger.LogInformation(
                    "Zone {ZoneId} occupancy: {Old} -> {New}",
                    zoneId, oldCount.CurrentCount, newCount.CurrentCount);

                return Task.CompletedTask;
            }

            public Task OnCountingErrorAsync(string zoneId, Exception error)
            {
                _logger.LogError(
                    error,
                    "Counting error in zone {ZoneId}",
                    zoneId);

                return Task.CompletedTask;
            }

            public Task OnSensorStatusChangedAsync(string sensorId, SensorStatus oldStatus, SensorStatus newStatus)
            {
                if (newStatus == SensorStatus.Error || newStatus == SensorStatus.Offline)
                {
                    _logger.LogWarning(
                        "Sensor {SensorId} is {Status}",
                        sensorId, newStatus);
                }

                return Task.CompletedTask;
            }
        }

        // Example interfaces for dependencies;
        public interface INotificationService;
        {
            Task SendAlertAsync(string recipient, string subject, string message);
            Task SendCriticalAlertAsync(string recipient, string subject, string message);
        }

        public interface IDisplaySystem;
        {
            Task UpdateZoneDisplayAsync(string zoneId, ZoneCount count);
            Task ShowWarningAsync(string zoneId, string message);
        }
    }

    #endregion;
}
