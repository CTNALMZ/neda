using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.AI.NaturalLanguage;
using NEDA.Brain.NLP_Engine;
using NEDA.Communication.EmotionalIntelligence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NEDA.Interface.ResponseGenerator.EmotionalIntonation;
{
    /// <summary>
    /// Duygusal ifadeler oluşturan, metin ve konuşmaya duygu katmanı ekleyen sınıf;
    /// </summary>
    public class ExpressionBuilder : IExpressionBuilder;
    {
        private readonly IEmotionAnalyzer _emotionAnalyzer;
        private readonly IToneModulator _toneModulator;
        private readonly INLPEngine _nlpEngine;
        private readonly ILogger _logger;
        private readonly ExpressionLibrary _expressionLibrary;
        private readonly Dictionary<string, EmotionalProfile> _userProfiles;
        private readonly object _profileLock = new object();

        /// <summary>
        /// ExpressionBuilder constructor;
        /// </summary>
        public ExpressionBuilder(
            IEmotionAnalyzer emotionAnalyzer,
            IToneModulator toneModulator,
            INLPEngine nlpEngine,
            ILogger logger)
        {
            _emotionAnalyzer = emotionAnalyzer ?? throw new ArgumentNullException(nameof(emotionAnalyzer));
            _toneModulator = toneModulator ?? throw new ArgumentNullException(nameof(toneModulator));
            _nlpEngine = nlpEngine ?? throw new ArgumentNullException(nameof(nlpEngine));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _expressionLibrary = new ExpressionLibrary();
            _userProfiles = new Dictionary<string, EmotionalProfile>();

            InitializeExpressionLibrary();
            _logger.LogInformation("ExpressionBuilder initialized successfully");
        }

        /// <summary>
        /// Temel metne duygusal ifadeler ekler;
        /// </summary>
        public async Task<EmotionalExpression> BuildExpressionAsync(
            string baseText,
            EmotionContext context,
            string userId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(baseText))
                throw new ArgumentException("Base text cannot be null or empty", nameof(baseText));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            try
            {
                _logger.LogDebug($"Building expression for text: {baseText.Substring(0, Math.Min(100, baseText.Length))}...");

                // 1. Kullanıcı profilini al;
                var userProfile = await GetOrCreateUserProfileAsync(userId, cancellationToken);

                // 2. Bağlam analizi;
                var analyzedContext = await AnalyzeContextAsync(context, baseText, cancellationToken);

                // 3. Duygusal profili belirle;
                var emotionalProfile = await DetermineEmotionalProfileAsync(
                    baseText,
                    analyzedContext,
                    userProfile,
                    cancellationToken);

                // 4. Metin dönüşümlerini uygula;
                var transformedText = await ApplyTextTransformationsAsync(
                    baseText,
                    emotionalProfile,
                    analyzedContext,
                    cancellationToken);

                // 5. Ses tonu parametrelerini oluştur;
                var toneParameters = await GenerateToneParametersAsync(
                    emotionalProfile,
                    analyzedContext,
                    userProfile,
                    cancellationToken);

                // 6. Yüz ifadesi parametrelerini oluştur (görsel arayüzler için)
                var facialParameters = await GenerateFacialParametersAsync(
                    emotionalProfile,
                    analyzedContext,
                    cancellationToken);

                // 7. Vücut dili parametrelerini oluştur;
                var bodyLanguageParameters = await GenerateBodyLanguageParametersAsync(
                    emotionalProfile,
                    analyzedContext,
                    cancellationToken);

                var expression = new EmotionalExpression;
                {
                    Id = Guid.NewGuid().ToString(),
                    BaseText = baseText,
                    TransformedText = transformedText,
                    EmotionalProfile = emotionalProfile,
                    ToneParameters = toneParameters,
                    FacialParameters = facialParameters,
                    BodyLanguageParameters = bodyLanguageParameters,
                    Context = analyzedContext,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>
                    {
                        ["OriginalLength"] = baseText.Length,
                        ["TransformedLength"] = transformedText.Length,
                        ["Complexity"] = CalculateComplexity(transformedText)
                    }
                };

                // 8. Kullanıcı profilini güncelle;
                await UpdateUserProfileAsync(userId, emotionalProfile, expression, cancellationToken);

                _logger.LogInformation($"Expression built successfully: {expression.Id}");
                return expression;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Expression building cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error building expression for text: {baseText}");
                throw new ExpressionBuildingException($"Failed to build expression: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Çoklu duygu katmanları ile ifade oluşturur;
        /// </summary>
        public async Task<LayeredExpression> BuildLayeredExpressionAsync(
            string baseText,
            List<EmotionLayer> emotionLayers,
            EmotionContext context,
            string userId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(baseText))
                throw new ArgumentException("Base text cannot be null or empty", nameof(baseText));

            if (emotionLayers == null || emotionLayers.Count == 0)
                throw new ArgumentException("At least one emotion layer is required", nameof(emotionLayers));

            try
            {
                _logger.LogDebug($"Building layered expression with {emotionLayers.Count} layers");

                var layers = new List<EmotionalExpression>();
                var currentText = baseText;

                // Her bir duygu katmanını sırayla uygula;
                foreach (var layer in emotionLayers.OrderBy(l => l.Priority))
                {
                    var layerContext = new EmotionContext;
                    {
                        PrimaryEmotion = layer.Emotion,
                        Intensity = layer.Intensity,
                        TargetAudience = context?.TargetAudience,
                        CulturalContext = context?.CulturalContext,
                        Situation = context?.Situation;
                    };

                    var expression = await BuildExpressionAsync(
                        currentText,
                        layerContext,
                        userId,
                        cancellationToken);

                    layers.Add(expression);
                    currentText = expression.TransformedText;
                }

                var layeredExpression = new LayeredExpression;
                {
                    Id = Guid.NewGuid().ToString(),
                    BaseText = baseText,
                    FinalText = currentText,
                    Layers = layers,
                    PrimaryEmotion = emotionLayers.First().Emotion,
                    ComplexityLevel = CalculateLayeredComplexity(layers),
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation($"Layered expression built with {layers.Count} layers");
                return layeredExpression;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building layered expression");
                throw;
            }
        }

        /// <summary>
        /// Kültürel bağlama uygun ifade oluşturur;
        /// </summary>
        public async Task<CulturalExpression> BuildCulturalExpressionAsync(
            string baseText,
            string cultureCode,
            EmotionContext context,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(baseText))
                throw new ArgumentException("Base text cannot be null or empty", nameof(baseText));

            if (string.IsNullOrWhiteSpace(cultureCode))
                throw new ArgumentException("Culture code cannot be null or empty", nameof(cultureCode));

            try
            {
                _logger.LogDebug($"Building cultural expression for culture: {cultureCode}");

                // Kültürel adaptasyon kurallarını yükle;
                var culturalRules = await LoadCulturalRulesAsync(cultureCode, cancellationToken);

                // Temel ifadeyi oluştur;
                var baseExpression = await BuildExpressionAsync(baseText, context, null, cancellationToken);

                // Kültürel adaptasyonları uygula;
                var adaptedText = await ApplyCulturalAdaptationsAsync(
                    baseExpression.TransformedText,
                    culturalRules,
                    context,
                    cancellationToken);

                // Kültürel ton parametrelerini ayarla;
                var culturalToneParameters = await AdjustToneForCultureAsync(
                    baseExpression.ToneParameters,
                    culturalRules,
                    cancellationToken);

                var culturalExpression = new CulturalExpression;
                {
                    Id = Guid.NewGuid().ToString(),
                    BaseExpression = baseExpression,
                    AdaptedText = adaptedText,
                    CultureCode = cultureCode,
                    CulturalRules = culturalRules,
                    ToneParameters = culturalToneParameters,
                    FormalityLevel = DetermineFormalityLevel(culturalRules, context),
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation($"Cultural expression built for {cultureCode}");
                return culturalExpression;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error building cultural expression for {cultureCode}");
                throw;
            }
        }

        /// <summary>
        /// Duygusal yoğunluğu ayarlar;
        /// </summary>
        public async Task<EmotionalExpression> AdjustIntensityAsync(
            EmotionalExpression expression,
            float intensityMultiplier,
            CancellationToken cancellationToken = default)
        {
            if (expression == null)
                throw new ArgumentNullException(nameof(expression));

            if (intensityMultiplier < 0.1f || intensityMultiplier > 3.0f)
                throw new ArgumentOutOfRangeException(nameof(intensityMultiplier),
                    "Intensity multiplier must be between 0.1 and 3.0");

            try
            {
                _logger.LogDebug($"Adjusting expression intensity with multiplier: {intensityMultiplier}");

                // Yoğunluğu ayarla;
                var adjustedProfile = AdjustEmotionalProfile(
                    expression.EmotionalProfile,
                    intensityMultiplier);

                // Metni yoğunluğa göre yeniden işle;
                var adjustedText = await AdjustTextIntensityAsync(
                    expression.TransformedText,
                    adjustedProfile,
                    expression.Context,
                    cancellationToken);

                // Ton parametrelerini ayarla;
                var adjustedTone = AdjustToneParameters(
                    expression.ToneParameters,
                    intensityMultiplier);

                var adjustedExpression = new EmotionalExpression;
                {
                    Id = Guid.NewGuid().ToString(),
                    BaseText = expression.BaseText,
                    TransformedText = adjustedText,
                    EmotionalProfile = adjustedProfile,
                    ToneParameters = adjustedTone,
                    FacialParameters = expression.FacialParameters, // Yoğunluğa göre ayarlanabilir;
                    BodyLanguageParameters = expression.BodyLanguageParameters,
                    Context = expression.Context,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>(expression.Metadata)
                    {
                        ["OriginalExpressionId"] = expression.Id,
                        ["IntensityMultiplier"] = intensityMultiplier,
                        ["AdjustmentType"] = "Intensity"
                    }
                };

                _logger.LogInformation($"Expression intensity adjusted from {expression.EmotionalProfile.Intensity} to {adjustedProfile.Intensity}");
                return adjustedExpression;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adjusting expression intensity");
                throw;
            }
        }

        /// <summary>
        /// Duygu karışımını ayarlar;
        /// </summary>
        public async Task<EmotionalExpression> BlendEmotionsAsync(
            EmotionalExpression baseExpression,
            EmotionMix additionalEmotions,
            float blendRatio,
            CancellationToken cancellationToken = default)
        {
            if (baseExpression == null)
                throw new ArgumentNullException(nameof(baseExpression));

            if (additionalEmotions == null)
                throw new ArgumentNullException(nameof(additionalEmotions));

            if (blendRatio < 0f || blendRatio > 1f)
                throw new ArgumentOutOfRangeException(nameof(blendRatio),
                    "Blend ratio must be between 0 and 1");

            try
            {
                _logger.LogDebug($"Blending emotions with ratio: {blendRatio}");

                // Duygu profillerini karıştır;
                var blendedProfile = BlendEmotionalProfiles(
                    baseExpression.EmotionalProfile,
                    additionalEmotions,
                    blendRatio);

                // Karıştırılmış metni oluştur;
                var blendedText = await BlendTextEmotionsAsync(
                    baseExpression.TransformedText,
                    blendedProfile,
                    baseExpression.Context,
                    cancellationToken);

                // Ton karışımını uygula;
                var blendedTone = await BlendToneParametersAsync(
                    baseExpression.ToneParameters,
                    additionalEmotions,
                    blendRatio,
                    cancellationToken);

                var blendedExpression = new EmotionalExpression;
                {
                    Id = Guid.NewGuid().ToString(),
                    BaseText = baseExpression.BaseText,
                    TransformedText = blendedText,
                    EmotionalProfile = blendedProfile,
                    ToneParameters = blendedTone,
                    FacialParameters = baseExpression.FacialParameters,
                    BodyLanguageParameters = baseExpression.BodyLanguageParameters,
                    Context = baseExpression.Context,
                    Timestamp = DateTime.UtcNow,
                    Metadata = new Dictionary<string, object>(baseExpression.Metadata)
                    {
                        ["OriginalExpressionId"] = baseExpression.Id,
                        ["BlendRatio"] = blendRatio,
                        ["AdditionalEmotions"] = additionalEmotions.ToString()
                    }
                };

                _logger.LogInformation($"Emotions blended successfully");
                return blendedExpression;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error blending emotions");
                throw;
            }
        }

        /// <summary>
        /// İfadeyi doğal dil işleme ile zenginleştirir;
        /// </summary>
        public async Task<EnrichedExpression> EnrichWithNLPAsync(
            EmotionalExpression expression,
            NLPOptions options,
            CancellationToken cancellationToken = default)
        {
            if (expression == null)
                throw new ArgumentNullException(nameof(expression));

            try
            {
                _logger.LogDebug("Enriching expression with NLP");

                // Sentiment analizi;
                var sentimentAnalysis = await _nlpEngine.AnalyzeSentimentAsync(
                    expression.TransformedText,
                    cancellationToken);

                // Entity tanıma;
                var entities = await _nlpEngine.ExtractEntitiesAsync(
                    expression.TransformedText,
                    cancellationToken);

                // Anahtar kelime çıkarımı;
                var keywords = await _nlpEngine.ExtractKeywordsAsync(
                    expression.TransformedText,
                    options?.KeywordCount ?? 5,
                    cancellationToken);

                // Anlamsal benzerlik;
                var semanticFeatures = await _nlpEngine.ExtractSemanticFeaturesAsync(
                    expression.TransformedText,
                    cancellationToken);

                var enrichedExpression = new EnrichedExpression;
                {
                    BaseExpression = expression,
                    SentimentAnalysis = sentimentAnalysis,
                    Entities = entities,
                    Keywords = keywords,
                    SemanticFeatures = semanticFeatures,
                    ReadabilityScore = CalculateReadability(expression.TransformedText),
                    EmotionalConsistency = CheckEmotionalConsistency(expression, sentimentAnalysis),
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Expression enriched with NLP features");
                return enrichedExpression;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching expression with NLP");
                throw;
            }
        }

        /// <summary>
        /// İfade kalitesini değerlendirir;
        /// </summary>
        public async Task<ExpressionQuality> EvaluateQualityAsync(
            EmotionalExpression expression,
            QualityMetrics metrics,
            CancellationToken cancellationToken = default)
        {
            if (expression == null)
                throw new ArgumentNullException(nameof(expression));

            try
            {
                _logger.LogDebug("Evaluating expression quality");

                var qualityScore = new ExpressionQuality;
                {
                    ExpressionId = expression.Id,
                    Timestamp = DateTime.UtcNow,
                    Metrics = new Dictionary<string, double>()
                };

                // 1. Duygusal tutarlılık;
                var emotionalConsistency = await EvaluateEmotionalConsistencyAsync(
                    expression,
                    cancellationToken);
                qualityScore.Metrics["EmotionalConsistency"] = emotionalConsistency;

                // 2. Doğallık;
                var naturalness = await EvaluateNaturalnessAsync(
                    expression.TransformedText,
                    cancellationToken);
                qualityScore.Metrics["Naturalness"] = naturalness;

                // 3. Bağlam uyumu;
                var contextAlignment = EvaluateContextAlignment(expression);
                qualityScore.Metrics["ContextAlignment"] = contextAlignment;

                // 4. Kültürel uygunluk;
                var culturalAppropriateness = await EvaluateCulturalAppropriatenessAsync(
                    expression,
                    cancellationToken);
                qualityScore.Metrics["CulturalAppropriateness"] = culturalAppropriateness;

                // 5. Anlaşılabilirlik;
                var clarity = EvaluateClarity(expression.TransformedText);
                qualityScore.Metrics["Clarity"] = clarity;

                // Toplam puan;
                qualityScore.OverallScore = qualityScore.Metrics.Values.Average();
                qualityScore.Grade = GetQualityGrade(qualityScore.OverallScore);

                _logger.LogInformation($"Expression quality evaluated: {qualityScore.OverallScore:F2} ({qualityScore.Grade})");
                return qualityScore;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating expression quality");
                throw;
            }
        }

        private async Task<EmotionalProfile> DetermineEmotionalProfileAsync(
            string text,
            EmotionContext context,
            EmotionalProfile userProfile,
            CancellationToken cancellationToken)
        {
            // Metinden duygu analizi;
            var textEmotions = await _emotionAnalyzer.AnalyzeTextEmotionAsync(text, cancellationToken);

            // Bağlamdan duygu analizi;
            var contextEmotions = await _emotionAnalyzer.AnalyzeContextEmotionAsync(context, cancellationToken);

            // Kullanıcı profilini birleştir;
            var combinedProfile = CombineEmotionalProfiles(textEmotions, contextEmotions, userProfile);

            // Yoğunluğu bağlama göre ayarla;
            AdjustIntensityForContext(combinedProfile, context);

            return combinedProfile;
        }

        private async Task<string> ApplyTextTransformationsAsync(
            string text,
            EmotionalProfile profile,
            EmotionContext context,
            CancellationToken cancellationToken)
        {
            var transformations = new List<ITextTransformation>();

            // Duyguya özgü dönüşümler;
            transformations.AddRange(GetEmotionSpecificTransformations(profile.PrimaryEmotion));

            // Yoğunluğa göre dönüşümler;
            transformations.AddRange(GetIntensityTransformations(profile.Intensity));

            // Bağlama özgü dönüşümler;
            transformations.AddRange(GetContextSpecificTransformations(context));

            // Dönüşümleri uygula;
            var result = text;
            foreach (var transformation in transformations.OrderBy(t => t.Priority))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                result = await transformation.ApplyAsync(result, profile, context, cancellationToken);
            }

            return result;
        }

        private async Task<ToneParameters> GenerateToneParametersAsync(
            EmotionalProfile profile,
            EmotionContext context,
            EmotionalProfile userProfile,
            CancellationToken cancellationToken)
        {
            return await _toneModulator.GenerateToneParametersAsync(profile, context, userProfile, cancellationToken);
        }

        private async Task<FacialParameters> GenerateFacialParametersAsync(
            EmotionalProfile profile,
            EmotionContext context,
            CancellationToken cancellationToken)
        {
            var parameters = new FacialParameters();

            // Temel yüz ifadesi;
            parameters.BaseExpression = GetBaseFacialExpression(profile.PrimaryEmotion);

            // Yoğunluğa göre ayarla;
            parameters.Intensity = profile.Intensity;

            // Mikro ifadeler;
            parameters.MicroExpressions = GenerateMicroExpressions(profile);

            // Geçiş süreleri;
            parameters.TransitionDuration = CalculateTransitionDuration(profile, context);

            // Senkronizasyon;
            parameters.SyncWithSpeech = true;
            parameters.SyncOffset = CalculateSyncOffset(profile);

            return await Task.FromResult(parameters);
        }

        private async Task<BodyLanguageParameters> GenerateBodyLanguageParametersAsync(
            EmotionalProfile profile,
            EmotionContext context,
            CancellationToken cancellationToken)
        {
            var parameters = new BodyLanguageParameters();

            // Postür;
            parameters.Posture = GetPostureForEmotion(profile.PrimaryEmotion);

            // Jestler;
            parameters.Gestures = GenerateGestures(profile, context);

            // Baş hareketleri;
            parameters.HeadMovements = GenerateHeadMovements(profile);

            // Göz teması;
            parameters.EyeContact = CalculateEyeContactLevel(profile, context);

            return await Task.FromResult(parameters);
        }

        private async Task<EmotionalProfile> GetOrCreateUserProfileAsync(string userId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return EmotionalProfile.Default;

            lock (_profileLock)
            {
                if (_userProfiles.TryGetValue(userId, out var profile))
                    return profile;
            }

            // Profil yoksa varsayılan oluştur;
            var newProfile = EmotionalProfile.CreateDefaultForUser(userId);

            lock (_profileLock)
            {
                _userProfiles[userId] = newProfile;
            }

            _logger.LogDebug($"Created new emotional profile for user: {userId}");
            return await Task.FromResult(newProfile);
        }

        private async Task UpdateUserProfileAsync(
            string userId,
            EmotionalProfile usedProfile,
            EmotionalExpression expression,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            lock (_profileLock)
            {
                if (_userProfiles.TryGetValue(userId, out var profile))
                {
                    profile.UpdateFromUsage(usedProfile, expression);
                    _logger.LogDebug($"Updated emotional profile for user: {userId}");
                }
            }

            await Task.CompletedTask;
        }

        private void InitializeExpressionLibrary()
        {
            // Duygu ifade kütüphanesini başlat;
            _expressionLibrary.LoadDefaultExpressions();

            // Kültürel ifadeleri yükle;
            _expressionLibrary.LoadCulturalExpressions();

            // Domain-specific ifadeleri yükle;
            _expressionLibrary.LoadDomainSpecificExpressions();

            _logger.LogInformation("Expression library initialized");
        }

        private float CalculateComplexity(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0f;

            // Metin karmaşıklığı hesapla;
            var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var sentenceCount = text.Split('.', '!', '?').Length - 1;
            var avgWordLength = text.Replace(" ", "").Length / (float)Math.Max(1, wordCount);

            return (wordCount * 0.3f) + (sentenceCount * 0.2f) + (avgWordLength * 0.1f);
        }

        private float CalculateLayeredComplexity(List<EmotionalExpression> layers)
        {
            if (layers == null || layers.Count == 0)
                return 0f;

            var baseComplexity = layers.First().Metadata.TryGetValue("Complexity", out var value)
                ? Convert.ToSingle(value)
                : 1f;

            return baseComplexity * (1 + (layers.Count - 1) * 0.2f);
        }

        /// <summary>
        /// ExpressionBuilder interface'i;
        /// </summary>
        public interface IExpressionBuilder;
        {
            Task<EmotionalExpression> BuildExpressionAsync(
                string baseText,
                EmotionContext context,
                string userId = null,
                CancellationToken cancellationToken = default);

            Task<LayeredExpression> BuildLayeredExpressionAsync(
                string baseText,
                List<EmotionLayer> emotionLayers,
                EmotionContext context,
                string userId = null,
                CancellationToken cancellationToken = default);

            Task<CulturalExpression> BuildCulturalExpressionAsync(
                string baseText,
                string cultureCode,
                EmotionContext context,
                CancellationToken cancellationToken = default);

            Task<EmotionalExpression> AdjustIntensityAsync(
                EmotionalExpression expression,
                float intensityMultiplier,
                CancellationToken cancellationToken = default);

            Task<EmotionalExpression> BlendEmotionsAsync(
                EmotionalExpression baseExpression,
                EmotionMix additionalEmotions,
                float blendRatio,
                CancellationToken cancellationToken = default);

            Task<EnrichedExpression> EnrichWithNLPAsync(
                EmotionalExpression expression,
                NLPOptions options,
                CancellationToken cancellationToken = default);

            Task<ExpressionQuality> EvaluateQualityAsync(
                EmotionalExpression expression,
                QualityMetrics metrics,
                CancellationToken cancellationToken = default);
        }

        /// <summary>
        /// Duygusal ifade;
        /// </summary>
        public class EmotionalExpression;
        {
            public string Id { get; set; }
            public string BaseText { get; set; }
            public string TransformedText { get; set; }
            public EmotionalProfile EmotionalProfile { get; set; }
            public ToneParameters ToneParameters { get; set; }
            public FacialParameters FacialParameters { get; set; }
            public BodyLanguageParameters BodyLanguageParameters { get; set; }
            public EmotionContext Context { get; set; }
            public DateTime Timestamp { get; set; }
            public Dictionary<string, object> Metadata { get; set; }
        }

        /// <summary>
        /// Katmanlı ifade;
        /// </summary>
        public class LayeredExpression;
        {
            public string Id { get; set; }
            public string BaseText { get; set; }
            public string FinalText { get; set; }
            public List<EmotionalExpression> Layers { get; set; }
            public string PrimaryEmotion { get; set; }
            public float ComplexityLevel { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Kültürel ifade;
        /// </summary>
        public class CulturalExpression;
        {
            public string Id { get; set; }
            public EmotionalExpression BaseExpression { get; set; }
            public string AdaptedText { get; set; }
            public string CultureCode { get; set; }
            public CulturalRules CulturalRules { get; set; }
            public ToneParameters ToneParameters { get; set; }
            public FormalityLevel FormalityLevel { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Zenginleştirilmiş ifade;
        /// </summary>
        public class EnrichedExpression;
        {
            public EmotionalExpression BaseExpression { get; set; }
            public SentimentAnalysis SentimentAnalysis { get; set; }
            public List<Entity> Entities { get; set; }
            public List<string> Keywords { get; set; }
            public SemanticFeatures SemanticFeatures { get; set; }
            public float ReadabilityScore { get; set; }
            public float EmotionalConsistency { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// İfade kalitesi;
        /// </summary>
        public class ExpressionQuality;
        {
            public string ExpressionId { get; set; }
            public Dictionary<string, double> Metrics { get; set; }
            public double OverallScore { get; set; }
            public QualityGrade Grade { get; set; }
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Kalite sınıfları;
        /// </summary>
        public enum QualityGrade;
        {
            Poor,
            Fair,
            Good,
            Excellent,
            Outstanding;
        }

        /// <summary>
        /// Özel exception'lar;
        /// </summary>
        public class ExpressionBuildingException : Exception
        {
            public ExpressionBuildingException(string message) : base(message) { }
            public ExpressionBuildingException(string message, Exception inner) : base(message, inner) { }
        }

        /// <summary>
        /// Yardımcı sınıflar (diğer dosyalardan import edilecek)
        /// </summary>
        public class EmotionContext { }
        public class EmotionalProfile { }
        public class ToneParameters { }
        public class FacialParameters { }
        public class BodyLanguageParameters { }
        public class EmotionLayer { }
        public class EmotionMix { }
        public class CulturalRules { }
        public class NLPOptions { }
        public class QualityMetrics { }
        public class SentimentAnalysis { }
        public class Entity { }
        public class SemanticFeatures { }
        public class FormalityLevel { }
        public class ExpressionLibrary { }

        public interface ITextTransformation { }
        public interface IEmotionAnalyzer { }
        public interface IToneModulator { }
        public interface INLPEngine { }
    }

    // Yardımcı extension metodlar;
    public static class ExpressionBuilderExtensions;
    {
        public static async Task<EmotionalExpression> BuildWithDefaultContextAsync(
            this IExpressionBuilder builder,
            string baseText,
            string userId = null,
            CancellationToken cancellationToken = default)
        {
            var context = new EmotionContext;
            {
                PrimaryEmotion = "neutral",
                Intensity = 0.5f,
                TargetAudience = "general",
                CulturalContext = "international",
                Situation = "casual"
            };

            return await builder.BuildExpressionAsync(baseText, context, userId, cancellationToken);
        }

        public static async Task<EmotionalExpression> BuildHappyExpressionAsync(
            this IExpressionBuilder builder,
            string baseText,
            float intensity = 0.7f,
            string userId = null,
            CancellationToken cancellationToken = default)
        {
            var context = new EmotionContext;
            {
                PrimaryEmotion = "happiness",
                Intensity = intensity,
                TargetAudience = "general",
                CulturalContext = "international",
                Situation = "positive"
            };

            return await builder.BuildExpressionAsync(baseText, context, userId, cancellationToken);
        }

        public static async Task<EmotionalExpression> BuildFormalExpressionAsync(
            this IExpressionBuilder builder,
            string baseText,
            string userId = null,
            CancellationToken cancellationToken = default)
        {
            var context = new EmotionContext;
            {
                PrimaryEmotion = "neutral",
                Intensity = 0.3f,
                TargetAudience = "professional",
                CulturalContext = "formal",
                Situation = "business"
            };

            return await builder.BuildExpressionAsync(baseText, context, userId, cancellationToken);
        }
    }
}
