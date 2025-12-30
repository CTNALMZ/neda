using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Automation.TaskRouter;
using NEDA.Common.Utilities;
using NEDA.Core.Engine;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.NeuralNetwork.PatternRecognition;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.AdaptiveLearning.PerformanceAdaptation;
{
    /// <summary>
    /// Adaptive learning engine that dynamically adjusts system parameters;
    /// based on performance metrics and environmental conditions;
    /// </summary>
    public interface IAdaptiveEngine;
    {
        /// <summary>
        /// Starts the adaptive learning process;
        /// </summary>
        Task StartAdaptationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops the adaptive learning process;
        /// </summary>
        Task StopAdaptationAsync();

        /// <summary>
        /// Adapts system parameters based on current performance metrics;
        /// </summary>
        Task<AdaptationResult> AdaptAsync(PerformanceMetrics metrics, AdaptationContext context);

        /// <summary>
        /// Registers a new adaptation strategy;
        /// </summary>
        void RegisterStrategy(IAdaptationStrategy strategy);

        /// <summary>
        /// Gets current adaptation configuration;
        /// </summary>
        AdaptationConfig GetConfiguration();

        /// <summary>
        /// Updates adaptation configuration;
        /// </summary>
        Task UpdateConfigurationAsync(AdaptationConfig config);

        /// <summary>
        /// Event triggered when adaptation occurs;
        /// </summary>
        event EventHandler<AdaptationEventArgs> OnAdaptation;

        /// <summary>
        /// Event triggered when adaptation threshold is reached;
        /// </summary>
        event EventHandler<ThresholdExceededEventArgs> OnThresholdExceeded;
    }

    /// <summary>
    /// Main adaptive engine implementation;
    /// </summary>
    public class AdaptiveEngine : IAdaptiveEngine, IDisposable;
    {
        private readonly ILogger<AdaptiveEngine> _logger;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IEventBus _eventBus;
        private readonly AdaptiveEngineOptions _options;
        private readonly List<IAdaptationStrategy> _strategies;
        private readonly SemaphoreSlim _adaptationLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cancellationTokenSource;
        private Task _adaptationTask;
        private bool _isRunning;
        private DateTime _lastAdaptationTime;
        private AdaptationHistory _history;
        private AdaptationConfig _currentConfig;

        /// <summary>
        /// Initializes a new instance of the AdaptiveEngine;
        /// </summary>
        public AdaptiveEngine(
            ILogger<AdaptiveEngine> logger,
            IPerformanceMonitor performanceMonitor,
            IEventBus eventBus,
            IOptions<AdaptiveEngineOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _strategies = new List<IAdaptationStrategy>();
            _cancellationTokenSource = new CancellationTokenSource();
            _history = new AdaptationHistory();
            _currentConfig = LoadDefaultConfiguration();

            InitializeStrategies();
            _logger.LogInformation("AdaptiveEngine initialized with {StrategyCount} strategies", _strategies.Count);
        }

        /// <inheritdoc/>
        public event EventHandler<AdaptationEventArgs> OnAdaptation;

        /// <inheritdoc/>
        public event EventHandler<ThresholdExceededEventArgs> OnThresholdExceeded;

        /// <inheritdoc/>
        public async Task StartAdaptationAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Adaptation process is already running");
                return;
            }

            try
            {
                await _adaptationLock.WaitAsync(cancellationToken);
                _isRunning = true;
                _cancellationTokenSource.TryReset();

                _adaptationTask = Task.Run(async () =>
                {
                    await RunAdaptationLoopAsync(_cancellationTokenSource.Token);
                }, cancellationToken);

                _logger.LogInformation("AdaptiveEngine started successfully");
                await _eventBus.PublishAsync(new EngineStartedEvent;
                {
                    EngineName = nameof(AdaptiveEngine),
                    Timestamp = DateTime.UtcNow,
                    Configuration = _currentConfig;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start adaptation process");
                throw new AdaptiveEngineException("Failed to start adaptation process", ex);
            }
            finally
            {
                _adaptationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task StopAdaptationAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            try
            {
                await _adaptationLock.WaitAsync();
                _isRunning = false;
                _cancellationTokenSource.Cancel();

                if (_adaptationTask != null && !_adaptationTask.IsCompleted)
                {
                    await Task.WhenAny(_adaptationTask, Task.Delay(TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds)));
                }

                _logger.LogInformation("AdaptiveEngine stopped successfully");
                await _eventBus.PublishAsync(new EngineStoppedEvent;
                {
                    EngineName = nameof(AdaptiveEngine),
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while stopping adaptation process");
                throw new AdaptiveEngineException("Error while stopping adaptation process", ex);
            }
            finally
            {
                _adaptationLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<AdaptationResult> AdaptAsync(PerformanceMetrics metrics, AdaptationContext context)
        {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));
            if (context == null) throw new ArgumentNullException(nameof(context));

            await _adaptationLock.WaitAsync();
            try
            {
                _logger.LogDebug("Starting adaptation process with {@Metrics}", metrics);

                // Check if adaptation is needed based on thresholds;
                if (!ShouldAdapt(metrics, context))
                {
                    _logger.LogDebug("Adaptation not needed based on current metrics and thresholds");
                    return AdaptationResult.NotRequired;
                }

                // Select appropriate strategies;
                var applicableStrategies = SelectStrategies(metrics, context);
                if (!applicableStrategies.Any())
                {
                    _logger.LogWarning("No applicable strategies found for current adaptation context");
                    return AdaptationResult.NoStrategiesAvailable;
                }

                var results = new List<StrategyResult>();
                var overallSuccess = true;

                // Execute strategies in priority order;
                foreach (var strategy in applicableStrategies.OrderByDescending(s => s.Priority))
                {
                    try
                    {
                        _logger.LogInformation("Executing adaptation strategy: {StrategyName}", strategy.Name);
                        var result = await strategy.ExecuteAsync(metrics, context, _currentConfig);
                        results.Add(result);

                        if (!result.Success)
                        {
                            overallSuccess = false;
                            _logger.LogWarning("Strategy {StrategyName} failed: {Error}", strategy.Name, result.ErrorMessage);
                        }
                        else;
                        {
                            _logger.LogInformation("Strategy {StrategyName} completed successfully with impact: {Impact}",
                                strategy.Name, result.ImpactScore);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing strategy {StrategyName}", strategy.Name);
                        overallSuccess = false;
                        results.Add(new StrategyResult;
                        {
                            StrategyName = strategy.Name,
                            Success = false,
                            ErrorMessage = ex.Message,
                            ImpactScore = 0;
                        });
                    }
                }

                // Update history and configuration;
                var adaptationResult = new AdaptationResult;
                {
                    Success = overallSuccess,
                    Timestamp = DateTime.UtcNow,
                    AppliedStrategies = results,
                    MetricsBefore = metrics,
                    ConfigurationChanges = CalculateConfigurationChanges(results)
                };

                _history.AddEntry(adaptationResult);
                _lastAdaptationTime = DateTime.UtcNow;

                // Trigger events;
                OnAdaptation?.Invoke(this, new AdaptationEventArgs(adaptationResult));
                await _eventBus.PublishAsync(new AdaptationPerformedEvent;
                {
                    Result = adaptationResult,
                    Context = context,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Adaptation process completed with success: {Success}", overallSuccess);
                return adaptationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adaptation process failed");
                throw new AdaptiveEngineException("Adaptation process failed", ex);
            }
            finally
            {
                _adaptationLock.Release();
            }
        }

        /// <inheritdoc/>
        public void RegisterStrategy(IAdaptationStrategy strategy)
        {
            if (strategy == null) throw new ArgumentNullException(nameof(strategy));

            _strategies.Add(strategy);
            _logger.LogDebug("Registered adaptation strategy: {StrategyName}", strategy.Name);
        }

        /// <inheritdoc/>
        public AdaptationConfig GetConfiguration()
        {
            return _currentConfig.Clone();
        }

        /// <inheritdoc/>
        public async Task UpdateConfigurationAsync(AdaptationConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            await _adaptationLock.WaitAsync();
            try
            {
                if (!config.Validate())
                {
                    throw new ArgumentException("Invalid configuration provided");
                }

                var oldConfig = _currentConfig;
                _currentConfig = config;

                _logger.LogInformation("Adaptation configuration updated");
                await _eventBus.PublishAsync(new ConfigurationUpdatedEvent;
                {
                    OldConfiguration = oldConfig,
                    NewConfiguration = config,
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                _adaptationLock.Release();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _adaptationLock?.Dispose();
            _adaptationTask?.Dispose();
            GC.SuppressFinalize(this);
        }

        #region Private Methods;

        private async Task RunAdaptationLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting adaptation monitoring loop");

            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    // Wait for next adaptation cycle;
                    await Task.Delay(_options.AdaptationInterval, cancellationToken);

                    // Collect current performance metrics;
                    var metrics = await _performanceMonitor.GetCurrentMetricsAsync();
                    var context = CreateAdaptationContext();

                    // Check if we should perform adaptation;
                    if (ShouldPerformPeriodicAdaptation(metrics))
                    {
                        await AdaptAsync(metrics, context);
                    }

                    // Check for threshold violations;
                    await CheckThresholdsAsync(metrics);
                }
                catch (TaskCanceledException)
                {
                    // Normal shutdown;
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in adaptation loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }

            _logger.LogInformation("Adaptation monitoring loop stopped");
        }

        private bool ShouldAdapt(PerformanceMetrics metrics, AdaptationContext context)
        {
            // Check minimum time between adaptations;
            if ((DateTime.UtcNow - _lastAdaptationTime).TotalSeconds < _options.MinAdaptationIntervalSeconds)
            {
                return false;
            }

            // Check performance thresholds;
            if (metrics.CpuUsage < _options.CpuThreshold &&
                metrics.MemoryUsage < _options.MemoryThreshold &&
                metrics.ResponseTime < _options.ResponseTimeThreshold)
            {
                return false;
            }

            // Check if system is in stable state;
            if (!context.IsSystemStable)
            {
                _logger.LogWarning("System is not in stable state, postponing adaptation");
                return false;
            }

            return true;
        }

        private bool ShouldPerformPeriodicAdaptation(PerformanceMetrics metrics)
        {
            var timeSinceLastAdaptation = DateTime.UtcNow - _lastAdaptationTime;

            // Force adaptation if thresholds are severely exceeded;
            if (metrics.CpuUsage > _options.CpuThreshold * 1.5 ||
                metrics.MemoryUsage > _options.MemoryThreshold * 1.5)
            {
                return true;
            }

            // Perform periodic adaptation based on interval;
            return timeSinceLastAdaptation.TotalMinutes >= _options.AdaptationInterval.TotalMinutes;
        }

        private async Task CheckThresholdsAsync(PerformanceMetrics metrics)
        {
            var thresholdsExceeded = new List<ThresholdExceeded>();

            if (metrics.CpuUsage > _options.CpuThreshold)
            {
                thresholdsExceeded.Add(new ThresholdExceeded;
                {
                    MetricName = "CPU",
                    CurrentValue = metrics.CpuUsage,
                    ThresholdValue = _options.CpuThreshold,
                    Severity = CalculateSeverity(metrics.CpuUsage, _options.CpuThreshold)
                });
            }

            if (metrics.MemoryUsage > _options.MemoryThreshold)
            {
                thresholdsExceeded.Add(new ThresholdExceeded;
                {
                    MetricName = "Memory",
                    CurrentValue = metrics.MemoryUsage,
                    ThresholdValue = _options.MemoryThreshold,
                    Severity = CalculateSeverity(metrics.MemoryUsage, _options.MemoryThreshold)
                });
            }

            if (metrics.ResponseTime > _options.ResponseTimeThreshold)
            {
                thresholdsExceeded.Add(new ThresholdExceeded;
                {
                    MetricName = "ResponseTime",
                    CurrentValue = metrics.ResponseTime,
                    ThresholdValue = _options.ResponseTimeThreshold,
                    Severity = CalculateSeverity(metrics.ResponseTime, _options.ResponseTimeThreshold)
                });
            }

            if (thresholdsExceeded.Any())
            {
                var args = new ThresholdExceededEventArgs(thresholdsExceeded);
                OnThresholdExceeded?.Invoke(this, args);

                await _eventBus.PublishAsync(new ThresholdExceededEvent;
                {
                    Thresholds = thresholdsExceeded,
                    Metrics = metrics,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private SeverityLevel CalculateSeverity(double currentValue, double threshold)
        {
            var ratio = currentValue / threshold;

            if (ratio > 2.0) return SeverityLevel.Critical;
            if (ratio > 1.5) return SeverityLevel.High;
            if (ratio > 1.2) return SeverityLevel.Medium;
            return SeverityLevel.Low;
        }

        private IEnumerable<IAdaptationStrategy> SelectStrategies(PerformanceMetrics metrics, AdaptationContext context)
        {
            var selectedStrategies = new List<IAdaptationStrategy>();

            foreach (var strategy in _strategies)
            {
                if (strategy.CanExecute(metrics, context))
                {
                    selectedStrategies.Add(strategy);
                }
            }

            // Limit number of strategies to prevent over-adaptation;
            return selectedStrategies.Take(_options.MaxStrategiesPerAdaptation);
        }

        private AdaptationContext CreateAdaptationContext()
        {
            return new AdaptationContext;
            {
                Timestamp = DateTime.UtcNow,
                SystemLoad = CalculateSystemLoad(),
                IsSystemStable = IsSystemStable(),
                PreviousAdaptations = _history.GetRecentEntries(10),
                Environment = GetEnvironmentInfo()
            };
        }

        private double CalculateSystemLoad()
        {
            // Complex system load calculation based on multiple factors;
            return 0.0; // Placeholder - actual implementation would use performance counters;
        }

        private bool IsSystemStable()
        {
            // Check if system has been stable for minimum period;
            var recentHistory = _history.GetRecentEntries(5);
            if (recentHistory.Count < 5) return true;

            return recentHistory.All(h => h.Success &&
                h.AppliedStrategies.All(s => s.Success));
        }

        private Dictionary<string, object> CalculateConfigurationChanges(List<StrategyResult> results)
        {
            var changes = new Dictionary<string, object>();

            foreach (var result in results.Where(r => r.ConfigurationChanges != null))
            {
                foreach (var change in result.ConfigurationChanges)
                {
                    changes[change.Key] = change.Value;
                }
            }

            return changes;
        }

        private void InitializeStrategies()
        {
            // Register default strategies;
            RegisterStrategy(new LoadBalancingStrategy(_logger, _options));
            RegisterStrategy(new ResourceOptimizationStrategy(_logger, _options));
            RegisterStrategy(new LearningRateAdjustmentStrategy(_logger, _options));
            RegisterStrategy(new BatchSizeOptimizationStrategy(_logger, _options));
            RegisterStrategy(new ModelComplexityStrategy(_logger, _options));
        }

        private AdaptationConfig LoadDefaultConfiguration()
        {
            return new AdaptationConfig;
            {
                AdaptationMode = AdaptationMode.Auto,
                Aggressiveness = AggressivenessLevel.Moderate,
                MaxAdaptationsPerHour = 5,
                EnableRollback = true,
                RollbackTimeout = TimeSpan.FromMinutes(5),
                PerformanceTargets = new PerformanceTargets;
                {
                    TargetCpuUsage = 70.0,
                    TargetMemoryUsage = 75.0,
                    TargetResponseTime = TimeSpan.FromMilliseconds(100),
                    TargetAccuracy = 95.0;
                }
            };
        }

        private SystemEnvironment GetEnvironmentInfo()
        {
            return new SystemEnvironment;
            {
                Runtime = Environment.Version.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                TotalMemory = GC.GetTotalMemory(false),
                Is64Bit = Environment.Is64BitProcess,
                MachineName = Environment.MachineName;
            };
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    /// <summary>
    /// Adaptation strategy interface;
    /// </summary>
    public interface IAdaptationStrategy;
    {
        string Name { get; }
        string Description { get; }
        int Priority { get; }
        bool CanExecute(PerformanceMetrics metrics, AdaptationContext context);
        Task<StrategyResult> ExecuteAsync(PerformanceMetrics metrics, AdaptationContext context, AdaptationConfig config);
    }

    /// <summary>
    /// Performance metrics for adaptation decisions;
    /// </summary>
    public class PerformanceMetrics;
    {
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double NetworkThroughput { get; set; }
        public TimeSpan ResponseTime { get; set; }
        public double ErrorRate { get; set; }
        public int ActiveConnections { get; set; }
        public double ProcessingRate { get; set; }
        public double ModelAccuracy { get; set; }
        public double TrainingLoss { get; set; }
        public double ValidationLoss { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Context for adaptation decisions;
    /// </summary>
    public class AdaptationContext;
    {
        public DateTime Timestamp { get; set; }
        public double SystemLoad { get; set; }
        public bool IsSystemStable { get; set; }
        public List<AdaptationResult> PreviousAdaptations { get; set; }
        public SystemEnvironment Environment { get; set; }
        public Dictionary<string, object> CustomData { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Result of an adaptation operation;
    /// </summary>
    public class AdaptationResult;
    {
        public bool Success { get; set; }
        public DateTime Timestamp { get; set; }
        public List<StrategyResult> AppliedStrategies { get; set; }
        public PerformanceMetrics MetricsBefore { get; set; }
        public Dictionary<string, object> ConfigurationChanges { get; set; }
        public TimeSpan Duration { get; set; }
        public string ErrorMessage { get; set; }

        public static AdaptationResult NotRequired => new AdaptationResult { Success = true };
        public static AdaptationResult NoStrategiesAvailable => new AdaptationResult { Success = false, ErrorMessage = "No strategies available" };
    }

    /// <summary>
    /// Result of an individual strategy execution;
    /// </summary>
    public class StrategyResult;
    {
        public string StrategyName { get; set; }
        public bool Success { get; set; }
        public double ImpactScore { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> ConfigurationChanges { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    /// <summary>
    /// Adaptation engine configuration;
    /// </summary>
    public class AdaptationConfig : ICloneable;
    {
        public AdaptationMode AdaptationMode { get; set; }
        public AggressivenessLevel Aggressiveness { get; set; }
        public int MaxAdaptationsPerHour { get; set; }
        public bool EnableRollback { get; set; }
        public TimeSpan RollbackTimeout { get; set; }
        public PerformanceTargets PerformanceTargets { get; set; }

        public bool Validate()
        {
            return MaxAdaptationsPerHour > 0 &&
                   MaxAdaptationsPerHour <= 60 &&
                   RollbackTimeout > TimeSpan.Zero &&
                   PerformanceTargets != null;
        }

        public object Clone()
        {
            return new AdaptationConfig;
            {
                AdaptationMode = AdaptationMode,
                Aggressiveness = Aggressiveness,
                MaxAdaptationsPerHour = MaxAdaptationsPerHour,
                EnableRollback = EnableRollback,
                RollbackTimeout = RollbackTimeout,
                PerformanceTargets = PerformanceTargets?.Clone() as PerformanceTargets;
            };
        }
    }

    /// <summary>
    /// Performance targets for adaptation;
    /// </summary>
    public class PerformanceTargets : ICloneable;
    {
        public double TargetCpuUsage { get; set; }
        public double TargetMemoryUsage { get; set; }
        public TimeSpan TargetResponseTime { get; set; }
        public double TargetAccuracy { get; set; }
        public double TargetThroughput { get; set; }

        public object Clone()
        {
            return new PerformanceTargets;
            {
                TargetCpuUsage = TargetCpuUsage,
                TargetMemoryUsage = TargetMemoryUsage,
                TargetResponseTime = TargetResponseTime,
                TargetAccuracy = TargetAccuracy,
                TargetThroughput = TargetThroughput;
            };
        }
    }

    /// <summary>
    /// Options for adaptive engine;
    /// </summary>
    public class AdaptiveEngineOptions;
    {
        public TimeSpan AdaptationInterval { get; set; } = TimeSpan.FromMinutes(5);
        public int MinAdaptationIntervalSeconds { get; set; } = 60;
        public int ShutdownTimeoutSeconds { get; set; } = 30;
        public double CpuThreshold { get; set; } = 80.0;
        public double MemoryThreshold { get; set; } = 85.0;
        public int ResponseTimeThreshold { get; set; } = 200; // milliseconds;
        public int MaxStrategiesPerAdaptation { get; set; } = 3;
        public bool EnableDetailedLogging { get; set; } = true;
    }

    /// <summary>
    /// System environment information;
    /// </summary>
    public class SystemEnvironment;
    {
        public string Runtime { get; set; }
        public int ProcessorCount { get; set; }
        public long TotalMemory { get; set; }
        public bool Is64Bit { get; set; }
        public string MachineName { get; set; }
    }

    /// <summary>
    /// Threshold exceeded information;
    /// </summary>
    public class ThresholdExceeded;
    {
        public string MetricName { get; set; }
        public double CurrentValue { get; set; }
        public double ThresholdValue { get; set; }
        public SeverityLevel Severity { get; set; }
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Event arguments for adaptation events;
    /// </summary>
    public class AdaptationEventArgs : EventArgs;
    {
        public AdaptationResult Result { get; }

        public AdaptationEventArgs(AdaptationResult result)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }
    }

    /// <summary>
    /// Event arguments for threshold exceeded events;
    /// </summary>
    public class ThresholdExceededEventArgs : EventArgs;
    {
        public List<ThresholdExceeded> Thresholds { get; }

        public ThresholdExceededEventArgs(List<ThresholdExceeded> thresholds)
        {
            Thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
        }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Adaptation mode;
    /// </summary>
    public enum AdaptationMode;
    {
        Auto,
        Manual,
        SemiAuto;
    }

    /// <summary>
    /// Aggressiveness level for adaptations;
    /// </summary>
    public enum AggressivenessLevel;
    {
        Conservative,
        Moderate,
        Aggressive;
    }

    /// <summary>
    /// Severity level for threshold violations;
    /// </summary>
    public enum SeverityLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Engine started event;
    /// </summary>
    public class EngineStartedEvent : IEvent;
    {
        public string EngineName { get; set; }
        public DateTime Timestamp { get; set; }
        public AdaptationConfig Configuration { get; set; }
    }

    /// <summary>
    /// Engine stopped event;
    /// </summary>
    public class EngineStoppedEvent : IEvent;
    {
        public string EngineName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Adaptation performed event;
    /// </summary>
    public class AdaptationPerformedEvent : IEvent;
    {
        public AdaptationResult Result { get; set; }
        public AdaptationContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Threshold exceeded event;
    /// </summary>
    public class ThresholdExceededEvent : IEvent;
    {
        public List<ThresholdExceeded> Thresholds { get; set; }
        public PerformanceMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Configuration updated event;
    /// </summary>
    public class ConfigurationUpdatedEvent : IEvent;
    {
        public AdaptationConfig OldConfiguration { get; set; }
        public AdaptationConfig NewConfiguration { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Adaptive engine specific exception;
    /// </summary>
    public class AdaptiveEngineException : Exception
    {
        public AdaptiveEngineException(string message) : base(message) { }
        public AdaptiveEngineException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Internal Classes;

    /// <summary>
    /// Tracks adaptation history;
    /// </summary>
    internal class AdaptationHistory;
    {
        private readonly List<AdaptationResult> _history;
        private readonly int _maxEntries;

        public AdaptationHistory(int maxEntries = 1000)
        {
            _maxEntries = maxEntries;
            _history = new List<AdaptationResult>(maxEntries);
        }

        public void AddEntry(AdaptationResult result)
        {
            lock (_history)
            {
                _history.Add(result);

                // Maintain maximum size;
                if (_history.Count > _maxEntries)
                {
                    _history.RemoveRange(0, _history.Count - _maxEntries);
                }
            }
        }

        public List<AdaptationResult> GetRecentEntries(int count)
        {
            lock (_history)
            {
                return _history;
                    .OrderByDescending(h => h.Timestamp)
                    .Take(Math.Min(count, _history.Count))
                    .ToList();
            }
        }

        public AdaptationResult GetLastSuccessfulAdaptation()
        {
            lock (_history)
            {
                return _history;
                    .Where(h => h.Success)
                    .OrderByDescending(h => h.Timestamp)
                    .FirstOrDefault();
            }
        }
    }

    #endregion;
}
