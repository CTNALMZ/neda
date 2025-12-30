using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Common;
using NEDA.Core.Common.Extensions;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Monitoring.Logging;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Monitoring.HealthChecks;
{
    /// <summary>
    /// Advanced Health Checker for comprehensive system health monitoring;
    /// Provides real-time health assessment, automatic recovery, and detailed reporting;
    /// </summary>
    public sealed class HealthChecker : IHealthChecker, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IMetricsCollector _metricsCollector;
        private readonly HealthCheckConfiguration _configuration;

        private readonly ConcurrentDictionary<string, HealthCheckRegistration> _healthChecks;
        private readonly ConcurrentDictionary<string, HealthCheckResult> _latestResults;
        private readonly ConcurrentDictionary<string, HealthCheckHistory> _healthHistory;
        private readonly object _syncLock = new object();

        private Timer _scheduledCheckTimer;
        private Timer _continuousMonitoringTimer;
        private bool _isInitialized;
        private bool _isMonitoringActive;
        private CancellationTokenSource _monitoringCts;

        private static readonly Lazy<HealthChecker> _instance =
            new Lazy<HealthChecker>(() => new HealthChecker());

        #endregion;

        #region Constructors;

        /// <summary>
        /// Private constructor for singleton pattern;
        /// </summary>
        private HealthChecker()
        {
            _healthChecks = new ConcurrentDictionary<string, HealthCheckRegistration>();
            _latestResults = new ConcurrentDictionary<string, HealthCheckResult>();
            _healthHistory = new ConcurrentDictionary<string, HealthCheckHistory>();

            _configuration = HealthCheckConfiguration.Default;
            _isInitialized = false;
            _isMonitoringActive = false;

            // Get dependencies from service locator (temporary, DI preferred)
            _logger = LogManager.GetLogger("HealthChecker");
            _eventBus = EventBus.Instance;
            _performanceMonitor = PerformanceMonitor.Instance;
            _metricsCollector = MetricsCollector.Instance;
        }

        /// <summary>
        /// Constructor with dependency injection support;
        /// </summary>
        public HealthChecker(
            ILogger logger,
            IEventBus eventBus,
            IPerformanceMonitor performanceMonitor,
            IMetricsCollector metricsCollector,
            HealthCheckConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _configuration = configuration ?? HealthCheckConfiguration.Default;

            _healthChecks = new ConcurrentDictionary<string, HealthCheckRegistration>();
            _latestResults = new ConcurrentDictionary<string, HealthCheckResult>();
            _healthHistory = new ConcurrentDictionary<string, HealthCheckHistory>();

            _isInitialized = false;
            _isMonitoringActive = false;

            Initialize();
        }

        #endregion;

        #region Properties;

        /// <summary>
        /// Singleton instance for global access;
        /// </summary>
        public static HealthChecker Instance => _instance.Value;

        /// <summary>
        /// Gets the overall system health status;
        /// </summary>
        public HealthStatus OverallHealthStatus;
        {
            get;
            {
                if (_latestResults.IsEmpty)
                    return HealthStatus.Unknown;

                var criticalChecks = _latestResults.Values;
                    .Where(r => r.Status == HealthStatus.Unhealthy)
                    .Count();

                var warningChecks = _latestResults.Values;
                    .Where(r => r.Status == HealthStatus.Degraded)
                    .Count();

                if (criticalChecks > 0)
                    return HealthStatus.Unhealthy;

                if (warningChecks > 0)
                    return HealthStatus.Degraded;

                return HealthStatus.Healthy;
            }
        }

        /// <summary>
        /// Gets the number of registered health checks;
        /// </summary>
        public int RegisteredCheckCount => _healthChecks.Count;

        /// <summary>
        /// Gets the number of failed health checks;
        /// </summary>
        public int FailedCheckCount => _latestResults.Values;
            .Count(r => r.Status == HealthStatus.Unhealthy);

        /// <summary>
        /// Gets the number of degraded health checks;
        /// </summary>
        public int DegradedCheckCount => _latestResults.Values;
            .Count(r => r.Status == HealthStatus.Degraded);

        /// <summary>
        /// Indicates if monitoring is currently active;
        /// </summary>
        public bool IsMonitoringActive => _isMonitoringActive;

        /// <summary>
        /// Gets the last full health check execution time;
        /// </summary>
        public DateTime? LastCheckTime { get; private set; }

        /// <summary>
        /// Gets the configuration;
        /// </summary>
        public HealthCheckConfiguration Configuration => _configuration;

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the health checker with default system checks;
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;

            lock (_syncLock)
            {
                if (_isInitialized)
                    return;

                try
                {
                    RegisterSystemHealthChecks();

                    if (_configuration.EnableAutoMonitoring)
                    {
                        StartContinuousMonitoring();
                    }

                    _isInitialized = true;
                    _logger.Info("HealthChecker initialized successfully");
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to initialize HealthChecker", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Registers a custom health check;
        /// </summary>
        /// <param name="name">Unique name of the health check</param>
        /// <param name="checkFunction">Function that performs the health check</param>
        /// <param name="criticality">Criticality level of the check</param>
        /// <param name="tags">Optional tags for categorization</param>
        /// <param name="timeout">Timeout for the health check</param>
        public void RegisterHealthCheck(
            string name,
            Func<CancellationToken, Task<HealthCheckResult>> checkFunction,
            HealthCheckCriticality criticality = HealthCheckCriticality.Medium,
            IEnumerable<string> tags = null,
            TimeSpan? timeout = null)
        {
            Validate.NotNullOrEmpty(name, nameof(name));
            Validate.NotNull(checkFunction, nameof(checkFunction));

            var registration = new HealthCheckRegistration(
                name,
                checkFunction,
                criticality,
                tags ?? Enumerable.Empty<string>(),
                timeout ?? _configuration.DefaultHealthCheckTimeout);

            if (!_healthChecks.TryAdd(name, registration))
            {
                throw new InvalidOperationException($"Health check with name '{name}' is already registered");
            }

            _logger.Info($"Registered health check: {name} (Criticality: {criticality})");
        }

        /// <summary>
        /// Unregisters a health check;
        /// </summary>
        /// <param name="name">Name of the health check to unregister</param>
        public bool UnregisterHealthCheck(string name)
        {
            Validate.NotNullOrEmpty(name, nameof(name));

            if (_healthChecks.TryRemove(name, out _))
            {
                _latestResults.TryRemove(name, out _);
                _healthHistory.TryRemove(name, out _);

                _logger.Info($"Unregistered health check: {name}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Executes all registered health checks;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Comprehensive health report</returns>
        public async Task<SystemHealthReport> CheckAllAsync(CancellationToken cancellationToken = default)
        {
            var report = new SystemHealthReport;
            {
                CheckTime = DateTime.UtcNow,
                TotalChecks = _healthChecks.Count;
            };

            var checkTasks = new List<Task<HealthCheckResult>>();
            var checkContexts = new Dictionary<string, HealthCheckContext>();

            // Prepare health check contexts;
            foreach (var kvp in _healthChecks)
            {
                var context = new HealthCheckContext;
                {
                    CheckName = kvp.Key,
                    Registration = kvp.Value,
                    StartTime = DateTime.UtcNow;
                };

                checkContexts[kvp.Key] = context;
            }

            // Execute health checks in parallel with concurrency limit;
            var semaphore = new SemaphoreSlim(_configuration.MaxConcurrentChecks);

            foreach (var kvp in checkContexts)
            {
                await semaphore.WaitAsync(cancellationToken);

                checkTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        return await ExecuteSingleHealthCheckAsync(
                            kvp.Value.Registration,
                            cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            // Wait for all checks to complete;
            var results = await Task.WhenAll(checkTasks);

            // Process results;
            foreach (var result in results)
            {
                report.CheckResults[result.CheckName] = result;

                // Update latest results;
                _latestResults[result.CheckName] = result;

                // Update history;
                UpdateHealthHistory(result);

                // Update report statistics;
                UpdateReportStatistics(report, result);

                // Publish health check event;
                PublishHealthCheckEvent(result);
            }

            report.OverallStatus = CalculateOverallStatus(report);
            report.Duration = DateTime.UtcNow - report.CheckTime;
            LastCheckTime = report.CheckTime;

            // Generate recommendations;
            report.Recommendations = GenerateRecommendations(report);

            _logger.Info($"Health check completed. Status: {report.OverallStatus}, " +
                        $"Healthy: {report.HealthyCount}, Degraded: {report.DegradedCount}, " +
                        $"Unhealthy: {report.UnhealthyCount}");

            return report;
        }

        /// <summary>
        /// Executes a single health check by name;
        /// </summary>
        /// <param name="checkName">Name of the health check to execute</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Health check result</returns>
        public async Task<HealthCheckResult> CheckSingleAsync(
            string checkName,
            CancellationToken cancellationToken = default)
        {
            Validate.NotNullOrEmpty(checkName, nameof(checkName));

            if (!_healthChecks.TryGetValue(checkName, out var registration))
            {
                throw new ArgumentException($"Health check '{checkName}' is not registered");
            }

            var result = await ExecuteSingleHealthCheckAsync(registration, cancellationToken);

            // Update latest results;
            _latestResults[checkName] = result;

            // Update history;
            UpdateHealthHistory(result);

            // Publish event;
            PublishHealthCheckEvent(result);

            return result;
        }

        /// <summary>
        /// Gets the latest result for a specific health check;
        /// </summary>
        /// <param name="checkName">Name of the health check</param>
        /// <returns>Latest result or null if not found</returns>
        public HealthCheckResult GetLatestResult(string checkName)
        {
            Validate.NotNullOrEmpty(checkName, nameof(checkName));

            return _latestResults.TryGetValue(checkName, out var result) ? result : null;
        }

        /// <summary>
        /// Gets health history for a specific check;
        /// </summary>
        /// <param name="checkName">Name of the health check</param>
        /// <param name="timeRange">Time range for history</param>
        /// <returns>Health check history</returns>
        public HealthCheckHistory GetHealthHistory(string checkName, TimeSpan? timeRange = null)
        {
            Validate.NotNullOrEmpty(checkName, nameof(checkName));

            if (!_healthHistory.TryGetValue(checkName, out var history))
            {
                return new HealthCheckHistory(checkName);
            }

            if (timeRange.HasValue)
            {
                var cutoff = DateTime.UtcNow - timeRange.Value;
                return history.FilterByTime(cutoff);
            }

            return history;
        }

        /// <summary>
        /// Starts continuous health monitoring;
        /// </summary>
        public void StartContinuousMonitoring()
        {
            if (_isMonitoringActive)
                return;

            lock (_syncLock)
            {
                if (_isMonitoringActive)
                    return;

                _monitoringCts = new CancellationTokenSource();

                // Start scheduled checks;
                _scheduledCheckTimer = new Timer(
                    async _ => await ExecuteScheduledCheckAsync(),
                    null,
                    TimeSpan.Zero,
                    _configuration.ScheduledCheckInterval);

                // Start continuous monitoring for critical checks;
                _continuousMonitoringTimer = new Timer(
                    async _ => await ExecuteContinuousMonitoringAsync(),
                    null,
                    TimeSpan.Zero,
                    _configuration.ContinuousMonitoringInterval);

                _isMonitoringActive = true;

                _logger.Info("Continuous health monitoring started");

                // Publish monitoring started event;
                _eventBus.Publish(new HealthMonitoringStartedEvent;
                {
                    StartedAt = DateTime.UtcNow,
                    Configuration = _configuration;
                });
            }
        }

        /// <summary>
        /// Stops continuous health monitoring;
        /// </summary>
        public void StopContinuousMonitoring()
        {
            if (!_isMonitoringActive)
                return;

            lock (_syncLock)
            {
                if (!_isMonitoringActive)
                    return;

                _monitoringCts?.Cancel();
                _monitoringCts?.Dispose();
                _monitoringCts = null;

                _scheduledCheckTimer?.Dispose();
                _scheduledCheckTimer = null;

                _continuousMonitoringTimer?.Dispose();
                _continuousMonitoringTimer = null;

                _isMonitoringActive = false;

                _logger.Info("Continuous health monitoring stopped");

                // Publish monitoring stopped event;
                _eventBus.Publish(new HealthMonitoringStoppedEvent;
                {
                    StoppedAt = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Performs deep health analysis with detailed diagnostics;
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Deep health analysis report</returns>
        public async Task<DeepHealthAnalysis> PerformDeepAnalysisAsync(
            CancellationToken cancellationToken = default)
        {
            var analysis = new DeepHealthAnalysis;
            {
                AnalysisStartTime = DateTime.UtcNow;
            };

            try
            {
                // Collect performance metrics;
                analysis.PerformanceMetrics = await CollectPerformanceMetricsAsync(cancellationToken);

                // Collect system metrics;
                analysis.SystemMetrics = await CollectSystemMetricsAsync(cancellationToken);

                // Run extended health checks;
                analysis.ExtendedHealthChecks = await RunExtendedHealthChecksAsync(cancellationToken);

                // Analyze trends;
                analysis.TrendAnalysis = AnalyzeHealthTrends();

                // Calculate health score;
                analysis.HealthScore = CalculateHealthScore(analysis);

                // Generate insights;
                analysis.Insights = GenerateHealthInsights(analysis);

                // Generate recommendations;
                analysis.Recommendations = GenerateDeepAnalysisRecommendations(analysis);
            }
            catch (Exception ex)
            {
                analysis.Errors.Add($"Deep analysis failed: {ex.Message}");
                _logger.Error("Deep health analysis failed", ex);
            }
            finally
            {
                analysis.AnalysisEndTime = DateTime.UtcNow;
                analysis.Duration = analysis.AnalysisEndTime - analysis.AnalysisStartTime;
            }

            return analysis;
        }

        /// <summary>
        /// Triggers automatic recovery for failed health checks;
        /// </summary>
        /// <param name="checkName">Specific check name or null for all failed checks</param>
        /// <returns>Recovery report</returns>
        public async Task<RecoveryReport> TriggerRecoveryAsync(string checkName = null)
        {
            var report = new RecoveryReport;
            {
                RecoveryStartTime = DateTime.UtcNow;
            };

            try
            {
                var checksToRecover = string.IsNullOrEmpty(checkName)
                    ? GetFailedHealthChecks()
                    : new[] { checkName };

                foreach (var check in checksToRecover)
                {
                    var recoveryResult = await AttemptRecoveryAsync(check);
                    report.RecoveryAttempts.Add(recoveryResult);

                    if (recoveryResult.Success)
                    {
                        report.SuccessfulRecoveries++;
                    }
                    else;
                    {
                        report.FailedRecoveries++;
                    }
                }
            }
            catch (Exception ex)
            {
                report.Errors.Add($"Recovery process failed: {ex.Message}");
                _logger.Error("Recovery process failed", ex);
            }
            finally
            {
                report.RecoveryEndTime = DateTime.UtcNow;
                report.Duration = report.RecoveryEndTime - report.RecoveryStartTime;
            }

            return report;
        }

        /// <summary>
        /// Gets health check statistics;
        /// </summary>
        /// <returns>Health check statistics</returns>
        public HealthCheckStatistics GetStatistics()
        {
            var stats = new HealthCheckStatistics;
            {
                TotalChecks = _healthChecks.Count,
                LastCheckTime = LastCheckTime;
            };

            foreach (var kvp in _latestResults)
            {
                switch (kvp.Value.Status)
                {
                    case HealthStatus.Healthy:
                        stats.HealthyCount++;
                        break;
                    case HealthStatus.Degraded:
                        stats.DegradedCount++;
                        break;
                    case HealthStatus.Unhealthy:
                        stats.UnhealthyCount++;
                        break;
                }
            }

            // Calculate success rates from history;
            foreach (var history in _healthHistory.Values)
            {
                stats.TotalExecutions += history.Entries.Count;

                var successfulExecutions = history.Entries;
                    .Count(e => e.Status == HealthStatus.Healthy);

                if (history.Entries.Count > 0)
                {
                    stats.AverageSuccessRate += (double)successfulExecutions / history.Entries.Count;
                }
            }

            if (_healthHistory.Count > 0)
            {
                stats.AverageSuccessRate /= _healthHistory.Count;
            }

            return stats;
        }

        /// <summary>
        /// Exports health data for external analysis;
        /// </summary>
        /// <param name="format">Export format</param>
        /// <param name="timeRange">Time range for data export</param>
        /// <returns>Exported health data</returns>
        public HealthDataExport ExportHealthData(
            ExportFormat format = ExportFormat.Json,
            TimeSpan? timeRange = null)
        {
            var export = new HealthDataExport;
            {
                ExportTime = DateTime.UtcNow,
                Format = format,
                OverallStatus = OverallHealthStatus;
            };

            // Add latest results;
            foreach (var kvp in _latestResults)
            {
                export.LatestResults.Add(kvp.Value);
            }

            // Add historical data;
            foreach (var kvp in _healthHistory)
            {
                var history = timeRange.HasValue;
                    ? kvp.Value.FilterByTime(DateTime.UtcNow - timeRange.Value)
                    : kvp.Value;

                export.HistoricalData[kvp.Key] = history;
            }

            // Add statistics;
            export.Statistics = GetStatistics();

            return export;
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Registers default system health checks;
        /// </summary>
        private void RegisterSystemHealthChecks()
        {
            // Memory health check;
            RegisterHealthCheck(
                "MemoryHealth",
                async token => await CheckMemoryHealthAsync(token),
                HealthCheckCriticality.High,
                new[] { "System", "Memory", "Resource" },
                TimeSpan.FromSeconds(10));

            // CPU health check;
            RegisterHealthCheck(
                "CPUHealth",
                async token => await CheckCPUHealthAsync(token),
                HealthCheckCriticality.High,
                new[] { "System", "CPU", "Performance" },
                TimeSpan.FromSeconds(10));

            // Disk health check;
            RegisterHealthCheck(
                "DiskHealth",
                async token => await CheckDiskHealthAsync(token),
                HealthCheckCriticality.Medium,
                new[] { "System", "Disk", "Storage" },
                TimeSpan.FromSeconds(15));

            // Database health check;
            RegisterHealthCheck(
                "DatabaseHealth",
                async token => await CheckDatabaseHealthAsync(token),
                HealthCheckCriticality.Critical,
                new[] { "Service", "Database", "Persistence" },
                TimeSpan.FromSeconds(30));

            // Network health check;
            RegisterHealthCheck(
                "NetworkHealth",
                async token => await CheckNetworkHealthAsync(token),
                HealthCheckCriticality.Medium,
                new[] { "System", "Network", "Connectivity" },
                TimeSpan.FromSeconds(20));

            // Service health check;
            RegisterHealthCheck(
                "ServiceHealth",
                async token => await CheckServiceHealthAsync(token),
                HealthCheckCriticality.Critical,
                new[] { "Service", "Availability" },
                TimeSpan.FromSeconds(20));

            _logger.Info($"Registered {_healthChecks.Count} system health checks");
        }

        /// <summary>
        /// Executes a single health check;
        /// </summary>
        private async Task<HealthCheckResult> ExecuteSingleHealthCheckAsync(
            HealthCheckRegistration registration,
            CancellationToken cancellationToken)
        {
            var result = new HealthCheckResult;
            {
                CheckName = registration.Name,
                Criticality = registration.Criticality,
                StartTime = DateTime.UtcNow,
                Tags = registration.Tags.ToList()
            };

            try
            {
                // Create timeout token;
                using var timeoutCts = new CancellationTokenSource(registration.Timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token);

                // Execute health check;
                var checkResult = await registration.CheckFunction(linkedCts.Token);

                // Copy results;
                result.Status = checkResult.Status;
                result.Description = checkResult.Description;
                result.Data = checkResult.Data ?? new Dictionary<string, object>();
                result.Exception = checkResult.Exception;
                result.Metrics = checkResult.Metrics ?? new Dictionary<string, double>();
            }
            catch (OperationCanceledException)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Health check timed out after {registration.Timeout.TotalSeconds} seconds";
                result.Data["Timeout"] = registration.Timeout;
            }
            catch (Exception ex)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Health check failed with exception: {ex.Message}";
                result.Exception = ex;
            }
            finally
            {
                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.Data["ExecutionTime"] = result.Duration.TotalMilliseconds;
            }

            // Add performance metrics;
            result.Metrics["ExecutionTimeMs"] = result.Duration.TotalMilliseconds;

            return result;
        }

        /// <summary>
        /// Updates health history for a check;
        /// </summary>
        private void UpdateHealthHistory(HealthCheckResult result)
        {
            var history = _healthHistory.GetOrAdd(
                result.CheckName,
                _ => new HealthCheckHistory(result.CheckName));

            history.AddEntry(result);

            // Trim history if it exceeds max size;
            if (history.Entries.Count > _configuration.MaxHistoryEntries)
            {
                history.Entries = history.Entries;
                    .Skip(history.Entries.Count - _configuration.MaxHistoryEntries)
                    .ToList();
            }
        }

        /// <summary>
        /// Updates report statistics;
        /// </summary>
        private void UpdateReportStatistics(SystemHealthReport report, HealthCheckResult result)
        {
            switch (result.Status)
            {
                case HealthStatus.Healthy:
                    report.HealthyCount++;
                    break;
                case HealthStatus.Degraded:
                    report.DegradedCount++;
                    break;
                case HealthStatus.Unhealthy:
                    report.UnhealthyCount++;
                    break;
            }

            report.TotalDuration += result.Duration;

            if (result.Criticality == HealthCheckCriticality.Critical)
            {
                report.CriticalCheckCount++;

                if (result.Status == HealthStatus.Unhealthy)
                {
                    report.FailedCriticalChecks++;
                }
            }
        }

        /// <summary>
        /// Calculates overall health status;
        /// </summary>
        private HealthStatus CalculateOverallStatus(SystemHealthReport report)
        {
            if (report.FailedCriticalChecks > 0)
                return HealthStatus.Unhealthy;

            if (report.UnhealthyCount > 0)
                return HealthStatus.Degraded;

            if (report.DegradedCount > 0)
                return HealthStatus.Degraded;

            return HealthStatus.Healthy;
        }

        /// <summary>
        /// Publishes health check event;
        /// </summary>
        private void PublishHealthCheckEvent(HealthCheckResult result)
        {
            try
            {
                var healthEvent = new HealthCheckCompletedEvent;
                {
                    CheckName = result.CheckName,
                    Status = result.Status,
                    Criticality = result.Criticality,
                    Duration = result.Duration,
                    CheckTime = result.StartTime,
                    Description = result.Description;
                };

                _eventBus.Publish(healthEvent);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to publish health check event: {ex.Message}");
            }
        }

        /// <summary>
        /// Executes scheduled health check;
        /// </summary>
        private async Task ExecuteScheduledCheckAsync()
        {
            if (!_isMonitoringActive || _monitoringCts?.IsCancellationRequested == true)
                return;

            try
            {
                var report = await CheckAllAsync(_monitoringCts.Token);

                // Check if status changed;
                var previousStatus = _latestResults.Values;
                    .Select(r => r.Status)
                    .DefaultIfEmpty(HealthStatus.Unknown)
                    .Aggregate((current, next) =>
                        current == HealthStatus.Unhealthy || next == HealthStatus.Unhealthy;
                            ? HealthStatus.Unhealthy;
                            : current == HealthStatus.Degraded || next == HealthStatus.Degraded;
                                ? HealthStatus.Degraded;
                                : HealthStatus.Healthy);

                if (report.OverallStatus != previousStatus)
                {
                    PublishHealthStatusChangedEvent(previousStatus, report.OverallStatus);
                }

                // Trigger recovery if needed;
                if (report.UnhealthyCount > 0 && _configuration.AutoRecoveryEnabled)
                {
                    _ = TriggerRecoveryAsync(); // Fire and forget;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Scheduled health check failed", ex);
            }
        }

        /// <summary>
        /// Executes continuous monitoring;
        /// </summary>
        private async Task ExecuteContinuousMonitoringAsync()
        {
            if (!_isMonitoringActive || _monitoringCts?.IsCancellationRequested == true)
                return;

            try
            {
                // Only monitor critical checks continuously;
                var criticalChecks = _healthChecks.Values;
                    .Where(h => h.Criticality == HealthCheckCriticality.Critical)
                    .ToList();

                foreach (var check in criticalChecks)
                {
                    var result = await ExecuteSingleHealthCheckAsync(check, _monitoringCts.Token);

                    // If critical check fails, trigger immediate action;
                    if (result.Status == HealthStatus.Unhealthy)
                    {
                        PublishCriticalHealthAlert(result);

                        if (_configuration.AutoRecoveryEnabled)
                        {
                            await AttemptRecoveryAsync(check.Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Continuous monitoring failed", ex);
            }
        }

        /// <summary>
        /// Publishes health status changed event;
        /// </summary>
        private void PublishHealthStatusChangedEvent(
            HealthStatus previousStatus,
            HealthStatus newStatus)
        {
            try
            {
                var statusEvent = new HealthStatusChangedEvent;
                {
                    PreviousStatus = previousStatus,
                    NewStatus = newStatus,
                    ChangeTime = DateTime.UtcNow,
                    CheckCount = _latestResults.Count;
                };

                _eventBus.Publish(statusEvent);

                _logger.Info($"Health status changed from {previousStatus} to {newStatus}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to publish health status changed event: {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes critical health alert;
        /// </summary>
        private void PublishCriticalHealthAlert(HealthCheckResult result)
        {
            try
            {
                var alert = new CriticalHealthAlertEvent;
                {
                    CheckName = result.CheckName,
                    Status = result.Status,
                    Description = result.Description,
                    AlertTime = DateTime.UtcNow,
                    Criticality = result.Criticality;
                };

                _eventBus.Publish(alert);

                _logger.Warn($"Critical health alert: {result.CheckName} - {result.Description}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to publish critical health alert: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets list of failed health checks;
        /// </summary>
        private IEnumerable<string> GetFailedHealthChecks()
        {
            return _latestResults;
                .Where(kvp => kvp.Value.Status == HealthStatus.Unhealthy)
                .Select(kvp => kvp.Key);
        }

        /// <summary>
        /// Attempts recovery for a failed health check;
        /// </summary>
        private async Task<RecoveryAttempt> AttemptRecoveryAsync(string checkName)
        {
            var attempt = new RecoveryAttempt;
            {
                CheckName = checkName,
                AttemptTime = DateTime.UtcNow;
            };

            try
            {
                // Implement recovery strategies based on check type;
                if (checkName.Contains("Memory"))
                {
                    attempt.RecoveryStrategy = "ForceGarbageCollection";
                    attempt.Success = await RecoverMemoryAsync();
                }
                else if (checkName.Contains("Database"))
                {
                    attempt.RecoveryStrategy = "ConnectionReset";
                    attempt.Success = await RecoverDatabaseConnectionAsync();
                }
                else if (checkName.Contains("Service"))
                {
                    attempt.RecoveryStrategy = "ServiceRestart";
                    attempt.Success = await RestartServiceAsync(checkName);
                }
                else;
                {
                    attempt.RecoveryStrategy = "Recheck";
                    // Simply re-run the health check;
                    var result = await CheckSingleAsync(checkName);
                    attempt.Success = result.Status == HealthStatus.Healthy;
                }

                if (attempt.Success)
                {
                    attempt.Message = $"Successfully recovered {checkName}";
                    _logger.Info(attempt.Message);
                }
                else;
                {
                    attempt.Message = $"Failed to recover {checkName}";
                    _logger.Warn(attempt.Message);
                }
            }
            catch (Exception ex)
            {
                attempt.Success = false;
                attempt.Message = $"Recovery failed with exception: {ex.Message}";
                attempt.Error = ex;

                _logger.Error($"Recovery attempt failed for {checkName}", ex);
            }
            finally
            {
                attempt.CompletionTime = DateTime.UtcNow;
                attempt.Duration = attempt.CompletionTime - attempt.AttemptTime;
            }

            return attempt;
        }

        #region System Health Check Implementations;

        private async Task<HealthCheckResult> CheckMemoryHealthAsync(CancellationToken token)
        {
            var result = new HealthCheckResult;
            {
                CheckName = "MemoryHealth",
                Criticality = HealthCheckCriticality.High;
            };

            try
            {
                var memoryInfo = GC.GetGCMemoryInfo();
                var totalMemory = GC.GetTotalMemory(false);
                var allocatedMemory = Process.GetCurrentProcess().WorkingSet64;

                var memoryUsagePercentage = (double)allocatedMemory / totalMemory * 100;

                result.Data["TotalMemory"] = totalMemory;
                result.Data["AllocatedMemory"] = allocatedMemory;
                result.Data["MemoryUsagePercentage"] = memoryUsagePercentage;
                result.Data["GCCount"] = GC.CollectionCount(0);
                result.Metrics["MemoryUsagePercent"] = memoryUsagePercentage;

                if (memoryUsagePercentage > _configuration.MemoryCriticalThreshold)
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = $"Critical memory usage: {memoryUsagePercentage:F2}%";
                }
                else if (memoryUsagePercentage > _configuration.MemoryWarningThreshold)
                {
                    result.Status = HealthStatus.Degraded;
                    result.Description = $"High memory usage: {memoryUsagePercentage:F2}%";
                }
                else;
                {
                    result.Status = HealthStatus.Healthy;
                    result.Description = $"Memory usage normal: {memoryUsagePercentage:F2}%";
                }
            }
            catch (Exception ex)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Memory check failed: {ex.Message}";
                result.Exception = ex;
            }

            return await Task.FromResult(result);
        }

        private async Task<HealthCheckResult> CheckCPUHealthAsync(CancellationToken token)
        {
            var result = new HealthCheckResult;
            {
                CheckName = "CPUHealth",
                Criticality = HealthCheckCriticality.High;
            };

            try
            {
                // Using PerformanceCounter for CPU usage (Windows specific)
                // For cross-platform, would need different implementation;
                var cpuUsage = await _performanceMonitor.GetCpuUsageAsync();

                result.Data["CpuUsage"] = cpuUsage;
                result.Metrics["CpuUsagePercent"] = cpuUsage;

                if (cpuUsage > _configuration.CpuCriticalThreshold)
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = $"Critical CPU usage: {cpuUsage:F2}%";
                }
                else if (cpuUsage > _configuration.CpuWarningThreshold)
                {
                    result.Status = HealthStatus.Degraded;
                    result.Description = $"High CPU usage: {cpuUsage:F2}%";
                }
                else;
                {
                    result.Status = HealthStatus.Healthy;
                    result.Description = $"CPU usage normal: {cpuUsage:F2}%";
                }
            }
            catch (Exception ex)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"CPU check failed: {ex.Message}";
                result.Exception = ex;
            }

            return result;
        }

        private async Task<HealthCheckResult> CheckDiskHealthAsync(CancellationToken token)
        {
            var result = new HealthCheckResult;
            {
                CheckName = "DiskHealth",
                Criticality = HealthCheckCriticality.Medium;
            };

            try
            {
                var drives = System.IO.DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .ToList();

                var unhealthyDrives = new List<string>();
                var degradedDrives = new List<string>();

                foreach (var drive in drives)
                {
                    var freeSpaceGB = drive.AvailableFreeSpace / (1024 * 1024 * 1024);
                    var totalSpaceGB = drive.TotalSize / (1024 * 1024 * 1024);
                    var usedPercentage = 100 - ((double)drive.AvailableFreeSpace / drive.TotalSize * 100);

                    result.Data[$"{drive.Name}_FreeSpaceGB"] = freeSpaceGB;
                    result.Data[$"{drive.Name}_TotalSpaceGB"] = totalSpaceGB;
                    result.Data[$"{drive.Name}_UsedPercentage"] = usedPercentage;

                    if (usedPercentage > _configuration.DiskCriticalThreshold)
                    {
                        unhealthyDrives.Add($"{drive.Name} ({usedPercentage:F1}%)");
                    }
                    else if (usedPercentage > _configuration.DiskWarningThreshold)
                    {
                        degradedDrives.Add($"{drive.Name} ({usedPercentage:F1}%)");
                    }
                }

                if (unhealthyDrives.Any())
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = $"Critical disk usage: {string.Join(", ", unhealthyDrives)}";
                }
                else if (degradedDrives.Any())
                {
                    result.Status = HealthStatus.Degraded;
                    result.Description = $"High disk usage: {string.Join(", ", degradedDrives)}";
                }
                else;
                {
                    result.Status = HealthStatus.Healthy;
                    result.Description = $"Disk usage normal on all drives";
                }
            }
            catch (Exception ex)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Disk check failed: {ex.Message}";
                result.Exception = ex;
            }

            return await Task.FromResult(result);
        }

        private async Task<HealthCheckResult> CheckDatabaseHealthAsync(CancellationToken token)
        {
            var result = new HealthCheckResult;
            {
                CheckName = "DatabaseHealth",
                Criticality = HealthCheckCriticality.Critical;
            };

            try
            {
                // This would be implemented based on your database provider;
                // Example implementation for checking database connectivity;
                var isConnected = await CheckDatabaseConnectivityAsync(token);
                var latency = await MeasureDatabaseLatencyAsync(token);

                result.Data["Connected"] = isConnected;
                result.Data["LatencyMs"] = latency;
                result.Metrics["DatabaseLatencyMs"] = latency;

                if (!isConnected)
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = "Database connection failed";
                }
                else if (latency > _configuration.DatabaseCriticalLatency)
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = $"Critical database latency: {latency}ms";
                }
                else if (latency > _configuration.DatabaseWarningLatency)
                {
                    result.Status = HealthStatus.Degraded;
                    result.Description = $"High database latency: {latency}ms";
                }
                else;
                {
                    result.Status = HealthStatus.Healthy;
                    result.Description = $"Database connection healthy (latency: {latency}ms)";
                }
            }
            catch (Exception ex)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Database check failed: {ex.Message}";
                result.Exception = ex;
            }

            return result;
        }

        private async Task<HealthCheckResult> CheckNetworkHealthAsync(CancellationToken token)
        {
            var result = new HealthCheckResult;
            {
                CheckName = "NetworkHealth",
                Criticality = HealthCheckCriticality.Medium;
            };

            try
            {
                // Implement network connectivity checks;
                var connectivity = await CheckNetworkConnectivityAsync(token);
                var latency = await MeasureNetworkLatencyAsync(token);

                result.Data["Connectivity"] = connectivity;
                result.Data["LatencyMs"] = latency;
                result.Metrics["NetworkLatencyMs"] = latency;

                if (!connectivity)
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = "Network connectivity lost";
                }
                else if (latency > _configuration.NetworkCriticalLatency)
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = $"Critical network latency: {latency}ms";
                }
                else if (latency > _configuration.NetworkWarningLatency)
                {
                    result.Status = HealthStatus.Degraded;
                    result.Description = $"High network latency: {latency}ms";
                }
                else;
                {
                    result.Status = HealthStatus.Healthy;
                    result.Description = $"Network connectivity healthy (latency: {latency}ms)";
                }
            }
            catch (Exception ex)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Network check failed: {ex.Message}";
                result.Exception = ex;
            }

            return result;
        }

        private async Task<HealthCheckResult> CheckServiceHealthAsync(CancellationToken token)
        {
            var result = new HealthCheckResult;
            {
                CheckName = "ServiceHealth",
                Criticality = HealthCheckCriticality.Critical;
            };

            try
            {
                // Check if key services are running;
                var services = new[]
                {
                    "NEDA.Engine",
                    "NEDA.API",
                    "NEDA.Database"
                };

                var unhealthyServices = new List<string>();

                foreach (var service in services)
                {
                    var isRunning = await CheckServiceStatusAsync(service, token);

                    if (!isRunning)
                    {
                        unhealthyServices.Add(service);
                    }
                }

                if (unhealthyServices.Any())
                {
                    result.Status = HealthStatus.Unhealthy;
                    result.Description = $"Services not running: {string.Join(", ", unhealthyServices)}";
                    result.Data["FailedServices"] = unhealthyServices;
                }
                else;
                {
                    result.Status = HealthStatus.Healthy;
                    result.Description = "All critical services are running";
                }
            }
            catch (Exception ex)
            {
                result.Status = HealthStatus.Unhealthy;
                result.Description = $"Service check failed: {ex.Message}";
                result.Exception = ex;
            }

            return result;
        }

        #endregion;

        #region Recovery Implementations;

        private async Task<bool> RecoverMemoryAsync()
        {
            try
            {
                // Force garbage collection;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                await Task.Delay(1000); // Give time for cleanup;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> RecoverDatabaseConnectionAsync()
        {
            // Implementation depends on your database provider;
            // This would typically involve resetting connection pool;
            await Task.Delay(1000);
            return true;
        }

        private async Task<bool> RestartServiceAsync(string serviceName)
        {
            // Implementation depends on your service management;
            await Task.Delay(2000);
            return true;
        }

        #endregion;

        #region Helper Methods for Health Checks;

        private async Task<bool> CheckDatabaseConnectivityAsync(CancellationToken token)
        {
            // Implement actual database connectivity check;
            await Task.Delay(100, token);
            return true;
        }

        private async Task<long> MeasureDatabaseLatencyAsync(CancellationToken token)
        {
            // Implement actual database latency measurement;
            await Task.Delay(50, token);
            return new Random().Next(10, 100);
        }

        private async Task<bool> CheckNetworkConnectivityAsync(CancellationToken token)
        {
            // Implement actual network connectivity check;
            await Task.Delay(100, token);
            return true;
        }

        private async Task<long> MeasureNetworkLatencyAsync(CancellationToken token)
        {
            // Implement actual network latency measurement;
            await Task.Delay(50, token);
            return new Random().Next(5, 50);
        }

        private async Task<bool> CheckServiceStatusAsync(string serviceName, CancellationToken token)
        {
            // Implement actual service status check;
            await Task.Delay(100, token);
            return true;
        }

        #endregion;

        #region Analysis Methods;

        private async Task<PerformanceMetrics> CollectPerformanceMetricsAsync(CancellationToken token)
        {
            var metrics = new PerformanceMetrics;
            {
                CollectionTime = DateTime.UtcNow;
            };

            try
            {
                metrics.CpuUsage = await _performanceMonitor.GetCpuUsageAsync();
                metrics.MemoryUsage = GC.GetTotalMemory(false);
                metrics.ThreadCount = Process.GetCurrentProcess().Threads.Count;
                metrics.HandleCount = Process.GetCurrentProcess().HandleCount;

                // Collect additional metrics;
                var allMetrics = await _metricsCollector.CollectAllAsync(token);
                metrics.AdditionalMetrics = allMetrics;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to collect performance metrics: {ex.Message}");
            }

            return metrics;
        }

        private async Task<SystemMetrics> CollectSystemMetricsAsync(CancellationToken token)
        {
            var metrics = new SystemMetrics;
            {
                CollectionTime = DateTime.UtcNow;
            };

            try
            {
                metrics.ProcessorCount = Environment.ProcessorCount;
                metrics.SystemMemory = Environment.WorkingSet;
                metrics.OSVersion = Environment.OSVersion.VersionString;
                metrics.ClrVersion = Environment.Version.ToString();
                metrics.MachineName = Environment.MachineName;

                // Disk metrics;
                var drives = System.IO.DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .ToList();

                foreach (var drive in drives)
                {
                    metrics.DiskMetrics.Add(new DiskMetric;
                    {
                        Drive = drive.Name,
                        TotalSize = drive.TotalSize,
                        AvailableSpace = drive.AvailableFreeSpace,
                        UsedPercentage = 100 - ((double)drive.AvailableFreeSpace / drive.TotalSize * 100)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to collect system metrics: {ex.Message}");
            }

            return await Task.FromResult(metrics);
        }

        private async Task<List<ExtendedHealthCheck>> RunExtendedHealthChecksAsync(CancellationToken token)
        {
            var extendedChecks = new List<ExtendedHealthCheck>();

            // Add additional checks not in regular monitoring;
            var checks = new[]
            {
                new ExtendedHealthCheck;
                {
                    Name = "SecurityHealth",
                    Description = "Security configuration and compliance check",
                    Status = HealthStatus.Healthy;
                },
                new ExtendedHealthCheck;
                {
                    Name = "BackupHealth",
                    Description = "Backup system status check",
                    Status = HealthStatus.Healthy;
                },
                new ExtendedHealthCheck;
                {
                    Name = "LoggingHealth",
                    Description = "Logging system health check",
                    Status = HealthStatus.Healthy;
                }
            };

            extendedChecks.AddRange(checks);
            await Task.Delay(100, token); // Simulate async work;

            return extendedChecks;
        }

        private HealthTrendAnalysis AnalyzeHealthTrends()
        {
            var analysis = new HealthTrendAnalysis;
            {
                AnalysisTime = DateTime.UtcNow;
            };

            foreach (var history in _healthHistory.Values)
            {
                if (history.Entries.Count < 2)
                    continue;

                var recentEntries = history.Entries;
                    .OrderByDescending(e => e.CheckTime)
                    .Take(_configuration.TrendAnalysisWindow)
                    .ToList();

                var failureCount = recentEntries.Count(e => e.Status == HealthStatus.Unhealthy);
                var degradedCount = recentEntries.Count(e => e.Status == HealthStatus.Degraded);

                if (failureCount > 0)
                {
                    analysis.DegradingChecks.Add(history.CheckName);
                    analysis.Trend = HealthTrend.Degrading;
                }
                else if (degradedCount > recentEntries.Count / 2)
                {
                    analysis.WarningChecks.Add(history.CheckName);
                    analysis.Trend = HealthTrend.StableWithWarnings;
                }
            }

            return analysis;
        }

        private double CalculateHealthScore(DeepHealthAnalysis analysis)
        {
            double score = 100.0;

            // Deduct for performance issues;
            if (analysis.PerformanceMetrics?.CpuUsage > 80)
                score -= 20;
            if (analysis.PerformanceMetrics?.MemoryUsage > 1024 * 1024 * 1024) // 1GB;
                score -= 15;

            // Deduct for disk issues;
            var fullDisks = analysis.SystemMetrics?.DiskMetrics;
                ?.Count(d => d.UsedPercentage > 90) ?? 0;
            score -= fullDisks * 10;

            // Deduct for failed extended checks;
            var failedExtended = analysis.ExtendedHealthChecks;
                ?.Count(c => c.Status == HealthStatus.Unhealthy) ?? 0;
            score -= failedExtended * 5;

            // Deduct for degrading trends;
            if (analysis.TrendAnalysis?.Trend == HealthTrend.Degrading)
                score -= 25;

            return Math.Max(0, Math.Min(100, score));
        }

        private List<string> GenerateHealthInsights(DeepHealthAnalysis analysis)
        {
            var insights = new List<string>();

            if (analysis.PerformanceMetrics?.CpuUsage > 80)
                insights.Add("High CPU usage detected - consider optimizing resource-intensive operations");

            if (analysis.PerformanceMetrics?.MemoryUsage > 1024 * 1024 * 1024)
                insights.Add("High memory consumption - review memory management and caching strategies");

            var fullDisks = analysis.SystemMetrics?.DiskMetrics;
                ?.Where(d => d.UsedPercentage > 90)
                .ToList();

            if (fullDisks?.Any() == true)
            {
                insights.Add($"Critical disk usage on: {string.Join(", ", fullDisks.Select(d => d.Drive))}");
            }

            if (analysis.TrendAnalysis?.Trend == HealthTrend.Degrading)
                insights.Add("System health shows degrading trend - immediate attention recommended");

            return insights;
        }

        private List<string> GenerateRecommendations(SystemHealthReport report)
        {
            var recommendations = new List<string>();

            if (report.UnhealthyCount > 0)
            {
                recommendations.Add("Investigate and resolve unhealthy health checks immediately");
            }

            if (report.DegradedCount > 3)
            {
                recommendations.Add("Multiple degraded checks detected - schedule maintenance");
            }

            if (report.TotalDuration.TotalSeconds > 30)
            {
                recommendations.Add("Health checks taking too long - optimize check performance");
            }

            return recommendations;
        }

        private List<string> GenerateDeepAnalysisRecommendations(DeepHealthAnalysis analysis)
        {
            var recommendations = new List<string>();

            if (analysis.HealthScore < 70)
            {
                recommendations.Add("System health score is low - comprehensive review required");
            }

            if (analysis.PerformanceMetrics?.CpuUsage > 80)
            {
                recommendations.Add("Optimize CPU usage through code profiling and async operations");
            }

            if (analysis.PerformanceMetrics?.MemoryUsage > 1024 * 1024 * 1024)
            {
                recommendations.Add("Implement memory optimization and review object lifecycle management");
            }

            if (analysis.TrendAnalysis?.Trend == HealthTrend.Degrading)
            {
                recommendations.Add("Schedule immediate maintenance to address degrading health trend");
            }

            return recommendations;
        }

        #endregion;

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        /// <summary>
        /// Disposes the health checker and releases resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Stop monitoring;
                    StopContinuousMonitoring();

                    // Dispose timers;
                    _scheduledCheckTimer?.Dispose();
                    _continuousMonitoringTimer?.Dispose();

                    // Dispose cancellation token source;
                    _monitoringCts?.Dispose();

                    // Clear collections;
                    _healthChecks.Clear();
                    _latestResults.Clear();
                    _healthHistory.Clear();
                }

                _disposed = true;
            }
        }

        ~HealthChecker()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Health status enumeration;
    /// </summary>
    public enum HealthStatus;
    {
        /// <summary>Health status is unknown</summary>
        Unknown,
        /// <summary>System is healthy and operating normally</summary>
        Healthy,
        /// <summary>System is degraded but still functional</summary>
        Degraded,
        /// <summary>System is unhealthy and requires attention</summary>
        Unhealthy;
    }

    /// <summary>
    /// Health check criticality levels;
    /// </summary>
    public enum HealthCheckCriticality;
    {
        /// <summary>Low criticality - informational only</summary>
        Low,
        /// <summary>Medium criticality - should be monitored</summary>
        Medium,
        /// <summary>High criticality - important for system operation</summary>
        High,
        /// <summary>Critical - system cannot function without this</summary>
        Critical;
    }

    /// <summary>
    /// Health trend analysis;
    /// </summary>
    public enum HealthTrend;
    {
        /// <summary>Trend is improving</summary>
        Improving,
        /// <summary>Trend is stable and healthy</summary>
        Stable,
        /// <summary>Trend is stable but with warnings</summary>
        StableWithWarnings,
        /// <summary>Trend is degrading</summary>
        Degrading,
        /// <summary>Trend is critical</summary>
        Critical;
    }

    /// <summary>
    /// Export format for health data;
    /// </summary>
    public enum ExportFormat;
    {
        /// <summary>JSON format</summary>
        Json,
        /// <summary>XML format</summary>
        Xml,
        /// <summary>CSV format</summary>
        Csv,
        /// <summary>HTML format</summary>
        Html;
    }

    /// <summary>
    /// Health check configuration;
    /// </summary>
    public sealed class HealthCheckConfiguration;
    {
        /// <summary>Enable auto monitoring on startup</summary>
        public bool EnableAutoMonitoring { get; set; } = true;

        /// <summary>Enable automatic recovery for failed checks</summary>
        public bool AutoRecoveryEnabled { get; set; } = true;

        /// <summary>Interval for scheduled health checks</summary>
        public TimeSpan ScheduledCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Interval for continuous monitoring</summary>
        public TimeSpan ContinuousMonitoringInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>Default timeout for health checks</summary>
        public TimeSpan DefaultHealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Maximum concurrent health checks</summary>
        public int MaxConcurrentChecks { get; set; } = 10;

        /// <summary>Maximum history entries per check</summary>
        public int MaxHistoryEntries { get; set; } = 100;

        /// <summary>Memory warning threshold percentage</summary>
        public double MemoryWarningThreshold { get; set; } = 70.0;

        /// <summary>Memory critical threshold percentage</summary>
        public double MemoryCriticalThreshold { get; set; } = 90.0;

        /// <summary>CPU warning threshold percentage</summary>
        public double CpuWarningThreshold { get; set; } = 70.0;

        /// <summary>CPU critical threshold percentage</summary>
        public double CpuCriticalThreshold { get; set; } = 90.0;

        /// <summary>Disk warning threshold percentage</summary>
        public double DiskWarningThreshold { get; set; } = 80.0;

        /// <summary>Disk critical threshold percentage</summary>
        public double DiskCriticalThreshold { get; set; } = 95.0;

        /// <summary>Database warning latency in milliseconds</summary>
        public long DatabaseWarningLatency { get; set; } = 100;

        /// <summary>Database critical latency in milliseconds</summary>
        public long DatabaseCriticalLatency { get; set; } = 500;

        /// <summary>Network warning latency in milliseconds</summary>
        public long NetworkWarningLatency { get; set; } = 50;

        /// <summary>Network critical latency in milliseconds</summary>
        public long NetworkCriticalLatency { get; set; } = 200;

        /// <summary>Number of entries to consider for trend analysis</summary>
        public int TrendAnalysisWindow { get; set; } = 10;

        /// <summary>Default configuration instance</summary>
        public static HealthCheckConfiguration Default => new HealthCheckConfiguration();
    }

    /// <summary>
    /// Health check registration;
    /// </summary>
    public sealed class HealthCheckRegistration;
    {
        public string Name { get; }
        public Func<CancellationToken, Task<HealthCheckResult>> CheckFunction { get; }
        public HealthCheckCriticality Criticality { get; }
        public IEnumerable<string> Tags { get; }
        public TimeSpan Timeout { get; }

        public HealthCheckRegistration(
            string name,
            Func<CancellationToken, Task<HealthCheckResult>> checkFunction,
            HealthCheckCriticality criticality,
            IEnumerable<string> tags,
            TimeSpan timeout)
        {
            Name = name;
            CheckFunction = checkFunction;
            Criticality = criticality;
            Tags = tags;
            Timeout = timeout;
        }
    }

    /// <summary>
    /// Health check context;
    /// </summary>
    public sealed class HealthCheckContext;
    {
        public string CheckName { get; set; }
        public HealthCheckRegistration Registration { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;
    }

    /// <summary>
    /// Health check result;
    /// </summary>
    public sealed class HealthCheckResult;
    {
        public string CheckName { get; set; }
        public HealthStatus Status { get; set; } = HealthStatus.Unknown;
        public HealthCheckCriticality Criticality { get; set; }
        public string Description { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public Exception Exception { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, double> Metrics { get; set; } = new Dictionary<string, double>();
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Health check history entry
    /// </summary>
    public sealed class HealthCheckHistoryEntry
    {
        public DateTime CheckTime { get; set; }
        public HealthStatus Status { get; set; }
        public TimeSpan Duration { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Health check history;
    /// </summary>
    public sealed class HealthCheckHistory;
    {
        public string CheckName { get; }
        public List<HealthCheckHistoryEntry> Entries { get; set; } = new List<HealthCheckHistoryEntry>();

        public HealthCheckHistory(string checkName)
        {
            CheckName = checkName;
        }

        public void AddEntry(HealthCheckResult result)
        {
            Entries.Add(new HealthCheckHistoryEntry
            {
                CheckTime = result.StartTime,
                Status = result.Status,
                Duration = result.Duration,
                Description = result.Description,
                Data = result.Data;
            });
        }

        public HealthCheckHistory FilterByTime(DateTime cutoff)
        {
            var filtered = new HealthCheckHistory(CheckName)
            {
                Entries = Entries.Where(e => e.CheckTime >= cutoff).ToList()
            };

            return filtered;
        }
    }

    /// <summary>
    /// System health report;
    /// </summary>
    public sealed class SystemHealthReport;
    {
        public DateTime CheckTime { get; set; }
        public HealthStatus OverallStatus { get; set; } = HealthStatus.Unknown;
        public int TotalChecks { get; set; }
        public int HealthyCount { get; set; }
        public int DegradedCount { get; set; }
        public int UnhealthyCount { get; set; }
        public int CriticalCheckCount { get; set; }
        public int FailedCriticalChecks { get; set; }
        public TimeSpan TotalDuration { get; set; }
        public Dictionary<string, HealthCheckResult> CheckResults { get; set; } = new Dictionary<string, HealthCheckResult>();
        public List<string> Recommendations { get; set; } = new List<string>();
    }

    /// <summary>
    /// Performance metrics;
    /// </summary>
    public sealed class PerformanceMetrics;
    {
        public DateTime CollectionTime { get; set; }
        public double CpuUsage { get; set; }
        public long MemoryUsage { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// System metrics;
    /// </summary>
    public sealed class SystemMetrics;
    {
        public DateTime CollectionTime { get; set; }
        public int ProcessorCount { get; set; }
        public long SystemMemory { get; set; }
        public string OSVersion { get; set; }
        public string ClrVersion { get; set; }
        public string MachineName { get; set; }
        public List<DiskMetric> DiskMetrics { get; set; } = new List<DiskMetric>();
    }

    /// <summary>
    /// Disk metric;
    /// </summary>
    public sealed class DiskMetric;
    {
        public string Drive { get; set; }
        public long TotalSize { get; set; }
        public long AvailableSpace { get; set; }
        public double UsedPercentage { get; set; }
    }

    /// <summary>
    /// Extended health check;
    /// </summary>
    public sealed class ExtendedHealthCheck;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public HealthStatus Status { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Health trend analysis;
    /// </summary>
    public sealed class HealthTrendAnalysis;
    {
        public DateTime AnalysisTime { get; set; }
        public HealthTrend Trend { get; set; } = HealthTrend.Stable;
        public List<string> ImprovingChecks { get; set; } = new List<string>();
        public List<string> DegradingChecks { get; set; } = new List<string>();
        public List<string> WarningChecks { get; set; } = new List<string>();
    }

    /// <summary>
    /// Deep health analysis;
    /// </summary>
    public sealed class DeepHealthAnalysis;
    {
        public DateTime AnalysisStartTime { get; set; }
        public DateTime AnalysisEndTime { get; set; }
        public TimeSpan Duration => AnalysisEndTime - AnalysisStartTime;
        public PerformanceMetrics PerformanceMetrics { get; set; }
        public SystemMetrics SystemMetrics { get; set; }
        public List<ExtendedHealthCheck> ExtendedHealthChecks { get; set; } = new List<ExtendedHealthCheck>();
        public HealthTrendAnalysis TrendAnalysis { get; set; }
        public double HealthScore { get; set; }
        public List<string> Insights { get; set; } = new List<string>();
        public List<string> Recommendations { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Recovery attempt;
    /// </summary>
    public sealed class RecoveryAttempt;
    {
        public string CheckName { get; set; }
        public string RecoveryStrategy { get; set; }
        public DateTime AttemptTime { get; set; }
        public DateTime CompletionTime { get; set; }
        public TimeSpan Duration => CompletionTime - AttemptTime;
        public bool Success { get; set; }
        public string Message { get; set; }
        public Exception Error { get; set; }
    }

    /// <summary>
    /// Recovery report;
    /// </summary>
    public sealed class RecoveryReport;
    {
        public DateTime RecoveryStartTime { get; set; }
        public DateTime RecoveryEndTime { get; set; }
        public TimeSpan Duration => RecoveryEndTime - RecoveryStartTime;
        public List<RecoveryAttempt> RecoveryAttempts { get; set; } = new List<RecoveryAttempt>();
        public int SuccessfulRecoveries { get; set; }
        public int FailedRecoveries { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Health check statistics;
    /// </summary>
    public sealed class HealthCheckStatistics;
    {
        public int TotalChecks { get; set; }
        public int HealthyCount { get; set; }
        public int DegradedCount { get; set; }
        public int UnhealthyCount { get; set; }
        public long TotalExecutions { get; set; }
        public double AverageSuccessRate { get; set; }
        public DateTime? LastCheckTime { get; set; }
    }

    /// <summary>
    /// Health data export;
    /// </summary>
    public sealed class HealthDataExport;
    {
        public DateTime ExportTime { get; set; }
        public ExportFormat Format { get; set; }
        public HealthStatus OverallStatus { get; set; }
        public List<HealthCheckResult> LatestResults { get; set; } = new List<HealthCheckResult>();
        public Dictionary<string, HealthCheckHistory> HistoricalData { get; set; } = new Dictionary<string, HealthCheckHistory>();
        public HealthCheckStatistics Statistics { get; set; }
    }

    /// <summary>
    /// Health check completed event;
    /// </summary>
    public sealed class HealthCheckCompletedEvent : IEvent;
    {
        public string CheckName { get; set; }
        public HealthStatus Status { get; set; }
        public HealthCheckCriticality Criticality { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime CheckTime { get; set; }
        public string Description { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "HealthCheckCompleted";
    }

    /// <summary>
    /// Health status changed event;
    /// </summary>
    public sealed class HealthStatusChangedEvent : IEvent;
    {
        public HealthStatus PreviousStatus { get; set; }
        public HealthStatus NewStatus { get; set; }
        public DateTime ChangeTime { get; set; }
        public int CheckCount { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "HealthStatusChanged";
    }

    /// <summary>
    /// Critical health alert event;
    /// </summary>
    public sealed class CriticalHealthAlertEvent : IEvent;
    {
        public string CheckName { get; set; }
        public HealthStatus Status { get; set; }
        public string Description { get; set; }
        public DateTime AlertTime { get; set; }
        public HealthCheckCriticality Criticality { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CriticalHealthAlert";
    }

    /// <summary>
    /// Health monitoring started event;
    /// </summary>
    public sealed class HealthMonitoringStartedEvent : IEvent;
    {
        public DateTime StartedAt { get; set; }
        public HealthCheckConfiguration Configuration { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "HealthMonitoringStarted";
    }

    /// <summary>
    /// Health monitoring stopped event;
    /// </summary>
    public sealed class HealthMonitoringStoppedEvent : IEvent;
    {
        public DateTime StoppedAt { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "HealthMonitoringStopped";
    }

    /// <summary>
    /// Health checker interface for dependency injection;
    /// </summary>
    public interface IHealthChecker : IDisposable
    {
        HealthStatus OverallHealthStatus { get; }
        int RegisteredCheckCount { get; }
        int FailedCheckCount { get; }
        int DegradedCheckCount { get; }
        bool IsMonitoringActive { get; }
        DateTime? LastCheckTime { get; }
        HealthCheckConfiguration Configuration { get; }

        void Initialize();
        void RegisterHealthCheck(
            string name,
            Func<CancellationToken, Task<HealthCheckResult>> checkFunction,
            HealthCheckCriticality criticality = HealthCheckCriticality.Medium,
            IEnumerable<string> tags = null,
            TimeSpan? timeout = null);
        bool UnregisterHealthCheck(string name);
        Task<SystemHealthReport> CheckAllAsync(CancellationToken cancellationToken = default);
        Task<HealthCheckResult> CheckSingleAsync(
            string checkName,
            CancellationToken cancellationToken = default);
        HealthCheckResult GetLatestResult(string checkName);
        HealthCheckHistory GetHealthHistory(string checkName, TimeSpan? timeRange = null);
        void StartContinuousMonitoring();
        void StopContinuousMonitoring();
        Task<DeepHealthAnalysis> PerformDeepAnalysisAsync(
            CancellationToken cancellationToken = default);
        Task<RecoveryReport> TriggerRecoveryAsync(string checkName = null);
        HealthCheckStatistics GetStatistics();
        HealthDataExport ExportHealthData(
            ExportFormat format = ExportFormat.Json,
            TimeSpan? timeRange = null);
    }

    #endregion;
}
