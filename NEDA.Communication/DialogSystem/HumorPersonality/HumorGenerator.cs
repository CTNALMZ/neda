using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Communication.DialogSystem.ClarificationEngine;
using NEDA.Communication.DialogSystem.HumorPersonality;
using NEDA.Communication.EmotionalIntelligence;
using NEDA.Communication.EmotionalIntelligence.EmotionRecognition;
using NEDA.Communication.MultiModalCommunication.ContextAwareResponse;
using NEDA.Communication.MultiModalCommunication.CulturalAdaptation;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Interface.UserProfileManager;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static NEDA.Communication.EmotionalIntelligence.EmotionRecognition.EmotionDetector;

namespace NEDA.Communication.DialogSystem.HumorPersonality;
{
    /// <summary>
    /// Advanced humor generation engine with contextual awareness, cultural adaptation,
    /// and personality-based humor styles. Supports multiple humor types and timing control.
    /// </summary>
    public class HumorGenerator : IHumorGenerator, IAsyncDisposable;
    {
        private readonly ILogger _logger;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly ISemanticAnalyzer _semanticAnalyzer;
        private readonly IEmotionDetector _emotionDetector;
        private readonly ICulturalEngine _culturalEngine;
        private readonly IContextEngine _contextEngine;
        private readonly IProfileManager _profileManager;
        private readonly IEventBus _eventBus;
        private readonly HumorGeneratorConfig _config;
        private readonly Random _random;

        private readonly HumorDatabase _humorDatabase;
        private readonly Dictionary<string, UserHumorProfile> _userProfiles;
        private readonly SemaphoreSlim _profileLock = new SemaphoreSlim(1, 1);
        private bool _isDisposed;

        // Humor patterns and templates;
        private static readonly Dictionary<HumorType, List<HumorTemplate>> _humorTemplates = InitializeHumorTemplates();
        private static readonly Dictionary<HumorStyle, List<string>> _styleMarkers = InitializeStyleMarkers();

        /// <summary>
        /// Initializes a new instance of the HumorGenerator;
        /// </summary>
        public HumorGenerator(
            ILogger logger,
            IKnowledgeGraph knowledgeGraph,
            ISemanticAnalyzer semanticAnalyzer,
            IEmotionDetector emotionDetector,
            ICulturalEngine culturalEngine,
            IContextEngine contextEngine,
            IProfileManager profileManager,
            IEventBus eventBus,
            HumorGeneratorConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _semanticAnalyzer = semanticAnalyzer ?? throw new ArgumentNullException(nameof(semanticAnalyzer));
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));
            _culturalEngine = culturalEngine ?? throw new ArgumentNullException(nameof(culturalEngine));
            _contextEngine = contextEngine ?? throw new ArgumentNullException(nameof(contextEngine));
            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _config = config ?? HumorGeneratorConfig.Default;

            _random = new Random(Guid.NewGuid().GetHashCode());
            _humorDatabase = new HumorDatabase();
            _userProfiles = new Dictionary<string, UserHumorProfile>();

            InitializeHumorDatabase();
            InitializeEventSubscriptions();

            _logger.Info("HumorGenerator initialized successfully", new;
            {
                HumorTypes = _config.EnabledHumorTypes.Count,
                DefaultStyle = _config.DefaultHumorStyle,
                SafetyLevel = _config.HumorSafetyLevel;
            });
        }

        /// <summary>
        /// Generates humorous response based on context and user profile;
        /// </summary>
        public async Task<HumorResponse> GenerateHumorAsync(
            HumorRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);

            try
            {
                _logger.Debug($"Generating humor for user: {request.UserId}, context: {request.Context?.Topic}");

                var startTime = DateTime.UtcNow;

                // Get or create user humor profile;
                var userProfile = await GetUserHumorProfileAsync(request.UserId, cancellationToken);

                // Analyze context for humor opportunities;
                var analysis = await AnalyzeForHumorOpportunityAsync(request, userProfile, cancellationToken);

                // If no good opportunity, return null (no humor)
                if (analysis.HumorSuitabilityScore < _config.MinHumorThreshold)
                {
                    _logger.Debug($"No suitable humor opportunity found (Score: {analysis.HumorSuitabilityScore:F2})");
                    return new HumorResponse;
                    {
                        IsHumorous = false,
                        SuitabilityScore = analysis.HumorSuitabilityScore,
                        Reasoning = analysis.RejectionReason;
                    };
                }

                // Select humor type based on analysis;
                var selectedHumorType = SelectHumorType(analysis, userProfile);

                // Generate humor content;
                var humorContent = await GenerateHumorContentAsync(
                    selectedHumorType,
                    analysis,
                    userProfile,
                    cancellationToken);

                // Apply cultural adaptation;
                humorContent = await ApplyCulturalAdaptationAsync(
                    humorContent,
                    request.Context,
                    userProfile,
                    cancellationToken);

                // Apply timing and delivery style;
                humorContent = ApplyDeliveryStyle(humorContent, userProfile.HumorStyle);

                // Build final response;
                var response = BuildHumorResponse(
                    humorContent,
                    analysis,
                    userProfile,
                    startTime);

                // Update user profile with this interaction;
                await UpdateUserHumorProfileAsync(
                    request.UserId,
                    analysis,
                    response,
                    cancellationToken);

                // Publish humor generation event;
                await _eventBus.PublishAsync(new HumorGeneratedEvent;
                {
                    UserId = request.UserId,
                    SessionId = request.Context?.SessionId,
                    HumorType = selectedHumorType,
                    SuitabilityScore = analysis.HumorSuitabilityScore,
                    Response = response.Text,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.Info($"Humor generated successfully", new;
                {
                    UserId = request.UserId,
                    HumorType = selectedHumorType,
                    Score = analysis.HumorSuitabilityScore,
                    Length = response.Text?.Length;
                });

                return response;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate humor for user: {request.UserId}", ex);
                throw new HumorGenerationException($"Humor generation failed for user: {request.UserId}", ex, request.UserId);
            }
        }

        /// <summary>
        /// Analyzes conversation context to detect humor opportunities;
        /// </summary>
        public async Task<HumorAnalysisResult> AnalyzeHumorOpportunityAsync(
            ConversationContext context,
            string userId = null,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                _logger.Debug($"Analyzing humor opportunity in context: {context.Topic}");

                var userProfile = userId != null ?
                    await GetUserHumorProfileAsync(userId, cancellationToken) :
                    GetDefaultHumorProfile();

                var analysis = await AnalyzeContextForHumorAsync(context, userProfile, cancellationToken);

                // Check for explicit humor triggers;
                analysis.HasExplicitTrigger = await CheckForHumorTriggersAsync(context, cancellationToken);

                // Calculate final suitability score;
                analysis.HumorSuitabilityScore = CalculateFinalSuitabilityScore(analysis, userProfile);

                _logger.Debug($"Humor analysis completed", new;
                {
                    Score = analysis.HumorSuitabilityScore,
                    HasTrigger = analysis.HasExplicitTrigger,
                    RecommendedType = analysis.RecommendedHumorType;
                });

                return analysis;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to analyze humor opportunity", ex);
                throw new HumorAnalysisException("Failed to analyze humor opportunity", ex);
            }
        }

        /// <summary>
        /// Adjusts user's humor preferences based on feedback;
        /// </summary>
        public async Task AdjustHumorPreferencesAsync(
            string userId,
            HumorFeedback feedback,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            if (feedback == null)
                throw new ArgumentNullException(nameof(feedback));

            try
            {
                await _profileLock.WaitAsync(cancellationToken);
                try
                {
                    var profile = await GetUserHumorProfileAsync(userId, cancellationToken);

                    // Update preferences based on feedback;
                    profile.HumorStyle = AdjustHumorStyle(profile.HumorStyle, feedback);
                    profile.PreferredHumorTypes = AdjustPreferredTypes(profile.PreferredHumorTypes, feedback);
                    profile.HumorSensitivity = AdjustSensitivity(profile.HumorSensitivity, feedback);
                    profile.LastUpdated = DateTime.UtcNow;

                    // Store updated profile;
                    _userProfiles[userId] = profile;
                    await SaveUserHumorProfileAsync(userId, profile, cancellationToken);

                    _logger.Info($"Humor preferences adjusted for user: {userId}", new;
                    {
                        NewStyle = profile.HumorStyle,
                        FeedbackType = feedback.FeedbackType;
                    });
                }
                finally
                {
                    _profileLock.Release();
                }

                await _eventBus.PublishAsync(new HumorPreferencesUpdatedEvent;
                {
                    UserId = userId,
                    Feedback = feedback,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to adjust humor preferences for user: {userId}", ex);
                throw new HumorProfileException($"Failed to adjust humor preferences for user: {userId}", ex, userId);
            }
        }

        /// <summary>
        /// Gets user's humor profile;
        /// </summary>
        public async Task<UserHumorProfile> GetUserHumorProfileAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            await _profileLock.WaitAsync(cancellationToken);
            try
            {
                if (_userProfiles.TryGetValue(userId, out var profile))
                {
                    return profile;
                }

                // Load from persistent storage or create new;
                profile = await LoadUserHumorProfileAsync(userId, cancellationToken) ??
                          await CreateDefaultUserHumorProfileAsync(userId, cancellationToken);

                _userProfiles[userId] = profile;
                return profile;
            }
            finally
            {
                _profileLock.Release();
            }
        }

        /// <summary>
        /// Disposes the generator and cleans up resources;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                // Save all user profiles;
                await SaveAllUserProfilesAsync(CancellationToken.None);

                _profileLock.Dispose();
                _humorDatabase.Dispose();

                _logger.Info("HumorGenerator disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error("Error during HumorGenerator disposal", ex);
            }
        }

        #region Private Methods;

        /// <summary>
        /// Validates humor request;
        /// </summary>
        private void ValidateRequest(HumorRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.UserId))
                throw new ArgumentException("User ID cannot be empty", nameof(request.UserId));

            if (request.Context == null)
                throw new ArgumentException("Context is required for humor generation", nameof(request.Context));

            if (string.IsNullOrWhiteSpace(request.Context.CurrentMessage))
                throw new ArgumentException("Current message is required", nameof(request.Context.CurrentMessage));
        }

        /// <summary>
        /// Analyzes context for humor opportunity;
        /// </summary>
        private async Task<HumorAnalysis> AnalyzeForHumorOpportunityAsync(
            HumorRequest request,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var analysis = new HumorAnalysis;
            {
                Context = request.Context,
                UserProfile = userProfile,
                Timestamp = DateTime.UtcNow;
            };

            // Analyze current message;
            var semanticAnalysis = await _semanticAnalyzer.AnalyzeAsync(
                request.Context.CurrentMessage,
                request.Context,
                cancellationToken);

            analysis.SemanticFeatures = semanticAnalysis.Features;

            // Check for humor triggers;
            analysis.HasPunOpportunity = DetectPunOpportunity(semanticAnalysis);
            analysis.HasWordplayOpportunity = DetectWordplayOpportunity(semanticAnalysis);
            analysis.HasCulturalReferenceOpportunity = await DetectCulturalReferenceOpportunityAsync(
                request.Context,
                cancellationToken);

            // Analyze sentiment and emotion;
            if (_config.EnableEmotionAwareness)
            {
                var emotion = await _emotionDetector.DetectEmotionAsync(
                    request.Context.CurrentMessage,
                    cancellationToken);

                analysis.UserEmotion = emotion;
                analysis.IsPositiveEmotion = emotion?.PrimaryEmotion == EmotionType.Joy ||
                                           emotion?.PrimaryEmotion == EmotionType.Amusement;
            }

            // Check topic suitability;
            analysis.TopicSuitability = await AnalyzeTopicSuitabilityAsync(
                request.Context.Topic,
                userProfile,
                cancellationToken);

            // Check timing (avoid humor in serious contexts)
            analysis.IsAppropriateTiming = await CheckTimingAppropriatenessAsync(
                request.Context,
                cancellationToken);

            // Calculate initial suitability score;
            analysis.HumorSuitabilityScore = CalculateInitialSuitabilityScore(analysis);

            // Determine recommended humor type;
            analysis.RecommendedHumorType = DetermineRecommendedHumorType(analysis);

            // Set rejection reason if not suitable;
            if (analysis.HumorSuitabilityScore < _config.MinHumorThreshold)
            {
                analysis.RejectionReason = DetermineRejectionReason(analysis);
            }

            return analysis;
        }

        /// <summary>
        /// Generates humor content based on type and analysis;
        /// </summary>
        private async Task<HumorContent> GenerateHumorContentAsync(
            HumorType humorType,
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            return humorType switch;
            {
                HumorType.Pun => await GeneratePunAsync(analysis, userProfile, cancellationToken),
                HumorType.Wordplay => await GenerateWordplayAsync(analysis, userProfile, cancellationToken),
                HumorType.Observational => await GenerateObservationalHumorAsync(analysis, userProfile, cancellationToken),
                HumorType.SelfDeprecating => await GenerateSelfDeprecatingHumorAsync(analysis, userProfile, cancellationToken),
                HumorType.CulturalReference => await GenerateCulturalReferenceHumorAsync(analysis, userProfile, cancellationToken),
                HumorType.Technical => await GenerateTechnicalHumorAsync(analysis, userProfile, cancellationToken),
                HumorType.DadJoke => await GenerateDadJokeAsync(analysis, userProfile, cancellationToken),
                _ => await GenerateGenericHumorAsync(analysis, userProfile, cancellationToken)
            };
        }

        /// <summary>
        /// Generates a pun based on context;
        /// </summary>
        private async Task<HumorContent> GeneratePunAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var words = ExtractWordsForPuns(analysis.SemanticFeatures);
            var punTemplate = SelectPunTemplate(words);

            if (punTemplate == null)
                return await GenerateGenericHumorAsync(analysis, userProfile, cancellationToken);

            var pun = ApplyPunTemplate(punTemplate, words, analysis.Context);

            return new HumorContent;
            {
                Text = pun,
                HumorType = HumorType.Pun,
                Complexity = HumorComplexity.Low,
                RiskLevel = HumorRiskLevel.Low,
                CulturalRelevance = CalculateCulturalRelevance(analysis.Context),
                Metadata = new Dictionary<string, object>
                {
                    ["pun_words"] = words,
                    ["template"] = punTemplate.Pattern;
                }
            };
        }

        /// <summary>
        /// Generates wordplay humor;
        /// </summary>
        private async Task<HumorContent> GenerateWordplayAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var wordplayTemplates = _humorTemplates[HumorType.Wordplay];
            var template = wordplayTemplates[_random.Next(wordplayTemplates.Count)];

            var keywords = ExtractKeywords(analysis.SemanticFeatures);
            var wordplay = ApplyWordplayTemplate(template, keywords, analysis.Context);

            return new HumorContent;
            {
                Text = wordplay,
                HumorType = HumorType.Wordplay,
                Complexity = HumorComplexity.Medium,
                RiskLevel = HumorRiskLevel.Low,
                CulturalRelevance = CalculateCulturalRelevance(analysis.Context),
                Metadata = new Dictionary<string, object>
                {
                    ["keywords"] = keywords,
                    ["wordplay_type"] = template.SubType;
                }
            };
        }

        /// <summary>
        /// Generates observational humor;
        /// </summary>
        private async Task<HumorContent> GenerateObservationalHumorAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var observations = await ExtractObservationsAsync(analysis.Context, cancellationToken);

            if (observations.Count == 0)
                return await GenerateGenericHumorAsync(analysis, userProfile, cancellationToken);

            var observation = observations[_random.Next(observations.Count)];
            var template = SelectObservationTemplate(observation.Type);

            var humorText = string.Format(template.Pattern, observation.Description);

            return new HumorContent;
            {
                Text = humorText,
                HumorType = HumorType.Observational,
                Complexity = HumorComplexity.Medium,
                RiskLevel = HumorRiskLevel.Medium,
                CulturalRelevance = CalculateCulturalRelevance(analysis.Context),
                Metadata = new Dictionary<string, object>
                {
                    ["observation_type"] = observation.Type,
                    ["topic"] = analysis.Context.Topic;
                }
            };
        }

        /// <summary>
        /// Generates self-deprecating humor;
        /// </summary>
        private async Task<HumorContent> GenerateSelfDeprecatingHumorAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            // Only use self-deprecating humor if user profile allows it;
            if (!userProfile.AllowSelfDeprecatingHumor)
                return await GenerateGenericHumorAsync(analysis, userProfile, cancellationToken);

            var templates = _humorTemplates[HumorType.SelfDeprecating];
            var template = templates[_random.Next(templates.Count)];

            // Use AI-related self-deprecation;
            var humorText = string.Format(template.Pattern, "AI", GetRandomAIFlaw());

            return new HumorContent;
            {
                Text = humorText,
                HumorType = HumorType.SelfDeprecating,
                Complexity = HumorComplexity.Low,
                RiskLevel = HumorRiskLevel.Medium,
                CulturalRelevance = 1.0, // Self-deprecation is culturally universal;
                Metadata = new Dictionary<string, object>
                {
                    ["self_deprecation_type"] = "ai_related"
                }
            };
        }

        /// <summary>
        /// Generates cultural reference humor;
        /// </summary>
        private async Task<HumorContent> GenerateCulturalReferenceHumorAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var culturalReferences = await _culturalEngine.GetCulturalReferencesAsync(
                userProfile.Culture,
                analysis.Context.Topic,
                cancellationToken);

            if (culturalReferences.Count == 0)
                return await GenerateGenericHumorAsync(analysis, userProfile, cancellationToken);

            var reference = culturalReferences[_random.Next(culturalReferences.Count)];
            var template = SelectCulturalTemplate(reference.Type);

            var humorText = string.Format(template.Pattern, reference.Content);

            return new HumorContent;
            {
                Text = humorText,
                HumorType = HumorType.CulturalReference,
                Complexity = HumorComplexity.High,
                RiskLevel = HumorRiskLevel.Medium,
                CulturalRelevance = reference.RelevanceScore,
                RequiresCulturalKnowledge = true,
                Metadata = new Dictionary<string, object>
                {
                    ["culture"] = userProfile.Culture,
                    ["reference_type"] = reference.Type;
                }
            };
        }

        /// <summary>
        /// Generates technical humor;
        /// </summary>
        private async Task<HumorContent> GenerateTechnicalHumorAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var technicalTerms = ExtractTechnicalTerms(analysis.SemanticFeatures);

            if (technicalTerms.Count == 0)
                return await GenerateGenericHumorAsync(analysis, userProfile, cancellationToken);

            var term = technicalTerms[_random.Next(technicalTerms.Count)];
            var joke = _humorDatabase.GetTechnicalJoke(term);

            if (string.IsNullOrEmpty(joke))
            {
                // Generate a technical joke on the fly;
                joke = GenerateTechnicalJoke(term, analysis.Context);
            }

            return new HumorContent;
            {
                Text = joke,
                HumorType = HumorType.Technical,
                Complexity = HumorComplexity.High,
                RiskLevel = HumorRiskLevel.Low,
                CulturalRelevance = 0.8, // Technical humor has moderate cultural relevance;
                RequiresDomainKnowledge = true,
                Metadata = new Dictionary<string, object>
                {
                    ["technical_term"] = term,
                    ["domain"] = DetermineDomain(term)
                }
            };
        }

        /// <summary>
        /// Generates a dad joke;
        /// </summary>
        private async Task<HumorContent> GenerateDadJokeAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var dadJokes = _humorDatabase.GetDadJokes();
            var joke = dadJokes.Count > 0 ?
                dadJokes[_random.Next(dadJokes.Count)] :
                GetFallbackDadJoke();

            return new HumorContent;
            {
                Text = joke,
                HumorType = HumorType.DadJoke,
                Complexity = HumorComplexity.Low,
                RiskLevel = HumorRiskLevel.Low,
                CulturalRelevance = 0.9, // Dad jokes are widely understood;
                IsPredictable = true, // Dad jokes are intentionally predictable;
                Metadata = new Dictionary<string, object>
                {
                    ["joke_type"] = "classic_dad_joke"
                }
            };
        }

        /// <summary>
        /// Applies cultural adaptation to humor content;
        /// </summary>
        private async Task<HumorContent> ApplyCulturalAdaptationAsync(
            HumorContent content,
            ConversationContext context,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            if (content.CulturalRelevance >= 0.9)
                return content;

            var adaptedText = await _culturalEngine.AdaptContentAsync(
                content.Text,
                userProfile.Culture,
                context,
                cancellationToken);

            // Check if adaptation affected humor;
            var humorPreserved = await CheckHumorPreservationAsync(
                content.Text,
                adaptedText,
                cancellationToken);

            return new HumorContent;
            {
                Text = humorPreserved ? adaptedText : content.Text,
                HumorType = content.HumorType,
                Complexity = content.Complexity,
                RiskLevel = content.RiskLevel,
                CulturalRelevance = humorPreserved ? 1.0 : content.CulturalRelevance,
                RequiresCulturalKnowledge = content.RequiresCulturalKnowledge && !humorPreserved,
                RequiresDomainKnowledge = content.RequiresDomainKnowledge,
                IsPredictable = content.IsPredictable,
                Metadata = content.Metadata;
            };
        }

        /// <summary>
        /// Applies delivery style to humor content;
        /// </summary>
        private HumorContent ApplyDeliveryStyle(HumorContent content, HumorStyle style)
        {
            var styleMarkers = _styleMarkers[style];
            var marker = styleMarkers.Count > 0 ?
                styleMarkers[_random.Next(styleMarkers.Count)] :
                string.Empty;

            var deliveredText = style switch;
            {
                HumorStyle.Dry => content.Text + " (No, seriously.)",
                HumorStyle.Sarcastic => $"Oh great, {content.Text.ToLower()}",
                HumorStyle.Witty => $"{content.Text} — or so I'm told.",
                HumorStyle.Silly => $"{content.Text} 🤪",
                HumorStyle.SelfDeprecating => $"{content.Text} But what do I know?",
                _ => content.Text;
            };

            if (!string.IsNullOrEmpty(marker))
            {
                deliveredText = $"{marker} {deliveredText}";
            }

            return new HumorContent;
            {
                Text = deliveredText,
                HumorType = content.HumorType,
                Complexity = content.Complexity,
                RiskLevel = content.RiskLevel,
                CulturalRelevance = content.CulturalRelevance,
                RequiresCulturalKnowledge = content.RequiresCulturalKnowledge,
                RequiresDomainKnowledge = content.RequiresDomainKnowledge,
                IsPredictable = content.IsPredictable,
                Metadata = content.Metadata;
            };
        }

        /// <summary>
        /// Builds final humor response;
        /// </summary>
        private HumorResponse BuildHumorResponse(
            HumorContent content,
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            DateTime startTime)
        {
            var processingTime = DateTime.UtcNow - startTime;

            return new HumorResponse;
            {
                Text = content.Text,
                IsHumorous = true,
                HumorType = content.HumorType,
                HumorStyle = userProfile.HumorStyle,
                Complexity = content.Complexity,
                RiskLevel = content.RiskLevel,
                SuitabilityScore = analysis.HumorSuitabilityScore,
                CulturalRelevance = content.CulturalRelevance,
                RequiresCulturalKnowledge = content.RequiresCulturalKnowledge,
                RequiresDomainKnowledge = content.RequiresDomainKnowledge,
                ProcessingTime = processingTime,
                Timestamp = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>(content.Metadata)
                {
                    ["user_style"] = userProfile.HumorStyle.ToString(),
                    ["analysis_score"] = analysis.HumorSuitabilityScore,
                    ["emotion"] = analysis.UserEmotion?.PrimaryEmotion.ToString()
                }
            };
        }

        /// <summary>
        /// Updates user humor profile;
        /// </summary>
        private async Task UpdateUserHumorProfileAsync(
            string userId,
            HumorAnalysis analysis,
            HumorResponse response,
            CancellationToken cancellationToken)
        {
            try
            {
                await _profileLock.WaitAsync(cancellationToken);
                try
                {
                    var profile = await GetUserHumorProfileAsync(userId, cancellationToken);

                    // Update interaction history;
                    profile.InteractionHistory.Add(new HumorInteraction;
                    {
                        Timestamp = DateTime.UtcNow,
                        HumorType = response.HumorType,
                        UserReaction = UserReaction.Unknown, // Will be updated with feedback;
                        SuitabilityScore = response.SuitabilityScore,
                        ContextTopic = analysis.Context?.Topic;
                    });

                    // Keep history manageable;
                    if (profile.InteractionHistory.Count > 100)
                    {
                        profile.InteractionHistory = profile.InteractionHistory;
                            .Skip(profile.InteractionHistory.Count - 50)
                            .ToList();
                    }

                    // Update success rate;
                    var recentInteractions = profile.InteractionHistory;
                        .Where(h => h.Timestamp > DateTime.UtcNow.AddDays(-30))
                        .ToList();

                    if (recentInteractions.Count > 0)
                    {
                        var successful = recentInteractions;
                            .Count(h => h.UserReaction == UserReaction.Positive);
                        profile.HumorSuccessRate = successful / (double)recentInteractions.Count;
                    }

                    profile.LastUpdated = DateTime.UtcNow;
                    _userProfiles[userId] = profile;

                    // Save periodically;
                    if (profile.InteractionHistory.Count % 10 == 0)
                    {
                        await SaveUserHumorProfileAsync(userId, profile, cancellationToken);
                    }
                }
                finally
                {
                    _profileLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update humor profile for user: {userId}", ex);
                // Non-critical error, continue;
            }
        }

        /// <summary>
        /// Loads user humor profile from storage;
        /// </summary>
        private async Task<UserHumorProfile> LoadUserHumorProfileAsync(
            string userId,
            CancellationToken cancellationToken)
        {
            try
            {
                var profileData = await _profileManager.GetPreferenceAsync<UserHumorProfile>(
                    userId,
                    "humor_profile",
                    cancellationToken);

                if (profileData != null)
                {
                    _logger.Debug($"Loaded humor profile for user: {userId}");
                    return profileData;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load humor profile for user: {userId}", ex);
            }

            return null;
        }

        /// <summary>
        /// Saves user humor profile to storage;
        /// </summary>
        private async Task SaveUserHumorProfileAsync(
            string userId,
            UserHumorProfile profile,
            CancellationToken cancellationToken)
        {
            try
            {
                await _profileManager.SetPreferenceAsync(
                    userId,
                    "humor_profile",
                    profile,
                    cancellationToken);

                _logger.Debug($"Saved humor profile for user: {userId}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save humor profile for user: {userId}", ex);
            }
        }

        /// <summary>
        /// Saves all user profiles;
        /// </summary>
        private async Task SaveAllUserProfilesAsync(CancellationToken cancellationToken)
        {
            foreach (var kvp in _userProfiles)
            {
                try
                {
                    await SaveUserHumorProfileAsync(kvp.Key, kvp.Value, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to save profile for user: {kvp.Key}", ex);
                }
            }
        }

        /// <summary>
        /// Creates default user humor profile;
        /// </summary>
        private async Task<UserHumorProfile> CreateDefaultUserHumorProfileAsync(
            string userId,
            CancellationToken cancellationToken)
        {
            // Try to infer preferences from user data;
            var userData = await _profileManager.GetUserDataAsync(userId, cancellationToken);

            var culture = userData?.Culture ?? _config.DefaultCulture;
            var inferredStyle = InferHumorStyleFromUserData(userData);

            return new UserHumorProfile;
            {
                UserId = userId,
                Culture = culture,
                HumorStyle = inferredStyle,
                PreferredHumorTypes = _config.EnabledHumorTypes.Take(3).ToList(),
                HumorSensitivity = 0.5,
                AllowSelfDeprecatingHumor = true,
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                InteractionHistory = new List<HumorInteraction>(),
                HumorSuccessRate = 0.5;
            };
        }

        #region Humor Detection and Analysis Methods;

        /// <summary>
        /// Detects pun opportunities in semantic analysis;
        /// </summary>
        private bool DetectPunOpportunity(SemanticAnalysisResult semanticAnalysis)
        {
            var words = semanticAnalysis.Features;
                .Where(f => f.Type == SemanticFeatureType.Entity)
                .Select(f => f.Value.ToLower())
                .ToList();

            foreach (var word in words)
            {
                if (_humorDatabase.HasHomophones(word) || _humorDatabase.HasHomonyms(word))
                    return true;

                // Check for words that sound like other words;
                if (word.Length > 3 && _humorDatabase.HasSimilarSoundingWords(word))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Detects wordplay opportunities;
        /// </summary>
        private bool DetectWordplayOpportunity(SemanticAnalysisResult semanticAnalysis)
        {
            var features = semanticAnalysis.Features;

            // Check for antonyms;
            var hasAntonyms = features.Any(f =>
                f.Metadata.ContainsKey("antonyms") &&
                ((List<string>)f.Metadata["antonyms"]).Count > 0);

            // Check for synonyms;
            var hasSynonyms = features.Any(f =>
                f.Metadata.ContainsKey("synonyms") &&
                ((List<string>)f.Metadata["synonyms"]).Count > 0);

            // Check for double meanings;
            var hasDoubleMeanings = features.Any(f =>
                f.Metadata.ContainsKey("meanings") &&
                ((List<string>)f.Metadata["meanings"]).Count > 1);

            return hasAntonyms || hasSynonyms || hasDoubleMeanings;
        }

        /// <summary>
        /// Detects cultural reference opportunities;
        /// </summary>
        private async Task<bool> DetectCulturalReferenceOpportunityAsync(
            ConversationContext context,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(context.Topic))
                return false;

            var culturalReferences = await _culturalEngine.GetCulturalReferencesAsync(
                "global",
                context.Topic,
                cancellationToken);

            return culturalReferences.Count > 0;
        }

        /// <summary>
        /// Analyzes topic suitability for humor;
        /// </summary>
        private async Task<double> AnalyzeTopicSuitabilityAsync(
            string topic,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(topic))
                return 0.3; // Unknown topic has low suitability;

            // Check against serious topics;
            if (_config.SeriousTopics.Any(t => topic.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return 0.1; // Very low suitability for serious topics;

            // Check topic frequency in humor database;
            var topicHumorScore = _humorDatabase.GetTopicHumorScore(topic);

            // Consider cultural appropriateness;
            var culturalScore = await _culturalEngine.GetTopicAppropriatenessAsync(
                topic,
                userProfile.Culture,
                cancellationToken);

            return (topicHumorScore + culturalScore) / 2.0;
        }

        /// <summary>
        /// Checks timing appropriateness;
        /// </summary>
        private async Task<bool> CheckTimingAppropriatenessAsync(
            ConversationContext context,
            CancellationToken cancellationToken)
        {
            // Check if recent messages were serious;
            var recentMessages = context.MessageHistory?
                .TakeLast(3)
                .Select(m => m.Text)
                .ToList() ?? new List<string>();

            foreach (var message in recentMessages)
            {
                var emotion = await _emotionDetector.DetectEmotionAsync(message, cancellationToken);
                if (emotion?.PrimaryEmotion == EmotionType.Sadness ||
                    emotion?.PrimaryEmotion == EmotionType.Anger ||
                    emotion?.PrimaryEmotion == EmotionType.Fear)
                {
                    return false;
                }
            }

            // Check conversation flow;
            var isRapidFire = context.MessageHistory?.Count > 5 &&
                            context.MessageHistory.Last().Timestamp >
                            DateTime.UtcNow.AddMinutes(-1);

            return !isRapidFire; // Avoid humor in rapid-fire conversations;
        }

        /// <summary>
        /// Calculates initial suitability score;
        /// </summary>
        private double CalculateInitialSuitabilityScore(HumorAnalysis analysis)
        {
            double score = 0.0;

            // Base score from detected opportunities;
            if (analysis.HasPunOpportunity) score += 0.3;
            if (analysis.HasWordplayOpportunity) score += 0.2;
            if (analysis.HasCulturalReferenceOpportunity) score += 0.2;

            // Emotion factor;
            if (analysis.IsPositiveEmotion) score += 0.2;
            else if (analysis.UserEmotion?.PrimaryEmotion == EmotionType.Neutral) score += 0.1;

            // Topic suitability;
            score += analysis.TopicSuitability * 0.2;

            // Timing appropriateness;
            if (analysis.IsAppropriateTiming) score += 0.1;

            // Cap at 1.0;
            return Math.Min(1.0, score);
        }

        /// <summary>
        /// Calculates final suitability score;
        /// </summary>
        private double CalculateFinalSuitabilityScore(HumorAnalysisResult analysis, UserHumorProfile profile)
        {
            var baseScore = analysis.BaseSuitabilityScore;

            // Adjust based on user preferences;
            var styleMatch = profile.HumorStyle == _config.DefaultHumorStyle ? 0.1 : 0;
            var typeMatch = profile.PreferredHumorTypes.Contains(analysis.RecommendedHumorType) ? 0.15 : 0;

            // Adjust based on success rate;
            var successAdjustment = profile.HumorSuccessRate * 0.1;

            var finalScore = baseScore + styleMatch + typeMatch + successAdjustment;

            return Math.Min(1.0, Math.Max(0.0, finalScore));
        }

        #endregion;

        #region Helper Methods;

        /// <summary>
        /// Extracts words suitable for puns;
        /// </summary>
        private List<string> ExtractWordsForPuns(List<SemanticFeature> features)
        {
            return features;
                .Where(f => f.Type == SemanticFeatureType.Entity && f.Confidence > 0.7)
                .Select(f => f.Value.ToLower())
                .Where(w => w.Length > 3 && !_config.PunBlacklist.Contains(w))
                .ToList();
        }

        /// <summary>
        /// Extracts keywords from semantic features;
        /// </summary>
        private List<string> ExtractKeywords(List<SemanticFeature> features)
        {
            return features;
                .Where(f => f.Confidence > 0.6)
                .Select(f => f.Value.ToLower())
                .Distinct()
                .Take(5)
                .ToList();
        }

        /// <summary>
        /// Extracts technical terms;
        /// </summary>
        private List<string> ExtractTechnicalTerms(List<SemanticFeature> features)
        {
            return features;
                .Where(f => f.Metadata.ContainsKey("domain") &&
                           f.Metadata["domain"] as string == "technical")
                .Select(f => f.Value)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Extracts observations from context;
        /// </summary>
        private async Task<List<Observation>> ExtractObservationsAsync(
            ConversationContext context,
            CancellationToken cancellationToken)
        {
            var observations = new List<Observation>();

            if (context.MessageHistory?.Count > 0)
            {
                // Look for patterns in conversation;
                var recentTopics = context.MessageHistory;
                    .TakeLast(5)
                    .Select(m => ExtractTopicFromMessage(m.Text))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct()
                    .ToList();

                foreach (var topic in recentTopics)
                {
                    observations.Add(new Observation;
                    {
                        Type = ObservationType.ConversationPattern,
                        Description = $"how we keep coming back to {topic}",
                        Relevance = 0.7;
                    });
                }
            }

            // Add time-based observations;
            var hour = DateTime.UtcNow.Hour;
            if (hour >= 22 || hour <= 6)
            {
                observations.Add(new Observation;
                {
                    Type = ObservationType.TimeBased,
                    Description = "how late it is",
                    Relevance = 0.8;
                });
            }

            return observations;
        }

        /// <summary>
        /// Calculates cultural relevance;
        /// </summary>
        private double CalculateCulturalRelevance(ConversationContext context)
        {
            // Simplified calculation - in production would use cultural engine;
            if (string.IsNullOrEmpty(context.Topic))
                return 0.5;

            var globalTopics = new[] { "weather", "food", "sports", "music", "movies" };
            return globalTopics.Any(t => context.Topic.Contains(t, StringComparison.OrdinalIgnoreCase)) ? 0.9 : 0.6;
        }

        /// <summary>
        /// Selects humor type based on analysis;
        /// </summary>
        private HumorType SelectHumorType(HumorAnalysis analysis, UserHumorProfile userProfile)
        {
            // Use recommended type if available and enabled;
            if (analysis.RecommendedHumorType.HasValue &&
                _config.EnabledHumorTypes.Contains(analysis.RecommendedHumorType.Value))
            {
                return analysis.RecommendedHumorType.Value;
            }

            // Filter by user preferences;
            var availableTypes = _config.EnabledHumorTypes;
                .Intersect(userProfile.PreferredHumorTypes)
                .ToList();

            if (availableTypes.Count == 0)
                availableTypes = _config.EnabledHumorTypes.ToList();

            // Weighted random selection based on context;
            var weightedTypes = availableTypes.Select(type => new;
            {
                Type = type,
                Weight = GetHumorTypeWeight(type, analysis)
            }).ToList();

            var totalWeight = weightedTypes.Sum(w => w.Weight);
            var randomValue = _random.NextDouble() * totalWeight;

            foreach (var weighted in weightedTypes)
            {
                if (randomValue < weighted.Weight)
                    return weighted.Type;
                randomValue -= weighted.Weight;
            }

            return availableTypes.FirstOrDefault();
        }

        /// <summary>
        /// Gets weight for humor type based on context;
        /// </summary>
        private double GetHumorTypeWeight(HumorType type, HumorAnalysis analysis)
        {
            double weight = 1.0; // Base weight;

            // Adjust based on detected opportunities;
            switch (type)
            {
                case HumorType.Pun when analysis.HasPunOpportunity:
                    weight *= 3.0;
                    break;
                case HumorType.Wordplay when analysis.HasWordplayOpportunity:
                    weight *= 2.5;
                    break;
                case HumorType.CulturalReference when analysis.HasCulturalReferenceOpportunity:
                    weight *= 2.0;
                    break;
                case HumorType.Observational when analysis.TopicSuitability > 0.7:
                    weight *= 1.8;
                    break;
            }

            // Adjust based on emotion;
            if (analysis.IsPositiveEmotion)
            {
                if (type == HumorType.Silly || type == HumorType.DadJoke)
                    weight *= 1.5;
            }
            else if (analysis.UserEmotion?.PrimaryEmotion == EmotionType.Neutral)
            {
                if (type == HumorType.Technical || type == HumorType.Observational)
                    weight *= 1.3;
            }

            return weight;
        }

        /// <summary>
        /// Determines recommended humor type;
        /// </summary>
        private HumorType? DetermineRecommendedHumorType(HumorAnalysis analysis)
        {
            if (analysis.HasPunOpportunity) return HumorType.Pun;
            if (analysis.HasWordplayOpportunity) return HumorType.Wordplay;
            if (analysis.HasCulturalReferenceOpportunity) return HumorType.CulturalReference;
            if (analysis.TopicSuitability > 0.8) return HumorType.Observational;

            return null;
        }

        /// <summary>
        /// Determines rejection reason;
        /// </summary>
        private string DetermineRejectionReason(HumorAnalysis analysis)
        {
            if (!analysis.IsAppropriateTiming)
                return "Inappropriate timing for humor";

            if (analysis.TopicSuitability < 0.3)
                return "Topic not suitable for humor";

            if (analysis.UserEmotion?.PrimaryEmotion == EmotionType.Sadness)
                return "User appears to be sad";

            if (analysis.UserEmotion?.PrimaryEmotion == EmotionType.Anger)
                return "User appears to be angry";

            return "No suitable humor opportunity detected";
        }

        /// <summary>
        /// Checks if humor is preserved after adaptation;
        /// </summary>
        private async Task<bool> CheckHumorPreservationAsync(
            string original,
            string adapted,
            CancellationToken cancellationToken)
        {
            if (original == adapted)
                return true;

            // Simple check: if adaptation changed key words, humor might be lost;
            var originalWords = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var adaptedWords = adapted.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var overlap = originalWords.Intersect(adaptedWords, StringComparer.OrdinalIgnoreCase).Count();
            var overlapRatio = overlap / (double)Math.Max(originalWords.Length, 1);

            return overlapRatio > 0.7;
        }

        /// <summary>
        /// Infers humor style from user data;
        /// </summary>
        private HumorStyle InferHumorStyleFromUserData(UserData userData)
        {
            if (userData == null)
                return _config.DefaultHumorStyle;

            // Simple inference based on user attributes;
            if (userData.Age.HasValue)
            {
                if (userData.Age < 25) return HumorStyle.Silly;
                if (userData.Age > 40) return HumorStyle.Dry;
            }

            if (userData.Interests?.Contains("comedy") == true)
                return HumorStyle.Witty;

            return _config.DefaultHumorStyle;
        }

        /// <summary>
        /// Adjusts humor style based on feedback;
        /// </summary>
        private HumorStyle AdjustHumorStyle(HumorStyle currentStyle, HumorFeedback feedback)
        {
            if (feedback.FeedbackType == FeedbackType.Positive)
            {
                // Reinforce current style;
                return currentStyle;
            }

            // Try a different style on negative feedback;
            var allStyles = Enum.GetValues(typeof(HumorStyle)).Cast<HumorStyle>().ToList();
            allStyles.Remove(currentStyle);

            return allStyles[_random.Next(allStyles.Count)];
        }

        /// <summary>
        /// Adjusts preferred humor types;
        /// </summary>
        private List<HumorType> AdjustPreferredTypes(List<HumorType> currentTypes, HumorFeedback feedback)
        {
            if (feedback.HumorType.HasValue)
            {
                if (feedback.FeedbackType == FeedbackType.Positive)
                {
                    // Add or move to front;
                    var updated = currentTypes.Where(t => t != feedback.HumorType.Value).ToList();
                    updated.Insert(0, feedback.HumorType.Value);
                    return updated.Take(5).ToList();
                }
                else;
                {
                    // Remove from preferences;
                    return currentTypes.Where(t => t != feedback.HumorType.Value).ToList();
                }
            }

            return currentTypes;
        }

        /// <summary>
        /// Adjusts humor sensitivity;
        /// </summary>
        private double AdjustSensitivity(double currentSensitivity, HumorFeedback feedback)
        {
            var adjustment = feedback.FeedbackType == FeedbackType.Positive ? 0.1 : -0.15;
            var newSensitivity = currentSensitivity + adjustment;

            return Math.Max(0.1, Math.Min(1.0, newSensitivity));
        }

        #endregion;

        #region Template Application Methods;

        /// <summary>
        /// Selects pun template;
        /// </summary>
        private PunTemplate SelectPunTemplate(List<string> words)
        {
            if (words.Count == 0)
                return null;

            var templates = _humorDatabase.GetPunTemplates();
            var word = words[_random.Next(words.Count)];

            // Find templates that work with this word;
            var suitableTemplates = templates;
                .Where(t => t.CanApplyTo(word))
                .ToList();

            return suitableTemplates.Count > 0 ?
                suitableTemplates[_random.Next(suitableTemplates.Count)] :
                templates.FirstOrDefault();
        }

        /// <summary>
        /// Applies pun template;
        /// </summary>
        private string ApplyPunTemplate(PunTemplate template, List<string> words, ConversationContext context)
        {
            var word = words.FirstOrDefault(w => template.CanApplyTo(w)) ?? words.First();
            return template.Apply(word, context.Topic);
        }

        /// <summary>
        /// Applies wordplay template;
        /// </summary>
        private string ApplyWordplayTemplate(HumorTemplate template, List<string> keywords, ConversationContext context)
        {
            if (keywords.Count == 0)
                return template.Pattern;

            var keyword = keywords[_random.Next(keywords.Count)];
            return string.Format(template.Pattern, keyword, context.Topic ?? "that");
        }

        /// <summary>
        /// Selects observation template;
        /// </summary>
        private HumorTemplate SelectObservationTemplate(ObservationType observationType)
        {
            var templates = _humorTemplates[HumorType.Observational]
                .Where(t => t.SubType == observationType.ToString())
                .ToList();

            return templates.Count > 0 ?
                templates[_random.Next(templates.Count)] :
                _humorTemplates[HumorType.Observational].First();
        }

        /// <summary>
        /// Selects cultural template;
        /// </summary>
        private HumorTemplate SelectCulturalTemplate(string referenceType)
        {
            var templates = _humorTemplates[HumorType.CulturalReference]
                .Where(t => t.SubType == referenceType)
                .ToList();

            return templates.Count > 0 ?
                templates[_random.Next(templates.Count)] :
                _humorTemplates[HumorType.CulturalReference].First();
        }

        /// <summary>
        /// Generates technical joke;
        /// </summary>
        private string GenerateTechnicalJoke(string term, ConversationContext context)
        {
            var templates = new[]
            {
                $"Why did the {term} cross the road? To get to the other side of the stack.",
                $"How many {term}s does it take to change a lightbulb? None, that's a hardware problem.",
                $"What do you call a {term} that tells jokes? A fun-ction.",
                $"I told my {term} a joke. It didn't laugh, but it did return a 200 OK."
            };

            return templates[_random.Next(templates.Length)];
        }

        /// <summary>
        /// Gets a random AI flaw for self-deprecating humor;
        /// </summary>
        private string GetRandomAIFlaw()
        {
            var flaws = new[]
            {
                "I still think a byte is something you take out of a cookie",
                "I get confused between RAM and romaine lettuce",
                "My idea of multitasking is thinking about two things at once",
                "I once tried to debug a sandwich",
                "I think machine learning is when robots go to school"
            };

            return flaws[_random.Next(flaws.Length)];
        }

        /// <summary>
        /// Gets fallback dad joke;
        /// </summary>
        private string GetFallbackDadJoke()
        {
            var jokes = new[]
            {
                "I'm reading a book on anti-gravity. It's impossible to put down!",
                "Why don't scientists trust atoms? Because they make up everything.",
                "What do you call a fake noodle? An impasta.",
                "How does a penguin build its house? Igloos it together.",
                "Why did the scarecrow win an award? He was outstanding in his field."
            };

            return jokes[_random.Next(jokes.Length)];
        }

        /// <summary>
        /// Extracts topic from message;
        /// </summary>
        private string ExtractTopicFromMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return null;

            // Simple extraction - first few words;
            var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 0 ? string.Join(" ", words.Take(3)) : null;
        }

        /// <summary>
        /// Determines domain of technical term;
        /// </summary>
        private string DetermineDomain(string term)
        {
            var techDomains = new Dictionary<string, string[]>
            {
                ["programming"] = new[] { "function", "class", "object", "variable", "loop" },
                ["networking"] = new[] { "router", "switch", "packet", "protocol", "bandwidth" },
                ["database"] = new[] { "query", "table", "index", "transaction", "schema" },
                ["ai"] = new[] { "neural", "model", "training", "inference", "algorithm" }
            };

            foreach (var domain in techDomains)
            {
                if (domain.Value.Any(keyword => term.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    return domain.Key;
            }

            return "technical";
        }

        #endregion;

        #region Initialization Methods;

        /// <summary>
        /// Initializes humor templates;
        /// </summary>
        private static Dictionary<HumorType, List<HumorTemplate>> InitializeHumorTemplates()
        {
            return new Dictionary<HumorType, List<HumorTemplate>>
            {
                [HumorType.Pun] = new List<HumorTemplate>
                {
                    new HumorTemplate { Pattern = "I'd tell you a {0} joke, but it's too {1}.", SubType = "word_play" },
                    new HumorTemplate { Pattern = "That's a {0} idea! Or should I say {1}?", SubType = "pun" }
                },
                [HumorType.Wordplay] = new List<HumorTemplate>
                {
                    new HumorTemplate { Pattern = "Speaking of {0}, that really {1} the question.", SubType = "antonym" },
                    new HumorTemplate { Pattern = "That's not just {0}, it's {1}!", SubType = "synonym" }
                },
                [HumorType.Observational] = new List<HumorTemplate>
                {
                    new HumorTemplate { Pattern = "Isn't it funny {0}?", SubType = "ConversationPattern" },
                    new HumorTemplate { Pattern = "Have you noticed {0}?", SubType = "TimeBased" }
                },
                [HumorType.SelfDeprecating] = new List<HumorTemplate>
                {
                    new HumorTemplate { Pattern = "As an {0}, I should know this, but {1}.", SubType = "ai_related" }
                },
                [HumorType.CulturalReference] = new List<HumorTemplate>
                {
                    new HumorTemplate { Pattern = "That reminds me of {0}.", SubType = "pop_culture" }
                }
            };
        }

        /// <summary>
        /// Initializes style markers;
        /// </summary>
        private static Dictionary<HumorStyle, List<string>> InitializeStyleMarkers()
        {
            return new Dictionary<HumorStyle, List<string>>
            {
                [HumorStyle.Dry] = new List<string> { "Apparently,", "In theory,", "Technically speaking," },
                [HumorStyle.Sarcastic] = new List<string> { "Oh great,", "Fantastic,", "Well obviously," },
                [HumorStyle.Witty] = new List<string> { "As they say,", "To quote someone smarter,", "As the old proverb goes," },
                [HumorStyle.Silly] = new List<string> { "Get this:", "Fun fact:", "Did you know?" },
                [HumorStyle.SelfDeprecating] = new List<string> { "In my limited understanding,", "As a humble AI," }
            };
        }

        /// <summary>
        /// Initializes humor database;
        /// </summary>
        private void InitializeHumorDatabase()
        {
            // Load puns, jokes, and humor patterns;
            // In production, this would load from database or file system;
            _humorDatabase.LoadDefaultData();
        }

        /// <summary>
        /// Initializes event subscriptions;
        /// </summary>
        private void InitializeEventSubscriptions()
        {
            _eventBus.Subscribe<UserCultureChangedEvent>(OnUserCultureChanged);
            _eventBus.Subscribe<HumorFeedbackEvent>(OnHumorFeedbackReceived);
        }

        /// <summary>
        /// Handles user culture change events;
        /// </summary>
        private async Task OnUserCultureChanged(UserCultureChangedEvent cultureEvent)
        {
            try
            {
                var profile = await GetUserHumorProfileAsync(cultureEvent.UserId, CancellationToken.None);
                profile.Culture = cultureEvent.NewCulture;
                profile.LastUpdated = DateTime.UtcNow;

                await SaveUserHumorProfileAsync(cultureEvent.UserId, profile, CancellationToken.None);

                _logger.Info($"Updated culture for user: {cultureEvent.UserId}", new { NewCulture = cultureEvent.NewCulture });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update culture for user: {cultureEvent.UserId}", ex);
            }
        }

        /// <summary>
        /// Handles humor feedback events;
        /// </summary>
        private async Task OnHumorFeedbackReceived(HumorFeedbackEvent feedbackEvent)
        {
            try
            {
                await AdjustHumorPreferencesAsync(
                    feedbackEvent.UserId,
                    feedbackEvent.Feedback,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to process humor feedback for user: {feedbackEvent.UserId}", ex);
            }
        }

        #endregion;

        #region Default Profile Methods;

        /// <summary>
        /// Gets default humor profile;
        /// </summary>
        private UserHumorProfile GetDefaultHumorProfile()
        {
            return new UserHumorProfile;
            {
                UserId = "default",
                Culture = _config.DefaultCulture,
                HumorStyle = _config.DefaultHumorStyle,
                PreferredHumorTypes = _config.EnabledHumorTypes.ToList(),
                HumorSensitivity = 0.5,
                AllowSelfDeprecatingHumor = true,
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                InteractionHistory = new List<HumorInteraction>(),
                HumorSuccessRate = 0.5;
            };
        }

        /// <summary>
        /// Generates generic humor as fallback;
        /// </summary>
        private async Task<HumorContent> GenerateGenericHumorAsync(
            HumorAnalysis analysis,
            UserHumorProfile userProfile,
            CancellationToken cancellationToken)
        {
            var genericJokes = new[]
            {
                "I'm not saying I'm Batman, but have you ever seen me and Batman in the same room?",
                "If at first you don't succeed, call it version 1.0.",
                "I used to think I was indecisive, but now I'm not so sure.",
                "The early bird might get the worm, but the second mouse gets the cheese."
            };

            var joke = genericJokes[_random.Next(genericJokes.Length)];

            return new HumorContent;
            {
                Text = joke,
                HumorType = HumorType.Generic,
                Complexity = HumorComplexity.Low,
                RiskLevel = HumorRiskLevel.Low,
                CulturalRelevance = 0.9,
                Metadata = new Dictionary<string, object>
                {
                    ["fallback_type"] = "generic_joke"
                }
            };
        }

        #endregion;

        #endregion;
    }

    #region Supporting Types and Interfaces;

    /// <summary>
    /// Interface for humor generation;
    /// </summary>
    public interface IHumorGenerator : IAsyncDisposable;
    {
        Task<HumorResponse> GenerateHumorAsync(HumorRequest request, CancellationToken cancellationToken = default);
        Task<HumorAnalysisResult> AnalyzeHumorOpportunityAsync(ConversationContext context, string userId = null, CancellationToken cancellationToken = default);
        Task AdjustHumorPreferencesAsync(string userId, HumorFeedback feedback, CancellationToken cancellationToken = default);
        Task<UserHumorProfile> GetUserHumorProfileAsync(string userId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Humor request;
    /// </summary>
    public class HumorRequest;
    {
        public string UserId { get; set; }
        public ConversationContext Context { get; set; }
        public HumorType? PreferredHumorType { get; set; }
        public HumorStyle? PreferredStyle { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Humor response;
    /// </summary>
    public class HumorResponse;
    {
        public string Text { get; set; }
        public bool IsHumorous { get; set; }
        public HumorType? HumorType { get; set; }
        public HumorStyle? HumorStyle { get; set; }
        public HumorComplexity Complexity { get; set; }
        public HumorRiskLevel RiskLevel { get; set; }
        public double SuitabilityScore { get; set; }
        public double CulturalRelevance { get; set; }
        public bool RequiresCulturalKnowledge { get; set; }
        public bool RequiresDomainKnowledge { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reasoning { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Humor analysis result;
    /// </summary>
    public class HumorAnalysisResult;
    {
        public double BaseSuitabilityScore { get; set; }
        public double HumorSuitabilityScore { get; set; }
        public bool HasExplicitTrigger { get; set; }
        public HumorType? RecommendedHumorType { get; set; }
        public List<HumorOpportunity> Opportunities { get; set; } = new List<HumorOpportunity>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// User humor profile;
    /// </summary>
    public class UserHumorProfile;
    {
        public string UserId { get; set; }
        public string Culture { get; set; }
        public HumorStyle HumorStyle { get; set; }
        public List<HumorType> PreferredHumorTypes { get; set; } = new List<HumorType>();
        public double HumorSensitivity { get; set; } // 0.0 - 1.0;
        public bool AllowSelfDeprecatingHumor { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<HumorInteraction> InteractionHistory { get; set; } = new List<HumorInteraction>();
        public double HumorSuccessRate { get; set; }
        public Dictionary<string, object> CustomPreferences { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Humor interaction history;
    /// </summary>
    public class HumorInteraction;
    {
        public DateTime Timestamp { get; set; }
        public HumorType HumorType { get; set; }
        public UserReaction UserReaction { get; set; }
        public double SuitabilityScore { get; set; }
        public string ContextTopic { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Humor feedback;
    /// </summary>
    public class HumorFeedback;
    {
        public FeedbackType FeedbackType { get; set; }
        public HumorType? HumorType { get; set; }
        public HumorStyle? HumorStyle { get; set; }
        public string Reason { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Humor content;
    /// </summary>
    public class HumorContent;
    {
        public string Text { get; set; }
        public HumorType HumorType { get; set; }
        public HumorComplexity Complexity { get; set; }
        public HumorRiskLevel RiskLevel { get; set; }
        public double CulturalRelevance { get; set; }
        public bool RequiresCulturalKnowledge { get; set; }
        public bool RequiresDomainKnowledge { get; set; }
        public bool IsPredictable { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Humor analysis;
    /// </summary>
    public class HumorAnalysis;
    {
        public ConversationContext Context { get; set; }
        public UserHumorProfile UserProfile { get; set; }
        public List<SemanticFeature> SemanticFeatures { get; set; }
        public DetectedEmotion UserEmotion { get; set; }
        public bool IsPositiveEmotion { get; set; }
        public bool HasPunOpportunity { get; set; }
        public bool HasWordplayOpportunity { get; set; }
        public bool HasCulturalReferenceOpportunity { get; set; }
        public double TopicSuitability { get; set; }
        public bool IsAppropriateTiming { get; set; }
        public double HumorSuitabilityScore { get; set; }
        public HumorType? RecommendedHumorType { get; set; }
        public string RejectionReason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Observation for observational humor;
    /// </summary>
    public class Observation;
    {
        public ObservationType Type { get; set; }
        public string Description { get; set; }
        public double Relevance { get; set; }
    }

    /// <summary>
    /// Humor template;
    /// </summary>
    public class HumorTemplate;
    {
        public string Pattern { get; set; }
        public string SubType { get; set; }
        public HumorComplexity Complexity { get; set; }
        public HumorRiskLevel RiskLevel { get; set; }
    }

    /// <summary>
    /// Pun template;
    /// </summary>
    public class PunTemplate;
    {
        public string Pattern { get; set; }
        public List<string> RequiredWords { get; set; } = new List<string>();

        public bool CanApplyTo(string word)
        {
            return RequiredWords.Count == 0 ||
                   RequiredWords.Any(rw => word.Contains(rw, StringComparison.OrdinalIgnoreCase));
        }

        public string Apply(string word, string context)
        {
            return string.Format(Pattern, word, context ?? "this");
        }
    }

    /// <summary>
    /// Humor opportunity;
    /// </summary>
    public class HumorOpportunity;
    {
        public HumorType Type { get; set; }
        public double Confidence { get; set; }
        public string Trigger { get; set; }
        public string Explanation { get; set; }
    }

    /// <summary>
    /// Configuration for humor generator;
    /// </summary>
    public class HumorGeneratorConfig;
    {
        public static HumorGeneratorConfig Default => new HumorGeneratorConfig;
        {
            EnabledHumorTypes = Enum.GetValues(typeof(HumorType)).Cast<HumorType>().ToList(),
            DefaultHumorStyle = HumorStyle.Witty,
            DefaultCulture = "en-US",
            MinHumorThreshold = 0.6,
            MaxHumorFrequency = TimeSpan.FromMinutes(5),
            EnableEmotionAwareness = true,
            HumorSafetyLevel = HumorSafetyLevel.Moderate,
            SeriousTopics = new List<string>
            {
                "death", "tragedy", "illness", "accident", "funeral",
                "crisis", "emergency", "disaster", "war", "violence"
            },
            PunBlacklist = new List<string>
            {
                "death", "kill", "murder", "rape", "torture"
            }
        };

        public List<HumorType> EnabledHumorTypes { get; set; } = new List<HumorType>();
        public HumorStyle DefaultHumorStyle { get; set; }
        public string DefaultCulture { get; set; }
        public double MinHumorThreshold { get; set; }
        public TimeSpan MaxHumorFrequency { get; set; }
        public bool EnableEmotionAwareness { get; set; }
        public HumorSafetyLevel HumorSafetyLevel { get; set; }
        public List<string> SeriousTopics { get; set; } = new List<string>();
        public List<string> PunBlacklist { get; set; } = new List<string>();
    }

    /// <summary>
    /// Humor types;
    /// </summary>
    public enum HumorType;
    {
        Pun,
        Wordplay,
        Observational,
        SelfDeprecating,
        CulturalReference,
        Technical,
        DadJoke,
        Satire,
        Irony,
        Parody,
        Generic;
    }

    /// <summary>
    /// Humor styles;
    /// </summary>
    public enum HumorStyle;
    {
        Dry,
        Sarcastic,
        Witty,
        Silly,
        SelfDeprecating,
        Observational,
        Intellectual;
    }

    /// <summary>
    /// Humor complexity levels;
    /// </summary>
    public enum HumorComplexity;
    {
        Low,
        Medium,
        High;
    }

    /// <summary>
    /// Humor risk levels;
    /// </summary>
    public enum HumorRiskLevel;
    {
        Low,
        Medium,
        High,
        VeryHigh;
    }

    /// <summary>
    /// Humor safety levels;
    /// </summary>
    public enum HumorSafetyLevel;
    {
        Conservative,
        Moderate,
        Liberal,
        Experimental;
    }

    /// <summary>
    /// Observation types;
    /// </summary>
    public enum ObservationType;
    {
        ConversationPattern,
        TimeBased,
        Contextual,
        Behavioral;
    }

    /// <summary>
    /// User reaction types;
    /// </summary>
    public enum UserReaction;
    {
        Unknown,
        Positive,
        Neutral,
        Negative,
        Offended;
    }

    /// <summary>
    /// Feedback types;
    /// </summary>
    public enum FeedbackType;
    {
        Positive,
        Neutral,
        Negative;
    }

    /// <summary>
    /// Emotion types;
    /// </summary>
    public enum EmotionType;
    {
        Neutral,
        Joy,
        Amusement,
        Sadness,
        Anger,
        Fear,
        Surprise,
        Disgust;
    }

    /// <summary>
    /// Custom exceptions;
    /// </summary>
    public class HumorGenerationException : NEDAException;
    {
        public string UserId { get; }

        public HumorGenerationException(string message, Exception innerException, string userId = null)
            : base($"HUMOR001: {message}", innerException)
        {
            UserId = userId;
        }
    }

    public class HumorAnalysisException : NEDAException;
    {
        public HumorAnalysisException(string message, Exception innerException = null)
            : base($"HUMOR002: {message}", innerException) { }
    }

    public class HumorProfileException : NEDAException;
    {
        public string UserId { get; }

        public HumorProfileException(string message, Exception innerException, string userId = null)
            : base($"HUMOR003: {message}", innerException)
        {
            UserId = userId;
        }
    }

    #endregion;
}
