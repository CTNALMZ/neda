using NEDA.Communication.EmotionalIntelligence.EmotionRecognition;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static NEDA.Interface.ResponseGenerator.EmotionalIntonation.ToneModulator;

namespace NEDA.Communication.EmotionalIntelligence.ToneAdjustment;
{
    /// <summary>
    /// Ses modülasyonu için profesyonel seviyede motor;
    /// Duygusal duruma göre ses tonu, perde, hız ve vurgu ayarları yapar;
    /// </summary>
    public interface IVoiceModulator;
    {
        /// <summary>
        /// Metni duygusal duruma göre modüle eder;
        /// </summary>
        /// <param name="text">Modüle edilecek metin</param>
        /// <param name="emotion">Hedef duygu durumu</param>
        /// <param name="intensity">Duygu yoğunluğu (0.0 - 1.0)</param>
        /// <returns>Modüle edilmiş ses parametreleri</returns>
        Task<VoiceModulationResult> ModulateAsync(string text, EmotionType emotion, double intensity);

        /// <summary>
        /// Ses parametrelerini kullanıcı tercihlerine göre ayarlar;
        /// </summary>
        /// <param name="parameters">Temel ses parametreleri</param>
        /// <param name="userPreferences">Kullanıcı tercihleri</param>
        /// <returns>Kişiselleştirilmiş ses parametreleri</returns>
        Task<VoiceParameters> ApplyPreferencesAsync(VoiceParameters parameters, UserVoicePreferences userPreferences);

        /// <summary>
        /// Gerçek zamanlı ses modülasyonu için akış başlatır;
        /// </summary>
        /// <param name="emotion">Başlangıç duygu durumu</param>
        /// <returns>Ses modülasyon akışı</returns>
        IRealTimeModulationStream StartRealTimeModulation(EmotionType emotion);

        /// <summary>
        /// Modülasyon profilini kaydeder;
        /// </summary>
        /// <param name="profile">Kaydedilecek profil</param>
        Task SaveModulationProfileAsync(ModulationProfile profile);

        /// <summary>
        /// Kayıtlı modülasyon profilini yükler;
        /// </summary>
        /// <param name="profileId">Profil ID</param>
        /// <returns>Modülasyon profili</returns>
        Task<ModulationProfile> LoadModulationProfileAsync(Guid profileId);
    }

    /// <summary>
    /// Ses modülasyon motoru - Endüstriyel implementasyon;
    /// </summary>
    public class VoiceModulator : IVoiceModulator, IDisposable;
    {
        private readonly IEmotionDetector _emotionDetector;
        private readonly IToneProfileManager _toneProfileManager;
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ModulationCache _modulationCache;
        private readonly RealTimeModulationEngine _realTimeEngine;
        private bool _disposed;
        private readonly object _syncLock = new object();

        /// <summary>
        /// VoiceModulator constructor - Dependency Injection;
        /// </summary>
        public VoiceModulator(
            IEmotionDetector emotionDetector,
            IToneProfileManager toneProfileManager,
            ILogger logger,
            IEventBus eventBus)
        {
            _emotionDetector = emotionDetector ??
                throw new ArgumentNullException(nameof(emotionDetector));
            _toneProfileManager = toneProfileManager ??
                throw new ArgumentNullException(nameof(toneProfileManager));
            _logger = logger ??
                throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ??
                throw new ArgumentNullException(nameof(eventBus));

            _modulationCache = new ModulationCache();
            _realTimeEngine = new RealTimeModulationEngine(logger);

            _logger.Info("VoiceModulator initialized successfully");
        }

        /// <summary>
        /// Metni duygusal duruma göre modüle eder;
        /// </summary>
        public async Task<VoiceModulationResult> ModulateAsync(string text, EmotionType emotion, double intensity)
        {
            try
            {
                ValidateInput(text, emotion, intensity);

                _logger.Debug($"Modulating text for emotion: {emotion}, intensity: {intensity}");

                // Önbellek kontrolü;
                var cacheKey = GenerateCacheKey(text, emotion, intensity);
                if (_modulationCache.TryGet(cacheKey, out var cachedResult))
                {
                    _logger.Debug("Returning cached modulation result");
                    return cachedResult;
                }

                // Duygu analizi yap;
                var emotionAnalysis = await _emotionDetector.AnalyzeEmotionAsync(text);

                // Ton profili al;
                var toneProfile = await _toneProfileManager.GetProfileForEmotionAsync(emotion);

                // Ses parametrelerini hesapla;
                var parameters = CalculateVoiceParameters(text, emotionAnalysis, toneProfile, intensity);

                // Prosodi (vurgu ve ritim) ekle;
                ApplyProsody(ref parameters, text, emotion);

                // Konuşma hızını ayarla;
                AdjustSpeechRate(ref parameters, emotion, intensity);

                // Perde varyasyonları ekle;
                AddPitchVariation(ref parameters, emotionAnalysis);

                var result = new VoiceModulationResult;
                {
                    Parameters = parameters,
                    OriginalText = text,
                    AppliedEmotion = emotion,
                    ModulationIntensity = intensity,
                    Timestamp = DateTime.UtcNow,
                    Success = true;
                };

                // Önbelleğe ekle;
                _modulationCache.Add(cacheKey, result);

                // Olay yayınla;
                await _eventBus.PublishAsync(new VoiceModulationCompletedEvent;
                {
                    Text = text,
                    Emotion = emotion,
                    Intensity = intensity,
                    Result = result,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Voice modulation completed successfully for text length: {text.Length}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Voice modulation failed: {ex.Message}", ex);
                throw new VoiceModulationException("Voice modulation failed", ex);
            }
        }

        /// <summary>
        /// Ses parametrelerini kullanıcı tercihlerine göre ayarlar;
        /// </summary>
        public async Task<VoiceParameters> ApplyPreferencesAsync(VoiceParameters parameters, UserVoicePreferences userPreferences)
        {
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));
            if (userPreferences == null)
                throw new ArgumentNullException(nameof(userPreferences));

            try
            {
                var adjustedParameters = parameters.Clone();

                // Kullanıcı tercihlerini uygula;
                if (userPreferences.PreferredPitch.HasValue)
                {
                    adjustedParameters.BasePitch = AdjustValueWithPreference(
                        adjustedParameters.BasePitch,
                        userPreferences.PreferredPitch.Value,
                        userPreferences.PreferenceStrength);
                }

                if (userPreferences.PreferredRate.HasValue)
                {
                    adjustedParameters.SpeechRate = AdjustValueWithPreference(
                        adjustedParameters.SpeechRate,
                        userPreferences.PreferredRate.Value,
                        userPreferences.PreferenceStrength);
                }

                if (userPreferences.PreferredVolume.HasValue)
                {
                    adjustedParameters.Volume = AdjustValueWithPreference(
                        adjustedParameters.Volume,
                        userPreferences.PreferredVolume.Value,
                        userPreferences.PreferenceStrength);
                }

                // Kültürel adaptasyon;
                if (!string.IsNullOrEmpty(userPreferences.Culture))
                {
                    adjustedParameters = await ApplyCulturalAdaptationAsync(adjustedParameters, userPreferences.Culture);
                }

                // Aksan adaptasyonu;
                if (!string.IsNullOrEmpty(userPreferences.AccentPreference))
                {
                    adjustedParameters = ApplyAccentAdaptation(adjustedParameters, userPreferences.AccentPreference);
                }

                _logger.Debug($"Applied user preferences to voice parameters");

                return adjustedParameters;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to apply user preferences: {ex.Message}", ex);
                throw new PreferenceApplicationException("Failed to apply user preferences", ex);
            }
        }

        /// <summary>
        /// Gerçek zamanlı ses modülasyonu için akış başlatır;
        /// </summary>
        public IRealTimeModulationStream StartRealTimeModulation(EmotionType emotion)
        {
            lock (_syncLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(VoiceModulator));

                var stream = _realTimeEngine.CreateStream(emotion);
                _logger.Info($"Real-time modulation stream started for emotion: {emotion}");

                return stream;
            }
        }

        /// <summary>
        /// Modülasyon profilini kaydeder;
        /// </summary>
        public async Task SaveModulationProfileAsync(ModulationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            try
            {
                // Profil doğrulama;
                ValidateModulationProfile(profile);

                // Profili kaydet;
                await _toneProfileManager.SaveProfileAsync(profile);

                // Önbelleği temizle (ilgili öğeler)
                _modulationCache.ClearByProfile(profile.Id);

                _logger.Info($"Modulation profile saved: {profile.Name} (ID: {profile.Id})");

                // Olay yayınla;
                await _eventBus.PublishAsync(new ModulationProfileSavedEvent;
                {
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save modulation profile: {ex.Message}", ex);
                throw new ProfileSaveException("Failed to save modulation profile", ex);
            }
        }

        /// <summary>
        /// Kayıtlı modülasyon profilini yükler;
        /// </summary>
        public async Task<ModulationProfile> LoadModulationProfileAsync(Guid profileId)
        {
            try
            {
                var profile = await _toneProfileManager.LoadProfileAsync(profileId);

                if (profile == null)
                {
                    _logger.Warn($"Modulation profile not found: {profileId}");
                    throw new ProfileNotFoundException($"Modulation profile not found: {profileId}");
                }

                _logger.Info($"Modulation profile loaded: {profile.Name}");

                return profile;
            }
            catch (ProfileNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load modulation profile: {ex.Message}", ex);
                throw new ProfileLoadException("Failed to load modulation profile", ex);
            }
        }

        #region Private Methods;

        private void ValidateInput(string text, EmotionType emotion, double intensity)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            if (!Enum.IsDefined(typeof(EmotionType), emotion))
                throw new ArgumentException($"Invalid emotion type: {emotion}", nameof(emotion));

            if (intensity < 0.0 || intensity > 1.0)
                throw new ArgumentOutOfRangeException(nameof(intensity),
                    "Intensity must be between 0.0 and 1.0");
        }

        private void ValidateModulationProfile(ModulationProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                throw new ArgumentException("Profile name cannot be empty");

            if (profile.Parameters == null)
                throw new ArgumentException("Profile parameters cannot be null");

            if (profile.Id == Guid.Empty)
                profile.Id = Guid.NewGuid();
        }

        private string GenerateCacheKey(string text, EmotionType emotion, double intensity)
        {
            return $"{text.GetHashCode()}_{emotion}_{intensity:F2}";
        }

        private VoiceParameters CalculateVoiceParameters(string text, EmotionAnalysis analysis,
            ToneProfile profile, double intensity)
        {
            var parameters = new VoiceParameters;
            {
                BasePitch = profile.BasePitch * (1.0 + (intensity * 0.3)),
                PitchRange = profile.PitchRange * intensity,
                SpeechRate = profile.SpeechRate * (1.0 + (intensity * 0.2)),
                Volume = profile.BaseVolume * (1.0 + (intensity * 0.15)),
                Timbre = profile.TimbreSettings,
                EmotionWeight = intensity,
                Resonance = CalculateResonance(analysis, intensity),
                Breathiness = CalculateBreathiness(analysis.Emotion, intensity),
                Tension = CalculateTension(analysis.EmotionIntensity, intensity)
            };

            // Metin uzunluğuna göre ek ayarlamalar;
            if (text.Length > 100)
            {
                parameters.SpeechRate *= 1.05; // Uzun metinlerde hızı biraz artır;
            }

            return parameters;
        }

        private void ApplyProsody(ref VoiceParameters parameters, string text, EmotionType emotion)
        {
            // Vurgu pattern'lerini ekle;
            switch (emotion)
            {
                case EmotionType.Happy:
                    parameters.StressPattern = StressPattern.Energetic;
                    parameters.Rhythm = RhythmPattern.Regular;
                    break;
                case EmotionType.Sad:
                    parameters.StressPattern = StressPattern.Flat;
                    parameters.Rhythm = RhythmPattern.Slow;
                    break;
                case EmotionType.Angry:
                    parameters.StressPattern = StressPattern.Strong;
                    parameters.Rhythm = RhythmPattern.Irregular;
                    break;
                case EmotionType.Excited:
                    parameters.StressPattern = StressPattern.Variable;
                    parameters.Rhythm = RhythmPattern.Fast;
                    break;
                default:
                    parameters.StressPattern = StressPattern.Neutral;
                    parameters.Rhythm = RhythmPattern.Regular;
                    break;
            }
        }

        private void AdjustSpeechRate(ref VoiceParameters parameters, EmotionType emotion, double intensity)
        {
            var emotionRateFactor = GetEmotionRateFactor(emotion);
            parameters.SpeechRate *= (1.0 + (emotionRateFactor * intensity));

            // Sınırları kontrol et;
            parameters.SpeechRate = Math.Clamp(parameters.SpeechRate, 0.5, 2.0);
        }

        private void AddPitchVariation(ref VoiceParameters parameters, EmotionAnalysis analysis)
        {
            if (analysis.PitchVariability > 0.5)
            {
                parameters.PitchRange *= 1.2;
                parameters.HasPitchVariation = true;
                parameters.PitchVariationPattern = PitchVariationPattern.Emotional;
            }
        }

        private double CalculateResonance(EmotionAnalysis analysis, double intensity)
        {
            var baseResonance = 0.5;
            var emotionFactor = analysis.EmotionIntensity * intensity;
            return Math.Clamp(baseResonance + emotionFactor, 0.1, 0.9);
        }

        private double CalculateBreathiness(EmotionType emotion, double intensity)
        {
            switch (emotion)
            {
                case EmotionType.Sad:
                    return 0.7 * intensity;
                case EmotionType.Relaxed:
                    return 0.6 * intensity;
                default:
                    return 0.3 * intensity;
            }
        }

        private double CalculateTension(double emotionIntensity, double intensity)
        {
            return emotionIntensity * intensity * 0.8;
        }

        private double GetEmotionRateFactor(EmotionType emotion)
        {
            return emotion switch;
            {
                EmotionType.Excited or EmotionType.Angry => 0.3,
                EmotionType.Happy => 0.2,
                EmotionType.Sad or EmotionType.Relaxed => -0.2,
                _ => 0.0;
            };
        }

        private double AdjustValueWithPreference(double currentValue, double preferredValue, double strength)
        {
            var difference = preferredValue - currentValue;
            return currentValue + (difference * Math.Clamp(strength, 0.0, 1.0));
        }

        private async Task<VoiceParameters> ApplyCulturalAdaptationAsync(VoiceParameters parameters, string culture)
        {
            // Kültürel adaptasyon mantığı;
            // Burada kültüre özgü ses parametreleri uygulanır;
            var adapted = parameters.Clone();

            // Örnek: Japonca için perde farklılıkları;
            if (culture.StartsWith("ja"))
            {
                adapted.PitchRange *= 1.1;
                adapted.SpeechRate *= 0.9;
            }
            // Örnek: İtalyanca için daha melodik;
            else if (culture.StartsWith("it"))
            {
                adapted.PitchRange *= 1.2;
                adapted.HasPitchVariation = true;
            }

            await Task.CompletedTask;
            return adapted;
        }

        private VoiceParameters ApplyAccentAdaptation(VoiceParameters parameters, string accent)
        {
            var adapted = parameters.Clone();

            // Aksana göre ayarlamalar;
            switch (accent.ToLowerInvariant())
            {
                case "british":
                    adapted.BasePitch *= 0.95;
                    adapted.Timbre.Quality = "Crisp";
                    break;
                case "american":
                    adapted.Resonance *= 1.1;
                    adapted.Timbre.Quality = "Full";
                    break;
                case "australian":
                    adapted.BasePitch *= 1.05;
                    adapted.PitchRange *= 0.9;
                    break;
            }

            return adapted;
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _realTimeEngine?.Dispose();
                    _modulationCache?.Clear();
                    _logger.Info("VoiceModulator disposed");
                }

                _disposed = true;
            }
        }

        ~VoiceModulator()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Ses modülasyon sonucu;
    /// </summary>
    public class VoiceModulationResult;
    {
        public VoiceParameters Parameters { get; set; }
        public string OriginalText { get; set; }
        public EmotionType AppliedEmotion { get; set; }
        public double ModulationIntensity { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Ses parametreleri;
    /// </summary>
    public class VoiceParameters : ICloneable;
    {
        public double BasePitch { get; set; } // Hz cinsinden temel perde;
        public double PitchRange { get; set; } // Perde varyasyon aralığı;
        public double SpeechRate { get; set; } // Konuşma hızı (normal=1.0)
        public double Volume { get; set; } // Ses şiddeti (0.0-1.0)
        public TimbreSettings Timbre { get; set; } = new TimbreSettings();
        public double EmotionWeight { get; set; }
        public double Resonance { get; set; } // Rezonans (0.0-1.0)
        public double Breathiness { get; set; } // Nefeslilik (0.0-1.0)
        public double Tension { get; set; } // Ses gerilimi (0.0-1.0)
        public StressPattern StressPattern { get; set; }
        public RhythmPattern Rhythm { get; set; }
        public bool HasPitchVariation { get; set; }
        public PitchVariationPattern PitchVariationPattern { get; set; }
        public Dictionary<string, double> AdditionalParameters { get; set; } = new Dictionary<string, double>();

        public VoiceParameters Clone()
        {
            return new VoiceParameters;
            {
                BasePitch = this.BasePitch,
                PitchRange = this.PitchRange,
                SpeechRate = this.SpeechRate,
                Volume = this.Volume,
                Timbre = this.Timbre?.Clone(),
                EmotionWeight = this.EmotionWeight,
                Resonance = this.Resonance,
                Breathiness = this.Breathiness,
                Tension = this.Tension,
                StressPattern = this.StressPattern,
                Rhythm = this.Rhythm,
                HasPitchVariation = this.HasPitchVariation,
                PitchVariationPattern = this.PitchVariationPattern,
                AdditionalParameters = new Dictionary<string, double>(this.AdditionalParameters)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Tını (timbre) ayarları;
    /// </summary>
    public class TimbreSettings : ICloneable;
    {
        public string Quality { get; set; } = "Neutral"; // Bright, Warm, Dark, Neutral;
        public double Brightness { get; set; } = 0.5;
        public double Warmth { get; set; } = 0.5;
        public double Richness { get; set; } = 0.5;
        public Dictionary<string, double> HarmonicContent { get; set; } = new Dictionary<string, double>();

        public TimbreSettings Clone()
        {
            return new TimbreSettings;
            {
                Quality = this.Quality,
                Brightness = this.Brightness,
                Warmth = this.Warmth,
                Richness = this.Richness,
                HarmonicContent = new Dictionary<string, double>(this.HarmonicContent)
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Modülasyon profili;
    /// </summary>
    public class ModulationProfile;
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Description { get; set; }
        public EmotionType TargetEmotion { get; set; }
        public VoiceParameters Parameters { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime Modified { get; set; } = DateTime.UtcNow;
        public string Author { get; set; }
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>();
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Kullanıcı ses tercihleri;
    /// </summary>
    public class UserVoicePreferences;
    {
        public string UserId { get; set; }
        public double? PreferredPitch { get; set; }
        public double? PreferredRate { get; set; }
        public double? PreferredVolume { get; set; }
        public string PreferredTimbre { get; set; }
        public double PreferenceStrength { get; set; } = 0.7;
        public string Culture { get; set; } = "en-US";
        public string AccentPreference { get; set; }
        public List<string> DislikedModulations { get; set; } = new List<string>();
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Gerçek zamanlı modülasyon akışı arayüzü;
    /// </summary>
    public interface IRealTimeModulationStream : IDisposable
    {
        Task<VoiceParameters> ProcessChunkAsync(AudioChunk chunk);
        void UpdateEmotion(EmotionType emotion, double intensity);
        Task CompleteAsync();
        event EventHandler<ModulationUpdateEventArgs> ModulationUpdated;
    }

    /// <summary>
    /// Duygu tipleri;
    /// </summary>
    public enum EmotionType;
    {
        Neutral = 0,
        Happy = 1,
        Sad = 2,
        Angry = 3,
        Excited = 4,
        Relaxed = 5,
        Surprised = 6,
        Fearful = 7,
        Disgusted = 8,
        Confident = 9,
        Empathetic = 10;
    }

    /// <summary>
    /// Vurgu pattern'leri;
    /// </summary>
    public enum StressPattern;
    {
        Neutral = 0,
        Strong = 1,
        Weak = 2,
        Variable = 3,
        Flat = 4,
        Energetic = 5;
    }

    /// <summary>
    /// Ritim pattern'leri;
    /// </summary>
    public enum RhythmPattern;
    {
        Regular = 0,
        Irregular = 1,
        Fast = 2,
        Slow = 3,
        Staccato = 4,
        Legato = 5;
    }

    /// <summary>
    /// Perde varyasyon pattern'leri;
    /// </summary>
    public enum PitchVariationPattern;
    {
        None = 0,
        Smooth = 1,
        Emotional = 2,
        Dramatic = 3,
        Subtle = 4;
    }

    #endregion;

    #region Event Classes;

    /// <summary>
    /// Ses modülasyonu tamamlandı olayı;
    /// </summary>
    public class VoiceModulationCompletedEvent : IEvent;
    {
        public string Text { get; set; }
        public EmotionType Emotion { get; set; }
        public double Intensity { get; set; }
        public VoiceModulationResult Result { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Modülasyon profili kaydedildi olayı;
    /// </summary>
    public class ModulationProfileSavedEvent : IEvent;
    {
        public Guid ProfileId { get; set; }
        public string ProfileName { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Modülasyon güncelleme olay argümanları;
    /// </summary>
    public class ModulationUpdateEventArgs : EventArgs;
    {
        public VoiceParameters Parameters { get; set; }
        public EmotionType CurrentEmotion { get; set; }
        public double Intensity { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exception Classes;

    /// <summary>
    /// Ses modülasyonu istisnası;
    /// </summary>
    public class VoiceModulationException : Exception
    {
        public VoiceModulationException(string message) : base(message) { }
        public VoiceModulationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Tercih uygulama istisnası;
    /// </summary>
    public class PreferenceApplicationException : Exception
    {
        public PreferenceApplicationException(string message) : base(message) { }
        public PreferenceApplicationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Profil kaydetme istisnası;
    /// </summary>
    public class ProfileSaveException : Exception
    {
        public ProfileSaveException(string message) : base(message) { }
        public ProfileSaveException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Profil yükleme istisnası;
    /// </summary>
    public class ProfileLoadException : Exception
    {
        public ProfileLoadException(string message) : base(message) { }
        public ProfileLoadException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Profil bulunamadı istisnası;
    /// </summary>
    public class ProfileNotFoundException : Exception
    {
        public ProfileNotFoundException(string message) : base(message) { }
        public ProfileNotFoundException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;

    #region Internal Helper Classes;

    internal class ModulationCache;
    {
        private readonly Dictionary<string, VoiceModulationResult> _cache = new Dictionary<string, VoiceModulationResult>();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(30);
        private readonly object _lock = new object();
        private DateTime _lastCleanup = DateTime.UtcNow;

        public bool TryGet(string key, out VoiceModulationResult result)
        {
            lock (_lock)
            {
                CleanupIfNeeded();

                if (_cache.TryGetValue(key, out result))
                {
                    if (DateTime.UtcNow - result.Timestamp < _cacheDuration)
                    {
                        return true;
                    }
                    _cache.Remove(key);
                }
                result = null;
                return false;
            }
        }

        public void Add(string key, VoiceModulationResult result)
        {
            lock (_lock)
            {
                CleanupIfNeeded();
                _cache[key] = result;
            }
        }

        public void ClearByProfile(Guid profileId)
        {
            lock (_lock)
            {
                var keysToRemove = _cache;
                    .Where(kvp => kvp.Value.Parameters?.AdditionalParameters?
                        .ContainsKey($"Profile_{profileId}") == true)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _cache.Remove(key);
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
            }
        }

        private void CleanupIfNeeded()
        {
            if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromMinutes(5))
            {
                var expiredKeys = _cache;
                    .Where(kvp => DateTime.UtcNow - kvp.Value.Timestamp > _cacheDuration)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _cache.Remove(key);
                }

                _lastCleanup = DateTime.UtcNow;
            }
        }
    }

    internal class RealTimeModulationEngine : IDisposable
    {
        private readonly ILogger _logger;
        private readonly List<RealTimeModulationStream> _activeStreams = new List<RealTimeModulationStream>();
        private bool _disposed;

        public RealTimeModulationEngine(ILogger logger)
        {
            _logger = logger;
        }

        public IRealTimeModulationStream CreateStream(EmotionType emotion)
        {
            var stream = new RealTimeModulationStream(emotion, _logger);
            lock (_activeStreams)
            {
                _activeStreams.Add(stream);
            }
            return stream;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                lock (_activeStreams)
                {
                    foreach (var stream in _activeStreams)
                    {
                        stream.Dispose();
                    }
                    _activeStreams.Clear();
                }
                _disposed = true;
            }
        }
    }

    internal class RealTimeModulationStream : IRealTimeModulationStream;
    {
        private EmotionType _currentEmotion;
        private double _currentIntensity = 0.7;
        private readonly ILogger _logger;
        private bool _disposed;
        private bool _completed;

        public event EventHandler<ModulationUpdateEventArgs> ModulationUpdated;

        public RealTimeModulationStream(EmotionType emotion, ILogger logger)
        {
            _currentEmotion = emotion;
            _logger = logger;
        }

        public async Task<VoiceParameters> ProcessChunkAsync(AudioChunk chunk)
        {
            if (_disposed || _completed)
                throw new InvalidOperationException("Stream is closed");

            // Gerçek zamanlı işleme mantığı;
            var parameters = CalculateRealTimeParameters(chunk);

            ModulationUpdated?.Invoke(this, new ModulationUpdateEventArgs;
            {
                Parameters = parameters,
                CurrentEmotion = _currentEmotion,
                Intensity = _currentIntensity,
                Timestamp = DateTime.UtcNow;
            });

            await Task.Delay(10); // Simülasyon;
            return parameters;
        }

        public void UpdateEmotion(EmotionType emotion, double intensity)
        {
            _currentEmotion = emotion;
            _currentIntensity = Math.Clamp(intensity, 0.0, 1.0);
            _logger.Debug($"Real-time stream emotion updated: {emotion}, intensity: {intensity}");
        }

        public async Task CompleteAsync()
        {
            _completed = true;
            _logger.Info("Real-time modulation stream completed");
            await Task.CompletedTask;
        }

        private VoiceParameters CalculateRealTimeParameters(AudioChunk chunk)
        {
            // Basit gerçek zamanlı hesaplama;
            return new VoiceParameters;
            {
                BasePitch = 220.0 * (1.0 + (_currentIntensity * 0.2)),
                SpeechRate = 1.0 * (1.0 + (_currentIntensity * 0.1)),
                Volume = 0.8,
                EmotionWeight = _currentIntensity;
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _completed = true;
                _disposed = true;
                _logger.Debug("Real-time modulation stream disposed");
            }
        }
    }

    #endregion;
}
