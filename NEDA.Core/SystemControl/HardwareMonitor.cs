using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NEDA.Core.SystemControl
{
    /// <summary>
    /// Sistem donanımını gerçek zamanlı olarak izleyen gelişmiş monitoring sistemi.
    /// CPU, RAM, Disk, GPU, Ağ ve diğer donanım bileşenlerini izler.
    /// </summary>
    public class HardwareMonitor : IHardwareMonitor, IDisposable
    {
        private readonly ILogger<HardwareMonitor> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly HardwareMonitorConfig _config;

        // Performance counters;
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _ramCounter;
        private PerformanceCounter _diskReadCounter;
        private PerformanceCounter _diskWriteCounter;
        private PerformanceCounter _networkSentCounter;
        private PerformanceCounter _networkReceivedCounter;

        // Monitoring data;
        private readonly ConcurrentDictionary<string, HardwareMetric> _metrics;
        private readonly ConcurrentDictionary<string, HardwareAlert> _activeAlerts;
        private readonly ConcurrentQueue<HardwareEvent> _eventQueue;
        private readonly ConcurrentDictionary<string, HardwareComponent> _components;

        // Timers and cancellation;
        private readonly Timer _monitoringTimer;
        private readonly Timer _alertCheckTimer;
        private readonly Timer _healthCheckTimer;
        private CancellationTokenSource _monitoringCts;
        private bool _isMonitoring;
        private readonly object _syncLock = new object();

        // Historical data;
        private readonly FixedSizeQueue<HardwareSnapshot> _snapshots;
        private readonly FixedSizeQueue<PerformanceTrend> _performanceTrends;

        // WMI management;
        private ManagementScope _wmiScope;
        private bool _wmiInitialized;

        // External sensors;
        private readonly List<IHardwareSensor> _externalSensors;
        private readonly List<IHardwareProvider> _hardwareProviders;

        /// <summary>
        /// HardwareMonitor constructor;
        /// </summary>
        public HardwareMonitor(
            ILogger<HardwareMonitor> logger,
            IAuditLogger auditLogger,
            IOptions<HardwareMonitorConfig> config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));

            _metrics = new ConcurrentDictionary<string, HardwareMetric>();
            _activeAlerts = new ConcurrentDictionary<string, HardwareAlert>();
            _eventQueue = new ConcurrentQueue<HardwareEvent>();
            _components = new ConcurrentDictionary<string, HardwareComponent>();

            _snapshots = new FixedSizeQueue<HardwareSnapshot>(_config.MaxSnapshots);
            _performanceTrends = new FixedSizeQueue<PerformanceTrend>(_config.MaxTrends);

            _externalSensors = new List<IHardwareSensor>();
            _hardwareProviders = new List<IHardwareProvider>();

            // Initialize timers;
            _monitoringTimer = new Timer(MonitoringCallback, null, Timeout.Infinite, Timeout.Infinite);
            _alertCheckTimer = new Timer(AlertCheckCallback, null, Timeout.Infinite, Timeout.Infinite);
            _healthCheckTimer = new Timer(HealthCheckCallback, null, Timeout.Infinite, Timeout.Infinite);

            _monitoringCts = new CancellationTokenSource();

            Initialize();
        }

        public HardwareMonitor()
        {
        }

        /// <summary>
        /// Monitor'ü başlatır;
        /// </summary>
        public async Task StartMonitoringAsync()
        {
            lock (_syncLock)
            {
                if (_isMonitoring)
                {
                    _logger.LogWarning("Hardware monitor is already running");
                    return;
                }

                _isMonitoring = true;
                _monitoringCts = new CancellationTokenSource();
            }

            try
            {
                InitializePerformanceCounters();
                InitializeWmi();
                await DiscoverHardwareAsync();

                // Start timers;
                _monitoringTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(_config.MonitoringIntervalMs));
                _alertCheckTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(_config.AlertCheckIntervalSeconds));
                _healthCheckTimer.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(_config.HealthCheckIntervalMinutes));

                // Start external sensors;
                await StartExternalSensorsAsync();

                // Start monitoring tasks;
                _ = Task.Run(() => MonitorHardwareLoopAsync(_monitoringCts.Token));
                _ = Task.Run(() => ProcessEventsLoopAsync(_monitoringCts.Token));

                await _auditLogger.LogSystemEventAsync(
                    "HARDWARE_MONITOR_STARTED",
                    $"Hardware monitoring started with interval: {_config.MonitoringIntervalMs}ms",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Hardware monitor started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start hardware monitor");
                throw new HardwareMonitorException(ErrorCodes.HARDWARE_MONITOR_START_FAILED,
                    "Failed to start hardware monitor", ex);
            }
        }

        /// <summary>
        /// Monitor'ü durdurur;
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            lock (_syncLock)
            {
                if (!_isMonitoring)
                {
                    _logger.LogWarning("Hardware monitor is not running");
                    return;
                }

                _isMonitoring = false;
                _monitoringCts?.Cancel();
            }

            try
            {
                // Stop timers;
                _monitoringTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _alertCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _healthCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // Stop external sensors;
                await StopExternalSensorsAsync();

                // Cleanup;
                CleanupPerformanceCounters();
                CleanupWmi();

                await _auditLogger.LogSystemEventAsync(
                    "HARDWARE_MONITOR_STOPPED",
                    "Hardware monitoring stopped",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Hardware monitor stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping hardware monitor");
                throw new HardwareMonitorException(ErrorCodes.HARDWARE_MONITOR_STOP_FAILED,
                    "Failed to stop hardware monitor", ex);
            }
        }

        /// <summary>
        /// Donanım ölçümlerini getirir;
        /// </summary>
        public async Task<HardwareMetrics> GetMetricsAsync(bool includeHistory = false)
        {
            try
            {
                var metrics = new HardwareMetrics
                {
                    Timestamp = DateTime.UtcNow,
                    CpuUsage = GetCpuMetrics(),
                    MemoryUsage = GetMemoryMetrics(),
                    DiskUsage = await GetDiskMetricsAsync(),
                    NetworkUsage = GetNetworkMetrics(),
                    GpuUsage = GetGpuMetrics(),
                    TemperatureReadings = GetTemperatures(),
                    FanSpeeds = GetFanSpeeds(),
                    PowerUsage = GetPowerUsage(),
                    SystemUptime = GetSystemUptime(),
                    ActiveAlerts = _activeAlerts.Values.ToList(),
                    ComponentHealth = GetComponentHealth()
                };

                if (includeHistory)
                {
                    metrics.History = _snapshots.ToList();
                    metrics.PerformanceTrends = _performanceTrends.ToList();
                }

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get hardware metrics");
                throw new HardwareMonitorException(ErrorCodes.METRICS_RETRIEVAL_FAILED,
                    "Failed to get hardware metrics", ex);
            }
        }

        /// <summary>
        /// Belirli bir bileşenin metriklerini getirir;
        /// </summary>
        public async Task<ComponentMetrics> GetComponentMetricsAsync(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                throw new ArgumentException("Component ID cannot be null or empty", nameof(componentId));

            try
            {
                if (!_components.TryGetValue(componentId, out var component))
                {
                    throw new HardwareMonitorException(ErrorCodes.COMPONENT_NOT_FOUND,
                        $"Component not found: {componentId}");
                }

                var metrics = await GetMetricsForComponentAsync(component);
                return metrics;
            }
            catch (HardwareMonitorException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get metrics for component {ComponentId}", componentId);
                throw new HardwareMonitorException(ErrorCodes.COMPONENT_METRICS_FAILED,
                    $"Failed to get metrics for component {componentId}", ex);
            }
        }

        /// <summary>
        /// Sistem sağlık durumunu getirir;
        /// </summary>
        public async Task<SystemHealthReport> GetSystemHealthAsync()
        {
            try
            {
                var metrics = await GetMetricsAsync();
                var health = new SystemHealthReport
                {
                    Timestamp = DateTime.UtcNow,
                    OverallHealth = CalculateOverallHealth(metrics),
                    HealthScore = CalculateHealthScore(metrics),
                    CriticalIssues = GetCriticalIssues(),
                    Warnings = GetWarningIssues(),
                    Recommendations = GenerateRecommendations(metrics),
                    ComponentHealth = metrics.ComponentHealth,
                    ResourceUtilization = new ResourceUtilization
                    {
                        CpuUtilization = metrics.CpuUsage?.OverallUsage ?? 0,
                        MemoryUtilization = metrics.MemoryUsage?.UsedPercentage ?? 0,
                        DiskUtilization = metrics.DiskUsage?.AverageUsage ?? 0,
                        NetworkUtilization = metrics.NetworkUsage?.TotalUtilization ?? 0
                    }
                };

                // Add trend analysis;
                health.Trends = await AnalyzePerformanceTrendsAsync();

                return health;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate system health report");
                throw new HardwareMonitorException(ErrorCodes.HEALTH_REPORT_FAILED,
                    "Failed to generate system health report", ex);
            }
        }

        /// <summary>
        /// Alarm/alert ekler;
        /// </summary>
        public async Task<bool> AddAlertThresholdAsync(AlertThreshold threshold)
        {
            ValidateAlertThreshold(threshold);

            try
            {
                var alert = new HardwareAlert
                {
                    Id = Guid.NewGuid().ToString(),
                    ComponentId = threshold.ComponentId,
                    MetricName = threshold.MetricName,
                    Condition = threshold.Condition,
                    ThresholdValue = threshold.ThresholdValue,
                    Severity = threshold.Severity,
                    Message = threshold.Message,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true
                };

                var key = $"{threshold.ComponentId}_{threshold.MetricName}_{threshold.Condition}";
                _activeAlerts[key] = alert;

                await _auditLogger.LogSystemEventAsync(
                    "ALERT_THRESHOLD_ADDED",
                    $"Alert threshold added: {threshold.MetricName} for {threshold.ComponentId}",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Alert threshold added: {Metric} for {Component}",
                    threshold.MetricName, threshold.ComponentId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add alert threshold");
                throw new HardwareMonitorException(ErrorCodes.ALERT_THRESHOLD_ADD_FAILED,
                    "Failed to add alert threshold", ex);
            }
        }

        /// <summary>
        /// Alarm/alert kaldırır;
        /// </summary>
        public async Task<bool> RemoveAlertThresholdAsync(string componentId, string metricName, AlertCondition condition)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                throw new ArgumentException("Component ID cannot be null or empty", nameof(componentId));
            if (string.IsNullOrWhiteSpace(metricName))
                throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

            try
            {
                var key = $"{componentId}_{metricName}_{condition}";
                var removed = _activeAlerts.TryRemove(key, out _);

                if (removed)
                {
                    await _auditLogger.LogSystemEventAsync(
                        "ALERT_THRESHOLD_REMOVED",
                        $"Alert threshold removed: {metricName} for {componentId}",
                        AuditLogSeverity.Information);

                    _logger.LogInformation("Alert threshold removed: {Metric} for {Component}",
                        metricName, componentId);
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove alert threshold");
                throw new HardwareMonitorException(ErrorCodes.ALERT_THRESHOLD_REMOVE_FAILED,
                    "Failed to remove alert threshold", ex);
            }
        }

        /// <summary>
        /// Donanım bileşeni ekler;
        /// </summary>
        public async Task<HardwareComponent> AddComponentAsync(HardwareComponent component)
        {
            ValidateHardwareComponent(component);

            try
            {
                component.Id = string.IsNullOrEmpty(component.Id)
                    ? Guid.NewGuid().ToString()
                    : component.Id;

                component.CreatedAt = DateTime.UtcNow;
                component.LastSeen = DateTime.UtcNow;
                component.IsActive = true;

                if (!_components.TryAdd(component.Id, component))
                {
                    throw new HardwareMonitorException(ErrorCodes.COMPONENT_ADD_FAILED,
                        $"Component with ID {component.Id} already exists");
                }

                // Initialize metrics for component;
                await InitializeComponentMetricsAsync(component);

                await _auditLogger.LogSystemEventAsync(
                    "HARDWARE_COMPONENT_ADDED",
                    $"Hardware component added: {component.Name} (Type: {component.Type})",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Hardware component added: {Name} (Type: {Type}, ID: {Id})",
                    component.Name, component.Type, component.Id);

                return component;
            }
            catch (HardwareMonitorException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add hardware component {Name}", component.Name);
                throw new HardwareMonitorException(ErrorCodes.COMPONENT_ADD_FAILED,
                    $"Failed to add hardware component {component.Name}", ex);
            }
        }

        /// <summary>
        /// Donanım bileşeni kaldırır;
        /// </summary>
        public async Task<bool> RemoveComponentAsync(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId))
                throw new ArgumentException("Component ID cannot be null or empty", nameof(componentId));

            try
            {
                if (!_components.TryRemove(componentId, out var component))
                {
                    _logger.LogWarning("Component {ComponentId} not found for removal", componentId);
                    return false;
                }

                // Remove associated alerts;
                var alertsToRemove = _activeAlerts.Keys
                    .Where(k => k.StartsWith(componentId + "_"))
                    .ToList();

                foreach (var alertKey in alertsToRemove)
                {
                    _activeAlerts.TryRemove(alertKey, out _);
                }

                await _auditLogger.LogSystemEventAsync(
                    "HARDWARE_COMPONENT_REMOVED",
                    $"Hardware component removed: {component.Name} (ID: {componentId})",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Hardware component removed: {Name} (ID: {Id})",
                    component.Name, componentId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove hardware component {ComponentId}", componentId);
                throw new HardwareMonitorException(ErrorCodes.COMPONENT_REMOVE_FAILED,
                    $"Failed to remove hardware component {componentId}", ex);
            }
        }

        /// <summary>
        /// Performans raporu oluşturur;
        /// </summary>
        public async Task<PerformanceReport> GeneratePerformanceReportAsync(DateTime? startTime = null, DateTime? endTime = null)
        {
            try
            {
                var start = startTime ?? DateTime.UtcNow.AddHours(-24);
                var end = endTime ?? DateTime.UtcNow;

                var report = new PerformanceReport
                {
                    GeneratedAt = DateTime.UtcNow,
                    PeriodStart = start,
                    PeriodEnd = end,
                    Summary = await GeneratePerformanceSummaryAsync(start, end),
                    DetailedMetrics = await GetDetailedMetricsAsync(start, end),
                    Anomalies = await DetectAnomaliesAsync(start, end),
                    Recommendations = await GeneratePerformanceRecommendationsAsync(start, end),
                    ComponentReports = await GenerateComponentReportsAsync(start, end)
                };

                // Calculate statistics;
                report.Statistics = CalculateStatistics(report.DetailedMetrics);

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate performance report");
                throw new HardwareMonitorException(ErrorCodes.PERFORMANCE_REPORT_FAILED,
                    "Failed to generate performance report", ex);
            }
        }

        /// <summary>
        /// Dış donanım sensorü ekler;
        /// </summary>
        public void AddExternalSensor(IHardwareSensor sensor)
        {
            if (sensor == null)
                throw new ArgumentNullException(nameof(sensor));

            lock (_syncLock)
            {
                _externalSensors.Add(sensor);
                _logger.LogInformation("External sensor added: {SensorName}", sensor.GetType().Name);
            }
        }

        /// <summary>
        /// Donanım sağlayıcı ekler;
        /// </summary>
        public void AddHardwareProvider(IHardwareProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            lock (_syncLock)
            {
                _hardwareProviders.Add(provider);
                _logger.LogInformation("Hardware provider added: {ProviderName}", provider.GetType().Name);
            }
        }

        /// <summary>
        /// Real-time eventleri subscribe olma;
        /// </summary>
        public IDisposable Subscribe(IObserver<HardwareEvent> observer)
        {
            return new HardwareEventSubscription(this, observer);
        }

        #region Private Methods - Initialization

        private void Initialize()
        {
            try
            {
                // Initialize default components;
                InitializeDefaultComponents();

                // Set up event handlers;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                _logger.LogInformation("Hardware monitor initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize hardware monitor");
                throw new HardwareMonitorException(ErrorCodes.INITIALIZATION_FAILED,
                    "Failed to initialize hardware monitor", ex);
            }
        }

        private void InitializePerformanceCounters()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
                    _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    _networkSentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", GetNetworkInterfaceName());
                    _networkReceivedCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", GetNetworkInterfaceName());
                }

                _logger.LogDebug("Performance counters initialized");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize some performance counters");
                // Continue with partial initialization;
            }
        }

        private void InitializeWmi()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _wmiInitialized = false;
                return;
            }

            try
            {
                _wmiScope = new ManagementScope(@"\\.\root\cimv2");
                _wmiScope.Connect();
                _wmiInitialized = true;

                _logger.LogDebug("WMI initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize WMI");
                _wmiInitialized = false;
            }
        }

        private async Task DiscoverHardwareAsync()
        {
            try
            {
                // Discover CPU;
                await DiscoverCpuAsync();

                // Discover Memory;
                await DiscoverMemoryAsync();

                // Discover Disks;
                await DiscoverDisksAsync();

                // Discover Network Adapters;
                await DiscoverNetworkAdaptersAsync();

                // Discover GPU (if available)
                await DiscoverGpuAsync();

                // Use external providers;
                foreach (var provider in _hardwareProviders)
                {
                    try
                    {
                        var components = await provider.DiscoverComponentsAsync();
                        foreach (var component in components)
                        {
                            if (!_components.ContainsKey(component.Id))
                            {
                                _components.TryAdd(component.Id, component);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Provider {Provider} failed to discover components", provider.GetType().Name);
                    }
                }

                _logger.LogInformation("Hardware discovery completed. Found {Count} components", _components.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to discover hardware");
                throw new HardwareMonitorException(ErrorCodes.HARDWARE_DISCOVERY_FAILED,
                    "Failed to discover hardware", ex);
            }
        }

        private void InitializeDefaultComponents()
        {
            // System CPU;
            var cpu = new HardwareComponent
            {
                Id = "CPU_MAIN",
                Name = "Main Processor",
                Type = HardwareComponentType.CPU,
                Manufacturer = "System",
                Model = "CPU",
                SerialNumber = "SYSTEM_CPU",
                Capabilities = new Dictionary<string, object>
                {
                    ["Cores"] = Environment.ProcessorCount,
                    ["Architecture"] = RuntimeInformation.ProcessArchitecture.ToString()
                }
            };
            _components.TryAdd(cpu.Id, cpu);

            // System Memory;
            var memory = new HardwareComponent
            {
                Id = "MEMORY_MAIN",
                Name = "System Memory",
                Type = HardwareComponentType.Memory,
                Manufacturer = "System",
                Model = "RAM",
                SerialNumber = "SYSTEM_RAM"
            };
            _components.TryAdd(memory.Id, memory);

            // System Disk;
            var disk = new HardwareComponent
            {
                Id = "DISK_SYSTEM",
                Name = "System Disk",
                Type = HardwareComponentType.Disk,
                Manufacturer = "System",
                Model = "HDD/SSD",
                SerialNumber = "SYSTEM_DISK"
            };
            _components.TryAdd(disk.Id, disk);

            // Network Adapter;
            var network = new HardwareComponent
            {
                Id = "NETWORK_MAIN",
                Name = "Primary Network Adapter",
                Type = HardwareComponentType.Network,
                Manufacturer = "System",
                Model = "Network Interface",
                SerialNumber = "SYSTEM_NET"
            };
            _components.TryAdd(network.Id, network);
        }

        #endregion

        #region Private Methods - Monitoring Loop

        private async Task MonitorHardwareLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Hardware monitoring loop started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_config.MonitoringIntervalMs, cancellationToken);

                    // Collect metrics;
                    var snapshot = await CollectHardwareSnapshotAsync();

                    // Store snapshot;
                    _snapshots.Enqueue(snapshot);

                    // Update metrics;
                    UpdateMetrics(snapshot);

                    // Check thresholds;
                    await CheckThresholdsAsync(snapshot);

                    // Process external sensors;
                    await ProcessExternalSensorsAsync();

                    // Calculate trends;
                    await CalculateTrendsAsync();
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in monitoring loop");
                    await Task.Delay(5000, cancellationToken); // Wait before retry
                }
            }

            _logger.LogInformation("Hardware monitoring loop stopped");
        }

        private async Task ProcessEventsLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Event processing loop started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_eventQueue.TryDequeue(out var hardwareEvent))
                    {
                        await ProcessEventAsync(hardwareEvent);
                    }
                    else
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in event processing loop");
                }
            }

            _logger.LogInformation("Event processing loop stopped");
        }

        private async Task<HardwareSnapshot> CollectHardwareSnapshotAsync()
        {
            var snapshot = new HardwareSnapshot
            {
                Timestamp = DateTime.UtcNow,
                CpuMetrics = GetCpuMetrics(),
                MemoryMetrics = GetMemoryMetrics(),
                DiskMetrics = await GetDiskMetricsAsync(),
                NetworkMetrics = GetNetworkMetrics(),
                GpuMetrics = GetGpuMetrics(),
                TemperatureMetrics = GetTemperatureMetrics(),
                PowerMetrics = GetPowerMetrics(),
                SystemMetrics = GetSystemMetrics()
            };

            // Add component-specific metrics;
            snapshot.ComponentMetrics = await GetComponentMetricsSnapshotAsync();

            return snapshot;
        }

        private void UpdateMetrics(HardwareSnapshot snapshot)
        {
            // Update CPU metrics;
            UpdateMetric("cpu.usage", snapshot.CpuMetrics.OverallUsage);
            UpdateMetric("cpu.temperature", snapshot.CpuMetrics.Temperature);
            UpdateMetric("cpu.power", snapshot.CpuMetrics.PowerUsage);

            // Update Memory metrics;
            UpdateMetric("memory.usage", snapshot.MemoryMetrics.UsedPercentage);
            UpdateMetric("memory.available", snapshot.MemoryMetrics.AvailableBytes);
            UpdateMetric("memory.total", snapshot.MemoryMetrics.TotalBytes);

            // Update Disk metrics;
            UpdateMetric("disk.usage", snapshot.DiskMetrics.AverageUsage);
            UpdateMetric("disk.read", snapshot.DiskMetrics.ReadBytesPerSecond);
            UpdateMetric("disk.write", snapshot.DiskMetrics.WriteBytesPerSecond);

            // Update Network metrics;
            UpdateMetric("network.sent", snapshot.NetworkMetrics.SentBytesPerSecond);
            UpdateMetric("network.received", snapshot.NetworkMetrics.ReceivedBytesPerSecond);
            UpdateMetric("network.utilization", snapshot.NetworkMetrics.TotalUtilization);

            // Update GPU metrics;
            if (snapshot.GpuMetrics != null)
            {
                UpdateMetric("gpu.usage", snapshot.GpuMetrics.UsagePercentage);
                UpdateMetric("gpu.temperature", snapshot.GpuMetrics.Temperature);
                UpdateMetric("gpu.memory", snapshot.GpuMetrics.MemoryUsage);
            }

            // Update component metrics;
            foreach (var componentMetric in snapshot.ComponentMetrics)
            {
                var metricKey = $"{componentMetric.ComponentId}.{componentMetric.MetricName}";
                UpdateMetric(metricKey, componentMetric.Value);
            }
        }

        private void UpdateMetric(string metricName, double value)
        {
            var metric = _metrics.GetOrAdd(metricName, _ => new HardwareMetric
            {
                Name = metricName,
                Values = new FixedSizeQueue<double>(_config.MetricHistorySize)
            });

            metric.Values.Enqueue(value);
            metric.LastValue = value;
            metric.LastUpdated = DateTime.UtcNow;

            // Calculate statistics;
            metric.Average = metric.Values.Average();
            metric.Minimum = metric.Values.Min();
            metric.Maximum = metric.Values.Max();
            metric.StandardDeviation = CalculateStandardDeviation(metric.Values);
        }

        #endregion

        #region Private Methods - Metric Collection

        private CpuMetrics GetCpuMetrics()
        {
            var metrics = new CpuMetrics
            {
                Timestamp = DateTime.UtcNow,
                OverallUsage = GetCpuUsage(),
                CoreUsages = GetCoreUsages(),
                Temperature = GetCpuTemperature(),
                PowerUsage = GetCpuPowerUsage(),
                Frequency = GetCpuFrequency(),
                LoadAverage = GetCpuLoadAverage()
            };

            return metrics;
        }

        private double GetCpuUsage()
        {
            try
            {
                if (_cpuCounter != null)
                {
                    return _cpuCounter.NextValue();
                }

                // Alternative method for non-Windows or when performance counter fails;
                if (_wmiInitialized)
                {
                    using var searcher = new ManagementObjectSearcher(_wmiScope,
                        new ObjectQuery("SELECT LoadPercentage FROM Win32_Processor"));

                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["LoadPercentage"] != null)
                        {
                            return Convert.ToDouble(obj["LoadPercentage"]);
                        }
                    }
                }

                // Fallback to Process class;
                using var process = Process.GetCurrentProcess();
                var startTime = DateTime.UtcNow;
                var startCpuUsage = process.TotalProcessorTime;

                Thread.Sleep(100);

                var endTime = DateTime.UtcNow;
                var endCpuUsage = process.TotalProcessorTime;

                var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
                var totalMsPassed = (endTime - startTime).TotalMilliseconds;
                var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

                return cpuUsageTotal * 100;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get CPU usage");
                return 0;
            }
        }

        private List<double> GetCoreUsages()
        {
            var coreUsages = new List<double>();

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _wmiInitialized)
                {
                    using var searcher = new ManagementObjectSearcher(_wmiScope,
                        new ObjectQuery("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name != '_Total'"));

                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["PercentProcessorTime"] != null)
                        {
                            coreUsages.Add(Convert.ToDouble(obj["PercentProcessorTime"]));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get core usages");
            }

            // If we couldn't get individual cores, create a simple list;
            if (coreUsages.Count == 0)
            {
                var overallUsage = GetCpuUsage();
                for (int i = 0; i < Environment.ProcessorCount; i++)
                {
                    coreUsages.Add(overallUsage);
                }
            }

            return coreUsages;
        }

        private MemoryMetrics GetMemoryMetrics()
        {
            var metrics = new MemoryMetrics
            {
                Timestamp = DateTime.UtcNow,
                TotalBytes = GetTotalMemory(),
                UsedBytes = GetUsedMemory(),
                AvailableBytes = GetAvailableMemory(),
                UsedPercentage = GetMemoryUsage(),
                CacheBytes = GetCacheMemory(),
                SwapTotalBytes = GetSwapTotal(),
                SwapUsedBytes = GetSwapUsed()
            };

            return metrics;
        }

        private double GetMemoryUsage()
        {
            try
            {
                if (_ramCounter != null)
                {
                    return _ramCounter.NextValue();
                }

                // Alternative method;
                var totalMemory = GetTotalMemory();
                var usedMemory = GetUsedMemory();

                if (totalMemory > 0)
                {
                    return (usedMemory / totalMemory) * 100;
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get memory usage");
                return 0;
            }
        }

        private async Task<DiskMetrics> GetDiskMetricsAsync()
        {
            var metrics = new DiskMetrics
            {
                Timestamp = DateTime.UtcNow,
                TotalBytes = GetTotalDiskSpace(),
                UsedBytes = GetUsedDiskSpace(),
                AvailableBytes = GetAvailableDiskSpace(),
                AverageUsage = GetDiskUsage(),
                ReadBytesPerSecond = GetDiskReadSpeed(),
                WriteBytesPerSecond = GetDiskWriteSpeed(),
                Iops = GetDiskIops(),
                ResponseTime = GetDiskResponseTime(),
                Drives = await GetDriveInfoAsync()
            };

            return metrics;
        }

        private NetworkMetrics GetNetworkMetrics()
        {
            var metrics = new NetworkMetrics
            {
                Timestamp = DateTime.UtcNow,
                SentBytesPerSecond = GetNetworkSentSpeed(),
                ReceivedBytesPerSecond = GetNetworkReceivedSpeed(),
                TotalUtilization = GetNetworkUtilization(),
                Connections = GetNetworkConnections(),
                Adapters = GetNetworkAdapters()
            };

            return metrics;
        }

        private GpuMetrics GetGpuMetrics()
        {
            try
            {
                // This would typically use vendor-specific APIs (NVIDIA, AMD, Intel)
                // For now, return null or implement basic detection;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _wmiInitialized)
                {
                    using var searcher = new ManagementObjectSearcher(_wmiScope,
                        new ObjectQuery("SELECT AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController"));

                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["AdapterRAM"] != null)
                        {
                            return new GpuMetrics
                            {
                                Timestamp = DateTime.UtcNow,
                                UsagePercentage = 0, // Would need specific API
                                Temperature = 0,
                                MemoryUsage = Convert.ToUInt64(obj["AdapterRAM"]),
                                MemoryTotal = Convert.ToUInt64(obj["AdapterRAM"]),
                                ClockSpeed = 0,
                                PowerUsage = 0
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get GPU metrics");
            }

            return null;
        }

        #endregion

        #region Private Methods - Alerting and Thresholds

        private async Task CheckThresholdsAsync(HardwareSnapshot snapshot)
        {
            foreach (var alert in _activeAlerts.Values)
            {
                try
                {
                    var metricValue = GetMetricValue(snapshot, alert.ComponentId, alert.MetricName);

                    if (IsThresholdBreached(metricValue, alert.Condition, alert.ThresholdValue))
                    {
                        await TriggerAlertAsync(alert, metricValue);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check threshold for alert {AlertId}", alert.Id);
                }
            }
        }

        private double GetMetricValue(HardwareSnapshot snapshot, string componentId, string metricName)
        {
            // Check component-specific metrics first;
            var componentMetric = snapshot.ComponentMetrics
                .FirstOrDefault(m => m.ComponentId == componentId && m.MetricName == metricName);

            if (componentMetric != null)
            {
                return componentMetric.Value;
            }

            // Check system metrics;
            return metricName.ToLower() switch
            {
                "cpu.usage" => snapshot.CpuMetrics.OverallUsage,
                "memory.usage" => snapshot.MemoryMetrics.UsedPercentage,
                "disk.usage" => snapshot.DiskMetrics.AverageUsage,
                "network.utilization" => snapshot.NetworkMetrics.TotalUtilization,
                "temperature" => snapshot.TemperatureMetrics?.AverageTemperature ?? 0,
                _ => 0
            };
        }

        private bool IsThresholdBreached(double value, AlertCondition condition, double threshold)
        {
            return condition switch
            {
                AlertCondition.GreaterThan => value > threshold,
                AlertCondition.GreaterThanOrEqual => value >= threshold,
                AlertCondition.LessThan => value < threshold,
                AlertCondition.LessThanOrEqual => value <= threshold,
                AlertCondition.Equals => Math.Abs(value - threshold) < 0.001,
                AlertCondition.NotEquals => Math.Abs(value - threshold) >= 0.001,
                _ => false
            };
        }

        private async Task TriggerAlertAsync(HardwareAlert alert, double currentValue)
        {
            // Check if alert is already active;
            if (alert.LastTriggered.HasValue &&
                alert.LastTriggered.Value.AddSeconds(alert.CooldownSeconds) > DateTime.UtcNow)
            {
                return;
            }

            alert.CurrentValue = currentValue;
            alert.LastTriggered = DateTime.UtcNow;
            alert.TriggerCount++;

            // Create event;
            var hardwareEvent = new HardwareEvent
            {
                Id = Guid.NewGuid().ToString(),
                ComponentId = alert.ComponentId,
                EventType = HardwareEventType.Alert,
                Severity = alert.Severity,
                Message = $"{alert.Message} (Current: {currentValue:F2}, Threshold: {alert.ThresholdValue})",
                Timestamp = DateTime.UtcNow,
                Data = new Dictionary<string, object>
                {
                    ["alert_id"] = alert.Id,
                    ["metric"] = alert.MetricName,
                    ["condition"] = alert.Condition.ToString(),
                    ["threshold"] = alert.ThresholdValue,
                    ["current_value"] = currentValue,
                    ["trigger_count"] = alert.TriggerCount
                }
            };

            // Add to event queue;
            _eventQueue.Enqueue(hardwareEvent);

            // Log to audit;
            var auditSeverity = alert.Severity switch
            {
                AlertSeverity.Low => AuditLogSeverity.Information,
                AlertSeverity.Medium => AuditLogSeverity.Warning,
                AlertSeverity.High => AuditLogSeverity.Error,
                AlertSeverity.Critical => AuditLogSeverity.Critical,
                _ => AuditLogSeverity.Warning
            };

            await _auditLogger.LogSystemEventAsync(
                "HARDWARE_ALERT_TRIGGERED",
                hardwareEvent.Message,
                auditSeverity);

            _logger.Log(GetLogLevel(alert.Severity),
                "Hardware alert triggered: {Message} (Component: {ComponentId}, Metric: {Metric})",
                hardwareEvent.Message, alert.ComponentId, alert.MetricName);
        }

        private LogLevel GetLogLevel(AlertSeverity severity)
        {
            return severity switch
            {
                AlertSeverity.Low => LogLevel.Information,
                AlertSeverity.Medium => LogLevel.Warning,
                AlertSeverity.High => LogLevel.Error,
                AlertSeverity.Critical => LogLevel.Critical,
                _ => LogLevel.Warning
            };
        }

        #endregion

        #region Private Methods - Event Processing

        private async Task ProcessEventAsync(HardwareEvent hardwareEvent)
        {
            try
            {
                // Process based on event type;
                switch (hardwareEvent.EventType)
                {
                    case HardwareEventType.Alert:
                        await ProcessAlertEventAsync(hardwareEvent);
                        break;

                    case HardwareEventType.StatusChange:
                        await ProcessStatusChangeEventAsync(hardwareEvent);
                        break;

                    case HardwareEventType.ThresholdBreach:
                        await ProcessThresholdBreachEventAsync(hardwareEvent);
                        break;

                    case HardwareEventType.ComponentFailure:
                        await ProcessComponentFailureEventAsync(hardwareEvent);
                        break;

                    case HardwareEventType.PerformanceDegradation:
                        await ProcessPerformanceEventAsync(hardwareEvent);
                        break;
                }

                // Notify subscribers;
                NotifyEventSubscribers(hardwareEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process hardware event {EventId}", hardwareEvent.Id);
            }
        }

        private async Task ProcessAlertEventAsync(HardwareEvent hardwareEvent)
        {
            // Implement alert processing logic;
            // This could include notifications, auto-remediation, etc.

            _logger.LogInformation("Processing alert event: {EventId}", hardwareEvent.Id);

            // Example: Send notification if severity is high or critical;
            if (hardwareEvent.Severity >= AlertSeverity.High)
            {
                await SendNotificationAsync(hardwareEvent);
            }
        }

        private async Task SendNotificationAsync(HardwareEvent hardwareEvent)
        {
            try
            {
                // Implement notification logic;
                // This could be email, SMS, webhook, etc.

                _logger.LogDebug("Notification sent for event {EventId}", hardwareEvent.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send notification for event {EventId}", hardwareEvent.Id);
            }
        }

        #endregion

        #region Private Methods - External Sensors

        private async Task StartExternalSensorsAsync()
        {
            foreach (var sensor in _externalSensors)
            {
                try
                {
                    await sensor.StartAsync();
                    _logger.LogDebug("External sensor started: {SensorName}", sensor.GetType().Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to start external sensor {SensorName}", sensor.GetType().Name);
                }
            }
        }

        private async Task StopExternalSensorsAsync()
        {
            foreach (var sensor in _externalSensors)
            {
                try
                {
                    await sensor.StopAsync();
                    _logger.LogDebug("External sensor stopped: {SensorName}", sensor.GetType().Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to stop external sensor {SensorName}", sensor.GetType().Name);
                }
            }
        }

        private async Task ProcessExternalSensorsAsync()
        {
            foreach (var sensor in _externalSensors)
            {
                try
                {
                    var readings = await sensor.GetReadingsAsync();
                    foreach (var reading in readings)
                    {
                        var eventData = new HardwareEvent
                        {
                            Id = Guid.NewGuid().ToString(),
                            ComponentId = reading.ComponentId,
                            EventType = HardwareEventType.SensorReading,
                            Severity = AlertSeverity.Low,
                            Message = $"Sensor reading: {reading.MetricName} = {reading.Value}",
                            Timestamp = DateTime.UtcNow,
                            Data = new Dictionary<string, object>
                            {
                                ["sensor_type"] = sensor.GetType().Name,
                                ["metric"] = reading.MetricName,
                                ["value"] = reading.Value,
                                ["unit"] = reading.Unit
                            }
                        };

                        _eventQueue.Enqueue(eventData);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process external sensor {SensorName}", sensor.GetType().Name);
                }
            }
        }

        #endregion

        #region Private Methods - Health and Trends

        private SystemHealth CalculateOverallHealth(HardwareMetrics metrics)
        {
            // Simple health calculation based on thresholds;
            var issues = 0;

            if (metrics.CpuUsage?.OverallUsage > _config.CpuCriticalThreshold)
                issues++;
            if (metrics.MemoryUsage?.UsedPercentage > _config.MemoryCriticalThreshold)
                issues++;
            if (metrics.DiskUsage?.AverageUsage > _config.DiskCriticalThreshold)
                issues++;
            if (metrics.TemperatureReadings?.Any(t => t.Value > _config.TemperatureCriticalThreshold) == true)
                issues++;

            return issues switch
            {
                0 => SystemHealth.Healthy,
                1 => SystemHealth.Warning,
                _ => SystemHealth.Critical
            };
        }

        private async Task<List<PerformanceTrend>> AnalyzePerformanceTrendsAsync()
        {
            var trends = new List<PerformanceTrend>();

            if (_snapshots.Count < 2)
                return trends;

            try
            {
                // Analyze CPU trend;
                var cpuTrend = AnalyzeMetricTrend("cpu.usage", "CPU Usage");
                if (cpuTrend != null) trends.Add(cpuTrend);

                // Analyze Memory trend;
                var memoryTrend = AnalyzeMetricTrend("memory.usage", "Memory Usage");
                if (memoryTrend != null) trends.Add(memoryTrend);

                // Analyze Disk trend;
                var diskTrend = AnalyzeMetricTrend("disk.usage", "Disk Usage");
                if (diskTrend != null) trends.Add(diskTrend);

                // Store trends;
                foreach (var trend in trends)
                {
                    _performanceTrends.Enqueue(trend);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze performance trends");
            }

            return trends;
        }

        private PerformanceTrend AnalyzeMetricTrend(string metricName, string displayName)
        {
            if (!_metrics.TryGetValue(metricName, out var metric))
                return null;

            if (metric.Values.Count < 10)
                return null;

            var values = metric.Values.ToList();
            var recentValues = values.TakeLast(10).ToList();
            var previousValues = values.SkipLast(10).Take(10).ToList();

            if (previousValues.Count < 10)
                return null;

            var recentAvg = recentValues.Average();
            var previousAvg = previousValues.Average();
            var change = recentAvg - previousAvg;
            var changePercentage = previousAvg > 0 ? (change / previousAvg) * 100 : 0;

            var trend = new PerformanceTrend
            {
                MetricName = metricName,
                DisplayName = displayName,
                CurrentValue = recentAvg,
                PreviousValue = previousAvg,
                ChangeAmount = change,
                ChangePercentage = changePercentage,
                TrendDirection = change > 0 ? TrendDirection.Increasing :
                                change < 0 ? TrendDirection.Decreasing :
                                TrendDirection.Stable,
                Confidence = CalculateTrendConfidence(values),
                Timestamp = DateTime.UtcNow
            };

            return trend;
        }

        #endregion

        #region Private Methods - Helper Methods

        private void CleanupPerformanceCounters()
        {
            _cpuCounter?.Dispose();
            _ramCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            _networkSentCounter?.Dispose();
            _networkReceivedCounter?.Dispose();

            _cpuCounter = null;
            _ramCounter = null;
            _diskReadCounter = null;
            _diskWriteCounter = null;
            _networkSentCounter = null;
            _networkReceivedCounter = null;
        }

        private void CleanupWmi()
        {
            _wmiScope?.Dispose();
            _wmiScope = null;
            _wmiInitialized = false;
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            _ = StopMonitoringAsync().ConfigureAwait(false);
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            _logger.LogCritical((Exception)e.ExceptionObject, "Unhandled exception in hardware monitor");
        }

        private void MonitoringCallback(object state)
        {
            // Timer callback - can be used for additional periodic tasks;
        }

        private void AlertCheckCallback(object state)
        {
            // Periodic alert checking;
        }

        private void HealthCheckCallback(object state)
        {
            // Periodic health checking;
        }

        private double CalculateStandardDeviation(IEnumerable<double> values)
        {
            var valueList = values.ToList();
            if (valueList.Count < 2)
                return 0;

            var avg = valueList.Average();
            var sumOfSquares = valueList.Sum(x => Math.Pow(x - avg, 2));
            return Math.Sqrt(sumOfSquares / (valueList.Count - 1));
        }

        private double CalculateTrendConfidence(List<double> values)
        {
            if (values.Count < 10)
                return 0;

            // Simple confidence calculation based on data points and variance;
            var variance = CalculateStandardDeviation(values);
            var avg = values.Average();

            if (avg == 0)
                return 0;

            var coefficientOfVariation = variance / avg;
            var confidence = 1.0 - Math.Min(coefficientOfVariation, 1.0);

            return Math.Max(0, Math.Min(1, confidence));
        }

        private string GetNetworkInterfaceName()
        {
            try
            {
                var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                var firstActive = networkInterfaces.FirstOrDefault(ni =>
                    ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

                return firstActive?.Name ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        #endregion

        #region Platform-Specific Methods

        private async Task DiscoverCpuAsync()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _wmiInitialized)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using var searcher = new ManagementObjectSearcher(_wmiScope,
                            new ObjectQuery("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"));

                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var cpu = new HardwareComponent
                            {
                                Id = $"CPU_{obj["Name"]}",
                                Name = obj["Name"]?.ToString() ?? "Unknown CPU",
                                Type = HardwareComponentType.CPU,
                                Manufacturer = obj["Manufacturer"]?.ToString() ?? "Unknown",
                                Model = obj["Name"]?.ToString() ?? "Unknown",
                                Capabilities = new Dictionary<string, object>
                                {
                                    ["Cores"] = obj["NumberOfCores"] ?? Environment.ProcessorCount,
                                    ["LogicalProcessors"] = obj["NumberOfLogicalProcessors"] ?? Environment.ProcessorCount,
                                    ["MaxClockSpeed"] = obj["MaxClockSpeed"] ?? 0
                                }
                            };

                            _components.TryAdd(cpu.Id, cpu);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to discover CPU via WMI");
                    }
                });
            }
        }

        private async Task DiscoverMemoryAsync()
        {
            // Memory discovery logic;
            await Task.CompletedTask;
        }

        private async Task DiscoverDisksAsync()
        {
            // Disk discovery logic;
            await Task.CompletedTask;
        }

        private async Task DiscoverNetworkAdaptersAsync()
        {
            // Network adapter discovery logic;
            await Task.CompletedTask;
        }

        private async Task DiscoverGpuAsync()
        {
            // GPU discovery logic;
            await Task.CompletedTask;
        }

        // Platform-specific metric getters;
        private double GetCpuTemperature() => 0; // Would use platform-specific API;
        private double GetCpuPowerUsage() => 0;
        private double GetCpuFrequency() => 0;
        private double GetCpuLoadAverage() => 0;
        private ulong GetTotalMemory() => 0;
        private ulong GetUsedMemory() => 0;
        private ulong GetAvailableMemory() => 0;
        private ulong GetCacheMemory() => 0;
        private ulong GetSwapTotal() => 0;
        private ulong GetSwapUsed() => 0;
        private ulong GetTotalDiskSpace() => 0;
        private ulong GetUsedDiskSpace() => 0;
        private ulong GetAvailableDiskSpace() => 0;
        private double GetDiskReadSpeed() => _diskReadCounter?.NextValue() ?? 0;
        private double GetDiskWriteSpeed() => _diskWriteCounter?.NextValue() ?? 0;
        private double GetDiskIops() => 0;
        private double GetDiskResponseTime() => 0;
        private double GetNetworkSentSpeed() => _networkSentCounter?.NextValue() ?? 0;
        private double GetNetworkReceivedSpeed() => _networkReceivedCounter?.NextValue() ?? 0;
        private double GetNetworkUtilization() => 0;
        private List<NetworkConnection> GetNetworkConnections() => new List<NetworkConnection>();
        private List<NetworkAdapter> GetNetworkAdapters() => new List<NetworkAdapter>();
        private List<TemperatureReading> GetTemperatures() => new List<TemperatureReading>();
        private List<FanSpeed> GetFanSpeeds() => new List<FanSpeed>();
        private PowerUsage GetPowerUsage() => new PowerUsage();
        private TimeSpan GetSystemUptime() => TimeSpan.FromMilliseconds(Environment.TickCount);
        private Task<List<DriveInfo>> GetDriveInfoAsync() => Task.FromResult(new List<DriveInfo>());
        private TemperatureMetrics GetTemperatureMetrics() => new TemperatureMetrics();
        private PowerMetrics GetPowerMetrics() => new PowerMetrics();
        private SystemMetrics GetSystemMetrics() => new SystemMetrics();
        private Task<List<ComponentMetricSnapshot>> GetComponentMetricsSnapshotAsync() => Task.FromResult(new List<ComponentMetricSnapshot>());
        private Task<ComponentMetrics> GetMetricsForComponentAsync(HardwareComponent component) => Task.FromResult(new ComponentMetrics());
        private Task InitializeComponentMetricsAsync(HardwareComponent component) => Task.CompletedTask;
        private HealthStatus GetComponentHealth() => new HealthStatus();
        private List<CriticalIssue> GetCriticalIssues() => new List<CriticalIssue>();
        private List<WarningIssue> GetWarningIssues() => new List<WarningIssue>();
        private List<Recommendation> GenerateRecommendations(HardwareMetrics metrics) => new List<Recommendation>();
        private Task<PerformanceSummary> GeneratePerformanceSummaryAsync(DateTime start, DateTime end) => Task.FromResult(new PerformanceSummary());
        private Task<List<DetailedMetric>> GetDetailedMetricsAsync(DateTime start, DateTime end) => Task.FromResult(new List<DetailedMetric>());
        private Task<List<Anomaly>> DetectAnomaliesAsync(DateTime start, DateTime end) => Task.FromResult(new List<Anomaly>());

        private Task<List<Recommendation>> GeneratePerformanceRecommendationsAsync(DateTime start, DateTime end)
        {
            return Task.FromResult(new List<Recommendation>());
        }

        private Task<List<ComponentReport>> GenerateComponentReportsAsync(DateTime start, DateTime end)
        {
            return Task.FromResult(new List<ComponentReport>());
        }

        private Statistics CalculateStatistics(List<DetailedMetric> metrics) => new Statistics();
        private double GetDiskUsage() => 0;

        private void ValidateAlertThreshold(AlertThreshold threshold)
        {
            if (threshold == null) throw new ArgumentNullException(nameof(threshold));
            if (string.IsNullOrWhiteSpace(threshold.ComponentId)) throw new ArgumentException("Component ID cannot be null or empty", nameof(threshold.ComponentId));
            if (string.IsNullOrWhiteSpace(threshold.MetricName)) throw new ArgumentException("Metric name cannot be null or empty", nameof(threshold.MetricName));
            if (string.IsNullOrWhiteSpace(threshold.Message)) throw new ArgumentException("Message cannot be null or empty", nameof(threshold.Message));
        }

        private void ValidateHardwareComponent(HardwareComponent component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (string.IsNullOrWhiteSpace(component.Name)) throw new ArgumentException("Component name cannot be null or empty", nameof(component.Name));
        }

        private void NotifyEventSubscribers(HardwareEvent hardwareEvent)
        {
            // Implementation would notify registered observers;
        }

        private double CalculateHealthScore(HardwareMetrics metrics)
        {
            // Simple health score calculation;
            double score = 100;

            if (metrics.CpuUsage?.OverallUsage > 90) score -= 20;
            else if (metrics.CpuUsage?.OverallUsage > 80) score -= 10;

            if (metrics.MemoryUsage?.UsedPercentage > 90) score -= 20;
            else if (metrics.MemoryUsage?.UsedPercentage > 80) score -= 10;

            if (metrics.DiskUsage?.AverageUsage > 90) score -= 20;
            else if (metrics.DiskUsage?.AverageUsage > 80) score -= 10;

            return Math.Max(0, score);
        }

        private async Task CalculateTrendsAsync()
        {
            // Calculate performance trends;
            await Task.CompletedTask;
        }

        private Task ProcessStatusChangeEventAsync(HardwareEvent hardwareEvent) => Task.CompletedTask;
        private Task ProcessThresholdBreachEventAsync(HardwareEvent hardwareEvent) => Task.CompletedTask;
        private Task ProcessComponentFailureEventAsync(HardwareEvent hardwareEvent) => Task.CompletedTask;
        private Task ProcessPerformanceEventAsync(HardwareEvent hardwareEvent) => Task.CompletedTask;

        #endregion

        #region IDisposable Implementation

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources;
                    _monitoringCts?.Cancel();
                    _monitoringCts?.Dispose();

                    _monitoringTimer?.Dispose();
                    _alertCheckTimer?.Dispose();
                    _healthCheckTimer?.Dispose();

                    CleanupPerformanceCounters();
                    CleanupWmi();

                    // Remove event handlers;
                    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

                    // Stop external sensors;
                    _ = StopExternalSensorsAsync().ConfigureAwait(false);

                    // Clear collections;
                    _metrics.Clear();
                    _activeAlerts.Clear();
                    _components.Clear();

                    while (_eventQueue.TryDequeue(out _)) { }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~HardwareMonitor()
        {
            Dispose(false);
        }

        #endregion

        #region Nested Classes

        private class HardwareEventSubscription : IDisposable
        {
            private readonly HardwareMonitor _monitor;
            private readonly IObserver<HardwareEvent> _observer;

            public HardwareEventSubscription(HardwareMonitor monitor, IObserver<HardwareEvent> observer)
            {
                _monitor = monitor;
                _observer = observer;
            }

            public void Dispose()
            {
                // Unsubscribe logic;
            }
        }

        private class FixedSizeQueue<T> : Queue<T>
        {
            private readonly int _maxSize;

            public FixedSizeQueue(int maxSize)
            {
                _maxSize = maxSize;
            }

            public new void Enqueue(T item)
            {
                base.Enqueue(item);
                while (Count > _maxSize)
                {
                    Dequeue();
                }
            }
        }

        #endregion
    }

    #region Supporting Classes and Interfaces

    /// <summary>
    /// Hardware monitor configuration;
    /// </summary>
    public class HardwareMonitorConfig
    {
        public int MonitoringIntervalMs { get; set; } = 1000;
        public int AlertCheckIntervalSeconds { get; set; } = 30;
        public int HealthCheckIntervalMinutes { get; set; } = 5;
        public int MaxSnapshots { get; set; } = 1000;
        public int MaxTrends { get; set; } = 100;
        public int MetricHistorySize { get; set; } = 100;
        public double CpuWarningThreshold { get; set; } = 80;
        public double CpuCriticalThreshold { get; set; } = 90;
        public double MemoryWarningThreshold { get; set; } = 80;
        public double MemoryCriticalThreshold { get; set; } = 90;
        public double DiskWarningThreshold { get; set; } = 80;
        public double DiskCriticalThreshold { get; set; } = 90;
        public double TemperatureWarningThreshold { get; set; } = 70;
        public double TemperatureCriticalThreshold { get; set; } = 85;
        public bool EnablePerformanceCounters { get; set; } = true;
        public bool EnableWmi { get; set; } = true;
        public bool EnableExternalSensors { get; set; } = true;
        public int EventQueueSize { get; set; } = 1000;
    }

    /// <summary>
    /// Hardware metrics;
    /// </summary>
    public class HardwareMetrics
    {
        public DateTime Timestamp { get; set; }
        public CpuMetrics CpuUsage { get; set; }
        public MemoryMetrics MemoryUsage { get; set; }
        public DiskMetrics DiskUsage { get; set; }
        public NetworkMetrics NetworkUsage { get; set; }
        public GpuMetrics GpuUsage { get; set; }
        public List<TemperatureReading> TemperatureReadings { get; set; }
        public List<FanSpeed> FanSpeeds { get; set; }
        public PowerUsage PowerUsage { get; set; }
        public TimeSpan SystemUptime { get; set; }
        public List<HardwareAlert> ActiveAlerts { get; set; }
        public HealthStatus ComponentHealth { get; set; }
        public List<HardwareSnapshot> History { get; set; }
        public List<PerformanceTrend> PerformanceTrends { get; set; }
    }

    /// <summary>
    /// CPU metrics;
    /// </summary>
    public class CpuMetrics
    {
        public DateTime Timestamp { get; set; }
        public double OverallUsage { get; set; }
        public List<double> CoreUsages { get; set; }
        public double Temperature { get; set; }
        public double PowerUsage { get; set; }
        public double Frequency { get; set; }
        public double LoadAverage { get; set; }
    }

    /// <summary>
    /// Memory metrics;
    /// </summary>
    public class MemoryMetrics
    {
        public DateTime Timestamp { get; set; }
        public ulong TotalBytes { get; set; }
        public ulong UsedBytes { get; set; }
        public ulong AvailableBytes { get; set; }
        public double UsedPercentage { get; set; }
        public ulong CacheBytes { get; set; }
        public ulong SwapTotalBytes { get; set; }
        public ulong SwapUsedBytes { get; set; }
    }

    /// <summary>
    /// Disk metrics;
    /// </summary>
    public class DiskMetrics
    {
        public DateTime Timestamp { get; set; }
        public ulong TotalBytes { get; set; }
        public ulong UsedBytes { get; set; }
        public ulong AvailableBytes { get; set; }
        public double AverageUsage { get; set; }
        public double ReadBytesPerSecond { get; set; }
        public double WriteBytesPerSecond { get; set; }
        public double Iops { get; set; }
        public double ResponseTime { get; set; }
        public List<DriveInfo> Drives { get; set; }
    }

    /// <summary>
    /// Network metrics;
    /// </summary>
    public class NetworkMetrics
    {
        public DateTime Timestamp { get; set; }
        public double SentBytesPerSecond { get; set; }
        public double ReceivedBytesPerSecond { get; set; }
        public double TotalUtilization { get; set; }
        public List<NetworkConnection> Connections { get; set; }
        public List<NetworkAdapter> Adapters { get; set; }
    }

    /// <summary>
    /// GPU metrics;
    /// </summary>
    public class GpuMetrics
    {
        public DateTime Timestamp { get; set; }
        public double UsagePercentage { get; set; }
        public double Temperature { get; set; }
        public ulong MemoryUsage { get; set; }
        public ulong MemoryTotal { get; set; }
        public double ClockSpeed { get; set; }
        public double PowerUsage { get; set; }
    }

    /// <summary>
    /// Temperature metrics;
    /// </summary>
    public class TemperatureMetrics
    {
        public DateTime Timestamp { get; set; }
        public double AverageTemperature { get; set; }
        public double MaximumTemperature { get; set; }
        public double MinimumTemperature { get; set; }
        public List<TemperatureReading> Readings { get; set; }
    }

    /// <summary>
    /// Power metrics;
    /// </summary>
    public class PowerMetrics
    {
        public DateTime Timestamp { get; set; }
        public double TotalPowerUsage { get; set; }
        public double CpuPower { get; set; }
        public double GpuPower { get; set; }
        public double DiskPower { get; set; }
        public double OtherPower { get; set; }
    }

    /// <summary>
    /// System metrics;
    /// </summary>
    public class SystemMetrics
    {
        public DateTime Timestamp { get; set; }
        public int ProcessCount { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }
        public TimeSpan Uptime { get; set; }
        public DateTime BootTime { get; set; }
    }

    /// <summary>
    /// Hardware snapshot;
    /// </summary>
    public class HardwareSnapshot
    {
        public DateTime Timestamp { get; set; }
        public CpuMetrics CpuMetrics { get; set; }
        public MemoryMetrics MemoryMetrics { get; set; }
        public DiskMetrics DiskMetrics { get; set; }
        public NetworkMetrics NetworkMetrics { get; set; }
        public GpuMetrics GpuMetrics { get; set; }
        public TemperatureMetrics TemperatureMetrics { get; set; }
        public PowerMetrics PowerMetrics { get; set; }
        public SystemMetrics SystemMetrics { get; set; }
        public List<ComponentMetricSnapshot> ComponentMetrics { get; set; }
    }

    /// <summary>
    /// Component metric snapshot;
    /// </summary>
    public class ComponentMetricSnapshot
    {
        public string ComponentId { get; set; }
        public string MetricName { get; set; }
        public double Value { get; set; }
        public string Unit { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hardware metric;
    /// </summary>
    public class HardwareMetric
    {
        public string Name { get; set; }
        public double LastValue { get; set; }
        public DateTime LastUpdated { get; set; }
        public FixedSizeQueue<double> Values { get; set; }
        public double Average { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public double StandardDeviation { get; set; }
    }

    /// <summary>
    /// Hardware alert;
    /// </summary>
    public class HardwareAlert
    {
        public string Id { get; set; }
        public string ComponentId { get; set; }
        public string MetricName { get; set; }
        public AlertCondition Condition { get; set; }
        public double ThresholdValue { get; set; }
        public double CurrentValue { get; set; }
        public AlertSeverity Severity { get; set; }
        public string Message { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastTriggered { get; set; }
        public int TriggerCount { get; set; }
        public int CooldownSeconds { get; set; } = 300;
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Alert threshold;
    /// </summary>
    public class AlertThreshold
    {
        public string ComponentId { get; set; }
        public string MetricName { get; set; }
        public AlertCondition Condition { get; set; }
        public double ThresholdValue { get; set; }
        public AlertSeverity Severity { get; set; }
        public string Message { get; set; }
        public int CooldownSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Alert condition;
    /// </summary>
    public enum AlertCondition
    {
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Equals,
        NotEquals
    }

    /// <summary>
    /// Alert severity;
    /// </summary>
    public enum AlertSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Hardware event;
    /// </summary>
    public class HardwareEvent
    {
        public string Id { get; set; }
        public string ComponentId { get; set; }
        public HardwareEventType EventType { get; set; }
        public AlertSeverity Severity { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    /// <summary>
    /// Hardware event type;
    /// </summary>
    public enum HardwareEventType
    {
        Alert,
        StatusChange,
        ThresholdBreach,
        ComponentFailure,
        PerformanceDegradation,
        SensorReading
    }

    /// <summary>
    /// Hardware component;
    /// </summary>
    public class HardwareComponent
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public HardwareComponentType Type { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string SerialNumber { get; set; }
        public string Version { get; set; }
        public Dictionary<string, object> Capabilities { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsActive { get; set; }
        public ComponentStatus Status { get; set; }
    }

    /// <summary>
    /// Hardware component type;
    /// </summary>
    public enum HardwareComponentType
    {
        CPU,
        GPU,
        Memory,
        Disk,
        Network,
        Motherboard,
        PowerSupply,
        Cooling,
        Sensor,
        Other
    }

    /// <summary>
    /// Component status;
    /// </summary>
    public enum ComponentStatus
    {
        Unknown,
        Healthy,
        Warning,
        Error,
        Offline,
        Disabled
    }

    /// <summary>
    /// Component metrics;
    /// </summary>
    public class ComponentMetrics
    {
        public string ComponentId { get; set; }
        public string ComponentName { get; set; }
        public HardwareComponentType ComponentType { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, double> Metrics { get; set; }
        public ComponentStatus Status { get; set; }
        public List<HardwareAlert> ActiveAlerts { get; set; }
        public PerformanceMetrics Performance { get; set; }
    }

    /// <summary>
    /// System health report;
    /// </summary>
    public class SystemHealthReport
    {
        public DateTime Timestamp { get; set; }
        public SystemHealth OverallHealth { get; set; }
        public double HealthScore { get; set; }
        public List<CriticalIssue> CriticalIssues { get; set; }
        public List<WarningIssue> Warnings { get; set; }
        public List<Recommendation> Recommendations { get; set; }
        public HealthStatus ComponentHealth { get; set; }
        public ResourceUtilization ResourceUtilization { get; set; }
        public List<PerformanceTrend> Trends { get; set; }
    }

    /// <summary>
    /// System health status;
    /// </summary>
    public enum SystemHealth
    {
        Unknown,
        Healthy,
        Warning,
        Critical
    }

    /// <summary>
    /// Health status;
    /// </summary>
    public class HealthStatus
    {
        public Dictionary<string, ComponentHealth> Components { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Component health;
    /// </summary>
    public class ComponentHealth
    {
        public string ComponentId { get; set; }
        public ComponentStatus Status { get; set; }
        public double HealthScore { get; set; }
        public List<string> Issues { get; set; }
        public DateTime LastChecked { get; set; }
    }

    /// <summary>
    /// Critical issue;
    /// </summary>
    public class CriticalIssue
    {
        public string Id { get; set; }
        public string ComponentId { get; set; }
        public string Description { get; set; }
        public DateTime DetectedAt { get; set; }
        public string RecommendedAction { get; set; }
    }

    /// <summary>
    /// Warning issue;
    /// </summary>
    public class WarningIssue
    {
        public string Id { get; set; }
        public string ComponentId { get; set; }
        public string Description { get; set; }
        public DateTime DetectedAt { get; set; }
        public string RecommendedAction { get; set; }
    }

    /// <summary>
    /// Recommendation;
    /// </summary>
    public class Recommendation
    {
        public string Id { get; set; }
        public string ComponentId { get; set; }
        public string Description { get; set; }
        public RecommendationType Type { get; set; }
        public string Action { get; set; }
        public int Priority { get; set; }
    }

    /// <summary>
    /// Recommendation type;
    /// </summary>
    public enum RecommendationType
    {
        Optimization,
        Maintenance,
        Upgrade,
        Configuration,
        Security
    }

    /// <summary>
    /// Resource utilization;
    /// </summary>
    public class ResourceUtilization
    {
        public double CpuUtilization { get; set; }
        public double MemoryUtilization { get; set; }
        public double DiskUtilization { get; set; }
        public double NetworkUtilization { get; set; }
    }

    /// <summary>
    /// Performance report;
    /// </summary>
    public class PerformanceReport
    {
        public DateTime GeneratedAt { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public PerformanceSummary Summary { get; set; }
        public List<DetailedMetric> DetailedMetrics { get; set; }
        public List<Anomaly> Anomalies { get; set; }
        public List<Recommendation> Recommendations { get; set; }
        public List<ComponentReport> ComponentReports { get; set; }
        public Statistics Statistics { get; set; }
    }

    /// <summary>
    /// Performance summary;
    /// </summary>
    public class PerformanceSummary
    {
        public double AverageCpuUsage { get; set; }
        public double AverageMemoryUsage { get; set; }
        public double AverageDiskUsage { get; set; }
        public double AverageNetworkUsage { get; set; }
        public int AlertCount { get; set; }
        public int CriticalAlertCount { get; set; }
        public TimeSpan Uptime { get; set; }
    }

    /// <summary>
    /// Detailed metric;
    /// </summary>
    public class DetailedMetric
    {
        public string MetricName { get; set; }
        public List<DataPoint> DataPoints { get; set; }
        public Statistics Statistics { get; set; }
    }

    /// <summary>
    /// Data point;
    /// </summary>
    public class DataPoint
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
    }

    /// <summary>
    /// Anomaly;
    /// </summary>
    public class Anomaly
    {
        public string Id { get; set; }
        public string MetricName { get; set; }
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
        public double ExpectedValue { get; set; }
        public double Deviation { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Component report;
    /// </summary>
    public class ComponentReport
    {
        public string ComponentId { get; set; }
        public string ComponentName { get; set; }
        public PerformanceMetrics Performance { get; set; }
        public List<HardwareAlert> Alerts { get; set; }
        public ComponentHealth Health { get; set; }
    }

    /// <summary>
    /// Statistics;
    /// </summary>
    public class Statistics
    {
        public double Average { get; set; }
        public double Median { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public double StandardDeviation { get; set; }
        public double Percentile90 { get; set; }
        public double Percentile95 { get; set; }
        public double Percentile99 { get; set; }
    }

    /// <summary>
    /// Performance metrics;
    /// </summary>
    public class PerformanceMetrics
    {
        public double CurrentValue { get; set; }
        public double AverageValue { get; set; }
        public double PeakValue { get; set; }
        public double MinimumValue { get; set; }
        public double Trend { get; set; }
    }

    /// <summary>
    /// Performance trend;
    /// </summary>
    public class PerformanceTrend
    {
        public string MetricName { get; set; }
        public string DisplayName { get; set; }
        public double CurrentValue { get; set; }
        public double PreviousValue { get; set; }
        public double ChangeAmount { get; set; }
        public double ChangePercentage { get; set; }
        public TrendDirection TrendDirection { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Trend direction;
    /// </summary>
    public enum TrendDirection
    {
        Increasing,
        Decreasing,
        Stable
    }

    /// <summary>
    /// Temperature reading;
    /// </summary>
    public class TemperatureReading
    {
        public string SensorId { get; set; }
        public string Location { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Fan speed;
    /// </summary>
    public class FanSpeed
    {
        public string FanId { get; set; }
        public string Location { get; set; }
        public int Rpm { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Power usage;
    /// </summary>
    public class PowerUsage
    {
        public double TotalWatts { get; set; }
        public double CpuWatts { get; set; }
        public double GpuWatts { get; set; }
        public double DiskWatts { get; set; }
        public double OtherWatts { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Drive info;
    /// </summary>
    public class DriveInfo
    {
        public string DriveLetter { get; set; }
        public string DriveType { get; set; }
        public ulong TotalSize { get; set; }
        public ulong FreeSpace { get; set; }
        public string FileSystem { get; set; }
    }

    /// <summary>
    /// Network connection;
    /// </summary>
    public class NetworkConnection
    {
        public string LocalAddress { get; set; }
        public string RemoteAddress { get; set; }
        public int LocalPort { get; set; }
        public int RemotePort { get; set; }
        public string State { get; set; }
        public string Protocol { get; set; }
    }

    /// <summary>
    /// Network adapter;
    /// </summary>
    public class NetworkAdapter
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string MacAddress { get; set; }
        public string[] IpAddresses { get; set; }
        public string Status { get; set; }
        public ulong Speed { get; set; }
    }

    /// <summary>
    /// Hardware sensor interface;
    /// </summary>
    public interface IHardwareSensor
    {
        Task StartAsync();
        Task StopAsync();
        Task<IEnumerable<SensorReading>> GetReadingsAsync();
    }

    /// <summary>
    /// Sensor reading;
    /// </summary>
    public class SensorReading
    {
        public string ComponentId { get; set; }
        public string MetricName { get; set; }
        public double Value { get; set; }
        public string Unit { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hardware provider interface;
    /// </summary>
    public interface IHardwareProvider
    {
        Task<IEnumerable<HardwareComponent>> DiscoverComponentsAsync();
        Task<IEnumerable<SensorReading>> GetComponentMetricsAsync(string componentId);
    }

    /// <summary>
    /// Hardware monitor interface;
    /// </summary>
    public interface IHardwareMonitor : IDisposable
    {
        Task StartMonitoringAsync();
        Task StopMonitoringAsync();
        Task<HardwareMetrics> GetMetricsAsync(bool includeHistory = false);
        Task<ComponentMetrics> GetComponentMetricsAsync(string componentId);
        Task<SystemHealthReport> GetSystemHealthAsync();
        Task<bool> AddAlertThresholdAsync(AlertThreshold threshold);
        Task<bool> RemoveAlertThresholdAsync(string componentId, string metricName, AlertCondition condition);
        Task<HardwareComponent> AddComponentAsync(HardwareComponent component);
        Task<bool> RemoveComponentAsync(string componentId);
        Task<PerformanceReport> GeneratePerformanceReportAsync(DateTime? startTime = null, DateTime? endTime = null);
        void AddExternalSensor(IHardwareSensor sensor);
        void AddHardwareProvider(IHardwareProvider provider);
        IDisposable Subscribe(IObserver<HardwareEvent> observer);
    }

    /// <summary>
    /// Hardware monitor exception;
    /// </summary>
    public class HardwareMonitorException : Exception
    {
        public string ErrorCode { get; }

        public HardwareMonitorException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public HardwareMonitorException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    #endregion
}
