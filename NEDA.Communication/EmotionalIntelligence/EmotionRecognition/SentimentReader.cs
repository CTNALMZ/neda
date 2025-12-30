// NEDA.Communication/EmotionalIntelligence/EmotionRecognition/SentimentReader.cs;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Brain.NLP_Engine.SentimentAnalysis;
using NEDA.Common.Utilities;
using NEDA.Communication.EmotionalIntelligence.EmotionRecognition.Options;
using NEDA.Communication.EmotionalIntelligence.ToneAdjustment;
using NEDA.Monitoring.MetricsCollector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Communication.EmotionalIntelligence.EmotionRecognition;
{
    /// <summary>
    /// Sentiment analysis engine for reading and interpreting emotional sentiment from text input.
    /// Provides real-time sentiment scoring with contextual awareness and cultural adaptation.
    /// </summary>
    public interface ISentimentReader;
    {
        /// <summary>
        /// Analyzes sentiment from text with high precision and contextual understanding.
        /// </summary>
        /// <param name="text">Input text for sentiment analysis.</param>
        /// <param name="context">Optional context for better sentiment interpretation.</param>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        /// <returns>Comprehensive sentiment analysis result.</returns>
        Task<SentimentAnalysisResult> AnalyzeSentimentAsync(
            string text,
            SentimentContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Analyzes sentiment in batch mode for performance optimization.
        /// </summary>
        Task<IReadOnlyList<SentimentAnalysisResult>> AnalyzeBatchSentimentAsync(
            IReadOnlyList<string> texts,
            SentimentContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets real-time sentiment trend analysis for streaming text.
        /// </summary>
        IAsyncEnumerable<SentimentTrend> GetSentimentTrendAsync(
            IAsyncEnumerable<string> textStream,
            SentimentContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Calibrates the sentiment reader with custom training data.
        /// </summary>
        Task CalibrateAsync(
            IReadOnlyList<SentimentCalibrationData> calibrationData,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets detailed diagnostics and performance metrics.
        /// </summary>
        Task<SentimentReaderDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implementation of sentiment analysis with advanced NLP and ML capabilities.
    /// </summary>
    public class SentimentReader : ISentimentReader, IDisposable;
    {
        private readonly ILogger<SentimentReader> _logger;
        private readonly SentimentReaderOptions _options;
        private readonly ISentimentAnalyzer _sentimentAnalyzer;
        private readonly IEmotionDetector _emotionDetector;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IToneAnalyzer _toneAnalyzer;
        private readonly SemaphoreSlim _processingSemaphore;
        private readonly Dictionary<string, double> _sentimentCache;
        private readonly object _cacheLock = new object();
        private bool _disposed;
        private DateTime _lastCalibrationTime;
        private int _totalAnalyses;

        /// <summary>
        /// Initializes a new instance of the SentimentReader.
        /// </summary>
        public SentimentReader(
            ILogger<SentimentReader> logger,
            IOptions<SentimentReaderOptions> options,
            ISentimentAnalyzer sentimentAnalyzer,
            IEmotionDetector emotionDetector,
            IMetricsCollector metricsCollector,
            IToneAnalyzer toneAnalyzer = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _sentimentAnalyzer = sentimentAnalyzer ?? throw new ArgumentNullException(nameof(sentimentAnalyzer));
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _toneAnalyzer = toneAnalyzer;

            _processingSemaphore = new SemaphoreSlim(
                _options.MaxConcurrentAnalyses,
                _options.MaxConcurrentAnalyses);

            _sentimentCache = new Dictionary<string, double>(
                StringComparer.OrdinalIgnoreCase);

            _lastCalibrationTime = DateTime.UtcNow;

            _logger.LogInformation("SentimentReader initialized with {MaxConcurrentAnalyses} concurrent analyses",
                _options.MaxConcurrentAnalyses);
        }

        /// <inheritdoc/>
        public async Task<SentimentAnalysisResult> AnalyzeSentimentAsync(
            string text,
            SentimentContext context = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text cannot be null or empty", nameof(text));
            }

            var operationId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["OperationId"] = operationId,
                ["TextLength"] = text.Length,
                ["Context"] = context?.ContextType ?? "Default"
            }))
            {
                try
                {
                    _logger.LogDebug("Starting sentiment analysis for text of length {TextLength}", text.Length);

                    // Check cache first for performance optimization;
                    var cacheKey = GenerateCacheKey(text, context);
                    if (TryGetFromCache(cacheKey, out var cachedScore))
                    {
                        _logger.LogDebug("Cache hit for sentiment analysis");
                        await _metricsCollector.RecordMetricAsync(
                            "sentiment.cache.hits", 1, cancellationToken);

                        return new SentimentAnalysisResult;
                        {
                            SentimentScore = cachedScore,
                            PrimaryEmotion = await DetectPrimaryEmotionAsync(text, cancellationToken),
                            Confidence = 1.0, // Cache results have highest confidence;
                            IsFromCache = true,
                            AnalysisTimestamp = startTime,
                            Context = context;
                        };
                    }

                    await _processingSemaphore.WaitAsync(cancellationToken);

                    try
                    {
                        // Perform comprehensive sentiment analysis;
                        var analysisResult = await PerformComprehensiveAnalysisAsync(
                            text, context, cancellationToken);

                        // Update cache;
                        lock (_cacheLock)
                        {
                            if (_sentimentCache.Count >= _options.MaxCacheSize)
                            {
                                ClearOldestCacheEntries();
                            }
                            _sentimentCache[cacheKey] = analysisResult.SentimentScore;
                        }

                        // Record metrics;
                        await RecordAnalysisMetricsAsync(
                            text, analysisResult, startTime, cancellationToken);

                        Interlocked.Increment(ref _totalAnalyses);

                        _logger.LogInformation(
                            "Sentiment analysis completed. Score: {Score}, Confidence: {Confidence}",
                            analysisResult.SentimentScore,
                            analysisResult.Confidence);

                        return analysisResult;
                    }
                    finally
                    {
                        _processingSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Sentiment analysis was cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during sentiment analysis");
                    await _metricsCollector.RecordMetricAsync(
                        "sentiment.errors", 1, cancellationToken);

                    // Provide fallback analysis in case of errors;
                    return await GetFallbackAnalysisAsync(text, context, cancellationToken);
                }
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<SentimentAnalysisResult>> AnalyzeBatchSentimentAsync(
            IReadOnlyList<string> texts,
            SentimentContext context = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (texts == null || texts.Count == 0)
            {
                throw new ArgumentException("Texts cannot be null or empty", nameof(texts));
            }

            _logger.LogInformation("Starting batch sentiment analysis for {Count} texts", texts.Count);

            var results = new List<SentimentAnalysisResult>();
            var tasks = new List<Task<SentimentAnalysisResult>>();
            var batchSize = Math.Min(_options.BatchSize, texts.Count);

            // Process in parallel with controlled concurrency;
            for (int i = 0; i < texts.Count; i += batchSize)
            {
                var batch = texts.Skip(i).Take(batchSize).ToList();

                var batchTasks = batch.Select(text =>
                    AnalyzeSentimentAsync(text, context, cancellationToken));

                var batchResults = await Task.WhenAll(batchTasks);
                results.AddRange(batchResults);

                // Check cancellation between batches;
                cancellationToken.ThrowIfCancellationRequested();
            }

            await _metricsCollector.RecordMetricAsync(
                "sentiment.batch.completed", 1, cancellationToken);
            await _metricsCollector.RecordMetricAsync(
                "sentiment.batch.size", texts.Count, cancellationToken);

            return results.AsReadOnly();
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<SentimentTrend> GetSentimentTrendAsync(
            IAsyncEnumerable<string> textStream,
            SentimentContext context = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var trendWindow = new List<SentimentAnalysisResult>();
            var windowSize = _options.TrendWindowSize;

            await foreach (var text in textStream.WithCancellation(cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var analysis = await AnalyzeSentimentAsync(text, context, cancellationToken);
                trendWindow.Add(analysis);

                // Maintain window size;
                if (trendWindow.Count > windowSize)
                {
                    trendWindow.RemoveAt(0);
                }

                // Calculate trend once we have enough data;
                if (trendWindow.Count >= _options.MinTrendWindow)
                {
                    var trend = CalculateTrend(trendWindow);
                    yield return trend;
                }
            }
        }

        /// <inheritdoc/>
        public async Task CalibrateAsync(
            IReadOnlyList<SentimentCalibrationData> calibrationData,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (calibrationData == null || calibrationData.Count == 0)
            {
                throw new ArgumentException("Calibration data cannot be null or empty", nameof(calibrationData));
            }

            _logger.LogInformation("Starting calibration with {Count} data points", calibrationData.Count);

            try
            {
                // Clear cache before calibration;
                ClearCache();

                // Perform calibration;
                await _sentimentAnalyzer.CalibrateAsync(calibrationData, cancellationToken);

                if (_toneAnalyzer != null)
                {
                    await _toneAnalyzer.RecalibrateAsync(calibrationData, cancellationToken);
                }

                _lastCalibrationTime = DateTime.UtcNow;

                await _metricsCollector.RecordMetricAsync(
                    "sentiment.calibration.completed", 1, cancellationToken);
                await _metricsCollector.RecordMetricAsync(
                    "sentiment.calibration.samples", calibrationData.Count, cancellationToken);

                _logger.LogInformation("Calibration completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during calibration");
                await _metricsCollector.RecordMetricAsync(
                    "sentiment.calibration.errors", 1, cancellationToken);
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task<SentimentReaderDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            var diagnostics = new SentimentReaderDiagnostics;
            {
                TotalAnalyses = _totalAnalyses,
                CacheSize = _sentimentCache.Count,
                CacheHitRate = await CalculateCacheHitRateAsync(cancellationToken),
                LastCalibrationTime = _lastCalibrationTime,
                ConcurrentAnalyses = _options.MaxConcurrentAnalyses - _processingSemaphore.CurrentCount,
                IsOperational = !_disposed,
                Version = GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0",
                Uptime = DateTime.UtcNow - _lastCalibrationTime;
            };

            // Add performance metrics;
            diagnostics.PerformanceMetrics = await _metricsCollector.GetMetricsAsync(
                new[] { "sentiment.*" },
                cancellationToken);

            return diagnostics;
        }

        /// <summary>
        /// Performs comprehensive sentiment analysis using multiple analyzers.
        /// </summary>
        private async Task<SentimentAnalysisResult> PerformComprehensiveAnalysisAsync(
            string text,
            SentimentContext context,
            CancellationToken cancellationToken)
        {
            var analysisTasks = new List<Task<double>>
            {
                _sentimentAnalyzer.AnalyzeAsync(text, cancellationToken)
            };

            // Add tone analysis if available;
            if (_toneAnalyzer != null)
            {
                analysisTasks.Add(_toneAnalyzer.AnalyzeToneAsync(text, cancellationToken));
            }

            // Wait for all analyses to complete;
            var scores = await Task.WhenAll(analysisTasks);

            // Calculate weighted average;
            var weightedScore = CalculateWeightedScore(scores, context);

            // Detect emotions;
            var emotions = await _emotionDetector.DetectEmotionsAsync(text, cancellationToken);
            var primaryEmotion = emotions.OrderByDescending(e => e.Confidence).FirstOrDefault();

            // Calculate confidence based on analyzer agreement;
            var confidence = CalculateConfidence(scores, emotions);

            return new SentimentAnalysisResult;
            {
                SentimentScore = weightedScore,
                PrimaryEmotion = primaryEmotion?.EmotionType ?? EmotionType.Neutral,
                EmotionConfidence = primaryEmotion?.Confidence ?? 0,
                SecondaryEmotions = emotions;
                    .Where(e => e.EmotionType != primaryEmotion?.EmotionType)
                    .OrderByDescending(e => e.Confidence)
                    .Take(3)
                    .ToList(),
                Confidence = confidence,
                AnalysisTimestamp = DateTime.UtcNow,
                Context = context,
                Metadata = new Dictionary<string, object>
                {
                    ["TextLength"] = text.Length,
                    ["WordCount"] = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                    ["AnalysisDuration"] = DateTime.UtcNow - DateTime.UtcNow // Would be actual duration in real implementation;
                }
            };
        }

        /// <summary>
        /// Detects primary emotion from text.
        /// </summary>
        private async Task<EmotionType> DetectPrimaryEmotionAsync(string text, CancellationToken cancellationToken)
        {
            try
            {
                var emotions = await _emotionDetector.DetectEmotionsAsync(text, cancellationToken);
                return emotions.OrderByDescending(e => e.Confidence)
                    .FirstOrDefault()?.EmotionType ?? EmotionType.Neutral;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to detect emotion, falling back to neutral");
                return EmotionType.Neutral;
            }
        }

        /// <summary>
        /// Calculates weighted sentiment score from multiple analyzers.
        /// </summary>
        private double CalculateWeightedScore(double[] scores, SentimentContext context)
        {
            if (scores.Length == 1) return scores[0];

            // Apply context-specific weights;
            var weights = context?.AnalyzerWeights ?? _options.DefaultAnalyzerWeights;

            if (weights.Length != scores.Length)
            {
                weights = Enumerable.Repeat(1.0 / scores.Length, scores.Length).ToArray();
            }

            var weightedSum = 0.0;
            var totalWeight = 0.0;

            for (int i = 0; i < scores.Length; i++)
            {
                weightedSum += scores[i] * weights[i];
                totalWeight += weights[i];
            }

            return weightedSum / totalWeight;
        }

        /// <summary>
        /// Calculates confidence score based on analyzer agreement.
        /// </summary>
        private double CalculateConfidence(double[] scores, IReadOnlyList<EmotionDetection> emotions)
        {
            // Calculate variance between analyzers;
            var mean = scores.Average();
            var variance = scores.Sum(s => Math.Pow(s - mean, 2)) / scores.Length;
            var analyzerAgreement = Math.Max(0, 1 - variance);

            // Consider emotion detection confidence;
            var emotionConfidence = emotions.Any()
                ? emotions.Average(e => e.Confidence)
                : 0.5;

            // Weighted combination;
            return (analyzerAgreement * 0.6) + (emotionConfidence * 0.4);
        }

        /// <summary>
        /// Calculates sentiment trend from window of analyses.
        /// </summary>
        private SentimentTrend CalculateTrend(IReadOnlyList<SentimentAnalysisResult> window)
        {
            var scores = window.Select(r => r.SentimentScore).ToArray();
            var timestamps = window.Select(r => r.AnalysisTimestamp).ToArray();

            // Simple linear regression for trend calculation;
            var n = scores.Length;
            var sumX = 0.0;
            var sumY = 0.0;
            var sumXY = 0.0;
            var sumX2 = 0.0;

            for (int i = 0; i < n; i++)
            {
                var x = (timestamps[i] - timestamps[0]).TotalSeconds;
                sumX += x;
                sumY += scores[i];
                sumXY += x * scores[i];
                sumX2 += x * x;
            }

            var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);

            return new SentimentTrend;
            {
                CurrentSentiment = scores.Last(),
                TrendSlope = slope,
                TrendStrength = Math.Abs(slope),
                Direction = slope > 0 ? TrendDirection.Up : slope < 0 ? TrendDirection.Down : TrendDirection.Stable,
                WindowSize = n,
                StartTime = timestamps.First(),
                EndTime = timestamps.Last(),
                Volatility = CalculateVolatility(scores)
            };
        }

        /// <summary>
        /// Calculates volatility of sentiment scores.
        /// </summary>
        private double CalculateVolatility(double[] scores)
        {
            if (scores.Length < 2) return 0;

            var mean = scores.Average();
            var sumSquares = scores.Sum(s => Math.Pow(s - mean, 2));
            return Math.Sqrt(sumSquares / (scores.Length - 1));
        }

        /// <summary>
        /// Generates cache key for text and context.
        /// </summary>
        private string GenerateCacheKey(string text, SentimentContext context)
        {
            var normalizedText = text.Trim().ToLowerInvariant();
            var contextKey = context?.GetHashCode().ToString() ?? "default";
            return $"{normalizedText.GetHashCode()}|{contextKey}";
        }

        /// <summary>
        /// Attempts to get sentiment score from cache.
        /// </summary>
        private bool TryGetFromCache(string cacheKey, out double score)
        {
            lock (_cacheLock)
            {
                if (_sentimentCache.TryGetValue(cacheKey, out score))
                {
                    return true;
                }
            }

            score = 0;
            return false;
        }

        /// <summary>
        /// Clears oldest cache entries when cache is full.
        /// </summary>
        private void ClearOldestCacheEntries()
        {
            lock (_cacheLock)
            {
                var entriesToRemove = _sentimentCache.Count - _options.MaxCacheSize + _options.CacheCleanupSize;
                if (entriesToRemove > 0)
                {
                    var oldestKeys = _sentimentCache;
                        .OrderBy(kv => kv.Key)
                        .Take(entriesToRemove)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in oldestKeys)
                    {
                        _sentimentCache.Remove(key);
                    }

                    _logger.LogDebug("Cleared {Count} oldest cache entries", entriesToRemove);
                }
            }
        }

        /// <summary>
        /// Clears the entire cache.
        /// </summary>
        private void ClearCache()
        {
            lock (_cacheLock)
            {
                _sentimentCache.Clear();
                _logger.LogInformation("Cache cleared");
            }
        }

        /// <summary>
        /// Calculates cache hit rate from metrics.
        /// </summary>
        private async Task<double> CalculateCacheHitRateAsync(CancellationToken cancellationToken)
        {
            try
            {
                var hits = await _metricsCollector.GetMetricAsync("sentiment.cache.hits", cancellationToken);
                var misses = await _metricsCollector.GetMetricAsync("sentiment.cache.misses", cancellationToken);

                var total = hits + misses;
                return total > 0 ? hits / total : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate cache hit rate");
                return 0;
            }
        }

        /// <summary>
        /// Records analysis metrics for monitoring.
        /// </summary>
        private async Task RecordAnalysisMetricsAsync(
            string text,
            SentimentAnalysisResult result,
            DateTime startTime,
            CancellationToken cancellationToken)
        {
            var duration = DateTime.UtcNow - startTime;

            await _metricsCollector.RecordMetricAsync(
                "sentiment.analysis.duration", duration.TotalMilliseconds, cancellationToken);
            await _metricsCollector.RecordMetricAsync(
                "sentiment.score", result.SentimentScore, cancellationToken);
            await _metricsCollector.RecordMetricAsync(
                "sentiment.confidence", result.Confidence, cancellationToken);

            if (result.IsFromCache)
            {
                await _metricsCollector.RecordMetricAsync(
                    "sentiment.cache.hits", 1, cancellationToken);
            }
            else;
            {
                await _metricsCollector.RecordMetricAsync(
                    "sentiment.cache.misses", 1, cancellationToken);
            }
        }

        /// <summary>
        /// Provides fallback analysis when primary analysis fails.
        /// </summary>
        private async Task<SentimentAnalysisResult> GetFallbackAnalysisAsync(
            string text,
            SentimentContext context,
            CancellationToken cancellationToken)
        {
            _logger.LogWarning("Using fallback sentiment analysis");

            // Simple fallback based on keyword matching;
            var positiveWords = new[] { "good", "great", "excellent", "happy", "love" };
            var negativeWords = new[] { "bad", "terrible", "awful", "hate", "sad" };

            var lowerText = text.ToLowerInvariant();
            var positiveCount = positiveWords.Count(w => lowerText.Contains(w));
            var negativeCount = negativeWords.Count(w => lowerText.Contains(w));

            var score = positiveCount > negativeCount ? 0.7 :
                       negativeCount > positiveCount ? 0.3 : 0.5;

            return new SentimentAnalysisResult;
            {
                SentimentScore = score,
                PrimaryEmotion = EmotionType.Neutral,
                Confidence = 0.3, // Low confidence for fallback;
                IsFallback = true,
                AnalysisTimestamp = DateTime.UtcNow,
                Context = context;
            };
        }

        /// <summary>
        /// Validates that the instance is not disposed.
        /// </summary>
        private void ValidateNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SentimentReader));
            }
        }

        /// <summary>
        /// Disposes resources used by the SentimentReader.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _processingSemaphore?.Dispose();
                _disposed = true;
                _logger.LogInformation("SentimentReader disposed");
            }
        }
    }

    /// <summary>
    /// Context for sentiment analysis providing additional information for better interpretation.
    /// </summary>
    public class SentimentContext;
    {
        public string ContextType { get; set; } = "General";
        public string Culture { get; set; } = "en-US";
        public string Domain { get; set; } = "General";
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
        public double[] AnalyzerWeights { get; set; }
        public bool EnableSarcasmDetection { get; set; } = true;
        public bool ConsiderHistoricalContext { get; set; } = false;
        public TimeSpan? HistoricalWindow { get; set; }
    }

    /// <summary>
    /// Result of sentiment analysis.
    /// </summary>
    public class SentimentAnalysisResult;
    {
        public double SentimentScore { get; set; } // -1.0 to 1.0;
        public EmotionType PrimaryEmotion { get; set; }
        public double EmotionConfidence { get; set; }
        public IReadOnlyList<EmotionDetection> SecondaryEmotions { get; set; } = Array.Empty<EmotionDetection>();
        public double Confidence { get; set; } // 0.0 to 1.0;
        public bool IsFromCache { get; set; }
        public bool IsFallback { get; set; }
        public DateTime AnalysisTimestamp { get; set; }
        public SentimentContext Context { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Sentiment trend analysis for streaming data.
    /// </summary>
    public class SentimentTrend;
    {
        public double CurrentSentiment { get; set; }
        public double TrendSlope { get; set; }
        public double TrendStrength { get; set; }
        public TrendDirection Direction { get; set; }
        public double Volatility { get; set; }
        public int WindowSize { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    /// <summary>
    /// Direction of sentiment trend.
    /// </summary>
    public enum TrendDirection;
    {
        Up,
        Down,
        Stable;
    }

    /// <summary>
    /// Calibration data for sentiment reader.
    /// </summary>
    public class SentimentCalibrationData;
    {
        public string Text { get; set; }
        public double ExpectedSentiment { get; set; }
        public EmotionType ExpectedEmotion { get; set; }
        public string Context { get; set; }
        public double Weight { get; set; } = 1.0;
    }

    /// <summary>
    /// Diagnostics information for sentiment reader.
    /// </summary>
    public class SentimentReaderDiagnostics;
    {
        public int TotalAnalyses { get; set; }
        public int CacheSize { get; set; }
        public double CacheHitRate { get; set; }
        public DateTime LastCalibrationTime { get; set; }
        public int ConcurrentAnalyses { get; set; }
        public bool IsOperational { get; set; }
        public string Version { get; set; }
        public TimeSpan Uptime { get; set; }
        public Dictionary<string, double> PerformanceMetrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Configuration options for SentimentReader.
    /// </summary>
    public class SentimentReaderOptions;
    {
        public int MaxConcurrentAnalyses { get; set; } = 10;
        public int MaxCacheSize { get; set; } = 1000;
        public int CacheCleanupSize { get; set; } = 100;
        public int BatchSize { get; set; } = 50;
        public int TrendWindowSize { get; set; } = 100;
        public int MinTrendWindow { get; set; } = 5;
        public double[] DefaultAnalyzerWeights { get; set; } = new[] { 1.0 };
        public TimeSpan CacheTTL { get; set; } = TimeSpan.FromMinutes(30);
    }
}
