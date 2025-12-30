using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.NeuralNetwork.DeepLearning.Common;
using NEDA.NeuralNetwork.DeepLearning.ModelManagement;
using NEDA.NeuralNetwork.CognitiveModels.ProblemSolving;

namespace NEDA.NeuralNetwork.DeepLearning.PerformanceMetrics;
{
    /// <summary>
    /// Advanced metrics calculator for comprehensive model evaluation, performance analysis,
    /// and statistical measurement across multiple dimensions and domains;
    /// </summary>
    public class MetricsCalculator : IMetricsCalculator, IDisposable;
    {
        private readonly ILogger<MetricsCalculator> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly MetricsCalculatorConfig _config;
        private readonly StatisticalAnalyzer _statisticalAnalyzer;
        private readonly MetricRegistry _metricRegistry
        private readonly ValidationEngine _validationEngine;
        private readonly SemaphoreSlim _calculationSemaphore;
        private readonly ConcurrentDictionary<string, MetricCalculationSession> _activeSessions;
        private readonly Timer _sessionCleanupTimer;
        private bool _disposed;
        private long _totalMetricsCalculated;
        private DateTime _startTime;
        private readonly Random _random;

        /// <summary>
        /// Initializes a new instance of the MetricsCalculator;
        /// </summary>
        public MetricsCalculator(
            ILogger<MetricsCalculator> logger,
            IErrorReporter errorReporter,
            IOptions<MetricsCalculatorConfig> configOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _config = configOptions?.Value ?? throw new ArgumentNullException(nameof(configOptions));

            _statisticalAnalyzer = new StatisticalAnalyzer(_logger, _config.StatisticalSettings);
            _metricRegistry = new MetricRegistry(_config.RegistrySettings);
            _validationEngine = new ValidationEngine(_config.ValidationSettings);

            _calculationSemaphore = new SemaphoreSlim(
                _config.MaxConcurrentCalculations,
                _config.MaxConcurrentCalculations);

            _activeSessions = new ConcurrentDictionary<string, MetricCalculationSession>();
            _sessionCleanupTimer = new Timer(
                CleanupExpiredSessions,
                null,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(15));

            _totalMetricsCalculated = 0;
            _startTime = DateTime.UtcNow;
            _random = new Random(Guid.NewGuid().GetHashCode());

            _logger.LogInformation(
                "MetricsCalculator initialized with {MaxConcurrentCalculations} concurrent calculations",
                _config.MaxConcurrentCalculations);
        }

        /// <summary>
        /// Calculates comprehensive performance metrics for a model;
        /// </summary>
        /// <param name="request">Metrics calculation request</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Comprehensive metrics calculation result</returns>
        public async Task<MetricsCalculationResult> CalculateAsync(
            MetricsCalculationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.SessionId))
                throw new ArgumentException("Session ID is required", nameof(request.SessionId));

            await _calculationSemaphore.WaitAsync(cancellationToken);
            var calculationId = Guid.NewGuid().ToString();

            try
            {
                _logger.LogInformation(
                    "Starting metrics calculation {CalculationId} for session {SessionId}",
                    calculationId, request.SessionId);

                // Get or create calculation session;
                var session = await GetOrCreateSessionAsync(
                    request.SessionId,
                    request.SessionConfig,
                    cancellationToken);

                // Validate calculation request;
                var validationResult = await ValidateCalculationRequestAsync(
                    request,
                    cancellationToken);

                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Calculation validation failed for {CalculationId}: {Errors}",
                        calculationId, string.Join(", ", validationResult.Errors));
                    return CreateErrorResult(calculationId, request.SessionId,
                        $"Calculation validation failed: {validationResult.Errors.First()}");
                }

                // Load ground truth and predictions;
                var data = await LoadAndPrepareDataAsync(
                    request,
                    cancellationToken);

                // Select appropriate metrics based on problem type;
                var selectedMetrics = await SelectMetricsAsync(
                    request.ProblemType,
                    request.MetricTypes,
                    request.MetricOptions,
                    cancellationToken);

                // Calculate metrics in parallel;
                var metricResults = new ConcurrentDictionary<string, MetricResult>();
                var calculationTasks = new List<Task>();

                foreach (var metric in selectedMetrics)
                {
                    calculationTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var result = await CalculateSingleMetricAsync(
                                metric,
                                data,
                                request,
                                cancellationToken);

                            metricResults[metric.Name] = result;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error calculating metric {MetricName}", metric.Name);
                            metricResults[metric.Name] = CreateMetricError(metric.Name, ex.Message);
                        }
                    }, cancellationToken));
                }

                // Wait for all calculations with timeout;
                using var timeoutCts = new CancellationTokenSource(_config.CalculationTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCts.Token);

                try
                {
                    await Task.WhenAll(calculationTasks);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    _logger.LogWarning("Metrics calculation timed out for {CalculationId}", calculationId);
                }

                // Calculate aggregated metrics;
                var aggregatedMetrics = await CalculateAggregatedMetricsAsync(
                    metricResults,
                    request,
                    data,
                    cancellationToken);

                // Perform statistical analysis;
                var statisticalAnalysis = await PerformStatisticalAnalysisAsync(
                    metricResults,
                    data,
                    request,
                    cancellationToken);

                // Perform comparative analysis if baseline provided;
                var comparativeAnalysis = request.BaselineResults != null;
                    ? await PerformComparativeAnalysisAsync(
                        metricResults,
                        request.BaselineResults,
                        request,
                        cancellationToken)
                    : null;

                // Generate comprehensive report;
                var report = await GenerateMetricsReportAsync(
                    calculationId,
                    metricResults,
                    aggregatedMetrics,
                    statisticalAnalysis,
                    comparativeAnalysis,
                    request,
                    cancellationToken);

                // Validate results;
                var resultsValidation = await ValidateResultsAsync(
                    metricResults,
                    aggregatedMetrics,
                    request,
                    cancellationToken);

                // Update session with results;
                await UpdateSessionWithResultsAsync(
                    session,
                    report,
                    metricResults,
                    cancellationToken);

                Interlocked.Increment(ref _totalMetricsCalculated);
                _logger.LogInformation(
                    "Metrics calculation {CalculationId} completed successfully. " +
                    "Calculated {MetricCount} metrics with {AggregatedCount} aggregates",
                    calculationId, metricResults.Count, aggregatedMetrics.Count);

                return report;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Metrics calculation {CalculationId} was cancelled", calculationId);
                throw;
            }
            catch (InvalidDataException ex)
            {
                _logger.LogError(ex, "Invalid data in metrics calculation {CalculationId}", calculationId);
                return CreateInvalidDataResult(calculationId, request.SessionId, ex);
            }
            catch (MetricCalculationException ex)
            {
                _logger.LogError(ex, "Metric calculation error in {CalculationId}", calculationId);
                return CreateMetricErrorResult(calculationId, request.SessionId, ex);
            }
            catch (StatisticalAnalysisException ex)
            {
                _logger.LogError(ex, "Statistical analysis error in {CalculationId}", calculationId);
                return CreateStatisticalErrorResult(calculationId, request.SessionId, ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in metrics calculation {CalculationId}", calculationId);

                await _errorReporter.ReportErrorAsync(
                    new ErrorReport;
                    {
                        ErrorCode = ErrorCodes.MetricsCalculationFailed,
                        Message = $"Metrics calculation failed for {calculationId}",
                        Exception = ex,
                        Severity = ErrorSeverity.Medium,
                        Component = nameof(MetricsCalculator)
                    },
                    cancellationToken);

                return CreateErrorResult(calculationId, request.SessionId, ex.Message);
            }
            finally
            {
                _calculationSemaphore.Release();
            }
        }

        /// <summary>
        /// Performs cross-validation and calculates metrics for each fold;
        /// </summary>
        public async Task<CrossValidationResult> CrossValidateAsync(
            CrossValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            // Implementation for cross-validation metrics;
            // Calculates metrics for each fold and aggregates results;
        }

        /// <summary>
        /// Calculates confidence intervals and statistical significance for metrics;
        /// </summary>
        public async Task<StatisticalSignificanceResult> CalculateStatisticalSignificanceAsync(
            StatisticalSignificanceRequest request,
            CancellationToken cancellationToken = default)
        {
            // Implementation for statistical significance testing;
            // Uses bootstrapping, t-tests, ANOVA, etc.
        }

        /// <summary>
        /// Performs error analysis and identifies patterns in model errors;
        /// </summary>
        public async Task<ErrorAnalysisResult> AnalyzeErrorsAsync(
            ErrorAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            // Implementation for error analysis;
            // Identifies error patterns, confusion analysis, failure modes;
        }

        /// <summary>
        /// Calculates model calibration metrics and reliability;
        /// </summary>
        public async Task<CalibrationResult> CalculateCalibrationAsync(
            CalibrationRequest request,
            CancellationToken cancellationToken = default)
        {
            // Implementation for calibration metrics;
            // Calculates calibration curves, ECE, MCE, reliability diagrams;
        }

        /// <summary>
        /// Performs fairness and bias analysis;
        /// </summary>
        public async Task<FairnessAnalysisResult> AnalyzeFairnessAsync(
            FairnessAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            // Implementation for fairness analysis;
            // Calculates demographic parity, equalized odds, disparate impact;
        }

        private async Task<MetricCalculationSession> GetOrCreateSessionAsync(
            string sessionId,
            SessionConfiguration sessionConfig,
            CancellationToken cancellationToken)
        {
            if (_activeSessions.TryGetValue(sessionId, out var existingSession))
            {
                existingSession.LastAccessed = DateTime.UtcNow;
                existingSession.AccessCount++;
                return existingSession;
            }

            var newSession = new MetricCalculationSession;
            {
                Id = sessionId,
                CreatedAt = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
                AccessCount = 1,
                Config = sessionConfig ?? new SessionConfiguration(),
                Metrics = new CalculationMetrics()
            };

            _activeSessions.TryAdd(sessionId, newSession);

            _logger.LogDebug("Created new metrics calculation session {SessionId}", sessionId);

            return newSession;
        }

        private async Task<CalculationValidationResult> ValidateCalculationRequestAsync(
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            // Validate data;
            if (request.GroundTruth == null || !request.GroundTruth.Any())
                errors.Add("Ground truth data is required");

            if (request.Predictions == null || !request.Predictions.Any())
                errors.Add("Prediction data is required");

            // Validate data consistency;
            if (request.GroundTruth.Count() != request.Predictions.Count())
                errors.Add("Ground truth and predictions must have the same number of samples");

            // Validate problem type;
            if (!Enum.IsDefined(typeof(ProblemType), request.ProblemType))
                errors.Add($"Invalid problem type: {request.ProblemType}");

            // Validate metric types;
            if (request.MetricTypes != null)
            {
                foreach (var metricType in request.MetricTypes)
                {
                    if (!_metricRegistry.IsMetricAvailable(metricType, request.ProblemType))
                        warnings.Add($"Metric {metricType} may not be appropriate for {request.ProblemType}");
                }
            }

            // Validate thresholds;
            if (request.Thresholds != null)
            {
                foreach (var threshold in request.Thresholds)
                {
                    if (threshold.Value < 0 || threshold.Value > 1)
                        errors.Add($"Threshold {threshold.Key} must be between 0 and 1");
                }
            }

            return new CalculationValidationResult;
            {
                IsValid = !errors.Any(),
                Errors = errors,
                Warnings = warnings,
                SessionId = request.SessionId,
                ValidationTime = DateTime.UtcNow;
            };
        }

        private async Task<CalculationData> LoadAndPrepareDataAsync(
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            var data = new CalculationData;
            {
                SessionId = request.SessionId,
                LoadedAt = DateTime.UtcNow;
            };

            // Load ground truth;
            data.GroundTruth = await Task.Run(() =>
                request.GroundTruth.ToList(), cancellationToken);

            // Load predictions;
            data.Predictions = await Task.Run(() =>
                request.Predictions.ToList(), cancellationToken);

            // Load confidence scores if available;
            if (request.ConfidenceScores != null)
            {
                data.ConfidenceScores = await Task.Run(() =>
                    request.ConfidenceScores.ToList(), cancellationToken);
            }

            // Load sample weights if available;
            if (request.SampleWeights != null)
            {
                data.SampleWeights = await Task.Run(() =>
                    request.SampleWeights.ToList(), cancellationToken);
            }

            // Load additional data;
            if (request.AdditionalData != null)
            {
                data.AdditionalData = request.AdditionalData;
            }

            // Preprocess data based on problem type;
            data = await PreprocessDataAsync(data, request.ProblemType, cancellationToken);

            // Validate data integrity;
            await ValidateDataIntegrityAsync(data, request, cancellationToken);

            return data;
        }

        private async Task<List<MetricDefinition>> SelectMetricsAsync(
            ProblemType problemType,
            List<MetricType> requestedMetrics,
            MetricOptions options,
            CancellationToken cancellationToken)
        {
            var selectedMetrics = new List<MetricDefinition>();

            // Get all available metrics for the problem type;
            var availableMetrics = await _metricRegistry.GetMetricsForProblemTypeAsync(
                problemType,
                cancellationToken);

            // If specific metrics requested, filter to those;
            if (requestedMetrics != null && requestedMetrics.Any())
            {
                selectedMetrics = availableMetrics;
                    .Where(m => requestedMetrics.Contains(m.Type))
                    .OrderBy(m => requestedMetrics.IndexOf(m.Type))
                    .ToList();
            }
            else;
            {
                // Select default metrics based on problem type;
                selectedMetrics = await GetDefaultMetricsAsync(
                    problemType,
                    options,
                    cancellationToken);
            }

            // Apply metric options;
            selectedMetrics = await ApplyMetricOptionsAsync(
                selectedMetrics,
                options,
                cancellationToken);

            return selectedMetrics;
                .Take(_config.MaxMetricsPerCalculation)
                .ToList();
        }

        private async Task<MetricResult> CalculateSingleMetricAsync(
            MetricDefinition metric,
            CalculationData data,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            var calculationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                _logger.LogDebug("Calculating metric {MetricName} ({CalculationId})",
                    metric.Name, calculationId);

                // Get calculator for metric;
                var calculator = await _metricRegistry.GetCalculatorAsync(
                    metric.Type,
                    cancellationToken);

                // Prepare metric parameters;
                var parameters = await PrepareMetricParametersAsync(
                    metric,
                    request,
                    cancellationToken);

                // Calculate metric;
                var rawResult = await calculator.CalculateAsync(
                    data.GroundTruth,
                    data.Predictions,
                    data.ConfidenceScores,
                    data.SampleWeights,
                    parameters,
                    cancellationToken);

                // Post-process result;
                var processedResult = await PostProcessMetricResultAsync(
                    rawResult,
                    metric,
                    data,
                    cancellationToken);

                // Validate result;
                var validation = await ValidateMetricResultAsync(
                    processedResult,
                    metric,
                    cancellationToken);

                var result = new MetricResult;
                {
                    MetricId = calculationId,
                    MetricName = metric.Name,
                    MetricType = metric.Type,
                    Value = processedResult.Value,
                    ConfidenceInterval = processedResult.ConfidenceInterval,
                    StandardError = processedResult.StandardError,
                    SampleSize = data.GroundTruth.Count,
                    CalculationTime = DateTime.UtcNow - startTime,
                    Parameters = parameters,
                    Interpretation = await GenerateInterpretationAsync(
                        processedResult,
                        metric,
                        data,
                        cancellationToken),
                    IsValid = validation.IsValid,
                    Warnings = validation.Warnings,
                    CalculatedAt = DateTime.UtcNow,
                    AdditionalData = processedResult.AdditionalData;
                };

                _logger.LogDebug("Metric {MetricName} calculated: {Value:F4} (CI: {ConfidenceInterval})",
                    metric.Name, processedResult.Value, processedResult.ConfidenceInterval);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating metric {MetricName}", metric.Name);
                throw new MetricCalculationException(
                    $"Failed to calculate metric {metric.Name}",
                    metric.Type,
                    ex);
            }
        }

        private async Task<Dictionary<string, AggregatedMetric>> CalculateAggregatedMetricsAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            MetricsCalculationRequest request,
            CalculationData data,
            CancellationToken cancellationToken)
        {
            var aggregatedMetrics = new Dictionary<string, AggregatedMetric>();

            // Calculate overall score if requested;
            if (request.CalculateOverallScore && metricResults.Any())
            {
                var overallScore = await CalculateOverallScoreAsync(
                    metricResults,
                    request.ScoringWeights,
                    cancellationToken);

                aggregatedMetrics["overall_score"] = new AggregatedMetric;
                {
                    Name = "Overall Score",
                    Value = overallScore.Value,
                    ConfidenceInterval = overallScore.ConfidenceInterval,
                    Components = metricResults.Keys.ToList(),
                    CalculationMethod = "Weighted Average",
                    CalculatedAt = DateTime.UtcNow;
                };
            }

            // Calculate composite metrics;
            if (request.CompositeMetrics != null && request.CompositeMetrics.Any())
            {
                foreach (var composite in request.CompositeMetrics)
                {
                    var compositeResult = await CalculateCompositeMetricAsync(
                        composite,
                        metricResults,
                        data,
                        cancellationToken);

                    if (compositeResult != null)
                    {
                        aggregatedMetrics[composite.Name] = compositeResult;
                    }
                }
            }

            // Calculate statistical aggregates;
            if (request.CalculateStatisticalAggregates)
            {
                var statisticalAggregates = await CalculateStatisticalAggregatesAsync(
                    metricResults,
                    data,
                    cancellationToken);

                foreach (var aggregate in statisticalAggregates)
                {
                    aggregatedMetrics[aggregate.Key] = aggregate.Value;
                }
            }

            return aggregatedMetrics;
        }

        private async Task<StatisticalAnalysis> PerformStatisticalAnalysisAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            CalculationData data,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            var analysis = new StatisticalAnalysis;
            {
                SessionId = request.SessionId,
                AnalyzedAt = DateTime.UtcNow,
                Analyses = new Dictionary<string, object>()
            };

            // Perform distribution analysis;
            if (request.PerformDistributionAnalysis)
            {
                analysis.DistributionAnalysis = await _statisticalAnalyzer.AnalyzeDistributionsAsync(
                    data.GroundTruth,
                    data.Predictions,
                    request.ProblemType,
                    cancellationToken);
            }

            // Perform correlation analysis;
            if (request.PerformCorrelationAnalysis && metricResults.Count > 1)
            {
                analysis.CorrelationAnalysis = await _statisticalAnalyzer.AnalyzeCorrelationsAsync(
                    metricResults,
                    cancellationToken);
            }

            // Perform trend analysis if time series;
            if (request.PerformTrendAnalysis && data.AdditionalData?.ContainsKey("timestamps") == true)
            {
                analysis.TrendAnalysis = await _statisticalAnalyzer.AnalyzeTrendsAsync(
                    metricResults,
                    data.AdditionalData["timestamps"] as List<DateTime>,
                    cancellationToken);
            }

            // Perform significance testing;
            if (request.PerformSignificanceTesting)
            {
                analysis.SignificanceTesting = await _statisticalAnalyzer.PerformSignificanceTestsAsync(
                    metricResults,
                    data,
                    cancellationToken);
            }

            return analysis;
        }

        private async Task<ComparativeAnalysis> PerformComparativeAnalysisAsync(
            ConcurrentDictionary<string, MetricResult> currentResults,
            Dictionary<string, MetricResult> baselineResults,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            var analysis = new ComparativeAnalysis;
            {
                SessionId = request.SessionId,
                AnalyzedAt = DateTime.UtcNow,
                Comparisons = new Dictionary<string, MetricComparison>()
            };

            foreach (var kvp in currentResults)
            {
                var metricName = kvp.Key;
                var currentResult = kvp.Value;

                if (baselineResults.TryGetValue(metricName, out var baselineResult))
                {
                    var comparison = await CompareMetricsAsync(
                        currentResult,
                        baselineResult,
                        cancellationToken);

                    analysis.Comparisons[metricName] = comparison;

                    // Track improvement/deterioration;
                    if (comparison.Improvement > 0)
                        analysis.Improvements.Add(metricName);
                    else if (comparison.Improvement < 0)
                        analysis.Deteriorations.Add(metricName);
                }
            }

            // Calculate overall comparison;
            analysis.OverallComparison = await CalculateOverallComparisonAsync(
                currentResults,
                baselineResults,
                cancellationToken);

            return analysis;
        }

        private async Task<MetricsCalculationResult> GenerateMetricsReportAsync(
            string calculationId,
            ConcurrentDictionary<string, MetricResult> metricResults,
            Dictionary<string, AggregatedMetric> aggregatedMetrics,
            StatisticalAnalysis statisticalAnalysis,
            ComparativeAnalysis comparativeAnalysis,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            // Generate summary;
            var summary = await GenerateSummaryAsync(
                metricResults,
                aggregatedMetrics,
                request,
                cancellationToken);

            // Generate insights;
            var insights = await GenerateInsightsAsync(
                metricResults,
                statisticalAnalysis,
                comparativeAnalysis,
                request,
                cancellationToken);

            // Generate recommendations;
            var recommendations = await GenerateRecommendationsAsync(
                metricResults,
                aggregatedMetrics,
                statisticalAnalysis,
                request,
                cancellationToken);

            // Generate visualizations;
            var visualizations = request.GenerateVisualizations;
                ? await GenerateVisualizationsAsync(
                    metricResults,
                    statisticalAnalysis,
                    request,
                    cancellationToken)
                : new Dictionary<string, VisualizationData>();

            var report = new MetricsCalculationResult;
            {
                CalculationId = calculationId,
                SessionId = request.SessionId,
                Success = true,
                Metrics = metricResults.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AggregatedMetrics = aggregatedMetrics,
                StatisticalAnalysis = statisticalAnalysis,
                ComparativeAnalysis = comparativeAnalysis,
                Summary = summary,
                Insights = insights,
                Recommendations = recommendations,
                Visualizations = visualizations,
                CalculationTime = DateTime.UtcNow,
                ProcessingTime = DateTime.UtcNow - DateTime.UtcNow, // Will track actual;
                Metadata = new MetricsMetadata;
                {
                    ProblemType = request.ProblemType,
                    MetricCount = metricResults.Count,
                    SampleSize = metricResults.First().Value?.SampleSize ?? 0,
                    ConfidenceLevel = request.ConfidenceLevel,
                    ValidationStatus = MetricsValidationStatus.Validated;
                }
            };

            return report;
        }

        private async Task<ResultsValidation> ValidateResultsAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            Dictionary<string, AggregatedMetric> aggregatedMetrics,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            return await _validationEngine.ValidateAsync(
                metricResults,
                aggregatedMetrics,
                request,
                cancellationToken);
        }

        private async Task UpdateSessionWithResultsAsync(
            MetricCalculationSession session,
            MetricsCalculationResult result,
            ConcurrentDictionary<string, MetricResult> metricResults,
            CancellationToken cancellationToken)
        {
            session.LastResult = result;
            session.TotalCalculations++;
            session.TotalMetrics += metricResults.Count;
            session.LastAccessed = DateTime.UtcNow;

            // Update session metrics;
            session.Metrics.TotalMetricsCalculated += metricResults.Count;
            session.Metrics.AverageMetricCount = session.Metrics.TotalMetricsCalculated / session.TotalCalculations;

            if (result.AggregatedMetrics?.ContainsKey("overall_score") == true)
            {
                var overallScore = result.AggregatedMetrics["overall_score"].Value;
                session.Metrics.TotalScore += overallScore;
                session.Metrics.AverageScore = session.Metrics.TotalScore / session.TotalCalculations;

                if (overallScore > session.Metrics.BestScore)
                {
                    session.Metrics.BestScore = overallScore;
                    session.Metrics.BestCalculationId = result.CalculationId;
                }
            }
        }

        private async Task<CalculationData> PreprocessDataAsync(
            CalculationData data,
            ProblemType problemType,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Handle different data types based on problem type;
                switch (problemType)
                {
                    case ProblemType.Classification:
                        // Ensure predictions are valid probabilities or class labels;
                        data = PreprocessClassificationData(data);
                        break;

                    case ProblemType.Regression:
                        // Handle outliers, scale if needed;
                        data = PreprocessRegressionData(data);
                        break;

                    case ProblemType.MultiLabelClassification:
                        // Convert to multi-hot encoding if needed;
                        data = PreprocessMultiLabelData(data);
                        break;

                    case ProblemType.Ranking:
                        // Sort by scores, handle ties;
                        data = PreprocessRankingData(data);
                        break;
                }

                return data;
            }, cancellationToken);
        }

        private async Task ValidateDataIntegrityAsync(
            CalculationData data,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            var issues = new List<string>();

            // Check for NaN or infinite values;
            await CheckForInvalidValuesAsync(data, issues, cancellationToken);

            // Check class distribution for classification;
            if (request.ProblemType == ProblemType.Classification ||
                request.ProblemType == ProblemType.MultiLabelClassification)
            {
                await CheckClassDistributionAsync(data, issues, cancellationToken);
            }

            // Check for data leakage;
            await CheckForDataLeakageAsync(data, request, issues, cancellationToken);

            if (issues.Any())
            {
                throw new InvalidDataException(
                    $"Data integrity issues: {string.Join(", ", issues)}",
                    data.SessionId);
            }
        }

        private async Task<List<MetricDefinition>> GetDefaultMetricsAsync(
            ProblemType problemType,
            MetricOptions options,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var metrics = new List<MetricDefinition>();

                switch (problemType)
                {
                    case ProblemType.Classification:
                        metrics.AddRange(GetClassificationMetrics(options));
                        break;

                    case ProblemType.Regression:
                        metrics.AddRange(GetRegressionMetrics(options));
                        break;

                    case ProblemType.MultiLabelClassification:
                        metrics.AddRange(GetMultiLabelMetrics(options));
                        break;

                    case ProblemType.Ranking:
                        metrics.AddRange(GetRankingMetrics(options));
                        break;

                    case ProblemType.Clustering:
                        metrics.AddRange(GetClusteringMetrics(options));
                        break;

                    case ProblemType.AnomalyDetection:
                        metrics.AddRange(GetAnomalyDetectionMetrics(options));
                        break;
                }

                return metrics;
            }, cancellationToken);
        }

        private List<MetricDefinition> GetClassificationMetrics(MetricOptions options)
        {
            var metrics = new List<MetricDefinition>
            {
                new() { Name = "Accuracy", Type = MetricType.Accuracy, Priority = 1 },
                new() { Name = "Precision", Type = MetricType.Precision, Priority = 2 },
                new() { Name = "Recall", Type = MetricType.Recall, Priority = 3 },
                new() { Name = "F1 Score", Type = MetricType.F1Score, Priority = 4 }
            };

            if (options?.CalculateProbabilisticMetrics == true)
            {
                metrics.AddRange(new[]
                {
                    new MetricDefinition { Name = "ROC AUC", Type = MetricType.ROCAUC, Priority = 5 },
                    new MetricDefinition { Name = "PR AUC", Type = MetricType.PRAUC, Priority = 6 },
                    new MetricDefinition { Name = "Log Loss", Type = MetricType.LogLoss, Priority = 7 }
                });
            }

            if (options?.CalculateMultiClassMetrics == true)
            {
                metrics.Add(new MetricDefinition;
                {
                    Name = "Macro F1",
                    Type = MetricType.MacroF1,
                    Priority = 8;
                });
            }

            return metrics.OrderBy(m => m.Priority).ToList();
        }

        private async Task<Dictionary<string, object>> PrepareMetricParametersAsync(
            MetricDefinition metric,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, object>();

            // Add thresholds if applicable;
            if (request.Thresholds != null && request.Thresholds.ContainsKey(metric.Name))
            {
                parameters["threshold"] = request.Thresholds[metric.Name];
            }

            // Add problem-specific parameters;
            switch (metric.Type)
            {
                case MetricType.Precision:
                case MetricType.Recall:
                case MetricType.F1Score:
                    parameters["average"] = request.AverageMethod ?? "binary";
                    if (request.PositiveLabel.HasValue)
                        parameters["positive_label"] = request.PositiveLabel.Value;
                    break;

                case MetricType.ROCAUC:
                case MetricType.PRAUC:
                    parameters["multi_class"] = request.MultiClassStrategy ?? "ovr";
                    break;

                case MetricType.MAP:
                case MetricType.NDCG:
                    parameters["k"] = request.TopK ?? 10;
                    break;
            }

            // Add custom parameters;
            if (request.MetricParameters != null &&
                request.MetricParameters.ContainsKey(metric.Name))
            {
                foreach (var kvp in request.MetricParameters[metric.Name])
                {
                    parameters[kvp.Key] = kvp.Value;
                }
            }

            return await Task.FromResult(parameters);
        }

        private async Task<AggregatedMetric> CalculateOverallScoreAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            Dictionary<string, double> scoringWeights,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var totalScore = 0.0;
                var totalWeight = 0.0;

                foreach (var kvp in metricResults)
                {
                    var metricName = kvp.Key;
                    var metricResult = kvp.Value;
                    var weight = scoringWeights?.ContainsKey(metricName) == true;
                        ? scoringWeights[metricName]
                        : 1.0;

                    totalScore += metricResult.Value * weight;
                    totalWeight += weight;
                }

                var overallScore = totalWeight > 0 ? totalScore / totalWeight : 0;

                // Calculate confidence interval for overall score;
                var confidenceInterval = CalculateOverallConfidenceInterval(
                    metricResults,
                    scoringWeights);

                return new AggregatedMetric;
                {
                    Name = "Overall Score",
                    Value = overallScore,
                    ConfidenceInterval = confidenceInterval,
                    Components = metricResults.Keys.ToList(),
                    CalculationMethod = "Weighted Average",
                    CalculatedAt = DateTime.UtcNow;
                };
            }, cancellationToken);
        }

        private ConfidenceInterval CalculateOverallConfidenceInterval(
            ConcurrentDictionary<string, MetricResult> metricResults,
            Dictionary<string, double> scoringWeights)
        {
            // Simplified confidence interval calculation;
            // In practice, would use more sophisticated statistical methods;
            var values = metricResults.Values;
                .Select(r => r.Value)
                .ToList();

            if (!values.Any())
                return new ConfidenceInterval(0, 0, 0.95);

            var mean = values.Average();
            var std = Math.Sqrt(values.Select(v => Math.Pow(v - mean, 2)).Sum() / values.Count);
            var margin = 1.96 * std / Math.Sqrt(values.Count); // 95% confidence;

            return new ConfidenceInterval(
                mean - margin,
                mean + margin,
                0.95);
        }

        private async Task<MetricComparison> CompareMetricsAsync(
            MetricResult current,
            MetricResult baseline,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var improvement = current.Value - baseline.Value;
                var relativeImprovement = baseline.Value != 0;
                    ? improvement / Math.Abs(baseline.Value)
                    : 0;

                var isSignificant = false;
                var effectSize = 0.0;

                // Calculate statistical significance if confidence intervals available;
                if (current.ConfidenceInterval != null && baseline.ConfidenceInterval != null)
                {
                    isSignificant = CheckStatisticalSignificance(
                        current,
                        baseline);

                    effectSize = CalculateEffectSize(
                        current,
                        baseline);
                }

                return new MetricComparison;
                {
                    MetricName = current.MetricName,
                    CurrentValue = current.Value,
                    BaselineValue = baseline.Value,
                    Improvement = improvement,
                    RelativeImprovement = relativeImprovement,
                    IsSignificant = isSignificant,
                    EffectSize = effectSize,
                    SignificanceLevel = 0.05,
                    ComparedAt = DateTime.UtcNow;
                };
            }, cancellationToken);
        }

        private bool CheckStatisticalSignificance(MetricResult current, MetricResult baseline)
        {
            // Simplified significance check;
            // In practice, would use proper statistical tests;
            if (current.ConfidenceInterval == null || baseline.ConfidenceInterval == null)
                return false;

            var currentLower = current.ConfidenceInterval.LowerBound;
            var currentUpper = current.ConfidenceInterval.UpperBound;
            var baselineLower = baseline.ConfidenceInterval.LowerBound;
            var baselineUpper = baseline.ConfidenceInterval.UpperBound;

            // Check if confidence intervals overlap;
            return !(currentLower <= baselineUpper && currentUpper >= baselineLower);
        }

        private double CalculateEffectSize(MetricResult current, MetricResult baseline)
        {
            // Cohen's d effect size;
            if (current.StandardError == null || baseline.StandardError == null)
                return 0;

            var pooledStd = Math.Sqrt(
                (Math.Pow(current.StandardError.Value, 2) +
                 Math.Pow(baseline.StandardError.Value, 2)) / 2);

            return pooledStd > 0;
                ? (current.Value - baseline.Value) / pooledStd;
                : 0;
        }

        private async Task<Summary> GenerateSummaryAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            Dictionary<string, AggregatedMetric> aggregatedMetrics,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var summary = new Summary;
                {
                    GeneratedAt = DateTime.UtcNow,
                    KeyMetrics = new Dictionary<string, double>(),
                    KeyInsights = new List<string>()
                };

                // Add key metrics;
                var topMetrics = metricResults;
                    .OrderByDescending(kvp => kvp.Value.Value)
                    .Take(5)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value);

                foreach (var kvp in topMetrics)
                {
                    summary.KeyMetrics[kvp.Key] = kvp.Value;
                }

                // Add aggregated metrics;
                if (aggregatedMetrics.ContainsKey("overall_score"))
                {
                    summary.KeyMetrics["overall_score"] = aggregatedMetrics["overall_score"].Value;
                }

                // Generate insights;
                if (metricResults.TryGetValue("accuracy", out var accuracy))
                {
                    if (accuracy.Value > 0.9)
                        summary.KeyInsights.Add("Excellent accuracy achieved (>90%)");
                    else if (accuracy.Value > 0.8)
                        summary.KeyInsights.Add("Good accuracy achieved (>80%)");
                }

                if (metricResults.TryGetValue("precision", out var precision) &&
                    metricResults.TryGetValue("recall", out var recall))
                {
                    if (precision.Value > recall.Value * 1.2)
                        summary.KeyInsights.Add("Model is conservative (high precision, lower recall)");
                    else if (recall.Value > precision.Value * 1.2)
                        summary.KeyInsights.Add("Model is aggressive (high recall, lower precision)");
                }

                summary.OverallAssessment = GenerateOverallAssessment(
                    metricResults,
                    aggregatedMetrics);

                return summary;
            }, cancellationToken);
        }

        private string GenerateOverallAssessment(
            ConcurrentDictionary<string, MetricResult> metricResults,
            Dictionary<string, AggregatedMetric> aggregatedMetrics)
        {
            if (!metricResults.Any())
                return "No metrics calculated";

            var averageScore = metricResults.Average(kvp => kvp.Value.Value);
            var overallScore = aggregatedMetrics.ContainsKey("overall_score")
                ? aggregatedMetrics["overall_score"].Value;
                : averageScore;

            if (overallScore > 0.9)
                return "Excellent performance";
            if (overallScore > 0.8)
                return "Very good performance";
            if (overallScore > 0.7)
                return "Good performance";
            if (overallScore > 0.6)
                return "Acceptable performance";

            return "Performance needs improvement";
        }

        private void CleanupExpiredSessions(object state)
        {
            try
            {
                var expirationThreshold = DateTime.UtcNow.AddHours(-_config.SessionExpirationHours);
                var sessionsToRemove = _activeSessions;
                    .Where(kvp => kvp.Value.LastAccessed < expirationThreshold)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var sessionId in sessionsToRemove)
                {
                    _activeSessions.TryRemove(sessionId, out _);
                }

                if (sessionsToRemove.Any())
                {
                    _logger.LogDebug("Cleaned up {Count} expired metrics sessions", sessionsToRemove.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up expired sessions");
            }
        }

        private MetricsCalculationResult CreateErrorResult(
            string calculationId,
            string sessionId,
            string error)
        {
            return new MetricsCalculationResult;
            {
                CalculationId = calculationId,
                SessionId = sessionId,
                Success = false,
                Error = new MetricsError;
                {
                    Code = ErrorCodes.MetricsCalculationFailed,
                    Message = error,
                    Timestamp = DateTime.UtcNow;
                },
                CalculationTime = DateTime.UtcNow;
            };
        }

        private MetricsCalculationResult CreateInvalidDataResult(
            string calculationId,
            string sessionId,
            InvalidDataException ex)
        {
            return new MetricsCalculationResult;
            {
                CalculationId = calculationId,
                SessionId = sessionId,
                Success = false,
                Error = new MetricsError;
                {
                    Code = ErrorCodes.InvalidData,
                    Message = ex.Message,
                    Details = ex.DataDetails,
                    Timestamp = DateTime.UtcNow;
                },
                CalculationTime = DateTime.UtcNow;
            };
        }

        private MetricsCalculationResult CreateMetricErrorResult(
            string calculationId,
            string sessionId,
            MetricCalculationException ex)
        {
            return new MetricsCalculationResult;
            {
                CalculationId = calculationId,
                SessionId = sessionId,
                Success = false,
                Error = new MetricsError;
                {
                    Code = ErrorCodes.MetricCalculationError,
                    Message = ex.Message,
                    Details = ex.MetricType.ToString(),
                    Timestamp = DateTime.UtcNow;
                },
                CalculationTime = DateTime.UtcNow;
            };
        }

        private MetricsCalculationResult CreateStatisticalErrorResult(
            string calculationId,
            string sessionId,
            StatisticalAnalysisException ex)
        {
            return new MetricsCalculationResult;
            {
                CalculationId = calculationId,
                SessionId = sessionId,
                Success = false,
                Error = new MetricsError;
                {
                    Code = ErrorCodes.StatisticalAnalysisError,
                    Message = ex.Message,
                    Details = ex.AnalysisType,
                    Timestamp = DateTime.UtcNow;
                },
                CalculationTime = DateTime.UtcNow;
            };
        }

        private MetricResult CreateMetricError(string metricName, string error)
        {
            return new MetricResult;
            {
                MetricName = metricName,
                Value = double.NaN,
                IsValid = false,
                Warnings = new List<string> { error },
                CalculatedAt = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Gets calculator statistics and performance metrics;
        /// </summary>
        public MetricsCalculatorStatistics GetStatistics()
        {
            var uptime = DateTime.UtcNow - _startTime;

            return new MetricsCalculatorStatistics;
            {
                TotalMetricsCalculated = _totalMetricsCalculated,
                Uptime = uptime,
                ActiveSessions = _activeSessions.Count,
                ActiveCalculations = _config.MaxConcurrentCalculations - _calculationSemaphore.CurrentCount,
                MemoryUsage = GC.GetTotalMemory(false) / (1024 * 1024), // MB;
                AverageMetricsPerCalculation = CalculateAverageMetricsPerCalculation(),
                MetricRegistryStats = _metricRegistry.GetStatistics()
            };
        }

        private double CalculateAverageMetricsPerCalculation()
        {
            var sessions = _activeSessions.Values;
            if (!sessions.Any() || sessions.Sum(s => s.TotalCalculations) == 0)
                return 0;

            var totalMetrics = sessions.Sum(s => s.TotalMetrics);
            var totalCalculations = sessions.Sum(s => s.TotalCalculations);

            return (double)totalMetrics / totalCalculations;
        }

        /// <summary>
        /// Clears all calculation sessions;
        /// </summary>
        public void ClearSessions()
        {
            _activeSessions.Clear();
            _logger.LogInformation("All metrics calculation sessions cleared");
        }

        /// <summary>
        /// Gets a specific calculation session;
        /// </summary>
        public MetricCalculationSession GetSession(string sessionId)
        {
            return _activeSessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        /// <summary>
        /// Exports calculation session data;
        /// </summary>
        public async Task<CalculationSessionExport> ExportSessionAsync(
            string sessionId,
            ExportOptions options,
            CancellationToken cancellationToken = default)
        {
            var session = GetSession(sessionId);
            if (session == null)
                throw new KeyNotFoundException($"Session {sessionId} not found");

            return await Task.Run(() =>
            {
                return new CalculationSessionExport;
                {
                    SessionId = sessionId,
                    CreatedAt = session.CreatedAt,
                    TotalCalculations = session.TotalCalculations,
                    TotalMetrics = session.TotalMetrics,
                    TotalTime = session.TotalTime,
                    BestScore = session.Metrics.BestScore,
                    BestCalculationId = session.Metrics.BestCalculationId,
                    AverageScore = session.Metrics.AverageScore,
                    AverageMetricCount = session.Metrics.AverageMetricCount,
                    LastResult = options.IncludeResults ? session.LastResult : null,
                    ExportedAt = DateTime.UtcNow;
                };
            }, cancellationToken);
        }

        /// <summary>
        /// Disposes the calculator resources;
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
                    _calculationSemaphore?.Dispose();
                    _sessionCleanupTimer?.Dispose();
                    _metricRegistry?.Dispose();
                    _statisticalAnalyzer?.Dispose();
                    ClearSessions();
                    _logger.LogInformation("MetricsCalculator disposed");
                }
                _disposed = true;
            }
        }

        ~MetricsCalculator()
        {
            Dispose(false);
        }

        // Helper methods for data preprocessing;
        private CalculationData PreprocessClassificationData(CalculationData data)
        {
            // Implement classification data preprocessing;
            return data;
        }

        private CalculationData PreprocessRegressionData(CalculationData data)
        {
            // Implement regression data preprocessing;
            return data;
        }

        private CalculationData PreprocessMultiLabelData(CalculationData data)
        {
            // Implement multi-label data preprocessing;
            return data;
        }

        private CalculationData PreprocessRankingData(CalculationData data)
        {
            // Implement ranking data preprocessing;
            return data;
        }

        private async Task CheckForInvalidValuesAsync(CalculationData data, List<string> issues, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Check ground truth;
                if (data.GroundTruth.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    issues.Add("Ground truth contains invalid values (NaN or Infinity)");

                // Check predictions;
                if (data.Predictions.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    issues.Add("Predictions contain invalid values (NaN or Infinity)");
            }, cancellationToken);
        }

        private async Task CheckClassDistributionAsync(CalculationData data, List<string> issues, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Count class frequencies;
                var classCounts = data.GroundTruth;
                    .GroupBy(v => v)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Check for class imbalance;
                var total = classCounts.Values.Sum();
                var minCount = classCounts.Values.Min();
                var maxCount = classCounts.Values.Max();

                if (maxCount > minCount * 10)
                    issues.Add("Severe class imbalance detected");

                // Check for missing classes in predictions;
                var predictedClasses = data.Predictions.Distinct();
                var missingClasses = classCounts.Keys.Except(predictedClasses);

                if (missingClasses.Any())
                    issues.Add($"Some classes not predicted: {string.Join(", ", missingClasses)}");
            }, cancellationToken);
        }

        private async Task CheckForDataLeakageAsync(
            CalculationData data,
            MetricsCalculationRequest request,
            List<string> issues,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Implement data leakage checks;
                // This would involve checking for duplicate samples, temporal leakage, etc.
            }, cancellationToken);
        }

        // Additional helper methods for metric calculation;
        private async Task<List<MetricDefinition>> ApplyMetricOptionsAsync(
            List<MetricDefinition> metrics,
            MetricOptions options,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                if (options == null)
                    return metrics;

                var filteredMetrics = metrics.Where(m =>
                {
                    // Filter based on options;
                    if (options.ExcludeMetrics?.Contains(m.Type) == true)
                        return false;

                    if (options.RequiredMetrics?.Contains(m.Type) == true)
                        return true;

                    // Apply other filters;
                    return true;
                }).ToList();

                return filteredMetrics;
            }, cancellationToken);
        }

        private async Task<MetricResult> PostProcessMetricResultAsync(
            RawMetricResult rawResult,
            MetricDefinition metric,
            CalculationData data,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Apply scaling or transformation if needed;
                var processedValue = rawResult.Value;

                // Calculate confidence interval if not provided;
                var confidenceInterval = rawResult.ConfidenceInterval ??
                    CalculateDefaultConfidenceInterval(processedValue, data.GroundTruth.Count);

                // Calculate standard error;
                var standardError = rawResult.StandardError ??
                    CalculateStandardError(processedValue, data.GroundTruth.Count);

                return new MetricResult;
                {
                    MetricName = metric.Name,
                    MetricType = metric.Type,
                    Value = processedValue,
                    ConfidenceInterval = confidenceInterval,
                    StandardError = standardError,
                    SampleSize = data.GroundTruth.Count,
                    AdditionalData = rawResult.AdditionalData;
                };
            }, cancellationToken);
        }

        private ConfidenceInterval CalculateDefaultConfidenceInterval(double value, int sampleSize)
        {
            // Simplified confidence interval calculation;
            // In practice, would use proper statistical methods based on metric type;
            if (sampleSize <= 1)
                return new ConfidenceInterval(value, value, 0.95);

            var std = Math.Sqrt(value * (1 - value) / sampleSize);
            var margin = 1.96 * std; // 95% confidence;

            return new ConfidenceInterval(
                Math.Max(0, value - margin),
                Math.Min(1, value + margin),
                0.95);
        }

        private double? CalculateStandardError(double value, int sampleSize)
        {
            if (sampleSize <= 1)
                return null;

            return Math.Sqrt(value * (1 - value) / sampleSize);
        }

        private async Task<ValidationResult> ValidateMetricResultAsync(
            MetricResult result,
            MetricDefinition metric,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var validation = new ValidationResult;
                {
                    IsValid = true,
                    Warnings = new List<string>()
                };

                // Check for valid value range;
                if (double.IsNaN(result.Value) || double.IsInfinity(result.Value))
                {
                    validation.IsValid = false;
                    validation.Errors.Add("Metric value is NaN or Infinity");
                }

                // Check value bounds based on metric type;
                switch (metric.Type)
                {
                    case MetricType.Accuracy:
                    case MetricType.Precision:
                    case MetricType.Recall:
                    case MetricType.F1Score:
                    case MetricType.ROCAUC:
                    case MetricType.PRAUC:
                        if (result.Value < 0 || result.Value > 1)
                            validation.Warnings.Add("Metric value outside typical range [0, 1]");
                        break;
                }

                // Check confidence interval;
                if (result.ConfidenceInterval != null)
                {
                    if (result.ConfidenceInterval.LowerBound > result.ConfidenceInterval.UpperBound)
                    {
                        validation.Warnings.Add("Confidence interval lower bound exceeds upper bound");
                    }
                }

                return validation;
            }, cancellationToken);
        }

        private async Task<string> GenerateInterpretationAsync(
            MetricResult result,
            MetricDefinition metric,
            CalculationData data,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                return result.Value switch;
                {
                    > 0.9 => $"Excellent {metric.Name}",
                    > 0.8 => $"Very good {metric.Name}",
                    > 0.7 => $"Good {metric.Name}",
                    > 0.6 => $"Acceptable {metric.Name}",
                    > 0.5 => $"Moderate {metric.Name}",
                    _ => $"Poor {metric.Name}"
                };
            }, cancellationToken);
        }

        // Additional metric category getters;
        private List<MetricDefinition> GetRegressionMetrics(MetricOptions options)
        {
            return new List<MetricDefinition>
            {
                new() { Name = "Mean Absolute Error", Type = MetricType.MAE, Priority = 1 },
                new() { Name = "Mean Squared Error", Type = MetricType.MSE, Priority = 2 },
                new() { Name = "Root Mean Squared Error", Type = MetricType.RMSE, Priority = 3 },
                new() { Name = "R² Score", Type = MetricType.R2, Priority = 4 }
            };
        }

        private List<MetricDefinition> GetMultiLabelMetrics(MetricOptions options)
        {
            var metrics = new List<MetricDefinition>
            {
                new() { Name = "Hamming Loss", Type = MetricType.HammingLoss, Priority = 1 },
                new() { Name = "Subset Accuracy", Type = MetricType.SubsetAccuracy, Priority = 2 }
            };

            if (options?.CalculateProbabilisticMetrics == true)
            {
                metrics.Add(new MetricDefinition;
                {
                    Name = "Ranking Loss",
                    Type = MetricType.RankingLoss,
                    Priority = 3;
                });
            }

            return metrics;
        }

        private List<MetricDefinition> GetRankingMetrics(MetricOptions options)
        {
            return new List<MetricDefinition>
            {
                new() { Name = "Mean Average Precision", Type = MetricType.MAP, Priority = 1 },
                new() { Name = "Normalized Discounted Cumulative Gain", Type = MetricType.NDCG, Priority = 2 },
                new() { Name = "Precision at K", Type = MetricType.PrecisionAtK, Priority = 3 }
            };
        }

        private List<MetricDefinition> GetClusteringMetrics(MetricOptions options)
        {
            return new List<MetricDefinition>
            {
                new() { Name = "Silhouette Score", Type = MetricType.SilhouetteScore, Priority = 1 },
                new() { Name = "Davies-Bouldin Index", Type = MetricType.DaviesBouldinIndex, Priority = 2 }
            };
        }

        private List<MetricDefinition> GetAnomalyDetectionMetrics(MetricOptions options)
        {
            return new List<MetricDefinition>
            {
                new() { Name = "Average Precision", Type = MetricType.AveragePrecision, Priority = 1 },
                new() { Name = "Area Under Precision-Recall Curve", Type = MetricType.PRAUC, Priority = 2 }
            };
        }

        // Additional async helper methods;
        private async Task<AggregatedMetric> CalculateCompositeMetricAsync(
            CompositeMetricDefinition composite,
            ConcurrentDictionary<string, MetricResult> metricResults,
            CalculationData data,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Implement composite metric calculation;
                // Could be harmonic mean, arithmetic mean, custom formula, etc.
                return new AggregatedMetric;
                {
                    Name = composite.Name,
                    Value = 0.0, // Placeholder;
                    CalculatedAt = DateTime.UtcNow;
                };
            }, cancellationToken);
        }

        private async Task<Dictionary<string, AggregatedMetric>> CalculateStatisticalAggregatesAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            CalculationData data,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var aggregates = new Dictionary<string, AggregatedMetric>();

                if (metricResults.Any())
                {
                    var values = metricResults.Values.Select(r => r.Value).ToList();

                    aggregates["mean"] = new AggregatedMetric;
                    {
                        Name = "Mean",
                        Value = values.Average(),
                        CalculatedAt = DateTime.UtcNow;
                    };

                    aggregates["median"] = new AggregatedMetric;
                    {
                        Name = "Median",
                        Value = CalculateMedian(values),
                        CalculatedAt = DateTime.UtcNow;
                    };

                    aggregates["std"] = new AggregatedMetric;
                    {
                        Name = "Standard Deviation",
                        Value = CalculateStandardDeviation(values),
                        CalculatedAt = DateTime.UtcNow;
                    };
                }

                return aggregates;
            }, cancellationToken);
        }

        private double CalculateMedian(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var count = sorted.Count;

            if (count % 2 == 0)
                return (sorted[count / 2 - 1] + sorted[count / 2]) / 2;

            return sorted[count / 2];
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            var mean = values.Average();
            var sumSquares = values.Sum(v => Math.Pow(v - mean, 2));
            return Math.Sqrt(sumSquares / values.Count);
        }

        private async Task<OverallComparison> CalculateOverallComparisonAsync(
            ConcurrentDictionary<string, MetricResult> currentResults,
            Dictionary<string, MetricResult> baselineResults,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Calculate overall improvement;
                var improvements = new List<double>();

                foreach (var kvp in currentResults)
                {
                    if (baselineResults.TryGetValue(kvp.Key, out var baseline))
                    {
                        improvements.Add(kvp.Value.Value - baseline.Value);
                    }
                }

                var overallImprovement = improvements.Any() ? improvements.Average() : 0;

                return new OverallComparison;
                {
                    OverallImprovement = overallImprovement,
                    ImprovedMetrics = improvements.Count(i => i > 0),
                    WorsenedMetrics = improvements.Count(i => i < 0),
                    UnchangedMetrics = improvements.Count(i => Math.Abs(i) < 0.001),
                    ComparedAt = DateTime.UtcNow;
                };
            }, cancellationToken);
        }

        private async Task<List<Insight>> GenerateInsightsAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            StatisticalAnalysis statisticalAnalysis,
            ComparativeAnalysis comparativeAnalysis,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var insights = new List<Insight>();

                // Generate insights from metrics;
                foreach (var kvp in metricResults)
                {
                    var insight = GenerateMetricInsight(kvp.Value, request);
                    if (insight != null)
                        insights.Add(insight);
                }

                // Generate insights from comparative analysis;
                if (comparativeAnalysis != null)
                {
                    var comparativeInsights = GenerateComparativeInsights(comparativeAnalysis);
                    insights.AddRange(comparativeInsights);
                }

                // Generate insights from statistical analysis;
                if (statisticalAnalysis?.DistributionAnalysis != null)
                {
                    var statisticalInsights = GenerateStatisticalInsights(statisticalAnalysis);
                    insights.AddRange(statisticalInsights);
                }

                return insights.OrderByDescending(i => i.Importance).ToList();
            }, cancellationToken);
        }

        private Insight GenerateMetricInsight(MetricResult result, MetricsCalculationRequest request)
        {
            // Generate insight based on metric value and problem type;
            var insight = new Insight;
            {
                MetricName = result.MetricName,
                GeneratedAt = DateTime.UtcNow;
            };

            switch (result.MetricType)
            {
                case MetricType.Accuracy:
                    insight.Text = result.Value > 0.8;
                        ? "High accuracy achieved"
                        : "Accuracy needs improvement";
                    insight.Importance = result.Value > 0.8 ? ImportanceLevel.High : ImportanceLevel.Critical;
                    break;

                case MetricType.Precision:
                case MetricType.Recall:
                    insight.Text = result.Value > 0.7;
                        ? $"Good {result.MetricName} achieved"
                        : $"{result.MetricName} needs improvement";
                    insight.Importance = ImportanceLevel.Medium;
                    break;

                case MetricType.F1Score:
                    insight.Text = result.Value > 0.7;
                        ? "Good balance between precision and recall"
                        : "Imbalance between precision and recall detected";
                    insight.Importance = ImportanceLevel.High;
                    break;
            }

            return insight;
        }

        private List<Insight> GenerateComparativeInsights(ComparativeAnalysis analysis)
        {
            var insights = new List<Insight>();

            if (analysis.OverallComparison.OverallImprovement > 0)
            {
                insights.Add(new Insight;
                {
                    Text = $"Overall improvement of {analysis.OverallComparison.OverallImprovement:P2} over baseline",
                    Importance = ImportanceLevel.High,
                    GeneratedAt = DateTime.UtcNow;
                });
            }

            return insights;
        }

        private List<Insight> GenerateStatisticalInsights(StatisticalAnalysis analysis)
        {
            var insights = new List<Insight>();
            // Implement statistical insights generation;
            return insights;
        }

        private async Task<List<Recommendation>> GenerateRecommendationsAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            Dictionary<string, AggregatedMetric> aggregatedMetrics,
            StatisticalAnalysis statisticalAnalysis,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var recommendations = new List<Recommendation>();

                // Generate recommendations based on metric values;
                foreach (var kvp in metricResults)
                {
                    var recommendation = GenerateMetricRecommendation(kvp.Value, request);
                    if (recommendation != null)
                        recommendations.Add(recommendation);
                }

                // Generate overall recommendations;
                if (aggregatedMetrics.ContainsKey("overall_score"))
                {
                    var overallScore = aggregatedMetrics["overall_score"].Value;
                    if (overallScore < 0.7)
                    {
                        recommendations.Add(new Recommendation;
                        {
                            Type = RecommendationType.ModelImprovement,
                            Priority = RecommendationPriority.High,
                            Description = "Overall performance is below target. Consider model improvements.",
                            Action = "Review model architecture, hyperparameters, or training data",
                            GeneratedAt = DateTime.UtcNow;
                        });
                    }
                }

                return recommendations.OrderByDescending(r => r.Priority).ToList();
            }, cancellationToken);
        }

        private Recommendation GenerateMetricRecommendation(MetricResult result, MetricsCalculationRequest request)
        {
            if (result.Value >= 0.8)
                return null;

            var recommendation = new Recommendation;
            {
                MetricName = result.MetricName,
                GeneratedAt = DateTime.UtcNow;
            };

            switch (result.MetricType)
            {
                case MetricType.Accuracy when result.Value < 0.7:
                    recommendation.Type = RecommendationType.DataQuality;
                    recommendation.Priority = RecommendationPriority.High;
                    recommendation.Description = "Low accuracy detected";
                    recommendation.Action = "Check data quality, consider collecting more training data";
                    break;

                case MetricType.Precision when result.Value < 0.6:
                    recommendation.Type = RecommendationType.ThresholdAdjustment;
                    recommendation.Priority = RecommendationPriority.Medium;
                    recommendation.Description = "Low precision detected (many false positives)";
                    recommendation.Action = "Increase classification threshold to reduce false positives";
                    break;

                case MetricType.Recall when result.Value < 0.6:
                    recommendation.Type = RecommendationType.ThresholdAdjustment;
                    recommendation.Priority = RecommendationPriority.Medium;
                    recommendation.Description = "Low recall detected (many false negatives)";
                    recommendation.Action = "Decrease classification threshold to reduce false negatives";
                    break;
            }

            return recommendation;
        }

        private async Task<Dictionary<string, VisualizationData>> GenerateVisualizationsAsync(
            ConcurrentDictionary<string, MetricResult> metricResults,
            StatisticalAnalysis statisticalAnalysis,
            MetricsCalculationRequest request,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var visualizations = new Dictionary<string, VisualizationData>();

                // Generate metric distribution visualization;
                if (metricResults.Count > 1)
                {
                    visualizations["metric_distribution"] = new VisualizationData;
                    {
                        Type = VisualizationType.BarChart,
                        Title = "Metric Distribution",
                        Data = metricResults.ToDictionary(
                            kvp => kvp.Key,
                            kvp => kvp.Value.Value),
                        GeneratedAt = DateTime.UtcNow;
                    };
                }

                // Generate confidence interval visualization;
                if (metricResults.Values.Any(r => r.ConfidenceInterval != null))
                {
                    visualizations["confidence_intervals"] = new VisualizationData;
                    {
                        Type = VisualizationType.IntervalPlot,
                        Title = "Confidence Intervals",
                        Data = metricResults.ToDictionary(
                            kvp => kvp.Key,
                            kvp => new;
                            {
                                Value = kvp.Value.Value,
                                Lower = kvp.Value.ConfidenceInterval?.LowerBound,
                                Upper = kvp.Value.ConfidenceInterval?.UpperBound;
                            }),
                        GeneratedAt = DateTime.UtcNow;
                    };
                }

                return visualizations;
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Interface for the Metrics Calculator;
    /// </summary>
    public interface IMetricsCalculator : IDisposable
    {
        Task<MetricsCalculationResult> CalculateAsync(
            MetricsCalculationRequest request,
            CancellationToken cancellationToken = default);

        Task<CrossValidationResult> CrossValidateAsync(
            CrossValidationRequest request,
            CancellationToken cancellationToken = default);

        Task<StatisticalSignificanceResult> CalculateStatisticalSignificanceAsync(
            StatisticalSignificanceRequest request,
            CancellationToken cancellationToken = default);

        Task<ErrorAnalysisResult> AnalyzeErrorsAsync(
            ErrorAnalysisRequest request,
            CancellationToken cancellationToken = default);

        Task<CalibrationResult> CalculateCalibrationAsync(
            CalibrationRequest request,
            CancellationToken cancellationToken = default);

        Task<FairnessAnalysisResult> AnalyzeFairnessAsync(
            FairnessAnalysisRequest request,
            CancellationToken cancellationToken = default);

        MetricsCalculatorStatistics GetStatistics();
        void ClearSessions();
        MetricCalculationSession GetSession(string sessionId);
        Task<CalculationSessionExport> ExportSessionAsync(
            string sessionId,
            ExportOptions options,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Configuration for MetricsCalculator;
    /// </summary>
    public class MetricsCalculatorConfig;
    {
        public int MaxConcurrentCalculations { get; set; } = 10;
        public int SessionExpirationHours { get; set; } = 24;
        public int MaxMetricsPerCalculation { get; set; } = 50;
        public TimeSpan CalculationTimeout { get; set; } = TimeSpan.FromMinutes(2);
        public StatisticalSettings StatisticalSettings { get; set; } = new StatisticalSettings();
        public RegistrySettings RegistrySettings { get; set; } = new RegistrySettings();
        public ValidationSettings ValidationSettings { get; set; } = new ValidationSettings();
    }

    /// <summary>
    /// Metrics calculation request;
    /// </summary>
    public class MetricsCalculationRequest;
    {
        public string SessionId { get; set; }
        public ProblemType ProblemType { get; set; }
        public IEnumerable<double> GroundTruth { get; set; }
        public IEnumerable<double> Predictions { get; set; }
        public IEnumerable<double> ConfidenceScores { get; set; }
        public IEnumerable<double> SampleWeights { get; set; }
        public List<MetricType> MetricTypes { get; set; }
        public Dictionary<string, double> MetricParameters { get; set; } = new();
        public Dictionary<string, double> Thresholds { get; set; } = new();
        public Dictionary<string, double> ScoringWeights { get; set; } = new();
        public List<CompositeMetricDefinition> CompositeMetrics { get; set; } = new();
        public Dictionary<string, MetricResult> BaselineResults { get; set; }
        public MetricOptions MetricOptions { get; set; } = new MetricOptions();
        public double ConfidenceLevel { get; set; } = 0.95;
        public string AverageMethod { get; set; }
        public string MultiClassStrategy { get; set; }
        public int? PositiveLabel { get; set; }
        public int? TopK { get; set; }
        public bool CalculateOverallScore { get; set; } = true;
        public bool CalculateStatisticalAggregates { get; set; } = false;
        public bool PerformDistributionAnalysis { get; set; } = false;
        public bool PerformCorrelationAnalysis { get; set; } = false;
        public bool PerformTrendAnalysis { get; set; } = false;
        public bool PerformSignificanceTesting { get; set; } = false;
        public bool GenerateVisualizations { get; set; } = false;
        public SessionConfiguration SessionConfig { get; set; } = new SessionConfiguration();
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    /// <summary>
    /// Metrics calculation result;
    /// </summary>
    public class MetricsCalculationResult;
    {
        public string CalculationId { get; set; }
        public string SessionId { get; set; }
        public bool Success { get; set; }
        public Dictionary<string, MetricResult> Metrics { get; set; } = new();
        public Dictionary<string, AggregatedMetric> AggregatedMetrics { get; set; } = new();
        public StatisticalAnalysis StatisticalAnalysis { get; set; }
        public ComparativeAnalysis ComparativeAnalysis { get; set; }
        public Summary Summary { get; set; }
        public List<Insight> Insights { get; set; } = new();
        public List<Recommendation> Recommendations { get; set; } = new();
        public Dictionary<string, VisualizationData> Visualizations { get; set; } = new();
        public DateTime CalculationTime { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public MetricsError Error { get; set; }
        public MetricsMetadata Metadata { get; set; }
    }

    /// <summary>
    /// Problem types for metrics calculation;
    /// </summary>
    public enum ProblemType;
    {
        Classification,
        Regression,
        MultiLabelClassification,
        Ranking,
        Clustering,
        AnomalyDetection,
        ObjectDetection,
        Segmentation,
        Generation;
    }

    /// <summary>
    /// Metric types;
    /// </summary>
    public enum MetricType;
    {
        // Classification metrics;
        Accuracy,
        Precision,
        Recall,
        F1Score,
        ROCAUC,
        PRAUC,
        LogLoss,
        MacroF1,
        MicroF1,
        WeightedF1,
        MatthewsCorrelationCoefficient,

        // Regression metrics;
        MAE,
        MSE,
        RMSE,
        R2,
        ExplainedVariance,
        MaxError,

        // Multi-label metrics;
        HammingLoss,
        SubsetAccuracy,
        RankingLoss,

        // Ranking metrics;
        MAP,
        NDCG,
        PrecisionAtK,
        RecallAtK,
        MRR,

        // Clustering metrics;
        SilhouetteScore,
        DaviesBouldinIndex,
        CalinskiHarabaszIndex,

        // Statistical metrics;
        ConfidenceInterval,
        StandardError,
        PValue,

        // Custom metrics;
        Custom;
    }

    /// <summary>
    /// Metrics calculator statistics;
    /// </summary>
    public class MetricsCalculatorStatistics;
    {
        public long TotalMetricsCalculated { get; set; }
        public TimeSpan Uptime { get; set; }
        public int ActiveSessions { get; set; }
        public int ActiveCalculations { get; set; }
        public double MemoryUsage { get; set; } // MB;
        public double AverageMetricsPerCalculation { get; set; }
        public MetricRegistryStatistics MetricRegistryStats { get; set; }
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new();
    }

    /// <summary>
    /// Metrics error;
    /// </summary>
    public class MetricsError;
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> Context { get; set; } = new();
    }

    /// <summary>
    /// Metrics metadata;
    /// </summary>
    public class MetricsMetadata;
    {
        public ProblemType ProblemType { get; set; }
        public int MetricCount { get; set; }
        public int SampleSize { get; set; }
        public double ConfidenceLevel { get; set; }
        public MetricsValidationStatus ValidationStatus { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; } = new();
    }

    /// <summary>
    /// Exception for invalid data;
    /// </summary>
    public class InvalidDataException : Exception
    {
        public string DataDetails { get; }

        public InvalidDataException(string message, string dataDetails) : base(message)
        {
            DataDetails = dataDetails;
        }
    }

    /// <summary>
    /// Exception for metric calculation errors;
    /// </summary>
    public class MetricCalculationException : Exception
    {
        public MetricType MetricType { get; }

        public MetricCalculationException(string message, MetricType metricType, Exception innerException = null)
            : base(message, innerException)
        {
            MetricType = metricType;
        }
    }

    /// <summary>
    /// Exception for statistical analysis errors;
    /// </summary>
    public class StatisticalAnalysisException : Exception
    {
        public string AnalysisType { get; }

        public StatisticalAnalysisException(string message, string analysisType) : base(message)
        {
            AnalysisType = analysisType;
        }
    }

    // Additional supporting classes (simplified for brevity)
    public class MetricDefinition;
    {
        public string Name { get; set; }
        public MetricType Type { get; set; }
        public int Priority { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    public class MetricResult;
    {
        public string MetricId { get; set; }
        public string MetricName { get; set; }
        public MetricType MetricType { get; set; }
        public double Value { get; set; }
        public ConfidenceInterval ConfidenceInterval { get; set; }
        public double? StandardError { get; set; }
        public int SampleSize { get; set; }
        public TimeSpan CalculationTime { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string Interpretation { get; set; }
        public bool IsValid { get; set; }
        public List<string> Warnings { get; set; } = new();
        public DateTime CalculatedAt { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new();
    }

    public class AggregatedMetric;
    {
        public string Name { get; set; }
        public double Value { get; set; }
        public ConfidenceInterval ConfidenceInterval { get; set; }
        public List<string> Components { get; set; } = new();
        public string CalculationMethod { get; set; }
        public DateTime CalculatedAt { get; set; }
    }

    public class ConfidenceInterval;
    {
        public double LowerBound { get; set; }
        public double UpperBound { get; set; }
        public double ConfidenceLevel { get; set; }

        public ConfidenceInterval(double lower, double upper, double confidence = 0.95)
        {
            LowerBound = lower;
            UpperBound = upper;
            ConfidenceLevel = confidence;
        }
    }

    // Note: Due to the complexity and length of this file, many additional classes;
    // (StatisticalAnalyzer, MetricRegistry, ValidationEngine, etc.) would be;
    // implemented in separate files following the same pattern.

    // The remaining classes (enums, result types, request types, etc.)
    // would be fully implemented with all necessary properties and methods.
}
