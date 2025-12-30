// NEDA.Monitoring/MetricsCollector/DataCollector.cs;

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
    /// High-performance, multi-source data collection system for system and application metrics;
    /// Provides real-time data collection, aggregation, and forwarding capabilities;
    /// </summary>
    public class DataCollector : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly PerformanceMonitor _performanceMonitor;

        private readonly ConcurrentDictionary<string, DataSource> _dataSources;
        private readonly ConcurrentQueue<MetricData> _metricBuffer;
        private readonly ConcurrentDictionary<string, MetricAggregate> _aggregates;
        private readonly List<IDataProcessor> _dataProcessors;
        private readonly List<IMetricSink> _metricSinks;

        private readonly Timer _collectionTimer;
        private readonly Timer _aggregationTimer;
        private readonly Timer _flushTimer;
        private readonly SemaphoreSlim _collectionLock = new SemaphoreSlim(1, 1);
        private readonly object _bufferLock = new object();

        private DataCollectorSettings _settings;
        private bool _isDisposed;
        private bool _isCollecting;
        private long _totalMetricsCollected;
        private long _totalBytesCollected;
        private DateTime _startTime;

        /// <summary>
        /// Event triggered when new metrics are collected;
        /// </summary>
        public event EventHandler<MetricsCollectedEventArgs> OnMetricsCollected;

        /// <summary>
        /// Event triggered when data collection error occurs;
        /// </summary>
        public event EventHandler<DataCollectionErrorEventArgs> OnCollectionError;

        /// <summary>
        /// Event triggered when buffer is about to overflow;
        /// </summary>
        public event EventHandler<BufferWarningEventArgs> OnBufferWarning;

        /// <summary>
        /// Current collector status;
        /// </summary>
        public CollectorStatus Status { get; private set; }

        /// <summary>
        /// Collector settings;
        /// </summary>
        public DataCollectorSettings Settings;
        {
            get => _settings;
            set;
            {
                _settings = value ?? throw new ArgumentNullException(nameof(value));
                ConfigureTimers();
            }
        }

        /// <summary>
        /// Total metrics collected since start;
        /// </summary>
        public long TotalMetricsCollected => Interlocked.Read(ref _totalMetricsCollected);

        /// <summary>
        /// Total bytes collected since start;
        /// </summary>
        public long TotalBytesCollected => Interlocked.Read(ref _totalBytesCollected);

        /// <summary>
        /// Current buffer size;
        /// </summary>
        public int CurrentBufferSize => _metricBuffer.Count;

        /// <summary>
        /// Active data sources count;
        /// </summary>
        public int ActiveSourcesCount => _dataSources.Count;

        /// <summary>
        /// Initialize data collector with dependencies;
        /// </summary>
        public DataCollector(
            ILogger logger,
            IEventBus eventBus,
            PerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            _dataSources = new ConcurrentDictionary<string, DataSource>();
            _metricBuffer = new ConcurrentQueue<MetricData>();
            _aggregates = new ConcurrentDictionary<string, MetricAggregate>();
            _dataProcessors = new List<IDataProcessor>();
            _metricSinks = new List<IMetricSink>();

            _settings = DataCollectorSettings.Default;
            Status = CollectorStatus.Stopped;

            InitializeBuiltInSources();
            ConfigureTimers();

            _logger.LogInformation("DataCollector initialized successfully", "DataCollector");
        }

        /// <summary>
        /// Start data collection;
        /// </summary>
        public async Task StartCollectionAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (_isCollecting)
                throw new InvalidOperationException("Data collection is already running");

            try
            {
                Status = CollectorStatus.Starting;
                _logger.LogInformation("Starting data collection", "DataCollector");

                // Start all data sources;
                await StartAllDataSourcesAsync(cancellationToken);

                // Start timers;
                _collectionTimer?.Change(TimeSpan.Zero, _settings.CollectionInterval);
                _aggregationTimer?.Change(TimeSpan.Zero, _settings.AggregationInterval);
                _flushTimer?.Change(TimeSpan.Zero, _settings.FlushInterval);

                _isCollecting = true;
                _startTime = DateTime.UtcNow;
                Status = CollectorStatus.Running;

                _logger.LogInformation($"Data collection started with {_dataSources.Count} sources", "DataCollector");
            }
            catch (Exception ex)
            {
                Status = CollectorStatus.Error;
                _logger.LogError($"Failed to start data collection: {ex.Message}", "DataCollector", ex);
                throw new DataCollectionException(CollectionErrorCodes.StartFailed,
                    "Failed to start data collection", ex);
            }
        }

        /// <summary>
        /// Stop data collection;
        /// </summary>
        public async Task StopCollectionAsync()
        {
            ValidateNotDisposed();

            if (!_isCollecting)
                return;

            try
            {
                Status = CollectorStatus.Stopping;
                _logger.LogInformation("Stopping data collection", "DataCollector");

                // Stop all data sources;
                await StopAllDataSourcesAsync();

                // Stop timers;
                _collectionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _aggregationTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // Flush remaining data;
                await FlushBufferAsync();

                _isCollecting = false;
                Status = CollectorStatus.Stopped;

                _logger.LogInformation($"Data collection stopped. Total metrics: {TotalMetricsCollected}",
                    "DataCollector");
            }
            catch (Exception ex)
            {
                Status = CollectorStatus.Error;
                _logger.LogError($"Error stopping data collection: {ex.Message}", "DataCollector", ex);
                throw;
            }
        }

        /// <summary>
        /// Register a new data source for collection;
        /// </summary>
        public async Task RegisterDataSourceAsync(DataSourceRegistration registration,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (registration == null)
                throw new ArgumentNullException(nameof(registration));

            if (string.IsNullOrWhiteSpace(registration.SourceId))
                throw new ArgumentException("Source ID cannot be empty", nameof(registration.SourceId));

            await _collectionLock.WaitAsync(cancellationToken);
            try
            {
                if (_dataSources.ContainsKey(registration.SourceId))
                    throw new DataSourceAlreadyRegisteredException($"Data source '{registration.SourceId}' already registered");

                var source = new DataSource;
                {
                    SourceId = registration.SourceId,
                    Name = registration.Name,
                    SourceType = registration.SourceType,
                    CollectionMethod = registration.CollectionMethod,
                    CollectionInterval = registration.CollectionInterval ?? _settings.DefaultCollectionInterval,
                    Enabled = true,
                    CreatedTime = DateTime.UtcNow,
                    LastCollectionTime = DateTime.MinValue,
                    Configuration = registration.Configuration ?? new Dictionary<string, object>()
                };

                if (_dataSources.TryAdd(registration.SourceId, source))
                {
                    _logger.LogInformation($"Registered data source: {registration.Name} ({registration.SourceId})",
                        "DataCollector");

                    // Initialize source if collection is running;
                    if (_isCollecting)
                    {
                        await InitializeDataSourceAsync(source, cancellationToken);
                    }
                }
            }
            finally
            {
                _collectionLock.Release();
            }
        }

        /// <summary>
        /// Unregister a data source;
        /// </summary>
        public async Task<bool> UnregisterDataSourceAsync(string sourceId)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source ID cannot be empty", nameof(sourceId));

            await _collectionLock.WaitAsync();
            try
            {
                if (_dataSources.TryRemove(sourceId, out var source))
                {
                    // Cleanup source resources;
                    await CleanupDataSourceAsync(source);

                    _logger.LogInformation($"Unregistered data source: {source.Name} ({sourceId})", "DataCollector");
                    return true;
                }
                return false;
            }
            finally
            {
                _collectionLock.Release();
            }
        }

        /// <summary>
        /// Enable or disable a data source;
        /// </summary>
        public async Task SetDataSourceEnabledAsync(string sourceId, bool enabled)
        {
            ValidateNotDisposed();

            if (!_dataSources.TryGetValue(sourceId, out var source))
                throw new DataSourceNotFoundException($"Data source '{sourceId}' not found");

            await _collectionLock.WaitAsync();
            try
            {
                source.Enabled = enabled;
                source.LastModifiedTime = DateTime.UtcNow;

                _dataSources[sourceId] = source;

                _logger.LogInformation($"Data source '{source.Name}' {(enabled ? "enabled" : "disabled")}",
                    "DataCollector");
            }
            finally
            {
                _collectionLock.Release();
            }
        }

        /// <summary>
        /// Manually trigger data collection from specific source;
        /// </summary>
        public async Task<CollectionResult> CollectFromSourceAsync(string sourceId,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!_dataSources.TryGetValue(sourceId, out var source))
                throw new DataSourceNotFoundException($"Data source '{sourceId}' not found");

            if (!source.Enabled)
                throw new DataCollectionException(CollectionErrorCodes.SourceDisabled,
                    $"Data source '{sourceId}' is disabled");

            var result = new CollectionResult;
            {
                SourceId = sourceId,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                var metrics = await CollectDataFromSourceAsync(source, cancellationToken);

                result.MetricsCollected = metrics.Count;
                result.Success = true;
                result.EndTime = DateTime.UtcNow;
                result.Duration = (result.EndTime - result.StartTime).TotalMilliseconds;

                // Process and store metrics;
                await ProcessAndStoreMetricsAsync(metrics, cancellationToken);

                // Update source statistics;
                source.LastCollectionTime = DateTime.UtcNow;
                source.TotalCollections++;
                source.LastCollectionDuration = result.Duration;
                _dataSources[sourceId] = source;

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                result.EndTime = DateTime.UtcNow;

                source.ErrorCount++;
                source.LastError = ex.Message;
                source.LastErrorTime = DateTime.UtcNow;
                _dataSources[sourceId] = source;

                _logger.LogError($"Collection failed for source '{sourceId}': {ex.Message}",
                    "DataCollector", ex);

                throw new DataCollectionException(CollectionErrorCodes.CollectionFailed,
                    $"Collection failed for source '{sourceId}'", ex);
            }
        }

        /// <summary>
        /// Add custom metric data to collector;
        /// </summary>
        public async Task AddCustomMetricAsync(MetricData metric, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (metric == null)
                throw new ArgumentNullException(nameof(metric));

            if (string.IsNullOrWhiteSpace(metric.MetricName))
                throw new ArgumentException("Metric name cannot be empty", nameof(metric));

            try
            {
                // Apply data processors;
                var processedMetric = await ProcessMetricAsync(metric, cancellationToken);

                // Add to buffer;
                AddToBuffer(processedMetric);

                Interlocked.Increment(ref _totalMetricsCollected);
                Interlocked.Add(ref _totalBytesCollected, CalculateMetricSize(processedMetric));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to add custom metric: {ex.Message}", "DataCollector", ex);
                throw new DataCollectionException(CollectionErrorCodes.MetricAddFailed,
                    "Failed to add custom metric", ex);
            }
        }

        /// <summary>
        /// Add multiple custom metrics in batch;
        /// </summary>
        public async Task AddCustomMetricsBatchAsync(IEnumerable<MetricData> metrics,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (metrics == null)
                throw new ArgumentNullException(nameof(metrics));

            var batch = metrics.ToList();
            if (!batch.Any())
                return;

            try
            {
                var processedMetrics = new List<MetricData>();

                foreach (var metric in batch)
                {
                    var processed = await ProcessMetricAsync(metric, cancellationToken);
                    processedMetrics.Add(processed);
                }

                AddBatchToBuffer(processedMetrics);

                Interlocked.Add(ref _totalMetricsCollected, batch.Count);
                Interlocked.Add(ref _totalBytesCollected, processedMetrics.Sum(CalculateMetricSize));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to add metrics batch: {ex.Message}", "DataCollector", ex);
                throw new DataCollectionException(CollectionErrorCodes.BatchAddFailed,
                    "Failed to add metrics batch", ex);
            }
        }

        /// <summary>
        /// Get current metrics from buffer;
        /// </summary>
        public List<MetricData> GetCurrentMetrics(int maxCount = 1000)
        {
            ValidateNotDisposed();

            return _metricBuffer.Take(maxCount).ToList();
        }

        /// <summary>
        /// Get aggregated metric data;
        /// </summary>
        public Dictionary<string, MetricAggregate> GetAggregatedMetrics()
        {
            ValidateNotDisposed();

            return _aggregates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Get specific metric aggregate;
        /// </summary>
        public MetricAggregate GetMetricAggregate(string metricName)
        {
            ValidateNotDisposed();

            if (_aggregates.TryGetValue(metricName, out var aggregate))
                return aggregate;

            return null;
        }

        /// <summary>
        /// Get data source information;
        /// </summary>
        public IReadOnlyCollection<DataSource> GetDataSources()
        {
            ValidateNotDisposed();

            return _dataSources.Values.ToList();
        }

        /// <summary>
        /// Get data source statistics;
        /// </summary>
        public DataSourceStatistics GetSourceStatistics(string sourceId)
        {
            ValidateNotDisposed();

            if (!_dataSources.TryGetValue(sourceId, out var source))
                throw new DataSourceNotFoundException($"Data source '{sourceId}' not found");

            return new DataSourceStatistics;
            {
                SourceId = source.SourceId,
                Name = source.Name,
                Enabled = source.Enabled,
                TotalCollections = source.TotalCollections,
                ErrorCount = source.ErrorCount,
                LastCollectionTime = source.LastCollectionTime,
                LastErrorTime = source.LastErrorTime,
                AverageCollectionDuration = source.TotalCollections > 0 ?
                    source.TotalCollectionDuration / source.TotalCollections : 0,
                CreatedTime = source.CreatedTime;
            };
        }

        /// <summary>
        /// Get collector statistics;
        /// </summary>
        public CollectorStatistics GetCollectorStatistics()
        {
            ValidateNotDisposed();

            return new CollectorStatistics;
            {
                Status = Status,
                IsCollecting = _isCollecting,
                Uptime = _isCollecting ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
                TotalMetricsCollected = TotalMetricsCollected,
                TotalBytesCollected = TotalBytesCollected,
                CurrentBufferSize = CurrentBufferSize,
                ActiveSourcesCount = ActiveSourcesCount,
                DataProcessorsCount = _dataProcessors.Count,
                MetricSinksCount = _metricSinks.Count,
                MemoryUsageMB = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)
            };
        }

        /// <summary>
        /// Register a data processor for metric processing;
        /// </summary>
        public void RegisterDataProcessor(IDataProcessor processor)
        {
            ValidateNotDisposed();

            if (processor == null)
                throw new ArgumentNullException(nameof(processor));

            _dataProcessors.Add(processor);
            _logger.LogInformation($"Registered data processor: {processor.GetType().Name}", "DataCollector");
        }

        /// <summary>
        /// Register a metric sink for data output;
        /// </summary>
        public void RegisterMetricSink(IMetricSink sink)
        {
            ValidateNotDisposed();

            if (sink == null)
                throw new ArgumentNullException(nameof(sink));

            _metricSinks.Add(sink);
            _logger.LogInformation($"Registered metric sink: {sink.GetType().Name}", "DataCollector");
        }

        /// <summary>
        /// Clear metric buffer;
        /// </summary>
        public void ClearBuffer()
        {
            ValidateNotDisposed();

            lock (_bufferLock)
            {
                while (_metricBuffer.TryDequeue(out _)) { }
                _logger.LogInformation("Metric buffer cleared", "DataCollector");
            }
        }

        /// <summary>
        /// Clear metric aggregates;
        /// </summary>
        public void ClearAggregates()
        {
            ValidateNotDisposed();

            _aggregates.Clear();
            _logger.LogInformation("Metric aggregates cleared", "DataCollector");
        }

        /// <summary>
        /// Flush buffer to sinks;
        /// </summary>
        public async Task<int> FlushBufferAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!_metricSinks.Any())
                return 0;

            var metricsToFlush = new List<MetricData>();
            int flushedCount = 0;

            lock (_bufferLock)
            {
                while (_metricBuffer.TryDequeue(out var metric))
                {
                    metricsToFlush.Add(metric);
                }
            }

            if (!metricsToFlush.Any())
                return 0;

            try
            {
                // Send to all registered sinks;
                var sinkTasks = _metricSinks.Select(sink =>
                    sink.WriteMetricsAsync(metricsToFlush, cancellationToken)).ToList();

                await Task.WhenAll(sinkTasks);
                flushedCount = metricsToFlush.Count;

                _logger.LogDebug($"Flushed {flushedCount} metrics to {_metricSinks.Count} sinks", "DataCollector");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to flush buffer: {ex.Message}", "DataCollector", ex);
                throw new DataCollectionException(CollectionErrorCodes.FlushFailed, "Failed to flush buffer", ex);
            }

            return flushedCount;
        }

        /// <summary>
        /// Get metrics by name pattern;
        /// </summary>
        public List<MetricData> QueryMetrics(string namePattern, DateTime? startTime = null,
            DateTime? endTime = null, int limit = 1000)
        {
            ValidateNotDisposed();

            var query = _metricBuffer.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(namePattern))
            {
                query = query.Where(m => m.MetricName.Contains(namePattern));
            }

            if (startTime.HasValue)
            {
                query = query.Where(m => m.Timestamp >= startTime.Value);
            }

            if (endTime.HasValue)
            {
                query = query.Where(m => m.Timestamp <= endTime.Value);
            }

            return query.Take(limit).ToList();
        }

        #region Private Methods;

        private void InitializeBuiltInSources()
        {
            // Initialize built-in system data sources;
            var builtInSources = new[]
            {
                new DataSourceRegistration;
                {
                    SourceId = "system.cpu",
                    Name = "CPU Metrics",
                    SourceType = DataSourceType.System,
                    CollectionMethod = CollectionMethod.PerformanceCounter,
                    CollectionInterval = TimeSpan.FromSeconds(30)
                },
                new DataSourceRegistration;
                {
                    SourceId = "system.memory",
                    Name = "Memory Metrics",
                    SourceType = DataSourceType.System,
                    CollectionMethod = CollectionMethod.PerformanceCounter,
                    CollectionInterval = TimeSpan.FromSeconds(30)
                },
                new DataSourceRegistration;
                {
                    SourceId = "system.disk",
                    Name = "Disk Metrics",
                    SourceType = DataSourceType.System,
                    CollectionMethod = CollectionMethod.PerformanceCounter,
                    CollectionInterval = TimeSpan.FromSeconds(60)
                },
                new DataSourceRegistration;
                {
                    SourceId = "system.network",
                    Name = "Network Metrics",
                    SourceType = DataSourceType.System,
                    CollectionMethod = CollectionMethod.PerformanceCounter,
                    CollectionInterval = TimeSpan.FromSeconds(60)
                },
                new DataSourceRegistration;
                {
                    SourceId = "application.performance",
                    Name = "Application Performance",
                    SourceType = DataSourceType.Application,
                    CollectionMethod = CollectionMethod.Instrumentation,
                    CollectionInterval = TimeSpan.FromSeconds(10)
                }
            };

            foreach (var registration in builtInSources)
            {
                var source = new DataSource;
                {
                    SourceId = registration.SourceId,
                    Name = registration.Name,
                    SourceType = registration.SourceType,
                    CollectionMethod = registration.CollectionMethod,
                    CollectionInterval = registration.CollectionInterval ?? _settings.DefaultCollectionInterval,
                    Enabled = true,
                    CreatedTime = DateTime.UtcNow,
                    Configuration = new Dictionary<string, object>()
                };

                _dataSources.TryAdd(registration.SourceId, source);
            }
        }

        private void ConfigureTimers()
        {
            _collectionTimer?.Dispose();
            _aggregationTimer?.Dispose();
            _flushTimer?.Dispose();

            if (_settings.Enabled)
            {
                _collectionTimer = new Timer(async _ => await CollectFromAllSourcesAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);

                _aggregationTimer = new Timer(async _ => await AggregateMetricsAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);

                _flushTimer = new Timer(async _ => await FlushBufferAsync(), null,
                    Timeout.Infinite, Timeout.Infinite);
            }
        }

        private async Task StartAllDataSourcesAsync(CancellationToken cancellationToken)
        {
            var tasks = _dataSources.Values;
                .Where(s => s.Enabled)
                .Select(s => InitializeDataSourceAsync(s, cancellationToken));

            await Task.WhenAll(tasks);
        }

        private async Task StopAllDataSourcesAsync()
        {
            var tasks = _dataSources.Values.Select(CleanupDataSourceAsync);
            await Task.WhenAll(tasks);
        }

        private async Task InitializeDataSourceAsync(DataSource source, CancellationToken cancellationToken)
        {
            try
            {
                // Source-specific initialization;
                switch (source.SourceType)
                {
                    case DataSourceType.System:
                        await InitializeSystemSourceAsync(source, cancellationToken);
                        break;
                    case DataSourceType.Application:
                        await InitializeApplicationSourceAsync(source, cancellationToken);
                        break;
                    case DataSourceType.Database:
                        await InitializeDatabaseSourceAsync(source, cancellationToken);
                        break;
                    case DataSourceType.External:
                        await InitializeExternalSourceAsync(source, cancellationToken);
                        break;
                }

                source.Initialized = true;
                _dataSources[source.SourceId] = source;

                _logger.LogDebug($"Initialized data source: {source.Name}", "DataCollector");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize data source '{source.SourceId}': {ex.Message}",
                    "DataCollector", ex);
                source.Initialized = false;
                source.LastError = ex.Message;
            }
        }

        private async Task CleanupDataSourceAsync(DataSource source)
        {
            try
            {
                // Source-specific cleanup;
                if (source.SourceType == DataSourceType.External &&
                    source.Configuration.ContainsKey("connection"))
                {
                    // Close external connections;
                    await Task.CompletedTask;
                }

                source.Initialized = false;
                _logger.LogDebug($"Cleaned up data source: {source.Name}", "DataCollector");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error cleaning up data source '{source.SourceId}': {ex.Message}",
                    "DataCollector", ex);
            }
        }

        private Task InitializeSystemSourceAsync(DataSource source, CancellationToken cancellationToken)
        {
            // System sources use performance counters - no special initialization needed;
            return Task.CompletedTask;
        }

        private Task InitializeApplicationSourceAsync(DataSource source, CancellationToken cancellationToken)
        {
            // Application sources might need instrumentation setup;
            return Task.CompletedTask;
        }

        private Task InitializeDatabaseSourceAsync(DataSource source, CancellationToken cancellationToken)
        {
            // Database sources might need connection setup;
            return Task.CompletedTask;
        }

        private Task InitializeExternalSourceAsync(DataSource source, CancellationToken cancellationToken)
        {
            // External sources might need API client initialization;
            return Task.CompletedTask;
        }

        private async Task CollectFromAllSourcesAsync()
        {
            if (!_collectionLock.Wait(0))
                return; // Collection already in progress;

            try
            {
                var now = DateTime.UtcNow;
                var collectionTasks = new List<Task<CollectionResult>>();

                foreach (var source in _dataSources.Values)
                {
                    if (!source.Enabled || !source.Initialized)
                        continue;

                    // Check if it's time to collect from this source;
                    if (now - source.LastCollectionTime >= source.CollectionInterval)
                    {
                        collectionTasks.Add(CollectFromSourceAsync(source.SourceId, CancellationToken.None));
                    }
                }

                if (collectionTasks.Any())
                {
                    var results = await Task.WhenAll(collectionTasks);

                    // Log collection summary;
                    var successful = results.Count(r => r.Success);
                    var failed = results.Count(r => !r.Success);

                    if (failed > 0)
                    {
                        _logger.LogWarning($"Collection completed: {successful} successful, {failed} failed",
                            "DataCollector");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during scheduled collection: {ex.Message}", "DataCollector", ex);
            }
            finally
            {
                _collectionLock.Release();
            }
        }

        private async Task<List<MetricData>> CollectDataFromSourceAsync(DataSource source,
            CancellationToken cancellationToken)
        {
            var metrics = new List<MetricData>();
            var timestamp = DateTime.UtcNow;

            try
            {
                switch (source.SourceType)
                {
                    case DataSourceType.System:
                        metrics.AddRange(await CollectSystemMetricsAsync(source, timestamp, cancellationToken));
                        break;
                    case DataSourceType.Application:
                        metrics.AddRange(await CollectApplicationMetricsAsync(source, timestamp, cancellationToken));
                        break;
                    case DataSourceType.Database:
                        metrics.AddRange(await CollectDatabaseMetricsAsync(source, timestamp, cancellationToken));
                        break;
                    case DataSourceType.External:
                        metrics.AddRange(await CollectExternalMetricsAsync(source, timestamp, cancellationToken));
                        break;
                    case DataSourceType.Custom:
                        metrics.AddRange(await CollectCustomMetricsAsync(source, timestamp, cancellationToken));
                        break;
                }

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to collect data from source '{source.SourceId}': {ex.Message}",
                    "DataCollector", ex);

                // Add error metric;
                metrics.Add(new MetricData;
                {
                    MetricName = $"{source.SourceId}.collection_error",
                    Value = 1,
                    Timestamp = timestamp,
                    Tags = new Dictionary<string, string>
                    {
                        ["source"] = source.SourceId,
                        ["error"] = ex.GetType().Name,
                        ["message"] = ex.Message;
                    }
                });

                return metrics;
            }
        }

        private async Task<List<MetricData>> CollectSystemMetricsAsync(DataSource source, DateTime timestamp,
            CancellationToken cancellationToken)
        {
            var metrics = new List<MetricData>();

            switch (source.SourceId)
            {
                case "system.cpu":
                    var cpuUsage = await _performanceMonitor.GetSystemCpuUsageAsync(cancellationToken);
                    metrics.Add(CreateMetric("system.cpu.usage", cpuUsage, timestamp,
                        new Dictionary<string, string> { ["type"] = "system" }));
                    break;

                case "system.memory":
                    var memoryInfo = await _performanceMonitor.GetMemoryInfoAsync(cancellationToken);
                    var memoryUsage = (memoryInfo.Total - memoryInfo.Available) * 100.0 / memoryInfo.Total;

                    metrics.Add(CreateMetric("system.memory.usage", memoryUsage, timestamp));
                    metrics.Add(CreateMetric("system.memory.available_mb", memoryInfo.Available / 1024 / 1024, timestamp));
                    metrics.Add(CreateMetric("system.memory.total_mb", memoryInfo.Total / 1024 / 1024, timestamp));
                    break;

                case "system.disk":
                    var diskInfo = await _performanceMonitor.GetDiskInfoAsync("C:", cancellationToken);

                    metrics.Add(CreateMetric("system.disk.usage", diskInfo.UsagePercentage, timestamp,
                        new Dictionary<string, string> { ["drive"] = "C:" }));
                    metrics.Add(CreateMetric("system.disk.free_gb", diskInfo.FreeSpace / 1024 / 1024 / 1024, timestamp,
                        new Dictionary<string, string> { ["drive"] = "C:" }));
                    break;

                case "system.network":
                    var networkInfo = await _performanceMonitor.GetNetworkInfoAsync(cancellationToken);

                    metrics.Add(CreateMetric("system.network.latency", networkInfo.Latency, timestamp));
                    metrics.Add(CreateMetric("system.network.packet_loss", networkInfo.PacketLoss, timestamp));
                    break;
            }

            return metrics;
        }

        private async Task<List<MetricData>> CollectApplicationMetricsAsync(DataSource source, DateTime timestamp,
            CancellationToken cancellationToken)
        {
            var metrics = new List<MetricData>();

            // Collect application performance metrics;
            using var process = Process.GetCurrentProcess();

            metrics.Add(CreateMetric("application.memory.working_set_mb",
                process.WorkingSet64 / (1024 * 1024), timestamp));
            metrics.Add(CreateMetric("application.threads",
                process.Threads.Count, timestamp));
            metrics.Add(CreateMetric("application.handles",
                process.HandleCount, timestamp));

            // GC metrics;
            metrics.Add(CreateMetric("application.gc.collection_count_0",
                GC.CollectionCount(0), timestamp));
            metrics.Add(CreateMetric("application.gc.collection_count_1",
                GC.CollectionCount(1), timestamp));
            metrics.Add(CreateMetric("application.gc.collection_count_2",
                GC.CollectionCount(2), timestamp));

            // Collector buffer metrics;
            metrics.Add(CreateMetric("collector.buffer.size", CurrentBufferSize, timestamp));
            metrics.Add(CreateMetric("collector.metrics.total", TotalMetricsCollected, timestamp));
            metrics.Add(CreateMetric("collector.sources.active", ActiveSourcesCount, timestamp));

            return await Task.FromResult(metrics);
        }

        private Task<List<MetricData>> CollectDatabaseMetricsAsync(DataSource source, DateTime timestamp,
            CancellationToken cancellationToken)
        {
            // Database metrics collection - implemented by database-specific modules;
            var metrics = new List<MetricData>
            {
                CreateMetric("database.connections.active", 5, timestamp, // Simulated;
                    new Dictionary<string, string> { ["source"] = source.SourceId }),
                CreateMetric("database.queries.per_second", 120.5, timestamp,
                    new Dictionary<string, string> { ["source"] = source.SourceId })
            };

            return Task.FromResult(metrics);
        }

        private Task<List<MetricData>> CollectExternalMetricsAsync(DataSource source, DateTime timestamp,
            CancellationToken cancellationToken)
        {
            // External API metrics - implemented by integration modules;
            var metrics = new List<MetricData>
            {
                CreateMetric("external.api.response_time", 45.2, timestamp,
                    new Dictionary<string, string> { ["source"] = source.SourceId }),
                CreateMetric("external.api.success_rate", 99.8, timestamp,
                    new Dictionary<string, string> { ["source"] = source.SourceId })
            };

            return Task.FromResult(metrics);
        }

        private Task<List<MetricData>> CollectCustomMetricsAsync(DataSource source, DateTime timestamp,
            CancellationToken cancellationToken)
        {
            // Custom metrics collection logic would be implemented based on configuration;
            var metrics = new List<MetricData>();
            return Task.FromResult(metrics);
        }

        private MetricData CreateMetric(string name, double value, DateTime timestamp,
            Dictionary<string, string> tags = null)
        {
            return new MetricData;
            {
                MetricName = name,
                Value = value,
                Timestamp = timestamp,
                Tags = tags ?? new Dictionary<string, string>(),
                Source = "DataCollector"
            };
        }

        private async Task ProcessAndStoreMetricsAsync(List<MetricData> metrics, CancellationToken cancellationToken)
        {
            if (!metrics.Any())
                return;

            var processedMetrics = new List<MetricData>();

            // Process each metric through registered processors;
            foreach (var metric in metrics)
            {
                var processed = await ProcessMetricAsync(metric, cancellationToken);
                processedMetrics.Add(processed);
            }

            // Add to buffer;
            AddBatchToBuffer(processedMetrics);

            // Update statistics;
            Interlocked.Add(ref _totalMetricsCollected, metrics.Count);
            Interlocked.Add(ref _totalBytesCollected, processedMetrics.Sum(CalculateMetricSize));

            // Trigger event;
            OnMetricsCollected?.Invoke(this, new MetricsCollectedEventArgs;
            {
                Metrics = processedMetrics,
                Timestamp = DateTime.UtcNow,
                SourceCount = 1;
            });

            // Check buffer warning;
            CheckBufferWarning();
        }

        private async Task<MetricData> ProcessMetricAsync(MetricData metric, CancellationToken cancellationToken)
        {
            var processedMetric = metric;

            foreach (var processor in _dataProcessors)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    processedMetric = await processor.ProcessAsync(processedMetric, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Processor '{processor.GetType().Name}' failed: {ex.Message}",
                        "DataCollector");
                    // Continue with other processors;
                }
            }

            return processedMetric;
        }

        private void AddToBuffer(MetricData metric)
        {
            lock (_bufferLock)
            {
                _metricBuffer.Enqueue(metric);

                // Check if buffer is getting full;
                if (_metricBuffer.Count > _settings.BufferWarningThreshold)
                {
                    OnBufferWarning?.Invoke(this, new BufferWarningEventArgs;
                    {
                        CurrentSize = _metricBuffer.Count,
                        WarningThreshold = _settings.BufferWarningThreshold,
                        MaxCapacity = _settings.BufferMaxCapacity,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Enforce maximum buffer size;
                while (_metricBuffer.Count > _settings.BufferMaxCapacity)
                {
                    _metricBuffer.TryDequeue(out _);
                }
            }
        }

        private void AddBatchToBuffer(List<MetricData> metrics)
        {
            lock (_bufferLock)
            {
                foreach (var metric in metrics)
                {
                    _metricBuffer.Enqueue(metric);
                }

                // Check buffer limits;
                if (_metricBuffer.Count > _settings.BufferWarningThreshold)
                {
                    OnBufferWarning?.Invoke(this, new BufferWarningEventArgs;
                    {
                        CurrentSize = _metricBuffer.Count,
                        WarningThreshold = _settings.BufferWarningThreshold,
                        MaxCapacity = _settings.BufferMaxCapacity,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Trim if necessary;
                while (_metricBuffer.Count > _settings.BufferMaxCapacity)
                {
                    _metricBuffer.TryDequeue(out _);
                }
            }
        }

        private async Task AggregateMetricsAsync()
        {
            try
            {
                var metrics = GetCurrentMetrics(10000); // Limit for aggregation;
                if (!metrics.Any())
                    return;

                var grouped = metrics.GroupBy(m => m.MetricName);

                foreach (var group in grouped)
                {
                    var metricName = group.Key;
                    var values = group.Select(m => m.Value).ToList();

                    var aggregate = new MetricAggregate;
                    {
                        MetricName = metricName,
                        Count = values.Count,
                        Sum = values.Sum(),
                        Average = values.Average(),
                        Min = values.Min(),
                        Max = values.Max(),
                        Timestamp = DateTime.UtcNow;
                    };

                    // Calculate standard deviation;
                    if (values.Count > 1)
                    {
                        var variance = values.Sum(v => Math.Pow(v - aggregate.Average, 2)) / (values.Count - 1);
                        aggregate.StandardDeviation = Math.Sqrt(variance);
                    }

                    _aggregates[metricName] = aggregate;
                }

                _logger.LogDebug($"Aggregated {grouped.Count()} metrics", "DataCollector");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metric aggregation failed: {ex.Message}", "DataCollector", ex);
            }
        }

        private void CheckBufferWarning()
        {
            var bufferSize = CurrentBufferSize;
            var warningThreshold = _settings.BufferWarningThreshold;

            if (bufferSize > warningThreshold && bufferSize % 1000 == 0) // Log every 1000 metrics over threshold;
            {
                _logger.LogWarning($"Buffer size warning: {bufferSize}/{_settings.BufferMaxCapacity}",
                    "DataCollector");
            }
        }

        private long CalculateMetricSize(MetricData metric)
        {
            // Approximate size calculation;
            long size = 0;

            size += metric.MetricName?.Length * 2 ?? 0;
            size += 8; // double value;
            size += 8; // timestamp;
            size += metric.Source?.Length * 2 ?? 0;

            if (metric.Tags != null)
            {
                foreach (var tag in metric.Tags)
                {
                    size += (tag.Key?.Length ?? 0) * 2;
                    size += (tag.Value?.Length ?? 0) * 2;
                }
            }

            return size;
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(DataCollector));
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _collectionTimer?.Dispose();
                    _aggregationTimer?.Dispose();
                    _flushTimer?.Dispose();
                    _collectionLock?.Dispose();

                    // Stop collection if running;
                    if (_isCollecting)
                    {
                        StopCollectionAsync().GetAwaiter().GetResult();
                    }

                    // Clear collections;
                    _dataSources.Clear();
                    ClearBuffer();
                    _aggregates.Clear();
                    _dataProcessors.Clear();
                    _metricSinks.Clear();

                    Status = CollectorStatus.Disposed;
                    _logger.LogInformation("DataCollector disposed", "DataCollector");
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
    /// Data collector status;
    /// </summary>
    public enum CollectorStatus;
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Error,
        Disposed;
    }

    /// <summary>
    /// Data source type;
    /// </summary>
    public enum DataSourceType;
    {
        System,
        Application,
        Database,
        External,
        Custom;
    }

    /// <summary>
    /// Data collection method;
    /// </summary>
    public enum CollectionMethod;
    {
        PerformanceCounter,
        Instrumentation,
        Polling,
        EventBased,
        Custom;
    }

    /// <summary>
    /// Data collector settings;
    /// </summary>
    public class DataCollectorSettings;
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan CollectionInterval { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan AggregationInterval { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan DefaultCollectionInterval { get; set; } = TimeSpan.FromSeconds(60);
        public int BufferMaxCapacity { get; set; } = 100000;
        public int BufferWarningThreshold { get; set; } = 75000;
        public bool EnableAggregation { get; set; } = true;
        public bool EnableBuffering { get; set; } = true;
        public int MaxRetryAttempts { get; set; } = 3;
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

        public static DataCollectorSettings Default => new DataCollectorSettings();
    }

    /// <summary>
    /// Data source registration information;
    /// </summary>
    public class DataSourceRegistration;
    {
        public string SourceId { get; set; }
        public string Name { get; set; }
        public DataSourceType SourceType { get; set; }
        public CollectionMethod CollectionMethod { get; set; }
        public TimeSpan? CollectionInterval { get; set; }
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
        public List<string> Dependencies { get; set; } = new List<string>();
    }

    /// <summary>
    /// Data source information;
    /// </summary>
    public class DataSource;
    {
        public string SourceId { get; set; }
        public string Name { get; set; }
        public DataSourceType SourceType { get; set; }
        public CollectionMethod CollectionMethod { get; set; }
        public TimeSpan CollectionInterval { get; set; }
        public bool Enabled { get; set; }
        public bool Initialized { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastModifiedTime { get; set; }
        public DateTime LastCollectionTime { get; set; }
        public long TotalCollections { get; set; }
        public double TotalCollectionDuration { get; set; }
        public double LastCollectionDuration { get; set; }
        public int ErrorCount { get; set; }
        public DateTime LastErrorTime { get; set; }
        public string LastError { get; set; }
        public Dictionary<string, object> Configuration { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Metric data;
    /// </summary>
    public class MetricData;
    {
        public string MetricName { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public string Source { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Metric aggregate;
    /// </summary>
    public class MetricAggregate;
    {
        public string MetricName { get; set; }
        public int Count { get; set; }
        public double Sum { get; set; }
        public double Average { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double StandardDeviation { get; set; }
        public DateTime Timestamp { get; set; }

        public override string ToString()
        {
            return $"{MetricName}: Count={Count}, Avg={Average:F2}, Min={Min:F2}, Max={Max:F2}";
        }
    }

    /// <summary>
    /// Collection result;
    /// </summary>
    public class CollectionResult;
    {
        public string SourceId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double Duration { get; set; }
        public int MetricsCollected { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Data source statistics;
    /// </summary>
    public class DataSourceStatistics;
    {
        public string SourceId { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public long TotalCollections { get; set; }
        public int ErrorCount { get; set; }
        public DateTime LastCollectionTime { get; set; }
        public DateTime LastErrorTime { get; set; }
        public double AverageCollectionDuration { get; set; }
        public DateTime CreatedTime { get; set; }
    }

    /// <summary>
    /// Collector statistics;
    /// </summary>
    public class CollectorStatistics;
    {
        public CollectorStatus Status { get; set; }
        public bool IsCollecting { get; set; }
        public TimeSpan Uptime { get; set; }
        public long TotalMetricsCollected { get; set; }
        public long TotalBytesCollected { get; set; }
        public int CurrentBufferSize { get; set; }
        public int ActiveSourcesCount { get; set; }
        public int DataProcessorsCount { get; set; }
        public int MetricSinksCount { get; set; }
        public long MemoryUsageMB { get; set; }
    }

    /// <summary>
    /// Metrics collected event arguments;
    /// </summary>
    public class MetricsCollectedEventArgs : EventArgs;
    {
        public List<MetricData> Metrics { get; set; }
        public DateTime Timestamp { get; set; }
        public int SourceCount { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>
    /// Data collection error event arguments;
    /// </summary>
    public class DataCollectionErrorEventArgs : EventArgs;
    {
        public string SourceId { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Buffer warning event arguments;
    /// </summary>
    public class BufferWarningEventArgs : EventArgs;
    {
        public int CurrentSize { get; set; }
        public int WarningThreshold { get; set; }
        public int MaxCapacity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Data processor interface;
    /// </summary>
    public interface IDataProcessor;
    {
        Task<MetricData> ProcessAsync(MetricData metric, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Metric sink interface;
    /// </summary>
    public interface IMetricSink;
    {
        Task WriteMetricsAsync(IEnumerable<MetricData> metrics, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Data collection specific exception;
    /// </summary>
    public class DataCollectionException : Exception
    {
        public string ErrorCode { get; }

        public DataCollectionException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public DataCollectionException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Data source not found exception;
    /// </summary>
    public class DataSourceNotFoundException : Exception
    {
        public DataSourceNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Data source already registered exception;
    /// </summary>
    public class DataSourceAlreadyRegisteredException : Exception
    {
        public DataSourceAlreadyRegisteredException(string message) : base(message) { }
    }

    /// <summary>
    /// Collection error codes;
    /// </summary>
    public static class CollectionErrorCodes;
    {
        public const string StartFailed = "COLLECT_001";
        public const string CollectionFailed = "COLLECT_002";
        public const string SourceDisabled = "COLLECT_003";
        public const string MetricAddFailed = "COLLECT_004";
        public const string BatchAddFailed = "COLLECT_005";
        public const string FlushFailed = "COLLECT_006";
        public const string BufferOverflow = "COLLECT_007";
        public const string AggregationFailed = "COLLECT_008";
    }

    #endregion;
}
