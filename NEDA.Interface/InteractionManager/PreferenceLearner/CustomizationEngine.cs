using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.AI.MachineLearning;
using NEDA.Brain.PatternStorage;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Logging;
using NEDA.NeuralNetwork.AdaptiveLearning;
using NEDA.Services.UserService;

namespace NEDA.Interface.InteractionManager.PreferenceLearner;
{
    /// <summary>
    /// Represents a user preference with learning capabilities;
    /// </summary>
    public class UserPreference;
    {
        public string PreferenceId { get; set; }
        public string UserId { get; set; }
        public string Category { get; set; }
        public string Key { get; set; }
        public object Value { get; set; }
        public PreferenceType Type { get; set; }
        public double Confidence { get; set; } = 1.0;
        public int UsageCount { get; set; }
        public DateTime FirstObserved { get; set; }
        public DateTime LastUpdated { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
        public List<PreferenceHistory> History { get; set; } = new List<PreferenceHistory>();
        public Dictionary<string, double> Correlations { get; set; } = new Dictionary<string, double>();
        public bool IsExplicit { get; set; }
        public double DecayRate { get; set; } = 0.95;
        public DateTime? Expiration { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// History of preference changes;
    /// </summary>
    public class PreferenceHistory;
    {
        public DateTime Timestamp { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public string ChangeReason { get; set; }
        public string Source { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Type of preference;
    /// </summary>
    public enum PreferenceType;
    {
        Boolean,
        Integer,
        Double,
        String,
        List,
        Dictionary,
        Color,
        DateTime,
        TimeSpan,
        Enum,
        Complex;
    }

    /// <summary>
    /// Configuration for customization engine;
    /// </summary>
    public class CustomizationEngineOptions;
    {
        public int LearningWindowSize { get; set; } = 1000;
        public double ConfidenceThreshold { get; set; } = 0.7;
        public int MinimumObservations { get; set; } = 3;
        public TimeSpan PreferenceDecayInterval { get; set; } = TimeSpan.FromDays(30);
        public int MaxHistoryEntries { get; set; } = 100;
        public bool EnablePatternRecognition { get; set; } = true;
        public bool EnableAdaptiveLearning { get; set; } = true;
        public TimeSpan PatternAnalysisInterval { get; set; } = TimeSpan.FromHours(1);
        public int BatchProcessingSize { get; set; } = 100;
        public Dictionary<string, object> DefaultPreferences { get; set; } = new Dictionary<string, object>();
        public List<string> CriticalPreferences { get; set; } = new List<string>();
        public double AdaptationRate { get; set; } = 0.1;
        public bool EnableCrossUserLearning { get; set; } = false;
        public TimeSpan CacheTimeout { get; set; } = TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// Result of preference analysis;
    /// </summary>
    public class PreferenceAnalysisResult;
    {
        public string PreferenceId { get; set; }
        public double StabilityScore { get; set; }
        public double ConsistencyScore { get; set; }
        public double PredictabilityScore { get; set; }
        public List<string> DetectedPatterns { get; set; } = new List<string>();
        public Dictionary<string, double> InfluencingFactors { get; set; } = new Dictionary<string, double>();
        public DateTime LastPatternChange { get; set; }
        public bool IsStable { get; set; }
        public double RecommendationConfidence { get; set; }
        public object RecommendedValue { get; set; }
        public Dictionary<string, object> Insights { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Pattern detected in user behavior;
    /// </summary>
    public class BehavioralPattern;
    {
        public string PatternId { get; set; }
        public string UserId { get; set; }
        public string PatternType { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Conditions { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Actions { get; set; } = new Dictionary<string, object>();
        public double Confidence { get; set; }
        public int OccurrenceCount { get; set; }
        public DateTime FirstDetected { get; set; }
        public DateTime LastDetected { get; set; }
        public TimeSpan AverageInterval { get; set; }
        public Dictionary<string, double> Triggers { get; set; } = new Dictionary<string, double>();
        public List<string> RelatedPreferences { get; set; } = new List<string>();
        public bool IsActive { get; set; } = true;
        public DateTime? ExpirationDate { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Interface for customization engine operations;
    /// </summary>
    public interface ICustomizationEngine;
    {
        /// <summary>
        /// Learns a preference from user interaction;
        /// </summary>
        Task LearnPreferenceAsync(string userId, string category, string key, object value,
            Dictionary<string, object> context = null, bool isExplicit = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a learned preference with confidence;
        /// </summary>
        Task<(object Value, double Confidence)> GetPreferenceAsync(string userId, string category,
            string key, Dictionary<string, object> context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets all preferences for a user;
        /// </summary>
        Task<IEnumerable<UserPreference>> GetUserPreferencesAsync(string userId,
            string category = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Predicts user preference based on context;
        /// </summary>
        Task<object> PredictPreferenceAsync(string userId, string category, string key,
            Dictionary<string, object> context, CancellationToken cancellationToken = default);

        /// <summary>
        /// Analyzes preference patterns and stability;
        /// </summary>
        Task<PreferenceAnalysisResult> AnalyzePreferenceAsync(string userId, string preferenceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Detects behavioral patterns from user interactions;
        /// </summary>
        Task<IEnumerable<BehavioralPattern>> DetectBehavioralPatternsAsync(string userId,
            TimeSpan? period = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adapts system behavior based on learned preferences;
        /// </summary>
        Task<Dictionary<string, object>> AdaptBehaviorAsync(string userId,
            Dictionary<string, object> currentState, CancellationToken cancellationToken = default);

        /// <summary>
        /// Recommends optimizations based on user habits;
        /// </summary>
        Task<IEnumerable<OptimizationRecommendation>> GetRecommendationsAsync(string userId,
            int limit = 10, CancellationToken cancellationToken = default);

        /// <summary>
        /// Forgets or decays old preferences;
        /// </summary>
        Task<int> DecayOldPreferencesAsync(string userId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Exports learned preferences for backup or transfer;
        /// </summary>
        Task<byte[]> ExportPreferencesAsync(string userId, ExportFormat format,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Imports preferences from backup;
        /// </summary>
        Task<int> ImportPreferencesAsync(string userId, byte[] data, ImportMode mode,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets customization engine health and metrics;
        /// </summary>
        Task<CustomizationEngineHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optimization recommendation based on user behavior;
    /// </summary>
    public class OptimizationRecommendation;
    {
        public string RecommendationId { get; set; }
        public string UserId { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public RecommendationType Type { get; set; }
        public double ImpactScore { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> SuggestedChanges { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> ExpectedBenefits { get; set; } = new Dictionary<string, object>();
        public DateTime GeneratedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public bool IsApplied { get; set; }
        public DateTime? AppliedAt { get; set; }
        public Dictionary<string, object> ApplicationResults { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Type of recommendation;
    /// </summary>
    public enum RecommendationType;
    {
        Performance,
        Usability,
        Personalization,
        Security,
        Efficiency,
        Accessibility,
        Productivity,
        Comfort;
    }

    /// <summary>
    /// Format for exporting preferences;
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Xml,
        Binary,
        EncryptedJson;
    }

    /// <summary>
    /// Mode for importing preferences;
    /// </summary>
    public enum ImportMode;
    {
        Merge,
        Replace,
        UpdateOnly,
        Append;
    }

    /// <summary>
    /// Health status of customization engine;
    /// </summary>
    public class CustomizationEngineHealth;
    {
        public bool IsHealthy { get; set; }
        public string Status { get; set; }
        public int TotalUsers { get; set; }
        public int TotalPreferences { get; set; }
        public int ActivePatterns { get; set; }
        public double AverageConfidence { get; set; }
        public Dictionary<string, int> PreferenceDistribution { get; set; } = new Dictionary<string, int>();
        public DateTime? LastLearningCycle { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Context for preference learning;
    /// </summary>
    public class LearningContext;
    {
        public DateTime Timestamp { get; set; }
        public string SessionId { get; set; }
        public string DeviceId { get; set; }
        public string Location { get; set; }
        public string TimeOfDay { get; set; }
        public string DayOfWeek { get; set; }
        public string ApplicationState { get; set; }
        public Dictionary<string, object> EnvironmentalFactors { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> UserState { get; set; } = new Dictionary<string, object>();
        public List<string> RecentActions { get; set; } = new List<string>();
    }

    /// <summary>
    /// Implementation of adaptive customization engine with ML capabilities;
    /// </summary>
    public class CustomizationEngine : ICustomizationEngine, IDisposable;
    {
        private readonly ILogger<CustomizationEngine> _logger;
        private readonly IUserManager _userManager;
        private readonly IPatternManager _patternManager;
        private readonly ISelfLearner _selfLearner;
        private readonly IMLModel _mlModel;
        private readonly CustomizationEngineOptions _options;
        private readonly ConcurrentDictionary<string, UserPreference> _preferenceCache;
        private readonly ConcurrentDictionary<string, BehavioralPattern> _patternCache;
        private readonly SemaphoreSlim _learningLock = new SemaphoreSlim(1, 1);
        private readonly Timer _decayTimer;
        private readonly Timer _patternAnalysisTimer;
        private bool _disposed;

        private const string PreferenceTableName = "UserPreferences";
        private const string PatternTableName = "BehavioralPatterns";
        private const string HistoryTableName = "PreferenceHistory";
        private const int MaxBatchSize = 500;
        private const double MinimumConfidenceForAdaptation = 0.8;

        /// <summary>
        /// Initializes a new instance of CustomizationEngine;
        /// </summary>
        public CustomizationEngine(
            ILogger<CustomizationEngine> logger,
            IUserManager userManager,
            IPatternManager patternManager,
            ISelfLearner selfLearner,
            IMLModel mlModel,
            IOptions<CustomizationEngineOptions> options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _patternManager = patternManager ?? throw new ArgumentNullException(nameof(patternManager));
            _selfLearner = selfLearner ?? throw new ArgumentNullException(nameof(selfLearner));
            _mlModel = mlModel ?? throw new ArgumentNullException(nameof(mlModel));
            _options = options?.Value ?? new CustomizationEngineOptions();

            _preferenceCache = new ConcurrentDictionary<string, UserPreference>();
            _patternCache = new ConcurrentDictionary<string, BehavioralPattern>();

            // Initialize timers for background tasks;
            _decayTimer = new Timer(DecayPreferencesCallback, null,
                TimeSpan.FromHours(6), _options.PreferenceDecayInterval);

            _patternAnalysisTimer = new Timer(AnalyzePatternsCallback, null,
                TimeSpan.Zero, _options.PatternAnalysisInterval);

            _logger.LogInformation("CustomizationEngine initialized with {CacheTimeout} cache timeout",
                _options.CacheTimeout);
        }

        /// <inheritdoc/>
        public async Task LearnPreferenceAsync(string userId, string category, string key, object value,
            Dictionary<string, object> context = null, bool isExplicit = false,
            CancellationToken cancellationToken = default)
        {
            ValidateInputs(userId, category, key, value);

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var preferenceId = GeneratePreferenceId(userId, category, key);
                var existingPreference = await GetPreferenceFromStoreAsync(preferenceId, cancellationToken);
                var learningContext = BuildLearningContext(context);

                if (existingPreference != null)
                {
                    await UpdateExistingPreferenceAsync(existingPreference, value, learningContext,
                        isExplicit, cancellationToken);
                }
                else;
                {
                    await CreateNewPreferenceAsync(userId, category, key, value, learningContext,
                        isExplicit, cancellationToken);
                }

                // Update cache;
                var updatedPreference = await GetPreferenceFromStoreAsync(preferenceId, cancellationToken);
                if (updatedPreference != null)
                {
                    _preferenceCache[preferenceId] = updatedPreference;
                }

                // Trigger pattern analysis if enabled;
                if (_options.EnablePatternRecognition)
                {
                    _ = Task.Run(() => AnalyzePatternForPreferenceAsync(userId, preferenceId,
                        cancellationToken), cancellationToken);
                }

                _logger.LogDebug("Learned preference {PreferenceId} for user {UserId} with confidence {Confidence}",
                    preferenceId, userId, updatedPreference?.Confidence ?? 0);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogError(ex, "Failed to learn preference for user {UserId}, category {Category}, key {Key}",
                    userId, category, key);
                throw new CustomizationEngineException("Failed to learn preference", ex);
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<(object Value, double Confidence)> GetPreferenceAsync(string userId, string category,
            string key, Dictionary<string, object> context = null, CancellationToken cancellationToken = default)
        {
            ValidateInputs(userId, category, key);

            var preferenceId = GeneratePreferenceId(userId, category, key);

            // Try to get from cache first;
            if (_preferenceCache.TryGetValue(preferenceId, out var cachedPreference) &&
                cachedPreference.LastUpdated > DateTime.UtcNow.Add(-_options.CacheTimeout))
            {
                return (cachedPreference.Value, cachedPreference.Confidence);
            }

            // Get from store;
            var preference = await GetPreferenceFromStoreAsync(preferenceId, cancellationToken);
            if (preference != null)
            {
                // Apply context adaptation if context is provided;
                if (context != null && _options.EnableAdaptiveLearning)
                {
                    var adaptedValue = await AdaptPreferenceToContextAsync(preference, context, cancellationToken);
                    if (adaptedValue != null)
                    {
                        return (adaptedValue, preference.Confidence * 0.9); // Slightly lower confidence for adaptations;
                    }
                }

                // Update cache;
                _preferenceCache[preferenceId] = preference;
                return (preference.Value, preference.Confidence);
            }

            // Return default if not found;
            var defaultValue = GetDefaultPreference(category, key);
            return (defaultValue, 0.1); // Very low confidence for defaults;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<UserPreference>> GetUserPreferencesAsync(string userId,
            string category = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var query = $"SELECT * FROM {PreferenceTableName} WHERE UserId = @UserId";
                if (!string.IsNullOrEmpty(category))
                {
                    query += " AND Category = @Category";
                }
                query += " ORDER BY LastUpdated DESC";

                using var command = CreateCommand(query);
                command.Parameters.Add(CreateParameter("@UserId", userId));
                if (!string.IsNullOrEmpty(category))
                {
                    command.Parameters.Add(CreateParameter("@Category", category));
                }

                var preferences = new List<UserPreference>();
                using var reader = await ExecuteReaderAsync(command, cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var preference = MapToPreference(reader);
                    preferences.Add(preference);

                    // Update cache;
                    var preferenceId = GeneratePreferenceId(preference.UserId, preference.Category, preference.Key);
                    _preferenceCache[preferenceId] = preference;
                }

                return preferences;
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<object> PredictPreferenceAsync(string userId, string category, string key,
            Dictionary<string, object> context, CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            ValidateInputs(userId, category, key);

            try
            {
                // Prepare features for ML model;
                var features = PreparePredictionFeatures(userId, category, key, context);

                // Use ML model for prediction if available;
                if (_mlModel != null && _mlModel.IsTrained)
                {
                    var prediction = await _mlModel.PredictAsync(features, cancellationToken);
                    return ConvertPredictionToValue(prediction, category, key);
                }

                // Fallback to rule-based prediction;
                return await RuleBasedPredictionAsync(userId, category, key, context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to predict preference for user {UserId}, category {Category}, key {Key}",
                    userId, category, key);
                return GetDefaultPreference(category, key);
            }
        }

        /// <inheritdoc/>
        public async Task<PreferenceAnalysisResult> AnalyzePreferenceAsync(string userId, string preferenceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            if (string.IsNullOrWhiteSpace(preferenceId))
                throw new ArgumentException("Preference ID cannot be null or empty", nameof(preferenceId));

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var preference = await GetPreferenceFromStoreAsync(preferenceId, cancellationToken);
                if (preference == null)
                {
                    throw new CustomizationEngineException($"Preference {preferenceId} not found for user {userId}");
                }

                var analysis = new PreferenceAnalysisResult;
                {
                    PreferenceId = preferenceId,
                    StabilityScore = CalculateStabilityScore(preference),
                    ConsistencyScore = CalculateConsistencyScore(preference),
                    PredictabilityScore = CalculatePredictabilityScore(preference),
                    LastPatternChange = preference.LastUpdated,
                    IsStable = preference.Confidence >= _options.ConfidenceThreshold &&
                               preference.UsageCount >= _options.MinimumObservations;
                };

                // Analyze patterns if enabled;
                if (_options.EnablePatternRecognition)
                {
                    analysis.DetectedPatterns = await DetectPreferencePatternsAsync(preference, cancellationToken);
                    analysis.InfluencingFactors = await AnalyzeInfluencingFactorsAsync(preference, cancellationToken);
                }

                // Generate recommendation if confidence is high;
                if (analysis.IsStable && preference.Confidence >= MinimumConfidenceForAdaptation)
                {
                    analysis.RecommendedValue = await GenerateRecommendationAsync(preference, cancellationToken);
                    analysis.RecommendationConfidence = preference.Confidence * analysis.StabilityScore;
                }

                // Generate insights;
                analysis.Insights = GenerateInsights(preference, analysis);

                return analysis;
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<BehavioralPattern>> DetectBehavioralPatternsAsync(string userId,
            TimeSpan? period = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            var cacheKey = $"{userId}_{period?.TotalMinutes ?? 0}";
            if (_patternCache.TryGetValue(cacheKey, out var cachedPattern))
            {
                return new[] { cachedPattern };
            }

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var patterns = new List<BehavioralPattern>();

                // Get user preferences for analysis;
                var preferences = await GetUserPreferencesAsync(userId, null, cancellationToken);
                if (!preferences.Any())
                {
                    return Enumerable.Empty<BehavioralPattern>();
                }

                // Use pattern manager for detection;
                if (_patternManager != null)
                {
                    var patternData = preferences.Select(p => new;
                    {
                        p.Category,
                        p.Key,
                        p.Value,
                        p.LastUpdated,
                        p.Context;
                    }).ToList();

                    var detectedPatterns = await _patternManager.DetectPatternsAsync(patternData, cancellationToken);

                    foreach (var pattern in detectedPatterns)
                    {
                        var behavioralPattern = new BehavioralPattern;
                        {
                            PatternId = Guid.NewGuid().ToString(),
                            UserId = userId,
                            PatternType = pattern.PatternType,
                            Description = pattern.Description,
                            Conditions = pattern.Conditions,
                            Actions = pattern.Actions,
                            Confidence = pattern.Confidence,
                            OccurrenceCount = pattern.OccurrenceCount,
                            FirstDetected = DateTime.UtcNow.AddDays(-pattern.OccurrenceCount),
                            LastDetected = DateTime.UtcNow,
                            AverageInterval = pattern.AverageInterval,
                            Triggers = pattern.Triggers,
                            RelatedPreferences = preferences;
                                .Where(p => pattern.RelatedItems.Contains(p.PreferenceId))
                                .Select(p => p.PreferenceId)
                                .ToList(),
                            IsActive = true;
                        };

                        patterns.Add(behavioralPattern);

                        // Store pattern;
                        await StoreBehavioralPatternAsync(behavioralPattern, cancellationToken);
                    }
                }

                // Cache results;
                if (patterns.Any())
                {
                    var mainPattern = patterns.OrderByDescending(p => p.Confidence).First();
                    _patternCache[cacheKey] = mainPattern;
                }

                return patterns;
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, object>> AdaptBehaviorAsync(string userId,
            Dictionary<string, object> currentState, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            if (currentState == null)
                throw new ArgumentNullException(nameof(currentState));

            var adaptations = new Dictionary<string, object>();

            try
            {
                // Get user preferences with high confidence;
                var preferences = (await GetUserPreferencesAsync(userId, null, cancellationToken))
                    .Where(p => p.Confidence >= MinimumConfidenceForAdaptation)
                    .ToList();

                if (!preferences.Any())
                {
                    return adaptations;
                }

                // Apply adaptive learning if enabled;
                if (_options.EnableAdaptiveLearning && _selfLearner != null)
                {
                    var learningResult = await _selfLearner.AdaptAsync(currentState, preferences, cancellationToken);
                    adaptations = learningResult.Adaptations;
                }
                else;
                {
                    // Apply rule-based adaptations;
                    adaptations = ApplyRuleBasedAdaptations(preferences, currentState);
                }

                // Log adaptations;
                if (adaptations.Any())
                {
                    _logger.LogInformation("Applied {Count} adaptations for user {UserId}",
                        adaptations.Count, userId);

                    await LogAdaptationsAsync(userId, adaptations, cancellationToken);
                }

                return adaptations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to adapt behavior for user {UserId}", userId);
                return new Dictionary<string, object>();
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<OptimizationRecommendation>> GetRecommendationsAsync(string userId,
            int limit = 10, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            var recommendations = new List<OptimizationRecommendation>();

            try
            {
                // Analyze preferences for optimization opportunities;
                var preferences = await GetUserPreferencesAsync(userId, null, cancellationToken);
                var patterns = await DetectBehavioralPatternsAsync(userId, null, cancellationToken);

                // Generate performance recommendations;
                recommendations.AddRange(await GeneratePerformanceRecommendationsAsync(
                    userId, preferences, patterns, cancellationToken));

                // Generate usability recommendations;
                recommendations.AddRange(await GenerateUsabilityRecommendationsAsync(
                    userId, preferences, patterns, cancellationToken));

                // Generate personalization recommendations;
                recommendations.AddRange(await GeneratePersonalizationRecommendationsAsync(
                    userId, preferences, patterns, cancellationToken));

                // Sort by impact score and limit;
                return recommendations;
                    .OrderByDescending(r => r.ImpactScore * r.Confidence)
                    .Take(limit)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate recommendations for user {UserId}", userId);
                return Enumerable.Empty<OptimizationRecommendation>();
            }
        }

        /// <inheritdoc/>
        public async Task<int> DecayOldPreferencesAsync(string userId = null,
            CancellationToken cancellationToken = default)
        {
            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var decayedCount = 0;
                var cutoffDate = DateTime.UtcNow.Add(-_options.PreferenceDecayInterval);

                var query = $"SELECT * FROM {PreferenceTableName} WHERE LastUpdated < @CutoffDate";
                if (!string.IsNullOrEmpty(userId))
                {
                    query += " AND UserId = @UserId";
                }

                using var command = CreateCommand(query);
                command.Parameters.Add(CreateParameter("@CutoffDate", cutoffDate));
                if (!string.IsNullOrEmpty(userId))
                {
                    command.Parameters.Add(CreateParameter("@UserId", userId));
                }

                using var reader = await ExecuteReaderAsync(command, cancellationToken);
                var preferencesToDecay = new List<UserPreference>();

                while (await reader.ReadAsync(cancellationToken))
                {
                    preferencesToDecay.Add(MapToPreference(reader));
                }

                foreach (var preference in preferencesToDecay)
                {
                    // Apply decay to confidence;
                    preference.Confidence *= preference.DecayRate;
                    preference.LastUpdated = DateTime.UtcNow;

                    // If confidence is too low, mark for deletion;
                    if (preference.Confidence < 0.1)
                    {
                        await DeletePreferenceAsync(preference.PreferenceId, cancellationToken);
                    }
                    else;
                    {
                        await UpdatePreferenceInStoreAsync(preference, cancellationToken);
                    }

                    decayedCount++;

                    // Remove from cache;
                    _preferenceCache.TryRemove(preference.PreferenceId, out _);
                }

                _logger.LogInformation("Decayed {Count} old preferences", decayedCount);
                return decayedCount;
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<byte[]> ExportPreferencesAsync(string userId, ExportFormat format,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                var preferences = await GetUserPreferencesAsync(userId, null, cancellationToken);
                var exportData = new;
                {
                    UserId = userId,
                    ExportDate = DateTime.UtcNow,
                    PreferenceCount = preferences.Count(),
                    Preferences = preferences.Select(p => new;
                    {
                        p.PreferenceId,
                        p.Category,
                        p.Key,
                        p.Value,
                        p.Type,
                        p.Confidence,
                        p.UsageCount,
                        p.FirstObserved,
                        p.LastUpdated,
                        p.Context,
                        p.IsExplicit,
                        p.Tags;
                    })
                };

                switch (format)
                {
                    case ExportFormat.Json:
                        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                    case ExportFormat.Xml:
                        var xmlSerializer = new System.Xml.Serialization.XmlSerializer(exportData.GetType());
                        using (var stream = new System.IO.MemoryStream())
                        {
                            xmlSerializer.Serialize(stream, exportData);
                            return stream.ToArray();
                        }

                    case ExportFormat.EncryptedJson:
                        var jsonBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(exportData);
                        // In real implementation, encrypt the bytes;
                        return jsonBytes;

                    default:
                        throw new NotSupportedException($"Export format {format} is not supported");
                }
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<int> ImportPreferencesAsync(string userId, byte[] data, ImportMode mode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            if (data == null || data.Length == 0)
                throw new ArgumentException("Import data cannot be null or empty", nameof(data));

            await _learningLock.WaitAsync(cancellationToken);

            try
            {
                // Deserialize based on format detection;
                object importData;
                try
                {
                    importData = System.Text.Json.JsonSerializer.Deserialize<dynamic>(data);
                }
                catch
                {
                    throw new CustomizationEngineException("Failed to deserialize import data");
                }

                int importedCount = 0;
                // Implementation would parse and import based on mode;
                // Simplified for brevity;

                _logger.LogInformation("Imported {Count} preferences for user {UserId} in {Mode} mode",
                    importedCount, userId, mode);

                return importedCount;
            }
            finally
            {
                _learningLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<CustomizationEngineHealth> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            var health = new CustomizationEngineHealth;
            {
                IsHealthy = true,
                Status = "Healthy",
                Warnings = new List<string>(),
                Errors = new List<string>(),
                Metrics = new Dictionary<string, object>()
            };

            try
            {
                // Check database connection;
                // Check cache health;
                health.TotalPreferences = _preferenceCache.Count;
                health.ActivePatterns = _patternCache.Count;

                // Get user count;
                health.TotalUsers = await GetUserCountAsync(cancellationToken);

                // Calculate average confidence;
                if (_preferenceCache.Any())
                {
                    health.AverageConfidence = _preferenceCache.Values.Average(p => p.Confidence);
                }

                // Check for stale data;
                var staleCount = _preferenceCache.Count(p =>
                    p.Value.LastUpdated < DateTime.UtcNow.AddDays(-30));

                if (staleCount > 0)
                {
                    health.Warnings.Add($"{staleCount} preferences haven't been updated in 30 days");
                }

                // Update metrics;
                health.Metrics["CacheHitRatio"] = CalculateCacheHitRatio();
                health.Metrics["LearningRate"] = await CalculateLearningRateAsync(cancellationToken);
                health.LastLearningCycle = DateTime.UtcNow;

                return health;
            }
            catch (Exception ex)
            {
                health.IsHealthy = false;
                health.Status = "Unhealthy";
                health.Errors.Add($"Health check failed: {ex.Message}");
                return health;
            }
        }

        /// <summary>
        /// Disposes resources;
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _decayTimer?.Dispose();
                _patternAnalysisTimer?.Dispose();
                _learningLock?.Dispose();
                _disposed = true;
            }
        }

        #region Private Helper Methods;

        private void ValidateInputs(string userId, string category, string key, object value = null)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Category cannot be null or empty", nameof(category));
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));
            if (value == null && key != "null") // Allow null values only if explicitly allowed;
                throw new ArgumentNullException(nameof(value));
        }

        private string GeneratePreferenceId(string userId, string category, string key)
        {
            return $"{userId}_{category}_{key}".ToLowerInvariant();
        }

        private async Task<UserPreference> GetPreferenceFromStoreAsync(string preferenceId, CancellationToken cancellationToken)
        {
            using var command = CreateCommand($"SELECT * FROM {PreferenceTableName} WHERE PreferenceId = @PreferenceId");
            command.Parameters.Add(CreateParameter("@PreferenceId", preferenceId));

            using var reader = await ExecuteReaderAsync(command, cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return MapToPreference(reader);
            }

            return null;
        }

        private LearningContext BuildLearningContext(Dictionary<string, object> context)
        {
            var learningContext = new LearningContext;
            {
                Timestamp = DateTime.UtcNow,
                TimeOfDay = DateTime.UtcNow.ToString("HH:mm"),
                DayOfWeek = DateTime.UtcNow.DayOfWeek.ToString(),
                EnvironmentalFactors = context ?? new Dictionary<string, object>()
            };

            // Add additional context factors;
            // Implementation would add more context based on application state;

            return learningContext;
        }

        private async Task UpdateExistingPreferenceAsync(UserPreference preference, object newValue,
            LearningContext context, bool isExplicit, CancellationToken cancellationToken)
        {
            // Add to history;
            preference.History.Add(new PreferenceHistory;
            {
                Timestamp = DateTime.UtcNow,
                OldValue = preference.Value,
                NewValue = newValue,
                ChangeReason = isExplicit ? "ExplicitUserChange" : "LearnedFromBehavior",
                Source = "CustomizationEngine",
                Metadata = context.EnvironmentalFactors;
            });

            // Trim history if needed;
            if (preference.History.Count > _options.MaxHistoryEntries)
            {
                preference.History = preference.History;
                    .OrderByDescending(h => h.Timestamp)
                    .Take(_options.MaxHistoryEntries)
                    .ToList();
            }

            // Update preference;
            preference.Value = newValue;
            preference.LastUpdated = DateTime.UtcNow;
            preference.UsageCount++;

            // Adjust confidence;
            if (isExplicit)
            {
                preference.Confidence = Math.Min(1.0, preference.Confidence + 0.1);
                preference.IsExplicit = true;
            }
            else;
            {
                // Implicit learning has slower confidence growth;
                preference.Confidence = Math.Min(1.0, preference.Confidence + 0.05);
            }

            // Update context;
            foreach (var factor in context.EnvironmentalFactors)
            {
                preference.Context[factor.Key] = factor.Value;
            }

            await UpdatePreferenceInStoreAsync(preference, cancellationToken);
        }

        private async Task CreateNewPreferenceAsync(string userId, string category, string key,
            object value, LearningContext context, bool isExplicit, CancellationToken cancellationToken)
        {
            var preference = new UserPreference;
            {
                PreferenceId = GeneratePreferenceId(userId, category, key),
                UserId = userId,
                Category = category,
                Key = key,
                Value = value,
                Type = DeterminePreferenceType(value),
                Confidence = isExplicit ? 0.8 : 0.5,
                UsageCount = 1,
                FirstObserved = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Context = context.EnvironmentalFactors ?? new Dictionary<string, object>(),
                History = new List<PreferenceHistory>
                {
                    new PreferenceHistory;
                    {
                        Timestamp = DateTime.UtcNow,
                        NewValue = value,
                        ChangeReason = isExplicit ? "InitialExplicit" : "InitialObservation",
                        Source = "CustomizationEngine",
                        Metadata = context.EnvironmentalFactors;
                    }
                },
                IsExplicit = isExplicit,
                Tags = new List<string> { "initial" }
            };

            await StorePreferenceAsync(preference, cancellationToken);
        }

        private PreferenceType DeterminePreferenceType(object value)
        {
            return value switch;
            {
                bool _ => PreferenceType.Boolean,
                int _ => PreferenceType.Integer,
                double _ => PreferenceType.Double,
                string _ => PreferenceType.String,
                IList<object> _ => PreferenceType.List,
                IDictionary<string, object> _ => PreferenceType.Dictionary,
                DateTime _ => PreferenceType.DateTime,
                TimeSpan _ => PreferenceType.TimeSpan,
                Enum _ => PreferenceType.Enum,
                _ => PreferenceType.Complex;
            };
        }

        private async Task<object> AdaptPreferenceToContextAsync(UserPreference preference,
            Dictionary<string, object> context, CancellationToken cancellationToken)
        {
            if (!_options.EnableAdaptiveLearning)
                return null;

            // Check if context matches any known patterns;
            var patterns = await DetectBehavioralPatternsAsync(preference.UserId, null, cancellationToken);
            var applicablePatterns = patterns.Where(p =>
                p.RelatedPreferences.Contains(preference.PreferenceId) &&
                p.Conditions.All(c => context.ContainsKey(c.Key) &&
                    context[c.Key].ToString() == c.Value?.ToString()))
                .ToList();

            if (!applicablePatterns.Any())
                return null;

            // Use the pattern with highest confidence;
            var bestPattern = applicablePatterns.OrderByDescending(p => p.Confidence).First();

            // Apply adaptation based on pattern;
            if (bestPattern.Actions.TryGetValue("adaptation", out var adaptation))
            {
                return adaptation;
            }

            return null;
        }

        private object GetDefaultPreference(string category, string key)
        {
            var defaultKey = $"{category}.{key}";
            if (_options.DefaultPreferences.TryGetValue(defaultKey, out var value))
            {
                return value;
            }

            // Return type-appropriate default;
            return key.ToLower() switch;
            {
                var k when k.Contains("enabled") => true,
                var k when k.Contains("count") || k.Contains("limit") => 10,
                var k when k.Contains("threshold") => 0.5,
                var k when k.Contains("color") => "#0078D7",
                var k when k.Contains("theme") => "Dark",
                var k when k.Contains("language") => "en-US",
                _ => null;
            };
        }

        private double CalculateStabilityScore(UserPreference preference)
        {
            if (preference.History.Count < 2)
                return 0.5;

            var changes = preference.History;
                .OrderByDescending(h => h.Timestamp)
                .Take(10)
                .ToList();

            if (changes.Count < 2)
                return 0.5;

            // Calculate variance in change intervals;
            var intervals = new List<double>();
            for (int i = 0; i < changes.Count - 1; i++)
            {
                var interval = (changes[i].Timestamp - changes[i + 1].Timestamp).TotalDays;
                intervals.Add(interval);
            }

            if (intervals.Count < 2)
                return 0.5;

            var averageInterval = intervals.Average();
            var variance = intervals.Select(i => Math.Pow(i - averageInterval, 2)).Average();

            // Lower variance = higher stability;
            return Math.Max(0, 1 - (variance / (averageInterval * averageInterval)));
        }

        private double CalculateConsistencyScore(UserPreference preference)
        {
            if (preference.History.Count < 2)
                return 0.5;

            var recentValues = preference.History;
                .OrderByDescending(h => h.Timestamp)
                .Take(5)
                .Select(h => h.NewValue?.ToString())
                .ToList();

            if (recentValues.Count < 2)
                return 0.5;

            // Count how many times the value has changed;
            int changes = 0;
            for (int i = 0; i < recentValues.Count - 1; i++)
            {
                if (recentValues[i] != recentValues[i + 1])
                    changes++;
            }

            return 1 - (changes / (double)recentValues.Count);
        }

        private double CalculatePredictabilityScore(UserPreference preference)
        {
            // Analyze patterns in context changes;
            if (preference.History.Count < 3)
                return 0.3;

            // Simplified predictability calculation;
            // In real implementation, use pattern recognition;
            return Math.Min(1.0, preference.Confidence * 0.8 +
                (preference.UsageCount / 100.0));
        }

        private async Task<List<string>> DetectPreferencePatternsAsync(UserPreference preference,
            CancellationToken cancellationToken)
        {
            var patterns = new List<string>();

            // Detect time-based patterns;
            if (preference.History.Count >= 5)
            {
                var timePattern = DetectTimePattern(preference);
                if (!string.IsNullOrEmpty(timePattern))
                    patterns.Add(timePattern);
            }

            // Detect value transition patterns;
            var transitionPattern = DetectTransitionPattern(preference);
            if (!string.IsNullOrEmpty(transitionPattern))
                patterns.Add(transitionPattern);

            // Use ML for advanced pattern detection;
            if (_mlModel != null && _mlModel.IsTrained)
            {
                var mlPatterns = await DetectMLPatternsAsync(preference, cancellationToken);
                patterns.AddRange(mlPatterns);
            }

            return patterns.Distinct().ToList();
        }

        private async Task<Dictionary<string, double>> AnalyzeInfluencingFactorsAsync(UserPreference preference,
            CancellationToken cancellationToken)
        {
            var factors = new Dictionary<string, double>();

            // Analyze context factors;
            foreach (var contextEntry in preference.Context)
            {
                if (contextEntry.Value != null)
                {
                    // Calculate correlation with preference changes;
                    var correlation = CalculateContextCorrelation(preference, contextEntry.Key);
                    if (Math.Abs(correlation) > 0.3)
                    {
                        factors[contextEntry.Key] = correlation;
                    }
                }
            }

            // Analyze time factors;
            factors["TimeOfDay"] = CalculateTimeCorrelation(preference, "TimeOfDay");
            factors["DayOfWeek"] = CalculateTimeCorrelation(preference, "DayOfWeek");

            return factors;
        }

        private async Task<object> GenerateRecommendationAsync(UserPreference preference,
            CancellationToken cancellationToken)
        {
            // Analyze history for optimization opportunities;
            var recentValues = preference.History;
                .OrderByDescending(h => h.Timestamp)
                .Take(10)
                .Select(h => h.NewValue)
                .ToList();

            if (!recentValues.Any())
                return preference.Value;

            // For numeric values, recommend average or mode;
            if (preference.Type == PreferenceType.Integer || preference.Type == PreferenceType.Double)
            {
                try
                {
                    var numericValues = recentValues.Select(v => Convert.ToDouble(v)).ToList();
                    return numericValues.Average();
                }
                catch
                {
                    // If conversion fails, return most frequent value;
                    return recentValues;
                        .GroupBy(v => v)
                        .OrderByDescending(g => g.Count())
                        .First()
                        .Key;
                }
            }

            // For other types, return most frequent value;
            return recentValues;
                .GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .First()
                .Key;
        }

        private Dictionary<string, object> GenerateInsights(UserPreference preference,
            PreferenceAnalysisResult analysis)
        {
            var insights = new Dictionary<string, object>();

            insights["UsageFrequency"] = preference.UsageCount /
                (DateTime.UtcNow - preference.FirstObserved).TotalDays;

            insights["ConfidenceTrend"] = CalculateConfidenceTrend(preference);
            insights["ValueRange"] = CalculateValueRange(preference);
            insights["ContextSensitivity"] = analysis.InfluencingFactors.Count;

            if (analysis.DetectedPatterns.Any())
            {
                insights["PrimaryPattern"] = analysis.DetectedPatterns.First();
                insights["PatternCount"] = analysis.DetectedPatterns.Count;
            }

            return insights;
        }

        private async Task StorePreferenceAsync(UserPreference preference, CancellationToken cancellationToken)
        {
            using var command = CreateCommand($@"
                INSERT INTO {PreferenceTableName} (
                    PreferenceId, UserId, Category, Key, Value, Type, Confidence,
                    UsageCount, FirstObserved, LastUpdated, Context, History,
                    IsExplicit, DecayRate, Expiration, Tags;
                ) VALUES (
                    @PreferenceId, @UserId, @Category, @Key, @Value, @Type, @Confidence,
                    @UsageCount, @FirstObserved, @LastUpdated, @Context, @History,
                    @IsExplicit, @DecayRate, @Expiration, @Tags;
                )");

            AddPreferenceParameters(command, preference);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task UpdatePreferenceInStoreAsync(UserPreference preference, CancellationToken cancellationToken)
        {
            using var command = CreateCommand($@"
                UPDATE {PreferenceTableName} SET;
                    Value = @Value,
                    Confidence = @Confidence,
                    UsageCount = @UsageCount,
                    LastUpdated = @LastUpdated,
                    Context = @Context,
                    History = @History,
                    IsExplicit = @IsExplicit,
                    Tags = @Tags;
                WHERE PreferenceId = @PreferenceId");

            AddPreferenceParameters(command, preference);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task DeletePreferenceAsync(string preferenceId, CancellationToken cancellationToken)
        {
            using var command = CreateCommand($"DELETE FROM {PreferenceTableName} WHERE PreferenceId = @PreferenceId");
            command.Parameters.Add(CreateParameter("@PreferenceId", preferenceId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task StoreBehavioralPatternAsync(BehavioralPattern pattern, CancellationToken cancellationToken)
        {
            using var command = CreateCommand($@"
                INSERT INTO {PatternTableName} (
                    PatternId, UserId, PatternType, Description, Conditions, Actions,
                    Confidence, OccurrenceCount, FirstDetected, LastDetected,
                    AverageInterval, Triggers, RelatedPreferences, IsActive,
                    ExpirationDate, Metadata;
                ) VALUES (
                    @PatternId, @UserId, @PatternType, @Description, @Conditions, @Actions,
                    @Confidence, @OccurrenceCount, @FirstDetected, @LastDetected,
                    @AverageInterval, @Triggers, @RelatedPreferences, @IsActive,
                    @ExpirationDate, @Metadata;
                )");

            AddPatternParameters(command, pattern);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private void AddPreferenceParameters(System.Data.Common.DbCommand command, UserPreference preference)
        {
            command.Parameters.Add(CreateParameter("@PreferenceId", preference.PreferenceId));
            command.Parameters.Add(CreateParameter("@UserId", preference.UserId));
            command.Parameters.Add(CreateParameter("@Category", preference.Category));
            command.Parameters.Add(CreateParameter("@Key", preference.Key));
            command.Parameters.Add(CreateParameter("@Value", preference.Value));
            command.Parameters.Add(CreateParameter("@Type", (int)preference.Type));
            command.Parameters.Add(CreateParameter("@Confidence", preference.Confidence));
            command.Parameters.Add(CreateParameter("@UsageCount", preference.UsageCount));
            command.Parameters.Add(CreateParameter("@FirstObserved", preference.FirstObserved));
            command.Parameters.Add(CreateParameter("@LastUpdated", preference.LastUpdated));
            command.Parameters.Add(CreateParameter("@Context",
                System.Text.Json.JsonSerializer.Serialize(preference.Context)));
            command.Parameters.Add(CreateParameter("@History",
                System.Text.Json.JsonSerializer.Serialize(preference.History)));
            command.Parameters.Add(CreateParameter("@IsExplicit", preference.IsExplicit));
            command.Parameters.Add(CreateParameter("@DecayRate", preference.DecayRate));
            command.Parameters.Add(CreateParameter("@Expiration", preference.Expiration));
            command.Parameters.Add(CreateParameter("@Tags",
                preference.Tags != null ? string.Join(",", preference.Tags) : ""));
        }

        private void AddPatternParameters(System.Data.Common.DbCommand command, BehavioralPattern pattern)
        {
            command.Parameters.Add(CreateParameter("@PatternId", pattern.PatternId));
            command.Parameters.Add(CreateParameter("@UserId", pattern.UserId));
            command.Parameters.Add(CreateParameter("@PatternType", pattern.PatternType));
            command.Parameters.Add(CreateParameter("@Description", pattern.Description));
            command.Parameters.Add(CreateParameter("@Conditions",
                System.Text.Json.JsonSerializer.Serialize(pattern.Conditions)));
            command.Parameters.Add(CreateParameter("@Actions",
                System.Text.Json.JsonSerializer.Serialize(pattern.Actions)));
            command.Parameters.Add(CreateParameter("@Confidence", pattern.Confidence));
            command.Parameters.Add(CreateParameter("@OccurrenceCount", pattern.OccurrenceCount));
            command.Parameters.Add(CreateParameter("@FirstDetected", pattern.FirstDetected));
            command.Parameters.Add(CreateParameter("@LastDetected", pattern.LastDetected));
            command.Parameters.Add(CreateParameter("@AverageInterval", pattern.AverageInterval));
            command.Parameters.Add(CreateParameter("@Triggers",
                System.Text.Json.JsonSerializer.Serialize(pattern.Triggers)));
            command.Parameters.Add(CreateParameter("@RelatedPreferences",
                System.Text.Json.JsonSerializer.Serialize(pattern.RelatedPreferences)));
            command.Parameters.Add(CreateParameter("@IsActive", pattern.IsActive));
            command.Parameters.Add(CreateParameter("@ExpirationDate", pattern.ExpirationDate));
            command.Parameters.Add(CreateParameter("@Metadata",
                System.Text.Json.JsonSerializer.Serialize(pattern.Metadata)));
        }

        private UserPreference MapToPreference(System.Data.Common.DbDataReader reader)
        {
            return new UserPreference;
            {
                PreferenceId = reader["PreferenceId"].ToString(),
                UserId = reader["UserId"].ToString(),
                Category = reader["Category"].ToString(),
                Key = reader["Key"].ToString(),
                Value = reader["Value"],
                Type = (PreferenceType)Convert.ToInt32(reader["Type"]),
                Confidence = Convert.ToDouble(reader["Confidence"]),
                UsageCount = Convert.ToInt32(reader["UsageCount"]),
                FirstObserved = Convert.ToDateTime(reader["FirstObserved"]),
                LastUpdated = Convert.ToDateTime(reader["LastUpdated"]),
                Context = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                    reader["Context"].ToString()),
                History = System.Text.Json.JsonSerializer.Deserialize<List<PreferenceHistory>>(
                    reader["History"].ToString()),
                IsExplicit = Convert.ToBoolean(reader["IsExplicit"]),
                DecayRate = Convert.ToDouble(reader["DecayRate"]),
                Expiration = reader["Expiration"] as DateTime?,
                Tags = (reader["Tags"].ToString()?.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList()
            };
        }

        private System.Data.Common.DbCommand CreateCommand(string commandText)
        {
            // Implementation depends on database provider;
            // This is a placeholder - actual implementation would use specific DbCommand;
            throw new NotImplementedException("Database command creation not implemented");
        }

        private System.Data.Common.DbParameter CreateParameter(string name, object value)
        {
            // Implementation depends on database provider;
            throw new NotImplementedException("Parameter creation not implemented");
        }

        private async Task<System.Data.Common.DbDataReader> ExecuteReaderAsync(
            System.Data.Common.DbCommand command, CancellationToken cancellationToken)
        {
            // Implementation depends on database provider;
            throw new NotImplementedException("Command execution not implemented");
        }

        private async Task AnalyzePatternForPreferenceAsync(string userId, string preferenceId,
            CancellationToken cancellationToken)
        {
            // Background task for pattern analysis;
            await Task.Delay(1000, cancellationToken); // Simulated delay;
        }

        private Dictionary<string, object> PreparePredictionFeatures(string userId, string category,
            string key, Dictionary<string, object> context)
        {
            var features = new Dictionary<string, object>
            {
                ["user_id"] = userId,
                ["category"] = category,
                ["key"] = key,
                ["timestamp"] = DateTime.UtcNow.Ticks,
                ["time_of_day"] = DateTime.UtcNow.Hour,
                ["day_of_week"] = (int)DateTime.UtcNow.DayOfWeek;
            };

            foreach (var item in context)
            {
                features[$"ctx_{item.Key}"] = item.Value;
            }

            return features;
        }

        private object ConvertPredictionToValue(object prediction, string category, string key)
        {
            // Convert ML prediction to appropriate type;
            return prediction;
        }

        private async Task<object> RuleBasedPredictionAsync(string userId, string category, string key,
            Dictionary<string, object> context, CancellationToken cancellationToken)
        {
            // Simple rule-based prediction;
            var preference = await GetPreferenceFromStoreAsync(
                GeneratePreferenceId(userId, category, key), cancellationToken);

            if (preference != null && preference.Confidence > 0.7)
            {
                return preference.Value;
            }

            return GetDefaultPreference(category, key);
        }

        private string DetectTimePattern(UserPreference preference)
        {
            // Simplified time pattern detection;
            return null;
        }

        private string DetectTransitionPattern(UserPreference preference)
        {
            // Simplified transition pattern detection;
            return null;
        }

        private async Task<IEnumerable<string>> DetectMLPatternsAsync(UserPreference preference,
            CancellationToken cancellationToken)
        {
            // Use ML model for pattern detection;
            return Enumerable.Empty<string>();
        }

        private double CalculateContextCorrelation(UserPreference preference, string contextKey)
        {
            // Calculate correlation between context and value changes;
            return 0.5; // Placeholder;
        }

        private double CalculateTimeCorrelation(UserPreference preference, string timeFactor)
        {
            // Calculate correlation with time factors;
            return 0.3; // Placeholder;
        }

        private double CalculateConfidenceTrend(UserPreference preference)
        {
            if (preference.History.Count < 3)
                return 0;

            var recentConfidences = preference.History;
                .OrderByDescending(h => h.Timestamp)
                .Take(3)
                .Select((h, i) => new { Index = i, Value = 0.5 }) // Placeholder;
                .ToList();

            if (recentConfidences.Count < 2)
                return 0;

            // Simple trend calculation;
            return recentConfidences.Last().Value - recentConfidences.First().Value;
        }

        private Dictionary<string, object> CalculateValueRange(UserPreference preference)
        {
            var range = new Dictionary<string, object>();

            if (preference.History.Any())
            {
                var values = preference.History.Select(h => h.NewValue).ToList();
                range["min"] = values.Min();
                range["max"] = values.Max();
                range["unique_count"] = values.Distinct().Count();
            }

            return range;
        }

        private Dictionary<string, object> ApplyRuleBasedAdaptations(List<UserPreference> preferences,
            Dictionary<string, object> currentState)
        {
            var adaptations = new Dictionary<string, object>();

            foreach (var preference in preferences)
            {
                // Apply simple rule-based adaptations;
                if (preference.Key.Contains("theme") && currentState.ContainsKey("ambient_light"))
                {
                    var lightLevel = Convert.ToDouble(currentState["ambient_light"]);
                    if (lightLevel < 0.3 && preference.Value?.ToString() != "Dark")
                    {
                        adaptations["theme"] = "Dark";
                    }
                    else if (lightLevel > 0.7 && preference.Value?.ToString() != "Light")
                    {
                        adaptations["theme"] = "Light";
                    }
                }
            }

            return adaptations;
        }

        private async Task LogAdaptationsAsync(string userId, Dictionary<string, object> adaptations,
            CancellationToken cancellationToken)
        {
            // Log adaptations for analysis;
            foreach (var adaptation in adaptations)
            {
                await LearnPreferenceAsync(userId, "Adaptation", adaptation.Key,
                    adaptation.Value, null, false, cancellationToken);
            }
        }

        private async Task<IEnumerable<OptimizationRecommendation>> GeneratePerformanceRecommendationsAsync(
            string userId, IEnumerable<UserPreference> preferences, IEnumerable<BehavioralPattern> patterns,
            CancellationToken cancellationToken)
        {
            var recommendations = new List<OptimizationRecommendation>();

            // Analyze for performance optimization opportunities;
            var performancePreferences = preferences;
                .Where(p => p.Category == "Performance" || p.Tags.Contains("performance"))
                .ToList();

            foreach (var preference in performancePreferences)
            {
                if (preference.Confidence > 0.8 && preference.UsageCount > 10)
                {
                    recommendations.Add(new OptimizationRecommendation;
                    {
                        RecommendationId = Guid.NewGuid().ToString(),
                        UserId = userId,
                        Category = "Performance",
                        Title = $"Optimize {preference.Key}",
                        Description = $"Based on your usage patterns, adjusting {preference.Key} could improve performance by 15%",
                        Type = RecommendationType.Performance,
                        ImpactScore = 0.7,
                        Confidence = preference.Confidence,
                        SuggestedChanges = new Dictionary<string, object>
                        {
                            [preference.Key] = preference.Value;
                        },
                        ExpectedBenefits = new Dictionary<string, object>
                        {
                            ["performance_improvement"] = "15%",
                            ["energy_savings"] = "10%"
                        },
                        GeneratedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddDays(30),
                        Tags = new List<string> { "performance", "optimization" }
                    });
                }
            }

            return recommendations;
        }

        private async Task<IEnumerable<OptimizationRecommendation>> GenerateUsabilityRecommendationsAsync(
            string userId, IEnumerable<UserPreference> preferences, IEnumerable<BehavioralPattern> patterns,
            CancellationToken cancellationToken)
        {
            // Similar implementation for usability recommendations;
            return Enumerable.Empty<OptimizationRecommendation>();
        }

        private async Task<IEnumerable<OptimizationRecommendation>> GeneratePersonalizationRecommendationsAsync(
            string userId, IEnumerable<UserPreference> preferences, IEnumerable<BehavioralPattern> patterns,
            CancellationToken cancellationToken)
        {
            // Similar implementation for personalization recommendations;
            return Enumerable.Empty<OptimizationRecommendation>();
        }

        private async Task<int> GetUserCountAsync(CancellationToken cancellationToken)
        {
            using var command = CreateCommand($"SELECT COUNT(DISTINCT UserId) FROM {PreferenceTableName}");
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result);
        }

        private double CalculateCacheHitRatio()
        {
            // Simplified cache hit ratio calculation;
            return 0.85;
        }

        private async Task<double> CalculateLearningRateAsync(CancellationToken cancellationToken)
        {
            var today = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);

            using var command = CreateCommand($@"
                SELECT COUNT(*) FROM {PreferenceTableName} 
                WHERE LastUpdated >= @Yesterday AND LastUpdated < @Today");

            command.Parameters.Add(CreateParameter("@Yesterday", yesterday));
            command.Parameters.Add(CreateParameter("@Today", today));

            var recentChanges = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            var totalPreferences = _preferenceCache.Count;

            return totalPreferences > 0 ? (double)recentChanges / totalPreferences : 0;
        }

        private async void DecayPreferencesCallback(object state)
        {
            try
            {
                await DecayOldPreferencesAsync(null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in preference decay timer");
            }
        }

        private async void AnalyzePatternsCallback(object state)
        {
            try
            {
                // Analyze patterns for all active users;
                // Implementation would iterate through users and analyze patterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in pattern analysis timer");
            }
        }

        #endregion;
    }

    /// <summary>
    /// Custom exception for customization engine operations;
    /// </summary>
    public class CustomizationEngineException : Exception
    {
        public CustomizationEngineException() { }
        public CustomizationEngineException(string message) : base(message) { }
        public CustomizationEngineException(string message, Exception innerException) : base(message, innerException) { }
    }
}
