// NEDA.Communication/EmotionalIntelligence/PersonalityTraits/TraitManager.cs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.Middleware;
using NEDA.Automation.ScenarioPlanner;
using NEDA.Brain;
using NEDA.Brain.KnowledgeBase.ProblemSolutions;
using NEDA.Common;
using NEDA.Communication.DialogSystem.QuestionAnswering;
using NEDA.Communication.EmotionalIntelligence.EmotionRecognition;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.NeuralNetwork;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;
using static NEDA.Brain.NLP_Engine.SyntaxAnalysis.GrammarEngine;

namespace NEDA.Communication.EmotionalIntelligence.PersonalityTraits;
{
    /// <summary>
    /// Kişilik özelliklerini yöneten ve analiz eden sistem;
    /// </summary>
    public interface ITraitManager;
    {
        /// <summary>
        /// Kullanıcının kişilik özelliklerini analiz eder;
        /// </summary>
        Task<PersonalityProfile> AnalyzePersonalityAsync(string userId, BehaviorData behaviorData);

        /// <summary>
        /// Belirli bir özellik için skor hesaplar;
        /// </summary>
        Task<TraitScore> CalculateTraitScoreAsync(string userId, string traitId, ContextData context);

        /// <summary>
        /// Kişilik özelliklerini tahmin eder;
        /// </summary>
        Task<List<PredictedTrait>> PredictTraitsAsync(string userId, InteractionData interaction);

        /// <summary>
        /// Özellikler arasındaki korelasyonları analiz eder;
        /// </summary>
        Task<CorrelationMatrix> AnalyzeTraitCorrelationsAsync(string userId);

        /// <summary>
        /// Kişilik modelini günceller;
        /// </summary>
        Task UpdatePersonalityModelAsync(string userId, PersonalityUpdateData updateData);

        /// <summary>
        /// Belirli bir senaryo için kişilik uyumunu değerlendirir;
        /// </summary>
        Task<CompatibilityAssessment> AssessPersonalityCompatibilityAsync(
            string userId,
            Scenario scenario);

        /// <summary>
        /// Kullanıcının kişilik evrimini izler;
        /// </summary>
        Task<PersonalityEvolution> TrackPersonalityEvolutionAsync(string userId, TimeSpan timeRange);

        /// <summary>
        /// Kültürel normlara göre kişilik özelliklerini normalize eder;
        /// </summary>
        Task<NormalizedTraits> NormalizeTraitsByCultureAsync(
            PersonalityProfile profile,
            string cultureCode);

        /// <summary>
        /// Karşılaştırmalı kişilik analizi yapar;
        /// </summary>
        Task<ComparativeAnalysis> ComparePersonalitiesAsync(
            List<string> userIds,
            ComparisonCriteria criteria);

        /// <summary>
        /// Kişilik tabanlı öneriler oluşturur;
        /// </summary>
        Task<PersonalityRecommendations> GenerateRecommendationsAsync(
            string userId,
            RecommendationContext context);
    }

    /// <summary>
    /// Kişilik özellik yöneticisinin ana implementasyonu;
    /// </summary>
    public class TraitManager : ITraitManager, IDisposable;
    {
        private readonly ILogger<TraitManager> _logger;
        private readonly ITraitAnalysisEngine _analysisEngine;
        private readonly INeuralNetworkService _neuralNetwork;
        private readonly IPersonalityRepository _repository;
        private readonly IAuditLogger _auditLogger;
        private readonly IErrorReporter _errorReporter;
        private readonly IMetricsCollector _metricsCollector;
        private readonly TraitManagerConfig _config;

        private readonly ConcurrentDictionary<string, UserTraitCache> _traitCache;
        private readonly TraitModelRegistry _modelRegistry
        private readonly PersonalityValidator _validator;
        private bool _isDisposed;
        private bool _isInitialized;
        private DateTime _lastModelUpdate;

        public TraitManager(
            ILogger<TraitManager> logger,
            ITraitAnalysisEngine analysisEngine,
            INeuralNetworkService neuralNetwork,
            IPersonalityRepository repository,
            IAuditLogger auditLogger,
            IErrorReporter errorReporter,
            IMetricsCollector metricsCollector,
            IOptions<TraitManagerConfig> configOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
            _neuralNetwork = neuralNetwork ?? throw new ArgumentNullException(nameof(neuralNetwork));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _config = configOptions?.Value ?? throw new ArgumentNullException(nameof(configOptions));

            _traitCache = new ConcurrentDictionary<string, UserTraitCache>();
            _modelRegistry = new TraitModelRegistry();
            _validator = new PersonalityValidator();
            _isDisposed = false;
            _isInitialized = false;
            _lastModelUpdate = DateTime.MinValue;

            InitializeModels();

            _logger.LogInformation("TraitManager initialized with {ModelCount} trait models",
                _modelRegistry.ModelCount);
        }

        /// <summary>
        /// Kişilik özelliklerini analiz eder;
        /// </summary>
        public async Task<PersonalityProfile> AnalyzePersonalityAsync(
            string userId,
            BehaviorData behaviorData)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);
            ValidateBehaviorData(behaviorData);

            using (var operation = _metricsCollector.StartOperation("TraitManager.AnalyzePersonality"))
            {
                try
                {
                    _logger.LogDebug("Analyzing personality for user: {UserId}", userId);

                    // Önbellekten kontrol et;
                    if (_traitCache.TryGetValue(userId, out var cachedProfile) &&
                        cachedProfile.IsValidForAnalysis(behaviorData.Timestamp))
                    {
                        _logger.LogDebug("Returning cached personality profile for user: {UserId}", userId);
                        operation.SetTag("cache_hit", true);
                        return cachedProfile.Profile;
                    }

                    operation.SetTag("cache_hit", false);

                    // Davranış verilerini ön işlemeden geçir;
                    var processedData = await PreprocessBehaviorDataAsync(behaviorData);

                    // Çoklu analiz yöntemleri uygula;
                    var analysisResults = await PerformMultiMethodAnalysisAsync(userId, processedData);

                    // Sonuçları birleştir;
                    var combinedProfile = await CombineAnalysisResultsAsync(analysisResults);

                    // Geçerlilik kontrolü yap;
                    await ValidateProfileAsync(combinedProfile);

                    // Kültürel normalizasyon uygula;
                    if (_config.EnableCulturalNormalization)
                    {
                        combinedProfile = await ApplyCulturalNormalizationAsync(combinedProfile, behaviorData.Culture);
                    }

                    // Güven skorunu hesapla;
                    combinedProfile.ConfidenceScore = CalculateConfidenceScore(analysisResults);

                    // Önbelleğe kaydet;
                    await CacheProfileAsync(userId, combinedProfile, behaviorData.Timestamp);

                    // Veritabanına kaydet;
                    await _repository.SavePersonalityProfileAsync(userId, combinedProfile);

                    _logger.LogInformation("Personality analysis completed for user {UserId}. Trait count: {TraitCount}, Confidence: {Confidence}",
                        userId, combinedProfile.Traits.Count, combinedProfile.ConfidenceScore);

                    await _auditLogger.LogPersonalityAnalysisAsync(
                        userId,
                        combinedProfile.Traits.Count,
                        combinedProfile.ConfidenceScore,
                        "Personality analysis completed");

                    // Metrikleri güncelle;
                    _metricsCollector.RecordMetric("personality_analysis_completed", 1);
                    _metricsCollector.RecordMetric("average_confidence", combinedProfile.ConfidenceScore);

                    return combinedProfile;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error analyzing personality for user: {UserId}", userId);

                    await _errorReporter.ReportErrorAsync(
                        new SystemError;
                        {
                            ErrorCode = ErrorCodes.TraitManager.AnalysisFailed,
                            Message = $"Personality analysis failed for user {userId}",
                            Severity = ErrorSeverity.High,
                            Component = "TraitManager",
                            UserId = userId,
                            AdditionalData = new Dictionary<string, object>
                            {
                                { "behavior_data_type", behaviorData.Type },
                                { "data_points", behaviorData.DataPoints.Count }
                            }
                        },
                        ex);

                    throw new TraitAnalysisException(
                        $"Failed to analyze personality for user {userId}",
                        ex,
                        ErrorCodes.TraitManager.AnalysisFailed);
                }
            }
        }

        /// <summary>
        /// Belirli bir özellik için skor hesaplar;
        /// </summary>
        public async Task<TraitScore> CalculateTraitScoreAsync(
            string userId,
            string traitId,
            ContextData context)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);
            ValidateTraitId(traitId);

            try
            {
                _logger.LogDebug("Calculating trait score for user: {UserId}, trait: {TraitId}",
                    userId, traitId);

                // Özellik modelini yükle;
                var traitModel = await LoadTraitModelAsync(traitId);

                // Bağlam verilerini hazırla;
                var preparedContext = await PrepareContextForTraitAsync(context, traitId);

                // Skor hesapla;
                var rawScore = await traitModel.CalculateScoreAsync(preparedContext);

                // Normalize et;
                var normalizedScore = NormalizeTraitScore(rawScore, traitModel);

                // Güven aralığını hesapla;
                var confidenceInterval = await CalculateConfidenceIntervalAsync(
                    traitId,
                    normalizedScore,
                    context);

                var score = new TraitScore;
                {
                    TraitId = traitId,
                    UserId = userId,
                    RawScore = rawScore,
                    NormalizedScore = normalizedScore,
                    Percentile = await CalculatePercentileAsync(traitId, normalizedScore),
                    ConfidenceInterval = confidenceInterval,
                    ContextFactors = ExtractContextFactors(context),
                    CalculatedAt = DateTime.UtcNow,
                    ModelVersion = traitModel.Version;
                };

                _logger.LogDebug("Trait score calculated: {TraitId} = {Score} (Normalized: {Normalized})",
                    traitId, rawScore, normalizedScore);

                return score;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating trait score for user: {UserId}, trait: {TraitId}",
                    userId, traitId);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.ScoreCalculationFailed,
                        Message = $"Trait score calculation failed for trait {traitId}",
                        Severity = ErrorSeverity.Medium,
                        Component = "TraitManager",
                        UserId = userId,
                        TraitId = traitId;
                    },
                    ex);

                throw new TraitCalculationException(
                    $"Failed to calculate score for trait {traitId}",
                    ex,
                    ErrorCodes.TraitManager.ScoreCalculationFailed);
            }
        }

        /// <summary>
        /// Kişilik özelliklerini tahmin eder;
        /// </summary>
        public async Task<List<PredictedTrait>> PredictTraitsAsync(
            string userId,
            InteractionData interaction)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);
            ValidateInteractionData(interaction);

            try
            {
                _logger.LogDebug("Predicting traits for user: {UserId} based on interaction", userId);

                // Etkileşim verilerini özellik vektörüne dönüştür;
                var featureVector = await ExtractFeatureVectorAsync(interaction);

                // Sinir ağı ile tahmin yap;
                var predictions = await _neuralNetwork.PredictTraitsAsync(featureVector);

                // Tahminleri filtrele ve sırala;
                var filteredPredictions = FilterPredictions(predictions, _config.PredictionThreshold);

                // Güven skorlarını hesapla;
                var scoredPredictions = await CalculatePredictionConfidenceAsync(filteredPredictions, interaction);

                // Sonuçları formatla;
                var result = FormatPredictions(scoredPredictions, userId, interaction.Timestamp);

                _logger.LogInformation("Predicted {Count} traits for user {UserId}. Top trait: {TopTrait}",
                    result.Count, userId, result.FirstOrDefault()?.TraitId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error predicting traits for user: {UserId}", userId);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.PredictionFailed,
                        Message = $"Trait prediction failed for user {userId}",
                        Severity = ErrorSeverity.Medium,
                        Component = "TraitManager",
                        UserId = userId;
                    },
                    ex);

                throw new TraitPredictionException(
                    $"Failed to predict traits for user {userId}",
                    ex,
                    ErrorCodes.TraitManager.PredictionFailed);
            }
        }

        /// <summary>
        /// Özellikler arasındaki korelasyonları analiz eder;
        /// </summary>
        public async Task<CorrelationMatrix> AnalyzeTraitCorrelationsAsync(string userId)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Analyzing trait correlations for user: {UserId}", userId);

                // Kullanıcının tüm özellik skorlarını getir;
                var traitScores = await _repository.GetAllTraitScoresAsync(userId);

                if (traitScores.Count < 2)
                {
                    throw new TraitAnalysisException(
                        "Insufficient trait data for correlation analysis",
                        ErrorCodes.TraitManager.InsufficientData);
                }

                // Korelasyon matrisini hesapla;
                var matrix = CalculateCorrelationMatrix(traitScores);

                // İstatistiksel anlamlılık testi yap;
                var significance = await TestStatisticalSignificanceAsync(matrix, traitScores.Count);

                // Desenleri tespit et;
                var patterns = DetectCorrelationPatterns(matrix, significance);

                var result = new CorrelationMatrix;
                {
                    UserId = userId,
                    Matrix = matrix,
                    SignificanceLevels = significance,
                    DetectedPatterns = patterns,
                    AnalysisTimestamp = DateTime.UtcNow,
                    DataPointsUsed = traitScores.Count,
                    Confidence = CalculateCorrelationConfidence(matrix, traitScores.Count)
                };

                _logger.LogInformation("Correlation analysis completed for user {UserId}. Matrix size: {Size}x{Size}",
                    userId, matrix.GetLength(0), matrix.GetLength(1));

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing trait correlations for user: {UserId}", userId);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.CorrelationAnalysisFailed,
                        Message = $"Trait correlation analysis failed for user {userId}",
                        Severity = ErrorSeverity.Low,
                        Component = "TraitManager",
                        UserId = userId;
                    },
                    ex);

                throw new TraitAnalysisException(
                    $"Failed to analyze trait correlations for user {userId}",
                    ex,
                    ErrorCodes.TraitManager.CorrelationAnalysisFailed);
            }
        }

        /// <summary>
        /// Kişilik modelini günceller;
        /// </summary>
        public async Task UpdatePersonalityModelAsync(string userId, PersonalityUpdateData updateData)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);
            ValidateUpdateData(updateData);

            try
            {
                _logger.LogInformation("Updating personality model for user: {UserId}", userId);

                // Mevcut profili getir;
                var currentProfile = await _repository.GetPersonalityProfileAsync(userId);

                if (currentProfile == null)
                {
                    throw new TraitManagementException(
                        $"No existing profile found for user {userId}",
                        ErrorCodes.TraitManager.ProfileNotFound);
                }

                // Güncelleme türüne göre işlem yap;
                switch (updateData.UpdateType)
                {
                    case UpdateType.Incremental:
                        await ApplyIncrementalUpdateAsync(userId, currentProfile, updateData);
                        break;

                    case UpdateType.Correction:
                        await ApplyCorrectionUpdateAsync(userId, currentProfile, updateData);
                        break;

                    case UpdateType.Revision:
                        await ApplyRevisionUpdateAsync(userId, currentProfile, updateData);
                        break;

                    default:
                        throw new TraitManagementException(
                            $"Unknown update type: {updateData.UpdateType}",
                            ErrorCodes.TraitManager.InvalidUpdateType);
                }

                // Önbelleği güncelle;
                await InvalidateCacheAsync(userId);

                // Model versiyonunu artır;
                await IncrementModelVersionAsync(userId);

                _logger.LogInformation("Personality model updated for user {UserId}. New version: {Version}",
                    userId, updateData.NewVersion ?? "Unknown");

                await _auditLogger.LogModelUpdateAsync(
                    userId,
                    "PersonalityModel",
                    updateData.UpdateType.ToString(),
                    updateData.Reason);

                _lastModelUpdate = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating personality model for user: {UserId}", userId);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.ModelUpdateFailed,
                        Message = $"Personality model update failed for user {userId}",
                        Severity = ErrorSeverity.High,
                        Component = "TraitManager",
                        UserId = userId,
                        UpdateType = updateData.UpdateType.ToString()
                    },
                    ex);

                throw new TraitManagementException(
                    $"Failed to update personality model for user {userId}",
                    ex,
                    ErrorCodes.TraitManager.ModelUpdateFailed);
            }
        }

        /// <summary>
        /// Kişilik uyumunu değerlendirir;
        /// </summary>
        public async Task<CompatibilityAssessment> AssessPersonalityCompatibilityAsync(
            string userId,
            Scenario scenario)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);
            ValidateScenario(scenario);

            try
            {
                _logger.LogDebug("Assessing personality compatibility for user: {UserId}, scenario: {ScenarioId}",
                    userId, scenario.Id);

                // Kullanıcının kişilik profilini getir;
                var userProfile = await GetOrAnalyzeProfileAsync(userId);

                // Senaryonun gerektirdiği özellikleri analiz et;
                var requiredTraits = AnalyzeScenarioRequirements(scenario);

                // Uyum skorunu hesapla;
                var compatibilityScores = CalculateCompatibilityScores(userProfile, requiredTraits);

                // Risk faktörlerini değerlendir;
                var riskFactors = await AssessRiskFactorsAsync(userProfile, scenario);

                // Öneriler oluştur;
                var recommendations = GenerateCompatibilityRecommendations(
                    compatibilityScores,
                    riskFactors,
                    scenario);

                var assessment = new CompatibilityAssessment;
                {
                    UserId = userId,
                    ScenarioId = scenario.Id,
                    OverallCompatibility = compatibilityScores.OverallScore,
                    TraitCompatibility = compatibilityScores.TraitScores,
                    RiskFactors = riskFactors,
                    Recommendations = recommendations,
                    AssessmentDate = DateTime.UtcNow,
                    ConfidenceLevel = CalculateAssessmentConfidence(userProfile, scenario)
                };

                _logger.LogInformation("Compatibility assessment completed. Score: {Score}, Risk level: {RiskLevel}",
                    assessment.OverallCompatibility, assessment.RiskFactors.OverallRisk);

                return assessment;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assessing compatibility for user: {UserId}, scenario: {ScenarioId}",
                    userId, scenario.Id);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.CompatibilityAssessmentFailed,
                        Message = $"Compatibility assessment failed for scenario {scenario.Id}",
                        Severity = ErrorSeverity.Medium,
                        Component = "TraitManager",
                        UserId = userId,
                        ScenarioId = scenario.Id;
                    },
                    ex);

                throw new TraitAssessmentException(
                    $"Failed to assess compatibility for scenario {scenario.Id}",
                    ex,
                    ErrorCodes.TraitManager.CompatibilityAssessmentFailed);
            }
        }

        /// <summary>
        /// Kişilik evrimini izler;
        /// </summary>
        public async Task<PersonalityEvolution> TrackPersonalityEvolutionAsync(
            string userId,
            TimeSpan timeRange)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Tracking personality evolution for user: {UserId}, time range: {TimeRange}",
                    userId, timeRange);

                // Zaman aralığındaki tüm profilleri getir;
                var historicalProfiles = await _repository.GetHistoricalProfilesAsync(userId, timeRange);

                if (historicalProfiles.Count < 2)
                {
                    throw new TraitAnalysisException(
                        "Insufficient historical data for evolution tracking",
                        ErrorCodes.TraitManager.InsufficientHistoricalData);
                }

                // Evrim trendlerini analiz et;
                var evolutionTrends = AnalyzeEvolutionTrends(historicalProfiles);

                // Değişim noktalarını tespit et;
                var changePoints = DetectChangePoints(historicalProfiles);

                // Stabilite analizi yap;
                var stabilityAnalysis = AnalyzeStability(historicalProfiles);

                var evolution = new PersonalityEvolution;
                {
                    UserId = userId,
                    TimeRange = timeRange,
                    ProfilesAnalyzed = historicalProfiles.Count,
                    EvolutionTrends = evolutionTrends,
                    SignificantChanges = changePoints,
                    StabilityMetrics = stabilityAnalysis,
                    OverallEvolutionScore = CalculateEvolutionScore(evolutionTrends, stabilityAnalysis),
                    AnalysisTimestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Evolution tracking completed. Profiles analyzed: {Count}, Evolution score: {Score}",
                    historicalProfiles.Count, evolution.OverallEvolutionScore);

                return evolution;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error tracking personality evolution for user: {UserId}", userId);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.EvolutionTrackingFailed,
                        Message = $"Personality evolution tracking failed for user {userId}",
                        Severity = ErrorSeverity.Low,
                        Component = "TraitManager",
                        UserId = userId;
                    },
                    ex);

                throw new TraitAnalysisException(
                    $"Failed to track personality evolution for user {userId}",
                    ex,
                    ErrorCodes.TraitManager.EvolutionTrackingFailed);
            }
        }

        /// <summary>
        /// Özellikleri kültürel normlara göre normalize eder;
        /// </summary>
        public async Task<NormalizedTraits> NormalizeTraitsByCultureAsync(
            PersonalityProfile profile,
            string cultureCode)
        {
            ValidateNotDisposed();
            ValidateProfile(profile);
            ValidateCultureCode(cultureCode);

            try
            {
                _logger.LogDebug("Normalizing traits by culture: {CultureCode}", cultureCode);

                // Kültürel normları getir;
                var culturalNorms = await LoadCulturalNormsAsync(cultureCode);

                // Her özellik için kültürel normalizasyon uygula;
                var normalizedTraits = new Dictionary<string, NormalizedTrait>();

                foreach (var trait in profile.Traits)
                {
                    var culturalContext = culturalNorms.GetTraitContext(trait.Key);
                    var normalized = await ApplyCulturalNormalizationToTraitAsync(trait.Value, culturalContext);
                    normalizedTraits[trait.Key] = normalized;
                }

                // Kültürel uyum skorunu hesapla;
                var culturalFitScore = CalculateCulturalFitScore(normalizedTraits, culturalNorms);

                var result = new NormalizedTraits;
                {
                    OriginalProfile = profile,
                    NormalizedTraits = normalizedTraits,
                    CultureCode = cultureCode,
                    CulturalNormsVersion = culturalNorms.Version,
                    CulturalFitScore = culturalFitScore,
                    NormalizationTimestamp = DateTime.UtcNow,
                    Notes = GenerateNormalizationNotes(normalizedTraits, culturalNorms)
                };

                _logger.LogInformation("Cultural normalization completed. Culture: {Culture}, Fit score: {Score}",
                    cultureCode, culturalFitScore);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error normalizing traits by culture: {CultureCode}", cultureCode);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.CulturalNormalizationFailed,
                        Message = $"Cultural normalization failed for culture {cultureCode}",
                        Severity = ErrorSeverity.Medium,
                        Component = "TraitManager",
                        CultureCode = cultureCode;
                    },
                    ex);

                throw new TraitNormalizationException(
                    $"Failed to normalize traits by culture {cultureCode}",
                    ex,
                    ErrorCodes.TraitManager.CulturalNormalizationFailed);
            }
        }

        /// <summary>
        /// Karşılaştırmalı kişilik analizi yapar;
        /// </summary>
        public async Task<ComparativeAnalysis> ComparePersonalitiesAsync(
            List<string> userIds,
            ComparisonCriteria criteria)
        {
            ValidateNotDisposed();
            ValidateUserIds(userIds);
            ValidateComparisonCriteria(criteria);

            try
            {
                _logger.LogDebug("Comparing personalities for {Count} users", userIds.Count);

                // Tüm kullanıcıların profillerini getir;
                var profiles = new Dictionary<string, PersonalityProfile>();
                foreach (var userId in userIds)
                {
                    profiles[userId] = await GetOrAnalyzeProfileAsync(userId);
                }

                // Karşılaştırma metriklerini hesapla;
                var similarityMatrix = CalculateSimilarityMatrix(profiles);
                var clusteringResults = PerformClusterAnalysis(profiles, criteria.ClusterCount);
                var divergenceMetrics = CalculateDivergenceMetrics(profiles);

                // Gruplandırma analizi yap;
                var groupingAnalysis = AnalyzeGroupings(profiles, criteria);

                var analysis = new ComparativeAnalysis;
                {
                    UserIds = userIds,
                    ComparisonCriteria = criteria,
                    SimilarityMatrix = similarityMatrix,
                    ClusterResults = clusteringResults,
                    DivergenceMetrics = divergenceMetrics,
                    GroupingAnalysis = groupingAnalysis,
                    KeyInsights = ExtractKeyInsights(profiles, similarityMatrix, clusteringResults),
                    AnalysisTimestamp = DateTime.UtcNow,
                    Confidence = CalculateComparisonConfidence(profiles)
                };

                _logger.LogInformation("Comparative analysis completed for {Count} users. Clusters found: {ClusterCount}",
                    userIds.Count, clusteringResults.ClusterCount);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error comparing personalities for {Count} users", userIds.Count);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.ComparativeAnalysisFailed,
                        Message = $"Comparative analysis failed for {userIds.Count} users",
                        Severity = ErrorSeverity.Medium,
                        Component = "TraitManager",
                        UserCount = userIds.Count;
                    },
                    ex);

                throw new TraitAnalysisException(
                    $"Failed to compare personalities for {userIds.Count} users",
                    ex,
                    ErrorCodes.TraitManager.ComparativeAnalysisFailed);
            }
        }

        /// <summary>
        /// Kişilik tabanlı öneriler oluşturur;
        /// </summary>
        public async Task<PersonalityRecommendations> GenerateRecommendationsAsync(
            string userId,
            RecommendationContext context)
        {
            ValidateNotDisposed();
            ValidateUserId(userId);
            ValidateRecommendationContext(context);

            try
            {
                _logger.LogDebug("Generating personality-based recommendations for user: {UserId}", userId);

                // Kullanıcının profilini getir;
                var profile = await GetOrAnalyzeProfileAsync(userId);

                // Bağlamı analiz et;
                var contextAnalysis = AnalyzeRecommendationContext(context);

                // Öneri stratejilerini belirle;
                var strategies = DetermineRecommendationStrategies(profile, contextAnalysis);

                // Öneriler oluştur;
                var recommendations = await CreateRecommendationsAsync(profile, strategies, context);

                // Önceliklendirme yap;
                var prioritizedRecommendations = PrioritizeRecommendations(recommendations, context.Priority);

                // Etkinlik tahmini yap;
                var effectivenessPrediction = await PredictEffectivenessAsync(
                    prioritizedRecommendations,
                    profile,
                    context);

                var result = new PersonalityRecommendations;
                {
                    UserId = userId,
                    Context = context,
                    Recommendations = prioritizedRecommendations,
                    StrategiesUsed = strategies,
                    EffectivenessPrediction = effectivenessPrediction,
                    GeneratedAt = DateTime.UtcNow,
                    ConfidenceScore = CalculateRecommendationConfidence(profile, contextAnalysis)
                };

                _logger.LogInformation("Generated {Count} recommendations for user {UserId}. Top recommendation: {TopRec}",
                    result.Recommendations.Count, userId,
                    result.Recommendations.FirstOrDefault()?.Title);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating recommendations for user: {UserId}", userId);

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.RecommendationGenerationFailed,
                        Message = $"Recommendation generation failed for user {userId}",
                        Severity = ErrorSeverity.Medium,
                        Component = "TraitManager",
                        UserId = userId,
                        ContextType = context.Type;
                    },
                    ex);

                throw new TraitRecommendationException(
                    $"Failed to generate recommendations for user {userId}",
                    ex,
                    ErrorCodes.TraitManager.RecommendationGenerationFailed);
            }
        }

        /// <summary>
        /// Sistemi başlatır;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Initializing TraitManager...");

                // Modelleri yükle;
                await LoadAllTraitModelsAsync();

                // Kültürel normları yükle;
                await LoadCulturalNormsAsync();

                // Önbelleği temizle;
                _traitCache.Clear();

                _isInitialized = true;
                _lastModelUpdate = DateTime.UtcNow;

                _logger.LogInformation("TraitManager initialized successfully");

                await _auditLogger.LogSystemEventAsync(
                    "TraitManager_Initialized",
                    "Trait manager system initialized",
                    SystemEventLevel.Info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize TraitManager");

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.InitializationFailed,
                        Message = "Trait manager initialization failed",
                        Severity = ErrorSeverity.Critical,
                        Component = "TraitManager"
                    },
                    ex);

                throw new TraitManagementException(
                    "Failed to initialize trait manager",
                    ex,
                    ErrorCodes.TraitManager.InitializationFailed);
            }
        }

        /// <summary>
        /// Sistemi kapatır;
        /// </summary>
        public async Task ShutdownAsync()
        {
            try
            {
                _logger.LogInformation("Shutting down TraitManager...");

                // Önbelleği kaydet;
                await SaveCacheToStorageAsync();

                // Modelleri boşalt;
                await UnloadModelsAsync();

                // Kaynakları temizle;
                _traitCache.Clear();
                _modelRegistry.Clear();

                _isInitialized = false;

                _logger.LogInformation("TraitManager shutdown completed");

                await _auditLogger.LogSystemEventAsync(
                    "TraitManager_Shutdown",
                    "Trait manager system shutdown completed",
                    SystemEventLevel.Info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during TraitManager shutdown");

                await _errorReporter.ReportErrorAsync(
                    new SystemError;
                    {
                        ErrorCode = ErrorCodes.TraitManager.ShutdownFailed,
                        Message = "Trait manager shutdown failed",
                        Severity = ErrorSeverity.Critical,
                        Component = "TraitManager"
                    },
                    ex);

                throw new TraitManagementException(
                    "Failed to shutdown trait manager",
                    ex,
                    ErrorCodes.TraitManager.ShutdownFailed);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Senkron shutdown yap;
                    try
                    {
                        ShutdownAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during TraitManager disposal");
                    }

                    _traitCache.Clear();
                    _modelRegistry.Dispose();
                }

                _isDisposed = true;
            }
        }

        #region Private Helper Methods;

        private void InitializeModels()
        {
            // Temel kişilik modellerini kaydet;
            _modelRegistry.RegisterModel(new BigFiveTraitModel());
            _modelRegistry.RegisterModel(new MBTITraitModel());
            _modelRegistry.RegisterModel(new DarkTriadTraitModel());
            _modelRegistry.RegisterModel(new HEXACOTraitModel());
            _modelRegistry.RegisterModel(new CulturalTraitModel());

            // Özel özellik modelleri;
            _modelRegistry.RegisterModel(new ResilienceTraitModel());
            _modelRegistry.RegisterModel(new CreativityTraitModel());
            _modelRegistry.RegisterModel(new LeadershipTraitModel());
            _modelRegistry.RegisterModel(new EmpathyTraitModel());
        }

        private async Task<PersonalityProfile> GetOrAnalyzeProfileAsync(string userId)
        {
            // Önce önbellekten dene;
            if (_traitCache.TryGetValue(userId, out var cached) &&
                !cached.IsExpired(_config.CacheExpiration))
            {
                return cached.Profile;
            }

            // Sonra veritabanından dene;
            var storedProfile = await _repository.GetPersonalityProfileAsync(userId);
            if (storedProfile != null)
            {
                await CacheProfileAsync(userId, storedProfile, DateTime.UtcNow);
                return storedProfile;
            }

            // Yoksa temel analiz yap;
            var basicData = await _repository.GetBasicBehaviorDataAsync(userId);
            if (basicData == null)
            {
                throw new TraitAnalysisException(
                    $"No data available for user {userId}",
                    ErrorCodes.TraitManager.NoDataAvailable);
            }

            return await AnalyzePersonalityAsync(userId, basicData);
        }

        private async Task CacheProfileAsync(string userId, PersonalityProfile profile, DateTime timestamp)
        {
            var cacheEntry = new UserTraitCache(profile, timestamp);
            _traitCache.AddOrUpdate(userId, cacheEntry, (key, existing) => cacheEntry);

            // Cache boyutunu kontrol et;
            if (_traitCache.Count > _config.MaxCacheSize)
            {
                await EvictOldestCacheEntriesAsync(_config.MaxCacheSize / 2);
            }
        }

        private async Task InvalidateCacheAsync(string userId)
        {
            _traitCache.TryRemove(userId, out _);
            await Task.CompletedTask;
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(TraitManager), "TraitManager has been disposed");
            }
        }

        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            }

            if (userId.Length > _config.MaxUserIdLength)
            {
                throw new ArgumentException($"User ID too long. Maximum length is {_config.MaxUserIdLength}",
                    nameof(userId));
            }
        }

        private void ValidateBehaviorData(BehaviorData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data), "Behavior data cannot be null");
            }

            if (data.DataPoints == null || data.DataPoints.Count == 0)
            {
                throw new ArgumentException("Behavior data must contain data points", nameof(data.DataPoints));
            }

            if (data.Timestamp > DateTime.UtcNow.AddHours(1))
            {
                throw new ArgumentException("Behavior data timestamp cannot be in the future", nameof(data.Timestamp));
            }
        }

        private void ValidateTraitId(string traitId)
        {
            if (string.IsNullOrWhiteSpace(traitId))
            {
                throw new ArgumentException("Trait ID cannot be null or empty", nameof(traitId));
            }

            if (!_modelRegistry.ContainsModel(traitId))
            {
                throw new TraitManagementException(
                    $"Unknown trait ID: {traitId}",
                    ErrorCodes.TraitManager.UnknownTrait);
            }
        }

        private void ValidateInteractionData(InteractionData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data), "Interaction data cannot be null");
            }

            if (string.IsNullOrWhiteSpace(data.Type))
            {
                throw new ArgumentException("Interaction type cannot be null or empty", nameof(data.Type));
            }
        }

        private void ValidateUpdateData(PersonalityUpdateData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data), "Update data cannot be null");
            }

            if (string.IsNullOrWhiteSpace(data.Reason))
            {
                throw new ArgumentException("Update reason cannot be null or empty", nameof(data.Reason));
            }

            if (data.Evidence == null || data.Evidence.Count == 0)
            {
                throw new ArgumentException("Update must include evidence", nameof(data.Evidence));
            }
        }

        private void ValidateScenario(Scenario scenario)
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario), "Scenario cannot be null");
            }

            if (string.IsNullOrWhiteSpace(scenario.Id))
            {
                throw new ArgumentException("Scenario ID cannot be null or empty", nameof(scenario.Id));
            }

            if (scenario.Requirements == null || scenario.Requirements.Count == 0)
            {
                throw new ArgumentException("Scenario must include requirements", nameof(scenario.Requirements));
            }
        }

        private void ValidateProfile(PersonalityProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile), "Profile cannot be null");
            }

            if (string.IsNullOrWhiteSpace(profile.UserId))
            {
                throw new ArgumentException("Profile must have a user ID", nameof(profile.UserId));
            }

            if (profile.Traits == null || profile.Traits.Count == 0)
            {
                throw new ArgumentException("Profile must contain traits", nameof(profile.Traits));
            }
        }

        private void ValidateCultureCode(string cultureCode)
        {
            if (string.IsNullOrWhiteSpace(cultureCode))
            {
                throw new ArgumentException("Culture code cannot be null or empty", nameof(cultureCode));
            }

            if (cultureCode.Length != 5) // Örnek: en-US, tr-TR;
            {
                throw new ArgumentException("Culture code must be in format 'xx-XX'", nameof(cultureCode));
            }
        }

        private void ValidateUserIds(List<string> userIds)
        {
            if (userIds == null || userIds.Count == 0)
            {
                throw new ArgumentException("User IDs list cannot be null or empty", nameof(userIds));
            }

            if (userIds.Count > _config.MaxComparisonUsers)
            {
                throw new ArgumentException($"Cannot compare more than {_config.MaxComparisonUsers} users",
                    nameof(userIds));
            }

            foreach (var userId in userIds)
            {
                ValidateUserId(userId);
            }
        }

        private void ValidateComparisonCriteria(ComparisonCriteria criteria)
        {
            if (criteria == null)
            {
                throw new ArgumentNullException(nameof(criteria), "Comparison criteria cannot be null");
            }

            if (criteria.Metrics == null || criteria.Metrics.Count == 0)
            {
                throw new ArgumentException("Comparison criteria must include metrics", nameof(criteria.Metrics));
            }
        }

        private void ValidateRecommendationContext(RecommendationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context), "Recommendation context cannot be null");
            }

            if (string.IsNullOrWhiteSpace(context.Type))
            {
                throw new ArgumentException("Context type cannot be null or empty", nameof(context.Type));
            }

            if (context.Parameters == null)
            {
                throw new ArgumentException("Context parameters cannot be null", nameof(context.Parameters));
            }
        }

        #endregion;

        #region Private Classes;

        private class UserTraitCache;
        {
            public PersonalityProfile Profile { get; }
            public DateTime LastUpdated { get; }
            public DateTime ExpiresAt { get; }

            public UserTraitCache(PersonalityProfile profile, DateTime updated)
            {
                Profile = profile ?? throw new ArgumentNullException(nameof(profile));
                LastUpdated = updated;
                ExpiresAt = updated.AddHours(24); // 24 saat cache ömrü;
            }

            public bool IsValidForAnalysis(DateTime dataTimestamp)
            {
                return LastUpdated >= dataTimestamp && DateTime.UtcNow < ExpiresAt;
            }

            public bool IsExpired(TimeSpan expiration)
            {
                return DateTime.UtcNow > LastUpdated.Add(expiration);
            }
        }

        private class TraitModelRegistry : IDisposable
        {
            private readonly Dictionary<string, ITraitModel> _models;
            private bool _isDisposed;

            public int ModelCount => _models.Count;

            public TraitModelRegistry()
            {
                _models = new Dictionary<string, ITraitModel>();
                _isDisposed = false;
            }

            public void RegisterModel(ITraitModel model)
            {
                if (model == null)
                    throw new ArgumentNullException(nameof(model));

                _models[model.Id] = model;
            }

            public bool ContainsModel(string traitId)
            {
                return _models.ContainsKey(traitId);
            }

            public ITraitModel GetModel(string traitId)
            {
                if (_models.TryGetValue(traitId, out var model))
                {
                    return model;
                }

                throw new KeyNotFoundException($"Trait model not found: {traitId}");
            }

            public void Clear()
            {
                foreach (var model in _models.Values)
                {
                    if (model is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                _models.Clear();
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!_isDisposed)
                {
                    if (disposing)
                    {
                        Clear();
                    }
                    _isDisposed = true;
                }
            }
        }

        private class PersonalityValidator;
        {
            public async Task<bool> ValidateProfileAsync(PersonalityProfile profile)
            {
                // Temel validasyonlar;
                if (profile == null) return false;
                if (profile.Traits == null || profile.Traits.Count == 0) return false;
                if (profile.ConfidenceScore < 0 || profile.ConfidenceScore > 1) return false;

                // Özellik değerlerinin geçerliliğini kontrol et;
                foreach (var trait in profile.Traits)
                {
                    if (!await ValidateTraitValueAsync(trait.Key, trait.Value))
                    {
                        return false;
                    }
                }

                return true;
            }

            private async Task<bool> ValidateTraitValueAsync(string traitId, TraitValue value)
            {
                // Özellik değeri validasyonu;
                // Implementasyon detayları...
                return await Task.FromResult(true);
            }
        }

        #endregion;
    }

    #region Data Models;

    public class TraitManagerConfig;
    {
        public string DefaultCulture { get; set; } = "en-US";
        public int MaxCacheSize { get; set; } = 10000;
        public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromHours(24);
        public float PredictionThreshold { get; set; } = 0.6f;
        public bool EnableCulturalNormalization { get; set; } = true;
        public int MaxUserIdLength { get; set; } = 100;
        public int MaxComparisonUsers { get; set; } = 50;
        public int MinDataPointsForAnalysis { get; set; } = 10;
        public TimeSpan ModelRefreshInterval { get; set; } = TimeSpan.FromDays(7);
        public string TraitModelsDirectory { get; set; } = "Models/Traits/";
        public string CulturalNormsDirectory { get; set; } = "Data/CulturalNorms/";
    }

    public class PersonalityProfile;
    {
        public string UserId { get; set; }
        public Dictionary<string, TraitValue> Traits { get; set; }
        public float ConfidenceScore { get; set; }
        public DateTime AnalyzedAt { get; set; }
        public string ModelVersion { get; set; }
        public CulturalContext Culture { get; set; }
        public List<string> AnalysisMethods { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class TraitScore;
    {
        public string TraitId { get; set; }
        public string UserId { get; set; }
        public float RawScore { get; set; }
        public float NormalizedScore { get; set; }
        public float Percentile { get; set; }
        public ConfidenceInterval ConfidenceInterval { get; set; }
        public Dictionary<string, object> ContextFactors { get; set; }
        public DateTime CalculatedAt { get; set; }
        public string ModelVersion { get; set; }
    }

    public class PredictedTrait;
    {
        public string TraitId { get; set; }
        public float Probability { get; set; }
        public float Confidence { get; set; }
        public List<string> SupportingEvidence { get; set; }
        public DateTime PredictedAt { get; set; }
        public Dictionary<string, float> FeatureContributions { get; set; }
    }

    public class CorrelationMatrix;
    {
        public string UserId { get; set; }
        public double[,] Matrix { get; set; }
        public double[,] SignificanceLevels { get; set; }
        public List<CorrelationPattern> DetectedPatterns { get; set; }
        public DateTime AnalysisTimestamp { get; set; }
        public int DataPointsUsed { get; set; }
        public float Confidence { get; set; }
    }

    public class CompatibilityAssessment;
    {
        public string UserId { get; set; }
        public string ScenarioId { get; set; }
        public float OverallCompatibility { get; set; }
        public Dictionary<string, float> TraitCompatibility { get; set; }
        public RiskAssessment RiskFactors { get; set; }
        public List<CompatibilityRecommendation> Recommendations { get; set; }
        public DateTime AssessmentDate { get; set; }
        public float ConfidenceLevel { get; set; }
    }

    public class PersonalityEvolution;
    {
        public string UserId { get; set; }
        public TimeSpan TimeRange { get; set; }
        public int ProfilesAnalyzed { get; set; }
        public List<EvolutionTrend> EvolutionTrends { get; set; }
        public List<ChangePoint> SignificantChanges { get; set; }
        public StabilityMetrics StabilityMetrics { get; set; }
        public float OverallEvolutionScore { get; set; }
        public DateTime AnalysisTimestamp { get; set; }
    }

    public class NormalizedTraits;
    {
        public PersonalityProfile OriginalProfile { get; set; }
        public Dictionary<string, NormalizedTrait> NormalizedTraits { get; set; }
        public string CultureCode { get; set; }
        public string CulturalNormsVersion { get; set; }
        public float CulturalFitScore { get; set; }
        public DateTime NormalizationTimestamp { get; set; }
        public string Notes { get; set; }
    }

    public class ComparativeAnalysis;
    {
        public List<string> UserIds { get; set; }
        public ComparisonCriteria ComparisonCriteria { get; set; }
        public double[,] SimilarityMatrix { get; set; }
        public ClusterResults ClusterResults { get; set; }
        public DivergenceMetrics DivergenceMetrics { get; set; }
        public GroupingAnalysis GroupingAnalysis { get; set; }
        public List<string> KeyInsights { get; set; }
        public DateTime AnalysisTimestamp { get; set; }
        public float Confidence { get; set; }
    }

    public class PersonalityRecommendations;
    {
        public string UserId { get; set; }
        public RecommendationContext Context { get; set; }
        public List<PersonalityRecommendation> Recommendations { get; set; }
        public List<string> StrategiesUsed { get; set; }
        public EffectivenessPrediction EffectivenessPrediction { get; set; }
        public DateTime GeneratedAt { get; set; }
        public float ConfidenceScore { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class TraitManagementException : Exception
    {
        public string ErrorCode { get; }

        public TraitManagementException(string message) : base(message)
        {
            ErrorCode = ErrorCodes.TraitManager.GeneralError;
        }

        public TraitManagementException(string message, string errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public TraitManagementException(string message, Exception innerException, string errorCode)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    public class TraitAnalysisException : TraitManagementException;
    {
        public TraitAnalysisException(string message) : base(message)
        {
        }

        public TraitAnalysisException(string message, string errorCode) : base(message, errorCode)
        {
        }

        public TraitAnalysisException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    public class TraitCalculationException : TraitManagementException;
    {
        public TraitCalculationException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    public class TraitPredictionException : TraitManagementException;
    {
        public TraitPredictionException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    public class TraitAssessmentException : TraitManagementException;
    {
        public TraitAssessmentException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    public class TraitNormalizationException : TraitManagementException;
    {
        public TraitNormalizationException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    public class TraitRecommendationException : TraitManagementException;
    {
        public TraitRecommendationException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    #endregion;
}

// ErrorCodes.cs (ilgili bölüm)
namespace NEDA.ExceptionHandling.ErrorCodes;
{
    public static class TraitManager;
    {
        public const string GeneralError = "TRAIT_001";
        public const string InitializationFailed = "TRAIT_002";
        public const string ShutdownFailed = "TRAIT_003";
        public const string AnalysisFailed = "TRAIT_101";
        public const string ScoreCalculationFailed = "TRAIT_102";
        public const string PredictionFailed = "TRAIT_103";
        public const string CorrelationAnalysisFailed = "TRAIT_104";
        public const string ModelUpdateFailed = "TRAIT_105";
        public const string CompatibilityAssessmentFailed = "TRAIT_106";
        public const string EvolutionTrackingFailed = "TRAIT_107";
        public const string CulturalNormalizationFailed = "TRAIT_108";
        public const string ComparativeAnalysisFailed = "TRAIT_109";
        public const string RecommendationGenerationFailed = "TRAIT_110";
        public const string UnknownTrait = "TRAIT_201";
        public const string ProfileNotFound = "TRAIT_202";
        public const string InvalidUpdateType = "TRAIT_203";
        public const string InsufficientData = "TRAIT_204";
        public const string InsufficientHistoricalData = "TRAIT_205";
        public const string NoDataAvailable = "TRAIT_206";
    }
}
