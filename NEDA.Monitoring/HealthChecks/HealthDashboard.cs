// NEDA.Monitoring/HealthChecks/HealthDashboard.cs;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Common.Utilities;
using NEDA.Logging;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Monitoring.HealthChecks;
{
    /// <summary>
    /// Comprehensive health dashboard for monitoring system health status and components;
    /// Provides real-time health monitoring, alerts, and visualization data;
    /// </summary>
    public class HealthDashboard : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly HealthChecker _healthChecker;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly MetricsEngine _metricsEngine;

        private readonly ConcurrentDictionary<string, ComponentHealth> _componentHealth;
        private readonly ConcurrentDictionary<string, HealthMetric> _healthMetrics;
        private readonly ConcurrentQueue<HealthAlert> _recentAlerts;
        private readonly Timer _updateTimer;
        private readonly Timer _alertCheckTimer;
        private readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);

        private HealthDashboardSettings _settings;
        private bool _isDisposed;
        private DateTime _lastFullUpdate;
        private HealthStatus _overallStatus;

        /// <summary>
        /// Event triggered when component health status changes;
        /// </summary>
        public event EventHandler<ComponentHealthChangedEventArgs> OnComponentHealthChanged;

        /// <summary>
        /// Event triggered when new health alert is generated;
        /// </summary>
        public event EventHandler<HealthAlertEventArgs> OnHealthAlert;

        /// <summary>
        /// Event triggered when overall system health changes;
        /// </summary>
        public event EventHandler<SystemHealthChangedEventArgs> OnSystemHealthChanged;

        /// <summary>
        /// Current dashboard status;
        /// </summary>
        public DashboardStatus Status { get; private set; }

        /// <summary>
        /// Dashboard settings;
        /// </summary>
        public HealthDashboardSettings Settings;
        {
            get => _settings;
            set;
            {
                _settings = value ?? throw new ArgumentNullException(nameof(value));
                ConfigureTimers();
            }
        }

        /// <summary>
        /// Overall system health status;
        /// </summary>
        public HealthStatus OverallStatus;
        {
            get => _overallStatus;
            private set;
            {
                if (_overallStatus != value)
                {
                    var previous = _overallStatus;
                    _overallStatus = value;

                    OnSystemHealthChanged?.Invoke(this, new SystemHealthChangedEventArgs;
                    {
                        PreviousStatus = previous,
                        NewStatus = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Initialize health dashboard with dependencies;
        /// </summary>
        public HealthDashboard(
            ILogger logger,
            IEventBus eventBus,
            HealthChecker healthChecker,
            PerformanceMonitor performanceMonitor,
            MetricsEngine metricsEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _metricsEngine = metricsEngine ?? throw new ArgumentNullException(nameof(metricsEngine));

            _componentHealth = new ConcurrentDictionary<string, ComponentHealth>();
            _healthMetrics = new ConcurrentDictionary<string, HealthMetric>();
            _recentAlerts = new ConcurrentQueue<HealthAlert>();
            _settings = HealthDashboardSettings.Default;
            Status = DashboardStatus.Initializing;
            _overallStatus = HealthStatus.Unknown;

            InitializeDefaultComponents();
            ConfigureTimers();
            SubscribeToEvents();

            Status = DashboardStatus.Running;
            _logger.LogInformation("HealthDashboard initialized successfully", "HealthDashboard");
        }

        /// <summary>
        /// Start health monitoring and dashboard updates;
        /// </summary>
        public async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            try
            {
                Status = DashboardStatus.Starting;
                _logger.LogInformation("Starting health monitoring", "HealthDashboard");

                // Perform initial health check;
                await PerformFullHealthCheckAsync(cancellationToken);

                // Start periodic updates;
                _updateTimer?.Change(TimeSpan.Zero, _settings.UpdateInterval);
                _alertCheckTimer?.Change(TimeSpan.Zero, _settings.AlertCheckInterval);

                Status = DashboardStatus.Running;
                _logger.LogInformation("Health monitoring started successfully", "HealthDashboard");
            }
            catch (Exception ex)
            {
                Status = DashboardStatus.Error;
                _logger.LogError($"Failed to start health monitoring: {ex.Message}", "HealthDashboard", ex);
                throw new HealthDashboardException(HealthErrorCodes.StartFailed, "Failed to start health monitoring", ex);
            }
        }

        /// <summary>
        /// Stop health monitoring;
        /// </summary>
        public void StopMonitoring()
        {
            ValidateNotDisposed();

            _updateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _alertCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            Status = DashboardStatus.Stopped;
            _logger.LogInformation("Health monitoring stopped", "HealthDashboard");
        }

        /// <summary>
        /// Get current system health snapshot;
        /// </summary>
        public async Task<SystemHealthSnapshot> GetHealthSnapshotAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            await _updateLock.WaitAsync(cancellationToken);
            try
            {
                var snapshot = new SystemHealthSnapshot;
                {
                    Timestamp = DateTime.UtcNow,
                    OverallStatus = OverallStatus,
                    ComponentCount = _componentHealth.Count,
                    HealthyComponents = _componentHealth.Values.Count(c => c.Status == HealthStatus.Healthy),
                    WarningComponents = _componentHealth.Values.Count(c => c.Status == HealthStatus.Warning),
                    CriticalComponents = _componentHealth.Values.Count(c => c.Status == HealthStatus.Critical),
                    RecentAlertCount = _recentAlerts.Count;
                };

                // Add component summaries;
                foreach (var component in _componentHealth.Values)
                {
                    snapshot.ComponentSummaries.Add(new ComponentSummary;
                    {
                        ComponentId = component.ComponentId,
                        Name = component.Name,
                        Status = component.Status,
                        LastCheck = component.LastCheckTime,
                        ResponseTime = component.ResponseTime,
                        Details = component.Details;
                    });
                }

                // Add recent alerts;
                snapshot.RecentAlerts.AddRange(_recentAlerts.Take(_settings.MaxAlertsInSnapshot));

                // Calculate health score;
                snapshot.HealthScore = CalculateHealthScore();

                return snapshot;
            }
            finally
            {
                _updateLock.Release();
            }
        }

        /// <summary>
        /// Get detailed health information for specific component;
        /// </summary>
        public async Task<ComponentHealthDetails> GetComponentHealthAsync(string componentId,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(componentId))
                throw new ArgumentException("Component ID cannot be empty", nameof(componentId));

            if (_componentHealth.TryGetValue(componentId, out var component))
            {
                var details = new ComponentHealthDetails;
                {
                    ComponentId = component.ComponentId,
                    Name = component.Name,
                    Status = component.Status,
                    ComponentType = component.ComponentType,
                    LastCheckTime = component.LastCheckTime,
                    ResponseTime = component.ResponseTime,
                    ErrorCount = component.ErrorCount,
                    WarningCount = component.WarningCount,
                    Uptime = component.Uptime,
                    Metrics = new Dictionary<string, object>(component.Metrics),
                    Details = component.Details,
                    Dependencies = component.Dependencies.ToList()
                };

                // Get recent health history;
                details.HealthHistory = await GetComponentHealthHistoryAsync(componentId,
                    TimeSpan.FromHours(1), cancellationToken);

                return details;
            }

            throw new ComponentNotFoundException($"Component '{componentId}' not found");
        }

        /// <summary>
        /// Register a new component for health monitoring;
        /// </summary>
        public async Task RegisterComponentAsync(ComponentRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (registration == null)
                throw new ArgumentNullException(nameof(registration));

            await _updateLock.WaitAsync(cancellationToken);
            try
            {
                var component = new ComponentHealth;
                {
                    ComponentId = registration.ComponentId,
                    Name = registration.Name,
                    ComponentType = registration.ComponentType,
                    HealthCheckUrl = registration.HealthCheckUrl,
                    Critical = registration.Critical,
                    CheckInterval = registration.CheckInterval ?? _settings.DefaultCheckInterval,
                    Timeout = registration.Timeout ?? _settings.DefaultTimeout,
                    Dependencies = new HashSet<string>(registration.Dependencies ?? Enumerable.Empty<string>()),
                    CreatedTime = DateTime.UtcNow,
                    LastCheckTime = DateTime.MinValue,
                    Status = HealthStatus.Unknown;
                };

                if (_componentHealth.TryAdd(registration.ComponentId, component))
                {
                    _logger.LogInformation($"Registered component for health monitoring: {registration.Name}",
                        "HealthDashboard");

                    // Schedule initial check;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                        await CheckComponentHealthAsync(registration.ComponentId, cancellationToken);
                    }, cancellationToken);
                }
                else;
                {
                    throw new ComponentAlreadyRegisteredException($"Component '{registration.ComponentId}' already registered");
                }
            }
            finally
            {
                _updateLock.Release();
            }
        }

        /// <summary>
        /// Unregister a component from health monitoring;
        /// </summary>
        public void UnregisterComponent(string componentId)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(componentId))
                throw new ArgumentException("Component ID cannot be empty", nameof(componentId));

            if (_componentHealth.TryRemove(componentId, out var component))
            {
                _logger.LogInformation($"Unregistered component from health monitoring: {component.Name}",
                    "HealthDashboard");
            }
        }

        /// <summary>
        /// Manually trigger health check for specific component;
        /// </summary>
        public async Task<ComponentHealth> CheckComponentHealthAsync(string componentId,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(componentId))
                throw new ArgumentException("Component ID cannot be empty", nameof(componentId));

            if (!_componentHealth.TryGetValue(componentId, out var component))
                throw new ComponentNotFoundException($"Component '{componentId}' not found");

            try
            {
                var checkStart = DateTime.UtcNow;
                var previousStatus = component.Status;

                // Perform health check based on component type;
                var result = await PerformComponentHealthCheckAsync(component, cancellationToken);

                component.LastCheckTime = DateTime.UtcNow;
                component.ResponseTime = (component.LastCheckTime - checkStart).TotalMilliseconds;
                component.Status = result.Status;
                component.Details = result.Message;
                component.Metrics = result.Metrics ?? new Dictionary<string, object>();

                if (result.Status == HealthStatus.Error || result.Status == HealthStatus.Critical)
                    component.ErrorCount++;
                else if (result.Status == HealthStatus.Warning)
                    component.WarningCount++;

                // Update component in dictionary;
                _componentHealth[componentId] = component;

                // Trigger event if status changed;
                if (previousStatus != component.Status)
                {
                    OnComponentHealthChanged?.Invoke(this, new ComponentHealthChangedEventArgs;
                    {
                        ComponentId = component.ComponentId,
                        ComponentName = component.Name,
                        PreviousStatus = previousStatus,
                        NewStatus = component.Status,
                        Timestamp = DateTime.UtcNow,
                        Details = component.Details;
                    });
                }

                // Check if alert should be generated;
                CheckForComponentAlert(component, previousStatus);

                return component;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Health check failed for component '{componentId}': {ex.Message}",
                    "HealthDashboard", ex);

                // Update component with error status;
                component.LastCheckTime = DateTime.UtcNow;
                component.Status = HealthStatus.Error;
                component.Details = $"Health check failed: {ex.Message}";
                component.ErrorCount++;

                _componentHealth[componentId] = component;

                throw new HealthCheckException($"Health check failed for component '{componentId}'", ex);
            }
        }

        /// <summary>
        /// Get all registered components with their health status;
        /// </summary>
        public IReadOnlyCollection<ComponentHealth> GetAllComponents()
        {
            ValidateNotDisposed();
            return _componentHealth.Values.ToList();
        }

        /// <summary>
        /// Get recent health alerts;
        /// </summary>
        public IReadOnlyCollection<HealthAlert> GetRecentAlerts(int maxCount = 50)
        {
            ValidateNotDisposed();
            return _recentAlerts.Take(maxCount).ToList();
        }

        /// <summary>
        /// Get health metrics for dashboard visualization;
        /// </summary>
        public async Task<DashboardMetrics> GetDashboardMetricsAsync(TimeSpan timeRange,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var metrics = new DashboardMetrics;
            {
                TimeRange = timeRange,
                StartTime = DateTime.UtcNow - timeRange,
                EndTime = DateTime.UtcNow;
            };

            try
            {
                // Get system performance metrics;
                var performanceData = await _metricsEngine.GetMetricsAsync("system.performance",
                    timeRange, cancellationToken);

                // Get component health trends;
                var healthTrends = await CalculateHealthTrendsAsync(timeRange, cancellationToken);

                // Get alert statistics;
                var alertStats = CalculateAlertStatistics(timeRange);

                metrics.PerformanceData = performanceData;
                metrics.HealthTrends = healthTrends;
                metrics.AlertStatistics = alertStats;
                metrics.ComponentStatusDistribution = CalculateStatusDistribution();
                metrics.SystemLoad = await CalculateSystemLoadAsync(cancellationToken);

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get dashboard metrics: {ex.Message}", "HealthDashboard", ex);
                throw new HealthDashboardException(HealthErrorCodes.MetricsFailed,
                    "Failed to get dashboard metrics", ex);
            }
        }

        /// <summary>
        /// Acknowledge a health alert;
        /// </summary>
        public bool AcknowledgeAlert(Guid alertId, string acknowledgedBy, string notes = null)
        {
            ValidateNotDisposed();

            var alert = _recentAlerts.FirstOrDefault(a => a.AlertId == alertId);
            if (alert != null && !alert.Acknowledged)
            {
                alert.Acknowledged = true;
                alert.AcknowledgedBy = acknowledgedBy;
                alert.AcknowledgedAt = DateTime.UtcNow;
                alert.Notes = notes;

                _logger.LogInformation($"Alert acknowledged: {alertId} by {acknowledgedBy}", "HealthDashboard");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get health history for a component;
        /// </summary>
        public async Task<List<HealthHistoryPoint>> GetComponentHealthHistoryAsync(string componentId,
            TimeSpan timeRange, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            // In production, this would query a time-series database;
            // For now, return simulated data;
            var history = new List<HealthHistoryPoint>();
            var now = DateTime.UtcNow;
            var interval = TimeSpan.FromMinutes(5);

            for (var time = now - timeRange; time <= now; time += interval)
            {
                history.Add(new HealthHistoryPoint;
                {
                    Timestamp = time,
                    Status = HealthStatus.Healthy, // Simulated;
                    ResponseTime = 50 + new Random().NextDouble() * 100,
                    MetricValue = 0.95 + new Random().NextDouble() * 0.05;
                });
            }

            return await Task.FromResult(history);
        }

        /// <summary>
        /// Reset component health statistics;
        /// </summary>
        public void ResetComponentStatistics(string componentId)
        {
            ValidateNotDisposed();

            if (_componentHealth.TryGetValue(componentId, out var component))
            {
                component.ErrorCount = 0;
                component.WarningCount = 0;
                _componentHealth[componentId] = component;

                _logger.LogInformation($"Reset statistics for component: {component.Name}", "HealthDashboard");
            }
        }

        #region Private Methods;

        private void InitializeDefaultComponents()
        {
            // Register core system components;
            var coreComponents = new[]
            {
                new ComponentRegistration;
                {
                    ComponentId = "system.cpu",
                    Name = "CPU",
                    ComponentType = ComponentType.System,
                    Critical = true,
                    CheckInterval = TimeSpan.FromSeconds(30)
                },
                new ComponentRegistration;
                {
                    ComponentId = "system.memory",
                    Name = "Memory",
                    ComponentType = ComponentType.System,
                    Critical = true,
                    CheckInterval = TimeSpan.FromSeconds(30)
                },
                new ComponentRegistration;
                {
                    ComponentId = "system.disk",
                    Name = "Disk",
                    ComponentType = ComponentType.System,
                    Critical = true,
                    CheckInterval = TimeSpan.FromSeconds(60)
                },
                new ComponentRegistration;
                {
                    ComponentId = "system.network",
                    Name = "Network",
                    ComponentType = ComponentType.System,
                    Critical = true,
                    CheckInterval = TimeSpan.FromSeconds(60)
                },
                new ComponentRegistration;
                {
                    ComponentId = "database.primary",
                    Name = "Primary Database",
                    ComponentType = ComponentType.Database,
                    Critical = true,
                    CheckInterval = TimeSpan.FromSeconds(45)
                },
                new ComponentRegistration;
                {
                    ComponentId = "api.gateway",
                    Name = "API Gateway",
                    ComponentType = ComponentType.Service,
                    Critical = true,
                    CheckInterval = TimeSpan.FromSeconds(20)
                }
            };

            foreach (var component in coreComponents)
            {
                _componentHealth.TryAdd(component.ComponentId, new ComponentHealth;
                {
                    ComponentId = component.ComponentId,
                    Name = component.Name,
                    ComponentType = component.ComponentType,
                    Critical = component.Critical,
                    CheckInterval = component.CheckInterval ?? _settings.DefaultCheckInterval,
                    CreatedTime = DateTime.UtcNow,
                    Status = HealthStatus.Unknown;
                });
            }
        }

        private void SubscribeToEvents()
        {
            // Subscribe to system events for proactive monitoring;
            _eventBus.Subscribe<SystemEvent>(HandleSystemEvent);
            _eventBus.Subscribe<PerformanceEvent>(HandlePerformanceEvent);
        }

        private void ConfigureTimers()
        {
            _updateTimer?.Dispose();
            _alertCheckTimer?.Dispose();

            if (_settings.Enabled)
            {
                _updateTimer = new Timer(async _ => await UpdateDashboardAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);

                _alertCheckTimer = new Timer(async _ => await CheckForAlertsAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);
            }
        }

        private async Task UpdateDashboardAsync()
        {
            if (!_updateLock.Wait(0))
                return; // Update already in progress;

            try
            {
                var now = DateTime.UtcNow;

                // Check if full update is needed;
                if (now - _lastFullUpdate > _settings.FullUpdateInterval)
                {
                    await PerformFullHealthCheckAsync(CancellationToken.None);
                    _lastFullUpdate = now;
                }
                else;
                {
                    await PerformIncrementalHealthCheckAsync(CancellationToken.None);
                }

                // Update overall status;
                UpdateOverallStatus();

                // Update health metrics;
                await UpdateHealthMetricsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Dashboard update failed: {ex.Message}", "HealthDashboard", ex);
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private async Task PerformFullHealthCheckAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Performing full health check", "HealthDashboard");

            var checkTasks = new List<Task>();
            foreach (var componentId in _componentHealth.Keys)
            {
                checkTasks.Add(CheckComponentHealthAsync(componentId, cancellationToken));
            }

            await Task.WhenAll(checkTasks);
        }

        private async Task PerformIncrementalHealthCheckAsync(CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var checkTasks = new List<Task>();

            foreach (var component in _componentHealth.Values)
            {
                if (now - component.LastCheckTime >= component.CheckInterval)
                {
                    checkTasks.Add(CheckComponentHealthAsync(component.ComponentId, cancellationToken));
                }
            }

            if (checkTasks.Any())
            {
                await Task.WhenAll(checkTasks);
            }
        }

        private async Task<HealthCheckResult> PerformComponentHealthCheckAsync(ComponentHealth component,
            CancellationToken cancellationToken)
        {
            try
            {
                return component.ComponentType switch;
                {
                    ComponentType.System => await CheckSystemComponentAsync(component, cancellationToken),
                    ComponentType.Service => await CheckServiceComponentAsync(component, cancellationToken),
                    ComponentType.Database => await CheckDatabaseComponentAsync(component, cancellationToken),
                    ComponentType.External => await CheckExternalComponentAsync(component, cancellationToken),
                    ComponentType.Custom => await CheckCustomComponentAsync(component, cancellationToken),
                    _ => new HealthCheckResult;
                    {
                        Status = HealthStatus.Unknown,
                        Message = $"Unknown component type: {component.ComponentType}"
                    }
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult;
                {
                    Status = HealthStatus.Error,
                    Message = $"Health check failed: {ex.Message}",
                    Error = ex;
                };
            }
        }

        private async Task<HealthCheckResult> CheckSystemComponentAsync(ComponentHealth component,
            CancellationToken cancellationToken)
        {
            var result = new HealthCheckResult();
            var metrics = new Dictionary<string, object>();

            switch (component.ComponentId)
            {
                case "system.cpu":
                    var cpuUsage = await _performanceMonitor.GetSystemCpuUsageAsync(cancellationToken);
                    metrics["cpu_usage"] = cpuUsage;
                    result.Status = cpuUsage > 90 ? HealthStatus.Critical :
                                  cpuUsage > 75 ? HealthStatus.Warning : HealthStatus.Healthy;
                    result.Message = $"CPU usage: {cpuUsage:F1}%";
                    break;

                case "system.memory":
                    var memoryInfo = await _performanceMonitor.GetMemoryInfoAsync(cancellationToken);
                    var memoryUsage = (memoryInfo.Total - memoryInfo.Available) * 100.0 / memoryInfo.Total;
                    metrics["memory_usage"] = memoryUsage;
                    metrics["available_mb"] = memoryInfo.Available / 1024 / 1024;
                    result.Status = memoryUsage > 90 ? HealthStatus.Critical :
                                  memoryUsage > 80 ? HealthStatus.Warning : HealthStatus.Healthy;
                    result.Message = $"Memory usage: {memoryUsage:F1}%";
                    break;

                case "system.disk":
                    var diskInfo = await _performanceMonitor.GetDiskInfoAsync("C:", cancellationToken);
                    metrics["disk_usage"] = diskInfo.UsagePercentage;
                    metrics["free_gb"] = diskInfo.FreeSpace / 1024 / 1024 / 1024;
                    result.Status = diskInfo.UsagePercentage > 95 ? HealthStatus.Critical :
                                  diskInfo.UsagePercentage > 85 ? HealthStatus.Warning : HealthStatus.Healthy;
                    result.Message = $"Disk usage: {diskInfo.UsagePercentage:F1}%";
                    break;

                case "system.network":
                    var networkInfo = await _performanceMonitor.GetNetworkInfoAsync(cancellationToken);
                    metrics["latency_ms"] = networkInfo.Latency;
                    metrics["packet_loss"] = networkInfo.PacketLoss;
                    result.Status = networkInfo.Latency > 500 ? HealthStatus.Critical :
                                  networkInfo.Latency > 200 ? HealthStatus.Warning : HealthStatus.Healthy;
                    result.Message = $"Network latency: {networkInfo.Latency}ms";
                    break;
            }

            result.Metrics = metrics;
            return result;
        }

        private async Task<HealthCheckResult> CheckServiceComponentAsync(ComponentHealth component,
            CancellationToken cancellationToken)
        {
            // In production, this would make actual HTTP requests to health endpoints;
            await Task.Delay(100, cancellationToken); // Simulate check;

            return new HealthCheckResult;
            {
                Status = HealthStatus.Healthy,
                Message = "Service is responding normally",
                Metrics = new Dictionary<string, object>
                {
                    ["response_time"] = 45.2,
                    ["throughput"] = 1250;
                }
            };
        }

        private async Task<HealthCheckResult> CheckDatabaseComponentAsync(ComponentHealth component,
            CancellationToken cancellationToken)
        {
            // In production, this would test database connectivity and performance;
            await Task.Delay(150, cancellationToken); // Simulate check;

            return new HealthCheckResult;
            {
                Status = HealthStatus.Healthy,
                Message = "Database is accessible and responding",
                Metrics = new Dictionary<string, object>
                {
                    ["connection_count"] = 24,
                    ["query_time"] = 12.5;
                }
            };
        }

        private Task<HealthCheckResult> CheckExternalComponentAsync(ComponentHealth component,
            CancellationToken cancellationToken)
        {
            // External service checks would go here;
            return Task.FromResult(new HealthCheckResult;
            {
                Status = HealthStatus.Healthy,
                Message = "External component is available"
            });
        }

        private Task<HealthCheckResult> CheckCustomComponentAsync(ComponentHealth component,
            CancellationToken cancellationToken)
        {
            // Custom health check logic would go here;
            return Task.FromResult(new HealthCheckResult;
            {
                Status = HealthStatus.Healthy,
                Message = "Custom component check passed"
            });
        }

        private void UpdateOverallStatus()
        {
            if (!_componentHealth.Any())
            {
                OverallStatus = HealthStatus.Unknown;
                return;
            }

            var criticalComponents = _componentHealth.Values;
                .Where(c => c.Critical)
                .ToList();

            if (!criticalComponents.Any())
            {
                // If no critical components, use worst status among all components;
                var worstStatus = _componentHealth.Values;
                    .Select(c => c.Status)
                    .OrderByDescending(s => (int)s)
                    .FirstOrDefault();

                OverallStatus = worstStatus;
                return;
            }

            // Check if any critical component is not healthy;
            if (criticalComponents.Any(c => c.Status == HealthStatus.Critical || c.Status == HealthStatus.Error))
            {
                OverallStatus = HealthStatus.Critical;
            }
            else if (criticalComponents.Any(c => c.Status == HealthStatus.Warning))
            {
                OverallStatus = HealthStatus.Warning;
            }
            else if (criticalComponents.All(c => c.Status == HealthStatus.Healthy))
            {
                OverallStatus = HealthStatus.Healthy;
            }
            else;
            {
                OverallStatus = HealthStatus.Unknown;
            }
        }

        private async Task UpdateHealthMetricsAsync(CancellationToken cancellationToken)
        {
            var timestamp = DateTime.UtcNow;

            // Update component count metrics;
            _healthMetrics["components.total"] = new HealthMetric;
            {
                Name = "Total Components",
                Value = _componentHealth.Count,
                Timestamp = timestamp,
                Unit = "count"
            };

            _healthMetrics["components.healthy"] = new HealthMetric;
            {
                Name = "Healthy Components",
                Value = _componentHealth.Values.Count(c => c.Status == HealthStatus.Healthy),
                Timestamp = timestamp,
                Unit = "count"
            };

            _healthMetrics["components.critical"] = new HealthMetric;
            {
                Name = "Critical Components",
                Value = _componentHealth.Values.Count(c => c.Status == HealthStatus.Critical),
                Timestamp = timestamp,
                Unit = "count"
            };

            // Update system metrics;
            try
            {
                var systemMetrics = await _healthChecker.GetSystemHealthAsync(cancellationToken);

                _healthMetrics["system.health_score"] = new HealthMetric;
                {
                    Name = "System Health Score",
                    Value = systemMetrics.HealthScore,
                    Timestamp = timestamp,
                    Unit = "percent"
                };

                _healthMetrics["system.uptime"] = new HealthMetric;
                {
                    Name = "System Uptime",
                    Value = systemMetrics.Uptime.TotalHours,
                    Timestamp = timestamp,
                    Unit = "hours"
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to update system metrics: {ex.Message}", "HealthDashboard");
            }
        }

        private async Task CheckForAlertsAsync()
        {
            try
            {
                var now = DateTime.UtcNow;

                // Check for components needing attention;
                foreach (var component in _componentHealth.Values)
                {
                    // Check if component hasn't been checked recently;
                    if (now - component.LastCheckTime > component.CheckInterval * 2)
                    {
                        GenerateAlert(new HealthAlert;
                        {
                            AlertId = Guid.NewGuid(),
                            ComponentId = component.ComponentId,
                            ComponentName = component.Name,
                            AlertType = AlertType.StaleData,
                            Severity = AlertSeverity.Warning,
                            Message = $"Component '{component.Name}' has stale health data",
                            Timestamp = now,
                            Details = $"Last check: {component.LastCheckTime}, Interval: {component.CheckInterval}"
                        });
                    }

                    // Check for repeated errors;
                    if (component.ErrorCount >= _settings.ErrorThreshold)
                    {
                        GenerateAlert(new HealthAlert;
                        {
                            AlertId = Guid.NewGuid(),
                            ComponentId = component.ComponentId,
                            ComponentName = component.Name,
                            AlertType = AlertType.RepeatedErrors,
                            Severity = AlertSeverity.Critical,
                            Message = $"Component '{component.Name}' has repeated errors",
                            Timestamp = now,
                            Details = $"Error count: {component.ErrorCount}"
                        });
                    }
                }

                // Check overall system status;
                if (OverallStatus == HealthStatus.Critical)
                {
                    GenerateAlert(new HealthAlert;
                    {
                        AlertId = Guid.NewGuid(),
                        ComponentId = "system.overall",
                        ComponentName = "System Overall",
                        AlertType = AlertType.SystemCritical,
                        Severity = AlertSeverity.Critical,
                        Message = "System health is critical",
                        Timestamp = now,
                        Details = "One or more critical components are not healthy"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Alert check failed: {ex.Message}", "HealthDashboard", ex);
            }
        }

        private void CheckForComponentAlert(ComponentHealth component, HealthStatus previousStatus)
        {
            if (component.Status == HealthStatus.Critical && previousStatus != HealthStatus.Critical)
            {
                GenerateAlert(new HealthAlert;
                {
                    AlertId = Guid.NewGuid(),
                    ComponentId = component.ComponentId,
                    ComponentName = component.Name,
                    AlertType = AlertType.ComponentCritical,
                    Severity = AlertSeverity.Critical,
                    Message = $"Component '{component.Name}' is critical",
                    Timestamp = DateTime.UtcNow,
                    Details = component.Details;
                });
            }
            else if (component.Status == HealthStatus.Warning && previousStatus == HealthStatus.Healthy)
            {
                GenerateAlert(new HealthAlert;
                {
                    AlertId = Guid.NewGuid(),
                    ComponentId = component.ComponentId,
                    ComponentName = component.Name,
                    AlertType = AlertType.ComponentWarning,
                    Severity = AlertSeverity.Warning,
                    Message = $"Component '{component.Name}' is warning",
                    Timestamp = DateTime.UtcNow,
                    Details = component.Details;
                });
            }
        }

        private void GenerateAlert(HealthAlert alert)
        {
            // Add to recent alerts queue;
            _recentAlerts.Enqueue(alert);

            // Maintain queue size;
            while (_recentAlerts.Count > _settings.MaxAlertsInMemory)
            {
                _recentAlerts.TryDequeue(out _);
            }

            // Trigger event;
            OnHealthAlert?.Invoke(this, new HealthAlertEventArgs;
            {
                Alert = alert;
            });

            // Log alert;
            var logLevel = alert.Severity == AlertSeverity.Critical ? LogLevel.Error : LogLevel.Warning;
            _logger.Log(logLevel, $"Health alert: {alert.Message}", "HealthDashboard");
        }

        private double CalculateHealthScore()
        {
            if (!_componentHealth.Any())
                return 100.0;

            var totalWeight = 0.0;
            var weightedScore = 0.0;

            foreach (var component in _componentHealth.Values)
            {
                var weight = component.Critical ? 2.0 : 1.0;
                var componentScore = component.Status switch;
                {
                    HealthStatus.Healthy => 100.0,
                    HealthStatus.Warning => 60.0,
                    HealthStatus.Unknown => 50.0,
                    HealthStatus.Error => 30.0,
                    HealthStatus.Critical => 0.0,
                    _ => 50.0;
                };

                weightedScore += componentScore * weight;
                totalWeight += weight;
            }

            return totalWeight > 0 ? weightedScore / totalWeight : 100.0;
        }

        private async Task<HealthTrends> CalculateHealthTrendsAsync(TimeSpan timeRange,
            CancellationToken cancellationToken)
        {
            // In production, this would analyze historical data;
            await Task.CompletedTask;

            return new HealthTrends;
            {
                ImprovingComponents = _componentHealth.Values.Count(c => c.Status == HealthStatus.Healthy),
                DecliningComponents = _componentHealth.Values.Count(c =>
                    c.Status == HealthStatus.Warning || c.Status == HealthStatus.Critical),
                StabilityScore = 85.5 // Simulated;
            };
        }

        private AlertStatistics CalculateAlertStatistics(TimeSpan timeRange)
        {
            var recentAlerts = _recentAlerts;
                .Where(a => DateTime.UtcNow - a.Timestamp <= timeRange)
                .ToList();

            return new AlertStatistics;
            {
                TotalAlerts = recentAlerts.Count,
                CriticalAlerts = recentAlerts.Count(a => a.Severity == AlertSeverity.Critical),
                WarningAlerts = recentAlerts.Count(a => a.Severity == AlertSeverity.Warning),
                AcknowledgedAlerts = recentAlerts.Count(a => a.Acknowledged),
                AverageResponseTime = recentAlerts.Any() ?
                    recentAlerts.Average(a => (DateTime.UtcNow - a.Timestamp).TotalMinutes) : 0;
            };
        }

        private ComponentStatusDistribution CalculateStatusDistribution()
        {
            var components = _componentHealth.Values.ToList();

            return new ComponentStatusDistribution;
            {
                Healthy = components.Count(c => c.Status == HealthStatus.Healthy),
                Warning = components.Count(c => c.Status == HealthStatus.Warning),
                Critical = components.Count(c => c.Status == HealthStatus.Critical),
                Error = components.Count(c => c.Status == HealthStatus.Error),
                Unknown = components.Count(c => c.Status == HealthStatus.Unknown),
                Total = components.Count;
            };
        }

        private async Task<SystemLoad> CalculateSystemLoadAsync(CancellationToken cancellationToken)
        {
            try
            {
                var cpuUsage = await _performanceMonitor.GetSystemCpuUsageAsync(cancellationToken);
                var memoryInfo = await _performanceMonitor.GetMemoryInfoAsync(cancellationToken);
                var memoryUsage = (memoryInfo.Total - memoryInfo.Available) * 100.0 / memoryInfo.Total;

                return new SystemLoad;
                {
                    CpuLoad = cpuUsage,
                    MemoryLoad = memoryUsage,
                    DiskLoad = 65.0, // Simulated;
                    NetworkLoad = 45.0, // Simulated;
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to calculate system load: {ex.Message}", "HealthDashboard");
                return new SystemLoad { Timestamp = DateTime.UtcNow };
            }
        }

        private void HandleSystemEvent(SystemEvent systemEvent)
        {
            // React to system events;
            if (systemEvent.EventType == SystemEventType.ResourceExhaustion)
            {
                GenerateAlert(new HealthAlert;
                {
                    AlertId = Guid.NewGuid(),
                    ComponentId = "system.event",
                    ComponentName = "System Event",
                    AlertType = AlertType.SystemEvent,
                    Severity = AlertSeverity.Warning,
                    Message = $"System event: {systemEvent.EventType}",
                    Timestamp = DateTime.UtcNow,
                    Details = systemEvent.Details;
                });
            }
        }

        private void HandlePerformanceEvent(PerformanceEvent performanceEvent)
        {
            // React to performance events;
            if (performanceEvent.MetricType == "response_time" && performanceEvent.Value > 1000)
            {
                GenerateAlert(new HealthAlert;
                {
                    AlertId = Guid.NewGuid(),
                    ComponentId = "performance.event",
                    ComponentName = "Performance Event",
                    AlertType = AlertType.Performance,
                    Severity = AlertSeverity.Warning,
                    Message = "High response time detected",
                    Timestamp = DateTime.UtcNow,
                    Details = $"Response time: {performanceEvent.Value}ms"
                });
            }
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(HealthDashboard));
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _updateTimer?.Dispose();
                    _alertCheckTimer?.Dispose();
                    _updateLock?.Dispose();

                    StopMonitoring();
                    Status = DashboardStatus.Disposed;

                    _logger.LogInformation("HealthDashboard disposed", "HealthDashboard");
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

    #region Supporting Types;

    /// <summary>
    /// Dashboard operational status;
    /// </summary>
    public enum DashboardStatus;
    {
        Initializing,
        Starting,
        Running,
        Stopped,
        Error,
        Disposed;
    }

    /// <summary>
    /// Component health status;
    /// </summary>
    public enum HealthStatus;
    {
        Unknown,
        Healthy,
        Warning,
        Critical,
        Error;
    }

    /// <summary>
    /// Component type classification;
    /// </summary>
    public enum ComponentType;
    {
        System,
        Service,
        Database,
        External,
        Custom;
    }

    /// <summary>
    /// Alert severity levels;
    /// </summary>
    public enum AlertSeverity;
    {
        Info,
        Warning,
        Critical;
    }

    /// <summary>
    /// Alert type classification;
    /// </summary>
    public enum AlertType;
    {
        ComponentCritical,
        ComponentWarning,
        SystemCritical,
        Performance,
        StaleData,
        RepeatedErrors,
        SystemEvent;
    }

    /// <summary>
    /// Health dashboard settings;
    /// </summary>
    public class HealthDashboardSettings;
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan AlertCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan FullUpdateInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan DefaultCheckInterval { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public int MaxAlertsInMemory { get; set; } = 1000;
        public int MaxAlertsInSnapshot { get; set; } = 50;
        public int ErrorThreshold { get; set; } = 5;
        public bool EnableDetailedLogging { get; set; } = true;

        public static HealthDashboardSettings Default => new HealthDashboardSettings();
    }

    /// <summary>
    /// Component registration information;
    /// </summary>
    public class ComponentRegistration;
    {
        public string ComponentId { get; set; }
        public string Name { get; set; }
        public ComponentType ComponentType { get; set; }
        public string HealthCheckUrl { get; set; }
        public bool Critical { get; set; } = false;
        public TimeSpan? CheckInterval { get; set; }
        public TimeSpan? Timeout { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Component health information;
    /// </summary>
    public class ComponentHealth;
    {
        public string ComponentId { get; set; }
        public string Name { get; set; }
        public ComponentType ComponentType { get; set; }
        public HealthStatus Status { get; set; }
        public bool Critical { get; set; }
        public string HealthCheckUrl { get; set; }
        public TimeSpan CheckInterval { get; set; }
        public TimeSpan Timeout { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastCheckTime { get; set; }
        public double ResponseTime { get; set; } // milliseconds;
        public string Details { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public TimeSpan Uptime => DateTime.UtcNow - CreatedTime;
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
        public HashSet<string> Dependencies { get; set; } = new HashSet<string>();
    }

    /// <summary>
    /// Health check result;
    /// </summary>
    public class HealthCheckResult;
    {
        public HealthStatus Status { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Metrics { get; set; }
        public Exception Error { get; set; }
    }

    /// <summary>
    /// Health metric for tracking;
    /// </summary>
    public class HealthMetric;
    {
        public string Name { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public string Unit { get; set; }
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Health alert;
    /// </summary>
    public class HealthAlert;
    {
        public Guid AlertId { get; set; }
        public string ComponentId { get; set; }
        public string ComponentName { get; set; }
        public AlertType AlertType { get; set; }
        public AlertSeverity Severity { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
        public bool Acknowledged { get; set; }
        public string AcknowledgedBy { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>
    /// System health snapshot;
    /// </summary>
    public class SystemHealthSnapshot;
    {
        public DateTime Timestamp { get; set; }
        public HealthStatus OverallStatus { get; set; }
        public double HealthScore { get; set; }
        public int ComponentCount { get; set; }
        public int HealthyComponents { get; set; }
        public int WarningComponents { get; set; }
        public int CriticalComponents { get; set; }
        public int RecentAlertCount { get; set; }
        public List<ComponentSummary> ComponentSummaries { get; set; } = new List<ComponentSummary>();
        public List<HealthAlert> RecentAlerts { get; set; } = new List<HealthAlert>();
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Component summary;
    /// </summary>
    public class ComponentSummary;
    {
        public string ComponentId { get; set; }
        public string Name { get; set; }
        public HealthStatus Status { get; set; }
        public DateTime LastCheck { get; set; }
        public double ResponseTime { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Detailed component health information;
    /// </summary>
    public class ComponentHealthDetails;
    {
        public string ComponentId { get; set; }
        public string Name { get; set; }
        public HealthStatus Status { get; set; }
        public ComponentType ComponentType { get; set; }
        public DateTime LastCheckTime { get; set; }
        public double ResponseTime { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public TimeSpan Uptime { get; set; }
        public Dictionary<string, object> Metrics { get; set; }
        public string Details { get; set; }
        public List<string> Dependencies { get; set; }
        public List<HealthHistoryPoint> HealthHistory { get; set; } = new List<HealthHistoryPoint>();
    }

    /// <summary>
    /// Health history data point;
    /// </summary>
    public class HealthHistoryPoint;
    {
        public DateTime Timestamp { get; set; }
        public HealthStatus Status { get; set; }
        public double ResponseTime { get; set; }
        public double MetricValue { get; set; }
    }

    /// <summary>
    /// Dashboard metrics for visualization;
    /// </summary>
    public class DashboardMetrics;
    {
        public TimeSpan TimeRange { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public object PerformanceData { get; set; }
        public HealthTrends HealthTrends { get; set; }
        public AlertStatistics AlertStatistics { get; set; }
        public ComponentStatusDistribution ComponentStatusDistribution { get; set; }
        public SystemLoad SystemLoad { get; set; }
    }

    /// <summary>
    /// Health trends analysis;
    /// </summary>
    public class HealthTrends;
    {
        public int ImprovingComponents { get; set; }
        public int DecliningComponents { get; set; }
        public double StabilityScore { get; set; }
        public Dictionary<string, double> TrendMetrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Alert statistics;
    /// </summary>
    public class AlertStatistics;
    {
        public int TotalAlerts { get; set; }
        public int CriticalAlerts { get; set; }
        public int WarningAlerts { get; set; }
        public int AcknowledgedAlerts { get; set; }
        public double AverageResponseTime { get; set; } // minutes;
    }

    /// <summary>
    /// Component status distribution;
    /// </summary>
    public class ComponentStatusDistribution;
    {
        public int Healthy { get; set; }
        public int Warning { get; set; }
        public int Critical { get; set; }
        public int Error { get; set; }
        public int Unknown { get; set; }
        public int Total { get; set; }
    }

    /// <summary>
    /// System load information;
    /// </summary>
    public class SystemLoad;
    {
        public double CpuLoad { get; set; } // percentage;
        public double MemoryLoad { get; set; } // percentage;
        public double DiskLoad { get; set; } // percentage;
        public double NetworkLoad { get; set; } // percentage;
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for component health change;
    /// </summary>
    public class ComponentHealthChangedEventArgs : EventArgs;
    {
        public string ComponentId { get; set; }
        public string ComponentName { get; set; }
        public HealthStatus PreviousStatus { get; set; }
        public HealthStatus NewStatus { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Event arguments for health alert;
    /// </summary>
    public class HealthAlertEventArgs : EventArgs;
    {
        public HealthAlert Alert { get; set; }
    }

    /// <summary>
    /// Event arguments for system health change;
    /// </summary>
    public class SystemHealthChangedEventArgs : EventArgs;
    {
        public HealthStatus PreviousStatus { get; set; }
        public HealthStatus NewStatus { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// System event for monitoring;
    /// </summary>
    public class SystemEvent;
    {
        public SystemEventType EventType { get; set; }
        public string Source { get; set; }
        public DateTime Timestamp { get; set; }
        public string Details { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// System event types;
    /// </summary>
    public enum SystemEventType;
    {
        ResourceExhaustion,
        ConfigurationChange,
        ServiceRestart,
        SecurityEvent,
        PerformanceDegradation;
    }

    /// <summary>
    /// Performance event;
    /// </summary>
    public class PerformanceEvent;
    {
        public string MetricType { get; set; }
        public string ComponentId { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Tags { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Health dashboard specific exception;
    /// </summary>
    public class HealthDashboardException : Exception
    {
        public string ErrorCode { get; }

        public HealthDashboardException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public HealthDashboardException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Health check exception;
    /// </summary>
    public class HealthCheckException : Exception
    {
        public HealthCheckException(string message) : base(message) { }
        public HealthCheckException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Component not found exception;
    /// </summary>
    public class ComponentNotFoundException : Exception
    {
        public ComponentNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Component already registered exception;
    /// </summary>
    public class ComponentAlreadyRegisteredException : Exception
    {
        public ComponentAlreadyRegisteredException(string message) : base(message) { }
    }

    /// <summary>
    /// Health error codes;
    /// </summary>
    public static class HealthErrorCodes;
    {
        public const string StartFailed = "HEALTH_001";
        public const string MetricsFailed = "HEALTH_002";
        public const string ComponentCheckFailed = "HEALTH_003";
        public const string RegistrationFailed = "HEALTH_004";
        public const string UpdateFailed = "HEALTH_005";
    }

    #endregion;
}
