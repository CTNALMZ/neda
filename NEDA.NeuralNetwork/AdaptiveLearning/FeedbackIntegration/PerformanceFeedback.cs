using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NEDA.Common.Utilities;
using NEDA.Logging;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.NeuralNetwork.DeepLearning.PerformanceMetrics;
using NEDA.NeuralNetwork.DeepLearning.ModelOptimization;

namespace NEDA.NeuralNetwork.AdaptiveLearning.FeedbackIntegration;
{
    /// <summary>
    /// Performs analysis and processing of neural network performance feedback;
    /// for adaptive learning and model optimization;
    /// </summary>
    public class PerformanceFeedback : IPerformanceFeedback, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IModelEvaluator _modelEvaluator;
        private readonly IPerformanceOptimizer _performanceOptimizer;
        private readonly Dictionary<string, FeedbackHistory> _feedbackHistory;
        private readonly object _syncLock = new object();
        private bool _isInitialized;
        private bool _isDisposed;

        /// <summary>
        /// Feedback processing configuration;
        /// </summary>
        public FeedbackConfiguration Configuration { get; private set; }

        /// <summary>
        /// Current performance statistics;
        /// </summary>
        public PerformanceStatistics CurrentStats { get; private set; }

        /// <summary>
        /// Event raised when significant performance change detected;
        /// </summary>
        public event EventHandler<PerformanceChangeEventArgs> PerformanceChangeDetected;

        /// <summary>
        /// Initializes a new instance of PerformanceFeedback;
        /// </summary>
        public PerformanceFeedback(
            ILogger logger,
            IMetricsCollector metricsCollector,
            IModelEvaluator modelEvaluator,
            IPerformanceOptimizer performanceOptimizer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _modelEvaluator = modelEvaluator ?? throw new ArgumentNullException(nameof(modelEvaluator));
            _performanceOptimizer = performanceOptimizer ?? throw new ArgumentNullException(nameof(performanceOptimizer));

            _feedbackHistory = new Dictionary<string, FeedbackHistory>();
            CurrentStats = new PerformanceStatistics();
            Configuration = FeedbackConfiguration.Default;

            _logger.LogInformation("PerformanceFeedback initialized", GetType().Name);
        }

        /// <summary>
        /// Initializes feedback system with custom configuration;
        /// </summary>
        public async Task InitializeAsync(FeedbackConfiguration configuration = null)
        {
            if (_isInitialized)
                return;

            try
            {
                Configuration = configuration ?? FeedbackConfiguration.Default;

                await _metricsCollector.StartCollectionAsync();
                await _performanceOptimizer.InitializeAsync();

                _isInitialized = true;

                _logger.LogInformation("PerformanceFeedback system initialized successfully",
                    GetType().Name,
                    new { Configuration.Thresholds, Configuration.SamplingInterval });
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize PerformanceFeedback", ex, GetType().Name);
                throw new PerformanceFeedbackException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Processes performance feedback for a neural network model;
        /// </summary>
        public async Task<FeedbackResult> ProcessFeedbackAsync(
            string modelId,
            ModelPerformance metrics,
            TrainingContext context = null)
        {
            ValidateState();
            ValidateParameters(modelId, metrics);

            try
            {
                _logger.LogDebug("Processing performance feedback",
                    GetType().Name,
                    new { modelId, metrics.Accuracy, metrics.Loss });

                // Collect comprehensive metrics;
                var detailedMetrics = await CollectDetailedMetricsAsync(modelId, metrics, context);

                // Analyze performance trends;
                var analysis = await AnalyzePerformanceAsync(modelId, detailedMetrics);

                // Store feedback history;
                UpdateFeedbackHistory(modelId, detailedMetrics, analysis);

                // Update current statistics;
                UpdateStatistics(detailedMetrics);

                // Generate optimization recommendations;
                var recommendations = await GenerateRecommendationsAsync(modelId, analysis);

                // Check for performance anomalies;
                await CheckForAnomaliesAsync(modelId, detailedMetrics);

                // Apply automatic adjustments if configured;
                if (Configuration.AutoAdjust && analysis.RequiresAdjustment)
                {
                    await ApplyAutomaticAdjustmentsAsync(modelId, recommendations);
                }

                var result = new FeedbackResult;
                {
                    ModelId = modelId,
                    Analysis = analysis,
                    Recommendations = recommendations,
                    Metrics = detailedMetrics,
                    Timestamp = DateTime.UtcNow,
                    Success = true;
                };

                _logger.LogInformation("Performance feedback processed successfully",
                    GetType().Name,
                    new { modelId, result.Recommendations.Count });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error processing performance feedback", ex, GetType().Name);
                throw new PerformanceFeedbackException("Feedback processing failed", ex);
            }
        }

        /// <summary>
        /// Analyzes performance trends over time;
        /// </summary>
        public async Task<PerformanceAnalysis> AnalyzeTrendsAsync(string modelId, TimeSpan period)
        {
            ValidateState();

            try
            {
                lock (_syncLock)
                {
                    if (!_feedbackHistory.ContainsKey(modelId))
                    {
                        throw new ModelNotFoundException($"No feedback history found for model: {modelId}");
                    }

                    var history = _feedbackHistory[modelId];
                    var timeFiltered = history.GetEntriesWithinPeriod(period);

                    if (timeFiltered.Count == 0)
                        return new PerformanceAnalysis { HasEnoughData = false };

                    return PerformTrendAnalysis(timeFiltered);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error analyzing performance trends", ex, GetType().Name);
                throw new PerformanceFeedbackException("Trend analysis failed", ex);
            }
        }

        /// <summary>
        /// Gets detailed feedback history for a model;
        /// </summary>
        public FeedbackHistory GetFeedbackHistory(string modelId)
        {
            ValidateState();

            lock (_syncLock)
            {
                if (_feedbackHistory.TryGetValue(modelId, out var history))
                {
                    return history.Clone();
                }

                return new FeedbackHistory(modelId);
            }
        }

        /// <summary>
        /// Resets feedback history for a specific model;
        /// </summary>
        public void ResetHistory(string modelId)
        {
            ValidateState();

            lock (_syncLock)
            {
                if (_feedbackHistory.ContainsKey(modelId))
                {
                    _feedbackHistory.Remove(modelId);
                    _logger.LogInformation($"Reset feedback history for model: {modelId}", GetType().Name);
                }
            }
        }

        /// <summary>
        /// Applies optimization recommendations to a model;
        /// </summary>
        public async Task<OptimizationResult> ApplyOptimizationAsync(
            string modelId,
            OptimizationRecommendation recommendation)
        {
            ValidateState();
            ValidateRecommendation(recommendation);

            try
            {
                _logger.LogInformation($"Applying optimization: {recommendation.Type}",
                    GetType().Name,
                    new { modelId, recommendation.Parameters });

                var result = await _performanceOptimizer.ApplyOptimizationAsync(
                    modelId,
                    recommendation);

                if (result.Success)
                {
                    await LogOptimizationAsync(modelId, recommendation, result);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error applying optimization", ex, GetType().Name);
                throw new PerformanceFeedbackException("Optimization application failed", ex);
            }
        }

        /// <summary>
        /// Generates performance report;
        /// </summary>
        public async Task<PerformanceReport> GenerateReportAsync(string modelId, ReportOptions options = null)
        {
            ValidateState();

            try
            {
                options ??= ReportOptions.Default;

                var history = GetFeedbackHistory(modelId);
                var currentMetrics = await _modelEvaluator.EvaluateCurrentAsync(modelId);
                var trends = await AnalyzeTrendsAsync(modelId, options.Period);

                var report = new PerformanceReport;
                {
                    ModelId = modelId,
                    GeneratedAt = DateTime.UtcNow,
                    Period = options.Period,
                    CurrentPerformance = currentMetrics,
                    TrendAnalysis = trends,
                    HistoricalData = history.GetSummary(),
                    Recommendations = trends.Recommendations,
                    AnomaliesDetected = trends.Anomalies.Count > 0;
                };

                if (options.IncludeDetails)
                {
                    report.DetailedMetrics = history.GetRecentEntries(options.MaxHistoryEntries);
                }

                return report;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error generating performance report", ex, GetType().Name);
                throw new PerformanceFeedbackException("Report generation failed", ex);
            }
        }

        /// <summary>
        /// Updates feedback configuration;
        /// </summary>
        public void UpdateConfiguration(FeedbackConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            Configuration = configuration;
            _logger.LogInformation("Feedback configuration updated", GetType().Name);
        }

        private async Task<DetailedMetrics> CollectDetailedMetricsAsync(
            string modelId,
            ModelPerformance metrics,
            TrainingContext context)
        {
            var detailed = new DetailedMetrics;
            {
                BasicMetrics = metrics,
                Context = context,
                CollectedAt = DateTime.UtcNow,
                SystemMetrics = await _metricsCollector.CollectSystemMetricsAsync(),
                ModelSpecific = await _modelEvaluator.GetDetailedMetricsAsync(modelId)
            };

            // Calculate derived metrics;
            CalculateDerivedMetrics(detailed);

            return detailed;
        }

        private async Task<PerformanceAnalysis> AnalyzePerformanceAsync(
            string modelId,
            DetailedMetrics metrics)
        {
            var analysis = new PerformanceAnalysis;
            {
                Timestamp = DateTime.UtcNow,
                BasicMetrics = metrics.BasicMetrics,
                SystemImpact = metrics.SystemMetrics;
            };

            // Calculate performance scores;
            analysis.OverallScore = CalculatePerformanceScore(metrics);
            analysis.TrainingEfficiency = CalculateTrainingEfficiency(metrics);
            analysis.InferenceSpeed = CalculateInferenceSpeed(metrics);
            analysis.MemoryEfficiency = CalculateMemoryEfficiency(metrics);

            // Detect issues;
            analysis.Issues = DetectPerformanceIssues(metrics);
            analysis.RequiresAdjustment = analysis.Issues.Count > 0 ||
                                          analysis.OverallScore < Configuration.Thresholds.MinimumScore;

            // Compare with history;
            if (_feedbackHistory.ContainsKey(modelId))
            {
                var history = _feedbackHistory[modelId];
                analysis.Trend = AnalyzeHistoricalTrend(history, metrics);
            }

            return analysis;
        }

        private async Task<List<OptimizationRecommendation>> GenerateRecommendationsAsync(
            string modelId,
            PerformanceAnalysis analysis)
        {
            var recommendations = new List<OptimizationRecommendation>();

            if (analysis.RequiresAdjustment)
            {
                // Generate recommendations based on detected issues;
                foreach (var issue in analysis.Issues)
                {
                    var recommendation = await GenerateRecommendationForIssueAsync(modelId, issue);
                    if (recommendation != null)
                    {
                        recommendations.Add(recommendation);
                    }
                }

                // Add general optimization recommendations;
                if (analysis.OverallScore < Configuration.Thresholds.TargetScore)
                {
                    recommendations.AddRange(await GenerateGeneralOptimizationsAsync(modelId, analysis));
                }
            }

            return recommendations;
        }

        private async Task CheckForAnomaliesAsync(string modelId, DetailedMetrics metrics)
        {
            var anomalyThreshold = Configuration.Thresholds.AnomalyThreshold;

            // Check for statistical anomalies;
            if (IsAnomaly(metrics, anomalyThreshold))
            {
                var anomalyEvent = new PerformanceAnomalyEvent;
                {
                    ModelId = modelId,
                    Metrics = metrics,
                    DetectedAt = DateTime.UtcNow,
                    Severity = CalculateAnomalySeverity(metrics)
                };

                _logger.LogWarning("Performance anomaly detected",
                    GetType().Name,
                    new { modelId, anomalyEvent.Severity });

                // Raise event for monitoring;
                PerformanceChangeDetected?.Invoke(this,
                    new PerformanceChangeEventArgs;
                    {
                        ModelId = modelId,
                        ChangeType = PerformanceChangeType.Anomaly,
                        Details = anomalyEvent;
                    });
            }
        }

        private void UpdateFeedbackHistory(string modelId, DetailedMetrics metrics, PerformanceAnalysis analysis)
        {
            lock (_syncLock)
            {
                if (!_feedbackHistory.ContainsKey(modelId))
                {
                    _feedbackHistory[modelId] = new FeedbackHistory(modelId);
                }

                var entry = new FeedbackEntry
                {
                    Timestamp = metrics.CollectedAt,
                    Metrics = metrics,
                    Analysis = analysis,
                    Context = metrics.Context;
                };

                _feedbackHistory[modelId].AddEntry(entry);

                // Maintain history size;
                if (_feedbackHistory[modelId].Count > Configuration.MaxHistoryEntries)
                {
                    _feedbackHistory[modelId].RemoveOldest();
                }
            }
        }

        private void UpdateStatistics(DetailedMetrics metrics)
        {
            lock (_syncLock)
            {
                CurrentStats.TotalProcessed++;
                CurrentStats.LastProcessed = DateTime.UtcNow;

                // Update running averages;
                UpdateRunningAverages(metrics);

                // Update performance bands;
                UpdatePerformanceBands(metrics);
            }
        }

        private async Task ApplyAutomaticAdjustmentsAsync(
            string modelId,
            List<OptimizationRecommendation> recommendations)
        {
            foreach (var recommendation in recommendations)
            {
                if (recommendation.Priority >= Configuration.AutoAdjustmentMinPriority)
                {
                    try
                    {
                        await ApplyOptimizationAsync(modelId, recommendation);

                        _logger.LogInformation($"Auto-adjusted model {modelId} with {recommendation.Type}",
                            GetType().Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Auto-adjustment failed for {recommendation.Type}",
                            ex, GetType().Name);
                    }
                }
            }
        }

        private async Task LogOptimizationAsync(
            string modelId,
            OptimizationRecommendation recommendation,
            OptimizationResult result)
        {
            var logEntry = new OptimizationLogEntry
            {
                ModelId = modelId,
                Recommendation = recommendation,
                Result = result,
                AppliedAt = DateTime.UtcNow,
                AppliedBy = "PerformanceFeedback System"
            };

            // Store in optimization history;
            lock (_syncLock)
            {
                if (_feedbackHistory.ContainsKey(modelId))
                {
                    _feedbackHistory[modelId].AddOptimizationLog(logEntry);
                }
            }

            await _logger.LogOptimizationAsync(logEntry);
        }

        private void ValidateState()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("PerformanceFeedback not initialized. Call InitializeAsync first.");

            if (_isDisposed)
                throw new ObjectDisposedException(nameof(PerformanceFeedback));
        }

        private void ValidateParameters(string modelId, ModelPerformance metrics)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Model ID cannot be null or empty", nameof(modelId));

            if (metrics == null)
                throw new ArgumentNullException(nameof(metrics));

            if (metrics.Accuracy < 0 || metrics.Accuracy > 1)
                throw new ArgumentOutOfRangeException(nameof(metrics.Accuracy), "Accuracy must be between 0 and 1");
        }

        private void ValidateRecommendation(OptimizationRecommendation recommendation)
        {
            if (recommendation == null)
                throw new ArgumentNullException(nameof(recommendation));

            if (string.IsNullOrWhiteSpace(recommendation.Type))
                throw new ArgumentException("Recommendation type cannot be empty", nameof(recommendation.Type));
        }

        // Performance calculation methods (implementation details)
        private float CalculatePerformanceScore(DetailedMetrics metrics)
        {
            // Weighted combination of accuracy, speed, and efficiency;
            var accuracyWeight = 0.5f;
            var speedWeight = 0.3f;
            var efficiencyWeight = 0.2f;

            return (metrics.BasicMetrics.Accuracy * accuracyWeight) +
                   (metrics.ModelSpecific.InferenceSpeed * speedWeight) +
                   (metrics.SystemMetrics.MemoryEfficiency * efficiencyWeight);
        }

        private float CalculateTrainingEfficiency(DetailedMetrics metrics)
        {
            if (metrics.Context?.TrainingTime == null || metrics.Context.TrainingTime.Value == TimeSpan.Zero)
                return 0;

            var samplesPerSecond = metrics.Context.SamplesProcessed /
                                   (float)metrics.Context.TrainingTime.Value.TotalSeconds;
            return samplesPerSecond / 1000; // Normalize;
        }

        private float CalculateInferenceSpeed(DetailedMetrics metrics)
        {
            return metrics.ModelSpecific?.InferenceSpeed ?? 0;
        }

        private float CalculateMemoryEfficiency(DetailedMetrics metrics)
        {
            var maxMemory = metrics.SystemMetrics.MaxMemoryUsage;
            var usedMemory = metrics.SystemMetrics.CurrentMemoryUsage;

            if (maxMemory == 0)
                return 0;

            return 1 - (usedMemory / maxMemory);
        }

        private List<PerformanceIssue> DetectPerformanceIssues(DetailedMetrics metrics)
        {
            var issues = new List<PerformanceIssue>();
            var thresholds = Configuration.Thresholds;

            if (metrics.BasicMetrics.Accuracy < thresholds.MinimumAccuracy)
            {
                issues.Add(new PerformanceIssue;
                {
                    Type = IssueType.LowAccuracy,
                    Severity = CalculateIssueSeverity(thresholds.MinimumAccuracy, metrics.BasicMetrics.Accuracy),
                    Description = $"Accuracy ({metrics.BasicMetrics.Accuracy:P2}) below threshold ({thresholds.MinimumAccuracy:P2})"
                });
            }

            if (metrics.BasicMetrics.Loss > thresholds.MaximumLoss)
            {
                issues.Add(new PerformanceIssue;
                {
                    Type = IssueType.HighLoss,
                    Severity = CalculateIssueSeverity(thresholds.MaximumLoss, metrics.BasicMetrics.Loss),
                    Description = $"Loss ({metrics.BasicMetrics.Loss:F4}) above threshold ({thresholds.MaximumLoss:F4})"
                });
            }

            if (metrics.SystemMetrics.CurrentMemoryUsage > thresholds.MaxMemoryUsage)
            {
                issues.Add(new PerformanceIssue;
                {
                    Type = IssueType.HighMemoryUsage,
                    Severity = CalculateIssueSeverity(thresholds.MaxMemoryUsage, metrics.SystemMetrics.CurrentMemoryUsage),
                    Description = $"Memory usage ({metrics.SystemMetrics.CurrentMemoryUsage}MB) above threshold ({thresholds.MaxMemoryUsage}MB)"
                });
            }

            return issues;
        }

        private async Task<OptimizationRecommendation> GenerateRecommendationForIssueAsync(
            string modelId,
            PerformanceIssue issue)
        {
            // This would typically involve more sophisticated logic;
            // and possibly consulting with the optimization engine;

            return await Task.Run(() =>
            {
                return issue.Type switch;
                {
                    IssueType.LowAccuracy => new OptimizationRecommendation;
                    {
                        Type = "IncreaseTraining",
                        Priority = issue.Severity >= 0.7 ? RecommendationPriority.High : RecommendationPriority.Medium,
                        Parameters = new Dictionary<string, object>
                        {
                            { "epochs", 10 },
                            { "learningRate", 0.001 },
                            { "dataAugmentation", true }
                        },
                        Description = "Increase training epochs with data augmentation"
                    },
                    IssueType.HighLoss => new OptimizationRecommendation;
                    {
                        Type = "AdjustLearningRate",
                        Priority = RecommendationPriority.Medium,
                        Parameters = new Dictionary<string, object>
                        {
                            { "newLearningRate", 0.0001 },
                            { "schedule", "exponentialDecay" }
                        },
                        Description = "Reduce learning rate to prevent overshooting"
                    },
                    IssueType.HighMemoryUsage => new OptimizationRecommendation;
                    {
                        Type = "OptimizeMemory",
                        Priority = RecommendationPriority.High,
                        Parameters = new Dictionary<string, object>
                        {
                            { "batchSize", 32 },
                            { "mixedPrecision", true },
                            { "gradientCheckpointing", false }
                        },
                        Description = "Reduce batch size and enable mixed precision"
                    },
                    _ => null;
                };
            });
        }

        private async Task<List<OptimizationRecommendation>> GenerateGeneralOptimizationsAsync(
            string modelId,
            PerformanceAnalysis analysis)
        {
            var recommendations = new List<OptimizationRecommendation>();

            // Consult with optimization engine for general improvements;
            var engineRecommendations = await _performanceOptimizer.GenerateOptimizationsAsync(
                modelId,
                analysis);

            recommendations.AddRange(engineRecommendations);

            return recommendations;
        }

        private bool IsAnomaly(DetailedMetrics metrics, float threshold)
        {
            // Simple anomaly detection based on deviation from expected ranges;
            var expectedAccuracy = 0.85; // This would come from historical data;
            var accuracyDeviation = Math.Abs(metrics.BasicMetrics.Accuracy - expectedAccuracy);

            return accuracyDeviation > threshold;
        }

        private float CalculateAnomalySeverity(DetailedMetrics metrics)
        {
            // Calculate severity based on multiple factors;
            var factors = new List<float>
            {
                Math.Abs(metrics.BasicMetrics.Accuracy - 0.85f), // Accuracy deviation;
                metrics.BasicMetrics.Loss / 2, // Normalized loss;
                metrics.SystemMetrics.CPUUsage / 100 // CPU usage percentage;
            };

            return factors.Average();
        }

        private float CalculateIssueSeverity(float threshold, float actualValue)
        {
            var deviation = Math.Abs(actualValue - threshold) / threshold;
            return Math.Min(deviation, 1.0f);
        }

        private void CalculateDerivedMetrics(DetailedMetrics metrics)
        {
            // Calculate F1 score if precision and recall are available;
            if (metrics.BasicMetrics.Precision > 0 && metrics.BasicMetrics.Recall > 0)
            {
                metrics.DerivedMetrics.F1Score = 2 *
                    (metrics.BasicMetrics.Precision * metrics.BasicMetrics.Recall) /
                    (metrics.BasicMetrics.Precision + metrics.BasicMetrics.Recall);
            }

            // Calculate training efficiency;
            if (metrics.Context?.TrainingTime != null && metrics.Context.SamplesProcessed > 0)
            {
                metrics.DerivedMetrics.SamplesPerSecond =
                    metrics.Context.SamplesProcessed /
                    (float)metrics.Context.TrainingTime.Value.TotalSeconds;
            }
        }

        private PerformanceTrend AnalyzeHistoricalTrend(FeedbackHistory history, DetailedMetrics current)
        {
            var recentEntries = history.GetRecentEntries(10);
            if (recentEntries.Count < 5)
                return PerformanceTrend.InsufficientData;

            var accuracyTrend = CalculateTrend(recentEntries.Select(e => e.Metrics.BasicMetrics.Accuracy).ToArray());
            var lossTrend = CalculateTrend(recentEntries.Select(e => e.Metrics.BasicMetrics.Loss).ToArray());

            if (accuracyTrend > 0.1 && lossTrend < -0.1)
                return PerformanceTrend.Improving;
            else if (accuracyTrend < -0.1 && lossTrend > 0.1)
                return PerformanceTrend.Degrading;
            else;
                return PerformanceTrend.Stable;
        }

        private float CalculateTrend(float[] values)
        {
            if (values.Length < 2)
                return 0;

            // Simple linear regression slope;
            var n = values.Length;
            var sumX = 0;
            var sumY = 0.0f;
            var sumXY = 0.0f;
            var sumXX = 0;

            for (int i = 0; i < n; i++)
            {
                sumX += i;
                sumY += values[i];
                sumXY += i * values[i];
                sumXX += i * i;
            }

            var slope = (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);
            return slope;
        }

        private void UpdateRunningAverages(DetailedMetrics metrics)
        {
            // Update exponential moving averages;
            var alpha = 0.1f; // Smoothing factor;

            CurrentStats.AverageAccuracy = alpha * metrics.BasicMetrics.Accuracy +
                                          (1 - alpha) * CurrentStats.AverageAccuracy;
            CurrentStats.AverageLoss = alpha * metrics.BasicMetrics.Loss +
                                       (1 - alpha) * CurrentStats.AverageLoss;
            CurrentStats.AverageInferenceSpeed = alpha * metrics.ModelSpecific.InferenceSpeed +
                                                 (1 - alpha) * CurrentStats.AverageInferenceSpeed;
        }

        private void UpdatePerformanceBands(DetailedMetrics metrics)
        {
            // Update min/max tracking;
            CurrentStats.MinAccuracy = Math.Min(CurrentStats.MinAccuracy, metrics.BasicMetrics.Accuracy);
            CurrentStats.MaxAccuracy = Math.Max(CurrentStats.MaxAccuracy, metrics.BasicMetrics.Accuracy);
            CurrentStats.MinLoss = Math.Min(CurrentStats.MinLoss, metrics.BasicMetrics.Loss);
            CurrentStats.MaxLoss = Math.Max(CurrentStats.MaxLoss, metrics.BasicMetrics.Loss);
        }

        private PerformanceAnalysis PerformTrendAnalysis(List<FeedbackEntry> entries)
        {
            var analysis = new PerformanceAnalysis;
            {
                HasEnoughData = true,
                Timestamp = DateTime.UtcNow;
            };

            if (entries.Count == 0)
                return analysis;

            // Calculate statistical measures;
            var accuracies = entries.Select(e => e.Metrics.BasicMetrics.Accuracy).ToArray();
            var losses = entries.Select(e => e.Metrics.BasicMetrics.Loss).ToArray();

            analysis.Trend = AnalyzeHistoricalTrend(new FeedbackHistory("temp")
            {
                Entries = entries;
            }, entries.Last().Metrics);

            // Detect anomalies in historical data;
            analysis.Anomalies = DetectHistoricalAnomalies(entries);

            // Generate recommendations based on trends;
            analysis.Recommendations = GenerateTrendBasedRecommendations(analysis.Trend, entries);

            return analysis;
        }

        private List<PerformanceAnomaly> DetectHistoricalAnomalies(List<FeedbackEntry> entries)
        {
            var anomalies = new List<PerformanceAnomaly>();

            // Implement statistical anomaly detection (e.g., using z-score)
            if (entries.Count >= 10)
            {
                var recentAccuracies = entries.TakeLast(10).Select(e => e.Metrics.BasicMetrics.Accuracy).ToArray();
                var mean = recentAccuracies.Average();
                var stdDev = Math.Sqrt(recentAccuracies.Select(x => Math.Pow(x - mean, 2)).Average());

                for (int i = 0; i < recentAccuracies.Length; i++)
                {
                    var zScore = Math.Abs((recentAccuracies[i] - mean) / stdDev);
                    if (zScore > 2.5) // 2.5 standard deviations;
                    {
                        anomalies.Add(new PerformanceAnomaly;
                        {
                            Timestamp = entries[i].Timestamp,
                            Value = recentAccuracies[i],
                            ZScore = (float)zScore,
                            Metric = "Accuracy"
                        });
                    }
                }
            }

            return anomalies;
        }

        private List<OptimizationRecommendation> GenerateTrendBasedRecommendations(
            PerformanceTrend trend,
            List<FeedbackEntry> entries)
        {
            var recommendations = new List<OptimizationRecommendation>();

            switch (trend)
            {
                case PerformanceTrend.Degrading:
                    recommendations.Add(new OptimizationRecommendation;
                    {
                        Type = "TrendIntervention",
                        Priority = RecommendationPriority.High,
                        Parameters = new Dictionary<string, object>
                        {
                            { "intervention", "earlyStopping" },
                            { "patience", 5 },
                            { "restoreBestWeights", true }
                        },
                        Description = "Model performance is degrading, consider early stopping"
                    });
                    break;

                case PerformanceTrend.Stable:
                    if (entries.Last().Metrics.BasicMetrics.Accuracy < 0.8)
                    {
                        recommendations.Add(new OptimizationRecommendation;
                        {
                            Type = "ArchitectureReview",
                            Priority = RecommendationPriority.Medium,
                            Parameters = new Dictionary<string, object>
                            {
                                { "action", "considerArchitectureChange" },
                                { "suggestion", "increaseCapacity" }
                            },
                            Description = "Performance stable but suboptimal, consider model architecture changes"
                        });
                    }
                    break;
            }

            return recommendations;
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _metricsCollector?.StopCollectionAsync().Wait(TimeSpan.FromSeconds(5));
                _performanceOptimizer?.Dispose();

                lock (_syncLock)
                {
                    _feedbackHistory.Clear();
                }

                _isDisposed = true;
                _logger.LogInformation("PerformanceFeedback disposed", GetType().Name);
            }
        }
    }

    // Supporting classes and interfaces...

    public interface IPerformanceFeedback;
    {
        Task<FeedbackResult> ProcessFeedbackAsync(string modelId, ModelPerformance metrics, TrainingContext context);
        Task<PerformanceAnalysis> AnalyzeTrendsAsync(string modelId, TimeSpan period);
        Task<PerformanceReport> GenerateReportAsync(string modelId, ReportOptions options);
        Task<OptimizationResult> ApplyOptimizationAsync(string modelId, OptimizationRecommendation recommendation);
        FeedbackHistory GetFeedbackHistory(string modelId);
        void UpdateConfiguration(FeedbackConfiguration configuration);
    }

    public class FeedbackConfiguration;
    {
        public static FeedbackConfiguration Default => new FeedbackConfiguration;
        {
            AutoAdjust = true,
            AutoAdjustmentMinPriority = RecommendationPriority.Medium,
            MaxHistoryEntries = 1000,
            SamplingInterval = TimeSpan.FromMinutes(5),
            Thresholds = new PerformanceThresholds;
            {
                MinimumAccuracy = 0.75f,
                MaximumLoss = 2.0f,
                TargetScore = 0.85f,
                MinimumScore = 0.65f,
                MaxMemoryUsage = 2048, // MB;
                AnomalyThreshold = 0.15f;
            }
        };

        public bool AutoAdjust { get; set; }
        public RecommendationPriority AutoAdjustmentMinPriority { get; set; }
        public int MaxHistoryEntries { get; set; }
        public TimeSpan SamplingInterval { get; set; }
        public PerformanceThresholds Thresholds { get; set; }
    }

    public class PerformanceThresholds;
    {
        public float MinimumAccuracy { get; set; }
        public float MaximumLoss { get; set; }
        public float TargetScore { get; set; }
        public float MinimumScore { get; set; }
        public int MaxMemoryUsage { get; set; } // MB;
        public float AnomalyThreshold { get; set; }
    }

    public class ModelPerformance;
    {
        public float Accuracy { get; set; }
        public float Loss { get; set; }
        public float Precision { get; set; }
        public float Recall { get; set; }
        public float F1Score { get; set; }
        public Dictionary<string, float> AdditionalMetrics { get; set; } = new Dictionary<string, float>();
    }

    public class TrainingContext;
    {
        public int? Epoch { get; set; }
        public int? BatchSize { get; set; }
        public TimeSpan? TrainingTime { get; set; }
        public int? SamplesProcessed { get; set; }
        public float? LearningRate { get; set; }
        public string Phase { get; set; } // "training", "validation", "testing"
        public Dictionary<string, object> AdditionalInfo { get; set; } = new Dictionary<string, object>();
    }

    public class DetailedMetrics;
    {
        public ModelPerformance BasicMetrics { get; set; }
        public TrainingContext Context { get; set; }
        public DateTime CollectedAt { get; set; }
        public SystemMetrics SystemMetrics { get; set; }
        public ModelSpecificMetrics ModelSpecific { get; set; }
        public DerivedMetrics DerivedMetrics { get; set; } = new DerivedMetrics();
    }

    public class SystemMetrics;
    {
        public float CPUUsage { get; set; } // Percentage;
        public float CurrentMemoryUsage { get; set; } // MB;
        public float MaxMemoryUsage { get; set; } // MB;
        public float MemoryEfficiency { get; set; } // 0-1;
        public float GPUUsage { get; set; } // Percentage;
        public float GPUMemoryUsage { get; set; } // MB;
        public float DiskIO { get; set; } // MB/s;
        public float NetworkIO { get; set; } // MB/s;
    }

    public class ModelSpecificMetrics;
    {
        public float InferenceSpeed { get; set; } // samples/second;
        public float ModelSize { get; set; } // MB;
        public int ParameterCount { get; set; }
        public float Flops { get; set; } // GFLOPs;
        public Dictionary<string, float> LayerMetrics { get; set; } = new Dictionary<string, float>();
    }

    public class DerivedMetrics;
    {
        public float F1Score { get; set; }
        public float SamplesPerSecond { get; set; }
        public float EnergyEfficiency { get; set; } // samples/Joule;
        public float CostPerInference { get; set; } // Estimated cost;
    }

    public class FeedbackResult;
    {
        public string ModelId { get; set; }
        public PerformanceAnalysis Analysis { get; set; }
        public List<OptimizationRecommendation> Recommendations { get; set; } = new List<OptimizationRecommendation>();
        public DetailedMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class PerformanceAnalysis;
    {
        public DateTime Timestamp { get; set; }
        public ModelPerformance BasicMetrics { get; set; }
        public SystemMetrics SystemImpact { get; set; }
        public float OverallScore { get; set; }
        public float TrainingEfficiency { get; set; }
        public float InferenceSpeed { get; set; }
        public float MemoryEfficiency { get; set; }
        public List<PerformanceIssue> Issues { get; set; } = new List<PerformanceIssue>();
        public bool RequiresAdjustment { get; set; }
        public PerformanceTrend Trend { get; set; }
        public bool HasEnoughData { get; set; }
        public List<PerformanceAnomaly> Anomalies { get; set; } = new List<PerformanceAnomaly>();
        public List<OptimizationRecommendation> Recommendations { get; set; } = new List<OptimizationRecommendation>();
    }

    public class PerformanceIssue;
    {
        public IssueType Type { get; set; }
        public float Severity { get; set; } // 0-1;
        public string Description { get; set; }
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    }

    public enum IssueType;
    {
        LowAccuracy,
        HighLoss,
        HighMemoryUsage,
        SlowInference,
        Overfitting,
        Underfitting,
        GradientExplosion,
        GradientVanishing;
    }

    public enum PerformanceTrend;
    {
        Improving,
        Stable,
        Degrading,
        InsufficientData;
    }

    public class PerformanceAnomaly;
    {
        public DateTime Timestamp { get; set; }
        public string Metric { get; set; }
        public float Value { get; set; }
        public float ZScore { get; set; }
        public string Description { get; set; }
    }

    public class OptimizationRecommendation;
    {
        public string Type { get; set; }
        public RecommendationPriority Priority { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string Description { get; set; }
        public float ExpectedImprovement { get; set; } // 0-1;
        public TimeSpan EstimatedDuration { get; set; }
        public string RiskLevel { get; set; } // "low", "medium", "high"
    }

    public enum RecommendationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public class OptimizationResult;
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> Results { get; set; } = new Dictionary<string, object>();
        public TimeSpan Duration { get; set; }
        public float ImprovementAchieved { get; set; }
        public Dictionary<string, float> BeforeMetrics { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, float> AfterMetrics { get; set; } = new Dictionary<string, float>();
    }

    public class FeedbackHistory;
    {
        public string ModelId { get; set; }
        public List<FeedbackEntry> Entries { get; set; } = new List<FeedbackEntry>();
        public List<OptimizationLogEntry> OptimizationLogs { get; set; } = new List<OptimizationLogEntry>();

        public int Count => Entries.Count;

        public FeedbackHistory(string modelId)
        {
            ModelId = modelId;
        }

        public void AddEntry(FeedbackEntry entry)
        {
            Entries.Add(entry);
        }

        public void AddOptimizationLog(OptimizationLogEntry logEntry)
        {
            OptimizationLogs.Add(logEntry);
        }

        public List<FeedbackEntry> GetRecentEntries(int count)
        {
            return Entries.TakeLast(count).ToList();
        }

        public List<FeedbackEntry> GetEntriesWithinPeriod(TimeSpan period)
        {
            var cutoff = DateTime.UtcNow - period;
            return Entries.Where(e => e.Timestamp >= cutoff).ToList();
        }

        public void RemoveOldest()
        {
            if (Entries.Count > 0)
            {
                Entries.RemoveAt(0);
            }
        }

        public FeedbackHistory Clone()
        {
            return new FeedbackHistory(ModelId)
            {
                Entries = new List<FeedbackEntry>(Entries),
                OptimizationLogs = new List<OptimizationLogEntry>(OptimizationLogs)
            };
        }

        public HistorySummary GetSummary()
        {
            if (Entries.Count == 0)
                return new HistorySummary();

            return new HistorySummary;
            {
                TotalEntries = Entries.Count,
                FirstEntry = Entries.First().Timestamp,
                LastEntry = Entries.Last().Timestamp,
                AverageAccuracy = Entries.Average(e => e.Metrics.BasicMetrics.Accuracy),
                AverageLoss = Entries.Average(e => e.Metrics.BasicMetrics.Loss),
                OptimizationCount = OptimizationLogs.Count;
            };
        }
    }

    public class FeedbackEntry
    {
        public DateTime Timestamp { get; set; }
        public DetailedMetrics Metrics { get; set; }
        public PerformanceAnalysis Analysis { get; set; }
        public TrainingContext Context { get; set; }
    }

    public class OptimizationLogEntry
    {
        public string ModelId { get; set; }
        public OptimizationRecommendation Recommendation { get; set; }
        public OptimizationResult Result { get; set; }
        public DateTime AppliedAt { get; set; }
        public string AppliedBy { get; set; }
    }

    public class HistorySummary;
    {
        public int TotalEntries { get; set; }
        public DateTime FirstEntry { get; set; }
        public DateTime LastEntry { get; set; }
        public float AverageAccuracy { get; set; }
        public float AverageLoss { get; set; }
        public int OptimizationCount { get; set; }
        public TimeSpan CoveragePeriod => LastEntry - FirstEntry
    }

    public class PerformanceReport;
    {
        public string ModelId { get; set; }
        public DateTime GeneratedAt { get; set; }
        public TimeSpan Period { get; set; }
        public ModelPerformance CurrentPerformance { get; set; }
        public PerformanceAnalysis TrendAnalysis { get; set; }
        public HistorySummary HistoricalData { get; set; }
        public List<OptimizationRecommendation> Recommendations { get; set; }
        public List<FeedbackEntry> DetailedMetrics { get; set; }
        public bool AnomaliesDetected { get; set; }
        public string OverallStatus { get; set; }
        public Dictionary<string, object> AdditionalInsights { get; set; } = new Dictionary<string, object>();
    }

    public class ReportOptions;
    {
        public static ReportOptions Default => new ReportOptions;
        {
            Period = TimeSpan.FromDays(30),
            IncludeDetails = true,
            MaxHistoryEntries = 100,
            Format = ReportFormat.Detailed;
        };

        public TimeSpan Period { get; set; }
        public bool IncludeDetails { get; set; }
        public int MaxHistoryEntries { get; set; }
        public ReportFormat Format { get; set; }
    }

    public enum ReportFormat;
    {
        Summary,
        Detailed,
        Executive;
    }

    public class PerformanceStatistics;
    {
        public int TotalProcessed { get; set; }
        public DateTime LastProcessed { get; set; }
        public float AverageAccuracy { get; set; }
        public float AverageLoss { get; set; }
        public float AverageInferenceSpeed { get; set; }
        public float MinAccuracy { get; set; } = float.MaxValue;
        public float MaxAccuracy { get; set; } = float.MinValue;
        public float MinLoss { get; set; } = float.MaxValue;
        public float MaxLoss { get; set; } = float.MinValue;
    }

    public class PerformanceAnomalyEvent;
    {
        public string ModelId { get; set; }
        public DetailedMetrics Metrics { get; set; }
        public DateTime DetectedAt { get; set; }
        public float Severity { get; set; }
        public string Description { get; set; }
    }

    public class PerformanceChangeEventArgs : EventArgs;
    {
        public string ModelId { get; set; }
        public PerformanceChangeType ChangeType { get; set; }
        public object Details { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public enum PerformanceChangeType;
    {
        Improvement,
        Degradation,
        Anomaly,
        OptimizationApplied,
        ThresholdBreached;
    }

    // Exceptions;
    public class PerformanceFeedbackException : Exception
    {
        public PerformanceFeedbackException(string message) : base(message) { }
        public PerformanceFeedbackException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ModelNotFoundException : PerformanceFeedbackException;
    {
        public ModelNotFoundException(string message) : base(message) { }
    }
}
