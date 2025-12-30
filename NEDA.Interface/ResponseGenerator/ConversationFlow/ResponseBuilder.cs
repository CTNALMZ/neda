using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Interface.ResponseGenerator.TextToSpeech;
using NEDA.Interface.ResponseGenerator.NaturalVoiceSynthesis;
using NEDA.Interface.ResponseGenerator.EmotionalIntonation;
using NEDA.Interface.ResponseGenerator.MultilingualSupport;
using NEDA.Logging;
using Microsoft.Extensions.Logging;
using NEDA.Communication.DialogSystem;

namespace NEDA.Interface.ResponseGenerator.ConversationFlow;
{
    /// <summary>
    /// Intelligent response builder that constructs natural, contextual, and emotionally appropriate responses;
    /// by orchestrating multiple response generation components;
    /// </summary>
    public class ResponseBuilder : IResponseBuilder, IDisposable;
    {
        #region Private Fields;

        private readonly ITTSEngine _ttsEngine;
        private readonly IVoiceGenerator _voiceGenerator;
        private readonly IEmotionEngine _emotionEngine;
        private readonly ITranslator _translator;
        private readonly ICultureAdapter _cultureAdapter;
        private readonly IConversationManager _conversationManager;
        private readonly ILogger<ResponseBuilder> _logger;
        private readonly ResponseConfiguration _configuration;
        private bool _disposed;

        #endregion;

        #region Properties;

        /// <summary>
        /// Current response context containing conversation state and metadata;
        /// </summary>
        public ResponseContext CurrentContext { get; private set; }

        /// <summary>
        /// User preferences for response generation;
        /// </summary>
        public UserResponsePreferences UserPreferences { get; set; }

        /// <summary>
        /// Available response templates categorized by context;
        /// </summary>
        public ResponseTemplateLibrary TemplateLibrary { get; private set; }

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of ResponseBuilder with required dependencies;
        /// </summary>
        /// <param name="ttsEngine">Text-to-speech engine for audio rendering</param>
        /// <param name="voiceGenerator">Natural voice synthesis engine</param>
        /// <param name="emotionEngine">Emotional intonation and expression engine</param>
        /// <param name="translator">Multilingual translation service</param>
        /// <param name="cultureAdapter">Cultural adaptation service</param>
        /// <param name="conversationManager">Conversation flow manager</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="configuration">Response configuration settings</param>
        public ResponseBuilder(
            ITTSEngine ttsEngine,
            IVoiceGenerator voiceGenerator,
            IEmotionEngine emotionEngine,
            ITranslator translator,
            ICultureAdapter cultureAdapter,
            IConversationManager conversationManager,
            ILogger<ResponseBuilder> logger,
            ResponseConfiguration configuration = null)
        {
            _ttsEngine = ttsEngine ?? throw new ArgumentNullException(nameof(ttsEngine));
            _voiceGenerator = voiceGenerator ?? throw new ArgumentNullException(nameof(voiceGenerator));
            _emotionEngine = emotionEngine ?? throw new ArgumentNullException(nameof(emotionEngine));
            _translator = translator ?? throw new ArgumentNullException(nameof(translator));
            _cultureAdapter = cultureAdapter ?? throw new ArgumentNullException(nameof(cultureAdapter));
            _conversationManager = conversationManager ?? throw new ArgumentNullException(nameof(conversationManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? ResponseConfiguration.Default;

            InitializeBuilder();
        }

        /// <summary>
        /// Initializes the response builder with default settings;
        /// </summary>
        private void InitializeBuilder()
        {
            CurrentContext = new ResponseContext();
            UserPreferences = UserResponsePreferences.Default;
            TemplateLibrary = new ResponseTemplateLibrary();

            _logger.LogInformation("ResponseBuilder initialized with {EngineCount} engines", 6);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Builds a complete response based on input text and context;
        /// </summary>
        /// <param name="inputText">User input text</param>
        /// <param name="context">Current conversation context</param>
        /// <param name="userProfile">User profile for personalization</param>
        /// <returns>Complete response object with text and rendering instructions</returns>
        public async Task<CompleteResponse> BuildResponseAsync(
            string inputText,
            ConversationContext context,
            UserProfile userProfile = null)
        {
            if (string.IsNullOrWhiteSpace(inputText))
                throw new ArgumentException("Input text cannot be null or empty", nameof(inputText));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                _logger.LogDebug("Building response for input: {InputText}", inputText.Truncate(100));

                // Update current context;
                CurrentContext.Update(context, inputText);

                // Step 1: Analyze input and determine response type;
                var analysis = await AnalyzeInputAsync(inputText, context);

                // Step 2: Generate base response text;
                var baseResponse = await GenerateBaseResponseAsync(analysis, context, userProfile);

                // Step 3: Apply emotional tone and personality;
                var emotionalResponse = await ApplyEmotionalToneAsync(baseResponse, analysis.EmotionalState);

                // Step 4: Apply cultural and linguistic adaptation;
                var adaptedResponse = await ApplyCulturalAdaptationAsync(emotionalResponse, context.Culture);

                // Step 5: Prepare rendering instructions;
                var renderingInstructions = await PrepareRenderingInstructionsAsync(adaptedResponse);

                // Step 6: Create complete response object;
                var completeResponse = new CompleteResponse;
                {
                    ResponseId = Guid.NewGuid().ToString(),
                    Text = adaptedResponse.FinalText,
                    RenderingInstructions = renderingInstructions,
                    EmotionalTone = emotionalResponse.EmotionalTone,
                    LanguageCode = context.Language,
                    CulturalContext = context.Culture,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new ResponseMetadata;
                    {
                        ResponseType = analysis.ResponseType,
                        ConfidenceScore = analysis.Confidence,
                        ProcessingTime = DateTime.UtcNow - CurrentContext.LastUpdateTime;
                    }
                };

                // Step 7: Update conversation history;
                await UpdateConversationHistoryAsync(completeResponse, context);

                _logger.LogInformation("Response built successfully. ResponseId: {ResponseId}, Type: {ResponseType}",
                    completeResponse.ResponseId, analysis.ResponseType);

                return completeResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build response for input: {InputText}", inputText);

                // Return fallback response;
                return await CreateFallbackResponseAsync(inputText, context, ex);
            }
        }

        /// <summary>
        /// Builds a quick response without full processing pipeline;
        /// </summary>
        /// <param name="inputText">User input text</param>
        /// <param name="responseType">Type of response to generate</param>
        /// <returns>Quick response with basic text</returns>
        public async Task<QuickResponse> BuildQuickResponseAsync(string inputText, ResponseType responseType = ResponseType.Informative)
        {
            if (string.IsNullOrWhiteSpace(inputText))
                throw new ArgumentException("Input text cannot be null or empty", nameof(inputText));

            try
            {
                var template = TemplateLibrary.GetTemplate(responseType, QuickResponseTemplates.Templates);
                var responseText = template.FormatWith(inputText);

                return new QuickResponse;
                {
                    Text = responseText,
                    ResponseType = responseType,
                    GeneratedAt = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build quick response for input: {InputText}", inputText);
                return QuickResponse.CreateErrorResponse("I'm having trouble processing that right now.");
            }
        }

        /// <summary>
        /// Generates multiple response options for the same input;
        /// </summary>
        /// <param name="inputText">User input text</param>
        /// <param name="context">Conversation context</param>
        /// <param name="optionCount">Number of options to generate</param>
        /// <returns>Collection of response options</returns>
        public async Task<IEnumerable<ResponseOption>> GenerateResponseOptionsAsync(
            string inputText,
            ConversationContext context,
            int optionCount = 3)
        {
            if (optionCount < 1 || optionCount > 10)
                throw new ArgumentOutOfRangeException(nameof(optionCount), "Option count must be between 1 and 10");

            var options = new List<ResponseOption>();
            var analysis = await AnalyzeInputAsync(inputText, context);

            for (int i = 0; i < optionCount; i++)
            {
                try
                {
                    var response = await GenerateResponseVariantAsync(analysis, context, i);
                    options.Add(new ResponseOption;
                    {
                        OptionId = i + 1,
                        Text = response.Text,
                        Style = response.Style,
                        Confidence = response.Confidence,
                        ProsodyProfile = response.ProsodyProfile;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate response variant {VariantIndex}", i);
                }
            }

            return options.OrderByDescending(o => o.Confidence);
        }

        /// <summary>
        /// Prepares audio rendering for a text response;
        /// </summary>
        /// <param name="responseText">Text to render as audio</param>
        /// <param name="voiceProfile">Voice profile to use</param>
        /// <param name="emotion">Emotional tone for rendering</param>
        /// <returns>Audio rendering result</returns>
        public async Task<AudioRenderingResult> RenderAudioAsync(
            string responseText,
            VoiceProfile voiceProfile = null,
            EmotionalState emotion = null)
        {
            if (string.IsNullOrWhiteSpace(responseText))
                throw new ArgumentException("Response text cannot be null or empty", nameof(responseText));

            try
            {
                // Use default voice profile if none provided;
                voiceProfile ??= UserPreferences.VoiceProfile ?? VoiceProfile.Default;

                // Prepare text for speech synthesis;
                var processedText = await _ttsEngine.PrepareTextForSpeechAsync(responseText);

                // Apply emotional intonation if specified;
                if (emotion != null)
                {
                    processedText = await _emotionEngine.ApplyEmotionalIntonationAsync(processedText, emotion);
                }

                // Generate natural voice rendering;
                var audioResult = await _voiceGenerator.GenerateNaturalVoiceAsync(
                    processedText,
                    voiceProfile,
                    new ProsodySettings;
                    {
                        Rate = UserPreferences.SpeechRate,
                        Pitch = UserPreferences.SpeechPitch,
                        Volume = UserPreferences.SpeechVolume;
                    });

                _logger.LogDebug("Audio rendered successfully. Duration: {Duration}ms, Size: {Size} bytes",
                    audioResult.DurationMilliseconds, audioResult.AudioData?.Length ?? 0);

                return audioResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to render audio for text: {Text}", responseText.Truncate(200));
                throw new ResponseRenderingException("Failed to render audio response", ex);
            }
        }

        /// <summary>
        /// Updates response builder configuration;
        /// </summary>
        /// <param name="configuration">New configuration settings</param>
        public void UpdateConfiguration(ResponseConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            _configuration.MergeWith(configuration);
            _logger.LogInformation("Response builder configuration updated");
        }

        /// <summary>
        /// Clears current context and resets builder state;
        /// </summary>
        public void Reset()
        {
            CurrentContext.Clear();
            _logger.LogDebug("Response builder reset");
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Analyzes input text to determine appropriate response strategy;
        /// </summary>
        private async Task<InputAnalysis> AnalyzeInputAsync(string inputText, ConversationContext context)
        {
            var analysis = new InputAnalysis;
            {
                RawText = inputText,
                WordCount = inputText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                ContainsQuestion = inputText.Contains("?"),
                ContainsExclamation = inputText.Contains("!"),
                DetectedLanguage = await _translator.DetectLanguageAsync(inputText)
            };

            // Analyze emotional content;
            analysis.EmotionalState = await _emotionEngine.AnalyzeEmotionalContentAsync(inputText);

            // Determine response type based on analysis;
            analysis.ResponseType = DetermineResponseType(analysis, context);

            // Calculate confidence score;
            analysis.Confidence = CalculateConfidenceScore(analysis, context);

            _logger.LogDebug("Input analysis complete. Type: {ResponseType}, Confidence: {Confidence}",
                analysis.ResponseType, analysis.Confidence);

            return analysis;
        }

        /// <summary>
        /// Generates base response text based on analysis;
        /// </summary>
        private async Task<BaseResponse> GenerateBaseResponseAsync(
            InputAnalysis analysis,
            ConversationContext context,
            UserProfile userProfile)
        {
            // Check for template-based responses first;
            if (TryGetTemplateResponse(analysis, context, out var templateResponse))
            {
                return templateResponse;
            }

            // Generate dynamic response;
            var responseText = await GenerateDynamicResponseAsync(analysis, context, userProfile);

            return new BaseResponse;
            {
                Text = responseText,
                Source = ResponseSource.DynamicGeneration,
                AnalysisReference = analysis;
            };
        }

        /// <summary>
        /// Applies emotional tone to response text;
        /// </summary>
        private async Task<EmotionalResponse> ApplyEmotionalToneAsync(
            BaseResponse baseResponse,
            EmotionalState emotionalState)
        {
            var emotionalText = await _emotionEngine.ApplyEmotionalToneAsync(
                baseResponse.Text,
                emotionalState,
                UserPreferences.EmotionalStyle);

            return new EmotionalResponse;
            {
                OriginalText = baseResponse.Text,
                EmotionalText = emotionalText,
                EmotionalTone = emotionalState,
                Intensity = emotionalState.Intensity;
            };
        }

        /// <summary>
        /// Applies cultural and linguistic adaptation;
        /// </summary>
        private async Task<AdaptedResponse> ApplyCulturalAdaptationAsync(
            EmotionalResponse emotionalResponse,
            string targetCulture)
        {
            // Check if translation is needed;
            var needsTranslation = !string.Equals(
                CurrentContext.CurrentLanguage,
                targetCulture,
                StringComparison.OrdinalIgnoreCase);

            string finalText = emotionalResponse.EmotionalText;

            if (needsTranslation && _configuration.EnableAutoTranslation)
            {
                finalText = await _translator.TranslateAsync(
                    emotionalResponse.EmotionalText,
                    CurrentContext.CurrentLanguage,
                    targetCulture);

                _logger.LogDebug("Response translated from {SourceLang} to {TargetLang}",
                    CurrentContext.CurrentLanguage, targetCulture);
            }

            // Apply cultural adaptation;
            finalText = await _cultureAdapter.AdaptForCultureAsync(finalText, targetCulture);

            return new AdaptedResponse;
            {
                PreAdaptedText = emotionalResponse.EmotionalText,
                FinalText = finalText,
                TargetCulture = targetCulture,
                WasTranslated = needsTranslation;
            };
        }

        /// <summary>
        /// Prepares rendering instructions for the response;
        /// </summary>
        private async Task<RenderingInstructions> PrepareRenderingInstructionsAsync(AdaptedResponse adaptedResponse)
        {
            var instructions = new RenderingInstructions;
            {
                TextToRender = adaptedResponse.FinalText,
                VoiceProfile = UserPreferences.VoiceProfile,
                SpeechSettings = new SpeechSettings;
                {
                    Rate = UserPreferences.SpeechRate,
                    Pitch = UserPreferences.SpeechPitch,
                    Volume = UserPreferences.SpeechVolume,
                    PauseDuration = _configuration.DefaultPauseDuration;
                },
                VisualEffects = await PrepareVisualEffectsAsync(adaptedResponse),
                TimingProfile = CalculateTimingProfile(adaptedResponse.FinalText)
            };

            return instructions;
        }

        /// <summary>
        /// Updates conversation history with new response;
        /// </summary>
        private async Task UpdateConversationHistoryAsync(
            CompleteResponse response,
            ConversationContext context)
        {
            try
            {
                await _conversationManager.AddResponseAsync(context.ConversationId, response);
                CurrentContext.LastResponse = response;
                CurrentContext.ResponseCount++;

                _logger.LogTrace("Conversation history updated for conversation {ConversationId}",
                    context.ConversationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update conversation history");
            }
        }

        /// <summary>
        /// Creates a fallback response when normal processing fails;
        /// </summary>
        private async Task<CompleteResponse> CreateFallbackResponseAsync(
            string inputText,
            ConversationContext context,
            Exception error)
        {
            var fallbackText = _configuration.FallbackResponses.GetRandom();

            return new CompleteResponse;
            {
                ResponseId = $"fallback_{Guid.NewGuid()}",
                Text = fallbackText,
                RenderingInstructions = new RenderingInstructions;
                {
                    TextToRender = fallbackText,
                    VoiceProfile = VoiceProfile.Default,
                    SpeechSettings = SpeechSettings.Default;
                },
                EmotionalTone = EmotionalState.Neutral,
                LanguageCode = context.Language,
                Timestamp = DateTime.UtcNow,
                Metadata = new ResponseMetadata;
                {
                    ResponseType = ResponseType.Fallback,
                    ConfidenceScore = 0.1,
                    Error = error.Message,
                    IsFallback = true;
                }
            };
        }

        /// <summary>
        /// Determines the appropriate response type based on analysis;
        /// </summary>
        private ResponseType DetermineResponseType(InputAnalysis analysis, ConversationContext context)
        {
            if (analysis.ContainsQuestion)
                return ResponseType.QuestionAnswer;

            if (analysis.EmotionalState.Intensity > 0.7)
                return ResponseType.Emotional;

            if (context.ConversationHistory.Count == 0)
                return ResponseType.Greeting;

            if (analysis.WordCount < 3)
                return ResponseType.ShortReply;

            return ResponseType.Informative;
        }

        /// <summary>
        /// Calculates confidence score for response generation;
        /// </summary>
        private double CalculateConfidenceScore(InputAnalysis analysis, ConversationContext context)
        {
            double score = 0.5; // Base score;

            // Increase score for clear questions;
            if (analysis.ContainsQuestion && analysis.WordCount > 2)
                score += 0.2;

            // Decrease score for very short inputs;
            if (analysis.WordCount < 2)
                score -= 0.1;

            // Consider conversation history depth;
            if (context.ConversationHistory.Count > 5)
                score += 0.1;

            return Math.Clamp(score, 0.1, 1.0);
        }

        /// <summary>
        /// Attempts to get a response from templates;
        /// </summary>
        private bool TryGetTemplateResponse(
            InputAnalysis analysis,
            ConversationContext context,
            out BaseResponse response)
        {
            response = null;

            var template = TemplateLibrary.FindMatchingTemplate(analysis, context);
            if (template != null)
            {
                response = new BaseResponse;
                {
                    Text = template.GenerateResponse(analysis, context),
                    Source = ResponseSource.Template,
                    TemplateId = template.Id;
                };
                return true;
            }

            return false;
        }

        /// <summary>
        /// Generates a dynamic response when no template matches;
        /// </summary>
        private async Task<string> GenerateDynamicResponseAsync(
            InputAnalysis analysis,
            ConversationContext context,
            UserProfile userProfile)
        {
            // This would integrate with NLP engine for dynamic generation;
            // For now, return a simple constructed response;

            if (analysis.ContainsQuestion)
            {
                return $"Based on your question about '{analysis.RawText.Truncate(50)}', " +
                       $"I can provide information on that topic.";
            }

            return $"I understand you mentioned '{analysis.RawText.Truncate(50)}'. " +
                   $"That's interesting and I'd like to discuss it further.";
        }

        /// <summary>
        /// Generates a variant of response for multiple options;
        /// </summary>
        private async Task<ResponseVariant> GenerateResponseVariantAsync(
            InputAnalysis analysis,
            ConversationContext context,
            int variantIndex)
        {
            var baseText = await GenerateDynamicResponseAsync(analysis, context, null);

            // Apply different styles based on variant index;
            var style = GetVariantStyle(variantIndex);
            var styledText = ApplyStyle(baseText, style);

            return new ResponseVariant;
            {
                Text = styledText,
                Style = style,
                Confidence = 0.7 - (variantIndex * 0.1),
                ProsodyProfile = GetProsodyProfile(style)
            };
        }

        /// <summary>
        /// Prepares visual effects for the response;
        /// </summary>
        private async Task<VisualEffects> PrepareVisualEffectsAsync(AdaptedResponse response)
        {
            var emotion = await _emotionEngine.AnalyzeEmotionalContentAsync(response.FinalText);

            return new VisualEffects;
            {
                EmotionColor = emotion.GetAssociatedColor(),
                Intensity = emotion.Intensity,
                AnimationType = GetAnimationType(emotion.PrimaryEmotion),
                Duration = CalculateEffectDuration(response.FinalText)
            };
        }

        /// <summary>
        /// Calculates timing profile for speech rendering;
        /// </summary>
        private TimingProfile CalculateTimingProfile(string text)
        {
            var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var estimatedDuration = wordCount * _configuration.WordsPerMinute / 60.0 * 1000; // in milliseconds;

            return new TimingProfile;
            {
                EstimatedDurationMs = (int)estimatedDuration,
                PausePoints = CalculatePausePoints(text),
                WordCount = wordCount;
            };
        }

        /// <summary>
        /// Calculates pause points based on punctuation;
        /// </summary>
        private List<PausePoint> CalculatePausePoints(string text)
        {
            var points = new List<PausePoint>();
            int position = 0;

            foreach (char c in text)
            {
                position++;
                if (c == '.' || c == '!' || c == '?')
                {
                    points.Add(new PausePoint;
                    {
                        Position = position,
                        Duration = _configuration.DefaultPauseDuration,
                        Type = PauseType.SentenceEnd;
                    });
                }
                else if (c == ',')
                {
                    points.Add(new PausePoint;
                    {
                        Position = position,
                        Duration = _configuration.DefaultPauseDuration / 2,
                        Type = PauseType.Comma;
                    });
                }
            }

            return points;
        }

        /// <summary>
        /// Gets style for response variant;
        /// </summary>
        private ResponseStyle GetVariantStyle(int index)
        {
            return index switch;
            {
                0 => ResponseStyle.Professional,
                1 => ResponseStyle.Casual,
                2 => ResponseStyle.Friendly,
                3 => ResponseStyle.Concise,
                4 => ResponseStyle.Detailed,
                _ => ResponseStyle.Neutral;
            };
        }

        /// <summary>
        /// Applies style to response text;
        /// </summary>
        private string ApplyStyle(string text, ResponseStyle style)
        {
            return style switch;
            {
                ResponseStyle.Professional => $"To address your inquiry: {text}",
                ResponseStyle.Casual => $"Hey, so about that - {text.ToLower()}",
                ResponseStyle.Friendly => $"I'd love to share that {text} with you!",
                ResponseStyle.Concise => text.Split('.')[0] + ".",
                ResponseStyle.Detailed => $"Let me provide a comprehensive response: {text}",
                _ => text;
            };
        }

        /// <summary>
        /// Gets prosody profile for a style;
        /// </summary>
        private ProsodyProfile GetProsodyProfile(ResponseStyle style)
        {
            return style switch;
            {
                ResponseStyle.Professional => ProsodyProfile.Formal,
                ResponseStyle.Casual => ProsodyProfile.Casual,
                ResponseStyle.Friendly => ProsodyProfile.Friendly,
                ResponseStyle.Concise => ProsodyProfile.Quick,
                ResponseStyle.Detailed => ProsodyProfile.Expressive,
                _ => ProsodyProfile.Neutral;
            };
        }

        /// <summary>
        /// Gets animation type for emotion;
        /// </summary>
        private AnimationType GetAnimationType(string emotion)
        {
            return emotion.ToLower() switch;
            {
                "happy" => AnimationType.Bounce,
                "sad" => AnimationType.Fade,
                "angry" => AnimationType.Shake,
                "excited" => AnimationType.Pulse,
                "calm" => AnimationType.Float,
                _ => AnimationType.None;
            };
        }

        /// <summary>
        /// Calculates effect duration based on text;
        /// </summary>
        private TimeSpan CalculateEffectDuration(string text)
        {
            var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var durationMs = Math.Max(1000, wordCount * 100);
            return TimeSpan.FromMilliseconds(durationMs);
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Disposes managed resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected disposal method;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    Reset();
                    _logger.LogInformation("ResponseBuilder disposed");
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~ResponseBuilder()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Complete response object containing all rendering information;
        /// </summary>
        public class CompleteResponse;
        {
            public string ResponseId { get; set; }
            public string Text { get; set; }
            public RenderingInstructions RenderingInstructions { get; set; }
            public EmotionalState EmotionalTone { get; set; }
            public string LanguageCode { get; set; }
            public string CulturalContext { get; set; }
            public DateTime Timestamp { get; set; }
            public ResponseMetadata Metadata { get; set; }
        }

        /// <summary>
        /// Quick response for simple interactions;
        /// </summary>
        public class QuickResponse;
        {
            public string Text { get; set; }
            public ResponseType ResponseType { get; set; }
            public DateTime GeneratedAt { get; set; }

            public static QuickResponse CreateErrorResponse(string message)
            {
                return new QuickResponse;
                {
                    Text = message,
                    ResponseType = ResponseType.Error,
                    GeneratedAt = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Response option for multiple choice scenarios;
        /// </summary>
        public class ResponseOption;
        {
            public int OptionId { get; set; }
            public string Text { get; set; }
            public ResponseStyle Style { get; set; }
            public double Confidence { get; set; }
            public ProsodyProfile ProsodyProfile { get; set; }
        }

        /// <summary>
        /// Audio rendering result;
        /// </summary>
        public class AudioRenderingResult;
        {
            public byte[] AudioData { get; set; }
            public int DurationMilliseconds { get; set; }
            public string Format { get; set; }
            public int SampleRate { get; set; }
            public int BitDepth { get; set; }
            public VoiceProfile VoiceProfile { get; set; }
        }

        /// <summary>
        /// Response context tracking conversation state;
        /// </summary>
        public class ResponseContext;
        {
            public string CurrentLanguage { get; set; } = "en-US";
            public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
            public CompleteResponse LastResponse { get; set; }
            public int ResponseCount { get; set; }
            public Dictionary<string, object> CustomData { get; } = new Dictionary<string, object>();

            public void Update(ConversationContext context, string inputText)
            {
                CurrentLanguage = context.Language;
                LastUpdateTime = DateTime.UtcNow;
            }

            public void Clear()
            {
                CurrentLanguage = "en-US";
                LastResponse = null;
                ResponseCount = 0;
                CustomData.Clear();
            }
        }

        /// <summary>
        /// Input analysis result;
        /// </summary>
        private class InputAnalysis;
        {
            public string RawText { get; set; }
            public int WordCount { get; set; }
            public bool ContainsQuestion { get; set; }
            public bool ContainsExclamation { get; set; }
            public string DetectedLanguage { get; set; }
            public EmotionalState EmotionalState { get; set; }
            public ResponseType ResponseType { get; set; }
            public double Confidence { get; set; }
        }

        /// <summary>
        /// Base response before processing;
        /// </summary>
        private class BaseResponse;
        {
            public string Text { get; set; }
            public ResponseSource Source { get; set; }
            public string TemplateId { get; set; }
            public InputAnalysis AnalysisReference { get; set; }
        }

        /// <summary>
        /// Emotional response with tone applied;
        /// </summary>
        private class EmotionalResponse;
        {
            public string OriginalText { get; set; }
            public string EmotionalText { get; set; }
            public EmotionalState EmotionalTone { get; set; }
            public double Intensity { get; set; }
        }

        /// <summary>
        /// Culturally adapted response;
        /// </summary>
        private class AdaptedResponse;
        {
            public string PreAdaptedText { get; set; }
            public string FinalText { get; set; }
            public string TargetCulture { get; set; }
            public bool WasTranslated { get; set; }
        }

        /// <summary>
        /// Response variant for multiple options;
        /// </summary>
        private class ResponseVariant;
        {
            public string Text { get; set; }
            public ResponseStyle Style { get; set; }
            public double Confidence { get; set; }
            public ProsodyProfile ProsodyProfile { get; set; }
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Response configuration settings;
    /// </summary>
    public class ResponseConfiguration;
    {
        public static ResponseConfiguration Default => new ResponseConfiguration;
        {
            EnableAutoTranslation = true,
            DefaultPauseDuration = 300,
            WordsPerMinute = 150,
            MaxResponseLength = 500,
            EnableEmotionalToning = true,
            FallbackResponses = new List<string>
            {
                "I understand what you're saying.",
                "Let me think about that for a moment.",
                "That's an interesting point.",
                "I appreciate you sharing that with me.",
                "Could you please clarify that?"
            }
        };

        public bool EnableAutoTranslation { get; set; }
        public int DefaultPauseDuration { get; set; } // milliseconds;
        public int WordsPerMinute { get; set; }
        public int MaxResponseLength { get; set; }
        public bool EnableEmotionalToning { get; set; }
        public List<string> FallbackResponses { get; set; }

        public void MergeWith(ResponseConfiguration other)
        {
            if (other == null) return;

            EnableAutoTranslation = other.EnableAutoTranslation;
            DefaultPauseDuration = other.DefaultPauseDuration;
            WordsPerMinute = other.WordsPerMinute;
            MaxResponseLength = other.MaxResponseLength;
            EnableEmotionalToning = other.EnableEmotionalToning;

            if (other.FallbackResponses != null && other.FallbackResponses.Any())
            {
                FallbackResponses = other.FallbackResponses;
            }
        }
    }

    /// <summary>
    /// User preferences for response generation;
    /// </summary>
    public class UserResponsePreferences;
    {
        public static UserResponsePreferences Default => new UserResponsePreferences;
        {
            VoiceProfile = VoiceProfile.Default,
            SpeechRate = 1.0,
            SpeechPitch = 1.0,
            SpeechVolume = 1.0,
            EmotionalStyle = EmotionalStyle.Natural,
            PreferredResponseLength = ResponseLength.Medium;
        };

        public VoiceProfile VoiceProfile { get; set; }
        public double SpeechRate { get; set; }
        public double SpeechPitch { get; set; }
        public double SpeechVolume { get; set; }
        public EmotionalStyle EmotionalStyle { get; set; }
        public ResponseLength PreferredResponseLength { get; set; }
    }

    /// <summary>
    /// Response template library;
    /// </summary>
    public class ResponseTemplateLibrary;
    {
        private readonly Dictionary<string, ResponseTemplate> _templates = new Dictionary<string, ResponseTemplate>();

        public ResponseTemplateLibrary()
        {
            InitializeDefaultTemplates();
        }

        private void InitializeDefaultTemplates()
        {
            // Add default templates here;
            _templates["greeting"] = new ResponseTemplate;
            {
                Id = "greeting",
                Pattern = "hello|hi|hey|greetings",
                Template = "Hello! How can I assist you today?",
                ResponseType = ResponseType.Greeting;
            };

            _templates["farewell"] = new ResponseTemplate;
            {
                Id = "farewell",
                Pattern = "bye|goodbye|see you|farewell",
                Template = "Goodbye! Have a wonderful day!",
                ResponseType = ResponseType.Farewell;
            };

            _templates["thanks"] = new ResponseTemplate;
            {
                Id = "thanks",
                Pattern = "thank|thanks|appreciate",
                Template = "You're welcome! I'm glad I could help.",
                ResponseType = ResponseType.Acknowledgment;
            };
        }

        public ResponseTemplate FindMatchingTemplate(InputAnalysis analysis, ConversationContext context)
        {
            // Simple pattern matching - in production would use more sophisticated NLP;
            foreach (var template in _templates.Values)
            {
                if (template.Matches(analysis.RawText))
                    return template;
            }

            return null;
        }

        public ResponseTemplate GetTemplate(ResponseType type, Dictionary<ResponseType, string> templates)
        {
            if (templates.TryGetValue(type, out var templateText))
            {
                return new ResponseTemplate;
                {
                    Id = type.ToString().ToLower(),
                    Template = templateText,
                    ResponseType = type;
                };
            }

            return null;
        }

        public void AddTemplate(ResponseTemplate template)
        {
            _templates[template.Id] = template;
        }

        public bool RemoveTemplate(string templateId)
        {
            return _templates.Remove(templateId);
        }
    }

    /// <summary>
    /// Individual response template;
    /// </summary>
    public class ResponseTemplate;
    {
        public string Id { get; set; }
        public string Pattern { get; set; }
        public string Template { get; set; }
        public ResponseType ResponseType { get; set; }
        public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();

        public bool Matches(string input)
        {
            if (string.IsNullOrEmpty(Pattern) || string.IsNullOrEmpty(input))
                return false;

            var patterns = Pattern.Split('|');
            return patterns.Any(pattern =>
                input.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public string GenerateResponse(InputAnalysis analysis, ConversationContext context)
        {
            var response = Template;

            foreach (var variable in Variables)
            {
                response = response.Replace($"{{{variable.Key}}}", variable.Value);
            }

            return response;
        }
    }

    /// <summary>
    /// Rendering instructions for response output;
    /// </summary>
    public class RenderingInstructions;
    {
        public string TextToRender { get; set; }
        public VoiceProfile VoiceProfile { get; set; }
        public SpeechSettings SpeechSettings { get; set; }
        public VisualEffects VisualEffects { get; set; }
        public TimingProfile TimingProfile { get; set; }
    }

    /// <summary>
    /// Speech settings for audio rendering;
    /// </summary>
    public class SpeechSettings;
    {
        public static SpeechSettings Default => new SpeechSettings;
        {
            Rate = 1.0,
            Pitch = 1.0,
            Volume = 1.0,
            PauseDuration = 300;
        };

        public double Rate { get; set; }
        public double Pitch { get; set; }
        public double Volume { get; set; }
        public int PauseDuration { get; set; }
    }

    /// <summary>
    /// Visual effects for response display;
    /// </summary>
    public class VisualEffects;
    {
        public string EmotionColor { get; set; }
        public double Intensity { get; set; }
        public AnimationType AnimationType { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Timing profile for speech rendering;
    /// </summary>
    public class TimingProfile;
    {
        public int EstimatedDurationMs { get; set; }
        public List<PausePoint> PausePoints { get; set; }
        public int WordCount { get; set; }
    }

    /// <summary>
    /// Pause point in speech;
    /// </summary>
    public class PausePoint;
    {
        public int Position { get; set; }
        public int Duration { get; set; }
        public PauseType Type { get; set; }
    }

    /// <summary>
    /// Response metadata;
    /// </summary>
    public class ResponseMetadata;
    {
        public ResponseType ResponseType { get; set; }
        public double ConfidenceScore { get; set; }
        public TimeSpan? ProcessingTime { get; set; }
        public string Error { get; set; }
        public bool IsFallback { get; set; }
    }

    /// <summary>
    /// Quick response templates;
    /// </summary>
    public static class QuickResponseTemplates;
    {
        public static readonly Dictionary<ResponseType, string> Templates = new Dictionary<ResponseType, string>
        {
            [ResponseType.Informative] = "Here's what I found: {0}",
            [ResponseType.QuestionAnswer] = "The answer to your question is: {0}",
            [ResponseType.Confirmation] = "Yes, that's correct: {0}",
            [ResponseType.Clarification] = "Let me clarify: {0}",
            [ResponseType.Acknowledgment] = "I understand: {0}"
        };
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Type of response being generated;
    /// </summary>
    public enum ResponseType;
    {
        Informative,
        QuestionAnswer,
        Emotional,
        Greeting,
        Farewell,
        Acknowledgment,
        Confirmation,
        Clarification,
        ShortReply,
        Fallback,
        Error;
    }

    /// <summary>
    /// Source of the response;
    /// </summary>
    public enum ResponseSource;
    {
        Template,
        DynamicGeneration,
        MachineLearning,
        Hybrid;
    }

    /// <summary>
    /// Style of response;
    /// </summary>
    public enum ResponseStyle;
    {
        Neutral,
        Professional,
        Casual,
        Friendly,
        Concise,
        Detailed;
    }

    /// <summary>
    /// Length preference for responses;
    /// </summary>
    public enum ResponseLength;
    {
        Short,
        Medium,
        Long;
    }

    /// <summary>
    /// Emotional style preference;
    /// </summary>
    public enum EmotionalStyle;
    {
        Neutral,
        Natural,
        Expressive,
        Reserved;
    }

    /// <summary>
    /// Prosody profile for speech;
    /// </summary>
    public enum ProsodyProfile;
    {
        Neutral,
        Formal,
        Casual,
        Friendly,
        Quick,
        Expressive;
    }

    /// <summary>
    /// Type of pause in speech;
    /// </summary>
    public enum PauseType;
    {
        SentenceEnd,
        Comma,
        Emphasis,
        Dramatic;
    }

    /// <summary>
    /// Type of animation for visual effects;
    /// </summary>
    public enum AnimationType;
    {
        None,
        Fade,
        Bounce,
        Pulse,
        Shake,
        Float;
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// Main interface for response building;
    /// </summary>
    public interface IResponseBuilder : IDisposable
    {
        Task<CompleteResponse> BuildResponseAsync(string inputText, ConversationContext context, UserProfile userProfile = null);
        Task<QuickResponse> BuildQuickResponseAsync(string inputText, ResponseType responseType = ResponseType.Informative);
        Task<IEnumerable<ResponseOption>> GenerateResponseOptionsAsync(string inputText, ConversationContext context, int optionCount = 3);
        Task<AudioRenderingResult> RenderAudioAsync(string responseText, VoiceProfile voiceProfile = null, EmotionalState emotion = null);
        void UpdateConfiguration(ResponseConfiguration configuration);
        void Reset();
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Exception thrown during response rendering;
    /// </summary>
    public class ResponseRenderingException : Exception
    {
        public ResponseRenderingException(string message) : base(message) { }
        public ResponseRenderingException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
