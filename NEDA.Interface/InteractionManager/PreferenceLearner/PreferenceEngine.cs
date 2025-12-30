// NEDA.Interface/InteractionManager/PreferenceLearner/PreferenceEngine.cs;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;
using NEDA.Logging;
using NEDA.ExceptionHandling;
using NEDA.Configuration.AppSettings;
using NEDA.NeuralNetwork.PatternRecognition;

namespace NEDA.Interface.InteractionManager.PreferenceLearner;
{
    /// <summary>
    /// Intelligent preference learning engine that analyzes user behavior patterns,
    /// extracts preferences, and provides personalized adaptations;
    /// </summary>
    public interface IPreferenceEngine;
    {
        /// <summary>
        /// Analyzes user behavior and extracts preferences;
        /// </summary>
        Task<UserPreferences> AnalyzePreferencesAsync(string userId, PreferenceAnalysisOptions options = null);

        /// <summary>
        /// Gets user preferences with automatic updates if stale;
        /// </summary>
        Task<UserPreferences> GetPreferencesAsync(string userId, bool forceRefresh = false);

        /// <summary>
        /// Updates user preferences based on new interaction;
        /// </summary>
        Task UpdatePreferencesAsync(string userId, UserInteraction interaction);

        /// <summary>
        /// Predicts user preference for a specific context;
        /// </summary>
        Task<PreferencePrediction> PredictPreferenceAsync(string userId, PreferenceContext context);

        /// <summary>
        /// Gets personalized recommendations based on preferences;
        /// </summary>
        Task<IEnumerable<PersonalizationRecommendation>> GetPersonalizedRecommendationsAsync(string userId, RecommendationContext context);

        /// <summary>
        /// Learns from explicit user feedback on preferences;
        /// </summary>
        Task LearnFromFeedbackAsync(string userId, PreferenceFeedback feedback);

        /// <summary>
        /// Merges multiple preference sets (e.g., from different devices)
        /// </summary>
        Task<UserPreferences> MergePreferencesAsync(string userId, IEnumerable<UserPreferences> preferenceSets);

        /// <summary>
        /// Detects changes in user preferences over time;
        /// </summary>
        Task<PreferenceChangeDetection> DetectPreferenceChangesAsync(string userId, TimeSpan analysisPeriod);

        /// <summary>
        /// Gets preference patterns across user segments;
        /// </summary>
        Task<SegmentPreferencePatterns> GetSegmentPatternsAsync(UserSegment segment);

        /// <summary>
        /// Optimizes preference model for specific user;
        /// </summary>
        Task OptimizeForUserAsync(string userId, OptimizationCriteria criteria);

        /// <summary>
        /// Exports user preferences in specified format;
        /// </summary>
        Task<PreferenceExport> ExportPreferencesAsync(string userId, ExportFormat format);

        /// <summary>
        /// Imports user preferences from external source;
        /// </summary>
        Task ImportPreferencesAsync(string userId, PreferenceImport importData);

        /// <summary>
        /// Resets preferences for user with optional preservation;
        /// </summary>
        Task ResetPreferencesAsync(string userId, ResetOptions options);

        /// <summary>
        /// Gets preference learning statistics;
        /// </summary>
        Task<PreferenceLearningStats> GetStatisticsAsync(string userId = null);
    }

    /// <summary>
    /// Advanced preference learning engine with neural network adaptation,
    /// pattern recognition, and continuous optimization;
    /// </summary>
    public class PreferenceEngine : IPreferenceEngine;
    {
        private readonly ILogger<PreferenceEngine> _logger;
        private readonly IPreferenceStore _preferenceStore;
        private readonly IBehaviorAnalyzer _behaviorAnalyzer;
        private readonly IPatternRecognizer _patternRecognizer;
        private readonly INeuralNetworkEngine _neuralEngine;
        private readonly IMemoryCache _memoryCache;
        private readonly ISettingsManager _settingsManager;
        private readonly IErrorReporter _errorReporter;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private readonly ConcurrentDictionary<string, PreferenceLearningSession> _learningSessions;
        private readonly MemoryCacheEntryOptions _cacheOptions;
        private readonly TimeSpan _defaultCacheDuration = TimeSpan.FromMinutes(30);

        private const double PREFERENCE_CONFIDENCE_THRESHOLD = 0.7;
        private const int MIN_INTERACTIONS_FOR_ANALYSIS = 10;
        private const double ADAPTATION_LEARNING_RATE = 0.1;

        /// <summary>
        /// Initializes a new instance of PreferenceEngine;
        /// </summary>
        public PreferenceEngine(
            ILogger<PreferenceEngine> logger,
            IPreferenceStore preferenceStore,
            IBehaviorAnalyzer behaviorAnalyzer,
            IPatternRecognizer patternRecognizer,
            INeuralNetworkEngine neuralEngine,
            IMemoryCache memoryCache,
            ISettingsManager settingsManager,
            IErrorReporter errorReporter)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _preferenceStore = preferenceStore ?? throw new ArgumentNullException(nameof(preferenceStore));
            _behaviorAnalyzer = behaviorAnalyzer ?? throw new ArgumentNullException(nameof(behaviorAnalyzer));
            _patternRecognizer = patternRecognizer ?? throw new ArgumentNullException(nameof(patternRecognizer));
            _neuralEngine = neuralEngine ?? throw new ArgumentNullException(nameof(neuralEngine));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));

            _learningSessions = new ConcurrentDictionary<string, PreferenceLearningSession>();
            _cacheOptions = new MemoryCacheEntryOptions;
            {
                SlidingExpiration = _defaultCacheDuration,
                Size = 1;
            };

            _logger.LogInformation("PreferenceEngine initialized with neural adaptation capabilities");
        }

        /// <inheritdoc/>
        public async Task<UserPreferences> AnalyzePreferencesAsync(string userId, PreferenceAnalysisOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            options ??= new PreferenceAnalysisOptions();

            try
            {
                await _semaphore.WaitAsync();

                _logger.LogDebug("Analyzing preferences for user: {UserId} with options: {@Options}", userId, options);

                // Get user interactions for analysis;
                var interactions = await _preferenceStore.GetUserInteractionsAsync(
                    userId,
                    DateTime.UtcNow - options.AnalysisPeriod,
                    options.MaxInteractions);

                if (interactions.Count() < MIN_INTERACTIONS_FOR_ANALYSIS && !options.ForceAnalysis)
                {
                    _logger.LogWarning("Insufficient interactions ({Count}) for preference analysis for user: {UserId}",
                        interactions.Count(), userId);
                    return await CreateDefaultPreferencesAsync(userId);
                }

                // Analyze behavior patterns;
                var behaviorPatterns = await _behaviorAnalyzer.AnalyzePatternsAsync(interactions, options.PatternDepth);

                // Extract explicit preferences from interactions;
                var explicitPreferences = await ExtractExplicitPreferencesAsync(interactions);

                // Infer implicit preferences from behavior;
                var implicitPreferences = await InferImplicitPreferencesAsync(behaviorPatterns, interactions);

                // Merge and prioritize preferences;
                var mergedPreferences = MergeAndPrioritizePreferences(explicitPreferences, implicitPreferences);

                // Apply neural network for preference prediction refinement;
                var neuralRefined = await RefineWithNeuralNetworkAsync(userId, mergedPreferences, interactions);

                // Calculate confidence scores;
                var preferences = CalculatePreferenceConfidence(neuralRefined, interactions.Count());

                // Store analysis results;
                await StorePreferenceAnalysisAsync(userId, preferences, behaviorPatterns);

                // Cache the preferences;
                var cacheKey = GetPreferencesCacheKey(userId);
                _memoryCache.Set(cacheKey, preferences, _cacheOptions);

                _logger.LogInformation("Analyzed {PreferenceCount} preferences for user {UserId} with average confidence {Confidence:F2}",
                    preferences.Categories.Sum(c => c.Preferences.Count), userId,
                    preferences.Categories.Average(c => c.Confidence));

                return preferences;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing preferences for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.AnalyzePreferencesAsync", ex,
                    new { UserId = userId, Options = options });
                throw new PreferenceAnalysisException("Failed to analyze user preferences", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UserPreferences> GetPreferencesAsync(string userId, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                var cacheKey = GetPreferencesCacheKey(userId);

                if (!forceRefresh && _memoryCache.TryGetValue(cacheKey, out UserPreferences cachedPreferences))
                {
                    _logger.LogDebug("Retrieved preferences from cache for user: {UserId}", userId);
                    return cachedPreferences;
                }

                // Check if preferences exist in store;
                var storedPreferences = await _preferenceStore.GetPreferencesAsync(userId);
                if (storedPreferences != null && !ShouldRefreshPreferences(storedPreferences))
                {
                    _memoryCache.Set(cacheKey, storedPreferences, _cacheOptions);
                    return storedPreferences;
                }

                // Analyze preferences if needed;
                var analyzedPreferences = await AnalyzePreferencesAsync(userId, new PreferenceAnalysisOptions;
                {
                    ForceAnalysis = forceRefresh,
                    AnalysisPeriod = TimeSpan.FromDays(30)
                });

                return analyzedPreferences;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting preferences for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.GetPreferencesAsync", ex,
                    new { UserId = userId, ForceRefresh = forceRefresh });
                throw;
            }
        }

        /// <inheritdoc/>
        public async Task UpdatePreferencesAsync(string userId, UserInteraction interaction)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (interaction == null)
                throw new ArgumentNullException(nameof(interaction));

            try
            {
                await _semaphore.WaitAsync();

                _logger.LogDebug("Updating preferences for user {UserId} based on interaction: {@Interaction}",
                    userId, interaction);

                // Store the interaction;
                await _preferenceStore.StoreInteractionAsync(userId, interaction);

                // Get or create learning session;
                var session = _learningSessions.GetOrAdd(userId,
                    id => new PreferenceLearningSession { UserId = id });

                // Update session with new interaction;
                session.RecentInteractions.Add(interaction);
                session.TotalInteractions++;

                // Maintain sliding window of recent interactions;
                if (session.RecentInteractions.Count > 100)
                {
                    session.RecentInteractions = session.RecentInteractions;
                        .Skip(session.RecentInteractions.Count - 50)
                        .ToList();
                }

                // Extract immediate preference signals;
                var immediateSignals = ExtractPreferenceSignals(interaction);

                // Update preference model incrementally;
                await UpdatePreferenceModelIncrementallyAsync(userId, immediateSignals, session);

                // Check if batch analysis is needed;
                if (ShouldPerformBatchAnalysis(session))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AnalyzePreferencesAsync(userId, new PreferenceAnalysisOptions;
                            {
                                AnalysisPeriod = TimeSpan.FromDays(7),
                                MaxInteractions = 500;
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Background preference analysis failed for user: {UserId}", userId);
                        }
                    });
                }

                // Clear cache to force refresh on next read;
                var cacheKey = GetPreferencesCacheKey(userId);
                _memoryCache.Remove(cacheKey);

                _logger.LogInformation("Updated preferences for user {UserId} (Total interactions: {Total})",
                    userId, session.TotalInteractions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating preferences for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.UpdatePreferencesAsync", ex,
                    new { UserId = userId, Interaction = interaction });
                throw new PreferenceUpdateException("Failed to update user preferences", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<PreferencePrediction> PredictPreferenceAsync(string userId, PreferenceContext context)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                _logger.LogDebug("Predicting preference for user {UserId} in context: {@Context}", userId, context);

                // Get current preferences;
                var preferences = await GetPreferencesAsync(userId);

                // Get similar contexts from history;
                var similarContexts = await _preferenceStore.FindSimilarContextsAsync(userId, context, 5);

                // Use neural network for prediction;
                var neuralPrediction = await _neuralEngine.PredictPreferenceAsync(userId, context, preferences);

                // Combine predictions from different methods;
                var combinedPrediction = CombinePreferencePredictions(
                    neuralPrediction,
                    preferences,
                    similarContexts,
                    context);

                // Calculate prediction confidence;
                combinedPrediction.Confidence = CalculatePredictionConfidence(
                    combinedPrediction,
                    similarContexts.Count(),
                    preferences.OverallConfidence);

                // Store prediction for learning;
                await _preferenceStore.StorePredictionAsync(userId, context, combinedPrediction);

                _logger.LogDebug("Predicted preference for user {UserId}: {Prediction} with confidence {Confidence:F2}",
                    userId, combinedPrediction.PreferredOption, combinedPrediction.Confidence);

                return combinedPrediction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error predicting preference for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.PredictPreferenceAsync", ex,
                    new { UserId = userId, Context = context });
                throw new PreferencePredictionException("Failed to predict user preference", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<PersonalizationRecommendation>> GetPersonalizedRecommendationsAsync(
            string userId, RecommendationContext context)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                _logger.LogDebug("Getting personalized recommendations for user {UserId} in context: {@Context}",
                    userId, context);

                // Get user preferences;
                var preferences = await GetPreferencesAsync(userId);

                // Get recommendation candidates;
                var candidates = await _preferenceStore.GetRecommendationCandidatesAsync(context);

                // Score candidates based on preferences;
                var scoredCandidates = await ScoreRecommendationCandidatesAsync(candidates, preferences, context);

                // Apply diversity filter to avoid repetitive recommendations;
                var diverseCandidates = ApplyDiversityFilter(scoredCandidates, context.DiversityRequirement);

                // Generate personalized explanations;
                var recommendations = await GeneratePersonalizedRecommendationsAsync(diverseCandidates, preferences, context);

                // Store recommendations for feedback tracking;
                await _preferenceStore.StoreRecommendationsAsync(userId, recommendations);

                _logger.LogInformation("Generated {Count} personalized recommendations for user {UserId}",
                    recommendations.Count(), userId);

                return recommendations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting personalized recommendations for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.GetPersonalizedRecommendationsAsync", ex,
                    new { UserId = userId, Context = context });
                throw new RecommendationException("Failed to generate personalized recommendations", ex);
            }
        }

        /// <inheritdoc/>
        public async Task LearnFromFeedbackAsync(string userId, PreferenceFeedback feedback)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (feedback == null)
                throw new ArgumentNullException(nameof(feedback));

            try
            {
                await _semaphore.WaitAsync();

                _logger.LogDebug("Learning from feedback for user {UserId}: {@Feedback}", userId, feedback);

                // Validate feedback;
                if (!IsValidFeedback(feedback))
                {
                    _logger.LogWarning("Invalid feedback received from user {UserId}", userId);
                    return;
                }

                // Get the original prediction/context;
                var originalContext = await _preferenceStore.GetPredictionContextAsync(feedback.PredictionId);
                if (originalContext == null)
                {
                    _logger.LogWarning("Original prediction context not found for feedback: {PredictionId}", feedback.PredictionId);
                    return;
                }

                // Calculate learning signal strength;
                var learningSignal = CalculateLearningSignal(feedback);

                // Update preference models based on feedback;
                await UpdateModelsFromFeedbackAsync(userId, feedback, originalContext, learningSignal);

                // Adjust confidence weights;
                await AdjustConfidenceWeightsAsync(userId, feedback, learningSignal);

                // Store feedback for future learning;
                await _preferenceStore.StoreFeedbackAsync(userId, feedback);

                // Trigger model retraining if needed;
                if (ShouldRetrainFromFeedback(feedback, learningSignal))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RetrainPreferenceModelAsync(userId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Feedback-triggered retraining failed for user: {UserId}", userId);
                        }
                    });
                }

                // Clear cache to reflect updated preferences;
                var cacheKey = GetPreferencesCacheKey(userId);
                _memoryCache.Remove(cacheKey);

                _logger.LogInformation("Learned from feedback for user {UserId} (Signal strength: {Signal:F2})",
                    userId, learningSignal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error learning from feedback for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.LearnFromFeedbackAsync", ex,
                    new { UserId = userId, Feedback = feedback });
                throw new PreferenceLearningException("Failed to learn from user feedback", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<UserPreferences> MergePreferencesAsync(string userId, IEnumerable<UserPreferences> preferenceSets)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (preferenceSets == null || !preferenceSets.Any())
                throw new ArgumentException("Preference sets cannot be null or empty", nameof(preferenceSets));

            try
            {
                await _semaphore.WaitAsync();

                _logger.LogDebug("Merging {SetCount} preference sets for user: {UserId}", preferenceSets.Count(), userId);

                // Filter out null sets;
                var validSets = preferenceSets.Where(p => p != null).ToList();
                if (validSets.Count < 2)
                {
                    _logger.LogWarning("Insufficient valid preference sets for merging for user: {UserId}", userId);
                    return validSets.FirstOrDefault();
                }

                // Merge categories;
                var mergedCategories = MergePreferenceCategories(validSets);

                // Resolve conflicts between preference sets;
                var resolvedCategories = ResolvePreferenceConflicts(mergedCategories);

                // Calculate merged confidence scores;
                var mergedPreferences = CalculateMergedConfidence(resolvedCategories, validSets);

                // Apply temporal weighting (more recent preferences get higher weight)
                ApplyTemporalWeighting(mergedPreferences, validSets);

                // Validate merged preferences;
                ValidateMergedPreferences(mergedPreferences);

                // Store merged preferences;
                await _preferenceStore.StorePreferencesAsync(userId, mergedPreferences);

                // Clear cache;
                var cacheKey = GetPreferencesCacheKey(userId);
                _memoryCache.Remove(cacheKey);

                _logger.LogInformation("Merged {SetCount} preference sets for user {UserId}, resulting in {CategoryCount} categories",
                    validSets.Count, userId, mergedPreferences.Categories.Count);

                return mergedPreferences;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error merging preferences for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.MergePreferencesAsync", ex,
                    new { UserId = userId, SetCount = preferenceSets.Count() });
                throw new PreferenceMergeException("Failed to merge user preferences", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<PreferenceChangeDetection> DetectPreferenceChangesAsync(string userId, TimeSpan analysisPeriod)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                _logger.LogDebug("Detecting preference changes for user {UserId} over {Period}", userId, analysisPeriod);

                // Get preference history for analysis period;
                var preferenceHistory = await _preferenceStore.GetPreferenceHistoryAsync(userId, analysisPeriod);

                if (preferenceHistory.Count() < 2)
                {
                    _logger.LogWarning("Insufficient preference history for change detection for user: {UserId}", userId);
                    return new PreferenceChangeDetection;
                    {
                        UserId = userId,
                        AnalysisPeriod = analysisPeriod,
                        HasSignificantChanges = false;
                    };
                }

                // Analyze changes over time;
                var changeAnalysis = AnalyzePreferenceChangesOverTime(preferenceHistory);

                // Detect significant changes;
                var significantChanges = DetectSignificantChanges(changeAnalysis);

                // Calculate change patterns;
                var changePatterns = CalculateChangePatterns(changeAnalysis);

                // Predict future changes;
                var futurePredictions = await PredictFutureChangesAsync(changeAnalysis, preferenceHistory);

                var detectionResult = new PreferenceChangeDetection;
                {
                    UserId = userId,
                    AnalysisPeriod = analysisPeriod,
                    HasSignificantChanges = significantChanges.Any(),
                    SignificantChanges = significantChanges,
                    ChangePatterns = changePatterns,
                    FuturePredictions = futurePredictions,
                    ChangeConfidence = CalculateChangeConfidence(changeAnalysis),
                    LastAnalysisTime = DateTime.UtcNow;
                };

                // Store change detection results;
                await _preferenceStore.StoreChangeDetectionAsync(userId, detectionResult);

                _logger.LogInformation("Detected {ChangeCount} significant preference changes for user {UserId}",
                    significantChanges.Count, userId);

                return detectionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting preference changes for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.DetectPreferenceChangesAsync", ex,
                    new { UserId = userId, AnalysisPeriod = analysisPeriod });
                throw new PreferenceChangeException("Failed to detect preference changes", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<SegmentPreferencePatterns> GetSegmentPatternsAsync(UserSegment segment)
        {
            if (segment == null)
                throw new ArgumentNullException(nameof(segment));

            try
            {
                _logger.LogDebug("Getting preference patterns for segment: {@Segment}", segment);

                // Get users in segment;
                var segmentUsers = await _preferenceStore.GetUsersInSegmentAsync(segment);

                if (!segmentUsers.Any())
                {
                    _logger.LogWarning("No users found in segment: {@Segment}", segment);
                    return new SegmentPreferencePatterns;
                    {
                        Segment = segment,
                        UserCount = 0;
                    };
                }

                // Get preferences for all users in segment;
                var allPreferences = new List<UserPreferences>();
                foreach (var userId in segmentUsers.Take(1000)) // Limit for performance;
                {
                    var preferences = await GetPreferencesAsync(userId);
                    if (preferences != null)
                    {
                        allPreferences.Add(preferences);
                    }
                }

                if (!allPreferences.Any())
                {
                    return new SegmentPreferencePatterns;
                    {
                        Segment = segment,
                        UserCount = segmentUsers.Count()
                    };
                }

                // Analyze common patterns;
                var commonPatterns = AnalyzeCommonPatterns(allPreferences, segment);

                // Identify segment-specific preferences;
                var segmentSpecific = IdentifySegmentSpecificPreferences(allPreferences, segment);

                // Calculate pattern confidence;
                var patterns = new SegmentPreferencePatterns;
                {
                    Segment = segment,
                    UserCount = segmentUsers.Count(),
                    AnalyzedUserCount = allPreferences.Count,
                    CommonPatterns = commonPatterns,
                    SegmentSpecificPreferences = segmentSpecific,
                    PatternConfidence = CalculateSegmentPatternConfidence(commonPatterns, allPreferences.Count),
                    LastUpdated = DateTime.UtcNow;
                };

                _logger.LogInformation("Analyzed preference patterns for segment {SegmentName} with {UserCount} users",
                    segment.Name, patterns.UserCount);

                return patterns;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting segment patterns for segment: {@Segment}", segment);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.GetSegmentPatternsAsync", ex, segment);
                throw new SegmentAnalysisException("Failed to analyze segment preference patterns", ex);
            }
        }

        /// <inheritdoc/>
        public async Task OptimizeForUserAsync(string userId, OptimizationCriteria criteria)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            try
            {
                await _semaphore.WaitAsync();

                _logger.LogDebug("Optimizing preference model for user {UserId} with criteria: {@Criteria}",
                    userId, criteria);

                // Get current preferences;
                var currentPreferences = await GetPreferencesAsync(userId, true);

                // Analyze optimization opportunities;
                var opportunities = await AnalyzeOptimizationOpportunitiesAsync(userId, currentPreferences, criteria);

                // Apply neural network optimization;
                await ApplyNeuralOptimizationAsync(userId, opportunities, criteria);

                // Adjust preference weights;
                await AdjustPreferenceWeightsAsync(userId, opportunities);

                // Validate optimization results;
                var optimizedPreferences = await ValidateOptimizationAsync(userId, currentPreferences);

                // Store optimized preferences;
                await _preferenceStore.StorePreferencesAsync(userId, optimizedPreferences);

                // Clear cache;
                var cacheKey = GetPreferencesCacheKey(userId);
                _memoryCache.Remove(cacheKey);

                _logger.LogInformation("Optimized preference model for user {UserId}, improved {OpportunityCount} areas",
                    userId, opportunities.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing preference model for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.OptimizeForUserAsync", ex,
                    new { UserId = userId, Criteria = criteria });
                throw new OptimizationException("Failed to optimize preference model", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<PreferenceExport> ExportPreferencesAsync(string userId, ExportFormat format)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            try
            {
                _logger.LogDebug("Exporting preferences for user {UserId} in format: {Format}", userId, format);

                // Get preferences;
                var preferences = await GetPreferencesAsync(userId);

                // Get preference history;
                var history = await _preferenceStore.GetPreferenceHistoryAsync(userId, TimeSpan.FromDays(365));

                // Create export data structure;
                var exportData = new PreferenceExport;
                {
                    UserId = userId,
                    ExportTimestamp = DateTime.UtcNow,
                    Format = format,
                    Preferences = preferences,
                    PreferenceHistory = history.ToList(),
                    Metadata = new ExportMetadata;
                    {
                        Version = "1.0",
                        ExportTool = "NEDA Preference Engine",
                        DataIntegrityHash = CalculateDataIntegrityHash(preferences, history)
                    }
                };

                // Serialize based on format;
                exportData.SerializedData = format switch;
                {
                    ExportFormat.JSON => SerializeToJson(exportData),
                    ExportFormat.XML => SerializeToXml(exportData),
                    ExportFormat.CSV => SerializeToCsv(exportData),
                    _ => SerializeToJson(exportData)
                };

                // Store export record;
                await _preferenceStore.StoreExportRecordAsync(userId, exportData);

                _logger.LogInformation("Exported preferences for user {UserId} in {Format} format, size: {Size} bytes",
                    userId, format, exportData.SerializedData.Length);

                return exportData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting preferences for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.ExportPreferencesAsync", ex,
                    new { UserId = userId, Format = format });
                throw new ExportException("Failed to export user preferences", ex);
            }
        }

        /// <inheritdoc/>
        public async Task ImportPreferencesAsync(string userId, PreferenceImport importData)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (importData == null)
                throw new ArgumentNullException(nameof(importData));

            try
            {
                await _semaphore.WaitAsync();

                _logger.LogDebug("Importing preferences for user {UserId} from import data", userId);

                // Validate import data;
                if (!ValidateImportData(importData))
                {
                    throw new ImportValidationException("Invalid import data");
                }

                // Parse import data based on format;
                var parsedPreferences = ParseImportData(importData);

                // Validate parsed preferences;
                ValidateParsedPreferences(parsedPreferences);

                // Merge with existing preferences;
                var existingPreferences = await GetPreferencesAsync(userId);
                var mergedPreferences = await MergePreferencesAsync(userId,
                    new[] { existingPreferences, parsedPreferences }.Where(p => p != null));

                // Apply import-specific adjustments;
                await ApplyImportAdjustmentsAsync(userId, mergedPreferences, importData);

                // Store import record;
                await _preferenceStore.StoreImportRecordAsync(userId, importData);

                // Clear cache;
                var cacheKey = GetPreferencesCacheKey(userId);
                _memoryCache.Remove(cacheKey);

                _logger.LogInformation("Imported preferences for user {UserId}, merged {PreferenceCount} categories",
                    userId, mergedPreferences.Categories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing preferences for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.ImportPreferencesAsync", ex,
                    new { UserId = userId, ImportData = importData });
                throw new ImportException("Failed to import user preferences", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task ResetPreferencesAsync(string userId, ResetOptions options)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            try
            {
                await _semaphore.WaitAsync();

                _logger.LogDebug("Resetting preferences for user {UserId} with options: {@Options}", userId, options);

                if (options.PreserveCategories?.Any() == true)
                {
                    // Partial reset - preserve specified categories;
                    await PerformPartialResetAsync(userId, options);
                }
                else;
                {
                    // Full reset;
                    await PerformFullResetAsync(userId, options);
                }

                // Clear all caches;
                var cacheKey = GetPreferencesCacheKey(userId);
                _memoryCache.Remove(cacheKey);

                // Remove learning session;
                _learningSessions.TryRemove(userId, out _);

                // Log reset event;
                await _preferenceStore.LogResetEventAsync(userId, options);

                _logger.LogInformation("Reset preferences for user {UserId} ({ResetType} reset)",
                    userId, options.PreserveCategories?.Any() == true ? "partial" : "full");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting preferences for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.ResetPreferencesAsync", ex,
                    new { UserId = userId, Options = options });
                throw new ResetException("Failed to reset user preferences", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<PreferenceLearningStats> GetStatisticsAsync(string userId = null)
        {
            try
            {
                _logger.LogDebug("Getting preference learning statistics for user: {UserId}", userId ?? "system");

                var stats = new PreferenceLearningStats;
                {
                    Timestamp = DateTime.UtcNow,
                    Scope = userId == null ? StatsScope.System : StatsScope.User;
                };

                if (userId != null)
                {
                    // User-specific statistics;
                    var session = _learningSessions.GetValueOrDefault(userId);
                    if (session != null)
                    {
                        stats.UserId = userId;
                        stats.TotalInteractions = session.TotalInteractions;
                        stats.RecentInteractions = session.RecentInteractions.Count;
                        stats.LastAnalysisTime = session.LastAnalysisTime;
                    }

                    var preferences = await _preferenceStore.GetPreferencesAsync(userId);
                    if (preferences != null)
                    {
                        stats.TotalPreferences = preferences.Categories.Sum(c => c.Preferences.Count);
                        stats.AverageConfidence = preferences.Categories.Average(c => c.Confidence);
                        stats.LastUpdated = preferences.LastUpdated;
                    }
                }
                else;
                {
                    // System-wide statistics;
                    stats.TotalUsers = await _preferenceStore.GetTotalUserCountAsync();
                    stats.ActiveUsers = await _preferenceStore.GetActiveUserCountAsync(TimeSpan.FromDays(30));
                    stats.TotalInteractions = await _preferenceStore.GetTotalInteractionCountAsync();
                    stats.AveragePreferencesPerUser = await _preferenceStore.GetAveragePreferencesPerUserAsync();
                }

                // Calculate learning effectiveness;
                stats.LearningEffectiveness = await CalculateLearningEffectivenessAsync(userId);

                // Get performance metrics;
                stats.PerformanceMetrics = await GetPerformanceMetricsAsync(userId);

                _logger.LogDebug("Generated preference statistics: {@Stats}", stats);

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting preference statistics for user: {UserId}", userId);
                await _errorReporter.ReportErrorAsync("PreferenceEngine.GetStatisticsAsync", ex,
                    new { UserId = userId });
                throw;
            }
        }

        #region Private Helper Methods;

        private async Task<UserPreferences> CreateDefaultPreferencesAsync(string userId)
        {
            var defaultPreferences = new UserPreferences;
            {
                UserId = userId,
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                OverallConfidence = 0.5,
                Categories = new List<PreferenceCategory>
                {
                    new PreferenceCategory;
                    {
                        Name = "General",
                        Confidence = 0.5,
                        Preferences = new List<PreferenceItem>
                        {
                            new PreferenceItem;
                            {
                                Key = "response_speed",
                                Value = "balanced",
                                Confidence = 0.5,
                                Source = PreferenceSource.Default;
                            }
                        }
                    }
                }
            };

            await _preferenceStore.StorePreferencesAsync(userId, defaultPreferences);
            return defaultPreferences;
        }

        private async Task<List<ExplicitPreference>> ExtractExplicitPreferencesAsync(IEnumerable<UserInteraction> interactions)
        {
            var explicitPreferences = new List<ExplicitPreference>();

            foreach (var interaction in interactions)
            {
                if (interaction.ExplicitPreferences != null)
                {
                    explicitPreferences.AddRange(interaction.ExplicitPreferences);
                }

                // Extract from interaction metadata;
                if (interaction.Metadata?.TryGetValue("preference_hint", out var hint) == true)
                {
                    explicitPreferences.Add(new ExplicitPreference;
                    {
                        Key = "hint_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        Value = hint.ToString(),
                        Strength = 0.7,
                        Context = interaction.Context;
                    });
                }
            }

            // Group and consolidate;
            var grouped = explicitPreferences;
                .GroupBy(p => p.Key)
                .Select(g => new ExplicitPreference;
                {
                    Key = g.Key,
                    Value = g.GroupBy(p => p.Value)
                           .OrderByDescending(vg => vg.Sum(p => p.Strength))
                           .First().Key,
                    Strength = g.Average(p => p.Strength),
                    Frequency = g.Count(),
                    Contexts = g.Select(p => p.Context).Distinct().ToList()
                })
                .Where(p => p.Strength >= PREFERENCE_CONFIDENCE_THRESHOLD)
                .ToList();

            return grouped;
        }

        private async Task<List<ImplicitPreference>> InferImplicitPreferencesAsync(
            BehaviorPatterns patterns, IEnumerable<UserInteraction> interactions)
        {
            var implicitPreferences = new List<ImplicitPreference>();

            // Infer from behavior patterns;
            foreach (var pattern in patterns.Patterns)
            {
                var inferred = await _patternRecognizer.InferPreferencesFromPatternAsync(pattern);
                if (inferred != null)
                {
                    implicitPreferences.AddRange(inferred);
                }
            }

            // Infer from interaction sequences;
            var sequencePreferences = InferFromInteractionSequences(interactions);
            implicitPreferences.AddRange(sequencePreferences);

            // Apply pattern recognition for hidden preferences;
            var hiddenPreferences = await _patternRecognizer.DiscoverHiddenPreferencesAsync(interactions);
            implicitPreferences.AddRange(hiddenPreferences);

            // Filter and consolidate;
            return implicitPreferences;
                .GroupBy(p => p.Category + ":" + p.Key)
                .Select(g => new ImplicitPreference;
                {
                    Category = g.First().Category,
                    Key = g.First().Key,
                    Value = g.First().Value,
                    Confidence = g.Average(p => p.Confidence),
                    Support = g.Sum(p => p.Support),
                    Evidence = g.SelectMany(p => p.Evidence).Distinct().ToList()
                })
                .Where(p => p.Confidence >= 0.6 && p.Support >= 3)
                .ToList();
        }

        private UserPreferences MergeAndPrioritizePreferences(
            List<ExplicitPreference> explicitPrefs, List<ImplicitPreference> implicitPrefs)
        {
            var categories = new Dictionary<string, PreferenceCategory>();

            // Process explicit preferences (higher priority)
            foreach (var expPref in explicitPrefs)
            {
                var categoryName = expPref.Category ?? "General";
                if (!categories.ContainsKey(categoryName))
                {
                    categories[categoryName] = new PreferenceCategory;
                    {
                        Name = categoryName,
                        Preferences = new List<PreferenceItem>()
                    };
                }

                categories[categoryName].Preferences.Add(new PreferenceItem;
                {
                    Key = expPref.Key,
                    Value = expPref.Value,
                    Confidence = expPref.Strength,
                    Source = PreferenceSource.Explicit,
                    LastObserved = DateTime.UtcNow;
                });
            }

            // Process implicit preferences (lower priority)
            foreach (var impPref in implicitPrefs)
            {
                var categoryName = impPref.Category;
                if (!categories.ContainsKey(categoryName))
                {
                    categories[categoryName] = new PreferenceCategory;
                    {
                        Name = categoryName,
                        Preferences = new List<PreferenceItem>()
                    };
                }

                // Check if similar preference already exists;
                var existing = categories[categoryName].Preferences;
                    .FirstOrDefault(p => p.Key == impPref.Key);

                if (existing != null)
                {
                    // Update confidence based on implicit evidence;
                    existing.Confidence = Math.Min(1.0, existing.Confidence + (impPref.Confidence * 0.2));
                    existing.Sources.Add(PreferenceSource.Implicit);
                }
                else;
                {
                    categories[categoryName].Preferences.Add(new PreferenceItem;
                    {
                        Key = impPref.Key,
                        Value = impPref.Value,
                        Confidence = impPref.Confidence,
                        Source = PreferenceSource.Implicit,
                        LastObserved = DateTime.UtcNow,
                        EvidenceCount = impPref.Evidence.Count;
                    });
                }
            }

            // Calculate category confidence;
            foreach (var category in categories.Values)
            {
                category.Confidence = category.Preferences.Any() ?
                    category.Preferences.Average(p => p.Confidence) : 0;
            }

            return new UserPreferences;
            {
                Categories = categories.Values.ToList(),
                LastUpdated = DateTime.UtcNow,
                OverallConfidence = categories.Values.Any() ?
                    categories.Values.Average(c => c.Confidence) : 0;
            };
        }

        private async Task<UserPreferences> RefineWithNeuralNetworkAsync(
            string userId, UserPreferences preferences, IEnumerable<UserInteraction> interactions)
        {
            try
            {
                // Prepare training data;
                var trainingData = PrepareNeuralTrainingData(interactions);

                // Refine preference confidence using neural network;
                var refinedPreferences = await _neuralEngine.RefinePreferencesAsync(
                    userId, preferences, trainingData);

                // Apply neural insights;
                var neuralInsights = await _neuralEngine.ExtractPreferenceInsightsAsync(interactions);
                ApplyNeuralInsights(refinedPreferences, neuralInsights);

                return refinedPreferences;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Neural network refinement failed for user: {UserId}", userId);
                return preferences; // Return original if refinement fails;
            }
        }

        private UserPreferences CalculatePreferenceConfidence(UserPreferences preferences, int interactionCount)
        {
            foreach (var category in preferences.Categories)
            {
                foreach (var pref in category.Preferences)
                {
                    // Adjust confidence based on interaction count;
                    var interactionFactor = Math.Min(1.0, interactionCount / 50.0);
                    pref.Confidence = Math.Min(1.0, pref.Confidence * interactionFactor);

                    // Apply source-specific confidence adjustments;
                    if (pref.Source == PreferenceSource.Explicit)
                    {
                        pref.Confidence *= 1.2;
                    }
                    else if (pref.Source == PreferenceSource.Inferred)
                    {
                        pref.Confidence *= 0.8;
                    }

                    pref.Confidence = Math.Min(1.0, Math.Max(0.1, pref.Confidence));
                }

                category.Confidence = category.Preferences.Any() ?
                    category.Preferences.Average(p => p.Confidence) : 0;
            }

            preferences.OverallConfidence = preferences.Categories.Any() ?
                preferences.Categories.Average(c => c.Confidence) : 0;

            return preferences;
        }

        private async Task StorePreferenceAnalysisAsync(
            string userId, UserPreferences preferences, BehaviorPatterns patterns)
        {
            var analysis = new PreferenceAnalysis;
            {
                UserId = userId,
                Timestamp = DateTime.UtcNow,
                Preferences = preferences,
                BehaviorPatterns = patterns,
                AnalysisVersion = "2.0",
                ConfidenceScore = preferences.OverallConfidence;
            };

            await _preferenceStore.StoreAnalysisAsync(userId, analysis);

            // Update learning session;
            if (_learningSessions.TryGetValue(userId, out var session))
            {
                session.LastAnalysisTime = DateTime.UtcNow;
                session.LastAnalysisConfidence = preferences.OverallConfidence;
            }
        }

        private string GetPreferencesCacheKey(string userId) => $"preferences_{userId}";

        private bool ShouldRefreshPreferences(UserPreferences preferences)
        {
            var age = DateTime.UtcNow - preferences.LastUpdated;
            var confidenceThreshold = preferences.OverallConfidence < 0.6;
            var ageThreshold = age > TimeSpan.FromDays(7);

            return confidenceThreshold || ageThreshold;
        }

        private List<PreferenceSignal> ExtractPreferenceSignals(UserInteraction interaction)
        {
            var signals = new List<PreferenceSignal>();

            // Extract from interaction type;
            signals.Add(new PreferenceSignal;
            {
                Type = SignalType.InteractionPattern,
                Category = interaction.Type,
                Strength = 0.7,
                Timestamp = interaction.Timestamp;
            });

            // Extract from context;
            if (interaction.Context != null)
            {
                signals.Add(new PreferenceSignal;
                {
                    Type = SignalType.Contextual,
                    Category = "context_" + interaction.Context.Type,
                    Strength = 0.6,
                    Timestamp = interaction.Timestamp;
                });
            }

            // Extract from duration (if available)
            if (interaction.Duration.HasValue)
            {
                var durationSignal = interaction.Duration.Value.TotalSeconds > 30 ?
                    "long_engagement" : "quick_interaction";

                signals.Add(new PreferenceSignal;
                {
                    Type = SignalType.Temporal,
                    Category = durationSignal,
                    Strength = 0.5,
                    Timestamp = interaction.Timestamp;
                });
            }

            return signals;
        }

        private async Task UpdatePreferenceModelIncrementallyAsync(
            string userId, List<PreferenceSignal> signals, PreferenceLearningSession session)
        {
            // Group signals by category;
            var groupedSignals = signals.GroupBy(s => s.Category);

            foreach (var group in groupedSignals)
            {
                var averageStrength = group.Average(s => s.Strength);
                var signalCount = group.Count();

                // Update or create preference item;
                await UpdatePreferenceItemAsync(userId, group.Key, averageStrength, signalCount);
            }

            // Update session statistics;
            session.RecentSignals.AddRange(signals);
            if (session.RecentSignals.Count > 100)
            {
                session.RecentSignals = session.RecentSignals;
                    .Skip(session.RecentSignals.Count - 50)
                    .ToList();
            }
        }

        private bool ShouldPerformBatchAnalysis(PreferenceLearningSession session)
        {
            return session.TotalInteractions % 50 == 0 ||
                   DateTime.UtcNow - session.LastAnalysisTime > TimeSpan.FromDays(1);
        }

        private PreferencePrediction CombinePreferencePredictions(
            PreferencePrediction neuralPrediction,
            UserPreferences preferences,
            IEnumerable<HistoricalContext> similarContexts,
            PreferenceContext currentContext)
        {
            var combined = new PreferencePrediction;
            {
                Context = currentContext,
                Timestamp = DateTime.UtcNow;
            };

            // Weight neural prediction highest;
            if (neuralPrediction != null && neuralPrediction.Confidence > 0.6)
            {
                combined.PreferredOption = neuralPrediction.PreferredOption;
                combined.Confidence = neuralPrediction.Confidence * 0.4;
            }

            // Add weight from similar historical contexts;
            if (similarContexts.Any())
            {
                var historicalChoice = similarContexts;
                    .GroupBy(c => c.ChosenOption)
                    .OrderByDescending(g => g.Count())
                    .First();

                var historicalWeight = 0.3 * (historicalChoice.Count() / (double)similarContexts.Count());
                combined.Confidence += historicalWeight;

                if (historicalWeight > combined.Confidence * 0.5)
                {
                    combined.PreferredOption = historicalChoice.Key;
                }
            }

            // Add weight from explicit preferences;
            var relevantPreferences = preferences.Categories;
                .SelectMany(c => c.Preferences)
                .Where(p => p.IsRelevantToContext(currentContext))
                .ToList();

            if (relevantPreferences.Any())
            {
                var preferenceWeight = 0.3 * relevantPreferences.Average(p => p.Confidence);
                combined.Confidence += preferenceWeight;
            }

            combined.Confidence = Math.Min(1.0, combined.Confidence);

            return combined;
        }

        private double CalculatePredictionConfidence(
            PreferencePrediction prediction, int historicalSupport, double preferenceConfidence)
        {
            var baseConfidence = prediction.Confidence;
            var supportFactor = Math.Min(1.0, historicalSupport / 10.0);
            var preferenceFactor = preferenceConfidence;

            return (baseConfidence * 0.5) + (supportFactor * 0.3) + (preferenceFactor * 0.2);
        }

        private async Task<List<ScoredCandidate>> ScoreRecommendationCandidatesAsync(
            IEnumerable<RecommendationCandidate> candidates,
            UserPreferences preferences,
            RecommendationContext context)
        {
            var scoredCandidates = new List<ScoredCandidate>();

            foreach (var candidate in candidates)
            {
                var score = new ScoredCandidate;
                {
                    Candidate = candidate,
                    BaseScore = candidate.RelevanceScore;
                };

                // Apply preference matching;
                score.PreferenceMatchScore = await CalculatePreferenceMatchScoreAsync(candidate, preferences);

                // Apply context relevance;
                score.ContextRelevanceScore = CalculateContextRelevance(candidate, context);

                // Apply novelty score (avoid recommending things user has recently seen)
                score.NoveltyScore = await CalculateNoveltyScoreAsync(candidate, preferences.UserId);

                // Calculate final score;
                score.FinalScore = (score.BaseScore * 0.3) +
                                 (score.PreferenceMatchScore * 0.4) +
                                 (score.ContextRelevanceScore * 0.2) +
                                 (score.NoveltyScore * 0.1);

                scoredCandidates.Add(score);
            }

            return scoredCandidates.OrderByDescending(s => s.FinalScore).ToList();
        }

        private IEnumerable<PersonalizationRecommendation> ApplyDiversityFilter(
            IEnumerable<ScoredCandidate> candidates, double diversityRequirement)
        {
            var filtered = new List<PersonalizationRecommendation>();
            var usedCategories = new HashSet<string>();

            foreach (var candidate in candidates)
            {
                if (usedCategories.Count >= diversityRequirement * 10)
                {
                    // Enough diversity achieved;
                    break;
                }

                var category = candidate.Candidate.Category;
                if (!usedCategories.Contains(category))
                {
                    usedCategories.Add(category);
                    filtered.Add(new PersonalizationRecommendation;
                    {
                        Item = candidate.Candidate,
                        Score = candidate.FinalScore,
                        Reason = $"Matches your interest in {category}"
                    });
                }
                else if (filtered.Count < 5) // Keep some from same category;
                {
                    filtered.Add(new PersonalizationRecommendation;
                    {
                        Item = candidate.Candidate,
                        Score = candidate.FinalScore,
                        Reason = $"Similar to your previous interests"
                    });
                }
            }

            return filtered.Take(10);
        }

        private async Task<IEnumerable<PersonalizationRecommendation>> GeneratePersonalizedRecommendationsAsync(
            IEnumerable<PersonalizationRecommendation> candidates,
            UserPreferences preferences,
            RecommendationContext context)
        {
            var recommendations = new List<PersonalizationRecommendation>();

            foreach (var candidate in candidates)
            {
                // Generate personalized explanation;
                var explanation = await GeneratePersonalizedExplanationAsync(candidate, preferences, context);
                candidate.Explanation = explanation;

                // Calculate confidence;
                candidate.Confidence = await CalculateRecommendationConfidenceAsync(candidate, preferences);

                recommendations.Add(candidate);
            }

            return recommendations.OrderByDescending(r => r.Score * r.Confidence);
        }

        private bool IsValidFeedback(PreferenceFeedback feedback)
        {
            return feedback != null &&
                   !string.IsNullOrEmpty(feedback.PredictionId) &&
                   feedback.FeedbackType != FeedbackType.Unknown &&
                   feedback.Timestamp > DateTime.UtcNow.AddDays(-7); // Only accept recent feedback;
        }

        private double CalculateLearningSignal(PreferenceFeedback feedback)
        {
            double baseSignal = feedback.FeedbackType switch;
            {
                FeedbackType.StronglyPositive => 1.0,
                FeedbackType.Positive => 0.7,
                FeedbackType.Neutral => 0.3,
                FeedbackType.Negative => 0.5,
                FeedbackType.StronglyNegative => 0.8,
                _ => 0.2;
            };

            // Adjust based on feedback details;
            if (!string.IsNullOrEmpty(feedback.Comment))
            {
                baseSignal *= 1.2;
            }

            return Math.Min(1.0, baseSignal);
        }

        private async Task UpdateModelsFromFeedbackAsync(
            string userId, PreferenceFeedback feedback, PreferenceContext context, double learningSignal)
        {
            // Update neural network model;
            await _neuralEngine.LearnFromFeedbackAsync(userId, feedback, context, learningSignal);

            // Update preference store;
            await _preferenceStore.UpdateFromFeedbackAsync(userId, feedback, learningSignal);

            // Update behavior analyzer;
            await _behaviorAnalyzer.AdjustForFeedbackAsync(userId, feedback, learningSignal);
        }

        private async Task AdjustConfidenceWeightsAsync(string userId, PreferenceFeedback feedback, double learningSignal)
        {
            var preferences = await GetPreferencesAsync(userId);
            var adjustmentFactor = learningSignal * ADAPTATION_LEARNING_RATE;

            foreach (var category in preferences.Categories)
            {
                foreach (var pref in category.Preferences)
                {
                    if (pref.IsRelevantToFeedback(feedback))
                    {
                        // Adjust confidence based on feedback;
                        pref.Confidence += feedback.FeedbackType == FeedbackType.Positive ?
                            adjustmentFactor : -adjustmentFactor;
                        pref.Confidence = Math.Max(0.1, Math.Min(1.0, pref.Confidence));

                        // Update last feedback time;
                        pref.LastFeedbackTime = DateTime.UtcNow;
                    }
                }
            }

            await _preferenceStore.StorePreferencesAsync(userId, preferences);
        }

        private bool ShouldRetrainFromFeedback(PreferenceFeedback feedback, double learningSignal)
        {
            return learningSignal > 0.7 ||
                   feedback.FeedbackType == FeedbackType.StronglyNegative ||
                   feedback.FeedbackType == FeedbackType.StronglyPositive;
        }

        private async Task RetrainPreferenceModelAsync(string userId)
        {
            _logger.LogInformation("Retraining preference model for user: {UserId}", userId);

            // Get recent interactions;
            var interactions = await _preferenceStore.GetUserInteractionsAsync(
                userId, DateTime.UtcNow.AddDays(-30), 1000);

            // Retrain neural model;
            await _neuralEngine.RetrainForUserAsync(userId, interactions);

            // Clear cache for fresh analysis;
            var cacheKey = GetPreferencesCacheKey(userId);
            _memoryCache.Remove(cacheKey);
        }

        private List<PreferenceCategory> MergePreferenceCategories(IEnumerable<UserPreferences> preferenceSets)
        {
            var mergedCategories = new Dictionary<string, PreferenceCategory>();

            foreach (var set in preferenceSets)
            {
                foreach (var category in set.Categories)
                {
                    if (!mergedCategories.ContainsKey(category.Name))
                    {
                        mergedCategories[category.Name] = new PreferenceCategory;
                        {
                            Name = category.Name,
                            Preferences = new List<PreferenceItem>()
                        };
                    }

                    var mergedCategory = mergedCategories[category.Name];

                    foreach (var pref in category.Preferences)
                    {
                        var existingPref = mergedCategory.Preferences;
                            .FirstOrDefault(p => p.Key == pref.Key);

                        if (existingPref != null)
                        {
                            // Merge values;
                            existingPref.Value = MergePreferenceValues(existingPref, pref);
                            existingPref.Confidence = Math.Max(existingPref.Confidence, pref.Confidence);
                            existingPref.Sources.UnionWith(pref.Sources);
                            existingPref.LastObserved = pref.LastObserved > existingPref.LastObserved ?
                                pref.LastObserved : existingPref.LastObserved;
                        }
                        else;
                        {
                            mergedCategory.Preferences.Add(pref.Clone());
                        }
                    }
                }
            }

            return mergedCategories.Values.ToList();
        }

        private List<PreferenceCategory> ResolvePreferenceConflicts(List<PreferenceCategory> mergedCategories)
        {
            foreach (var category in mergedCategories)
            {
                // Group preferences by key;
                var preferenceGroups = category.Preferences;
                    .GroupBy(p => p.Key)
                    .ToList();

                var resolvedPreferences = new List<PreferenceItem>();

                foreach (var group in preferenceGroups)
                {
                    if (group.Count() == 1)
                    {
                        resolvedPreferences.Add(group.First());
                        continue;
                    }

                    // Resolve conflicts based on confidence and recency;
                    var bestPreference = group;
                        .OrderByDescending(p => p.Confidence)
                        .ThenByDescending(p => p.LastObserved)
                        .First();

                    // Adjust confidence based on agreement;
                    var agreementLevel = group.Count(p => p.Value == bestPreference.Value) / (double)group.Count();
                    bestPreference.Confidence *= (1.0 + agreementLevel) / 2.0;

                    resolvedPreferences.Add(bestPreference);
                }

                category.Preferences = resolvedPreferences;
            }

            return mergedCategories;
        }

        private UserPreferences CalculateMergedConfidence(List<PreferenceCategory> categories, List<UserPreferences> originalSets)
        {
            foreach (var category in categories)
            {
                if (category.Preferences.Any())
                {
                    category.Confidence = category.Preferences.Average(p => p.Confidence);
                }
            }

            return new UserPreferences;
            {
                Categories = categories,
                LastUpdated = DateTime.UtcNow,
                OverallConfidence = categories.Any() ?
                    categories.Average(c => c.Confidence) : 0,
                SourceCount = originalSets.Count;
            };
        }

        private void ApplyTemporalWeighting(UserPreferences preferences, List<UserPreferences> originalSets)
        {
            var setWeights = originalSets;
                .Select((set, index) => new;
                {
                    Set = set,
                    Weight = Math.Pow(0.9, index) // Recent sets get higher weight;
                })
                .ToList();

            // This would be implemented to adjust confidence based on recency;
            // For simplicity, we're just documenting the approach;
        }

        private void ValidateMergedPreferences(UserPreferences preferences)
        {
            // Validate confidence ranges;
            foreach (var category in preferences.Categories)
            {
                category.Confidence = Math.Max(0, Math.Min(1, category.Confidence));

                foreach (var pref in category.Preferences)
                {
                    pref.Confidence = Math.Max(0.1, Math.Min(1.0, pref.Confidence));
                }
            }

            preferences.OverallConfidence = Math.Max(0, Math.Min(1, preferences.OverallConfidence));
        }

        private List<PreferenceChange> AnalyzePreferenceChangesOverTime(IEnumerable<PreferenceSnapshot> history)
        {
            var changes = new List<PreferenceChange>();
            var orderedHistory = history.OrderBy(h => h.Timestamp).ToList();

            for (int i = 1; i < orderedHistory.Count; i++)
            {
                var previous = orderedHistory[i - 1];
                var current = orderedHistory[i];

                var change = CalculatePreferenceChange(previous, current);
                if (change.Magnitude > 0.1) // Only track significant changes;
                {
                    changes.Add(change);
                }
            }

            return changes;
        }

        private List<SignificantChange> DetectSignificantChanges(List<PreferenceChange> changes)
        {
            var significant = new List<SignificantChange>();

            foreach (var change in changes.Where(c => c.Magnitude > 0.3))
            {
                significant.Add(new SignificantChange;
                {
                    Change = change,
                    SignificanceLevel = change.Magnitude > 0.5 ?
                        SignificanceLevel.High : SignificanceLevel.Medium,
                    ImpactScore = CalculateChangeImpact(change)
                });
            }

            return significant.OrderByDescending(s => s.ImpactScore).ToList();
        }

        private List<ChangePattern> CalculateChangePatterns(List<PreferenceChange> changes)
        {
            var patterns = new List<ChangePattern>();

            // Group changes by category;
            var categoryGroups = changes.GroupBy(c => c.Category);

            foreach (var group in categoryGroups)
            {
                if (group.Count() >= 3) // Need at least 3 changes to detect pattern;
                {
                    var pattern = DetectChangePattern(group.ToList());
                    if (pattern != null)
                    {
                        patterns.Add(pattern);
                    }
                }
            }

            return patterns;
        }

        private async Task<List<FutureChangePrediction>> PredictFutureChangesAsync(
            List<PreferenceChange> changes, IEnumerable<PreferenceSnapshot> history)
        {
            var predictions = new List<FutureChangePrediction>();

            // Use neural network for prediction;
            var neuralPredictions = await _neuralEngine.PredictPreferenceChangesAsync(history, changes);
            predictions.AddRange(neuralPredictions);

            // Add statistical predictions;
            var statisticalPredictions = CalculateStatisticalPredictions(changes);
            predictions.AddRange(statisticalPredictions);

            return predictions;
                .GroupBy(p => p.Category)
                .Select(g => g.OrderByDescending(p => p.Confidence).First())
                .Take(5)
                .ToList();
        }

        private double CalculateChangeConfidence(List<PreferenceChange> changes)
        {
            if (!changes.Any())
                return 1.0;

            var averageMagnitude = changes.Average(c => c.Magnitude);
            var consistency = 1.0 - (changes.Select(c => c.Magnitude).StdDev() / averageMagnitude);

            return (averageMagnitude * 0.6) + (consistency * 0.4);
        }

        private List<CommonPattern> AnalyzeCommonPatterns(List<UserPreferences> allPreferences, UserSegment segment)
        {
            var commonPatterns = new List<CommonPattern>();

            // Analyze preference frequency across segment;
            var preferenceFrequency = new Dictionary<string, int>();
            var preferenceValues = new Dictionary<string, Dictionary<string, int>>();

            foreach (var prefs in allPreferences)
            {
                foreach (var category in prefs.Categories)
                {
                    foreach (var pref in category.Preferences)
                    {
                        var key = $"{category.Name}.{pref.Key}";

                        if (!preferenceFrequency.ContainsKey(key))
                        {
                            preferenceFrequency[key] = 0;
                            preferenceValues[key] = new Dictionary<string, int>();
                        }

                        preferenceFrequency[key]++;

                        if (!preferenceValues[key].ContainsKey(pref.Value))
                        {
                            preferenceValues[key][pref.Value] = 0;
                        }
                        preferenceValues[key][pref.Value]++;
                    }
                }
            }

            // Identify common patterns (present in > 30% of users)
            var threshold = allPreferences.Count * 0.3;

            foreach (var kvp in preferenceFrequency.Where(f => f.Value > threshold))
            {
                var mostCommonValue = preferenceValues[kvp.Key]
                    .OrderByDescending(v => v.Value)
                    .First();

                commonPatterns.Add(new CommonPattern;
                {
                    Category = kvp.Key.Split('.')[0],
                    Key = kvp.Key.Split('.')[1],
                    Value = mostCommonValue.Key,
                    Frequency = kvp.Value / (double)allPreferences.Count,
                    Confidence = mostCommonValue.Value / (double)kvp.Value;
                });
            }

            return commonPatterns.OrderByDescending(p => p.Frequency).Take(20).ToList();
        }

        private List<SegmentSpecificPreference> IdentifySegmentSpecificPreferences(
            List<UserPreferences> allPreferences, UserSegment segment)
        {
            var segmentSpecific = new List<SegmentSpecificPreference>();

            // This would compare with other segments to find unique preferences;
            // For now, return preferences with high frequency in this segment;

            var threshold = allPreferences.Count * 0.5; // Present in > 50% of segment;

            // Similar analysis as common patterns but with higher threshold;
            return segmentSpecific;
        }

        private double CalculateSegmentPatternConfidence(List<CommonPattern> patterns, int userCount)
        {
            if (!patterns.Any())
                return 0.0;

            var averageFrequency = patterns.Average(p => p.Frequency);
            var averageConfidence = patterns.Average(p => p.Confidence);
            var userFactor = Math.Min(1.0, userCount / 100.0);

            return (averageFrequency * 0.4) + (averageConfidence * 0.4) + (userFactor * 0.2);
        }

        private async Task<List<OptimizationOpportunity>> AnalyzeOptimizationOpportunitiesAsync(
            string userId, UserPreferences preferences, OptimizationCriteria criteria)
        {
            var opportunities = new List<OptimizationOpportunity>();

            // Analyze low-confidence preferences;
            foreach (var category in preferences.Categories)
            {
                foreach (var pref in category.Preferences.Where(p => p.Confidence < 0.6))
                {
                    opportunities.Add(new OptimizationOpportunity;
                    {
                        Type = OptimizationType.ConfidenceImprovement,
                        Category = category.Name,
                        Key = pref.Key,
                        CurrentValue = pref.Confidence,
                        TargetValue = 0.8,
                        Priority = PriorityLevel.Medium;
                    });
                }
            }

            // Analyze stale preferences (not updated recently)
            var staleThreshold = DateTime.UtcNow.AddDays(-30);
            var stalePrefs = preferences.Categories;
                .SelectMany(c => c.Preferences)
                .Where(p => p.LastObserved < staleThreshold);

            foreach (var pref in stalePrefs)
            {
                opportunities.Add(new OptimizationOpportunity;
                {
                    Type = OptimizationType.RecencyUpdate,
                    Category = pref.Category,
                    Key = pref.Key,
                    DaysStale = (DateTime.UtcNow - pref.LastObserved).Days,
                    Priority = PriorityLevel.Low;
                });
            }

            // Get neural network suggestions;
            var neuralOpportunities = await _neuralEngine.SuggestOptimizationsAsync(userId, preferences);
            opportunities.AddRange(neuralOpportunities);

            return opportunities;
                .OrderByDescending(o => o.Priority)
                .ThenByDescending(o => o.PotentialImpact)
                .Take(10)
                .ToList();
        }

        private async Task ApplyNeuralOptimizationAsync(
            string userId, List<OptimizationOpportunity> opportunities, OptimizationCriteria criteria)
        {
            foreach (var opportunity in opportunities.Where(o => o.Priority >= PriorityLevel.Medium))
            {
                await _neuralEngine.OptimizePreferenceAsync(userId, opportunity, criteria);
            }
        }

        private async Task AdjustPreferenceWeightsAsync(string userId, List<OptimizationOpportunity> opportunities)
        {
            var preferences = await GetPreferencesAsync(userId);

            foreach (var opportunity in opportunities)
            {
                var pref = preferences.Categories;
                    .SelectMany(c => c.Preferences)
                    .FirstOrDefault(p => p.Category == opportunity.Category && p.Key == opportunity.Key);

                if (pref != null)
                {
                    // Adjust based on opportunity type;
                    switch (opportunity.Type)
                    {
                        case OptimizationType.ConfidenceImprovement:
                            pref.Confidence = Math.Min(1.0, pref.Confidence + 0.1);
                            break;
                        case OptimizationType.RecencyUpdate:
                            pref.LastObserved = DateTime.UtcNow;
                            break;
                    }
                }
            }

            await _preferenceStore.StorePreferencesAsync(userId, preferences);
        }

        private async Task<UserPreferences> ValidateOptimizationAsync(
            string userId, UserPreferences originalPreferences)
        {
            var optimized = await GetPreferencesAsync(userId);

            // Ensure optimization didn't break anything;
            var improvement = optimized.OverallConfidence - originalPreferences.OverallConfidence;

            if (improvement < -0.1) // Optimization made things worse;
            {
                _logger.LogWarning("Optimization degraded preferences for user {UserId}, rolling back", userId);
                return originalPreferences;
            }

            return optimized;
        }

        private string CalculateDataIntegrityHash(UserPreferences preferences, IEnumerable<PreferenceSnapshot> history)
        {
            // Simple hash calculation for data integrity;
            var dataString = $"{preferences.UserId}-{preferences.LastUpdated.Ticks}-{history.Count()}";
            return HashUtility.ComputeSHA256(dataString);
        }

        private string SerializeToJson(PreferenceExport exportData)
        {
            return System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions;
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });
        }

        private string SerializeToXml(PreferenceExport exportData)
        {
            // Simplified XML serialization;
            return $"<PreferenceExport userId=\"{exportData.UserId}\" timestamp=\"{exportData.ExportTimestamp}\" />";
        }

        private string SerializeToCsv(PreferenceExport exportData)
        {
            // Simplified CSV serialization;
            var lines = new List<string>
            {
                "Category,Key,Value,Confidence,LastObserved"
            };

            foreach (var category in exportData.Preferences.Categories)
            {
                foreach (var pref in category.Preferences)
                {
                    lines.Add($"\"{category.Name}\",\"{pref.Key}\",\"{pref.Value}\",{pref.Confidence:F2},{pref.LastObserved:yyyy-MM-dd}");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        private bool ValidateImportData(PreferenceImport importData)
        {
            return !string.IsNullOrEmpty(importData.SerializedData) &&
                   importData.Format != ExportFormat.Unknown &&
                   importData.Timestamp > DateTime.UtcNow.AddYears(-1); // Only accept imports from last year;
        }

        private UserPreferences ParseImportData(PreferenceImport importData)
        {
            return importData.Format switch;
            {
                ExportFormat.JSON => System.Text.Json.JsonSerializer.Deserialize<UserPreferences>(
                    importData.SerializedData),
                _ => throw new NotSupportedException($"Format {importData.Format} not supported for import")
            };
        }

        private void ValidateParsedPreferences(UserPreferences preferences)
        {
            if (preferences == null)
                throw new ImportValidationException("Failed to parse preferences from import data");

            if (preferences.Categories == null || !preferences.Categories.Any())
                throw new ImportValidationException("No preference categories found in import data");
        }

        private async Task ApplyImportAdjustmentsAsync(
            string userId, UserPreferences preferences, PreferenceImport importData)
        {
            // Mark preferences as imported;
            foreach (var category in preferences.Categories)
            {
                foreach (var pref in category.Preferences)
                {
                    pref.Sources.Add(PreferenceSource.Imported);
                }
            }

            preferences.LastImportTime = DateTime.UtcNow;
            preferences.ImportSource = importData.Source;

            await _preferenceStore.StorePreferencesAsync(userId, preferences);
        }

        private async Task PerformPartialResetAsync(string userId, ResetOptions options)
        {
            var currentPreferences = await GetPreferencesAsync(userId);
            var preservedCategories = options.PreserveCategories.ToHashSet();

            // Remove non-preserved categories;
            currentPreferences.Categories = currentPreferences.Categories;
                .Where(c => preservedCategories.Contains(c.Name))
                .ToList();

            // Update confidence for preserved categories (may be affected by reset)
            foreach (var category in currentPreferences.Categories)
            {
                category.Confidence *= 0.8; // Reduce confidence slightly;
            }

            await _preferenceStore.StorePreferencesAsync(userId, currentPreferences);
        }

        private async Task PerformFullResetAsync(string userId, ResetOptions options)
        {
            // Create fresh default preferences;
            var defaultPrefs = await CreateDefaultPreferencesAsync(userId);

            // Store reset preferences;
            await _preferenceStore.StorePreferencesAsync(userId, defaultPrefs);

            // Clear interaction history if requested;
            if (options.ClearHistory)
            {
                await _preferenceStore.ClearUserHistoryAsync(userId);
            }
        }

        private async Task<double> CalculateLearningEffectivenessAsync(string userId)
        {
            if (userId == null)
            {
                // System-wide effectiveness;
                var feedback = await _preferenceStore.GetRecentFeedbackAsync(TimeSpan.FromDays(30), 1000);
                if (!feedback.Any())
                    return 0.5;

                var positiveFeedback = feedback.Count(f =>
                    f.FeedbackType == FeedbackType.Positive ||
                    f.FeedbackType == FeedbackType.StronglyPositive);

                return positiveFeedback / (double)feedback.Count;
            }
            else;
            {
                // User-specific effectiveness;
                var userFeedback = await _preferenceStore.GetUserFeedbackAsync(userId, TimeSpan.FromDays(90));
                if (!userFeedback.Any())
                    return 0.5;

                var accuracy = await _preferenceStore.CalculatePredictionAccuracyAsync(userId);
                return accuracy;
            }
        }

        private async Task<PerformanceMetrics> GetPerformanceMetricsAsync(string userId)
        {
            return new PerformanceMetrics;
            {
                AverageProcessingTime = await CalculateAverageProcessingTimeAsync(userId),
                CacheHitRate = await CalculateCacheHitRateAsync(userId),
                MemoryUsage = CalculateMemoryUsage(),
                ActiveLearningSessions = _learningSessions.Count;
            };
        }

        private async Task<TimeSpan> CalculateAverageProcessingTimeAsync(string userId)
        {
            // This would come from performance monitoring;
            await Task.Delay(1);
            return TimeSpan.FromMilliseconds(new Random().Next(50, 200));
        }

        private async Task<double> CalculateCacheHitRateAsync(string userId)
        {
            // Simplified cache hit rate calculation;
            await Task.Delay(1);
            return 0.75; // 75% cache hit rate;
        }

        private long CalculateMemoryUsage()
        {
            return GC.GetTotalMemory(false) / 1024 / 1024; // MB;
        }

        private List<ImplicitPreference> InferFromInteractionSequences(IEnumerable<UserInteraction> interactions)
        {
            var implicitPreferences = new List<ImplicitPreference>();
            var orderedInteractions = interactions.OrderBy(i => i.Timestamp).ToList();

            // Look for patterns in sequences;
            for (int i = 1; i < orderedInteractions.Count; i++)
            {
                var previous = orderedInteractions[i - 1];
                var current = orderedInteractions[i];

                // Detect preference for certain sequences;
                if (previous.Type == "search" && current.Type == "select")
                {
                    implicitPreferences.Add(new ImplicitPreference;
                    {
                        Category = "interaction_flow",
                        Key = "search_to_select",
                        Value = "preferred",
                        Confidence = 0.6,
                        Support = 1,
                        Evidence = new List<string> { $"Sequence {previous.Id} -> {current.Id}" }
                    });
                }
            }

            return implicitPreferences;
        }

        private NeuralTrainingData PrepareNeuralTrainingData(IEnumerable<UserInteraction> interactions)
        {
            return new NeuralTrainingData;
            {
                Interactions = interactions.ToList(),
                Features = interactions.Select(i => ExtractNeuralFeatures(i)).ToList(),
                Timestamps = interactions.Select(i => i.Timestamp).ToList()
            };
        }

        private NeuralFeatures ExtractNeuralFeatures(UserInteraction interaction)
        {
            return new NeuralFeatures;
            {
                Type = interaction.Type,
                Duration = (float)(interaction.Duration?.TotalSeconds ?? 0),
                ContextType = interaction.Context?.Type ?? "unknown",
                TimeOfDay = interaction.Timestamp.Hour / 23.0f;
            };
        }

        private void ApplyNeuralInsights(UserPreferences preferences, NeuralInsights insights)
        {
            // Apply neural network insights to preferences;
            foreach (var insight in insights.Insights)
            {
                var category = preferences.Categories.FirstOrDefault(c => c.Name == insight.Category);
                if (category == null)
                {
                    category = new PreferenceCategory;
                    {
                        Name = insight.Category,
                        Preferences = new List<PreferenceItem>()
                    };
                    preferences.Categories.Add(category);
                }

                var pref = category.Preferences.FirstOrDefault(p => p.Key == insight.Key);
                if (pref == null)
                {
                    category.Preferences.Add(new PreferenceItem;
                    {
                        Key = insight.Key,
                        Value = insight.Value,
                        Confidence = insight.Confidence,
                        Source = PreferenceSource.Inferred,
                        LastObserved = DateTime.UtcNow;
                    });
                }
                else;
                {
                    pref.Confidence = Math.Max(pref.Confidence, insight.Confidence);
                }
            }
        }

        private async Task UpdatePreferenceItemAsync(string userId, string categoryKey, double strength, int signalCount)
        {
            var preferences = await GetPreferencesAsync(userId);
            var category = preferences.Categories.FirstOrDefault(c => c.Name == categoryKey);

            if (category == null)
            {
                category = new PreferenceCategory;
                {
                    Name = categoryKey,
                    Preferences = new List<PreferenceItem>()
                };
                preferences.Categories.Add(category);
            }

            // Find or create preference item;
            var prefKey = $"auto_{categoryKey}";
            var pref = category.Preferences.FirstOrDefault(p => p.Key == prefKey);

            if (pref == null)
            {
                pref = new PreferenceItem;
                {
                    Key = prefKey,
                    Value = "detected",
                    Confidence = strength,
                    Source = PreferenceSource.Inferred,
                    LastObserved = DateTime.UtcNow;
                };
                category.Preferences.Add(pref);
            }
            else;
            {
                // Update confidence with moving average;
                pref.Confidence = (pref.Confidence * 0.7) + (strength * 0.3);
                pref.LastObserved = DateTime.UtcNow;
            }

            await _preferenceStore.StorePreferencesAsync(userId, preferences);
        }

        private async Task<double> CalculatePreferenceMatchScoreAsync(
            RecommendationCandidate candidate, UserPreferences preferences)
        {
            double totalScore = 0.0;
            int matchCount = 0;

            foreach (var category in preferences.Categories)
            {
                foreach (var pref in category.Preferences)
                {
                    if (candidate.MatchesPreference(pref))
                    {
                        totalScore += pref.Confidence;
                        matchCount++;
                    }
                }
            }

            return matchCount > 0 ? totalScore / matchCount : 0.0;
        }

        private double CalculateContextRelevance(RecommendationCandidate candidate, RecommendationContext context)
        {
            // Simple context matching;
            var contextMatch = candidate.ContextTags?.Intersect(context.Tags ?? new List<string>()).Count() ?? 0;
            var maxContextTags = Math.Max(candidate.ContextTags?.Count ?? 0, context.Tags?.Count ?? 1);

            return contextMatch / (double)maxContextTags;
        }

        private async Task<double> CalculateNoveltyScoreAsync(RecommendationCandidate candidate, string userId)
        {
            var recentRecommendations = await _preferenceStore.GetRecentRecommendationsAsync(userId, TimeSpan.FromDays(7));
            var similarCount = recentRecommendations.Count(r => r.IsSimilarTo(candidate));

            // Higher novelty if not recently recommended;
            return Math.Exp(-similarCount * 0.5);
        }

        private async Task<string> GeneratePersonalizedExplanationAsync(
            PersonalizationRecommendation candidate, UserPreferences preferences, RecommendationContext context)
        {
            // Find matching preferences to explain recommendation;
            var matchingPrefs = preferences.Categories;
                .SelectMany(c => c.Preferences)
                .Where(p => candidate.Item.MatchesPreference(p))
                .OrderByDescending(p => p.Confidence)
                .Take(2)
                .ToList();

            if (matchingPrefs.Any())
            {
                var pref = matchingPrefs.First();
                return $"Based on your preference for {pref.Key} ({pref.Value})";
            }

            // Fallback to context-based explanation;
            if (context.Tags?.Any() == true)
            {
                return $"Related to your interest in {string.Join(", ", context.Tags.Take(2))}";
            }

            return "You might find this interesting based on your activity";
        }

        private async Task<double> CalculateRecommendationConfidenceAsync(
            PersonalizationRecommendation recommendation, UserPreferences preferences)
        {
            var preferenceMatch = await CalculatePreferenceMatchScoreAsync(recommendation.Item, preferences);
            var scoreFactor = recommendation.Score;
            var noveltyFactor = recommendation.NoveltyScore;

            return (preferenceMatch * 0.5) + (scoreFactor * 0.3) + (noveltyFactor * 0.2);
        }

        private PreferenceChange CalculatePreferenceChange(PreferenceSnapshot previous, PreferenceSnapshot current)
        {
            // Calculate change magnitude between snapshots;
            // This is simplified - actual implementation would compare preference values;
            return new PreferenceChange;
            {
                Category = "overall",
                PreviousConfidence = previous.OverallConfidence,
                CurrentConfidence = current.OverallConfidence,
                Magnitude = Math.Abs(current.OverallConfidence - previous.OverallConfidence),
                Timestamp = current.Timestamp;
            };
        }

        private double CalculateChangeImpact(PreferenceChange change)
        {
            return change.Magnitude * (change.CurrentConfidence > change.PreviousConfidence ? 1.0 : 0.8);
        }

        private ChangePattern DetectChangePattern(List<PreferenceChange> changes)
        {
            // Simple pattern detection;
            var magnitudes = changes.Select(c => c.Magnitude).ToList();
            var averageMagnitude = magnitudes.Average();
            var isIncreasing = changes.Last().CurrentConfidence > changes.First().PreviousConfidence;

            return new ChangePattern;
            {
                Category = changes.First().Category,
                PatternType = isIncreasing ? ChangePatternType.Increasing : ChangePatternType.Decreasing,
                Strength = averageMagnitude,
                Confidence = 1.0 - (magnitudes.StdDev() / averageMagnitude),
                StartTime = changes.First().Timestamp,
                EndTime = changes.Last().Timestamp;
            };
        }

        private List<FutureChangePrediction> CalculateStatisticalPredictions(List<PreferenceChange> changes)
        {
            var predictions = new List<FutureChangePrediction>();

            // Group by category;
            var categoryGroups = changes.GroupBy(c => c.Category);

            foreach (var group in categoryGroups.Where(g => g.Count() >= 3))
            {
                var recentChanges = group.OrderByDescending(c => c.Timestamp).Take(3).ToList();
                var avgChange = recentChanges.Average(c => c.Magnitude);
                var trend = recentChanges.Last().CurrentConfidence > recentChanges.First().PreviousConfidence;

                predictions.Add(new FutureChangePrediction;
                {
                    Category = group.Key,
                    PredictedChange = trend ? avgChange : -avgChange,
                    Confidence = 0.6,
                    PredictionHorizon = TimeSpan.FromDays(7),
                    Basis = "statistical_trend"
                });
            }

            return predictions;
        }

        private string MergePreferenceValues(PreferenceItem existing, PreferenceItem newItem)
        {
            // If confidences are close, prefer the newer value;
            if (Math.Abs(existing.Confidence - newItem.Confidence) < 0.2)
            {
                return newItem.LastObserved > existing.LastObserved ?
                    newItem.Value : existing.Value;
            }

            // Otherwise prefer higher confidence;
            return newItem.Confidence > existing.Confidence ?
                newItem.Value : existing.Value;
        }

        #endregion;
    }

    #region Supporting Models and Enums;

    public class UserPreferences;
    {
        public string UserId { get; set; }
        public List<PreferenceCategory> Categories { get; set; } = new List<PreferenceCategory>();
        public DateTime Created { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime? LastImportTime { get; set; }
        public string ImportSource { get; set; }
        public double OverallConfidence { get; set; }
        public int SourceCount { get; set; } = 1;
    }

    public class PreferenceCategory;
    {
        public string Name { get; set; }
        public List<PreferenceItem> Preferences { get; set; } = new List<PreferenceItem>();
        public double Confidence { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class PreferenceItem;
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public double Confidence { get; set; }
        public HashSet<PreferenceSource> Sources { get; set; } = new HashSet<PreferenceSource>();
        public string Category { get; set; }
        public DateTime LastObserved { get; set; }
        public DateTime? LastFeedbackTime { get; set; }
        public int EvidenceCount { get; set; }

        public PreferenceSource Source;
        {
            get => Sources.FirstOrDefault();
            set { Sources.Clear(); Sources.Add(value); }
        }

        public bool IsRelevantToContext(PreferenceContext context) => true; // Simplified;
        public bool IsRelevantToFeedback(PreferenceFeedback feedback) => true; // Simplified;

        public PreferenceItem Clone()
        {
            return new PreferenceItem;
            {
                Key = Key,
                Value = Value,
                Confidence = Confidence,
                Sources = new HashSet<PreferenceSource>(Sources),
                Category = Category,
                LastObserved = LastObserved,
                LastFeedbackTime = LastFeedbackTime,
                EvidenceCount = EvidenceCount;
            };
        }
    }

    public class UserInteraction;
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public string Type { get; set; }
        public DateTime Timestamp { get; set; }
        public TimeSpan? Duration { get; set; }
        public InteractionContext Context { get; set; }
        public List<ExplicitPreference> ExplicitPreferences { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ExplicitPreference;
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public string Category { get; set; }
        public double Strength { get; set; }
        public PreferenceContext Context { get; set; }
        public int Frequency { get; set; } = 1;
        public List<PreferenceContext> Contexts { get; set; }
    }

    public class ImplicitPreference;
    {
        public string Category { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public double Confidence { get; set; }
        public int Support { get; set; }
        public List<string> Evidence { get; set; }
    }

    public class PreferenceAnalysis;
    {
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public UserPreferences Preferences { get; set; }
        public BehaviorPatterns BehaviorPatterns { get; set; }
        public string AnalysisVersion { get; set; }
        public double ConfidenceScore { get; set; }
    }

    public class BehaviorPatterns;
    {
        public List<BehaviorPattern> Patterns { get; set; } = new List<BehaviorPattern>();
        public DateTime AnalysisTime { get; set; }
        public double PatternConfidence { get; set; }
    }

    public class BehaviorPattern;
    {
        public string PatternType { get; set; }
        public List<UserInteraction> SupportingInteractions { get; set; }
        public double Strength { get; set; }
    }

    public class PreferenceSignal;
    {
        public SignalType Type { get; set; }
        public string Category { get; set; }
        public double Strength { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PreferenceLearningSession;
    {
        public string UserId { get; set; }
        public int TotalInteractions { get; set; }
        public List<UserInteraction> RecentInteractions { get; set; } = new List<UserInteraction>();
        public List<PreferenceSignal> RecentSignals { get; set; } = new List<PreferenceSignal>();
        public DateTime LastAnalysisTime { get; set; } = DateTime.UtcNow;
        public double LastAnalysisConfidence { get; set; }
    }

    public class PreferencePrediction;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public PreferenceContext Context { get; set; }
        public string PreferredOption { get; set; }
        public double Confidence { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, double> OptionScores { get; set; } = new Dictionary<string, double>();
    }

    public class PersonalizationRecommendation;
    {
        public RecommendationCandidate Item { get; set; }
        public double Score { get; set; }
        public double Confidence { get; set; }
        public string Explanation { get; set; }
        public double NoveltyScore { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }

    public class RecommendationCandidate;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public List<string> Tags { get; set; }
        public List<string> ContextTags { get; set; }
        public double RelevanceScore { get; set; }

        public bool MatchesPreference(PreferenceItem preference) =>
            Tags?.Contains(preference.Key) == true ||
            Category == preference.Category;

        public bool IsSimilarTo(PersonalizationRecommendation other) =>
            Category == other.Item.Category &&
            Tags?.Intersect(other.Item.Tags ?? new List<string>()).Any() == true;
    }

    public class PreferenceFeedback;
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public string PredictionId { get; set; }
        public FeedbackType FeedbackType { get; set; }
        public string Comment { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class PreferenceChangeDetection;
    {
        public string UserId { get; set; }
        public TimeSpan AnalysisPeriod { get; set; }
        public bool HasSignificantChanges { get; set; }
        public List<SignificantChange> SignificantChanges { get; set; } = new List<SignificantChange>();
        public List<ChangePattern> ChangePatterns { get; set; } = new List<ChangePattern>();
        public List<FutureChangePrediction> FuturePredictions { get; set; } = new List<FutureChangePrediction>();
        public double ChangeConfidence { get; set; }
        public DateTime LastAnalysisTime { get; set; }
    }

    public class PreferenceChange;
    {
        public string Category { get; set; }
        public double PreviousConfidence { get; set; }
        public double CurrentConfidence { get; set; }
        public double Magnitude { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SignificantChange;
    {
        public PreferenceChange Change { get; set; }
        public SignificanceLevel SignificanceLevel { get; set; }
        public double ImpactScore { get; set; }
    }

    public class ChangePattern;
    {
        public string Category { get; set; }
        public ChangePatternType PatternType { get; set; }
        public double Strength { get; set; }
        public double Confidence { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public class FutureChangePrediction;
    {
        public string Category { get; set; }
        public double PredictedChange { get; set; }
        public double Confidence { get; set; }
        public TimeSpan PredictionHorizon { get; set; }
        public string Basis { get; set; }
    }

    public class SegmentPreferencePatterns;
    {
        public UserSegment Segment { get; set; }
        public int UserCount { get; set; }
        public int AnalyzedUserCount { get; set; }
        public List<CommonPattern> CommonPatterns { get; set; } = new List<CommonPattern>();
        public List<SegmentSpecificPreference> SegmentSpecificPreferences { get; set; } = new List<SegmentSpecificPreference>();
        public double PatternConfidence { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class CommonPattern;
    {
        public string Category { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public double Frequency { get; set; }
        public double Confidence { get; set; }
    }

    public class SegmentSpecificPreference;
    {
        public string Category { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public double SegmentFrequency { get; set; }
        public double OtherSegmentsFrequency { get; set; }
        public double SpecificityScore { get; set; }
    }

    public class OptimizationOpportunity;
    {
        public OptimizationType Type { get; set; }
        public string Category { get; set; }
        public string Key { get; set; }
        public double CurrentValue { get; set; }
        public double TargetValue { get; set; }
        public int DaysStale { get; set; }
        public PriorityLevel Priority { get; set; }
        public double PotentialImpact { get; set; }
    }

    public class PreferenceExport;
    {
        public string UserId { get; set; }
        public DateTime ExportTimestamp { get; set; }
        public ExportFormat Format { get; set; }
        public UserPreferences Preferences { get; set; }
        public List<PreferenceSnapshot> PreferenceHistory { get; set; }
        public ExportMetadata Metadata { get; set; }
        public string SerializedData { get; set; }
    }

    public class PreferenceImport;
    {
        public string Source { get; set; }
        public ExportFormat Format { get; set; }
        public string SerializedData { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class PreferenceLearningStats;
    {
        public DateTime Timestamp { get; set; }
        public StatsScope Scope { get; set; }
        public string UserId { get; set; }
        public int TotalInteractions { get; set; }
        public int RecentInteractions { get; set; }
        public int TotalPreferences { get; set; }
        public double AverageConfidence { get; set; }
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public double AveragePreferencesPerUser { get; set; }
        public double LearningEffectiveness { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
        public DateTime? LastAnalysisTime { get; set; }
        public DateTime? LastUpdated { get; set; }
    }

    public class PerformanceMetrics;
    {
        public TimeSpan AverageProcessingTime { get; set; }
        public double CacheHitRate { get; set; }
        public long MemoryUsage { get; set; } // MB;
        public int ActiveLearningSessions { get; set; }
    }

    public class ScoredCandidate;
    {
        public RecommendationCandidate Candidate { get; set; }
        public double BaseScore { get; set; }
        public double PreferenceMatchScore { get; set; }
        public double ContextRelevanceScore { get; set; }
        public double NoveltyScore { get; set; }
        public double FinalScore { get; set; }
    }

    public class NeuralTrainingData;
    {
        public List<UserInteraction> Interactions { get; set; }
        public List<NeuralFeatures> Features { get; set; }
        public List<DateTime> Timestamps { get; set; }
    }

    public class NeuralFeatures;
    {
        public string Type { get; set; }
        public float Duration { get; set; }
        public string ContextType { get; set; }
        public float TimeOfDay { get; set; }
    }

    public class NeuralInsights;
    {
        public List<NeuralInsight> Insights { get; set; } = new List<NeuralInsight>();
        public double Confidence { get; set; }
    }

    public class NeuralInsight;
    {
        public string Category { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public double Confidence { get; set; }
    }

    public class PreferenceSnapshot;
    {
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public double OverallConfidence { get; set; }
        public Dictionary<string, double> CategoryConfidences { get; set; }
    }

    public class HistoricalContext;
    {
        public PreferenceContext Context { get; set; }
        public string ChosenOption { get; set; }
        public double Satisfaction { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PreferenceAnalysisOptions;
    {
        public TimeSpan AnalysisPeriod { get; set; } = TimeSpan.FromDays(30);
        public int MaxInteractions { get; set; } = 1000;
        public int PatternDepth { get; set; } = 3;
        public bool ForceAnalysis { get; set; } = false;
    }

    public class PreferenceContext;
    {
        public string Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class RecommendationContext;
    {
        public string Type { get; set; }
        public List<string> Tags { get; set; }
        public double DiversityRequirement { get; set; } = 0.5;
        public int MaxResults { get; set; } = 10;
    }

    public class InteractionContext;
    {
        public string Type { get; set; }
        public Dictionary<string, object> Data { get; set; }
    }

    public class ExportMetadata;
    {
        public string Version { get; set; }
        public string ExportTool { get; set; }
        public string DataIntegrityHash { get; set; }
    }

    public class UserSegment;
    {
        public string Name { get; set; }
        public Dictionary<string, object> Criteria { get; set; }
        public DateTime Created { get; set; }
    }

    public class OptimizationCriteria;
    {
        public OptimizationGoal Goal { get; set; }
        public TimeSpan TimeLimit { get; set; } = TimeSpan.FromMinutes(5);
        public double QualityThreshold { get; set; } = 0.7;
    }

    public class ResetOptions;
    {
        public List<string> PreserveCategories { get; set; }
        public bool ClearHistory { get; set; } = false;
        public string Reason { get; set; }
    }

    public enum PreferenceSource;
    {
        Default,
        Explicit,
        Implicit,
        Inferred,
        Imported;
    }

    public enum SignalType;
    {
        InteractionPattern,
        Contextual,
        Temporal,
        Behavioral;
    }

    public enum FeedbackType;
    {
        Unknown,
        StronglyPositive,
        Positive,
        Neutral,
        Negative,
        StronglyNegative;
    }

    public enum SignificanceLevel;
    {
        Low,
        Medium,
        High;
    }

    public enum ChangePatternType;
    {
        Increasing,
        Decreasing,
        Cyclical,
        Stable,
        Volatile;
    }

    public enum ExportFormat;
    {
        Unknown,
        JSON,
        XML,
        CSV;
    }

    public enum StatsScope;
    {
        User,
        System;
    }

    public enum OptimizationType;
    {
        ConfidenceImprovement,
        RecencyUpdate,
        PatternAlignment,
        DiversityEnhancement;
    }

    public enum PriorityLevel;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    public enum OptimizationGoal;
    {
        Accuracy,
        Speed,
        Coverage,
        Personalization;
    }

    #endregion;

    #region Exceptions;

    public class PreferenceAnalysisException : Exception
    {
        public PreferenceAnalysisException(string message) : base(message) { }
        public PreferenceAnalysisException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PreferenceUpdateException : Exception
    {
        public PreferenceUpdateException(string message) : base(message) { }
        public PreferenceUpdateException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PreferencePredictionException : Exception
    {
        public PreferencePredictionException(string message) : base(message) { }
        public PreferencePredictionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class RecommendationException : Exception
    {
        public RecommendationException(string message) : base(message) { }
        public RecommendationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PreferenceLearningException : Exception
    {
        public PreferenceLearningException(string message) : base(message) { }
        public PreferenceLearningException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PreferenceMergeException : Exception
    {
        public PreferenceMergeException(string message) : base(message) { }
        public PreferenceMergeException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PreferenceChangeException : Exception
    {
        public PreferenceChangeException(string message) : base(message) { }
        public PreferenceChangeException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SegmentAnalysisException : Exception
    {
        public SegmentAnalysisException(string message) : base(message) { }
        public SegmentAnalysisException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class OptimizationException : Exception
    {
        public OptimizationException(string message) : base(message) { }
        public OptimizationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ExportException : Exception
    {
        public ExportException(string message) : base(message) { }
        public ExportException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ImportException : Exception
    {
        public ImportException(string message) : base(message) { }
        public ImportException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class ImportValidationException : Exception
    {
        public ImportValidationException(string message) : base(message) { }
    }

    public class ResetException : Exception
    {
        public ResetException(string message) : base(message) { }
        public ResetException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Interfaces for Dependencies;

    public interface IPreferenceStore;
    {
        Task<IEnumerable<UserInteraction>> GetUserInteractionsAsync(string userId, DateTime since, int maxResults);
        Task<UserPreferences> GetPreferencesAsync(string userId);
        Task StoreInteractionAsync(string userId, UserInteraction interaction);
        Task StorePreferencesAsync(string userId, UserPreferences preferences);
        Task StoreAnalysisAsync(string userId, PreferenceAnalysis analysis);
        Task<IEnumerable<HistoricalContext>> FindSimilarContextsAsync(string userId, PreferenceContext context, int maxResults);
        Task<IEnumerable<RecommendationCandidate>> GetRecommendationCandidatesAsync(RecommendationContext context);
        Task StoreRecommendationsAsync(string userId, IEnumerable<PersonalizationRecommendation> recommendations);
        Task<PreferenceContext> GetPredictionContextAsync(string predictionId);
        Task StoreFeedbackAsync(string userId, PreferenceFeedback feedback);
        Task UpdateFromFeedbackAsync(string userId, PreferenceFeedback feedback, double learningSignal);
        Task StorePredictionAsync(string userId, PreferenceContext context, PreferencePrediction prediction);
        Task<IEnumerable<PreferenceSnapshot>> GetPreferenceHistoryAsync(string userId, TimeSpan period);
        Task StoreChangeDetectionAsync(string userId, PreferenceChangeDetection detection);
        Task<IEnumerable<string>> GetUsersInSegmentAsync(UserSegment segment);
        Task StoreExportRecordAsync(string userId, PreferenceExport export);
        Task StoreImportRecordAsync(string userId, PreferenceImport import);
        Task ClearUserHistoryAsync(string userId);
        Task LogResetEventAsync(string userId, ResetOptions options);
        Task<long> GetTotalUserCountAsync();
        Task<long> GetActiveUserCountAsync(TimeSpan period);
        Task<long> GetTotalInteractionCountAsync();
        Task<double> GetAveragePreferencesPerUserAsync();
        Task<IEnumerable<PreferenceFeedback>> GetRecentFeedbackAsync(TimeSpan period, int maxResults);
        Task<IEnumerable<PreferenceFeedback>> GetUserFeedbackAsync(string userId, TimeSpan period);
        Task<double> CalculatePredictionAccuracyAsync(string userId);
        Task<IEnumerable<PersonalizationRecommendation>> GetRecentRecommendationsAsync(string userId, TimeSpan period);
    }

    public interface IBehaviorAnalyzer;
    {
        Task<BehaviorPatterns> AnalyzePatternsAsync(IEnumerable<UserInteraction> interactions, int depth);
        Task AdjustForFeedbackAsync(string userId, PreferenceFeedback feedback, double learningSignal);
    }

    public interface IPatternRecognizer;
    {
        Task<List<ImplicitPreference>> InferPreferencesFromPatternAsync(BehaviorPattern pattern);
        Task<List<ImplicitPreference>> DiscoverHiddenPreferencesAsync(IEnumerable<UserInteraction> interactions);
    }

    public interface INeuralNetworkEngine;
    {
        Task<UserPreferences> RefinePreferencesAsync(string userId, UserPreferences preferences, NeuralTrainingData trainingData);
        Task<PreferencePrediction> PredictPreferenceAsync(string userId, PreferenceContext context, UserPreferences preferences);
        Task<NeuralInsights> ExtractPreferenceInsightsAsync(IEnumerable<UserInteraction> interactions);
        Task LearnFromFeedbackAsync(string userId, PreferenceFeedback feedback, PreferenceContext context, double learningSignal);
        Task RetrainForUserAsync(string userId, IEnumerable<UserInteraction> interactions);
        Task<List<OptimizationOpportunity>> SuggestOptimizationsAsync(string userId, UserPreferences preferences);
        Task OptimizePreferenceAsync(string userId, OptimizationOpportunity opportunity, OptimizationCriteria criteria);
        Task<List<FutureChangePrediction>> PredictPreferenceChangesAsync(IEnumerable<PreferenceSnapshot> history, List<PreferenceChange> changes);
    }

    public interface IErrorReporter;
    {
        Task ReportErrorAsync(string methodName, Exception exception, object additionalData = null);
    }

    #endregion;

    #region Utility Extensions;

    public static class ListExtensions;
    {
        public static double StdDev(this IEnumerable<double> values)
        {
            var mean = values.Average();
            var variance = values.Average(v => Math.Pow(v - mean, 2));
            return Math.Sqrt(variance);
        }
    }

    public static class HashUtility;
    {
        public static string ComputeSHA256(string input)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }
    }

    #endregion;
}
