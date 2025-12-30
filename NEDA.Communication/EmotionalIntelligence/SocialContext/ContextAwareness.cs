using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.ClientSDK;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Communication.DialogSystem;
using NEDA.Communication.EmotionalIntelligence.EmpathyModel;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NEDA.Communication.EmotionalIntelligence.SocialContext;
{
    /// <summary>
    /// Context Type Enumeration;
    /// </summary>
    public enum ContextType;
    {
        Personal = 0,           // One-on-one personal conversation;
        Professional = 1,       // Work or business context;
        Educational = 2,        // Learning or teaching context;
        Technical = 3,          // Technical support or debugging;
        Creative = 4,           // Artistic or creative work;
        Casual = 5,             // Informal social interaction;
        Emergency = 6,          // Crisis or urgent situation;
        Therapeutic = 7,        // Emotional support context;
        Formal = 8,             // Official or ceremonial context;
        Confidential = 9        // Sensitive or private information;
    }

    /// <summary>
    /// Social Relationship Type;
    /// </summary>
    public enum RelationshipType;
    {
        Stranger = 0,           // No prior relationship;
        Acquaintance = 1,       // Casual knowledge of each other;
        Colleague = 2,          // Work or professional relationship;
        Friend = 3,             // Personal friendship;
        Mentor = 4,             // Guidance relationship;
        Student = 5,            // Learning relationship;
        Customer = 6,           // Service provider relationship;
        Family = 7,             // Family relationship (if known)
        Partner = 8,            // Close partnership;
        Authority = 9           // Hierarchical relationship;
    }

    /// <summary>
    /// Cultural Context;
    /// </summary>
    public enum CulturalContext;
    {
        Western = 0,
        Eastern = 1,
        MiddleEastern = 2,
        African = 3,
        LatinAmerican = 4,
        SouthAsian = 5,
        EastAsian = 6,
        Nordic = 7,
        Global = 8              // International/mixed context;
    }

    /// <summary>
    /// Temporal Context;
    /// </summary>
    public enum TemporalContext;
    {
        FirstInteraction = 0,
        EarlyRelationship = 1,
        Established = 2,
        LongTerm = 3,
        Reconnecting = 4;
    }

    /// <summary>
    /// Context Configuration;
    /// </summary>
    public class ContextAwarenessConfig;
    {
        public int ContextWindowSize { get; set; } = 10; // Number of interactions to consider;
        public TimeSpan ContextMemoryDuration { get; set; } = TimeSpan.FromDays(30);
        public double ContextConfidenceThreshold { get; set; } = 0.7;
        public bool EnableRealTimeAdaptation { get; set; } = true;
        public int MaxActiveContexts { get; set; } = 5;
        public Dictionary<ContextType, ContextRules> ContextRules { get; set; }
        public Dictionary<CulturalContext, CulturalNorms> CulturalNorms { get; set; }
        public Dictionary<RelationshipType, RelationshipRules> RelationshipRules { get; set; }
        public List<string> SensitiveTopics { get; set; }
        public List<string> ProfessionalDomains { get; set; }
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(15);
    }

    /// <summary>
    /// Context Rules;
    /// </summary>
    public class ContextRules;
    {
        public double RequiredFormality { get; set; }
        public double AllowedHumorLevel { get; set; }
        public double ExpectedEmpathy { get; set; }
        public double TechnicalDepth { get; set; }
        public List<CommunicationStyle> PreferredStyles { get; set; }
        public List<string> AppropriateTopics { get; set; }
        public List<string> AvoidedTopics { get; set; }
        public TimeSpan ExpectedResponseTime { get; set; }
        public bool AllowPersonalQuestions { get; set; }
        public bool RequireExplicitConsent { get; set; }
    }

    /// <summary>
    /// Cultural Norms;
    /// </summary>
    public class CulturalNorms;
    {
        public double DirectnessLevel { get; set; } // 0=indirect, 1=direct;
        public double FormalityBaseline { get; set; }
        public List<string> GreetingPatterns { get; set; }
        public List<string> FarewellPatterns { get; set; }
        public List<string> PoliteExpressions { get; set; }
        public List<string> TabooTopics { get; set; }
        public Dictionary<string, string> CulturalReferences { get; set; }
        public TimeZoneInfo PrimaryTimeZone { get; set; }
        public List<string> Holidays { get; set; }
        public Dictionary<string, double> CommunicationPreferences { get; set; }
    }

    /// <summary>
    /// Relationship Rules;
    /// </summary>
    public class RelationshipRules;
    {
        public double AppropriateFamiliarity { get; set; }
        public double ExpectedResponsiveness { get; set; }
        public List<string> RelationshipSpecificTopics { get; set; }
        public List<string> BoundaryTopics { get; set; }
        public double EmotionalSupportLevel { get; set; }
        public bool AllowNicknames { get; set; }
        public bool AllowInformalLanguage { get; set; }
        public TimeSpan RelationshipMemoryDuration { get; set; }
    }

    /// <summary>
    /// Context State;
    /// </summary>
    public class ContextState;
    {
        public string ContextId { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }

        // Primary Context;
        public ContextType PrimaryContext { get; set; }
        public double ContextConfidence { get; set; }
        public List<ContextType> SecondaryContexts { get; set; }

        // Relationship Context;
        public RelationshipType Relationship { get; set; }
        public TemporalContext TemporalRelationship { get; set; }
        public double RelationshipStrength { get; set; } // 0-1;

        // Cultural Context;
        public CulturalContext CulturalBackground { get; set; }
        public string Language { get; set; }
        public string Region { get; set; }
        public TimeZoneInfo UserTimeZone { get; set; }

        // Environmental Context;
        public string Platform { get; set; } // e.g., "web", "mobile", "desktop"
        public string DeviceType { get; set; }
        public string LocationHint { get; set; }
        public DateTime LocalTime { get; set; }

        // Conversation Context;
        public string CurrentTopic { get; set; }
        public List<string> TopicHistory { get; set; }
        public EmotionalContext EmotionalState { get; set; }
        public UrgencyLevel Urgency { get; set; }
        public PrivacyLevel Privacy { get; set; }

        // Behavioral Context;
        public List<InteractionPattern> InteractionPatterns { get; set; }
        public Dictionary<string, double> UserPreferences { get; set; }
        public List<string> KnownSensitivities { get; set; }

        // Metadata;
        public Dictionary<string, object> Metadata { get; set; }

        public bool IsSensitiveContext =>
            PrimaryContext == ContextType.Confidential ||
            PrimaryContext == ContextType.Therapeutic ||
            Privacy == PrivacyLevel.High;

        public bool RequiresFormality =>
            PrimaryContext == ContextType.Professional ||
            PrimaryContext == ContextType.Formal ||
            Relationship == RelationshipType.Authority;
    }

    /// <summary>
    /// Interaction Pattern;
    /// </summary>
    public class InteractionPattern;
    {
        public string PatternId { get; set; }
        public string PatternType { get; set; } // e.g., "greeting", "question", "complaint"
        public DateTime FirstObserved { get; set; }
        public DateTime LastObserved { get; set; }
        public int OccurrenceCount { get; set; }
        public double SuccessRate { get; set; }
        public Dictionary<string, object> PatternData { get; set; }
    }

    /// <summary>
    /// Emotional Context;
    /// </summary>
    public class EmotionalContext;
    {
        public string Emotion { get; set; } // Primary emotion;
        public double Intensity { get; set; } // 0-1;
        public double Valence { get; set; } // -1 to 1 (negative to positive)
        public double Arousal { get; set; } // 0-1 (calm to excited)
        public List<string> SecondaryEmotions { get; set; }
        public string Trigger { get; set; }
        public DateTime DetectedAt { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Context Analysis Result;
    /// </summary>
    public class ContextAnalysis;
    {
        public string AnalysisId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }

        public ContextState CurrentContext { get; set; }
        public List<ContextShift> DetectedShifts { get; set; }
        public List<ContextViolation> DetectedViolations { get; set; }
        public List<ContextOpportunity> IdentifiedOpportunities { get; set; }

        public Dictionary<ContextType, double> ContextProbabilities { get; set; }
        public Dictionary<string, double> FeatureWeights { get; set; }

        public RecommendedAdjustments Adjustments { get; set; }
        public double OverallConfidence { get; set; }

        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Context Shift;
    /// </summary>
    public class ContextShift;
    {
        public ContextType FromContext { get; set; }
        public ContextType ToContext { get; set; }
        public DateTime DetectedAt { get; set; }
        public string Trigger { get; set; }
        public double ShiftMagnitude { get; set; }
        public bool WasSmooth { get; set; }
        public List<string> SupportingEvidence { get; set; }
    }

    /// <summary>
    /// Context Violation;
    /// </summary>
    public class ContextViolation;
    {
        public string ViolationType { get; set; }
        public string Description { get; set; }
        public ContextType AffectedContext { get; set; }
        public DateTime DetectedAt { get; set; }
        public SeverityLevel Severity { get; set; }
        public string SuggestedCorrection { get; set; }
        public Dictionary<string, object> ViolationData { get; set; }
    }

    /// <summary>
    /// Context Opportunity;
    /// </summary>
    public class ContextOpportunity;
    {
        public string OpportunityType { get; set; }
        public string Description { get; set; }
        public ContextType TargetContext { get; set; }
        public DateTime IdentifiedAt { get; set; }
        public double PotentialBenefit { get; set; }
        public double ImplementationEffort { get; set; }
        public List<string> SuggestedActions { get; set; }
    }

    /// <summary>
    /// Recommended Adjustments;
    /// </summary>
    public class RecommendedAdjustments;
    {
        public CommunicationStyle? SuggestedStyle { get; set; }
        public double? FormalityAdjustment { get; set; }
        public double? EmpathyAdjustment { get; set; }
        public double? TechnicalDepthAdjustment { get; set; }
        public HumorStyle? HumorAdjustment { get; set; }
        public List<string> TopicsToEmphasize { get; set; }
        public List<string> TopicsToAvoid { get; set; }
        public TimeSpan? ResponseTimeTarget { get; set; }
        public Dictionary<string, object> AdditionalAdjustments { get; set; }
    }

    /// <summary>
    /// Context Update Request;
    /// </summary>
    public class ContextUpdateRequest;
    {
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public string InputText { get; set; }
        public Dictionary<string, object> EnvironmentalData { get; set; }
        public List<InteractionEvent> RecentEvents { get; set; }
        public UserProfile UserProfile { get; set; }
        public bool ForceReanalysis { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }
    }

    /// <summary>
    /// Interaction Event;
    /// </summary>
    public class InteractionEvent;
    {
        public string EventId { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; }
        public string Content { get; set; }
        public string Sender { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public EmotionalContext EmotionalContent { get; set; }
    }

    /// <summary>
    /// Context Awareness Interface;
    /// </summary>
    public interface IContextAwareness;
    {
        Task<ContextAnalysis> AnalyzeContextAsync(ContextUpdateRequest request);
        Task<ContextState> GetCurrentContextAsync(string userId, string sessionId = null);
        Task<ContextState> UpdateContextAsync(ContextUpdateRequest request);
        Task<bool> SaveContextAsync(ContextState context);

        Task<List<ContextViolation>> CheckContextComplianceAsync(string userId, ProposedResponse response);
        Task<RecommendedAdjustments> GetContextualAdjustmentsAsync(string userId, string sessionId);
        Task<List<ContextShift>> GetContextHistoryAsync(string userId, TimeSpan? lookbackPeriod = null);

        Task<double> CalculateContextSimilarityAsync(ContextState context1, ContextState context2);
        Task<ContextType> PredictNextContextAsync(string userId, string currentTopic);
        Task<bool> DetectContextDriftAsync(string userId, TimeSpan window);

        Task TrainContextModelAsync(string userId, List<InteractionEvent> trainingData);
        Task ResetContextAsync(string userId, string sessionId = null);

        Task<Dictionary<string, double>> ExtractContextFeaturesAsync(string text, Dictionary<string, object> metadata);
        Task<ContextRules> GetContextRulesAsync(ContextType contextType);
        Task<CulturalNorms> GetCulturalNormsAsync(CulturalContext culturalContext);
    }

    /// <summary>
    /// Context Awareness Implementation;
    /// </summary>
    public class ContextAwareness : IContextAwareness;
    {
        private readonly ILogger<ContextAwareness> _logger;
        private readonly IMemoryCache _cache;
        private readonly ILongTermMemory _longTermMemory;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly ICompassionModel _compassionModel;
        private readonly ContextAwarenessConfig _config;

        private readonly Dictionary<string, ContextState> _activeContexts;
        private readonly Dictionary<string, ContextModel> _contextModels;
        private readonly object _contextLock = new object();

        private const string ContextCachePrefix = "context_state_";
        private const string AnalysisCachePrefix = "context_analysis_";
        private const string ModelCachePrefix = "context_model_";

        private static readonly Regex _sensitivePatterns = new Regex(
            @"\b(confidential|secret|private|sensitive|personal|health|financial|legal)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _professionalPatterns = new Regex(
            @"\b(meeting|deadline|project|report|client|business|work|job|career)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _emotionalPatterns = new Regex(
            @"\b(sad|happy|angry|frustrated|excited|worried|anxious|stressed|overwhelmed)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Context Model for user/relationship;
        /// </summary>
        private class ContextModel;
        {
            public string UserId { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastTrained { get; set; }
            public int TrainingSamples { get; set; }

            public Dictionary<ContextType, double> BaseProbabilities { get; set; }
            public Dictionary<string, FeatureWeights> FeatureWeights { get; set; }
            public Dictionary<ContextType, TransitionMatrix> TransitionMatrices { get; set; }
            public Dictionary<string, PatternRecognizer> PatternRecognizers { get; set; }

            public double OverallAccuracy { get; set; }
            public Dictionary<ContextType, double> TypeAccuracies { get; set; }
            public List<PredictionError> RecentErrors { get; set; }
        }

        /// <summary>
        /// Feature Weights for context prediction;
        /// </summary>
        private class FeatureWeights;
        {
            public string FeatureName { get; set; }
            public Dictionary<ContextType, double> Weights { get; set; }
            public double Importance { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        /// <summary>
        /// Context Transition Matrix;
        /// </summary>
        private class TransitionMatrix;
        {
            public ContextType FromContext { get; set; }
            public Dictionary<ContextType, double> TransitionProbabilities { get; set; }
            public int TotalTransitions { get; set; }
            public Dictionary<string, double> TriggerProbabilities { get; set; }
        }

        /// <summary>
        /// Pattern Recognizer;
        /// </summary>
        private class PatternRecognizer;
        {
            public string PatternType { get; set; }
            public List<string> Triggers { get; set; }
            public Dictionary<ContextType, double> ContextAssociations { get; set; }
            public double ConfidenceThreshold { get; set; }
            public int MatchCount { get; set; }
        }

        /// <summary>
        /// Prediction Error;
        /// </summary>
        private class PredictionError;
        {
            public DateTime Timestamp { get; set; }
            public ContextType Predicted { get; set; }
            public ContextType Actual { get; set; }
            public double Confidence { get; set; }
            public string InputText { get; set; }
            public string ErrorType { get; set; }
        }

        /// <summary>
        /// Constructor;
        /// </summary>
        public ContextAwareness(
            ILogger<ContextAwareness> logger,
            IMemoryCache cache,
            ILongTermMemory longTermMemory,
            IShortTermMemory shortTermMemory,
            ISemanticAnalyzer semanticAnalyzer,
            IKnowledgeGraph knowledgeGraph,
            IMetricsCollector metricsCollector,
            IEventBus eventBus,
            ICompassionModel compassionModel,
            IOptions<ContextAwarenessConfig> configOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _compassionModel = compassionModel ?? throw new ArgumentNullException(nameof(compassionModel));
            _config = configOptions?.Value ?? CreateDefaultConfig();

            _activeContexts = new Dictionary<string, ContextState>();
            _contextModels = new Dictionary<string, ContextModel>();

            InitializeContextRules();
            InitializeCulturalNorms();
            SubscribeToEvents();

            _logger.LogInformation("ContextAwareness initialized with {RuleCount} context rules",
                _config.ContextRules?.Count ?? 0);
        }

        /// <summary>
        /// Analyze context from update request;
        /// </summary>
        public async Task<ContextAnalysis> AnalyzeContextAsync(ContextUpdateRequest request)
        {
            try
            {
                ValidateRequest(request);

                var cacheKey = $"{AnalysisCachePrefix}{request.UserId}_{request.SessionId}_{DateTime.UtcNow:yyyyMMddHHmm}";
                if (_cache.TryGetValue(cacheKey, out ContextAnalysis cachedAnalysis) && !request.ForceReanalysis)
                {
                    _logger.LogDebug("Returning cached context analysis for user {UserId}", request.UserId);
                    return cachedAnalysis;
                }

                // Get or create current context;
                var currentContext = await GetOrCreateContextAsync(request.UserId, request.SessionId);

                // Extract features from input;
                var features = await ExtractContextFeaturesAsync(request.InputText, request.EnvironmentalData);

                // Update context with new information;
                var updatedContext = await UpdateContextWithFeaturesAsync(currentContext, features, request);

                // Analyze context shifts;
                var shifts = await DetectContextShiftsAsync(currentContext, updatedContext, request);

                // Check for context violations;
                var violations = await CheckForViolationsAsync(updatedContext, request);

                // Identify opportunities;
                var opportunities = await IdentifyOpportunitiesAsync(updatedContext, request);

                // Calculate context probabilities;
                var probabilities = await CalculateContextProbabilitiesAsync(updatedContext, features);

                // Generate recommendations;
                var adjustments = await GenerateAdjustmentsAsync(updatedContext, shifts, violations);

                // Create analysis result;
                var analysis = new ContextAnalysis;
                {
                    AnalysisId = Guid.NewGuid().ToString(),
                    UserId = request.UserId,
                    Timestamp = DateTime.UtcNow,
                    CurrentContext = updatedContext,
                    DetectedShifts = shifts,
                    DetectedViolations = violations,
                    IdentifiedOpportunities = opportunities,
                    ContextProbabilities = probabilities,
                    FeatureWeights = await ExtractFeatureWeightsAsync(request.UserId),
                    Adjustments = adjustments,
                    OverallConfidence = CalculateOverallConfidence(probabilities, features),
                    Metadata = new Dictionary<string, object>
                    {
                        ["input_length"] = request.InputText?.Length ?? 0,
                        ["feature_count"] = features.Count,
                        ["analysis_duration_ms"] = 0 // Would be calculated in real implementation;
                    }
                };

                // Update context model;
                await UpdateContextModelAsync(request.UserId, currentContext.PrimaryContext,
                    updatedContext.PrimaryContext, features, request.InputText);

                // Save updated context;
                await SaveContextAsync(updatedContext);

                // Cache analysis;
                _cache.Set(cacheKey, analysis, TimeSpan.FromMinutes(5));

                // Publish analysis event;
                await PublishContextAnalysisEventAsync(analysis, request);

                // Record metrics;
                await RecordAnalysisMetricsAsync(analysis, request);

                _logger.LogInformation("Context analysis completed for user {UserId}, primary context: {Context}",
                    request.UserId, updatedContext.PrimaryContext);

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing context for user {UserId}", request?.UserId);
                throw new ContextAwarenessException($"Failed to analyze context for user {request?.UserId}", ex);
            }
        }

        /// <summary>
        /// Get current context for user;
        /// </summary>
        public async Task<ContextState> GetCurrentContextAsync(string userId, string sessionId = null)
        {
            try
            {
                ValidateUserId(userId);

                var contextKey = GetContextKey(userId, sessionId);
                var cacheKey = $"{ContextCachePrefix}{contextKey}";

                // Check cache;
                if (_cache.TryGetValue(cacheKey, out ContextState cachedContext))
                {
                    return cachedContext;
                }

                // Check active contexts;
                lock (_contextLock)
                {
                    if (_activeContexts.TryGetValue(contextKey, out var activeContext))
                    {
                        _cache.Set(cacheKey, activeContext, _config.CacheDuration);
                        return activeContext;
                    }
                }

                // Load from storage;
                var storedContext = await LoadContextFromStorageAsync(userId, sessionId);
                if (storedContext != null)
                {
                    lock (_contextLock)
                    {
                        _activeContexts[contextKey] = storedContext;
                    }
                    _cache.Set(cacheKey, storedContext, _config.CacheDuration);
                    return storedContext;
                }

                // Create default context;
                var defaultContext = await CreateDefaultContextAsync(userId, sessionId);

                lock (_contextLock)
                {
                    _activeContexts[contextKey] = defaultContext;
                }

                _cache.Set(cacheKey, defaultContext, _config.CacheDuration);

                return defaultContext;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current context for user {UserId}", userId);
                throw new ContextAwarenessException($"Failed to get context for user {userId}", ex);
            }
        }

        /// <summary>
        /// Update context with new request;
        /// </summary>
        public async Task<ContextState> UpdateContextAsync(ContextUpdateRequest request)
        {
            try
            {
                var analysis = await AnalyzeContextAsync(request);
                return analysis.CurrentContext;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating context for user {UserId}", request?.UserId);
                throw new ContextAwarenessException($"Failed to update context for user {request?.UserId}", ex);
            }
        }

        /// <summary>
        /// Save context to storage;
        /// </summary>
        public async Task<bool> SaveContextAsync(ContextState context)
        {
            try
            {
                ValidateContext(context);

                var contextKey = GetContextKey(context.UserId, context.SessionId);

                // Update timestamp;
                context.LastUpdated = DateTime.UtcNow;

                // Store in long-term memory;
                var storageKey = $"context_state_{contextKey}";
                await _longTermMemory.StoreAsync(storageKey, context, _config.ContextMemoryDuration);

                // Update active contexts;
                lock (_contextLock)
                {
                    _activeContexts[contextKey] = context;
                }

                // Update cache;
                var cacheKey = $"{ContextCachePrefix}{contextKey}";
                _cache.Set(cacheKey, context, _config.CacheDuration);

                // Store in short-term memory for quick access;
                await _shortTermMemory.StoreAsync($"active_context_{contextKey}", context, TimeSpan.FromHours(1));

                _logger.LogDebug("Saved context for user {UserId}, session {SessionId}",
                    context.UserId, context.SessionId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving context for user {UserId}", context?.UserId);
                throw new ContextAwarenessException($"Failed to save context for user {context?.UserId}", ex);
            }
        }

        /// <summary>
        /// Check context compliance for proposed response;
        /// </summary>
        public async Task<List<ContextViolation>> CheckContextComplianceAsync(string userId, ProposedResponse response)
        {
            try
            {
                var context = await GetCurrentContextAsync(userId);
                var violations = new List<ContextViolation>();

                // Check against context rules;
                var rules = await GetContextRulesAsync(context.PrimaryContext);
                if (rules != null)
                {
                    violations.AddRange(CheckRuleCompliance(context, response, rules));
                }

                // Check against cultural norms;
                var norms = await GetCulturalNormsAsync(context.CulturalBackground);
                if (norms != null)
                {
                    violations.AddRange(CheckCulturalCompliance(context, response, norms));
                }

                // Check for sensitive topics;
                violations.AddRange(CheckSensitiveTopics(context, response));

                // Check emotional appropriateness;
                violations.AddRange(await CheckEmotionalAppropriatenessAsync(context, response));

                // Check relationship boundaries;
                violations.AddRange(CheckRelationshipBoundaries(context, response));

                return violations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking context compliance for user {UserId}", userId);
                return new List<ContextViolation>
                {
                    new ContextViolation;
                    {
                        ViolationType = "SystemError",
                        Description = "Failed to perform compliance check",
                        Severity = SeverityLevel.Low,
                        SuggestedCorrection = "Proceed with caution"
                    }
                };
            }
        }

        /// <summary>
        /// Get contextual adjustments for response;
        /// </summary>
        public async Task<RecommendedAdjustments> GetContextualAdjustmentsAsync(string userId, string sessionId)
        {
            try
            {
                var context = await GetCurrentContextAsync(userId, sessionId);
                var rules = await GetContextRulesAsync(context.PrimaryContext);

                if (rules == null)
                    return new RecommendedAdjustments();

                var adjustments = new RecommendedAdjustments;
                {
                    FormalityAdjustment = rules.RequiredFormality,
                    EmpathyAdjustment = rules.ExpectedEmpathy,
                    TechnicalDepthAdjustment = rules.TechnicalDepth,
                    HumorAdjustment = rules.AllowedHumorLevel > 0.5 ? HumorStyle.Light : HumorStyle.None,
                    ResponseTimeTarget = rules.ExpectedResponseTime,
                    TopicsToAvoid = rules.AvoidedTopics,
                    TopicsToEmphasize = rules.AppropriateTopics?.Take(3).ToList()
                };

                // Set suggested style based on preferences;
                if (rules.PreferredStyles?.Any() == true)
                {
                    adjustments.SuggestedStyle = rules.PreferredStyles.First();
                }

                // Add context-specific adjustments;
                adjustments.AdditionalAdjustments = new Dictionary<string, object>
                {
                    ["requires_consent"] = rules.RequireExplicitConsent,
                    ["allow_personal_questions"] = rules.AllowPersonalQuestions,
                    ["context_type"] = context.PrimaryContext.ToString(),
                    ["relationship_strength"] = context.RelationshipStrength;
                };

                return adjustments;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contextual adjustments for user {UserId}", userId);
                return new RecommendedAdjustments();
            }
        }

        /// <summary>
        /// Get context history;
        /// </summary>
        public async Task<List<ContextShift>> GetContextHistoryAsync(string userId, TimeSpan? lookbackPeriod = null)
        {
            try
            {
                var period = lookbackPeriod ?? TimeSpan.FromDays(7);
                var startTime = DateTime.UtcNow - period;

                var pattern = $"context_shift_{userId}_*";
                var shifts = await _longTermMemory.SearchAsync<ContextShift>(pattern);

                return shifts;
                    .Where(s => s.DetectedAt >= startTime)
                    .OrderByDescending(s => s.DetectedAt)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting context history for user {UserId}", userId);
                return new List<ContextShift>();
            }
        }

        /// <summary>
        /// Calculate context similarity;
        /// </summary>
        public async Task<double> CalculateContextSimilarityAsync(ContextState context1, ContextState context2)
        {
            try
            {
                var similarities = new List<double>();

                // Context type similarity;
                similarities.Add(context1.PrimaryContext == context2.PrimaryContext ? 1.0 : 0.0);

                // Relationship similarity;
                similarities.Add(context1.Relationship == context2.Relationship ? 1.0 : 0.0);

                // Cultural similarity;
                similarities.Add(context1.CulturalBackground == context2.CulturalBackground ? 1.0 : 0.5);

                // Topic similarity (if topics exist)
                if (!string.IsNullOrEmpty(context1.CurrentTopic) && !string.IsNullOrEmpty(context2.CurrentTopic))
                {
                    var topicSimilarity = await CalculateTopicSimilarityAsync(context1.CurrentTopic, context2.CurrentTopic);
                    similarities.Add(topicSimilarity);
                }

                // Emotional similarity;
                if (context1.EmotionalState != null && context2.EmotionalState != null)
                {
                    var emotionalSimilarity = CalculateEmotionalSimilarity(context1.EmotionalState, context2.EmotionalState);
                    similarities.Add(emotionalSimilarity);
                }

                // Weighted average;
                var weights = new[] { 0.3, 0.2, 0.2, 0.2, 0.1 }; // Adjust weights as needed;
                var weightedSum = 0.0;
                var weightSum = 0.0;

                for (int i = 0; i < Math.Min(similarities.Count, weights.Length); i++)
                {
                    weightedSum += similarities[i] * weights[i];
                    weightSum += weights[i];
                }

                return weightSum > 0 ? weightedSum / weightSum : 0.0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating context similarity");
                return 0.0;
            }
        }

        /// <summary>
        /// Predict next context;
        /// </summary>
        public async Task<ContextType> PredictNextContextAsync(string userId, string currentTopic)
        {
            try
            {
                var model = await GetOrCreateContextModelAsync(userId);
                var currentContext = await GetCurrentContextAsync(userId);

                if (model.TransitionMatrices.TryGetValue(currentContext.PrimaryContext, out var matrix))
                {
                    // Get most probable transition;
                    var nextContext = matrix.TransitionProbabilities;
                        .OrderByDescending(kv => kv.Value)
                        .FirstOrDefault();

                    if (nextContext.Value > 0.3) // Threshold;
                    {
                        return nextContext.Key;
                    }
                }

                // Fallback: analyze topic;
                return await PredictContextFromTopicAsync(currentTopic);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error predicting next context for user {UserId}", userId);
                return ContextType.Personal; // Default fallback;
            }
        }

        /// <summary>
        /// Detect context drift;
        /// </summary>
        public async Task<bool> DetectContextDriftAsync(string userId, TimeSpan window)
        {
            try
            {
                var recentShifts = await GetContextHistoryAsync(userId, window);
                if (recentShifts.Count < 3)
                    return false;

                // Calculate drift metrics;
                var shiftMagnitudes = recentShifts.Select(s => s.ShiftMagnitude).ToList();
                var averageMagnitude = shiftMagnitudes.Average();
                var magnitudeVariance = shiftMagnitudes.Select(m => Math.Pow(m - averageMagnitude, 2)).Average();

                // Check for abnormal drift;
                var driftThreshold = 0.5; // Configurable;
                var varianceThreshold = 0.1;

                return averageMagnitude > driftThreshold || magnitudeVariance > varianceThreshold;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting context drift for user {UserId}", userId);
                return false;
            }
        }

        /// <summary>
        /// Train context model;
        /// </summary>
        public async Task TrainContextModelAsync(string userId, List<InteractionEvent> trainingData)
        {
            try
            {
                var model = await GetOrCreateContextModelAsync(userId);

                if (trainingData == null || !trainingData.Any())
                    return;

                foreach (var eventData in trainingData)
                {
                    // Extract features from event;
                    var features = await ExtractContextFeaturesAsync(eventData.Content, eventData.Metadata);

                    // Determine actual context from event;
                    var actualContext = DetermineContextFromEvent(eventData);

                    // Update model with this sample;
                    await UpdateModelWithSampleAsync(model, features, actualContext, eventData.Content);
                }

                model.LastTrained = DateTime.UtcNow;
                model.TrainingSamples += trainingData.Count;

                // Save updated model;
                await SaveContextModelAsync(userId, model);

                _logger.LogInformation("Trained context model for user {UserId} with {Count} samples",
                    userId, trainingData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error training context model for user {UserId}", userId);
            }
        }

        /// <summary>
        /// Reset context;
        /// </summary>
        public async Task ResetContextAsync(string userId, string sessionId = null)
        {
            try
            {
                var contextKey = GetContextKey(userId, sessionId);

                // Remove from active contexts;
                lock (_contextLock)
                {
                    _activeContexts.Remove(contextKey);
                }

                // Clear cache;
                var cacheKey = $"{ContextCachePrefix}{contextKey}";
                _cache.Remove(cacheKey);

                // Clear from short-term memory;
                await _shortTermMemory.DeleteAsync($"active_context_{contextKey}");

                // Create new default context;
                var newContext = await CreateDefaultContextAsync(userId, sessionId);
                await SaveContextAsync(newContext);

                _logger.LogInformation("Reset context for user {UserId}, session {SessionId}", userId, sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting context for user {UserId}", userId);
                throw new ContextAwarenessException($"Failed to reset context for user {userId}", ex);
            }
        }

        /// <summary>
        /// Extract context features from text;
        /// </summary>
        public async Task<Dictionary<string, double>> ExtractContextFeaturesAsync(string text, Dictionary<string, object> metadata)
        {
            var features = new Dictionary<string, double>();

            try
            {
                if (string.IsNullOrWhiteSpace(text))
                    return features;

                var textLower = text.ToLowerInvariant();

                // 1. Length-based features;
                features["text_length"] = NormalizeFeature(text.Length, 0, 1000);
                features["word_count"] = NormalizeFeature(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 0, 200);

                // 2. Content-based features;
                features["question_present"] = text.Contains('?') ? 1.0 : 0.0;
                features["exclamation_present"] = text.Contains('!') ? 1.0 : 0.0;

                // 3. Context indicators;
                features["sensitive_content"] = _sensitivePatterns.IsMatch(textLower) ? 1.0 : 0.0;
                features["professional_content"] = _professionalPatterns.IsMatch(textLower) ? 1.0 : 0.0;
                features["emotional_content"] = _emotionalPatterns.IsMatch(textLower) ? 1.0 : 0.0;

                // 4. Semantic features (if analyzer available)
                if (_semanticAnalyzer != null)
                {
                    try
                    {
                        var semanticFeatures = await _semanticAnalyzer.ExtractFeaturesAsync(text);
                        foreach (var kv in semanticFeatures)
                        {
                            features[$"semantic_{kv.Key}"] = kv.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to extract semantic features");
                    }
                }

                // 5. Metadata features;
                if (metadata != null)
                {
                    foreach (var kv in metadata)
                    {
                        features[$"meta_{kv.Key}"] = Convert.ToDouble(kv.Value);
                    }
                }

                // 6. Temporal features;
                var now = DateTime.UtcNow;
                features["hour_of_day"] = now.Hour / 23.0;
                features["day_of_week"] = ((int)now.DayOfWeek) / 6.0;

                // 7. Sentiment features (simplified)
                var positiveWords = new[] { "good", "great", "excellent", "happy", "thanks", "helpful" };
                var negativeWords = new[] { "bad", "poor", "terrible", "sad", "angry", "frustrated" };

                var positiveCount = positiveWords.Count(w => textLower.Contains(w));
                var negativeCount = negativeWords.Count(w => textLower.Contains(w));

                features["sentiment_bias"] = NormalizeFeature(positiveCount - negativeCount, -5, 5);

                return features;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting context features from text");
                return features;
            }
        }

        /// <summary>
        /// Get context rules for type;
        /// </summary>
        public Task<ContextRules> GetContextRulesAsync(ContextType contextType)
        {
            if (_config.ContextRules != null && _config.ContextRules.TryGetValue(contextType, out var rules))
            {
                return Task.FromResult(rules);
            }

            // Return default rules;
            return Task.FromResult(CreateDefaultRules(contextType));
        }

        /// <summary>
        /// Get cultural norms;
        /// </summary>
        public Task<CulturalNorms> GetCulturalNormsAsync(CulturalContext culturalContext)
        {
            if (_config.CulturalNorms != null && _config.CulturalNorms.TryGetValue(culturalContext, out var norms))
            {
                return Task.FromResult(norms);
            }

            // Return default/global norms;
            return Task.FromResult(CreateDefaultNorms(culturalContext));
        }

        #region Private Methods;

        /// <summary>
        /// Initialize default configuration;
        /// </summary>
        private ContextAwarenessConfig CreateDefaultConfig()
        {
            return new ContextAwarenessConfig;
            {
                ContextWindowSize = 10,
                ContextMemoryDuration = TimeSpan.FromDays(30),
                ContextConfidenceThreshold = 0.7,
                EnableRealTimeAdaptation = true,
                MaxActiveContexts = 5,
                CacheDuration = TimeSpan.FromMinutes(15),
                SensitiveTopics = new List<string>
                {
                    "health issues", "financial problems", "legal matters",
                    "personal relationships", "political views", "religious beliefs"
                },
                ProfessionalDomains = new List<string>
                {
                    "technology", "business", "education", "healthcare",
                    "finance", "engineering", "science", "arts"
                }
            };
        }

        /// <summary>
        /// Initialize context rules;
        /// </summary>
        private void InitializeContextRules()
        {
            if (_config.ContextRules == null)
            {
                _config.ContextRules = new Dictionary<ContextType, ContextRules>
                {
                    [ContextType.Personal] = new ContextRules;
                    {
                        RequiredFormality = 0.2,
                        AllowedHumorLevel = 0.8,
                        ExpectedEmpathy = 0.9,
                        TechnicalDepth = 0.1,
                        PreferredStyles = new List<CommunicationStyle> { CommunicationStyle.Personal, CommunicationStyle.Intuitive },
                        AppropriateTopics = new List<string> { "hobbies", "interests", "daily life", "feelings" },
                        AvoidedTopics = new List<string> { "confidential work information", "highly technical details" },
                        ExpectedResponseTime = TimeSpan.FromSeconds(5),
                        AllowPersonalQuestions = true,
                        RequireExplicitConsent = false;
                    },

                    [ContextType.Professional] = new ContextRules;
                    {
                        RequiredFormality = 0.8,
                        AllowedHumorLevel = 0.3,
                        ExpectedEmpathy = 0.5,
                        TechnicalDepth = 0.7,
                        PreferredStyles = new List<CommunicationStyle> { CommunicationStyle.Analytical, CommunicationStyle.Functional },
                        AppropriateTopics = new List<string> { "work projects", "business goals", "professional development" },
                        AvoidedTopics = new List<string> { "personal relationships", "political opinions", "gossip" },
                        ExpectedResponseTime = TimeSpan.FromSeconds(10),
                        AllowPersonalQuestions = false,
                        RequireExplicitConsent = true;
                    },

                    [ContextType.Emergency] = new ContextRules;
                    {
                        RequiredFormality = 0.9,
                        AllowedHumorLevel = 0.0,
                        ExpectedEmpathy = 1.0,
                        TechnicalDepth = 0.0,
                        PreferredStyles = new List<CommunicationStyle> { CommunicationStyle.Functional, CommunicationStyle.Personal },
                        AppropriateTopics = new List<string> { "immediate needs", "safety concerns", "urgent assistance" },
                        AvoidedTopics = new List<string> { "unrelated topics", "complex explanations" },
                        ExpectedResponseTime = TimeSpan.FromSeconds(2),
                        AllowPersonalQuestions = false,
                        RequireExplicitConsent = false;
                    },

                    // Additional context rules would be defined here...
                };
            }
        }

        /// <summary>
        /// Initialize cultural norms;
        /// </summary>
        private void InitializeCulturalNorms()
        {
            if (_config.CulturalNorms == null)
            {
                _config.CulturalNorms = new Dictionary<CulturalContext, CulturalNorms>
                {
                    [CulturalContext.Western] = new CulturalNorms;
                    {
                        DirectnessLevel = 0.8,
                        FormalityBaseline = 0.4,
                        GreetingPatterns = new List<string> { "Hello", "Hi", "Hey" },
                        FarewellPatterns = new List<string> { "Goodbye", "See you", "Take care" },
                        PoliteExpressions = new List<string> { "Please", "Thank you", "Excuse me" },
                        TabooTopics = new List<string> { "salary", "age", "weight" },
                        CommunicationPreferences = new Dictionary<string, double>
                        {
                            ["direct"] = 0.8,
                            ["concise"] = 0.7,
                            ["friendly"] = 0.6;
                        }
                    },

                    [CulturalContext.Eastern] = new CulturalNorms;
                    {
                        DirectnessLevel = 0.3,
                        FormalityBaseline = 0.7,
                        GreetingPatterns = new List<string> { "您好", "こんにちは", "안녕하세요" },
                        FarewellPatterns = new List<string> { "再见", "さようなら", "안녕히 가세요" },
                        PoliteExpressions = new List<string> { "请", "お願いします", "부탁합니다" },
                        TabooTopics = new List<string> { "family problems", "personal failures", "political criticism" },
                        CommunicationPreferences = new Dictionary<string, double>
                        {
                            ["indirect"] = 0.8,
                            ["respectful"] = 0.9,
                            ["harmonious"] = 0.8;
                        }
                    },

                    [CulturalContext.Global] = new CulturalNorms;
                    {
                        DirectnessLevel = 0.5,
                        FormalityBaseline = 0.5,
                        GreetingPatterns = new List<string> { "Hello", "Hi", "Greetings" },
                        FarewellPatterns = new List<string> { "Goodbye", "Farewell", "Until next time" },
                        PoliteExpressions = new List<string> { "Please", "Thank you", "Sorry" },
                        TabooTopics = new List<string>(),
                        CommunicationPreferences = new Dictionary<string, double>
                        {
                            ["clear"] = 0.7,
                            ["polite"] = 0.8,
                            ["universal"] = 0.6;
                        }
                    }
                };
            }
        }

        /// <summary>
        /// Subscribe to events;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<InteractionCompletedEvent>(HandleInteractionCompleted);
            _eventBus.Subscribe<UserProfileUpdatedEvent>(HandleUserProfileUpdated);
            _eventBus.Subscribe<EmotionalStateChangedEvent>(HandleEmotionalStateChanged);
        }

        /// <summary>
        /// Validate request;
        /// </summary>
        private void ValidateRequest(ContextUpdateRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ValidateUserId(request.UserId);

            if (string.IsNullOrWhiteSpace(request.InputText) &&
                (request.RecentEvents == null || !request.RecentEvents.Any()))
            {
                throw new ArgumentException("Request must contain either input text or recent events");
            }
        }

        /// <summary>
        /// Validate user ID;
        /// </summary>
        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));
        }

        /// <summary>
        /// Validate context;
        /// </summary>
        private void ValidateContext(ContextState context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            ValidateUserId(context.UserId);

            if (!Enum.IsDefined(typeof(ContextType), context.PrimaryContext))
                throw new ArgumentException($"Invalid context type: {context.PrimaryContext}");

            if (context.ContextConfidence < 0.0 || context.ContextConfidence > 1.0)
                throw new ArgumentException($"Context confidence must be between 0 and 1, got {context.ContextConfidence}");
        }

        /// <summary>
        /// Get context key;
        /// </summary>
        private string GetContextKey(string userId, string sessionId)
        {
            return string.IsNullOrEmpty(sessionId) ? userId : $"{userId}_{sessionId}";
        }

        /// <summary>
        /// Get or create context;
        /// </summary>
        private async Task<ContextState> GetOrCreateContextAsync(string userId, string sessionId)
        {
            var existingContext = await GetCurrentContextAsync(userId, sessionId);
            if (existingContext != null)
                return existingContext;

            return await CreateDefaultContextAsync(userId, sessionId);
        }

        /// <summary>
        /// Create default context;
        /// </summary>
        private async Task<ContextState> CreateDefaultContextAsync(string userId, string sessionId)
        {
            // Try to infer from user profile if available;
            var userProfile = await LoadUserProfileAsync(userId);

            return new ContextState;
            {
                ContextId = Guid.NewGuid().ToString(),
                UserId = userId,
                SessionId = sessionId,
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                PrimaryContext = ContextType.Personal,
                ContextConfidence = 0.5,
                SecondaryContexts = new List<ContextType>(),
                Relationship = RelationshipType.Stranger,
                TemporalRelationship = TemporalContext.FirstInteraction,
                RelationshipStrength = 0.1,
                CulturalBackground = CulturalContext.Global,
                Language = "en",
                Region = "International",
                Platform = "unknown",
                DeviceType = "unknown",
                LocalTime = DateTime.UtcNow,
                CurrentTopic = "introduction",
                TopicHistory = new List<string>(),
                EmotionalState = new EmotionalContext;
                {
                    Emotion = "neutral",
                    Intensity = 0.1,
                    Valence = 0.0,
                    Arousal = 0.1,
                    Confidence = 0.5;
                },
                Urgency = UrgencyLevel.Low,
                Privacy = PrivacyLevel.Normal,
                InteractionPatterns = new List<InteractionPattern>(),
                UserPreferences = new Dictionary<string, double>(),
                KnownSensitivities = new List<string>(),
                Metadata = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Create default rules;
        /// </summary>
        private ContextRules CreateDefaultRules(ContextType contextType)
        {
            return new ContextRules;
            {
                RequiredFormality = 0.5,
                AllowedHumorLevel = 0.5,
                ExpectedEmpathy = 0.7,
                TechnicalDepth = 0.5,
                PreferredStyles = new List<CommunicationStyle>
                {
                    CommunicationStyle.Personal,
                    CommunicationStyle.Functional;
                },
                AppropriateTopics = new List<string>(),
                AvoidedTopics = _config.SensitiveTopics ?? new List<string>(),
                ExpectedResponseTime = TimeSpan.FromSeconds(5),
                AllowPersonalQuestions = contextType == ContextType.Personal,
                RequireExplicitConsent = contextType == ContextType.Professional ||
                                       contextType == ContextType.Confidential;
            };
        }

        /// <summary>
        /// Create default norms;
        /// </summary>
        private CulturalNorms CreateDefaultNorms(CulturalContext culturalContext)
        {
            return new CulturalNorms;
            {
                DirectnessLevel = 0.5,
                FormalityBaseline = 0.5,
                GreetingPatterns = new List<string> { "Hello", "Hi" },
                FarewellPatterns = new List<string> { "Goodbye", "See you" },
                PoliteExpressions = new List<string> { "Please", "Thank you" },
                TabooTopics = new List<string>(),
                CommunicationPreferences = new Dictionary<string, double>
                {
                    ["clear"] = 0.8,
                    ["respectful"] = 0.7;
                }
            };
        }

        /// <summary>
        /// Update context with features;
        /// </summary>
        private async Task<ContextState> UpdateContextWithFeaturesAsync(
            ContextState currentContext,
            Dictionary<string, double> features,
            ContextUpdateRequest request)
        {
            var updatedContext = CloneContext(currentContext);
            updatedContext.LastUpdated = DateTime.UtcNow;

            // Update primary context based on features;
            var newContext = await DeterminePrimaryContextAsync(features, request);
            if (newContext != currentContext.PrimaryContext)
            {
                updatedContext.SecondaryContexts.Add(currentContext.PrimaryContext);
                updatedContext.PrimaryContext = newContext;
            }

            // Update topic;
            if (!string.IsNullOrEmpty(request.InputText))
            {
                var newTopic = await ExtractTopicAsync(request.InputText);
                if (!string.IsNullOrEmpty(newTopic) && newTopic != updatedContext.CurrentTopic)
                {
                    updatedContext.TopicHistory.Add(updatedContext.CurrentTopic);
                    if (updatedContext.TopicHistory.Count > 10)
                        updatedContext.TopicHistory.RemoveAt(0);

                    updatedContext.CurrentTopic = newTopic;
                }
            }

            // Update emotional state;
            if (features.TryGetValue("emotional_content", out var emotionalScore) && emotionalScore > 0.3)
            {
                updatedContext.EmotionalState = await AnalyzeEmotionalStateAsync(request.InputText);
            }

            // Update relationship strength;
            if (request.RecentEvents?.Any() == true)
            {
                updatedContext.RelationshipStrength = CalculateUpdatedRelationshipStrength(
                    currentContext.RelationshipStrength,
                    request.RecentEvents.Count,
                    request.RecentEvents.Average(e => CalculateEventEngagement(e))
                );
            }

            // Update temporal relationship;
            updatedContext.TemporalRelationship = DetermineTemporalRelationship(
                updatedContext.RelationshipStrength,
                request.RecentEvents?.Count ?? 0;
            );

            // Update metadata;
            updatedContext.Metadata["last_analysis"] = DateTime.UtcNow;
            updatedContext.Metadata["feature_count"] = features.Count;
            updatedContext.Metadata["input_length"] = request.InputText?.Length ?? 0;

            return updatedContext;
        }

        /// <summary>
        /// Determine primary context from features;
        /// </summary>
        private async Task<ContextType> DeterminePrimaryContextAsync(
            Dictionary<string, double> features,
            ContextUpdateRequest request)
        {
            var scores = new Dictionary<ContextType, double>();

            foreach (ContextType contextType in Enum.GetValues(typeof(ContextType)))
            {
                scores[contextType] = await CalculateContextScoreAsync(contextType, features, request);
            }

            var topContext = scores.OrderByDescending(kv => kv.Value).First();

            return topContext.Value >= _config.ContextConfidenceThreshold;
                ? topContext.Key;
                : ContextType.Personal; // Default fallback;
        }

        /// <summary>
        /// Calculate context score;
        /// </summary>
        private async Task<double> CalculateContextScoreAsync(
            ContextType contextType,
            Dictionary<string, double> features,
            ContextUpdateRequest request)
        {
            var score = 0.0;
            var weightSum = 0.0;

            // Feature-based scoring;
            if (features.TryGetValue("professional_content", out var professionalScore))
            {
                var weight = contextType == ContextType.Professional ? 0.8 : 0.2;
                score += professionalScore * weight;
                weightSum += weight;
            }

            if (features.TryGetValue("sensitive_content", out var sensitiveScore))
            {
                var weight = contextType == ContextType.Confidential ? 0.9 : 0.1;
                score += sensitiveScore * weight;
                weightSum += weight;
            }

            if (features.TryGetValue("emotional_content", out var emotionalScore))
            {
                var weight = contextType == ContextType.Therapeutic ? 0.9 :
                            contextType == ContextType.Personal ? 0.7 : 0.2;
                score += emotionalScore * weight;
                weightSum += weight;
            }

            // User profile consideration;
            if (request.UserProfile != null)
            {
                var profileWeight = 0.3;
                var profileScore = CalculateProfileContextScore(contextType, request.UserProfile);
                score += profileScore * profileWeight;
                weightSum += profileWeight;
            }

            // Environmental considerations;
            if (request.EnvironmentalData != null)
            {
                var envWeight = 0.2;
                var envScore = CalculateEnvironmentalContextScore(contextType, request.EnvironmentalData);
                score += envScore * envWeight;
                weightSum += envWeight;
            }

            return weightSum > 0 ? score / weightSum : 0.0;
        }

        /// <summary>
        /// Normalize feature value;
        /// </summary>
        private double NormalizeFeature(double value, double min, double max)
        {
            if (max <= min) return 0.0;
            var normalized = (value - min) / (max - min);
            return Math.Clamp(normalized, 0.0, 1.0);
        }

        /// <summary>
        /// Clone context object;
        /// </summary>
        private ContextState CloneContext(ContextState source)
        {
            // Simple clone using serialization;
            var json = JsonSerializer.Serialize(source);
            return JsonSerializer.Deserialize<ContextState>(json);
        }

        /// <summary>
        /// Load context from storage;
        /// </summary>
        private async Task<ContextState> LoadContextFromStorageAsync(string userId, string sessionId)
        {
            try
            {
                var key = $"context_state_{GetContextKey(userId, sessionId)}";
                return await _longTermMemory.RetrieveAsync<ContextState>(key);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Load user profile;
        /// </summary>
        private async Task<UserProfile> LoadUserProfileAsync(string userId)
        {
            try
            {
                var key = $"user_profile_{userId}";
                return await _longTermMemory.RetrieveAsync<UserProfile>(key);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract topic from text;
        /// </summary>
        private async Task<string> ExtractTopicAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "unknown";

            // Simple topic extraction - in production would use NLP;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return "unknown";

            // Look for keywords;
            var topicKeywords = new[]
            {
                "work", "project", "job", "career",
                "family", "friend", "relationship",
                "health", "exercise", "diet",
                "money", "finance", "budget",
                "hobby", "sport", "game",
                "problem", "issue", "help"
            };

            foreach (var keyword in topicKeywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return keyword;
            }

            // Use first meaningful word as topic;
            var stopWords = new[] { "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for" };
            var firstWord = words.FirstOrDefault(w => !stopWords.Contains(w.ToLower()));

            return !string.IsNullOrEmpty(firstWord) ? firstWord.ToLower() : "general";
        }

        /// <summary>
        /// Analyze emotional state;
        /// </summary>
        private async Task<EmotionalContext> AnalyzeEmotionalStateAsync(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                    return new EmotionalContext { Emotion = "neutral", Intensity = 0.1 };

                // Simplified emotion detection;
                var emotionalWords = new Dictionary<string, string[]>
                {
                    ["happy"] = new[] { "happy", "glad", "excited", "joy", "great" },
                    ["sad"] = new[] { "sad", "upset", "depressed", "unhappy", "sorry" },
                    ["angry"] = new[] { "angry", "mad", "furious", "annoyed", "frustrated" },
                    ["worried"] = new[] { "worried", "anxious", "nervous", "concerned", "stressed" },
                    ["surprised"] = new[] { "surprised", "amazed", "shocked", "astonished" }
                };

                var textLower = text.ToLower();
                var detectedEmotions = new Dictionary<string, int>();

                foreach (var emotion in emotionalWords)
                {
                    var count = emotion.Value.Count(word => textLower.Contains(word));
                    if (count > 0)
                        detectedEmotions[emotion.Key] = count;
                }

                if (detectedEmotions.Any())
                {
                    var primaryEmotion = detectedEmotions.OrderByDescending(kv => kv.Value).First();
                    var intensity = Math.Clamp(primaryEmotion.Value / 5.0, 0.1, 1.0);

                    return new EmotionalContext;
                    {
                        Emotion = primaryEmotion.Key,
                        Intensity = intensity,
                        Valence = primaryEmotion.Key == "happy" ? 0.8 :
                                 primaryEmotion.Key == "sad" ? -0.8 : 0.0,
                        Arousal = primaryEmotion.Key == "excited" ? 0.9 :
                                 primaryEmotion.Key == "calm" ? 0.1 : 0.5,
                        DetectedAt = DateTime.UtcNow,
                        Confidence = 0.7;
                    };
                }

                return new EmotionalContext { Emotion = "neutral", Intensity = 0.1 };
            }
            catch
            {
                return new EmotionalContext { Emotion = "neutral", Intensity = 0.1 };
            }
        }

        // Additional helper methods would continue...

        #endregion;

        #region Event Handlers;

        private async Task HandleInteractionCompleted(InteractionCompletedEvent @event)
        {
            try
            {
                var request = new ContextUpdateRequest;
                {
                    UserId = @event.UserId,
                    SessionId = @event.SessionId,
                    InputText = @event.UserInput,
                    EnvironmentalData = @event.EnvironmentalData,
                    RecentEvents = new List<InteractionEvent>
                    {
                        new InteractionEvent;
                        {
                            EventId = @event.InteractionId,
                            Timestamp = @event.Timestamp,
                            EventType = "interaction_completed",
                            Content = @event.UserInput,
                            Sender = "user",
                            Metadata = @event.Metadata;
                        }
                    }
                };

                await AnalyzeContextAsync(request);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling interaction completed event");
            }
        }

        private async Task HandleUserProfileUpdated(UserProfileUpdatedEvent @event)
        {
            try
            {
                // Update context with new profile information;
                var context = await GetCurrentContextAsync(@event.UserId);
                if (context != null)
                {
                    context.CulturalBackground = @event.Profile.CulturalBackground;
                    context.Language = @event.Profile.PreferredLanguage;
                    context.Region = @event.Profile.Region;
                    context.KnownSensitivities = @event.Profile.Sensitivities;

                    await SaveContextAsync(context);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling user profile updated event");
            }
        }

        private async Task HandleEmotionalStateChanged(EmotionalStateChangedEvent @event)
        {
            try
            {
                var context = await GetCurrentContextAsync(@event.UserId);
                if (context != null)
                {
                    context.EmotionalState = @event.NewState;
                    await SaveContextAsync(context);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling emotional state changed event");
            }
        }

        #endregion;

        #region Event Publishing;

        private async Task PublishContextAnalysisEventAsync(ContextAnalysis analysis, ContextUpdateRequest request)
        {
            var @event = new ContextAnalysisCompletedEvent;
            {
                AnalysisId = analysis.AnalysisId,
                UserId = analysis.UserId,
                Timestamp = analysis.Timestamp,
                PrimaryContext = analysis.CurrentContext.PrimaryContext,
                ContextConfidence = analysis.OverallConfidence,
                DetectedShifts = analysis.DetectedShifts.Count,
                DetectedViolations = analysis.DetectedViolations.Count,
                AdjustmentRecommendations = analysis.Adjustments != null;
            };

            await _eventBus.PublishAsync(@event);
        }

        #endregion;

        #region Metrics Recording;

        private async Task RecordAnalysisMetricsAsync(ContextAnalysis analysis, ContextUpdateRequest request)
        {
            var metrics = new Dictionary<string, object>
            {
                ["user_id"] = analysis.UserId,
                ["primary_context"] = analysis.CurrentContext.PrimaryContext.ToString(),
                ["context_confidence"] = analysis.OverallConfidence,
                ["detected_shifts"] = analysis.DetectedShifts.Count,
                ["detected_violations"] = analysis.DetectedViolations.Count,
                ["feature_count"] = analysis.FeatureWeights?.Count ?? 0,
                ["input_length"] = request.InputText?.Length ?? 0;
            };

            await _metricsCollector.RecordMetricAsync("context_analysis", metrics);
        }

        #endregion;
    }

    #region Supporting Classes;

    public class ProposedResponse;
    {
        public string ResponseText { get; set; }
        public CommunicationStyle Style { get; set; }
        public double FormalityLevel { get; set; }
        public double EmpathyLevel { get; set; }
        public bool IncludesHumor { get; set; }
        public Dictionary<string, object> ResponseMetadata { get; set; }
    }

    public class UserProfile;
    {
        public string UserId { get; set; }
        public CulturalContext CulturalBackground { get; set; }
        public string PreferredLanguage { get; set; }
        public string Region { get; set; }
        public List<string> Sensitivities { get; set; }
        public Dictionary<string, object> Preferences { get; set; }
    }

    public enum UrgencyLevel { Low, Medium, High, Critical }
    public enum PrivacyLevel { Public, Normal, High, Confidential }
    public enum SeverityLevel { Info, Low, Medium, High, Critical }

    #endregion;

    #region Event Definitions;

    public class InteractionCompletedEvent : IEvent;
    {
        public string InteractionId { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserInput { get; set; }
        public string AssistantResponse { get; set; }
        public Dictionary<string, object> EnvironmentalData { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class UserProfileUpdatedEvent : IEvent;
    {
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public UserProfile Profile { get; set; }
        public string UpdateSource { get; set; }
    }

    public class EmotionalStateChangedEvent : IEvent;
    {
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public EmotionalContext NewState { get; set; }
        public EmotionalContext PreviousState { get; set; }
        public string Trigger { get; set; }
    }

    public class ContextAnalysisCompletedEvent : IEvent;
    {
        public string AnalysisId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public ContextType PrimaryContext { get; set; }
        public double ContextConfidence { get; set; }
        public int DetectedShifts { get; set; }
        public int DetectedViolations { get; set; }
        public bool AdjustmentRecommendations { get; set; }
    }

    #endregion;

    #region Exception Classes;

    public class ContextAwarenessException : Exception
    {
        public ContextAwarenessException(string message) : base(message) { }
        public ContextAwarenessException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Dependency Injection Extension;

    public static class ContextAwarenessExtensions;
    {
        public static IServiceCollection AddContextAwareness(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<ContextAwarenessConfig>(configuration.GetSection("ContextAwareness"));

            services.AddSingleton<IContextAwareness, ContextAwareness>();

            // Register memory cache if not already registered;
            if (!services.Any(x => x.ServiceType == typeof(IMemoryCache)))
            {
                services.AddMemoryCache();
            }

            return services;
        }
    }

    #endregion;

    #region Configuration Example;

    /*
    {
      "ContextAwareness": {
        "ContextWindowSize": 10,
        "ContextMemoryDuration": "30.00:00:00",
        "ContextConfidenceThreshold": 0.7,
        "EnableRealTimeAdaptation": true,
        "MaxActiveContexts": 5,
        "CacheDuration": "00:15:00",
        "SensitiveTopics": [
          "health issues",
          "financial problems", 
          "legal matters",
          "personal relationships",
          "political views",
          "religious beliefs"
        ],
        "ProfessionalDomains": [
          "technology",
          "business", 
          "education",
          "healthcare",
          "finance",
          "engineering"
        ]
      }
    }
    */

    #endregion;
}
