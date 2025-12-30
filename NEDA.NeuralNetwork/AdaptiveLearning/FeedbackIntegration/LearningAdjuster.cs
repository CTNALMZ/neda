using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.NeuralNetwork.AdaptiveLearning.FeedbackIntegration;
{
    /// <summary>
    /// Adjustment strategies for learning parameters;
    /// </summary>
    public enum LearningAdjustmentStrategy;
    {
        Conservative,
        Moderate,
        Aggressive,
        Adaptive;
    }

    /// <summary>
    /// Types of feedback that can influence learning adjustments;
    /// </summary>
    public enum FeedbackType;
    {
        PerformanceMetrics,
        UserFeedback,
        SystemConstraints,
        ResourceUtilization,
        ErrorRate,
        ConvergenceRate;
    }

    /// <summary>
    /// Configuration options for LearningAdjuster;
    /// </summary>
    public class LearningAdjusterOptions;
    {
        public double InitialLearningRate { get; set; } = 0.001;
        public double MinLearningRate { get; set; } = 1e-6;
        public double MaxLearningRate { get; set; } = 0.1;
        public double LearningRateDecayFactor { get; set; } = 0.95;
        public double LearningRateIncreaseFactor { get; set; } = 1.05;
        public int PatienceEpochs { get; set; } = 10;
        public double PerformanceThreshold { get; set; } = 0.85;
        public double ErrorThreshold { get; set; } = 0.15;
        public LearningAdjustmentStrategy DefaultStrategy { get; set; } = LearningAdjustmentStrategy.Moderate;
        public TimeSpan AdjustmentInterval { get; set; } = TimeSpan.FromMinutes(5);
        public bool EnableDynamicBatchSize { get; set; } = true;
        public int MinBatchSize { get; set; } = 16;
        public int MaxBatchSize { get; set; } = 1024;
        public Dictionary<FeedbackType, double> FeedbackWeights { get; set; } = new()
        {
            [FeedbackType.PerformanceMetrics] = 0.4,
            [FeedbackType.UserFeedback] = 0.2,
            [FeedbackType.SystemConstraints] = 0.15,
            [FeedbackType.ResourceUtilization] = 0.1,
            [FeedbackType.ErrorRate] = 0.1,
            [FeedbackType.ConvergenceRate] = 0.05;
        };
    }

    /// <summary>
    /// Represents feedback from various sources;
    /// </summary>
    public class LearningFeedback;
    {
        public FeedbackType Type { get; set; }
        public double Score { get; set; } // 0.0 to 1.0;
        public double Confidence { get; set; } = 1.0;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Result of a learning adjustment operation;
    /// </summary>
    public class AdjustmentResult;
    {
        public bool Adjusted { get; set; }
        public string Parameter { get; set; }
        public double OldValue { get; set; }
        public double NewValue { get; set; }
        public double AdjustmentFactor { get; set; }
        public string Reason { get; set; }
        public DateTime AppliedAt { get; set; }
        public Dictionary<FeedbackType, double> InfluencingFactors { get; set; } = new();
    }

    /// <summary>
    /// Current learning state snapshot;
    /// </summary>
    public class LearningState;
    {
        public string ModelId { get; set; }
        public double CurrentLearningRate { get; set; }
        public int CurrentBatchSize { get; set; }
        public int CurrentEpoch { get; set; }
        public double CurrentLoss { get; set; }
        public double CurrentAccuracy { get; set; }
        public double AverageFeedbackScore { get; set; }
        public Dictionary<FeedbackType, double> FeedbackScores { get; set; } = new();
        public DateTime LastAdjustment { get; set; }
        public List<AdjustmentResult> RecentAdjustments { get; set; } = new();
    }

    /// <summary>
    /// Adjusts learning parameters dynamically based on integrated feedback from multiple sources;
    /// </summary>
    public class LearningAdjuster : ILearningAdjuster, IDisposable;
    {
        private readonly IFeedbackEngine _feedbackEngine;
        private readonly IPerformanceFeedback _performanceFeedback;
        private readonly ILogger<LearningAdjuster> _logger;
        private readonly LearningAdjusterOptions _options;
        private readonly ConcurrentDictionary<string, LearningState> _learningStates;
        private readonly Timer _adjustmentTimer;
        private readonly Random _random;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of LearningAdjuster;
        /// </summary>
        public LearningAdjuster(
            IFeedbackEngine feedbackEngine,
            IPerformanceFeedback performanceFeedback,
            ILogger<LearningAdjuster> logger,
            IOptions<LearningAdjusterOptions> options)
        {
            _feedbackEngine = feedbackEngine ?? throw new ArgumentNullException(nameof(feedbackEngine));
            _performanceFeedback = performanceFeedback ?? throw new ArgumentNullException(nameof(performanceFeedback));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _learningStates = new ConcurrentDictionary<string, LearningState>();
            _random = new Random();

            // Start periodic adjustment timer;
            _adjustmentTimer = new Timer(
                PerformPeriodicAdjustments,
                null,
                _options.AdjustmentInterval,
                _options.AdjustmentInterval);

            _logger.LogInformation("LearningAdjuster initialized with strategy: {Strategy}",
                _options.DefaultStrategy);
        }

        /// <summary>
        /// Adjusts learning parameters for a specific model based on recent feedback;
        /// </summary>
        public async Task<AdjustmentResult> AdjustLearningAsync(
            string modelId,
            LearningAdjustmentStrategy? strategy = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Model ID cannot be null or empty", nameof(modelId));

            var actualStrategy = strategy ?? _options.DefaultStrategy;

            try
            {
                _logger.LogDebug("Starting learning adjustment for model {ModelId} with strategy {Strategy}",
                    modelId, actualStrategy);

                // Get current learning state;
                var currentState = await GetCurrentLearningStateAsync(modelId, cancellationToken);

                // Collect recent feedback;
                var feedbacks = await _feedbackEngine.GetRecentFeedbackAsync(
                    modelId,
                    TimeSpan.FromMinutes(30),
                    cancellationToken);

                // Analyze feedback and calculate adjustment;
                var analysis = await AnalyzeFeedbackAsync(feedbacks, currentState, cancellationToken);

                // Determine if adjustment is needed;
                if (!ShouldAdjust(analysis, currentState, actualStrategy))
                {
                    _logger.LogDebug("No adjustment needed for model {ModelId}", modelId);
                    return new AdjustmentResult;
                    {
                        Adjusted = false,
                        Parameter = "None",
                        Reason = "No significant change detected"
                    };
                }

                // Calculate new parameters;
                var newParameters = CalculateNewParameters(
                    currentState,
                    analysis,
                    actualStrategy);

                // Apply adjustments;
                var result = await ApplyAdjustmentsAsync(
                    modelId,
                    currentState,
                    newParameters,
                    analysis,
                    cancellationToken);

                // Update learning state;
                await UpdateLearningStateAsync(modelId, result, newParameters, cancellationToken);

                _logger.LogInformation("Learning adjusted for model {ModelId}: {Parameter} from {OldValue} to {NewValue}",
                    modelId, result.Parameter, result.OldValue, result.NewValue);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adjusting learning for model {ModelId}", modelId);
                throw new LearningAdjustmentException($"Failed to adjust learning for model {modelId}", ex);
            }
        }

        /// <summary>
        /// Gets the current learning state for a model;
        /// </summary>
        public async Task<LearningState> GetLearningStateAsync(string modelId, CancellationToken cancellationToken = default)
        {
            if (!_learningStates.TryGetValue(modelId, out var state))
            {
                state = await InitializeLearningStateAsync(modelId, cancellationToken);
            }

            return state;
        }

        /// <summary>
        /// Updates feedback weights dynamically;
        /// </summary>
        public void UpdateFeedbackWeights(Dictionary<FeedbackType, double> newWeights)
        {
            if (newWeights == null || newWeights.Count == 0)
                throw new ArgumentException("Weights cannot be null or empty", nameof(newWeights));

            var totalWeight = newWeights.Values.Sum();
            if (Math.Abs(totalWeight - 1.0) > 0.01)
                throw new ArgumentException("Feedback weights must sum to 1.0", nameof(newWeights));

            lock (_options.FeedbackWeights)
            {
                foreach (var kvp in newWeights)
                {
                    _options.FeedbackWeights[kvp.Key] = kvp.Value;
                }
            }

            _logger.LogInformation("Feedback weights updated: {Weights}",
                string.Join(", ", newWeights.Select(w => $"{w.Key}: {w.Value:P}")));
        }

        /// <summary>
        /// Resets learning parameters to defaults for a model;
        /// </summary>
        public async Task ResetLearningParametersAsync(string modelId, CancellationToken cancellationToken = default)
        {
            var resetResult = new AdjustmentResult;
            {
                Adjusted = true,
                Parameter = "All",
                OldValue = 0,
                NewValue = _options.InitialLearningRate,
                AdjustmentFactor = 1.0,
                Reason = "Manual reset",
                AppliedAt = DateTime.UtcNow;
            };

            var initialState = new LearningState;
            {
                ModelId = modelId,
                CurrentLearningRate = _options.InitialLearningRate,
                CurrentBatchSize = _options.MinBatchSize,
                CurrentEpoch = 0,
                CurrentLoss = 0,
                CurrentAccuracy = 0,
                LastAdjustment = DateTime.UtcNow,
                RecentAdjustments = new List<AdjustmentResult> { resetResult }
            };

            _learningStates.AddOrUpdate(modelId, initialState, (k, v) => initialState);

            await _performanceFeedback.ResetMetricsAsync(modelId, cancellationToken);

            _logger.LogInformation("Learning parameters reset for model {ModelId}", modelId);
        }

        /// <summary>
        /// Performs periodic adjustments for all active models;
        /// </summary>
        private async void PerformPeriodicAdjustments(object state)
        {
            try
            {
                foreach (var modelId in _learningStates.Keys.ToList())
                {
                    try
                    {
                        await AdjustLearningAsync(modelId, cancellationToken: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to periodically adjust learning for model {ModelId}", modelId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic adjustment cycle");
            }
        }

        /// <summary>
        /// Analyzes collected feedback to determine adjustment needs;
        /// </summary>
        private async Task<FeedbackAnalysis> AnalyzeFeedbackAsync(
            IEnumerable<LearningFeedback> feedbacks,
            LearningState currentState,
            CancellationToken cancellationToken)
        {
            var analysis = new FeedbackAnalysis();
            var feedbackList = feedbacks.ToList();

            if (!feedbackList.Any())
                return analysis;

            // Group feedback by type;
            var groupedFeedback = feedbackList;
                .GroupBy(f => f.Type)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Calculate weighted scores for each feedback type;
            foreach (var feedbackType in Enum.GetValues<FeedbackType>())
            {
                if (groupedFeedback.TryGetValue(feedbackType, out var typeFeedbacks))
                {
                    var averageScore = typeFeedbacks.Average(f => f.Score * f.Confidence);
                    var weight = _options.FeedbackWeights.GetValueOrDefault(feedbackType, 0.1);

                    analysis.FeedbackScores[feedbackType] = averageScore;
                    analysis.WeightedScores[feedbackType] = averageScore * weight;

                    // Store specific analysis based on feedback type;
                    AnalyzeSpecificFeedback(feedbackType, typeFeedbacks, analysis);
                }
            }

            analysis.OverallScore = analysis.WeightedScores.Values.Sum();
            analysis.IsImproving = await IsPerformanceImprovingAsync(
                currentState.ModelId,
                analysis.OverallScore,
                cancellationToken);

            return analysis;
        }

        /// <summary>
        /// Determines if adjustment should be performed;
        /// </summary>
        private bool ShouldAdjust(
            FeedbackAnalysis analysis,
            LearningState currentState,
            LearningAdjustmentStrategy strategy)
        {
            if (currentState.RecentAdjustments.Count >= 3)
            {
                var recentAdjustments = currentState.RecentAdjustments;
                    .OrderByDescending(a => a.AppliedAt)
                    .Take(3)
                    .ToList();

                // Check if recent adjustments were too frequent;
                if (recentAdjustments.All(a => a.AppliedAt > DateTime.UtcNow.AddHours(-1)))
                {
                    _logger.LogDebug("Skipping adjustment due to frequent recent adjustments");
                    return false;
                }
            }

            var timeSinceLastAdjustment = DateTime.UtcNow - currentState.LastAdjustment;
            if (timeSinceLastAdjustment < TimeSpan.FromMinutes(2))
            {
                _logger.LogDebug("Skipping adjustment due to minimum time not elapsed");
                return false;
            }

            var scoreChange = Math.Abs(analysis.OverallScore - currentState.AverageFeedbackScore);

            return strategy switch;
            {
                LearningAdjustmentStrategy.Conservative => scoreChange > 0.2,
                LearningAdjustmentStrategy.Moderate => scoreChange > 0.1,
                LearningAdjustmentStrategy.Aggressive => scoreChange > 0.05,
                LearningAdjustmentStrategy.Adaptive => scoreChange > CalculateAdaptiveThreshold(currentState),
                _ => scoreChange > 0.1;
            };
        }

        /// <summary>
        /// Calculates new learning parameters based on analysis;
        /// </summary>
        private LearningParameters CalculateNewParameters(
            LearningState currentState,
            FeedbackAnalysis analysis,
            LearningAdjustmentStrategy strategy)
        {
            var parameters = new LearningParameters;
            {
                LearningRate = currentState.CurrentLearningRate,
                BatchSize = currentState.CurrentBatchSize;
            };

            var adjustmentFactor = CalculateAdjustmentFactor(analysis, strategy);

            // Adjust learning rate;
            if (analysis.OverallScore < _options.PerformanceThreshold)
            {
                parameters.LearningRate *= adjustmentFactor;
                parameters.LearningRate = Math.Max(
                    _options.MinLearningRate,
                    Math.Min(parameters.LearningRate, _options.MaxLearningRate));
            }

            // Adjust batch size if enabled;
            if (_options.EnableDynamicBatchSize)
            {
                var resourceScore = analysis.FeedbackScores.GetValueOrDefault(FeedbackType.ResourceUtilization, 0.5);

                if (resourceScore > 0.7 && currentState.CurrentBatchSize < _options.MaxBatchSize)
                {
                    // Increase batch size if resources are underutilized;
                    parameters.BatchSize = Math.Min(
                        _options.MaxBatchSize,
                        currentState.CurrentBatchSize * 2);
                }
                else if (resourceScore < 0.3 && currentState.CurrentBatchSize > _options.MinBatchSize)
                {
                    // Decrease batch size if resources are overutilized;
                    parameters.BatchSize = Math.Max(
                        _options.MinBatchSize,
                        currentState.CurrentBatchSize / 2);
                }
            }

            return parameters;
        }

        /// <summary>
        /// Applies calculated adjustments to the learning process;
        /// </summary>
        private async Task<AdjustmentResult> ApplyAdjustmentsAsync(
            string modelId,
            LearningState currentState,
            LearningParameters newParameters,
            FeedbackAnalysis analysis,
            CancellationToken cancellationToken)
        {
            var result = new AdjustmentResult;
            {
                Adjusted = true,
                AppliedAt = DateTime.UtcNow;
            };

            // Determine which parameter to adjust;
            var learningRateChange = Math.Abs(newParameters.LearningRate - currentState.CurrentLearningRate) / currentState.CurrentLearningRate;
            var batchSizeChange = Math.Abs(newParameters.BatchSize - currentState.CurrentBatchSize) / (double)currentState.CurrentBatchSize;

            if (learningRateChange > batchSizeChange && learningRateChange > 0.01)
            {
                result.Parameter = "LearningRate";
                result.OldValue = currentState.CurrentLearningRate;
                result.NewValue = newParameters.LearningRate;
                result.AdjustmentFactor = newParameters.LearningRate / currentState.CurrentLearningRate;
                result.Reason = $"Feedback score: {analysis.OverallScore:F2}, Strategy: {analysis.IsImproving}";

                await _performanceFeedback.UpdateLearningRateAsync(
                    modelId,
                    newParameters.LearningRate,
                    cancellationToken);
            }
            else if (batchSizeChange > 0.01)
            {
                result.Parameter = "BatchSize";
                result.OldValue = currentState.CurrentBatchSize;
                result.NewValue = newParameters.BatchSize;
                result.AdjustmentFactor = (double)newParameters.BatchSize / currentState.CurrentBatchSize;
                result.Reason = $"Resource utilization feedback";

                await _performanceFeedback.UpdateBatchSizeAsync(
                    modelId,
                    newParameters.BatchSize,
                    cancellationToken);
            }
            else;
            {
                result.Adjusted = false;
                result.Parameter = "None";
                result.Reason = "Changes below threshold";
            }

            // Record influencing factors;
            foreach (var kvp in analysis.WeightedScores.OrderByDescending(x => x.Value).Take(3))
            {
                result.InfluencingFactors[kvp.Key] = kvp.Value;
            }

            return result;
        }

        /// <summary>
        /// Updates the learning state after adjustment;
        /// </summary>
        private async Task UpdateLearningStateAsync(
            string modelId,
            AdjustmentResult result,
            LearningParameters newParameters,
            CancellationToken cancellationToken)
        {
            if (!_learningStates.TryGetValue(modelId, out var state))
                return;

            if (result.Adjusted)
            {
                state.CurrentLearningRate = newParameters.LearningRate;
                state.CurrentBatchSize = newParameters.BatchSize;
                state.LastAdjustment = result.AppliedAt;

                state.RecentAdjustments.Add(result);
                if (state.RecentAdjustments.Count > 10)
                {
                    state.RecentAdjustments = state.RecentAdjustments;
                        .OrderByDescending(a => a.AppliedAt)
                        .Take(10)
                        .ToList();
                }

                // Update metrics;
                var metrics = await _performanceFeedback.GetCurrentMetricsAsync(modelId, cancellationToken);
                state.CurrentLoss = metrics.Loss;
                state.CurrentAccuracy = metrics.Accuracy;

                // Update feedback scores;
                state.AverageFeedbackScore = result.InfluencingFactors.Values.Average();
            }

            _learningStates[modelId] = state;
        }

        /// <summary>
        /// Initializes a new learning state for a model;
        /// </summary>
        private async Task<LearningState> InitializeLearningStateAsync(
            string modelId,
            CancellationToken cancellationToken)
        {
            var metrics = await _performanceFeedback.GetCurrentMetricsAsync(modelId, cancellationToken);

            var initialState = new LearningState;
            {
                ModelId = modelId,
                CurrentLearningRate = _options.InitialLearningRate,
                CurrentBatchSize = _options.MinBatchSize,
                CurrentEpoch = metrics.Epoch,
                CurrentLoss = metrics.Loss,
                CurrentAccuracy = metrics.Accuracy,
                AverageFeedbackScore = 0.5,
                LastAdjustment = DateTime.UtcNow.AddDays(-1),
                RecentAdjustments = new List<AdjustmentResult>()
            };

            _learningStates.TryAdd(modelId, initialState);
            return initialState;
        }

        /// <summary>
        /// Gets current learning state with updated metrics;
        /// </summary>
        private async Task<LearningState> GetCurrentLearningStateAsync(
            string modelId,
            CancellationToken cancellationToken)
        {
            var state = await GetLearningStateAsync(modelId, cancellationToken);
            var metrics = await _performanceFeedback.GetCurrentMetricsAsync(modelId, cancellationToken);

            state.CurrentEpoch = metrics.Epoch;
            state.CurrentLoss = metrics.Loss;
            state.CurrentAccuracy = metrics.Accuracy;

            return state;
        }

        /// <summary>
        /// Checks if performance is improving;
        /// </summary>
        private async Task<bool> IsPerformanceImprovingAsync(
            string modelId,
            double currentScore,
            CancellationToken cancellationToken)
        {
            var historical = await _performanceFeedback.GetHistoricalMetricsAsync(
                modelId,
                TimeSpan.FromHours(6),
                cancellationToken);

            if (historical.Count < 3)
                return true;

            var recentScores = historical;
                .OrderByDescending(h => h.Timestamp)
                .Take(3)
                .Select(h => h.Accuracy)
                .ToList();

            return recentScores.Zip(recentScores.Skip(1), (a, b) => a >= b).All(x => x);
        }

        /// <summary>
        /// Calculates adaptive threshold based on current state;
        /// </summary>
        private double CalculateAdaptiveThreshold(LearningState state)
        {
            var epochFactor = Math.Min(state.CurrentEpoch / 100.0, 1.0);
            var stabilityFactor = 1.0 - (state.RecentAdjustments.Count / 20.0);

            return 0.05 + (0.15 * epochFactor * stabilityFactor);
        }

        /// <summary>
        /// Calculates adjustment factor based on strategy;
        /// </summary>
        private double CalculateAdjustmentFactor(
            FeedbackAnalysis analysis,
            LearningAdjustmentStrategy strategy)
        {
            var baseFactor = analysis.OverallScore < 0.3 ? 0.5 :
                           analysis.OverallScore < 0.6 ? 0.8 :
                           analysis.OverallScore < 0.8 ? 1.0 : 1.2;

            return strategy switch;
            {
                LearningAdjustmentStrategy.Conservative => baseFactor * 0.8,
                LearningAdjustmentStrategy.Moderate => baseFactor,
                LearningAdjustmentStrategy.Aggressive => baseFactor * 1.2,
                LearningAdjustmentStrategy.Adaptive => baseFactor * (1.0 + (1.0 - analysis.OverallScore)),
                _ => baseFactor;
            };
        }

        /// <summary>
        /// Analyzes specific feedback types;
        /// </summary>
        private void AnalyzeSpecificFeedback(
            FeedbackType type,
            List<LearningFeedback> feedbacks,
            FeedbackAnalysis analysis)
        {
            switch (type)
            {
                case FeedbackType.ErrorRate:
                    var avgError = feedbacks.Average(f => f.Score);
                    analysis.HasHighErrorRate = avgError > _options.ErrorThreshold;
                    break;

                case FeedbackType.ConvergenceRate:
                    var convergenceScores = feedbacks.Select(f => f.Score).ToList();
                    if (convergenceScores.Count >= 2)
                    {
                        analysis.ConvergenceTrend = convergenceScores.Last() - convergenceScores.First();
                    }
                    break;
            }
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _adjustmentTimer?.Dispose();
                }
                _disposed = true;
            }
        }

        // Helper classes for internal use;
        private class FeedbackAnalysis;
        {
            public double OverallScore { get; set; }
            public bool IsImproving { get; set; }
            public bool HasHighErrorRate { get; set; }
            public double ConvergenceTrend { get; set; }
            public Dictionary<FeedbackType, double> FeedbackScores { get; set; } = new();
            public Dictionary<FeedbackType, double> WeightedScores { get; set; } = new();
        }

        private class LearningParameters;
        {
            public double LearningRate { get; set; }
            public int BatchSize { get; set; }
        }
    }

    /// <summary>
    /// Interface for LearningAdjuster;
    /// </summary>
    public interface ILearningAdjuster;
    {
        Task<AdjustmentResult> AdjustLearningAsync(string modelId, LearningAdjustmentStrategy? strategy = null, CancellationToken cancellationToken = default);
        Task<LearningState> GetLearningStateAsync(string modelId, CancellationToken cancellationToken = default);
        void UpdateFeedbackWeights(Dictionary<FeedbackType, double> newWeights);
        Task ResetLearningParametersAsync(string modelId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Interface for feedback engine;
    /// </summary>
    public interface IFeedbackEngine;
    {
        Task<IEnumerable<LearningFeedback>> GetRecentFeedbackAsync(string modelId, TimeSpan timeWindow, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Interface for performance feedback;
    /// </summary>
    public interface IPerformanceFeedback;
    {
        Task<ModelMetrics> GetCurrentMetricsAsync(string modelId, CancellationToken cancellationToken);
        Task<List<HistoricalMetric>> GetHistoricalMetricsAsync(string modelId, TimeSpan timeWindow, CancellationToken cancellationToken);
        Task UpdateLearningRateAsync(string modelId, double learningRate, CancellationToken cancellationToken);
        Task UpdateBatchSizeAsync(string modelId, int batchSize, CancellationToken cancellationToken);
        Task ResetMetricsAsync(string modelId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Model metrics;
    /// </summary>
    public class ModelMetrics;
    {
        public string ModelId { get; set; }
        public int Epoch { get; set; }
        public double Loss { get; set; }
        public double Accuracy { get; set; }
        public double LearningRate { get; set; }
        public int BatchSize { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Historical metric;
    /// </summary>
    public class HistoricalMetric : ModelMetrics;
    {
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Custom exception for learning adjustment errors;
    /// </summary>
    public class LearningAdjustmentException : Exception
    {
        public LearningAdjustmentException(string message) : base(message) { }
        public LearningAdjustmentException(string message, Exception innerException) : base(message, innerException) { }
    }
}
