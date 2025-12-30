// NEDA.Monitoring/MetricsCollector/MetricsEngine.cs;
// Not: Proje yapısında MetricsEngine.cs olarak geçiyor ama talep MetricsCollector.cs üzerine;

using NEDA.Common.Utilities;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.Logging;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Monitoring.MetricsCollector;
{
    /// <summary>
    /// Advanced metrics collection and analysis engine for system, application, and business metrics;
    /// Provides real-time metrics processing, aggregation, correlation, and anomaly detection;
    /// </summary>
    public class MetricsEngine : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly DataCollector _dataCollector;
        private readonly PerformanceMonitor _performanceMonitor;

        private readonly ConcurrentDictionary<string, MetricDefinition> _metricDefinitions;
        private readonly ConcurrentDictionary<string, TimeSeries> _timeSeries;
        private readonly ConcurrentDictionary<string, MetricCorrelation> _correlations;
        private readonly ConcurrentDictionary<string, MetricAlertRule> _alertRules;
        private readonly ConcurrentQueue<MetricEvent> _metricEvents;

        private readonly Timer _analysisTimer;
        private readonly Timer _correlationTimer;
        private readonly Timer _alertCheckTimer;
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);
        private readonly object _timeSeriesLock = new object();

        private MetricsEngineSettings _settings;
        private bool _isDisposed;
        private bool _isRunning;
        private long _totalMetricsProcessed;
        private DateTime _startTime;
        private readonly Random _random = new Random();

        /// <summary>
        /// Event triggered when metric value exceeds threshold;
        /// </summary>
        public event EventHandler<MetricThresholdExceededEventArgs> OnThresholdExceeded;

        /// <summary>
        /// Event triggered when anomaly is detected;
        /// </summary>
        public event EventHandler<AnomalyDetectedEventArgs> OnAnomalyDetected;

        /// <summary>
        /// Event triggered when metric correlation is found;
        /// </summary>
        public event EventHandler<MetricCorrelationEventArgs> OnCorrelationFound;

        /// <summary>
        /// Event triggered when metric aggregation is completed;
        /// </summary>
        public event EventHandler<AggregationCompletedEventArgs> OnAggregationCompleted;

        /// <summary>
        /// Current engine status;
        /// </summary>
        public EngineStatus Status { get; private set; }

        /// <summary>
        /// Engine settings;
        /// </summary>
        public MetricsEngineSettings Settings;
        {
            get => _settings;
            set;
            {
                _settings = value ?? throw new ArgumentNullException(nameof(value));
                ConfigureTimers();
            }
        }

        /// <summary>
        /// Total metrics processed;
        /// </summary>
        public long TotalMetricsProcessed => Interlocked.Read(ref _totalMetricsProcessed);

        /// <summary>
        /// Active metric definitions count;
        /// </summary>
        public int ActiveMetricsCount => _metricDefinitions.Count;

        /// <summary>
        /// Active time series count;
        /// </summary>
        public int TimeSeriesCount => _timeSeries.Count;

        /// <summary>
        /// Initialize metrics engine with dependencies;
        /// </summary>
        public MetricsEngine(
            ILogger logger,
            IEventBus eventBus,
            DataCollector dataCollector,
            PerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _dataCollector = dataCollector ?? throw new ArgumentNullException(nameof(dataCollector));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            _metricDefinitions = new ConcurrentDictionary<string, MetricDefinition>();
            _timeSeries = new ConcurrentDictionary<string, TimeSeries>();
            _correlations = new ConcurrentDictionary<string, MetricCorrelation>();
            _alertRules = new ConcurrentDictionary<string, MetricAlertRule>();
            _metricEvents = new ConcurrentQueue<MetricEvent>();

            _settings = MetricsEngineSettings.Default;
            Status = EngineStatus.Stopped;

            InitializeBuiltInMetrics();
            ConfigureTimers();
            RegisterEventHandlers();

            _logger.LogInformation("MetricsEngine initialized successfully", "MetricsEngine");
        }

        /// <summary>
        /// Start metrics processing engine;
        /// </summary>
        public async Task StartEngineAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (_isRunning)
                throw new InvalidOperationException("Metrics engine is already running");

            try
            {
                Status = EngineStatus.Starting;
                _logger.LogInformation("Starting metrics engine", "MetricsEngine");

                // Start data collector if not already running;
                if (_dataCollector.Status != CollectorStatus.Running)
                {
                    await _dataCollector.StartCollectionAsync(cancellationToken);
                }

                // Start timers;
                _analysisTimer?.Change(TimeSpan.Zero, _settings.AnalysisInterval);
                _correlationTimer?.Change(TimeSpan.Zero, _settings.CorrelationInterval);
                _alertCheckTimer?.Change(TimeSpan.Zero, _settings.AlertCheckInterval);

                _isRunning = true;
                _startTime = DateTime.UtcNow;
                Status = EngineStatus.Running;

                _logger.LogInformation($"Metrics engine started with {ActiveMetricsCount} metrics", "MetricsEngine");
            }
            catch (Exception ex)
            {
                Status = EngineStatus.Error;
                _logger.LogError($"Failed to start metrics engine: {ex.Message}", "MetricsEngine", ex);
                throw new MetricsEngineException(MetricErrorCodes.StartFailed,
                    "Failed to start metrics engine", ex);
            }
        }

        /// <summary>
        /// Stop metrics processing engine;
        /// </summary>
        public async Task StopEngineAsync()
        {
            ValidateNotDisposed();

            if (!_isRunning)
                return;

            try
            {
                Status = EngineStatus.Stopping;
                _logger.LogInformation("Stopping metrics engine", "MetricsEngine");

                // Stop timers;
                _analysisTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _correlationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _alertCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                _isRunning = false;
                Status = EngineStatus.Stopped;

                _logger.LogInformation($"Metrics engine stopped. Total metrics processed: {TotalMetricsProcessed}",
                    "MetricsEngine");
            }
            catch (Exception ex)
            {
                Status = EngineStatus.Error;
                _logger.LogError($"Error stopping metrics engine: {ex.Message}", "MetricsEngine", ex);
                throw;
            }
        }

        /// <summary>
        /// Define a new metric for collection and analysis;
        /// </summary>
        public async Task DefineMetricAsync(MetricDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (string.IsNullOrWhiteSpace(definition.MetricId))
                throw new ArgumentException("Metric ID cannot be empty", nameof(definition));

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                if (_metricDefinitions.ContainsKey(definition.MetricId))
                    throw new MetricAlreadyDefinedException($"Metric '{definition.MetricId}' already defined");

                // Initialize time series for the metric;
                var timeSeries = new TimeSeries;
                {
                    MetricId = definition.MetricId,
                    MetricName = definition.Name,
                    RetentionPeriod = definition.RetentionPeriod ?? _settings.DefaultRetentionPeriod,
                    CreatedTime = DateTime.UtcNow;
                };

                // Store metric definition;
                definition.CreatedTime = DateTime.UtcNow;
                _metricDefinitions[definition.MetricId] = definition;

                // Initialize time series storage;
                _timeSeries[definition.MetricId] = timeSeries;

                _logger.LogInformation($"Defined metric: {definition.Name} ({definition.MetricId})",
                    "MetricsEngine");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Record a metric value;
        /// </summary>
        public async Task RecordMetricAsync(string metricId, double value,
            Dictionary<string, string> tags = null, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(metricId))
                throw new ArgumentException("Metric ID cannot be empty", nameof(metricId));

            if (!_metricDefinitions.TryGetValue(metricId, out var definition))
                throw new MetricNotDefinedException($"Metric '{metricId}' is not defined");

            if (!definition.Enabled)
                throw new MetricDisabledException($"Metric '{metricId}' is disabled");

            var timestamp = DateTime.UtcNow;
            var dataPoint = new MetricDataPoint;
            {
                MetricId = metricId,
                Value = value,
                Timestamp = timestamp,
                Tags = tags ?? new Dictionary<string, string>()
            };

            try
            {
                // Store in time series;
                await StoreDataPointAsync(dataPoint, cancellationToken);

                // Check thresholds;
                await CheckThresholdsAsync(definition, value, timestamp, cancellationToken);

                // Process metric events;
                await ProcessMetricEventAsync(new MetricEvent;
                {
                    MetricId = metricId,
                    Value = value,
                    Timestamp = timestamp,
                    EventType = MetricEventType.ValueRecorded;
                }, cancellationToken);

                Interlocked.Increment(ref _totalMetricsProcessed);

                if (_settings.EnableVerboseLogging && TotalMetricsProcessed % 1000 == 0)
                {
                    _logger.LogDebug($"Processed {TotalMetricsProcessed} metrics", "MetricsEngine");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to record metric '{metricId}': {ex.Message}", "MetricsEngine", ex);
                throw new MetricsEngineException(MetricErrorCodes.RecordFailed,
                    $"Failed to record metric '{metricId}'", ex);
            }
        }

        /// <summary>
        /// Record multiple metrics in batch;
        /// </summary>
        public async Task RecordMetricsBatchAsync(IEnumerable<MetricDataPoint> dataPoints,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (dataPoints == null)
                throw new ArgumentNullException(nameof(dataPoints));

            var batch = dataPoints.ToList();
            if (!batch.Any())
                return;

            await _processingLock.WaitAsync(cancellationToken);
            try
            {
                foreach (var dataPoint in batch)
                {
                    if (!_metricDefinitions.ContainsKey(dataPoint.MetricId))
                        continue;

                    await StoreDataPointAsync(dataPoint, cancellationToken);

                    // Process each metric for thresholds and events;
                    if (_metricDefinitions.TryGetValue(dataPoint.MetricId, out var definition))
                    {
                        await CheckThresholdsAsync(definition, dataPoint.Value, dataPoint.Timestamp, cancellationToken);

                        await ProcessMetricEventAsync(new MetricEvent;
                        {
                            MetricId = dataPoint.MetricId,
                            Value = dataPoint.Value,
                            Timestamp = dataPoint.Timestamp,
                            EventType = MetricEventType.ValueRecorded;
                        }, cancellationToken);
                    }
                }

                Interlocked.Add(ref _totalMetricsProcessed, batch.Count);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Get metric statistics for specified time range;
        /// </summary>
        public async Task<MetricStatistics> GetMetricStatisticsAsync(string metricId,
            TimeRange timeRange, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!_metricDefinitions.ContainsKey(metricId))
                throw new MetricNotDefinedException($"Metric '{metricId}' is not defined");

            if (!_timeSeries.TryGetValue(metricId, out var timeSeries))
                throw new TimeSeriesNotFoundException($"Time series for metric '{metricId}' not found");

            var statistics = new MetricStatistics;
            {
                MetricId = metricId,
                MetricName = _metricDefinitions[metricId].Name,
                TimeRange = timeRange,
                CalculatedAt = DateTime.UtcNow;
            };

            try
            {
                // Get data points for time range;
                var dataPoints = await GetDataPointsAsync(metricId, timeRange, cancellationToken);

                if (!dataPoints.Any())
                    return statistics;

                // Calculate basic statistics;
                statistics.Count = dataPoints.Count;
                statistics.Sum = dataPoints.Sum(d => d.Value);
                statistics.Average = statistics.Sum / statistics.Count;
                statistics.Min = dataPoints.Min(d => d.Value);
                statistics.Max = dataPoints.Max(d => d.Value);

                // Calculate variance and standard deviation;
                if (dataPoints.Count > 1)
                {
                    var variance = dataPoints.Sum(d => Math.Pow(d.Value - statistics.Average, 2)) / (dataPoints.Count - 1);
                    statistics.Variance = variance;
                    statistics.StandardDeviation = Math.Sqrt(variance);
                }

                // Calculate percentiles;
                if (dataPoints.Count >= 10)
                {
                    var sorted = dataPoints.Select(d => d.Value).OrderBy(v => v).ToList();
                    statistics.Percentile50 = CalculatePercentile(sorted, 50);
                    statistics.Percentile90 = CalculatePercentile(sorted, 90);
                    statistics.Percentile95 = CalculatePercentile(sorted, 95);
                    statistics.Percentile99 = CalculatePercentile(sorted, 99);
                }

                // Calculate rate of change if applicable;
                if (dataPoints.Count >= 2 && timeRange.Duration.TotalSeconds > 0)
                {
                    var first = dataPoints.First();
                    var last = dataPoints.Last();
                    var timeDiff = (last.Timestamp - first.Timestamp).TotalSeconds;
                    var valueDiff = last.Value - first.Value;

                    statistics.RateOfChange = timeDiff > 0 ? valueDiff / timeDiff : 0;
                }

                return statistics;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to calculate statistics for metric '{metricId}': {ex.Message}",
                    "MetricsEngine", ex);
                throw new MetricsEngineException(MetricErrorCodes.StatisticsFailed,
                    $"Failed to calculate statistics for metric '{metricId}'", ex);
            }
        }

        /// <summary>
        /// Analyze metric for anomalies using statistical methods;
        /// </summary>
        public async Task<AnomalyAnalysis> AnalyzeForAnomaliesAsync(string metricId,
            TimeRange timeRange, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!_metricDefinitions.ContainsKey(metricId))
                throw new MetricNotDefinedException($"Metric '{metricId}' is not defined");

            var analysis = new AnomalyAnalysis;
            {
                MetricId = metricId,
                TimeRange = timeRange,
                AnalysisTime = DateTime.UtcNow;
            };

            try
            {
                // Get historical data for baseline;
                var baselineRange = new TimeRange;
                {
                    StartTime = timeRange.StartTime - TimeSpan.FromHours(24),
                    EndTime = timeRange.StartTime;
                };

                var currentData = await GetDataPointsAsync(metricId, timeRange, cancellationToken);
                var baselineData = await GetDataPointsAsync(metricId, baselineRange, cancellationToken);

                if (!currentData.Any() || !baselineData.Any())
                {
                    analysis.Result = AnomalyResult.InsufficientData;
                    return analysis;
                }

                // Calculate baseline statistics;
                var baselineStats = baselineData.Select(d => d.Value).ToList();
                var baselineMean = baselineStats.Average();
                var baselineStdDev = CalculateStandardDeviation(baselineStats);

                // Analyze current data points;
                var anomalies = new List<AnomalyDetection>();

                foreach (var dataPoint in currentData)
                {
                    var zScore = Math.Abs((dataPoint.Value - baselineMean) / (baselineStdDev + double.Epsilon));

                    if (zScore > _settings.AnomalyDetectionThreshold)
                    {
                        anomalies.Add(new AnomalyDetection;
                        {
                            DataPoint = dataPoint,
                            ZScore = zScore,
                            Deviation = dataPoint.Value - baselineMean,
                            IsAnomaly = true;
                        });
                    }
                }

                analysis.Anomalies = anomalies;
                analysis.AnomalyCount = anomalies.Count;
                analysis.BaselineMean = baselineMean;
                analysis.BaselineStdDev = baselineStdDev;
                analysis.Threshold = _settings.AnomalyDetectionThreshold;

                if (anomalies.Any())
                {
                    analysis.Result = AnomalyResult.AnomaliesDetected;

                    // Trigger anomaly event;
                    OnAnomalyDetected?.Invoke(this, new AnomalyDetectedEventArgs;
                    {
                        MetricId = metricId,
                        Anomalies = anomalies,
                        TimeRange = timeRange,
                        Timestamp = DateTime.UtcNow;
                    });
                }
                else;
                {
                    analysis.Result = AnomalyResult.NoAnomalies;
                }

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Anomaly analysis failed for metric '{metricId}': {ex.Message}",
                    "MetricsEngine", ex);
                throw new MetricsEngineException(MetricErrorCodes.AnomalyAnalysisFailed,
                    $"Anomaly analysis failed for metric '{metricId}'", ex);
            }
        }

        /// <summary>
        /// Find correlations between metrics;
        /// </summary>
        public async Task<CorrelationAnalysis> FindCorrelationsAsync(string primaryMetricId,
            IEnumerable<string> secondaryMetricIds, TimeRange timeRange,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!_metricDefinitions.ContainsKey(primaryMetricId))
                throw new MetricNotDefinedException($"Primary metric '{primaryMetricId}' is not defined");

            var secondaryList = secondaryMetricIds.ToList();
            foreach (var metricId in secondaryList)
            {
                if (!_metricDefinitions.ContainsKey(metricId))
                    throw new MetricNotDefinedException($"Secondary metric '{metricId}' is not defined");
            }

            var analysis = new CorrelationAnalysis;
            {
                PrimaryMetricId = primaryMetricId,
                TimeRange = timeRange,
                AnalysisTime = DateTime.UtcNow;
            };

            try
            {
                // Get data points for primary metric;
                var primaryData = await GetDataPointsAsync(primaryMetricId, timeRange, cancellationToken);
                if (!primaryData.Any())
                {
                    analysis.Result = CorrelationResult.InsufficientData;
                    return analysis;
                }

                // Calculate correlations with each secondary metric;
                var correlations = new List<MetricCorrelation>();

                foreach (var secondaryMetricId in secondaryList)
                {
                    var secondaryData = await GetDataPointsAsync(secondaryMetricId, timeRange, cancellationToken);

                    if (!secondaryData.Any() || primaryData.Count != secondaryData.Count)
                        continue;

                    // Align data points by timestamp;
                    var alignedData = AlignDataPoints(primaryData, secondaryData, timeRange);
                    if (alignedData.Count < 10) // Need minimum data points;
                        continue;

                    var correlation = CalculateCorrelation(alignedData);
                    correlation.MetricId1 = primaryMetricId;
                    correlation.MetricId2 = secondaryMetricId;
                    correlation.TimeRange = timeRange;
                    correlation.CalculatedAt = DateTime.UtcNow;

                    correlations.Add(correlation);

                    // Store significant correlations;
                    if (Math.Abs(correlation.Coefficient) > _settings.CorrelationThreshold)
                    {
                        var correlationKey = $"{primaryMetricId}_{secondaryMetricId}";
                        _correlations[correlationKey] = correlation;

                        // Trigger correlation event;
                        OnCorrelationFound?.Invoke(this, new MetricCorrelationEventArgs;
                        {
                            Correlation = correlation,
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }

                analysis.Correlations = correlations;
                analysis.CorrelationCount = correlations.Count;

                if (correlations.Any(c => Math.Abs(c.Coefficient) > _settings.CorrelationThreshold))
                {
                    analysis.Result = CorrelationResult.StrongCorrelationsFound;
                }
                else if (correlations.Any())
                {
                    analysis.Result = CorrelationResult.WeakCorrelationsFound;
                }
                else;
                {
                    analysis.Result = CorrelationResult.NoCorrelations;
                }

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Correlation analysis failed: {ex.Message}", "MetricsEngine", ex);
                throw new MetricsEngineException(MetricErrorCodes.CorrelationFailed,
                    "Correlation analysis failed", ex);
            }
        }

        /// <summary>
        /// Forecast metric values using time series analysis;
        /// </summary>
        public async Task<ForecastResult> ForecastMetricAsync(string metricId,
            TimeSpan forecastPeriod, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!_metricDefinitions.ContainsKey(metricId))
                throw new MetricNotDefinedException($"Metric '{metricId}' is not defined");

            var result = new ForecastResult;
            {
                MetricId = metricId,
                ForecastPeriod = forecastPeriod,
                GeneratedAt = DateTime.UtcNow;
            };

            try
            {
                // Get historical data for analysis;
                var historicalRange = new TimeRange;
                {
                    EndTime = DateTime.UtcNow,
                    StartTime = DateTime.UtcNow - TimeSpan.FromDays(7) // Last 7 days;
                };

                var historicalData = await GetDataPointsAsync(metricId, historicalRange, cancellationToken);
                if (historicalData.Count < 50) // Need sufficient data;
                {
                    result.Confidence = 0;
                    result.Message = "Insufficient historical data for forecasting";
                    return result;
                }

                // Simple linear regression forecasting;
                var timeValues = historicalData;
                    .Select((d, i) => new { Index = i, Value = d.Value, Time = d.Timestamp })
                    .ToList();

                var n = timeValues.Count;
                var sumX = timeValues.Sum(t => t.Index);
                var sumY = timeValues.Sum(t => t.Value);
                var sumXY = timeValues.Sum(t => t.Index * t.Value);
                var sumX2 = timeValues.Sum(t => t.Index * t.Index);

                var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
                var intercept = (sumY - slope * sumX) / n;

                // Generate forecast points;
                var forecastPoints = new List<ForecastPoint>();
                var forecastStep = forecastPeriod.TotalMinutes / 10; // 10 points in forecast;

                for (int i = 0; i <= 10; i++)
                {
                    var forecastIndex = n + i * forecastStep;
                    var forecastValue = slope * forecastIndex + intercept;
                    var forecastTime = DateTime.UtcNow.AddMinutes(i * forecastStep);

                    forecastPoints.Add(new ForecastPoint;
                    {
                        Timestamp = forecastTime,
                        Value = forecastValue,
                        Confidence = CalculateConfidence(historicalData, forecastValue)
                    });
                }

                result.ForecastPoints = forecastPoints;
                result.Confidence = forecastPoints.Average(p => p.Confidence);
                result.Method = "LinearRegression";
                result.Slope = slope;
                result.Intercept = intercept;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Forecast failed for metric '{metricId}': {ex.Message}",
                    "MetricsEngine", ex);
                throw new MetricsEngineException(MetricErrorCodes.ForecastFailed,
                    $"Forecast failed for metric '{metricId}'", ex);
            }
        }

        /// <summary>
        /// Get metric data points for visualization;
        /// </summary>
        public async Task<List<MetricDataPoint>> GetMetricDataAsync(string metricId,
            TimeRange timeRange, AggregationType aggregation = AggregationType.None,
            TimeSpan? interval = null, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!_metricDefinitions.ContainsKey(metricId))
                throw new MetricNotDefinedException($"Metric '{metricId}' is not defined");

            var rawData = await GetDataPointsAsync(metricId, timeRange, cancellationToken);

            if (aggregation == AggregationType.None || !interval.HasValue || !rawData.Any())
                return rawData;

            // Apply aggregation;
            var aggregatedData = new List<MetricDataPoint>();
            var currentWindowStart = timeRange.StartTime;

            while (currentWindowStart < timeRange.EndTime)
            {
                var windowEnd = currentWindowStart + interval.Value;
                var windowData = rawData;
                    .Where(d => d.Timestamp >= currentWindowStart && d.Timestamp < windowEnd)
                    .ToList();

                if (windowData.Any())
                {
                    double aggregatedValue = 0;

                    switch (aggregation)
                    {
                        case AggregationType.Average:
                            aggregatedValue = windowData.Average(d => d.Value);
                            break;
                        case AggregationType.Sum:
                            aggregatedValue = windowData.Sum(d => d.Value);
                            break;
                        case AggregationType.Max:
                            aggregatedValue = windowData.Max(d => d.Value);
                            break;
                        case AggregationType.Min:
                            aggregatedValue = windowData.Min(d => d.Value);
                            break;
                        case AggregationType.Count:
                            aggregatedValue = windowData.Count;
                            break;
                    }

                    aggregatedData.Add(new MetricDataPoint;
                    {
                        MetricId = metricId,
                        Value = aggregatedValue,
                        Timestamp = currentWindowStart + interval.Value / 2, // Middle of window;
                        Tags = new Dictionary<string, string> { ["aggregation"] = aggregation.ToString() }
                    });
                }

                currentWindowStart = windowEnd;
            }

            return aggregatedData;
        }

        /// <summary>
        /// Add alert rule for metric;
        /// </summary>
        public void AddAlertRule(MetricAlertRule rule)
        {
            ValidateNotDisposed();

            if (rule == null)
                throw new ArgumentNullException(nameof(rule));

            if (string.IsNullOrWhiteSpace(rule.RuleId))
                rule.RuleId = Guid.NewGuid().ToString();

            _alertRules[rule.RuleId] = rule;
            _logger.LogInformation($"Added alert rule: {rule.Name}", "MetricsEngine");
        }

        /// <summary>
        /// Remove alert rule;
        /// </summary>
        public bool RemoveAlertRule(string ruleId)
        {
            ValidateNotDisposed();

            return _alertRules.TryRemove(ruleId, out _);
        }

        /// <summary>
        /// Get engine statistics;
        /// </summary>
        public EngineStatistics GetEngineStatistics()
        {
            ValidateNotDisposed();

            return new EngineStatistics;
            {
                Status = Status,
                IsRunning = _isRunning,
                Uptime = _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                TotalMetricsProcessed = TotalMetricsProcessed,
                ActiveMetricsCount = ActiveMetricsCount,
                TimeSeriesCount = TimeSeriesCount,
                AlertRulesCount = _alertRules.Count,
                CorrelationsCount = _correlations.Count,
                MemoryUsageMB = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
            };
        }

        #region Private Methods;

        private void InitializeBuiltInMetrics()
        {
            // Initialize system metrics;
            var systemMetrics = new[]
            {
                new MetricDefinition;
                {
                    MetricId = "system.cpu.usage",
                    Name = "CPU Usage",
                    Description = "System CPU utilization percentage",
                    MetricType = MetricType.Gauge,
                    Unit = "percent",
                    MinValue = 0,
                    MaxValue = 100,
                    RetentionPeriod = TimeSpan.FromDays(30),
                    Enabled = true;
                },
                new MetricDefinition;
                {
                    MetricId = "system.memory.usage",
                    Name = "Memory Usage",
                    Description = "System memory utilization percentage",
                    MetricType = MetricType.Gauge,
                    Unit = "percent",
                    MinValue = 0,
                    MaxValue = 100,
                    RetentionPeriod = TimeSpan.FromDays(30),
                    Enabled = true;
                },
                new MetricDefinition;
                {
                    MetricId = "system.disk.usage",
                    Name = "Disk Usage",
                    Description = "Disk utilization percentage",
                    MetricType = MetricType.Gauge,
                    Unit = "percent",
                    MinValue = 0,
                    MaxValue = 100,
                    RetentionPeriod = TimeSpan.FromDays(30),
                    Enabled = true;
                },
                new MetricDefinition;
                {
                    MetricId = "system.network.latency",
                    Name = "Network Latency",
                    Description = "Network latency in milliseconds",
                    MetricType = MetricType.Gauge,
                    Unit = "milliseconds",
                    MinValue = 0,
                    MaxValue = 10000,
                    RetentionPeriod = TimeSpan.FromDays(7),
                    Enabled = true;
                },
                new MetricDefinition;
                {
                    MetricId = "application.requests.count",
                    Name = "Request Count",
                    Description = "Number of application requests",
                    MetricType = MetricType.Counter,
                    Unit = "count",
                    MinValue = 0,
                    RetentionPeriod = TimeSpan.FromDays(7),
                    Enabled = true;
                },
                new MetricDefinition;
                {
                    MetricId = "application.response.time",
                    Name = "Response Time",
                    Description = "Application response time in milliseconds",
                    MetricType = MetricType.Histogram,
                    Unit = "milliseconds",
                    MinValue = 0,
                    RetentionPeriod = TimeSpan.FromDays(7),
                    Enabled = true;
                }
            };

            foreach (var metric in systemMetrics)
            {
                _metricDefinitions[metric.MetricId] = metric;

                var timeSeries = new TimeSeries;
                {
                    MetricId = metric.MetricId,
                    MetricName = metric.Name,
                    RetentionPeriod = metric.RetentionPeriod ?? _settings.DefaultRetentionPeriod,
                    CreatedTime = DateTime.UtcNow;
                };

                _timeSeries[metric.MetricId] = timeSeries;
            }
        }

        private void ConfigureTimers()
        {
            _analysisTimer?.Dispose();
            _correlationTimer?.Dispose();
            _alertCheckTimer?.Dispose();

            if (_settings.Enabled)
            {
                _analysisTimer = new Timer(async _ => await PerformAnalysisAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);

                _correlationTimer = new Timer(async _ => await FindCorrelationsAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);

                _alertCheckTimer = new Timer(async _ => await CheckAlertRulesAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void RegisterEventHandlers()
        {
            // Subscribe to data collector events;
            _dataCollector.OnMetricsCollected += async (sender, args) =>
            {
                if (!_isRunning) return;

                try
                {
                    await ProcessCollectedMetricsAsync(args.Metrics, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to process collected metrics: {ex.Message}",
                        "MetricsEngine", ex);
                }
            };
        }

        private async Task ProcessCollectedMetricsAsync(List<MetricData> metrics,
            CancellationToken cancellationToken)
        {
            var dataPoints = new List<MetricDataPoint>();

            foreach (var metric in metrics)
            {
                // Convert collected metrics to data points;
                var metricId = metric.MetricName.Replace(".", "_").ToLowerInvariant();

                if (!_metricDefinitions.ContainsKey(metricId))
                {
                    // Auto-create metric definition if not exists;
                    await DefineMetricAsync(new MetricDefinition;
                    {
                        MetricId = metricId,
                        Name = metric.MetricName,
                        MetricType = MetricType.Gauge,
                        Unit = "unit",
                        Enabled = true;
                    }, cancellationToken);
                }

                dataPoints.Add(new MetricDataPoint;
                {
                    MetricId = metricId,
                    Value = metric.Value,
                    Timestamp = metric.Timestamp,
                    Tags = metric.Tags;
                });
            }

            if (dataPoints.Any())
            {
                await RecordMetricsBatchAsync(dataPoints, cancellationToken);
            }
        }

        private async Task StoreDataPointAsync(MetricDataPoint dataPoint, CancellationToken cancellationToken)
        {
            if (!_timeSeries.TryGetValue(dataPoint.MetricId, out var timeSeries))
                return;

            lock (_timeSeriesLock)
            {
                // Add to time series storage;
                // In production, this would write to a time-series database;
                // For now, we'll maintain an in-memory list with retention policy;

                if (!timeSeries.DataPoints.ContainsKey(dataPoint.Timestamp))
                {
                    timeSeries.DataPoints[dataPoint.Timestamp] = dataPoint;

                    // Apply retention policy;
                    var cutoff = DateTime.UtcNow - timeSeries.RetentionPeriod;
                    var expired = timeSeries.DataPoints.Keys.Where(t => t < cutoff).ToList();

                    foreach (var expiredTime in expired)
                    {
                        timeSeries.DataPoints.Remove(expiredTime);
                    }

                    _timeSeries[dataPoint.MetricId] = timeSeries;
                }
            }

            await Task.CompletedTask;
        }

        private async Task<List<MetricDataPoint>> GetDataPointsAsync(string metricId, TimeRange timeRange,
            CancellationToken cancellationToken)
        {
            if (!_timeSeries.TryGetValue(metricId, out var timeSeries))
                return new List<MetricDataPoint>();

            var dataPoints = timeSeries.DataPoints.Values;
                .Where(d => d.Timestamp >= timeRange.StartTime && d.Timestamp <= timeRange.EndTime)
                .OrderBy(d => d.Timestamp)
                .ToList();

            return await Task.FromResult(dataPoints);
        }

        private async Task CheckThresholdsAsync(MetricDefinition definition, double value,
            DateTime timestamp, CancellationToken cancellationToken)
        {
            if (!definition.Thresholds.Any())
                return;

            foreach (var threshold in definition.Thresholds)
            {
                bool thresholdExceeded = false;

                switch (threshold.Operator)
                {
                    case ThresholdOperator.GreaterThan:
                        thresholdExceeded = value > threshold.Value;
                        break;
                    case ThresholdOperator.GreaterThanOrEqual:
                        thresholdExceeded = value >= threshold.Value;
                        break;
                    case ThresholdOperator.LessThan:
                        thresholdExceeded = value < threshold.Value;
                        break;
                    case ThresholdOperator.LessThanOrEqual:
                        thresholdExceeded = value <= threshold.Value;
                        break;
                    case ThresholdOperator.Equal:
                        thresholdExceeded = Math.Abs(value - threshold.Value) < double.Epsilon;
                        break;
                }

                if (thresholdExceeded)
                {
                    OnThresholdExceeded?.Invoke(this, new MetricThresholdExceededEventArgs;
                    {
                        MetricId = definition.MetricId,
                        MetricName = definition.Name,
                        Value = value,
                        Threshold = threshold,
                        Timestamp = timestamp,
                        Severity = threshold.Severity;
                    });

                    await ProcessMetricEventAsync(new MetricEvent;
                    {
                        MetricId = definition.MetricId,
                        Value = value,
                        Timestamp = timestamp,
                        EventType = MetricEventType.ThresholdExceeded,
                        Details = $"Threshold {threshold.Operator} {threshold.Value}"
                    }, cancellationToken);
                }
            }
        }

        private async Task ProcessMetricEventAsync(MetricEvent metricEvent,
            CancellationToken cancellationToken)
        {
            _metricEvents.Enqueue(metricEvent);

            // Maintain queue size;
            while (_metricEvents.Count > _settings.MaxEventsInMemory)
            {
                _metricEvents.TryDequeue(out _);
            }

            await Task.CompletedTask;
        }

        private async Task PerformAnalysisAsync()
        {
            if (!_processingLock.Wait(0))
                return;

            try
            {
                var metrics = _metricDefinitions.Values.Where(m => m.Enabled).ToList();

                foreach (var metric in metrics)
                {
                    // Perform periodic analysis on each metric;
                    var timeRange = new TimeRange;
                    {
                        EndTime = DateTime.UtcNow,
                        StartTime = DateTime.UtcNow - TimeSpan.FromMinutes(30)
                    };

                    var stats = await GetMetricStatisticsAsync(metric.MetricId, timeRange, CancellationToken.None);

                    // Store analysis results;
                    metric.LastAnalysisTime = DateTime.UtcNow;
                    metric.LastAnalysisResult = stats;

                    _metricDefinitions[metric.MetricId] = metric;
                }

                // Trigger aggregation completed event;
                OnAggregationCompleted?.Invoke(this, new AggregationCompletedEventArgs;
                {
                    MetricsAnalyzed = metrics.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Performed analysis on {metrics.Count} metrics", "MetricsEngine");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Analysis failed: {ex.Message}", "MetricsEngine", ex);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        private async Task FindCorrelationsAsync()
        {
            if (!_settings.EnableCorrelationAnalysis)
                return;

            try
            {
                var metrics = _metricDefinitions.Values.Where(m => m.Enabled).ToList();
                if (metrics.Count < 2)
                    return;

                var timeRange = new TimeRange;
                {
                    EndTime = DateTime.UtcNow,
                    StartTime = DateTime.UtcNow - TimeSpan.FromHours(1)
                };

                // Find correlations between system metrics;
                var systemMetrics = metrics.Where(m => m.MetricId.StartsWith("system.")).ToList();

                if (systemMetrics.Count >= 2)
                {
                    var primary = systemMetrics.First();
                    var secondary = systemMetrics.Skip(1).Select(m => m.MetricId).Take(5).ToList();

                    await FindCorrelationsAsync(primary.MetricId, secondary, timeRange, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Correlation analysis failed: {ex.Message}", "MetricsEngine", ex);
            }
        }

        private async Task CheckAlertRulesAsync()
        {
            try
            {
                foreach (var rule in _alertRules.Values.Where(r => r.Enabled))
                {
                    var timeRange = new TimeRange;
                    {
                        EndTime = DateTime.UtcNow,
                        StartTime = DateTime.UtcNow - rule.EvaluationWindow;
                    };

                    var stats = await GetMetricStatisticsAsync(rule.MetricId, timeRange, CancellationToken.None);

                    bool shouldAlert = false;

                    switch (rule.Condition)
                    {
                        case AlertCondition.AboveAverage:
                            shouldAlert = stats.CurrentValue > (stats.Average * (1 + rule.Threshold));
                            break;
                        case AlertCondition.BelowAverage:
                            shouldAlert = stats.CurrentValue < (stats.Average * (1 - rule.Threshold));
                            break;
                        case AlertCondition.AboveThreshold:
                            shouldAlert = stats.CurrentValue > rule.Threshold;
                            break;
                        case AlertCondition.BelowThreshold:
                            shouldAlert = stats.CurrentValue < rule.Threshold;
                            break;
                        case AlertCondition.Spike:
                            // Check for sudden spikes;
                            if (stats.StandardDeviation > 0)
                            {
                                var zScore = Math.Abs((stats.CurrentValue - stats.Average) / stats.StandardDeviation);
                                shouldAlert = zScore > 3; // 3 standard deviations;
                            }
                            break;
                    }

                    if (shouldAlert && !rule.LastTriggered.HasValue)
                    {
                        // Trigger alert;
                        rule.LastTriggered = DateTime.UtcNow;
                        rule.TriggerCount++;

                        _logger.LogWarning($"Alert triggered: {rule.Name} for metric {rule.MetricId}",
                            "MetricsEngine");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Alert check failed: {ex.Message}", "MetricsEngine", ex);
            }
        }

        private double CalculatePercentile(List<double> sortedValues, double percentile)
        {
            if (!sortedValues.Any())
                return 0;

            var index = (percentile / 100) * (sortedValues.Count - 1);
            var floor = (int)Math.Floor(index);
            var ceiling = (int)Math.Ceiling(index);

            if (floor == ceiling)
                return sortedValues[floor];

            var lowerValue = sortedValues[floor];
            var upperValue = sortedValues[ceiling];
            var weight = index - floor;

            return lowerValue + (upperValue - lowerValue) * weight;
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            if (values.Count < 2)
                return 0;

            var mean = values.Average();
            var variance = values.Sum(v => Math.Pow(v - mean, 2)) / (values.Count - 1);
            return Math.Sqrt(variance);
        }

        private List<Tuple<double, double>> AlignDataPoints(List<MetricDataPoint> data1,
            List<MetricDataPoint> data2, TimeRange timeRange)
        {
            var aligned = new List<Tuple<double, double>>();

            var interval = TimeSpan.FromSeconds(30); // 30-second intervals;
            var current = timeRange.StartTime;

            while (current <= timeRange.EndTime)
            {
                var windowEnd = current + interval;

                var point1 = data1;
                    .Where(d => d.Timestamp >= current && d.Timestamp < windowEnd)
                    .Average(d => d.Value);

                var point2 = data2;
                    .Where(d => d.Timestamp >= current && d.Timestamp < windowEnd)
                    .Average(d => d.Value);

                aligned.Add(Tuple.Create(point1, point2));
                current = windowEnd;
            }

            return aligned;
        }

        private MetricCorrelation CalculateCorrelation(List<Tuple<double, double>> data)
        {
            if (data.Count < 2)
                return new MetricCorrelation { Coefficient = 0 };

            var xValues = data.Select(d => d.Item1).ToList();
            var yValues = data.Select(d => d.Item2).ToList();

            var meanX = xValues.Average();
            var meanY = yValues.Average();

            var sumXY = 0.0;
            var sumX2 = 0.0;
            var sumY2 = 0.0;

            for (int i = 0; i < data.Count; i++)
            {
                var xDiff = xValues[i] - meanX;
                var yDiff = yValues[i] - meanY;

                sumXY += xDiff * yDiff;
                sumX2 += xDiff * xDiff;
                sumY2 += yDiff * yDiff;
            }

            var correlation = sumXY / (Math.Sqrt(sumX2) * Math.Sqrt(sumY2) + double.Epsilon);

            return new MetricCorrelation;
            {
                Coefficient = correlation,
                DataPointCount = data.Count;
            };
        }

        private double CalculateConfidence(List<MetricDataPoint> historicalData, double forecastValue)
        {
            if (historicalData.Count < 10)
                return 0.5;

            var recentData = historicalData;
                .OrderByDescending(d => d.Timestamp)
                .Take(20)
                .Select(d => d.Value)
                .ToList();

            var recentStdDev = CalculateStandardDeviation(recentData);
            var recentMean = recentData.Average();

            // Confidence based on how close forecast is to recent mean and std dev;
            var zScore = Math.Abs((forecastValue - recentMean) / (recentStdDev + double.Epsilon));

            // Higher confidence for values closer to mean;
            var confidence = Math.Max(0, 1 - (zScore / 5)); // Cap at 5 std devs;

            return Math.Min(confidence, 0.95); // Max 95% confidence;
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(MetricsEngine));
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _analysisTimer?.Dispose();
                    _correlationTimer?.Dispose();
                    _alertCheckTimer?.Dispose();
                    _processingLock?.Dispose();

                    // Stop engine if running;
                    if (_isRunning)
                    {
                        StopEngineAsync().GetAwaiter().GetResult();
                    }

                    // Clear collections;
                    _metricDefinitions.Clear();
                    _timeSeries.Clear();
                    _correlations.Clear();
                    _alertRules.Clear();
                    _metricEvents.Clear();

                    Status = EngineStatus.Disposed;
                    _logger.LogInformation("MetricsEngine disposed", "MetricsEngine");
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
    /// Metrics engine status;
    /// </summary>
    public enum EngineStatus;
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error,
        Disposed;
    }

    /// <summary>
    /// Metric type classification;
    /// </summary>
    public enum MetricType;
    {
        Gauge,
        Counter,
        Histogram,
        Summary,
        Custom;
    }

    /// <summary>
    /// Aggregation type for metric data;
    /// </summary>
    public enum AggregationType;
    {
        None,
        Average,
        Sum,
        Max,
        Min,
        Count;
    }

    /// <summary>
    /// Threshold comparison operator;
    /// </summary>
    public enum ThresholdOperator;
    {
        GreaterThan,
        GreaterThanOrEqual,
        LessThan,
        LessThanOrEqual,
        Equal;
    }

    /// <summary>
    /// Threshold severity level;
    /// </summary>
    public enum ThresholdSeverity;
    {
        Info,
        Warning,
        Critical;
    }

    /// <summary>
    /// Metric event type;
    /// </summary>
    public enum MetricEventType;
    {
        ValueRecorded,
        ThresholdExceeded,
        AnomalyDetected,
        CorrelationFound;
    }

    /// <summary>
    /// Anomaly detection result;
    /// </summary>
    public enum AnomalyResult;
    {
        NoAnomalies,
        AnomaliesDetected,
        InsufficientData;
    }

    /// <summary>
    /// Correlation analysis result;
    /// </summary>
    public enum CorrelationResult;
    {
        NoCorrelations,
        WeakCorrelationsFound,
        StrongCorrelationsFound,
        InsufficientData;
    }

    /// <summary>
    /// Alert condition type;
    /// </summary>
    public enum AlertCondition;
    {
        AboveAverage,
        BelowAverage,
        AboveThreshold,
        BelowThreshold,
        Spike;
    }

    /// <summary>
    /// Metrics engine settings;
    /// </summary>
    public class MetricsEngineSettings;
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan AnalysisInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan CorrelationInterval { get; set; } = TimeSpan.FromMinutes(10);
        public TimeSpan AlertCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan DefaultRetentionPeriod { get; set; } = TimeSpan.FromDays(7);
        public double AnomalyDetectionThreshold { get; set; } = 3.0; // Z-score threshold;
        public double CorrelationThreshold { get; set; } = 0.7; // Minimum correlation coefficient;
        public int MaxEventsInMemory { get; set; } = 10000;
        public bool EnableCorrelationAnalysis { get; set; } = true;
        public bool EnableAnomalyDetection { get; set; } = true;
        public bool EnableVerboseLogging { get; set; } = false;

        public static MetricsEngineSettings Default => new MetricsEngineSettings();
    }

    /// <summary>
    /// Metric definition;
    /// </summary>
    public class MetricDefinition;
    {
        public string MetricId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public MetricType MetricType { get; set; }
        public string Unit { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public TimeSpan? RetentionPeriod { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTime CreatedTime { get; set; }
        public DateTime LastModifiedTime { get; set; }
        public DateTime? LastAnalysisTime { get; set; }
        public MetricStatistics LastAnalysisResult { get; set; }
        public List<MetricThreshold> Thresholds { get; set; } = new List<MetricThreshold>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Metric threshold definition;
    /// </summary>
    public class MetricThreshold;
    {
        public string ThresholdId { get; set; } = Guid.NewGuid().ToString();
        public double Value { get; set; }
        public ThresholdOperator Operator { get; set; }
        public ThresholdSeverity Severity { get; set; }
        public string Message { get; set; }
        public TimeSpan? Duration { get; set; } // Required duration for threshold;
    }

    /// <summary>
    /// Time series data;
    /// </summary>
    public class TimeSeries;
    {
        public string MetricId { get; set; }
        public string MetricName { get; set; }
        public TimeSpan RetentionPeriod { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public SortedDictionary<DateTime, MetricDataPoint> DataPoints { get; set; } = new SortedDictionary<DateTime, MetricDataPoint>();
    }

    /// <summary>
    /// Metric data point;
    /// </summary>
    public class MetricDataPoint;
    {
        public string MetricId { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Time range for queries;
    /// </summary>
    public class TimeRange;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public TimeSpan Duration => EndTime - StartTime;
    }

    /// <summary>
    /// Metric statistics;
    /// </summary>
    public class MetricStatistics;
    {
        public string MetricId { get; set; }
        public string MetricName { get; set; }
        public TimeRange TimeRange { get; set; }
        public DateTime CalculatedAt { get; set; }
        public int Count { get; set; }
        public double Sum { get; set; }
        public double Average { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Variance { get; set; }
        public double StandardDeviation { get; set; }
        public double Percentile50 { get; set; }
        public double Percentile90 { get; set; }
        public double Percentile95 { get; set; }
        public double Percentile99 { get; set; }
        public double RateOfChange { get; set; }
        public double CurrentValue { get; set; }
        public Dictionary<string, object> AdditionalStats { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Anomaly analysis result;
    /// </summary>
    public class AnomalyAnalysis;
    {
        public string MetricId { get; set; }
        public TimeRange TimeRange { get; set; }
        public DateTime AnalysisTime { get; set; }
        public AnomalyResult Result { get; set; }
        public int AnomalyCount { get; set; }
        public List<AnomalyDetection> Anomalies { get; set; } = new List<AnomalyDetection>();
        public double BaselineMean { get; set; }
        public double BaselineStdDev { get; set; }
        public double Threshold { get; set; }
        public Dictionary<string, object> AnalysisDetails { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Anomaly detection details;
    /// </summary>
    public class AnomalyDetection;
    {
        public MetricDataPoint DataPoint { get; set; }
        public double ZScore { get; set; }
        public double Deviation { get; set; }
        public bool IsAnomaly { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Correlation analysis result;
    /// </summary>
    public class CorrelationAnalysis;
    {
        public string PrimaryMetricId { get; set; }
        public TimeRange TimeRange { get; set; }
        public DateTime AnalysisTime { get; set; }
        public CorrelationResult Result { get; set; }
        public int CorrelationCount { get; set; }
        public List<MetricCorrelation> Correlations { get; set; } = new List<MetricCorrelation>();
        public Dictionary<string, object> AnalysisDetails { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Metric correlation details;
    /// </summary>
    public class MetricCorrelation;
    {
        public string MetricId1 { get; set; }
        public string MetricId2 { get; set; }
        public double Coefficient { get; set; } // -1 to 1;
        public TimeRange TimeRange { get; set; }
        public int DataPointCount { get; set; }
        public DateTime CalculatedAt { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Forecast result;
    /// </summary>
    public class ForecastResult;
    {
        public string MetricId { get; set; }
        public TimeSpan ForecastPeriod { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<ForecastPoint> ForecastPoints { get; set; } = new List<ForecastPoint>();
        public double Confidence { get; set; } // 0-1;
        public string Method { get; set; }
        public double Slope { get; set; }
        public double Intercept { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> ForecastDetails { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Forecast point;
    /// </summary>
    public class ForecastPoint;
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
        public double Confidence { get; set; } // 0-1;
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Metric event;
    /// </summary>
    public class MetricEvent;
    {
        public string MetricId { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public MetricEventType EventType { get; set; }
        public string Details { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Metric alert rule;
    /// </summary>
    public class MetricAlertRule;
    {
        public string RuleId { get; set; }
        public string Name { get; set; }
        public string MetricId { get; set; }
        public AlertCondition Condition { get; set; }
        public double Threshold { get; set; }
        public TimeSpan EvaluationWindow { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan CooldownPeriod { get; set; } = TimeSpan.FromMinutes(1);
        public bool Enabled { get; set; } = true;
        public DateTime? LastTriggered { get; set; }
        public int TriggerCount { get; set; }
        public string MessageTemplate { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Engine statistics;
    /// </summary>
    public class EngineStatistics;
    {
        public EngineStatus Status { get; set; }
        public bool IsRunning { get; set; }
        public TimeSpan Uptime { get; set; }
        public long TotalMetricsProcessed { get; set; }
        public int ActiveMetricsCount { get; set; }
        public int TimeSeriesCount { get; set; }
        public int AlertRulesCount { get; set; }
        public int CorrelationsCount { get; set; }
        public long MemoryUsageMB { get; set; }
    }

    /// <summary>
    /// Event arguments for threshold exceeded;
    /// </summary>
    public class MetricThresholdExceededEventArgs : EventArgs;
    {
        public string MetricId { get; set; }
        public string MetricName { get; set; }
        public double Value { get; set; }
        public MetricThreshold Threshold { get; set; }
        public DateTime Timestamp { get; set; }
        public ThresholdSeverity Severity { get; set; }
    }

    /// <summary>
    /// Event arguments for anomaly detected;
    /// </summary>
    public class AnomalyDetectedEventArgs : EventArgs;
    {
        public string MetricId { get; set; }
        public List<AnomalyDetection> Anomalies { get; set; }
        public TimeRange TimeRange { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for correlation found;
    /// </summary>
    public class MetricCorrelationEventArgs : EventArgs;
    {
        public MetricCorrelation Correlation { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Event arguments for aggregation completed;
    /// </summary>
    public class AggregationCompletedEventArgs : EventArgs;
    {
        public int MetricsAnalyzed { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Metrics engine specific exception;
    /// </summary>
    public class MetricsEngineException : Exception
    {
        public string ErrorCode { get; }

        public MetricsEngineException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public MetricsEngineException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Metric already defined exception;
    /// </summary>
    public class MetricAlreadyDefinedException : Exception
    {
        public MetricAlreadyDefinedException(string message) : base(message) { }
    }

    /// <summary>
    /// Metric not defined exception;
    /// </summary>
    public class MetricNotDefinedException : Exception
    {
        public MetricNotDefinedException(string message) : base(message) { }
    }

    /// <summary>
    /// Metric disabled exception;
    /// </summary>
    public class MetricDisabledException : Exception
    {
        public MetricDisabledException(string message) : base(message) { }
    }

    /// <summary>
    /// Time series not found exception;
    /// </summary>
    public class TimeSeriesNotFoundException : Exception
    {
        public TimeSeriesNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Metric error codes;
    /// </summary>
    public static class MetricErrorCodes;
    {
        public const string StartFailed = "METRIC_001";
        public const string RecordFailed = "METRIC_002";
        public const string StatisticsFailed = "METRIC_003";
        public const string AnomalyAnalysisFailed = "METRIC_004";
        public const string CorrelationFailed = "METRIC_005";
        public const string ForecastFailed = "METRIC_006";
        public const string InvalidMetric = "METRIC_007";
    }

    #endregion;
}
