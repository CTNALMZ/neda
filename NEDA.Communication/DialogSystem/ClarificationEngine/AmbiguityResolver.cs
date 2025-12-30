using NEDA.Brain.IntentRecognition;
using NEDA.Brain.IntentRecognition.CommandParser;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Communication.DialogSystem.ClarificationEngine;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.Brain.IntentRecognition.IntentDetector;

namespace NEDA.Communication.DialogSystem.ClarificationEngine;
{
    /// <summary>
    /// Handles ambiguity resolution in user queries through clarification dialogs;
    /// Implements advanced NLP techniques to disambiguate user intent;
    /// </summary>
    public class AmbiguityResolver : IAmbiguityResolver;
    {
        private readonly ILogger _logger;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IIntentDetector _intentDetector;
        private readonly IClarificationStrategyProvider _strategyProvider;
        private readonly AmbiguityResolverConfig _config;

        private const int MAX_CLARIFICATION_ATTEMPTS = 3;
        private const double CONFIDENCE_THRESHOLD = 0.7;

        /// <summary>
        /// Initializes a new instance of the AmbiguityResolver class;
        /// </summary>
        public AmbiguityResolver(
            ILogger logger,
            ISemanticAnalyzer semanticAnalyzer,
            IIntentDetector intentDetector,
            IClarificationStrategyProvider strategyProvider,
            AmbiguityResolverConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _intentDetector = intentDetector ?? throw new ArgumentNullException(nameof(intentDetector));
            _strategyProvider = strategyProvider ?? throw new ArgumentNullException(nameof(strategyProvider));
            _config = config ?? AmbiguityResolverConfig.Default;

            _logger.Info("AmbiguityResolver initialized", new { MaxAttempts = MAX_CLARIFICATION_ATTEMPTS, ConfidenceThreshold = CONFIDENCE_THRESHOLD });
        }

        /// <summary>
        /// Resolves ambiguity in a user query through interactive clarification;
        /// </summary>
        /// <param name="query">The ambiguous user query</param>
        /// <param name="context">Current conversation context</param>
        /// <param name="cancellationToken">Cancellation token for async operations</param>
        /// <returns>Resolved intent with confidence score</returns>
        public async Task<ResolvedIntent> ResolveAmbiguityAsync(
            string query,
            ConversationContext context,
            System.Threading.CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.Debug($"Starting ambiguity resolution for query: {query}");

                var analysisResult = await AnalyzeQueryAsync(query, context, cancellationToken);

                if (analysisResult.ConfidenceScore >= CONFIDENCE_THRESHOLD)
                {
                    _logger.Info($"High confidence intent detected: {analysisResult.PrimaryIntent.IntentName} (Score: {analysisResult.ConfidenceScore})");
                    return new ResolvedIntent;
                    {
                        Intent = analysisResult.PrimaryIntent,
                        Confidence = analysisResult.ConfidenceScore,
                        ResolutionMethod = ResolutionMethod.Automatic,
                        ClarificationQuestions = new List<ClarificationQuestion>()
                    };
                }

                return await PerformInteractiveClarificationAsync(query, analysisResult, context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Ambiguity resolution failed for query: {query}", ex);
                throw new AmbiguityResolutionException($"Failed to resolve ambiguity for query: {query}", ex, query);
            }
        }

        /// <summary>
        /// Analyzes query for potential ambiguities and alternative interpretations;
        /// </summary>
        private async Task<AmbiguityAnalysisResult> AnalyzeQueryAsync(
            string query,
            ConversationContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var tasks = new[]
            {
                _semanticAnalyzer.AnalyzeAsync(query, context, cancellationToken),
                _intentDetector.DetectIntentAsync(query, context, cancellationToken)
            };

            await Task.WhenAll(tasks);

            var semanticAnalysis = tasks[0].Result;
            var intentDetection = tasks[1].Result;

            var ambiguityLevel = CalculateAmbiguityLevel(semanticAnalysis, intentDetection);
            var alternativeIntents = ExtractAlternativeIntents(intentDetection);

            return new AmbiguityAnalysisResult;
            {
                Query = query,
                PrimaryIntent = intentDetection.PrimaryIntent,
                ConfidenceScore = intentDetection.Confidence,
                AmbiguityLevel = ambiguityLevel,
                AlternativeIntents = alternativeIntents,
                SemanticFeatures = semanticAnalysis.Features,
                ContextRelevance = CalculateContextRelevance(context, intentDetection.PrimaryIntent)
            };
        }

        /// <summary>
        /// Performs interactive clarification dialog with user;
        /// </summary>
        private async Task<ResolvedIntent> PerformInteractiveClarificationAsync(
            string originalQuery,
            AmbiguityAnalysisResult analysis,
            ConversationContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var clarificationQuestions = new List<ClarificationQuestion>();
            var currentAnalysis = analysis;
            int attempt = 0;

            while (attempt < MAX_CLARIFICATION_ATTEMPTS &&
                   currentAnalysis.ConfidenceScore < CONFIDENCE_THRESHOLD)
            {
                attempt++;
                _logger.Debug($"Clarification attempt {attempt} for query: {originalQuery}");

                var strategy = _strategyProvider.GetClarificationStrategy(currentAnalysis);
                var question = GenerateClarificationQuestion(strategy, currentAnalysis, context);

                clarificationQuestions.Add(question);

                // In real implementation, this would await user response;
                // For now, simulate response based on context;
                var userResponse = await SimulateUserResponseAsync(question, context, cancellationToken);

                // Update analysis with user response;
                currentAnalysis = await UpdateAnalysisWithResponseAsync(
                    originalQuery, currentAnalysis, question, userResponse, context, cancellationToken);

                if (currentAnalysis.ConfidenceScore >= CONFIDENCE_THRESHOLD)
                {
                    break;
                }
            }

            return BuildResolvedIntent(currentAnalysis, clarificationQuestions, attempt);
        }

        /// <summary>
        /// Generates specific clarification question based on strategy;
        /// </summary>
        private ClarificationQuestion GenerateClarificationQuestion(
            ClarificationStrategy strategy,
            AmbiguityAnalysisResult analysis,
            ConversationContext context)
        {
            return strategy.Type switch;
            {
                ClarificationType.DisambiguateEntity => GenerateEntityClarification(strategy, analysis),
                ClarificationType.ConfirmIntent => GenerateIntentConfirmation(strategy, analysis),
                ClarificationType.SpecifyParameter => GenerateParameterSpecification(strategy, analysis),
                ClarificationType.ProvideContext => GenerateContextRequest(strategy, analysis, context),
                _ => GenerateGenericClarification(analysis)
            };
        }

        /// <summary>
        /// Generates entity disambiguation question;
        /// </summary>
        private ClarificationQuestion GenerateEntityClarification(
            ClarificationStrategy strategy,
            AmbiguityAnalysisResult analysis)
        {
            var ambiguousEntities = analysis.SemanticFeatures;
                .Where(f => f.Type == SemanticFeatureType.Entity && f.Confidence < 0.8)
                .Select(f => f.Value)
                .ToList();

            return new ClarificationQuestion;
            {
                QuestionId = Guid.NewGuid().ToString(),
                QuestionText = $"Did you mean {string.Join(" or ", ambiguousEntities.Take(3))}?",
                QuestionType = ClarificationQuestionType.MultipleChoice,
                Options = ambiguousEntities.Take(5).Select(e => new ClarificationOption;
                {
                    OptionId = Guid.NewGuid().ToString(),
                    OptionText = e,
                    ConfidenceBoost = 0.3;
                }).ToList(),
                Strategy = strategy,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Generates intent confirmation question;
        /// </summary>
        private ClarificationQuestion GenerateIntentConfirmation(
            ClarificationStrategy strategy,
            AmbiguityAnalysisResult analysis)
        {
            var topIntents = analysis.AlternativeIntents;
                .Take(3)
                .Select(i => i.IntentName)
                .ToList();

            return new ClarificationQuestion;
            {
                QuestionId = Guid.NewGuid().ToString(),
                QuestionText = $"Are you trying to {analysis.PrimaryIntent.IntentName}, or perhaps {string.Join(" or ", topIntents)}?",
                QuestionType = ClarificationQuestionType.YesNoWithAlternatives,
                Options = new List<ClarificationOption>
                {
                    new ClarificationOption;
                    {
                        OptionId = "yes_primary",
                        OptionText = $"Yes, {analysis.PrimaryIntent.IntentName}",
                        ConfidenceBoost = 0.4;
                    }
                }.Concat(topIntents.Select((intent, index) => new ClarificationOption;
                {
                    OptionId = $"alternative_{index}",
                    OptionText = $"No, {intent}",
                    ConfidenceBoost = 0.35;
                })).ToList(),
                Strategy = strategy,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Updates analysis based on user response;
        /// </summary>
        private async Task<AmbiguityAnalysisResult> UpdateAnalysisWithResponseAsync(
            string originalQuery,
            AmbiguityAnalysisResult currentAnalysis,
            ClarificationQuestion question,
            string userResponse,
            ConversationContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            var responseAnalysis = await _semanticAnalyzer.AnalyzeAsync(userResponse, context, cancellationToken);

            // Apply confidence boosts based on response;
            var updatedConfidence = CalculateUpdatedConfidence(
                currentAnalysis.ConfidenceScore,
                question,
                responseAnalysis);

            var updatedIntent = DetermineUpdatedIntent(
                currentAnalysis.PrimaryIntent,
                question,
                responseAnalysis,
                currentAnalysis.AlternativeIntents);

            return new AmbiguityAnalysisResult;
            {
                Query = originalQuery,
                PrimaryIntent = updatedIntent,
                ConfidenceScore = updatedConfidence,
                AmbiguityLevel = RecalculateAmbiguityLevel(updatedConfidence, responseAnalysis),
                AlternativeIntents = currentAnalysis.AlternativeIntents;
                    .Where(i => i.IntentName != updatedIntent.IntentName)
                    .ToList(),
                SemanticFeatures = MergeFeatures(currentAnalysis.SemanticFeatures, responseAnalysis.Features),
                ContextRelevance = currentAnalysis.ContextRelevance;
            };
        }

        /// <summary>
        /// Calculates updated confidence score based on user response;
        /// </summary>
        private double CalculateUpdatedConfidence(
            double currentConfidence,
            ClarificationQuestion question,
            SemanticAnalysisResult responseAnalysis)
        {
            if (responseAnalysis.Features.Any(f => f.Type == SemanticFeatureType.Confirmation))
            {
                var selectedOption = question.Options.FirstOrDefault(o =>
                    responseAnalysis.Features.Any(f => f.Value.Contains(o.OptionId) ||
                                                     f.Value.Contains(o.OptionText)));

                if (selectedOption != null)
                {
                    return Math.Min(1.0, currentConfidence + selectedOption.ConfidenceBoost);
                }
            }

            return currentConfidence;
        }

        /// <summary>
        /// Determines updated intent based on clarification response;
        /// </summary>
        private DetectedIntent DetermineUpdatedIntent(
            DetectedIntent currentIntent,
            ClarificationQuestion question,
            SemanticAnalysisResult responseAnalysis,
            List<DetectedIntent> alternatives)
        {
            // Check if response indicates a different intent;
            foreach (var alternative in alternatives)
            {
                if (responseAnalysis.Features.Any(f =>
                    f.Value.Contains(alternative.IntentName, StringComparison.OrdinalIgnoreCase)))
                {
                    return alternative;
                }
            }

            return currentIntent;
        }

        /// <summary>
        /// Calculates ambiguity level based on semantic analysis and intent detection;
        /// </summary>
        private AmbiguityLevel CalculateAmbiguityLevel(
            SemanticAnalysisResult semanticAnalysis,
            IntentDetectionResult intentDetection)
        {
            var factors = new List<double>
            {
                1.0 - intentDetection.Confidence,
                semanticAnalysis.Features.Count(f => f.Confidence < 0.7) / (double)Math.Max(1, semanticAnalysis.Features.Count),
                intentDetection.AlternativeIntents.Count > 2 ? 0.3 : 0;
            };

            var score = factors.Average();

            return score switch;
            {
                < 0.3 => AmbiguityLevel.Low,
                < 0.6 => AmbiguityLevel.Medium,
                _ => AmbiguityLevel.High;
            };
        }

        /// <summary>
        /// Extracts alternative intents from detection result;
        /// </summary>
        private List<DetectedIntent> ExtractAlternativeIntents(IntentDetectionResult detection)
        {
            return detection.AlternativeIntents;
                .Where(i => i.Confidence > 0.3)
                .OrderByDescending(i => i.Confidence)
                .Take(5)
                .ToList();
        }

        /// <summary>
        /// Calculates context relevance for intent;
        /// </summary>
        private double CalculateContextRelevance(ConversationContext context, DetectedIntent intent)
        {
            if (context == null || string.IsNullOrEmpty(context.Topic))
                return 0.5;

            // Simple relevance calculation based on topic matching;
            // In production, this would use more sophisticated NLP;
            var topicWords = context.Topic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var intentWords = intent.IntentName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var matches = topicWords.Count(tw =>
                intentWords.Any(iw => iw.Equals(tw, StringComparison.OrdinalIgnoreCase)));

            return matches / (double)Math.Max(1, topicWords.Length);
        }

        /// <summary>
        /// Simulates user response for demonstration purposes;
        /// </summary>
        private Task<string> SimulateUserResponseAsync(
            ClarificationQuestion question,
            ConversationContext context,
            System.Threading.CancellationToken cancellationToken)
        {
            // In production, this would integrate with actual user input;
            // For now, simulate based on context and question type;
            var response = question.QuestionType switch;
            {
                ClarificationQuestionType.YesNo => "Yes",
                ClarificationQuestionType.MultipleChoice => question.Options.FirstOrDefault()?.OptionText ?? "Option 1",
                _ => "I meant the first option"
            };

            return Task.FromResult(response);
        }

        /// <summary>
        /// Builds final resolved intent result;
        /// </summary>
        private ResolvedIntent BuildResolvedIntent(
            AmbiguityAnalysisResult analysis,
            List<ClarificationQuestion> questions,
            int attempts)
        {
            var resolutionMethod = attempts > 0 ? ResolutionMethod.Interactive : ResolutionMethod.Automatic;

            return new ResolvedIntent;
            {
                Intent = analysis.PrimaryIntent,
                Confidence = analysis.ConfidenceScore,
                ResolutionMethod = resolutionMethod,
                ClarificationQuestions = questions,
                AttemptsRequired = attempts,
                Timestamp = DateTime.UtcNow,
                Metadata = new ResolutionMetadata;
                {
                    OriginalAmbiguityLevel = analysis.AmbiguityLevel,
                    FinalAmbiguityLevel = analysis.ConfidenceScore >= CONFIDENCE_THRESHOLD ?
                        AmbiguityLevel.Low : AmbiguityLevel.Medium,
                    StrategyUsed = questions.LastOrDefault()?.Strategy?.Type.ToString() ?? "Automatic",
                    ProcessingTime = TimeSpan.Zero // Would be calculated in real implementation;
                }
            };
        }

        /// <summary>
        /// Merges semantic features from different analyses;
        /// </summary>
        private List<SemanticFeature> MergeFeatures(
            List<SemanticFeature> original,
            List<SemanticFeature> newFeatures)
        {
            var merged = new List<SemanticFeature>(original);

            foreach (var feature in newFeatures)
            {
                var existing = merged.FirstOrDefault(f =>
                    f.Type == feature.Type &&
                    f.Value.Equals(feature.Value, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Confidence = Math.Max(existing.Confidence, feature.Confidence);
                }
                else;
                {
                    merged.Add(feature);
                }
            }

            return merged;
        }

        /// <summary>
        /// Recalculates ambiguity level after clarification;
        /// </summary>
        private AmbiguityLevel RecalculateAmbiguityLevel(double confidence, SemanticAnalysisResult response)
        {
            var ambiguityScore = (1.0 - confidence) * 0.7 +
                               (response.Features.Any(f => f.Type == SemanticFeatureType.Uncertainty) ? 0.3 : 0);

            return ambiguityScore switch;
            {
                < 0.3 => AmbiguityLevel.Low,
                < 0.6 => AmbiguityLevel.Medium,
                _ => AmbiguityLevel.High;
            };
        }

        /// <summary>
        /// Generates generic clarification question as fallback;
        /// </summary>
        private ClarificationQuestion GenerateGenericClarification(AmbiguityAnalysisResult analysis)
        {
            return new ClarificationQuestion;
            {
                QuestionId = Guid.NewGuid().ToString(),
                QuestionText = "Could you please clarify what you meant?",
                QuestionType = ClarificationQuestionType.OpenEnded,
                Strategy = new ClarificationStrategy;
                {
                    Type = ClarificationType.Generic,
                    Priority = ClarificationPriority.Medium;
                },
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Generates parameter specification question;
        /// </summary>
        private ClarificationQuestion GenerateParameterSpecification(
            ClarificationStrategy strategy,
            AmbiguityAnalysisResult analysis)
        {
            var missingParams = analysis.PrimaryIntent.Parameters;
                .Where(p => p.Confidence < 0.6)
                .Select(p => p.Name)
                .ToList();

            return new ClarificationQuestion;
            {
                QuestionId = Guid.NewGuid().ToString(),
                QuestionText = $"Could you specify {string.Join(" and ", missingParams)}?",
                QuestionType = ClarificationQuestionType.ParameterRequest,
                Options = missingParams.Select(p => new ClarificationOption;
                {
                    OptionId = $"param_{p}",
                    OptionText = p,
                    IsParameter = true;
                }).ToList(),
                Strategy = strategy,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Generates context request question;
        /// </summary>
        private ClarificationQuestion GenerateContextRequest(
            ClarificationStrategy strategy,
            AmbiguityAnalysisResult analysis,
            ConversationContext context)
        {
            var contextKeywords = context?.RecentTopics?.Take(3) ?? new List<string>();

            return new ClarificationQuestion;
            {
                QuestionId = Guid.NewGuid().ToString(),
                QuestionText = "Could you provide more context about what you're referring to?",
                QuestionType = ClarificationQuestionType.Contextual,
                Options = contextKeywords.Select((topic, idx) => new ClarificationOption;
                {
                    OptionId = $"context_{idx}",
                    OptionText = topic,
                    ConfidenceBoost = 0.25;
                }).ToList(),
                Strategy = strategy,
                Timestamp = DateTime.UtcNow;
            };
        }
    }

    #region Supporting Types and Interfaces;

    /// <summary>
    /// Interface for ambiguity resolution service;
    /// </summary>
    public interface IAmbiguityResolver;
    {
        Task<ResolvedIntent> ResolveAmbiguityAsync(
            string query,
            ConversationContext context,
            System.Threading.CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of ambiguity resolution process;
    /// </summary>
    public class ResolvedIntent;
    {
        public DetectedIntent Intent { get; set; }
        public double Confidence { get; set; }
        public ResolutionMethod ResolutionMethod { get; set; }
        public List<ClarificationQuestion> ClarificationQuestions { get; set; }
        public int AttemptsRequired { get; set; }
        public DateTime Timestamp { get; set; }
        public ResolutionMetadata Metadata { get; set; }
    }

    /// <summary>
    /// Clarification question for disambiguation;
    /// </summary>
    public class ClarificationQuestion;
    {
        public string QuestionId { get; set; }
        public string QuestionText { get; set; }
        public ClarificationQuestionType QuestionType { get; set; }
        public List<ClarificationOption> Options { get; set; }
        public ClarificationStrategy Strategy { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Option for clarification question;
    /// </summary>
    public class ClarificationOption;
    {
        public string OptionId { get; set; }
        public string OptionText { get; set; }
        public double ConfidenceBoost { get; set; }
        public bool IsParameter { get; set; }
    }

    /// <summary>
    /// Strategy for clarification;
    /// </summary>
    public class ClarificationStrategy;
    {
        public ClarificationType Type { get; set; }
        public ClarificationPriority Priority { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Result of ambiguity analysis;
    /// </summary>
    public class AmbiguityAnalysisResult;
    {
        public string Query { get; set; }
        public DetectedIntent PrimaryIntent { get; set; }
        public double ConfidenceScore { get; set; }
        public AmbiguityLevel AmbiguityLevel { get; set; }
        public List<DetectedIntent> AlternativeIntents { get; set; }
        public List<SemanticFeature> SemanticFeatures { get; set; }
        public double ContextRelevance { get; set; }
    }

    /// <summary>
    /// Metadata about resolution process;
    /// </summary>
    public class ResolutionMetadata;
    {
        public AmbiguityLevel OriginalAmbiguityLevel { get; set; }
        public AmbiguityLevel FinalAmbiguityLevel { get; set; }
        public string StrategyUsed { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    /// <summary>
    /// Configuration for ambiguity resolver;
    /// </summary>
    public class AmbiguityResolverConfig;
    {
        public static AmbiguityResolverConfig Default => new AmbiguityResolverConfig;
        {
            EnableInteractiveClarification = true,
            MaxClarificationAttempts = 3,
            ConfidenceThreshold = 0.7,
            TimeoutSeconds = 30,
            EnableContextAwareness = true;
        };

        public bool EnableInteractiveClarification { get; set; }
        public int MaxClarificationAttempts { get; set; }
        public double ConfidenceThreshold { get; set; }
        public int TimeoutSeconds { get; set; }
        public bool EnableContextAwareness { get; set; }
    }

    /// <summary>
    /// Types of clarification questions;
    /// </summary>
    public enum ClarificationQuestionType;
    {
        YesNo,
        YesNoWithAlternatives,
        MultipleChoice,
        OpenEnded,
        ParameterRequest,
        Contextual;
    }

    /// <summary>
    /// Types of clarification strategies;
    /// </summary>
    public enum ClarificationType;
    {
        DisambiguateEntity,
        ConfirmIntent,
        SpecifyParameter,
        ProvideContext,
        Generic;
    }

    /// <summary>
    /// Priority levels for clarification;
    /// </summary>
    public enum ClarificationPriority;
    {
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// Ambiguity levels;
    /// </summary>
    public enum AmbiguityLevel;
    {
        Low,
        Medium,
        High;
    }

    /// <summary>
    /// Resolution methods;
    /// </summary>
    public enum ResolutionMethod;
    {
        Automatic,
        Interactive,
        Contextual,
        Historical;
    }

    /// <summary>
    /// Custom exception for ambiguity resolution failures;
    /// </summary>
    public class AmbiguityResolutionException : NEDAException;
    {
        public string OriginalQuery { get; }
        public AmbiguityLevel? DetectedAmbiguityLevel { get; set; }

        public AmbiguityResolutionException(string message, Exception innerException, string originalQuery)
            : base($"AMB001: {message}", innerException)
        {
            OriginalQuery = originalQuery;
        }

        public AmbiguityResolutionException(string message, string originalQuery)
            : base($"AMB001: {message}")
        {
            OriginalQuery = originalQuery;
        }
    }

    /// <summary>
    /// Interface for clarification strategy provider;
    /// </summary>
    public interface IClarificationStrategyProvider;
    {
        ClarificationStrategy GetClarificationStrategy(AmbiguityAnalysisResult analysis);
    }

    /// <summary>
    /// Semantic feature for analysis;
    /// </summary>
    public class SemanticFeature;
    {
        public SemanticFeatureType Type { get; set; }
        public string Value { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Types of semantic features;
    /// </summary>
    public enum SemanticFeatureType;
    {
        Entity,
        Intent,
        Parameter,
        Context,
        Confirmation,
        Negation,
        Uncertainty,
        Emotion;
    }

    #endregion;
}
