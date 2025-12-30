using NEDA.Brain.DecisionMaking.EthicalChecker;
using NEDA.Brain.MemorySystem;
using NEDA.Brain.NLP_Engine.SentimentAnalysis;
using NEDA.Communication.DialogSystem.ConversationManager;
using NEDA.Communication.DialogSystem.QuestionAnswering;
using NEDA.Communication.EmotionalIntelligence.EmotionRecognition;
using NEDA.Communication.EmotionalIntelligence.ToneAdjustment;
using NEDA.Interface.InteractionManager;
using NEDA.Interface.InteractionManager.ContextKeeper;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.DialogueSystem.SubtitleManagement.SubtitleEngine;
using static NEDA.CharacterSystems.DialogueSystem.VoiceActing.AudioManager;

namespace NEDA.Communication.EmotionalIntelligence.EmpathyModel;
{
    /// <summary>
    /// Empathy Engine - Duygusal zeka ve empati modelleme motoru;
    /// </summary>
    public class EmpathyEngine : IEmpathyEngine;
    {
        private readonly IEmotionDetector _emotionDetector;
        private readonly ISentimentAnalyzer _sentimentAnalyzer;
        private readonly IShortTermMemory _shortTermMemory;
        private readonly IContextManager _contextManager;
        private readonly IUserProfileManager _profileManager;

        private EmpathyConfiguration _configuration;
        private EmpathyModelState _currentState;
        private readonly Dictionary<string, EmpathyProfile> _userEmpathyProfiles;

        /// <summary>
        /// Empati motoru başlatıcı;
        /// </summary>
        public EmpathyEngine(
            IEmotionDetector emotionDetector,
            ISentimentAnalyzer sentimentAnalyzer,
            IShortTermMemory shortTermMemory,
            IContextManager contextManager,
            IUserProfileManager profileManager)
        {
            _emotionDetector = emotionDetector ?? throw new ArgumentNullException(nameof(emotionDetector));
            _sentimentAnalyzer = sentimentAnalyzer ?? throw new ArgumentNullException(nameof(sentimentAnalyzer));
            _shortTermMemory = shortTermMemory ?? throw new ArgumentNullException(nameof(shortTermMemory));
            _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));

            _userEmpathyProfiles = new Dictionary<string, EmpathyProfile>();
            _currentState = new EmpathyModelState();
            _configuration = EmpathyConfiguration.Default;
        }

        /// <summary>
        /// Kullanıcının duygusal durumunu analiz eder;
        /// </summary>
        /// <param name="userId">Kullanıcı ID</param>
        /// <param name="textInput">Metin girdisi</param>
        /// <param name="audioAnalysis">Ses analizi (varsa)</param>
        /// <param name="context">İletişim bağlamı</param>
        /// <returns>Duygusal analiz sonucu</returns>
        public async Task<EmotionalAnalysisResult> AnalyzeEmotionalStateAsync(
            string userId,
            string textInput,
            AudioEmotionalAnalysis audioAnalysis = null,
            CommunicationContext context = null)
        {
            ValidateUserId(userId);

            try
            {
                // 1. Metin tabanlı duygu analizi;
                var textEmotion = await _sentimentAnalyzer.AnalyzeAsync(textInput);

                // 2. Ses analizi varsa birleştir;
                var combinedEmotion = CombineEmotionalAnalyses(textEmotion, audioAnalysis);

                // 3. Bağlam bilgisini ekle;
                var contextualEmotion = ApplyContextToEmotion(combinedEmotion, context);

                // 4. Kullanıcı empati profilini güncelle;
                await UpdateUserEmpathyProfileAsync(userId, contextualEmotion);

                // 5. Geçmiş etkileşimlerden öğren;
                var learnedResponse = ApplyLearningFromHistory(userId, contextualEmotion);

                return new EmotionalAnalysisResult;
                {
                    UserId = userId,
                    PrimaryEmotion = contextualEmotion.PrimaryEmotion,
                    SecondaryEmotions = contextualEmotion.SecondaryEmotions,
                    ConfidenceLevel = contextualEmotion.Confidence,
                    EmotionalIntensity = contextualEmotion.Intensity,
                    SuggestedEmpatheticResponse = learnedResponse,
                    Timestamp = DateTime.UtcNow,
                    ContextTags = context?.Tags ?? new List<string>()
                };
            }
            catch (Exception ex)
            {
                throw new EmpathyAnalysisException($"Empati analizi başarısız: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Empatik yanıt oluşturur;
        /// </summary>
        /// <param name="userId">Kullanıcı ID</param>
        /// <param name="emotionalState">Duygusal durum</param>
        /// <param name="communicationType">İletişim türü</param>
        /// <returns>Empatik yanıt önerisi</returns>
        public async Task<EmpatheticResponse> GenerateEmpatheticResponseAsync(
            string userId,
            EmotionalState emotionalState,
            CommunicationType communicationType)
        {
            ValidateUserId(userId);

            try
            {
                // 1. Kullanıcı empati profilini al;
                var userProfile = await GetOrCreateEmpathyProfileAsync(userId);

                // 2. Duyguya uygun yanıt stratejisini belirle;
                var responseStrategy = DetermineResponseStrategy(emotionalState, userProfile);

                // 3. İletişim türüne göre formatla;
                var formattedResponse = FormatResponseForCommunicationType(
                    responseStrategy,
                    communicationType);

                // 4. Yanıtın empatik kalitesini doğrula;
                var qualityCheck = ValidateEmpathyQuality(formattedResponse, emotionalState);

                if (!qualityCheck.IsValid)
                {
                    formattedResponse = ApplyQualityCorrections(formattedResponse, qualityCheck);
                }

                // 5. Yanıtı kişiselleştir;
                var personalizedResponse = PersonalizeResponse(
                    formattedResponse,
                    userProfile,
                    emotionalState);

                return new EmpatheticResponse;
                {
                    ResponseId = Guid.NewGuid().ToString(),
                    UserId = userId,
                    ResponseText = personalizedResponse.Text,
                    EmotionalTone = personalizedResponse.EmotionalTone,
                    ResponseStrategy = responseStrategy.StrategyType,
                    EmpathyLevel = personalizedResponse.EmpathyLevel,
                    PersonalizationLevel = personalizedResponse.PersonalizationScore,
                    GeneratedAt = DateTime.UtcNow,
                    SuggestedActions = personalizedResponse.SuggestedActions;
                };
            }
            catch (Exception ex)
            {
                throw new EmpathyResponseGenerationException(
                    $"Empatik yanıt oluşturma başarısız: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Kullanıcının empati profilini alır veya oluşturur;
        /// </summary>
        public async Task<EmpathyProfile> GetOrCreateEmpathyProfileAsync(string userId)
        {
            ValidateUserId(userId);

            if (_userEmpathyProfiles.TryGetValue(userId, out var profile))
            {
                return profile;
            }

            // Yeni profil oluştur;
            var userProfile = await _profileManager.GetUserProfileAsync(userId);
            var newEmpathyProfile = new EmpathyProfile;
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                EmotionalPatterns = new List<EmotionalPattern>(),
                PreferredCommunicationStyle = DeterminePreferredStyle(userProfile),
                SensitivityLevel = CalculateInitialSensitivity(userProfile),
                TrustLevel = 0.5m, // Başlangıç güven seviyesi;
                LearningRate = _configuration.DefaultLearningRate;
            };

            _userEmpathyProfiles[userId] = newEmpathyProfile;
            return newEmpathyProfile;
        }

        /// <summary>
        /// Empati modelini eğitir;
        /// </summary>
        public async Task<TrainingResult> TrainEmpathyModelAsync(
            string userId,
            List<EmpathyTrainingSample> trainingSamples)
        {
            ValidateUserId(userId);

            if (trainingSamples == null || trainingSamples.Count == 0)
            {
                throw new ArgumentException("Eğitim verisi boş olamaz", nameof(trainingSamples));
            }

            try
            {
                var profile = await GetOrCreateEmpathyProfileAsync(userId);
                var trainingMetrics = new TrainingMetrics();

                foreach (var sample in trainingSamples)
                {
                    // 1. Örnek üzerinde analiz yap;
                    var analysis = await AnalyzeEmotionalStateAsync(
                        userId,
                        sample.UserInput,
                        sample.AudioAnalysis,
                        sample.Context);

                    // 2. Beklenen ve üretilen yanıtı karşılaştır;
                    var responseComparison = CompareResponses(
                        sample.ExpectedResponse,
                        analysis.SuggestedEmpatheticResponse);

                    // 3. Model parametrelerini güncelle;
                    UpdateProfileFromFeedback(profile, responseComparison);

                    // 4. Metrikleri topla;
                    trainingMetrics.TotalSamples++;
                    trainingMetrics.SuccessfulMatches += responseComparison.MatchScore > 0.7 ? 1 : 0;
                    trainingMetrics.AverageMatchScore += responseComparison.MatchScore;
                }

                // 5. Profili güncelle;
                profile.LastUpdated = DateTime.UtcNow;
                profile.TrainingIterations++;
                profile.LearningRate = AdjustLearningRate(profile);

                // 6. Sonuçları hesapla;
                trainingMetrics.AverageMatchScore /= trainingSamples.Count;
                trainingMetrics.CompletionTime = DateTime.UtcNow;

                return new TrainingResult;
                {
                    UserId = userId,
                    IsSuccessful = trainingMetrics.SuccessfulMatches >= trainingSamples.Count * 0.8,
                    TrainingMetrics = trainingMetrics,
                    UpdatedProfile = profile,
                    Recommendations = GenerateTrainingRecommendations(trainingMetrics, profile)
                };
            }
            catch (Exception ex)
            {
                throw new EmpathyTrainingException($"Empati modeli eğitimi başarısız: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Çoklu modalite empati analizi;
        /// </summary>
        public async Task<MultimodalEmpathyAnalysis> AnalyzeMultimodalEmpathyAsync(
            string userId,
            TextAnalysis textAnalysis,
            VoiceAnalysis voiceAnalysis,
            FacialExpressionAnalysis facialAnalysis,
            BodyLanguageAnalysis bodyLanguage)
        {
            ValidateUserId(userId);

            try
            {
                // 1. Her modalite için analiz yap;
                var textEmpathy = await AnalyzeTextualEmpathyAsync(textAnalysis);
                var voiceEmpathy = AnalyzeVoiceEmpathy(voiceAnalysis);
                var facialEmpathy = AnalyzeFacialEmpathy(facialAnalysis);
                var bodyEmpathy = AnalyzeBodyLanguageEmpathy(bodyLanguage);

                // 2. Modaliteleri birleştir;
                var combinedAnalysis = CombineMultimodalAnalyses(
                    textEmpathy,
                    voiceEmpathy,
                    facialEmpathy,
                    bodyEmpathy);

                // 3. Tutarlılık kontrolü yap;
                var consistencyCheck = CheckConsistencyAcrossModalities(
                    textEmpathy,
                    voiceEmpathy,
                    facialEmpathy,
                    bodyEmpathy);

                // 4. Güven skorunu hesapla;
                var confidenceScore = CalculateConfidenceScore(
                    combinedAnalysis,
                    consistencyCheck);

                // 5. Kullanıcı bağlamını ekle;
                var contextualAnalysis = ApplyUserContext(
                    userId,
                    combinedAnalysis);

                return new MultimodalEmpathyAnalysis;
                {
                    UserId = userId,
                    AnalysisId = Guid.NewGuid().ToString(),
                    TextualEmpathy = textEmpathy,
                    VocalEmpathy = voiceEmpathy,
                    FacialEmpathy = facialEmpathy,
                    BodyLanguageEmpathy = bodyEmpathy,
                    CombinedEmpathyScore = combinedAnalysis.OverallEmpathyScore,
                    ConsistencyLevel = consistencyCheck.ConsistencyScore,
                    ConfidenceLevel = confidenceScore,
                    ContextualFactors = contextualAnalysis.ContextualFactors,
                    DetectedEmotionalConflicts = consistencyCheck.Conflicts,
                    RecommendedAction = GenerateMultimodalRecommendation(combinedAnalysis),
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                throw new MultimodalEmpathyAnalysisException(
                    $"Çoklu modalite empati analizi başarısız: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Empati motoru konfigürasyonunu günceller;
        /// </summary>
        public void UpdateConfiguration(EmpathyConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (!configuration.Validate())
            {
                throw new InvalidConfigurationException("Geçersiz empati motoru konfigürasyonu");
            }

            _configuration = configuration;
            _currentState.ConfigurationVersion = configuration.Version;
            _currentState.LastConfigurationUpdate = DateTime.UtcNow;

            // Bağımlı bileşenleri güncelle;
            UpdateDependentComponents(configuration);
        }

        /// <summary>
        /// Empati motoru durumunu getirir;
        /// </summary>
        public EmpathyModelState GetCurrentState()
        {
            return new EmpathyModelState;
            {
                ActiveUserCount = _userEmpathyProfiles.Count,
                TotalAnalysesPerformed = _currentState.TotalAnalysesPerformed,
                AverageResponseTime = _currentState.AverageResponseTime,
                ConfigurationVersion = _currentState.ConfigurationVersion,
                LastConfigurationUpdate = _currentState.LastConfigurationUpdate,
                EngineHealth = CheckEngineHealth(),
                MemoryUsage = GetMemoryUsage(),
                PerformanceMetrics = GetPerformanceMetrics()
            };
        }

        /// <summary>
        /// Empati motorunu sıfırlar;
        /// </summary>
        public void ResetEngine()
        {
            _userEmpathyProfiles.Clear();
            _currentState = new EmpathyModelState();
            _configuration = EmpathyConfiguration.Default;

            // Bağımlı bileşenleri sıfırla;
            ResetDependentComponents();
        }

        #region Private Methods;

        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("Kullanıcı ID boş olamaz", nameof(userId));
            }

            if (userId.Length > 100)
            {
                throw new ArgumentException("Kullanıcı ID çok uzun", nameof(userId));
            }
        }

        private async Task UpdateUserEmpathyProfileAsync(string userId, EmotionalAnalysis emotion)
        {
            var profile = await GetOrCreateEmpathyProfileAsync(userId);

            // Duygusal kalıpları güncelle;
            var pattern = new EmotionalPattern;
            {
                EmotionType = emotion.PrimaryEmotion,
                Intensity = emotion.Intensity,
                TriggerContext = _contextManager.GetCurrentContext(),
                Timestamp = DateTime.UtcNow,
                Duration = TimeSpan.Zero // Gerçek uygulamada hesaplanacak;
            };

            profile.EmotionalPatterns.Add(pattern);

            // Eğer çok fazla kalıp varsa eski olanları temizle;
            if (profile.EmotionalPatterns.Count > _configuration.MaxEmotionalPatterns)
            {
                profile.EmotionalPatterns = profile.EmotionalPatterns;
                    .OrderByDescending(p => p.Timestamp)
                    .Take(_configuration.MaxEmotionalPatterns)
                    .ToList();
            }

            // Duyarlılık seviyesini güncelle;
            profile.SensitivityLevel = RecalculateSensitivity(profile, emotion);

            profile.LastUpdated = DateTime.UtcNow;
            _userEmpathyProfiles[userId] = profile;
        }

        private ResponseStrategy DetermineResponseStrategy(EmotionalState emotion, EmpathyProfile profile)
        {
            // Duygu türüne göre strateji belirle;
            switch (emotion.EmotionType)
            {
                case EmotionType.Joy:
                    return new ResponseStrategy;
                    {
                        StrategyType = ResponseStrategyType.PositiveReinforcement,
                        Intensity = emotion.Intensity * 0.8f, // Biraz daha az yoğun;
                        ValidationRules = GetValidationRulesForJoy()
                    };

                case EmotionType.Sadness:
                    return new ResponseStrategy;
                    {
                        StrategyType = ResponseStrategyType.ComfortingSupport,
                        Intensity = emotion.Intensity * 1.2f, // Daha fazla destek;
                        ValidationRules = GetValidationRulesForSadness()
                    };

                case EmotionType.Anger:
                    return new ResponseStrategy;
                    {
                        StrategyType = ResponseStrategyType.Deescalation,
                        Intensity = Math.Min(emotion.Intensity * 0.5f, 0.7f), // Sakinleştirici;
                        ValidationRules = GetValidationRulesForAnger()
                    };

                case EmotionType.Fear:
                    return new ResponseStrategy;
                    {
                        StrategyType = ResponseStrategyType.Reassurance,
                        Intensity = emotion.Intensity * 1.1f,
                        ValidationRules = GetValidationRulesForFear()
                    };

                default:
                    return new ResponseStrategy;
                    {
                        StrategyType = ResponseStrategyType.NeutralSupport,
                        Intensity = 0.5f,
                        ValidationRules = GetDefaultValidationRules()
                    };
            }
        }

        private PersonalizedResponse PersonalizeResponse(
            FormattedResponse response,
            EmpathyProfile profile,
            EmotionalState emotion)
        {
            // Kişiselleştirme puanını hesapla;
            var personalizationScore = CalculatePersonalizationScore(profile, emotion);

            // Kişiselleştirilmiş yanıt oluştur;
            var personalized = new PersonalizedResponse;
            {
                Text = ApplyPersonalizationTemplates(response.Text, profile),
                EmotionalTone = AdjustToneForProfile(response.EmotionalTone, profile),
                EmpathyLevel = response.EmpathyLevel * profile.SensitivityLevel,
                PersonalizationScore = personalizationScore,
                SuggestedActions = GeneratePersonalizedActions(profile, emotion)
            };

            // Kültürel adaptasyon;
            if (!string.IsNullOrEmpty(profile.CulturalContext))
            {
                personalized.Text = ApplyCulturalAdaptation(personalized.Text, profile.CulturalContext);
            }

            return personalized;
        }

        private decimal CalculatePersonalizationScore(EmpathyProfile profile, EmotionalState emotion)
        {
            // Kişiselleştirme skorunu hesapla;
            var score = 0.5m; // Temel skor;

            // Güven seviyesi etkisi;
            score += (profile.TrustLevel - 0.5m) * 0.3m;

            // Duygusal kalıp eşleşmesi;
            var patternMatch = profile.EmotionalPatterns;
                .Any(p => p.EmotionType == emotion.EmotionType &&
                         Math.Abs(p.Intensity - emotion.Intensity) < 0.2);

            if (patternMatch)
            {
                score += 0.2m;
            }

            // Öğrenme oranı etkisi;
            score += (profile.LearningRate - 0.1m) * 0.1m;

            return Math.Clamp(score, 0m, 1m);
        }

        private void UpdateDependentComponents(EmpathyConfiguration configuration)
        {
            // Bağımlı bileşenlerin konfigürasyonunu güncelle;
            // Burada gerçek uygulamada dependency injection container kullanılır;
        }

        private void ResetDependentComponents()
        {
            // Bağımlı bileşenleri sıfırla;
        }

        private EngineHealth CheckEngineHealth()
        {
            return new EngineHealth;
            {
                Status = HealthStatus.Healthy,
                Issues = new List<HealthIssue>(),
                LastHealthCheck = DateTime.UtcNow,
                PerformanceScore = CalculatePerformanceScore()
            };
        }

        private float CalculatePerformanceScore()
        {
            // Performans skorunu hesapla;
            var score = 1.0f;

            // Bellek kullanımı etkisi;
            var memoryUsage = GetMemoryUsage();
            if (memoryUsage > 0.8f)
            {
                score -= 0.2f;
            }

            // Yanıt süresi etkisi;
            if (_currentState.AverageResponseTime > TimeSpan.FromSeconds(2))
            {
                score -= 0.1f;
            }

            return Math.Max(0.1f, score);
        }

        private float GetMemoryUsage()
        {
            // Gerçek uygulamada System.Diagnostics kullanılır;
            return 0.3f; // Örnek değer;
        }

        private PerformanceMetrics GetPerformanceMetrics()
        {
            return new PerformanceMetrics;
            {
                AverageAnalysisTime = _currentState.AverageResponseTime,
                SuccessRate = 0.95f, // Örnek değer;
                ErrorRate = 0.02f,
                ActiveConnections = _userEmpathyProfiles.Count,
                PeakMemoryUsage = GetMemoryUsage(),
                Uptime = TimeSpan.FromHours(24) // Örnek değer;
            };
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Yönetilen kaynakları serbest bırak;
                    _userEmpathyProfiles.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    /// <summary>
    /// Empati motoru arayüzü;
    /// </summary>
    public interface IEmpathyEngine : IDisposable
    {
        Task<EmotionalAnalysisResult> AnalyzeEmotionalStateAsync(
            string userId,
            string textInput,
            AudioEmotionalAnalysis audioAnalysis = null,
            CommunicationContext context = null);

        Task<EmpatheticResponse> GenerateEmpatheticResponseAsync(
            string userId,
            EmotionalState emotionalState,
            CommunicationType communicationType);

        Task<EmpathyProfile> GetOrCreateEmpathyProfileAsync(string userId);

        Task<TrainingResult> TrainEmpathyModelAsync(
            string userId,
            List<EmpathyTrainingSample> trainingSamples);

        Task<MultimodalEmpathyAnalysis> AnalyzeMultimodalEmpathyAsync(
            string userId,
            TextAnalysis textAnalysis,
            VoiceAnalysis voiceAnalysis,
            FacialExpressionAnalysis facialAnalysis,
            BodyLanguageAnalysis bodyLanguage);

        void UpdateConfiguration(EmpathyConfiguration configuration);

        EmpathyModelState GetCurrentState();

        void ResetEngine();
    }

    /// <summary>
    /// Empati motoru konfigürasyonu;
    /// </summary>
    public class EmpathyConfiguration;
    {
        public string Version { get; set; } = "1.0.0";
        public float DefaultLearningRate { get; set; } = 0.1f;
        public int MaxEmotionalPatterns { get; set; } = 1000;
        public TimeSpan PatternRetentionPeriod { get; set; } = TimeSpan.FromDays(30);
        public float MinEmpathyConfidence { get; set; } = 0.6f;
        public bool EnableMultimodalAnalysis { get; set; } = true;
        public bool EnablePersonalization { get; set; } = true;
        public CulturalContext DefaultCulturalContext { get; set; } = CulturalContext.Neutral;
        public Dictionary<string, object> AdvancedSettings { get; set; } = new();

        public static EmpathyConfiguration Default => new EmpathyConfiguration();

        public bool Validate()
        {
            if (DefaultLearningRate <= 0 || DefaultLearningRate > 1)
                return false;

            if (MaxEmotionalPatterns <= 0)
                return false;

            if (MinEmpathyConfidence < 0 || MinEmpathyConfidence > 1)
                return false;

            return true;
        }
    }

    /// <summary>
    /// Empati model durumu;
    /// </summary>
    public class EmpathyModelState;
    {
        public int ActiveUserCount { get; set; }
        public long TotalAnalysesPerformed { get; set; }
        public TimeSpan AverageResponseTime { get; set; }
        public string ConfigurationVersion { get; set; } = "1.0.0";
        public DateTime LastConfigurationUpdate { get; set; } = DateTime.UtcNow;
        public EngineHealth EngineHealth { get; set; }
        public float MemoryUsage { get; set; }
        public PerformanceMetrics PerformanceMetrics { get; set; }
    }

    /// <summary>
    /// Empati profili;
    /// </summary>
    public class EmpathyProfile;
    {
        public string UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }
        public List<EmotionalPattern> EmotionalPatterns { get; set; }
        public CommunicationStyle PreferredCommunicationStyle { get; set; }
        public float SensitivityLevel { get; set; }
        public decimal TrustLevel { get; set; }
        public float LearningRate { get; set; }
        public string CulturalContext { get; set; }
        public int TrainingIterations { get; set; }
        public Dictionary<string, object> CustomPreferences { get; set; }
    }

    /// <summary>
    /// Duygusal analiz sonucu;
    /// </summary>
    public class EmotionalAnalysisResult;
    {
        public string UserId { get; set; }
        public EmotionType PrimaryEmotion { get; set; }
        public List<EmotionType> SecondaryEmotions { get; set; }
        public float ConfidenceLevel { get; set; }
        public float EmotionalIntensity { get; set; }
        public string SuggestedEmpatheticResponse { get; set; }
        public DateTime Timestamp { get; set; }
        public List<string> ContextTags { get; set; }
    }

    /// <summary>
    /// Empatik yanıt;
    /// </summary>
    public class EmpatheticResponse;
    {
        public string ResponseId { get; set; }
        public string UserId { get; set; }
        public string ResponseText { get; set; }
        public EmotionalTone EmotionalTone { get; set; }
        public ResponseStrategyType ResponseStrategy { get; set; }
        public float EmpathyLevel { get; set; }
        public float PersonalizationLevel { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<SuggestedAction> SuggestedActions { get; set; }
    }

    /// <summary>
    /// Eğitim sonucu;
    /// </summary>
    public class TrainingResult;
    {
        public string UserId { get; set; }
        public bool IsSuccessful { get; set; }
        public TrainingMetrics TrainingMetrics { get; set; }
        public EmpathyProfile UpdatedProfile { get; set; }
        public List<string> Recommendations { get; set; }
    }

    /// <summary>
    /// Çoklu modalite empati analizi;
    /// </summary>
    public class MultimodalEmpathyAnalysis;
    {
        public string UserId { get; set; }
        public string AnalysisId { get; set; }
        public TextEmpathyAnalysis TextualEmpathy { get; set; }
        public VocalEmpathyAnalysis VocalEmpathy { get; set; }
        public FacialEmpathyAnalysis FacialEmpathy { get; set; }
        public BodyLanguageEmpathyAnalysis BodyLanguageEmpathy { get; set; }
        public float CombinedEmpathyScore { get; set; }
        public float ConsistencyLevel { get; set; }
        public float ConfidenceLevel { get; set; }
        public List<ContextualFactor> ContextualFactors { get; set; }
        public List<EmotionalConflict> DetectedEmotionalConflicts { get; set; }
        public string RecommendedAction { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Enum tanımları;
    public enum EmotionType;
    {
        Joy,
        Sadness,
        Anger,
        Fear,
        Surprise,
        Disgust,
        Neutral,
        Confusion,
        Excitement,
        Contentment;
    }

    public enum CommunicationType;
    {
        Text,
        Voice,
        Video,
        Multimodal;
    }

    public enum ResponseStrategyType;
    {
        PositiveReinforcement,
        ComfortingSupport,
        Deescalation,
        Reassurance,
        NeutralSupport,
        Motivational,
        ProblemSolving,
        Validation;
    }

    public enum HealthStatus;
    {
        Healthy,
        Degraded,
        Unhealthy,
        Unknown;
    }

    public enum CulturalContext;
    {
        Neutral,
        Western,
        Eastern,
        MiddleEastern,
        LatinAmerican,
        African,
        Custom;
    }

    public enum CommunicationStyle;
    {
        Direct,
        Indirect,
        Formal,
        Informal,
        Emotional,
        Logical,
        Balanced;
    }

    // Özel istisna sınıfları;
    public class EmpathyAnalysisException : Exception
    {
        public EmpathyAnalysisException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class EmpathyResponseGenerationException : Exception
    {
        public EmpathyResponseGenerationException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class EmpathyTrainingException : Exception
    {
        public EmpathyTrainingException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class MultimodalEmpathyAnalysisException : Exception
    {
        public MultimodalEmpathyAnalysisException(string message, Exception innerException = null)
            : base(message, innerException) { }
    }

    public class InvalidConfigurationException : Exception
    {
        public InvalidConfigurationException(string message)
            : base(message) { }
    }
}
