using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using DynamicData;
using DynamicData.Binding;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.Core.SystemControl;
using NEDA.Monitoring.Diagnostics;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.Messaging.EventBus;
using ReactiveUI;

namespace NEDA.Interface.VisualInterface.RealTimeFeedback;
{
    /// <summary>
    /// Real-time monitoring system for live data visualization and feedback;
    /// </summary>
    public class LiveMonitor : ReactiveObject, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ISystemManager _systemManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IDiagnosticTool _diagnosticTool;
        private readonly IMetricsCollector _metricsCollector;
        private readonly CompositeDisposable _disposables;
        private readonly Subject<MonitoringData> _dataStream;
        private readonly SourceCache<MonitoringMetric, string> _metricsCache;
        private readonly ReadOnlyObservableCollection<MonitoringMetric> _visibleMetrics;
        private readonly Dictionary<string, Alert> _activeAlerts;
        private readonly object _syncLock = new object();
        private MonitoringConfig _config;
        private bool _isMonitoring;
        private bool _isPaused;
        private DateTime _startTime;
        private int _samplingRate;
        private MonitoringDisplayMode _displayMode;
        private double _updateFrequency;
        private AlertThreshold _alertThresholds;
        private MonitoringData _currentData;
        private CancellationTokenSource _monitoringCts;
        private Task _monitoringTask;
        private PerformanceHistory _performanceHistory;
        private Dictionary<string, MetricSeries> _metricSeries;
        private bool _isDisposed;

        /// <summary>
        /// Monitoring configuration;
        /// </summary>
        public MonitoringConfig Config;
        {
            get => _config;
            set => this.RaiseAndSetIfChanged(ref _config, value);
        }

        /// <summary>
        /// Whether monitoring is active;
        /// </summary>
        public bool IsMonitoring;
        {
            get => _isMonitoring;
            private set => this.RaiseAndSetIfChanged(ref _isMonitoring, value);
        }

        /// <summary>
        /// Whether monitoring is paused;
        /// </summary>
        public bool IsPaused;
        {
            get => _isPaused;
            private set => this.RaiseAndSetIfChanged(ref _isPaused, value);
        }

        /// <summary>
        /// Monitoring start time;
        /// </summary>
        public DateTime StartTime;
        {
            get => _startTime;
            private set => this.RaiseAndSetIfChanged(ref _startTime, value);
        }

        /// <summary>
        /// Sampling rate in milliseconds;
        /// </summary>
        public int SamplingRate;
        {
            get => _samplingRate;
            set;
            {
                if (value < 100) value = 100; // Minimum 100ms;
                if (value > 10000) value = 10000; // Maximum 10s;
                this.RaiseAndSetIfChanged(ref _samplingRate, value);
            }
        }

        /// <summary>
        /// Display mode for monitoring data;
        /// </summary>
        public MonitoringDisplayMode DisplayMode;
        {
            get => _displayMode;
            set => this.RaiseAndSetIfChanged(ref _displayMode, value);
        }

        /// <summary>
        /// Update frequency in Hz;
        /// </summary>
        public double UpdateFrequency;
        {
            get => _updateFrequency;
            set;
            {
                if (value < 0.1) value = 0.1; // Minimum 0.1 Hz;
                if (value > 60) value = 60; // Maximum 60 Hz;
                this.RaiseAndSetIfChanged(ref _updateFrequency, value);
            }
        }

        /// <summary>
        /// Alert thresholds;
        /// </summary>
        public AlertThreshold AlertThresholds;
        {
            get => _alertThresholds;
            set => this.RaiseAndSetIfChanged(ref _alertThresholds, value);
        }

        /// <summary>
        /// Current monitoring data;
        /// </summary>
        public MonitoringData CurrentData;
        {
            get => _currentData;
            private set => this.RaiseAndSetIfChanged(ref _currentData, value);
        }

        /// <summary>
        /// Observable metrics collection;
        /// </summary>
        public ReadOnlyObservableCollection<MonitoringMetric> VisibleMetrics => _visibleMetrics;

        /// <summary>
        /// Active alerts;
        /// </summary>
        public ObservableCollection<Alert> ActiveAlerts { get; }

        /// <summary>
        /// Performance history;
        /// </summary>
        public PerformanceHistory PerformanceHistory => _performanceHistory;

        /// <summary>
        /// Data stream for real-time updates;
        /// </summary>
        public IObservable<MonitoringData> DataStream => _dataStream.AsObservable();

        /// <summary>
        /// Total monitoring time;
        /// </summary>
        public TimeSpan MonitoringDuration => IsMonitoring ? DateTime.Now - StartTime : TimeSpan.Zero;

        /// <summary>
        /// Number of samples collected;
        /// </summary>
        public long SampleCount { get; private set; }

        /// <summary>
        /// CPU usage history;
        /// </summary>
        public ObservableCollection<DataPoint> CpuHistory { get; }

        /// <summary>
        /// Memory usage history;
        /// </summary>
        public ObservableCollection<DataPoint> MemoryHistory { get; }

        /// <summary>
        /// Disk usage history;
        /// </summary>
        public ObservableCollection<DataPoint> DiskHistory { get; }

        /// <summary>
        /// Network usage history;
        /// </summary>
        public ObservableCollection<DataPoint> NetworkHistory { get; }

        /// <summary>
        /// Event triggered when monitoring starts;
        /// </summary>
        public event EventHandler<MonitoringEventArgs> MonitoringStarted;

        /// <summary>
        /// Event triggered when monitoring stops;
        /// </summary>
        public event EventHandler<MonitoringEventArgs> MonitoringStopped;

        /// <summary>
        /// Event triggered when data is updated;
        /// </summary>
        public event EventHandler<DataUpdatedEventArgs> DataUpdated;

        /// <summary>
        /// Event triggered when alert is raised;
        /// </summary>
        public event EventHandler<AlertEventArgs> AlertRaised;

        /// <summary>
        /// Event triggered when alert is resolved;
        /// </summary>
        public event EventHandler<AlertEventArgs> AlertResolved;

        /// <summary>
        /// Creates a new LiveMonitor instance;
        /// </summary>
        public LiveMonitor(
            ILogger logger,
            IEventBus eventBus,
            ISystemManager systemManager,
            IPerformanceMonitor performanceMonitor,
            IDiagnosticTool diagnosticTool,
            IMetricsCollector metricsCollector)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _diagnosticTool = diagnosticTool ?? throw new ArgumentNullException(nameof(diagnosticTool));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));

            _disposables = new CompositeDisposable();
            _dataStream = new Subject<MonitoringData>();
            _metricsCache = new SourceCache<MonitoringMetric, string>(m => m.Id);
            _activeAlerts = new Dictionary<string, Alert>();
            _performanceHistory = new PerformanceHistory();
            _metricSeries = new Dictionary<string, MetricSeries>();

            ActiveAlerts = new ObservableCollection<Alert>();
            CpuHistory = new ObservableCollection<DataPoint>();
            MemoryHistory = new ObservableCollection<DataPoint>();
            DiskHistory = new ObservableCollection<DataPoint>();
            NetworkHistory = new ObservableCollection<DataPoint>();

            InitializeConfig();
            InitializeMetrics();
            SetupFiltering();
            SubscribeToEvents();

            _logger.LogInformation("LiveMonitor initialized successfully");
        }

        /// <summary>
        /// Starts real-time monitoring;
        /// </summary>
        public async Task StartMonitoringAsync()
        {
            if (IsMonitoring)
            {
                _logger.LogWarning("Monitoring is already running");
                return;
            }

            try
            {
                _monitoringCts = new CancellationTokenSource();
                StartTime = DateTime.Now;
                IsMonitoring = true;
                IsPaused = false;
                SampleCount = 0;

                // Clear history;
                ClearHistory();

                // Start monitoring task;
                _monitoringTask = Task.Run(async () => await MonitorLoopAsync(_monitoringCts.Token));

                // Subscribe to system events;
                await SubscribeToSystemEventsAsync();

                // Raise event;
                MonitoringStarted?.Invoke(this, new MonitoringEventArgs(StartTime));

                // Log;
                _logger.LogInformation($"Live monitoring started with sampling rate: {SamplingRate}ms");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error starting monitoring: {ex.Message}", ex);
                IsMonitoring = false;
                throw;
            }
        }

        /// <summary>
        /// Stops real-time monitoring;
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (!IsMonitoring)
            {
                _logger.LogWarning("Monitoring is not running");
                return;
            }

            try
            {
                // Cancel monitoring task;
                _monitoringCts?.Cancel();
                if (_monitoringTask != null)
                {
                    await _monitoringTask;
                }

                // Cleanup;
                _monitoringCts?.Dispose();
                _monitoringCts = null;
                _monitoringTask = null;

                IsMonitoring = false;
                IsPaused = false;

                // Unsubscribe from system events;
                await UnsubscribeFromSystemEventsAsync();

                // Raise event;
                MonitoringStopped?.Invoke(this, new MonitoringEventArgs(DateTime.Now));

                // Log;
                _logger.LogInformation($"Live monitoring stopped. Duration: {MonitoringDuration}, Samples: {SampleCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping monitoring: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Pauses monitoring;
        /// </summary>
        public void PauseMonitoring()
        {
            if (!IsMonitoring || IsPaused)
                return;

            IsPaused = true;
            _logger.LogInformation("Monitoring paused");
        }

        /// <summary>
        /// Resumes monitoring;
        /// </summary>
        public void ResumeMonitoring()
        {
            if (!IsMonitoring || !IsPaused)
                return;

            IsPaused = false;
            _logger.LogInformation("Monitoring resumed");
        }

        /// <summary>
        /// Collects a single sample of monitoring data;
        /// </summary>
        public async Task<MonitoringData> CollectSampleAsync()
        {
            try
            {
                var timestamp = DateTime.Now;
                var data = new MonitoringData;
                {
                    Timestamp = timestamp,
                    SampleId = SampleCount + 1;
                };

                // Collect system metrics;
                await CollectSystemMetricsAsync(data);

                // Collect performance metrics;
                await CollectPerformanceMetricsAsync(data);

                // Collect application metrics;
                await CollectApplicationMetricsAsync(data);

                // Collect custom metrics;
                await CollectCustomMetricsAsync(data);

                // Update current data;
                CurrentData = data;

                // Add to history;
                AddToHistory(data);

                // Publish to stream;
                _dataStream.OnNext(data);

                // Check alerts;
                await CheckAlertsAsync(data);

                // Update metric cache;
                UpdateMetricCache(data);

                SampleCount++;

                // Raise event;
                DataUpdated?.Invoke(this, new DataUpdatedEventArgs(data));

                return data;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error collecting sample: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Sets up monitoring for specific metric;
        /// </summary>
        public void MonitorMetric(string metricId, MetricConfig config)
        {
            try
            {
                if (string.IsNullOrEmpty(metricId))
                    throw new ArgumentException("Metric ID cannot be null or empty", nameof(metricId));

                var metric = new MonitoringMetric;
                {
                    Id = metricId,
                    Name = config.Name ?? metricId,
                    Category = config.Category,
                    Unit = config.Unit,
                    Description = config.Description,
                    IsEnabled = config.IsEnabled,
                    MinValue = config.MinValue,
                    MaxValue = config.MaxValue,
                    WarningThreshold = config.WarningThreshold,
                    CriticalThreshold = config.CriticalThreshold,
                    DisplayOrder = config.DisplayOrder;
                };

                _metricsCache.AddOrUpdate(metric);

                if (!_metricSeries.ContainsKey(metricId))
                {
                    _metricSeries[metricId] = new MetricSeries(metricId, config.HistorySize);
                }

                _logger.LogDebug($"Metric monitoring configured: {metricId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error setting up metric monitoring: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Removes metric from monitoring;
        /// </summary>
        public void RemoveMetric(string metricId)
        {
            try
            {
                _metricsCache.Remove(metricId);
                _metricSeries.Remove(metricId);
                _logger.LogDebug($"Metric removed from monitoring: {metricId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error removing metric: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets metric history;
        /// </summary>
        public IEnumerable<DataPoint> GetMetricHistory(string metricId, TimeSpan? timeWindow = null)
        {
            if (_metricSeries.TryGetValue(metricId, out var series))
            {
                var points = series.GetPoints();
                if (timeWindow.HasValue)
                {
                    var cutoff = DateTime.Now - timeWindow.Value;
                    return points.Where(p => p.Timestamp >= cutoff);
                }
                return points;
            }
            return Enumerable.Empty<DataPoint>();
        }

        /// <summary>
        /// Gets metric statistics;
        /// </summary>
        public MetricStatistics GetMetricStatistics(string metricId, TimeSpan? timeWindow = null)
        {
            var points = GetMetricHistory(metricId, timeWindow).ToList();
            if (points.Count == 0)
                return new MetricStatistics();

            var values = points.Select(p => p.Value).ToList();
            return new MetricStatistics;
            {
                Count = points.Count,
                Average = values.Average(),
                Min = values.Min(),
                Max = values.Max(),
                StandardDeviation = CalculateStandardDeviation(values),
                FirstTimestamp = points.Min(p => p.Timestamp),
                LastTimestamp = points.Max(p => p.Timestamp)
            };
        }

        /// <summary>
        /// Raises a custom alert;
        /// </summary>
        public void RaiseAlert(string alertId, string title, string message, AlertSeverity severity,
            string metricId = null, double? metricValue = null)
        {
            try
            {
                var alert = new Alert;
                {
                    Id = alertId,
                    Title = title,
                    Message = message,
                    Severity = severity,
                    Timestamp = DateTime.Now,
                    MetricId = metricId,
                    MetricValue = metricValue,
                    Status = AlertStatus.Active;
                };

                lock (_syncLock)
                {
                    if (_activeAlerts.ContainsKey(alertId))
                    {
                        // Update existing alert;
                        _activeAlerts[alertId] = alert;
                    }
                    else;
                    {
                        // Add new alert;
                        _activeAlerts[alertId] = alert;
                        ActiveAlerts.Add(alert);
                    }
                }

                // Raise event;
                AlertRaised?.Invoke(this, new AlertEventArgs(alert));

                // Log;
                _logger.Log(GetLogLevelForSeverity(severity), $"Alert raised: {title} - {message}");

                // Publish event;
                _eventBus.Publish(new AlertRaisedEvent(alert));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error raising alert: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Resolves an alert;
        /// </summary>
        public void ResolveAlert(string alertId, string resolutionMessage = null)
        {
            try
            {
                if (!_activeAlerts.TryGetValue(alertId, out var alert))
                    return;

                alert.Status = AlertStatus.Resolved;
                alert.ResolvedAt = DateTime.Now;
                alert.ResolutionMessage = resolutionMessage ?? "Alert resolved";

                lock (_syncLock)
                {
                    _activeAlerts.Remove(alertId);
                    ActiveAlerts.Remove(alert);
                }

                // Raise event;
                AlertResolved?.Invoke(this, new AlertEventArgs(alert));

                // Log;
                _logger.LogInformation($"Alert resolved: {alert.Title}");

                // Publish event;
                _eventBus.Publish(new AlertResolvedEvent(alert));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resolving alert: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Clears all alerts;
        /// </summary>
        public void ClearAlerts()
        {
            try
            {
                lock (_syncLock)
                {
                    _activeAlerts.Clear();
                    ActiveAlerts.Clear();
                }
                _logger.LogInformation("All alerts cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error clearing alerts: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Exports monitoring data;
        /// </summary>
        public async Task ExportDataAsync(ExportFormat format, string filePath,
            DateTime? startTime = null, DateTime? endTime = null)
        {
            try
            {
                var exportData = new MonitoringExport;
                {
                    ExportTime = DateTime.Now,
                    StartTime = startTime ?? StartTime,
                    EndTime = endTime ?? DateTime.Now,
                    Config = Config,
                    Samples = GetSamplesInRange(startTime, endTime).ToList(),
                    Alerts = ActiveAlerts.ToList(),
                    Metrics = _metricsCache.Items.ToList()
                };

                switch (format)
                {
                    case ExportFormat.JSON:
                        await ExportToJsonAsync(exportData, filePath);
                        break;
                    case ExportFormat.CSV:
                        await ExportToCsvAsync(exportData, filePath);
                        break;
                    case ExportFormat.XML:
                        await ExportToXmlAsync(exportData, filePath);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(format));
                }

                _logger.LogInformation($"Monitoring data exported to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error exporting data: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Gets performance report;
        /// </summary>
        public PerformanceReport GetPerformanceReport(TimeSpan? period = null)
        {
            try
            {
                var endTime = DateTime.Now;
                var startTime = period.HasValue ? endTime - period.Value : StartTime;

                var samples = GetSamplesInRange(startTime, endTime).ToList();
                if (samples.Count == 0)
                    return new PerformanceReport();

                var cpuValues = samples.Select(s => s.CpuUsage).ToList();
                var memoryValues = samples.Select(s => s.MemoryUsage).ToList();
                var diskValues = samples.Select(s => s.DiskUsage).ToList();

                return new PerformanceReport;
                {
                    PeriodStart = startTime,
                    PeriodEnd = endTime,
                    SampleCount = samples.Count,
                    AverageCpuUsage = cpuValues.Average(),
                    AverageMemoryUsage = memoryValues.Average(),
                    AverageDiskUsage = diskValues.Average(),
                    MaxCpuUsage = cpuValues.Max(),
                    MaxMemoryUsage = memoryValues.Max(),
                    MaxDiskUsage = diskValues.Max(),
                    CpuUtilizationTrend = CalculateTrend(cpuValues),
                    MemoryUtilizationTrend = CalculateTrend(memoryValues),
                    AlertCount = ActiveAlerts.Count,
                    Recommendations = GenerateRecommendations(samples)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating performance report: {ex.Message}", ex);
                return new PerformanceReport();
            }
        }

        /// <summary>
        /// Resets monitoring data;
        /// </summary>
        public void Reset()
        {
            try
            {
                ClearHistory();
                ClearAlerts();
                _metricsCache.Clear();
                _metricSeries.Clear();
                SampleCount = 0;
                CurrentData = null;

                _logger.LogInformation("Monitoring data reset");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resetting monitoring data: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            try
            {
                if (IsMonitoring)
                {
                    StopMonitoringAsync().Wait(5000);
                }

                _disposables?.Dispose();
                _monitoringCts?.Dispose();
                _dataStream?.OnCompleted();
                _dataStream?.Dispose();
                _metricsCache?.Dispose();

                _isDisposed = true;
                GC.SuppressFinalize(this);

                _logger.LogInformation("LiveMonitor disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error disposing LiveMonitor: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Main monitoring loop;
        /// </summary>
        private async Task MonitorLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Monitoring loop started");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (IsPaused)
                    {
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }

                    var sampleTask = CollectSampleAsync();
                    var delayTask = Task.Delay(SamplingRate, cancellationToken);

                    await Task.WhenAll(sampleTask, delayTask);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Monitoring loop cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in monitoring loop: {ex.Message}", ex);
            }
            finally
            {
                _logger.LogInformation("Monitoring loop ended");
            }
        }

        /// <summary>
        /// Collects system metrics;
        /// </summary>
        private async Task CollectSystemMetricsAsync(MonitoringData data)
        {
            try
            {
                // Get CPU usage;
                data.CpuUsage = await _performanceMonitor.GetCpuUsageAsync();
                data.CpuCores = Environment.ProcessorCount;

                // Get memory usage;
                var memoryInfo = await _performanceMonitor.GetMemoryInfoAsync();
                data.MemoryUsage = memoryInfo.UsagePercentage;
                data.TotalMemory = memoryInfo.TotalBytes;
                data.UsedMemory = memoryInfo.UsedBytes;
                data.AvailableMemory = memoryInfo.AvailableBytes;

                // Get disk usage;
                var diskInfo = await _performanceMonitor.GetDiskInfoAsync();
                data.DiskUsage = diskInfo.UsagePercentage;
                data.TotalDiskSpace = diskInfo.TotalBytes;
                data.UsedDiskSpace = diskInfo.UsedBytes;
                data.AvailableDiskSpace = diskInfo.AvailableBytes;

                // Get network usage;
                var networkInfo = await _performanceMonitor.GetNetworkInfoAsync();
                data.NetworkUploadSpeed = networkInfo.UploadSpeed;
                data.NetworkDownloadSpeed = networkInfo.DownloadSpeed;
                data.NetworkUsage = networkInfo.UsagePercentage;

                // Get process info;
                data.ProcessCount = Process.GetProcesses().Length;
                data.ThreadCount = Process.GetCurrentProcess().Threads.Count;
                data.HandleCount = Process.GetCurrentProcess().HandleCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error collecting system metrics: {ex.Message}");
            }
        }

        /// <summary>
        /// Collects performance metrics;
        /// </summary>
        private async Task CollectPerformanceMetricsAsync(MonitoringData data)
        {
            try
            {
                data.PerformanceMetrics = await _metricsCollector.CollectMetricsAsync();
                data.ResponseTime = await _diagnosticTool.MeasureResponseTimeAsync();
                data.Throughput = await _diagnosticTool.MeasureThroughputAsync();
                data.ErrorRate = await _diagnosticTool.GetErrorRateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error collecting performance metrics: {ex.Message}");
            }
        }

        /// <summary>
        /// Collects application metrics;
        /// </summary>
        private async Task CollectApplicationMetricsAsync(MonitoringData data)
        {
            try
            {
                data.ApplicationMetrics = new Dictionary<string, double>();

                // Collect metrics from enabled metrics;
                foreach (var metric in _metricsCache.Items.Where(m => m.IsEnabled))
                {
                    var value = await GetMetricValueAsync(metric.Id);
                    if (value.HasValue)
                    {
                        data.ApplicationMetrics[metric.Id] = value.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error collecting application metrics: {ex.Message}");
            }
        }

        /// <summary>
        /// Collects custom metrics;
        /// </summary>
        private async Task CollectCustomMetricsAsync(MonitoringData data)
        {
            try
            {
                data.CustomMetrics = new Dictionary<string, object>();

                // Add timestamp-based metrics;
                data.CustomMetrics["Timestamp"] = data.Timestamp;
                data.CustomMetrics["Uptime"] = MonitoringDuration.TotalSeconds;
                data.CustomMetrics["SampleCount"] = SampleCount;

                // Add system info;
                data.CustomMetrics["OSVersion"] = Environment.OSVersion.VersionString;
                data.CustomMetrics["MachineName"] = Environment.MachineName;
                data.CustomMetrics["UserName"] = Environment.UserName;

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error collecting custom metrics: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks for alert conditions;
        /// </summary>
        private async Task CheckAlertsAsync(MonitoringData data)
        {
            try
            {
                // Check system alerts;
                CheckSystemAlerts(data);

                // Check metric alerts;
                await CheckMetricAlertsAsync(data);

                // Check custom alerts;
                CheckCustomAlerts(data);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error checking alerts: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks system alerts;
        /// </summary>
        private void CheckSystemAlerts(MonitoringData data)
        {
            // CPU alerts;
            if (data.CpuUsage >= AlertThresholds.CriticalCpuUsage)
            {
                RaiseAlert($"CPU_CRITICAL_{data.Timestamp:HHmmss}",
                    "Critical CPU Usage",
                    $"CPU usage is critically high: {data.CpuUsage:F1}%",
                    AlertSeverity.Critical,
                    "cpu_usage",
                    data.CpuUsage);
            }
            else if (data.CpuUsage >= AlertThresholds.WarningCpuUsage)
            {
                RaiseAlert($"CPU_WARNING_{data.Timestamp:HHmmss}",
                    "High CPU Usage",
                    $"CPU usage is high: {data.CpuUsage:F1}%",
                    AlertSeverity.Warning,
                    "cpu_usage",
                    data.CpuUsage);
            }

            // Memory alerts;
            if (data.MemoryUsage >= AlertThresholds.CriticalMemoryUsage)
            {
                RaiseAlert($"MEMORY_CRITICAL_{data.Timestamp:HHmmss}",
                    "Critical Memory Usage",
                    $"Memory usage is critically high: {data.MemoryUsage:F1}%",
                    AlertSeverity.Critical,
                    "memory_usage",
                    data.MemoryUsage);
            }
            else if (data.MemoryUsage >= AlertThresholds.WarningMemoryUsage)
            {
                RaiseAlert($"MEMORY_WARNING_{data.Timestamp:HHmmss}",
                    "High Memory Usage",
                    $"Memory usage is high: {data.MemoryUsage:F1}%",
                    AlertSeverity.Warning,
                    "memory_usage",
                    data.MemoryUsage);
            }

            // Disk alerts;
            if (data.DiskUsage >= AlertThresholds.CriticalDiskUsage)
            {
                RaiseAlert($"DISK_CRITICAL_{data.Timestamp:HHmmss}",
                    "Critical Disk Usage",
                    $"Disk usage is critically high: {data.DiskUsage:F1}%",
                    AlertSeverity.Critical,
                    "disk_usage",
                    data.DiskUsage);
            }
            else if (data.DiskUsage >= AlertThresholds.WarningDiskUsage)
            {
                RaiseAlert($"DISK_WARNING_{data.Timestamp:HHmmss}",
                    "High Disk Usage",
                    $"Disk usage is high: {data.DiskUsage:F1}%",
                    AlertSeverity.Warning,
                    "disk_usage",
                    data.DiskUsage);
            }
        }

        /// <summary>
        /// Checks metric alerts;
        /// </summary>
        private async Task CheckMetricAlertsAsync(MonitoringData data)
        {
            foreach (var metric in _metricsCache.Items)
            {
                if (!metric.IsEnabled || !data.ApplicationMetrics.TryGetValue(metric.Id, out var value))
                    continue;

                if (metric.CriticalThreshold.HasValue && value >= metric.CriticalThreshold.Value)
                {
                    RaiseAlert($"{metric.Id}_CRITICAL_{data.Timestamp:HHmmss}",
                        $"Critical: {metric.Name}",
                        $"{metric.Name} reached critical level: {value:F2}{metric.Unit}",
                        AlertSeverity.Critical,
                        metric.Id,
                        value);
                }
                else if (metric.WarningThreshold.HasValue && value >= metric.WarningThreshold.Value)
                {
                    RaiseAlert($"{metric.Id}_WARNING_{data.Timestamp:HHmmss}",
                        $"Warning: {metric.Name}",
                        $"{metric.Name} reached warning level: {value:F2}{metric.Unit}",
                        AlertSeverity.Warning,
                        metric.Id,
                        value);
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Checks custom alerts;
        /// </summary>
        private void CheckCustomAlerts(MonitoringData data)
        {
            // Add custom alert logic here based on specific requirements;
            // Example: Check for specific patterns or combinations of metrics;
        }

        /// <summary>
        /// Initializes configuration;
        /// </summary>
        private void InitializeConfig()
        {
            Config = new MonitoringConfig;
            {
                Name = "Live Monitoring",
                Description = "Real-time system and application monitoring",
                Version = "1.0.0"
            };

            SamplingRate = 1000; // 1 second;
            UpdateFrequency = 1.0; // 1 Hz;
            DisplayMode = MonitoringDisplayMode.RealTime;

            AlertThresholds = new AlertThreshold;
            {
                WarningCpuUsage = 70.0,
                CriticalCpuUsage = 90.0,
                WarningMemoryUsage = 75.0,
                CriticalMemoryUsage = 90.0,
                WarningDiskUsage = 80.0,
                CriticalDiskUsage = 95.0,
                WarningResponseTime = 1000.0, // 1 second;
                CriticalResponseTime = 5000.0 // 5 seconds;
            };
        }

        /// <summary>
        /// Initializes default metrics;
        /// </summary>
        private void InitializeMetrics()
        {
            var defaultMetrics = new[]
            {
                new MonitoringMetric;
                {
                    Id = "cpu_usage",
                    Name = "CPU Usage",
                    Category = "System",
                    Unit = "%",
                    Description = "Percentage of CPU utilization",
                    IsEnabled = true,
                    MinValue = 0,
                    MaxValue = 100,
                    WarningThreshold = 70,
                    CriticalThreshold = 90,
                    DisplayOrder = 1;
                },
                new MonitoringMetric;
                {
                    Id = "memory_usage",
                    Name = "Memory Usage",
                    Category = "System",
                    Unit = "%",
                    Description = "Percentage of memory utilization",
                    IsEnabled = true,
                    MinValue = 0,
                    MaxValue = 100,
                    WarningThreshold = 75,
                    CriticalThreshold = 90,
                    DisplayOrder = 2;
                },
                new MonitoringMetric;
                {
                    Id = "disk_usage",
                    Name = "Disk Usage",
                    Category = "System",
                    Unit = "%",
                    Description = "Percentage of disk utilization",
                    IsEnabled = true,
                    MinValue = 0,
                    MaxValue = 100,
                    WarningThreshold = 80,
                    CriticalThreshold = 95,
                    DisplayOrder = 3;
                },
                new MonitoringMetric;
                {
                    Id = "response_time",
                    Name = "Response Time",
                    Category = "Performance",
                    Unit = "ms",
                    Description = "System response time in milliseconds",
                    IsEnabled = true,
                    WarningThreshold = 1000,
                    CriticalThreshold = 5000,
                    DisplayOrder = 4;
                },
                new MonitoringMetric;
                {
                    Id = "error_rate",
                    Name = "Error Rate",
                    Category = "Performance",
                    Unit = "%",
                    Description = "Percentage of failed operations",
                    IsEnabled = true,
                    MinValue = 0,
                    MaxValue = 100,
                    WarningThreshold = 5,
                    CriticalThreshold = 10,
                    DisplayOrder = 5;
                }
            };

            foreach (var metric in defaultMetrics)
            {
                _metricsCache.AddOrUpdate(metric);
                _metricSeries[metric.Id] = new MetricSeries(metric.Id, 1000);
            }
        }

        /// <summary>
        /// Sets up filtering for metrics;
        /// </summary>
        private void SetupFiltering()
        {
            _metricsCache.Connect()
                .Filter(m => m.IsEnabled)
                .Sort(SortExpressionComparer<MonitoringMetric>.Ascending(m => m.DisplayOrder))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _visibleMetrics)
                .Subscribe()
                .DisposeWith(_disposables);
        }

        /// <summary>
        /// Subscribes to system events;
        /// </summary>
        private async Task SubscribeToSystemEventsAsync()
        {
            try
            {
                // Subscribe to performance events;
                await _performanceMonitor.SubscribeToMetricsAsync(OnPerformanceMetricReceived);

                // Subscribe to diagnostic events;
                _diagnosticTool.DiagnosticsUpdated += OnDiagnosticsUpdated;

                // Subscribe to system events;
                _systemManager.SystemEvent += OnSystemEvent;

                _logger.LogDebug("Subscribed to system events");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error subscribing to system events: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Unsubscribes from system events;
        /// </summary>
        private async Task UnsubscribeFromSystemEventsAsync()
        {
            try
            {
                // Unsubscribe from performance events;
                await _performanceMonitor.UnsubscribeFromMetricsAsync(OnPerformanceMetricReceived);

                // Unsubscribe from diagnostic events;
                _diagnosticTool.DiagnosticsUpdated -= OnDiagnosticsUpdated;

                // Unsubscribe from system events;
                _systemManager.SystemEvent -= OnSystemEvent;

                _logger.LogDebug("Unsubscribed from system events");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error unsubscribing from system events: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Subscribes to application events;
        /// </summary>
        private void SubscribeToEvents()
        {
            // Subscribe to event bus for application events;
            _eventBus.Subscribe<ApplicationEvent>(OnApplicationEvent);
            _eventBus.Subscribe<PerformanceEvent>(OnPerformanceEvent);
            _eventBus.Subscribe<ErrorEvent>(OnErrorEvent);
        }

        // Event handlers;
        private void OnPerformanceMetricReceived(PerformanceMetric metric)
        {
            // Handle performance metrics;
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                // Update metric series;
                if (_metricSeries.TryGetValue(metric.Name, out var series))
                {
                    series.AddPoint(new DataPoint(DateTime.Now, metric.Value));
                }
            });
        }

        private void OnDiagnosticsUpdated(object sender, DiagnosticsEventArgs e)
        {
            // Handle diagnostic updates;
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                // Update monitoring data;
                if (CurrentData != null)
                {
                    CurrentData.Diagnostics = e.Diagnostics;
                    _dataStream.OnNext(CurrentData);
                }
            });
        }

        private void OnSystemEvent(object sender, SystemEventArgs e)
        {
            // Handle system events;
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                if (e.EventType == SystemEventType.Critical)
                {
                    RaiseAlert($"SYSTEM_{e.EventType}_{DateTime.Now:HHmmss}",
                        $"System Event: {e.EventType}",
                        e.Message,
                        AlertSeverity.Critical);
                }
            });
        }

        private void OnApplicationEvent(ApplicationEvent e)
        {
            // Handle application events;
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                // Update application metrics;
                if (CurrentData != null && e.Metrics != null)
                {
                    foreach (var metric in e.Metrics)
                    {
                        CurrentData.ApplicationMetrics[metric.Key] = metric.Value;
                    }
                    _dataStream.OnNext(CurrentData);
                }
            });
        }

        private void OnPerformanceEvent(PerformanceEvent e)
        {
            // Handle performance events;
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                // Update performance metrics;
                if (CurrentData != null)
                {
                    CurrentData.PerformanceMetrics[e.MetricName] = e.Value;
                    _dataStream.OnNext(CurrentData);
                }
            });
        }

        private void OnErrorEvent(ErrorEvent e)
        {
            // Handle error events;
            RxApp.MainThreadScheduler.Schedule(() =>
            {
                RaiseAlert($"ERROR_{e.ErrorCode}_{DateTime.Now:HHmmss}",
                    $"Error: {e.ErrorCode}",
                    e.Message,
                    AlertSeverity.Error);
            });
        }

        // Helper methods;
        private void AddToHistory(MonitoringData data)
        {
            // Add to CPU history;
            CpuHistory.Add(new DataPoint(data.Timestamp, data.CpuUsage));
            if (CpuHistory.Count > 1000) CpuHistory.RemoveAt(0);

            // Add to memory history;
            MemoryHistory.Add(new DataPoint(data.Timestamp, data.MemoryUsage));
            if (MemoryHistory.Count > 1000) MemoryHistory.RemoveAt(0);

            // Add to disk history;
            DiskHistory.Add(new DataPoint(data.Timestamp, data.DiskUsage));
            if (DiskHistory.Count > 1000) DiskHistory.RemoveAt(0);

            // Add to network history;
            NetworkHistory.Add(new DataPoint(data.Timestamp, data.NetworkUsage));
            if (NetworkHistory.Count > 1000) NetworkHistory.RemoveAt(0);

            // Add to metric series;
            foreach (var kvp in data.ApplicationMetrics)
            {
                if (_metricSeries.TryGetValue(kvp.Key, out var series))
                {
                    series.AddPoint(new DataPoint(data.Timestamp, kvp.Value));
                }
            }

            // Update performance history;
            _performanceHistory.AddSample(data);
        }

        private void ClearHistory()
        {
            CpuHistory.Clear();
            MemoryHistory.Clear();
            DiskHistory.Clear();
            NetworkHistory.Clear();

            foreach (var series in _metricSeries.Values)
            {
                series.Clear();
            }

            _performanceHistory.Clear();
        }

        private async Task<double?> GetMetricValueAsync(string metricId)
        {
            // This method should be implemented based on specific metric sources;
            // For now, return random values for demonstration;
            await Task.CompletedTask;
            var random = new Random();
            return random.NextDouble() * 100;
        }

        private void UpdateMetricCache(MonitoringData data)
        {
            foreach (var metric in _metricsCache.Items)
            {
                if (data.ApplicationMetrics.TryGetValue(metric.Id, out var value))
                {
                    metric.CurrentValue = value;
                    metric.LastUpdated = data.Timestamp;
                }
            }
        }

        private IEnumerable<MonitoringData> GetSamplesInRange(DateTime? startTime, DateTime? endTime)
        {
            // This should return samples from performance history;
            // For now, return empty enumerable - implement based on storage strategy;
            return Enumerable.Empty<MonitoringData>();
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            if (values.Count < 2) return 0;

            var mean = values.Average();
            var sum = values.Sum(v => Math.Pow(v - mean, 2));
            return Math.Sqrt(sum / (values.Count - 1));
        }

        private double CalculateTrend(List<double> values)
        {
            if (values.Count < 2) return 0;

            // Simple linear regression for trend;
            var xValues = Enumerable.Range(0, values.Count).Select(x => (double)x).ToList();
            var yValues = values;

            var xMean = xValues.Average();
            var yMean = yValues.Average();

            var numerator = xValues.Zip(yValues, (x, y) => (x - xMean) * (y - yMean)).Sum();
            var denominator = xValues.Sum(x => Math.Pow(x - xMean, 2));

            if (Math.Abs(denominator) < 0.0001) return 0;

            return numerator / denominator;
        }

        private List<string> GenerateRecommendations(List<MonitoringData> samples)
        {
            var recommendations = new List<string>();

            var avgCpu = samples.Average(s => s.CpuUsage);
            if (avgCpu > 80)
            {
                recommendations.Add("Consider optimizing CPU-intensive operations or scaling resources.");
            }

            var avgMemory = samples.Average(s => s.MemoryUsage);
            if (avgMemory > 85)
            {
                recommendations.Add("Memory usage is high. Consider memory optimization or increasing available memory.");
            }

            var avgDisk = samples.Average(s => s.DiskUsage);
            if (avgDisk > 90)
            {
                recommendations.Add("Disk space is critically low. Consider cleanup or storage expansion.");
            }

            return recommendations;
        }

        private LogLevel GetLogLevelForSeverity(AlertSeverity severity)
        {
            return severity switch;
            {
                AlertSeverity.Info => LogLevel.Information,
                AlertSeverity.Warning => LogLevel.Warning,
                AlertSeverity.Error => LogLevel.Error,
                AlertSeverity.Critical => LogLevel.Critical,
                _ => LogLevel.Information;
            };
        }

        // Export methods;
        private async Task ExportToJsonAsync(MonitoringExport exportData, string filePath)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(exportData,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);
        }

        private async Task ExportToCsvAsync(MonitoringExport exportData, string filePath)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Timestamp,CPU,Memory,Disk,Network");

            foreach (var sample in exportData.Samples)
            {
                csv.AppendLine($"{sample.Timestamp},{sample.CpuUsage},{sample.MemoryUsage},{sample.DiskUsage},{sample.NetworkUsage}");
            }

            await System.IO.File.WriteAllTextAsync(filePath, csv.ToString());
        }

        private async Task ExportToXmlAsync(MonitoringExport exportData, string filePath)
        {
            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine("<MonitoringExport>");
            xml.AppendLine($"<ExportTime>{exportData.ExportTime}</ExportTime>");
            xml.AppendLine($"<SampleCount>{exportData.Samples.Count}</SampleCount>");
            xml.AppendLine("</MonitoringExport>");

            await System.IO.File.WriteAllTextAsync(filePath, xml.ToString());
        }
    }

    // Supporting classes and enums;
    public class MonitoringData;
    {
        public DateTime Timestamp { get; set; }
        public long SampleId { get; set; }
        public double CpuUsage { get; set; }
        public int CpuCores { get; set; }
        public double MemoryUsage { get; set; }
        public long TotalMemory { get; set; }
        public long UsedMemory { get; set; }
        public long AvailableMemory { get; set; }
        public double DiskUsage { get; set; }
        public long TotalDiskSpace { get; set; }
        public long UsedDiskSpace { get; set; }
        public long AvailableDiskSpace { get; set; }
        public double NetworkUsage { get; set; }
        public double NetworkUploadSpeed { get; set; }
        public double NetworkDownloadSpeed { get; set; }
        public int ProcessCount { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }
        public double ResponseTime { get; set; }
        public double Throughput { get; set; }
        public double ErrorRate { get; set; }
        public Dictionary<string, double> PerformanceMetrics { get; set; }
        public Dictionary<string, double> ApplicationMetrics { get; set; }
        public Dictionary<string, object> CustomMetrics { get; set; }
        public object Diagnostics { get; set; }
    }

    public class MonitoringMetric;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Unit { get; set; }
        public string Description { get; set; }
        public bool IsEnabled { get; set; }
        public double? CurrentValue { get; set; }
        public DateTime? LastUpdated { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public double? WarningThreshold { get; set; }
        public double? CriticalThreshold { get; set; }
        public int DisplayOrder { get; set; }
        public Color DisplayColor { get; set; }
    }

    public class Alert;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public AlertSeverity Severity { get; set; }
        public DateTime Timestamp { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public AlertStatus Status { get; set; }
        public string MetricId { get; set; }
        public double? MetricValue { get; set; }
        public string ResolutionMessage { get; set; }
        public Color SeverityColor => GetSeverityColor();

        private Color GetSeverityColor()
        {
            return Severity switch;
            {
                AlertSeverity.Info => Colors.Blue,
                AlertSeverity.Warning => Colors.Orange,
                AlertSeverity.Error => Colors.Red,
                AlertSeverity.Critical => Colors.DarkRed,
                _ => Colors.Gray;
            };
        }
    }

    public class DataPoint;
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }

        public DataPoint(DateTime timestamp, double value)
        {
            Timestamp = timestamp;
            Value = value;
        }
    }

    public class MetricSeries;
    {
        private readonly LinkedList<DataPoint> _points;
        private readonly int _maxPoints;

        public string MetricId { get; }
        public int Count => _points.Count;

        public MetricSeries(string metricId, int maxPoints = 1000)
        {
            MetricId = metricId;
            _maxPoints = maxPoints;
            _points = new LinkedList<DataPoint>();
        }

        public void AddPoint(DataPoint point)
        {
            _points.AddLast(point);
            while (_points.Count > _maxPoints)
            {
                _points.RemoveFirst();
            }
        }

        public IEnumerable<DataPoint> GetPoints()
        {
            return _points;
        }

        public void Clear()
        {
            _points.Clear();
        }
    }

    public class PerformanceHistory;
    {
        private readonly List<MonitoringData> _samples;
        private readonly int _maxSamples;

        public PerformanceHistory(int maxSamples = 10000)
        {
            _maxSamples = maxSamples;
            _samples = new List<MonitoringData>(maxSamples);
        }

        public void AddSample(MonitoringData sample)
        {
            lock (_samples)
            {
                _samples.Add(sample);
                if (_samples.Count > _maxSamples)
                {
                    _samples.RemoveRange(0, _samples.Count - _maxSamples);
                }
            }
        }

        public IEnumerable<MonitoringData> GetSamples(DateTime? from = null, DateTime? to = null)
        {
            lock (_samples)
            {
                var query = _samples.AsEnumerable();
                if (from.HasValue)
                    query = query.Where(s => s.Timestamp >= from.Value);
                if (to.HasValue)
                    query = query.Where(s => s.Timestamp <= to.Value);
                return query.ToList();
            }
        }

        public void Clear()
        {
            lock (_samples)
            {
                _samples.Clear();
            }
        }
    }

    public class MonitoringConfig;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public bool AutoStart { get; set; }
        public bool EnableAlerts { get; set; }
        public bool EnableLogging { get; set; }
        public int DataRetentionDays { get; set; } = 30;
    }

    public class AlertThreshold;
    {
        public double WarningCpuUsage { get; set; }
        public double CriticalCpuUsage { get; set; }
        public double WarningMemoryUsage { get; set; }
        public double CriticalMemoryUsage { get; set; }
        public double WarningDiskUsage { get; set; }
        public double CriticalDiskUsage { get; set; }
        public double WarningResponseTime { get; set; }
        public double CriticalResponseTime { get; set; }
    }

    public class MetricConfig;
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Unit { get; set; }
        public string Description { get; set; }
        public bool IsEnabled { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public double? WarningThreshold { get; set; }
        public double? CriticalThreshold { get; set; }
        public int DisplayOrder { get; set; }
        public int HistorySize { get; set; } = 1000;
    }

    public class MetricStatistics;
    {
        public int Count { get; set; }
        public double Average { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double StandardDeviation { get; set; }
        public DateTime? FirstTimestamp { get; set; }
        public DateTime? LastTimestamp { get; set; }
    }

    public class PerformanceReport;
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int SampleCount { get; set; }
        public double AverageCpuUsage { get; set; }
        public double AverageMemoryUsage { get; set; }
        public double AverageDiskUsage { get; set; }
        public double MaxCpuUsage { get; set; }
        public double MaxMemoryUsage { get; set; }
        public double MaxDiskUsage { get; set; }
        public double CpuUtilizationTrend { get; set; }
        public double MemoryUtilizationTrend { get; set; }
        public int AlertCount { get; set; }
        public List<string> Recommendations { get; set; } = new List<string>();
    }

    public class MonitoringExport;
    {
        public DateTime ExportTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public MonitoringConfig Config { get; set; }
        public List<MonitoringData> Samples { get; set; }
        public List<Alert> Alerts { get; set; }
        public List<MonitoringMetric> Metrics { get; set; }
    }

    // Event classes;
    public class MonitoringEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; }

        public MonitoringEventArgs(DateTime timestamp)
        {
            Timestamp = timestamp;
        }
    }

    public class DataUpdatedEventArgs : EventArgs;
    {
        public MonitoringData Data { get; }

        public DataUpdatedEventArgs(MonitoringData data)
        {
            Data = data;
        }
    }

    public class AlertEventArgs : EventArgs;
    {
        public Alert Alert { get; }

        public AlertEventArgs(Alert alert)
        {
            Alert = alert;
        }
    }

    // Enums;
    public enum MonitoringDisplayMode;
    {
        RealTime,
        Historical,
        Comparative,
        Predictive;
    }

    public enum AlertSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    public enum AlertStatus;
    {
        Active,
        Acknowledged,
        Resolved;
    }

    public enum ExportFormat;
    {
        JSON,
        CSV,
        XML;
    }

    // Event bus events;
    public class AlertRaisedEvent : IEvent;
    {
        public Alert Alert { get; }

        public AlertRaisedEvent(Alert alert)
        {
            Alert = alert;
        }
    }

    public class AlertResolvedEvent : IEvent;
    {
        public Alert Alert { get; }

        public AlertResolvedEvent(Alert alert)
        {
            Alert = alert;
        }
    }

    public class ApplicationEvent : IEvent;
    {
        public string ApplicationId { get; set; }
        public Dictionary<string, double> Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PerformanceEvent : IEvent;
    {
        public string MetricName { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ErrorEvent : IEvent;
    {
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // System event types;
    public enum SystemEventType;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    public class SystemEventArgs : EventArgs;
    {
        public SystemEventType EventType { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }
    }

    public class DiagnosticsEventArgs : EventArgs;
    {
        public object Diagnostics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PerformanceMetric;
    {
        public string Name { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
