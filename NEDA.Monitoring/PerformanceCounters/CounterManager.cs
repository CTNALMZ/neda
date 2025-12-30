using CsvHelper;
using NEDA.Core.Common;
using NEDA.Core.Common.Extensions;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Monitoring.Logging;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Monitoring.PerformanceCounters;
{
    /// <summary>
    /// Advanced Performance Counter Manager for comprehensive system performance monitoring;
    /// Provides Windows/Linux performance counter abstraction with high-performance collection;
    /// </summary>
    public sealed class CounterManager : ICounterManager, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly CounterManagerConfiguration _configuration;

        private readonly ConcurrentDictionary<string, PerformanceCounter> _counters;
        private readonly ConcurrentDictionary<string, CounterCategory> _categories;
        private readonly ConcurrentDictionary<string, CounterData> _counterData;
        private readonly ConcurrentDictionary<string, List<CounterSample>> _counterHistory;
        private readonly object _syncLock = new object();

        private Timer _collectionTimer;
        private Timer _cleanupTimer;
        private bool _isInitialized;
        private bool _isMonitoringActive;
        private CancellationTokenSource _monitoringCts;
        private Task _collectionTask;

        private static readonly Lazy<CounterManager> _instance =
            new Lazy<CounterManager>(() => new CounterManager());

        #endregion;

        #region Constructors;

        /// <summary>
        /// Private constructor for singleton pattern;
        /// </summary>
        private CounterManager()
        {
            _counters = new ConcurrentDictionary<string, PerformanceCounter>();
            _categories = new ConcurrentDictionary<string, CounterCategory>();
            _counterData = new ConcurrentDictionary<string, CounterData>();
            _counterHistory = new ConcurrentDictionary<string, List<CounterSample>>();

            _configuration = CounterManagerConfiguration.Default;
            _isInitialized = false;
            _isMonitoringActive = false;

            // Get dependencies;
            _logger = LogManager.GetLogger("CounterManager");
            _eventBus = EventBus.Instance;
        }

        /// <summary>
        /// Constructor with dependency injection support;
        /// </summary>
        public CounterManager(
            ILogger logger,
            IEventBus eventBus,
            CounterManagerConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _configuration = configuration ?? CounterManagerConfiguration.Default;

            _counters = new ConcurrentDictionary<string, PerformanceCounter>();
            _categories = new ConcurrentDictionary<string, CounterCategory>();
            _counterData = new ConcurrentDictionary<string, CounterData>();
            _counterHistory = new ConcurrentDictionary<string, List<CounterSample>>();

            _isInitialized = false;
            _isMonitoringActive = false;

            Initialize();
        }

        #endregion;

        #region Properties;

        /// <summary>
        /// Singleton instance for global access;
        /// </summary>
        public static CounterManager Instance => _instance.Value;

        /// <summary>
        /// Gets the number of active counters;
        /// </summary>
        public int ActiveCounterCount => _counters.Count;

        /// <summary>
        /// Gets the number of counter categories;
        /// </summary>
        public int CategoryCount => _categories.Count;

        /// <summary>
        /// Indicates if counter monitoring is active;
        /// </summary>
        public bool IsMonitoringActive => _isMonitoringActive;

        /// <summary>
        /// Gets the configuration;
        /// </summary>
        public CounterManagerConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets the last collection time;
        /// </summary>
        public DateTime? LastCollectionTime { get; private set; }

        /// <summary>
        /// Gets the total samples collected;
        /// </summary>
        public long TotalSamplesCollected { get; private set; }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the counter manager with default counters;
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
                    // Initialize platform-specific counter system;
                    InitializePlatformCounters();

                    // Register default counters;
                    RegisterDefaultCounters();

                    // Start monitoring if enabled;
                    if (_configuration.AutoStartMonitoring)
                    {
                        StartMonitoring();
                    }

                    _isInitialized = true;
                    _logger.Info("CounterManager initialized successfully");
                }
                catch (Exception ex)
                {
                    _logger.Error("Failed to initialize CounterManager", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Registers a performance counter for monitoring;
        /// </summary>
        /// <param name="counterInfo">Counter information</param>
        /// <returns>True if registration successful</returns>
        public bool RegisterCounter(CounterInfo counterInfo)
        {
            Validate.NotNull(counterInfo, nameof(counterInfo));
            Validate.NotNullOrEmpty(counterInfo.Name, nameof(counterInfo.Name));
            Validate.NotNullOrEmpty(counterInfo.CategoryName, nameof(counterInfo.CategoryName));

            try
            {
                PerformanceCounter counter;

                // Create platform-specific counter;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    counter = CreateWindowsCounter(counterInfo);
                }
                else;
                {
                    counter = CreateCrossPlatformCounter(counterInfo);
                }

                if (counter == null)
                {
                    _logger.Warn($"Failed to create counter: {counterInfo.Name}");
                    return false;
                }

                // Register the counter;
                var counterKey = GetCounterKey(counterInfo.CategoryName, counterInfo.Name, counterInfo.InstanceName);

                if (!_counters.TryAdd(counterKey, counter))
                {
                    _logger.Warn($"Counter already registered: {counterKey}");
                    return false;
                }

                // Initialize counter data;
                var counterData = new CounterData(counterInfo)
                {
                    LastValue = 0,
                    LastSampleTime = DateTime.UtcNow;
                };

                _counterData[counterKey] = counterData;
                _counterHistory[counterKey] = new List<CounterSample>();

                // Ensure category exists;
                EnsureCategoryExists(counterInfo.CategoryName, counterInfo.CategoryHelp);

                _logger.Info($"Counter registered: {counterKey} (Type: {counterInfo.CounterType})");

                // Publish counter registered event;
                PublishCounterRegisteredEvent(counterInfo);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to register counter {counterInfo.Name}", ex);
                return false;
            }
        }

        /// <summary>
        /// Unregisters a performance counter;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <returns>True if unregistration successful</returns>
        public bool UnregisterCounter(string categoryName, string counterName, string instanceName = null)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (_counters.TryRemove(counterKey, out var counter))
            {
                // Dispose counter resources;
                counter?.Dispose();

                // Remove associated data;
                _counterData.TryRemove(counterKey, out _);
                _counterHistory.TryRemove(counterKey, out _);

                _logger.Info($"Counter unregistered: {counterKey}");

                // Publish counter unregistered event;
                PublishCounterUnregisteredEvent(categoryName, counterName, instanceName);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a custom counter category;
        /// </summary>
        /// <param name="categoryInfo">Category information</param>
        /// <returns>True if creation successful</returns>
        public bool CreateCounterCategory(CounterCategoryInfo categoryInfo)
        {
            Validate.NotNull(categoryInfo, nameof(categoryInfo));
            Validate.NotNullOrEmpty(categoryInfo.Name, nameof(categoryInfo.Name));

            try
            {
                if (_categories.ContainsKey(categoryInfo.Name))
                {
                    _logger.Warn($"Counter category already exists: {categoryInfo.Name}");
                    return false;
                }

                var category = new CounterCategory(categoryInfo);

                // Platform-specific category creation;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    CreateWindowsCounterCategory(categoryInfo);
                }
                else;
                {
                    CreateCrossPlatformCounterCategory(categoryInfo);
                }

                _categories[categoryInfo.Name] = category;

                _logger.Info($"Counter category created: {categoryInfo.Name}");

                // Publish category created event;
                PublishCategoryCreatedEvent(categoryInfo);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create counter category {categoryInfo.Name}", ex);
                return false;
            }
        }

        /// <summary>
        /// Deletes a counter category;
        /// </summary>
        /// <param name="categoryName">Category name to delete</param>
        /// <returns>True if deletion successful</returns>
        public bool DeleteCounterCategory(string categoryName)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));

            try
            {
                // First, unregister all counters in this category;
                var countersToRemove = _counters.Keys;
                    .Where(k => k.StartsWith($"{categoryName}_"))
                    .ToList();

                foreach (var counterKey in countersToRemove)
                {
                    UnregisterCounter(categoryName, ExtractCounterName(counterKey), ExtractInstanceName(counterKey));
                }

                // Remove category;
                if (_categories.TryRemove(categoryName, out _))
                {
                    // Platform-specific category deletion;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        DeleteWindowsCounterCategory(categoryName);
                    }
                    else;
                    {
                        DeleteCrossPlatformCounterCategory(categoryName);
                    }

                    _logger.Info($"Counter category deleted: {categoryName}");

                    // Publish category deleted event;
                    PublishCategoryDeletedEvent(categoryName);

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete counter category {categoryName}", ex);
                return false;
            }
        }

        /// <summary>
        /// Collects values from all registered counters;
        /// </summary>
        /// <returns>Collection of counter samples</returns>
        public async Task<List<CounterSample>> CollectAllCountersAsync()
        {
            var samples = new List<CounterSample>();
            var collectionTime = DateTime.UtcNow;

            try
            {
                var collectionTasks = new List<Task<CounterSample>>();

                foreach (var kvp in _counters)
                {
                    var counterKey = kvp.Key;
                    var counter = kvp.Value;

                    collectionTasks.Add(Task.Run(() =>
                        CollectCounterSample(counterKey, counter, collectionTime)));
                }

                // Collect all samples with timeout;
                var timeoutCts = new CancellationTokenSource(_configuration.CollectionTimeout);
                var completedTask = await Task.WhenAny(
                    Task.WhenAll(collectionTasks),
                    Task.Delay(_configuration.CollectionTimeout, timeoutCts.Token));

                if (completedTask == collectionTasks.FirstOrDefault())
                {
                    samples.AddRange(await Task.WhenAll(collectionTasks));
                }
                else;
                {
                    _logger.Warn($"Counter collection timed out after {_configuration.CollectionTimeout.TotalSeconds} seconds");
                }

                // Update history and statistics;
                foreach (var sample in samples)
                {
                    UpdateCounterHistory(sample);
                    UpdateCounterStatistics(sample);
                }

                LastCollectionTime = collectionTime;
                TotalSamplesCollected += samples.Count;

                // Publish collection completed event;
                PublishCollectionCompletedEvent(samples.Count, collectionTime);

                _logger.Debug($"Collected {samples.Count} counter samples");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to collect counters", ex);
            }

            return samples;
        }

        /// <summary>
        /// Collects value from a specific counter;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <returns>Counter sample or null if not found</returns>
        public async Task<CounterSample> CollectCounterAsync(
            string categoryName,
            string counterName,
            string instanceName = null)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (!_counters.TryGetValue(counterKey, out var counter))
            {
                _logger.Warn($"Counter not found: {counterKey}");
                return null;
            }

            try
            {
                var sample = await Task.Run(() =>
                    CollectCounterSample(counterKey, counter, DateTime.UtcNow));

                UpdateCounterHistory(sample);
                UpdateCounterStatistics(sample);

                return sample;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to collect counter {counterKey}", ex);
                return null;
            }
        }

        /// <summary>
        /// Gets the current value of a counter;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <returns>Current counter value</returns>
        public double GetCounterValue(string categoryName, string counterName, string instanceName = null)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                return counterData.LastValue;
            }

            return 0;
        }

        /// <summary>
        /// Gets counter history for analysis;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <param name="timeRange">Time range for history</param>
        /// <returns>List of historical samples</returns>
        public List<CounterSample> GetCounterHistory(
            string categoryName,
            string counterName,
            string instanceName = null,
            TimeSpan? timeRange = null)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (!_counterHistory.TryGetValue(counterKey, out var history))
            {
                return new List<CounterSample>();
            }

            if (timeRange.HasValue)
            {
                var cutoff = DateTime.UtcNow - timeRange.Value;
                return history.Where(s => s.SampleTime >= cutoff).ToList();
            }

            return history.ToList();
        }

        /// <summary>
        /// Gets counter statistics and trends;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <returns>Counter statistics</returns>
        public CounterStatistics GetCounterStatistics(
            string categoryName,
            string counterName,
            string instanceName = null)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                return counterData.Statistics;
            }

            return new CounterStatistics();
        }

        /// <summary>
        /// Gets all registered counter information;
        /// </summary>
        /// <returns>List of counter information</returns>
        public List<CounterInfo> GetAllCounterInfo()
        {
            return _counterData.Values;
                .Select(cd => cd.Info)
                .ToList();
        }

        /// <summary>
        /// Gets all registered category information;
        /// </summary>
        /// <returns>List of category information</returns>
        public List<CounterCategoryInfo> GetAllCategoryInfo()
        {
            return _categories.Values;
                .Select(c => c.Info)
                .ToList();
        }

        /// <summary>
        /// Starts counter monitoring;
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoringActive)
                return;

            lock (_syncLock)
            {
                if (_isMonitoringActive)
                    return;

                _monitoringCts = new CancellationTokenSource();

                // Start collection timer;
                _collectionTimer = new Timer(
                    async _ => await CollectAllCountersAsync(),
                    null,
                    TimeSpan.Zero,
                    _configuration.CollectionInterval);

                // Start cleanup timer;
                _cleanupTimer = new Timer(
                    _ => CleanupOldSamples(),
                    null,
                    TimeSpan.Zero,
                    _configuration.CleanupInterval);

                // Start background collection task for high-frequency counters;
                _collectionTask = Task.Run(() => ProcessHighFrequencyCountersAsync(_monitoringCts.Token));

                _isMonitoringActive = true;

                _logger.Info("Counter monitoring started");

                // Publish monitoring started event;
                _eventBus.Publish(new CounterMonitoringStartedEvent;
                {
                    StartedAt = DateTime.UtcNow,
                    Configuration = _configuration;
                });
            }
        }

        /// <summary>
        /// Stops counter monitoring;
        /// </summary>
        public void StopMonitoring()
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

                _collectionTimer?.Dispose();
                _collectionTimer = null;

                _cleanupTimer?.Dispose();
                _cleanupTimer = null;

                _isMonitoringActive = false;

                // Wait for collection task to complete;
                try
                {
                    _collectionTask?.Wait(TimeSpan.FromSeconds(30));
                }
                catch (AggregateException ex)
                {
                    _logger.Warn($"Collection task stopped with errors: {ex.Message}");
                }

                _logger.Info("Counter monitoring stopped");

                // Publish monitoring stopped event;
                _eventBus.Publish(new CounterMonitoringStoppedEvent;
                {
                    StoppedAt = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Increments a custom counter value;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <param name="incrementBy">Amount to increment</param>
        public void IncrementCounter(
            string categoryName,
            string counterName,
            string instanceName = null,
            long incrementBy = 1)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                lock (counterData.SyncLock)
                {
                    counterData.LastValue += incrementBy;
                    counterData.Statistics.TotalIncrements += incrementBy;
                    counterData.LastUpdated = DateTime.UtcNow;
                }

                // Publish counter incremented event;
                _eventBus.Publish(new CounterIncrementedEvent;
                {
                    CategoryName = categoryName,
                    CounterName = counterName,
                    InstanceName = instanceName,
                    IncrementAmount = incrementBy,
                    NewValue = counterData.LastValue,
                    IncrementTime = DateTime.UtcNow;
                });
            }
            else;
            {
                _logger.Warn($"Counter not found for increment: {counterKey}");
            }
        }

        /// <summary>
        /// Decrements a custom counter value;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <param name="decrementBy">Amount to decrement</param>
        public void DecrementCounter(
            string categoryName,
            string counterName,
            string instanceName = null,
            long decrementBy = 1)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                lock (counterData.SyncLock)
                {
                    counterData.LastValue -= decrementBy;
                    counterData.Statistics.TotalDecrements += decrementBy;
                    counterData.LastUpdated = DateTime.UtcNow;
                }

                // Publish counter decremented event;
                _eventBus.Publish(new CounterDecrementedEvent;
                {
                    CategoryName = categoryName,
                    CounterName = counterName,
                    InstanceName = instanceName,
                    DecrementAmount = decrementBy,
                    NewValue = counterData.LastValue,
                    DecrementTime = DateTime.UtcNow;
                });
            }
            else;
            {
                _logger.Warn($"Counter not found for decrement: {counterKey}");
            }
        }

        /// <summary>
        /// Sets a custom counter value;
        /// </summary>
        /// <param name="categoryName">Counter category name</param>
        /// <param name="counterName">Counter name</param>
        /// <param name="instanceName">Counter instance name (optional)</param>
        /// <param name="value">Value to set</param>
        public void SetCounterValue(
            string categoryName,
            string counterName,
            string instanceName = null,
            double value = 0)
        {
            Validate.NotNullOrEmpty(categoryName, nameof(categoryName));
            Validate.NotNullOrEmpty(counterName, nameof(counterName));

            var counterKey = GetCounterKey(categoryName, counterName, instanceName);

            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                lock (counterData.SyncLock)
                {
                    counterData.LastValue = value;
                    counterData.LastUpdated = DateTime.UtcNow;
                }

                // Publish counter set event;
                _eventBus.Publish(new CounterSetEvent;
                {
                    CategoryName = categoryName,
                    CounterName = counterName,
                    InstanceName = instanceName,
                    NewValue = value,
                    SetTime = DateTime.UtcNow;
                });
            }
            else;
            {
                _logger.Warn($"Counter not found for set value: {counterKey}");
            }
        }

        /// <summary>
        /// Performs counter diagnostics and validation;
        /// </summary>
        /// <returns>Diagnostic results</returns>
        public async Task<CounterDiagnostics> PerformDiagnosticsAsync()
        {
            var diagnostics = new CounterDiagnostics;
            {
                DiagnosticTime = DateTime.UtcNow;
            };

            try
            {
                diagnostics.TotalCounters = _counters.Count;
                diagnostics.TotalCategories = _categories.Count;

                // Check system performance counter availability;
                diagnostics.SystemCountersAvailable = await CheckSystemCounterAvailabilityAsync();

                // Check custom counter health;
                diagnostics.CustomCounterHealth = CheckCustomCounterHealth();

                // Check collection performance;
                diagnostics.CollectionPerformance = await MeasureCollectionPerformanceAsync();

                // Check memory usage;
                diagnostics.MemoryUsage = CalculateMemoryUsage();

                // Generate recommendations;
                diagnostics.Recommendations = GenerateDiagnosticRecommendations(diagnostics);

                _logger.Info($"Counter diagnostics completed: {diagnostics.TotalCounters} counters, {diagnostics.TotalCategories} categories");
            }
            catch (Exception ex)
            {
                diagnostics.Errors.Add($"Diagnostics failed: {ex.Message}");
                _logger.Error("Counter diagnostics failed", ex);
            }

            return diagnostics;
        }

        /// <summary>
        /// Exports counter data for external analysis;
        /// </summary>
        /// <param name="format">Export format</param>
        /// <param name="timeRange">Time range for data</param>
        /// <returns>Exported counter data</returns>
        public CounterDataExport ExportCounterData(
            ExportFormat format = ExportFormat.Json,
            TimeSpan? timeRange = null)
        {
            var export = new CounterDataExport;
            {
                ExportTime = DateTime.UtcNow,
                Format = format,
                TotalCounters = _counters.Count;
            };

            try
            {
                foreach (var kvp in _counterData)
                {
                    var counterKey = kvp.Key;
                    var counterData = kvp.Value;

                    var counterExport = new CounterExportInfo;
                    {
                        CounterInfo = counterData.Info,
                        CurrentValue = counterData.LastValue,
                        Statistics = counterData.Statistics,
                        LastUpdated = counterData.LastUpdated;
                    };

                    // Add historical data if available;
                    if (_counterHistory.TryGetValue(counterKey, out var history))
                    {
                        counterExport.HistoricalSamples = timeRange.HasValue;
                            ? history.Where(s => s.SampleTime >= DateTime.UtcNow - timeRange.Value).ToList()
                            : history.ToList();
                    }

                    export.CounterData.Add(counterExport);
                }

                _logger.Info($"Counter data exported: {export.CounterData.Count} counters");
            }
            catch (Exception ex)
            {
                export.Errors.Add($"Export failed: {ex.Message}");
                _logger.Error("Counter data export failed", ex);
            }

            return export;
        }

        /// <summary>
        /// Gets performance statistics;
        /// </summary>
        /// <returns>Performance statistics</returns>
        public PerformanceStatistics GetPerformanceStatistics()
        {
            var stats = new PerformanceStatistics;
            {
                TotalCounters = _counters.Count,
                TotalCategories = _categories.Count,
                TotalSamplesCollected = TotalSamplesCollected,
                LastCollectionTime = LastCollectionTime,
                IsMonitoringActive = _isMonitoringActive;
            };

            // Calculate collection rates;
            if (_counterData.Any())
            {
                stats.AverageCollectionTimeMs = _counterData.Values;
                    .Average(cd => cd.Statistics.AverageCollectionTimeMs);

                stats.TotalCollectionErrors = _counterData.Values;
                    .Sum(cd => cd.Statistics.TotalCollectionErrors);

                stats.HighestValueCounter = _counterData.Values;
                    .OrderByDescending(cd => cd.LastValue)
                    .FirstOrDefault()?.Info.Name;
            }

            return stats;
        }

        /// <summary>
        /// Resets counter statistics;
        /// </summary>
        /// <param name="categoryName">Category name (optional)</param>
        /// <param name="counterName">Counter name (optional)</param>
        /// <param name="instanceName">Instance name (optional)</param>
        public void ResetStatistics(string categoryName = null, string counterName = null, string instanceName = null)
        {
            if (string.IsNullOrEmpty(categoryName))
            {
                // Reset all counters;
                foreach (var counterData in _counterData.Values)
                {
                    counterData.Statistics.Reset();
                }
                _logger.Info("Reset statistics for all counters");
            }
            else if (string.IsNullOrEmpty(counterName))
            {
                // Reset all counters in category;
                var counterKeys = _counters.Keys;
                    .Where(k => k.StartsWith($"{categoryName}_"))
                    .ToList();

                foreach (var key in counterKeys)
                {
                    if (_counterData.TryGetValue(key, out var counterData))
                    {
                        counterData.Statistics.Reset();
                    }
                }
                _logger.Info($"Reset statistics for category: {categoryName}");
            }
            else;
            {
                // Reset specific counter;
                var counterKey = GetCounterKey(categoryName, counterName, instanceName);

                if (_counterData.TryGetValue(counterKey, out var counterData))
                {
                    counterData.Statistics.Reset();
                    _logger.Info($"Reset statistics for counter: {counterKey}");
                }
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Initializes platform-specific counter system;
        /// </summary>
        private void InitializePlatformCounters()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    InitializeWindowsPerformanceCounters();
                }
                else;
                {
                    InitializeCrossPlatformPerformanceCounters();
                }

                _logger.Info("Platform counters initialized");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Platform counter initialization failed: {ex.Message}");
                // Continue with basic counters;
            }
        }

        /// <summary>
        /// Registers default system counters;
        /// </summary>
        private void RegisterDefaultCounters()
        {
            // System performance counters;
            RegisterSystemCounters();

            // Application performance counters;
            RegisterApplicationCounters();

            // Custom performance counters;
            RegisterCustomCounters();
        }

        /// <summary>
        /// Registers system performance counters;
        /// </summary>
        private void RegisterSystemCounters()
        {
            try
            {
                // Processor counters;
                RegisterCounter(new CounterInfo;
                {
                    CategoryName = "Processor",
                    Name = "% Processor Time",
                    InstanceName = "_Total",
                    CounterType = PerformanceCounterType.ProcessorTime,
                    HelpText = "Percentage of processor time used",
                    IsSystemCounter = true;
                });

                // Memory counters;
                RegisterCounter(new CounterInfo;
                {
                    CategoryName = "Memory",
                    Name = "Available MBytes",
                    CounterType = PerformanceCounterType.NumberOfItems64,
                    HelpText = "Available memory in megabytes",
                    IsSystemCounter = true;
                });

                RegisterCounter(new CounterInfo;
                {
                    CategoryName = "Memory",
                    Name = "% Committed Bytes In Use",
                    CounterType = PerformanceCounterType.RawFraction,
                    HelpText = "Percentage of committed memory in use",
                    IsSystemCounter = true;
                });

                // Disk counters;
                RegisterCounter(new CounterInfo;
                {
                    CategoryName = "PhysicalDisk",
                    Name = "% Disk Time",
                    InstanceName = "_Total",
                    CounterType = PerformanceCounterType.RawFraction,
                    HelpText = "Percentage of disk time active",
                    IsSystemCounter = true;
                });

                // Network counters;
                RegisterCounter(new CounterInfo;
                {
                    CategoryName = "Network Interface",
                    Name = "Bytes Total/sec",
                    InstanceName = GetPrimaryNetworkInterface(),
                    CounterType = PerformanceCounterType.RateOfCountsPerSecond64,
                    HelpText = "Total network bytes per second",
                    IsSystemCounter = true;
                });

                _logger.Info("System performance counters registered");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to register system counters: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers application performance counters;
        /// </summary>
        private void RegisterApplicationCounters()
        {
            try
            {
                // Create application category if it doesn't exist;
                var appCategory = new CounterCategoryInfo;
                {
                    Name = "NEDA Application",
                    HelpText = "NEDA Application Performance Counters",
                    CategoryType = PerformanceCounterCategoryType.MultiInstance;
                };

                CreateCounterCategory(appCategory);

                // Application-specific counters;
                var appCounters = new[]
                {
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Application",
                        Name = "Total Requests",
                        CounterType = PerformanceCounterType.NumberOfItems64,
                        HelpText = "Total number of requests processed",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Application",
                        Name = "Requests/sec",
                        CounterType = PerformanceCounterType.RateOfCountsPerSecond32,
                        HelpText = "Requests processed per second",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Application",
                        Name = "Average Response Time",
                        CounterType = PerformanceCounterType.AverageTimer32,
                        HelpText = "Average response time in milliseconds",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Application",
                        Name = "Active Users",
                        CounterType = PerformanceCounterType.NumberOfItems32,
                        HelpText = "Number of active users",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Application",
                        Name = "Error Rate",
                        CounterType = PerformanceCounterType.RawFraction,
                        HelpText = "Percentage of requests resulting in errors",
                        IsSystemCounter = false;
                    }
                };

                foreach (var counter in appCounters)
                {
                    RegisterCounter(counter);
                }

                _logger.Info("Application performance counters registered");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to register application counters: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers custom performance counters;
        /// </summary>
        private void RegisterCustomCounters()
        {
            try
            {
                // Create custom category if it doesn't exist;
                var customCategory = new CounterCategoryInfo;
                {
                    Name = "NEDA Custom",
                    HelpText = "NEDA Custom Performance Counters",
                    CategoryType = PerformanceCounterCategoryType.SingleInstance;
                };

                CreateCounterCategory(customCategory);

                // Custom counters for specific monitoring;
                var customCounters = new[]
                {
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Custom",
                        Name = "Cache Hits",
                        CounterType = PerformanceCounterType.NumberOfItems64,
                        HelpText = "Total cache hits",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Custom",
                        Name = "Cache Misses",
                        CounterType = PerformanceCounterType.NumberOfItems64,
                        HelpText = "Total cache misses",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Custom",
                        Name = "Database Connections",
                        CounterType = PerformanceCounterType.NumberOfItems32,
                        HelpText = "Active database connections",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Custom",
                        Name = "Queue Length",
                        CounterType = PerformanceCounterType.NumberOfItems32,
                        HelpText = "Current queue length",
                        IsSystemCounter = false;
                    },
                    new CounterInfo;
                    {
                        CategoryName = "NEDA Custom",
                        Name = "Processing Time",
                        CounterType = PerformanceCounterType.AverageTimer32,
                        HelpText = "Average processing time in milliseconds",
                        IsSystemCounter = false;
                    }
                };

                foreach (var counter in customCounters)
                {
                    RegisterCounter(counter);
                }

                _logger.Info("Custom performance counters registered");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to register custom counters: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a Windows performance counter;
        /// </summary>
        private PerformanceCounter CreateWindowsCounter(CounterInfo counterInfo)
        {
            try
            {
                PerformanceCounter counter;

                if (!string.IsNullOrEmpty(counterInfo.InstanceName))
                {
                    counter = new PerformanceCounter(
                        counterInfo.CategoryName,
                        counterInfo.Name,
                        counterInfo.InstanceName,
                        readOnly: true);
                }
                else;
                {
                    counter = new PerformanceCounter(
                        counterInfo.CategoryName,
                        counterInfo.Name,
                        readOnly: true);
                }

                // Test the counter;
                var testValue = counter.NextValue();

                return counter;
            }
            catch
            {
                // If system counter fails, create a custom counter;
                return CreateCustomCounter(counterInfo);
            }
        }

        /// <summary>
        /// Creates a cross-platform performance counter;
        /// </summary>
        private PerformanceCounter CreateCrossPlatformCounter(CounterInfo counterInfo)
        {
            // For non-Windows platforms, create custom counters;
            return CreateCustomCounter(counterInfo);
        }

        /// <summary>
        /// Creates a custom counter (platform-independent)
        /// </summary>
        private PerformanceCounter CreateCustomCounter(CounterInfo counterInfo)
        {
            // Create a custom counter implementation;
            // This is a simplified version that doesn't rely on Windows Performance Counters;
            var customCounter = new PerformanceCounter();

            // Initialize custom counter properties;
            // Note: This would need a full custom implementation for non-Windows platforms;

            return customCounter;
        }

        /// <summary>
        /// Creates a Windows counter category;
        /// </summary>
        private void CreateWindowsCounterCategory(CounterCategoryInfo categoryInfo)
        {
            // Windows-specific category creation;
            // This would use PerformanceCounterCategory.Create;
            // Note: Requires administrative privileges;

            _logger.Debug($"Windows counter category created: {categoryInfo.Name}");
        }

        /// <summary>
        /// Creates a cross-platform counter category;
        /// </summary>
        private void CreateCrossPlatformCounterCategory(CounterCategoryInfo categoryInfo)
        {
            // Cross-platform category creation;
            // Store category information in our internal dictionary;
            _logger.Debug($"Cross-platform counter category created: {categoryInfo.Name}");
        }

        /// <summary>
        /// Deletes a Windows counter category;
        /// </summary>
        private void DeleteWindowsCounterCategory(string categoryName)
        {
            // Windows-specific category deletion;
            // This would use PerformanceCounterCategory.Delete;
            // Note: Requires administrative privileges;

            _logger.Debug($"Windows counter category deleted: {categoryName}");
        }

        /// <summary>
        /// Deletes a cross-platform counter category;
        /// </summary>
        private void DeleteCrossPlatformCounterCategory(string categoryName)
        {
            // Cross-platform category deletion;
            // Remove from internal dictionary;
            _logger.Debug($"Cross-platform counter category deleted: {categoryName}");
        }

        /// <summary>
        /// Initializes Windows performance counters;
        /// </summary>
        private void InitializeWindowsPerformanceCounters()
        {
            // Windows-specific initialization;
            // Check if performance counters are available;
            try
            {
                var categories = PerformanceCounterCategory.GetCategories();
                _logger.Info($"Windows performance counters available: {categories.Length} categories");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Windows performance counters not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes cross-platform performance counters;
        /// </summary>
        private void InitializeCrossPlatformPerformanceCounters()
        {
            // Cross-platform initialization;
            // Setup custom counter system;
            _logger.Info("Cross-platform performance counters initialized");
        }

        /// <summary>
        /// Ensures a category exists;
        /// </summary>
        private void EnsureCategoryExists(string categoryName, string categoryHelp)
        {
            if (!_categories.ContainsKey(categoryName))
            {
                var categoryInfo = new CounterCategoryInfo;
                {
                    Name = categoryName,
                    HelpText = categoryHelp ?? $"{categoryName} performance counters",
                    CategoryType = PerformanceCounterCategoryType.SingleInstance;
                };

                _categories[categoryName] = new CounterCategory(categoryInfo);
            }
        }

        /// <summary>
        /// Gets the counter key for dictionary lookup;
        /// </summary>
        private string GetCounterKey(string categoryName, string counterName, string instanceName)
        {
            return string.IsNullOrEmpty(instanceName)
                ? $"{categoryName}_{counterName}"
                : $"{categoryName}_{counterName}_{instanceName}";
        }

        /// <summary>
        /// Extracts counter name from key;
        /// </summary>
        private string ExtractCounterName(string counterKey)
        {
            var parts = counterKey.Split('_');
            return parts.Length >= 2 ? parts[1] : string.Empty;
        }

        /// <summary>
        /// Extracts instance name from key;
        /// </summary>
        private string ExtractInstanceName(string counterKey)
        {
            var parts = counterKey.Split('_');
            return parts.Length >= 3 ? parts[2] : null;
        }

        /// <summary>
        /// Gets the primary network interface name;
        /// </summary>
        private string GetPrimaryNetworkInterface()
        {
            try
            {
                // This would use System.Net.NetworkInformation to get primary interface;
                // For now, return a default value;
                return "*";
            }
            catch
            {
                return "*";
            }
        }

        /// <summary>
        /// Collects a single counter sample;
        /// </summary>
        private CounterSample CollectCounterSample(string counterKey, PerformanceCounter counter, DateTime collectionTime)
        {
            var sample = new CounterSample;
            {
                CounterKey = counterKey,
                SampleTime = collectionTime;
            };

            try
            {
                // Get counter value;
                double rawValue = 0;

                if (counter != null)
                {
                    try
                    {
                        rawValue = counter.NextValue();
                    }
                    catch
                    {
                        // For custom counters, get from internal data;
                        if (_counterData.TryGetValue(counterKey, out var counterData))
                        {
                            rawValue = counterData.LastValue;
                        }
                    }
                }

                sample.RawValue = rawValue;
                sample.FormattedValue = FormatCounterValue(counterKey, rawValue);

                // Calculate derived values if needed;
                CalculateDerivedValues(sample);

                // Update performance metrics;
                UpdateCollectionPerformance(counterKey, sample);
            }
            catch (Exception ex)
            {
                sample.Error = ex.Message;
                sample.HasError = true;

                // Update error statistics;
                if (_counterData.TryGetValue(counterKey, out var counterData))
                {
                    Interlocked.Increment(ref counterData.Statistics.TotalCollectionErrors);
                }

                _logger.Warn($"Error collecting counter {counterKey}: {ex.Message}");
            }

            return sample;
        }

        /// <summary>
        /// Formats counter value based on counter type;
        /// </summary>
        private string FormatCounterValue(string counterKey, double rawValue)
        {
            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                switch (counterData.Info.CounterType)
                {
                    case PerformanceCounterType.ProcessorTime:
                    case PerformanceCounterType.RawFraction:
                        return $"{rawValue:F2}%";

                    case PerformanceCounterType.NumberOfItems64:
                        return FormatLargeNumber(rawValue);

                    case PerformanceCounterType.RateOfCountsPerSecond32:
                    case PerformanceCounterType.RateOfCountsPerSecond64:
                        return $"{rawValue:F0}/s";

                    case PerformanceCounterType.AverageTimer32:
                        return $"{rawValue:F2} ms";

                    default:
                        return $"{rawValue:F2}";
                }
            }

            return $"{rawValue:F2}";
        }

        /// <summary>
        /// Formats large numbers with suffixes;
        /// </summary>
        private string FormatLargeNumber(double value)
        {
            string[] suffixes = { "", "K", "M", "B", "T" };
            int suffixIndex = 0;

            while (value >= 1000 && suffixIndex < suffixes.Length - 1)
            {
                value /= 1000;
                suffixIndex++;
            }

            return $"{value:F2}{suffixes[suffixIndex]}";
        }

        /// <summary>
        /// Calculates derived values for counter sample;
        /// </summary>
        private void CalculateDerivedValues(CounterSample sample)
        {
            if (_counterData.TryGetValue(sample.CounterKey, out var counterData))
            {
                // Calculate rate of change if we have previous value;
                if (counterData.LastSampleTime.HasValue)
                {
                    var timeDiff = (sample.SampleTime - counterData.LastSampleTime.Value).TotalSeconds;
                    if (timeDiff > 0)
                    {
                        sample.RatePerSecond = (sample.RawValue - counterData.LastValue) / timeDiff;
                    }
                }

                // Calculate percentage change;
                if (counterData.LastValue != 0)
                {
                    sample.PercentageChange = ((sample.RawValue - counterData.LastValue) / Math.Abs(counterData.LastValue)) * 100;
                }

                // Update counter data;
                counterData.LastValue = sample.RawValue;
                counterData.LastSampleTime = sample.SampleTime;
                counterData.LastUpdated = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Updates collection performance metrics;
        /// </summary>
        private void UpdateCollectionPerformance(string counterKey, CounterSample sample)
        {
            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                var collectionTime = DateTime.UtcNow - sample.SampleTime;

                lock (counterData.SyncLock)
                {
                    // Update average collection time;
                    counterData.Statistics.TotalCollectionTimeMs += collectionTime.TotalMilliseconds;
                    counterData.Statistics.TotalCollections++;

                    counterData.Statistics.AverageCollectionTimeMs =
                        counterData.Statistics.TotalCollectionTimeMs / counterData.Statistics.TotalCollections;

                    // Update min/max collection times;
                    if (collectionTime.TotalMilliseconds < counterData.Statistics.MinCollectionTimeMs)
                    {
                        counterData.Statistics.MinCollectionTimeMs = collectionTime.TotalMilliseconds;
                    }

                    if (collectionTime.TotalMilliseconds > counterData.Statistics.MaxCollectionTimeMs)
                    {
                        counterData.Statistics.MaxCollectionTimeMs = collectionTime.TotalMilliseconds;
                    }
                }
            }
        }

        /// <summary>
        /// Updates counter history;
        /// </summary>
        private void UpdateCounterHistory(CounterSample sample)
        {
            if (!_counterHistory.TryGetValue(sample.CounterKey, out var history))
            {
                history = new List<CounterSample>();
                _counterHistory[sample.CounterKey] = history;
            }

            lock (history)
            {
                history.Add(sample);

                // Trim history if it exceeds max size;
                if (history.Count > _configuration.MaxHistorySamples)
                {
                    history.RemoveRange(0, history.Count - _configuration.MaxHistorySamples);
                }
            }
        }

        /// <summary>
        /// Updates counter statistics;
        /// </summary>
        private void UpdateCounterStatistics(CounterSample sample)
        {
            if (!_counterData.TryGetValue(sample.CounterKey, out var counterData))
                return;

            lock (counterData.SyncLock)
            {
                var stats = counterData.Statistics;

                // Update value statistics;
                stats.TotalValue += sample.RawValue;
                stats.SampleCount++;

                stats.AverageValue = stats.TotalValue / stats.SampleCount;

                if (sample.RawValue < stats.MinValue)
                {
                    stats.MinValue = sample.RawValue;
                    stats.MinValueTime = sample.SampleTime;
                }

                if (sample.RawValue > stats.MaxValue)
                {
                    stats.MaxValue = sample.RawValue;
                    stats.MaxValueTime = sample.SampleTime;
                }

                // Update trend analysis;
                UpdateTrendAnalysis(stats, sample);

                // Update rate statistics;
                if (!double.IsNaN(sample.RatePerSecond))
                {
                    stats.TotalRate += sample.RatePerSecond;
                    stats.RateSampleCount++;
                    stats.AverageRate = stats.TotalRate / stats.RateSampleCount;
                }
            }
        }

        /// <summary>
        /// Updates trend analysis;
        /// </summary>
        private void UpdateTrendAnalysis(CounterStatistics stats, CounterSample sample)
        {
            // Simple trend analysis based on recent samples;
            stats.RecentSamples.Add(sample.RawValue);

            if (stats.RecentSamples.Count > _configuration.TrendAnalysisWindow)
            {
                stats.RecentSamples.RemoveAt(0);
            }

            if (stats.RecentSamples.Count >= 2)
            {
                // Calculate simple linear trend;
                var firstValue = stats.RecentSamples.First();
                var lastValue = stats.RecentSamples.Last();

                stats.TrendValue = lastValue - firstValue;
                stats.TrendPercentage = (stats.TrendValue / Math.Abs(firstValue)) * 100;

                // Determine trend direction;
                if (Math.Abs(stats.TrendPercentage) < 1)
                    stats.TrendDirection = TrendDirection.Stable;
                else if (stats.TrendValue > 0)
                    stats.TrendDirection = TrendDirection.Increasing;
                else;
                    stats.TrendDirection = TrendDirection.Decreasing;
            }
        }

        /// <summary>
        /// Processes high-frequency counters in background;
        /// </summary>
        private async Task ProcessHighFrequencyCountersAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Process high-frequency counters more frequently;
                    var highFrequencyCounters = _counters;
                        .Where(kvp => IsHighFrequencyCounter(kvp.Key))
                        .ToList();

                    if (highFrequencyCounters.Any())
                    {
                        foreach (var kvp in highFrequencyCounters)
                        {
                            var sample = CollectCounterSample(kvp.Key, kvp.Value, DateTime.UtcNow);
                            UpdateCounterHistory(sample);
                            UpdateCounterStatistics(sample);
                        }

                        await Task.Delay(_configuration.HighFrequencyCollectionInterval, cancellationToken);
                    }
                    else;
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("High-frequency counter processing failed", ex);
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Checks if a counter is high-frequency;
        /// </summary>
        private bool IsHighFrequencyCounter(string counterKey)
        {
            if (_counterData.TryGetValue(counterKey, out var counterData))
            {
                // Counters that need more frequent updates;
                var highFrequencyNames = new[]
                {
                    "Processor Time",
                    "Requests/sec",
                    "Bytes Total/sec",
                    "Queue Length"
                };

                return highFrequencyNames.Any(name =>
                    counterData.Info.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        /// <summary>
        /// Cleans up old samples from history;
        /// </summary>
        private void CleanupOldSamples()
        {
            try
            {
                var cutoff = DateTime.UtcNow - _configuration.DataRetentionPeriod;
                var removedCount = 0;

                foreach (var kvp in _counterHistory)
                {
                    var history = kvp.Value;

                    lock (history)
                    {
                        var oldSamples = history.Where(s => s.SampleTime < cutoff).ToList();
                        removedCount += oldSamples.Count;

                        foreach (var sample in oldSamples)
                        {
                            history.Remove(sample);
                        }
                    }
                }

                if (removedCount > 0)
                {
                    _logger.Debug($"Cleaned up {removedCount} old counter samples");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to clean up old samples: {ex.Message}");
            }
        }

        #region Diagnostic Methods;

        /// <summary>
        /// Checks system counter availability;
        /// </summary>
        private async Task<bool> CheckSystemCounterAvailabilityAsync()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var categories = await Task.Run(() => PerformanceCounterCategory.GetCategories());
                    return categories.Length > 0;
                }

                // For non-Windows, we use custom counters;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks custom counter health;
        /// </summary>
        private CounterHealth CheckCustomCounterHealth()
        {
            var health = new CounterHealth;
            {
                TotalCounters = _counters.Count;
            };

            foreach (var kvp in _counterData)
            {
                var counterData = kvp.Value;

                if (counterData.LastUpdated < DateTime.UtcNow.AddMinutes(-5))
                {
                    health.StaleCounters++;
                }

                if (counterData.Statistics.TotalCollectionErrors > 0)
                {
                    health.ErrorCounters++;
                }

                health.TotalCollections += counterData.Statistics.TotalCollections;
            }

            if (health.TotalCounters > 0)
            {
                health.HealthPercentage = 100 -
                    ((health.StaleCounters + health.ErrorCounters) * 100 / health.TotalCounters / 2);
            }

            return health;
        }

        /// <summary>
        /// Measures collection performance;
        /// </summary>
        private async Task<CollectionPerformance> MeasureCollectionPerformanceAsync()
        {
            var performance = new CollectionPerformance;
            {
                MeasurementTime = DateTime.UtcNow;
            };

            try
            {
                // Measure collection time for all counters;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var samples = await CollectAllCountersAsync();
                stopwatch.Stop();

                performance.TotalCollectionTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                performance.SamplesCollected = samples.Count;

                if (samples.Count > 0)
                {
                    performance.AverageCollectionTimePerCounterMs = performance.TotalCollectionTimeMs / samples.Count;
                    performance.CollectionsPerSecond = samples.Count / (performance.TotalCollectionTimeMs / 1000);
                }

                // Calculate memory usage;
                performance.MemoryUsageBytes = CalculateHistoryMemoryUsage();
            }
            catch (Exception ex)
            {
                performance.Errors.Add($"Performance measurement failed: {ex.Message}");
            }

            return performance;
        }

        /// <summary>
        /// Calculates memory usage of counter history;
        /// </summary>
        private long CalculateMemoryUsage()
        {
            long totalBytes = 0;

            foreach (var history in _counterHistory.Values)
            {
                // Approximate size: each sample ~ 100 bytes;
                totalBytes += history.Count * 100;
            }

            return totalBytes;
        }

        /// <summary>
        /// Calculates history memory usage;
        /// </summary>
        private long CalculateHistoryMemoryUsage()
        {
            long totalBytes = 0;

            foreach (var kvp in _counterHistory)
            {
                var history = kvp.Value;
                totalBytes += history.Count * 64; // Approximate size per sample;
            }

            return totalBytes;
        }

        /// <summary>
        /// Generates diagnostic recommendations;
        /// </summary>
        private List<string> GenerateDiagnosticRecommendations(CounterDiagnostics diagnostics)
        {
            var recommendations = new List<string>();

            if (diagnostics.TotalCounters == 0)
            {
                recommendations.Add("No counters registered - consider adding performance counters for monitoring");
            }

            if (!diagnostics.SystemCountersAvailable)
            {
                recommendations.Add("System performance counters not available - using custom counters only");
            }

            if (diagnostics.CustomCounterHealth?.StaleCounters > 0)
            {
                recommendations.Add($"{diagnostics.CustomCounterHealth.StaleCounters} stale counters detected - check collection intervals");
            }

            if (diagnostics.CollectionPerformance?.AverageCollectionTimePerCounterMs > 10)
            {
                recommendations.Add("High collection latency detected - optimize counter collection");
            }

            if (diagnostics.MemoryUsage > 1024 * 1024 * 100) // 100MB;
            {
                recommendations.Add("High memory usage for counter history - consider reducing retention period");
            }

            return recommendations;
        }

        #endregion;

        #region Event Publishing;

        /// <summary>
        /// Publishes counter registered event;
        /// </summary>
        private void PublishCounterRegisteredEvent(CounterInfo counterInfo)
        {
            try
            {
                var evt = new CounterRegisteredEvent;
                {
                    CategoryName = counterInfo.CategoryName,
                    CounterName = counterInfo.Name,
                    InstanceName = counterInfo.InstanceName,
                    CounterType = counterInfo.CounterType,
                    RegistrationTime = DateTime.UtcNow;
                };

                _eventBus.Publish(evt);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to publish CounterRegisteredEvent: {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes counter unregistered event;
        /// </summary>
        private void PublishCounterUnregisteredEvent(string categoryName, string counterName, string instanceName)
        {
            try
            {
                var evt = new CounterUnregisteredEvent;
                {
                    CategoryName = categoryName,
                    CounterName = counterName,
                    InstanceName = instanceName,
                    UnregistrationTime = DateTime.UtcNow;
                };

                _eventBus.Publish(evt);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to publish CounterUnregisteredEvent: {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes category created event;
        /// </summary>
        private void PublishCategoryCreatedEvent(CounterCategoryInfo categoryInfo)
        {
            try
            {
                var evt = new CounterCategoryCreatedEvent;
                {
                    CategoryName = categoryInfo.Name,
                    CategoryType = categoryInfo.CategoryType,
                    CreationTime = DateTime.UtcNow;
                };

                _eventBus.Publish(evt);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to publish CounterCategoryCreatedEvent: {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes category deleted event;
        /// </summary>
        private void PublishCategoryDeletedEvent(string categoryName)
        {
            try
            {
                var evt = new CounterCategoryDeletedEvent;
                {
                    CategoryName = categoryName,
                    DeletionTime = DateTime.UtcNow;
                };

                _eventBus.Publish(evt);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to publish CounterCategoryDeletedEvent: {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes collection completed event;
        /// </summary>
        private void PublishCollectionCompletedEvent(int sampleCount, DateTime collectionTime)
        {
            try
            {
                var evt = new CounterCollectionCompletedEvent;
                {
                    SampleCount = sampleCount,
                    CollectionTime = collectionTime,
                    TotalCounters = _counters.Count;
                };

                _eventBus.Publish(evt);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to publish CounterCollectionCompletedEvent: {ex.Message}");
            }
        }

        #endregion;

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        /// <summary>
        /// Disposes the counter manager and releases resources;
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
                    StopMonitoring();

                    // Dispose timers;
                    _collectionTimer?.Dispose();
                    _cleanupTimer?.Dispose();

                    // Dispose cancellation token source;
                    _monitoringCts?.Dispose();

                    // Dispose all counters;
                    foreach (var counter in _counters.Values)
                    {
                        counter?.Dispose();
                    }

                    // Clear collections;
                    _counters.Clear();
                    _categories.Clear();
                    _counterData.Clear();
                    _counterHistory.Clear();
                }

                _disposed = true;
            }
        }

        ~CounterManager()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Performance counter types;
    /// </summary>
    public enum PerformanceCounterType;
    {
        /// <summary>Number of items counter</summary>
        NumberOfItems32,
        /// <summary>64-bit number of items counter</summary>
        NumberOfItems64,
        /// <summary>Rate of counts per second (32-bit)</summary>
        RateOfCountsPerSecond32,
        /// <summary>Rate of counts per second (64-bit)</summary>
        RateOfCountsPerSecond64,
        /// <summary>Average timer</summary>
        AverageTimer32,
        /// <summary>Average base</summary>
        AverageBase,
        /// <summary>Raw fraction</summary>
        RawFraction,
        /// <summary>Raw base</summary>
        RawBase,
        /// <summary>Processor time counter</summary>
        ProcessorTime,
        /// <summary>Elapsed time counter</summary>
        ElapsedTime;
    }

    /// <summary>
    /// Performance counter category types;
    /// </summary>
    public enum PerformanceCounterCategoryType;
    {
        /// <summary>Single instance category</summary>
        SingleInstance,
        /// <summary>Multi-instance category</summary>
        MultiInstance;
    }

    /// <summary>
    /// Trend direction;
    /// </summary>
    public enum TrendDirection;
    {
        /// <summary>Increasing trend</summary>
        Increasing,
        /// <summary>Decreasing trend</summary>
        Decreasing,
        /// <summary>Stable trend</summary>
        Stable,
        /// <summary>Volatile trend</summary>
        Volatile;
    }

    /// <summary>
    /// Export format for counter data;
    /// </summary>
    public enum ExportFormat;
    {
        /// <summary>JSON format</summary>
        Json,
        /// <summary>CSV format</summary>
        Csv,
        /// <summary>XML format</summary>
        Xml,
        /// <summary>HTML format</summary>
        Html;
    }

    /// <summary>
    /// Counter manager configuration;
    /// </summary>
    public sealed class CounterManagerConfiguration;
    {
        /// <summary>Auto-start monitoring on initialization</summary>
        public bool AutoStartMonitoring { get; set; } = true;

        /// <summary>Collection interval for regular counters</summary>
        public TimeSpan CollectionInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Collection interval for high-frequency counters</summary>
        public TimeSpan HighFrequencyCollectionInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Timeout for counter collection</summary>
        public TimeSpan CollectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Cleanup interval for old samples</summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Data retention period</summary>
        public TimeSpan DataRetentionPeriod { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Maximum history samples per counter</summary>
        public int MaxHistorySamples { get; set; } = 10000;

        /// <summary>Trend analysis window size</summary>
        public int TrendAnalysisWindow { get; set; } = 100;

        /// <summary>Enable detailed logging</summary>
        public bool EnableDetailedLogging { get; set; } = false;

        /// <summary>Enable counter validation</summary>
        public bool EnableCounterValidation { get; set; } = true;

        /// <summary>Default configuration instance</summary>
        public static CounterManagerConfiguration Default => new CounterManagerConfiguration();
    }

    /// <summary>
    /// Counter information;
    /// </summary>
    public sealed class CounterInfo;
    {
        public string CategoryName { get; set; }
        public string Name { get; set; }
        public string InstanceName { get; set; }
        public PerformanceCounterType CounterType { get; set; }
        public string HelpText { get; set; }
        public bool IsSystemCounter { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Counter category information;
    /// </summary>
    public sealed class CounterCategoryInfo;
    {
        public string Name { get; set; }
        public string HelpText { get; set; }
        public PerformanceCounterCategoryType CategoryType { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Counter category;
    /// </summary>
    public sealed class CounterCategory;
    {
        public CounterCategoryInfo Info { get; }
        public List<string> Counters { get; } = new List<string>();
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public CounterCategory(CounterCategoryInfo info)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }
    }

    /// <summary>
    /// Counter data;
    /// </summary>
    public sealed class CounterData;
    {
        public CounterInfo Info { get; }
        public double LastValue { get; set; }
        public DateTime? LastSampleTime { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public CounterStatistics Statistics { get; } = new CounterStatistics();
        public object SyncLock { get; } = new object();

        public CounterData(CounterInfo info)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }
    }

    /// <summary>
    /// Counter statistics;
    /// </summary>
    public sealed class CounterStatistics;
    {
        public double MinValue { get; set; } = double.MaxValue;
        public double MaxValue { get; set; } = double.MinValue;
        public DateTime? MinValueTime { get; set; }
        public DateTime? MaxValueTime { get; set; }
        public double AverageValue { get; set; }
        public double TotalValue { get; set; }
        public long SampleCount { get; set; }

        public double MinRate { get; set; } = double.MaxValue;
        public double MaxRate { get; set; } = double.MinValue;
        public double AverageRate { get; set; }
        public double TotalRate { get; set; }
        public long RateSampleCount { get; set; }

        public double MinCollectionTimeMs { get; set; } = double.MaxValue;
        public double MaxCollectionTimeMs { get; set; } = double.MinValue;
        public double AverageCollectionTimeMs { get; set; }
        public double TotalCollectionTimeMs { get; set; }
        public long TotalCollections { get; set; }
        public long TotalCollectionErrors { get; set; }

        public double TrendValue { get; set; }
        public double TrendPercentage { get; set; }
        public TrendDirection TrendDirection { get; set; }
        public List<double> RecentSamples { get; } = new List<double>();

        public long TotalIncrements { get; set; }
        public long TotalDecrements { get; set; }

        /// <summary>
        /// Resets all statistics;
        /// </summary>
        public void Reset()
        {
            MinValue = double.MaxValue;
            MaxValue = double.MinValue;
            MinValueTime = null;
            MaxValueTime = null;
            AverageValue = 0;
            TotalValue = 0;
            SampleCount = 0;

            MinRate = double.MaxValue;
            MaxRate = double.MinValue;
            AverageRate = 0;
            TotalRate = 0;
            RateSampleCount = 0;

            MinCollectionTimeMs = double.MaxValue;
            MaxCollectionTimeMs = double.MinValue;
            AverageCollectionTimeMs = 0;
            TotalCollectionTimeMs = 0;
            TotalCollections = 0;
            TotalCollectionErrors = 0;

            TrendValue = 0;
            TrendPercentage = 0;
            TrendDirection = TrendDirection.Stable;
            RecentSamples.Clear();

            TotalIncrements = 0;
            TotalDecrements = 0;
        }
    }

    /// <summary>
    /// Counter sample;
    /// </summary>
    public sealed class CounterSample;
    {
        public string CounterKey { get; set; }
        public DateTime SampleTime { get; set; }
        public double RawValue { get; set; }
        public string FormattedValue { get; set; }
        public double RatePerSecond { get; set; }
        public double PercentageChange { get; set; }
        public string Error { get; set; }
        public bool HasError { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Counter diagnostics;
    /// </summary>
    public sealed class CounterDiagnostics;
    {
        public DateTime DiagnosticTime { get; set; }
        public int TotalCounters { get; set; }
        public int TotalCategories { get; set; }
        public bool SystemCountersAvailable { get; set; }
        public CounterHealth CustomCounterHealth { get; set; }
        public CollectionPerformance CollectionPerformance { get; set; }
        public long MemoryUsage { get; set; }
        public List<string> Recommendations { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Counter health information;
    /// </summary>
    public sealed class CounterHealth;
    {
        public int TotalCounters { get; set; }
        public int StaleCounters { get; set; }
        public int ErrorCounters { get; set; }
        public long TotalCollections { get; set; }
        public double HealthPercentage { get; set; }
    }

    /// <summary>
    /// Collection performance metrics;
    /// </summary>
    public sealed class CollectionPerformance;
    {
        public DateTime MeasurementTime { get; set; }
        public double TotalCollectionTimeMs { get; set; }
        public int SamplesCollected { get; set; }
        public double AverageCollectionTimePerCounterMs { get; set; }
        public double CollectionsPerSecond { get; set; }
        public long MemoryUsageBytes { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Counter data export;
    /// </summary>
    public sealed class CounterDataExport;
    {
        public DateTime ExportTime { get; set; }
        public ExportFormat Format { get; set; }
        public int TotalCounters { get; set; }
        public List<CounterExportInfo> CounterData { get; set; } = new List<CounterExportInfo>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Counter export information;
    /// </summary>
    public sealed class CounterExportInfo;
    {
        public CounterInfo CounterInfo { get; set; }
        public double CurrentValue { get; set; }
        public CounterStatistics Statistics { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<CounterSample> HistoricalSamples { get; set; } = new List<CounterSample>();
    }

    /// <summary>
    /// Performance statistics;
    /// </summary>
    public sealed class PerformanceStatistics;
    {
        public int TotalCounters { get; set; }
        public int TotalCategories { get; set; }
        public long TotalSamplesCollected { get; set; }
        public DateTime? LastCollectionTime { get; set; }
        public double AverageCollectionTimeMs { get; set; }
        public long TotalCollectionErrors { get; set; }
        public string HighestValueCounter { get; set; }
        public bool IsMonitoringActive { get; set; }
    }

    #region Events;

    /// <summary>
    /// Counter registered event;
    /// </summary>
    public sealed class CounterRegisteredEvent : IEvent;
    {
        public string CategoryName { get; set; }
        public string CounterName { get; set; }
        public string InstanceName { get; set; }
        public PerformanceCounterType CounterType { get; set; }
        public DateTime RegistrationTime { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterRegistered";
    }

    /// <summary>
    /// Counter unregistered event;
    /// </summary>
    public sealed class CounterUnregisteredEvent : IEvent;
    {
        public string CategoryName { get; set; }
        public string CounterName { get; set; }
        public string InstanceName { get; set; }
        public DateTime UnregistrationTime { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterUnregistered";
    }

    /// <summary>
    /// Counter category created event;
    /// </summary>
    public sealed class CounterCategoryCreatedEvent : IEvent;
    {
        public string CategoryName { get; set; }
        public PerformanceCounterCategoryType CategoryType { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterCategoryCreated";
    }

    /// <summary>
    /// Counter category deleted event;
    /// </summary>
    public sealed class CounterCategoryDeletedEvent : IEvent;
    {
        public string CategoryName { get; set; }
        public DateTime DeletionTime { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterCategoryDeleted";
    }

    /// <summary>
    /// Counter collection completed event;
    /// </summary>
    public sealed class CounterCollectionCompletedEvent : IEvent;
    {
        public int SampleCount { get; set; }
        public DateTime CollectionTime { get; set; }
        public int TotalCounters { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterCollectionCompleted";
    }

    /// <summary>
    /// Counter monitoring started event;
    /// </summary>
    public sealed class CounterMonitoringStartedEvent : IEvent;
    {
        public DateTime StartedAt { get; set; }
        public CounterManagerConfiguration Configuration { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterMonitoringStarted";
    }

    /// <summary>
    /// Counter monitoring stopped event;
    /// </summary>
    public sealed class CounterMonitoringStoppedEvent : IEvent;
    {
        public DateTime StoppedAt { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterMonitoringStopped";
    }

    /// <summary>
    /// Counter incremented event;
    /// </summary>
    public sealed class CounterIncrementedEvent : IEvent;
    {
        public string CategoryName { get; set; }
        public string CounterName { get; set; }
        public string InstanceName { get; set; }
        public long IncrementAmount { get; set; }
        public double NewValue { get; set; }
        public DateTime IncrementTime { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterIncremented";
    }

    /// <summary>
    /// Counter decremented event;
    /// </summary>
    public sealed class CounterDecrementedEvent : IEvent;
    {
        public string CategoryName { get; set; }
        public string CounterName { get; set; }
        public string InstanceName { get; set; }
        public long DecrementAmount { get; set; }
        public double NewValue { get; set; }
        public DateTime DecrementTime { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterDecremented";
    }

    /// <summary>
    /// Counter set event;
    /// </summary>
    public sealed class CounterSetEvent : IEvent;
    {
        public string CategoryName { get; set; }
        public string CounterName { get; set; }
        public string InstanceName { get; set; }
        public double NewValue { get; set; }
        public DateTime SetTime { get; set; }
        public DateTime Timestamp { get; } = DateTime.UtcNow;
        public string EventType => "CounterSet";
    }

    #endregion;

    /// <summary>
    /// Counter manager interface for dependency injection;
    /// </summary>
    public interface ICounterManager : IDisposable
    {
        int ActiveCounterCount { get; }
        int CategoryCount { get; }
        bool IsMonitoringActive { get; }
        CounterManagerConfiguration Configuration { get; }
        DateTime? LastCollectionTime { get; }
        long TotalSamplesCollected { get; }

        void Initialize();
        bool RegisterCounter(CounterInfo counterInfo);
        bool UnregisterCounter(string categoryName, string counterName, string instanceName = null);
        bool CreateCounterCategory(CounterCategoryInfo categoryInfo);
        bool DeleteCounterCategory(string categoryName);
        Task<List<CounterSample>> CollectAllCountersAsync();
        Task<CounterSample> CollectCounterAsync(
            string categoryName,
            string counterName,
            string instanceName = null);
        double GetCounterValue(string categoryName, string counterName, string instanceName = null);
        List<CounterSample> GetCounterHistory(
            string categoryName,
            string counterName,
            string instanceName = null,
            TimeSpan? timeRange = null);
        CounterStatistics GetCounterStatistics(
            string categoryName,
            string counterName,
            string instanceName = null);
        List<CounterInfo> GetAllCounterInfo();
        List<CounterCategoryInfo> GetAllCategoryInfo();
        void StartMonitoring();
        void StopMonitoring();
        void IncrementCounter(
            string categoryName,
            string counterName,
            string instanceName = null,
            long incrementBy = 1);
        void DecrementCounter(
            string categoryName,
            string counterName,
            string instanceName = null,
            long decrementBy = 1);
        void SetCounterValue(
            string categoryName,
            string counterName,
            string instanceName = null,
            double value = 0);
        Task<CounterDiagnostics> PerformDiagnosticsAsync();
        CounterDataExport ExportCounterData(
            ExportFormat format = ExportFormat.Json,
            TimeSpan? timeRange = null);
        PerformanceStatistics GetPerformanceStatistics();
        void ResetStatistics(string categoryName = null, string counterName = null, string instanceName = null);
    }

    #endregion;
}
